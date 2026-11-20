using System.Security.Cryptography;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseRootLabelTargetInspectionTests
{
    [TestMethod]
    [DataRow(RootRegistrationState.Active)]
    [DataRow(RootRegistrationState.Disabled)]
    public void OriginalSealedTargetIndexDoesNotPublishRoutingOrGrantAnUnsealedMember(RootRegistrationState state)
    {
        var (intent, snapshot, registration) = CreateModel(state);
        var router = new MirrorPulseRootRouter(SyncRoot, [registration]);
        var paths = new MirrorPulseRootLabelTargetPaths(intent, snapshot, registration);
        paths.RequireCurrentRouter(router);
        foreach (var member in snapshot.Entries)
            Assert.AreEqual(member, paths.RequireMember("Renamed" + member.RelativePath["Docs".Length..], member.IsDirectory));
        Assert.ThrowsExactly<FileNotFoundException>(() => router.ResolveCurrentPath(Path.Combine(SyncRoot, "Renamed", "nested", "leaf.txt")));
        Assert.AreEqual(registration, router.FindRegistration(registration.RootId));
        Assert.AreEqual(registration.State, router.Registrations.Single().State);
        Assert.ThrowsExactly<InvalidDataException>(() => paths.RequireMember("Renamed/nested/unregistered.txt", false));
    }

    [TestMethod]
    [DataRow("Docs/nested/leaf.txt")]
    [DataRow("renamed/nested/leaf.txt")]
    [DataRow("Renamed/Nested/leaf.txt")]
    [DataRow("Renamed/nested/leaf.txt/child")]
    [DataRow("Renamed/nested/../leaf.txt")]
    [DataRow("Renamed/nested/leaf.txt:stream")]
    [DataRow("Renamed\\nested\\leaf.txt")]
    [DataRow("Foreign/nested/leaf.txt")]
    [DataRow("")]
    public void TargetIndexRefusesOldNamesAliasesTraversalStreamsAndUnsealedPaths(string path)
    {
        var (intent, snapshot, registration) = CreateModel();
        var paths = new MirrorPulseRootLabelTargetPaths(intent, snapshot, registration);
        Assert.ThrowsExactly<InvalidDataException>(() => paths.RequireMember(path, false));
        Assert.ThrowsExactly<InvalidDataException>(() => paths.RequireMember("Renamed/nested/leaf.txt", true));
    }

    [TestMethod]
    [DataRow("generic-version")]
    [DataRow("operation")]
    [DataRow("public-evidence")]
    [DataRow("source-registration")]
    [DataRow("removed-registration")]
    [DataRow("other-root")]
    [DataRow("other-parent")]
    [DataRow("not-sync-root")]
    [DataRow("parent-volume")]
    [DataRow("root-missing")]
    [DataRow("root-binding")]
    [DataRow("root-identity")]
    [DataRow("root-protection")]
    [DataRow("parent")]
    [DataRow("depth")]
    [DataRow("traversal")]
    [DataRow("case-alias")]
    [DataRow("duplicate-binding")]
    [DataRow("duplicate-member")]
    [DataRow("owner")]
    [DataRow("identity-member")]
    [DataRow("future-protection")]
    public void TargetIndexRefusesSubstitutedOriginalsAndInvalidCompleteTree(string changed)
    {
        var (intent, snapshot, registration) = CreateModel();
        var entries = snapshot.Entries.ToArray();
        switch (changed)
        {
            case "generic-version": intent = intent with { Version = 1 }; break;
            case "operation": snapshot = snapshot with { OperationId = Guid.NewGuid() }; break;
            case "public-evidence": snapshot = snapshot with { PublicEvidenceSha256 = new string('A', 64) }; break;
            case "source-registration": registration = Rename(registration, "Another"); break;
            case "removed-registration": registration = Rename(registration, "Docs", RootRegistrationState.Removed); break;
            case "other-root": intent = intent with { RootId = RootId.New() }; break;
            case "other-parent": intent = intent with { Target = intent.Target with { ParentMemberId = Guid.NewGuid() } }; break;
            case "not-sync-root":
                intent = intent with
                {
                    Source = intent.Source with { ParentLocalObject = intent.LocalObject },
                    Target = intent.Target with { ParentLocalObject = intent.LocalObject }
                }; break;
            case "parent-volume":
                var parent = intent.Source.ParentLocalObject with { VolumeSerialNumber = 2 };
                intent = intent with { Source = intent.Source with { ParentLocalObject = parent }, Target = intent.Target with { ParentLocalObject = parent } };
                break;
            case "root-missing": entries = entries[1..]; break;
            case "root-binding": entries[0] = entries[0] with { LocalObject = entries[0].LocalObject with { LocalFileId = Guid.NewGuid() } }; break;
            case "root-identity": entries[0] = entries[0] with { Identity = entries[0].Identity with { RemoteId = "substitute" } }; break;
            case "root-protection": entries[0] = entries[0] with { ProtectionId = Guid.NewGuid() }; break;
            case "parent": entries[2] = entries[2] with { ParentMemberId = intent.Source.ParentMemberId }; break;
            case "depth": entries[2] = entries[2] with { Depth = 1 }; break;
            case "traversal": entries[2] = entries[2] with { RelativePath = "Docs/nested/../leaf.txt" }; break;
            case "case-alias":
                entries = [.. entries, entries[2] with { MemberId = Guid.NewGuid(), RelativePath = "Docs/nested/Leaf.txt",
                LocalObject = entries[2].LocalObject with { LocalFileId = Guid.NewGuid() } }];
                entries[^1] = entries[^1] with { Identity = entries[^1].Identity with { MemberId = entries[^1].MemberId } }; break;
            case "duplicate-binding": entries[2] = entries[2] with { LocalObject = entries[1].LocalObject }; break;
            case "duplicate-member": entries = [.. entries, entries[2]]; break;
            case "owner": entries[2] = entries[2] with { OwnerSid = "S-1-5-21-9-8-7-1001" }; break;
            case "identity-member": entries[2] = entries[2] with { Identity = entries[2].Identity with { MemberId = Guid.NewGuid() } }; break;
            case "future-protection": entries[2] = entries[2] with { ProtectionVerifiedAt = intent.PreparedAt.AddSeconds(1) }; break;
        }
        snapshot = snapshot with { Entries = entries };
        Assert.ThrowsExactly<InvalidDataException>(() => new MirrorPulseRootLabelTargetPaths(intent, snapshot, registration));
    }

    [TestMethod]
    [DataRow("label")]
    [DataRow("disabled")]
    [DataRow("removed")]
    public void AChangedCurrentRegistrationExpiresTheRetainedTargetMap(string changed)
    {
        var (intent, snapshot, registration) = CreateModel();
        var paths = new MirrorPulseRootLabelTargetPaths(intent, snapshot, registration);
        var router = new MirrorPulseRootRouter(SyncRoot, [registration]);
        router.ReplaceRegistrations(changed == "removed" ? [] : [Rename(registration,
            changed == "label" ? "Later" : "Docs", changed == "disabled" ? RootRegistrationState.Disabled : registration.State)]);
        Assert.ThrowsExactly<InvalidOperationException>(() => paths.RequireCurrentRouter(router));
    }

    [TestMethod]
    public void TheTargetIndexCopiesItsMembershipInsteadOfAdoptingLaterArrayChanges()
    {
        var (intent, snapshot, registration) = CreateModel();
        var entries = snapshot.Entries.ToArray();
        var original = entries[2];
        var paths = new MirrorPulseRootLabelTargetPaths(intent, snapshot with { Entries = entries }, registration);
        entries[2] = entries[2] with { RelativePath = "Docs/nested/unregistered.txt" };
        Assert.AreEqual(original, paths.RequireMember("Renamed/nested/leaf.txt", false));
        Assert.ThrowsExactly<InvalidDataException>(() => paths.RequireMember("Renamed/nested/unregistered.txt", false));
    }

    [TestMethod]
    public async Task TargetInspectionRequiresTheActualActorAndOriginalUncancelledOperationBeforeCallback()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse.TargetInspection", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulse.Core.Configuration.MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            await using var actor = new MirrorPulseNamespaceActor();
            var router = new MirrorPulseRootRouter(paths.SyncRootPath, []);
            int calls = 0;
            Task<int> Inspect(CancellationToken token) => MirrorPulseRootLabelTargetInspection.RunAsync(catalog, router, actor, Guid.NewGuid(),
                (_, _) => { calls++; return Task.FromResult(1); }, token);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Inspect(default));
            await actor.RunAsync<int>(async token =>
            {
                using var stop = new CancellationTokenSource(); stop.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => Inspect(stop.Token));
                await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => Inspect(token));
                return 1;
            });
            Assert.AreEqual(0, calls);
            Assert.IsFalse(Directory.Exists(paths.SyncRootPath));
            Assert.IsFalse(File.Exists(paths.CfSharpStateDatabasePath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static readonly string SyncRoot = Path.Combine(Path.GetTempPath(), "MirrorPulse.TargetInspection.Model");

    private static (MirrorPulseNamespaceMemberMoveIntent Intent, MirrorPulseNamespaceDirectoryMoveSnapshot Snapshot,
        RootRegistration Registration) CreateModel(RootRegistrationState state = RootRegistrationState.Active)
    {
        var rootId = RootId.New(); var anchorId = Guid.NewGuid(); var syncId = Guid.NewGuid();
        var registration = new RootRegistration(AdapterId.Parse("example.target-inspection"), InstanceId.New(), rootId,
            "docs", "Docs", "Docs", false, state, DateTimeOffset.UtcNow);
        var binding = new MirrorPulseLocalFileBinding(1, syncId, Guid.NewGuid());
        var anchor = new MirrorPulseLocalFileBinding(1, syncId, syncId);
        var rootMember = Guid.NewGuid(); var protection = Guid.NewGuid(); var itemId = Guid.NewGuid();
        var intent = new MirrorPulseNamespaceMemberMoveIntent(3, Guid.NewGuid(), rootMember, protection, rootId, binding,
            itemId, "local:root", "S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-2001",
            new(anchorId, anchor, protection, "Docs"), new(anchorId, anchor, protection, "Renamed"), DateTimeOffset.UtcNow)
        { DirectoryMoveEvidence = "AQID" };
        MirrorPulseNamespaceDirectoryMoveEntry Entry(Guid member, Guid parent, string path, bool directory, int depth,
            MirrorPulseLocalFileBinding local, Guid id, string remote) => new(member, parent, path, directory, local,
                intent.OwnerSid, protection, intent.PreparedAt, null, new(member, id, remote, null, id, remote), depth);
        var first = Entry(rootMember, anchorId, "Docs", true, 0, binding, itemId, intent.RemoteId);
        var nested = Entry(Guid.NewGuid(), rootMember, "Docs/nested", true, 1, binding with { LocalFileId = Guid.NewGuid() }, Guid.NewGuid(), "local:nested");
        var leaf = Entry(Guid.NewGuid(), nested.MemberId, "Docs/nested/leaf.txt", false, 2, binding with { LocalFileId = Guid.NewGuid() }, Guid.NewGuid(), "local:leaf");
        // Opaque synthetic bytes exercise only the pure path index. They are not public SDK/native evidence.
        var snapshot = new MirrorPulseNamespaceDirectoryMoveSnapshot(1, intent.OperationId,
            Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(intent.DirectoryMoveEvidence))), [first, nested, leaf]);
        return (intent, snapshot, registration);
    }

    private static RootRegistration Rename(RootRegistration root, string label, RootRegistrationState? state = null) =>
        new(root.AdapterId, root.InstanceId, root.RootId, root.UniquenessKey, label, label, root.CustomEntry,
            state ?? root.State, root.RegisteredAt, root.IdentityScope);
}
