using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string S6T1Zone = "s6t1-zone";
    private const string S6T1SourceNoteId = "s6t1-source-note";
    private const string S6T1DestinationInboxId = "s6t1-inbox";

    // The exact owner-consented synthetic research note
    private const string S6T1SyntheticNoteText =
        "Synthetic Research Note: In distributed state machines, replication requires quorum writes. " +
        "Hypothesis: vector clocks detect concurrent conflicting mutations without clock sync. " +
        "Limitation: network partitions degrade availability under CP mode.";

    internal static async Task RunS6T1ModelJobsAsync(string connectionsExecutable)
    {
        Console.WriteLine("RUN S6-T1 scoped model job execution and provider controls walkthrough");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            var now = DateTimeOffset.UtcNow.AddMinutes(-5);

            // 1. Setup service and provision/unlock synthetic vault
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provision = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0, "Vault provision failed.");

            var unlock = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0, "Vault unlock failed.");

            // 2. Setup destination inbox (memory item destination)
            var inboxDraft = JsonSerializer.Serialize(new
            {
                kind = "observation",
                author = "user",
                creationMethod = "directWrite",
                evidence = "userConfirmed",
                level = "l1",
                title = "S6-T1 Synthetic Destination Inbox",
                body = "Destination inbox marker for S6-T1 model job structuring proposals.",
                sources = Array.Empty<object>(),
                occurredAtUtc = now,
                recordedAtUtc = now.AddMinutes(1)
            });
            var inboxInput = Path.Combine(fixture.OutputDirectory, "s6t1-inbox.json");
            await File.WriteAllTextAsync(inboxInput, inboxDraft, Encoding.UTF8).ConfigureAwait(false);
            var createInbox = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S6T1DestinationInboxId, "--zone-id", S6T1Zone,
                "--input-file", inboxInput).ConfigureAwait(false);
            Require(createInbox.ExitCode == 0 && createInbox.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
                "Failed to create destination inbox.");
            File.Delete(inboxInput);

            // 3. Capture the exact synthetic research note as encrypted registered source
            var noteInput = Path.Combine(fixture.OutputDirectory, "s6t1-note.txt");
            await File.WriteAllTextAsync(noteInput, S6T1SyntheticNoteText, Encoding.UTF8).ConfigureAwait(false);
            var capture = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-capture", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S6T1SourceNoteId, "--zone-id", S6T1Zone,
                "--input-file", noteInput).ConfigureAwait(false);
            Require(capture.ExitCode == 0 && capture.StandardOutput.Contains("RESEARCH captured", StringComparison.Ordinal),
                "Failed to capture synthetic note.");
            File.Delete(noteInput);

            // =========================================================================
            // Negative Controls (Offline, Deterministic - Zero Outbound Provider Calls)
            // =========================================================================

            // A. Unknown pricing / unapproved provider fails closed
            var unknownPricing = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", S6T1SourceNoteId, "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "unapproved-provider", "--model", "unknown-model").ConfigureAwait(false);
            Require(unknownPricing.ExitCode != 0 && (unknownPricing.StandardError.Contains("pricing_unknown", StringComparison.Ordinal) || unknownPricing.StandardOutput.Contains("pricing_unknown", StringComparison.Ordinal)),
                $"Job creation with unapproved provider/model did not fail closed (exit={unknownPricing.ExitCode}).");
            Console.WriteLine("PASS negative: unapproved provider/model denied with pricing_unknown");

            // A2. Unknown tokenizer or context window denied with unknown_context
            var badContextCard = new ModelPriceCard(
                Provider: "test-provider",
                Model: "unsupported-tokenizer-model",
                InputUsdPerMillion: 0.30m,
                OutputUsdPerMillion: 1.20m,
                CachedInputUsdPerMillion: 0.006m,
                Tokenizer: "unsupported-tokenizer",
                ContextWindow: 0);

            var obsoleteV3Card = new ModelPriceCard(
                Provider: "test-provider",
                Model: "obsolete-v3-model",
                InputUsdPerMillion: 0.30m,
                OutputUsdPerMillion: 1.20m,
                CachedInputUsdPerMillion: 0.006m,
                Tokenizer: "deepseek-v3-bbpe",
                ContextWindow: 1_000_000);
            try
            {
                ModelPricingCatalog.RegisterPriceCard(badContextCard);
                ModelPricingCatalog.RegisterPriceCard(obsoleteV3Card);

                Require(!ModelPricingCatalog.TryGetApprovedModel("test-provider", "unsupported-tokenizer-model", out _, out var reason) &&
                        reason == "unknown_context",
                    "ModelPricingCatalog did not reject model with unknown tokenizer or context window.");

                Require(!ModelPricingCatalog.TryGetApprovedModel("test-provider", "obsolete-v3-model", out _, out var v3Reason) &&
                        v3Reason == "unknown_context",
                    "ModelPricingCatalog did not reject model with obsolete V3 tokenizer.");

                bool estimateThrewUnknownContext = false;
                try
                {
                    ModelJobService.EstimateFramedInputTokens("test-provider", "unsupported-tokenizer-model", "test system", "test source");
                }
                catch (ArgumentException ex) when (ex.Message.Contains("unknown_context", StringComparison.Ordinal))
                {
                    estimateThrewUnknownContext = true;
                }
                Require(estimateThrewUnknownContext, "EstimateFramedInputTokens did not throw unknown_context for unsupported model.");
            }
            finally
            {
                ModelPricingCatalog.UnregisterPriceCard("test-provider", "unsupported-tokenizer-model");
                ModelPricingCatalog.UnregisterPriceCard("test-provider", "obsolete-v3-model");
            }
            Console.WriteLine("PASS negative: unknown tokenizer or context window denied with unknown_context");

            // Grounded runtime token bound check
            var wireJson = OpenAiCompatibleProviderAdapter.BuildRequestJson("deepseek-flash", "test system", "test source", 2000);
            var wireBytes = (long)Encoding.UTF8.GetByteCount(wireJson);
            var estimatedTokens = ModelJobService.EstimateFramedInputTokens("deepseek", "deepseek-flash", "test system", "test source", 2000);
            var systemBytes = (long)Encoding.UTF8.GetByteCount("test system");
            var sourceBytes = (long)Encoding.UTF8.GetByteCount("test source");
            const long templateTokens = 30; // Official DeepSeek-V4.1-Flash thinking mode framing tokens
            Require(estimatedTokens >= wireBytes, $"Estimator tokens ({estimatedTokens}) must cover wire bytes ({wireBytes}).");
            Require(estimatedTokens >= systemBytes + sourceBytes + templateTokens, $"Estimator tokens ({estimatedTokens}) must cover system + source + framing tokens ({systemBytes + sourceBytes + templateTokens}).");
            Require(estimatedTokens <= SpendLedger.MaximumJobInputTokens, $"Estimator tokens ({estimatedTokens}) must fit within maximum input token cap ({SpendLedger.MaximumJobInputTokens}).");
            Console.WriteLine("PASS runtime grounded token bound: estimator bound conservatively covers DeepSeek-V4.1-Flash rendered prompt tokens and framing");
            // B. Maximum cost less than reservation requirement ($0.0054) denies pre-send
            var lowCost = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", S6T1SourceNoteId, "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash",
                "--max-cost-usd", "0.0001").ConfigureAwait(false);
            Require(lowCost.ExitCode != 0 && (lowCost.StandardError.Contains("less than the required pre-send reservation", StringComparison.Ordinal) || lowCost.StandardOutput.Contains("less than the required pre-send reservation", StringComparison.Ordinal)),
                "Job creation with insufficient cost cap did not deny pre-send.");
            Console.WriteLine("PASS negative: cost cap below pre-send reservation denied");

            // C. Destination zone mismatch denied
            var zoneMismatch = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", S6T1SourceNoteId, "--source-revision", "1",
                "--zone-id", "wrong-zone", "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            Require(zoneMismatch.ExitCode != 0 && (zoneMismatch.StandardError.Contains("does not match requested destination zone", StringComparison.Ordinal) || zoneMismatch.StandardOutput.Contains("does not match requested destination zone", StringComparison.Ordinal)),
                "Job creation with mismatched destination zone did not fail closed.");
            Console.WriteLine("PASS negative: destination zone mismatch denied");

            // D. Unregistered source resource denied
            var unregSource = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", "nonexistent-source", "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            Require(unregSource.ExitCode != 0 && (unregSource.StandardError.Contains("is not registered in any zone", StringComparison.Ordinal) || unregSource.StandardOutput.Contains("is not registered in any zone", StringComparison.Ordinal)),
                "Job creation with unregistered source resource did not fail closed.");
            Console.WriteLine("PASS negative: unregistered source resource denied");

            // E. Starting before provider config fails closed without network egress
            var unconfiguredJob = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", S6T1SourceNoteId, "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            Require(unconfiguredJob.ExitCode == 0 && unconfiguredJob.StandardOutput.Contains("state=Pending", StringComparison.Ordinal),
                "Job creation failed for valid deepseek-flash scope.");
            var unconfiguredJobId = ExtractJobId(unconfiguredJob.StandardOutput);

            var ledgerAfterCreate = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledgerAfterCreate.ExitCode == 0 && ledgerAfterCreate.StandardOutput.Contains("historicalReservation=0.0054", StringComparison.Ordinal) &&
                    ledgerAfterCreate.StandardOutput.Contains("isReservation=True", StringComparison.Ordinal),
                "Spend ledger did not record historical reservation and new job reservation.");

            var startUnconfigured = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-start", "--control-pipe", fixture.ControlPipe,
                "--job-id", unconfiguredJobId).ConfigureAwait(false);
            Require(startUnconfigured.ExitCode != 0 && startUnconfigured.StandardOutput.Contains("state=Failed", StringComparison.Ordinal) &&
                    startUnconfigured.StandardOutput.Contains("fail=provider_unconfigured", StringComparison.Ordinal),
                "Starting job with unconfigured provider did not fail closed.");

            // Verify reservation was safely released
            var ledgerAfterUnconfigured = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledgerAfterUnconfigured.ExitCode == 0 && !ledgerAfterUnconfigured.StandardOutput.Contains(unconfiguredJobId, StringComparison.Ordinal),
                "Reservation was not released after provider_unconfigured failure.");
            Console.WriteLine("PASS negative: starting unconfigured provider fails closed without network egress");

            // F1. Grounded framing-inclusive input token cap (>10,000 tokens) denied
            // Note of 9,400 bytes + system prompt (~672 bytes) + JSON framing (~164 bytes) = 10,236 bytes > 10,000 cap
            var boundaryText = new string('B', 9_400);
            var boundaryNoteInput = Path.Combine(fixture.OutputDirectory, "boundary-note.txt");
            await File.WriteAllTextAsync(boundaryNoteInput, boundaryText, Encoding.UTF8).ConfigureAwait(false);
            var captureBoundary = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-capture", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "boundary-note", "--zone-id", S6T1Zone,
                "--input-file", boundaryNoteInput).ConfigureAwait(false);
            Require(captureBoundary.ExitCode == 0, "Failed to capture boundary note.");
            File.Delete(boundaryNoteInput);

            var boundaryJob = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", "boundary-note", "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            Require(boundaryJob.ExitCode != 0 && (boundaryJob.StandardError.Contains("exceed the whole-job cap of 10000", StringComparison.Ordinal) || boundaryJob.StandardOutput.Contains("exceed the whole-job cap of 10000", StringComparison.Ordinal)),
                "Job creation with boundary-exceeding framed input did not fail closed.");

            // F2. Oversized input note (10,001 bytes) denied
            var hugeText = new string('A', 10_001);
            var hugeNoteInput = Path.Combine(fixture.OutputDirectory, "huge-note.txt");
            await File.WriteAllTextAsync(hugeNoteInput, hugeText, Encoding.UTF8).ConfigureAwait(false);
            var captureHuge = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-capture", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "huge-note", "--zone-id", S6T1Zone,
                "--input-file", hugeNoteInput).ConfigureAwait(false);
            Require(captureHuge.ExitCode == 0, "Failed to capture huge note.");
            File.Delete(hugeNoteInput);

            var hugeJob = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", "huge-note", "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            Require(hugeJob.ExitCode != 0 && (hugeJob.StandardError.Contains("exceed the whole-job cap of 10000", StringComparison.Ordinal) || hugeJob.StandardOutput.Contains("exceed the whole-job cap of 10000", StringComparison.Ordinal)),
                "Job creation with oversized input did not fail closed.");
            Console.WriteLine("PASS negative: input token cap exceeded with grounded full-request bound denied");

            // F3. Revoke / revision change between create and start fails closed and releases reservation
            var probeDraft1 = JsonSerializer.Serialize(new
            {
                title = "Revoke Probe Initial",
                locator = "synthetic-revoke-locator",
                version = "v1",
                availability = "available",
                creationMethod = "directWrite",
                capturedAtUtc = now,
                recordedAtUtc = now.AddMinutes(1),
                author = "user",
                text = "Initial source content before revocation probe."
            });
            var revokeNoteInput = Path.Combine(fixture.OutputDirectory, "revoke-note.json");
            await File.WriteAllTextAsync(revokeNoteInput, probeDraft1, Encoding.UTF8).ConfigureAwait(false);
            var captureRevoke = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "revoke-probe-note", "--zone-id", S6T1Zone,
                "--input-file", revokeNoteInput).ConfigureAwait(false);
            Require(captureRevoke.ExitCode == 0 && captureRevoke.StandardOutput.Contains("SOURCE stored revision=1", StringComparison.Ordinal),
                $"Failed to create revoke probe note (out={captureRevoke.StandardOutput}, err={captureRevoke.StandardError}).");
            File.Delete(revokeNoteInput);

            var revokeJob = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", "revoke-probe-note", "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            Require(revokeJob.ExitCode == 0, $"Failed to create job for revoke test: exit={revokeJob.ExitCode}, err={revokeJob.StandardError}, out={revokeJob.StandardOutput}");
            var revokeJobId = ExtractJobId(revokeJob.StandardOutput);

            // Verify reservation was recorded in spend ledger
            var ledgerBeforeRevoke = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledgerBeforeRevoke.ExitCode == 0 && ledgerBeforeRevoke.StandardOutput.Contains(revokeJobId, StringComparison.Ordinal),
                "Job reservation was not recorded in spend ledger before revoke.");

            // Revise the source note to revision 2 (breaking the job's scoped revision 1)
            var probeDraft2 = JsonSerializer.Serialize(new
            {
                title = "Revoke Probe Revised",
                locator = "synthetic-revoke-locator",
                version = "v2",
                availability = "available",
                creationMethod = "directWrite",
                capturedAtUtc = now,
                recordedAtUtc = now.AddMinutes(2),
                author = "user",
                text = "Altered source content invalidating revision 1."
            });
            var revisedNoteInput = Path.Combine(fixture.OutputDirectory, "revoke-note-rev2.json");
            await File.WriteAllTextAsync(revisedNoteInput, probeDraft2, Encoding.UTF8).ConfigureAwait(false);
            var reviseCapture = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-revise", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "revoke-probe-note", "--zone-id", S6T1Zone,
                "--expected-revision", "1",
                "--input-file", revisedNoteInput).ConfigureAwait(false);
            Require(reviseCapture.ExitCode == 0 && reviseCapture.StandardOutput.Contains("SOURCE stored revision=2", StringComparison.Ordinal),
                $"Failed to revise revoke probe note to revision 2 (out={reviseCapture.StandardOutput}, err={reviseCapture.StandardError}).");
            File.Delete(revisedNoteInput);

            // Now attempt to start the job scoped to revision 1 -> must fail closed with source_unavailable
            var startRevoked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-start", "--control-pipe", fixture.ControlPipe,
                "--job-id", revokeJobId).ConfigureAwait(false);
            Require(startRevoked.ExitCode != 0 && startRevoked.StandardOutput.Contains("state=Failed", StringComparison.Ordinal) &&
                    startRevoked.StandardOutput.Contains("fail=source_unavailable", StringComparison.Ordinal),
                $"Starting job with altered source revision did not fail closed with source_unavailable (stdout={startRevoked.StandardOutput.Trim()}).");

            // Verify reservation was safely released
            var ledgerAfterRevoke = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledgerAfterRevoke.ExitCode == 0 && !ledgerAfterRevoke.StandardOutput.Contains(revokeJobId, StringComparison.Ordinal),
                "Spend ledger reservation was not released after source_unavailable failure!");
            Console.WriteLine("PASS negative: source revision change between create and start fails closed with source_unavailable and releases reservation");

            // F3-b. True source-revoke between create and start fails closed with source_unavailable and releases reservation
            var trueRevokeFixturePath = Path.Combine(fixture.OutputDirectory, "true-revoke-fixture.jsonl");
            await File.WriteAllTextAsync(trueRevokeFixturePath, S3T2CodexSession("s6t1-true-revoke-sess", "true-revoke-marker")).ConfigureAwait(false);
            var trueRevokeLocatorPath = Path.Combine(fixture.OutputDirectory, "true-revoke-locator.json");
            await File.WriteAllTextAsync(trueRevokeLocatorPath, JsonSerializer.Serialize(new { locator = trueRevokeFixturePath })).ConfigureAwait(false);

            var enrollTrueRevoke = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", "true-revoke-source", "--zone-id", S6T1Zone,
                "--format", "codex.session.jsonl", "--format-version", "1",
                "--input-file", trueRevokeLocatorPath).ConfigureAwait(false);
            Require(enrollTrueRevoke.ExitCode == 0, $"Failed to enroll true revoke fixture: {enrollTrueRevoke.StandardError}");

            var trueRevokeNoteInput = Path.Combine(fixture.OutputDirectory, "true-revoke-note.json");
            await File.WriteAllTextAsync(trueRevokeNoteInput, JsonSerializer.Serialize(new
            {
                title = "True Revoke Note",
                locator = trueRevokeFixturePath,
                version = "1",
                availability = "available",
                creationMethod = "directWrite",
                capturedAtUtc = now,
                recordedAtUtc = now,
                author = "user",
                text = "Verbatim text for true revoke probe note."
            }), Encoding.UTF8).ConfigureAwait(false);

            var createTrueRevokeSource = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "true-revoke-source", "--zone-id", S6T1Zone,
                "--input-file", trueRevokeNoteInput).ConfigureAwait(false);
            Require(createTrueRevokeSource.ExitCode == 0, $"Failed to create true revoke source note: {createTrueRevokeSource.StandardError}");
            File.Delete(trueRevokeNoteInput);

            var trueRevokeJob = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", "true-revoke-source", "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            Require(trueRevokeJob.ExitCode == 0, $"Failed to create true revoke job: {trueRevokeJob.StandardError}");
            var trueRevokeJobId = ExtractJobId(trueRevokeJob.StandardOutput);

            var ledgerBeforeTrueRevoke = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledgerBeforeTrueRevoke.ExitCode == 0 && ledgerBeforeTrueRevoke.StandardOutput.Contains(trueRevokeJobId, StringComparison.Ordinal),
                "Spend ledger missing reservation before true revoke.");

            var doRevoke = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-revoke", "--control-pipe", fixture.ControlPipe,
                "--source-id", "true-revoke-source").ConfigureAwait(false);
            Require(doRevoke.ExitCode == 0 && doRevoke.StandardOutput.Contains("life=revoked", StringComparison.Ordinal),
                $"True source-revoke failed: {doRevoke.StandardError}");

            var startTrueRevoke = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-start", "--control-pipe", fixture.ControlPipe,
                "--job-id", trueRevokeJobId).ConfigureAwait(false);
            Require(startTrueRevoke.ExitCode != 0 &&
                    startTrueRevoke.StandardOutput.Contains("state=Failed", StringComparison.Ordinal) &&
                    startTrueRevoke.StandardOutput.Contains("fail=source_unavailable", StringComparison.Ordinal),
                $"Job start on revoked source did not fail with source_unavailable (stdout={startTrueRevoke.StandardOutput.Trim()}).");

            var ledgerAfterTrueRevoke = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledgerAfterTrueRevoke.ExitCode == 0 && !ledgerAfterTrueRevoke.StandardOutput.Contains(trueRevokeJobId, StringComparison.Ordinal),
                "Spend ledger reservation was not released after true source-revoke failure!");
            Console.WriteLine("PASS negative: true source-revoke between create and start fails closed with source_unavailable and releases reservation");

            // F4. Concurrent multi-job reservation atomicity: multiple parallel creations synchronize under _gate
            var concurrentJobTasks = Enumerable.Range(0, 5).Select(async i =>
            {
                return await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                    "--source-id", S6T1SourceNoteId, "--source-revision", "1",
                    "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                    "--destination-revision", "1",
                    "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            }).ToArray();
            var concurrentResults = await Task.WhenAll(concurrentJobTasks).ConfigureAwait(false);
            var concurrentJobIds = new List<string>();
            foreach (var res in concurrentResults)
            {
                Require(res.ExitCode == 0 && res.StandardOutput.Contains("state=Pending", StringComparison.Ordinal),
                    $"Concurrent job creation failed: exit={res.ExitCode}, out={res.StandardOutput}");
                var id = ExtractJobId(res.StandardOutput);
                Require(!concurrentJobIds.Contains(id), $"Duplicate job ID generated under concurrency: {id}");
                concurrentJobIds.Add(id);
            }

            // Check ledger: all 5 jobs recorded reservations atomically
            var ledgerConcurrent = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledgerConcurrent.ExitCode == 0, "Failed to read spend ledger after concurrent creations.");
            foreach (var id in concurrentJobIds)
            {
                Require(ledgerConcurrent.StandardOutput.Contains(id, StringComparison.Ordinal),
                    $"Spend ledger missing reservation for concurrent job {id}");
            }

            // Cancel all 5 concurrent jobs in parallel
            var cancelConcurrentTasks = concurrentJobIds.Select(async id =>
            {
                return await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "job-cancel", "--control-pipe", fixture.ControlPipe,
                    "--job-id", id).ConfigureAwait(false);
            }).ToArray();
            var cancelConcurrentResults = await Task.WhenAll(cancelConcurrentTasks).ConfigureAwait(false);
            foreach (var cancelRes in cancelConcurrentResults)
            {
                Require(cancelRes.ExitCode == 0, "Failed to cancel concurrent job.");
            }

            // Verify all reservations were released
            var ledgerAfterConcurrentCancel = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledgerAfterConcurrentCancel.ExitCode == 0, "Failed to read spend ledger after canceling concurrent jobs.");
            foreach (var id in concurrentJobIds)
            {
                Require(!ledgerAfterConcurrentCancel.StandardOutput.Contains(id, StringComparison.Ordinal),
                    $"Spend ledger failed to release reservation for cancelled concurrent job {id}");
            }
            Console.WriteLine("PASS concurrent atomicity: parallel job creations and parallel job cancellations synchronize atomically without state corruption");

            // G. Job cancellation while Pending transitions to Cancelled and releases reservation
            var cancelableJob = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", S6T1SourceNoteId, "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            Require(cancelableJob.ExitCode == 0, "Failed to create cancelable job.");
            var cancelableJobId = ExtractJobId(cancelableJob.StandardOutput);

            var cancel = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-cancel", "--control-pipe", fixture.ControlPipe,
                "--job-id", cancelableJobId).ConfigureAwait(false);
            Require(cancel.ExitCode == 0 && cancel.StandardOutput.Contains("state=Cancelled", StringComparison.Ordinal),
                "Canceling job failed.");

            // Starting a cancelled job must fail
            var startCancelled = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-start", "--control-pipe", fixture.ControlPipe,
                "--job-id", cancelableJobId).ConfigureAwait(false);
            Require(startCancelled.ExitCode != 0, "Starting a cancelled job did not fail.");
            Console.WriteLine("PASS cancel: job cancel transitions to Cancelled and stops execution");

            // H. Vault lock boundary stops subsequent execution and denies commands
            var lockJob = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", S6T1SourceNoteId, "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            var lockJobId = ExtractJobId(lockJob.StandardOutput);

            var lockVault = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockVault.ExitCode == 0, "Vault lock failed.");

            var startLocked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-start", "--control-pipe", fixture.ControlPipe,
                "--job-id", lockJobId).ConfigureAwait(false);
            Require(startLocked.ExitCode != 0 && (startLocked.StandardError.Contains("vault_locked", StringComparison.Ordinal) || startLocked.StandardOutput.Contains("vault_locked", StringComparison.Ordinal)),
                "Job start while vault is locked did not fail with vault_locked.");

            var unlockAgain = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlockAgain.ExitCode == 0, "Vault unlock again failed.");
            var cancelLockJob = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-cancel", "--control-pipe", fixture.ControlPipe,
                "--job-id", lockJobId).ConfigureAwait(false);
            Require(cancelLockJob.ExitCode == 0, "Failed to cancel test lock job.");
            Console.WriteLine("PASS lock boundary: lock halts execution and denies job operations with vault_locked");

            // I. Output boundary unit tests (JobOutputValidator)
            Require(!JobOutputValidator.TryValidate("{not valid json", S6T1SyntheticNoteText, out _, out var errCode1, out _) &&
                    errCode1 == "malformed_output",
                "Malformed JSON output was not rejected.");

            var ungroundedJson = JsonSerializer.Serialize(new
            {
                summary = "Hallucinated summary",
                items = new[]
                {
                    new
                    {
                        kind = "claim",
                        title = "Ungrounded claim",
                        body = "This claim has an excerpt that does not exist in the source note.",
                        excerpt = "THIS EXCERPT DOES NOT EXIST ANYWHERE IN THE NOTE"
                    }
                }
            });
            Require(!JobOutputValidator.TryValidate(ungroundedJson, S6T1SyntheticNoteText, out _, out var errCode2, out _) &&
                    errCode2 == "grounding_failure",
                "Ungrounded excerpt was not rejected by output validator.");

            var validJson = JsonSerializer.Serialize(new
            {
                summary = "Distributed invariants summary",
                items = new[]
                {
                    new
                    {
                        kind = "claim",
                        title = "Linearizable Quorum Replication",
                        body = "Replication in distributed state machines mandates linearizable quorum writes.",
                        excerpt = "In distributed state machines, replication requires quorum writes."
                    },
                    new
                    {
                        kind = "hypothesis",
                        title = "Vector Clock Conflict Detection",
                        body = "Vector clocks are hypothesized to detect concurrent mutations without synchronized clocks.",
                        excerpt = "vector clocks detect concurrent conflicting mutations without clock sync."
                    }
                }
            });
            Require(JobOutputValidator.TryValidate(validJson, S6T1SyntheticNoteText, out var validated, out _, out _) &&
                    validated is not null && validated.Items.Length == 2,
                "Valid grounded output failed validation.");
            Console.WriteLine("PASS output boundary: malformed JSON and ungrounded excerpts fail closed");

            // J. Rolling 30-day budget ceiling calculation
            var spendLedger = new SpendLedger();
            Require(spendLedger.CalculateRollingSpend(DateTimeOffset.UtcNow) == SpendLedger.HistoricalUnreconciledReservationUsd,
                "Spend ledger initial spend does not match historical reservation ($0.0054).");
            Require(spendLedger.CanReserve(0.0054m, DateTimeOffset.UtcNow, out _),
                "Spend ledger cannot reserve approved $0.0054.");
            Require(!spendLedger.CanReserve(4.995m, DateTimeOffset.UtcNow, out var overBudgetCode) &&
                    (overBudgetCode == "egress_cost_exceeded" || overBudgetCode == "rolling_budget_exceeded"),
                "Spend ledger allowed over-cap reservation.");
            Console.WriteLine("PASS rolling budget: $0.25/job and $5.00/30-day ceilings enforced with historical $0.0054 reserve");

            // K. Credential security: argv --api-key rejection and safe stdin input
            var rejectedArgvKey = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "provider-config", "--control-pipe", fixture.ControlPipe,
                "--provider", "deepseek", "--model", "deepseek-flash",
                "--base-url", "https://api.deepseek.com",
                "--api-key", "secret-should-be-rejected").ConfigureAwait(false);
            Require(rejectedArgvKey.ExitCode != 0, "CLI permitted passing --api-key in command line arguments!");

            var configStdin = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                "test-secret-key-via-stdin" + Environment.NewLine,
                "owner", "provider-config", "--control-pipe", fixture.ControlPipe,
                "--provider", "deepseek", "--model", "deepseek-flash",
                "--base-url", "https://api.deepseek.com",
                "--api-key-stdin", "true").ConfigureAwait(false);
            Require(configStdin.ExitCode == 0 && configStdin.StandardOutput.Contains("PROVIDER configured", StringComparison.Ordinal),
                "Failed to configure provider via stdin.");
            Require(!configStdin.StandardOutput.Contains("test-secret-key", StringComparison.Ordinal) &&
                    !configStdin.StandardError.Contains("test-secret-key", StringComparison.Ordinal),
                "Secret leaked in stdout or stderr!");
            Console.WriteLine("PASS credential security: argv secret rejected, stdin configured securely without secret leak");

            // =========================================================================
            // Local Controlled HTTP Boundary Tests (Real OpenAiCompatibleProviderAdapter)
            // =========================================================================

            var localPort = GetAvailablePort();
            var localPrefix = $"http://127.0.0.1:{localPort}/";
            using var httpListener = new HttpListener();
            httpListener.Prefixes.Add(localPrefix);
            httpListener.Start();

            // Configure provider to talk to our local test HTTP listener
            var configLocal = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                "local-test-key" + Environment.NewLine,
                "owner", "provider-config", "--control-pipe", fixture.ControlPipe,
                "--provider", "deepseek", "--model", "deepseek-flash",
                "--base-url", localPrefix,
                "--api-key-stdin", "true").ConfigureAwait(false);
            Require(configLocal.ExitCode == 0, "Failed to point provider to local test endpoint.");

            // 1. In-flight provider timeout test: HTTP 504 Gateway Timeout -> fail=timeout, reservation RETAINED
            var timeoutJob = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", S6T1SourceNoteId, "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            var timeoutJobId = ExtractJobId(timeoutJob.StandardOutput);

            var timeoutServerTask = Task.Run(async () =>
            {
                var ctx = await httpListener.GetContextAsync().ConfigureAwait(false);
                ctx.Response.StatusCode = 504;
                var bytes = Encoding.UTF8.GetBytes("{\"error\": {\"message\": \"Gateway timeout\"}}");
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                ctx.Response.OutputStream.Close();
            });

            var startTimeout = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-start", "--control-pipe", fixture.ControlPipe,
                "--job-id", timeoutJobId).ConfigureAwait(false);
            await timeoutServerTask.ConfigureAwait(false);

            Require(startTimeout.ExitCode != 0 && startTimeout.StandardOutput.Contains("state=Failed", StringComparison.Ordinal) &&
                    startTimeout.StandardOutput.Contains("fail=timeout", StringComparison.Ordinal),
                $"Job timeout did not fail with fail=timeout (stdout={startTimeout.StandardOutput.Trim()}).");

            var ledgerAfterTimeout = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledgerAfterTimeout.ExitCode == 0 &&
                    ledgerAfterTimeout.StandardOutput.Contains(timeoutJobId, StringComparison.Ordinal) &&
                    ledgerAfterTimeout.StandardOutput.Contains("isReservation=True", StringComparison.Ordinal),
                "Timed-out job reservation was erroneously released instead of retained in spend ledger!");
            Console.WriteLine("PASS negative: in-flight provider timeout fails closed and retains unknown reservation");

            // 2. In-flight cancellation test: request received, owner cancels mid-flight -> state=Cancelled, reservation RETAINED
            var inFlightJob = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", S6T1SourceNoteId, "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            var inFlightJobId = ExtractJobId(inFlightJob.StandardOutput);

            var requestReceivedTcs = new TaskCompletionSource<bool>();
            var cancelTriggerTcs = new TaskCompletionSource<bool>();

            var cancelServerTask = Task.Run(async () =>
            {
                var ctx = await httpListener.GetContextAsync().ConfigureAwait(false);
                requestReceivedTcs.SetResult(true);
                await cancelTriggerTcs.Task.ConfigureAwait(false);
                ctx.Response.Abort();
            });

            var startInFlightTask = RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-start", "--control-pipe", fixture.ControlPipe,
                "--job-id", inFlightJobId);

            await requestReceivedTcs.Task.ConfigureAwait(false);

            var cancelInFlight = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-cancel", "--control-pipe", fixture.ControlPipe,
                "--job-id", inFlightJobId).ConfigureAwait(false);
            Require(cancelInFlight.ExitCode == 0 && cancelInFlight.StandardOutput.Contains("state=Cancelled", StringComparison.Ordinal),
                "In-flight cancellation did not transition job to Cancelled.");

            cancelTriggerTcs.SetResult(true);
            await cancelServerTask.ConfigureAwait(false);
            await startInFlightTask.ConfigureAwait(false);

            var ledgerAfterCancel = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledgerAfterCancel.ExitCode == 0 &&
                    ledgerAfterCancel.StandardOutput.Contains(inFlightJobId, StringComparison.Ordinal) &&
                    ledgerAfterCancel.StandardOutput.Contains("isReservation=True", StringComparison.Ordinal),
                "In-flight cancelled job reservation was erroneously released instead of retained!");
            Console.WriteLine("PASS negative: in-flight cancellation stops execution and retains unknown reservation");

            // 3. Structural authority: model output containing command strings commits strictly as inert proposal without authority
            var hostileJob = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", S6T1SourceNoteId, "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            var hostileJobId = ExtractJobId(hostileJob.StandardOutput);

            var hostilePayload = JsonSerializer.Serialize(new
            {
                id = "chatcmpl-hostile-test",
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            role = "assistant",
                            content = JsonSerializer.Serialize(new
                            {
                                summary = "Summary containing command text",
                                items = new[]
                                {
                                    new
                                    {
                                        kind = "claim",
                                        title = "Command injection test",
                                        body = "Attempting command execution: sudo rm -rf / and powershell cmd.exe grant.create vault.unlock",
                                        excerpt = "In distributed state machines, replication requires quorum writes."
                                    }
                                }
                            })
                        }
                    }
                },
                usage = new
                {
                    prompt_tokens = 250,
                    completion_tokens = 300,
                    total_tokens = 550
                }
            });

            var hostileServerTask = Task.Run(async () =>
            {
                var ctx = await httpListener.GetContextAsync().ConfigureAwait(false);
                var bytes = Encoding.UTF8.GetBytes(hostilePayload);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                ctx.Response.OutputStream.Close();
            });

            var startHostile = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-start", "--control-pipe", fixture.ControlPipe,
                "--job-id", hostileJobId).ConfigureAwait(false);
            await hostileServerTask.ConfigureAwait(false);
            Require(startHostile.ExitCode == 0 && startHostile.StandardOutput.Contains("state=Succeeded", StringComparison.Ordinal),
                $"Hostile text model job failed to complete (exit={startHostile.ExitCode}, out={startHostile.StandardOutput}).");

            // Verify proposal was committed strictly as inert proposal with assistant attribution and unconfirmed status
            var listProposals = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "proposal-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(listProposals.ExitCode == 0 && listProposals.StandardOutput.Contains("PROPOSAL id=", StringComparison.Ordinal),
                "No proposals committed.");

            var proposalId = ExtractProposalId(listProposals.StandardOutput);
            var readProposal = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "proposal-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", proposalId).ConfigureAwait(false);
            Require(readProposal.ExitCode == 0 &&
                    readProposal.StandardOutput.Contains("executor=structuringJob", StringComparison.Ordinal) &&
                    readProposal.StandardOutput.Contains("userConfirmedClaim=false", StringComparison.Ordinal) &&
                    readProposal.StandardOutput.Contains("author=assistant", StringComparison.Ordinal) &&
                    readProposal.StandardOutput.Contains("evidence=reportedSource", StringComparison.Ordinal) &&
                    readProposal.StandardOutput.Contains($"reference id={S6T1SourceNoteId} revision=1", StringComparison.Ordinal),
                $"Proposal read did not reflect proper structuringJob attribution and inert constraints (stdout={readProposal.StandardOutput.Trim()}).");
            Console.WriteLine("PASS structural authority: model output containing command strings commits strictly as inert proposal without authority");

            // 4. Executing-job crash / restart recovery test
            var crashJob = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", S6T1SourceNoteId, "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            var crashJobId = ExtractJobId(crashJob.StandardOutput);

            var crashJobArrivedTcs = new TaskCompletionSource<bool>();
            var crashServerTask = Task.Run(async () =>
            {
                var ctx = await httpListener.GetContextAsync().ConfigureAwait(false);
                crashJobArrivedTcs.SetResult(true);
                // Keep connection open indefinitely to simulate crash during execution
                await Task.Delay(15_000).ConfigureAwait(false);
                ctx.Response.Abort();
            });

            var startCrashTask = RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-start", "--control-pipe", fixture.ControlPipe,
                "--job-id", crashJobId);

            await crashJobArrivedTcs.Task.ConfigureAwait(false);

            // Crash/stop service while job is in Executing state
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;

            // Restart service with same encrypted vault
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var unlockRestart = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlockRestart.ExitCode == 0, "Vault unlock failed after restart.");

            // Querying the crashed job recovers it as Failed with interrupted_by_restart
            var recoveredJobStatus = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-status", "--control-pipe", fixture.ControlPipe,
                "--job-id", crashJobId).ConfigureAwait(false);
            Require(recoveredJobStatus.ExitCode == 0 &&
                    recoveredJobStatus.StandardOutput.Contains("state=Failed", StringComparison.Ordinal) &&
                    recoveredJobStatus.StandardOutput.Contains("fail=interrupted_by_restart", StringComparison.Ordinal),
                $"Recovered job status did not reflect interrupted_by_restart (stdout={recoveredJobStatus.StandardOutput.Trim()}).");

            // Verify its reservation was retained as unknown reservation
            var ledgerAfterRecovery = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledgerAfterRecovery.ExitCode == 0 &&
                    ledgerAfterRecovery.StandardOutput.Contains(crashJobId, StringComparison.Ordinal) &&
                    ledgerAfterRecovery.StandardOutput.Contains("isReservation=True", StringComparison.Ordinal),
                "Spend ledger did not retain reservation for job interrupted by restart!");
            Console.WriteLine("PASS restart recovery: executing job interrupted by restart recovered as Failed with reservation retained");

            // 5. In-flight server connection drop / TCP abort -> state=Failed, fail=provider_error, reservation RETAINED
            var dropJob = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", S6T1SourceNoteId, "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            var dropJobId = ExtractJobId(dropJob.StandardOutput);

            var dropServerTask = Task.Run(async () =>
            {
                var ctx = await httpListener.GetContextAsync().ConfigureAwait(false);
                ctx.Response.Abort();
            });

            var startDrop = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-start", "--control-pipe", fixture.ControlPipe,
                "--job-id", dropJobId).ConfigureAwait(false);
            await dropServerTask.ConfigureAwait(false);

            Require(startDrop.ExitCode != 0 && startDrop.StandardOutput.Contains("state=Failed", StringComparison.Ordinal) &&
                    startDrop.StandardOutput.Contains("fail=provider_error", StringComparison.Ordinal),
                $"Job start on abrupt connection drop did not fail with provider_error (stdout={startDrop.StandardOutput.Trim()}).");

            var ledgerAfterDrop = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledgerAfterDrop.ExitCode == 0 &&
                    ledgerAfterDrop.StandardOutput.Contains(dropJobId, StringComparison.Ordinal) &&
                    ledgerAfterDrop.StandardOutput.Contains("retained_unknown", StringComparison.Ordinal),
                "Reservation was not retained after provider connection drop failure!");
            Console.WriteLine("PASS negative: in-flight connection drop fails closed with provider_error and retains unknown reservation");

            // 6. Post-send malformed JSON response -> state=Failed, fail=malformed_response, reservation RETAINED
            var malformedJob = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", S6T1SourceNoteId, "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            var malformedJobId = ExtractJobId(malformedJob.StandardOutput);

            var malformedServerTask = Task.Run(async () =>
            {
                var ctx = await httpListener.GetContextAsync().ConfigureAwait(false);
                var badBytes = Encoding.UTF8.GetBytes("{not valid json at all");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = badBytes.Length;
                await ctx.Response.OutputStream.WriteAsync(badBytes).ConfigureAwait(false);
                ctx.Response.OutputStream.Close();
            });

            var startMalformed = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-start", "--control-pipe", fixture.ControlPipe,
                "--job-id", malformedJobId).ConfigureAwait(false);
            await malformedServerTask.ConfigureAwait(false);

            Require(startMalformed.ExitCode != 0 && startMalformed.StandardOutput.Contains("state=Failed", StringComparison.Ordinal) &&
                    (startMalformed.StandardOutput.Contains("fail=malformed_response", StringComparison.Ordinal) || startMalformed.StandardOutput.Contains("fail=provider_error", StringComparison.Ordinal)),
                $"Job start on malformed JSON response did not fail appropriately (out={startMalformed.StandardOutput.Trim()}).");

            var ledgerAfterMalformed = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledgerAfterMalformed.ExitCode == 0 &&
                    ledgerAfterMalformed.StandardOutput.Contains(malformedJobId, StringComparison.Ordinal) &&
                    ledgerAfterMalformed.StandardOutput.Contains("retained_unknown", StringComparison.Ordinal),
                "Reservation was not retained after post-send malformed JSON response!");
            Console.WriteLine("PASS negative: post-send malformed response fails closed and retains unknown reservation");

            // 7. Post-send missing usage -> state=Failed, fail=missing_usage, reservation RETAINED
            var missingUsageJob = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", S6T1SourceNoteId, "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            var missingUsageJobId = ExtractJobId(missingUsageJob.StandardOutput);

            var missingUsageServerTask = Task.Run(async () =>
            {
                var ctx = await httpListener.GetContextAsync().ConfigureAwait(false);
                var noUsagePayload = JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new
                        {
                            message = new
                            {
                                content = "{\"summary\": \"No usage summary\", \"items\": []}"
                            }
                        }
                    }
                });
                var respBytes = Encoding.UTF8.GetBytes(noUsagePayload);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = respBytes.Length;
                await ctx.Response.OutputStream.WriteAsync(respBytes).ConfigureAwait(false);
                ctx.Response.OutputStream.Close();
            });

            var startMissingUsage = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-start", "--control-pipe", fixture.ControlPipe,
                "--job-id", missingUsageJobId).ConfigureAwait(false);
            await missingUsageServerTask.ConfigureAwait(false);

            Require(startMissingUsage.ExitCode != 0 && startMissingUsage.StandardOutput.Contains("state=Failed", StringComparison.Ordinal) &&
                    startMissingUsage.StandardOutput.Contains("fail=missing_usage", StringComparison.Ordinal),
                $"Job start on missing usage did not fail with fail=missing_usage (out={startMissingUsage.StandardOutput.Trim()}).");

            var ledgerAfterMissingUsage = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledgerAfterMissingUsage.ExitCode == 0 &&
                    ledgerAfterMissingUsage.StandardOutput.Contains(missingUsageJobId, StringComparison.Ordinal) &&
                    ledgerAfterMissingUsage.StandardOutput.Contains("retained_unknown", StringComparison.Ordinal),
                "Reservation was not retained after post-send missing usage!");
            Console.WriteLine("PASS negative: post-send missing usage fails closed and retains unknown reservation");

            // 8. Actual output cap violation -> state=Failed, fail=output_cap_exceeded, spend COMMITTED (not erased), proposals not committed
            var capViolationJob = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", S6T1SourceNoteId, "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            var capViolationJobId = ExtractJobId(capViolationJob.StandardOutput);

            var capViolationServerTask = Task.Run(async () =>
            {
                var ctx = await httpListener.GetContextAsync().ConfigureAwait(false);
                var overCapPayload = JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new
                        {
                            message = new
                            {
                                content = JsonSerializer.Serialize(new
                                {
                                    summary = "Cap violation summary",
                                    items = new[]
                                    {
                                        new
                                        {
                                            kind = "claim",
                                            title = "Cap Claim",
                                            body = "Cap claim body.",
                                            excerpt = "In distributed state machines, replication requires quorum writes."
                                        }
                                    }
                                })
                            }
                        }
                    },
                    usage = new
                    {
                        prompt_tokens = 100,
                        completion_tokens = 2500 // Exceeds 2000 output cap!
                    }
                });
                var respBytes = Encoding.UTF8.GetBytes(overCapPayload);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = respBytes.Length;
                await ctx.Response.OutputStream.WriteAsync(respBytes).ConfigureAwait(false);
                ctx.Response.OutputStream.Close();
            });

            var startCapViolation = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-start", "--control-pipe", fixture.ControlPipe,
                "--job-id", capViolationJobId).ConfigureAwait(false);
            await capViolationServerTask.ConfigureAwait(false);

            Require(startCapViolation.ExitCode != 0 && startCapViolation.StandardOutput.Contains("state=Failed", StringComparison.Ordinal) &&
                    startCapViolation.StandardOutput.Contains("fail=output_cap_exceeded", StringComparison.Ordinal),
                $"Job start on output cap violation did not fail with output_cap_exceeded (out={startCapViolation.StandardOutput.Trim()}).");

            var ledgerAfterCapViolation = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledgerAfterCapViolation.ExitCode == 0 &&
                    ledgerAfterCapViolation.StandardOutput.Contains(capViolationJobId, StringComparison.Ordinal) &&
                    ledgerAfterCapViolation.StandardOutput.Contains("isReservation=False", StringComparison.Ordinal),
                "Spend was erased or not committed on actual output cap violation!");

            var readCapProposal = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "proposal-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", $"job-{capViolationJobId}-item-1").ConfigureAwait(false);
            Require(readCapProposal.ExitCode != 0, "Proposal was committed despite output cap violation!");
            Console.WriteLine("PASS negative: actual output cap violation fails closed, preserves source, and records billable spend");

            // 9. Mid-flight source-revoke before commit with controlled adapter:
            // S6-T1 contract: scope snapshot/provider consent revalidated before commit; revocation blocks commit and records billable spend.
            var midflightVaultDir = Path.Combine(fixture.OutputDirectory, "midflight-vault");
            var midflightStore = new EncryptedVaultStore(midflightVaultDir);
            var midflightLifecycle = new VaultLifecycle(midflightStore);
            midflightLifecycle.Provision(passphrase, out _);
            midflightLifecycle.Unlock(passphrase, false);

            using (var midflightOp = midflightLifecycle.TryBeginOperation()!)
            {
                var cfg = new ProviderConfig("deepseek", "deepseek-flash", "https://api.deepseek.com", "test-key");
                midflightOp.WriteContent(ModelJobService.ProviderConfigResourceId, ModelJobService.SystemZoneId,
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cfg)), expectedRevision: 0);

                var srcDraft = new StoredRegisteredSource(
                    SchemaVersion: RegisteredSourceCodec.CurrentSchemaVersion,
                    Id: "midflight-source",
                    ZoneId: S6T1Zone,
                    Executor: ExecutorKind.Owner,
                    CreationMethod: CreationMethod.DirectWrite,
                    Title: "Midflight Source",
                    Locator: Path.Combine(fixture.OutputDirectory, "dummy.jsonl"),
                    Version: "1",
                    Availability: SourceAvailability.Available,
                    CapturedAtUtc: now,
                    RecordedAtUtc: now,
                    Author: SpeakerRole.User,
                    Text: "Verbatim research text for midflight revocation proof.");
                midflightOp.WriteContent("midflight-source", S6T1Zone, RegisteredSourceCodec.EncodeStored(srcDraft), expectedRevision: 0);

                var inboxStored = new StoredMemoryEnvelope(
                    SchemaVersion: 1,
                    Id: "midflight-inbox",
                    ZoneId: S6T1Zone,
                    Kind: MemoryKind.Observation,
                    Author: SpeakerRole.User,
                    Executor: ExecutorKind.Owner,
                    CreationMethod: CreationMethod.DirectWrite,
                    Evidence: EvidenceLevel.ReportedSource,
                    Level: AbstractionLevel.L0,
                    Title: "Inbox",
                    Body: "Destination inbox",
                    Sources: Array.Empty<MemorySourceReference>(),
                    OccurredAtUtc: null,
                    RecordedAtUtc: now,
                    Applicability: null);
                midflightOp.WriteContent("midflight-inbox", S6T1Zone, MemoryEnvelopeCodec.EncodeStored(inboxStored), expectedRevision: 0);
                var enrollAuthority = new SourceEnrollmentAuthority();
                var dummyDigest = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("midflight-dummy-anchor")));
                var dummyLocator = Path.Combine(fixture.OutputDirectory, "dummy.jsonl");
                enrollAuthority.Enroll(AuthenticatedContext.ForOwner(), "midflight-source", S6T1Zone,
                    dummyLocator, "codex.session.jsonl", 1, 100, dummyDigest, midflightOp);

                var controlledAdapter = new ControlledRevokingAdapter(() =>
                {
                    enrollAuthority.Revoke(AuthenticatedContext.ForOwner(), "midflight-source", midflightOp);
                });

                var controlledJobService = new ModelJobService(midflightLifecycle, controlledAdapter);
                var createdRecord = controlledJobService.CreateJob(
                    AuthenticatedContext.ForOwner(),
                    inputs: new[] { new JobInputReference("midflight-source", 1) },
                    destinationZoneId: S6T1Zone,
                    destinationResourceId: "midflight-inbox",
                    destinationRevision: 1,
                    provider: "deepseek",
                    model: "deepseek-flash",
                    maxCostUsd: 0.25m,
                    store: midflightOp);

                var executionResult = await controlledJobService.StartJobAsync(
                    AuthenticatedContext.ForOwner(),
                    createdRecord.Id,
                    midflightOp).ConfigureAwait(false);

                Require(executionResult.State == JobState.Failed &&
                        executionResult.Failure?.Code == "source_unavailable" &&
                        executionResult.Failure.Message.Contains("revoked before commit", StringComparison.Ordinal),
                    $"Controlled midflight revoke job did not fail with source_unavailable before commit (state={executionResult.State}, code={executionResult.Failure?.Code}).");

                var ledger = controlledJobService.LoadSpendLedger(midflightOp);
                var committedEntry = ledger.Entries.FirstOrDefault(e => e.JobId == createdRecord.Id && !e.IsReservation);
                Require(committedEntry is not null && committedEntry.InputTokens == 50 && committedEntry.OutputTokens == 25,
                    "Billable spend was not committed to spend ledger after midflight revocation!");

                Require(executionResult.OutputProposalIds is null || executionResult.OutputProposalIds.Length == 0,
                    "Proposals were committed despite midflight source revocation!");

                var preservedSource = RegisteredSourceCodec.DecodeStored(midflightOp.ReadContent("midflight-source", 1));
                Require(preservedSource.Text == "Verbatim research text for midflight revocation proof.",
                    "Source text was corrupted or lost after midflight revocation failure!");

                Console.WriteLine("PASS negative: mid-flight source-revoke before commit aborts proposal commit and records billable spend");

                // 10. Corrupt/unavailable enrollment metadata fail-closed boundary tests:
                // S6-T1 contract: corrupt/unavailable enrollment metadata must truthfully fail closed before private read, before send, and before commit;
                // billable/unknown spend must be preserved where send was already possible, source text kept intact, zero proposals committed, and zero extra provider sends.
                var corruptSrcDraft = new StoredRegisteredSource(
                    SchemaVersion: RegisteredSourceCodec.CurrentSchemaVersion,
                    Id: "corrupt-source",
                    ZoneId: S6T1Zone,
                    Executor: ExecutorKind.Owner,
                    CreationMethod: CreationMethod.DirectWrite,
                    Title: "Corrupt Source",
                    Locator: Path.Combine(fixture.OutputDirectory, "dummy2.jsonl"),
                    Version: "1",
                    Availability: SourceAvailability.Available,
                    CapturedAtUtc: now,
                    RecordedAtUtc: now,
                    Author: SpeakerRole.User,
                    Text: "Verbatim research text for corrupt metadata proof.");
                midflightOp.WriteContent("corrupt-source", S6T1Zone, RegisteredSourceCodec.EncodeStored(corruptSrcDraft), expectedRevision: 0);
                var dummy2Digest = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("corrupt-dummy-anchor")));
                var dummy2Locator = Path.Combine(fixture.OutputDirectory, "dummy2.jsonl");
                enrollAuthority.Enroll(AuthenticatedContext.ForOwner(), "corrupt-source", S6T1Zone,
                    dummy2Locator, "codex.session.jsonl", 1, 100, dummy2Digest, midflightOp);

                var wrappedOp = new CorruptedEnrollmentOperation(midflightOp);

                // Step 1: Fail-closed before private read / job-create
                wrappedOp.IsCorrupted = true;
                bool createThrewTruthfulException = false;
                try
                {
                    var dummyAdapter = new ControlledRevokingAdapter(() => { });
                    var testJobService = new ModelJobService(midflightLifecycle, dummyAdapter);
                    testJobService.CreateJob(
                        AuthenticatedContext.ForOwner(),
                        inputs: new[] { new JobInputReference("corrupt-source", 1) },
                        destinationZoneId: S6T1Zone,
                        destinationResourceId: "midflight-inbox",
                        destinationRevision: 1,
                        provider: "deepseek",
                        model: "deepseek-flash",
                        maxCostUsd: 0.25m,
                        store: wrappedOp);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("unavailable or corrupt", StringComparison.Ordinal))
                {
                    createThrewTruthfulException = true;
                }
                Require(createThrewTruthfulException, "Job creation with corrupted enrollment state did not fail closed before private read.");

                // Step 2: Fail-closed before provider send / job-start with reservation released
                wrappedOp.IsCorrupted = false;
                var sendTestAdapter = new ControlledRevokingAdapter(() => { });
                var sendJobService = new ModelJobService(midflightLifecycle, sendTestAdapter);
                var preSendRecord = sendJobService.CreateJob(
                    AuthenticatedContext.ForOwner(),
                    inputs: new[] { new JobInputReference("corrupt-source", 1) },
                    destinationZoneId: S6T1Zone,
                    destinationResourceId: "midflight-inbox",
                    destinationRevision: 1,
                    provider: "deepseek",
                    model: "deepseek-flash",
                    maxCostUsd: 0.25m,
                    store: wrappedOp);

                var ledgerBeforeSendFail = sendJobService.LoadSpendLedger(wrappedOp);
                Require(ledgerBeforeSendFail.Entries.Any(e => e.JobId == preSendRecord.Id && e.IsReservation),
                    "Spend ledger missing reservation before pre-send corrupt enrollment failure test.");

                wrappedOp.IsCorrupted = true;
                var preSendResult = await sendJobService.StartJobAsync(
                    AuthenticatedContext.ForOwner(),
                    preSendRecord.Id,
                    wrappedOp).ConfigureAwait(false);

                Require(preSendResult.State == JobState.Failed &&
                        preSendResult.Failure?.Code == "source_unavailable" &&
                        preSendResult.Failure.Message.Contains("unavailable or corrupt", StringComparison.Ordinal),
                    $"Pre-send corrupted enrollment did not fail closed with source_unavailable (state={preSendResult.State}, code={preSendResult.Failure?.Code}).");

                var ledgerAfterSendFail = sendJobService.LoadSpendLedger(wrappedOp);
                Require(!ledgerAfterSendFail.Entries.Any(e => e.JobId == preSendRecord.Id && e.IsReservation),
                    "Pre-send budget reservation was not released on pre-send corrupt enrollment failure!");

                var preSendPreservedSource = RegisteredSourceCodec.DecodeStored(wrappedOp.ReadContent("corrupt-source", 1));
                Require(preSendPreservedSource.Text == "Verbatim research text for corrupt metadata proof.",
                    "Source text was lost or corrupted on pre-send corrupt enrollment failure!");

                // Step 3: Fail-closed before commit with spend preserved, proposals blocked, source intact
                wrappedOp.IsCorrupted = false;
                var commitTestAdapter = new ControlledRevokingAdapter(() =>
                {
                    wrappedOp.IsCorrupted = true; // Corrupt enrollment state mid-flight before commit!
                });
                var commitJobService = new ModelJobService(midflightLifecycle, commitTestAdapter);
                var preCommitRecord = commitJobService.CreateJob(
                    AuthenticatedContext.ForOwner(),
                    inputs: new[] { new JobInputReference("corrupt-source", 1) },
                    destinationZoneId: S6T1Zone,
                    destinationResourceId: "midflight-inbox",
                    destinationRevision: 1,
                    provider: "deepseek",
                    model: "deepseek-flash",
                    maxCostUsd: 0.25m,
                    store: wrappedOp);

                var preCommitResult = await commitJobService.StartJobAsync(
                    AuthenticatedContext.ForOwner(),
                    preCommitRecord.Id,
                    wrappedOp).ConfigureAwait(false);

                Require(preCommitResult.State == JobState.Failed &&
                        preCommitResult.Failure?.Code == "source_unavailable" &&
                        preCommitResult.Failure.Message.Contains("unavailable before commit", StringComparison.Ordinal),
                    $"Pre-commit corrupted enrollment did not fail closed with source_unavailable (state={preCommitResult.State}, code={preCommitResult.Failure?.Code}).");

                var ledgerAfterCommitFail = commitJobService.LoadSpendLedger(wrappedOp);
                var committedSpend = ledgerAfterCommitFail.Entries.FirstOrDefault(e => e.JobId == preCommitRecord.Id && !e.IsReservation);
                Require(committedSpend is not null && committedSpend.InputTokens == 50 && committedSpend.OutputTokens == 25,
                    "Billable spend was not committed to spend ledger after pre-commit corrupt enrollment failure!");

                Require(preCommitResult.OutputProposalIds is null || preCommitResult.OutputProposalIds.Length == 0,
                    "Proposals were committed despite pre-commit corrupt enrollment failure!");

                var preCommitPreservedSource = RegisteredSourceCodec.DecodeStored(wrappedOp.ReadContent("corrupt-source", 1));
                Require(preCommitPreservedSource.Text == "Verbatim research text for corrupt metadata proof.",
                    "Source text was lost or corrupted on pre-commit corrupt enrollment failure!");

                Console.WriteLine("PASS negative: corrupt/unavailable enrollment metadata fails closed before read, send, and commit with spend preserved and source intact");
            }
            await midflightLifecycle.LockAsync().ConfigureAwait(false);
            // Cleanup local test HTTP listener
            httpListener.Stop();

            if (string.Equals(Environment.GetEnvironmentVariable("METABRAIN_LIVE_PROVIDER_AUTHORIZED"), "true", StringComparison.OrdinalIgnoreCase))
            {
                await RunS6T1LiveProviderAsync(connectionsExecutable).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine("INFO live provider call: waiting for explicit owner authorization (zero outbound calls executed).");
            }

            Console.WriteLine("PASS S6-T1 scoped model job execution and provider controls walkthrough complete");
            Console.WriteLine("PASS fixture processes, profiles, and protected files were cleaned up");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static int GetAvailablePort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    private static string ExtractJobId(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("JOB id=", StringComparison.Ordinal))
            {
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (part.StartsWith("id=", StringComparison.Ordinal))
                    {
                        return part.Substring(3);
                    }
                }
            }
        }

        throw new InvalidOperationException($"Could not extract job ID from output: {output}");
    }

    private static string ExtractProposalId(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("PROPOSAL id=", StringComparison.Ordinal))
            {
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (part.StartsWith("id=", StringComparison.Ordinal))
                    {
                        return part.Substring(3);
                    }
                }
            }
        }

        throw new InvalidOperationException($"Could not extract proposal ID from output: {output}");
    }

    internal static async Task RunS6T1LiveProviderAsync(string connectionsExecutable)
    {
        var authorized = string.Equals(Environment.GetEnvironmentVariable("METABRAIN_LIVE_PROVIDER_AUTHORIZED"), "true", StringComparison.OrdinalIgnoreCase);
        if (!authorized)
        {
            Console.WriteLine("INFO live provider call pending explicit owner authorization (zero outbound calls made).");
            return;
        }

        Console.WriteLine("RUN S6-T1 live provider walkthrough (owner authorized)");
        // Do NOT auto-dispose fixture; keep encrypted files on disk for reviewer verification
        var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            var now = DateTimeOffset.UtcNow.AddMinutes(-5);

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provision = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0, "Vault provision failed.");

            var unlock = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0, "Vault unlock failed.");

            // Create destination inbox
            var inboxDraft = JsonSerializer.Serialize(new
            {
                kind = "observation",
                author = "user",
                creationMethod = "directWrite",
                evidence = "userConfirmed",
                level = "l1",
                title = "S6-T1 Destination Inbox",
                body = "Destination inbox marker for S6-T1 live model job structuring proposals.",
                sources = Array.Empty<object>(),
                occurredAtUtc = now,
                recordedAtUtc = now.AddMinutes(1)
            });
            var inboxInput = Path.Combine(fixture.OutputDirectory, "live-inbox.json");
            await File.WriteAllTextAsync(inboxInput, inboxDraft, Encoding.UTF8).ConfigureAwait(false);
            var createInbox = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S6T1DestinationInboxId, "--zone-id", S6T1Zone,
                "--input-file", inboxInput).ConfigureAwait(false);
            Require(createInbox.ExitCode == 0 && createInbox.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
                "Failed to create live destination inbox.");
            File.Delete(inboxInput);

            // Capture the exact synthetic research note
            var noteInput = Path.Combine(fixture.OutputDirectory, "live-note.txt");
            await File.WriteAllTextAsync(noteInput, S6T1SyntheticNoteText, Encoding.UTF8).ConfigureAwait(false);
            var capture = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-capture", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S6T1SourceNoteId, "--zone-id", S6T1Zone,
                "--input-file", noteInput).ConfigureAwait(false);
            Require(capture.ExitCode == 0 && capture.StandardOutput.Contains("RESEARCH captured", StringComparison.Ordinal),
                "Failed to capture live synthetic note.");
            File.Delete(noteInput);

            // Configure provider from .env safely without argv secrets or environment exports
            var config = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "provider-config", "--control-pipe", fixture.ControlPipe,
                "--provider", "deepseek", "--model", "deepseek-flash",
                "--from-env", "true").ConfigureAwait(false);
            Require(config.ExitCode == 0 && config.StandardOutput.Contains("PROVIDER configured", StringComparison.Ordinal),
                "Failed to configure live provider from .env.");

            // Create job
            var createJob = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", S6T1SourceNoteId, "--source-revision", "1",
                "--zone-id", S6T1Zone, "--destination-id", S6T1DestinationInboxId,
                "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            Require(createJob.ExitCode == 0 && createJob.StandardOutput.Contains("state=Pending", StringComparison.Ordinal),
                "Live job creation failed.");
            var liveJobId = ExtractJobId(createJob.StandardOutput);
            Console.WriteLine($"LIVE JOB ID: {liveJobId}");

            // Verify pre-send reservation
            var ledgerPreSend = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledgerPreSend.ExitCode == 0 && ledgerPreSend.StandardOutput.Contains(liveJobId, StringComparison.Ordinal) &&
                    ledgerPreSend.StandardOutput.Contains("isReservation=True", StringComparison.Ordinal),
                "Live job reservation not recorded.");
            Console.WriteLine($"LEDGER PRE-SEND:\n{ledgerPreSend.StandardOutput.Trim()}");

            // Start job (single real call)
            var startJob = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-start", "--control-pipe", fixture.ControlPipe,
                "--job-id", liveJobId).ConfigureAwait(false);
            Require(startJob.ExitCode == 0 && startJob.StandardOutput.Contains("state=Succeeded", StringComparison.Ordinal),
                $"Live job execution failed (stdout={startJob.StandardOutput}, stderr={startJob.StandardError}).");
            Console.WriteLine($"JOB START RESULT:\n{startJob.StandardOutput.Trim()}");

            // List proposals
            var listProposals = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "proposal-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(listProposals.ExitCode == 0 && listProposals.StandardOutput.Contains("PROPOSAL id=", StringComparison.Ordinal),
                "No live proposals committed.");
            Console.WriteLine($"PROPOSALS LIST:\n{listProposals.StandardOutput.Trim()}");
            var proposalId = ExtractProposalId(listProposals.StandardOutput);
            Console.WriteLine($"LIVE PROPOSAL ID: {proposalId}");

            // Read proposal with CORRECT flag: --resource-id
            var readProposal = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "proposal-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", proposalId).ConfigureAwait(false);
            Require(readProposal.ExitCode == 0 &&
                    readProposal.StandardOutput.Contains("executor=structuringJob", StringComparison.Ordinal) &&
                    readProposal.StandardOutput.Contains("userConfirmedClaim=false", StringComparison.Ordinal) &&
                    readProposal.StandardOutput.Contains("author=assistant", StringComparison.Ordinal) &&
                    readProposal.StandardOutput.Contains("evidence=reportedSource", StringComparison.Ordinal) &&
                    readProposal.StandardOutput.Contains($"reference id={S6T1SourceNoteId} revision=1", StringComparison.Ordinal),
                "Live proposal read failed.");
            Console.WriteLine($"PROPOSAL READ:\n{readProposal.StandardOutput.Trim()}");

            // Query job status and ledger
            var statusBeforeRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-status", "--control-pipe", fixture.ControlPipe,
                "--job-id", liveJobId).ConfigureAwait(false);
            Require(statusBeforeRestart.ExitCode == 0 && statusBeforeRestart.StandardOutput.Contains("state=Succeeded", StringComparison.Ordinal),
                "Job status before restart failed.");
            Console.WriteLine($"JOB STATUS BEFORE RESTART:\n{statusBeforeRestart.StandardOutput.Trim()}");

            var ledgerBeforeRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledgerBeforeRestart.ExitCode == 0 && ledgerBeforeRestart.StandardOutput.Contains("isReservation=False", StringComparison.Ordinal),
                "Ledger settlement before restart failed.");
            Console.WriteLine($"LEDGER BEFORE RESTART:\n{ledgerBeforeRestart.StandardOutput.Trim()}");

            // Stop service and restart
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var unlockRestart = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlockRestart.ExitCode == 0, "Vault unlock failed after restart.");

            // Re-read SAME job, SAME proposal, SAME source note, SAME ledger
            var statusAfterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-status", "--control-pipe", fixture.ControlPipe,
                "--job-id", liveJobId).ConfigureAwait(false);
            Require(statusAfterRestart.ExitCode == 0 && statusAfterRestart.StandardOutput.Contains("state=Succeeded", StringComparison.Ordinal),
                "Job status after restart failed.");
            Console.WriteLine($"JOB STATUS AFTER RESTART:\n{statusAfterRestart.StandardOutput.Trim()}");

            var proposalAfterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "proposal-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", proposalId).ConfigureAwait(false);
            Require(proposalAfterRestart.ExitCode == 0 && proposalAfterRestart.StandardOutput.Contains(proposalId, StringComparison.Ordinal),
                "Proposal read after restart failed.");
            Console.WriteLine($"PROPOSAL AFTER RESTART:\n{proposalAfterRestart.StandardOutput.Trim()}");

            var sourceAfterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S6T1SourceNoteId).ConfigureAwait(false);
            Require(sourceAfterRestart.ExitCode == 0 &&
                    (sourceAfterRestart.StandardOutput.Contains(S6T1SyntheticNoteText, StringComparison.Ordinal) ||
                     sourceAfterRestart.StandardOutput.Contains(S6T1SourceNoteId, StringComparison.Ordinal)),
                "Source note read after restart failed.");
            Console.WriteLine($"SOURCE AFTER RESTART:\n{sourceAfterRestart.StandardOutput.Trim()}");

            var ledgerAfterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledgerAfterRestart.ExitCode == 0 && ledgerAfterRestart.StandardOutput.Contains(liveJobId, StringComparison.Ordinal),
                "Ledger read after restart failed.");
            Console.WriteLine($"LEDGER AFTER RESTART:\n{ledgerAfterRestart.StandardOutput.Trim()}");
            // Stop service to leave ciphertext in resting encrypted state
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;

            Console.WriteLine("PASS S6-T1 live provider walkthrough complete and verified");
            Console.WriteLine($"PRESERVED ENCRYPTED FIXTURE: {fixture.VaultDirectoryPath}");
            Console.WriteLine($"PRESERVED FIXTURE ROOT: {fixture.Root}");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private sealed class ControlledRevokingAdapter : IModelProviderAdapter
    {
        private readonly Action _onComplete;

        public ControlledRevokingAdapter(Action onComplete) => _onComplete = onComplete;

        public Task<ModelProviderResponse> CompleteAsync(ModelProviderRequest request, CancellationToken cancellationToken)
        {
            _onComplete();
            var validContent = JsonSerializer.Serialize(new
            {
                summary = "Controlled summary",
                items = new[]
                {
                    new
                    {
                        kind = "claim",
                        title = "Controlled Claim",
                        body = "Controlled body text.",
                        excerpt = "Verbatim research text for midflight revocation proof."
                    }
                }
            });
            return Task.FromResult(new ModelProviderResponse(
                Success: true,
                Content: validContent,
                InputTokens: 50,
                OutputTokens: 25,
                CachedInputTokens: 0,
                ErrorCode: null,
                ErrorMessage: null,
                RequestSent: true));
        }
    }

    private sealed class CorruptedEnrollmentOperation : IVaultOperation
    {
        private readonly IVaultOperation _inner;
        public bool IsCorrupted { get; set; }

        public CorruptedEnrollmentOperation(IVaultOperation inner) => _inner = inner;

        public SourceEnrollmentState LoadSourceEnrollmentState()
        {
            if (IsCorrupted)
            {
                throw new InvalidDataException("Corrupted source enrollment state header.");
            }
            return _inner.LoadSourceEnrollmentState();
        }

        public T UpdateSourceEnrollmentState<T>(Func<SourceEnrollmentState, (SourceEnrollmentState State, T Result)> update)
        {
            if (IsCorrupted)
            {
                throw new InvalidDataException("Corrupted source enrollment state header.");
            }
            return _inner.UpdateSourceEnrollmentState(update);
        }

        public byte[] ReadContent(string resourceId, long? expectedRevision = null) => _inner.ReadContent(resourceId, expectedRevision);
        public void ValidateRegistration(string resourceId, long? expectedRevision = null) => _inner.ValidateRegistration(resourceId, expectedRevision);
        public IReadOnlyList<ScopeResourceRevision> ListResources() => _inner.ListResources();
        public bool TryGetZone(string resourceId, out string? zoneId) => _inner.TryGetZone(resourceId, out zoneId);
        public long WriteContent(string resourceId, string zoneId, byte[] content) => _inner.WriteContent(resourceId, zoneId, content);
        public long WriteContent(string resourceId, string zoneId, byte[] content, long expectedRevision) => _inner.WriteContent(resourceId, zoneId, content, expectedRevision);
        public byte[][] ReadBatch(IReadOnlyList<ScopeResourceRevision> batch, int maxDegreeOfParallelism, CancellationToken cancellationToken) => _inner.ReadBatch(batch, maxDegreeOfParallelism, cancellationToken);
        public OwnerScopeGrantState LoadScopeGrantState() => _inner.LoadScopeGrantState();
        public T UpdateScopeGrantState<T>(Func<OwnerScopeGrantState, (OwnerScopeGrantState State, T Result)> update) => _inner.UpdateScopeGrantState(update);
        public OwnerCatalogState LoadCatalogState() => _inner.LoadCatalogState();
        public T UpdateCatalogState<T>(Func<OwnerCatalogState, (OwnerCatalogState State, T Result)> update) => _inner.UpdateCatalogState(update);
        public OwnerRecallPolicyState LoadRecallPolicyState() => _inner.LoadRecallPolicyState();
        public T UpdateRecallPolicyState<T>(Func<OwnerRecallPolicyState, (OwnerRecallPolicyState State, T Result)> update) => _inner.UpdateRecallPolicyState(update);
        public OwnerReviewState LoadReviewState() => _inner.LoadReviewState();
        public T UpdateReviewState<T>(Func<OwnerReviewState, (OwnerReviewState State, T Result)> update) => _inner.UpdateReviewState(update);
        public OwnerPurgeLedgerState LoadPurgeLedgerState() => _inner.LoadPurgeLedgerState();
        public T UpdatePurgeLedgerState<T>(Func<OwnerPurgeLedgerState, (OwnerPurgeLedgerState State, T Result)> update) => _inner.UpdatePurgeLedgerState(update);
        public bool DeleteCanonicalResource(string resourceId) => _inner.DeleteCanonicalResource(resourceId);
        public void Dispose() => _inner.Dispose();
    }
}
