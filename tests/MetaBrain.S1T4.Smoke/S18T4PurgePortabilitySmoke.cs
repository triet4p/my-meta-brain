using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S18-T4 runtime proof on the real owner CLI/service over repo-local
/// synthetic encrypted fixtures. One migrated original is purged end to end;
/// a second is purged through a genuine partial state (canonical intent only)
/// and an interrupted-boundary resume. The preview is checked write-free and
/// for its closed cross-representation impact and disclosures; an exact-byte
/// export with labeled generated metadata, the no-delete-original-session
/// rule, and a whole-vault restore that keeps an owner correction are checked.
/// No browser, WPF, provider, real owner file or host mutation is involved.
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S18T4SourceA = "s18t4-source-a";
    private const string S18T4SourceB = "s18t4-source-b";
    private const string S18T4SnapshotA = "snap-s18t4-source-a-g1";
    private const string S18T4SnapshotB = "snap-s18t4-source-b-g1";
    private const string S18T4Correction = "s18t4-owner-correction";
    private static readonly byte[] S18T4MarkerA = Encoding.UTF8.GetBytes("S18T4_ORIGINAL_A_EXACT_2d91");
    private static readonly byte[] S18T4MarkerB = Encoding.UTF8.GetBytes("S18T4_ORIGINAL_B_EXACT_6c37");
    private static readonly byte[] S18T4ExternalMarker = Encoding.UTF8.GetBytes("S18T4_EXTERNAL_SESSION_KEEP_81af");

    public static async Task RunS18T4PurgePortabilityAsync(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S18-T4 purge smoke is Windows-only.");
        }

        Console.WriteLine("RUN S18-T4 cross-representation purge, resume and portability");
        using var fixture = SmokeFixture.CreateUninitializedAt(
            Path.Combine(SmokeFixture.FindRepositoryRoot(), "runtime", "s18-t4", "run-" + Guid.NewGuid().ToString("N")));
        RunningService? live = null;
        RunningService? restored = null;
        var passphraseBytes = RandomNumberGenerator.GetBytes(24);
        var passphrase = Convert.ToBase64String(passphraseBytes);
        CryptographicOperations.ZeroMemory(passphraseBytes);
        var output = fixture.OutputDirectory;
        var outside = Path.Combine(fixture.Root, "outside-vault");
        Directory.CreateDirectory(outside);
        var externalSession = Path.Combine(outside, "synthetic-session.jsonl");
        await File.WriteAllBytesAsync(externalSession, S18T4ExternalMarker).ConfigureAwait(false);
        try
        {
            live = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            var provision = await RunOwnerCommandWithInputAsync(
                executable, passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            S18T2Success(provision, "S18-T4 provision");
            var unlock = await RunOwnerCommandWithInputAsync(
                executable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            S18T2Success(unlock, "S18-T4 unlock");
            var init = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-mkdir", "--parent-id", "root", "--name", "init-s18t4",
                "--expected-tree-revision", "0").ConfigureAwait(false);
            S18T2Success(init, "S18-T4 workspace init");

            await S18T2CreateSourceAsync(executable, fixture, S18T4SourceA, "workspace",
                Path.Combine(output, "s18t4-a.jsonl"), S18T4MarkerA).ConfigureAwait(false);
            await S18T2CreateSourceAsync(executable, fixture, S18T4SourceB, "workspace",
                Path.Combine(output, "s18t4-b.jsonl"), S18T4MarkerB).ConfigureAwait(false);

            var backup = Path.Combine(output, "s18t4-pre-migration.mbbackup");
            var backupResult = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "backup", "--output-file", backup).ConfigureAwait(false);
            S18T2Success(backupResult, "S18-T4 pre-migration backup");
            var preview = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-preview").ConfigureAwait(false);
            S18T2Success(preview, "S18-T4 migration preview");
            var token = S18T2Field(preview.StandardOutput, "MIGRATION PREVIEW", "token=");
            Require(token.Length == 64 && token.All(Uri.IsHexDigit), "S18-T4 migration preview has no binding token.");
            var execute = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "migration-execute", "--preview", token, "--backup-file", backup,
                "--confirm", "MIGRATE").ConfigureAwait(false);
            S18T2Success(execute, "S18-T4 guarded execute");

            var report = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-report").ConfigureAwait(false);
            S18T2Success(report, "S18-T4 migration report");
            Require(report.StandardOutput.Contains("MIGRATED legacy=" + S18T4SnapshotA, StringComparison.Ordinal) &&
                    report.StandardOutput.Contains("MIGRATED legacy=" + S18T4SnapshotB, StringComparison.Ordinal),
                "S18-T4 did not migrate both synthetic originals (stdout=" + report.StandardOutput.Trim() + ").");
            var tree = await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false);
            S18T2Success(tree, "S18-T4 workspace tree");
            var nodeA = S18T2NodeIdFor(tree.StandardOutput, S18T4SnapshotA);
            var nodeB = S18T2NodeIdFor(tree.StandardOutput, S18T4SnapshotB);
            Require(nodeA.Length > 0 && nodeB.Length > 0, "S18-T4 could not resolve both migrated node ids.");

            // ---- Exact-byte original export with labeled generated metadata. ----
            var exportDir = Path.Combine(outside, "export-a");
            var vaultBeforeExport = S18T4VaultDigestExcludingAudit(fixture.VaultDirectoryPath);
            var export = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "migration-export", "--legacy-id", S18T4SnapshotA,
                "--output-dir", exportDir, "--confirm", "EXPORT").ConfigureAwait(false);
            S18T2Success(export, "S18-T4 original export");
            Require(export.StandardOutput.Contains("EXPORT GENERATED", StringComparison.Ordinal) &&
                    export.StandardOutput.Contains("migration-map: generated provenance", StringComparison.Ordinal) &&
                    export.StandardOutput.Contains("sha256=" + S18T2Sha256(S18T4MarkerA), StringComparison.Ordinal),
                "S18-T4 export did not label generated representations or report the exact digest (stdout=" +
                export.StandardOutput.Trim() + ").");
            RequireBytes(await File.ReadAllBytesAsync(Path.Combine(exportDir, "original.bin")).ConfigureAwait(false),
                S18T4MarkerA, "S18-T4 exported original bytes differ from the legacy original.");
            var sidecar = await File.ReadAllTextAsync(Path.Combine(exportDir, "metadata.json")).ConfigureAwait(false);
            Require(sidecar.Contains(S18T2Sha256(S18T4MarkerA), StringComparison.Ordinal) &&
                    sidecar.Contains("generatedRepresentations", StringComparison.Ordinal),
                "S18-T4 metadata sidecar is missing provenance or generated labels.");
            Require(string.Equals(vaultBeforeExport, S18T4VaultDigestExcludingAudit(fixture.VaultDirectoryPath), StringComparison.Ordinal),
                "S18-T4 export changed the encrypted vault.");

            // ---- Write-free cross-representation purge preview for A. ----
            var vaultBeforePreview = S18T4VaultDigestExcludingAudit(fixture.VaultDirectoryPath);
            var purgePreview = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "migration-purge-preview", "--legacy-id", S18T4SnapshotA).ConfigureAwait(false);
            S18T2Success(purgePreview, "S18-T4 purge preview");
            var previewOut = purgePreview.StandardOutput;
            foreach (var expected in new[]
                     {
                         "category=snapshot", "category=map-entry",
                         "category=workspace-node", "category=workspace-revision",
                         "category=passage-index", "category=embedding"
                     })
            {
                Require(previewOut.Contains(expected, StringComparison.Ordinal),
                    "S18-T4 purge preview omitted " + expected + " (stdout=" + previewOut.Trim() + ").");
            }

            Require(previewOut.Contains("PURGE DISCLOSURE retainedBackups=", StringComparison.Ordinal) &&
                    previewOut.Contains("PURGE DISCLOSURE outsideControl=", StringComparison.Ordinal),
                "S18-T4 purge preview omitted retention/backup or outside-control disclosure.");
            var purgeToken = S18T2Field(previewOut, "MIGRATION PURGE PREVIEW", "token=");
            Require(purgeToken.Length == 64 && purgeToken.All(Uri.IsHexDigit),
                "S18-T4 purge preview has no binding token.");
            Require(string.Equals(vaultBeforePreview, S18T4VaultDigestExcludingAudit(fixture.VaultDirectoryPath), StringComparison.Ordinal),
                "S18-T4 purge preview changed the encrypted vault.");

            var wrongConfirm = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "migration-purge", "--legacy-id", S18T4SnapshotA, "--confirm", "EXPORT",
                "--reason", "s18t4-wrong-confirmation").ConfigureAwait(false);
            Require(wrongConfirm.ExitCode != 0 &&
                    wrongConfirm.StandardError.Contains("purge_confirmation_required", StringComparison.Ordinal),
                "S18-T4 accepted a wrong purge confirmation.");

            // ---- Full cross-representation purge of A. ----
            var purgeA = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "migration-purge", "--legacy-id", S18T4SnapshotA,
                "--confirm", "PURGE", "--reason", "s18t4-retention-a").ConfigureAwait(false);
            S18T2Success(purgeA, "S18-T4 migration purge A");
            Require(purgeA.StandardOutput.Contains("workspaceRemoved=true", StringComparison.Ordinal) &&
                    purgeA.StandardOutput.Contains("mapRemoved=true", StringComparison.Ordinal) &&
                    purgeA.StandardOutput.Contains("decisionApplied=true", StringComparison.Ordinal),
                "S18-T4 purge A did not finish both representations (stdout=" + purgeA.StandardOutput.Trim() + ").");
            var reportAfterA = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-report").ConfigureAwait(false);
            S18T2Success(reportAfterA, "S18-T4 report after purge A");
            Require(!reportAfterA.StandardOutput.Contains("MIGRATED legacy=" + S18T4SnapshotA, StringComparison.Ordinal) &&
                    reportAfterA.StandardOutput.Contains("MIGRATED legacy=" + S18T4SnapshotB, StringComparison.Ordinal),
                "S18-T4 purge A removed the wrong map entry or left a hidden second truth.");
            var treeAfterA = await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false);
            S18T2Success(treeAfterA, "S18-T4 tree after purge A");
            Require(S18T2CountLines(treeAfterA.StandardOutput, "FILE ", S18T4SnapshotA) == 0 &&
                    S18T2CountLines(treeAfterA.StandardOutput, "FILE ", S18T4SnapshotB) == 1,
                "S18-T4 purge A did not erase only its workspace node.");
            var legacyA = Path.Combine(output, "s18t4-legacy-after-purge.bin");
            var legacyReadA = await S18T2OwnerAsync(executable, fixture.ControlPipe, "source-snapshot-read",
                "--snapshot-id", S18T4SnapshotA, "--snapshot-revision", "1", "--output-file", legacyA).ConfigureAwait(false);
            Require(legacyReadA.ExitCode != 0, "S18-T4 purge A left its legacy canonical snapshot readable.");
            RequireBytes(await File.ReadAllBytesAsync(externalSession).ConfigureAwait(false), S18T4ExternalMarker,
                "S18-T4 purge deleted or changed an original session file outside the vault.");

            // ---- Interrupted purge of B: canonical-only partial state, then resume. ----
            var canonicalOnly = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "source-snapshot-purge", "--snapshot-id", S18T4SnapshotB,
                "--expected-revision", "1").ConfigureAwait(false);
            S18T2Success(canonicalOnly, "S18-T4 canonical-only partial purge B");
            live = await S18T3RestartAsync(executable, fixture.SettingsPath, fixture.ControlPipe, passphrase, live).ConfigureAwait(false);
            var resumeB = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "migration-purge-resume", "--legacy-id", S18T4SnapshotB,
                "--node-id", nodeB,
                "--confirm", "RESUME", "--reason", "s18t4-resume-b").ConfigureAwait(false);
            S18T2Success(resumeB, "S18-T4 migration purge resume B");
            Require(resumeB.StandardOutput.Contains("workspaceRemoved=true", StringComparison.Ordinal) &&
                    resumeB.StandardOutput.Contains("mapRemoved=true", StringComparison.Ordinal),
                "S18-T4 resume B did not finish the partial purge (stdout=" + resumeB.StandardOutput.Trim() + ").");
            var reportAfterB = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-report").ConfigureAwait(false);
            S18T2Success(reportAfterB, "S18-T4 report after resume B");
            Require(!reportAfterB.StandardOutput.Contains("MIGRATED legacy=", StringComparison.Ordinal),
                "S18-T4 resume B resurrected a purged map entry.");
            var repeatResume = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "migration-purge-resume", "--legacy-id", S18T4SnapshotB,
                "--node-id", nodeB,
                "--confirm", "RESUME", "--reason", "s18t4-resume-b-repeat").ConfigureAwait(false);
            S18T2Success(repeatResume, "S18-T4 idempotent repeat resume B");

            // ---- Backup/restore keeps an owner correction and does not resurrect. ----
            var correctionJson = JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "lesson",
                author = "user",
                creationMethod = "directWrite",
                evidence = "userConfirmed",
                level = "l2",
                title = "S18-T4 owner correction",
                body = "S18T4_OWNER_CORRECTION_KEEP",
                sources = Array.Empty<object>(),
                occurredAtUtc = (DateTimeOffset?)null,
                recordedAtUtc = DateTimeOffset.UtcNow,
                applicability = (object?)null
            });
            var correctionPath = Path.Combine(output, "s18t4-correction.json");
            await File.WriteAllBytesAsync(correctionPath, correctionJson).ConfigureAwait(false);
            var correctionCreate = await S18T2OwnerAsync(executable, fixture.ControlPipe, "memory-create",
                "--resource-id", S18T4Correction, "--zone-id", "s18t4-owner-zone",
                "--input-file", correctionPath).ConfigureAwait(false);
            S18T2Success(correctionCreate, "S18-T4 owner correction");
            var afterPurgeBackup = Path.Combine(output, "s18t4-after-purge.mbbackup");
            var afterPurgeBackupResult = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "backup", "--output-file", afterPurgeBackup).ConfigureAwait(false);
            S18T2Success(afterPurgeBackupResult, "S18-T4 post-purge backup");

            var restoredRoot = Path.Combine(fixture.Root, "restored", "service");
            Directory.CreateDirectory(restoredRoot);
            var restoredControlPipe = "MetaBrainS18T4R-" + Guid.NewGuid().ToString("N");
            var restoredSettingsPath = S18T3WriteSettings(
                restoredRoot, restoredControlPipe, restoredControlPipe + ".agent");
            var restoreTarget = Path.Combine(restoredRoot, "vault");
            var restore = await RunOwnerCommandWithInputAsync(executable, passphrase + Environment.NewLine,
                "owner", "restore", "--control-pipe", fixture.ControlPipe,
                "--backup-file", afterPurgeBackup, "--target-directory", restoreTarget).ConfigureAwait(false);
            S18T2Success(restore, "S18-T4 whole-vault restore");
            Require(restore.StandardOutput.Contains("RESTORE written", StringComparison.Ordinal) &&
                    restore.StandardOutput.Contains("purgedRecordsWithheld=", StringComparison.Ordinal),
                "S18-T4 restore did not report purge-ledger re-application (stdout=" + restore.StandardOutput.Trim() + ").");
            restored = await StartServiceAsync(executable, restoredSettingsPath).ConfigureAwait(false);
            var restoredUnlock = await RunOwnerCommandWithInputAsync(executable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", restoredControlPipe).ConfigureAwait(false);
            S18T2Success(restoredUnlock, "S18-T4 restored unlock");
            var restoredReport = await S18T3OwnerAsync(executable, restoredControlPipe, "migration-report").ConfigureAwait(false);
            S18T2Success(restoredReport, "S18-T4 restored migration report");
            Require(!restoredReport.StandardOutput.Contains("MIGRATED legacy=", StringComparison.Ordinal),
                "S18-T4 restore resurrected a purged migration map entry.");
            var restoredCorrection = await S18T3OwnerAsync(executable, restoredControlPipe, "memory-read",
                "--resource-id", S18T4Correction).ConfigureAwait(false);
            S18T2Success(restoredCorrection, "S18-T4 restored owner correction read");
            Require(restoredCorrection.StandardOutput.Contains("S18T4_OWNER_CORRECTION_KEEP", StringComparison.Ordinal),
                "S18-T4 restore lost the owner correction.");
            var restoredTree = await S18T3OwnerAsync(executable, restoredControlPipe, "workspace-tree").ConfigureAwait(false);
            S18T2Success(restoredTree, "S18-T4 restored workspace tree");
            Require(!restoredTree.StandardOutput.Contains(S18T4SnapshotA, StringComparison.Ordinal) &&
                    !restoredTree.StandardOutput.Contains(S18T4SnapshotB, StringComparison.Ordinal),
                "S18-T4 restore resurrected a purged workspace node.");
            RequireBytes(await File.ReadAllBytesAsync(externalSession).ConfigureAwait(false), S18T4ExternalMarker,
                "S18-T4 restore touched a session file outside the vault.");

            Console.WriteLine("OBSERVATION S18-T4 preview named the closed cross-representation impact and disclosures, both purges finished without resurrection, exact-byte export carried labeled provenance, and restore kept the owner correction");
        }
        finally
        {
            S18T3Stop(restored);
            S18T3Stop(live);
        }
    }

    private static string S18T4VaultDigestExcludingAudit(string root)
    {
        var builder = new StringBuilder();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => !Path.GetFileName(path).StartsWith("operations-audit", StringComparison.Ordinal))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            builder.Append(Path.GetRelativePath(root, path)).Append(':')
                .Append(S18T2Sha256(File.ReadAllBytes(path))).Append('\n');
        }

        return builder.ToString();
    }
}
