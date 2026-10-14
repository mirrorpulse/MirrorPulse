namespace MirrorPulse.Control.Contracts;

/// <summary>
/// Stable names and metadata for commands exposed by Host to all clients.
/// </summary>
public static class MirrorPulseControlCommands
{
    public const string HostStatus = "host.status";
    public const string HostStart = "host.start";
    public const string HostStop = "host.stop";
    public const string HostRestart = "host.restart";

    public const string AdapterList = "adapter.list";
    public const string AdapterInstall = "adapter.install";
    public const string AdapterRemove = "adapter.remove";

    public const string InstanceList = "instance.list";
    public const string InstanceCreate = "instance.create";
    public const string InstanceConfigure = "instance.configure";
    public const string InstanceEnable = "instance.enable";
    public const string InstanceSelectVersion = "instance.selectVersion";
    public const string RootList = "root.list";
    public const string RootRecover = "root.recover";
    public const string RootPrepareRename = "root.prepareRename";
    public const string NamespaceMemberMove = "namespace.member.move";
    public const string NamespaceMemberMoveStatus = "namespace.member.moveStatus";
    public const string NamespaceMemberRetirement = "namespace.member.retire";
    public const string NamespaceMemberRetirementStatus = "namespace.member.retirementStatus";
    public const string NamespaceMemberPrepareRetirement = "namespace.member.prepareRetire";
    public const string NamespaceMemberCreate = "namespace.member.create";
    public const string NamespaceMemberPrepareImport = "namespace.member.prepareImport";
    public const string NamespaceMemberImport = "namespace.member.import";
    public const string NamespaceMemberImportStatus = "namespace.member.importStatus";
    public const string NamespaceMemberPrepareReplace = "namespace.member.prepareReplace";
    public const string NamespaceMemberReplace = "namespace.member.replace";
    public const string NamespaceMemberReplacementStatus = "namespace.member.replacementStatus";
    public const string NamespaceProtectionCaptures = "namespace.protection.captures";
    public const string NamespaceProtectionRecoverTree = "namespace.protection.recoverTree";
    public const string NamespaceProtectionInitialize = "namespace.protection.initialize";
    public const string NamespaceProtectionRecoverCurrent = "namespace.protection.recoverCurrent";
    public const string NamespaceProtectionInitializationStatus = "namespace.protection.initializationStatus";
    public const string NamespaceProtectionInitializations = "namespace.protection.initializations";
    public const string NamespaceMemberInspect = "namespace.member.inspect";
    public const string NamespaceMemberTree = "namespace.member.tree";
    public const string NamespaceMemberPrepareMove = "namespace.member.prepareMove";
    public const string NamespaceBirthRecover = "namespace.birth.recover";

    public const string SyncStatus = "sync.status";
    public const string SyncRefresh = "sync.refresh";
    public const string ConflictList = "conflict.list";
    public const string ConflictSnooze = "conflict.snooze";
    public const string ConflictResolve = "conflict.resolve";

    public const string OperationGet = "operation.get";
    public const string OperationWatch = "operation.watch";
    public const string OperationCancel = "operation.cancel";

    public const string SettingsGet = "settings.get";
    public const string SettingsSet = "settings.set";
    public const string DiagnosticsCollect = "diagnostics.collect";

    public static IReadOnlyList<MirrorPulseControlCommandDescriptor> Catalog { get; } =
    [
        new(HostStatus, "host", false, false),
        new(HostStart, "host", true, false),
        new(HostStop, "host", true, false),
        new(HostRestart, "host", true, false),
        new(AdapterList, "adapter", false, false),
        new(AdapterInstall, "adapter", true, false),
        new(AdapterRemove, "adapter", true, false),
        new(InstanceList, "instance", false, false),
        new(InstanceCreate, "instance", true, true),
        new(InstanceConfigure, "instance", true, true),
        new(InstanceEnable, "instance", true, false),
        new(InstanceSelectVersion, "instance", true, false),
        new(RootList, "root", false, false),
        new(RootRecover, "root", true, false),
        new(RootPrepareRename, "root", true, false),
        new(NamespaceMemberMove, "namespace", true, false),
        new(NamespaceMemberMoveStatus, "namespace", false, false),
        new(NamespaceMemberRetirement, "namespace", true, false),
        new(NamespaceMemberRetirementStatus, "namespace", false, false),
        new(NamespaceMemberPrepareRetirement, "namespace", true, false),
        new(NamespaceMemberCreate, "namespace", true, false),
        new(NamespaceMemberPrepareImport, "namespace", true, false),
        new(NamespaceMemberImport, "namespace", true, false),
        new(NamespaceMemberImportStatus, "namespace", false, false),
        new(NamespaceMemberPrepareReplace, "namespace", true, false),
        new(NamespaceMemberReplace, "namespace", true, false),
        new(NamespaceMemberReplacementStatus, "namespace", false, false),
        new(NamespaceProtectionCaptures, "namespace", false, false),
        new(NamespaceProtectionRecoverTree, "namespace", true, false),
        new(NamespaceProtectionInitialize, "namespace", true, false),
        new(NamespaceProtectionRecoverCurrent, "namespace", true, false),
        new(NamespaceProtectionInitializationStatus, "namespace", false, false),
        new(NamespaceProtectionInitializations, "namespace", false, false),
        new(NamespaceMemberInspect, "namespace", false, false),
        new(NamespaceMemberTree, "namespace", false, false),
        new(NamespaceMemberPrepareMove, "namespace", true, false),
        new(NamespaceBirthRecover, "namespace", true, false),
        new(SyncStatus, "sync", false, false),
        new(SyncRefresh, "sync", true, false),
        new(ConflictList, "conflict", false, false),
        new(ConflictSnooze, "conflict", true, false),
        new(ConflictResolve, "conflict", true, false),
        new(OperationGet, "operation", false, false),
        new(OperationWatch, "operation", false, false),
        new(OperationCancel, "operation", true, false),
        new(SettingsGet, "settings", false, false),
        new(SettingsSet, "settings", true, true),
        new(DiagnosticsCollect, "diagnostics", false, false)
    ];
}

/// <summary>
/// Describes a command without coupling clients to its implementation.
/// </summary>
public sealed record MirrorPulseControlCommandDescriptor(
    string Name,
    string Category,
    bool Mutating,
    bool AcceptsSensitiveInput);
