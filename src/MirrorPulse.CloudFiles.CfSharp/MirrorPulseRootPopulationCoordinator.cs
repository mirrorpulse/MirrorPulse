using System.Runtime.Versioning;
using CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Creates Adapter entry directories with CfSharp's identity-verified creation coordination.</summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseRootPopulationCoordinator
{
    private readonly CloudFileSystem _fileSystem;
    private readonly CloudLocalChangeFeed _feed;

    public MirrorPulseRootPopulationCoordinator(CloudFileSystem fileSystem, CloudLocalChangeFeed feed)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
    }

    public async ValueTask<CloudPlaceholderBatchResult> PopulateAsync(
        MirrorPulseRootRouter router,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(router);
        if (!_feed.IsStarted)
        {
            throw new InvalidOperationException("The CfSharp local change feed must start before root population.");
        }

        CloudProviderDirectoryPage page = router.CreateRootPage();
        return await _fileSystem.Root.CreatePlaceholdersAsync(page.Children, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
