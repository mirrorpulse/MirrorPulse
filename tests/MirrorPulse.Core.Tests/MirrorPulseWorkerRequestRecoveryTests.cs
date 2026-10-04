using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseWorkerRequestRecoveryTests
{
    [TestMethod]
    public async Task StableReplaySurvivesRestartAndRejectsChangedPayloadOrInstance()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        Guid operation = Guid.NewGuid();
        InstanceId instance = InstanceId.New();
        byte[] original = SHA256.HashData(new byte[] { 1 });
        byte[] changedReference = SHA256.HashData(new byte[] { 2 });
        byte[] stable = SHA256.HashData(new byte[] { 3 });
        byte[] changedPayload = SHA256.HashData(new byte[] { 4 });
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
                Assert.IsTrue(await catalog.TryRecordWorkerRequestAsync(operation, instance, original, stableFingerprint: stable));
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            Assert.IsFalse(await reopened.TryRecordWorkerRequestAsync(operation, instance, changedReference, stableFingerprint: stable));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.TryRecordWorkerRequestAsync(operation, instance, original, stableFingerprint: changedPayload));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.TryRecordWorkerRequestAsync(operation, InstanceId.New(), changedReference, stableFingerprint: stable));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.TryRecordWorkerRequestAsync(operation, instance, changedReference));
            CollectionAssert.AreEqual(original, (await reopened.ReadWorkerRequestAsync(operation))!.Fingerprint);
            CollectionAssert.AreEqual(stable, (await reopened.ReadWorkerRequestAsync(operation))!.StableFingerprint!);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Schema13LegacyFingerprintRequiresExactReplayBeforeSupplementingProof()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        Guid operation = Guid.NewGuid();
        InstanceId instance = InstanceId.New();
        byte[] original = SHA256.HashData(new byte[] { 1 });
        byte[] changed = SHA256.HashData(new byte[] { 2 });
        byte[] stable = SHA256.HashData(new byte[] { 3 });
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
                await catalog.TryRecordWorkerRequestAsync(operation, instance, original);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.ProductCatalogDatabasePath, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE worker_requests DROP COLUMN stable_fingerprint; PRAGMA user_version=13;";
                await command.ExecuteNonQueryAsync();
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.TryRecordWorkerRequestAsync(operation, instance, changed, stableFingerprint: stable));
            Assert.IsNull((await reopened.ReadWorkerRequestAsync(operation))!.StableFingerprint);
            Assert.IsFalse(await reopened.TryRecordWorkerRequestAsync(operation, instance, original, stableFingerprint: stable));
            Assert.IsFalse(await reopened.TryRecordWorkerRequestAsync(operation, instance, changed, stableFingerprint: stable));
        }
        finally { Directory.Delete(root, true); }
    }
}
