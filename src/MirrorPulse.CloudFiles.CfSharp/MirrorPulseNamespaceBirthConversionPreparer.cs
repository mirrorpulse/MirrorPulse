using System.Runtime.Versioning;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Retains the actual ordinary birth before requesting public CfSharp conversion.</summary>
/// <remarks>
/// The Host owns the root and parent admission, lifetime guard and creator execution context.
/// This boundary performs no conversion, permission write, source access or journal synthesis.
/// Its historical preparation proves neither current protection nor permission to replay creation.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public static class MirrorPulseNamespaceBirthConversionPreparer
{
    public static async Task<MirrorPulseNamespaceBirthConversionPreparation> RecordAsync(MirrorPulseProductCatalog catalog,
        Guid operationId, CloudItem item, MirrorPulseRootRouter router, string expectedConvertedDacl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(expectedConvertedDacl);
        var birth = await catalog.ReadNamespaceBirthAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original birth admission is missing.");
        _ = await catalog.ReadNamespaceBirthPlanAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original birth identity plan is missing.");
        _ = await catalog.ReadNamespaceBirthStartAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original birth start is missing.");
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(caller).IsInRole(new SecurityIdentifier(birth.RoleSid)))
            throw new UnauthorizedAccessException("The original namespace creator role is required for first conversion preparation.");
        var snapshot = await item.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Exists || snapshot.IsPlaceholder || snapshot.IsTombstone || snapshot.LocalBinding is not { } binding ||
            (snapshot.Kind == CloudItemKind.Directory) != birth.IsDirectory || item.RelativePath.Replace('\\', '/') != birth.RelativePath)
            throw new InvalidDataException("Conversion preparation requires the admitted ordinary object before placeholder conversion.");
        await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(item, router, cancellationToken).ConfigureAwait(false);
        var facts = await lease.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (facts.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
            facts.RootId != birth.RootId || facts.RelativePath != birth.RelativePath || facts.IsDirectory != birth.IsDirectory || facts.LinkCount != 1)
            throw new InvalidDataException("The retained ordinary object does not belong to the original birth admission.");
        var preparation = new MirrorPulseNamespaceBirthConversionPreparation(1, operationId, facts.LocalObject,
            facts.IsDirectory, facts.OwnerSid, facts.Dacl, expectedConvertedDacl, facts.LinkCount.Value, facts.ObservedAt);
        var retained = await catalog.ReadNamespaceBirthConversionAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (retained is not null)
        {
            if (retained.LocalObject != preparation.LocalObject || retained.IsDirectory != preparation.IsDirectory ||
                retained.OwnerSid != preparation.OwnerSid || retained.BirthDacl != preparation.BirthDacl ||
                retained.ExpectedConvertedDacl != preparation.ExpectedConvertedDacl)
                throw new InvalidDataException("A later object or descriptor cannot replace the original ordinary birth preparation.");
            return retained;
        }
        return await catalog.PrepareNamespaceBirthConversionAsync(preparation, cancellationToken).ConfigureAwait(false);
    }
}
