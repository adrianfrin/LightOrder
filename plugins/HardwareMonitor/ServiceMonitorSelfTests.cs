using ControlCenter.Contracts;

// Only in-memory backend and clock. No Computer, CIM, OS policy, or controller lease.
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
        try
        {
            var backend = new FakeBackend();
            var clock = new FakeTime();
            await using (var monitor = new ServiceMonitor(backend, clock))
            {
                Check(backend.Opens == 0, "lazy opening");
                var devices = (StandardDevice[])await monitor.HandleAsync("device.discover", Wire.Element(new { }), default);
                Check(backend.Opens == 1 && devices[0].Capabilities.Contains("fan.profile"), "standard discovery");
                Check(Wire.Read<DeviceInfo[]>(Wire.Element(devices))[0].Id == ServiceMonitor.DeviceId, "legacy discovery");
                Check(devices[0].Metadata!.Value.GetProperty("sessionLimitSeconds").GetInt32() == 1800, "bounded metadata");
                await Reject(monitor, Command(profile: FanProfile.Silent with { RadiatorPercent = 0 }), "InvalidArgument");
                Check(backend.Writes == 0, "invalid profile no write");
                await monitor.HandleAsync("device.execute", Wire.Element(Command()), default);
                Check(backend.Writes == 1 && backend.LastRadiator == 100 && backend.LastCase == 100, "conservative startup");
                clock.Advance(TimeSpan.FromMinutes(29));
                await monitor.HandleAsync("device.execute", Wire.Element(Command()), default);
                Check(backend.Begins == 1, "reapply retains session baseline and timer");
                clock.Advance(TimeSpan.FromMinutes(1));
                await Reject(monitor, Command(), "SessionExpired");
                Check(backend.Restores == 1 && backend.Writes == 2, "expiry restores without extra write");
                await monitor.HandleAsync("device.execute", Wire.Element(Command()), default);
                var release = (ApplyResult)await monitor.HandleAsync("device.execute", Wire.Element(Command("device.release")), default);
                Check(release.Status == "released" && backend.Restores == 2, "standard release");
                await monitor.HandleAsync("device.execute", Wire.Element(Command()), default);
            }
            Check(backend.Restores == 3 && backend.Disposed, "dispose restores");
            var failing = new FakeBackend { FailWrite = true };
            await using (var monitor = new ServiceMonitor(failing))
            {
                await Reject(monitor, Command(), "ControlFailed");
                Check(failing.Restores == 1, "partial write restores");
                await Reject(monitor, Command(), "ReadOnly");
            }
            var invalidSensor = new FakeBackend { Temperature = float.NaN };
            await using (var monitor = new ServiceMonitor(invalidSensor))
            {
                await Reject(monitor, Command(), "ControlFailed");
                Check(invalidSensor.Writes == 0 && invalidSensor.Restores == 1, "invalid sensor restores before write");
            }
            var restoreFailure = new FakeBackend { FailRestore = true };
            await using (var monitor = new ServiceMonitor(restoreFailure))
            {
                await monitor.HandleAsync("device.execute", Wire.Element(Command()), default);
                var result = (ApplyResult)await monitor.HandleAsync("device.release", Wire.Element(new { }), default);
                Check(result.Status == "failed", "restore failure not success");
                await Reject(monitor, Command(), "ReadOnly");
                restoreFailure.FailRestore = false;
            }
            var absent = new FakeBackend { Available = false };
            await using (var monitor = new ServiceMonitor(absent))
            {
                var devices = (StandardDevice[])await monitor.HandleAsync("device.discover", Wire.Element(new { }), default);
                Check(!devices[0].Capabilities.Contains("fan.profile"), "unrecognized board read-only");
                await Reject(monitor, Command(), "ReadOnly");
            }
            Console.WriteLine($"Service monitor self-test: {count} assertions passed; no hardware accessed.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private sealed class FakeTime : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(TimeSpan value) => ticks += value.Ticks;
    }
    private sealed class FakeBackend : IMonitorBackend
    {
        public bool Available = true, FailWrite, FailRestore, Disposed;
        public float Temperature = 40, LastRadiator, LastCase;
        public int Opens, Writes, Begins, Restores;
        public bool CanControl => Available;
        public string Detail => "Fake hardware for tests only";
        public void Open() => Opens++;
        public MonitorSample[] Sample() => [new(ServiceMonitor.DeviceId, "cpu.temperature", "temperature", Temperature, "°C", DateTimeOffset.UtcNow),
            new(ServiceMonitor.DeviceId, "radiator.rpm", "fanRpm", 900, "RPM", DateTimeOffset.UtcNow), new(ServiceMonitor.DeviceId, "case.rpm", "fanRpm", 800, "RPM", DateTimeOffset.UtcNow)];
        public object LegacySnapshot() => new { };
        public void BeginControl() => Begins++;
        public void Apply(float radiator, float chassis)
        {
            Writes++; LastRadiator = radiator; LastCase = chassis;
            if (FailWrite) throw new InvalidOperationException("Injected partial control failure.");
        }
        public bool Restore() { Restores++; if (FailRestore) throw new InvalidOperationException("Injected restoration failure."); return Writes > 0; }
        public void Dispose() => Disposed = true;
    }
}
