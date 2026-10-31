using System.Security.AccessControl;
using System.Security.Principal;
using ControlCenter.Contracts;

namespace ControlCenter.OpenRgb;

// Fail closed on writable settings, executable/config paths, junctions and writable
// replacement ancestors. Packaging must install these paths with protected ACLs.
internal static class ProtectedPaths
{
    private static readonly HashSet<string> Trusted = new(StringComparer.Ordinal)
    {
        "S-1-5-18", "S-1-5-32-544",
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" // TrustedInstaller
    };
    internal static bool UntrustedWrite(string sid, FileSystemRights rights, bool directory, bool ancestor)
    {
        var mutation = FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        if (directory) mutation |= FileSystemRights.DeleteSubdirectoriesAndFiles;
        if (!ancestor) mutation |= FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;
        return !Trusted.Contains(sid) && (rights & mutation) != 0;
    }
    public static void Require(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PluginException("ConfigurationError", "Protected Windows installation required");
        var full = Path.GetFullPath(path);
        FileSystemInfo? current = File.Exists(full) ? new FileInfo(full) : Directory.Exists(full) ? new DirectoryInfo(full) : null;
        var missing = current == null;
        current ??= new DirectoryInfo(Path.GetDirectoryName(full)!);
        while (!current.Exists && current is DirectoryInfo absent && absent.Parent != null) current = absent.Parent;
        var depth = 0;
        var targetIsFile = current is FileInfo;
        while (current != null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) Fail(path, "reparse point");
            var directory = current is DirectoryInfo;
            FileSystemSecurity acl = directory ? ((DirectoryInfo)current).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                : ((FileInfo)current).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var descriptor = new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0);
            if (descriptor.DiscretionaryAcl == null) Fail(path, "unrestricted null DACL");
            if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Trusted.Contains(owner.Value)) Fail(path, "untrusted owner");
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                // For a missing target, the nearest existing parent must also prevent creation.
                if (UntrustedWrite(((SecurityIdentifier)rule.IdentityReference).Value, rule.FileSystemRights, directory,
                    depth > (targetIsFile ? 1 : 0) && !missing))
                    Fail(path, "non-administrator write or replacement access");
            }
            missing = false;
            current = directory ? ((DirectoryInfo)current).Parent : ((FileInfo)current).Directory;
            depth++;
        }
    }
    private static void Fail(string path, string reason) => throw new PluginException("ConfigurationError", $"Protected administrator-installed path required ({reason}): {path}");
}
