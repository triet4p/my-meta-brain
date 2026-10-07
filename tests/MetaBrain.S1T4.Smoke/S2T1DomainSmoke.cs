using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    internal static async Task RunS2T1DomainEnvelopeAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S2-T1 domain envelope smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S2-T1 domain envelope and zone/resource semantics");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        Exception? failure = null;
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false))
                    .Contains("vault=uninitialized", StringComparison.Ordinal),
                "A service with no vault did not report the uninitialized state for S2-T1.");

            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "Provision did not create the S2-T1 synthetic vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");

            var now = DateTimeOffset.UtcNow.AddMinutes(-2);
            var questionDraft = JsonSerializer.Serialize(new
            {
                kind = "question",
                author = "user",
                creationMethod = "directWrite",
                evidence = "unverified",
                level = "l1",
                title = "Why does staging reject this payload?",
                body = "Synthetic S2-T1 user question; never a real owner secret.",
                sources = Array.Empty<object>(),
                occurredAtUtc = now,
                recordedAtUtc = now.AddMinutes(1),
                applicability = new { project = "synthetic-app", environment = "staging", version = "0.0.0-s2t1" }
            });
            var questionId = "s2t1-question-alpha";
            var questionZone = "s2t1-private-zone";
            await WriteDraftAsync(connectionsExecutable, fixture, questionId, questionZone, questionDraft, expectedRevision: 1).ConfigureAwait(false);
            Console.WriteLine("PASS owner creates user question envelope with known kind/author/method/evidence/level");

            var synthesisDraft = JsonSerializer.Serialize(new
            {
                kind = "synthesis",
                author = "assistant",
                creationMethod = "synthesis",
                evidence = "reportedSource",
                level = "l3Plus",
                title = "Synthetic staging synthesis",
                body = "Synthetic S2-T1 AI synthesis kept distinct from user authorship; never verified as user belief.",
                sources = new[] { new { sourceId = (string?)null, locator = "synthetic-locator-session-1", availability = "available" } },
                occurredAtUtc = now,
                recordedAtUtc = now.AddMinutes(1),
                applicability = new { project = "synthetic-app", environment = (string?)null, version = (string?)null }
            });
            var synthesisId = "s2t1-synthesis-beta";
            await WriteDraftAsync(connectionsExecutable, fixture, synthesisId, questionZone, synthesisDraft, expectedRevision: 1).ConfigureAwait(false);
            Console.WriteLine("PASS owner creates AI synthesis envelope with source locator and reported evidence");

            var observationDraft = JsonSerializer.Serialize(new
            {
                kind = "observation",
                author = "tool",
                creationMethod = "verbatimExtract",
                evidence = "experimentalObservation",
                level = "l0",
                title = (string?)null,
                body = "Synthetic S2-T1 tool observation payload; kept distinct from claim and decision.",
                sources = new[] { new { sourceId = "s2t1-tool-source", locator = (string?)null, availability = "unavailable" } },
                occurredAtUtc = (DateTimeOffset?)null,
                recordedAtUtc = now.AddMinutes(1),
                applicability = (object?)null
            });
            var observationId = "s2t1-observation-gamma";
            await WriteDraftAsync(connectionsExecutable, fixture, observationId, questionZone, observationDraft, expectedRevision: 1).ConfigureAwait(false);
            await VerifyMemoryReadAsync(connectionsExecutable, fixture, observationId, "observation", "tool", "verbatimExtract", "experimentalObservation", "l0").ConfigureAwait(false);
            Console.WriteLine("PASS owner creates tool observation envelope with unavailable source and unknown stays unknown");
            var discovery = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "catalog.list"
            }).ConfigureAwait(false);
            Require(discovery.TryGetProperty("status", out var discoveryStatus) &&
                    discoveryStatus.GetString() == "catalog" &&
                    discovery.TryGetProperty("publishedCatalog", out var published) &&
                    published.ValueKind == JsonValueKind.Array && published.GetArrayLength() == 0,
                "Creating memory items auto-published private metadata to the agent catalog.");
            Console.WriteLine("PASS metadata not auto-published: agent catalog stays empty after memory creates");

            var invalidQuestion = JsonSerializer.Serialize(new
            {
                kind = "question",
                author = "user",
                creationMethod = "directWrite",
                evidence = "userConfirmed",
                level = "l1",
                title = "Invalid synthetic question",
                body = "A question must not carry user-confirmed belief evidence.",
                sources = Array.Empty<object>(),
                occurredAtUtc = now,
                recordedAtUtc = now.AddMinutes(1),
                applicability = (object?)null
            });
            var invalidWrite = await WriteDraftResultAsync(connectionsExecutable, fixture, "s2t1-invalid-question", questionZone, invalidQuestion).ConfigureAwait(false);
            Require(invalidWrite.ExitCode == 4 && invalidWrite.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "Invalid attribution (question with user-confirmed evidence) was not rejected.");
            Console.WriteLine("PASS invalid attribution rejected: question cannot carry user-confirmed evidence");

            var missingDraft = JsonSerializer.Serialize(new
            {
                kind = "claim",
                author = (string?)null,
                creationMethod = "directWrite",
                evidence = "unverified",
                level = "l1",
                title = "Missing author",
                body = "Author is required.",
                sources = Array.Empty<object>(),
                occurredAtUtc = now,
                recordedAtUtc = now.AddMinutes(1),
                applicability = (object?)null
            });
            var missingWrite = await WriteDraftResultAsync(connectionsExecutable, fixture, "s2t1-invalid-claim", questionZone, missingDraft).ConfigureAwait(false);
            Require(missingWrite.ExitCode == 4 && missingWrite.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "Missing required attribution was not rejected.");
            Console.WriteLine("PASS invalid schema rejected: missing author and fabricated evidence denied");

            var badTitle = new string('x', 513);
            var badDraft = JsonSerializer.Serialize(new
            {
                kind = "claim",
                author = "user",
                creationMethod = "directWrite",
                evidence = "unverified",
                level = "l1",
                title = badTitle,
                body = "Title too long.",
                sources = Array.Empty<object>(),
                occurredAtUtc = now,
                recordedAtUtc = now.AddMinutes(1),
                applicability = (object?)null
            });
            var badWrite = await WriteDraftResultAsync(connectionsExecutable, fixture, "s2t1-invalid-title", questionZone, badDraft).ConfigureAwait(false);
            Require(badWrite.ExitCode == 4 && badWrite.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "Overlong title schema was not rejected.");
            Console.WriteLine("PASS invalid schema rejected: overlong title denied");

            var retitleRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", questionId).ConfigureAwait(false);
            Require(retitleRead.ExitCode == 0 &&
                    retitleRead.StandardOutput.Contains("Why does staging reject this payload?", StringComparison.Ordinal) &&
                    !retitleRead.StandardOutput.Contains("retitled", StringComparison.Ordinal),
                "The stored title changed without a revisioned write; ID/title independence was not proven.");
            Console.WriteLine("PASS ID stable independent of title: same opaque ID reads original title until a revisioned write");
            var grantBefore = await ReadScopeSnapshotAsync(connectionsExecutable, fixture).ConfigureAwait(false);
            var membership = await RunOwnerCommandWithInputAsync(connectionsExecutable, "SET\n",
                "owner", "collection-set", "--control-pipe", fixture.ControlPipe,
                "--collection-id", "s2t1-collection", "--resource-ids", questionId).ConfigureAwait(false);
            Require(membership.ExitCode == 0, "Owner collection membership could not be set for S2-T1.");
            var revisedDraft = questionDraft.Replace("Why does staging reject this payload?", "Why does staging reject this payload, revised?");
            await ReviseS2T1DraftAsync(connectionsExecutable, fixture, questionId, questionZone, revisedDraft, writeRevision: 1, expectedRevision: 2).ConfigureAwait(false);
            var grantAfter = await ReadScopeSnapshotAsync(connectionsExecutable, fixture).ConfigureAwait(false);
            Require(grantBefore == grantAfter, "Adding a memory item, membership, or revision changed the grant snapshot.");
            Console.WriteLine("PASS grant snapshot unchanged after item add, membership set, and revision bump");
            await VerifyAgentScopedMemoryAsync(connectionsExecutable, fixture, questionId, questionZone).ConfigureAwait(false);

            var deniedCrossScope = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "s2t1-not-present").ConfigureAwait(false);
            Require(deniedCrossScope.ExitCode == 3 && deniedCrossScope.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "Unknown memory ID was not denied without an existence oracle.");
            Console.WriteLine("PASS scope negative: unknown memory ID denied with generic invalid_request");

            var status = await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false);
            Require(status.Contains("vault=unlocked", StringComparison.Ordinal), "Vault did not stay unlocked for the S2-T1 lock boundary check.");
            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "Owner lock did not complete for S2-T1.");
            var lockedRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", questionId).ConfigureAwait(false);
            Require(lockedRead.ExitCode == 3, "Locked memory read was not denied at the lock boundary.");
            Console.WriteLine("PASS lock boundary remains correct: locked reads denied after owner lock");

            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            VerifyS2T1CiphertextOnly(fixture);
            Console.WriteLine("PASS encrypted persistence only: vault resources are ciphertext with bound identity/revision");
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (service is not null)
            {
                try
                {
                    await StopServiceAsync(service).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = failure is null ? ex : new AggregateException(failure, ex);
                }
            }

            try
            {
                fixture.Dispose();
            }
            catch (Exception ex)
            {
                failure = failure is null ? ex : new AggregateException(failure, ex);
            }
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        Console.WriteLine("LIMIT synthetic S2-T1 fixture only; no source registry, relations, lifecycle, collections views beyond membership, or index rebuild exercised; same-owner OS compromise remains outside the threat model.");
    }

    private static async Task WriteDraftAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson, long expectedRevision)
    {
        var result = await WriteDraftResultAsync(executable, fixture, resourceId, zoneId, draftJson).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"MEMORY stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Memory create did not store {resourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}; draft={draftJson}).");
    }

    private static async Task ReviseS2T1DraftAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson, long writeRevision, long expectedRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t1-revise-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--expected-revision", writeRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"MEMORY stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Memory revise did not store {resourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}; draft={draftJson}).");
    }

    private static async Task<CommandResult> WriteDraftResultAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t1-draft-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
    }

    private static async Task VerifyMemoryReadAsync(string executable, SmokeFixture fixture, string resourceId, string kind, string author, string method, string evidence, string level)
    {
        var read = await RunOwnerCommandAsync(executable,
            "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId).ConfigureAwait(false);
        Require(read.ExitCode == 0 &&
                read.StandardOutput.Contains($"MEMORY id={resourceId}", StringComparison.Ordinal) &&
                read.StandardOutput.Contains($"kind={kind}", StringComparison.Ordinal) &&
                read.StandardOutput.Contains($"author={author}", StringComparison.Ordinal) &&
                read.StandardOutput.Contains($"executor=owner", StringComparison.Ordinal) &&
                read.StandardOutput.Contains($"method={method}", StringComparison.Ordinal) &&
                read.StandardOutput.Contains($"evidence={evidence}", StringComparison.Ordinal) &&
                read.StandardOutput.Contains($"level={level}", StringComparison.Ordinal),
            $"Memory read did not return the typed envelope for {resourceId} (exit={read.ExitCode}; stdout={read.StandardOutput.Trim()}; stderr={read.StandardError.Trim()}).");
    }

    private static async Task<string> ReadScopeSnapshotAsync(string executable, SmokeFixture fixture)
    {
        var grants = await RunOwnerCommandAsync(executable,
            "owner", "grants", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(grants.ExitCode == 0, "Owner grant listing failed during the S2-T1 snapshot check.");
        return grants.StandardOutput;
    }
    private static async Task VerifyAgentScopedMemoryAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId)
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var handoff = Path.Combine(fixture.OutputDirectory, "s2t1-grant.handoff");
        var sessionFile = Path.Combine(fixture.OutputDirectory, "s2t1-session.handoff");
        var grant = await RunOwnerCommandWithInputAsync(executable, "ISSUE\n",
            "owner", "grant", "--control-pipe", fixture.ControlPipe,
            "--resource-ids", resourceId, "--operations", "memory.read",
            "--expires-at-utc", expiry, "--handoff-file", handoff).ConfigureAwait(false);
        Require(grant.ExitCode == 0 && grant.StandardOutput.Contains("operations=memory.read", StringComparison.Ordinal),
            "Owner could not issue a memory.read-scoped grant for S2-T1 (exit=" + grant.ExitCode + "; stderr=" + grant.StandardError.Trim() + ").");
        var redeem = await RunOwnerCommandAsync(executable,
            "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
            "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
        Require(redeem.ExitCode == 0, "Agent redemption failed for the S2-T1 memory.read grant (stderr=" + redeem.StandardError.Trim() + ").");
        var allowed = await InvokeAgentRequestAsync(fixture.AgentPipe, new
        {
            protocolVersion = 1,
            operation = "agent.memory.read",
            sessionToken = ReadSessionBearer(sessionFile),
            accessOperation = "memory.read",
            resourceId,
            resourceRevision = 2L
        }).ConfigureAwait(false);
        Require(allowed.TryGetProperty("status", out var allowedStatus) && allowedStatus.GetString() == "memory" &&
                allowed.TryGetProperty("memoryItem", out var item) && item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("id", out var id) && id.GetString() == resourceId &&
                item.TryGetProperty("kind", out var kind) && kind.GetString() == "question",
            "Scoped agent memory.read did not return the typed envelope inside the approved ID/revision.");
        var denied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
        {
            protocolVersion = 1,
            operation = "agent.memory.read",
            sessionToken = ReadSessionBearer(sessionFile),
            accessOperation = "memory.read",
            resourceId = "s2t1-synthesis-beta",
            resourceRevision = 1L
        }).ConfigureAwait(false);
        Require(denied.TryGetProperty("error", out var deniedError) && deniedError.GetString() == "resource_unavailable",
            "Agent memory.read outside the approved ID scope was not denied.");
        Console.WriteLine("PASS scoped agent memory.read serves the typed envelope inside the grant and denies outside IDs");
    }

    private static void VerifyS2T1CiphertextOnly(SmokeFixture fixture)
    {
        var files = Directory.GetFiles(fixture.VaultResourcesPath, "*.mbv", SearchOption.TopDirectoryOnly);
        Require(files.Length >= 3, "The S2-T1 vault did not persist three encrypted memory envelopes.");
        foreach (var file in files)
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            Require(!text.Contains("Synthetic S2-T1", StringComparison.Ordinal),
                "Private memory plaintext leaked into a vault ciphertext file: " + Path.GetFileName(file));
            Require(text.Contains("\"ciphertext\"", StringComparison.Ordinal) && text.Contains("\"nonce\"", StringComparison.Ordinal),
                "A vault resource is not in the authenticated envelope form: " + Path.GetFileName(file));
        }
    }
}
