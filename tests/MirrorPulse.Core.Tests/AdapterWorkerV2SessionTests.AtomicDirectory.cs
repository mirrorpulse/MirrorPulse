using System.Security.Cryptography;
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
    public async Task RealHostAndLocalWorkerRecoverWholeBatchAfterCatalogCommitInterruption()
    {
        using var fixture = new AtomicHostFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = deadline.Token;
        AdapterDirectoryAdmissionRequest request;
        byte[] nativeReceipt;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths, token))
        {
            await catalog.SaveAdapterTopologyAsync(fixture.Topology, token);
            await using var supervisor = await fixture.StartAsync(catalog, token);
            request = await fixture.ReadRequestAsync(supervisor, token);
            await PrepareAtomicOwnersAsync(catalog, request, token);
            Guid interruptedMember = request.Operations[1].Operation.OperationId;
            await RunRetirementCatalogSqlAsync(fixture.Paths, $"""
                CREATE TRIGGER interrupt_atomic_accept BEFORE UPDATE ON mutation_intents
                WHEN NEW.operation_id='{interruptedMember:D}' AND NEW.state={(int)MirrorPulseMutationState.RemoteAccepted}
                BEGIN SELECT RAISE(ABORT,'interrupted atomic receipt acceptance'); END;
                """);
            await Assert.ThrowsExactlyAsync<SqliteException>(() => supervisor.ExecuteAtomicDirectoryDeletionAsync(request, token).AsTask());
            Assert.IsFalse(Directory.Exists(fixture.Tree));
            nativeReceipt = await File.ReadAllBytesAsync(fixture.Receipt(request.AdmissionId), token);
            var retained = await catalog.ReadAtomicDirectoryBatchAsync(request.AdmissionId, token);
            Assert.IsNotNull(retained);
            Assert.IsNull(retained.Receipt);
            await RequireAtomicOwnerStatesAsync(catalog, request, MirrorPulseMutationState.Executing, token);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.StartOriginalDirectoryLeafAsync(
                request.Operations[0].Operation.OperationId, token));
        }
        Directory.CreateDirectory(fixture.Tree);
        string successor = Path.Combine(fixture.Tree, "later.txt");
        await File.WriteAllTextAsync(successor, "later independent generation", token);
        AdapterAtomicDirectoryDeletionReceipt accepted;
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths, token))
        {
            await RunRetirementCatalogSqlAsync(fixture.Paths, "DROP TRIGGER interrupt_atomic_accept;");
            await using var restarted = await fixture.StartAsync(reopened, token);
            var changed = request with
            {
                Operations = [request.Operations[1], request.Operations[0], request.Operations[2]],
            };
            changed = changed with { RequestFingerprint = changed.CalculateFingerprint() };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => restarted.ExecuteAtomicDirectoryDeletionAsync(changed, token).AsTask());
            accepted = await restarted.ExecuteAtomicDirectoryDeletionAsync(request, token);
            var evidence = AdapterProtocolJson.Decode<System.Text.Json.JsonElement>(Convert.FromBase64String(accepted.BackendEvidence));
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(nativeReceipt)), evidence.GetProperty("nativeReceiptSha256").GetString());
            Assert.HasCount(request.Operations.Count, accepted.DeletedOriginals);
            await RequireAtomicOwnerStatesAsync(reopened, request, MirrorPulseMutationState.RemoteAccepted, token);
            var ordinary = new MirrorPulseMutationProjectionRecovery(reopened);
            Assert.IsEmpty(await ordinary.RepairAsync((_, _) => ValueTask.FromResult(false),
                (_, _) => throw new AssertFailedException("An ordinary missing-row check cannot project an atomic directory effect."), token));
            Guid operationId = request.Operations[0].Operation.OperationId;
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.TransitionMutationAsync(operationId,
                MirrorPulseMutationState.RemoteAccepted, MirrorPulseMutationState.Acknowledged, cancellationToken: token));
        }
        await using var final = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths, token);
        await using var offline = new AdapterInstanceProcessSupervisor(final, new WindowsCredentialManagerStore());
        var replayed = await offline.ExecuteAtomicDirectoryDeletionAsync(request, token);
        CollectionAssert.AreEqual(AdapterProtocolJson.Encode(accepted), AdapterProtocolJson.Encode(replayed));
        Assert.AreEqual("later independent generation", await File.ReadAllTextAsync(successor, token));
        Assert.AreEqual("peer", await File.ReadAllTextAsync(Path.Combine(fixture.Source, "peer.txt"), token));
    }

    [TestMethod]
    public async Task AtomicDirectoryReservationRollsBackAllOwnersBeforeAnyWorkerMutation()
    {
        using var fixture = new AtomicHostFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        CancellationToken token = deadline.Token;
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths, token);
        await catalog.SaveAdapterTopologyAsync(fixture.Topology, token);
        await using var supervisor = await fixture.StartAsync(catalog, token);
        var request = await fixture.ReadRequestAsync(supervisor, token);
        await PrepareAtomicOwnersAsync(catalog, request, token);
        Guid interruptedMember = request.Operations[1].Operation.OperationId;
        await RunRetirementCatalogSqlAsync(fixture.Paths, $"""
            CREATE TRIGGER interrupt_atomic_start BEFORE UPDATE ON mutation_intents
            WHEN NEW.operation_id='{interruptedMember:D}' AND NEW.state={(int)MirrorPulseMutationState.Executing}
            BEGIN SELECT RAISE(ABORT,'interrupted atomic request reservation'); END;
            """);
        await Assert.ThrowsExactlyAsync<SqliteException>(() => supervisor.ExecuteAtomicDirectoryDeletionAsync(request, token).AsTask());
        await RequireAtomicOwnerStatesAsync(catalog, request, MirrorPulseMutationState.Prepared, token);
        Assert.IsNull(await catalog.ReadAtomicDirectoryBatchAsync(request.AdmissionId, token));
        Assert.IsFalse(File.Exists(fixture.Receipt(request.AdmissionId)));
        Assert.AreEqual("first", await File.ReadAllTextAsync(Path.Combine(fixture.Tree, "first.txt"), token));
        Assert.AreEqual("second", await File.ReadAllTextAsync(Path.Combine(fixture.Tree, "second.txt"), token));
        await RunRetirementCatalogSqlAsync(fixture.Paths, "DROP TRIGGER interrupt_atomic_start;");
        var accepted = await supervisor.ExecuteAtomicDirectoryDeletionAsync(request, token);
        Assert.HasCount(3, accepted.DeletedOriginals);
        await RequireAtomicOwnerStatesAsync(catalog, request, MirrorPulseMutationState.RemoteAccepted, token);
    }

    [TestMethod]
    public async Task AtomicBatchLedgerMigratesSchema106AndRefusesLostSchema107Evidence()
    {
        using var fixture = new AtomicHostFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        CancellationToken token = deadline.Token;
        await using (var original = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths, token))
            await original.SaveAdapterTopologyAsync(fixture.Topology, token);
        await RunRetirementCatalogSqlAsync(fixture.Paths, "DROP TABLE original_atomic_directory_batches; PRAGMA user_version=106;");
        await using (var migrated = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths, token))
        {
            await using var supervisor = await fixture.StartAsync(migrated, token);
            var request = await fixture.ReadRequestAsync(supervisor, token);
            await PrepareAtomicOwnersAsync(migrated, request, token);
            var receipt = await supervisor.ExecuteAtomicDirectoryDeletionAsync(request, token);
            var changed = receipt with { ReceiptId = Guid.NewGuid() };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => migrated.AcceptAtomicDirectoryBatchAsync(request.AdmissionId, changed, token));
            await RunRetirementCatalogSqlAsync(fixture.Paths, """
                UPDATE original_atomic_directory_batches SET receipt_hash=zeroblob(32);
                """);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => migrated.ReadAtomicDirectoryBatchAsync(request.AdmissionId, token));
        }
        await RunRetirementCatalogSqlAsync(fixture.Paths, "DROP TABLE original_atomic_directory_batches;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await using var invalid = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths, token);
        });
    }

    [TestMethod]
    public async Task RealHostAndLocalWorkerRecoverWhenTheWorkerExitsAfterCommitWithoutReplying()
    {
        if (Environment.GetEnvironmentVariable("MP_S06_ATOMIC_FIXTURE_LOST_REPLY") != "1")
            Assert.Inconclusive("Requires the isolated pre-release Worker with the original process-exit fault.");
        using var fixture = new AtomicHostFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        CancellationToken token = deadline.Token;
        AdapterDirectoryAdmissionRequest request;
        byte[] nativeReceipt;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths, token))
        {
            await catalog.SaveAdapterTopologyAsync(fixture.Topology, token);
            await using var supervisor = await fixture.StartAsync(catalog, token);
            request = await fixture.ReadRequestAsync(supervisor, token);
            await PrepareAtomicOwnersAsync(catalog, request, token);
            string cut = Path.Combine(Path.GetDirectoryName(fixture.Receipt(request.AdmissionId))!, request.AdmissionId.ToString("N") + ".fixture-lose-reply");
            await File.WriteAllTextAsync(cut, string.Empty, token);
            await Assert.ThrowsExactlyAsync<IOException>(() => supervisor.ExecuteAtomicDirectoryDeletionAsync(request, token).AsTask());
            Assert.IsTrue(File.Exists(cut + ".observed"));
            Assert.IsFalse(Directory.Exists(fixture.Tree));
            nativeReceipt = await File.ReadAllBytesAsync(fixture.Receipt(request.AdmissionId), token);
            Assert.IsNull((await catalog.ReadAtomicDirectoryBatchAsync(request.AdmissionId, token))!.Receipt);
            await RequireAtomicOwnerStatesAsync(catalog, request, MirrorPulseMutationState.Executing, token);
        }
        Directory.CreateDirectory(fixture.Tree);
        string successor = Path.Combine(fixture.Tree, "later.txt");
        await File.WriteAllTextAsync(successor, "later occupant", token);
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths, token);
        await using var restarted = await fixture.StartAsync(reopened, token);
        var recovered = await restarted.ExecuteAtomicDirectoryDeletionAsync(request, token);
        var evidence = AdapterProtocolJson.Decode<System.Text.Json.JsonElement>(Convert.FromBase64String(recovered.BackendEvidence));
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(nativeReceipt)), evidence.GetProperty("nativeReceiptSha256").GetString());
        Assert.AreEqual("later occupant", await File.ReadAllTextAsync(successor, token));
        await RequireAtomicOwnerStatesAsync(reopened, request, MirrorPulseMutationState.RemoteAccepted, token);
    }

    private static async Task PrepareAtomicOwnersAsync(MirrorPulseProductCatalog catalog, AdapterDirectoryAdmissionRequest request,
        CancellationToken token)
    {
        foreach (var step in request.Operations)
        {
            var operation = step.Operation;
            var intent = new MirrorPulseMutationIntent(operation.OperationId, new(request.InstanceId), operation.RootKey,
                MirrorPulseWorkerChangeKind.Delete, operation.Path, null, operation.IsDirectory,
                operation.Preconditions!.ExpectedRevision, null, null, MirrorPulseMutationOrigin.Journal)
            { JournalItemId = Guid.NewGuid() };
            await catalog.PrepareMutationAsync(intent, token);
        }
    }

    private static async Task RequireAtomicOwnerStatesAsync(MirrorPulseProductCatalog catalog, AdapterDirectoryAdmissionRequest request,
        MirrorPulseMutationState expected, CancellationToken token)
    {
        foreach (var step in request.Operations)
        {
            var mutation = await catalog.ReadMutationAsync(step.Operation.OperationId, token);
            Assert.IsNotNull(mutation);
            Assert.AreEqual(expected, mutation.State);
            Assert.IsNull(mutation.AcceptedRevision);
        }
    }

    private sealed class AtomicHostFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        private readonly InstanceId _instance = InstanceId.New();
        public MirrorPulseStoragePaths Paths { get; }
        public string Source { get; }
        public string Tree => Path.Combine(Source, "tree");
        public MirrorPulseAdapterTopology Topology { get; }

        public AtomicHostFixture()
        {
            string? executable = Environment.GetEnvironmentVariable("MP_S06_ATOMIC_LOCAL_WORKER_EXE");
            if (string.IsNullOrWhiteSpace(executable))
                Assert.Inconclusive("Requires the actual Local Worker executable for the combined gate.");
            Assert.IsTrue(File.Exists(executable));
            Paths = new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data"));
            string cache = Path.Combine(_directory, "transfers");
            string fixture = Path.Combine(cache, "temporary", "mp-local-atomic-" + Guid.NewGuid().ToString("N"));
            Source = Path.Combine(fixture, "source");
            Directory.CreateDirectory(Tree);
            File.WriteAllText(Path.Combine(fixture, ".mp-atomic-test-fixture"), Path.GetFileName(fixture));
            File.WriteAllText(Path.Combine(Tree, "first.txt"), "first");
            File.WriteAllText(Path.Combine(Tree, "second.txt"), "second");
            File.WriteAllText(Path.Combine(Source, "peer.txt"), "peer");
            var adapter = new AdapterId("example.atomic-local");
            InstallId install = InstallId.New();
            var manifest = new AdapterManifest(1, adapter, "Atomic Local fixture", "1.0.0", new(2, 2),
                new Dictionary<string, string> { ["win-x64"] = Path.GetFileName(executable), ["win-arm64"] = Path.GetFileName(executable) },
                new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0");
            var installed = new InstalledAdapter(manifest, install, Path.GetDirectoryName(executable)!,
                new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
            var instance = new AdapterInstance(adapter, install, _instance, "Atomic Local",
                new Dictionary<string, string> { ["root.docs.sourceDirectory"] = Source }, [], Path.Combine(_directory, "files"),
                cache, true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
            Topology = new([installed], [instance],
                [new(adapter, _instance, RootId.New(), "docs", "Docs", "Docs", false, RootRegistrationState.Active, DateTimeOffset.UtcNow)]);
        }

        public string Receipt(Guid admissionId) => Path.Combine(Paths.DataRootPath, "state", "workers", _instance.Value.ToString("N"),
            admissionId.ToString("N") + ".committed.json");

        public async Task<AdapterInstanceProcessSupervisor> StartAsync(MirrorPulseProductCatalog catalog, CancellationToken token)
        {
            var supervisor = new AdapterInstanceProcessSupervisor(catalog, new WindowsCredentialManagerStore());
            try
            {
                await supervisor.StartAsync(Topology);
                while (!supervisor.IsConnected(_instance)) await Task.Delay(25, token);
                return supervisor;
            }
            catch { await supervisor.DisposeAsync(); throw; }
        }

        public async Task<AdapterDirectoryAdmissionRequest> ReadRequestAsync(AdapterInstanceProcessSupervisor supervisor, CancellationToken token)
        {
            var operations = new List<AdapterOriginalDirectoryOperation>();
            foreach (string path in new[] { "tree/second.txt", "tree/first.txt", "tree" })
            {
                string? revision = await supervisor.StatAsync(new(_instance, path, "docs"), token);
                Assert.IsNotNull(revision);
                operations.Add(new("Delete", new(Guid.NewGuid(), "docs", path, Preconditions: new(revision), IsDirectory: path == "tree")));
            }
            var request = new AdapterDirectoryAdmissionRequest(1, _instance.Value, Guid.NewGuid(), operations, string.Empty);
            return request with { RequestFingerprint = request.CalculateFingerprint() };
        }

        public void Dispose()
        {
            string directory = Path.GetFullPath(_directory);
            if (!SourceDirectoryPathNormalizer.IsWithin(Path.Combine(Path.GetTempPath(), "MirrorPulse-tests"), directory))
                throw new InvalidOperationException("The test directory escaped its expected parent.");
            Directory.Delete(directory, recursive: true);
        }
    }
}
