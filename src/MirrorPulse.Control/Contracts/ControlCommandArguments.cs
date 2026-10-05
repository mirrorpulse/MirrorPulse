using System.Text.Json.Serialization;
using MirrorPulse.Core.Conflicts;

namespace MirrorPulse.Control.Contracts;

/// <summary>
/// Marker for a command argument object accepted by Host.
/// </summary>
public interface IMirrorPulseControlArguments;

/// <summary>
/// Marks a property that must be redacted by diagnostics and logging layers.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class MirrorPulseSensitiveDataAttribute : Attribute;

public sealed record ControlEmptyArguments : IMirrorPulseControlArguments;

public sealed record SyncRefreshArguments(bool Force = false) : IMirrorPulseControlArguments;

public sealed record HostLifecycleArguments(bool Force = false) : IMirrorPulseControlArguments;

/// <summary>
/// Snapshot of the current user's Host process and lifecycle channel.
/// </summary>
public sealed record MirrorPulseHostStatus(
    string State,
    int ProcessId,
    string ControlPipeName,
    DateTimeOffset StartedAt,
    bool CanStop,
    bool CanRestart,
    string? RequestedAction = null);

public sealed record MirrorPulseControlTopology(
    IReadOnlyList<MirrorPulseControlInstallation> Installations,
    IReadOnlyList<MirrorPulseControlInstance> Instances,
    IReadOnlyList<MirrorPulseControlRoot> Roots,
    IReadOnlyList<MirrorPulseControlRuntimeState> RuntimeStates);

public sealed record MirrorPulseControlInstallation(
    string AdapterId,
    string InstallId,
    string Version,
    string Publisher,
    string InstallationDirectory,
    string PackageSha256,
    string Source,
    string? SourceReference,
    bool IsSigned,
    DateTimeOffset InstalledAt,
    string LifecycleState,
    string MinimumMirrorPulseVersion);

public sealed record MirrorPulseControlInstance(
    string AdapterId,
    string InstallId,
    string InstanceId,
    string DisplayName,
    IReadOnlyDictionary<string, string> Configuration,
    IReadOnlyList<string> CredentialReferences,
    string FileCacheDirectory,
    string TransferCacheDirectory,
    bool Enabled,
    string LifecycleState,
    string? WorkerSessionId,
    DateTimeOffset CreatedAt);

public sealed record MirrorPulseControlRoot(
    string AdapterId,
    string InstanceId,
    string RootId,
    string UniquenessKey,
    string Label,
    string DirectoryName,
    bool CustomEntry,
    string State,
    DateTimeOffset RegisteredAt);

public sealed record MirrorPulseControlRuntimeState(
    string InstanceId,
    string Phase,
    bool RequiresFullRescan,
    DateTimeOffset? LastSuccessfulSync,
    string? LastErrorCode,
    string? TransferOperation,
    long? BytesTransferred,
    long? TotalBytes,
    DateTimeOffset? TransferUpdatedAt);

public sealed record AdapterInstallArguments(string PackagePath) : IMirrorPulseControlArguments;

public sealed record AdapterRemoveArguments(
    string AdapterId,
    bool Purge = false,
    string? InstallId = null) : IMirrorPulseControlArguments;

public sealed record InstanceListArguments(int? Limit = null, string? Cursor = null) : IMirrorPulseControlArguments;

public sealed record InstanceCreateArguments(
    string InstallId,
    string DisplayName,
    IReadOnlyDictionary<string, string> Configuration,
    IReadOnlyDictionary<string, string> RootLabels,
    [property: MirrorPulseSensitiveData] string? Secret,
    bool Enabled) : IMirrorPulseControlArguments;

public sealed record InstanceConfigureArguments(
    string InstanceId,
    string DisplayName,
    IReadOnlyDictionary<string, string> Configuration,
    IReadOnlyDictionary<string, string> RootLabels,
    [property: MirrorPulseSensitiveData] string? Secret = null,
    bool RemoveCredential = false) : IMirrorPulseControlArguments;

public sealed record InstanceIdArguments(string InstanceId) : IMirrorPulseControlArguments;

public sealed record InstanceEnableArguments(string InstanceId, bool Enabled) : IMirrorPulseControlArguments;

public sealed record InstanceSelectVersionArguments(string InstanceId, string InstallId) : IMirrorPulseControlArguments;

public sealed record ConflictListArguments(int? Limit = null, string? Cursor = null) : IMirrorPulseControlArguments;

public sealed record MirrorPulseControlConflict(
    string ConflictId,
    string InstanceId,
    string RelativePath,
    string Reason,
    string Status,
    string Source,
    string? LocalRevision,
    string? RemoteRevision,
    DateTimeOffset DetectedAt);

public sealed record MirrorPulseControlConflictList(
    IReadOnlyList<MirrorPulseControlConflict> Items,
    string? NextCursor = null);

public sealed record ConflictSnoozeArguments(Guid ConflictId) : IMirrorPulseControlArguments;

public sealed record ConflictResolveArguments(
    Guid ConflictId,
    MirrorPulseConflictAction Action,
    string? PreservedPath = null) : IMirrorPulseControlArguments;

public sealed record OperationIdArguments(string OperationId) : IMirrorPulseControlArguments;

public sealed record MirrorPulseControlOperation(
    string OperationId,
    string Action,
    string TargetId,
    string State);

public sealed record SettingsSetArguments(
    [property: MirrorPulseSensitiveData] IReadOnlyDictionary<string, string> Values) : IMirrorPulseControlArguments;

public sealed record MirrorPulseSettingsUpdateArguments(
    string? Locale = null,
    bool? DeveloperMode = null,
    bool? StartWithWindows = null,
    IReadOnlyList<string>? EnabledInstallations = null,
    string? SyncRootDisplayName = null) : IMirrorPulseControlArguments;

public sealed record MirrorPulseControlSettings(
    int SchemaVersion,
    string Locale,
    bool DeveloperMode,
    bool StartWithWindows,
    IReadOnlyList<string> EnabledInstallations,
    string SyncRootDisplayName);

public sealed record DiagnosticsArguments(
    bool IncludeLogs = false,
    string? OutputPath = null) : IMirrorPulseControlArguments;

public sealed record MirrorPulseControlDiagnosticsResult(
    string PackagePath,
    DateTimeOffset CreatedAt,
    bool IncludedLogs);
