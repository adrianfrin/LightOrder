using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ControlCenter.Contracts;

namespace LightOrder.Service;

[JsonConverter(typeof(JsonStringEnumConverter<AnimatedLightingMode>))]
public enum AnimatedLightingMode { Breathing, Rainbow }

public sealed record LightingAnimation(string Effect, string Color, double Brightness,
    double PeriodSeconds = 6, string? OperationId = null, DateTimeOffset? PhaseUtc = null)
{
    [JsonIgnore]
    public AnimatedLightingMode Mode => Effect switch
    {
        "breathing" => AnimatedLightingMode.Breathing,
        "rainbow" => AnimatedLightingMode.Rainbow,
        _ => throw new PluginException("InvalidArgument", "Unknown animated lighting effect.")
    };

    public static LightingAnimation Parse(JsonElement parameters, DateTimeOffset now)
    {
        LightingAnimation animation;
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("brightness", out _))
            throw new PluginException("InvalidArgument", "Lighting animation brightness is required.");
        try { animation = parameters.Deserialize<LightingAnimation>(ServiceSettings.StrictJson) ?? throw new JsonException(); }
        catch (JsonException) { throw new PluginException("InvalidArgument", "Invalid lighting animation parameters."); }
        _ = animation.Mode;
        if (animation.Color is null || !Regex.IsMatch(animation.Color, "^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant) ||
            !double.IsFinite(animation.Brightness) || animation.Brightness is < 0 or > 1 ||
            !double.IsFinite(animation.PeriodSeconds) || animation.PeriodSeconds is < 2 or > 60 ||
            animation.OperationId is { Length: > 128 } ||
            animation.PhaseUtc is { } phase && Math.Abs((phase - now).TotalDays) > 1)
            throw new PluginException("InvalidArgument", "Color, brightness, period (2–60 seconds) or phase is invalid.");
        return animation;
    }

    public DeviceCommand Frame(string deviceId, double elapsedSeconds, string operationId)
    {
        var phase = ((elapsedSeconds % PeriodSeconds) + PeriodSeconds) % PeriodSeconds / PeriodSeconds;
        var color = Color;
        var brightness = Brightness;
        switch (Mode)
        {
            case AnimatedLightingMode.Breathing:
                brightness *= (1 - Math.Cos(phase * 2 * Math.PI)) / 2;
                break;
            case AnimatedLightingMode.Rainbow:
                color = Hue(phase * 6);
                break;
            default: throw new ArgumentOutOfRangeException();
        }
        return new DeviceCommand(deviceId, StandardCapabilities.LightingStatic,
            Wire.Element(new { color, brightness, operationId }));
    }

    private static string Hue(double hue)
    {
        var x = 1 - Math.Abs(hue % 2 - 1);
        (double r, double g, double b) = (int)hue switch
        {
            0 => (1d, x, 0d), 1 => (x, 1d, 0d), 2 => (0d, 1d, x),
            3 => (0d, x, 1d), 4 => (x, 0d, 1d), _ => (1d, 0d, x)
        };
        return $"#{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}";
    }
}

/// <summary>Software effects use only advertised static color control. All calls run under the runtime gate.</summary>
public sealed class LightingEffects(TimeProvider? clock = null)
{
    private sealed record Active(LightingAnimation Animation, long Started, double Offset);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Active> active = new(StringComparer.Ordinal);
    public int Count => active.Count;

    public static bool CanAnimate(StandardDevice device) =>
        device.Capabilities.Contains(StandardCapabilities.LightingStatic, StringComparer.Ordinal);

    public static StandardDevice Advertise(StandardDevice device)
    {
        if (!CanAnimate(device)) return device;
        var properties = device.Metadata is { ValueKind: JsonValueKind.Object } metadata
            ? metadata.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var effects = properties.TryGetValue("effects", out var existing) && existing.ValueKind == JsonValueKind.Array
            ? existing.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
            : new List<string> { "static" };
        effects.AddRange(["breathing", "rainbow"]);
        properties["effects"] = Wire.Element(effects.Distinct(StringComparer.Ordinal).ToArray());
        properties["animationSource"] = Wire.Element("service");
        properties["animationFrameRate"] = Wire.Element(4);
        return device with { Capabilities = device.Capabilities.Append(StandardCapabilities.LightingEffect).Distinct(StringComparer.Ordinal).ToArray(), Metadata = Wire.Element(properties) };
    }

    public StandardDevice Describe(StandardDevice device)
    {
        var decorated = Advertise(device);
        if (!active.TryGetValue(device.Id, out var entry)) return decorated;
        var properties = decorated.Metadata!.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        properties["current"] = Wire.Element(new { effect = entry.Animation.Effect, color = entry.Animation.Color,
            brightness = entry.Animation.Brightness, periodSeconds = entry.Animation.PeriodSeconds });
        properties["stateSource"] = Wire.Element("service-command");
        return decorated with { Metadata = Wire.Element(properties) };
    }

    public async Task<ApplyResult> StartAsync(DeviceCommand command, Func<DeviceCommand, Task<ApplyResult>> execute)
    {
        var now = time.GetUtcNow();
        var animation = LightingAnimation.Parse(command.Parameters, now);
        if (!active.ContainsKey(command.DeviceId) && active.Count >= 16)
            throw new PluginException("ResourceLimit", "At most 16 devices may run software animations.");
        var offset = animation.PhaseUtc is { } phase ? (now - phase).TotalSeconds : 0;
        // Supersede before the write: if its outcome becomes unknown the previous effect cannot keep writing.
        active.Remove(command.DeviceId);
        var started = time.GetTimestamp();
        var first = await execute(animation.Frame(command.DeviceId, offset, Guid.NewGuid().ToString("N")));
        RequireAccepted(first);
        active[command.DeviceId] = new(animation, started, offset);
        return new ApplyResult("accepted", "Service animation started; physical lighting readback is unavailable.",
            new { effect = animation.Effect, frameRate = 4, source = "service" });
    }

    public void Stop(string deviceId) => active.Remove(deviceId);
    public void Clear() => active.Clear();

    public async Task TickAsync(Func<DeviceCommand, Task<ApplyResult>> execute, Action<string> diagnostic)
    {
        foreach (var (id, entry) in active.ToArray())
        {
            try
            {
                var elapsed = time.GetElapsedTime(entry.Started).TotalSeconds + entry.Offset;
                RequireAccepted(await execute(entry.Animation.Frame(id, elapsed, Guid.NewGuid().ToString("N"))));
            }
            catch (Exception error)
            {
                active.Remove(id);
                diagnostic($"Animation stopped for {id}: {(error is PluginException p ? p.Code : error.GetType().Name)}. No retry.");
            }
        }
    }

    public async Task FinishAsync(Func<DeviceCommand, Task<ApplyResult>> execute, Action<string> diagnostic)
    {
        var entries = active.ToArray();
        active.Clear();
        foreach (var (id, entry) in entries)
        {
            try
            {
                RequireAccepted(await execute(new DeviceCommand(id, StandardCapabilities.LightingStatic,
                    Wire.Element(new { color = entry.Animation.Color, brightness = entry.Animation.Brightness, operationId = Guid.NewGuid().ToString("N") }))));
            }
            catch (Exception error) { diagnostic($"Final static lighting for {id} unconfirmed: {error.GetType().Name}."); }
        }
    }

    private static void RequireAccepted(ApplyResult result)
    {
        if (result.Status is not ("accepted" or "applied" or "verified" or "unchanged"))
            throw new PluginException("EffectFrameRejected", "Plugin did not accept the effect frame.");
    }
}
