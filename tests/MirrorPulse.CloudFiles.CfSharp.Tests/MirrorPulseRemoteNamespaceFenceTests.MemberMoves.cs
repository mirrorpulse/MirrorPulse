using System.Security.AccessControl;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseRemoteNamespaceFenceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ControlledMoveFencesPollingBothRemotePathsAndCursorAcrossOwners(bool started)
    {
        await using var fixture = await MoveFixture.OpenAsync();
        Assert.IsTrue(await fixture.Fence.CanPollAsync(fixture.Docs, default));
        await fixture.Catalog.PrepareNamespaceMemberMoveAsync(fixture.Move);
        if (started) await fixture.Catalog.RecordNamespaceMemberMoveStartAsync(new(1, fixture.Move.OperationId, DateTimeOffset.UtcNow));
        for (int owner = 0; owner < 2; owner++)
        {
            Assert.IsFalse(await fixture.Fence.CanPollAsync(fixture.Docs, default));
            Assert.IsTrue(await fixture.Fence.CanPollAsync(fixture.Archive, default));
            Assert.IsTrue(await fixture.Fence.CanPollAsync(fixture.Foreign, default));
            await fixture.Fence.EnsureApplyAllowedAsync(fixture.Docs.InstanceId, Batch(@"Archive\report.txt"), default);
            await fixture.Fence.EnsureApplyAllowedAsync(fixture.Foreign.InstanceId, Batch(@"Foreign\report.txt"), default);
            await fixture.Fence.EnsureApplyAllowedAsync(fixture.Foreign.InstanceId, new("foreign-cursor", new byte[] { 1 }, [], new byte[] { 2 }), default);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Fence.EnsureApplyAllowedAsync(
                fixture.Docs.InstanceId, Batch(@"Docs\new.txt"), default).AsTask());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Fence.EnsureApplyAllowedAsync(
                fixture.Docs.InstanceId, Batch(@"Archive\report.txt", @"Docs\new.txt"), default).AsTask());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Fence.EnsureApplyAllowedAsync(
                fixture.Docs.InstanceId, Batch(@"Docs\moved.txt", @"Archive\report.txt"), default).AsTask());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Fence.EnsureApplyAllowedAsync(
                fixture.Docs.InstanceId, new("shared-cursor", new byte[] { 1 }, [], new byte[] { 2 }), default).AsTask());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Fence.EnsureApplyAllowedAsync(
                fixture.Docs.InstanceId, Batch(@"Foreign\report.txt"), default).AsTask());
            Assert.AreEqual(fixture.Move, await fixture.Catalog.ReadPendingNamespaceMemberMoveAsync(fixture.Docs.RootId));
            Assert.AreEqual(fixture.Child, await fixture.Catalog.ReadNamespaceMemberChildAsync(fixture.Parent.MemberId, "new.txt"));
            if (owner == 0) await fixture.RestartAsync();
        }
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    public async Task RootLabelRecoveryAndMemberMoveAreMutuallyExclusiveWithinOneRoot()
    {
        await using var fixture = await MoveFixture.OpenAsync();
        var label = await fixture.Catalog.PrepareManagedRootRenameAsync(fixture.Docs.RootId, "My Files");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Catalog.PrepareNamespaceMemberMoveAsync(fixture.Move));
        Assert.IsNull(await fixture.Catalog.ReadNamespaceMemberMoveAsync(fixture.Move.OperationId));
        Assert.AreEqual(label, await fixture.Catalog.PrepareManagedRootRenameAsync(fixture.Docs.RootId, "My Files"));
        await fixture.Catalog.TransitionManagedRootRenameAsync(label.OperationId, MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled);
        await fixture.Catalog.PrepareNamespaceMemberMoveAsync(fixture.Move);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Catalog.PrepareManagedRootRenameAsync(fixture.Docs.RootId, "Other Name"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Catalog.RenameManagedRootAsync(fixture.Docs.RootId, "Other Name"));
        Assert.AreEqual("Docs", (await fixture.Catalog.ReadAdapterTopologyAsync()).Roots.Single(root => root.RootId == fixture.Docs.RootId).DirectoryName);
        var sibling = await fixture.Catalog.PrepareManagedRootRenameAsync(fixture.Archive.RootId, "Archive Label");
        CollectionAssert.AreEquivalent(new[] { fixture.Docs.RootId, fixture.Archive.RootId }, (await fixture.Catalog.ReadNamespaceRecoveryRootsAsync()).ToArray());
        Assert.IsFalse(await fixture.Fence.CanPollAsync(fixture.Archive, default));
        await fixture.Catalog.TransitionManagedRootRenameAsync(sibling.OperationId, MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled);
        CollectionAssert.AreEqual(new[] { fixture.Docs.RootId }, (await fixture.Catalog.ReadNamespaceRecoveryRootsAsync()).ToArray());
        Assert.IsTrue(await fixture.Fence.CanPollAsync(fixture.Archive, default));
        Assert.IsFalse(await fixture.Fence.CanPollAsync(fixture.Docs, default));
    }

    [TestMethod]
    public async Task CancelledFenceQueriesRetainPendingMoveAndItsSourceName()
    {
        await using var fixture = await MoveFixture.OpenAsync();
        await fixture.Catalog.PrepareNamespaceMemberMoveAsync(fixture.Move);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Fence.CanPollAsync(fixture.Docs, cancellation.Token).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Fence.EnsureApplyAllowedAsync(
            fixture.Docs.InstanceId, Batch(@"Docs\new.txt"), cancellation.Token).AsTask());
        Assert.AreEqual(fixture.Move, await fixture.Catalog.ReadPendingNamespaceMemberMoveAsync(fixture.Docs.RootId));
        Assert.AreEqual(fixture.Child, await fixture.Catalog.ReadNamespaceMemberChildAsync(fixture.Parent.MemberId, "new.txt"));
    }

    private static CloudRemoteChangeBatch Batch(string path, string? previous = null) => new("remote-batch", new byte[] { 1 },
        [new CloudRemoteChange("change", previous is null ? CloudRemoteChangeKind.FileUpsert : CloudRemoteChangeKind.Move,
            "remote-file", "v2", CloudItemKind.File, path, previousRelativePath: previous,
            length: 1, metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build())], new byte[] { 2 });

    private sealed class MoveFixture : IAsyncDisposable
    {
        private const string Owner = "S-1-5-21-100-200-300-1001", Role = "S-1-5-5-123-456";
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-move-fence", Guid.NewGuid().ToString("N"));
        private static readonly string Original = Dacl($"D:AI(A;OICI;FA;;;{Owner})");
        private static readonly string Protected = Dacl($"D:P(A;OICI;FRFW;;;{Owner})(A;OICI;FA;;;{Role})");
        public MirrorPulseStoragePaths Paths { get; private set; } = null!;
        public MirrorPulseProductCatalog Catalog { get; private set; } = null!;
        public RootRegistration Docs { get; private set; } = null!;
        public RootRegistration Archive { get; private set; } = null!;
        public RootRegistration Foreign { get; private set; } = null!;
        public MirrorPulseRootRouter Router { get; private set; } = null!;
        public MirrorPulseRemoteNamespaceFence Fence => new(Router, Catalog);
        public MirrorPulseNamespaceMemberRegistration Parent { get; private set; } = null!;
        public MirrorPulseNamespaceMemberRegistration Child { get; private set; } = null!;
        public MirrorPulseNamespaceMemberMoveIntent Move { get; private set; } = null!;

        public static async Task<MoveFixture> OpenAsync()
        {
            var fixture = new MoveFixture();
            try
            {
                fixture.Paths = new(Path.Combine(fixture._directory, "sync"), Path.Combine(fixture._directory, "data"));
                fixture.Catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
                var adapter = AdapterId.Parse("example.move-fence");
                var manifest = new AdapterManifest(1, adapter, "Example", "1.0.0", new(1, 1),
                    new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
                    new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0",
                    [new("docs", "Docs", "Docs", false), new("archive", "Archive", "Archive", false)]);
                var installed = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(fixture._directory, "installed"),
                    new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
                var configured = new AdapterInstance(adapter, installed.InstallId, InstanceId.New(), "Example",
                    new Dictionary<string, string>(), [], Path.Combine(fixture._directory, "files"), Path.Combine(fixture._directory, "transfers"),
                    true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
                RootRegistration Registration(string key, string label) => AdapterRootRegistrationMapper.Map(adapter,
                    configured.InstanceId, new(key, label, label, false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
                fixture.Docs = Registration("docs", "Docs");
                fixture.Archive = Registration("archive", "Archive");
                await fixture.Catalog.SaveAdapterTopologyAsync(new([installed], [configured], [fixture.Docs, fixture.Archive]));
                fixture.Foreign = AdapterRootRegistrationMapper.Map(adapter, InstanceId.New(), new("foreign", "Foreign", "Foreign", false),
                    RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
                fixture.Router = new(fixture.Paths.SyncRootPath, [fixture.Docs, fixture.Archive, fixture.Foreign]);
                var binding = new MirrorPulseLocalFileBinding(7, Guid.NewGuid(), Guid.NewGuid());
                var baseline = new MirrorPulseNamespacePermissionBaseline(Guid.NewGuid(), null,
                    binding with { LocalFileId = binding.SyncRootFileId }, string.Empty, true, Owner, Original, DateTimeOffset.UtcNow);
                var anchor = await fixture.RegisterAsync(baseline, null);
                var parent = await fixture.RegisterAsync(baseline with
                {
                    EvidenceId = Guid.NewGuid(),
                    RootId = fixture.Docs.RootId,
                    LocalObject = binding,
                    RelativePath = "Docs"
                }, anchor.Member.MemberId);
                fixture.Parent = parent.Member;
                Guid itemId = Guid.NewGuid();
                var child = await fixture.RegisterAsync(baseline with
                {
                    EvidenceId = Guid.NewGuid(),
                    RootId = fixture.Docs.RootId,
                    LocalObject = binding with { LocalFileId = Guid.NewGuid() },
                    RelativePath = "Docs/new.txt",
                    IsDirectory = false
                }, parent.Member.MemberId, itemId);
                fixture.Child = child.Member;
                fixture.Move = new(1, Guid.NewGuid(), child.Member.MemberId, child.Intent.OperationId, fixture.Docs.RootId,
                    child.Member.LocalObject, itemId, "local:first", Owner, Role,
                    new(parent.Member.MemberId, parent.Member.LocalObject, parent.Intent.OperationId, child.Member.RelativePath),
                    new(parent.Member.MemberId, parent.Member.LocalObject, parent.Intent.OperationId, "Docs/moved.txt"), DateTimeOffset.UtcNow);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private async Task<(MirrorPulseNamespaceMemberRegistration Member, MirrorPulseNamespacePermissionIntent Intent)> RegisterAsync(
            MirrorPulseNamespacePermissionBaseline original, Guid? parent, Guid? itemId = null)
        {
            var intent = new MirrorPulseNamespacePermissionIntent(Guid.NewGuid(), original.EvidenceId, original.LocalObject, original.RootId,
                original.RelativePath, MirrorPulseNamespacePermissionChangeKind.Protect, Role, Original, Protected, DateTimeOffset.UtcNow);
            await Catalog.PrepareNamespacePermissionChangeAsync(original, intent);
            if (itemId is { } item) await Catalog.PrepareNamespacePermissionLocalIdentityAsync(new(1, original.EvidenceId,
                intent.OperationId, original.LocalObject, item, "local:first", DateTimeOffset.UtcNow));
            await Catalog.RecordNamespacePermissionApplicationAsync(intent.OperationId, original.LocalObject, DateTimeOffset.UtcNow);
            await Catalog.VerifyNamespacePermissionChangeAsync(intent.OperationId, new(original.LocalObject, Owner, Protected, DateTimeOffset.UtcNow));
            var member = new MirrorPulseNamespaceMemberRegistration(1, Guid.NewGuid(), parent, MirrorPulseNamespaceMemberOrigin.CapturedOriginal,
                original.EvidenceId, intent.OperationId, original.RootId, original.RelativePath, original.LocalObject, original.IsDirectory, Owner, DateTimeOffset.UtcNow);
            return (await Catalog.RegisterNamespaceMemberAsync(member), intent);
        }
        public async Task RestartAsync() { await Catalog.DisposeAsync(); Catalog = await MirrorPulseProductCatalog.OpenAsync(Paths); }
        private static string Dacl(string value) => new RawSecurityDescriptor(value).GetSddlForm(AccessControlSections.Access);
        public async ValueTask DisposeAsync()
        {
            if (Catalog is not null) await Catalog.DisposeAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
