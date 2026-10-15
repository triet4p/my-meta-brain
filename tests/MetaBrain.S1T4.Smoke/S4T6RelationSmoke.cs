using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string S4T6ZoneAlpha = "s4t6-zone-alpha";
    private const string S4T6ZoneBeta = "s4t6-zone-beta";
    private const string S4T6Idea = "s4t6-item-idea";
    private const string S4T6IdeaTwo = "s4t6-item-idea-two";
    private const string S4T6Old = "s4t6-item-old";
    private const string S4T6New = "s4t6-item-new";
    private const string S4T6Revision = "s4t6-item-revision";
    private const string S4T6UniqueResearch = "s4t6-source-research-x";
    private const string S4T6AmbiguousOne = "s4t6-source-ambiguous-one";
    private const string S4T6AmbiguousTwo = "s4t6-source-ambiguous-two";
    private const string S4T6CrossZone = "s4t6-source-cross-zone";
    private const string S4T6CrossRelation = "rel-s4t6-owner-cross-zone";
    private const string S4T6UniqueTitle = "Synthetic Research X Unique";
    private const string S4T6UniqueRequest = "Please connect this idea with Research X; they are related, not evidence for each other.";
    private const string S4T6BridgeRequest = "These notes are related but do not support one another.";
    private const string S4T6RemoveRequest = "Please unlink this relation from the current idea.";
    private const string S4T6RestoreRequest = "Undo that unlink and restore the proposed relation.";
    private const string S4T6SupersedeRequest = "The newer decision explicitly supersedes the earlier decision.";

    private sealed record S4T6Session(string SessionId, string SessionFile);

    private static string S4T6ItemDraftJson(string title, string body, DateTimeOffset now) => JsonSerializer.Serialize(new
    {
        kind = "claim",
        author = "user",
        creationMethod = "directWrite",
        evidence = "unverified",
        level = "l1",
        title,
        body,
        sources = Array.Empty<object>(),
        occurredAtUtc = now,
        recordedAtUtc = now.AddMinutes(1),
        applicability = (object?)null
    });

    private static string S4T6SourceDraftJson(string title, string locator, DateTimeOffset now) => JsonSerializer.Serialize(new
    {
        title,
        locator,
        version = "synthetic-research-v1",
        availability = "available",
        creationMethod = "directWrite",
        capturedAtUtc = now,
        recordedAtUtc = now.AddMinutes(1)
    });

    private static string S4T6RelationDraft(
        string kind,
        string state,
        string fromResourceId,
        string fromEndpointKind,
        long fromRevision,
        string toResourceId,
        string toEndpointKind,
        long toRevision,
        string reason,
        string? requestEvidence,
        DateTimeOffset now) => JsonSerializer.Serialize(new
    {
        kind,
        state,
        from = new { resourceId = fromResourceId, endpointKind = fromEndpointKind, revision = fromRevision, span = (object?)null },
        to = new { resourceId = toResourceId, endpointKind = toEndpointKind, revision = toRevision, span = (object?)null },
        creationMethod = "directWrite",
        reason,
        stateReason = "agent-supplied state rationale must not survive creation",
        occurredAtUtc = now.AddYears(-2),
        recordedAtUtc = now.AddYears(-2).AddMinutes(1),
        requestEvidence
    });

    private static string S4T6TransitionDraft(string? stateReason, string requestEvidence) => JsonSerializer.Serialize(new
    {
        stateReason,
        requestEvidence
    });

    internal static async Task RunS4T6RelationsAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S4-T6 relation smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S4-T6 scoped relation resolve/list/create/unlink/supersede/undo");
        using var fixture = SmokeFixture.CreateUninitializedAt(
            Path.Combine(SmokeFixture.FindRepositoryRoot(), "artifacts", "sprint-4", ".s4t6-smoke-" + Guid.NewGuid().ToString("N")));
        RunningService? service = null;
        string passphrase;
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "Provision did not create the synthetic S4-T6 vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");
            var unlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S4-T6 vault did not unlock (exit=" + unlocked.ExitCode + "; stderr=" + unlocked.StandardError.Trim() + ").");

            var now = DateTimeOffset.UtcNow.AddMinutes(-5);
            await WriteS4T6ResourceAsync(connectionsExecutable, fixture, S4T6Idea, S4T6ZoneAlpha,
                S4T6ItemDraftJson("Synthetic S4-T6 current idea", "Synthetic S4-T6 idea body; no real owner content.", now),
                "memory-create", "MEMORY").ConfigureAwait(false);
            await WriteS4T6ResourceAsync(connectionsExecutable, fixture, S4T6IdeaTwo, S4T6ZoneAlpha,
                S4T6ItemDraftJson("Synthetic S4-T6 second idea", "Synthetic S4-T6 second idea body; no real owner content.", now),
                "memory-create", "MEMORY").ConfigureAwait(false);
            await WriteS4T6ResourceAsync(connectionsExecutable, fixture, S4T6Old, S4T6ZoneAlpha,
                S4T6ItemDraftJson("Synthetic S4-T6 earlier decision", "Synthetic S4-T6 earlier decision body.", now),
                "memory-create", "MEMORY").ConfigureAwait(false);
            await WriteS4T6ResourceAsync(connectionsExecutable, fixture, S4T6New, S4T6ZoneAlpha,
                S4T6ItemDraftJson("Synthetic S4-T6 newer decision", "Synthetic S4-T6 newer decision body.", now),
                "memory-create", "MEMORY").ConfigureAwait(false);
            await WriteS4T6ResourceAsync(connectionsExecutable, fixture, S4T6Revision, S4T6ZoneAlpha,
                S4T6ItemDraftJson("Synthetic S4-T6 frozen revision label", "Synthetic S4-T6 frozen revision body.", now),
                "memory-create", "MEMORY").ConfigureAwait(false);
            await WriteS4T6ResourceAsync(connectionsExecutable, fixture, S4T6UniqueResearch, S4T6ZoneAlpha,
                S4T6SourceDraftJson(S4T6UniqueTitle, "s4t6-private-locator-unique", now),
                "source-create", "SOURCE").ConfigureAwait(false);
            await WriteS4T6ResourceAsync(connectionsExecutable, fixture, S4T6AmbiguousOne, S4T6ZoneAlpha,
                S4T6SourceDraftJson("Synthetic Research Ambiguous Alpha", "s4t6-private-locator-ambiguous-one", now),
                "source-create", "SOURCE").ConfigureAwait(false);
            await WriteS4T6ResourceAsync(connectionsExecutable, fixture, S4T6AmbiguousTwo, S4T6ZoneAlpha,
                S4T6SourceDraftJson("Synthetic Research Ambiguous Beta", "s4t6-private-locator-ambiguous-two", now),
                "source-create", "SOURCE").ConfigureAwait(false);
            await WriteS4T6ResourceAsync(connectionsExecutable, fixture, S4T6CrossZone, S4T6ZoneBeta,
                S4T6SourceDraftJson("Synthetic Research Cross-Zone Sentinel", "s4t6-private-locator-cross-zone", now),
                "source-create", "SOURCE").ConfigureAwait(false);

            var crossRelationDraft = S4T6RelationDraft("relatedTo", "accepted", S4T6Idea, "memoryItem", 1,
                S4T6CrossZone, "registeredSource", 1,
                "Synthetic owner relation crosses zones; no content is shared by the link.", null, now);
            var crossRelationPath = await WriteS4T6DraftAsync(fixture, crossRelationDraft).ConfigureAwait(false);
            var crossRelation = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "relation-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S4T6CrossRelation, "--zone-id", S4T6ZoneAlpha,
                "--input-file", crossRelationPath).ConfigureAwait(false);
            Require(crossRelation.ExitCode == 0 && crossRelation.StandardOutput.Contains("RELATION stored revision=1", StringComparison.Ordinal),
                "The owner could not create the synthetic cross-zone relation fixture (exit=" + crossRelation.ExitCode + "; stderr=" + crossRelation.StandardError.Trim() + ").");

            var resourceScope = new[] { S4T6Idea, S4T6IdeaTwo, S4T6Old, S4T6Revision, S4T6AmbiguousOne, S4T6AmbiguousTwo };
            var destinationScope = new[] { S4T6UniqueResearch, S4T6New };
            var fullSession = await IssueS4T6GrantAsync(connectionsExecutable, fixture, "s4t6-full",
                "relation.list,memory.read,source.read,link.create,link.supersede,link.remove,link.restore",
                resourceScope, destinationScope).ConfigureAwait(false);
            var noCreateSession = await IssueS4T6GrantAsync(connectionsExecutable, fixture, "s4t6-no-create",
                "relation.list,memory.read,source.read", new[] { S4T6Idea, S4T6UniqueResearch }, Array.Empty<string>()).ConfigureAwait(false);
            var noEndpointReadSession = await IssueS4T6GrantAsync(connectionsExecutable, fixture, "s4t6-no-endpoint-read",
                "link.create", new[] { S4T6Idea }, new[] { S4T6UniqueResearch }).ConfigureAwait(false);
            var noListSession = await IssueS4T6GrantAsync(connectionsExecutable, fixture, "s4t6-no-list",
                "memory.read,source.read", new[] { S4T6Idea, S4T6UniqueResearch }, Array.Empty<string>()).ConfigureAwait(false);
            var removeOnlySession = await IssueS4T6GrantAsync(connectionsExecutable, fixture, "s4t6-remove-only",
                "relation.list,memory.read,source.read,link.remove", new[] { S4T6Idea, S4T6UniqueResearch }, Array.Empty<string>()).ConfigureAwait(false);
            var crossZoneSession = await IssueS4T6GrantAsync(connectionsExecutable, fixture, "s4t6-cross-zone-deny",
                "memory.read,source.read,link.create", new[] { S4T6Idea }, new[] { S4T6UniqueResearch }).ConfigureAwait(false);
            var noSupersedeSession = await IssueS4T6GrantAsync(connectionsExecutable, fixture, "s4t6-no-supersede",
                "memory.read,link.create", new[] { S4T6Old }, new[] { S4T6New }).ConfigureAwait(false);

            var bridgeTools = await RunBridgeToolsListAsync(connectionsExecutable, fixture.AgentPipe).ConfigureAwait(false);
            foreach (var tool in new[]
                     {
                         "metabrain_relation_resolve", "metabrain_relation_list", "metabrain_relation_create",
                         "metabrain_relation_supersede", "metabrain_relation_unlink", "metabrain_relation_restore"
                     })
            {
                Require(bridgeTools.Contains(tool, StringComparison.Ordinal), "The MCP bridge did not advertise " + tool + ".");
            }

            var initialList = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-relation-list", "--agent-pipe", fixture.AgentPipe,
                "--session-file", fullSession.SessionFile).ConfigureAwait(false);
            Require(initialList.ExitCode == 0 && initialList.StandardOutput.Contains("RELATIONS count=0", StringComparison.Ordinal) &&
                    !initialList.StandardOutput.Contains(S4T6CrossRelation, StringComparison.Ordinal) &&
                    !initialList.StandardOutput.Contains(S4T6CrossZone, StringComparison.Ordinal),
                "The initial list leaked a cross-zone relation or reported an unrelated count (stdout=" + initialList.StandardOutput.Trim() + ").");

            var uniqueResolution = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_relation_resolve", new { sessionFile = fullSession.SessionFile, query = S4T6UniqueTitle }).ConfigureAwait(false);
            Require(uniqueResolution.Contains("relation_resolved", StringComparison.Ordinal) &&
                    uniqueResolution.Contains(S4T6UniqueResearch, StringComparison.Ordinal) &&
                    uniqueResolution.Contains(S4T6UniqueTitle, StringComparison.Ordinal) &&
                    !uniqueResolution.Contains("s4t6-private-locator", StringComparison.Ordinal) &&
                    !uniqueResolution.Contains("body", StringComparison.OrdinalIgnoreCase) &&
                    !uniqueResolution.Contains(S4T6CrossZone, StringComparison.Ordinal),
                "Unique endpoint resolution did not stay within the frozen metadata-only scope (result=" + uniqueResolution.Trim() + ").");
            var uniqueResolutionViaCli = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-relation-resolve", "--agent-pipe", fixture.AgentPipe,
                "--session-file", fullSession.SessionFile, "--query", S4T6UniqueTitle).ConfigureAwait(false);
            Require(uniqueResolutionViaCli.ExitCode == 0 &&
                    uniqueResolutionViaCli.StandardOutput.Contains("status=relation_resolved", StringComparison.Ordinal) &&
                    uniqueResolutionViaCli.StandardOutput.Contains(S4T6UniqueResearch, StringComparison.Ordinal) &&
                    !uniqueResolutionViaCli.StandardOutput.Contains("s4t6-private-locator", StringComparison.Ordinal),
                "The actual agent CLI did not resolve a unique in-scope endpoint.");


            var ambiguousResolution = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_relation_resolve", new { sessionFile = fullSession.SessionFile, query = "Synthetic Research Ambiguous" }).ConfigureAwait(false);
            Require(ambiguousResolution.Contains("relation_ambiguous", StringComparison.Ordinal) &&
                    ambiguousResolution.Contains(S4T6AmbiguousOne, StringComparison.Ordinal) &&
                    ambiguousResolution.Contains(S4T6AmbiguousTwo, StringComparison.Ordinal) &&
                    ambiguousResolution.Contains("mutation_performed=false", StringComparison.Ordinal) &&
                    !ambiguousResolution.Contains("s4t6-private-locator", StringComparison.Ordinal),
                "Ambiguous endpoint resolution did not return only permitted candidates with no mutation signal (result=" + ambiguousResolution.Trim() + ").");
            var unresolvedCrossZone = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_relation_resolve", new { sessionFile = fullSession.SessionFile, query = "Synthetic Research Cross-Zone Sentinel" }).ConfigureAwait(false);
            Require(unresolvedCrossZone.Contains("relation_no_match", StringComparison.Ordinal) &&
                    !unresolvedCrossZone.Contains(S4T6CrossZone, StringComparison.Ordinal) &&
                    !unresolvedCrossZone.Contains("Cross-Zone Sentinel", StringComparison.Ordinal),
                "Endpoint resolution exposed an unapproved cross-zone source.");
            var listWithoutPermission = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-relation-resolve", "--agent-pipe", fixture.AgentPipe,
                "--session-file", noListSession.SessionFile, "--query", S4T6UniqueTitle).ConfigureAwait(false);
            Require(listWithoutPermission.ExitCode != 0 && listWithoutPermission.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "Endpoint resolution worked without the explicit relation.list operation.");

            var relationDraft = S4T6RelationDraft("relatedTo", "accepted", S4T6Idea, "memoryItem", 1,
                S4T6UniqueResearch, "registeredSource", 1,
                "Synthetic association only; it makes no evidence claim.", S4T6UniqueRequest, now);
            var relationDraftPath = await WriteS4T6DraftAsync(fixture, relationDraft).ConfigureAwait(false);
            var createDecision = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.access.authorize",
                sessionToken = ReadSessionBearer(fullSession.SessionFile),
                accessOperation = "link.create",
                resourceId = S4T6Idea,
                resourceRevision = 1L,
                destinationResourceId = S4T6UniqueResearch,
                destinationRevision = 1L
            }).ConfigureAwait(false);
            Require(ReplyCode(createDecision) == "authorization_decision" &&
                    createDecision.GetProperty("decision").GetProperty("allowed").GetBoolean(),
                "The exact link.create source/destination pair was not authorized before relation creation.");
            var subjectReadDecision = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.access.authorize",
                sessionToken = ReadSessionBearer(fullSession.SessionFile),
                accessOperation = "memory.read",
                resourceId = S4T6Idea,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            Require(ReplyCode(subjectReadDecision) == "authorization_decision" &&
                    subjectReadDecision.GetProperty("decision").GetProperty("allowed").GetBoolean(),
                "The agent session did not have memory.read for the relation subject.");
            var createViaCli = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-relation-create", "--agent-pipe", fixture.AgentPipe,
                "--session-file", fullSession.SessionFile, "--input-file", relationDraftPath).ConfigureAwait(false);
            var relationId = S4T6RelationIdFromOutput(createViaCli.StandardOutput);
            Require(createViaCli.ExitCode == 0 && relationId.StartsWith("rel-", StringComparison.Ordinal) &&
                    createViaCli.StandardOutput.Contains("kind=relatedTo", StringComparison.Ordinal) &&
                    createViaCli.StandardOutput.Contains("state=proposed", StringComparison.Ordinal) &&
                    createViaCli.StandardOutput.Contains("executor=agent", StringComparison.Ordinal) &&
                    createViaCli.StandardOutput.Contains("method=paraphrase", StringComparison.Ordinal) &&
                    !createViaCli.StandardOutput.Contains("owner", StringComparison.OrdinalIgnoreCase) &&
                    !createViaCli.StandardOutput.Contains(S4T6UniqueRequest, StringComparison.Ordinal),
                "The real agent CLI did not persist a proposed related_to link with server-stamped provenance (exit=" + createViaCli.ExitCode + "; stdout=" + createViaCli.StandardOutput.Trim() + "; stderr=" + createViaCli.StandardError.Trim() + ").");

            var listAfterCreate = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-relation-list", "--agent-pipe", fixture.AgentPipe,
                "--session-file", fullSession.SessionFile).ConfigureAwait(false);
            Require(listAfterCreate.ExitCode == 0 && listAfterCreate.StandardOutput.Contains(relationId, StringComparison.Ordinal) &&
                    listAfterCreate.StandardOutput.Contains("kind=relatedTo", StringComparison.Ordinal) &&
                    !listAfterCreate.StandardOutput.Contains("kind=supports", StringComparison.Ordinal) &&
                    !listAfterCreate.StandardOutput.Contains(S4T6CrossRelation, StringComparison.Ordinal) &&
                    !listAfterCreate.StandardOutput.Contains(S4T6CrossZone, StringComparison.Ordinal) &&
                    !listAfterCreate.StandardOutput.Contains(S4T6UniqueRequest, StringComparison.Ordinal),
                "The scoped relation list leaked hidden endpoints or request evidence, or changed related_to semantics (stdout=" + listAfterCreate.StandardOutput.Trim() + ").");

            var noMutationCreate = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_relation_create", new { sessionFile = noCreateSession.SessionFile, draft = relationDraft }).ConfigureAwait(false);
            Require(noMutationCreate.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !noMutationCreate.Contains("RELATION id=rel-", StringComparison.Ordinal),
                "A session without link.create mutated a relation.");
            var noEndpointRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-relation-create", "--agent-pipe", fixture.AgentPipe,
                "--session-file", noEndpointReadSession.SessionFile, "--input-file", relationDraftPath).ConfigureAwait(false);
            Require(noEndpointRead.ExitCode != 0 && noEndpointRead.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !noEndpointRead.StandardOutput.Contains("RELATION id=rel-", StringComparison.Ordinal),
                "A link.create-only grant created a relation without separate endpoint read rights.");

            var bridgeRelationDraft = S4T6RelationDraft("relatedTo", "proposed", S4T6IdeaTwo, "memoryItem", 1,
                S4T6UniqueResearch, "registeredSource", 1,
                "Related organization only, not evidentiary support.", S4T6BridgeRequest, now);
            var bridgeCreated = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_relation_create", new { sessionFile = fullSession.SessionFile, draft = bridgeRelationDraft }).ConfigureAwait(false);
            var bridgeRelationId = S4T6RelationIdFromOutput(bridgeCreated);
            Require(bridgeRelationId.StartsWith("rel-", StringComparison.Ordinal) &&
                    bridgeCreated.Contains("kind=relatedTo", StringComparison.Ordinal) &&
                    bridgeCreated.Contains("state=proposed", StringComparison.Ordinal) &&
                    !bridgeCreated.Contains("kind=supports", StringComparison.Ordinal) &&
                    !bridgeCreated.Contains(S4T6BridgeRequest, StringComparison.Ordinal) &&
                    !bridgeCreated.Contains("mb1_", StringComparison.Ordinal) && !bridgeCreated.Contains("mbs1_", StringComparison.Ordinal),
                "The actual MCP bridge did not create an explicitly related-not-supports relation (result=" + bridgeCreated.Trim() + ").");
            const string apiRequest = "Synthetic direct API relation request; related, not evidentiary support.";
            var apiRelationDraft = S4T6RelationDraft("relatedTo", "accepted", S4T6Revision, "memoryItem", 1,
                S4T6UniqueResearch, "registeredSource", 1,
                "Direct service API relation fixture; no evidence assertion.", apiRequest, now);
            var apiDraftBytes = Encoding.UTF8.GetBytes(apiRelationDraft);
            JsonElement apiCreated;
            try
            {
                apiCreated = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = "agent.relation.create",
                    sessionToken = ReadSessionBearer(fullSession.SessionFile),
                    accessOperation = "link.create",
                    contentBase64 = Convert.ToBase64String(apiDraftBytes)
                }).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(apiDraftBytes);
            }

            Require(ReplyCode(apiCreated) == "relation_created" &&
                    apiCreated.TryGetProperty("relation", out _),
                "The direct agent API did not create a relation.");
            var apiRelationWire = apiCreated.GetProperty("relation");
            Require(apiRelationWire.ValueKind == JsonValueKind.Object &&
                    apiRelationWire.GetProperty("kind").GetString() == "relatedTo" &&
                    apiRelationWire.GetProperty("state").GetString() == "proposed" &&
                    apiRelationWire.GetProperty("executor").GetString() == "agent" &&
                    apiRelationWire.GetProperty("creationMethod").GetString() == "paraphrase" &&
                    !apiCreated.GetRawText().Contains(apiRequest, StringComparison.Ordinal),
                "The direct agent API did not stamp a proposed typed relation or exposed private request evidence.");
            var apiRelationId = apiRelationWire.GetProperty("id").GetString() ?? string.Empty;

            var apiResolve = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.relation.resolve",
                sessionToken = ReadSessionBearer(fullSession.SessionFile),
                accessOperation = "relation.list",
                query = S4T6UniqueTitle
            }).ConfigureAwait(false);
            Require(ReplyCode(apiResolve) == "relation_resolved" &&
                    apiResolve.GetProperty("relationCandidates")[0].GetProperty("resourceId").GetString() == S4T6UniqueResearch,
                "The direct agent API did not resolve the authorized unique endpoint.");
            var apiList = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.relation.list",
                sessionToken = ReadSessionBearer(fullSession.SessionFile),
                accessOperation = "relation.list"
            }).ConfigureAwait(false);
            Require(ReplyCode(apiList) == "relations" &&
                    apiList.GetProperty("relations").GetArrayLength() == 3 &&
                    !apiList.GetRawText().Contains(S4T6CrossRelation, StringComparison.Ordinal) &&
                    !apiList.GetRawText().Contains(S4T6CrossZone, StringComparison.Ordinal) &&
                    !apiList.GetRawText().Contains(apiRequest, StringComparison.Ordinal),
                "The direct agent API list returned hidden endpoint metadata or caller-reported text.");

            var bridgeList = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_relation_list", new { sessionFile = fullSession.SessionFile }).ConfigureAwait(false);
            Require(bridgeList.Contains("RELATIONS count=3", StringComparison.Ordinal) &&
                    bridgeList.Contains(relationId, StringComparison.Ordinal) &&
                    bridgeList.Contains(bridgeRelationId, StringComparison.Ordinal) &&
                    bridgeList.Contains(apiRelationId, StringComparison.Ordinal) &&
                    !bridgeList.Contains(S4T6CrossRelation, StringComparison.Ordinal) &&
                    !bridgeList.Contains(S4T6CrossZone, StringComparison.Ordinal),
                "The MCP relation.list tool did not apply both-endpoint filtering.");

            var crossDraft = S4T6RelationDraft("relatedTo", "proposed", S4T6Idea, "memoryItem", 1,
                S4T6CrossZone, "registeredSource", 1,
                "Cross-zone relation attempt must not disclose the endpoint.", "Synthetic explicit cross-zone request.", now);
            var crossDenied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.relation.create",
                sessionToken = ReadSessionBearer(crossZoneSession.SessionFile),
                accessOperation = "link.create",
                contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(crossDraft))
            }).ConfigureAwait(false);
            var crossDeniedText = crossDenied.GetRawText();
            Require(crossDenied.TryGetProperty("error", out var crossError) &&
                    crossError.GetString() == "resource_unavailable" &&
                    !crossDeniedText.Contains(S4T6CrossZone, StringComparison.Ordinal) &&
                    !crossDeniedText.Contains("Cross-Zone Sentinel", StringComparison.Ordinal) &&
                    !crossDeniedText.Contains("zone-beta", StringComparison.Ordinal),
                "The relation API did not fail closed for the unapproved cross-zone endpoint (reply=" + crossDeniedText + ").");

            var supersedeDraft = S4T6RelationDraft("supersedes", "accepted", S4T6Old, "memoryItem", 1,
                S4T6New, "memoryItem", 1,
                "Synthetic replacement link; neither memory item is rewritten.", S4T6SupersedeRequest, now);
            var supersedePath = await WriteS4T6DraftAsync(fixture, supersedeDraft).ConfigureAwait(false);
            var noSupersede = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-relation-supersede", "--agent-pipe", fixture.AgentPipe,
                "--session-file", noSupersedeSession.SessionFile, "--input-file", supersedePath).ConfigureAwait(false);
            Require(noSupersede.ExitCode != 0 && noSupersede.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !noSupersede.StandardOutput.Contains("RELATION id=rel-", StringComparison.Ordinal),
                "A link.create grant was treated as link.supersede authority.");
            var superseded = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-relation-supersede", "--agent-pipe", fixture.AgentPipe,
                "--session-file", fullSession.SessionFile, "--input-file", supersedePath).ConfigureAwait(false);
            var supersedeId = S4T6RelationIdFromOutput(superseded.StandardOutput);
            Require(superseded.ExitCode == 0 && supersedeId.StartsWith("rel-", StringComparison.Ordinal) &&
                    superseded.StandardOutput.Contains("kind=supersedes", StringComparison.Ordinal) &&
                    superseded.StandardOutput.Contains("state=proposed", StringComparison.Ordinal) &&
                    superseded.StandardOutput.Contains("executor=agent", StringComparison.Ordinal),
                "The explicit link.supersede command did not create a proposed typed replacement relation (stdout=" + superseded.StandardOutput.Trim() + "; stderr=" + superseded.StandardError.Trim() + ").");
            var mcpSupersedeDenied = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_relation_supersede", new { sessionFile = noSupersedeSession.SessionFile, draft = supersedeDraft }).ConfigureAwait(false);
            Require(mcpSupersedeDenied.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !mcpSupersedeDenied.Contains("RELATION id=rel-", StringComparison.Ordinal),
                "The MCP relation.supersede path treated link.create as supersede authority.");
            var mcpSuperseded = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_relation_supersede", new { sessionFile = fullSession.SessionFile, draft = supersedeDraft }).ConfigureAwait(false);
            var mcpSupersedeId = S4T6RelationIdFromOutput(mcpSuperseded);
            Require(mcpSupersedeId.StartsWith("rel-", StringComparison.Ordinal) &&
                    mcpSupersedeId != supersedeId &&
                    mcpSuperseded.Contains("kind=supersedes", StringComparison.Ordinal) &&
                    mcpSuperseded.Contains("state=proposed", StringComparison.Ordinal) &&
                    mcpSuperseded.Contains("executor=agent", StringComparison.Ordinal),
                "The MCP relation.supersede path did not create a separate proposed replacement relation (reply=" + mcpSuperseded.Trim() + ").");


            var removeDraftPath = await WriteS4T6DraftAsync(fixture, S4T6TransitionDraft(
                "Synthetic unlink rationale; reversible and relation-only.", S4T6RemoveRequest)).ConfigureAwait(false);
            var removeDenied = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-relation-unlink", "--agent-pipe", fixture.AgentPipe,
                "--session-file", noCreateSession.SessionFile,
                "--resource-id", relationId, "--resource-revision", "1", "--input-file", removeDraftPath).ConfigureAwait(false);
            Require(removeDenied.ExitCode != 0 && removeDenied.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "A session without link.remove unlinked a relation.");

            var unlinked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-relation-unlink", "--agent-pipe", fixture.AgentPipe,
                "--session-file", fullSession.SessionFile,
                "--resource-id", relationId, "--resource-revision", "1", "--input-file", removeDraftPath).ConfigureAwait(false);
            Require(unlinked.ExitCode == 0 && unlinked.StandardOutput.Contains("state=retracted", StringComparison.Ordinal) &&
                    unlinked.StandardOutput.Contains("revision=2", StringComparison.Ordinal),
                "The real agent CLI did not record the reversible unlink (stdout=" + unlinked.StandardOutput.Trim() + "; stderr=" + unlinked.StandardError.Trim() + ").");

            var restoreDraft = S4T6TransitionDraft(null, S4T6RestoreRequest);
            var restoreDenied = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_relation_restore", new
                {
                    sessionFile = removeOnlySession.SessionFile,
                    resourceId = relationId,
                    resourceRevision = "2",
                    draft = restoreDraft
                }).ConfigureAwait(false);
            Require(restoreDenied.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !restoreDenied.Contains("state=proposed", StringComparison.Ordinal),
                "link.remove authority was mistaken for explicit link.restore authority.");

            var restored = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_relation_restore", new
                {
                    sessionFile = fullSession.SessionFile,
                    resourceId = relationId,
                    resourceRevision = "2",
                    draft = restoreDraft
                }).ConfigureAwait(false);
            Require(restored.Contains($"RELATION id={relationId}", StringComparison.Ordinal) &&
                    restored.Contains("state=proposed", StringComparison.Ordinal) &&
                    restored.Contains("revision=3", StringComparison.Ordinal),
                "The MCP bridge undo did not restore the relation to proposed (never accepted).");

            var postCreateList = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-relation-list", "--agent-pipe", fixture.AgentPipe,
                "--session-file", fullSession.SessionFile).ConfigureAwait(false);
            Require(postCreateList.ExitCode == 0 && postCreateList.StandardOutput.Contains("RELATIONS count=5", StringComparison.Ordinal) &&
                    postCreateList.StandardOutput.Contains(relationId, StringComparison.Ordinal) &&
                    postCreateList.StandardOutput.Contains(bridgeRelationId, StringComparison.Ordinal) &&
                    postCreateList.StandardOutput.Contains(apiRelationId, StringComparison.Ordinal) &&
                    postCreateList.StandardOutput.Contains(supersedeId, StringComparison.Ordinal) &&
                    postCreateList.StandardOutput.Contains(mcpSupersedeId, StringComparison.Ordinal) &&
                    !postCreateList.StandardOutput.Contains(S4T6CrossZone, StringComparison.Ordinal),
                "Relation listing changed records or disclosed the unapproved cross-zone link (stdout=" + postCreateList.StandardOutput.Trim() + ").");

            var staleRevisionDraft = S4T6RelationDraft("relatedTo", "proposed", S4T6Revision, "memoryItem", 2,
                S4T6UniqueResearch, "registeredSource", 1,
                "A new revision requires a fresh owner grant.", "Synthetic stale-grant attempt.", now);
            var revised = await ReviseS4T6ItemAsync(connectionsExecutable, fixture, S4T6Revision,
                S4T6ItemDraftJson("Synthetic S4-T6 replacement revision title", "Synthetic S4-T6 replacement revision body.", now.AddMinutes(2))).ConfigureAwait(false);
            Require(revised.ExitCode == 0 && revised.StandardOutput.Contains("MEMORY stored revision=2", StringComparison.Ordinal),
                "The owner did not revise the synthetic scoped item.");
            var staleResolve = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_relation_resolve", new { sessionFile = fullSession.SessionFile, query = "Synthetic S4-T6 frozen revision label" }).ConfigureAwait(false);
            Require(staleResolve.Contains("relation_no_match", StringComparison.Ordinal) &&
                    !staleResolve.Contains(S4T6Revision, StringComparison.Ordinal),
                "Endpoint resolution widened a frozen grant to a new memory revision.");
            var staleRevisionPath = await WriteS4T6DraftAsync(fixture, staleRevisionDraft).ConfigureAwait(false);
            var staleCreate = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-relation-create", "--agent-pipe", fixture.AgentPipe,
                "--session-file", fullSession.SessionFile, "--input-file", staleRevisionPath).ConfigureAwait(false);
            Require(staleCreate.ExitCode != 0 && staleCreate.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !staleCreate.StandardOutput.Contains("RELATION id=rel-", StringComparison.Ordinal),
                "A relation write used a newer, unapproved endpoint revision.");
            var staleRelationList = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-relation-list", "--agent-pipe", fixture.AgentPipe,
                "--session-file", fullSession.SessionFile).ConfigureAwait(false);
            Require(staleRelationList.ExitCode == 0 &&
                    staleRelationList.StandardOutput.Contains("RELATIONS count=5", StringComparison.Ordinal) &&
                    staleRelationList.StandardOutput.Contains(apiRelationId, StringComparison.Ordinal) &&
                    staleRelationList.StandardOutput.Contains(mcpSupersedeId, StringComparison.Ordinal) &&
                    staleRelationList.StandardOutput.Contains($"from resource={S4T6Revision} kind=memoryItem revision=1", StringComparison.Ordinal),
                "A relatedTo relation listing changed its pinned endpoint revision after the item changed.");

            var ownerReadBeforeRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "relation-read", "--control-pipe", fixture.ControlPipe, "--resource-id", relationId).ConfigureAwait(false);
            Require(ownerReadBeforeRestart.ExitCode == 0 &&
                    ownerReadBeforeRestart.StandardOutput.Contains("request action=create executor=agent", StringComparison.Ordinal) &&
                    ownerReadBeforeRestart.StandardOutput.Contains(S4T6UniqueRequest, StringComparison.Ordinal) &&
                    ownerReadBeforeRestart.StandardOutput.Contains("request action=remove executor=agent", StringComparison.Ordinal) &&
                    ownerReadBeforeRestart.StandardOutput.Contains(S4T6RemoveRequest, StringComparison.Ordinal) &&
                    ownerReadBeforeRestart.StandardOutput.Contains("request action=restore executor=agent", StringComparison.Ordinal) &&
                    ownerReadBeforeRestart.StandardOutput.Contains(S4T6RestoreRequest, StringComparison.Ordinal) &&
                    ownerReadBeforeRestart.StandardOutput.Contains("state=proposed", StringComparison.Ordinal),
                "The owner did not see the encrypted caller-reported request evidence and reversible lifecycle history.");

            foreach (var sessionFile in new[]
                     {
                         fullSession.SessionFile, noCreateSession.SessionFile, noEndpointReadSession.SessionFile,
                         noListSession.SessionFile, removeOnlySession.SessionFile, crossZoneSession.SessionFile,
                         noSupersedeSession.SessionFile
                     })
            {
                VerifyOwnerHandoffAcl(sessionFile);
            }

            foreach (var path in Directory.GetFiles(fixture.VaultResourcesPath, "*", SearchOption.AllDirectories))
            {
                var persisted = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
                Require(!persisted.Contains(S4T6UniqueRequest, StringComparison.Ordinal) &&
                        !persisted.Contains(S4T6RemoveRequest, StringComparison.Ordinal) &&
                        !persisted.Contains(S4T6RestoreRequest, StringComparison.Ordinal) &&
                        !persisted.Contains(S4T6SupersedeRequest, StringComparison.Ordinal) &&
                        !persisted.Contains(apiRequest, StringComparison.Ordinal),
                    "Synthetic relation request evidence was persisted as plaintext outside the encrypted vault: " + Path.GetFileName(path));
            }

            var revokeSession = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "revoke-session", "--control-pipe", fixture.ControlPipe,
                "--session-id", noCreateSession.SessionId).ConfigureAwait(false);
            Require(revokeSession.ExitCode == 0, "The owner could not revoke the synthetic relation-list session.");
            var revokedList = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-relation-list", "--agent-pipe", fixture.AgentPipe,
                "--session-file", noCreateSession.SessionFile).ConfigureAwait(false);
            Require(revokedList.ExitCode != 0 && revokedList.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "A revoked session still listed relation metadata.");

            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The owner could not lock the synthetic relation vault.");
            var lockedList = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-relation-list", "--agent-pipe", fixture.AgentPipe,
                "--session-file", fullSession.SessionFile).ConfigureAwait(false);
            Require(lockedList.ExitCode != 0 && !lockedList.StandardOutput.Contains("RELATION id=", StringComparison.Ordinal),
                "A locked vault still served relation metadata.");

            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var lockedStatus = await ReadS4T5StatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false);
            Require(lockedStatus.Contains("vault=locked", StringComparison.Ordinal),
                "The restarted relation fixture did not return locked.");
            var lockedOwnerRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "relation-read", "--control-pipe", fixture.ControlPipe, "--resource-id", relationId).ConfigureAwait(false);
            Require(lockedOwnerRead.ExitCode != 0 && !lockedOwnerRead.StandardOutput.Contains("RELATION id=", StringComparison.Ordinal),
                "The locked restart served private relation evidence.");
            var reUnlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(reUnlocked.ExitCode == 0 && reUnlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The owner could not unlock the durable relation vault after restart.");
            var durableRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "relation-read", "--control-pipe", fixture.ControlPipe, "--resource-id", relationId).ConfigureAwait(false);
            Require(durableRead.ExitCode == 0 && durableRead.StandardOutput.Contains("revision=3", StringComparison.Ordinal) &&
                    durableRead.StandardOutput.Contains("state=proposed", StringComparison.Ordinal) &&
                    durableRead.StandardOutput.Contains(S4T6UniqueRequest, StringComparison.Ordinal) &&
                    durableRead.StandardOutput.Contains(S4T6RemoveRequest, StringComparison.Ordinal) &&
                    durableRead.StandardOutput.Contains(S4T6RestoreRequest, StringComparison.Ordinal),
                "Relation state or request evidence did not survive locked restart/unlock.");
            var durableApiRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "relation-read", "--control-pipe", fixture.ControlPipe, "--resource-id", apiRelationId).ConfigureAwait(false);
            Require(durableApiRead.ExitCode == 0 &&
                    durableApiRead.StandardOutput.Contains("request action=create executor=agent", StringComparison.Ordinal) &&
                    durableApiRead.StandardOutput.Contains(apiRequest, StringComparison.Ordinal),
                "The owner could not read the direct API relation's durable request evidence.");
            var durableSupersedeRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "relation-read", "--control-pipe", fixture.ControlPipe, "--resource-id", supersedeId).ConfigureAwait(false);
            Require(durableSupersedeRead.ExitCode == 0 &&
                    durableSupersedeRead.StandardOutput.Contains("request action=supersede executor=agent", StringComparison.Ordinal) &&
                    durableSupersedeRead.StandardOutput.Contains(S4T6SupersedeRequest, StringComparison.Ordinal),
                "The owner could not read the supersede request's durable agent provenance.");
            var oldSessionAfterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-relation-list", "--agent-pipe", fixture.AgentPipe,
                "--session-file", fullSession.SessionFile).ConfigureAwait(false);
            Require(oldSessionAfterRestart.ExitCode != 0 && oldSessionAfterRestart.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "A pre-restart session was resurrected after unlock.");

            var unchangedItem = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S4T6Idea).ConfigureAwait(false);
            Require(unchangedItem.ExitCode == 0 && unchangedItem.StandardOutput.Contains("revision=1", StringComparison.Ordinal) &&
                    unchangedItem.StandardOutput.Contains("Synthetic S4-T6 idea body", StringComparison.Ordinal),
                "Relation creation/removal/supersession rewrote its endpoint memory.");

            Console.WriteLine("PASS relation.resolve returns only authorized exact-revision titles and flags ambiguous targets without mutation");
            Console.WriteLine("PASS relatedTo stays distinct from supports; CLI and MCP create proposed links with server-stamped agent provenance");
            Console.WriteLine("PASS both endpoint read rights and the exact link action are required; cross-zone and out-of-scope revisions deny generically");
            Console.WriteLine("PASS unlink/relink records agent request evidence and undo restores proposed state without mutating endpoints");
            Console.WriteLine("PASS lock, revoke and restart deny stale sessions while relation changes and audit evidence remain encrypted and durable");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    internal static async Task RunS4T6OfficialClientFixtureAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S4-T6 official-client fixture requires Windows named pipes and DPAPI.");
        }

        using var fixture = SmokeFixture.CreateUninitializedAt(
            Path.Combine(SmokeFixture.FindRepositoryRoot(), "artifacts", "sprint-4", ".s4t6-client-" + Guid.NewGuid().ToString("N")));
        RunningService? service = null;
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            string passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The synthetic S4-T6 official-client fixture could not provision its vault.");
            var unlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S4-T6 official-client fixture could not unlock its vault.");
            passphrase = string.Empty;

            var now = DateTimeOffset.UtcNow.AddMinutes(-5);
            await WriteS4T6ResourceAsync(connectionsExecutable, fixture, S4T6Idea, S4T6ZoneAlpha,
                S4T6ItemDraftJson("Synthetic S4-T6 current idea", "Synthetic S4-T6 idea body; no real owner content.", now),
                "memory-create", "MEMORY").ConfigureAwait(false);
            await WriteS4T6ResourceAsync(connectionsExecutable, fixture, S4T6UniqueResearch, S4T6ZoneAlpha,
                S4T6SourceDraftJson(S4T6UniqueTitle, "s4t6-client-private-locator-unique", now),
                "source-create", "SOURCE").ConfigureAwait(false);
            await WriteS4T6ResourceAsync(connectionsExecutable, fixture, S4T6AmbiguousOne, S4T6ZoneAlpha,
                S4T6SourceDraftJson("Synthetic Research Ambiguous Alpha", "s4t6-client-private-locator-ambiguous-one", now),
                "source-create", "SOURCE").ConfigureAwait(false);
            await WriteS4T6ResourceAsync(connectionsExecutable, fixture, S4T6AmbiguousTwo, S4T6ZoneAlpha,
                S4T6SourceDraftJson("Synthetic Research Ambiguous Beta", "s4t6-client-private-locator-ambiguous-two", now),
                "source-create", "SOURCE").ConfigureAwait(false);
            await WriteS4T6ResourceAsync(connectionsExecutable, fixture, S4T6CrossZone, S4T6ZoneBeta,
                S4T6SourceDraftJson("Synthetic Research Cross-Zone Sentinel", "s4t6-client-private-locator-cross", now),
                "source-create", "SOURCE").ConfigureAwait(false);

            var fullSession = await IssueS4T6GrantAsync(connectionsExecutable, fixture, "s4t6-client-full",
                "relation.list,memory.read,source.read,link.create,link.remove,link.restore",
                new[] { S4T6Idea, S4T6AmbiguousOne, S4T6AmbiguousTwo },
                new[] { S4T6UniqueResearch }).ConfigureAwait(false);
            var crossZoneSession = await IssueS4T6GrantAsync(connectionsExecutable, fixture, "s4t6-client-cross-zone-deny",
                "memory.read,source.read,link.create", new[] { S4T6Idea }, new[] { S4T6UniqueResearch }).ConfigureAwait(false);
            VerifyOwnerHandoffAcl(fullSession.SessionFile);
            VerifyOwnerHandoffAcl(crossZoneSession.SessionFile);

            var fixtureInfo = new
            {
                controlPipe = fixture.ControlPipe,
                agentPipe = fixture.AgentPipe,
                ideaId = S4T6Idea,
                uniqueResearchId = S4T6UniqueResearch,
                uniqueResearchTitle = S4T6UniqueTitle,
                ambiguousQuery = "Synthetic Research Ambiguous",
                crossZoneId = S4T6CrossZone,
                fullSessionFile = fullSession.SessionFile,
                crossZoneSessionFile = crossZoneSession.SessionFile
            };
            var fixtureInfoPath = await WriteS4T6DraftAsync(fixture, JsonSerializer.Serialize(fixtureInfo)).ConfigureAwait(false);
            Console.WriteLine("S4T6_CLIENT_FIXTURE_READY " +
                Path.GetRelativePath(SmokeFixture.FindRepositoryRoot(), fixtureInfoPath).Replace(Path.DirectorySeparatorChar, '/'));
            Console.WriteLine("Keep this process running while both official clients exercise only the synthetic fixture.");
            await Console.In.ReadLineAsync().ConfigureAwait(false);

            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The synthetic S4-T6 official-client fixture could not lock its vault during cleanup.");
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            Console.WriteLine("S4T6_CLIENT_FIXTURE_STOPPED");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteS4T6ResourceAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string zoneId,
        string draftJson,
        string command,
        string marker)
    {
        var input = await WriteS4T6DraftAsync(fixture, draftJson).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", command, "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"{marker} stored revision=1", StringComparison.Ordinal),
            $"S4-T6 did not store {resourceId} at revision 1 (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
    }

    private static async Task<CommandResult> ReviseS4T6ItemAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string draftJson)
    {
        var input = await WriteS4T6DraftAsync(fixture, draftJson).ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "memory-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", S4T6ZoneAlpha,
            "--expected-revision", "1", "--input-file", input).ConfigureAwait(false);
    }

    private static async Task<string> WriteS4T6DraftAsync(SmokeFixture fixture, string content)
    {
        var path = Path.Combine(fixture.OutputDirectory, "s4t6-draft-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false)).ConfigureAwait(false);
        return path;
    }

    private static async Task<S4T6Session> IssueS4T6GrantAsync(
        string executable,
        SmokeFixture fixture,
        string name,
        string operations,
        string[] resourceIds,
        string[] destinationIds)
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O", CultureInfo.InvariantCulture);
        var handoff = Path.Combine(fixture.OutputDirectory, name + ".grant-handoff");
        var sessionFile = Path.Combine(fixture.OutputDirectory, name + ".session-handoff");
        var args = new List<string>
        {
            "owner", "grant", "--control-pipe", fixture.ControlPipe,
            "--resource-ids", string.Join(",", resourceIds),
            "--operations", operations,
            "--expires-at-utc", expiry,
            "--handoff-file", handoff,
        };
        if (destinationIds.Length > 0)
        {
            args.Add("--destination-resource-ids");
            args.Add(string.Join(",", destinationIds));
        }

        var grant = await RunOwnerCommandWithInputAsync(executable, "ISSUE" + Environment.NewLine, args.ToArray()).ConfigureAwait(false);
        Require(grant.ExitCode == 0,
            $"The owner could not issue the synthetic S4-T6 grant {name} (exit={grant.ExitCode}; stdout={grant.StandardOutput.Trim()}; stderr={grant.StandardError.Trim()}).");
        VerifyOwnerHandoffAcl(handoff);
        var redeem = await RunOwnerCommandAsync(executable,
            "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
            "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
        Require(redeem.ExitCode == 0 && redeem.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
            $"Redemption failed for the synthetic S4-T6 grant {name} (exit={redeem.ExitCode}; stderr={redeem.StandardError.Trim()}).");
        VerifyOwnerHandoffAcl(sessionFile);
        return new S4T6Session(ParseSessionId(redeem.StandardOutput), sessionFile);
    }

    private static string S4T6RelationIdFromOutput(string output)
    {
        const string marker = "RELATION id=";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += marker.Length;
        var end = output.IndexOfAny([' ', '\r', '\n'], start);
        return (end < 0 ? output[start..] : output[start..end]).Trim();
    }

}
