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
    private const string S7T4Zone = "s7t4-forget-zone";
    private const string S7T4RareLessonId = "s7t4-rare-lesson";
    private const string S7T4RareLessonTitle = "S7T4_SYNTHETIC_RARE_LESSON_TITLE_5b71";
    private const string S7T4RareLessonBody = "S7T4_SYNTHETIC_RARE_LESSON_BODY_9c02: the owner keeps one rarely used lesson about a retired write-ahead log format.";
    private const string S7T4NoisyItemId = "s7t4-noisy-item";
    private const string S7T4NoisyItemTitle = "S7T4_SYNTHETIC_NOISY_ITEM_TITLE_3a6e";
    private const string S7T4NoisyItemBody = "S7T4_SYNTHETIC_NOISY_ITEM_BODY_7f48: a repeatedly read note that carries no new information for the owner.";
    private const string S7T4SharedToken = "S7T4_SHARED_RECALL_TOKEN_4d17";
    private const string S7T4OtherZoneId = "s7t4-other-zone-item";
    private const string S7T4OtherZoneBody = "S7T4_SYNTHETIC_OTHER_ZONE_BODY_2b90: an item in a second zone that the frozen scope must never reach.";
    private const string S7T4DemoteReason = "S7T4_SYNTHETIC_DEMOTE_REASON_6d52: the owner reads this often and it never changed anything.";
    private const string S7T4ArchiveReason = "S7T4_SYNTHETIC_ARCHIVE_REASON_8e13: the owner set this aside and keeps it for the record.";

    /// <summary>
    /// S7-T4 runtime smoke: the owner's recall policy end to end.
    /// <para>
    /// The scenario proves the acceptance criteria against the real service,
    /// the real MCP bridge and the real WPF console: a rarely read but
    /// important lesson stays findable and can be protected, a repeatedly read
    /// noisy item is not strengthened by being read again, an explicit owner
    /// demote lowers only its priority, an explicit archive takes it out of the
    /// default recall and keeps it reachable through an explicit expansion, a
    /// restore brings it back without widening any scope, and the read loop
    /// never raises an item's truth.
    /// </para>
    /// </summary>
    internal static async Task RunS7T4ForgettingAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S7-T4 forgetting smoke requires Windows named pipes, DPAPI and WPF.");
        }

        Console.WriteLine("RUN S7-T4 owner recall policy: demote, pin, archive and restore through scoped MCP queries and actual WPF controls");
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
                "The synthetic S7-T4 vault was not provisioned.");
            recoveryCode = ParseRecoveryCode(provisioned.StandardOutput);
            var unlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S7-T4 vault did not unlock.");

            await WriteS7T4ItemAsync(connectionsExecutable, fixture, S7T4RareLessonId, S7T4RareLessonTitle, S7T4RareLessonBody).ConfigureAwait(false);
            await WriteS7T4ItemAsync(connectionsExecutable, fixture, S7T4NoisyItemId, S7T4NoisyItemTitle, S7T4NoisyItemBody).ConfigureAwait(false);
            await WriteS7T4ItemAsync(connectionsExecutable, fixture, S7T4OtherZoneId, "S7T4_SYNTHETIC_OTHER_ZONE_TITLE_1f77", S7T4OtherZoneBody).ConfigureAwait(false);

            // The private index must exist before the owner can search at all.
            var rebuilt = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(rebuilt.ExitCode == 0, "The owner could not materialize the S7-T4 library index (" + rebuilt.StandardError.Trim() + ").");

            var session = await IssueS7T4GrantAsync(connectionsExecutable, fixture, "s7t4-forget",
                new[] { S7T4RareLessonId, S7T4NoisyItemId }).ConfigureAwait(false);

            // The baseline: both approved items are findable through the real
            // MCP bridge, and the item outside the frozen scope is not.
            var baseline = await RunS7T4SearchAsync(connectionsExecutable, fixture, session, includeArchived: false).ConfigureAwait(false);
            Require(baseline.Contains($"HIT id={S7T4RareLessonId}", StringComparison.Ordinal) &&
                    baseline.Contains($"HIT id={S7T4NoisyItemId}", StringComparison.Ordinal) &&
                    !baseline.Contains(S7T4OtherZoneId, StringComparison.Ordinal),
                "The baseline scoped MCP search did not serve exactly the two approved items (" + baseline.Trim() + ").");

            // The read loop must not strengthen anything: reading the noisy item
            // again and again leaves its evidence, its verification and its
            // place in the ranking exactly as they were.
            var beforeReads = await RunS7T4SearchAsync(connectionsExecutable, fixture, session, includeArchived: false).ConfigureAwait(false);
            for (var read = 0; read < 5; read++)
            {
                var repeated = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                    "metabrain_memory_read", new
                    {
                        sessionFile = session.SessionFile,
                        resourceId = S7T4NoisyItemId,
                        resourceRevision = "1",
                    }).ConfigureAwait(false);
                Require(repeated.Contains(S7T4NoisyItemBody, StringComparison.Ordinal),
                    "The repeated scoped read of the noisy item did not serve its stored body.");
            }

            var afterReads = await RunS7T4SearchAsync(connectionsExecutable, fixture, session, includeArchived: false).ConfigureAwait(false);
            Require(afterReads == beforeReads,
                "Reading the same item repeatedly changed what the scoped recall served (" + afterReads.Trim() + " vs " + beforeReads.Trim() + ").");
            var noisyDetail = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T4NoisyItemId).ConfigureAwait(false);
            Require(noisyDetail.ExitCode == 0 && noisyDetail.StandardOutput.Contains(S7T4NoisyItemBody, StringComparison.Ordinal) &&
                    noisyDetail.StandardOutput.Contains("evidence=unverified", StringComparison.Ordinal) &&
                    noisyDetail.StandardOutput.Contains("revision=1", StringComparison.Ordinal),
                "The repeated read loop raised the noisy item's evidence or verification (" + noisyDetail.StandardOutput.Trim() + ").");

            // An explicit owner demote lowers only the priority of the noisy
            // item. The reason the owner gave is recorded, and the item is
            // still findable.
            var demoted = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "recall-apply", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T4NoisyItemId, "--act", "demote", "--reason", S7T4DemoteReason).ConfigureAwait(false);
            Require(demoted.ExitCode == 0 && demoted.StandardOutput.Contains("state=demoted", StringComparison.Ordinal) &&
                    demoted.StandardOutput.Contains(S7T4DemoteReason, StringComparison.Ordinal),
                "The owner could not demote the noisy item with a reason (" + demoted.StandardError.Trim() + ").");

            var demotedSearch = await RunS7T4SearchAsync(connectionsExecutable, fixture, session, includeArchived: false).ConfigureAwait(false);
            Require(demotedSearch.Contains($"HIT id={S7T4NoisyItemId}", StringComparison.Ordinal) &&
                    demotedSearch.Contains("recall=demoted", StringComparison.Ordinal) &&
                    demotedSearch.Contains("demoted by the owner", StringComparison.Ordinal),
                "A demoted item was not offered with its explicit owner reason (" + demotedSearch.Trim() + ").");
            Require(HasHitBefore(demotedSearch, S7T4RareLessonId, S7T4NoisyItemId),
                "The demoted noisy item was not ranked below the ordinary rare lesson (" + demotedSearch.Trim() + ").");

            // The owner's own decision is readable back with the reason and the
            // recorded act history, so the ranking can always be explained.
            var recallRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "recall-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T4NoisyItemId).ConfigureAwait(false);
            Require(recallRead.ExitCode == 0 && recallRead.StandardOutput.Contains("state=demoted", StringComparison.Ordinal) &&
                    recallRead.StandardOutput.Contains(S7T4DemoteReason, StringComparison.Ordinal) &&
                    recallRead.StandardOutput.Contains("ACT from=normal to=demoted", StringComparison.Ordinal),
                "The owner could not read back the recorded recall decision and its act history (" + recallRead.StandardError.Trim() + ").");

            // The agent surface offers no recall act at all: the forgetting
            // policy is the owner's own decision and no agent can change it.
            var tools = await RunBridgeToolsListAsync(connectionsExecutable, fixture.AgentPipe).ConfigureAwait(false);
            Require(!tools.Contains("\"name\":\"metabrain_recall", StringComparison.OrdinalIgnoreCase) &&
                    !tools.Contains("\"name\":\"metabrain_forget", StringComparison.OrdinalIgnoreCase) &&
                    !tools.Contains("\"name\":\"metabrain_archive", StringComparison.OrdinalIgnoreCase) &&
                    !tools.Contains("\"name\":\"metabrain_demote", StringComparison.OrdinalIgnoreCase),
                "The real MCP bridge offered a recall-policy act to the agent channel (" + tools.Trim() + ").");

            // The rarely read lesson is protected with an explicit owner pin,
            // and it stays findable. A pinned item cannot then be demoted or
            // set aside without being released first.
            var pinned = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "recall-apply", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T4RareLessonId, "--act", "pin", "--reason", "S7T4_SYNTHETIC_PIN_REASON_1f04: rare but important lesson.").ConfigureAwait(false);
            Require(pinned.ExitCode == 0 && pinned.StandardOutput.Contains("state=pinned", StringComparison.Ordinal),
                "The owner could not pin the rare lesson (" + pinned.StandardError.Trim() + ").");

            var pinnedSearch = await RunS7T4SearchAsync(connectionsExecutable, fixture, session, includeArchived: false).ConfigureAwait(false);
            Require(pinnedSearch.Contains($"HIT id={S7T4RareLessonId}", StringComparison.Ordinal) &&
                    pinnedSearch.Contains("recall=pinned", StringComparison.Ordinal) &&
                    pinnedSearch.Contains("pinned by the owner", StringComparison.Ordinal),
                "The pinned rare lesson was not offered with its explicit owner reason (" + pinnedSearch.Trim() + ").");
            Require(HasHitBefore(pinnedSearch, S7T4RareLessonId, S7T4NoisyItemId),
                "The pinned rare lesson was not ranked above the demoted noisy item (" + pinnedSearch.Trim() + ").");

            var protectedDemote = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "recall-apply", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T4RareLessonId, "--act", "demote", "--reason", S7T4DemoteReason).ConfigureAwait(false);
            Require(protectedDemote.ExitCode != 0,
                "A protected rare lesson accepted a demote without being released first.");
            var protectedArchive = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "recall-apply", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T4RareLessonId, "--act", "archive", "--reason", S7T4ArchiveReason).ConfigureAwait(false);
            Require(protectedArchive.ExitCode != 0,
                "A protected rare lesson was set aside without being released first.");

            // An explicit archive takes the noisy item out of the default
            // recall and keeps it reachable through an explicit expansion.
            var archived = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "recall-apply", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T4NoisyItemId, "--act", "archive", "--reason", S7T4ArchiveReason).ConfigureAwait(false);
            Require(archived.ExitCode == 0 && archived.StandardOutput.Contains("state=archived", StringComparison.Ordinal) &&
                    archived.StandardOutput.Contains(S7T4ArchiveReason, StringComparison.Ordinal),
                "The owner could not set the noisy item aside with a reason (" + archived.StandardError.Trim() + ").");

            var archivedSearch = await RunS7T4SearchAsync(connectionsExecutable, fixture, session, includeArchived: false).ConfigureAwait(false);
            Require(!archivedSearch.Contains($"HIT id={S7T4NoisyItemId}", StringComparison.Ordinal) &&
                    archivedSearch.Contains($"HIT id={S7T4RareLessonId}", StringComparison.Ordinal),
                "An archived item was still offered by the default scoped recall (" + archivedSearch.Trim() + ").");

            var expandedSearch = await RunS7T4SearchAsync(connectionsExecutable, fixture, session, includeArchived: true).ConfigureAwait(false);
            Require(expandedSearch.Contains($"HIT id={S7T4NoisyItemId}", StringComparison.Ordinal) &&
                    expandedSearch.Contains("recall=archived", StringComparison.Ordinal) &&
                    expandedSearch.Contains("archived by the owner", StringComparison.Ordinal),
                "An explicit archive expansion did not serve the archived item with its reason (" + expandedSearch.Trim() + ").");
            Require(!expandedSearch.Contains(S7T4OtherZoneId, StringComparison.Ordinal),
                "The archive expansion reached an item outside the frozen scope (" + expandedSearch.Trim() + ").");

            // The brief is the second recall surface: an item the owner set
            // aside is not projected, but it is named in the omission trace
            // rather than disappearing silently, and an explicit expansion
            // projects it again.
            var briefSession = await IssueS7T4GrantAsync(connectionsExecutable, fixture, "s7t4-brief",
                new[] { S7T4RareLessonId, S7T4NoisyItemId }, "memory.context,memory.read").ConfigureAwait(false);
            var brief = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-brief", "--agent-pipe", fixture.AgentPipe,
                "--session-file", briefSession.SessionFile,
                "--query", S7T4SharedToken, "--budget-chars", "8000").ConfigureAwait(false);
            Require(brief.ExitCode == 0 &&
                    !brief.StandardOutput.Contains($"ENTRY id={S7T4NoisyItemId}", StringComparison.Ordinal) &&
                    brief.StandardOutput.Contains($"OMITTED id={S7T4NoisyItemId}", StringComparison.Ordinal) &&
                    brief.StandardOutput.Contains("reason=owner_archived", StringComparison.Ordinal) &&
                    brief.StandardOutput.Contains($"ENTRY id={S7T4RareLessonId}", StringComparison.Ordinal),
                "The scoped brief did not keep the owner's set-aside item out of the projection while naming it in the omission trace (" + brief.StandardOutput.Trim() + ").");
            var expandedBrief = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-brief", "--agent-pipe", fixture.AgentPipe,
                "--session-file", briefSession.SessionFile,
                "--query", S7T4SharedToken, "--budget-chars", "8000",
                "--include-archived", "true").ConfigureAwait(false);
            Require(expandedBrief.ExitCode == 0 &&
                    expandedBrief.StandardOutput.Contains($"ENTRY id={S7T4NoisyItemId}", StringComparison.Ordinal) &&
                    expandedBrief.StandardOutput.Contains("recall=archived", StringComparison.Ordinal),
                "An explicit archive expansion did not project the set-aside item in the scoped brief (" + expandedBrief.StandardOutput.Trim() + ").");

            // The archived item is still in the vault and still readable by the
            // scope that already had it: archiving changes prominence, never
            // access.
            var archivedRead = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_memory_read", new
                {
                    sessionFile = session.SessionFile,
                    resourceId = S7T4NoisyItemId,
                    resourceRevision = "1",
                }).ConfigureAwait(false);
            Require(archivedRead.Contains(S7T4NoisyItemBody, StringComparison.Ordinal),
                "An archived item stopped being readable through the scope that already had it.");

            // An explicit restore brings the item back to the default recall.
            var restored = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "recall-apply", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S7T4NoisyItemId, "--act", "restore").ConfigureAwait(false);
            Require(restored.ExitCode == 0 && restored.StandardOutput.Contains("state=normal", StringComparison.Ordinal),
                "The owner could not restore the archived item (" + restored.StandardError.Trim() + ").");

            var restoredSearch = await RunS7T4SearchAsync(connectionsExecutable, fixture, session, includeArchived: false).ConfigureAwait(false);
            Require(restoredSearch.Contains($"HIT id={S7T4NoisyItemId}", StringComparison.Ordinal) &&
                    !restoredSearch.Contains("recall=archived", StringComparison.Ordinal),
                "The restored item did not return to the default scoped recall (" + restoredSearch.Trim() + ").");

            // A restore returns visibility only. A fresh session that was never
            // granted the item must still not see it.
            var narrowSession = await IssueS7T4GrantAsync(connectionsExecutable, fixture, "s7t4-narrow",
                new[] { S7T4RareLessonId }).ConfigureAwait(false);
            var narrowSearch = await RunS7T4SearchAsync(connectionsExecutable, fixture, narrowSession, includeArchived: true).ConfigureAwait(false);
            Require(!narrowSearch.Contains(S7T4NoisyItemId, StringComparison.Ordinal),
                "Restoring an item put it into a scope that never had it (" + narrowSearch.Trim() + ").");

            // The recall policy never changes what an item says.
            var noisyAfterActs = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T4NoisyItemId).ConfigureAwait(false);
            Require(noisyAfterActs.ExitCode == 0 &&
                    noisyAfterActs.StandardOutput.Contains(S7T4NoisyItemBody, StringComparison.Ordinal) &&
                    noisyAfterActs.StandardOutput.Contains("revision=1", StringComparison.Ordinal),
                "The recall acts changed the stored body or revision of the item.");

            // The actual WPF owner console performs the same acts on the open
            // item and shows the owner's own decision and reason.
            DriveS7T4Wpf(fixture, connectionsExecutable);
            var evidenceDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts", "sprint-7", "evidence");
            Directory.CreateDirectory(evidenceDirectory);
            foreach (var name in new[] { "s7t4-wpf-recall-policy.png", "s7t4-wpf-recall-expansion.png" })
            {
                var rendered = Path.Combine(fixture.OutputDirectory, name);
                Require(File.Exists(rendered), "The actual shown WPF recall-policy evidence was not rendered: " + name);
                File.Copy(rendered, Path.Combine(evidenceDirectory, name), overwrite: true);
                Console.WriteLine("S7T4 WPF visual evidence: " + Path.Combine(evidenceDirectory, name));
            }

            // The private state stays private: no recall content, reason or
            // disposition is written in plaintext beside the encrypted vault.
            VerifyS7T4EncryptedState(fixture, passphrase, recoveryCode);

            Console.WriteLine("PASS S7-T4 owner recall policy: explicit demote, pin protection, archive with expansion, restore without scope change, no read-loop reinforcement, real MCP queries and real WPF controls");
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

    private static async Task WriteS7T4ItemAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string title,
        string body)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s7t4-item-" + resourceId + ".json");
        var recordedAt = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new
        {
            kind = "lesson",
            author = "user",
            creationMethod = "directWrite",
            evidence = "unverified",
            level = "l1",
            title,
            body = body + " " + S7T4SharedToken,
            sources = Array.Empty<object>(),
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = recordedAt,
            applicability = (object?)null,
        }), Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", S7T4Zone,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
            "The synthetic S7-T4 item " + resourceId + " was not stored (" + result.StandardError.Trim() + ").");
    }

    private static async Task<S4T4Session> IssueS7T4GrantAsync(
        string executable,
        SmokeFixture fixture,
        string name,
        string[] resourceIds,
        string operations = "memory.read,memory.search")
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
            "The owner could not issue the S7-T4 scoped grant (" + grant.StandardError.Trim() + ").");
        // A scoped search grant carries only the memory read operations: the
        // service refuses a search grant that also carries the context
        // operation, because a search scope must never widen into another
        // projection's rights. The brief therefore needs its own grant.
        VerifyOwnerHandoffProtection(handoff);
        var redeem = await RunOwnerCommandAsync(executable,
            "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
            "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
        Require(redeem.ExitCode == 0 && redeem.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
            "The S7-T4 scoped grant could not be redeemed (" + redeem.StandardError.Trim() + ").");
        VerifyOwnerHandoffProtection(sessionFile);
        return new S4T4Session(ParseSessionId(redeem.StandardOutput), sessionFile);
    }

    private static async Task<string> RunS7T4SearchAsync(
        string executable,
        SmokeFixture fixture,
        S4T4Session session,
        bool includeArchived)
    {
        var response = await RunBridgeToolAsync(executable, fixture.AgentPipe,
            "metabrain_memory_search", new
            {
                sessionFile = session.SessionFile,
                query = S7T4SharedToken,
                limit = "20",
                includeArchived = includeArchived ? "true" : "false",
            }).ConfigureAwait(false);
        using var document = JsonDocument.Parse(response);
        return document.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()
            ?? string.Empty;
    }

    /// <summary>
    /// Whether the first named hit of a scoped search page comes before the
    /// second one. The page is the service's own order, so this reads the
    /// ranking the service actually applied.
    /// </summary>
    private static bool HasHitBefore(string searchText, string firstId, string secondId)
    {
        var first = searchText.IndexOf("HIT id=" + firstId, StringComparison.Ordinal);
        var second = searchText.IndexOf("HIT id=" + secondId, StringComparison.Ordinal);
        return first >= 0 && second >= 0 && first < second;
    }

    private static void DriveS7T4Wpf(SmokeFixture fixture, string connectionsExecutable)
    {
        var session = new OwnerConsoleSession(fixture.ControlPipe);
        session.AttachAsync().GetAwaiter().GetResult();
        Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
            "The actual S7-T4 owner session did not attach to the unlocked service.");
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
                        await S7T4WpfInteractionsAsync(activeWindow, session, fixture, connectionsExecutable).ConfigureAwait(true);
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
            throw new InvalidOperationException("The shown S7-T4 WPF recall-policy surface failed: " + failure, failure);
        }
    }

    private static async Task S7T4WpfInteractionsAsync(
        OwnerConsoleWindow window,
        OwnerConsoleSession session,
        SmokeFixture fixture,
        string connectionsExecutable)
    {
        SelectS7T2Navigation(window, "Library");
        await session.RebuildLibraryIndexAsync().ConfigureAwait(true);
        Require(await WaitForAsync(() => !session.LibraryActInFlight, TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The actual WPF library did not finish its index rebuild.");

        window.LibraryQueryInput.Text = S7T4SharedToken;
        window.LibrarySearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.LibraryPage is not null && session.LibraryPage.Hits.Length > 0,
            TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The actual WPF library search did not return a page.");
        var noisyHit = session.LibraryPage!.Hits.FirstOrDefault(hit => hit.Id == S7T4NoisyItemId);
        Require(noisyHit is not null,
            "The actual WPF library page did not list the item the owner had restored.");

        await session.OpenLibraryItemAsync(noisyHit!).ConfigureAwait(true);
        Require(await WaitForAsync(() => session.LibraryItem?.Id == S7T4NoisyItemId, TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The actual WPF library did not open the item at its exact revision.");

        // The console protects an item with an explicit owner pin, and it
        // refuses to offer to set a protected item aside.
        window.LibraryRecallReasonInput.Text = "S7T4_SYNTHETIC_WPF_PIN_REASON_0a7c: keep this one findable.";
        Require(window.LibraryRecallPinButton.IsEnabled,
            "The actual WPF console did not enable the pin control for the open item.");
        window.LibraryRecallPinButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.LibraryRecall?.Disposition == "pinned", TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The actual WPF pin control did not persist the owner's protection.");
        Require(!window.LibraryRecallArchiveButton.IsEnabled,
            "The actual WPF console offered to set aside a protected item.");
        SaveS7T2Wpf(window, fixture, "s7t4-wpf-recall-policy.png", window.LibraryRecallPinButton);

        // The owner releases that protection, and then sets the item aside
        // with an explicit reason.
        window.LibraryRecallRestoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.LibraryRecall?.Disposition == "normal", TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The actual WPF restore control did not release the owner's protection.");
        window.LibraryRecallReasonInput.Text = "S7T4_SYNTHETIC_WPF_ARCHIVE_REASON_5b31: set aside, kept for the record.";
        Require(window.LibraryRecallArchiveButton.IsEnabled,
            "The actual WPF console did not offer to set the released item aside.");
        window.LibraryRecallArchiveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.LibraryRecall?.Disposition == "archived", TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The actual WPF console did not persist the owner's decision to set the item aside.");

        // The default library search no longer offers it, and the explicit
        // expansion reaches it again. Each search is awaited as a new page:
        // the console answers asynchronously, so the previous page is not the
        // answer to the new question.
        var pageBeforeDefault = session.LibraryPage;
        window.LibrarySearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.LibraryPage is not null && !ReferenceEquals(session.LibraryPage, pageBeforeDefault),
            TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The actual WPF library did not answer the default search.");
        Require(session.LibraryPage!.Hits.All(hit => hit.Id != S7T4NoisyItemId),
            "The actual WPF default library search still offered the item the owner set aside.");

        window.LibraryIncludeArchivedInput.IsChecked = true;
        var pageBeforeExpanded = session.LibraryPage;
        window.LibrarySearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.LibraryPage is not null && !ReferenceEquals(session.LibraryPage, pageBeforeExpanded),
            TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The actual WPF library did not answer the expanded search.");
        Require(session.LibraryPage!.Hits.Any(hit => hit.Id == S7T4NoisyItemId),
            "The actual WPF expanded library search did not reach the item the owner set aside.");
        // The capture scrolls to the explicit expansion control itself, so the
        // evidence shows the checked filter and the page it produced together.
        SaveS7T2Wpf(window, fixture, "s7t4-wpf-recall-expansion.png", window.LibraryIncludeArchivedInput);

        // A lock clears every private view of this console, including the
        // owner's own recall decision. The lock runs through the console's own
        // owner-channel path, so the private views go with the locked epoch.
        await session.LockAsync().ConfigureAwait(true);
        Require(session.ReportedVaultState == OwnerVaultState.Locked &&
                session.LibraryPage is null &&
                session.LibraryRecall is null &&
                window.LibraryResults.Items.Count == 0 &&
                !window.LibraryRecallText.Text.Contains("pinned", StringComparison.Ordinal),
            "Lock did not clear the actual WPF private library and recall views.");
        Console.WriteLine("PASS actual WPF library pins a rare lesson, refuses to set a protected item aside, expands the archive explicitly, and clears every private view on lock");
    }

    private static void VerifyS7T4EncryptedState(SmokeFixture fixture, string passphrase, string recoveryCode)
    {
        var sensitive = new[]
        {
            S7T4RareLessonTitle,
            S7T4RareLessonBody,
            S7T4NoisyItemTitle,
            S7T4NoisyItemBody,
            S7T4OtherZoneBody,
            S7T4DemoteReason,
            S7T4ArchiveReason,
            S7T4SharedToken,
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
                        "Synthetic recall-policy content appeared in plaintext in an encrypted vault file.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        Require(!Directory.EnumerateFiles(fixture.VaultDirectoryPath, "*.key", SearchOption.AllDirectories).Any(),
            "The synthetic vault persisted a key file beside its encrypted state.");
    }
}
