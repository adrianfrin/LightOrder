using ControlCenter.Contracts;

// Feed-forward fan curves: filter brief CPU spikes, add hysteresis, and limit duty
// changes. Raw temperature bypasses every comfort filter for thermal protection.
sealed class FanController
{
    internal const float EmergencyTemperature = 80;
    internal const float RecoveryTemperature = 75;
    internal const double RecoverySeconds = 10;
    internal const double FilterSeconds = 5;
    internal const float TemperatureDeadband = 2;
    internal const float RisePercentPerSecond = 5;
    internal const float FallPercentPerSecond = 2;
    internal const double RiseHoldSeconds = 3;
    private bool initialized, emergency;
    private double coolSeconds, riseSeconds;
    private float filtered, curveTemperature, radiator, chassis;
    public object Status => new { filteredTemperature = initialized ? (float?)filtered : null,
        curveTemperature = initialized ? (float?)curveTemperature : null,
        radiatorPercent = initialized ? (float?)radiator : null, casePercent = initialized ? (float?)chassis : null,
        thermalProtection = emergency, recoverySeconds = coolSeconds };

    public void Reset()
    {
        initialized = emergency = false;
        coolSeconds = riseSeconds = 0;
    }

    public (float Radiator, float Case) Next(FanProfile profile, float raw, TimeSpan elapsed)
    {
        if (!FanSessionPolicy.ValidTemperature(raw)) throw new ArgumentOutOfRangeException(nameof(raw));
        var seconds = Math.Clamp(elapsed.TotalSeconds, 0, ServiceMonitor.MaximumControlGap.TotalSeconds);
        var first = !initialized;
        if (first) filtered = curveTemperature = raw;
        else
        {
            filtered += (raw - filtered) * (float)(1 - Math.Exp(-seconds / FilterSeconds));
            if (Math.Abs(raw - filtered) < .1f) filtered = curveTemperature = raw;
            else if (Math.Abs(filtered - curveTemperature) >= TemperatureDeadband) curveTemperature = filtered;
        }
        initialized = true;
        if (raw >= EmergencyTemperature)
        {
            emergency = true;
            coolSeconds = 0;
        }
        else if (emergency)
        {
            coolSeconds = raw <= RecoveryTemperature ? coolSeconds + seconds : 0;
            if (coolSeconds >= RecoverySeconds) emergency = false;
        }
        if (emergency) return (radiator = 100, chassis = 100);
        // Filtered temperature may remain above 80°C during recovery. The raw
        // safety latch above owns protection; use the curve only for comfort here.
        var target = profile.Evaluate(Math.Min(curveTemperature, 79.9f));
        // Fixed duty really stays fixed below the independent emergency threshold.
        // After emergency recovery, use the same gentle descent as curve control.
        if (first || (ProtocolValues.ParseFanMode(profile.Mode) == FanControlMode.Manual && coolSeconds == 0))
            return (radiator = target.Radiator, chassis = target.Case);
        riseSeconds = target.Radiator > radiator || target.Case > chassis ? riseSeconds + seconds : 0;
        radiator = Move(radiator, target.Radiator > radiator && riseSeconds < RiseHoldSeconds ? radiator : target.Radiator, seconds);
        chassis = Move(chassis, target.Case > chassis && riseSeconds < RiseHoldSeconds ? chassis : target.Case, seconds);
        return (radiator, chassis);
    }

    private static float Move(float current, float target, double seconds) =>
        current + Math.Clamp(target - current, -(float)seconds * FallPercentPerSecond, (float)seconds * RisePercentPerSecond);
}
