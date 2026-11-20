using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>A borrowed coherent inventory valid only inside the permission coordinator's awaited audit callback.</summary>
/// <remarks>The owner validates the complete catalog fingerprint before and after that callback.
/// This lifetime supplies no native observation, permission-write authority or completion receipt.</remarks>
internal sealed class MirrorPulseNamespaceCurrentTreeAuditAdmission(MirrorPulseNamespaceCurrentTreeSnapshot inventory) : IDisposable
{
    private readonly Dictionary<Guid, MirrorPulseNamespaceMemberProtectionSnapshot> _members =
        inventory.Members.ToDictionary(member => member.Context.Member.MemberId);
    private int _disposed;

    internal MirrorPulseNamespaceCurrentTreeSnapshot Inventory
    {
        get { RequireActive(); return inventory; }
    }

    internal void RequireSnapshot(MirrorPulseNamespaceMemberProtectionSnapshot snapshot)
    {
        RequireActive(); ArgumentNullException.ThrowIfNull(snapshot);
        if (!_members.TryGetValue(snapshot.Context.Member.MemberId, out var retained) || retained != snapshot)
            throw new InvalidDataException("The member is outside this coherent current-tree audit admission.");
    }

    internal void RequireActive() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        GC.SuppressFinalize(this);
    }
}
