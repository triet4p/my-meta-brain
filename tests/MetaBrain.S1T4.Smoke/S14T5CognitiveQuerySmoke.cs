using System.Globalization;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string S14T5CorrectionReason = "owner corrected the synthetic passage classification";
    private const string S14T5RejectReason = "the synthetic suggestion does not match this passage";

    public static async Task RunS14T5CognitiveQueriesAsync(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S14-T5 cognitive CLI smoke is Windows-only.");
        }

        using var fixture = S14T5Fixture.Create();
        RunningService? live = null;
        try
        {
            live = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            var passphrase = CreateSyntheticPassphrase();
            var provision = await RunOwnerCommandWithInputAsync(
                executable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The owner CLI did not provision the synthetic S14-T5 vault.");

            var passages = new[]
            {
                new S14T5Passage("world", "question", "World: a synthetic report names the observed condition."),
                new S14T5Passage("experience", "lesson", "Experience: I attempted one bounded change."),
                new S14T5Passage("opinion", "decision", "Opinion: I selected the safer option."),
                new S14T5Passage("habit", "lesson", "Habit: I review the result before merging."),
                new S14T5Passage("intention", "intention", "Intention: I will recheck the result tomorrow."),
                new S14T5Passage("action-effect", "observation", "Action effect: I changed one setting and logs improved.")
            };
            var sourceText = string.Join("\n", passages.Select(passage => passage.Text)) + "\n";
            var sourcePath = Path.Combine(fixture.OutputDirectory, "s14-t5-source.txt");
            await File.WriteAllTextAsync(sourcePath, sourceText, new UTF8Encoding(false)).ConfigureAwait(false);

            var workspaceStatus = await S14T5OwnerAsync(executable, fixture.ControlPipe, "workspace-status").ConfigureAwait(false);
            Require(workspaceStatus.ExitCode == 0, "The workspace did not report its initial state.");
            var treeRevision = WorkspaceNumber(workspaceStatus.StandardOutput, "revision");
            var imported = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "workspace-import", "--parent-id", "root", "--name", "s14-t5-source.txt",
                "--input-file", sourcePath, "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(imported.ExitCode == 0 && imported.StandardOutput.Contains("IMPORT id=", StringComparison.Ordinal),
                "The owner could not save the source file before choosing any taxonomy.");
            var fileId = WorkspaceToken(imported.StandardOutput, "id");
            treeRevision = WorkspaceNumber(imported.StandardOutput, "revision");

            var anchors = new Dictionary<string, string>(StringComparer.Ordinal);
            var ownerAnnotations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var passage in passages)
            {
                var characterOffset = sourceText.IndexOf(passage.Text, StringComparison.Ordinal);
                Require(characterOffset >= 0, "A synthetic passage was not present in the source bytes.");
                var byteOffset = Encoding.UTF8.GetByteCount(sourceText.AsSpan(0, characterOffset));
                var byteLength = Encoding.UTF8.GetByteCount(passage.Text);
                var anchor = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                    "workspace-anchor-create", "--file-id", fileId,
                    "--expected-content-revision", "1",
                    "--offset", byteOffset.ToString(CultureInfo.InvariantCulture),
                    "--length", byteLength.ToString(CultureInfo.InvariantCulture),
                    "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                Require(anchor.ExitCode == 0 && anchor.StandardOutput.Contains("state=live", StringComparison.Ordinal),
                    "The owner CLI did not pin the exact synthetic passage anchor.");
                var anchorId = WorkspaceToken(anchor.StandardOutput, "id");
                anchors.Add(passage.Facet, anchorId);

                workspaceStatus = await S14T5OwnerAsync(executable, fixture.ControlPipe, "workspace-status").ConfigureAwait(false);
                Require(workspaceStatus.ExitCode == 0, "The workspace status failed after creating an anchor.");
                treeRevision = WorkspaceNumber(workspaceStatus.StandardOutput, "revision");

                // Draft JSON uses the canonical enum token; the query CLI also accepts kebab-case.
                var draftPath = await S14T5WriteJsonAsync(fixture, "owner-" + passage.Facet + ".json", new
                {
                    anchorId,
                    facets = new[] { passage.Facet == "action-effect" ? "actionEffect" : passage.Facet },
                    kind = passage.Kind,
                    author = "user",
                    creationMethod = "directWrite",
                    evidence = "reportedSource",
                    level = "l1"
                }).ConfigureAwait(false);
                var created = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                    "cognitive-annotation-create", "--file-id", fileId, "--input-file", draftPath).ConfigureAwait(false);
                Require(created.ExitCode == 0 && created.StandardOutput.Contains("origin=Owner", StringComparison.Ordinal) &&
                        created.StandardOutput.Contains("executor=Owner", StringComparison.Ordinal) &&
                        created.StandardOutput.Contains("review=Accepted", StringComparison.Ordinal) &&
                        created.StandardOutput.Contains("evidence=ReportedSource", StringComparison.Ordinal),
                    "An owner-authored classification did not stay confirmed owner state with its stated evidence (stdout=" + created.StandardOutput.Trim() + "; stderr=" + created.StandardError.Trim() + ").");
                ownerAnnotations.Add(passage.Facet, WorkspaceToken(created.StandardOutput, "id"));
            }

            // An owner can save a note on the same passage without assigning any taxonomy.
            var unclassifiedDraft = await S14T5WriteJsonAsync(fixture, "owner-unclassified.json", new
            {
                anchorId = anchors["world"]
            }).ConfigureAwait(false);
            var unclassified = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-create", "--file-id", fileId, "--input-file", unclassifiedDraft).ConfigureAwait(false);
            Require(unclassified.ExitCode == 0 && unclassified.StandardOutput.Contains("origin=Owner", StringComparison.Ordinal) &&
                    unclassified.StandardOutput.Contains("review=Accepted", StringComparison.Ordinal) &&
                    unclassified.StandardOutput.Contains("kind=Unknown", StringComparison.Ordinal) &&
                    unclassified.StandardOutput.Contains("FACETS none", StringComparison.Ordinal),
                "Saving an owner note incorrectly required a classification or model result.");
            var unclassifiedId = WorkspaceToken(unclassified.StandardOutput, "id");

            var tree = await S14T5OwnerAsync(executable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false);
            Require(tree.ExitCode == 0 && CountFileLines(tree.StandardOutput, fileId) == 1,
                "Multiple passage annotations duplicated their one canonical workspace file.");

            foreach (var passage in passages)
            {
                var query = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                    "cognitive-annotation-query", "--facet", passage.Facet).ConfigureAwait(false);
                Require(query.ExitCode == 0 && query.StandardOutput.Contains("id=" + ownerAnnotations[passage.Facet], StringComparison.Ordinal),
                    "The facet query did not find the owner classification for " + passage.Facet + ".");
            }

            var questions = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-query", "--kind", "question").ConfigureAwait(false);
            Require(questions.ExitCode == 0 && questions.StandardOutput.Contains("id=" + ownerAnnotations["world"], StringComparison.Ordinal),
                "The owner could not filter question annotations by their existing kind.");
            var decisions = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-query", "--kind", "decision").ConfigureAwait(false);
            Require(decisions.ExitCode == 0 && decisions.StandardOutput.Contains("id=" + ownerAnnotations["opinion"], StringComparison.Ordinal),
                "The owner could not filter decision annotations by their existing kind.");
            var lessons = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-query", "--kind", "lesson").ConfigureAwait(false);
            Require(lessons.ExitCode == 0 && lessons.StandardOutput.Contains("id=" + ownerAnnotations["experience"], StringComparison.Ordinal) &&
                    lessons.StandardOutput.Contains("id=" + ownerAnnotations["habit"], StringComparison.Ordinal),
                "The owner could not find both lesson annotations without converting them into facets.");
            var empty = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-query", "--facet", "habit", "--kind", "decision").ConfigureAwait(false);
            Require(empty.ExitCode == 0 && empty.StandardOutput.Contains("ANNOTATION QUERY no_match", StringComparison.Ordinal),
                "A query with no matching classifications did not report an explicit empty result.");

            var agentList = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.OwnerCognitiveAnnotationListOperation
            }).ConfigureAwait(false);
            Require(ReplyCode(agentList) == "forbidden",
                "The agent pipe exposed an owner-only annotation list operation used by the CLI query.");

            var pendingExperienceDraft = await S14T5WriteJsonAsync(fixture, "pending-experience.json", new
            {
                anchorId = anchors["experience"],
                facets = new[] { "experience" },
                kind = "lesson",
                author = "assistant",
                creationMethod = "paraphrase",
                evidence = "reportedSource",
                level = "l1",
                suggestion = true
            }).ConfigureAwait(false);
            var pendingExperience = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-create", "--file-id", fileId, "--input-file", pendingExperienceDraft).ConfigureAwait(false);
            Require(pendingExperience.ExitCode == 0 && pendingExperience.StandardOutput.Contains("origin=AiSuggestion", StringComparison.Ordinal) &&
                    pendingExperience.StandardOutput.Contains("review=Pending", StringComparison.Ordinal),
                "The synthetic pending suggestion lost its distinct AI-origin review state.");
            var pendingExperienceId = WorkspaceToken(pendingExperience.StandardOutput, "id");

            var pendingWorldDraft = await S14T5WriteJsonAsync(fixture, "pending-world.json", new
            {
                anchorId = anchors["world"],
                facets = new[] { "world" },
                kind = "question",
                author = "assistant",
                creationMethod = "paraphrase",
                evidence = "reportedSource",
                level = "l1",
                suggestion = true
            }).ConfigureAwait(false);
            var pendingWorld = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-create", "--file-id", fileId, "--input-file", pendingWorldDraft).ConfigureAwait(false);
            Require(pendingWorld.ExitCode == 0 && pendingWorld.StandardOutput.Contains("origin=AiSuggestion", StringComparison.Ordinal) &&
                    pendingWorld.StandardOutput.Contains("review=Pending", StringComparison.Ordinal),
                "The second synthetic suggestion did not start pending review.");
            var pendingWorldId = WorkspaceToken(pendingWorld.StandardOutput, "id");
            var allAnnotations = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-query").ConfigureAwait(false);
            var expectedAnnotationIds = ownerAnnotations.Values
                .Append(unclassifiedId)
                .Append(pendingExperienceId)
                .Append(pendingWorldId)
                .ToArray();
            Require(allAnnotations.ExitCode == 0 &&
                    allAnnotations.StandardOutput.Contains("ANNOTATION QUERY count=" + expectedAnnotationIds.Length.ToString(CultureInfo.InvariantCulture) + " ", StringComparison.Ordinal) &&
                    expectedAnnotationIds.All(id => allAnnotations.StandardOutput.Contains("id=" + id + " ", StringComparison.Ordinal)),
                "The unfiltered query silently omitted canonical annotations.");

            var reviewQueue = await S14T5OwnerAsync(executable, fixture.ControlPipe, "review-list").ConfigureAwait(false);
            Require(reviewQueue.ExitCode == 0 && reviewQueue.StandardOutput.Contains("annotationSuggestions=2", StringComparison.Ordinal) &&
                    reviewQueue.StandardOutput.Contains("ANNOTATION REVIEW id=" + pendingExperienceId, StringComparison.Ordinal) &&
                    reviewQueue.StandardOutput.Contains("ANNOTATION REVIEW id=" + pendingWorldId, StringComparison.Ordinal) &&
                    reviewQueue.StandardOutput.Contains("availability=Available", StringComparison.Ordinal),
                "The review queue did not list both pending annotations with source availability.");

            var selected = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-query", "--facet", "experience", "--kind", "lesson",
                "--review-status", "pending", "--origin", "ai-suggestion").ConfigureAwait(false);
            Require(selected.ExitCode == 0 && selected.StandardOutput.Contains("id=" + pendingExperienceId, StringComparison.Ordinal) &&
                    selected.StandardOutput.Contains("author=Assistant", StringComparison.Ordinal) &&
                    selected.StandardOutput.Contains("executor=Owner", StringComparison.Ordinal) &&
                    selected.StandardOutput.Contains("evidence=ReportedSource", StringComparison.Ordinal) &&
                    selected.StandardOutput.Contains("availability=Available", StringComparison.Ordinal) &&
                    selected.StandardOutput.Contains("contentRevision=1", StringComparison.Ordinal),
                "The combined review query hid the pending suggestion's authorship, executor, evidence or anchor state.");

            var annotationRead = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", pendingExperienceId).ConfigureAwait(false);
            Require(annotationRead.ExitCode == 0 && annotationRead.StandardOutput.Contains("origin=AiSuggestion", StringComparison.Ordinal) &&
                    annotationRead.StandardOutput.Contains("review=Pending", StringComparison.Ordinal) &&
                    annotationRead.StandardOutput.Contains("file=" + fileId, StringComparison.Ordinal) &&
                    annotationRead.StandardOutput.Contains("contentRevision=1", StringComparison.Ordinal) &&
                    annotationRead.StandardOutput.Contains("availability=Available", StringComparison.Ordinal),
                "The selected annotation read did not identify its exact source file and revision.");
            var sourceRead = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "workspace-read", "--file-id", fileId, "--revision", "1").ConfigureAwait(false);
            Require(sourceRead.ExitCode == 0 && sourceRead.StandardOutput == sourceText,
                "The owner could not inspect the exact anchored source revision through the CLI.");

            var correctionPath = await S14T5WriteJsonAsync(fixture, "correction.json", new
            {
                facets = new[] { "opinion" },
                kind = "decision",
                reason = S14T5CorrectionReason
            }).ConfigureAwait(false);
            var corrected = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-correct", "--resource-id", pendingExperienceId,
                "--expected-revision", "1", "--input-file", correctionPath).ConfigureAwait(false);
            Require(corrected.ExitCode == 0 && corrected.StandardOutput.Contains("revision=2", StringComparison.Ordinal) &&
                    corrected.StandardOutput.Contains("FACETS Opinion", StringComparison.Ordinal) &&
                    corrected.StandardOutput.Contains("kind=Decision", StringComparison.Ordinal) &&
                    corrected.StandardOutput.Contains("origin=AiSuggestion", StringComparison.Ordinal) &&
                    corrected.StandardOutput.Contains("review=Pending", StringComparison.Ordinal) &&
                    corrected.StandardOutput.Contains("author=Assistant", StringComparison.Ordinal) &&
                    corrected.StandardOutput.Contains("executor=Owner", StringComparison.Ordinal) &&
                    corrected.StandardOutput.Contains("evidence=ReportedSource", StringComparison.Ordinal),
                "The CLI correction did not preserve suggestion origin, attribution, evidence and pending review.");
            var staleCorrection = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-correct", "--resource-id", pendingExperienceId,
                "--expected-revision", "1", "--input-file", correctionPath).ConfigureAwait(false);
            Require(staleCorrection.ExitCode != 0 && staleCorrection.StandardError.Contains("revision_conflict", StringComparison.Ordinal),
                "A stale annotation correction did not fail closed.");
            var afterConflict = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", pendingExperienceId).ConfigureAwait(false);
            Require(afterConflict.ExitCode == 0 && afterConflict.StandardOutput.Contains("revision=2", StringComparison.Ordinal) &&
                    afterConflict.StandardOutput.Contains("review=Pending", StringComparison.Ordinal),
                "The stale correction changed the live pending annotation.");

            var accepted = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-accept", "--resource-id", pendingExperienceId,
                "--expected-revision", "2", "--reason", "owner accepted the corrected classification").ConfigureAwait(false);
            Require(accepted.ExitCode == 0 && accepted.StandardOutput.Contains("revision=3", StringComparison.Ordinal) &&
                    accepted.StandardOutput.Contains("origin=AiSuggestion", StringComparison.Ordinal) &&
                    accepted.StandardOutput.Contains("review=Accepted", StringComparison.Ordinal) &&
                    accepted.StandardOutput.Contains("author=Assistant", StringComparison.Ordinal) &&
                    accepted.StandardOutput.Contains("executor=Owner", StringComparison.Ordinal) &&
                    accepted.StandardOutput.Contains("evidence=ReportedSource", StringComparison.Ordinal),
                "Accepting the suggestion erased its AI origin or attribution/evidence distinctions.");
            var rejected = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-reject", "--resource-id", pendingWorldId,
                "--expected-revision", "1", "--reason", S14T5RejectReason).ConfigureAwait(false);
            Require(rejected.ExitCode == 0 && rejected.StandardOutput.Contains("review=Rejected", StringComparison.Ordinal) &&
                    rejected.StandardOutput.Contains("origin=AiSuggestion", StringComparison.Ordinal),
                "The CLI did not record the explicit rejection while retaining AI origin.");

            var reviewedOpinion = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-query", "--facet", "opinion", "--kind", "decision",
                "--review-status", "accepted").ConfigureAwait(false);
            Require(reviewedOpinion.ExitCode == 0 && reviewedOpinion.StandardOutput.Contains("id=" + pendingExperienceId, StringComparison.Ordinal) &&
                    reviewedOpinion.StandardOutput.Contains("id=" + ownerAnnotations["opinion"], StringComparison.Ordinal),
                "The owner could not query both the accepted suggestion and owner-authored decision.");
            reviewQueue = await S14T5OwnerAsync(executable, fixture.ControlPipe, "review-list").ConfigureAwait(false);
            Require(reviewQueue.ExitCode == 0 && reviewQueue.StandardOutput.Contains("annotationSuggestions=0", StringComparison.Ordinal),
                "Reviewed suggestions remained in the pending annotation queue.");

            var missingAnnotation = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", "ann-s14t5-missing").ConfigureAwait(false);
            Require(missingAnnotation.ExitCode != 0 && missingAnnotation.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "Reading a missing annotation did not fail explicitly.");

            var revisedSourcePath = Path.Combine(fixture.OutputDirectory, "s14-t5-source-revised.txt");
            var revisedSourceText = sourceText + "A later source revision preserves the original anchor.\n";
            await File.WriteAllTextAsync(revisedSourcePath, revisedSourceText, new UTF8Encoding(false)).ConfigureAwait(false);
            var revisedSource = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "workspace-revise", "--file-id", fileId, "--expected-content-revision", "1",
                "--input-file", revisedSourcePath).ConfigureAwait(false);
            Require(revisedSource.ExitCode == 0 && revisedSource.StandardOutput.Contains("contentRevision=2", StringComparison.Ordinal),
                "The synthetic source could not be advanced to test stale-anchor reporting.");
            var staleQuery = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-query", "--facet", "opinion", "--source-availability", "available").ConfigureAwait(false);
            Require(staleQuery.ExitCode == 0 && staleQuery.StandardOutput.Contains("id=" + pendingExperienceId, StringComparison.Ordinal) &&
                    staleQuery.StandardOutput.Contains("contentRevision=1", StringComparison.Ordinal) &&
                    staleQuery.StandardOutput.Contains("state=stale availability=Available", StringComparison.Ordinal),
                "A revised source was rebound instead of reporting the original stale anchor and available file.");

            var statusAfterRevision = await S14T5OwnerAsync(executable, fixture.ControlPipe, "workspace-status").ConfigureAwait(false);
            var missingSourcePath = Path.Combine(fixture.OutputDirectory, "s14-t5-source-to-delete.txt");
            const string missingSourceText = "Unavailable synthetic source passage.";
            await File.WriteAllTextAsync(missingSourcePath, missingSourceText + "\n", new UTF8Encoding(false)).ConfigureAwait(false);
            var missingImport = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "workspace-import", "--parent-id", "root", "--name", "s14-t5-source-to-delete.txt",
                "--input-file", missingSourcePath,
                "--expected-tree-revision", WorkspaceNumber(statusAfterRevision.StandardOutput, "revision").ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(missingImport.ExitCode == 0, "The owner could not save the second synthetic source file.");
            var missingFileId = WorkspaceToken(missingImport.StandardOutput, "id");
            var missingStatus = await S14T5OwnerAsync(executable, fixture.ControlPipe, "workspace-status").ConfigureAwait(false);
            var missingAnchor = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "workspace-anchor-create", "--file-id", missingFileId,
                "--expected-content-revision", "1", "--offset", "0",
                "--length", Encoding.UTF8.GetByteCount(missingSourceText).ToString(CultureInfo.InvariantCulture),
                "--expected-tree-revision", WorkspaceNumber(missingStatus.StandardOutput, "revision").ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(missingAnchor.ExitCode == 0, "The owner could not pin the synthetic missing-source passage.");
            var missingAnchorId = WorkspaceToken(missingAnchor.StandardOutput, "id");
            var missingDraft = await S14T5WriteJsonAsync(fixture, "missing-source-annotation.json", new
            {
                anchorId = missingAnchorId,
                facets = new[] { "world" },
                kind = "claim",
                author = "user",
                creationMethod = "directWrite",
                evidence = "reportedSource",
                level = "l0"
            }).ConfigureAwait(false);
            var missingSourceAnnotation = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-create", "--file-id", missingFileId, "--input-file", missingDraft).ConfigureAwait(false);
            Require(missingSourceAnnotation.ExitCode == 0, "The missing-source annotation was not saved.");
            var missingSourceAnnotationId = WorkspaceToken(missingSourceAnnotation.StandardOutput, "id");

            var deletePreview = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "workspace-delete-preview", "--node-id", missingFileId).ConfigureAwait(false);
            Require(deletePreview.ExitCode == 0, "The owner could not preview removal of the synthetic source.");
            var deleted = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "workspace-delete", "--node-id", missingFileId,
                "--expected-tree-revision", WorkspaceNumber(deletePreview.StandardOutput, "revision").ToString(CultureInfo.InvariantCulture),
                "--expected-node-revision", WorkspaceNumber(deletePreview.StandardOutput, "nodeRevision").ToString(CultureInfo.InvariantCulture),
                "--preview", WorkspaceToken(deletePreview.StandardOutput, "preview")).ConfigureAwait(false);
            Require(deleted.ExitCode == 0, "The exact previewed synthetic source could not be removed.");
            var unavailableQuery = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-query", "--source-availability", "unavailable").ConfigureAwait(false);
            Require(unavailableQuery.ExitCode == 0 && unavailableQuery.StandardOutput.Contains("id=" + missingSourceAnnotationId, StringComparison.Ordinal) &&
                    unavailableQuery.StandardOutput.Contains("availability=Unavailable", StringComparison.Ordinal),
                "The query hid the explicit unavailable state of a deleted source.");
            tree = await S14T5OwnerAsync(executable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false);
            Require(tree.ExitCode == 0 && CountFileLines(tree.StandardOutput, fileId) == 1 &&
                    !tree.StandardOutput.Contains("FILE id=" + missingFileId, StringComparison.Ordinal),
                "Multiple annotations or source removal changed canonical file identity unexpectedly.");

            var lockedVault = await S14T5OwnerAsync(executable, fixture.ControlPipe, "lock").ConfigureAwait(false);
            Require(lockedVault.ExitCode == 0, "The synthetic vault did not lock before the query negative.");
            var lockedQuery = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-query", "--facet", "opinion").ConfigureAwait(false);
            Require(lockedQuery.ExitCode != 0 && lockedQuery.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "A locked vault served the owner cognitive query.");

            await StopServiceAsync(live).ConfigureAwait(false);
            live = null;
            live = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            var lockedStatus = await S14T5OwnerAsync(executable, fixture.ControlPipe, "status").ConfigureAwait(false);
            Require(lockedStatus.ExitCode == 0 && lockedStatus.StandardOutput.Contains("vault=locked", StringComparison.Ordinal),
                "The restarted service did not start locked.");
            var unlock = await RunOwnerCommandWithInputAsync(executable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0 && unlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The owner could not unlock the restarted synthetic vault.");
            var rebuilt = await S14T5OwnerAsync(executable, fixture.ControlPipe, "index-rebuild").ConfigureAwait(false);
            Require(rebuilt.ExitCode == 0 && rebuilt.StandardOutput.Contains("INDEX rebuilt", StringComparison.Ordinal),
                "The owner could not rebuild the private index after restart.");

            var afterRestart = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-query", "--facet", "opinion", "--kind", "decision",
                "--review-status", "accepted", "--origin", "ai-suggestion",
                "--source-availability", "available").ConfigureAwait(false);
            Require(afterRestart.ExitCode == 0 && afterRestart.StandardOutput.Contains("id=" + pendingExperienceId, StringComparison.Ordinal) &&
                    afterRestart.StandardOutput.Contains("revision=3", StringComparison.Ordinal) &&
                    afterRestart.StandardOutput.Contains("state=stale availability=Available", StringComparison.Ordinal),
                "The owner correction, acceptance and exact stale source anchor did not survive restart/rebuild.");
            sourceRead = await S14T5OwnerAsync(executable, fixture.ControlPipe,
                "workspace-read", "--file-id", fileId, "--revision", "1").ConfigureAwait(false);
            Require(sourceRead.ExitCode == 0 && sourceRead.StandardOutput == sourceText,
                "The original source revision could not be re-read after restart/rebuild.");
            reviewQueue = await S14T5OwnerAsync(executable, fixture.ControlPipe, "review-list").ConfigureAwait(false);
            Require(reviewQueue.ExitCode == 0 && reviewQueue.StandardOutput.Contains("annotationSuggestions=0", StringComparison.Ordinal),
                "The restarted review queue resurrected an accepted or rejected suggestion.");

            Console.WriteLine("PASS S14-T5 CLI facet/kind queries, exact source inspection, revision-gated correction and review, source availability, lock/restart/rebuild, and owner-only boundary");
        }
        finally
        {
            if (live is not null)
            {
                await StopServiceAsync(live).ConfigureAwait(false);
            }
        }
    }

    private static async Task<CommandResult> S14T5OwnerAsync(
        string executable,
        string controlPipe,
        string command,
        params string[] arguments)
    {
        var full = new List<string>(arguments.Length + 4)
        {
            "owner",
            command,
            "--control-pipe",
            controlPipe
        };
        full.AddRange(arguments);
        return await RunOwnerCommandAsync(executable, full.ToArray()).ConfigureAwait(false);
    }

    private static async Task<string> S14T5WriteJsonAsync(S14T5Fixture fixture, string fileName, object value)
    {
        var path = Path.Combine(fixture.OutputDirectory, fileName);
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(value)).ConfigureAwait(false);
        return path;
    }

    private sealed record S14T5Passage(string Facet, string Kind, string Text);
}

internal sealed class S14T5Fixture : IDisposable
{
    private bool _disposed;

    private S14T5Fixture(string root, string liveRoot, string settingsPath, string controlPipe)
    {
        Root = root;
        LiveRoot = liveRoot;
        SettingsPath = settingsPath;
        ControlPipe = controlPipe;
        AgentPipe = controlPipe + ".agent";
        OutputDirectory = Path.Combine(root, "output");
    }

    public string Root { get; }
    public string LiveRoot { get; }
    public string SettingsPath { get; }
    public string ControlPipe { get; }
    public string AgentPipe { get; }
    public string OutputDirectory { get; }

    public static S14T5Fixture Create()
    {
        var repositoryRoot = SmokeFixture.FindRepositoryRoot();
        var root = Path.Combine(repositoryRoot, "runtime", "s14-t5", "run-" + Guid.NewGuid().ToString("N"));
        var liveRoot = Path.Combine(root, "live");
        var outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(liveRoot);
        Directory.CreateDirectory(outputDirectory);
        var controlPipe = "MetaBrainS14T5-" + Guid.NewGuid().ToString("N");
        var settingsPath = S12T5Fixture.WriteSettingsForTest(liveRoot, controlPipe);
        return new S14T5Fixture(root, liveRoot, settingsPath, controlPipe);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            var repositoryRoot = SmokeFixture.FindRepositoryRoot();
            var ownedPrefix = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(repositoryRoot, "runtime", "s14-t5"))) + Path.DirectorySeparatorChar;
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
            if (!fullRoot.StartsWith(ownedPrefix, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullRoot).StartsWith("run-", StringComparison.Ordinal))
            {
                return;
            }

            if (Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
