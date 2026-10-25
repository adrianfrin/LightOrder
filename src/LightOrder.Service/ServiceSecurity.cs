using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace LightOrder.Service;

public static class ServiceSecurity
{
    public const string PipeName = "LightOrder.Control.v1";
    public const uint RejectRemoteClients = 0x8;
    private static readonly HashSet<string> TrustedOwners = new(StringComparer.Ordinal)
    {
        "S-1-5-18", "S-1-5-32-544",
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" // TrustedInstaller
    };
    private const FileSystemRights MutationRights = FileSystemRights.Write | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    public static SecurityIdentifier ValidateOwnerSid(string? value)
    {
        SecurityIdentifier sid;
        try { sid = new SecurityIdentifier(value ?? ""); }
        catch (ArgumentException) { throw new InvalidDataException("ownerSid must be an installation user's Windows account SID."); }
        if (!sid.IsAccountSid() || sid.AccountDomainSid is null || sid.Value != value)
            throw new InvalidDataException("ownerSid must be a canonical Windows account SID.");
        return sid;
    }

    public static bool IsTrustedOwner(string? sid) => sid is not null && TrustedOwners.Contains(sid);
    public static bool IsUnsafeGrant(string sid, FileSystemRights rights, AccessControlType type) =>
        type == AccessControlType.Allow && !IsTrustedOwner(sid) && (rights & MutationRights) != 0;

    public static void ValidateProtectedPath(string path, bool directory)
    {
        path = Path.GetFullPath(path);
        FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Required protected deployment path is missing.", path);
        RejectReparse(info);
        FileSystemSecurity acl = directory
            ? ((DirectoryInfo)info).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : ((FileInfo)info).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!IsTrustedOwner(acl.GetOwner(typeof(SecurityIdentifier))?.Value))
            throw new UnauthorizedAccessException("Deployment paths must be owned by Administrators or SYSTEM.");
        var raw = new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0);
        if (raw.DiscretionaryAcl is null) throw new UnauthorizedAccessException("A protected deployment path has no DACL.");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (IsUnsafeGrant(rule.IdentityReference.Value, rule.FileSystemRights, rule.AccessControlType))
                throw new UnauthorizedAccessException("Deployment paths grant modification rights to an untrusted principal.");
        // A writable parent must not permit replacing the protected child directory/file.
        for (var parent = directory ? ((DirectoryInfo)info).Parent : ((FileInfo)info).Directory;
             parent is not null; parent = parent.Parent)
        {
            RejectReparse(parent);
            var parentAcl = parent.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            if (!IsTrustedOwner(parentAcl.GetOwner(typeof(SecurityIdentifier))?.Value))
                throw new UnauthorizedAccessException("Deployment ancestors must have a trusted owner.");
            var parentRaw = new RawSecurityDescriptor(parentAcl.GetSecurityDescriptorBinaryForm(), 0);
            if (parentRaw.DiscretionaryAcl is null) throw new UnauthorizedAccessException("Deployment ancestor has no DACL.");
            foreach (FileSystemAccessRule rule in parentAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if ((rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 && rule.AccessControlType == AccessControlType.Allow &&
                    !IsTrustedOwner(rule.IdentityReference.Value) &&
                    (rule.FileSystemRights & (FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0)
                    throw new UnauthorizedAccessException("Deployment ancestor permits replacement by an untrusted principal.");
        }
    }

    public static void ValidateTree(string directory)
    {
        ValidateProtectedPath(directory, true);
        foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            RejectReparse(item);
            if (item is DirectoryInfo child) ValidateTree(child.FullName);
            else ValidateProtectedPath(item.FullName, false);
        }
    }

    private static void RejectReparse(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Reparse points are not allowed in service deployment or state paths.");
    }

    public static PipeSecurity CreatePipeSecurity(SecurityIdentifier owner)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        // Exclude CreateNewInstance. Clients must request specific ReadWrite rights rather than GENERIC_WRITE.
        security.AddAccessRule(new PipeAccessRule(owner,
            PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes |
            PipeAccessRights.WriteAttributes | PipeAccessRights.ReadExtendedAttributes | PipeAccessRights.WriteExtendedAttributes |
            PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return security;
    }

    public static NamedPipeServerStream CreatePipe(SecurityIdentifier owner)
    {
        var bytes = CreatePipeSecurity(owner).GetSecurityDescriptorBinaryForm();
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pin.AddrOfPinnedObject() };
            // PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE.
            var handle = CreateNamedPipeW(@"\\.\pipe\" + PipeName, 0x40080003, RejectRemoteClients,
                1, 65536, 65536, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "Cannot create the exclusive LightOrder pipe. Check permissions or another service instance.");
            }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { pin.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes attributes);
}
