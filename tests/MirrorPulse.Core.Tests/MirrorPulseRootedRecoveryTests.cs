using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRootedRecoveryTests
{
    private static readonly string[] ExpectedRoots = ["right", "left"];
    [TestMethod]
    public async Task AcceptedMoveRetainsBothRootBindingsAcrossCatalogRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new Core.Configuration.MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var intent = new MirrorPulseMutationIntent(Guid.NewGuid(), InstanceId.New(), "right", MirrorPulseWorkerChangeKind.Move,
            "same.txt", "same.txt", false, "old", null, null, MirrorPulseMutationOrigin.Journal, PreviousRootKey: "left");
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await catalog.PrepareMutationAsync(intent);
                await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
                await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted, "accepted");
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            MirrorPulseMutationRecord record = (await reopened.ReadMutationAsync(intent.OperationId))!;
            Assert.AreEqual("left", record.Intent.PreviousRootKey);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.PrepareMutationAsync(intent with { PreviousRootKey = "another" }));
            var stats = new RootedStat();
            MirrorPulseMutationProof proof = await new MirrorPulseMutationReadback(stats).VerifyAsync(record, CancellationToken.None);
            Assert.AreEqual(MirrorPulseMutationProofKind.Verified, proof.Kind);
            CollectionAssert.AreEqual(ExpectedRoots, stats.Requests.Select(r => r.RootKey).ToArray());
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class RootedStat : IMirrorPulseWorkerStatTransport
    {
        public List<MirrorPulseWorkerStatRequest> Requests { get; } = [];
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(request.RootKey == "right" ? "accepted" : null);
        }
    }
}
