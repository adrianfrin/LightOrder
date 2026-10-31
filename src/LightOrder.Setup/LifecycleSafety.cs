using System.Security.Principal;
using System.Text.Json;

namespace LightOrder.Setup;

internal static class LifecycleSafety
{
    internal static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    internal static void RequireExactRoot(string candidate, string fixedRoot)
    {
        if (!Path.IsPathFullyQualified(candidate) || candidate.StartsWith(@"\\") || !SamePath(candidate, fixedRoot))
            throw new InvalidDataException("Unexpected lifecycle root.");
        CheckNoReparse(candidate);
    }

    internal static void CheckNoReparse(string target)
    {
        for (var current = Path.GetFullPath(target); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Lifecycle paths cannot contain reparse points: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static void InspectTree(string root)
    {
        CheckNoReparse(root);
        if (!Directory.Exists(root)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            CheckNoReparse(entry);
            if (Directory.Exists(entry)) InspectTree(entry);
        }
    }

    // Preflight the complete tree before the first deletion, then check each entry again.
    internal static void DeleteTree(string candidate, string fixedRoot)
    {
        RequireExactRoot(candidate, fixedRoot);
        InspectTree(candidate);
        DeleteInspected(candidate);
    }

    private static void DeleteInspected(string path)
    {
        CheckNoReparse(path);
        if (!Directory.Exists(path)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            CheckNoReparse(entry);
            if (Directory.Exists(entry)) DeleteInspected(entry);
            else { File.SetAttributes(entry, FileAttributes.Normal); File.Delete(entry); }
        }
        Directory.Delete(path);
    }

    internal static void RefuseUnresolvedRestoration(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "FanControl", "restoration-required.json");
        CheckNoReparse(path);
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        throw new IOException("Fan restoration remains unverified. Installation/removal stopped; program files, service registration and recovery evidence were retained. Verify firmware fan control before retrying.");
    }

    internal static void ValidateProtectedDirectory(string path)
    {
        CheckNoReparse(path);
        var info = new DirectoryInfo(path);
        var acl = info.GetAccessControl(System.Security.AccessControl.AccessControlSections.Owner | System.Security.AccessControl.AccessControlSections.Access);
        var trusted = new[] { "S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" };
        if (!trusted.Contains(acl.GetOwner(typeof(SecurityIdentifier))?.Value)) throw new UnauthorizedAccessException("Untrusted staging directory owner.");
        const System.Security.AccessControl.FileSystemRights mutations = System.Security.AccessControl.FileSystemRights.Write | System.Security.AccessControl.FileSystemRights.Delete | System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles | System.Security.AccessControl.FileSystemRights.ChangePermissions | System.Security.AccessControl.FileSystemRights.TakeOwnership;
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow && !trusted.Contains(rule.IdentityReference.Value) && (rule.FileSystemRights & mutations) != 0)
                throw new UnauthorizedAccessException("Staging directory grants untrusted write access.");
        for (var parent = info.Parent; parent is not null; parent = parent.Parent)
        {
            var parentAcl = parent.GetAccessControl(System.Security.AccessControl.AccessControlSections.Owner | System.Security.AccessControl.AccessControlSections.Access);
            if (!trusted.Contains(parentAcl.GetOwner(typeof(SecurityIdentifier))?.Value)) throw new UnauthorizedAccessException("Untrusted staging ancestor owner.");
            foreach (System.Security.AccessControl.FileSystemAccessRule rule in parentAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if ((rule.PropagationFlags & System.Security.AccessControl.PropagationFlags.InheritOnly) == 0 && rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow && !trusted.Contains(rule.IdentityReference.Value) &&
                    (rule.FileSystemRights & (System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles | System.Security.AccessControl.FileSystemRights.ChangePermissions | System.Security.AccessControl.FileSystemRights.TakeOwnership)) != 0)
                    throw new UnauthorizedAccessException("Staging ancestor permits replacement by an untrusted user.");
        }
    }

    internal static SecurityIdentifier ValidateOwnerSid(string value)
    {
        SecurityIdentifier sid;
        try { sid = new SecurityIdentifier(value); }
        catch (ArgumentException e) { throw new InvalidDataException("Owner must be a canonical Windows account SID.", e); }
        if (!sid.IsAccountSid() || sid.AccountDomainSid is null || sid.Value != value)
            throw new InvalidDataException("Owner must be a canonical Windows account SID.");
        return sid;
    }

    internal static void ValidateConfiguration(byte[] bytes)
    {
        if (bytes.Length > 16384) throw new InvalidDataException("service.json exceeds 16 KiB.");
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported service configuration.");
        ValidateOwnerSid(root.GetProperty("ownerSid").GetString() ?? "");
        var plugins = root.GetProperty("enabledPlugins").EnumerateArray().Select(p => p.GetString()).ToArray();
        if (plugins.Length > 32 || plugins.Distinct(StringComparer.Ordinal).Count() != plugins.Length ||
            plugins.Any(p => p is null || p.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(p[0]) || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-')))
            throw new InvalidDataException("Invalid enabledPlugins configuration.");
    }

    internal static bool IsDirectoryEntry(string fullName, string name) => fullName.EndsWith('/') || fullName.EndsWith('\\') || string.IsNullOrEmpty(name);

    internal static void ValidateEntry(string entry)
    {
        var normalized = entry.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(entry) || entry.Contains(':') ||
            normalized.Split('/').Any(s => s is ".." or "." or "" || s.EndsWith('.') || s.EndsWith(' ') || s.Any(c => c < 32 || "<>\"|?*".Contains(c)) ||
                new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(s.Split('.')[0], StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("Unsafe payload entry: " + entry);
    }

    internal static bool IsOwnedBackend(string executable, string[] args, string installRoot, string dataRoot)
    {
        if (!SamePath(executable, Path.Combine(installRoot, "plugins", "OpenRgb", "runtime", "OpenRGB.exe")) || args.Length != 11) return false;
        var runs = Path.Combine(dataRoot, "OpenRgb", "runs");
        var config = args[2];
        return args[1] == "--config" && Path.IsPathFullyQualified(config) &&
            SamePath(Path.GetDirectoryName(config) ?? "", runs) && Guid.TryParseExact(Path.GetFileName(config), "N", out _) &&
            args[3..].SequenceEqual(new[] { "--server", "--server-host", "127.0.0.1", "--server-port", "16742", "--noautoconnect", "--loglevel", "warning" });
    }
}
