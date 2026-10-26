using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    private async Task ObserveRootLabelJournalOriginalsAsync(CloudFileSystem fileSystem, Guid originalRootItemId,
        RootRegistrationState state, string cut, CancellationToken token)
    {
        // Optional observation after the original assertions. No public ACK, SDK
        // mutation, rescan reset, Worker call, content read or inferred ownership.
        object report;
        try
        {
            await using var feed = fileSystem.CreateLocalChangeFeed();
            await feed.StartAsync(token);
            var scan = await feed.BeginScanAsync(token);
            var current = await fileSystem.GetDirectory("RENAMED").InspectAsync(token);
            var originals = new List<object>();
            long after = 0;
            int pages = 0, scanned = 0, rootMoves = 0, unassigned = 0;
            bool more = !scan.RequiresFullRescan;
            bool loss = scan.RequiresFullRescan;
            while (more && pages < 8)
            {
                var page = await feed.ReadPageAsync(scan, after, 64, token);
                pages++; scanned += page.Changes.Count;
                loss |= page.RequiresFullRescan;
                foreach (var change in page.Changes)
                {
                    if (change.Kind != CloudLocalChangeKind.Move || !change.IsDirectory ||
                        !IsRootJournalName(change.RelativePath) || !IsRootJournalName(change.PreviousRelativePath)) continue;
                    rootMoves++;
                    if (change.ItemId is null) unassigned++;
                    if (originals.Count < 16)
                        originals.Add(new
                        {
                            change.OperationId,
                            change.Sequence,
                            change.ItemId,
                            change.RelativePath,
                            change.PreviousRelativePath,
                            change.ObservedAt,
                        });
                }
                if (page.HasMore && page.LastScannedSequence <= after)
                    throw new InvalidDataException("The original SDK journal page did not advance.");
                after = page.LastScannedSequence;
                more = page.HasMore && !loss;
            }
            report = new
            {
                schemaVersion = 1,
                available = true,
                state = state.ToString(),
                cut,
                originalRootItemId,
                currentNativeItemId = current.ItemId,
                sdkAssemblyVersion = typeof(CloudFileSystem).Assembly.GetName().Version?.ToString(),
                throughSequence = scan.ThroughSequence,
                lastScannedSequence = after,
                pages,
                scanned,
                rootMoves,
                unassigned,
                requiresFullRescan = loss,
                completeWindow = !loss && !more,
                retainedOriginalsTruncated = rootMoves > originals.Count,
                originals,
                acknowledged = false,
                runtimeHandlerActivated = false,
                rootNotificationOwnershipAccepted = false,
            };
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Failure of optional observation cannot replace the original native
            // matrix outcome. An unavailable observation is never a passed ACK boundary.
            report = new
            {
                schemaVersion = 1,
                available = false,
                state = state.ToString(),
                cut,
                errorKind = failure.GetType().Name,
                hresult = failure.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
                nativeError = failure is System.ComponentModel.Win32Exception native ? (int?)native.NativeErrorCode : null,
                acknowledged = false,
                runtimeHandlerActivated = false,
                rootNotificationOwnershipAccepted = false,
            };
        }
        // Await feed disposal before emitting exactly one availability result.
        TestContext.WriteLine("RootLabelJournalOriginals: " + JsonSerializer.Serialize(report));
    }

    private static bool IsRootJournalName(string? path) => !string.IsNullOrWhiteSpace(path) &&
        !path.Contains('/') && !path.Contains('\\');
}
