using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task RemoteIdentityResolverSelectsRemoteDeletionSuccessorWithoutChangingOriginalOutcomeAcrossRestart()
    {
        using var fixture = new Fixture(); RootRegistration root; CloudPlaceholderIdentity successor;
        MirrorPulseNamespaceRetirement terminal; string original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var prepared = await PrepareCanonicalRemoteRetirementAsync(catalog); root = prepared.Root;
            await catalog.RecordNamespaceRetirementStartAsync(prepared.Intent.OperationId, prepared.Intent.PreparedAt, prepared.Intent.RemoteEntry!.ItemId);
            var outcome = RemoteRetirementOutcome(prepared.Intent, false);
            terminal = await catalog.SettleNamespaceRemoteRetirementAsync(outcome);
            Assert.IsNotNull(terminal.RemoteSourceNameEpoch); Assert.IsGreaterThanOrEqualTo(2L, terminal.RemoteSourceNameEpoch.Value);
            original = await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths);
            var resolver = new MirrorPulseRemoteIdentityResolver(catalog, new TransferClock(outcome.VerifiedAt.AddSeconds(1)));
            successor = await resolver.ResolveAsync(root, "backend-file", "later-v2");
            Assert.AreNotEqual(prepared.Intent.RemoteEntry.ItemId, successor.ItemId);
            Assert.AreNotEqual(prepared.Intent.RemoteEntry.RemoteId, successor.RemoteId);
            var selected = await catalog.ReadRemoteIdentityGenerationAsync(root.RootId, prepared.Intent.RemoteEntry.ItemId);
            Assert.IsNotNull(selected); Assert.AreEqual(2, selected.Version); Assert.AreEqual(Guid.Empty, selected.TransferOperationId);
            Assert.AreEqual(terminal.OperationId, selected.RetirementOperationId);
            Assert.AreEqual("backend-file", MirrorPulsePlaceholderIdentity.Decode(root.InstanceId, successor.Encode()).RemoteId);
            await resolver.RequireCurrentAsync(root, successor);
            var old = MirrorPulsePlaceholderIdentity.CreateForRoot(root, "backend-file", "accepted").ToCfSharp();
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => resolver.RequireCurrentAsync(root, old).AsTask());
            Assert.AreEqual(original, await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths));
            Assert.AreEqual(terminal, await catalog.SettleNamespaceRemoteRetirementAsync(outcome));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var observed = await new MirrorPulseRemoteIdentityResolver(reopened).ResolveAsync(root, "backend-file", "later-v3");
        Assert.AreEqual(successor.ItemId, observed.ItemId); Assert.AreEqual(successor.RemoteId, observed.RemoteId);
        Assert.AreEqual("later-v3", observed.RemoteRevision);
        Assert.AreEqual(terminal, await reopened.ReadNamespaceRetirementAsync(terminal.OperationId));
        Assert.AreEqual(original, await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PendingOriginalRemoteDeletionFencesCallbacksAndSuccessorSelection(bool started)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareCanonicalRemoteRetirementAsync(catalog);
        if (started) await catalog.RecordNamespaceRetirementStartAsync(prepared.Intent.OperationId, prepared.Intent.PreparedAt, prepared.Intent.RemoteEntry!.ItemId);
        var original = await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths);
        var resolver = new MirrorPulseRemoteIdentityResolver(catalog);
        var old = await resolver.ReadCurrentAsync(prepared.Root, "backend-file", "accepted");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => resolver.RequireCurrentAsync(prepared.Root, old).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => resolver.ResolveAsync(prepared.Root, "backend-file", "later-v2").AsTask());
        Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(prepared.Root.RootId, old.ItemId));
        Assert.AreEqual(original, await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths));
        CollectionAssert.Contains((await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), prepared.Root.RootId);
    }

    [TestMethod]
    public async Task RejectedOriginalRemoteDeletionKeepsItsCurrentIdentityAndConflictAcrossRestart()
    {
        using var fixture = new Fixture(); RootRegistration root; MirrorPulseNamespaceRetirement terminal;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var prepared = await PrepareCanonicalRemoteRetirementAsync(catalog); root = prepared.Root;
            await catalog.RecordNamespaceRetirementStartAsync(prepared.Intent.OperationId, prepared.Intent.PreparedAt, prepared.Intent.RemoteEntry!.ItemId);
            terminal = await catalog.SettleNamespaceRemoteRetirementAsync(RemoteRetirementOutcome(prepared.Intent, true));
            Assert.IsNull(terminal.RemoteSourceNameEpoch);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var resolver = new MirrorPulseRemoteIdentityResolver(reopened);
        var current = await resolver.ResolveAsync(root, "backend-file", "accepted");
        Assert.AreEqual(terminal.Intent.RemoteEntry!.ItemId, current.ItemId);
        await resolver.RequireCurrentAsync(root, current);
        Assert.IsNull(await reopened.ReadRemoteIdentityGenerationAsync(root.RootId, current.ItemId));
        Assert.IsNotNull((await reopened.ReadNamespaceRemoteRetirementOutcomeAsync(terminal.OperationId))!.ConflictId);
    }

    private static async Task<(RootRegistration Root, MirrorPulseNamespaceRetirementIntent Intent)> PrepareCanonicalRemoteRetirementAsync(
        MirrorPulseProductCatalog catalog, bool legacy = false)
    {
        var prepared = await PrepareCanonicalResolverSourceAsync(catalog, false);
        var intent = await RemoteRetirementAsync(catalog, prepared.Plan.Source.Context.Member);
        intent = intent with
        {
            RemoteEntry = intent.RemoteEntry! with
            {
                Version = legacy ? 1 : 2,
                InstanceId = prepared.Root.InstanceId,
                RootKey = legacy ? null : prepared.Root.UniquenessKey,
                BatchId = prepared.Root.InstanceId + "/remote-retirement"
            }
        };
        await catalog.PrepareNamespaceRetirementAsync(intent);
        return (prepared.Root, intent);
    }

    private static async Task<string> ReadRemoteRetirementGenerationOriginalsAsync(MirrorPulseStoragePaths paths)
    {
        await using var connection = Connection(paths); await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT 'retirement',operation_id,hex(payload),hex(fingerprint),phase FROM namespace_member_retirements
            UNION ALL SELECT 'outcome',operation_id,hex(payload),hex(fingerprint),0 FROM namespace_remote_retirement_outcomes
            ORDER BY 1,2;
            """;
        var rows = new List<string>(); await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(string.Join('|', Enumerable.Range(0, 5).Select(reader.GetValue)));
        return string.Join('\n', rows);
    }
}
