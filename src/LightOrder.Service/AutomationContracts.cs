using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ControlCenter.Contracts;

namespace LightOrder.Service;

public enum AutomationTriggerKind { Daily, Process }
public enum AutomationExecutionState { Disabled, Ready, Running, Completed, Paused }

public sealed record AutomationDocument(int SchemaVersion, AutomationRule[] Rules);
public sealed record AutomationRule(string Id, string Name, bool Enabled, AutomationTrigger Trigger,
    DeviceCommand[] Commands, DeviceCommand[]? ExitCommands = null);
public sealed record AutomationTrigger(AutomationTriggerKind Kind, string? Time = null, int[]? Days = null, string? ProcessName = null);
public sealed record AutomationRuleStatus(string RuleId, AutomationExecutionState State, string? Detail);
public sealed record AutomationReadResult(int SchemaVersion, AutomationRule[] Rules, AutomationRuleStatus[] Status);

public static class AutomationValidation
{
    public const int MaxBytes = 128 * 1024;
    public static readonly JsonSerializerOptions Json = CreateJson();
    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(Wire.Json) { MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            PropertyNameCaseInsensitive = false, RespectRequiredConstructorParameters = true };
        options.Converters.Add(new JsonStringEnumConverter<AutomationTriggerKind>(JsonNamingPolicy.CamelCase, false));
        options.Converters.Add(new JsonStringEnumConverter<AutomationExecutionState>(JsonNamingPolicy.CamelCase, false));
        return options;
    }

    public static AutomationDocument Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Automation exceeds 128 KiB.");
        using var parsed = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        CheckJson(parsed.RootElement);
        if (parsed.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
            foreach (var rule in rules.EnumerateArray())
                if (rule.ValueKind == JsonValueKind.Object && rule.TryGetProperty("trigger", out var trigger) && trigger.ValueKind == JsonValueKind.Object &&
                    trigger.TryGetProperty("kind", out var kind) && (kind.ValueKind != JsonValueKind.String || kind.GetString() is not ("daily" or "process")))
                    throw new InvalidDataException("Trigger kind must be exactly daily or process.");
        var document = parsed.RootElement.Deserialize<AutomationDocument>(Json) ?? throw new InvalidDataException("Missing automation document.");
        Validate(document);
        return document;
    }

    internal static void CheckJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property.");
                CheckJson(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckJson(item);
        else if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out var number) || !double.IsFinite(number)))
            throw new InvalidDataException("Automation numbers must be finite.");
    }

    public static void Validate(AutomationDocument document)
    {
        if (document.SchemaVersion != 1 || document.Rules is null || document.Rules.Length > 64)
            throw new InvalidDataException("Automation requires schemaVersion 1 and at most 64 rules.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in document.Rules)
        {
            if (rule is null || !ValidId(rule.Id) || !ids.Add(rule.Id) || string.IsNullOrWhiteSpace(rule.Name) ||
                rule.Name.Length > 128 || rule.Name.Any(char.IsControl) || rule.Trigger is null)
                throw new InvalidDataException("Invalid or duplicate automation rule identity.");
            var trigger = rule.Trigger;
            switch (trigger.Kind)
            {
                case AutomationTriggerKind.Daily:
                    if (trigger.ProcessName is not null || trigger.Time is null || trigger.Time.Length != 5 ||
                        !TimeOnly.TryParseExact(trigger.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                        trigger.Days is null || trigger.Days.Length is < 1 or > 7 || trigger.Days.Any(d => d is < 0 or > 6) ||
                        trigger.Days.Distinct().Count() != trigger.Days.Length || rule.ExitCommands is { Length: > 0 })
                        throw new InvalidDataException("Daily triggers need HH:mm, unique days 0..6, and no exit commands.");
                    break;
                case AutomationTriggerKind.Process:
                    if (trigger.Time is not null || trigger.Days is not null || !ValidProcessName(trigger.ProcessName))
                        throw new InvalidDataException("Process triggers require an exact executable filename ending in .exe; paths and wildcards are forbidden.");
                    break;
                default: throw new InvalidDataException("Unknown automation trigger kind.");
            }
            ValidateCommands(rule.Commands, false, trigger.Kind);
            if (rule.ExitCommands is not null) ValidateCommands(rule.ExitCommands, true, trigger.Kind);
            // Windows policy's device.release is global, including process and power baselines.
            // No automated exit may call it: the plugin cleans up dead process identities itself.
            if (rule.ExitCommands?.Any(c => c.Capability is "device.release" or "process.policy") == true)
                throw new InvalidDataException("Process exit cannot perform global device.release or write a dead process policy.");
        }
        if (JsonSerializer.SerializeToUtf8Bytes(document, Json).Length > MaxBytes)
            throw new InvalidDataException("Automation exceeds 128 KiB.");
    }

    private static void ValidateCommands(DeviceCommand[]? commands, bool exit, AutomationTriggerKind kind)
    {
        if (commands is null || commands.Length > 16 || (!exit && commands.Length == 0))
            throw new InvalidDataException("Each command list must contain at most 16 commands; start commands cannot be empty.");
        foreach (var command in commands)
        {
            if (command is null || string.IsNullOrWhiteSpace(command.DeviceId) || command.DeviceId.Length > 386 || command.DeviceId.Any(char.IsControl) ||
                !ValidId(command.Capability) || command.Parameters.ValueKind != JsonValueKind.Object || command.Parameters.GetRawText().Length > 65536)
                throw new InvalidDataException("Invalid device command.");
            CheckJson(command.Parameters);
            if (command.Capability == "process.policy")
            {
                if (kind != AutomationTriggerKind.Process || exit || command.Parameters.TryGetProperty("pid", out _) || command.Parameters.TryGetProperty("startTimeUtc", out _))
                    throw new InvalidDataException("Process policies require a process trigger; pid/startTimeUtc are supplied by the service.");
            }
        }
    }

    private static bool ValidId(string? value) => value is { Length: > 0 and <= 128 } &&
        char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    public static bool ValidProcessName(string? value) => value is { Length: > 4 and <= 128 } &&
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && value[..^4].Any(c => c != '.') &&
        !value.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') && value.Trim() == value;
}
