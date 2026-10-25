using System.Text.Json;
using ControlCenter.Contracts;

namespace ControlCenter.WindowsPolicy;

public sealed class WindowsPolicyPlugin : IDevicePlugin
{
    public const string DeviceId = "windows-policy";
    public PluginHello Hello { get; } = new("local.windows-policy", "0.1.0");
    private readonly IPolicySystem system;
    private readonly RecoveryJournal journal;
    private readonly object gate = new();
    private bool disposed;
    private readonly Func<StandardDevice> readSystemInformation;
    private StandardDevice? systemInformation;

    public WindowsPolicyPlugin() : this(new NativePolicy(), new RecoveryJournal()) { }
    internal WindowsPolicyPlugin(IPolicySystem system, RecoveryJournal journal, Func<StandardDevice>? readSystemInformation = null)
    {
        this.system = system;
        this.journal = journal;
        this.readSystemInformation = readSystemInformation ?? SystemInformation.Read;
    }

    public Task<object> HandleAsync(string method, JsonElement args, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            object result = method switch
            {
                "device.discover" => Discover(),
                "device.execute" => Execute(args),
                "telemetry.read" => Array.Empty<object>(),
                "power.list" => ListPower(),
                "power.active" => ActivePower(),
                "power.apply" => ApplyPower(args),
                "process.list" => ListProcesses(),
                "process.apply" => ApplyProcess(args),
                StandardMethods.Release => StandardRelease(args),
                "policy.recovery.status" => RecoveryStatus(),
                "policy.recover" => Release(args, recovery: true),
                _ => throw new PluginException("MethodNotFound", $"Unknown WindowsPolicy method: {method}")
            };
            return Task.FromResult(result);
        }
    }

    private StandardDevice[] Discover()
    {
        object? power = null, processes = null;
        var diagnostics = new List<string>();
        try { power = ListPower(); } catch (Exception ex) { diagnostics.Add("Power metadata unavailable: " + ErrorCode(ex)); }
        try { processes = ListProcesses(256); } catch (Exception ex) { diagnostics.Add("Process metadata unavailable: " + ErrorCode(ex)); }
        return [new StandardDevice(DeviceId, "Windows power and process policy", "windows-policy",
            [StandardCapabilities.WindowsPowerScheme, StandardCapabilities.ProcessPolicy, "device.release", "power.list", "power.active", "power.apply", "process.list", "process.apply", "policy.recovery.status", "policy.recover"],
            journal.PendingFromPreviousRun ? "recovery-required" : diagnostics.Count == 0 ? "ready" : "partial",
            "Power scheme, process priority, and single-processor-group affinity. Restore changes only while values still match this plugin's writes.",
            Wire.Element(new { schemaVersion = 1, supportsRestore = true, power, process = processes, diagnostics,
                recoveryPending = journal.PendingFromPreviousRun,
                parameterSchemas = new Dictionary<string, object> {
                    [StandardCapabilities.WindowsPowerScheme] = new { required = new[] { "schemeId" }, schemeId = "installed scheme GUID" },
                    [StandardCapabilities.ProcessPolicy] = new { required = new[] { "pid", "startTimeUtc", "priority" }, optional = new[] { "affinityMask" }, priorities = PolicyValues.Priorities }
                } })), systemInformation ??= readSystemInformation()];
    }

    private ApplyResult Execute(JsonElement args)
    {
        var command = Wire.Read<DeviceCommand>(args);
        if (command.DeviceId == SystemInformation.DeviceId) throw new PluginException("ReadOnly", "System information is read-only.");
        if (command.DeviceId != DeviceId) throw new PluginException("DeviceNotFound", "Unknown Windows policy device.");
        object result = command.Capability switch
        {
            StandardCapabilities.WindowsPowerScheme => ApplyPower(command.Parameters),
            StandardCapabilities.ProcessPolicy => ApplyProcess(command.Parameters),
            "device.release" => Release(Wire.Element(new { deviceId = DeviceId }), recovery: false),
            _ => throw new PluginException("UnsupportedCapability", "Unknown Windows policy capability.")
        };
        var data = Wire.Element(result);
        return new ApplyResult(data.GetProperty("status").GetString()!, "Windows policy operation completed; inspect readback and restoration outcomes.", data);
    }

    private object ActivePower()
    {
        Guid active = system.ReadActiveScheme();
        string? name = system.ListSchemes().FirstOrDefault(s => s.Id == active)?.Name;
        return new { id = active.ToString("D"), name, readbackVerified = true };
    }

    private object ListPower()
    {
        Guid active = system.ReadActiveScheme();
        return new
        {
            schemes = system.ListSchemes().Select(s => new { id = s.Id.ToString("D"), name = s.Name, isActive = s.Id == active }).ToArray(),
            activeSchemeId = active.ToString("D"),
            readbackVerified = true
        };
    }

    private object ListProcesses(int limit = int.MaxValue)
    {
        var rows = new List<object>();
        var ids = system.ListProcessIds();
        foreach (int pid in ids.Take(limit))
        {
            string? name = null;
            string? start = null;
            string? priority = null;
            string? affinity = null;
            string? available = null;
            try
            {
                using var target = system.OpenProcess(pid, writable: false);
                start = target.StartTimeUtc;
                name = system.ProcessName(pid);
                priority = target.ReadPriority();
                affinity = target.ReadAffinity();
                available = target.SystemAffinity;
                rows.Add(new { pid, startTimeUtc = start, name, priority, affinityMask = affinity, systemAffinityMask = available,
                    status = "ok", errorCode = (string?)null, detail = (string?)null, readbackVerified = true });
            }
            catch (Exception ex) when (ex is PluginException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
            {
                rows.Add(new { pid, startTimeUtc = start, name, priority, affinityMask = affinity, systemAffinityMask = available,
                    status = "unavailable", errorCode = ErrorCode(ex), detail = ex.Message, readbackVerified = false });
            }
        }
        return new { processes = rows, truncated = ids.Length > limit, totalProcessCount = ids.Length, processorGroupCount = system.ProcessorGroupCount,
            affinityWriteSupported = system.ProcessorGroupCount == 1, allowedPriorities = PolicyValues.Priorities };
    }

    private object ApplyPower(JsonElement args)
    {
        RequireWritable();
        string text = RequiredString(args, "schemeId");
        if (!Guid.TryParse(text, out Guid scheme)) throw new PluginException("InvalidArgument", "schemeId must be a GUID.");
        if (!system.ListSchemes().Any(s => s.Id == scheme)) throw new PluginException("InvalidArgument", "The requested power scheme is not installed.");
        string before = system.ReadActiveScheme().ToString("D");
        string desired = scheme.ToString("D");
        var changes = new List<Change>();
        if (before != desired)
            changes.Add(new(new("power", null, null, before, desired), before,
                () => system.ReadActiveScheme().ToString("D"), v => system.SetActiveScheme(Guid.Parse(v))));
        RunTransaction(changes, () =>
        {
            if (system.ReadActiveScheme() != scheme) throw new PluginException("ReadbackMismatch", "Active power scheme does not match the requested value.");
        });
        return new { status = changes.Count == 0 ? "unchanged" : "applied", schemeId = desired, readbackVerified = true };
    }

    private object ApplyProcess(JsonElement args)
    {
        RequireWritable();
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("pid", out var pidValue) || pidValue.ValueKind != JsonValueKind.Number || !pidValue.TryGetInt32(out int pid) || pid <= 0)
            throw new PluginException("InvalidArgument", "pid must be a positive integer.");
        string start = PolicyValues.NormalizeStartTime(RequiredString(args, "startTimeUtc"));
        string priority = PolicyValues.ValidatePriority(RequiredString(args, "priority"));
        string? requestedMask = null;
        if (args.TryGetProperty("affinityMask", out var maskValue))
        {
            if (maskValue.ValueKind != JsonValueKind.String) throw new PluginException("InvalidArgument", "affinityMask must be a hexadecimal string when supplied.");
            requestedMask = maskValue.GetString()!;
        }
        using var target = system.OpenProcess(pid, writable: true);
        if (target.StartTimeUtc != start) throw new PluginException("ProcessIdentityMismatch", "PID creation time differs; no changes were made.");
        string beforePriority = target.ReadPriority();
        // Do not create an ownership record whose restoration would require a forbidden class.
        if (beforePriority == "RealTime") throw new PluginException("Unsupported", "Changing a RealTime process is disabled because safe restoration would require setting RealTime.");
        string beforeAffinity = target.ReadAffinity();
        string available = target.SystemAffinity;
        string? affinity = requestedMask is null ? null : PolicyValues.ValidateMask(requestedMask, available, system.ProcessorGroupCount);
        var changes = new List<Change>();
        if (priority != beforePriority)
            changes.Add(new(new("priority", pid, start, beforePriority, priority), beforePriority, target.ReadPriority, target.SetPriority));
        if (affinity is not null && affinity != beforeAffinity)
            changes.Add(new(new("affinity", pid, start, beforeAffinity, affinity), beforeAffinity, target.ReadAffinity, target.SetAffinity));
        string observedPriority = "", observedAffinity = "";
        RunTransaction(changes, () =>
        {
            observedPriority = target.ReadPriority();
            observedAffinity = target.ReadAffinity();
            if (observedPriority != priority || (affinity is not null && observedAffinity != affinity))
                throw new PluginException("ReadbackMismatch", "Process policy readback does not match the requested values.");
        });
        return new { status = changes.Count == 0 ? "unchanged" : "applied", pid, startTimeUtc = start,
            priority = observedPriority, affinityMask = observedAffinity, systemAffinityMask = available, readbackVerified = true };
    }

    private sealed record Change(Ownership Entry, string Before, Func<string> Read, Action<string> Write);

    private void RunTransaction(List<Change> changes, Action verify)
    {
        var prior = journal.Entries.ToList();
        var attempted = new List<Change>();
        foreach (var change in changes)
        {
            var existing = journal.Entries.FirstOrDefault(e => e.Key == change.Entry.Key);
            var staged = change.Entry;
            // Preserve the first baseline only while the existing property is still ours.
            if (existing is not null && existing.Applied == change.Before) staged = staged with { Original = existing.Original };
            journal.Entries.RemoveAll(e => e.Key == staged.Key);
            journal.Entries.Add(staged);
        }
        try { if (changes.Count > 0) journal.Save(); }
        catch { journal.Entries = prior; throw; }
        try
        {
            foreach (var change in changes)
            {
                if (change.Read() != change.Before)
                    throw new PluginException("ConcurrentChange", $"{change.Entry.Property} changed before application.");
                attempted.Add(change); // Also covers a setter that changed state before reporting failure.
                change.Write(change.Entry.Applied);
                if (change.Read() != change.Entry.Applied)
                    throw new PluginException("ReadbackMismatch", $"{change.Entry.Property} failed immediate readback.");
            }
            verify();
        }
        catch (Exception original)
        {
            var staged = journal.Entries.ToList();
            journal.Entries = prior;
            var outcomes = new List<string>();
            foreach (var change in attempted.AsEnumerable().Reverse())
            {
                try
                {
                    string current = change.Read();
                    if (current == change.Entry.Applied)
                    {
                        change.Write(change.Before);
                        if (change.Read() != change.Before) throw new PluginException("ReadbackMismatch", "Rollback readback mismatch.");
                        outcomes.Add($"{change.Entry.Property}:rolledBack");
                    }
                    else if (current == change.Before) outcomes.Add($"{change.Entry.Property}:unchanged");
                    else
                    {
                        journal.Entries.RemoveAll(e => e.Key == change.Entry.Key);
                        outcomes.Add($"{change.Entry.Property}:externalChangeSkipped");
                    }
                }
                catch (Exception rollback)
                {
                    journal.Entries.RemoveAll(e => e.Key == change.Entry.Key);
                    journal.Entries.Add(staged.Single(e => e.Key == change.Entry.Key));
                    outcomes.Add($"{change.Entry.Property}:rollbackFailed ({rollback.Message})");
                }
            }
            try { if (changes.Count > 0) journal.Save(); }
            catch (Exception persistence) { outcomes.Add($"journal:{persistence.Message}"); }
            throw new PluginException(ErrorCode(original), $"{original.Message} Rollback: {string.Join("; ", outcomes.DefaultIfEmpty("no writes attempted"))}");
        }
    }

    private ApplyResult StandardRelease(JsonElement args)
    {
        var raw = Wire.Element(Release(args, recovery: false));
        return new ApplyResult(raw.GetProperty("status").GetString()!, "Inspect per-property restoration outcomes.", raw);
    }

    private object RecoveryStatus() => new
    {
        recoveryPending = journal.PendingFromPreviousRun,
        journalPath = journal.Path,
        entries = journal.Entries.Select(e => new { property = e.Property, pid = e.Pid, startTimeUtc = e.StartTimeUtc, original = e.Original, applied = e.Applied }).ToArray()
    };

    private object Release(JsonElement args, bool recovery)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("deviceId", out var systemId) &&
            systemId.ValueKind == JsonValueKind.String && systemId.GetString() == SystemInformation.DeviceId)
            return new ApplyResult("released", "Read-only system information has no changes to restore.");
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("deviceId", out var id) &&
            (id.ValueKind != JsonValueKind.String || id.GetString() != DeviceId))
            throw new PluginException("InvalidArgument", $"deviceId must be {DeviceId}.");
        if (!recovery && journal.PendingFromPreviousRun)
            throw new PluginException("RecoveryRequired", "A previous run left a journal. Inspect policy.recovery.status, then call policy.recover explicitly.");
        return RestoreEntries();
    }

    private object RestoreEntries()
    {
        var outcomes = new List<object>();
        bool allVerified = journal.Entries.Count > 0;
        bool hadFailure = false;
        foreach (var entry in journal.Entries.ToArray().Reverse())
        {
            IProcessTarget? target = null;
            string status;
            string? observed = null;
            string? code = null;
            string? detail = null;
            bool verified = false;
            bool forget = false;
            try
            {
                Func<string> read;
                Action<string> write;
                if (entry.Property == "power")
                {
                    read = () => system.ReadActiveScheme().ToString("D");
                    write = value => system.SetActiveScheme(Guid.Parse(value));
                }
                else
                {
                    // If PID vanished, do not interpret another process or an access error as ours.
                    if (!system.ListProcessIds().Contains(entry.Pid!.Value))
                    {
                        status = "processExited";
                        forget = true;
                        goto Record;
                    }
                    target = system.OpenProcess(entry.Pid.Value, writable: false);
                    if (target.StartTimeUtc != entry.StartTimeUtc)
                    {
                        status = "pidReused";
                        forget = true;
                        goto Record;
                    }
                    // First inspect with query rights; only ask for write rights when still owned.
                    observed = entry.Property == "priority" ? target.ReadPriority() : target.ReadAffinity();
                    if (observed != entry.Applied)
                    {
                        status = observed == entry.Original ? "alreadyRestored" : "externalChangeSkipped";
                        verified = observed == entry.Original;
                        forget = true;
                        goto Record;
                    }
                    target.Dispose();
                    target = system.OpenProcess(entry.Pid.Value, writable: true);
                    if (target.StartTimeUtc != entry.StartTimeUtc)
                    {
                        status = "pidReused";
                        forget = true;
                        goto Record;
                    }
                    read = entry.Property == "priority" ? target.ReadPriority : target.ReadAffinity;
                    write = entry.Property == "priority" ? target.SetPriority : target.SetAffinity;
                    if (entry.Property == "affinity")
                        PolicyValues.ValidateMask(entry.Original, target.SystemAffinity, system.ProcessorGroupCount);
                }
                observed = read();
                if (observed == entry.Original)
                {
                    status = "alreadyRestored";
                    verified = true;
                    forget = true;
                }
                else if (observed != entry.Applied)
                {
                    status = "externalChangeSkipped";
                    forget = true;
                }
                else
                {
                    write(entry.Original);
                    observed = read();
                    if (observed != entry.Original) throw new PluginException("ReadbackMismatch", "Restoration readback differs from the original value.");
                    status = "restored";
                    verified = true;
                    forget = true;
                }
            }
            catch (PluginException ex) when (ex.Code == "ProcessUnavailable" && entry.Pid is not null)
            {
                status = "processExited";
                code = ex.Code;
                detail = ex.Message;
                forget = true;
            }
            catch (Exception ex)
            {
                status = "failed";
                code = ErrorCode(ex);
                detail = ex.Message;
                hadFailure = true;
            }
            finally { target?.Dispose(); }
        Record:
            allVerified &= verified;
            if (forget) journal.Entries.RemoveAll(e => e.Key == entry.Key);
            outcomes.Add(new { property = entry.Property, pid = entry.Pid, startTimeUtc = entry.StartTimeUtc,
                status, original = entry.Original, applied = entry.Applied, observed, readbackVerified = verified, errorCode = code, detail });
        }
        journal.Save();
        journal.PendingFromPreviousRun = journal.PendingFromPreviousRun && journal.Entries.Count > 0;
        return new { status = hadFailure ? "partial" : "released", outcomes, remainingChanges = journal.Entries.Count,
            recoveryPending = journal.PendingFromPreviousRun, readbackVerified = allVerified };
    }

    private void RequireWritable()
    {
        if (journal.PendingFromPreviousRun)
            throw new PluginException("RecoveryRequired", "A previous run left a journal. Call policy.recovery.status and policy.recover before applying changes.");
    }
    private static string RequiredString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new PluginException("InvalidArgument", $"{name} is required and must be a nonempty string.");
        return value.GetString()!;
    }
    private static string ErrorCode(Exception ex) => ex is PluginException pe ? pe.Code : "DeviceError";

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return ValueTask.CompletedTask;
            disposed = true;
            try
            {
                // A stale journal is never recovered merely by starting/stopping the plugin.
                if (!journal.PendingFromPreviousRun && journal.Entries.Count > 0)
                {
                    object result = RestoreEntries();
                    if (journal.Entries.Count > 0) Console.Error.WriteLine(JsonSerializer.Serialize(result, Wire.Json));
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"WindowsPolicy restoration requires explicit recovery: {ex.Message}"); }
            finally { journal.Dispose(); }
        }
        return ValueTask.CompletedTask;
    }
}
