using System.Diagnostics;
using System.Text.Json;
using ControlCenter.Contracts;
using LightOrder.App;

internal static class LiveVerification
{
    internal static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var client = new PipeClient();
        var records = new List<object>();
        async Task<JsonElement> Rpc(string method, object value) => await client.CallAsync(method, value);
        async Task<JsonElement> Apply(string id, string capability, object parameters)
        {
            var result = await Rpc(StandardMethods.Apply, new DeviceCommand(id, capability, Wire.Element(parameters)));
            if (result.GetProperty("status").GetString() is not ("accepted" or "applied" or "unchanged" or "released" or "restored" or "verified"))
                throw new InvalidOperationException("Device did not confirm success: " + result.GetRawText());
            return result;
        }
        async Task<ServiceSnapshot> Snapshot(bool refresh = false) => Wire.Read<ServiceSnapshot>(await Rpc(refresh ? StandardMethods.Refresh : StandardMethods.Snapshot, new { }));
        var initial = await Snapshot(true);
        var lights = initial.Devices.Where(d => d.Capabilities.Contains(StandardCapabilities.LightingEffect)).ToArray();
        var fan = initial.Devices.Single(d => d.Capabilities.Contains(StandardCapabilities.FanProfile));
        var policy = initial.Devices.Single(d => d.Capabilities.Contains(StandardCapabilities.ProcessPolicy));
        var prefs = await Rpc(StandardMethods.ProfilesRead, new { });
        var automation = await Rpc(StandardMethods.AutomationRead, new { });
        var originalRules = automation.GetProperty("rules").Clone();
        if (originalRules.GetArrayLength() != 0) throw new InvalidOperationException("Live verification requires an empty automation list so no user rule is interrupted.");
        if (fan.Metadata!.Value.GetProperty("session").GetProperty("active").GetBoolean())
            throw new InvalidOperationException("Live verification refuses to replace active fan control.");
        if (policy.Metadata!.Value.GetProperty("recoveryPending").GetBoolean()) throw new InvalidOperationException("Policy recovery must be resolved first.");
        if (lights.Length != 3) throw new InvalidOperationException("Expected the three previously validated lighting endpoints.");
        var baselines = new Dictionary<string, JsonElement>();
        foreach (var light in lights)
        {
            if (!prefs.TryGetProperty("appliedLighting", out var saved) || !saved.TryGetProperty(light.Id, out var baseline))
                throw new InvalidOperationException("A known saved lighting setting is required for restoration.");
            baselines[light.Id] = baseline.Clone();
        }
        async Task RestoreLight(string id)
        {
            var baseline = baselines[id];
            var effect = baseline.GetProperty("effect").GetString();
            var parameters = new Dictionary<string, object?> { ["color"] = baseline.GetProperty("color").GetString(),
                ["brightness"] = baseline.GetProperty("brightness").GetDouble(), ["operationId"] = Guid.NewGuid().ToString("N") };
            var capability = effect switch { "off" => StandardCapabilities.LightingOff, "static" => StandardCapabilities.LightingStatic,
                "breathing" or "rainbow" => StandardCapabilities.LightingEffect, _ => throw new InvalidOperationException("Unknown baseline effect.") };
            if (capability == StandardCapabilities.LightingEffect)
            {
                parameters["effect"] = effect;
                parameters["periodSeconds"] = baseline.TryGetProperty("periodSeconds", out var period) ? period.GetDouble() : 6;
            }
            await Apply(id, capability, parameters);
        }
        var changedLights = new HashSet<string>();
        var fanAttempted = false;
        var rulesAttempted = false;
        Process? probe = null;
        var completed = false;
        try
        {
            foreach (var mode in new[] { "breathing", "rainbow" })
            {
                var phase = DateTimeOffset.UtcNow;
                foreach (var light in lights)
                {
                    changedLights.Add(light.Id);
                    await Apply(light.Id, StandardCapabilities.LightingEffect, new { effect = mode, color = "#4D9FFF", brightness = .2,
                        periodSeconds = 4, phaseUtc = phase, operationId = Guid.NewGuid().ToString("N") });
                }
                await Task.Delay(5000);
                var running = await Snapshot();
                if (lights.Any(l => running.Devices.Single(d => d.Id == l.Id).Metadata!.Value.GetProperty("current").GetProperty("effect").GetString() != mode))
                    throw new InvalidOperationException("An animation stopped before the verification sample.");
                records.Add(new { stage = "lighting-effect", mode, devices = lights.Length, sharedPhaseUtc = phase, softwareActiveAfterSeconds = 5,
                    physicalReadback = false });
                Console.WriteLine("PASS live " + mode + " on three devices; physical appearance is unverified.");
            }
            foreach (var light in lights) await RestoreLight(light.Id);
            changedLights.Clear();
            var stopped = await Snapshot(true);
            foreach (var light in lights)
                if (stopped.Devices.Single(d => d.Id == light.Id).Metadata!.Value.GetProperty("current").GetProperty("effect").GetString() != baselines[light.Id].GetProperty("effect").GetString())
                    throw new InvalidOperationException("Lighting baseline did not replace the test animation.");
            records.Add(new { stage = "lighting-restored", devices = lights.Length, source = "saved-user-setting", physicalReadback = false });

            var ruleId = "integration-process-" + Guid.NewGuid().ToString("N");
            var processName = Path.GetFileName(Environment.ProcessPath!);
            if (!processName.Equals("LightOrder.Integration.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Run using the integration apphost.");
            var rule = new { id = ruleId, name = "Temporary integration process", enabled = true,
                trigger = new { kind = "process", processName }, commands = new[] {
                    new DeviceCommand(policy.Id, StandardCapabilities.ProcessPolicy, Wire.Element(new { priority = "BelowNormal", affinityMask = "1" })) } };
            rulesAttempted = true;
            await Rpc(StandardMethods.AutomationWrite, new { schemaVersion = 1, rules = new[] { rule } });
            await Task.Delay(2500); // Let the service baseline this already-running caller.
            probe = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "wait-probe" } })
                ?? throw new IOException("Probe failed to start.");
            var limit = Stopwatch.StartNew();
            while (limit.Elapsed < TimeSpan.FromSeconds(12))
            {
                await Task.Delay(500); probe.Refresh();
                if (probe.PriorityClass == ProcessPriorityClass.BelowNormal && probe.ProcessorAffinity == (nint)1) break;
            }
            probe.Refresh();
            if (probe.PriorityClass != ProcessPriorityClass.BelowNormal || probe.ProcessorAffinity != (nint)1)
                throw new InvalidOperationException("Process trigger did not produce the independently observed priority/affinity.");
            var statuses = await Rpc(StandardMethods.AutomationRead, new { });
            if (!statuses.GetProperty("status").EnumerateArray().Any(s => s.GetProperty("ruleId").GetString() == ruleId && s.GetProperty("state").GetString() == "completed"))
                throw new InvalidOperationException("Process automation did not persist completion.");
            var release = await Apply(policy.Id, StandardCapabilities.Release, new { });
            probe.Refresh();
            if (probe.PriorityClass != ProcessPriorityClass.Normal || probe.ProcessorAffinity == (nint)1)
                throw new InvalidOperationException("Probe policies did not restore.");
            records.Add(new { stage = "process-automation", exactExecutable = processName, priorityReadback = "BelowNormal", affinityReadback = "1",
                normalPriorityRestored = true, fullAffinityRestored = true, durableState = "completed", release });
            Console.WriteLine("PASS live exact-process rule, durable completion, independent priority/affinity readback and restoration.");
            probe.Kill(); await probe.WaitForExitAsync(); probe.Dispose(); probe = null;
            await Rpc(StandardMethods.AutomationWrite, new { schemaVersion = 1, rules = originalRules });
            rulesAttempted = false;

            fanAttempted = true;
            var start = await Apply(fan.Id, StandardCapabilities.FanProfile, FanProfile.Silent);
            var samples = new List<object>();
            for (var i = 0; i < 15; i++)
            {
                await Task.Delay(2000);
                var snapshot = await Snapshot();
                var session = snapshot.Devices.Single(d => d.Id == fan.Id).Metadata!.Value.GetProperty("session");
                if (!session.GetProperty("active").GetBoolean() || session.GetProperty("sampleCount").GetInt64() < 1)
                    throw new InvalidOperationException("Ordinary snapshots did not reflect the active fan session.");
                var readings = snapshot.Telemetry.Where(v => v.TryGetProperty("name", out var n) && n.GetString() is "cpu.temperature" or "radiator.rpm" or "case.rpm" or "pump.rpm").ToArray();
                foreach (var name in new[] { "cpu.temperature", "radiator.rpm", "case.rpm", "pump.rpm" })
                {
                    var reading = readings.Single(v => v.GetProperty("name").GetString() == name);
                    var value = reading.GetProperty("value").GetDouble();
                    if (name == "cpu.temperature" ? value > 80 || value < 0 : value < 200) throw new InvalidOperationException("Live verification thermal/RPM cutoff: " + name);
                }
                samples.Add(new { at = DateTimeOffset.UtcNow, readings });
            }
            var fanRelease = await Apply(fan.Id, StandardCapabilities.Release, new { });
            if (!fanRelease.GetProperty("readback").GetProperty("restorationVerified").GetBoolean()) throw new InvalidOperationException("Fan baseline register restoration was not verified.");
            fanAttempted = false;
            var restoredSnapshot = await Snapshot();
            var restoredSession = restoredSnapshot.Devices.Single(d => d.Id == fan.Id).Metadata!.Value.GetProperty("session");
            if (restoredSession.GetProperty("active").GetBoolean() || !restoredSession.GetProperty("restorationVerified").GetBoolean())
                throw new InvalidOperationException("Ordinary snapshots did not reflect verified fan restoration.");
            records.Add(new { stage = "fan-continuous-smoke", seconds = 30, start, samples, release = fanRelease,
                pump = "read-only", longDurationAccepted = false });
            Console.WriteLine("PASS live continuous fan smoke; baseline registers restored; pump read-only.");
            completed = true;
        }
        finally
        {
            var cleanupErrors = new List<string>();
            if (rulesAttempted) try { await Rpc(StandardMethods.AutomationWrite, new { schemaVersion = 1, rules = originalRules }); } catch (Exception e) { cleanupErrors.Add("rules: " + e.Message); }
            if (probe is not null)
            {
                try { await Apply(policy.Id, StandardCapabilities.Release, new { }); } catch (Exception e) { cleanupErrors.Add("policy: " + e.Message); }
                try { if (!probe.HasExited) { probe.Kill(); await probe.WaitForExitAsync(); } } finally { probe.Dispose(); }
            }
            if (fanAttempted) try { var release = await Apply(fan.Id, StandardCapabilities.Release, new { }); if (!release.GetProperty("readback").GetProperty("restorationVerified").GetBoolean()) cleanupErrors.Add("fan restoration unverified"); } catch (Exception e) { cleanupErrors.Add("fan: " + e.Message); }
            foreach (var id in changedLights) try { await RestoreLight(id); } catch (Exception e) { cleanupErrors.Add("lighting: " + e.Message); }
            await File.WriteAllTextAsync(Path.Combine(directory, "live-verification.json"), JsonSerializer.Serialize(new {
                at = DateTimeOffset.UtcNow, completed = completed && cleanupErrors.Count == 0, records, cleanupErrors,
                limits = new[] { "No physical light observation", "No suspend/resume or power-loss test", "30-second fan smoke only" } }, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
            if (cleanupErrors.Count > 0) throw new InvalidOperationException("Cleanup incomplete: " + string.Join("; ", cleanupErrors));
        }
    }
}
