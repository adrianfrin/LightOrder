using System.Diagnostics;
using System.Text.Json;
using ControlCenter.Contracts;
using LightOrder.App;

internal static class FanVerification
{
    internal static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var client = new PipeClient();
        async Task<ServiceSnapshot> Snapshot() => Wire.Read<ServiceSnapshot>(await client.CallAsync(StandardMethods.Snapshot, new { }));
        var before = await Snapshot();
        var fan = before.Devices.Single(d => d.Capabilities.Contains(StandardCapabilities.FanProfile));
        async Task<JsonElement> Apply(string capability, object parameters)
        {
            var result = await client.CallAsync(StandardMethods.Apply, new DeviceCommand(fan.Id, capability, Wire.Element(parameters)));
            if (result.GetProperty("status").GetString() is not ("applied" or "released")) throw new IOException("Fan operation not confirmed.");
            return result;
        }
        var records = new List<object>();
        var completed = false;
        string? failure = null;
        var cleanupErrors = new List<string>();
        try
        {
            var initialRelease = await Apply(StandardCapabilities.Release, new { });
            records.Add(new { mode = "initial-release", readback = initialRelease.GetProperty("readback").Clone() });
            foreach (var mode in new[] { "silent-start", "balanced", "silent-return" })
            {
                var profile = mode == "balanced" ? FanProfile.Silent with { Mode = "curve", RadiatorPercent = 65, CasePercent = 55,
                    RadiatorCurve = [new(40,65), new(55,65), new(65,80), new(80,100)],
                    CaseCurve = [new(40,55), new(55,65), new(65,80), new(80,100)] } : FanProfile.Silent;
                await Apply(StandardCapabilities.FanProfile, profile);
                var elapsed = Stopwatch.StartNew();
                var duration = mode == "balanced" ? 20 : 30;
                while (elapsed.Elapsed.TotalSeconds < duration)
                {
                    var snapshot = await Snapshot();
                    var device = snapshot.Devices.Single(d => d.Id == fan.Id);
                    var meta = device.Metadata!.Value;
                    if (!meta.GetProperty("session").GetProperty("active").GetBoolean()) throw new IOException("Control stopped during verification.");
                    var regulation = meta.GetProperty("regulation");
                    var thermal = regulation.GetProperty("thermalProtection").GetBoolean();
                    var rd = regulation.GetProperty("radiatorPercent").GetSingle();
                    var cd = regulation.GetProperty("casePercent").GetSingle();
                    if (mode != "balanced" && !thermal && (rd != 40 || cd != 25)) throw new IOException("Silent duty changed below emergency threshold.");
                    var sensors = snapshot.Telemetry.Select(x => x.Deserialize<Sample>(Wire.Json)!).Where(s => s.DeviceId == fan.Id && s.Name is "cpu.temperature" or "radiator.rpm" or "case.rpm" or "pump.rpm").ToArray();
                    foreach (var name in new[] { "cpu.temperature", "radiator.rpm", "case.rpm", "pump.rpm" })
                    {
                        var sample = sensors.SingleOrDefault(s => s.Name == name);
                        if (sample?.Value is not float value || !float.IsFinite(value) || (name.EndsWith("rpm", StringComparison.Ordinal) && value < 200))
                            throw new IOException("Required telemetry missing or invalid: " + name);
                    }
                    records.Add(new { mode, seconds = elapsed.Elapsed.TotalSeconds, regulation, sensors });
                    await Task.Delay(1000);
                }
                Console.WriteLine("PASS live fan " + mode + "; requested duty and real RPM recorded.");
            }
            var released = await Apply(StandardCapabilities.Release, new { });
            if (!released.GetProperty("readback").GetProperty("restorationVerified").GetBoolean()) throw new IOException("Release baseline not verified.");
            records.Add(new { mode = "release", verified = true });
            await Apply(StandardCapabilities.FanProfile, FanProfile.Silent);
            var prefs = await client.CallAsync(StandardMethods.ProfilesRead, new { });
            var values = prefs.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
            var profiles = prefs.TryGetProperty("fanProfiles", out var existing) ? existing.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal) : [];
            profiles[fan.Id] = Wire.Element(FanProfile.Silent);
            values["fanProfiles"] = Wire.Element(profiles);
            await client.CallAsync(StandardMethods.ProfilesWrite, values);
            var saved = await client.CallAsync(StandardMethods.ProfilesRead, new { });
            var savedProfile = Wire.Read<FanProfile>(saved.GetProperty("fanProfiles").GetProperty(fan.Id));
            if (savedProfile.Mode != "manual" || savedProfile.RadiatorPercent != 40 || savedProfile.CasePercent != 25)
                throw new IOException("Silent persistence not confirmed.");
            var final = (await Snapshot()).Devices.Single(d => d.Id == fan.Id).Metadata!.Value;
            var finalRegulation = final.GetProperty("regulation");
            if (!final.GetProperty("session").GetProperty("active").GetBoolean() ||
                (!finalRegulation.GetProperty("thermalProtection").GetBoolean() &&
                 (finalRegulation.GetProperty("radiatorPercent").GetSingle() != 40 || finalRegulation.GetProperty("casePercent").GetSingle() != 25)))
                throw new IOException("Final active silent state not confirmed.");
            records.Add(new { mode = "final-persisted-silent", profile = savedProfile, regulation = finalRegulation });
            completed = true;
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            if (!completed)
            {
                try { await Apply(StandardCapabilities.Release, new { }); }
                catch (Exception error) { cleanupErrors.Add(error.ToString()); }
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "fan-verification.json"), JsonSerializer.Serialize(new { completed, failure, cleanupErrors,
                requestedFinalState = "saved active silent 40% radiator / 25% case", acousticMeasurement = false, records }, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
        }
        Console.WriteLine("PASS baseline restoration and persisted active silent; acoustic output not measured.");
    }
    private sealed record Sample(string DeviceId, string Name, float? Value, string Unit, DateTimeOffset Timestamp);
}
