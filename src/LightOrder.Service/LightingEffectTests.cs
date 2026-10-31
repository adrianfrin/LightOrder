using ControlCenter.Contracts;

namespace LightOrder.Service;

public static class LightingEffectTests
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED lighting: " + name);
            checks++;
        }
        var clock = new TestClock();
        var animation = LightingAnimation.Parse(Wire.Element(new { effect = "breathing", color = "#804020", brightness = .8, periodSeconds = 4 }), clock.GetUtcNow());
        double Intensity(double seconds) => animation.Frame("device", seconds, "op").Parameters.GetProperty("brightness").GetDouble();
        Check(Math.Abs(Intensity(0)) < 1e-9, "breathing starts dark");
        Check(Math.Abs(Intensity(1) - .4) < 1e-9, "breathing quarter phase half intensity");
        Check(Math.Abs(Intensity(2) - .8) < 1e-9, "breathing peak respects maximum");
        Check(Math.Abs(Intensity(4)) < 1e-9, "breathing wraps");
        var rainbow = animation with { Effect = "rainbow", PeriodSeconds = 6 };
        string Color(double seconds) => rainbow.Frame("device", seconds, "op").Parameters.GetProperty("color").GetString()!;
        Check(Color(0) == "#FF0000" && Color(2) == "#00FF00" && Color(4) == "#0000FF", "rainbow primary phases independently specified");
        Check(Color(6) == "#FF0000" && Color(-2) == "#0000FF", "rainbow wrapping");
        foreach (var invalid in new object[] {
            new { effect = "rainbow", color = "#123456", periodSeconds = 4 },
            new { effect = "unknown", color = "#123456", brightness = .5, periodSeconds = 4 },
            new { effect = "rainbow", color = "garbage", brightness = .5, periodSeconds = 4 },
            new { effect = "rainbow", color = "#123456", brightness = 1.1, periodSeconds = 4 },
            new { effect = "rainbow", color = "#123456", brightness = .5, periodSeconds = 1 },
            new { effect = "rainbow", color = "#123456", brightness = .5, periodSeconds = 61 },
            new { effect = "rainbow", color = "#123456", brightness = .5, periodSeconds = 4, path = "file.exe" } })
        {
            bool rejected = false;
            try { LightingAnimation.Parse(Wire.Element(invalid), clock.GetUtcNow()); } catch (PluginException) { rejected = true; }
            Check(rejected, "invalid animation rejected");
        }
        var device = new StandardDevice("d", "Light", "lighting", [StandardCapabilities.LightingStatic], "ready", "", Wire.Element(new { effects = new[] { "static" }, custom = "preserved" }));
        var advertised = LightingEffects.Advertise(device);
        Check(advertised.Capabilities.Contains(StandardCapabilities.LightingEffect) && advertised.Metadata!.Value.GetProperty("custom").GetString() == "preserved", "add generic animation capability without losing plugin metadata");
        Check(!LightingEffects.Advertise(device with { Capabilities = [] }).Capabilities.Contains(StandardCapabilities.LightingEffect), "no fabricated write ability");
        var frames = new List<DeviceCommand>();
        Task<ApplyResult> Apply(DeviceCommand frame) { frames.Add(frame); return Task.FromResult(new ApplyResult("accepted", "Fake transport only")); }
        var effects = new LightingEffects(clock);
        var command = new DeviceCommand("d", StandardCapabilities.LightingEffect, Wire.Element(animation));
        await effects.StartAsync(command, Apply);
        clock.Advance(TimeSpan.FromSeconds(2));
        await effects.TickAsync(Apply, _ => { });
        Check(frames.Count == 2 && Math.Abs(frames[1].Parameters.GetProperty("brightness").GetDouble() - .8) < 1e-9, "monotonic animation clock");
        Check(effects.Describe(device).Metadata!.Value.GetProperty("current").GetProperty("effect").GetString() == "breathing", "snapshot reports intended animated mode");
        Check(frames[0].Parameters.GetProperty("operationId").GetString() != frames[1].Parameters.GetProperty("operationId").GetString(), "frames use distinct operation identity");
        var writes = frames.Count;
        effects.Stop("d");
        await effects.TickAsync(Apply, _ => { });
        Check(frames.Count == writes, "static supersession stops future frames");
        await effects.StartAsync(command, Apply);
        var diagnostics = new List<string>();
        await effects.TickAsync(_ => throw new PluginException("TimeoutOutcomeUnknown", "test"), diagnostics.Add);
        await effects.TickAsync(Apply, diagnostics.Add);
        Check(effects.Count == 0 && diagnostics.Count == 1, "unknown write outcome stops without replay");
        await effects.StartAsync(command, Apply);
        await effects.FinishAsync(Apply, diagnostics.Add);
        Check(effects.Count == 0 && frames[^1].Parameters.GetProperty("brightness").GetDouble() == .8 && frames[^1].Parameters.GetProperty("color").GetString() == "#804020", "normal stop leaves final requested static color");
        var phase = clock.GetUtcNow().AddSeconds(-1);
        await effects.StartAsync(command with { Parameters = Wire.Element(animation with { PhaseUtc = phase }) }, Apply);
        Check(Math.Abs(frames[^1].Parameters.GetProperty("brightness").GetDouble() - .4) < 1e-9, "shared wall-clock phase at start");
        effects.Clear();
        for (var i = 0; i < 16; i++) await effects.StartAsync(command with { DeviceId = "d" + i }, Apply);
        var capped = false;
        try { await effects.StartAsync(command with { DeviceId = "over" }, Apply); } catch (PluginException e) { capped = e.Code == "ResourceLimit"; }
        Check(capped && effects.Count == 16, "bounded simultaneous effects");
        Console.WriteLine($"PASS {checks} lighting effect checks; no hardware accessed.");
        return checks;
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset utc = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        private long ticks;
        public override DateTimeOffset GetUtcNow() => utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan duration) { utc += duration; ticks += duration.Ticks; }
    }
}
