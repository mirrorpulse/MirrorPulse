using System.Text.Json;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Host;

public enum AdapterAuthorizationPhase { Pending, ReadyToSave, Cancelled, Failed, Expired, Saved }

/// <summary>A receipt visible to clients. Returned authorization field values stay in the Host.</summary>
public sealed record AdapterAuthorizationStatus(Guid AuthorizationId, string DeploymentId, string CallbackId,
    DateTimeOffset ExpiresAt, AdapterAuthorizationPhase Phase, Uri? InteractionUri = null);

public sealed record AdapterAuthorizationStart(InstallId InstallId, InstanceId? InstanceId, InstanceId? DraftId,
    Guid? ExpectedRevision, string CallbackId, IReadOnlyDictionary<string, string> Configuration);

/// <summary>Owns short-lived authorization drafts in the current edition and consumes them during a form save.</summary>
public sealed class AdapterAuthorizationCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly MirrorPulseProductCatalog _catalog;
    private readonly ISecureCredentialStore _credentials;
    private readonly MirrorPulseAdapterInstanceProvisioner _provisioner;
    private readonly string _authorizationRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Authorization> _authorizations = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _expiry;

    public AdapterAuthorizationCoordinator(MirrorPulseProductCatalog catalog, ISecureCredentialStore credentials,
        MirrorPulseAdapterInstanceProvisioner provisioner, string dataRoot)
    {
        _catalog = catalog;
        _credentials = credentials;
        _provisioner = provisioner;
        _authorizationRoot = Path.Combine(dataRoot, "adapters", "authorization");
        _expiry = ExpireAsync();
    }

    public async ValueTask<AdapterAuthorizationStatus> StartAsync(AdapterAuthorizationStart arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments.Configuration);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReapAsync().ConfigureAwait(false);
            if (_authorizations.Count >= 32)
                throw new AdapterConfigurationValidationException("Too many recent Adapter authorizations. Try again shortly.");

            MirrorPulseAdapterTopology topology = await _catalog.ReadAdapterTopologyAsync(cancellationToken).ConfigureAwait(false);
            InstalledAdapter installation = topology.Installations.SingleOrDefault(item => item.InstallId == arguments.InstallId)
                ?? throw new AdapterConfigurationValidationException("The selected Adapter installation was not found.");
            AdapterInstance? deployment = arguments.InstanceId is { } id
                ? topology.Instances.SingleOrDefault(item => item.InstanceId == id)
                    ?? throw new AdapterConfigurationValidationException("The selected deployment was not found.") : null;
            if (deployment is not null && (deployment.InstallId != installation.InstallId ||
                    (deployment.ConfigurationRevision ?? Guid.Empty) != arguments.ExpectedRevision))
                throw new AdapterConfigurationRevisionException();
            if ((deployment is null && arguments.ExpectedRevision is not null) || (deployment is not null && arguments.DraftId is not null))
                throw new AdapterConfigurationValidationException("The authorization does not match the selected configuration target.");
            if (!installation.IsSigned || installation.Manifest.Presentation is not { } presentation)
                throw new NotSupportedException("Authorization requires a signed Adapter configuration form.");

            InstanceId target = deployment?.InstanceId ?? arguments.DraftId ?? InstanceId.New();
            if (arguments.DraftId is not null && !_authorizations.Values.Any(item => item.IsNew && item.InstallId == arguments.InstallId &&
                    item.Target == target && item.Status.Phase is AdapterAuthorizationPhase.Pending or AdapterAuthorizationPhase.ReadyToSave))
                throw new AdapterConfigurationValidationException("The new deployment authorization draft has expired.");
            if (_authorizations.Values.Any(item => item.Target == target && item.Request.CallbackId == arguments.CallbackId &&
                    item.Status.Phase is AdapterAuthorizationPhase.Pending or AdapterAuthorizationPhase.ReadyToSave))
                throw new AdapterConfigurationValidationException("Cancel the current authorization before starting it again.");

            var original = SignedAdapterFormValidator.Preview(presentation, deployment, arguments.Configuration);
            var request = new AdapterConfigurationCallbackRequest(Guid.NewGuid(), arguments.CallbackId, DateTimeOffset.UtcNow + Lifetime);
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            var session = await AdapterConfigurationWorkerSession.OpenAsync(installation, deployment, target,
                arguments.Configuration, _authorizationRoot, request, _credentials, requestCancellation.Token).ConfigureAwait(false);
            var entry = new Authorization(installation, target, deployment, request, original, session);
            try
            {
                await ExchangeAsync(entry, AdapterConfigurationCallbackContract.StartMessage, requestCancellation.Token).ConfigureAwait(false);
                _authorizations.Add(request.InvocationId, entry);
                return entry.Status;
            }
            catch
            {
                await entry.StopWorkerAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public ValueTask<AdapterAuthorizationStatus> PollAsync(Guid id, CancellationToken cancellationToken) =>
        AdvanceAsync(id, cancel: false, cancellationToken);

    public ValueTask<AdapterAuthorizationStatus> CancelAsync(Guid id, CancellationToken cancellationToken) =>
        AdvanceAsync(id, cancel: true, cancellationToken);

    private async ValueTask<AdapterAuthorizationStatus> AdvanceAsync(Guid id, bool cancel, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReapAsync().ConfigureAwait(false);
            Authorization entry = Find(id);
            if (entry.Status.Phase == AdapterAuthorizationPhase.Pending)
            {
                using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
                await ExchangeAsync(entry, cancel ? AdapterConfigurationCallbackContract.CancelMessage :
                    AdapterConfigurationCallbackContract.PollMessage, requestCancellation.Token).ConfigureAwait(false);
            }
            else if (cancel && entry.Status.Phase == AdapterAuthorizationPhase.ReadyToSave)
            {
                entry.SetPhase(AdapterAuthorizationPhase.Cancelled);
            }
            return entry.Status;
        }
        finally { _gate.Release(); }
    }

    public async Task<AdapterInstance> CreateAsync(MirrorPulseCreateInstanceRequest request,
        IReadOnlyList<Guid>? authorizations, CancellationToken cancellationToken)
    {
        if (authorizations is null or { Count: 0 })
            return await _provisioner.CreateAsync(request, cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReapAsync().ConfigureAwait(false);
            Authorization[] entries = FindCompleted(authorizations, null, null);
            if (entries.Any(entry => entry.InstallId != InstallId.Parse(request.InstallId)))
                throw new AdapterConfigurationValidationException("The authorization belongs to a different Adapter installation.");
            (var values, var secrets) = ApplyWrites(entries, request.Configuration, request.SecretFields);
            using CancellationTokenSource deadline = SaveDeadline(entries, cancellationToken);
            AdapterInstance instance = await _provisioner.CreateAsync(request with { Configuration = values, SecretFields = secrets },
                entries[0].Target, deadline.Token).ConfigureAwait(false);
            MarkSaved(entries);
            return instance;
        }
        finally { _gate.Release(); }
    }

    public async Task ConfigureAsync(InstanceId instanceId, string displayName, IReadOnlyDictionary<string, string> patch,
        IReadOnlyDictionary<string, string> rootLabels, string? secret, bool removeCredential,
        IReadOnlyDictionary<string, string?>? secretFields, Guid? revision, IReadOnlyList<Guid>? authorizations,
        CancellationToken cancellationToken)
    {
        if (authorizations is null or { Count: 0 })
        {
            await _provisioner.ConfigureAsync(instanceId, displayName, patch, rootLabels, secret, removeCredential,
                secretFields, revision, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReapAsync().ConfigureAwait(false);
            Authorization[] entries = FindCompleted(authorizations, instanceId, revision);
            (var values, var secrets) = ApplyWrites(entries, patch, secretFields);
            using CancellationTokenSource deadline = SaveDeadline(entries, cancellationToken);
            await _provisioner.ConfigureAsync(instanceId, displayName, values, rootLabels, secret, removeCredential,
                secrets, revision, deadline.Token).ConfigureAwait(false);
            MarkSaved(entries);
        }
        finally { _gate.Release(); }
    }

    private Authorization Find(Guid id) => _authorizations.TryGetValue(id, out Authorization? entry) ? entry
        : throw new AdapterConfigurationValidationException("The authorization has expired or belongs to another Host.");

    private Authorization[] FindCompleted(IReadOnlyList<Guid> ids, InstanceId? target, Guid? revision)
    {
        if (ids.Count > 16 || ids.Distinct().Count() != ids.Count)
            throw new AdapterConfigurationValidationException("Supply at most 16 distinct Adapter authorizations.");
        Authorization[] entries = ids.Select(Find).ToArray();
        InstanceId expectedTarget = target ?? entries[0].Target;
        if (entries.Any(entry => entry.Target != expectedTarget || entry.IsNew != (target is null) ||
                entry.Revision != revision || entry.Status.Phase != AdapterAuthorizationPhase.ReadyToSave))
            throw new AdapterConfigurationValidationException("The authorization is no longer available for this configuration.");
        return entries;
    }

    private static (Dictionary<string, string> Values, Dictionary<string, string?> Secrets) ApplyWrites(
        Authorization[] entries, IReadOnlyDictionary<string, string> values, IReadOnlyDictionary<string, string?>? secrets)
    {
        ArgumentNullException.ThrowIfNull(values);
        var updatedValues = new Dictionary<string, string>(values, StringComparer.Ordinal);
        Dictionary<string, string?> updatedSecrets = secrets is null ? new(StringComparer.Ordinal) : new(secrets, StringComparer.Ordinal);
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (Authorization entry in entries)
        {
            var effective = SignedAdapterFormValidator.Preview(entry.Presentation, entry.Deployment, values);
            if (entry.Input.Any(pair => !effective.TryGetValue(pair.Key, out string? value) || value != pair.Value))
                throw new AdapterConfigurationValidationException("A setting used during authorization changed. Authorize the updated configuration before saving.");

            foreach (AdapterConfigurationWrite write in entry.Writes)
            {
                if (!written.Add(write.Key) || updatedSecrets.ContainsKey(write.Key))
                    throw new AdapterConfigurationValidationException("More than one value was supplied for an authorization field.");
                if (entry.PasswordFields.Contains(write.Key)) updatedSecrets.Add(write.Key, write.Value);
                else updatedValues[write.Key] = write.Value;
            }
        }
        return (updatedValues, updatedSecrets);
    }

    private CancellationTokenSource SaveDeadline(Authorization[] entries, CancellationToken cancellationToken)
    {
        TimeSpan remaining = entries.Min(entry => entry.Request.ExpiresAt) - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new AdapterConfigurationValidationException("The authorization expired before the configuration was saved.");
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        deadline.CancelAfter(remaining);
        return deadline;
    }

    private static void MarkSaved(Authorization[] entries)
    {
        foreach (Authorization entry in entries) entry.SetPhase(AdapterAuthorizationPhase.Saved);
    }

    private static async Task ExchangeAsync(Authorization entry, string message, CancellationToken cancellationToken)
    {
        bool cancel = message == AdapterConfigurationCallbackContract.CancelMessage;
        try
        {
            var result = await entry.Session!.ExchangeAsync(message, cancellationToken).ConfigureAwait(false);
            switch (result.State)
            {
                case AdapterConfigurationCallbackState.Pending:
                    entry.Status = entry.Status with { InteractionUri = result.InteractionUri ?? entry.Status.InteractionUri };
                    return;
                case AdapterConfigurationCallbackState.Completed:
                    entry.SetPhase(AdapterAuthorizationPhase.ReadyToSave);
                    entry.Writes = result.Writes ?? [];
                    break;
                case AdapterConfigurationCallbackState.Cancelled:
                    entry.SetPhase(AdapterAuthorizationPhase.Cancelled);
                    break;
                default:
                    entry.SetPhase(cancel ? AdapterAuthorizationPhase.Cancelled : AdapterAuthorizationPhase.Failed);
                    break;
            }
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or JsonException or
            UnauthorizedAccessException or KeyNotFoundException or OperationCanceledException)
        {
            entry.SetPhase(cancel ? AdapterAuthorizationPhase.Cancelled : DateTimeOffset.UtcNow >= entry.Request.ExpiresAt
                ? AdapterAuthorizationPhase.Expired : AdapterAuthorizationPhase.Failed);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            if (entry.Status.Phase != AdapterAuthorizationPhase.Pending)
                await entry.StopWorkerAsync().ConfigureAwait(false);
        }
    }

    private async Task ReapAsync()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (Authorization entry in _authorizations.Values)
        {
            if (entry.Request.ExpiresAt > now) continue;
            if (entry.Status.Phase is AdapterAuthorizationPhase.Pending or AdapterAuthorizationPhase.ReadyToSave)
            {
                entry.SetPhase(AdapterAuthorizationPhase.Expired);
                await entry.StopWorkerAsync().ConfigureAwait(false);
            }
        }
        foreach (Guid id in _authorizations.Where(pair => pair.Value.Request.ExpiresAt.AddMinutes(1) <= now)
            .Select(pair => pair.Key).ToArray()) _authorizations.Remove(id);
    }

    private async Task ExpireAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                await _gate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                try { await ReapAsync().ConfigureAwait(false); }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _expiry.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (Authorization entry in _authorizations.Values) await entry.StopWorkerAsync().ConfigureAwait(false);
            _authorizations.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _shutdown.Dispose();
        }
    }

    private sealed class Authorization(InstalledAdapter installation, InstanceId target, AdapterInstance? instance,
        AdapterConfigurationCallbackRequest request, Dictionary<string, string> input, AdapterConfigurationWorkerSession session)
    {
        public InstallId InstallId { get; } = installation.InstallId;
        public InstanceId Target { get; } = target;
        public bool IsNew { get; } = instance is null;
        public Guid? Revision { get; } = instance is null ? null : instance.ConfigurationRevision ?? Guid.Empty;
        public AdapterInstance? Deployment { get; } = instance;
        public AdapterPackagePresentation Presentation { get; } = installation.Manifest.Presentation!;
        public AdapterConfigurationCallbackRequest Request { get; } = request;
        public Dictionary<string, string> Input { get; } = input;
        public HashSet<string> PasswordFields { get; } = installation.Manifest.Presentation!.Resolve(null).Controls
            .Where(control => control.Kind == AdapterPackageFormKind.Password).Select(control => control.Key!).ToHashSet(StringComparer.Ordinal);
        public AdapterConfigurationWorkerSession? Session { get; private set; } = session;
        public IReadOnlyList<AdapterConfigurationWrite> Writes { get; set; } = [];
        public AdapterAuthorizationStatus Status { get; set; } = new(request.InvocationId, target.ToString(), request.CallbackId,
            request.ExpiresAt, AdapterAuthorizationPhase.Pending);

        public void SetPhase(AdapterAuthorizationPhase phase)
        {
            Status = Status with { Phase = phase, InteractionUri = null };
            Writes = [];
        }

        public async ValueTask StopWorkerAsync()
        {
            AdapterConfigurationWorkerSession? worker = Session;
            Session = null;
            if (worker is not null) await worker.DisposeAsync().ConfigureAwait(false);
        }
    }
}
