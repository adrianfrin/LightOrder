using System.Text.Json.Serialization;

namespace ControlCenter.Contracts;

/// <summary>Built-in extensible identifiers. Third-party plugins may advertise namespaced capabilities.</summary>
public static class StandardCapabilities
{
    public const string LightingStatic = "lighting.static";
    public const string LightingOff = "lighting.off";
    public const string FanProfile = "fan.profile";
    public const string WindowsPowerScheme = "power.windowsScheme";
    public const string ProcessPolicy = "process.policy";
    public const string SystemInfo = "system.info";
    public const string Release = "device.release";
}

[JsonConverter(typeof(JsonStringEnumConverter<LightingEffect>))]
public enum LightingEffect { Static, Off }

[JsonConverter(typeof(JsonStringEnumConverter<FanControlMode>))]
public enum FanControlMode { Manual, Curve }

[JsonConverter(typeof(JsonStringEnumConverter<ExecutionState>))]
public enum ExecutionState { Accepted, ReadbackVerified, Partial, Failed, Unknown }

public static class ProtocolValues
{
    public static LightingEffect ParseLightingEffect(string value) => value switch
    {
        "static" => LightingEffect.Static,
        "off" => LightingEffect.Off,
        _ => throw new PluginException("InvalidArgument", "Unknown lighting effect.")
    };
    public static FanControlMode ParseFanMode(string value) => value switch
    {
        "manual" => FanControlMode.Manual,
        "curve" => FanControlMode.Curve,
        _ => throw new PluginException("InvalidArgument", "Unknown fan control mode.")
    };
    public static string ToWire(this LightingEffect value) => value switch
    {
        LightingEffect.Static => "static",
        LightingEffect.Off => "off",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
