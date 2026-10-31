using System.IO.Compression;
using System.Text;

namespace LightOrder.Setup;

internal static class SetupSelfTests
{
    internal static int Run()
    {
        var count = 0;
        var temporary = Path.Combine(Path.GetTempPath(), "LightOrder-lifecycle-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        void Reject(Action action, string message)
        {
            try { action(); } catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException) { count++; return; }
            throw new Exception("Expected rejection: " + message);
        }
        try
        {
            foreach (var entry in new[] { "plugins/a/plugin.json", "docs/", "docs\\", "plugins/OpenRgb/runtime/OpenRGB.exe" }) LifecycleSafety.ValidateEntry(entry);
            foreach (var entry in new[] { "../escape", "a/../b", @"a\..\b", "C:/escape", @"\\server\share", "a:stream", "a/./b", "a//b", "a/file. ", "a/NUL.txt", "a/file.", "a/COM1", "a/file\n.exe" })
                Reject(() => LifecycleSafety.ValidateEntry(entry), "unsafe archive path " + entry);
            Check(LifecycleSafety.IsDirectoryEntry("docs/", "") && LifecycleSafety.IsDirectoryEntry("docs\\", "docs\\") && !LifecycleSafety.IsDirectoryEntry("docs/a.md", "a.md"), "ZIP directory recognition");
            var install = Path.Combine(temporary, "LightOrder");
            var neighbor = Path.Combine(temporary, "LightOrder-other");
            var data = Path.Combine(temporary, "data");
            Directory.CreateDirectory(install); Directory.CreateDirectory(neighbor); Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(neighbor, "sentinel"), "retain");
            File.WriteAllText(Path.Combine(data, "profiles.json"), "retained-profile");
            Reject(() => LifecycleSafety.DeleteTree(neighbor, install), "prefix sibling root");
            Reject(() => LifecycleSafety.DeleteTree(temporary, install), "parent root");
            var incoming = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"ownerSid\":\"S-1-5-21-101-202-303-1001\",\"enabledPlugins\":[\"local.openrgb\"]}");
            LifecycleSafety.ValidateConfiguration(incoming);
            Check(true, "canonical owner and custom plugin selection");
            foreach (var sid in new[] { "S-1-1-0", "S-1-5-18", "S-1-5-32-544", "bogus" }) Reject(() => LifecycleSafety.ValidateOwnerSid(sid), "non-account owner");
            Reject(() => LifecycleSafety.ValidateConfiguration(Encoding.UTF8.GetBytes("{\"schemaVersion\":2,\"ownerSid\":\"S-1-5-21-101-202-303-1001\",\"enabledPlugins\":[]}")), "schema version");
            var exe = Path.Combine(install, "plugins", "OpenRgb", "runtime", "OpenRGB.exe");
            var run = Path.Combine(data, "OpenRgb", "runs", Guid.NewGuid().ToString("N"));
            string[] args = [exe, "--config", run, "--server", "--server-host", "127.0.0.1", "--server-port", "16742", "--noautoconnect", "--loglevel", "warning"];
            Check(LifecycleSafety.IsOwnedBackend(exe, args, install, data), "owned OpenRGB process");
            Check(!LifecycleSafety.IsOwnedBackend(Path.Combine(neighbor, "OpenRGB.exe"), args, install, data), "external OpenRGB untouched");
            var modified = (string[])args.Clone(); modified[2] = Path.Combine(data, "OpenRgb", "runs-other", Guid.NewGuid().ToString("N"));
            Check(!LifecycleSafety.IsOwnedBackend(exe, modified, install, data), "backend config prefix attack");
            modified = (string[])args.Clone(); modified[6] = "0.0.0.0";
            Check(!LifecycleSafety.IsOwnedBackend(exe, modified, install, data), "different backend command is not ours");
            Check(!LifecycleSafety.IsOwnedBackend(exe, args.Append("--extra").ToArray(), install, data), "extra backend arguments rejected");
            Check(WindowsLifecycle.SplitCommandLine("\"C:\\Program Files\\LightOrder\\LightOrder.exe\"").SequenceEqual(new[] { @"C:\Program Files\LightOrder\LightOrder.exe" }), "quoted launch command");
            Directory.CreateDirectory(Path.Combine(install, "plugins"));
            File.WriteAllText(Path.Combine(install, "plugins", "runtime.bin"), "old-runtime");
            using (var archive = new MemoryStream())
            {
                using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
                { using var writer = new StreamWriter(zip.CreateEntry("plugins/runtime.bin").Open()); writer.Write("changed-runtime"); }
                archive.Position = 0;
                using var incomingZip = new ZipArchive(archive, ZipArchiveMode.Read);
                Program.ApplyPayload(incomingZip, install);
            }
            Check(File.ReadAllText(Path.Combine(install, "plugins", "runtime.bin")) == "changed-runtime", "changed runtime installed");
            using (var archive = new MemoryStream())
            {
                using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
                {
                    using (var writer = new StreamWriter(zip.CreateEntry("plugins/runtime.bin").Open())) writer.Write("should-not-replace");
                    using (var writer = new StreamWriter(zip.CreateEntry("../escape.bin").Open())) writer.Write("unsafe");
                }
                archive.Position = 0;
                using var incomingZip = new ZipArchive(archive, ZipArchiveMode.Read);
                Reject(() => Program.ApplyPayload(incomingZip, install), "whole archive preflight before writes");
                Check(File.ReadAllText(Path.Combine(install, "plugins", "runtime.bin")) == "changed-runtime", "invalid later entry cannot partially replace runtime");
            }
            var freshData = Path.Combine(temporary, "fresh-data"); Directory.CreateDirectory(freshData);
            Program.EnsureConfiguration(freshData, "S-1-5-21-404-505-606-1002");
            LifecycleSafety.ValidateConfiguration(File.ReadAllBytes(Path.Combine(freshData, "service.json")));
            Check(true, "initial configuration created with validated owner");
            var config = Path.Combine(data, "service.json"); File.WriteAllBytes(config, incoming);
            Program.EnsureConfiguration(data, "S-1-5-21-404-505-606-1002");
            Check(File.ReadAllBytes(config).SequenceEqual(incoming), "upgrade preserves owner and enabled plugins byte for byte");
            Check(File.ReadAllText(Path.Combine(data, "profiles.json")) == "retained-profile", "upgrade preserves profiles");
            Directory.CreateDirectory(Path.Combine(data, "FanControl"));
            var restoration = Path.Combine(data, "FanControl", "restoration-required.json");
            File.WriteAllText(restoration, "unverified");
            Reject(() => LifecycleSafety.RefuseUnresolvedRestoration(data), "unverified fan restoration blocks lifecycle deletion");
            Check(File.ReadAllText(restoration) == "unverified", "recovery evidence preserved");
            File.Delete(restoration);
            LifecycleSafety.RefuseUnresolvedRestoration(data);
            // A junction requires no developer mode or admin rights. It lives only in this test's temp tree.
            var junction = Path.Combine(install, "linked");
            var result = Program.Run(WindowsLifecycle.Tool("cmd.exe"), ["/d", "/c", "mklink", "/J", junction, neighbor], [0]);
            Check(result == 0, "temporary junction created");
            try
            {
                Reject(() => LifecycleSafety.DeleteTree(install, install), "reparse subtree deletion");
                Check(File.Exists(Path.Combine(install, "plugins", "runtime.bin")), "preflight prevents partial deletion");
                Check(File.ReadAllText(Path.Combine(neighbor, "sentinel")) == "retain", "junction target retained");
            }
            finally { Directory.Delete(junction); }
            LifecycleSafety.DeleteTree(install, install);
            Check(!Directory.Exists(install) && Directory.Exists(data), "keep-preferences removal");
            LifecycleSafety.DeleteTree(data, data);
            Check(!Directory.Exists(data) && File.Exists(Path.Combine(neighbor, "sentinel")), "delete-preferences removal stays scoped");
            Console.WriteLine($"LightOrder setup self-test: {count} checks passed. No services, installed files, registry or hardware changed.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { Directory.Delete(temporary, true); }
    }
}
