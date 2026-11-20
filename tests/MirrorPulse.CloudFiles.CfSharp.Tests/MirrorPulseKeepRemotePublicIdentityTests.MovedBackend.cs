using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseKeepRemotePublicIdentityTests
{
    [TestMethod]
    [DataRow("item")]
    [DataRow("instance")]
    [DataRow("root")]
    [DataRow("malformed")]
    [DataRow("noncanonical")]
    [DataRow("oversized")]
    public async Task AsyncPublicIdentityCannotBorrowAForeignOrUnboundedOriginalBeforeBackendCorrespondence(string changed)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-keep-remote-identity", Guid.NewGuid().ToString("N"));
        try
        {
            var root = Root(RootIdentityScope.InstanceRoot);
            var identity = MirrorPulsePlaceholderIdentity.CreateForRoot(root, "backend", "original").ToCfSharp();
            Guid itemId = changed == "item" ? Guid.NewGuid() : identity.ItemId;
            string encoded = changed switch
            {
                "malformed" => "invalid",
                "noncanonical" => " " + Convert.ToBase64String(identity.Encode()),
                "oversized" => new string('A', 5501),
                _ => Convert.ToBase64String(identity.Encode())
            };
            var instance = changed == "instance" ? InstanceId.New() : root.InstanceId;
            string rootKey = changed == "root" ? "other-root" : root.UniquenessKey;
            var content = new MirrorPulseCoalescingContent(3, new string('A', 64),
                new(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.ReplacePlaceholderIdentity, encoded));
            var rejected = new MirrorPulseMutationIntent(Guid.NewGuid(), instance, rootKey, MirrorPulseWorkerChangeKind.ContentUpdate,
                "current.bin", null, false, "original", 3, content.Sha256, MirrorPulseMutationOrigin.CoalescedJournal, content.UploadBinding);
            var intent = new MirrorPulseConflictKeepRemoteIntent(1, Guid.NewGuid(), Guid.NewGuid(), instance,
                Guid.NewGuid(), new string('B', 64), itemId, "Docs/current.bin", rejected,
                new(1, instance.ToString(), rootKey, "current.bin", "backend", "selected", 3), content, [Guid.NewGuid(), Guid.NewGuid()],
                [Guid.NewGuid(), Guid.NewGuid()], DateTimeOffset.UtcNow);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(new(Path.Combine(directory, "sync"), Path.Combine(directory, "data")));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseKeepRemotePublicIdentity.ReadOriginalAsync(catalog, root, intent, CancellationToken.None).AsTask());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
