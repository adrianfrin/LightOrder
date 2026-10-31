using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LightOrder.Setup;

internal static class WindowsLifecycle
{
    internal static readonly string SystemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
    internal static string Tool(string name) => Path.Combine(SystemDirectory, name);

    internal static void StopService(string name)
    {
        ValidateServiceImage(name);
        // Capture the host before requesting stop, even if SCM reaches STOPPED immediately.
        using var host = FindServiceHost(name);
        Program.Run(Tool("sc.exe"), ["stop", name], [0, 1060, 1062]);
        WaitService(name, stopped: true);
        if (host is not null && !host.WaitForExit(10000)) throw new TimeoutException("LightOrder service host has not exited.");
    }

    private static void ValidateServiceImage(string name)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
        if (key is null) return;
        var args = SplitCommandLine(key.GetValue("ImagePath") as string ?? "");
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LightOrder", "LightOrder.Service.exe");
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || !LifecycleSafety.SamePath(args[0], expected))
            throw new IOException("Existing service does not point to the fixed LightOrder executable; refusing to stop or replace it.");
    }

    private static Process? FindServiceHost(string name)
    {
        using var manager = OpenSCManager(null, null, 1);
        if (manager.IsInvalid) throw new Win32Exception();
        using var service = OpenService(manager, name, 4);
        if (service.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() == 1060) return null;
            throw new Win32Exception();
        }
        if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _)) throw new Win32Exception();
        if (status.ProcessId == 0) return null;
        try { var process = Process.GetProcessById((int)status.ProcessId); _ = process.Handle; return process; }
        catch (ArgumentException) { return null; }
    }

    // Numeric SCM state and service PID are independent of the OS display language.
    internal static void WaitService(string name, bool stopped)
    {
        using var manager = OpenSCManager(null, null, 1);
        if (manager.IsInvalid) throw new Win32Exception();
        using var service = OpenService(manager, name, 4);
        if (service.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() == 1060 && stopped) return;
            throw new Win32Exception();
        }
        Process? host = null;
        try
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(40))
            {
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _)) throw new Win32Exception();
                if (stopped && host is null && status.ProcessId != 0)
                {
                    try { host = Process.GetProcessById((int)status.ProcessId); _ = host.Handle; }
                    catch (ArgumentException) { }
                }
                if (status.State == (stopped ? 1u : 4u))
                {
                    if (stopped && host is not null && !host.WaitForExit(10000)) throw new TimeoutException("Service reports stopped but its process has not exited.");
                    return;
                }
                Thread.Sleep(200);
            }
            throw new TimeoutException("LightOrder service did not reach the required state. Files were not changed.");
        }
        finally { host?.Dispose(); }
    }

    internal static void CloseInstalledProcesses(string installRoot, string dataRoot)
    {
        var ui = Path.Combine(installRoot, "LightOrder.App.exe");
        var backend = Path.Combine(installRoot, "plugins", "OpenRgb", "runtime", "OpenRGB.exe");
        foreach (var name in new[] { "LightOrder.App", "OpenRGB", "ControlCenter.OpenRgb" })
        foreach (var process in Process.GetProcessesByName(name))
        using (process)
        {
            try
            {
                // Open the process handle before inspecting it; never kill by a later PID lookup.
                _ = process.Handle;
                var path = process.MainModule?.FileName;
                var helper = Path.Combine(installRoot, "plugins", "OpenRgb", "ControlCenter.OpenRgb.exe");
                if (path is not null && LifecycleSafety.SamePath(path, helper))
                {
                    // Service plugin exits on service shutdown; elevated helper exits when its
                    // owned backend exits. Wait only; no arbitrary plugin process is killed.
                    if (!process.WaitForExit(10000)) throw new IOException("Installed OpenRGB helper is still running. Close it, then retry.");
                    continue;
                }
                if (path is null || (!LifecycleSafety.SamePath(path, ui) && !LifecycleSafety.SamePath(path, backend))) continue;
                LifecycleSafety.CheckNoReparse(path);
                if (LifecycleSafety.SamePath(path, backend))
                {
                    var command = ReadCommandLine(process);
                    var arguments = SplitCommandLine(command);
                    if (!LifecycleSafety.IsOwnedBackend(path, arguments, installRoot, dataRoot))
                        throw new IOException("Installed OpenRGB is running without LightOrder ownership evidence. Close it manually, then retry.");
                    LifecycleSafety.CheckNoReparse(arguments[2]);
                    if (!File.Exists(Path.Combine(arguments[2], "OpenRGB.json"))) throw new IOException("OpenRGB ownership configuration is missing; close it manually before retrying.");
                }
                process.CloseMainWindow();
                if (!process.WaitForExit(5000))
                {
                    process.Kill(entireProcessTree: false);
                    if (!process.WaitForExit(5000)) throw new TimeoutException("An installed LightOrder process did not exit.");
                }
            }
            catch (InvalidOperationException) when (process.HasExited) { }
            catch (Win32Exception e)
            {
                // Unknown image paths cannot be assumed to belong to us. File replacement/deletion
                // will still fail explicitly if such a process holds an installed file open.
                if (e.NativeErrorCode != 5) throw;
            }
        }
    }

    internal static string[] SplitCommandLine(string command)
    {
        var pointer = CommandLineToArgvW(command, out var count);
        if (pointer == IntPtr.Zero) throw new Win32Exception();
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(pointer); }
    }

    private static string ReadCommandLine(Process process)
    {
        NtQueryInformationProcess(process.Handle, 60, IntPtr.Zero, 0, out var size);
        if (size is < 16 or > 131072) throw new IOException("Cannot inspect OpenRGB command line.");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryInformationProcess(process.Handle, 60, buffer, size, out _) != 0) throw new IOException("Cannot establish OpenRGB ownership.");
            var text = Marshal.PtrToStructure<UnicodeString>(buffer);
            return Marshal.PtrToStringUni(text.Buffer, text.Length / 2) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint Type, State, Accepted, ExitCode, SpecificExitCode, CheckPoint, WaitHint, ProcessId, Flags; }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int type, IntPtr buffer, int size, out int needed);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
