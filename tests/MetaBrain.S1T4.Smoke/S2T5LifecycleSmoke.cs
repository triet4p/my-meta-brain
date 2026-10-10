using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    internal static async Task RunS2T5LifecycleAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S2-T5 lifecycle smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S2-T5 domain lifecycle, temporal validity, attribution and retained history");
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
                "Provision did not create the S2-T5 synthetic vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");

            var now = DateTimeOffset.UtcNow.AddMinutes(-30);
            var recordedUtc = now.AddMinutes(1);
            var zone = "s2t5-zone-alpha";
            var qOpen = "s2t5-q-open";
            var qAi = "s2t5-q-ai";
            var aiAnswer = "s2t5-ai-answer";
            var intOne = "s2t5-int-one";
            var intTwo = "s2t5-int-two";
            var intOld = "s2t5-int-old";
            var intNew = "s2t5-int-new";
            var decision = "s2t5-decision";
            var historyItem = "s2t5-history";
            var coarseItem = "s2t5-coarse";
            var declaredItem = "s2t5-declared";
            var trustedUser = "s2t5-trusted-user";
            var toolObservation = "s2t5-tool-observation";
            var registeredSource = "s2t5-src-one";
            var addressRelation = "rel-s2t5-addresses";
            const string questionReason = "Synthetic S2-T5 question basis; never a real owner secret.";
            const string publishReason = "Synthetic S2-T5 withdrawal rationale; never a real owner secret.";
            const string rejectReason = "Synthetic S2-T5 rejection rationale; never a real owner secret.";
            const string resolveReason = "Synthetic S2-T5 resolution rationale; never a real owner secret.";
            const string reopenReason = "Synthetic S2-T5 reopen rationale; never a real owner secret.";
            const string completeReason = "Synthetic S2-T5 completion rationale; never a real owner secret.";
            const string cancelReason = "Synthetic S2-T5 cancellation rationale; never a real owner secret.";
            const string supersedeReason = "Synthetic S2-T5 supersession rationale; never a real owner secret.";

            // Subjects: two questions, one AI answer, three intentions, one
            // decision, one revision-history item, one coarse-time item, one
            // body-declared draft target, one user-confirmed claim, one tool
            // observation and one registered source.
            await WriteS2T5ItemDraftAsync(connectionsExecutable, fixture, qOpen, zone,
                S2T5ItemDraft("question", "user", "directWrite", "unverified", "Synthetic S2-T5 open question", "Synthetic S2-T5 open question body; never a real owner secret.", now), 1).ConfigureAwait(false);
            await WriteS2T5ItemDraftAsync(connectionsExecutable, fixture, qAi, zone,
                S2T5ItemDraft("question", "user", "directWrite", "unverified", "Synthetic S2-T5 answered question", "Synthetic S2-T5 answered question body; never a real owner secret.", now), 1).ConfigureAwait(false);
            await WriteS2T5ItemDraftAsync(connectionsExecutable, fixture, aiAnswer, zone,
                S2T5ItemDraft("synthesis", "assistant", "paraphrase", "reportedSource", "Synthetic S2-T5 AI answer", "Synthetic S2-T5 AI answer body; never a real owner secret.", now, "l2"), 1).ConfigureAwait(false);
            await WriteS2T5ItemDraftAsync(connectionsExecutable, fixture, intOne, zone,
                S2T5ItemDraft("intention", "user", "directWrite", "unverified", "Synthetic S2-T5 intention one", "Synthetic S2-T5 intention note; never a real owner secret.", now), 1).ConfigureAwait(false);
            await WriteS2T5ItemDraftAsync(connectionsExecutable, fixture, intTwo, zone,
                S2T5ItemDraft("intention", "user", "directWrite", "unverified", "Synthetic S2-T5 intention two", "Synthetic S2-T5 intention two note; never a real owner secret.", now), 1).ConfigureAwait(false);
            await WriteS2T5ItemDraftAsync(connectionsExecutable, fixture, intOld, zone,
                S2T5ItemDraft("intention", "user", "directWrite", "unverified", "Synthetic S2-T5 superseded intention", "Synthetic S2-T5 superseded intention note; never a real owner secret.", now), 1).ConfigureAwait(false);
            await WriteS2T5ItemDraftAsync(connectionsExecutable, fixture, intNew, zone,
                S2T5ItemDraft("intention", "user", "directWrite", "unverified", "Synthetic S2-T4 replacement intention", "Synthetic S2-T5 replacement intention note; never a real owner secret.", now), 1).ConfigureAwait(false);
            await WriteS2T5ItemDraftAsync(connectionsExecutable, fixture, decision, zone,
                S2T5ItemDraft("decision", "user", "directWrite", "unverified", "Synthetic S2-T5 decision", "Synthetic S2-T5 decision body; never a real owner secret.", now), 1).ConfigureAwait(false);
            await WriteS2T5ItemDraftAsync(connectionsExecutable, fixture, historyItem, zone,
                S2T5ItemDraft("claim", "user", "directWrite", "unverified", "Synthetic S2-T5 revision history item", "Synthetic S2-T5 revision one body; never a real owner secret.", now), 1).ConfigureAwait(false);
            await WriteS2T5ItemDraftAsync(connectionsExecutable, fixture, coarseItem, zone,
                S2T5ItemDraft("claim", "user", "directWrite", "unverified", "Synthetic S2-T5 coarse time item", "Synthetic S2-T5 coarse time body; never a real owner secret.", null, "l1", recordedUtc), 1).ConfigureAwait(false);
            await WriteS2T5ItemDraftAsync(connectionsExecutable, fixture, declaredItem, zone,
                S2T5ItemDraft("lesson", "user", "directWrite", "unverified", "Synthetic S2-T5 body-declared draft target", "Synthetic S2-T5 body-declared body; never a real owner secret.", now, "l2"), 1).ConfigureAwait(false);
            await WriteS2T5ItemDraftAsync(connectionsExecutable, fixture, trustedUser, zone,
                S2T5ItemDraft("claim", "user", "directWrite", "userConfirmed", "Synthetic S2-T5 user-confirmed claim", "Synthetic S2-T5 user-confirmed body; never a real owner secret.", now), 1).ConfigureAwait(false);
            await WriteS2T5ItemDraftAsync(connectionsExecutable, fixture, toolObservation, zone,
                S2T5ItemDraft("observation", "tool", "verbatimExtract", "experimentalObservation", "Synthetic S2-T5 tool observation", "Synthetic S2-T5 tool observation body; never a real owner secret.", now, "l0"), 1).ConfigureAwait(false);
            await WriteS2T4SourceDraftAsync(connectionsExecutable, fixture, registeredSource, zone, JsonSerializer.Serialize(new
            {
                title = "Synthetic S2-T5 source one",
                locator = "s2t5-private-locator",
                version = "synthetic-source-v1",
                availability = "available",
                creationMethod = "directWrite",
                capturedAtUtc = now,
                recordedAtUtc = now.AddMinutes(1)
            }), expectedRevision: 1).ConfigureAwait(false);

            // An AI answer only addresses a question: the relation is the
            // strongest form an answer can take, and it must not close it.
            await CreateS2T4RelationAsync(connectionsExecutable, fixture, addressRelation, zone,
                S2T4RelationDraft("addresses", "accepted", aiAnswer, "memoryItem", 1L, null, qAi, "memoryItem", 1L, null,
                    "Synthetic S2-T5 answer addresses the question; never a real owner secret.", now)).ConfigureAwait(false);

            await CreateS2T5LifecycleAsync(connectionsExecutable, fixture, qOpen,
                S2T5LifecycleDraft(questionReason)).ConfigureAwait(false);
            await CreateS2T5LifecycleAsync(connectionsExecutable, fixture, qAi,
                S2T5LifecycleDraft(questionReason)).ConfigureAwait(false);
            await CreateS2T5LifecycleAsync(connectionsExecutable, fixture, intOne,
                S2T5LifecycleDraft(questionReason)).ConfigureAwait(false);
            await CreateS2T5LifecycleAsync(connectionsExecutable, fixture, intTwo,
                S2T5LifecycleDraft(questionReason)).ConfigureAwait(false);
            await CreateS2T5LifecycleAsync(connectionsExecutable, fixture, intOld,
                S2T5LifecycleDraft(questionReason)).ConfigureAwait(false);
            await CreateS2T5LifecycleAsync(connectionsExecutable, fixture, decision,
                S2T5LifecycleDraft(questionReason)).ConfigureAwait(false);
            await CreateS2T5LifecycleAsync(connectionsExecutable, fixture, historyItem,
                S2T5LifecycleDraft(questionReason)).ConfigureAwait(false);
            await CreateS2T5LifecycleAsync(connectionsExecutable, fixture, coarseItem,
                S2T5LifecycleDraft(questionReason)).ConfigureAwait(false);

            // A draft that declares its own states, executor and identity is
            // served with the derived values instead: nothing in a body can
            // mint a lifecycle identity or a state.
            var declaredCreate = await CreateS2T5LifecycleAsync(connectionsExecutable, fixture, declaredItem,
                JsonSerializer.Serialize(new
                {
                    creationMethod = "directWrite",
                    reason = "Synthetic S2-T5 body-declared draft; never a real owner secret.",
                    publication = "published",
                    question = "resolved",
                    intention = "done",
                    executor = "agent",
                    id = "lc-s2t5-attacker",
                    subjectResourceId = "s2t5-attacker"
                })).ConfigureAwait(false);
            Require(declaredCreate.ExitCode == 0,
                "The lifecycle create with a body-declared draft was refused outright (exit=" + declaredCreate.ExitCode + "; stderr=" + declaredCreate.StandardError.Trim() + ").");
            var declaredRead = await ReadS2T5LifecycleAsync(connectionsExecutable, fixture, declaredItem).ConfigureAwait(false);
            Require(declaredRead.ExitCode == 0 &&
                    declaredRead.StandardOutput.Contains("publication=proposal", StringComparison.Ordinal) &&
                    declaredRead.StandardOutput.Contains("question=none", StringComparison.Ordinal) &&
                    declaredRead.StandardOutput.Contains("intention=none", StringComparison.Ordinal) &&
                    declaredRead.StandardOutput.Contains("executor=owner", StringComparison.Ordinal) &&
                    declaredRead.StandardOutput.Contains($"subject={declaredItem}", StringComparison.Ordinal) &&
                    declaredRead.StandardOutput.Contains($"id=lc-{declaredItem}", StringComparison.Ordinal),
                "A lifecycle draft declared a state, an executor, or an identity and it was honoured (stdout=" + declaredRead.StandardOutput.Trim() + ").");

            var initialRead = await ReadS2T5LifecycleAsync(connectionsExecutable, fixture, qOpen).ConfigureAwait(false);
            Require(initialRead.ExitCode == 0 &&
                    initialRead.StandardOutput.Contains("publication=proposal", StringComparison.Ordinal) &&
                    initialRead.StandardOutput.Contains("question=open", StringComparison.Ordinal) &&
                    initialRead.StandardOutput.Contains("intention=none", StringComparison.Ordinal) &&
                    initialRead.StandardOutput.Contains("subjectKind=question", StringComparison.Ordinal) &&
                    initialRead.StandardOutput.Contains("executor=owner", StringComparison.Ordinal) &&
                    initialRead.StandardOutput.Contains("revision=1", StringComparison.Ordinal) &&
                    !initialRead.StandardOutput.Contains("HISTORY", StringComparison.Ordinal),
                "The created lifecycle record did not report derived initial states (stdout=" + initialRead.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS lifecycle records derive identity, zone, kind, initial states and executor from the trusted subject and context");

            var duplicateCreate = await CreateS2T5LifecycleAsync(connectionsExecutable, fixture, qOpen,
                S2T5LifecycleDraft(questionReason)).ConfigureAwait(false);
            Require(duplicateCreate.ExitCode != 0 && duplicateCreate.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A second lifecycle record was created for the same subject.");
            var missingSubject = await CreateS2T5LifecycleAsync(connectionsExecutable, fixture, "s2t5-not-present",
                S2T5LifecycleDraft(questionReason)).ConfigureAwait(false);
            Require(missingSubject.ExitCode != 0 && missingSubject.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A lifecycle record was created for a subject that does not exist.");
            var sourceSubject = await CreateS2T5LifecycleAsync(connectionsExecutable, fixture, registeredSource,
                S2T5LifecycleDraft(questionReason)).ConfigureAwait(false);
            Require(sourceSubject.ExitCode != 0 && sourceSubject.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A lifecycle record was created for a registered source.");
            var relationSubject = await CreateS2T5LifecycleAsync(connectionsExecutable, fixture, addressRelation,
                S2T5LifecycleDraft(questionReason)).ConfigureAwait(false);
            Require(relationSubject.ExitCode != 0 && relationSubject.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A lifecycle record was created for a relation.");
            var questionOnlyRecord = await ReadS2T5LifecycleAsync(connectionsExecutable, fixture, intOne).ConfigureAwait(false);
            Require(questionOnlyRecord.ExitCode == 0 && questionOnlyRecord.StandardOutput.Contains("intention=pending", StringComparison.Ordinal) &&
                    questionOnlyRecord.StandardOutput.Contains("question=none", StringComparison.Ordinal) &&
                    questionOnlyRecord.StandardOutput.Contains("subjectKind=intention", StringComparison.Ordinal),
                "The intention subject did not derive its intention lifecycle (stdout=" + questionOnlyRecord.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS one lifecycle record per subject: duplicates, unknown subjects, sources and relations are refused");

            // Publication lifecycle: every act is explicit, recorded with its
            // actor and rationale, and the graph never reverts a publication
            // implicitly.
            var published = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-publish", decision, expectedRevision: 1).ConfigureAwait(false);
            Require(published.ExitCode == 0 && published.StandardOutput.Contains("publication=published", StringComparison.Ordinal),
                "Publish did not move the proposal to published (exit=" + published.ExitCode + "; stdout=" + published.StandardOutput.Trim() + "; stderr=" + published.StandardError.Trim() + ").");
            var withdrawn = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-withdraw", decision, expectedRevision: 2,
                S2T5TransitionDraft(publishReason)).ConfigureAwait(false);
            Require(withdrawn.ExitCode == 0 && withdrawn.StandardOutput.Contains("publication=withdrawn", StringComparison.Ordinal) &&
                    withdrawn.StandardOutput.Contains($"rationale={publishReason}", StringComparison.Ordinal),
                "Withdraw did not record the published-to-withdrawn transition with its rationale (stdout=" + withdrawn.StandardOutput.Trim() + ").");
            var reproposed = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-repropose", decision, expectedRevision: 3).ConfigureAwait(false);
            Require(reproposed.ExitCode == 0 && reproposed.StandardOutput.Contains("publication=proposal", StringComparison.Ordinal),
                "Re-propose did not return the withdrawn record to proposal (stdout=" + reproposed.StandardOutput.Trim() + ").");
            var rejected = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-reject", decision, expectedRevision: 4,
                S2T5TransitionDraft(rejectReason)).ConfigureAwait(false);
            Require(rejected.ExitCode == 0 && rejected.StandardOutput.Contains("publication=rejected", StringComparison.Ordinal) &&
                    rejected.StandardOutput.Contains($"rationale={rejectReason}", StringComparison.Ordinal),
                "Reject did not record the rejected state with its rationale (stdout=" + rejected.StandardOutput.Trim() + ").");
            var reproposedAgain = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-repropose", decision, expectedRevision: 5).ConfigureAwait(false);
            Require(reproposedAgain.ExitCode == 0 && reproposedAgain.StandardOutput.Contains("publication=proposal", StringComparison.Ordinal),
                "A rejected proposal could not be proposed again (stdout=" + reproposedAgain.StandardOutput.Trim() + ").");
            var historyRead = await ReadS2T5LifecycleAsync(connectionsExecutable, fixture, decision).ConfigureAwait(false);
            Require(historyRead.ExitCode == 0 && historyRead.StandardOutput.Contains("HISTORY aspect=publication act=publish from=proposal to=published executor=owner", StringComparison.Ordinal) &&
                    historyRead.StandardOutput.Contains("HISTORY aspect=publication act=withdraw from=published to=withdrawn executor=owner", StringComparison.Ordinal) &&
                    historyRead.StandardOutput.Contains("HISTORY aspect=publication act=rePropose from=withdrawn to=proposal executor=owner", StringComparison.Ordinal) &&
                    historyRead.StandardOutput.Contains("HISTORY aspect=publication act=rejectProposal from=proposal to=rejected executor=owner", StringComparison.Ordinal) &&
                    historyRead.StandardOutput.Contains("HISTORY aspect=publication act=rePropose from=rejected to=proposal executor=owner", StringComparison.Ordinal) &&
                    historyRead.StandardOutput.Contains("revision=6", StringComparison.Ordinal),
                "The publication history was not recorded in order with its actors (stdout=" + historyRead.StandardOutput.Trim() + ").");
            var publishTwice = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-publish", decision, expectedRevision: 2).ConfigureAwait(false);
            Require(publishTwice.ExitCode != 0 && publishTwice.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "Publishing an already published record was accepted.");
            var withdrawProposal = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-withdraw", decision, expectedRevision: 6,
                S2T5TransitionDraft(publishReason)).ConfigureAwait(false);
            Require(withdrawProposal.ExitCode != 0 && withdrawProposal.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "Withdrawing a proposal that was never published was accepted.");
            var rejectPublished = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-reject", decision, expectedRevision: 2,
                S2T5TransitionDraft(rejectReason)).ConfigureAwait(false);
            Require(rejectPublished.ExitCode != 0 && rejectPublished.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A published record was rejected instead of being withdrawn.");
            var stalePublication = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-publish", decision, expectedRevision: 1).ConfigureAwait(false);
            Require(stalePublication.ExitCode != 0 && stalePublication.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A stale publication transition was accepted.");
            var transitionQuestionState = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-question-resolve", decision, expectedRevision: 6,
                S2T5TransitionDraft(resolveReason)).ConfigureAwait(false);
            Require(transitionQuestionState.ExitCode != 0 && transitionQuestionState.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A question act was accepted on a decision subject.");
            var transitionIntentionState = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-intention-complete", decision, expectedRevision: 6,
                S2T5TransitionDraft(completeReason)).ConfigureAwait(false);
            Require(transitionIntentionState.ExitCode != 0 && transitionIntentionState.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "An intention act was accepted on a decision subject.");
            Console.WriteLine("PASS publication lifecycle records explicit publish/reject/withdraw/re-propose with actor and history and refuses invalid or stale acts");

            // Question lifecycle: an AI answer only addresses the question.
            var questionAfterAnswer = await ReadS2T5LifecycleAsync(connectionsExecutable, fixture, qAi).ConfigureAwait(false);
            Require(questionAfterAnswer.ExitCode == 0 && questionAfterAnswer.StandardOutput.Contains("question=open", StringComparison.Ordinal),
                "An addresses relation from an AI answer resolved the question (stdout=" + questionAfterAnswer.StandardOutput.Trim() + ").");
            var resolved = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-question-resolve", qAi, expectedRevision: 1,
                S2T5TransitionDraft(resolveReason)).ConfigureAwait(false);
            Require(resolved.ExitCode == 0 && resolved.StandardOutput.Contains("question=resolved", StringComparison.Ordinal) &&
                    resolved.StandardOutput.Contains($"rationale={resolveReason}", StringComparison.Ordinal),
                "Resolution did not record the resolved state with its rationale (stdout=" + resolved.StandardOutput.Trim() + ").");
            var reopened = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-question-reopen", qAi, expectedRevision: 2,
                S2T5TransitionDraft(reopenReason)).ConfigureAwait(false);
            Require(reopened.ExitCode == 0 && reopened.StandardOutput.Contains("question=reopened", StringComparison.Ordinal),
                "Reopen did not record the reopened state (stdout=" + reopened.StandardOutput.Trim() + ").");
            var resolvedAgain = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-question-resolve", qAi, expectedRevision: 3,
                S2T5TransitionDraft(resolveReason)).ConfigureAwait(false);
            Require(resolvedAgain.ExitCode == 0 && resolvedAgain.StandardOutput.Contains("question=resolved", StringComparison.Ordinal),
                "A reopened question could not be resolved again (stdout=" + resolvedAgain.StandardOutput.Trim() + ").");
            var resolveWithoutReason = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-question-resolve", qAi, expectedRevision: 4).ConfigureAwait(false);
            Require(resolveWithoutReason.ExitCode != 0 && resolveWithoutReason.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A resolution without an explicit rationale was accepted.");
            var resolveTwice = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-question-resolve", qAi, expectedRevision: 4,
                S2T5TransitionDraft(resolveReason)).ConfigureAwait(false);
            Require(resolveTwice.ExitCode != 0 && resolveTwice.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "An already resolved question was resolved again.");
            var reopenOpen = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-question-reopen", qOpen, expectedRevision: 1,
                S2T5TransitionDraft(reopenReason)).ConfigureAwait(false);
            Require(reopenOpen.ExitCode != 0 && reopenOpen.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "An open question was reopened.");
            Console.WriteLine("PASS question lifecycle stays open through an AI answer and moves only through explicit resolve/reopen with rationale");

            // Intention lifecycle: notified and acknowledged are markers, never
            // completion, and supersession is explicit and never overwritten.
            var notified = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-intention-notify", intOne, expectedRevision: 1).ConfigureAwait(false);
            Require(notified.ExitCode == 0 && notified.StandardOutput.Contains("intention=pending", StringComparison.Ordinal) &&
                    notified.StandardOutput.Contains("notified=true", StringComparison.Ordinal),
                "Notify changed the intention state instead of recording a marker (stdout=" + notified.StandardOutput.Trim() + ").");
            var acknowledged = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-intention-acknowledge", intOne, expectedRevision: 2).ConfigureAwait(false);
            Require(acknowledged.ExitCode == 0 && acknowledged.StandardOutput.Contains("intention=pending", StringComparison.Ordinal) &&
                    acknowledged.StandardOutput.Contains("acknowledged=true", StringComparison.Ordinal) &&
                    acknowledged.StandardOutput.Contains("notified=true", StringComparison.Ordinal),
                "Acknowledge changed the intention state or dropped the notification marker (stdout=" + acknowledged.StandardOutput.Trim() + ").");
            var completed = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-intention-complete", intOne, expectedRevision: 3,
                S2T5TransitionDraft(completeReason)).ConfigureAwait(false);
            Require(completed.ExitCode == 0 && completed.StandardOutput.Contains("intention=done", StringComparison.Ordinal) &&
                    completed.StandardOutput.Contains("notified=true", StringComparison.Ordinal) &&
                    completed.StandardOutput.Contains("acknowledged=true", StringComparison.Ordinal) &&
                    completed.StandardOutput.Contains($"rationale={completeReason}", StringComparison.Ordinal),
                "Completion did not record the done state with its rationale while keeping the markers (stdout=" + completed.StandardOutput.Trim() + ").");
            var completeWithoutReason = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-intention-complete", intTwo, expectedRevision: 1).ConfigureAwait(false);
            Require(completeWithoutReason.ExitCode != 0 && completeWithoutReason.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "An intention was completed without an explicit rationale.");
            var cancelled = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-intention-cancel", intTwo, expectedRevision: 1,
                S2T5TransitionDraft(cancelReason)).ConfigureAwait(false);
            Require(cancelled.ExitCode == 0 && cancelled.StandardOutput.Contains("intention=cancelled", StringComparison.Ordinal),
                "Cancel did not record the cancelled state (stdout=" + cancelled.StandardOutput.Trim() + ").");
            var completeCancelled = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-intention-complete", intTwo, expectedRevision: 2,
                S2T5TransitionDraft(completeReason)).ConfigureAwait(false);
            Require(completeCancelled.ExitCode != 0 && completeCancelled.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A cancelled intention was completed.");
            var superseded = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-intention-supersede", intOld, expectedRevision: 1,
                S2T5TransitionDraft(supersedeReason, replacementResourceId: intNew)).ConfigureAwait(false);
            Require(superseded.ExitCode == 0 && superseded.StandardOutput.Contains("intention=superseded", StringComparison.Ordinal) &&
                    superseded.StandardOutput.Contains($"supersededBy={intNew}", StringComparison.Ordinal) &&
                    superseded.StandardOutput.Contains($"replacement={intNew}", StringComparison.Ordinal),
                "Supersede did not record the superseded state and its explicit replacement item (stdout=" + superseded.StandardOutput.Trim() + ").");
            var conflictingSupersede = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-intention-supersede", intOld, expectedRevision: 2,
                S2T5TransitionDraft(supersedeReason, replacementResourceId: intTwo)).ConfigureAwait(false);
            Require(conflictingSupersede.ExitCode != 0 && conflictingSupersede.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A conflicting supersession was accepted and overwrote the recorded replacement.");
            var afterConflict = await ReadS2T5LifecycleAsync(connectionsExecutable, fixture, intOld).ConfigureAwait(false);
            Require(afterConflict.ExitCode == 0 && afterConflict.StandardOutput.Contains($"supersededBy={intNew}", StringComparison.Ordinal) &&
                    afterConflict.StandardOutput.Contains("intention=superseded", StringComparison.Ordinal),
                "A refused conflicting supersession still changed the recorded replacement (stdout=" + afterConflict.StandardOutput.Trim() + ").");
            var supersedeWithoutReplacement = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-intention-supersede", intOld, expectedRevision: 2,
                S2T5TransitionDraft(supersedeReason)).ConfigureAwait(false);
            Require(supersedeWithoutReplacement.ExitCode != 0 && supersedeWithoutReplacement.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A supersession without an explicit replacement item was accepted.");
            var supersedeUnknownReplacement = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-intention-supersede", intOld, expectedRevision: 2,
                S2T5TransitionDraft(supersedeReason, replacementResourceId: "s2t5-not-present")).ConfigureAwait(false);
            Require(supersedeUnknownReplacement.ExitCode != 0 && supersedeUnknownReplacement.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A supersession naming an item that does not exist was accepted.");
            var supersedeSelf = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-intention-supersede", intOld, expectedRevision: 2,
                S2T5TransitionDraft(supersedeReason, replacementResourceId: intOld)).ConfigureAwait(false);
            Require(supersedeSelf.ExitCode != 0 && supersedeSelf.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "An intention superseded itself.");
            Console.WriteLine("PASS intention lifecycle keeps notified/acknowledged distinct from done and records explicit, non-overwriting supersession");

            // Temporal validity: occurred, recorded, validity bounds, review
            // deadline, precision, timezone and unknown all round-trip exactly.
            var validitySet = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-validity-set", qOpen, expectedRevision: 1,
                S2T5TransitionDraft(
                    occurred: S2T5Stamp("exact", instantUtc: now, zoneOffsetMinutes: 420),
                    recorded: S2T5Stamp("exact", instantUtc: recordedUtc, zoneOffsetMinutes: 420),
                    validFrom: S2T5Stamp("month", year: 2026, month: 10),
                    validUntil: S2T5Stamp("unknown"),
                    reviewAfter: S2T5Stamp("year", year: 2027, zoneOffsetMinutes: -300))).ConfigureAwait(false);
            Require(validitySet.ExitCode == 0, "A valid validity update was refused (exit=" + validitySet.ExitCode + "; stderr=" + validitySet.StandardError.Trim() + ").");
            var validityRead = await ReadS2T5LifecycleAsync(connectionsExecutable, fixture, qOpen).ConfigureAwait(false);
            var occurredText = JsonSerializer.Serialize(now).Trim('"');
            var recordedText = JsonSerializer.Serialize(recordedUtc).Trim('"');
            Require(validityRead.ExitCode == 0 &&
                    validityRead.StandardOutput.Contains($"occurred=exact:{occurredText}@+07:00", StringComparison.Ordinal) &&
                    validityRead.StandardOutput.Contains($"recorded=exact:{recordedText}@+07:00", StringComparison.Ordinal) &&
                    validityRead.StandardOutput.Contains("validFrom=month:2026-10@unknown", StringComparison.Ordinal) &&
                    validityRead.StandardOutput.Contains("validUntil=unknown", StringComparison.Ordinal) &&
                    validityRead.StandardOutput.Contains("reviewAfter=year:2027@-05:00", StringComparison.Ordinal),
                "The temporal validity block did not round-trip its precision, timezone and unknown values (stdout=" + validityRead.StandardOutput.Trim() + ").");
            var coarseValiditySet = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-validity-set", coarseItem, expectedRevision: 1,
                S2T5TransitionDraft(
                    occurred: S2T5Stamp("day", year: 2026, month: 10, day: 5, zoneOffsetMinutes: 420),
                    recorded: S2T5Stamp("exact", instantUtc: recordedUtc, zoneOffsetMinutes: -300),
                    validFrom: S2T5Stamp("day", year: 2026, month: 10, day: 1, zoneOffsetMinutes: 420),
                    validUntil: S2T5Stamp("year", year: 2027),
                    reviewAfter: S2T5Stamp("unknown"))).ConfigureAwait(false);
            Require(coarseValiditySet.ExitCode == 0, "A coarse-time validity update was refused (stderr=" + coarseValiditySet.StandardError.Trim() + ").");
            var coarseValidityRead = await ReadS2T5LifecycleAsync(connectionsExecutable, fixture, coarseItem).ConfigureAwait(false);
            Require(coarseValidityRead.ExitCode == 0 &&
                    coarseValidityRead.StandardOutput.Contains("occurred=day:2026-10-5@+07:00", StringComparison.Ordinal) &&
                    coarseValidityRead.StandardOutput.Contains("validFrom=day:2026-10-1@+07:00", StringComparison.Ordinal) &&
                    coarseValidityRead.StandardOutput.Contains("validUntil=year:2027@unknown", StringComparison.Ordinal) &&
                    coarseValidityRead.StandardOutput.Contains("reviewAfter=unknown", StringComparison.Ordinal),
                "A coarse-time item did not keep its day/year precision and unknown review deadline (stdout=" + coarseValidityRead.StandardOutput.Trim() + ").");
            var wrongRecorded = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-validity-set", qOpen, expectedRevision: 2,
                S2T5TransitionDraft(recorded: S2T5Stamp("exact", instantUtc: now.AddMinutes(30), zoneOffsetMinutes: 420))).ConfigureAwait(false);
            Require(wrongRecorded.ExitCode != 0 && wrongRecorded.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A recorded lifecycle time that disagrees with the subject envelope was accepted.");
            var wrongOccurred = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-validity-set", qOpen, expectedRevision: 2,
                S2T5TransitionDraft(occurred: S2T5Stamp("exact", instantUtc: now.AddSeconds(90), zoneOffsetMinutes: 420))).ConfigureAwait(false);
            Require(wrongOccurred.ExitCode != 0 && wrongOccurred.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "An occurred lifecycle time that disagrees with the subject envelope was accepted.");
            var reversedWindow = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-validity-set", coarseItem, expectedRevision: 2,
                S2T5TransitionDraft(validFrom: S2T5Stamp("year", year: 2030), validUntil: S2T5Stamp("year", year: 2026))).ConfigureAwait(false);
            Require(reversedWindow.ExitCode != 0 && reversedWindow.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A validity window that starts after it ends was accepted.");
            var incompleteDay = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-validity-set", coarseItem, expectedRevision: 2,
                S2T5TransitionDraft(validFrom: S2T5Stamp("day", year: 2026, month: 10))).ConfigureAwait(false);
            Require(incompleteDay.ExitCode != 0 && incompleteDay.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A day-precision stamp without a known day was accepted.");
            var fabricatedUnknown = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-validity-set", coarseItem, expectedRevision: 2,
                S2T5TransitionDraft(validUntil: S2T5Stamp("unknown", year: 2026))).ConfigureAwait(false);
            Require(fabricatedUnknown.ExitCode != 0 && fabricatedUnknown.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "An unknown timestamp was fabricated into a year.");
            var impossibleZone = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-validity-set", coarseItem, expectedRevision: 2,
                S2T5TransitionDraft(validFrom: S2T5Stamp("year", year: 2026, zoneOffsetMinutes: 1000))).ConfigureAwait(false);
            Require(impossibleZone.ExitCode != 0 && impossibleZone.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "An impossible timezone offset was accepted.");
            var stampsOnOtherAct = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-publish", decision, expectedRevision: 6,
                S2T5TransitionDraft(validFrom: S2T5Stamp("year", year: 2026))).ConfigureAwait(false);
            Require(stampsOnOtherAct.ExitCode != 0 && stampsOnOtherAct.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A non-temporal act carried temporal stamps.");
            Console.WriteLine("PASS temporal validity preserves occurred/recorded, validity bounds, precision, timezone and unknown without fabrication");

            // R12: saving an AI note never re-attributes it, and only the user
            // can confirm belief.
            var assistantDraft = S2T5ItemDraft("synthesis", "assistant", "paraphrase", "reportedSource",
                "Synthetic S2-T5 AI answer", "Synthetic S2-T5 AI answer body revision two; never a real owner secret.", now, "l2");
            var spoofAuthor = await WriteS2T5ItemDraftResultAsync(connectionsExecutable, fixture, aiAnswer, zone,
                S2T5ItemDraft("synthesis", "user", "paraphrase", "userConfirmed",
                    "Synthetic S2-T5 AI answer", "Synthetic S2-T5 AI answer body revision two; never a real owner secret.", now, "l2")).ConfigureAwait(false);
            Require(spoofAuthor.ExitCode != 0 && spoofAuthor.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "Saving an AI note as user-written and user-verified was accepted.");
            var spoofMethod = await WriteS2T5ItemDraftResultAsync(connectionsExecutable, fixture, aiAnswer, zone,
                S2T5ItemDraft("synthesis", "assistant", "directWrite", "reportedSource",
                    "Synthetic S2-T5 AI answer", "Synthetic S2-T5 AI answer body revision two; never a real owner secret.", now, "l2")).ConfigureAwait(false);
            Require(spoofMethod.ExitCode != 0 && spoofMethod.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "Saving an AI note changed its creation method.");
            await ReviseS2T5ItemDraftAsync(connectionsExecutable, fixture, aiAnswer, zone, assistantDraft, writeRevision: 1, expectedRevision: 2).ConfigureAwait(false);
            var revisedAnswer = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", aiAnswer).ConfigureAwait(false);
            Require(revisedAnswer.ExitCode == 0 && revisedAnswer.StandardOutput.Contains("author=assistant", StringComparison.Ordinal) &&
                    revisedAnswer.StandardOutput.Contains("evidence=reportedSource", StringComparison.Ordinal) &&
                    revisedAnswer.StandardOutput.Contains("Synthetic S2-T5 AI answer body revision two", StringComparison.Ordinal),
                "An AI-attributed record did not keep its attribution while its body was revised (stdout=" + revisedAnswer.StandardOutput.Trim() + ").");
            var assistantConfirmed = await WriteS2T5ItemDraftResultAsync(connectionsExecutable, fixture, "s2t5-assistant-confirmed", zone,
                S2T5ItemDraft("synthesis", "assistant", "paraphrase", "userConfirmed",
                    "Synthetic S2-T5 assistant confirmed claim", "Synthetic S2-T5 assistant confirmed body; never a real owner secret.", now, "l2")).ConfigureAwait(false);
            Require(assistantConfirmed.ExitCode != 0 && assistantConfirmed.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "An assistant-authored claim was created with user-confirmed evidence.");
            var toolConfirmed = await WriteS2T5ItemDraftResultAsync(connectionsExecutable, fixture, "s2t5-tool-confirmed", zone,
                S2T5ItemDraft("claim", "tool", "verbatimExtract", "userConfirmed",
                    "Synthetic S2-T5 tool confirmed claim", "Synthetic S2-T5 tool confirmed body; never a real owner secret.", now)).ConfigureAwait(false);
            Require(toolConfirmed.ExitCode != 0 && toolConfirmed.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A tool-authored claim was created with user-confirmed evidence.");
            var toolReviseSpoof = await WriteS2T5ItemDraftResultAsync(connectionsExecutable, fixture, toolObservation, zone,
                S2T5ItemDraft("observation", "tool", "verbatimExtract", "userConfirmed",
                    "Synthetic S2-T5 tool observation", "Synthetic S2-T5 tool observation body revision two; never a real owner secret.", now, "l0")).ConfigureAwait(false);
            Require(toolReviseSpoof.ExitCode != 0 && toolReviseSpoof.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A tool-observed record was revised into user-confirmed belief.");
            await ReviseS2T5ItemDraftAsync(connectionsExecutable, fixture, trustedUser, zone,
                S2T5ItemDraft("claim", "user", "directWrite", "userConfirmed",
                    "Synthetic S2-T5 user-confirmed claim", "Synthetic S2-T5 user-confirmed body revision two; never a real owner secret.", now), writeRevision: 1, expectedRevision: 2).ConfigureAwait(false);
            Console.WriteLine("PASS user saving an AI note preserves authorship and verification while the user's own claims still grow evidence");

            // A trusted executor comes from the authenticated context: a body
            // that names an executor, a state, or an act is never honoured.
            var spoofedPublish = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-publish", decision, expectedRevision: 6,
                S2T5TransitionDraft(executor: "agent", state: "rejected")).ConfigureAwait(false);
            Require(spoofedPublish.ExitCode == 0 && spoofedPublish.StandardOutput.Contains("publication=published", StringComparison.Ordinal) &&
                    spoofedPublish.StandardOutput.Contains("executor=owner", StringComparison.Ordinal) &&
                    spoofedPublish.StandardOutput.Contains("HISTORY aspect=publication act=publish from=proposal to=published executor=owner", StringComparison.Ordinal),
                "A lifecycle body declared an executor or a state and it was honoured (stdout=" + spoofedPublish.StandardOutput.Trim() + ").");
            var agentLifecycleRead = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "lifecycle.read",
                resourceId = decision
            }).ConfigureAwait(false);
            Require(agentLifecycleRead.TryGetProperty("error", out var agentLifecycleError) &&
                    agentLifecycleError.GetString() == "forbidden",
                "The agent channel served an owner lifecycle operation (reply=" + agentLifecycleRead.GetRawText() + ").");
            var agentLifecycleTransition = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "lifecycle.publish",
                resourceId = decision,
                resourceRevision = 7L
            }).ConfigureAwait(false);
            Require(agentLifecycleTransition.TryGetProperty("error", out var agentTransitionError) &&
                    agentTransitionError.GetString() == "forbidden",
                "The agent channel served an owner lifecycle transition (reply=" + agentLifecycleTransition.GetRawText() + ").");
            Console.WriteLine("PASS the trusted executor and the owner-only channel boundary reject body-declared executors, states and agent lifecycle acts");

            // Retained historical revisions are genuinely retrievable: the
            // archive holds the exact retained bytes under the derived ID, and a
            // grant over the archive never reaches the live item.
            var retained = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-retain", historyItem, expectedRevision: 1).ConfigureAwait(false);
            Require(retained.ExitCode == 0 && retained.StandardOutput.Contains("RETAINED revision=1 archive=arc-s2t5-history-r1 executor=owner", StringComparison.Ordinal),
                "Retaining a revision did not record the derived archive resource (stdout=" + retained.StandardOutput.Trim() + ").");
            await ReviseS2T5ItemDraftAsync(connectionsExecutable, fixture, historyItem, zone,
                S2T5ItemDraft("claim", "user", "directWrite", "unverified", "Synthetic S2-T5 revision history item",
                    "Synthetic S2-T5 revision two body; never a real owner secret.", now), writeRevision: 1, expectedRevision: 2).ConfigureAwait(false);
            var retainedRead = await ReadS2T5RetainedAsync(connectionsExecutable, fixture, historyItem, revision: 1).ConfigureAwait(false);
            Require(retainedRead.ExitCode == 0 &&
                    retainedRead.StandardOutput.Contains("subjectRevision=1", StringComparison.Ordinal) &&
                    retainedRead.StandardOutput.Contains("archive=arc-s2t5-history-r1", StringComparison.Ordinal) &&
                    retainedRead.StandardOutput.Contains("Synthetic S2-T5 revision one body", StringComparison.Ordinal) &&
                    retainedRead.StandardOutput.Contains("revision=1", StringComparison.Ordinal),
                "The retained historical revision was not retrievable with its exact revision (exit=" + retainedRead.ExitCode + "; stdout=" + retainedRead.StandardOutput.Trim() + "; stderr=" + retainedRead.StandardError.Trim() + ").");
            var liveRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", historyItem).ConfigureAwait(false);
            Require(liveRead.ExitCode == 0 && liveRead.StandardOutput.Contains("Synthetic S2-T5 revision two body", StringComparison.Ordinal) &&
                    liveRead.StandardOutput.Contains("revision=2", StringComparison.Ordinal),
                "The live item did not serve its newest revision after a retention (stdout=" + liveRead.StandardOutput.Trim() + ").");
            var retainedWrongRevision = await ReadS2T5RetainedAsync(connectionsExecutable, fixture, historyItem, revision: 2).ConfigureAwait(false);
            Require(retainedWrongRevision.ExitCode != 0 && retainedWrongRevision.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A revision that was never retained was served from the archive.");
            var retainedSecond = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-retain", historyItem, expectedRevision: 2).ConfigureAwait(false);
            Require(retainedSecond.ExitCode == 0 &&
                    retainedSecond.StandardOutput.Contains("RETAINED revision=2 archive=arc-s2t5-history-r2 executor=owner", StringComparison.Ordinal),
                "A second live revision could not be retained under its own derived archive (exit=" + retainedSecond.ExitCode + "; stdout=" + retainedSecond.StandardOutput.Trim() + "; stderr=" + retainedSecond.StandardError.Trim() + ").");
            var retainedSecondRead = await ReadS2T5RetainedAsync(connectionsExecutable, fixture, historyItem, revision: 2).ConfigureAwait(false);
            Require(retainedSecondRead.ExitCode == 0 &&
                    retainedSecondRead.StandardOutput.Contains("subjectRevision=2", StringComparison.Ordinal) &&
                    retainedSecondRead.StandardOutput.Contains("archive=arc-s2t5-history-r2", StringComparison.Ordinal) &&
                    retainedSecondRead.StandardOutput.Contains("Synthetic S2-T5 revision two body", StringComparison.Ordinal),
                "The second retained historical revision was not retrievable with its exact revision (exit=" + retainedSecondRead.ExitCode + "; stdout=" + retainedSecondRead.StandardOutput.Trim() + "; stderr=" + retainedSecondRead.StandardError.Trim() + ").");
            var retainedAgain = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-retain", historyItem, expectedRevision: 3).ConfigureAwait(false);
            Require(retainedAgain.ExitCode != 0 && retainedAgain.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "The same revision was retained twice.");
            var archiveSession = await IssueS2T5ArchiveGrantAsync(connectionsExecutable, fixture, "s2t5-archive", "arc-s2t5-history-r1").ConfigureAwait(false);
            var archivedAgentRead = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.memory.read",
                sessionToken = ReadSessionBearer(archiveSession.SessionFile),
                accessOperation = "memory.read",
                resourceId = "arc-s2t5-history-r1",
                resourceRevision = 1L
            }).ConfigureAwait(false);
            Require(archivedAgentRead.TryGetProperty("status", out var archivedStatus) && archivedStatus.GetString() == "memory" &&
                    archivedAgentRead.TryGetProperty("memoryItem", out var archivedItem) && archivedItem.ValueKind == JsonValueKind.Object &&
                    archivedItem.GetProperty("body").GetString()!.Contains("Synthetic S2-T5 revision one body", StringComparison.Ordinal),
                "A retained historical revision was not readable inside its own granted archive scope (reply=" + archivedAgentRead.GetRawText() + ").");
            var liveUnderArchiveGrant = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.memory.read",
                sessionToken = ReadSessionBearer(archiveSession.SessionFile),
                accessOperation = "memory.read",
                resourceId = historyItem,
                resourceRevision = 2L
            }).ConfigureAwait(false);
            Require(liveUnderArchiveGrant.TryGetProperty("error", out var liveGrantError) && liveGrantError.GetString() == "resource_unavailable",
                "A grant over one retained revision opened the live item or a newer revision (reply=" + liveUnderArchiveGrant.GetRawText() + ").");
            var lifecycleReadLeak = await ReadS2T5LifecycleAsync(connectionsExecutable, fixture, historyItem).ConfigureAwait(false);
            Require(lifecycleReadLeak.ExitCode == 0 &&
                    !lifecycleReadLeak.StandardOutput.Contains("Synthetic S2-T5 revision two body", StringComparison.Ordinal) &&
                    !lifecycleReadLeak.StandardOutput.Contains("s2t5-private-locator", StringComparison.Ordinal),
                "A lifecycle read disclosed subject content (stdout=" + lifecycleReadLeak.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS retained revisions are retrievable at their exact revision and never open newer or live access");

            // Lock boundary and restart durability.
            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "Owner lock did not complete for the S2-T5 lock boundary check.");
            var lockedLifecycleRead = await ReadS2T5LifecycleAsync(connectionsExecutable, fixture, decision).ConfigureAwait(false);
            Require(lockedLifecycleRead.ExitCode != 0 && !lockedLifecycleRead.StandardOutput.Contains("LIFECYCLE id=", StringComparison.Ordinal),
                "A locked lifecycle read was served.");
            var lockedTransition = await TransitionS2T5LifecycleAsync(connectionsExecutable, fixture, "lifecycle-publish", decision, expectedRevision: 7).ConfigureAwait(false);
            Require(lockedTransition.ExitCode != 0 && !lockedTransition.StandardOutput.Contains("LIFECYCLE stored", StringComparison.Ordinal),
                "A locked lifecycle transition was accepted.");
            var lockedRetainedRead = await ReadS2T5RetainedAsync(connectionsExecutable, fixture, historyItem, revision: 1).ConfigureAwait(false);
            Require(lockedRetainedRead.ExitCode != 0 && !lockedRetainedRead.StandardOutput.Contains("Synthetic S2-T5 revision one body", StringComparison.Ordinal),
                "A locked retained revision read was served.");
            var lockedAgentArchiveRead = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.memory.read",
                sessionToken = ReadSessionBearer(archiveSession.SessionFile),
                accessOperation = "memory.read",
                resourceId = "arc-s2t5-history-r1",
                resourceRevision = 1L
            }).ConfigureAwait(false);
            Require(lockedAgentArchiveRead.TryGetProperty("error", out var lockedArchiveError),
                "A locked vault served an archived revision read (reply=" + lockedAgentArchiveRead.GetRawText() + ").");

            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false)).Contains("vault=locked", StringComparison.Ordinal),
                "The restarted service did not return locked for the S2-T5 durability check.");
            var lockedAfterRestart = await ReadS2T5LifecycleAsync(connectionsExecutable, fixture, decision).ConfigureAwait(false);
            Require(lockedAfterRestart.ExitCode != 0, "A locked lifecycle read after restart was served.");
            var reunlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(reunlock.ExitCode == 0 && reunlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The passphrase did not unlock the restarted vault for the S2-T5 durability check.");
            var afterRestart = await ReadS2T5LifecycleAsync(connectionsExecutable, fixture, decision).ConfigureAwait(false);
            Require(afterRestart.ExitCode == 0 &&
                    afterRestart.StandardOutput.Contains("publication=published", StringComparison.Ordinal) &&
                    afterRestart.StandardOutput.Contains("revision=7", StringComparison.Ordinal) &&
                    afterRestart.StandardOutput.Contains("HISTORY aspect=publication act=publish from=proposal to=published executor=owner", StringComparison.Ordinal) &&
                    afterRestart.StandardOutput.Contains("RETAINED", StringComparison.Ordinal) == false,
                "The lifecycle history did not survive restart and unlock (stdout=" + afterRestart.StandardOutput.Trim() + ").");
            var historyRecordAfterRestart = await ReadS2T5LifecycleAsync(connectionsExecutable, fixture, historyItem).ConfigureAwait(false);
            Require(historyRecordAfterRestart.ExitCode == 0 &&
                    historyRecordAfterRestart.StandardOutput.Contains("RETAINED revision=1 archive=arc-s2t5-history-r1 executor=owner", StringComparison.Ordinal),
                "The retained revision record did not survive restart and unlock (stdout=" + historyRecordAfterRestart.StandardOutput.Trim() + ").");
            var retainedAfterRestart = await ReadS2T5RetainedAsync(connectionsExecutable, fixture, historyItem, revision: 1).ConfigureAwait(false);
            Require(retainedAfterRestart.ExitCode == 0 &&
                    retainedAfterRestart.StandardOutput.Contains("Synthetic S2-T5 revision one body", StringComparison.Ordinal),
                "The retained historical revision did not survive restart and unlock (stdout=" + retainedAfterRestart.StandardOutput.Trim() + ").");
            var intentionAfterRestart = await ReadS2T5LifecycleAsync(connectionsExecutable, fixture, intOne).ConfigureAwait(false);
            Require(intentionAfterRestart.ExitCode == 0 &&
                    intentionAfterRestart.StandardOutput.Contains("intention=done", StringComparison.Ordinal) &&
                    intentionAfterRestart.StandardOutput.Contains("notified=true", StringComparison.Ordinal) &&
                    intentionAfterRestart.StandardOutput.Contains("acknowledged=true", StringComparison.Ordinal),
                "The intention markers and done state did not survive restart and unlock (stdout=" + intentionAfterRestart.StandardOutput.Trim() + ").");
            var unknownLifecycleRead = await ReadS2T5LifecycleAsync(connectionsExecutable, fixture, "s2t5-not-present").ConfigureAwait(false);
            Require(unknownLifecycleRead.ExitCode == 3 && unknownLifecycleRead.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "An unknown lifecycle subject was not denied with the generic code (existence oracle).");
            Console.WriteLine("PASS lifecycle records, history and retained revisions survive lock and restart while lock denies generically");

            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            VerifyS2T5CiphertextOnly(fixture, expectedResources: 26);
            Console.WriteLine("PASS persisted lifecycle/archive/canonical/temp files carry no plaintext");
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

        Console.WriteLine("LIMIT synthetic S2-T5 fixture only; the retained-revision archive holds only revisions an explicit retain act captured (a revision revised before retention is not recoverable, matching the S2-T2 live-revision protocol), scheduling/retention/reminder triggers stay in Sprint 7, the owner channel is the only lifecycle surface here so an agent intention view and MCP lifecycle tools stay in later tasks, and same-owner OS compromise remains outside the threat model.");
    }
}
