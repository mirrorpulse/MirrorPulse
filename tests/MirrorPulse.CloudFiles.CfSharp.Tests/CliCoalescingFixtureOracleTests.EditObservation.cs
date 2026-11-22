using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using MirrorPulse.CfSharp.CrashProbe;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class PendingOracleBoundaryTests
{
    [TestMethod]
    public async Task IndependentEditObserverRetainsActualJournalIdsAndPayloadsWithoutAcknowledgingOrDisclosingThem()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = new Dictionary<Guid, string>(); Guid? item = null;
        await using (var store = await MirrorPulseCfSharpStateStoreFactory.Create(fixture.Paths).OpenAsync(new(fixture.Paths.SyncRootPath)))
        await using (var transaction = await store.BeginTransactionAsync())
        {
            foreach (Guid id in fixture.OriginalIds)
            {
                var row = (await transaction.Operations.GetAsync(id))!;
                before.Add(id, Convert.ToHexString(SHA256.HashData(row.Payload.Span))); item = row.ItemId;
            }
            await transaction.RollbackAsync();
        }
        var result = await RunEditObservationAsync(fixture.Root);
        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.IsFalse(result.Output.Contains("PRIVATE_", StringComparison.Ordinal)); Assert.IsFalse(result.Output.Contains(fixture.Root, StringComparison.Ordinal));
        var observed = JsonSerializer.Deserialize<CliCoalescingEditObservation>(result.Output, JsonOptions)!;
        Assert.IsTrue(observed.ExclusiveCatalogOwner); Assert.IsTrue(observed.JournalInventoryComplete); Assert.AreEqual(item, observed.TargetItemId);
        CollectionAssert.AreEquivalent(fixture.OriginalIds, observed.Journal.Select(row => row.OperationId).ToArray());
        foreach (var row in observed.Journal) { Assert.IsTrue(row.AssociatedWithTarget); Assert.AreEqual(before[row.OperationId], row.PayloadSha256); }
        Assert.IsFalse(observed.NativeInspected); Assert.IsNull(observed.Native); Assert.IsNull(observed.NativeIdentitySha256);
        Assert.IsFalse(observed.OriginalSdkAcknowledgementExecuted); Assert.IsFalse(observed.MutationExecuted);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "inspection")));
        await using var reopened = await MirrorPulseCfSharpStateStoreFactory.Create(fixture.Paths).OpenAsync(new(fixture.Paths.SyncRootPath));
        await using var retained = await reopened.BeginTransactionAsync();
        foreach (Guid id in fixture.OriginalIds) Assert.AreEqual(before[id], Convert.ToHexString(SHA256.HashData((await retained.Operations.GetAsync(id))!.Payload.Span)));
        await retained.RollbackAsync();
    }

    [TestMethod]
    [DataRow("live-owner")]
    [DataRow("foreign-marker")]
    [DataRow("foreign-route")]
    [DataRow("missing-sdk")]
    [DataRow("unapproved-directory")]
    public async Task IndependentEditObserverRefusesForeignOrLiveFixturesWithoutCreatingAnInspectionStore(string change)
    {
        await using var fixture = await Fixture.CreateAsync(change); MirrorPulseProductCatalog? owner = null;
        try
        {
            if (change == "live-owner") owner = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
            string selected = change == "unapproved-directory" ? Directory.CreateDirectory(Path.Combine(fixture.Root, "child")).FullName : fixture.Root;
            var result = await RunEditObservationAsync(selected);
            Assert.AreNotEqual(0, result.ExitCode); Assert.AreEqual(string.Empty, result.Output.Trim());
            Assert.IsFalse(result.Error.Contains("PRIVATE_", StringComparison.Ordinal)); Assert.IsFalse(result.Error.Contains(fixture.Root, StringComparison.Ordinal));
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "inspection")));
            if (change == "missing-sdk") Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        }
        finally { if (owner is not null) await owner.DisposeAsync(); }
    }

    [TestMethod]
    public async Task IndependentEditObserverReportsItsActualBoundInsteadOfClaimingACompleteLargeInventory()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var store = await MirrorPulseCfSharpStateStoreFactory.Create(fixture.Paths).OpenAsync(new(fixture.Paths.SyncRootPath)))
        await using (var transaction = await store.BeginTransactionAsync())
        {
            for (int index = 0; index < 2047; index++)
                await transaction.Operations.EnqueueAsync(new(Guid.NewGuid(), CloudStateOperationKind.MetadataUpdate, null, new byte[] { 9 }, DateTimeOffset.UtcNow));
            await transaction.CommitAsync();
        }
        var result = await RunEditObservationAsync(fixture.Root); Assert.AreEqual(0, result.ExitCode, result.Error);
        var observed = JsonSerializer.Deserialize<CliCoalescingEditObservation>(result.Output, JsonOptions)!;
        Assert.HasCount(2048, observed.Journal); Assert.IsFalse(observed.JournalInventoryComplete); Assert.AreEqual(2049L, observed.ThroughSequence);
        Assert.AreEqual(2048L, observed.LastScannedSequence); Assert.HasCount(2, observed.Journal.Where(row => row.AssociatedWithTarget).ToArray());
        await using var reopened = await MirrorPulseCfSharpStateStoreFactory.Create(fixture.Paths).OpenAsync(new(fixture.Paths.SyncRootPath));
        await using var retained = await reopened.BeginTransactionAsync();
        var paging = (ICloudOperationJournalPaging)retained.Operations;
        Assert.AreEqual(2049L, await paging.GetHighWaterSequenceAsync());
        Assert.HasCount(1, (await paging.ReadPageAsync(observed.LastScannedSequence, observed.ThroughSequence, 128)).Operations);
        foreach (Guid original in fixture.OriginalIds) Assert.IsNotNull(await retained.Operations.GetAsync(original));
        await retained.RollbackAsync();
    }

    [TestMethod]
    public async Task EditObserverRequiresExplicitDisposableEnvironmentForInspection()
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = await RunEditObservationAsync(fixture.Root, native: true);
        Assert.AreEqual(2, result.ExitCode); Assert.AreEqual(string.Empty, result.Output.Trim());
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "inspection")));
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunEditObservationAsync(string root, bool native = false)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["MIRRORPULSE_NATIVE_TEST"] = "0";
        foreach (string argument in new[] { typeof(CliCoalescingFixtureProbe).Assembly.Location, native ? "--observe-cli-coalescing-edit" : "--audit-cli-coalescing-edit", root })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        return (process.ExitCode, await output, await error);
    }
}
