using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseJournalUploadPump
{
    private async ValueTask RecoverRootLabelJournalAsync(CancellationToken token)
    {
        if (_rootLabelJournalRecovery is null) return;
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
                     new("hresult", attempt.Failure.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))]), token).ConfigureAwait(false);
            }
            catch (Exception loggingFailure) when (loggingFailure is not OperationCanceledException) { }
        }
    }
}
