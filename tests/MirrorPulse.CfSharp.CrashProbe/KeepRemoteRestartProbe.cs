using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CfSharp.CrashProbe;

public sealed record KeepRemoteRestartEvidence(int ProcessId, string Architecture, Guid CommandId, Guid InvocationId,
    Guid StoreFileId, Guid LocalFileId, int OriginalCount, string SelectedSha256, string PreservedSha256);

[SupportedOSPlatform("windows10.0.26100")]
internal static class KeepRemoteRestartProbe
{
    internal static async Task<int> RunAsync(string directory, Guid commandId)
    {
        string fixture = Path.GetFullPath(directory);
        string fixtureParent = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests");
        string leaf = Path.GetRelativePath(fixtureParent, fixture);
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1" ||
            !Guid.TryParseExact(leaf, "N", out _) || !File.Exists(Path.Combine(fixture, ".mp-permission-fixture")) ||
            File.GetAttributes(fixture).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("A disposable native KeepRemote fixture is required.");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var paths = new MirrorPulseStoragePaths(Path.Combine(fixture, "sync"), Path.Combine(fixture, "data"));
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, token);
        var pending = await catalog.ReadConflictKeepRemoteAsync(commandId, token)
            ?? throw new InvalidDataException("The original decision is missing.");
        var first = await catalog.ReadConflictKeepRemoteFileStartAsync(commandId, token)
            ?? throw new InvalidDataException("The original file start is missing.");
        var action = await catalog.ReadConflictKeepRemoteFileActionAsync(commandId, token)
            ?? throw new InvalidDataException("The original permission action is missing.");
        var root = (await catalog.ReadAdapterTopologyAsync(token)).Roots.Single();
        var selected = pending.RemoteCopy ?? throw new InvalidDataException("The selected copy is missing.");
        var preserved = pending.LocalPreservation?.Copy ?? throw new InvalidDataException("The preserved original copy is missing.");
        if (first.Original.Context.Member.RootId != root.RootId ||
            (await catalog.ReadUserCommandAsync(commandId, token))?.State != "pending" ||
            (await catalog.ReadConflictKeepRemoteFileContentAsync(commandId, token))?.WriteStart is not null ||
            await catalog.ReadConflictKeepRemoteAcknowledgementAsync(commandId, token) is not null)
            throw new InvalidDataException("The independent owner must start at the original before-write cut.");

        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        var worker = new NoWorkerAccess();
        await using var execution = new MirrorPulseNamespaceExecutionSession();
        await using var scheduler = new MirrorPulseInstanceScheduler();
        await using var actor = new MirrorPulseNamespaceActor();
        await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
        var factory = new CfSharpMirrorPulseCloudRuntimeFactory(uploads: worker, stats: worker, router: router,
            catalog: catalog, mayDispatch: _ => false, scheduler: scheduler, namespaceExecution: execution,
            namespaceActor: actor, namespaceWork: work, initializeNamespaceOnStart: true);
        string localPath = Path.GetFullPath(first.Original.Context.Location.RelativePath, paths.SyncRootPath);
        var route = router.ResolveCurrentPath(localPath);
        if (route.InstanceId != root.InstanceId || route.RootKey != root.UniquenessKey)
            throw new InvalidDataException("The original file is outside its retained route.");
        await using (var runtime = factory.Create(paths))
        {
            await runtime.StartAsync(token);
            var startup = runtime.NamespaceStartupResults.Single();
            if (startup.RootId != root.RootId || !startup.Admitted || !work.IsAdmitted(root.RootId))
            {
                Console.Error.WriteLine($"KeepRemote startup: admitted={startup.Admitted}; recovery={startup.RecoveryCode}; native={startup.NativeErrorCode}.");
                throw new InvalidDataException("Startup did not settle the original command and freshly admit its root.");
            }
            var replay = await runtime.ApplyUploadConflictAsync(pending.Intent.ConflictId, MirrorPulseConflictAction.KeepRemote, token);
            if (replay.ResolutionId != commandId || replay.PreservedPath is null ||
                await ReadHashAsync(replay.PreservedPath, token) != preserved.Sha256 ||
                await ReadHashAsync(localPath, token) != selected.Sha256)
                throw new InvalidDataException("Startup did not retain both verified byte sources and the original decision.");
        }

        var role = (await catalog.ReadConflictKeepRemoteRoleAttemptsAsync(commandId, token)).Single();
        var applied = (await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(commandId, token))
            .Single(attempt => attempt.Result?.Receipt.MayAcknowledge == true).Result!;
        var acknowledgement = await catalog.ReadConflictKeepRemoteAcknowledgementAsync(commandId, token)
            ?? throw new InvalidDataException("The independent owner did not acknowledge the original claim.");
        if (role.Result is null || role.Start.FirstActionId != action.ActionId || role.Start.NextAction.RoleSid != execution.RoleSid.Value ||
            action.RoleSid == execution.RoleSid.Value || applied.Receipt.ObservedInSync != true ||
            applied.Child.LocalObject != first.Original.Context.Member.LocalObject ||
            acknowledgement.Result is not { } confirmed || !confirmed.VerifiedMissingOriginals.SequenceEqual(pending.Intent.OriginalOperationIds) ||
            acknowledgement.Start.StoreFileId != first.StoreFileId || acknowledgement.Start.StoreVolumeSerialNumber != first.StoreVolumeSerialNumber ||
            (await catalog.ReadUserCommandAsync(commandId, token))?.State != "resolved" ||
            (await catalog.ReadNamespaceDispatchFencedRootsAsync(token)).Count != 0 || worker.Calls != 0)
            throw new InvalidDataException("The independent owner did not retain the original native, store and settlement proofs.");
        await using var store = await MirrorPulseCfSharpStateStoreFactory.Create(paths).OpenAsync(new CloudStateStoreContext(paths.SyncRootPath), token);
        await using var official = await store.BeginTransactionAsync(token);
        var projected = await official.Items.GetByItemIdAsync(pending.Intent.ItemId, token);
        var atPath = projected is null ? null : await official.Items.GetByRelativePathAsync(projected.RelativePath, token);
        bool sameLocation = projected is not null && string.Equals(
            Path.GetFullPath(projected.RelativePath, paths.SyncRootPath), localPath, StringComparison.OrdinalIgnoreCase);
        if (projected is not { IsTombstone: false } || projected.RemoteRevision != pending.Intent.RemoteSource.RemoteRevision ||
            projected.RemoteId != pending.Intent.RemoteSource.RemoteId || atPath?.ItemId != projected.ItemId || !sameLocation)
        {
            Console.Error.WriteLine($"SDK projection: live={projected is { IsTombstone: false }}; revision={projected?.RemoteRevision == pending.Intent.RemoteSource.RemoteRevision}; " +
                $"remote={projected?.RemoteId == pending.Intent.RemoteSource.RemoteId}; path={sameLocation && atPath?.ItemId == pending.Intent.ItemId}.");
            throw new InvalidDataException("The independently reopened SDK store lost the original selected projection.");
        }
        foreach (Guid id in pending.Intent.OriginalOperationIds)
            if (await official.Operations.GetAsync(id, token) is not null)
                throw new InvalidDataException("An original journal row survived the independent owner.");
        await official.RollbackAsync(token);

        Console.WriteLine(JsonSerializer.Serialize(new KeepRemoteRestartEvidence(Environment.ProcessId,
            RuntimeInformation.ProcessArchitecture.ToString(), commandId, confirmed.InvocationId, first.StoreFileId,
            applied.Child.LocalObject.LocalFileId, confirmed.VerifiedMissingOriginals.Count, selected.Sha256, preserved.Sha256)));
        return 0;
    }

    private static async Task<string> ReadHashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    private sealed class NoWorkerAccess : IMirrorPulseWorkerUploadTransport, IMirrorPulseWorkerStatTransport,
        IMirrorPulseWorkerRangeTransport, IMirrorPulseWorkerDirectoryPageSource
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken cancellationToken) => Refuse<string>();
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken cancellationToken) => Refuse<string?>();
        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken cancellationToken) => Refuse<Stream>();
        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request,
            CancellationToken cancellationToken) => Refuse<MirrorPulseWorkerDirectoryPage>();

        private ValueTask<T> Refuse<T>()
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromException<T>(new InvalidOperationException("The independent recovery has no Adapter source."));
        }
    }
}
