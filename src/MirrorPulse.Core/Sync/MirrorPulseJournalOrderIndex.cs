using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Sync;

/// <summary>Indexes held journal paths without scanning all earlier commands for each candidate.</summary>
/// <remarks>Contains only ephemeral dependency summaries. Original commands, IDs and dispatch
/// ownership remain with their caller. Instance and root comparisons follow the journal policy;
/// path components use its case-insensitive Windows name comparisons.</remarks>
public sealed class MirrorPulseJournalOrderIndex
{
    private readonly Dictionary<(InstanceId Instance, string Root), Node> _roots = [];

    public void Add(MirrorPulseWorkerChangeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        foreach (var path in MirrorPulseJournalOrderPolicy.Paths(command))
        {
            var key = (path.Instance, path.Root);
            if (!_roots.TryGetValue(key, out var node)) _roots.Add(key, node = new());
            node.SubtreeSequence = Math.Min(node.SubtreeSequence, command.Sequence);
            foreach (string component in Components(path.Path))
            {
                if (!node.Children.TryGetValue(component, out var child)) node.Children.Add(component, child = new());
                node = child;
                node.SubtreeSequence = Math.Min(node.SubtreeSequence, command.Sequence);
            }
            node.ExactSequence = Math.Min(node.ExactSequence, command.Sequence);
            if (command.IsDirectory) node.DirectorySequence = Math.Min(node.DirectorySequence, command.Sequence);
        }
    }

    public bool HasEarlierDependency(MirrorPulseWorkerChangeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        foreach (var path in MirrorPulseJournalOrderPolicy.Paths(command))
        {
            if (!_roots.TryGetValue((path.Instance, path.Root), out var node)) continue;
            bool missing = false;
            foreach (string component in Components(path.Path))
            {
                // A held directory fences every descendant, including the root's children.
                if (node.DirectorySequence < command.Sequence) return true;
                if (!node.Children.TryGetValue(component, out var child)) { missing = true; break; }
                node = child;
            }
            if (!missing && (node.ExactSequence < command.Sequence ||
                command.IsDirectory && node.SubtreeSequence < command.Sequence)) return true;
        }
        return false;
    }

    private static string[] Components(string path) => path.Length == 0 ? [] : path.Split('/');

    private sealed class Node
    {
        internal Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal long ExactSequence { get; set; } = long.MaxValue;
        internal long DirectorySequence { get; set; } = long.MaxValue;
        internal long SubtreeSequence { get; set; } = long.MaxValue;
    }
}
