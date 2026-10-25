using System.Globalization;

// Pure policy and report parsing: no hardware access or register writes.
static class FanSessionPolicy
{
    public const int MaximumSessionMinutes = 30;
    public const int MaximumSamples = MaximumSessionMinutes * 60;
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(MaximumSessionMinutes);
    // IT87XX.cs v0.9.6: FAN_MAIN_CTRL_REG, PWM_CTRL[0/3], PWM_CTRL_EXT[0/3].
    private static readonly byte[] RelevantRegisters = [0x13, 0x15, 0x63, 0x7B, 0x7F];

    public static bool ValidTemperature(float? value) => value is { } v && float.IsFinite(v) && v is >= 0 and <= 110;
    public static bool ValidRpm(float? value) => value is { } v && float.IsFinite(v) && v >= 0;
    public static bool RunningRpm(float? value) => ValidRpm(value) && value >= 200;

    public static (float Temperature, float Duty) Next(float raw, float previous, string profile)
    {
        if (!ValidTemperature(raw) || !ValidTemperature(previous)) throw new ArgumentOutOfRangeException(nameof(raw));
        var floor = profile switch { "quiet" => 50f, "balanced" => 65f, _ => throw new ArgumentException("Unknown fan profile", nameof(profile)) };
        var temperature = Math.Max(raw, previous - 2);
        return (temperature, Math.Clamp(floor + (temperature - 50) * (100 - floor) / 30, floor, 100));
    }

    public static Dictionary<string, byte> ReadRegisters(string report)
    {
        var lines = report.Replace("\r", "").Split('\n');
        Dictionary<byte, byte>? bank = null;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() != "LPC IT87XX") continue;
            var end = i + 1;
            while (end < lines.Length && !lines[end].TrimStart().StartsWith("---", StringComparison.Ordinal) && lines[end].Trim() != "LPC IT87XX") end++;
            if (!lines[(i + 1)..end].Any(s => s.Trim() == "Chip ID: 0x8689")) continue;
            if (bank is not null) throw new InvalidOperationException("Ambiguous IT8689E register report");
            bank = new Dictionary<byte, byte>();
            var inBank = false;
            for (var j = i + 1; j < end; j++)
            {
                var line = lines[j].Trim();
                if (line.StartsWith("Environment Controller Registers Bank ", StringComparison.Ordinal))
                {
                    if (inBank) break;
                    inBank = line == "Environment Controller Registers Bank 0";
                    continue;
                }
                if (!inBank) continue;
                var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0 || words.Length == 16) continue; // blank or column header
                if (words.Length != 17 || !byte.TryParse(words[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var row) || row % 16 != 0)
                    throw new InvalidOperationException("Malformed bank 0 register row");
                for (var column = 0; column < 16; column++)
                {
                    if (words[column + 1].Length != 2 || !byte.TryParse(words[column + 1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) || !bank.TryAdd((byte)(row + column), value))
                        throw new InvalidOperationException("Invalid or duplicate bank 0 register value");
                }
            }
        }
        if (bank is null || RelevantRegisters.Any(r => !bank.ContainsKey(r))) throw new InvalidOperationException("Required IT8689E bank 0 registers unavailable");
        return RelevantRegisters.ToDictionary(r => $"0x{r:X2}", r => bank[r]);
    }

    public static bool ValidBaseline(IReadOnlyDictionary<string, byte> registers) => registers.TryGetValue("0x13", out var value) && (value & 7) == 7;
    public static bool Restored(IReadOnlyDictionary<string, byte> before, IReadOnlyDictionary<string, byte> after) =>
        RelevantRegisters.All(r => before.TryGetValue($"0x{r:X2}", out var a) && after.TryGetValue($"0x{r:X2}", out var b) && a == b);
}
