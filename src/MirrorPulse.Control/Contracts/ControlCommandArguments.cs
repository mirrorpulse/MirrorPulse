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

public sealed record MirrorPulseControlRootRename(string OperationId, string SourceName, string TargetName,
    string Phase, DateTimeOffset UpdatedAt);

public sealed record MirrorPulseControlRootRenamePreparation(string OperationId, string RootId,
    string SourceName, string TargetName, string Phase, DateTimeOffset UpdatedAt);

public sealed record MirrorPulseControlRootStatus(MirrorPulseControlRoot Root, string SyncState,
    bool RequiresFullRescan, MirrorPulseControlRootRename? PendingRename);

public sealed record RootRecoverArguments(Guid OperationId) : IMirrorPulseControlArguments;

public sealed record RootPrepareRenameArguments(Guid RootId, string Label) : IMirrorPulseControlArguments;

public sealed record NamespaceMemberMoveArguments(Guid OperationId) : IMirrorPulseControlArguments;

public sealed record NamespaceMemberMoveStatusArguments(Guid OperationId) : IMirrorPulseControlArguments;

public sealed record NamespaceMemberRetirementArguments(Guid OperationId, bool Recursive = false) : IMirrorPulseControlArguments;

public sealed record NamespaceMemberRetirementStatusArguments(Guid OperationId, bool Recursive = false) : IMirrorPulseControlArguments;

public sealed record NamespaceMemberPrepareRetirementArguments(Guid OperationId, Guid MemberId, bool Recursive = false) : IMirrorPulseControlArguments;

public sealed record NamespaceMemberCreateArguments(Guid OperationId, Guid ParentMemberId,
    string Name, bool IsDirectory = false) : IMirrorPulseControlArguments;

public sealed record MirrorPulseControlNamespaceMemberCreation(Guid OperationId, Guid MemberId,
    Guid ParentMemberId, string RelativePath, bool IsDirectory, bool Registered);

public sealed record NamespaceMemberPrepareImportArguments(Guid OperationId, Guid ParentMemberId,
    string Name, string SourcePath) : IMirrorPulseControlArguments;

public sealed record NamespaceMemberImportArguments(Guid OperationId) : IMirrorPulseControlArguments;

public sealed record NamespaceMemberImportStatusArguments(Guid OperationId) : IMirrorPulseControlArguments;

public sealed record NamespaceMemberPrepareReplaceArguments(Guid OperationId, Guid MemberId, string SourcePath) : IMirrorPulseControlArguments;
public sealed record NamespaceMemberReplaceArguments(Guid OperationId) : IMirrorPulseControlArguments;
public sealed record NamespaceMemberReplacementStatusArguments(Guid OperationId) : IMirrorPulseControlArguments;
public sealed record NamespaceProtectionCapturesArguments(Guid RootId, Guid? AfterManifestId = null, int Limit = 256) : IMirrorPulseControlArguments;
public sealed record NamespaceProtectionRecoverTreeArguments(Guid ManifestId) : IMirrorPulseControlArguments;
public sealed record MirrorPulseControlPermissionCapture(Guid ManifestId, Guid RootId, string Phase, int CapturedMembers,
    int ExpectedMembers, DateTimeOffset CreatedAt, DateTimeOffset? SealedAt);
public sealed record MirrorPulseControlOriginalTreeRecovery(Guid ManifestId, string Outcome, int CompletedMembers,
    DateTimeOffset ObservedAt, string? RecoveryReason);

public sealed record MirrorPulseControlNamespaceReplacementPreparation(Guid OperationId, Guid MemberId, string RelativePath,
    long Length, string Sha256, DateTimeOffset PreparedAt);

public sealed record MirrorPulseControlNamespaceReplacementCompletion(Guid OperationId, Guid MemberId, string RelativePath,
    DateTimeOffset CompletedAt, bool RescanRequested);

public sealed record MirrorPulseControlNamespaceReplacementStatus(Guid OperationId, Guid MemberId, string RelativePath, string Phase,
    long Length, string Sha256, bool Started, bool ActionRecorded, bool BackupRecorded, bool WriteStarted,
    bool ContentRecorded, bool ProtectionRestored, bool Completed, bool Cancelled, bool RequiresRecovery);

public sealed record MirrorPulseControlNamespaceImportPreparation(Guid OperationId, Guid ParentMemberId,
    string Name, long Length, string Sha256, DateTimeOffset PreparedAt);

/// <summary>Historical local facts; these fields do not assert remote acceptance or current native authority.</summary>
public sealed record MirrorPulseControlNamespaceImportStatus(Guid OperationId, string RelativePath, string Phase,
    long Length, string Sha256, bool Started, bool OriginalBindingRecorded, bool ContentRecorded,
    bool Converted, bool Protected, bool Registered, bool RequiresRecovery);

public sealed record NamespaceMemberInspectArguments(string RelativePath, bool IsDirectory = false) : IMirrorPulseControlArguments;

public sealed record NamespaceMemberTreeArguments(Guid MemberId) : IMirrorPulseControlArguments;

public sealed record NamespaceMemberPrepareMoveArguments(Guid OperationId, Guid MemberId,
    Guid TargetParentMemberId, string TargetName) : IMirrorPulseControlArguments;

public sealed record MirrorPulseControlNamespaceMemberInspection(Guid MemberId, Guid? ActualParentMemberId,
    string? RootId, string RelativePath, bool IsDirectory, Guid? NativeItemId, DateTimeOffset ObservedAt);

public sealed record MirrorPulseControlNamespaceMemberTreeEntry(Guid MemberId, Guid? ParentMemberId,
    string RelativePath, bool IsDirectory, int Depth);

public sealed record MirrorPulseControlNamespaceMemberMovePreparation(Guid OperationId, Guid MemberId,
    Guid TargetParentMemberId, string SourceRelativePath, string TargetRelativePath, DateTimeOffset PreparedAt);

public sealed record NamespaceBirthRecoverArguments(Guid RecoveryId) : IMirrorPulseControlArguments;

public sealed record MirrorPulseControlNamespaceMemberMoveResult(Guid OperationId, Guid ItemId,
    string RemoteId, string RelativePath, bool IsDirectory, bool CurrentNameCommitted,
    bool RequiresSubtreeReconciliation, string? OfficialRemoteRevision);

public sealed record MirrorPulseControlNamespaceMemberMoveStatus(Guid OperationId, Guid MemberId, bool IsDirectory,
    string SourceRelativePath, string TargetRelativePath, string Phase, bool Started,
    bool NativeObserved, bool OfficialProjectionRecorded, bool CurrentNameCommitted);

public sealed record MirrorPulseControlNamespaceMemberRetirementPreparation(Guid OperationId, Guid MemberId,
    string RelativePath, bool IsDirectory, DateTimeOffset PreparedAt, bool Recursive = false, int MemberCount = 1);

public sealed record MirrorPulseControlNamespaceMemberRetirementResult(Guid OperationId, Guid MemberId,
    string RelativePath, bool IsDirectory, string Phase, bool NativeDeleted, bool MembershipSettled,
    bool Recursive = false, int MemberCount = 1);

public sealed record MirrorPulseControlNamespaceBirthRecoveryResult(Guid RecoveryId, string Outcome,
    bool CallbackCompleted, bool Drained, bool NativeIdentityPrepared, bool DurableProjectionCommitted, bool VerificationRecorded);

/// <summary>Separates the durable product phase from a fresh public reconciliation receipt.</summary>
public sealed record MirrorPulseControlRootRecovery(Guid OperationId, string RootId, string Phase,
    bool? NativeMoveObserved, bool? DurableProjectionCommitted, bool? RequiresFullRescan,
    string? Outcome, string? Stage, int? NativeHResult);

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
