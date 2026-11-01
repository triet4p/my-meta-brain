using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MetaBrain.Core.Security;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S12-T5 runtime proof that the shared encrypted backup carries the whole
/// workspace (identity, tree, retained original bytes, anchors, revisions and
/// tombstones) together with the one canonical purge ledger, that a restart or
/// lock/unlock reproduces that state exactly, and that a restore of a backup
/// taken before a purge cannot resurrect identities the same live installation
/// already purged. Every root, backup and restored vault lives under the
/// repository's own runtime directory; the external originals are synthetic.
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S12T5Zone = "s12t5-zone";
    private const string S12T5RecordKeep = "s12t5-record-keep";
    private const string S12T5RecordPurged = "s12t5-record-purged";
    private const string S12T5KeepBody = "S12T5_SYNTHETIC_KEEP_BODY_4c19";
    private const string S12T5PurgedBody = "S12T5_SYNTHETIC_PURGED_BODY_8b02";
    private const string S12T5LegacyRecord = "s12t5-legacy-record";
    private const string S12T5LegacyBody = "S12T5_LEGACY_FORMAT1_BODY_6e21";
    private const string S12T5PurgeFolder = "purge-folder-s12t5";
    private const string S12T5PurgeNested = "purge-nested-s12t5";
    private const string S12T5PurgeNote = "purge-note-s12t5.md";
    private const string S12T5PurgeDeep = "purge-deep-s12t5.bin";
    private const string S12T5KeepFolder = "keep-folder-s12t5";
    private const string S12T5KeepNote = "keep-note-s12t5.md";
    private const string S12T5KeepBinary = "keep-binary-s12t5.bin";
    private const string S12T5KeepEmpty = "keep-empty-s12t5.bin";
    private const string S12T5Tombstone = "tombstone-note-s12t5.md";
    private const string S12T5Replacement = "tombstone-note-s12t5.md";
    private const string S12T5PurgeReason = "S12-T5 synthetic irreversible purge of the retained draft group.";
    private const string S12T5PurgeNoteMarker = "S12T5_PURGE_NOTE_MARKER_5d77";
    private const string S12T5KeepRevisionMarker = "S12T5_KEEP_REVISION_MARKER_a3e8";
    private static readonly byte[] S12T5AnchorRange = Encoding.ASCII.GetBytes("synthetic");
    private static readonly byte[] S12T5KeepNoteBytes = Encoding.UTF8.GetBytes(
        "synthetic S12-T5 keep note\nanchored line for the citation\n");
    private static readonly byte[] S12T5KeepRevisionBytes = Encoding.UTF8.GetBytes(
        "# synthetic S12-T5 keep note (revision 2)\nmarker " + S12T5KeepRevisionMarker + "\n");
    private static readonly byte[] S12T5BinaryBytes = { 0x00, 0xFF, 0x81, 0xC0, 0xAF, 0xED, 0xA0, 0x80, 0x0A, 0x7F };
    private static readonly byte[] S12T5EmptyBytes = Array.Empty<byte>();

    public static async Task RunS12T5WorkspaceBackupRestoreAsync(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S12-T5 backup smoke is Windows-only.");
        }

        using var fixture = S12T5Fixture.Create();
        RunningService? live = null;
        RunningService? restoredPost = null;
        RunningService? restoredOlder = null;
        RunningService? restoredLocked = null;
        try
        {
            live = await StartServiceAsync(executable, fixture.LiveSettingsPath).ConfigureAwait(false);
            var passphrase = CreateSyntheticPassphrase();
            var provision = await RunOwnerCommandWithInputAsync(executable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The owner CLI did not provision the S12-T5 vault (stderr=" + provision.StandardError.Trim() + ").");


            // The S12-T5 secure provider-key cutover: no environment path,
            // the key rides one redirected stdin line and never argv/log/files.
            var fromEnv = await S12T5OwnerAsync(executable, fixture.ControlPipe,
                "provider-config", "--provider", "openai", "--model", "s12t5-synthetic",
                "--from-env").ConfigureAwait(false);
            Require(fromEnv.ExitCode != 0 && !fromEnv.StandardOutput.Contains("PROVIDER configured", StringComparison.Ordinal),
                "The removed --from-env provider path was accepted.");
            var syntheticKey = "sk-s12t5-synthetic-" + Guid.NewGuid().ToString("N");
            var missingKey = await RunOwnerCommandWithInputAsync(executable, Environment.NewLine,
                "owner", "provider-config", "--control-pipe", fixture.ControlPipe,
                "--provider", "openai", "--model", "s12t5-synthetic", "--api-key-stdin", "true").ConfigureAwait(false);
            Require(missingKey.ExitCode != 0 && missingKey.StandardError.Contains("missing_api_key", StringComparison.Ordinal),
                "An empty secure provider key was accepted (" + missingKey.StandardError.Trim() + ").");
            var providerConfigured = await RunOwnerCommandWithInputAsync(executable, syntheticKey + Environment.NewLine,
                "owner", "provider-config", "--control-pipe", fixture.ControlPipe,
                "--provider", "openai", "--model", "s12t5-synthetic", "--api-key-stdin", "true").ConfigureAwait(false);
            Require(providerConfigured.ExitCode == 0 && providerConfigured.StandardOutput.Contains("PROVIDER configured", StringComparison.Ordinal),
                "The stdin provider key was not configured (" + providerConfigured.StandardError.Trim() + ").");
            Require(!providerConfigured.StandardOutput.Contains(syntheticKey, StringComparison.Ordinal) &&
                    !providerConfigured.StandardError.Contains(syntheticKey, StringComparison.Ordinal),
                "The provider CLI echoed the API key.");
            Require(!File.ReadAllText(fixture.LiveSettingsPath).Contains(syntheticKey, StringComparison.Ordinal),
                "The provider API key reached the service settings file.");
            foreach (var path in Directory.GetFiles(Path.Combine(fixture.LiveRoot, "vault"), "*", SearchOption.AllDirectories))
            {
                RequireNoS12T5Plaintext(await File.ReadAllBytesAsync(path).ConfigureAwait(false),
                    new[] { syntheticKey }, "vault file " + Path.GetFileName(path));
            }

            Console.WriteLine("PASS provider-config rejects --from-env and keeps the stdin key out of argv/stdout/settings/plaintext vault files");

            var emptyStatus = await S12T5StatusAsync(executable, fixture.ControlPipe).ConfigureAwait(false);
            Require(emptyStatus.Contains("workspace_uninitialized", StringComparison.Ordinal) ||
                    emptyStatus.Contains("state=uninitialized", StringComparison.Ordinal),
                "A fresh vault did not report an uninitialized workspace (" + emptyStatus.Trim() + ").");

            var dataset = await WriteS12T5DatasetAsync(executable, fixture).ConfigureAwait(false);

            // B1 precedes every purge: it holds the draft group, both anchors,
            // the tombstone and the two canonical records.
            var backupPre = Path.Combine(fixture.BackupDirectory, "s12t5-pre-purge.mbbackup");
            var preBackup = await RunOwnerCommandAsync(executable,
                "owner", "backup", "--control-pipe", fixture.ControlPipe, "--output-file", backupPre).ConfigureAwait(false);
            Require(preBackup.ExitCode == 0 && preBackup.StandardOutput.Contains("BACKUP written", StringComparison.Ordinal),
                "The owner CLI did not write the pre-purge S12-T5 backup (stderr=" + preBackup.StandardError.Trim() + ").");
            Require(File.Exists(backupPre), "The pre-purge backup file is missing.");
            RequireNoS12T5Plaintext(await File.ReadAllBytesAsync(backupPre).ConfigureAwait(false),
                new[] { S12T5PurgeNoteMarker, S12T5KeepRevisionMarker, S12T5KeepBody, S12T5PurgedBody, S12T5KeepNote, S12T5PurgeFolder },
                "pre-purge backup ciphertext");
            Console.WriteLine("PASS the pre-purge backup is ciphertext carrying no record body, name or anchor marker");

            // The purge lifecycle: preview refusal paths first, then the exact
            // confirmed purge, all through the real owner CLI.
            var purgePreview = await S12T5OwnerAsync(executable, fixture.ControlPipe,
                "workspace-purge-preview", "--node-id", dataset.PurgeFolderId).ConfigureAwait(false);
            Require(purgePreview.ExitCode == 0 && purgePreview.StandardOutput.Contains("PURGE PREVIEW", StringComparison.Ordinal),
                "The workspace purge preview failed (stderr=" + purgePreview.StandardError.Trim() + ").");
            Require(WorkspaceNumber(purgePreview.StandardOutput.Split('\n').First(line => line.StartsWith("PURGE PREVIEW ", StringComparison.Ordinal)), "nodes") == 4,
                "The purge preview did not list the folder, its note, its nested folder and the nested binary.");
            Require(purgePreview.StandardOutput.Contains("PURGE DISCLOSURE", StringComparison.Ordinal) &&
                    purgePreview.StandardOutput.Contains("backup", StringComparison.OrdinalIgnoreCase),
                "The purge preview did not disclose the backup and export copies the act cannot recall.");
            var purgeToken = WorkspaceToken(purgePreview.StandardOutput, "token");
            var purgeTreeRevision = WorkspaceNumber(purgePreview.StandardOutput.Split('\n').First(line => line.StartsWith("PURGE PREVIEW ", StringComparison.Ordinal)), "revision");

            var wrongPurgeToken = await S12T5OwnerAsync(executable, fixture.ControlPipe,
                "workspace-purge", "--node-id", dataset.PurgeFolderId,
                "--expected-tree-revision", purgeTreeRevision,
                "--preview", new string('0', purgeToken.Length),
                "--confirm", "PURGE", "--reason", S12T5PurgeReason).ConfigureAwait(false);
            RequireWorkspaceRefusal(wrongPurgeToken, WorkspaceErrorCodes.Conflict, "a workspace purge with a mismatched preview");
            var missingReason = await S12T5OwnerAsync(executable, fixture.ControlPipe,
                "workspace-purge", "--node-id", dataset.PurgeFolderId,
                "--expected-tree-revision", purgeTreeRevision,
                "--preview", purgeToken, "--confirm", "PURGE").ConfigureAwait(false);
            RequireWorkspaceRefusal(missingReason, WorkspaceErrorCodes.PurgeConfirmation, "a workspace purge without the owner reason");
            var stalePurge = await S12T5OwnerAsync(executable, fixture.ControlPipe,
                "workspace-purge", "--node-id", dataset.PurgeFolderId,
                "--expected-tree-revision", purgeTreeRevision - 1,
                "--preview", purgeToken, "--confirm", "PURGE", "--reason", S12T5PurgeReason).ConfigureAwait(false);
            RequireWorkspaceRefusal(stalePurge, WorkspaceErrorCodes.Conflict, "a workspace purge at a stale tree revision");
            var unchangedStatus = await S12T5StatusAsync(executable, fixture.ControlPipe).ConfigureAwait(false);
            Require(unchangedStatus.Contains("purged=0", StringComparison.Ordinal),
                "A refused workspace purge recorded a ledger entry (" + unchangedStatus.Trim() + ").");

            var purge = await S12T5OwnerAsync(executable, fixture.ControlPipe,
                "workspace-purge", "--node-id", dataset.PurgeFolderId,
                "--expected-tree-revision", purgeTreeRevision,
                "--preview", purgeToken, "--confirm", "PURGE", "--reason", S12T5PurgeReason).ConfigureAwait(false);
            Require(purge.ExitCode == 0 && purge.StandardOutput.Contains("PURGED ", StringComparison.Ordinal) &&
                    purge.StandardOutput.Contains("purgeId=", StringComparison.Ordinal) &&
                    purge.StandardOutput.Contains("nodes=4", StringComparison.Ordinal),
                "The owner CLI did not purge the previewed group (exit=" + purge.ExitCode.ToString(CultureInfo.InvariantCulture) +
                " err=" + purge.StandardError.Trim() + ").");
            Require(purge.StandardOutput.Contains("PURGE LEDGER written", StringComparison.Ordinal),
                "The successful purge did not report the durable ledger entry.");

            var purgedStatus = await S12T5StatusAsync(executable, fixture.ControlPipe).ConfigureAwait(false);
            Require(purgedStatus.Contains("purged=4", StringComparison.Ordinal),
                "workspace-status did not report the four reserved purged identities (" + purgedStatus.Trim() + ").");
            var tombstonesAfterPurge = await S12T5OwnerAsync(executable, fixture.ControlPipe, "workspace-tombstones").ConfigureAwait(false);
            Require(tombstonesAfterPurge.ExitCode == 0 &&
                    tombstonesAfterPurge.StandardOutput.Contains("TOMBSTONES count=1", StringComparison.Ordinal),
                "The purge left tombstones behind or removed the unrelated one (" + tombstonesAfterPurge.StandardOutput.Trim() + ").");
            Require(!tombstonesAfterPurge.StandardOutput.Contains(dataset.PurgeFolderId, StringComparison.Ordinal) &&
                    !tombstonesAfterPurge.StandardOutput.Contains(dataset.PurgedNoteId, StringComparison.Ordinal),
                "The purged identities are still listed as tombstones.");
            var readPurged = await S12T5OwnerAsync(executable, fixture.ControlPipe,
                "workspace-read", "--file-id", dataset.PurgedNoteId).ConfigureAwait(false);
            Require(readPurged.ExitCode != 0, "A purged workspace file could still be read.");
            var restorePurged = await S12T5OwnerAsync(executable, fixture.ControlPipe,
                "workspace-restore", "--node-id", dataset.PurgeFolderId,
                "--expected-tree-revision", WorkspaceNumber(await S12T5StatusAsync(executable, fixture.ControlPipe).ConfigureAwait(false), "revision")).ConfigureAwait(false);
            RequireWorkspaceRefusal(restorePurged, WorkspaceErrorCodes.NotFound, "a restore of a purged tombstone");

            // The canonical record purge writes the same vault ledger.
            var recordPreview = await S12T5OwnerAsync(executable, fixture.ControlPipe,
                "purge-preview", "--resource-id", S12T5RecordPurged, "--resource-revision", "1",
                "--reason", "S12-T5 synthetic canonical purge reason.").ConfigureAwait(false);
            Require(recordPreview.ExitCode == 0 && recordPreview.StandardOutput.Contains("PURGE PREVIEW", StringComparison.Ordinal),
                "The canonical purge preview failed (stderr=" + recordPreview.StandardError.Trim() + ").");
            var canonicalPurgeId = S12T5LineValue(recordPreview.StandardOutput, "PURGE PREVIEW id=");
            var canonicalImpacted = S12T5ImpactedTokens(recordPreview.StandardOutput);
            var canonicalGeneration = S12T5LineValue(recordPreview.StandardOutput, "generation=");
            var canonicalConfirm = await RunOwnerCommandWithInputAsync(executable, "PURGE" + Environment.NewLine,
                "owner", "purge-confirm", "--control-pipe", fixture.ControlPipe,
                "--purge-id", canonicalPurgeId, "--resource-id", S12T5RecordPurged, "--resource-revision", "1",
                "--target-zone", S12T5Zone, "--target-kind", "memory-or-source",
                "--impacted", string.Join(",", canonicalImpacted),
                "--live-generation", canonicalGeneration,
                "--reason", "S12-T5 synthetic canonical purge reason.").ConfigureAwait(false);
            Require(canonicalConfirm.ExitCode == 0 && canonicalConfirm.StandardOutput.Contains("PURGED ", StringComparison.Ordinal),
                "The canonical record purge did not commit (out=" + canonicalConfirm.StandardOutput.Trim() +
                " err=" + canonicalConfirm.StandardError.Trim() + ").");
            var readPurgedRecord = await S12T5OwnerAsync(executable, fixture.ControlPipe,
                "memory-read", "--resource-id", S12T5RecordPurged).ConfigureAwait(false);
            Require(readPurgedRecord.ExitCode != 0, "A purged canonical record could still be read.");

            // A grant redeemed after B1 makes the consumed-token union in the
            // restore policy observable, not merely asserted.
            var handoff = Path.Combine(fixture.OutputDirectory, "s12t5.grant-handoff");
            var sessionFile = Path.Combine(fixture.OutputDirectory, "s12t5.session-handoff");
            var expiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O", CultureInfo.InvariantCulture);
            var grant = await RunOwnerCommandWithInputAsync(executable, "ISSUE" + Environment.NewLine,
                "owner", "grant", "--control-pipe", fixture.ControlPipe,
                "--resource-ids", S12T5RecordKeep,
                "--operations", "memory.read",
                "--expires-at-utc", expiry,
                "--handoff-file", handoff).ConfigureAwait(false);
            Require(grant.ExitCode == 0 && File.Exists(handoff),
                "The owner could not issue the S12-T5 scoped grant (" + grant.StandardError.Trim() + ").");
            var redeem = await RunOwnerCommandAsync(executable,
                "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
            Require(redeem.ExitCode == 0 && redeem.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
                "The S12-T5 scoped grant could not be redeemed (" + redeem.StandardError.Trim() + ").");

            var backupPost = Path.Combine(fixture.BackupDirectory, "s12t5-post-purge.mbbackup");
            var postBackup = await RunOwnerCommandAsync(executable,
                "owner", "backup", "--control-pipe", fixture.ControlPipe, "--output-file", backupPost).ConfigureAwait(false);
            Require(postBackup.ExitCode == 0 && postBackup.StandardOutput.Contains("BACKUP written", StringComparison.Ordinal),
                "The owner CLI did not write the post-purge S12-T5 backup (stderr=" + postBackup.StandardError.Trim() + ").");
            var postTreeRevision = WorkspaceNumber(await S12T5StatusAsync(executable, fixture.ControlPipe).ConfigureAwait(false), "revision");
            var liveTree = await S12T5TreeAsync(executable, fixture.ControlPipe).ConfigureAwait(false);
            var liveNodeLines = S12T5NodeLines(liveTree);
            Require(liveNodeLines.Count == 6,
                "The retained S12-T5 tree did not hold exactly the root, one folder and four live files (" +
                liveNodeLines.Count.ToString(CultureInfo.InvariantCulture) + ").");
            Require(postBackup.StandardOutput.Contains("workspaceTombstones=1", StringComparison.Ordinal),
                "The post-purge backup did not report the one retained tombstone (" + postBackup.StandardOutput.Trim() + ").");

            // Lock/unlock and a real process restart must reproduce the exact
            // committed state before any restore runs.
            var lockResult = await RunOwnerCommandAsync(executable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockResult.ExitCode == 0, "The owner vault did not lock.");
            await S12T5UnlockAsync(executable, fixture.ControlPipe, passphrase).ConfigureAwait(false);
            Require(S12T5NodeLines(await S12T5TreeAsync(executable, fixture.ControlPipe).ConfigureAwait(false))
                    .SequenceEqual(liveNodeLines), "A lock/unlock changed the committed workspace tree.");
            await StopServiceAsync(live).ConfigureAwait(false);
            live = await StartServiceAsync(executable, fixture.LiveSettingsPath).ConfigureAwait(false);
            await S12T5UnlockAsync(executable, fixture.ControlPipe, passphrase).ConfigureAwait(false);
            Require(S12T5NodeLines(await S12T5TreeAsync(executable, fixture.ControlPipe).ConfigureAwait(false))
                    .SequenceEqual(liveNodeLines), "A restart changed the committed workspace tree.");
            Require((await S12T5StatusAsync(executable, fixture.ControlPipe).ConfigureAwait(false)).Contains("purged=4", StringComparison.Ordinal),
                "A restart lost the durable purge ledger count.");
            Console.WriteLine("PASS lock/unlock and a restart reproduce the tree, tombstones and ledger without resurrecting sessions");

            // Restoring the post-purge backup must reproduce everything the
            // backup holds, with nothing withheld.
            var postRestore = await S12T5RestoreAsync(executable, fixture.ControlPipe, backupPost,
                fixture.PostTarget, passphrase).ConfigureAwait(false);
            Require(postRestore.ExitCode == 0 && postRestore.StandardOutput.Contains("RESTORE written", StringComparison.Ordinal),
                "The post-purge backup did not restore (stderr=" + postRestore.StandardError.Trim() + ").");
            Require(postRestore.StandardOutput.Contains("purgedRecordsWithheld=0", StringComparison.Ordinal) &&
                    postRestore.StandardOutput.Contains("purgedWorkspaceNodesWithheld=0", StringComparison.Ordinal),
                "The post-purge restore withheld state its own backup did not purge (" + postRestore.StandardOutput.Trim() + ").");
            restoredPost = await StartServiceAsync(executable, fixture.PostSettingsPath).ConfigureAwait(false);
            await S12T5UnlockAsync(executable, fixture.PostControlPipe, passphrase).ConfigureAwait(false);
            var postTree = await S12T5TreeAsync(executable, fixture.PostControlPipe).ConfigureAwait(false);
            Require(S12T5NodeLines(postTree).SequenceEqual(liveNodeLines),
                "The restored post-purge workspace tree changed ids, names, parents, revisions or bytes.");
            Require(S12T5TreeRevision(postTree) == postTreeRevision,
                "The restored post-purge workspace revision changed.");
            Require((await S12T5StatusAsync(executable, fixture.PostControlPipe).ConfigureAwait(false)).Contains("purged=4", StringComparison.Ordinal),
                "The restored post-purge vault lost the purge ledger.");
            await VerifyS12T5RetainedStateAsync(executable, fixture, fixture.PostControlPipe, dataset).ConfigureAwait(false);
            Require(postRestore.StandardOutput.Contains("liveGrants=0", StringComparison.Ordinal),
                "The restored vault came back with live grants.");
            await VerifyS12T5TokenStaysConsumedAsync(executable, fixture.PostControlPipe + ".agent",
                handoff, Path.Combine(fixture.OutputDirectory, "s12t5-redeem-post"), "the post-purge backup restore").ConfigureAwait(false);
            Console.WriteLine("PASS the post-purge backup restores ids/tree/bytes/anchors/revisions/tombstones and the ledger exactly");

            // The discriminating case: the older backup has no record of the
            // purge, but the unlocked live installation does. The merged ledger
            // must withhold the purged record and the four purged nodes.
            var olderRestore = await S12T5RestoreAsync(executable, fixture.ControlPipe, backupPre,
                fixture.OlderTarget, passphrase).ConfigureAwait(false);
            Require(olderRestore.ExitCode == 0 && olderRestore.StandardOutput.Contains("RESTORE written", StringComparison.Ordinal),
                "The pre-purge backup did not restore (stderr=" + olderRestore.StandardError.Trim() + ").");
            Require(olderRestore.StandardOutput.Contains("purgedRecordsWithheld=1", StringComparison.Ordinal) &&
                    olderRestore.StandardOutput.Contains("purgedWorkspaceNodesWithheld=4", StringComparison.Ordinal),
                "The older backup restore did not withhold the purged record and nodes (" + olderRestore.StandardOutput.Trim() + ").");
            Require(olderRestore.StandardOutput.Contains("RESTORE PURGE LEDGER re-applied", StringComparison.Ordinal),
                "The withholding was not disclosed as the re-applied purge ledger.");
            Require(WorkspaceNumber(olderRestore.StandardOutput, "consumedTokens") >= 1,
                "Restoring the older backup did not preserve the token the live installation had already consumed.");
            restoredOlder = await StartServiceAsync(executable, fixture.OlderSettingsPath).ConfigureAwait(false);
            await S12T5UnlockAsync(executable, fixture.OlderControlPipe, passphrase).ConfigureAwait(false);
            var olderTree = await S12T5TreeAsync(executable, fixture.OlderControlPipe).ConfigureAwait(false);
            Require(S12T5NodeLines(olderTree).SequenceEqual(liveNodeLines),
                "The restore of the older backup resurrected purged workspace nodes or changed the retained ones.");
            Require(S12T5TreeRevision(olderTree) == dataset.PrePurgeRevision,
                "The filtered older restore did not keep its own committed tree revision.");
            Require((await S12T5StatusAsync(executable, fixture.OlderControlPipe).ConfigureAwait(false)).Contains("purged=4", StringComparison.Ordinal),
                "The older backup restore did not persist the merged purge ledger.");
            await VerifyS12T5RetainedStateAsync(executable, fixture, fixture.OlderControlPipe, dataset).ConfigureAwait(false);
            var olderPurgedRecord = await S12T5OwnerAsync(executable, fixture.OlderControlPipe,
                "memory-read", "--resource-id", S12T5RecordPurged).ConfigureAwait(false);
            Require(olderPurgedRecord.ExitCode != 0, "The older backup restore resurrected the purged canonical record.");
            var olderPurgedNode = await S12T5OwnerAsync(executable, fixture.OlderControlPipe,
                "workspace-read", "--file-id", dataset.PurgedNoteId).ConfigureAwait(false);
            Require(olderPurgedNode.ExitCode != 0, "The older backup restore resurrected a purged workspace file.");
            await VerifyS12T5TokenStaysConsumedAsync(executable, fixture.OlderControlPipe + ".agent",
                handoff, Path.Combine(fixture.OutputDirectory, "s12t5-redeem-older"), "the older backup restore").ConfigureAwait(false);
            Console.WriteLine("PASS restoring the older backup withholds the purged record and four nodes, keeps the token consumed and drops live authority");

            // A locked live vault cannot decrypt its own purge ledger. A backup
            // of that same vault is refused before anything is written, and the
            // documented remedy (unlock, retry) then restores it with the ledger.
            var lockForRestore = await RunOwnerCommandAsync(executable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockForRestore.ExitCode == 0, "The owner vault did not lock before the locked-restore case.");
            var lockedRestore = await S12T5RestoreAsync(executable, fixture.ControlPipe, backupPost,
                fixture.LockedTarget, passphrase).ConfigureAwait(false);
            Require(lockedRestore.ExitCode != 0 &&
                    lockedRestore.StandardError.Contains("restore_requires_unlock", StringComparison.Ordinal),
                "A locked live vault restored a backup of itself instead of failing closed (stderr=" +
                lockedRestore.StandardError.Trim() + ").");
            Require(!Directory.Exists(fixture.LockedTarget),
                "The refused locked-vault restore wrote a target directory.");
            await S12T5UnlockAsync(executable, fixture.ControlPipe, passphrase).ConfigureAwait(false);
            var lockedRetry = await S12T5RestoreAsync(executable, fixture.ControlPipe, backupPost,
                fixture.LockedTarget, passphrase).ConfigureAwait(false);
            Require(lockedRetry.ExitCode == 0 && lockedRetry.StandardOutput.Contains("RESTORE written", StringComparison.Ordinal),
                "Unlocking the live vault did not make the same restore succeed (stderr=" + lockedRetry.StandardError.Trim() + ").");
            restoredLocked = await StartServiceAsync(executable, fixture.LockedSettingsPath).ConfigureAwait(false);
            await S12T5UnlockAsync(executable, fixture.LockedControlPipe, passphrase).ConfigureAwait(false);
            Require(S12T5NodeLines(await S12T5TreeAsync(executable, fixture.LockedControlPipe).ConfigureAwait(false))
                    .SequenceEqual(liveNodeLines),
                "The unlocked retry did not re-apply the backup's purge ledger.");

            await VerifyS12T5RestoreRefusalsAsync(executable, fixture, backupPost, passphrase).ConfigureAwait(false);

            Console.WriteLine("PASS S12-T5 encrypted backup, purge ledger, restart and older-backup withholding");
        }
        finally
        {
            if (restoredLocked is not null)
            {
                await StopServiceAsync(restoredLocked).ConfigureAwait(false);
            }

            if (restoredOlder is not null)
            {
                await StopServiceAsync(restoredOlder).ConfigureAwait(false);
            }

            if (restoredPost is not null)
            {
                await StopServiceAsync(restoredPost).ConfigureAwait(false);
            }

            if (live is not null)
            {
                await StopServiceAsync(live).ConfigureAwait(false);
            }
        }
    }

    private sealed record S12T5Dataset(
        string PurgeFolderId,
        string PurgedNoteId,
        string KeepNoteId,
        string KeepBinaryId,
        string TombstoneId,
        string ReplacementId,
        long PrePurgeRevision);

    private static async Task<S12T5Dataset> WriteS12T5DatasetAsync(string executable, S12T5Fixture fixture)
    {
        var pipe = fixture.ControlPipe;
        var output = fixture.OutputDirectory;
        var purgeNoteBytes = Encoding.UTF8.GetBytes(
            "synthetic S12-T5 purge note " + S12T5PurgeNoteMarker + "\n");
                var tombstoneBytes = Encoding.UTF8.GetBytes("synthetic S12-T5 tombstone body\n");
        var replacementBytes = Encoding.UTF8.GetBytes("synthetic S12-T5 replacement at the freed name\n");

        var mkdirPurge = await S12T5OwnerAsync(executable, pipe,
            "workspace-mkdir", "--parent-id", "root", "--name", S12T5PurgeFolder,
            "--expected-tree-revision", "0").ConfigureAwait(false);
        Require(mkdirPurge.ExitCode == 0, "The purge folder was not created (stderr=" + mkdirPurge.StandardError.Trim() + ").");
        var purgeFolderId = WorkspaceToken(mkdirPurge.StandardOutput, "id");
        var revision = WorkspaceNumber(mkdirPurge.StandardOutput, "revision");

        var mkdirNested = await S12T5OwnerAsync(executable, pipe,
            "workspace-mkdir", "--parent-id", purgeFolderId, "--name", S12T5PurgeNested,
            "--expected-tree-revision", revision).ConfigureAwait(false);
        Require(mkdirNested.ExitCode == 0, "The nested purge folder was not created (stderr=" + mkdirNested.StandardError.Trim() + ").");
        var nestedId = WorkspaceToken(mkdirNested.StandardOutput, "id");
        revision = WorkspaceNumber(mkdirNested.StandardOutput, "revision");

        var purgeNote = await S12T5ImportAsync(executable, fixture, purgeFolderId, S12T5PurgeNote,
            purgeNoteBytes, revision).ConfigureAwait(false);
        revision = purgeNote.TreeRevision;
        var purgeAnchor = await S12T5OwnerAsync(executable, pipe,
            "workspace-anchor-create", "--file-id", purgeNote.FileId,
            "--expected-content-revision", "1",
            "--offset", "0", "--length", S12T5AnchorRange.Length.ToString(CultureInfo.InvariantCulture),
            "--expected-tree-revision", revision).ConfigureAwait(false);
        Require(purgeAnchor.ExitCode == 0 && purgeAnchor.StandardOutput.Contains("state=live", StringComparison.Ordinal),
            "The purged note's anchor was not pinned (stderr=" + purgeAnchor.StandardError.Trim() + ").");
        revision = await S12T5RevisionAsync(executable, pipe).ConfigureAwait(false);

        var purgeDeep = await S12T5ImportAsync(executable, fixture, nestedId, S12T5PurgeDeep, S12T5BinaryBytes, revision).ConfigureAwait(false);
        revision = purgeDeep.TreeRevision;

        var deleteFolderPreview = await S12T5OwnerAsync(executable, pipe,
            "workspace-delete-preview", "--node-id", purgeFolderId).ConfigureAwait(false);
        Require(deleteFolderPreview.ExitCode == 0 && deleteFolderPreview.StandardOutput.Contains("descendants=3", StringComparison.Ordinal),
            "The draft delete preview did not list the folder's three descendants (" + deleteFolderPreview.StandardOutput.Trim() + ").");
        var deleteFolderLine = S12T5DeletePreviewLine(deleteFolderPreview.StandardOutput);
        var deleteFolder = await S12T5OwnerAsync(executable, pipe,
            "workspace-delete", "--node-id", purgeFolderId,
            "--expected-tree-revision", WorkspaceNumber(deleteFolderLine, "revision"),
            "--expected-node-revision", WorkspaceNumber(deleteFolderLine, "nodeRevision"),
            "--preview", WorkspaceToken(deleteFolderLine, "preview")).ConfigureAwait(false);
        Require(deleteFolder.ExitCode == 0 && deleteFolder.StandardOutput.Contains("files=2", StringComparison.Ordinal),
            "The draft group was not tombstoned (stderr=" + deleteFolder.StandardError.Trim() + ").");
        revision = WorkspaceNumber(deleteFolder.StandardOutput, "revision");

        var mkdirKeep = await S12T5OwnerAsync(executable, pipe,
            "workspace-mkdir", "--parent-id", "root", "--name", S12T5KeepFolder,
            "--expected-tree-revision", revision).ConfigureAwait(false);
        Require(mkdirKeep.ExitCode == 0, "The keep folder was not created (stderr=" + mkdirKeep.StandardError.Trim() + ").");
        var keepFolderId = WorkspaceToken(mkdirKeep.StandardOutput, "id");
        revision = WorkspaceNumber(mkdirKeep.StandardOutput, "revision");

        var keepNote = await S12T5ImportAsync(executable, fixture, keepFolderId, S12T5KeepNote, S12T5KeepNoteBytes, revision).ConfigureAwait(false);
        revision = keepNote.TreeRevision;
        var staleAnchor = await S12T5OwnerAsync(executable, pipe,
            "workspace-anchor-create", "--file-id", keepNote.FileId,
            "--expected-content-revision", "1",
            "--offset", "0", "--length", S12T5AnchorRange.Length.ToString(CultureInfo.InvariantCulture),
            "--expected-tree-revision", revision).ConfigureAwait(false);
        Require(staleAnchor.ExitCode == 0 && staleAnchor.StandardOutput.Contains("state=live", StringComparison.Ordinal),
            "The pre-revision anchor was not pinned (stderr=" + staleAnchor.StandardError.Trim() + ").");
        revision = await S12T5RevisionAsync(executable, pipe).ConfigureAwait(false);

        var revise = await S12T5OwnerAsync(executable, pipe,
            "workspace-revise", "--file-id", keepNote.FileId, "--expected-content-revision", "1",
            "--input-file", await S12T5WriteInputAsync(fixture, "s12t5-keep-revision.md", S12T5KeepRevisionBytes).ConfigureAwait(false)).ConfigureAwait(false);
        Require(revise.ExitCode == 0 && revise.StandardOutput.Contains("contentRevision=2", StringComparison.Ordinal),
            "The keep note did not reach revision 2 (stderr=" + revise.StandardError.Trim() + ").");
        revision = WorkspaceNumber(revise.StandardOutput, "revision");
        var liveAnchor = await S12T5OwnerAsync(executable, pipe,
            "workspace-anchor-create", "--file-id", keepNote.FileId,
            "--expected-content-revision", "2",
            "--offset", "0", "--length", S12T5AnchorRange.Length.ToString(CultureInfo.InvariantCulture),
            "--expected-tree-revision", revision).ConfigureAwait(false);
        Require(liveAnchor.ExitCode == 0 && liveAnchor.StandardOutput.Contains("state=live", StringComparison.Ordinal),
            "The current-revision anchor was not pinned (stderr=" + liveAnchor.StandardError.Trim() + ").");
        revision = await S12T5RevisionAsync(executable, pipe).ConfigureAwait(false);
        var anchorList = await S12T5OwnerAsync(executable, pipe,
            "workspace-anchor-list", "--file-id", keepNote.FileId).ConfigureAwait(false);
        Require(anchorList.ExitCode == 0 && anchorList.StandardOutput.Contains("count=2", StringComparison.Ordinal) &&
                anchorList.StandardOutput.Contains("state=live", StringComparison.Ordinal) &&
                anchorList.StandardOutput.Contains("state=stale", StringComparison.Ordinal),
            "The revised note did not keep one live and one stale anchor (" + anchorList.StandardOutput.Trim() + ").");

        var keepBinary = await S12T5ImportAsync(executable, fixture, keepFolderId, S12T5KeepBinary, S12T5BinaryBytes, revision).ConfigureAwait(false);
        revision = keepBinary.TreeRevision;
        var keepEmpty = await S12T5ImportAsync(executable, fixture, keepFolderId, S12T5KeepEmpty, S12T5EmptyBytes, revision).ConfigureAwait(false);
        revision = keepEmpty.TreeRevision;

        var tombstone = await S12T5ImportAsync(executable, fixture, keepFolderId, S12T5Tombstone, tombstoneBytes, revision).ConfigureAwait(false);
        revision = tombstone.TreeRevision;
        var deletePreview = await S12T5OwnerAsync(executable, pipe,
            "workspace-delete-preview", "--node-id", tombstone.FileId).ConfigureAwait(false);
        Require(deletePreview.ExitCode == 0 && deletePreview.StandardOutput.Contains("descendants=0", StringComparison.Ordinal),
            "The tombstone file delete preview failed (stderr=" + deletePreview.StandardError.Trim() + ").");
        var deleteFileLine = S12T5DeletePreviewLine(deletePreview.StandardOutput);
        var deleteFile = await S12T5OwnerAsync(executable, pipe,
            "workspace-delete", "--node-id", tombstone.FileId,
            "--expected-tree-revision", WorkspaceNumber(deleteFileLine, "revision"),
            "--expected-node-revision", WorkspaceNumber(deleteFileLine, "nodeRevision"),
            "--preview", WorkspaceToken(deleteFileLine, "preview")).ConfigureAwait(false);
        Require(deleteFile.ExitCode == 0 && deleteFile.StandardOutput.Contains("files=1", StringComparison.Ordinal),
            "The retained tombstone was not committed (stderr=" + deleteFile.StandardError.Trim() + ").");
        revision = WorkspaceNumber(deleteFile.StandardOutput, "revision");

        var replacement = await S12T5ImportAsync(executable, fixture, keepFolderId, S12T5Replacement, replacementBytes, revision).ConfigureAwait(false);
        revision = replacement.TreeRevision;
        Require(!string.Equals(replacement.FileId, tombstone.FileId, StringComparison.Ordinal),
            "The new file at the tombstoned name reused the deleted identity.");
        var restoreOccupied = await S12T5OwnerAsync(executable, pipe,
            "workspace-restore", "--node-id", tombstone.FileId,
            "--expected-tree-revision", revision).ConfigureAwait(false);
        RequireWorkspaceRefusal(restoreOccupied, WorkspaceErrorCodes.RestoreConflict, "a restore into an occupied path");

        await S12T5MemoryAsync(executable, fixture, S12T5RecordKeep, "S12-T5 synthetic keep record", S12T5KeepBody).ConfigureAwait(false);
        await S12T5MemoryAsync(executable, fixture, S12T5RecordPurged, "S12-T5 synthetic purged record", S12T5PurgedBody).ConfigureAwait(false);

        var prePurgeStatus = await S12T5StatusAsync(executable, pipe).ConfigureAwait(false);
        Require(prePurgeStatus.Contains("deleted=5", StringComparison.Ordinal) &&
                prePurgeStatus.Contains("files=4", StringComparison.Ordinal),
            "The dataset did not hold the expected live files and tombstone (" + prePurgeStatus.Trim() + ").");
        Console.WriteLine("PASS the synthetic workspace holds live/empty/binary originals, two anchor states, a tombstone group and two canonical records");
        return new S12T5Dataset(purgeFolderId, purgeNote.FileId, keepNote.FileId, keepBinary.FileId, tombstone.FileId,
            replacement.FileId, WorkspaceNumber(prePurgeStatus, "revision"));
    }

    private static async Task VerifyS12T5RetainedStateAsync(
        string executable,
        S12T5Fixture fixture,
        string controlPipe,
        S12T5Dataset dataset)
    {
        await VerifyS12T5BytesAsync(executable, fixture, controlPipe, dataset.KeepNoteId, 1, S12T5KeepNoteBytes,
            "the restored original revision 1").ConfigureAwait(false);
        await VerifyS12T5BytesAsync(executable, fixture, controlPipe, dataset.KeepNoteId, 2, S12T5KeepRevisionBytes,
            "the restored revised original revision 2").ConfigureAwait(false);
        await VerifyS12T5BytesAsync(executable, fixture, controlPipe, dataset.KeepNoteId, null, S12T5KeepRevisionBytes,
            "the restored latest revision").ConfigureAwait(false);
        var tree = await S12T5TreeAsync(executable, controlPipe).ConfigureAwait(false);
        Require(S12T5FileLine(tree, dataset.KeepNoteId).Contains("revisions=2", StringComparison.Ordinal),
            "The restored keep note lost its retained revision.");
        var anchors = await S12T5OwnerAsync(executable, controlPipe,
            "workspace-anchor-list", "--file-id", dataset.KeepNoteId).ConfigureAwait(false);
        Require(anchors.ExitCode == 0 && anchors.StandardOutput.Contains("count=2", StringComparison.Ordinal) &&
                anchors.StandardOutput.Contains("state=live", StringComparison.Ordinal) &&
                anchors.StandardOutput.Contains("state=stale", StringComparison.Ordinal),
            "The restored keep note did not keep its live and stale anchors.");
        var tombstones = await S12T5OwnerAsync(executable, controlPipe, "workspace-tombstones").ConfigureAwait(false);
        Require(tombstones.ExitCode == 0 && tombstones.StandardOutput.Contains("TOMBSTONES count=1", StringComparison.Ordinal) &&
                tombstones.StandardOutput.Contains(dataset.TombstoneId, StringComparison.Ordinal),
            "The restored vault lost or changed the retained tombstone (" + tombstones.StandardOutput.Trim() + ").");
        var keepRecord = await S12T5OwnerAsync(executable, controlPipe,
            "memory-read", "--resource-id", S12T5RecordKeep).ConfigureAwait(false);
        Require(keepRecord.ExitCode == 0 && keepRecord.StandardOutput.Contains(S12T5KeepBody, StringComparison.Ordinal),
            "The restored vault lost the canonical record body (stderr=" + keepRecord.StandardError.Trim() + ").");
        var binaryRead = await S12T5OwnerAsync(executable, controlPipe,
            "workspace-read", "--file-id", dataset.KeepNoteId, "--revision", "2").ConfigureAwait(false);
        Require(binaryRead.ExitCode == 0, "The restored keep note revision 2 could not be read.");
        var binaryLine = S12T5FileLine(tree, dataset.KeepBinaryId);
        Require(binaryLine.Contains("sha256=", StringComparison.Ordinal),
            "The restored binary file lost its digest.");
        Require(S12T5NodeLines(tree).Count == 6, "The restored retained tree changed its node count.");
    }

    private static async Task VerifyS12T5RestoreRefusalsAsync(
        string executable,
        S12T5Fixture fixture,
        string goodBackup,
        string passphrase)
    {
        var wrong = await S12T5RestoreAsync(executable, fixture.ControlPipe, goodBackup,
            fixture.WrongTarget, CreateSyntheticPassphrase()).ConfigureAwait(false);
        Require(wrong.ExitCode != 0 && wrong.StandardError.Contains("restore_denied", StringComparison.Ordinal),
            "A wrong passphrase did not refuse the restore (" + wrong.StandardError.Trim() + ").");
        Require(!Directory.Exists(fixture.WrongTarget), "A refused wrong-key restore created its target directory.");

        var missing = await RunOwnerCommandWithInputAsync(executable, string.Empty,
            "owner", "restore", "--control-pipe", fixture.ControlPipe,
            "--backup-file", goodBackup, "--target-directory", fixture.MissingTarget).ConfigureAwait(false);
        Require(missing.ExitCode != 0 && missing.StandardError.Contains("credential_required", StringComparison.Ordinal),
            "A restore without a credential was not refused (" + missing.StandardError.Trim() + ").");
        Require(!Directory.Exists(fixture.MissingTarget), "A restore without a credential created its target directory.");

        var containerBytes = await File.ReadAllBytesAsync(goodBackup).ConfigureAwait(false);
        var tamperedNode = JsonNode.Parse(containerBytes) as JsonObject
            ?? throw new InvalidOperationException("The backup container was not a JSON object.");
        var ciphertext = Convert.FromBase64String(tamperedNode["ciphertext"]!.GetValue<string>());
        ciphertext[0] ^= 0x5A;
        tamperedNode["ciphertext"] = Convert.ToBase64String(ciphertext);
        var tamperedPath = Path.Combine(fixture.BackupDirectory, "s12t5-tampered.mbbackup");
        await File.WriteAllBytesAsync(tamperedPath, Encoding.UTF8.GetBytes(tamperedNode.ToJsonString())).ConfigureAwait(false);
        var tampered = await S12T5RestoreAsync(executable, fixture.ControlPipe, tamperedPath,
            fixture.TamperedTarget, passphrase).ConfigureAwait(false);
        Require(tampered.ExitCode != 0 && tampered.StandardError.Contains("restore_corrupt", StringComparison.Ordinal),
            "A tampered backup was restored (" + tampered.StandardError.Trim() + ").");
        Require(!Directory.Exists(fixture.TamperedTarget), "A refused tampered restore created its target directory.");

        var futureNode = JsonNode.Parse(containerBytes) as JsonObject
            ?? throw new InvalidOperationException("The backup container was not a JSON object.");
        Require(futureNode["formatVersion"]!.GetValue<int>() == 2,
            "The candidate backup did not write the current workspace format version.");
        futureNode["formatVersion"] = 99;
        var futurePath = Path.Combine(fixture.BackupDirectory, "s12t5-future-format.mbbackup");
        await File.WriteAllBytesAsync(futurePath, Encoding.UTF8.GetBytes(futureNode.ToJsonString())).ConfigureAwait(false);
        var future = await S12T5RestoreAsync(executable, fixture.ControlPipe, futurePath,
            fixture.FutureTarget, passphrase).ConfigureAwait(false);
        Require(future.ExitCode != 0 && future.StandardError.Contains("restore_corrupt", StringComparison.Ordinal),
            "An unsupported backup format was restored (" + future.StandardError.Trim() + ").");
        Require(!Directory.Exists(fixture.FutureTarget), "A refused unsupported-format restore created its target directory.");

        var truncatedPath = Path.Combine(fixture.BackupDirectory, "s12t5-truncated.mbbackup");
        await File.WriteAllBytesAsync(truncatedPath, containerBytes[..(containerBytes.Length / 2)]).ConfigureAwait(false);
        var truncated = await S12T5RestoreAsync(executable, fixture.ControlPipe, truncatedPath,
            fixture.TruncatedTarget, passphrase).ConfigureAwait(false);
        Require(truncated.ExitCode != 0 && truncated.StandardError.Contains("restore_corrupt", StringComparison.Ordinal),
            "A truncated backup was restored (" + truncated.StandardError.Trim() + ").");
        Require(!Directory.Exists(fixture.TruncatedTarget), "A refused truncated restore created its target directory.");
        Console.WriteLine("PASS wrong-key, missing-credential, tampered, truncated and unsupported backups refuse with no partial target");
    }

    private static async Task S12T5UnlockAsync(string executable, string pipe, string passphrase)
    {
        var result = await RunOwnerCommandWithInputAsync(executable, passphrase + Environment.NewLine,
            "owner", "unlock", "--control-pipe", pipe).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
            "The passphrase did not unlock the S12-T5 vault (stderr=" + result.StandardError.Trim() + ").");
    }

    private static async Task VerifyS12T5BytesAsync(
        string executable,
        S12T5Fixture fixture,
        string controlPipe,
        string fileId,
        long? revision,
        byte[] expected,
        string label)
    {
        var outputPath = Path.Combine(fixture.OutputDirectory, "s12t5-read-" + Guid.NewGuid().ToString("N"));
        var arguments = new List<object> { "workspace-read", "--file-id", fileId, "--output-file", outputPath };
        if (revision is not null)
        {
            arguments.Add("--revision");
            arguments.Add(revision.Value);
        }

        var result = await S12T5OwnerAsync(executable, controlPipe, arguments.ToArray()).ConfigureAwait(false);
        Require(result.ExitCode == 0, "The read of " + label + " failed (stderr=" + result.StandardError.Trim() + ").");
        RequireBytes(await File.ReadAllBytesAsync(outputPath).ConfigureAwait(false), expected, label);
        File.Delete(outputPath);
    }

    /// <summary>
    /// Redeems the same handoff again against a restored vault's agent
    /// channel. A token the live installation already consumed must be
    /// refused as used, never as unknown, so the restored vault's consumed
    /// verifier set is the live union.
    /// </summary>
    private static async Task VerifyS12T5TokenStaysConsumedAsync(
        string executable,
        string agentPipe,
        string handoff,
        string sessionFile,
        string label)
    {
        var replay = await RunOwnerCommandAsync(executable,
            "owner", "redeem", "--agent-pipe", agentPipe,
            "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
        Require(replay.ExitCode != 0 && replay.StandardError.Contains("token_used", StringComparison.Ordinal) &&
                !replay.StandardError.Contains("token_unknown", StringComparison.Ordinal),
            "The token consumed before the backup was redeemable again after " + label +
            " (stderr=" + replay.StandardError.Trim() + ").");
    }

    private static Task<CommandResult> S12T5OwnerAsync(string executable, string pipe, params object[] arguments)
    {
        var all = new List<string>
        {
            "owner",
            Convert.ToString(arguments[0], CultureInfo.InvariantCulture) ?? string.Empty,
            "--control-pipe",
            pipe
        };
        all.AddRange(arguments.Skip(1).Select(value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
        return RunOwnerCommandAsync(executable, all.ToArray());
    }

    private static async Task<string> S12T5TreeAsync(string executable, string pipe)
    {
        var result = await S12T5OwnerAsync(executable, pipe, "workspace-tree").ConfigureAwait(false);
        Require(result.ExitCode == 0, "The owner workspace-tree failed (stderr=" + result.StandardError.Trim() + ").");
        return result.StandardOutput;
    }

    private static async Task<string> S12T5StatusAsync(string executable, string pipe)
    {
        var result = await S12T5OwnerAsync(executable, pipe, "workspace-status").ConfigureAwait(false);
        Require(result.ExitCode == 0, "The owner workspace-status failed (stderr=" + result.StandardError.Trim() + ").");
        return result.StandardOutput;
    }

    private static async Task<long> S12T5RevisionAsync(string executable, string pipe) =>
        WorkspaceNumber(await S12T5StatusAsync(executable, pipe).ConfigureAwait(false), "revision");

    private static async Task<(string FileId, long TreeRevision)> S12T5ImportAsync(
        string executable,
        S12T5Fixture fixture,
        string parentId,
        string name,
        byte[] content,
        long treeRevision)
    {
        var inputPath = await S12T5WriteInputAsync(fixture, "s12t5-input-" + Guid.NewGuid().ToString("N"), content).ConfigureAwait(false);
        var result = await S12T5OwnerAsync(executable, fixture.ControlPipe,
            "workspace-import", "--parent-id", parentId, "--name", name,
            "--input-file", inputPath, "--expected-tree-revision", treeRevision).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("IMPORT id=", StringComparison.Ordinal),
            "The import of " + name + " failed (stderr=" + result.StandardError.Trim() + ").");
        return (WorkspaceToken(result.StandardOutput, "id"), WorkspaceNumber(result.StandardOutput, "revision"));
    }

    private static async Task S12T5MemoryAsync(
        string executable,
        S12T5Fixture fixture,
        string resourceId,
        string title,
        string body)
    {
        var document = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "lesson",
            author = "user",
            creationMethod = "directWrite",
            evidence = "unverified",
            level = "l1",
            title,
            body,
            sources = Array.Empty<object>(),
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = DateTimeOffset.UtcNow,
            applicability = (object?)null,
        });
        var inputPath = await S12T5WriteInputAsync(fixture, "s12t5-record-" + resourceId + ".json", document).ConfigureAwait(false);
        var result = await S12T5OwnerAsync(executable, fixture.ControlPipe,
            "memory-create", "--resource-id", resourceId, "--zone-id", S12T5Zone,
            "--input-file", inputPath).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
            "The canonical record " + resourceId + " was not stored (stderr=" + result.StandardError.Trim() + ").");
    }

    private static async Task<string> S12T5WriteInputAsync(S12T5Fixture fixture, string name, byte[] content)
    {
        var path = Path.Combine(fixture.OutputDirectory, name);
        await File.WriteAllBytesAsync(path, content).ConfigureAwait(false);
        return path;
    }

    private static Task<CommandResult> S12T5RestoreAsync(
        string executable,
        string pipe,
        string backupFile,
        string target,
        string credential) =>
        RunOwnerCommandWithInputAsync(executable, credential + Environment.NewLine,
            "owner", "restore", "--control-pipe", pipe,
            "--backup-file", backupFile, "--target-directory", target);

    private static long S12T5TreeRevision(string tree) =>
        WorkspaceNumber(tree.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .First(line => line.StartsWith("WORKSPACE revision=", StringComparison.Ordinal)), "revision");

    private static List<string> S12T5NodeLines(string tree) =>
        tree.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("FOLDER ", StringComparison.Ordinal) ||
                           line.StartsWith("FILE ", StringComparison.Ordinal))
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();

    private static string S12T5FileLine(string tree, string fileId) =>
        tree.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .First(line => line.StartsWith("FILE id=" + fileId + " ", StringComparison.Ordinal));

    private static string S12T5DeletePreviewLine(string output) =>
        output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .First(line => line.StartsWith("DELETE PREVIEW ", StringComparison.Ordinal));

    private static string S12T5LineValue(string output, string marker)
    {
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        Require(start >= 0, "The command output did not carry " + marker + ".");
        start += marker.Length;
        var end = output.IndexOfAny([' ', '\r', '\n'], start);
        return (end < 0 ? output[start..] : output[start..end]).Trim();
    }

    private static string[] S12T5ImpactedTokens(string output) =>
        output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("impacted ", StringComparison.Ordinal))
            .Select(line => line["impacted ".Length..].Trim())
            .ToArray();

    private static void RequireNoS12T5Plaintext(byte[] content, IReadOnlyList<string> markers, string label)
    {
        foreach (var marker in markers)
        {
            var bytes = Encoding.UTF8.GetBytes(marker);
            Require(content.AsSpan().IndexOf(bytes) < 0, "The " + label + " held the plaintext marker " + marker + ".");
        }
    }

    /// <summary>
    /// S12-T5 transition proof with the actual released 1.1 payload already in
    /// the repository: a real format-1 encrypted backup written by the 1.1 CLI
    /// is restored by this candidate, and both the old client on the new host
    /// and the new client on the old host are refused at the protocol boundary
    /// before any private state is read or written. Nothing is installed and no
    /// released setup binary is executed.
    /// </summary>
    public static async Task RunS12T5LegacyAsync(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S12-T5 legacy smoke is Windows-only.");
        }

        var repositoryRoot = SmokeFixture.FindRepositoryRoot();
        var payloadRoot = Path.Combine(repositoryRoot, "runtime", "release", "MetaBrain-1.1.0-win-x64");
        var legacyService = Path.Combine(payloadRoot, "service", "MetaBrain.Connections.exe");
        var legacyCli = Path.Combine(payloadRoot, "cli", "MetaBrain.Cli.exe");
        Require(File.Exists(legacyService) && File.Exists(legacyCli),
            "The released 1.1 payload needed for the transition proof is not present under runtime/release.");

        using var fixture = S12T5LegacyFixture.Create();
        RunningService? legacy = null;
        RunningService? candidate = null;
        RunningService? restored = null;
        try
        {
            legacy = await S12T5StartReleasedServiceAsync(legacyService, fixture.LegacySettingsPath).ConfigureAwait(false);
            var passphrase = CreateSyntheticPassphrase();
            var provision = await RunOwnerCommandWithInputAsync(legacyCli,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.LegacyControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The released 1.1 CLI did not provision its synthetic vault (stderr=" + provision.StandardError.Trim() + ").");
            var created = await RunOwnerCommandAsync(legacyCli,
                "owner", "memory-create", "--control-pipe", fixture.LegacyControlPipe,
                "--resource-id", S12T5LegacyRecord, "--zone-id", S12T5Zone,
                "--body", S12T5LegacyBody, "--title", "S12-T5 legacy record").ConfigureAwait(false);
            Require(created.ExitCode == 0 && created.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
                "The released 1.1 CLI did not store the format-1 record (stderr=" + created.StandardError.Trim() + ").");
            var legacyBackup = Path.Combine(fixture.BackupDirectory, "s12t5-legacy-1.1.mbbackup");
            var backedUp = await RunOwnerCommandAsync(legacyCli,
                "owner", "backup", "--control-pipe", fixture.LegacyControlPipe, "--output-file", legacyBackup).ConfigureAwait(false);
            Require(backedUp.ExitCode == 0 && backedUp.StandardOutput.Contains("BACKUP written", StringComparison.Ordinal),
                "The released 1.1 CLI did not write its backup (stderr=" + backedUp.StandardError.Trim() + ").");
            using (var container = JsonDocument.Parse(await File.ReadAllBytesAsync(legacyBackup).ConfigureAwait(false)))
            {
                Require(container.RootElement.GetProperty("formatVersion").GetInt32() == 1,
                    "The released 1.1 service did not write a format-1 backup.");
            }

            // The new client must not talk to the old host, and the old client
            // must not talk to the new host; neither refusal writes a file.
            var newOnOld = Path.Combine(fixture.BackupDirectory, "s12t5-new-on-old.mbbackup");
            var candidateAgainstLegacy = await RunOwnerCommandAsync(executable,
                "owner", "backup", "--control-pipe", fixture.LegacyControlPipe, "--output-file", newOnOld).ConfigureAwait(false);
            Require(candidateAgainstLegacy.ExitCode != 0 && !File.Exists(newOnOld),
                "The protocol-2 candidate CLI was served by the protocol-1 host.");
            Require(candidateAgainstLegacy.StandardError.Contains("unsupported_protocol", StringComparison.Ordinal),
                "The candidate did not name the protocol refusal against the 1.1 host (stderr=" +
                candidateAgainstLegacy.StandardError.Trim() + ").");

            candidate = await StartServiceAsync(executable, fixture.CandidateSettingsPath).ConfigureAwait(false);
            var oldOnNew = Path.Combine(fixture.BackupDirectory, "s12t5-old-on-new.mbbackup");
            var legacyAgainstCandidate = await RunOwnerCommandAsync(legacyCli,
                "owner", "backup", "--control-pipe", fixture.CandidateControlPipe, "--output-file", oldOnNew).ConfigureAwait(false);
            Require(legacyAgainstCandidate.ExitCode != 0 && !File.Exists(oldOnNew),
                "The protocol-1 CLI was served by the protocol-2 host.");
            Require((legacyAgainstCandidate.StandardOutput + legacyAgainstCandidate.StandardError)
                    .Contains("protocol", StringComparison.OrdinalIgnoreCase),
                "The 1.1 CLI did not report a protocol refusal (" +
                (legacyAgainstCandidate.StandardOutput + legacyAgainstCandidate.StandardError).Trim() + ").");
            Console.WriteLine("PASS old/new protocol boundaries refuse both directions before any private operation");

            var restoredResult = await S12T5RestoreAsync(executable, fixture.CandidateControlPipe, legacyBackup,
                fixture.RestoredTarget, passphrase).ConfigureAwait(false);
            Require(restoredResult.ExitCode == 0 && restoredResult.StandardOutput.Contains("RESTORE written", StringComparison.Ordinal),
                "The candidate did not restore the released format-1 backup (stderr=" + restoredResult.StandardError.Trim() + ").");
            Require(restoredResult.StandardOutput.Contains("workspaceNodes=0", StringComparison.Ordinal),
                "The format-1 restore claimed a workspace its backup never held (" + restoredResult.StandardOutput.Trim() + ").");

            restored = await StartServiceAsync(executable, fixture.RestoredSettingsPath).ConfigureAwait(false);
            await S12T5UnlockAsync(executable, fixture.RestoredControlPipe, passphrase).ConfigureAwait(false);
            var record = await S12T5OwnerAsync(executable, fixture.RestoredControlPipe,
                "memory-read", "--resource-id", S12T5LegacyRecord).ConfigureAwait(false);
            Require(record.ExitCode == 0 && record.StandardOutput.Contains(S12T5LegacyBody, StringComparison.Ordinal),
                "The released format-1 record body was not restored (stderr=" + record.StandardError.Trim() + ").");
            var restoredStatus = await S12T5StatusAsync(executable, fixture.RestoredControlPipe).ConfigureAwait(false);
            Require(restoredStatus.Contains("state=uninitialized", StringComparison.Ordinal) ||
                    restoredStatus.Contains("workspace_uninitialized", StringComparison.Ordinal),
                "The format-1 restore invented a workspace (" + restoredStatus.Trim() + ").");
            var restoredTree = await S12T5OwnerAsync(executable, fixture.RestoredControlPipe, "workspace-tree").ConfigureAwait(false);
            Require(!restoredTree.StandardOutput.Contains("FOLDER ", StringComparison.Ordinal) &&
                    !restoredTree.StandardOutput.Contains("FILE ", StringComparison.Ordinal),
                "The format-1 restore materialized workspace nodes.");
            Console.WriteLine("PASS the released 1.1 format-1 backup restores records and truthfully no workspace");
        }
        finally
        {
            if (restored is not null)
            {
                await StopServiceAsync(restored).ConfigureAwait(false);
            }

            if (candidate is not null)
            {
                await StopServiceAsync(candidate).ConfigureAwait(false);
            }

            if (legacy is not null)
            {
                await StopServiceAsync(legacy).ConfigureAwait(false);
            }
        }
    }

    private static async Task<RunningService> S12T5StartReleasedServiceAsync(string executable, string settingsPath)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("serve-console");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(settingsPath);
        var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The released 1.1 service did not start.");
        }

        process.StandardInput.Close();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputDrain = DrainS12T5ServiceOutputAsync(process.StandardOutput, ready);
        var standardErrorDrain = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExitAsync();
        try
        {
            var startup = await Task.WhenAny(ready.Task, exited).WaitAsync(ServiceReadyTimeout).ConfigureAwait(false);
            if (startup == exited)
            {
                var error = await standardErrorDrain.ConfigureAwait(false);
                throw new InvalidOperationException("The released 1.1 service exited before READY (stderr bytes=" + error.Length + ").");
            }

            await ready.Task.ConfigureAwait(false);
            return new RunningService(process, outputDrain, standardErrorDrain);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync().WaitAsync(CleanupTimeout).ConfigureAwait(false);
                await Task.WhenAll(outputDrain, standardErrorDrain).WaitAsync(CleanupTimeout).ConfigureAwait(false);
            }
            catch
            {
            }

            process.Dispose();
            throw;
        }
    }

    private static async Task<string> DrainS12T5ServiceOutputAsync(StreamReader reader, TaskCompletionSource<bool> ready)
    {
        var output = new StringBuilder();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            output.AppendLine(line);
            if (line.StartsWith("READY protocol=", StringComparison.Ordinal))
            {
                ready.TrySetResult(true);
            }
        }

        return output.ToString();
    }


    /// <summary>
    /// S12-T5 installer preflight proof on project-local roots. Every case is
    /// refused before the installer stops a host or writes into the install or
    /// data roots; the legacy WPF layout, an unknown/mixed component
    /// registration, a data root owned by another install, an unowned data
    /// root that already holds Meta Brain data, overlapping roots and an
    /// endpoint speaking another protocol are all covered. The payload is the
    /// real candidate build output, and the old layout is produced by the
    /// released 1.1 installer from the extracted release payload, so the
    /// pre-1.2 version refusal runs against actual 1.1 files and a live 1.1
    /// endpoint. No host service registration, ACL or registry is touched
    /// because no case reaches the install commit.
    /// </summary>
    public static async Task RunS12T5InstallerPreflightAsync(string installer)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S12-T5 installer preflight smoke is Windows-only.");
        }

        using var fixture = S12T5InstallFixture.Create();
        fixture.ReplaceWithRealCandidatePayload(installer);

        // 1. A root-level 1.1 WPF marker is refused before anything is written.
        var legacyInstall = Path.Combine(fixture.CasesRoot, "legacy-wpf", "install");
        var legacyData = Path.Combine(fixture.CasesRoot, "legacy-wpf", "data");
        Directory.CreateDirectory(legacyInstall);
        await File.WriteAllTextAsync(Path.Combine(legacyInstall, "MetaBrain.OwnerUi.exe"), "synthetic legacy marker", new UTF8Encoding(false)).ConfigureAwait(false);
        var legacyBefore = S12T5DirectoryFingerprint(legacyInstall);
        await S12T5RequireInstallerRefusalAsync(installer, fixture,
            legacyInstall, legacyData, "legacy WPF root", "1.1 WPF application").ConfigureAwait(false);
        Require(S12T5DirectoryFingerprint(legacyInstall) == legacyBefore,
            "The refused legacy-WPF case changed the install root.");
        Require(!Directory.Exists(legacyData), "The refused legacy-WPF case created its data root.");

        // 2. An unknown/mixed component registration is refused.
        var mixedInstall = Path.Combine(fixture.CasesRoot, "mixed", "install");
        var mixedData = Path.Combine(fixture.CasesRoot, "mixed", "data");
        Directory.CreateDirectory(mixedInstall);
        Directory.CreateDirectory(mixedData);
        await S12T5WriteInstallStateAsync(mixedInstall, mixedData, new[] { "cli", "rogue-component" }).ConfigureAwait(false);
        var mixedBefore = S12T5DirectoryFingerprint(mixedInstall);
        await S12T5RequireInstallerRefusalAsync(installer, fixture,
            mixedInstall, mixedData, "unknown component", "unknown or mixed").ConfigureAwait(false);
        Require(S12T5DirectoryFingerprint(mixedInstall) == mixedBefore,
            "The refused mixed-component case changed the install root.");
        Require(Directory.GetFileSystemEntries(mixedData).Length == 0,
            "The refused mixed-component case wrote into the data root.");

        // 2c. Naming the removed Owner UI component is refused with the
        // support-transition message before any file is written.
        var unsupportedRoot = Path.Combine(fixture.CasesRoot, "owner-ui-component", "install");
        var unsupported = await RunOwnerCommandAsync(installer,
            "install", "--payload", fixture.PayloadDirectory,
            "--install-root", unsupportedRoot,
            "--data-root", Path.Combine(fixture.CasesRoot, "owner-ui-component", "data"),
            "--component", "owner-ui").ConfigureAwait(false);
        Require(unsupported.ExitCode != 0 &&
                unsupported.StandardError.Contains("frozen 1.1.0 WPF product", StringComparison.Ordinal),
            "The installer accepted the removed Owner UI component (exit=" +
            unsupported.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + unsupported.StandardError.Trim() + ").");
        Require(!Directory.Exists(unsupportedRoot),
            "The refused Owner UI component case created its install root.");

        // 2b. A different release candidate registered for another frontend
        // is refused (mixed candidate).
        var candidateInstall = Path.Combine(fixture.CasesRoot, "candidate-mix", "install");
        var candidateData = Path.Combine(fixture.CasesRoot, "candidate-mix", "data");
        Directory.CreateDirectory(candidateInstall);
        Directory.CreateDirectory(candidateData);
        await File.WriteAllTextAsync(Path.Combine(candidateInstall, "install-state.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            installedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            dataRoot = candidateData,
            productVersion = MetaBrain.Connections.ProductInfo.Version,
            components = new[] { "mcp" },
            candidateIdentity = "another-release",
        }), new UTF8Encoding(false)).ConfigureAwait(false);
        var candidateBefore = S12T5DirectoryFingerprint(candidateInstall);
        var mixedCandidate = await RunOwnerCommandAsync(installer,
            "install", "--payload", fixture.PayloadDirectory,
            "--install-root", candidateInstall, "--data-root", candidateData,
            "--component", "cli", "--candidate-id", "legacy").ConfigureAwait(false);
        Require(mixedCandidate.ExitCode != 0 &&
                mixedCandidate.StandardError.Contains("different release candidate", StringComparison.Ordinal),
            "The installer did not refuse the mixed release candidate (exit=" +
            mixedCandidate.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + mixedCandidate.StandardError.Trim() + ").");
        Require(S12T5DirectoryFingerprint(candidateInstall) == candidateBefore,
            "The refused mixed-candidate case changed the install root.");

        // 3. A data root owned by a different install root is refused.
        var ownedInstall = Path.Combine(fixture.CasesRoot, "owned-data", "install");
        var ownedData = Path.Combine(fixture.CasesRoot, "owned-data", "data");
        Directory.CreateDirectory(ownedInstall);
        Directory.CreateDirectory(ownedData);
        var otherInstall = Path.Combine(fixture.CasesRoot, "other-install");
        Directory.CreateDirectory(otherInstall);
        var ownerPath = Path.Combine(ownedData, "data-root-owner.json");
        await File.WriteAllTextAsync(ownerPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            installRoot = otherInstall,
            candidateIdentity = "legacy",
            productVersion = "1.1.0",
            recordedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        }), new UTF8Encoding(false)).ConfigureAwait(false);
        var ownerBytesBefore = await File.ReadAllBytesAsync(ownerPath).ConfigureAwait(false);
        await S12T5RequireInstallerRefusalAsync(installer, fixture,
            ownedInstall, ownedData, "foreign data-root owner", "belongs to the installation").ConfigureAwait(false);
        Require((await File.ReadAllBytesAsync(ownerPath).ConfigureAwait(false)).AsSpan().SequenceEqual(ownerBytesBefore),
            "The refused foreign-data-root case rewrote the ownership marker.");
        Require(Directory.GetFileSystemEntries(ownedInstall).Length == 0,
            "The refused foreign-data-root case wrote into the install root.");

        // 4. An unowned data root already holding Meta Brain data is refused.
        var dataInstall = Path.Combine(fixture.CasesRoot, "existing-data", "install");
        var existingData = Path.Combine(fixture.CasesRoot, "existing-data", "data");
        Directory.CreateDirectory(dataInstall);
        Directory.CreateDirectory(Path.Combine(existingData, "vault"));
        await S12T5RequireInstallerRefusalAsync(installer, fixture,
            dataInstall, existingData, "unowned Meta Brain data", "already contains Meta Brain data").ConfigureAwait(false);
        Require(Directory.GetFileSystemEntries(dataInstall).Length == 0 &&
                !Directory.EnumerateFileSystemEntries(Path.Combine(existingData, "vault")).Any(),
            "The refused existing-data case wrote into its synthetic roots.");

        // 5. Overlapping roots are refused before any directory is created.
        var overlapRoot = Path.Combine(fixture.CasesRoot, "overlap");
        var overlapInstall = Path.Combine(overlapRoot, "install");
        await S12T5RequireInstallerRefusalAsync(installer, fixture,
            overlapInstall, Path.Combine(overlapInstall, "data"), "overlapping roots", "must not live inside").ConfigureAwait(false);
        Require(!Directory.Exists(overlapInstall), "The refused overlapping-roots case created the install root.");

        // 6. A live endpoint speaking another protocol revision is refused
        // before the running host is stopped. The host is the actual released
        // 1.1 service on a synthetic data root.
        var repositoryRoot = SmokeFixture.FindRepositoryRoot();
        var legacyService = Path.Combine(repositoryRoot, "runtime", "release", "MetaBrain-1.1.0-win-x64", "service", "MetaBrain.Connections.exe");
        Require(File.Exists(legacyService), "The released 1.1 service needed for the endpoint preflight is missing.");
        var endpointInstall = Path.Combine(fixture.CasesRoot, "endpoint", "install");
        var endpointData = Path.Combine(fixture.CasesRoot, "endpoint", "data");
        Directory.CreateDirectory(Path.Combine(endpointInstall, "service"));
        Directory.CreateDirectory(endpointData);
        File.WriteAllText(Path.Combine(endpointInstall, "service", "MetaBrain.Connections.exe"),
            "synthetic placeholder", new UTF8Encoding(false));
        var endpointPipe = "MetaBrainS12T5InstallEndpoint-" + Guid.NewGuid().ToString("N");
        S12T5Fixture.WriteSettingsForTest(endpointData, endpointPipe);
        await File.WriteAllTextAsync(Path.Combine(endpointInstall, "install-state.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            installedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            dataRoot = endpointData,
            productVersion = MetaBrain.Connections.ProductInfo.Version,
            components = new[] { "cli" },
            candidateIdentity = "legacy",
        }), new UTF8Encoding(false)).ConfigureAwait(false);
        var endpointInstallBefore = S12T5DirectoryFingerprint(endpointInstall);
        var oldHost = await S12T5StartReleasedServiceAsync(legacyService,
            Path.Combine(endpointData, "service-settings.json")).ConfigureAwait(false);
        var endpointDataBefore = S12T5DirectoryFingerprint(endpointData);
        try
        {
            await S12T5RequireInstallerRefusalAsync(installer, fixture,
                endpointInstall, endpointData, "foreign protocol endpoint", "different protocol revision").ConfigureAwait(false);
            Require(!oldHost.Process.HasExited,
                "The refused endpoint preflight stopped the older host.");
        }
        finally
        {
            await StopServiceAsync(oldHost).ConfigureAwait(false);
        }

        Require(S12T5DirectoryFingerprint(endpointInstall) == endpointInstallBefore,
            "The refused endpoint case changed the install root.");
        Require(S12T5DirectoryFingerprint(endpointData) == endpointDataBefore,
            "The refused endpoint case wrote into the data root.");
        Console.WriteLine("PASS installer preflight refuses legacy/mixed/foreign-data/unowned-data/overlap and a different-protocol live host before any root or process change");

        // 6b. The real installed 1.1 layout, created by the released 1.1
        // installer from the extracted payload, is refused in place before the
        // live 1.1 host is stopped and without changing its files or data.
        var releasedRoot = Path.Combine(repositoryRoot, "runtime", "release", "MetaBrain-1.1.0-win-x64");
        var releasedInstaller = Path.Combine(releasedRoot, "installer", "MetaBrain.Installer.exe");
        Require(File.Exists(releasedInstaller),
            "The released 1.1 installer needed for the installed-layout refusal is missing.");
        var realInstall = Path.Combine(fixture.CasesRoot, "real-1.1", "install");
        var realData = Path.Combine(fixture.CasesRoot, "real-1.1", "data");
        var releasedInstall = await RunOwnerCommandAsync(releasedInstaller,
            "install", "--payload", releasedRoot,
            "--install-root", realInstall, "--data-root", realData,
            "--component", "all").ConfigureAwait(false);
        Require(releasedInstall.ExitCode == 0 &&
                releasedInstall.StandardOutput.Contains("INSTALLED", StringComparison.Ordinal) &&
                releasedInstall.StandardOutput.Contains("product version=1.1.0", StringComparison.Ordinal),
            "The released 1.1 installer did not create its own layout (exit=" +
            releasedInstall.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" +
            releasedInstall.StandardError.Trim() + ").");
        using (var releasedState = JsonDocument.Parse(
                   await File.ReadAllBytesAsync(Path.Combine(realInstall, "install-state.json")).ConfigureAwait(false)))
        {
            Require(releasedState.RootElement.GetProperty("productVersion").GetString() == "1.1.0",
                "The released 1.1 installer did not record its own product version.");
        }
        foreach (var relative in new[]
                 {
                     "cli/MetaBrain.Cli.exe", "service/MetaBrain.Connections.exe",
                     "owner-ui/MetaBrain.OwnerUi.exe", "mcp/MetaBrain.Mcp.exe"
                 })
        {
            Require(File.Exists(Path.Combine(realInstall, relative)),
                "The released 1.1 installation is missing " + relative + ".");
        }

        var releasedHost = await S12T5StartReleasedServiceAsync(
            Path.Combine(realInstall, "service", "MetaBrain.Connections.exe"),
            Path.Combine(realData, "service-settings.json")).ConfigureAwait(false);
        try
        {
            var realInstallBefore = S12T5DirectoryFingerprint(realInstall);
            var realDataBefore = S12T5DirectoryFingerprint(realData);
            var refusedReal = await RunOwnerCommandAsync(installer,
                "install", "--payload", fixture.PayloadDirectory,
                "--install-root", realInstall, "--data-root", realData,
                "--component", "all").ConfigureAwait(false);
            Require(refusedReal.ExitCode != 0 &&
                    refusedReal.StandardError.Contains("1.1.0 installation", StringComparison.Ordinal) &&
                    refusedReal.StandardError.Contains("separate install root", StringComparison.Ordinal) &&
                    refusedReal.StandardError.Contains("distinct control/agent endpoints", StringComparison.Ordinal) &&
                    refusedReal.StandardError.Contains("stopped nothing and changed no files", StringComparison.Ordinal),
                "The current installer did not refuse the real installed 1.1 layout with its separate-root remedy (exit=" +
                refusedReal.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" +
                refusedReal.StandardError.Trim() + ").");
            Require(!releasedHost.Process.HasExited,
                "The refused real 1.1 layout stopped its live 1.1 host.");
            Require(S12T5DirectoryFingerprint(realInstall) == realInstallBefore,
                "The refused real 1.1 layout changed its install root.");
            Require(S12T5DirectoryFingerprint(realData) == realDataBefore,
                "The refused real 1.1 layout wrote into its data root.");
        }
        finally
        {
            await StopServiceAsync(releasedHost).ConfigureAwait(false);
        }

        Console.WriteLine("PASS the current installer refuses the real installed 1.1 layout and its live 1.1 endpoint before any file, data or process change");

        // 7. A separate install root with its own data root and endpoints is
        // the supported transition and completes without starting a host.
        var safeInstall = Path.Combine(fixture.CasesRoot, "safe", "install");
        var safeData = Path.Combine(fixture.CasesRoot, "safe", "data");
        var safeControl = "MetaBrainS12T5SafeControl-" + Guid.NewGuid().ToString("N");
        var safeAgent = "MetaBrainS12T5SafeAgent-" + Guid.NewGuid().ToString("N");
        var safe = await RunOwnerCommandAsync(installer,
            "install", "--payload", fixture.PayloadDirectory,
            "--install-root", safeInstall, "--data-root", safeData,
            "--component", "all", "--control-pipe", safeControl, "--agent-pipe", safeAgent).ConfigureAwait(false);
        Require(safe.ExitCode == 0 && safe.StandardOutput.Contains("INSTALLED", StringComparison.Ordinal),
            "The separate candidate install was refused (" + safe.StandardError.Trim() + ").");
        Require(safe.StandardOutput.Contains("product version=" + MetaBrain.Connections.ProductInfo.Version, StringComparison.Ordinal),
            "The separate install did not report the candidate product version (" + safe.StandardOutput.Trim() + ").");
        Require(safe.StandardOutput.Contains("control pipe=" + safeControl, StringComparison.Ordinal) &&
                safe.StandardOutput.Contains("agent pipe=" + safeAgent, StringComparison.Ordinal),
            "The separate install did not use its own endpoints.");
        foreach (var relative in new[]
                 {
                     "cli/MetaBrain.Cli.exe", "service/MetaBrain.Connections.exe",
                     "mcp/MetaBrain.Mcp.exe",
                     "installer/MetaBrain.Installer.exe", "install-state.json"
                 })
        {
            Require(File.Exists(Path.Combine(safeInstall, relative)),
                "The separate install did not write " + relative + ".");
        }

        Require(!Directory.Exists(Path.Combine(safeInstall, "owner-ui")),
            "The CLI+MCP install created an Owner UI component directory.");

        foreach (var relative in new[] { "service-settings.json", "data-root-owner.json" })
        {
            Require(File.Exists(Path.Combine(safeData, relative)),
                "The separate install did not write " + relative + ".");
        }

        Require(!Directory.Exists(Path.Combine(safeData, "vault")) &&
                !Directory.Exists(Path.Combine(safeData, "workspace")) &&
                !Directory.Exists(Path.Combine(safeData, "backups")),
            "The separate install created vault/workspace/backup state the owner did not ask for.");
        Require(safe.StandardOutput.Contains("vault state=uninitialized", StringComparison.Ordinal),
            "The separate install did not report an uninitialized vault.");
        Console.WriteLine("PASS a separate candidate install root with its own data root and endpoints completes without starting a host or creating vault state");
    }

    private static async Task S12T5RequireInstallerRefusalAsync(
        string installer,
        S12T5InstallFixture fixture,
        string installRoot,
        string dataRoot,
        string label,
        string expectedText)
    {
        var result = await RunOwnerCommandAsync(installer,
            "install", "--payload", fixture.PayloadDirectory,
            "--install-root", installRoot, "--data-root", dataRoot,
            "--component", "cli").ConfigureAwait(false);
        Require(result.ExitCode != 0 && result.StandardError.Contains(expectedText, StringComparison.Ordinal),
            "The installer did not refuse the " + label + " case with '" + expectedText + "' (exit=" +
            result.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + result.StandardError.Trim() + ").");
    }

    private static Task S12T5WriteInstallStateAsync(string installRoot, string dataRoot, string[] components) =>
        File.WriteAllTextAsync(
            Path.Combine(installRoot, "install-state.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                installedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                dataRoot,
                productVersion = MetaBrain.Connections.ProductInfo.Version,
                components,
                candidateIdentity = "legacy",
            }),
            new UTF8Encoding(false));

    private static string S12T5DirectoryFingerprint(string directory) =>
        Directory.Exists(directory)
            ? string.Join("|", Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => path + ":" + (File.Exists(path) ? new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture) : "dir")))
            : "<absent>";
}

internal sealed class S12T5Fixture : IDisposable
{
    private bool _disposed;

    private S12T5Fixture(
        string root,
        string liveRoot,
        string liveSettingsPath,
        string controlPipe,
        string agentPipe,
        string outputDirectory,
        string backupDirectory,
        string postRoot,
        string postSettingsPath,
        string postControlPipe,
        string olderRoot,
        string olderSettingsPath,
        string olderControlPipe,
        string lockedRoot,
        string lockedSettingsPath,
        string lockedControlPipe)
    {
        Root = root;
        LiveRoot = liveRoot;
        LiveSettingsPath = liveSettingsPath;
        ControlPipe = controlPipe;
        AgentPipe = agentPipe;
        OutputDirectory = outputDirectory;
        BackupDirectory = backupDirectory;
        PostRoot = postRoot;
        PostSettingsPath = postSettingsPath;
        PostControlPipe = postControlPipe;
        OlderRoot = olderRoot;
        OlderSettingsPath = olderSettingsPath;
        OlderControlPipe = olderControlPipe;
        LockedRoot = lockedRoot;
        LockedSettingsPath = lockedSettingsPath;
        LockedControlPipe = lockedControlPipe;
    }

    public string Root { get; }
    public string LiveRoot { get; }
    public string LiveSettingsPath { get; }
    public string ControlPipe { get; }
    public string AgentPipe { get; }
    public string OutputDirectory { get; }
    public string BackupDirectory { get; }
    public string PostRoot { get; }
    public string PostSettingsPath { get; }
    public string PostControlPipe { get; }
    public string PostTarget => Path.Combine(PostRoot, "vault");
    public string OlderRoot { get; }
    public string OlderSettingsPath { get; }
    public string OlderControlPipe { get; }
    public string OlderTarget => Path.Combine(OlderRoot, "vault");
    public string LockedRoot { get; }
    public string LockedSettingsPath { get; }
    public string LockedControlPipe { get; }
    public string LockedTarget => Path.Combine(LockedRoot, "vault");
    public string WrongTarget => Path.Combine(Root, "restored", "wrong-key-vault");
    public string MissingTarget => Path.Combine(Root, "restored", "missing-key-vault");
    public string TamperedTarget => Path.Combine(Root, "restored", "tampered-vault");
    public string FutureTarget => Path.Combine(Root, "restored", "future-format-vault");
    public string TruncatedTarget => Path.Combine(Root, "restored", "truncated-vault");

    public static S12T5Fixture Create()
    {
        var repositoryRoot = SmokeFixture.FindRepositoryRoot();
        var root = Path.Combine(repositoryRoot, "runtime", "s12-t5", "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var liveRoot = Path.Combine(root, "live");
            var outputDirectory = Path.Combine(root, "output");
            var backupDirectory = Path.Combine(outputDirectory, "backups");
            var restoredRoot = Path.Combine(root, "restored");
            var postRoot = Path.Combine(restoredRoot, "post");
            var olderRoot = Path.Combine(restoredRoot, "older");
            var lockedRoot = Path.Combine(restoredRoot, "locked");
            foreach (var directory in new[] { liveRoot, outputDirectory, backupDirectory, postRoot, olderRoot, lockedRoot })
            {
                Directory.CreateDirectory(directory);
            }

            var controlPipe = "MetaBrainS12T5-" + Guid.NewGuid().ToString("N");
            var agentPipe = controlPipe + ".agent";
            var postControlPipe = "MetaBrainS12T5P-" + Guid.NewGuid().ToString("N");
            var olderControlPipe = "MetaBrainS12T5O-" + Guid.NewGuid().ToString("N");
            var lockedControlPipe = "MetaBrainS12T5L-" + Guid.NewGuid().ToString("N");
            var liveSettingsPath = WriteSettings(liveRoot, controlPipe, agentPipe);
            var postSettingsPath = WriteSettings(postRoot, postControlPipe);
            var olderSettingsPath = WriteSettings(olderRoot, olderControlPipe);
            var lockedSettingsPath = WriteSettings(lockedRoot, lockedControlPipe);
            return new S12T5Fixture(root, liveRoot, liveSettingsPath, controlPipe, agentPipe,
                outputDirectory, backupDirectory,
                postRoot, postSettingsPath, postControlPipe,
                olderRoot, olderSettingsPath, olderControlPipe,
                lockedRoot, lockedSettingsPath, lockedControlPipe);
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

    internal static string WriteSettingsForTest(string dataRoot, string controlPipe) => WriteSettings(dataRoot, controlPipe);

    private static string WriteSettings(string dataRoot, string controlPipe, string? agentPipeName = null)
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
                agentPipe = agentPipeName ?? controlPipe + ".agent",
            }),
            new UTF8Encoding(false));
        return path;
    }

    private static void TryDelete(string root)
    {
        try
        {
            var repositoryRoot = SmokeFixture.FindRepositoryRoot();
            var ownedPrefix = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(repositoryRoot, "runtime", "s12-t5"))) + Path.DirectorySeparatorChar;
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (!fullRoot.StartsWith(ownedPrefix, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullRoot).StartsWith("run-", StringComparison.Ordinal))
            {
                return;
            }

            if (Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class S12T5LegacyFixture : IDisposable
{
    private bool _disposed;

    private S12T5LegacyFixture(
        string root,
        string legacyRoot,
        string legacySettingsPath,
        string legacyControlPipe,
        string candidateRoot,
        string candidateSettingsPath,
        string candidateControlPipe,
        string restoredRoot,
        string restoredSettingsPath,
        string restoredControlPipe,
        string outputDirectory,
        string backupDirectory)
    {
        Root = root;
        LegacyRoot = legacyRoot;
        LegacySettingsPath = legacySettingsPath;
        LegacyControlPipe = legacyControlPipe;
        CandidateRoot = candidateRoot;
        CandidateSettingsPath = candidateSettingsPath;
        CandidateControlPipe = candidateControlPipe;
        RestoredRoot = restoredRoot;
        RestoredSettingsPath = restoredSettingsPath;
        RestoredControlPipe = restoredControlPipe;
        OutputDirectory = outputDirectory;
        BackupDirectory = backupDirectory;
    }

    public string Root { get; }
    public string LegacyRoot { get; }
    public string LegacySettingsPath { get; }
    public string LegacyControlPipe { get; }
    public string CandidateRoot { get; }
    public string CandidateSettingsPath { get; }
    public string CandidateControlPipe { get; }
    public string RestoredRoot { get; }
    public string RestoredSettingsPath { get; }
    public string RestoredControlPipe { get; }
    public string RestoredTarget => Path.Combine(RestoredRoot, "vault");
    public string OutputDirectory { get; }
    public string BackupDirectory { get; }

    public static S12T5LegacyFixture Create()
    {
        var repositoryRoot = SmokeFixture.FindRepositoryRoot();
        var root = Path.Combine(repositoryRoot, "runtime", "s12-t5", "legacy-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var legacyRoot = Path.Combine(root, "legacy");
            var candidateRoot = Path.Combine(root, "candidate");
            var restoredRoot = Path.Combine(root, "restored");
            var outputDirectory = Path.Combine(root, "output");
            var backupDirectory = Path.Combine(outputDirectory, "backups");
            foreach (var directory in new[] { legacyRoot, candidateRoot, restoredRoot, outputDirectory, backupDirectory })
            {
                Directory.CreateDirectory(directory);
            }

            var legacyControlPipe = "MetaBrainS12T5Legacy-" + Guid.NewGuid().ToString("N");
            var candidateControlPipe = "MetaBrainS12T5Candidate-" + Guid.NewGuid().ToString("N");
            var restoredControlPipe = "MetaBrainS12T5LegacyRestored-" + Guid.NewGuid().ToString("N");
            return new S12T5LegacyFixture(root, legacyRoot,
                S12T5Fixture.WriteSettingsForTest(legacyRoot, legacyControlPipe),
                legacyControlPipe, candidateRoot,
                S12T5Fixture.WriteSettingsForTest(candidateRoot, candidateControlPipe),
                candidateControlPipe, restoredRoot,
                S12T5Fixture.WriteSettingsForTest(restoredRoot, restoredControlPipe),
                restoredControlPipe, outputDirectory, backupDirectory);
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

    private static void TryDelete(string root)
    {
        try
        {
            var repositoryRoot = SmokeFixture.FindRepositoryRoot();
            var ownedPrefix = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(repositoryRoot, "runtime", "s12-t5"))) + Path.DirectorySeparatorChar;
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (!fullRoot.StartsWith(ownedPrefix, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullRoot).StartsWith("legacy-run-", StringComparison.Ordinal))
            {
                return;
            }

            if (Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class S12T5InstallFixture : IDisposable
{
    private bool _disposed;

    private S12T5InstallFixture(string root, string casesRoot, string payloadDirectory)
    {
        Root = root;
        CasesRoot = casesRoot;
        PayloadDirectory = payloadDirectory;
    }

    public string Root { get; }
    public string CasesRoot { get; }
    public string PayloadDirectory { get; }

    public static S12T5InstallFixture Create()
    {
        var repositoryRoot = SmokeFixture.FindRepositoryRoot();
        var root = Path.Combine(repositoryRoot, "runtime", "s12-t5", "install-run-" + Guid.NewGuid().ToString("N"));
        var casesRoot = Path.Combine(root, "cases");
        var payloadDirectory = Path.Combine(root, "payload");
        Directory.CreateDirectory(casesRoot);
        Directory.CreateDirectory(payloadDirectory);
        return new S12T5InstallFixture(root, casesRoot, payloadDirectory);
    }

    /// <summary>
    /// S12-T5 the install proof uses the actual candidate build output, not
    /// placeholder files: the installer copies these directories exactly as a
    /// published bundle would. The configuration and target framework are
    /// derived from the installer executable the harness launched, so the
    /// scenario cannot silently mix builds.
    /// </summary>
    public void ReplaceWithRealCandidatePayload(string installer)
    {
        var repositoryRoot = SmokeFixture.FindRepositoryRoot();
        var installerDirectory = Path.GetDirectoryName(Path.GetFullPath(installer))
            ?? throw new InvalidOperationException("The installer path has no directory.");
        var configurationDirectory = Path.GetDirectoryName(installerDirectory)
            ?? throw new InvalidOperationException("The installer build directory has no configuration parent.");
        var configuration = Path.GetFileName(configurationDirectory);
        var targetFramework = Path.GetFileName(installerDirectory);
        CopyOutputDirectory(repositoryRoot, configuration, targetFramework, "MetaBrain.Connections", "service", "MetaBrain.Connections.exe");
        CopyOutputDirectory(repositoryRoot, configuration, targetFramework, "MetaBrain.Cli", "cli", "MetaBrain.Cli.exe");
        CopyOutputDirectory(repositoryRoot, configuration, targetFramework, "MetaBrain.Mcp", "mcp", "MetaBrain.Mcp.exe");
        CopyOutputDirectory(repositoryRoot, configuration, targetFramework, "MetaBrain.Installer", "installer", "MetaBrain.Installer.exe");
    }

    private void CopyOutputDirectory(
        string repositoryRoot,
        string configuration,
        string targetFramework,
        string project,
        string payloadDirectoryName,
        string executable)
    {
        var source = Path.Combine(repositoryRoot, "src", project, "bin", configuration, targetFramework);
        if (!Directory.Exists(source) || !File.Exists(Path.Combine(source, executable)))
        {
            throw new InvalidOperationException(
                "The candidate build output for " + project + " is missing at " + source +
                "; build that project before running the S12-T5 installer proof.");
        }

        CopyDirectory(source, Path.Combine(PayloadDirectory, payloadDirectoryName));
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            var repositoryRoot = SmokeFixture.FindRepositoryRoot();
            var ownedPrefix = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(repositoryRoot, "runtime", "s12-t5"))) + Path.DirectorySeparatorChar;
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
            if (Directory.Exists(fullRoot) &&
                fullRoot.StartsWith(ownedPrefix, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(fullRoot).StartsWith("install-run-", StringComparison.Ordinal))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
