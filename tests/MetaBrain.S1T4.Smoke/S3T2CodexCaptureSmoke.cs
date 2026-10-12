using System.Diagnostics;
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
    /// S3-T2 Codex session JSONL read-only adapter, proven in two phases:
    /// <list type="bullet">
    /// <item><description>Phase A drives the real service process and the real owner CLI over named pipes with deterministic Codex-format fixtures: enrollment by (format, version), first ingest, idempotent re-import, append, partial tail, and explicit unsupported-record diagnostics through the shipped binary (which now registers the real Codex parser).</description></item>
    /// <item><description>Phase B drives the same production object graph in-process with the real <see cref="CodexSessionNormalizer"/> attached through the real registry, and asserts the stored encrypted journal: per-event attribution (user/assistant/tool/system), session identity, timestamps, exact source spans and digests, plus the boundary matrix (unknown record type, blank line, invalid JSON, bad ordinal, missing identity, unobserved payload type, incomplete tail).</description></item>
    /// </list>
    /// Every fixture is locally generated synthetic data in the discovered Codex
    /// schema shape; no private source is read by this smoke.
    /// </summary>
    internal static async Task RunS3T2CodexAdapterAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S3-T2 Codex adapter smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S3-T2 Codex session JSONL read-only adapter");
        await RunS3T2OwnerServicePhaseAsync(connectionsExecutable).ConfigureAwait(false);
        await RunS3T2CodexCoordinatorPhaseAsync().ConfigureAwait(false);
    }

    private static async Task RunS3T2OwnerServicePhaseAsync(string connectionsExecutable)
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
                "The S3-T2 service fixture did not start uninitialized.");
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "Provision did not create the S3-T2 synthetic vault (exit=" + provisioned.ExitCode + ").");

            const string zoneId = "s3t2-codex-zone";
            const string sourceId = "s3t2-codex-session";
            var locator = Path.Combine(fixture.OutputDirectory, "codex-session.jsonl");
            var marker = "S3T2-CODEX-PAYLOAD-MARKER-" + Guid.NewGuid().ToString("N")[..12];
            var sessionBytes = Encoding.UTF8.GetBytes(S3T2CodexSession("s3t2-session-alpha", marker));
            await File.WriteAllBytesAsync(locator, sessionBytes).ConfigureAwait(false);
            var locatorRequestPath = Path.Combine(fixture.OutputDirectory, "codex-enroll.json");
            await File.WriteAllTextAsync(locatorRequestPath, JsonSerializer.Serialize(new { locator })).ConfigureAwait(false);

            // Enrollment names the discovered Codex format and version; the
            // reply never echoes the locator.
            var enroll = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId, "--zone-id", zoneId,
                "--format", CodexSessionNormalizer.FormatName, "--format-version", "1",
                "--input-file", locatorRequestPath).ConfigureAwait(false);
            Require(enroll.ExitCode == 0 &&
                    enroll.StandardOutput.Contains($"SOURCE id={sourceId}", StringComparison.Ordinal) &&
                    enroll.StandardOutput.Contains($"format={CodexSessionNormalizer.FormatName}", StringComparison.Ordinal) &&
                    enroll.StandardOutput.Contains("version=1", StringComparison.Ordinal) &&
                    enroll.StandardOutput.Contains("generation=1", StringComparison.Ordinal),
                "Owner enrollment of the Codex-format fixture failed (exit=" + enroll.ExitCode + "; stdout=" + enroll.StandardOutput.Trim() + "; stderr=" + enroll.StandardError.Trim() + ").");
            Require(!enroll.StandardOutput.Contains(locator, StringComparison.Ordinal),
                "The owner enrollment reply echoed the private locator.");
            Console.WriteLine("PASS owner enrolls one Codex-format session through the real owner CLI");

            // The agent channel still cannot reach ingestion.
            var agentIngest = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "source.ingest",
                resourceId = sourceId
            }).ConfigureAwait(false);
            Require(ReplyCode(agentIngest) == "forbidden",
                "The agent channel reached a source ingestion operation for a Codex source.");
            Console.WriteLine("PASS the agent channel is refused for Codex ingestion by the transport allowlist");

            var first = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            var expectedRecords = S3T2CodexRecordCount;
            Require(first.ExitCode == 0 &&
                    first.StandardOutput.Contains("status=ingest_advanced", StringComparison.Ordinal) &&
                    S3T2IngestEvents(first.StandardOutput) == expectedRecords &&
                    S3T2IngestConsumed(first.StandardOutput) == sessionBytes.Length,
                "The first Codex ingest did not accept every record exactly once (exit=" + first.ExitCode + "; stdout=" + first.StandardOutput.Trim() + "; stderr=" + first.StandardError.Trim() + ").");
            Console.WriteLine($"PASS the first ingest accepts all {expectedRecords} Codex records and advances the checkpoint to the file end");

            var revisionAfterFirst = S3T2IngestRevision(first.StandardOutput);
            var reimport = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(reimport.ExitCode == 0 && reimport.StandardOutput.Contains("status=ingest_idle", StringComparison.Ordinal) &&
                    S3T2IngestRevision(reimport.StandardOutput) == revisionAfterFirst &&
                    S3T2IngestEvents(reimport.StandardOutput) == expectedRecords,
                "Re-import of a Codex session was not idempotent (status=" + reimport.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS re-import is a no-op: same journal revision, same events, no duplicates");

            await File.AppendAllTextAsync(locator, S3T2CodexAppendRecords("s3t2-session-alpha", marker)).ConfigureAwait(false);
            var appended = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(appended.ExitCode == 0 && appended.StandardOutput.Contains("status=ingest_advanced", StringComparison.Ordinal) &&
                    S3T2IngestEvents(appended.StandardOutput) == expectedRecords + S3T2CodexAppendRecordCount &&
                    S3T2IngestConsumed(appended.StandardOutput) == new FileInfo(locator).Length,
                "The appended Codex records were not consumed exactly once (stdout=" + appended.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS append consumes only the new Codex records");

            // Partial tail: an incomplete trailing record is never committed.
            var partialRecord = S3T2CodexLine("2026-10-12T00:00:17.000Z", 17, "response_item",
                "{\"type\":\"message\",\"id\":\"msg-partial\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + marker + " partial without terminator\"}]}");
            await File.AppendAllTextAsync(locator, partialRecord[..^1]).ConfigureAwait(false);
            var partial = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(partial.ExitCode == 0 && partial.StandardOutput.Contains("status=ingest_partial_pending", StringComparison.Ordinal) &&
                    S3T2IngestConsumed(partial.StandardOutput) < new FileInfo(locator).Length &&
                    S3T2IngestEvents(partial.StandardOutput) == expectedRecords + S3T2CodexAppendRecordCount,
                "The incomplete trailing record was committed or lost (stdout=" + partial.StandardOutput.Trim() + ").");
            await File.AppendAllTextAsync(locator, "\n").ConfigureAwait(false);
            var completed = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(completed.ExitCode == 0 && completed.StandardOutput.Contains("status=ingest_advanced", StringComparison.Ordinal) &&
                    S3T2IngestEvents(completed.StandardOutput) == expectedRecords + S3T2CodexAppendRecordCount + 1 &&
                    S3T2IngestConsumed(completed.StandardOutput) == new FileInfo(locator).Length,
                "The completed record after the partial tail was not consumed exactly once (stdout=" + completed.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS a partial tail stays unconsumed and is consumed exactly once when completed");

            // An unobserved record shape stops ingestion explicitly at its
            // offset: no silent skip, no fabricated valid event.
            var unsupportedRecord = S3T2CodexLine("2026-10-12T00:00:10.000Z", 7, "response_item",
                "{\"type\":\"unobserved_mystery_tool\",\"call_id\":\"call-x\"}");
            await File.AppendAllTextAsync(locator, unsupportedRecord).ConfigureAwait(false);
            var unsupported = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            var expectedTotal = expectedRecords + S3T2CodexAppendRecordCount + 1;
            Require(unsupported.ExitCode == 0 &&
                    unsupported.StandardOutput.Contains("status=ingest_unsupported_record", StringComparison.Ordinal) &&
                    S3T2IngestEvents(unsupported.StandardOutput) == expectedTotal &&
                    S3T2IngestDiagnostics(unsupported.StandardOutput) == 1,
                "An unobserved Codex record shape did not stop the run with an explicit diagnostic (stdout=" + unsupported.StandardOutput.Trim() + ").");
            var stuckOffset = S3T2IngestConsumed(unsupported.StandardOutput);
            var unsupportedAgain = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(unsupportedAgain.StandardOutput.Contains("status=ingest_unsupported_record", StringComparison.Ordinal) &&
                    S3T2IngestConsumed(unsupportedAgain.StandardOutput) == stuckOffset,
                "The unsupported Codex record was not left explicitly blocked on re-import.");
            Console.WriteLine("PASS an unobserved record shape stops ingestion explicitly without silent skip or drop");

            // Boundary fixtures on separate enrollments: every refusal is
            // explicit and names what was seen.
            await RunS3T2BoundaryFixtureAsync(connectionsExecutable, fixture, zoneId, "s3t2-codex-unknown-type",
                S3T2CodexSession("s3t2-session-unknown", marker) +
                S3T2CodexLine("2026-10-12T00:00:11.000Z", 1, "brand_new_record_type", "{\"anything\":true}"),
                "codex_type_unsupported");
            await RunS3T2BoundaryFixtureAsync(connectionsExecutable, fixture, zoneId, "s3t2-codex-blank-line",
                S3T2CodexSession("s3t2-session-blank", marker) + "\n",
                "codex_record_blank");
            await RunS3T2BoundaryFixtureAsync(connectionsExecutable, fixture, zoneId, "s3t2-codex-invalid-json",
                S3T2CodexSession("s3t2-session-json", marker) + "{not json}\n",
                "codex_record_json_invalid");
            await RunS3T2BoundaryFixtureAsync(connectionsExecutable, fixture, zoneId, "s3t2-codex-missing-identity",
                "{\"timestamp\":\"2026-10-12T00:00:12.000Z\",\"type\":\"session_meta\",\"payload\":{\"cwd\":\"C:\\\\synthetic\"}}\n",
                "codex_session_identity_invalid");
            await RunS3T2BoundaryFixtureAsync(connectionsExecutable, fixture, zoneId, "s3t2-codex-bad-ordinal",
                S3T2CodexSession("s3t2-session-ordinal", marker).Replace("\"ordinal\":1", "\"ordinal\":-1", StringComparison.Ordinal),
                "codex_record_ordinal_invalid");
            Console.WriteLine("PASS unknown type, blank line, invalid JSON, missing session identity and bad ordinal are explicit diagnostics");

            // Locked vault refuses Codex ingestion like any other source.
            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The owner could not lock the S3-T2 vault.");
            var lockedIngest = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", sourceId).ConfigureAwait(false);
            Require(lockedIngest.ExitCode == 4 && lockedIngest.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "Codex ingestion was not refused while the vault was locked.");
            var unlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0, "The owner could not unlock the S3-T2 vault.");
            Console.WriteLine("PASS a locked vault refuses Codex ingestion; unlock resumes from the persisted checkpoint");

            // Encrypted persistence and original-byte preservation.
            var vaultFiles = Directory.GetFiles(fixture.VaultDirectoryPath, "*", SearchOption.AllDirectories);
            Require(vaultFiles.Length >= 3, "The encrypted vault did not persist its private files.");
            foreach (var path in vaultFiles)
            {
                var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                Require(!text.Contains(marker, StringComparison.Ordinal) &&
                        !text.Contains(locator, StringComparison.Ordinal) &&
                        !text.Contains(CodexSessionNormalizer.FormatName, StringComparison.Ordinal),
                    "An encrypted vault file persisted plaintext payload, locator or format: " + Path.GetFileName(path) + ".");
            }

            var settingsText = await File.ReadAllTextAsync(fixture.SettingsPath).ConfigureAwait(false);
            Require(!settingsText.Contains(marker, StringComparison.Ordinal) &&
                    !settingsText.Contains(locator, StringComparison.Ordinal),
                "The service settings persisted private enrollment metadata.");
            var afterBytes = await File.ReadAllBytesAsync(locator).ConfigureAwait(false);
            Require(afterBytes.AsSpan().SequenceEqual(S3T2ExpectedCodexBytes("s3t2-session-alpha", marker)),
                "The read-only Codex adapter modified the original source bytes.");
            Console.WriteLine("PASS Codex ingestion is encrypted end to end and the original bytes are unchanged");
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
    private static async Task RunS3T2BoundaryFixtureAsync(
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
            "--format", CodexSessionNormalizer.FormatName, "--format-version", "1",
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

    private const int S3T2CodexRecordCount = 14;

    private const int S3T2CodexAppendRecordCount = 2;

    /// <summary>
    /// Deterministic Codex-format session fixture in the discovered schema
    /// shape (synthetic content only). Every record is one JSON object per
    /// line with the observed envelope fields, and the record mix covers the
    /// attribution-bearing shapes the adapter supports.
    /// </summary>
    private static string S3T2CodexSession(string sessionId, string marker) =>
        string.Concat(
            S3T2CodexLine("2026-10-12T00:00:00.000Z", 0, "session_meta",
                "{\"id\":\"" + sessionId + "\",\"session_id\":\"s3t2-parent-thread\",\"timestamp\":\"2026-10-12T00:00:00.000Z\"," +
                "\"cwd\":\"C:\\\\synthetic\\\\codex-fixture\",\"originator\":\"MetaBrain-S3T2-Smoke\",\"cli_version\":\"fixture-1.0\"," +
                "\"source\":\"cli\",\"git\":{}}"),
            S3T2CodexLine("2026-10-12T00:00:01.000Z", 1, "event_msg",
                "{\"type\":\"task_started\",\"turn_id\":\"turn-1\",\"started_at\":1,\"model_context_window\":1000,\"collaboration_mode_kind\":\"default\"}"),
            S3T2CodexLine("2026-10-12T00:00:02.000Z", 2, "response_item",
                "{\"type\":\"message\",\"id\":\"msg-dev\",\"role\":\"developer\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + marker + " developer instructions\"}]}"),
            S3T2CodexLine("2026-10-12T00:00:03.000Z", 3, "response_item",
                "{\"type\":\"message\",\"id\":\"msg-user-1\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + marker + " user request\"},{\"type\":\"input_text\",\"text\":\"" + marker + " user detail\"}]}"),
            S3T2CodexLine("2026-10-12T00:00:04.000Z", 4, "response_item",
                "{\"type\":\"function_call\",\"id\":\"fc-1\",\"call_id\":\"call-1\",\"name\":\"shell\",\"status\":\"completed\",\"arguments\":\"{\\\"command\\\":[\\\"echo\\\",\\\"" + marker + "\\\"]}\"}"),
            S3T2CodexLine("2026-10-12T00:00:05.000Z", 5, "response_item",
                "{\"type\":\"function_call_output\",\"id\":\"fco-1\",\"call_id\":\"call-1\",\"name\":\"shell\",\"output\":\"" + marker + " tool output text\"}"),
            S3T2CodexLine("2026-10-12T00:00:06.000Z", 6, "response_item",
                "{\"type\":\"reasoning\",\"id\":\"rs-1\",\"summary\":[],\"content\":null,\"encrypted_content\":\"opaque\"}"),
            S3T2CodexLine("2026-10-12T00:00:07.000Z", 7, "response_item",
                "{\"type\":\"message\",\"id\":\"msg-assistant-1\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"" + marker + " final answer\"}],\"phase\":\"final_answer\"}"),
            S3T2CodexLine("2026-10-12T00:00:08.000Z", 8, "event_msg",
                "{\"type\":\"item_completed\",\"thread_id\":\"s3t2-parent-thread\",\"turn_id\":\"turn-1\",\"item\":{\"type\":\"UserMessage\",\"id\":\"mi-1\",\"content\":[{\"type\":\"text\",\"text\":\"" + marker + " echoed user\"}]},\"completed_at_ms\":1000}"),
            S3T2CodexLine("2026-10-12T00:00:09.000Z", 9, "turn_context",
                "{\"turn_id\":\"turn-1\",\"root_turn_id\":\"turn-1\",\"cwd\":\"C:\\\\synthetic\\\\codex-fixture\",\"workspace_roots\":[\"C:\\\\synthetic\\\\codex-fixture\"],\"model\":\"fixture-model\"}"),
            S3T2CodexLine("2026-10-12T00:00:10.000Z", 10, "world_state",
                "{\"full\":true,\"state\":{\"agents_md\":{\"directory\":\"C:\\\\synthetic\\\\codex-fixture\",\"text\":\"" + marker + " agents\"}}}"),
            S3T2CodexLine("2026-10-12T00:00:11.000Z", 11, "token_usage_record",
                "{\"thread_id\":\"s3t2-parent-thread\",\"turn_id\":\"turn-1\",\"session_id\":\"" + sessionId + "\",\"usage\":{\"input_tokens\":1,\"output_tokens\":2,\"total_tokens\":3}}"),
            S3T2CodexLine("2026-10-12T00:00:12.000Z", 12, "event_msg",
                "{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":1,\"output_tokens\":2,\"total_tokens\":3}}}"),
            S3T2CodexLine("2026-10-12T00:00:13.000Z", 13, "event_msg",
                "{\"type\":\"task_complete\",\"turn_id\":\"turn-1\",\"last_agent_message\":\"" + marker + " final answer\",\"started_at\":1,\"completed_at\":2,\"duration_ms\":1000,\"time_to_first_token_ms\":10}"));

    private static string S3T2CodexAppendRecords(string sessionId, string marker) =>
        string.Concat(
            S3T2CodexLine("2026-10-12T00:00:14.000Z", 14, "response_item",
                "{\"type\":\"message\",\"id\":\"msg-user-2\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + marker + " follow-up\"}]}"),
            S3T2CodexLine("2026-10-12T00:00:15.000Z", 15, "response_item",
                "{\"type\":\"message\",\"id\":\"msg-assistant-2\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"" + marker + " follow-up answer\"}],\"phase\":\"final_answer\",\"internal_chat_message_metadata_passthrough\":{\"turn_id\":\"turn-1\"}}"));

    private static byte[] S3T2ExpectedCodexBytes(string sessionId, string marker) =>
        Encoding.UTF8.GetBytes(string.Concat(
            S3T2CodexSession(sessionId, marker),
            S3T2CodexAppendRecords(sessionId, marker),
            S3T2CodexLine("2026-10-12T00:00:17.000Z", 17, "response_item",
                "{\"type\":\"message\",\"id\":\"msg-partial\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + marker + " partial without terminator\"}]}"),
            S3T2CodexLine("2026-10-12T00:00:10.000Z", 7, "response_item",
                "{\"type\":\"unobserved_mystery_tool\",\"call_id\":\"call-x\"}")));

    private static string S3T2CodexLine(string timestamp, int ordinal, string type, string payload) =>
        "{\"timestamp\":\"" + timestamp + "\",\"ordinal\":" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ",\"type\":\"" + type + "\",\"payload\":" + payload + "}\n";

    private static string S3T2IngestField(string output, string field)
    {
        foreach (var part in output.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith(field + "=", StringComparison.Ordinal))
            {
                return part[(field.Length + 1)..];
            }
        }

        return string.Empty;
    }

    private static int S3T2IngestEvents(string output) =>
        int.TryParse(S3T2IngestField(output, "events"), out var events) ? events : -1;

    private static long S3T2IngestConsumed(string output) =>
        long.TryParse(S3T2IngestField(output, "consumedOffset"), out var consumed) ? consumed : -1;

    private static long S3T2IngestRevision(string output) =>
        long.TryParse(S3T2IngestField(output, "journalRevision"), out var revision) ? revision : -1;

    private static int S3T2IngestDiagnostics(string output) =>
        int.TryParse(S3T2IngestField(output, "diagnostics"), out var diagnostics) ? diagnostics : -1;
}
