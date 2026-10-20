using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MetaBrain.Connections;
using MetaBrain.OwnerUi;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string S7T6Zone = "s7t6-review-zone";
    private const string S7T6StaleItem = "s7t6-stale-item";
    private const string S7T6UnknownItem = "s7t6-unknown-item";
    private const string S7T6IntentionItem = "s7t6-intention-item";
    private const string S7T6ArchivedItem = "s7t6-archived-item";
    private const string S7T6PurgedCitingItem = "s7t6-purge-citing-item";
    private const string S7T6SourceA = "s7t6-source-a";
    private const string S7T6SourceB = "s7t6-source-b";
    private const string S7T6SourcePurge = "s7t6-source-purge";
    private const string S7T6StaleTitle = "S7T6_SYNTHETIC_STALE_TITLE_1c8e";
    private const string S7T6StaleBody = "S7T6_SYNTHETIC_STALE_BODY_5b90: the owner wrote this synthetic synthesis against one source revision.";
    private const string S7T6UnknownBody = "S7T6_SYNTHETIC_UNKNOWN_BODY_2f34: this synthetic note cites a source whose availability nobody asserted.";
    private const string S7T6IntentionBody = "S7T6_SYNTHETIC_INTENTION_BODY_7a26: the owner keeps one synthetic intention pending across the restart.";
    private const string S7T6ArchivedBody = "S7T6_SYNTHETIC_ARCHIVED_BODY_9d51: this synthetic note is set aside by the owner and must stay set aside.";
    private const string S7T6ReviewReason = "S7T6_SYNTHETIC_REVIEW_REASON_4e18: the owner read the moved source and recorded what it means.";
    private const string S7T6PurgeReason = "S7T6_SYNTHETIC_PURGE_REASON_6c72: the owner deletes this synthetic source and everything derived from it.";

    /// <summary>
    /// S7-T6 review-after and lifecycle reconciliation: a source that moved
    /// past the revision an item cited, a source that is gone, a source whose
    /// availability nobody asserted, a review-after deadline that passed and
    /// one that is only known as a year, an explicit owner review that changes
    /// nothing about the item, a purge that cannot be undone by re-enrolling,
    /// re-ingesting or rebuilding the index, and a restart that keeps the
    /// due, archive and review states.
    /// </summary>
    internal static async Task RunS7T6ReviewAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S7-T6 review smoke requires Windows named pipes, DPAPI and WPF.");
        }

        Console.WriteLine("RUN S7-T6 review-after, source-change invalidation, lifecycle reconciliation and no-resurrection");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        var passphraseBytes = RandomNumberGenerator.GetBytes(24);
        var passphrase = Convert.ToBase64String(passphraseBytes);
        CryptographicOperations.ZeroMemory(passphraseBytes);
        string? recoveryCode = null;
        try
        {
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The synthetic S7-T6 vault was not provisioned.");
            recoveryCode = ParseRecoveryCode(provisioned.StandardOutput);
            var unlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S7-T6 vault did not unlock.");

            // ---- Sources: one that will move, one whose availability is unknown ----
            await WriteS7T6SourceAsync(connectionsExecutable, fixture, S7T6SourceA, "available", "v1").ConfigureAwait(false);
            await WriteS7T6SourceAsync(connectionsExecutable, fixture, S7T6SourceB, "unknown", "v1").ConfigureAwait(false);

            // ---- Items: a synthesis citing the source at its live revision ----
            await WriteS7T6ItemAsync(connectionsExecutable, fixture, S7T6StaleItem, S7T6StaleTitle, S7T6StaleBody,
                S7T6SourceA, 1L, "synthesis").ConfigureAwait(false);
            await WriteS7T6ItemAsync(connectionsExecutable, fixture, S7T6UnknownItem, "S7T6_SYNTHETIC_UNKNOWN_TITLE_8f13",
                S7T6UnknownBody, S7T6SourceB, 1L, "lesson").ConfigureAwait(false);
            await WriteS7T6ItemAsync(connectionsExecutable, fixture, S7T6IntentionItem, "S7T6_SYNTHETIC_INTENTION_TITLE_3d67",
                S7T6IntentionBody, null, null, "intention").ConfigureAwait(false);
            await WriteS7T6ItemAsync(connectionsExecutable, fixture, S7T6ArchivedItem, "S7T6_SYNTHETIC_ARCHIVED_TITLE_5b22",
                S7T6ArchivedBody, null, null, "lesson").ConfigureAwait(false);

            // ---- Lifecycle records: one deadline already passed, one only known as a year ----
            var dueAt = DateTimeOffset.UtcNow.AddHours(-2);
            await WriteS7T6LifecycleAsync(connectionsExecutable, fixture, S7T6StaleItem, S7T6Stamp("exact", instantUtc: dueAt, zoneOffsetMinutes: 0))
                .ConfigureAwait(false);
            await WriteS7T6LifecycleAsync(connectionsExecutable, fixture, S7T6UnknownItem, S7T6Stamp("year", year: 2027))
                .ConfigureAwait(false);
            await WriteS7T6LifecycleAsync(connectionsExecutable, fixture, S7T6IntentionItem, null).ConfigureAwait(false);
            await WriteS7T6LifecycleAsync(connectionsExecutable, fixture, S7T6ArchivedItem, null).ConfigureAwait(false);

            // The intention stays pending with its own notification and
            // acknowledgement markers; neither marker completes it.
            var intentionLifecycle = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T6IntentionItem)
                .ConfigureAwait(false);
            Require(intentionLifecycle.ExitCode == 0 && intentionLifecycle.StandardOutput.Contains("intention=pending", StringComparison.Ordinal),
                "The synthetic S7-T6 intention did not start pending (" + intentionLifecycle.StandardError.Trim() + ").");
            var notify = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-intention-notify", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T6IntentionItem, "--expected-revision", "1").ConfigureAwait(false);
            Require(notify.ExitCode == 0, "The synthetic S7-T6 intention could not be notified.");
            var acknowledge = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-intention-acknowledge", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T6IntentionItem, "--expected-revision", "2").ConfigureAwait(false);
            Require(acknowledge.ExitCode == 0, "The synthetic S7-T6 intention could not be acknowledged.");

            // The owner sets one item aside; the archive state is private
            // owner state and must survive a restart.
            var archived = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "recall-apply", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T6ArchivedItem, "--act", "archive",
                "--reason", "S7T6_SYNTHETIC_ARCHIVE_REASON_8e42: the owner set this synthetic note aside.")
                .ConfigureAwait(false);
            Require(archived.ExitCode == 0 && archived.StandardOutput.Contains("state=archived", StringComparison.Ordinal),
                "The synthetic S7-T6 item could not be set aside (" + archived.StandardError.Trim() + ").");

            // ---- The source changes: the item's citation is now behind the live source ----
            var before = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T6StaleItem).ConfigureAwait(false);
            Require(before.ExitCode == 0 && before.StandardOutput.Contains("revision=1", StringComparison.Ordinal) &&
                    before.StandardOutput.Contains(S7T6StaleBody, StringComparison.Ordinal) &&
                    before.StandardOutput.Contains("evidence=unverified", StringComparison.Ordinal),
                "The synthetic S7-T6 item was not readable before the source change.");

            var revised = await WriteS7T6SourceRevisionAsync(connectionsExecutable, fixture, S7T6SourceA, 1L, "v2", "unavailable")
                .ConfigureAwait(false);
            Require(revised.ExitCode == 0 && revised.StandardOutput.Contains("SOURCE stored revision=2", StringComparison.Ordinal),
                "The synthetic S7-T6 source did not move to its second revision (" + revised.StandardError.Trim() + ").");

            // ---- The review queue: what the service reports, and nothing else ----
            var queue = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "review-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(queue.ExitCode == 0 && queue.StandardOutput.Contains("REVIEW QUEUE", StringComparison.Ordinal),
                "The owner could not read the review queue (" + queue.StandardError.Trim() + ").");
            var staleEntry = FindS7T6ReviewEntry(queue.StandardOutput, S7T6StaleItem);
            Require(staleEntry is not null,
                "The review queue did not report the item whose cited source moved (" + queue.StandardOutput.Trim() + ").");
            Require(staleEntry!.Contains("cited_source_revision_changed", StringComparison.Ordinal),
                "The review queue did not report the moved source revision (" + staleEntry + ").");
            Require(staleEntry.Contains("cited_source_unavailable", StringComparison.Ordinal),
                "The review queue did not report the unavailable source (" + staleEntry + ").");
            Require(staleEntry.Contains("review_after_due", StringComparison.Ordinal),
                "The review queue did not report the passed review-after deadline (" + staleEntry + ").");
            Require(staleEntry.Contains("reviewAfter=exact:", StringComparison.Ordinal),
                "The review queue did not carry the review-after deadline with its precision (" + staleEntry + ").");
            Require(staleEntry.Contains("CITATION source=" + S7T6SourceA + " cited=1 live=2 availability=unavailable", StringComparison.Ordinal),
                "The review queue did not carry the exact cited and live source revisions (" + staleEntry + ").");
            Require(staleEntry.Contains("state=needs_review", StringComparison.Ordinal),
                "The moved-source item was not reported as needing review (" + staleEntry + ").");

            var unknownEntry = FindS7T6ReviewEntry(queue.StandardOutput, S7T6UnknownItem);
            Require(unknownEntry is not null,
                "The review queue did not report the item whose deadline cannot be decided (" + queue.StandardOutput.Trim() + ").");
            Require(unknownEntry!.Contains("state=undetermined", StringComparison.Ordinal) &&
                    unknownEntry.Contains("undetermined=[review_after_undetermined]", StringComparison.Ordinal),
                "A coarsely known deadline was not reported as undetermined (" + unknownEntry + ").");
            Require(unknownEntry.Contains("CITATION source=" + S7T6SourceB + " cited=1 live=1 availability=unknown", StringComparison.Ordinal),
                "An unknown availability was not reported as unknown (" + unknownEntry + ").");
            Require(!unknownEntry.Contains("cited_source_unavailable", StringComparison.Ordinal) &&
                    !unknownEntry.Contains("cited_source_revision_changed", StringComparison.Ordinal),
                "An unknown source was reported as unavailable or moved (" + unknownEntry + ").");
            Require(unknownEntry.Contains("reviewAfter=year:2027@", StringComparison.Ordinal),
                "A coarsely known deadline was narrowed into a fabricated date (" + unknownEntry + ").");

            // Nothing about the item itself changed: no belief was denied, no
            // authored content moved, no evidence level was touched.
            var after = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T6StaleItem).ConfigureAwait(false);
            Require(after.ExitCode == 0 && after.StandardOutput.Contains("revision=1", StringComparison.Ordinal) &&
                    after.StandardOutput.Contains(S7T6StaleBody, StringComparison.Ordinal) &&
                    after.StandardOutput.Contains("evidence=unverified", StringComparison.Ordinal) &&
                    after.StandardOutput.Contains("kind=synthesis", StringComparison.Ordinal),
                "The moved source rewrote the item's content, evidence or revision (" + after.StandardOutput.Trim() + ").");
            var staleLifecycle = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T6StaleItem).ConfigureAwait(false);
            Require(staleLifecycle.ExitCode == 0 && staleLifecycle.StandardOutput.Contains("publication=proposal", StringComparison.Ordinal) &&
                    staleLifecycle.StandardOutput.Contains("intention=none", StringComparison.Ordinal),
                "The moved source changed the lifecycle state of the item (" + staleLifecycle.StandardOutput.Trim() + ").");

            // ---- The agent channel never gets the owner's review surfaces ----
            var agentReview = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "owner.review.list",
            }).ConfigureAwait(false);
            Require(ReplyCode(agentReview) == "forbidden",
                "The agent channel accepted an owner review operation (reply=" + agentReview.GetRawText() + ").");

            // ---- The owner reviews one item: the act records attention, nothing else ----
            var recorded = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "review-record", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T6StaleItem, "--reason", S7T6ReviewReason).ConfigureAwait(false);
            Require(recorded.ExitCode == 0 && recorded.StandardOutput.Contains("REVIEW RECORDED subject=" + S7T6StaleItem, StringComparison.Ordinal) &&
                    recorded.StandardOutput.Contains("acknowledged=[", StringComparison.Ordinal),
                "The owner could not record the review (" + recorded.StandardError.Trim() + ").");
            var queueAfterReview = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "review-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            var acknowledgedEntry = FindS7T6ReviewEntry(queueAfterReview.StandardOutput, S7T6StaleItem);
            Require(acknowledgedEntry is not null && acknowledgedEntry.Contains("pending=[]", StringComparison.Ordinal),
                "The recorded review did not answer the reasons the owner had seen (" + acknowledgedEntry ?? "(missing)" + ").");
            Require(acknowledgedEntry!.Contains("lastReviewed=", StringComparison.Ordinal) &&
                    !acknowledgedEntry.Contains("lastReviewed=unknown", StringComparison.Ordinal),
                "The review queue did not carry the owner's last review (" + acknowledgedEntry + ").");
            var afterReviewRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T6StaleItem).ConfigureAwait(false);
            Require(afterReviewRead.ExitCode == 0 && afterReviewRead.StandardOutput.Contains("revision=1", StringComparison.Ordinal) &&
                    afterReviewRead.StandardOutput.Contains(S7T6StaleBody, StringComparison.Ordinal) &&
                    afterReviewRead.StandardOutput.Contains("evidence=unverified", StringComparison.Ordinal),
                "Recording a review changed the item's content, evidence or revision.");
            var reviewOfUnknown = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "review-record", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "s7t6-not-an-item", "--reason", S7T6ReviewReason).ConfigureAwait(false);
            Require(reviewOfUnknown.ExitCode != 0,
                "The owner recorded a review of an item that does not exist.");

            // A reason the owner answered settles, and it settles only while
            // the thing behind it stays put: the coarsely known deadline the
            // owner answered is settled, and the moved source opens again when
            // it moves once more.
            var reviewOfUndetermined = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "review-record", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T6UnknownItem, "--reason", S7T6ReviewReason).ConfigureAwait(false);
            Require(reviewOfUndetermined.ExitCode == 0,
                "The owner could not record the review of the undetermined item (" + reviewOfUndetermined.StandardError.Trim() + ").");
            var queueAfterSecondReview = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "review-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            var settledEntry = FindS7T6ReviewEntry(queueAfterSecondReview.StandardOutput, S7T6UnknownItem);
            Require(settledEntry is not null && settledEntry.Contains("pending=[]", StringComparison.Ordinal),
                "The answered undetermined deadline was not settled (" + settledEntry ?? "(missing)" + ").");
            Require(settledEntry!.Contains("state=undetermined", StringComparison.Ordinal) &&
                    settledEntry.Contains("undetermined=[review_after_undetermined]", StringComparison.Ordinal),
                "Answering the deadline changed what the queue reports about it (" + settledEntry + ").");

            var revisedAgain = await WriteS7T6SourceRevisionAsync(connectionsExecutable, fixture, S7T6SourceA, 2L, "v3", "unavailable")
                .ConfigureAwait(false);
            Require(revisedAgain.ExitCode == 0 && revisedAgain.StandardOutput.Contains("SOURCE stored revision=3", StringComparison.Ordinal),
                "The synthetic S7-T6 source did not move to its third revision (" + revisedAgain.StandardError.Trim() + ").");
            var queueAfterSecondMove = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "review-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            var reopenedEntry = FindS7T6ReviewEntry(queueAfterSecondMove.StandardOutput, S7T6StaleItem);
            Require(reopenedEntry is not null && reopenedEntry.Contains("pending=", StringComparison.Ordinal) &&
                    reopenedEntry.Contains("cited_source_revision_changed", StringComparison.Ordinal) &&
                    !reopenedEntry.Contains("pending=[]", StringComparison.Ordinal),
                "A source that moved again did not re-open the reason the owner had answered (" + reopenedEntry ?? "(missing)" + ").");
            Require(reopenedEntry!.Contains("cited=1 live=3", StringComparison.Ordinal),
                "The re-opened reason did not carry the exact cited and live source revisions (" + reopenedEntry + ").");

            // ---- Purge, then re-enroll / re-ingest / rebuild: no resurrection ----
            await WriteS7T6SourceAsync(connectionsExecutable, fixture, S7T6SourcePurge, "available", "v1").ConfigureAwait(false);
            // One item cites the source that is about to be purged. The item is
            // not a derivative of the source, so it survives the purge and its
            // citation becomes a missing source, which is a different fact from
            // an unavailable one.
            await WriteS7T6ItemAsync(connectionsExecutable, fixture, S7T6PurgedCitingItem, "S7T6_SYNTHETIC_PURGE_CITING_TITLE_5e91",
                "S7T6_SYNTHETIC_PURGE_CITING_BODY_2d47: this synthetic note cites the source the owner deletes.",
                S7T6SourcePurge, 1L, "lesson").ConfigureAwait(false);
            var enrolled = await EnrollS7T6SourceAsync(connectionsExecutable, fixture, S7T6SourcePurge).ConfigureAwait(false);
            Require(enrolled.ExitCode == 0 && enrolled.StandardOutput.Contains("SOURCE id=" + S7T6SourcePurge, StringComparison.Ordinal),
                "The synthetic S7-T6 source could not be enrolled (exit=" + enrolled.ExitCode + " out=" + enrolled.StandardOutput.Trim() + " err=" + enrolled.StandardError.Trim() + ").");
            var ingested = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe, "--source-id", S7T6SourcePurge).ConfigureAwait(false);
            Require(ingested.ExitCode == 0 || ingested.StandardError.Contains("unsupported", StringComparison.Ordinal) ||
                    ingested.StandardError.Contains("invalid", StringComparison.Ordinal),
                "The synthetic S7-T6 source could not be ingested at all (" + ingested.StandardError.Trim() + ").");

            var sourceRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T6SourcePurge).ConfigureAwait(false);
            Require(sourceRead.ExitCode == 0, "The synthetic S7-T6 source could not be read before the purge.");
            var sourceRevision = ParseS7T6Revision(sourceRead.StandardOutput, "SOURCE");
            var preview = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "purge-preview", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T6SourcePurge, "--resource-revision", sourceRevision.ToString(CultureInfo.InvariantCulture),
                "--reason", S7T6PurgeReason).ConfigureAwait(false);
            Require(preview.ExitCode == 0 && preview.StandardOutput.Contains("PURGE PREVIEW", StringComparison.Ordinal),
                "The owner could not preview the purge of the synthetic source (" + preview.StandardError.Trim() + ").");
            var purgeId = ParseS7T5PurgeId(preview.StandardOutput);
            var impacted = ParseS7T5Impacted(preview.StandardOutput);
            var generation = ParseS7T5Generation(preview.StandardOutput);
            var confirmed = await RunOwnerCommandWithInputAsync(connectionsExecutable, "PURGE" + Environment.NewLine,
                "owner", "purge-confirm", "--control-pipe", fixture.ControlPipe,
                "--purge-id", purgeId, "--resource-id", S7T6SourcePurge, "--resource-revision", sourceRevision.ToString(CultureInfo.InvariantCulture),
                "--target-zone", S7T6Zone, "--target-kind", "memory-or-source",
                "--impacted", string.Join(",", impacted),
                "--live-generation", generation,
                "--reason", S7T6PurgeReason).ConfigureAwait(false);
            Require(confirmed.ExitCode == 0 && confirmed.StandardOutput.Contains("PURGED id=" + purgeId, StringComparison.Ordinal),
                "The owner could not confirm the purge of the synthetic source (" + confirmed.StandardError.Trim() + ").");

            var reEnroll = await EnrollS7T6SourceAsync(connectionsExecutable, fixture, S7T6SourcePurge).ConfigureAwait(false);
            Require(reEnroll.ExitCode != 0 && reEnroll.StandardError.Contains("resource_purged", StringComparison.Ordinal),
                "Re-enrolling a purged source ID was not refused (" + reEnroll.StandardError.Trim() + ").");
            var reIngest = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe, "--source-id", S7T6SourcePurge).ConfigureAwait(false);
            Require(reIngest.ExitCode != 0 && reIngest.StandardError.Contains("resource_purged", StringComparison.Ordinal),
                "Re-ingesting a purged source ID was not refused (" + reIngest.StandardError.Trim() + ").");
            var resurrect = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T6SourcePurge, "--zone-id", S7T6Zone,
                "--input-file", await WriteS7T6ItemDraftAsync(fixture, S7T6ArchivedItem, "S7T6_SYNTHETIC_RESURRECT_TITLE_1f66",
                    S7T6ArchivedBody, null, null, "lesson").ConfigureAwait(false)).ConfigureAwait(false);
            Require(resurrect.ExitCode != 0,
                "A purged resource ID was recreated by an owner write.");
            var rebuilt = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(rebuilt.ExitCode == 0,
                "The owner could not rebuild the index after the purge (" + rebuilt.StandardError.Trim() + ").");
            var page = await OwnerChannelClient.SearchLibraryAsync(fixture.ControlPipe, new OwnerLibraryQuery
            {
                Text = S7T6StaleTitle,
            }, CancellationToken.None).ConfigureAwait(false);
            Require(page.Outcome == OwnerCommandOutcome.Completed && page.Page is not null,
                "The rebuilt library index did not serve the surviving item (" + page.ErrorCode + ").");
            Require(page.Page!.Hits.All(hit => !string.Equals(hit.Id, S7T6SourcePurge, StringComparison.Ordinal)),
                "The rebuilt index served a purged resource again.");

            // The item that cited the purged source now reports it as missing:
            // a missing source is not an unavailable one.
            var queueAfterPurge = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "review-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            var missingEntry = FindS7T6ReviewEntry(queueAfterPurge.StandardOutput, S7T6PurgedCitingItem);
            Require(missingEntry is not null && missingEntry.Contains("cited_source_missing", StringComparison.Ordinal),
                "The review queue did not report the missing source (" + missingEntry ?? "(missing)" + ").");
            Require(missingEntry!.Contains("availability=missing", StringComparison.Ordinal),
                "A deleted source was not reported as missing (" + missingEntry + ").");
            Require(!missingEntry.Contains("cited_source_unavailable", StringComparison.Ordinal),
                "A deleted source was reported as unavailable (" + missingEntry + ").");
            var citingItem = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T6PurgedCitingItem).ConfigureAwait(false);
            Require(citingItem.ExitCode == 0 && citingItem.StandardOutput.Contains("revision=1", StringComparison.Ordinal),
                "Purging a source deleted the item that cited it (" + citingItem.StandardError.Trim() + ").");

            // ---- Restart: due, archive and review states survive ----
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var restartUnlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(restartUnlock.ExitCode == 0,
                "The owner could not unlock the synthetic vault after the restart.");

            var queueAfterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "review-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(queueAfterRestart.ExitCode == 0,
                "The owner could not read the review queue after the restart (" + queueAfterRestart.StandardError.Trim() + ").");
            var restartedEntry = FindS7T6ReviewEntry(queueAfterRestart.StandardOutput, S7T6StaleItem);
            Require(restartedEntry is not null && restartedEntry.Contains("review_after_due", StringComparison.Ordinal),
                "The due review-after deadline did not survive the restart (" + restartedEntry ?? "(missing)" + ").");
            Require(restartedEntry!.Contains("lastReviewed=", StringComparison.Ordinal),
                "The recorded owner review did not survive the restart (" + restartedEntry + ").");
            var restartedUnknown = FindS7T6ReviewEntry(queueAfterRestart.StandardOutput, S7T6UnknownItem);
            Require(restartedUnknown is not null && restartedUnknown.Contains("state=undetermined", StringComparison.Ordinal),
                "The undetermined deadline state did not survive the restart (" + restartedUnknown ?? "(missing)" + ").");
            var archivedAfterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "recall-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T6ArchivedItem).ConfigureAwait(false);
            Require(archivedAfterRestart.ExitCode == 0 && archivedAfterRestart.StandardOutput.Contains("state=archived", StringComparison.Ordinal),
                "The archived state did not survive the restart (" + archivedAfterRestart.StandardOutput.Trim() + ").");
            var intentionAfterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T6IntentionItem).ConfigureAwait(false);
            Require(intentionAfterRestart.ExitCode == 0 &&
                    intentionAfterRestart.StandardOutput.Contains("intention=pending", StringComparison.Ordinal) &&
                    intentionAfterRestart.StandardOutput.Contains("notified=true", StringComparison.Ordinal) &&
                    intentionAfterRestart.StandardOutput.Contains("acknowledged=true", StringComparison.Ordinal),
                "The intention state did not survive the restart (" + intentionAfterRestart.StandardOutput.Trim() + ").");

            // ---- Actual WPF review controls and status ----
            DriveS7T6Wpf(fixture, connectionsExecutable);
            var evidenceDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts", "sprint-7", "evidence");
            Directory.CreateDirectory(evidenceDirectory);
            foreach (var name in new[] { "s7t6-wpf-review-queue.png", "s7t6-wpf-review-recorded.png" })
            {
                var rendered = Path.Combine(fixture.OutputDirectory, name);
                Require(File.Exists(rendered), "The actual shown S7-T6 review evidence was not rendered: " + name);
                File.Copy(rendered, Path.Combine(evidenceDirectory, name), true);
            }

            VerifyS7T6EncryptedState(fixture, passphrase, recoveryCode);
            Console.WriteLine("PASS S7-T6 review-after: due deadline, moved source, unknown and unavailable availability kept distinct, owner review changed nothing, purge survived re-enroll/re-ingest/rebuild, restart kept due/archive/review state, real WPF review controls");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }

            CryptographicOperations.ZeroMemory(passphraseBytes);
        }
    }

    private static long ParseS7T6Revision(string output, string marker)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!trimmed.StartsWith(marker + " id=", StringComparison.Ordinal))
            {
                continue;
            }

            var token = "revision=";
            var start = trimmed.IndexOf(token, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            start += token.Length;
            var end = start;
            while (end < trimmed.Length && char.IsAsciiDigit(trimmed[end]))
            {
                end++;
            }

            if (end > start && long.TryParse(trimmed.AsSpan(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision))
            {
                return revision;
            }
        }

        throw new InvalidOperationException("The " + marker + " line carried no revision: " + output.Trim());
    }

    private static string? FindS7T6ReviewEntry(string output, string resourceId)
    {
        var lines = output.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (!line.StartsWith("REVIEW id=", StringComparison.Ordinal) ||
                !line.Contains("id=" + resourceId + " ", StringComparison.Ordinal))
            {
                continue;
            }

            // The entry line and the citation lines below it belong together.
            var builder = new StringBuilder(line);
            for (var next = index + 1; next < lines.Length; next++)
            {
                var candidate = lines[next].TrimEnd('\r');
                if (!candidate.StartsWith("CITATION ", StringComparison.Ordinal))
                {
                    break;
                }

                builder.Append('\n').Append(candidate);
            }

            return builder.ToString();
        }

        return null;
    }

    private static object S7T6Stamp(
        string precision,
        DateTimeOffset? instantUtc = null,
        int? year = null,
        int? month = null,
        int? day = null,
        int? quarter = null,
        int? zoneOffsetMinutes = null) => new
        {
            precision,
            instantUtc,
            localYear = year,
            localMonth = month,
            localDay = day,
            localQuarter = quarter,
            timeZoneOffsetMinutes = zoneOffsetMinutes
        };

    private static async Task<string> WriteS7T6TempAsync(SmokeFixture fixture, string content, string name)
    {
        var path = Path.Combine(fixture.OutputDirectory, name);
        // No byte-order mark: the owner CLI parses the enrollment request
        // file with a strict JSON document reader.
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false)).ConfigureAwait(false);
        return path;
    }

    private static async Task WriteS7T6SourceAsync(
        string executable,
        SmokeFixture fixture,
        string sourceId,
        string availability,
        string version)
    {
        var draft = JsonSerializer.Serialize(new
        {
            title = "S7T6_SYNTHETIC_SOURCE_TITLE_3b47",
            locator = "s7t6-synthetic-locator-" + sourceId,
            version,
            availability,
            creationMethod = "directWrite",
            capturedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
            recordedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        var result = await RunOwnerCommandAsync(executable,
            "owner", "source-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", sourceId, "--zone-id", S7T6Zone,
            "--input-file", await WriteS7T6TempAsync(fixture, draft, "s7t6-source-" + sourceId + ".json").ConfigureAwait(false))
            .ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("SOURCE stored revision=1", StringComparison.Ordinal),
            "The synthetic S7-T6 source " + sourceId + " was not registered (" + result.StandardError.Trim() + ").");
    }

    private static async Task<CommandResult> WriteS7T6SourceRevisionAsync(
        string executable,
        SmokeFixture fixture,
        string sourceId,
        long expectedRevision,
        string version,
        string availability)
    {
        var draft = JsonSerializer.Serialize(new
        {
            title = "S7T6_SYNTHETIC_SOURCE_TITLE_3b47",
            locator = "s7t6-synthetic-locator-" + sourceId,
            version,
            availability,
            creationMethod = "directWrite",
            capturedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
            recordedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        return await RunOwnerCommandAsync(executable,
            "owner", "source-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", sourceId, "--zone-id", S7T6Zone,
            "--expected-revision", expectedRevision.ToString(CultureInfo.InvariantCulture),
            "--input-file", await WriteS7T6TempAsync(fixture, draft, "s7t6-source-revise-" + sourceId + ".json").ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    private static async Task<string> WriteS7T6ItemDraftAsync(
        SmokeFixture fixture,
        string resourceId,
        string title,
        string body,
        string? sourceId,
        long? sourceRevision,
        string kind)
    {
        var sources = sourceId is null
            ? Array.Empty<object>()
            : new object[] { new { sourceId, sourceRevision } };
        var draft = JsonSerializer.Serialize(new
        {
            kind,
            author = "user",
            creationMethod = "directWrite",
            evidence = "unverified",
            level = "l1",
            title,
            body,
            sources,
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = DateTimeOffset.UtcNow,
            applicability = (object?)null,
        });
        return await WriteS7T6TempAsync(fixture, draft, "s7t6-item-" + resourceId + ".json").ConfigureAwait(false);
    }

    private static async Task WriteS7T6ItemAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string title,
        string body,
        string? sourceId,
        long? sourceRevision,
        string kind)
    {
        var input = await WriteS7T6ItemDraftAsync(fixture, resourceId, title, body, sourceId, sourceRevision, kind).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", S7T6Zone,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
            "The synthetic S7-T6 item " + resourceId + " was not stored (" + result.StandardError.Trim() + ").");
    }

    private static async Task WriteS7T6LifecycleAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        object? reviewAfter)
    {
        var draft = JsonSerializer.Serialize(new
        {
            creationMethod = "directWrite",
            reason = "S7T6 synthetic lifecycle record.",
            occurred = (object?)null,
            recorded = (object?)null,
            validFrom = (object?)null,
            validUntil = (object?)null,
            reviewAfter,
        });
        var result = await RunOwnerCommandAsync(executable,
            "owner", "lifecycle-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId,
            "--input-file", await WriteS7T6TempAsync(fixture, draft, "s7t6-lifecycle-" + resourceId + ".json").ConfigureAwait(false))
            .ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("LIFECYCLE stored revision=1", StringComparison.Ordinal),
            "The synthetic S7-T6 lifecycle record for " + resourceId + " was not stored (" + result.StandardError.Trim() + ").");
    }

    private static async Task<CommandResult> EnrollS7T6SourceAsync(
        string executable,
        SmokeFixture fixture,
        string sourceId)
    {
        var original = Path.Combine(fixture.OutputDirectory, "s7t6-enrolled-" + sourceId + ".jsonl");
        await File.WriteAllTextAsync(original,
            "{\"type\":\"message\",\"role\":\"user\",\"content\":\"S7T6_SYNTHETIC_ENROLLED_LINE_4c19\"}\n",
            new UTF8Encoding(false)).ConfigureAwait(false);
        var request = JsonSerializer.Serialize(new { locator = original });
        var requestPath = await WriteS7T6TempAsync(fixture, request, "s7t6-enroll-request-" + sourceId + ".json").ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
            "--source-id", sourceId, "--zone-id", S7T6Zone,
            "--format", "codex.session.jsonl", "--format-version", "1",
            "--input-file", requestPath).ConfigureAwait(false);
    }

    private static void VerifyS7T6EncryptedState(SmokeFixture fixture, string passphrase, string recoveryCode)
    {
        var sensitive = new[]
        {
            S7T6StaleTitle,
            S7T6StaleBody,
            S7T6UnknownBody,
            S7T6IntentionBody,
            S7T6ArchivedBody,
            S7T6ReviewReason,
            S7T6PurgeReason,
            passphrase,
            recoveryCode
        };
        foreach (var path in Directory.EnumerateFiles(fixture.VaultDirectoryPath, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(path);
            try
            {
                var content = Encoding.UTF8.GetString(bytes);
                foreach (var value in sensitive)
                {
                    Require(!content.Contains(value, StringComparison.Ordinal),
                        "Synthetic S7-T6 content appeared in plaintext in an encrypted vault file: " + Path.GetFileName(path));
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        var reviewState = Path.Combine(fixture.VaultDirectoryPath, "review-state.enc");
        Require(File.Exists(reviewState), "The durable encrypted owner review state was not written.");
        Require(!Directory.EnumerateFiles(fixture.VaultDirectoryPath, "*.key", SearchOption.AllDirectories).Any(),
            "The synthetic vault persisted a key file beside its encrypted state.");
    }

    private static void DriveS7T6Wpf(SmokeFixture fixture, string connectionsExecutable)
    {
        var session = new OwnerConsoleSession(fixture.ControlPipe);
        session.AttachAsync().GetAwaiter().GetResult();
        Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
            "The actual S7-T6 owner session did not attach to the unlocked service.");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            OwnerConsoleWindow? window = null;
            try
            {
                var activeWindow = new OwnerConsoleWindow(session)
                {
                    Width = 1360,
                    Height = 1400
                };
                window = activeWindow;
                activeWindow.Show();
                activeWindow.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await S7T6WpfInteractionsAsync(activeWindow, session, fixture, connectionsExecutable).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                    finally
                    {
                        await WaitForAsync(() => !session.ReviewActInFlight, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
                        await Task.Delay(200).ConfigureAwait(true);
                        activeWindow.DetachView();
                        activeWindow.Close();
                        activeWindow.Dispatcher.InvokeShutdown();
                    }
                });
                System.Windows.Threading.Dispatcher.Run();
            }
            catch (Exception ex)
            {
                failure ??= ex;
                try
                {
                    window?.DetachView();
                    window?.Close();
                }
                catch
                {
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("The shown S7-T6 WPF review surface failed: " + failure, failure);
        }
    }

    private static async Task S7T6WpfInteractionsAsync(
        OwnerConsoleWindow window,
        OwnerConsoleSession session,
        SmokeFixture fixture,
        string connectionsExecutable)
    {
        SelectS7T2Navigation(window, "Review queue");
        Require(await WaitForAsync(() => session.ReviewQueue.Length > 0, TimeSpan.FromSeconds(60)).ConfigureAwait(true),
            "The actual WPF review queue did not show the entries the service reported.");
        Require(window.ReviewQueueList.Items.Count > 0,
            "The actual WPF review queue list did not render the reported entries.");
        var row = window.ReviewQueueList.Items.OfType<OwnerConsoleWindow.OwnerReviewListRow>()
            .FirstOrDefault(item => string.Equals(item.Review.ResourceId, S7T6StaleItem, StringComparison.Ordinal));
        Require(row is not null,
            "The actual WPF review queue did not render the item whose cited source moved.");
        window.ReviewQueueList.SelectedItem = row;
        window.ReviewQueueList.ScrollIntoView(row);
        await WaitForAsync(() => true, TimeSpan.FromMilliseconds(50)).ConfigureAwait(true);
        Require(window.ReviewQueueDetailText.Text.Contains("review_after_due", StringComparison.Ordinal) &&
                window.ReviewQueueDetailText.Text.Contains("cited_source_revision_changed", StringComparison.Ordinal),
            "The actual WPF review detail did not show the reasons the service computed (" + window.ReviewQueueDetailText.Text + ").");
        Require(window.ReviewQueueCitationsText.Text.Contains("source=" + S7T6SourceA, StringComparison.Ordinal) &&
                window.ReviewQueueCitationsText.Text.Contains("cited=1", StringComparison.Ordinal) &&
                window.ReviewQueueCitationsText.Text.Contains("live=", StringComparison.Ordinal) &&
                window.ReviewQueueCitationsText.Text.Contains("availability=unavailable", StringComparison.Ordinal),
            "The actual WPF review detail did not show the exact cited and live source revisions (" + window.ReviewQueueCitationsText.Text + ").");
        Require(window.ReviewQueuePanel.Visibility == Visibility.Visible,
            "The actual WPF console did not show the review queue view.");
        window.ScrollToReviewQueueTop();
        SaveS7T2Wpf(window, fixture, "s7t6-wpf-review-queue.png", window.ReviewQueueList);

        window.ReviewQueueRationaleInput.Text = S7T6ReviewReason;
        Require(window.ReviewQueueRecordButton.IsEnabled,
            "The actual WPF console did not enable the review control for the selected item.");
        window.ReviewQueueRecordButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.ReviewMessage is not null &&
                session.ReviewMessage.Contains("recorded your review", StringComparison.Ordinal), TimeSpan.FromSeconds(60)).ConfigureAwait(true),
            "The actual WPF review control did not record the owner's review.");
        Require(session.ReviewQueue.Any(entry => entry.ResourceId == S7T6StaleItem &&
                entry.LastReviewedAtUtc is not null && entry.PendingReasons.Length == 0),
            "The actual WPF review control did not persist the review with the service.");
        SaveS7T2Wpf(window, fixture, "s7t6-wpf-review-recorded.png", window.ReviewQueueList);

        // A lock clears every private view of this console, including the
        // review queue and the review the owner just recorded on screen.
        await session.LockAsync().ConfigureAwait(true);
        Require(session.ReportedVaultState == OwnerVaultState.Locked &&
                session.ReviewQueue.Length == 0 &&
                window.ReviewQueueList.Items.Count == 0 &&
                string.IsNullOrEmpty(window.ReviewQueueRationaleInput.Text),
            "Lock did not clear the actual WPF review queue view and its private input.");
        Console.WriteLine("PASS actual WPF review queue shows the service's reasons and source revisions, records the owner's review, and clears every private view on lock");
    }
}
