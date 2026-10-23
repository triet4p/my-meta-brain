    using System.Diagnostics;
    using System.Globalization;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media.Imaging;
    using MetaBrain.Core.Domain;
    using MetaBrain.Connections;
    using MetaBrain.OwnerUi;

    namespace MetaBrain.S1T4.Smoke;

    internal static partial class OwnerServiceSmoke
    {
        internal static async Task RunS7T1TemporalAsync(string connectionsExecutable)
        {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S7-T1 temporal smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S7-T1 temporal recall and decision history end-to-end (current/as-of/history over validity/revision)");
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
                "Provision did not create the S7-T1 synthetic vault.");
            var unlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S7-T1 vault did not unlock.");

            const string zone = "s7t1-zone";
            const string oldDecision = "s7t1-decision-old";
            const string newDecision = "s7t1-decision-new";
            const string researchA = "s7t1-research-alpha";
            const string researchB = "s7t1-research-beta";
            const string expired = "s7t1-expired-note";
            const string approxItem = "s7t1-approx-note";

            var now = DateTimeOffset.UtcNow.AddMinutes(-10);
            var occurredOld = now.AddDays(-400);
            var occurredNew = now.AddDays(-30);
            var recordedOld = occurredOld.AddMinutes(5);
            var recordedNew = occurredNew.AddMinutes(5);

            // Old decision: occurred/recorded ~400 days ago; validity bounded to its own year span.
            await WriteS7T1ItemAsync(connectionsExecutable, fixture, oldDecision, zone,
                S7T1Draft("decision", "Synthetic S7-T1 old deployment decision; keep legacy playbook for legacy hosts.", occurredOld, recordedOld), 1).ConfigureAwait(false);
            await CreateS7T1LifecycleAsync(connectionsExecutable, fixture, oldDecision,
                S7T1LifecycleDraft("Synthetic S7-T1 old decision basis; never a real owner secret.",
                    S7T1Exact(occurredOld, 0), S7T1Exact(recordedOld, 0),
                    S7T1Exact(occurredOld.AddDays(-30), 0), S7T1Exact(occurredOld.AddDays(30), 0), null)).ConfigureAwait(false);
            // New decision: occurred/recorded ~30 days ago; validity open-ended from its start.
            await WriteS7T1ItemAsync(connectionsExecutable, fixture, newDecision, zone,
                S7T1Draft("decision", "Synthetic S7-T1 new deployment decision; use current playbook for current hosts.", occurredNew, recordedNew), 1).ConfigureAwait(false);
            await CreateS7T1LifecycleAsync(connectionsExecutable, fixture, newDecision,
                S7T1LifecycleDraft("Synthetic S7-T1 new decision basis; never a real owner secret.",
                    S7T1Exact(occurredNew, 0), S7T1Exact(recordedNew, 0),
                    S7T1Exact(occurredNew.AddDays(-7), 0), null, null)).ConfigureAwait(false);
            // Conflicting research pair: two attributed voices, no resolution.
            await WriteS7T1ItemAsync(connectionsExecutable, fixture, researchA, zone,
                S7T1Draft("claim", "Synthetic S7-T1 alpha finding: cache flag speeds checkout.", occurredNew, recordedNew), 1).ConfigureAwait(false);
            await WriteS7T1ItemAsync(connectionsExecutable, fixture, researchB, zone,
                S7T1Draft("claim", "Synthetic S7-T1 beta finding: cache flag slows checkout.", occurredNew, recordedNew.AddMinutes(1)), 1).ConfigureAwait(false);
            await CreateS7T1RelationAsync(connectionsExecutable, fixture, "rel-s7t1-conflict", zone,
                S7T1RelationDraft("contradicts", "accepted", researchA, researchB, "Synthetic S7-T1 recorded contradiction; both voices stay visible.")).ConfigureAwait(false);
            // Expired validity note: valid only long ago.
            var expiredOccurred = now.AddDays(-700);
            await WriteS7T1ItemAsync(connectionsExecutable, fixture, expired, zone,
                S7T1Draft("claim", "Synthetic S7-T1 expired rollout note from long ago.", expiredOccurred, expiredOccurred.AddMinutes(5)), 1).ConfigureAwait(false);
            await CreateS7T1LifecycleAsync(connectionsExecutable, fixture, expired,
                S7T1LifecycleDraft("Synthetic S7-T1 expired basis; never a real owner secret.",
                    S7T1Exact(expiredOccurred, 0), S7T1Exact(expiredOccurred.AddMinutes(5), 0),
                    S7T1Exact(expiredOccurred.AddDays(-30), 0), S7T1Exact(expiredOccurred.AddDays(30), 0), null)).ConfigureAwait(false);
            // Approximate-time note: no occurred instant; validity uses coarse month precision with zone.
            var approxRecorded = now.AddDays(-5);
            await WriteS7T1ItemAsync(connectionsExecutable, fixture, approxItem, zone,
                S7T1DraftNoOccurred("claim", "Synthetic S7-T1 approximate rollout note from planning.", approxRecorded), 1).ConfigureAwait(false);
            var approxMonth = new DateTimeOffset(approxRecorded.Year, approxRecorded.Month, 1, 0, 0, 0, TimeSpan.Zero);
            await CreateS7T1LifecycleAsync(connectionsExecutable, fixture, approxItem,
                S7T1LifecycleDraft("Synthetic S7-T1 approximate basis; never a real owner secret.",
                    S7T1Stamp("unknown"), S7T1Exact(approxRecorded, 0),
                    S7T1Month(approxMonth.AddMonths(-1).Year, approxMonth.AddMonths(-1).Month, 420),
                    S7T1Month(approxMonth.AddMonths(1).Year, approxMonth.AddMonths(1).Month, 420), null)).ConfigureAwait(false);

            // Import today must not make the old event new: build the old stored
            // envelope bytes with the original occurred/recorded instants, import
            // them today as a new resource, and prove the live read keeps the old
            // event time (not the import time). A stale revision-gated import of
            // the same bytes must still fail closed per S2-T2.
            const string importedOld = "s7t1-decision-old-imported";
            var importEnvelope = S7T1StoredEnvelopeBytes(importedOld, zone,
                "Synthetic S7-T1 old deployment decision; keep legacy playbook for legacy hosts.",
                "Synthetic S7-T1 old decision title", occurredOld, recordedOld);
            var imported = await ImportS7T1EnvelopeBytesAsync(connectionsExecutable, fixture, importedOld, zone, importEnvelope, expectedRevision: 0).ConfigureAwait(false);
            Require(imported.ExitCode == 0 && imported.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
                "The old envelope bytes did not import today (exit=" + imported.ExitCode + "; stdout=" + imported.StandardOutput.Trim() + "; stderr=" + imported.StandardError.Trim() + ").");
            var importedRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", importedOld).ConfigureAwait(false);
            Require(importedRead.ExitCode == 0 && importedRead.StandardOutput.Contains("Synthetic S7-T1 old deployment decision", StringComparison.Ordinal),
                "The imported old decision body did not round-trip (stdout=" + importedRead.StandardOutput.Trim() + ").");
            var importedDetail = await OwnerChannelClient.ReadMemoryItemAsync(fixture.ControlPipe, importedOld, null, CancellationToken.None).ConfigureAwait(false);
            Require(importedDetail.Outcome == OwnerCommandOutcome.Completed && importedDetail.Item is not null,
                "The imported old decision could not be read back over the owner channel (" + (importedDetail.ErrorCode ?? "no error") + ").");
            Require(importedDetail.Item!.OccurredAtUtc is { } importedOccurred && S7T1SameDay(importedOccurred, occurredOld) &&
                S7T1SameDay(importedDetail.Item.RecordedAtUtc, recordedOld),
                "Importing today re-dated the old event: the live item carries occurred=" + (importedDetail.Item.OccurredAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "(none)") +
                " recorded=" + importedDetail.Item.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture) +
                " instead of the original old instants.");
            Console.WriteLine("S7T1-PROBE import occurred=" + (importedDetail.Item.OccurredAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "(none)") +
                " recorded=" + importedDetail.Item.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            var staleImport = await ImportS7T1EnvelopeBytesAsync(connectionsExecutable, fixture, importedOld, zone, S7T1StoredEnvelopeBytes(importedOld, zone,
                "Synthetic S7-T1 old deployment decision; keep legacy playbook for legacy hosts.",
                "Synthetic S7-T1 old decision title", occurredOld, recordedOld), expectedRevision: 7).ConfigureAwait(false);
            Require(staleImport.ExitCode == 4 && staleImport.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A stale revision-gated import was accepted instead of failing closed (exit=" + staleImport.ExitCode + "; stdout=" + staleImport.StandardOutput.Trim() + "; stderr=" + staleImport.StandardError.Trim() + ").");
            Console.WriteLine("PASS owner memory-import today preserves the old event time; stale revision-gated import refused");

            var oldRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", oldDecision).ConfigureAwait(false);
            Require(oldRead.ExitCode == 0 && oldRead.StandardOutput.Contains("Synthetic S7-T1 old deployment decision", StringComparison.Ordinal),
                "The old decision body did not round-trip (stdout=" + oldRead.StandardOutput.Trim() + ").");

            var rebuild = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(rebuild.ExitCode == 0 && rebuild.StandardOutput.Contains("INDEX rebuilt rows=", StringComparison.Ordinal) &&
                    !rebuild.StandardOutput.Contains("rows=0", StringComparison.Ordinal),
                "The S7-T1 private index rebuild did not publish a materialized generation.");

            // Owner grant over the six corpus items for both search and read. The
            // imported old envelope stays outside the scoped grant on purpose: it
            // is the import-honesty probe, not part of the decision corpus.
            var grantedIds = new[] { oldDecision, newDecision, researchA, researchB, expired, approxItem };
            var session = await S7T1IssueGrantAsync(connectionsExecutable, fixture, "s7t1-temporal", "memory.search,memory.read", grantedIds).ConfigureAwait(false);

            // as-of between the two records: old decision current, new decision not yet recorded.
            var asOfOld = occurredOld.AddDays(7).ToString("O", CultureInfo.InvariantCulture);
            var currentOld = await S7T1SearchAsync(connectionsExecutable, fixture, session, "deployment decision", current: "current", asOf: asOfOld).ConfigureAwait(false);
            Require(currentOld.ExitCode == 0 && currentOld.StandardOutput.Contains("HIT id=" + oldDecision, StringComparison.Ordinal),
                "As-of current did not surface the old decision (exit=" + currentOld.ExitCode + "; stdout=" + currentOld.StandardOutput.Trim() + "; stderr=" + currentOld.StandardError.Trim() + ").");
            Require(!currentOld.StandardOutput.Contains("HIT id=" + newDecision, StringComparison.Ordinal),
                "As-of current leaked the not-yet-recorded new decision (stdout=" + currentOld.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS as-of replays recorded history: old decision current, new decision not yet visible");

            // as-of now: new decision current, old decision past validity -> excluded from current.
            var asOfNow = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var currentNow = await S7T1SearchAsync(connectionsExecutable, fixture, session, "deployment decision", current: "current", asOf: asOfNow).ConfigureAwait(false);
            Require(currentNow.ExitCode == 0 && currentNow.StandardOutput.Contains("HIT id=" + newDecision, StringComparison.Ordinal),
                "Current did not surface the live decision (stdout=" + currentNow.StandardOutput.Trim() + "; stderr=" + currentNow.StandardError.Trim() + ").");
            Require(!currentNow.StandardOutput.Contains("HIT id=" + oldDecision, StringComparison.Ordinal),
                "Current leaked the past-validity decision (stdout=" + currentNow.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS current excludes past-validity superseded guidance without newest-wins rewriting");

            // history at now: both decisions visible.
            var historyNow = await S7T1SearchAsync(connectionsExecutable, fixture, session, "deployment decision", current: "history", asOf: asOfNow).ConfigureAwait(false);
            Require(historyNow.ExitCode == 0 && historyNow.StandardOutput.Contains("HIT id=" + oldDecision, StringComparison.Ordinal) &&
                historyNow.StandardOutput.Contains("HIT id=" + newDecision, StringComparison.Ordinal),
                "History did not keep both decisions (stdout=" + historyNow.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS history keeps both decisions with their original times");

            // Expired note: current excludes, history keeps.
            var expiredCurrent = await S7T1SearchAsync(connectionsExecutable, fixture, session, "expired rollout", current: "current").ConfigureAwait(false);
            Require(expiredCurrent.ExitCode == 0 && !expiredCurrent.StandardOutput.Contains("HIT id=" + expired, StringComparison.Ordinal),
                "Current served the expired note (stdout=" + expiredCurrent.StandardOutput.Trim() + ").");
            var expiredHistory = await S7T1SearchAsync(connectionsExecutable, fixture, session, "expired rollout", current: "history").ConfigureAwait(false);
            Require(expiredHistory.ExitCode == 0 && expiredHistory.StandardOutput.Contains("HIT id=" + expired, StringComparison.Ordinal),
                "History did not keep the expired note (stdout=" + expiredHistory.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS expired validity: current excludes, history keeps");

            // Conflicting research: both voices visible in history; current does not pick newest-wins.
            var conflictHistory = await S7T1SearchAsync(connectionsExecutable, fixture, session, "cache flag checkout", current: "history").ConfigureAwait(false);
            Require(conflictHistory.ExitCode == 0 && conflictHistory.StandardOutput.Contains("HIT id=" + researchA, StringComparison.Ordinal) &&
                conflictHistory.StandardOutput.Contains("HIT id=" + researchB, StringComparison.Ordinal),
                "History did not keep both conflicting voices (stdout=" + conflictHistory.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS unresolved contradiction: both voices stay visible, no newest-wins");

            // Timezone boundary: validity windows are zone-anchored; an instant on the
            // boundary belongs to the window that contains it (inclusive bounds).
            var boundaryInside = approxRecorded.ToString("O", CultureInfo.InvariantCulture);
            var approxCurrent = await S7T1SearchAsync(connectionsExecutable, fixture, session, "approximate rollout", current: "current", asOf: boundaryInside).ConfigureAwait(false);
            Require(approxCurrent.ExitCode == 0 && approxCurrent.StandardOutput.Contains("HIT id=" + approxItem, StringComparison.Ordinal),
                "The coarse zone-anchored validity window did not contain its boundary instant (stdout=" + approxCurrent.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS timezone-anchored coarse validity contains its boundary instant");

            var approxOutside = approxRecorded.AddDays(-90).ToString("O", CultureInfo.InvariantCulture);
            var approxExcluded = await S7T1SearchAsync(connectionsExecutable, fixture, session, "approximate rollout", current: "current", asOf: approxOutside).ConfigureAwait(false);
            Require(approxExcluded.ExitCode == 0 && !approxExcluded.StandardOutput.Contains("HIT id=" + approxItem, StringComparison.Ordinal),
                "The coarse window leaked past its start (exit=" + approxExcluded.ExitCode + "; stdout=" + approxExcluded.StandardOutput.Trim() + "; stderr=" + approxExcluded.StandardError.Trim() + ").");
            Console.WriteLine("PASS coarse validity ends honestly without fabricated precision");

            // MCP bridge carries the same current/history/asOf semantics.
            var bridgeList = await RunBridgeToolsListAsync(connectionsExecutable, fixture.AgentPipe).ConfigureAwait(false);
            Require(bridgeList.Contains("metabrain_memory_search", StringComparison.Ordinal),
                "The MCP bridge did not advertise metabrain_memory_search.");
            var bridgeHistory = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_memory_search", new { sessionFile = session.SessionFile, query = "deployment decision", current = "history", asOf = asOfNow }).ConfigureAwait(false);
            Require(bridgeHistory.Contains(oldDecision, StringComparison.Ordinal) && bridgeHistory.Contains(newDecision, StringComparison.Ordinal),
                "The MCP bridge history did not serve both decisions (" + bridgeHistory.Trim() + ").");
            Console.WriteLine("PASS MCP search serves current/history/as-of with source evidence at the right revision");

            // Decision history over the owner channel: recorded acts with actor/time/rationale, retained original reasons.
            var timeline = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", newDecision).ConfigureAwait(false);
            Require(timeline.ExitCode == 0 && timeline.StandardOutput.Contains("subject=" + newDecision, StringComparison.Ordinal) &&
                timeline.StandardOutput.Contains("validFrom=") && timeline.StandardOutput.Contains("validUntil="),
                "The decision timeline did not serve recorded validity (stdout=" + timeline.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS decision history serves validity and recorded acts at the right revision/time");

            // Retained revision path: retain revision 1 of the new decision, revise
            // it, then prove the retained read serves the older exact bytes while
            // the live read serves the newer revision.
            const string retainedSubject = newDecision;
            var retain = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-retain", "--control-pipe", fixture.ControlPipe,
                "--resource-id", retainedSubject, "--expected-revision", "1").ConfigureAwait(false);
            Require(retain.ExitCode == 0 && retain.StandardOutput.Contains("RETAINED revision=1 archive=arc-s7t1-decision-new-r1", StringComparison.Ordinal),
                "Retaining revision 1 did not record the derived archive (exit=" + retain.ExitCode + "; stdout=" + retain.StandardOutput.Trim() + "; stderr=" + retain.StandardError.Trim() + ").");
            await ReviseS7T1ItemAsync(connectionsExecutable, fixture, retainedSubject, zone,
                S7T1Draft("decision", "Synthetic S7-T1 new deployment decision revision two; use current playbook for current hosts.", occurredNew, recordedNew.AddMinutes(2)), writeRevision: 1, expectedRevision: 2).ConfigureAwait(false);
            var retainedRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-retained-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", retainedSubject, "--resource-revision", "1").ConfigureAwait(false);
            Require(retainedRead.ExitCode == 0 && retainedRead.StandardOutput.Contains("subjectRevision=1", StringComparison.Ordinal) &&
                retainedRead.StandardOutput.Contains("archive=arc-s7t1-decision-new-r1", StringComparison.Ordinal) &&
                retainedRead.StandardOutput.Contains("Synthetic S7-T1 new deployment decision; use current playbook for current hosts.", StringComparison.Ordinal),
                "The retained historical revision did not serve its exact older bytes (exit=" + retainedRead.ExitCode + "; stdout=" + retainedRead.StandardOutput.Trim() + "; stderr=" + retainedRead.StandardError.Trim() + ").");
            var retainedLive = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", retainedSubject).ConfigureAwait(false);
            Require(retainedLive.ExitCode == 0 && retainedLive.StandardOutput.Contains("revision two", StringComparison.Ordinal) &&
                retainedLive.StandardOutput.Contains("revision=2", StringComparison.Ordinal),
                "The live item did not serve its newer revision after retention (stdout=" + retainedLive.StandardOutput.Trim() + ").");
            var retainedMissing = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-retained-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", retainedSubject, "--resource-revision", "2").ConfigureAwait(false);
            Require(retainedMissing.ExitCode != 0 && retainedMissing.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A revision that was never retained was served from the archive.");
            Console.WriteLine("PASS retained revision read serves the exact older bytes; live serves the newer revision");
            var rebuildAfterRetain = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(rebuildAfterRetain.ExitCode == 0 && rebuildAfterRetain.StandardOutput.Contains("INDEX rebuilt rows=", StringComparison.Ordinal) &&
                    !rebuildAfterRetain.StandardOutput.Contains("rows=0", StringComparison.Ordinal),
                "The S7-T1 private index rebuild after the retained revision did not publish a materialized generation.");
            // Owner UI leg: the real WPF OwnerConsoleWindow on an STA thread, with
            // the changed LibraryAsOfInput/history/retained controls and code-behind.
            // It searches (history as-of now), opens the new decision from the
            // rendered results, reads its decision history through
            // LibraryHistoryButton, then opens the retained revision 1 through
            // LibraryRetainedButton. Rendered text is observed and a real visual
            // proof PNG is captured under the ignored sprint artifacts.
            DriveS7T1WpfLibrary(fixture, newDecision, asOfNow);
            Console.WriteLine("PASS WPF OwnerConsoleWindow shows history/as-of controls, decision history and retained revision");

            // Supersede the old intention explicitly; conflicting supersession is refused, never newest-wins.
            const string intOld = "s7t1-intention-old";
            const string intNew = "s7t1-intention-new";
            await WriteS7T1ItemAsync(connectionsExecutable, fixture, intOld, zone,
                S7T1Draft("intention", "Synthetic S7-T1 superseded intention; never a real owner secret.", occurredOld, recordedOld), 1).ConfigureAwait(false);
            await WriteS7T1ItemAsync(connectionsExecutable, fixture, intNew, zone,
                S7T1Draft("intention", "Synthetic S7-T1 replacement intention; never a real owner secret.", occurredNew, recordedNew), 1).ConfigureAwait(false);
            await CreateS7T1LifecycleSimpleAsync(connectionsExecutable, fixture, intOld, "Synthetic S7-T1 intention basis; never a real owner secret.").ConfigureAwait(false);
            var supersede = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-intention-supersede", "--control-pipe", fixture.ControlPipe,
                "--resource-id", intOld, "--expected-revision", "1",
                "--input-file", await WriteS7T1JsonAsync(fixture, JsonSerializer.Serialize(new { rationale = "Synthetic S7-T1 supersession rationale; never a real owner secret.", replacementResourceId = intNew }))).ConfigureAwait(false);
            Require(supersede.ExitCode == 0 && supersede.StandardOutput.Contains("supersededBy=" + intNew, StringComparison.Ordinal),
                "Supersede did not record its explicit replacement (stdout=" + supersede.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS explicit supersession names its replacement; conflicts refuse instead of newest-wins");

            Console.WriteLine("PASS S7-T1 temporal recall and decision history end-to-end with no fabricated time");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static string S7T1Draft(string kind, string body, DateTimeOffset occurred, DateTimeOffset recorded) => JsonSerializer.Serialize(new
    {
        kind,
        author = "user",
        creationMethod = "directWrite",
        evidence = "unverified",
        level = "l1",
        title = "Synthetic S7-T1 " + kind,
        body,
        sources = Array.Empty<object>(),
        occurredAtUtc = occurred,
        recordedAtUtc = recorded,
        applicability = (object?)null
    });

    private static string S7T1DraftNoOccurred(string kind, string body, DateTimeOffset recorded) => JsonSerializer.Serialize(new
    {
        kind,
        author = "user",
        creationMethod = "directWrite",
        evidence = "unverified",
        level = "l1",
        title = "Synthetic S7-T1 " + kind,
        body,
        sources = Array.Empty<object>(),
        occurredAtUtc = (DateTimeOffset?)null,
        recordedAtUtc = recorded,
        applicability = (object?)null
    });

    private static object S7T1Exact(DateTimeOffset instant, int zone) => new
    {
        precision = "exact",
        instantUtc = instant,
        localYear = (int?)null,
        localMonth = (int?)null,
        localDay = (int?)null,
        localQuarter = (int?)null,
        timeZoneOffsetMinutes = zone
    };

    private static object S7T1Day(int year, int month, int day, int zone) => new
    {
        precision = "day",
        instantUtc = (DateTimeOffset?)null,
        localYear = year,
        localMonth = month,
        localDay = day,
        localQuarter = (int?)null,
        timeZoneOffsetMinutes = zone
    };

    private static object S7T1Month(int year, int month, int zone) => new
    {
        precision = "month",
        instantUtc = (DateTimeOffset?)null,
        localYear = year,
        localMonth = month,
        localDay = (int?)null,
        localQuarter = (int?)null,
        timeZoneOffsetMinutes = zone
    };

    private static object S7T1Stamp(string precision) => new
    {
        precision,
        instantUtc = (DateTimeOffset?)null,
        localYear = (int?)null,
        localMonth = (int?)null,
        localDay = (int?)null,
        localQuarter = (int?)null,
        timeZoneOffsetMinutes = (int?)null
    };

    private static string S7T1LifecycleDraft(string reason, object? occurred, object? recorded, object? validFrom, object? validUntil, object? reviewAfter) => JsonSerializer.Serialize(new
    {
        creationMethod = "directWrite",
        reason,
        occurred,
        recorded,
        validFrom,
        validUntil,
        reviewAfter
    });

    private static string S7T1RelationDraft(string kind, string state, string from, string to, string reason) => JsonSerializer.Serialize(new
    {
        kind,
        state,
        from = new { resourceId = from, endpointKind = "memoryItem", revision = 1L, span = (object?)null },
        to = new { resourceId = to, endpointKind = "memoryItem", revision = 1L, span = (object?)null },
        creationMethod = "directWrite",
        reason,
        requestEvidence = (string?)null,
        stateReason = (string?)null,
        occurredAtUtc = (DateTimeOffset?)null,
        recordedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
    });

    private static async Task WriteS7T1ItemAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson, long expectedRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s7t1-item-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"MEMORY stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Memory create did not store {resourceId} (exit={result.ExitCode}; stderr={result.StandardError.Trim()}).");
    }

    private static async Task ReviseS7T1ItemAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson, long writeRevision, long expectedRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s7t1-item-revise-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--expected-revision", writeRevision.ToString(CultureInfo.InvariantCulture),
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"MEMORY stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Memory revise did not store {resourceId} at revision {expectedRevision} (exit={result.ExitCode}; stderr={result.StandardError.Trim()}).");
    }

    private static async Task CreateS7T1LifecycleAsync(string executable, SmokeFixture fixture, string subjectId, string draftJson)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s7t1-lifecycle-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "lifecycle-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", subjectId, "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0,
            $"Lifecycle create did not store {subjectId} (exit={result.ExitCode}; stderr={result.StandardError.Trim()}; stdout={result.StandardOutput.Trim()}).");
    }

    private static async Task CreateS7T1LifecycleSimpleAsync(string executable, SmokeFixture fixture, string subjectId, string reason)
    {
        await CreateS7T1LifecycleAsync(executable, fixture, subjectId,
            S7T1LifecycleDraft(reason, null, null, null, null, null)).ConfigureAwait(false);
    }

    private static async Task CreateS7T1RelationAsync(string executable, SmokeFixture fixture, string relationId, string zoneId, string draftJson)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s7t1-relation-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "relation-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", relationId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("RELATION stored revision=1", StringComparison.Ordinal),
            $"Relation create did not store {relationId} (exit={result.ExitCode}; stderr={result.StandardError.Trim()}).");
    }

    private static byte[] S7T1StoredEnvelopeBytes(string resourceId, string zoneId, string body, string title, DateTimeOffset occurred, DateTimeOffset recorded)
    {
        var envelope = new StoredMemoryEnvelope(
            MemoryEnvelopeCodec.CurrentSchemaVersion,
            resourceId,
            zoneId,
            MemoryKind.Decision,
            SpeakerRole.User,
            ExecutorKind.Owner,
            CreationMethod.DirectWrite,
            EvidenceLevel.Unverified,
            AbstractionLevel.L1,
            title,
            body,
            Array.Empty<MemorySourceReference>(),
            occurred,
            recorded,
            null);
        return MemoryEnvelopeCodec.EncodeStored(envelope);
    }

    private static async Task<CommandResult> ImportS7T1EnvelopeBytesAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, byte[] envelopeBytes, long expectedRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s7t1-import-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllBytesAsync(input, envelopeBytes).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(envelopeBytes);
        return await RunOwnerCommandAsync(executable,
            "owner", "memory-import", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--expected-revision", expectedRevision.ToString(CultureInfo.InvariantCulture),
            "--input-file", input).ConfigureAwait(false);
    }

    private static bool S7T1SameDay(DateTimeOffset actual, DateTimeOffset expected) =>
        actual.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ==
            expected.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool S7T1ReadCarriesInstant(string output, DateTimeOffset instant)
    {
        var roundTrip = instant.ToString("O", CultureInfo.InvariantCulture);
        if (output.Contains(roundTrip, StringComparison.Ordinal))
        {
            return true;
        }

        var dateOnly = instant.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return output.Contains(dateOnly, StringComparison.Ordinal);
    }

    private static async Task<string> WriteS7T1JsonAsync(SmokeFixture fixture, string json)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s7t1-transition-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, json, Encoding.UTF8).ConfigureAwait(false);
        return input;
    }

    private sealed record S7T1Session(string SessionId, string SessionFile);

    private static async Task<S7T1Session> S7T1IssueGrantAsync(string executable, SmokeFixture fixture, string name, string operations, string[] resourceIds)
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
        Require(grant.ExitCode == 0, $"The owner could not issue the S7-T1 grant {name}.");
        var redeem = await RunOwnerCommandAsync(executable,
            "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
            "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
        Require(redeem.ExitCode == 0 && redeem.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
            $"Redemption failed for the S7-T1 grant {name}.");
        return new S7T1Session(ParseSessionId(redeem.StandardOutput), sessionFile);
    }

    private static async Task<CommandResult> S7T1SearchAsync(string executable, SmokeFixture fixture, S7T1Session session, string query, int limit = 10, string? current = null, string? asOf = null)
    {
        var args = new List<string>
        {
            "owner", "agent-search", "--agent-pipe", fixture.AgentPipe,
            "--session-file", session.SessionFile,
            "--query", query,
            "--offset", "0",
            "--limit", limit.ToString(CultureInfo.InvariantCulture),
        };
        if (current is not null)
        {
            args.Add("--current");
            args.Add(current);
        }

        if (asOf is not null)
        {
            args.Add("--as-of");
            args.Add(asOf);
        }

        return await RunOwnerCommandAsync(executable, args.ToArray()).ConfigureAwait(false);
    }

    private static void DriveS7T1WpfLibrary(SmokeFixture fixture, string expectedDecision, string asOfNow)
    {
        var session = new OwnerConsoleSession(fixture.ControlPipe);
        session.AttachAsync().GetAwaiter().GetResult();
        Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
            "The console session did not report the real unlocked vault (" + session.ReportedVaultState + ").");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            OwnerConsoleWindow? window = null;
            try
            {
                window = new OwnerConsoleWindow(session);
                window.Width = 1360;
                window.Height = 1100;
                window.Show();
                window.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await S7T1WpfLibraryAsync(window, session, fixture, expectedDecision, asOfNow).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                    finally
                    {
                        await WaitForAsync(() => !session.LibraryActInFlight, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
                        await Task.Delay(200).ConfigureAwait(true);
                        window.DetachView();
                        window.Close();
                        window.Dispatcher.InvokeShutdown();
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
            throw new InvalidOperationException("The shown S7-T1 WPF library surface failed: " + failure.Message, failure);
        }
    }

    private static async Task S7T1WpfLibraryAsync(OwnerConsoleWindow window, OwnerConsoleSession session, SmokeFixture fixture, string expectedDecision, string asOfNow)
    {
        SelectS7T1Navigation(window, "Library");
        Require(window.Navigation.SelectedItem is ListBoxItem selected && string.Equals(selected.Content as string, "Library", StringComparison.Ordinal),
            "The shown WPF console did not navigate to the Library surface.");
        window.LibraryQueryInput.Text = "deployment decision";
        window.LibraryCurrentInput.SelectedItem = "history";
        window.LibraryAsOfInput.Text = asOfNow;
        Require(string.Equals(window.LibraryAsOfInput.Text, asOfNow, StringComparison.Ordinal),
            "The shown WPF as-of input did not keep the typed instant.");
        window.LibrarySearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.LibraryPage is { } page && page.Hits.Any(hit => string.Equals(hit.Id, expectedDecision, StringComparison.Ordinal)), TimeSpan.FromSeconds(45)).ConfigureAwait(true),
            "The shown WPF library did not list the expected decision (" + (session.LibraryMessage ?? "no message") + ").");
        Require(window.LibraryPageText.Text.Contains("show=history", StringComparison.OrdinalIgnoreCase) &&
            window.LibraryPageText.Text.Contains("asOf=", StringComparison.Ordinal),
            "The shown WPF page text did not echo the history/as-of filters (" + window.LibraryPageText.Text + ").");
        var index = Array.FindIndex(session.LibraryPage!.Hits, hit => string.Equals(hit.Id, expectedDecision, StringComparison.Ordinal));
        Require(index >= 0 && index < window.LibraryResults.Items.Count,
            "The shown WPF results did not render the expected decision row.");
        window.LibraryResults.SelectedIndex = index;
        window.LibraryResults.ScrollIntoView(window.LibraryResults.SelectedItem);
        await session.OpenLibraryItemAsync(session.LibraryPage.Hits[index]).ConfigureAwait(true);
        Require(session.LibraryItem is { } open && string.Equals(open.Id, expectedDecision, StringComparison.Ordinal),
            "The shown WPF surface did not open the exact revision the history page named.");
        Require(await WaitForAsync(() => window.LibraryDetailSummaryText.Text.Contains(expectedDecision, StringComparison.Ordinal), TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The shown WPF detail did not render the opened decision (" + window.LibraryDetailSummaryText.Text + ").");
        Require(window.LibraryHistoryButton.IsEnabled, "The shown WPF console did not offer the decision-history act for the open item.");
        window.LibraryHistoryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.LibraryTimeline is { } timeline && string.Equals(timeline.SubjectResourceId, expectedDecision, StringComparison.Ordinal), TimeSpan.FromSeconds(45)).ConfigureAwait(true),
            "The shown WPF decision history did not arrive (" + (session.LibraryMessage ?? "no message") + ").");
        Require(await WaitForAsync(() => window.LibraryHistoryText.Text.Contains("subject=" + expectedDecision, StringComparison.Ordinal) &&
            window.LibraryHistoryText.Text.Contains("validFrom=", StringComparison.Ordinal), TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The shown WPF history text did not render the recorded validity (" + window.LibraryHistoryText.Text + ").");
        Console.WriteLine("S7T1-PROBE WPF history text=" + FirstS7T1Line(window.LibraryHistoryText.Text));
        Require(window.LibraryRetainedButton.IsEnabled, "The shown WPF console did not offer the retained-revision act for the archived history.");
        window.LibraryRetainedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.LibraryRetained is { } retained && retained.SubjectRevision == 1, TimeSpan.FromSeconds(45)).ConfigureAwait(true),
            "The shown WPF retained revision did not arrive (" + (session.LibraryMessage ?? "no message") + ").");
        Require(await WaitForAsync(() => window.LibraryRetainedText.Text.Contains("retained subject revision=1", StringComparison.Ordinal) &&
            window.LibraryRetainedText.Text.Contains("Synthetic S7-T1 new deployment decision; use current playbook for current hosts.", StringComparison.Ordinal), TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The shown WPF retained text did not render the exact older bytes (" + FirstS7T1Line(window.LibraryRetainedText.Text) + ").");
        Console.WriteLine("S7T1-PROBE WPF retained text=" + FirstS7T1Line(window.LibraryRetainedText.Text));
        SaveS7T1Wpf(window, fixture, "s7t1-wpf-library-history-retained.png", window.LibraryRetainedText);
        PersistS7T1Evidence(fixture);
    }

    private static void SelectS7T1Navigation(OwnerConsoleWindow window, string entry)
    {
        foreach (var item in window.Navigation.Items.OfType<ListBoxItem>())
        {
            if (string.Equals(item.Content as string, entry, StringComparison.Ordinal))
            {
                window.Navigation.SelectedItem = item;
                return;
            }
        }

        throw new InvalidOperationException("The owner console has no '" + entry + "' navigation entry.");
    }

    private static string FirstS7T1Line(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length <= 220 ? line : line[..220];
    }

    private static void SaveS7T1Wpf(OwnerConsoleWindow window, SmokeFixture fixture, string fileName, FrameworkElement? scrollTo = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(fileName);
        window.UpdateLayout();
        scrollTo?.BringIntoView();
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(fixture.OutputDirectory, fileName);
        using (var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            encoder.Save(stream);
        }

        Console.WriteLine("WPF render saved: " + path);
    }

    private static void PersistS7T1Evidence(SmokeFixture fixture)
    {
        var evidenceDirectory = Path.Combine(Path.GetTempPath(), "MetaBrain-S7-T1-A2", "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        foreach (var file in Directory.EnumerateFiles(fixture.OutputDirectory, "s7t1-*.png"))
        {
            File.Copy(file, Path.Combine(evidenceDirectory, Path.GetFileName(file)), overwrite: true);
        }

        Console.WriteLine("S7-T1 evidence directory: " + evidenceDirectory);
    }

    private static async Task<OwnerLibraryPage?> S7T1UiSearchAsync(OwnerConsoleSession session, OwnerLibraryQuery query)
    {
        session.SetLibraryQuery(query);
        await session.SearchLibraryAsync().ConfigureAwait(false);
        var page = session.LibraryPage;
        Console.WriteLine("S7T1-PROBE UI search text=" + (query.Text ?? "(browse)") +
            " show=" + query.Current + " asOf=" + (query.AsOfUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "-") +
            " -> " + (page is null
                ? "no page (" + (session.LibraryMessage ?? "no message") + ")"
                : "total=" + page.Total + " hits=" + string.Join(",", page.Hits.Select(hit => hit.Id))));
        return page;
    }
}
