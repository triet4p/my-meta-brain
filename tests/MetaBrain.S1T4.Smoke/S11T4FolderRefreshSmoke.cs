using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private sealed record S11T4Checkpoint(long ConsumedOffset, long JournalRevision, int EventCount);

    private sealed record S11T4JournalCapture(long ConsumedOffset, int EventCount, byte[][] PreservedBytes);

    internal static async Task RunS11T4FolderRefreshAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S11-T4 folder refresh smoke requires Windows named pipes and filesystem boundaries.");
        }

        Console.WriteLine("RUN S11-T4 owner folder refresh through the real service, encrypted store and CLI");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        string? junctionPath = null;
        var junctionCreated = false;
        var passphraseBytes = RandomNumberGenerator.GetBytes(24);
        var passphrase = Convert.ToBase64String(passphraseBytes);
        CryptographicOperations.ZeroMemory(passphraseBytes);
        try
        {
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provision = await RunOwnerCommandWithInputAsync(
                connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The synthetic S11-T4 vault did not provision (exit=" + provision.ExitCode + "; stderr=" + provision.StandardError.Trim() + ").");

            var folderRoot = Path.Combine(fixture.OutputDirectory, "s11t4-folder");
            var nested = Path.Combine(folderRoot, "nested");
            Directory.CreateDirectory(nested);
            var marker = "S11T4-MARKER-" + Guid.NewGuid().ToString("N")[..12];
            var baselinePath = Path.Combine(folderRoot, "baseline.jsonl");
            var currentPath = Path.Combine(folderRoot, "current.jsonl");
            var pausePath = Path.Combine(folderRoot, "pause.jsonl");
            var optOutPath = Path.Combine(folderRoot, "optout.jsonl");
            var revokePath = Path.Combine(folderRoot, "revoke.jsonl");
            var truncatedPath = Path.Combine(folderRoot, "truncated.jsonl");
            var replacedPath = Path.Combine(folderRoot, "replaced.jsonl");
            var excludedPath = Path.Combine(nested, "excluded-file.jsonl");
            var unknownRecord = S3T2CodexLine("2026-10-30T00:00:14.000Z", 14, "brand_new_record_type",
                "{\"synthetic\":true,\"id\":\"s11t4-preserved-baseline\"}");
            var baselineBytes = Encoding.UTF8.GetBytes(S3T2CodexSession("s11t4-baseline", marker) + unknownRecord);
            await File.WriteAllBytesAsync(baselinePath, baselineBytes).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(baselineBytes);
            foreach (var (path, sessionId) in new[]
                     {
                         (currentPath, "s11t4-current"),
                         (pausePath, "s11t4-pause"),
                         (optOutPath, "s11t4-optout"),
                         (revokePath, "s11t4-revoke"),
                         (truncatedPath, "s11t4-truncated"),
                         (replacedPath, "s11t4-replaced"),
                         (excludedPath, "s11t4-excluded")
                     })
            {
                await File.WriteAllTextAsync(path, S3T2CodexSession(sessionId, marker), new UTF8Encoding(false)).ConfigureAwait(false);
            }

            var outsideRoot = Path.Combine(fixture.OutputDirectory, "outside-folder-target");
            Directory.CreateDirectory(outsideRoot);
            var outsidePath = Path.Combine(outsideRoot, "outside-only.jsonl");
            await File.WriteAllTextAsync(outsidePath, S3T2CodexSession("s11t4-outside", marker), new UTF8Encoding(false)).ConfigureAwait(false);
            junctionPath = Path.Combine(folderRoot, "linked-outside");
            await CreateJunctionAsync(junctionPath, outsideRoot).ConfigureAwait(false);
            junctionCreated = true;

            var folderArgs = FolderCommand(fixture.ControlPipe, folderRoot, "**/*.jsonl", "**/excluded*.jsonl", recursive: true);
            var enrolledFolder = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, "ENROLL" + Environment.NewLine, folderArgs).ConfigureAwait(false);
            var folderId = SourceFolderEnrollmentGrammar.FolderIdFor(SourceFolderEnrollmentGrammar.NormalizeRoot(folderRoot));
            Require(enrolledFolder.ExitCode == 0 &&
                    enrolledFolder.StandardOutput.Contains("SOURCE FOLDER enrolled", StringComparison.Ordinal) &&
                    enrolledFolder.StandardOutput.Contains("members=7", StringComparison.Ordinal) &&
                    enrolledFolder.StandardOutput.Contains("refreshOptIn=false", StringComparison.Ordinal) &&
                    enrolledFolder.StandardOutput.Contains("reason=excluded_reparse_point", StringComparison.Ordinal),
                "Folder enrollment did not keep refresh consent off or exclude the selected excluded/reparse entries (stdout=" + enrolledFolder.StandardOutput.Trim() + "; stderr=" + enrolledFolder.StandardError.Trim() + ").");

            var baselineId = FolderMemberSourceId(folderRoot, baselinePath);
            var currentId = FolderMemberSourceId(folderRoot, currentPath);
            var pauseId = FolderMemberSourceId(folderRoot, pausePath);
            var optOutId = FolderMemberSourceId(folderRoot, optOutPath);
            var revokeId = FolderMemberSourceId(folderRoot, revokePath);
            var truncatedId = FolderMemberSourceId(folderRoot, truncatedPath);
            var replacedId = FolderMemberSourceId(folderRoot, replacedPath);
            var excludedId = FolderMemberSourceId(folderRoot, excludedPath);
            var outsideId = FolderMemberSourceId(folderRoot, Path.Combine(junctionPath, "outside-only.jsonl"));

            var currentBeforeOptInPath = Path.Combine(folderRoot, "current-before-optin.jsonl");
            await File.WriteAllTextAsync(currentBeforeOptInPath, S3T2CodexSession("s11t4-before-optin", marker), new UTF8Encoding(false)).ConfigureAwait(false);
            var currentBeforeOptInId = FolderMemberSourceId(folderRoot, currentBeforeOptInPath);
            var initialList = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(initialList.ExitCode == 0 && initialList.StandardOutput.Contains("SOURCES count=7", StringComparison.Ordinal),
                "Initial folder selection did not enroll the exact seven regular matching members.");

            CommandResult defaultOffRefresh;
            using (var held = new FileStream(baselinePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                _ = held;
                defaultOffRefresh = await RunOwnerCommandAsync(
                    connectionsExecutable, "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            }

            Require(defaultOffRefresh.ExitCode == 0 &&
                    S3T6RefreshStatus(defaultOffRefresh.StandardOutput, baselineId) == "skipped_folder_opted_out" &&
                    S3T6RefreshStatus(defaultOffRefresh.StandardOutput, currentId) == "skipped_folder_opted_out",
                "The default-off folder refresh opened a member or did not report the folder veto.");
            await RequireSourceAbsentAsync(connectionsExecutable, fixture.ControlPipe, currentBeforeOptInId).ConfigureAwait(false);
            Console.WriteLine("PASS selection confirmation remains default-off; no enrolled member or newly matching file is refreshed/admitted");

            var rawRetainUntil = DateTimeOffset.UtcNow.AddDays(30);
            var rawConsent = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-raw-preservation-optin",
                "--control-pipe", fixture.ControlPipe, "--source-id", baselineId,
                "--retain-until-utc", rawRetainUntil.ToString("O", CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(rawConsent.ExitCode == 0 && rawConsent.StandardOutput.Contains("rawPreservationOptIn=true", StringComparison.Ordinal),
                "The synthetic baseline member could not establish its separate raw-preservation consent.");

            var folderOptIn = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-folder-refresh-optin",
                "--control-pipe", fixture.ControlPipe, "--folder-id", folderId).ConfigureAwait(false);
            Require(folderOptIn.ExitCode == 0 &&
                    folderOptIn.StandardOutput.Contains("SOURCE FOLDER refresh_opted_in", StringComparison.Ordinal) &&
                    folderOptIn.StandardOutput.Contains("refreshOptIn=true", StringComparison.Ordinal),
                "The owner folder refresh opt-in command did not persist explicit consent.");
            var firstRefresh = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(firstRefresh.ExitCode == 0 && firstRefresh.StandardOutput.Contains("REFRESH count=", StringComparison.Ordinal),
                "Manual refresh through the shared coordinator failed (stdout=" + firstRefresh.StandardOutput.Trim() + "; stderr=" + firstRefresh.StandardError.Trim() + ").");
            var firstList = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(firstList.ExitCode == 0 && firstList.StandardOutput.Contains("SOURCES count=8", StringComparison.Ordinal) &&
                    firstList.StandardOutput.Contains($"SOURCE id={currentBeforeOptInId}", StringComparison.Ordinal) &&
                    firstList.StandardOutput.Contains("folderRefreshOptIn=true", StringComparison.Ordinal),
                "Explicit consent did not adopt the current matching file under stored folder rules (refresh=" +
                firstRefresh.StandardOutput.Trim() + "; list=" + firstList.StandardOutput.Trim() +
                "; stderr=" + firstList.StandardError.Trim() + ").");

            var baselineStatus = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", baselineId).ConfigureAwait(false);
            Require(baselineStatus.ExitCode == 0 && baselineStatus.StandardOutput.Contains("preservation=preserved", StringComparison.Ordinal),
                "Raw preservation was not durably recorded for the synthetic baseline member.");
            var preservedOffset = long.Parse(
                PrintedToken(baselineStatus.StandardOutput, "PRESERVED SOURCE RECORD offset=") ??
                throw new InvalidOperationException("The S11-T4 raw record offset was missing."), CultureInfo.InvariantCulture);
            var preservedOutput = Path.Combine(fixture.OutputDirectory, "s11t4-preserved.bin");
            var preservedRead = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-preserved-record-read",
                "--control-pipe", fixture.ControlPipe, "--source-id", baselineId,
                "--offset", preservedOffset.ToString(CultureInfo.InvariantCulture),
                "--output-file", preservedOutput).ConfigureAwait(false);
            var preservedBytes = await File.ReadAllBytesAsync(preservedOutput).ConfigureAwait(false);
            try
            {
                Require(preservedRead.ExitCode == 0 && preservedBytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(unknownRecord)),
                    "The encrypted journal did not return the exact saved raw record bytes.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(preservedBytes);
            }
            Console.WriteLine("PASS folder opt-in refreshes current and future matching files while preserving exact opted-in raw bytes");

            var tailStart = await ReadS11T4CheckpointAsync(connectionsExecutable, fixture.ControlPipe, currentBeforeOptInId).ConfigureAwait(false);
            var partialTail = S3T2CodexLine("2026-10-30T00:00:14.000Z", 14, "response_item",
                "{\"type\":\"message\",\"id\":\"s11t4-partial\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + marker + " partial tail\"}]}");
            await File.AppendAllTextAsync(currentBeforeOptInPath, partialTail[..^1], new UTF8Encoding(false)).ConfigureAwait(false);
            var partialRefresh = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            var afterPartial = await ReadS11T4CheckpointAsync(connectionsExecutable, fixture.ControlPipe, currentBeforeOptInId).ConfigureAwait(false);
            var partialStatus = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", currentBeforeOptInId).ConfigureAwait(false);
            Require(partialRefresh.ExitCode == 0 &&
                    S3T6RefreshStatus(partialRefresh.StandardOutput, currentBeforeOptInId) == "ingest_partial_pending" &&
                    afterPartial.ConsumedOffset == tailStart.ConsumedOffset && afterPartial.EventCount == tailStart.EventCount &&
                    long.Parse(PrintedToken(partialStatus.StandardOutput, "pendingTailBytes=") ??
                        throw new InvalidOperationException("The partial-tail byte count was missing."), CultureInfo.InvariantCulture) > 0,
                "A partial JSONL tail moved the checkpoint or was not left pending.");
            await File.AppendAllTextAsync(currentBeforeOptInPath, "\n", new UTF8Encoding(false)).ConfigureAwait(false);
            var completedTailRefresh = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            var afterCompletedTail = await ReadS11T4CheckpointAsync(connectionsExecutable, fixture.ControlPipe, currentBeforeOptInId).ConfigureAwait(false);
            var repeatedTailRefresh = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            var afterRepeatedTail = await ReadS11T4CheckpointAsync(connectionsExecutable, fixture.ControlPipe, currentBeforeOptInId).ConfigureAwait(false);
            Require(completedTailRefresh.ExitCode == 0 &&
                    (S3T6RefreshStatus(completedTailRefresh.StandardOutput, currentBeforeOptInId) is "ingest_advanced" or "ingest_idle") &&
                    afterCompletedTail.EventCount == tailStart.EventCount + 1 &&
                    afterCompletedTail.ConsumedOffset == new FileInfo(currentBeforeOptInPath).Length &&
                    repeatedTailRefresh.ExitCode == 0 && S3T6RefreshStatus(repeatedTailRefresh.StandardOutput, currentBeforeOptInId) == "ingest_idle" &&
                    afterRepeatedTail.ConsumedOffset == afterCompletedTail.ConsumedOffset &&
                    afterRepeatedTail.EventCount == afterCompletedTail.EventCount &&
                    afterRepeatedTail.JournalRevision == afterCompletedTail.JournalRevision,
                "Tail completion/retry was not exact-once and checkpoint-idempotent.");
            Console.WriteLine("PASS append, incomplete tail, completed-tail retry and repeated refresh are checkpoint-idempotent");

            var futurePath = Path.Combine(folderRoot, "future.jsonl");
            await File.WriteAllTextAsync(futurePath, S3T2CodexSession("s11t4-future", marker), new UTF8Encoding(false)).ConfigureAwait(false);
            var futureId = FolderMemberSourceId(folderRoot, futurePath);
            CommandResult reparseRefresh;
            using (var held = new FileStream(outsidePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                _ = held;
                reparseRefresh = await RunOwnerCommandAsync(
                    connectionsExecutable, "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            }
            Require(reparseRefresh.ExitCode == 0 &&
                    (S3T6RefreshStatus(reparseRefresh.StandardOutput, futureId) is "ingest_advanced" or "ingest_idle"),
                "A future matching regular file was not adopted under explicit folder consent.");
            await RequireSourceAbsentAsync(connectionsExecutable, fixture.ControlPipe, excludedId).ConfigureAwait(false);
            await RequireSourceAbsentAsync(connectionsExecutable, fixture.ControlPipe, outsideId).ConfigureAwait(false);
            var afterFutureList = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(afterFutureList.ExitCode == 0 && afterFutureList.StandardOutput.Contains($"SOURCE id={futureId}", StringComparison.Ordinal) &&
                    !afterFutureList.StandardOutput.Contains($"SOURCE id={excludedId}", StringComparison.Ordinal) &&
                    !afterFutureList.StandardOutput.Contains($"SOURCE id={outsideId}", StringComparison.Ordinal),
                "Folder discovery admitted an excluded or reparse-target file.");
            Console.WriteLine("PASS future matching files are admitted only within include/exclude, root and no-reparse boundaries");

            var pauseBefore = await ReadS11T4CheckpointAsync(connectionsExecutable, fixture.ControlPipe, pauseId).ConfigureAwait(false);
            var optOutBefore = await ReadS11T4CheckpointAsync(connectionsExecutable, fixture.ControlPipe, optOutId).ConfigureAwait(false);
            var revokeBefore = await ReadS11T4CheckpointAsync(connectionsExecutable, fixture.ControlPipe, revokeId).ConfigureAwait(false);
            var optedOutSource = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-refresh-optout", "--control-pipe", fixture.ControlPipe,
                "--source-id", optOutId).ConfigureAwait(false);
            CommandResult repeatedOptOut;
            using (var held = new FileStream(optOutPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                _ = held;
                repeatedOptOut = await RunOwnerCommandAsync(
                    connectionsExecutable, "owner", "source-refresh-optout", "--control-pipe", fixture.ControlPipe,
                    "--source-id", optOutId).ConfigureAwait(false);
            }
            var pausedSource = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-pause", "--control-pipe", fixture.ControlPipe,
                "--source-id", pauseId).ConfigureAwait(false);
            var revokedSource = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-revoke", "--control-pipe", fixture.ControlPipe,
                "--source-id", revokeId).ConfigureAwait(false);
            Require(optedOutSource.ExitCode == 0 && repeatedOptOut.ExitCode == 0 &&
                    pausedSource.ExitCode == 0 && revokedSource.ExitCode == 0,
                "Per-source refresh opt-out, pause, or revoke could not be applied while the folder was opted in.");
            await File.AppendAllTextAsync(optOutPath, S3T2CodexLine("2026-10-30T00:00:14.000Z", 14, "response_item",
                "{\"type\":\"message\",\"id\":\"s11t4-optout-after\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + marker + " vetoed\"}]}")).ConfigureAwait(false);
            await File.AppendAllTextAsync(pausePath, S3T2CodexLine("2026-10-30T00:00:14.000Z", 14, "response_item",
                "{\"type\":\"message\",\"id\":\"s11t4-paused-after\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + marker + " paused\"}]}")).ConfigureAwait(false);
            await File.AppendAllTextAsync(revokePath, S3T2CodexLine("2026-10-30T00:00:14.000Z", 14, "response_item",
                "{\"type\":\"message\",\"id\":\"s11t4-revoked-after\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + marker + " revoked\"}]}")).ConfigureAwait(false);
            var sourceVetoRefresh = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            var pauseAfter = await ReadS11T4CheckpointAsync(connectionsExecutable, fixture.ControlPipe, pauseId).ConfigureAwait(false);
            var optOutAfter = await ReadS11T4CheckpointAsync(connectionsExecutable, fixture.ControlPipe, optOutId).ConfigureAwait(false);
            var revokeAfter = await ReadS11T4CheckpointAsync(connectionsExecutable, fixture.ControlPipe, revokeId).ConfigureAwait(false);
            Require(sourceVetoRefresh.ExitCode == 0 &&
                    S3T6RefreshStatus(sourceVetoRefresh.StandardOutput, optOutId) == "skipped_source_opted_out" &&
                    S3T6RefreshStatus(sourceVetoRefresh.StandardOutput, pauseId) == "skipped_paused" &&
                    S3T6RefreshStatus(sourceVetoRefresh.StandardOutput, revokeId) == "skipped_revoked" &&
                    pauseAfter == pauseBefore && optOutAfter == optOutBefore && revokeAfter == revokeBefore,
                "Per-source opt-out/pause/revoke did not veto folder refresh before checkpoint movement.");
            Console.WriteLine("PASS individual opt-out, pause and revoke remain vetoes after folder consent");

            var folderOptOut = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-folder-refresh-optout",
                "--control-pipe", fixture.ControlPipe, "--folder-id", folderId).ConfigureAwait(false);
            var duringFolderOptOutPath = Path.Combine(folderRoot, "during-folder-optout.jsonl");
            await File.WriteAllTextAsync(duringFolderOptOutPath, S3T2CodexSession("s11t4-during-optout", marker), new UTF8Encoding(false)).ConfigureAwait(false);
            var duringFolderOptOutId = FolderMemberSourceId(folderRoot, duringFolderOptOutPath);
            CommandResult folderOptOutRefresh;
            using (var held = new FileStream(baselinePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                _ = held;
                folderOptOutRefresh = await RunOwnerCommandAsync(
                    connectionsExecutable, "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            }
            var listWhileFolderOptedOut = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(folderOptOut.ExitCode == 0 && folderOptOut.StandardOutput.Contains("refreshOptIn=false", StringComparison.Ordinal) &&
                    folderOptOutRefresh.ExitCode == 0 &&
                    S3T6RefreshStatus(folderOptOutRefresh.StandardOutput, baselineId) == "skipped_folder_opted_out" &&
                    listWhileFolderOptedOut.ExitCode == 0 && !listWhileFolderOptedOut.StandardOutput.Contains($"SOURCE id={duringFolderOptOutId}", StringComparison.Ordinal),
                "Folder opt-out did not stop reads and membership discovery at the folder boundary.");
            var reOptedFolder = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-folder-refresh-optin",
                "--control-pipe", fixture.ControlPipe, "--folder-id", folderId).ConfigureAwait(false);
            var resumedFolderRefresh = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            var listAfterFolderReOptIn = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(reOptedFolder.ExitCode == 0 && resumedFolderRefresh.ExitCode == 0 &&
                    listAfterFolderReOptIn.ExitCode == 0 &&
                    listAfterFolderReOptIn.StandardOutput.Contains($"SOURCE id={duringFolderOptOutId}", StringComparison.Ordinal) &&
                    listAfterFolderReOptIn.StandardOutput.Contains("folderRefreshOptIn=true", StringComparison.Ordinal) &&
                    S3T6RefreshStatus(resumedFolderRefresh.StandardOutput, optOutId) == "skipped_source_opted_out" &&
                    S3T6RefreshStatus(resumedFolderRefresh.StandardOutput, pauseId) == "skipped_paused" &&
                    S3T6RefreshStatus(resumedFolderRefresh.StandardOutput, revokeId) == "skipped_revoked",
                "Folder re-opt-in failed to adopt current matching membership or overrode member vetoes.");
            Console.WriteLine("PASS folder opt-out stops membership/read work; re-opt-in resumes without overriding member vetoes");

            File.Delete(duringFolderOptOutPath);
            var deletedRefresh = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            var deletedStatus = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", duringFolderOptOutId).ConfigureAwait(false);
            var deletedCanonical = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", duringFolderOptOutId).ConfigureAwait(false);
            Require(deletedRefresh.ExitCode == 0 && S3T6RefreshStatus(deletedRefresh.StandardOutput, duringFolderOptOutId) == "source_missing" &&
                    deletedStatus.ExitCode == 0 && deletedStatus.StandardOutput.Contains("availability=missing", StringComparison.Ordinal) &&
                    deletedCanonical.ExitCode == 0 && deletedCanonical.StandardOutput.Contains($"SOURCE id={duringFolderOptOutId}", StringComparison.Ordinal),
                "Deleting a member revoked, purged or erased canonical/history state instead of retaining it as missing.");
            Console.WriteLine("PASS deleted member remains canonical/history and reports missing without automatic purge/revoke");

            var truncatedBefore = await ReadS11T4CheckpointAsync(connectionsExecutable, fixture.ControlPipe, truncatedId).ConfigureAwait(false);
            var replacedBefore = await ReadS11T4CheckpointAsync(connectionsExecutable, fixture.ControlPipe, replacedId).ConfigureAwait(false);
            await File.WriteAllBytesAsync(truncatedPath, Array.Empty<byte>()).ConfigureAwait(false);
            var replacementMarker = new string('X', marker.Length);
            await File.WriteAllTextAsync(replacedPath, S3T2CodexSession("s11t4-replaced", replacementMarker), new UTF8Encoding(false)).ConfigureAwait(false);
            var generationRefresh = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            var truncatedStatus = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", truncatedId).ConfigureAwait(false);
            var replacedStatus = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", replacedId).ConfigureAwait(false);
            Require(generationRefresh.ExitCode == 0 &&
                    S3T6RefreshStatus(generationRefresh.StandardOutput, truncatedId) == "source_generation_changed" &&
                    S3T6RefreshStatus(generationRefresh.StandardOutput, replacedId) == "source_generation_changed" &&
                    truncatedStatus.ExitCode == 0 && truncatedStatus.StandardOutput.Contains("quarantine=generation_truncated", StringComparison.Ordinal) &&
                    replacedStatus.ExitCode == 0 && replacedStatus.StandardOutput.Contains("quarantine=generation_replaced", StringComparison.Ordinal) &&
                    await ReadS11T4CheckpointAsync(connectionsExecutable, fixture.ControlPipe, truncatedId).ConfigureAwait(false) == truncatedBefore &&
                    await ReadS11T4CheckpointAsync(connectionsExecutable, fixture.ControlPipe, replacedId).ConfigureAwait(false) == replacedBefore,
                "Truncation/replacement did not quarantine without moving saved checkpoints.");
            Console.WriteLine("PASS truncation and replacement quarantine without advancing journal checkpoints");

            var crashCheckpoint = await ReadS11T4CheckpointAsync(connectionsExecutable, fixture.ControlPipe, currentBeforeOptInId).ConfigureAwait(false);
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var locked = await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.Contains("vault=locked", StringComparison.Ordinal),
                "The service did not restart locked after its process crash.");
            var unlocked = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0, "The service did not unlock the real encrypted store after restart.");
            var afterRestartRefresh = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            var afterRestartCheckpoint = await ReadS11T4CheckpointAsync(connectionsExecutable, fixture.ControlPipe, currentBeforeOptInId).ConfigureAwait(false);
            var afterRestartList = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(afterRestartRefresh.ExitCode == 0 &&
                    (S3T6RefreshStatus(afterRestartRefresh.StandardOutput, currentBeforeOptInId) is "ingest_idle" or "ingest_advanced") &&
                    afterRestartCheckpoint == crashCheckpoint && afterRestartList.ExitCode == 0 &&
                    afterRestartList.StandardOutput.Contains("folderRefreshOptIn=true", StringComparison.Ordinal),
                "Crash/restart failed to preserve folder consent and the committed journal checkpoint without duplicates.");
            Console.WriteLine("PASS real service process crash/restart reopens encrypted folder consent and journal checkpoint without duplicates");

            var backgroundPath = Path.Combine(folderRoot, "background-after-restart.jsonl");
            await File.WriteAllTextAsync(backgroundPath, S3T2CodexSession("s11t4-background", marker), new UTF8Encoding(false)).ConfigureAwait(false);
            var backgroundId = FolderMemberSourceId(folderRoot, backgroundPath);
            var backgroundObserved = false;
            for (var attempt = 0; attempt < 50 && !backgroundObserved; attempt++)
            {
                var list = await RunOwnerCommandAsync(
                    connectionsExecutable, "owner", "source-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
                Require(list.ExitCode == 0, "Owner source-list failed while waiting for the shipped background refresh loop.");
                backgroundObserved = list.StandardOutput.Contains($"SOURCE id={backgroundId}", StringComparison.Ordinal);
                if (!backgroundObserved)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                }
            }

            Require(backgroundObserved,
                "The shipped background loop did not admit a future matching file after persisted folder consent and unlock.");
            var backgroundCheckpoint = await ReadS11T4CheckpointAsync(connectionsExecutable, fixture.ControlPipe, backgroundId).ConfigureAwait(false);
            var backgroundRefresh = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            var backgroundAfterManual = await ReadS11T4CheckpointAsync(connectionsExecutable, fixture.ControlPipe, backgroundId).ConfigureAwait(false);
            Require(backgroundCheckpoint.EventCount == S3T2CodexRecordCount &&
                    backgroundRefresh.ExitCode == 0 && S3T6RefreshStatus(backgroundRefresh.StandardOutput, backgroundId) == "ingest_idle" &&
                    backgroundAfterManual == backgroundCheckpoint,
                "The background pass did not checkpoint through the shared coordinator or the following manual pass duplicated it.");
            Console.WriteLine("PASS actual service background pass admits and checkpoints future folder members; manual retry is idempotent");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }

            if (junctionCreated && junctionPath is not null)
            {
                RemoveSyntheticJunction(junctionPath);
            }
        }

        await RunS11T4CancellationBoundariesAsync().ConfigureAwait(false);
    }

    private static async Task RunS11T4CancellationBoundariesAsync()
    {
        Console.WriteLine("RUN S11-T4 deterministic read-cancellation boundaries against the real encrypted store");
        using var reader = new S11T4BlockingSourceReader();
        using var harness = S3T2CodexCoordinatorHarness.Create(reader);
        var folderRoot = Path.Combine(harness.Root, "refresh-cancellation-folder");
        Directory.CreateDirectory(folderRoot);
        var sourcePath = Path.Combine(folderRoot, "preserved.jsonl");
        const string marker = "S11T4-CANCEL-MARKER";
        var initialUnknown = S3T2CodexLine("2026-10-30T00:00:14.000Z", 14, "brand_new_record_type",
            "{\"synthetic\":true,\"id\":\"saved-before-cancel\"}");
        await File.WriteAllTextAsync(sourcePath, S3T2CodexSession("s11t4-cancel-source", marker) + initialUnknown, new UTF8Encoding(false)).ConfigureAwait(false);
        var retention = DateTimeOffset.UtcNow.AddDays(30);
        var preview = await harness.OwnerSendAsync(new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.SourceFolderPreviewOperation,
            ZoneId: "s11t4-cancellation-zone",
            SourceFormat: CodexSessionNormalizer.FormatName,
            SourceFormatVersion: 1,
            SourceFolderRoot: folderRoot,
            SourceFolderRecursive: true,
            SourceFolderIncludePatterns: ["**/*.jsonl"],
            SourceFolderExcludePatterns: [],
            SourceFolderRawPreservationRetainUntilUtc: retention)).ConfigureAwait(false);
        var folderPreview = preview.SourceFolderPreview
            ?? throw new InvalidOperationException("The real-store folder preview did not include its owner preview data.");
        Require(preview.Status == "source_folder_previewed",
            "The in-process real-store folder preview failed (status=" + preview.Status + "; error=" + preview.Error + ").");
        var enrolled = await harness.OwnerSendAsync(new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.SourceFolderEnrollOperation,
            SourceFolderPreviewId: folderPreview.PreviewId,
            SourceFolderConfirmation: "ENROLL")).ConfigureAwait(false);
        var folderEnrollment = enrolled.SourceFolderEnrollment
            ?? throw new InvalidOperationException("The real-store folder enrollment reply omitted its enrollment.");
        Require(enrolled.Status == "source_folder_enrolled" && !folderEnrollment.RefreshOptIn,
            "The in-process folder confirmation did not preserve refresh-default-off.");
        var folderId = folderEnrollment.FolderId;
        var sourceId = FolderMemberSourceId(folderRoot, sourcePath);
        var optedIn = await harness.OwnerSendAsync(new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.SourceFolderRefreshOptInOperation,
            ResourceId: folderId)).ConfigureAwait(false);
        Require(optedIn.Status == "source_folder_refresh_opted_in",
            "The in-process real-store folder consent could not be enabled.");
        var baseline = await harness.OwnerSendAsync(new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.SourceRefreshOperation)).ConfigureAwait(false);
        Require(baseline.Status == "sources_refreshed" &&
                baseline.SourceRefresh?.Single(result => result.SourceId == sourceId).Status == "ingest_advanced",
            "The in-process baseline did not save the v2 raw-preservation journal.");

        var savedJournal = CaptureS11T4Journal(harness, sourceId);
        try
        {
            Require(savedJournal.PreservedBytes.Length == 1 &&
                    savedJournal.PreservedBytes[0].AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(initialUnknown)),
                "The test baseline did not preserve the exact first unsupported record.");
            var sourceOptOut = await harness.OwnerSendAsync(new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceRefreshOptOutOperation,
                ResourceId: sourceId)).ConfigureAwait(false);
            Require(sourceOptOut.Status == "source_refresh_opted_out", "The synthetic source opt-out action was not audited.");
            using (var operation = harness.Lifecycle.TryBeginOperation()
                   ?? throw new InvalidOperationException("The legacy-state fixture could not open an encrypted vault operation."))
            {
                _ = operation.UpdateSourceEnrollmentState(state =>
                {
                    var normalized = SourceEnrollmentAuthority.ValidateAndNormalizeState(state);
                    var enrollments = normalized.Enrollments
                        .Select(enrollment => string.Equals(enrollment.SourceId, sourceId, StringComparison.Ordinal)
                            ? enrollment with { RefreshOptIn = false, RefreshOptOut = false }
                            : enrollment)
                        .ToArray();
                    return (normalized with { Enrollments = enrollments }, true);
                });
            }

            var legacyFolderOptOut = await harness.OwnerSendAsync(new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceFolderRefreshOptOutOperation,
                ResourceId: folderId)).ConfigureAwait(false);
            var legacyFolderReOptIn = await harness.OwnerSendAsync(new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceFolderRefreshOptInOperation,
                ResourceId: folderId)).ConfigureAwait(false);
            var migratedList = await harness.OwnerSendAsync(new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceListOperation)).ConfigureAwait(false);
            Require(legacyFolderOptOut.Status == "source_folder_refresh_opted_out" &&
                    legacyFolderReOptIn.Status == "source_folder_refresh_opted_in" &&
                    migratedList.SourceEnrollments?.Single(item => item.SourceId == sourceId).RefreshOptOut == true,
                "A retained legacy per-source opt-out was not migrated into the durable veto before folder re-consent.");
            ServiceReply migratedRefresh;
            using (var held = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                _ = held;
                migratedRefresh = await harness.OwnerSendAsync(new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.SourceRefreshOperation)).ConfigureAwait(false);
            }
            Require(migratedRefresh.Status == "sources_refreshed" &&
                    migratedRefresh.SourceRefresh?.Single(result => result.SourceId == sourceId).Status == "skipped_source_opted_out",
                "Folder consent overwrote a retained legacy source opt-out or opened its file.");
            RequireS11T4JournalUnchanged(harness, sourceId, savedJournal, "legacy source opt-out");
            var explicitSourceReOptIn = await harness.OwnerSendAsync(new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceRefreshOptInOperation,
                ResourceId: sourceId)).ConfigureAwait(false);
            Require(explicitSourceReOptIn.Status == "source_refresh_opted_in" &&
                    explicitSourceReOptIn.SourceEnrollment?.RefreshOptOut == false,
                "A later explicit source opt-in did not clear the durable member veto.");
            Console.WriteLine("PASS retained legacy opt-out migrates to a durable member veto; explicit source re-opt-in is required to clear it");
            var pauseRecord = S3T2CodexLine("2026-10-30T00:00:15.000Z", 15, "brand_new_record_type",
                "{\"synthetic\":true,\"id\":\"cancelled-by-pause\"}");
            await File.AppendAllTextAsync(sourcePath, pauseRecord, new UTF8Encoding(false)).ConfigureAwait(false);
            reader.BlockNext(sourcePath);
            var pausedRefreshTask = Task.Run(() => harness.OwnerSendAsync(new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.SourceRefreshOperation)));
            Require(reader.WaitForBlockedRead(TimeSpan.FromSeconds(10)), "The pause-boundary refresh did not reach its active source read.");
            var pause = await harness.OwnerSendAsync(new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourcePauseOperation,
                ResourceId: sourceId)).ConfigureAwait(false);
            var pausedRefresh = await pausedRefreshTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Require(pause.Status == "source_paused" && pausedRefresh.Status == "sources_refreshed" &&
                    pausedRefresh.SourceRefresh?.Single(result => result.SourceId == sourceId).Status == "skipped_source_stop_requested",
                "A source pause did not stop the in-flight read at its source boundary.");
            RequireS11T4JournalUnchanged(harness, sourceId, savedJournal, "pause");
            var resume = await harness.OwnerSendAsync(new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceResumeOperation,
                ResourceId: sourceId)).ConfigureAwait(false);
            Require(resume.Status == "source_resumed", "The in-process cancellation source could not resume after pause.");
            Console.WriteLine("PASS active source pause cancels a blocked read before checkpoint publication and retains saved raw bytes");

            var folderRecord = S3T2CodexLine("2026-10-30T00:00:16.000Z", 16, "brand_new_record_type",
                "{\"synthetic\":true,\"id\":\"cancelled-by-folder-optout\"}");
            await File.AppendAllTextAsync(sourcePath, folderRecord, new UTF8Encoding(false)).ConfigureAwait(false);
            reader.BlockNext(sourcePath);
            var folderOptOutRefreshTask = Task.Run(() => harness.OwnerSendAsync(new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.SourceRefreshOperation)));
            Require(reader.WaitForBlockedRead(TimeSpan.FromSeconds(10)), "The folder-opt-out refresh did not reach its active source read.");
            var folderOptOut = await harness.OwnerSendAsync(new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceFolderRefreshOptOutOperation,
                ResourceId: folderId)).ConfigureAwait(false);
            var folderOptOutRefresh = await folderOptOutRefreshTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Require(folderOptOut.Status == "source_folder_refresh_opted_out" &&
                    folderOptOutRefresh.Status == "sources_refreshed" &&
                    folderOptOutRefresh.SourceRefresh?.Single(result => result.SourceId == sourceId).Status == "skipped_folder_stop_requested",
                "Folder opt-out did not cancel the blocked member read at its folder boundary.");
            RequireS11T4JournalUnchanged(harness, sourceId, savedJournal, "folder opt-out");
            var folderReOptIn = await harness.OwnerSendAsync(new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.SourceFolderRefreshOptInOperation,
                ResourceId: folderId)).ConfigureAwait(false);
            Require(folderReOptIn.Status == "source_folder_refresh_opted_in", "Folder consent did not resume after explicit re-opt-in.");
            Console.WriteLine("PASS folder opt-out cancels an active member read before the shared checkpoint boundary");

            var lockRecord = S3T2CodexLine("2026-10-30T00:00:17.000Z", 17, "brand_new_record_type",
                "{\"synthetic\":true,\"id\":\"cancelled-by-lock\"}");
            await File.AppendAllTextAsync(sourcePath, lockRecord, new UTF8Encoding(false)).ConfigureAwait(false);
            reader.BlockNext(sourcePath);
            var lockToken = harness.Lifecycle.RefreshCancellation;
            var lockedRefreshTask = Task.Run(() => harness.OwnerSendAsync(new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.SourceRefreshOperation), lockToken));
            Require(reader.WaitForBlockedRead(TimeSpan.FromSeconds(10)), "The lock-boundary refresh did not reach its active source read.");
            var lockTask = harness.Lifecycle.LockAsync();
            try
            {
                _ = await lockedRefreshTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await lockTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Require(string.Equals(harness.Lifecycle.State, "locked", StringComparison.Ordinal),
                "The in-process lifecycle did not finish the lock drain after canceling the active read.");
            harness.Restart();
            RequireS11T4JournalUnchanged(harness, sourceId, savedJournal, "lock/restart");
            var resumedRefresh = await harness.OwnerSendAsync(new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.SourceRefreshOperation)).ConfigureAwait(false);
            var resumedJournal = CaptureS11T4Journal(harness, sourceId);
            try
            {
                Require(resumedRefresh.Status == "sources_refreshed" &&
                        resumedRefresh.SourceRefresh?.Single(result => result.SourceId == sourceId).Status == "ingest_advanced" &&
                        resumedJournal.PreservedBytes.Length == savedJournal.PreservedBytes.Length + 3 &&
                        savedJournal.PreservedBytes[0].AsSpan().SequenceEqual(resumedJournal.PreservedBytes[0]),
                    "Restart did not resume the uncommitted raw records exactly once while retaining the previous saved bytes.");
            }
            finally
            {
                ClearS11T4JournalCapture(resumedJournal);
            }

            Console.WriteLine("PASS lock cancels active read, drains the vault, and restart resumes only uncommitted raw records");
        }
        finally
        {
            ClearS11T4JournalCapture(savedJournal);
        }
    }

    private static async Task RequireSourceAbsentAsync(string executable, string controlPipe, string sourceId)
    {
        var result = await RunOwnerCommandAsync(
            executable, "owner", "source-read", "--control-pipe", controlPipe,
            "--resource-id", sourceId).ConfigureAwait(false);
        Require(result.ExitCode != 0 && result.StandardError.Contains("invalid_request", StringComparison.Ordinal),
            "An excluded or unconsented folder path created canonical source metadata.");
    }

    private static async Task<S11T4Checkpoint> ReadS11T4CheckpointAsync(string executable, string controlPipe, string sourceId)
    {
        var status = await RunOwnerCommandAsync(
            executable, "owner", "source-status", "--control-pipe", controlPipe,
            "--source-id", sourceId).ConfigureAwait(false);
        Require(status.ExitCode == 0 && status.StandardOutput.Contains("checkpoint consumedOffset=", StringComparison.Ordinal),
            "The owner could not read the encrypted source checkpoint for " + sourceId + ".");
        return new S11T4Checkpoint(
            long.Parse(PrintedToken(status.StandardOutput, "consumedOffset=") ?? throw new InvalidOperationException("Checkpoint offset missing."), CultureInfo.InvariantCulture),
            long.Parse(PrintedToken(status.StandardOutput, "journalRevision=") ?? throw new InvalidOperationException("Journal revision missing."), CultureInfo.InvariantCulture),
            int.Parse(PrintedToken(status.StandardOutput, "events=") ?? throw new InvalidOperationException("Event count missing."), CultureInfo.InvariantCulture));
    }

    private static S11T4JournalCapture CaptureS11T4Journal(S3T2CodexCoordinatorHarness harness, string sourceId)
    {
        var journal = harness.DecodeJournal(sourceId, 1)
            ?? throw new InvalidOperationException("The encrypted journal was missing during the cancellation-boundary check.");
        try
        {
            var preserved = journal.Version2?.PreservedRecords
                .Select(record => record.Bytes.ToArray())
                .ToArray() ?? Array.Empty<byte[]>();
            return new S11T4JournalCapture(journal.ConsumedOffset, journal.Events.Length, preserved);
        }
        finally
        {
            if (journal.Version2 is { } version2)
            {
                foreach (var record in version2.PreservedRecords)
                {
                    CryptographicOperations.ZeroMemory(record.Bytes);
                }
            }
        }
    }

    private static void RequireS11T4JournalUnchanged(
        S3T2CodexCoordinatorHarness harness,
        string sourceId,
        S11T4JournalCapture expected,
        string boundary)
    {
        var actual = CaptureS11T4Journal(harness, sourceId);
        try
        {
            Require(actual.ConsumedOffset == expected.ConsumedOffset && actual.EventCount == expected.EventCount &&
                    PreservedBytesEqual(actual.PreservedBytes, expected.PreservedBytes),
                "The " + boundary + " boundary moved the checkpoint or lost previously saved raw bytes.");
        }
        finally
        {
            ClearS11T4JournalCapture(actual);
        }
    }

    private static bool PreservedBytesEqual(byte[][] left, byte[][] right) =>
        left.Length == right.Length &&
        Enumerable.Range(0, left.Length).All(index => left[index].AsSpan().SequenceEqual(right[index]));

    private static void ClearS11T4JournalCapture(S11T4JournalCapture capture)
    {
        foreach (var bytes in capture.PreservedBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private sealed class S11T4BlockingSourceReader : ISourceSnapshotReader, IDisposable
    {
        private readonly EnrolledSourceFileReader _reader = new();
        private readonly ManualResetEventSlim _readStarted = new(false);
        private string? _blockedLocator;
        private int _blockNext;

        public void BlockNext(string locator)
        {
            _blockedLocator = Path.GetFullPath(locator);
            _readStarted.Reset();
            Volatile.Write(ref _blockNext, 1);
        }

        public bool WaitForBlockedRead(TimeSpan timeout) => _readStarted.Wait(timeout);

        public SourceSnapshotResult TryReadSnapshot(string locator) =>
            TryReadSnapshot(locator, CancellationToken.None);

        public SourceSnapshotResult TryReadSnapshot(string locator, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _blockNext) == 1 &&
                string.Equals(Path.GetFullPath(locator), _blockedLocator, StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Exchange(ref _blockNext, 0) == 1)
            {
                _readStarted.Set();
                if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException("The deterministic S11-T4 source read was not canceled at its boundary.");
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            return _reader.TryReadSnapshot(locator, cancellationToken);
        }

        public void Dispose() => _readStarted.Dispose();
    }
}
