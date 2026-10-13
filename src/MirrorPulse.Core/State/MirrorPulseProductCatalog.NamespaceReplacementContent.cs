using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>First flushed and reread new content on the admitted original, still role-only object.</summary>
/// <remarks>This is historical copy evidence, not current content, restored protection, remote acceptance or journal acknowledgement.</remarks>
public sealed record MirrorPulseNamespaceReplacementContentCompletion(int Version, Guid OperationId, Guid ActionId,
    MirrorPulseNamespaceImportContent Content, MirrorPulseNamespacePermissionVerification Verification,
    uint LinkCount, bool IsPlaceholder, bool IsInSync, string? PlaceholderIdentity);

public sealed partial class MirrorPulseProductCatalog
{
    public Task<MirrorPulseNamespaceReplacementContentCompletion> RecordNamespaceReplacementContentCompletionAsync(
        MirrorPulseNamespaceReplacementContentCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return RecordNamespaceReplacementFactAsync(completion, completion.OperationId, 4, completion.ActionId, ValidateReplacementContentCompletion,
            execution => RequireReplacementContentCompletionReferences(completion, execution.Intent,
                execution.Action ?? throw new InvalidOperationException("The content receipt requires its original action."),
                execution.WriteStart ?? throw new InvalidOperationException("The content receipt requires its before-write intent."),
                execution.Backup ?? throw new InvalidOperationException("The content receipt requires its original old-content backup.")), cancellationToken);
    }

    private static void RequireReplacementContentCompletionReferences(MirrorPulseNamespaceReplacementContentCompletion completion,
        MirrorPulseNamespaceReplacementIntent intent, MirrorPulseNamespaceReplacementAction action, MirrorPulseNamespaceReplacementWriteStart write,
        MirrorPulseNamespaceReplacementBackup backup)
    {
        if (completion.OperationId != intent.OperationId || completion.ActionId != action.ActionId || completion.ActionId != write.ActionId ||
            completion.Content != intent.Content || completion.Verification.LocalObject != action.LocalObject ||
            completion.Verification.OwnerSid != action.OwnerSid || !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(
                action.RoleOnlyDacl, completion.Verification.Dacl) || completion.Verification.ObservedAt < write.StartedAt ||
            completion.IsPlaceholder && completion.PlaceholderIdentity != intent.OriginalPlaceholderIdentity ||
            completion.IsInSync && backup.Content != intent.Content)
            throw new InvalidOperationException("The new-content receipt requires the admitted bytes, original object, role-only action and identity after its durable write intent.");
    }

    private static void ValidateReplacementContentCompletion(MirrorPulseNamespaceReplacementContentCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion); ArgumentNullException.ThrowIfNull(completion.Content); ArgumentNullException.ThrowIfNull(completion.Verification);
        ValidateReplacementFactIdentity(completion.Version, completion.OperationId, completion.ActionId); ValidateReplacementContent(completion.Content);
        ValidatePermissionBinding(completion.Verification.LocalObject); ValidatePermissionSid(completion.Verification.OwnerSid, role: false);
        ValidatePermissionDacl(completion.Verification.Dacl);
        if (completion.LinkCount != 1 || completion.IsInSync && !completion.IsPlaceholder || completion.Verification.ObservedAt == default ||
            (completion.IsPlaceholder ? completion.PlaceholderIdentity is not { Length: > 0 and <= 5464 } : completion.PlaceholderIdentity is not null) ||
            NamespaceReplacementPayload(completion).Length > 262_144)
            throw new ArgumentException("A replacement copy must retain one dirty original object, its observed identity and bounded content facts.", nameof(completion));
    }

    private static async Task EnsureNamespaceReplacementContentSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_replacement_content_completions (
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_replacements(operation_id),
                action_id TEXT NOT NULL,
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
            );
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
