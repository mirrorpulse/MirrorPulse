using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task CurrentRecoveryExecutionAdmissionSurvivesOwnersWithoutClaimingNativeCompletion()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var action = RecoveryAction(conversion, protection);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareStagedRecoveryAsync(first, parent, protection, birth, plan, start, conversion);
            await first.PrepareNamespaceBirthRecoveryActionAsync(action);
            Assert.AreEqual(action, await first.ValidateNamespaceBirthRecoveryExecutionAsync(action.RecoveryId));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(action, await second.ValidateNamespaceBirthRecoveryExecutionAsync(action.RecoveryId));
        Assert.IsNull(await second.ReadNamespaceBirthRecoveryVerificationAsync(action.RecoveryId));
        Assert.IsNull(await second.ReadNamespaceBirthObservationAsync(birth.OperationId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("superseded")]
    [DataRow("rotated")]
    [DataRow("observed")]
    [DataRow("capture")]
    [DataRow("reservation")]
    public async Task HistoricalRecoveryReplayDoesNotAuthorizeStaleNativeExecution(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var action = RecoveryAction(conversion, protection);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
        await catalog.PrepareNamespaceBirthRecoveryActionAsync(action);
        Assert.AreEqual(action, await catalog.ValidateNamespaceBirthRecoveryExecutionAsync(action.RecoveryId));
        if (scenario == "superseded") await catalog.PrepareNamespaceBirthRecoveryActionAsync(action with
        { RecoveryId = Guid.NewGuid(), PreviousRecoveryId = action.RecoveryId, PreparedAt = action.PreparedAt.AddSeconds(1) });
        else if (scenario == "observed") await catalog.RecordNamespaceBirthObservationAsync(Observation(birth, plan, start) with
        { LocalObject = conversion.LocalObject, ObservedAt = action.PreparedAt.AddSeconds(1) });
        else if (scenario == "reservation") await ExecuteSqlAsync(fixture.Paths, "DELETE FROM namespace_birth_reservations;");
        else
        {
            var rotation = protection with
            {
                OperationId = Guid.NewGuid(),
                Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
                RoleSid = "S-1-5-5-987-654",
                ExpectedDacl = protection.TargetDacl,
                TargetDacl = protection.TargetDacl.Replace(Role, "S-1-5-5-987-654", StringComparison.Ordinal),
                PreparedAt = action.PreparedAt.AddSeconds(1),
            };
            if (scenario == "rotated") await ProtectAsync(catalog, parent, rotation);
            else await catalog.CreateNamespacePermissionTreeAsync(new(Guid.NewGuid(), new(parent, rotation), 1, action.PreparedAt));
        }
        Assert.AreEqual(action, await catalog.PrepareNamespaceBirthRecoveryActionAsync(action));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceBirthRecoveryExecutionAsync(action.RecoveryId));
        Assert.IsNull(await catalog.ReadNamespaceBirthRecoveryVerificationAsync(action.RecoveryId));
        Assert.AreEqual(conversion, await catalog.ReadNamespaceBirthConversionAsync(birth.OperationId));
    }

    [TestMethod]
    public async Task RecoveryExecutionCannotInventAMissingAction()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.ValidateNamespaceBirthRecoveryExecutionAsync(Guid.NewGuid()));
    }

    [TestMethod]
    public async Task CanceledRecoveryExecutionAdmissionLeavesItsOriginalActionPending()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var protection = Protection(parent); var birth = Birth(parent, protection);
        var plan = Plan(birth); var start = Start(plan); var conversion = Conversion(birth, start);
        var action = RecoveryAction(conversion, protection);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStagedRecoveryAsync(catalog, parent, protection, birth, plan, start, conversion);
        await catalog.PrepareNamespaceBirthRecoveryActionAsync(action);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.ValidateNamespaceBirthRecoveryExecutionAsync(action.RecoveryId, canceled.Token));
        Assert.AreEqual(action, await catalog.ReadNamespaceBirthRecoveryActionAsync(action.RecoveryId));
        Assert.IsNull(await catalog.ReadNamespaceBirthRecoveryVerificationAsync(action.RecoveryId));
    }
}
