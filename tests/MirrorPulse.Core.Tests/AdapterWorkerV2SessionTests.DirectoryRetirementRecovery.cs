using Microsoft.Data.Sqlite;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class AdapterWorkerV2SessionTests
{
    [TestMethod]
    public async Task RealWorkerRetirementRecoversTheSameReceiptAfterBothProcessesReopen()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        InstanceId instance = InstanceId.New();
        var topology = CreateFixtureTopology(directory, instance);
        var plan = RetirementPlan(instance, "left");
        Guid operation = plan.Steps[^1].Operation.OperationId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        try
        {
            string cache = topology.Instances.Single().TransferCacheDirectory;
            Directory.CreateDirectory(cache);
            await File.WriteAllTextAsync(Path.Combine(cache, ".mp-startup-fixture-mode"), "LostOriginalDirectoryReply", token);
            AdapterDirectoryRetirementRequest request;
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, cancellationToken: token))
            {
                await catalog.SaveAdapterTopologyAsync(topology, token);
                request = await PrepareRetirementAsync(catalog, plan, token: token);
                await using var supervisor = new AdapterInstanceProcessSupervisor(catalog, new WindowsCredentialManagerStore());
                await supervisor.StartAsync(topology);
                while (!supervisor.IsConnected(instance)) await Task.Delay(25, token);
                await Assert.ThrowsExactlyAsync<IOException>(() => supervisor.RetireOriginalDirectoryAsync(request, token).AsTask());
                var pending = await catalog.ReadOriginalDirectoryRetirementAsync(operation, token);
                Assert.IsNotNull(pending);
                Assert.AreEqual(request.RequestFingerprint, pending.Request.RequestFingerprint);
                Assert.IsNull(pending.Receipt);
                Assert.AreEqual(MirrorPulseMutationState.Executing, (await catalog.ReadMutationAsync(operation, token))!.State);
            }

            string workerState = Path.Combine(paths.DataRootPath, "state", "workers", instance.Value.ToString("N"));
            byte[] workerOriginal = await File.ReadAllBytesAsync(Path.Combine(workerState, ".mp-original-directory-fixture-receipt"), token);
            var workerReceipt = AdapterProtocolJson.Decode<AdapterOwnedChildEffectReceipt>(workerOriginal);
            // Clear the disposable transfer cache after both processes stop. A
            // committed backend receipt belongs to the durable instance grant.
            foreach (string entry in Directory.EnumerateFiles(cache)) File.Delete(entry);
            await File.WriteAllTextAsync(Path.Combine(cache, ".mp-startup-fixture-mode"), "LostOriginalDirectoryReply", token);
            // A fresh Worker process reconnects to the reopened catalog and supervisor.
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, cancellationToken: token))
            {
                await using var supervisor = new AdapterInstanceProcessSupervisor(catalog, new WindowsCredentialManagerStore());
                var changed = request with { Retirement = request.Retirement with { SealedManifestFingerprint = new string('B', 64) } };
                changed = changed with { RequestFingerprint = changed.CalculateFingerprint() };
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => supervisor.RetireOriginalDirectoryAsync(changed, token).AsTask());
                await supervisor.StartAsync(topology);
                while (!supervisor.IsConnected(instance)) await Task.Delay(25, token);
                var pending = await catalog.ReadOriginalDirectoryRetirementAsync(operation, token);
                Assert.IsNotNull(pending);
                var replay = await supervisor.RetireOriginalDirectoryAsync(pending.Request, token);
                Assert.AreEqual(workerReceipt.ReceiptId, replay.ReceiptId);
                CollectionAssert.AreEqual(workerOriginal, AdapterProtocolJson.Encode(replay));
            }

            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths, cancellationToken: token);
            await using var offline = new AdapterInstanceProcessSupervisor(reopened, new WindowsCredentialManagerStore());
            var accepted = await offline.RetireOriginalDirectoryAsync(request, token);
            Assert.IsFalse(offline.IsConnected(instance));
            CollectionAssert.AreEqual(workerOriginal, AdapterProtocolJson.Encode(accepted));
            Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, (await reopened.ReadMutationAsync(operation, token))!.State);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DirectoryRetirementReceiptAndMutationStateCommitTogetherAndRequireTypedRecovery(bool moving)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var plan = RetirementPlan(InstanceId.New(), "left", moving ? "right" : null);
        Guid operation = plan.Steps[^1].Operation.OperationId;
        try
        {
            AdapterOwnedChildEffectReceipt receipt;
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SealOriginalDirectoryPlanAsync(plan));
                var request = await PrepareRetirementAsync(catalog, plan);
                receipt = RetirementReceipt(request, "after-retirement");
                await catalog.StartOriginalDirectoryRetirementAsync(request, default);
                await RunRetirementCatalogSqlAsync(paths, """
                    CREATE TRIGGER interrupt_retirement BEFORE UPDATE ON mutation_intents
                    BEGIN SELECT RAISE(ABORT,'interrupted directory retirement'); END;
                    """);
                await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.AcceptOriginalDirectoryRetirementAsync(operation, receipt, default));
            }

            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            var pending = await reopened.ReadOriginalDirectoryRetirementAsync(operation);
            Assert.IsNotNull(pending);
            Assert.IsNull(pending.Receipt);
            Assert.AreEqual(MirrorPulseMutationState.Executing, (await reopened.ReadMutationAsync(operation))!.State);
            await RunRetirementCatalogSqlAsync(paths, "DROP TRIGGER interrupt_retirement;");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                reopened.AcceptOriginalDirectoryRetirementAsync(operation, receipt with { OriginalChildGeneration = "successor" }, default));
            Assert.AreEqual(receipt.ReceiptId, (await reopened.AcceptOriginalDirectoryRetirementAsync(operation, receipt, default)).ReceiptId);
            Assert.AreEqual(receipt.ReceiptId, (await reopened.AcceptOriginalDirectoryRetirementAsync(operation, receipt, default)).ReceiptId);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                reopened.AcceptOriginalDirectoryRetirementAsync(operation, receipt with { ReceiptId = Guid.NewGuid() }, default));

            var accepted = await reopened.ReadMutationAsync(operation);
            Assert.IsNotNull(accepted);
            Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, accepted.State);
            Assert.AreEqual(receipt.AcceptedRevision, accepted.AcceptedRevision);
            var ordinary = new MirrorPulseMutationExecutor(reopened);
            await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() => ordinary.ReconcileAsync(accepted,
                (_, _) => throw new AssertFailedException("Typed retirement must not use revision-only readback."),
                (_, _) => throw new AssertFailedException("Typed retirement must not use ordinary journal ACK."), default).AsTask());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.TransitionMutationAsync(operation,
                MirrorPulseMutationState.RemoteAccepted, MirrorPulseMutationState.Acknowledged));
            await RunRetirementCatalogSqlAsync(paths, """
                UPDATE mutation_intents SET payload=CAST(json_set(CAST(payload AS TEXT),'$.ContentLength',0) AS BLOB);
                """);
            var changedOwner = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadOriginalDirectoryRetirementAsync(operation));
            Assert.AreEqual("The original directory retirement owner changed.", changedOwner.Message);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task DirectoryRetirementLedgerUpgradesFrom104AndRefusesMissingCurrentEvidence()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var plan = RetirementPlan(InstanceId.New(), "left");
        Guid operation = plan.Steps[^1].Operation.OperationId;
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
                await PrepareDirectoryPlanOwnersAsync(catalog, plan);
            await RunRetirementCatalogSqlAsync(paths, "DROP TABLE original_directory_retirements; PRAGMA user_version=104;");
            await using (var migrated = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                Assert.IsNull(await migrated.ReadOriginalDirectoryRetirementAsync(operation));
                var request = await PrepareRetirementAsync(migrated, plan, prepareOwners: false);
                await migrated.StartOriginalDirectoryRetirementAsync(request, default);
            }

            await RunRetirementCatalogSqlAsync(paths, "PRAGMA user_version=104;");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseProductCatalog.OpenAsync(paths));
            await RunRetirementCatalogSqlAsync(paths, "PRAGMA user_version=106; DROP TABLE original_directory_retirements;");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseProductCatalog.OpenAsync(paths));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task SealedRecursivePlanDerivesFinalRequestsFromTheAcceptedOriginalPrefixAcrossReopen()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var seed = RetirementPlan(InstanceId.New(), "left");
        var root = seed.Steps[^1];
        var rootWitness = root.Directory!;
        var nestedWitness = rootWitness with
        {
            WitnessId = Guid.NewGuid(),
            Directory = new("left", "tree/nested"),
            Generation = "nested-generation",
            OriginalRevision = "nested-revision",
            ParentGeneration = rootWitness.Generation,
        };
        var nested = new MirrorPulseOriginalDirectoryStep("Delete",
            new(Guid.NewGuid(), "left", "tree/nested", Preconditions: new("nested-revision"), IsDirectory: true),
            nestedWitness.Generation, nestedWitness, [rootWitness]);
        var first = seed.Steps[0] with
        {
            Operation = seed.Steps[0].Operation with { Path = "tree/nested/leaf", OperationId = Guid.NewGuid() },
            Parents = [nestedWitness],
        };
        var sibling = seed.Steps[0];
        var plan = seed with { Steps = [first, nested, sibling, root] };
        Guid rootId = root.Operation.OperationId;
        try
        {
            AdapterOwnedChildEffectReceipt nestedReceipt;
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await PrepareDirectoryPlanOwnersAsync(catalog, plan);
                var reversed = plan with { Steps = [nested, first, sibling, root] };
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.SealOriginalDirectoryPlanAsync(reversed));
                await catalog.SealOriginalDirectoryPlanAsync(plan);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadOriginalDirectoryPlanRetirementAsync(rootId));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    catalog.TransitionMutationAsync(first.Operation.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing));

                var leaf = await catalog.StartOriginalDirectoryLeafAsync(first.Operation.OperationId);
                var firstReceipt = LeafReceipt(plan, leaf, "nested-after-leaf");
                await catalog.AcceptOriginalDirectoryLeafAsync(first.Operation.OperationId, firstReceipt);
                var nestedRequest = await catalog.ReadOriginalDirectoryPlanRetirementAsync(nested.Operation.OperationId);
                Assert.AreEqual("nested-after-leaf", nestedRequest.Operation.Preconditions!.ExpectedRevision);
                Assert.AreEqual(firstReceipt.ReceiptId, nestedRequest.Retirement.AcceptedChildren.Single().ReceiptId);
                await catalog.StartOriginalDirectoryRetirementAsync(nestedRequest, default);
                nestedReceipt = RetirementReceipt(nestedRequest, "tree-after-nested");
                await catalog.AcceptOriginalDirectoryRetirementAsync(nested.Operation.OperationId, nestedReceipt, default);
            }

            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            await reopened.SealOriginalDirectoryPlanAsync(plan);
            var resumed = await reopened.StartOriginalDirectoryLeafAsync(sibling.Operation.OperationId);
            Assert.AreEqual(nestedReceipt.ReceiptId, resumed.ParentPredecessors.Single().Receipt.ReceiptId);
            var successor = LeafReceipt(plan, resumed, "tree-after-sibling");
            var forged = successor with
            {
                Parents = [new(rootWitness, rootWitness.OriginalRevision, "tree-after-sibling", null, "AQID")],
            };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                reopened.AcceptOriginalDirectoryLeafAsync(sibling.Operation.OperationId, forged));
            await reopened.AcceptOriginalDirectoryLeafAsync(sibling.Operation.OperationId, successor);
            var final = await reopened.ReadOriginalDirectoryPlanRetirementAsync(rootId);
            CollectionAssert.AreEqual(new[] { nestedReceipt.ReceiptId, successor.ReceiptId },
                final.Retirement.AcceptedChildren.Select(receipt => receipt.ReceiptId).ToArray());
            Assert.AreEqual("tree-after-sibling", final.Operation.Preconditions!.ExpectedRevision);

            // A self-consistent SDK request can still omit real sealed dependencies.
            var empty = final with
            {
                Operation = root.Operation,
                Retirement = final.Retirement with
                {
                    RequiredChildren = [],
                    AcceptedChildren = [],
                    FinalExpectedRevision = rootWitness.OriginalRevision,
                    TerminalReceiptFingerprint = null,
                },
            };
            empty = empty with { RequestFingerprint = empty.CalculateFingerprint() };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.StartOriginalDirectoryRetirementAsync(empty, default));
            Assert.IsNull(await reopened.ReadOriginalDirectoryRetirementAsync(rootId));
            await reopened.StartOriginalDirectoryRetirementAsync(final, default);
            var rootReceipt = RetirementReceipt(final, "root-after-tree");
            await reopened.AcceptOriginalDirectoryRetirementAsync(rootId, rootReceipt, default);
            Assert.AreEqual(rootReceipt.ReceiptId, (await reopened.ReadOriginalDirectoryRetirementAsync(rootId))!.Receipt!.ReceiptId);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static AdapterOwnedChildEffectReceipt RetirementReceipt(AdapterDirectoryRetirementRequest request, string after)
    {
        var original = request.Retirement.OriginalDirectory;
        bool moving = request.OperationKind == "Move";
        return new(1, original.InstanceId, original.AdmissionId, Guid.NewGuid(), request.Operation.OperationId,
            request.OperationKind, request.RequestFingerprint, original.Directory, original.Generation, moving ? "moved-revision" : null,
            request.OriginalParents.Select(parent =>
            {
                var predecessor = request.ParentPredecessors.SingleOrDefault(receipt => receipt.Parents.Any(value => value.OriginalParent == parent));
                return new AdapterOwnedParentTransition(parent, predecessor?.Parents.Single(value => value.OriginalParent == parent).AfterRevision ??
                    parent.OriginalRevision, after, predecessor is null ? null : AdapterDirectoryEffects.Fingerprint(predecessor), "AQID");
            }).ToArray(), moving ? new(request.Operation.DestinationRootKey!, request.Operation.DestinationPath!) : null);
    }

    [TestMethod]
    public async Task SealedPlanRetainsBothParentsOfAMovedChildAndSuppliesTheFinalMovePredecessor()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var seed = RetirementPlan(InstanceId.New(), "left", "right");
        var parent = seed.Steps[^1];
        var moved = seed.Steps[0] with
        {
            OperationKind = "Move",
            Operation = seed.Steps[0].Operation with { DestinationRootKey = "right", DestinationPath = "leaf" },
            Parents = [parent.Directory!, parent.Parents.Single(value => value.Directory.RootKey == "right")],
        };
        var plan = seed with { Steps = [moved, parent] };
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            await PrepareDirectoryPlanOwnersAsync(catalog, plan);
            await catalog.SealOriginalDirectoryPlanAsync(plan);
            var leaf = await catalog.StartOriginalDirectoryLeafAsync(moved.Operation.OperationId);
            var receipt = LeafReceipt(plan, leaf, "after-leaf-move");
            await catalog.AcceptOriginalDirectoryLeafAsync(moved.Operation.OperationId, receipt);
            var final = await catalog.ReadOriginalDirectoryPlanRetirementAsync(parent.Operation.OperationId);
            Assert.AreEqual(receipt.ReceiptId, final.ParentPredecessors.Single().ReceiptId);
            Assert.AreEqual(receipt.ReceiptId, final.Retirement.AcceptedChildren.Single().ReceiptId);
            Assert.AreEqual("after-leaf-move", final.Operation.Preconditions!.ExpectedRevision);
            await catalog.StartOriginalDirectoryRetirementAsync(final, default);
            var accepted = RetirementReceipt(final, "after-parent-move");
            await catalog.AcceptOriginalDirectoryRetirementAsync(parent.Operation.OperationId, accepted, default);
            Assert.AreEqual(accepted.ReceiptId, (await catalog.ReadOriginalDirectoryRetirementAsync(parent.Operation.OperationId))!.Receipt!.ReceiptId);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task SealingARecursivePlanCommitsEveryOwnerTogetherAndRejectsAnotherAdmission()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var plan = RetirementPlan(InstanceId.New(), "left");
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await PrepareDirectoryPlanOwnersAsync(catalog, plan);
                await RunRetirementCatalogSqlAsync(paths, """
                    CREATE TRIGGER interrupt_plan BEFORE INSERT ON original_directory_plan_members
                    WHEN NEW.ordinal=1 BEGIN SELECT RAISE(ABORT,'interrupted plan seal'); END;
                    """);
                await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.SealOriginalDirectoryPlanAsync(plan));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    catalog.StartOriginalDirectoryLeafAsync(plan.Steps[0].Operation.OperationId));
            }
            await RunRetirementCatalogSqlAsync(paths, "DROP TRIGGER interrupt_plan;");
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            await reopened.SealOriginalDirectoryPlanAsync(plan);
            var aggregate = new MirrorPulseJournalCoalescingPlan(Guid.NewGuid(), plan.InstanceId, "left", Guid.NewGuid(),
                2, "tree/leaf", "tree/leaf", MirrorPulseCoalescedEffect.DeleteFile,
                [new(plan.Steps[0].Operation.OperationId, 1, new string('A', 64)), new(Guid.NewGuid(), 2, new string('B', 64))]);
            var refused = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareJournalCoalescingAsync(aggregate));
            Assert.AreEqual("The original operation belongs to its sealed directory plan.", refused.Message);
            Assert.IsEmpty(await reopened.ReadJournalCoalescingPlansAsync());
            var other = plan with { AdmissionId = Guid.NewGuid() };
            var witnesses = plan.Steps.SelectMany(step => step.Parents).Append(plan.Steps[^1].Directory!)
                .Distinct().ToDictionary(value => value, value => value with { AdmissionId = other.AdmissionId });
            other = other with
            {
                Steps = plan.Steps.Select(step => step with
                {
                    Directory = step.Directory is null ? null : witnesses[step.Directory],
                    Parents = step.Parents.Select(parent => witnesses[parent]).ToArray(),
                }).ToArray(),
            };
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.SealOriginalDirectoryPlanAsync(other));
            var leaf = await reopened.StartOriginalDirectoryLeafAsync(plan.Steps[0].Operation.OperationId);
            Assert.AreEqual(plan.Steps[0].Operation.OperationId, leaf.Step.Operation.OperationId);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow("manifest")]
    [DataRow("member")]
    [DataRow("schema")]
    public async Task OriginalDirectoryPlansRefuseLostOrAlteredEvidenceAfterReopen(string damage)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var plan = RetirementPlan(InstanceId.New(), "left");
        Guid leaf = plan.Steps[0].Operation.OperationId;
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await PrepareDirectoryPlanOwnersAsync(catalog, plan);
                await catalog.SealOriginalDirectoryPlanAsync(plan);
            }
            await RunRetirementCatalogSqlAsync(paths, damage switch
            {
                "manifest" => "UPDATE original_directory_plans SET manifest=zeroblob(length(manifest));",
                "member" => "UPDATE original_directory_plan_members SET owner_hash=zeroblob(32);",
                _ => "DROP TABLE original_directory_plan_members;",
            });
            if (damage == "schema")
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseProductCatalog.OpenAsync(paths));
            }
            else
            {
                await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.StartOriginalDirectoryLeafAsync(leaf));
                Assert.AreEqual(MirrorPulseMutationState.Prepared, (await reopened.ReadMutationAsync(leaf))!.State);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task RunRetirementCatalogSqlAsync(MirrorPulseStoragePaths paths, string sql)
    {
        var options = new SqliteConnectionStringBuilder { DataSource = paths.ProductCatalogDatabasePath, Pooling = false };
        await using var connection = new SqliteConnection(options.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
