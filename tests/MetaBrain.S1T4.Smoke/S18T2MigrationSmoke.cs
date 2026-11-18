using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S18-T2 runtime proof on the actual owner CLI and shared service over a
/// repo-local synthetic encrypted fixture: a write-free preview with exact
/// byte/digest/zone eligibility and ineligible reporting; refusal without
/// confirmation, with a missing backup and on a stale preview; a guarded
/// execute against a verified encrypted pre-migration backup; exact retained
/// bytes, a complete encrypted reverse map, unchanged legacy authority, and no
/// plaintext staging. No browser or WPF surface is involved or claimed.
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S18T2EligibleSource = "s18t2-eligible";
    private const string S18T2ZonedSource = "s18t2-zoned";
    private const string S18T2EligibleSnapshot = "snap-s18t2-eligible-g1";
    private const string S18T2ZonedSnapshot = "snap-s18t2-zoned-g1";
    private static readonly byte[] S18T2EligibleMarker = Encoding.UTF8.GetBytes("S18T2_SYNTHETIC_ELIGIBLE_ORIGINAL_9d41");
    private static readonly byte[] S18T2ZonedMarker = Encoding.UTF8.GetBytes("S18T2_SYNTHETIC_ZONED_ORIGINAL_6b02");
    private const string S18T2OversizedSource = "s18t2-oversized";
    private const string S18T2OversizedSnapshot = "snap-s18t2-oversized-g1";

    public static async Task RunS18T2MigrationAsync(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S18-T2 migration smoke is Windows-only.");
        }

        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        var output = fixture.OutputDirectory;
        try
        {
            service = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            var passphrase = CreateSyntheticPassphrase();
            var provision = await RunOwnerCommandWithInputAsync(
                executable, passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            S18T2Success(provision, "S18-T2 provision");
            var unlock = await RunOwnerCommandWithInputAsync(
                executable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            S18T2Success(unlock, "S18-T2 unlock");

            // Initialize the workspace so the preview binds a concrete tree revision.
            var init = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-mkdir", "--parent-id", "root", "--name", "init-s18t2", "--expected-tree-revision", "0").ConfigureAwait(false);
            S18T2Success(init, "S18-T2 workspace init");

            var eligiblePath = Path.Combine(output, "s18t2-eligible.jsonl");
            await S18T2CreateSourceAsync(executable, fixture, S18T2EligibleSource, "workspace", eligiblePath, S18T2EligibleMarker).ConfigureAwait(false);
            var zonedPath = Path.Combine(output, "s18t2-zoned.jsonl");
            await S18T2CreateSourceAsync(executable, fixture, S18T2ZonedSource, "s18t2-restricted-zone", zonedPath, S18T2ZonedMarker).ConfigureAwait(false);
            // F2: a preserved snapshot above the 1 MiB per-act cap must stay legacy.
            var oversizedMarker = new byte[(1024 * 1024) + 1];
            Array.Fill(oversizedMarker, (byte)0x41);
            var oversizedPath = Path.Combine(output, "s18t2-oversized.jsonl");
            await S18T2CreateSourceAsync(executable, fixture, S18T2OversizedSource,
                "workspace", oversizedPath, oversizedMarker).ConfigureAwait(false);

            // ---- Preview: write-free, exact eligibility and disclosure. ----
            var vaultBeforePreview = S18T2DirectoryDigest(fixture.VaultDirectoryPath);
            var preview = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-preview").ConfigureAwait(false);
            S18T2Success(preview, "S18-T2 preview");
            Require(preview.StandardOutput.Contains("MIGRATION PREVIEW", StringComparison.Ordinal),
                "S18-T2 preview did not return the owner preview summary.");
            Require(preview.StandardOutput.Contains("MIGRATION DISCLOSURE", StringComparison.Ordinal) &&
                    preview.StandardOutput.Contains("declassification is never offered", StringComparison.Ordinal),
                "S18-T2 preview did not disclose zone-independent destination semantics and no-declassification.");
            Require(preview.StandardOutput.Contains("legacy=" + S18T2EligibleSnapshot, StringComparison.Ordinal) &&
                    preview.StandardOutput.Contains("reason=eligible", StringComparison.Ordinal),
                "S18-T2 preview did not mark the zone-independent original eligible (stdout=" + preview.StandardOutput.Trim() + ").");
            Require(preview.StandardOutput.Contains("legacy=" + S18T2ZonedSnapshot, StringComparison.Ordinal) &&
                    preview.StandardOutput.Contains("reason=zone_protection_not_enforced", StringComparison.Ordinal),
                "S18-T2 preview did not report the zoned original as ineligible without declassification.");
            Require(preview.StandardOutput.Contains("reason=structured_only", StringComparison.Ordinal),
                "S18-T2 preview did not report structured-only legacy records as ineligible.");
            Require(preview.StandardOutput.Contains("sha256=" + S18T2Sha256(S18T2EligibleMarker), StringComparison.Ordinal),
                "S18-T2 preview did not list the exact digest of the eligible original.");
            Require(preview.StandardOutput.Contains("legacy=" + S18T2OversizedSnapshot, StringComparison.Ordinal) &&
                    preview.StandardOutput.Contains("reason=oversized_snapshot", StringComparison.Ordinal),
                "S18-T2 preview did not report the >1 MiB snapshot as oversized_snapshot (stdout=" + preview.StandardOutput.Trim() + ").");
            var token = S18T2Field(preview.StandardOutput, "MIGRATION PREVIEW", "token=");
            Require(token.Length == 64 && token.All(Uri.IsHexDigit),
                "S18-T2 preview did not return a bound 64-hex token.");
            Require(string.Equals(vaultBeforePreview, S18T2DirectoryDigest(fixture.VaultDirectoryPath), StringComparison.Ordinal),
                "S18-T2 preview wrote to the encrypted vault; a dry run must be write-free.\nBEFORE:\n" +
                vaultBeforePreview + "\nAFTER:\n" + S18T2DirectoryDigest(fixture.VaultDirectoryPath));

            var authorityBefore = await S18T2AuthoritySnapshotAsync(executable, fixture.ControlPipe).ConfigureAwait(false);

            // Verify a consistent pre-migration encrypted backup as the preview required.
            var backupPath = Path.Combine(output, "s18t2-pre-migration.mbbackup");
            var backup = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "backup", "--output-file", backupPath).ConfigureAwait(false);
            S18T2Success(backup, "S18-T2 pre-migration backup");
            Require(File.Exists(backupPath), "S18-T2 did not create the encrypted pre-migration backup.");

            // ---- Refusals: no confirmation, wrong confirmation, stale preview. ----
            var noConfirm = await RunOwnerCommandAsync(executable, "owner", "migration-execute",
                "--control-pipe", fixture.ControlPipe, "--preview", token,
                "--backup-file", backupPath).ConfigureAwait(false);
            Require(noConfirm.ExitCode != 0,
                "S18-T2 execute without confirmation was not refused (stdout=" + noConfirm.StandardOutput.Trim() +
                "; stderr=" + noConfirm.StandardError.Trim() + ").");

            var wrongConfirm = await RunOwnerCommandAsync(executable, "owner", "migration-execute",
                "--control-pipe", fixture.ControlPipe, "--preview", token,
                "--backup-file", backupPath, "--confirm", "EXPORT").ConfigureAwait(false);
            Require(wrongConfirm.ExitCode != 0,
                "S18-T2 execute with a wrong confirmation was not refused.");

            // Changing workspace state after the preview makes the bound token stale.
            var drift = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-mkdir", "--parent-id", "root", "--name", "s18t2-stale", "--expected-tree-revision", "2").ConfigureAwait(false);
            S18T2Success(drift, "S18-T2 workspace drift for stale preview");
            var stale = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "migration-execute", "--preview", token, "--backup-file", backupPath, "--confirm", "MIGRATE").ConfigureAwait(false);
            var staleText = stale.StandardOutput + "\n" + stale.StandardError;
            Require(stale.ExitCode != 0 && staleText.Contains("stale_preview", StringComparison.Ordinal),
                "S18-T2 stale preview was not refused (stdout=" + stale.StandardOutput.Trim() +
                "; stderr=" + stale.StandardError.Trim() + ").");
            var afterStale = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-report").ConfigureAwait(false);
            S18T2Success(afterStale, "S18-T2 report after stale preview");
            Require(afterStale.StandardOutput.Contains("MIGRATION REPORT") &&
                    !afterStale.StandardOutput.Contains("MIGRATED legacy=", StringComparison.Ordinal),
                "S18-T2 stale execute wrote a migration map entry; nothing should have transferred.");

            // ---- Fresh preview + fresh backup, then a missing-backup refusal. ----
            var freshPreview = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-preview").ConfigureAwait(false);
            S18T2Success(freshPreview, "S18-T2 fresh preview");
            var freshToken = S18T2Field(freshPreview.StandardOutput, "MIGRATION PREVIEW", "token=");
            var freshBackupPath = Path.Combine(output, "s18t2-pre-migration-fresh.mbbackup");
            var freshBackup = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "backup", "--output-file", freshBackupPath).ConfigureAwait(false);
            S18T2Success(freshBackup, "S18-T2 fresh backup");

            var missingBackup = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "migration-execute", "--preview", freshToken,
                "--backup-file", Path.Combine(output, "s18t2-does-not-exist.mbbackup"), "--confirm", "MIGRATE").ConfigureAwait(false);
            Require(missingBackup.ExitCode != 0 &&
                    !missingBackup.StandardOutput.Contains("MIGRATION EXECUTED", StringComparison.Ordinal),
                "S18-T2 execute with an unavailable backup was not refused (stdout=" + missingBackup.StandardOutput.Trim() +
                "; stderr=" + missingBackup.StandardError.Trim() + ").");

            // ---- Guarded execute against the verified consistent backup. ----
            var execute = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "migration-execute", "--preview", freshToken, "--backup-file", freshBackupPath,
                "--confirm", "MIGRATE").ConfigureAwait(false);
            S18T2Success(execute, "S18-T2 guarded execute");
            Require(execute.StandardOutput.Contains("MIGRATION EXECUTED", StringComparison.Ordinal),
                "S18-T2 execute did not report a completed transfer.");

            // Exact bytes, revision and reverse trace on the actual CLI surfaces.
            var tree = await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false);
            S18T2Success(tree, "S18-T2 workspace tree");
            Require(tree.StandardOutput.Contains("Migrated", StringComparison.Ordinal) &&
                    tree.StandardOutput.Contains(S18T2EligibleSnapshot, StringComparison.Ordinal),
                "S18-T2 execute did not create the deterministic Migrated/snap original node.");
            Require(!tree.StandardOutput.Contains(S18T2ZonedSnapshot, StringComparison.Ordinal),
                "S18-T2 migrated an ineligible zoned record.");
            var nodeId = S18T2NodeIdFor(tree.StandardOutput, S18T2EligibleSnapshot);
            Require(nodeId.Length > 0, "S18-T2 could not resolve the migrated workspace node id.");

            // F1: an ungranted agent (well-formed but unregistered bearer) cannot
            // read the migrated node. The service denies uniformly and reveals no
            // node id, snapshot name, digest or content on either stdout or stderr.
            var ungrantedBytes = RandomNumberGenerator.GetBytes(32);
            var ungrantedToken = "mbs1_" + Convert.ToBase64String(ungrantedBytes)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            CryptographicOperations.ZeroMemory(ungrantedBytes);
            var ungrantedSession = Path.Combine(output, "s18t2-ungranted-session.bin");
            MetaBrain.Connections.AgentSessionFile.Write(ungrantedToken, ungrantedSession);
            var ungrantedRead = await RunOwnerCommandAsync(executable,
                "owner", "agent-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", ungrantedSession, "--operation", "workspace.file.read",
                "--resource-id", nodeId, "--resource-revision", "1").ConfigureAwait(false);
            var ungrantedText = ungrantedRead.StandardOutput + "\n" + ungrantedRead.StandardError;
            Require(ungrantedRead.ExitCode != 0 &&
                    ungrantedText.Contains("resource_unavailable", StringComparison.Ordinal),
                "S18-T2 ungranted agent read of the migrated node was not denied (stdout=" +
                ungrantedRead.StandardOutput.Trim() + "; stderr=" + ungrantedRead.StandardError.Trim() + ").");
            Require(!ungrantedText.Contains(nodeId, StringComparison.Ordinal) &&
                    !ungrantedText.Contains(S18T2EligibleSnapshot, StringComparison.Ordinal) &&
                    !ungrantedText.Contains(S18T2Sha256(S18T2EligibleMarker), StringComparison.Ordinal) &&
                    !ungrantedText.Contains(Convert.ToBase64String(S18T2EligibleMarker), StringComparison.Ordinal) &&
                    !ungrantedText.Contains(Encoding.UTF8.GetString(S18T2EligibleMarker), StringComparison.Ordinal),
                "S18-T2 ungranted agent denial leaked the migrated node id, name, digest or content.");

            var readPath = Path.Combine(output, "s18t2-read-back.bin");
            var read = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-read", "--file-id", nodeId, "--revision", "1", "--output-file", readPath).ConfigureAwait(false);
            S18T2Success(read, "S18-T2 original read-back");
            RequireBytes(await File.ReadAllBytesAsync(readPath).ConfigureAwait(false), S18T2EligibleMarker,
                "S18-T2 migrated original bytes differ from the exact legacy bytes.");

            var report = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-report").ConfigureAwait(false);
            S18T2Success(report, "S18-T2 migration report");
            Require(report.StandardOutput.Contains("MIGRATED legacy=" + S18T2EligibleSnapshot, StringComparison.Ordinal) &&
                    report.StandardOutput.Contains("node=" + nodeId, StringComparison.Ordinal) &&
                    report.StandardOutput.Contains("sha256=" + S18T2Sha256(S18T2EligibleMarker), StringComparison.Ordinal),
                "S18-T2 reverse map did not resolve the migrated legacy id to its node/revision/digest.");
            Require(!report.StandardOutput.Contains("MIGRATED legacy=" + S18T2ZonedSnapshot, StringComparison.Ordinal),
                "S18-T2 reverse map contained an ineligible zoned record.");

            // No duplicated writable authority: every legacy grant/catalog/
            // collection/session set is byte-for-byte what it was before.
            var authorityAfter = await S18T2AuthoritySnapshotAsync(executable, fixture.ControlPipe).ConfigureAwait(false);
            Require(string.Equals(authorityBefore, authorityAfter, StringComparison.Ordinal),
                "S18-T2 transfer changed grants, catalog, collections or sessions (no authority transfer is allowed).");
            // The ineligible zoned snapshot stays legacy and unmodified; the
            // reverse map contains no entry for it (asserted above).
            RequireNoPlaintextVaultFiles(fixture.VaultDirectoryPath, eligiblePath,
                new[] { S18T2EligibleMarker, S18T2ZonedMarker });

            // F4: legacy originals remain immutable history after the transfer.
            foreach (var (snapshotId, marker) in new[]
                     {
                         (S18T2EligibleSnapshot, S18T2EligibleMarker),
                         (S18T2ZonedSnapshot, S18T2ZonedMarker)
                     })
            {
                var legacySourceId = snapshotId == S18T2EligibleSnapshot
                    ? S18T2EligibleSource : S18T2ZonedSource;
                var legacyList = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                    "source-snapshot-list", "--source-id", legacySourceId).ConfigureAwait(false);
                S18T2Success(legacyList, "S18-T2 legacy snapshot list " + snapshotId);
                Require(legacyList.StandardOutput.Contains(snapshotId, StringComparison.Ordinal),
                    "S18-T2 legacy snapshot " + snapshotId + " was no longer listed after transfer.");
                var legacyPath = Path.Combine(output, "s18t2-legacy-" + snapshotId + ".bin");
                var legacyRead = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                    "source-snapshot-read", "--snapshot-id", snapshotId, "--snapshot-revision", "1",
                    "--output-file", legacyPath).ConfigureAwait(false);
                S18T2Success(legacyRead, "S18-T2 legacy snapshot read " + snapshotId);
                RequireBytes(await File.ReadAllBytesAsync(legacyPath).ConfigureAwait(false), marker,
                    "S18-T2 legacy snapshot bytes changed after transfer for " + snapshotId + ".");
            }

            // F3: a repeat preview reports the original as already_migrated; a
            // fresh repeat execute must not create a duplicate node or a second
            // map entry, nor re-point the migrated original.
            var repeatPreview = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-preview").ConfigureAwait(false);
            S18T2Success(repeatPreview, "S18-T2 repeat preview");
            Require(repeatPreview.StandardOutput.Contains("legacy=" + S18T2EligibleSnapshot, StringComparison.Ordinal) &&
                    repeatPreview.StandardOutput.Contains("reason=already_migrated", StringComparison.Ordinal),
                "S18-T2 repeat preview did not report the migrated original as already_migrated (stdout=" +
                repeatPreview.StandardOutput.Trim() + ").");
            var repeatToken = S18T2Field(repeatPreview.StandardOutput, "MIGRATION PREVIEW", "token=");
            var repeatBackupPath = Path.Combine(output, "s18t2-pre-migration-repeat.mbbackup");
            var repeatBackup = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "backup", "--output-file", repeatBackupPath).ConfigureAwait(false);
            S18T2Success(repeatBackup, "S18-T2 repeat backup");
            var repeatExecute = await S18T2OwnerAsync(executable, fixture.ControlPipe,
                "migration-execute", "--preview", repeatToken, "--backup-file", repeatBackupPath,
                "--confirm", "MIGRATE").ConfigureAwait(false);
            S18T2Success(repeatExecute, "S18-T2 repeat execute");
            Require(repeatExecute.StandardOutput.Contains("migrated=0", StringComparison.Ordinal),
                "S18-T2 repeat execute reported new migrations (dual write) (stdout=" +
                repeatExecute.StandardOutput.Trim() + ").");
            var treeAfterRepeat = await S18T2OwnerAsync(executable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false);
            S18T2Success(treeAfterRepeat, "S18-T2 tree after repeat");
            Require(S18T2CountLines(treeAfterRepeat.StandardOutput, "FILE ", S18T2EligibleSnapshot) == 1,
                "S18-T2 repeat execute created a duplicate workspace node for the migrated original.");
            var reportAfterRepeat = await S18T2OwnerAsync(executable, fixture.ControlPipe, "migration-report").ConfigureAwait(false);
            S18T2Success(reportAfterRepeat, "S18-T2 report after repeat");
            Require(S18T2CountLines(reportAfterRepeat.StandardOutput, "MIGRATED legacy=", S18T2EligibleSnapshot) == 1 &&
                    reportAfterRepeat.StandardOutput.Contains("node=" + nodeId, StringComparison.Ordinal) &&
                    reportAfterRepeat.StandardOutput.Contains("sha256=" + S18T2Sha256(S18T2EligibleMarker), StringComparison.Ordinal),
                "S18-T2 repeat execute changed or duplicated the encrypted reverse map entry.");

            var lockResult = await S18T2OwnerAsync(executable, fixture.ControlPipe, "lock").ConfigureAwait(false);
            S18T2Success(lockResult, "S18-T2 final lock");
        }
        finally
        {
            if (service is not null)
            {
                try { service.Process.Kill(entireProcessTree: true); } catch { /* fixture teardown */ }
            }
        }
    }

    private static async Task S18T2CreateSourceAsync(
        string executable,
        SmokeFixture fixture,
        string sourceId,
        string zone,
        string sourcePath,
        byte[] marker)
    {
        await File.WriteAllBytesAsync(sourcePath, marker).ConfigureAwait(false);
        var created = await S18T2OwnerAsync(executable, fixture.ControlPipe,
            "source-create", "--resource-id", sourceId, "--zone-id", zone,
            "--title", "S18-T2 synthetic source " + sourceId,
            "--locator", Path.GetFullPath(sourcePath), "--version", "1", "--availability", "available").ConfigureAwait(false);
        S18T2Success(created, "S18-T2 source create " + sourceId);
        var locatorPath = Path.Combine(fixture.OutputDirectory, "locator-" + sourceId + ".json");
        await File.WriteAllTextAsync(locatorPath,
            JsonSerializer.Serialize(new { locator = Path.GetFullPath(sourcePath) }), new UTF8Encoding(false)).ConfigureAwait(false);
        var enroll = await S18T2OwnerAsync(executable, fixture.ControlPipe,
            "source-enroll", "--source-id", sourceId, "--zone-id", zone,
            "--format", "jsonl", "--format-version", "1", "--input-file", locatorPath).ConfigureAwait(false);
        S18T2Success(enroll, "S18-T2 source enroll " + sourceId);
        var retention = DateTimeOffset.UtcNow.AddDays(30).ToString("O", CultureInfo.InvariantCulture);
        var preserve = await S18T2OwnerAsync(executable, fixture.ControlPipe,
            "source-snapshot-preserve", "--source-id", sourceId, "--source-revision", "1",
            "--retain-until-utc", retention).ConfigureAwait(false);
        S18T2Success(preserve, "S18-T2 explicit snapshot preservation " + sourceId);
    }
    private static async Task<string> S18T2AuthoritySnapshotAsync(string executable, string controlPipe)
    {
        var builder = new StringBuilder();
        foreach (var command in new[] { "grants", "catalog-list", "collections", "sessions" })
        {
            var result = await S18T2OwnerAsync(executable, controlPipe, command).ConfigureAwait(false);
            S18T2Success(result, "S18-T2 authority snapshot " + command);
            builder.Append(command).Append(':').Append(result.StandardOutput).Append('\n');
        }

        return builder.ToString();
    }

    private static Task<CommandResult> S18T2OwnerAsync(string executable, string controlPipe, params string[] arguments)
    {
        var full = new string[arguments.Length + 3];
        full[0] = "owner";
        full[1] = arguments[0];
        full[2] = "--control-pipe";
        full[3] = controlPipe;
        Array.Copy(arguments, 1, full, 4, arguments.Length - 1);
        return RunOwnerCommandAsync(executable, full);
    }

    private static void S18T2Success(CommandResult result, string label) =>
        Require(result.ExitCode == 0,
            label + " failed (stdout=" + result.StandardOutput.Trim() + "; stderr=" + result.StandardError.Trim() + ").");

    private static string S18T2Field(string output, string linePrefix, string fieldPrefix)
    {
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith(linePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var start = line.IndexOf(fieldPrefix, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            start += fieldPrefix.Length;
            var end = line.IndexOf(' ', start);
            return end < 0 ? line[start..] : line[start..end];
        }

        return string.Empty;
    }

    private static string S18T2NodeIdFor(string treeOutput, string snapshotId)
    {
        foreach (var line in treeOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains(snapshotId, StringComparison.Ordinal))
            {
                continue;
            }

            var id = S18T2Field(line, "FILE ", "id=");
            if (id.Length > 0)
            {
                return id;
            }
        }

        return string.Empty;
    }

    private static int S18T2CountLines(string text, string linePrefix, string contains)
    {
        var count = 0;
        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith(linePrefix, StringComparison.Ordinal) &&
                line.Contains(contains, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static string S18T2Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string S18T2DirectoryDigest(string root)
    {
        var builder = new StringBuilder();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            // S18-T2 preview is an unaudited pure read: every vault file,
            // including the owner audit state, must stay byte-identical.
            builder.Append(Path.GetRelativePath(root, path)).Append(':')
                .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).Append('\n');
        }

        return builder.ToString();
    }
}
