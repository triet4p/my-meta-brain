using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Core.Domain;

namespace MetaBrain.S1T4.Smoke;

// S4-T4: scoped brief/context with an explicit character budget plus
// append-only feedback storage, through the existing core/application/
// connections boundaries. One synthetic owner vault is provisioned and
// unlocked through the real owner CLI over the real named-pipe service.
// Memory items cite a registered source revision; conflicting and stale
// entries carry honest availability warnings. An owner grant with
// memory.context plus memory.read opens the brief only; an explicit
// memory.feedback grant opens agent feedback on the seen targets; an
// explicit feedback.read grant opens stored feedback records. Every
// positive/negative scenario below runs through the real owner surface and
// the real agent surface (agent-brief/agent-feedback/agent-feedback-read
// plus the matching MCP bridge tools). All evidence is observed from the
// real service and real persisted files; nothing here is a mock.
internal static partial class OwnerServiceSmoke
{
    private const string S4T4Zone = "s4t4-private-zone";
    private const string S4T4ItemAlpha = "s4t4-item-alpha";
    private const string S4T4ItemBeta = "s4t4-item-beta";
    private const string S4T4ItemConflict = "s4t4-item-conflict";
    private const string S4T4ItemStale = "s4t4-item-stale";
    private const string S4T4ItemQuestion = "s4t4-item-question";
    private const string S4T4ItemOutside = "s4t4-item-outside";
    private const string S4T4Source = "s4t4-source-alpha";
    private const string S4T4GuessedItem = "s4t4-guessed-item";

    private static string S4T4ItemDraft(string title, string body, DateTimeOffset occurred, object? sources, string kind = "lesson", string author = "user", string evidence = "unverified") =>
        JsonSerializer.Serialize(new
        {
            kind,
            author,
            creationMethod = "directWrite",
            evidence,
            level = "l1",
            title,
            body,
            sources = sources ?? Array.Empty<object>(),
            occurredAtUtc = occurred,
            recordedAtUtc = occurred.AddMinutes(1),
            applicability = new { project = "synthetic-s4t4-project", environment = "synthetic-s4t4-env", version = "s4t4-v1" }
        });

    private static string S4T4SourceDraft(string title, string locator, string version, string availability, DateTimeOffset now) =>
        JsonSerializer.Serialize(new
        {
            title,
            locator,
            version,
            availability,
            creationMethod = "directWrite",
            capturedAtUtc = now,
            recordedAtUtc = now.AddMinutes(1)
        });

    private static string S4T4FeedbackDraft(string targetId, long targetRevision, string kind, string? note) =>
        JsonSerializer.Serialize(new
        {
            targetResourceId = targetId,
            targetRevision = targetRevision,
            kind,
            note,
            creationMethod = "directWrite"
        });

    internal static async Task RunS4T4BriefAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S4-T4 brief smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S4-T4 scoped brief with budget, citations, uncertainty, and feedback storage");
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
                "Provision did not create the S4-T4 synthetic vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");
            var unlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0 && unlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S4-T4 vault did not unlock (exit=" + unlock.ExitCode + "; stderr=" + unlock.StandardError.Trim() + ").");

            var now = DateTimeOffset.UtcNow.AddMinutes(-5);
            const string registryTitle = "Synthetic S4-T4 registry title";
            const string enrolledLocator = "s4t4-enrolled-synthetic-locator";
            await WriteS4T4SourceAsync(connectionsExecutable, fixture, S4T4Source, S4T4Zone,
                S4T4SourceDraft(registryTitle, enrolledLocator, "synthetic-source-v1", "available", now), 1).ConfigureAwait(false);
            await WriteS4T4ItemAsync(connectionsExecutable, fixture, S4T4ItemAlpha, S4T4Zone,
                S4T4ItemDraft("Synthetic S4-T4 alpha lesson",
                    "Synthetic S4-T4 body alpha: the return-to-project playbook for the synthetic project; never a real owner secret.",
                    now,
                    new[]
                    {
                        new
                        {
                            sourceId = S4T4Source,
                            locator = (string?)null,
                            availability = "available",
                            sourceRevision = 1L,
                            span = new { start = 0L, end = 64L }
                        }
                    }), 1).ConfigureAwait(false);
            await WriteS4T4ItemAsync(connectionsExecutable, fixture, S4T4ItemBeta, S4T4Zone,
                S4T4ItemDraft("Synthetic S4-T4 beta lesson",
                    "Synthetic S4-T4 body beta: the conflicting playbook revision kept beside alpha for the same project; never a real owner secret.",
                    now.AddMinutes(1),
                    new[]
                    {
                        new
                        {
                            sourceId = S4T4Source,
                            locator = (string?)null,
                            availability = "available",
                            sourceRevision = 1L,
                            span = new { start = 0L, end = 64L }
                        }
                    }), 1).ConfigureAwait(false);
            await WriteS4T4ItemAsync(connectionsExecutable, fixture, S4T4ItemConflict, S4T4Zone,
                S4T4ItemDraft("Synthetic S4-T4 conflict observation",
                    "Synthetic S4-T4 body conflict: an assistant-reported observation that disagrees with the alpha playbook; never a real owner secret.",
                    now.AddMinutes(2), null, "observation", "assistant", "reportedSource"), 1).ConfigureAwait(false);
            await WriteS4T4ItemAsync(connectionsExecutable, fixture, S4T4ItemStale, S4T4Zone,
                S4T4ItemDraft("Synthetic S4-T4 stale lesson",
                    "Synthetic S4-T4 body stale: kept for the trace but superseded by the beta revision path; never a real owner secret.",
                    now.AddDays(-30), null), 1).ConfigureAwait(false);
            await WriteS4T4ItemAsync(connectionsExecutable, fixture, S4T4ItemQuestion, S4T4Zone,
                S4T4ItemDraft("Synthetic S4-T4 open question",
                    "Synthetic S4-T4 body question: which playbook applies to the next synthetic rollout; never a real owner secret.",
                    now.AddMinutes(3), null, "question", "user", "unverified"), 1).ConfigureAwait(false);
            await WriteS4T4ItemAsync(connectionsExecutable, fixture, S4T4ItemOutside, S4T4Zone,
                S4T4ItemDraft("Synthetic S4-T4 outside lesson",
                    "Synthetic S4-T4 body S4T4-OUTSIDE-SENTINEL-9f31: kept outside every grant; never a real owner secret.",
                    now, null), 1).ConfigureAwait(false);

            // Conflicting entry: revise the registered source so the cited
            // items render superseded while keeping their frozen bytes.
            await WriteS4T4SourceReviseAsync(connectionsExecutable, fixture, S4T4Source, S4T4Zone,
                S4T4SourceDraft(registryTitle, enrolledLocator, "synthetic-source-v2", "unavailable", now), 1, 2).ConfigureAwait(false);

            var rebuild = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(rebuild.ExitCode == 0 && rebuild.StandardOutput.Contains("INDEX rebuilt rows=", StringComparison.Ordinal) &&
                    !rebuild.StandardOutput.Contains("rows=0", StringComparison.Ordinal),
                "The S4-T4 private index rebuild did not publish a materialized generation (exit=" + rebuild.ExitCode + "; stdout=" + rebuild.StandardOutput.Trim() + "; stderr=" + rebuild.StandardError.Trim() + ").");

            // Brief grant: memory.context + memory.read over the five in-scope
            // items (never the outside item). A read-only grant without the
            // context operation must deny the brief path.
            var briefSession = await IssueS4T4GrantAsync(connectionsExecutable, fixture, "s4t4-brief", "memory.context,memory.read",
                new[] { S4T4ItemAlpha, S4T4ItemBeta, S4T4ItemConflict, S4T4ItemStale, S4T4ItemQuestion }).ConfigureAwait(false);
            var readOnlySession = await IssueS4T4GrantAsync(connectionsExecutable, fixture, "s4t4-readonly", "memory.read",
                new[] { S4T4ItemAlpha }).ConfigureAwait(false);

            // Full-budget brief: all five entries selected with citations,
            // applicability, uncertainty, and no omissions beyond the trace.
            var brief = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-brief", "--agent-pipe", fixture.AgentPipe,
                "--session-file", briefSession.SessionFile,
                "--query", "playbook",
                "--budget-chars", "8000").ConfigureAwait(false);
            Require(brief.ExitCode == 0 &&
                    brief.StandardOutput.Contains("BRIEF query=playbook", StringComparison.Ordinal) &&
                    brief.StandardOutput.Contains($"ENTRY id={S4T4ItemAlpha} revision=1", StringComparison.Ordinal) &&
                    brief.StandardOutput.Contains($"ENTRY id={S4T4ItemBeta} revision=1", StringComparison.Ordinal) &&
                    brief.StandardOutput.Contains($"citation id={S4T4Source} revision=1", StringComparison.Ordinal) &&
                    brief.StandardOutput.Contains("status=superseded", StringComparison.Ordinal) &&
                    brief.StandardOutput.Contains("warning=citation_source_superseded", StringComparison.Ordinal) &&
                    brief.StandardOutput.Contains("UNCERTAIN", StringComparison.Ordinal) &&
                    brief.StandardOutput.Contains("applicability project=synthetic-s4t4-project", StringComparison.Ordinal),
                "Full-budget brief did not serve entries with citations, applicability, and uncertainty (exit=" + brief.ExitCode + "; stdout=" + brief.StandardOutput.Trim() + "; stderr=" + brief.StandardError.Trim() + ").");
            Require(!brief.StandardOutput.Contains(registryTitle, StringComparison.Ordinal) &&
                    !brief.StandardOutput.Contains(enrolledLocator, StringComparison.Ordinal),
                "Brief disclosed registry-private title/locator.");
            Require(!brief.StandardOutput.Contains("you must", StringComparison.OrdinalIgnoreCase) &&
                    !brief.StandardOutput.Contains("instruction", StringComparison.OrdinalIgnoreCase),
                "Brief carried instruction-shaped text.");
            Console.WriteLine("PASS full-budget brief serves entries with citations, applicability, and uncertainty");

            // Small-budget brief: the same query under 400 chars truncates
            // honestly with an explicit omission trace and uncertainty flags.
            var small = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-brief", "--agent-pipe", fixture.AgentPipe,
                "--session-file", briefSession.SessionFile,
                "--query", "playbook",
                "--budget-chars", "400").ConfigureAwait(false);
            Require(small.ExitCode == 0 &&
                    small.StandardOutput.Contains("truncated=true", StringComparison.Ordinal) &&
                    small.StandardOutput.Contains("OMITTED id=", StringComparison.Ordinal) &&
                    small.StandardOutput.Contains("reason=budget_exhausted", StringComparison.Ordinal) &&
                    small.StandardOutput.Contains("UNCERTAIN", StringComparison.Ordinal),
                "Small-budget brief did not trace omissions honestly (exit=" + small.ExitCode + "; stdout=" + small.StandardOutput.Trim() + ").");
            Require(small.StandardOutput.Contains($"ENTRY id={S4T4ItemAlpha}", StringComparison.Ordinal),
                "Small-budget brief dropped its first selection instead of truncating honestly.");
            Console.WriteLine("PASS small-budget brief truncates with explicit omissions and uncertainty");

            // Rebuild under the current grant: the brief renders the same
            // frozen selection again (deterministic reconstruction).
            var rebuilt = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-brief", "--agent-pipe", fixture.AgentPipe,
                "--session-file", briefSession.SessionFile,
                "--query", "playbook",
                "--budget-chars", "8000").ConfigureAwait(false);
            Require(rebuilt.ExitCode == 0 &&
                    rebuilt.StandardOutput.Contains($"ENTRY id={S4T4ItemAlpha} revision=1", StringComparison.Ordinal) &&
                    rebuilt.StandardOutput.Contains($"ENTRY id={S4T4ItemBeta} revision=1", StringComparison.Ordinal),
                "Brief did not reconstruct under the current grant (exit=" + rebuilt.ExitCode + "; stdout=" + rebuilt.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS brief reconstructs under the current grant");

            // Read-only grant denies the brief path generically.
            var deniedBrief = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-brief", "--agent-pipe", fixture.AgentPipe,
                "--session-file", readOnlySession.SessionFile,
                "--query", "playbook",
                "--budget-chars", "8000").ConfigureAwait(false);
            Require(deniedBrief.ExitCode == 3 && deniedBrief.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !deniedBrief.StandardOutput.Contains("ENTRY id=", StringComparison.Ordinal),
                "A grant without memory.context served the brief.");
            const string outsideSentinel = "S4T4-OUTSIDE-SENTINEL-9f31";
            var outsideBrief = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-brief", "--agent-pipe", fixture.AgentPipe,
                "--session-file", briefSession.SessionFile,
                "--query", "playbook",
                "--budget-chars", "8000").ConfigureAwait(false);
            Require(outsideBrief.ExitCode == 0 &&
                    outsideBrief.StandardOutput.Contains("BRIEF query=playbook", StringComparison.Ordinal) &&
                    !outsideBrief.StandardOutput.Contains(S4T4ItemOutside, StringComparison.Ordinal) &&
                    !outsideBrief.StandardOutput.Contains(outsideSentinel, StringComparison.Ordinal) &&
                    !outsideBrief.StandardOutput.Contains("kept outside every grant", StringComparison.Ordinal),
                "Brief served an out-of-scope body, ID, or sentinel anywhere in entries, omissions, or trace (exit=" + outsideBrief.ExitCode + "; stdout=" + outsideBrief.StandardOutput.Trim() + ").");

            // MCP bridge: the new tools are advertised and honor the scopes.
            var bridgeList = await RunBridgeToolsListAsync(connectionsExecutable, fixture.AgentPipe).ConfigureAwait(false);
            Require(bridgeList.Contains("metabrain_memory_brief", StringComparison.Ordinal) &&
                    bridgeList.Contains("metabrain_memory_feedback", StringComparison.Ordinal) &&
                    bridgeList.Contains("metabrain_feedback_read", StringComparison.Ordinal),
                "The MCP bridge did not advertise the S4-T4 brief/feedback tools.");
            var bridgeBrief = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_memory_brief", new { sessionFile = briefSession.SessionFile, query = "playbook", budgetChars = "8000" }).ConfigureAwait(false);
            Require(bridgeBrief.Contains($"ENTRY id={S4T4ItemAlpha}", StringComparison.Ordinal) &&
                    bridgeBrief.Contains("UNCERTAIN", StringComparison.Ordinal) &&
                    !bridgeBrief.Contains(registryTitle, StringComparison.Ordinal) &&
                    !bridgeBrief.Contains("mb1_", StringComparison.Ordinal),
                "The MCP bridge brief did not serve the scoped projection without registry/bearer material.");
            var bridgeDenied = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_memory_brief", new { sessionFile = readOnlySession.SessionFile, query = "playbook", budgetChars = "8000" }).ConfigureAwait(false);
            Require(bridgeDenied.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !bridgeDenied.Contains($"ENTRY id={S4T4ItemAlpha}", StringComparison.Ordinal),
                "The MCP bridge let a context-free grant read the brief.");
            Console.WriteLine("PASS MCP bridge brief honors the context scope");

            // Feedback: an explicit memory.feedback grant on alpha+beta stores
            // useful/wrong/stale verdicts without touching canonical bodies.
            var feedbackSession = await IssueS4T4GrantAsync(connectionsExecutable, fixture, "s4t4-feedback", "memory.feedback,memory.read",
                new[] { S4T4ItemAlpha, S4T4ItemBeta }).ConfigureAwait(false);
            var usefulPath = await WriteS4T4FeedbackDraftAsync(fixture, S4T4ItemAlpha, 1, "useful", "Synthetic S4-T4 note: alpha helped the return-to-project lookup.").ConfigureAwait(false);
            var useful = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-feedback", "--agent-pipe", fixture.AgentPipe,
                "--session-file", feedbackSession.SessionFile,
                "--input-file", usefulPath).ConfigureAwait(false);
            Require(useful.ExitCode == 0 && useful.StandardOutput.Contains("FEEDBACK id=fb-", StringComparison.Ordinal) &&
                    useful.StandardOutput.Contains($"target={S4T4ItemAlpha}", StringComparison.Ordinal) &&
                    useful.StandardOutput.Contains("kind=useful", StringComparison.Ordinal),
                "Agent useful feedback was not stored (exit=" + useful.ExitCode + "; stdout=" + useful.StandardOutput.Trim() + "; stderr=" + useful.StandardError.Trim() + ").");
            var usefulId = S4T4FeedbackId(useful.StandardOutput);
            Require(!string.IsNullOrEmpty(usefulId) && usefulId.StartsWith("fb-", StringComparison.Ordinal),
                "Stored feedback did not mint an fb- identity.");
            var wrongPath = await WriteS4T4FeedbackDraftAsync(fixture, S4T4ItemBeta, 1, "wrong", "Synthetic S4-T4 note: beta conflicts with the alpha playbook.").ConfigureAwait(false);
            var wrong = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-feedback", "--agent-pipe", fixture.AgentPipe,
                "--session-file", feedbackSession.SessionFile,
                "--input-file", wrongPath).ConfigureAwait(false);
            Require(wrong.ExitCode == 0 && wrong.StandardOutput.Contains("kind=wrong", StringComparison.Ordinal),
                "Agent wrong feedback was not stored.");
            var stalePath = await WriteS4T4FeedbackDraftAsync(fixture, S4T4ItemAlpha, 1, "stale", "Synthetic S4-T4 note: alpha needs a fresh revision check.").ConfigureAwait(false);
            var stale = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-feedback", "--agent-pipe", fixture.AgentPipe,
                "--session-file", feedbackSession.SessionFile,
                "--input-file", stalePath).ConfigureAwait(false);
            Require(stale.ExitCode == 0 && stale.StandardOutput.Contains("kind=stale", StringComparison.Ordinal),
                "Agent stale feedback was not stored.");
            var staleId = S4T4FeedbackId(stale.StandardOutput);
            var bridgeFeedback = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_memory_feedback", new { sessionFile = feedbackSession.SessionFile, targetResourceId = S4T4ItemBeta, targetRevision = "1", kind = "useful", note = "Synthetic S4-T4 bridge note." }).ConfigureAwait(false);
            Require(bridgeFeedback.Contains("FEEDBACK id=fb-", StringComparison.Ordinal) &&
                    !bridgeFeedback.Contains("mb1_", StringComparison.Ordinal),
                "The MCP bridge feedback did not store the verdict without bearer material.");
            Console.WriteLine("PASS feedback stores useful/wrong/stale verdicts through CLI and MCP");

            // Feedback never rewrites beliefs: the target bodies, evidence,
            // and citations render exactly as before every verdict.
            var afterFeedback = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", briefSession.SessionFile,
                "--resource-id", S4T4ItemAlpha, "--resource-revision", "1").ConfigureAwait(false);
            Require(afterFeedback.ExitCode == 0 &&
                    afterFeedback.StandardOutput.Contains("the return-to-project playbook", StringComparison.Ordinal) &&
                    afterFeedback.StandardOutput.Contains("evidence=unverified", StringComparison.Ordinal) &&
                    afterFeedback.StandardOutput.Contains($"citation id={S4T4Source} revision=1", StringComparison.Ordinal),
                "Feedback mutated the owner belief body, evidence, or citations.");
            Console.WriteLine("PASS feedback never rewrites owner beliefs, evidence, or citations");

            // Feedback scope: a verdict on an ungranted target denies
            // generically; an item-only grant cannot write feedback.
            var outsidePath = await WriteS4T4FeedbackDraftAsync(fixture, S4T4ItemOutside, 1, "useful", "Synthetic S4-T4 note: outside scope.").ConfigureAwait(false);
            var outside = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-feedback", "--agent-pipe", fixture.AgentPipe,
                "--session-file", feedbackSession.SessionFile,
                "--input-file", outsidePath).ConfigureAwait(false);
            Require(outside.ExitCode == 3 && outside.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "Feedback on an ungranted target was stored.");
            var itemOnlyPath = await WriteS4T4FeedbackDraftAsync(fixture, S4T4ItemAlpha, 1, "useful", "Synthetic S4-T4 note: wrong grant.").ConfigureAwait(false);
            var itemOnly = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-feedback", "--agent-pipe", fixture.AgentPipe,
                "--session-file", briefSession.SessionFile,
                "--input-file", itemOnlyPath).ConfigureAwait(false);
            Require(itemOnly.ExitCode == 3 && itemOnly.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "An item-only grant stored feedback.");
            var guessedPath = await WriteS4T4FeedbackDraftAsync(fixture, S4T4GuessedItem, 1, "wrong", "Synthetic S4-T4 note: guessed.").ConfigureAwait(false);
            var guessed = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-feedback", "--agent-pipe", fixture.AgentPipe,
                "--session-file", feedbackSession.SessionFile,
                "--input-file", guessedPath).ConfigureAwait(false);
            Require(guessed.ExitCode == 3 && guessed.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "Feedback on a guessed ID was stored.");
            Console.WriteLine("PASS feedback denies out-of-scope, wrong-grant, and guessed targets");

            // Feedback persistence: owner reads the stored record after
            // restart/unlock, and a separately authorized feedback.read grant
            // opens it through the agent surface.
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadS4T4StatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false)).Contains("vault=locked", StringComparison.Ordinal),
                "The restarted service did not return locked.");
            var lockedFeedback = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-feedback", "--agent-pipe", fixture.AgentPipe,
                "--session-file", feedbackSession.SessionFile,
                "--input-file", usefulPath).ConfigureAwait(false);
            Require(lockedFeedback.ExitCode == 3,
                "Feedback was stored while locked.");
            var reunlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(reunlock.ExitCode == 0 && reunlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The passphrase did not unlock the restarted vault.");
            var ownerFeedback = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "feedback-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", usefulId, "--resource-revision", "1").ConfigureAwait(false);
            Require(ownerFeedback.ExitCode == 0 && ownerFeedback.StandardOutput.Contains($"FEEDBACK id={usefulId}", StringComparison.Ordinal) &&
                    ownerFeedback.StandardOutput.Contains("kind=useful", StringComparison.Ordinal),
                "Stored feedback did not survive restart/unlock (exit=" + ownerFeedback.ExitCode + "; stdout=" + ownerFeedback.StandardOutput.Trim() + "; stderr=" + ownerFeedback.StandardError.Trim() + ").");
            var readSession = await IssueS4T4GrantAsync(connectionsExecutable, fixture, "s4t4-feedback-read", "feedback.read",
                new[] { usefulId }).ConfigureAwait(false);
            var agentFeedbackRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-feedback-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", readSession.SessionFile,
                "--resource-id", usefulId, "--resource-revision", "1").ConfigureAwait(false);
            Require(agentFeedbackRead.ExitCode == 0 && agentFeedbackRead.StandardOutput.Contains($"FEEDBACK id={usefulId}", StringComparison.Ordinal),
                "A separately authorized feedback.read grant did not open the record.");
            var bridgeRead = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_feedback_read", new { sessionFile = readSession.SessionFile, resourceId = usefulId, resourceRevision = "1" }).ConfigureAwait(false);
            Require(bridgeRead.Contains($"FEEDBACK id={usefulId}", StringComparison.Ordinal) &&
                    !bridgeRead.Contains("mb1_", StringComparison.Ordinal),
                "The MCP bridge feedback read did not serve the record without bearer material.");
            var itemGrantRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-feedback-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", briefSession.SessionFile,
                "--resource-id", usefulId, "--resource-revision", "1").ConfigureAwait(false);
            Require(itemGrantRead.ExitCode == 3 && itemGrantRead.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "An item grant opened a feedback record.");
            Console.WriteLine("PASS feedback persists across restart/unlock with independent read grants");

            // Revoke after cache: issue a fresh brief grant (the earlier brief
            // sessions expired from the live list after restart/unlock churn),
            // revoke it by its listed session ID, and prove the next brief
            // call denies; a wrong-revision feedback write denies too.
            var revokeSession = await IssueS4T4GrantAsync(connectionsExecutable, fixture, "s4t4-revoke", "memory.context,memory.read",
                new[] { S4T4ItemBeta }).ConfigureAwait(false);
            var revokeId = await S4T4SessionId(revokeSession, connectionsExecutable, fixture).ConfigureAwait(false);
            Require(!string.IsNullOrEmpty(revokeId),
                "The sessions list did not show the fresh revoke session.");
            var revoke = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "revoke-session", "--control-pipe", fixture.ControlPipe,
                "--session-id", revokeId).ConfigureAwait(false);
            Require(revoke.ExitCode == 0, "The owner could not revoke the S4-T4 brief session.");
            var revokedBrief = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-brief", "--agent-pipe", fixture.AgentPipe,
                "--session-file", revokeSession.SessionFile,
                "--query", "playbook",
                "--budget-chars", "8000").ConfigureAwait(false);
            Require(revokedBrief.ExitCode == 3 && revokedBrief.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !revokedBrief.StandardOutput.Contains("ENTRY id=", StringComparison.Ordinal),
                "A revoked session still served the cached brief.");
            var wrongRevPath = await WriteS4T4FeedbackDraftAsync(fixture, S4T4ItemAlpha, 999, "wrong", "Synthetic S4-T4 note: wrong revision.").ConfigureAwait(false);
            var wrongRev = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-feedback", "--agent-pipe", fixture.AgentPipe,
                "--session-file", feedbackSession.SessionFile,
                "--input-file", wrongRevPath).ConfigureAwait(false);
            Require(wrongRev.ExitCode == 3 && wrongRev.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "Feedback on a wrong target revision was stored.");
            Console.WriteLine("PASS revoke after cache denies the next brief; wrong revisions deny");

            // Lock denies brief/feedback reads and cached serving.
            var lockSession = await IssueS4T4GrantAsync(connectionsExecutable, fixture, "s4t4-lock", "memory.context,memory.read",
                new[] { S4T4ItemBeta }).ConfigureAwait(false);
            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The owner could not lock after the S4-T4 walkthrough.");
            var lockedBrief = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-brief", "--agent-pipe", fixture.AgentPipe,
                "--session-file", lockSession.SessionFile,
                "--query", "playbook",
                "--budget-chars", "8000").ConfigureAwait(false);
            Require(lockedBrief.ExitCode == 3 && !lockedBrief.StandardOutput.Contains("ENTRY id=", StringComparison.Ordinal),
                "A locked vault served the brief.");
            var lockedRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-feedback-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", readSession.SessionFile,
                "--resource-id", usefulId, "--resource-revision", "1").ConfigureAwait(false);
            Require(lockedRead.ExitCode == 3 && !lockedRead.StandardOutput.Contains("FEEDBACK id=", StringComparison.Ordinal),
                "A locked vault served feedback.");
            Console.WriteLine("PASS lock denies brief/feedback reads and cached serving");

            Console.WriteLine("PASS S4-T4 scoped brief walkthrough with budget, uncertainty, and feedback storage");
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

    private static async Task<string> ReadS4T4StatusAsync(string executable, string pipe)
    {
        var result = await RunOwnerCommandAsync(executable, "owner", "status", "--control-pipe", pipe).ConfigureAwait(false);
        Require(result.ExitCode == 0, "The actual owner CLI status request failed.");
        return result.StandardOutput;
    }

    private static async Task WriteS4T4ItemAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson, long expectedRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s4t4-item-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"MEMORY stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Memory create did not store {resourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
        File.Delete(input);
    }

    private static async Task WriteS4T4SourceAsync(string executable, SmokeFixture fixture, string sourceId, string zoneId, string draftJson, long expectedRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s4t4-source-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "source-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", sourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"SOURCE stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Source create did not store {sourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
        File.Delete(input);
    }

    private static async Task WriteS4T4SourceReviseAsync(string executable, SmokeFixture fixture, string sourceId, string zoneId, string draftJson, long writeRevision, long expectedRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s4t4-source-revise-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "source-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", sourceId, "--zone-id", zoneId,
            "--expected-revision", writeRevision.ToString(CultureInfo.InvariantCulture),
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"SOURCE stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Source revise did not store {sourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
        File.Delete(input);
    }

    private static async Task<string> WriteS4T4FeedbackDraftAsync(SmokeFixture fixture, string targetId, long targetRevision, string kind, string note)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s4t4-feedback-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, S4T4FeedbackDraft(targetId, targetRevision, kind, note), Encoding.UTF8).ConfigureAwait(false);
        return input;
    }

    private static string S4T4FeedbackId(string output)
    {
        const string prefix = "FEEDBACK id=";
        var start = output.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += prefix.Length;
        var end = output.IndexOfAny([' ', '\r', '\n'], start);
        return (end < 0 ? output[start..] : output[start..end]).Trim();
    }

    private sealed record S4T4Session(string SessionId, string SessionFile);

    private static async Task<string> S4T4SessionId(S4T4Session session, string executable, SmokeFixture fixture)
    {
        // The redeem receipt carries no session ID (bearer redacted); the
        // owner `sessions` list is the honest source of the live ID. The
        // brief session is the only live session carrying memory.context, so
        // the operations marker below identifies it without guessing.
        var listed = await RunOwnerCommandAsync(executable,
            "owner", "sessions", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(listed.ExitCode == 0, "The owner could not list live sessions for the S4-T4 revoke leg (stderr=" + listed.StandardError.Trim() + ").");
        var parsed = S4T4ParseBriefSessionId(listed.StandardOutput);
        Require(!string.IsNullOrEmpty(parsed),
            "The sessions list did not show the brief session carrying memory.context (stdout=" + listed.StandardOutput.Trim() + ").");
        return parsed;
    }

    private static string S4T4ParseBriefSessionId(string output)
    {
        var latest = string.Empty;
        var match = string.Empty;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            const string prefix = "SESSION id=";
            var at = line.IndexOf(prefix, StringComparison.Ordinal);
            if (at >= 0)
            {
                var start = at + prefix.Length;
                var end = line.IndexOfAny([' ', '\t'], start);
                latest = (end < 0 ? line[start..] : line[start..end]).Trim();
            }

            if (line.Contains("memory.context", StringComparison.Ordinal) && !string.IsNullOrEmpty(latest))
            {
                match = latest;
            }
        }

        return match;
    }

    private static async Task<S4T4Session> IssueS4T4GrantAsync(string executable, SmokeFixture fixture, string name, string operations, string[] resourceIds)
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O", CultureInfo.InvariantCulture);
        var handoff = Path.Combine(fixture.OutputDirectory, name + ".grant-handoff");
        var sessionFile = Path.Combine(fixture.OutputDirectory, name + ".session-handoff");
        var grant = await RunOwnerCommandWithInputAsync(executable, "ISSUE" + Environment.NewLine,
            "owner", "grant", "--control-pipe", fixture.ControlPipe,
            "--resource-ids", string.Join(",", resourceIds),
            "--operations", operations,
            "--expires-at-utc", expiry,
            "--handoff-file", handoff).ConfigureAwait(false);
        Require(grant.ExitCode == 0,
            $"The owner could not issue the synthetic S4-T4 grant {name} (exit={grant.ExitCode}; stdout={grant.StandardOutput.Trim()}; stderr={grant.StandardError.Trim()}).");
        VerifyOwnerHandoffAcl(handoff);
        var redeem = await RunOwnerCommandAsync(executable,
            "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
            "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
        Require(redeem.ExitCode == 0 && redeem.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
            $"Redemption failed for the synthetic S4-T4 grant {name} (exit={redeem.ExitCode}; stderr={redeem.StandardError.Trim()}).");
        VerifyOwnerHandoffAcl(sessionFile);
        return new S4T4Session(ParseSessionId(redeem.StandardOutput), sessionFile);
    }
}
