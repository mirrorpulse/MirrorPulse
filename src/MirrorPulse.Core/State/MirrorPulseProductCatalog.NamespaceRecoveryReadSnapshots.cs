using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    // Recovery and current-inventory reads never write within their transaction. Reuse only
    // successfully validated results from that exact SQLite snapshot. Write paths
    // and later API/native boundaries never receive this cache, including misses.
    private readonly ConditionalWeakTable<SqliteTransaction, NamespaceRecoveryReadSnapshot> _namespaceRecoveryReadSnapshots = new();

    private sealed class NamespaceRecoveryReadSnapshot
    {
        internal NamespaceHistoryCache<MirrorPulseNamespaceMemberRegistration> Members { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceMemberRegistration, MirrorPulseNamespaceMemberLocation> Locations { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceBirthIntent, bool> BirthReferenceIndexes { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceInitializationStatus> Initializations { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceBirthIntent> ValidatedBirths { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceBirthObservation> ValidatedBirthObservations { get; } = new();
        internal NamespaceHistoryCache<(Guid? SelectedActionId, BornMemberRecoveryHistory Summary)> BornRecoverySummaries { get; } = new();
    }

    private SqliteTransaction BeginNamespaceRecoveryReadSnapshot()
    {
        var transaction = _connection.BeginTransaction(deferred: true);
        _namespaceRecoveryReadSnapshots.Add(transaction, new());
        return transaction;
    }
}
