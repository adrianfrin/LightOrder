using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using ControlCenter.Contracts;

// In-memory backend and monotonic clock only. No Computer, CIM, or controller lease.
static class ServiceMonitorSelfTests
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); count++; }
        async Task Reject(ServiceMonitor monitor, object command, string code)
        {
            try { await monitor.HandleAsync("device.execute", Wire.Element(command), default); }
            catch (PluginException e) when (e.Code == code) { count++; return; }
            throw new InvalidOperationException("Expected " + code);
        }
        object Command(string capability = "fan.profile", FanProfile? profile = null) => new DeviceCommand(ServiceMonitor.DeviceId, capability, Wire.Element(profile ?? FanProfile.Silent));
        Task<object> Apply(ServiceMonitor m, FanProfile? p = null, CancellationToken token = default) => m.HandleAsync("device.execute", Wire.Element(Command(profile: p)), token);
        async Task<JsonElement> Metadata(ServiceMonitor m) => ((StandardDevice[])
            await m.HandleAsync("device.discover", Wire.Element(new { }), default))[0].Metadata!.Value;
        try
        {
            var clock = new FakeTime();
            var backend = new FakeBackend(clock);
            await using (var monitor = new ServiceMonitor(backend, clock, false))
            {
                Check(backend.Opens == 0, "lazy opening");
                var devices = (StandardDevice[])await monitor.HandleAsync("device.discover", Wire.Element(new { }), default);
                Check(backend.Opens == 1 && devices[0].Capabilities.Contains("fan.profile"), "standard discovery");
                Check(Wire.Read<DeviceInfo[]>(Wire.Element(devices))[0].Id == ServiceMonitor.DeviceId, "legacy discovery");
                var meta = devices[0].Metadata!.Value;
                Check(meta.GetProperty("sessionLimitSeconds").ValueKind == JsonValueKind.Null && meta.GetProperty("controlMode").GetString() == "continuous", "explicit continuous mode");
                Check(meta.GetProperty("channels")[2].GetProperty("readOnly").GetBoolean(), "pump read-only");
                await Reject(monitor, Command(profile: FanProfile.Silent with { RadiatorPercent = 0 }), "InvalidArgument");
                Check(backend.Writes == 0, "invalid profile no write");
                await Apply(monitor);
                Check(backend.Writes == 1 && backend.LastRadiator == 40 && backend.LastCase == 25, "fresh low-temperature startup without full-speed burst");
                var started = (await Metadata(monitor)).GetProperty("session").GetProperty("startedUtc").GetString();
                for (var i = 0; i < 3605; i++)
                {
                    clock.Advance(TimeSpan.FromSeconds(1));
                    await monitor.PollOnceAsync();
                }
                Check(backend.Writes == 3606 && backend.Restores == 0 && backend.Begins == 1, "continuous beyond 1800 updates and 30 minutes");
                Check(backend.LastRadiator == 40 && backend.LastCase == 25, "curve reaches valid low temperature target");
                var manual = FanProfile.Silent with { Mode = "manual", RadiatorPercent = 73, CasePercent = 47 };
                await Apply(monitor, manual);
                Check(backend.LastRadiator == 73 && backend.LastCase == 47 && backend.Begins == 1, "profile update retains captured baseline");
                Check((await Metadata(monitor)).GetProperty("session").GetProperty("startedUtc").GetString() == started, "profile update retains start");
                // Wall clock jumps alone must not trigger suspend recovery.
                clock.WallOffset = TimeSpan.FromDays(-2);
                await monitor.PollOnceAsync();
                Check(backend.Restores == 0, "elapsed uses monotonic clock");
                backend.ExtraSamples = 500;
                await monitor.PollOnceAsync();
                var telemetry = (MonitorSample[])await monitor.HandleAsync("telemetry.read", Wire.Element(new { }), default);
                Check(telemetry.Length <= ServiceMonitor.TelemetryLimit, "bounded in-memory telemetry");
                backend.ExtraSamples = 0;
                clock.Advance(TimeSpan.FromMinutes(10));
                var before = backend.Events.Count;
                await monitor.PollOnceAsync();
                Check(backend.Events.Skip(before).SequenceEqual(new[] { "restore", "sample", "begin", "apply" }), "gap restores before new sampling and writes");
                Check(backend.LastRadiator == 73 && backend.LastCase == 47 && backend.Begins == 2, "resume starts from freshly validated real temperature");
                var release = (ApplyResult)await monitor.HandleAsync("device.execute", Wire.Element(Command("device.release")), default);
                Check(release.Status == "released" && backend.Restores == 2 && Wire.Element(release.Readback).GetProperty("restorationVerified").GetBoolean(), "explicit release verified");
                var writes = backend.Writes;
                await monitor.PollOnceAsync();
                Check(backend.Writes == writes, "release stops control");
                await Apply(monitor);
            }
            Check(backend.Restores == 3 && backend.Disposed, "dispose restores");

            async Task SensorFailure(Action<FakeBackend> inject, string name, bool gap = false)
            {
                var c = new FakeTime(); var b = new FakeBackend(c);
                await using var m = new ServiceMonitor(b, c, false);
                await Apply(m);
                inject(b);
                // Low-RPM checks must keep each cycle below the suspend threshold.
                if (!gap) { for (int i = 0; i < 9; i++) { c.Advance(TimeSpan.FromSeconds(1)); await m.PollOnceAsync(); } }
                else { c.Advance(TimeSpan.FromSeconds(20)); await m.PollOnceAsync(); }
                Check(b.Restores >= 1 && b.Writes <= 9, name);
                var meta = await Metadata(m);
                Check(meta.GetProperty("readOnly").GetBoolean(), name + " latches read-only");
                await Reject(m, Command(), "ReadOnly");
            }
            await SensorFailure(b => b.Temperature = float.NaN, "nonfinite sensor restores");
            await SensorFailure(b => b.Temperature = null, "missing sensor restores");
            await SensorFailure(b => b.Rpm = float.NaN, "nonfinite tachometer restores");
            await SensorFailure(b => b.Rpm = 199, "low RPM after grace restores");
            await SensorFailure(b => b.Age = TimeSpan.FromSeconds(4), "stale telemetry restores", true);
            await SensorFailure(b => b.Age = TimeSpan.FromSeconds(-1), "future telemetry refused", true);
            await SensorFailure(b => b.ThrowSample = true, "sample exception restores", true);

            var slowClock = new FakeTime(); var slow = new FakeBackend(slowClock);
            await using (var m = new ServiceMonitor(slow, slowClock, false))
            {
                await Apply(m);
                slow.OnSample = () => slowClock.Advance(TimeSpan.FromSeconds(6));
                await m.PollOnceAsync();
                Check(slow.Restores == 1 && slow.Writes == 1, "suspend during sensor read prevents write");
                slow.OnSample = null;
                await m.PollOnceAsync();
                Check(slow.Begins == 2 && slow.LastRadiator == 40, "fresh cycle resumes without synthetic temperature");
            }
            var power = new FakeBackend(new FakeTime());
            await using (var m = new ServiceMonitor(power, power.Clock, false))
            {
                await Apply(m);
                m.HandlePowerTransition(true);
                Check(power.Restores == 1, "suspend signal proactively restores");
                await m.PollOnceAsync();
                Check(power.Writes == 1, "no polling write while suspended");
                await Reject(m, Command(), "ReadOnly");
                m.HandlePowerTransition(false);
                await m.PollOnceAsync();
                Check(power.Begins == 2 && power.LastRadiator == 40, "resume notification uses fresh real temperature");
                m.HandlePowerTransition(false);
                Check(power.Restores == 2, "resume without suspend still restores");
            }
            var slowWrite = new FakeBackend(new FakeTime());
            await using (var m = new ServiceMonitor(slowWrite, slowWrite.Clock, false))
            {
                await Apply(m);
                slowWrite.OnApply = () => slowWrite.Clock.Advance(TimeSpan.FromSeconds(6));
                await m.PollOnceAsync();
                Check(slowWrite.Restores == 1, "slow control I/O restores immediately on return");
                slowWrite.OnApply = null;
            }
            var failed = new FakeBackend(new FakeTime()) { FailWrite = true };
            await using (var m = new ServiceMonitor(failed, failed.Clock, false))
            {
                await Reject(m, Command(), "ControlFailed");
                Check(failed.Restores == 1, "partial write restores");
                await m.HandleAsync("device.release", Wire.Element(new { }), default);
                await Reject(m, Command(), "ReadOnly");
            }
            var restoreFailure = new FakeBackend(new FakeTime()) { FailRestore = true };
            await using (var m = new ServiceMonitor(restoreFailure, restoreFailure.Clock, false))
            {
                await Apply(m);
                restoreFailure.Clock.Advance(TimeSpan.FromMinutes(1));
                await m.PollOnceAsync();
                Check(restoreFailure.Writes == 1 && restoreFailure.Begins == 1, "failed gap restore prevents takeover");
                Check((await Metadata(m)).GetProperty("session").GetProperty("status").GetString() == "restore-failed", "failed register verification visible");
                await Reject(m, Command(), "ReadOnly");
                restoreFailure.FailRestore = false;
                var release = (ApplyResult)await m.HandleAsync("device.release", Wire.Element(new { }), default);
                Check(release.Status == "released" && restoreFailure.Restores >= 2, "explicit release retries pending restoration");
            }
            var absent = new FakeBackend(new FakeTime()) { Available = false };
            await using (var m = new ServiceMonitor(absent, absent.Clock, false))
            {
                await Reject(m, Command(), "ReadOnly");
                Check(absent.Writes == 0, "unrecognized board read-only");
            }
            var cancelBackend = new FakeBackend(new FakeTime());
            var cancelledMonitor = new ServiceMonitor(cancelBackend, cancelBackend.Clock, false);
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                try { await Apply(cancelledMonitor, token: cancel.Token); throw new Exception("cancellation missing"); }
                catch (OperationCanceledException) { Check(cancelBackend.Opens == 0, "pre-cancelled request cannot open hardware"); }
            }
            await Apply(cancelledMonitor);
            using (var cancel = new CancellationTokenSource())
            {
                cancelBackend.OnApply = cancel.Cancel;
                try { await Apply(cancelledMonitor, token: cancel.Token); throw new Exception("cancellation missing"); }
                catch (OperationCanceledException) { Check(cancelBackend.Restores == 1, "cancellation during write restores"); }
            }
            cancelBackend.OnApply = null;
            await Apply(cancelledMonitor);
            await cancelledMonitor.DisposeAsync();
            await cancelledMonitor.DisposeAsync();
            Check(cancelBackend.Restores == 2 && cancelBackend.DisposeCount == 1, "idempotent disposal");

            // Exercise the real shared RPC server EOF/shutdown disposal path using fake hardware.
            foreach (var input in new[] { "", "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"plugin.shutdown\",\"params\":{}}\n", "incomplete" })
            {
                var b = new FakeBackend(new FakeTime()); var m = new ServiceMonitor(b, b.Clock, false);
                await Apply(m);
                var oldIn = Console.In; var oldOut = Console.Out;
                try
                {
                    Console.SetIn(new StringReader(input)); Console.SetOut(new StringWriter());
                    try { await PluginServer.RunAsync(new FakePlugin(m)); }
                    catch (EndOfStreamException) when (input == "incomplete") { }
                }
                finally { Console.SetIn(oldIn); Console.SetOut(oldOut); }
                Check(b.Disposed && b.Restores == 1, "RPC EOF/shutdown/incomplete input restores");
            }

            // Test the same restoration helper used by the hardware backend, without LHM.
            var baseline = new Dictionary<string, byte> { ["0x13"] = 0x77, ["0x15"] = 0xD0, ["0x63"] = 0x42, ["0x7B"] = 0x3F, ["0x7F"] = 0xC0 };
            var commands = new List<string>();
            FanRestoration.RestoreVerified(() => commands.Add("radiator"), () => commands.Add("case"), baseline, () => { commands.Add("readback"); return baseline; });
            Check(commands.SequenceEqual(new[] { "radiator", "case", "readback" }), "default commands precede register verification");
            foreach (var address in baseline.Keys)
            {
                var mismatch = new Dictionary<string, byte>(baseline) { [address] = (byte)(baseline[address] ^ 1) };
                try { FanRestoration.RestoreVerified(() => { }, () => { }, baseline, () => mismatch); throw new Exception("verification accepted mismatch"); }
                catch (InvalidOperationException) { count++; }
            }
            commands.Clear();
            try
            {
                FanRestoration.RestoreVerified(() => { commands.Add("radiator"); throw new IOException("setter"); }, () => commands.Add("case"), baseline,
                    () => { commands.Add("readback"); return baseline; });
                throw new Exception("setter failure accepted");
            }
            catch (InvalidOperationException) { Check(commands.SequenceEqual(new[] { "radiator", "case", "readback" }), "both restore attempts and readback run after setter failure"); }
            try { FanRestoration.RestoreVerified(() => { }, () => { }, baseline, () => throw new IOException("report unavailable")); throw new Exception("missing readback accepted"); }
            catch (InvalidOperationException) { count++; }

            // Temp evidence only. Never touches the real ProgramData marker.
            var temp = Path.Combine(Path.GetTempPath(), "icc-fake-intent-" + Guid.NewGuid().ToString("N"), "intent.json");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                var validations = 0;
                void FakeValidate(string _) { validations++; }
                var intent = new RestorationIntent(temp, FakeValidate);
                RestorationIntent.RefuseUnresolved(temp, FakeValidate);
                intent.Record(baseline);
                try { RestorationIntent.RefuseUnresolved(temp, FakeValidate); throw new Exception("unresolved intent allowed takeover"); }
                catch (InvalidOperationException) { count++; }
                using var document = JsonDocument.Parse(File.ReadAllText(temp));
                Check(document.RootElement.GetProperty("baseline").GetProperty("0x13").GetByte() == 0x77, "durable baseline evidence");
                try { intent.Record(baseline); throw new Exception("overwrote pending intent"); } catch (IOException) { count++; }
                intent.ClearVerified();
                Check(!File.Exists(temp), "verified restoration clears intent");
                RestorationIntent.RefuseUnresolved(temp, FakeValidate);
                Check(validations == 6, "create/read/delete all validate protected path");
                File.WriteAllText(temp, "retain evidence");
                var denied = new RestorationIntent(temp, _ => throw new UnauthorizedAccessException("unsafe ACL"));
                try { denied.ClearVerified(); throw new Exception("unsafe deletion permitted"); }
                catch (UnauthorizedAccessException) { Check(File.Exists(temp), "unsafe delete preserves marker"); }
                try { denied.Record(baseline); throw new Exception("unsafe write permitted"); }
                catch (UnauthorizedAccessException) { Check(File.ReadAllText(temp) == "retain evidence", "unsafe write refused before file access"); }
                try { RestorationIntent.RefuseUnresolved(temp, _ => throw new UnauthorizedAccessException()); throw new Exception("unsafe read permitted"); }
                catch (UnauthorizedAccessException) { count++; }
            }
            finally { Directory.Delete(Path.GetDirectoryName(temp)!, true); }
            Check(ProtectedIntentPath.IsExpectedPath(RestorationIntent.DefaultPath), "protected ProgramData LightOrder path");
            Check(!ProtectedIntentPath.IsExpectedPath(temp), "reject unexpected state location");
            try { ProtectedIntentPath.RejectReparse(FileAttributes.Directory | FileAttributes.ReparsePoint); throw new Exception("reparse accepted"); }
            catch (UnauthorizedAccessException) { count++; }
            DirectorySecurity Acl(string owner = "S-1-5-32-544")
            {
                var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
                acl.SetOwner(new SecurityIdentifier(owner));
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-18"), FileSystemRights.FullControl, AccessControlType.Allow));
                return acl;
            }
            var trustedAcl = Acl();
            ProtectedIntentPath.ValidateAcl(trustedAcl, false); count++;
            foreach (var rights in new[] { FileSystemRights.Write, FileSystemRights.Delete, FileSystemRights.ChangePermissions,
                FileSystemRights.TakeOwnership, FileSystemRights.DeleteSubdirectoriesAndFiles })
            {
                var acl = Acl();
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-11"), rights, AccessControlType.Allow));
                try { ProtectedIntentPath.ValidateAcl(acl, false); throw new Exception("unsafe grant accepted"); }
                catch (UnauthorizedAccessException) { count++; }
            }
            var ancestorAcl = Acl();
            ancestorAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-11"), FileSystemRights.CreateDirectories, AccessControlType.Allow));
            ProtectedIntentPath.ValidateAcl(ancestorAcl, true); count++;
            ancestorAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-11"), FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow));
            try { ProtectedIntentPath.ValidateAcl(ancestorAcl, true); throw new Exception("unsafe ancestor accepted"); }
            catch (UnauthorizedAccessException) { count++; }
            try { ProtectedIntentPath.ValidateAcl(Acl("S-1-5-21-1-2-3-1001"), false); throw new Exception("unsafe owner accepted"); }
            catch (UnauthorizedAccessException) { count++; }
            var noDacl = new DirectorySecurity();
            noDacl.SetSecurityDescriptorSddlForm("O:BAG:BAD:NO_ACCESS_CONTROL");
            try { ProtectedIntentPath.ValidateAcl(noDacl, false); throw new Exception("null DACL accepted"); }
            catch (UnauthorizedAccessException) { count++; }
            count += FanControllerTests.Run();
            Console.WriteLine($"Service monitor self-test: {count} assertions passed; no hardware accessed.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private sealed class FakePlugin(ServiceMonitor monitor) : IDevicePlugin
    {
        public PluginHello Hello => new("fake", "1");
        public Task<object> HandleAsync(string method, JsonElement args, CancellationToken token) => monitor.HandleAsync(method, args, token);
        public ValueTask DisposeAsync() => monitor.DisposeAsync();
    }
    private sealed class FakeTime : TimeProvider
    {
        private long ticks;
        public TimeSpan WallOffset;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks) + WallOffset;
        public void Advance(TimeSpan value) => ticks += value.Ticks;
    }
    private sealed class FakeBackend(FakeTime clock) : IMonitorBackend
    {
        public FakeTime Clock => clock;
        public bool Available = true, FailWrite, FailRestore, Disposed, ThrowSample;
        public float? Temperature = 40;
        public float Rpm = 900, LastRadiator, LastCase;
        public int Opens, Writes, Begins, Restores, DisposeCount, ExtraSamples;
        public TimeSpan Age;
        public Action? OnSample, OnApply;
        public List<string> Events = [];
        public bool CanControl => Available;
        public string Detail => "Fake hardware for tests only";
        public void Open() => Opens++;
        public MonitorSample[] Sample()
        {
            Events.Add("sample");
            if (ThrowSample) throw new IOException("Injected sample failure");
            OnSample?.Invoke();
            var stamp = clock.GetUtcNow() - Age;
            return new MonitorSample[] { new(ServiceMonitor.DeviceId, "cpu.temperature", "temperature", Temperature, "°C", stamp),
                new(ServiceMonitor.DeviceId, "radiator.rpm", "fanRpm", Rpm, "RPM", stamp), new(ServiceMonitor.DeviceId, "case.rpm", "fanRpm", Rpm, "RPM", stamp) }
                .Concat(Enumerable.Range(0, ExtraSamples).Select(i => new MonitorSample(ServiceMonitor.DeviceId, "extra" + i, "load", 1, "%", stamp))).ToArray();
        }
        public object LegacySnapshot() => new { };
        public void BeginControl() { Events.Add("begin"); Begins++; }
        public void Apply(float radiator, float chassis)
        {
            Events.Add("apply"); Writes++; LastRadiator = radiator; LastCase = chassis;
            OnApply?.Invoke();
            if (FailWrite) throw new InvalidOperationException("Injected partial control failure.");
        }
        public bool Restore() { Events.Add("restore"); Restores++; if (FailRestore) throw new InvalidOperationException("Injected restoration failure."); return Writes > 0; }
        public void Dispose() { Disposed = true; DisposeCount++; }
    }
}
