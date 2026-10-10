using System.Security.AccessControl;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed partial class MirrorPulseNamespacePermissionTreeCatalogTests
{
    private const string Owner = "S-1-5-21-100-200-300-1001";
    private const string Role = "S-1-5-5-123-456";
    private static readonly string Original = new RawSecurityDescriptor($"D:AI(A;OICI;FA;;;{Owner})").GetSddlForm(AccessControlSections.Access);
    private static readonly string Target = new RawSecurityDescriptor($"D:PAI(A;OICI;FRFW;;;{Owner})(A;OICI;FA;;;{Role})").GetSddlForm(AccessControlSections.Access);

    [TestMethod]
    public async Task CaptureResumesAcrossRestartAndOnlyOriginalSealedIntentCanBePrepared()
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(0, (await catalog.CreateNamespacePermissionTreeAsync(definition)).CapturedMembers);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(
                definition.Anchor.Baseline, definition.Anchor.Intent));
            await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members[..2]);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(
                members[2].Preparation.Baseline, members[2].Preparation.Intent with { OperationId = Guid.NewGuid() }));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SealNamespacePermissionTreeAsync(definition.ManifestId, SealTime(definition)));
            Assert.HasCount(0, await catalog.ReadNamespacePermissionChangesAsync());
            Assert.IsNull(await catalog.ReadNamespacePermissionBaselineAsync(members[0].Preparation.Baseline.EvidenceId));
        }
        MirrorPulseNamespacePermissionTree sealedTree;
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(2, (await reopened.CreateNamespacePermissionTreeAsync(definition)).CapturedMembers);
            Assert.AreEqual(2, (await reopened.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members[..2])).CapturedMembers);
            await reopened.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members[2..]);
            sealedTree = await reopened.SealNamespacePermissionTreeAsync(definition.ManifestId, SealTime(definition));
            Assert.AreEqual(MirrorPulseNamespacePermissionTreePhase.Sealed, sealedTree.Phase);
            Assert.AreEqual(3, sealedTree.Seal!.MemberCount);
            Assert.AreEqual(64, sealedTree.Seal.Fingerprint.Length);
            Assert.HasCount(0, await reopened.ReadNamespacePermissionChangesAsync());
            foreach (var member in members)
            {
                var preparation = member.Preparation;
                await reopened.PrepareNamespacePermissionChangeAsync(preparation.Baseline, preparation.Intent);
            }
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareNamespacePermissionChangeAsync(
                definition.Anchor.Baseline, definition.Anchor.Intent with { TargetDacl = new RawSecurityDescriptor($"D:PAI(A;OICI;FR;;;{Owner})(A;OICI;FA;;;{Role})").GetSddlForm(AccessControlSections.Access) }));
        }
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(sealedTree, await reopened.ReadNamespacePermissionTreeAsync(definition.ManifestId));
            Assert.AreEqual(sealedTree, await reopened.SealNamespacePermissionTreeAsync(definition.ManifestId, SealTime(definition)));
            Assert.AreEqual(sealedTree, await reopened.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members));
            CollectionAssert.AreEqual(members, (await reopened.ReadNamespacePermissionTreeMembersAsync(definition.ManifestId)).ToArray());
            Assert.HasCount(3, await reopened.ReadNamespacePermissionChangesAsync());
            Assert.AreEqual(definition.Anchor.Baseline, await reopened.ReadNamespacePermissionBaselineAsync(definition.Anchor.Baseline.EvidenceId));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.SealNamespacePermissionTreeAsync(definition.ManifestId, SealTime(definition).AddSeconds(1)));
        }
    }

    [TestMethod]
    public async Task CapturingRootAlsoFencesItsParentButLeavesOtherRootPreparationIndependent()
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.CreateNamespacePermissionTreeAsync(definition);
        var parent = definition.Anchor.Baseline with
        {
            EvidenceId = Guid.NewGuid(),
            RootId = null,
            RelativePath = string.Empty,
            LocalObject = definition.Anchor.Baseline.LocalObject with { LocalFileId = definition.Anchor.Baseline.LocalObject.SyncRootFileId },
        };
        var parentIntent = definition.Anchor.Intent with
        {
            OperationId = Guid.NewGuid(),
            EvidenceId = parent.EvidenceId,
            RootId = null,
            RelativePath = string.Empty,
            LocalObject = parent.LocalObject,
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(parent, parentIntent));
        var other = Child(definition.Anchor, 1, "Other/file.txt");
        other = other with
        {
            Preparation = other.Preparation with
            {
                Baseline = other.Preparation.Baseline with { RootId = RootId.New() },
            }
        };
        var otherIntent = other.Preparation.Intent with { RootId = other.Preparation.Baseline.RootId };
        await catalog.PrepareNamespacePermissionChangeAsync(other.Preparation.Baseline, otherIntent);
        await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members);
        await catalog.SealNamespacePermissionTreeAsync(definition.ManifestId, SealTime(definition));
        await catalog.PrepareNamespacePermissionChangeAsync(parent, parentIntent);
        Assert.HasCount(2, await catalog.ReadNamespacePermissionChangesAsync());
    }

    [TestMethod]
    [DataRow("path")]
    [DataRow("unicode-path")]
    [DataRow("object")]
    [DataRow("evidence")]
    [DataRow("operation")]
    public async Task LaterDuplicateRollsBackTheWholePageAndItsCursor(string scenario)
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree(4);
        if (scenario == "unicode-path") members[2] = members[2] with
        {
            Preparation = members[2].Preparation with
            {
                Baseline = members[2].Preparation.Baseline with { RelativePath = "Docs/Nested/Ä.txt" },
                Intent = members[2].Preparation.Intent with { RelativePath = "Docs/Nested/Ä.txt" },
            }
        };
        var duplicate = Child(definition.Anchor, 3, "Docs/Nested/another.txt", members[1]);
        var prior = members[2].Preparation;
        duplicate = scenario switch
        {
            "path" => duplicate with { Preparation = duplicate.Preparation with { Intent = duplicate.Preparation.Intent with { RelativePath = "Docs/Nested/EDITED.txt" }, Baseline = duplicate.Preparation.Baseline with { RelativePath = "Docs/Nested/EDITED.txt" } } },
            "unicode-path" => duplicate with { Preparation = duplicate.Preparation with { Intent = duplicate.Preparation.Intent with { RelativePath = "Docs/Nested/ä.txt" }, Baseline = duplicate.Preparation.Baseline with { RelativePath = "Docs/Nested/ä.txt" } } },
            "object" => duplicate with { Preparation = duplicate.Preparation with { Intent = duplicate.Preparation.Intent with { LocalObject = prior.Baseline.LocalObject }, Baseline = duplicate.Preparation.Baseline with { LocalObject = prior.Baseline.LocalObject } } },
            "evidence" => duplicate with { Preparation = duplicate.Preparation with { Intent = duplicate.Preparation.Intent with { EvidenceId = prior.Baseline.EvidenceId }, Baseline = duplicate.Preparation.Baseline with { EvidenceId = prior.Baseline.EvidenceId } } },
            _ => duplicate with { Preparation = duplicate.Preparation with { Intent = duplicate.Preparation.Intent with { OperationId = prior.Intent.OperationId } } },
        };
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.CreateNamespacePermissionTreeAsync(definition);
            await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members[..2]);
            await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, [members[2], duplicate]));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(2, (await reopened.ReadNamespacePermissionTreeAsync(definition.ManifestId))!.CapturedMembers);
        Assert.HasCount(2, await reopened.ReadNamespacePermissionTreeMembersAsync(definition.ManifestId));
        Assert.HasCount(0, await reopened.ReadNamespacePermissionChangesAsync());
        Assert.IsNull(await reopened.ReadNamespacePermissionBaselineAsync(prior.Baseline.EvidenceId));
        await reopened.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, [members[2], Child(definition.Anchor, 3, "Docs/Nested/other.txt", members[1])]);
        await reopened.SealNamespacePermissionTreeAsync(definition.ManifestId, SealTime(definition));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("not-immediate")]
    [DataRow("file-parent")]
    public async Task InvalidParentRollsBackEarlierMembersInThePage(string scenario)
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree();
        if (scenario == "missing") members[2] = members[2] with { ParentEvidenceId = Guid.NewGuid() };
        else if (scenario == "not-immediate") members[2] = members[2] with { ParentEvidenceId = definition.Anchor.Baseline.EvidenceId };
        else members[1] = members[1] with { Preparation = members[1].Preparation with { Baseline = members[1].Preparation.Baseline with { IsDirectory = false } } };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.CreateNamespacePermissionTreeAsync(definition);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members));
        Assert.AreEqual(0, (await catalog.ReadNamespacePermissionTreeAsync(definition.ManifestId))!.CapturedMembers);
        Assert.HasCount(0, await catalog.ReadNamespacePermissionTreeMembersAsync(definition.ManifestId));
    }

    [TestMethod]
    [DataRow("scope")]
    [DataRow("owner")]
    [DataRow("sync-root")]
    [DataRow("role")]
    [DataRow("kind")]
    [DataRow("object")]
    public async Task ForeignOrMismatchedMembersCannotEnterTheCapture(string scenario)
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree();
        var preparation = members[2].Preparation;
        preparation = scenario switch
        {
            "scope" => preparation with { Intent = preparation.Intent with { RootId = RootId.New() } },
            "owner" => preparation with { Baseline = preparation.Baseline with { OwnerSid = "S-1-5-18" } },
            "sync-root" => preparation with { Baseline = preparation.Baseline with { LocalObject = preparation.Baseline.LocalObject with { SyncRootFileId = Guid.NewGuid() } }, Intent = preparation.Intent with { LocalObject = preparation.Baseline.LocalObject with { SyncRootFileId = Guid.NewGuid() } } },
            "role" => preparation with { Intent = preparation.Intent with { RoleSid = "S-1-5-5-123-457" } },
            "kind" => preparation with { Intent = preparation.Intent with { Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole } },
            _ => preparation with { Intent = preparation.Intent with { LocalObject = preparation.Intent.LocalObject with { LocalFileId = Guid.NewGuid() } } },
        };
        members[2] = members[2] with { Preparation = preparation };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.CreateNamespacePermissionTreeAsync(definition);
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members));
        Assert.HasCount(0, await catalog.ReadNamespacePermissionTreeMembersAsync(definition.ManifestId));
    }

    [TestMethod]
    public async Task ChangedReplayGapPartialReplayAndChangedHeaderPreserveCapturedOriginals()
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree(4);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.CreateNamespacePermissionTreeAsync(definition);
        await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members[..2]);
        var changed = members[0] with { Preparation = members[0].Preparation with { Baseline = members[0].Preparation.Baseline with { OriginalDacl = Target } } };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, [changed]));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members[1..]));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, [members[2] with { Sequence = 3 }]));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CreateNamespacePermissionTreeAsync(definition with { ExpectedMembers = 5 }));
        CollectionAssert.AreEqual(members[..2], (await catalog.ReadNamespacePermissionTreeMembersAsync(definition.ManifestId)).ToArray());
    }

    [TestMethod]
    public async Task CaptureCannotAdoptAnExecutedOperationOrChangeExistingObjectOwnership()
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(members[2].Preparation.Baseline, members[2].Preparation.Intent);
        await catalog.CreateNamespacePermissionTreeAsync(definition);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members));
        var original = members[2].Preparation.Baseline;
        var alias = original with { EvidenceId = Guid.NewGuid() };
        members[2] = members[2] with { Preparation = new(alias, members[2].Preparation.Intent with { OperationId = Guid.NewGuid(), EvidenceId = alias.EvidenceId }) };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members));
        Assert.AreEqual(0, (await catalog.ReadNamespacePermissionTreeAsync(definition.ManifestId))!.CapturedMembers);
        Assert.AreEqual(original, await catalog.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
        Assert.HasCount(1, await catalog.ReadNamespacePermissionChangesAsync());
    }

    [TestMethod]
    public async Task LargeCaptureUsesBoundedPagesAndSealsTheSameEvidenceAfterRestart()
    {
        using var fixture = new CatalogFixture();
        var (definition, _) = Tree(4097);
        var members = new MirrorPulseNamespacePermissionTreeMember[4097];
        members[0] = new(0, null, definition.Anchor);
        for (int index = 1; index < members.Length; index++) members[index] = Child(definition.Anchor, index, $"Docs/file{index}.txt");
        MirrorPulseNamespacePermissionTree sealedTree;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.CreateNamespacePermissionTreeAsync(definition);
            await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members[..4096]);
        }
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(4096, (await reopened.ReadNamespacePermissionTreeAsync(definition.ManifestId))!.CapturedMembers);
            await reopened.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members[4096..]);
            sealedTree = await reopened.SealNamespacePermissionTreeAsync(definition.ManifestId, SealTime(definition));
            Assert.HasCount(4096, await reopened.ReadNamespacePermissionTreeMembersAsync(definition.ManifestId));
            CollectionAssert.AreEqual(members[4096..], (await reopened.ReadNamespacePermissionTreeMembersAsync(definition.ManifestId, 4095)).ToArray());
            Assert.HasCount(0, await reopened.ReadNamespacePermissionChangesAsync());
        }
        await using var final = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(sealedTree, await final.SealNamespacePermissionTreeAsync(definition.ManifestId, SealTime(definition)));
    }

    [TestMethod]
    [DataRow("payload")]
    [DataRow("index")]
    [DataRow("fingerprint")]
    public async Task CorruptMemberCannotBeReadSealedOrPrepared(string scenario)
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.CreateNamespacePermissionTreeAsync(definition);
            await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members);
            await catalog.SealNamespacePermissionTreeAsync(definition.ManifestId, SealTime(definition));
        }
        await ExecuteSqlAsync(fixture.Paths, scenario switch
        {
            "payload" => "UPDATE namespace_permission_tree_members SET payload=json_set(payload,'$.Preparation.Baseline.OwnerSid','S-1-5-18') WHERE sequence=2;",
            "index" => "UPDATE namespace_permission_tree_members SET relative_path='Docs/Nested/wrong.txt' WHERE sequence=2;",
            _ => "UPDATE namespace_permission_tree_members SET fingerprint=zeroblob(32) WHERE sequence=2;",
        });
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        if (scenario == "payload")
        {
            await Assert.ThrowsAsync<ArgumentException>(() => reopened.ReadNamespacePermissionTreeMembersAsync(definition.ManifestId));
            await Assert.ThrowsAsync<ArgumentException>(() => reopened.SealNamespacePermissionTreeAsync(definition.ManifestId, SealTime(definition)));
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespacePermissionTreeMembersAsync(definition.ManifestId));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.SealNamespacePermissionTreeAsync(definition.ManifestId, SealTime(definition)));
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.PrepareNamespacePermissionChangeAsync(members[2].Preparation.Baseline, members[2].Preparation.Intent));
        Assert.HasCount(0, await reopened.ReadNamespacePermissionChangesAsync());
    }

    [TestMethod]
    public async Task MigrationFromSchema22PreservesImmutablePermissionHistory()
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await catalog.PrepareNamespacePermissionChangeAsync(members[2].Preparation.Baseline, members[2].Preparation.Intent);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_permission_tree_members; DROP TABLE namespace_permission_trees; PRAGMA user_version=22;");
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(members[2].Preparation.Baseline, await reopened.ReadNamespacePermissionBaselineAsync(members[2].Preparation.Baseline.EvidenceId));
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, (await reopened.ReadNamespacePermissionChangeAsync(members[2].Preparation.Intent.OperationId))!.Phase);
            await reopened.CreateNamespacePermissionTreeAsync(definition);
        }
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(29L, await command.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task InvalidBoundsAndCancellationDoNotAdvanceTheCapture()
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.CreateNamespacePermissionTreeAsync(definition with { ExpectedMembers = 1_000_001 }));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.CreateNamespacePermissionTreeAsync(definition with { ExpectedMembers = 0 }));
        await catalog.CreateNamespacePermissionTreeAsync(definition);
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, []));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, Enumerable.Repeat(members[0], 4097).ToArray()));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, [members[0], members[2]]));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.ReadNamespacePermissionTreeMembersAsync(definition.ManifestId, limit: 4097));
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members, new(true)));
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.SealNamespacePermissionTreeAsync(definition.ManifestId, SealTime(definition), new(true)));
        Assert.AreEqual(0, (await catalog.ReadNamespacePermissionTreeAsync(definition.ManifestId))!.CapturedMembers);
        Assert.HasCount(0, await catalog.ReadNamespacePermissionTreeMembersAsync(definition.ManifestId));
    }

    [TestMethod]
    public async Task ConcurrentConflictingPagesRetainOneCompleteWinner()
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.CreateNamespacePermissionTreeAsync(definition);
        await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members[..2]);
        var competing = Child(definition.Anchor, 2, "Docs/Nested/other.txt", members[1]);
        var first = catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members[2..]);
        var second = catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, [competing]);
        Assert.AreEqual(3, (await first).CapturedMembers);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second);
        CollectionAssert.AreEqual(members, (await catalog.ReadNamespacePermissionTreeMembersAsync(definition.ManifestId)).ToArray());
        await catalog.SealNamespacePermissionTreeAsync(definition.ManifestId, SealTime(definition));
        Assert.IsNull(await catalog.ReadNamespacePermissionChangeAsync(competing.Preparation.Intent.OperationId));
    }

    private static (MirrorPulseNamespacePermissionTreeDefinition Definition, MirrorPulseNamespacePermissionTreeMember[] Members) Tree(int count = 3)
    {
        var baseline = new MirrorPulseNamespacePermissionBaseline(Guid.NewGuid(), RootId.New(), new(1, Guid.NewGuid(), Guid.NewGuid()),
            "Docs", true, Owner, Original, DateTimeOffset.UtcNow);
        var intent = new MirrorPulseNamespacePermissionIntent(Guid.NewGuid(), baseline.EvidenceId, baseline.LocalObject, baseline.RootId,
            baseline.RelativePath, MirrorPulseNamespacePermissionChangeKind.Protect, Role, Original, Target, baseline.CapturedAt.AddSeconds(1));
        var anchor = new MirrorPulseNamespacePermissionPreparation(baseline, intent);
        var definition = new MirrorPulseNamespacePermissionTreeDefinition(Guid.NewGuid(), anchor, count, baseline.CapturedAt);
        var root = new MirrorPulseNamespacePermissionTreeMember(0, null, anchor);
        var directory = Child(anchor, 1, "Docs/Nested", isDirectory: true);
        return (definition, [root, directory, Child(anchor, 2, "Docs/Nested/edited.txt", directory)]);
    }

    private static MirrorPulseNamespacePermissionTreeMember Child(MirrorPulseNamespacePermissionPreparation anchor,
        int sequence, string path, MirrorPulseNamespacePermissionTreeMember? parent = null, bool isDirectory = false)
    {
        var baseline = anchor.Baseline with { EvidenceId = Guid.NewGuid(), LocalObject = anchor.Baseline.LocalObject with { LocalFileId = Guid.NewGuid() }, RelativePath = path, IsDirectory = isDirectory };
        var intent = anchor.Intent with { OperationId = Guid.NewGuid(), EvidenceId = baseline.EvidenceId, LocalObject = baseline.LocalObject, RelativePath = path };
        return new(sequence, parent?.Preparation.Baseline.EvidenceId ?? anchor.Baseline.EvidenceId, new(baseline, intent));
    }

    private static DateTimeOffset SealTime(MirrorPulseNamespacePermissionTreeDefinition definition) => definition.Anchor.Intent.PreparedAt.AddSeconds(1);

    private static async Task ExecuteSqlAsync(MirrorPulseStoragePaths paths, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class CatalogFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-permission-tree-catalog", Guid.NewGuid().ToString("N"));
        public MirrorPulseStoragePaths Paths { get; }
        public CatalogFixture() => Paths = new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data"));
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}
