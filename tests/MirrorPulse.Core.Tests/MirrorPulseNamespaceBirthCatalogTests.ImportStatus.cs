using System.Security.Cryptography;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task ImportStatusRetainsEachDurablePhaseWithoutAwaitingTheNamespaceActorAcrossOwners()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var permission = Protection(parent);
        var birth = Birth(parent, permission) with { Origin = MirrorPulseNamespaceBirthOrigin.Import };
        var plan = Plan(birth) with { Version = 2, Content = new(3, Convert.ToHexString(SHA256.HashData([1, 2, 3]))) };
        var start = Start(plan); var conversion = Conversion(birth, start);
        await using var writer = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(writer, parent, permission); await writer.PrepareNamespaceBirthAsync(birth);
        await writer.PrepareNamespaceBirthPlanAsync(plan);
        var reader = writer; // The Host retains the exclusive catalog owner during native work.
        await using var actor = new MirrorPulseNamespaceActor();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task copy = actor.RunAsync(async _ => { entered.SetResult(); await release.Task; return true; }).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var prepared = await reader.ReadNamespaceImportStatusAsync(birth.OperationId).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("Prepared", prepared.Phase); Assert.IsFalse(prepared.Started || prepared.Registered || prepared.RequiresRecovery);
            Assert.AreEqual(plan.Content.Length, prepared.Length); Assert.AreEqual(plan.Content.Sha256, prepared.Sha256);
            await writer.RecordNamespaceBirthStartAsync(start);
            var begun = await reader.ReadNamespaceImportStatusAsync(birth.OperationId);
            Assert.AreEqual("RecoveryRequired", begun.Phase); Assert.IsTrue(begun.Started && begun.RequiresRecovery);
            Assert.IsFalse(begun.OriginalBindingRecorded);
            await writer.PrepareNamespaceBirthConversionAsync(conversion);
            var bound = await reader.ReadNamespaceImportStatusAsync(birth.OperationId);
            Assert.AreEqual("OriginalBindingRecorded", bound.Phase); Assert.IsTrue(bound.OriginalBindingRecorded);
            Assert.IsFalse(bound.ContentRecorded || bound.Converted || bound.Protected || bound.Registered);
            var action = RecoveryAction(conversion, permission); await writer.PrepareNamespaceBirthRecoveryActionAsync(action);
            var completion = new MirrorPulseNamespaceBirthContentCompletion(1, birth.OperationId, action.RecoveryId, plan.Content,
                new(conversion.LocalObject, conversion.OwnerSid, action.RoleOnlyDacl, action.PreparedAt.AddSeconds(1)), false, 1);
            await writer.RecordNamespaceBirthContentCompletionAsync(completion);
            var copied = await reader.ReadNamespaceImportStatusAsync(birth.OperationId);
            Assert.AreEqual("ContentRecorded", copied.Phase); Assert.IsTrue(copied.ContentRecorded && copied.RequiresRecovery);
            var observation = Observation(birth, plan, start) with
            {
                LocalObject = conversion.LocalObject,
                ObservedAt = completion.Verification.ObservedAt.AddSeconds(1),
                BirthDacl = action.ConvertedDacl
            };
            await writer.RecordNamespaceBirthObservationAsync(observation);
            var observed = await reader.ReadNamespaceImportStatusAsync(birth.OperationId);
            Assert.AreEqual("Converted", observed.Phase); Assert.IsTrue(observed.Converted); Assert.IsFalse(observed.Protected || observed.Registered);
            Assert.IsFalse(copy.IsCompleted, "Historical status must not require the actor held by an active native copy.");
            Assert.AreEqual(plan, await reader.ReadNamespaceBirthPlanAsync(birth.OperationId));
            Assert.IsNull(await reader.ReadMutationAsync(birth.OperationId));
            Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        }
        finally { release.TrySetResult(); await copy; }
        await writer.DisposeAsync();
        await using var nextOwner = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retained = await nextOwner.ReadNamespaceImportStatusAsync(birth.OperationId);
        Assert.AreEqual("Converted", retained.Phase); Assert.IsTrue(retained.ContentRecorded && retained.Converted && retained.RequiresRecovery);
        Assert.IsFalse(retained.Protected || retained.Registered);
    }

    [TestMethod]
    public async Task ImportStatusRejectsUnknownOtherOriginAndCancelledQueries()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.ReadNamespaceImportStatusAsync(Guid.Empty));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.ReadNamespaceImportStatusAsync(Guid.NewGuid()));
        var parent = Baseline(); var permission = Protection(parent); var birth = Birth(parent, permission);
        await ProtectAsync(catalog, parent, permission); await catalog.PrepareNamespaceBirthAsync(birth);
        await catalog.PrepareNamespaceBirthPlanAsync(Plan(birth));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceImportStatusAsync(birth.OperationId));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.ReadNamespaceImportStatusAsync(birth.OperationId, cancelled.Token));
    }
}
