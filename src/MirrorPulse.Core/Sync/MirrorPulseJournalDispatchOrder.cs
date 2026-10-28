using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Sync;

/// <summary>Tracks unaccepted original prefixes during one bounded, round-robin dispatch cycle.</summary>
/// <remarks>Positions preserve the caller's stable order, including equal sequences and repeated
/// commands. Accepting an operation removes only its dependency entries; it does not acknowledge
/// a journal or change the original commands. The entire index expires with its dispatch cycle.</remarks>
public sealed class MirrorPulseJournalDispatchOrder
{
    private readonly Dictionary<(InstanceId Instance, string Root), Node> _roots = [];
    private readonly Dictionary<MirrorPulseWorkerChangeCommand, int> _firstPositions = [];
    private readonly Dictionary<Guid, List<(Node Node, int Position, bool Exact, bool Directory)>> _operations = [];

    public MirrorPulseJournalDispatchOrder(IReadOnlyList<MirrorPulseWorkerChangeCommand> ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        int position = 0;
        foreach (var command in ordered)
        {
            _firstPositions.TryAdd(command, position);
            if (!_operations.TryGetValue(command.OperationId, out var entries)) _operations.Add(command.OperationId, entries = []);
            foreach (var path in MirrorPulseJournalOrderPolicy.Paths(command))
            {
                if (!_roots.TryGetValue((path.Instance, path.Root), out var node)) _roots.Add((path.Instance, path.Root), node = new());
                Retain(node);
                foreach (string component in Components(path.Path))
                {
                    if (!node.Children.TryGetValue(component, out var child)) node.Children.Add(component, child = new());
                    node = child;
                    Retain(node);
                }
                node.Exact.Add(position);
                if (command.IsDirectory) node.Directories.Add(position);
                entries.Add((node, position, true, command.IsDirectory));

                void Retain(Node retained)
                {
                    retained.Subtree.Add(position);
                    entries.Add((retained, position, false, false));
                }
            }
            position++;
        }
    }

    public bool HasEarlierDependency(MirrorPulseWorkerChangeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        int position = _firstPositions[command];
        foreach (var path in MirrorPulseJournalOrderPolicy.Paths(command))
        {
            if (!_roots.TryGetValue((path.Instance, path.Root), out var node)) continue;
            bool missing = false;
            foreach (string component in Components(path.Path))
            {
                if (Earlier(node.Directories, position)) return true;
                if (!node.Children.TryGetValue(component, out var child)) { missing = true; break; }
                node = child;
            }
            if (!missing && (Earlier(node.Exact, position) || command.IsDirectory && Earlier(node.Subtree, position))) return true;
        }
        return false;
    }

    public void Accept(Guid operationId)
    {
        if (!_operations.Remove(operationId, out var entries)) return;
        foreach (var entry in entries)
        {
            if (!entry.Exact) entry.Node.Subtree.Remove(entry.Position);
            else
            {
                entry.Node.Exact.Remove(entry.Position);
                if (entry.Directory) entry.Node.Directories.Remove(entry.Position);
            }
        }
    }

    private static bool Earlier(SortedSet<int> positions, int candidate) => positions.Count > 0 && positions.Min < candidate;
    private static string[] Components(string path) => path.Length == 0 ? [] : path.Split('/');

    private sealed class Node
    {
        internal Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal SortedSet<int> Exact { get; } = [];
        internal SortedSet<int> Directories { get; } = [];
        internal SortedSet<int> Subtree { get; } = [];
    }
}
