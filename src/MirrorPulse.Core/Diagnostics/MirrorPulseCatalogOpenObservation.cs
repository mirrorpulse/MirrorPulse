using System.Diagnostics;

namespace MirrorPulse.Core.Diagnostics;

internal enum MirrorPulseCatalogOpenPhase
{
    OwnerLeaseOpening, OwnerLeaseOpened, ConnectionOpening, ConnectionOpened,
    JournalModeSelecting, JournalModeSelected, SchemaVersionReading, SchemaVersionRead,
    BaseSchemaCreating, BaseSchemaCreated, LegacyColumnsChecking, LegacyColumnsChecked,
    NamespaceSchemaPreparing, NamespaceSchemaPrepared, TransferSchemaPreparing, TransferSchemaPrepared,
    RecoverySchemaPreparing, RecoverySchemaPrepared, Completed
}

/// <summary>Optional closed-phase timing for existing catalog work; never an initialization or readiness witness.</summary>
internal readonly struct MirrorPulseCatalogOpenObservation
{
    internal const string SourceName = "MirrorPulse.ProductCatalog.Open";
    private static readonly Lazy<DiagnosticListener> Listener = new(() => new(SourceName));
    private readonly Guid _traceId;
    private readonly long _started;

    private MirrorPulseCatalogOpenObservation(Guid traceId, long started) { _traceId = traceId; _started = started; }

    internal static MirrorPulseCatalogOpenObservation Begin()
    {
        try
        {
            return Listener.Value.IsEnabled() ? new(Guid.NewGuid(), Stopwatch.GetTimestamp()) : default;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { return default; }
    }

    internal void Mark(MirrorPulseCatalogOpenPhase phase)
    {
        if (_traceId == Guid.Empty || !Enum.IsDefined(phase)) return;
        try
        {
            string name = phase.ToString();
            if (Listener.Value.IsEnabled(name))
                Listener.Value.Write(name, (_traceId, (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds));
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { }
    }
}
