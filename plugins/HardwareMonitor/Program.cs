using System.Security.Principal;
using System.Text.Json;
using ControlCenter.Contracts;
using LibreHardwareMonitor.Hardware;

if (args is ["--service-self-test"]) return await ServiceMonitorSelfTests.RunAsync();
if (args is ["--fan-self-test"]) return FanSessionSelfTest.Run();
if (args is ["--fan-session", var statePath, var parentId, var profile])
    return await FanSession.RunAsync(statePath, int.Parse(parentId), profile);
if (args is ["--fan-test", var testDestination, var channelText])
{
    await File.WriteAllTextAsync(testDestination, "{\"status\":\"starting\"}");
    try { var result = await FanCalibration.RunAsync(int.Parse(channelText)); await File.WriteAllTextAsync(testDestination, JsonSerializer.Serialize(result, new JsonSerializerOptions(Wire.Json) { WriteIndented = true })); return 0; }
    catch (Exception e) { await File.WriteAllTextAsync(testDestination, JsonSerializer.Serialize(new { status = "failed", error = e.ToString() }, Wire.Json)); return 1; }
}
if (args is ["--snapshot", var destination])
{
    try
    {
        await using var plugin = new MonitorPlugin();
        var value = await plugin.HandleAsync("monitor.snapshot", Wire.Element(new { }), default);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(value, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
        return 0;
    }
    catch (Exception e) { await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(new { error = e.ToString() }, Wire.Json)); return 1; }
}
await PluginServer.RunAsync(new MonitorPlugin());
return 0;

sealed class MonitorPlugin : IDevicePlugin
{
    public PluginHello Hello => new("local.hardware-monitor", "0.2.0");
    private readonly ServiceMonitor monitor = new(new HardwareBackend());
    public Task<object> HandleAsync(string method, JsonElement args, CancellationToken cancellationToken) => monitor.HandleAsync(method, args, cancellationToken);
    public ValueTask DisposeAsync() => monitor.DisposeAsync();
}
