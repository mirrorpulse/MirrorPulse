using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespacePermissionCoordinatorTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProtectedPreparationRetainsIdentityBeforeNativeWorkAndPreservesKnownOfficialIds(bool known)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var candidate = new CloudPlaceholderIdentity(Guid.NewGuid(), "host-selected-local");
        var existing = new CloudPlaceholderIdentity(Guid.NewGuid(), "known-official");
        var lease = new ProtectedObjectLease(fixture)
        {
            Identity = known ? new(existing.ItemId, existing.RemoteId, false, null) : new(null, null, false, null),
        };
        lease.BeforeConversion = async identity =>
        {
            var retained = (await catalog.ReadNamespacePermissionLocalIdentityAsync(fixture.Baseline.EvidenceId))!;
            Assert.AreEqual(identity.ItemId, retained.ItemId);
            Assert.AreEqual(identity.RemoteId, retained.RemoteId);
            Assert.AreEqual(fixture.Baseline.LocalObject, retained.LocalObject);
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, (await catalog.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId))!.Phase);
            Assert.AreEqual(0, lease.Object.Writes);
            Assert.AreEqual(fixture.Baseline.OriginalDacl, lease.Object.Current.Dacl);
            Assert.IsNull(identity.RemoteRevision);
        };
        var result = await ApplyProtectedFixtureAsync(coordinator, fixture.Intent.OperationId, lease, candidate);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, result.Outcome);
        Assert.AreEqual(known ? existing.ItemId : candidate.ItemId, lease.ConvertedIdentity!.ItemId);
        Assert.AreEqual(known ? existing.RemoteId : candidate.RemoteId, lease.ConvertedIdentity.RemoteId);
        Assert.AreEqual(1, lease.Preparations);
        Assert.AreEqual(1, lease.Object.Writes);
        Assert.AreEqual(Target, lease.Object.Current.Dacl);
        Assert.IsFalse(lease.Disposed);
    }

    [TestMethod]
    public async Task AcceptedPlaceholderUsesAccessOnlyAndRetainsItsAcceptedIdentity()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var accepted = new CloudPlaceholderIdentity(Guid.NewGuid(), "accepted-official", "accepted-v1");
        var lease = new ProtectedObjectLease(fixture) { Identity = new(accepted.ItemId, accepted.RemoteId, true, accepted) };
        var result = await ApplyProtectedFixtureAsync(coordinator, fixture.Intent.OperationId, lease, null);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, result.Outcome);
        Assert.AreEqual(0, lease.Preparations);
        Assert.AreSame(accepted, lease.Identity.PlaceholderIdentity);
        var retained = (await catalog.ReadNamespacePermissionLocalIdentityAsync(fixture.Baseline.EvidenceId))!;
        Assert.AreEqual(accepted.ItemId, retained.ItemId);
        Assert.AreEqual(accepted.RemoteId, retained.RemoteId);
    }

    [TestMethod]
    [DataRow("binding")]
    [DataRow("owner")]
    [DataRow("descriptor")]
    public async Task OriginalMismatchRejectsIdentityPreparationAndEveryNativeMutation(string scenario)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var lease = new ProtectedObjectLease(fixture);
        lease.Object.Current = scenario switch
        {
            "binding" => lease.Object.Current with { LocalObject = fixture.Baseline.LocalObject with { LocalFileId = Guid.NewGuid() } },
            "owner" => lease.Object.Current with { OwnerSid = "S-1-5-21-100-200-300-1002" },
            _ => lease.Object.Current with { Dacl = Unowned },
        };
        var result = await ApplyProtectedFixtureAsync(coordinator, fixture.Intent.OperationId, lease, new(Guid.NewGuid(), "candidate"));
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.RecoveryRequired, result.Outcome);
        Assert.AreEqual(0, lease.IdentityReads);
        Assert.AreEqual(0, lease.Preparations);
        Assert.AreEqual(0, lease.Object.Writes);
        Assert.IsNull(await catalog.ReadNamespacePermissionLocalIdentityAsync(fixture.Baseline.EvidenceId));
        Assert.AreEqual(fixture.Baseline, await catalog.ReadNamespacePermissionBaselineAsync(fixture.Baseline.EvidenceId));
    }

    [TestMethod]
    [DataRow("partial-official")]
    [DataRow("empty-official")]
    [DataRow("missing-native")]
    [DataRow("different-native")]
    public async Task InconsistentOfficialAndNativeIdentityCannotBeRepairedByAReplacementCandidate(string scenario)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        Guid item = Guid.NewGuid();
        var lease = new ProtectedObjectLease(fixture)
        {
            Identity = scenario switch
            {
                "partial-official" => new(item, null, false, null),
                "empty-official" => new(Guid.Empty, "known", false, null),
                "missing-native" => new(item, "known", true, null),
                _ => new(item, "known", true, new(Guid.NewGuid(), "known")),
            },
        };
        var result = await ApplyProtectedFixtureAsync(coordinator, fixture.Intent.OperationId, lease, new(Guid.NewGuid(), "replacement"));
        Assert.AreEqual(MirrorPulseNamespacePermissionRecoveryReason.ObjectChanged, result.RecoveryReason);
        Assert.AreEqual(0, lease.Preparations);
        Assert.AreEqual(0, lease.Object.Writes);
        Assert.IsNull(await catalog.ReadNamespacePermissionLocalIdentityAsync(fixture.Baseline.EvidenceId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InterruptedConversionKeepsOriginalIdentityAcrossRestartAndNeverWritesPermissions(bool nativeApplied)
    {
        using var fixture = new Fixture();
        var candidate = new CloudPlaceholderIdentity(Guid.NewGuid(), "original-local");
        var lease = new ProtectedObjectLease(fixture) { FailConversion = true, FailAfterConversion = nativeApplied };
        MirrorPulseNamespacePermissionLocalIdentity retained;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await first.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(first);
            await Assert.ThrowsExactlyAsync<IOException>(() => ApplyProtectedFixtureAsync(coordinator, fixture.Intent.OperationId, lease, candidate));
            retained = (await first.ReadNamespacePermissionLocalIdentityAsync(fixture.Baseline.EvidenceId))!;
            Assert.AreEqual(candidate.ItemId, retained.ItemId);
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, (await first.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId))!.Phase);
            Assert.AreEqual(0, lease.Object.Writes);
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var recovered = new MirrorPulseNamespacePermissionCoordinator(second);
        lease.FailConversion = false;
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, (await ApplyProtectedFixtureAsync(recovered,
            fixture.Intent.OperationId, lease, new(Guid.NewGuid(), "different-later-candidate"))).Outcome);
        Assert.AreEqual(retained, await second.ReadNamespacePermissionLocalIdentityAsync(fixture.Baseline.EvidenceId));
        Assert.AreEqual(candidate.ItemId, lease.Identity.ItemId);
        Assert.AreEqual(candidate.RemoteId, lease.Identity.RemoteId);
        Assert.AreEqual(2, lease.Preparations, "Native presence still requires replay of the public durable projection preparation.");
        Assert.AreEqual(1, lease.Object.Writes);
    }

    [TestMethod]
    public async Task VerificationInterruptionRetainsAppliedPermissionAndRestartsWithoutRekeyOrRewrite()
    {
        using var fixture = new Fixture();
        var lease = new ProtectedObjectLease(fixture);
        var candidate = new CloudPlaceholderIdentity(Guid.NewGuid(), "original-local");
        MirrorPulseNamespacePermissionLocalIdentity retained;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await first.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(first);
            lease.Object.BeforeRead = count =>
            {
                if (count == 3) throw new IOException("Synthetic independent permission read-back failure.");
                return Task.CompletedTask;
            };
            await Assert.ThrowsExactlyAsync<IOException>(() => ApplyProtectedFixtureAsync(coordinator, fixture.Intent.OperationId, lease, candidate));
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Applied, (await first.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId))!.Phase);
            retained = (await first.ReadNamespacePermissionLocalIdentityAsync(fixture.Baseline.EvidenceId))!;
            Assert.AreEqual(1, lease.Object.Writes);
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var recovered = new MirrorPulseNamespacePermissionCoordinator(second);
        lease.Object.BeforeRead = null;
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, (await ApplyProtectedFixtureAsync(recovered,
            fixture.Intent.OperationId, lease, null)).Outcome);
        Assert.AreEqual(2, lease.Preparations, "Unaccepted native identity does not prove its earlier projection committed.");
        Assert.AreEqual(1, lease.Object.Writes);
        Assert.AreEqual(retained, await second.ReadNamespacePermissionLocalIdentityAsync(fixture.Baseline.EvidenceId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LegacyAppliedOrVerifiedHistoryCannotInventMissingOriginalIdentity(bool verified)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        await catalog.RecordNamespacePermissionApplicationAsync(fixture.Intent.OperationId, fixture.Baseline.LocalObject, DateTimeOffset.UtcNow);
        if (verified) await catalog.VerifyNamespacePermissionChangeAsync(fixture.Intent.OperationId,
            new(fixture.Baseline.LocalObject, Owner, Target, DateTimeOffset.UtcNow));
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var lease = new ProtectedObjectLease(fixture);
        lease.Object.Current = lease.Object.Current with { Dacl = Target };
        var result = await ApplyProtectedFixtureAsync(coordinator, fixture.Intent.OperationId, lease, new(Guid.NewGuid(), "current-path"));
        Assert.AreEqual(MirrorPulseNamespacePermissionRecoveryReason.Interrupted, result.RecoveryReason);
        Assert.AreEqual(0, lease.IdentityReads);
        Assert.AreEqual(0, lease.Preparations);
        Assert.AreEqual(0, lease.Object.Writes);
        Assert.IsNull(await catalog.ReadNamespacePermissionLocalIdentityAsync(fixture.Baseline.EvidenceId));
        Assert.AreEqual(verified ? MirrorPulseNamespacePermissionPhase.Verified : MirrorPulseNamespacePermissionPhase.RecoveryRequired,
            (await catalog.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId))!.Phase);
    }

    [TestMethod]
    public async Task RecoveryRequiredNeverInspectsOrPreparesTheOriginal()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        await catalog.RequireNamespacePermissionRecoveryAsync(fixture.Intent.OperationId, MirrorPulseNamespacePermissionRecoveryReason.Interrupted);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var lease = new ProtectedObjectLease(fixture);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.RecoveryRequired,
            (await ApplyProtectedFixtureAsync(coordinator, fixture.Intent.OperationId, lease, null)).Outcome);
        Assert.AreEqual(0, lease.Object.Reads);
        Assert.AreEqual(0, lease.IdentityReads);
        Assert.AreEqual(0, lease.Preparations);
        Assert.AreEqual(0, lease.Object.Writes);
    }

    [TestMethod]
    public async Task DirectoryMetadataWorkDoesNotCreateAnyFileIdentity()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var baseline = fixture.Baseline with { IsDirectory = true, RelativePath = "Docs" };
        var intent = fixture.Intent with { RelativePath = "Docs" };
        await catalog.PrepareNamespacePermissionChangeAsync(baseline, intent);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var lease = new ProtectedObjectLease(fixture);
        lease.Object.Current = lease.Object.Current with { IsDirectory = true, RelativePath = "Docs" };
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified,
            (await ApplyProtectedFixtureAsync(coordinator, intent.OperationId, lease, null)).Outcome);
        Assert.AreEqual(0, lease.IdentityReads);
        Assert.AreEqual(0, lease.Preparations);
        Assert.AreEqual(1, lease.Object.Writes);
        Assert.IsNull(await catalog.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId));
    }

    [TestMethod]
    public async Task VerifiedOrdinaryEditIsRepreparedWithOriginalIdentityWithoutAnotherPermissionWrite()
    {
        using var fixture = new Fixture();
        var candidate = new CloudPlaceholderIdentity(Guid.NewGuid(), "original-local");
        var lease = new ProtectedObjectLease(fixture);
        MirrorPulseNamespacePermissionLocalIdentity retained;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await first.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(first);
            Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified,
                (await ApplyProtectedFixtureAsync(coordinator, fixture.Intent.OperationId, lease, candidate)).Outcome);
            retained = (await first.ReadNamespacePermissionLocalIdentityAsync(fixture.Baseline.EvidenceId))!;
        }
        lease.Identity = new(candidate.ItemId, candidate.RemoteId, false, null);
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var recovered = new MirrorPulseNamespacePermissionCoordinator(second);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.AlreadyVerified,
            (await ApplyProtectedFixtureAsync(recovered, fixture.Intent.OperationId, lease, new(Guid.NewGuid(), "replacement"))).Outcome);
        Assert.AreEqual(retained, await second.ReadNamespacePermissionLocalIdentityAsync(fixture.Baseline.EvidenceId));
        Assert.AreEqual(2, lease.Preparations);
        Assert.AreEqual(1, lease.Object.Writes);
        Assert.AreEqual(candidate.ItemId, lease.ConvertedIdentity!.ItemId);
    }

    [TestMethod]
    public async Task RetainedIdentityCannotAdoptDifferentOfficialIdsOnTheSameNativeObject()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        var retained = new MirrorPulseNamespacePermissionLocalIdentity(1, fixture.Baseline.EvidenceId,
            fixture.Intent.OperationId, fixture.Baseline.LocalObject, Guid.NewGuid(), "original-local", DateTimeOffset.UtcNow);
        await catalog.PrepareNamespacePermissionLocalIdentityAsync(retained);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var lease = new ProtectedObjectLease(fixture) { Identity = new(Guid.NewGuid(), "replacement-official", false, null) };
        Assert.AreEqual(MirrorPulseNamespacePermissionRecoveryReason.ObjectChanged,
            (await ApplyProtectedFixtureAsync(coordinator, fixture.Intent.OperationId, lease, null)).RecoveryReason);
        Assert.AreEqual(0, lease.Preparations);
        Assert.AreEqual(0, lease.Object.Writes);
        Assert.AreEqual(retained, await catalog.ReadNamespacePermissionLocalIdentityAsync(fixture.Baseline.EvidenceId));
    }

    [TestMethod]
    public async Task UnknownOriginalWithoutHostIdentityCannotConvertOrPersistAnInventedIdentity()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var lease = new ProtectedObjectLease(fixture);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ApplyProtectedFixtureAsync(coordinator, fixture.Intent.OperationId, lease, null));
        Assert.AreEqual(0, lease.Preparations);
        Assert.AreEqual(0, lease.Object.Writes);
        Assert.IsNull(await catalog.ReadNamespacePermissionLocalIdentityAsync(fixture.Baseline.EvidenceId));
        Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, (await catalog.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId))!.Phase);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedProjectionReplayOnAnExistingLocalPlaceholderCannotStartPermissionWork(bool previouslyVerified)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var identity = new CloudPlaceholderIdentity(Guid.NewGuid(), "retained-local");
        var retained = new MirrorPulseNamespacePermissionLocalIdentity(1, fixture.Baseline.EvidenceId,
            fixture.Intent.OperationId, fixture.Baseline.LocalObject, identity.ItemId, identity.RemoteId, DateTimeOffset.UtcNow);
        await catalog.PrepareNamespacePermissionLocalIdentityAsync(retained);
        var lease = new ProtectedObjectLease(fixture) { Identity = new(identity.ItemId, identity.RemoteId, true, identity), FailConversion = true };
        if (previouslyVerified)
        {
            await catalog.RecordNamespacePermissionApplicationAsync(fixture.Intent.OperationId, fixture.Baseline.LocalObject, DateTimeOffset.UtcNow);
            await catalog.VerifyNamespacePermissionChangeAsync(fixture.Intent.OperationId,
                new(fixture.Baseline.LocalObject, Owner, Target, DateTimeOffset.UtcNow));
            lease.Object.Current = lease.Object.Current with { Dacl = Target };
        }
        var before = (await catalog.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId))!;
        await Assert.ThrowsExactlyAsync<IOException>(() => ApplyProtectedFixtureAsync(coordinator, fixture.Intent.OperationId, lease, null));
        Assert.AreEqual(1, lease.Preparations);
        Assert.AreEqual(0, lease.Object.Writes);
        Assert.AreEqual(before, await catalog.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId));
        Assert.AreEqual(retained, await catalog.ReadNamespacePermissionLocalIdentityAsync(fixture.Baseline.EvidenceId));
        lease.FailConversion = false;
        var recovered = await ApplyProtectedFixtureAsync(coordinator, fixture.Intent.OperationId, lease, null);
        Assert.AreEqual(previouslyVerified ? MirrorPulseNamespacePermissionOutcome.AlreadyVerified : MirrorPulseNamespacePermissionOutcome.Verified, recovered.Outcome);
        Assert.AreEqual(2, lease.Preparations);
        Assert.AreEqual(previouslyVerified ? 0 : 1, lease.Object.Writes);
    }

    private static Task<MirrorPulseNamespacePermissionResult> ApplyProtectedFixtureAsync(
        MirrorPulseNamespacePermissionCoordinator coordinator, Guid operationId,
        ProtectedObjectLease lease, CloudPlaceholderIdentity? candidate) =>
        coordinator.RunAdmittedPermissionWorkAsync(operationId,
            (change, baseline, stop) => coordinator.ApplyWithinProtectionAsync(change, baseline, lease, candidate, stop));

    private sealed class ProtectedObjectLease(Fixture fixture) : IMirrorPulseProtectedNamespacePermissionLease
    {
        public ObjectLease Object { get; } = new(fixture.Baseline, fixture.Intent);
        public MirrorPulseProtectedLocalIdentityObservation Identity { get; set; } = new(null, null, false, null);
        public int IdentityReads { get; private set; }
        public int Preparations { get; private set; }
        public bool FailConversion { get; set; }
        public bool FailAfterConversion { get; set; }
        public bool Disposed { get; private set; }
        public CloudPlaceholderIdentity? ConvertedIdentity { get; private set; }
        public Func<CloudPlaceholderIdentity, Task>? BeforeConversion { get; set; }
        public ValueTask<MirrorPulseNamespacePermissionObject> InspectAsync(CancellationToken cancellationToken) => Object.InspectAsync(cancellationToken);
        public ValueTask ApplyDaclAsync(string dacl, CancellationToken cancellationToken) => Object.ApplyDaclAsync(dacl, cancellationToken);
        public ValueTask<MirrorPulseProtectedLocalIdentityObservation> InspectLocalIdentityAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IdentityReads++;
            return ValueTask.FromResult(Identity);
        }
        public async ValueTask PrepareLocalIdentityAsync(CloudPlaceholderIdentity identity, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Preparations++;
            if (BeforeConversion is not null) await BeforeConversion(identity);
            if (!FailConversion || FailAfterConversion)
            {
                ConvertedIdentity = identity;
                Identity = new(identity.ItemId, identity.RemoteId, true, identity);
            }
            if (FailConversion) throw new IOException("Synthetic local conversion or required projection failure.");
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
