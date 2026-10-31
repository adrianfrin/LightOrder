using System.Runtime.InteropServices;

namespace LightOrder.Setup;

internal static partial class Program
{
    private static void InstallShortcuts()
    {
        var target = Path.Combine(InstallRoot, "LightOrder.App.exe");
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows shortcut support is unavailable.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) })
            {
                CheckNoReparse(folder);
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "LightOrder.lnk");
                CheckNoReparse(path);
                dynamic link = shell.CreateShortcut(path);
                try
                {
                    if (File.Exists(path) && (!LifecycleSafety.SamePath((string)link.TargetPath, target) || ((string)link.Arguments).Length != 0))
                        throw new IOException("A shortcut with another target already exists: " + path);
                    link.TargetPath = target;
                    link.Arguments = "";
                    link.WorkingDirectory = InstallRoot;
                    link.IconLocation = target + ",0";
                    link.Description = "LightOrder / 光序";
                    link.Save();
                }
                finally { Marshal.FinalReleaseComObject(link); }
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
}
