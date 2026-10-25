using System.Text.Json;
using ControlCenter.Contracts;

namespace ControlCenter.WindowsPolicy;

internal sealed record Ownership(string Property, int? Pid, string? StartTimeUtc, string Original, string Applied)
{
    public string Key => $"{Property}:{Pid}:{StartTimeUtc}";
}
internal sealed record JournalDocument(int Version, List<Ownership> Entries);

internal sealed class RecoveryJournal : IDisposable
{
    private readonly FileStream? lease;
    public string Path { get; }
    public List<Ownership> Entries { get; set; } = [];
    public bool PendingFromPreviousRun { get; set; }
    private readonly bool memoryOnly;

    public RecoveryJournal(string? directory = null, bool memoryOnly = false)
    {
        this.memoryOnly = memoryOnly;
        Path = System.IO.Path.Combine(directory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IndependentControlCenter", "WindowsPolicy"), "recovery-v1.json");
        if (memoryOnly) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        try
        {
            lease = new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new PluginException("PluginBusy", $"Cannot lock the per-user WindowsPolicy journal. Another instance may be running. {ex.Message}");
        }
        try
        {
            if (!File.Exists(Path)) return;
            var info = new FileInfo(Path);
            if (info.Length > 1024 * 1024) throw new InvalidDataException("Recovery journal is larger than 1 MiB.");
            var document = JsonSerializer.Deserialize<JournalDocument>(File.ReadAllText(Path), Wire.Json)
                ?? throw new InvalidDataException("Recovery journal is empty.");
            if (document.Version != 1 || document.Entries is null) throw new InvalidDataException("Unsupported recovery journal format.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in document.Entries)
            {
                ValidateEntry(entry);
                if (!keys.Add(entry.Key)) throw new InvalidDataException("Duplicate recovery journal property.");
            }
            Entries = document.Entries;
            PendingFromPreviousRun = Entries.Count > 0;
        }
        catch (Exception ex)
        {
            lease.Dispose();
            throw new PluginException("JournalInvalid", $"Recovery journal was preserved and not applied: {ex.Message}");
        }
    }

    private static void ValidateEntry(Ownership entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Original) || string.IsNullOrWhiteSpace(entry.Applied))
            throw new InvalidDataException("Invalid journal entry.");
        if (entry.Property == "power")
        {
            if (entry.Pid is not null || entry.StartTimeUtc is not null || !Guid.TryParse(entry.Original, out _) || !Guid.TryParse(entry.Applied, out _))
                throw new InvalidDataException("Invalid power journal entry.");
            return;
        }
        if (entry.Pid is null or <= 0 || entry.StartTimeUtc is null || PolicyValues.NormalizeStartTime(entry.StartTimeUtc) != entry.StartTimeUtc)
            throw new InvalidDataException("Invalid process journal identity.");
        switch (entry.Property)
        {
            case "priority":
                PolicyValues.ValidatePriority(entry.Original);
                PolicyValues.ValidatePriority(entry.Applied);
                break;
            case "affinity":
                PolicyValues.ParseMask(entry.Original);
                PolicyValues.ParseMask(entry.Applied);
                break;
            default: throw new InvalidDataException("Unknown journal property.");
        }
    }

    public void Save()
    {
        if (memoryOnly) return;
        // Same-directory replacement keeps a failed write from truncating the prior journal.
        string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(new JournalDocument(1, Entries), Wire.Json);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginException("JournalUnavailable", $"Cannot persist recovery journal; no new unjournaled writes are permitted. {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose() => lease?.Dispose();
}
