using System.Security.AccessControl;
using System.Security.Cryptography;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespaceMemberLocatorTests
{
    // Real file/SQLite byte persistence; synthetic observations do not establish native protection.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReplacementByteWriterKeepsOldBackupAndPersistsExactBinaryBeforeHistoricalReplay(bool placeholder)
    {
        await using var fixture = await Fixture.OpenAsync();
        byte[] oldBytes = [0, 5, 255, 0, 128]; byte[] newBytes = Enumerable.Range(0, 2 * 1024 * 1024 + 1).Select(index => (byte)(index * 7)).ToArray();
        var prepared = await PrepareReplacementBytesAsync(fixture, oldBytes, newBytes);
        var backups = new MirrorPulseLocalReplacementBackupStore(fixture.Paths);
        await using (var input = await new MirrorPulseLocalImportStagingStore(fixture.Paths).OpenLeaseAsync(prepared.Intent.OperationId, prepared.Intent.Content))
        await using (var target = new FileStream(prepared.TargetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            target.Position = 2;
            var receipt = await MirrorPulseNamespaceReplacementContentWriter.CopyRetainedAsync(fixture.Catalog, backups,
                prepared.Intent.OperationId, target, input, _ => ValueTask.FromResult(ReplacementByteObservation(prepared.Intent, prepared.Action, placeholder)), null, CancellationToken.None);
            Assert.AreEqual(prepared.Intent.Content, receipt.Content); Assert.IsFalse(receipt.IsInSync); Assert.AreEqual(placeholder, receipt.IsPlaceholder);
        }
        CollectionAssert.AreEqual(newBytes, await File.ReadAllBytesAsync(prepared.TargetPath));
        var saved = (await fixture.Catalog.ReadNamespaceReplacementExecutionAsync(prepared.Intent.OperationId))!;
        Assert.AreEqual(oldBytes.LongLength, saved.Backup!.Content.Length);
        await using (var backup = await backups.OpenLeaseAsync(prepared.Intent.OperationId, saved.Backup.Content))
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(oldBytes)), Convert.ToHexString(await SHA256.HashDataAsync(backup.Data)));
        byte[] laterEdit = [7, 7, 7, 0]; await File.WriteAllBytesAsync(prepared.TargetPath, laterEdit);
        await fixture.RestartAsync();
        Assert.AreEqual(saved.ContentCompletion, await MirrorPulseNamespaceReplacementContentWriter.CopyRetainedAsync(fixture.Catalog, backups,
            prepared.Intent.OperationId, null, null, _ => throw new AssertFailedException("Historical receipt must not inspect or rewrite the current file."), null, CancellationToken.None));
        CollectionAssert.AreEqual(laterEdit, await File.ReadAllBytesAsync(prepared.TargetPath));
        CollectionAssert.AreEqual(new[] { prepared.Intent.RootId }, (await fixture.Catalog.ReadNamespaceRecoveryRootsAsync()).ToArray());
    }

    [TestMethod]
    [DataRow((int)MirrorPulseNamespaceReplacementContentCheckpoint.AfterBackupRecorded)]
    [DataRow((int)MirrorPulseNamespaceReplacementContentCheckpoint.AfterWriteStarted)]
    [DataRow((int)MirrorPulseNamespaceReplacementContentCheckpoint.AfterContentFlush)]
    [DataRow((int)MirrorPulseNamespaceReplacementContentCheckpoint.AfterContentRecorded)]
    public async Task ReplacementByteWriterRecoversEachDurableBoundaryWithoutReplacingFirstBackup(int interruption)
    {
        var interrupted = (MirrorPulseNamespaceReplacementContentCheckpoint)interruption;
        await using var fixture = await Fixture.OpenAsync(); byte[] oldBytes = [255, 4, 1]; byte[] newBytes = [0, 0, 128, 255, 3];
        var prepared = await PrepareReplacementBytesAsync(fixture, oldBytes, newBytes); var backups = new MirrorPulseLocalReplacementBackupStore(fixture.Paths);
        await using (var input = await new MirrorPulseLocalImportStagingStore(fixture.Paths).OpenLeaseAsync(prepared.Intent.OperationId, prepared.Intent.Content))
        await using (var target = new FileStream(prepared.TargetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            await Assert.ThrowsExactlyAsync<IOException>(() => MirrorPulseNamespaceReplacementContentWriter.CopyRetainedAsync(fixture.Catalog, backups,
                prepared.Intent.OperationId, target, input, _ => ValueTask.FromResult(ReplacementByteObservation(prepared.Intent, prepared.Action, true)),
                (point, stop) => point == interrupted ? throw new IOException("Injected byte persistence interruption.") : ValueTask.CompletedTask, CancellationToken.None));
        var before = (await fixture.Catalog.ReadNamespaceReplacementExecutionAsync(prepared.Intent.OperationId))!;
        Assert.IsNotNull(before.Backup); Assert.AreEqual(Convert.ToHexString(SHA256.HashData(oldBytes)), before.Backup.Content.Sha256);
        Assert.AreEqual(interrupted != MirrorPulseNamespaceReplacementContentCheckpoint.AfterBackupRecorded, before.WriteStart is not null);
        Assert.AreEqual(interrupted == MirrorPulseNamespaceReplacementContentCheckpoint.AfterContentRecorded, before.ContentCompletion is not null);
        await fixture.RestartAsync();
        await using (var input = await new MirrorPulseLocalImportStagingStore(fixture.Paths).OpenLeaseAsync(prepared.Intent.OperationId, prepared.Intent.Content))
        await using (var target = new FileStream(prepared.TargetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            await MirrorPulseNamespaceReplacementContentWriter.CopyRetainedAsync(fixture.Catalog, backups, prepared.Intent.OperationId,
                target, input, _ => ValueTask.FromResult(ReplacementByteObservation(prepared.Intent, prepared.Action, true)), null, CancellationToken.None);
        var recovered = (await fixture.Catalog.ReadNamespaceReplacementExecutionAsync(prepared.Intent.OperationId))!;
        Assert.AreEqual(before.Backup, recovered.Backup); Assert.IsNotNull(recovered.ContentCompletion);
        CollectionAssert.AreEqual(newBytes, await File.ReadAllBytesAsync(prepared.TargetPath));
    }

    [TestMethod]
    [DataRow("binding")]
    [DataRow("identity")]
    [DataRow("descriptor")]
    [DataRow("links")]
    public async Task ReplacementByteWriterRejectsChangedOriginalBeforeRetainingBackupOrWriting(string changed)
    {
        await using var fixture = await Fixture.OpenAsync(); byte[] oldBytes = [1, 2, 3];
        var prepared = await PrepareReplacementBytesAsync(fixture, oldBytes, [8, 9]); var backups = new MirrorPulseLocalReplacementBackupStore(fixture.Paths);
        var observation = ReplacementByteObservation(prepared.Intent, prepared.Action, true);
        observation = changed switch
        {
            "binding" => observation with { Object = observation.Object with { LocalObject = observation.Object.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "identity" => observation with { PlaceholderIdentity = Convert.ToBase64String([1, 2, 3]) },
            "descriptor" => observation with { Object = observation.Object with { Dacl = prepared.Action.RestoreDacl } },
            "links" => observation with { Object = observation.Object with { LinkCount = 2 } },
            _ => throw new AssertFailedException()
        };
        await using var input = await new MirrorPulseLocalImportStagingStore(fixture.Paths).OpenLeaseAsync(prepared.Intent.OperationId, prepared.Intent.Content);
        await using var target = new FileStream(prepared.TargetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseNamespaceReplacementContentWriter.CopyRetainedAsync(fixture.Catalog, backups,
            prepared.Intent.OperationId, target, input, _ => ValueTask.FromResult(observation), null, CancellationToken.None));
        await target.DisposeAsync();
        CollectionAssert.AreEqual(oldBytes, await File.ReadAllBytesAsync(prepared.TargetPath));
        var retained = (await fixture.Catalog.ReadNamespaceReplacementExecutionAsync(prepared.Intent.OperationId))!;
        Assert.IsNull(retained.Backup); Assert.IsNull(retained.WriteStart); Assert.IsNull(retained.ContentCompletion);
    }

    private static async Task<(MirrorPulseNamespaceReplacementIntent Intent, MirrorPulseNamespaceReplacementAction Action, string TargetPath)> PrepareReplacementBytesAsync(
        Fixture fixture, byte[] oldBytes, byte[] newBytes)
    {
        string sourcePath = Path.Combine(fixture.Paths.DataRootPath, "replacement-test-input.bin");
        string targetPath = Path.Combine(fixture.Paths.SyncRootPath, "Docs", "existing");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!); await File.WriteAllBytesAsync(targetPath, oldBytes);
        await File.WriteAllBytesAsync(sourcePath, newBytes);
        Guid operationId = Guid.NewGuid(); var staging = new MirrorPulseLocalImportStagingStore(fixture.Paths);
        var content = await staging.StageAsync(operationId, sourcePath);
        var original = (await fixture.Catalog.ReadNamespaceMemberProtectionSnapshotAsync(fixture.Member.MemberId))!;
        var intent = new MirrorPulseNamespaceReplacementIntent(1, operationId, original,
            Convert.ToBase64String(new CloudPlaceholderIdentity(original.Identity.ItemId!.Value, original.Identity.RemoteId!, "revision-1").Encode()), content, DateTimeOffset.UtcNow);
        await fixture.Catalog.PrepareNamespaceReplacementAsync(intent);
        await fixture.Catalog.RecordNamespaceReplacementStartAsync(new(1, operationId, DateTimeOffset.UtcNow));
        var action = new MirrorPulseNamespaceReplacementAction(1, operationId, Guid.NewGuid(), original.Context.Member.LocalObject,
            original.Context.Verification.OwnerSid, original.Context.Protection.RoleSid,
            new RawSecurityDescriptor($"D:P(A;;FA;;;{original.Context.Protection.RoleSid})").GetSddlForm(AccessControlSections.Access), original.ExpectedDacl, DateTimeOffset.UtcNow);
        await fixture.Catalog.PrepareNamespaceReplacementActionAsync(action);
        return (intent, action, targetPath);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(8)]
    public async Task ReplacementByteWriterAcceptsUnchangedResidentContentWithoutInventingRemoteAcceptance(int length)
    {
        await using var fixture = await Fixture.OpenAsync(); byte[] expectedBytes = Enumerable.Range(0, length).Select(index => (byte)(index * 13)).ToArray();
        var prepared = await PrepareReplacementBytesAsync(fixture, expectedBytes, expectedBytes); var backups = new MirrorPulseLocalReplacementBackupStore(fixture.Paths);
        await using (var input = await new MirrorPulseLocalImportStagingStore(fixture.Paths).OpenLeaseAsync(prepared.Intent.OperationId, prepared.Intent.Content))
        await using (var target = new FileStream(prepared.TargetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var completion = await MirrorPulseNamespaceReplacementContentWriter.CopyRetainedAsync(fixture.Catalog, backups, prepared.Intent.OperationId,
                target, input, _ => ValueTask.FromResult(ReplacementByteObservation(prepared.Intent, prepared.Action, true) with { IsInSync = true }), null, CancellationToken.None);
            Assert.IsTrue(completion.IsInSync); Assert.AreEqual(prepared.Intent.Content, completion.Content);
        }
        CollectionAssert.AreEqual(expectedBytes, await File.ReadAllBytesAsync(prepared.TargetPath));
        var retained = (await fixture.Catalog.ReadNamespaceReplacementExecutionAsync(prepared.Intent.OperationId))!;
        Assert.AreEqual(prepared.Intent.Content, retained.Backup!.Content); Assert.IsNull(await fixture.Catalog.ReadMutationAsync(prepared.Intent.OperationId));
        CollectionAssert.AreEqual(new[] { prepared.Intent.RootId }, (await fixture.Catalog.ReadNamespaceRecoveryRootsAsync()).ToArray());
    }

    private static MirrorPulseNamespaceReplacementWriteObservation ReplacementByteObservation(MirrorPulseNamespaceReplacementIntent intent,
        MirrorPulseNamespaceReplacementAction action, bool placeholder) =>
        new(new(action.LocalObject, intent.RootId, intent.Original.Context.Location.RelativePath, false, action.OwnerSid, action.RoleOnlyDacl,
            DateTimeOffset.UtcNow)
        { LinkCount = 1 }, placeholder, false, placeholder ? intent.OriginalPlaceholderIdentity : null);
}
