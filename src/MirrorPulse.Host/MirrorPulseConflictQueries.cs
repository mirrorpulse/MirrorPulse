using MirrorPulse.Control.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Host;

/// <summary>Maps Host-owned pending conflict pages into the existing Control read contract.</summary>
internal static class MirrorPulseConflictQueries
{
    internal static async Task<MirrorPulseControlConflictList> ReadAsync(MirrorPulseProductCatalog catalog,
        ConflictListArguments arguments, CancellationToken token)
    {
        var page = await catalog.ReadPendingConflictPageAsync(arguments.Limit ?? 100, arguments.Cursor, token).ConfigureAwait(false);
        return new(page.Items.Select(conflict => new MirrorPulseControlConflict(conflict.ConflictId.ToString("D"), conflict.InstanceId.ToString(),
            conflict.RelativePath, conflict.Reason.ToString(), conflict.Status.ToString(), conflict.Source.ToString(),
            conflict.LocalRevision, conflict.RemoteRevision, conflict.DetectedAt)).ToArray(), page.NextCursor);
    }
}
