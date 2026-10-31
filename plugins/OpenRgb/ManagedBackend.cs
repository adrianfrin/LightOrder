using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using ControlCenter.Contracts;

namespace ControlCenter.OpenRgb;

internal sealed record BackendSettings
{
    public string ExecutablePath { get; init; } = Path.Combine(AppContext.BaseDirectory, "runtime", "OpenRGB.exe");
    public string SourceConfigPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "detectors.json");
    public string DataDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LightOrder", "OpenRgb");
    public int RequestTimeoutMs { get; init; } = 5000;
    public bool EnableEneDram { get; init; } = true;
    public DeviceRule[] AdditionalDevices { get; init; } = [];
    internal static string SettingsPath(string baseDirectory) => Path.Combine(baseDirectory, "backend.json");
    public static (BackendSettings Settings, string Path) Load()
    {
        // The service runs as SYSTEM. Never consult environment or per-user overrides.
        var path = SettingsPath(AppContext.BaseDirectory);
        ProtectedPaths.Require(path);
        if (File.Exists(path) && new FileInfo(path).Length > 65536) throw new PluginException("ConfigurationError", "backend.json exceeds 64 KiB");
        var settings = File.Exists(path) ? Parse(File.ReadAllText(path)) : new();
        Validate(settings);
        foreach (var item in new[] { settings.ExecutablePath, settings.SourceConfigPath, settings.DataDirectory }) ProtectedPaths.Require(item);
        return (settings, path);
    }
    internal static BackendSettings Parse(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 65536) throw new PluginException("ConfigurationError", "backend.json exceeds 64 KiB");
        try
        {
            var options = new JsonSerializerOptions(Wire.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
            var settings = JsonSerializer.Deserialize<BackendSettings>(json, options) ?? throw new JsonException("Empty settings");
            Validate(settings);
            return settings;
        }
        catch (JsonException e) { throw new PluginException("ConfigurationError", $"Invalid backend.json: {e.Message}"); }
    }
    private static void Validate(BackendSettings settings)
    {
        foreach (var item in new[] { settings.ExecutablePath, settings.SourceConfigPath, settings.DataDirectory })
            if (string.IsNullOrWhiteSpace(item) || !Path.IsPathFullyQualified(item) || item.StartsWith("\\\\") || item.StartsWith("//")
                || item.Length < 3 || item[1] != ':' || item.AsSpan(2).Contains(':'))
                throw new PluginException("ConfigurationError", "OpenRGB paths must be absolute local drive paths without alternate streams");
        if (!Path.GetFileName(settings.ExecutablePath).Equals("OpenRGB.exe", StringComparison.OrdinalIgnoreCase))
            throw new PluginException("ConfigurationError", "Executable must be named OpenRGB.exe so duplicate-process checks remain effective");
        if (settings.RequestTimeoutMs is < 250 or > 30000) throw new PluginException("ConfigurationError", "requestTimeoutMs must be 250..30000");
        DevicePolicy.Validate(settings);
    }
}

internal sealed class ManagedBackend(BackendSettings settings) : IAsyncDisposable
{
    public const int Port = 16742;
    public const string AdminHelp = "内存访问需要可用的 PawnIO 接口。请使用界面中的‘以管理员权限启动内存后端’；普通连接不会自动提权或安装驱动。";
    private Process? owned;
    private readonly Queue<string> logs = new();
    private readonly object logLock = new();
    public string? ConfigDirectory { get; private set; }
    // v3 has no completed-hardware-write acknowledgement. Never clear this from cache readback.
    public bool WritesMayBePending { get; set; }
    public int? OwnedPid => owned is { HasExited: false } ? owned.Id : null;
    public string[] RecentLog { get { lock (logLock) return logs.ToArray(); } }

    public async Task StartAsync(CancellationToken token)
    {
        var others = Process.GetProcessesByName("OpenRGB");
        try
        {
            if (others.Any(p => p.Id != OwnedPid)) throw new PluginException("BackendAlreadyRunning", "Another OpenRGB process is running. Close it yourself before starting this dedicated backend to avoid simultaneous SMBus access.");
            if (OwnedPid != null) return;
        }
        finally { foreach (var process in others) process.Dispose(); }
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            throw new PluginException("ElevationRequired", "内存后端必须以管理员权限启动；驱动运行或端口可连接不代表具有内存访问权限。");
        if (!File.Exists(settings.ExecutablePath)) throw new PluginException("BackendMissing", $"OpenRGB executable not found: {settings.ExecutablePath}");
        var needsMemoryBus = DevicePolicy.Detectors(settings).Any(d => d.Contains("SMBus", StringComparison.OrdinalIgnoreCase));
        var driver = needsMemoryBus ? PawnIoState() : "not-required";
        if (needsMemoryBus && driver != "running") throw new PluginException("DriverUnavailable", $"PawnIO is {driver}. Explicitly start the already-installed driver from an Administrator session before starting this backend. No driver was installed or started. {AdminHelp}");
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == Port))
            throw new PluginException("PortInUse", $"Port {Port} is already in use; no process was started");
        if (!File.Exists(settings.SourceConfigPath) || new FileInfo(settings.SourceConfigPath).Length > 4 * 1024 * 1024)
            throw new PluginException("ConfigurationError", "Source OpenRGB.json is missing or exceeds 4 MiB");
        foreach (var path in new[] { settings.ExecutablePath, settings.SourceConfigPath, settings.DataDirectory }) ProtectedPaths.Require(path);
        var clean = SanitizeConfig(await File.ReadAllTextAsync(settings.SourceConfigPath, token), settings);
        // Fresh directory prevents old profiles, plugins, shutdown actions or executable settings leaking in.
        ConfigDirectory = Path.Combine(settings.DataDirectory, "runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ConfigDirectory);
        await File.WriteAllTextAsync(Path.Combine(ConfigDirectory, "OpenRGB.json"), clean.ToJsonString(), token);
        var info = new ProcessStartInfo(settings.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = ConfigDirectory,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var arg in new[] { "--config", ConfigDirectory, "--server", "--server-host", "127.0.0.1", "--server-port", Port.ToString(), "--noautoconnect", "--loglevel", "warning" })
            info.ArgumentList.Add(arg);
        // A named cross-process gate also serializes starts from multiple plugin instances.
        using var gate = new Mutex(false, "Local\\IndependentControlCenter.OpenRgb.Start");
        var acquired = false;
        try
        {
            try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new PluginException("BackendBusy", "Another plugin is starting OpenRGB");
            var running = Process.GetProcessesByName("OpenRGB");
            try { if (running.Length != 0) throw new PluginException("BackendAlreadyRunning", "OpenRGB started concurrently; dedicated launch refused"); }
            finally { foreach (var p in running) p.Dispose(); }
            token.ThrowIfCancellationRequested();
            owned?.Dispose();
            owned = new Process { StartInfo = info };
            owned.OutputDataReceived += (_, e) => AppendLog(e.Data);
            owned.ErrorDataReceived += (_, e) => AppendLog(e.Data);
            if (!owned.Start()) throw new PluginException("BackendStartFailed", "OpenRGB did not start");
            WritesMayBePending = false;
            owned.BeginOutputReadLine(); owned.BeginErrorReadLine();
        }
        catch (Win32Exception e) when (e.NativeErrorCode is 740 or 5)
        { throw new PluginException("ElevationRequired", AdminHelp); }
        finally { if (acquired) gate.ReleaseMutex(); }
        // No startup retry loop. The UI can call backend.status until detection finishes.
    }
    internal static JsonObject SanitizeConfig(string source, BackendSettings? settings = null)
    {
        settings ??= new();
        DevicePolicy.Validate(settings);
        JsonNode? root;
        try { root = JsonNode.Parse(source); } catch (JsonException e) { throw new PluginException("ConfigurationError", e.Message); }
        if (root is not JsonObject obj || obj["Detectors"] is not JsonObject section || section["detectors"] is not JsonObject detectors
            || !detectors.ContainsKey(DevicePolicy.MemoryDetector))
            throw new PluginException("ConfigurationError", "Source config must contain the exact installed detector inventory including ENE SMBus DRAM");
        var allowed = DevicePolicy.Detectors(settings).ToHashSet(StringComparer.Ordinal);
        if (allowed.Any(name => !detectors.ContainsKey(name)))
            throw new PluginException("ConfigurationError", "An explicitly allowed detector is absent from the installed inventory (names are case-sensitive)");
        var cleanDetectors = new JsonObject();
        foreach (var entry in detectors) cleanDetectors[entry.Key] = allowed.Contains(entry.Key);
        return new JsonObject
        {
            ["Detectors"] = new JsonObject { ["detectors"] = cleanDetectors, ["hid_safe_mode"] = true },
            ["Server"] = new JsonObject { ["all_controllers"] = false, ["default_host"] = "127.0.0.1", ["default_port"] = Port, ["legacy_workaround"] = false },
            ["ENESMBusSettings"] = new JsonObject { ["enable_save"] = false }
        };
    }
    public static string PawnIoState()
    {
        if (!OperatingSystem.IsWindows()) return "unsupported";
        var manager = OpenSCManager(null, null, 1); // SC_MANAGER_CONNECT, read-only
        if (manager == IntPtr.Zero) return "unknown";
        try
        {
            var service = OpenService(manager, "PawnIO", 4); // SERVICE_QUERY_STATUS only
            if (service == IntPtr.Zero) return Marshal.GetLastWin32Error() == 1060 ? "not-installed" : "unknown";
            try
            {
                if (!QueryServiceStatus(service, out var status)) return "unknown";
                return status.CurrentState == 4 ? "running" : status.CurrentState == 1 ? "stopped" : "transitioning";
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    private void AppendLog(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (logLock) { if (logs.Count >= 20) logs.Dequeue(); logs.Enqueue(line.Length > 1024 ? line[..1024] : line); }
    }
    public async Task StopUnusedAsync(CancellationToken token)
    {
        if (owned is null || owned.HasExited) throw new PluginException("ExternalBackend", "当前后端不是此插件启动的，请关闭对应 OpenRGB 后再重试。");
        if (WritesMayBePending) throw new PluginException("BackendBusy", "后端已处理灯光写入，不能自动终止。");
        owned.Kill(entireProcessTree: false);
        await owned.WaitForExitAsync(token);
        owned.Dispose(); owned = null;
    }
    public async ValueTask DisposeAsync()
    {
        if (owned != null)
        {
            try
            {
                // All SDK scopes have ended before normal plugin disposal. Close only
                // this retained Process, including after writes; never search by PID/name.
                await OwnedProcessShutdown.CloseAsync(new OwnedProcess(owned), WritesMayBePending);
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or OperationCanceledException) { }
            finally { owned.Dispose(); owned = null; }
        }
    }
}
