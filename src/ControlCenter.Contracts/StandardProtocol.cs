using System.Text.Json;

namespace ControlCenter.Contracts;

/// <summary>Vendor-neutral capability descriptor exchanged across all process boundaries.</summary>
public sealed record StandardDevice(string Id, string Name, string Kind, string[] Capabilities,
    string Status, string Detail, JsonElement? Metadata = null);
public sealed record DeviceCommand(string DeviceId, string Capability, JsonElement Parameters);
public sealed record ServiceSnapshot(string Version, StandardDevice[] Devices, JsonElement[] Telemetry,
    string[] Diagnostics, bool Demo = false);
public sealed record PluginDescriptor(string Id, string Name, string Version, int ProtocolMajor,
    string EntryPoint, string[] Arguments, string[] Permissions, string[] Methods, bool Enabled = false);

public static class StandardMethods
{
    public const string Discover = "device.discover";
    public const string Execute = "device.execute";
    public const string Telemetry = "telemetry.read";
    public const string Release = "device.release";
    public const string Snapshot = "app.snapshot";
    public const string Refresh = "app.refresh";
    public const string Apply = "app.execute";
    public const string Plugins = "app.plugins";
    public const string ProfilesRead = "profiles.read";
    public const string ProfilesWrite = "profiles.write";
    public const string AutomationRead = "app.automation.read";
    public const string AutomationWrite = "app.automation.write";
}
