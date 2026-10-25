using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace LightOrder.Setup;

internal static class Program
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
            if (args is ["--self-test"])
            {
                ValidateEntry("plugins/a/plugin.json");
                if (!IsDirectoryEntry("docs/", "") || !IsDirectoryEntry("docs\\", "docs\\") || IsDirectoryEntry("docs/readme.md", "readme.md")) return 1;
                try { ValidateEntry("../escape.exe"); return 1; } catch (InvalidDataException) { return 0; }
            }
            if (args is ["--install-worker", var sid])
            {
                if (!IsAdmin()) throw new UnauthorizedAccessException("Administrator access is required.");
                _ = new SecurityIdentifier(sid);
                Install(sid); return 0;
            }
            if (args is ["--uninstall"])
            {
                if (!IsAdmin()) return Elevate(["--uninstall"]);
                if (MessageBox.Show("停止并移除 LightOrder 服务？用户配置会保留。\nStop and uninstall LightOrder service? Preferences will be retained.", "LightOrder", MessageBoxButtons.OKCancel) != DialogResult.OK) return 0;
                Run("sc.exe", ["stop", ServiceName], [0, 1060, 1062]);
                WaitStopped();
                Run("sc.exe", ["delete", ServiceName], [0, 1060]);
                Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LightOrder", false);
                MessageBox.Show("服务已移除。程序文件保留在安装目录，可在退出所有 LightOrder 进程后删除。\nService removed. Program files can be deleted after all LightOrder processes exit.", "LightOrder"); return 0;
            }
            var app = Path.Combine(InstallRoot, "LightOrder.App.exe");
            if (args.Length == 0 && File.Exists(app)) { Process.Start(new ProcessStartInfo(app) { UseShellExecute = true }); return 0; }
            if (args.Length > 0 && args is not ["--install"]) throw new ArgumentException("Unknown setup option.");
            if (MessageBox.Show("首次配置将安装本地硬件服务和设备插件。之后日常使用无需重复管理员授权。\n\nFirst setup installs a local hardware service and plugins. Normal use does not require repeated elevation.\n\n继续 / Continue?", "光序 LightOrder", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return 0;
            using var user = WindowsIdentity.GetCurrent();
            var ownerSid = user.User?.Value ?? throw new InvalidOperationException("Cannot determine installation owner.");
            var code = Elevate(["--install-worker", ownerSid]);
            if (code != 0) throw new InvalidOperationException($"Installation failed ({code}).");
            Process.Start(new ProcessStartInfo(app) { UseShellExecute = true }); return 0;
        }
        catch (Exception e)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "LightOrder-setup-error.log"), DateTimeOffset.UtcNow + "\n" + e); } catch { }
            MessageBox.Show(e.Message + "\n\nLog: " + Path.Combine(Path.GetTempPath(), "LightOrder-setup-error.log"), "LightOrder", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1;
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
        CheckNoReparse(InstallRoot); CheckNoReparse(DataRoot);
        Directory.CreateDirectory(InstallRoot); Directory.CreateDirectory(DataRoot);
        SecureDirectory(InstallRoot, readUsers: true); SecureDirectory(DataRoot, readUsers: false);
        Run("sc.exe", ["stop", ServiceName], [0,1060,1062]); WaitStopped();
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                ValidateEntry(entry.FullName);
                var dest = Path.GetFullPath(Path.Combine(InstallRoot, entry.FullName));
                if (!dest.StartsWith(InstallRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Payload path escapes installation.");
                CheckNoReparse(dest);
                if (IsDirectoryEntry(entry.FullName, entry.Name)) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (File.Exists(dest))
                {
                    using var existing = File.OpenRead(dest);
                    using var incoming = entry.Open();
                    if (System.Security.Cryptography.SHA256.HashData(existing).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(incoming))) continue;
                }
                entry.ExtractToFile(dest, true);
            }
        }
        // Apply protected ACLs recursively, including files left by a previous installation.
        SecureTree(InstallRoot, true); SecureTree(DataRoot, false);
        var ownPath = Environment.ProcessPath!;
        var bootstrap = Path.Combine(InstallRoot, "LightOrder.exe");
        if (!Path.GetFullPath(ownPath).Equals(bootstrap, StringComparison.OrdinalIgnoreCase)) File.Copy(ownPath, bootstrap, true);
        SecureFile(bootstrap, true);
        var config = new { schemaVersion = 1, ownerSid, enabledPlugins = new[] { "local.gigabyte-it5702", "local.openrgb", "local.hardware-monitor", "local.windows-policy" } };
        File.WriteAllText(Path.Combine(DataRoot, "service.json"), JsonSerializer.Serialize(config));
        SecureFile(Path.Combine(DataRoot, "service.json"), false);
        var driver = Path.Combine(InstallRoot, "dependencies", "PawnIO_setup.exe");
        if (File.Exists(driver) && !DriverInstalled())
        {
            VerifySignature(driver);
            Run(driver, ["-install", "-silent"], [0,3010]);
        }
        if (DriverInstalled()) Run("sc.exe", ["start", "PawnIO"], [0,1056]);
        var service = Path.Combine(InstallRoot, "LightOrder.Service.exe");
        var create = Run("sc.exe", ["create", ServiceName, "binPath=", '"' + service + '"', "start=", "auto", "DisplayName=", "LightOrder Hardware Service"], [0,1073]);
        if (create == 1073) Run("sc.exe", ["config", ServiceName, "binPath=", '"' + service + '"', "start=", "auto"], [0]);
        Run("sc.exe", ["description", ServiceName, "LightOrder local hardware plugin host. Restore control before uninstalling."], [0]);
        if (DriverInstalled()) Run("sc.exe", ["config", ServiceName, "depend=", "PawnIO"], [0]);
        Run("sc.exe", ["start", ServiceName], [0,1056]);
        VerifyServiceReady();
        using var uninstall = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LightOrder");
        uninstall.SetValue("DisplayName", "LightOrder / 光序"); uninstall.SetValue("DisplayVersion", "0.1.0");
        uninstall.SetValue("Publisher", "LightOrder contributors"); uninstall.SetValue("InstallLocation", InstallRoot);
        uninstall.SetValue("UninstallString", '"' + bootstrap + "\" --uninstall");
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
        Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command], [0]);
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
    private static bool IsDirectoryEntry(string fullName, string name) => fullName.EndsWith('/') || fullName.EndsWith('\\') || string.IsNullOrEmpty(name);
    private static void ValidateEntry(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry) || Path.IsPathRooted(entry) || entry.Contains(':') || entry.Replace('\\','/').Split('/').Any(s=>s=="..")) throw new InvalidDataException("Unsafe payload entry.");
    }
    private static void CheckNoReparse(string target)
    {
        for(var current=Path.GetFullPath(target);!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
            if((File.Exists(current)||Directory.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Installation path cannot contain reparse points.");
    }
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
    private static int Run(string exe,string[] args,int[] accepted)
    {
        var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in args)info.ArgumentList.Add(arg);
        using var p=Process.Start(info)??throw new IOException("Cannot start "+exe);var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();if(!p.WaitForExit(120000))throw new TimeoutException(exe+" timed out");Task.WaitAll(output,error);if(!accepted.Contains(p.ExitCode))throw new InvalidOperationException(exe+" failed: "+output.Result+error.Result);return p.ExitCode;
    }
    private static void WaitStopped()
    {
        for(var i=0;i<30;i++)
        {
            using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+ServiceName);
            if(key is null)return;
            var info=new ProcessStartInfo("sc.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true};info.ArgumentList.Add("query");info.ArgumentList.Add(ServiceName);
            using var p=Process.Start(info)!;var text=p.StandardOutput.ReadToEnd();p.WaitForExit();if(text.Contains("STOPPED",StringComparison.OrdinalIgnoreCase)||p.ExitCode==1060)return;Thread.Sleep(1000);
        }
        throw new TimeoutException("Service did not stop. Files were not replaced.");
    }
}
