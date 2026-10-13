using System.Runtime.Versioning;
using System.Security.AccessControl;
using CfSharp;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseNamespaceCreationCheckpoint { AfterOriginalBinding, AfterContentRecorded }

/// <summary>Creates or imports a local member under a currently protected registered directory.</summary>
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
    internal Func<MirrorPulseNamespaceCreationCheckpoint, CancellationToken, ValueTask>? Checkpoint { get; init; }

    public async ValueTask<MirrorPulseNamespaceMemberRegistration> CreateAsync(Guid operationId, Guid parentMemberId,
        string name, bool isDirectory, CancellationToken cancellationToken = default)
    {
        ValidateChildRequest(operationId, parentMemberId, name);
        var result = await actor.RunAsync(token => CreateAdmittedAsync(operationId, parentMemberId, name, isDirectory,
            MirrorPulseNamespaceBirthOrigin.ControlledCreation, null, null, false, token), cancellationToken).ConfigureAwait(false);
        return result.Member ?? throw new InvalidDataException("Creation did not complete membership registration.");
    }

    /// <summary>Retains import admission and content intent without creating a native object.</summary>
    /// <remarks>Open and verify the private input before acquiring either the instance scheduler or actor.</remarks>
    public async ValueTask<MirrorPulseNamespaceBirthPlan> PrepareImportAsync(Guid operationId, Guid parentMemberId,
        string name, MirrorPulseLocalImportInputLease input, CancellationToken cancellationToken = default)
    {
        ValidateChildRequest(operationId, parentMemberId, name);
        ArgumentNullException.ThrowIfNull(input);
        if (input.OperationId != operationId) throw new ArgumentException("The input lease belongs to another import.", nameof(input));
        var result = await actor.RunAsync(token => CreateAdmittedAsync(operationId, parentMemberId, name, false,
            MirrorPulseNamespaceBirthOrigin.Import, input.Content, input, true, token), cancellationToken).ConfigureAwait(false);
        return result.Plan;
    }

    /// <summary>Executes the original import; completed replay returns history without touching current bytes.</summary>
    /// <remarks>The input may be omitted only after the original content completion was retained.</remarks>
    public ValueTask<MirrorPulseNamespaceMemberRegistration> ExecuteImportAsync(Guid operationId,
        MirrorPulseLocalImportInputLease? input, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An import operation ID is required.", nameof(operationId));
        return actor.RunAsync(async token =>
        {
            var birth = await catalog.ReadNamespaceBirthAsync(operationId, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original import admission is missing.");
            var plan = await catalog.ReadNamespaceBirthPlanAsync(operationId, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original import plan is missing.");
            if (birth.Origin != MirrorPulseNamespaceBirthOrigin.Import || birth.IsDirectory || plan.Content is null)
                throw new InvalidOperationException("Execution requires an admitted file import with its original content plan.");
            var replay = await catalog.ReadNamespaceMemberAsync(operationId, token).ConfigureAwait(false);
            if (replay is not null)
            {
                RequireReplay(replay, birth, replay.ParentMemberId ?? Guid.Empty);
                return replay;
            }
            if (await catalog.ReadNamespaceBirthContentCompletionAsync(operationId, token).ConfigureAwait(false) is null &&
                (input is null || input.OperationId != operationId || input.Content != plan.Content))
                throw new InvalidOperationException("Uncopied imports require their verified private input lease.");
            var parent = await catalog.ReadNamespaceMemberByBindingAsync(birth.ParentLocalObject, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original import parent member is missing.");
            string name = birth.RelativePath[(birth.RelativePath.LastIndexOf('/') + 1)..];
            var result = await CreateAdmittedAsync(operationId, parent.MemberId, name, false,
                MirrorPulseNamespaceBirthOrigin.Import, plan.Content, input, false, token).ConfigureAwait(false);
            return result.Member ?? throw new InvalidDataException("Import did not complete membership registration.");
        }, cancellationToken);
    }

    private static void ValidateChildRequest(Guid operationId, Guid parentMemberId, string name)
    {
        if (operationId == Guid.Empty || parentMemberId == Guid.Empty)
            throw new ArgumentException("Operation and parent member IDs are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("Creation requires one direct child name.", nameof(name));
    }

    private async ValueTask<(MirrorPulseNamespaceBirthPlan Plan, MirrorPulseNamespaceMemberRegistration? Member)> CreateAdmittedAsync(
        Guid operationId, Guid parentMemberId, string name, bool isDirectory, MirrorPulseNamespaceBirthOrigin origin,
        MirrorPulseNamespaceImportContent? content, MirrorPulseLocalImportInputLease? input, bool prepareOnly, CancellationToken token)
    {
        actor.AssertAccess();
        var retainedBirth = await catalog.ReadNamespaceBirthAsync(operationId, token).ConfigureAwait(false);
        var retainedPlan = await catalog.ReadNamespaceBirthPlanAsync(operationId, token).ConfigureAwait(false);
        if (retainedBirth is not null && (retainedBirth.Origin != origin || retainedBirth.IsDirectory != isDirectory ||
            retainedBirth.RelativePath[(retainedBirth.RelativePath.LastIndexOf('/') + 1)..] != name) ||
            retainedPlan is not null && retainedPlan.Content != content)
            throw new InvalidOperationException("The operation ID already identifies a different creation or import.");
        var replay = await catalog.ReadNamespaceMemberAsync(operationId, token).ConfigureAwait(false);
        if (replay is not null)
        {
            RequireReplay(replay, retainedBirth, parentMemberId);
            return (retainedPlan ?? throw new InvalidDataException("Completed creation lost its original plan."), replay);
        }
        var parentMember = await catalog.ReadNamespaceMemberAsync(parentMemberId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The creation parent is not registered.");
        if (!parentMember.IsDirectory || parentMember.RootId is null)
            throw new InvalidOperationException("Creation requires a directory inside an Adapter's managed root.");
        var location = await catalog.ReadNamespaceMemberLocationAsync(parentMemberId, token).ConfigureAwait(false);
        CloudDirectory parent = fileSystem.GetDirectory(location.RelativePath);
        // CfSharp performs public child-name validation before any durable product work.
        CloudItem item = isDirectory ? parent.GetDirectory(name) : parent.GetFile(name);
        string path = item.RelativePath.Replace('\\', '/');
        if (retainedBirth is not null && (retainedBirth.RelativePath != path || retainedBirth.IsDirectory != isDirectory ||
            retainedBirth.RootId != parentMember.RootId || retainedBirth.Origin != origin ||
            (retainedBirth.BornParent?.BirthOperationId ?? retainedBirth.ParentEvidenceId) != parentMember.OriginId))
            throw new InvalidOperationException("The operation ID already identifies a different creation.");
        var proof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, token).ConfigureAwait(false);
        if (proof.RoleSid != execution.RoleSid.Value || proof.OwnerSid != execution.OwnerSid.Value ||
            proof.RelativePath != location.RelativePath)
            throw new InvalidOperationException("Creation requires the current parent role and its proved location.");
        if ((await catalog.ReadNamespaceRecoveryRootsAsync(token).ConfigureAwait(false)).Contains(parentMember.RootId.Value))
            throw new InvalidOperationException("The creation root has pending namespace recovery.");
        (MirrorPulseNamespaceBirthPlan Plan, MirrorPulseNamespaceMemberRegistration? Member) result = default;
        await execution.RunNamespaceOperationAsync(async () =>
        {
            await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, router, token).ConfigureAwait(false);
            if (retainedBirth is null && parentMember.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth &&
                location.CurrentPositionOperationId is not null)
            {
                var epoch = await catalog.ReadLatestNamespaceBirthProtectionAsync(parentMember.OriginId, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The placed parent lost its retained protection epoch.");
                if (epoch.Placement?.PositionOperationId != location.CurrentPositionOperationId)
                {
                    // A first birth path cannot authorize creating below a relocated parent.
                    // Reverify its actual object and parent without writing ACLs or old facts.
                    _ = await MirrorPulseNamespaceBirthProtectionVerifier.RecordRegisteredAsync(catalog, Guid.NewGuid(),
                        parentMember.MemberId, parent, parent.Parent
                            ?? throw new InvalidDataException("A placed parent requires its actual ancestor."), router, actor, token).ConfigureAwait(false);
                    proof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, token).ConfigureAwait(false);
                }
            }
            var birth = retainedBirth ?? new MirrorPulseNamespaceBirthIntent(proof.BornParent is null ? 1 : 2, operationId,
                parentMember.RootId.Value, proof.BornParent is null ? parentMember.OriginId : Guid.Empty, proof.PermissionOperationId,
                parentMember.LocalObject, path, isDirectory, origin,
                execution.RoleSid.Value, DateTimeOffset.UtcNow)
            { BornParent = proof.BornParent };
            var before = await MirrorPulseNamespaceBirthProtectionVerifier.InspectRetainedParentAsync(
                birth, parent, proof, parentGuard, token).ConfigureAwait(false);
            if (retainedBirth is null && (await item.InspectAsync(token).ConfigureAwait(false)).Exists)
                throw new IOException("The creation destination is occupied.");
            if (retainedBirth is null)
                birth = await catalog.PrepareNamespaceBirthAsync(birth, token).ConfigureAwait(false);
            var plan = retainedPlan;
            if (plan is null)
            {
                var route = router.ResolvePath(path);
                var identity = router.CreateFileIdentity(route.InstanceId, route.RootKey, "local:" + operationId.ToString("N"));
                plan = await catalog.PrepareNamespaceBirthPlanAsync(new(content is null ? 1 : 2, operationId, identity.ItemId, identity.RemoteId,
                    null, DateTimeOffset.UtcNow)
                { Content = content }, token).ConfigureAwait(false);
            }
            result = (plan, null);
            if (prepareOnly) return;
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
                CreateOrdinaryItem(item, isDirectory);
                conversion = await MirrorPulseNamespaceBirthConversionPreparer.RecordAsync(catalog, operationId, item, router,
                    expectedDacl, CancellationToken.None).ConfigureAwait(false);
            }
            else if (conversion is null)
                throw new InvalidOperationException("A started creation without its first native binding requires explicit recovery; it cannot be repeated.");

            CancellationToken durable = CancellationToken.None;
            if (plan.Content is not null)
                await CheckpointAsync(MirrorPulseNamespaceCreationCheckpoint.AfterOriginalBinding, durable).ConfigureAwait(false);
            if (await catalog.ReadNamespaceBirthObservationAsync(operationId, durable).ConfigureAwait(false) is null)
            {
                if (isDirectory)
                    await ConvertDirectoryAsync(item, conversion, plan, durable).ConfigureAwait(false);
                else
                {
                    if (plan.Content is not null)
                    {
                        _ = await new MirrorPulseNamespaceImportContentWriter(catalog, router, execution, actor)
                            .CopyAsync(operationId, (CloudFile)item, parent, input, durable).ConfigureAwait(false);
                        await CheckpointAsync(MirrorPulseNamespaceCreationCheckpoint.AfterContentRecorded, durable).ConfigureAwait(false);
                    }
                    var recovery = new MirrorPulseNamespaceBirthRecoveryCoordinator(catalog, router, execution);
                    var action = await recovery.PrepareStagedFileAsync(Guid.NewGuid(), operationId,
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
            var member = new MirrorPulseNamespaceMemberRegistration(location.CurrentPositionOperationId is null ? 1 : 2, operationId, parentMemberId,
                MirrorPulseNamespaceMemberOrigin.ProtectedBirth, operationId, protection.ProtectionId, birth.RootId, path,
                observed.LocalObject, isDirectory, observed.OwnerSid, DateTimeOffset.UtcNow)
            { FirstParentPositionOperationId = location.CurrentPositionOperationId };
            actor.AssertAccess();
            result = (plan, await MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, member, item, parent, router, actor, durable).ConfigureAwait(false));
        }).ConfigureAwait(false);
        return result;
    }

    private ValueTask CheckpointAsync(MirrorPulseNamespaceCreationCheckpoint checkpoint, CancellationToken token) =>
        Checkpoint?.Invoke(checkpoint, token) ?? ValueTask.CompletedTask;

    private static void RequireReplay(MirrorPulseNamespaceMemberRegistration replay, MirrorPulseNamespaceBirthIntent? birth,
        Guid parentMemberId)
    {
        if (birth is null || replay.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth || replay.OriginId != birth.OperationId ||
            replay.MemberId != birth.OperationId || replay.ParentMemberId != parentMemberId || replay.IsDirectory != birth.IsDirectory ||
            replay.RootId != birth.RootId || replay.RelativePath != birth.RelativePath)
            throw new InvalidDataException("Creation replay lost its original member provenance.");
    }

    private void CreateOrdinaryItem(CloudItem item, bool isDirectory)
    {
        if (isDirectory)
        {
            // Birth uses an explicit owned policy. Inherited protection is applied only
            // after public conversion, while the first binding and parent remain guarded.
            var security = new DirectorySecurity(); security.SetOwner(execution.OwnerSid);
            security.SetSecurityDescriptorSddlForm(MirrorPulseNamespacePermissionPolicy.CreateNewObjectDacl(execution, true), AccessControlSections.Access);
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
        RequireDirectory(facts, snapshot);
        if (!snapshot.IsPlaceholder)
            await item.ConvertToPlaceholderAsync(identity, CloudPlaceholderConversionOptions.CreateBuilder()
                .WithPopulationState(CloudDirectoryPopulationState.Complete).Build(), token).ConfigureAwait(false);
        // Directory conversion is not supported inside CfSharp's protected scope. The
        // metadata lease pins this original name across public conversion; the separate
        // public directory scope then owns the same-object descriptor handoff.
        var receipt = await MirrorPulseWindowsNamespacePermissionLease.RunOwnedProtectedAsync(item, router, conversion.LocalObject,
            async (protectedLease, stop) =>
            {
                var current = await protectedLease.InspectAsync(stop).ConfigureAwait(false);
                RequireDirectoryObject(current);
                var converted = await protectedLease.InspectLocalIdentityAsync(stop).ConfigureAwait(false);
                if (!converted.IsPlaceholder || converted.PlaceholderIdentity is not { } nativeIdentity || !nativeIdentity.Equals(identity) ||
                    converted.ItemId is { } projectedItem && projectedItem != plan.ItemId ||
                    converted.RemoteId is { } projectedRemote && projectedRemote != plan.RemoteId)
                    throw new InvalidDataException("Directory permission handoff requires its public converted identity.");
                if (!MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(conversion.ExpectedConvertedDacl, current.Dacl))
                    await protectedLease.ApplyDaclAsync(conversion.ExpectedConvertedDacl, stop).ConfigureAwait(false);
                current = await protectedLease.InspectAsync(stop).ConfigureAwait(false);
                RequireDirectoryObject(current);
                if (!MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(conversion.ExpectedConvertedDacl, current.Dacl))
                    throw new InvalidDataException("Directory inherited protection was not independently verified.");
            }, token).ConfigureAwait(false);
        if (receipt.Outcome != CloudProtectedLocalOperationOutcome.Completed || !receipt.CallbackCompleted || !receipt.Drained)
            throw new IOException("The created directory requires protected permission recovery.", receipt.Error);
        RequireDirectory(await lease.InspectAsync(token).ConfigureAwait(false), await item.InspectAsync(token).ConfigureAwait(false));

        void RequireDirectory(MirrorPulseNamespacePermissionObject observed, CloudItemSnapshot native)
        {
            RequireDirectoryObject(observed);
            if (!native.Exists || native.IsTombstone || native.Kind != CloudItemKind.Directory || native.LocalBinding is not { } binding ||
                observed.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
                !native.IsPlaceholder && observed.Dacl != conversion.BirthDacl ||
                native.IsPlaceholder && !native.PlaceholderIdentity.Span.SequenceEqual(identity.Encode()))
                throw new InvalidDataException("Directory conversion requires its first object, known descriptor and public identity.");
        }

        void RequireDirectoryObject(MirrorPulseNamespacePermissionObject observed)
        {
            if (observed.LocalObject != conversion.LocalObject || observed.OwnerSid != conversion.OwnerSid || !observed.IsDirectory || observed.LinkCount != 1 ||
                observed.RelativePath != item.RelativePath.Replace('\\', '/') ||
                (observed.Dacl != conversion.BirthDacl && !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(conversion.ExpectedConvertedDacl, observed.Dacl)))
                throw new InvalidDataException("Directory handoff requires its first binding, owner and known protection descriptor.");
        }
    }
}
