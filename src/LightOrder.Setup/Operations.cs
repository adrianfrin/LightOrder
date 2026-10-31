using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace LightOrder.Setup;

internal static partial class Program
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LightOrder";
    private static readonly string WorkerParent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LightOrder.Setup");

    private static int BeginOperation(string operation, string preferences)
    {
        using var self = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new IOException("Cannot determine installation owner.");
        LifecycleSafety.ValidateOwnerSid(sid);
        // The staging process returns after starting an independent worker. This launcher must
        // exit, including when run from InstallRoot, before the worker can replace/delete it.
        return Elevate(["--stage-worker", operation, preferences, sid, self.Id.ToString(), self.StartTime.ToUniversalTime().Ticks.ToString()]);
    }

    private static int StageWorker(string[] args)
    {
        if (!IsAdmin()) throw new UnauthorizedAccessException("Administrator access is required.");
        if (args is not ["--stage-worker", var operation, var preferences, var sid, var pidText, var timeText]) throw new ArgumentException("Invalid worker request.");
        ValidateOperation(operation, preferences);
        LifecycleSafety.ValidateOwnerSid(sid);
        using var caller = OpenOriginal(pidText, timeText);
        // Validate the unelevated initiator's token, including UAC with a different admin account.
        if (!OpenProcessToken(caller.Handle, 8, out var token)) throw new System.ComponentModel.Win32Exception();
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            if (identity.User?.Value != sid) throw new UnauthorizedAccessException("Owner SID does not match the initiating Windows account.");
        if (!LifecycleSafety.SamePath(caller.MainModule?.FileName ?? "", Environment.ProcessPath!))
            throw new UnauthorizedAccessException("Setup initiator image does not match.");

        CheckNoReparse(WorkerParent);
        if (Directory.Exists(WorkerParent)) LifecycleSafety.ValidateProtectedDirectory(WorkerParent);
        Directory.CreateDirectory(WorkerParent); SecureDirectory(WorkerParent, false);
        var work = Path.Combine(WorkerParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work); SecureDirectory(work, false);
        var helper = Path.Combine(work, "LightOrder.exe");
        // Production setup is a self-contained single file. Never execute a helper from a
        // caller-controlled temporary directory or accept paths from renderer/CLI input.
        File.Copy(Environment.ProcessPath!, helper); SecureFile(helper, false);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = work };
        foreach (var arg in new[] { "--execute-worker", operation, preferences, sid, pidText, timeText }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new IOException("Cannot start lifecycle worker.");
        return 0;
    }

    private static int ExecuteWorker(string[] args)
    {
        if (!IsAdmin()) throw new UnauthorizedAccessException("Administrator access is required.");
        if (args is not ["--execute-worker", var operation, var preferences, var sid, var pidText, var timeText]) throw new ArgumentException("Invalid worker request.");
        ValidateOperation(operation, preferences);
        LifecycleSafety.ValidateOwnerSid(sid);
        var work = Path.GetDirectoryName(Environment.ProcessPath!)!;
        if (!LifecycleSafety.SamePath(Path.GetDirectoryName(work)!, WorkerParent) || !Guid.TryParseExact(Path.GetFileName(work), "N", out _) ||
            !LifecycleSafety.SamePath(Environment.ProcessPath!, Path.Combine(work, "LightOrder.exe"))) throw new IOException("Worker must run from its protected staging directory.");
        CheckNoReparse(work);
        LifecycleSafety.ValidateProtectedDirectory(WorkerParent);
        LifecycleSafety.ValidateProtectedDirectory(work);
        using var gate = new Mutex(false, @"Global\LightOrder.Setup.Lifecycle");
        var acquired = false;
        try
        {
            try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Another LightOrder installation or removal is in progress.");
            try
            {
                using var caller = OpenOriginal(pidText, timeText);
                if (!caller.WaitForExit(60000)) throw new TimeoutException("Setup launcher did not exit; installed files were not changed.");
            }
            catch (ArgumentException) { /* launcher already exited */ }
            InspectFixedRoots();
            if (operation == "install")
            {
                Install(sid);
                if (!OwnerAppLaunch.TryLaunch(sid, Path.Combine(InstallRoot, "LightOrder.App.exe")))
                    MessageBox.Show("LightOrder 0.2.0 安装完成。请重新打开 LightOrder。\nInstallation complete. Open LightOrder to continue.", "LightOrder");
            }
            else
            {
                Uninstall(preferences == "delete", sid);
                MessageBox.Show("LightOrder 已卸载。共享 PawnIO 和 WebView2 已保留。\nLightOrder uninstalled. Shared PawnIO and WebView2 were retained.", "LightOrder");
            }
            return 0;
        }
        finally
        {
            if (acquired) gate.ReleaseMutex();
            // Only our generated helper and its empty directory need reboot cleanup; program
            // files are removed synchronously here, after the installed launcher has exited.
            if (!MoveFileEx(Environment.ProcessPath!, null, 4) || !MoveFileEx(work, null, 4))
                Trace.WriteLine("LightOrder setup helper could not be scheduled for reboot cleanup: " + work);
        }
    }

    private static Process OpenOriginal(string pid, string ticks)
    {
        if (!int.TryParse(pid, out var id) || id <= 0 || !long.TryParse(ticks, out var started)) throw new InvalidDataException("Invalid launcher identity.");
        var process = Process.GetProcessById(id);
        if (process.StartTime.ToUniversalTime().Ticks != started) { process.Dispose(); throw new IOException("Launcher PID was reused."); }
        _ = process.Handle;
        return process;
    }

    private static void ValidateOperation(string operation, string preferences)
    {
        if (operation is not ("install" or "uninstall") || preferences is not ("keep" or "delete") || (operation == "install" && preferences != "keep"))
            throw new ArgumentException("Invalid lifecycle operation.");
    }

    private static void InspectFixedRoots()
    {
        LifecycleSafety.RequireExactRoot(InstallRoot, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LightOrder"));
        LifecycleSafety.RequireExactRoot(DataRoot, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LightOrder"));
        LifecycleSafety.InspectTree(InstallRoot); LifecycleSafety.InspectTree(DataRoot);
    }

    private static void Uninstall(bool deletePreferences, string initiatorSid)
    {
        var owner = initiatorSid;
        var config = Path.Combine(DataRoot, "service.json");
        if (File.Exists(config))
        {
            var bytes = File.ReadAllBytes(config); LifecycleSafety.ValidateConfiguration(bytes);
            using var doc = System.Text.Json.JsonDocument.Parse(bytes);
            owner = doc.RootElement.GetProperty("ownerSid").GetString()!;
        }
        var localPreferences = OwnerLocalPreferences(owner);
        if (deletePreferences && localPreferences is not null) LifecycleSafety.InspectTree(localPreferences);
        WindowsLifecycle.StopService(ServiceName);
        LifecycleSafety.RefuseUnresolvedRestoration(DataRoot);
        WindowsLifecycle.CloseInstalledProcesses(InstallRoot, DataRoot);
        Run(WindowsLifecycle.Tool("sc.exe"), ["delete", ServiceName], [0, 1060]);
        var removal = Stopwatch.StartNew();
        while (true)
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
            if (key is null) break;
            if (removal.Elapsed > TimeSpan.FromSeconds(15)) throw new IOException("Service is marked for deletion but still registered. Close service-management tools, then retry; program files were retained.");
            Thread.Sleep(200);
        }
        RemoveOwnedIntegrations(owner);
        // Recheck immediately before deletion, never use a registry InstallLocation or argument as a root.
        InspectFixedRoots();
        LifecycleSafety.DeleteTree(InstallRoot, InstallRoot);
        if (deletePreferences)
        {
            LifecycleSafety.DeleteTree(DataRoot, DataRoot);
            if (localPreferences is not null) LifecycleSafety.DeleteTree(localPreferences, OwnerLocalPreferences(owner)!);
        }
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
    }

    private static string? OwnerLocalPreferences(string owner)
    {
        LifecycleSafety.ValidateOwnerSid(owner);
        using var profile = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + owner);
        if (profile?.GetValue("ProfileImagePath") is not string home) return null;
        home = Environment.ExpandEnvironmentVariables(home);
        if (!Path.IsPathFullyQualified(home) || home.StartsWith(@"\\") || Path.GetPathRoot(home) == home) throw new IOException("Unsafe owner profile path.");
        CheckNoReparse(home);
        return Path.Combine(home, "AppData", "Local", "LightOrder");
    }

    // Remove only exact LightOrder launch commands from the machine and owner's loaded hive.
    // Never delete entries by display name alone, nor load another user's hive or run scripts.
    private static void RemoveOwnedIntegrations(string owner)
    {
        RemoveRunEntries(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
        using var hive = Registry.Users.OpenSubKey(owner, writable: true);
        if (hive is not null) RemoveRunEntries(hive, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
            RemoveOwnedShortcuts(folder);
        var local = OwnerLocalPreferences(owner);
        if (local is not null)
        {
            var profile = Path.GetFullPath(Path.Combine(local, "..", "..", ".."));
            foreach (var relative in new[] { "Desktop", @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs", @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup" }) RemoveOwnedShortcuts(Path.Combine(profile, relative));
        }
    }

    private static bool OwnLaunchCommand(string command)
    {
        var args = WindowsLifecycle.SplitCommandLine(command);
        return args.Length == 1 && Path.IsPathFullyQualified(args[0]) &&
            (LifecycleSafety.SamePath(args[0], Path.Combine(InstallRoot, "LightOrder.exe")) || LifecycleSafety.SamePath(args[0], Path.Combine(InstallRoot, "LightOrder.App.exe")));
    }

    private static void RemoveRunEntries(RegistryKey root, string path)
    {
        using var run = root.OpenSubKey(path, writable: true);
        if (run is null) return;
        foreach (var name in run.GetValueNames())
            if (run.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command && OwnLaunchCommand(command)) run.DeleteValue(name);
    }

    private static void RemoveOwnedShortcuts(string folder)
    {
        CheckNoReparse(folder);
        if (!Directory.Exists(folder)) return;
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows shortcut support is unavailable.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.lnk"))
            {
                CheckNoReparse(file);
                dynamic link = shell.CreateShortcut(file);
                try
                {
                    string target = link.TargetPath; string arguments = link.Arguments;
                    if (arguments.Length == 0 && Path.IsPathFullyQualified(target) && OwnLaunchCommand('"' + target + '"')) File.Delete(file);
                }
                finally { Marshal.FinalReleaseComObject(link); }
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool MoveFileEx(string existing, string? replacement, uint flags);
}
