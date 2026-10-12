using System.Runtime.Versioning;
using System.Security.AccessControl;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Creates an empty local member under a currently protected registered directory.</summary>
/// <remarks>
/// The caller drains earlier instance work and retains root and ancestor guards. Source work
/// stays outside this boundary. Birth, identity, start and first binding are retained separately;
/// a started creation without an original binding is never repeated or adopted by its path.
/// Successful replay returns immutable membership and grants no current filesystem authority.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceCreationCoordinator(CloudFileSystem fileSystem, MirrorPulseCfSharpStateSession state,
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution,
    MirrorPulseNamespaceActor actor)
{
    public ValueTask<MirrorPulseNamespaceMemberRegistration> CreateAsync(Guid operationId, Guid parentMemberId,
        string name, bool isDirectory, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || parentMemberId == Guid.Empty)
            throw new ArgumentException("Operation and parent member IDs are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("Creation requires one direct child name.", nameof(name));
        return actor.RunAsync(token => CreateAdmittedAsync(operationId, parentMemberId, name, isDirectory, token), cancellationToken);
    }

    private async ValueTask<MirrorPulseNamespaceMemberRegistration> CreateAdmittedAsync(Guid operationId, Guid parentMemberId,
        string name, bool isDirectory, CancellationToken token)
    {
        actor.AssertAccess();
        var parentMember = await catalog.ReadNamespaceMemberAsync(parentMemberId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The creation parent is not registered.");
        if (!parentMember.IsDirectory || parentMember.RootId is null)
            throw new InvalidOperationException("Creation requires a directory inside an Adapter's managed root.");
        var location = await catalog.ReadNamespaceMemberLocationAsync(parentMemberId, token).ConfigureAwait(false);
        CloudDirectory parent = fileSystem.GetDirectory(location.RelativePath);
        // CfSharp performs public child-name validation before any durable product work.
        CloudItem item = isDirectory ? parent.GetDirectory(name) : parent.GetFile(name);
        string path = item.RelativePath.Replace('\\', '/');
        var retainedBirth = await catalog.ReadNamespaceBirthAsync(operationId, token).ConfigureAwait(false);
        if (retainedBirth is not null && (retainedBirth.RelativePath != path || retainedBirth.IsDirectory != isDirectory ||
            retainedBirth.RootId != parentMember.RootId || retainedBirth.Origin != MirrorPulseNamespaceBirthOrigin.ControlledCreation ||
            (retainedBirth.BornParent?.BirthOperationId ?? retainedBirth.ParentEvidenceId) != parentMember.OriginId))
            throw new InvalidOperationException("The operation ID already identifies a different creation.");
        var replay = await catalog.ReadNamespaceMemberAsync(operationId, token).ConfigureAwait(false);
        if (replay is not null)
        {
            if (retainedBirth is null || replay.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth ||
                replay.OriginId != operationId || replay.ParentMemberId != parentMemberId)
                throw new InvalidDataException("The creation replay lost its original member provenance.");
            return replay;
        }
        var proof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, token).ConfigureAwait(false);
        if (proof.RoleSid != execution.RoleSid.Value || proof.OwnerSid != execution.OwnerSid.Value ||
            proof.RelativePath != location.RelativePath)
            throw new InvalidOperationException("Creation requires the current parent role and its proved location.");
        if ((await catalog.ReadNamespaceRecoveryRootsAsync(token).ConfigureAwait(false)).Contains(parentMember.RootId.Value))
            throw new InvalidOperationException("The creation root has pending namespace recovery.");
        MirrorPulseNamespaceMemberRegistration result = null!;
        await execution.RunNamespaceOperationAsync(async () =>
        {
            await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, router, token).ConfigureAwait(false);
            var birth = retainedBirth ?? new MirrorPulseNamespaceBirthIntent(proof.BornParent is null ? 1 : 2, operationId,
                parentMember.RootId.Value, proof.BornParent is null ? parentMember.OriginId : Guid.Empty, proof.PermissionOperationId,
                parentMember.LocalObject, path, isDirectory, MirrorPulseNamespaceBirthOrigin.ControlledCreation,
                execution.RoleSid.Value, DateTimeOffset.UtcNow)
            { BornParent = proof.BornParent };
            var before = await MirrorPulseNamespaceBirthProtectionVerifier.InspectRetainedParentAsync(
                birth, parent, proof, parentGuard, token).ConfigureAwait(false);
            if (retainedBirth is null && (await item.InspectAsync(token).ConfigureAwait(false)).Exists)
                throw new IOException("The creation destination is occupied.");
            if (retainedBirth is null)
                birth = await catalog.PrepareNamespaceBirthAsync(birth, token).ConfigureAwait(false);
            var plan = await catalog.ReadNamespaceBirthPlanAsync(operationId, token).ConfigureAwait(false);
            if (plan is null)
            {
                var route = router.ResolvePath(path);
                var identity = router.CreateFileIdentity(route.InstanceId, route.RootKey, "local:" + operationId.ToString("N"));
                plan = await catalog.PrepareNamespaceBirthPlanAsync(new(1, operationId, identity.ItemId, identity.RemoteId,
                    null, DateTimeOffset.UtcNow), token).ConfigureAwait(false);
            }
            _ = await MirrorPulseNamespaceBirthIdentityProjection.PrepareAsync(catalog, state.OpenStore, operationId, item, token).ConfigureAwait(false);
            var start = await catalog.ReadNamespaceBirthStartAsync(operationId, token).ConfigureAwait(false);
            var conversion = await catalog.ReadNamespaceBirthConversionAsync(operationId, token).ConfigureAwait(false);
            if (start is null)
            {
                if ((await item.InspectAsync(token).ConfigureAwait(false)).Exists)
                    throw new IOException("The creation destination is occupied.");
                if (await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, token).ConfigureAwait(false) != proof)
                    throw new InvalidOperationException("Parent protection changed before creation.");
                MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await parentGuard.InspectAsync(token).ConfigureAwait(false));
                token.ThrowIfCancellationRequested();
                _ = await catalog.RecordNamespaceBirthStartAsync(new(1, operationId, DateTimeOffset.UtcNow), token).ConfigureAwait(false);
                // From the first native creation, drain local persistence despite client cancellation.
                string expectedDacl = MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl(before.Dacl, isDirectory);
                CreateOrdinaryItem(item, isDirectory, expectedDacl);
                conversion = await MirrorPulseNamespaceBirthConversionPreparer.RecordAsync(catalog, operationId, item, router,
                    expectedDacl, CancellationToken.None).ConfigureAwait(false);
            }
            else if (conversion is null)
                throw new InvalidOperationException("A started creation without its first native binding requires explicit recovery; it cannot be repeated.");

            CancellationToken durable = CancellationToken.None;
            if (await catalog.ReadNamespaceBirthObservationAsync(operationId, durable).ConfigureAwait(false) is null)
            {
                if (isDirectory)
                    await ConvertDirectoryAsync(item, conversion, plan, durable).ConfigureAwait(false);
                else
                {
                    var recovery = new MirrorPulseNamespaceBirthRecoveryCoordinator(catalog, router, execution);
                    var action = await catalog.ReadLatestNamespaceBirthRecoveryActionAsync(operationId, durable).ConfigureAwait(false);
                    action = await recovery.PrepareStagedFileAsync(action?.RecoveryId ?? Guid.NewGuid(), operationId,
                        (CloudFile)item, parent, durable).ConfigureAwait(false);
                    var recovered = await recovery.ExecuteStagedFileAsync(action.RecoveryId, (CloudFile)item, parent, durable).ConfigureAwait(false);
                    if (recovered.Verification is null)
                        throw new IOException("The created file requires protected conversion recovery.");
                }
                _ = await MirrorPulseNamespaceBirthObserver.RecordAsync(catalog, operationId, item, router, durable).ConfigureAwait(false);
            }
            var observed = await catalog.ReadNamespaceBirthObservationAsync(operationId, durable).ConfigureAwait(false)
                ?? throw new InvalidDataException("The created object has no retained birth observation.");
            var protection = await catalog.ReadLatestNamespaceBirthProtectionAsync(operationId, durable).ConfigureAwait(false);
            protection = await MirrorPulseNamespaceBirthProtectionVerifier.RecordAsync(catalog, protection?.ProtectionId ?? Guid.NewGuid(),
                operationId, item, parent, router, durable).ConfigureAwait(false);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await parentGuard.InspectAsync(durable).ConfigureAwait(false));
            var member = new MirrorPulseNamespaceMemberRegistration(1, operationId, parentMemberId,
                MirrorPulseNamespaceMemberOrigin.ProtectedBirth, operationId, protection.ProtectionId, birth.RootId, path,
                observed.LocalObject, isDirectory, observed.OwnerSid, DateTimeOffset.UtcNow);
            actor.AssertAccess();
            result = await MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, member, item, parent, router, actor, durable).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return result;
    }

    private void CreateOrdinaryItem(CloudItem item, bool isDirectory, string inheritedDacl)
    {
        if (isDirectory)
        {
            // Inherit the guarded parent's DACL and select the current user as owner atomically,
            // including when a diagnostic fixture happens to use an elevated process token.
            var security = new DirectorySecurity(); security.SetOwner(execution.OwnerSid);
            // An owner-only DirectorySecurity has an empty DACL, not implicit inheritance.
            security.SetSecurityDescriptorSddlForm(inheritedDacl, AccessControlSections.Access);
            new DirectoryInfo(item.FullPath).Create(security);
        }
        else
        {
            var security = new FileSecurity(); security.SetOwner(execution.OwnerSid);
            security.SetSecurityDescriptorSddlForm(MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(execution), AccessControlSections.Access);
            using FileStream writer = new FileInfo(item.FullPath).Create(FileMode.CreateNew, FileSystemRights.Write,
                FileShare.None, 4096, FileOptions.None, security);
            writer.Flush(flushToDisk: true);
        }
    }

    private async Task ConvertDirectoryAsync(CloudItem item, MirrorPulseNamespaceBirthConversionPreparation conversion,
        MirrorPulseNamespaceBirthPlan plan, CancellationToken token)
    {
        await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, token).ConfigureAwait(false);
        var facts = await lease.InspectAsync(token).ConfigureAwait(false);
        var snapshot = await item.InspectAsync(token).ConfigureAwait(false);
        var identity = new CloudPlaceholderIdentity(plan.ItemId, plan.RemoteId, plan.RemoteRevision);
        if (!snapshot.Exists || snapshot.IsTombstone || snapshot.Kind != CloudItemKind.Directory ||
            facts.LocalObject != conversion.LocalObject || facts.OwnerSid != conversion.OwnerSid || facts.LinkCount != 1 ||
            facts.Dacl != conversion.BirthDacl || !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(conversion.ExpectedConvertedDacl, facts.Dacl) ||
            snapshot.IsPlaceholder && !snapshot.PlaceholderIdentity.Span.SequenceEqual(identity.Encode()))
            throw new InvalidDataException("Directory conversion requires its first object, descriptor and public identity.");
        if (!snapshot.IsPlaceholder)
            await item.ConvertToPlaceholderAsync(identity, CloudPlaceholderConversionOptions.CreateBuilder()
                .WithPopulationState(CloudDirectoryPopulationState.Complete).Build(), token).ConfigureAwait(false);
    }
}
