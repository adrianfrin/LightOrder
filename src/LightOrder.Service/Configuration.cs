using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ControlCenter.Contracts;

namespace LightOrder.Service;

public sealed record ServiceSettings(int SchemaVersion, string OwnerSid, string[] EnabledPlugins)
{
    public static readonly string StateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LightOrder");
    public static readonly string ConfigurationPath = Path.Combine(StateDirectory, "service.json");
    public static readonly JsonSerializerOptions StrictJson = new(Wire.Json) { MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static ServiceSettings Load()
    {
        ServiceSecurity.ValidateProtectedPath(StateDirectory, true);
        ServiceSecurity.ValidateProtectedPath(ConfigurationPath, false);
        var result = JsonSerializer.Deserialize<ServiceSettings>(ReadBounded(ConfigurationPath, 16384), StrictJson)
            ?? throw new InvalidDataException("service.json must be an object.");
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (SchemaVersion != Wire.Version) throw new InvalidDataException("Unsupported service configuration schemaVersion.");
        ServiceSecurity.ValidateOwnerSid(OwnerSid);
        if (EnabledPlugins is null || EnabledPlugins.Length > 32 || EnabledPlugins.Any(id => !ManifestValidation.ValidId(id)) ||
            EnabledPlugins.Distinct(StringComparer.Ordinal).Count() != EnabledPlugins.Length)
            throw new InvalidDataException("enabledPlugins must contain at most 32 unique plugin IDs.");
    }

    public static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new InvalidDataException("JSON file exceeds the allowed size.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? bytes[3..] : bytes;
    }
}

public static partial class ManifestValidation
{
    public static readonly string[] AllowedMethods = [StandardMethods.Discover, StandardMethods.Execute, StandardMethods.Telemetry, StandardMethods.Release];
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
    public static bool ValidId(string? value) => value is not null && IdPattern().IsMatch(value);

    public static void Validate(PluginDescriptor manifest)
    {
        if (!ValidId(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 256 ||
            string.IsNullOrWhiteSpace(manifest.Version) || manifest.Version.Length > 64 || manifest.ProtocolMajor != Wire.Version)
            throw new InvalidDataException("Plugin identity/version is invalid or incompatible.");
        if (string.IsNullOrWhiteSpace(manifest.EntryPoint) || manifest.EntryPoint.Length > 256 ||
            Path.IsPathRooted(manifest.EntryPoint) || manifest.EntryPoint.Contains(':') ||
            manifest.EntryPoint.Split(['/', '\\']).Any(p => p is ".." or "." or "") ||
            !manifest.EntryPoint.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plugin entryPoint must be a relative .exe within the plugin directory.");
        if (manifest.Arguments is null || manifest.Arguments.Length > 32 || manifest.Arguments.Any(s => s is null || s.Length > 2048 || s.Contains('\0')) ||
            manifest.Permissions is null || manifest.Permissions.Length > 32 || manifest.Permissions.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 128))
            throw new InvalidDataException("Invalid plugin arguments/permissions.");
        if (manifest.Methods is null || manifest.Methods.Length != AllowedMethods.Length ||
            !AllowedMethods.ToHashSet(StringComparer.Ordinal).SetEquals(manifest.Methods))
            throw new InvalidDataException("Plugin methods must explicitly list the four standard device methods only.");
    }

    public static List<(PluginDescriptor Descriptor, string Path)> Load(string root, ServiceSettings settings)
    {
        ServiceSecurity.ValidateTree(root);
        var result = new List<(PluginDescriptor Descriptor, string Path)>();
        var candidates = Directory.EnumerateDirectories(root).Select(p => Path.Combine(p, "plugin.json"))
            .Append(Path.Combine(root, "plugin.json")).Where(File.Exists).ToArray();
        if (candidates.Length > 64) throw new InvalidDataException("Too many installed plugin manifests.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in candidates)
        {
            var descriptor = JsonSerializer.Deserialize<PluginDescriptor>(ServiceSettings.ReadBounded(path, 32768), ServiceSettings.StrictJson)
                ?? throw new InvalidDataException("Empty plugin manifest.");
            Validate(descriptor);
            if (!identities.Add(descriptor.Id)) throw new InvalidDataException("Duplicate plugin ID.");
            var entry = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, descriptor.EntryPoint));
            ServiceSecurity.ValidateProtectedPath(entry, false);
            result.Add((descriptor with { Enabled = descriptor.Enabled && settings.EnabledPlugins.Contains(descriptor.Id, StringComparer.Ordinal) }, path));
        }
        if (settings.EnabledPlugins.Any(id => !result.Any(p => p.Descriptor.Id == id && p.Descriptor.Enabled)))
            throw new InvalidDataException("An enabledPlugins entry has no enabled installed manifest.");
        return result;
    }
}

public sealed class ProfileStore(string directory)
{
    public const int MaxBytes = 256 * 1024;
    private readonly string path = Path.Combine(directory, "profiles.json");
    public static void Validate(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty("schemaVersion", out var schema) ||
            !schema.TryGetInt32(out var version) || version != Wire.Version)
            throw new PluginException("InvalidArgument", "Profiles require an object with schemaVersion: 1.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Wire.Json);
        if (bytes.Length > MaxBytes) throw new PluginException("InvalidArgument", "Profiles exceed 256 KiB.");
        using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        ValidateData(parsed.RootElement);
    }

    private static void ValidateData(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new PluginException("InvalidArgument", "Duplicate JSON property.");
                ValidateData(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateData(item);
        else if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out var number) || !double.IsFinite(number)))
            throw new PluginException("InvalidArgument", "Profile numbers must be finite.");
    }

    public JsonElement Read()
    {
        ServiceSecurity.ValidateProtectedPath(directory, true);
        if (!File.Exists(path)) return Wire.Element(new { schemaVersion = 1 });
        ServiceSecurity.ValidateProtectedPath(path, false);
        using var document = JsonDocument.Parse(ServiceSettings.ReadBounded(path, MaxBytes), new JsonDocumentOptions { MaxDepth = 32 });
        Validate(document.RootElement);
        return document.RootElement.Clone();
    }

    public ApplyResult Write(JsonElement document)
    {
        Validate(document);
        ServiceSecurity.ValidateProtectedPath(directory, true);
        if (File.Exists(path)) ServiceSecurity.ValidateProtectedPath(path, false);
        var temporary = Path.Combine(directory, ".profiles-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, Wire.Json);
                stream.Flush(true);
            }
            ServiceSecurity.ValidateProtectedPath(temporary, false);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new ApplyResult("saved", "Profiles saved. Saving profiles does not execute device commands.");
    }
}
