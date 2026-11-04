using System.Diagnostics;
using CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseProviderReadStage { Routing, WorkAdmission, IdentityValidation, StreamCreation, RangeTransport, RangeContent }

/// <summary>Attaches closed failure facts to the original public FetchData activity without changing its outcome.</summary>
internal static class MirrorPulseProviderReadFailure
{
    internal const string StageTag = "mirrorpulse.provider.read.failure_stage";
    internal const string KindTag = "mirrorpulse.provider.read.failure_kind";

    internal static void Record(MirrorPulseProviderReadStage stage, Exception error)
    {
        try
        {
            if (!Enum.IsDefined(stage)) return;
            int depth = 0;
            for (var activity = Activity.Current; activity is not null && depth++ < 8; activity = activity.Parent)
            {
                if (activity.Source.Name != CloudDiagnostics.ActivitySourceName ||
                    activity.GetTagItem("cfsharp.operation") is not string operation ||
                    operation[(operation.LastIndexOf('.') + 1)..] != "FetchData") continue;
                if (activity.GetTagItem(StageTag) is not null) return;
                activity.SetTag(StageTag, stage.ToString());
                activity.SetTag(KindTag, error switch
                {
                    ObjectDisposedException => "Disposed",
                    InvalidOperationException => "InvalidOperation",
                    InvalidDataException => "InvalidData",
                    IOException => "IO",
                    UnauthorizedAccessException => "Authorization",
                    OperationCanceledException => "Cancelled",
                    ArgumentException => "Argument",
                    _ => "Other"
                });
                return;
            }
        }
        catch { /* Diagnostic observation cannot replace the original provider failure. */ }
    }
}
