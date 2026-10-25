using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlCenter.Contracts;

namespace ControlCenter.OpenRgb;

internal sealed record LightingRequest(string DeviceId, string OperationId, string Color, double Brightness, bool Off);
internal sealed record DeviceBinding(string Id, RgbDevice Device, bool Ambiguous);
internal sealed record CacheReadback(string Source, string DeviceId, string Mode, string[] Colors, bool MatchesRequested, string PhysicalActual = "unknown");

public sealed class OpenRgbPlugin : IDevicePlugin
{
    public PluginHello Hello => new("local.openrgb", "0.1.0");
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly BackendSettings settings;
    private readonly string settingsPath;
    private readonly ManagedBackend backend;
    private readonly int port;
    private readonly Dictionary<string, DeviceBinding> discovered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (LightingRequest Request, ApplyResult Result)> operations = new(StringComparer.Ordinal);
    private bool disposed;
    private readonly Dictionary<string, object> lastCommands = new(StringComparer.Ordinal);
    public OpenRgbPlugin()
    {
        (settings, settingsPath) = BackendSettings.Load();
        backend = new(settings); port = ManagedBackend.Port;
    }
    // Mock tests use an ephemeral loopback port; production never accepts a port from RPC/settings.
    internal OpenRgbPlugin(int testPort)
    {
        settings = new() { RequestTimeoutMs = 1000 }; settingsPath = "mock";
        backend = new(settings); port = testPort;
    }
    public async Task<object> HandleAsync(string method, JsonElement args, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(settings.RequestTimeoutMs);
            var token = deadline.Token;
            return method switch
            {
                "backend.status" => await StatusAsync(token),
                "backend.start" => await StartAsync(token),
                "backend.stop-unused" => await StopUnusedAsync(token),
                "device.discover" => await DiscoverStandardAsync(token),
                "device.apply" => await ApplyAsync(ParseRequest(args), token),
                "device.execute" => await ExecuteAsync(Wire.Read<DeviceCommand>(args), token),
                "telemetry.read" => Array.Empty<object>(),
                "device.read" => new ApplyResult("unsupported", "内存灯光没有独立物理回读；OpenRGB 缓存不能证明实际颜色。"),
                "device.release" => new ApplyResult("accepted", "已释放应用控制；未恢复未知的原始灯光。"),
                _ => throw new PluginException("MethodNotFound", $"Unknown method: {method}")
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new PluginException("BackendTimeout", "OpenRGB request timed out. A write may have been queued; no automatic retry was made."); }
        catch (SocketException e) { throw new PluginException("BackendUnavailable", $"OpenRGB loopback SDK is unavailable: {e.Message}. Use backend.start explicitly. {ManagedBackend.AdminHelp}"); }
        catch (IOException e) { throw new PluginException("BackendUnavailable", $"OpenRGB connection or file access failed: {e.Message}"); }
        finally { gate.Release(); }
    }
    private async Task<object> StopUnusedAsync(CancellationToken token)
    {
        await using (var connection = await OpenRgbConnection.ConnectAsync(port, token))
        {
            if ((await connection.DiscoverAsync(token)).Length != 0)
                throw new PluginException("BackendBusy", "后端已经发现设备，拒绝自动停止。");
        }
        await backend.StopUnusedAsync(token);
        return new ApplyResult("accepted", "已停止本插件启动且无设备、无写入的后端，准备管理员启动。");
    }
    private async Task<object> StartAsync(CancellationToken token)
    {
        await backend.StartAsync(token);
        // Return immediately; hardware discovery in OpenRGB can take time.
        return new { status = "starting", ownedProcessId = backend.OwnedPid, host = "127.0.0.1", port,
            configDirectory = backend.ConfigDirectory, detail = "OpenRGB launched. Call backend.status to check SDK readiness.", elevationHelp = ManagedBackend.AdminHelp };
    }
    private async Task<object> StatusAsync(CancellationToken token)
    {
        bool connected = false; uint? serverVersion = null; string? error = null; string? errorCode = null;
        try
        {
            await using var connection = await OpenRgbConnection.ConnectAsync(port, token);
            connected = true; serverVersion = connection.ServerVersion;
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or PluginException)
        {
            error = e is OperationCanceledException ? "OpenRGB SDK probe timed out" : e.Message;
            errorCode = e is PluginException pe ? pe.Code : "BackendUnavailable";
        }
        return new { status = connected ? "connected" : backend.OwnedPid != null ? "starting" : "unavailable", connected,
            host = "127.0.0.1", port, protocolVersion = connected ? (int?)3 : null, serverProtocolVersion = serverVersion,
            ownedProcessId = backend.OwnedPid, writesMayBePending = backend.WritesMayBePending,
            executablePath = settings.ExecutablePath, settingsPath, configDirectory = backend.ConfigDirectory,
            errorCode, error, pawnIoState = settingsPath == "mock" ? "mock" : ManagedBackend.PawnIoState(), recentLog = backend.RecentLog, elevationHelp = ManagedBackend.AdminHelp, physicalActual = "unknown" };
    }
    private async Task<object> ExecuteAsync(DeviceCommand command, CancellationToken token)
    {
        if (command.Capability is not ("lighting.static" or "lighting.off")) throw new PluginException("UnsupportedCapability", "Unsupported lighting capability");
        var p = command.Parameters; var off = command.Capability == "lighting.off";
        var request = ParseRequest(Wire.Element(new { deviceId = command.DeviceId, operationId = p.TryGetProperty("operationId", out var id) ? id.GetString() : Guid.NewGuid().ToString("N"), color = off ? "#000000" : p.GetProperty("color").GetString(), brightness = off ? 0 : p.GetProperty("brightness").GetDouble(), off }));
        var result = await ApplyAsync(request, token);
        if (result.Status == "accepted") lastCommands[command.DeviceId] = new { color = request.Color, brightness = request.Brightness, effect = off ? "off" : "static" };
        return result;
    }
    private async Task<object> DiscoverStandardAsync(CancellationToken token)
    {
        // The mock transport retains the legacy DTO for protocol fixture assertions.
        if (settingsPath == "mock") return await DiscoverAsync(token);
        try
        {
            await using var probe = await OpenRgbConnection.ConnectAsync(port, token);
        }
        catch (SocketException)
        {
            await backend.StartAsync(token);
            for (var attempt = 0; attempt < 12; attempt++)
            {
                await Task.Delay(250, token);
                try { await using var probe = await OpenRgbConnection.ConnectAsync(port, token); break; }
                catch (SocketException) when (attempt < 11) { }
            }
        }
        var devices = await DiscoverAsync(token);
        return devices.Select(d => new StandardDevice(d.Id, d.Name, d.Kind, d.Capabilities, d.Status, d.Detail,
            Wire.Element(new { schemaVersion = 1, effects = new[] { "static", "off" }, current = lastCommands.GetValueOrDefault(d.Id), stateSource = lastCommands.ContainsKey(d.Id) ? "last-command" : "unknown", readback = false }))).ToArray();
    }
    private async Task<DeviceInfo[]> DiscoverAsync(CancellationToken token)
    {
        // Clear stale bindings even if the replacement discovery fails.
        discovered.Clear();
        await using var connection = await OpenRgbConnection.ConnectAsync(port, token);
        var bindings = Bind(await connection.DiscoverAsync(token));
        foreach (var binding in bindings) discovered.Add(binding.Id, binding);
        return bindings.Select((b, index) => new DeviceInfo(b.Id, $"内存灯光 {index + 1} · {b.Device.Name}", "memory",
            b.Ambiguous || b.Device.StaticMode < 0 || b.Device.Colors.Length == 0 ? [] : ["lighting.static", "lighting.off"],
            b.Ambiguous ? "ambiguous" : b.Device.StaticMode < 0 || b.Device.Colors.Length == 0 ? "unsupported" : "ready",
            b.Ambiguous ? $"Ambiguous identity at SDK index {b.Device.Index}; writes refused. {b.Device.Location}"
                : $"ENE DRAM; {b.Device.Location}; {b.Device.Colors.Length} LEDs. SDK cache only; physical light state unknown.")).ToArray();
    }
    internal static DeviceBinding[] Bind(RgbDevice[] devices)
    {
        var targets = devices.Where(d => d.IsEneDram).ToArray();
        // Location+name survives SDK reordering. Index only distinguishes ambiguous display entries;
        // it is deliberately never accepted as sufficient identity for a write.
        var groups = targets.GroupBy(d => Identity(d), StringComparer.Ordinal);
        var result = new List<DeviceBinding>();
        foreach (var group in groups)
        {
            var ambiguous = group.Count() != 1 || string.IsNullOrWhiteSpace(group.First().Location);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(group.Key))).ToLowerInvariant();
            foreach (var d in group)
                result.Add(new($"ene-dram:{hash}" + (ambiguous ? $":index-{d.Index}" : ""), d, ambiguous));
        }
        return result.OrderBy(b => b.Device.Index).ToArray();
    }
    private static string Identity(RgbDevice d) => JsonSerializer.Serialize(new[] { d.Location.Trim(), d.Name.Trim() });
    private static bool SameDevice(RgbDevice a, RgbDevice b) => a.IsEneDram && b.IsEneDram && Identity(a) == Identity(b) && a.Serial == b.Serial && a.Version == b.Version && a.Colors.Length == b.Colors.Length;
    private static LightingRequest ParseRequest(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("brightness", out var brightness) || !brightness.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0 || number > 1
            || !args.TryGetProperty("off", out var off) || off.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new PluginException("InvalidArgument", "brightness (0..1) and off (boolean) are required");
        var request = Wire.Read<LightingRequest>(args);
        if (string.IsNullOrWhiteSpace(request.DeviceId) || request.DeviceId.Length > 256 || string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 128
            || request.Color is not { Length: 7 } || request.Color[0] != '#' || !uint.TryParse(request.Color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            throw new PluginException("InvalidArgument", "deviceId, operationId (1..128 characters) and color '#RRGGBB' are required");
        return request with { Color = request.Color.ToUpperInvariant() };
    }
    private async Task<ApplyResult> ApplyAsync(LightingRequest request, CancellationToken token)
    {
        if (operations.TryGetValue(request.OperationId, out var previous))
        {
            if (previous.Request != request) throw new PluginException("OperationConflict", "operationId was already used with different parameters");
            return previous.Result;
        }
        if (!discovered.TryGetValue(request.DeviceId, out var known)) throw new PluginException("DeviceNotFound", "Discover the current device before applying");
        if (known.Ambiguous) throw new PluginException("AmbiguousDevice", "Controller identity is ambiguous; no write was sent");
        await using var connection = await OpenRgbConnection.ConnectAsync(port, token);
        var current = Bind(await connection.DiscoverAsync(token)).SingleOrDefault(b => b.Id == request.DeviceId);
        if (current == null || current.Ambiguous || !SameDevice(known.Device, current.Device))
            throw new PluginException("DeviceChanged", "Controller identity changed; rediscover before applying");
        var device = current.Device;
        var modeIndex = device.StaticMode;
        if (modeIndex < 0 || device.Colors.Length == 0) throw new PluginException("Unsupported", "ENE controller has no supported Direct/Static per-LED mode");
        // Revalidate the selected index immediately before writing; v3 has no atomic identity+write primitive.
        if (!SameDevice(device, await connection.ReadDeviceAsync(device.Index, token))) throw new PluginException("DeviceChanged", "SDK index changed before write");
        var parsed = uint.Parse(request.Color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var scale = request.Off ? 0 : request.Brightness;
        uint Channel(uint value) => (uint)Math.Round((value & 255) * scale, MidpointRounding.AwayFromZero);
        var color = Channel(parsed >> 16) | (Channel(parsed >> 8) << 8) | (Channel(parsed) << 16);
        // Remember uncertainty before the first socket write. Repeated operationIds never replay a partial write.
        var result = new ApplyResult("unknown", "A write may have been queued. Physical light state is unknown; no automatic retry.");
        Remember(request, result);
        backend.WritesMayBePending = true;
        try
        {
            await connection.SendAsync(device.Index, 1101, RgbCodec.Mode(modeIndex, device.Modes[modeIndex]), token);
            await connection.SendAsync(device.Index, 1050, RgbCodec.Colors(device.Colors.Length, color), token);
            // SDK replies expose backend cached values and can race queued writes. Never call this physical verification.
            var readback = await connection.ReadDeviceAsync(device.Index, token);
            var same = SameDevice(device, readback);
            var matches = same && readback.ActiveMode == modeIndex && readback.Colors.All(c => (c & 0xFFFFFF) == color);
            var mode = readback.ActiveMode < readback.Modes.Length ? readback.Modes[readback.ActiveMode].Name : "unknown";
            result = new("accepted", matches ? "Commands sent; OpenRGB cache matches. Physical lighting is unverified."
                : "Commands sent; OpenRGB cache has not confirmed the requested values. Physical lighting is unverified.",
                new CacheReadback("openrgb-cache", request.DeviceId, mode, readback.Colors.Select(RgbCodec.Hex).ToArray(), matches));
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or PluginException)
        { result = new("unknown", $"Write/readback interrupted: {e.Message}. Some commands may have been queued; physical lighting is unknown."); }
        Remember(request, result);
        return result;
    }
    private void Remember(LightingRequest request, ApplyResult result)
    {
        if (!operations.ContainsKey(request.OperationId) && operations.Count >= 256) operations.Remove(operations.Keys.First());
        operations[request.OperationId] = (request, result);
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (disposed) return; disposed = true; await backend.DisposeAsync(); }
        finally { gate.Release(); }
    }
}
