using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    internal static async Task RunS2T4RelationStoreAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S2-T4 relation store smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S2-T4 typed relations, cycle constraints, revision-aware review and scoped access");
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
                "Provision did not create the S2-T4 synthetic vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");

            var now = DateTimeOffset.UtcNow.AddMinutes(-2);
            const string zoneAlpha = "s2t4-zone-alpha";
            const string zoneBeta = "s2t4-zone-beta";
            var srcOne = "s2t4-src-one";
            var srcTwo = "s2t4-src-two";
            var lesson = "s2t4-item-lesson";
            var synth = "s2t4-item-synth";
            var remote = "s2t4-item-remote";
            var oldItem = "s2t4-item-old";
            var newItem = "s2t4-item-new";
            var relSupportOne = "rel-s2t4-support-one";
            var relSupportTwo = "rel-s2t4-support-two";
            var relDerived = "rel-s2t4-derived";
            var relCrossZone = "rel-s2t4-cross-zone";
            var relCycleBack = "rel-s2t4-cycle-back";
            var relSupersedeOld = "rel-s2t4-supersede-old";
            var relRejectable = "rel-s2t4-rejectable";
            const string reasonOne = "Synthetic S2-T4 reason one; never a real owner secret.";
            const string reasonTwo = "Synthetic S2-T4 reason two; never a real owner secret.";
            const string unlinkReason = "Synthetic S2-T4 unlink rationale; never a real owner secret.";
            const string rejectReason = "Synthetic S2-T4 rejection rationale; never a real owner secret.";

            // Two registered sources in the subject zone plus one cross-zone
            // item give the chain its multi-source and cross-zone shape.
            var sourceOneDraft = JsonSerializer.Serialize(new
            {
                title = "Synthetic S2-T4 source one",
                locator = "s2t4-private-source-one-locator",
                version = "synthetic-source-v1",
                availability = "available",
                creationMethod = "directWrite",
                capturedAtUtc = now,
                recordedAtUtc = now.AddMinutes(1)
            });
            await WriteS2T4SourceDraftAsync(connectionsExecutable, fixture, srcOne, zoneAlpha, sourceOneDraft, expectedRevision: 1).ConfigureAwait(false);
            var sourceTwoDraft = JsonSerializer.Serialize(new
            {
                title = "Synthetic S2-T4 source two",
                locator = "s2t4-private-source-two-locator",
                version = "synthetic-source-v1",
                availability = "available",
                creationMethod = "directWrite",
                capturedAtUtc = now,
                recordedAtUtc = now.AddMinutes(1)
            });
            await WriteS2T4SourceDraftAsync(connectionsExecutable, fixture, srcTwo, zoneAlpha, sourceTwoDraft, expectedRevision: 1).ConfigureAwait(false);

            var lessonBody = "Synthetic S2-T4 lesson body revision one; never a real owner secret.";
            var synthBody = "Synthetic S2-T4 synthesis body revision one; never a real owner secret.";
            await WriteS2T4ItemDraftAsync(connectionsExecutable, fixture, lesson, zoneAlpha,
                S2T4ItemDraft("claim", "Synthetic S2-T4 lesson", lessonBody, now), expectedRevision: 1).ConfigureAwait(false);
            await WriteS2T4ItemDraftAsync(connectionsExecutable, fixture, synth, zoneAlpha,
                S2T4ItemDraft("synthesis", "Synthetic S2-T4 synthesis", synthBody, now), expectedRevision: 1).ConfigureAwait(false);
            await WriteS2T4ItemDraftAsync(connectionsExecutable, fixture, remote, zoneBeta,
                S2T4ItemDraft("claim", "Synthetic S2-T4 cross-zone item", "Synthetic S2-T4 cross-zone body; never a real owner secret.", now), expectedRevision: 1).ConfigureAwait(false);
            await WriteS2T4ItemDraftAsync(connectionsExecutable, fixture, oldItem, zoneAlpha,
                S2T4ItemDraft("decision", "Synthetic S2-T4 superseded decision", "Synthetic S2-T4 old decision body; never a real owner secret.", now), expectedRevision: 1).ConfigureAwait(false);
            await WriteS2T4ItemDraftAsync(connectionsExecutable, fixture, newItem, zoneAlpha,
                S2T4ItemDraft("decision", "Synthetic S2-T4 replacement decision", "Synthetic S2-T4 new decision body; never a real owner secret.", now), expectedRevision: 1).ConfigureAwait(false);

            // Multi-source chain: lesson supports two registered sources at
            // pinned revisions with spans; synthesis derives from the lesson.
            await CreateS2T4RelationAsync(connectionsExecutable, fixture, relSupportOne, zoneAlpha,
                S2T4RelationDraft("supports", "accepted", lesson, "memoryItem", 1L, null, srcOne, "registeredSource", 1L, new { start = 0L, end = 512L }, reasonOne, now)).ConfigureAwait(false);
            await CreateS2T4RelationAsync(connectionsExecutable, fixture, relSupportTwo, zoneAlpha,
                S2T4RelationDraft("supports", "accepted", lesson, "memoryItem", 1L, null, srcTwo, "registeredSource", 1L, new { start = 0L, end = 256L }, reasonTwo, now)).ConfigureAwait(false);
            await CreateS2T4RelationAsync(connectionsExecutable, fixture, relDerived, zoneAlpha,
                S2T4RelationDraft("derivedFrom", "accepted", synth, "memoryItem", 1L, null, lesson, "memoryItem", 1L, null, "Synthetic S2-T4 derivation reason; never a real owner secret.", now)).ConfigureAwait(false);
            await CreateS2T4RelationAsync(connectionsExecutable, fixture, relCrossZone, zoneAlpha,
                S2T4RelationDraft("relatedTo", "accepted", lesson, "memoryItem", 1L, null, remote, "memoryItem", 1L, null, "Synthetic S2-T4 cross-zone organization reason; never a real owner secret.", now)).ConfigureAwait(false);

            var chainRead = await ReadS2T4RelationAsync(connectionsExecutable, fixture, relDerived).ConfigureAwait(false);
            Require(chainRead.ExitCode == 0 &&
                    chainRead.StandardOutput.Contains("kind=derivedFrom", StringComparison.Ordinal) &&
                    chainRead.StandardOutput.Contains("state=accepted", StringComparison.Ordinal) &&
                    chainRead.StandardOutput.Contains($"from resource={synth}", StringComparison.Ordinal) &&
                    chainRead.StandardOutput.Contains($"to resource={lesson}", StringComparison.Ordinal) &&
                    chainRead.StandardOutput.Contains("review=current", StringComparison.Ordinal),
                "The created derivation relation did not read back with its endpoints and current review state (exit=" + chainRead.ExitCode + "; stdout=" + chainRead.StandardOutput.Trim() + "; stderr=" + chainRead.StandardError.Trim() + ").");
            var spanRead = await ReadS2T4RelationAsync(connectionsExecutable, fixture, relSupportOne).ConfigureAwait(false);
            Require(spanRead.ExitCode == 0 &&
                    spanRead.StandardOutput.Contains($"to resource={srcOne}", StringComparison.Ordinal) &&
                    spanRead.StandardOutput.Contains("kind=registeredSource revision=1 span=0-512", StringComparison.Ordinal) &&
                    spanRead.StandardOutput.Contains("review=current", StringComparison.Ordinal),
                "The evidence link did not keep its pinned source revision and span (exit=" + spanRead.ExitCode + "; stdout=" + spanRead.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS owner creates a multi-source relation chain keeping pinned revisions, spans and current review state");

            // Cycle constraints: derived_from and supersedes are one derivation
            // DAG; pure and mixed cycles are refused, related_to may cycle.
            var derivedCycle = await CreateS2T4RelationResultAsync(connectionsExecutable, fixture, "rel-s2t4-cycle-derived", zoneAlpha,
                S2T4RelationDraft("derivedFrom", "accepted", lesson, "memoryItem", 1L, null, synth, "memoryItem", 1L, null, reasonOne, now)).ConfigureAwait(false);
            Require(derivedCycle.ExitCode == 4 && derivedCycle.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A derived_from cycle was accepted.");
            await CreateS2T4RelationAsync(connectionsExecutable, fixture, relSupersedeOld, zoneAlpha,
                S2T4RelationDraft("supersedes", "accepted", oldItem, "memoryItem", 1L, null, newItem, "memoryItem", 1L, null, "Synthetic S2-T4 supersession reason; never a real owner secret.", now)).ConfigureAwait(false);
            var supersedeCycle = await CreateS2T4RelationResultAsync(connectionsExecutable, fixture, "rel-s2t4-cycle-supersede", zoneAlpha,
                S2T4RelationDraft("supersedes", "accepted", newItem, "memoryItem", 1L, null, oldItem, "memoryItem", 1L, null, reasonTwo, now)).ConfigureAwait(false);
            Require(supersedeCycle.ExitCode == 4 && supersedeCycle.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A supersedes cycle was accepted.");
            var mixedCycle = await CreateS2T4RelationResultAsync(connectionsExecutable, fixture, "rel-s2t4-cycle-mixed", zoneAlpha,
                S2T4RelationDraft("derivedFrom", "accepted", newItem, "memoryItem", 1L, null, oldItem, "memoryItem", 1L, null, reasonOne, now)).ConfigureAwait(false);
            Require(mixedCycle.ExitCode == 4 && mixedCycle.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A mixed derived_from/supersedes cycle was accepted.");
            var selfEndpoint = await CreateS2T4RelationResultAsync(connectionsExecutable, fixture, "rel-s2t4-self", zoneAlpha,
                S2T4RelationDraft("relatedTo", "accepted", lesson, "memoryItem", 1L, null, lesson, "memoryItem", 1L, null, reasonOne, now)).ConfigureAwait(false);
            Require(selfEndpoint.ExitCode == 4 && selfEndpoint.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A relation with one endpoint was accepted.");
            await CreateS2T4RelationAsync(connectionsExecutable, fixture, relCycleBack, zoneAlpha,
                S2T4RelationDraft("relatedTo", "accepted", remote, "memoryItem", 1L, null, lesson, "memoryItem", 1L, null, "Synthetic S2-T4 reverse organization reason; never a real owner secret.", now)).ConfigureAwait(false);
            Console.WriteLine("PASS derived_from/supersedes cycles (pure and mixed) rejected while related_to cycles are allowed");

            // Endpoints must exist at the live revision the link pins, with the
            // declared kind: a stale pin, an unknown endpoint, or a span on a
            // memory item fails closed.
            var stalePin = await CreateS2T4RelationResultAsync(connectionsExecutable, fixture, "rel-s2t4-stale-pin", zoneAlpha,
                S2T4RelationDraft("supports", "accepted", lesson, "memoryItem", 1L, null, srcTwo, "registeredSource", 7L, null, reasonOne, now)).ConfigureAwait(false);
            Require(stalePin.ExitCode == 4 && stalePin.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A relation pinned a revision the vault does not hold.");
            var missingEndpoint = await CreateS2T4RelationResultAsync(connectionsExecutable, fixture, "rel-s2t4-missing", zoneAlpha,
                S2T4RelationDraft("supports", "accepted", lesson, "memoryItem", 1L, null, "s2t4-not-present", "registeredSource", 1L, null, reasonOne, now)).ConfigureAwait(false);
            Require(missingEndpoint.ExitCode == 4 && missingEndpoint.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A relation was created against an endpoint that does not exist.");
            var wrongKind = await CreateS2T4RelationResultAsync(connectionsExecutable, fixture, "rel-s2t4-wrong-kind", zoneAlpha,
                S2T4RelationDraft("supports", "accepted", lesson, "memoryItem", 1L, null, srcTwo, "memoryItem", 1L, null, reasonOne, now)).ConfigureAwait(false);
            Require(wrongKind.ExitCode == 4 && wrongKind.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A relation endpoint kind was accepted against a different resource kind.");
            var spanOnItem = await CreateS2T4RelationResultAsync(connectionsExecutable, fixture, "rel-s2t4-item-span", zoneAlpha,
                S2T4RelationDraft("supports", "accepted", lesson, "memoryItem", 1L, null, srcTwo, "registeredSource", 1L, new { start = 0L, end = 512L }, reasonOne, now)).ConfigureAwait(false);
            Require(spanOnItem.ExitCode == 0, "A valid registered-source span relation was rejected.");
            var relationIdPrefix = await CreateS2T4RelationResultAsync(connectionsExecutable, fixture, "s2t4-not-a-relation", zoneAlpha,
                S2T4RelationDraft("relatedTo", "accepted", lesson, "memoryItem", 1L, null, remote, "memoryItem", 1L, null, reasonOne, now)).ConfigureAwait(false);
            Require(relationIdPrefix.ExitCode == 4 && relationIdPrefix.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A relation resource ID outside the reserved relation namespace was accepted.");
            var relationEndpointRelation = await CreateS2T4RelationResultAsync(connectionsExecutable, fixture, "rel-s2t4-endpoint-relation", zoneAlpha,
                S2T4RelationDraft("relatedTo", "accepted", lesson, "memoryItem", 1L, null, relSupportTwo, "memoryItem", 1L, null, reasonOne, now)).ConfigureAwait(false);
            Require(relationEndpointRelation.ExitCode == 4 && relationEndpointRelation.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A relation used another relation as an endpoint.");
            var duplicateCreate = await CreateS2T4RelationResultAsync(connectionsExecutable, fixture, relSupportOne, zoneAlpha,
                S2T4RelationDraft("supports", "accepted", lesson, "memoryItem", 1L, null, srcOne, "registeredSource", 1L, new { start = 0L, end = 512L }, reasonOne, now)).ConfigureAwait(false);
            Require(duplicateCreate.ExitCode == 4 && duplicateCreate.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A duplicate relation create overwrote the existing record.");
            Console.WriteLine("PASS relation endpoints are verified live at the pinned revision with the declared kind and namespace");

            // Source revision change and availability loss: the direct evidence
            // link and its transitive dependents need review, while the pinned
            // revision stays the truthful historical record.
            var revisedSourceDraft = JsonSerializer.Serialize(new
            {
                title = "Synthetic S2-T4 source one",
                locator = "s2t4-private-source-one-locator",
                version = "synthetic-source-v2",
                availability = "unavailable",
                creationMethod = "directWrite",
                capturedAtUtc = now,
                recordedAtUtc = now.AddMinutes(1)
            });
            await ReviseS2T4SourceDraftAsync(connectionsExecutable, fixture, srcOne, zoneAlpha, revisedSourceDraft, writeRevision: 1, expectedRevision: 2).ConfigureAwait(false);
            var changedRead = await ReadS2T4RelationAsync(connectionsExecutable, fixture, relSupportOne).ConfigureAwait(false);
            Require(changedRead.ExitCode == 0 &&
                    changedRead.StandardOutput.Contains("review=needsReview", StringComparison.Ordinal) &&
                    changedRead.StandardOutput.Contains("sourceUnavailable", StringComparison.Ordinal) &&
                    changedRead.StandardOutput.Contains("endpointRevisionChanged", StringComparison.Ordinal) &&
                    changedRead.StandardOutput.Contains($"stale={srcOne}", StringComparison.Ordinal) &&
                    changedRead.StandardOutput.Contains("kind=registeredSource revision=1 span=0-512", StringComparison.Ordinal),
                "A changed, unavailable source did not mark the direct evidence link for review while keeping the pinned revision (exit=" + changedRead.ExitCode + "; stdout=" + changedRead.StandardOutput.Trim() + ").");
            var transitiveRead = await ReadS2T4RelationAsync(connectionsExecutable, fixture, relDerived).ConfigureAwait(false);
            Require(transitiveRead.ExitCode == 0 &&
                    transitiveRead.StandardOutput.Contains("review=needsReview", StringComparison.Ordinal) &&
                    transitiveRead.StandardOutput.Contains("dependencyNeedsReview", StringComparison.Ordinal) &&
                    transitiveRead.StandardOutput.Contains($"stale={lesson}", StringComparison.Ordinal),
                "The derivation did not inherit the review need transitively from the changed source (stdout=" + transitiveRead.StandardOutput.Trim() + ").");
            var untouchedRead = await ReadS2T4RelationAsync(connectionsExecutable, fixture, relSupportTwo).ConfigureAwait(false);
            Require(untouchedRead.ExitCode == 0 && untouchedRead.StandardOutput.Contains("review=current", StringComparison.Ordinal),
                "An unrelated evidence link was marked for review.");
            var itemAfterChange = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", synth).ConfigureAwait(false);
            Require(itemAfterChange.ExitCode == 0 && itemAfterChange.StandardOutput.Contains(synthBody, StringComparison.Ordinal),
                "The derived item body was rewritten instead of being left to owner review.");
            Console.WriteLine("PASS source revision change and missing availability mark direct and transitive dependents for review without rewriting conclusions");

            // Lifecycle: unlink needs a rationale, relink returns through
            // proposed, reject needs a rationale, invalid transitions fail.
            var unlinked = await TransitionS2T4RelationAsync(connectionsExecutable, fixture, "relation-unlink", relDerived, expectedRevision: 1, unlinkReason).ConfigureAwait(false);
            Require(unlinked.ExitCode == 0 && unlinked.StandardOutput.Contains("state=retracted", StringComparison.Ordinal) &&
                    unlinked.StandardOutput.Contains($"stateReason={unlinkReason}", StringComparison.Ordinal),
                "Unlink did not record the retracted state with its rationale (exit=" + unlinked.ExitCode + "; stdout=" + unlinked.StandardOutput.Trim() + "; stderr=" + unlinked.StandardError.Trim() + ").");
            var unlinkWithoutReason = await TransitionS2T4RelationResultAsync(connectionsExecutable, fixture, "relation-unlink", relSupportTwo, expectedRevision: 1, stateReason: null).ConfigureAwait(false);
            Require(unlinkWithoutReason.ExitCode == 4 && unlinkWithoutReason.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "An unlink without an explicit rationale was accepted.");
            var acceptRetracted = await TransitionS2T4RelationResultAsync(connectionsExecutable, fixture, "relation-accept", relDerived, expectedRevision: 2, stateReason: null).ConfigureAwait(false);
            Require(acceptRetracted.ExitCode == 4 && acceptRetracted.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A retracted relation was accepted directly.");
            var relinked = await TransitionS2T4RelationAsync(connectionsExecutable, fixture, "relation-relink", relDerived, expectedRevision: 2, stateReason: null).ConfigureAwait(false);
            Require(relinked.ExitCode == 0 && relinked.StandardOutput.Contains("state=proposed", StringComparison.Ordinal),
                "Relink did not restore the unlinked relation as proposed (exit=" + relinked.ExitCode + "; stdout=" + relinked.StandardOutput.Trim() + ").");
            var acceptedAgain = await TransitionS2T4RelationAsync(connectionsExecutable, fixture, "relation-accept", relDerived, expectedRevision: 3, stateReason: null).ConfigureAwait(false);
            Require(acceptedAgain.ExitCode == 0 && acceptedAgain.StandardOutput.Contains("state=accepted", StringComparison.Ordinal),
                "The relinked relation could not be accepted again.");
            await CreateS2T4RelationAsync(connectionsExecutable, fixture, relRejectable, zoneAlpha,
                S2T4RelationDraft("contradicts", "proposed", oldItem, "memoryItem", 1L, null, newItem, "memoryItem", 1L, null, "Synthetic S2-T4 contradiction reason; never a real owner secret.", now)).ConfigureAwait(false);
            var rejected = await TransitionS2T4RelationAsync(connectionsExecutable, fixture, "relation-reject", relRejectable, expectedRevision: 1, rejectReason).ConfigureAwait(false);
            Require(rejected.ExitCode == 0 && rejected.StandardOutput.Contains("state=rejected", StringComparison.Ordinal) &&
                    rejected.StandardOutput.Contains($"stateReason={rejectReason}", StringComparison.Ordinal),
                "Reject did not record the rejected state with its rationale (exit=" + rejected.ExitCode + "; stdout=" + rejected.StandardOutput.Trim() + ").");
            var acceptRejected = await TransitionS2T4RelationResultAsync(connectionsExecutable, fixture, "relation-accept", relRejectable, expectedRevision: 2, stateReason: null).ConfigureAwait(false);
            Require(acceptRejected.ExitCode == 4 && acceptRejected.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A rejected relation was accepted again.");
            var createTerminal = await CreateS2T4RelationResultAsync(connectionsExecutable, fixture, "rel-s2t4-terminal", zoneAlpha,
                S2T4RelationDraft("relatedTo", "rejected", oldItem, "memoryItem", 1L, null, newItem, "memoryItem", 1L, null, reasonOne, now)).ConfigureAwait(false);
            Require(createTerminal.ExitCode == 4 && createTerminal.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A relation was created directly in a terminal state.");
            var staleTransition = await TransitionS2T4RelationResultAsync(connectionsExecutable, fixture, "relation-accept", relRejectable, expectedRevision: 1, stateReason: null).ConfigureAwait(false);
            Require(staleTransition.ExitCode == 4 && staleTransition.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A stale lifecycle transition was accepted.");
            Console.WriteLine("PASS relation lifecycle transitions record explicit rationale and refuse invalid or stale transitions");

            // Agent link creation inside the approved link.create grant and
            // scoped relation reads with both endpoints in the granted snapshot.
            var linkSession = await IssueS2T4LinkGrantAsync(connectionsExecutable, fixture, "s2t4-link",
                relSupportTwo, lesson, srcTwo).ConfigureAwait(false);
            var agentLinkDraft = S2T4RelationDraft("supports", "proposed", lesson, "memoryItem", 1L, null, srcTwo, "registeredSource", 1L, new { start = 64L, end = 128L },
                "Synthetic S2-T4 agent link reason; never a real owner secret.", now,
                "The user explicitly asked to connect this synthetic lesson to this synthetic research source as evidence.");
            var agentCreated = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.relation.create",
                sessionToken = ReadSessionBearer(linkSession.SessionFile),
                accessOperation = "link.create",
                contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(agentLinkDraft))
            }).ConfigureAwait(false);
            string agentRelationId = string.Empty;
            if (agentCreated.TryGetProperty("status", out var linkStatus) &&
                linkStatus.GetString() == "relation_created" &&
                agentCreated.TryGetProperty("relation", out var linkRelation) &&
                linkRelation.ValueKind == JsonValueKind.Object &&
                linkRelation.TryGetProperty("id", out var linkId) &&
                linkId.GetString() is { } createdRelationId &&
                createdRelationId.StartsWith("rel-", StringComparison.Ordinal) &&
                linkRelation.GetProperty("state").GetString() == "proposed" &&
                linkRelation.GetProperty("executor").GetString() == "agent")
            {
                agentRelationId = createdRelationId;
            }

            Require(agentRelationId.Length > 0,
                "The agent link.create grant did not create a proposed agent relation (reply=" + agentCreated.GetRawText() + ").");
            var agentRelationRead = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.relation.read",
                sessionToken = ReadSessionBearer(linkSession.SessionFile),
                accessOperation = "relation.read",
                resourceId = relSupportTwo,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            Require(agentRelationRead.TryGetProperty("status", out var readStatus) && readStatus.GetString() == "relation" &&
                    agentRelationRead.TryGetProperty("relation", out var readRelation) && readRelation.ValueKind == JsonValueKind.Object &&
                    readRelation.GetProperty("id").GetString() == relSupportTwo &&
                    readRelation.GetProperty("review").GetProperty("state").GetString() == "current",
                "The scoped agent relation read did not serve the approved relation inside its snapshot (reply=" + agentRelationRead.GetRawText() + ").");

            // Two cross-zone negative reads: the relation is in scope but the
            // cross-zone endpoint is not, and the relation itself is out of
            // scope. Neither discloses the endpoint, its zone, or existence.
            var crossZoneSession = await IssueS2T4RelationReadGrantAsync(connectionsExecutable, fixture, "s2t4-cross-zone",
                relCrossZone, lesson).ConfigureAwait(false);
            var crossZoneDenied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.relation.read",
                sessionToken = ReadSessionBearer(crossZoneSession.SessionFile),
                accessOperation = "relation.read",
                resourceId = relCrossZone,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            Require(crossZoneDenied.TryGetProperty("error", out var crossZoneError) && crossZoneError.GetString() == "resource_unavailable",
                "A cross-zone relation read was served outside the granted endpoints.");
            var crossZoneRaw = crossZoneDenied.GetRawText();
            foreach (var forbidden in new[] { remote, zoneBeta, "Synthetic S2-T4 cross-zone organization reason", "title", "body" })
            {
                Require(!crossZoneRaw.Contains(forbidden, StringComparison.Ordinal),
                    "A denied cross-zone relation read disclosed endpoint metadata: " + forbidden);
            }

            var outOfScopeSession = await IssueS2T4RelationReadGrantAsync(connectionsExecutable, fixture, "s2t4-out-of-scope",
                relSupportTwo, lesson, srcTwo).ConfigureAwait(false);
            var outOfScopeDenied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.relation.read",
                sessionToken = ReadSessionBearer(outOfScopeSession.SessionFile),
                accessOperation = "relation.read",
                resourceId = relCrossZone,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            Require(outOfScopeDenied.TryGetProperty("error", out var outOfScopeError) && outOfScopeError.GetString() == "resource_unavailable",
                "A relation outside the granted snapshot was served.");
            var unknownRelation = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.relation.read",
                sessionToken = ReadSessionBearer(outOfScopeSession.SessionFile),
                accessOperation = "relation.read",
                resourceId = "rel-s2t4-not-present",
                resourceRevision = 1L
            }).ConfigureAwait(false);
            Require(unknownRelation.TryGetProperty("error", out var unknownError) && unknownError.GetString() == "resource_unavailable",
                "An unknown relation ID produced a distinguishable denial (existence oracle).");

            // A source endpoint requires the source read operation; an item-only
            // grant is denied even when the relation itself is in scope.
            var itemOnlySession = await IssueS2T4MemoryOnlyGrantAsync(connectionsExecutable, fixture, "s2t4-item-only",
                relSupportTwo, lesson).ConfigureAwait(false);
            var itemOnlyDenied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.relation.read",
                sessionToken = ReadSessionBearer(itemOnlySession.SessionFile),
                accessOperation = "relation.read",
                resourceId = relSupportTwo,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            Require(itemOnlyDenied.TryGetProperty("error", out var itemOnlyError) && itemOnlyError.GetString() == "resource_unavailable",
                "An item-only grant served a relation whose other endpoint is a registered source.");
            Console.WriteLine("PASS scoped agent link creation and relation reads enforce exact endpoints and deny cross-zone reads without metadata leaks");

            // Zone change / policy generation / revocation deny warm sessions
            // without expanding the newer scope backward or forward.
            var warmRead = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.relation.read",
                sessionToken = ReadSessionBearer(linkSession.SessionFile),
                accessOperation = "relation.read",
                resourceId = relSupportTwo,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            Require(warmRead.TryGetProperty("status", out _), "The warm relation read stopped working before the zone-change check.");
            var collectionChange = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, "SET" + Environment.NewLine,
                "owner", "collection-set", "--control-pipe", fixture.ControlPipe,
                "--collection-id", "s2t4-relation-collection",
                "--resource-ids", lesson + "," + srcTwo).ConfigureAwait(false);
            Require(collectionChange.ExitCode == 0, "The owner could not change synthetic collection membership for the policy-generation check.");
            var generationDenied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.relation.read",
                sessionToken = ReadSessionBearer(linkSession.SessionFile),
                accessOperation = "relation.read",
                resourceId = relSupportTwo,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            Require(generationDenied.TryGetProperty("error", out var generationError) && generationError.GetString() == "resource_unavailable",
                "A policy-generation change left a warm relation read authorized.");
            var revokedSession = await IssueS2T4RelationReadGrantAsync(connectionsExecutable, fixture, "s2t4-revoke",
                relSupportTwo, lesson, srcTwo).ConfigureAwait(false);
            var revoke = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "revoke-session", "--control-pipe", fixture.ControlPipe,
                "--session-id", revokedSession.SessionId).ConfigureAwait(false);
            Require(revoke.ExitCode == 0, "The owner could not revoke the synthetic relation-read session.");
            var revokedDenied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.relation.read",
                sessionToken = ReadSessionBearer(revokedSession.SessionFile),
                accessOperation = "relation.read",
                resourceId = relSupportTwo,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            Require(revokedDenied.TryGetProperty("error", out var revokedError) && revokedError.GetString() == "resource_unavailable",
                "A revoked session still served a relation read.");

            // A grant resolved over a different zone never reaches the first
            // zone's relation, even though the resource IDs look similar.
            var zoneGrant = await IssueS2T4ZoneGrantAsync(connectionsExecutable, fixture, "s2t4-zone-beta-only", zoneBeta).ConfigureAwait(false);
            var zoneDenied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.relation.read",
                sessionToken = ReadSessionBearer(zoneGrant.SessionFile),
                accessOperation = "relation.read",
                resourceId = relCrossZone,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            Require(zoneDenied.TryGetProperty("error", out var zoneError) && zoneError.GetString() == "resource_unavailable",
                "A zone-scoped grant reached a relation outside its zone.");
            var zoneLinkDenied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.relation.create",
                sessionToken = ReadSessionBearer(zoneGrant.SessionFile),
                accessOperation = "link.create",
                contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(agentLinkDraft))
            }).ConfigureAwait(false);
            Require(zoneLinkDenied.TryGetProperty("error", out var zoneLinkError) && zoneLinkError.GetString() == "resource_unavailable",
                "A zone-scoped grant created a link outside its zone.");
            Console.WriteLine("PASS zone change, policy-generation change and revocation deny warm relation reads and links without scope expansion");

            // Lock boundary and restart durability.
            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "Owner lock did not complete for the S2-T4 lock boundary check.");
            var lockedRelationRead = await ReadS2T4RelationAsync(connectionsExecutable, fixture, relSupportOne).ConfigureAwait(false);
            Require(lockedRelationRead.ExitCode != 0 && !lockedRelationRead.StandardOutput.Contains($"RELATION id={relSupportOne}", StringComparison.Ordinal),
                "A locked relation read was served.");
            var lockedTransition = await TransitionS2T4RelationResultAsync(connectionsExecutable, fixture, "relation-accept", relRejectable, expectedRevision: 2, stateReason: null).ConfigureAwait(false);
            Require(lockedTransition.ExitCode != 0 && !lockedTransition.StandardOutput.Contains("RELATION stored", StringComparison.Ordinal),
                "A locked lifecycle transition was accepted.");
            var lockedAgentLink = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.relation.create",
                sessionToken = ReadSessionBearer(linkSession.SessionFile),
                accessOperation = "link.create",
                contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(agentLinkDraft))
            }).ConfigureAwait(false);
            Require(lockedAgentLink.TryGetProperty("error", out var lockedLinkError),
                "A locked vault served an agent link creation.");
            var lockedAgentRead = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.relation.read",
                sessionToken = ReadSessionBearer(linkSession.SessionFile),
                accessOperation = "relation.read",
                resourceId = relSupportTwo,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            Require(lockedAgentRead.TryGetProperty("error", out var lockedReadError),
                "A locked vault served an agent relation read.");

            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false)).Contains("vault=locked", StringComparison.Ordinal),
                "The restarted service did not return locked for the S2-T4 durability check.");
            var lockedAfterRestart = await ReadS2T4RelationAsync(connectionsExecutable, fixture, relSupportOne).ConfigureAwait(false);
            Require(lockedAfterRestart.ExitCode != 0, "A locked relation read after restart was served.");
            var reunlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(reunlock.ExitCode == 0 && reunlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The passphrase did not unlock the restarted vault for the S2-T4 durability check.");
            var afterRestart = await ReadS2T4RelationAsync(connectionsExecutable, fixture, relSupportOne).ConfigureAwait(false);
            Require(afterRestart.ExitCode == 0 &&
                    afterRestart.StandardOutput.Contains("review=needsReview", StringComparison.Ordinal) &&
                    afterRestart.StandardOutput.Contains("kind=registeredSource revision=1 span=0-512", StringComparison.Ordinal),
                "The relation did not survive restart and unlock with its pinned revision and review state (stdout=" + afterRestart.StandardOutput.Trim() + ").");
            var agentRelationAfterRestart = await ReadS2T4RelationAsync(connectionsExecutable, fixture, agentRelationId).ConfigureAwait(false);
            Require(agentRelationAfterRestart.ExitCode == 0 &&
                    agentRelationAfterRestart.StandardOutput.Contains("state=proposed", StringComparison.Ordinal) &&
                    agentRelationAfterRestart.StandardOutput.Contains("executor=agent", StringComparison.Ordinal),
                "The agent-created relation did not survive restart and unlock (stdout=" + agentRelationAfterRestart.StandardOutput.Trim() + ").");
            var unknownRelationRead = await ReadS2T4RelationAsync(connectionsExecutable, fixture, "rel-s2t4-not-present").ConfigureAwait(false);
            Require(unknownRelationRead.ExitCode == 3 && unknownRelationRead.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "An unknown relation ID was not denied with the generic code (existence oracle).");
            Console.WriteLine("PASS relations survive restart and unlock with pinned revisions and review state; lock and unknown IDs deny generically");

            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            VerifyS2T4CiphertextOnly(fixture, expectedResources: 16);
            Console.WriteLine("PASS persisted relation/canonical/temp files carry no plaintext");
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

        Console.WriteLine("LIMIT synthetic S2-T4 fixture only; relation graph traversal re-reads relation records per operation and a rebuildable relation index is S2-T7 work; cross-zone declassification, MCP link tools, agent unlink, and lifecycle state machines for items stay in later tasks; same-owner OS compromise remains outside the threat model.");
    }
}
