using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceAnchorPermissionPreparationTests
{
    [TestMethod]
    [DataRow("S-1-1-0", "D:(A;;FA;;;WD)")]
    [DataRow("S-1-5-18", "D:(A;;FA;;;SY)")]
    public void OriginalOwnerDaclRetainsWindowsCanonicalSidAliases(string owner, string expected)
    {
        string descriptor = OriginalOwnerDacl(new SecurityIdentifier(owner));
        Assert.AreEqual(expected, descriptor);
        Assert.AreEqual(descriptor, new RawSecurityDescriptor(descriptor).GetSddlForm(AccessControlSections.Access));
    }

    [TestMethod]
    public async Task AnchorIntentIsBuiltAndPersistedAsHostOwnerBeforeReturningToNamespaceRole()
    {
        using var fixture = new CatalogFixture();
        await using var execution = new MirrorPulseNamespaceExecutionSession();
        var baseline = Baseline(execution);
        MirrorPulseNamespacePermissionChange retained = null!;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await execution.RunNamespaceOperationAsync(async () =>
            {
                AssertIdentity(execution, namespaceRole: true);
                retained = await MirrorPulseNamespaceAnchorProtectionCoordinator.PrepareInHostOwnerAsync(catalog, execution, baseline,
                    () => IntentAsHostOwner(execution, baseline), CancellationToken.None);
                await Task.Yield();
                AssertIdentity(execution, namespaceRole: true);
                Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, retained.Phase);
            });
            AssertIdentity(execution, namespaceRole: false);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(baseline, await reopened.ReadNamespacePermissionBaselineAsync(baseline.EvidenceId));
        Assert.AreEqual(retained, await reopened.ReadNamespacePermissionChangeAsync(retained.Intent.OperationId));
        Assert.HasCount(1, await reopened.ReadNamespacePermissionChangesAsync());
        fixture.AssertNoCloudFilesState();
    }

    [TestMethod]
    [DataRow("policy")]
    [DataRow("catalog")]
    [DataRow("cancelled")]
    public async Task FailedPreparationRestoresNamespaceRoleAndLeavesNoPartialOriginalEvidence(string failure)
    {
        using var fixture = new CatalogFixture();
        await using var execution = new MirrorPulseNamespaceExecutionSession();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        if (failure == "catalog") await fixture.FailIntentInsertAsync();
        var baseline = Baseline(execution);
        var original = new FileNotFoundException("Synthetic policy construction failure.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await execution.RunNamespaceOperationAsync(async () =>
        {
            Task PrepareAsync() => MirrorPulseNamespaceAnchorProtectionCoordinator.PrepareInHostOwnerAsync(catalog, execution, baseline, () =>
            {
                var intent = IntentAsHostOwner(execution, baseline);
                if (failure == "policy") throw original;
                return intent;
            }, failure == "cancelled" ? cancelled.Token : CancellationToken.None);

            Exception error = failure switch
            {
                "policy" => await Assert.ThrowsExactlyAsync<FileNotFoundException>(PrepareAsync),
                "catalog" => await Assert.ThrowsExactlyAsync<SqliteException>(PrepareAsync),
                _ => await Assert.ThrowsAsync<OperationCanceledException>(PrepareAsync)
            };
            AssertIdentity(execution, namespaceRole: true);
            if (failure == "policy") Assert.AreSame(original, error);
            Assert.AreEqual(failure == "policy" ? MirrorPulseStartupFailureSite.AnchorPermissionPolicy :
                MirrorPulseStartupFailureSite.AnchorPermissionCatalog, MirrorPulseStartupFailureTrace.ReadSite(error));
            MirrorPulseStartupFailureTrace.Record(error, MirrorPulseStartupFailureSite.AnchorPermissionPreparation);
            Assert.AreEqual(failure == "policy" ? MirrorPulseStartupFailureSite.AnchorPermissionPolicy :
                MirrorPulseStartupFailureSite.AnchorPermissionCatalog, MirrorPulseStartupFailureTrace.ReadSite(error));
            Assert.IsNull(await catalog.ReadNamespacePermissionBaselineAsync(baseline.EvidenceId));
            Assert.HasCount(0, await catalog.ReadNamespacePermissionChangesAsync());
        });
        AssertIdentity(execution, namespaceRole: false);
        await catalog.DisposeAsync();
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsNull(await reopened.ReadNamespacePermissionBaselineAsync(baseline.EvidenceId));
        Assert.HasCount(0, await reopened.ReadNamespacePermissionChangesAsync());
        fixture.AssertNoCloudFilesState();
    }

    private static MirrorPulseNamespacePermissionBaseline Baseline(MirrorPulseNamespaceExecutionSession execution)
    {
        Guid anchor = Guid.NewGuid();
        return new(Guid.NewGuid(), null, new(1, anchor, anchor), string.Empty, true,
            execution.OwnerSid.Value, OriginalOwnerDacl(execution.OwnerSid), DateTimeOffset.UtcNow);
    }

    private static string OriginalOwnerDacl(SecurityIdentifier owner) =>
        new RawSecurityDescriptor($"D:(A;;FA;;;{owner.Value})").GetSddlForm(AccessControlSections.Access);

    private static MirrorPulseNamespacePermissionIntent IntentAsHostOwner(MirrorPulseNamespaceExecutionSession execution,
        MirrorPulseNamespacePermissionBaseline baseline)
    {
        AssertIdentity(execution, namespaceRole: false);
        return new(Guid.NewGuid(), baseline.EvidenceId, baseline.LocalObject, null, string.Empty,
            MirrorPulseNamespacePermissionChangeKind.Protect, execution.RoleSid.Value, baseline.OriginalDacl,
            MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(execution, isDirectory: true), DateTimeOffset.UtcNow);
    }

    private static void AssertIdentity(MirrorPulseNamespaceExecutionSession execution, bool namespaceRole)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        Assert.AreEqual(execution.OwnerSid, identity.User);
        Assert.AreEqual(namespaceRole, new WindowsPrincipal(identity).IsInRole(execution.RoleSid));
    }

    private sealed class CatalogFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-anchor-preparation", Guid.NewGuid().ToString("N"));
        public MirrorPulseStoragePaths Paths { get; }
        public CatalogFixture() => Paths = new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data"));

        public async Task FailIntentInsertAsync()
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Paths.ProductCatalogDatabasePath,
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER fail_anchor_intent BEFORE INSERT ON namespace_permission_changes
                BEGIN SELECT RAISE(ABORT, 'Synthetic permission intent insert failure.'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        public void AssertNoCloudFilesState()
        {
            Assert.IsFalse(Directory.Exists(Paths.SyncRootPath));
            Assert.IsFalse(File.Exists(Paths.CfSharpStateDatabasePath));
        }

        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
}
