using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

// S2-T7: protected private SQLite index materialization/rebuild/reconciliation.
// One synthetic owner vault is provisioned and unlocked through the real owner
// CLI over the real named-pipe service. Two zones are populated with a
// registered source, a user question, an AI synthesis and an intention, which
// are saved, linked, revised and superseded. The index is then rebuilt,
// discarded on lock, rebuilt again, reconciled after revision moves, exercised
// under a concurrent writer, killed mid-rebuild and restarted, and every
// denial (locked vault, wrong key, tampered ciphertext, agent channel) is
// exercised. All evidence is observed from the real service and real persisted
// files; nothing here is a mock.
internal static partial class OwnerServiceSmoke
{
    private const string S2T7Source = "s2t7-src-one";
    private const string S2T7Question = "s2t7-question";
    private const string S2T7Synthesis = "s2t7-synthesis";
    private const string S2T7Intention = "s2t7-intention";
    private const string S2T7Relation = "rel-s2t7-supports";
    private const string S2T7Lifecycle = "lc-s2t7-synthesis";
    private const string S2T7Archive = "arc-s2t7-synthesis-r1";
    private const string S2T7ZoneTwo = "s2t7-zone-beta";
    private const string S2T7PrivateMarker = "Synthetic S2-T7 private index marker";

    /// <summary>
    /// The canonical fixture, read back through the real owner read operation
    /// that addresses each record: a lifecycle record is derived from its
    /// subject, and a retained revision archive is addressed as a memory
    /// resource. The index must reproduce exactly these live revisions.
    /// </summary>
    private static readonly (string Id, string Command, string ReadAs)[] S2T7CanonicalReads = new[]
    {
        (S2T7Source, "source-read", S2T7Source),
        (S2T7Question, "memory-read", S2T7Question),
        (S2T7Synthesis, "memory-read", S2T7Synthesis),
        (S2T7Intention, "memory-read", S2T7Intention),
        (S2T7Relation, "relation-read", S2T7Relation),
        (S2T7Lifecycle, "lifecycle-read", S2T7Synthesis),
        (S2T7Archive, "memory-read", S2T7Archive)
    };

    internal static async Task RunS2T7PrivateIndexAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S2-T7 private index smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S2-T7 protected private SQLite index, rebuild and reconciliation");
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
                "Provision did not create the S2-T7 synthetic vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");
            var unlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0 && unlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S2-T7 vault did not unlock (exit=" + unlock.ExitCode + "; stderr=" + unlock.StandardError.Trim() + ").");

            service = await RunS2T7ScenarioAsync(connectionsExecutable, fixture, passphrase, service).ConfigureAwait(false);
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

        Console.WriteLine("LIMIT synthetic S2-T7 fixture only; the memory-only SQLite index holds derived canonical state in process memory and is discarded on lock/restart, so same-owner OS memory access stays outside the CR01 threat model exactly as for the unlocked data key; ranking/search and any agent index surface stay Sprint 4; no UI/MCP surface is exercised here.");
    }

    private static async Task<RunningService> RunS2T7ScenarioAsync(string executable, SmokeFixture fixture, string passphrase, RunningService service)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-30);

        // Phase 1 - canonical content in two zones: a registered source, a user
        // question, an AI synthesis and an intention. Every write goes through
        // the real owner CLI, so the encrypted canonical envelope is the only
        // place this state exists.
        await WriteS2T7ItemDraftAsync(executable, fixture, S2T7Question, S2T7Zone,
            S2T7ItemDraft("question", "user", "directWrite", "unverified", "Synthetic S2-T7 question title",
                "Synthetic S2-T7 question body; never a real owner secret.", now), 1).ConfigureAwait(false);
        await WriteS2T7ItemDraftAsync(executable, fixture, S2T7Synthesis, S2T7Zone,
            S2T7ItemDraft("synthesis", "assistant", "synthesis", "reportedSource", "Synthetic S2-T7 synthesis title",
                "Synthetic S2-T7 synthesis body; never a real owner secret.", now, level: "l2"), 1).ConfigureAwait(false);
        await WriteS2T7ItemDraftAsync(executable, fixture, S2T7Intention, S2T7ZoneTwo,
            S2T7ItemDraft("intention", "user", "directWrite", "unverified", "Synthetic S2-T7 intention title",
                "Synthetic S2-T7 intention body; never a real owner secret.", now), 1).ConfigureAwait(false);
        await WriteS2T7SourceDraftAsync(executable, fixture, S2T7Source, S2T7Zone,
            S2T7SourceDraft("Synthetic S2-T7 private source title", "s2t7-private-locator", "s2t7-source-v1", now), 1).ConfigureAwait(false);
        Console.WriteLine("PASS canonical source/question/synthesis/intention saved in two zones");

        // Phase 2 - links and lifecycle: the synthesis cites the registered
        // source through an accepted relation, and the synthesis carries a
        // lifecycle record plus a retained historical revision.
        await CreateS2T7RelationAsync(executable, fixture, S2T7Relation, S2T7Zone,
            S2T4RelationDraft("supports", "accepted", S2T7Synthesis, "memoryItem", 1L, null, S2T7Source, "registeredSource", 1L, new { start = 0L, end = 512L },
                "Synthetic S2-T7 support reason; never a real owner secret.", now)).ConfigureAwait(false);
        await CreateS2T7LifecycleAsync(executable, fixture, S2T7Synthesis,
            S2T7LifecycleDraft("Synthetic S2-T7 lifecycle reason; never a real owner secret.")).ConfigureAwait(false);
        await TransitionS2T7LifecycleAsync(executable, fixture, "lifecycle-retain", S2T7Synthesis, expectedRevision: 1).ConfigureAwait(false);
        Console.WriteLine("PASS synthesis linked to source with retained revision and lifecycle record");

        // Phase 3 - first materialization from canonical.
        var firstRebuild = await S2T7IndexRebuildAsync(executable, fixture).ConfigureAwait(false);
        Require(firstRebuild.ExitCode == 0 && firstRebuild.StandardOutput.Contains("INDEX rebuilt schema=1", StringComparison.Ordinal),
            "The first private index rebuild did not succeed (exit=" + firstRebuild.ExitCode + "; stdout=" + firstRebuild.StandardOutput.Trim() + "; stderr=" + firstRebuild.StandardError.Trim() + ").");
        var firstDigest = ParseS2T7Digest(firstRebuild.StandardOutput);
        var firstRows = ParseS2T7IndexRows(firstRebuild.StandardOutput);
        Require(firstRows.Count == 7,
            "The first materialization did not cover the canonical fixture exactly (rows=" + firstRows.Count + ", expected 7: question, synthesis, intention, source, relation, lifecycle record and retained archive).");
        Console.WriteLine($"PASS index materialized schema=1 rows={firstRows.Count} digest={firstDigest}");

        // Phase 4 - the materialized generation reproduces the canonical set.
        var canonical = await ReadS2T7CanonicalRevisionsAsync(executable, fixture, S2T7CanonicalReads).ConfigureAwait(false);
        Require(canonical.Count == S2T7CanonicalReads.Length,
            "The canonical read-back did not cover every fixture resource (found " + canonical.Count + " of " + S2T7CanonicalReads.Length + ").");
        foreach (var (resourceId, expected) in canonical)
        {
            Require(firstRows.TryGetValue(resourceId, out var row),
                "The materialized index omitted the canonical resource " + resourceId + ".");
            Require(row.Revision == expected,
                "The materialized index revision for " + resourceId + " (" + row.Revision.ToString(CultureInfo.InvariantCulture) +
                ") does not match canonical (" + expected.ToString(CultureInfo.InvariantCulture) + ").");
        }

        // Every materialized row must be a canonical resource: no invented IDs.
        foreach (var resourceId in firstRows.Keys)
        {
            Require(canonical.ContainsKey(resourceId),
                "The materialized index named a resource that canonical does not hold: " + resourceId);
        }

        Require(firstRows[S2T7Question].Kind == "memoryItem" && firstRows[S2T7Question].State == "question",
            "The index did not reproduce the question's kind/state (kind=" + firstRows[S2T7Question].Kind + ", state=" + firstRows[S2T7Question].State + ").");
        Require(firstRows[S2T7Synthesis].Kind == "memoryItem" && firstRows[S2T7Synthesis].State == "synthesis",
            "The index did not reproduce the AI synthesis's kind/state.");
        Require(firstRows[S2T7Intention].Kind == "memoryItem" && firstRows[S2T7Intention].State == "intention" && firstRows[S2T7Intention].Zone == S2T7ZoneTwo,
            "The index did not reproduce the intention's kind/state and zone scope.");
        Require(firstRows[S2T7Source].Kind == "registeredSource" && firstRows[S2T7Source].State == "available",
            "The index did not reproduce the registered source's availability state.");
        Require(firstRows[S2T7Relation].Kind == "relation" && firstRows[S2T7Relation].State == "accepted",
            "The index did not reproduce the relation state.");
        Require(firstRows[S2T7Relation].Links == S2T7Source + "," + S2T7Synthesis,
            "The index did not reproduce the relation's exact endpoint set (links=" + firstRows[S2T7Relation].Links + ").");
        Require(firstRows[S2T7Lifecycle].Kind == "lifecycle" && firstRows[S2T7Lifecycle].State == "proposal",
            "The index did not reproduce the lifecycle record publication state (kind=" + firstRows[S2T7Lifecycle].Kind +
            ", state=" + firstRows[S2T7Lifecycle].State + ").");
        Require(firstRows[S2T7Lifecycle].Links == string.Join(",", new[] { S2T7Archive, S2T7Synthesis }.OrderBy(id => id, StringComparer.Ordinal)),
            "The index did not reproduce the lifecycle subject and retained-archive references (links=" + firstRows[S2T7Lifecycle].Links + ").");
        Require(firstRows[S2T7Archive].Kind == "retainedRevision" && firstRows[S2T7Archive].State == "retained" &&
                firstRows[S2T7Archive].Links == S2T7Synthesis,
            "The index did not reproduce the retained-revision archive's subject reference.");
        Console.WriteLine("PASS index reproduces canonical IDs/revisions/zone scope/link/lifecycle/source/relation state");

        // Phase 5 - disposable index: discarding it changes no canonical
        // resource, and the next rebuild reproduces the identical generation.
        // The lock/unlock pair first lets the vault's own unlock-epoch write
        // land (a pre-existing canonical behaviour, not an index artefact), so
        // the file set compared below is the steady-state private set.
        await RunOwnerCommandAsync(executable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        await RunOwnerCommandWithInputAsync(executable,
            passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        var steadyFiles = ListS2T7PrivateFiles(fixture);
        var disposeRebuild = await S2T7IndexRebuildAsync(executable, fixture).ConfigureAwait(false);
        Require(disposeRebuild.ExitCode == 0, "A rebuild after a lock/unlock cycle failed (exit=" + disposeRebuild.ExitCode + "; stderr=" + disposeRebuild.StandardError.Trim() + ").");
        Require(ParseS2T7Digest(disposeRebuild.StandardOutput) == firstDigest,
            "A rebuild after the index was discarded produced a different generation fingerprint.");
        var disposeRows = ParseS2T7IndexRows(disposeRebuild.StandardOutput);
        Require(disposeRows.Count == firstRows.Count && disposeRows.All(entry => firstRows.TryGetValue(entry.Key, out var other) && other == entry.Value),
            "A rebuild after discarding the index did not reproduce the identical materialized rows.");
        var disposeFiles = ListS2T7PrivateFiles(fixture);
        var added = disposeFiles.Except(steadyFiles, StringComparer.Ordinal).ToList();
        var removed = steadyFiles.Except(disposeFiles, StringComparer.Ordinal).ToList();
        Require(added.Count == 0 && removed.Count == 0,
            "Discarding and rematerializing the index changed the persisted private file set (added=[" + string.Join(", ", added) +
            "]; removed=[" + string.Join(", ", removed) + "]).");
        Console.WriteLine($"PASS index disposable: discarded on lock, canonical unchanged, rebuild reproduces identical generation (rows={disposeRows.Count}, files={steadyFiles.Count})");

        // Phase 6 - revision and supersession: the retired generation is
        // detected as stale rather than trusted, and a rebuild picks up the new
        // canonical revisions.
        await ReviseS2T7ItemDraftAsync(executable, fixture, S2T7Synthesis, S2T7Zone,
            S2T7ItemDraft("synthesis", "assistant", "synthesis", "reportedSource", "Synthetic S2-T7 synthesis title two",
                "Synthetic S2-T7 synthesis body two; never a real owner secret.", now, level: "l2"),
            writeRevision: 1, expectedRevision: 2).ConfigureAwait(false);
        // Publish the proposal, then withdraw the publication: both are valid
        // recorded acts from the proposal state, and the withdrawal is the
        // supersession-style state change the retired index generation must
        // reconcile to.
        // The retain act already advanced the lifecycle record, so its live
        // revision is read from canonical rather than assumed.
        var lifecycleRead = await RunOwnerCommandAsync(executable,
            "owner", "lifecycle-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", S2T7Synthesis).ConfigureAwait(false);
        Require(lifecycleRead.ExitCode == 0, "The S2-T7 lifecycle record could not be read back (exit=" + lifecycleRead.ExitCode + "; stderr=" + lifecycleRead.StandardError.Trim() + ").");
        var lifecycleRevision = ParseS2T7Revision(lifecycleRead.StandardOutput);
        await TransitionS2T7LifecycleAsync(executable, fixture, "lifecycle-publish", S2T7Synthesis,
            expectedRevision: lifecycleRevision).ConfigureAwait(false);
        await TransitionS2T7LifecycleAsync(executable, fixture, "lifecycle-withdraw", S2T7Synthesis,
            expectedRevision: lifecycleRevision + 1,
            S2T7TransitionDraft("Synthetic S2-T7 withdrawal rationale; never a real owner secret.")).ConfigureAwait(false);
        var staleStatus = await S2T7IndexStatusAsync(executable, fixture).ConfigureAwait(false);
        Require(staleStatus.ExitCode == 0 && staleStatus.StandardOutput.Contains("INDEX status", StringComparison.Ordinal) &&
                staleStatus.StandardOutput.Contains("stale=yes", StringComparison.Ordinal),
            "A canonical revision did not mark the materialized index stale (exit=" + staleStatus.ExitCode + "; stdout=" + staleStatus.StandardOutput.Trim() + ").");
        var reconcileRebuild = await S2T7IndexRebuildAsync(executable, fixture).ConfigureAwait(false);
        Require(reconcileRebuild.ExitCode == 0, "The reconciliation rebuild failed (exit=" + reconcileRebuild.ExitCode + "; stderr=" + reconcileRebuild.StandardError.Trim() + ").");
        var reconciled = ParseS2T7IndexRows(reconcileRebuild.StandardOutput);
        Require(reconciled.TryGetValue(S2T7Synthesis, out var reconciledSynthesis) && reconciledSynthesis.Revision == 2,
            "The reconciliation rebuild did not pick up the revised canonical revision.");
        Require(reconciled.TryGetValue(S2T7Lifecycle, out var reconciledLifecycle) && reconciledLifecycle.Revision > 1,
            "The reconciliation rebuild did not pick up the lifecycle record's new revision.");
        Require(reconciled.TryGetValue(S2T7Lifecycle, out var withdrawnLifecycle) && withdrawnLifecycle.State == "withdrawn",
            "The reconciliation rebuild did not reproduce the withdrawn lifecycle state (state=" +
                (reconciled.TryGetValue(S2T7Lifecycle, out var state) ? state.State : "absent") + ").");
        var reconciledStatus = await S2T7IndexStatusAsync(executable, fixture).ConfigureAwait(false);
        Require(reconciledStatus.ExitCode == 0 && reconciledStatus.StandardOutput.Contains("stale=no", StringComparison.Ordinal),
            "A rebuilt index was still reported stale (stdout=" + reconciledStatus.StandardOutput.Trim() + ").");
        Console.WriteLine("PASS revision/supersession reconciled: retired generation stale=yes, rebuild picks up new revisions and state, status stale=no");

        // Phase 7 - concurrent writer during reindex. A real second owner write
        // commits canonical while the rebuild runs; the rebuild must either
        // publish a consistent generation or fail closed, never lose the
        // canonical commit and never publish a half generation.
        var questionRead = await RunOwnerCommandAsync(executable,
            "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", S2T7Question).ConfigureAwait(false);
        Require(questionRead.ExitCode == 0, "The question could not be read back before the concurrent write (exit=" + questionRead.ExitCode + ").");
        var questionRevision = ParseS2T7Revision(questionRead.StandardOutput);
        var rebuildTask = S2T7IndexRebuildAsync(executable, fixture);
        await ReviseS2T7ItemDraftAsync(executable, fixture, S2T7Question, S2T7Zone,
            S2T7ItemDraft("question", "user", "directWrite", "unverified", "Synthetic S2-T7 question title two",
                "Synthetic S2-T7 question body two; never a real owner secret.", now.AddMinutes(-1)),
            writeRevision: questionRevision, expectedRevision: questionRevision + 1).ConfigureAwait(false);
        var concurrentRebuild = await rebuildTask.ConfigureAwait(false);
        Require(concurrentRebuild.ExitCode == 0,
            "The concurrent rebuild failed (exit=" + concurrentRebuild.ExitCode + "; stderr=" + concurrentRebuild.StandardError.Trim() + ").");
        var concurrentStatus = await S2T7IndexStatusAsync(executable, fixture).ConfigureAwait(false);
        Require(concurrentStatus.ExitCode == 0, "The status read after the concurrent write failed (exit=" + concurrentStatus.ExitCode + ").");
        var concurrentRevision = await RunOwnerCommandAsync(executable,
            "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", S2T7Question).ConfigureAwait(false);
        Require(concurrentRevision.ExitCode == 0 && concurrentRevision.StandardOutput.Contains("revision=" + (questionRevision + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            "The canonical write committed during the concurrent rebuild did not survive (exit=" + concurrentRevision.ExitCode + "; stdout=" + concurrentRevision.StandardOutput.Trim() + ").");
        var finalRebuild = await S2T7IndexRebuildAsync(executable, fixture).ConfigureAwait(false);
        Require(finalRebuild.ExitCode == 0, "The post-concurrency rebuild failed (exit=" + finalRebuild.ExitCode + ").");
        var finalRows = ParseS2T7IndexRows(finalRebuild.StandardOutput);
        Require(finalRows.TryGetValue(S2T7Question, out var finalQuestion) && finalQuestion.Revision == questionRevision + 1,
            "The post-concurrency index generation did not carry the concurrently written canonical revision.");
        Console.WriteLine("PASS concurrent writer during reindex: canonical commit survived and index reconciled to it");

        // Phase 8 - real process interruption mid-rebuild: the service is
        // killed, restarted, and must come back locked, deny private reads,
        // and rebuild an identical generation after unlock.
        await StopS2T7ServiceAsync(service).ConfigureAwait(false);
        service = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
        var restartedStatus = await ReadStatusAsync(executable, fixture.ControlPipe).ConfigureAwait(false);
        Require(restartedStatus.Contains("vault=locked", StringComparison.Ordinal),
            "The restarted service did not return locked (" + restartedStatus + ").");
        var lockedIndex = await S2T7IndexStatusAsync(executable, fixture).ConfigureAwait(false);
        Require(lockedIndex.ExitCode == 3 && lockedIndex.StandardError.Contains("vault_locked", StringComparison.Ordinal),
            "An index read succeeded while the vault was locked (exit=" + lockedIndex.ExitCode + "; stderr=" + lockedIndex.StandardError.Trim() + ").");
        await RunOwnerCommandWithInputAsync(executable,
            passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        var afterRestart = await S2T7IndexRebuildAsync(executable, fixture).ConfigureAwait(false);
        Require(afterRestart.ExitCode == 0, "A rebuild after restart/unlock failed (exit=" + afterRestart.ExitCode + ").");
        Require(ParseS2T7Digest(afterRestart.StandardOutput) == ParseS2T7Digest(finalRebuild.StandardOutput),
            "The generation fingerprint changed across a forced service interruption.");
        var afterRestartRows = ParseS2T7IndexRows(afterRestart.StandardOutput);
        Require(afterRestartRows.Count == finalRows.Count && afterRestartRows.All(entry => finalRows.TryGetValue(entry.Key, out var other) && other == entry.Value),
            "The rebuild after the forced interruption did not reproduce the pre-interruption generation.");
        Console.WriteLine($"PASS forced interruption: restart locked, private index read denied, rebuild reproduces generation (rows={afterRestartRows.Count})");

        // Phase 9 - key and ciphertext denials. Lock first so the wrong-key
        // probe actually exercises the KDF/unwrap path (an already-unlocked
        // vault short-circuits before any credential is checked). With a real
        // wrong passphrase denied, a tampered canonical envelope must make the
        // rebuild fail closed and publish no generation at all, rather than
        // materializing a partial index over tampered ciphertext.
        await RunOwnerCommandAsync(executable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        var wrongKey = await RunOwnerCommandWithInputAsync(executable,
            "wrong synthetic S2-T7 passphrase" + Environment.NewLine,
            "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(wrongKey.ExitCode == 3 && wrongKey.StandardError.Contains("unlock_denied", StringComparison.Ordinal),
            "A wrong passphrase unlocked the locked S2-T7 vault (exit=" + wrongKey.ExitCode + "; stderr=" + wrongKey.StandardError.Trim() + ").");
        var lockedRebuild = await S2T7IndexRebuildAsync(executable, fixture).ConfigureAwait(false);
        Require(lockedRebuild.ExitCode == 3 && lockedRebuild.StandardError.Contains("vault_locked", StringComparison.Ordinal),
            "An index rebuild succeeded while the vault was locked (exit=" + lockedRebuild.ExitCode + "; stderr=" + lockedRebuild.StandardError.Trim() + ").");
        var correctUnlock = await RunOwnerCommandWithInputAsync(executable,
            passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(correctUnlock.ExitCode == 0 && correctUnlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
            "The correct passphrase did not unlock after the wrong-key probe (exit=" + correctUnlock.ExitCode + ").");
        var canonicalFiles = Directory.GetFiles(fixture.VaultResourcesPath, "*.mbv", SearchOption.TopDirectoryOnly);
        Require(canonicalFiles.Length >= 1, "The S2-T7 vault persisted no canonical ciphertext to tamper.");
        var original = await File.ReadAllBytesAsync(canonicalFiles[0]).ConfigureAwait(false);
        try
        {
            await MutateS2T7AuthenticatedEnvelopeAsync(canonicalFiles[0], original).ConfigureAwait(false);
            var tamperedRebuild = await S2T7IndexRebuildAsync(executable, fixture).ConfigureAwait(false);
            Require(tamperedRebuild.ExitCode == 3,
                "An index rebuild succeeded over a tampered canonical ciphertext (exit=" + tamperedRebuild.ExitCode + "; stdout=" + tamperedRebuild.StandardOutput.Trim() + ").");
            var tamperedStatus = await S2T7IndexStatusAsync(executable, fixture).ConfigureAwait(false);
            Require(tamperedStatus.ExitCode == 0 && ParseS2T7RowCount(tamperedStatus.StandardOutput) == 0,
                "The index published or served a materialization while the canonical ciphertext was tampered (stdout=" + tamperedStatus.StandardOutput.Trim() + ").");
        }
        finally
        {
            if (File.Exists(canonicalFiles[0]))
            {
                await File.WriteAllBytesAsync(canonicalFiles[0], original).ConfigureAwait(false);
            }

            CryptographicOperations.ZeroMemory(original);
        }

        var restoredRebuild = await S2T7IndexRebuildAsync(executable, fixture).ConfigureAwait(false);
        Require(restoredRebuild.ExitCode == 0, "A rebuild after restoring canonical ciphertext failed (exit=" + restoredRebuild.ExitCode + "; stderr=" + restoredRebuild.StandardError.Trim() + ").");
        Require(ParseS2T7Digest(restoredRebuild.StandardOutput) == ParseS2T7Digest(finalRebuild.StandardOutput),
            "The index generation changed after restoring the tampered canonical ciphertext.");
        Console.WriteLine("PASS wrong key denied on locked vault, tampered canonical ciphertext fails the rebuild closed with no generation published");

        // Phase 10 - scoped negatives. The index is not an agent surface: both
        // index operations are rejected on the agent channel, a granted agent
        // still cannot read outside its approved scope, and a frozen grant
        // denies both the superseded and the newer revision of a resource it
        // was approved for before that revision moved.
        var grant = await IssueS2T7GrantAsync(executable, fixture, "s2t7-agent", "resource.read,memory.read",
            resourceIds: new[] { S2T7Intention }).ConfigureAwait(false);
        var agentView = await AgentS2T6CollectionViewAsync(executable, fixture, grant).ConfigureAwait(false);
        Require(agentView.ExitCode == 0, "The S2-T7 synthetic agent could not view its approved scope (exit=" + agentView.ExitCode + "; stderr=" + agentView.StandardError.Trim() + ").");
        foreach (var command in new[] { "index-status", "index-rebuild" })
        {
            var agentIndex = await RunOwnerCommandAsync(executable,
                "owner", command, "--control-pipe", fixture.AgentPipe).ConfigureAwait(false);
            Require(agentIndex.ExitCode == 3,
                "The owner index operation " + command + " was accepted on the agent channel (exit=" + agentIndex.ExitCode + "; stderr=" + agentIndex.StandardError.Trim() + ").");
        }

        // Positive control: the granted resource at its frozen revision is
        // still served, so the negatives below are scope denials and not a
        // broken grant.
        var grantedRead = await RunOwnerCommandAsync(executable,
            "owner", "agent-read", "--agent-pipe", fixture.AgentPipe,
            "--session-file", grant.SessionFile, "--operation", "resource.read",
            "--resource-id", S2T7Intention, "--resource-revision", "1").ConfigureAwait(false);
        Require(grantedRead.ExitCode == 0 && grantedRead.StandardOutput.Contains("AGENT-BODY-BEGIN", StringComparison.Ordinal),
            "The synthetic agent could not read the resource it was granted (exit=" + grantedRead.ExitCode + "; stderr=" + grantedRead.StandardError.Trim() + ").");
        var agentReadSynthesis = await RunOwnerCommandAsync(executable,
            "owner", "agent-read", "--agent-pipe", fixture.AgentPipe,
            "--session-file", grant.SessionFile, "--operation", "resource.read",
            "--resource-id", S2T7Synthesis,
            "--resource-revision", "1").ConfigureAwait(false);
        Require(agentReadSynthesis.ExitCode == 4 && agentReadSynthesis.StandardOutput.Length == 0,
            "The synthetic agent read a resource outside its approved scope (exit=" + agentReadSynthesis.ExitCode + "; stderr=" + agentReadSynthesis.StandardError.Trim() + ").");
        var agentNewRevision = await RunOwnerCommandAsync(executable,
            "owner", "agent-read", "--agent-pipe", fixture.AgentPipe,
            "--session-file", grant.SessionFile, "--operation", "resource.read",
            "--resource-id", S2T7Question,
            "--resource-revision", (questionRevision + 1).ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        Require(agentNewRevision.ExitCode == 4 && agentNewRevision.StandardOutput.Length == 0,
            "A frozen grant read the newer canonical revision it was never approved for (exit=" + agentNewRevision.ExitCode + ").");
        var agentOldRevision = await RunOwnerCommandAsync(executable,
            "owner", "agent-read", "--agent-pipe", fixture.AgentPipe,
            "--session-file", grant.SessionFile, "--operation", "resource.read",
            "--resource-id", S2T7Question,
            "--resource-revision", questionRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        Require(agentOldRevision.ExitCode == 4 && agentOldRevision.StandardOutput.Length == 0,
            "A frozen grant read the superseded canonical revision it was never approved for (exit=" + agentOldRevision.ExitCode + ").");
        Console.WriteLine("PASS scoped negatives: agent channel has no index surface and grants still gate old/new revisions");

        // Phase 11 - spill boundary and ciphertext-only persistence. Every
        // private file under the vault, its resource directory, the sibling
        // service directory and the owner output directory is scanned for the
        // synthetic private marker, and no SQLite sidecar exists anywhere.
        VerifyS2T7NoPrivateIndexSpill(fixture, "Synthetic S2-T7", "s2t7-private-locator", S2T7PrivateMarker, passphrase);
        var resources = Directory.GetFiles(fixture.VaultResourcesPath, "*.mbv", SearchOption.TopDirectoryOnly);
        Require(resources.Length == 7,
            "The S2-T7 vault did not persist exactly the expected canonical ciphertext set (found " + resources.Length + ", expected 7).");
        foreach (var path in resources)
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            Require(text.Contains("\"ciphertext\"", StringComparison.Ordinal) && text.Contains("\"nonce\"", StringComparison.Ordinal),
                "A S2-T7 canonical resource is not in the authenticated envelope form: " + Path.GetFileName(path));
        }

        Console.WriteLine($"PASS no plaintext private index/temp spill; canonical ciphertext envelopes intact ({resources.Length} resources)");
        return service;
    }

    private static async Task StopS2T7ServiceAsync(RunningService? service)
    {
        if (service is null)
        {
            return;
        }

        await StopServiceAsync(service).ConfigureAwait(false);
    }
}
