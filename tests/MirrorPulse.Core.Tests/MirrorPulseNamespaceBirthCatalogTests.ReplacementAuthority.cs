using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReplacementNewRoleCopyRetainsFirstFactsAndItsHistoricalHandoffAcrossOwners(bool backupBeforeRole)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementExecution expected;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var original = await PrepareReplacementRoleFixtureAsync(catalog, false);
            MirrorPulseNamespaceReplacementBackup? backup = null;
            if (backupBeforeRole)
            {
                backup = AuthorityBackup(original, null);
                await catalog.RecordNamespaceReplacementBackupAsync(backup);
            }
            var role = await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-50-60");
            await catalog.PrepareNamespaceReplacementRoleActionAsync(role);
            var handoff = ReplacementRoleHandoff(role, original.Intent, true);
            await catalog.RecordNamespaceReplacementRoleHandoffAsync(handoff);
            backup ??= await catalog.RecordNamespaceReplacementBackupAsync(AuthorityBackup(original, role));
            var write = new MirrorPulseNamespaceReplacementWriteStart(1, original.Intent.OperationId, original.Action!.ActionId,
                backup.Verification.ObservedAt.AddSeconds(1));
            await catalog.RecordNamespaceReplacementWriteStartAsync(write);
            var receipt = AuthorityContent(original, role);
            await catalog.RecordNamespaceReplacementContentCompletionAsync(receipt);
            var next = await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-70-80", role);
            await catalog.PrepareNamespaceReplacementRoleActionAsync(next);
            expected = (await catalog.ReadNamespaceReplacementExecutionAsync(original.Intent.OperationId))!;
            Assert.AreEqual(original.Action, expected.Action); Assert.AreEqual(backup, expected.Backup);
            Assert.AreEqual(write, expected.WriteStart); Assert.AreEqual(receipt, expected.ContentCompletion);
            Assert.AreEqual(role.RecoveryId, receipt.AuthorityRecoveryId); Assert.AreEqual(original.Action.ActionId, receipt.ActionId);
            Assert.HasCount(1, expected.RoleHandoffHistory); Assert.AreEqual(handoff, expected.RoleHandoffHistory[0]);
            Assert.IsNull(expected.RoleHandoff); Assert.IsNull(expected.Restoration);
            Assert.IsNull(await catalog.ReadMutationAsync(original.Intent.OperationId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = (await reopened.ReadNamespaceReplacementExecutionAsync(expected.Intent.OperationId))!;
        Assert.AreEqual(expected.Action, actual.Action); Assert.AreEqual(expected.Backup, actual.Backup);
        Assert.AreEqual(expected.WriteStart, actual.WriteStart); Assert.AreEqual(expected.ContentCompletion, actual.ContentCompletion);
        Assert.AreEqual(expected.Backup, await reopened.RecordNamespaceReplacementBackupAsync(expected.Backup!));
        Assert.AreEqual(expected.ContentCompletion, await reopened.RecordNamespaceReplacementContentCompletionAsync(expected.ContentCompletion!));
        CollectionAssert.AreEqual(new[] { expected.Intent.RootId }, (await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow(false, "missing")]
    [DataRow(true, "missing")]
    [DataRow(false, "unobserved")]
    [DataRow(true, "unobserved")]
    [DataRow(false, "superseded")]
    [DataRow(true, "superseded")]
    [DataRow(false, "legacy")]
    [DataRow(true, "legacy")]
    [DataRow(false, "before-handoff")]
    [DataRow(true, "before-handoff")]
    [DataRow(false, "first-action")]
    [DataRow(true, "first-action")]
    [DataRow(false, "descriptor")]
    [DataRow(true, "descriptor")]
    [DataRow(false, "binding")]
    [DataRow(true, "binding")]
    public async Task ReplacementCopyCannotBorrowUndeclaredUnobservedOrSupersededAuthority(bool content, string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareReplacementRoleFixtureAsync(catalog, false);
        if (content)
        {
            var backup = AuthorityBackup(original, null); await catalog.RecordNamespaceReplacementBackupAsync(backup);
            await catalog.RecordNamespaceReplacementWriteStartAsync(new(1, original.Intent.OperationId, original.Action!.ActionId,
                backup.Verification.ObservedAt.AddSeconds(1)));
        }
        var role = await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-50-60");
        await catalog.PrepareNamespaceReplacementRoleActionAsync(role);
        if (changed != "unobserved") await catalog.RecordNamespaceReplacementRoleHandoffAsync(ReplacementRoleHandoff(role, original.Intent, true));
        if (changed == "superseded")
            await catalog.PrepareNamespaceReplacementRoleActionAsync(await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-70-80", role));
        var backupFact = AuthorityBackup(original, role); var contentFact = AuthorityContent(original, role);
        Guid? authority = changed == "missing" ? Guid.NewGuid() : changed == "legacy" ? null : role.RecoveryId;
        var verification = content ? contentFact.Verification : backupFact.Verification;
        verification = changed switch
        {
            "before-handoff" => verification with { ObservedAt = role.Action.PreparedAt },
            "descriptor" => verification with { Dacl = original.Action!.RoleOnlyDacl },
            "binding" => verification with { LocalObject = verification.LocalObject with { LocalFileId = Guid.NewGuid() } },
            _ => verification
        };
        Guid firstId = changed == "first-action" ? Guid.NewGuid() : original.Action!.ActionId;
        if (content)
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceReplacementContentCompletionAsync(contentFact with
            { Version = changed == "legacy" ? 1 : 2, AuthorityRecoveryId = authority, ActionId = firstId, Verification = verification }));
        else
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceReplacementBackupAsync(backupFact with
            { Version = changed == "legacy" ? 1 : 2, AuthorityRecoveryId = authority, ActionId = firstId, Verification = verification }));
        var retained = (await catalog.ReadNamespaceReplacementExecutionAsync(original.Intent.OperationId))!;
        Assert.IsNull(retained.ContentCompletion); if (!content) Assert.IsNull(retained.Backup);
        CollectionAssert.AreEqual(new[] { original.Intent.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReplacementCopyVersionRequiresASeparateExplicitRecoveryId(bool content)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareReplacementRoleFixtureAsync(catalog, false);
        foreach (var id in new Guid?[] { null, Guid.Empty, original.Intent.OperationId, original.Action!.ActionId })
        {
            if (content) await Assert.ThrowsAsync<ArgumentException>(() => catalog.RecordNamespaceReplacementContentCompletionAsync(AuthorityContent(original, null) with { Version = 2, AuthorityRecoveryId = id }));
            else await Assert.ThrowsAsync<ArgumentException>(() => catalog.RecordNamespaceReplacementBackupAsync(AuthorityBackup(original, null) with { Version = 2, AuthorityRecoveryId = id }));
        }
        if (content) await Assert.ThrowsAsync<ArgumentException>(() => catalog.RecordNamespaceReplacementContentCompletionAsync(AuthorityContent(original, null) with { AuthorityRecoveryId = Guid.NewGuid() }));
        else await Assert.ThrowsAsync<ArgumentException>(() => catalog.RecordNamespaceReplacementBackupAsync(AuthorityBackup(original, null) with { AuthorityRecoveryId = Guid.NewGuid() }));
    }

    [TestMethod]
    public async Task ReplacementHistoricalCopyRequiresItsOriginalHandoffEvenAfterFurtherRoleAdmission()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareReplacementRoleFixtureAsync(catalog, false);
        var role = await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-50-60"); await catalog.PrepareNamespaceReplacementRoleActionAsync(role);
        await catalog.RecordNamespaceReplacementRoleHandoffAsync(ReplacementRoleHandoff(role, original.Intent, true));
        var backup = AuthorityBackup(original, role); await catalog.RecordNamespaceReplacementBackupAsync(backup);
        await catalog.RecordNamespaceReplacementWriteStartAsync(new(1, original.Intent.OperationId, original.Action!.ActionId, backup.Verification.ObservedAt.AddSeconds(1)));
        await catalog.RecordNamespaceReplacementContentCompletionAsync(AuthorityContent(original, role));
        await catalog.PrepareNamespaceReplacementRoleActionAsync(await ReplacementRoleActionAsync(catalog, original, "S-1-5-5-70-80", role));
        await ExecuteSqlAsync(fixture.Paths, "DELETE FROM namespace_replacement_role_handoffs;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceReplacementExecutionAsync(original.Intent.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task ReplacementVersionOnePayloadsAndFingerprintsSurviveSchema46UpgradeByteForByte()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementExecution original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) original = await PrepareReplacementRoleFixtureAsync(catalog, true);
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT payload,fingerprint FROM namespace_replacement_execution_facts WHERE kind=2 UNION ALL SELECT payload,fingerprint FROM namespace_replacement_content_completions;";
        var before = new List<byte[]>();
        await using (var rows = await query.ExecuteReaderAsync())
            while (await rows.ReadAsync()) { before.Add((byte[])rows[0]); before.Add((byte[])rows[1]); }
        Assert.HasCount(4, before);
        var oldBackup = original.Backup!; var oldCopy = original.ContentCompletion!;
        CollectionAssert.AreEqual(JsonSerializer.SerializeToUtf8Bytes(new { oldBackup.Version, oldBackup.OperationId, oldBackup.ActionId, oldBackup.Content, oldBackup.Verification, oldBackup.LinkCount }), before[0]);
        CollectionAssert.AreEqual(JsonSerializer.SerializeToUtf8Bytes(new { oldCopy.Version, oldCopy.OperationId, oldCopy.ActionId, oldCopy.Content, oldCopy.Verification, oldCopy.LinkCount, oldCopy.IsPlaceholder, oldCopy.IsInSync, oldCopy.PlaceholderIdentity }), before[2]);
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA user_version=46;");
        await using var upgraded = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(original.Backup, (await upgraded.ReadNamespaceReplacementExecutionAsync(original.Intent.OperationId))!.Backup);
        Assert.AreEqual(original.ContentCompletion, await upgraded.RecordNamespaceReplacementContentCompletionAsync(oldCopy));
        await using var after = await query.ExecuteReaderAsync(); int index = 0;
        while (await after.ReadAsync()) { CollectionAssert.AreEqual(before[index++], (byte[])after[0]); CollectionAssert.AreEqual(before[index++], (byte[])after[1]); }
        Assert.AreEqual(4, index);
    }

    private static MirrorPulseNamespaceReplacementBackup AuthorityBackup(MirrorPulseNamespaceReplacementExecution original,
        MirrorPulseNamespaceReplacementRoleAction? role)
    {
        var action = role?.Action ?? original.Action!;
        return new(role is null ? 1 : 2, original.Intent.OperationId, original.Action!.ActionId,
            new(0, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([]))),
            new(action.LocalObject, action.OwnerSid, action.RoleOnlyDacl, action.PreparedAt.AddSeconds(2)), 1)
        { AuthorityRecoveryId = role?.RecoveryId };
    }

    private static MirrorPulseNamespaceReplacementContentCompletion AuthorityContent(MirrorPulseNamespaceReplacementExecution original,
        MirrorPulseNamespaceReplacementRoleAction? role)
    {
        var action = role?.Action ?? original.Action!;
        return new(role is null ? 1 : 2, original.Intent.OperationId, original.Action!.ActionId, original.Intent.Content,
            new(action.LocalObject, action.OwnerSid, action.RoleOnlyDacl, action.PreparedAt.AddSeconds(4)), 1, true, false, original.Intent.OriginalPlaceholderIdentity)
        { AuthorityRecoveryId = role?.RecoveryId };
    }
}
