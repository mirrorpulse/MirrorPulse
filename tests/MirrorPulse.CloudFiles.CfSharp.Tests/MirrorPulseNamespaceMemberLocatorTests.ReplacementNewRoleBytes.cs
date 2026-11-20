using System.Security.AccessControl;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespaceMemberLocatorTests
{
    [TestMethod]
    [DataRow(-1)]
    [DataRow((int)MirrorPulseNamespaceReplacementContentCheckpoint.AfterBackupRecorded)]
    [DataRow((int)MirrorPulseNamespaceReplacementContentCheckpoint.AfterWriteStarted)]
    [DataRow((int)MirrorPulseNamespaceReplacementContentCheckpoint.AfterContentFlush)]
    [DataRow((int)MirrorPulseNamespaceReplacementContentCheckpoint.AfterContentRecorded)]
    public async Task ReplacementByteWriterContinuesWithNewRoleAndKeepsFirstBackupAndLaterEdits(int interruption)
    {
        // Real private files and SQLite; these synthetic permission observations prove no native ACL.
        await using var fixture = await Fixture.OpenAsync(); byte[] oldBytes = [0, 255, 7, 3]; byte[] inputBytes = [0, 128, 0, 255, 2];
        var prepared = await PrepareReplacementBytesAsync(fixture, oldBytes, inputBytes);
        var backups = new MirrorPulseLocalReplacementBackupStore(fixture.Paths);
        if (interruption >= 0)
        {
            await using var input = await new MirrorPulseLocalImportStagingStore(fixture.Paths).OpenLeaseAsync(prepared.Intent.OperationId, prepared.Intent.Content);
            await using var target = new FileStream(prepared.TargetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            await Assert.ThrowsExactlyAsync<IOException>(() => MirrorPulseNamespaceReplacementContentWriter.CopyRetainedAsync(fixture.Catalog, backups,
                prepared.Intent.OperationId, target, input, _ => ValueTask.FromResult(ReplacementByteObservation(prepared.Intent, prepared.Action, true)),
                (point, _) => (int)point == interruption ? throw new IOException("Injected old-owner byte interruption.") : ValueTask.CompletedTask, CancellationToken.None));
        }
        var before = (await fixture.Catalog.ReadNamespaceReplacementExecutionAsync(prepared.Intent.OperationId))!;
        await fixture.RestartAsync(); Guid roleId = Guid.NewGuid(); string roleSid = "S-1-5-5-234-456";
        var newAction = prepared.Action with
        {
            ActionId = roleId,
            RoleSid = roleSid,
            RoleOnlyDacl = new RawSecurityDescriptor($"D:P(A;;FA;;;{roleSid})").GetSddlForm(AccessControlSections.Access),
            RestoreDacl = new RawSecurityDescriptor($"D:P(A;;FRFW;;;{prepared.Action.OwnerSid})(A;;FA;;;{roleSid})").GetSddlForm(AccessControlSections.Access),
            PreparedAt = DateTimeOffset.UtcNow
        };
        var parent = (await fixture.Catalog.ReadNamespaceMemberProtectionSnapshotAsync(prepared.Intent.Original.Context.Location.ParentMemberId!.Value))!;
        await fixture.Catalog.PrepareNamespaceReplacementRoleActionAsync(new(1, prepared.Intent.OperationId, roleId, null,
            prepared.Action.ActionId, prepared.Action.ActionId, newAction, parent, prepared.Action.RoleOnlyDacl));
        var observed = ReplacementByteObservation(prepared.Intent, newAction, true);
        await fixture.Catalog.RecordNamespaceReplacementRoleHandoffAsync(new(1, prepared.Intent.OperationId, roleId,
            new(observed.Object.LocalObject, observed.Object.OwnerSid, observed.Object.Dacl, observed.Object.ObservedAt), 1, true, observed.PlaceholderIdentity));
        if (before.ContentCompletion is null)
        {
            await using var input = await new MirrorPulseLocalImportStagingStore(fixture.Paths).OpenLeaseAsync(prepared.Intent.OperationId, prepared.Intent.Content);
            await using var target = new FileStream(prepared.TargetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var completion = await MirrorPulseNamespaceReplacementContentWriter.CopyRetainedAsync(fixture.Catalog, backups, prepared.Intent.OperationId,
                target, input, _ => ValueTask.FromResult(ReplacementByteObservation(prepared.Intent, newAction, true)), null, CancellationToken.None);
            Assert.AreEqual(roleId, completion.AuthorityRecoveryId); Assert.AreEqual(prepared.Action.ActionId, completion.ActionId);
        }
        var after = (await fixture.Catalog.ReadNamespaceReplacementExecutionAsync(prepared.Intent.OperationId))!;
        Assert.AreEqual(prepared.Action, after.Action); if (before.Backup is not null) Assert.AreEqual(before.Backup, after.Backup);
        else Assert.AreEqual(roleId, after.Backup!.AuthorityRecoveryId);
        Assert.AreEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(oldBytes)), after.Backup!.Content.Sha256);
        CollectionAssert.AreEqual(inputBytes, await File.ReadAllBytesAsync(prepared.TargetPath));
        byte[] laterBytes = [7, 0, 255]; await File.WriteAllBytesAsync(prepared.TargetPath, laterBytes); await fixture.RestartAsync();
        Assert.AreEqual(after.ContentCompletion, await MirrorPulseNamespaceReplacementContentWriter.CopyRetainedAsync(fixture.Catalog, backups,
            prepared.Intent.OperationId, null, null, _ => throw new AssertFailedException("Historical copy must not inspect later edits."), null, CancellationToken.None));
        CollectionAssert.AreEqual(laterBytes, await File.ReadAllBytesAsync(prepared.TargetPath));
    }
}
