using System.Text.Json;
using MirrorPulse.CfSharp.CrashProbe;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class CliFixtureProbeTests
{
    [TestMethod]
    [DataRow("missing")]
    [DataRow("prepared")]
    [DataRow("cancelled")]
    [DataRow("legacy-completed")]
    public async Task ChangedMarkerCannotGrantCleanupOfAnUnprovedOrUnfinishedLabel(string state)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string expectedName = fixture.Marker.DirectoryName;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            if (state != "missing")
            {
                var pending = await catalog.PrepareManagedRootRenameAsync(RootId.Parse(fixture.Marker.RootId), "NewLabel");
                if (state == "cancelled")
                    await catalog.TransitionManagedRootRenameAsync(pending.OperationId, MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled);
                if (state == "legacy-completed")
                {
                    var proof = new MirrorPulseRootRenameProof(Guid.NewGuid(), new(1, Guid.NewGuid(), Guid.NewGuid()), "AQID", DateTimeOffset.UtcNow);
                    await catalog.SaveManagedRootRenameProofAsync(pending.OperationId, proof);
                    await catalog.ObserveManagedRootRenameAsync(pending.OperationId, new(proof.LocalObject, proof.PlaceholderIdentity, "NewLabel", DateTimeOffset.UtcNow));
                    var topology = await catalog.ReadAdapterTopologyAsync(); var original = topology.Roots.Single();
                    var renamed = new RootRegistration(original.AdapterId, original.InstanceId, original.RootId, original.UniquenessKey,
                        "NewLabel", "NewLabel", original.CustomEntry, original.State, original.RegisteredAt, original.IdentityScope);
                    await catalog.SaveAdapterTopologyAsync(topology with { Roots = [renamed] });
                    await catalog.TransitionManagedRootRenameAsync(pending.OperationId, MirrorPulseRootRenamePhase.NativeObserved, MirrorPulseRootRenamePhase.LocalProjected);
                    await catalog.TransitionManagedRootRenameAsync(pending.OperationId, MirrorPulseRootRenamePhase.LocalProjected, MirrorPulseRootRenamePhase.Completed);
                    expectedName = "NewLabel";
                }
            }
        }
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, CliFixtureProbe.MarkerFileName),
            JsonSerializer.Serialize(state == "legacy-completed" ? fixture.Marker : fixture.Marker with { DirectoryName = "UnprovedOriginal" }, JsonOptions));
        var result = await RunAsync(fixture.Root, audit: false, cleanup: true);
        Assert.AreNotEqual(0, result.ExitCode); Assert.AreEqual(string.Empty, result.Output.Trim());
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "inspection")));
        await using var unchanged = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(expectedName, (await unchanged.ReadAdapterTopologyAsync()).Roots.Single().DirectoryName);
    }
}
