using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    // A transaction sees one SQLite snapshot. Reuse only validated immutable facts,
    // never current names, protection heads, absence or native state. Full caches
    // replace their least recently read immutable fact, retaining at most sixteen.
    // A later miss still reads and validates its original before adding it again.
    private readonly ConditionalWeakTable<SqliteTransaction, NamespaceHistoryReads> _namespaceHistoryReads = new();

    private sealed class NamespaceHistoryReads
    {
        internal NamespaceHistoryCache<(long Sequence, MirrorPulseNamespaceBirthProtection Protection)> BirthProtectionRows { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceBirthIntent> BirthIntentRows { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceBirthPlan> BirthPlanRows { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceBirthStart> BirthStartRows { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceBirthConversionPreparation> BirthConversionRows { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceBirthObservation> BirthObservationRows { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespacePermissionChange> VerifiedPermissionRows { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespacePermissionBaseline> PermissionBaselines { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceMemberRegistration> MemberRegistrations { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceBirthProtection> UnlocatedBirthProtections { get; } = new();
        internal NamespaceHistoryCache<(Guid MemberId, Guid OperationId), MirrorPulseNamespaceMemberPosition> PositionRows { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceBirthProtection> LocatedBirthGraphs { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceMemberMoveIntent> Moves { get; } = new();
        internal NamespaceHistorySet MoveChains { get; } = new();
        internal NamespaceHistoryCache<MirrorPulseNamespaceMemberMoveCompletion> Completions { get; } = new();
        internal NamespaceHistoryCache<NamespaceDirectoryHistoryGraph> DirectoryCompletions { get; } = new();
        internal NamespaceHistorySet DirectoryPositions { get; } = new();
    }

    private sealed record NamespaceDirectoryHistoryGraph(MirrorPulseNamespaceDirectoryMoveCompletion Completion,
        MirrorPulseNamespaceDirectoryMoveSnapshot Snapshot, IReadOnlyDictionary<Guid, int> MemberIndexes);

    private sealed class NamespaceHistoryCache<T> : NamespaceHistoryCache<Guid, T> { }

    private class NamespaceHistoryCache<TKey, T> where TKey : notnull
    {
        private const int Capacity = 16;
        private readonly Dictionary<TKey, T> _entries = new();
        private readonly List<TKey> _recent = new(Capacity);
        internal int Count => _entries.Count;

        internal bool TryGetValue(TKey id, [MaybeNullWhen(false)] out T value)
        {
            if (!_entries.TryGetValue(id, out value)) return false;
            _recent.Remove(id);
            _recent.Add(id);
            return true;
        }

        internal void Add(TKey id, T value)
        {
            if (_entries.ContainsKey(id)) throw new ArgumentException("The immutable history key is already retained.", nameof(id));
            MakeRoom();
            _entries.Add(id, value);
            _recent.Add(id);
        }

        internal T this[TKey id]
        {
            set
            {
                if (_entries.ContainsKey(id)) _recent.Remove(id);
                else MakeRoom();
                _entries[id] = value;
                _recent.Add(id);
            }
        }

        internal bool Remove(TKey id)
        {
            if (!_entries.Remove(id)) return false;
            _recent.Remove(id);
            return true;
        }

        private void MakeRoom()
        {
            if (_entries.Count < Capacity) return;
            _entries.Remove(_recent[0]);
            _recent.RemoveAt(0);
        }
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
