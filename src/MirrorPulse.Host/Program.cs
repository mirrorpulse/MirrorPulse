using MirrorPulse.Core;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
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
    await using var application = await MirrorPulseHostApplication.CreateAsync(paths);
    using var shutdown = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };
    Console.CancelKeyPress += cancel;
    try
    {
        await application.StartAsync(shutdown.Token);
        Console.WriteLine($"{ProductInfo.Name} Cloud Files session started at {paths.SyncRootPath}.");
        if (args.Length == 0)
        {
            await application.ServeAsync(shutdown.Token);
        }

        return 0;
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
        return 0;
    }
    finally
    {
        Console.CancelKeyPress -= cancel;
    }
}
catch (Exception exception)
{
    try
    {
        using var log = new LocalRollingLogWriter(Path.Combine(paths.DataRootPath, "logs"));
        string code = exception.Message switch
        {
            "The actual Shell root policy does not match content synchronization." => "ShellRootPolicyMismatch",
            "CfSharp registration metadata does not match the MirrorPulse root." => "CloudRootMetadataMismatch",
            "The existing Cloud Files registration belongs to another root identity." => "CloudRootIdentityMismatch",
            "The existing Shell registration belongs to another provider." => "ShellRootOwnershipMismatch",
            "The MirrorPulse Shell registration belongs to another sync root." => "ShellRootPathMismatch",
            _ when exception.Message.StartsWith("Shell sync-root registration failed with HRESULT", StringComparison.Ordinal) => "ShellRootRegistrationFailed",
            _ => "HostStartupFailed",
        };
        await log.WriteAsync(new LogEntry(LogLevel.Error, "host", code, DateTimeOffset.UtcNow,
            [new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(exception)),
                new("hresult", exception.GetBaseException().HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))]));
    }
    catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
    {
        // Preserve the original startup failure when local diagnostics are unavailable.
    }
    Console.Error.WriteLine($"{ProductInfo.Name} Host failed: {exception.Message}");
    return 1;
}
