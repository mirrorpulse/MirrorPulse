using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeConfirmedUploadIdentityRemainsMovableAcrossRestart()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.accepted-identity"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var role = new MirrorPulseNamespaceExecutionSession();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var source = new ProtectedPermissionNoSourceProvider();
        var originals = new List<MirrorPulseNamespacePermissionPreparation>();
        var members = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
        MirrorPulseNamespaceMemberRegistration? member = null;
        MirrorPulseNamespaceMemberMoveCompletion? firstMove = null;
        MirrorPulseNamespaceIdentityAcceptance? firstAcceptance = null;
        byte[] bytes = [1, 3, 5, 7, 9];
        string currentPath = "Docs/original.bin";
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [4, 5, 6]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            for (int owner = 0; owner < 2; owner++)
            {
                await using var actor = new MirrorPulseNamespaceActor();
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state).WithContentProvider(source).Build();
                await role.RunNamespaceOperationAsync(async () => await fileSystem.StartAsync(deadline.Token));
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                if (owner == 0)
                {
                    Directory.CreateDirectory(fileSystem.GetDirectory("Docs").FullPath);
                    await File.WriteAllBytesAsync(fileSystem.GetFile(currentPath).FullPath, bytes, deadline.Token);
                    await PrepareAcceptedIdentityOriginalsAsync(fileSystem, catalog, router, role, caller.User!, originals, deadline.Token);
                    await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, deadline.Token);
                }
                // Hold the real root and ancestors throughout all registered namespace work.
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(fileSystem.Root, router, deadline.Token);
                if (owner == 0)
                {
                    await RegisterNativeOriginalMembersAsync(fileSystem, catalog, router, role, originals, members, actor, deadline.Token);
                    member = members.Values.Single(value => !value.IsDirectory);
                }
                Assert.IsNotNull(member);
                Assert.AreEqual(member, await catalog.ReadNamespaceMemberAsync(member.MemberId, deadline.Token));
                var file = fileSystem.GetFile(currentPath);
                if (owner == 1)
                {
                    bytes = [2, 4, 6, 8, 10, 12];
                    await File.WriteAllBytesAsync(file.FullPath, bytes, deadline.Token);
                }
                var before = await file.InspectAsync(deadline.Token);
                var confirmation = new MirrorPulseNamespaceContentConfirmation(catalog, router, actor, role);
                var binding = await confirmation.CaptureUploadBindingAsync(file, before, deadline.Token);
                Assert.AreEqual(member.LocalObject, binding.LocalObject);
                if (owner == 1)
                {
                    Assert.IsFalse(before.IsPlaceholder, "A real ordinary overwrite exercises public reconversion.");
                    Assert.AreEqual(MirrorPulseContentPreparation.ConvertRegularFile, binding.Preparation);
                    Assert.IsNotNull(binding.NamespaceIdentity);
                    Assert.AreEqual(member.MemberId, binding.NamespaceIdentity.MemberId);
                    Assert.AreEqual(firstAcceptance!.Proof.OperationId, binding.NamespaceIdentity.AcceptanceOperationId);
                }
                var intent = new MirrorPulseMutationIntent(Guid.NewGuid(), root.InstanceId, root.UniquenessKey,
                    MirrorPulseWorkerChangeKind.ContentUpdate, currentPath[5..], null, false, before.RemoteRevision,
                    bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), MirrorPulseMutationOrigin.Journal, binding);
                await catalog.PrepareMutationAsync(intent, deadline.Token);
                await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing,
                    cancellationToken: deadline.Token);
                // Remote acceptance is a fixture premise. The tested confirmation, native
                // identity replacement, official projection and production moves are real.
                string revision = "accepted-" + owner;
                await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted,
                    revision, deadline.Token);
                var acceptedIdentity = router.CreateFileIdentity(root.InstanceId, root.UniquenessKey, "remote-object-" + owner, revision);
                var proof = new MirrorPulseContentAcceptanceProof(intent.OperationId, binding, acceptedIdentity.ItemId,
                    acceptedIdentity.RemoteId, revision, bytes.Length, intent.ContentSha256!);
                await catalog.SaveContentAcceptanceProofAsync(proof, deadline.Token);
                if (owner == 0)
                    await AssertConfirmationRejectsForeignProtectionAsync(fileSystem, catalog, router, role, confirmation, file, proof, deadline.Token);
                var receipt = await confirmation.ConfirmAsync(file, proof, deadline.Token);
                for (int retry = 1; retry < 3 && receipt.Outcome is MirrorPulseContentConfirmationOutcome.Busy or MirrorPulseContentConfirmationOutcome.ProtectionLost; retry++)
                {
                    await Task.Delay(50, deadline.Token);
                    receipt = await confirmation.ConfirmAsync(file, proof, deadline.Token);
                }
                TestContext.WriteLine($"AcceptedIdentityConfirmation: owner={owner}; outcome={receipt.Outcome}; nativeStage={receipt.NativeStage}; verified={receipt.NativeConfirmationVerified}; projected={receipt.DurableProjectionCommitted}; bytesVerified={receipt.BytesVerified}; proofLength={proof.Length}; nativeHResult={receipt.NativeErrorHResult:X8}.");
                Assert.IsTrue(receipt.MayAcknowledge);
                var retained = await catalog.ReadNamespaceIdentityAcceptanceAsync(intent.OperationId, deadline.Token);
                Assert.IsNotNull(retained); Assert.AreEqual(proof, retained.Proof);
                firstAcceptance ??= retained;
                if (owner == 1) Assert.AreEqual(firstAcceptance.Proof.OperationId, retained.PreviousAcceptanceOperationId);
                await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.RemoteAccepted, MirrorPulseMutationState.Acknowledged,
                    cancellationToken: deadline.Token);
                var actual = await file.InspectAsync(deadline.Token);
                Assert.AreEqual(before.LocalBinding, actual.LocalBinding); Assert.AreEqual(proof.AcceptedItemId, actual.ItemId);
                Assert.AreEqual(proof.RemoteId, actual.RemoteId); CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(file.FullPath, deadline.Token));
                var inspector = new MirrorPulseNamespaceMemberInspector(fileSystem, catalog, router, role, actor);
                Assert.AreEqual(member.MemberId, (await inspector.InspectAsync(currentPath, false, deadline.Token)).MemberId);
                var move = new MirrorPulseNamespaceMemberMoveCoordinator(fileSystem, state, catalog, router, role, actor);
                string name = owner == 0 ? "moved.bin" : "final.bin";
                var prepared = await move.PrepareAsync(Guid.NewGuid(), member.MemberId, member.ParentMemberId!.Value, name, deadline.Token);
                Assert.AreEqual(intent.OperationId, prepared.IdentityAcceptanceOperationId);
                if (owner == 1) Assert.AreEqual(firstMove!.OperationId, prepared.PreviousMoveOperationId);
                var completed = await move.ExecuteAsync(prepared.OperationId, deadline.Token); firstMove ??= completed;
                currentPath = "Docs/" + name;
                Assert.AreEqual(member, await catalog.ReadNamespaceMemberAsync(member.MemberId, deadline.Token));
                Assert.AreEqual(prepared.OperationId, (await catalog.ReadNamespaceMemberLocationAsync(member.MemberId, deadline.Token)).CompletedMoveOperationId);
                Assert.AreEqual(completed, await move.ExecuteAsync(prepared.OperationId, deadline.Token));
                if (owner == 1)
                {
                    Assert.AreEqual(firstMove, await move.ExecuteAsync(firstMove.OperationId, deadline.Token));
                    Assert.IsFalse((await fileSystem.GetFile("Docs/moved.bin").InspectAsync(deadline.Token)).Exists);
                    Assert.AreEqual(firstAcceptance, await catalog.ReadNamespaceIdentityAcceptanceAsync(firstAcceptance.Proof.OperationId, deadline.Token));
                }
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(fileSystem.GetFile(currentPath).FullPath, deadline.Token));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(fileSystem.GetFile(currentPath).FullPath), deadline.Token));
            }
            Assert.AreEqual(0, source.Reads);
            TestContext.WriteLine("AcceptedIdentityMove: publicConfirmation=True; ordinaryEditReconverted=True; uploadTimeMemberIdentity=True; currentProtectionBeforeMark=True; foreignChildAndParentAclRejected=True; fullBinding=True; acceptedItemIdChanged=True; originalMembershipUnchanged=True; officialProjection=True; productionMovePreparation=True; productionMoveCoordinator=True; owners=2; historicalReplayDoesNotRewind=True; strictDeleteDenied=True; fixtureRemoteAcceptance=True; actualWorkerUpload=False; hostIntegrated=False.");
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreSourceContextFixtureForCleanup(directory, paths.SyncRootPath, caller.User!);
            DeleteFixture(directory);
        }
    }

    private static async Task AssertConfirmationRejectsForeignProtectionAsync(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
        MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession role, MirrorPulseNamespaceContentConfirmation confirmation, CloudFile file,
        MirrorPulseContentAcceptanceProof proof, CancellationToken token)
    {
        byte[] before = (await file.InspectAsync(token)).PlaceholderIdentity.ToArray();
        foreach (CloudItem subject in new CloudItem[] { file, fileSystem.GetDirectory("Docs") })
        {
            bool directory = subject.Kind == CloudItemKind.Directory;
            await role.RunNamespaceOperationAsync(async () =>
            {
                await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(subject, router, token);
                var original = await guard.InspectAsync(token);
                FileSystemSecurity foreign = directory ? new DirectorySecurity() : new FileSecurity();
                foreign.SetSecurityDescriptorSddlForm(original.Dacl, AccessControlSections.Access);
                foreign.AddAccessRule(new FileSystemAccessRule(role.OwnerSid, FileSystemRights.Delete, AccessControlType.Allow));
                string descriptor = foreign.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                Assert.AreNotEqual(original.Dacl, descriptor);
                try
                {
                    await guard.ApplyDaclAsync(descriptor, token);
                    await Assert.ThrowsAsync<InvalidDataException>(() => confirmation.ConfirmAsync(file, proof, token).AsTask());
                    Assert.IsNull(await catalog.ReadContentConfirmationReceiptAsync(proof.OperationId, token));
                    var observed = await file.InspectAsync(token);
                    CollectionAssert.AreEqual(before, observed.PlaceholderIdentity.ToArray());
                    Assert.AreEqual(CloudSynchronizationState.NotInSync, observed.SynchronizationState);
                }
                finally { await guard.ApplyDaclAsync(original.Dacl, CancellationToken.None); }
                Assert.AreEqual(original.Dacl, (await guard.InspectAsync(token)).Dacl, "The negative fixture must restore the retained descriptor before positive confirmation.");
            });
        }
    }

    private static async Task PrepareAcceptedIdentityOriginalsAsync(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
        MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession role, SecurityIdentifier owner,
        List<MirrorPulseNamespacePermissionPreparation> originals, CancellationToken token)
    {
        foreach (CloudItem item in new CloudItem[] { fileSystem.Root, fileSystem.GetDirectory("Docs"), fileSystem.GetFile("Docs/original.bin") })
        {
            SetProtectedFixtureUserOwner(item, owner);
            await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, token);
            var observed = await lease.InspectAsync(token);
            var baseline = new MirrorPulseNamespacePermissionBaseline(Guid.NewGuid(), observed.RootId, observed.LocalObject,
                observed.RelativePath, observed.IsDirectory, observed.OwnerSid, observed.Dacl, observed.ObservedAt);
            var protection = new MirrorPulseNamespacePermissionIntent(Guid.NewGuid(), baseline.EvidenceId, baseline.LocalObject,
                baseline.RootId, baseline.RelativePath, MirrorPulseNamespacePermissionChangeKind.Protect, role.RoleSid.Value,
                baseline.OriginalDacl, MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(role, baseline.IsDirectory), DateTimeOffset.UtcNow);
            originals.Add(new(baseline, protection));
        }
        await catalog.PrepareNamespacePermissionChangesAsync(originals, token);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        await role.RunNamespaceOperationAsync(async () =>
        {
            foreach (var original in originals.AsEnumerable().Reverse())
            {
                var localIdentity = original.Baseline.IsDirectory ? null : router.CreateFileIdentity(router.Registrations.Single().InstanceId, "docs", "local:original");
                var applied = await coordinator.ApplyProtectedAsync(original.Intent.OperationId,
                    NativeOriginalMemberItem(fileSystem, original.Baseline), router, localIdentity, token);
                Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, applied.Permission!.Outcome);
            }
        });
    }

    private static async Task SaveAcceptedIdentityFixtureTopologyAsync(MirrorPulseProductCatalog catalog, RootRegistration root, CancellationToken token)
    {
        var manifest = new AdapterManifest(1, root.AdapterId, "Example", "1.0.0", new(1, 1),
            new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
            new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0", [new("docs", "Docs", "Docs", false)]);
        var installation = new InstalledAdapter(manifest, InstallId.New(), Path.GetTempPath(), new(new string('A', 64)),
            AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        var instance = new AdapterInstance(root.AdapterId, installation.InstallId, root.InstanceId, "Storage",
            new Dictionary<string, string>(), [], Path.GetTempPath(), Path.GetTempPath(), true, AdapterLifecycleState.Configured, null, DateTimeOffset.UtcNow);
        await catalog.SaveAdapterTopologyAsync(new([installation], [instance], [root]), token);
    }
}
