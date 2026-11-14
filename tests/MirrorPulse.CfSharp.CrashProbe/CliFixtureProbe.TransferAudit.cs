using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CfSharp.CrashProbe;

public sealed record CliTransferFixtureReceipt(Guid OperationId, string Scope, Guid SourceRootId, Guid TargetRootId,
    Guid SourceMemberId, Guid TargetMemberId, Guid SourceItemId, Guid TargetItemId, long Length, string Sha256,
    int OriginalJournalCount, string AcknowledgementKind, DateTimeOffset AcknowledgedAt,
    long SourceNameEpoch, bool PrivateBytesVerified)
{
    public string? Direction { get; init; }
}
public sealed record CliDirectoryTransferFixtureReceipt(Guid OperationId, Guid SourceRootId, Guid TargetRootId,
    Guid SourceMemberId, Guid TargetMemberId, int OriginalMemberCount, Guid SourceDeleteReceiptId, int OriginalJournalCount,
    DateTimeOffset AcknowledgedAt, long Length, string Sha256);
public sealed record CliTransferFixtureAudit(int SchemaVersion, bool ExclusiveCatalogOwner, IReadOnlyList<CliTransferFixtureReceipt> Transfers)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CliDirectoryTransferFixtureReceipt? DirectoryTransfer { get; init; }
}

public static partial class CliFixtureProbe
{
    /// <summary>Reads retained transfer receipts and private bytes only after taking the stopped Host's actual lease.</summary>
    public static async Task<int> RunTransferAuditAsync(string directory)
    {
        string stage = "FixtureAuthentication";
        try
        {
            await using var fixture = await OpenTransferFixtureAsync(directory, value => stage = value);
            stage = "TransferReceipts";
            var operations = fixture.Marker.TransferOperationIds;
            if (operations is not { Count: 2 } || operations.Any(id => id == Guid.Empty) || operations.Distinct().Count() != 2)
                throw new InvalidDataException("The stopped transfer oracle requires two distinct original command IDs.");
            if ((await fixture.Catalog.ReadNamespaceDispatchFencedRootsAsync()).Count != 0)
                throw new InvalidDataException("An unfinished namespace owner cannot be reported as completed transfers.");
            var receipts = new List<CliTransferFixtureReceipt>();
            bool singleRoot = fixture.Marker.SchemaVersion is 2 or 3;
            foreach (Guid operation in operations)
            {
                var copy = await fixture.Catalog.ReadNamespaceTransferAsync(operation)
                    ?? throw new InvalidDataException("The original transfer command lost its retained plan.");
                var retirement = await fixture.Catalog.ReadNamespaceRetirementAsync(copy.Plan.DeleteOperationId);
                var plan = copy.Plan;
                var source = fixture.Inventory.Roots.SingleOrDefault(root => root.RootId == plan.SourceRoute.RootId);
                var target = fixture.Inventory.Roots.SingleOrDefault(root => root.RootId == plan.TargetRoute.RootId);
                if (plan.Version != 2 || plan.CommandOrigin?.CommandId != operation || source is null || target is null ||
                    !MatchesOriginalTransferRoute(fixture.Marker, receipts.Count, source, target) ||
                    plan.SourceRoute.ProtocolVersion != 2 || plan.TargetRoute.ProtocolVersion != 2 || plan.SourceRemoteRevision is null ||
                    copy.CopyAcceptedAt is null || copy.TargetVerification is null || copy.TargetPublication is null ||
                    copy.SourceDeletion?.AcceptedAt is null || copy.Acknowledgement?.AcknowledgedAt is null || copy.JournalClaim is null ||
                    retirement is not
                    {
                        Phase: MirrorPulseNamespaceRetirementPhase.Completed, TransferReceipt: not null,
                        TransferSourceProjection: not null, TransferSourceNameEpoch: >= 2
                    } ||
                    plan.Source.Identity.ItemId is not { } sourceItem || sourceItem == copy.TargetPublication.ItemId ||
                    copy.TargetPublication.MemberId == plan.Source.Context.Member.MemberId)
                    throw new InvalidDataException("A complete CLI transfer requires its original accepted copy, distinct publication, source retirement and actual completion receipt.");
                int originalCount = copy.JournalClaim.Members.Count;
                if ((originalCount != 0) != (copy.Acknowledgement.Kind == MirrorPulseTransferAcknowledgementKind.OriginalJournal))
                    throw new InvalidDataException("An empty command cannot claim acknowledgement of captured original notifications.");
                stage = "PrivateTransferBytes";
                await using var input = await new MirrorPulseLocalTransferContentStore(fixture.Paths).OpenLeaseAsync(plan);
                string digest = Convert.ToHexString(await SHA256.HashDataAsync(input.Data));
                if (input.Data.Length != plan.Length || !digest.Equals(plan.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The transfer lost or changed its original complete private source bytes.");
                receipts.Add(new(operation, source.InstanceId == target.InstanceId ? "SameInstance" : "CrossInstance",
                    source.RootId.Value, target.RootId.Value, plan.Source.Context.Member.MemberId, copy.TargetPublication.MemberId,
                    sourceItem, copy.TargetPublication.ItemId, plan.Length, digest, originalCount, copy.Acknowledgement.Kind.ToString(),
                    copy.Acknowledgement.AcknowledgedAt.Value, retirement.TransferSourceNameEpoch.Value, true)
                { Direction = singleRoot ? receipts.Count == 0 ? "Forward" : "Reverse" : null });
            }
            if (singleRoot ? receipts.Any(item => item.Scope != "CrossInstance") :
                receipts.Select(item => item.Scope).Distinct(StringComparer.Ordinal).Count() != 2)
                throw new InvalidDataException("The stopped transfer oracle lost its required original execution shapes.");
            stage = "DirectoryTransferReceipt";
            var directoryReceipt = fixture.Marker.DirectoryTransferOperationId is { } directoryId
                ? await ReadDirectoryTransferFixtureReceiptAsync(fixture, directoryId) : null;
            Console.WriteLine(JsonSerializer.Serialize(new CliTransferFixtureAudit(fixture.Marker.SchemaVersion, true, receipts.AsReadOnly())
            { DirectoryTransfer = directoryReceipt }, JsonOptions));
            return 0;
        }
        catch (Exception failure) { failure.Data["CliFixtureCleanupStage"] = stage; throw; }
    }

    private static async Task<CliDirectoryTransferFixtureReceipt> ReadDirectoryTransferFixtureReceiptAsync(
        TransferFixtureCatalog fixture, Guid operation)
    {
        var catalog = fixture.Catalog;
        var plan = await catalog.ReadNamespaceDirectoryTransferAsync(operation)
            ?? throw new InvalidDataException("The original CLI directory transfer plan is missing.");
        var status = await catalog.ReadNamespaceTransferStatusAsync(operation);
        var deletion = await catalog.ReadNamespaceDirectoryTransferSourceDeletionAsync(operation, CancellationToken.None);
        var retired = await catalog.ReadNamespaceDirectoryTransferRetirementsAsync(operation, CancellationToken.None);
        var acknowledgement = await catalog.ReadNamespaceDirectoryTransferAcknowledgementAsync(operation, CancellationToken.None);
        var source = fixture.Inventory.Roots.Single(root => root.RootId == plan.SourceRoute.RootId);
        var target = fixture.Inventory.Roots.Single(root => root.RootId == plan.TargetRoute.RootId);
        if (!MatchesOriginalTransferRoute(fixture.Marker, 0, source, target) || plan.Members.Count != 3 ||
            plan.Members.Any(member => member.SourceRevision is null) ||
            status is not { IsDirectory: true, Completed: true, RootsFenced: false, TargetMemberId: { } targetId } ||
            status.SourceRelativePath != source.DirectoryName + "/transfer-tree" ||
            status.TargetRelativePath != target.DirectoryName + "/transferred-tree" ||
            deletion is not { Receipt: { } receipt, AcceptedAt: not null } || receipt.DeletedOriginals.Count != 3 ||
            retired.Count != 3 || retired.Any(member => member.RetiredAt is null || member.NameEpoch is null) ||
            acknowledgement?.AcknowledgedAt is not { } completed || status.JournalAcknowledged != (plan.Originals.Members.Count != 0))
            throw new InvalidDataException("The directory command lost its original routes, copy, retirement or acknowledgement.");
        var file = plan.Members.Single(member => member.Content is not null);
        await using var privateBytes = await new MirrorPulseLocalTransferContentStore(fixture.Paths).OpenLeaseAsync(operation, file, CancellationToken.None);
        string digest = Convert.ToHexString(await SHA256.HashDataAsync(privateBytes.Data));
        if (privateBytes.Data.Length != file.Content!.Length || !digest.Equals(file.Content.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The directory command lost its verified private file bytes.");
        return new(operation, source.RootId.Value, target.RootId.Value, plan.Command.SourceMemberId, targetId,
            retired.Count, receipt.ReceiptId, plan.Originals.Members.Count, completed, file.Content.Length, digest);
    }

    private static bool MatchesOriginalTransferRoute(CliTransferFixtureMarker marker, int index,
        RootRegistration source, RootRegistration target)
    {
        if (marker.SchemaVersion == 1) return source.UniquenessKey == "left" && target.UniquenessKey is "right" or "target";
        string sourceSide = index == 0 ? "left" : "right", targetSide = index == 0 ? "right" : "left";
        string sourceKey = marker.SchemaVersion == 3 && index == 1 ? "webdav" : "local";
        string targetKey = marker.SchemaVersion == 3 && index == 0 ? "webdav" : "local";
        return source.UniquenessKey == sourceKey && target.UniquenessKey == targetKey && source.InstanceId != target.InstanceId &&
            marker.Routes.Single(route => route.FixtureSide == sourceSide).RootId == source.RootId.ToString() &&
            marker.Routes.Single(route => route.FixtureSide == targetSide).RootId == target.RootId.ToString();
    }
}
