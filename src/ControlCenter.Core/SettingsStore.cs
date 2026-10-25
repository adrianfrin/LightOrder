using System.Text.Json;
using ControlCenter.Contracts;

namespace ControlCenter.Core;

public sealed record LightingProfile(string Name, string Color, double Brightness);
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string Color { get; set; } = "#4D9FFF";
    public double Brightness { get; set; } = 0.35;
    public List<LightingProfile> Profiles { get; set; } = [];
}
public static class SettingsStore
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IndependentControlCenter");
    public static async Task<AppSettings> LoadAsync()
    {
        var file = Path.Combine(Folder, "settings.json");
        if (!File.Exists(file)) return new();
        var value = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(file), Wire.Json) ?? throw new InvalidDataException("配置文件为空");
        if (value.SchemaVersion != 1) throw new InvalidDataException("配置版本不受支持，未覆盖原文件");
        ValidateColor(value.Color);
        ValidateBrightness(value.Brightness);
        if (value.Profiles is null || value.Profiles.Count > 100) throw new InvalidDataException("场景数量无效");
        foreach (var profile in value.Profiles) { ValidateColor(profile.Color); ValidateBrightness(profile.Brightness); }
        return value;
    }
    public static async Task SaveAsync(AppSettings settings)
    {
        ValidateColor(settings.Color); ValidateBrightness(settings.Brightness);
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, "settings.json");
        // Never overwrite a future schema even when loading it failed earlier.
        if (File.Exists(file))
        {
            using var old = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            if (old.RootElement.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("配置版本不兼容，未保存");
        }
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
            File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void ValidateColor(string value)
    {
        if (value is null || value.Length != 7 || value[0] != '#' || !uint.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _))
            throw new ArgumentException("颜色格式应为 #RRGGBB，例如 #4D9FFF");
    }
    public static void ValidateBrightness(double value)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1) throw new ArgumentException("亮度应在 0 到 100% 之间");
    }
}
