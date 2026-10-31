using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace LightOrder.Setup;

internal static partial class Program
{
    private const string ServiceName = "LightOrder.Service";
    private static readonly string InstallRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LightOrder");
    private static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LightOrder");
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args is ["--self-test"]) return SetupSelfTests.Run();
            if (args is ["--install-worker", var workerSid])
            {
                if (!IsAdmin()) throw new UnauthorizedAccessException("Administrator access is required.");
                using var identity = WindowsIdentity.GetCurrent();
                if (LifecycleSafety.ValidateOwnerSid(workerSid).Value != identity.User?.Value) throw new UnauthorizedAccessException("Initial owner must match this worker's account.");
                if (LifecycleSafety.SamePath(Environment.ProcessPath!, Path.Combine(InstallRoot, "LightOrder.exe"))) throw new IOException("Synchronous worker must run outside the installed directory; use --install for in-place setup.");
                using var gate = new Mutex(false, @"Global\LightOrder.Setup.Lifecycle");
                var acquired = false;
                try
                {
                    try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("Another lifecycle operation is in progress.");
                    Install(workerSid); return 0;
                }
                finally { if (acquired) gate.ReleaseMutex(); }
            }
            if (args.Length > 0 && args[0] == "--execute-worker") return ExecuteWorker(args);
            if (args.Length > 0 && args[0] == "--stage-worker") return StageWorker(args);
            if (args is ["--uninstall"])
            {
                var choice = MessageBox.Show("卸载 LightOrder 服务和程序文件。\n是否保留偏好设置、灯光配置和专用 OpenRGB 数据？\n\nUninstall LightOrder service and program files.\nKeep preferences, profiles and dedicated OpenRGB data?\n\n是 / Yes: 保留 / Keep\n否 / No: 删除 / Delete\n取消 / Cancel: 退出 / Exit\n\n共享 PawnIO 和 WebView2 将保留。Shared PawnIO and WebView2 are retained.", "LightOrder", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (choice == DialogResult.Cancel) return 0;
                return BeginOperation("uninstall", choice == DialogResult.Yes ? "keep" : "delete");
            }
            var app = Path.Combine(InstallRoot, "LightOrder.App.exe");
            if (args.Length == 0 && File.Exists(app) && LifecycleSafety.SamePath(Environment.ProcessPath!, Path.Combine(InstallRoot, "LightOrder.exe")))
            { Process.Start(new ProcessStartInfo(app) { UseShellExecute = true }); return 0; }
            if (args.Length > 0 && args is not ["--install"]) throw new ArgumentException("Unknown setup option.");
            var message = File.Exists(app)
                ? "更新光序及本地硬件服务，保留现有配置。安装后可从桌面或开始菜单打开。\n\nUpdate LightOrder and its hardware service, keeping your settings. Launch from the desktop or Start menu."
                : "安装光序、本地硬件服务和插件，并创建桌面及开始菜单快捷方式。之后日常使用无需重复管理员授权。\n\nInstall LightOrder, its hardware service and plugins, with desktop and Start menu shortcuts. Normal use does not require repeated elevation.";
            if (MessageBox.Show(message + "\n\n继续 / Continue?", "光序 LightOrder", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return 0;
            return BeginOperation("install", "keep");
        }
        catch (Exception e)
        {
            var log = "";
            try
            {
                var folder = args.Length > 0 && args[0] == "--execute-worker" ? Path.GetDirectoryName(Environment.ProcessPath!)! : Path.GetTempPath();
                log = Path.Combine(folder, "LightOrder-setup-error-" + Guid.NewGuid().ToString("N") + ".log");
                using var file = new FileStream(log, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(file); writer.Write(DateTimeOffset.UtcNow + "\n" + e);
            }
            catch { }
            if (args.Length > 0 && args[0] == "--install-worker") { Console.Error.WriteLine(e + "\nLog: " + log); return 1; }
            MessageBox.Show(e.Message + "\n\nLog: " + log, "LightOrder", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1;
        }
    }
    private static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    private static int Elevate(string[] args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new IOException("Cannot start setup.");
        child.WaitForExit(); return child.ExitCode;
    }
    private static void Install(string ownerSid)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("LightOrder.payload.zip")
            ?? throw new InvalidOperationException("This is a development build without an installation payload.");
        if (!WebViewInstalled()) throw new InvalidOperationException("请先安装 Microsoft Edge WebView2 Runtime，再重试。\nInstall Microsoft Edge WebView2 Runtime first: https://developer.microsoft.com/microsoft-edge/webview2/");
        LifecycleSafety.ValidateOwnerSid(ownerSid);
        InspectFixedRoots();
        var configurationPath = Path.Combine(DataRoot, "service.json");
        // Existing installation ownership and enabled plugins survive upgrades byte for byte.
        if (File.Exists(configurationPath)) LifecycleSafety.ValidateConfiguration(File.ReadAllBytes(configurationPath));
        Directory.CreateDirectory(InstallRoot); Directory.CreateDirectory(DataRoot);
        SecureDirectory(InstallRoot, readUsers: true); SecureDirectory(DataRoot, readUsers: false);
        var openRgbData = Path.Combine(DataRoot, "OpenRgb");
        CheckNoReparse(openRgbData); Directory.CreateDirectory(openRgbData); SecureDirectory(openRgbData, readUsers: false);
        WindowsLifecycle.StopService(ServiceName);
        LifecycleSafety.RefuseUnresolvedRestoration(DataRoot);
        WindowsLifecycle.CloseInstalledProcesses(InstallRoot, DataRoot);
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Read)) ApplyPayload(zip, InstallRoot);
        // Apply protected ACLs recursively, including files left by a previous installation.
        SecureTree(InstallRoot, true); SecureTree(DataRoot, false);
        var ownPath = Environment.ProcessPath!;
        var bootstrap = Path.Combine(InstallRoot, "LightOrder.exe");
        if (!Path.GetFullPath(ownPath).Equals(bootstrap, StringComparison.OrdinalIgnoreCase)) File.Copy(ownPath, bootstrap, true);
        SecureFile(bootstrap, true);
        EnsureConfiguration(DataRoot, ownerSid);
        SecureFile(configurationPath, false);
        var driver = Path.Combine(InstallRoot, "dependencies", "PawnIO_setup.exe");
        if (File.Exists(driver) && !DriverInstalled())
        {
            VerifySignature(driver);
            Run(driver, ["-install", "-silent"], [0,3010]);
        }
        if (DriverInstalled()) Run(WindowsLifecycle.Tool("sc.exe"), ["start", "PawnIO"], [0,1056]);
        var service = Path.Combine(InstallRoot, "LightOrder.Service.exe");
        var create = Run(WindowsLifecycle.Tool("sc.exe"), ["create", ServiceName, "binPath=", '"' + service + '"', "start=", "auto", "DisplayName=", "LightOrder Hardware Service"], [0,1073]);
        if (create == 1073) Run(WindowsLifecycle.Tool("sc.exe"), ["config", ServiceName, "binPath=", '"' + service + '"', "start=", "auto"], [0]);
        Run(WindowsLifecycle.Tool("sc.exe"), ["description", ServiceName, "LightOrder local hardware plugin host. Restore control before uninstalling."], [0]);
        if (DriverInstalled()) Run(WindowsLifecycle.Tool("sc.exe"), ["config", ServiceName, "depend=", "PawnIO"], [0]);
        Run(WindowsLifecycle.Tool("sc.exe"), ["start", ServiceName], [0,1056]);
        WindowsLifecycle.WaitService(ServiceName, stopped: false);
        VerifyServiceReady();
        using var uninstall = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LightOrder");
        uninstall.SetValue("DisplayName", "LightOrder / 光序"); uninstall.SetValue("DisplayVersion", "0.2.0");
        uninstall.SetValue("Publisher", "LightOrder contributors"); uninstall.SetValue("InstallLocation", InstallRoot);
        uninstall.SetValue("UninstallString", '"' + bootstrap + "\" --uninstall");
        uninstall.SetValue("DisplayIcon", Path.Combine(InstallRoot, "LightOrder.App.exe") + ",0");
        uninstall.SetValue("NoModify", 1); uninstall.SetValue("NoRepair", 1);
        InstallShortcuts();
    }
    private static void VerifyServiceReady()
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", "LightOrder.Control.v1", System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                pipe.Connect(2000);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, leaveOpen: true);
                writer.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"app.snapshot\",\"params\":{}}");
                var line = reader.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
                using var response = JsonDocument.Parse(line ?? throw new IOException("Service disconnected during setup."));
                if (response.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) throw new IOException("Service rejected setup verification.");
                _ = response.RootElement.GetProperty("result").GetProperty("devices");
                return;
            }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { last = e; Thread.Sleep(500); }
        }
        throw new IOException("Service installed but did not become ready. Check Windows Application logs before retrying.", last);
    }
    private static void VerifySignature(string file)
    {
        var command = "$s=Get-AuthenticodeSignature -LiteralPath '" + file.Replace("'", "''") + "'; if($s.Status -ne 'Valid'){exit 1}";
        Run(Path.Combine(WindowsLifecycle.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), ["-NoProfile", "-NonInteractive", "-Command", command], [0]);
    }
    private static bool DriverInstalled() { using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"); return key is not null; }
    private static bool WebViewInstalled()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            using var root=RegistryKey.OpenBaseKey(hive,view); using var clients=root.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients");
            if(clients is null)continue;
            foreach(var name in clients.GetSubKeyNames()) { using var key=clients.OpenSubKey(name); if(key?.GetValue("name")?.ToString()?.Contains("WebView2",StringComparison.OrdinalIgnoreCase)==true && Version.TryParse(key.GetValue("pv")?.ToString(),out _))return true; }
        }
        return false;
    }
    private static bool IsDirectoryEntry(string fullName, string name) => LifecycleSafety.IsDirectoryEntry(fullName, name);
    private static void ValidateEntry(string entry) => LifecycleSafety.ValidateEntry(entry);
    private static void CheckNoReparse(string target) => LifecycleSafety.CheckNoReparse(target);
    private static FileSystemAccessRule Rule(WellKnownSidType sid,FileSystemRights rights,bool directory)=>new(new SecurityIdentifier(sid,null),rights,directory?InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit:InheritanceFlags.None,PropagationFlags.None,AccessControlType.Allow);
    private static void SecureDirectory(string folder,bool readUsers)
    {
        var acl=new DirectorySecurity(); acl.SetAccessRuleProtection(true,false); acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));
        acl.AddAccessRule(Rule(WellKnownSidType.LocalSystemSid,FileSystemRights.FullControl,true));acl.AddAccessRule(Rule(WellKnownSidType.BuiltinAdministratorsSid,FileSystemRights.FullControl,true));
        if(readUsers)acl.AddAccessRule(Rule(WellKnownSidType.AuthenticatedUserSid,FileSystemRights.ReadAndExecute,true));new DirectoryInfo(folder).SetAccessControl(acl);
    }
    private static void SecureFile(string file,bool readUsers)
    {
        var acl=new FileSecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));
        acl.AddAccessRule(Rule(WellKnownSidType.LocalSystemSid,FileSystemRights.FullControl,false));acl.AddAccessRule(Rule(WellKnownSidType.BuiltinAdministratorsSid,FileSystemRights.FullControl,false));if(readUsers)acl.AddAccessRule(Rule(WellKnownSidType.AuthenticatedUserSid,FileSystemRights.ReadAndExecute,false));new FileInfo(file).SetAccessControl(acl);
    }
    private static void SecureTree(string folder,bool readUsers)
    {
        foreach(var entry in Directory.EnumerateFileSystemEntries(folder)) { CheckNoReparse(entry); if(Directory.Exists(entry)){SecureDirectory(entry,readUsers);SecureTree(entry,readUsers);}else SecureFile(entry,readUsers); }
    }
    internal static int Run(string exe,string[] args,int[] accepted)
    {
        var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in args)info.ArgumentList.Add(arg);
        using var p=Process.Start(info)??throw new IOException("Cannot start "+exe);var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();if(!p.WaitForExit(120000)){p.Kill(entireProcessTree:false);p.WaitForExit(5000);throw new TimeoutException(exe+" timed out");}Task.WaitAll(output,error);if(!accepted.Contains(p.ExitCode))throw new InvalidOperationException(exe+" failed: "+output.Result+error.Result);return p.ExitCode;
    }
    internal static void EnsureConfiguration(string dataRoot, string ownerSid)
    {
        LifecycleSafety.ValidateOwnerSid(ownerSid);
        var path = Path.Combine(dataRoot, "service.json");
        CheckNoReparse(path);
        if (File.Exists(path)) { LifecycleSafety.ValidateConfiguration(File.ReadAllBytes(path)); return; }
        var config = new { schemaVersion = 1, ownerSid, enabledPlugins = new[] { "local.gigabyte-it5702", "local.openrgb", "local.hardware-monitor", "local.windows-policy" } };
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, config);
    }

    internal static void ApplyPayload(ZipArchive zip, string root)
    {
        LifecycleSafety.InspectTree(root);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Validate the whole archive and lock availability before replacing any existing file.
        foreach (var entry in zip.Entries)
        {
            ValidateEntry(entry.FullName);
            var dest = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!dest.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !seen.Add(Path.TrimEndingDirectorySeparator(dest)))
                throw new InvalidDataException("Duplicate or escaping payload path.");
            CheckNoReparse(dest);
            if (File.Exists(dest))
            {
                var timer = Stopwatch.StartNew();
                while (true)
                {
                    try { using var probe = new FileStream(dest, FileMode.Open, FileAccess.ReadWrite, FileShare.None); break; }
                    catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(10)) { Thread.Sleep(200); }
                }
            }
        }
        foreach (var entry in zip.Entries)
        {
            var dest = Path.GetFullPath(Path.Combine(root, entry.FullName));
            CheckNoReparse(dest);
            if (IsDirectoryEntry(entry.FullName, entry.Name)) { Directory.CreateDirectory(dest); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var temporary = dest + ".setup-" + Guid.NewGuid().ToString("N");
            try { entry.ExtractToFile(temporary); File.Move(temporary, dest, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
