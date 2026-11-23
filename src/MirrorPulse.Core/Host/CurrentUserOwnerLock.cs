using System.Security.Principal;

namespace MirrorPulse.Core.Host;

/// <summary>
/// Cross-process owner lock scoped to the current Windows user's SID.
/// </summary>
public interface ICurrentUserOwnerLock
{
    bool IsHeld { get; }

    bool TryAcquire(TimeSpan timeout);

    void Release();
}

public sealed class CurrentUserOwnerLock : IDisposable, ICurrentUserOwnerLock
{
    private FileStream? _lockFile;

    private CurrentUserOwnerLock(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public bool IsHeld => _lockFile is not null;

    public static CurrentUserOwnerLock Create(string scope = "host")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("The current Windows identity has no security identifier.");
        var safeScope = string.Concat(scope.Trim().Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductInfo.Edition.IdentityPrefix, "locks");
        Directory.CreateDirectory(root);
        var name = Path.Combine(root, $"owner-{sid}-{safeScope}.lock");
        return new CurrentUserOwnerLock(name);
    }

    public bool TryAcquire(TimeSpan timeout)
    {
        if (_lockFile is not null)
        {
            return true;
        }

        var milliseconds = ToTimeoutMilliseconds(timeout);
        var deadline = milliseconds == Timeout.Infinite ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddMilliseconds(milliseconds);
        do
        {
            try
            {
                _lockFile = new FileStream(Name, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                return true;
            }
            catch (IOException) when (milliseconds != Timeout.Infinite && DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }
            catch (IOException)
            {
                Thread.Sleep(25);
            }
        }
        while (milliseconds == Timeout.Infinite || DateTimeOffset.UtcNow < deadline);

        return false;
    }

    public void Release()
    {
        if (_lockFile is not null)
        {
            _lockFile.Dispose();
            _lockFile = null;
        }
    }

    public void Dispose()
    {
        Release();
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return Timeout.Infinite;
        }

        if (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return (int)Math.Ceiling(timeout.TotalMilliseconds);
    }
}
