using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CfSharp.CrashProbe;

public sealed record CliDisabledJournalFact(Guid OperationId, long Sequence, Guid? ItemId, string Kind,
    int AttemptCount, DateTimeOffset CreatedAt, string PayloadSha256);
public sealed record CliDisabledEffectFact(Guid OperationId, string State, string? Kind, bool? IsDirectory,
    string? ContentSha256, bool NativeConfirmationVerified, bool NativeMayAcknowledge);
public sealed record CliDisabledOriginalFact(Guid OperationId, bool Pending, Guid? PlanId, IReadOnlyList<CliDisabledEffectFact> Effects, string? BlockReason);

public static partial class CliFixtureProbe
{
    private sealed record DisabledFixtureCatalog(string Root, MirrorPulseStoragePaths Paths, CliDisabledFixtureMarker Marker,
        MirrorPulseProductCatalog Catalog, CliDisabledFixtureInventory Inventory) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Catalog.DisposeAsync();
    }

    private static async Task<DisabledFixtureCatalog> OpenDisabledFixtureAsync(string directory, Action<string> stage)
    {
        string root = CliDisabledFixtureLayout.RequireFixtureDirectory(directory);
        string markerPath = Path.Combine(root, CliDisabledFixtureLayout.MarkerFileName);
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        foreach (string path in new[] { root, markerPath, paths.SyncRootPath, paths.DataRootPath,
            Path.GetDirectoryName(paths.ProductCatalogDatabasePath)!, paths.ProductCatalogDatabasePath })
            if (!Path.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The stopped disabled fixture was redirected or lost.");
        if (new FileInfo(markerPath).Length > 65_536) throw new InvalidDataException("The disabled marker exceeds its bounded inventory.");
        var marker = JsonSerializer.Deserialize<CliDisabledFixtureMarker>(await File.ReadAllBytesAsync(markerPath), JsonOptions)
            ?? throw new InvalidDataException("The disabled marker is empty.");
        string? source = FileVersionInfo.GetVersionInfo(typeof(CliFixtureProbe).Assembly.Location).ProductVersion;
        if (source is null || !source.EndsWith("+" + marker.SourceSha, StringComparison.Ordinal))
            throw new InvalidDataException("The disabled oracle differs from the actual selected source.");
        stage("CatalogOwnership");
        var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
        try
        {
            var inventory = CliDisabledFixtureLayout.Authenticate(root, marker, await catalog.ReadAdapterTopologyAsync());
            return new(root, paths, marker, catalog, inventory);
        }
        catch { await catalog.DisposeAsync(); throw; }
    }

    /// <summary>Authenticates the complete fixed namespace before restoring this stopped GUID-owned fixture.</summary>
    public static async Task<int> RunDisabledCleanupAsync(string directory)
    {
        string stage = "FixtureAuthentication";
        try
        {
            await using var fixture = await OpenDisabledFixtureAsync(directory, value => stage = value);
            var result = await CleanupAuthenticatedInventoryAsync(fixture.Root, fixture.Paths, fixture.Catalog,
                fixture.Inventory.Roots, fixture.Inventory.AllowedEntries, value => stage = value);
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions)); return 0;
        }
        catch (Exception error) { error.Data["CliFixtureCleanupStage"] = stage; throw; }
    }

    /// <summary>Reads original SDK journal facts and product receipts only under the stopped Host's actual exclusive lease.</summary>
    public static async Task<int> RunDisabledAuditAsync(string directory)
    {
        string stage = "FixtureAuthentication";
        try
        {
            await using var fixture = await OpenDisabledFixtureAsync(directory, value => stage = value);
            var catalog = fixture.Catalog; var marker = fixture.Marker; var inventory = fixture.Inventory;
            stage = "OriginalMemberBindings";
            var members = new List<object>(); var itemIds = new HashSet<Guid>();
            foreach (var entry in marker.Members.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                var spec = CliDisabledFixtureLayout.MemberSpecs[entry.Key];
                var route = marker.Routes.Single(item => item.FixtureSide == spec.Side);
                var member = await catalog.ReadNamespaceMemberAsync(entry.Value.MemberId)
                    ?? throw new InvalidDataException("The original disabled fixture member is missing.");
                if (member.RootId?.ToString() != route.RootId || member.RelativePath != route.DirectoryName + "/" + spec.Name || member.IsDirectory != spec.IsDirectory)
                    throw new InvalidDataException("The stopped oracle cannot associate a foreign member with an original fixture item.");
                if (inventory.Roots.Single(item => item.RootId == member.RootId).State == RootRegistrationState.Disabled &&
                    entry.Key is not ("retiredFile" or "retiredDirectory" or "ephemeralFile" or "ephemeralDirectory"))
                {
                    var protection = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(member.MemberId)
                        ?? throw new InvalidDataException("A live disabled fixture member lost its retained identity.");
                    if (protection.Identity.ItemId != entry.Value.ItemId)
                        throw new InvalidDataException("A disabled original cannot borrow another member's native ItemId.");
                }
                itemIds.Add(entry.Value.ItemId);
                members.Add(new
                {
                    key = entry.Key,
                    member.MemberId,
                    originalItemId = entry.Value.ItemId,
                    rootId = route.RootId,
                    member.IsDirectory,
                    originalRelativePath = member.RelativePath,
                    originalBindingSha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(member.LocalObject, JsonOptions)))
                });
            }
            stage = "OfficialJournalOwnership";
            if (!File.Exists(fixture.Paths.CfSharpStateDatabasePath) || (File.GetAttributes(fixture.Paths.CfSharpStateDatabasePath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The official fixture journal was redirected or lost.");
            await using var store = await MirrorPulseCfSharpStateStoreFactory.Create(fixture.Paths).OpenAsync(new(fixture.Paths.SyncRootPath));
            await using var transaction = await store.BeginTransactionAsync();
            var all = await transaction.Operations.ListAsync(512);
            if (all.Count >= 512) throw new InvalidDataException("The fixture exceeded its bounded official journal observation.");
            var selected = all.Where(row => row.ItemId is { } itemId && itemIds.Contains(itemId))
                .OrderBy(row => row.Sequence).Select(row => new CliDisabledJournalFact(row.OperationId, row.Sequence, row.ItemId,
                    row.Kind.ToString(), row.AttemptCount, row.CreatedAt, Convert.ToHexString(SHA256.HashData(row.Payload.Span)))).ToArray();
            var blocked = await catalog.ReadBlockedLocalOperationsAsync();
            if (blocked.Count > 128) throw new InvalidDataException("The fixture exceeded its bounded blocked-operation observation.");
            foreach (var value in blocked)
            {
                string relativePath = CliDisabledFixtureLayout.RequireAllowedRelativePath(inventory, value.RelativePath);
                if (!inventory.Roots.Any(root => root.InstanceId == value.InstanceId &&
                    (relativePath == root.DirectoryName || relativePath.StartsWith(root.DirectoryName + "/", StringComparison.Ordinal))))
                    throw new InvalidDataException("A foreign blocked operation entered the stopped fixture oracle.");
            }
            stage = "OriginalOperationOutcomes";
            var originals = new List<CliDisabledOriginalFact>();
            foreach (Guid id in marker.Originals)
            {
                var pending = await transaction.Operations.GetAsync(id);
                var owner = await catalog.ReadJournalCoalescingOwnerAsync(id);
                var effects = new List<CliDisabledEffectFact>();
                if (owner is null) effects.Add(await ReadEffectAsync(id));
                else
                {
                    RequireRoute(owner.InstanceId, owner.RootKey, owner.OriginalPath);
                    RequireRoute(owner.InstanceId, owner.RootKey, owner.FinalPath);
                    if (owner.Members.Count > 64 || !owner.Members.Any(item => item.OperationId == id))
                        throw new InvalidDataException("The retained group lost its original bounded journal membership.");
                    var dispatch = await catalog.ReadJournalCoalescingDispatchAsync(owner.PlanId);
                    foreach (var step in dispatch.Steps) effects.Add(await ReadEffectAsync(step.OperationId));
                }
                originals.Add(new(id, pending is not null, owner?.PlanId, effects, blocked.SingleOrDefault(item => item.OperationId == id)?.Reason.ToString()));
            }
            await transaction.RollbackAsync();
            stage = "OriginalLocalReceipts";
            var births = new List<object>(); var moves = new List<object>(); var retirements = new List<object>();
            foreach (var command in marker.Commands.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                if (command.Key.StartsWith("create", StringComparison.Ordinal) && await catalog.ReadNamespaceBirthAsync(command.Value) is { } birth)
                {
                    stage = "OriginalBirthReceipt";
                    string memberKey = command.Key switch
                    { "createBornFile" => "bornFile", "createBornDirectory" => "bornDirectory", "createEphemeralFile" => "ephemeralFile", _ => "ephemeralDirectory" };
                    var spec = CliDisabledFixtureLayout.MemberSpecs[memberKey];
                    if (birth.RootId != inventory.Roots[0].RootId || birth.RelativePath != inventory.Roots[0].DirectoryName + "/" + spec.Name ||
                        birth.IsDirectory != spec.IsDirectory || birth.Origin != MirrorPulseNamespaceBirthOrigin.ControlledCreation)
                        throw new InvalidDataException("The original local birth differs from its exact offline fixture command.");
                    var plan = await catalog.ReadNamespaceBirthPlanAsync(command.Value);
                    if (marker.Phase != "Setup" && plan is null) throw new InvalidDataException("An original controlled birth lost its official native plan.");
                    if (marker.Members.TryGetValue(memberKey, out var originalMember) && plan?.ItemId != originalMember.ItemId)
                        throw new InvalidDataException("The original birth cannot borrow another fixture identity.");
                    births.Add(new
                    {
                        key = command.Key,
                        birth.OperationId,
                        rootId = birth.RootId.ToString(),
                        origin = birth.Origin.ToString(),
                        birth.RelativePath,
                        birth.IsDirectory,
                        plan?.ItemId,
                        originalSha256 = Fingerprint(birth)
                    });
                }
                if (command.Key.StartsWith("move", StringComparison.Ordinal) && await catalog.ReadNamespaceMemberMoveAsync(command.Value) is { } move)
                {
                    stage = "OriginalMoveReceipt";
                    string memberKey = command.Key switch { "moveKeptFile" => "keptFile", "moveBornFile" => "bornFile", _ => "bornDirectory" };
                    string targetName = command.Key switch { "moveKeptFile" => "kept-renamed.txt", "moveBornFile" => "born-renamed.txt", _ => "moved-dir" };
                    var spec = CliDisabledFixtureLayout.MemberSpecs[memberKey];
                    if (!marker.Members.TryGetValue(memberKey, out var originalMember) || move.MemberId != originalMember.MemberId ||
                        move.RootId != inventory.Roots[0].RootId || move.Version != 1 || move.RemoteEntry is not null ||
                        move.Source.RelativePath != inventory.Roots[0].DirectoryName + "/" + spec.Name ||
                        move.Target.RelativePath != inventory.Roots[0].DirectoryName + "/" + targetName)
                        throw new InvalidDataException("The original move differs from its exact offline member and admitted names.");
                    var completion = await catalog.ReadNamespaceMemberMoveCompletionAsync(command.Value);
                    if (marker.Phase != "Setup" && completion is null) throw new InvalidDataException("The original local move is incomplete.");
                    moves.Add(new
                    {
                        key = command.Key,
                        move.OperationId,
                        move.MemberId,
                        source = move.Source.RelativePath,
                        target = move.Target.RelativePath,
                        originalSha256 = Fingerprint(move),
                        completion
                    });
                }
                if (command.Key.StartsWith("retire", StringComparison.Ordinal) && await catalog.ReadNamespaceRetirementAsync(command.Value) is { } retired)
                {
                    stage = "OriginalRetirementReceipt";
                    string memberKey = command.Key switch
                    { "retireFile" => "retiredFile", "retireDirectory" => "retiredDirectory", "retireEphemeralFile" => "ephemeralFile", _ => "ephemeralDirectory" };
                    var spec = CliDisabledFixtureLayout.MemberSpecs[memberKey];
                    if (!marker.Members.TryGetValue(memberKey, out var originalMember) || retired.Intent.MemberId != originalMember.MemberId ||
                        retired.Intent.RootId != inventory.Roots[0].RootId || retired.Intent.Version != 1 || retired.Intent.BatchOperationId is not null ||
                        retired.Intent.RemoteEntry is not null || retired.Intent.TransferEntry is not null || retired.Intent.IsDirectory != spec.IsDirectory ||
                        retired.Intent.RelativePath != inventory.Roots[0].DirectoryName + "/" + spec.Name)
                        throw new InvalidDataException("The original leaf retirement differs from its exact offline member and local origin.");
                    if (marker.Phase != "Setup" && retired.Phase != MirrorPulseNamespaceRetirementPhase.Completed)
                        throw new InvalidDataException("The original local leaf retirement is incomplete.");
                    retirements.Add(new
                    {
                        key = command.Key,
                        retired.OperationId,
                        retired.Intent.MemberId,
                        retired.Intent.IsDirectory,
                        phase = retired.Phase.ToString(),
                        originalSha256 = Fingerprint(retired)
                    });
                }
            }
            object? replacement = null;
            Guid replacementId = marker.Commands["replaceKeptFile"];
            if (await catalog.ReadNamespaceReplacementExecutionAsync(replacementId) is { } replaced)
            {
                stage = "OriginalReplacementBackup";
                if (!marker.Members.TryGetValue("keptFile", out var originalMember) || replaced.Intent.MemberId != originalMember.MemberId ||
                    replaced.Intent.RootId != inventory.Roots[0].RootId || replaced.Intent.Version != 1 ||
                    replaced.Intent.Original.Context.Location.RelativePath != inventory.Roots[0].DirectoryName + "/kept.txt")
                    throw new InvalidDataException("The original replacement differs from its exact original offline member and placement.");
                string? backupDigest = null;
                if (replaced.Backup is { } original)
                {
                    await using var backup = await new MirrorPulseLocalReplacementBackupStore(fixture.Paths).OpenLeaseAsync(replacementId, original.Content);
                    backupDigest = Convert.ToHexString(await SHA256.HashDataAsync(backup.Data));
                    if (backupDigest != original.Content.Sha256 || backup.Data.Length != original.Content.Length)
                        throw new InvalidDataException("The original complete private replacement backup changed.");
                }
                var completion = await catalog.ReadNamespaceReplacementCompletionAsync(replacementId);
                if (marker.Phase != "Setup" && (backupDigest is null || completion is null)) throw new InvalidDataException("The original local replacement or complete backup is missing.");
                replacement = new
                {
                    operationId = replacementId,
                    replaced.Intent.MemberId,
                    originalSha256 = Fingerprint(replaced.Intent),
                    backupSha256 = backupDigest,
                    completion
                };
            }
            if (marker.Phase != "Setup" && (members.Count != CliDisabledFixtureLayout.MemberSpecs.Count ||
                births.Count != 4 || moves.Count != 3 || retirements.Count != 4 || replacement is null))
                throw new InvalidDataException("The stopped oracle requires every original member and local command receipt.");
            stage = "FinalRootAndEffectBoundaries";
            var rootFacts = new List<object>();
            var fenced = await catalog.ReadNamespaceDispatchFencedRootsAsync();
            foreach (var root in inventory.Roots)
                rootFacts.Add(new
                {
                    rootId = root.RootId.ToString(),
                    instanceId = root.InstanceId.ToString(),
                    state = root.State.ToString(),
                    dispatchFenced = fenced.Contains(root.RootId),
                    pending = await catalog.ReadNamespacePendingOperationsAsync(root.RootId)
                });
            var incomplete = await catalog.ReadIncompleteMutationsAsync();
            if (incomplete.Count > 128) throw new InvalidDataException("The fixture exceeded its bounded retained effect observation.");
            var unsettled = new List<CliDisabledEffectFact>();
            foreach (var value in incomplete) unsettled.Add(await ReadEffectAsync(value.Intent.OperationId));
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                marker.SourceSha,
                marker.Phase,
                exclusiveCatalogOwner = true,
                members,
                roots = rootFacts,
                journal = selected,
                originals,
                births,
                moves,
                retirements,
                replacement,
                unsettled,
                blocked = blocked.Select(value => new { value.OperationId, value.InstanceId, value.RelativePath, reason = value.Reason.ToString() }).ToArray(),
                mutationExecuted = false,
                originalSdkAcknowledgementExecuted = false
            }, JsonOptions));
            return 0;

            void RequireRoute(InstanceId instance, string key, string path)
            {
                var root = inventory.Roots.SingleOrDefault(item => item.InstanceId == instance && item.UniquenessKey == key);
                string relative = root?.DirectoryName + "/" + path.Replace('\\', '/');
                if (root is null || !inventory.AllowedEntries.ContainsKey(relative))
                    throw new InvalidDataException("A foreign root or path entered the disabled effect oracle.");
            }
            async Task<CliDisabledEffectFact> ReadEffectAsync(Guid id)
            {
                var mutation = await catalog.ReadMutationAsync(id);
                if (mutation is null) return new(id, "Missing", null, null, null, false, false);
                RequireRoute(mutation.Intent.InstanceId, mutation.Intent.RootKey, mutation.Intent.RelativePath);
                var proof = await catalog.ReadContentAcceptanceProofAsync(id);
                var native = await catalog.ReadContentConfirmationReceiptAsync(id);
                if (proof is not null && (proof.OperationId != id || proof.Sha256 != mutation.Intent.ContentSha256))
                    throw new InvalidDataException("An actual content proof differs from its retained original effect.");
                return new(id, mutation.State.ToString(), mutation.Intent.Kind.ToString(), mutation.Intent.IsDirectory,
                    proof?.Sha256 ?? mutation.Intent.ContentSha256, native?.NativeConfirmationVerified == true, native?.MayAcknowledge == true);
            }
        }
        catch (Exception error) { error.Data["CliFixtureCleanupStage"] = stage; throw; }
    }

    private static string Fingerprint<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)));
}
