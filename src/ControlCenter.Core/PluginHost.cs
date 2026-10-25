using System.Diagnostics;
using System.Text.Json;
using ControlCenter.Contracts;

namespace ControlCenter.Core;

public sealed class PluginClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly SemaphoreSlim gate = new(1, 1);
    private long nextId;
    private bool disposed;
    private bool faulted;
    public PluginManifest Manifest { get; }
    public string DirectoryPath { get; }
    public event Action<string>? Diagnostic;
    private PluginClient(PluginManifest manifest, string directory, Process child)
    {
        Manifest = manifest; DirectoryPath = directory; process = child;
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } text) Diagnostic?.Invoke(text.Length > 2048 ? text[..2048] : text); };
        process.BeginErrorReadLine();
    }
    public static async Task<PluginClient> StartAsync(string manifestPath)
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(await File.ReadAllTextAsync(manifestPath), Wire.Json)
            ?? throw new InvalidDataException("插件清单为空");
        if (!manifest.Enabled) throw new InvalidDataException("插件尚未启用");
        if (manifest.ProtocolMajor != Wire.Version) throw new InvalidDataException("插件协议主版本不兼容");
        if (string.IsNullOrWhiteSpace(manifest.Id) || manifest.Arguments is null || manifest.Permissions is null)
            throw new InvalidDataException("插件清单缺少必需字段");
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var entry = Path.GetFullPath(Path.Combine(directory, manifest.EntryPoint));
        if (!entry.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(manifest.EntryPoint))
            throw new InvalidDataException("插件入口必须位于插件目录内");
        if (!File.Exists(entry)) throw new FileNotFoundException("插件可执行文件不存在", entry);
        var info = new ProcessStartInfo(entry)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false)
        };
        foreach (var arg in manifest.Arguments) info.ArgumentList.Add(arg);
        var child = Process.Start(info) ?? throw new IOException("无法启动插件");
        var client = new PluginClient(manifest, directory, child);
        try
        {
            var hello = Wire.Read<PluginHello>(await client.CallAsync("plugin.hello", new { protocolMajor = Wire.Version }));
            if (hello.ProtocolMajor != Wire.Version || hello.Id != manifest.Id) throw new InvalidDataException("插件身份或协议握手不匹配");
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }
    public async Task<JsonElement> CallAsync(string method, object? args = null, int timeoutSeconds = 10)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (faulted) throw new IOException("插件连接已失效，请重新打开应用；上一操作结果可能未知");
        await gate.WaitAsync();
        try
        {
            if (process.HasExited) throw new IOException($"插件已退出，代码 {process.ExitCode}");
            var id = Interlocked.Increment(ref nextId);
            var request = JsonSerializer.Serialize(new RpcRequest("2.0", id, method, Wire.Element(args ?? new { })), Wire.Json);
            if (request.Length > Wire.MaxLine) throw new ArgumentException("请求超过协议上限");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            await process.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            var line = await PluginServer.ReadLineLimitedAsync(process.StandardOutput, timeout.Token) ?? throw new IOException("插件关闭了连接");
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.GetProperty("jsonrpc").GetString() != "2.0" || root.GetProperty("id").GetInt64() != id)
                throw new InvalidDataException("插件返回了不匹配的响应");
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                throw new PluginException(error.GetProperty("data").GetString() ?? "DeviceError", error.GetProperty("message").GetString() ?? "插件调用失败");
            return root.GetProperty("result").Clone();
        }
        catch (OperationCanceledException)
        {
            faulted = true;
            // Closing stdin requests graceful cleanup once an in-flight operation finishes.
            process.StandardInput.Close();
            throw new PluginException("TimeoutOutcomeUnknown", "插件响应超时；操作结果未知，未自动重试。请检查设备后重新打开应用。");
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException)
        {
            faulted = true;
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
            throw;
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        try
        {
            if (!faulted && !process.HasExited) await CallAsync("plugin.shutdown", timeoutSeconds: 8);
        }
        catch (Exception e) { Diagnostic?.Invoke("插件退出：" + e.Message); }
        disposed = true;
        try
        {
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Diagnostic?.Invoke("插件未按时退出；终止其进程，恢复状态需查看诊断。");
            if (!process.HasExited) process.Kill(true);
        }
        finally { process.Dispose(); }
    }
}

public sealed record ConnectedDevice(PluginClient Plugin, DeviceInfo Device)
{
    public override string ToString() => Device.Name;
}
public sealed class ControlCenterHost : IAsyncDisposable
{
    public List<PluginClient> Plugins { get; } = [];
    public List<ConnectedDevice> Devices { get; } = [];
    public event Action<string>? Log;
    public async Task StartAsync(string root)
    {
        if (!Directory.Exists(root)) { Log?.Invoke("插件目录不存在：" + root); return; }
        foreach (var manifest in Directory.EnumerateFiles(root, "plugin.json", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, "plugin.json")).Where(File.Exists)))
        {
            try
            {
                var settings = JsonSerializer.Deserialize<PluginManifest>(await File.ReadAllTextAsync(manifest), Wire.Json);
                if (settings?.Enabled != true) continue;
                if (Plugins.Any(p => p.Manifest.Id == settings.Id)) throw new InvalidDataException("重复插件 ID");
                var client = await PluginClient.StartAsync(manifest);
                client.Diagnostic += text => Log?.Invoke($"{client.Manifest.Name}：{text}");
                Plugins.Add(client);
                Log?.Invoke($"已连接 {client.Manifest.Name} {client.Manifest.Version}");
            }
            catch (Exception e) { Log?.Invoke($"加载 {Path.GetFileName(Path.GetDirectoryName(manifest))} 失败：{e.Message}"); }
        }
        await DiscoverAsync();
    }
    public async Task DiscoverAsync()
    {
        Devices.Clear();
        foreach (var plugin in Plugins)
        {
            try
            {
                var devices = Wire.Read<DeviceInfo[]>(await plugin.CallAsync("device.discover", timeoutSeconds: 20));
                foreach (var device in devices)
                {
                    var current = device;
                    Devices.Add(new(plugin, current));
                }
                Log?.Invoke($"{plugin.Manifest.Name}：发现 {devices.Length} 个设备");
            }
            catch (Exception e) { Log?.Invoke($"{plugin.Manifest.Name}：{e.Message}"); }
        }
    }
    public PluginClient GetPlugin(string id) => Plugins.SingleOrDefault(p => p.Manifest.Id == id)
        ?? throw new InvalidOperationException("对应插件尚未连接");
    public async ValueTask DisposeAsync()
    {
        foreach (var plugin in Plugins.AsEnumerable().Reverse()) await plugin.DisposeAsync();
        Plugins.Clear();
    }
}
