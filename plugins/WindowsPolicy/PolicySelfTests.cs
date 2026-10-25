using ControlCenter.Contracts;

namespace ControlCenter.WindowsPolicy;

// Runs entirely against memory; no NativePolicy instance, disk journal, or OS writes.
internal static class PolicySelfTests
{
    public static async Task RunAsync()
    {
        int passed = 0;
        await Test("per-property external ownership", async (plugin, system, journal) =>
        {
            await Apply(plugin, system, "High", "3");
            system.Priority = "BelowNormal";
            await Call(plugin, "device.release");
            Assert(system.Priority == "BelowNormal" && system.Affinity == "0xF", "Release overwrote another owner's property or missed owned affinity.");
            Assert(journal.Entries.Count == 0, "Release retained completed entries.");
        });
        await Test("partial apply rollback", async (plugin, system, journal) =>
        {
            system.FailAffinityOnce = true;
            await Expect("AccessDenied", () => Apply(plugin, system, "High", "3"));
            Assert(system.Priority == "Normal" && system.Affinity == "0xF", "Partial apply was not rolled back.");
            Assert(journal.Entries.Count == 0, "Successful rollback retained entries.");
        });
        await Test("PID reuse", async (plugin, system, journal) =>
        {
            await Apply(plugin, system, "High", null);
            system.Start = "2026-01-02T00:00:00.0000000Z";
            await Call(plugin, "device.release");
            Assert(system.Priority == "High" && journal.Entries.Count == 0, "Reused PID was modified.");
        });
        await Test("identity and validation", async (plugin, system, journal) =>
        {
            await Expect("ProcessIdentityMismatch", () => Call(plugin, "process.apply", new { pid = 42, startTimeUtc = "2026-02-01T00:00:00Z", priority = "High" }));
            await Expect("InvalidArgument", () => Apply(plugin, system, "RealTime", null));
            await Expect("InvalidArgument", () => Apply(plugin, system, "High", "10"));
            Assert(system.Writes == 0, "Validation performed writes.");
        });
        await Test("repeat apply preserves original", async (plugin, system, journal) =>
        {
            await Apply(plugin, system, "High", null);
            await Apply(plugin, system, "AboveNormal", null);
            await plugin.DisposeAsync();
            Assert(system.Priority == "Normal", "Dispose failed to restore the first baseline.");
        });
        await Test("stale journal explicit recovery", async (plugin, system, journal) =>
        {
            system.Priority = "High";
            journal.Entries.Add(new("priority", 42, system.Start, "Normal", "High"));
            journal.PendingFromPreviousRun = true;
            await Expect("RecoveryRequired", () => Apply(plugin, system, "Idle", null));
            await Expect("RecoveryRequired", () => Call(plugin, "device.release"));
            Assert(system.Writes == 0, "Stale journal caused implicit writes.");
            await Call(plugin, "policy.recover");
            Assert(system.Priority == "Normal" && !journal.PendingFromPreviousRun, "Explicit recovery failed.");
        });
        await Test("stale journal dispose leaves it alone", async (plugin, system, journal) =>
        {
            system.Priority = "High";
            journal.Entries.Add(new("priority", 42, system.Start, "Normal", "High"));
            journal.PendingFromPreviousRun = true;
            await plugin.DisposeAsync();
            Assert(system.Priority == "High" && system.Writes == 0, "Dispose recovered stale changes without consent.");
        });
        await Test("power ownership", async (plugin, system, journal) =>
        {
            Guid baseline = system.Active;
            await Call(plugin, "power.apply", new { schemeId = FakeSystem.Alternate.ToString() });
            await Call(plugin, "device.release");
            Assert(system.Active == baseline, "Owned power scheme was not restored.");
            await Call(plugin, "power.apply", new { schemeId = FakeSystem.Alternate.ToString() });
            system.Active = Guid.NewGuid();
            Guid external = system.Active;
            await Call(plugin, "device.release");
            Assert(system.Active == external, "External power scheme was overwritten.");
        });
        await Test("standard adapter and metadata", async (plugin, system, journal) =>
        {
            var devices = (StandardDevice[])await Call(plugin, "device.discover");
            Assert(devices.Length == 2 && devices[0].Capabilities.Contains("power.windowsScheme") && devices[0].Capabilities.Contains("device.release"), "Standard capabilities missing.");
            Assert(devices[0].Metadata!.Value.GetProperty("power").GetProperty("schemes").GetArrayLength() == 2, "Power options missing.");
            var legacy = Wire.Read<DeviceInfo[]>(Wire.Element(devices));
            Assert(legacy[0].Id == WindowsPolicyPlugin.DeviceId, "Legacy discovery compatibility failed.");
            var result = (ApplyResult)await Call(plugin, "device.execute", new DeviceCommand(WindowsPolicyPlugin.DeviceId, "process.policy", Wire.Element(new { pid = 42, startTimeUtc = system.Start, priority = "High" })));
            Assert(result.Status == "applied" && system.Priority == "High", "Standard command did not use policy transaction.");
            await Call(plugin, "device.execute", new DeviceCommand(WindowsPolicyPlugin.DeviceId, "device.release", Wire.Element(new { })));
            Assert(system.Priority == "Normal", "Standard release did not restore.");
            await Expect("DeviceNotFound", () => Call(plugin, "device.execute", new DeviceCommand("wrong", "power.windowsScheme", Wire.Element(new { schemeId = FakeSystem.Alternate }))));
            await Expect("ReadOnly", () => Call(plugin, "device.execute", new DeviceCommand("system-info", "system.info", Wire.Element(new { }))));
            Assert(((object[])await Call(plugin, "telemetry.read")).Length == 0, "Policy telemetry must be an array.");
        });
        Console.WriteLine($"WindowsPolicy self-test: {passed} passed; no native calls or system settings changed.");

        async Task Test(string name, Func<WindowsPolicyPlugin, FakeSystem, RecoveryJournal, Task> run)
        {
            var system = new FakeSystem();
            var journal = new RecoveryJournal(memoryOnly: true);
            await using var plugin = new WindowsPolicyPlugin(system, journal, () => new StandardDevice("system-info", "Fake components", "system", ["system.info"], "read-only", "Fake data for tests only"));
            await run(plugin, system, journal);
            passed++;
            Console.WriteLine($"PASS {name}");
        }
    }

    private static Task<object> Call(WindowsPolicyPlugin plugin, string method, object? args = null) =>
        plugin.HandleAsync(method, Wire.Element(args ?? new { }), CancellationToken.None);
    private static Task<object> Apply(WindowsPolicyPlugin plugin, FakeSystem system, string priority, string? mask) =>
        mask is null ? Call(plugin, "process.apply", new { pid = 42, startTimeUtc = system.Start, priority }) :
        Call(plugin, "process.apply", new { pid = 42, startTimeUtc = system.Start, priority, affinityMask = mask });
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static async Task Expect(string code, Func<Task<object>> action)
    {
        try { await action(); }
        catch (PluginException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException($"Expected error {code}.");
    }

    private sealed class FakeSystem : IPolicySystem
    {
        public static readonly Guid Alternate = Guid.Parse("00000000-0000-0000-0000-000000000002");
        public Guid Active = Guid.Parse("00000000-0000-0000-0000-000000000001");
        public string Priority = "Normal";
        public string Affinity = "0xF";
        public string Start = "2026-01-01T00:00:00.0000000Z";
        public bool FailAffinityOnce;
        public int Writes;
        public int ProcessorGroupCount => 1;
        public IReadOnlyList<Scheme> ListSchemes() => [new(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Baseline"), new(Alternate, "Alternate")];
        public Guid ReadActiveScheme() => Active;
        public void SetActiveScheme(Guid id) { Active = id; Writes++; }
        public int[] ListProcessIds() => [42];
        public string ProcessName(int pid) => "fake-process";
        public IProcessTarget OpenProcess(int pid, bool writable) => new Target(this);
        private sealed class Target(FakeSystem owner) : IProcessTarget
        {
            public string StartTimeUtc { get; } = owner.Start;
            public string ReadPriority() => owner.Priority;
            public string ReadAffinity() => owner.Affinity;
            public string SystemAffinity => "0xF";
            public void SetPriority(string value) { owner.Priority = value; owner.Writes++; }
            public void SetAffinity(string value)
            {
                if (owner.FailAffinityOnce)
                {
                    owner.FailAffinityOnce = false;
                    throw new PluginException("AccessDenied", "Injected affinity access denial.");
                }
                owner.Affinity = value;
                owner.Writes++;
            }
            public void Dispose() { }
        }
    }
}
