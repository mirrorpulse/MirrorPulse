using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    private sealed class RootLabelJournalObservation(TestContext context, CloudFileSystem fileSystem,
        Guid originalRootItemId, RootRegistrationState state, string cut, int owner) : IAsyncDisposable
    {
        private CloudLocalChangeFeed? _feed;
        private object? _report;
        private Exception? _failure;
        private bool _startedBeforeOperations;
        private string _stage = "start";

        internal async Task StartAsync(CancellationToken token)
        {
            try
            {
                _feed = fileSystem.CreateLocalChangeFeed();
                await _feed.StartAsync(token);
                _startedBeforeOperations = true;
                _stage = "operations";
            }
            catch (Exception failure) when (failure is not OutOfMemoryException) { _failure = failure; }
        }

        internal async Task CaptureAsync(string currentRootName, CancellationToken token)
        {
            if (_failure is not null) return;
            _stage = "scan";
            try
            {
                // Public scans include durable rows through a finite boundary;
                // they do not wait for native events still being normalized.
                var scan = await _feed!.BeginScanAsync(token);
                var current = await fileSystem.GetDirectory(currentRootName).InspectAsync(token);
                var originals = new List<object>();
                long after = 0;
                int pages = 0, scanned = 0, rootMoves = 0, unassigned = 0;
                bool more = !scan.RequiresFullRescan;
                bool loss = scan.RequiresFullRescan;
                while (more && pages < 8)
                {
                    var page = await _feed.ReadPageAsync(scan, after, 64, token);
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
                _report = new
                {
                    schemaVersion = 2,
                    available = true,
                    state = state.ToString(),
                    cut,
                    owner,
                    watcherStartedBeforeOperations = _startedBeforeOperations,
                    nativeQueueDrainProven = false,
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
                _stage = "captured";
            }
            catch (Exception failure) when (failure is not OutOfMemoryException) { _failure = failure; }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_feed is not null) await _feed.DisposeAsync();
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                _failure ??= failure;
                _stage = "dispose";
            }
            // An optional observer never replaces an original matrix failure.
            // Emit once, only after closing the watcher that covered this owner.
            object report = _failure is null && _report is not null ? _report : new
            {
                schemaVersion = 2,
                available = false,
                state = state.ToString(),
                cut,
                owner,
                stage = _stage,
                watcherStartedBeforeOperations = _startedBeforeOperations,
                nativeQueueDrainProven = false,
                errorKind = _failure?.GetType().Name,
                hresult = _failure?.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
                nativeError = _failure is System.ComponentModel.Win32Exception native ? (int?)native.NativeErrorCode : null,
                acknowledged = false,
                runtimeHandlerActivated = false,
                rootNotificationOwnershipAccepted = false,
            };
            context.WriteLine("RootLabelJournalOriginals: " + JsonSerializer.Serialize(report));
        }
    }

    private static bool IsRootJournalName(string? path) => !string.IsNullOrWhiteSpace(path) &&
        !path.Contains('/') && !path.Contains('\\');
}
