using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    // A transaction sees one SQLite snapshot. Only successfully validated immutable facts
    // may be reused within it: never current names, protection heads, absence or native state.
    // A new transaction (including one after corruption or role rotation) starts empty.
    private readonly ConditionalWeakTable<SqliteTransaction, NamespaceHistoryReads> _namespaceHistoryReads = new();

    private sealed class NamespaceHistoryReads
    {
        internal Dictionary<Guid, MirrorPulseNamespaceMemberMoveIntent> Moves { get; } = new();
        internal HashSet<Guid> MoveChains { get; } = new();
        internal Dictionary<Guid, MirrorPulseNamespaceMemberMoveCompletion> Completions { get; } = new();
        internal Dictionary<Guid, (MirrorPulseNamespaceDirectoryMoveCompletion Completion,
            MirrorPulseNamespaceDirectoryMoveSnapshot Snapshot,
            IReadOnlyDictionary<Guid, int> MemberIndexes)> DirectoryCompletions
        { get; } = new();
        internal HashSet<Guid> DirectoryPositions { get; } = new();
    }
}
