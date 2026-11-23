using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Host;

namespace MirrorPulse.App;

internal static class AdapterHealthText
{
    public static string Describe(MirrorPulseAdapterHealthIssue issue)
    {
        string reason = issue.Reason switch
        {
            AdapterHealthReason.MissingConfiguration => "A required setting is missing.",
            AdapterHealthReason.InvalidConfiguration => "A setting needs to be corrected.",
            AdapterHealthReason.MissingCredential => "A credential is required.",
            AdapterHealthReason.ExpiredCredential => "A credential expired. Replace it or authorize again.",
            AdapterHealthReason.SourceUnavailable => "The source is unavailable.",
            _ => "The Adapter reported a Worker failure.",
        };
        string? field = issue.FieldLabel ?? issue.FieldKey;
        return field is null ? reason : $"{field} · {reason}";
    }
}
