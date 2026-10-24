using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    // A transaction sees one SQLite snapshot. Reuse only validated immutable facts,
    // never current names, protection heads, absence or native state. Full caches
    // decline new entries; the caller still reads and validates their originals.
    private readonly ConditionalWeakTable<SqliteTransaction, NamespaceHistoryReads> _namespaceHistoryReads = new();

    private sealed class NamespaceHistoryReads
    {
        internal NamespaceHistoryCache<(long Sequence, MirrorPulseNamespaceBirthProtection Protection)> BirthProtectionRows { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceBirthIntent> BirthIntentRows { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceBirthObservation> BirthObservationRows { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespacePermissionChange> VerifiedPermissionRows { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespacePermissionBaseline> PermissionBaselines { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceMemberRegistration> MemberRegistrations { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceBirthProtection> UnlocatedBirthProtections { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceBirthProtection> LocatedBirthGraphs { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceMemberMoveIntent> Moves { get; } = new();
        internal NamespaceHistorySet MoveChains { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceMemberMoveCompletion> Completions { get; } = new();
        internal NamespaceHistoryCache<NamespaceDirectoryHistoryGraph> DirectoryCompletions { get; } = new();
        internal NamespaceHistorySet DirectoryPositions { get; } = new();
    }

    private sealed record NamespaceDirectoryHistoryGraph(MirrorPulseNamespaceDirectoryMoveCompletion Completion,
        MirrorPulseNamespaceDirectoryMoveSnapshot Snapshot, IReadOnlyDictionary<Guid, int> MemberIndexes);

    private sealed class NamespaceHistoryCache<T>
    {
        private const int Capacity = 16;
        private readonly Dictionary<Guid, T> _entries = new();
        internal int Count => _entries.Count;

        internal bool TryGetValue(Guid id, [MaybeNullWhen(false)] out T value) => _entries.TryGetValue(id, out value);

        internal void Add(Guid id, T value)
        {
            if (_entries.Count < Capacity || _entries.ContainsKey(id)) _entries.Add(id, value);
        }

        internal T this[Guid id]
        {
            set
            {
                if (_entries.Count < Capacity || _entries.ContainsKey(id)) _entries[id] = value;
            }
        }

        internal bool Remove(Guid id) => _entries.Remove(id);
    }

    private sealed class NamespaceHistorySet
    {
        private readonly NamespaceHistoryCache<bool> _entries = new();
        internal int Count => _entries.Count;
        internal bool Contains(Guid id) => _entries.TryGetValue(id, out _);
        internal void Add(Guid id)
        {
            if (!Contains(id)) _entries.Add(id, true);
        }
    }
}
