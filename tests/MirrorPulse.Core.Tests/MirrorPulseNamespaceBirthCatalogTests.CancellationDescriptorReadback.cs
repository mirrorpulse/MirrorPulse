using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("CancellationDescriptorReadback")]
    [DataRow(true, true, true, "anchor")]
    [DataRow(true, true, true, "root")]
    [DataRow(true, true, true, "leaf")]
    [DataRow(true, true, false, "anchor")]
    [DataRow(true, true, false, "root")]
    [DataRow(true, true, false, "leaf")]
    [DataRow(true, false, true, "anchor")]
    [DataRow(true, false, true, "root")]
    [DataRow(true, false, true, "leaf")]
    [DataRow(true, false, false, "anchor")]
    [DataRow(true, false, false, "root")]
    [DataRow(true, false, false, "leaf")]
    [DataRow(false, true, true, "anchor")]
    [DataRow(false, true, true, "root")]
    [DataRow(false, true, true, "leaf")]
    [DataRow(false, true, false, "anchor")]
    [DataRow(false, true, false, "root")]
    [DataRow(false, true, false, "leaf")]
    [DataRow(false, false, true, "anchor")]
    [DataRow(false, false, true, "root")]
    [DataRow(false, false, true, "leaf")]
    [DataRow(false, false, false, "anchor")]
    [DataRow(false, false, false, "root")]
    [DataRow(false, false, false, "leaf")]
    public async Task CancellationReadbackPreservesVerifiedProtectionAndImmutableReceipt(bool sealedMove, bool enabled, bool born, string scope)
    {
        using var fixture = new Fixture(); RootSubtreeFixture setup;
        MirrorPulseRootLabelCancellation observed = null!;
        MirrorPulseRootLabelUnsealedCancellation? unsealed = null;
        Dictionary<Guid, MirrorPulseNamespacePermissionVerification> originals = [];
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = born ? await PrepareBornRootJournalFixtureAsync(catalog, enabled, nested: true)
                : await PrepareRootSubtreeFixtureAsync(catalog, enabled);
            if (sealedMove) await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
            else unsealed = await UnsealedCancellationAsync(catalog, setup);
            var tree = await catalog.ReadRootLabelCancellationTreeAsync(setup.Move.OperationId);
            foreach (var snapshot in tree.Members.Prepend(tree.Anchor))
                originals.Add(snapshot.Context.Member.MemberId, snapshot.Context.Verification);
            var original = unsealed?.Observation ?? await RootCancellationAsync(catalog, setup);
            Guid selected = scope == "anchor" ? setup.Anchor.MemberId : scope == "root" ? setup.Root.MemberId
                : setup.Members.First(member => !member.IsDirectory).MemberId;
            observed = ChangeCancellationReadback(original, selected, native => native with { Dacl = CancellationCompletionDacl(native.Dacl) });
            var originalFact = selected == setup.Anchor.MemberId ? original.Anchor : original.Members.Single(member => member.MemberId == selected).Native;
            var observedFact = selected == setup.Anchor.MemberId ? observed.Anchor : observed.Members.Single(member => member.MemberId == selected).Native;
            Assert.AreNotEqual(originalFact.Dacl, observedFact.Dacl, "This must exercise an actual added completion marker.");
            Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(originalFact.Dacl, observedFact.Dacl));
            await AcceptCancellationReadbackAsync(catalog, observed, unsealed);
            if (unsealed is null) await AssertRootCancellationSettledAsync(catalog, setup, observed);
            else await AssertUnsealedCancellationAsync(catalog, setup, unsealed with { Observation = observed });
        }
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await AcceptCancellationReadbackAsync(reopened, observed, unsealed);
            var stored = unsealed is null ? await reopened.ReadRootLabelCancellationAsync(setup.Move.OperationId)
                : (await reopened.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId))!.Observation;
            Assert.AreEqual(JsonSerializer.Serialize(observed), JsonSerializer.Serialize(stored));
            foreach (var member in setup.Members.Prepend(setup.Anchor))
                Assert.AreEqual(originals[member.MemberId], (await reopened.ReadNamespaceMemberProtectionSnapshotAsync(member.MemberId))!.Context.Verification);
            Guid selected = scope == "anchor" ? setup.Anchor.MemberId : scope == "root" ? setup.Root.MemberId
                : setup.Members.First(member => !member.IsDirectory).MemberId;
            var changedReplay = ChangeCancellationReadback(observed, selected, native => native with { Dacl = originals[selected].Dacl });
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => AcceptCancellationReadbackAsync(reopened, changedReplay, unsealed));
            Assert.IsNull(await reopened.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
        }
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("CancellationDescriptorReadback")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CancellationReadbackRetainsTheUnchangedDescriptorControl(bool sealedMove)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog);
        if (sealedMove) await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        var unsealed = sealedMove ? null : await UnsealedCancellationAsync(catalog, setup);
        await AcceptCancellationReadbackAsync(catalog, unsealed?.Observation ?? await RootCancellationAsync(catalog, setup), unsealed);
    }

    [TestMethod]
    [TestCategory("CancellationDescriptorReadback")]
    [DataRow(true, "role")]
    [DataRow(false, "role")]
    [DataRow(true, "owner-policy")]
    [DataRow(false, "owner-policy")]
    [DataRow(true, "rights")]
    [DataRow(false, "rights")]
    [DataRow(true, "add-ace")]
    [DataRow(false, "add-ace")]
    [DataRow(true, "remove-protection")]
    [DataRow(false, "remove-protection")]
    [DataRow(true, "add-request")]
    [DataRow(false, "add-request")]
    [DataRow(true, "owner")]
    [DataRow(false, "owner")]
    [DataRow(true, "binding")]
    [DataRow(false, "binding")]
    public async Task CancellationReadbackRefusesChangedPolicyOrObjectWithoutReleasingOwners(bool sealedMove, string change)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog);
        if (sealedMove) await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        var unsealed = sealedMove ? null : await UnsealedCancellationAsync(catalog, setup);
        var original = unsealed?.Observation ?? await RootCancellationAsync(catalog, setup);
        var damaged = ChangeCancellationReadback(original, setup.Root.MemberId, native => DamageCancellationReadback(native, change));
        if (!sealedMove && (change == "owner" || change == "binding"))
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => AcceptCancellationReadbackAsync(catalog, damaged, unsealed));
        else
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => AcceptCancellationReadbackAsync(catalog, damaged, unsealed));
        Assert.AreEqual(MirrorPulseRootRenamePhase.Prepared, (await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId))!.Intent.Phase);
        Assert.IsNull(await catalog.ReadRootLabelCancellationAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
    }

    private static string CancellationCompletionDacl(string value)
    {
        var descriptor = new RawSecurityDescriptor(value);
        descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited);
        return descriptor.GetSddlForm(AccessControlSections.Access);
    }

    private static MirrorPulseNamespacePermissionVerification DamageCancellationReadback(MirrorPulseNamespacePermissionVerification native, string change)
    {
        var observed = native with { Dacl = CancellationCompletionDacl(native.Dacl) };
        if (change == "owner") return observed with { OwnerSid = "S-1-5-21-100-200-300-1002" };
        if (change == "binding") return observed with { LocalObject = observed.LocalObject with { LocalFileId = Guid.NewGuid() } };
        if (change == "role") return observed with { Dacl = observed.Dacl.Replace(Role, "S-1-5-5-123-457", StringComparison.Ordinal) };
        if (change == "owner-policy") return observed with { Dacl = observed.Dacl.Replace(Owner, "S-1-5-21-100-200-300-1002", StringComparison.Ordinal) };
        var descriptor = new RawSecurityDescriptor(observed.Dacl);
        if (change == "rights") ((CommonAce)descriptor.DiscretionaryAcl![0]).AccessMask ^= 0x10;
        else if (change == "add-ace") descriptor.DiscretionaryAcl!.InsertAce(descriptor.DiscretionaryAcl.Count,
            new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x120089, new SecurityIdentifier(WellKnownSidType.WorldSid, null), false, null));
        else descriptor.SetFlags(change == "remove-protection" ? descriptor.ControlFlags & ~ControlFlags.DiscretionaryAclProtected
            : descriptor.ControlFlags | ControlFlags.DiscretionaryAclAutoInheritRequired);
        return observed with { Dacl = descriptor.GetSddlForm(AccessControlSections.Access) };
    }

    private static MirrorPulseRootLabelCancellation ChangeCancellationReadback(MirrorPulseRootLabelCancellation original, Guid selected,
        Func<MirrorPulseNamespacePermissionVerification, MirrorPulseNamespacePermissionVerification> change)
        => selected == original.AnchorMemberId ? original with { Anchor = change(original.Anchor) }
            : original with { Members = original.Members.Select(member => member.MemberId == selected ? member with { Native = change(member.Native) } : member).ToArray() };

    private static async Task AcceptCancellationReadbackAsync(MirrorPulseProductCatalog catalog, MirrorPulseRootLabelCancellation observation,
        MirrorPulseRootLabelUnsealedCancellation? unsealed)
    {
        if (unsealed is null) _ = await catalog.CancelUnstartedRootLabelAsync(observation);
        else _ = await catalog.CancelUnsealedRootLabelAsync(unsealed with { Observation = observation });
    }
}
