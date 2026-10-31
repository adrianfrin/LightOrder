using System.Security.AccessControl;
using System.Security.Principal;

// Plugin-local protection: no dependency on the service executable. Refuse unsafe
// existing entries rather than trying to repair attacker-controlled directories.
static class ProtectedIntentPath
{
    private const FileSystemRights Mutation = FileSystemRights.Write | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    private const FileSystemRights Replacement = FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    internal static bool Trusted(string? sid) => sid is "S-1-5-18" or "S-1-5-32-544" or
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    internal static void ValidateAcl(FileSystemSecurity acl, bool ancestor)
    {
        if (!Trusted(acl.GetOwner(typeof(SecurityIdentifier))?.Value))
            throw new UnauthorizedAccessException("Fan restoration paths require a trusted owner.");
        if (new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl is null)
            throw new UnauthorizedAccessException("Fan restoration paths require a DACL.");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (ancestor && (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            if (rule.AccessControlType == AccessControlType.Allow && !Trusted(rule.IdentityReference.Value) &&
                (rule.FileSystemRights & (ancestor ? Replacement : Mutation)) != 0)
                throw new UnauthorizedAccessException("Untrusted principals can modify or replace fan restoration paths.");
        }
    }

    internal static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static void RejectReparse(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Reparse points are forbidden in fan restoration paths.");
    }

    private static void ValidateDirectory(string path, bool ancestor)
    {
        var attributes = Attributes(path) ?? throw new DirectoryNotFoundException(path);
        RejectReparse(attributes);
        if ((attributes & FileAttributes.Directory) == 0) throw new IOException("Expected fan state directory: " + path);
        ValidateAcl(new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), ancestor);
    }

    // ProgramData may allow creating new children, but must not allow replacing
    // protected children. Each plugin-owned directory forbids every untrusted mutation.
    internal static bool IsExpectedPath(string filePath)
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LightOrder", "FanControl", "restoration-required.json");
        return string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
    }

    internal static void EnsureDirectory(string filePath)
    {
        var programData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        if (!IsExpectedPath(filePath))
            throw new UnauthorizedAccessException("Unexpected fan restoration intent path.");
        for (var ancestor = new DirectoryInfo(programData); ancestor is not null; ancestor = ancestor.Parent)
            ValidateDirectory(ancestor.FullName, true);
        var parent = programData;
        foreach (var name in new[] { "LightOrder", "FanControl" })
        {
            var next = Path.Combine(parent, name);
            if (Attributes(next) is null)
            {
                // Supply a protected ACL at creation, never create-then-secure.
                var acl = new DirectorySecurity();
                acl.SetAccessRuleProtection(true, false);
                acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                    acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(next).Create(acl);
            }
            ValidateDirectory(next, false);
            parent = next;
        }
    }

    internal static void ValidateFile(string path)
    {
        EnsureDirectory(path);
        var attributes = Attributes(path);
        if (attributes is null) return;
        RejectReparse(attributes.Value);
        if ((attributes & FileAttributes.Directory) != 0) throw new IOException("Intent must be a file.");
        ValidateAcl(new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), false);
        // Multiple hard links could make deletion/update evidence ambiguous.
        // The intent is CreateNew-only; no existing file is ever opened for writing.
    }
}
