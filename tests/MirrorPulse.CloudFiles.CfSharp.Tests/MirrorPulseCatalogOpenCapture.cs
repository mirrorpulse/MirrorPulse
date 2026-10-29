using System.Diagnostics;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

/// <summary>Consumes only closed catalog phase names and anonymous elapsed-time tuples in disposable verification.</summary>
internal sealed class MirrorPulseCatalogOpenCapture : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private const string SourceName = "MirrorPulse.ProductCatalog.Open";
    private static readonly HashSet<string> Phases = new(StringComparer.Ordinal)
    {
        "OwnerLeaseOpening", "OwnerLeaseOpened", "ConnectionOpening", "ConnectionOpened", "JournalModeSelecting", "JournalModeSelected",
        "SchemaVersionReading", "SchemaVersionRead", "BaseSchemaCreating", "BaseSchemaCreated", "LegacyColumnsChecking", "LegacyColumnsChecked",
        "NamespaceSchemaPreparing", "NamespaceSchemaPrepared", "TransferSchemaPreparing", "TransferSchemaPrepared",
        "RecoverySchemaPreparing", "RecoverySchemaPrepared", "Completed"
    };
    private readonly Action<string, Guid, long> _write;
    private readonly List<IDisposable> _events = [];
    private readonly IDisposable _sources;
    private bool _disposed;

    internal MirrorPulseCatalogOpenCapture(Action<string, Guid, long> write)
    {
        _write = write; _sources = DiagnosticListener.AllListeners.Subscribe(this);
    }

    public void OnNext(DiagnosticListener value)
    {
        if (value.Name != SourceName) return;
        lock (_events) { if (!_disposed) _events.Add(value.Subscribe(this)); }
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (Phases.Contains(value.Key) && value.Value is ValueTuple<Guid, long> observation && observation.Item1 != Guid.Empty && observation.Item2 >= 0)
            _write(value.Key, observation.Item1, observation.Item2);
    }

    public void OnError(Exception error) { }
    public void OnCompleted() { }

    public void Dispose()
    {
        _sources.Dispose();
        lock (_events)
        {
            _disposed = true;
            foreach (var subscription in _events) subscription.Dispose();
            _events.Clear();
        }
    }
}
