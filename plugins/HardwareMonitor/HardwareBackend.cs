using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

// Only the already validated board and LHM channels may be controlled. No raw register writes.
sealed class HardwareBackend : IMonitorBackend
{
    private Computer? computer;
    private IHardware? chip, cpu;
    private IControl? radiator, chassis;
    private Dictionary<string, byte>? baseline;
    private ControllerLease? owner;
    private bool touched;
    public bool CanControl { get; private set; }
    public string Detail { get; private set; } = "Read-only hardware monitoring.";

    public void Open()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Start the plugin under the installed administrator/SYSTEM service for hardware monitoring.");
        computer = new Computer { IsCpuEnabled = true, IsMotherboardEnabled = true };
        computer.Open();
        Update();
        var boards = computer.Hardware.Where(h => h.HardwareType == HardwareType.Motherboard).ToArray();
        cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (boards.Length != 1 || !boards[0].Name.Contains("B650M AORUS ELITE AX ICE", StringComparison.OrdinalIgnoreCase))
        { Detail = "Unrecognized board; actual sensors available read-only. Fan channel mapping is not inferred."; return; }
        chip = boards[0].SubHardware.SingleOrDefault(h => h.Identifier.ToString() == "/lpc/it8689e/0");
        radiator = chip?.Sensors.SingleOrDefault(s => s.Identifier.ToString() == "/lpc/it8689e/0/control/0")?.Control;
        chassis = chip?.Sensors.SingleOrDefault(s => s.Identifier.ToString() == "/lpc/it8689e/0/control/3")?.Control;
        try
        {
            CanControl = cpu is not null && radiator is not null && chassis is not null && FanSessionPolicy.ValidBaseline(FanSessionPolicy.ReadRegisters(computer.GetReport()));
            Detail = CanControl ? "Validated radiator/case controls; pump read-only. Sessions stop and verify restoration after at most 30 minutes." : "Validated controls or baseline unavailable; read-only.";
        }
        catch (Exception e) { Detail = "Baseline unavailable; read-only: " + e.Message; }
    }

    private IEnumerable<IHardware> Hardware()
    {
        static IEnumerable<IHardware> Visit(IHardware h)
        {
            yield return h;
            foreach (var child in h.SubHardware) foreach (var descendant in Visit(child)) yield return descendant;
        }
        return computer is null ? [] : computer.Hardware.SelectMany(Visit);
    }
    private void Update() { foreach (var hw in Hardware()) hw.Update(); }
    private float? Temperature => (cpu?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Tctl"))
        ?? cpu?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Package")))?.Value;

    public MonitorSample[] Sample()
    {
        Update();
        var timestamp = DateTimeOffset.UtcNow;
        var values = new List<MonitorSample>();
        // Reserve canonical values first so a large sensor collection cannot evict control inputs.
        if (cpu is not null)
        {
            values.Add(new(ServiceMonitor.DeviceId, "cpu.temperature", "temperature", Finite(Temperature), "°C", timestamp));
            values.Add(new(ServiceMonitor.DeviceId, "cpu.load", "load", Finite(cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Total"))?.Value), "%", timestamp));
        }
        if (chip is not null)
            foreach (var mapping in new[] { (0, "radiator.rpm"), (3, "case.rpm"), (4, "pump.rpm") })
                values.Add(new(ServiceMonitor.DeviceId, mapping.Item2, "fanRpm", Finite(chip.Sensors.FirstOrDefault(s => s.Identifier.ToString() == $"/lpc/it8689e/0/fan/{mapping.Item1}")?.Value), "RPM", timestamp));
        foreach (var hardware in Hardware())
            foreach (var sensor in hardware.Sensors.Where(s => s.SensorType is SensorType.Temperature or SensorType.Fan or SensorType.Load))
            {
                if (values.Count >= 64) break;
                var type = sensor.SensorType switch { SensorType.Temperature => "temperature", SensorType.Fan => "fanRpm", _ => "load" };
                values.Add(new(ServiceMonitor.DeviceId, sensor.Identifier.ToString(), type, Finite(sensor.Value), type == "temperature" ? "°C" : type == "fanRpm" ? "RPM" : "%", timestamp));
            }
        return values.ToArray();
    }
    private static float? Finite(float? value) => value is { } v && float.IsFinite(v) ? v : null;
    public object LegacySnapshot()
    {
        Update();
        return new { timestamp = DateTimeOffset.UtcNow, administrator = true, readOnly = !touched,
            hardware = Hardware().Select(hw => new { id = hw.Identifier.ToString(), name = hw.Name, type = hw.HardwareType.ToString(),
                sensors = hw.Sensors.Where(s => s.SensorType is SensorType.Temperature or SensorType.Fan or SensorType.Control)
                    .Select(s => new { id = s.Identifier.ToString(), name = s.Name, type = s.SensorType.ToString(), value = Finite(s.Value), controlAvailable = s.Control != null,
                        controlMode = s.Control?.ControlMode.ToString(), min = s.Control?.MinSoftwareValue, max = s.Control?.MaxSoftwareValue }).ToArray() }).ToArray(),
            report = computer?.GetReport() };
    }

    public void BeginControl()
    {
        if (!CanControl) throw new InvalidOperationException("Fan control is unavailable.");
        owner = new ControllerLease();
        try
        {
            baseline = FanSessionPolicy.ReadRegisters(computer!.GetReport());
            if (!FanSessionPolicy.ValidBaseline(baseline)) throw new InvalidOperationException("Fan baseline no longer valid.");
        }
        catch { owner.Dispose(); owner = null; throw; }
    }
    public void Apply(float radiatorDuty, float caseDuty)
    {
        touched = true; // A setter may change hardware before throwing.
        radiator!.SetSoftware(radiatorDuty);
        chassis!.SetSoftware(caseDuty);
    }
    public bool Restore()
    {
        if (!touched) { owner?.Dispose(); owner = null; return false; }
        var failures = new List<string>();
        try { radiator!.SetDefault(); } catch (Exception e) { failures.Add("radiator: " + e.Message); }
        try { chassis!.SetDefault(); } catch (Exception e) { failures.Add("case: " + e.Message); }
        try
        {
            var after = FanSessionPolicy.ReadRegisters(computer!.GetReport());
            if (baseline is null || !FanSessionPolicy.Restored(baseline, after)) failures.Add("Bank 0 register readback differs from captured baseline.");
        }
        catch (Exception e) { failures.Add("Register verification: " + e.Message); }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("; ", failures));
        touched = false;
        owner?.Dispose(); owner = null;
        return true;
    }
    public void Dispose()
    {
        try { if (touched) Restore(); }
        finally { try { computer?.Close(); } finally { owner?.Dispose(); owner = null; } }
    }
}
