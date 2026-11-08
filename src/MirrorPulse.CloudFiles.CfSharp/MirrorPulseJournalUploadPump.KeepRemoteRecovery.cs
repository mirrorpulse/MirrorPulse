using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseJournalUploadPump
{
    private Guid? _keepRemotePreparationAfter;

    internal async ValueTask<bool> WaitForKeepRemoteReconciliationAsync(CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();
        while (_feed.IsStarted && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
        {
            if (!(await _feed.BeginScanAsync(token).ConfigureAwait(false)).RequiresFullRescan &&
                await _catalog.ReadFullRescanAsync(token).ConfigureAwait(false) is null)
                return true;
            await Task.Delay(100, token).ConfigureAwait(false);
        }
        return false;
    }

    private async ValueTask RecoverKeepRemotePreparationsAsync(MirrorPulseUploadConflictActions actions,
        MirrorPulseNamespaceWorkAdmission namespaceWork, CancellationToken token)
    {
        var pending = await _catalog.ReadPendingConflictKeepRemotePreparationsAsync(_keepRemotePreparationAfter, token).ConfigureAwait(false);
        foreach (Guid commandId in pending)
        {
            token.ThrowIfCancellationRequested();
            _keepRemotePreparationAfter = commandId;
            try
            {
                var preparation = await _catalog.ReadConflictKeepRemoteAsync(commandId, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The original pending KeepRemote decision disappeared.");
                var intent = preparation.Intent;
                var root = _router.GetRegistration(intent.InstanceId, intent.RemoteSource.RootKey);
                if (root.State != RootRegistrationState.Active ||
                    preparation.RemoteCopy is null && !_mayDispatch(root.InstanceId) ||
                    !namespaceWork.TryEnter([root.RootId], out var work)) continue;
                using (work)
                {
                    bool recovered = await MirrorPulseUploadConflictActions.RetryKeepRemoteProtectionLossAsync(() =>
                        _scheduler.RunAsync(root.InstanceId, async stop =>
                        {
                            // An explicit command may have finished while this reader waited for the instance.
                            var current = await _catalog.ReadUserCommandAsync(commandId, stop).ConfigureAwait(false);
                            if (current?.State is not ("pending" or "failed"))
                                return false;
                            _ = await actions.ExecuteKeepRemoteAdmittedAsync(commandId, root.RootId, stop).ConfigureAwait(false);
                            return true;
                        }, token)).ConfigureAwait(false);
                    if (recovered) return;
                }
            }
            catch (MirrorPulseKeepRemoteJournalDeferredException)
            {
                // This cycle must reach journal dispatch or reconciliation before retrying.
                return;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or
                UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception or JsonException)
            {
                try
                {
                    var fields = new List<LogField> { new("commandId", commandId.ToString("D")),
                        new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(error)),
                        new("journalReadFailureKind", MirrorPulseJournalReadDiagnostics.Kind(error)),
                        new("hresult", error.HResult.ToString("X8", CultureInfo.InvariantCulture)) };
                    if (error is Win32Exception native) fields.Add(new("nativeErrorCode", native.NativeErrorCode.ToString(CultureInfo.InvariantCulture)));
                    await _log.WriteAsync(new(LogLevel.Warning, "CloudFiles.Upload", "ConflictKeepRemotePreparationRecoveryFailed",
                        DateTimeOffset.UtcNow, fields), token).ConfigureAwait(false);
                }
                catch (Exception loggingFailure) when (loggingFailure is not OperationCanceledException) { }
                return;
            }
        }
        if (pending.Count < 64) _keepRemotePreparationAfter = null;
    }
}
