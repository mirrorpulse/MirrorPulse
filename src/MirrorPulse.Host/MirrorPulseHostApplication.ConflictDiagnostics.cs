using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.Host;

namespace MirrorPulse.Host;

public sealed partial class MirrorPulseHostApplication
{
    private Task<MirrorPulseAppStatusResponse> ResolveConflictAsync(Guid conflictId, MirrorPulseConflictAction action,
        CancellationToken cancellationToken) => MirrorPulseConflictCommandDiagnostics.RunAsync(conflictId,
            () => ResolveConflictCoreAsync(conflictId, action, cancellationToken), async entry =>
            {
                using var log = new LocalRollingLogWriter(Path.Combine(_paths.DataRootPath, "logs", "cloud-files"));
                await log.WriteAsync(entry, CancellationToken.None).ConfigureAwait(false);
            });
}
