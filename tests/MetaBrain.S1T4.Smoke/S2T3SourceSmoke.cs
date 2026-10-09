using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    internal static async Task RunS2T3SourceRegistryAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S2-T3 source registry smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S2-T3 encrypted source registry and item-independent citation spans");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        Exception? failure = null;
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "Provision did not create the S2-T3 synthetic vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");

            var now = DateTimeOffset.UtcNow.AddMinutes(-2);
            var sourceId = "s2t3-source-alpha";
            var itemId = "s2t3-item-beta";
            const string zoneId = "s2t3-private-zone";
            const string registryLocator = "s2t3-private-registry-locator-session-file";
            const string registryTitle = "Synthetic S2-T3 registry title";
            const string registryVersionOne = "synthetic-source-v1";
            const string registryVersionTwo = "synthetic-source-v2";
            const string citationLocator = "s2t3-item-side-citation-locator";

            var sourceDraft = JsonSerializer.Serialize(new
            {
                title = registryTitle,
                locator = registryLocator,
                version = registryVersionOne,
                availability = "available",
                creationMethod = "directWrite",
                capturedAtUtc = now,
                recordedAtUtc = now.AddMinutes(1)
            });
            await WriteS2T3SourceDraftAsync(connectionsExecutable, fixture, sourceId, zoneId, sourceDraft, expectedRevision: 1).ConfigureAwait(false);
            var registered = await ReadS2T3SourceAsync(connectionsExecutable, fixture, sourceId).ConfigureAwait(false);
            Require(registered.ExitCode == 0 &&
                    registered.StandardOutput.Contains($"SOURCE id={sourceId}", StringComparison.Ordinal) &&
                    registered.StandardOutput.Contains($"zone={zoneId}", StringComparison.Ordinal) &&
                    registered.StandardOutput.Contains($"locator={registryLocator}", StringComparison.Ordinal) &&
                    registered.StandardOutput.Contains($"version={registryVersionOne}", StringComparison.Ordinal) &&
                    registered.StandardOutput.Contains("availability=available", StringComparison.Ordinal) &&
                    registered.StandardOutput.Contains("revision=1", StringComparison.Ordinal),
                "Owner source read did not round-trip the registered metadata/version/locator/availability (exit=" + registered.ExitCode + "; stdout=" + registered.StandardOutput.Trim() + "; stderr=" + registered.StandardError.Trim() + ").");
            Console.WriteLine("PASS owner registers and reads encrypted synthetic source metadata/version/locator/availability");

            var itemDraft = JsonSerializer.Serialize(new
            {
                kind = "question",
                author = "user",
                creationMethod = "directWrite",
                evidence = "unverified",
                level = "l1",
                title = "Which synthetic session revision does this citation anchor to?",
                body = "Synthetic S2-T3 item body; never a real owner secret.",
                sources = Array.Empty<object>(),
                occurredAtUtc = now,
                recordedAtUtc = now.AddMinutes(1),
                applicability = (object?)null
            });
            await WriteS2T3ItemDraftAsync(connectionsExecutable, fixture, itemId, zoneId, itemDraft, expectedRevision: 1).ConfigureAwait(false);

            // Link: revise the item with a citation anchored to the live source
            // revision plus a validated span.
            var linkedDraft = JsonSerializer.Serialize(new
            {
                kind = "question",
                author = "user",
                creationMethod = "directWrite",
                evidence = "unverified",
                level = "l1",
                title = "Which synthetic session revision does this citation anchor to?",
                body = "Synthetic S2-T3 item body linked to the registered source; never a real owner secret.",
                sources = new[]
                {
                    new
                    {
                        sourceId,
                        locator = citationLocator,
                        availability = "available",
                        sourceRevision = 1L,
                        span = new { start = 0L, end = 4096L }
                    }
                },
                occurredAtUtc = now,
                recordedAtUtc = now.AddMinutes(1),
                applicability = (object?)null
            });
            await ReviseS2T3ItemDraftAsync(connectionsExecutable, fixture, itemId, zoneId, linkedDraft, writeRevision: 1, expectedRevision: 2).ConfigureAwait(false);
            Console.WriteLine("PASS owner links memory item to the live registered source revision with a validated span");

            // Span boundary negatives: empty and inverted spans fail closed.
            foreach (var (start, end) in new[] { (100L, 100L), (500L, 100L) })
            {
                var badSpan = JsonSerializer.Serialize(new
                {
                    kind = "claim",
                    author = "user",
                    creationMethod = "directWrite",
                    evidence = "unverified",
                    level = "l1",
                    title = "Invalid synthetic span citation",
                    body = "A citation span must be a non-empty bounded range.",
                    sources = new[]
                    {
                        new
                        {
                            sourceId,
                            locator = citationLocator,
                            availability = "available",
                            sourceRevision = 1L,
                            span = new { start, end }
                        }
                    },
                    occurredAtUtc = now,
                    recordedAtUtc = now.AddMinutes(1),
                    applicability = (object?)null
                });
                var rejected = await WriteS2T3ItemDraftResultAsync(connectionsExecutable, fixture, "s2t3-item-bad-span", zoneId, badSpan).ConfigureAwait(false);
                Require(rejected.ExitCode == 4 && rejected.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                    $"A citation span [{start},{end}) was accepted instead of failing closed.");
            }

            // A revision anchor that names no registered source is rejected.
            var unregisteredAnchor = JsonSerializer.Serialize(new
            {
                kind = "claim",
                author = "user",
                creationMethod = "directWrite",
                evidence = "unverified",
                level = "l1",
                title = "Unregistered anchor citation",
                body = "A revision anchor requires a registered source.",
                sources = new[]
                {
                    new
                    {
                        sourceId = "s2t3-unregistered-source",
                        locator = (string?)null,
                        availability = "unknown",
                        sourceRevision = 7L,
                        span = new { start = 0L, end = 16L }
                    }
                },
                occurredAtUtc = now,
                recordedAtUtc = now.AddMinutes(1),
                applicability = (object?)null
            });
            var anchorRejected = await WriteS2T3ItemDraftResultAsync(connectionsExecutable, fixture, "s2t3-item-unregistered", zoneId, unregisteredAnchor).ConfigureAwait(false);
            Require(anchorRejected.ExitCode == 4 && anchorRejected.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A citation anchor for an unregistered source revision was accepted.");
            Console.WriteLine("PASS invalid citation spans and unregistered source anchors rejected");

            // Availability and revision change under the live-revision gate.
            var revisedSourceDraft = JsonSerializer.Serialize(new
            {
                title = registryTitle,
                locator = registryLocator,
                version = registryVersionTwo,
                availability = "unavailable",
                creationMethod = "directWrite",
                capturedAtUtc = now,
                recordedAtUtc = now.AddMinutes(1)
            });
            await ReviseS2T3SourceDraftAsync(connectionsExecutable, fixture, sourceId, zoneId, revisedSourceDraft, writeRevision: 1, expectedRevision: 2).ConfigureAwait(false);
            var staleSource = await ReviseS2T3SourceDraftResultAsync(connectionsExecutable, fixture, sourceId, zoneId, revisedSourceDraft, writeRevision: 1).ConfigureAwait(false);
            Require(staleSource.ExitCode == 4 && staleSource.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A stale source revise was accepted instead of failing closed.");
            Console.WriteLine("PASS owner changes source availability and revision under the live-revision gate");

            // The stored citation anchor stays historical and truthful after
            // the source moves to revision 2; a new stale anchor is rejected.
            var staleAnchorDraft = JsonSerializer.Serialize(new
            {
                kind = "claim",
                author = "user",
                creationMethod = "directWrite",
                evidence = "unverified",
                level = "l1",
                title = "Stale anchor citation",
                body = "A citation anchor must name the live registered source revision.",
                sources = new[]
                {
                    new
                    {
                        sourceId,
                        locator = citationLocator,
                        availability = "available",
                        sourceRevision = 1L,
                        span = new { start = 0L, end = 16L }
                    }
                },
                occurredAtUtc = now,
                recordedAtUtc = now.AddMinutes(1),
                applicability = (object?)null
            });
            var staleAnchor = await WriteS2T3ItemDraftResultAsync(connectionsExecutable, fixture, "s2t3-item-stale-anchor", zoneId, staleAnchorDraft).ConfigureAwait(false);
            Require(staleAnchor.ExitCode == 4 && staleAnchor.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A citation anchor for a superseded source revision was accepted as if verified.");
            var historicalRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", itemId).ConfigureAwait(false);
            Require(historicalRead.ExitCode == 0 &&
                    historicalRead.StandardOutput.Contains("revision=2", StringComparison.Ordinal) &&
                    historicalRead.StandardOutput.Contains($"source id={sourceId}", StringComparison.Ordinal) &&
                    historicalRead.StandardOutput.Contains("revision=1 span=0-4096", StringComparison.Ordinal),
                "The linked item could not be read back with its historical citation anchor and span.");
            Console.WriteLine("PASS stale citation anchor rejected; stored anchor remains a truthful historical record");

            // Grant A: item-only. The citation projection must not disclose
            // registry-private title/locator/version, and the source API stays
            // unauthorized.
            var itemSession = await IssueS2T3AgentSessionAsync(connectionsExecutable, fixture, itemId, "memory.read").ConfigureAwait(false);
            var itemGranted = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.memory.read",
                sessionToken = ReadSessionBearer(itemSession),
                accessOperation = "memory.read",
                resourceId = itemId,
                resourceRevision = 2L
            }).ConfigureAwait(false);
            Require(itemGranted.TryGetProperty("status", out var itemStatus) && itemStatus.GetString() == "memory" &&
                    itemGranted.TryGetProperty("memoryItem", out var itemWire) && itemWire.ValueKind == JsonValueKind.Object &&
                    itemWire.TryGetProperty("sources", out var itemSources) && itemSources.ValueKind == JsonValueKind.Array &&
                    itemSources.GetArrayLength() == 1 &&
                    itemSources[0].TryGetProperty("sourceId", out var citedId) && citedId.GetString() == sourceId &&
                    itemSources[0].TryGetProperty("sourceRevision", out var citedRevision) && citedRevision.GetInt64() == 1 &&
                    itemSources[0].TryGetProperty("span", out var citedSpan) && citedSpan.ValueKind == JsonValueKind.Object &&
                    citedSpan.GetProperty("start").GetInt64() == 0 && citedSpan.GetProperty("end").GetInt64() == 4096,
                "The item-only grant did not serve the linked citation anchor and span.");
            var itemRaw = itemGranted.GetRawText();
            foreach (var forbidden in new[] { registryTitle, registryLocator, registryVersionTwo })
            {
                Require(!itemRaw.Contains(forbidden, StringComparison.Ordinal),
                    "The citation projection under an item-only grant disclosed registry-private metadata: " + forbidden);
            }

            var sourceViaItemGrant = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.source.read",
                sessionToken = ReadSessionBearer(itemSession),
                accessOperation = "source.read",
                resourceId = sourceId,
                resourceRevision = 2L
            }).ConfigureAwait(false);
            Require(sourceViaItemGrant.TryGetProperty("error", out var itemGrantError) && itemGrantError.GetString() == "resource_unavailable",
                "An item-only grant authorized the source API.");
            Console.WriteLine("PASS item-only grant serves the citation without registry metadata and denies the source API");

            // Grant B: explicit source grant opens the live revision only.
            var sourceSession = await IssueS2T3AgentSessionAsync(connectionsExecutable, fixture, sourceId, "source.read").ConfigureAwait(false);
            var sourceGranted = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.source.read",
                sessionToken = ReadSessionBearer(sourceSession),
                accessOperation = "source.read",
                resourceId = sourceId,
                resourceRevision = 2L
            }).ConfigureAwait(false);
            Require(sourceGranted.TryGetProperty("status", out var sourceStatus) && sourceStatus.GetString() == "source" &&
                    sourceGranted.TryGetProperty("source", out var sourceWire) && sourceWire.ValueKind == JsonValueKind.Object &&
                    sourceWire.GetProperty("availability").GetString() == "unavailable" &&
                    sourceWire.GetProperty("version").GetString() == registryVersionTwo &&
                    sourceWire.GetProperty("locator").GetString() == registryLocator &&
                    sourceWire.GetProperty("revision").GetInt64() == 2,
                "The explicit source grant did not open the live registered source revision.");
            var wrongRevision = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.source.read",
                sessionToken = ReadSessionBearer(sourceSession),
                accessOperation = "source.read",
                resourceId = sourceId,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            Require(wrongRevision.TryGetProperty("error", out var revisionError) && revisionError.GetString() == "resource_unavailable",
                "A source read outside the granted revision was allowed.");
            var itemViaSourceGrant = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.memory.read",
                sessionToken = ReadSessionBearer(sourceSession),
                accessOperation = "memory.read",
                resourceId = itemId,
                resourceRevision = 2L
            }).ConfigureAwait(false);
            Require(itemViaSourceGrant.TryGetProperty("error", out var crossGrantError) && crossGrantError.GetString() == "resource_unavailable",
                "A source-only grant authorized item reads.");
            Console.WriteLine("PASS explicit source grant opens the live revision, denies other revisions, and opens no item reads");

            // Lock boundary: no source reads, writes, or expansion while locked.
            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "Owner lock did not complete for the S2-T3 lock boundary check.");
            var lockedSourceRead = await ReadS2T3SourceAsync(connectionsExecutable, fixture, sourceId).ConfigureAwait(false);
            Require(lockedSourceRead.ExitCode != 0 && !lockedSourceRead.StandardOutput.Contains($"SOURCE id={sourceId}", StringComparison.Ordinal),
                "A locked source read was served.");
            var lockedSourceWrite = await ReviseS2T3SourceDraftResultAsync(connectionsExecutable, fixture, sourceId, zoneId, revisedSourceDraft, writeRevision: 2).ConfigureAwait(false);
            Require(lockedSourceWrite.ExitCode != 0 && !lockedSourceWrite.StandardOutput.Contains("SOURCE stored", StringComparison.Ordinal),
                "A locked source write was accepted.");
            var lockedExpansion = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.source.read",
                sessionToken = ReadSessionBearer(sourceSession),
                accessOperation = "source.read",
                resourceId = sourceId,
                resourceRevision = 2L
            }).ConfigureAwait(false);
            Require(lockedExpansion.TryGetProperty("error", out var lockedError),
                "A locked vault still expanded a registered source for an agent session.");
            Console.WriteLine("PASS lock boundary denies source reads, writes, and expansion");

            // Restart + unlock durability.
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false)).Contains("vault=locked", StringComparison.Ordinal),
                "The restarted service did not return locked for the S2-T3 durability check.");
            var lockedAfterRestart = await ReadS2T3SourceAsync(connectionsExecutable, fixture, sourceId).ConfigureAwait(false);
            Require(lockedAfterRestart.ExitCode != 0, "A locked source read after restart was served.");
            var reunlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(reunlock.ExitCode == 0 && reunlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The passphrase did not unlock the restarted vault for the S2-T3 durability check.");
            var afterRestart = await ReadS2T3SourceAsync(connectionsExecutable, fixture, sourceId).ConfigureAwait(false);
            Require(afterRestart.ExitCode == 0 &&
                    afterRestart.StandardOutput.Contains("availability=unavailable", StringComparison.Ordinal) &&
                    afterRestart.StandardOutput.Contains($"locator={registryLocator}", StringComparison.Ordinal) &&
                    afterRestart.StandardOutput.Contains($"version={registryVersionTwo}", StringComparison.Ordinal),
                "The registered source did not survive restart and unlock with its revised state.");
            var unknownSource = await ReadS2T3SourceAsync(connectionsExecutable, fixture, "s2t3-not-present").ConfigureAwait(false);
            Require(unknownSource.ExitCode == 3 && unknownSource.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "An unknown source ID was not denied with the generic code (existence oracle).");
            var missingAvailability = JsonSerializer.Serialize(new
            {
                title = registryTitle,
                locator = registryLocator,
                version = registryVersionOne,
                creationMethod = "directWrite",
                capturedAtUtc = now,
                recordedAtUtc = now.AddMinutes(1)
            });
            var missingAvailabilityWrite = await WriteS2T3SourceDraftResultAsync(connectionsExecutable, fixture, "s2t3-source-missing-availability", zoneId, missingAvailability).ConfigureAwait(false);
            Require(missingAvailabilityWrite.ExitCode == 4 && missingAvailabilityWrite.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A source registration without an explicit availability was accepted.");
            Console.WriteLine("PASS registered source survives restart and unlock; missing availability explicit; unknown IDs denied generically");

            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            VerifyS2T3CiphertextOnly(fixture);
            Console.WriteLine("PASS persisted registry/canonical/temp files carry no plaintext");
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

        Console.WriteLine("LIMIT synthetic S2-T3 fixture only; adapters/JSONL ingestion stay Sprint 3, originals stay outside vault protection; relations/lifecycle/collections/index stay pending; same-owner OS compromise remains outside the threat model.");
    }

    private static async Task WriteS2T3SourceDraftAsync(string executable, SmokeFixture fixture, string sourceId, string zoneId, string draftJson, long expectedRevision)
    {
        var result = await WriteS2T3SourceDraftResultAsync(executable, fixture, sourceId, zoneId, draftJson).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"SOURCE stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Source create did not store {sourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}; draft={draftJson}).");
    }

    private static async Task<CommandResult> WriteS2T3SourceDraftResultAsync(string executable, SmokeFixture fixture, string sourceId, string zoneId, string draftJson)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t3-source-draft-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "source-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", sourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
    }

    private static async Task ReviseS2T3SourceDraftAsync(string executable, SmokeFixture fixture, string sourceId, string zoneId, string draftJson, long writeRevision, long expectedRevision)
    {
        var result = await ReviseS2T3SourceDraftResultAsync(executable, fixture, sourceId, zoneId, draftJson, writeRevision).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"SOURCE stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Source revise did not store {sourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}; draft={draftJson}).");
    }

    private static async Task<CommandResult> ReviseS2T3SourceDraftResultAsync(string executable, SmokeFixture fixture, string sourceId, string zoneId, string draftJson, long writeRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t3-source-revise-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "source-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", sourceId, "--zone-id", zoneId,
            "--expected-revision", writeRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--input-file", input).ConfigureAwait(false);
    }

    private static async Task<CommandResult> ReadS2T3SourceAsync(string executable, SmokeFixture fixture, string sourceId)
    {
        return await RunOwnerCommandAsync(executable,
            "owner", "source-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", sourceId).ConfigureAwait(false);
    }

    private static async Task WriteS2T3ItemDraftAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson, long expectedRevision)
    {
        var result = await WriteS2T3ItemDraftResultAsync(executable, fixture, resourceId, zoneId, draftJson).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"MEMORY stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Memory create did not store {resourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}; draft={draftJson}).");
    }

    private static async Task<CommandResult> WriteS2T3ItemDraftResultAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t3-item-draft-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
    }

    private static async Task ReviseS2T3ItemDraftAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson, long writeRevision, long expectedRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t3-item-revise-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--expected-revision", writeRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"MEMORY stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Memory revise did not store {resourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}; draft={draftJson}).");
    }

    private static async Task<string> IssueS2T3AgentSessionAsync(string executable, SmokeFixture fixture, string resourceId, string operation)
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var handoff = Path.Combine(fixture.OutputDirectory, "s2t3-grant-" + Guid.NewGuid().ToString("N") + ".handoff");
        var sessionFile = Path.Combine(fixture.OutputDirectory, "s2t3-session-" + Guid.NewGuid().ToString("N") + ".handoff");
        var grant = await RunOwnerCommandWithInputAsync(executable, "ISSUE\n",
            "owner", "grant", "--control-pipe", fixture.ControlPipe,
            "--resource-ids", resourceId, "--operations", operation,
            "--expires-at-utc", expiry, "--handoff-file", handoff).ConfigureAwait(false);
        Require(grant.ExitCode == 0 && grant.StandardOutput.Contains($"operations={operation}", StringComparison.Ordinal),
            $"Owner could not issue a {operation}-scoped grant for S2-T3 (exit=" + grant.ExitCode + "; stderr=" + grant.StandardError.Trim() + ").");
        var redeem = await RunOwnerCommandAsync(executable,
            "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
            "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
        Require(redeem.ExitCode == 0, $"Agent redemption failed for the S2-T3 {operation} grant (stderr=" + redeem.StandardError.Trim() + ").");
        return sessionFile;
    }

    private static void VerifyS2T3CiphertextOnly(SmokeFixture fixture)
    {
        foreach (var path in Directory.GetFiles(fixture.VaultDirectoryPath, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            Require(!text.Contains("Synthetic S2-T3", StringComparison.Ordinal),
                "Private source/citation plaintext leaked into a persisted vault file: " + Path.GetFileName(path));
            Require(!text.Contains("s2t3-private-registry-locator-session-file", StringComparison.Ordinal),
                "The private registry locator leaked into a persisted vault file: " + Path.GetFileName(path));
        }

        var resources = Directory.GetFiles(fixture.VaultResourcesPath, "*.mbv", SearchOption.TopDirectoryOnly);
        Require(resources.Length == 2, "The S2-T3 vault did not persist exactly the registered source and the linked item ciphertext (found " + resources.Length + ").");
        foreach (var file in resources)
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            Require(text.Contains("\"ciphertext\"", StringComparison.Ordinal) && text.Contains("\"nonce\"", StringComparison.Ordinal),
                "A vault resource is not in the authenticated envelope form: " + Path.GetFileName(file));
        }

        Require(!Directory.GetFiles(fixture.VaultDirectoryPath, "*", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(fixture.ServiceDirectoryPath, "*", SearchOption.AllDirectories))
            .Any(path => Path.GetFileName(path).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)),
            "The S2-T3 vault or service directory left an atomic-write temporary file.");

        foreach (var draft in Directory.GetFiles(fixture.OutputDirectory, "s2t3-*", SearchOption.TopDirectoryOnly))
        {
            File.Delete(draft);
        }

        Require(!Directory.GetFiles(fixture.OutputDirectory, "s2t3-*", SearchOption.TopDirectoryOnly).Any(),
            "Synthetic plaintext draft/handoff files remained outside the vault after the S2-T3 scenario.");
    }
}
