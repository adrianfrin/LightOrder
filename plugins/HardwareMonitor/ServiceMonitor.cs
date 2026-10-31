using System.Text.Json;
using ControlCenter.Contracts;

// All hardware access, including background sampling and restoration, shares this gate.
sealed class ServiceMonitor : IAsyncDisposable
{
    internal const string DeviceId = "hardware-monitor";
    internal static readonly TimeSpan MaximumControlGap = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaximumTelemetryAge = TimeSpan.FromSeconds(3);
    internal const int TelemetryLimit = 64;
    private readonly IMonitorBackend backend;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly bool backgroundPolling;
    private Task? worker;
    private PowerNotifications? powerNotifications;
    private bool opened, disposed, needsRestore, suspended;
    private FanProfile profile = FanProfile.Silent;
    private readonly TimeProvider time;
    private long lastCycleTimestamp, controlStartTimestamp;
    private DateTimeOffset? startedUtc;
    private readonly FanController controller = new();
    private long lastControlTimestamp;
    private long samples;
    private MonitorState state = MonitorState.Idle;
    private string detail = "Monitor has not been opened.";
    private bool restorationVerified;
    private MonitorSample[] telemetry = [];
    private bool Active => state is MonitorState.Active or MonitorState.Resuming;
    private bool Faulted => state is MonitorState.Failed or MonitorState.RestoreFailed or MonitorState.Unavailable;
    internal ServiceMonitor(IMonitorBackend backend, TimeProvider? time = null, bool backgroundPolling = true)
    {
        this.backend = backend;
        this.time = time ?? TimeProvider.System;
        this.backgroundPolling = backgroundPolling;
        if (backgroundPolling)
        {
            try { powerNotifications = new PowerNotifications(HandlePowerTransition); }
            catch (Exception e) { Console.Error.WriteLine("Power notifications unavailable; monotonic-gap recovery remains active: " + e.Message); }
        }
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
                    if (suspended) throw new PluginException("ReadOnly", "System is suspending; fan defaults restored.");
                    if (!backend.CanControl || Faulted) throw new PluginException("ReadOnly", detail);
                    cancellationToken.ThrowIfCancellationRequested();
                    profile = next;
                    if (!Active)
                    {
                        state = MonitorState.Resuming;
                        controller.Reset();
                        samples = 0;
                        startedUtc = time.GetUtcNow();
                        lastCycleTimestamp = time.GetTimestamp();
                    }
                    try { SampleAndControl(cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    { Release("cancelled"); throw; }
                    catch (Exception e) { FailAndRestore(e); throw new PluginException("ControlFailed", detail); }
                    return new ApplyResult("applied", "Continuous fan control enabled; pump remains read-only.", Session());
                case StandardMethods.Discover:
                    EnsureOpen();
                    return Discover();
                case StandardMethods.Telemetry:
                    EnsureOpen();
                    // Do not present pre-suspend data as current even before the next timer tick.
                    return telemetry.Where(IsFresh).ToArray();
                case "monitor.snapshot":
                    EnsureOpen();
                    if (state == MonitorState.Unavailable) throw new PluginException("MonitorUnavailable", detail);
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
            telemetry = backend.Sample().Take(TelemetryLimit).ToArray();
            state = backend.CanControl ? MonitorState.Ready : MonitorState.ReadOnly;
            if (backgroundPolling) worker = Task.Run(PollAsync);
        }
        catch (Exception e) { detail = "Monitor unavailable: " + e.Message; state = MonitorState.Unavailable; }
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
                await PollOnceAsync(stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    // Same path as the timer; exposed internally for deterministic fake-clock tests.
    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (disposed || Faulted) return;
            SampleAndControl(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { Release("cancelled"); throw; }
        catch (Exception e) { FailAndRestore(e); }
        finally { gate.Release(); }
    }

    // Native power callbacks are synchronous so a delivered suspend notification attempts
    // restoration before returning. Serialization prevents racing sensor/control I/O.
    internal void HandlePowerTransition(bool suspending)
    {
        gate.Wait();
        try
        {
            if (disposed || Faulted) return;
            suspended = suspending;
            if (!opened) return;
            telemetry = [];
            if (!Active) return;
            if (suspending) ResumeAfterGap();
            else
            {
                // Resume may arrive without a suspend notification.
                if (needsRestore) ResumeAfterGap();
                controller.Reset();
                lastCycleTimestamp = time.GetTimestamp();
            }
        }
        catch (Exception e) { FailAndRestore(e); }
        finally { gate.Release(); }
    }

    private void ResumeAfterGap()
    {
        var result = Release("monotonic-time-gap");
        if (result.Status == "failed") throw new InvalidOperationException(detail);
        state = MonitorState.Resuming;
        controller.Reset();
        telemetry = [];
        detail = "Control gap detected; defaults restored. Waiting for fresh telemetry before restart.";
    }

    private void SampleAndControl(CancellationToken cancellationToken)
    {
        if (suspended) return;
        if (Active && time.GetElapsedTime(lastCycleTimestamp) > MaximumControlGap) ResumeAfterGap();
        var sampleStarted = time.GetTimestamp();
        var values = backend.Sample();
        cancellationToken.ThrowIfCancellationRequested();
        // A slow sensor read may itself span a suspend. Never apply its result.
        if (Active && time.GetElapsedTime(sampleStarted) > MaximumControlGap)
        {
            ResumeAfterGap();
            lastCycleTimestamp = time.GetTimestamp();
            return;
        }
        telemetry = values.Take(TelemetryLimit).ToArray();
        if (!Active) return;
        MonitorSample Required(string name)
        {
            var matches = values.Where(s => s.DeviceId == DeviceId && s.Name == name).Take(2).ToArray();
            if (matches.Length != 1 || !IsFresh(matches[0]))
                throw new InvalidOperationException(name + " missing, ambiguous or stale.");
            return matches[0];
        }
        var raw = Required("cpu.temperature").Value;
        var radiator = Required("radiator.rpm").Value;
        var chassis = Required("case.rpm").Value;
        if (!FanSessionPolicy.ValidTemperature(raw)) throw new InvalidOperationException("CPU temperature unavailable or out of range.");
        if (!FanSessionPolicy.ValidRpm(radiator) || !FanSessionPolicy.ValidRpm(chassis)) throw new InvalidOperationException("Controlled fan tachometer unavailable.");
        if (state == MonitorState.Active && time.GetElapsedTime(controlStartTimestamp) >= TimeSpan.FromSeconds(8) &&
            (!FanSessionPolicy.RunningRpm(radiator) || !FanSessionPolicy.RunningRpm(chassis)))
            throw new InvalidOperationException("Controlled fan tachometer below 200 RPM.");
        if (state == MonitorState.Resuming)
        {
            backend.BeginControl();
            needsRestore = true;
            restorationVerified = false;
            controlStartTimestamp = time.GetTimestamp();
        }
        if (time.GetElapsedTime(sampleStarted) > MaximumControlGap ||
            !IsFresh(Required("cpu.temperature")) || !IsFresh(Required("radiator.rpm")) || !IsFresh(Required("case.rpm")))
        {
            ResumeAfterGap();
            lastCycleTimestamp = time.GetTimestamp();
            return;
        }
        var duties = controller.Next(profile, raw!.Value, state == MonitorState.Resuming
            ? TimeSpan.Zero : time.GetElapsedTime(lastControlTimestamp));
        cancellationToken.ThrowIfCancellationRequested();
        backend.Apply(duties.Radiator, duties.Case);
        lastControlTimestamp = time.GetTimestamp();
        // Cancellation arriving during synchronous hardware I/O must still restore.
        cancellationToken.ThrowIfCancellationRequested();
        if (time.GetElapsedTime(sampleStarted) > MaximumControlGap)
        {
            ResumeAfterGap();
            lastCycleTimestamp = time.GetTimestamp();
            return;
        }
        if (samples < long.MaxValue) samples++;
        lastCycleTimestamp = time.GetTimestamp();
        state = MonitorState.Active;
        detail = "Continuous fan control active; pump read-only.";
    }

    private bool IsFresh(MonitorSample sample)
    {
        var age = time.GetUtcNow() - sample.Timestamp;
        return age >= TimeSpan.Zero && age <= MaximumTelemetryAge;
    }

    private object Session() => new { active = Active, startedUtc, expiresUtc = (DateTimeOffset?)null,
        mode = "continuous", status = state.ToWire(), restorationVerified, sampleCount = samples, sessionLimitSeconds = (int?)null };

    private StandardDevice[] Discover() => [new(DeviceId, "Cooling and CPU sensors", "monitor",
        backend.CanControl && !Faulted ? ["sensor.temperature", "sensor.fanRpm", StandardCapabilities.FanProfile, "device.release"] : ["sensor.temperature", "sensor.fanRpm", "device.release"],
        state.ToWire(), detail, Wire.Element(new
        {
            schemaVersion = 1, supportsRestore = true, readOnly = !backend.CanControl || Faulted,
            channels = new[] {
                new { id = "radiator", name = "Radiator", readOnly = !backend.CanControl || Faulted, minPercent = 40, maxPercent = 100, telemetryName = (string?)"radiator.rpm" },
                new { id = "case", name = "Case", readOnly = !backend.CanControl || Faulted, minPercent = 25, maxPercent = 100, telemetryName = (string?)"case.rpm" },
                new { id = "pump", name = "Pump", readOnly = true, minPercent = 100, maxPercent = 100, telemetryName = (string?)"pump.rpm" }
            },
            profile, session = Session(), sessionLimitSeconds = (int?)null, controlMode = "continuous",
            crashRecoveryGuaranteed = false, telemetryLimitSamples = TelemetryLimit, pollIntervalSeconds = 1,
            maximumControlGapSeconds = MaximumControlGap.TotalSeconds, maximumTelemetryAgeSeconds = MaximumTelemetryAge.TotalSeconds,
            regulation = controller.Status,
            regulationPolicy = new { filterSeconds = FanController.FilterSeconds, temperatureDeadband = FanController.TemperatureDeadband,
                risePercentPerSecond = FanController.RisePercentPerSecond, fallPercentPerSecond = FanController.FallPercentPerSecond,
                emergencyTemperature = FanController.EmergencyTemperature, recoveryTemperature = FanController.RecoveryTemperature,
                recoverySeconds = FanController.RecoverySeconds, riseHoldSeconds = FanController.RiseHoldSeconds },
            parameterSchema = new { capability = StandardCapabilities.FanProfile, model = "FanProfile", modes = new[] { "manual", "curve" },
                curveMinPoints = 2, curveMaxPoints = 12, terminalTemperature = 80, terminalDuty = 100 }
        }))];

    private ApplyResult Release(string reason)
    {
        var wasFaulted = Faulted;
        if (needsRestore)
        {
            try
            {
                restorationVerified = backend.Restore();
                // False means no writes occurred. A backend must throw for unverified writes.
                needsRestore = false;
            }
            catch (Exception e)
            {
                state = MonitorState.RestoreFailed;
                detail = "Restoration failed or could not be verified: " + e.Message;
                return new ApplyResult("failed", detail, Session());
            }
        }
        // A sensor/control failure stays latched even after an explicit release.
        state = wasFaulted ? MonitorState.Failed : MonitorState.Released;
        detail = "Fan control released: " + reason + (restorationVerified ? "; baseline registers verified." : "; no controls changed.");
        return new ApplyResult("released", detail, Session());
    }

    private void FailAndRestore(Exception e)
    {
        var failure = e.Message;
        if (needsRestore) Release("sensor-or-control-error");
        telemetry = [];
        if (state != MonitorState.RestoreFailed) state = MonitorState.Failed;
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
        finally
        {
            gate.Release();
            // Unregister outside the gate: an in-flight native callback may be waiting on it.
            powerNotifications?.Dispose();
            powerNotifications = null;
        }
    }
}

// One conversion table for the finite service state; protocol strings stay compatible.
enum MonitorState { Idle, Ready, ReadOnly, Active, Resuming, Released, Failed, RestoreFailed, Unavailable }
static class MonitorStateWire
{
    public static string ToWire(this MonitorState state) => state switch
    {
        MonitorState.Idle => "idle", MonitorState.Ready => "ready", MonitorState.ReadOnly => "read-only",
        MonitorState.Active => "active", MonitorState.Resuming => "resuming", MonitorState.Released => "released",
        MonitorState.Failed => "failed", MonitorState.RestoreFailed => "restore-failed", MonitorState.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
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
    // True only after default commands AND baseline register readback succeed.
    // False only if no write was attempted. Failed verification must throw.
    bool Restore();
}
