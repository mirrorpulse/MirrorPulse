using System.Globalization;
using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Diagnostics;

public sealed record SafeLogEntry(int SchemaVersion, Guid DiagnosticId, DateTimeOffset OccurredAt,
    string Level, string Category, string Code, string Message, IReadOnlyDictionary<string, string> Fields);
public sealed record SafeDiagnosticData(string Code, string Message, string Severity);
public sealed record SafeDiagnosticEvent(Guid EventId, string Source, SafeDiagnosticData Diagnostic,
    DateTimeOffset OccurredAt, string? CorrelationId, IReadOnlyDictionary<string, string> Properties);

/// <summary>Emits only registered descriptions and typed fields; arbitrary messages and values are omitted.</summary>
public static class SafeDiagnosticPolicy
{
    private static readonly Dictionary<string, string> Messages = new(StringComparer.Ordinal)
    {
        ["HostStartupFailed"] = "The Host could not start.",
        ["ShellRootPolicyMismatch"] = "The actual Shell root policy does not match content synchronization.",
        ["CloudRootMetadataMismatch"] = "The actual Cloud Files registration does not match the product root.",
        ["CloudRootIdentityMismatch"] = "The existing Cloud Files registration belongs to another root identity.",
        ["ShellRootOwnershipMismatch"] = "The existing Shell registration belongs to another provider.",
        ["ShellRootPathMismatch"] = "The existing Shell registration belongs to another sync root.",
        ["ShellRootRegistrationFailed"] = "Windows rejected the Shell root registration.",
        ["JournalReadFailed"] = "The local journal could not be read.",
        ["JournalCommandFailed"] = "A local journal command failed.",
        ["JournalAcknowledgementFailed"] = "An accepted Worker result could not be acknowledged.",
        ["MutationOutcomeAmbiguous"] = "A remote outcome requires reconciliation before retry.",
        ["RemoteBatchRetry"] = "The remote batch requires another attempt.",
        ["RemotePollFailed"] = "Remote polling failed.",
        ["TransferComplete"] = "The transfer completed.",
        ["sync.failed"] = "Synchronization failed.",
    };

    public static SafeLogEntry Sanitize(LogEntry entry, Guid? diagnosticId = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string code = Code(entry.Message);
        return new(1, diagnosticId ?? Guid.NewGuid(), entry.OccurredAt,
            Enum.IsDefined(entry.Level) ? entry.Level.ToString() : "Warning", Source(entry.Category), code,
            Message(code), Fields(entry.Fields.Select(field => KeyValuePair.Create(field.Name, field.Value))));
    }

    public static SafeDiagnosticEvent Sanitize(DiagnosticEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string code = Code(value.Diagnostic.Code);
        return new(value.EventId, Source(value.Source), new(code, Message(code),
            Enum.IsDefined(value.Diagnostic.Severity) ? value.Diagnostic.Severity.ToString() : "Error"),
            value.OccurredAt, Guid.TryParse(value.CorrelationId, out Guid id) ? id.ToString("D") : null, Fields(value.Properties));
    }

    public static string ClassifyFailure(Exception exception) => exception switch
    {
        InvalidDataException => "InvalidData",
        UnauthorizedAccessException => "Authorization",
        OperationCanceledException => "Cancelled",
        IOException => "IO",
        _ => "Internal",
    };

    internal static SafeLogEntry SanitizeLegacyLog(JsonElement root)
    {
        string? Read(string name)
        {
            if (root.ValueKind != JsonValueKind.Object) return null;
            foreach (JsonProperty property in root.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
            return null;
        }
        string code = Code(Read("Code") ?? Read("Message"));
        var fields = new List<KeyValuePair<string, string>>();
        if (root.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in root.EnumerateObject())
                if (property.Name.Equals("Fields", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty field in property.Value.EnumerateObject())
                        if (field.Value.ValueKind == JsonValueKind.String) fields.Add(KeyValuePair.Create(field.Name, field.Value.GetString()!));
        return new(1, Guid.TryParse(Read("DiagnosticId"), out Guid diagnosticId) ? diagnosticId : Guid.NewGuid(),
            DateTimeOffset.TryParse(Read("OccurredAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset at)
                ? at : DateTimeOffset.UnixEpoch,
            Enum.TryParse(Read("Level"), out LogLevel level) && Enum.IsDefined(level) ? level.ToString() : "Warning",
            Source(Read("Category")), code, Message(code), Fields(fields));
    }

    private static string Source(string? value) => value is "host" or "worker" or "sync" or "CloudFiles.Upload" or "diagnostics" ? value : "unknown";
    private static string Code(string? value) => value == "Transfer complete" ? "TransferComplete" :
        value is not null && Messages.ContainsKey(value) ? value : "UnclassifiedDiagnostic";
    private static string Message(string code) => Messages.GetValueOrDefault(code, LogFieldPolicy.RedactedValue);

    private static Dictionary<string, string> Fields(IEnumerable<KeyValuePair<string, string>> values)
    {
        var safe = new Dictionary<string, string>(StringComparer.Ordinal);
        int omitted = 0;
        foreach ((string name, string value) in values)
        {
            string? accepted = name switch
            {
                "operationId" or "instanceId" or "diagnosticId" or "correlationId" =>
                    Guid.TryParse(value, out Guid id) ? id.ToString("D") : null,
                "hresult" => value.Length == 8 && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint number)
                    ? number.ToString("X8", CultureInfo.InvariantCulture) : null,
                "attempt" or "bytesTransferred" or "totalBytes" or "pendingCount" =>
                    long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long count) && count >= 0
                        ? count.ToString(CultureInfo.InvariantCulture) : null,
                "failureCategory" => value is "IO" or "InvalidData" or "Authorization" or "Cancelled" or "Internal" ? value : null,
                "kind" => Enum.TryParse(value, out MirrorPulseWorkerChangeKind kind) && Enum.IsDefined(kind) ? kind.ToString() : null,
                "confirmationOutcome" => Enum.TryParse(value, out MirrorPulseContentConfirmationOutcome outcome) && Enum.IsDefined(outcome) ? outcome.ToString() : null,
                "confirmationStage" => Enum.TryParse(value, out MirrorPulseContentConfirmationStage stage) && Enum.IsDefined(stage) ? stage.ToString() : null,
                "nativeApplied" or "nativeVerified" or "projectionCommitted" => bool.TryParse(value, out bool flag) ? flag.ToString() : null,
                "redacted" => value == LogFieldPolicy.RedactedValue ? value : null,
                "omittedFieldCount" => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count >= 0
                    ? count.ToString(CultureInfo.InvariantCulture) : null,
                _ => null,
            };
            if (accepted is not null) safe[name] = accepted;
            else omitted++;
        }
        if (omitted != 0)
        {
            safe["omittedFieldCount"] = omitted.ToString(CultureInfo.InvariantCulture);
            safe["redacted"] = LogFieldPolicy.RedactedValue;
        }
        return safe;
    }
}
