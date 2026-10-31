using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    // Reuse only successful facts within an entirely read-only transaction or an
    // explicitly scoped validation phase. A writer must dispose its validation
    // phase before the first mutation; later API/native boundaries and misses
    // never receive these facts. Each kind retains at most sixteen entries.
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

    private NamespaceRecoveryValidationReadPhase BeginNamespaceRecoveryValidationReadPhase(SqliteTransaction transaction)
    {
        _namespaceRecoveryReadSnapshots.Add(transaction, new());
        return new(this, transaction);
    }

    private sealed class NamespaceRecoveryValidationReadPhase(MirrorPulseProductCatalog catalog, SqliteTransaction transaction) : IDisposable
    {
        private MirrorPulseProductCatalog? _catalog = catalog;
        public void Dispose() => Interlocked.Exchange(ref _catalog, null)?._namespaceRecoveryReadSnapshots.Remove(transaction);
    }
}
