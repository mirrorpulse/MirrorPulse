using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using CfSharp;
using CfSharp.Storage.Sqlite;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    private sealed record AccessJournalRow(Guid Id, long Sequence, CloudStateOperationKind Kind, string PayloadSha256);

    private static async Task VerifyAccessOnlyJournalQualificationAsync(MirrorPulseCfSharpStateSession state, CloudLocalChangeFeed feed,
        MirrorPulseRootRouter router, CloudFile file, MirrorPulseNamespaceExecutionSession role, MirrorPulseJournalCoalescingPlan plan,
        CloudItemSnapshot original, byte[] originalBytes, CancellationToken token)
    {
        Assert.IsTrue(original.IsPlaceholder);
        Assert.AreEqual(CloudContentAvailability.FullyAvailable, original.ContentAvailability);
        Assert.IsNotNull(original.LocalBinding);
        var originals = await ReadRowsAsync();
        CollectionAssert.AreEqual(plan.Members.Select(member => member.OperationId).ToArray(), originals.Select(row => row.Id).ToArray());
        await RequirePublicContentRowsAsync(originals);

        await using var nextRole = new MirrorPulseNamespaceExecutionSession();
        string changedDacl = MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(nextRole, isDirectory: false);
        var binding = original.LocalBinding;
        CloudProtectedLocalOperationResult? receipt = null;
        await role.RunNamespaceOperationAsync(async () =>
            receipt = await MirrorPulseWindowsNamespacePermissionLease.RunOwnedProtectedAsync(file, router,
                new(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId), async (lease, stop) =>
                {
                    Assert.AreNotEqual(changedDacl, (await lease.InspectAsync(stop)).Dacl);
                    await lease.ApplyDaclAsync(changedDacl, stop);
                    Assert.AreEqual(changedDacl, (await lease.InspectAsync(stop)).Dacl);
                }, token));
        Assert.IsNotNull(receipt);
        Assert.IsTrue(receipt.CallbackCompleted && receipt.Drained && receipt.AccessDescriptorApplied && receipt.AccessDescriptorReadBack);
        var afterAccess = await file.InspectAsync(token);
        RequireSamePlaceholder(afterAccess);
        Assert.AreEqual(original.LastWriteTime, afterAccess.LastWriteTime);
        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(file.FullPath, token));

        // An Access-only event is a negative assertion. Allow the actual Windows feed to observe
        // it before issuing the separate positive control on the same file.
        var accessRows = await ObserveRowsAsync(originals, TimeSpan.FromSeconds(2));
        var accessContent = accessRows.Where(row => row.Sequence > originals[^1].Sequence &&
            row.Kind == CloudStateOperationKind.ContentUpdate).ToArray();
        await RequirePublicContentRowsAsync(accessContent);

        byte[] edited = [.. originalBytes];
        edited[^1] ^= 1;
        await using (var writer = new FileStream(file.FullPath, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            await writer.WriteAsync(edited, token);
            writer.Flush(flushToDisk: true);
        }
        var editedRows = await ObserveRowsAsync(accessRows, TimeSpan.FromSeconds(8));
        var ordinaryContent = editedRows.Where(row => row.Sequence > accessRows[^1].Sequence &&
            row.Kind == CloudStateOperationKind.ContentUpdate).ToArray();
        await RequirePublicContentRowsAsync(ordinaryContent);
        CollectionAssert.AreEqual(accessRows, editedRows.Take(accessRows.Length).ToArray());
        RequireSamePlaceholder(await file.InspectAsync(token));
        CollectionAssert.AreEqual(edited, await File.ReadAllBytesAsync(file.FullPath, token));
        Console.WriteLine($"CfSharp={AssemblyVersion(typeof(CloudFile))} Sqlite={AssemblyVersion(typeof(SqliteCloudStateStoreFactory))}");
        Console.WriteLine($"OriginalRows={originals.Length} AccessOnlyContentRows={accessContent.Length} RealSameLengthContentRows={ordinaryContent.Length}");
        Assert.IsNotEmpty(ordinaryContent, "A real same-length user write must remain visible in both public journal views.");
        Assert.IsEmpty(accessContent, "An Access-only DACL write must not produce a content operation.");

        void RequireSamePlaceholder(CloudItemSnapshot current)
        {
            Assert.IsTrue(current.IsPlaceholder);
            Assert.AreEqual(original.LocalBinding, current.LocalBinding);
            Assert.AreEqual(original.Length, current.Length);
            CollectionAssert.AreEqual(original.PlaceholderIdentity.ToArray(), current.PlaceholderIdentity.ToArray());
        }

        async Task<AccessJournalRow[]> ReadRowsAsync()
        {
            await using var read = await state.OpenStore.BeginTransactionAsync(token);
            var rows = await read.Operations.ListByItemIdAsync(plan.ItemId, 32, token);
            await read.RollbackAsync(token);
            Assert.IsLessThan(32, rows.Count, "The qualification must read the complete small target-item journal.");
            return rows.OrderBy(row => row.Sequence).Select(row => new AccessJournalRow(row.OperationId, row.Sequence,
                row.Kind, Convert.ToHexString(SHA256.HashData(row.Payload.Span)))).ToArray();
        }

        async Task<AccessJournalRow[]> ObserveRowsAsync(AccessJournalRow[] prior, TimeSpan window)
        {
            long started = Stopwatch.GetTimestamp();
            while (true)
            {
                var rows = await ReadRowsAsync();
                CollectionAssert.AreEqual(prior, rows.Take(prior.Length).ToArray());
                if (rows.Skip(prior.Length).Any(row => row.Kind == CloudStateOperationKind.ContentUpdate) ||
                    Stopwatch.GetElapsedTime(started) >= window)
                    return rows;
                await Task.Delay(50, token);
            }
        }

        async Task RequirePublicContentRowsAsync(AccessJournalRow[] rows)
        {
            var scan = await feed.BeginScanAsync(token);
            Assert.IsFalse(scan.RequiresFullRescan);
            foreach (var row in rows)
            {
                var page = await feed.ReadPageAsync(scan, row.Sequence - 1, 1, token);
                Assert.IsFalse(page.RequiresFullRescan);
                var change = page.Changes.Single();
                Assert.AreEqual(row.Id, change.OperationId);
                Assert.AreEqual(row.Sequence, change.Sequence);
                Assert.AreEqual(plan.ItemId, change.ItemId);
                Assert.AreEqual(CloudLocalChangeKind.ContentUpdate, change.Kind);
            }
        }
    }

    private static string AssemblyVersion(Type type) =>
        type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidDataException("The executing SDK assembly has no informational version.");
}
