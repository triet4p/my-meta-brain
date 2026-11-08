using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
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
    private const string S15T4ModelRevision = "614241f622f53c4eeff9890bdc4f31cfecc418b3";

    public static async Task RunS15T4PurposeAwareRecallAsync(string connectionsExecutable, string cliExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S15-T4 owner recall smoke is Windows-only.");
        }

        var modelDirectory = Path.Combine(
            SmokeFixture.FindRepositoryRoot(), "runtime", "s15-t1-model-staging", S15T4ModelRevision, "model");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        try
        {
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var passphrase = CreateSyntheticPassphrase();
            var provision = await RunOwnerCommandWithInputAsync(
                cliExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The S15-T4 synthetic owner vault could not be provisioned.");
            var unlock = await RunOwnerCommandWithInputAsync(
                cliExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0 && unlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The S15-T4 synthetic owner vault could not be unlocked.");

            var allowed = await CreateFolderAsync(cliExecutable, fixture, "root", "inside", 0).ConfigureAwait(false);
            var outside = await CreateFolderAsync(cliExecutable, fixture, "root", "outside", allowed.TreeRevision).ConfigureAwait(false);
            var treeRevision = outside.TreeRevision;

            async Task<S15T4RecallSource> ImportAsync(string name, string content, string? parentId = null)
            {
                var workspaceStatus = await ReadWorkspaceStatusAsync(cliExecutable, fixture).ConfigureAwait(false);
                treeRevision = WorkspaceNumber(workspaceStatus, "revision");
                var path = await WriteWorkspaceInputAsync(
                    fixture, "s15t4-" + name, Encoding.UTF8.GetBytes(content)).ConfigureAwait(false);
                var imported = await ImportWorkspaceFileAsync(
                    cliExecutable, fixture, parentId ?? allowed.FolderId, name, path, treeRevision).ConfigureAwait(false);
                treeRevision = imported.TreeRevision;
                return new S15T4RecallSource(imported.FileId, name, content, 1);
            }

            var currentExperienceText =
                "S15_T4_REGION Current experience: I tried a calibration step on content date 2026-03-04; this is reported source evidence.\n";
            var currentExperience = await ImportAsync("experience-current.txt", currentExperienceText).ConfigureAwait(false);
            var currentExperienceOwner = await CreateS15T4AnnotationAsync(
                cliExecutable, fixture, currentExperience, currentExperienceText.TrimEnd('\n'), "experience", "observation", "user")
                .ConfigureAwait(false);
            var currentExperienceSuggestion = await CreateS15T4AnnotationAsync(
                cliExecutable, fixture, currentExperience, currentExperienceText.TrimEnd('\n'), "experience", "observation", "assistant", suggestion: true)
                .ConfigureAwait(false);

            var retainedExperienceText =
                "S15_T4_REGION Retained experience: I tried a superseded calibration process on content date 2026-04-05.\n";
            var retainedExperience = await ImportAsync("experience-retained.txt", retainedExperienceText).ConfigureAwait(false);
            var retainedExperienceAnnotation = await CreateS15T4AnnotationAsync(
                cliExecutable, fixture, retainedExperience, retainedExperienceText.TrimEnd('\n'), "experience", "lesson", "user")
                .ConfigureAwait(false);
            var replacementPath = await WriteWorkspaceInputAsync(
                fixture,
                "s15t4-experience-retained-revision-2.txt",
                Encoding.UTF8.GetBytes("Replacement current source revision with no retired calibration marker.\n"))
                .ConfigureAwait(false);
            var revised = await RunOwnerCommandAsync(
                cliExecutable,
                "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                "--file-id", retainedExperience.FileId,
                "--expected-content-revision", "1",
                "--input-file", replacementPath).ConfigureAwait(false);
            Require(revised.ExitCode == 0 && revised.StandardOutput.Contains("contentRevision=2", StringComparison.Ordinal),
                "The synthetic retained experience did not advance to content revision 2.");
            treeRevision = WorkspaceNumber(revised.StandardOutput, "revision");
            retainedExperience = retainedExperience with { Revision = 2 };

            var temporalKnownText =
                "S15_T4_TEMPORAL_KNOWN Content date 2026-05-06; the source content does not declare the stored event or record time.\n";
            var temporalUnknownText =
                "S15_T4_TEMPORAL_UNKNOWN Content date 2026-05-07; occurred and recorded event stamps were not supplied.\n";
            var temporal = await ImportAsync("temporal.txt", temporalKnownText + temporalUnknownText).ConfigureAwait(false);
            var temporalKnown = await CreateS15T4AnnotationAsync(
                cliExecutable, fixture, temporal, temporalKnownText.TrimEnd('\n'), "world", "observation", "user")
                .ConfigureAwait(false);
            var temporalUnknown = await CreateS15T4AnnotationAsync(
                cliExecutable, fixture, temporal, temporalUnknownText.TrimEnd('\n'), "world", "observation", "user")
                .ConfigureAwait(false);
            var knownLifecycleDraft = JsonSerializer.SerializeToUtf8Bytes(new
            {
                creationMethod = "directWrite",
                reason = "The stored occurred stamp differs from the source content date; no recorded stamp is supplied.",
                occurred = S15T4ExactStamp("2020-02-03T04:05:06Z"),
                recorded = (object?)null
            });
            await CreateS15T4LifecycleAsync(cliExecutable, fixture, temporalKnown.Id, knownLifecycleDraft, "temporal-known")
                .ConfigureAwait(false);
            await CreateS15T4LifecycleAsync(cliExecutable, fixture, temporalUnknown.Id, null, "temporal-unknown")
                .ConfigureAwait(false);

            var intentionLines = new[]
            {
                "S15_T4_INTENTION_PENDING I will recheck the synthetic sequence tomorrow; remind me, but no completion is recorded.",
                "S15_T4_INTENTION_ACKNOWLEDGED I will inspect the synthetic result after the reminder; acknowledgement is not completion.",
                "S15_T4_INTENTION_DONE I will inspect the synthetic result; this intention is explicitly completed.",
                "S15_T4_INTENTION_ARCHIVED I will revisit the synthetic archived note; it remains an intention record."
            };
            var intentionText = string.Join('\n', intentionLines) + "\n";
            var intentions = await ImportAsync("intentions.txt", intentionText).ConfigureAwait(false);
            var pending = await CreateS15T4AnnotationAsync(
                cliExecutable, fixture, intentions, intentionLines[0], "intention", "intention", "user").ConfigureAwait(false);
            var acknowledged = await CreateS15T4AnnotationAsync(
                cliExecutable, fixture, intentions, intentionLines[1], "intention", "intention", "user").ConfigureAwait(false);
            var done = await CreateS15T4AnnotationAsync(
                cliExecutable, fixture, intentions, intentionLines[2], "intention", "intention", "user").ConfigureAwait(false);
            var archived = await CreateS15T4AnnotationAsync(
                cliExecutable, fixture, intentions, intentionLines[3], "intention", "intention", "user").ConfigureAwait(false);
            foreach (var annotation in new[] { pending, acknowledged, done, archived })
            {
                await CreateS15T4LifecycleAsync(cliExecutable, fixture, annotation.Id, null, annotation.Id)
                    .ConfigureAwait(false);
            }

            var acknowledgedResult = await RunOwnerCommandAsync(
                cliExecutable, "owner", "lifecycle-intention-acknowledge", "--control-pipe", fixture.ControlPipe,
                "--resource-id", acknowledged.Id, "--expected-revision", "1").ConfigureAwait(false);
            Require(acknowledgedResult.ExitCode == 0 &&
                    acknowledgedResult.StandardOutput.Contains("intention=pending", StringComparison.Ordinal) &&
                    acknowledgedResult.StandardOutput.Contains("acknowledged=true", StringComparison.Ordinal),
                "The acknowledgement smoke did not preserve a pending intention.");
            var doneResult = await RunOwnerCommandAsync(
                cliExecutable, "owner", "lifecycle-intention-complete", "--control-pipe", fixture.ControlPipe,
                "--resource-id", done.Id, "--expected-revision", "1",
                "--reason", "Owner explicitly completes the synthetic intention after the source says it is done.")
                .ConfigureAwait(false);
            Require(doneResult.ExitCode == 0 && doneResult.StandardOutput.Contains("intention=done", StringComparison.Ordinal),
                "The explicit completion smoke did not record the done intention state (exit=" +
                doneResult.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stdout=" +
                doneResult.StandardOutput.Trim() + "; stderr=" + doneResult.StandardError.Trim() + ").");
            var archivedResult = await RunOwnerCommandAsync(
                cliExecutable, "owner", "recall-apply", "--control-pipe", fixture.ControlPipe,
                "--resource-id", archived.Id, "--act", "archive", "--reason", "synthetic archived recall evidence")
                .ConfigureAwait(false);
            Require(archivedResult.ExitCode == 0 && archivedResult.StandardOutput.Contains("state=archived", StringComparison.Ordinal),
                "The owner could not archive the synthetic passage annotation (exit=" +
                archivedResult.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stdout=" +
                archivedResult.StandardOutput.Trim() + "; stderr=" + archivedResult.StandardError.Trim() + ").");

            var actionText =
                "S15_T4_ACTION_EFFECT I adjusted the synthetic dial and observed lower vibration; this is an association, not a causal finding.\n";
            var action = await ImportAsync("action-effect.txt", actionText).ConfigureAwait(false);
            var actionAnnotation = await CreateS15T4AnnotationAsync(
                cliExecutable, fixture, action, actionText.TrimEnd('\n'), "action-effect", "observation", "user")
                .ConfigureAwait(false);

            const string tieContent = "S15_T4_TIE_MARKER shared synthetic tie evidence.\n";
            _ = await ImportAsync("tie-a.txt", tieContent).ConfigureAwait(false);
            _ = await ImportAsync("tie-b.txt", tieContent).ConfigureAwait(false);
            const string outsideContent = "S15_T4_OUTSIDE_PRIVATE outside-only synthetic disclosure marker.\n";
            _ = await ImportAsync("outside-private.txt", outsideContent, outside.FolderId).ConfigureAwait(false);

            await VerifyAgentChannelCannotReachHybridSearchAsync(fixture).ConfigureAwait(false);

            var initialRebuild = await RunOwnerCommandAsync(
                cliExecutable,
                new[]
                {
                    "owner", "workspace-index-rebuild", "--control-pipe", fixture.ControlPipe,
                    "--model-directory", modelDirectory,
                },
                standardInput: null,
                timeout: TimeSpan.FromMinutes(6)).ConfigureAwait(false);
            Require(initialRebuild.ExitCode == 0 && initialRebuild.StandardOutput.Contains("WORKSPACE_INDEX", StringComparison.Ordinal),
                "The approved local passage generation did not build for the S15-T4 consumer smoke (stderr=" +
                initialRebuild.StandardError.Trim() + ").");

            var regionsFirst = await HybridSearchJsonAsync(
                cliExecutable, fixture, "S15_T4_REGION", allowed.FolderId, modelDirectory,
                current: "all", limit: 1, purpose: WorkspaceRecallPurposes.Experience).ConfigureAwait(false);
            Require(regionsFirst.GetProperty("literalTotal").GetInt32() == 2 &&
                    regionsFirst.GetProperty("semanticCandidateTotal").GetInt32() == 0 &&
                    regionsFirst.GetProperty("total").GetInt32() == 2,
                "Purpose filtering changed T3 literal/currentness counts for exact retained/current evidence.");
            var firstRecall = regionsFirst.GetProperty("recall");
            Require(firstRecall.GetProperty("totalRegions").GetInt32() == 2 &&
                    firstRecall.GetProperty("regions").GetArrayLength() == 1 &&
                    firstRecall.GetProperty("hasMore").GetBoolean(),
                "The exact-revision region page did not retain the full scoped count before paging.");
            var firstRegion = firstRecall.GetProperty("regions")[0];
            Require(firstRegion.GetProperty("contentRevision").GetInt64() == 1 &&
                    firstRegion.GetProperty("representativeRank").GetInt32() == 1 &&
                    firstRegion.GetProperty("total").GetInt32() == 1 &&
                    firstRegion.GetProperty("literalTotal").GetInt32() == 1 &&
                    firstRegion.GetProperty("semanticCandidateTotal").GetInt32() == 0 &&
                    firstRegion.GetProperty("selectionReasons").GetArrayLength() > 0,
                "The current exact-revision region lost its count, globally ranked representative or selection reason.");
            var nextRegion = await HybridSearchJsonAsync(
                cliExecutable, fixture, "S15_T4_REGION", allowed.FolderId, modelDirectory,
                current: "all", offset: 1, limit: 1, purpose: WorkspaceRecallPurposes.Experience).ConfigureAwait(false);
            var nextRecall = nextRegion.GetProperty("recall");
            Require(nextRecall.GetProperty("totalRegions").GetInt32() == 2 &&
                    nextRecall.GetProperty("regions").GetArrayLength() == 1 &&
                    nextRecall.GetProperty("regions")[0].GetProperty("stale").GetBoolean() &&
                    nextRecall.GetProperty("regions")[0].GetProperty("representativeRank").GetInt32() == 2,
                "The retained revision region did not preserve stale state and the representative's global rank.");
            var retainedHit = nextRecall.GetProperty("regions")[0].GetProperty("representative");
            Require(retainedHit.GetProperty("contentRevision").GetInt64() == 1 &&
                    retainedHit.GetProperty("stale").GetBoolean() &&
                    FindS15T4Annotation(retainedHit, retainedExperienceAnnotation.Id).GetProperty("anchorState").GetString() == "stale",
                "The retained region rebound its annotation or source evidence to the current revision.");

            var currentOnly = await HybridSearchJsonAsync(
                cliExecutable, fixture, "S15_T4_REGION", allowed.FolderId, modelDirectory,
                current: "current", purpose: WorkspaceRecallPurposes.Experience).ConfigureAwait(false);
            var historyOnly = await HybridSearchJsonAsync(
                cliExecutable, fixture, "S15_T4_REGION", allowed.FolderId, modelDirectory,
                current: "history", purpose: WorkspaceRecallPurposes.Experience).ConfigureAwait(false);
            Require(currentOnly.GetProperty("total").GetInt32() == 1 &&
                    historyOnly.GetProperty("total").GetInt32() == 1 &&
                    historyOnly.GetProperty("hits")[0].GetProperty("stale").GetBoolean(),
                "The T3 current/history filters changed on the purpose-aware owner path.");

            var experience = await HybridSearchJsonAsync(
                cliExecutable, fixture, "S15_T4_REGION", allowed.FolderId, modelDirectory,
                purpose: WorkspaceRecallPurposes.Experience).ConfigureAwait(false);
            var ownerEvidence = FindS15T4Annotation(experience.GetProperty("hits")[0], currentExperienceOwner.Id);
            var suggestionEvidence = FindS15T4Annotation(experience.GetProperty("hits")[0], currentExperienceSuggestion.Id);
            Require(ownerEvidence.GetProperty("origin").GetString() == "Owner" &&
                    ownerEvidence.GetProperty("reviewStatus").GetString() == "Accepted" &&
                    ownerEvidence.GetProperty("author").GetString() == "User" &&
                    ownerEvidence.GetProperty("executor").GetString() == "Owner" &&
                    ownerEvidence.GetProperty("evidence").GetString() == "ReportedSource" &&
                    suggestionEvidence.GetProperty("origin").GetString() == "AiSuggestion" &&
                    suggestionEvidence.GetProperty("reviewStatus").GetString() == "Pending" &&
                    suggestionEvidence.GetProperty("author").GetString() == "Assistant" &&
                    suggestionEvidence.GetProperty("executor").GetString() == "Owner" &&
                    suggestionEvidence.GetProperty("evidence").GetString() == "ReportedSource",
                "Purpose-aware recall lost owner authorship or the AI suggestion's pending provenance/evidence.");

            var semantic = await HybridSearchJsonAsync(
                cliExecutable, fixture,
                "Which procedure did the writer experiment with, and what happened afterwards?",
                allowed.FolderId, modelDirectory, limit: WorkspaceHybridSearchService.MaximumLimit,
                purpose: WorkspaceRecallPurposes.Experience).ConfigureAwait(false);
            var semanticHits = semantic.GetProperty("hits").EnumerateArray()
                .Where(hit => hit.GetProperty("evidenceClass").GetString() == WorkspaceHybridEvidenceClass.SemanticCandidate)
                .ToArray();
            Require(semanticHits.Length > 0 && semanticHits.All(hit =>
                    hit.GetProperty("recall").GetProperty("selectionReasons").EnumerateArray()
                        .Any(reason => reason.GetString()!.Contains("unconfirmed", StringComparison.OrdinalIgnoreCase)) &&
                    hit.GetProperty("recall").GetProperty("purposeEvidenceStatus").GetString() is "unknown" or "annotated"),
                "A semantic-only purpose-aware result was promoted from an unconfirmed candidate to supporting evidence.");

            var temporalKnownResult = await HybridSearchJsonAsync(
                cliExecutable, fixture, "S15_T4_TEMPORAL_KNOWN", allowed.FolderId, modelDirectory,
                purpose: WorkspaceRecallPurposes.Temporal).ConfigureAwait(false);
            var temporalKnownAnnotation = FindS15T4Annotation(temporalKnownResult.GetProperty("hits")[0], temporalKnown.Id);
            var knownLifecycle = temporalKnownAnnotation.GetProperty("lifecycle");
            Require(temporalKnownAnnotation.GetProperty("annotationRecordedAtUtc").ValueKind == JsonValueKind.String &&
                    DateTimeOffset.Parse(knownLifecycle.GetProperty("occurred").GetProperty("instantUtc").GetString()!, CultureInfo.InvariantCulture).Date ==
                    new DateTime(2020, 2, 3) &&
                    knownLifecycle.GetProperty("recorded").ValueKind == JsonValueKind.Null &&
                    knownLifecycle.GetProperty("lifecycleRecordedAtUtc").ValueKind == JsonValueKind.String,
                "Temporal recall conflated source content date, annotation record time and distinct occurred/recorded lifecycle stamps.");
            var contentDate = await HybridSearchJsonAsync(
                cliExecutable, fixture, "S15_T4_TEMPORAL_KNOWN", allowed.FolderId, modelDirectory,
                contentDateFrom: "2026-05-06", contentDateTo: "2026-05-06",
                purpose: WorkspaceRecallPurposes.Temporal).ConfigureAwait(false);
            var notContentDate = await HybridSearchJsonAsync(
                cliExecutable, fixture, "S15_T4_TEMPORAL_KNOWN", allowed.FolderId, modelDirectory,
                contentDateFrom: "2020-02-03", contentDateTo: "2020-02-03",
                purpose: WorkspaceRecallPurposes.Temporal).ConfigureAwait(false);
            Require(contentDate.GetProperty("total").GetInt32() == 1 && notContentDate.GetProperty("total").GetInt32() == 0,
                "The inclusive content-date filter used stored occurred time as a source-content date.");
            var temporalUnknownResult = await HybridSearchJsonAsync(
                cliExecutable, fixture, "S15_T4_TEMPORAL_UNKNOWN", allowed.FolderId, modelDirectory,
                purpose: WorkspaceRecallPurposes.Temporal).ConfigureAwait(false);
            var temporalUnknownHit = temporalUnknownResult.GetProperty("hits")[0];
            var temporalUnknownAnnotation = FindS15T4Annotation(temporalUnknownHit, temporalUnknown.Id);
            var unknownLifecycle = temporalUnknownAnnotation.GetProperty("lifecycle");
            Require(temporalUnknownHit.GetProperty("recall").GetProperty("purposeEvidenceStatus").GetString() == "event_time_unknown" &&
                    unknownLifecycle.GetProperty("occurred").ValueKind == JsonValueKind.Null &&
                    unknownLifecycle.GetProperty("recorded").ValueKind == JsonValueKind.Null &&
                    temporalUnknownAnnotation.GetProperty("annotationRecordedAtUtc").ValueKind == JsonValueKind.String,
                "Missing event stamps were fabricated or confused with the known annotation record timestamp.");

            var pendingBefore = await ReadS15T4OwnerStateAsync(cliExecutable, fixture, pending.Id).ConfigureAwait(false);
            var intention = await HybridSearchJsonAsync(
                cliExecutable, fixture, "S15_T4_INTENTION_PENDING", allowed.FolderId, modelDirectory,
                purpose: WorkspaceRecallPurposes.Intention).ConfigureAwait(false);
            var pendingEvidence = FindS15T4Annotation(intention.GetProperty("hits")[0], pending.Id);
            var ackEvidence = FindS15T4Annotation(
                (await HybridSearchJsonAsync(
                    cliExecutable, fixture, "S15_T4_INTENTION_ACKNOWLEDGED", allowed.FolderId, modelDirectory,
                    purpose: WorkspaceRecallPurposes.Intention).ConfigureAwait(false)).GetProperty("hits")[0], acknowledged.Id);
            var doneEvidence = FindS15T4Annotation(
                (await HybridSearchJsonAsync(
                    cliExecutable, fixture, "S15_T4_INTENTION_DONE", allowed.FolderId, modelDirectory,
                    purpose: WorkspaceRecallPurposes.Intention).ConfigureAwait(false)).GetProperty("hits")[0], done.Id);
            Require(pendingEvidence.GetProperty("lifecycle").GetProperty("intention").GetString() == "Pending" &&
                    ackEvidence.GetProperty("lifecycle").GetProperty("intention").GetString() == "Pending" &&
                    ackEvidence.GetProperty("lifecycle").GetProperty("acknowledged").GetBoolean() &&
                    doneEvidence.GetProperty("lifecycle").GetProperty("intention").GetString() == "Done" &&
                    intention.GetProperty("recall").GetProperty("selectionReason").GetString()!.Contains("prospective", StringComparison.OrdinalIgnoreCase) &&
                    pendingEvidence.GetProperty("lifecycle").GetProperty("intentionDueAt").ValueKind == JsonValueKind.Null,
                "Mentioning, reminding or acknowledging an intention changed its stored pending/done state or invented a due time.");
            var pendingAfter = await ReadS15T4OwnerStateAsync(cliExecutable, fixture, pending.Id).ConfigureAwait(false);
            Require(pendingBefore.Annotation == pendingAfter.Annotation &&
                    pendingBefore.Lifecycle == pendingAfter.Lifecycle &&
                    pendingBefore.Recall == pendingAfter.Recall,
                "A recall read mutated the annotation, lifecycle or owner recall-policy revision/state.");

            var archivedDefault = await HybridSearchJsonAsync(
                cliExecutable, fixture, "S15_T4_INTENTION_ARCHIVED", allowed.FolderId, modelDirectory,
                purpose: WorkspaceRecallPurposes.Intention).ConfigureAwait(false);
            var archivedIncluded = await HybridSearchJsonAsync(
                cliExecutable, fixture, "S15_T4_INTENTION_ARCHIVED", allowed.FolderId, modelDirectory,
                purpose: WorkspaceRecallPurposes.Intention, includeArchived: true).ConfigureAwait(false);
            Require(archivedDefault.GetProperty("total").GetInt32() == 0 &&
                    archivedDefault.GetProperty("recall").GetProperty("includeArchived").GetBoolean() == false &&
                    FindS15T4Annotation(archivedIncluded.GetProperty("hits")[0], archived.Id)
                        .GetProperty("recallDisposition").GetString() == "Archived" &&
                    archivedIncluded.GetProperty("recall").GetProperty("includeArchived").GetBoolean(),
                "Purpose-aware recall did not follow the existing archived-by-default owner policy.");

            var actionResult = await HybridSearchJsonAsync(
                cliExecutable, fixture, "S15_T4_ACTION_EFFECT", allowed.FolderId, modelDirectory,
                purpose: WorkspaceRecallPurposes.ActionEffect).ConfigureAwait(false);
            var actionHit = actionResult.GetProperty("hits")[0];
            var actionEvidence = FindS15T4Annotation(actionHit, actionAnnotation.Id);
            Require(actionHit.GetProperty("recall").GetProperty("purposeEvidenceStatus").GetString() == "annotated_association" &&
                    actionHit.GetProperty("recall").GetProperty("selectionReasons").EnumerateArray()
                        .Any(reason => reason.GetString()!.Contains("does not establish causality", StringComparison.OrdinalIgnoreCase)) &&
                    actionEvidence.GetProperty("facets").EnumerateArray().Any(facet => facet.GetString() == "ActionEffect") &&
                    actionEvidence.GetProperty("evidence").GetString() == "ReportedSource",
                "Action/effect recall did not preserve the recorded association and explicit no-causality reason.");

            var autoAction = await HybridSearchJsonAsync(
                cliExecutable, fixture, "What was the effect when I planned the action and tried it?",
                allowed.FolderId, modelDirectory, purpose: WorkspaceRecallPurposes.Auto).ConfigureAwait(false);
            var autoIntention = await HybridSearchJsonAsync(
                cliExecutable, fixture, "When should I remind myself about this intention?",
                allowed.FolderId, modelDirectory, purpose: WorkspaceRecallPurposes.Auto).ConfigureAwait(false);
            var autoTemporal = await HybridSearchJsonAsync(
                cliExecutable, fixture, "When did I attempt this synthetic test?",
                allowed.FolderId, modelDirectory, purpose: WorkspaceRecallPurposes.Auto).ConfigureAwait(false);
            var autoExperience = await HybridSearchJsonAsync(
                cliExecutable, fixture, "What did I try in the synthetic experiment?",
                allowed.FolderId, modelDirectory, purpose: WorkspaceRecallPurposes.Auto).ConfigureAwait(false);
            Require(autoAction.GetProperty("recall").GetProperty("purpose").GetString() == WorkspaceRecallPurposes.ActionEffect &&
                    autoIntention.GetProperty("recall").GetProperty("purpose").GetString() == WorkspaceRecallPurposes.Intention &&
                    autoTemporal.GetProperty("recall").GetProperty("purpose").GetString() == WorkspaceRecallPurposes.Temporal &&
                    autoExperience.GetProperty("recall").GetProperty("purpose").GetString() == WorkspaceRecallPurposes.Experience,
                "Rule-first auto routing did not preserve action/effect, intention, temporal, experience precedence.");

            var ties = await HybridSearchJsonAsync(
                cliExecutable, fixture, "S15_T4_TIE_MARKER", allowed.FolderId, modelDirectory,
                purpose: WorkspaceRecallPurposes.General).ConfigureAwait(false);
            var tieHits = ties.GetProperty("hits").EnumerateArray().ToArray();
            Require(tieHits.Length == 2 && ties.GetProperty("literalTotal").GetInt32() == 2 &&
                    ties.GetProperty("semanticCandidateTotal").GetInt32() == 0 &&
                    tieHits[0].GetProperty("path").GetString() == "inside/tie-a.txt" &&
                    tieHits[1].GetProperty("path").GetString() == "inside/tie-b.txt",
                "Purpose-aware general routing changed T3 exact-literal or ordinal tie behavior.");

            var scopedNegative = await HybridSearchJsonAsync(
                cliExecutable, fixture, "S15_T4_OUTSIDE_PRIVATE", allowed.FolderId, modelDirectory,
                purpose: WorkspaceRecallPurposes.General).ConfigureAwait(false);
            var unscopedWitness = await HybridSearchJsonAsync(
                cliExecutable, fixture, "S15_T4_OUTSIDE_PRIVATE", null, modelDirectory,
                purpose: WorkspaceRecallPurposes.General).ConfigureAwait(false);
            Require(scopedNegative.GetProperty("total").GetInt32() == 0 &&
                    scopedNegative.GetProperty("recall").GetProperty("totalRegions").GetInt32() == 0 &&
                    unscopedWitness.GetProperty("hits").EnumerateArray().Any(hit =>
                        hit.GetProperty("path").GetString() == "outside/outside-private.txt"),
                "A folder-scoped hit, region count or representative disclosed the outside-folder decoy.");

            var textOutput = await RunOwnerCommandAsync(
                cliExecutable,
                HybridArguments(fixture, "S15_T4_ACTION_EFFECT", allowed.FolderId, modelDirectory,
                    format: "text", purpose: WorkspaceRecallPurposes.ActionEffect)).ConfigureAwait(false);
            Require(textOutput.ExitCode == 0 &&
                    textOutput.StandardOutput.Contains("RECALL purpose=action-effect", StringComparison.Ordinal) &&
                    textOutput.StandardOutput.Contains("REGION fileId=", StringComparison.Ordinal) &&
                    textOutput.StandardOutput.Contains("COGNITIVE_ANNOTATION", StringComparison.Ordinal),
                "The existing owner text CLI omitted purpose, region or stored cognitive evidence details.");
            var help = await RunOwnerCommandAsync(
                cliExecutable, "owner", "help", "--command", "workspace-search").ConfigureAwait(false);
            Require((help.StandardError + help.StandardOutput).Contains("--purpose", StringComparison.Ordinal) &&
                    (help.StandardError + help.StandardOutput).Contains("--include-archived", StringComparison.Ordinal),
                "The owner workspace-search help did not document the T4 purpose and archive options.");

            await VerifyS15T4SerializedSearchAndRecallAsync(
                cliExecutable, fixture, allowed.FolderId, modelDirectory, currentExperienceOwner.Id).ConfigureAwait(false);

            // Lock clears the explicit model selection; stale refusal remains deterministic.
            var staleProbeLock = await RunOwnerCommandAsync(
                cliExecutable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(staleProbeLock.ExitCode == 0 &&
                    staleProbeLock.StandardOutput.Contains("VAULT locked", StringComparison.Ordinal),
                "The vault did not lock before the stale-generation probe.");
            var staleProbeUnlock = await RunOwnerCommandWithInputAsync(
                cliExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(staleProbeUnlock.ExitCode == 0 &&
                    staleProbeUnlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The vault did not unlock before the stale-generation probe.");
            var staleFolder = await CreateFolderAsync(
                cliExecutable, fixture, allowed.FolderId, "stale-generation-probe",
                WorkspaceNumber(await ReadWorkspaceStatusAsync(cliExecutable, fixture).ConfigureAwait(false), "revision"))
                .ConfigureAwait(false);
            var staleResult = await RunOwnerCommandAsync(
                cliExecutable,
                HybridArguments(fixture, "Which procedure did the writer experiment with?", allowed.FolderId,
                    modelDirectory, purpose: WorkspaceRecallPurposes.Experience),
                standardInput: null,
                timeout: TimeSpan.FromMinutes(6)).ConfigureAwait(false);
            Require(staleResult.ExitCode == 3 &&
                    staleResult.StandardError.Contains("workspace_index_stale", StringComparison.Ordinal) &&
                    !staleResult.StandardOutput.Contains("HIT ", StringComparison.Ordinal),
                "A purpose-aware query served a stale passage generation or partially disclosed candidates.");
            var recoveryRebuild = await RunOwnerCommandAsync(
                cliExecutable,
                new[]
                {
                    "owner", "workspace-index-rebuild", "--control-pipe", fixture.ControlPipe,
                    "--model-directory", modelDirectory,
                },
                standardInput: null,
                timeout: TimeSpan.FromMinutes(6)).ConfigureAwait(false);
            Require(recoveryRebuild.ExitCode == 0, "The local passage generation did not recover after the stale-scope refusal.");
            _ = staleFolder;

            await VerifyS15T4LockAndCancellationAsync(
                cliExecutable, fixture, allowed.FolderId, modelDirectory, passphrase).ConfigureAwait(false);
            await VerifyS15T4InProcessRevisionConflictAsync(modelDirectory).ConfigureAwait(false);
            Console.WriteLine(
                "PASS S15-T4 purpose-aware owner recall: experience/temporal/intention/action-effect; exact revision regions; " +
                "unknown and archive policy; attribution; scoped disclosure; local semantic candidate class; " +
                "serialized CLI state change; in-process revision-conflict guard; stale/lock/cancel; T3 exact/currentness/tie contracts.");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static async Task<S15T4RecallAnnotation> CreateS15T4AnnotationAsync(
        string cliExecutable,
        SmokeFixture fixture,
        S15T4RecallSource source,
        string passage,
        string facet,
        string kind,
        string author,
        bool suggestion = false)
    {
        var characterOffset = source.Content.IndexOf(passage, StringComparison.Ordinal);
        Require(characterOffset >= 0, "The S15-T4 passage did not exist in its synthetic source file.");
        var byteOffset = Encoding.UTF8.GetByteCount(source.Content.AsSpan(0, characterOffset));
        var byteLength = Encoding.UTF8.GetByteCount(passage);
        var status = await ReadWorkspaceStatusAsync(cliExecutable, fixture).ConfigureAwait(false);
        var anchor = await RunOwnerCommandAsync(
            cliExecutable,
            "owner", "workspace-anchor-create", "--control-pipe", fixture.ControlPipe,
            "--file-id", source.FileId,
            "--expected-content-revision", source.Revision.ToString(CultureInfo.InvariantCulture),
            "--offset", byteOffset.ToString(CultureInfo.InvariantCulture),
            "--length", byteLength.ToString(CultureInfo.InvariantCulture),
            "--expected-tree-revision", WorkspaceNumber(status, "revision").ToString(CultureInfo.InvariantCulture))
            .ConfigureAwait(false);
        Require(anchor.ExitCode == 0 && anchor.StandardOutput.Contains("state=live", StringComparison.Ordinal),
            "The owner did not create an exact S15-T4 passage anchor (stderr=" + anchor.StandardError.Trim() + ").");
        var draftPath = await WriteWorkspaceInputAsync(
            fixture,
            "s15t4-annotation-" + source.Name + "-" + byteOffset.ToString(CultureInfo.InvariantCulture) + ".json",
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                anchorId = WorkspaceToken(anchor.StandardOutput, "id"),
                facets = new[] { facet == "action-effect" ? "actionEffect" : facet },
                kind,
                author,
                creationMethod = suggestion ? "paraphrase" : "directWrite",
                evidence = "reportedSource",
                level = "l1",
                suggestion
            })).ConfigureAwait(false);
        var created = await RunOwnerCommandAsync(
            cliExecutable,
            "owner", "cognitive-annotation-create", "--control-pipe", fixture.ControlPipe,
            "--file-id", source.FileId, "--input-file", draftPath).ConfigureAwait(false);
        Require(created.ExitCode == 0, "The owner could not create the S15-T4 cognitive annotation (stderr=" + created.StandardError.Trim() + ").");
        return new S15T4RecallAnnotation(WorkspaceToken(created.StandardOutput, "id"));
    }

    private static async Task CreateS15T4LifecycleAsync(
        string cliExecutable,
        SmokeFixture fixture,
        string annotationId,
        byte[]? draft,
        string suffix)
    {
        CommandResult created;
        if (draft is null)
        {
            created = await RunOwnerCommandAsync(
                cliExecutable, "owner", "lifecycle-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", annotationId, "--reason", "S15-T4 synthetic lifecycle state " + suffix)
                .ConfigureAwait(false);
        }
        else
        {
            var path = await WriteWorkspaceInputAsync(fixture, "s15t4-lifecycle-" + suffix + ".json", draft).ConfigureAwait(false);
            created = await RunOwnerCommandAsync(
                cliExecutable, "owner", "lifecycle-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", annotationId, "--input-file", path).ConfigureAwait(false);
        }

        Require(created.ExitCode == 0 && created.StandardOutput.Contains("LIFECYCLE stored", StringComparison.Ordinal),
            "The owner could not create the S15-T4 encrypted lifecycle record (stderr=" + created.StandardError.Trim() + ").");
    }

    private static object S15T4ExactStamp(string instantUtc) => new
    {
        precision = "exact",
        instantUtc,
        localYear = (int?)null,
        localMonth = (int?)null,
        localDay = (int?)null,
        localQuarter = (int?)null,
        timeZoneOffsetMinutes = 0
    };

    private static JsonElement FindS15T4Annotation(JsonElement hit, string annotationId)
    {
        if (hit.TryGetProperty("recall", out var recall) &&
            recall.TryGetProperty("annotations", out var annotations) &&
            annotations.ValueKind == JsonValueKind.Array)
        {
            foreach (var annotation in annotations.EnumerateArray())
            {
                if (annotation.GetProperty("id").GetString() == annotationId)
                {
                    return annotation;
                }
            }
        }

        throw new InvalidDataException("Purpose-aware recall omitted the expected exact-anchor annotation " + annotationId + ".");
    }

    private static async Task<(string Annotation, string Lifecycle, string Recall)> ReadS15T4OwnerStateAsync(
        string cliExecutable,
        SmokeFixture fixture,
        string annotationId)
    {
        var annotation = await RunOwnerCommandAsync(
            cliExecutable, "owner", "cognitive-annotation-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", annotationId).ConfigureAwait(false);
        var lifecycle = await RunOwnerCommandAsync(
            cliExecutable, "owner", "lifecycle-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", annotationId).ConfigureAwait(false);
        var recall = await RunOwnerCommandAsync(
            cliExecutable, "owner", "recall-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", annotationId).ConfigureAwait(false);
        Require(annotation.ExitCode == 0 && lifecycle.ExitCode == 0 && recall.ExitCode == 0,
            "The owner state snapshot could not be read around a purpose-aware recall query.");
        return (annotation.StandardOutput, lifecycle.StandardOutput, recall.StandardOutput);
    }

    private static async Task VerifyS15T4SerializedSearchAndRecallAsync(
        string cliExecutable,
        SmokeFixture fixture,
        string folderId,
        string modelDirectory,
        string annotationId)
    {
        const string query = "Which procedure did the writer experiment with, and what happened afterward?";
        var timedStart = Stopwatch.GetTimestamp();
        var baseline = await HybridSearchJsonAsync(
            cliExecutable, fixture, query, folderId, modelDirectory,
            purpose: WorkspaceRecallPurposes.Experience).ConfigureAwait(false);
        var durationMs = Stopwatch.GetElapsedTime(timedStart).TotalMilliseconds;
        Require(baseline.GetProperty("total").GetInt32() > 0,
            "The purpose-aware state-serialization probe query did not produce a real model-backed result.");

        var search = RunOwnerCommandAsync(
            cliExecutable,
            HybridArguments(fixture, query, folderId, modelDirectory,
                purpose: WorkspaceRecallPurposes.Experience),
            standardInput: null,
            timeout: TimeSpan.FromMinutes(6));
        var delayMs = Math.Max(10, Math.Min(100, (int)(durationMs / 4)));
        await Task.Delay(delayMs).ConfigureAwait(false);
        var change = await RunOwnerCommandAsync(
            cliExecutable, "owner", "recall-apply", "--control-pipe", fixture.ControlPipe,
            "--resource-id", annotationId, "--act", "demote",
            "--reason", "synthetic owner recall-state transition during S15-T4 serialization proof").ConfigureAwait(false);
        Require(change.ExitCode == 0 &&
                change.StandardOutput.Contains("state=demoted", StringComparison.Ordinal),
            "The owner recall transition did not complete during the S15-T4 serialization probe (exit=" +
            change.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stdout=" +
            change.StandardOutput.Trim() + "; stderr=" + change.StandardError.Trim() + ").");

        var result = await search.ConfigureAwait(false);
        if (result.ExitCode == 3 && result.StandardError.Contains("revision_conflict", StringComparison.Ordinal))
        {
            Console.WriteLine("PASS S15-T4 search refuses a changed annotation-state revision with revision_conflict");
            return;
        }

        Require(result.ExitCode == 0,
            "The concurrent owner search failed (exit=" + result.ExitCode.ToString(CultureInfo.InvariantCulture) +
            "; stdout=" + result.StandardOutput.Trim() + "; stderr=" + result.StandardError.Trim() + ").");
        using var page = JsonDocument.Parse(result.StandardOutput);
        var root = page.RootElement;
        Require(result.ExitCode == 0 &&
                root.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array &&
                root.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number &&
                total.GetInt32() > 0,
            "The concurrent owner search did not return a complete page while recall state changed (exit=" +
            result.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stdout=" +
            result.StandardOutput.Trim() + "; stderr=" + result.StandardError.Trim() + ").");
        Console.WriteLine(
            "PASS S15-T4 owner search and recall mutation both completed with a whole page and no partial disclosure " +
            "(delayMs=" + delayMs.ToString(CultureInfo.InvariantCulture) + ")");
    }

    private static async Task VerifyS15T4LockAndCancellationAsync(
        string cliExecutable,
        SmokeFixture fixture,
        string folderId,
        string modelDirectory,
        string passphrase)
    {
        const string query = "Which procedure did the writer experiment with, and what happened afterward?";
        var timer = Stopwatch.GetTimestamp();
        var measured = await HybridSearchJsonAsync(
            cliExecutable, fixture, query, folderId, modelDirectory,
            purpose: WorkspaceRecallPurposes.Experience).ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(timer).TotalMilliseconds;
        Require(measured.GetProperty("total").GetInt32() > 0,
            "The T4 lock/cancellation probe did not begin with a real owner recall result.");

        var lockedVault = await RunOwnerCommandAsync(
            cliExecutable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(lockedVault.ExitCode == 0, "The synthetic owner vault could not be locked for the T4 denial check.");
        var denied = await RunOwnerCommandAsync(
            cliExecutable,
            HybridArguments(fixture, query, folderId, modelDirectory, purpose: WorkspaceRecallPurposes.Experience),
            standardInput: null,
            timeout: TimeSpan.FromMinutes(6)).ConfigureAwait(false);
        Require(denied.ExitCode == 3 && denied.StandardError.Contains("vault_locked", StringComparison.Ordinal) &&
                !denied.StandardOutput.Contains("HIT ", StringComparison.Ordinal),
            "The locked purpose-aware owner channel disclosed recall results.");
        await UnlockWorkspaceAsync(cliExecutable, fixture, passphrase).ConfigureAwait(false);

        var inFlight = RunOwnerCommandAsync(
            cliExecutable,
            HybridArguments(fixture, query, folderId, modelDirectory, purpose: WorkspaceRecallPurposes.Experience),
            standardInput: null,
            timeout: TimeSpan.FromMinutes(6));
        var delayMs = Math.Max(50, Math.Min(300, (int)(elapsedMs / 3)));
        await Task.Delay(delayMs).ConfigureAwait(false);
        var concurrentLock = await RunOwnerCommandAsync(
            cliExecutable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        var cancelled = await inFlight.ConfigureAwait(false);
        Require(concurrentLock.ExitCode == 0 && cancelled.ExitCode != 0 &&
                cancelled.StandardError.Contains("vault_locked", StringComparison.Ordinal) &&
                !cancelled.StandardOutput.Contains("HYBRID_SEARCH", StringComparison.Ordinal) &&
                !cancelled.StandardOutput.Contains("HIT ", StringComparison.Ordinal),
            "An in-flight purpose-aware recall did not cancel without partial disclosure when the owner locked the vault.");
        await UnlockWorkspaceAsync(cliExecutable, fixture, passphrase).ConfigureAwait(false);
        var recovered = await HybridSearchJsonAsync(
            cliExecutable, fixture, query, folderId, modelDirectory,
            purpose: WorkspaceRecallPurposes.Experience).ConfigureAwait(false);
        Require(recovered.GetProperty("total").GetInt32() > 0,
            "Purpose-aware recall did not recover after unlock from the cancelled read.");
        Console.WriteLine(
            "PASS S15-T4 owner recall lock/cancel safety: lockedRefusal=true inFlightCancelled=true " +
            "noPartialDisclosure=true recovered=true delayMs=" + delayMs.ToString(CultureInfo.InvariantCulture));
    }

    private static async Task VerifyS15T4InProcessRevisionConflictAsync(string modelDirectory)
    {
        using var fixture = SmokeFixture.CreateUninitialized();
        using var vault = new VaultLifecycle(new EncryptedVaultStore(fixture.VaultDirectoryPath));
        var passphrase = CreateSyntheticPassphrase();
        Require(vault.Provision(passphrase, out _),
            "The in-process conflict fixture vault could not be provisioned.");

        var owner = AuthenticatedContext.ForOwner();
        var index = new EncryptedWorkspacePassageIndex(fixture.VaultDirectoryPath, vault);
        using var mutatingIndex = new S15T4ConflictIndex(index, owner);
        byte[] source = Encoding.UTF8.GetBytes(
            "S15_T4_COMPONENT_CONFLICT A synthetic owner reported trying a calibration step.\n");
        byte[]? draft = null;
        string annotationId;
        try
        {
            using (var operation = vault.TryBeginOperation() ??
                   throw new InvalidOperationException("The in-process conflict vault did not open an operation."))
            {
                var folder = operation.CreateWorkspaceFolder("root", "inside", expectedTreeRevision: 0);
                var file = operation.CreateWorkspaceFile(
                    folder.NodeId, "conflict-source.txt", source, folder.WorkspaceRevision);
                var anchor = operation.CreateWorkspaceAnchor(
                    file.NodeId, expectedContentRevision: 1, offset: 0, length: source.Length,
                    expectedTreeRevision: file.WorkspaceRevision);
                draft = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    anchorId = anchor.AnchorId,
                    facets = new[] { "experience" },
                    kind = "observation",
                    author = "user",
                    creationMethod = "directWrite",
                    evidence = "reportedSource",
                    level = "l1"
                });
                var annotation = new CognitiveAnnotationService().Create(owner, file.NodeId, draft, operation);
                annotationId = annotation.Id;
                mutatingIndex.SubjectResourceId = annotationId;

                var rebuilt = mutatingIndex.Rebuild(operation, modelDirectory, CancellationToken.None);
                Require(rebuilt.State == "ready" && !rebuilt.Stale && rebuilt.PassageCount > 0,
                    "The component conflict probe did not build a real encrypted passage generation.");
            }

            var handler = new ServiceRequestHandler(
                new GrantAuthority(new InMemoryGrantStore()),
                vault,
                new OwnerScopeGrantAuthority(),
                workspacePassageIndex: mutatingIndex);
            using var searchOperation = vault.TryBeginOperation() ??
                throw new InvalidOperationException("The in-process conflict vault did not reopen an operation.");
            var result = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerWorkspaceHybridSearchOperation,
                    Query: "Which synthetic procedure did the owner try?",
                    SearchOffset: 0,
                    SearchLimit: WorkspaceHybridSearchService.DefaultLimit,
                    SearchCurrent: WorkspaceHybridSearchCurrent.CurrentOnly,
                    Purpose: WorkspaceRecallPurposes.Experience,
                    WorkspaceModelDirectory: modelDirectory),
                searchOperation,
                CancellationToken.None).ConfigureAwait(false);
            Require(result.Error == "revision_conflict" && result.WorkspaceHybridSearch is null,
                "The real service handler did not refuse a recall-state transition during its exact-revision snapshot (error=" +
                (result.ErrorCode ?? result.Error ?? "none") + ").");
            Console.WriteLine(
                "PASS S15-T4 component conflict guard: a real encrypted recall-policy revision transition during semantic search " +
                "returns revision_conflict without a page (in-process seam; not external CLI concurrency)");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(source);
            if (draft is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(draft);
            }
        }
    }

    private sealed class S15T4ConflictIndex(
        IWorkspacePassageIndexEngine inner,
        AuthenticatedContext owner) : IWorkspacePassageIndexEngine
    {
        private bool _mutated;

        public string SubjectResourceId { private get; set; } = string.Empty;

        public WorkspacePassageIndexStatus ReadStatus(
            IWorkspacePersistence workspace,
            CancellationToken cancellationToken = default) =>
            inner.ReadStatus(workspace, cancellationToken);

        public WorkspacePassageIndexStatus Rebuild(
            IWorkspacePersistence workspace,
            string modelDirectory,
            CancellationToken cancellationToken = default) =>
            inner.Rebuild(workspace, modelDirectory, cancellationToken);

        public WorkspacePassageAnchorPage ReadAnchors(
            IWorkspacePersistence workspace,
            string fileId,
            long contentRevision,
            int offset,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.ReadAnchors(workspace, fileId, contentRevision, offset, limit, cancellationToken);

        public WorkspacePassageSemanticResult SearchSemantic(
            IWorkspacePersistence workspace,
            string modelDirectory,
            string query,
            IReadOnlyCollection<WorkspacePassageScope> scope,
            CancellationToken cancellationToken = default)
        {
            if (!_mutated)
            {
                if (workspace is not IVaultOperation operation || string.IsNullOrEmpty(SubjectResourceId))
                {
                    throw new InvalidOperationException("The component conflict seam did not receive its encrypted owner operation.");
                }

                _ = new RecallPolicyService().Apply(
                    owner,
                    SubjectResourceId,
                    RecallAct.Demote,
                    "synthetic in-operation state transition for S15-T4 component conflict proof",
                    operation);
                _mutated = true;
            }

            return inner.SearchSemantic(workspace, modelDirectory, query, scope, cancellationToken);
        }

        public void Reset() => inner.Reset();

        public void Dispose() => inner.Dispose();
    }
    private sealed record S15T4RecallSource(string FileId, string Name, string Content, long Revision);
    private sealed record S15T4RecallAnnotation(string Id);
}
