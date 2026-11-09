using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Observes the real public ACK of accepted current content without inventing a Worker mutation.</summary>
/// <remarks>These bounded diagnostics never authorize replay, source acceptance or another journal ACK.</remarks>
[SupportedOSPlatform("windows10.0.16299")]
internal sealed class MirrorPulseJournalAcceptedObservationAcknowledgement(
    MirrorPulseCfSharpStateSession state, MirrorPulseJournalUploadCompletion completion, LocalRollingLogWriter log)
{
    internal async ValueTask AcknowledgeAsync(MirrorPulseWorkerChangeCommand command, RootRegistration root,
        CloudItemSnapshot snapshot, string? revision, CancellationToken token)
    {
        if (command.IsDirectory || command.Kind is not (MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.ContentUpdate) ||
            command.InstanceId != root.InstanceId || command.RootKey != root.UniquenessKey ||
            !MirrorPulseJournalContentPolicy.IsAcceptedObservation(snapshot, root, revision))
            throw new InvalidDataException("An accepted-content observation requires its actual owned current native state and acknowledged revision.");
        LogField[]? fields = await ReadFieldsAsync(command, root, snapshot, revision!, token).ConfigureAwait(false);
        if (fields is not null) await ObserveAsync("JournalAcceptedContentObservation", fields, token).ConfigureAwait(false);
        await completion.AcknowledgeSuccessfulUploadAsync(command.OperationId, revision, token).ConfigureAwait(false);
        // Only the real public API's successful return emits this event. A missing SDK
        // row, a selected policy branch or the before-event cannot fabricate this result.
        if (fields is not null) await ObserveAsync("JournalAcceptedContentAcknowledged", fields, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<LogField[]?> ReadFieldsAsync(MirrorPulseWorkerChangeCommand command, RootRegistration root,
        CloudItemSnapshot snapshot, string revision, CancellationToken token)
    {
        try
        {
            await using var read = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
            var original = await read.Operations.GetAsync(command.OperationId, token).ConfigureAwait(false);
            await read.RollbackAsync(token).ConfigureAwait(false);
            if (original is null || original.Sequence != command.Sequence || original.Kind.ToString() != command.Kind.ToString()) return null;
            return
            [
                new("correlationId", Guid.NewGuid().ToString("D")), new("operationId", original.OperationId.ToString("D")),
                new("instanceId", root.InstanceId.ToString()), new("rootId", root.RootId.ToString()), new("kind", command.Kind.ToString()),
                new("journalSequence", original.Sequence.ToString(CultureInfo.InvariantCulture)),
                new("journalPayloadSha256", Convert.ToHexString(SHA256.HashData(original.Payload.Span))),
                new("nativeIdentitySha256", Convert.ToHexString(SHA256.HashData(snapshot.PlaceholderIdentity.Span))),
                new("nativeBindingSha256", Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot.LocalBinding)))),
                new("acknowledgedRevisionSha256", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revision))))
            ];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Diagnostics cannot change the existing native policy or the original ACK.
            return null;
        }
    }

    private async Task ObserveAsync(string code, LogField[] fields, CancellationToken token)
    {
        try { await log.WriteAsync(new(LogLevel.Information, "CloudFiles.Upload", code, DateTimeOffset.UtcNow, fields), token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException) { }
    }
}
