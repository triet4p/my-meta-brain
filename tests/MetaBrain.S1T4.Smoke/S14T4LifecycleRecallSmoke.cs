using System.Globalization;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Core.Domain;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S14-T4 actual CLI/service smoke: temporal validity/review-after, intention
/// due/context/acknowledged/done and recall pin/demote/archive/restore over
/// both the existing memory-item path and a durable passage annotation, plus
/// owner-only refusals that must keep an annotation out of agent scope.
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S14T4Zone = "s14t4-synthetic-zone";
    private const string S14T4ItemId = "s14t4-synthetic-intention-item";
    private const string S14T4ItemTitle = "S14T4_SYNTHETIC_ITEM_TITLE_7f21";
    private const string S14T4ItemBody = "S14T4_SYNTHETIC_ITEM_BODY_3c9a";
    private const string S14T4SourceText = "S14T4_SYNTHETIC_SOURCE_PASSAGE_2b84 and trailing words\n";

    public static async Task RunS14T4LifecycleRecallAsync(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S14-T4 lifecycle/recall smoke is Windows-only.");
        }

        Console.WriteLine("RUN S14-T4 temporal, intention and recall policy over memory items and durable passage annotations");
        using var fixture = S14T4Fixture.Create();
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
                "The owner CLI did not provision the synthetic S14-T4 vault.");

            // One live workspace file and one exact pinned anchor are the source
            // every passage annotation in this scenario references.
            var sourcePath = Path.Combine(fixture.OutputDirectory, "s14-t4-source.txt");
            await File.WriteAllTextAsync(sourcePath, S14T4SourceText, new UTF8Encoding(false)).ConfigureAwait(false);
            var status = await S14T4OwnerAsync(executable, fixture.ControlPipe, "workspace-status").ConfigureAwait(false);
            Require(status.ExitCode == 0, "The workspace status operation failed before the S14-T4 scenario.");
            var treeRevision = WorkspaceNumber(status.StandardOutput, "revision");
            var imported = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "workspace-import", "--parent-id", "root", "--name", "s14-t4-source.txt",
                "--input-file", sourcePath,
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(imported.ExitCode == 0 && imported.StandardOutput.Contains("IMPORT id=", StringComparison.Ordinal),
                "The actual owner CLI did not import the synthetic workspace file.");
            var fileId = WorkspaceToken(imported.StandardOutput, "id");
            treeRevision = WorkspaceNumber(imported.StandardOutput, "revision");
            var anchor = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "workspace-anchor-create", "--file-id", fileId,
                "--expected-content-revision", "1",
                "--offset", "0",
                "--length", S14T4SourceText.TrimEnd('\n').Length.ToString(CultureInfo.InvariantCulture),
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(anchor.ExitCode == 0 && anchor.StandardOutput.Contains("state=live", StringComparison.Ordinal),
                "The service did not pin the selected source passage to a live workspace anchor.");
            var anchorId = WorkspaceToken(anchor.StandardOutput, "id");

            // The existing file-level path: an ordinary memory-item intention.
            var created = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "memory-create", "--resource-id", S14T4ItemId, "--zone-id", S14T4Zone,
                "--body", S14T4ItemBody, "--kind", "intention", "--author", "user",
                "--method", "directWrite", "--evidence", "unverified", "--level", "l1",
                "--title", S14T4ItemTitle).ConfigureAwait(false);
            Require(created.ExitCode == 0, "The owner CLI did not create the synthetic memory-item intention (stderr=" +
                created.StandardError.Trim() + ").");
            var itemLifecycle = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-create", "--resource-id", S14T4ItemId,
                "--reason", "owner tracks the synthetic memory-item intention").ConfigureAwait(false);
            Require(itemLifecycle.ExitCode == 0 &&
                    S14T4Line(itemLifecycle.StandardOutput, "publication=").Contains("publication=proposal", StringComparison.Ordinal) &&
                    S14T4Line(itemLifecycle.StandardOutput, "publication=").Contains("intention=pending", StringComparison.Ordinal),
                "The existing memory-item lifecycle did not start as a pending proposal intention (exit=" +
                itemLifecycle.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stdout=" +
                itemLifecycle.StandardOutput.Trim() + "; stderr=" + itemLifecycle.StandardError.Trim() + ").");

            // The passage path: three annotations, two of them intention subjects.
            var annotationA = await S14T4CreateIntentionAnnotationAsync(executable, fixture, fileId, anchorId, "a").ConfigureAwait(false);
            var annotationB = await S14T4CreateIntentionAnnotationAsync(executable, fixture, fileId, anchorId, "b").ConfigureAwait(false);
            var annotationC = await S14T4CreateIntentionAnnotationAsync(executable, fixture, fileId, anchorId, "c").ConfigureAwait(false);
            var annotationUnclassified = await S14T4CreateAnnotationAsync(executable, fixture, fileId, anchorId, "unclassified",
                includeKind: false).ConfigureAwait(false);

            var annotationLifecycle = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-create", "--resource-id", annotationA,
                "--reason", "owner tracks the synthetic passage intention").ConfigureAwait(false);
            Require(annotationLifecycle.ExitCode == 0, "The annotation lifecycle record was not created.");
            Require(S14T4Line(annotationLifecycle.StandardOutput, "LIFECYCLE ").Contains("subjectKind=intention", StringComparison.Ordinal),
                "The annotation lifecycle did not resolve the annotation's own cognitive kind.");
            Require(S14T4Field(annotationLifecycle.StandardOutput, "publication") == "unknown",
                "The annotation lifecycle claimed a catalog publication state instead of staying unknown.");
            Require(S14T4Field(annotationLifecycle.StandardOutput, "intention") == "pending",
                "The annotation lifecycle did not carry the kind-derived pending intention state.");

            var annotationBLifecycle = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-create", "--resource-id", annotationB,
                "--reason", "owner tracks the second synthetic passage intention").ConfigureAwait(false);
            Require(annotationBLifecycle.ExitCode == 0 &&
                    S14T4Field(annotationBLifecycle.StandardOutput, "intention") == "pending",
                "The second passage intention lifecycle record was not created as pending.");

            var unclassifiedLifecycle = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-create", "--resource-id", annotationUnclassified,
                "--reason", "owner attempts an unclassified passage lifecycle").ConfigureAwait(false);
            Require(unclassifiedLifecycle.ExitCode != 0,
                "An unclassified passage annotation was accepted as a lifecycle subject.");

            // The unclassified annotation stays unclassified.
            var unclassifiedRead = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", annotationUnclassified).ConfigureAwait(false);
            Require(unclassifiedRead.ExitCode == 0 && unclassifiedRead.StandardOutput.Contains("kind=Unknown", StringComparison.Ordinal),
                "The refused unclassified passage was silently classified by the lifecycle refusal.");

            // One-time due triggers on both the memory item and the passage.
            var dueAt = DateTimeOffset.UtcNow.AddMinutes(-2)
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            var itemTrigger = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "intention-trigger-set", "--resource-id", S14T4ItemId, "--due-at", dueAt).ConfigureAwait(false);
            Require(itemTrigger.ExitCode == 0 && itemTrigger.StandardOutput.Contains("intention=pending", StringComparison.Ordinal),
                "The memory-item intention time trigger was not recorded as a pending intention.");
            var passageTrigger = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "intention-trigger-set", "--resource-id", annotationA, "--due-at", dueAt).ConfigureAwait(false);
            Require(passageTrigger.ExitCode == 0 && passageTrigger.StandardOutput.Contains("intention=pending", StringComparison.Ordinal),
                "The passage intention time trigger was not recorded as a pending intention.");

            // The service's own 5s time-trigger poll claims due subjects without any
            // owner CLI call. Wait for that pass, then prove the owner-initiated
            // passes record nothing new and the encrypted record keeps one act and
            // one notification instant for each subject.
            var backgroundNotified = await S14T4WaitForBackgroundNotificationsAsync(executable, fixture.ControlPipe,
                new[] { S14T4ItemId, annotationA }, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Require(backgroundNotified,
                "The service's own time-trigger pass did not mark both due subjects notified within the bounded wait.");
            var firstPass = await S14T4OwnerAsync(executable, fixture.ControlPipe, "intention-schedule-run").ConfigureAwait(false);
            Require(firstPass.ExitCode == 0 && S14T4Field(firstPass.StandardOutput, "notifications") == "0",
                "An owner schedule pass re-recorded a notification the service pass had already recorded (stderr=" +
                firstPass.StandardError.Trim() + ").");
            var secondPass = await S14T4OwnerAsync(executable, fixture.ControlPipe, "intention-schedule-run").ConfigureAwait(false);
            Require(secondPass.ExitCode == 0 && S14T4Field(secondPass.StandardOutput, "notifications") == "0",
                "A repeated unlocked schedule pass duplicated an already-recorded due notification.");

            var schedule = await S14T4OwnerAsync(executable, fixture.ControlPipe, "intentions").ConfigureAwait(false);
            Require(schedule.ExitCode == 0 && S14T4Field(schedule.StandardOutput, "count") == "2",
                "The owner schedule did not list the memory-item and passage intentions.");
            var passageBlock = S14T4ScheduleBlock(schedule.StandardOutput, annotationA);
            Require(S14T4Field(passageBlock, "notified") == "true" && S14T4Field(passageBlock, "acknowledged") == "false" &&
                    S14T4Field(passageBlock, "state") == "pending" && S14T4Field(passageBlock, "isDue") == "true",
                "The passage row did not report a due, notified, unacknowledged, pending intention.");
            var passageNotifiedAt = S14T4Field(passageBlock, "notifiedUtc");
            Require(passageNotifiedAt != "none",
                "The due passage recorded no notification instant.");
            Require(passageBlock.Contains("\ntitle=none\n", StringComparison.Ordinal) &&
                    passageBlock.Contains("\npreview=\n", StringComparison.Ordinal),
                "The passage schedule row copied private annotation or source text into the owner timeline (block=" +
                passageBlock.Replace("\n", "|", StringComparison.Ordinal) + ").");
            var itemRow = S14T4Line(schedule.StandardOutput, "INTENTION id=" + S14T4ItemId + " ");
            Require(S14T4Field(itemRow, "notified") == "true" && S14T4Field(itemRow, "acknowledged") == "false" &&
                    S14T4Field(itemRow, "isDue") == "true",
                "The existing memory-item row stopped reporting its due and notified markers correctly.");
            Require(S14T4Field(itemRow, "notifiedUtc") != "none",
                "The due memory item recorded no notification instant.");

            var passageNotifyHistory = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-read", "--resource-id", annotationA).ConfigureAwait(false);
            Require(passageNotifyHistory.ExitCode == 0 &&
                    S14T4Count(passageNotifyHistory.StandardOutput, "HISTORY aspect=notification act=notify") == 1,
                "The due passage did not retain exactly one notify act after repeated schedule passes.");
            var itemNotifyHistory = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-read", "--resource-id", S14T4ItemId).ConfigureAwait(false);
            Require(itemNotifyHistory.ExitCode == 0 &&
                    S14T4Count(itemNotifyHistory.StandardOutput, "HISTORY aspect=notification act=notify") == 1,
                "The due memory item did not retain exactly one notify act after repeated schedule passes.");

            // Restart/offline/lock: the marker is re-read, never re-recorded.
            await StopServiceAsync(live).ConfigureAwait(false);
            live = null;
            live = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            var lockedPass = await S14T4OwnerAsync(executable, fixture.ControlPipe, "intention-schedule-run").ConfigureAwait(false);
            Require(lockedPass.ExitCode != 0 && lockedPass.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "The locked restarted service ran a schedule pass instead of refusing it.");
            await S14T4UnlockAsync(executable, fixture.ControlPipe, passphrase).ConfigureAwait(false);
            var afterRestartPass = await S14T4OwnerAsync(executable, fixture.ControlPipe, "intention-schedule-run").ConfigureAwait(false);
            Require(afterRestartPass.ExitCode == 0 && S14T4Field(afterRestartPass.StandardOutput, "notifications") == "0",
                "A schedule pass after restart and explicit unlock re-recorded a due notification.");

            var restartSchedule = await S14T4OwnerAsync(executable, fixture.ControlPipe, "intentions").ConfigureAwait(false);
            var restartRow = S14T4Line(restartSchedule.StandardOutput, "INTENTION id=" + annotationA + " ");
            Require(S14T4Field(restartRow, "notified") == "true" && S14T4Field(restartRow, "acknowledged") == "false" &&
                    S14T4Field(restartRow, "notifiedUtc") == passageNotifiedAt,
                "The notified marker did not survive restart and explicit unlock as the same notification instant.");
            var restartNotifyHistory = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-read", "--resource-id", annotationA).ConfigureAwait(false);
            Require(restartNotifyHistory.ExitCode == 0 &&
                    S14T4Count(restartNotifyHistory.StandardOutput, "HISTORY aspect=notification act=notify") == 1,
                "Restart and a further schedule pass recorded a second notify act.");

            // Acknowledged is distinct from completed. The vault serves only the
            // live revision of a resource, so the exact earlier state is retained
            // as lifecycle history (act, from/to state, exact recorded time) and
            // the current read is checked before and after the acknowledgement.
            var notifiedLifecycleRevision = WorkspaceNumber(restartRow, "lifecycle");
            var notifiedRead = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-read", "--resource-id", annotationA).ConfigureAwait(false);
            Require(notifiedRead.ExitCode == 0 &&
                    S14T4Field(notifiedRead.StandardOutput, "notified") == "true" &&
                    S14T4Field(notifiedRead.StandardOutput, "acknowledged") == "false" &&
                    WorkspaceNumber(notifiedRead.StandardOutput, "revision") == notifiedLifecycleRevision,
                "The live read before acknowledgement did not report the notified, unacknowledged revision.");
            var acknowledged = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-intention-acknowledge", "--resource-id", annotationA,
                "--expected-revision", notifiedLifecycleRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(acknowledged.ExitCode == 0 &&
                    S14T4Field(acknowledged.StandardOutput, "acknowledged") == "true" &&
                    S14T4Field(acknowledged.StandardOutput, "intention") == "pending",
                "Acknowledging the passage reminder completed the intention instead of only marking it seen.");
            var acknowledgedRevision = WorkspaceNumber(acknowledged.StandardOutput, "revision");

            var acknowledgedRead = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-read", "--resource-id", annotationA).ConfigureAwait(false);
            Require(acknowledgedRead.ExitCode == 0 &&
                    WorkspaceNumber(acknowledgedRead.StandardOutput, "revision") == acknowledgedRevision &&
                    S14T4Field(acknowledgedRead.StandardOutput, "notified") == "true" &&
                    S14T4Field(acknowledgedRead.StandardOutput, "acknowledged") == "true" &&
                    S14T4Field(acknowledgedRead.StandardOutput, "intention") == "pending",
                "The acknowledged passage lifecycle did not stay notified and pending at the acknowledged revision.");
            Require(S14T4Count(acknowledgedRead.StandardOutput, "HISTORY aspect=notification act=notify") == 1 &&
                    S14T4Count(acknowledgedRead.StandardOutput, "HISTORY aspect=notification act=acknowledge") == 1,
                "The passage lifecycle did not retain both the notify and acknowledge acts as history.");
            var notifyRecordedAt = DateTimeOffset.Parse(
                S14T4Field(S14T4Line(acknowledgedRead.StandardOutput, "HISTORY aspect=notification act=notify"), "recordedUtc"),
                CultureInfo.InvariantCulture);
            var acknowledgeRecordedAt = DateTimeOffset.Parse(
                S14T4Field(S14T4Line(acknowledgedRead.StandardOutput, "HISTORY aspect=notification act=acknowledge"), "recordedUtc"),
                CultureInfo.InvariantCulture);
            Require(acknowledgeRecordedAt >= notifyRecordedAt,
                "The retained notification history did not keep the acknowledge act at or after the notify act.");

            var completed = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-intention-complete", "--resource-id", annotationA,
                "--expected-revision", acknowledgedRevision.ToString(CultureInfo.InvariantCulture),
                "--reason", "owner finished the intended action").ConfigureAwait(false);
            Require(completed.ExitCode == 0 && S14T4Field(completed.StandardOutput, "intention") == "done",
                "The explicit completion act did not mark the passage intention done.");
            var completedAgain = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-intention-complete", "--resource-id", annotationA,
                "--expected-revision", WorkspaceNumber(completed.StandardOutput, "revision").ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(completedAgain.ExitCode != 0,
                "A second completion act on an already-done passage intention was accepted.");

            // A superseded passage intention names the replacing passage and
            // keeps its rationale and original anchor.
            var superseded = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-intention-supersede", "--resource-id", annotationB,
                "--expected-revision", WorkspaceNumber(annotationBLifecycle.StandardOutput, "revision").ToString(CultureInfo.InvariantCulture),
                "--replacement", annotationC,
                "--reason", "a newer passage expresses the intention").ConfigureAwait(false);
            Require(superseded.ExitCode == 0 && S14T4Field(superseded.StandardOutput, "intention") == "superseded" &&
                    S14T4Field(superseded.StandardOutput, "supersededBy") == annotationC,
                "The passage intention was not superseded by the named replacement passage.");
            var supersededHistory = S14T4Line(superseded.StandardOutput, "HISTORY aspect=intention");
            Require(supersededHistory.Contains("act=supersedeIntention", StringComparison.Ordinal) &&
                    supersededHistory.Contains("replacement=" + annotationC, StringComparison.Ordinal) &&
                    supersededHistory.Contains("rationale=a newer passage expresses the intention", StringComparison.Ordinal),
                "The supersession history lost its act, replacement pointer or rationale.");
            var annotationBRead = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", annotationB).ConfigureAwait(false);
            Require(annotationBRead.ExitCode == 0 && S14T4Field(annotationBRead.StandardOutput, "revision") == "1" &&
                    S14T4Line(annotationBRead.StandardOutput, "ANCHOR ").Contains("ANCHOR id=" + anchorId + " ", StringComparison.Ordinal),
                "Superseding a passage intention rewrote the annotation instead of keeping its anchor and revision.");

            // Validity and review-after live on the shared record, not on the
            // annotation bytes.
            var liveLifecycle = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-read", "--resource-id", annotationA).ConfigureAwait(false);
            Require(liveLifecycle.ExitCode == 0, "The live passage lifecycle record was unreadable.");
            var liveRevision = WorkspaceNumber(liveLifecycle.StandardOutput, "revision");
            var validityInput = Path.Combine(fixture.OutputDirectory, "s14-t4-validity.json");
            await File.WriteAllBytesAsync(validityInput, S14T4ValidityDraft()).ConfigureAwait(false);
            var validity = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-validity-set", "--resource-id", annotationA,
                "--expected-revision", liveRevision.ToString(CultureInfo.InvariantCulture),
                "--input-file", validityInput).ConfigureAwait(false);
            Require(validity.ExitCode == 0 &&
                    S14T4Field(validity.StandardOutput, "validFrom").StartsWith("exact:", StringComparison.Ordinal) &&
                    S14T4Field(validity.StandardOutput, "reviewAfter").StartsWith("exact:", StringComparison.Ordinal),
                "The passage lifecycle did not record exact validity and review-after stamps.");
            Require(validity.StandardOutput.Contains("HISTORY aspect=temporal act=setValidity", StringComparison.Ordinal),
                "The validity act was not retained in the lifecycle history.");

            // A read never promotes truth and never rewrites the annotation.
            var annotationBeforePolicy = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", annotationA).ConfigureAwait(false);
            Require(annotationBeforePolicy.ExitCode == 0 &&
                    S14T4Field(annotationBeforePolicy.StandardOutput, "revision") == "1" &&
                    S14T4Field(annotationBeforePolicy.StandardOutput, "review") == "Accepted" &&
                    S14T4Field(annotationBeforePolicy.StandardOutput, "origin") == "Owner",
                "The passage lifecycle or a read changed the annotation's revision, review status or origin.");

            // Pin, protected refusal, restore, archive and restore over the passage.
            foreach (var (act, reason, expectedState) in new[]
                     {
                         ("pin", (string?)null, "pinned"),
                         ("restore", null, "normal"),
                         ("archive", "owner set this passage aside", "archived"),
                         ("restore", null, "normal")
                     })
            {
                var recallArguments = new List<string> { "--resource-id", annotationA, "--act", act };
                if (reason is not null)
                {
                    recallArguments.Add("--reason");
                    recallArguments.Add(reason);
                }

                var applied = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                    "recall-apply", recallArguments.ToArray()).ConfigureAwait(false);
                Require(applied.ExitCode == 0 && S14T4Field(applied.StandardOutput, "state") == expectedState,
                    "The recall act " + act + " on the passage annotation did not produce " + expectedState + ".");
                var readBack = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                    "recall-read", "--resource-id", annotationA).ConfigureAwait(false);
                Require(readBack.ExitCode == 0 && S14T4Field(readBack.StandardOutput, "state") == expectedState,
                    "The recall read did not report the passage disposition " + expectedState + ".");
            }

            var pinned = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "recall-apply", "--resource-id", annotationA, "--act", "pin").ConfigureAwait(false);
            Require(pinned.ExitCode == 0, "The passage annotation could not be pinned before the protection check.");
            var refusedDemote = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "recall-apply", "--resource-id", annotationA, "--act", "demote",
                "--reason", "should be refused while pinned").ConfigureAwait(false);
            Require(refusedDemote.ExitCode != 0,
                "A pinned passage annotation was demoted without an explicit restore.");
            var restoredAgain = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "recall-apply", "--resource-id", annotationA, "--act", "restore").ConfigureAwait(false);
            Require(restoredAgain.ExitCode == 0, "The pinned passage annotation could not be restored.");

            var demotedItem = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "recall-apply", "--resource-id", S14T4ItemId, "--act", "demote",
                "--reason", "existing memory-item recall path").ConfigureAwait(false);
            Require(demotedItem.ExitCode == 0 && S14T4Field(demotedItem.StandardOutput, "state") == "demoted",
                "The existing memory-item recall path regressed on demote.");
            var restoredItem = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "recall-apply", "--resource-id", S14T4ItemId, "--act", "restore").ConfigureAwait(false);
            Require(restoredItem.ExitCode == 0, "The existing memory-item recall path regressed on restore.");

            var annotationAfterPolicy = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", annotationA).ConfigureAwait(false);
            Require(annotationAfterPolicy.ExitCode == 0 &&
                    S14T4Field(annotationAfterPolicy.StandardOutput, "revision") == "1" &&
                    S14T4Field(annotationAfterPolicy.StandardOutput, "review") == "Accepted" &&
                    S14T4Field(annotationAfterPolicy.StandardOutput, "kind") == "Intention",
                "A recall act or its read changed the annotation's content, revision, review status or kind.");

            // Publication and retained revisions do not apply to a passage.
            var liveAfterPolicy = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-read", "--resource-id", annotationA).ConfigureAwait(false);
            var liveAfterPolicyRevision = WorkspaceNumber(liveAfterPolicy.StandardOutput, "revision");
            var publish = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-publish", "--resource-id", annotationA,
                "--expected-revision", liveAfterPolicyRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(publish.ExitCode != 0, "A passage annotation was catalog-published through the lifecycle record.");
            var retain = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-retain", "--resource-id", annotationA,
                "--expected-revision", liveAfterPolicyRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(retain.ExitCode != 0, "A passage annotation revision was retained although its own history already keeps it.");
            var retainedRead = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-retained-read", "--resource-id", annotationA, "--resource-revision", "1").ConfigureAwait(false);
            Require(retainedRead.ExitCode != 0, "A retained-read returned a passage annotation that was never retained.");

            // An owner-only annotation never becomes grantable.
            var grant = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "grant", "--resource-ids", annotationA,
                "--expires-at-utc", DateTimeOffset.UtcNow.AddMinutes(10).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                "--handoff-file", Path.Combine(fixture.OutputDirectory, "s14-t4-handoff.txt")).ConfigureAwait(false);
            Require(grant.ExitCode != 0 && grant.StandardError.Contains("invalid_grant_request", StringComparison.Ordinal),
                "An owner-only passage annotation was accepted into an agent scope grant.");

            // The agent pipe refuses the owner lifecycle, intention and recall operations.
            foreach (var operation in new[]
                     {
                         ServiceRequestHandler.LifecycleCreateOperation,
                         ServiceRequestHandler.LifecycleReadOperation,
                         ServiceRequestHandler.LifecycleRetainRevisionOperation,
                         ServiceRequestHandler.OwnerRecallApplyOperation,
                         ServiceRequestHandler.OwnerRecallReadOperation,
                         ServiceRequestHandler.OwnerIntentionScheduleListOperation,
                         ServiceRequestHandler.OwnerIntentionScheduleRunOperation
                     })
            {
                var denied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                    operation
                }).ConfigureAwait(false);
                Require(!string.IsNullOrEmpty(ReplyCode(denied)),
                    "The agent pipe answered the owner-only lifecycle/recall operation " + operation + ".");
            }

            // Final restart proves every state persisted, and a final read pass
            // proves no private-state change happens behind the owner's back.
            await StopServiceAsync(live).ConfigureAwait(false);
            live = null;
            live = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            await S14T4UnlockAsync(executable, fixture.ControlPipe, passphrase).ConfigureAwait(false);
            var finalSchedule = await S14T4OwnerAsync(executable, fixture.ControlPipe, "intentions").ConfigureAwait(false);
            Require(finalSchedule.ExitCode == 0 && S14T4Field(finalSchedule.StandardOutput, "count") == "1" &&
                    S14T4Line(finalSchedule.StandardOutput, "INTENTION id=" + S14T4ItemId + " ").Contains("notified=true", StringComparison.Ordinal),
                "The pending memory-item intention did not survive the final restart while the done passage left the schedule.");
            var finalDone = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-read", "--resource-id", annotationA).ConfigureAwait(false);
            Require(finalDone.ExitCode == 0 && S14T4Field(finalDone.StandardOutput, "intention") == "done" &&
                    S14T4Field(finalDone.StandardOutput, "acknowledged") == "true",
                "The completed passage intention did not persist its done and acknowledged states.");
            var finalSuperseded = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "lifecycle-read", "--resource-id", annotationB).ConfigureAwait(false);
            Require(finalSuperseded.ExitCode == 0 && S14T4Field(finalSuperseded.StandardOutput, "intention") == "superseded" &&
                    S14T4Field(finalSuperseded.StandardOutput, "supersededBy") == annotationC,
                "The superseded passage intention lost its replacement pointer after restart.");
            var finalAnnotation = await S14T4OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", annotationA).ConfigureAwait(false);
            Require(finalAnnotation.ExitCode == 0 && S14T4Field(finalAnnotation.StandardOutput, "revision") == "1" &&
                    S14T4Line(finalAnnotation.StandardOutput, "ANCHOR ").Contains("file=" + fileId, StringComparison.Ordinal),
                "The passage annotation source anchor did not survive the full lifecycle/recall scenario.");

            Console.WriteLine("PASS S14-T4 lifecycle and recall over memory items and passage annotations: each due notification recorded once across repeated, locked and restarted passes with one retained notify act and one notification instant, acknowledgement distinct from completion, earlier states retained as ordered lifecycle history, superseded passage keeps replacement and source, validity/review-after on the shared record, reads and recall acts never rewrite the annotation, publication/retention refused for a passage, and owner-only annotations stay out of grants and the agent pipe.");
            Console.WriteLine("LIMIT synthetic Windows-only vault and named pipes; no real model/provider call, WPF, browser or MCP tool was exercised.");
        }
        finally
        {
            if (live is not null)
            {
                await StopServiceAsync(live).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> S14T4CreateIntentionAnnotationAsync(
        string executable,
        S14T4Fixture fixture,
        string fileId,
        string anchorId,
        string tag) =>
        await S14T4CreateAnnotationAsync(executable, fixture, fileId, anchorId, "intention-" + tag, includeKind: true).ConfigureAwait(false);

    private static async Task<string> S14T4CreateAnnotationAsync(
        string executable,
        S14T4Fixture fixture,
        string fileId,
        string anchorId,
        string tag,
        bool includeKind)
    {
        var draft = new Dictionary<string, object?>
        {
            ["anchorId"] = anchorId,
            ["facets"] = includeKind ? new[] { "intention" } : Array.Empty<string>()
        };
        if (includeKind)
        {
            draft["kind"] = "intention";
            draft["author"] = "user";
            draft["creationMethod"] = "directWrite";
            draft["evidence"] = "unverified";
            draft["level"] = "l1";
        }

        var inputPath = Path.Combine(fixture.OutputDirectory, "s14-t4-annotation-" + tag + ".json");
        await File.WriteAllBytesAsync(inputPath, JsonSerializer.SerializeToUtf8Bytes(draft)).ConfigureAwait(false);
        var created = await S14T4OwnerAsync(executable, fixture.ControlPipe,
            "cognitive-annotation-create", "--file-id", fileId, "--input-file", inputPath).ConfigureAwait(false);
        Require(created.ExitCode == 0 && created.StandardOutput.Contains("origin=Owner", StringComparison.Ordinal),
            "The owner CLI did not create the synthetic " + tag + " passage annotation.");
        return WorkspaceToken(created.StandardOutput, "id");
    }

    private static byte[] S14T4ValidityDraft()
    {
        var recorded = DateTimeOffset.UtcNow;
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            rationale = "owner set the passage validity window",
            replacementResourceId = (string?)null,
            occurred = (object?)null,
            recorded = (object?)null,
            validFrom = new { precision = "exact", instantUtc = recorded.AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), timeZoneOffsetMinutes = 0 },
            validUntil = new { precision = "exact", instantUtc = recorded.AddDays(30).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), timeZoneOffsetMinutes = 0 },
            reviewAfter = new { precision = "exact", instantUtc = recorded.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), timeZoneOffsetMinutes = 0 },
            intentionDueAt = (object?)null,
            clearIntentionDueAt = false,
            contextTrigger = (object?)null,
            disableContextTrigger = false
        });
    }

    private static async Task<CommandResult> S14T4OwnerAsync(
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

    private static async Task S14T4UnlockAsync(string executable, string controlPipe, string passphrase)
    {
        var unlock = await RunOwnerCommandWithInputAsync(executable, passphrase + Environment.NewLine,
            "owner", "unlock", "--control-pipe", controlPipe).ConfigureAwait(false);
        Require(unlock.ExitCode == 0 && unlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
            "The owner could not unlock the restarted S14-T4 vault.");
    }

    /// <summary>
    /// Waits, bounded, for the running service's own 5s time-trigger poll to mark
    /// every named subject notified in its encrypted lifecycle record. The poll is
    /// observed only through the owner schedule view, never by calling the pass.
    /// </summary>
    private static async Task<bool> S14T4WaitForBackgroundNotificationsAsync(
        string executable,
        string controlPipe,
        IReadOnlyList<string> resourceIds,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var schedule = await S14T4OwnerAsync(executable, controlPipe, "intentions").ConfigureAwait(false);
            if (schedule.ExitCode == 0 && resourceIds.All(id =>
                {
                    try
                    {
                        return S14T4Field(S14T4Line(schedule.StandardOutput, "INTENTION id=" + id + " "), "notified") == "true";
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                }))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>The first line of one command's output that starts with a prefix.</summary>
    private static string S14T4Line(string output, string prefix) =>
        output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))
        ?? throw new InvalidOperationException("The command output did not carry a line starting with '" + prefix + "'.");

    /// <summary>The number of output lines that start with a prefix.</summary>
    private static int S14T4Count(string output, string prefix) =>
        output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// One scheduled intention block: its INTENTION line plus the title and
    /// preview lines the service printed for exactly that subject.
    /// </summary>
    private static string S14T4ScheduleBlock(string output, string resourceId)
    {
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var start = Array.FindIndex(lines, line =>
            line.StartsWith("INTENTION id=" + resourceId + " ", StringComparison.Ordinal));
        if (start < 0)
        {
            throw new InvalidOperationException("The owner schedule did not list " + resourceId + ".");
        }

        var end = start + 1;
        while (end < lines.Length && !lines[end].StartsWith("INTENTION ", StringComparison.Ordinal))
        {
            end++;
        }

        return "\n" + string.Join("\n", lines[start..end]) + "\n";
    }

    /// <summary>The first value of <paramref name="name"/>= anywhere in the output.</summary>
    private static string S14T4Field(string output, string name)
    {
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith(name + "=", StringComparison.Ordinal))
                {
                    return token[(name.Length + 1)..];
                }
            }
        }

        throw new InvalidOperationException("The command output did not carry " + name + ".");
    }
}

internal sealed class S14T4Fixture : IDisposable
{
    private bool _disposed;

    private S14T4Fixture(string root, string liveRoot, string settingsPath, string controlPipe, string agentPipe, string outputDirectory)
    {
        Root = root;
        LiveRoot = liveRoot;
        SettingsPath = settingsPath;
        ControlPipe = controlPipe;
        AgentPipe = agentPipe;
        OutputDirectory = outputDirectory;
    }

    public string Root { get; }
    public string LiveRoot { get; }
    public string SettingsPath { get; }
    public string ControlPipe { get; }
    public string AgentPipe { get; }
    public string OutputDirectory { get; }

    public static S14T4Fixture Create()
    {
        var repositoryRoot = SmokeFixture.FindRepositoryRoot();
        var root = Path.Combine(repositoryRoot, "runtime", "s14-t4", "run-" + Guid.NewGuid().ToString("N"));
        var liveRoot = Path.Combine(root, "live");
        var outputDirectory = Path.Combine(root, "output");
        foreach (var directory in new[] { liveRoot, outputDirectory })
        {
            Directory.CreateDirectory(directory);
        }

        var controlPipe = "MetaBrainS14T4-" + Guid.NewGuid().ToString("N");
        var settingsPath = S12T5Fixture.WriteSettingsForTest(liveRoot, controlPipe);
        return new S14T4Fixture(root, liveRoot, settingsPath, controlPipe, controlPipe + ".agent", outputDirectory);
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
                Path.GetFullPath(Path.Combine(repositoryRoot, "runtime", "s14-t4"))) + Path.DirectorySeparatorChar;
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
