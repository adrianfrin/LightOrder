namespace ControlCenter.Contracts;

public sealed record FanPoint(float Temperature, float Duty);
public sealed record FanProfile(string Mode, float RadiatorPercent, float CasePercent, FanPoint[] RadiatorCurve, FanPoint[] CaseCurve)
{
    public static FanProfile Silent => new("manual", 40, 25,
        [new(40,40), new(55,40), new(65,60), new(80,100)],
        [new(40,25), new(55,30), new(65,55), new(80,100)]);
    public void Validate()
    {
        _ = ProtocolValues.ParseFanMode(Mode);
        CheckDuty(RadiatorPercent, 40); CheckDuty(CasePercent, 25);
        ValidateCurve(RadiatorCurve, 40); ValidateCurve(CaseCurve, 25);
    }
    private static void CheckDuty(float duty, float minimum)
    {
        if (!float.IsFinite(duty) || duty < minimum || duty > 100) throw new ArgumentException($"风扇百分比必须在 {minimum}–100 之间");
    }
    private static void ValidateCurve(FanPoint[] points, float minimum)
    {
        if (points is null || points.Length is < 2 or > 12) throw new ArgumentException("每条曲线需要2–12个节点");
        for (int i=0;i<points.Length;i++)
        {
            var p=points[i] ?? throw new ArgumentException("曲线节点为空");
            CheckDuty(p.Duty,minimum);
            if (!float.IsFinite(p.Temperature) || p.Temperature < 0 || p.Temperature > 80 ||
                (i>0 && (p.Temperature <= points[i-1].Temperature || p.Duty < points[i-1].Duty)))
                throw new ArgumentException("温度必须递增且在0–80°C内，转速不能随温度升高而下降");
        }
        if (points[^1].Temperature != 80 || points[^1].Duty != 100) throw new ArgumentException("最后节点必须为80°C／100%");
    }
    public (float Radiator,float Case) Evaluate(float temperature)
    {
        Validate();
        if (!float.IsFinite(temperature) || temperature < 0 || temperature > 110) throw new ArgumentException("CPU温度无效");
        // Fixed duty remains fixed below the independent emergency threshold.
        float safety = temperature >= 80 ? 100 : 0;
        var requested = ProtocolValues.ParseFanMode(Mode) switch
        {
            FanControlMode.Manual => (RadiatorPercent, CasePercent),
            FanControlMode.Curve => (Interpolate(RadiatorCurve, temperature), Interpolate(CaseCurve, temperature)),
            _ => throw new InvalidOperationException("Unsupported fan control mode")
        };
        return (Math.Max(safety, requested.Item1), Math.Max(safety, requested.Item2));
    }
    private static float Interpolate(FanPoint[] points,float temperature)
    {
        if(temperature<=points[0].Temperature)return points[0].Duty;
        for(int i=1;i<points.Length;i++)
            if(temperature<=points[i].Temperature)
                return points[i-1].Duty+(points[i].Duty-points[i-1].Duty)*(temperature-points[i-1].Temperature)/(points[i].Temperature-points[i-1].Temperature);
        return points[^1].Duty;
    }
}
