using System.Reflection;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseManagedRootRenameServiceTests
{
    [TestMethod]
    [DataRow("owner")]
    [DataRow("proof")]
    public async Task SelectedRecoveryDoesNotReadAnotherRootsDamagedHistory(string damage)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scheduler = new MirrorPulseInstanceScheduler();
        var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Catalog)!;
        await using var command = connection.CreateCommand();
        command.CommandText = damage == "owner"
            ? "UPDATE managed_root_rename_history SET root_id=$damage WHERE operation_id=$operation;"
            : "UPDATE managed_root_rename_history SET proof=$damage WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$damage", damage == "owner" ? Guid.NewGuid().ToString("D") : "null");
        command.Parameters.AddWithValue("$operation", fixture.Intents[1].OperationId.ToString("D"));
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
        command.CommandText = "SELECT json_array(root_id,payload,proof,observation) FROM managed_root_rename_history WHERE operation_id=$operation;";
        string damaged = (string)(await command.ExecuteScalarAsync())!;
        var calls = new List<Guid>();
        var service = new MirrorPulseManagedRootRenameService(fixture.Catalog, scheduler, (operation, _) =>
        {
            calls.Add(operation);
            return ValueTask.FromResult(new MirrorPulseManagedRootRenameRecovery(fixture.Intents[0], null));
        });

        MirrorPulseManagedRootRenameRecovery selected = await service.RecoverAsync(fixture.Intents[0].OperationId);
        Assert.AreEqual(fixture.Intents[0], selected.Intent);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => service.RecoverAsync(Guid.NewGuid()).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.RecoverAsync(fixture.Intents[1].OperationId).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.ReadManagedRootRenameHistoryAsync());
        CollectionAssert.AreEqual(new[] { fixture.Intents[0].OperationId }, calls);
        Assert.AreEqual(damaged, (string)(await command.ExecuteScalarAsync())!);
        CollectionAssert.AreEquivalent(fixture.Intents, (await fixture.Catalog.ReadManagedRootRenamesAsync()).ToArray());
    }
}
