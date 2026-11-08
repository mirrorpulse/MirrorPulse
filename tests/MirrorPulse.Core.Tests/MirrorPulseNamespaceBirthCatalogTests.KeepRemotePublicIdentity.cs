using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task KeepRemoteFileStartRetainsDistinctPublicAndBackendIdentifiersAcrossCatalogOwners()
    {
        using var fixture = new Fixture();
        KeepRemoteFileSetup setup;
        MirrorPulseConflictKeepRemoteFileStart start;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            // The ACL binds public routing identities to backend objects. The catalog retains
            // both domains and must not treat their textual equality as that routing proof.
            setup = await PrepareKeepRemoteFileAsync(catalog, backendRemoteId: "adapter-object-identity");
            Assert.AreNotEqual(setup.Original.Identity.RemoteId, setup.Preparation.Intent.RemoteSource.RemoteId);
            start = await StartKeepRemoteFileAsync(catalog, setup);
            Assert.AreEqual(setup.Original, start.Original);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(JsonSerializer.Serialize(start), JsonSerializer.Serialize(await reopened.ReadConflictKeepRemoteFileStartAsync(start.CommandId)));
        Assert.AreEqual("adapter-object-identity", (await reopened.ReadConflictKeepRemoteAsync(start.CommandId))!.Intent.RemoteSource.RemoteId);
        Assert.AreEqual("pending", (await reopened.ReadUserCommandAsync(start.CommandId))!.State);
    }
}
