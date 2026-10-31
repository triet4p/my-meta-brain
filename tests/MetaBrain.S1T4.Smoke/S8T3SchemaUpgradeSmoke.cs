using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MetaBrain.Connections;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S8-T3 runtime proof for the versioned vault schema upgrade, its encrypted
/// rollback point, interruption recovery and key/backup compatibility.
/// <para>
/// The scenario starts the real service on vaults that were written by the
/// pinned historical builds (see scripts/s8-t3/prepare-old-vaults.ps1): one
/// holds canonical lifecycle v1 and model job v1 records, the other an owner
/// scope-grant state v3. It proves the upgrade only while the vault is locked,
/// that a wrong or missing credential writes nothing, that an interruption
/// leaves every record readable and resumable, that IDs, revisions, zones,
/// relations, attribution and lifecycle survive, that the private index
/// rebuilds, that the migrated vault is written, locked, restarted and read
/// through the owner channel and the MCP bridge, that a pre-upgrade encrypted
/// backup still restores, and that rollback reverts only the files the upgrade
/// wrote.
/// </para>
/// <para>
/// Everything is synthetic and stays inside the repository's ignored runtime
/// directory. The scenario never changes a permission, an owner, a registry
/// entry or a machine setting.
/// </para>
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S8T3Passphrase = "S8T3_SYNTHETIC_OLD_VAULT_PASSPHRASE_4f1c";
    private const string S8T3WrongPassphrase = "S8T3_WRONG_PASSPHRASE_9a2d";
    private const string S8T3Zone = "s8t3-owner-zone";
    private const string S8T3ItemA = "s8t3-item-a";
    private const string S8T3ItemB = "s8t3-item-b";
    private const string S8T3ItemC = "s8t3-item-c";
    private const string S8T3ControlItem = "s8t3-control-item";
    private const string S8T3BodyA = "S8T3_SYNTHETIC_PRIOR_ITEM_A_BODY_7c31";
    private const string S8T3Relation = "rel-s8t3-a-b";

    internal static async Task RunS8T3SchemaUpgradeAsync(string connectionsExecutable, string artifactRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S8-T3 migration smoke requires Windows named pipes.");
        }

        Console.WriteLine("RUN S8-T3 versioned schema upgrade, interruption, rollback and old-backup compatibility");
        var fullArtifacts = Path.GetFullPath(artifactRoot);
        Require(Directory.Exists(Path.Combine(fullArtifacts, "canonical-v1")),
            "The S8-T3 canonical-v1 artifact is missing. Run scripts/s8-t3/prepare-old-vaults.ps1 first; this smoke never fabricates the prior dataset.");
        Require(Directory.Exists(Path.Combine(fullArtifacts, "control-v3")),
            "The S8-T3 control-v3 artifact is missing. Run scripts/s8-t3/prepare-old-vaults.ps1 first.");

        using var fixture = S8T3Fixture.Create(fullArtifacts);
        RunningService? canonicalService = null;
        RunningService? controlService = null;
        RunningService? backupService = null;
        RunningService? restoredService = null;
        try
        {
            // ----- canonical-v1: key negatives, interruption, resume -----
            canonicalService = await StartServiceAsync(connectionsExecutable, fixture.CanonicalSettingsPath).ConfigureAwait(false);
            var initialStatus = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "status", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(initialStatus.ExitCode == 0 && initialStatus.StandardOutput.Contains("vault=locked", StringComparison.Ordinal),
                "The old S8-T3 vault did not start locked (" + initialStatus.StandardOutput.Trim() + ").");
            Require(initialStatus.StandardOutput.Contains("format=unknown", StringComparison.Ordinal),
                "The historical vault reported a format record it cannot have (" + initialStatus.StandardOutput.Trim() + ").");

            var headerBefore = HashFile(Path.Combine(fixture.CanonicalVaultPath, "vault-header.json"));
            var fingerprintBefore = VaultFingerprint(fixture.CanonicalVaultPath);
            var wrong = await RunOwnerCommandWithInputAsync(connectionsExecutable, S8T3WrongPassphrase + Environment.NewLine,
                "owner", "upgrade", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(wrong.ExitCode != 0 && wrong.StandardError.Contains("vault_denied", StringComparison.Ordinal),
                "A wrong passphrase ran the S8-T3 upgrade (" + wrong.StandardError.Trim() + ").");
            var missing = await RunOwnerCommandWithInputAsync(connectionsExecutable, string.Empty,
                "owner", "upgrade", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(missing.ExitCode != 0 && missing.StandardError.Contains("credential_required", StringComparison.Ordinal),
                "A missing credential ran the S8-T3 upgrade (" + missing.StandardError.Trim() + ").");
            Require(string.Equals(fingerprintBefore, VaultFingerprint(fixture.CanonicalVaultPath), StringComparison.Ordinal),
                "A refused S8-T3 upgrade changed a vault file.");
            Require(!Directory.Exists(Path.Combine(fixture.CanonicalVaultPath, "rollback")),
                "A refused S8-T3 upgrade created a rollback point.");
            Console.WriteLine("OBSERVATION wrong/missing key denied the upgrade; every vault byte and the rollback area stayed unchanged");

            // An interruption at a material boundary: the engine rewrites one
            // file and is killed before the next. The on-disk state must be
            // readable and resumable, not lost and not half-committed.
            byte[]? interruptedKey = null;
            try
            {
                var store = new EncryptedVaultStore(fixture.CanonicalVaultPath);
                Require(store.TryUnlock(S8T3Passphrase, useRecoveryCode: false, out interruptedKey, out var manifest) &&
                        interruptedKey is not null && manifest is not null,
                    "The S8-T3 smoke could not open the historical vault for the interruption test.");
                var engine = new VaultSchemaUpgradeEngine(store)
                {
                    OnFileMigrated = index =>
                    {
                        if (index == 0)
                        {
                            throw new InvalidOperationException("S8-T3 injected interruption");
                        }
                    }
                };
                var interrupted = false;
                try
                {
                    engine.Upgrade(manifest!, interruptedKey!);
                }
                catch (InvalidOperationException ex) when (string.Equals(ex.Message, "S8-T3 injected interruption", StringComparison.Ordinal))
                {
                    interrupted = true;
                }

                Require(interrupted, "The S8-T3 interruption point was never reached.");
            }
            finally
            {
                if (interruptedKey is not null)
                {
                    CryptographicOperations.ZeroMemory(interruptedKey);
                }
            }

            var interruptedStatus = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "status", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(interruptedStatus.ExitCode == 0 &&
                    interruptedStatus.StandardOutput.Contains("format=interrupted", StringComparison.Ordinal),
                "An interrupted S8-T3 upgrade was not reported as interrupted (" + interruptedStatus.StandardOutput.Trim() + ").");
            Require(Directory.Exists(Path.Combine(fixture.CanonicalVaultPath, "rollback")),
                "An interrupted S8-T3 upgrade lost its rollback point.");
            RequireNoPlaintext(fixture.CanonicalVaultPath, S8T3BodyA, "the interrupted rollback point");
            Console.WriteLine("OBSERVATION the upgrade was interrupted after one rewritten file; the vault still opens, the journal reports interrupted and the rollback point holds ciphertext only");

            var resumed = await RunOwnerCommandWithInputAsync(connectionsExecutable, S8T3Passphrase + Environment.NewLine,
                "owner", "upgrade", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(resumed.ExitCode == 0, "The resumed S8-T3 upgrade failed (" + resumed.StandardError.Trim() + ").");
            Require(resumed.StandardOutput.Contains("UPGRADE complete", StringComparison.Ordinal) &&
                    resumed.StandardOutput.Contains("from=control:4,job:1(1),lifecycle:1(1)", StringComparison.Ordinal) &&
                    resumed.StandardOutput.Contains("to=control:4,job:2(1),lifecycle:3(1)", StringComparison.Ordinal) &&
                    resumed.StandardOutput.Contains("resumed=true", StringComparison.Ordinal),
                "The resumed S8-T3 upgrade did not report the genuine old profile and the migrated one (" +
                resumed.StandardOutput.Trim() + ").");
            Require(string.Equals(headerBefore, HashFile(Path.Combine(fixture.CanonicalVaultPath, "vault-header.json")), StringComparison.Ordinal),
                "The S8-T3 upgrade changed the key envelope, which would break old backups.");
            RequireNoPlaintext(fixture.CanonicalVaultPath, S8T3BodyA, "the migrated vault and its rollback point");
            RequireNoTemporaryFiles(fixture.CanonicalVaultPath);
            Console.WriteLine("OBSERVATION the resumed upgrade migrated exactly the old canonical records, left the key envelope byte-identical and kept no plaintext or temporary file");

            var currentStatus = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "status", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(currentStatus.ExitCode == 0 &&
                    currentStatus.StandardOutput.Contains("format=current", StringComparison.Ordinal) &&
                    currentStatus.StandardOutput.Contains("rollback=available", StringComparison.Ordinal),
                "The upgraded S8-T3 vault did not report a current format with a rollback point (" +
                currentStatus.StandardOutput.Trim() + ").");

            var idempotent = await RunOwnerCommandWithInputAsync(connectionsExecutable, S8T3Passphrase + Environment.NewLine,
                "owner", "upgrade", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(idempotent.ExitCode == 0 && idempotent.StandardOutput.Contains("UPGRADE current", StringComparison.Ordinal),
                "A second upgrade of a current S8-T3 vault rewrote it (" + idempotent.StandardOutput.Trim() + ").");

            // ----- the migrated vault is read through the owner channel -----
            var unlock = await RunOwnerCommandWithInputAsync(connectionsExecutable, S8T3Passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0 && unlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The migrated S8-T3 vault did not unlock with the owner's passphrase (" + unlock.StandardError.Trim() + ").");

            var itemA = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.CanonicalControlPipe, "--resource-id", S8T3ItemA).ConfigureAwait(false);
            Require(itemA.ExitCode == 0 && itemA.StandardOutput.Contains(S8T3BodyA, StringComparison.Ordinal) &&
                    itemA.StandardOutput.Contains("revision=1", StringComparison.Ordinal),
                "The migrated S8-T3 record lost its body or revision (" + itemA.StandardError.Trim() + ").");
            Require(itemA.StandardOutput.Contains("author=user", StringComparison.Ordinal),
                "The migrated S8-T3 record lost its attribution (" + itemA.StandardOutput.Trim() + ").");

            var lifecycle = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-read", "--control-pipe", fixture.CanonicalControlPipe, "--resource-id", S8T3ItemA).ConfigureAwait(false);
            Require(lifecycle.ExitCode == 0 && lifecycle.StandardOutput.Contains("publication=", StringComparison.Ordinal),
                "The migrated S8-T3 lifecycle record was lost (" + lifecycle.StandardError.Trim() + ").");

            var relation = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "relation-read", "--control-pipe", fixture.CanonicalControlPipe, "--resource-id", S8T3Relation).ConfigureAwait(false);
            Require(relation.ExitCode == 0 &&
                    relation.StandardOutput.Contains(S8T3ItemA, StringComparison.Ordinal) &&
                    relation.StandardOutput.Contains(S8T3ItemB, StringComparison.Ordinal),
                "The prior S8-T3 relation lost an endpoint (" + relation.StandardError.Trim() + ").");

            var sourceRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-read", "--control-pipe", fixture.CanonicalControlPipe, "--resource-id", "s8t3-source-a").ConfigureAwait(false);
            Require(sourceRead.ExitCode == 0 &&
                    sourceRead.StandardOutput.Contains("S8T3_SYNTHETIC_PRIOR_SOURCE_TITLE", StringComparison.Ordinal),
                "The prior S8-T3 source record was lost (" + sourceRead.StandardError.Trim() + ").");

            var jobs = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-list", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(jobs.ExitCode == 0 && jobs.StandardOutput.Contains("job-", StringComparison.Ordinal),
                "The migrated S8-T3 job record was lost (" + jobs.StandardError.Trim() + ").");

            var rebuilt = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(rebuilt.ExitCode == 0,
                "The migrated S8-T3 vault could not rebuild its private index (exit=" + rebuilt.ExitCode +
                "; stdout=" + rebuilt.StandardOutput.Trim() + "; stderr=" + rebuilt.StandardError.Trim() + ").");
            var rebuiltQuery = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "library", "--control-pipe", fixture.CanonicalControlPipe,
                "--query", "S8T3_SYNTHETIC_PRIOR_ITEM_A_TITLE").ConfigureAwait(false);
            Require(rebuiltQuery.ExitCode == 0 &&
                    rebuiltQuery.StandardOutput.Contains(S8T3ItemA, StringComparison.Ordinal),
                "The rebuilt S8-T3 private index did not serve the migrated record for its exact title query (exit=" +
                rebuiltQuery.ExitCode + "; stdout=" + rebuiltQuery.StandardOutput.Trim() +
                "; stderr=" + rebuiltQuery.StandardError.Trim() + ").");
            Console.WriteLine("OBSERVATION the migrated vault kept item body/revision/attribution, the lifecycle record, the relation and the job, and rebuilt its private index from canonical");

            // ----- write, job and agent/MCP read on the migrated vault -----
            var revisionDraft = JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "lesson",
                author = "user",
                creationMethod = "directWrite",
                evidence = "unverified",
                level = "l1",
                title = "S8T3 upgraded item A title, revised",
                body = "S8T3_SYNTHETIC_UPGRADED_ITEM_A_BODY_51cd",
                sources = Array.Empty<object>(),
                occurredAtUtc = (DateTimeOffset?)null,
                recordedAtUtc = DateTimeOffset.UtcNow,
                applicability = (object?)null,
            });
            var revisionPath = WriteS8T3Temp(fixture.OutputDirectory, revisionDraft, "revise.json");
            var revised = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-revise", "--control-pipe", fixture.CanonicalControlPipe,
                "--resource-id", S8T3ItemA, "--zone-id", S8T3Zone,
                "--expected-revision", "1", "--input-file", revisionPath).ConfigureAwait(false);
            Require(revised.ExitCode == 0 && revised.StandardOutput.Contains("revision=2", StringComparison.Ordinal),
                "A write on the migrated S8-T3 vault was refused (" + revised.StandardError.Trim() + ").");

            var created = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-create", "--control-pipe", fixture.CanonicalControlPipe,
                "--resource-id", S8T3ItemC, "--zone-id", S8T3Zone,
                "--input-file", WriteS8T3Temp(fixture.OutputDirectory, revisionDraft, "create.json")).ConfigureAwait(false);
            Require(created.ExitCode == 0, "A create on the migrated S8-T3 vault was refused (" + created.StandardError.Trim() + ").");

            var jobCreated = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.CanonicalControlPipe,
                "--zone-id", S8T3Zone, "--destination-id", S8T3ItemA, "--destination-revision", "2",
                "--source-id", "s8t3-source-a", "--source-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash", "--max-cost-usd", "0.01").ConfigureAwait(false);
            Require(jobCreated.ExitCode == 0, "A job on the migrated S8-T3 vault was refused (" + jobCreated.StandardError.Trim() + ").");

            var handoff = Path.Combine(fixture.OutputDirectory, "s8t3.grant-handoff");
            var session = Path.Combine(fixture.OutputDirectory, "s8t3.session");
            var expiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O", CultureInfo.InvariantCulture);
            var grant = await RunOwnerCommandWithInputAsync(connectionsExecutable, "ISSUE" + Environment.NewLine,
                "owner", "grant", "--control-pipe", fixture.CanonicalControlPipe,
                "--resource-ids", S8T3ItemA, "--operations", "memory.read",
                "--expires-at-utc", expiry, "--handoff-file", handoff).ConfigureAwait(false);
            Require(grant.ExitCode == 0, "The migrated S8-T3 vault refused a scoped grant (" + grant.StandardError.Trim() + ").");
            var redeem = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "redeem", "--agent-pipe", fixture.CanonicalAgentPipe,
                "--handoff-file", handoff, "--session-file", session).ConfigureAwait(false);
            Require(redeem.ExitCode == 0 && redeem.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
                "The migrated S8-T3 vault refused the token (" + redeem.StandardError.Trim() + ").");

            var agentRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-memory-read", "--agent-pipe", fixture.CanonicalAgentPipe,
                "--session-file", session, "--resource-id", S8T3ItemA, "--resource-revision", "2").ConfigureAwait(false);
            Require(agentRead.ExitCode == 0 && agentRead.StandardOutput.Contains("S8T3_SYNTHETIC_UPGRADED_ITEM_A_BODY_51cd", StringComparison.Ordinal),
                "The agent channel could not read the migrated S8-T3 record (" + agentRead.StandardError.Trim() + ").");
            var mcpRead = await RunS8T3McpReadAsync(connectionsExecutable, fixture.CanonicalAgentPipe, session, S8T3ItemA, "2").ConfigureAwait(false);
            Require(mcpRead.Contains("S8T3_SYNTHETIC_UPGRADED_ITEM_A_BODY_51cd", StringComparison.Ordinal),
                "The MCP bridge could not read the migrated S8-T3 record (" + mcpRead + ").");
            Console.WriteLine("OBSERVATION the migrated vault accepted a revise, a create and a job, and served the item through the agent channel and the MCP bridge");

            // ----- purge, lock, restart -----
            var purgePreview = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "purge-preview", "--control-pipe", fixture.CanonicalControlPipe,
                "--resource-id", S8T3ItemB, "--resource-revision", "1",
                "--reason", "S8T3 synthetic purge after the upgrade.").ConfigureAwait(false);
            Require(purgePreview.ExitCode == 0 && purgePreview.StandardOutput.Contains("PURGE PREVIEW", StringComparison.Ordinal),
                "The S8-T3 purge preview did not run (" + purgePreview.StandardError.Trim() + ").");
            var purgeId = ParseS8T2Value(purgePreview.StandardOutput, "PURGE PREVIEW id=", ' ');
            var impacted = ParseS8T2Impacted(purgePreview.StandardOutput);
            var generation = ParseS8T2Value(purgePreview.StandardOutput, "generation=", ' ');
            var purged = await RunOwnerCommandWithInputAsync(connectionsExecutable, "PURGE" + Environment.NewLine,
                "owner", "purge-confirm", "--control-pipe", fixture.CanonicalControlPipe,
                "--purge-id", purgeId, "--resource-id", S8T3ItemB, "--resource-revision", "1",
                "--target-zone", S8T3Zone, "--target-kind", "memory-or-source",
                "--impacted", string.Join(",", impacted), "--live-generation", generation,
                "--reason", "S8T3 synthetic purge after the upgrade.").ConfigureAwait(false);
            Require(purged.ExitCode == 0, "The S8-T3 purge was refused (" + purged.StandardError.Trim() + ").");

            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The migrated S8-T3 vault did not lock (" + locked.StandardError.Trim() + ").");
            var lockedRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.CanonicalControlPipe, "--resource-id", S8T3ItemA).ConfigureAwait(false);
            Require(lockedRead.ExitCode != 0, "A locked migrated S8-T3 vault still served a record.");

            await StopServiceAsync(canonicalService).ConfigureAwait(false);
            canonicalService = await StartServiceAsync(connectionsExecutable, fixture.CanonicalSettingsPath).ConfigureAwait(false);
            var restartedStatus = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "status", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(restartedStatus.ExitCode == 0 && restartedStatus.StandardOutput.Contains("vault=locked", StringComparison.Ordinal),
                "The restarted S8-T3 service did not start locked (" + restartedStatus.StandardOutput.Trim() + ").");
            var restartedUnlock = await RunOwnerCommandWithInputAsync(connectionsExecutable, S8T3Passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(restartedUnlock.ExitCode == 0, "The restarted S8-T3 vault did not unlock (" + restartedUnlock.StandardError.Trim() + ").");
            var afterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.CanonicalControlPipe, "--resource-id", S8T3ItemA).ConfigureAwait(false);
            Require(afterRestart.ExitCode == 0 && afterRestart.StandardOutput.Contains("revision=2", StringComparison.Ordinal),
                "The restarted S8-T3 vault lost the write made after the upgrade (" + afterRestart.StandardError.Trim() + ").");
            var purgedRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.CanonicalControlPipe, "--resource-id", S8T3ItemB).ConfigureAwait(false);
            Require(purgedRead.ExitCode != 0, "The restarted S8-T3 vault resurrected a purged record.");
            Console.WriteLine("OBSERVATION purge, lock and a full service restart kept the migrated state: the write survived, the purged record stayed gone and the vault restarted locked");

            // ----- rollback, re-upgrade, discard -----
            // The maintenance acts run only while the vault is locked.
            var lockBeforeRollback = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(lockBeforeRollback.ExitCode == 0, "The S8-T3 vault did not lock before the rollback.");
            var rollback = await RunOwnerCommandWithInputAsync(connectionsExecutable, S8T3Passphrase + Environment.NewLine,
                "owner", "rollback", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(rollback.ExitCode == 0 && rollback.StandardOutput.Contains("ROLLBACK complete", StringComparison.Ordinal) &&
                    rollback.StandardOutput.Contains("restored=2", StringComparison.Ordinal) &&
                    rollback.StandardOutput.Contains("skipped=0", StringComparison.Ordinal) &&
                    rollback.StandardOutput.Contains("format=control:4", StringComparison.Ordinal) &&
                    rollback.StandardOutput.Contains("job:1(1)", StringComparison.Ordinal) &&
                    rollback.StandardOutput.Contains("lifecycle:1(1)", StringComparison.Ordinal),
                "The S8-T3 rollback did not restore the previous stored profile (" + rollback.StandardError.Trim() + " " +
                rollback.StandardOutput.Trim() + ").");
            Require(rollback.StandardOutput.Contains("job:2(1)", StringComparison.Ordinal),
                "The S8-T3 rollback did not keep the job the owner created after the upgrade (" +
                rollback.StandardOutput.Trim() + ").");
            var rolledBack = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "status", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(rolledBack.StandardOutput.Contains("format=upgrade_available", StringComparison.Ordinal) &&
                    rolledBack.StandardOutput.Contains("rollback=none", StringComparison.Ordinal),
                "The rolled-back S8-T3 vault did not report the old profile and a consumed rollback point (" +
                rolledBack.StandardOutput.Trim() + ").");
            var afterRollbackUnlock = await RunOwnerCommandWithInputAsync(connectionsExecutable, S8T3Passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(afterRollbackUnlock.ExitCode == 0, "The rolled-back S8-T3 vault did not unlock (" +
                afterRollbackUnlock.StandardError.Trim() + ").");
            var afterRollbackRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-read", "--control-pipe", fixture.CanonicalControlPipe, "--resource-id", S8T3ItemA).ConfigureAwait(false);
            Require(afterRollbackRead.ExitCode == 0, "A rolled-back S8-T3 vault stopped serving its lifecycle record (" +
                afterRollbackRead.StandardError.Trim() + ").");
            var lockAfterRollback = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(lockAfterRollback.ExitCode == 0, "The rolled-back S8-T3 vault did not lock again.");
            Console.WriteLine("OBSERVATION rollback restored only the files the upgrade wrote; the record written after the upgrade and its revision were untouched");

            var reUpgrade = await RunOwnerCommandWithInputAsync(connectionsExecutable, S8T3Passphrase + Environment.NewLine,
                "owner", "upgrade", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(reUpgrade.ExitCode == 0 && reUpgrade.StandardOutput.Contains("UPGRADE complete", StringComparison.Ordinal),
                "The S8-T3 vault could not be upgraded again after a rollback (" + reUpgrade.StandardError.Trim() + ").");
            var discarded = await RunOwnerCommandWithInputAsync(connectionsExecutable, S8T3Passphrase + Environment.NewLine,
                "owner", "rollback", "--control-pipe", fixture.CanonicalControlPipe, "--discard", "true").ConfigureAwait(false);
            Require(discarded.ExitCode == 0 && discarded.StandardOutput.Contains("ROLLBACK discarded", StringComparison.Ordinal),
                "The S8-T3 rollback point was not discarded (" + discarded.StandardError.Trim() + ").");
            var afterDiscard = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "status", "--control-pipe", fixture.CanonicalControlPipe).ConfigureAwait(false);
            Require(afterDiscard.StandardOutput.Contains("format=current", StringComparison.Ordinal) &&
                    afterDiscard.StandardOutput.Contains("rollback=none", StringComparison.Ordinal),
                "The S8-T3 vault did not report a current format after discarding its rollback point (" +
                afterDiscard.StandardOutput.Trim() + ").");

            // ----- control-v3: a genuinely old owner control state -----
            controlService = await StartServiceAsync(connectionsExecutable, fixture.ControlSettingsPath).ConfigureAwait(false);
            var controlUpgrade = await RunOwnerCommandWithInputAsync(connectionsExecutable, S8T3Passphrase + Environment.NewLine,
                "owner", "upgrade", "--control-pipe", fixture.ControlControlPipe).ConfigureAwait(false);
            Require(controlUpgrade.ExitCode == 0 && controlUpgrade.StandardOutput.Contains("UPGRADE complete", StringComparison.Ordinal) &&
                    controlUpgrade.StandardOutput.Contains("from=control:3", StringComparison.Ordinal) &&
                    controlUpgrade.StandardOutput.Contains("to=control:4", StringComparison.Ordinal),
                "The S8-T3 upgrade did not migrate the real v3 owner control state (" + controlUpgrade.StandardError.Trim() + " " +
                controlUpgrade.StandardOutput.Trim() + ").");
            var controlUnlock = await RunOwnerCommandWithInputAsync(connectionsExecutable, S8T3Passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlControlPipe).ConfigureAwait(false);
            Require(controlUnlock.ExitCode == 0, "The migrated control-v3 vault did not unlock (" + controlUnlock.StandardError.Trim() + ").");
            var grants = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "grants", "--control-pipe", fixture.ControlControlPipe).ConfigureAwait(false);
            Require(grants.ExitCode == 0 && grants.StandardOutput.Contains("GRANTS count=1", StringComparison.Ordinal),
                "The S8-T3 control migration lost the previously issued grant (" + grants.StandardOutput.Trim() + ").");
            Require(grants.StandardOutput.Contains(S8T3ControlItem, StringComparison.Ordinal),
                "The migrated grant lost its resource scope (" + grants.StandardOutput.Trim() + ").");
            var controlIndex = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlControlPipe).ConfigureAwait(false);
            Require(controlIndex.ExitCode == 0 && controlIndex.StandardOutput.Contains("INDEX rebuilt", StringComparison.Ordinal),
                "The migrated control-v3 vault could not rebuild its index (" + controlIndex.StandardError.Trim() + ").");
            Console.WriteLine("OBSERVATION the real v3 owner control state migrated to v4 and its live grant kept its resource scope");

            // ----- a pre-upgrade encrypted backup still restores -----
            backupService = await StartServiceAsync(connectionsExecutable, fixture.BackupSettingsPath).ConfigureAwait(false);
            var backupUnlock = await RunOwnerCommandWithInputAsync(connectionsExecutable, S8T3Passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.BackupControlPipe).ConfigureAwait(false);
            Require(backupUnlock.ExitCode == 0, "The S8-T3 backup copy did not unlock (" + backupUnlock.StandardError.Trim() + ").");
            var backupPath = Path.Combine(fixture.OutputDirectory, "s8t3-pre-upgrade.mbbackup");
            var backup = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "backup", "--control-pipe", fixture.BackupControlPipe, "--output-file", backupPath).ConfigureAwait(false);
            Require(backup.ExitCode == 0 && backup.StandardOutput.Contains("BACKUP written", StringComparison.Ordinal),
                "The pre-upgrade S8-T3 backup did not complete (" + backup.StandardError.Trim() + ").");
            var backupLock = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.BackupControlPipe).ConfigureAwait(false);
            Require(backupLock.ExitCode == 0, "The S8-T3 backup copy did not lock.");
            // The commit boundary: every file is rewritten, the process dies
            // before the journal is marked complete. The next upgrade must
            // finish the commit instead of leaving an interrupted journal.
            byte[]? boundaryKey = null;
            try
            {
                var boundaryStore = new EncryptedVaultStore(Path.Combine(fixture.BackupRoot, "vault"));
                Require(boundaryStore.TryUnlock(S8T3Passphrase, useRecoveryCode: false, out boundaryKey, out var boundaryManifest) &&
                        boundaryKey is not null && boundaryManifest is not null,
                    "The S8-T3 smoke could not open the old-backup vault for the commit-boundary test.");
                var boundaryEngine = new VaultSchemaUpgradeEngine(boundaryStore)
                {
                    OnFileMigrated = index =>
                    {
                        if (index == 1)
                        {
                            throw new InvalidOperationException("S8-T3 commit-boundary interruption");
                        }
                    }
                };
                var boundaryInterrupted = false;
                try
                {
                    boundaryEngine.Upgrade(boundaryManifest!, boundaryKey!);
                }
                catch (InvalidOperationException ex) when (string.Equals(ex.Message, "S8-T3 commit-boundary interruption", StringComparison.Ordinal))
                {
                    boundaryInterrupted = true;
                }

                Require(boundaryInterrupted, "The S8-T3 commit-boundary interruption point was never reached.");
            }
            finally
            {
                if (boundaryKey is not null)
                {
                    CryptographicOperations.ZeroMemory(boundaryKey);
                }
            }

            var boundaryStatus = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "status", "--control-pipe", fixture.BackupControlPipe).ConfigureAwait(false);
            Require(boundaryStatus.StandardOutput.Contains("format=interrupted", StringComparison.Ordinal),
                "The S8-T3 commit-boundary interruption was not reported as interrupted (" + boundaryStatus.StandardOutput.Trim() + ").");

            var backupUpgrade = await RunOwnerCommandWithInputAsync(connectionsExecutable, S8T3Passphrase + Environment.NewLine,
                "owner", "upgrade", "--control-pipe", fixture.BackupControlPipe).ConfigureAwait(false);
            Require(backupUpgrade.ExitCode == 0 && backupUpgrade.StandardOutput.Contains("UPGRADE complete", StringComparison.Ordinal),
                "The S8-T3 backup copy did not upgrade (" + backupUpgrade.StandardError.Trim() + ").");
            var restore = await RunOwnerCommandWithInputAsync(connectionsExecutable, S8T3Passphrase + Environment.NewLine,
                "owner", "restore", "--control-pipe", fixture.BackupControlPipe,
                "--backup-file", backupPath, "--target-directory", fixture.RestoredVaultPath).ConfigureAwait(false);
            Require(restore.ExitCode == 0 && restore.StandardOutput.Contains("RESTORE written", StringComparison.Ordinal),
                "A backup taken before the S8-T3 upgrade did not restore after it (" + restore.StandardError.Trim() + ").");
            restoredService = await StartServiceAsync(connectionsExecutable, fixture.RestoredSettingsPath).ConfigureAwait(false);
            var restoredUnlock = await RunOwnerCommandWithInputAsync(connectionsExecutable, S8T3Passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.RestoredControlPipe).ConfigureAwait(false);
            Require(restoredUnlock.ExitCode == 0, "The restored pre-upgrade S8-T3 backup did not open with the same passphrase (" +
                restoredUnlock.StandardError.Trim() + ").");
            var restoredItem = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.RestoredControlPipe, "--resource-id", S8T3ItemA).ConfigureAwait(false);
            Require(restoredItem.ExitCode == 0 && restoredItem.StandardOutput.Contains(S8T3BodyA, StringComparison.Ordinal),
                "The restored pre-upgrade S8-T3 backup lost its record (" + restoredItem.StandardError.Trim() + ").");
            var restoredLifecycle = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-read", "--control-pipe", fixture.RestoredControlPipe, "--resource-id", S8T3ItemA).ConfigureAwait(false);
            Require(restoredLifecycle.ExitCode == 0, "The restored pre-upgrade S8-T3 backup lost its lifecycle record (" +
                restoredLifecycle.StandardError.Trim() + ").");
            Console.WriteLine("OBSERVATION a backup taken before the upgrade still opened with the same passphrase after the migration and nested upgrade");

            RunS8T3LegacyCutover();
            Console.WriteLine("OBSERVATION the legacy plaintext cutover wrote no permission change, removed the migrated plaintext only after the commit, resumed an interrupted cutover and refused a different vault untouched");

            Console.WriteLine("LIMIT the S8-T3 proof uses the repository's own pinned historical builds as the prior-format source; the schema profile observed (control v3, lifecycle v1, job v1) is that of those commits. Envelope and manifest format stay 1 and are not claimed as migrated.");
            Console.WriteLine("LIMIT a rollback restores only files the upgrade wrote and that the owner has not changed since; a record revised after the upgrade keeps its newer bytes and the rollback point is consumed on the first restore.");
        }
        finally
        {
            foreach (var service in new[] { restoredService, backupService, controlService, canonicalService })
            {
                if (service is null)
                {
                    continue;
                }

                try
                {
                    if (!service.Process.HasExited)
                    {
                        service.Process.Kill(entireProcessTree: true);
                        await service.Process.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }


    /// <summary>
    /// S8-T3 the explicit legacy plaintext cutover under the no-ACL boundary.
    /// It proves the three observable rules the runbook states: the cutover
    /// changes no permission and no owner, it removes the migrated plaintext
    /// only after the encrypted vault and the settings file are committed, an
    /// interruption between those two steps is resumed, and a different vault
    /// at the same path is refused untouched.
    /// </summary>
    private static void RunS8T3LegacyCutover()
    {
        var repositoryRoot = SmokeFixture.FindRepositoryRoot();
        var ownerSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows token has no user SID.");
        var contentA = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"note\":\"S8T3_LEGACY_PLAINTEXT_A\"}");
        var contentB = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"note\":\"S8T3_LEGACY_PLAINTEXT_B\"}");
        var policy = Encoding.UTF8.GetBytes("{\"legacy\":\"policy\"}");
        var passphrase = S8T3Passphrase + "_legacy";

        var root = Path.Combine(repositoryRoot, "runtime", "s8-t3", "legacy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settingsPath = WriteS8T3LegacyFixture(root, ownerSid, contentA, contentB, policy, includePolicy: true);
            var resourceRoot = Path.Combine(root, "resources");
            var plainA = Path.Combine(resourceRoot, "legacy-a.txt");
            var plainB = Path.Combine(resourceRoot, "legacy-b.txt");
            var plainPolicy = Path.Combine(root, "grant-policy.json");

            var migrated = LegacyVaultMigration.Migrate(settingsPath, passphrase);
            Require(!migrated.Resumed && migrated.ResourceCount == 2 && migrated.LegacyPolicyPreserved &&
                    migrated.RecoveryCode.Length > 0,
                "The S8-T3 legacy cutover did not report a fresh migration with its private policy (" +
                migrated.ResourceCount.ToString(CultureInfo.InvariantCulture) + ").");
            Require(!File.Exists(plainA) && !File.Exists(plainB) && !File.Exists(plainPolicy),
                "The S8-T3 legacy cutover left a migrated plaintext copy behind.");
            Require(ReadS8T3SchemaVersion(settingsPath) == 3,
                "The S8-T3 legacy cutover did not write the current settings schema.");

            byte[]? key = null;
            try
            {
                var store = new EncryptedVaultStore(Path.Combine(root, "vault"));
                Require(store.TryUnlock(passphrase, useRecoveryCode: false, out key, out var manifest) &&
                        key is not null && manifest is not null,
                    "The S8-T3 legacy cutover produced a vault the owner passphrase does not open.");
                var readA = store.ReadResource(manifest!, key!, "s8t3-legacy-a", 1);
                try
                {
                    Require(readA.AsSpan().SequenceEqual(contentA),
                        "The S8-T3 legacy cutover changed the migrated bytes.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(readA);
                }

                var readPolicy = store.ReadResourceIncludingInternal(manifest!, key!, "service-policy-state", 1);
                try
                {
                    Require(readPolicy.AsSpan().SequenceEqual(policy),
                        "The S8-T3 legacy cutover lost the private policy state.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(readPolicy);
                }
            }
            finally
            {
                if (key is not null)
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }

            // The state an interrupted cutover leaves: the vault exists with the
            // exact legacy bytes and the legacy settings and plaintext are still
            // in place. A retry must complete it and keep the one-time recovery
            // key out of the reply.
            var resumeRoot = Path.Combine(repositoryRoot, "runtime", "s8-t3", "legacy-" + Guid.NewGuid().ToString("N"));
            try
            {
                var resumeSettings = WriteS8T3LegacyFixture(resumeRoot, ownerSid, contentA, contentB, policy, includePolicy: false);
                var seedStore = new EncryptedVaultStore(Path.Combine(resumeRoot, "vault"));
                var material = seedStore.Create(passphrase, new[]
                {
                    new VaultResourceSeed("s8t3-legacy-a", "s8t3-legacy-zone", contentA),
                    new VaultResourceSeed("s8t3-legacy-b", "s8t3-legacy-zone", contentB)
                });
                CryptographicOperations.ZeroMemory(material.DataKey);
                var resumed = LegacyVaultMigration.Migrate(resumeSettings, passphrase);
                Require(resumed.Resumed && resumed.RecoveryCode.Length == 0,
                    "The S8-T3 legacy cutover did not resume an interrupted cutover.");
                Require(!File.Exists(Path.Combine(resumeRoot, "resources", "legacy-a.txt")),
                    "The resumed S8-T3 cutover did not remove the migrated plaintext.");
                Require(ReadS8T3SchemaVersion(resumeSettings) == 3,
                    "The resumed S8-T3 cutover did not write the current settings schema.");
            }
            finally
            {
                TryDeleteS8T3(resumeRoot);
            }

            // A different vault at the same path is refused untouched.
            var otherRoot = Path.Combine(repositoryRoot, "runtime", "s8-t3", "legacy-" + Guid.NewGuid().ToString("N"));
            try
            {
                var otherSettings = WriteS8T3LegacyFixture(otherRoot, ownerSid, contentA, contentB, policy, includePolicy: false);
                var otherStore = new EncryptedVaultStore(Path.Combine(otherRoot, "vault"));
                var otherMaterial = otherStore.Create(S8T3Passphrase + "_other", new[]
                {
                    new VaultResourceSeed("s8t3-legacy-a", "s8t3-legacy-zone", contentA)
                });
                CryptographicOperations.ZeroMemory(otherMaterial.DataKey);
                var refused = false;
                try
                {
                    LegacyVaultMigration.Migrate(otherSettings, passphrase);
                }
                catch (IOException)
                {
                    refused = true;
                }

                Require(refused, "The S8-T3 legacy cutover overwrote a different vault.");
                Require(File.Exists(Path.Combine(otherRoot, "resources", "legacy-a.txt")) &&
                        ReadS8T3SchemaVersion(otherSettings) == 1,
                    "A refused S8-T3 legacy cutover changed the plaintext or the legacy settings.");
            }
            finally
            {
                TryDeleteS8T3(otherRoot);
            }
        }
        finally
        {
            TryDeleteS8T3(root);
        }
    }

    private static string WriteS8T3LegacyFixture(
        string root,
        string ownerSid,
        byte[] contentA,
        byte[] contentB,
        byte[] policy,
        bool includePolicy)
    {
        var resourceRoot = Path.Combine(root, "resources");
        Directory.CreateDirectory(resourceRoot);
        File.WriteAllBytes(Path.Combine(resourceRoot, "legacy-a.txt"), contentA);
        File.WriteAllBytes(Path.Combine(resourceRoot, "legacy-b.txt"), contentB);
        if (includePolicy)
        {
            File.WriteAllBytes(Path.Combine(root, "grant-policy.json"), policy);
        }

        var settingsPath = Path.Combine(root, "service-settings.json");
        var controlPipe = "MetaBrainS8T3Legacy-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(
            settingsPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                ownerSid,
                controlPipe,
                agentPipe = controlPipe + ".agent",
                managedResources = new[]
                {
                    new { resourceId = "s8t3-legacy-a", zoneId = "s8t3-legacy-zone", fileName = "legacy-a.txt" },
                    new { resourceId = "s8t3-legacy-b", zoneId = "s8t3-legacy-zone", fileName = "legacy-b.txt" }
                }
            }),
            new UTF8Encoding(false));
        return settingsPath;
    }

    private static int ReadS8T3SchemaVersion(string settingsPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        return document.RootElement.GetProperty("schemaVersion").GetInt32();
    }

    private static void TryDeleteS8T3(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Drives the real stdio MCP bridge for one scoped memory read: the same
    /// operation an official client calls after redemption. No provider and no
    /// network are involved.
    /// </summary>
    private static async Task<string> RunS8T3McpReadAsync(
        string executable,
        string agentPipe,
        string sessionFile,
        string resourceId,
        string revision)
    {
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("agent-mcp");
        start.ArgumentList.Add("--agent-pipe");
        start.ArgumentList.Add(agentPipe);
        using var process = new System.Diagnostics.Process { StartInfo = start };
        Require(process.Start(), "The S8-T3 MCP bridge process did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        var initialize = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { protocolVersion = "2025-11-25" }
        });
        var call = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new
            {
                name = "metabrain_memory_read",
                arguments = new
                {
                    sessionFile,
                    resourceId,
                    resourceRevision = revision
                }
            }
        });
        await process.StandardInput.WriteLineAsync(initialize).ConfigureAwait(false);
        await process.StandardInput.WriteLineAsync(call).ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(CommandTimeout).ConfigureAwait(false);
        var text = await output.ConfigureAwait(false);
        _ = await error.ConfigureAwait(false);
        return text;
    }

    private static string WriteS8T3Temp(string directory, byte[] content, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// A stable fingerprint of every vault file (relative path, length and
    /// SHA-256). It is used to prove that a refused act wrote nothing.
    /// </summary>
    private static string VaultFingerprint(string vaultPath)
    {
        var builder = new StringBuilder();
        foreach (var path in Directory.GetFiles(vaultPath, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            builder.Append(Path.GetRelativePath(vaultPath, path)).Append('|')
                .Append(new FileInfo(path).Length).Append('|')
                .Append(HashFile(path)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static void RequireNoPlaintext(string vaultPath, string marker, string what)
    {
        var markerBytes = Encoding.UTF8.GetBytes(marker);
        foreach (var path in Directory.GetFiles(vaultPath, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.AsSpan().IndexOf(markerBytes) >= 0)
            {
                throw new InvalidOperationException("S8-T3 " + what + " held a private record body in plaintext at " + path + ".");
            }
        }
    }

    private static void RequireNoTemporaryFiles(string vaultPath)
    {
        var leftovers = Directory.GetFiles(vaultPath, ".*.tmp", SearchOption.AllDirectories);
        Require(leftovers.Length == 0,
            "The S8-T3 upgrade left a temporary file behind (" + string.Join(",", leftovers) + ").");
    }
}

/// <summary>
/// The S8-T3 fixture: copies of the historical artifacts in a fresh working
/// root, each with its own service settings and pipe names, plus the output,
/// backup and restore locations.
/// </summary>
internal sealed class S8T3Fixture : IDisposable
{
    private bool _disposed;

    private S8T3Fixture(
        string root,
        string canonicalRoot,
        string canonicalSettingsPath,
        string canonicalControlPipe,
        string canonicalAgentPipe,
        string controlRoot,
        string controlSettingsPath,
        string controlControlPipe,
        string controlAgentPipe,
        string backupRoot,
        string backupSettingsPath,
        string backupControlPipe,
        string backupAgentPipe,
        string restoredRoot,
        string restoredSettingsPath,
        string restoredControlPipe,
        string outputDirectory)
    {
        Root = root;
        CanonicalRoot = canonicalRoot;
        CanonicalSettingsPath = canonicalSettingsPath;
        CanonicalControlPipe = canonicalControlPipe;
        CanonicalAgentPipe = canonicalAgentPipe;
        ControlRoot = controlRoot;
        ControlSettingsPath = controlSettingsPath;
        ControlControlPipe = controlControlPipe;
        ControlAgentPipe = controlAgentPipe;
        BackupRoot = backupRoot;
        BackupSettingsPath = backupSettingsPath;
        BackupControlPipe = backupControlPipe;
        BackupAgentPipe = backupAgentPipe;
        RestoredRoot = restoredRoot;
        RestoredSettingsPath = restoredSettingsPath;
        RestoredControlPipe = restoredControlPipe;
        OutputDirectory = outputDirectory;
    }

    public string Root { get; }
    public string CanonicalRoot { get; }
    public string CanonicalSettingsPath { get; }
    public string CanonicalControlPipe { get; }
    public string CanonicalAgentPipe { get; }
    public string ControlRoot { get; }
    public string ControlSettingsPath { get; }
    public string ControlControlPipe { get; }
    public string ControlAgentPipe { get; }
    public string BackupRoot { get; }
    public string BackupSettingsPath { get; }
    public string BackupControlPipe { get; }
    public string BackupAgentPipe { get; }
    public string RestoredRoot { get; }
    public string RestoredSettingsPath { get; }
    public string RestoredControlPipe { get; }
    public string OutputDirectory { get; }

    public string CanonicalVaultPath => Path.Combine(CanonicalRoot, "vault");
    public string RestoredVaultPath => Path.Combine(RestoredRoot, "vault");

    public static S8T3Fixture Create(string artifactRoot)
    {
        var repositoryRoot = SmokeFixture.FindRepositoryRoot();
        var root = Path.Combine(repositoryRoot, "runtime", "s8-t3", "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var canonicalRoot = Path.Combine(root, "canonical");
            var controlRoot = Path.Combine(root, "control");
            var backupRoot = Path.Combine(root, "oldbackup");
            var restoredRoot = Path.Combine(root, "restored");
            var outputDirectory = Path.Combine(root, "output");
            CopyDirectory(Path.Combine(artifactRoot, "canonical-v1"), canonicalRoot);
            CopyDirectory(Path.Combine(artifactRoot, "control-v3"), controlRoot);
            CopyDirectory(Path.Combine(artifactRoot, "canonical-v1"), backupRoot);
            Directory.CreateDirectory(restoredRoot);
            Directory.CreateDirectory(outputDirectory);

            var canonicalPipe = "MetaBrainS8T3A-" + Guid.NewGuid().ToString("N");
            var canonicalSettings = WriteSettings(canonicalRoot, canonicalPipe);
            var controlPipe = "MetaBrainS8T3C-" + Guid.NewGuid().ToString("N");
            var controlSettings = WriteSettings(controlRoot, controlPipe);
            var backupPipe = "MetaBrainS8T3B-" + Guid.NewGuid().ToString("N");
            var backupSettings = WriteSettings(backupRoot, backupPipe);
            var restoredPipe = "MetaBrainS8T3R-" + Guid.NewGuid().ToString("N");
            var restoredSettings = WriteSettings(restoredRoot, restoredPipe);
            return new S8T3Fixture(
                root,
                canonicalRoot,
                canonicalSettings,
                canonicalPipe,
                canonicalPipe + ".agent",
                controlRoot,
                controlSettings,
                controlPipe,
                controlPipe + ".agent",
                backupRoot,
                backupSettings,
                backupPipe,
                backupPipe + ".agent",
                restoredRoot,
                restoredSettings,
                restoredPipe,
                outputDirectory);
        }
        catch
        {
            TryDelete(root);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TryDelete(Root);
    }

    private static string WriteSettings(string dataRoot, string controlPipe)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var ownerSid = identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows token has no user SID.");
        var path = Path.Combine(dataRoot, "service-settings.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 3,
                ownerSid,
                controlPipe,
                agentPipe = controlPipe + ".agent",
            }),
            new UTF8Encoding(false));
        return path;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
