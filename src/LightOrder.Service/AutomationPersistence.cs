using System.Text.Json;

namespace LightOrder.Service;

// Inject an in-memory store for hardware-free tests. Production uses the protected file store.
public interface IAutomationStore
{
    byte[]? LoadDocument();
    byte[]? LoadJournal();
    void SaveDocument(byte[] bytes);
    void SaveJournal(byte[] bytes);
}

public sealed class AutomationFileStore(string directory) : IAutomationStore
{
    public byte[]? LoadDocument() => Load("automation.json", AutomationValidation.MaxBytes);
    public byte[]? LoadJournal() => Load("automation-journal.json", AutomationJournal.MaxBytes);
    public void SaveDocument(byte[] bytes) => Save("automation.json", bytes, AutomationValidation.MaxBytes);
    public void SaveJournal(byte[] bytes) => Save("automation-journal.json", bytes, AutomationJournal.MaxBytes);

    private byte[]? Load(string name, int maximum)
    {
        ServiceSecurity.ValidateProtectedPath(directory, true);
        var path = Path.Combine(directory, name);
        if (!File.Exists(path)) return null;
        ServiceSecurity.ValidateProtectedPath(path, false);
        return ServiceSettings.ReadBounded(path, maximum);
    }

    private void Save(string name, byte[] bytes, int maximum)
    {
        if (bytes.Length > maximum) throw new InvalidDataException("Automation state exceeds its size budget.");
        ServiceSecurity.ValidateProtectedPath(directory, true);
        var path = Path.Combine(directory, name);
        if (File.Exists(path)) ServiceSecurity.ValidateProtectedPath(path, false);
        var temporary = Path.Combine(directory, ".automation-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            ServiceSecurity.ValidateProtectedPath(temporary, false);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class AutomationJournal
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, AutomationJournalRule> Rules { get; set; } = new(StringComparer.Ordinal);
}
internal sealed class AutomationJournalRule
{
    public int DailyHighWater { get; set; }
    public AutomationExecutionState State { get; set; } = AutomationExecutionState.Ready;
    public List<AutomationProcessIdentity> Seen { get; set; } = [];
    public List<AutomationProcessIdentity> Active { get; set; } = [];
}
public sealed record AutomationProcessIdentity(int Pid, DateTimeOffset StartTimeUtc);
public sealed record AutomationProcessSnapshot(int Pid, DateTimeOffset StartTimeUtc, string ProcessName, int SessionId);
public interface ITargetedAutomationProcessSource : IAutomationProcessSource
{
    IReadOnlyList<AutomationProcessSnapshot> Snapshot(string[] processNames);
}
public interface IAutomationProcessSource
{
    // Return only the installation owner's processes. Throw on incomplete/uncertain snapshots.
    IReadOnlyList<AutomationProcessSnapshot> Snapshot();
}
