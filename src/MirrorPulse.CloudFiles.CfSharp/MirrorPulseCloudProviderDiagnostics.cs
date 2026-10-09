using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Records failures from the public CfSharp diagnostics source in bounded local logs.</summary>
public sealed class MirrorPulseCloudProviderDiagnostics : IAsyncDisposable
{
    private readonly LocalRollingLogWriter _writer;
    private readonly Channel<LogEntry> _entries = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(128)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly ActivityListener _listener;
    private readonly Task _drain;
    private int _disposed;

    public MirrorPulseCloudProviderDiagnostics(string directory)
    {
        _writer = new LocalRollingLogWriter(directory);
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CloudDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = Observe,
        };
        _drain = DrainAsync();
        ActivitySource.AddActivityListener(_listener);
    }

    private void Observe(Activity activity)
    {
        try
        {
            object? value = activity.GetTagItem("cfsharp.error.hresult") ?? activity.GetTagItem("cfsharp.native.hresult");
            if (value is not int hresult || hresult >= 0) return;
            string operation = activity.GetTagItem("cfsharp.operation") as string ?? string.Empty;
            string phase = operation[(operation.LastIndexOf('.') + 1)..];
            string requestKind = Enum.TryParse(phase, out CloudProviderRequestKind kind) && Enum.IsDefined(kind)
                ? kind.ToString() : "Other";
            string failure = activity.GetTagItem("cfsharp.error.type") switch
            {
                nameof(IOException) => "IO",
                nameof(InvalidDataException) => "InvalidData",
                nameof(UnauthorizedAccessException) => "Authorization",
                nameof(OperationCanceledException) or nameof(TaskCanceledException) => "Cancelled",
                _ => "Internal",
            };
            // Never copy arbitrary activity tags, exception messages, paths or identities.
            // TryWrite cannot delay or change a Windows callback when the queue is full.
            _entries.Writer.TryWrite(new(LogLevel.Warning, "CloudFiles.Provider", "CloudProviderRequestFailed", DateTimeOffset.UtcNow,
                [new("hresult", unchecked((uint)hresult).ToString("X8", CultureInfo.InvariantCulture)),
                    new("cloudRequestKind", requestKind), new("failureCategory", failure)]));
        }
        catch
        {
            // Diagnostics cannot alter the provider's original completion result.
        }
    }

    private async Task DrainAsync()
    {
        await foreach (LogEntry entry in _entries.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { await _writer.WriteAsync(entry).ConfigureAwait(false); }
            catch { /* A local log failure cannot interrupt Cloud Files synchronization. */ }
        }
        _writer.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _listener.Dispose();
            _entries.Writer.TryComplete();
        }
        await _drain.ConfigureAwait(false);
    }
}
