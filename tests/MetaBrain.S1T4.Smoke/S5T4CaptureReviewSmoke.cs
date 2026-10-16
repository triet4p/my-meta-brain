using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;
using MetaBrain.OwnerUi;

namespace MetaBrain.S1T4.Smoke;

// S5-T4: capture into the encrypted source registry, the pending proposal inbox
// and the owner review that publishes or rejects one proposal. One synthetic
// owner vault is provisioned and unlocked through the real owner CLI and the
// real owner-channel client; the note is captured through the console session
// the WPF window drives; the proposals are written by the real agent channel
// under a real owner-approved grant; the review acts go through the real owner
// control pipe into the real service and the real encrypted store. Nothing here
// is a mock and no assertion is a source-text echo: every observation comes from
// the service, the client or the console session.
internal static partial class OwnerServiceSmoke
{
    private const string S5T4Zone = "s5t4-zone";
    private const string S5T4Inbox = "s5t4-inbox";
    private const string S5T4Subject = "s5t4-subject";
    private const string S5T4Note = "s5t4-note";
    private const string S5T4Marker = "s5t4-synthetic-review-marker-4c17";
    private const string S5T4NoteText =
        "S5-T4 synthetic capture: ghi chu nghien cuu ve cach dang nhap he thong sau khi quen mat khau; " + S5T4Marker;
    private const string S5T4NoteLocator = "https://synthetic.invalid/s5t4-never-fetched";
    private const string S5T4ConsoleNote = "s5t4-console-note";
    private const string S5T4ProposedTitle = "Synthetic S5-T4 proposed login lesson";
    private const string S5T4ProposedBody =
        "S5-T4 synthetic proposed lesson: an assistant structured the captured note into a lesson; " + S5T4Marker;
    private const string S5T4SubjectBody = "S5-T4 synthetic subject body: a note the owner kept before any review; " + S5T4Marker;
    private const string S5T4Reason = "S5-T4 synthetic review rationale: the proposed lesson is not what the note says";
    private const string S5T4UndoReason = "S5-T4 synthetic undo rationale: reviewed again and the decision changes";

    private static string S5T4ItemDraft(
        string kind,
        string author,
        string creationMethod,
        string evidence,
        string level,
        string title,
        string body,
        DateTimeOffset occurred,
        object? sources = null) =>
        JsonSerializer.Serialize(new
        {
            kind,
            author,
            creationMethod,
            evidence,
            level,
            title,
            body,
            sources = sources ?? Array.Empty<object>(),
            occurredAtUtc = occurred,
            recordedAtUtc = occurred.AddMinutes(1),
            applicability = (object?)null
        });

    private static string S5T4ProposalDraft(
        string kind,
        string idempotencyKey,
        string? subjectResourceId,
        long? subjectRevision,
        string itemJson,
        bool? userConfirmed = null) =>
        JsonSerializer.Serialize(new
        {
            kind,
            idempotencyKey,
            destinationResourceId = S5T4Inbox,
            destinationRevision = 1L,
            subjectResourceId,
            subjectRevision,
            item = JsonSerializer.Deserialize<JsonElement>(itemJson),
            userConfirmed
        });

    /// <summary>One citation of the captured note at exactly the revision that was captured.</summary>
    private static object[] S5T4NoteAnchor() => new object[]
    {
        new
        {
            sourceId = S5T4Note,
            locator = S5T4NoteLocator,
            availability = "available",
            sourceRevision = 1L,
            span = new { start = 0L, end = 40L }
        }
    };

    /// <summary>The reviewed content a console would send: the owner's edits and the proposal's own attribution.</summary>
    private static OwnerReviewDraft S5T4Reviewed(OwnerProposalDetail proposal, string title, string body) => new(
        proposal.Item.Kind,
        proposal.Item.Author,
        proposal.Item.CreationMethod,
        proposal.Item.Evidence,
        proposal.Item.Level,
        title,
        body,
        proposal.Item.Sources,
        proposal.Item.OccurredAtUtc,
        proposal.Item.Project,
        proposal.Item.Environment,
        proposal.Item.Version);

    internal static async Task RunS5T4CaptureReviewAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S5-T4 capture/review smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S5-T4 owner capture into the encrypted source registry and proposal review through the shared core");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        var passphrase = "";
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
                "Provision did not create the S5-T4 synthetic vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");

            var now = DateTimeOffset.UtcNow.AddMinutes(-5);
            await S5T4WriteItemAsync(connectionsExecutable, fixture.ControlPipe, fixture.OutputDirectory, S5T4Inbox, S5T4Zone,
                S5T4ItemDraft("observation", "user", "directWrite", "unverified", "l1",
                    "Synthetic S5-T4 inbox destination", "S5-T4 synthetic inbox destination: proposals land here for review.", now)).ConfigureAwait(false);
            await S5T4WriteItemAsync(connectionsExecutable, fixture.ControlPipe, fixture.OutputDirectory, S5T4Subject, S5T4Zone,
                S5T4ItemDraft("lesson", "user", "directWrite", "unverified", "l1",
                    "Synthetic S5-T4 subject lesson", S5T4SubjectBody, now)).ConfigureAwait(false);
            Console.WriteLine("PASS the synthetic inbox destination and the item a revise proposal pins are stored through the real owner CLI");

            var session = new OwnerConsoleSession(fixture.ControlPipe);
            await session.AttachAsync().ConfigureAwait(false);
            Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
                "The console session did not report the real unlocked vault (" + session.ReportedVaultState + ").");

            // ---- capture: text + zone through the real service ----
            session.SetCaptureDraft(S5T4NoteText);
            Require(session.HasUnsavedCaptureText,
                "The console did not report the editor text as unsaved before it was saved.");
            await session.CaptureAsync(S5T4Note, S5T4Zone, "Synthetic S5-T4 captured note", S5T4NoteLocator, "user", now.AddDays(-1)).ConfigureAwait(false);
            Require(session.CaptureSource is { } captured && captured.Id == S5T4Note && captured.Revision == 1 &&
                    string.Equals(captured.Locator, S5T4NoteLocator, StringComparison.Ordinal),
                "The capture did not write the note as a registered encrypted source (source=" + DescribeS5T4(session.CaptureSource) + ").");
            Require(!session.HasUnsavedCaptureText && string.Equals(session.CaptureSavedText, S5T4NoteText, StringComparison.Ordinal),
                "The console did not report the exact saved text as saved (" + session.CaptureMessage + ").");
            Require(session.CaptureMessage is { } captureMessage &&
                    captureMessage.Contains("registered source", StringComparison.Ordinal) &&
                    captureMessage.Contains("no provider", StringComparison.OrdinalIgnoreCase),
                "The capture outcome did not state what was stored and that no provider was needed (" + session.CaptureMessage + ").");
            Console.WriteLine("PASS the owner captures text + zone into an encrypted registered source with no provider in the path");

            // ---- malformed input keeps the owner's text ----
            var unpaired = "S5-T4 malformed \ud800 capture text; " + S5T4Marker;
            session.SetCaptureDraft(unpaired);
            await session.CaptureAsync(S5T4Note + "-bad", S5T4Zone, null, null, null, null).ConfigureAwait(false);
            Require(string.Equals(session.CaptureDraft, unpaired, StringComparison.Ordinal) &&
                    session.CaptureSavedText == S5T4NoteText &&
                    session.CaptureMessage is { } encodeProblem && encodeProblem.Contains("UTF-8", StringComparison.Ordinal),
                "A text this process cannot encode was not refused with an explanation and kept in the editor (" + session.CaptureMessage + ").");
            session.SetCaptureDraft(S5T4NoteText + " (second attempt)");
            await session.CaptureAsync(S5T4Note + "-nozone", "", null, null, null, null).ConfigureAwait(false);
            Require(session.CaptureDraft.EndsWith("(second attempt)", StringComparison.Ordinal) &&
                    session.CaptureMessage is { } zoneRefusal && zoneRefusal.Contains("invalid_request", StringComparison.Ordinal),
                "A capture without a zone was not refused by the service with its own code, or the text was lost (" + session.CaptureMessage + ").");
            var missingNote = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S5T4Note + "-bad").ConfigureAwait(false);
            Require(missingNote.ExitCode != 0,
                "A refused capture still wrote a note into the vault (stdout=" + missingNote.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS malformed capture input is refused with an actionable message and the owner's text survives in the editor");

            // ---- restart: the saved note is exactly what the vault holds ----
            var draining = service!;
            await StopServiceAsync(service!).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var restarted = await OwnerChannelClient.ReadStatusAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(restarted is { Availability: OwnerChannelAvailability.Reachable, VaultState: OwnerVaultState.Locked },
                "A restarted service was not reported locked at startup (" + restarted + ").");
            session.SetCaptureDraft("");
            await session.CaptureAsync(S5T4Note + "-locked", S5T4Zone, null, null, null, null).ConfigureAwait(false);
            Require(session.CaptureSource is null && session.CaptureSavedText is null && !session.HasUnsavedCaptureText,
                "A locked console claimed a capture was saved (" + session.CaptureMessage + ").");
            var lockedRead = OwnerChannelClient.ReadResearchNoteAsync(fixture.ControlPipe, S5T4Note, 1, CancellationToken.None).GetAwaiter().GetResult();
            Require(lockedRead.Outcome == OwnerCommandOutcome.Refused && lockedRead.ErrorCode == "vault_locked",
                "A locked vault served the captured note (" + lockedRead.Outcome + "/" + lockedRead.ErrorCode + ").");
            var unlockedAgain = await OwnerChannelClient.UnlockAsync(fixture.ControlPipe, passphrase, CancellationToken.None).ConfigureAwait(false);
            Require(unlockedAgain.Outcome == OwnerCommandOutcome.Completed && unlockedAgain.VaultState == OwnerVaultState.Unlocked,
                "The owner passphrase did not open the vault after the restart (" + unlockedAgain.Outcome + ").");
            await session.CheckAsync().ConfigureAwait(false);
            Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
                "The console session did not re-report the unlocked vault after the restart (" + session.ReportedVaultState + ").");
            await session.ReadBackCaptureAsync(S5T4Note, 1).ConfigureAwait(false);
            Require(string.Equals(session.CaptureReadBackText, S5T4NoteText, StringComparison.Ordinal),
                "The restarted vault did not serve the exact captured text (" + session.CaptureReadBackMessage + ").");
            Require(session.CaptureReadBackSource is { Revision: 1 } readBack && string.Equals(readBack.Id, S5T4Note, StringComparison.Ordinal),
                "The read-back did not name the exact registry revision it served (" + DescribeS5T4(session.CaptureReadBackSource) + ").");
            Console.WriteLine("PASS a restart unlocks to the same vault and the captured note comes back with exactly its saved text");

            // ---- the proposal inbox: a pending agent write, nothing canonical ----
            var createSession = await IssueS4T5GrantAsync(connectionsExecutable, fixture, "s5t4-create",
                "proposal.create", new[] { S5T4Note }, new[] { S5T4Inbox }).ConfigureAwait(false);
            var createDraft = await WriteS4T5ProposalDraftAsync(fixture, "create-1", S5T4ProposalDraft(
                "create", "s5t4-create-1", null, null,
                S5T4ItemDraft("lesson", "assistant", "paraphrase", "reportedSource", "l1",
                    S5T4ProposedTitle, S5T4ProposedBody, now.AddHours(-2), S5T4NoteAnchor()), userConfirmed: true)).ConfigureAwait(false);
            var proposed = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", createSession.SessionFile, "--input-file", createDraft).ConfigureAwait(false);
            Require(proposed.ExitCode == 0 && proposed.StandardOutput.Contains("kind=create", StringComparison.Ordinal) &&
                    proposed.StandardOutput.Contains("executor=agent", StringComparison.Ordinal) &&
                    proposed.StandardOutput.Contains("userConfirmedClaim=true", StringComparison.Ordinal),
                "The agent proposal was not stored pending with its provenance (exit=" + proposed.ExitCode + "; stdout=" + proposed.StandardOutput.Trim() + "; stderr=" + proposed.StandardError.Trim() + ").");
            var createProposalId = S4T5ProposalId(proposed.StandardOutput);
            File.Delete(createDraft);
            Require(DomainIdentifiersIsProposal(createProposalId),
                "The agent proposal did not return a proposal identity (" + createProposalId + ").");
            var reviewItemId = MemoryItemValidator.ReviewedItemIdFor(createProposalId);

            await session.RefreshReviewInboxAsync().ConfigureAwait(false);
            Require(session.ReviewProposals.Any(identity => string.Equals(identity.Id, createProposalId, StringComparison.Ordinal)),
                "The console inbox did not list the pending proposal the service holds (" + session.ReviewInboxMessage + ").");
            var createIdentity = session.ReviewProposals.First(identity => string.Equals(identity.Id, createProposalId, StringComparison.Ordinal));
            await session.OpenReviewProposalAsync(createIdentity).ConfigureAwait(false);
            var opened = session.ReviewProposal ??
                throw new InvalidOperationException("The console did not open the pending proposal (" + session.ReviewMessage + ").");
            Require(string.Equals(opened.Id, createProposalId, StringComparison.Ordinal) &&
                    string.Equals(opened.Item.Author, "assistant", StringComparison.Ordinal) &&
                    opened.ClaimedUserConfirmed,
                "The console did not open the pending proposal with its preserved AI authorship and confirmation claim (" + session.ReviewMessage + ").");
            Require(string.Equals(session.ReviewSourceText, S5T4NoteText, StringComparison.Ordinal) &&
                    session.ReviewItemId == reviewItemId && session.ReviewItemLifecycle is null,
                "The review did not show the cited source beside the content, or claimed a decision that was never recorded (" +
                session.ReviewSourceMessage + " / " + session.ReviewItemStateMessage + ").");
            var untouched = await OwnerChannelClient.ReadMemoryItemAsync(fixture.ControlPipe, reviewItemId, null, CancellationToken.None).ConfigureAwait(false);
            Require(untouched.Outcome == OwnerCommandOutcome.Refused && untouched.Item is null,
                "A pending proposal had already written canonical content (" + untouched.Outcome + ").");
            Console.WriteLine("PASS the inbox lists the pending proposal, the review shows the source beside the content, and nothing canonical exists yet");

            // ---- publish: the reviewed content, with the proposal's own attribution ----
            var reviewed = S5T4Reviewed(opened, "Synthetic S5-T4 published login lesson", S5T4ProposedBody);
            await session.PublishReviewProposalAsync(reviewed).ConfigureAwait(false);
            Require(session.ReviewOutcome is { } published &&
                    published.Publication == "published" && published.ItemId == reviewItemId && published.DecisionApplied,
                "Publishing did not write the reviewed item and record the decision (" + session.ReviewMessage + ").");
            var publishedItem = await OwnerChannelClient.ReadMemoryItemAsync(fixture.ControlPipe, reviewItemId, null, CancellationToken.None).ConfigureAwait(false);
            Require(publishedItem.Outcome == OwnerCommandOutcome.Completed && publishedItem.Item is { } item &&
                    string.Equals(item.Author, "assistant", StringComparison.Ordinal) &&
                    string.Equals(item.Executor, "owner", StringComparison.Ordinal) &&
                    string.Equals(item.CreationMethod, "paraphrase", StringComparison.Ordinal) &&
                    string.Equals(item.Evidence, "reportedSource", StringComparison.Ordinal) &&
                    string.Equals(item.Title, "Synthetic S5-T4 published login lesson", StringComparison.Ordinal) &&
                    item.Citations.Any(citation => string.Equals(citation.SourceId, S5T4Note, StringComparison.Ordinal)),
                "The published item did not keep the proposal's attribution, evidence or citation (" + DescribeS5T4(publishedItem.Item) + ").");
            var proposalAfterRead = await OwnerChannelClient.ReadProposalAsync(fixture.ControlPipe, createProposalId, 1, CancellationToken.None).ConfigureAwait(false);
            Require(proposalAfterRead.Outcome == OwnerCommandOutcome.Completed &&
                    proposalAfterRead.Proposal is { Revision: 1 } stillPending &&
                    string.Equals(stillPending.PayloadHash, opened.PayloadHash, StringComparison.Ordinal),
                "The proposal record changed when it was reviewed (" + proposalAfterRead.Outcome + ").");
            Console.WriteLine("PASS publishing writes the reviewed content as the canonical item and leaves the proposal record exactly as the agent wrote it");

            // ---- authorship cannot be upgraded by an approval ----
            var forgedUpgrade = reviewed with { Author = "user", Evidence = "userConfirmed" };
            await session.PublishReviewProposalAsync(forgedUpgrade).ConfigureAwait(false);
            Require(session.ReviewMessage is { } upgradeRefusal && upgradeRefusal.Contains("invalid_request", StringComparison.Ordinal),
                "A review that relabels an AI proposal as user-written was not refused by the service (" + session.ReviewMessage + ").");
            var afterUpgrade = await OwnerChannelClient.ReadMemoryItemAsync(fixture.ControlPipe, reviewItemId, null, CancellationToken.None).ConfigureAwait(false);
            Require(afterUpgrade.Item is { } unchangedUpgrade &&
                    string.Equals(unchangedUpgrade.Author, "assistant", StringComparison.Ordinal) &&
                    string.Equals(unchangedUpgrade.Evidence, "reportedSource", StringComparison.Ordinal),
                "A refused attribution change still rewrote the published item (" + DescribeS5T4(afterUpgrade.Item) + ").");

            // ---- a completed review retried writes nothing twice ----
            await session.PublishReviewProposalAsync(reviewed).ConfigureAwait(false);
            Require(session.ReviewOutcome is { DecisionApplied: false, Publication: "published" } retried,
                "A repeated review of the same proposal did not return the recorded decision (" + session.ReviewMessage + ").");
            var afterRetry = await OwnerChannelClient.ReadMemoryItemAsync(fixture.ControlPipe, reviewItemId, null, CancellationToken.None).ConfigureAwait(false);
            Require(afterRetry.Item is { Revision: 1 } single,
                "A repeated review wrote a second revision of the same review item (revision=" + afterRetry.Item?.Revision + ").");
            var differentContent = reviewed with { Body = "S5-T4 synthetic different body; " + S5T4Marker };
            await session.PublishReviewProposalAsync(differentContent).ConfigureAwait(false);
            Require(session.ReviewMessage is { } conflictRefusal && conflictRefusal.Contains("invalid_request", StringComparison.Ordinal),
                "A repeated review with different content was not refused (" + session.ReviewMessage + ").");
            var afterConflict = await OwnerChannelClient.ReadMemoryItemAsync(fixture.ControlPipe, reviewItemId, null, CancellationToken.None).ConfigureAwait(false);
            Require(afterConflict.Item is { Revision: 1 } kept &&
                    string.Equals(kept.Body, S5T4ProposedBody, StringComparison.Ordinal),
                "A refused repeated review overwrote the published content (" + DescribeS5T4(afterConflict.Item) + ").");
            Console.WriteLine("PASS an approval cannot relabel the proposal's attribution and a repeated review writes no duplicate truth");

            // ---- rejection: the decision is recorded, the proposal is not applied ----
            var reviseSession = await IssueS4T5GrantAsync(connectionsExecutable, fixture, "s5t4-revise",
                "proposal.create", new[] { S5T4Subject }, new[] { S5T4Inbox }).ConfigureAwait(false);
            var reviseDraft = await WriteS4T5ProposalDraftAsync(fixture, "revise-1", S5T4ProposalDraft(
                "revise", "s5t4-revise-1", S5T4Subject, 1L,
                S5T4ItemDraft("lesson", "assistant", "synthesis", "unverified", "l2",
                    "Synthetic S5-T4 proposed revise", "S5-T4 synthetic proposed revision body; " + S5T4Marker, now.AddHours(-1)))).ConfigureAwait(false);
            var revisedProposal = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", reviseSession.SessionFile, "--input-file", reviseDraft).ConfigureAwait(false);
            Require(revisedProposal.ExitCode == 0 && revisedProposal.StandardOutput.Contains("kind=revise", StringComparison.Ordinal),
                "The revise proposal was not stored (exit=" + revisedProposal.ExitCode + "; stderr=" + revisedProposal.StandardError.Trim() + ").");
            var reviseProposalId = S4T5ProposalId(revisedProposal.StandardOutput);
            File.Delete(reviseDraft);
            await session.RefreshReviewInboxAsync().ConfigureAwait(false);
            var reviseIdentity = session.ReviewProposals.FirstOrDefault(identity => string.Equals(identity.Id, reviseProposalId, StringComparison.Ordinal));
            Require(reviseIdentity is not null, "The inbox did not list the revise proposal (" + session.ReviewInboxMessage + ").");
            await session.OpenReviewProposalAsync(reviseIdentity!).ConfigureAwait(false);
            var reviseReviewItemId = MemoryItemValidator.ReviewedItemIdFor(reviseProposalId);
            Require(session.ReviewProposal is { Kind: "revise", SubjectResourceId: S5T4Subject } openedRevise &&
                    session.ReviewItemId == reviseReviewItemId &&
                    session.ReviewItemLifecycle is null &&
                    session.ReviewSubjectLifecycle is null &&
                    session.ReviewItemStateMessage is { } reviseState &&
                    reviseState.Contains("No decision has been recorded", StringComparison.Ordinal) &&
                    reviseState.Contains("has no publication record yet", StringComparison.Ordinal),
                "The review did not open the revise proposal with its own review item and the state of the item it pins (" + session.ReviewMessage + " / " + session.ReviewItemStateMessage + ").");

            await session.RejectReviewProposalAsync("   ").ConfigureAwait(false);
            Require(session.ReviewMessage is { } rationaleRefusal && rationaleRefusal.Contains("rationale", StringComparison.OrdinalIgnoreCase),
                "A rejection without a rationale was not refused in the console (" + session.ReviewMessage + ").");
            await session.RejectReviewProposalAsync(S5T4Reason).ConfigureAwait(false);
            Require(session.ReviewOutcome is { } rejected &&
                    rejected.Publication == "rejected" && rejected.ItemId == reviseReviewItemId && rejected.DecisionApplied,
                "Rejecting did not record the decision on the review item (" + session.ReviewMessage + ").");
            var subjectAfterReject = await OwnerChannelClient.ReadMemoryItemAsync(fixture.ControlPipe, S5T4Subject, null, CancellationToken.None).ConfigureAwait(false);
            Require(subjectAfterReject.Item is { Revision: 1 } untouchedSubject &&
                    string.Equals(untouchedSubject.Body, S5T4SubjectBody, StringComparison.Ordinal),
                "Rejecting a revise proposal applied the revision it rejected (" + DescribeS5T4(subjectAfterReject.Item) + ").");
            var rejectedItem = await OwnerChannelClient.ReadMemoryItemAsync(fixture.ControlPipe, reviseReviewItemId, null, CancellationToken.None).ConfigureAwait(false);
            Require(rejectedItem.Item is { } rejectedRecord &&
                    string.Equals(rejectedRecord.Author, "assistant", StringComparison.Ordinal) &&
                    string.Equals(rejectedRecord.Evidence, "unverified", StringComparison.Ordinal) &&
                    !string.IsNullOrEmpty(rejectedRecord.Body),
                "The rejected review item did not keep the proposal's own attribution (" + DescribeS5T4(rejectedItem.Item) + ").");
            Console.WriteLine("PASS rejecting records the owner's decision, keeps the rejected content out of the item it would have revised, and needs a rationale");

            // ---- rejected and withdrawn items are not current, and undo reverses the decision ----
            var rejectedRebuild = await OwnerChannelClient.RebuildLibraryIndexAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(rejectedRebuild.Outcome == OwnerCommandOutcome.Completed,
                "The index rebuild before the rejected-state check failed (" + rejectedRebuild.ErrorCode + ").");
            var rejectedCurrent = await OwnerChannelClient.SearchLibraryAsync(
                fixture.ControlPipe,
                new OwnerLibraryQuery(Text: "S5-T4 proposed revise", Current: OwnerLibraryCurrentness.Current),
                CancellationToken.None).ConfigureAwait(false);
            var rejectedHistory = await OwnerChannelClient.SearchLibraryAsync(
                fixture.ControlPipe,
                new OwnerLibraryQuery(Text: "S5-T4 proposed revise", Current: OwnerLibraryCurrentness.History),
                CancellationToken.None).ConfigureAwait(false);
            var rejectedPage = rejectedCurrent.Page;
            Require(rejectedPage is not null && rejectedPage.Hits.All(hit => hit.Id != reviseReviewItemId),
                "A rejected review item was still current in a search (" + Describe5T4(rejectedPage, session) + ").");
            var rejectedHistoryPage = rejectedHistory.Page;
            Require(rejectedHistoryPage is not null && rejectedHistoryPage.Hits.Any(hit => hit.Id == reviseReviewItemId),
                "A rejected review item was not reachable as history (" + Describe5T4(rejectedHistoryPage, session) + ").");

            await session.UndoReviewDecisionAsync("").ConfigureAwait(false);
            Require(session.ReviewMessage is { } undoRationale && undoRationale.Contains("rationale", StringComparison.OrdinalIgnoreCase),
                "Undo without a rationale was not refused in the console (" + session.ReviewMessage + ").");
            await session.UndoReviewDecisionAsync(S5T4UndoReason).ConfigureAwait(false);
            Require(session.ReviewItemLifecycle is { Publication: "proposal" } reproposed &&
                    session.ReviewMessage is { } reproposedMessage && reproposedMessage.Contains("without writing a second copy", StringComparison.Ordinal),
                "Undoing a rejection did not return the same record to proposal (" + session.ReviewMessage + ").");

            var rebuild = await OwnerChannelClient.RebuildLibraryIndexAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(rebuild.Outcome == OwnerCommandOutcome.Completed,
                "The library index rebuild before the currentness check failed (" + rebuild.Outcome + "/" + rebuild.ErrorCode + ").");
            var currentRejected = await OwnerChannelClient.SearchLibraryAsync(
                fixture.ControlPipe,
                new OwnerLibraryQuery(Text: "S5-T4 proposed revise", Current: OwnerLibraryCurrentness.Current),
                CancellationToken.None).ConfigureAwait(false);
            var currentPublished = await OwnerChannelClient.SearchLibraryAsync(
                fixture.ControlPipe,
                new OwnerLibraryQuery(Text: "published login lesson", Current: OwnerLibraryCurrentness.Current),
                CancellationToken.None).ConfigureAwait(false);
            var historyRejected = await OwnerChannelClient.SearchLibraryAsync(
                fixture.ControlPipe,
                new OwnerLibraryQuery(Text: "S5-T4 proposed revise", Current: OwnerLibraryCurrentness.History),
                CancellationToken.None).ConfigureAwait(false);
            var reproposedPage = currentRejected.Page;
            Require(reproposedPage is not null && reproposedPage.Hits.Any(hit => hit.Id == reviseReviewItemId),
                "Undoing a rejection did not return the item to a current search (" + Describe5T4(reproposedPage, session) + ").");
            var publishedPage = currentPublished.Page;
            Require(publishedPage is not null && publishedPage.Hits.Any(hit => hit.Id == reviewItemId),
                "The published review item was not current after its publication (" + Describe5T4(publishedPage, session) + ").");
            var historyPage = historyRejected.Page;
            Require(historyPage is not null && historyPage.Hits.Any(hit => hit.Id == reviseReviewItemId),
                "The review item was not reachable in a history search (" + Describe5T4(historyPage, session) + ").");

            await session.RefreshReviewInboxAsync().ConfigureAwait(false);
            var publishedIdentity = session.ReviewProposals.FirstOrDefault(identity => string.Equals(identity.Id, createProposalId, StringComparison.Ordinal));
            Require(publishedIdentity is not null, "The inbox lost the reviewed proposal's identity (" + session.ReviewInboxMessage + ").");
            await session.OpenReviewProposalAsync(publishedIdentity!).ConfigureAwait(false);
            Require(session.ReviewItemLifecycle is { Publication: "published" } openPublished &&
                    session.ReviewDecisionIsUndoable,
                "Opening a decided proposal did not report its recorded decision (" + session.ReviewItemStateMessage + ").");
            await session.UndoReviewDecisionAsync(S5T4UndoReason).ConfigureAwait(false);
            Require(session.ReviewItemLifecycle is { Publication: "withdrawn" } withdrawn &&
                    session.ReviewMessage is { } withdrawnMessage &&
                    withdrawnMessage.Contains("no copy", StringComparison.Ordinal),
                "Undoing a publication did not withdraw the same record (" + session.ReviewMessage + ").");
            var afterWithdraw = await OwnerChannelClient.ReadMemoryItemAsync(fixture.ControlPipe, reviewItemId, null, CancellationToken.None).ConfigureAwait(false);
            Require(afterWithdraw.Item is { Revision: 1 } keptWithdrawn &&
                    string.Equals(keptWithdrawn.Author, "assistant", StringComparison.Ordinal) &&
                    string.Equals(keptWithdrawn.Body, S5T4ProposedBody, StringComparison.Ordinal),
                "Withdrawing rewrote the item's content or attribution (" + DescribeS5T4(afterWithdraw.Item) + ").");
            var rebuildAfterUndo = await OwnerChannelClient.RebuildLibraryIndexAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(rebuildAfterUndo.Outcome == OwnerCommandOutcome.Completed, "The index rebuild after the undo failed (" + rebuildAfterUndo.ErrorCode + ").");
            var currentWithdrawn = await OwnerChannelClient.SearchLibraryAsync(
                fixture.ControlPipe,
                new OwnerLibraryQuery(Text: "published login lesson", Current: OwnerLibraryCurrentness.Current),
                CancellationToken.None).ConfigureAwait(false);
            var historyWithdrawn = await OwnerChannelClient.SearchLibraryAsync(
                fixture.ControlPipe,
                new OwnerLibraryQuery(Text: "published login lesson", Current: OwnerLibraryCurrentness.History),
                CancellationToken.None).ConfigureAwait(false);
            var withdrawnCurrent = currentWithdrawn.Page;
            Require(withdrawnCurrent is not null && withdrawnCurrent.Hits.All(hit => hit.Id != reviewItemId),
                "A withdrawn review item was still current (" + Describe5T4(withdrawnCurrent, session) + ").");
            var withdrawnHistory = historyWithdrawn.Page;
            Require(withdrawnHistory is not null && withdrawnHistory.Hits.Any(hit => hit.Id == reviewItemId),
                "A withdrawn review item was not reachable as history (" + Describe5T4(withdrawnHistory, session) + ").");
            Console.WriteLine("PASS undo withdraws a published review item and re-proposes a rejected one without duplicating or relabelling anything");

            // ---- the revision conflict: a concurrent editor wins and the draft survives ----
            var subjectRevise = await WriteS5T4ItemReviseAsync(connectionsExecutable, fixture, S5T4Subject, S5T4Zone,
                S5T4ItemDraft("lesson", "user", "directWrite", "unverified", "l1",
                    "Synthetic S5-T4 subject lesson revised elsewhere",
                    "S5-T4 synthetic concurrent edit body; " + S5T4Marker, now.AddMinutes(-1)), expectedRevision: 1).ConfigureAwait(false);
            Require(subjectRevise.ExitCode == 0, "The concurrent owner edit of the pinned item failed (" + subjectRevise.StandardError.Trim() + ").");
            await session.RefreshReviewInboxAsync().ConfigureAwait(false);
            var staleIdentity = session.ReviewProposals.FirstOrDefault(identity => string.Equals(identity.Id, reviseProposalId, StringComparison.Ordinal));
            Require(staleIdentity is not null, "The inbox lost the revise proposal (" + session.ReviewInboxMessage + ").");
            await session.OpenReviewProposalAsync(staleIdentity!).ConfigureAwait(false);
            var staleDraft = S5T4Reviewed(session.ReviewProposal!, "Synthetic S5-T4 stale revise", "S5-T4 synthetic stale revise body; " + S5T4Marker);
            var draftVersionBeforeConflict = session.ReviewDraftVersion;
            await session.PublishReviewProposalAsync(staleDraft).ConfigureAwait(false);
            Require(session.ReviewMessage is { } staleRefusal && staleRefusal.Contains("invalid_request", StringComparison.Ordinal),
                "Publishing a proposal whose pinned revision moved was not refused (" + session.ReviewMessage + ").");
            var stillOpen = session.ReviewProposal;
            Require(session.ReviewDraftVersion == draftVersionBeforeConflict &&
                    stillOpen is not null &&
                    string.Equals(stillOpen.Id, reviseProposalId, StringComparison.Ordinal) &&
                    session.ReviewDraft is not null,
                "A refused publish replaced or dropped the proposal and reviewed content the console was showing (" + session.ReviewMessage + ").");
            var subjectAfterConflict = await OwnerChannelClient.ReadMemoryItemAsync(fixture.ControlPipe, S5T4Subject, null, CancellationToken.None).ConfigureAwait(false);
            Require(subjectAfterConflict.Item is { Revision: 2 } conflicted &&
                    string.Equals(conflicted.Title, "Synthetic S5-T4 subject lesson revised elsewhere", StringComparison.Ordinal),
                "A refused publish overwrote the item the concurrent editor won (" + DescribeS5T4(subjectAfterConflict.Item) + ").");
            Console.WriteLine("PASS a concurrent edit conflict refuses the stale review, overwrites nothing and keeps the reviewed content");

            // ---- the agent channel never performs an owner review act ----
            foreach (var ownerOperation in new[] { ServiceRequestHandler.OwnerProposalPublishOperation, ServiceRequestHandler.OwnerProposalRejectOperation })
            {
                var denied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = ownerOperation,
                    isOwner = true,
                    sessionToken = ReadSessionBearer(createSession.SessionFile),
                    resourceId = createProposalId,
                    resourceRevision = 1,
                    contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"))
                }).ConfigureAwait(false);
                Require(ReplyCode(denied) == "forbidden",
                    "The agent channel accepted the owner review operation " + ownerOperation + " (" + ReplyCode(denied) + ").");
            }

            var extras = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = ServiceRequestHandler.OwnerProposalPublishOperation,
                resourceId = createProposalId,
                resourceRevision = 1,
                zoneId = S5T4Zone,
                contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"))
            }).ConfigureAwait(false);
            Require(ReplyCode(extras) == "invalid_request",
                "A review request carrying a field its contract does not name was accepted (" + ReplyCode(extras) + ").");
            var movedProposal = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = ServiceRequestHandler.OwnerProposalPublishOperation,
                resourceId = createProposalId,
                resourceRevision = 9,
                contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"))
            }).ConfigureAwait(false);
            Require(ReplyCode(movedProposal) == "invalid_request",
                "A review of a proposal revision that does not exist was accepted (" + ReplyCode(movedProposal) + ").");
            Console.WriteLine("PASS the agent channel is denied every owner review act and a review refuses unnamed fields and a moved proposal revision");

            // ---- locked: nothing is saved, unsaved text is warned about, private views go ----
            var staleEpoch = session.PrivateEpoch;
            var staleSource = session.CaptureReadBackSource
                ?? throw new InvalidOperationException("The pre-lock epoch holds no captured source to replay.");
            var staleProposals = session.ReviewProposals;
            Require(staleSource.Revision == 1 && staleProposals.Length > 0,
                "The pre-lock epoch held no real answer to replay against the lock (" +
                DescribeS5T4(staleSource) + " / " + staleProposals.Length.ToString(CultureInfo.InvariantCulture) + " proposals).");

            session.SetCaptureDraft("S5-T4 synthetic unsaved capture that must not survive a lock; " + S5T4Marker);
            await session.LockAsync().ConfigureAwait(false);
            Require(session.ReportedVaultState == OwnerVaultState.Unlocked &&
                    session.VaultMessage is { } warning && warning.Contains("never saved", StringComparison.Ordinal),
                "Locking with unsaved capture text did not warn first (" + session.VaultMessage + ").");
            await session.LockAsync().ConfigureAwait(false);
            Require(session.ReportedVaultState == OwnerVaultState.Locked,
                "The second lock request did not lock the vault (" + session.ReportedVaultState + ").");
            Require(!session.HasUnsavedCaptureText && session.CaptureDraft.Length == 0 &&
                    session.CaptureSource is null && session.CaptureReadBackText is null &&
                    session.ReviewProposals.Length == 0 && session.ReviewProposal is null &&
                    session.ReviewSourceText is null && session.ReviewOutcome is null,
                "A locked console kept private capture or review state.");

            // A real answer of the finished unlocked epoch, applied after the
            // lock, must be dropped instead of restoring any private view.
            var replayedCapture = session.ApplyCaptureAnswer(
                staleEpoch, S5T4NoteText, new OwnerCaptureResult(OwnerCommandOutcome.Completed, null, staleSource));
            var replayedInbox = session.ApplyProposalListAnswer(
                staleEpoch, new OwnerProposalListResult(OwnerCommandOutcome.Completed, null, staleProposals));
            Require(!replayedCapture && session.CaptureSource is null && session.CaptureSavedText is null &&
                    session.CaptureReadBackText is null &&
                    !replayedInbox && session.ReviewProposals.Length == 0 && session.ReviewProposal is null &&
                    session.Activity.Any(entry => entry.Contains("ignored a stale", StringComparison.Ordinal)),
                "A late answer for the locked epoch was applied instead of dropped (" +
                session.CaptureMessage + " / " + session.ReviewInboxMessage + ").");

            await session.CaptureAsync(S5T4Note + "-after-lock", S5T4Zone, null, null, null, null).ConfigureAwait(false);
            Require(session.CaptureSource is null && session.CaptureSavedText is null,
                "A locked console reported a saved capture (" + session.CaptureMessage + ").");
            Console.WriteLine("PASS locking warns about unsaved capture text, clears every private view, drops a late answer of the locked epoch, and a locked console never claims a save");

            Require(!S5T4FixtureHoldsMarker(fixture),
                "A synthetic private marker was found in plaintext under the fixture root.");
            Console.WriteLine("PASS no synthetic private marker was persisted in plaintext under the fixture root");

            var serviceOutput = await draining.StandardOutputDrain.ConfigureAwait(false);
            var serviceError = await draining.StandardErrorDrain.ConfigureAwait(false);
            Require(!serviceOutput.Contains(passphrase, StringComparison.Ordinal) && !serviceError.Contains(passphrase, StringComparison.Ordinal),
                "The service wrote the owner passphrase to its own output.");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static bool DomainIdentifiersIsProposal(string proposalId) =>
        proposalId.StartsWith("prop-", StringComparison.Ordinal) && proposalId.Length > "prop-".Length;

    private static string DescribeS5T4(OwnerSourceDetail? source) =>
        source is null ? "no source" : "id=" + source.Id + " rev=" + source.Revision + " zone=" + source.ZoneId +
            " availability=" + source.Availability + " title=" + (source.Title ?? "(none)") + " locator=" + (source.Locator ?? "(none)");

    private static string DescribeS5T4(OwnerItemDetail? item) =>
        item is null ? "no item" : "id=" + item.Id + " rev=" + item.Revision + " kind=" + item.Kind + " level=" + item.Level +
            " author=" + item.Author + " executor=" + item.Executor + " creation=" + item.CreationMethod +
            " evidence=" + item.Evidence + " title=" + (item.Title ?? "(none)") +
            " citations=" + item.Citations.Length.ToString(CultureInfo.InvariantCulture);

    private static string Describe5T4(OwnerLibraryPage? page, OwnerConsoleSession session) =>
        page is null
            ? "no page (" + (session.LibraryMessage ?? "no message") + ")"
            : "total=" + page.Total.ToString(CultureInfo.InvariantCulture) +
                " hits=" + string.Join(",", page.Hits.Select(hit => hit.Id + "@" + hit.Revision + "/" + hit.Publication));

    private static async Task S5T4WriteItemAsync(
        string executable,
        string controlPipe,
        string outputDirectory,
        string resourceId,
        string zoneId,
        string draftJson)
    {
        var input = Path.Combine(outputDirectory, "s5t4-item-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", controlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId, "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
            "The S5-T4 item create did not store " + resourceId + " at revision 1 (exit=" + result.ExitCode +
            "; stdout=" + result.StandardOutput.Trim() + "; stderr=" + result.StandardError.Trim() + ").");
        File.Delete(input);
    }

    private static async Task<CommandResult> WriteS5T4ItemReviseAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string zoneId,
        string draftJson,
        long expectedRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s5t4-revise-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--expected-revision", expectedRevision.ToString(CultureInfo.InvariantCulture),
            "--input-file", input).ConfigureAwait(false);
        File.Delete(input);
        return result;
    }

    private static bool S5T4FixtureHoldsMarker(SmokeFixture fixture)
    {
        foreach (var path in Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories))
        {
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            if (text.Contains(S5T4Marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Creates the synthetic owner-console fixture at an explicit root for the
    /// external WPF capture/review proof. The caller owns that root: it starts
    /// and stops the service, drives the console and removes the root
    /// afterwards, so this scenario disposes nothing and provisions no vault.
    /// </summary>
    internal static Task RunS5T4InboxFixtureAsync(string fixtureRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S5-T4 owner console fixture requires Windows named pipes.");
        }

        var fixture = SmokeFixture.CreateUninitializedAt(fixtureRoot);
        Console.WriteLine("S5T4 CONSOLE FIXTURE root=" + fixture.Root);
        Console.WriteLine("S5T4 CONSOLE FIXTURE settings=" + fixture.SettingsPath);
        Console.WriteLine("S5T4 CONSOLE FIXTURE controlPipe=" + fixture.ControlPipe);
        Console.WriteLine("S5T4 CONSOLE FIXTURE agentPipe=" + fixture.AgentPipe);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes the fixture resources the console proof needs before it drives the
    /// window: the approved inbox destination and the item a revise proposal
    /// pins. The caller runs this after the console provisioned the vault (the
    /// service then holds the key) and removes the fixture root afterwards.
    /// </summary>
    internal static async Task S5T4WriteConsoleInboxAsync(string connectionsExecutable, string fixtureRoot)
    {
        var root = Path.GetFullPath(fixtureRoot);
        var settingsPath = Path.Combine(root, "service", "service-settings.json");
        using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var controlPipe = settings.RootElement.GetProperty("controlPipe").GetString()
            ?? throw new InvalidDataException("The console fixture settings carry no control pipe.");
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var output = Path.Combine(root, "output");
        await S5T4WriteItemAsync(connectionsExecutable, controlPipe, output, S5T4Inbox, S5T4Zone,
            S5T4ItemDraft("observation", "user", "directWrite", "unverified", "l1",
                "Synthetic S5-T4 inbox destination", "S5-T4 synthetic inbox destination: proposals land here for review.", now)).ConfigureAwait(false);
        await S5T4WriteItemAsync(connectionsExecutable, controlPipe, output, S5T4Subject, S5T4Zone,
            S5T4ItemDraft("lesson", "user", "directWrite", "unverified", "l1",
                "Synthetic S5-T4 subject lesson", S5T4SubjectBody, now)).ConfigureAwait(false);
        Console.WriteLine("S5T4 CONSOLE INBOX inbox=" + S5T4Inbox + " subject=" + S5T4Subject + " zone=" + S5T4Zone);
    }

    /// <summary>
    /// Submits the two pending proposals the console proof reviews, through the
    /// real agent channel under real owner-approved grants: a create proposal
    /// citing the note the console captured, and a revise proposal pinning the
    /// subject item at its live revision.
    /// </summary>
    public static async Task S5T4WriteConsoleProposalsAsync(string connectionsExecutable, string fixtureRoot)
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

        var createSession = await IssueOwnerGrantAsync(connectionsExecutable, controlPipe, agentPipe, output,
            "s5t4-console-create", "proposal.create", new[] { S5T4ConsoleNote }, new[] { S5T4Inbox }).ConfigureAwait(false);
        var createDraft = await S5T4WriteProposalDraftAsync(output, "console-create",
            S5T4ProposalDraft("create", "s5t4-console-create-1", null, null,
                S5T4ItemDraft("lesson", "assistant", "paraphrase", "reportedSource", "l1",
                    S5T4ProposedTitle, S5T4ProposedBody, now.AddHours(-2),
                    new object[]
                    {
                        new
                        {
                            sourceId = S5T4ConsoleNote,
                            locator = (string?)null,
                            availability = "available",
                            sourceRevision = 1L,
                            span = new { start = 0L, end = 24L }
                        }
                    }),
                userConfirmed: true)).ConfigureAwait(false);
        var proposed = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "agent-propose", "--agent-pipe", agentPipe,
            "--session-file", createSession.SessionFile, "--input-file", createDraft).ConfigureAwait(false);
        Require(proposed.ExitCode == 0,
            "The console fixture could not submit the create proposal (exit=" + proposed.ExitCode + "; stderr=" + proposed.StandardError.Trim() + ").");
        var createProposalId = S4T5ProposalId(proposed.StandardOutput);
        File.Delete(createDraft);

        var reviseSession = await IssueOwnerGrantAsync(connectionsExecutable, controlPipe, agentPipe, output,
            "s5t4-console-revise", "proposal.create", new[] { S5T4Subject }, new[] { S5T4Inbox }).ConfigureAwait(false);
        var reviseDraft = await S5T4WriteProposalDraftAsync(output, "console-revise",
            S5T4ProposalDraft("revise", "s5t4-console-revise-1", S5T4Subject, 1L,
                S5T4ItemDraft("lesson", "assistant", "synthesis", "unverified", "l2",
                    "Synthetic S5-T4 proposed revise", "S5-T4 synthetic proposed revision body; " + S5T4Marker, now.AddHours(-1)))).ConfigureAwait(false);
        var revised = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "agent-propose", "--agent-pipe", agentPipe,
            "--session-file", reviseSession.SessionFile, "--input-file", reviseDraft).ConfigureAwait(false);
        Require(revised.ExitCode == 0,
            "The console fixture could not submit the revise proposal (exit=" + revised.ExitCode + "; stderr=" + revised.StandardError.Trim() + ").");
        var reviseProposalId = S4T5ProposalId(revised.StandardOutput);
        File.Delete(reviseDraft);
        Console.WriteLine("S5T4 CONSOLE PROPOSALS create=" + createProposalId + " revise=" + reviseProposalId +
            " note=" + S5T4ConsoleNote + " controlPipe=" + controlPipe);
    }

    private static async Task<string> S5T4WriteProposalDraftAsync(string outputDirectory, string name, string draftJson)
    {
        var input = Path.Combine(outputDirectory, "s5t4-proposal-" + name + "-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return input;
    }
}
