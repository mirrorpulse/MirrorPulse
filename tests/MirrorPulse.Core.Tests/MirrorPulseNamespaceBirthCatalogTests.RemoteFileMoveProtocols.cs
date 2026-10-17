using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    private const string ProtocolSource = "3caa382e341d260543d3afc05e25fa034fb32e16";
    private static MirrorPulseRemoteFileMovePolicy OriginalFileMovePolicy => new(true, true, TimeSpan.FromMinutes(2).Ticks, 1);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FileMoveProtocolAdmissionRetainsTheOriginalStartAndPolicyAcrossOwners(bool nullBaseline)
    {
        using var fixture = new Fixture(); MoveFixture setup; MirrorPulseRemoteFileMoveProtocolAdmission original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRemoteRejectableMoveAsync(catalog, start: false);
            var start = new MirrorPulseNamespaceMemberMoveStart(2, setup.Move.OperationId, setup.Move.PreparedAt)
            { OriginalOfficialRemoteRevision = nullBaseline ? null : "actual-before-dispatch" };
            await catalog.RecordNamespaceMemberMoveStartAsync(start);
            original = await catalog.AdmitNamespaceRemoteFileMoveProtocolAsync(setup.Move.OperationId,
                "0.1.0-preview.6", ProtocolSource, OriginalFileMovePolicy, setup.Move.PreparedAt.AddSeconds(1));
            Assert.AreEqual(start, original.OriginalStart);
            Assert.AreEqual(MirrorPulseProductCatalog.CfSharpRemoteFileMoveProtocol, original.Protocol);
            Assert.AreEqual(original, await catalog.AdmitNamespaceRemoteFileMoveProtocolAsync(setup.Move.OperationId,
                "0.1.0-preview.6", ProtocolSource, OriginalFileMovePolicy, setup.Move.PreparedAt.AddSeconds(1)));
            Assert.IsNull(await catalog.ReadNamespaceMemberMoveObservationAsync(setup.Move.OperationId));
            Assert.IsNull(await catalog.ReadNamespaceMemberMoveProjectionAsync(setup.Move.OperationId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(original, await reopened.ReadNamespaceRemoteFileMoveProtocolAsync(setup.Move.OperationId));
        Assert.AreEqual(original.OriginalStart, await reopened.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Move, await reopened.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual(setup.Move.Source.RelativePath, (await reopened.ReadNamespaceMemberLocationAsync(setup.Member.MemberId)).RelativePath);
        Assert.IsNull(await reopened.ReadNamespaceMemberMoveCompletionAsync(setup.Move.OperationId));
        Assert.IsNull(await reopened.ReadNamespaceRemoteMoveRejectionAsync(setup.Move.OperationId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("preserve")]
    [DataRow("suppress")]
    [DataRow("lifetime")]
    [DataRow("observations")]
    [DataRow("source")]
    public async Task AnExistingFileProtocolCannotReplaceItsOriginalLibraryOrPolicy(string change)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareProtocolFileAsync(catalog);
        var original = await catalog.AdmitNamespaceRemoteFileMoveProtocolAsync(setup.Move.OperationId, "0.1.0-preview.6", ProtocolSource, OriginalFileMovePolicy, setup.Move.PreparedAt.AddSeconds(1));
        var altered = change switch
        {
            "preserve" => OriginalFileMovePolicy with { PreserveUnsynchronizedLocalContent = false },
            "suppress" => OriginalFileMovePolicy with { SuppressLocalEcho = false },
            "lifetime" => OriginalFileMovePolicy with { EchoSuppressionLifetimeTicks = TimeSpan.FromMinutes(3).Ticks },
            "observations" => OriginalFileMovePolicy with { EchoSuppressionObservationCount = 2 },
            _ => OriginalFileMovePolicy
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.AdmitNamespaceRemoteFileMoveProtocolAsync(setup.Move.OperationId,
            "0.1.0-preview.6", change == "source" ? new string('a', 40) : ProtocolSource, altered, setup.Move.PreparedAt.AddSeconds(1)));
        Assert.AreEqual(original, await catalog.ReadNamespaceRemoteFileMoveProtocolAsync(setup.Move.OperationId));
    }

    [TestMethod]
    [DataRow("unstarted")]
    [DataRow("legacy-start")]
    [DataRow("ordinary")]
    [DataRow("directory")]
    [DataRow("rejected")]
    public async Task AProtocolAdmissionCannotAdoptUnownedOrAlreadySettledWork(string invalid)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRemoteRejectableMoveAsync(catalog, directory: invalid == "directory", start: false, remote: invalid != "ordinary");
        if (invalid != "unstarted")
            await catalog.RecordNamespaceMemberMoveStartAsync(new(invalid is "ordinary" or "legacy-start" ? 1 : 2,
                setup.Move.OperationId, setup.Move.PreparedAt)
            { OriginalOfficialRemoteRevision = invalid == "rejected" ? setup.Move.RemoteEntry!.PreviousRemoteRevision : null });
        if (invalid == "rejected") await catalog.RecordNamespaceRemoteMoveRejectionAsync(RemoteRejection(setup));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.AdmitNamespaceRemoteFileMoveProtocolAsync(setup.Move.OperationId,
            "0.1.0-preview.6", ProtocolSource, OriginalFileMovePolicy, setup.Move.PreparedAt.AddSeconds(1)));
        Assert.IsNull(await catalog.ReadNamespaceRemoteFileMoveProtocolAsync(setup.Move.OperationId));
    }

    [TestMethod]
    [DataRow("old-package")]
    [DataRow("unknown-package")]
    [DataRow("source")]
    [DataRow("lifetime")]
    [DataRow("observations")]
    [DataRow("time")]
    public async Task UnsupportedLibraryOrPolicyCannotCreateAProtocolRecord(string invalid)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareProtocolFileAsync(catalog);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.AdmitNamespaceRemoteFileMoveProtocolAsync(setup.Move.OperationId,
            invalid == "old-package" ? "0.1.0-preview.5" : invalid == "unknown-package" ? "0.1.0-preview.7" : "0.1.0-preview.6",
            invalid == "source" ? "not-a-source-commit" : ProtocolSource,
            invalid == "lifetime" ? OriginalFileMovePolicy with { EchoSuppressionLifetimeTicks = 0 } :
            invalid == "observations" ? OriginalFileMovePolicy with { EchoSuppressionObservationCount = 65 } : OriginalFileMovePolicy,
            invalid == "time" ? setup.Move.PreparedAt.AddTicks(-1) : setup.Move.PreparedAt.AddSeconds(1)));
        Assert.IsNull(await catalog.ReadNamespaceRemoteFileMoveProtocolAsync(setup.Move.OperationId));
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("payload")]
    [DataRow("original-start")]
    public async Task DamagedProtocolEvidenceCannotBecomeAReplayAdmission(string damage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareProtocolFileAsync(catalog);
        await catalog.AdmitNamespaceRemoteFileMoveProtocolAsync(setup.Move.OperationId, "0.1.0-preview.6", ProtocolSource, OriginalFileMovePolicy, setup.Move.PreparedAt.AddSeconds(1));
        await ExecuteSqlAsync(fixture.Paths, damage switch
        {
            "fingerprint" => "UPDATE namespace_remote_file_move_protocols SET fingerprint=zeroblob(32);",
            "payload" => "UPDATE namespace_remote_file_move_protocols SET payload=x'00';",
            _ => "DELETE FROM namespace_member_move_starts;"
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceRemoteFileMoveProtocolAsync(setup.Move.OperationId));
        Assert.IsNotNull(await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
    }

    [TestMethod]
    public async Task ProtocolInsertFailurePreservesTheOriginalStartAndAllowsOneRetry()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareProtocolFileAsync(catalog);
        var start = await catalog.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId);
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_protocol BEFORE INSERT ON namespace_remote_file_move_protocols BEGIN SELECT RAISE(ABORT,'synthetic protocol interruption'); END;");
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => catalog.AdmitNamespaceRemoteFileMoveProtocolAsync(setup.Move.OperationId,
            "0.1.0-preview.6", ProtocolSource, OriginalFileMovePolicy, setup.Move.PreparedAt.AddSeconds(1)));
        Assert.IsNull(await catalog.ReadNamespaceRemoteFileMoveProtocolAsync(setup.Move.OperationId));
        Assert.AreEqual(start, await catalog.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_protocol;");
        Assert.IsNotNull(await catalog.AdmitNamespaceRemoteFileMoveProtocolAsync(setup.Move.OperationId,
            "0.1.0-preview.6", ProtocolSource, OriginalFileMovePolicy, setup.Move.PreparedAt.AddSeconds(1)));
    }

    [TestMethod]
    public async Task Schema65UpgradeCannotInventAProtocolForAnExistingRemoteStart()
    {
        using var fixture = new Fixture(); MoveFixture setup; MirrorPulseNamespaceMemberMoveStart? start;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareProtocolFileAsync(catalog);
            start = await catalog.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_remote_file_move_protocols; PRAGMA user_version=65;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(start, await reopened.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
        Assert.IsNull(await reopened.ReadNamespaceRemoteFileMoveProtocolAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Move, await reopened.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
    }

    private static async Task<MoveFixture> PrepareProtocolFileAsync(MirrorPulseProductCatalog catalog)
    {
        var setup = await PrepareRemoteRejectableMoveAsync(catalog, start: false);
        await catalog.RecordNamespaceMemberMoveStartAsync(new(2, setup.Move.OperationId, setup.Move.PreparedAt)
        { OriginalOfficialRemoteRevision = setup.Move.RemoteEntry!.PreviousRemoteRevision });
        return setup;
    }
}
