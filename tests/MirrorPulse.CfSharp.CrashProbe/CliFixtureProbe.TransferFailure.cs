using System.Text.Json;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CfSharp.CrashProbe;

public sealed record CliTransferEffectFailure(Guid OperationId, string Kind, string? State,
    string? ExecutionEvidence, bool? ExpectedRevisionPresent, bool? AcceptedRevisionPresent)
{
    public bool? ExpectedMatchesOriginalExecution { get; init; }
}
public sealed record CliTransferPlanFailure(Guid PlanId, Guid ItemId, string Effect, int OriginalJournalCount,
    bool ExecutionPresent, bool? ExpectedRevisionPresent, IReadOnlyList<CliTransferEffectFailure> Effects);
public sealed record CliTransferConflictFailure(Guid ConflictId, Guid OperationId, string Reason,
    bool ExpectedRevisionPresent, bool ActualRevisionPresent, Guid? PlanId)
{
    public bool SeparateRemoteObservationPresent { get; init; }
    public bool? SelectedExpectedMatchesOriginalExecution { get; init; }
    public bool? SelectedActualMatchesOriginalExecution { get; init; }
    public bool? ErrorExpectedMatchesOriginalExecution { get; init; }
    public bool? ErrorActualMatchesOriginalExecution { get; init; }
}
public sealed record CliTransferFailureObservation(int SchemaVersion, bool ExclusiveCatalogOwner, bool MoreConflictsAvailable,
    IReadOnlyList<CliTransferConflictFailure> Conflicts, IReadOnlyList<CliTransferPlanFailure> Plans);

public static partial class CliFixtureProbe
{
    /// <summary>Observes bounded retained upload intent only under the authenticated stopped fixture's catalog lease.</summary>
    /// <remarks>No SDK store, native registration, source operation, conflict resolution or acknowledgement is opened.</remarks>
    public static async Task<int> RunTransferFailureObservationAsync(string directory)
    {
        string stage = "FixtureAuthentication";
        try
        {
            await using var fixture = await OpenTransferFixtureAsync(directory, value => stage = value);
            stage = "TransferFailureObservation";
            var page = await fixture.Catalog.ReadPendingConflictPageAsync(64);
            var conflicts = new List<CliTransferConflictFailure>();
            var plans = new Dictionary<Guid, CliTransferPlanFailure>();
            foreach (var conflict in page.Items.Where(item => item.Source == MirrorPulseConflictSource.Upload))
            {
                if (!Guid.TryParseExact(conflict.ChangeId, "D", out Guid operationId) || operationId == Guid.Empty)
                    throw new InvalidDataException("A synthetic upload conflict lost its original operation.");
                var route = fixture.Inventory.Roots.SingleOrDefault(root => root.InstanceId == conflict.InstanceId &&
                    conflict.RelativePath.StartsWith(root.DirectoryName + "/", StringComparison.Ordinal));
                if (route is null || !fixture.Inventory.AllowedEntries.TryGetValue(conflict.RelativePath, out bool directoryEntry) || directoryEntry)
                    throw new InvalidDataException("Failure observation cannot disclose a foreign conflict or path.");
                var plan = await fixture.Catalog.ReadJournalCoalescingOwnerAsync(operationId);
                var execution = plan is null ? null : await fixture.Catalog.ReadJournalCoalescingExecutionAsync(plan.PlanId);
                MirrorPulseCoalescedConflictRemoteObservation? observed = null;
                if (execution is not null)
                {
                    var current = await fixture.Catalog.ReadJournalCoalescingDispatchAsync(plan!.PlanId);
                    foreach (var step in current.Steps)
                    {
                        var rejected = await fixture.Catalog.ReadMutationAsync(step.OperationId);
                        if (rejected?.State != MirrorPulseMutationState.Conflict) continue;
                        observed = await fixture.Catalog.ReadCoalescedConflictRemoteObservationAsync(step.OperationId);
                        if (observed is not null && (observed.PlanId != plan.PlanId || !observed.SelectedConflictIds.Contains(conflict.ConflictId)))
                            throw new InvalidDataException("The separate remote observation lost its exact selected conflict and original plan.");
                        break;
                    }
                }
                string? originalExpected = observed is null ? conflict.LocalRevision : observed.OriginalErrorExpectedRevision;
                string? originalActual = observed is null ? conflict.RemoteRevision : observed.OriginalErrorActualRevision;
                conflicts.Add(new(conflict.ConflictId, operationId, conflict.Reason.ToString(),
                    !string.IsNullOrEmpty(conflict.LocalRevision), !string.IsNullOrEmpty(conflict.RemoteRevision), plan?.PlanId)
                {
                    SeparateRemoteObservationPresent = observed is not null,
                    SelectedExpectedMatchesOriginalExecution = execution is null || string.IsNullOrEmpty(conflict.LocalRevision) ? null
                        : string.Equals(conflict.LocalRevision, execution.ExpectedRevision, StringComparison.Ordinal),
                    SelectedActualMatchesOriginalExecution = execution is null || string.IsNullOrEmpty(conflict.RemoteRevision) ? null
                        : string.Equals(conflict.RemoteRevision, execution.ExpectedRevision, StringComparison.Ordinal),
                    ErrorExpectedMatchesOriginalExecution = execution is null || string.IsNullOrEmpty(originalExpected) ? null
                        : string.Equals(originalExpected, execution.ExpectedRevision, StringComparison.Ordinal),
                    ErrorActualMatchesOriginalExecution = execution is null || string.IsNullOrEmpty(originalActual) ? null
                        : string.Equals(originalActual, execution.ExpectedRevision, StringComparison.Ordinal)
                });
                if (plan is null || plans.ContainsKey(plan.PlanId)) continue;
                string path = route.DirectoryName + "/" + plan.FinalPath.Replace('\\', '/');
                if (plan.InstanceId != route.InstanceId || plan.RootKey != route.UniquenessKey ||
                    !fixture.Inventory.AllowedEntries.TryGetValue(path, out bool isDirectory) || isDirectory)
                    throw new InvalidDataException("The retained plan escaped its exact authenticated fixture route.");
                var effects = new List<CliTransferEffectFailure>();
                if (execution is not null)
                {
                    var dispatch = await fixture.Catalog.ReadJournalCoalescingDispatchAsync(plan.PlanId);
                    foreach (var step in dispatch.Steps)
                    {
                        var mutation = await fixture.Catalog.ReadMutationAsync(step.OperationId);
                        effects.Add(new(step.OperationId, step.Kind.ToString(), mutation?.State.ToString(),
                            mutation?.ExecutionEvidence.ToString(), mutation is null ? null : !string.IsNullOrEmpty(mutation.Intent.ExpectedRevision),
                            mutation is null ? null : !string.IsNullOrEmpty(mutation.AcceptedRevision))
                        {
                            ExpectedMatchesOriginalExecution = mutation is null ? null
                                : string.Equals(mutation.Intent.ExpectedRevision, execution.ExpectedRevision, StringComparison.Ordinal)
                        });
                    }
                }
                plans.Add(plan.PlanId, new(plan.PlanId, plan.ItemId, plan.Effect.ToString(), plan.Members.Count,
                    execution is not null, execution is null ? null : !string.IsNullOrEmpty(execution.ExpectedRevision), effects.AsReadOnly()));
            }
            Console.WriteLine(JsonSerializer.Serialize(new CliTransferFailureObservation(1, true, page.NextCursor is not null,
                conflicts.AsReadOnly(), plans.Values.ToArray()), JsonOptions));
            return 0;
        }
        catch (Exception failure) { failure.Data["CliFixtureCleanupStage"] = stage; throw; }
    }
}
