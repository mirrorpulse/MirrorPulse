using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Host;

internal sealed class AdapterConfigurationCallbackPolicy
{
    private readonly AdapterPackageFormControl _callback;
    private readonly Dictionary<string, AdapterPackageFormControl> _fields;

    public AdapterConfigurationCallbackPolicy(AdapterPackagePresentation presentation, string callbackId)
    {
        IReadOnlyList<AdapterPackageFormControl> controls = presentation.Resolve(null).Controls;
        _callback = controls.SingleOrDefault(control => control.Kind == AdapterPackageFormKind.Callback &&
            string.Equals(control.Key, callbackId, StringComparison.Ordinal))
            ?? throw new AdapterConfigurationValidationException("The selected Adapter does not declare this authorization callback.");
        _fields = controls.Where(control => control.Key is not null && control.Kind != AdapterPackageFormKind.Callback)
            .ToDictionary(control => control.Key!, StringComparer.Ordinal);
    }

    public void Validate(AdapterConfigurationCallbackRequest request, AdapterConfigurationCallbackResult result,
        DateTimeOffset now, bool cancelling)
    {
        if (request.InvocationId == Guid.Empty || request.ExpiresAt <= now || request.InvocationId != result.InvocationId ||
            !string.Equals(_callback.Key, result.CallbackId, StringComparison.Ordinal))
            throw new InvalidDataException("The Adapter response does not match a live authorization.");

        IReadOnlyList<AdapterConfigurationWrite> writes = result.Writes ?? [];
        switch (result.State)
        {
            case AdapterConfigurationCallbackState.Pending when !cancelling && writes.Count == 0 && result.ErrorCode is null &&
                (result.InteractionUri is null || IsInteractionUri(result.InteractionUri)):
            case AdapterConfigurationCallbackState.Cancelled when writes.Count == 0 && result.InteractionUri is null && result.ErrorCode is null:
            case AdapterConfigurationCallbackState.Failed when writes.Count == 0 && result.InteractionUri is null && IsErrorCode(result.ErrorCode):
                return;
            case AdapterConfigurationCallbackState.Completed when !cancelling && result.InteractionUri is null && result.ErrorCode is null:
                ValidateWrites(writes);
                return;
            default:
                throw new InvalidDataException("The Adapter returned an invalid authorization state.");
        }
    }

    private void ValidateWrites(IReadOnlyList<AdapterConfigurationWrite> writes)
    {
        IReadOnlyList<string> allowed = _callback.WritableFields ?? [];
        if (writes.Count == 0 || writes.Count > allowed.Count)
            throw new InvalidDataException("The Adapter returned an invalid number of authorization fields.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (AdapterConfigurationWrite write in writes)
        {
            if (write?.Key is not { } key || !seen.Add(key) || !allowed.Contains(key, StringComparer.Ordinal) ||
                !_fields.TryGetValue(key, out var field) || !SignedAdapterFormValidator.AcceptsCallbackValue(field, write.Value))
                throw new InvalidDataException("The Adapter returned an invalid or undeclared authorization field.");
        }
    }

    private static bool IsInteractionUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.OriginalString.Length <= 2048;

    private static bool IsErrorCode(string? code) => code is { Length: > 0 and <= 64 } && char.IsAsciiLetter(code[0]) &&
        code.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}
