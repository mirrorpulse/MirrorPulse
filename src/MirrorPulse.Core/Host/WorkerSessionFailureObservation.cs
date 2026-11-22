using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Host;

internal enum WorkerSessionStage
{
    ResolvePayload, CreatePipe, CreateJob, StartProcess, AttachJob, AwaitPipe,
    ValidatePeer, AwaitHello, Negotiate, SendReady, ReceiveFrames, StopWorker,
}

/// <summary>Captures bounded facts before Worker shutdown can change process state.</summary>
internal sealed class WorkerSessionFailureObservation
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _stageStarted = Stopwatch.GetTimestamp();
    private WorkerSessionStage _stage = WorkerSessionStage.ResolvePayload;

    public bool DeadlineExpired { get; set; }

    public void Advance(WorkerSessionStage stage)
    {
        _stage = stage;
        _stageStarted = Stopwatch.GetTimestamp();
        DeadlineExpired = false;
    }

    public LogField[] Capture(WorkerSessionId sessionId, Process? process = null, PipeStream? pipe = null)
    {
        var fields = new List<LogField>
        {
            new("workerSessionId", sessionId.ToString()),
            new("workerStage", _stage.ToString()),
            new("workerElapsedMs", Elapsed(_started)),
            new("workerStageElapsedMs", Elapsed(_stageStarted)),
            new("workerDeadlineExpired", DeadlineExpired.ToString()),
            new("workerProcessStarted", (process is not null).ToString()),
            new("workerPipeConnected", (pipe?.IsConnected == true).ToString()),
        };
        bool observed = false;
        if (process is not null)
        {
            try
            {
                bool exited = process.HasExited;
                fields.Add(new("workerProcessHasExited", exited.ToString()));
                if (exited) fields.Add(new("workerProcessExitCode", process.ExitCode.ToString(CultureInfo.InvariantCulture)));
                observed = true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
        }
        fields.Add(new("workerProcessStateObserved", observed.ToString()));
        return fields.ToArray();
    }

    private static string Elapsed(long started) =>
        Math.Min(int.MaxValue, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
}
