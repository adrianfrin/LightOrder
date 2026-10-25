using LibreHardwareMonitor.Hardware;

static class FanCalibration
{
    public static async Task<object> RunAsync(int channel)
    {
        if (channel is not (0 or 3)) throw new ArgumentException("Only historical radiator/case candidate channels 0 and 3 are eligible; pump excluded.");
        using var serviceLease = new ControllerLease();
        using var owned = new FileStream(Path.Combine(Path.GetTempPath(), "IndependentControlCenter.Fan.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var computer = new Computer { IsCpuEnabled = true, IsMotherboardEnabled = true };
        computer.Open();
        IControl? control = null;
        var samples = new List<object>();
        string beforeReport = "", afterReport = "";
        try
        {
            var board = computer.Hardware.Single(h => h.HardwareType == HardwareType.Motherboard);
            if (!board.Name.Contains("B650M AORUS ELITE AX ICE", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Board does not match validated target");
            var chip = board.SubHardware.Single(h => h.Identifier.ToString() == "/lpc/it8689e/0");
            var cpu = computer.Hardware.Single(h => h.HardwareType == HardwareType.Cpu);
            void Sample(string phase)
            {
                chip.Update(); cpu.Update();
                var temperature = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Tctl"))?.Value;
                if (temperature is null || temperature > 85 || temperature < 0) throw new InvalidOperationException("CPU temperature unavailable/out of calibration range");
                samples.Add(new { time = DateTimeOffset.UtcNow, phase, cpu = temperature, fans = chip.Sensors.Where(s => s.SensorType == SensorType.Fan).Select(s => new { id = s.Identifier.ToString(), rpm = s.Value }).ToArray() });
            }
            Sample("before"); beforeReport = computer.GetReport();
            // The upstream default-mode restoration for channels 0..2 relies on bit 0..2.
            // This target's read baseline has all three output-enable bits set; refuse otherwise.
            var line = beforeReport.Split('\n').FirstOrDefault(s => s.TrimStart().StartsWith("10   "));
            if (line is null || (Convert.ToByte(line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[4], 16) & 7) != 7)
                throw new InvalidOperationException("Unverified initial fan-output register; calibration refused");
            control = chip.Sensors.Single(s => s.Identifier.ToString() == $"/lpc/it8689e/0/control/{channel}").Control
                ?? throw new InvalidOperationException("No writable control");
            try
            {
                control.SetSoftware(100);
                for (var i = 0; i < 5; i++) { await Task.Delay(1000); Sample("100-percent"); }
            }
            finally { control.SetDefault(); }
            for (var i = 0; i < 5; i++) { await Task.Delay(1000); Sample("restored"); }
            afterReport = computer.GetReport();
            return new { status = "sampled", channel, requestedDuty = 100, restoredDefaultCommand = true, samples, beforeReport, afterReport, physicalHeaderMapping = "historical-candidate-requires-response-analysis", pumpWritten = false };
        }
        finally { computer.Close(); }
    }
}
