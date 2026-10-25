using System.Text.Json;
using ControlCenter.Contracts;

namespace ControlCenter.GigabyteIt5702;

internal sealed class It5702Plugin : IDevicePlugin
{
    private readonly object gate = new();
    private readonly Dictionary<string, ControllerLease> leases = new(StringComparer.Ordinal);
    private readonly OperationCache operations = new();
    private bool disposed;
    private readonly Dictionary<string, object> lastCommands = new(StringComparer.Ordinal);
    public PluginHello Hello { get; } = new("local.gigabyte-it5702", "0.1.0");

    public Task<object> HandleAsync(string method, JsonElement args, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows()) throw new PluginException("PlatformUnsupported", "IT5702 requires Windows HID APIs.");
            object result = method switch
            {
                "device.discover" => Discover(),
                "device.identify" => Identify(args),
                "device.apply" => Apply(LightingRequest.Parse(args), cancellationToken),
                "device.execute" => Execute(Wire.Read<DeviceCommand>(args), cancellationToken),
                "telemetry.read" => Array.Empty<object>(),
                "device.read" => Read(args),
                "device.release" => Release(args),
                _ => throw new PluginException("MethodNotFound", $"Unsupported method: {method}")
            };
            return Task.FromResult(result);
        }
    }

    private StandardDevice[] Discover() => HidTransport.Enumerate().Select(endpoint => new StandardDevice(
        endpoint.Id, "主板与机箱灯光", "lighting", ["lighting.static", "lighting.off"], "available",
        "Controller identity is checked before every write. Physical color readback is unavailable.",
        Wire.Element(new { schemaVersion = 1, effects = new[] { "static", "off" }, current = lastCommands.GetValueOrDefault(endpoint.Id), stateSource = lastCommands.ContainsKey(endpoint.Id) ? "last-command" : "unknown", readback = false }))).ToArray();

    private object Execute(DeviceCommand command, CancellationToken token)
    {
        if (command.Capability is not ("lighting.static" or "lighting.off")) throw new PluginException("UnsupportedCapability", "Unsupported lighting capability");
        var p = command.Parameters;
        var off = command.Capability == "lighting.off";
        var color = off ? "#000000" : p.GetProperty("color").GetString()!;
        var brightness = off ? 0 : p.GetProperty("brightness").GetDouble();
        var operation = p.TryGetProperty("operationId", out var id) ? id.GetString()! : Guid.NewGuid().ToString("N");
        return Apply(LightingRequest.Parse(Wire.Element(new { deviceId = command.DeviceId, operationId = operation, color, brightness, off })), token);
    }

    private object Identify(JsonElement args)
    {
        var id = LightingRequest.RequiredString(args, "deviceId", 128);
        var matches = HidTransport.Enumerate().Where(e => e.Id == id).ToArray();
        if (matches.Length != 1) throw new PluginException("DeviceNotFound", "未找到唯一匹配的主板灯控器。");
        using var lease = leases.ContainsKey(id) ? null : new ControllerLease(id);
        using var handle = HidTransport.OpenForApply(matches[0]);
        HidTransport.Send(handle, It5702Protocol.Command(0x60));
        var bytes = HidTransport.ReadIdentification(handle);
        try
        {
            var identity = It5702Protocol.Identify(bytes);
            return new { status = "identified", detail = $"控制器 {identity.Name}，固件 {identity.Firmware}。未发送灯光设置命令。", identity, header = Convert.ToHexString(bytes.AsSpan(0, 12)), lightingVerified = false };
        }
        catch (PluginException e)
        {
            return new { status = "blocked", detail = e.Message, code = e.Code, header = Convert.ToHexString(bytes.AsSpan(0, 12)), lightingVerified = false };
        }
    }

    private ApplyResult Apply(LightingRequest request, CancellationToken token)
    {
        if (operations.TryGet(request, out var cached)) return cached!;
        bool sentLighting = false;
        try
        {
            var matches = HidTransport.Enumerate().Where(endpoint => endpoint.Id == request.DeviceId).ToArray();
            if (matches.Length == 0) throw new PluginException("DeviceNotFound", "Requested controller is no longer present. Discover again.");
            if (matches.Length != 1) throw new PluginException("AmbiguousDevice", "Multiple HID endpoints match the requested controller; no commands sent.");
            token.ThrowIfCancellationRequested();
            if (!leases.ContainsKey(request.DeviceId)) leases.Add(request.DeviceId, new(request.DeviceId));
            using var handle = HidTransport.OpenForApply(matches[0]);
            token.ThrowIfCancellationRequested();
            // This is the only identification write; discovery never sends a feature report.
            HidTransport.Send(handle, It5702Protocol.Command(0x60));
            var identification = It5702Protocol.Identify(HidTransport.ReadIdentification(handle));
            // Follow the known local sequence, disabling competing autonomous modes before
            // setting all eight channels. There is deliberately no flash/save command.
            void SendLighting(byte[] packet)
            {
                token.ThrowIfCancellationRequested();
                sentLighting = true;
                HidTransport.Send(handle, packet);
            }
            if (identification.HasLampArray) SendLighting(It5702Protocol.Command(0x48));
            SendLighting(It5702Protocol.Command(0x31));
            SendLighting(It5702Protocol.Command(0x32));
            for (int channel = 0; channel < 8; channel++)
                SendLighting(It5702Protocol.StaticReport(channel, request.Color, request.Brightness, request.Off));
            SendLighting(It5702Protocol.Command(0x28, 0xff));
            var result = new ApplyResult("accepted", $"HID 已接受八个通道的{(request.Off ? "关闭灯光" : "静态颜色")}命令。" +
                $"控制器：{identification.Name}；固件：{identification.Firmware}。" +
                "不支持读取或验证实际灯光状态。未发送写入闪存或持久保存命令。");
            operations.Add(request, result, null);
            lastCommands[request.DeviceId] = new { color = request.Color, brightness = request.Brightness, effect = request.Off ? "off" : "static" };
            return result;
        }
        catch (Exception exception)
        {
            string code = exception is PluginException pe ? pe.Code : exception is OperationCanceledException ? "Cancelled" : "DeviceError";
            var error = new PluginException(code, exception.Message + (sentLighting
                ? " 部分灯光命令可能已被接受，最终状态未知。未尝试回滚。"
                : " 未发送灯光命令，但可能已请求识别控制器。") +
                " 此 operationId 已保留；如需重新执行，请使用新的 operationId。");
            operations.Add(request, null, error);
            throw error;
        }
    }

    private static ApplyResult Read(JsonElement args)
    {
        LightingRequest.RequiredString(args, "deviceId", 128);
        return new("unsupported", "不支持读取硬件灯光状态。当前颜色、亮度和开关状态均未知；缓存的命令不能作为状态验证依据。");
    }

    private ApplyResult Release(JsonElement args)
    {
        if (!args.TryGetProperty("deviceId", out _))
        {
            foreach (var item in leases.Values) item.Dispose();
            leases.Clear();
            return new ApplyResult("accepted", "Controller leases released; lighting unchanged.");
        }
        string id = LightingRequest.RequiredString(args, "deviceId", 128);
        if (leases.Remove(id, out var lease)) lease.Dispose();
        return new("accepted", "已释放控制器占用。未发送硬件命令，也未恢复之前的灯光设置；当前状态未知。");
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return ValueTask.CompletedTask;
            foreach (var lease in leases.Values) lease.Dispose();
            leases.Clear();
            disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
