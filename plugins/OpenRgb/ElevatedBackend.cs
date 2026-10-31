using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using ControlCenter.Contracts;

namespace ControlCenter.OpenRgb;

internal static class ElevatedBackend
{
    public static string ResultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IndependentControlCenter", "OpenRgb", "elevated-start.json");
    public static async Task<int> RunAsync()
    {
        object result;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new PluginException("PermissionRequired", "请通过应用的管理员启动按钮启动此助手。");
            var (settings, _) = BackendSettings.Load();
            if (Process.GetProcessesByName("OpenRGB").Length != 0)
                throw new PluginException("BackendAlreadyRunning", "OpenRGB 已在运行，请先关闭它，避免同时访问内存总线。");
            // An explicit UAC action starts only an existing driver. It never installs one.
            var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            startInfo.ArgumentList.Add("start"); startInfo.ArgumentList.Add("PawnIO");
            if (DevicePolicy.Detectors(settings).Any(d => d.Contains("SMBus", StringComparison.OrdinalIgnoreCase)))
            using (var driver = Process.Start(startInfo)!)
            {
                var stdout = driver.StandardOutput.ReadToEndAsync(); var stderr = driver.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await driver.WaitForExitAsync(timeout.Token);
                var text = await stdout + await stderr;
                if (driver.ExitCode != 0 && driver.ExitCode != 1056) throw new PluginException("DriverUnavailable", "无法启动已有 PawnIO：" + text);
            }
            var backend = new ManagedBackend(settings);
            await backend.StartAsync(CancellationToken.None);
            // The helper remains the owner and drains OpenRGB output until that engine exits.
            // Parent UI accesses the loopback SDK; no privileged RPC or arbitrary shell command is exposed.
            result = new { status = "starting", pid = backend.OwnedPid, configDirectory = backend.ConfigDirectory, detail = "已启动独立内存后端，等待设备发现。PawnIO 保持按需运行。" };
            await SaveAsync(result);
            if (backend.OwnedPid is int pid)
            {
                using var process = Process.GetProcessById(pid);
                await process.WaitForExitAsync();
            }
            await backend.DisposeAsync();
            return 0;
        }
        catch (Exception e)
        {
            result = new { status = "failed", error = e is PluginException pe ? pe.Code : "BackendError", detail = e.Message };
            await SaveAsync(result);
            return 1;
        }
    }
    private static async Task SaveAsync(object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ResultPath)!);
        await File.WriteAllTextAsync(ResultPath, JsonSerializer.Serialize(value, Wire.Json));
    }
}
