namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static void ValidateReplacementCopyIdentity(int version, Guid operationId, Guid actionId, Guid? authorityId)
    {
        ValidateReplacementFactIdentity(1, operationId, actionId);
        if (version is not (1 or 2) || (version == 1 ? authorityId is not null : authorityId is null ||
            authorityId == Guid.Empty || authorityId == operationId || authorityId == actionId))
            throw new ArgumentException("A versioned copy fact must explicitly retain its separate role recovery authority.");
    }

    private static MirrorPulseNamespaceReplacementAction RequireReplacementCopyAuthority(
        MirrorPulseNamespaceReplacementExecution execution, Guid? authorityId, DateTimeOffset observedAt, bool requireCurrent)
    {
        var first = execution.Action ?? throw new InvalidOperationException("The copy lost its immutable first replacement action.");
        var actions = execution.RoleActions;
        int authorityIndex = -1;
        var authority = first;
        if (authorityId is { } id)
        {
            for (int index = 0; index < actions.Count; index++)
            {
                if (actions[index].RecoveryId != id) continue;
                authorityIndex = index; authority = actions[index].Action; break;
            }
            var handoff = execution.RoleHandoffHistory.SingleOrDefault(value => value.RecoveryId == id);
            if (authorityIndex < 0 || handoff is null || observedAt < handoff.Verification.ObservedAt)
                throw new InvalidOperationException("The copy requires its complete owned role action and independently recorded metadata handoff.");
        }
        if (observedAt < authority.PreparedAt || requireCurrent && authorityIndex != actions.Count - 1 ||
            authorityIndex + 1 < actions.Count && observedAt > actions[authorityIndex + 1].Action.PreparedAt)
            throw new InvalidOperationException("The copy authority must precede its observation and cannot borrow a superseded role.");
        return authority;
    }
}
