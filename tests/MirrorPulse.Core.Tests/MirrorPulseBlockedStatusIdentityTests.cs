using System.Text.Json;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseBlockedStatusIdentityTests
{
    [TestMethod]
    public void StatusCodecCannotConstructAnEmptyInstanceFromAnExplicitIdentityValue()
    {
        byte[] payload = MirrorPulseControlJsonCodec.Serialize(new { value = Guid.Empty });
        Assert.ThrowsExactly<ArgumentException>(() => MirrorPulseControlJsonCodec.Deserialize<InstanceId>(payload));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void VersionedStatusCodecRetainsTheExactBlockedInstanceOrExplicitUnroutedNull(bool unrouted)
    {
        InstanceId? instance = unrouted ? null : InstanceId.New();
        var original = new MirrorPulseBlockedLocalOperation(Guid.NewGuid(), instance, "Offline Operations/metadata.txt",
            MirrorPulseLocalOperationBlockReason.UnsupportedMetadataChange, DateTimeOffset.UtcNow);
        var status = new MirrorPulseAppStatusResponse(1, 0, [], [], BlockedLocalOperations: [original]);
        var encoded = MirrorPulseControlJsonCodec.Serialize(status);
        var actual = MirrorPulseControlJsonCodec.Deserialize<MirrorPulseAppStatusResponse>(encoded);
        Assert.IsNotNull(actual.BlockedLocalOperations);
        Assert.HasCount(1, actual.BlockedLocalOperations); Assert.AreEqual(original, actual.BlockedLocalOperations[0]);
        if (!unrouted)
        {
            using var document = JsonDocument.Parse(encoded);
            Assert.AreEqual(instance!.Value.Value, document.RootElement.GetProperty("blockedLocalOperations")[0].GetProperty("instanceId").GetProperty("value").GetGuid());
        }
    }
}
