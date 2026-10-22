using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MirrorPulse.Core.Diagnostics;

public enum MirrorPulseStartupFailureSite
{
    Unknown, InitializationRead, InitializationAdmission, InitializationInventory, InitializationAnchorProtection,
    InitializationAnchorRegistration, InitializationAudit, AnchorGuard, AnchorHistory, AnchorPermissionPreparation,
    AnchorProtectedPermission, AnchorVerification, AnchorPermissionPolicy, AnchorPermissionCatalog
}

/// <summary>Retains only the first closed startup call boundary without wrapping or changing the original exception.</summary>
public static class MirrorPulseStartupFailureTrace
{
    private sealed record Boundary(MirrorPulseStartupFailureSite Site);
    private static readonly ConditionalWeakTable<Exception, Boundary> Boundaries = new();

    public static void Record(Exception error, MirrorPulseStartupFailureSite site)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!Enum.IsDefined(site)) throw new ArgumentOutOfRangeException(nameof(site));
        _ = Boundaries.GetValue(ReadOriginalException(error), _ => new(site));
    }

    public static MirrorPulseStartupFailureSite ReadSite(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Boundaries.TryGetValue(ReadOriginalException(error), out var boundary) ? boundary.Site : MirrorPulseStartupFailureSite.Unknown;
    }

    public static string ReadKind(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return ReadOriginalException(error) switch
        {
            FileNotFoundException => "FileNotFound",
            DirectoryNotFoundException => "DirectoryNotFound",
            DllNotFoundException => "DllNotFound",
            Win32Exception => "Native",
            _ => "Other"
        };
    }

    /// <summary>Unwraps single-error cleanup chains while keeping multiple independent failures together.</summary>
    public static Exception ReadOriginalException(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        // AggregateException.GetBaseException stops at a non-aggregate wrapper.
        // Inspect each wrapper explicitly; never select one child of multiple failures.
        while (true)
        {
            if (error is AggregateException aggregate)
            {
                if (aggregate.InnerExceptions.Count != 1) return aggregate;
                error = aggregate.InnerExceptions[0];
            }
            else if (error.InnerException is { } inner) error = inner;
            else return error;
        }
    }
}
