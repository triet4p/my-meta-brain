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
    /// S3-T3 OMP session JSONL read-only adapter, proven in two phases:
    /// <list type="bullet">
    /// <item><description>Phase A drives the real service process and the real owner CLI over named pipes with deterministic OMP-format fixtures: enrollment by (format, version), first ingest, idempotent re-import, append, partial tail, and explicit unsupported-record diagnostics through the shipped binary (which now registers the real OMP parser beside the Codex one).</description></item>
    /// <item><description>Phase B drives the same production object graph in-process with the shipped registry (both real parsers), and asserts the stored encrypted journal: per-event attribution (user/assistant/tool/system by declared field), the session identity event, the declared id/parentId chain, the declared cwd context, timestamps, exact source spans and digests, plus the boundary matrix (unknown record type, blank line, invalid JSON, bad chain references, unknown role/part/stop reason/attribution, session version, incomplete tail).</description></item>
    /// </list>
    /// Every fixture is locally generated synthetic data in the discovered OMP
    /// schema shape; no private source is read by this smoke.
    /// </summary>
    internal static async Task RunS3T3OmpAdapterAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S3-T3 OMP adapter smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S3-T3 OMP session JSONL read-only adapter");
        await RunS3T3OwnerServicePhaseAsync(connectionsExecutable).ConfigureAwait(false);
        await RunS3T3OmpCoordinatorPhaseAsync().ConfigureAwait(false);
    }

    private static async Task RunS3T3OwnerServicePhaseAsync(string connectionsExecutable)
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
                "The S3-T3 service fixture did not start uninitialized.");
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "Provision did not create the S3-T3 synthetic vault (exit=" + provisioned.ExitCode + ").");

            const string zoneId = "s3t3-omp-zone";
            const string sourceId = "s3t3-omp-session";
            var locator = Path.Combine(fixture.OutputDirectory, "omp-session.jsonl");
            var marker = "S3T3-OMP-PAYLOAD-MARKER-" + Guid.NewGuid().ToString("N")[..12];
            var sessionBytes = Encoding.UTF8.GetBytes(S3T3OmpBase("s3t3-omp-session-alpha", marker));
            await File.WriteAllBytesAsync(locator, sessionBytes).ConfigureAwait(false);
            var locatorRequestPath = Path.Combine(fixture.OutputDirectory, "omp-enroll.json");
            await File.WriteAllTextAsync(locatorRequestPath, JsonSerializer.Serialize(new { locator })).ConfigureAwait(false);

            // Enrollment names the discovered OMP format and version; the reply
            // never echoes the locator.
            var enroll = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId, "--zone-id", zoneId,
                "--format", OmpSessionNormalizer.FormatName, "--format-version", "1",
                "--input-file", locatorRequestPath).ConfigureAwait(false);
            Require(enroll.ExitCode == 0 &&
                    enroll.StandardOutput.Contains($"SOURCE id={sourceId}", StringComparison.Ordinal) &&
                    enroll.StandardOutput.Contains($"format={OmpSessionNormalizer.FormatName}", StringComparison.Ordinal) &&
                    enroll.StandardOutput.Contains("version=1", StringComparison.Ordinal) &&
                    enroll.StandardOutput.Contains("generation=1", StringComparison.Ordinal),
                "Owner enrollment of the OMP-format fixture failed (exit=" + enroll.ExitCode + "; stdout=" + enroll.StandardOutput.Trim() + "; stderr=" + enroll.StandardError.Trim() + ").");
            Require(!enroll.StandardOutput.Contains(locator, StringComparison.Ordinal),
                "The owner enrollment reply echoed the private locator.");
            Console.WriteLine("PASS owner enrolls one OMP-format session through the real owner CLI");

            // The agent channel still cannot reach ingestion.
            var agentIngest = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "source.ingest",
                resourceId = sourceId
            }).ConfigureAwait(false);
            Require(ReplyCode(agentIngest) == "forbidden",
                "The agent channel reached a source ingestion operation for an OMP source.");
            Console.WriteLine("PASS the agent channel is refused for OMP ingestion by the transport allowlist");

            var first = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            var expectedRecords = S3T3OmpRecordCount;
            Require(first.ExitCode == 0 &&
                    first.StandardOutput.Contains("status=ingest_advanced", StringComparison.Ordinal) &&
                    S3T2IngestEvents(first.StandardOutput) == expectedRecords &&
                    S3T2IngestConsumed(first.StandardOutput) == sessionBytes.Length,
                "The first OMP ingest did not accept every record exactly once (exit=" + first.ExitCode + "; stdout=" + first.StandardOutput.Trim() + "; stderr=" + first.StandardError.Trim() + ").");
            Console.WriteLine($"PASS the first ingest accepts all {expectedRecords} OMP records and advances the checkpoint to the file end");

            var revisionAfterFirst = S3T2IngestRevision(first.StandardOutput);
            var reimport = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(reimport.ExitCode == 0 && reimport.StandardOutput.Contains("status=ingest_idle", StringComparison.Ordinal) &&
                    S3T2IngestRevision(reimport.StandardOutput) == revisionAfterFirst &&
                    S3T2IngestEvents(reimport.StandardOutput) == expectedRecords,
                "Re-import of an OMP session was not idempotent (status=" + reimport.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS re-import is a no-op: same journal revision, same events, no duplicates");

            await File.AppendAllTextAsync(locator, S3T3OmpAppendRecords(marker)).ConfigureAwait(false);
            var appended = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(appended.ExitCode == 0 && appended.StandardOutput.Contains("status=ingest_advanced", StringComparison.Ordinal) &&
                    S3T2IngestEvents(appended.StandardOutput) == expectedRecords + S3T3OmpAppendRecordCount &&
                    S3T2IngestConsumed(appended.StandardOutput) == new FileInfo(locator).Length,
                "The appended OMP records were not consumed exactly once (stdout=" + appended.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS append consumes only the new OMP records");

            // Partial tail: an incomplete trailing record is never committed.
            var partialRecord = S3T3OmpPartialRecord(marker);
            await File.AppendAllTextAsync(locator, partialRecord[..^1]).ConfigureAwait(false);
            var partial = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(partial.ExitCode == 0 && partial.StandardOutput.Contains("status=ingest_partial_pending", StringComparison.Ordinal) &&
                    S3T2IngestConsumed(partial.StandardOutput) < new FileInfo(locator).Length &&
                    S3T2IngestEvents(partial.StandardOutput) == expectedRecords + S3T3OmpAppendRecordCount,
                "The incomplete trailing record was committed or lost (stdout=" + partial.StandardOutput.Trim() + ").");
            await File.AppendAllTextAsync(locator, "\n").ConfigureAwait(false);
            var completed = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(completed.ExitCode == 0 && completed.StandardOutput.Contains("status=ingest_advanced", StringComparison.Ordinal) &&
                    S3T2IngestEvents(completed.StandardOutput) == expectedRecords + S3T3OmpAppendRecordCount + 1 &&
                    S3T2IngestConsumed(completed.StandardOutput) == new FileInfo(locator).Length,
                "The completed record after the partial tail was not consumed exactly once (stdout=" + completed.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS a partial tail stays unconsumed and is consumed exactly once when completed");

            // An unobserved record shape stops ingestion explicitly at its
            // offset: no silent skip, no fabricated valid event.
            var unsupportedRecord = "{\"type\":\"brand_new_omp_record\",\"id\":\"e99\",\"parentId\":\"e16\",\"timestamp\":\"2026-10-12T00:00:17.000Z\",\"anything\":true}\n";
            await File.AppendAllTextAsync(locator, unsupportedRecord).ConfigureAwait(false);
            var unsupported = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            var expectedTotal = expectedRecords + S3T3OmpAppendRecordCount + 1;
            Require(unsupported.ExitCode == 0 &&
                    unsupported.StandardOutput.Contains("status=ingest_unsupported_record", StringComparison.Ordinal) &&
                    S3T2IngestEvents(unsupported.StandardOutput) == expectedTotal &&
                    S3T2IngestDiagnostics(unsupported.StandardOutput) == 1,
                "An unobserved OMP record shape did not stop the run with an explicit diagnostic (stdout=" + unsupported.StandardOutput.Trim() + ").");
            var stuckOffset = S3T2IngestConsumed(unsupported.StandardOutput);
            var unsupportedAgain = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(unsupportedAgain.StandardOutput.Contains("status=ingest_unsupported_record", StringComparison.Ordinal) &&
                    S3T2IngestConsumed(unsupportedAgain.StandardOutput) == stuckOffset,
                "The unsupported OMP record was not left explicitly blocked on re-import.");
            Console.WriteLine("PASS an unobserved record shape stops ingestion explicitly without silent skip or drop");

            // Boundary fixtures on separate enrollments: every refusal is
            // explicit and names what was seen.
            await RunS3T3BoundaryFixtureAsync(connectionsExecutable, fixture, zoneId, "s3t3-omp-unknown-type",
                S3T3OmpBase("s3t3-omp-unknown", marker) +
                "{\"type\":\"brand_new_omp_record\",\"id\":\"e99\",\"parentId\":\"e12\",\"timestamp\":\"2026-10-12T00:00:20.000Z\",\"anything\":true}\n",
                "omp_type_unsupported.brand_new_omp_record");
            await RunS3T3BoundaryFixtureAsync(connectionsExecutable, fixture, zoneId, "s3t3-omp-blank-line",
                S3T3OmpBase("s3t3-omp-blank", marker) + "\n",
                "omp_record_blank");
            await RunS3T3BoundaryFixtureAsync(connectionsExecutable, fixture, zoneId, "s3t3-omp-invalid-json",
                S3T3OmpBase("s3t3-omp-json", marker) + "{not json}\n",
                "omp_record_json_invalid");
            await RunS3T3BoundaryFixtureAsync(connectionsExecutable, fixture, zoneId, "s3t3-omp-missing-id",
                S3T3OmpBase("s3t3-omp-noid", marker) +
                "{\"type\":\"title_change\",\"parentId\":\"e12\",\"timestamp\":\"2026-10-12T00:00:21.000Z\",\"title\":\"x\",\"source\":\"auto\"}\n",
                "omp_event_id_missing");
            await RunS3T3BoundaryFixtureAsync(connectionsExecutable, fixture, zoneId, "s3t3-omp-missing-parent",
                S3T3OmpBase("s3t3-omp-noparent", marker) +
                "{\"type\":\"title_change\",\"id\":\"e90\",\"timestamp\":\"2026-10-12T00:00:22.000Z\",\"title\":\"x\",\"source\":\"auto\"}\n",
                "omp_event_parent_missing");
            await RunS3T3BoundaryFixtureAsync(connectionsExecutable, fixture, zoneId, "s3t3-omp-unknown-role",
                S3T3OmpBase("s3t3-omp-role", marker) +
                S3T3OmpEventLine("e91", "e12", "2026-10-12T00:00:23.000Z",
                    "\"message\",\"message\":{\"role\":\"system\",\"content\":[{\"type\":\"text\",\"text\":\"" + marker + " mystery role\"}],\"timestamp\":\"2026-10-12T00:00:23.000Z\"}"),
                "omp_message_role_unsupported.system");
            await RunS3T3BoundaryFixtureAsync(connectionsExecutable, fixture, zoneId, "s3t3-omp-session-version",
                "{\"type\":\"session\",\"version\":4,\"id\":\"s3t3-omp-v4\",\"timestamp\":\"2026-10-12T00:00:00.000Z\",\"cwd\":\"C:\\\\synthetic\\\\omp-fixture\",\"title\":\"t\",\"titleSource\":\"auto\"}\n",
                "omp_session_version_unsupported.4");
            Console.WriteLine("PASS unknown type, blank line, invalid JSON, missing chain identity, unknown role and session version are explicit diagnostics");

            // Locked vault refuses OMP ingestion like any other source.
            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The owner could not lock the S3-T3 vault.");
            var lockedIngest = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(lockedIngest.ExitCode == 4 && lockedIngest.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "OMP ingestion was not refused while the vault was locked.");
            var unlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0, "The owner could not unlock the S3-T3 vault.");
            var status = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(status.ExitCode == 0 &&
                    status.StandardOutput.Contains($"format={OmpSessionNormalizer.FormatName}", StringComparison.Ordinal) &&
                    status.StandardOutput.Contains("version=1", StringComparison.Ordinal) &&
                    S3T2IngestEvents(status.StandardOutput) == expectedTotal,
                "The OMP source status did not report the supported format and the exact event count (stdout=" + status.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS a locked vault refuses OMP ingestion; unlock resumes from the persisted checkpoint");

            // Encrypted persistence and original-byte preservation.
            var vaultFiles = Directory.GetFiles(fixture.VaultDirectoryPath, "*", SearchOption.AllDirectories);
            Require(vaultFiles.Length >= 3, "The encrypted vault did not persist its private files.");
            foreach (var path in vaultFiles)
            {
                var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                Require(!text.Contains(marker, StringComparison.Ordinal) &&
                        !text.Contains(locator, StringComparison.Ordinal) &&
                        !text.Contains(OmpSessionNormalizer.FormatName, StringComparison.Ordinal),
                    "An encrypted vault file persisted plaintext payload, locator or format: " + Path.GetFileName(path) + ".");
            }

            var settingsText = await File.ReadAllTextAsync(fixture.SettingsPath).ConfigureAwait(false);
            Require(!settingsText.Contains(marker, StringComparison.Ordinal) &&
                    !settingsText.Contains(locator, StringComparison.Ordinal),
                "The service settings persisted private enrollment metadata.");
            var afterBytes = await File.ReadAllBytesAsync(locator).ConfigureAwait(false);
            Require(afterBytes.AsSpan().SequenceEqual(S3T3ExpectedOmpBytes("s3t3-omp-session-alpha", marker)),
                "The read-only OMP adapter modified the original source bytes.");
            Console.WriteLine("PASS OMP ingestion is encrypted end to end and the original bytes are unchanged");
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
    /// One boundary fixture enrolled on its own source: the explicit refusal
    /// code must name the condition and the checkpoint must not advance.
    /// </summary>
    private static async Task RunS3T3BoundaryFixtureAsync(
        string executable,
        SmokeFixture fixture,
        string zoneId,
        string sourceId,
        string content,
        string expectedCode)
    {
        var locator = Path.Combine(fixture.OutputDirectory, sourceId + ".jsonl");
        await File.WriteAllTextAsync(locator, content).ConfigureAwait(false);
        var requestPath = Path.Combine(fixture.OutputDirectory, sourceId + "-enroll.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { locator })).ConfigureAwait(false);
        var enroll = await RunOwnerCommandAsync(executable,
            "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
            "--source-id", sourceId, "--zone-id", zoneId,
            "--format", OmpSessionNormalizer.FormatName, "--format-version", "1",
            "--input-file", requestPath).ConfigureAwait(false);
        Require(enroll.ExitCode == 0,
            "The boundary fixture enrollment failed (source=" + sourceId + "; stderr=" + enroll.StandardError.Trim() + ").");
        var ingest = await RunOwnerCommandAsync(executable,
            "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
            "--source-id", sourceId).ConfigureAwait(false);
        Require(ingest.ExitCode == 0 &&
                ingest.StandardOutput.Contains("status=ingest_unsupported_record", StringComparison.Ordinal),
            "The boundary fixture did not report an explicit unsupported record (source=" + sourceId + "; stdout=" + ingest.StandardOutput.Trim() + ").");
        var again = await RunOwnerCommandAsync(executable,
            "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
            "--source-id", sourceId).ConfigureAwait(false);
        Require(again.StandardOutput.Contains("status=ingest_unsupported_record", StringComparison.Ordinal) &&
                S3T2IngestConsumed(again.StandardOutput) == S3T2IngestConsumed(ingest.StandardOutput),
            "The boundary fixture was not left blocked at the same offset (source=" + sourceId + ").");
        Require(S3T2IngestDiagnostics(ingest.StandardOutput) == 1,
            "The boundary fixture did not record exactly one diagnostic (source=" + sourceId + ").");
        Console.WriteLine($"PASS boundary fixture {sourceId} is refused explicitly ({expectedCode} class) and stays blocked");
    }

    private const int S3T3OmpRecordCount = 14;

    private const int S3T3OmpAppendRecordCount = 2;

    /// <summary>
    /// One OMP-format event record: the observed envelope
    /// <c>{type, id, parentId, timestamp, ...}</c> with its declared chain
    /// position, followed by the record-specific body fields. A null parent is
    /// the chain root, exactly like the real writer emits.
    /// </summary>
    private static string S3T3OmpEventLine(string id, string? parentId, string timestamp, string typeAndBody) =>
        "{\"type\":" + typeAndBody + ",\"id\":\"" + id + "\",\"parentId\":" +
        (parentId is null ? "null" : "\"" + parentId + "\"") +
        ",\"timestamp\":\"" + timestamp + "\"}\n";

    /// <summary>
    /// Deterministic OMP-format session fixture base lines in the discovered
    /// schema shape (synthetic content only). The order is the title record,
    /// the session record (the declared session identity and cwd context),
    /// the chain root, and one record of every supported event shape —
    /// user/assistant/toolResult messages, custom, custom_message with both
    /// declared attributions, config changes, credential pin, title change and
    /// the compaction boundary — with a complete id/parentId chain across all
    /// of them.
    /// </summary>
    private static string[] S3T3OmpBaseLines(string sessionId, string marker) =>
        new[]
        {
            "{\"type\":\"title\",\"v\":1,\"title\":\"" + sessionId + "\",\"source\":\"auto\",\"updatedAt\":\"2026-10-12T00:00:00.000Z\",\"pad\":\"" + new string(' ', 16) + "\"}\n",
            "{\"type\":\"session\",\"version\":3,\"id\":\"" + sessionId + "\",\"timestamp\":\"2026-10-12T00:00:00.500Z\",\"cwd\":\"C:\\\\synthetic\\\\omp-fixture\",\"title\":\"" + sessionId + "\",\"titleSource\":\"auto\"}\n",
            S3T3OmpEventLine("e01", null, "2026-10-12T00:00:01.000Z",
                "\"thinking_level_change\",\"thinkingLevel\":\"high\",\"configured\":true"),
            S3T3OmpEventLine("e02", "e01", "2026-10-12T00:00:02.000Z",
                "\"service_tier_change\",\"serviceTier\":null"),
            S3T3OmpEventLine("e03", "e02", "2026-10-12T00:00:03.000Z",
                "\"message\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"" + marker + " user request\"}],\"steering\":true,\"attribution\":\"user\",\"timestamp\":\"2026-10-12T00:00:03.000Z\"}"),
            S3T3OmpEventLine("e04", "e03", "2026-10-12T00:00:04.000Z",
                "\"message\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"" + marker + " reasoning\",\"summary\":true},{\"type\":\"toolCall\",\"id\":\"call-1\",\"name\":\"bash\",\"arguments\":{\"command\":\"echo " + marker + "\"},\"intent\":\"run\"}],\"api\":\"responses\",\"provider\":\"openai-codex\",\"model\":\"fixture-model\",\"usage\":{},\"stopReason\":\"toolUse\",\"timestamp\":\"2026-10-12T00:00:04.000Z\",\"credentialId\":\"cred-1\",\"responseId\":\"resp-1\",\"serviceTier\":null,\"providerPayload\":{},\"duration\":10,\"ttft\":5,\"completedAt\":\"2026-10-12T00:00:04.500Z\",\"contextSnapshot\":{}}"),
            S3T3OmpEventLine("e05", "e04", "2026-10-12T00:00:05.000Z",
                "\"message\",\"message\":{\"role\":\"toolResult\",\"toolCallId\":\"call-1\",\"toolName\":\"bash\",\"content\":[{\"type\":\"text\",\"text\":\"" + marker + " tool output\"}],\"details\":{\"exitCode\":0},\"isError\":false,\"timestamp\":\"2026-10-12T00:00:05.000Z\"}"),
            S3T3OmpEventLine("e06", "e05", "2026-10-12T00:00:06.000Z",
                "\"custom\",\"customType\":\"tool_execution_start\",\"data\":{\"toolCallId\":\"call-1\",\"toolName\":\"bash\",\"startedAt\":\"2026-10-12T00:00:04.800Z\",\"args\":{\"command\":\"echo " + marker + "\"},\"intent\":\"run\"}"),
            S3T3OmpEventLine("e07", "e06", "2026-10-12T00:00:07.000Z",
                "\"custom_message\",\"customType\":\"skill-prompt\",\"content\":\"" + marker + " skill prompt\",\"display\":true,\"details\":{\"name\":\"fixture\",\"path\":\"C:\\\\synthetic\\\\omp-fixture\\\\skill.md\",\"args\":\"{}\",\"prompt\":\"" + marker + " skill body\",\"lineCount\":3},\"attribution\":\"user\""),
            S3T3OmpEventLine("e08", "e07", "2026-10-12T00:00:08.000Z",
                "\"custom_message\",\"customType\":\"async-result\",\"content\":\"" + marker + " async result\",\"display\":false,\"details\":{\"meta\":{\"source\":\"fixture\"},\"jobs\":[{\"id\":\"job-1\"}]},\"attribution\":\"agent\""),
            S3T3OmpEventLine("e09", "e08", "2026-10-12T00:00:09.000Z",
                "\"credential_pin\",\"provider\":\"openai-codex\",\"hash\":\"fixture-hash-value\""),
            S3T3OmpEventLine("e10", "e09", "2026-10-12T00:00:10.000Z",
                "\"title_change\",\"title\":\"" + sessionId + " renamed\",\"source\":\"auto\",\"previousTitle\":\"" + sessionId + "\",\"trigger\":\"fixture\""),
            S3T3OmpEventLine("e11", "e10", "2026-10-12T00:00:11.000Z",
                "\"custom\",\"customType\":\"todo_hud_state\",\"data\":{\"sourceEntryId\":\"e03\",\"fingerprint\":\"fixture-fingerprint\",\"visibility\":\"visible\"}"),
            S3T3OmpEventLine("e12", "e11", "2026-10-12T00:00:12.000Z",
                "\"compaction\",\"summary\":\"" + marker + " summary\",\"shortSummary\":\"" + marker + " short\",\"firstKeptEntryId\":\"e03\",\"tokensBefore\":100,\"tokensAfter\":40,\"method\":\"openai-remote\",\"details\":{\"readFiles\":[],\"modifiedFiles\":[]},\"fromExtension\":false,\"preserveData\":{\"openaiRemoteCompaction\":{\"version\":1,\"provider\":\"openai-codex\",\"replacementHistory\":[],\"usedTokens\":40,\"usage\":{\"inputTokens\":10,\"outputTokens\":5,\"totalTokens\":15,\"cachedInputTokens\":0,\"reasoningOutputTokens\":0},\"retainedImageCount\":0}}")
        };

    /// <summary>
    /// The complete deterministic fixture base: every supported record shape
    /// exactly once, in chain order.
    /// </summary>
    private static string S3T3OmpBase(string sessionId, string marker) =>
        string.Concat(S3T3OmpBaseLines(sessionId, marker));

    private static string S3T3OmpAppendRecords(string marker) =>
        string.Concat(
            S3T3OmpEventLine("e13", "e12", "2026-10-12T00:00:14.000Z",
                "\"message\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"" + marker + " follow-up\"}],\"steering\":false,\"attribution\":\"user\",\"timestamp\":\"2026-10-12T00:00:14.000Z\"}"),
            S3T3OmpEventLine("e14", "e13", "2026-10-12T00:00:15.000Z",
                "\"message\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"" + marker + " final answer\"}],\"api\":\"responses\",\"provider\":\"openai-codex\",\"model\":\"fixture-model\",\"usage\":{},\"stopReason\":\"stop\",\"timestamp\":\"2026-10-12T00:00:15.000Z\",\"credentialId\":\"cred-1\",\"serviceTier\":null,\"providerPayload\":{},\"duration\":5,\"ttft\":1,\"completedAt\":\"2026-10-12T00:00:15.500Z\",\"contextSnapshot\":{}}"));

    /// <summary>
    /// The partial-tail record appended without its terminator in Phase A and
    /// completed afterwards; shared so the expected final bytes are built from
    /// exactly the same string.
    /// </summary>
    private static string S3T3OmpPartialRecord(string marker) =>
        S3T3OmpEventLine("e16", "e14", "2026-10-12T00:00:16.000Z",
            "\"message\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"" + marker + " partial without terminator\"}],\"steering\":false,\"attribution\":\"user\",\"timestamp\":\"2026-10-12T00:00:16.000Z\"}");

    private static byte[] S3T3ExpectedOmpBytes(string sessionId, string marker) =>
        Encoding.UTF8.GetBytes(string.Concat(
            S3T3OmpBase(sessionId, marker),
            S3T3OmpAppendRecords(marker),
            S3T3OmpPartialRecord(marker),
            "{\"type\":\"brand_new_omp_record\",\"id\":\"e99\",\"parentId\":\"e16\",\"timestamp\":\"2026-10-12T00:00:17.000Z\",\"anything\":true}\n"));
}
