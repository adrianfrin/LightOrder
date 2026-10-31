using System.Text.Json;
using ControlCenter.Contracts;

namespace LightOrder.Service;

public sealed class AutomationEngine(IAutomationStore store, Func<DeviceCommand, Task<ApplyResult>> apply,
    Action<DeviceCommand> validateDevice, IAutomationProcessSource? processes = null, TimeZoneInfo? timeZone = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Queue<string> diagnostics = new();
    private AutomationDocument document = new(1, []);
    private AutomationJournal journal = new();
    private bool loaded;
    private bool blocked;
    private bool journalUnreadable;
    private bool processBaseline;
    private readonly Dictionary<string, string> details = new(StringComparer.Ordinal);

    public string[] Diagnostics { get { lock (diagnostics) return diagnostics.ToArray(); } }
    public AutomationRuleStatus[] States => document.Rules.Select(r => new AutomationRuleStatus(r.Id,
        !r.Enabled ? AutomationExecutionState.Disabled : blocked ? AutomationExecutionState.Paused : Entry(r.Id).State,
        details.GetValueOrDefault(r.Id))).ToArray();

    // The host serializes RPC reads/writes with ticks, using its runtime gate.
    public AutomationReadResult Read()
    {
        Load();
        return new(1, Clone(document).Rules, States);
    }

    public ApplyResult Write(JsonElement value)
    {
        var next = AutomationValidation.Parse(JsonSerializer.SerializeToUtf8Bytes(value, AutomationValidation.Json));
        foreach (var rule in next.Rules)
            foreach (var command in rule.Commands.Concat(rule.ExitCommands ?? [])) validateDevice(command);
        Load();
        if (journalUnreadable) throw new InvalidDataException("The execution journal is unreadable. Restore a trusted journal before saving automation.");
        // Fail closed across a crash between the two files: first pause all old and new identities.
        foreach (var rule in next.Rules) _ = Entry(rule.Id);
        foreach (var entry in journal.Rules.Values) entry.State = AutomationExecutionState.Paused;
        try
        {
            PersistJournal();
            store.SaveDocument(JsonSerializer.SerializeToUtf8Bytes(next, AutomationValidation.Json));
            foreach (var entry in journal.Rules.Values)
            {
                entry.State = AutomationExecutionState.Ready;
                entry.Seen.Clear();
                entry.Active.Clear();
            }
            PersistJournal();
            document = next;
            details.Clear();
            blocked = false;
            processBaseline = false;
            Log("Automation saved; paused rules reset. Existing processes will be baselined.");
            return new("saved", "Automation saved. Commands execute only on a future matching tick or new process instance.");
        }
        catch
        {
            blocked = true;
            Log("Automation save failed; execution paused. Explicit save is required.");
            throw;
        }
    }

    public async Task TickAsync(DateTimeOffset now, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            Load();
            if (blocked) return;
            var local = TimeZoneInfo.ConvertTime(now, timeZone ?? TimeZoneInfo.Local);
            var date = DateOnly.FromDateTime(local.DateTime).DayNumber;
            IReadOnlyList<AutomationProcessSnapshot>? snapshot = null;
            if (document.Rules.Any(r => r.Enabled && r.Trigger.Kind == AutomationTriggerKind.Process && Entry(r.Id).State != AutomationExecutionState.Paused))
            {
                try
                {
                    var names = document.Rules.Where(r => r.Enabled && r.Trigger.Kind == AutomationTriggerKind.Process &&
                        Entry(r.Id).State != AutomationExecutionState.Paused).Select(r => r.Trigger.ProcessName!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    snapshot = processes is ITargetedAutomationProcessSource targeted ? targeted.Snapshot(names) :
                        processes?.Snapshot() ?? throw new InvalidOperationException("No owner-scoped process source configured.");
                    if (snapshot.Count > 4096 || snapshot.Any(p => p.Pid <= 0 || p.SessionId <= 0 || p.StartTimeUtc.Offset != TimeSpan.Zero ||
                        p.StartTimeUtc > now || !AutomationValidation.ValidProcessName(p.ProcessName)) ||
                        snapshot.Select(Identity).Distinct().Count() != snapshot.Count)
                        throw new InvalidDataException("Invalid or oversized process snapshot.");
                }
                catch (Exception e)
                {
                    foreach (var rule in document.Rules.Where(r => r.Enabled && r.Trigger.Kind == AutomationTriggerKind.Process)) Pause(rule, e);
                    // Process snapshot failure must not suppress independent daily rules.
                }
            }
            foreach (var rule in document.Rules)
            {
                token.ThrowIfCancellationRequested();
                if (!rule.Enabled || Entry(rule.Id).State == AutomationExecutionState.Paused) continue;
                try
                {
                    if (rule.Trigger.Kind == AutomationTriggerKind.Daily)
                    {
                        // Exact current minute only. The retained date high-water suppresses clock rollback and repeated DST hours.
                        if (date <= Entry(rule.Id).DailyHighWater || rule.Trigger.Time != local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) ||
                            !rule.Trigger.Days!.Contains((int)local.DayOfWeek)) continue;
                        Entry(rule.Id).DailyHighWater = date;
                        await Execute(rule, rule.Commands, null);
                    }
                    else if (snapshot is not null) await ProcessRule(rule, snapshot);
                }
                catch (Exception e) { Pause(rule, e); }
                if (blocked) break;
            }
            if (snapshot is not null) processBaseline = true;
        }
        finally { gate.Release(); }
    }

    private async Task ProcessRule(AutomationRule rule, IReadOnlyList<AutomationProcessSnapshot> snapshot)
    {
        var entry = Entry(rule.Id);
        var matching = snapshot.Where(p => string.Equals(p.ProcessName, rule.Trigger.ProcessName, StringComparison.OrdinalIgnoreCase)).Select(Identity).ToHashSet();
        if (matching.Count > 256) throw new InvalidDataException("Too many matching process instances.");
        if (!processBaseline)
        {
            // Baseline preexisting instances on startup and explicit save, without replay or exit catch-up.
            entry.Seen = matching.ToList();
            entry.Active.Clear();
            PersistJournal();
            return;
        }
        var exited = entry.Active.Where(p => !matching.Contains(p)).ToArray();
        foreach (var identity in exited) entry.Active.Remove(identity);
        if (exited.Length > 0 && rule.ExitCommands is { Length: > 0 })
        {
            // Exit commands are not instance-scoped for other devices. Run once only when the group is empty.
            if (matching.Count == 0) await Execute(rule, rule.ExitCommands, null);
            else Log($"Rule {rule.Id}: exit commands suppressed while another matching instance is alive.");
        }
        var newcomers = matching.Where(p => !entry.Seen.Contains(p)).OrderBy(p => p.StartTimeUtc).ThenBy(p => p.Pid).ToArray();
        entry.Seen = matching.ToList();
        // Bound burst work; pause instead of building an unbounded backlog or catching up later.
        if (newcomers.Length > 16) throw new InvalidDataException("More than 16 new process instances in one tick; rule paused.");
        foreach (var identity in newcomers)
        {
            entry.Active.Add(identity);
            await Execute(rule, rule.Commands, identity);
        }
        if (exited.Length > 0 || newcomers.Length > 0) PersistJournal();
    }

    private async Task Execute(AutomationRule rule, DeviceCommand[] commands, AutomationProcessIdentity? identity)
    {
        var entry = Entry(rule.Id);
        entry.State = AutomationExecutionState.Running;
        PersistJournal(); // No device side effect before the durable reservation.
        var phaseUtc = DateTimeOffset.UtcNow;
        foreach (var template in commands)
        {
            var command = template;
            if (command.Capability is "lighting.static" or "lighting.off" or "lighting.effect")
            {
                var parameters = command.Parameters.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                parameters["operationId"] = Wire.Element(Guid.NewGuid().ToString("N"));
                if (command.Capability == "lighting.effect") parameters["phaseUtc"] = Wire.Element(phaseUtc);
                command = command with { Parameters = Wire.Element(parameters) };
            }
            if (command.Capability == "process.policy")
            {
                if (identity is null) throw new InvalidDataException("Process identity is missing.");
                var parameters = command.Parameters.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                parameters["pid"] = Wire.Element(identity.Pid);
                parameters["startTimeUtc"] = Wire.Element(identity.StartTimeUtc.UtcDateTime.ToString("O"));
                command = command with { Parameters = Wire.Element(parameters) };
            }
            validateDevice(command);
            var result = await apply(command);
            if (result is null || result.Status is not ("applied" or "verified" or "unchanged" or "accepted" or "released" or "restored" or "simulated" or "ok" or "started" or "stopped"))
                throw new InvalidOperationException("Device command did not report successful completion.");
        }
        entry.State = AutomationExecutionState.Completed;
        PersistJournal();
        Log($"Rule {rule.Id}: completed.");
    }

    private void Load()
    {
        if (loaded) return;
        loaded = true;
        journalUnreadable = true;
        try
        {
            var journalBytes = store.LoadJournal();
            if (journalBytes is not null)
            {
                if (journalBytes.Length > AutomationJournal.MaxBytes) throw new InvalidDataException("Journal too large.");
                using var parsed = JsonDocument.Parse(journalBytes, new JsonDocumentOptions { MaxDepth = 32 });
                AutomationValidation.CheckJson(parsed.RootElement);
                if (parsed.RootElement.ValueKind != JsonValueKind.Object ||
                    !parsed.RootElement.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var version) || version != 1 ||
                    !parsed.RootElement.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Journal schemaVersion and rules are required.");
                foreach (var property in rules.EnumerateObject())
                    if (property.Value.ValueKind != JsonValueKind.Object || !property.Value.TryGetProperty("dailyHighWater", out _) ||
                        !property.Value.TryGetProperty("state", out _) || !property.Value.TryGetProperty("seen", out _) || !property.Value.TryGetProperty("active", out _))
                        throw new InvalidDataException("Journal rule fields are required.");
                journal = parsed.RootElement.Deserialize<AutomationJournal>(AutomationValidation.Json) ?? throw new InvalidDataException("Missing journal.");
                if (journal.SchemaVersion != 1 || journal.Rules is null || journal.Rules.Count > 1024 || journal.Rules.Any(p =>
                    p.Key.Length is < 1 or > 128 || p.Value is null || p.Value.DailyHighWater is < 0 or > 3652058 ||
                    !Enum.IsDefined(p.Value.State) || p.Value.Seen is null || p.Value.Active is null || p.Value.Seen.Count > 256 ||
                    p.Value.Active.Count > 256 || p.Value.Seen.Concat(p.Value.Active).Any(i => i is null || i.Pid <= 0 || i.StartTimeUtc.Offset != TimeSpan.Zero)))
                    throw new InvalidDataException("Invalid journal.");
            }
            journalUnreadable = false;
            var bytes = store.LoadDocument();
            if (bytes is not null) document = AutomationValidation.Parse(bytes);
            if (document.Rules.Length > 0 && (journalBytes is null || document.Rules.Any(r => !journal.Rules.ContainsKey(r.Id))))
            {
                blocked = true;
                Log("Journal missing for saved rules; explicit save required before execution.");
            }
            foreach (var entry in journal.Rules.Values)
                if (entry.State == AutomationExecutionState.Running) entry.State = AutomationExecutionState.Paused;
            foreach (var rule in document.Rules) _ = Entry(rule.Id);
        }
        catch
        {
            blocked = true;
            Log(journalUnreadable ? "Execution journal could not be read safely; restore a trusted journal before saving automation." :
                "Automation document could not be read safely; explicit save is required.");
            // Never silently replace a corrupt journal: keep the error visible to the caller.
            throw;
        }
    }

    private AutomationJournalRule Entry(string id)
    {
        if (journal.Rules.TryGetValue(id, out var entry)) return entry;
        if (journal.Rules.Count >= 1024) throw new InvalidDataException("Automation journal rule identity budget exhausted.");
        return journal.Rules[id] = new();
    }
    private void PersistJournal()
    {
        try { store.SaveJournal(JsonSerializer.SerializeToUtf8Bytes(journal, AutomationValidation.Json)); }
        catch { blocked = true; throw; }
    }
    private void Pause(AutomationRule rule, Exception error)
    {
        Entry(rule.Id).State = AutomationExecutionState.Paused;
        details[rule.Id] = "Paused after " + (error is PluginException pe ? pe.Code : error.GetType().Name) + ". Explicit save required; no retry.";
        Log($"Rule {rule.Id}: {details[rule.Id]}");
        try { PersistJournal(); } catch { Log("Journal save failed; all automation paused."); }
    }
    private void Log(string message)
    {
        lock (diagnostics)
        {
            diagnostics.Enqueue(message[..Math.Min(message.Length, 512)]);
            while (diagnostics.Count > 64) diagnostics.Dequeue();
        }
    }
    private static AutomationProcessIdentity Identity(AutomationProcessSnapshot snapshot) => new(snapshot.Pid, snapshot.StartTimeUtc);
    private static AutomationDocument Clone(AutomationDocument value) => AutomationValidation.Parse(JsonSerializer.SerializeToUtf8Bytes(value, AutomationValidation.Json));
}
