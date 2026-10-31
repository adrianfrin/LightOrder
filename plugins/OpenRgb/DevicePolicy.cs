using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlCenter.Contracts;

namespace ControlCenter.OpenRgb;

// Every extra adapter names one installed detector and one exact SDK identity.
// Detector names authorize probing; identities authorize subsequent SDK writes.
internal sealed record DeviceRule
{
    public uint Type { get; init; }
    public string Detector { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Location { get; init; }
    public string? Serial { get; init; }
    public string? Vendor { get; init; }
    public string? Description { get; init; }
    public bool Matches(RgbDevice d) => Type == d.Type && Name == d.Name
        && (Location == null || Location == d.Location) && (Serial == null || Serial == d.Serial)
        && (Vendor == null || Vendor == d.Vendor) && (Description == null || Description == d.Description);
}

internal static class DevicePolicy
{
    public const string MemoryDetector = "ENE SMBus DRAM";
    // OpenRGB device_type values, independent of vendor-specific HID product types.
    public static string Kind(uint type) => type switch
    {
        0 => "motherboard", 1 => "memory", 2 => "gpu", 3 => "cooler", 4 => "led-strip",
        5 => "keyboard", 6 => "mouse", 7 => "mousemat", 8 => "headset", 9 => "headset-stand",
        10 => "gamepad", 11 => "light", 12 => "speaker", 13 => "virtual", 14 => "storage",
        15 => "case", 16 => "microphone", 17 => "accessory", 18 => "keypad", 19 => "laptop",
        20 => "monitor", _ => "unknown"
    };
    public static void Validate(BackendSettings settings)
    {
        if (settings.AdditionalDevices == null || settings.AdditionalDevices.Length > 128)
            throw new PluginException("ConfigurationError", "additionalDevices must be an array of at most 128 exact device rules");
        foreach (var rule in settings.AdditionalDevices)
        {
            if (rule == null || Kind(rule.Type) is "unknown" or "virtual" || string.IsNullOrWhiteSpace(rule.Detector)
                || string.IsNullOrWhiteSpace(rule.Name) || (string.IsNullOrWhiteSpace(rule.Location) && string.IsNullOrWhiteSpace(rule.Serial))
                || new[] { rule.Detector, rule.Name, rule.Location, rule.Serial, rule.Vendor, rule.Description }
                    .Any(s => s != null && (s.Length > 4096 || s.Contains('*') || s.Contains('?') || s.Any(char.IsControl))))
                throw new PluginException("ConfigurationError", "Each additional device needs a known physical type, exact detector/name and location or serial; wildcards are forbidden");
        }
    }
    public static string[] Detectors(BackendSettings settings) => (settings.EnableEneDram ? new[] { MemoryDetector } : [])
        .Concat(settings.AdditionalDevices.Select(r => r.Detector)).Distinct(StringComparer.Ordinal).ToArray();
    public static bool Allows(RgbDevice d, BackendSettings settings) => (settings.EnableEneDram && d.IsEneDram)
        || settings.AdditionalDevices.Any(rule => rule.Matches(d));
    private static string Identity(RgbDevice d) => d.IsEneDram
        // Persisted IDs from the original memory plugin remain byte-for-byte compatible.
        ? JsonSerializer.Serialize(new[] { d.Location.Trim(), d.Name.Trim() })
        : JsonSerializer.Serialize(new[] { d.Type.ToString(System.Globalization.CultureInfo.InvariantCulture), d.Location, d.Name, d.Serial });
    public static DeviceBinding[] Bind(RgbDevice[] devices, BackendSettings settings)
    {
        var result = new List<DeviceBinding>();
        foreach (var group in devices.Where(d => Allows(d, settings)).GroupBy(Identity, StringComparer.Ordinal))
        {
            var first = group.First();
            var ambiguous = group.Count() != 1 || (first.IsEneDram ? string.IsNullOrWhiteSpace(first.Location)
                : string.IsNullOrWhiteSpace(first.Location) && string.IsNullOrWhiteSpace(first.Serial));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(group.Key))).ToLowerInvariant();
            foreach (var d in group)
                result.Add(new((d.IsEneDram ? "ene-dram:" : "openrgb:") + hash + (ambiguous ? $":index-{d.Index}" : ""), d, ambiguous));
        }
        return result.OrderBy(b => b.Device.Index).ToArray();
    }
    public static bool SameDevice(RgbDevice a, RgbDevice b) => a.Type == b.Type && Identity(a) == Identity(b)
        && a.Serial == b.Serial && a.Vendor == b.Vendor && a.Description == b.Description && a.Version == b.Version
        && a.Colors.Length == b.Colors.Length && a.Modes.Length == b.Modes.Length
        && a.Modes.Zip(b.Modes).All(pair => pair.First.Name == pair.Second.Name
            && pair.First.Fields.AsSpan(0, 8).SequenceEqual(pair.Second.Fields.AsSpan(0, 8)));
}
