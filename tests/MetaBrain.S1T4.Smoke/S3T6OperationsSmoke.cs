using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;
namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private sealed record S3T6ServiceExit(int ProcessId, DateTime StartedAtUtc);

    /// <summary>
    /// S3-T6 owner ingestion operations and safe background refresh, proven
    /// over the real service process and the real owner CLI over named
    /// pipes: allowlist listing with checkpoint/error/cache scope, explicit
    /// pause stopping reads, opt-in/opt-out refresh consent, incremental
    /// refresh over the allowlist only, owner-opt-in background updates without
    /// a manual command, active read/job cessation under pause/disable/lock,
    /// pause/append/resume/restart without duplicates, lock stopping every
    /// private read and refresh, and encrypted diagnostics with no transcript
    /// or secret spill. A research note capture proves the typed-source path
    /// stays refreshable without a journal. Every fixture is locally generated
    /// synthetic data; no private source, filesystem polling outside the
    /// allowlist, autonomous unlock or plaintext key is involved.
    /// </summary>
    internal static async Task RunS3T6OperationsAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S3-T6 operations smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S3-T6 owner ingestion operations and safe background refresh");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        var serviceExits = new List<S3T6ServiceExit>();
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false))
                    .Contains("vault=uninitialized", StringComparison.Ordinal),
                "The S3-T6 service fixture did not start uninitialized.");
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "Provision did not create the S3-T6 synthetic vault (exit=" + provisioned.ExitCode + ").");

            const string zoneId = "s3t6-operations-zone";
            const string codexId = "s3t6-codex-session";
            const string ompId = "s3t6-omp-session";
            const string noteId = "s3t6-research-note";
            var marker = "S3T6-OPERATIONS-MARKER-" + Guid.NewGuid().ToString("N")[..12];

            var codexLocator = Path.Combine(fixture.OutputDirectory, "s3t6-codex.jsonl");
            var codexBytes = Encoding.UTF8.GetBytes(S3T2CodexSession("s3t6-codex-alpha", marker));
            await File.WriteAllBytesAsync(codexLocator, codexBytes).ConfigureAwait(false);
            var codexRequest = Path.Combine(fixture.OutputDirectory, "s3t6-codex-enroll.json");
            await File.WriteAllTextAsync(codexRequest, JsonSerializer.Serialize(new { locator = codexLocator })).ConfigureAwait(false);

            var ompLocator = Path.Combine(fixture.OutputDirectory, "s3t6-omp.jsonl");
            var ompBytes = Encoding.UTF8.GetBytes(S3T3OmpBase("s3t6-omp-alpha", marker));
            await File.WriteAllBytesAsync(ompLocator, ompBytes).ConfigureAwait(false);
            var ompRequest = Path.Combine(fixture.OutputDirectory, "s3t6-omp-enroll.json");
            await File.WriteAllTextAsync(ompRequest, JsonSerializer.Serialize(new { locator = ompLocator })).ConfigureAwait(false);

            var codexEnroll = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", codexId, "--zone-id", zoneId,
                "--format", CodexSessionNormalizer.FormatName, "--format-version", "1",
                "--input-file", codexRequest).ConfigureAwait(false);
            Require(codexEnroll.ExitCode == 0 && codexEnroll.StandardOutput.Contains($"SOURCE id={codexId}", StringComparison.Ordinal),
                "Owner enrollment of the S3-T6 Codex fixture failed (exit=" + codexEnroll.ExitCode + "; stderr=" + codexEnroll.StandardError.Trim() + ").");
            var ompEnroll = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", ompId, "--zone-id", zoneId,
                "--format", OmpSessionNormalizer.FormatName, "--format-version", "1",
                "--input-file", ompRequest).ConfigureAwait(false);
            Require(ompEnroll.ExitCode == 0 && ompEnroll.StandardOutput.Contains($"SOURCE id={ompId}", StringComparison.Ordinal),
                "Owner enrollment of the S3-T6 OMP fixture failed (exit=" + ompEnroll.ExitCode + "; stderr=" + ompEnroll.StandardError.Trim() + ").");

            var noteText = "S3-T6 research note " + marker + ": claim and question stay verbatim; https://example.invalid/s3t6/note.\n";
            var noteBytes = Encoding.UTF8.GetBytes(noteText);
            var notePath = Path.Combine(fixture.OutputDirectory, "s3t6-note.txt");
            await File.WriteAllBytesAsync(notePath, noteBytes).ConfigureAwait(false);
            var captured = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-capture", "--control-pipe", fixture.ControlPipe,
                "--resource-id", noteId, "--zone-id", zoneId, "--input-file", notePath).ConfigureAwait(false);
            Require(captured.ExitCode == 0 && captured.StandardOutput.Contains($"RESEARCH captured id={noteId}", StringComparison.Ordinal),
                "Owner research capture failed in the S3-T6 walkthrough (exit=" + captured.ExitCode + "; stderr=" + captured.StandardError.Trim() + ").");
            Console.WriteLine("PASS owner enrolls Codex and OMP fixtures plus a research note through the real owner CLI");

            var agentPause = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "source.pause",
                resourceId = codexId
            }).ConfigureAwait(false);
            Require(ReplyCode(agentPause) == "forbidden",
                "The agent channel reached a source pause operation.");
            var agentRefresh = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "source.refresh"
            }).ConfigureAwait(false);
            Require(ReplyCode(agentRefresh) == "forbidden",
                "The agent channel reached the source refresh operation.");
            var agentList = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "source.list"
            }).ConfigureAwait(false);
            Require(ReplyCode(agentList) == "forbidden",
                "The agent channel reached the source list operation.");
            Console.WriteLine("PASS the agent channel is refused for every S3-T6 operation by the transport allowlist");

            // Error scope: unknown sources, double pause/resume and unknown
            // refresh consent are explicit refusals with no state change.
            var unknownStatus = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", "s3t6-source-unknown").ConfigureAwait(false);
            Require(unknownStatus.ExitCode == 3 && unknownStatus.StandardError.Contains("source_not_enrolled", StringComparison.Ordinal),
                "An unknown source status was not refused explicitly.");
            var unknownRefreshConsent = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-refresh-optin", "--control-pipe", fixture.ControlPipe,
                "--source-id", "s3t6-source-unknown").ConfigureAwait(false);
            Require(unknownRefreshConsent.ExitCode == 4 && unknownRefreshConsent.StandardError.Contains("source_not_enrolled", StringComparison.Ordinal),
                "Refresh opt-in for an unknown source was not refused explicitly.");
            var notPausedResume = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-resume", "--control-pipe", fixture.ControlPipe,
                "--source-id", codexId).ConfigureAwait(false);
            Require(notPausedResume.ExitCode == 4 && notPausedResume.StandardError.Contains("source_not_paused", StringComparison.Ordinal),
                "Resume of a non-paused source was not refused explicitly.");
            Console.WriteLine("PASS unknown sources and invalid pause/resume/consent transitions are explicit errors");

            var codexStatus = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", codexId).ConfigureAwait(false);
            Require(codexStatus.ExitCode == 0 &&
                    codexStatus.StandardOutput.Contains("availability=available", StringComparison.Ordinal) &&
                    codexStatus.StandardOutput.Contains("checkpoint consumedOffset=0", StringComparison.Ordinal) &&
                    codexStatus.StandardOutput.Contains("refreshOptIn=false", StringComparison.Ordinal) &&
                    codexStatus.StandardOutput.Contains("journalCiphertextBytes=", StringComparison.Ordinal) &&
                    codexStatus.StandardOutput.Contains("snapshotCiphertextBytes=", StringComparison.Ordinal) &&
                    codexStatus.StandardOutput.Contains("snapshotRevision=", StringComparison.Ordinal),
                "Owner status did not report source, checkpoint, consent and encrypted cache scope (stdout=" + codexStatus.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS owner status reports source, checkpoint, errors, consent and encrypted cache scope");

            var refreshBeforeOptIn = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(refreshBeforeOptIn.ExitCode == 0 &&
                    refreshBeforeOptIn.StandardOutput.Contains("REFRESH count=2", StringComparison.Ordinal) &&
                    refreshBeforeOptIn.StandardOutput.Contains("status=skipped_not_opted_in", StringComparison.Ordinal),
                "Refresh ran without an explicit owner opt-in (stdout=" + refreshBeforeOptIn.StandardOutput.Trim() + ").");
            var idleCheck = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", codexId).ConfigureAwait(false);
            Require(idleCheck.StandardOutput.Contains("checkpoint consumedOffset=0", StringComparison.Ordinal),
                "A skipped refresh moved a checkpoint.");
            // No-read proof: hold the opted-out fixture under an exclusive
            // write lock. Any attempted open would surface a different
            // refusal; only a state-first skip can answer
            // skipped_not_opted_in without touching the transcript.
            using (var exclusive = new FileStream(codexLocator, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                _ = exclusive;
                var skippedLocked = await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
                Require(S3T6RefreshStatus(skippedLocked.StandardOutput, codexId) == "skipped_not_opted_in",
                    "A skipped refresh opened the transcript instead of refusing state-first.");
            }

            Console.WriteLine("PASS refresh without opt-in skips every source with no read and no checkpoint movement");

            var optInCodex = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-refresh-optin", "--control-pipe", fixture.ControlPipe,
                "--source-id", codexId).ConfigureAwait(false);
            Require(optInCodex.ExitCode == 0 && optInCodex.StandardOutput.Contains("refreshOptIn=true", StringComparison.Ordinal),
                "Owner refresh opt-in failed for the Codex source (stderr=" + optInCodex.StandardError.Trim() + ").");
            var optInOmp = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-refresh-optin", "--control-pipe", fixture.ControlPipe,
                "--source-id", ompId).ConfigureAwait(false);
            Require(optInOmp.ExitCode == 0 && optInOmp.StandardOutput.Contains("refreshOptIn=true", StringComparison.Ordinal),
                "Owner refresh opt-in failed for the OMP source (stderr=" + optInOmp.StandardError.Trim() + ").");
            Console.WriteLine("PASS owner opts both runtime sources into safe incremental refresh");

            var firstRefresh = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(firstRefresh.ExitCode == 0,
                "The first opted-in refresh failed (exit=" + firstRefresh.ExitCode + "; stderr=" + firstRefresh.StandardError.Trim() + ").");
            var firstCodexAdvanced = S3T6RefreshStatus(firstRefresh.StandardOutput, codexId) == "ingest_advanced" &&
                    S3T6RefreshEvents(firstRefresh.StandardOutput, codexId) == S3T2CodexRecordCount;
            var firstOmpAdvanced = S3T6RefreshStatus(firstRefresh.StandardOutput, ompId) == "ingest_advanced" &&
                    S3T6RefreshEvents(firstRefresh.StandardOutput, ompId) == S3T3OmpRecordCount;
            Require((firstCodexAdvanced || S3T6RefreshStatus(firstRefresh.StandardOutput, codexId) == "ingest_idle") &&
                    (firstOmpAdvanced || S3T6RefreshStatus(firstRefresh.StandardOutput, ompId) == "ingest_idle"),
                "The first opted-in refresh reported an unexpected state (stdout=" + firstRefresh.StandardOutput.Trim() + ").");
            var codexAfterFirst = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", codexId).ConfigureAwait(false);
            Require(codexAfterFirst.StandardOutput.Contains($"checkpoint consumedOffset={codexBytes.Length}", StringComparison.Ordinal) &&
                    codexAfterFirst.StandardOutput.Contains($"events={S3T2CodexRecordCount}", StringComparison.Ordinal) &&
                    codexAfterFirst.StandardOutput.Contains("journalCiphertextBytes=", StringComparison.Ordinal) &&
                    codexAfterFirst.StandardOutput.Contains("snapshotCiphertextBytes=", StringComparison.Ordinal) &&
                    codexAfterFirst.StandardOutput.Contains("snapshotRevision=", StringComparison.Ordinal),
                "Codex checkpoint or encrypted cache scope did not persist after refresh (stdout=" + codexAfterFirst.StandardOutput.Trim() + ").");
            if (firstCodexAdvanced && firstOmpAdvanced)
            {
                Console.WriteLine("PASS opted-in refresh ingests both runtime sources incrementally from persisted checkpoints (manual pass)");
            }
            else
            {
                Console.WriteLine("PASS opted-in sources reached persisted checkpoints (background loop won the first race; manual pass observed idle)");
            }

            var secondRefresh = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(S3T6RefreshStatus(secondRefresh.StandardOutput, codexId) == "ingest_idle" &&
                    S3T6RefreshStatus(secondRefresh.StandardOutput, ompId) == "ingest_idle" &&
                    S3T6RefreshEvents(secondRefresh.StandardOutput, codexId) == 0 &&
                    S3T6RefreshEvents(secondRefresh.StandardOutput, ompId) == 0,
                "Refresh re-import was not idempotent (stdout=" + secondRefresh.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS refresh re-import is idempotent with no duplicates");

            var paused = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-pause", "--control-pipe", fixture.ControlPipe,
                "--source-id", codexId).ConfigureAwait(false);
            Require(paused.ExitCode == 0 && paused.StandardOutput.Contains("life=paused", StringComparison.Ordinal),
                "Owner pause failed for the Codex source (stderr=" + paused.StandardError.Trim() + ").");
            var pausedStatus = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", codexId).ConfigureAwait(false);
            Require(pausedStatus.ExitCode == 0 && pausedStatus.StandardOutput.Contains("life=paused", StringComparison.Ordinal),
                "Paused status did not report the paused life state.");
            var doublePause = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-pause", "--control-pipe", fixture.ControlPipe,
                "--source-id", codexId).ConfigureAwait(false);
            Require(doublePause.ExitCode == 4 && doublePause.StandardError.Contains("source_already_paused", StringComparison.Ordinal),
                "A second pause was not refused explicitly.");
            var pausedIngest = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", codexId).ConfigureAwait(false);
            Require(pausedIngest.ExitCode == 4 && pausedIngest.StandardError.Contains("source_paused", StringComparison.Ordinal),
                "A paused source was ingested instead of refused (exit=" + pausedIngest.ExitCode + "; stderr=" + pausedIngest.StandardError.Trim() + ").");
            // No-read proof for the paused gate: with the paused fixture
            // held under an exclusive share mode, an attempted open would
            // surface source_unreadable instead. Only a state-first refusal
            // can answer source_paused.
            using (var pausedExclusive = new FileStream(codexLocator, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                _ = pausedExclusive;
                var pausedHeld = await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                    "--source-id", codexId).ConfigureAwait(false);
                Require(pausedHeld.ExitCode == 4 && pausedHeld.StandardError.Contains("source_paused", StringComparison.Ordinal),
                    "A paused source was opened instead of refused state-first (exit=" + pausedHeld.ExitCode + "; stderr=" + pausedHeld.StandardError.Trim() + ").");
            }

            await File.AppendAllTextAsync(codexLocator, S3T2CodexAppendRecords("s3t6-codex-alpha", marker)).ConfigureAwait(false);
            var refreshWhilePaused = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(S3T6RefreshStatus(refreshWhilePaused.StandardOutput, codexId) == "skipped_paused" &&
                    S3T6RefreshStatus(refreshWhilePaused.StandardOutput, ompId) == "ingest_idle",
                "Refresh read a paused source (stdout=" + refreshWhilePaused.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS pausing a source stops status reads, ingest and refresh with no transcript open");

            var resumed = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-resume", "--control-pipe", fixture.ControlPipe,
                "--source-id", codexId).ConfigureAwait(false);
            Require(resumed.ExitCode == 0 && resumed.StandardOutput.Contains("life=enrolled", StringComparison.Ordinal) &&
                    resumed.StandardOutput.Contains($"checkpoint consumedOffset={codexBytes.Length}", StringComparison.Ordinal),
                "Owner resume did not restore the persisted checkpoint (stdout=" + resumed.StandardOutput.Trim() + ").");
            var resumedRefresh = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            var resumedCodexStatus = S3T6RefreshStatus(resumedRefresh.StandardOutput, codexId);
            Require(resumedCodexStatus == "ingest_advanced" || resumedCodexStatus == "ingest_idle",
                "Resume refresh reported an unexpected state (stdout=" + resumedRefresh.StandardOutput.Trim() + ").");
            var codexAfterResume = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", codexId).ConfigureAwait(false);
            Require(codexAfterResume.StandardOutput.Contains($"events={S3T2CodexRecordCount + S3T2CodexAppendRecordCount}", StringComparison.Ordinal),
                "Resume did not consume only the appended records exactly once (stdout=" + codexAfterResume.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS resume continues from the persisted checkpoint with only new records consumed");

            var optOutOmp = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-refresh-optout", "--control-pipe", fixture.ControlPipe,
                "--source-id", ompId).ConfigureAwait(false);
            Require(optOutOmp.ExitCode == 0 && optOutOmp.StandardOutput.Contains("refreshOptIn=false", StringComparison.Ordinal),
                "Owner refresh opt-out failed for the OMP source.");
            await File.AppendAllTextAsync(ompLocator, S3T3OmpAppendRecords(marker)).ConfigureAwait(false);
            var refreshAfterOptOut = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(S3T6RefreshStatus(refreshAfterOptOut.StandardOutput, ompId) == "skipped_not_opted_in",
                "Refresh read an opted-out source (stdout=" + refreshAfterOptOut.StandardOutput.Trim() + ").");
            var ompStatusAfterOptOut = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", ompId).ConfigureAwait(false);
            Require(ompStatusAfterOptOut.StandardOutput.Contains($"events={S3T3OmpRecordCount}", StringComparison.Ordinal),
                "An opted-out refresh moved the OMP checkpoint.");
            var reOptInOmp = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-refresh-optin", "--control-pipe", fixture.ControlPipe,
                "--source-id", ompId).ConfigureAwait(false);
            Require(reOptInOmp.ExitCode == 0, "Re opt-in failed for the OMP source.");
            var refreshReOpted = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            var reOptedStatus = S3T6RefreshStatus(refreshReOpted.StandardOutput, ompId);
            Require(reOptedStatus == "ingest_advanced" || reOptedStatus == "ingest_idle",
                "Re opt-in refresh reported an unexpected state (stdout=" + refreshReOpted.StandardOutput.Trim() + ").");
            var ompAfterReOptIn = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", ompId).ConfigureAwait(false);
            Require(ompAfterReOptIn.StandardOutput.Contains($"events={S3T3OmpRecordCount + S3T3OmpAppendRecordCount}", StringComparison.Ordinal),
                "Re opt-in did not consume the appended OMP records exactly once (stdout=" + ompAfterReOptIn.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS refresh opt-out stops reads; re opt-in resumes incrementally without duplicates");

            var revoked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-revoke", "--control-pipe", fixture.ControlPipe,
                "--source-id", codexId).ConfigureAwait(false);
            Require(revoked.ExitCode == 0 && revoked.StandardOutput.Contains("life=revoked", StringComparison.Ordinal),
                "Owner revoke failed for the Codex source.");
            var revokedRefresh = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(S3T6RefreshStatus(revokedRefresh.StandardOutput, codexId) == "skipped_revoked",
                "Refresh read a revoked source.");
            var reEnrolled = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", codexId, "--zone-id", zoneId,
                "--format", CodexSessionNormalizer.FormatName, "--format-version", "1",
                "--input-file", codexRequest).ConfigureAwait(false);
            Require(reEnrolled.ExitCode == 0 && reEnrolled.StandardOutput.Contains("generation=2", StringComparison.Ordinal),
                "Re-enrollment after revoke did not start a new generation.");
            var reEnrollStatus = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", codexId).ConfigureAwait(false);
            Require(reEnrollStatus.StandardOutput.Contains("generation=2", StringComparison.Ordinal) &&
                    reEnrollStatus.StandardOutput.Contains("checkpoint consumedOffset=0", StringComparison.Ordinal),
                "Re-enrolled generation did not start on a fresh checkpoint.");
            Console.WriteLine("PASS revoke stops refresh; explicit re-enrollment starts a new generation on a fresh checkpoint");

            var reEnrollOptIn = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-refresh-optin", "--control-pipe", fixture.ControlPipe,
                "--source-id", codexId).ConfigureAwait(false);
            Require(reEnrollOptIn.ExitCode == 0, "Opt-in failed for the re-enrolled Codex source.");
            var reEnrollRefresh = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(S3T6RefreshStatus(reEnrollRefresh.StandardOutput, codexId) == "ingest_advanced",
                "Refresh did not ingest the re-enrolled generation.");

            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The owner could not lock the S3-T6 vault.");
            var treeBefore = S3T6SnapshotTree(fixture.ServiceDirectoryPath);
            var lockedStatus = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", ompId).ConfigureAwait(false);
            Require(lockedStatus.ExitCode == 3 && lockedStatus.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "A locked vault served a source status read (exit=" + lockedStatus.ExitCode + "; stderr=" + lockedStatus.StandardError.Trim() + ").");
            var lockedRefresh = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockedRefresh.ExitCode == 4 && lockedRefresh.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "A locked vault served a background refresh.");
            var lockedList = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockedList.ExitCode == 3 && lockedList.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "A locked vault served the allowlist listing (exit=" + lockedList.ExitCode + "; stderr=" + lockedList.StandardError.Trim() + ").");
            var lockedNote = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", noteId).ConfigureAwait(false);
            Require(lockedNote.ExitCode == 4 && lockedNote.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "A locked vault served the research note.");
            var treeAfter = S3T6SnapshotTree(fixture.ServiceDirectoryPath);
            Require(treeBefore.SequenceEqual(treeAfter, StringComparer.Ordinal),
                "A locked refresh attempt changed the service tree (a plaintext queue or half-saved write).");
            Console.WriteLine("PASS a locked vault stops every private read and refresh with no write and no plaintext queue");

            var stopping = service;
            serviceExits.Add(await StopS3T6ServiceAsync(stopping).ConfigureAwait(false));
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false))
                    .Contains("vault=locked", StringComparison.Ordinal),
                "The restarted S3-T6 service did not start locked.");
            var unlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0, "The owner could not unlock the restarted S3-T6 vault.");
            var ompAfterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", ompId).ConfigureAwait(false);
            Require(ompAfterRestart.ExitCode == 0 &&
                    ompAfterRestart.StandardOutput.Contains($"events={S3T3OmpRecordCount + S3T3OmpAppendRecordCount}", StringComparison.Ordinal) &&
                    ompAfterRestart.StandardOutput.Contains("refreshOptIn=true", StringComparison.Ordinal),
                "OMP checkpoint or refresh consent did not survive restart/unlock (stdout=" + ompAfterRestart.StandardOutput.Trim() + ").");
            var codexAfterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", codexId).ConfigureAwait(false);
            Require(codexAfterRestart.ExitCode == 0 && codexAfterRestart.StandardOutput.Contains("generation=2", StringComparison.Ordinal),
                "Re-enrolled Codex generation did not survive restart/unlock.");
            var noteAfterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", noteId, "--output-file", Path.Combine(fixture.OutputDirectory, "s3t6-note-read.bin")).ConfigureAwait(false);
            Require(noteAfterRestart.ExitCode == 0 &&
                    (await File.ReadAllBytesAsync(Path.Combine(fixture.OutputDirectory, "s3t6-note-read.bin")).ConfigureAwait(false)).AsSpan().SequenceEqual(noteBytes),
                "The research note did not survive restart/unlock byte-for-byte.");
            var refreshAfterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-refresh", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(S3T6RefreshStatus(refreshAfterRestart.StandardOutput, ompId) == "ingest_idle",
                "Unlock did not resume from persisted checkpoints without duplicates.");
            Console.WriteLine("PASS restart stays locked; unlock resumes checkpoints and consent without duplicates");

            var vaultFiles = Directory.GetFiles(fixture.VaultDirectoryPath, "*", SearchOption.AllDirectories);
            Require(vaultFiles.Length >= 3, "The encrypted vault did not persist its private files.");
            foreach (var path in vaultFiles)
            {
                var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                Require(!text.Contains(marker, StringComparison.Ordinal) &&
                        !text.Contains(codexLocator, StringComparison.Ordinal) &&
                        !text.Contains(ompLocator, StringComparison.Ordinal) &&
                        !text.Contains(noteText, StringComparison.Ordinal) &&
                        !text.Contains(passphrase, StringComparison.Ordinal),
                    "An encrypted vault file persisted plaintext content, locator or key: " + Path.GetFileName(path) + ".");
            }

            var settingsText = await File.ReadAllTextAsync(fixture.SettingsPath).ConfigureAwait(false);
            Require(!settingsText.Contains(marker, StringComparison.Ordinal) &&
                    !settingsText.Contains(codexLocator, StringComparison.Ordinal) &&
                    !settingsText.Contains(ompLocator, StringComparison.Ordinal) &&
                    !settingsText.Contains(passphrase, StringComparison.Ordinal),
                "The service settings persisted private enrollment metadata or a key.");
            Require(!HasTemporaryFiles(fixture.VaultDirectoryPath) && !HasTemporaryFiles(fixture.ServiceDirectoryPath),
                "S3-T6 left a plaintext temp file in the vault or service tree.");
            var codexAfterBytes = await File.ReadAllBytesAsync(codexLocator).ConfigureAwait(false);
            var ompAfterBytes = await File.ReadAllBytesAsync(ompLocator).ConfigureAwait(false);
            Require(codexAfterBytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(string.Concat(
                    S3T2CodexSession("s3t6-codex-alpha", marker),
                    S3T2CodexAppendRecords("s3t6-codex-alpha", marker)))) &&
                    ompAfterBytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(string.Concat(
                        S3T3OmpBase("s3t6-omp-alpha", marker),
                        S3T3OmpAppendRecords(marker)))),
                "The read-only refresh modified an original fixture.");

            var finalList = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(finalList.ExitCode == 0 && finalList.StandardOutput.Contains("SOURCES count=2", StringComparison.Ordinal) &&
                    finalList.StandardOutput.Contains("journalCiphertextBytes=", StringComparison.Ordinal) &&
                    finalList.StandardOutput.Contains("snapshotCiphertextBytes=", StringComparison.Ordinal),
                "Final allowlist listing did not report the encrypted cache scope.");
            Console.WriteLine("PASS allowlist listing reports the actual encrypted cache scope with no locator echo");
        }
        finally
        {
            if (service is not null)
            {
                var stopping = service;
                serviceExits.Add(await StopS3T6ServiceAsync(stopping).ConfigureAwait(false));
                service = null;
            }

            foreach (var exit in serviceExits)
            {
                Console.WriteLine($"CLEANUP service pid={exit.ProcessId} startedUtc={exit.StartedAtUtc:O} exited=yes exactHandle=true treeKill=false");
            }
        }

        Console.WriteLine("PASS pause/append/resume/restart preserve checkpoints without duplicates across both runtime sources");
        Console.WriteLine("PASS refresh runs only for opted-in sources while unlocked with no polling outside the allowlist");
        Console.WriteLine("PASS encrypted persistence, lock boundaries and original-byte preservation hold end to end");

        await RunS3T6BackgroundPhaseAsync().ConfigureAwait(false);
    }
    /// <summary>
    /// S3-T6-A2 in-process background phase: the real shipped background refresh
    /// service (the same loop the named-pipe service runs) against the real
    /// handler/coordinator/vault stack with the shipped Codex registry. It proves
    /// the behaviors the pipe phase cannot deterministically isolate: background
    /// updates with no manual command, active cessation under pause/disable/lock,
    /// allowlist-only boundary, and checkpoint resume without duplicates.
    /// </summary>
    private static async Task RunS3T6BackgroundPhaseAsync()
    {
        Console.WriteLine("RUN S3-T6 background refresh service (in-process, shipped loop)");
        using var harness = S3T2CodexCoordinatorHarness.Create();
        const string zoneId = "s3t6-background-zone";
        const string sourceId = "s3t6-background-source";
        const string outsideId = "s3t6-background-outside";
        var locator = Path.Combine(harness.Root, "background-session.jsonl");
        var outsideLocator = Path.Combine(harness.Root, "outside-session.jsonl");
        var marker = "S3T6-BACKGROUND-MARKER-" + Guid.NewGuid().ToString("N")[..12];

        ServiceRequest Enroll(string id, string path) => new(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.SourceEnrollOperation,
            id,
            zoneId,
            SourceLocator: path,
            SourceFormat: CodexSessionNormalizer.FormatName,
            SourceFormatVersion: CodexSessionNormalizer.FormatVersionValue);
        ServiceRequest Simple(string operation, string id) => new(
            ServiceRequestHandler.CurrentProtocolVersion, operation, id);
        ServiceRequest RefreshAll() => new(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.SourceRefreshOperation);

        await File.WriteAllTextAsync(locator, S3T2CodexSession("s3t6-background-alpha", marker)).ConfigureAwait(false);
        await File.WriteAllTextAsync(outsideLocator, S3T2CodexSession("s3t6-background-outside", marker)).ConfigureAwait(false);
        var enrolled = await harness.OwnerSendAsync(Enroll(sourceId, locator)).ConfigureAwait(false);
        Require(enrolled.Status == "source_enrolled",
            "Background enrollment failed (status=" + enrolled.Status + "; error=" + enrolled.Error + ").");
        var enrolledOutside = await harness.OwnerSendAsync(Enroll(outsideId, outsideLocator)).ConfigureAwait(false);
        Require(enrolledOutside.Status == "source_enrolled",
            "Outside-allowlist enrollment failed (status=" + enrolledOutside.Status + "; error=" + enrolledOutside.Error + ").");

        var optIn = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceRefreshOptInOperation, sourceId)).ConfigureAwait(false);
        Require(optIn.Status == "source_refresh_opted_in",
            "Background opt-in failed (status=" + optIn.Status + "; error=" + optIn.Error + ").");
        var manualBaseline = await harness.OwnerSendAsync(RefreshAll()).ConfigureAwait(false);
        var manualJournal = harness.DecodeJournal(sourceId, 1);
        Console.WriteLine($"BASELINE manual refresh status={manualBaseline.Status} error={manualBaseline.Error} journalEvents={manualJournal?.Events.Length.ToString() ?? "null"} vaultState={harness.Lifecycle.State}");

        var backgroundService = new SourceRefreshBackgroundService(harness.Handler, harness.Lifecycle);
        using var stop = new CancellationTokenSource();
        var loop = backgroundService.RunAsync(stop.Token);
        try
        {
            Require(manualBaseline.Status == "sources_refreshed" && (manualJournal?.Events.Length ?? -1) == S3T2CodexRecordCount,
                "Manual refresh baseline failed in-process (status=" + manualBaseline.Status + "; error=" + manualBaseline.Error + ").");
            Console.WriteLine("PASS manual refresh baseline ingests in-process (path shared with background loop)");

            var outsideBefore = harness.DecodeJournal(outsideId, 1);
            Require(outsideBefore is null,
                "The fixture setup ingested a source the owner never opted in.");
            using (var exclusive = new FileStream(outsideLocator, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                _ = exclusive;
                await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
                Require(harness.DecodeJournal(outsideId, 1) is null,
                    "The background loop opened a non-opted-in source instead of skipping state-first.");
            }

            Console.WriteLine("PASS background refresh reads only opted-in allowlist sources (allowlist-only boundary)");

            await File.AppendAllTextAsync(locator, S3T2CodexAppendRecords("s3t6-background-alpha", marker)).ConfigureAwait(false);
            var appendedObserved = false;
            for (var attempt = 0; attempt < 24 && !appendedObserved; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                appendedObserved = (harness.DecodeJournal(sourceId, 1)?.Events.Length ?? -1) == S3T2CodexRecordCount + S3T2CodexAppendRecordCount;
            }

            Require(appendedObserved,
                "The background loop did not advance the appended source without a manual refresh.");
            Console.WriteLine("PASS background loop advances an appended source with no manual command");
            await File.AppendAllTextAsync(locator, S3T2CodexLine("2026-10-12T00:00:16.000Z", 16, "response_item",
                "{\"type\":\"message\",\"id\":\"msg-user-3\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + marker + " paused follow-up\"}]}")).ConfigureAwait(false);
            var paused = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourcePauseOperation, sourceId)).ConfigureAwait(false);
            Require(paused.Status == "source_paused",
                "Background pause failed (status=" + paused.Status + "; error=" + paused.Error + ").");
            var pausedEvents = harness.DecodeJournal(sourceId, 1)?.Events.Length ?? -1;
            await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
            Require((harness.DecodeJournal(sourceId, 1)?.Events.Length ?? -2) == pausedEvents,
                "The background loop read a paused source instead of stopping at its boundary.");
            Console.WriteLine("PASS pausing a source stops in-flight background reads with no checkpoint movement");

            var resumed = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceResumeOperation, sourceId)).ConfigureAwait(false);
            Require(resumed.Status == "source_resumed",
                "Background resume failed (status=" + resumed.Status + "; error=" + resumed.Error + ").");
            var resumedObserved = false;
            for (var attempt = 0; attempt < 24 && !resumedObserved; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                resumedObserved = (harness.DecodeJournal(sourceId, 1)?.Events.Length ?? -1) == pausedEvents + 1;
            }

            Require(resumedObserved,
                "Resume did not let the background loop consume only the appended record.");
            Console.WriteLine("PASS resume lets the background loop consume only new records without duplicates");

            var optOut = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceRefreshOptOutOperation, sourceId)).ConfigureAwait(false);
            Require(optOut.Status == "source_refresh_opted_out",
                "Background opt-out failed (status=" + optOut.Status + "; error=" + optOut.Error + ").");
            await File.AppendAllTextAsync(locator, S3T2CodexLine("2026-10-12T00:00:17.000Z", 17, "response_item",
                "{\"type\":\"message\",\"id\":\"msg-user-4\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + marker + " opted-out follow-up\"}]}")).ConfigureAwait(false);
            var optOutEvents = harness.DecodeJournal(sourceId, 1)?.Events.Length ?? -1;
            await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
            Require((harness.DecodeJournal(sourceId, 1)?.Events.Length ?? -2) == optOutEvents,
                "The background loop read an opted-out source.");
            Console.WriteLine("PASS opt-out stops background reads; checkpoints stay put");

            var reOptIn = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceRefreshOptInOperation, sourceId)).ConfigureAwait(false);
            Require(reOptIn.Status == "source_refresh_opted_in",
                "Background re opt-in failed (status=" + reOptIn.Status + "; error=" + reOptIn.Error + ").");
            var eventsBeforeLock = harness.DecodeJournal(sourceId, 1)?.Events.Length ?? -1;
            var lockTask = harness.Lifecycle.LockAsync();
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try
                {
                    _ = await harness.OwnerSendAsync(RefreshAll(), cancelled.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            await lockTask.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Require(string.Equals(harness.Lifecycle.State, "locked", StringComparison.Ordinal),
                "The lock did not engage during the background phase.");
            var lockedRefresh = await harness.OwnerSendAsync(RefreshAll()).ConfigureAwait(false);
            Require(lockedRefresh.Error == "vault_locked",
                "A locked vault served a background refresh (status=" + lockedRefresh.Status + "; error=" + lockedRefresh.Error + ").");
            Console.WriteLine("PASS lock engages while background work is in flight; locked refresh denied with no commit");

            Require(await harness.UnlockAsync().ConfigureAwait(false),
                "Unlock after the background lock failed.");
            var unlockObserved = false;
            for (var attempt = 0; attempt < 24 && !unlockObserved; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                var journal = harness.DecodeJournal(sourceId, 1);
                unlockObserved = journal is not null && journal.Events.Length == eventsBeforeLock + 1;
            }

            Require(unlockObserved,
                "Unlock did not resume the background loop from the persisted checkpoint without duplicates.");
            Console.WriteLine("PASS unlock resumes background refresh from checkpoints without duplicates");

            harness.Restart();
            var afterRestart = harness.DecodeJournal(sourceId, 1);
            Require(afterRestart is not null && afterRestart.Events.Length == eventsBeforeLock + 1,
                "Restart did not keep the persisted checkpoint without duplicates.");
            Console.WriteLine("PASS restart keeps checkpoints without duplicates under the background loop");
        }
        finally
        {
            stop.Cancel();
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("The background loop did not stop on its owned handle.");
            }
        }
    }

    private static async Task<S3T6ServiceExit> StopS3T6ServiceAsync(RunningService service)
    {
        var process = service.Process;
        var exit = new S3T6ServiceExit(process.Id, process.StartTime.ToUniversalTime());
        if (!process.HasExited)
        {
            process.Kill();
        }

        await process.WaitForExitAsync().WaitAsync(CleanupTimeout).ConfigureAwait(false);
        await Task.WhenAll(service.StandardOutputDrain, service.StandardErrorDrain)
            .WaitAsync(CleanupTimeout).ConfigureAwait(false);
        Require(process.HasExited, "The exact S3-T6-owned service process did not exit after cleanup.");
        process.Dispose();
        return exit;
    }

    private static string S3T6RefreshLine(string output, string sourceId)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("REFRESH source=", StringComparison.Ordinal) &&
                line.Contains("source=" + sourceId + " ", StringComparison.Ordinal))
            {
                return line;
            }
        }

        return string.Empty;
    }

    private static string S3T6RefreshField(string line, string field)
    {
        foreach (var part in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith(field + "=", StringComparison.Ordinal))
            {
                return part[(field.Length + 1)..];
            }
        }

        return string.Empty;
    }

    private static string S3T6RefreshStatus(string output, string sourceId) =>
        S3T6RefreshField(S3T6RefreshLine(output, sourceId), "status");

    private static int S3T6RefreshEvents(string output, string sourceId) =>
        int.TryParse(S3T6RefreshField(S3T6RefreshLine(output, sourceId), "eventsAdded"), out var events) ? events : -1;

    private static string[] S3T6SnapshotTree(string root)
    {
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path) + "|" + new FileInfo(path).Length + "|" +
                File.GetLastWriteTimeUtc(path).ToString("O", System.Globalization.CultureInfo.InvariantCulture))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
