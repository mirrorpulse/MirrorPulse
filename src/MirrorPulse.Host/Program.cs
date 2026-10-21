using MirrorPulse.Core;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.Host;
using MirrorPulse.Host;

if (!MirrorPulsePlatform.IsCurrentSupported())
{
    Console.Error.WriteLine(MirrorPulsePlatform.Requirement);
    return 8;
}

if (args.Length > 1 || (args.Length == 1 && args[0] != "--run-once"))
{
    Console.Error.WriteLine("Usage: MirrorPulse.Host [--run-once]");
    return 2;
}

string dataRoot = Environment.GetEnvironmentVariable("MIRRORPULSE_DATA_ROOT") is { Length: > 0 } configuredDataRoot
    ? Path.GetFullPath(configuredDataRoot)
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductInfo.Name);
string syncRoot = Environment.GetEnvironmentVariable("MIRRORPULSE_SYNC_ROOT") is { Length: > 0 } configuredSyncRoot
    ? Path.GetFullPath(configuredSyncRoot)
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ProductInfo.Name);
var paths = new MirrorPulseStoragePaths(syncRoot, dataRoot);

try
{
    var application = await MirrorPulseHostApplication.CreateAsync(paths);
    using var shutdown = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };
    Console.CancelKeyPress += cancel;
    Exception? failure = null;
    try
    {
        if (args.Length == 0)
        {
            await application.RunAsync(shutdown.Token);
        }
        else
        {
            await application.StartAsync(shutdown.Token);
            Console.WriteLine($"{ProductInfo.Name} Cloud Files session started at {paths.SyncRootPath}.");
        }
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested || application.ShutdownRequested)
    {
    }
    catch (Exception original) { failure = original; }
    finally
    {
        Console.CancelKeyPress -= cancel;
    }
    await MirrorPulseHostResourceCleanup.RunAsync([application.DisposeAsync], failure);
    return 0;
}
catch (Exception exception)
{
    try
    {
        using var log = new LocalRollingLogWriter(Path.Combine(paths.DataRootPath, "logs"));
        Exception originalStartupFailure = MirrorPulseStartupFailureTrace.ReadOriginalException(exception);
        string code = exception.Message switch
        {
            "The actual Shell root policy does not match content synchronization." => "ShellRootPolicyMismatch",
            "CfSharp registration metadata does not match the MirrorPulse root." => "CloudRootMetadataMismatch",
            "The existing Cloud Files registration belongs to another root identity." => "CloudRootIdentityMismatch",
            "The existing Shell registration belongs to another provider." => "ShellRootOwnershipMismatch",
            "The MirrorPulse Shell registration belongs to another sync root." => "ShellRootPathMismatch",
            "Anchor protection requires the actual single-link current-user sync-root directory." => "NamespaceAnchorBindingMismatch",
            "The sync-root anchor owner does not match the current Host user." => "NamespaceAnchorOwnerMismatch",
            _ when exception.Message.StartsWith("Shell sync-root registration failed with HRESULT", StringComparison.Ordinal) => "ShellRootRegistrationFailed",
            _ => "HostStartupFailed",
        };
        await log.WriteAsync(new LogEntry(LogLevel.Error, "host", code, DateTimeOffset.UtcNow,
            [new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(originalStartupFailure)),
                new("failureSite", MirrorPulseStartupFailureTrace.ReadSite(exception).ToString()),
                new("failureKind", MirrorPulseStartupFailureTrace.ReadKind(exception)),
                new("hresult", originalStartupFailure.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))]));
    }
    catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
    {
        // Preserve the original startup failure when local diagnostics are unavailable.
    }
    Console.Error.WriteLine($"{ProductInfo.Name} Host failed: {exception.Message}");
    return 1;
}
