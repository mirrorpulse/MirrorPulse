using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseJournalUploadPump
{
    private async ValueTask RecoverRootLabelJournalAsync(CancellationToken token)
    {
        if (_rootLabelJournalRecovery is not null)
            foreach (var attempt in await _rootLabelJournalRecovery.RecoverPendingAsync(token: token).ConfigureAwait(false))
            {
                if (attempt.Failure is null)
                {
                    _runner.ClearRecoveredFault(attempt.OperationId);
                    continue;
                }
                try
                {
                    await _log.WriteAsync(new(LogLevel.Warning, "CloudFiles.Upload", "RootLabelJournalRecoveryFailed", DateTimeOffset.UtcNow,
                        [new("operationId", attempt.OperationId.ToString("D")),
                     new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(attempt.Failure)),
                     new("rootLabelFailureBoundary", MirrorPulseRootLabelFailureDiagnostics.Classify(attempt.Failure).ToString()),
                     new("hresult", attempt.Failure.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))]), token).ConfigureAwait(false);
                }
                catch (Exception loggingFailure) when (loggingFailure is not OperationCanceledException) { }
            }
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) && _rootLabelAdmission is not null)
        {
            foreach (var failure in _rootLabelAdmission.TakeFailures())
            {
                try
                {
                    await _log.WriteAsync(new(LogLevel.Warning, "CloudFiles.Upload", "RootLabelJournalAdmissionFailed", DateTimeOffset.UtcNow,
                        [new("operationId", failure.OperationId.ToString("D")), new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(failure.Failure)),
                         new("rootLabelFailureBoundary", MirrorPulseRootLabelFailureDiagnostics.Classify(failure.Failure).ToString()),
                         new("hresult", failure.Failure.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))]), token).ConfigureAwait(false);
                }
                catch (Exception loggingFailure) when (loggingFailure is not OperationCanceledException) { }
            }
            foreach (var attempt in await _rootLabelAdmission.RecoverClosedAsync(token).ConfigureAwait(false))
            {
                if (attempt.Failure is null) continue;
                try
                {
                    await _log.WriteAsync(new(LogLevel.Warning, "CloudFiles.Upload", "RootLabelJournalReadmissionFailed", DateTimeOffset.UtcNow,
                        [new("rootId", attempt.RootId.ToString()), new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(attempt.Failure)),
                         new("rootLabelFailureBoundary", MirrorPulseRootLabelFailureDiagnostics.Classify(attempt.Failure).ToString()),
                         new("hresult", attempt.Failure.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))]), token).ConfigureAwait(false);
                }
                catch (Exception loggingFailure) when (loggingFailure is not OperationCanceledException) { }
            }
        }
    }
}
