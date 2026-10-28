using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task BornRootLabelJournalRetainsItsActualBirthProtectionAcrossCatalogOwners(bool enabled, bool nested)
    {
        using var fixture = new Fixture();
        MirrorPulseRootLabelJournalAcknowledgement first;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareBornRootJournalFixtureAsync(catalog, enabled, nested);
            var receipt = await PrepareRootSubtreeFactsAsync(catalog, setup);
            await catalog.CompleteNamespaceDirectoryMoveAsync(receipt);
            var inventory = await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Registration.RootId);
            var root = inventory.Members.Single(member => member.Context.Member.MemberId == setup.Root.MemberId);
            Assert.AreEqual(MirrorPulseNamespaceMemberOrigin.ProtectedBirth, root.Context.Member.Origin);
            Assert.AreEqual(Guid.Empty, root.PermissionOperationId);
            Assert.IsNotNull(root.BornParent);
            Assert.AreNotEqual(Guid.Empty, root.Context.Protection.ProtectionId);
            Assert.IsEmpty(inventory.FencedRoots);
            DateTimeOffset audit = receipt.Root.VerifiedAt.AddSeconds(3);
            Console.WriteLine($"BornRootJournalProof: enabled={enabled}; nested={nested}; permissionOperationAbsent=True; committedProtectionPresent=True; nativeExecuted=False.");
            first = await catalog.StartRootLabelJournalAcknowledgementAsync(inventory, root.Context.Protection.RoleSid,
                audit, RootLabelNativeIdentity(setup, audit), RootLabelObservation(setup, audit), audit.AddSeconds(1));
            Assert.AreEqual(root.Context.Protection.ProtectionId, first.Witness.ProtectionId);
            Assert.IsNull(await catalog.ReadNamespacePermissionChangeAsync(first.Witness.ProtectionId),
                "A birth protection reference must not fabricate an original permission operation.");
            Assert.AreEqual(inventory.Fingerprint, first.Witness.InventoryFingerprint);
            CollectionAssert.AreEqual(new[] { setup.Registration.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        }
        MirrorPulseRootLabelJournalAcknowledgement completed;
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(first, await reopened.ReadRootLabelJournalAcknowledgementAsync(first.Observation.OperationId));
            CollectionAssert.AreEqual(new[] { first.Witness.RootId }, (await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
            completed = await reopened.CompleteRootLabelJournalAcknowledgementAsync(first, first.StartedAt.AddSeconds(1));
            Assert.AreEqual(first.Witness, completed.Witness); Assert.AreEqual(first.Observation, completed.Observation);
            Assert.IsEmpty(await reopened.ReadNamespaceDispatchFencedRootsAsync());
        }
        await using (var final = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(completed, await final.ReadRootLabelJournalAcknowledgementAsync(first.Observation.OperationId));
            Assert.IsEmpty(await final.ReadPendingRootLabelJournalAcknowledgementsAsync());
            Assert.IsEmpty(await final.ReadNamespaceDispatchFencedRootsAsync());
        }
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    private static async Task<RootSubtreeFixture> PrepareBornRootJournalFixtureAsync(MirrorPulseProductCatalog catalog, bool enabled, bool nested,
        Func<MirrorPulseProductCatalog, IReadOnlyList<MirrorPulseNamespaceMemberRegistration>, Task>? beforePrepare = null)
    {
        var manifest = new AdapterManifest(1, AdapterId.Parse("example.born-root-journal"), "Example", "1.0.0", new(1, 1),
            new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
            new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0", [new("docs", "Docs", "Docs", false)]);
        var installation = new InstalledAdapter(manifest, InstallId.New(), "C:\\synthetic\\installed",
            new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        await catalog.AddInstallationAsync(installation);
        await catalog.CreateInstanceAsync(installation.InstallId, "Example", new Dictionary<string, string>(), [],
            "C:\\synthetic\\files", "C:\\synthetic\\transfers", true);
        var registration = (await catalog.ReadAdapterTopologyAsync()).Roots.Single();
        var anchorEvidence = Baseline() with { RootId = null, RelativePath = string.Empty };
        anchorEvidence = anchorEvidence with { LocalObject = anchorEvidence.LocalObject with { LocalFileId = anchorEvidence.LocalObject.SyncRootFileId } };
        var anchorPermission = Protection(anchorEvidence); await ProtectAsync(catalog, anchorEvidence, anchorPermission);
        var anchor = await catalog.RegisterNamespaceMemberAsync(OriginalMember(anchorEvidence, anchorPermission, null));
        var born = await AddBornAsync(catalog, new(1, Guid.NewGuid(), registration.RootId, anchorEvidence.EvidenceId,
            anchorPermission.OperationId, anchorEvidence.LocalObject, registration.DirectoryName, true,
            MirrorPulseNamespaceBirthOrigin.RemotePopulation, Role, anchorPermission.PreparedAt.AddSeconds(4)));
        var root = await catalog.RegisterNamespaceMemberAsync(BornMember(born, anchor));
        var members = new List<MirrorPulseNamespaceMemberRegistration> { root };
        if (nested)
        {
            var childBirth = await AddBornAsync(catalog, NestedBirth(born, "nested", true));
            var child = await catalog.RegisterNamespaceMemberAsync(BornMember(childBirth, root)); members.Add(child);
            var leafBirth = await AddBornAsync(catalog, NestedBirth(childBirth, "leaf.bin", false));
            members.Add(await catalog.RegisterNamespaceMemberAsync(BornMember(leafBirth, child)));
        }
        if (beforePrepare is not null) await beforePrepare(catalog, members.AsReadOnly());
        if (!enabled)
        {
            await catalog.SetInstanceEnabledAsync(registration.InstanceId, false);
            registration = (await catalog.ReadAdapterTopologyAsync()).Roots.Single();
        }
        var capturedAt = members.Max(member => member.RegisteredAt).AddSeconds(1);
        // Private catalog facts only: no SDK identity decoding, native operation, Worker or journal ACK return is claimed.
        var rename = await catalog.PrepareManagedRootRenameWithProofAsync(registration.RootId, "Renamed", "Docs",
            new(born.Observation.ItemId, root.LocalObject, "AQID", capturedAt, "AA=="));
        var move = new MirrorPulseNamespaceMemberMoveIntent(3, rename.Intent.OperationId, root.MemberId, root.ProtectionId,
            registration.RootId, root.LocalObject, born.Observation.ItemId, born.Observation.RemoteId, root.OwnerSid, Role,
            new(anchor.MemberId, anchor.LocalObject, anchor.ProtectionId, "Docs"),
            new(anchor.MemberId, anchor.LocalObject, anchor.ProtectionId, "Renamed"), capturedAt.AddSeconds(1))
        { DirectoryMoveEvidence = rename.Proof!.DirectoryMoveEvidence };
        return new(registration, rename, anchor, root, members.ToArray(), move);
    }
}
