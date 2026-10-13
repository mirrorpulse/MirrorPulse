using System.Security.Cryptography;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.Configuration;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseLocalReplacementBackupStoreTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(2_097_409)]
    public async Task OriginalBytesSurviveTargetOverwriteAndStoreRestartWithIndependentNewInput(int length)
    {
        using var fixture = new Fixture();
        byte[] originalBytes = new byte[length]; new Random(71).NextBytes(originalBytes);
        await File.WriteAllBytesAsync(fixture.Target, originalBytes);
        await File.WriteAllBytesAsync(fixture.Input, [8, 9]);
        Guid operation = Guid.NewGuid();
        var inputs = new MirrorPulseLocalImportStagingStore(fixture.Paths);
        var newContent = await inputs.StageAsync(operation, fixture.Input);
        var backups = new MirrorPulseLocalReplacementBackupStore(fixture.Paths);
        await using (var target = fixture.OpenTarget())
        {
            target.Position = length == 0 ? 0 : 1;
            var oldContent = await backups.StageRetainedAsync(operation, target);
            Assert.AreEqual(length == 0 ? 0L : 1L, target.Position);
            Assert.IsTrue(target.CanWrite);
            Assert.AreEqual((long)length, oldContent.Length);
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(originalBytes)), oldContent.Sha256);
            await Assert.ThrowsAsync<IOException>(() => File.WriteAllBytesAsync(fixture.Target, [0]));
            target.SetLength(0); target.Position = 0; await target.WriteAsync(new byte[] { 8, 9 }); await target.FlushAsync(); target.Flush(true);
            backups = new(fixture.Paths);
            await using var backup = await backups.OpenLeaseAsync(operation, oldContent);
            Assert.AreEqual(operation, backup.OperationId); Assert.AreEqual(oldContent, backup.Content);
            Assert.AreEqual(0L, backup.Data.Position); Assert.IsFalse(backup.Data.CanWrite);
            using var received = new MemoryStream(); await backup.Data.CopyToAsync(received);
            CollectionAssert.AreEqual(originalBytes, received.ToArray());
            await Assert.ThrowsAsync<IOException>(() => File.WriteAllBytesAsync(fixture.Backup(operation), [0]));
            Assert.Throws<IOException>(() => backups.RemoveReleased(operation));
            await using var input = await inputs.OpenLeaseAsync(operation, newContent);
            Assert.AreEqual(8, input.Data.ReadByte()); Assert.AreNotEqual(input.Content, backup.Content);
        }
        CollectionAssert.AreEqual(new byte[] { 8, 9 }, await File.ReadAllBytesAsync(fixture.Target));
        backups.RemoveReleased(operation); backups.RemoveReleased(operation);
        await using var retainedInput = await inputs.OpenLeaseAsync(operation, newContent);
        Assert.AreEqual(8, retainedInput.Data.ReadByte());
        Assert.HasCount(0, Directory.GetFiles(fixture.BackupDirectory));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.ProductCatalogDatabasePath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    public async Task RepeatedPreparationCannotReplaceTheFirstBackupWithLaterUnsentBytes()
    {
        using var fixture = new Fixture(); await File.WriteAllBytesAsync(fixture.Target, [1, 2, 3]);
        Guid operation = Guid.NewGuid(); var backups = new MirrorPulseLocalReplacementBackupStore(fixture.Paths);
        await using var target = fixture.OpenTarget(); target.Position = 2;
        var first = await backups.StageRetainedAsync(operation, target);
        Assert.AreEqual(first, await backups.StageRetainedAsync(operation, target));
        target.Position = 0; await target.WriteAsync(new byte[] { 3, 2, 1 }); await target.FlushAsync();
        target.Position = 2;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => backups.StageRetainedAsync(operation, target));
        Assert.AreEqual(2L, target.Position); Assert.IsTrue(target.CanWrite);
        await using var retained = await backups.OpenLeaseAsync(operation, first);
        Assert.AreEqual(1, retained.Data.ReadByte()); Assert.HasCount(1, Directory.GetFiles(fixture.BackupDirectory));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DamagedBackupsCannotBecomeRecoveryInput(bool changeLength)
    {
        using var fixture = new Fixture(); await File.WriteAllBytesAsync(fixture.Target, [1, 2, 3]);
        Guid operation = Guid.NewGuid(); var backups = new MirrorPulseLocalReplacementBackupStore(fixture.Paths);
        await using var target = fixture.OpenTarget(); var first = await backups.StageRetainedAsync(operation, target);
        await File.WriteAllBytesAsync(fixture.Backup(operation), changeLength ? [1] : [3, 2, 1]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => backups.OpenLeaseAsync(operation, first));
        File.Move(fixture.Backup(operation), fixture.Backup(operation) + ".damaged");
    }

    [TestMethod]
    public async Task CancellationAndInvalidIdentityDoNotCloseOrChangeTheRetainedTarget()
    {
        using var fixture = new Fixture(); await File.WriteAllBytesAsync(fixture.Target, [4, 5, 6]);
        var backups = new MirrorPulseLocalReplacementBackupStore(fixture.Paths);
        await using var target = fixture.OpenTarget(); target.Position = 1;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => backups.StageRetainedAsync(Guid.NewGuid(), target, cancelled.Token));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => backups.StageRetainedAsync(Guid.Empty, target));
        await Assert.ThrowsAsync<IOException>(() => backups.OpenLeaseAsync(Guid.NewGuid(), new(0, Convert.ToHexString(SHA256.HashData([])))));
        Assert.AreEqual(1L, target.Position); Assert.AreEqual(5, target.ReadByte()); Assert.IsTrue(target.CanWrite);
        Assert.IsFalse(Directory.Exists(fixture.BackupDirectory));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-replacement-backup-tests", Guid.NewGuid().ToString("N"));
        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            Paths = new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data"));
        }
        internal MirrorPulseStoragePaths Paths { get; }
        internal string Target => Path.Combine(_directory, "target.bin");
        internal string Input => Path.Combine(_directory, "input.bin");
        internal string BackupDirectory => Path.Combine(Paths.DataRootPath, "state", "replacement-backups");
        internal string Backup(Guid operation) => Path.Combine(BackupDirectory, operation.ToString("N") + ".bin");
        internal FileStream OpenTarget() => new(Target, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        public void Dispose()
        {
            string target = Path.GetFullPath(_directory);
            string expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-replacement-backup-tests")) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(expected, StringComparison.OrdinalIgnoreCase) || new DirectoryInfo(target).LinkTarget is not null)
                throw new InvalidOperationException("Replacement backup cleanup requires its own ordinary temporary fixture directory.");
            Directory.Delete(target, recursive: true);
        }
    }
}
