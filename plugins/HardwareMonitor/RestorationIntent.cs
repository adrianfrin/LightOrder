using System.Text.Json;

// Evidence and an interlock, not a substitute for an independent hardware watchdog.
// Only verified restoration clears this marker. A new process must not invent a baseline
// from hardware that may still carry the previous process's manual PWM values.
sealed class RestorationIntent(string path, Action<string>? validatePath = null)
{
    private readonly Action<string> validate = validatePath ?? ProtectedIntentPath.ValidateFile;
    internal static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LightOrder", "FanControl", "restoration-required.json");

    public void Record(IReadOnlyDictionary<string, byte> baseline)
    {
        validate(path);
        // CreateNew also refuses an unresolved intent from an earlier owner.
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, new { schemaVersion = 1, board = "B650M AORUS ELITE AX ICE",
            chip = "/lpc/it8689e/0", channels = new[] { 0, 3 }, baseline, createdUtc = DateTimeOffset.UtcNow });
        stream.Flush(flushToDisk: true);
    }

    public void ClearVerified()
    {
        validate(path);
        File.Delete(path);
    }

    internal static void RefuseUnresolved(string? intentPath = null, Action<string>? validatePath = null)
    {
        var target = intentPath ?? DefaultPath;
        (validatePath ?? ProtectedIntentPath.ValidateFile)(target);
        // File.Exists hides access/I/O errors. Only an absent path permits takeover.
        try { _ = File.GetAttributes(target); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        throw new InvalidOperationException(
            "Unresolved fan restoration intent: " + target +
            ". Automatic takeover refused. Verify firmware fan control through a documented hardware recovery procedure before clearing this evidence.");
    }
}

static class FanRestoration
{
    // Always attempt both channels, even when the first setter throws. A successful
    // SetDefault call is not evidence of restoration until report readback matches.
    internal static void RestoreVerified(Action radiatorDefault, Action caseDefault,
        IReadOnlyDictionary<string, byte> baseline, Func<IReadOnlyDictionary<string, byte>> readRegisters)
    {
        var failures = new List<string>();
        try { radiatorDefault(); } catch (Exception e) { failures.Add("radiator: " + e.Message); }
        try { caseDefault(); } catch (Exception e) { failures.Add("case: " + e.Message); }
        try
        {
            if (!FanSessionPolicy.Restored(baseline, readRegisters()))
                failures.Add("Bank 0 register readback differs from captured baseline.");
        }
        catch (Exception e) { failures.Add("Register verification: " + e.Message); }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("; ", failures));
    }
}
