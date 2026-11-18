using System.Security.Principal;
using CfSharp;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod, DoNotParallelize, TestCategory("NativeCloudFiles")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeActiveReplacementRecoversPreparedOriginalAcrossHostRoles(bool interruptRecovery)
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.active-replacement"), InstanceId.New(),
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        Guid operationId = Guid.NewGuid();
        string inputPath = Path.Combine(directory, "input.bin");
        string localPath = Path.Combine(paths.SyncRootPath, "Docs", "nested", "replacement.bin");
        byte[] original = "original bytes across a replacement restart"u8.ToArray();
        byte[] replacement = "staged replacement is never reopened from its original path"u8.ToArray();
        MirrorPulseNamespaceReplacementExecution? originalPreparation = null;
        MirrorPulseNamespaceMemberInspection? originalMember = null;
        string? oldRole = null;
        string? recoveryRole = null;
        Guid? originalHandoff = null;
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            await File.WriteAllBytesAsync(inputPath, replacement, timeout.Token);
            for (int owner = 0; owner < (interruptRecovery ? 3 : 2); owner++)
            {
                await using var execution = new MirrorPulseNamespaceExecutionSession();
                await using var actor = new MirrorPulseNamespaceActor();
                await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                var worker = new MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker(execution);
                var transport = new MirrorPulseNormalUserWorkerTransport(execution, worker, worker, worker, worker, worker);
                var factory = new CfSharpMirrorPulseCloudRuntimeFactory(
                    provider: new MirrorPulseDemandProvider(router, transport, new MirrorPulseAdapterDirectoryPageSource(transport), work),
                    uploads: transport, stats: transport, mutations: transport, mayDispatch: _ => false, router: router,
                    catalog: catalog, namespaceExecution: execution, namespaceActor: actor, scheduler: scheduler,
                    namespaceWork: work, initializeNamespaceOnStart: true);
                await using var runtime = factory.Create(paths);
                await runtime.StartAsync(timeout.Token);
                if (owner == 0)
                {
                    Assert.IsTrue(work.IsAdmitted(root.RootId));
                    oldRole = execution.RoleSid.Value;
                    var rootMember = await runtime.InspectNamespaceMemberAsync("Docs", true, timeout.Token);
                    var nested = await runtime.CreateNamespaceMemberAsync(Guid.NewGuid(), rootMember.MemberId, "nested", true, timeout.Token);
                    var file = await runtime.CreateNamespaceMemberAsync(Guid.NewGuid(), nested.MemberId, "replacement.bin", false, timeout.Token);
                    await using (var writer = new FileStream(localPath, FileMode.Open, FileAccess.Write, FileShare.Read))
                    {
                        await writer.WriteAsync(original, timeout.Token);
                        writer.Flush(true);
                    }
                    originalMember = await runtime.InspectNamespaceMemberAsync("Docs/nested/replacement.bin", false, timeout.Token);
                    Assert.IsNotNull(originalMember.NativeItemId);
                    originalPreparation = await runtime.PrepareNamespaceReplacementAsync(operationId, file.MemberId, inputPath, timeout.Token);
                    File.Delete(inputPath);
                    Assert.IsNull(originalPreparation.Backup);
                    Assert.IsNull(originalPreparation.WriteStart);
                    continue;
                }

                Assert.AreNotEqual(oldRole, execution.RoleSid.Value);
                Assert.IsFalse(File.Exists(inputPath));
                if (owner == 1)
                {
                    recoveryRole = execution.RoleSid.Value;
                    Assert.IsFalse(work.IsAdmitted(root.RootId));
                    var pending = await runtime.ReadNamespaceReplacementStatusAsync(operationId, timeout.Token);
                    Assert.AreEqual("Prepared", pending.Phase);
                    Assert.IsTrue(pending.RequiresRecovery);
                    var facts = await catalog.ReadNamespaceReplacementExecutionAsync(operationId, timeout.Token);
                    Assert.AreEqual(originalPreparation!.Action, facts!.Action);
                    Assert.AreEqual(originalPreparation.Start, facts.Start);
                    if (interruptRecovery)
                    {
                        await InterruptAsync("namespace_replacement_role_handoffs", "replacement-handoff-rollback");
                        facts = await catalog.ReadNamespaceReplacementExecutionAsync(operationId, timeout.Token);
                        Assert.IsNotNull(facts);
                        originalHandoff = facts.RoleActions.Single().RecoveryId;
                        Assert.IsNull(facts.RoleHandoff);
                        Assert.IsNull(facts.WriteStart);
                        Assert.IsFalse(work.IsAdmitted(root.RootId));
                        await InterruptAsync("namespace_initialization_completions", "replacement-audit-rollback");
                        Assert.IsNotNull(await catalog.ReadNamespaceReplacementCompletionAsync(operationId, timeout.Token));
                        Assert.IsFalse(work.IsAdmitted(root.RootId));
                        Assert.HasCount(1, await catalog.ReadPendingNamespaceInitializationsAsync(timeout.Token));
                        continue;
                    }
                }
                else
                {
                    Assert.AreNotEqual(recoveryRole, execution.RoleSid.Value);
                    Assert.IsTrue(work.IsAdmitted(root.RootId));
                    Assert.IsEmpty(await catalog.ReadPendingNamespaceInitializationsAsync(timeout.Token));
                }
                var completed = await runtime.ExecuteNamespaceReplacementAsync(operationId, timeout.Token);
                Assert.AreEqual(originalMember!.MemberId, completed.MemberId);
                var after = await runtime.InspectNamespaceMemberAsync("Docs/nested/replacement.bin", false, timeout.Token);
                Assert.AreEqual(originalMember.MemberId, after.MemberId);
                Assert.AreEqual(originalMember.NativeItemId, after.NativeItemId);
                CollectionAssert.AreEqual(replacement, await File.ReadAllBytesAsync(localPath, timeout.Token));
                var settled = await catalog.ReadNamespaceReplacementExecutionAsync(operationId, timeout.Token);
                Assert.AreEqual(originalPreparation!.Action, settled!.Action);
                Assert.AreEqual(originalPreparation.Start, settled.Start);
                var handoff = settled.RoleActions.Single();
                Assert.AreEqual(2, handoff.Version);
                Assert.AreEqual(handoff.RecoveryId, settled.RoleHandoff!.RecoveryId);
                Assert.AreEqual(recoveryRole, handoff.Parent.Context.Protection.RoleSid);
                if (originalHandoff is { } retainedHandoff) Assert.AreEqual(retainedHandoff, handoff.RecoveryId);
                await using var backup = await new MirrorPulseLocalReplacementBackupStore(paths)
                    .OpenLeaseAsync(operationId, settled.Backup!.Content, timeout.Token);
                using var backupBytes = new MemoryStream();
                await backup.Data.CopyToAsync(backupBytes, timeout.Token);
                CollectionAssert.AreEqual(original, backupBytes.ToArray());
                Assert.IsTrue(work.IsAdmitted(root.RootId));
                Assert.IsEmpty(await catalog.ReadNamespacePendingOperationsAsync(root.RootId, timeout.Token));
                Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token));

                async Task InterruptAsync(string table, string marker)
                {
                    await ExecuteTransferPublicationFixtureSqlAsync(paths,
                        $"CREATE TRIGGER interrupt_replacement BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'{marker}'); END;",
                        timeout.Token);
                    try
                    {
                        var failure = await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
                            await runtime.ExecuteNamespaceReplacementAsync(operationId, timeout.Token));
                        StringAssert.Contains(failure.Message, marker);
                    }
                    finally
                    {
                        await ExecuteTransferPublicationFixtureSqlAsync(paths, "DROP TRIGGER interrupt_replacement;", timeout.Token);
                    }
                }
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            RestoreAutomaticStartupFixtureForCleanup(directory, paths.SyncRootPath, user.User!);
            DeleteFixture(directory);
        }
    }
}
