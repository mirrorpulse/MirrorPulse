using System.Security.AccessControl;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed partial class MirrorPulseNamespaceMemberLocatorTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task OfficialIdentityResolvesMovedProjectionAcrossOwnersWithoutChangingFirstMembership(bool born, bool directory)
    {
        await using var fixture = await Fixture.OpenAsync(born, directory);
        await using var actor = new MirrorPulseNamespaceActor();
        await fixture.PutAsync(fixture.Row());
        var first = await ResolveAsync(fixture, actor);
        Assert.AreEqual(MirrorPulseNamespaceMemberLocationState.Projected, first.State);
        Assert.AreEqual(Fixture.FirstPath, first.RelativePath);
        var moved = fixture.Row(@"Docs\Other\moved");
        await fixture.PutAsync(moved);
        await fixture.PutAsync(new(Guid.NewGuid(), "another-object", Fixture.FirstPath, fixture.Kind, null, 42, false, DateTimeOffset.UtcNow));
        await fixture.RestartAsync();
        var current = await ResolveAsync(fixture, actor);
        Assert.AreEqual(MirrorPulseNamespaceMemberLocationState.Projected, current.State);
        Assert.AreEqual(moved.RelativePath, current.RelativePath);
        Assert.AreEqual(fixture.ItemId, current.ItemId);
        Assert.AreEqual(fixture.Member, current.Member);
        Assert.AreEqual("Docs/existing", current.Member.RelativePath);
        Assert.AreEqual(fixture.Member, await fixture.Catalog.ReadNamespaceMemberAsync(fixture.Member.MemberId));
        Assert.AreEqual(fixture.Member, await fixture.Catalog.ReadNamespaceMemberChildAsync(fixture.Parent.MemberId, "EXISTING"));
        await fixture.AssertRowAndNoJournalAsync(moved);
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task CurrentRemoteIdentityAndRevisionAreReportedWithoutRewritingTheOriginalIdentity()
    {
        await using var fixture = await Fixture.OpenAsync();
        await using var actor = new MirrorPulseNamespaceActor();
        var current = new CloudItemState(fixture.ItemId, "source:accepted", Fixture.FirstPath, fixture.Kind, "revision-2", 42, false, DateTimeOffset.UtcNow);
        await fixture.PutAsync(current);
        var candidate = await ResolveAsync(fixture, actor);
        Assert.AreEqual("local:original", candidate.OriginalRemoteId);
        Assert.AreEqual(current.RemoteId, candidate.RemoteId);
        Assert.AreEqual(current.RemoteRevision, candidate.RemoteRevision);
        var original = await fixture.Catalog.ReadNamespacePermissionLocalIdentityAsync(fixture.Member.OriginId);
        Assert.IsNotNull(original); Assert.AreEqual("local:original", original.RemoteId);
        await fixture.AssertRowAndNoJournalAsync(current);
    }

    [TestMethod]
    public async Task MissingRetainedItemDoesNotAdoptTheOldPathOrAReusedRemoteIdentifier()
    {
        await using var fixture = await Fixture.OpenAsync();
        await using var actor = new MirrorPulseNamespaceActor();
        var foreign = new CloudItemState(Guid.NewGuid(), "local:original", Fixture.FirstPath, fixture.Kind, null, 42, false, DateTimeOffset.UtcNow);
        await fixture.PutAsync(foreign);
        var candidate = await ResolveAsync(fixture, actor);
        Assert.AreEqual(MirrorPulseNamespaceMemberLocationState.ProjectionMissing, candidate.State);
        Assert.AreEqual(fixture.ItemId, candidate.ItemId);
        Assert.IsNull(candidate.RelativePath);
        Assert.IsNull(candidate.RemoteId);
        await fixture.AssertRowAndNoJournalAsync(foreign);
    }

    [TestMethod]
    public async Task LegacyMemberWithoutOriginalIdentityDoesNotInventItFromTheCurrentPath()
    {
        await using var fixture = await Fixture.OpenAsync(knownIdentity: false);
        await using var actor = new MirrorPulseNamespaceActor();
        var row = fixture.Row(); await fixture.PutAsync(row);
        var candidate = await ResolveAsync(fixture, actor);
        Assert.AreEqual(MirrorPulseNamespaceMemberLocationState.IdentityUnavailable, candidate.State);
        Assert.IsNull(candidate.ItemId); Assert.IsNull(candidate.RelativePath);
        Assert.IsNull(await fixture.Catalog.ReadNamespacePermissionLocalIdentityAsync(fixture.Member.OriginId));
        await fixture.AssertRowAndNoJournalAsync(row);
    }

    [TestMethod]
    public async Task TombstoneRemainsAnOfficialObservationWithoutRetiringMembershipOrReleasingItsName()
    {
        await using var fixture = await Fixture.OpenAsync();
        await using var actor = new MirrorPulseNamespaceActor();
        var tombstone = new CloudItemState(fixture.ItemId, "local:original", @"Docs\deleted", fixture.Kind, null, 42, true, DateTimeOffset.UtcNow);
        await fixture.PutAsync(tombstone); await fixture.RestartAsync();
        var candidate = await ResolveAsync(fixture, actor);
        Assert.AreEqual(MirrorPulseNamespaceMemberLocationState.ProjectedTombstone, candidate.State);
        Assert.AreEqual(tombstone.RelativePath, candidate.RelativePath);
        Assert.AreEqual(fixture.Member, await fixture.Catalog.ReadNamespaceMemberChildAsync(fixture.Parent.MemberId, "existing"));
        await fixture.AssertRowAndNoJournalAsync(tombstone);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RetainedPublicIdentityCannotChangeItsObjectKind(bool born, bool directory)
    {
        await using var fixture = await Fixture.OpenAsync(born, directory);
        await using var actor = new MirrorPulseNamespaceActor();
        var wrong = new CloudItemState(fixture.ItemId, "local:original", Fixture.FirstPath,
            directory ? CloudItemKind.File : CloudItemKind.Directory, null, 42, false, DateTimeOffset.UtcNow);
        await fixture.PutAsync(wrong);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ResolveAsync(fixture, actor));
        await fixture.AssertRowAndNoJournalAsync(wrong);
    }

    [TestMethod]
    public async Task QueriesRequireActiveAdmissionAndCancellationDoesNotPublishAnything()
    {
        await using var fixture = await Fixture.OpenAsync();
        await using var actor = new MirrorPulseNamespaceActor();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            MirrorPulseNamespaceMemberLocator.ResolveAsync(fixture.Catalog, fixture.Store, fixture.Member.MemberId, actor));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await actor.RunAsync(async _ =>
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                MirrorPulseNamespaceMemberLocator.ResolveAsync(fixture.Catalog, fixture.Store, fixture.Member.MemberId, actor, cancellation.Token));
            return true;
        });
        var candidate = await ResolveAsync(fixture, actor);
        Assert.AreEqual(MirrorPulseNamespaceMemberLocationState.ProjectionMissing, candidate.State);
    }

    [TestMethod]
    public async Task AnchorAndOriginalDirectoryWithoutPublicIdentityAreReportedExplicitly()
    {
        await using var fixture = await Fixture.OpenAsync();
        await using var actor = new MirrorPulseNamespaceActor();
        var anchor = await actor.RunAsync(async token => await MirrorPulseNamespaceMemberLocator.ResolveAsync(
            fixture.Catalog, fixture.Store, fixture.Anchor.MemberId, actor, token));
        Assert.AreEqual(MirrorPulseNamespaceMemberLocationState.SyncRootAnchor, anchor.State);
        Assert.IsNull(anchor.ItemId); Assert.IsNull(anchor.RelativePath);
        var directory = await actor.RunAsync(async token => await MirrorPulseNamespaceMemberLocator.ResolveAsync(
            fixture.Catalog, fixture.Store, fixture.Parent.MemberId, actor, token));
        Assert.AreEqual(MirrorPulseNamespaceMemberLocationState.IdentityUnavailable, directory.State);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => actor.RunAsync(async token =>
            await MirrorPulseNamespaceMemberLocator.ResolveAsync(fixture.Catalog, fixture.Store, Guid.NewGuid(), actor, token)).AsTask());
    }

    private static Task<MirrorPulseNamespaceMemberLocationCandidate> ResolveAsync(Fixture fixture, MirrorPulseNamespaceActor actor) =>
        actor.RunAsync(async token => await MirrorPulseNamespaceMemberLocator.ResolveAsync(
            fixture.Catalog, fixture.Store, fixture.Member.MemberId, actor, token)).AsTask();

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Owner = "S-1-5-21-100-200-300-1001", Role = "S-1-5-5-123-456";
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-member-location-tests", Guid.NewGuid().ToString("N"));
        private static readonly string Original = Dacl($"D:AI(A;OICI;FA;;;{Owner})");
        private static readonly string Protected = Dacl($"D:P(A;OICI;FRFW;;;{Owner})(A;OICI;FA;;;{Role})");
        public const string FirstPath = @"Docs\existing";
        public MirrorPulseStoragePaths Paths { get; private set; } = null!;
        public MirrorPulseProductCatalog Catalog { get; private set; } = null!;
        public ICloudStateStore Store { get; private set; } = null!;
        public MirrorPulseNamespaceMemberRegistration Anchor { get; private set; } = null!;
        public MirrorPulseNamespaceMemberRegistration Parent { get; private set; } = null!;
        public MirrorPulseNamespaceMemberRegistration Member { get; private set; } = null!;
        public Guid ItemId { get; private set; } = Guid.NewGuid();
        public CloudItemKind Kind => Member.IsDirectory ? CloudItemKind.Directory : CloudItemKind.File;

        public static async Task<Fixture> OpenAsync(bool born = false, bool directory = false, bool knownIdentity = true)
        {
            var fixture = new Fixture();
            try
            {
                fixture.Paths = new(Path.Combine(fixture._directory, "sync"), Path.Combine(fixture._directory, "data"));
                await fixture.OpenOwnersAsync();
                var binding = new MirrorPulseLocalFileBinding(7, Guid.NewGuid(), Guid.NewGuid());
                var anchor = new MirrorPulseNamespacePermissionBaseline(Guid.NewGuid(), null, binding with { LocalFileId = binding.SyncRootFileId },
                    string.Empty, true, Owner, Original, DateTimeOffset.UtcNow);
                fixture.Anchor = await fixture.RegisterOriginalAsync(anchor, null, false);
                var parent = anchor with
                {
                    EvidenceId = Guid.NewGuid(),
                    RootId = RootId.New(),
                    LocalObject = binding,
                    RelativePath = "Docs",
                    CapturedAt = DateTimeOffset.UtcNow
                };
                fixture.Parent = await fixture.RegisterOriginalAsync(parent, fixture.Anchor.MemberId, false);
                var child = parent with
                {
                    EvidenceId = Guid.NewGuid(),
                    LocalObject = binding with { LocalFileId = Guid.NewGuid() },
                    RelativePath = "Docs/existing",
                    IsDirectory = directory,
                    CapturedAt = DateTimeOffset.UtcNow
                };
                if (!born) fixture.Member = await fixture.RegisterOriginalAsync(child, fixture.Parent.MemberId, knownIdentity);
                else
                {
                    var permission = await fixture.Catalog.ReadLatestNamespacePermissionChangeAsync(parent.EvidenceId); Assert.IsNotNull(permission);
                    var birth = new MirrorPulseNamespaceBirthIntent(1, Guid.NewGuid(), parent.RootId!.Value, parent.EvidenceId,
                        permission.Intent.OperationId, binding, child.RelativePath, directory, MirrorPulseNamespaceBirthOrigin.RemotePopulation, Role, DateTimeOffset.UtcNow);
                    await fixture.Catalog.PrepareNamespaceBirthAsync(birth);
                    var plan = new MirrorPulseNamespaceBirthPlan(1, birth.OperationId, fixture.ItemId, "local:original", "revision-1", DateTimeOffset.UtcNow);
                    await fixture.Catalog.PrepareNamespaceBirthPlanAsync(plan);
                    await fixture.Catalog.RecordNamespaceBirthStartAsync(new(1, birth.OperationId, DateTimeOffset.UtcNow));
                    var observation = new MirrorPulseNamespaceBirthObservation(1, birth.OperationId, birth.RootId, birth.RelativePath,
                        directory, child.LocalObject, plan.ItemId, plan.RemoteId, plan.RemoteRevision, true, true, 1, Owner, Protected, DateTimeOffset.UtcNow);
                    await fixture.Catalog.RecordNamespaceBirthObservationAsync(observation);
                    var protection = new MirrorPulseNamespaceBirthProtection(1, Guid.NewGuid(), birth.OperationId, null,
                        permission.Intent.OperationId, Role, new(child.LocalObject, Owner, Protected, DateTimeOffset.UtcNow), true, 1);
                    await fixture.Catalog.RecordNamespaceBirthProtectionAsync(protection);
                    fixture.Member = new(1, Guid.NewGuid(), fixture.Parent.MemberId, MirrorPulseNamespaceMemberOrigin.ProtectedBirth,
                        birth.OperationId, protection.ProtectionId, birth.RootId, birth.RelativePath, child.LocalObject, directory, Owner, DateTimeOffset.UtcNow);
                    await fixture.Catalog.RegisterNamespaceMemberAsync(fixture.Member);
                }
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private async Task<MirrorPulseNamespaceMemberRegistration> RegisterOriginalAsync(MirrorPulseNamespacePermissionBaseline original,
            Guid? parentId, bool knownIdentity)
        {
            var intent = new MirrorPulseNamespacePermissionIntent(Guid.NewGuid(), original.EvidenceId, original.LocalObject,
                original.RootId, original.RelativePath, MirrorPulseNamespacePermissionChangeKind.Protect, Role, Original, Protected, DateTimeOffset.UtcNow);
            await Catalog.PrepareNamespacePermissionChangeAsync(original, intent);
            if (knownIdentity)
                await Catalog.PrepareNamespacePermissionLocalIdentityAsync(new(1, original.EvidenceId, intent.OperationId,
                    original.LocalObject, ItemId, "local:original", DateTimeOffset.UtcNow));
            await Catalog.RecordNamespacePermissionApplicationAsync(intent.OperationId, original.LocalObject, DateTimeOffset.UtcNow);
            await Catalog.VerifyNamespacePermissionChangeAsync(intent.OperationId, new(original.LocalObject, Owner, Protected, DateTimeOffset.UtcNow));
            var member = new MirrorPulseNamespaceMemberRegistration(1, Guid.NewGuid(), parentId, MirrorPulseNamespaceMemberOrigin.CapturedOriginal,
                original.EvidenceId, intent.OperationId, original.RootId, original.RelativePath, original.LocalObject, original.IsDirectory, Owner, DateTimeOffset.UtcNow);
            return await Catalog.RegisterNamespaceMemberAsync(member);
        }

        public CloudItemState Row(string path = FirstPath) => new(ItemId, "local:original", path, Kind, "revision-1", 42, false, DateTimeOffset.UtcNow);
        public async Task PutAsync(CloudItemState row)
        {
            await using var transaction = await Store.BeginTransactionAsync();
            await transaction.Items.UpsertAsync(row); await transaction.CommitAsync();
        }
        public async Task AssertRowAndNoJournalAsync(CloudItemState expected)
        {
            await using var transaction = await Store.BeginTransactionAsync();
            var actual = await transaction.Items.GetByItemIdAsync(expected.ItemId); Assert.IsNotNull(actual);
            Assert.AreEqual(expected.RelativePath, actual.RelativePath); Assert.AreEqual(expected.RemoteId, actual.RemoteId);
            Assert.AreEqual(expected.UpdatedAt, actual.UpdatedAt); Assert.AreEqual(expected.RemoteRevision, actual.RemoteRevision);
            Assert.AreEqual(expected.IsTombstone, actual.IsTombstone); Assert.AreEqual(expected.LocalFileId, actual.LocalFileId);
            Assert.HasCount(0, await transaction.Operations.ListAsync(16));
            await transaction.RollbackAsync();
        }
        public async Task RestartAsync()
        {
            await Store.DisposeAsync(); await Catalog.DisposeAsync(); await OpenOwnersAsync();
        }
        private async Task OpenOwnersAsync()
        {
            Catalog = await MirrorPulseProductCatalog.OpenAsync(Paths);
            Store = await MirrorPulseCfSharpStateStoreFactory.Create(Paths).OpenAsync(new(Paths.SyncRootPath));
        }
        private static string Dacl(string value) => new RawSecurityDescriptor(value).GetSddlForm(AccessControlSections.Access);
        public async ValueTask DisposeAsync()
        {
            if (Store is not null) await Store.DisposeAsync();
            if (Catalog is not null) await Catalog.DisposeAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
