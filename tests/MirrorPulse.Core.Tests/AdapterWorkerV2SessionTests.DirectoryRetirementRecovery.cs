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
        var request = RetirementRequest(instance, "left");
        Guid operation = request.Operation.OperationId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        try
        {
            string cache = topology.Instances.Single().TransferCacheDirectory;
            Directory.CreateDirectory(cache);
            await File.WriteAllTextAsync(Path.Combine(cache, ".mp-startup-fixture-mode"), "LostOriginalDirectoryReply", token);
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, cancellationToken: token))
            {
                await catalog.SaveAdapterTopologyAsync(topology, token);
                await catalog.PrepareMutationAsync(RetirementIntent(request), token);
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

            byte[] workerOriginal = await File.ReadAllBytesAsync(Path.Combine(cache, ".mp-original-directory-fixture-receipt"), token);
            var workerReceipt = AdapterProtocolJson.Decode<AdapterOwnedChildEffectReceipt>(workerOriginal);
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
        var request = RetirementRequest(InstanceId.New(), "left", moving ? "right" : null);
        Guid operation = request.Operation.OperationId;
        var intent = RetirementIntent(request);
        var original = request.Retirement.OriginalDirectory;
        var receipt = new AdapterOwnedChildEffectReceipt(1, original.InstanceId, original.AdmissionId, Guid.NewGuid(), operation,
            request.OperationKind, request.RequestFingerprint, original.Directory, original.Generation, moving ? "moved-revision" : null,
            request.OriginalParents.Select(parent => new AdapterOwnedParentTransition(parent, parent.OriginalRevision,
                "after-retirement", null, "AQID")).ToArray(),
            moving ? new(request.Operation.DestinationRootKey!, request.Operation.DestinationPath!) : null);
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.StartOriginalDirectoryRetirementAsync(request, default));
                await catalog.PrepareMutationAsync(intent);
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
        var request = RetirementRequest(InstanceId.New(), "left");
        Guid operation = request.Operation.OperationId;
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
                await catalog.PrepareMutationAsync(RetirementIntent(request));
            await RunRetirementCatalogSqlAsync(paths, "DROP TABLE original_directory_retirements; PRAGMA user_version=104;");
            await using (var migrated = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                Assert.IsNull(await migrated.ReadOriginalDirectoryRetirementAsync(operation));
                await migrated.StartOriginalDirectoryRetirementAsync(request, default);
            }

            await RunRetirementCatalogSqlAsync(paths, "PRAGMA user_version=104;");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseProductCatalog.OpenAsync(paths));
            await RunRetirementCatalogSqlAsync(paths, "PRAGMA user_version=105; DROP TABLE original_directory_retirements;");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseProductCatalog.OpenAsync(paths));
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
