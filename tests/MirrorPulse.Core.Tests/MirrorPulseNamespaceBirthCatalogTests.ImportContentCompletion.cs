using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task CompletedImportCopyRetainsOriginalInputAndBindingAcrossOwnersWithoutPublishingBirth()
    {
        using var fixture = new Fixture();
        ImportCopyFixture setup;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareImportCopyAsync(first);
            Assert.AreEqual(setup.Completion, await first.RecordNamespaceBirthContentCompletionAsync(setup.Completion));
            Assert.IsNull(await first.ReadNamespaceBirthObservationAsync(setup.Birth.OperationId));
            Assert.IsNull(await first.ReadNamespaceMemberAsync(setup.Birth.OperationId));
            Assert.IsNull(await first.ReadMutationAsync(setup.Birth.OperationId));
            var page = await first.ReadNamespaceBirthRecoveryPageAsync(await first.BeginNamespaceBirthRecoveryScanAsync(), 0, 1);
            Assert.IsTrue(page.Entries.Single().OwnsNameReservation);
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(setup.Completion, await second.ReadNamespaceBirthContentCompletionAsync(setup.Birth.OperationId));
        Assert.AreEqual(setup.Completion, await second.RecordNamespaceBirthContentCompletionAsync(setup.Completion));
        Assert.AreEqual(setup.Action, await second.ValidateNamespaceBirthRecoveryExecutionAsync(setup.Action.RecoveryId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    public async Task ImportBirthCannotPublishWithoutItsCompletedCopyOnTheOriginalObject()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareImportCopyAsync(catalog);
        var observation = ImportCopyObservation(setup);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthObservationAsync(observation));
        Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(setup.Birth.OperationId));
        await catalog.RecordNamespaceBirthContentCompletionAsync(setup.Completion);
        Assert.AreEqual(observation, await catalog.RecordNamespaceBirthObservationAsync(observation));
        Assert.AreEqual(setup.Completion, await catalog.ReadNamespaceBirthContentCompletionAsync(setup.Birth.OperationId));
    }

    [TestMethod]
    [DataRow("length")]
    [DataRow("digest")]
    [DataRow("binding")]
    [DataRow("owner")]
    [DataRow("descriptor")]
    [DataRow("time")]
    public async Task ImportCopyRequiresTheAdmittedInputOriginalObjectAndRoleOnlyState(string change)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareImportCopyAsync(catalog);
        var completion = setup.Completion;
        var invalid = change switch
        {
            "length" => completion with { Content = completion.Content with { Length = 4 } },
            "digest" => completion with { Content = completion.Content with { Sha256 = new('A', 64) } },
            "binding" => completion with { Verification = completion.Verification with { LocalObject = completion.Verification.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "owner" => completion with { Verification = completion.Verification with { OwnerSid = "S-1-5-21-1-2-3-1002" } },
            "descriptor" => completion with { Verification = completion.Verification with { Dacl = setup.Action.ConvertedDacl } },
            _ => completion with { Verification = completion.Verification with { ObservedAt = setup.Action.PreparedAt.AddTicks(-1) } }
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthContentCompletionAsync(invalid));
        Assert.IsNull(await catalog.ReadNamespaceBirthContentCompletionAsync(setup.Birth.OperationId));
        Assert.AreEqual(completion, await catalog.RecordNamespaceBirthContentCompletionAsync(completion));
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("placeholder")]
    [DataRow("missing-link")]
    [DataRow("alias")]
    [DataRow("negative-length")]
    [DataRow("lowercase-digest")]
    public async Task MalformedImportCopyCannotCreateCompletionOrReleaseItsReservation(string change)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareImportCopyAsync(catalog);
        var completion = setup.Completion;
        var invalid = change switch
        {
            "version" => completion with { Version = 2 },
            "placeholder" => completion with { IsPlaceholder = true },
            "missing-link" => completion with { LinkCount = 0 },
            "alias" => completion with { LinkCount = 2 },
            "negative-length" => completion with { Content = completion.Content with { Length = -1 } },
            _ => completion with { Content = completion.Content with { Sha256 = completion.Content.Sha256.ToLowerInvariant() } }
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.RecordNamespaceBirthContentCompletionAsync(invalid));
        Assert.IsNull(await catalog.ReadNamespaceBirthContentCompletionAsync(setup.Birth.OperationId));
        var page = await catalog.ReadNamespaceBirthRecoveryPageAsync(await catalog.BeginNamespaceBirthRecoveryScanAsync(), 0, 1);
        Assert.IsTrue(page.Entries.Single().OwnsNameReservation);
    }

    [TestMethod]
    public async Task OriginalImportCopyCannotRefreshItsTimeOrReplaceItsAction()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareImportCopyAsync(catalog);
        await catalog.RecordNamespaceBirthContentCompletionAsync(setup.Completion);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthContentCompletionAsync(setup.Completion with
        { Verification = setup.Completion.Verification with { ObservedAt = setup.Completion.Verification.ObservedAt.AddSeconds(1) } }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthContentCompletionAsync(setup.Completion with
        { RecoveryId = Guid.NewGuid() }));
        Assert.AreEqual(setup.Completion, await catalog.ReadNamespaceBirthContentCompletionAsync(setup.Birth.OperationId));
    }

    [TestMethod]
    public async Task CatalogUpgradeKeepsImportIncompleteWithoutInventingCopiedBytes()
    {
        using var fixture = new Fixture();
        ImportCopyFixture setup;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) setup = await PrepareImportCopyAsync(first);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_birth_content_completions; PRAGMA user_version=40;");
        await using var upgraded = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(setup.Plan, await upgraded.ReadNamespaceBirthPlanAsync(setup.Birth.OperationId));
        Assert.AreEqual(setup.Conversion, await upgraded.ReadNamespaceBirthConversionAsync(setup.Birth.OperationId));
        Assert.IsNull(await upgraded.ReadNamespaceBirthContentCompletionAsync(setup.Birth.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => upgraded.RecordNamespaceBirthObservationAsync(ImportCopyObservation(setup)));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task RecomputedReceiptFingerprintCannotAdoptAnotherObject()
    {
        using var fixture = new Fixture();
        ImportCopyFixture setup;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareImportCopyAsync(first);
            await first.RecordNamespaceBirthContentCompletionAsync(setup.Completion);
        }
        var invalid = setup.Completion with
        {
            Verification = setup.Completion.Verification with
            { LocalObject = setup.Completion.Verification.LocalObject with { LocalFileId = Guid.NewGuid() } }
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(invalid);
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE namespace_birth_content_completions SET payload=$payload,fingerprint=$fingerprint;";
            update.Parameters.AddWithValue("$payload", payload);
            update.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await update.ExecuteNonQueryAsync();
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceBirthContentCompletionAsync(setup.Birth.OperationId));
        Assert.AreEqual(setup.Conversion, await second.ReadNamespaceBirthConversionAsync(setup.Birth.OperationId));
    }

    private sealed record ImportCopyFixture(MirrorPulseNamespaceBirthIntent Birth, MirrorPulseNamespaceBirthPlan Plan,
        MirrorPulseNamespaceBirthStart Start, MirrorPulseNamespaceBirthConversionPreparation Conversion,
        MirrorPulseNamespaceBirthRecoveryAction Action, MirrorPulseNamespaceBirthContentCompletion Completion);

    private static async Task<ImportCopyFixture> PrepareImportCopyAsync(MirrorPulseProductCatalog catalog)
    {
        var parent = Baseline(); var protection = Protection(parent);
        var birth = Birth(parent, protection) with { Origin = MirrorPulseNamespaceBirthOrigin.Import };
        var plan = Plan(birth) with { Version = 2, Content = new(3, Convert.ToHexString(SHA256.HashData([1, 2, 3]))) };
        var start = Start(plan); var conversion = Conversion(birth, start);
        await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
        var action = RecoveryAction(conversion, protection);
        await catalog.PrepareNamespaceBirthRecoveryActionAsync(action);
        var completion = new MirrorPulseNamespaceBirthContentCompletion(1, birth.OperationId, action.RecoveryId, plan.Content!,
            new(conversion.LocalObject, conversion.OwnerSid, action.RoleOnlyDacl, action.PreparedAt.AddSeconds(1)), false, 1);
        return new(birth, plan, start, conversion, action, completion);
    }

    private static MirrorPulseNamespaceBirthObservation ImportCopyObservation(ImportCopyFixture setup) =>
        Observation(setup.Birth, setup.Plan, setup.Start) with
        {
            LocalObject = setup.Conversion.LocalObject,
            ObservedAt = setup.Completion.Verification.ObservedAt.AddSeconds(1),
            BirthDacl = setup.Action.ConvertedDacl
        };
}
