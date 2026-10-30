using System.Globalization;
using MirrorPulse.Control.Contracts;

namespace MirrorPulse.Cli;

public sealed partial class MirrorPulseCliHostOperations
{
    private static ConflictRefreshVersionArguments ParseConflictRefreshArguments(IReadOnlyList<string> arguments)
    {
        Guid? conflict = null, operation = null;
        if (arguments.Count != 4) throw new ArgumentException("refresh-version requires --conflict-id and --operation-id exactly once.");
        for (int index = 0; index < arguments.Count; index += 2)
        {
            if (!Guid.TryParse(arguments[index + 1], out Guid value) || value == Guid.Empty)
                throw new ArgumentException("The conflict and operation IDs must be nonempty GUIDs.");
            if (arguments[index] == "--conflict-id" && conflict is null) conflict = value;
            else if (arguments[index] == "--operation-id" && operation is null) operation = value;
            else throw new ArgumentException("refresh-version accepts only one --conflict-id and one --operation-id.");
        }
        return new(operation ?? throw new ArgumentException("An explicit operation ID is required."),
            conflict ?? throw new ArgumentException("A selected conflict ID is required."));
    }

    private static void ValidateConflictRefreshOutcome(ConflictRefreshVersionArguments request, MirrorPulseControlConflictVersionRefresh result)
    {
        if (result.Version != 1 || result.CommandId != request.CommandId || result.ConflictId != request.ConflictId ||
            result.State is not ("pending" or "resolved") || (result.State == "resolved") != (result.Observation is not null))
            throw new InvalidDataException("The Host returned an inconsistent conflict version outcome.");
        if (result.Observation is { } observed && (observed.Version is not (1 or 2) || observed.ObservationId == Guid.Empty ||
            observed.RejectedEffectId == Guid.Empty || string.IsNullOrWhiteSpace(observed.SelectedRemoteRevision) ||
            observed.StartedAt <= DateTimeOffset.UnixEpoch || observed.ObservedAt < observed.StartedAt ||
            observed.SelectedConflictIds is not { Count: >= 2 and <= 64 } || observed.SelectedConflictIds.Any(id => id == Guid.Empty || id == request.ConflictId) ||
            observed.SelectedConflictIds.Distinct().Count() != observed.SelectedConflictIds.Count ||
            observed.Version == 1 && observed.PreviousObservationId is not null ||
            observed.Version == 2 && (observed.PreviousObservationId is null || observed.PreviousObservationId == Guid.Empty || observed.PreviousObservationId == observed.ObservationId)))
            throw new InvalidDataException("The Host returned an invalid separate version observation.");
    }

    private static ConflictListArguments ParseConflictListArguments(IReadOnlyList<string> arguments)
    {
        int? limit = null; string? cursor = null;
        for (int index = 0; index < arguments.Count; index++)
        {
            string option = arguments[index]; int equals = option.IndexOf('=');
            string name = equals < 0 ? option : option[..equals];
            if (!name.Equals("--limit", StringComparison.OrdinalIgnoreCase) && !name.Equals("--cursor", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("conflict list accepts only --limit and --cursor.");
            string value = equals >= 0 ? option[(equals + 1)..] : ++index < arguments.Count ? arguments[index] : string.Empty;
            if (name.Equals("--limit", StringComparison.OrdinalIgnoreCase))
            {
                if (limit is not null || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed is < 1 or > 1000)
                    throw new ArgumentException("--limit must occur once and contain an integer from 1 to 1000.");
                limit = parsed;
            }
            else
            {
                if (cursor is not null || string.IsNullOrWhiteSpace(value) || value.Length > 96)
                    throw new ArgumentException("--cursor requires one bounded opaque cursor returned by the Host.");
                cursor = value;
            }
        }
        return new(limit, cursor);
    }

    private async Task<MirrorPulseControlConflict> FindConflictAsync(string conflictId, CancellationToken token)
    {
        if (!Guid.TryParse(conflictId, out Guid id) || id == Guid.Empty)
            throw new ArgumentException("The conflict ID is invalid.", nameof(conflictId));
        var cursors = new HashSet<string>(StringComparer.Ordinal); string? cursor = null;
        do
        {
            token.ThrowIfCancellationRequested();
            var page = await _client.GetConflictsAsync(cursor: cursor, cancellationToken: token).ConfigureAwait(false);
            var selected = page.Items.SingleOrDefault(item => item.ConflictId.Equals(id.ToString("D"), StringComparison.OrdinalIgnoreCase));
            if (selected is not null) return selected;
            cursor = page.NextCursor;
            if (cursor is not null && (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 96 || !cursors.Add(cursor)))
                throw new InvalidDataException("The Host returned an invalid or repeated conflict cursor.");
        } while (cursor is not null);
        throw new FileNotFoundException("The conflict was not found.");
    }
}
