using System.Text.Json;
using ControlCenter.Contracts;
await PluginServer.RunAsync(new ExamplePlugin());
sealed class ExamplePlugin : IDevicePlugin
{
    public PluginHello Hello => new("local.example", "0.1.0");
    private string color = "#000000";
    public Task<object> HandleAsync(string method, JsonElement args, CancellationToken cancellationToken)
    {
        object result = method switch
        {
            "device.discover" => new[] { new DeviceInfo("example:virtual-light", "模拟灯光（无真实硬件）", "simulation", ["lighting.static", "lighting.off"], "simulated", "仅用于验证插件协议，不能证明实机支持") },
            "device.read" => new { color, simulated = true },
            "device.apply" => Apply(args),
            StandardMethods.Execute => Execute(Wire.Read<DeviceCommand>(args)),
            StandardMethods.Telemetry => Array.Empty<object>(),
            "device.release" => new ApplyResult("simulated", "模拟设备已释放"),
            "test.crash" => Crash(),
            "test.delay" => Delay(),
            _ => throw new PluginException("UnsupportedCapability", "未知方法")
        };
        return Task.FromResult(result);
    }
    private object Execute(DeviceCommand command)
    {
        if (command.Capability is not (StandardCapabilities.LightingStatic or StandardCapabilities.LightingOff)) throw new PluginException("UnsupportedCapability", "Unsupported example capability");
        return Apply(Wire.Element(new { deviceId = command.DeviceId, color = command.Capability == StandardCapabilities.LightingOff ? "#000000" : command.Parameters.GetProperty("color").GetString(), off = command.Capability == StandardCapabilities.LightingOff }));
    }
    private object Apply(JsonElement args)
    {
        if (args.GetProperty("deviceId").GetString() != "example:virtual-light") throw new PluginException("DeviceDisconnected", "模拟设备不存在");
        color = args.GetProperty("off").GetBoolean() ? "#000000" : args.GetProperty("color").GetString()!;
        return new ApplyResult("simulated", "只改变模拟状态，没有硬件写入", new { color });
    }
    private static object Crash() { Environment.Exit(17); return new { }; }
    private static object Delay() { Thread.Sleep(3000); return new { }; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
