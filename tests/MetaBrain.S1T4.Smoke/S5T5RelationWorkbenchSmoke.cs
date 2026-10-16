using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.OwnerUi;

namespace MetaBrain.S1T4.Smoke;

// S5-T5: the owner relations workbench. One synthetic vault is provisioned and
// unlocked through the real owner CLI; the links are created, inspected,
// confirmed, removed and undone through the console session the WPF window
// drives; the agent link is created by a real MCP client against the real stdio
// bridge under a real owner-approved grant; the cycle refusal, the revised
// evidence endpoint and the cross-zone visibility are the service's own
// answers. Nothing here is a mock, and no assertion is a source-text echo.
internal static partial class OwnerServiceSmoke
{
    private const string S5T5Zone = "s5t5-zone";
    private const string S5T5OtherZone = "s5t5-zone-other";
    private const string S5T5Alpha = "s5t5-item-alpha";
    private const string S5T5Beta = "s5t5-item-beta";
    private const string S5T5Gamma = "s5t5-item-gamma";
    private const string S5T5CrossSource = "s5t5-source-cross";
    private const string S5T5Marker = "s5t5-synthetic-relation-marker-7b21";
    private const string S5T5OwnerRelated = "rel-s5t5-owner-related";
    private const string S5T5OwnerSupports = "rel-s5t5-owner-supports";
    private const string S5T5CrossRelation = "rel-s5t5-owner-cross-zone";
    private const string S5T5CycleDerived = "rel-s5t5-cycle-derived";
    private const string S5T5CycleAttempt = "rel-s5t5-cycle-attempt";
    private const string S5T5CycleRelated = "rel-s5t5-cycle-related";
    private const string S5T5CrossTitle = "Synthetic S5-T5 cross-zone sentinel";
    private const string S5T5RelatedReason =
        "S5-T5 synthetic owner reason: the two notes belong together as an association, not as evidence";
    private const string S5T5SupportsReason =
        "S5-T5 synthetic owner reason: the alpha note is the basis the beta note is judged against";
    private const string S5T5CycleReason =
        "S5-T5 synthetic derivation claim: the beta note derives from the alpha note";
    private const string S5T5RemoveReason = "S5-T5 synthetic owner rationale: this association no longer holds";
    private const string S5T5AgentClaim =
        "nối ý này với ghi chú kia; chúng liên quan, không phải bằng chứng cho nhau";
    private const string S5T5AgentReason = "S5-T5 synthetic agent reason: both notes describe the same decision";

    private static string S5T5ItemDraft(string title, string body, DateTimeOffset now) =>
        S5T4ItemDraft("claim", "user", "directWrite", "unverified", "l1", title, body + " " + S5T5Marker, now);

    /// <summary>
    /// Runs the S5-T5 workbench scenario: owner links (related_to and supports),
    /// an agent-created link through the real MCP bridge, the real cycle refusal,
    /// a revised evidence endpoint, remove/undo, cross-zone visibility and the
    /// lock boundary.
    /// </summary>
    internal static async Task RunS5T5RelationsAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S5-T5 relation workbench smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S5-T5 explicit relations workbench: owner add/remove/inspect/undo, agent MCP link provenance, cycle and stale revision");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
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
                "Provision did not create the synthetic S5-T5 vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");

            var now = DateTimeOffset.UtcNow.AddMinutes(-5);
            await S5T4WriteItemAsync(connectionsExecutable, fixture.ControlPipe, fixture.OutputDirectory, S5T5Alpha, S5T5Zone,
                S5T5ItemDraft("Synthetic S5-T5 alpha claim", "S5-T5 synthetic alpha body: the earlier statement.", now)).ConfigureAwait(false);
            await S5T4WriteItemAsync(connectionsExecutable, fixture.ControlPipe, fixture.OutputDirectory, S5T5Beta, S5T5Zone,
                S5T5ItemDraft("Synthetic S5-T5 beta claim", "S5-T5 synthetic beta body: the statement under review.", now)).ConfigureAwait(false);
            await S5T4WriteItemAsync(connectionsExecutable, fixture.ControlPipe, fixture.OutputDirectory, S5T5Gamma, S5T5Zone,
                S5T5ItemDraft("Synthetic S5-T5 gamma claim", "S5-T5 synthetic gamma body: an unrelated note.", now)).ConfigureAwait(false);
            await WriteS4T6ResourceAsync(connectionsExecutable, fixture, S5T5CrossSource, S5T5OtherZone,
                S4T6SourceDraftJson(S5T5CrossTitle, "s5t5-private-locator-cross-zone", now),
                "source-create", "SOURCE").ConfigureAwait(false);

            var session = new OwnerConsoleSession(fixture.ControlPipe);
            await session.AttachAsync().ConfigureAwait(false);
            Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
                "The console session did not report the real unlocked vault (" + session.ReportedVaultState + ").");

            // The library index is what the endpoint search reads, exactly as the
            // Library view has to materialize it before a search is answered.
            await session.RebuildLibraryIndexAsync().ConfigureAwait(false);
            Require(session.LibraryIndex is not null, "The console did not materialize the library index (" + session.LibraryMessage + ").");
            await session.SearchRelationCandidatesAsync("Synthetic").ConfigureAwait(false);
            var alpha = session.RelationCandidates.FirstOrDefault(candidate => string.Equals(candidate.ResourceId, S5T5Alpha, StringComparison.Ordinal));
            var beta = session.RelationCandidates.FirstOrDefault(candidate => string.Equals(candidate.ResourceId, S5T5Beta, StringComparison.Ordinal));
            var gamma = session.RelationCandidates.FirstOrDefault(candidate => string.Equals(candidate.ResourceId, S5T5Gamma, StringComparison.Ordinal));
            Require(alpha is { Revision: 1, EndpointKind: "memoryItem", ZoneId: S5T5Zone } &&
                    beta is { Revision: 1, EndpointKind: "memoryItem" } &&
                    gamma is { Revision: 1, EndpointKind: "memoryItem" },
                "The service's own library search did not rank the three synthetic endpoints at their live revisions (" + session.RelationCandidateMessage + ").");
            Console.WriteLine("PASS the endpoint candidates come from the service's materialized library index with their live revisions and zones");

            // ---- owner adds a related_to association ----
            await session.CreateRelationAsync(S5T5OwnerRelated, S5T5Zone, "relatedTo", "accepted", alpha!, beta!, S5T5RelatedReason).ConfigureAwait(false);
            Require(session.OpenRelation is { } related &&
                    string.Equals(related.Kind, "relatedTo", StringComparison.Ordinal) &&
                    string.Equals(related.State, "accepted", StringComparison.Ordinal) &&
                    string.Equals(related.Executor, "owner", StringComparison.Ordinal) &&
                    string.Equals(related.CreationMethod, "directWrite", StringComparison.Ordinal) &&
                    string.Equals(related.Reason, S5T5RelatedReason, StringComparison.Ordinal) &&
                    related.Revision == 1 &&
                    string.Equals(related.From.ResourceId, S5T5Alpha, StringComparison.Ordinal) &&
                    string.Equals(related.To.ResourceId, S5T5Beta, StringComparison.Ordinal) &&
                    related.From.Revision == 1 && related.To.Revision == 1 &&
                    string.Equals(related.Review.State, "current", StringComparison.Ordinal),
                "The console did not store the owner's related_to link with its pinned endpoints and provenance (" + session.RelationMessage + ").");
            Require(session.Relations.Any(relation => string.Equals(relation.Id, S5T5OwnerRelated, StringComparison.Ordinal)),
                "The relation set the console re-read after the write does not list the stored link (" + session.RelationsMessage + ").");
            Console.WriteLine("PASS the owner links two items as related_to through the real service and both endpoints stay pinned at revision 1");

            // ---- an agent link through the real MCP bridge ----
            var agentSession = await IssueOwnerGrantAsync(connectionsExecutable, fixture.ControlPipe, fixture.AgentPipe,
                fixture.OutputDirectory, "s5t5-agent",
                "relation.list,memory.read,source.read,link.create,link.remove,link.restore",
                new[] { S5T5Alpha, S5T5Gamma }, new[] { S5T5Beta }).ConfigureAwait(false);
            var agentDraft = S5T5RelationDraft("relatedTo", S5T5Alpha, "memoryItem", 1, S5T5Beta, "memoryItem", 1,
                S5T5AgentReason, S5T5AgentClaim);
            var agentCreate = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_relation_create", new { sessionFile = agentSession.SessionFile, draft = agentDraft }).ConfigureAwait(false);
            var agentRelationId = S5T5RelationIdFromOutput(agentCreate);
            Require(agentRelationId.StartsWith("rel-", StringComparison.Ordinal) &&
                    agentCreate.Contains("kind=relatedTo", StringComparison.Ordinal) &&
                    agentCreate.Contains("state=proposed", StringComparison.Ordinal) &&
                    agentCreate.Contains("executor=agent", StringComparison.Ordinal) &&
                    agentCreate.Contains("method=paraphrase", StringComparison.Ordinal) &&
                    !agentCreate.Contains("state=accepted", StringComparison.Ordinal),
                "A real MCP client did not create a proposed agent link with server-stamped provenance (" + agentCreate.Trim() + ").");

            await session.RefreshRelationsAsync().ConfigureAwait(false);
            var agentRelation = session.Relations.FirstOrDefault(relation => string.Equals(relation.Id, agentRelationId, StringComparison.Ordinal));
            Require(agentRelation is not null &&
                    string.Equals(agentRelation.Executor, "agent", StringComparison.Ordinal) &&
                    string.Equals(agentRelation.State, "proposed", StringComparison.Ordinal) &&
                    string.Equals(agentRelation.StateExecutor, "agent", StringComparison.Ordinal) &&
                    agentRelation.RequestEvidence.Length == 1 &&
                    agentRelation.RequestEvidence[0] is { Action: "create", Executor: "agent", Text: S5T5AgentClaim } &&
                    string.Equals(agentRelation.Reason, S5T5AgentReason, StringComparison.Ordinal),
                "The owner's relation set did not show the agent-created link with its executor and reported claim (" + session.RelationsMessage + ").");
            Console.WriteLine("PASS a link created by a real MCP client appears in the owner relation set as an agent proposal with its reported words");

            await session.OpenRelationAsync(agentRelation!).ConfigureAwait(false);
            Require(session.OpenRelation is { State: "proposed" } openedAgent &&
                    string.Equals(openedAgent.From.ResourceId, S5T5Alpha, StringComparison.Ordinal) &&
                    string.Equals(openedAgent.To.ResourceId, S5T5Beta, StringComparison.Ordinal),
                "The console did not open the agent link at the exact revision the service served (" + session.RelationMessage + ").");

            // ---- the owner confirms the agent link ----
            await session.AcceptRelationAsync().ConfigureAwait(false);
            Require(session.OpenRelation is { } confirmedAgent &&
                    string.Equals(confirmedAgent.State, "accepted", StringComparison.Ordinal) &&
                    string.Equals(confirmedAgent.StateExecutor, "owner", StringComparison.Ordinal) &&
                    string.Equals(confirmedAgent.Executor, "agent", StringComparison.Ordinal) &&
                    confirmedAgent.RequestEvidence.Length == 1 &&
                    string.Equals(confirmedAgent.RequestEvidence[0].Text, S5T5AgentClaim, StringComparison.Ordinal) &&
                    confirmedAgent.Revision == 2,
                "The owner's confirmation did not record the acceptance without rewriting the agent's claim (" + session.RelationMessage + ").");
            Console.WriteLine("PASS confirming an agent proposal records the acceptance as the owner's act and keeps the agent claim unchanged");

            // ---- owner adds an evidence link, then the evidence endpoint moves ----
            await session.CreateRelationAsync(S5T5OwnerSupports, S5T5Zone, "supports", "accepted", alpha!, beta!, S5T5SupportsReason).ConfigureAwait(false);
            Require(session.OpenRelation is { Kind: "supports", State: "accepted" },
                "The console did not store the owner's supports link, which is a different record from related_to (" + session.RelationMessage + ").");
            var revised = await WriteS5T4ItemReviseAsync(connectionsExecutable, fixture, S5T5Beta, S5T5Zone,
                S5T5ItemDraft("Synthetic S5-T5 beta claim (revised)", "S5-T5 synthetic beta body after the revision.", now.AddMinutes(1)), 1).ConfigureAwait(false);
            Require(revised.ExitCode == 0 && revised.StandardOutput.Contains("MEMORY stored revision=2", StringComparison.Ordinal),
                "The synthetic evidence endpoint was not revised to revision 2 (exit=" + revised.ExitCode + "; stderr=" + revised.StandardError.Trim() + ").");

            await session.RefreshRelationsAsync().ConfigureAwait(false);
            var supportsRow = session.Relations.FirstOrDefault(relation => string.Equals(relation.Id, S5T5OwnerSupports, StringComparison.Ordinal));
            var relatedRow = session.Relations.FirstOrDefault(relation => string.Equals(relation.Id, S5T5OwnerRelated, StringComparison.Ordinal));
            Require(supportsRow is not null && relatedRow is not null,
                "The relation set did not serve both owner links after the revision (" + session.RelationsMessage + ").");

            await session.OpenRelationAsync(supportsRow!).ConfigureAwait(false);
            Require(session.OpenRelation is { } staleSupports &&
                    string.Equals(staleSupports.Review.State, "needsReview", StringComparison.Ordinal) &&
                    staleSupports.Review.Reasons.Contains("endpointRevisionChanged", StringComparer.Ordinal) &&
                    staleSupports.Review.StaleResourceIds.Contains(S5T5Beta, StringComparer.Ordinal) &&
                    staleSupports.To.Revision == 1,
                "The supports link did not report its changed evidence endpoint as needing review (" + session.RelationMessage + ").");

            await session.OpenRelationAsync(relatedRow!).ConfigureAwait(false);
            Require(session.OpenRelation is { } stillCurrent &&
                    string.Equals(stillCurrent.Review.State, "current", StringComparison.Ordinal) &&
                    stillCurrent.Review.StaleResourceIds.Length == 0 &&
                    stillCurrent.From.Revision == 1 && stillCurrent.To.Revision == 1,
                "A related_to association was flagged for a revision change it does not pin, so related_to and supports were conflated (" + session.RelationMessage + ").");
            Console.WriteLine("PASS a revised evidence endpoint marks the supports link for review while the related_to association stays current");

            // ---- derivation cycles are refused, related_to cycles are not ----
            // The library index is behind the revision this scenario just
            // wrote, so the workbench search is refused until the owner
            // materializes a new generation — the same rule the Library view
            // enforces, on the same materialized index.
            await session.SearchRelationCandidatesAsync("Synthetic").ConfigureAwait(false);
            Require(session.RelationCandidateMessage is { } staleIndex &&
                    staleIndex.Contains("search_index_stale", StringComparison.Ordinal) &&
                    session.RelationCandidates.Length == 0,
                "The endpoint search was answered from a library index that no longer matched the vault (" + session.RelationCandidateMessage + ").");
            await session.RebuildLibraryIndexAsync().ConfigureAwait(false);
            await session.SearchRelationCandidatesAsync("Synthetic").ConfigureAwait(false);
            var alphaLive = session.RelationCandidates.FirstOrDefault(candidate => string.Equals(candidate.ResourceId, S5T5Alpha, StringComparison.Ordinal));
            var betaLive = session.RelationCandidates.FirstOrDefault(candidate => string.Equals(candidate.ResourceId, S5T5Beta, StringComparison.Ordinal));
            Require(alphaLive is { Revision: 1 } && betaLive is { Revision: 2 },
                "The endpoint search did not report the revised endpoint at its live revision (" + session.RelationCandidateMessage + ").");

            // A link that pins the revision the endpoint no longer has is refused
            // by the shared validator.
            await session.CreateRelationAsync(S5T5CycleDerived, S5T5Zone, "derivedFrom", "accepted", alpha!, beta!, S5T5CycleReason).ConfigureAwait(false);
            Require(session.RelationMessage is { } stalePinMessage &&
                    stalePinMessage.Contains("invalid_request", StringComparison.Ordinal),
                "A link pinning a revision the endpoint no longer has was accepted (" + session.RelationMessage + ").");
            var stalePinRead = await OwnerChannelClient.ReadRelationAsync(fixture.ControlPipe, S5T5CycleDerived, null, CancellationToken.None).ConfigureAwait(false);
            Require(stalePinRead.Outcome == OwnerCommandOutcome.Refused,
                "A stale-pinned link was still written (" + stalePinRead.Outcome + "/" + stalePinRead.ErrorCode + ").");

            await session.CreateRelationAsync(S5T5CycleDerived, S5T5Zone, "derivedFrom", "accepted", alphaLive!, betaLive!, S5T5CycleReason).ConfigureAwait(false);
            Require(session.OpenRelation is { Kind: "derivedFrom", State: "accepted" },
                "The console did not store the first derivation link (" + session.RelationMessage + ").");
            await session.CreateRelationAsync(S5T5CycleAttempt, S5T5Zone, "derivedFrom", "accepted", betaLive!, alphaLive!, S5T5CycleReason).ConfigureAwait(false);
            Require(session.RelationMessage is { } cycleMessage &&
                    cycleMessage.Contains("relation_cycle", StringComparison.Ordinal) &&
                    session.OpenRelation is { Id: S5T5CycleDerived },
                "The console did not report the service's own cycle refusal while keeping the stored graph (" + session.RelationMessage + ").");
            var cycleAttemptRead = await OwnerChannelClient.ReadRelationAsync(fixture.ControlPipe, S5T5CycleAttempt, null, CancellationToken.None).ConfigureAwait(false);
            Require(cycleAttemptRead.Outcome == OwnerCommandOutcome.Refused,
                "A refused derivation cycle still wrote a relation (" + cycleAttemptRead.Outcome + "/" + cycleAttemptRead.ErrorCode + ").");
            await session.CreateRelationAsync(S5T5CycleRelated, S5T5Zone, "relatedTo", "accepted", betaLive!, alphaLive!, S5T5RelatedReason).ConfigureAwait(false);
            Require(session.OpenRelation is { Kind: "relatedTo", State: "accepted" },
                "A related_to link formed from the same endpoints was not accepted, so the cycle rule is not kind-specific (" + session.RelationMessage + ").");
            Console.WriteLine("PASS the service refuses a real derivation cycle and still accepts the same endpoints as related_to");

            // ---- remove and undo through the shared lifecycle ----
            await session.RefreshRelationsAsync().ConfigureAwait(false);
            var removeTarget = session.Relations.First(relation => string.Equals(relation.Id, S5T5OwnerRelated, StringComparison.Ordinal));
            await session.OpenRelationAsync(removeTarget).ConfigureAwait(false);
            await session.UnlinkRelationAsync("   ").ConfigureAwait(false);
            Require(session.RelationMessage is { } rationaleRequired &&
                    rationaleRequired.Contains("rationale", StringComparison.Ordinal) &&
                    session.OpenRelation is { State: "accepted", Revision: 1 },
                "A removal without a rationale was sent instead of being refused with an explanation (" + session.RelationMessage + ").");

            await session.UnlinkRelationAsync(S5T5RemoveReason).ConfigureAwait(false);
            Require(session.OpenRelation is { State: "retracted", Revision: 2 } removed &&
                    string.Equals(removed.StateReason, S5T5RemoveReason, StringComparison.Ordinal) &&
                    string.Equals(removed.StateExecutor, "owner", StringComparison.Ordinal),
                "The console did not remove the link with the owner's recorded rationale (" + session.RelationMessage + ").");
            await session.RelinkRelationAsync().ConfigureAwait(false);
            Require(session.OpenRelation is { State: "proposed", Revision: 3 } relinked &&
                    relinked.StateReason is null &&
                    string.Equals(relinked.Reason, S5T5RelatedReason, StringComparison.Ordinal),
                "Undoing a removal did not return the link to proposed while keeping its original reason (" + session.RelationMessage + ").");
            Console.WriteLine("PASS removing a link records the owner rationale and undo returns it to proposed, never to accepted");

            // ---- cross-zone visibility: the owner sees the link, the agent does not ----
            var crossDraft = S5T5RelationDraft("relatedTo", S5T5Alpha, "memoryItem", 1, S5T5CrossSource, "registeredSource", 1,
                "S5-T5 synthetic owner reason: the cross-zone link is the owner's own record.", null);
            var crossPath = await WriteS4T6DraftAsync(fixture, crossDraft).ConfigureAwait(false);
            var crossCreated = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "relation-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S5T5CrossRelation, "--zone-id", S5T5Zone,
                "--input-file", crossPath).ConfigureAwait(false);
            Require(crossCreated.ExitCode == 0 && crossCreated.StandardOutput.Contains("RELATION stored revision=1", StringComparison.Ordinal),
                "The owner could not create the cross-zone link (exit=" + crossCreated.ExitCode + "; stderr=" + crossCreated.StandardError.Trim() + ").");

            await session.RefreshRelationsAsync().ConfigureAwait(false);
            Require(session.Relations.Any(relation => string.Equals(relation.Id, S5T5CrossRelation, StringComparison.Ordinal)),
                "The owner relation set did not list the owner's cross-zone link (" + session.RelationsMessage + ").");

            var agentList = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_relation_list", new { sessionFile = agentSession.SessionFile }).ConfigureAwait(false);
            Require(agentList.Contains("RELATIONS count=", StringComparison.Ordinal) &&
                    !agentList.Contains(S5T5CrossRelation, StringComparison.Ordinal) &&
                    !agentList.Contains(S5T5CrossSource, StringComparison.Ordinal) &&
                    !agentList.Contains(S5T5CrossTitle, StringComparison.Ordinal),
                "The scoped agent relation list disclosed an endpoint outside the session's grant (" + agentList.Trim() + ").");

            var agentResolve = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_relation_resolve", new { sessionFile = agentSession.SessionFile, query = S5T5CrossTitle }).ConfigureAwait(false);
            Require(agentResolve.Contains("relation_no_match", StringComparison.Ordinal) &&
                    !agentResolve.Contains(S5T5CrossSource, StringComparison.Ordinal),
                "The scoped endpoint resolution disclosed a cross-zone endpoint (" + agentResolve.Trim() + ").");

            var agentCrossDraft = S5T5RelationDraft("relatedTo", S5T5Alpha, "memoryItem", 1, S5T5CrossSource, "registeredSource", 1,
                "S5-T5 synthetic agent reason: link the alpha note to the cross-zone source.",
                "S5-T5 synthetic agent request: please connect the alpha note with the cross-zone sentinel");
            var agentCrossCreate = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_relation_create", new { sessionFile = agentSession.SessionFile, draft = agentCrossDraft }).ConfigureAwait(false);
            Require(agentCrossCreate.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !agentCrossCreate.Contains(S5T5CrossRelation, StringComparison.Ordinal) &&
                    !agentCrossCreate.Contains(S5T5CrossSource, StringComparison.Ordinal) &&
                    !agentCrossCreate.Contains(S5T5CrossTitle, StringComparison.Ordinal),
                "A cross-zone create was not refused generically, or the refusal disclosed the denied endpoint (" + agentCrossCreate.Trim() + ").");

            var agentListAfter = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_relation_list", new { sessionFile = agentSession.SessionFile }).ConfigureAwait(false);
            Require(!agentListAfter.Contains(S5T5CrossRelation, StringComparison.Ordinal),
                "The denied cross-zone create still added a relation to the agent's own view (" + agentListAfter.Trim() + ").");

            var agentOwnerList = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.OwnerRelationListOperation,
                isOwner = true,
                sessionToken = agentSession.SessionId,
                resourceId = S5T5Alpha
            }).ConfigureAwait(false);
            Require(ReplyCode(agentOwnerList) == "forbidden" && !agentOwnerList.GetRawText().Contains(S5T5CrossRelation, StringComparison.Ordinal),
                "The agent channel served the owner relation list (" + ReplyCode(agentOwnerList) + ").");
            Console.WriteLine("PASS the owner sees the whole vault while the agent session sees neither the cross-zone link nor its endpoint");

            var ownerCliList = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "relation-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ownerCliList.ExitCode == 0 &&
                    ownerCliList.StandardOutput.Contains("RELATIONS count=", StringComparison.Ordinal) &&
                    ownerCliList.StandardOutput.Contains(S5T5CrossRelation, StringComparison.Ordinal) &&
                    ownerCliList.StandardOutput.Contains(S5T5OwnerSupports, StringComparison.Ordinal),
                "The owner CLI relation list did not serve the owner's whole relation set (exit=" + ownerCliList.ExitCode +
                "; stdout=" + ownerCliList.StandardOutput.Trim() + "; stderr=" + ownerCliList.StandardError.Trim() + ").");

            // ---- the lock boundary ----
            var epochBeforeLock = session.PrivateEpoch;
            var formVersionBeforeLock = session.RelationFormVersion;
            await session.LockAsync().ConfigureAwait(false);
            Require(session.Relations.Length == 0 && session.OpenRelation is null &&
                    session.RelationCandidates.Length == 0 &&
                    session.RelationsMessage is null && session.RelationMessage is null &&
                    session.RelationFormVersion > formVersionBeforeLock &&
                    session.PrivateEpoch != epochBeforeLock,
                "Locking did not clear every relation view, the candidate list and the owner's draft.");
            var lateAnswer = new OwnerRelationListResult(OwnerCommandOutcome.Completed, null, new[] { supportsRow! });
            Require(!session.ApplyRelationListAnswer(epochBeforeLock, lateAnswer) && session.Relations.Length == 0,
                "A late relation answer restored a view the lock had cleared.");
            var lockedRead = await OwnerChannelClient.ListRelationsAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(lockedRead.Outcome == OwnerCommandOutcome.Refused && lockedRead.ErrorCode == "vault_locked" && lockedRead.Relations is null,
                "A locked vault served the relation set (" + lockedRead.Outcome + "/" + lockedRead.ErrorCode + ").");
            await session.RefreshRelationsAsync().ConfigureAwait(false);
            Require(session.Relations.Length == 0 && session.RelationsMessage is null,
                "The locked console offered a relation act (" + session.RelationsMessage + ").");
            Console.WriteLine("PASS locking clears the relation views, the candidates and the draft, and a late answer cannot restore them");

            if (S5T4FixtureHoldsMarker(fixture))
            {
                throw new InvalidOperationException("A synthetic S5-T5 private body marker was persisted in plaintext under the fixture root.");
            }

            Console.WriteLine("PASS no synthetic relation content was persisted by the console in plaintext");
            Console.WriteLine("LIMIT synthetic S5-T5 fixture only; the links, endpoints, revisions and claims are synthetic and the official Codex/OMP client conversation is the separate runtime proof under artifacts/sprint-5/task-5-run01/; same-owner OS compromise remains outside the threat model.");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static string S5T5RelationDraft(
        string kind,
        string fromResourceId,
        string fromEndpointKind,
        long fromRevision,
        string toResourceId,
        string toEndpointKind,
        long toRevision,
        string reason,
        string? requestEvidence) => JsonSerializer.Serialize(new
    {
        kind,
        state = "proposed",
        from = new { resourceId = fromResourceId, endpointKind = fromEndpointKind, revision = fromRevision, span = (object?)null },
        to = new { resourceId = toResourceId, endpointKind = toEndpointKind, revision = toRevision, span = (object?)null },
        creationMethod = "directWrite",
        reason,
        stateReason = "agent-supplied state rationale must not survive creation",
        occurredAtUtc = DateTimeOffset.UtcNow.AddYears(-1),
        recordedAtUtc = DateTimeOffset.UtcNow.AddYears(-1).AddMinutes(1),
        requestEvidence
    });

    private static string S5T5RelationIdFromOutput(string output)
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

    /// <summary>
    /// Creates the synthetic owner-console fixture at an explicit root for the
    /// external WPF relations proof. The caller owns that root: it starts and
    /// stops the service, drives the console and removes the root afterwards, so
    /// this scenario disposes nothing and provisions no vault.
    /// </summary>
    internal static Task RunS5T5ConsoleFixtureAsync(string fixtureRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S5-T5 owner console fixture requires Windows named pipes.");
        }

        var fixture = SmokeFixture.CreateUninitializedAt(fixtureRoot);
        Console.WriteLine("S5T5 CONSOLE FIXTURE root=" + fixture.Root);
        Console.WriteLine("S5T5 CONSOLE FIXTURE settings=" + fixture.SettingsPath);
        Console.WriteLine("S5T5 CONSOLE FIXTURE controlPipe=" + fixture.ControlPipe);
        Console.WriteLine("S5T5 CONSOLE FIXTURE agentPipe=" + fixture.AgentPipe);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes the synthetic corpus the relations proof needs after the console
    /// provisioned the vault: the three memory items, the cross-zone registered
    /// source, the owner cross-zone link and the owner-approved agent grant with
    /// its DPAPI session handoff. The caller removes the fixture root afterwards.
    /// </summary>
    internal static async Task S5T5WriteConsoleCorpusAsync(string connectionsExecutable, string fixtureRoot)
    {
        var root = Path.GetFullPath(fixtureRoot);
        var settingsPath = Path.Combine(root, "service", "service-settings.json");
        using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var controlPipe = settings.RootElement.GetProperty("controlPipe").GetString()
            ?? throw new InvalidDataException("The console fixture settings carry no control pipe.");
        var agentPipe = settings.RootElement.GetProperty("agentPipe").GetString()
            ?? throw new InvalidDataException("The console fixture settings carry no agent pipe.");
        var output = Path.Combine(root, "output");
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);

        await S5T4WriteItemAsync(connectionsExecutable, controlPipe, output, S5T5Alpha, S5T5Zone,
            S5T5ItemDraft("Synthetic S5-T5 alpha claim", "S5-T5 synthetic alpha body: the earlier statement.", now)).ConfigureAwait(false);
        await S5T4WriteItemAsync(connectionsExecutable, controlPipe, output, S5T5Beta, S5T5Zone,
            S5T5ItemDraft("Synthetic S5-T5 beta claim", "S5-T5 synthetic beta body: the statement under review.", now)).ConfigureAwait(false);
        await S5T4WriteItemAsync(connectionsExecutable, controlPipe, output, S5T5Gamma, S5T5Zone,
            S5T5ItemDraft("Synthetic S5-T5 gamma claim", "S5-T5 synthetic gamma body: an unrelated note.", now)).ConfigureAwait(false);

        var sourceDraft = await WriteS5T5DraftAsync(output, "cross-source",
            S4T6SourceDraftJson(S5T5CrossTitle, "s5t5-private-locator-cross-zone", now)).ConfigureAwait(false);
        var sourceCreated = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "source-create", "--control-pipe", controlPipe,
            "--resource-id", S5T5CrossSource, "--zone-id", S5T5OtherZone, "--input-file", sourceDraft).ConfigureAwait(false);
        File.Delete(sourceDraft);
        Require(sourceCreated.ExitCode == 0 && sourceCreated.StandardOutput.Contains("SOURCE stored revision=1", StringComparison.Ordinal),
            "The S5-T5 console fixture could not store the cross-zone source (" + sourceCreated.StandardError.Trim() + ").");

        var crossDraft = await WriteS5T5DraftAsync(output, "cross-relation",
            S5T5RelationDraft("relatedTo", S5T5Alpha, "memoryItem", 1, S5T5CrossSource, "registeredSource", 1,
                "S5-T5 synthetic owner reason: the cross-zone link is the owner's own record.", null)).ConfigureAwait(false);
        var crossCreated = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "relation-create", "--control-pipe", controlPipe,
            "--resource-id", S5T5CrossRelation, "--zone-id", S5T5Zone, "--input-file", crossDraft).ConfigureAwait(false);
        File.Delete(crossDraft);
        Require(crossCreated.ExitCode == 0 && crossCreated.StandardOutput.Contains("RELATION stored revision=1", StringComparison.Ordinal),
            "The S5-T5 console fixture could not store the owner cross-zone link (" + crossCreated.StandardError.Trim() + ").");

        var agentSession = await IssueOwnerGrantAsync(connectionsExecutable, controlPipe, agentPipe, output, "s5t5-console-agent",
            "relation.list,memory.read,source.read,link.create,link.remove,link.restore",
            new[] { S5T5Alpha, S5T5Gamma }, new[] { S5T5Beta }).ConfigureAwait(false);

        Console.WriteLine("S5T5 CONSOLE CORPUS controlPipe=" + controlPipe + " agentPipe=" + agentPipe +
            " alpha=" + S5T5Alpha + " beta=" + S5T5Beta + " gamma=" + S5T5Gamma +
            " crossSource=" + S5T5CrossSource + " crossRelation=" + S5T5CrossRelation +
            " crossTitle=" + S5T5CrossTitle + " zone=" + S5T5Zone + " sessionFile=" + agentSession.SessionFile);
    }

    private static async Task<string> WriteS5T5DraftAsync(string outputDirectory, string name, string content)
    {
        var path = Path.Combine(outputDirectory, "s5t5-" + name + "-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false)).ConfigureAwait(false);
        return path;
    }
}
