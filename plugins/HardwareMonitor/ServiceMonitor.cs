using System.Diagnostics;
using System.Text.Json;
using ControlCenter.Contracts;

// All hardware access, including background sampling and restoration, shares this gate.
sealed class ServiceMonitor : IAsyncDisposable
{
    internal const string DeviceId = "hardware-monitor";
    private readonly IMonitorBackend backend;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private Task? worker;
    private bool opened, disposed, active, needsRestore, faulted;
    private FanProfile profile = FanProfile.Silent;
    private readonly TimeProvider time;
    private long startTimestamp;
    private TimeSpan Elapsed => time.GetElapsedTime(startTimestamp);
    private DateTimeOffset? startedUtc;
    private float previousTemperature = 100;
    private int samples;
    private string status = "idle", detail = "Monitor has not been opened.";
    private bool restorationVerified;
    private MonitorSample[] telemetry = [];
    internal ServiceMonitor(IMonitorBackend backend, TimeProvider? time = null)
    {
        this.backend = backend;
        this.time = time ?? TimeProvider.System;
    }

    public async Task<object> HandleAsync(string method, JsonElement args, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            switch (method)
            {
                case StandardMethods.Release:
                    ValidateDevice(args);
                    return Release("requested");
                case StandardMethods.Execute:
                    var command = Wire.Read<DeviceCommand>(args);
                    if (command.DeviceId != DeviceId) throw new PluginException("DeviceNotFound", "Unknown monitor device.");
                    if (command.Capability == StandardMethods.Release) return Release("requested");
                    if (command.Capability != StandardCapabilities.FanProfile) throw new PluginException("UnsupportedCapability", "Only fan.profile is writable.");
                    FanProfile next;
                    try { next = Wire.Read<FanProfile>(command.Parameters); next.Validate(); }
                    catch (Exception e) when (e is ArgumentException or JsonException) { throw new PluginException("InvalidArgument", e.Message); }
                    EnsureOpen();
                    if (!backend.CanControl || faulted) throw new PluginException("ReadOnly", detail);
                    if (active && (Elapsed >= FanSessionPolicy.MaximumDuration || samples >= FanSessionPolicy.MaximumSamples))
                    {
                        Release("duration-limit");
                        throw new PluginException("SessionExpired", "The 30-minute session ended. Review restoration before starting a new session.");
                    }
                    if (!active)
                    {
                        backend.BeginControl();
                        needsRestore = true;
                        active = true;
                        restorationVerified = false;
                        previousTemperature = 100;
                        samples = 0;
                        startedUtc = time.GetUtcNow();
                        startTimestamp = time.GetTimestamp();
                    }
                    profile = next;
                    try { SampleAndControl(); }
                    catch (Exception e) { FailAndRestore(e); throw new PluginException("ControlFailed", detail); }
                    return new ApplyResult("applied", "Bounded fan session active; pump remains read-only.", Session());
                case StandardMethods.Discover:
                    EnsureOpen();
                    return Discover();
                case StandardMethods.Telemetry:
                    EnsureOpen();
                    return telemetry;
                case "monitor.snapshot":
                    EnsureOpen();
                    if (status == "unavailable") throw new PluginException("MonitorUnavailable", detail);
                    return backend.LegacySnapshot();
                default: throw new PluginException("MethodNotFound", "Unknown monitor method.");
            }
        }
        finally { gate.Release(); }
    }

    private void EnsureOpen()
    {
        if (opened) return;
        opened = true;
        try
        {
            backend.Open();
            detail = backend.Detail;
            telemetry = backend.Sample();
            status = backend.CanControl ? "ready" : "read-only";
            worker = Task.Run(PollAsync);
        }
        catch (Exception e) { faulted = true; detail = "Monitor unavailable: " + e.Message; status = "unavailable"; }
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                await gate.WaitAsync(stop.Token);
                try
                {
                    if (active && (Elapsed >= FanSessionPolicy.MaximumDuration || samples >= FanSessionPolicy.MaximumSamples))
                        Release("duration-limit");
                    SampleAndControl();
                }
                catch (Exception e) { FailAndRestore(e); return; }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private void SampleAndControl()
    {
        telemetry = backend.Sample();
        if (!active) return;
        var raw = telemetry.FirstOrDefault(s => s.Name == "cpu.temperature")?.Value;
        var radiator = telemetry.FirstOrDefault(s => s.Name == "radiator.rpm")?.Value;
        var chassis = telemetry.FirstOrDefault(s => s.Name == "case.rpm")?.Value;
        if (!FanSessionPolicy.ValidTemperature(raw)) throw new InvalidOperationException("CPU temperature unavailable or out of range.");
        if (!FanSessionPolicy.ValidRpm(radiator) || !FanSessionPolicy.ValidRpm(chassis)) throw new InvalidOperationException("Controlled fan tachometer unavailable.");
        if (Elapsed >= TimeSpan.FromSeconds(8) && (!FanSessionPolicy.RunningRpm(radiator) || !FanSessionPolicy.RunningRpm(chassis)))
            throw new InvalidOperationException("Controlled fan tachometer below 200 RPM.");
        previousTemperature = Math.Max(raw!.Value, previousTemperature - 2);
        var duties = profile.Evaluate(previousTemperature);
        // Slow I/O must not extend a session past its safety budget.
        if (Elapsed >= FanSessionPolicy.MaximumDuration) { Release("duration-limit"); return; }
        backend.Apply(duties.Radiator, duties.Case);
        samples++;
        status = "active";
    }

    private object Session() => new { active, startedUtc, expiresUtc = startedUtc?.Add(FanSessionPolicy.MaximumDuration), status,
        restorationVerified, sampleCount = samples, sessionLimitSeconds = (int)FanSessionPolicy.MaximumDuration.TotalSeconds };

    private StandardDevice[] Discover() => [new(DeviceId, "Cooling and CPU sensors", "monitor",
        backend.CanControl && !faulted ? ["sensor.temperature", "sensor.fanRpm", StandardCapabilities.FanProfile, "device.release"] : ["sensor.temperature", "sensor.fanRpm", "device.release"],
        status, detail, Wire.Element(new
        {
            schemaVersion = 1, supportsRestore = true, readOnly = !backend.CanControl || faulted,
            channels = new[] {
                new { id = "radiator", name = "Radiator", readOnly = !backend.CanControl || faulted, minPercent = 40, maxPercent = 100, telemetryName = (string?)"radiator.rpm" },
                new { id = "case", name = "Case", readOnly = !backend.CanControl || faulted, minPercent = 25, maxPercent = 100, telemetryName = (string?)"case.rpm" },
                new { id = "pump", name = "Pump", readOnly = true, minPercent = 100, maxPercent = 100, telemetryName = (string?)"pump.rpm" }
            },
            profile, session = Session(), sessionLimitSeconds = (int)FanSessionPolicy.MaximumDuration.TotalSeconds,
            crashRecoveryGuaranteed = false, telemetryLimitSamples = 64, pollIntervalSeconds = 1,
            parameterSchema = new { capability = StandardCapabilities.FanProfile, model = "FanProfile", modes = new[] { "manual", "curve" },
                curveMinPoints = 2, curveMaxPoints = 12, terminalTemperature = 80, terminalDuty = 100 }
        }))];

    private ApplyResult Release(string reason)
    {
        active = false;
        if (needsRestore)
        {
            try
            {
                restorationVerified = backend.Restore();
                needsRestore = false;
            }
            catch (Exception e)
            {
                faulted = true;
                status = "restore-failed";
                detail = "Restoration failed or could not be verified: " + e.Message;
                return new ApplyResult("failed", detail, Session());
            }
        }
        status = "released";
        detail = "Fan control released: " + reason + (restorationVerified ? "; baseline registers verified." : "; no controls changed.");
        return new ApplyResult("released", detail, Session());
    }

    private void FailAndRestore(Exception e)
    {
        var failure = e.Message;
        if (needsRestore) Release("sensor-or-control-error");
        telemetry = []; // Never return stale values as current readings.
        faulted = true;
        if (status != "restore-failed") status = "failed";
        detail = failure + "; " + detail;
        Console.Error.WriteLine(detail);
    }

    private static void ValidateDevice(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("deviceId", out var id) && id.GetString() != DeviceId)
            throw new PluginException("DeviceNotFound", "Unknown monitor device.");
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (worker is not null) await worker;
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            var result = Release("shutdown");
            if (result.Status == "failed") Console.Error.WriteLine(JsonSerializer.Serialize(result, Wire.Json));
            backend.Dispose();
        }
        finally { gate.Release(); }
    }
}

sealed record MonitorSample(string DeviceId, string Name, string Type, float? Value, string Unit, DateTimeOffset Timestamp);
interface IMonitorBackend : IDisposable
{
    bool CanControl { get; }
    string Detail { get; }
    void Open();
    MonitorSample[] Sample();
    object LegacySnapshot();
    void BeginControl();
    void Apply(float radiator, float chassis);
    bool Restore();
}
