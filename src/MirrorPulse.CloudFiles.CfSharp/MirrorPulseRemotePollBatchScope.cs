using CfSharp;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>The Host-owned polling namespace selects an independent cursor, never dispatch permission.</summary>
internal static class MirrorPulseRemotePollBatchScope
{
    internal const string ReservedPrefix = "poll-root/";
    internal static string Prefix(RootRegistration root) => $"{root.InstanceId}/{ReservedPrefix}{root.RootId}/";

    internal static RootId? Read(InstanceId instance, CloudRemoteChangeBatch batch)
    {
        string prefix = instance + "/" + ReservedPrefix;
        if (!batch.BatchId.StartsWith(prefix, StringComparison.Ordinal)) return null;
        string[] parts = batch.BatchId[prefix.Length..].Split('/');
        if (parts.Length != 3 || !RootId.TryParse(parts[0], out var root) || parts[0] != root.ToString() ||
            parts[1] != Convert.ToHexString(batch.InitialCursor.Span) || parts[2] != Convert.ToHexString(batch.FinalCursor.Span) ||
            batch.InitialCursor.Length != 32 || batch.FinalCursor.Length != 32)
            throw new InvalidDataException("The root polling batch scope or immutable cursor identity is invalid.");
        return root;
    }

    internal static RootId? RequireCurrentMapping(InstanceId instance, CloudRemoteChangeBatch batch, MirrorPulseRootRouter? router)
    {
        var scope = Read(instance, batch);
        if (scope is null) return null;
        var root = router?.Registrations.SingleOrDefault(value => value.InstanceId == instance && value.RootId == scope)
            ?? throw new InvalidDataException("The root polling batch has no current stable root mapping.");
        if (root.State != RootRegistrationState.Active) throw new InvalidOperationException("A disabled root cannot apply a polling batch.");
        foreach (var change in batch.Changes)
            foreach (var path in change.PreviousRelativePath is { } previous ? new[] { change.RelativePath, previous } : [change.RelativePath])
            {
                var route = router!.ResolveCurrentPath(path);
                if (route.InstanceId != instance || router.GetRegistration(route.InstanceId, route.RootKey).RootId != scope)
                    throw new InvalidDataException("The polling entry escapes its retained root scope.");
            }
        return scope;
    }
}
