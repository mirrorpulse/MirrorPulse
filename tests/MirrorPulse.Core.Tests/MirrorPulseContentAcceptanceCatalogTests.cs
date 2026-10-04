using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseContentAcceptanceCatalogTests
{
    private static MirrorPulseMutationIntent Intent() => MirrorPulseMutationExecutorTests.Intent() with
    {
        UploadBinding = new(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.ReplacePlaceholderIdentity,
            Convert.ToBase64String([1, 2, 3])),
    };

    private static MirrorPulseContentAcceptanceProof Proof(MirrorPulseMutationIntent intent) =>
        new(intent.OperationId, intent.UploadBinding!, Guid.NewGuid(), "remote-object", "accepted", intent.ContentLength!.Value, intent.ContentSha256!);

    private static async Task AcceptAsync(MirrorPulseProductCatalog catalog, MirrorPulseMutationIntent intent)
    {
        await catalog.PrepareMutationAsync(intent);
        await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
        await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted, "accepted");
    }

    [TestMethod]
    public async Task AcceptedProofAndNativeProjectionReceiptSurviveCatalogRestartWithoutAcknowledgement()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        MirrorPulseMutationIntent intent = Intent();
        MirrorPulseContentAcceptanceProof proof = Proof(intent);
        var receipt = new MirrorPulseContentConfirmationReceipt(MirrorPulseContentConfirmationOutcome.NativeAppliedProjectionPending,
            MirrorPulseContentConfirmationStage.Projection, MirrorPulseContentConfirmationStage.Complete,
            true, true, true, false, 4, 1, 0, 0, null, unchecked((int)0x80004005), true, "AQID", DateTimeOffset.UtcNow);
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await AcceptAsync(catalog, intent);
                await catalog.SaveContentAcceptanceProofAsync(proof);
                await catalog.SaveContentAcceptanceProofAsync(proof);
                await catalog.SaveContentConfirmationReceiptAsync(intent.OperationId, receipt);
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            Assert.AreEqual(intent, (await reopened.ReadMutationAsync(intent.OperationId))!.Intent);
            Assert.AreEqual(proof, await reopened.ReadContentAcceptanceProofAsync(intent.OperationId));
            Assert.AreEqual(receipt, await reopened.ReadContentConfirmationReceiptAsync(intent.OperationId));
            Assert.IsFalse(receipt.MayAcknowledge);
            Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, (await reopened.ReadMutationAsync(intent.OperationId))!.State);
            MirrorPulseContentConfirmationReceipt repaired = receipt with
            {
                Outcome = MirrorPulseContentConfirmationOutcome.AlreadyConfirmed,
                Stage = MirrorPulseContentConfirmationStage.Complete,
                DurableProjectionCommitted = true,
                ProjectionErrorHResult = null,
                ObservedInSync = false,
            };
            await reopened.SaveContentConfirmationReceiptAsync(intent.OperationId, repaired);
            Assert.IsTrue((await reopened.ReadContentConfirmationReceiptAsync(intent.OperationId))!.MayAcknowledge);
            Assert.AreEqual(proof, await reopened.ReadContentAcceptanceProofAsync(intent.OperationId));
            Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, (await reopened.ReadMutationAsync(intent.OperationId))!.State);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ProofRequiresRemoteAcceptanceAndRejectsAlteredBindingContentRevisionOrIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            MirrorPulseMutationIntent intent = Intent();
            MirrorPulseContentAcceptanceProof proof = Proof(intent);
            await catalog.PrepareMutationAsync(intent);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.SaveContentAcceptanceProofAsync(proof));
            await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
            await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted, "accepted");
            foreach (MirrorPulseContentAcceptanceProof changed in new[]
            {
                proof with { UploadBinding = proof.UploadBinding with { LocalObject = proof.UploadBinding.LocalObject with { LocalFileId = Guid.NewGuid() } } },
                proof with { Length = 5 }, proof with { Sha256 = new string('B', 64) }, proof with { AcceptedRevision = "other" },
            })
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.SaveContentAcceptanceProofAsync(changed));
            Assert.IsNull(await catalog.ReadContentAcceptanceProofAsync(intent.OperationId));
            await catalog.SaveContentAcceptanceProofAsync(proof);
            foreach (MirrorPulseContentAcceptanceProof changed in new[] { proof with { AcceptedItemId = Guid.NewGuid() }, proof with { RemoteId = "replacement" } })
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.SaveContentAcceptanceProofAsync(changed));
            Assert.AreEqual(proof, await catalog.ReadContentAcceptanceProofAsync(intent.OperationId));
            Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, (await catalog.ReadMutationAsync(intent.OperationId))!.State);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Version12HistoricalIntentWithoutBindingIsPreservedAndCannotAdoptCurrentObject()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        MirrorPulseMutationIntent historical = MirrorPulseMutationExecutorTests.Intent();
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths)) await AcceptAsync(catalog, historical);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = paths.ProductCatalogDatabasePath, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                // An actual pre-upgrade JSON payload has no new property at all.
                command.CommandText = "SELECT payload FROM mutation_intents WHERE operation_id=$operation;";
                command.Parameters.AddWithValue("$operation", historical.OperationId.ToString("D"));
                var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>((byte[])(await command.ExecuteScalarAsync())!)!;
                fields.Remove("uploadBinding");
                command.CommandText = "UPDATE mutation_intents SET payload=$payload WHERE operation_id=$operation; DROP TABLE content_acceptance_proofs; PRAGMA user_version=12;";
                command.Parameters.AddWithValue("$payload", JsonSerializer.SerializeToUtf8Bytes(fields));
                await command.ExecuteNonQueryAsync();
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            MirrorPulseMutationRecord record = (await reopened.ReadMutationAsync(historical.OperationId))!;
            Assert.IsNull(record.Intent.UploadBinding);
            Assert.AreEqual(historical, record.Intent);
            MirrorPulseContentAcceptanceProof forged = Proof(Intent() with { OperationId = historical.OperationId });
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.SaveContentAcceptanceProofAsync(forged));
            Assert.IsNull(await reopened.ReadContentAcceptanceProofAsync(historical.OperationId));
            Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, (await reopened.ReadMutationAsync(historical.OperationId))!.State);
            await reopened.SaveBlockedLocalOperationAsync(new(historical.OperationId, historical.InstanceId, historical.RelativePath,
                MirrorPulseLocalOperationBlockReason.MissingUploadBinding, DateTimeOffset.UtcNow));
            Assert.AreEqual(MirrorPulseLocalOperationBlockReason.MissingUploadBinding, (await reopened.ReadBlockedLocalOperationsAsync()).Single().Reason);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ReceiptRequiresRetainedProofAndCompleteNativeAndProjectionFactsForAcknowledgement()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var receipt = new MirrorPulseContentConfirmationReceipt(MirrorPulseContentConfirmationOutcome.Confirmed,
            MirrorPulseContentConfirmationStage.Complete, MirrorPulseContentConfirmationStage.Complete,
            false, true, true, true, 4, 1, null, 0, null, null, true, null, DateTimeOffset.UtcNow);
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.SaveContentConfirmationReceiptAsync(Guid.NewGuid(), receipt));
            Assert.IsTrue(receipt.MayAcknowledge);
            Assert.IsFalse((receipt with { NativeConfirmationVerified = false }).MayAcknowledge);
            Assert.IsFalse((receipt with { DurableProjectionCommitted = false }).MayAcknowledge);
            Assert.IsFalse((receipt with { Outcome = MirrorPulseContentConfirmationOutcome.ProjectionConflict }).MayAcknowledge);
        }
        finally { Directory.Delete(root, true); }
    }
}
