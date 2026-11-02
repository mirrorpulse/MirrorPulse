using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.Configuration;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

// Ordinary private files and the real official SQLite provider only; no registered Cloud Files root or Host.
[SupportedOSPlatform("windows10.0.16299")]
[TestClass]
public sealed partial class MirrorPulseCfSharpStoreFileLeaseTests
{
    [TestMethod]
    public async Task OriginalFileBindingSurvivesOfficialWritesAndOwnerReopenWithoutAcknowledgingRows()
    {
        await using var fixture = await Fixture.OpenAsync(); string originalRows = await fixture.RowsAsync();
        MirrorPulseCfSharpStoreFileBinding original;
        using (var lease = await fixture.Session.OpenStoreFileLeaseAsync())
        {
            original = JsonSerializer.Deserialize<MirrorPulseCfSharpStoreFileBinding>(JsonSerializer.Serialize(lease.Binding));
            Assert.AreNotEqual(Guid.Empty, original.FileId);
            await using (var transaction = await fixture.Store.BeginTransactionAsync())
            {
                await transaction.Checkpoints.UpsertAsync(new("product/test", [9], DateTimeOffset.UtcNow));
                await transaction.CommitAsync();
            }
            await lease.RequireCurrentAsync(original); Assert.AreEqual(originalRows, await fixture.RowsAsync());
        }
        await fixture.CloseAsync(); await fixture.ReopenAsync();
        using var reopened = await fixture.Session.OpenStoreFileLeaseAsync();
        await reopened.RequireCurrentAsync(original); Assert.AreEqual(originalRows, await fixture.RowsAsync());
    }

    [TestMethod]
    public async Task ByteIdenticalReplacementWithEveryOriginalRowCannotReuseOriginalDatabaseBinding()
    {
        await using var fixture = await Fixture.OpenAsync(); string rows = await fixture.RowsAsync();
        MirrorPulseCfSharpStoreFileBinding original;
        using (var first = await fixture.Session.OpenStoreFileLeaseAsync()) original = first.Binding;
        await fixture.CloseAsync();
        string retired = fixture.Paths.CfSharpStateDatabasePath + ".retained-original";
        File.Move(fixture.Paths.CfSharpStateDatabasePath, retired);
        File.Copy(retired, fixture.Paths.CfSharpStateDatabasePath);
        CollectionAssert.AreEqual(SHA256.HashData(await File.ReadAllBytesAsync(retired)),
            SHA256.HashData(await File.ReadAllBytesAsync(fixture.Paths.CfSharpStateDatabasePath)));
        await fixture.ReopenAsync(); Assert.AreEqual(rows, await fixture.RowsAsync());
        using var replaced = await fixture.Session.OpenStoreFileLeaseAsync();
        Assert.AreNotEqual(original, replaced.Binding);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await replaced.RequireCurrentAsync(original));
        Assert.AreEqual(rows, await fixture.RowsAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExistingOrNewlyObservedMainFileAliasesRefuseWithoutChangingOriginalRows(bool afterOpen)
    {
        await using var fixture = await Fixture.OpenAsync(); string rows = await fixture.RowsAsync();
        using var lease = afterOpen ? await fixture.Session.OpenStoreFileLeaseAsync() : null;
        string alias = fixture.Paths.CfSharpStateDatabasePath + ".alias";
        Assert.IsTrue(CreateHardLink(alias, fixture.Paths.CfSharpStateDatabasePath, 0));
        try
        {
            if (lease is not null)
                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await lease.RequireCurrentAsync(lease.Binding));
            else
                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => { using var refused = await fixture.Session.OpenStoreFileLeaseAsync(); });
            Assert.AreEqual(rows, await fixture.RowsAsync());
        }
        finally { File.Delete(alias); }
        using var retry = await fixture.Session.OpenStoreFileLeaseAsync(); await retry.RequireCurrentAsync(retry.Binding);
    }

    [TestMethod]
    public async Task RetainedFileHandleBlocksReplacementAfterSdkOwnerClosesButDoesNotImpersonateALiveStore()
    {
        await using var fixture = await Fixture.OpenAsync();
        using var lease = await fixture.Session.OpenStoreFileLeaseAsync(); await fixture.CloseAsync();
        string moved = fixture.Paths.CfSharpStateDatabasePath + ".moved";
        Assert.ThrowsExactly<IOException>(() => File.Move(fixture.Paths.CfSharpStateDatabasePath, moved));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await lease.RequireCurrentAsync(lease.Binding));
        lease.Dispose(); File.Move(fixture.Paths.CfSharpStateDatabasePath, moved);
        File.Move(moved, fixture.Paths.CfSharpStateDatabasePath); await fixture.ReopenAsync();
        using var reopened = await fixture.Session.OpenStoreFileLeaseAsync(); await reopened.RequireCurrentAsync(lease.Binding);
    }

    [TestMethod]
    public async Task DisposedOfficialOwnerCannotOpenANewBindingFromTheSameRemainingPath()
    {
        await using var fixture = await Fixture.OpenAsync(); var oldSession = fixture.Session;
        string rows = await fixture.RowsAsync(); await fixture.CloseAsync();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => { using var refused = await oldSession.OpenStoreFileLeaseAsync(); });
        await fixture.ReopenAsync(); Assert.AreEqual(rows, await fixture.RowsAsync());
    }

    [TestMethod]
    public async Task CancellationWhileWaitingForTheRealOfficialTransactionDoesNotAcquireOrChangeOwnership()
    {
        await using var fixture = await Fixture.OpenAsync(); string rows = await fixture.RowsAsync();
        await using (var held = await fixture.Store.BeginTransactionAsync())
        {
            using var stop = new CancellationTokenSource();
            var waiting = fixture.Session.OpenStoreFileLeaseAsync(stop.Token).AsTask(); Assert.IsFalse(waiting.IsCompleted);
            stop.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(async () => { using var refused = await waiting; });
            await held.RollbackAsync();
        }
        using var retry = await fixture.Session.OpenStoreFileLeaseAsync();
        await retry.RequireCurrentAsync(retry.Binding); Assert.AreEqual(rows, await fixture.RowsAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AForeignPhysicalVolumeOrFileBindingCannotReuseTheSameLiveDatabase(bool changeVolume)
    {
        await using var fixture = await Fixture.OpenAsync(); string rows = await fixture.RowsAsync();
        using var lease = await fixture.Session.OpenStoreFileLeaseAsync();
        var foreign = changeVolume ? lease.Binding with { VolumeSerialNumber = lease.Binding.VolumeSerialNumber ^ 1 } : lease.Binding with { FileId = Guid.NewGuid() };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await lease.RequireCurrentAsync(foreign));
        await lease.RequireCurrentAsync(lease.Binding); Assert.AreEqual(rows, await fixture.RowsAsync());
    }

    [TestMethod]
    public async Task DisposingTheAdditionalFileLeaseDoesNotDisposeTheOfficialStoreOrAcknowledgeItsJournal()
    {
        await using var fixture = await Fixture.OpenAsync(); string rows = await fixture.RowsAsync();
        var lease = await fixture.Session.OpenStoreFileLeaseAsync(); var binding = lease.Binding; lease.Dispose(); lease.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await lease.RequireCurrentAsync(binding));
        Assert.AreEqual(rows, await fixture.RowsAsync()); using var retry = await fixture.Session.OpenStoreFileLeaseAsync();
        await retry.RequireCurrentAsync(binding);
    }

    [TestMethod]
    public async Task AnUnopenedSessionCannotTreatAnExistingPrivatePathAsOfficialOwnership()
    {
        await using var fixture = await Fixture.OpenAsync(); string rows = await fixture.RowsAsync();
        var unopened = new MirrorPulseCfSharpStateSession(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => { using var refused = await unopened.OpenStoreFileLeaseAsync(); });
        Assert.AreEqual(rows, await fixture.RowsAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "MirrorPulse-store-file-binding", Guid.NewGuid().ToString("N"));
        private ICloudStateStore? _store;
        internal MirrorPulseStoragePaths Paths => new(Path.Combine(_root, "sync"), Path.Combine(_root, "data"));
        internal MirrorPulseCfSharpStateSession Session { get; private set; } = null!;
        internal ICloudStateStore Store => _store ?? throw new InvalidOperationException("The fixture store is closed.");
        internal Guid Item { get; } = Guid.NewGuid();

        internal static async Task<Fixture> OpenAsync()
        {
            var fixture = new Fixture();
            try
            {
                Directory.CreateDirectory(fixture.Paths.SyncRootPath); await fixture.ReopenAsync();
                await using var transaction = await fixture.Store.BeginTransactionAsync();
                await transaction.Items.UpsertAsync(new(fixture.Item, "fixture-original", "original.bin",
                    CloudItemKind.File, "original-revision", null, false, DateTimeOffset.UtcNow));
                for (int index = 0; index < 2; index++)
                    await transaction.Operations.EnqueueAsync(new(Guid.NewGuid(), CloudStateOperationKind.MetadataUpdate, fixture.Item,
                        new byte[] { (byte)(index + 1) }, DateTimeOffset.UtcNow.AddTicks(index), index, null, 0));
                await transaction.CommitAsync(); return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        internal async Task ReopenAsync()
        {
            Assert.IsNull(_store); Session = new(Paths);
            _store = await Session.OpenAsync(new(Paths.SyncRootPath));
        }

        internal async Task CloseAsync()
        {
            if (_store is null) return;
            await _store.DisposeAsync(); _store = null;
        }

        internal async Task<string> RowsAsync()
        {
            await using var transaction = await Store.BeginTransactionAsync();
            var rows = await transaction.Operations.ListByItemIdAsync(Item, 3);
            string serialized = JsonSerializer.Serialize(rows.Select(row => new
            {
                row.OperationId,
                row.ItemId,
                row.Sequence,
                row.Kind,
                Payload = Convert.ToHexString(row.Payload.Span),
                row.AttemptCount,
                row.CreatedAt,
                row.RetryAfter
            }));
            Assert.HasCount(2, rows); await transaction.RollbackAsync(); return serialized;
        }

        public async ValueTask DisposeAsync()
        {
            await CloseAsync(); string root = Path.GetFullPath(_root);
            string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-store-file-binding")) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup escaped its private directory.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string newName, string existingName, nint attributes);
}
