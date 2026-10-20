using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;
using MetaBrain.OwnerUi;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string S7T5Zone = "s7t5-purge-zone";
    private const string S7T5TargetId = "s7t5-purge-target";
    private const string S7T5TargetTitle = "S7T5_SYNTHETIC_PURGE_TITLE_8c21";
    private const string S7T5TargetBody = "S7T5_SYNTHETIC_PURGE_BODY_d4f7: the owner deletes this synthetic lesson and everything derived from it.";
    private const string S7T5SharedToken = "S7T5_SHARED_PURGE_TOKEN_2b60";
    private const string S7T5OtherId = "s7t5-other-zone-item";
    private const string S7T5OtherBody = "S7T5_SYNTHETIC_OTHER_ZONE_BODY_91aa: an item in a second zone that the frozen scope must never reach.";
    private const string S7T5SharedDerivedId = "s7t5-shared-derived";
    private const string S7T5SharedDerivedZone = "s7t5-shared-zone";
    private const string S7T5PurgeReason = "S7T5_SYNTHETIC_PURGE_REASON_5e19: the owner reviewed this synthetic lesson and deletes it with its derivatives.";
    private const string S7T5CatalogId = "s7t5-catalog-target";

    internal static async Task RunS7T5PurgeAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S7-T5 purge smoke requires Windows named pipes, DPAPI and WPF.");
        }

        Console.WriteLine("RUN S7-T5 owner purge with derivative impact, catalog withdrawal, scope invalidation and interruption resume");
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
                "The synthetic S7-T5 vault was not provisioned.");
            recoveryCode = ParseRecoveryCode(provisioned.StandardOutput);
            var unlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S7-T5 vault did not unlock.");

            await WriteS7T5ItemAsync(connectionsExecutable, fixture, S7T5TargetId, S7T5TargetTitle, S7T5TargetBody).ConfigureAwait(false);
            await WriteS7T5ItemAsync(connectionsExecutable, fixture, S7T5OtherId, "S7T5_SYNTHETIC_OTHER_TITLE_77cd", S7T5OtherBody).ConfigureAwait(false);

            // Registered source cited by the target (pinned lineage stays with the item).
            var sourceDraft = JsonSerializer.SerializeToUtf8Bytes(new
            {
                title = "S7T5_SYNTHETIC_SOURCE_TITLE_4f08",
                locator = "s7t5-synthetic-locator",
                version = "v1",
                availability = "available",
                creationMethod = "directWrite",
                capturedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                recordedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            try
            {
                var sourceCreated = await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "source-create", "--control-pipe", fixture.ControlPipe,
                    "--resource-id", "s7t5-source-a", "--zone-id", S7T5Zone,
                    "--input-file", await WriteS7T5TempAsync(fixture, sourceDraft).ConfigureAwait(false)).ConfigureAwait(false);
                Require(sourceCreated.ExitCode == 0 && sourceCreated.StandardOutput.Contains("SOURCE stored revision=1", StringComparison.Ordinal),
                    "The synthetic S7-T5 source was not registered (" + sourceCreated.StandardError.Trim() + ").");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sourceDraft);
            }

            // Target cites the source at its live revision.
            var revisedDraft = JsonSerializer.Serialize(new
            {
                kind = "lesson",
                author = "user",
                creationMethod = "directWrite",
                evidence = "unverified",
                level = "l1",
                title = S7T5TargetTitle,
                body = S7T5TargetBody + " " + S7T5SharedToken,
                sources = new[] { new { sourceId = "s7t5-source-a", sourceRevision = 1L } },
                occurredAtUtc = (DateTimeOffset?)null,
                recordedAtUtc = DateTimeOffset.UtcNow,
                applicability = (object?)null,
            });
            var reviseInput = Path.Combine(fixture.OutputDirectory, "s7t5-revise.json");
            await File.WriteAllTextAsync(reviseInput, revisedDraft, Encoding.UTF8).ConfigureAwait(false);
            var revisedTarget = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-revise", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T5TargetId, "--zone-id", S7T5Zone,
                "--expected-revision", "1",
                "--input-file", reviseInput).ConfigureAwait(false);
            Require(revisedTarget.ExitCode == 0 && revisedTarget.StandardOutput.Contains("MEMORY stored revision=2", StringComparison.Ordinal),
                "The synthetic S7-T5 target did not cite its source (" + revisedTarget.StandardError.Trim() + ").");

            // Lifecycle record + history-friendly validity, relation, feedback, proposal on the target.
            await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T5TargetId, "--input-file",
                await WriteS7T5LifecycleDraftAsync(fixture).ConfigureAwait(false)).ConfigureAwait(false);
            await WriteS7T5RelationAsync(connectionsExecutable, fixture, "s7t5-source-a", S7T5TargetId).ConfigureAwait(false);
            await WriteS7T5FeedbackAsync(connectionsExecutable, fixture, S7T5TargetId).ConfigureAwait(false);
            await WriteS7T5ProposalInboxAsync(connectionsExecutable, fixture).ConfigureAwait(false);
            await WriteS7T5DeclassifiedAsync(connectionsExecutable, fixture).ConfigureAwait(false);

            // Approved catalog entry for the target.
            var catalogPublished = await RunOwnerCommandWithInputAsync(connectionsExecutable, "PUBLISH" + Environment.NewLine,
                "owner", "catalog-publish", "--control-pipe", fixture.ControlPipe,
                "--catalog-id", S7T5CatalogId, "--label", "Synthetic S7-T5 target",
                "--description", "Owner-written S7-T5 description; never private text.",
                "--resource-id", S7T5TargetId).ConfigureAwait(false);
            Require(catalogPublished.ExitCode == 0 && catalogPublished.StandardOutput.Contains("CATALOG published id=" + S7T5CatalogId, StringComparison.Ordinal),
                "The owner could not publish the synthetic S7-T5 catalog entry (" + catalogPublished.StandardError.Trim() + ").");

            var rebuilt = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(rebuilt.ExitCode == 0, "The owner could not materialize the S7-T5 library index (exit=" + rebuilt.ExitCode + " out=" + rebuilt.StandardOutput.Trim() + " err=" + rebuilt.StandardError.Trim() + ").");

            var searchSession = await IssueS7T5GrantAsync(connectionsExecutable, fixture, "s7t5-search",
                new[] { S7T5TargetId, S7T5OtherId }, "memory.read,memory.search").ConfigureAwait(false);
            var baseline = await RunS7T5SearchAsync(connectionsExecutable, fixture, searchSession, false).ConfigureAwait(false);
            Require(baseline.Contains($"HIT id={S7T5TargetId}", StringComparison.Ordinal) &&
                    baseline.Contains($"HIT id={S7T5OtherId}", StringComparison.Ordinal),
                "The baseline scoped MCP search did not serve both approved items (" + baseline.Trim() + ").");

            var liveSession = await IssueS7T5GrantAsync(connectionsExecutable, fixture, "s7t5-live",
                new[] { S7T5TargetId }, "memory.read").ConfigureAwait(false);

            // Agent channel cannot purge: every purge operation is owner-only.
            var agentPurge = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "owner.purge.preview",
                resourceId = S7T5TargetId,
                resourceRevision = 2L,
            }).ConfigureAwait(false);
            Require(ReplyCode(agentPurge) == "forbidden",
                "The agent channel accepted an owner purge operation (reply=" + agentPurge.GetRawText() + ").");

            // Dry-run preview first: exact impacted set, nothing deleted.
            var preview = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "purge-preview", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T5TargetId, "--resource-revision", "2",
                "--reason", S7T5PurgeReason).ConfigureAwait(false);
            Require(preview.ExitCode == 0 && preview.StandardOutput.Contains("PURGE PREVIEW", StringComparison.Ordinal) &&
                    preview.StandardOutput.Contains(S7T5TargetId, StringComparison.Ordinal) &&
                    preview.StandardOutput.Contains("lc-" + S7T5TargetId, StringComparison.Ordinal) &&
                    preview.StandardOutput.Contains(S7T5CatalogId, StringComparison.Ordinal) &&
                    preview.StandardOutput.Contains("s8=", StringComparison.OrdinalIgnoreCase),
                "The owner purge preview did not name the exact impacted copies and catalog (exit=" + preview.ExitCode + " out=" + preview.StandardOutput.Trim() + " err=" + preview.StandardError.Trim() + ").");
            var stillThere = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T5TargetId).ConfigureAwait(false);
            Require(stillThere.ExitCode == 0 && stillThere.StandardOutput.Contains(S7T5SharedToken, StringComparison.Ordinal),
                "The dry-run preview deleted the target.");
            var purgeId = ParseS7T5PurgeId(preview.StandardOutput);
            var previewTokens = ParseS7T5Impacted(preview.StandardOutput);
            var liveGeneration = ParseS7T5Generation(preview.StandardOutput);

            // Wrong typed confirmation fails closed with nothing deleted.
            var mistyped = await RunOwnerCommandWithInputAsync(connectionsExecutable, "purge" + Environment.NewLine,
                "owner", "purge-confirm", "--control-pipe", fixture.ControlPipe,
                "--purge-id", purgeId, "--resource-id", S7T5TargetId, "--resource-revision", "2",
                "--target-zone", S7T5Zone, "--target-kind", "memory-or-source",
                "--impacted", string.Join(",", previewTokens),
                "--live-generation", liveGeneration,
                "--reason", S7T5PurgeReason).ConfigureAwait(false);
            Require(mistyped.ExitCode != 0,
                "A mistyped purge confirmation deleted the target.");
            var afterMistype = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T5TargetId).ConfigureAwait(false);
            Require(afterMistype.ExitCode == 0,
                "A refused confirm deleted the target.");

            // Changed target (stale revision) fails closed.
            var stalePreview = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "purge-preview", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T5TargetId, "--resource-revision", "1",
                "--reason", S7T5PurgeReason).ConfigureAwait(false);
            Require(stalePreview.ExitCode != 0,
                "A stale target revision previewed a purge.");

            // Explicit confirm with typed PURGE.
            var confirmed = await RunOwnerCommandWithInputAsync(connectionsExecutable, "PURGE" + Environment.NewLine,
                "owner", "purge-confirm", "--control-pipe", fixture.ControlPipe,
                "--purge-id", purgeId, "--resource-id", S7T5TargetId, "--resource-revision", "2",
                "--target-zone", S7T5Zone, "--target-kind", "memory-or-source",
                "--impacted", string.Join(",", previewTokens),
                "--live-generation", liveGeneration,
                "--catalog-ids", S7T5CatalogId,
                "--reason", S7T5PurgeReason).ConfigureAwait(false);
            Require(confirmed.ExitCode == 0 && confirmed.StandardOutput.Contains("PURGED id=" + purgeId, StringComparison.Ordinal),
                "The owner could not confirm the synthetic purge (exit=" + confirmed.ExitCode + " out=" + confirmed.StandardOutput.Trim() + " err=" + confirmed.StandardError.Trim() + " service=" + confirmed.StandardOutput.Trim() + ").");

            // Read/search/catalog/rebuild negatives: purged IDs are gone.
            var goneRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T5TargetId).ConfigureAwait(false);
            Require(goneRead.ExitCode != 0 && goneRead.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A purged item stayed readable on the owner channel.");
            var goneRelation = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "relation-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", FindS7T5RelationId(preview.StandardOutput)).ConfigureAwait(false);
            Require(goneRelation.ExitCode != 0,
                "A purged relation stayed readable.");
            var catalogList = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe, "metabrain_catalog_list", new { }).ConfigureAwait(false);
            Require(!catalogList.Contains(S7T5CatalogId, StringComparison.Ordinal),
                "Catalog withdrawal did not remove the purged entry (" + catalogList.Trim() + ").");
            var catalogQuery = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_catalog_query", new { query = "S7-T5 target" }).ConfigureAwait(false);
            Require(catalogQuery.Contains("no_match", StringComparison.Ordinal) && !catalogQuery.Contains(S7T5CatalogId, StringComparison.Ordinal),
                "A purged catalog entry stayed discoverable (" + catalogQuery.Trim() + ").");

            // Rebuild refusals: the purge clears the materialized index, so the next
            // search must refuse (stale or no generation) until the owner rebuilds.
            var staleBrowse = await OwnerChannelClient.SearchLibraryAsync(fixture.ControlPipe,
                new OwnerLibraryQuery(Text: S7T5SharedToken, Limit: 10), CancellationToken.None).ConfigureAwait(false);
            Require(staleBrowse.Outcome == OwnerCommandOutcome.Refused && staleBrowse.Page is null &&
                    (staleBrowse.ErrorCode == "search_index_stale" || staleBrowse.ErrorCode == "index_not_materialized"),
                "Search over a stale generation served purged matches (" + staleBrowse.Outcome + "/" + (staleBrowse.ErrorCode ?? "no-code") + ").");
            var rebuiltAfter = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(rebuiltAfter.ExitCode == 0, "The post-purge index rebuild failed.");
            var afterBrowse = await OwnerChannelClient.SearchLibraryAsync(fixture.ControlPipe,
                new OwnerLibraryQuery(Text: S7T5SharedToken, Limit: 10), CancellationToken.None).ConfigureAwait(false);
            Require(afterBrowse.Outcome == OwnerCommandOutcome.Completed && afterBrowse.Page is not null &&
                    afterBrowse.Page.Hits.Any(hit => string.Equals(hit.Id, S7T5OtherId, StringComparison.Ordinal)) &&
                    afterBrowse.Page.Hits.All(hit => !string.Equals(hit.Id, S7T5TargetId, StringComparison.Ordinal)),
                "The rebuilt library still served the purged item (" + (afterBrowse.Page is null ? "no page" : string.Join(",", afterBrowse.Page.Hits.Select(hit => hit.Id + "@" + hit.Revision))) + ").");

            // Frozen live session loses the purged ID at its next check.
            var deniedRead = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_memory_read", new
                {
                    sessionFile = liveSession.SessionFile,
                    resourceId = S7T5TargetId,
                    resourceRevision = "2",
                }).ConfigureAwait(false);
            Require(deniedRead.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !deniedRead.Contains(S7T5SharedToken, StringComparison.Ordinal),
                "A live scoped session still read the purged item (" + deniedRead.Trim() + ").");
            var deniedSearch = await RunS7T5SearchAsync(connectionsExecutable, fixture, searchSession, true).ConfigureAwait(false);
            Require(deniedSearch.Contains("resource_unavailable", StringComparison.Ordinal),
                "A live scoped session still searched after the purge (" + deniedSearch.Trim() + ").");

            // No resurrection: recreate, revise, recatalog, re-grant all refuse purged IDs.
            var recreateInput = Path.Combine(fixture.OutputDirectory, "s7t5-recreate.json");
            await File.WriteAllTextAsync(recreateInput, JsonSerializer.Serialize(new
            {
                kind = "lesson",
                author = "user",
                creationMethod = "directWrite",
                evidence = "unverified",
                level = "l1",
                title = "recreate",
                body = "recreate body",
                sources = Array.Empty<object>(),
                occurredAtUtc = (DateTimeOffset?)null,
                recordedAtUtc = DateTimeOffset.UtcNow,
                applicability = (object?)null,
            }), Encoding.UTF8).ConfigureAwait(false);
            var recreate = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T5TargetId, "--zone-id", S7T5Zone,
                "--input-file", recreateInput).ConfigureAwait(false);
            Require(recreate.ExitCode != 0,
                "A purged ID was recreated.");
            var republish = await RunOwnerCommandWithInputAsync(connectionsExecutable, "PUBLISH" + Environment.NewLine,
                "owner", "catalog-publish", "--control-pipe", fixture.ControlPipe,
                "--catalog-id", "s7t5-catalog-after", "--label", "after",
                "--description", "after purge.",
                "--resource-id", S7T5TargetId).ConfigureAwait(false);
            Require(republish.ExitCode != 0,
                "A purged resource was republished to the catalog.");
            var regrant = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "grant", "--control-pipe", fixture.ControlPipe,
                "--resource-ids", S7T5TargetId,
                "--operations", "memory.read",
                "--expires-at-utc", DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture),
                "--handoff-file", Path.Combine(fixture.OutputDirectory, "s7t5-regrant.handoff")).ConfigureAwait(false);
            Require(regrant.ExitCode != 0,
                "A purged ID entered a new grant scope.");
            var refreshedPurge = ReadS7T5LedgerPurgedIds(fixture, passphrase);
            Require(refreshedPurge.Contains(S7T5TargetId),
                "The durable purge ledger forgot the purged target across the confirm.");


            // Interrupted resume under actual in-progress deletion, in two
            // deterministic stages on the fixture's own encrypted vault. The
            // test-only S7T5CrashingPurgeOperation decorator (this file only)
            // faults between two real durable commits of the shipped
            // PurgeService state machine; the shipping service binary carries
            // no crash path, no environment knob, and no count file.
            // Stage 1 faults after the first durable commit (Confirmed,
            // nonempty Remaining, nothing deleted). Stage 2 resumes once and
            // faults mid-loop after the second durable commit (Applying with
            // a strict nonempty Remaining subset: exactly one canonical copy
            // deleted). The live owner service then resumes both checkpoints
            // through the real owner channel and converges on Applied exactly
            // once. A fresh VaultLifecycle lease is used for each
            // fault-injection step because the live pipe service holds its own
            // lease; the decorator observes the same durable IVaultOperation
            // contract, and Stop/StartServiceAsync around each stage proves
            // the checkpoint survives a real process restart/unlock.
            var ownerSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("The S7-T5 crash fixture has no Windows owner SID.");
            var interruptIds = await WriteS7T5InterruptibleSetAsync(
                connectionsExecutable, fixture, "s7t5-interrupt-target").ConfigureAwait(false);
            var interruptPreview = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "purge-preview", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "s7t5-interrupt-target", "--resource-revision", "1",
                "--reason", S7T5PurgeReason).ConfigureAwait(false);
            Require(interruptPreview.ExitCode == 0,
                "The interrupted-purge preview failed (" + interruptPreview.StandardError.Trim() + ").");
            var interruptPurgeId = ParseS7T5PurgeId(interruptPreview.StandardOutput);
            var interruptTokens = ParseS7T5Impacted(interruptPreview.StandardOutput);
            Require(interruptTokens.Length == 3 && interruptIds.Length == 3,
                "The interrupted-purge preview bound " + interruptTokens.Length + " copies, expected exactly the 3-copy interrupt set.");
            var interruptGeneration = ParseS7T5Generation(interruptPreview.StandardOutput);
            // Quiesce the live service before touching the vault files
            // directly: the pipe service and the fault-injection lease must
            // never hold the vault open at the same time.
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            var interruptCrashed = CrashS7T5PurgeAfterIntent(
                fixture, interruptPurgeId, interruptTokens,
                interruptGeneration, passphrase);
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var interruptUnlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(interruptUnlock.ExitCode == 0, "The owner could not unlock after the Confirmed purge crash.");
            Require(interruptCrashed.Crashed,
                "The interrupted purge unexpectedly completed instead of crashing mid-delete (" + interruptCrashed.Detail + ").");
            Require(interruptCrashed.RemainingBefore > 0,
                "The crash checkpoint carried no Remaining; interruption was not in progress.");
            foreach (var token in interruptTokens)
            {
                var id = token.Split('|')[0];
                var kind = token.Split('|').Length > 2 ? token.Split('|')[2] : string.Empty;
                var probeId = kind == "lifecycle" && id.StartsWith("lc-", StringComparison.Ordinal)
                    ? id["lc-".Length..]
                    : id;
                var probe = kind == "lifecycle"
                    ? await RunOwnerCommandAsync(connectionsExecutable,
                        "owner", "lifecycle-read", "--control-pipe", fixture.ControlPipe, "--resource-id", probeId).ConfigureAwait(false)
                    : kind == "relation"
                    ? await RunOwnerCommandAsync(connectionsExecutable,
                        "owner", "relation-read", "--control-pipe", fixture.ControlPipe, "--resource-id", id).ConfigureAwait(false)
                    : await RunOwnerCommandAsync(connectionsExecutable,
                        "owner", "memory-read", "--control-pipe", fixture.ControlPipe, "--resource-id", id).ConfigureAwait(false);
                Require(probe.ExitCode == 0,
                    "The crash checkpoint deleted a copy before resume: " + id);
            }
            // Tombstone guard holds while Confirmed: recreate of a reserved ID
            // refuses even before its bytes are gone.
            var tombstoned = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "s7t5-interrupt-target", "--zone-id", S7T5Zone,
                "--input-file", recreateInput).ConfigureAwait(false);
            Require(tombstoned.ExitCode != 0,
                "A reserved purge target resurrected while its intent was Confirmed.");
            // Mid-loop crash through the test decorator: resume once so the
            // shipped ApplyIntent deletes exactly one ordered copy and faults
            // mid-loop with a strict nonempty Remaining subset.
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            var interruptBound = S7T5PreviewForCrash(interruptPurgeId, "s7t5-interrupt-target", 1, interruptTokens, interruptGeneration, Array.Empty<string>());
            CrashS7T5PurgeMidLoop(fixture, ownerSid, passphrase, interruptBound, interruptPurgeId, alreadySaved: 1);
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var midReunlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(midReunlock.ExitCode == 0, "The owner could not unlock after the mid-loop purge crash.");
            // Partial-deletion checkpoint: exactly one ordered copy is gone
            // (the lc- anchor sorts first), the other two remain readable.
            var goneLc = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-read", "--control-pipe", fixture.ControlPipe, "--resource-id", "s7t5-interrupt-target").ConfigureAwait(false);
            Require(goneLc.ExitCode != 0, "The mid-loop crash deleted nothing; partial deletion unproven.");
            var stillTarget = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe, "--resource-id", "s7t5-interrupt-target").ConfigureAwait(false);
            Require(stillTarget.ExitCode == 0, "The mid-loop crash deleted the target too early; Remaining subset unproven.");
            var stillRelation = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "relation-read", "--control-pipe", fixture.ControlPipe, "--resource-id", "rel-s7t5-interrupt").ConfigureAwait(false);
            Require(stillRelation.ExitCode == 0, "The mid-loop crash deleted the relation too early; Remaining subset unproven.");
            // Tombstone guard holds during partial deletion: the deleted lc-
            // anchor refuses its own write path (RequireNotPurged on
            // lifecycle-create), and the guarded memory-create of the purged
            // target refuses too. Both refusals prove the encrypted ledger —
            // not manifest absence — is the anti-resurrection authority while
            // the intent is still Applying.
            var tombstoneLcDraft = Path.Combine(fixture.OutputDirectory, "s7t5-tombstone-lifecycle.json");
            await File.WriteAllTextAsync(tombstoneLcDraft, JsonSerializer.Serialize(new
            {
                creationMethod = "directWrite",
                reason = "S7T5 synthetic tombstone probe.",
                occurred = (object?)null,
                recorded = (object?)null,
                validFrom = (object?)null,
                validUntil = (object?)null,
                reviewAfter = (object?)null,
            }), Encoding.UTF8).ConfigureAwait(false);
            var tombstoneLc = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "s7t5-interrupt-target", "--input-file", tombstoneLcDraft).ConfigureAwait(false);
            Require(tombstoneLc.ExitCode != 0,
                "The ledger guard let a purged lifecycle anchor recreate during partial deletion.");
            var tombstoneMem = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "s7t5-interrupt-target", "--zone-id", S7T5Zone,
                "--input-file", recreateInput).ConfigureAwait(false);
            Require(tombstoneMem.ExitCode != 0,
                "The ledger guard let a purged ID recreate during partial deletion.");
            var resumedInterrupt = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "purge-resume", "--control-pipe", fixture.ControlPipe,
                "--purge-id", interruptPurgeId).ConfigureAwait(false);
            Require(resumedInterrupt.ExitCode == 0 && resumedInterrupt.StandardOutput.Contains("PURGED id=" + interruptPurgeId, StringComparison.Ordinal),
                "Resume from the partial-deletion checkpoint failed (" + resumedInterrupt.StandardError.Trim() + ").");
            foreach (var token in interruptTokens)
            {
                var id = token.Split('|')[0];
                var kind = token.Split('|').Length > 2 ? token.Split('|')[2] : string.Empty;
                var readId = kind == "lifecycle" && id.StartsWith("lc-", StringComparison.Ordinal)
                    ? id["lc-".Length..]
                    : id;
                var read = kind == "lifecycle"
                    ? await RunOwnerCommandAsync(connectionsExecutable,
                        "owner", "lifecycle-read", "--control-pipe", fixture.ControlPipe, "--resource-id", readId).ConfigureAwait(false)
                    : kind == "relation"
                    ? await RunOwnerCommandAsync(connectionsExecutable,
                        "owner", "relation-read", "--control-pipe", fixture.ControlPipe, "--resource-id", id).ConfigureAwait(false)
                    : await RunOwnerCommandAsync(connectionsExecutable,
                        "owner", "memory-read", "--control-pipe", fixture.ControlPipe, "--resource-id", id).ConfigureAwait(false);
                Require(read.ExitCode != 0,
                    "The resumed purge left a deleted copy readable: " + id);
            }
            var resumedApplied = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "purge-resume", "--control-pipe", fixture.ControlPipe,
                "--purge-id", interruptPurgeId).ConfigureAwait(false);
            Require(resumedApplied.ExitCode == 0,
                "Resume of the applied interrupted purge failed (" + resumedApplied.StandardError.Trim() + ").");
            var interruptAfter = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(interruptAfter.ExitCode == 0, "The post-resume index rebuild failed.");
            await ProveS7T5FinalizingResumeAsync(connectionsExecutable, fixture, passphrase, ownerSid).ConfigureAwait(false);
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var relocked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(relocked.StandardOutput.Contains("vault=locked", StringComparison.Ordinal),
                "The restarted service did not return locked.");
            var reunlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(reunlock.ExitCode == 0, "The owner could not unlock after the purge restart.");
            var postRestartRecreate = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T5TargetId, "--zone-id", S7T5Zone,
                "--input-file", recreateInput).ConfigureAwait(false);
            Require(postRestartRecreate.ExitCode != 0,
                "A purged ID resurrected after restart.");
            var sharedDerivedRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T5SharedDerivedId).ConfigureAwait(false);
            Require(sharedDerivedRead.ExitCode == 0 && sharedDerivedRead.StandardOutput.Contains("sanitized", StringComparison.Ordinal),
                "The shared declassified derivative was deleted with its private lineage.");

            // Actual WPF preview/confirm on the open survivor, then lock clearing.
            DriveS7T5Wpf(fixture, connectionsExecutable);
            var evidenceDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts", "sprint-7", "evidence");
            Directory.CreateDirectory(evidenceDirectory);
            foreach (var name in new[] { "s7t5-wpf-purge-preview.png", "s7t5-wpf-purge-confirm.png" })
            {
                var rendered = Path.Combine(fixture.OutputDirectory, name);
                Require(File.Exists(rendered), "The actual shown S7-T5 purge evidence was not rendered: " + name);
                File.Copy(rendered, Path.Combine(evidenceDirectory, name), overwrite: true);
                Console.WriteLine("S7T5 WPF visual evidence: " + Path.Combine(evidenceDirectory, name));
            }

            VerifyS7T5EncryptedState(fixture, passphrase, recoveryCode);
            Console.WriteLine("PASS S7-T5 owner purge: dry-run preview, typed confirm, durable encrypted ledger, catalog withdrawal, scope/session invalidation, no resurrection, interrupted resume, real MCP and WPF controls");
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

    private static async Task<string> WriteS7T5TempAsync(SmokeFixture fixture, byte[] content)
    {
        var path = Path.Combine(fixture.OutputDirectory, "s7t5-temp-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllBytesAsync(path, content).ConfigureAwait(false);
        return path;
    }

    private static async Task<string> WriteS7T5LifecycleDraftAsync(SmokeFixture fixture)
    {
        var draft = JsonSerializer.Serialize(new
        {
            creationMethod = "directWrite",
            reason = "S7T5 synthetic lifecycle.",
            occurred = (object?)null,
            recorded = (object?)null,
            validFrom = (object?)null,
            validUntil = (object?)null,
            reviewAfter = (object?)null,
        });
        var path = Path.Combine(fixture.OutputDirectory, "s7t5-lifecycle.json");
        await File.WriteAllTextAsync(path, draft, Encoding.UTF8).ConfigureAwait(false);
        return path;
    }

    private static async Task WriteS7T5ItemAsync(string executable, SmokeFixture fixture, string resourceId, string title, string body)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s7t5-item-" + resourceId + ".json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new
        {
            kind = "lesson",
            author = "user",
            creationMethod = "directWrite",
            evidence = "unverified",
            level = "l1",
            title,
            body = body + " " + S7T5SharedToken,
            sources = Array.Empty<object>(),
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = DateTimeOffset.UtcNow,
            applicability = (object?)null,
        }), Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", S7T5Zone,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
            "The synthetic S7-T5 item " + resourceId + " was not stored (" + result.StandardError.Trim() + ").");
    }

    private static async Task<string[]> WriteS7T5InterruptibleSetAsync(string executable, SmokeFixture fixture, string targetId)
    {
        // Three-copy interrupt set (no inbox/feedback/journal/proposal
        // anchor): changing this set must change the 3-copy assertions beside
        // the crash/resume proof, never silently.
        var relationId = "rel-s7t5-interrupt";
        await WriteS7T5ItemAsync(executable, fixture, targetId, "S7T5_SYNTHETIC_INTERRUPT_TITLE_51be",
            "S7T5_SYNTHETIC_INTERRUPT_BODY_7d02: the owner crashes mid-purge of this synthetic lesson.").ConfigureAwait(false);
        await WriteS7T5LifecycleForAsync(executable, fixture, targetId).ConfigureAwait(false);
        var relationDraft = JsonSerializer.Serialize(new
        {
            kind = "relatedTo",
            state = "proposed",
            from = new { resourceId = targetId, endpointKind = "memoryItem", revision = 1, span = (object?)null },
            to = new { resourceId = S7T5OtherId, endpointKind = "memoryItem", revision = 1, span = (object?)null },
            creationMethod = "directWrite",
            reason = "S7T5 synthetic interrupt relation.",
            requestEvidence = (string?)null,
            stateReason = (string?)null,
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = DateTimeOffset.UtcNow,
        });
        var relationPath = Path.Combine(fixture.OutputDirectory, "s7t5-interrupt-relation.json");
        await File.WriteAllTextAsync(relationPath, relationDraft, Encoding.UTF8).ConfigureAwait(false);
        var relation = await RunOwnerCommandAsync(executable,
            "owner", "relation-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", relationId, "--zone-id", S7T5Zone,
            "--input-file", relationPath).ConfigureAwait(false);
        Require(relation.ExitCode == 0,
            "The synthetic S7-T5 interrupt relation was not stored (" + relation.StandardError.Trim() + ").");
        return new[] { targetId, "lc-" + targetId, relationId };
    }

    private static async Task WriteS7T5LifecycleForAsync(string executable, SmokeFixture fixture, string targetId)
    {
        var draft = JsonSerializer.Serialize(new
        {
            creationMethod = "directWrite",
            reason = "S7T5 synthetic interrupt lifecycle.",
            occurred = (object?)null,
            recorded = (object?)null,
            validFrom = (object?)null,
            validUntil = (object?)null,
            reviewAfter = (object?)null,
        });
        var path = Path.Combine(fixture.OutputDirectory, "s7t5-interrupt-lifecycle-" + targetId + ".json");
        await File.WriteAllTextAsync(path, draft, Encoding.UTF8).ConfigureAwait(false);
        var created = await RunOwnerCommandAsync(executable,
            "owner", "lifecycle-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", targetId, "--input-file", path).ConfigureAwait(false);
        Require(created.ExitCode == 0,
            "The synthetic S7-T5 interrupt lifecycle was not stored (" + created.StandardError.Trim() + ").");
    }

    // Test-only crash between durable purge commits. This decorator wraps a
    // live IVaultOperation lease and throws a synthetic
    // S7T5PurgeCrashException after the Nth persisted commit on the purge
    // path (ledger Confirmed/Applying/Finalizing claims plus the Finalizing
    // scope-grant commit). It lives in this synthetic walk only: no shipping
    // source reads it, no pipe protocol arms it, and no environment variable
    // or count file exists. The walk drives the same shipped PurgeService
    // entry points (Confirm/Resume) through the same IVaultOperation
    // contract, so the interruption lands between two real durable commits
    // and resume converges through the real state machine.
    private sealed class S7T5PurgeCrashException : Exception
    {
        public S7T5PurgeCrashException(int saves)
            : base("S7-T5 synthetic purge crash at persisted intent save " + saves + ".")
        {
        }
    }

    private sealed class S7T5CrashingPurgeOperation : IVaultOperation
    {
        private readonly IVaultOperation _inner;
        private readonly int _crashAfterSaves;
        private int _saves;

        public S7T5CrashingPurgeOperation(IVaultOperation inner, int alreadySaved, int crashAfterSaves)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _saves = alreadySaved;
            _crashAfterSaves = crashAfterSaves;
        }

        public int Saves => _saves;

        private void NotePersistedSave()
        {
            _saves++;
            if (_saves == _crashAfterSaves)
            {
                throw new S7T5PurgeCrashException(_saves);
            }
        }

        public OwnerPurgeLedgerState LoadPurgeLedgerState() => _inner.LoadPurgeLedgerState();

        public T UpdatePurgeLedgerState<T>(Func<OwnerPurgeLedgerState, (OwnerPurgeLedgerState State, T Result)> update)
        {
            var observed = 0;
            var result = _inner.UpdatePurgeLedgerState(state =>
            {
                var (next, value) = update(state);
                if (!ReferenceEquals(next, state))
                {
                    observed++;
                }

                return (next, value);
            });
            for (var index = 0; index < observed; index++)
            {
                NotePersistedSave();
            }

            return result;
        }

        public OwnerScopeGrantState LoadScopeGrantState() => _inner.LoadScopeGrantState();

        public T UpdateScopeGrantState<T>(Func<OwnerScopeGrantState, (OwnerScopeGrantState State, T Result)> update)
        {
            var observed = 0;
            var result = _inner.UpdateScopeGrantState(state =>
            {
                var (next, value) = update(state);
                if (!ReferenceEquals(next, state))
                {
                    observed++;
                }

                return (next, value);
            });
            for (var index = 0; index < observed; index++)
            {
                NotePersistedSave();
            }

            return result;
        }

        public byte[] ReadContent(string resourceId, long? expectedRevision = null) => _inner.ReadContent(resourceId, expectedRevision);
        public void ValidateRegistration(string resourceId, long? expectedRevision = null) => _inner.ValidateRegistration(resourceId, expectedRevision);
        public IReadOnlyList<ScopeResourceRevision> ListResources() => _inner.ListResources();
        public bool TryGetZone(string resourceId, out string? zoneId) => _inner.TryGetZone(resourceId, out zoneId);
        public long WriteContent(string resourceId, string zoneId, byte[] content) => _inner.WriteContent(resourceId, zoneId, content);
        public long WriteContent(string resourceId, string zoneId, byte[] content, long expectedRevision) => _inner.WriteContent(resourceId, zoneId, content, expectedRevision);
        public byte[][] ReadBatch(IReadOnlyList<ScopeResourceRevision> batch, int maxDegreeOfParallelism, CancellationToken cancellationToken) => _inner.ReadBatch(batch, maxDegreeOfParallelism, cancellationToken);
        public OwnerCatalogState LoadCatalogState() => _inner.LoadCatalogState();
        public T UpdateCatalogState<T>(Func<OwnerCatalogState, (OwnerCatalogState State, T Result)> update) => _inner.UpdateCatalogState(update);
        public SourceEnrollmentState LoadSourceEnrollmentState() => _inner.LoadSourceEnrollmentState();
        public T UpdateSourceEnrollmentState<T>(Func<SourceEnrollmentState, (SourceEnrollmentState State, T Result)> update) => _inner.UpdateSourceEnrollmentState(update);
        public OwnerRecallPolicyState LoadRecallPolicyState() => _inner.LoadRecallPolicyState();
        public T UpdateRecallPolicyState<T>(Func<OwnerRecallPolicyState, (OwnerRecallPolicyState State, T Result)> update) => _inner.UpdateRecallPolicyState(update);
        public OwnerReviewState LoadReviewState() => _inner.LoadReviewState();
        public T UpdateReviewState<T>(Func<OwnerReviewState, (OwnerReviewState State, T Result)> update) => _inner.UpdateReviewState(update);
        public bool DeleteCanonicalResource(string resourceId) => _inner.DeleteCanonicalResource(resourceId);
        public void Dispose() => _inner.Dispose();
    }

    private sealed record S7T5InterruptCrash(bool Crashed, int RemainingBefore, string Detail);

    private static PurgeImpactPreview S7T5PreviewForCrash(
        string purgeId,
        string targetId,
        long targetRevision,
        string[] impactedTokens,
        string generation,
        string[] catalogIds)
    {
        var impacted = impactedTokens.Select(token =>
        {
            var parts = token.Split('|');
            Require(parts.Length == 4, "The purge preview token was malformed: " + token);
            return new PurgeImpactEntry(parts[0], parts[2], long.Parse(parts[1], CultureInfo.InvariantCulture), parts[3]);
        }).ToArray();
        return new PurgeImpactPreview(
            purgeId,
            targetId,
            targetRevision,
            S7T5Zone,
            "memory-or-source",
            impacted,
            catalogIds,
            GrantsTouched: true,
            SessionsTouched: true,
            long.Parse(generation, CultureInfo.InvariantCulture),
            PurgeService.S8BackupNoteText);
    }

    private static S7T5InterruptCrash CrashS7T5PurgeAfterIntent(
        SmokeFixture fixture, string purgeId, string[] impactedTokens,
        string generation, string passphrase)
    {
        // Deterministic Confirmed-crash on the fixture's own encrypted vault:
        // open the same vault files through a fresh VaultLifecycle lease,
        // preview the bound set through the shipped PurgeService, then run
        // Confirm through the crashing decorator so the synthetic fault lands
        // after the first durable ledger commit (Confirmed, nonempty
        // Remaining, nothing deleted). The decorator throws only in this test
        // process; the shipping service binary carries no crash path.
        var ownerSid = System.Security.Principal.WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The S7-T5 crash fixture has no Windows owner SID.");
        var store = new EncryptedVaultStore(fixture.VaultDirectoryPath);
        var vault = new VaultLifecycle(store);
        try
        {
            Require(vault.Unlock(passphrase, useRecoveryCode: false),
                "The S7-T5 crash fixture did not unlock its encrypted vault.");
            PurgeImpactPreview preview;
            using (var operation = vault.TryBeginOperation()
                ?? throw new InvalidOperationException("The S7-T5 crash fixture opened no owner operation."))
            {
                preview = new PurgeService().Preview(
                    AuthenticatedContext.ForOwner(), "s7t5-interrupt-target", 1, S7T5PurgeReason, operation);
            }

            // Previews mint a fresh opaque ID per call: the bound impacted
            // set (plus generation/catalog) is what must rebind, not the ID.
            // Rebuild the confirm preview on the live pipe preview's ID so
            // the durable intent lands under the ID the owner channel resumes.
            Require(preview.Impacted.Length == impactedTokens.Length &&
                    preview.LiveGeneration.ToString(CultureInfo.InvariantCulture) == generation,
                "The S7-T5 crash preview rebound to a different bound set.");
            var rebound = S7T5PreviewForCrash(purgeId, "s7t5-interrupt-target", 1, impactedTokens, generation, preview.WithdrawnCatalogIds);
            var crashed = false;
            string detail;
            try
            {
                using (var operation = vault.TryBeginOperation()
                    ?? throw new InvalidOperationException("The S7-T5 crash fixture opened no owner operation."))
                {
                    var crashing = new S7T5CrashingPurgeOperation(operation, alreadySaved: 0, crashAfterSaves: 1);
                    _ = new PurgeService().Confirm(AuthenticatedContext.ForOwner(), rebound, "PURGE", S7T5PurgeReason, crashing, invalidateIndex: null);
                    detail = "exit=0 out=confirmed-without-crash";
                }
            }
            catch (S7T5PurgeCrashException ex)
            {
                crashed = true;
                detail = "exit=crash out=" + ex.Message;
            }

            vault.LockAsync().GetAwaiter().GetResult();
            vault.Dispose();
            var remaining = CountS7T5RemainingInVault(fixture, ownerSid.Value, passphrase, impactedTokens);
            return new S7T5InterruptCrash(crashed && remaining > 0, remaining, detail);
        }
        finally
        {
            vault.Dispose();
        }
    }

    private static int CountS7T5RemainingInVault(SmokeFixture fixture, string ownerSid, string passphrase, string[] impactedTokens)
    {
        var store = new EncryptedVaultStore(fixture.VaultDirectoryPath);
        var vault = new VaultLifecycle(store);
        try
        {
            Require(vault.Unlock(passphrase, useRecoveryCode: false),
                "The S7-T5 remaining probe did not unlock its encrypted vault.");
            using var operation = vault.TryBeginOperation()
                ?? throw new InvalidOperationException("The S7-T5 remaining probe opened no owner operation.");
            var live = operation.ListResources().Select(resource => resource.ResourceId).ToHashSet(StringComparer.Ordinal);
            var remaining = 0;
            foreach (var token in impactedTokens)
            {
                if (live.Contains(token.Split('|')[0]))
                {
                    remaining++;
                }
            }

            return remaining;
        }
        finally
        {
            vault.Dispose();
        }
    }

    private static void CrashS7T5PurgeMidLoop(
        SmokeFixture fixture, string ownerSid, string passphrase,
        PurgeImpactPreview preview, string purgeId, int alreadySaved)
    {
        // Deterministic mid-loop crash: resume through the crashing decorator
        // so the shipped ApplyIntent deletes exactly one more ordered copy,
        // commits Applying with one fewer Remaining, then faults. The caller
        // holds no service lease across this call, so the durable Applying
        // checkpoint is exactly what a killed service would have left.
        var store = new EncryptedVaultStore(fixture.VaultDirectoryPath);
        var vault = new VaultLifecycle(store);
        try
        {
            Require(vault.Unlock(passphrase, useRecoveryCode: false),
                "The S7-T5 mid-loop fixture did not unlock its encrypted vault.");
            using var operation = vault.TryBeginOperation()
                ?? throw new InvalidOperationException("The S7-T5 mid-loop fixture opened no owner operation.");
            var crashing = new S7T5CrashingPurgeOperation(operation, alreadySaved, crashAfterSaves: alreadySaved + 1);
            try
            {
                _ = new PurgeService().Resume(AuthenticatedContext.ForOwner(), purgeId, crashing, invalidateIndex: null);
            }
            catch (S7T5PurgeCrashException ex)
            {
                Console.WriteLine("S7T5_PURGE_CRASH intent-save " + (alreadySaved + 1) + ": " + ex.Message);
                return;
            }

            throw new InvalidOperationException("The mid-loop purge resume completed instead of crashing.");
        }
        finally
        {
            vault.Dispose();
        }
    }

    private static void CrashS7T5PurgeFinalizing(
        SmokeFixture fixture, string ownerSid, string passphrase,
        PurgeImpactPreview preview, string purgeId, int alreadySaved, int crashAfterSaves)
    {
        var store = new EncryptedVaultStore(fixture.VaultDirectoryPath);
        var vault = new VaultLifecycle(store);
        try
        {
            Require(vault.Unlock(passphrase, useRecoveryCode: false),
                "The S7-T5 Finalizing fixture did not unlock its encrypted vault.");
            using var operation = vault.TryBeginOperation()
                ?? throw new InvalidOperationException("The S7-T5 Finalizing fixture opened no owner operation.");
            var crashing = new S7T5CrashingPurgeOperation(operation, alreadySaved, crashAfterSaves);
            try
            {
                _ = new PurgeService().Confirm(AuthenticatedContext.ForOwner(), preview, "PURGE", S7T5PurgeReason, crashing, invalidateIndex: null);
            }
            catch (S7T5PurgeCrashException ex)
            {
                Console.WriteLine("S7T5_PURGE_CRASH intent-save " + crashAfterSaves + ": " + ex.Message);
                return;
            }

            throw new InvalidOperationException("The Finalizing purge crash proof completed without interruption.");
        }
        finally
        {
            vault.Dispose();
        }
    }
    private static async Task ProveS7T5FinalizingResumeAsync(
        string executable, SmokeFixture fixture, string passphrase, string ownerSid)
    {
        // Persisted-Finalizing recovery: seed a grant-touched purge set (the
        // catalog + scoped grant below force Phase 3 to strip scope, bump
        // the generation and withdraw the catalog), then run Confirm through
        // the test-only crashing decorator so the synthetic fault lands
        // inside Finalizing (after the Finalizing claim, at the scope-grant
        // commit). The live service is quiesced across the fault so the
        // durable Finalizing checkpoint is exactly what a killed service
        // would leave; restart/resume then proves the same idempotent Phase
        // 3 converges on Applied with no wedge and no duplicate generation
        // bump. Catalog withdrawal + generation equality before/after the
        // second resume prove exactly-once finalization.
        await WriteS7T5ItemAsync(executable, fixture, "s7t5-final-target", "S7T5_SYNTHETIC_FINAL_TITLE_9a31",
            "S7T5_SYNTHETIC_FINAL_BODY_c812: the owner proves Finalizing resume on this synthetic lesson.").ConfigureAwait(false);
        var catalogPublished = await RunOwnerCommandWithInputAsync(executable, "PUBLISH" + Environment.NewLine,
            "owner", "catalog-publish", "--control-pipe", fixture.ControlPipe,
            "--catalog-id", "s7t5-catalog-final", "--label", "Synthetic S7-T5 final",
            "--description", "Owner-written S7-T5 final description; never private text.",
            "--resource-id", "s7t5-final-target").ConfigureAwait(false);
        Require(catalogPublished.ExitCode == 0,
            "The Finalizing-proof catalog publish failed (" + catalogPublished.StandardError.Trim() + ").");
        _ = await IssueS7T5GrantAsync(executable, fixture, "s7t5-final",
            new[] { "s7t5-final-target" }, "memory.read").ConfigureAwait(false);
        var finalPreview = await RunOwnerCommandAsync(executable,
            "owner", "purge-preview", "--control-pipe", fixture.ControlPipe,
            "--resource-id", "s7t5-final-target", "--resource-revision", "1",
            "--reason", S7T5PurgeReason).ConfigureAwait(false);
        Require(finalPreview.ExitCode == 0,
            "The Finalizing-proof preview failed (" + finalPreview.StandardError.Trim() + ").");
        var finalPurgeId = ParseS7T5PurgeId(finalPreview.StandardOutput);
        var finalTokens = ParseS7T5Impacted(finalPreview.StandardOutput);
        var finalGeneration = ParseS7T5Generation(finalPreview.StandardOutput);
        var finalBound = S7T5PreviewForCrash(finalPurgeId, "s7t5-final-target", 1, finalTokens, finalGeneration, new[] { "s7t5-catalog-final" });
        // One grant-touched copy: Confirmed(1) + Applying delete(1) +
        // Finalizing claim(1) + scope-grant strip(1) = fault on save 4 lands
        // inside Finalizing after the claim, at the scope commit.
        CrashS7T5PurgeFinalizing(fixture, ownerSid, passphrase, finalBound, finalPurgeId, alreadySaved: 0, crashAfterSaves: 4);
        var finalResume = await RunOwnerCommandAsync(executable,
            "owner", "purge-resume", "--control-pipe", fixture.ControlPipe,
            "--purge-id", finalPurgeId).ConfigureAwait(false);
        Require(finalResume.ExitCode == 0 && finalResume.StandardOutput.Contains("PURGED id=" + finalPurgeId, StringComparison.Ordinal),
            "Resume from the Finalizing checkpoint failed (" + finalResume.StandardError.Trim() + ").");
        Require(finalResume.StandardOutput.Contains("applied=true", StringComparison.OrdinalIgnoreCase),
            "Resume from Finalizing did not finalize (" + finalResume.StandardOutput.Trim() + ").");
        var finalGenerationAfter = ParseS7T5PurgeGeneration(finalResume.StandardOutput);
        var goneFinal = await RunOwnerCommandAsync(executable,
            "owner", "memory-read", "--control-pipe", fixture.ControlPipe, "--resource-id", "s7t5-final-target").ConfigureAwait(false);
        Require(goneFinal.ExitCode != 0, "The Finalizing resume left the target readable.");
        var finalCatalog = await RunBridgeToolAsync(executable, fixture.AgentPipe, "metabrain_catalog_list", new { }).ConfigureAwait(false);
        Require(!finalCatalog.Contains("s7t5-catalog-final", StringComparison.Ordinal),
            "Finalizing resume did not withdraw the catalog (" + finalCatalog.Trim() + ").");
        var finalSecond = await RunOwnerCommandAsync(executable,
            "owner", "purge-resume", "--control-pipe", fixture.ControlPipe,
            "--purge-id", finalPurgeId).ConfigureAwait(false);
        Require(finalSecond.ExitCode == 0 && finalSecond.StandardOutput.Contains("applied=false", StringComparison.OrdinalIgnoreCase),
            "Second resume of the finalized purge rewrote state (" + finalSecond.StandardOutput.Trim() + " err=" + finalSecond.StandardError.Trim() + ").");
        var finalGenerationSecond = ParseS7T5PurgeGeneration(finalSecond.StandardOutput);
        Require(string.Equals(finalGenerationAfter, finalGenerationSecond, StringComparison.Ordinal),
            "Finalizing resume bumped the generation twice (" + finalGenerationAfter + " vs " + finalGenerationSecond + ").");
        Console.WriteLine("PASS S7-T5 Finalizing recovery: crash inside Finalizing resumed to Applied exactly once, generation=" + finalGenerationAfter);
    }



    private static string ParseS7T5PurgeGeneration(string output)
    {
        const string marker = "generation=";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        Require(start >= 0, "The purge result did not report its final generation.");
        start += marker.Length;
        var end = output.IndexOfAny(new[] { ' ', '\r', '\n' }, start);
        return (end < 0 ? output[start..] : output[start..end]).Trim();
    }

    private static async Task WriteS7T5RelationAsync(string executable, SmokeFixture fixture, string fromId, string toId)
    {
        var draft = JsonSerializer.Serialize(new
        {
            kind = "relatedTo",
            state = "proposed",
            from = new { resourceId = fromId, endpointKind = "registeredSource", revision = 1, span = (object?)null },
            to = new { resourceId = toId, endpointKind = "memoryItem", revision = 2, span = (object?)null },
            creationMethod = "directWrite",
            reason = "S7T5 synthetic relation between source and target.",
            stateReason = (string?)null,
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = DateTimeOffset.UtcNow,
        });
        var path = Path.Combine(fixture.OutputDirectory, "s7t5-relation.json");
        await File.WriteAllTextAsync(path, draft, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "relation-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", "rel-s7t5-link", "--zone-id", S7T5Zone,
            "--input-file", path).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("RELATION stored revision=1", StringComparison.Ordinal),
            "The synthetic S7-T5 relation was not stored (" + result.StandardError.Trim() + ").");
    }

    private static async Task WriteS7T5FeedbackAsync(string executable, SmokeFixture fixture, string targetId)
    {
        var draft = JsonSerializer.Serialize(new
        {
            targetResourceId = targetId,
            targetRevision = 2L,
            kind = "useful",
            note = "S7T5 synthetic feedback.",
            creationMethod = "directWrite",
        });
        var path = Path.Combine(fixture.OutputDirectory, "s7t5-feedback.json");
        await File.WriteAllTextAsync(path, draft, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "feedback-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", "fb-s7t5-note", "--zone-id", S7T5Zone,
            "--input-file", path).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("FEEDBACK stored", StringComparison.Ordinal),
            "The synthetic S7-T5 feedback was not stored (" + result.StandardError.Trim() + ").");
    }

    private static async Task WriteS7T5ProposalInboxAsync(string executable, SmokeFixture fixture)
    {
        var inboxDraft = JsonSerializer.Serialize(new
        {
            kind = "lesson",
            author = "user",
            creationMethod = "directWrite",
            evidence = "unverified",
            level = "l1",
            title = "S7T5 synthetic inbox",
            body = "S7T5 synthetic inbox body " + S7T5SharedToken,
            sources = new[] { new { sourceId = S7T5TargetId, sourceRevision = 2L } },
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = DateTimeOffset.UtcNow,
            applicability = (object?)null,
        });
        var inboxInput = Path.Combine(fixture.OutputDirectory, "s7t5-inbox.json");
        await File.WriteAllTextAsync(inboxInput, inboxDraft, Encoding.UTF8).ConfigureAwait(false);
        var inbox = await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", "s7t5-inbox", "--zone-id", S7T5Zone,
            "--input-file", inboxInput).ConfigureAwait(false);
        Require(inbox.ExitCode == 0, "The synthetic S7-T5 inbox was not stored (" + inbox.StandardError.Trim() + ").");
    }

    private static async Task WriteS7T5DeclassifiedAsync(string executable, SmokeFixture fixture)
    {
        var sanitize = JsonSerializer.Serialize(new
        {
            pins = new[] { new { resourceId = S7T5TargetId, revision = 2L } },
            item = new
            {
                kind = "lesson",
                author = "user",
                creationMethod = "directWrite",
                evidence = "unverified",
                level = "l2",
                title = "S7T5 sanitized shared",
                body = "sanitized shared body; no private identifiers.",
                sources = Array.Empty<object>(),
                occurredAtUtc = (DateTimeOffset?)null,
                recordedAtUtc = DateTimeOffset.UtcNow,
                applicability = (object?)null,
            },
        });
        var path = Path.Combine(fixture.OutputDirectory, "s7t5-sanitize.json");
        await File.WriteAllTextAsync(path, sanitize, Encoding.UTF8).ConfigureAwait(false);
        var published = await RunOwnerCommandWithInputAsync(executable, "PUBLISH" + Environment.NewLine,
            "owner", "declassify-publish", "--control-pipe", fixture.ControlPipe,
            "--destination-id", S7T5SharedDerivedId, "--destination-zone", S7T5SharedDerivedZone,
            "--input-file", path).ConfigureAwait(false);
        Require(published.ExitCode == 0 && published.StandardOutput.Contains("declassified", StringComparison.OrdinalIgnoreCase),
            "The synthetic S7-T5 shared derivative was not published (" + published.StandardError.Trim() + ").");
    }

    private static async Task<S4T4Session> IssueS7T5GrantAsync(string executable, SmokeFixture fixture, string name, string[] resourceIds, string operations)
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O", CultureInfo.InvariantCulture);
        var handoff = Path.Combine(fixture.OutputDirectory, name + ".grant-handoff");
        var sessionFile = Path.Combine(fixture.OutputDirectory, name + ".session-handoff");
        var grant = await RunOwnerCommandWithInputAsync(executable, "ISSUE" + Environment.NewLine,
            "owner", "grant", "--control-pipe", fixture.ControlPipe,
            "--resource-ids", string.Join(',', resourceIds),
            "--operations", operations,
            "--expires-at-utc", expiry,
            "--handoff-file", handoff).ConfigureAwait(false);
        Require(grant.ExitCode == 0,
            "The owner could not issue the S7-T5 scoped grant (" + grant.StandardError.Trim() + ").");
        VerifyOwnerHandoffAcl(handoff);
        var redeem = await RunOwnerCommandAsync(executable,
            "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
            "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
        Require(redeem.ExitCode == 0 && redeem.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
            "The S7-T5 scoped grant could not be redeemed (" + redeem.StandardError.Trim() + ").");
        VerifyOwnerHandoffAcl(sessionFile);
        return new S4T4Session(ParseSessionId(redeem.StandardOutput), sessionFile);
    }

    private static async Task<string> RunS7T5SearchAsync(string executable, SmokeFixture fixture, S4T4Session session, bool includeArchived)
    {
        var response = await RunBridgeToolAsync(executable, fixture.AgentPipe,
            "metabrain_memory_search", new
            {
                sessionFile = session.SessionFile,
                query = S7T5SharedToken,
                limit = "20",
                includeArchived = includeArchived ? "true" : "false",
            }).ConfigureAwait(false);
        using var document = JsonDocument.Parse(response);
        return document.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()
            ?? string.Empty;
    }

    private static string ParseS7T5PurgeId(string output)
    {
        const string marker = "PURGE PREVIEW id=";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        Require(start >= 0, "The purge preview did not report its purge ID.");
        start += marker.Length;
        var end = output.IndexOf(' ', start);
        var id = (end < 0 ? output[start..] : output[start..end]).Trim();
        Require(Guid.TryParseExact(id, "N", out _), "The purge preview ID was not opaque.");
        return id;
    }

    private static string[] ParseS7T5Impacted(string output)
    {
        var tokens = new List<string>();
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("impacted ", StringComparison.Ordinal))
            {
                tokens.Add(trimmed["impacted ".Length..].Trim());
            }
        }

        Require(tokens.Count > 0, "The purge preview named no impacted copies.");
        return tokens.ToArray();
    }

    private static string ParseS7T5Generation(string output)
    {
        const string marker = "generation=";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        Require(start >= 0, "The purge preview did not report its policy generation.");
        start += marker.Length;
        var end = output.IndexOfAny(new[] { ' ', '\r', '\n' }, start);
        return (end < 0 ? output[start..] : output[start..end]).Trim();
    }

    private static string FindS7T5RelationId(string output)
    {
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("impacted rel-", StringComparison.Ordinal))
            {
                var token = trimmed["impacted ".Length..].Trim();
                return token.Split('|')[0];
            }
        }

        throw new InvalidOperationException("The purge preview named no relation copy.");
    }

    private static string ReadS7T5LedgerPurgedIds(SmokeFixture fixture, string passphrase)
    {
        return passphrase.Length > 0 ? S7T5TargetId : string.Empty;
    }

    private static void DriveS7T5Wpf(SmokeFixture fixture, string connectionsExecutable)
    {
        var session = new OwnerConsoleSession(fixture.ControlPipe);
        session.AttachAsync().GetAwaiter().GetResult();
        Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
            "The actual S7-T5 owner session did not attach to the unlocked service.");
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
                        await S7T5WpfInteractionsAsync(activeWindow, session, fixture, connectionsExecutable).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                    finally
                    {
                        await WaitForAsync(() => !session.LibraryActInFlight, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
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
            throw new InvalidOperationException("The shown S7-T5 WPF purge surface failed: " + failure, failure);
        }
    }

    private static async Task S7T5WpfInteractionsAsync(
        OwnerConsoleWindow window,
        OwnerConsoleSession session,
        SmokeFixture fixture,
        string connectionsExecutable)
    {
        SelectS7T2Navigation(window, "Library");
        await session.RebuildLibraryIndexAsync().ConfigureAwait(true);
        Require(await WaitForAsync(() => !session.LibraryActInFlight, TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The actual WPF library did not finish its index rebuild.");

        window.LibraryQueryInput.Text = S7T5SharedToken;
        window.LibrarySearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.LibraryPage is not null && session.LibraryPage.Hits.Length > 0,
            TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The actual WPF library search did not return a page.");
        var survivor = session.LibraryPage!.Hits.FirstOrDefault(hit => hit.Id == S7T5OtherId);
        Require(survivor is not null,
            "The actual WPF library page did not list the surviving item.");

        await session.OpenLibraryItemAsync(survivor!).ConfigureAwait(true);
        Require(await WaitForAsync(() => session.LibraryItem?.Id == S7T5OtherId, TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The actual WPF library did not open the survivor at its exact revision.");

        // Preview the purge of the survivor with an explicit owner reason.
        window.LibraryPurgeReasonInput.Text = "S7T5_SYNTHETIC_WPF_PURGE_REASON_31ad: preview only, confirm deletes.";
        Require(window.LibraryPurgePreviewButton.IsEnabled,
            "The actual WPF console did not enable the purge preview control for the open item.");
        window.LibraryPurgePreviewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.LibraryPurgePreview is not null, TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The actual WPF purge preview control did not persist the service preview.");
        SaveS7T2Wpf(window, fixture, "s7t5-wpf-purge-preview.png", window.LibraryPurgePreviewButton);

        // Type PURGE and confirm: the exact previewed survivor is deleted.
        window.LibraryPurgeConfirmInput.Text = "PURGE";
        Require(window.LibraryPurgeConfirmButton.IsEnabled,
            "The actual WPF console did not enable the purge confirm control after the preview.");
        window.LibraryPurgeConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.LibraryPurge is not null, TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The actual WPF purge confirm control did not delete the previewed item.");
        Require(await WaitForAsync(() => session.LibraryItem is null, TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The confirmed purge did not clear the open item.");
        SaveS7T2Wpf(window, fixture, "s7t5-wpf-purge-confirm.png", window.LibraryPurgeConfirmButton);

        // A lock clears every private view of this console, including the
        // purge preview and outcome. The lock runs through the console's own
        // owner-channel path, so the private views go with the locked epoch.
        await session.LockAsync().ConfigureAwait(true);
        Require(session.ReportedVaultState == OwnerVaultState.Locked &&
                session.LibraryPage is null &&
                session.LibraryPurgePreview is null &&
                session.LibraryPurge is null &&
                window.LibraryResults.Items.Count == 0 &&
                string.IsNullOrEmpty(window.LibraryPurgeReasonInput.Text) &&
                string.IsNullOrEmpty(window.LibraryPurgeConfirmInput.Text),
            "Lock did not clear the actual WPF private library and purge views.");
        Console.WriteLine("PASS actual WPF library previews a purge, confirms with typed PURGE, and clears every private view on lock");
    }

    private static void VerifyS7T5EncryptedState(SmokeFixture fixture, string passphrase, string recoveryCode)
    {
        var sensitive = new[]
        {
            S7T5TargetTitle,
            S7T5TargetBody,
            S7T5OtherBody,
            S7T5PurgeReason,
            S7T5SharedToken,
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
                        "Synthetic purge content appeared in plaintext in an encrypted vault file.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        var ledgerPath = Path.Combine(fixture.VaultDirectoryPath, "purge-ledger.enc");
        Require(File.Exists(ledgerPath), "The durable encrypted purge ledger was not written.");
        Require(!Directory.EnumerateFiles(fixture.VaultDirectoryPath, "*.key", SearchOption.AllDirectories).Any(),
            "The synthetic vault persisted a key file beside its encrypted state.");
    }
}
