using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Index;
using MetaBrain.Core.Security;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    /// <summary>
    /// S3-T1 source enrollment and incremental ingestion coordinator, proven in
    /// two phases:
    /// <list type="bullet">
    /// <item><description>Phase A drives the real service process and real owner CLI over named pipes: enrollment, revocation, transport-level agent refusal, locked-service refusal, explicit unsupported-format diagnostics on the shipped binary, encrypted persistence, and original-byte preservation.</description></item>
    /// <item><description>Phase B drives the same production code path (handler, coordinator, vault lifecycle, encrypted store, enrolled-source reader) in-process, with a nonshipping synthetic normalizer registered through the real domain-normalization contract, which is the only way to exercise a valid incremental path before the real Codex/OMP parsers exist (S3-T2/S3-T3).</description></item>
    /// </list>
    /// No private source is read anywhere in this smoke; every fixture is
    /// locally generated synthetic data.
    /// </summary>
    internal static async Task RunS3T1SourceIngestionAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S3-T1 source ingestion smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S3-T1 source enrollment and incremental ingestion coordinator");
        await RunS3T1OwnerServicePhaseAsync(connectionsExecutable).ConfigureAwait(false);
        await RunS3T1CoordinatorPhaseAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Phase A: real service process plus real owner CLI over the actual owner
    /// and agent pipes.
    /// </summary>
    private static async Task RunS3T1OwnerServicePhaseAsync(string connectionsExecutable)
    {
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false))
                    .Contains("vault=uninitialized", StringComparison.Ordinal),
                "The S3-T1 service fixture did not start uninitialized.");
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "Provision did not create the S3-T1 synthetic vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");

            const string zoneId = "s3t1-private-zone";
            const string sourceId = "s3t1-source-alpha";
            var locator = Path.Combine(fixture.OutputDirectory, "synthetic-session.jsonl");
            var marker = "S3T1-SYNTHETIC-PAYLOAD-MARKER-" + Guid.NewGuid().ToString("N")[..12];
            var sourceBytes = Encoding.UTF8.GetBytes(S3T1SyntheticSession(marker, 3, "alpha"));
            await File.WriteAllBytesAsync(locator, sourceBytes).ConfigureAwait(false);
            var locatorRequestPath = Path.Combine(fixture.OutputDirectory, "enroll-request.json");
            await File.WriteAllTextAsync(locatorRequestPath, JsonSerializer.Serialize(new { locator })).ConfigureAwait(false);

            var enroll = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId, "--zone-id", zoneId,
                "--format", SyntheticLineNormalizer.FormatName, "--format-version", "1",
                "--input-file", locatorRequestPath).ConfigureAwait(false);
            Require(enroll.ExitCode == 0 &&
                    enroll.StandardOutput.Contains($"SOURCE id={sourceId}", StringComparison.Ordinal) &&
                    enroll.StandardOutput.Contains("life=enrolled", StringComparison.Ordinal) &&
                    enroll.StandardOutput.Contains("generation=1", StringComparison.Ordinal),
                "Owner enrollment of the exact synthetic fixture failed (exit=" + enroll.ExitCode + "; stdout=" + enroll.StandardOutput.Trim() + "; stderr=" + enroll.StandardError.Trim() + ").");
            Require(!enroll.StandardOutput.Contains(locator, StringComparison.Ordinal),
                "The owner enrollment reply echoed the private locator.");
            Console.WriteLine("PASS owner enrolls one exact fixture through the real owner CLI; the reply never echoes the locator");

            var agentEnroll = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "source.enroll",
                resourceId = sourceId
            }).ConfigureAwait(false);
            Require(ReplyCode(agentEnroll) == "forbidden",
                "The agent channel reached a source enrollment operation.");
            var agentIngest = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "source.ingest",
                resourceId = sourceId
            }).ConfigureAwait(false);
            Require(ReplyCode(agentIngest) == "forbidden",
                "The agent channel reached a source ingestion operation.");
            var agentStatus = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "source.status",
                resourceId = sourceId
            }).ConfigureAwait(false);
            Require(ReplyCode(agentStatus) == "forbidden",
                "The agent channel reached a source status operation.");
            Console.WriteLine("PASS the agent channel is refused for every source ingestion operation by the transport allowlist");

            // Unknown/unmapped sources are refused before any file open: the
            // fixture is held under an exclusive share mode, so an attempted
            // open would surface source_unreadable instead. Only a state-first
            // rejection can answer source_not_enrolled.
            using (var exclusive = new FileStream(locator, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                _ = exclusive;
                var unknown = await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                    "--source-id", "s3t1-source-unknown").ConfigureAwait(false);
                Require(unknown.ExitCode == 4 && unknown.StandardError.Contains("source_not_enrolled", StringComparison.Ordinal),
                    "An unknown source was not rejected before the transcript open (exit=" + unknown.ExitCode + "; stderr=" + unknown.StandardError.Trim() + ").");

                var unmapped = await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                    "--source-id", "s3t1-source-unmapped").ConfigureAwait(false);
                Require(unmapped.ExitCode == 3 && unmapped.StandardError.Contains("source_not_enrolled", StringComparison.Ordinal),
                    "An unmapped source status did not fail closed.");

                // Dispatch happens before the open: with the fixture
                // writer-held, the shipped service (no parser registered)
                // still answers the explicit unsupported diagnostic rather
                // than attempting any read. The reader's own lock detection
                // is proven in the coordinator phase where a normalizer is
                // registered.
                var busy = await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                    "--source-id", sourceId).ConfigureAwait(false);
                Require(busy.ExitCode == 0 && busy.StandardOutput.Contains("status=ingest_unsupported_format", StringComparison.Ordinal),
                    "A writer-held source did not reach the explicit dispatch-first diagnostic (exit=" + busy.ExitCode + "; stdout=" + busy.StandardOutput.Trim() + "; stderr=" + busy.StandardError.Trim() + ").");
            }
            Console.WriteLine("PASS unknown/unmapped sources are rejected before open; format dispatch also precedes any open");

            var relativeRequestPath = Path.Combine(fixture.OutputDirectory, "enroll-relative.json");
            await File.WriteAllTextAsync(relativeRequestPath, JsonSerializer.Serialize(new { locator = "relative-not-allowed.jsonl" })).ConfigureAwait(false);
            var relative = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", "s3t1-source-relative", "--zone-id", zoneId,
                "--format", SyntheticLineNormalizer.FormatName, "--format-version", "1",
                "--input-file", relativeRequestPath).ConfigureAwait(false);
            Require(relative.ExitCode == 4 && relative.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A relative locator was not refused by the enrollment grammar (exit=" + relative.ExitCode + "; stderr=" + relative.StandardError.Trim() + ").");

            var wildcardRequestPath = Path.Combine(fixture.OutputDirectory, "enroll-wildcard.json");
            await File.WriteAllTextAsync(wildcardRequestPath, JsonSerializer.Serialize(new { locator = locator + "*" })).ConfigureAwait(false);
            var wildcard = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", "s3t1-source-wildcard", "--zone-id", zoneId,
                "--format", SyntheticLineNormalizer.FormatName, "--format-version", "1",
                "--input-file", wildcardRequestPath).ConfigureAwait(false);
            Require(wildcard.ExitCode == 4 && wildcard.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A wildcard locator was not refused: enrollment must never degrade into a profile scan.");

            var locatorInUse = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", "s3t1-source-beta", "--zone-id", zoneId,
                "--format", SyntheticLineNormalizer.FormatName, "--format-version", "1",
                "--input-file", locatorRequestPath).ConfigureAwait(false);
            Require(locatorInUse.ExitCode == 4 && locatorInUse.StandardError.Contains("source_locator_in_use", StringComparison.Ordinal),
                "One locator enrolled under a second source was not refused (exit=" + locatorInUse.ExitCode + "; stderr=" + locatorInUse.StandardError.Trim() + ").");

            var conflictingZone = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId, "--zone-id", "s3t1-other-zone",
                "--format", SyntheticLineNormalizer.FormatName, "--format-version", "1",
                "--input-file", locatorRequestPath).ConfigureAwait(false);
            Require(conflictingZone.ExitCode == 4 && conflictingZone.StandardError.Contains("source_enrollment_conflict", StringComparison.Ordinal),
                "An inconsistent mapping was not refused (exit=" + conflictingZone.ExitCode + "; stderr=" + conflictingZone.StandardError.Trim() + ").");
            Console.WriteLine("PASS enrollment grammar and mapping-consistency negatives are explicit");

            var lockResult = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockResult.ExitCode == 0, "The owner could not lock the synthetic S3-T1 vault.");
            var lockedIngest = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(lockedIngest.ExitCode == 4 && lockedIngest.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "Ingestion was not refused while the vault was locked (exit=" + lockedIngest.ExitCode + "; stderr=" + lockedIngest.StandardError.Trim() + ").");
            Console.WriteLine("PASS a locked vault refuses every source operation (startup-locked and explicit lock)");

            var restartUnlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(restartUnlock.ExitCode == 0, "The owner could not unlock after locking.");

            var rebuildBefore = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(rebuildBefore.ExitCode == 0, "The S3-T1 index rebuild did not run (stderr=" + rebuildBefore.StandardError.Trim() + ").");
            var statusBefore = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(statusBefore.ExitCode == 0, "The S3-T1 index status did not run.");

            var unsupported = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(unsupported.ExitCode == 0 &&
                    unsupported.StandardOutput.Contains("status=ingest_unsupported_format", StringComparison.Ordinal) &&
                    unsupported.StandardOutput.Contains($"format={SyntheticLineNormalizer.FormatName}", StringComparison.Ordinal),
                "The shipped coordinator did not report the unsupported format explicitly (exit=" + unsupported.ExitCode + "; stdout=" + unsupported.StandardOutput.Trim() + "; stderr=" + unsupported.StandardError.Trim() + ").");
            var rebuildAfter = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            var statusAfter = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(S3T1IndexDigest(statusBefore.StandardOutput) == S3T1IndexDigest(statusAfter.StandardOutput) &&
                    S3T1IndexRows(statusBefore.StandardOutput) == S3T1IndexRows(statusAfter.StandardOutput),
                "An unsupported ingest persisted canonical journal state.");
            Console.WriteLine("PASS the shipped service reports unsupported format explicitly and persists no journal");

            var revoke = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-revoke", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(revoke.ExitCode == 0 && revoke.StandardOutput.Contains("life=revoked", StringComparison.Ordinal),
                "Owner revocation of the enrolled fixture failed (exit=" + revoke.ExitCode + "; stderr=" + revoke.StandardError.Trim() + ").");
            var revokedIngest = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(revokedIngest.ExitCode == 4 && revokedIngest.StandardError.Contains("source_revoked", StringComparison.Ordinal),
                "A revoked source was not refused (exit=" + revokedIngest.ExitCode + "; stderr=" + revokedIngest.StandardError.Trim() + ").");
            var reEnroll = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId, "--zone-id", zoneId,
                "--format", SyntheticLineNormalizer.FormatName, "--format-version", "1",
                "--input-file", locatorRequestPath).ConfigureAwait(false);
            Require(reEnroll.ExitCode == 0 && reEnroll.StandardOutput.Contains("generation=2", StringComparison.Ordinal),
                "Re-enrollment after revocation did not start a new generation (exit=" + reEnroll.ExitCode + "; stdout=" + reEnroll.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS revoke refuses ingestion; re-enrollment starts a new recorded generation");

            await StopServiceAsync(service).ConfigureAwait(false);
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false))
                    .Contains("vault=locked", StringComparison.Ordinal),
                "The S3-T1 service restarted unlocked.");
            var lockedAfterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(lockedAfterRestart.ExitCode == 4 && lockedAfterRestart.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "Ingestion was not refused after the service restart.");
            var unlockAfterRestart = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlockAfterRestart.ExitCode == 0, "The owner could not unlock after the S3-T1 service restart.");
            var statusAfterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(statusAfterRestart.ExitCode == 0 &&
                    statusAfterRestart.StandardOutput.Contains("generation=2", StringComparison.Ordinal) &&
                    statusAfterRestart.StandardOutput.Contains("life=enrolled", StringComparison.Ordinal),
                "Enrollment state did not survive the service restart (exit=" + statusAfterRestart.ExitCode + "; stdout=" + statusAfterRestart.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS enrollment state persists across a real service process restart; restart stays locked");

            var vaultFiles = Directory.GetFiles(fixture.VaultDirectoryPath, "*", SearchOption.AllDirectories);
            Require(vaultFiles.Length >= 3,
                "The encrypted vault did not persist its private files (found " + vaultFiles.Length + ").");
            foreach (var path in vaultFiles)
            {
                var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                Require(!text.Contains(marker, StringComparison.Ordinal) &&
                        !text.Contains(locator, StringComparison.Ordinal) &&
                        !Path.GetFileName(path).Contains(marker, StringComparison.Ordinal),
                    "A vault file persisted a plaintext payload marker or locator: " + Path.GetFileName(path) + ".");
            }

            var settingsText = await File.ReadAllTextAsync(fixture.SettingsPath).ConfigureAwait(false);
            Require(!settingsText.Contains(locator, StringComparison.Ordinal) &&
                    !settingsText.Contains(marker, StringComparison.Ordinal) &&
                    !settingsText.Contains(SyntheticLineNormalizer.FormatName, StringComparison.Ordinal),
                "The service settings persisted private enrollment metadata.");
            var afterBytes = await File.ReadAllBytesAsync(locator).ConfigureAwait(false);
            Require(afterBytes.AsSpan().SequenceEqual(sourceBytes),
                "The enrolled original fixture bytes were modified by the reader.");
            Console.WriteLine("PASS persisted enrollment state is encrypted; no plaintext sidecar, settings or fixture change");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Phase B: the real handler/coordinator/store/reader stack in-process with
    /// the nonshipping synthetic normalizer attached through the real
    /// domain-normalization registry. This is the valid incremental path the
    /// shipped binary cannot yet exercise (no Codex/OMP parser ships in T1).
    /// </summary>
    private static async Task RunS3T1CoordinatorPhaseAsync()
    {
        using var harness = S3T1CoordinatorHarness.Create();
        const string zoneId = "s3t1-coordinator-zone";
        const string sourceId = "s3t1-coordinator-source";
        var locator = Path.Combine(harness.Root, "synthetic-session.jsonl");
        var marker = "S3T1-COORDINATOR-PAYLOAD-MARKER-" + Guid.NewGuid().ToString("N")[..12];

        ServiceRequest EnrollRequest() => new(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.SourceEnrollOperation,
            sourceId,
            zoneId,
            SourceLocator: locator,
            SourceFormat: SyntheticLineNormalizer.FormatName,
            SourceFormatVersion: SyntheticLineNormalizer.FormatVersionValue);

        ServiceRequest Simple(string operation) => new(
            ServiceRequestHandler.CurrentProtocolVersion, operation, sourceId);

        // The fixture exists before enrollment: enrollment captures its
        // generation anchor from a bounded identity read.
        await File.WriteAllBytesAsync(locator, Encoding.UTF8.GetBytes(S3T1SyntheticSession(marker, 3, "gen1"))).ConfigureAwait(false);
        var enrolled = await harness.OwnerSendAsync(EnrollRequest()).ConfigureAwait(false);
        Require(enrolled.Status == "source_enrolled" && enrolled.SourceEnrollment is { Generation: 1 },
            "In-process enrollment did not start generation 1 (status=" + enrolled.Status + "; error=" + enrolled.Error + ").");

        // The reader detects a writer-held fixture explicitly; a later reader
        // re-check proves nothing was consumed.
        using (var exclusive = new FileStream(locator, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            _ = exclusive;
            var writerHeld = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
            Require(writerHeld.Error == "source_unreadable",
                "A writer-held enrolled source did not report the explicit unreadable diagnostic (status=" + writerHeld.Status + "; error=" + writerHeld.Error + ").");
        }

        var first = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(first.Status == "ingest_advanced" && first.SourceIngestion is { EventCount: 3, ConsumedOffset: > 0 },
            "The first bounded ingest did not accept the complete records (status=" + first.Status + "; error=" + first.Error + ").");
        var firstLength = new FileInfo(locator).Length;
        Require(first.SourceIngestion!.ConsumedOffset == firstLength,
            "The checkpoint did not advance to the end of the fixture.");

        // Idempotent re-import: no new bytes, no journal write.
        var revisionBefore = first.SourceIngestion!.JournalRevision;
        var reimport = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(reimport.Status == "ingest_idle" &&
                reimport.SourceIngestion!.JournalRevision == revisionBefore &&
                reimport.SourceIngestion!.EventCount == 3,
            "Re-import was not idempotent (status=" + reimport.Status + "; journal=" + reimport.SourceIngestion?.JournalRevision + "; events=" + reimport.SourceIngestion?.EventCount + ").");
        Console.WriteLine("PASS first ingest advances the checkpoint; re-import is a no-op (idempotent)");

        // Append: only the new complete records are consumed.
        await File.AppendAllTextAsync(locator, S3T1SyntheticSession(marker, 2, "gen1-append")).ConfigureAwait(false);
        var appended = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(appended.Status == "ingest_advanced" && appended.SourceIngestion is { EventCount: 5 },
            "The append ingest did not consume exactly the new records (status=" + appended.Status + "; events=" + appended.SourceIngestion?.EventCount + ").");
        Require(appended.SourceIngestion!.ConsumedOffset == new FileInfo(locator).Length,
            "The checkpoint did not follow the appended bytes.");

        // Partial tail: the incomplete record is never committed as complete.
        var partialText = "assistant|gen1-partial|2026-10-12T00:00:00Z|answer|synthetic partial without term";
        await File.AppendAllTextAsync(locator, partialText).ConfigureAwait(false);
        var partial = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(partial.Status == "ingest_partial_pending" &&
                partial.SourceIngestion!.ConsumedOffset < new FileInfo(locator).Length &&
                partial.SourceIngestion!.EventCount == 5,
            "The partial tail was committed as complete or lost (status=" + partial.Status + "; consumed=" + partial.SourceIngestion?.ConsumedOffset + "; events=" + partial.SourceIngestion?.EventCount + ").");
        await File.AppendAllTextAsync(locator, "inator\n").ConfigureAwait(false);
        var completed = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(completed.Status == "ingest_advanced" && completed.SourceIngestion is { EventCount: 6 } &&
                completed.SourceIngestion!.ConsumedOffset == new FileInfo(locator).Length,
            "The completed record after the partial tail was not consumed exactly once (status=" + completed.Status + "; events=" + completed.SourceIngestion?.EventCount + ").");
        Console.WriteLine("PASS append consumes only new records; a partial tail is never committed as complete and no event is lost");

        // Unsupported record: explicit stop, no silent skip, no drop.
        await File.AppendAllTextAsync(locator, "tester|gen1-bad|not-a-timestamp|note|synthetic broken record\n").ConfigureAwait(false);
        var unsupportedRecord = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(unsupportedRecord.Status == "ingest_unsupported_record" &&
                unsupportedRecord.SourceIngestion is { EventCount: 6, DiagnosticCount: 1 },
            "An unsupported record did not stop the run with an explicit diagnostic (status=" + unsupportedRecord.Status + "; events=" + unsupportedRecord.SourceIngestion?.EventCount + "; diagnostics=" + unsupportedRecord.SourceIngestion?.DiagnosticCount + ").");
        var unsupportedAgain = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(unsupportedAgain.Status == "ingest_unsupported_record" && unsupportedAgain.SourceIngestion!.DiagnosticCount == 1,
            "The unsupported record was not left explicitly blocked on re-import.");
        var stuckOffset = unsupportedAgain.SourceIngestion!.ConsumedOffset;
        Require(stuckOffset == unsupportedRecord.SourceIngestion!.ConsumedOffset,
            "The checkpoint moved while an unsupported record blocked ingestion.");
        Console.WriteLine("PASS an unsupported record stops ingestion explicitly at its offset without silent skip or drop");

        // Grant over the journal resource: issued grants must stay frozen while
        // ingestion keeps writing the journal revisions.
        var journalId = SourceIngestionIds.JournalIdFor(sourceId, 1);
        var preview = await harness.OwnerSendAsync(new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.OwnerScopePreviewOperation,
            ResourceIds: new[] { journalId },
            Operations: new[] { "resource.read" },
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1))).ConfigureAwait(false);
        Require(preview.Status == "scope_preview" && preview.ScopePreview is not null,
            "The owner grant preview over the journal did not complete (status=" + preview.Status + "; error=" + preview.Error + ").");
        var issued = await harness.OwnerSendAsync(new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.OwnerScopeIssueOperation,
            PreviewId: preview.ScopePreview!.PreviewId)).ConfigureAwait(false);
        Require(issued.Status == "token_issued" && issued.GrantId is not null,
            "The owner grant over the journal resource was not issued (status=" + issued.Status + "; error=" + issued.Error + ").");
        var grantsBefore = await harness.OwnerSendAsync(new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.OwnerScopeListOperation)).ConfigureAwait(false);
        var grantsBeforeJson = JsonSerializer.Serialize(grantsBefore.ScopeGrants);

        // Replacement (different bytes, longer than the consumed prefix) must
        // never splice into the prior generation.
        var replacement = Encoding.UTF8.GetBytes(S3T1SyntheticSession(marker + "-replaced", 10, "gen1-replaced"));
        await File.WriteAllBytesAsync(locator, replacement).ConfigureAwait(false);
        var replaced = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(replaced.Error == "source_generation_changed",
            "A replaced fixture was not refused as a generation change (status=" + replaced.Status + "; error=" + replaced.Error + ").");
        var quarantinedStatus = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceStatusOperation)).ConfigureAwait(false);
        Require(quarantinedStatus.SourceEnrollment is { Life: "quarantined" } &&
                quarantinedStatus.SourceEnrollment!.Quarantine is { Reason: "generation_replaced" } &&
                quarantinedStatus.SourceIngestion!.ConsumedOffset == stuckOffset &&
                quarantinedStatus.SourceIngestion!.EventCount == 6,
            "Replacement did not quarantine the source and freeze the prior generation checkpoint.");

        // Quarantine is checked before the open: a writer-held, quarantined
        // fixture answers with the quarantine refusal, not an io failure.
        using (var exclusive = new FileStream(locator, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            _ = exclusive;
            var quarantinedBusy = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
            Require(quarantinedBusy.Error == "source_mixed_quarantined",
                "A quarantined source was not refused before the open (error=" + quarantinedBusy.Error + ").");
        }
        Console.WriteLine("PASS replacement is quarantined and refused; the prior generation is never spliced");

        // Explicit re-enrollment resolves the quarantine and starts generation 2
        // on a fresh journal; generation 1's journal is preserved.
        var reEnrolled = await harness.OwnerSendAsync(EnrollRequest()).ConfigureAwait(false);
        Require(reEnrolled.Status == "source_enrolled" && reEnrolled.SourceEnrollment is { Generation: 2 },
            "Explicit re-enrollment did not start generation 2 (status=" + reEnrolled.Status + "; error=" + reEnrolled.Error + ").");
        var generationTwo = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(generationTwo.Status == "ingest_advanced" && generationTwo.SourceIngestion is { EventCount: 10 },
            "Generation 2 did not ingest from a fresh checkpoint (status=" + generationTwo.Status + "; events=" + generationTwo.SourceIngestion?.EventCount + ").");

        var journals = await harness.OwnerSendAsync(new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.OwnerIndexRebuildOperation)).ConfigureAwait(false);
        Require(journals.Status == "index_rebuilt" && journals.PrivateIndex is { RowCount: 2 },
            "The two generation journals are not canonical resources (rows=" + journals.PrivateIndex?.RowCount + "; error=" + journals.Error + ").");
        var firstJournal = harness.DecodeJournal(sourceId, 1);
        Require(firstJournal is { Generation: 1, ConsumedOffset: var g1Offset, Events.Length: 6 } && g1Offset == stuckOffset,
            "The generation-1 journal did not keep its frozen checkpoint and events.");
        Console.WriteLine("PASS explicit re-enrollment adopts a new generation without splicing or losing the prior one");

        // Truncation below the consumed prefix is also refused as a generation
        // change and quarantined, never spliced.
        await File.WriteAllBytesAsync(locator, Encoding.UTF8.GetBytes(S3T1SyntheticSession(marker, 2, "gen2-short"))).ConfigureAwait(false);
        var truncated = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(truncated.Error == "source_generation_changed",
            "A truncated fixture was not refused as a generation change (error=" + truncated.Error + ").");
        var truncatedStatus = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceStatusOperation)).ConfigureAwait(false);
        Require(truncatedStatus.SourceEnrollment is { Life: "quarantined" } &&
                truncatedStatus.SourceEnrollment!.Quarantine is { Reason: "generation_truncated" } &&
                truncatedStatus.SourceIngestion!.ConsumedOffset == generationTwo.SourceIngestion!.ConsumedOffset &&
                truncatedStatus.SourceIngestion!.EventCount == 10,
            "Truncation did not quarantine the source while freezing the prior generation checkpoint.");
        var reEnrolledThree = await harness.OwnerSendAsync(EnrollRequest()).ConfigureAwait(false);
        Require(reEnrolledThree.Status == "source_enrolled" && reEnrolledThree.SourceEnrollment is { Generation: 3 },
            "The second explicit re-enrollment did not start generation 3 (status=" + reEnrolledThree.Status + "; error=" + reEnrolledThree.Error + ").");
        var generationThree = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(generationThree.Status == "ingest_advanced" && generationThree.SourceIngestion is { EventCount: 2 },
            "Generation 3 did not ingest from a fresh checkpoint (status=" + generationThree.Status + "; events=" + generationThree.SourceIngestion?.EventCount + ").");
        Console.WriteLine("PASS truncation is quarantined and refused; a new explicit generation starts cleanly");

        // Lock semantics: an in-flight ingestion either completes or is
        // refused; either way the lock boundary and the persisted checkpoint
        // stay consistent, and continuation after unlock is idempotent.
        await File.AppendAllTextAsync(locator, S3T1SyntheticSession(marker, 2, "gen2-append")).ConfigureAwait(false);
        var ingestTask = harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation));
        var lockTask = Task.Run(() => harness.Lifecycle.LockAsync());
        await Task.WhenAll(ingestTask, lockTask).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        Require(ingestTask.Result.Status == "ingest_advanced" || ingestTask.Result.Error == "vault_locked",
            "An in-flight ingest neither completed nor was refused by the lock (status=" + ingestTask.Result.Status + "; error=" + ingestTask.Result.Error + ").");
        if (harness.Lifecycle.State != "unlocked")
        {
            Require(await harness.UnlockAsync().ConfigureAwait(false),
                "The owner could not unlock after the concurrent lock boundary.");
        }

        var afterLock = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(afterLock.Status == "ingest_advanced" || afterLock.Status == "ingest_idle",
            "Ingestion after the lock boundary failed (status=" + afterLock.Status + "; error=" + afterLock.Error + ").");
        var drained = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(drained.Status == "ingest_idle" && drained.SourceIngestion!.ConsumedOffset == new FileInfo(locator).Length,
            "Lock drain/continuation duplicated or lost events (status=" + drained.Status + "; consumed=" + drained.SourceIngestion?.ConsumedOffset + ").");
        Console.WriteLine("PASS lock blocks/drains ingestion; continuation after unlock is idempotent from the persisted checkpoint");

        // Cancellation commits nothing and loses nothing.
        await File.AppendAllTextAsync(locator, S3T1SyntheticSession(marker, 2, "gen2-cancel")).ConfigureAwait(false);
        var beforeCancel = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceStatusOperation)).ConfigureAwait(false);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var cancelledRun = false;
            try
            {
                await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation), cancelled.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelledRun = true;
            }

            Require(cancelledRun, "A cancelled ingest did not stop before committing.");
        }

        var afterCancel = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceStatusOperation)).ConfigureAwait(false);
        Require(afterCancel.SourceIngestion!.ConsumedOffset == beforeCancel.SourceIngestion!.ConsumedOffset &&
                afterCancel.SourceIngestion!.JournalRevision == beforeCancel.SourceIngestion!.JournalRevision,
            "A cancelled ingest advanced the durable checkpoint.");
        var retried = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(retried.Status == "ingest_advanced" && retried.SourceIngestion!.ConsumedOffset == new FileInfo(locator).Length,
            "The retry after cancellation did not complete the interrupted run (status=" + retried.Status + ").");
        Console.WriteLine("PASS a cancelled ingest commits nothing; the retry completes the same run exactly once");

        // Restart over the same vault: resume from the persisted checkpoint.
        var beforeRestart = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceStatusOperation)).ConfigureAwait(false);
        harness.Restart();
        var afterRestart = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(afterRestart.Status == "ingest_idle" &&
                afterRestart.SourceIngestion!.ConsumedOffset == new FileInfo(locator).Length &&
                afterRestart.SourceEnrollment is { Generation: var restartedGeneration } &&
                restartedGeneration == beforeRestart.SourceEnrollment!.Generation &&
                afterRestart.SourceIngestion!.EventCount == beforeRestart.SourceIngestion!.EventCount,
            "The restart did not resume from the persisted checkpoint (status=" + afterRestart.Status + "; consumed=" + afterRestart.SourceIngestion?.ConsumedOffset + "; events=" + afterRestart.SourceIngestion?.EventCount + ").");
        await File.AppendAllTextAsync(locator, S3T1SyntheticSession(marker, 1, "gen2-restart")).ConfigureAwait(false);
        var afterRestartAppend = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(afterRestartAppend.Status == "ingest_advanced" &&
                afterRestartAppend.SourceIngestion!.EventCount == afterRestart.SourceIngestion!.EventCount + 1,
            "Post-restart ingestion did not continue from the correct offset (status=" + afterRestartAppend.Status + "; events=" + afterRestartAppend.SourceIngestion?.EventCount + ").");
        Console.WriteLine("PASS restart resumes from the persisted checkpoint without duplicates");

        // Encrypted persistence and original-byte preservation.
        var vaultFiles = Directory.GetFiles(harness.VaultDirectoryPath, "*", SearchOption.AllDirectories);
        Require(vaultFiles.Length >= 3, "The in-process vault did not persist its private files.");
        foreach (var path in vaultFiles)
        {
            var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            Require(!text.Contains(marker, StringComparison.Ordinal) && !text.Contains(locator, StringComparison.Ordinal),
                "An encrypted vault file persisted plaintext payload or locator: " + Path.GetFileName(path) + ".");
        }

        var expectedFixture = S3T1FixtureBytes(marker);
        var actualFixture = await File.ReadAllBytesAsync(locator).ConfigureAwait(false);
        Require(actualFixture.AsSpan().SequenceEqual(expectedFixture),
            "The reader modified the original fixture bytes.");
        var lastWrite = File.GetLastWriteTimeUtc(locator);
        _ = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(File.GetLastWriteTimeUtc(locator) == lastWrite,
            "A read-only ingest modified the fixture's last-write metadata.");

        // Grants stayed frozen through every enrollment, ingest, quarantine and
        // restart transition.
        var grantsAfter = await harness.OwnerSendAsync(new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.OwnerScopeListOperation)).ConfigureAwait(false);
        Require(JsonSerializer.Serialize(grantsAfter.ScopeGrants) == grantsBeforeJson &&
                grantsAfter.PolicyGeneration == grantsBefore.PolicyGeneration,
            "Source ingestion mutated a frozen owner grant or its policy generation.");
        Console.WriteLine("PASS encrypted journals/state, original bytes and metadata unchanged, grants frozen");
    }

    private static string S3T1SyntheticSession(string marker, int records, string prefix) =>
        string.Concat(Enumerable.Range(1, records).Select(index =>
            $"user|{prefix}-e{index}|2026-10-12T00:00:00Z|question|{marker} payload {prefix} {index}\n"));

    private static byte[] S3T1FixtureBytes(string marker) =>
        Encoding.UTF8.GetBytes(string.Concat(
            S3T1SyntheticSession(marker, 2, "gen2-short"),
            S3T1SyntheticSession(marker, 2, "gen2-append"),
            S3T1SyntheticSession(marker, 2, "gen2-cancel"),
            S3T1SyntheticSession(marker, 1, "gen2-restart")));

    private static string S3T1IndexDigest(string output) =>
        output.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith("digest=", StringComparison.Ordinal)) ?? string.Empty;

    private static string S3T1IndexRows(string output) =>
        output.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith("rows=", StringComparison.Ordinal)) ?? string.Empty;
}

/// <summary>
/// Nonshipping synthetic boundary consumer used only by the S3-T1 smoke to
/// exercise the valid incremental path through the real domain-normalization
/// contract. It parses a deliberately artificial line format
/// (<c>role|eventId|occurredUtc|kind|payload</c>) and is not, and must never
/// become, a Codex/OMP schema parser: real format parsers attach in S3-T2/S3-T3
/// against the same <see cref="ISourceRecordNormalizer"/> contract.
/// </summary>
internal sealed class SyntheticLineNormalizer : ISourceRecordNormalizer
{
    public const string FormatName = "synthetic-line";
    public const int FormatVersionValue = 1;

    public string Format => FormatName;

    public int FormatVersion => FormatVersionValue;

    public SourceRecordDecode Decode(ReadOnlySpan<byte> buffer, SourceRecordContext context)
    {
        var newline = buffer.IndexOf((byte)'\n');
        if (newline < 0)
        {
            // An incomplete trailing record: the caller leaves it unconsumed.
            return new SourceRecordDecode(SourceRecordDecodeKind.NeedMoreBytes);
        }

        var line = buffer[..newline];
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        var ranges = new Range[4];
        var count = 0;
        var fieldStart = 0;
        for (var index = 0; index < line.Length && count < 4; index++)
        {
            if (line[index] != (byte)'|')
            {
                continue;
            }

            ranges[count++] = new Range(fieldStart, index);
            fieldStart = index + 1;
        }

        if (count < 4)
        {
            return Unsupported("synthetic_record_fields");
        }

        var role = Encoding.ASCII.GetString(line[ranges[0]]).Trim();
        var eventId = Encoding.ASCII.GetString(line[ranges[1]]).Trim();
        var occurrence = Encoding.ASCII.GetString(line[ranges[2]]).Trim();
        var payloadKind = Encoding.ASCII.GetString(line[ranges[3]]).Trim();
        var payload = line[fieldStart..];
        if (payload.IsEmpty)
        {
            return Unsupported("synthetic_record_payload");
        }

        SourceEventAuthor author = role switch
        {
            "user" => SourceEventAuthor.User,
            "assistant" => SourceEventAuthor.Assistant,
            "tool" => SourceEventAuthor.Tool,
            "system" => SourceEventAuthor.System,
            _ => SourceEventAuthor.Unknown
        };
        if (author == SourceEventAuthor.Unknown)
        {
            return Unsupported("synthetic_record_role");
        }

        if (!IsSyntheticIdentifier(eventId) || !IsSyntheticCode(payloadKind))
        {
            return Unsupported("synthetic_record_identity");
        }

        DateTimeOffset? occurredAtUtc = null;
        if (occurrence.Length > 0)
        {
            if (!DateTimeOffset.TryParse(occurrence, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed) || parsed.Offset != TimeSpan.Zero)
            {
                return Unsupported("synthetic_record_time");
            }

            occurredAtUtc = parsed;
        }

        var draft = new SourceEventDraft(eventId, author, occurredAtUtc, payloadKind, SourceFingerprint.Compute(line));
        return new SourceRecordDecode(SourceRecordDecodeKind.Consumed, newline + 1, draft);
    }

    private static SourceRecordDecode Unsupported(string code) =>
        new(SourceRecordDecodeKind.Unsupported, 0, null, code);

    private static bool IsSyntheticIdentifier(string value)
    {
        if (value.Length is 0 or > 128)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSyntheticCode(string value)
    {
        if (value.Length is 0 or > 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// In-process harness for the S3-T1 coordinator phase: the same production
/// object graph the named-pipe service builds (encrypted store, vault
/// lifecycle, handler, authorities, enrolled-source reader), plus the
/// synthetic normalizer attached through the real registry. "Restart" rebuilds
/// the lifecycle and handler over the same vault directory, which is exactly
/// what a service process restart does.
/// </summary>
internal sealed class S3T1CoordinatorHarness : IDisposable
{
    private const string SyntheticPassphrase = "s3t1-synthetic-vault-passphrase";
    private readonly string _root;
    private readonly EncryptedVaultStore _store;
    private readonly SourceNormalizerRegistry _normalizers;
    private readonly SqlitePrivateIndexStore _index;
    private bool _disposed;

    private S3T1CoordinatorHarness(
        string root,
        EncryptedVaultStore store,
        SourceNormalizerRegistry normalizers,
        SqlitePrivateIndexStore index,
        VaultLifecycle lifecycle,
        ServiceRequestHandler handler)
    {
        _root = root;
        _store = store;
        _normalizers = normalizers;
        _index = index;
        Lifecycle = lifecycle;
        Handler = handler;
    }

    public string Root => _root;

    public string VaultDirectoryPath => Path.Combine(_root, "service", "vault");

    public VaultLifecycle Lifecycle { get; private set; }

    public ServiceRequestHandler Handler { get; private set; }

    public static S3T1CoordinatorHarness Create()
    {
        var ownerSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows token has no user SID.");
        var root = Path.Combine(Path.GetTempPath(), "MetaBrain-S3-T1-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root) || File.Exists(root))
        {
            throw new IOException("The generated synthetic harness path already exists.");
        }

        Directory.CreateDirectory(root);
        try
        {
            SmokeFixture.SetRestrictedDirectoryAcl(root, ownerSid);
            var serviceDirectory = Path.Combine(root, "service");
            Directory.CreateDirectory(serviceDirectory);
            SmokeFixture.SetRestrictedDirectoryAcl(serviceDirectory, ownerSid);

            var normalizers = new SourceNormalizerRegistry();
            normalizers.Register(new SyntheticLineNormalizer());
            var store = new EncryptedVaultStore(Path.Combine(serviceDirectory, "vault"), ownerSid.Value);
            var lifecycle = new VaultLifecycle(store);
            if (!lifecycle.Provision(SyntheticPassphrase, out var recoveryCode) || recoveryCode is null)
            {
                throw new InvalidOperationException("The synthetic harness vault was not created.");
            }

            RequireHarness(lifecycle.Unlock(SyntheticPassphrase, useRecoveryCode: false),
                "The synthetic harness vault did not unlock.");
            var index = new SqlitePrivateIndexStore();
            return new S3T1CoordinatorHarness(
                root, store, normalizers, index, lifecycle, BuildHandler(lifecycle, normalizers, index));
        }
        catch
        {
            DeleteS3T1Root(root);
            throw;
        }
    }

    /// <summary>
    /// Rebuilds the lifecycle and handler over the same vault directory: the
    /// in-process equivalent of a service process restart (fresh unlocked
    /// state, same durable ciphertext).
    /// </summary>
    public void Restart()
    {
        Lifecycle.Dispose();
        var lifecycle = new VaultLifecycle(_store);
        RequireHarness(lifecycle.Unlock(SyntheticPassphrase, useRecoveryCode: false),
            "The restarted harness vault did not unlock.");
        Lifecycle = lifecycle;
        Handler = BuildHandler(lifecycle, _normalizers, _index);
    }

    public async Task<bool> UnlockAsync() => await Task.FromResult(Lifecycle.Unlock(SyntheticPassphrase, useRecoveryCode: false)).ConfigureAwait(false);

    public SourceIngestionJournal? DecodeJournal(string sourceId, long generation)
    {
        var journalId = SourceIngestionIds.JournalIdFor(sourceId, generation);
        using var operation = Lifecycle.TryBeginOperation()
            ?? throw new InvalidOperationException("The harness vault locked before the journal decode.");
        var revision = operation.ListResources().FirstOrDefault(resource =>
            string.Equals(resource.ResourceId, journalId, StringComparison.Ordinal)) is { } live ? live.Revision : 0;
        if (revision <= 0)
        {
            return null;
        }

        var raw = operation.ReadContent(journalId, revision);
        try
        {
            return SourceIngestionJournalCodec.Decode(raw);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    public async Task<ServiceReply> OwnerSendAsync(ServiceRequest request, CancellationToken cancellationToken = default)
    {
        var needsOperation =
            request.Operation is ServiceRequestHandler.SourceEnrollOperation or
                ServiceRequestHandler.SourceRevokeOperation or
                ServiceRequestHandler.SourceStatusOperation or
                ServiceRequestHandler.SourceIngestOperation or
                ServiceRequestHandler.SourceRelocateOperation or
                ServiceRequestHandler.SourcePauseOperation or
                ServiceRequestHandler.SourceResumeOperation or
                ServiceRequestHandler.SourceRefreshOptInOperation or
                ServiceRequestHandler.SourceRefreshOptOutOperation or
                ServiceRequestHandler.SourceListOperation or
                ServiceRequestHandler.SourceRefreshOperation or
                ServiceRequestHandler.OwnerScopePreviewOperation or
                ServiceRequestHandler.OwnerScopeIssueOperation or
                ServiceRequestHandler.OwnerScopeListOperation or
                ServiceRequestHandler.OwnerIndexRebuildOperation;
        var operation = needsOperation ? Lifecycle.TryBeginOperation() : null;
        try
        {
            return await Handler.HandleAsync(
                AuthenticatedContext.ForOwner(), request, operation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operation?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Lifecycle.Dispose();
        _index.Dispose();
        DeleteS3T1Root(_root);
    }

    private static ServiceRequestHandler BuildHandler(
        VaultLifecycle lifecycle,
        SourceNormalizerRegistry normalizers,
        IPrivateIndexStore privateIndex) =>
        new(new GrantAuthority(new InMemoryGrantStore()),
            lifecycle,
            new OwnerScopeGrantAuthority(),
            projections: null,
            privateIndex: privateIndex,
            sourceNormalizers: normalizers,
            sourceReader: new EnrolledSourceFileReader());

    private static void RequireHarness(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void DeleteS3T1Root(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var temporaryDirectory = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var expectedPrefix = temporaryDirectory + Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullRoot).StartsWith("MetaBrain-S3-T1-", StringComparison.Ordinal) ||
            !Directory.Exists(fullRoot))
        {
            return;
        }

        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The synthetic harness root became a reparse point; refusing recursive cleanup.");
        }

        Directory.Delete(fullRoot, recursive: true);
    }
}
