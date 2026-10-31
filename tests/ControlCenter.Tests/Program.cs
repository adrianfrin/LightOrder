using System.Text.Json;
using ControlCenter.Contracts;
using ControlCenter.Core;

var root = args.Length > 0 ? Path.GetFullPath(args[0]) : throw new ArgumentException("Supply published app directory");
var passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); passed++; }
async Task ExpectError(Func<Task> action, string message)
{
    try { await action(); } catch { Check(true, message); return; }
    throw new Exception("Expected failure: " + message);
}
var silent = FanProfile.Silent;
Check(silent.Evaluate(40) == (40f,25f), "independent silent low-temperature duties");
Check(silent.Evaluate(60) == (40f,25f) && silent.Evaluate(79.9f) == (40f,25f), "silent fixed minimum below emergency");
Check((silent with { Mode = "curve" }).Evaluate(60) == (50f,42.5f), "independent custom curve interpolation");
var manual = silent with { Mode = "manual" };
Check(manual.Evaluate(75) == (40f,25f) && manual.Evaluate(80) == (100f,100f), "manual fixed duty and independent emergency override");
await ExpectError(() => { (manual with { CasePercent = 0 }).Validate(); return Task.CompletedTask; }, "fan-stop below calibrated range rejected");
await ExpectError(() => { (silent with { RadiatorCurve = [new(50,50),new(40,60),new(80,100)] }).Validate(); return Task.CompletedTask; }, "unsorted fan curve rejected");
await ExpectError(() => { (silent with { CasePercent = float.NaN }).Validate(); return Task.CompletedTask; }, "nonfinite custom duty rejected");
SettingsStore.ValidateColor("#12ABef");
Check(true, "mixed-case RGB hex accepted");
await ExpectError(() => { SettingsStore.ValidateColor("#XYZ123"); return Task.CompletedTask; }, "invalid RGB rejected");
await ExpectError(() => { SettingsStore.ValidateBrightness(double.NaN); return Task.CompletedTask; }, "NaN brightness rejected");
await ExpectError(() => { SettingsStore.ValidateBrightness(1.01); return Task.CompletedTask; }, "out-of-range brightness rejected");
Check(await PluginServer.ReadLineLimitedAsync(new StringReader("{}\n"), default) == "{}", "JSON line parsed");
await ExpectError(async () => await PluginServer.ReadLineLimitedAsync(new StringReader("unfinished"), default), "truncated message rejected");
await ExpectError(async () => await PluginServer.ReadLineLimitedAsync(new StringReader(new string('x', Wire.MaxLine + 1)), default), "oversize message rejected");
var directory = Path.Combine(root, "plugins", "ExampleDevice");
var fixture = Path.Combine(directory, "test-plugin.json");
var manifest = new PluginManifest("local.example", "test", "0.1.0", 1, "ControlCenter.ExampleDevice.exe", [], []);
await File.WriteAllTextAsync(fixture, JsonSerializer.Serialize(manifest, Wire.Json));
try
{
    await using (var client = await PluginClient.StartAsync(fixture))
    {
        var devices = Wire.Read<DeviceInfo[]>(await client.CallAsync("device.discover"));
        Check(devices.Length == 1 && devices[0].Kind == "simulation", "real plugin process discovered simulation explicitly");
        var result = await client.CallAsync("device.apply", new { deviceId = devices[0].Id, operationId = "test", color = "#123456", brightness = 1.0, off = false });
        Check(result.GetProperty("status").GetString() == "simulated", "simulation cannot claim hardware success");
        var state = await client.CallAsync("device.read");
        Check(state.GetProperty("color").GetString() == "#123456", "round-trip plugin state");
        await ExpectError(() => client.CallAsync("unknown.method"), "unknown RPC method rejected");
        Check((await client.CallAsync("plugin.hello")).GetProperty("id").GetString() == "local.example", "plugin remains usable after application error");
    }
    await using (var client = await PluginClient.StartAsync(fixture))
    {
        await ExpectError(() => client.CallAsync("test.crash"), "child crash contained by host");
    }
    await using (var client = await PluginClient.StartAsync(fixture))
    {
        await ExpectError(() => client.CallAsync("test.delay", timeoutSeconds: 1), "timeout is surfaced");
        await ExpectError(() => client.CallAsync("plugin.hello"), "timed-out connection cannot misroute later response");
    }
    await File.WriteAllTextAsync(fixture, JsonSerializer.Serialize(manifest with { EntryPoint = "../outside.exe" }, Wire.Json));
    await ExpectError(async () => await PluginClient.StartAsync(fixture), "entry path traversal rejected");
    await File.WriteAllTextAsync(fixture, JsonSerializer.Serialize(manifest with { ProtocolMajor = 99 }, Wire.Json));
    await ExpectError(async () => await PluginClient.StartAsync(fixture), "incompatible protocol rejected before launch");
}
finally { File.Delete(fixture); }
Console.WriteLine($"PASS TOTAL {passed}");
