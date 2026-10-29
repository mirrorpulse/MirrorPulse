using System.Text.Json;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CfSharp.CrashProbe;

public sealed record CliTransferEffectFailure(Guid OperationId, string Kind, string? State,
    string? ExecutionEvidence, bool? ExpectedRevisionPresent, bool? AcceptedRevisionPresent);
public sealed record CliTransferPlanFailure(Guid PlanId, Guid ItemId, string Effect, int OriginalJournalCount,
    bool ExecutionPresent, bool? ExpectedRevisionPresent, IReadOnlyList<CliTransferEffectFailure> Effects);
public sealed record CliTransferConflictFailure(Guid ConflictId, Guid OperationId, string Reason,
    bool ExpectedRevisionPresent, bool ActualRevisionPresent, Guid? PlanId);
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
                conflicts.Add(new(conflict.ConflictId, operationId, conflict.Reason.ToString(),
                    !string.IsNullOrEmpty(conflict.LocalRevision), !string.IsNullOrEmpty(conflict.RemoteRevision), plan?.PlanId));
                if (plan is null || plans.ContainsKey(plan.PlanId)) continue;
                string path = route.DirectoryName + "/" + plan.FinalPath.Replace('\\', '/');
                if (plan.InstanceId != route.InstanceId || plan.RootKey != route.UniquenessKey ||
                    !fixture.Inventory.AllowedEntries.TryGetValue(path, out bool isDirectory) || isDirectory)
                    throw new InvalidDataException("The retained plan escaped its exact authenticated fixture route.");
                var execution = await fixture.Catalog.ReadJournalCoalescingExecutionAsync(plan.PlanId);
                var effects = new List<CliTransferEffectFailure>();
                if (execution is not null)
                {
                    var dispatch = await fixture.Catalog.ReadJournalCoalescingDispatchAsync(plan.PlanId);
                    foreach (var step in dispatch.Steps)
                    {
                        var mutation = await fixture.Catalog.ReadMutationAsync(step.OperationId);
                        effects.Add(new(step.OperationId, step.Kind.ToString(), mutation?.State.ToString(),
                            mutation?.ExecutionEvidence.ToString(), mutation is null ? null : !string.IsNullOrEmpty(mutation.Intent.ExpectedRevision),
                            mutation is null ? null : !string.IsNullOrEmpty(mutation.AcceptedRevision)));
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
