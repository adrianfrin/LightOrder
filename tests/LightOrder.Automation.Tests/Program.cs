using System.Text;
using System.Text.Json;
using ControlCenter.Contracts;
using LightOrder.Service;

var tests = new (string Name, Func<Task> Run)[]
{
    ("strict schema and budgets", Schema),
    ("daily minute date rollback and restart", Daily),
    ("DST overlap and gap", Dst),
    ("uncertain apply pauses durably", Failures),
    ("process matching identity reuse and exit ownership", Processes),
    ("process lighting executions refresh identity and shared phase", ProcessLighting),
    ("crash reservation and interrupted save", Crash),
    ("store failure and corrupt state", Storage),
    ("concurrent tick serialization and completion failure", Concurrency),
    ("target executable filtering before ownership inspection", TargetedSource),
    ("owner-scoped source and bounded diagnostics", SourceAndLogs)
};
foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine("PASS " + test.Name);
}
Console.WriteLine($"PASS {tests.Length} groups; {Check.Count} assertions; no hardware access");

static DeviceCommand Command(string capability = "lighting.static", object? parameters = null) => new("fake::lamp", capability, Wire.Element(parameters ?? new { color = "#112233" }));
static AutomationRule Rule(AutomationTrigger? trigger = null, DeviceCommand[]? commands = null, DeviceCommand[]? exits = null, string id = "one") =>
    new(id, "Test rule", true, trigger ?? new(AutomationTriggerKind.Daily, "10:30", [0, 1, 2, 3, 4, 5, 6]), commands ?? [Command()], exits);
static JsonElement Doc(params AutomationRule[] rules) => JsonSerializer.SerializeToElement(new AutomationDocument(1, rules), AutomationValidation.Json);
static AutomationEngine Engine(MemoryStore store, List<DeviceCommand> calls, IAutomationProcessSource? source = null, TimeZoneInfo? zone = null,
    Func<DeviceCommand, Task<ApplyResult>>? execute = null, Action<DeviceCommand>? validate = null) =>
    new(store, execute ?? (c => { calls.Add(c); return Task.FromResult(new ApplyResult("applied", "test")); }), validate ?? (_ => { }), source, zone ?? TimeZoneInfo.Utc);
static DateTimeOffset At(string time) => DateTimeOffset.Parse(time, System.Globalization.CultureInfo.InvariantCulture);

static Task Schema()
{
    var rule = Rule();
    var bytes = JsonSerializer.SerializeToUtf8Bytes(new AutomationDocument(1, [rule]), AutomationValidation.Json);
    Check.True(Encoding.UTF8.GetString(bytes).Contains("\"kind\":\"daily\""), "kind lower-case");
    Check.Equal(1, AutomationValidation.Parse(bytes).Rules.Length, "round trip");
    foreach (var json in new[]
    {
        "{\"schemaVersion\":1,\"rules\":[],\"oops\":1}",
        "{\"schemaVersion\":1,\"schemaVersion\":1,\"rules\":[]}",
        "{\"schemaVersion\":1}",
        "{\"schemaVersion\":2,\"rules\":[]}",
        Encoding.UTF8.GetString(bytes).Replace("\"daily\"", "0"),
        Encoding.UTF8.GetString(bytes).Replace("\"daily\"", "\"DAILY\""),
        Encoding.UTF8.GetString(bytes).Replace("\"time\":", "\"unknown\":0,\"time\":")
    }) Check.Throws(() => AutomationValidation.Parse(Encoding.UTF8.GetBytes(json)), "strict JSON " + json[..Math.Min(json.Length, 45)]);
    Check.Throws(() => AutomationValidation.Validate(new(1, [rule, rule])), "duplicate IDs");
    Check.Throws(() => AutomationValidation.Validate(new(1, Enumerable.Range(0, 65).Select(i => rule with { Id = "r" + i }).ToArray())), "65 rules");
    AutomationValidation.Validate(new(1, Enumerable.Range(0, 64).Select(i => rule with { Id = "r" + i }).ToArray()));
    Check.True(true, "64 rules");
    foreach (var time in new[] { "24:00", "1:01", "12:60", "00:00:00", "xx:yy" })
        Check.Throws(() => AutomationValidation.Validate(new(1, [Rule(new(AutomationTriggerKind.Daily, time, [1]))])), "invalid time");
    foreach (var days in new[] { Array.Empty<int>(), new[] { 7 }, new[] { -1 }, new[] { 1, 1 } })
        Check.Throws(() => AutomationValidation.Validate(new(1, [Rule(new(AutomationTriggerKind.Daily, "10:30", days))])), "invalid days");
    Check.Throws(() => AutomationValidation.Validate(new(1, [Rule(commands: Enumerable.Repeat(Command(), 17).ToArray())])), "17 commands");
    AutomationValidation.Validate(new(1, [Rule(commands: Enumerable.Repeat(Command(), 16).ToArray())]));
    foreach (var name in new[] { "game", "C:/game.exe", "../game.exe", "game*.exe", "game.exe ", "a\\game.exe" })
        Check.Throws(() => AutomationValidation.Validate(new(1, [Rule(new(AutomationTriggerKind.Process, ProcessName: name))])), "invalid executable");
    var process = new AutomationTrigger(AutomationTriggerKind.Process, ProcessName: "game.exe");
    Check.Throws(() => AutomationValidation.Validate(new(1, [Rule(process, [Command("process.policy", new { pid = 1, priority = "High" })])])), "user PID forbidden");
    Check.Throws(() => AutomationValidation.Validate(new(1, [Rule(process, exits: [Command("device.release")])])), "global release forbidden");
    Check.Throws(() => AutomationValidation.Validate(new(1, [Rule(process, exits: [Command("process.policy")])])), "dead process policy forbidden");
    Check.Throws(() => AutomationValidation.Parse(new byte[AutomationValidation.MaxBytes + 1]), "byte boundary");
    Check.Throws(() => AutomationValidation.Validate(new(1, Enumerable.Range(0, 64).Select(i => Rule(commands: [Command(parameters: new { text = new string('x', 4096) })], id: "r" + i)).ToArray())), "total budget");
    return Task.CompletedTask;
}

static async Task Daily()
{
    var store = new MemoryStore(); var calls = new List<DeviceCommand>(); var engine = Engine(store, calls);
    engine.Write(Doc(Rule()));
    Check.Equal(0, calls.Count, "save only");
    await engine.TickAsync(At("2026-10-31T10:29:59Z"));
    Check.Equal(0, calls.Count, "not early");
    await engine.TickAsync(At("2026-10-31T10:30:59.999Z"));
    await engine.TickAsync(At("2026-10-31T10:30:00Z"));
    Check.Equal(1, calls.Count, "one per local date despite backward time");
    engine = Engine(store, calls);
    await engine.TickAsync(At("2026-10-31T10:30:20Z"));
    Check.Equal(1, calls.Count, "restart no replay");
    await engine.TickAsync(At("2026-11-01T10:31:00Z"));
    Check.Equal(1, calls.Count, "no catchup");
    await engine.TickAsync(At("2026-11-02T10:30:00Z"));
    Check.Equal(2, calls.Count, "next date");
    await engine.TickAsync(At("2026-11-01T10:30:00Z"));
    Check.Equal(2, calls.Count, "date rollback high water");
    engine.Write(Doc(Rule()));
    await engine.TickAsync(At("2026-11-02T10:30:00Z"));
    Check.Equal(2, calls.Count, "save preserves daily watermark");
    var monday = Rule(new(AutomationTriggerKind.Daily, "00:00", [1]), id: "monday");
    engine.Write(Doc(monday));
    await engine.TickAsync(At("2026-11-01T00:00:00Z"));
    await engine.TickAsync(At("2026-11-02T00:00:00Z"));
    Check.Equal(3, calls.Count, "day mapping Sunday zero Monday one");
}

static async Task Dst()
{
    var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    var store = new MemoryStore(); var calls = new List<DeviceCommand>(); var engine = Engine(store, calls, zone: zone);
    engine.Write(Doc(Rule(new(AutomationTriggerKind.Daily, "01:30", [0]))));
    await engine.TickAsync(At("2026-11-01T05:30:00Z"));
    engine = Engine(store, calls, zone: zone);
    await engine.TickAsync(At("2026-11-01T06:30:00Z"));
    Check.Equal(1, calls.Count, "DST overlap once across restart");
    engine.Write(Doc(Rule(new(AutomationTriggerKind.Daily, "02:30", [0]), id: "gap")));
    await engine.TickAsync(At("2027-03-14T06:30:00Z"));
    await engine.TickAsync(At("2027-03-14T07:30:00Z"));
    Check.Equal(1, calls.Count, "DST gap skips instead of catchup");
}

static async Task Failures()
{
    var store = new MemoryStore(); var calls = new List<DeviceCommand>();
    var engine = Engine(store, calls, execute: c => { calls.Add(c); throw new PluginException("TimeoutOutcomeUnknown", "test"); });
    engine.Write(Doc(Rule(commands: [Command(), Command()])));
    await engine.TickAsync(At("2026-10-31T10:30:00Z"));
    await engine.TickAsync(At("2026-11-01T10:30:00Z"));
    Check.Equal(1, calls.Count, "no second command or retry");
    Check.Equal(AutomationExecutionState.Paused, engine.Read().Status[0].State, "paused status");
    engine = Engine(store, calls);
    await engine.TickAsync(At("2026-11-02T10:30:00Z"));
    Check.Equal(1, calls.Count, "restart retains pause");
    engine.Write(Doc(Rule()));
    await engine.TickAsync(At("2026-11-02T10:30:00Z"));
    Check.Equal(2, calls.Count, "explicit save resumes");
    foreach (var status in new[] { "unknown", "partial", "failed", "unsupported" })
    {
        var s = new MemoryStore(); var e = Engine(s, [], execute: _ => Task.FromResult(new ApplyResult(status, "test")));
        e.Write(Doc(Rule())); await e.TickAsync(At("2026-10-31T10:30:00Z"));
        Check.Equal(AutomationExecutionState.Paused, e.Read().Status[0].State, "failure status " + status);
    }
}

static async Task Processes()
{
    var store = new MemoryStore(); var calls = new List<DeviceCommand>(); var source = new FakeSource(); var engine = Engine(store, calls, source);
    var rule = Rule(new(AutomationTriggerKind.Process, ProcessName: "game.exe"), [Command("process.policy", new { priority = "High" })], [Command("lighting.off", new { })]);
    source.Rows = [new(10, At("2026-10-31T08:00:00Z"), "game.exe", 1)];
    engine.Write(Doc(rule));
    await engine.TickAsync(At("2026-10-31T10:00:00Z"));
    Check.Equal(0, calls.Count, "existing baseline no startup replay");
    source.Rows = [new(11, At("2026-10-31T10:00:01Z"), "GAME.EXE", 1), new(12, At("2026-10-31T10:00:01Z"), "game-helper.exe", 1)];
    await engine.TickAsync(At("2026-10-31T10:00:02Z"));
    Check.Equal(1, calls.Count, "case-insensitive exact filename only");
    Check.Equal(11, calls[0].Parameters.GetProperty("pid").GetInt32(), "PID injected");
    Check.Equal(At("2026-10-31T10:00:01Z"), calls[0].Parameters.GetProperty("startTimeUtc").GetDateTimeOffset(), "start identity injected");
    source.Rows = [new(11, At("2026-10-31T10:00:03Z"), "game.exe", 1)];
    await engine.TickAsync(At("2026-10-31T10:00:04Z"));
    Check.Equal(2, calls.Count, "PID reuse is new instance and old exit suppressed");
    source.Rows = [.. source.Rows, new(14, At("2026-10-31T10:00:05Z"), "game.exe", 1)];
    await engine.TickAsync(At("2026-10-31T10:00:06Z"));
    source.Rows = [];
    await engine.TickAsync(At("2026-10-31T10:00:07Z"));
    Check.Equal(4, calls.Count, "two simultaneous exits run one final exit action");
    Check.Equal("lighting.off", calls[^1].Capability, "exit action");
    source.Rows = [new(15, At("2026-10-31T10:00:08Z"), "game.exe", 1)];
    await engine.TickAsync(At("2026-10-31T10:00:09Z"));
    engine = Engine(store, calls, source);
    await engine.TickAsync(At("2026-10-31T10:00:10Z"));
    source.Rows = [];
    await engine.TickAsync(At("2026-10-31T10:00:11Z"));
    Check.Equal(5, calls.Count, "restart no replay or stale exit catchup");
    source.Rows = [new(15, At("2026-10-31T10:00:12Z"), "game.exe", 0)];
    await engine.TickAsync(At("2026-10-31T10:00:13Z"));
    Check.Equal(AutomationExecutionState.Paused, engine.Read().Status[0].State, "session zero fails closed");
    var invalid = Engine(new MemoryStore(), [], validate: _ => throw new PluginException("DeviceUnavailable", "test"));
    Check.Throws(() => invalid.Write(Doc(rule)), "availability validation at save");
    engine.Write(Doc(rule, Rule(id: "daily")));
    source.Throw = true;
    await engine.TickAsync(At("2026-11-01T10:30:00Z"));
    Check.Equal(6, calls.Count, "snapshot error still permits independent daily rule");
    Check.Equal(AutomationExecutionState.Paused, engine.Read().Status[0].State, "snapshot error pauses process rule");
}

static async Task ProcessLighting()
{
    var store = new MemoryStore(); var calls = new List<DeviceCommand>(); var source = new FakeSource();
    var rule = Rule(new(AutomationTriggerKind.Process, ProcessName: "game.exe"),
    [
        Command("lighting.static", new { color = "#112233", brightness = .7, operationId = "saved-static" }),
        Command("lighting.off", new { operationId = "saved-off" }),
        Command("lighting.effect", new { effect = "breathing", color = "#445566", brightness = .8, periodSeconds = 4,
            operationId = "saved-breathing", phaseUtc = "2000-01-01T00:00:00Z" }),
        Command("lighting.effect", new { effect = "rainbow", color = "#778899", brightness = .6, periodSeconds = 6,
            operationId = "saved-rainbow", phaseUtc = "2001-01-01T00:00:00Z" })
    ]);
    var template = Doc(rule).GetRawText();
    var engine = Engine(store, calls, source, execute: async command =>
    {
        var journal = JsonSerializer.Deserialize<AutomationJournal>(store.Journal!, AutomationValidation.Json)!;
        Check.Equal(AutomationExecutionState.Running, journal.Rules[rule.Id].State, "lighting reserved durably before apply");
        calls.Add(command);
        // Separate command execution times so a per-command phase would break batch synchronization.
        if (command.Capability == "lighting.static") await Task.Delay(20);
        return new ApplyResult("applied", "test");
    });
    engine.Write(Doc(rule));
    var saved = store.Document!.ToArray();
    await engine.TickAsync(At("2026-10-31T10:00:00Z"));
    Check.Equal(0, calls.Count, "lighting process baseline has no side effect");
    DateTimeOffset? previousPhase = null;
    for (var execution = 0; execution < 2; execution++)
    {
        source.Rows = [.. source.Rows, new(10 + execution, At("2026-10-31T10:00:01Z").AddSeconds(execution), "game.exe", 1)];
        var before = DateTimeOffset.UtcNow;
        await engine.TickAsync(At("2026-10-31T10:00:03Z").AddSeconds(execution));
        var after = DateTimeOffset.UtcNow;
        Check.Equal((execution + 1) * rule.Commands.Length, calls.Count, "each new process executes the saved lighting batch");
        var batch = calls.Skip(execution * rule.Commands.Length).ToArray();
        for (var i = 0; i < batch.Length; i++)
        {
            var id = batch[i].Parameters.GetProperty("operationId").GetString();
            Check.True(!string.IsNullOrWhiteSpace(id) && id != rule.Commands[i].Parameters.GetProperty("operationId").GetString(),
                "lighting operation identity overrides saved value");
            Check.Equal(rule.Commands[i].DeviceId, batch[i].DeviceId, "lighting device preserved");
            Check.Equal(rule.Commands[i].Capability, batch[i].Capability, "lighting capability preserved");
            foreach (var parameter in rule.Commands[i].Parameters.EnumerateObject().Where(p => p.Name is not ("operationId" or "phaseUtc")))
                Check.Equal(parameter.Value.GetRawText(), batch[i].Parameters.GetProperty(parameter.Name).GetRawText(), "lighting parameter preserved: " + parameter.Name);
        }
        var phase = batch[2].Parameters.GetProperty("phaseUtc").GetDateTimeOffset();
        Check.Equal(TimeSpan.Zero, phase.Offset, "effect phase is UTC");
        Check.True(phase >= before && phase <= after, "effect phase is fresh execution time despite old template and synthetic tick");
        Check.Equal(phase, batch[3].Parameters.GetProperty("phaseUtc").GetDateTimeOffset(), "dynamic effects share one phase per execution");
        if (previousPhase is { } previous) Check.True(phase > previous, "next process execution refreshes phase");
        previousPhase = phase;
        Check.Equal(AutomationExecutionState.Completed, engine.Read().Status[0].State, "lighting execution completed");
        Check.True(saved.SequenceEqual(store.Document!), "execution leaves persisted template unchanged");
        Check.Equal(template, Doc(engine.Read().Rules).GetRawText(), "execution leaves in-memory template unchanged");
    }
    Check.Equal(calls.Count, calls.Select(c => c.Parameters.GetProperty("operationId").GetString()).Distinct().Count(),
        "all lighting commands have distinct operation identities across process triggers");
    Check.Equal(template, Doc(rule).GetRawText(), "caller template unchanged");
}

static async Task Crash()
{
    var store = new MemoryStore(); var calls = new List<DeviceCommand>();
    var engine = Engine(store, calls, execute: c =>
    {
        calls.Add(c);
        store.CrashJournal = store.Journal!.ToArray();
        return Task.FromResult(new ApplyResult("applied", "test"));
    });
    engine.Write(Doc(Rule()));
    await engine.TickAsync(At("2026-10-31T10:30:00Z"));
    store.Journal = store.CrashJournal;
    engine = Engine(store, calls);
    await engine.TickAsync(At("2026-11-01T10:30:00Z"));
    Check.Equal(1, calls.Count, "crashed pending reservation never replays");
    Check.Equal(AutomationExecutionState.Paused, engine.Read().Status[0].State, "pending reservation pauses");
    store.FailDocument = true;
    Check.Throws(() => engine.Write(Doc(Rule())), "interrupted document save");
    store.FailDocument = false;
    engine = Engine(store, calls);
    await engine.TickAsync(At("2026-11-02T10:30:00Z"));
    Check.Equal(1, calls.Count, "two file interrupted save remains paused");
}

static async Task Storage()
{
    var store = new MemoryStore(); var calls = new List<DeviceCommand>(); var engine = Engine(store, calls);
    engine.Write(Doc(Rule())); store.FailJournal = true;
    await engine.TickAsync(At("2026-10-31T10:30:00Z"));
    Check.Equal(0, calls.Count, "reservation store failure prevents write");
    Check.Equal(AutomationExecutionState.Paused, engine.Read().Status[0].State, "store failure global pause");
    store.FailJournal = false; engine.Write(Doc(Rule()));
    engine = Engine(new MemoryStore { Document = store.Document }, calls);
    await engine.TickAsync(At("2026-11-01T10:30:00Z"));
    Check.Equal(0, calls.Count, "missing journal blocks");
    var corrupt = new MemoryStore { Document = store.Document, Journal = Encoding.UTF8.GetBytes("{\"bad\":1}") };
    var broken = Engine(corrupt, calls);
    Check.Throws(() => broken.Read(), "corrupt journal rejected");
    await broken.TickAsync(At("2026-11-01T10:30:00Z"));
    Check.Equal(0, calls.Count, "corrupt journal stays fail closed");
    Check.Throws(() => broken.Write(Doc(Rule())), "corrupt journal cannot erase execution history on save");
    foreach (var incomplete in new[] { "{}", "{\"schemaVersion\":1,\"rules\":{\"one\":{}}}" })
    {
        var e = Engine(new MemoryStore { Document = store.Document, Journal = Encoding.UTF8.GetBytes(incomplete) }, calls);
        Check.Throws(() => e.Read(), "missing journal fields rejected");
    }
    var unmatched = Engine(new MemoryStore { Document = store.Document, Journal = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"rules\":{}}") }, calls);
    await unmatched.TickAsync(At("2026-11-01T10:30:00Z"));
    Check.Equal(0, calls.Count, "missing per-rule journal blocks replay");
    var validStore = new MemoryStore(); var validEngine = Engine(validStore, calls);
    validEngine.Write(Doc(Rule())); await validEngine.TickAsync(At("2026-11-01T10:30:00Z"));
    validStore.Document = Encoding.UTF8.GetBytes("invalid");
    var restored = Engine(validStore, calls);
    Check.Throws(() => restored.Read(), "corrupt document rejected");
    restored.Write(Doc(Rule())); await restored.TickAsync(At("2026-11-01T10:30:00Z"));
    Check.Equal(1, calls.Count, "repairing corrupt document retains valid journal watermark");
    var directory = Path.Combine(Path.GetTempPath(), "lightorder-automation-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        // Ordinary temp directories intentionally fail production ACL validation. Injected store covers persistence semantics.
        Check.Throws(() => new AutomationFileStore(directory).SaveDocument(store.Document!), "unprotected temp store rejected");
        Check.True(!Directory.EnumerateFiles(directory).Any(), "ACL failure leaves no temporary files");
    }
    finally { Directory.Delete(directory, true); }
}

static async Task Concurrency()
{
    var store = new MemoryStore(); var calls = new List<DeviceCommand>();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var engine = Engine(store, calls, execute: async c =>
    {
        calls.Add(c); entered.SetResult(); await release.Task;
        return new ApplyResult("verified", "test");
    });
    engine.Write(Doc(Rule()));
    var first = engine.TickAsync(At("2026-10-31T10:30:00Z"));
    await entered.Task;
    var second = engine.TickAsync(At("2026-10-31T10:30:00Z"));
    Check.Equal(1, calls.Count, "overlapping ticks do not duplicate pending action");
    release.SetResult(); await Task.WhenAll(first, second);
    Check.Equal(1, calls.Count, "serialized ticks honor watermark");
    store = new MemoryStore(); calls = [];
    engine = Engine(store, calls, execute: c =>
    {
        calls.Add(c); store.FailJournal = true;
        return Task.FromResult(new ApplyResult("applied", "test"));
    });
    engine.Write(Doc(Rule()));
    await engine.TickAsync(At("2026-10-31T10:30:00Z"));
    Check.Equal(1, calls.Count, "device write happened before completion-save failure");
    store.FailJournal = false;
    engine = Engine(store, calls);
    await engine.TickAsync(At("2026-11-01T10:30:00Z"));
    Check.Equal(1, calls.Count, "completion-save failure reservation survives restart");
    Check.Equal(AutomationExecutionState.Paused, engine.Read().Status[0].State, "completion failure pauses");
}

static async Task TargetedSource()
{
    var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "game.exe", "game.exe.exe" };
    Check.True(WindowsAutomationProcessSource.MatchesEnumeratedName("GAME", targets), "enumeration stem matches case-insensitively");
    Check.True(!WindowsAutomationProcessSource.MatchesEnumeratedName("game-helper", targets), "unrelated executable excluded");
    Check.True(WindowsAutomationProcessSource.MatchesEnumeratedName("game.exe", targets), "double-extension executable keeps exact stem");
    var source = new TargetedFakeSource(); var calls = new List<DeviceCommand>();
    var rule = Rule(new(AutomationTriggerKind.Process, ProcessName: "game.exe"));
    var engine = Engine(new MemoryStore(), calls, source);
    engine.Write(Doc(rule, rule with { Id = "duplicate", Trigger = new(AutomationTriggerKind.Process, ProcessName: "GAME.EXE") },
        rule with { Id = "disabled", Enabled = false, Trigger = new(AutomationTriggerKind.Process, ProcessName: "security.exe") }));
    await engine.TickAsync(At("2026-10-31T10:00:00Z"));
    Check.Equal(1, source.Names.Length, "active target names distinct and disabled names excluded");
    Check.Equal("game.exe", source.Names[0], "engine passes exact executable target");
    Check.Equal(0, source.ProtectedInspections, "unrelated protected process not inspected");
    Check.Equal(AutomationExecutionState.Ready, engine.Read().Status[0].State, "unrelated protected process does not pause rule");
    source.DenyMatching = true;
    await engine.TickAsync(At("2026-10-31T10:00:01Z"));
    Check.Equal(AutomationExecutionState.Paused, engine.Read().Status[0].State, "matching access denial still pauses rule");
    Check.Equal(0, calls.Count, "access denial performs no device writes");
}

static async Task SourceAndLogs()
{
    var store = new MemoryStore(); var calls = new List<DeviceCommand>(); var engine = Engine(store, calls);
    for (var i = 0; i < 80; i++) engine.Write(Doc(Rule()));
    Check.Equal(64, engine.Diagnostics.Length, "log cap");
    Check.Throws(() => new WindowsAutomationProcessSource("not-a-sid"), "owner required");
    var source = new FakeSource();
    engine = Engine(new MemoryStore(), calls, source);
    engine.Write(Doc(Rule(new(AutomationTriggerKind.Process, ProcessName: "game.exe"))));
    await engine.TickAsync(At("2026-10-31T10:00:00Z"));
    source.Rows = Enumerable.Range(1, 17).Select(i => new AutomationProcessSnapshot(i, At("2026-10-31T10:00:01Z"), "game.exe", 1)).ToArray();
    await engine.TickAsync(At("2026-10-31T10:00:02Z"));
    Check.Equal(0, calls.Count, "burst pauses without storm");
    Check.Equal(AutomationExecutionState.Paused, engine.Read().Status[0].State, "burst paused");
}

sealed class MemoryStore : IAutomationStore
{
    public byte[]? Document, Journal, CrashJournal;
    public bool FailJournal, FailDocument;
    public byte[]? LoadDocument() => Document?.ToArray();
    public byte[]? LoadJournal() => Journal?.ToArray();
    public void SaveDocument(byte[] bytes) { if (FailDocument) throw new IOException("test"); Document = bytes.ToArray(); }
    public void SaveJournal(byte[] bytes) { if (FailJournal) throw new IOException("test"); Journal = bytes.ToArray(); }
}
sealed class FakeSource : IAutomationProcessSource
{
    public AutomationProcessSnapshot[] Rows = [];
    public bool Throw;
    public IReadOnlyList<AutomationProcessSnapshot> Snapshot() => Throw ? throw new IOException("test") : Rows;
}
sealed class TargetedFakeSource : ITargetedAutomationProcessSource
{
    public string[] Names = [];
    public int ProtectedInspections;
    public bool DenyMatching;
    public IReadOnlyList<AutomationProcessSnapshot> Snapshot() => throw new Exception("Untargeted path must not be called.");
    public IReadOnlyList<AutomationProcessSnapshot> Snapshot(string[] processNames)
    {
        Names = processNames;
        var targets = processNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "security", "game" })
        {
            if (!WindowsAutomationProcessSource.MatchesEnumeratedName(name, targets)) continue;
            if (name == "security") { ProtectedInspections++; throw new System.ComponentModel.Win32Exception(5); }
            if (DenyMatching) throw new System.ComponentModel.Win32Exception(5);
        }
        return [];
    }
}
static class Check
{
    public static int Count;
    public static void True(bool condition, string message) { Count++; if (!condition) throw new Exception(message); }
    public static void Equal<T>(T expected, T actual, string message) => True(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, got {actual}");
    public static void Throws(Action action, string message)
    {
        Count++;
        try { action(); } catch { return; }
        throw new Exception("Expected exception: " + message);
    }
}
