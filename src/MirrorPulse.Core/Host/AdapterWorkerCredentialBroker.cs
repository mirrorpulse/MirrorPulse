using System.Text;
using System.Text.Json;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Host;

internal static class AdapterWorkerCredentialBroker
{
    public static async Task<ControlFrameEnvelope> CreateResponseAsync(ISecureCredentialStore credentials,
        AdapterInstance instance, ControlFrameEnvelope request, int version, CancellationToken cancellationToken)
    {
        string referenceId = request.Payload.GetProperty("referenceId").GetString()
            ?? throw new InvalidDataException("The Worker credential reference is missing.");
        if (!instance.CredentialReferences.Contains(referenceId, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("The Worker requested a credential outside its instance.");

        var reference = new CredentialReference(referenceId, CredentialKind.Password,
            instance.AdapterId.ToString(), CredentialScope.CurrentUser, DateTimeOffset.UtcNow);
        using SecureCredentialValue stored = await credentials.TryGetAsync(reference, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException("The Adapter credential was not found.");
        return new(version, "CredentialResponse", request.RequestId, instance.InstanceId,
            request.WorkerSessionId, true, JsonSerializer.SerializeToElement(new
            {
                referenceId,
                secret = Encoding.UTF8.GetString(stored.Value.Span),
            }));
    }
}
