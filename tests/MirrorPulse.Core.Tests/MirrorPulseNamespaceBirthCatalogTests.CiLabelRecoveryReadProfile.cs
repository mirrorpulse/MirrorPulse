using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("CiLabelRecoveryReadProfile")]
    [DataRow(1)]
    [DataRow(5)]
    [DataRow(12)]
    public async Task CiLabelOwnedRecoveryReadsRetainCompleteFactsAcrossIndependentAdmissions(int historyCount)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, true);
        var action = setup.Action;
        for (int index = 0; index < historyCount; index++)
        {
            await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
            if (index + 1 != historyCount)
                action = action with
                {
                    ActionId = Guid.NewGuid(),
                    PreviousActionId = action.ActionId,
                    PreparedAt = action.PreparedAt.AddSeconds(1)
                };
        }
        Assert.AreEqual(SerializeBornOwnerTest(action), SerializeBornOwnerTest(
            await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId)));
        var expected = await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(action.Child.Member.MemberId, action.Parent);
        var measured = await MeasureRegisteredBirthReadAsync(catalog, async () =>
        {
            var before = await catalog.ReadRootLabelBornRecoverySnapshotAsync(action);
            var after = await catalog.ReadRootLabelBornRecoverySnapshotAsync(action);
            Assert.AreEqual(SerializeBornOwnerTest(before), SerializeBornOwnerTest(after));
            return after;
        });
        Assert.AreEqual(SerializeBornOwnerTest(expected), SerializeBornOwnerTest(measured.Result));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Console.WriteLine("CiLabelBornReadSql: " + JsonSerializer.Serialize(new
        {
            historyCount,
            selects = measured.Selects,
            freshAdmissions = 2,
            allOriginalFactsRetained = true,
            nativeExecuted = false,
            wallClockSpeedupEstablished = false
        }));
    }
}
