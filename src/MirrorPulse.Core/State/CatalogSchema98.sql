-- Empty product catalog schema 96, generated through the real public initializer.
CREATE TABLE adapter_remote_projections(instance_id TEXT NOT NULL,batch_id TEXT NOT NULL,
    source_fingerprint BLOB NOT NULL CHECK(length(source_fingerprint)=32),
    payload BLOB NOT NULL CHECK(length(payload)>0 AND length(payload)<=33554432),
    payload_fingerprint BLOB NOT NULL CHECK(length(payload_fingerprint)=32),PRIMARY KEY(instance_id,batch_id));
CREATE TABLE adapter_topology (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    payload TEXT NOT NULL
);
CREATE TABLE blocked_local_operations (
    operation_id TEXT PRIMARY KEY,
    instance_id TEXT NULL,
    relative_path TEXT NOT NULL,
    reason INTEGER NOT NULL,
    observed_utc TEXT NOT NULL
);
CREATE TABLE coalesced_conflict_remote_heads(
    rejected_operation_id TEXT NOT NULL PRIMARY KEY REFERENCES mutation_intents(operation_id),
    observation_id TEXT NOT NULL UNIQUE REFERENCES coalesced_conflict_remote_observations(observation_id));
CREATE TABLE coalesced_conflict_remote_members(
    selected_conflict_id TEXT NOT NULL PRIMARY KEY REFERENCES upload_conflicts(conflict_id),
    previous_conflict_id TEXT NOT NULL UNIQUE REFERENCES upload_conflicts(conflict_id),
    observation_id TEXT NOT NULL REFERENCES coalesced_conflict_remote_observations(observation_id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    original_id TEXT NOT NULL REFERENCES journal_coalescing_members(operation_id),
    UNIQUE(observation_id,ordinal),UNIQUE(observation_id,original_id));
CREATE TABLE coalesced_conflict_remote_observations(
    observation_id TEXT NOT NULL PRIMARY KEY,
    rejected_operation_id TEXT NOT NULL REFERENCES mutation_intents(operation_id),
    plan_id TEXT NOT NULL REFERENCES journal_coalescing_plans(plan_id),
    refresh_command_id TEXT NULL UNIQUE REFERENCES user_commands(command_id),
    payload BLOB NOT NULL CHECK(length(payload)<=16777216),
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE conflict_deletion_members(
    original_id TEXT PRIMARY KEY, command_id TEXT NOT NULL REFERENCES conflict_deletions(command_id));
CREATE TABLE conflict_deletion_observations(
    operation_id TEXT PRIMARY KEY, decision_id TEXT NOT NULL REFERENCES conflict_deletions(command_id),
    payload BLOB NOT NULL, fingerprint BLOB NOT NULL);
CREATE TABLE conflict_deletion_retries(
    sequence INTEGER PRIMARY KEY AUTOINCREMENT, command_id TEXT NOT NULL UNIQUE, operation_id TEXT NOT NULL UNIQUE,
    decision_id TEXT NOT NULL REFERENCES conflict_deletions(command_id), previous_operation_id TEXT NOT NULL UNIQUE,
    payload BLOB NOT NULL, fingerprint BLOB NOT NULL);
CREATE TABLE conflict_deletions(
    command_id TEXT PRIMARY KEY, remote_operation_id TEXT NOT NULL UNIQUE, payload BLOB NOT NULL, fingerprint BLOB NOT NULL,
    phase INTEGER NOT NULL CHECK(phase BETWEEN 0 AND 5));
CREATE TABLE conflict_keep_both(command_id TEXT PRIMARY KEY REFERENCES user_commands(command_id),
    payload BLOB NOT NULL,fingerprint BLOB NOT NULL,copy_payload BLOB NULL,copy_fingerprint BLOB NULL,
    original_retries_prepared INTEGER NOT NULL DEFAULT 0 CHECK(original_retries_prepared IN (0,1)),
    CHECK((copy_payload IS NULL)=(copy_fingerprint IS NULL)));
CREATE TABLE conflict_keep_both_members(original_id TEXT NOT NULL,
    command_id TEXT NOT NULL REFERENCES conflict_keep_both(command_id),PRIMARY KEY(original_id,command_id));
CREATE TABLE conflict_keep_both_owners(original_id TEXT PRIMARY KEY,
    command_id TEXT NOT NULL REFERENCES conflict_keep_both(command_id));
CREATE TABLE conflict_keep_remote_copies(
    command_id TEXT NOT NULL REFERENCES conflict_keep_remote_preparations(command_id),
    side INTEGER NOT NULL CHECK(side IN (0,1)),
    payload BLOB NOT NULL CHECK(length(payload)<=65536),
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
    PRIMARY KEY(command_id,side));
CREATE TABLE conflict_keep_remote_file_pending(
    root_id TEXT NOT NULL PRIMARY KEY,
    command_id TEXT NOT NULL UNIQUE REFERENCES conflict_keep_remote_file_starts(command_id));
CREATE TABLE conflict_keep_remote_file_starts(
    command_id TEXT NOT NULL PRIMARY KEY REFERENCES conflict_keep_remote_preparations(command_id),
    member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
    root_id TEXT NOT NULL,
    payload BLOB NOT NULL CHECK(length(payload)<=1048576),
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE conflict_keep_remote_journal_bindings(
    command_id TEXT NOT NULL PRIMARY KEY REFERENCES conflict_keep_remote_preparations(command_id),
    payload BLOB NOT NULL CHECK(length(payload)<=16777216),
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE conflict_keep_remote_members(
    original_id TEXT NOT NULL PRIMARY KEY REFERENCES journal_coalescing_members(operation_id),
    command_id TEXT NOT NULL REFERENCES conflict_keep_remote_preparations(command_id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    conflict_id TEXT NOT NULL UNIQUE REFERENCES upload_conflicts(conflict_id),
    UNIQUE(command_id,ordinal));
CREATE TABLE conflict_keep_remote_preparations(
    command_id TEXT NOT NULL PRIMARY KEY REFERENCES user_commands(command_id),
    payload BLOB NOT NULL CHECK(length(payload)<=16777216),
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE conflict_local_removal_members(
    original_id TEXT PRIMARY KEY, command_id TEXT NOT NULL REFERENCES conflict_local_removals(command_id));
CREATE TABLE conflict_local_removals(
    command_id TEXT PRIMARY KEY, payload BLOB NOT NULL, fingerprint BLOB NOT NULL,
    phase INTEGER NOT NULL CHECK(phase BETWEEN 0 AND 4));
CREATE TABLE content_acceptance_proofs (
    operation_id TEXT PRIMARY KEY REFERENCES mutation_intents(operation_id),
    payload BLOB NOT NULL,
    receipt BLOB NULL
);
CREATE TABLE credential_cleanup (
    reference_id TEXT PRIMARY KEY,
    provider TEXT NOT NULL,
    created_utc TEXT NOT NULL
);
CREATE TABLE deferred_rescan_roots (root_id TEXT PRIMARY KEY);
CREATE TABLE full_rescan_checkpoint (
    singleton INTEGER PRIMARY KEY CHECK (singleton=1),
    generation TEXT NOT NULL,
    phase INTEGER NOT NULL,
    updated_utc TEXT NOT NULL
);
CREATE TABLE full_rescan_request_snapshots (
    singleton INTEGER PRIMARY KEY REFERENCES full_rescan_checkpoint(singleton) ON DELETE CASCADE CHECK(singleton=1),
    generation TEXT NOT NULL,
    captured_utc TEXT NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
);
CREATE TABLE full_rescan_root_requests (
    singleton INTEGER NOT NULL REFERENCES full_rescan_request_snapshots(singleton) ON DELETE CASCADE CHECK(singleton=1),
    root_id TEXT NOT NULL,
    generation TEXT NOT NULL UNIQUE,
    requested_utc TEXT NOT NULL,
    PRIMARY KEY(singleton,root_id)
);
CREATE TABLE instance_runtime (
    instance_id TEXT PRIMARY KEY,
    phase TEXT NOT NULL,
    requires_full_rescan INTEGER NOT NULL,
    last_successful_sync_utc TEXT NULL,
    last_error_code TEXT NULL,
    transfer_operation TEXT NULL,
    transfer_bytes INTEGER NULL,
    transfer_total INTEGER NULL,
    transfer_updated_utc TEXT NULL
);
CREATE TABLE journal_coalescing_executions (
    plan_id TEXT PRIMARY KEY REFERENCES journal_coalescing_plans(plan_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL
);
CREATE TABLE journal_coalescing_members (
    operation_id TEXT PRIMARY KEY,
    plan_id TEXT NOT NULL REFERENCES journal_coalescing_plans(plan_id)
);
CREATE TABLE journal_coalescing_plans (
    plan_id TEXT PRIMARY KEY,
    payload TEXT NOT NULL
);
CREATE TABLE journal_coalescing_retries (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    command_id TEXT NOT NULL UNIQUE REFERENCES user_commands(command_id),
    operation_id TEXT NOT NULL UNIQUE,
    plan_id TEXT NOT NULL REFERENCES journal_coalescing_executions(plan_id),
    ordinal INTEGER NOT NULL CHECK (ordinal IN (0,1)),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL,
    followup_operation_id TEXT NULL
);
CREATE TABLE journal_coalescing_steps (
    operation_id TEXT PRIMARY KEY,
    plan_id TEXT NOT NULL REFERENCES journal_coalescing_executions(plan_id),
    ordinal INTEGER NOT NULL CHECK (ordinal IN (0,1)),
    UNIQUE(plan_id, ordinal)
);
CREATE TABLE managed_root_names (
    directory_name TEXT PRIMARY KEY COLLATE NOCASE,
    root_id TEXT NOT NULL
);
CREATE TABLE managed_root_rename_history (
    operation_id TEXT PRIMARY KEY,
    root_id TEXT NOT NULL,
    payload TEXT NOT NULL,
    proof TEXT NULL,
    observation TEXT NULL
);
CREATE TABLE managed_root_renames (
    root_id TEXT PRIMARY KEY,
    payload TEXT NOT NULL
);
CREATE TABLE mutation_intents (
    operation_id TEXT PRIMARY KEY,
    payload BLOB NOT NULL,
    state INTEGER NOT NULL,
    accepted_revision TEXT NULL,
    updated_utc TEXT NOT NULL,
    execution_started INTEGER NULL CHECK (execution_started IN (0,1))
);
CREATE TABLE namespace_birth_content_completions (
    operation_id TEXT PRIMARY KEY REFERENCES namespace_birth_conversions(operation_id),
    recovery_id TEXT NOT NULL UNIQUE REFERENCES namespace_birth_recovery_actions(recovery_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL
);
CREATE TABLE namespace_birth_conversions (
    operation_id TEXT PRIMARY KEY REFERENCES namespace_birth_starts(operation_id),
    volume_serial TEXT NOT NULL,
    sync_root_file_id TEXT NOT NULL,
    local_file_id TEXT NOT NULL,
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK (length(fingerprint)=32),
    UNIQUE(volume_serial,sync_root_file_id,local_file_id)
);
CREATE TABLE "namespace_birth_intents" (
    operation_id TEXT PRIMARY KEY,
    parent_evidence_id TEXT NULL REFERENCES namespace_permission_baselines(evidence_id),
    parent_operation_id TEXT NULL REFERENCES namespace_permission_changes(operation_id),
    parent_birth_operation_id TEXT NULL REFERENCES namespace_birth_observations(operation_id),
    parent_birth_protection_id TEXT NULL REFERENCES namespace_birth_protections(protection_id),
    relative_path_key TEXT NOT NULL,
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
    CHECK((parent_evidence_id IS NOT NULL AND parent_operation_id IS NOT NULL AND
        parent_birth_operation_id IS NULL AND parent_birth_protection_id IS NULL) OR
        (parent_evidence_id IS NULL AND parent_operation_id IS NULL AND
        parent_birth_operation_id IS NOT NULL AND parent_birth_protection_id IS NOT NULL))
);
CREATE TABLE namespace_birth_observations (
    operation_id TEXT PRIMARY KEY REFERENCES namespace_birth_starts(operation_id),
    volume_serial TEXT NOT NULL,
    sync_root_file_id TEXT NOT NULL,
    local_file_id TEXT NOT NULL,
    item_id TEXT NOT NULL UNIQUE,
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK (length(fingerprint)=32),
    UNIQUE(volume_serial,sync_root_file_id,local_file_id)
);
CREATE TABLE namespace_birth_plans (
    operation_id TEXT PRIMARY KEY REFERENCES namespace_birth_intents(operation_id),
    item_id TEXT NOT NULL UNIQUE,
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK (length(fingerprint)=32)
);
CREATE TABLE "namespace_birth_protections" (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    protection_id TEXT NOT NULL UNIQUE,
    birth_operation_id TEXT NOT NULL REFERENCES namespace_birth_observations(operation_id),
    previous_protection_id TEXT NULL UNIQUE REFERENCES namespace_birth_protections(protection_id),
    parent_permission_operation_id TEXT NULL REFERENCES namespace_permission_changes(operation_id),
    parent_birth_operation_id TEXT NULL REFERENCES namespace_birth_observations(operation_id),
    parent_birth_protection_id TEXT NULL REFERENCES namespace_birth_protections(protection_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
    CHECK((parent_permission_operation_id IS NOT NULL AND parent_birth_operation_id IS NULL AND parent_birth_protection_id IS NULL) OR
        (parent_permission_operation_id IS NULL AND parent_birth_operation_id IS NOT NULL AND parent_birth_protection_id IS NOT NULL))
);
CREATE TABLE namespace_birth_recovery_actions (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    recovery_id TEXT NOT NULL UNIQUE,
    birth_operation_id TEXT NOT NULL REFERENCES namespace_birth_conversions(operation_id),
    previous_recovery_id TEXT NULL REFERENCES namespace_birth_recovery_actions(recovery_id),
    parent_permission_operation_id TEXT NULL REFERENCES namespace_permission_changes(operation_id),
    parent_birth_operation_id TEXT NULL REFERENCES namespace_birth_observations(operation_id),
    parent_birth_protection_id TEXT NULL REFERENCES namespace_birth_protections(protection_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
    CHECK(previous_recovery_id IS NULL OR previous_recovery_id<>recovery_id),
    CHECK((parent_permission_operation_id IS NOT NULL AND parent_birth_operation_id IS NULL AND parent_birth_protection_id IS NULL)
       OR (parent_permission_operation_id IS NULL AND parent_birth_operation_id IS NOT NULL AND parent_birth_protection_id IS NOT NULL))
);
CREATE TABLE namespace_birth_recovery_verifications (
    recovery_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_birth_recovery_actions(recovery_id),
    birth_operation_id TEXT NOT NULL REFERENCES namespace_birth_conversions(operation_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
);
CREATE TABLE "namespace_birth_reservations" (
    parent_kind INTEGER NOT NULL CHECK(parent_kind IN (0,1)),
    parent_id TEXT NOT NULL,
    child_name_key TEXT NOT NULL,
    operation_id TEXT NOT NULL UNIQUE REFERENCES namespace_birth_intents(operation_id),
    PRIMARY KEY(parent_kind,parent_id,child_name_key)
);
CREATE TABLE namespace_birth_starts (
    operation_id TEXT PRIMARY KEY REFERENCES namespace_birth_plans(operation_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK (length(fingerprint)=32)
);
CREATE TABLE namespace_born_member_recovery_actions (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT, action_id TEXT NOT NULL UNIQUE,
    member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
    initialization_id TEXT NOT NULL REFERENCES namespace_initialization_intents(operation_id),
    previous_id TEXT UNIQUE REFERENCES namespace_born_member_recovery_actions(action_id),
    payload BLOB NOT NULL, fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE namespace_born_member_recovery_completions (
    action_id TEXT PRIMARY KEY REFERENCES namespace_born_member_recovery_actions(action_id),
    payload BLOB NOT NULL, fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE namespace_born_member_recovery_heads (
    member_id TEXT PRIMARY KEY REFERENCES namespace_members(member_id),
    action_id TEXT NOT NULL UNIQUE REFERENCES namespace_born_member_recovery_actions(action_id));
CREATE TABLE namespace_born_member_recovery_pending (
    member_id TEXT PRIMARY KEY REFERENCES namespace_members(member_id), root_id TEXT NOT NULL,
    action_id TEXT NOT NULL UNIQUE REFERENCES namespace_born_member_recovery_actions(action_id));
CREATE TABLE namespace_directory_move_completions (
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_move_completions(operation_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
);
CREATE TABLE namespace_directory_move_snapshots (
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_moves(operation_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
);
CREATE TABLE namespace_initialization_completions (
    operation_id TEXT PRIMARY KEY REFERENCES namespace_initialization_intents(operation_id),
    payload BLOB NOT NULL, fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE namespace_initialization_intents (
    operation_id TEXT PRIMARY KEY, payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE namespace_initialization_roots (
    root_id TEXT PRIMARY KEY,
    operation_id TEXT NOT NULL REFERENCES namespace_initialization_intents(operation_id),
    manifest_id TEXT UNIQUE, born_member_id TEXT UNIQUE,
    CHECK ((manifest_id IS NOT NULL AND born_member_id IS NULL) OR
        (manifest_id IS NULL AND born_member_id IS NOT NULL)));
CREATE TABLE namespace_member_identity_acceptances(
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES content_acceptance_proofs(operation_id),
    member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
    previous_operation_id TEXT NULL REFERENCES namespace_member_identity_acceptances(operation_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE namespace_member_move_completions (
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_moves(operation_id),
    member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
);
CREATE TABLE namespace_member_move_observations (
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_move_starts(operation_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
);
CREATE TABLE namespace_member_move_pending (
    root_id TEXT NOT NULL PRIMARY KEY,
    member_id TEXT NOT NULL UNIQUE REFERENCES namespace_members(member_id),
    operation_id TEXT NOT NULL UNIQUE REFERENCES namespace_member_moves(operation_id)
);
CREATE TABLE namespace_member_move_predecessor_claims (
    previous_move_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_move_completions(operation_id),
    operation_id TEXT NOT NULL UNIQUE REFERENCES namespace_member_moves(operation_id)
);
CREATE TABLE namespace_member_move_projections (
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_move_observations(operation_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
);
CREATE TABLE namespace_member_move_reservations (
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_moves(operation_id),
    parent_member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
    child_name_key TEXT NOT NULL,
    UNIQUE(parent_member_id,child_name_key)
);
CREATE TABLE namespace_member_move_starts (
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_moves(operation_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
);
CREATE TABLE namespace_member_moves (
    operation_id TEXT NOT NULL PRIMARY KEY,
    member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
    root_id TEXT NOT NULL,
    source_parent_id TEXT NOT NULL REFERENCES namespace_members(member_id),
    target_parent_id TEXT NOT NULL REFERENCES namespace_members(member_id),
    target_name_key TEXT NOT NULL,
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
, previous_move_id TEXT NULL REFERENCES namespace_member_move_completions(operation_id));
CREATE TABLE namespace_member_names (
    member_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_members(member_id),
    parent_member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
    child_name_key TEXT NOT NULL,
    UNIQUE(parent_member_id,child_name_key)
);
CREATE TABLE namespace_member_positions (
    member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
    operation_id TEXT NOT NULL REFERENCES namespace_member_move_completions(operation_id),
    previous_operation_id TEXT NULL,
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
    PRIMARY KEY(member_id,operation_id)
);
CREATE TABLE namespace_member_retirement_batches (
    operation_id TEXT NOT NULL PRIMARY KEY,
    member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
    root_id TEXT NOT NULL,
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
    phase INTEGER NOT NULL CHECK(phase IN (0,1,2))
);
CREATE TABLE namespace_member_retirements (
    operation_id TEXT NOT NULL PRIMARY KEY,
    member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
    phase INTEGER NOT NULL CHECK(phase IN (0,1,2,3,4))
);
CREATE TABLE namespace_members (
    member_id TEXT NOT NULL PRIMARY KEY,
    origin_kind INTEGER NOT NULL CHECK(origin_kind IN (0,1)),
    original_evidence_id TEXT NULL UNIQUE REFERENCES namespace_permission_baselines(evidence_id),
    birth_operation_id TEXT NULL UNIQUE REFERENCES namespace_birth_observations(operation_id),
    permission_operation_id TEXT NULL REFERENCES namespace_permission_changes(operation_id),
    birth_protection_id TEXT NULL REFERENCES namespace_birth_protections(protection_id),
    parent_member_id TEXT NULL REFERENCES namespace_members(member_id),
    volume_serial TEXT NOT NULL,
    sync_root_file_id TEXT NOT NULL,
    local_file_id TEXT NOT NULL,
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
    UNIQUE(volume_serial,sync_root_file_id,local_file_id),
    CHECK((origin_kind=0 AND original_evidence_id IS NOT NULL AND permission_operation_id IS NOT NULL
        AND birth_operation_id IS NULL AND birth_protection_id IS NULL) OR
        (origin_kind=1 AND original_evidence_id IS NULL AND permission_operation_id IS NULL
        AND birth_operation_id IS NOT NULL AND birth_protection_id IS NOT NULL))
);
CREATE TABLE namespace_name_epochs(
    parent_member_id TEXT NOT NULL REFERENCES namespace_members(member_id),child_name_key TEXT NOT NULL,
    epoch INTEGER NOT NULL CHECK(typeof(epoch)='integer' AND epoch>0),PRIMARY KEY(parent_member_id,child_name_key));
CREATE TABLE namespace_permission_baselines (
    evidence_id TEXT PRIMARY KEY,
    volume_serial TEXT NOT NULL,
    sync_root_file_id TEXT NOT NULL,
    local_file_id TEXT NOT NULL,
    payload TEXT NOT NULL,
    UNIQUE(volume_serial, sync_root_file_id, local_file_id)
);
CREATE TABLE namespace_permission_changes (
    operation_id TEXT PRIMARY KEY,
    evidence_id TEXT NOT NULL REFERENCES namespace_permission_baselines(evidence_id),
    phase INTEGER NOT NULL CHECK (phase IN (0,1,2,3)),
    payload TEXT NOT NULL
);
CREATE TABLE namespace_permission_local_identities (
    evidence_id TEXT PRIMARY KEY REFERENCES namespace_permission_baselines(evidence_id),
    operation_id TEXT NOT NULL UNIQUE REFERENCES namespace_permission_changes(operation_id),
    item_id TEXT NOT NULL UNIQUE,
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL
);
CREATE TABLE namespace_permission_tree_cancellations (
    manifest_id TEXT PRIMARY KEY REFERENCES namespace_permission_trees(manifest_id),
    payload TEXT NOT NULL
);
CREATE TABLE namespace_permission_tree_members (
    manifest_id TEXT NOT NULL REFERENCES namespace_permission_trees(manifest_id),
    sequence INTEGER NOT NULL CHECK (sequence >= 0),
    evidence_id TEXT NOT NULL,
    operation_id TEXT NOT NULL UNIQUE,
    volume_serial TEXT NOT NULL,
    sync_root_file_id TEXT NOT NULL,
    local_file_id TEXT NOT NULL,
    relative_path TEXT NOT NULL,
    relative_path_key TEXT NOT NULL,
    parent_evidence_id TEXT NULL,
    payload TEXT NOT NULL,
    fingerprint BLOB NOT NULL CHECK (length(fingerprint) = 32),
    PRIMARY KEY(manifest_id, sequence),
    UNIQUE(manifest_id, evidence_id),
    UNIQUE(manifest_id, volume_serial, sync_root_file_id, local_file_id),
    UNIQUE(manifest_id, relative_path_key),
    FOREIGN KEY(manifest_id, parent_evidence_id)
        REFERENCES namespace_permission_tree_members(manifest_id, evidence_id)
);
CREATE TABLE namespace_permission_trees (
    manifest_id TEXT PRIMARY KEY,
    root_id TEXT NOT NULL,
    volume_serial TEXT NOT NULL,
    sync_root_file_id TEXT NOT NULL,
    payload TEXT NOT NULL,
    phase INTEGER NOT NULL CHECK (phase IN (0,1)),
    captured_count INTEGER NOT NULL CHECK (captured_count >= 0),
    seal TEXT NULL
);
CREATE TABLE namespace_remote_birth_entries (
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_birth_intents(operation_id),
    instance_id TEXT NOT NULL, batch_id TEXT NOT NULL,
    batch_fingerprint TEXT NOT NULL CHECK(length(batch_fingerprint)=64),
    entry_count INTEGER NOT NULL CHECK(entry_count>0),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0 AND ordinal<entry_count), change_id TEXT NOT NULL,
    item_id TEXT NOT NULL UNIQUE,
    UNIQUE(instance_id,batch_id,ordinal), UNIQUE(instance_id,batch_id,change_id)
);
CREATE TABLE namespace_remote_file_move_protocols(
    operation_id TEXT PRIMARY KEY REFERENCES namespace_member_moves(operation_id),
    payload BLOB NOT NULL, fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE namespace_remote_move_entries (
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_moves(operation_id),
    instance_id TEXT NOT NULL,
    batch_id TEXT NOT NULL,
    batch_fingerprint TEXT NOT NULL CHECK(length(batch_fingerprint)=64),
    entry_count INTEGER NOT NULL CHECK(entry_count>0),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0 AND ordinal<entry_count),
    change_id TEXT NOT NULL,
    UNIQUE(instance_id,batch_id,ordinal),
    UNIQUE(instance_id,batch_id,change_id)
);
CREATE TABLE namespace_remote_move_rejections (
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_move_starts(operation_id),
    conflict_id TEXT NOT NULL UNIQUE,
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
);
CREATE TABLE namespace_remote_retirement_entries(
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_retirements(operation_id),
    instance_id TEXT NOT NULL,batch_id TEXT NOT NULL,batch_fingerprint TEXT NOT NULL CHECK(length(batch_fingerprint)=64),
    entry_count INTEGER NOT NULL CHECK(entry_count>0),ordinal INTEGER NOT NULL CHECK(ordinal>=0 AND ordinal<entry_count),change_id TEXT NOT NULL,
    UNIQUE(instance_id,batch_id,ordinal),UNIQUE(instance_id,batch_id,change_id));
CREATE TABLE namespace_remote_retirement_outcomes(
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_remote_retirement_entries(operation_id),payload BLOB NOT NULL,fingerprint BLOB NOT NULL);
CREATE TABLE namespace_remote_retirement_tree_entries(
    operation_id TEXT NOT NULL REFERENCES namespace_remote_retirement_tree_owners(operation_id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
    payload BLOB NOT NULL,fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),PRIMARY KEY(operation_id,ordinal),UNIQUE(operation_id,member_id));
CREATE TABLE namespace_remote_retirement_tree_member_settlements(
    operation_id TEXT NOT NULL REFERENCES namespace_remote_retirement_tree_settlements(operation_id),
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
    native_missing INTEGER NOT NULL CHECK(native_missing IN (0,1)),payload BLOB NOT NULL,fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
    PRIMARY KEY(operation_id,ordinal),UNIQUE(operation_id,member_id));
CREATE TABLE namespace_remote_retirement_tree_outcomes(
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_remote_retirement_tree_settlements(operation_id),
    payload BLOB NOT NULL,fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE namespace_remote_retirement_tree_owners(
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_retirement_batches(operation_id),
    instance_id TEXT NOT NULL,batch_id TEXT NOT NULL,batch_fingerprint TEXT NOT NULL CHECK(length(batch_fingerprint)=64),
    entry_count INTEGER NOT NULL CHECK(entry_count>0),ordinal INTEGER NOT NULL CHECK(ordinal>=0 AND ordinal<entry_count),change_id TEXT NOT NULL,
    tree_count INTEGER NOT NULL CHECK(tree_count>0 AND tree_count<=65536),tree_fingerprint TEXT NOT NULL CHECK(length(tree_fingerprint)=64),
    UNIQUE(instance_id,batch_id,ordinal),UNIQUE(instance_id,batch_id,change_id));
CREATE TABLE namespace_remote_retirement_tree_settlements(
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_remote_retirement_tree_starts(operation_id),
    next_ordinal INTEGER NOT NULL CHECK(next_ordinal>=-1),payload BLOB NOT NULL,fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE namespace_remote_retirement_tree_starts(
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_remote_retirement_tree_owners(operation_id),
    payload BLOB NOT NULL,fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE namespace_remote_update_completions(operation_id TEXT PRIMARY KEY REFERENCES namespace_remote_updates(operation_id),
    owner_fingerprint BLOB NOT NULL CHECK(length(owner_fingerprint)=32));
CREATE TABLE namespace_remote_update_pending(root_id TEXT PRIMARY KEY,
    operation_id TEXT NOT NULL UNIQUE REFERENCES namespace_remote_updates(operation_id));
CREATE TABLE namespace_remote_updates(
    operation_id TEXT PRIMARY KEY,instance_id TEXT NOT NULL,batch_id TEXT NOT NULL,batch_fingerprint TEXT NOT NULL CHECK(length(batch_fingerprint)=64),
    entry_count INTEGER NOT NULL CHECK(entry_count BETWEEN 1 AND 65536),ordinal INTEGER NOT NULL CHECK(ordinal>=0 AND ordinal<entry_count),
    change_id TEXT NOT NULL,root_id TEXT NOT NULL,member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
    finished INTEGER NOT NULL CHECK(finished IN (0,1)),payload BLOB NOT NULL,fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
    UNIQUE(instance_id,batch_id,ordinal),UNIQUE(instance_id,batch_id,change_id));
CREATE TABLE namespace_replacement_cancellations (
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_replacements(operation_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
);
CREATE TABLE namespace_replacement_completions (
    operation_id TEXT PRIMARY KEY REFERENCES namespace_replacements(operation_id),
    member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
    restoration_id TEXT NOT NULL REFERENCES namespace_replacement_restoration_results(restoration_id),
    protection_id TEXT NOT NULL UNIQUE,
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
);
CREATE TABLE namespace_replacement_content_completions (
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_replacements(operation_id),
    action_id TEXT NOT NULL,
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
);
CREATE TABLE namespace_replacement_execution_facts (
    operation_id TEXT NOT NULL REFERENCES namespace_replacements(operation_id),
    kind INTEGER NOT NULL CHECK(kind BETWEEN 1 AND 3),
    action_id TEXT NOT NULL,
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
    PRIMARY KEY(operation_id,kind),
    UNIQUE(action_id,kind)
);
CREATE TABLE namespace_replacement_pending (
    root_id TEXT NOT NULL PRIMARY KEY,
    member_id TEXT NOT NULL UNIQUE REFERENCES namespace_members(member_id),
    operation_id TEXT NOT NULL UNIQUE REFERENCES namespace_replacements(operation_id)
);
CREATE TABLE namespace_replacement_restoration_results (
    restoration_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_replacement_restorations(restoration_id),
    operation_id TEXT NOT NULL REFERENCES namespace_replacements(operation_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
);
CREATE TABLE namespace_replacement_restorations (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    restoration_id TEXT NOT NULL UNIQUE,
    operation_id TEXT NOT NULL REFERENCES namespace_replacements(operation_id),
    previous_id TEXT NULL REFERENCES namespace_replacement_restorations(restoration_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
);
CREATE TABLE namespace_replacement_role_actions (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    recovery_id TEXT NOT NULL UNIQUE,
    operation_id TEXT NOT NULL REFERENCES namespace_replacements(operation_id),
    previous_id TEXT NULL REFERENCES namespace_replacement_role_actions(recovery_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
);
CREATE TABLE namespace_replacement_role_handoffs (
    recovery_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_replacement_role_actions(recovery_id),
    operation_id TEXT NOT NULL REFERENCES namespace_replacements(operation_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
);
CREATE TABLE namespace_replacement_starts (
    operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_replacements(operation_id),
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
);
CREATE TABLE namespace_replacements (
    operation_id TEXT NOT NULL PRIMARY KEY,
    member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
    root_id TEXT NOT NULL,
    payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
);
CREATE TABLE namespace_transfer_effects(effect_id TEXT PRIMARY KEY,
    operation_id TEXT NOT NULL REFERENCES namespace_transfers(operation_id),kind INTEGER NOT NULL CHECK(kind IN (0,1)),UNIQUE(operation_id,kind));
CREATE TABLE namespace_transfer_journal_members(
    operation_id TEXT PRIMARY KEY,transfer_id TEXT NOT NULL REFERENCES namespace_transfers(operation_id),
    sequence INTEGER NOT NULL CHECK(sequence>0),UNIQUE(transfer_id,sequence));
CREATE TABLE namespace_transfer_roots(root_id TEXT PRIMARY KEY,
    operation_id TEXT NOT NULL REFERENCES namespace_transfers(operation_id));
CREATE TABLE namespace_transfer_states(
    operation_id TEXT PRIMARY KEY REFERENCES namespace_transfers(operation_id),
    completed INTEGER NOT NULL CHECK(completed IN (0,1)),payload_fingerprint BLOB NOT NULL CHECK(length(payload_fingerprint)=32));
CREATE TABLE namespace_transfer_target_births(
    transfer_id TEXT NOT NULL UNIQUE REFERENCES namespace_transfers(operation_id),birth_id TEXT PRIMARY KEY,
    item_id TEXT NOT NULL UNIQUE,payload BLOB NOT NULL,fingerprint BLOB NOT NULL);
CREATE TABLE namespace_transfers(
    operation_id TEXT PRIMARY KEY,source_root_id TEXT NOT NULL,target_root_id TEXT NOT NULL CHECK(target_root_id<>source_root_id),
    copy_id TEXT NOT NULL UNIQUE,delete_id TEXT NOT NULL UNIQUE,content_id TEXT NOT NULL UNIQUE,
    payload BLOB NOT NULL,fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE notification_snoozes (
    conflict_id TEXT PRIMARY KEY,
    snoozed_utc TEXT NOT NULL
);
CREATE TABLE pending_remote_batches (
    instance_id TEXT PRIMARY KEY,
    batch_id TEXT NOT NULL,
    payload BLOB NOT NULL,
    candidate_snapshot BLOB NOT NULL
);
CREATE TABLE remote_conflict_actions(command_id TEXT PRIMARY KEY REFERENCES user_commands(command_id),
    payload BLOB NOT NULL,fingerprint BLOB NOT NULL,outcome_payload BLOB NULL,outcome_fingerprint BLOB NULL,
    CHECK((outcome_payload IS NULL)=(outcome_fingerprint IS NULL)));
CREATE TABLE remote_conflict_projections (
    conflict_id TEXT PRIMARY KEY,
    instance_id TEXT NOT NULL,
    change_id TEXT NOT NULL,
    relative_path TEXT NOT NULL,
    reason INTEGER NOT NULL,
    version_comparison INTEGER NOT NULL,
    local_revision TEXT NULL,
    remote_revision TEXT NULL,
    detected_utc TEXT NOT NULL
);
CREATE TABLE remote_delete_resolution_members(command_id TEXT NOT NULL REFERENCES remote_delete_resolutions(command_id),
    ordinal INTEGER NOT NULL,member_id TEXT NOT NULL,payload BLOB NOT NULL,fingerprint BLOB NOT NULL,
    PRIMARY KEY(command_id,ordinal),UNIQUE(command_id,member_id));
CREATE TABLE remote_delete_resolutions(command_id TEXT PRIMARY KEY REFERENCES user_commands(command_id),
    conflict_id TEXT NOT NULL UNIQUE,instance_id TEXT NOT NULL,root_id TEXT NOT NULL,
    payload BLOB NOT NULL,fingerprint BLOB NOT NULL,completed INTEGER NOT NULL CHECK(completed IN (0,1)),
    FOREIGN KEY(command_id) REFERENCES namespace_member_retirement_batches(operation_id));
CREATE TABLE remote_identity_generation_history(
    generation TEXT PRIMARY KEY,root_id TEXT NOT NULL,base_item_id TEXT NOT NULL,item_id TEXT NOT NULL UNIQUE,
    retirement_id TEXT NOT NULL UNIQUE REFERENCES namespace_member_retirements(operation_id),
    payload BLOB NOT NULL CHECK(length(payload)<=131072),fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE remote_identity_generations(root_id TEXT NOT NULL,base_item_id TEXT NOT NULL,
    generation TEXT NOT NULL UNIQUE REFERENCES remote_identity_generation_history(generation),PRIMARY KEY(root_id,base_item_id));
CREATE TABLE retained_remote_batches (
    instance_id TEXT PRIMARY KEY, batch_id TEXT NOT NULL, payload BLOB NOT NULL, fingerprint BLOB NOT NULL
);
CREATE TABLE retired_transfer_observations(
    operation_id TEXT PRIMARY KEY,transfer_id TEXT NOT NULL REFERENCES namespace_transfers(operation_id),
    retirement_id TEXT NOT NULL REFERENCES namespace_member_retirements(operation_id),source_root_id TEXT NOT NULL,
    started_utc TEXT NOT NULL,status INTEGER NOT NULL CHECK(status IN (0,1)),payload BLOB NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE root_label_journal_acknowledgements(
    operation_id TEXT PRIMARY KEY,root_id TEXT NOT NULL,
    member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
    label_operation_id TEXT NOT NULL REFERENCES managed_root_rename_history(operation_id),
    started_utc TEXT NOT NULL,status INTEGER NOT NULL CHECK(status IN (0,1)),
    payload BLOB NOT NULL CHECK(length(payload)<=262144),fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE root_label_unsealed_cancellations(
    operation_id TEXT PRIMARY KEY REFERENCES managed_root_rename_history(operation_id),
    root_id TEXT NOT NULL,payload BLOB NOT NULL CHECK(length(payload)<=16777216),
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE root_label_unstarted_cancellations(
    operation_id TEXT PRIMARY KEY REFERENCES namespace_member_moves(operation_id),
    root_id TEXT NOT NULL,payload BLOB NOT NULL CHECK(length(payload)<=16777216),
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE root_pending_remote_batches (
    instance_id TEXT NOT NULL, root_id TEXT NOT NULL, batch_id TEXT NOT NULL,
    payload BLOB NOT NULL, candidate_snapshot BLOB NOT NULL,
    PRIMARY KEY(instance_id,root_id), UNIQUE(batch_id)
);
CREATE TABLE root_rescan_generations (
    root_id TEXT PRIMARY KEY REFERENCES deferred_rescan_roots(root_id) ON DELETE CASCADE,
    generation TEXT NOT NULL UNIQUE,
    requested_utc TEXT NOT NULL
);
CREATE TABLE upload_conflicts (
    conflict_id TEXT PRIMARY KEY,
    instance_id TEXT NOT NULL,
    change_id TEXT NOT NULL,
    relative_path TEXT NOT NULL,
    reason INTEGER NOT NULL,
    version_comparison INTEGER NOT NULL,
    local_revision TEXT NULL,
    remote_revision TEXT NULL,
    detected_utc TEXT NOT NULL,
    status INTEGER NOT NULL
);
CREATE TABLE user_commands (
    command_id TEXT PRIMARY KEY,
    action TEXT NOT NULL,
    target_id TEXT NOT NULL,
    state TEXT NOT NULL
);
CREATE TABLE worker_requests (
    operation_id TEXT PRIMARY KEY,
    instance_id TEXT NOT NULL,
    fingerprint BLOB NOT NULL,
    stable_fingerprint BLOB NULL,
    attempt INTEGER NOT NULL DEFAULT 0,
    next_attempt_utc TEXT NULL
);
CREATE INDEX coalesced_conflict_remote_observations_effect ON coalesced_conflict_remote_observations(rejected_operation_id,observation_id);
CREATE INDEX conflict_deletion_retry_current ON conflict_deletion_retries(decision_id,sequence DESC);
CREATE INDEX conflict_deletions_pending ON conflict_deletions(phase,command_id);
CREATE INDEX conflict_keep_both_members_command ON conflict_keep_both_members(command_id,original_id);
CREATE INDEX conflict_keep_remote_members_command ON conflict_keep_remote_members(command_id,ordinal);
CREATE INDEX conflict_local_removals_pending ON conflict_local_removals(phase,command_id);
CREATE UNIQUE INDEX ix_journal_coalescing_followup ON journal_coalescing_retries(followup_operation_id) WHERE followup_operation_id IS NOT NULL;
CREATE INDEX ix_journal_coalescing_retries_step
    ON journal_coalescing_retries(plan_id, ordinal, sequence);
CREATE INDEX ix_keep_remote_file_start_root ON conflict_keep_remote_file_starts(root_id);
CREATE INDEX ix_namespace_identity_member ON namespace_member_identity_acceptances(member_id);
CREATE INDEX ix_namespace_member_retirement_batches_root
    ON namespace_member_retirement_batches(root_id, phase);
CREATE INDEX ix_namespace_move_completion_member ON namespace_member_move_completions(member_id);
CREATE INDEX ix_namespace_move_previous ON namespace_member_moves(previous_move_id);
CREATE INDEX ix_namespace_position_operation
    ON namespace_member_positions(operation_id,member_id);
CREATE INDEX ix_remote_conflicts_page ON remote_conflict_projections(-(621355968000000000 + CAST(strftime('%s', substr(detected_utc,1,19) || substr(detected_utc,28)) AS INTEGER)*10000000 + CAST(substr(detected_utc,21,7) AS INTEGER)),conflict_id COLLATE BINARY);
CREATE INDEX ix_upload_conflicts_operation_epoch
    ON upload_conflicts(change_id, instance_id, conflict_id);
CREATE INDEX ix_upload_conflicts_original_status
    ON upload_conflicts(change_id, status);
CREATE INDEX ix_upload_conflicts_page ON upload_conflicts(status,-(621355968000000000 + CAST(strftime('%s', substr(detected_utc,1,19) || substr(detected_utc,28)) AS INTEGER)*10000000 + CAST(substr(detected_utc,21,7) AS INTEGER)),conflict_id COLLATE BINARY);
CREATE INDEX namespace_birth_latest_protection ON namespace_birth_protections(birth_operation_id,sequence DESC);
CREATE INDEX namespace_birth_recovery_actions_by_birth
    ON namespace_birth_recovery_actions(birth_operation_id,sequence DESC);
CREATE INDEX namespace_born_member_recovery_member ON namespace_born_member_recovery_actions(member_id,sequence);
CREATE INDEX namespace_born_member_recovery_root ON namespace_born_member_recovery_pending(root_id);
CREATE INDEX namespace_controlled_retirement_items ON namespace_member_retirements(
    json_extract(CAST(payload AS TEXT),'$.StartedItemId'),operation_id)
WHERE json_type(CAST(payload AS TEXT),'$.Intent.RemoteEntry') IS NULL
    AND json_type(CAST(payload AS TEXT),'$.Intent.TransferEntry') IS NULL
    AND json_extract(CAST(payload AS TEXT),'$.StartedItemId') IS NOT NULL;
CREATE INDEX namespace_initialization_owner ON namespace_initialization_roots(operation_id);
CREATE UNIQUE INDEX namespace_permission_pending_object
    ON namespace_permission_changes(evidence_id) WHERE phase IN (0,1,3);
CREATE INDEX namespace_permission_tree_capture_scope
    ON namespace_permission_trees(volume_serial, sync_root_file_id, root_id) WHERE phase=0;
CREATE INDEX namespace_remote_retirement_items ON namespace_member_retirements(
    json_extract(CAST(payload AS TEXT),'$.Intent.RemoteEntry.ItemId'),operation_id)
WHERE json_type(CAST(payload AS TEXT),'$.Intent.RemoteEntry') IS NOT NULL;
CREATE UNIQUE INDEX namespace_remote_tree_removed_member ON namespace_remote_retirement_tree_member_settlements(member_id) WHERE native_missing=1;
CREATE INDEX namespace_remote_updates_pending ON namespace_remote_updates(finished,root_id,operation_id);
CREATE INDEX namespace_replacement_restorations_owner ON namespace_replacement_restorations(operation_id,sequence);
CREATE INDEX namespace_replacement_role_actions_owner ON namespace_replacement_role_actions(operation_id,sequence);
CREATE INDEX namespace_replacements_root ON namespace_replacements(root_id);
CREATE INDEX namespace_retirement_batches_root_operation ON namespace_member_retirement_batches(root_id,operation_id);
CREATE INDEX namespace_transfer_journal_owners ON namespace_transfer_journal_members(transfer_id,sequence);
CREATE INDEX namespace_transfer_pending ON namespace_transfer_states(completed,operation_id);
CREATE INDEX namespace_transfer_pending_source_root ON namespace_transfers(source_root_id,operation_id) WHERE json_extract(CAST(payload AS TEXT),'$.Acknowledgement.AcknowledgedAt') IS NULL;
CREATE INDEX namespace_transfer_pending_target_root ON namespace_transfers(target_root_id,operation_id) WHERE json_extract(CAST(payload AS TEXT),'$.Acknowledgement.AcknowledgedAt') IS NULL;
CREATE INDEX namespace_transfer_root_owners ON namespace_transfer_roots(operation_id);
CREATE INDEX namespace_transfer_source_items ON namespace_member_retirements(
    json_extract(CAST(payload AS TEXT),'$.StartedItemId'),operation_id)
WHERE json_type(CAST(payload AS TEXT),'$.Intent.TransferEntry') IS NOT NULL;
CREATE INDEX remote_delete_resolutions_pending ON remote_delete_resolutions(instance_id,completed,command_id);
CREATE INDEX remote_delete_resolutions_root ON remote_delete_resolutions(root_id,command_id);
CREATE INDEX retired_transfer_observation_pending
    ON retired_transfer_observations(status,source_root_id,started_utc,operation_id);
CREATE INDEX root_label_journal_pending_payload ON root_label_journal_acknowledgements(operation_id,root_id) WHERE (json_type(CAST(payload AS TEXT),'$.AcknowledgedAt') IS NULL OR json_type(CAST(payload AS TEXT),'$.AcknowledgedAt')='null');
CREATE INDEX root_label_journal_pending_payload_root ON root_label_journal_acknowledgements(root_id,operation_id) WHERE (json_type(CAST(payload AS TEXT),'$.AcknowledgedAt') IS NULL OR json_type(CAST(payload AS TEXT),'$.AcknowledgedAt')='null');
CREATE UNIQUE INDEX root_label_journal_pending_root ON root_label_journal_acknowledgements(root_id) WHERE status=0;
CREATE INDEX root_label_journal_pending_status ON root_label_journal_acknowledgements(operation_id,root_id) WHERE status=0;
CREATE INDEX root_label_unsealed_cancellations_root ON root_label_unsealed_cancellations(root_id,operation_id);
CREATE INDEX root_label_unstarted_cancellations_root ON root_label_unstarted_cancellations(root_id,operation_id);
CREATE UNIQUE INDEX ux_namespace_identity_first ON namespace_member_identity_acceptances(member_id)
    WHERE previous_operation_id IS NULL;
CREATE UNIQUE INDEX ux_namespace_identity_previous ON namespace_member_identity_acceptances(previous_operation_id)
    WHERE previous_operation_id IS NOT NULL;
CREATE UNIQUE INDEX ux_namespace_position_previous
    ON namespace_member_positions(member_id,previous_operation_id)
    WHERE previous_operation_id IS NOT NULL;
CREATE TRIGGER namespace_name_epoch_delete AFTER DELETE ON namespace_member_names BEGIN
    INSERT INTO namespace_name_epochs(parent_member_id,child_name_key,epoch) VALUES(OLD.parent_member_id,OLD.child_name_key,1)
    ON CONFLICT(parent_member_id,child_name_key) DO UPDATE SET epoch=epoch+1;
END;
CREATE TRIGGER namespace_name_epoch_insert AFTER INSERT ON namespace_member_names BEGIN
    INSERT INTO namespace_name_epochs(parent_member_id,child_name_key,epoch) VALUES(NEW.parent_member_id,NEW.child_name_key,1)
    ON CONFLICT(parent_member_id,child_name_key) DO UPDATE SET epoch=epoch+1;
END;
CREATE TRIGGER namespace_name_epoch_update AFTER UPDATE ON namespace_member_names BEGIN
    INSERT INTO namespace_name_epochs(parent_member_id,child_name_key,epoch) VALUES(OLD.parent_member_id,OLD.child_name_key,1)
    ON CONFLICT(parent_member_id,child_name_key) DO UPDATE SET epoch=epoch+1;
    INSERT INTO namespace_name_epochs(parent_member_id,child_name_key,epoch) VALUES(NEW.parent_member_id,NEW.child_name_key,1)
    ON CONFLICT(parent_member_id,child_name_key) DO UPDATE SET epoch=epoch+1;
END;
CREATE TABLE conflict_keep_remote_file_actions(
    command_id TEXT NOT NULL PRIMARY KEY REFERENCES conflict_keep_remote_file_starts(command_id),
    action_id TEXT NOT NULL UNIQUE,
    payload BLOB NOT NULL CHECK(length(payload)<=262144),
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
CREATE TABLE conflict_keep_remote_file_action_owners(
    command_id TEXT NOT NULL PRIMARY KEY REFERENCES conflict_keep_remote_file_starts(command_id),
    action_id TEXT NOT NULL UNIQUE REFERENCES conflict_keep_remote_file_actions(action_id));
CREATE TABLE conflict_keep_remote_content_facts(
    command_id TEXT NOT NULL REFERENCES conflict_keep_remote_file_actions(command_id),
    kind INTEGER NOT NULL CHECK(kind IN (1,2)),
    action_id TEXT NOT NULL REFERENCES conflict_keep_remote_file_actions(action_id),
    payload BLOB NOT NULL CHECK(length(payload)<=262144),
    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
    PRIMARY KEY(command_id,kind));
CREATE TABLE conflict_keep_remote_content_owners(
    command_id TEXT NOT NULL REFERENCES conflict_keep_remote_file_actions(command_id),
    kind INTEGER NOT NULL CHECK(kind IN (1,2)),
    action_id TEXT NOT NULL REFERENCES conflict_keep_remote_file_actions(action_id),
    PRIMARY KEY(command_id,kind));
PRAGMA user_version=98;
