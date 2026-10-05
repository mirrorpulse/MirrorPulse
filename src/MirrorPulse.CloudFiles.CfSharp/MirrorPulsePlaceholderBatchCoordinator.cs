using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed record MirrorPulsePlaceholderBatchPlan(
    IReadOnlyList<CloudPlaceholderSpec> Placeholders,
    IReadOnlyList<CloudRemoteDirectoryEntry> IgnoredEntries);

/// <summary>
/// Converts one remote directory page into stable Cloud Files placeholder specifications.
/// </summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulsePlaceholderBatchCoordinator
{
    private readonly Func<
        IReadOnlyList<CloudPlaceholderSpec>,
        CloudPlaceholderBatchOptions?,
        CancellationToken,
        ValueTask<CloudPlaceholderBatchResult>> _create;

    public MirrorPulsePlaceholderBatchCoordinator(
        Func<
            IReadOnlyList<CloudPlaceholderSpec>,
            CloudPlaceholderBatchOptions?,
            CancellationToken,
            ValueTask<CloudPlaceholderBatchResult>> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        _create = create;
    }

    public static MirrorPulsePlaceholderBatchCoordinator For(CloudDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return new((placeholders, options, cancellationToken) =>
            directory.CreatePlaceholdersAsync(placeholders, options, cancellationToken));
    }

    public static MirrorPulsePlaceholderBatchPlan Plan(
        InstanceId instanceId,
        IReadOnlyList<CloudRemoteDirectoryEntry> entries,
        RootRegistration? root = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var placeholders = new List<CloudPlaceholderSpec>(entries.Count);
        var ignored = new List<CloudRemoteDirectoryEntry>();
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.IsDeleted)
            {
                ignored.Add(entry);
                continue;
            }

            if (root is not null && root.InstanceId != instanceId) throw new InvalidDataException("RootInstanceMismatch");
            var identity = root is null ? MirrorPulsePlaceholderIdentity.Create(instanceId, entry.RemoteId, entry.RemoteRevision) :
                MirrorPulsePlaceholderIdentity.CreateForRoot(root, entry.RemoteId, entry.RemoteRevision);
            placeholders.Add(entry.ItemKind == CloudItemKind.Directory
                ? BuildDirectory(entry, identity.ToCfSharp())
                : BuildFile(entry, identity.ToCfSharp()));
        }

        return new(placeholders.AsReadOnly(), ignored.AsReadOnly());
    }

    public async ValueTask<CloudPlaceholderBatchResult> CreateAsync(
        InstanceId instanceId,
        IReadOnlyList<CloudRemoteDirectoryEntry> entries,
        CloudPlaceholderBatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var plan = Plan(instanceId, entries);
        return await _create(plan.Placeholders, options, cancellationToken).ConfigureAwait(false);
    }

    private static CloudFilePlaceholderSpec BuildFile(
        CloudRemoteDirectoryEntry entry,
        CloudPlaceholderIdentity identity)
    {
        var builder = CloudFilePlaceholderSpec.CreateBuilder(entry.Name, identity, entry.Length ?? 0)
            .WithMetadata(entry.Metadata ?? CloudPlaceholderMetadata.CreateFileBuilder().Build())
            .WithRemoteRevision(entry.RemoteRevision)
            .WithInSyncState(true)
            .WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly);
        return builder.Build();
    }

    private static CloudDirectoryPlaceholderSpec BuildDirectory(
        CloudRemoteDirectoryEntry entry,
        CloudPlaceholderIdentity identity)
    {
        var builder = CloudDirectoryPlaceholderSpec.CreateBuilder(entry.Name, identity)
            .WithMetadata(entry.Metadata ?? CloudPlaceholderMetadata.CreateDirectoryBuilder().Build())
            .WithRemoteRevision(entry.RemoteRevision)
            .WithInSyncState(true)
            .WithPopulationState(CloudDirectoryPopulationState.Partial);
        return builder.Build();
    }
}
