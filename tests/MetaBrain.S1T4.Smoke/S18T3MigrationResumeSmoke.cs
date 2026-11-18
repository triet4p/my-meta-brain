using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S18-T3 runtime proof on the real owner CLI/service over repo-local synthetic
/// encrypted fixtures. Each persisted transition of a guarded migration is
/// reconstructed deterministically, the service is stopped and restarted, and
/// resume must finish it with exact bytes, stable identity and no duplicate or
/// overwrite. An unrelated occupied destination, a stale preview and the wrong
/// confirmation are refused. Whole-vault rollback is then exercised with a
/// valid key, wrong key, tamper, schema mismatch and an occupied target, and the
/// restored root is inspected for excluded post-backup writes and for no live
/// grant, session or redeemable token. No browser or WPF surface is involved.
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S18T3Eligible = "s18t3-eligible";
    private const string S18T3Orphan = "s18t3-orphan";
    private const string S18T3Conflict = "s18t3-conflict";
    private const string S18T3Zoned = "s18t3-zoned";
    private const string S18T3GrantItem = "s18t3-grant-item";
    private const string S18T3EligibleSnapshot = "snap-s18t3-eligible-g1";
    private const string S18T3OrphanSnapshot = "snap-s18t3-orphan-g1";
    private const string S18T3ConflictSnapshot = "snap-s18t3-conflict-g1";
    private const string S18T3ZonedSnapshot = "snap-s18t3-zoned-g1";
    private static readonly byte[] S18T3EligibleMarker = Encoding.UTF8.GetBytes("S18T3_ELIGIBLE_EXACT_4c8f");
    private static readonly byte[] S18T3OrphanMarker = Encoding.UTF8.GetBytes("S18T3_ORPHAN_EXACT_7a12");
    private static readonly byte[] S18T3ConflictMarker = Encoding.UTF8.GetBytes("S18T3_CONFLICT_EXACT_9e44");
    private static readonly byte[] S18T3OccupantMarker = Encoding.UTF8.GetBytes("S18T3_UNRELATED_OCCUPANT_b731");
    private static readonly byte[] S18T3ZonedMarker = Encoding.UTF8.GetBytes("S18T3_ZONED_EXACT_0d65");

    public static async Task RunS18T3MigrationResumeAsync(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S18-T3 resume smoke is Windows-only.");
        }

        Console.WriteLine("RUN S18-T3 interrupted migration resume and whole-vault rollback");
        using var fixture = SmokeFixture.CreateUninitializedAt(
            Path.Combine(SmokeFixture.FindRepositoryRoot(), "runtime", "s18-t3", "run-" + Guid.NewGuid().ToString("N")));
        RunningService? live = null;
        RunningService? restarted = null;
        RunningService? restored = null;
        var passphraseBytes = RandomNumberGenerator.GetBytes(24);
        var passphrase = Convert.ToBase64String(passphraseBytes);
        CryptographicOperations.ZeroMemory(passphraseBytes);
        var output = fixture.OutputDirectory;
        var restoredServiceRoot = Path.Combine(fixture.Root, "restored", "service");
        Directory.CreateDirectory(restoredServiceRoot);
        var restoredControlPipe = "MetaBrainS18T3R-" + Guid.NewGuid().ToString("N");
        var restoredAgentPipe = restoredControlPipe + ".agent";
        var restoredSettingsPath = S18T3WriteSettings(restoredServiceRoot, restoredControlPipe, restoredAgentPipe);
        var restoreTarget = Path.Combine(restoredServiceRoot, "vault");
        try
        {
            live = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            var provision = await RunOwnerCommandWithInputAsync(
                executable, passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            S18T2Success(provision, "S18-T3 provision");
            var unlock = await RunOwnerCommandWithInputAsync(
                executable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            S18T2Success(unlock, "S18-T3 unlock");

            var init = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-mkdir", "--parent-id", "root", "--name", "init-s18t3",
                "--expected-tree-revision", "0").ConfigureAwait(false);
            S18T2Success(init, "S18-T3 workspace init");

            // Only the eligible original exists for the first boundary. Other
            // sources are added between boundaries so each resume has exactly
            // one new persisted transition to reconcile.
            await S18T2CreateSourceAsync(executable, fixture, S18T3Eligible, "workspace",
                Path.Combine(output, "s18t3-eligible.jsonl"), S18T3EligibleMarker).ConfigureAwait(false);
            await S18T2CreateSourceAsync(executable, fixture, S18T3Zoned, "s18t3-restricted-zone",
                Path.Combine(output, "s18t3-zoned.jsonl"), S18T3ZonedMarker).ConfigureAwait(false);

            // A redeemed grant must not come back after the rollback.
            var itemJson = JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "lesson",
                author = "user",
                creationMethod = "directWrite",
                evidence = "unverified",
                level = "l1",
                title = "S18-T3 synthetic grant item",
                body = "S18T3_GRANT_ITEM_BODY",
                sources = Array.Empty<object>(),
                occurredAtUtc = (DateTimeOffset?)null,
                recordedAtUtc = DateTimeOffset.UtcNow,
                applicability = (object?)null
            });
            var itemPath = Path.Combine(output, "s18t3-item.json");
            await File.WriteAllBytesAsync(itemPath, itemJson).ConfigureAwait(false);
            var itemCreate = await S18T2OwnerAsync(executable, fixture.ControlPipe, "memory-create",
                "--resource-id", S18T3GrantItem, "--zone-id", "s18t3-grant-zone", "--input-file", itemPath).ConfigureAwait(false);
            S18T2Success(itemCreate, "S18-T3 grant item");
            var handoff = Path.Combine(output, "s18t3.grant-handoff");
            var sessionFile = Path.Combine(output, "s18t3.session-handoff");
            var expiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O", CultureInfo.InvariantCulture);
            var grant = await RunOwnerCommandWithInputAsync(executable, "ISSUE" + Environment.NewLine,
                "owner", "grant", "--control-pipe", fixture.ControlPipe,
                "--resource-ids", S18T3GrantItem, "--operations", "memory.read",
                "--expires-at-utc", expiry, "--handoff-file", handoff).ConfigureAwait(false);
            S18T2Success(grant, "S18-T3 grant issue");
            var redeem = await RunOwnerCommandAsync(executable, "owner", "redeem",
                "--agent-pipe", fixture.AgentPipe, "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
            Require(redeem.ExitCode == 0 && redeem.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
                "S18-T3 grant redemption failed (stdout=" + redeem.StandardOutput.Trim() + "; stderr=" + redeem.StandardError.Trim() + ").");

            var authorityBefore = await S18T3StableAuthorityAsync(executable, fixture.ControlPipe).ConfigureAwait(false);

            // The verified pre-migration checkpoint is the only rollback source.
            var preBackup = Path.Combine(output, "s18t3-pre-migration.mbbackup");
            var backup = await S18T2OwnerAsync(executable, fixture.ControlPipe, "backup", "--output-file", preBackup).ConfigureAwait(false);
            S18T2Success(backup, "S18-T3 pre-migration backup");
            Require(File.Exists(preBackup), "S18-T3 did not create the encrypted pre-migration backup.");

            // ---- Boundary A: folders committed, no original written yet. ----
            var treeA0 = await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false);
            S18T2Success(treeA0, "S18-T3 tree before folder boundary");
            var revA0 = long.Parse(S18T2Field(treeA0.StandardOutput, "WORKSPACE ", "revision="), CultureInfo.InvariantCulture);
            var mkMigrated = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-mkdir", "--parent-id", "root", "--name", "Migrated",
                "--expected-tree-revision", revA0.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            S18T2Success(mkMigrated, "S18-T3 Migrated folder");
            var treeA1 = await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false);
            S18T2Success(treeA1, "S18-T3 tree after Migrated folder");
            var migratedId = S18T3FolderIdFor(treeA1.StandardOutput, "Migrated");
            var revA1 = long.Parse(S18T2Field(treeA1.StandardOutput, "WORKSPACE ", "revision="), CultureInfo.InvariantCulture);
            Require(migratedId.Length > 0, "S18-T3 could not resolve the Migrated folder id.");
            var mkSnap = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-mkdir", "--parent-id", migratedId, "--name", "snap",
                "--expected-tree-revision", revA1.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            S18T2Success(mkSnap, "S18-T3 snap folder");

            var previewA = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-preview").ConfigureAwait(false);
            S18T2Success(previewA, "S18-T3 preview at folder boundary");
            Require(previewA.StandardOutput.Contains("legacy=" + S18T3EligibleSnapshot, StringComparison.Ordinal) &&
                    previewA.StandardOutput.Contains("reason=eligible", StringComparison.Ordinal),
                "S18-T3 folder boundary did not report the original as eligible (stdout=" + previewA.StandardOutput.Trim() + ").");
            var tokenA = S18T2Field(previewA.StandardOutput, "MIGRATION PREVIEW", "token=");
            Require(tokenA.Length == 64 && tokenA.All(Uri.IsHexDigit), "S18-T3 folder boundary preview has no binding token.");

            // A genuine service restart between the persisted state and resume.
            live = await S18T3RestartAsync(executable, fixture.SettingsPath, fixture.ControlPipe, passphrase, live).ConfigureAwait(false);
            var previewA2 = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-preview").ConfigureAwait(false);
            S18T2Success(previewA2, "S18-T3 preview after restart");
            var tokenA2 = S18T2Field(previewA2.StandardOutput, "MIGRATION PREVIEW", "token=");
            var resumeA = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "migration-resume", "--preview", tokenA2, "--confirm", "RESUME").ConfigureAwait(false);
            S18T2Success(resumeA, "S18-T3 resume at folder boundary");
            Require(resumeA.StandardOutput.Contains("MIGRATION RESUMED", StringComparison.Ordinal) &&
                    string.Equals(S18T2Field(resumeA.StandardOutput, "MIGRATION RESUMED", "created="), "1", StringComparison.Ordinal) &&
                    string.Equals(S18T2Field(resumeA.StandardOutput, "MIGRATION RESUMED", "adopted="), "0", StringComparison.Ordinal),
                "S18-T3 folder boundary did not create exactly one original (stdout=" + resumeA.StandardOutput.Trim() + ").");
            var treeAfterA = await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false);
            S18T2Success(treeAfterA, "S18-T3 tree after folder resume");
            var eligibleNode = S18T2NodeIdFor(treeAfterA.StandardOutput, S18T3EligibleSnapshot);
            Require(eligibleNode.Length > 0 &&
                    S18T2CountLines(treeAfterA.StandardOutput, "FILE ", S18T3EligibleSnapshot) == 1,
                "S18-T3 folder resume did not create exactly one stable node.");
            var readEligible = Path.Combine(output, "s18t3-eligible-read.bin");
            var eligibleRead = await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-read",
                "--file-id", eligibleNode, "--revision", "1", "--output-file", readEligible).ConfigureAwait(false);
            S18T2Success(eligibleRead, "S18-T3 eligible read-back");
            RequireBytes(await File.ReadAllBytesAsync(readEligible).ConfigureAwait(false), S18T3EligibleMarker,
                "S18-T3 resumed original bytes differ from the exact legacy bytes.");
            var reportA = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-report").ConfigureAwait(false);
            S18T2Success(reportA, "S18-T3 report after folder resume");
            Require(reportA.StandardOutput.Contains("MIGRATED legacy=" + S18T3EligibleSnapshot, StringComparison.Ordinal) &&
                    reportA.StandardOutput.Contains("node=" + eligibleNode, StringComparison.Ordinal) &&
                    reportA.StandardOutput.Contains("sha256=" + S18T2Sha256(S18T3EligibleMarker), StringComparison.Ordinal),
                "S18-T3 reverse map does not trace the resumed original.");
            var mapRevisionA = S18T2Field(reportA.StandardOutput, "MIGRATION REPORT", "mapRevision=");
            Require(mapRevisionA.Length > 0 && mapRevisionA != "0", "S18-T3 resume did not advance the encrypted map revision.");

            // ---- Boundary B: original written, map not written yet. ----
            await S18T2CreateSourceAsync(executable, fixture, S18T3Orphan, "workspace",
                Path.Combine(output, "s18t3-orphan.jsonl"), S18T3OrphanMarker).ConfigureAwait(false);
            var treeB0 = await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false);
            S18T2Success(treeB0, "S18-T3 tree before orphan boundary");
            var snapFolderId = S18T3FolderIdFor(treeB0.StandardOutput, "snap");
            var revB0 = long.Parse(S18T2Field(treeB0.StandardOutput, "WORKSPACE ", "revision="), CultureInfo.InvariantCulture);
            Require(snapFolderId.Length > 0, "S18-T3 could not resolve the snap folder id.");
            var orphanInput = Path.Combine(output, "s18t3-orphan-import.bin");
            await File.WriteAllBytesAsync(orphanInput, S18T3OrphanMarker).ConfigureAwait(false);
            var import = await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-import",
                "--parent-id", snapFolderId, "--name", S18T3OrphanSnapshot, "--input-file", orphanInput,
                "--expected-tree-revision", revB0.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            S18T2Success(import, "S18-T3 interrupted original write");
            var treeB1 = await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false);
            S18T2Success(treeB1, "S18-T3 tree after interrupted original write");
            var orphanNode = S18T2NodeIdFor(treeB1.StandardOutput, S18T3OrphanSnapshot);
            Require(orphanNode.Length > 0, "S18-T3 could not resolve the interrupted original node id.");
            var previewB = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-preview").ConfigureAwait(false);
            S18T2Success(previewB, "S18-T3 preview at original-write boundary");
            Require(previewB.StandardOutput.Contains("legacy=" + S18T3OrphanSnapshot, StringComparison.Ordinal) &&
                    previewB.StandardOutput.Contains("reason=resumable", StringComparison.Ordinal),
                "S18-T3 original-write boundary was not reported resumable (stdout=" + previewB.StandardOutput.Trim() + ").");
            var tokenB = S18T2Field(previewB.StandardOutput, "MIGRATION PREVIEW", "token=");
            live = await S18T3RestartAsync(executable, fixture.SettingsPath, fixture.ControlPipe, passphrase, live).ConfigureAwait(false);
            var previewB2 = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-preview").ConfigureAwait(false);
            S18T2Success(previewB2, "S18-T3 preview after second restart");
            var tokenB2 = S18T2Field(previewB2.StandardOutput, "MIGRATION PREVIEW", "token=");
            var sessionsBeforeResume = await S18T3OwnerAsync(executable, fixture.ControlPipe, "sessions").ConfigureAwait(false);
            S18T2Success(sessionsBeforeResume, "S18-T3 sessions before resume");
            var resumeB = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "migration-resume", "--preview", tokenB2, "--confirm", "RESUME").ConfigureAwait(false);
            S18T2Success(resumeB, "S18-T3 resume at original-write boundary");
            Require(string.Equals(S18T2Field(resumeB.StandardOutput, "MIGRATION RESUMED", "adopted="), "1", StringComparison.Ordinal) &&
                    string.Equals(S18T2Field(resumeB.StandardOutput, "MIGRATION RESUMED", "created="), "0", StringComparison.Ordinal) &&
                    string.Equals(S18T2Field(resumeB.StandardOutput, "MIGRATION RESUMED", "verified="), "1", StringComparison.Ordinal),
                "S18-T3 did not adopt the exact-byte original and re-verify the completed one (stdout=" + resumeB.StandardOutput.Trim() + ").");
            var treeAfterB = await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false);
            S18T2Success(treeAfterB, "S18-T3 tree after adoption");
            Require(S18T2NodeIdFor(treeAfterB.StandardOutput, S18T3OrphanSnapshot) == orphanNode &&
                    S18T2CountLines(treeAfterB.StandardOutput, "FILE ", S18T3OrphanSnapshot) == 1 &&
                    S18T2CountLines(treeAfterB.StandardOutput, "FILE ", S18T3EligibleSnapshot) == 1,
                "S18-T3 adoption duplicated, overwrote or moved an original node.");
            var readOrphan = Path.Combine(output, "s18t3-orphan-read.bin");
            var orphanRead = await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-read",
                "--file-id", orphanNode, "--revision", "1", "--output-file", readOrphan).ConfigureAwait(false);
            S18T2Success(orphanRead, "S18-T3 adopted read-back");
            RequireBytes(await File.ReadAllBytesAsync(readOrphan).ConfigureAwait(false), S18T3OrphanMarker,
                "S18-T3 adopted original bytes differ from the exact legacy bytes.");
            var reportB = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-report").ConfigureAwait(false);
            S18T2Success(reportB, "S18-T3 report after adoption");
            Require(S18T2CountLines(reportB.StandardOutput, "MIGRATED legacy=", S18T3OrphanSnapshot) == 1 &&
                    reportB.StandardOutput.Contains("node=" + orphanNode, StringComparison.Ordinal) &&
                    reportB.StandardOutput.Contains("sha256=" + S18T2Sha256(S18T3OrphanMarker), StringComparison.Ordinal),
                "S18-T3 reverse map does not trace the adopted original exactly once.");
            var mapRevisionB = S18T2Field(reportB.StandardOutput, "MIGRATION REPORT", "mapRevision=");
            Require(!string.Equals(mapRevisionB, mapRevisionA, StringComparison.Ordinal),
                "S18-T3 adoption did not advance the encrypted map revision.");

            // ---- Boundary C: already complete; resume is idempotent and write-free. ----
            var previewC = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-preview").ConfigureAwait(false);
            S18T2Success(previewC, "S18-T3 preview at complete boundary");
            Require(previewC.StandardOutput.Contains("legacy=" + S18T3EligibleSnapshot, StringComparison.Ordinal) &&
                    previewC.StandardOutput.Contains("reason=already_migrated", StringComparison.Ordinal) &&
                    previewC.StandardOutput.Contains("legacy=" + S18T3OrphanSnapshot, StringComparison.Ordinal) &&
                    previewC.StandardOutput.Contains("reason=already_migrated", StringComparison.Ordinal),
                "S18-T3 complete boundary was not reported as fully migrated.");
            var tokenC = S18T2Field(previewC.StandardOutput, "MIGRATION PREVIEW", "token=");
            var resumeC = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "migration-resume", "--preview", tokenC, "--confirm", "RESUME").ConfigureAwait(false);
            S18T2Success(resumeC, "S18-T3 idempotent resume");
            Require(string.Equals(S18T2Field(resumeC.StandardOutput, "MIGRATION RESUMED", "adopted="), "0", StringComparison.Ordinal) &&
                    string.Equals(S18T2Field(resumeC.StandardOutput, "MIGRATION RESUMED", "created="), "0", StringComparison.Ordinal) &&
                    string.Equals(S18T2Field(resumeC.StandardOutput, "MIGRATION RESUMED", "verified="), "2", StringComparison.Ordinal),
                "S18-T3 idempotent resume wrote new entries (stdout=" + resumeC.StandardOutput.Trim() + ").");
            var reportC = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-report").ConfigureAwait(false);
            S18T2Success(reportC, "S18-T3 report after idempotent resume");
            Require(string.Equals(S18T2Field(reportC.StandardOutput, "MIGRATION REPORT", "mapRevision="), mapRevisionB, StringComparison.Ordinal),
                "S18-T3 idempotent resume advanced the map revision without new work.");

            // ---- Refusals: wrong confirmation, unrelated occupant, stale preview. ----
            var wrongConfirm = await RunOwnerCommandAsync(executable, "owner", "migration-resume",
                "--control-pipe", fixture.ControlPipe, "--preview", tokenC, "--confirm", "MIGRATE").ConfigureAwait(false);
            Require(wrongConfirm.ExitCode != 0,
                "S18-T3 resume with a wrong confirmation was accepted.");

            await S18T2CreateSourceAsync(executable, fixture, S18T3Conflict, "workspace",
                Path.Combine(output, "s18t3-conflict.jsonl"), S18T3ConflictMarker).ConfigureAwait(false);
            var treeD0 = await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false);
            S18T2Success(treeD0, "S18-T3 tree before conflict source");
            var conflictSnapId = S18T3FolderIdFor(treeD0.StandardOutput, "snap");
            var revD0 = long.Parse(S18T2Field(treeD0.StandardOutput, "WORKSPACE ", "revision="), CultureInfo.InvariantCulture);
            var occupantInput = Path.Combine(output, "s18t3-occupant.bin");
            await File.WriteAllBytesAsync(occupantInput, S18T3OccupantMarker).ConfigureAwait(false);
            var occupy = await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-import",
                "--parent-id", conflictSnapId, "--name", S18T3ConflictSnapshot, "--input-file", occupantInput,
                "--expected-tree-revision", revD0.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            S18T2Success(occupy, "S18-T3 unrelated destination occupant");
            var occupantNode = S18T2NodeIdFor(
                (await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false)).StandardOutput,
                S18T3ConflictSnapshot);
            Require(occupantNode.Length > 0, "S18-T3 could not resolve the unrelated occupant node.");
            var previewD = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-preview").ConfigureAwait(false);
            S18T2Success(previewD, "S18-T3 preview with unrelated occupant");
            Require(previewD.StandardOutput.Contains("legacy=" + S18T3ConflictSnapshot, StringComparison.Ordinal) &&
                    previewD.StandardOutput.Contains("reason=destination_conflict", StringComparison.Ordinal),
                "S18-T3 did not report the unrelated occupied destination as a conflict (stdout=" + previewD.StandardOutput.Trim() + ").");
            var tokenD = S18T2Field(previewD.StandardOutput, "MIGRATION PREVIEW", "token=");
            var conflictResume = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "migration-resume", "--preview", tokenD, "--confirm", "RESUME").ConfigureAwait(false);
            var conflictText = conflictResume.StandardOutput + "\n" + conflictResume.StandardError;
            Require(conflictResume.ExitCode != 0 && conflictText.Contains("destination_conflict", StringComparison.Ordinal),
                "S18-T3 did not refuse an unrelated occupied destination (stdout=" + conflictText.Trim() + ").");
            var occupantReadPath = Path.Combine(output, "s18t3-occupant-read.bin");
            var occupantRead = await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-read",
                "--file-id", occupantNode, "--revision", "1", "--output-file", occupantReadPath).ConfigureAwait(false);
            S18T2Success(occupantRead, "S18-T3 occupant read-back");
            RequireBytes(await File.ReadAllBytesAsync(occupantReadPath).ConfigureAwait(false), S18T3OccupantMarker,
                "S18-T3 overwrote or adopted the unrelated occupied destination.");
            var reportD = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-report").ConfigureAwait(false);
            S18T2Success(reportD, "S18-T3 report after conflict refusal");
            Require(!reportD.StandardOutput.Contains("MIGRATED legacy=" + S18T3ConflictSnapshot, StringComparison.Ordinal) &&
                    string.Equals(S18T2Field(reportD.StandardOutput, "MIGRATION REPORT", "mapRevision="), mapRevisionB, StringComparison.Ordinal),
                "S18-T3 wrote a map entry or changed the map on the conflict refusal.");

            var drift = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-mkdir", "--parent-id", "root", "--name", "s18t3-post-preview",
                "--expected-tree-revision", S18T2Field(
                    (await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false)).StandardOutput,
                    "WORKSPACE ", "revision=")).ConfigureAwait(false);
            S18T2Success(drift, "S18-T3 post-preview workspace drift");
            var stale = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "migration-resume", "--preview", tokenD, "--confirm", "RESUME").ConfigureAwait(false);
            var staleText = stale.StandardOutput + "\n" + stale.StandardError;
            Require(stale.ExitCode != 0 && staleText.Contains("stale_preview", StringComparison.Ordinal),
                "S18-T3 resume with a stale preview was accepted (stdout=" + staleText.Trim() + ").");

            var authorityAfter = await S18T3StableAuthorityAsync(executable, fixture.ControlPipe).ConfigureAwait(false);
            Require(string.Equals(authorityBefore, authorityAfter, StringComparison.Ordinal),
                "S18-T3 resume changed grants, catalog or collections.");
            var sessionsAfterResume = await S18T3OwnerAsync(executable, fixture.ControlPipe, "sessions").ConfigureAwait(false);
            S18T2Success(sessionsAfterResume, "S18-T3 sessions after resume");
            Require(string.Equals(sessionsBeforeResume.StandardOutput, sessionsAfterResume.StandardOutput, StringComparison.Ordinal),
                "S18-T3 resume minted, widened or changed a session.");
            var zonedTree = await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false);
            S18T2Success(zonedTree, "S18-T3 final tree");
            Require(!zonedTree.StandardOutput.Contains(S18T3ZonedSnapshot, StringComparison.Ordinal),
                "S18-T3 migrated an ineligible zoned original.");

            // ---- Whole-vault rollback: wrong key, tamper, schema, occupied target. ----
            var wrongTarget = Path.Combine(fixture.Root, "restored", "wrong-key-vault");
            var wrong = await RunOwnerCommandWithInputAsync(executable, "S18T3_WRONG_PASSPHRASE\n",
                "owner", "restore", "--control-pipe", fixture.ControlPipe,
                "--backup-file", preBackup, "--target-directory", wrongTarget).ConfigureAwait(false);
            Require(wrong.ExitCode != 0 && wrong.StandardError.Contains("restore_denied", StringComparison.Ordinal) &&
                    !Directory.Exists(wrongTarget),
                "S18-T3 restore accepted a wrong key or wrote a target (" + wrong.StandardError.Trim() + ").");

            var backupBytes = await File.ReadAllBytesAsync(preBackup).ConfigureAwait(false);
            var tamperedPath = Path.Combine(output, "s18t3-tampered.mbbackup");
            await File.WriteAllBytesAsync(tamperedPath, TamperS8T2Container(backupBytes)).ConfigureAwait(false);
            var tamperedTarget = Path.Combine(fixture.Root, "restored", "tampered-vault");
            var tampered = await RunOwnerCommandWithInputAsync(executable, passphrase + Environment.NewLine,
                "owner", "restore", "--control-pipe", fixture.ControlPipe,
                "--backup-file", tamperedPath, "--target-directory", tamperedTarget).ConfigureAwait(false);
            Require(tampered.ExitCode != 0 && tampered.StandardError.Contains("restore_corrupt", StringComparison.Ordinal) &&
                    !Directory.Exists(tamperedTarget),
                "S18-T3 restore accepted a tampered backup (" + tampered.StandardError.Trim() + ").");

            var schemaPath = Path.Combine(output, "s18t3-schema.mbbackup");
            var schemaNode = JsonNode.Parse(backupBytes)
                ?? throw new InvalidOperationException("S18-T3 could not parse the backup container.");
            schemaNode["formatVersion"] = 99;
            await File.WriteAllTextAsync(schemaPath, schemaNode.ToJsonString(), new UTF8Encoding(false)).ConfigureAwait(false);
            var schemaTarget = Path.Combine(fixture.Root, "restored", "schema-vault");
            var schema = await RunOwnerCommandWithInputAsync(executable, passphrase + Environment.NewLine,
                "owner", "restore", "--control-pipe", fixture.ControlPipe,
                "--backup-file", schemaPath, "--target-directory", schemaTarget).ConfigureAwait(false);
            Require(schema.ExitCode != 0 && schema.StandardError.Contains("restore_corrupt", StringComparison.Ordinal) &&
                    !Directory.Exists(schemaTarget),
                "S18-T3 restore accepted an unsupported container schema (" + schema.StandardError.Trim() + ").");

            var occupiedTarget = Path.Combine(fixture.Root, "restored", "occupied-vault");
            Directory.CreateDirectory(occupiedTarget);
            var sentinel = Path.Combine(occupiedTarget, "owner-sentinel.bin");
            await File.WriteAllBytesAsync(sentinel, S18T3OccupantMarker).ConfigureAwait(false);
            var occupied = await RunOwnerCommandWithInputAsync(executable, passphrase + Environment.NewLine,
                "owner", "restore", "--control-pipe", fixture.ControlPipe,
                "--backup-file", preBackup, "--target-directory", occupiedTarget).ConfigureAwait(false);
            Require(occupied.ExitCode != 0 &&
                    File.Exists(sentinel) &&
                    (await File.ReadAllBytesAsync(sentinel).ConfigureAwait(false)).SequenceEqual(S18T3OccupantMarker),
                "S18-T3 restore overwrote or used an occupied target directory.");

            // The supported rollback: verified pre-migration backup, valid key,
            // separate new root. Post-backup writes are excluded and disclosed.
            var validRestore = await RunOwnerCommandWithInputAsync(executable, passphrase + Environment.NewLine,
                "owner", "restore", "--control-pipe", fixture.ControlPipe,
                "--backup-file", preBackup, "--target-directory", restoreTarget).ConfigureAwait(false);
            S18T2Success(validRestore, "S18-T3 whole-vault restore");
            Require(validRestore.StandardOutput.Contains("RESTORE written", StringComparison.Ordinal) &&
                    validRestore.StandardOutput.Contains("RESTORE DISCLOSURE", StringComparison.Ordinal) &&
                    validRestore.StandardOutput.Contains("whole-vault restore", StringComparison.Ordinal) &&
                    validRestore.StandardOutput.Contains("Writes, exports and revisions made after that backup are not in it and are excluded", StringComparison.Ordinal) &&
                    validRestore.StandardOutput.Contains("not a per-record undo or an in-place downgrade", StringComparison.Ordinal) &&
                    validRestore.StandardOutput.Contains("older 1.0/1.1 binaries are not promised", StringComparison.Ordinal),
                "S18-T3 restore did not disclose its whole-vault limits (stdout=" + validRestore.StandardOutput.Trim() + ").");

            restored = await StartServiceAsync(executable, restoredSettingsPath).ConfigureAwait(false);
            var restoredUnlock = await RunOwnerCommandWithInputAsync(executable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", restoredControlPipe).ConfigureAwait(false);
            S18T2Success(restoredUnlock, "S18-T3 restored vault unlock");
            var restoredTree = await S18T3OwnerAsync(executable, restoredControlPipe, "workspace-tree").ConfigureAwait(false);
            S18T2Success(restoredTree, "S18-T3 restored tree");
            Require(!restoredTree.StandardOutput.Contains("Migrated", StringComparison.Ordinal) &&
                    !restoredTree.StandardOutput.Contains(S18T3EligibleSnapshot, StringComparison.Ordinal) &&
                    !restoredTree.StandardOutput.Contains("s18t3-post-preview", StringComparison.Ordinal),
                "S18-T3 restored vault contains post-migration or post-backup writes.");
            var restoredReport = await S18T3OwnerAsync(executable, restoredControlPipe, "migration-report").ConfigureAwait(false);
            S18T2Success(restoredReport, "S18-T3 restored migration report");
            Require(!restoredReport.StandardOutput.Contains("MIGRATED legacy=", StringComparison.Ordinal),
                "S18-T3 restored vault resurrected the migration map.");
            var restoredLegacyPath = Path.Combine(output, "s18t3-restored-legacy.bin");
            var restoredLegacy = await S18T3OwnerAsync(executable, restoredControlPipe, "source-snapshot-read",
                "--snapshot-id", S18T3EligibleSnapshot, "--snapshot-revision", "1", "--output-file", restoredLegacyPath).ConfigureAwait(false);
            S18T2Success(restoredLegacy, "S18-T3 restored legacy snapshot read");
            RequireBytes(await File.ReadAllBytesAsync(restoredLegacyPath).ConfigureAwait(false), S18T3EligibleMarker,
                "S18-T3 restore lost the pre-migration legacy original.");
            var restoredGrants = await S18T3OwnerAsync(executable, restoredControlPipe, "grants").ConfigureAwait(false);
            S18T2Success(restoredGrants, "S18-T3 restored grants");
            Require(restoredGrants.StandardOutput.Contains("GRANTS count=0", StringComparison.Ordinal),
                "S18-T3 restore resurrected a live grant.");
            var restoredSessions = await S18T3OwnerAsync(executable, restoredControlPipe, "sessions").ConfigureAwait(false);
            S18T2Success(restoredSessions, "S18-T3 restored sessions");
            Require(restoredSessions.StandardOutput.Contains("SESSIONS count=0", StringComparison.Ordinal),
                "S18-T3 restore resurrected a live session.");
            var restoredRedeem = await RunOwnerCommandAsync(executable, "owner", "redeem",
                "--agent-pipe", restoredAgentPipe, "--handoff-file", handoff,
                "--session-file", Path.Combine(output, "s18t3.redeem-after-restore")).ConfigureAwait(false);
            Require(restoredRedeem.ExitCode != 0 &&
                    restoredRedeem.StandardError.Contains("token_used", StringComparison.Ordinal) &&
                    !restoredRedeem.StandardError.Contains("token_unknown", StringComparison.Ordinal),
                "S18-T3 restore made a consumed token redeemable again (stdout=" + restoredRedeem.StandardOutput.Trim() +
                "; stderr=" + restoredRedeem.StandardError.Trim() + ").");

            Console.WriteLine("OBSERVATION S18-T3 resume completed every persisted boundary with exact bytes and no duplicate, and rollback restored only the verified pre-migration vault");
        }
        finally
        {
            S18T3Stop(restored);
            S18T3Stop(restarted);
            S18T3Stop(live);
        }
    }

    private static async Task<RunningService> S18T3RestartAsync(
        string executable,
        string settingsPath,
        string controlPipe,
        string passphrase,
        RunningService current)
    {
        S18T3Stop(current);
        var service = await StartServiceAsync(executable, settingsPath).ConfigureAwait(false);
        var unlock = await RunOwnerCommandWithInputAsync(executable, passphrase + Environment.NewLine,
            "owner", "unlock", "--control-pipe", controlPipe).ConfigureAwait(false);
        S18T2Success(unlock, "S18-T3 unlock after restart");
        return service;
    }

    private static void S18T3Stop(RunningService? service)
    {
        if (service is null)
        {
            return;
        }

        try
        {
            if (!service.Process.HasExited)
            {
                service.Process.Kill(entireProcessTree: true);
                service.Process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // The process had already exited.
        }
    }

    private static Task<CommandResult> S18T3OwnerAsync(string executable, string controlPipe, params string[] arguments)
    {
        var full = new string[arguments.Length + 3];
        full[0] = "owner";
        full[1] = arguments[0];
        full[2] = "--control-pipe";
        full[3] = controlPipe;
        Array.Copy(arguments, 1, full, 4, arguments.Length - 1);
        return RunOwnerCommandAsync(executable, full);
    }

    private static string S18T3FolderIdFor(string treeOutput, string name)
    {
        foreach (var line in treeOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("FOLDER ", StringComparison.Ordinal) &&
                line.Contains("name=\"" + name + "\"", StringComparison.Ordinal))
            {
                return S18T2Field(line, "FOLDER ", "id=");
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Grants, catalog and collections are authority surfaces a resume must
    /// not change. Sessions are inspected separately: a service restart
    /// legitimately invalidates them, so they are not part of this snapshot.
    /// </summary>
    private static async Task<string> S18T3StableAuthorityAsync(string executable, string controlPipe)
    {
        var builder = new StringBuilder();
        foreach (var command in new[] { "grants", "catalog-list", "collections" })
        {
            var result = await S18T3OwnerAsync(executable, controlPipe, command).ConfigureAwait(false);
            S18T2Success(result, "S18-T3 authority snapshot " + command);
            builder.Append(command).Append(':').Append(result.StandardOutput).Append('\n');
        }

        return builder.ToString();
    }

    private static string S18T3WriteSettings(string dataRoot, string controlPipe, string agentPipe)
    {
        Directory.CreateDirectory(dataRoot);
        using var identity = WindowsIdentity.GetCurrent();
        var ownerSid = identity.User?.Value
            ?? throw new InvalidOperationException("The S18-T3 fixture has no Windows owner SID.");
        var path = Path.Combine(dataRoot, "service-settings.json");
        File.WriteAllText(path,
            JsonSerializer.Serialize(new { schemaVersion = 3, ownerSid, controlPipe, agentPipe }),
            new UTF8Encoding(false));
        return path;
    }
}
