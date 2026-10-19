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
    private const string S7T3AlphaId = "s7t3-context-alpha";
    private const string S7T3AlphaTitle = "S7T3_SYNTHETIC_ALPHA_TITLE_37b2";
    private const string S7T3AlphaBody = "S7T3_SYNTHETIC_ALPHA_MOTIVATION_8e34: preserve the owner's original reason to finish the Alpha milestone.";
    private const string S7T3BetaId = "s7t3-context-beta";
    private const string S7T3BetaTitle = "S7T3_SYNTHETIC_BETA_TITLE_64d8";
    private const string S7T3BetaBody = "S7T3_SYNTHETIC_BETA_MOTIVATION_257c: keep the distinct Beta migration intention in its own project scope.";
    private const string S7T3GammaId = "s7t3-context-gamma";
    private const string S7T3GammaTitle = "S7T3_SYNTHETIC_GAMMA_TITLE_118e";
    private const string S7T3GammaBody = "S7T3_SYNTHETIC_GAMMA_BODY_c390: this intention is created after the Alpha grant.";
    private const string S7T3BetaRevisedBody = "S7T3_SYNTHETIC_BETA_REVISION_7a11: the newer intention revision must not replace the frozen one in Beta's old grant.";
    private const string S7T3CollectionId = "s7t3-alpha-project-collection";
    private const string S7T3ProjectAlpha = "S7T3 Project Alpha";
    private const string S7T3TaskAlpha = "S7T3 Task Alpha";
    private const string S7T3ProjectBeta = "S7T3 Project Beta";
    private const string S7T3TaskBeta = "S7T3 Task Beta";
    private const string S7T3ProjectGamma = "S7T3 Project Gamma";
    private const string S7T3TaskGamma = "S7T3 Task Gamma";
    private const string S7T3NoMatchQuery = "UNRELATED_TOKEN_9b7c";

    internal static async Task RunS7T3ContextualIntentionsAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S7-T3 contextual-intention smoke requires Windows named pipes, DPAPI and WPF.");
        }

        Console.WriteLine("RUN S7-T3 exact project/task intention triggers through scoped MCP briefs and actual WPF controls");
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
                "The synthetic S7-T3 vault was not provisioned.");
            recoveryCode = ParseRecoveryCode(provisioned.StandardOutput);
            var unlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S7-T3 vault did not unlock.");

            var now = DateTimeOffset.UtcNow;
            await WriteS7T2IntentionAsync(connectionsExecutable, fixture, S7T3AlphaId,
                S7T3AlphaTitle, S7T3AlphaBody, now).ConfigureAwait(false);
            await WriteS7T2IntentionAsync(connectionsExecutable, fixture, S7T3BetaId,
                S7T3BetaTitle, S7T3BetaBody, now).ConfigureAwait(false);

            var alphaContext = await OwnerChannelClient.SetIntentionContextTriggerAsync(
                fixture.ControlPipe, S7T3AlphaId, S7T3ProjectAlpha, S7T3TaskAlpha, disable: false,
                expectedLifecycleRevision: null, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            var betaContext = await OwnerChannelClient.SetIntentionContextTriggerAsync(
                fixture.ControlPipe, S7T3BetaId, S7T3ProjectBeta, S7T3TaskBeta, disable: false,
                expectedLifecycleRevision: null, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            var configuredSchedule = await OwnerChannelClient.ListScheduledIntentionsAsync(
                fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            var configuredSummary = configuredSchedule.Intentions is null
                ? "<null>"
                : string.Join(";", configuredSchedule.Intentions.Select(item =>
                    $"{item.ResourceId}:{item.State}:{item.ContextTriggerEnabled}:{item.ContextProject}:{item.ContextTask}"));
            var configuredAlpha = configuredSchedule.Intentions?.SingleOrDefault(item => item.ResourceId == S7T3AlphaId);
            var configuredBeta = configuredSchedule.Intentions?.SingleOrDefault(item => item.ResourceId == S7T3BetaId);
            Require(alphaContext.Outcome == OwnerCommandOutcome.Completed &&
                    betaContext.Outcome == OwnerCommandOutcome.Completed &&
                    configuredSchedule.Outcome == OwnerCommandOutcome.Completed &&
                    configuredAlpha is { ContextTriggerEnabled: true, ContextProject: S7T3ProjectAlpha, ContextTask: S7T3TaskAlpha } &&
                    configuredBeta is { ContextTriggerEnabled: true, ContextProject: S7T3ProjectBeta, ContextTask: S7T3TaskBeta },
                "The owner service did not persist the two distinct project/task triggers in encrypted lifecycle records (alpha=" +
                alphaContext.Outcome + "/" + alphaContext.ErrorCode + ", beta=" + betaContext.Outcome + "/" +
                betaContext.ErrorCode + ", list=" + configuredSchedule.Outcome + "/" + configuredSchedule.ErrorCode +
                ", items=" + configuredSummary + ").");

            var dueAt = now.AddMinutes(-2).ToString("O", CultureInfo.InvariantCulture);
            var dueTrigger = await OwnerChannelClient.SetIntentionTimeTriggerAsync(
                fixture.ControlPipe, S7T3AlphaId, dueAt, cancel: false,
                expectedLifecycleRevision: alphaContext.Lifecycle!.Revision,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            var dueSchedule = await OwnerChannelClient.ListScheduledIntentionsAsync(
                fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            var dueAlpha = dueSchedule.Intentions?.SingleOrDefault(item => item.ResourceId == S7T3AlphaId);
            Require(dueTrigger.Outcome == OwnerCommandOutcome.Completed &&
                    dueSchedule.Outcome == OwnerCommandOutcome.Completed &&
                    dueAlpha is { State: "pending", IsDue: true, DueAt: not null, ContextTriggerEnabled: true },
                "The due time trigger did not remain separate from the pending contextual intention.");
            var duePass = await OwnerChannelClient.RunIntentionSchedulePassAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(duePass.Outcome == OwnerCommandOutcome.Completed && duePass.NotificationsRecorded == 1,
                "The actual unlocked scheduler did not persist exactly one due reminder marker.");

            var collection = await RunOwnerCommandWithInputAsync(connectionsExecutable, "SET" + Environment.NewLine,
                "owner", "collection-set", "--control-pipe", fixture.ControlPipe,
                "--collection-id", S7T3CollectionId, "--resource-ids", S7T3AlphaId).ConfigureAwait(false);
            Require(collection.ExitCode == 0 && collection.StandardOutput.Contains("COLLECTION set", StringComparison.Ordinal),
                "The owner could not create the Alpha grant's initial one-item collection (" + collection.StandardError.Trim() + ").");
            var alphaSession = await IssueS7T3CollectionGrantAsync(connectionsExecutable, fixture, "s7t3-alpha", S7T3CollectionId).ConfigureAwait(false);
            var betaSession = await IssueS4T4GrantAsync(connectionsExecutable, fixture, "s7t3-beta", "memory.context,memory.read",
                new[] { S7T3BetaId }).ConfigureAwait(false);
            var bridgeList = await RunBridgeToolsListAsync(connectionsExecutable, fixture.AgentPipe).ConfigureAwait(false);
            Require(bridgeList.Contains("metabrain_memory_brief", StringComparison.Ordinal) &&
                    bridgeList.Contains("contextProject", StringComparison.Ordinal) &&
                    bridgeList.Contains("contextTask", StringComparison.Ordinal),
                "The real MCP bridge did not advertise explicit project/task context brief fields.");
            var alphaBrief = await RunS7T3BriefAsync(connectionsExecutable, fixture, alphaSession, S7T3ProjectAlpha, S7T3TaskAlpha).ConfigureAwait(false);
            var betaBrief = await RunS7T3BriefAsync(connectionsExecutable, fixture, betaSession, S7T3ProjectBeta, S7T3TaskBeta).ConfigureAwait(false);
            Require(alphaBrief.Contains($"CONTEXT_INTENTION id={S7T3AlphaId} revision=1", StringComparison.Ordinal) &&
                    alphaBrief.Contains(S7T3AlphaTitle, StringComparison.Ordinal) &&
                    alphaBrief.Contains(S7T3AlphaBody, StringComparison.Ordinal) &&
                    !alphaBrief.Contains(S7T3BetaId, StringComparison.Ordinal) &&
                    !alphaBrief.Contains(S7T3BetaTitle, StringComparison.Ordinal) &&
                    !alphaBrief.Contains(S7T3BetaBody, StringComparison.Ordinal),
                "The Alpha MCP brief did not return only its exact approved intention/context pair (" + alphaBrief.Trim() + ").");
            Require(betaBrief.Contains($"CONTEXT_INTENTION id={S7T3BetaId} revision=1", StringComparison.Ordinal) &&
                    betaBrief.Contains(S7T3BetaTitle, StringComparison.Ordinal) &&
                    betaBrief.Contains(S7T3BetaBody, StringComparison.Ordinal) &&
                    !betaBrief.Contains(S7T3AlphaId, StringComparison.Ordinal) &&
                    !betaBrief.Contains(S7T3AlphaTitle, StringComparison.Ordinal) &&
                    !betaBrief.Contains(S7T3AlphaBody, StringComparison.Ordinal),
                "The Beta MCP brief did not return only its distinct exact approved intention/context pair.");

            var wrongContext = await RunS7T3BriefAsync(connectionsExecutable, fixture, alphaSession, S7T3ProjectBeta, S7T3TaskBeta).ConfigureAwait(false);
            Require(wrongContext.Contains("BRIEF no_match", StringComparison.Ordinal) &&
                    !wrongContext.Contains("CONTEXT_INTENTION", StringComparison.Ordinal) &&
                    !wrongContext.Contains(S7T3AlphaBody, StringComparison.Ordinal) &&
                    !wrongContext.Contains(S7T3BetaBody, StringComparison.Ordinal),
                "A non-matching explicit project/task context surfaced a private intention reminder.");
            var outOfScope = await RunS7T3BriefAsync(connectionsExecutable, fixture, alphaSession, S7T3ProjectGamma, S7T3TaskGamma).ConfigureAwait(false);
            Require(outOfScope.Contains("BRIEF no_match", StringComparison.Ordinal) &&
                    !outOfScope.Contains(S7T3BetaId, StringComparison.Ordinal) &&
                    !outOfScope.Contains(S7T3BetaBody, StringComparison.Ordinal) &&
                    !outOfScope.Contains(S7T3GammaId, StringComparison.Ordinal) &&
                    !outOfScope.Contains(S7T3GammaBody, StringComparison.Ordinal),
                "A project context outside the Alpha session's frozen IDs/revisions returned private content.");

            var alphaBeforeRead = await OwnerChannelClient.ListScheduledIntentionsAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            var alphaBeforeRevision = alphaBeforeRead.Intentions?.Single(item => item.ResourceId == S7T3AlphaId).LifecycleRevision;
            var alphaAfterRead = await OwnerChannelClient.ListScheduledIntentionsAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(alphaBeforeRead.Outcome == OwnerCommandOutcome.Completed && alphaAfterRead.Outcome == OwnerCommandOutcome.Completed &&
                    alphaAfterRead.Intentions?.Single(item => item.ResourceId == S7T3AlphaId) is
                        { State: "pending", Notified: true, Acknowledged: false } alphaStillPending &&
                    alphaStillPending.LifecycleRevision == alphaBeforeRevision,
                "Reading or surfacing the contextual reminder changed its lifecycle revision, acknowledgement or completion state.");

            await WriteS7T2IntentionAsync(connectionsExecutable, fixture, S7T3GammaId,
                S7T3GammaTitle, S7T3GammaBody, now).ConfigureAwait(false);
            var gammaContext = await OwnerChannelClient.SetIntentionContextTriggerAsync(
                fixture.ControlPipe, S7T3GammaId, S7T3ProjectGamma, S7T3TaskGamma, disable: false,
                expectedLifecycleRevision: null, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            Require(gammaContext.Outcome == OwnerCommandOutcome.Completed,
                "The owner could not add the post-grant Gamma intention.");
            var newIntentionBrief = await RunS7T3BriefAsync(connectionsExecutable, fixture, alphaSession, S7T3ProjectGamma, S7T3TaskGamma).ConfigureAwait(false);
            Require(newIntentionBrief.Contains("BRIEF no_match", StringComparison.Ordinal) &&
                    !newIntentionBrief.Contains(S7T3GammaId, StringComparison.Ordinal) &&
                    !newIntentionBrief.Contains(S7T3GammaTitle, StringComparison.Ordinal) &&
                    !newIntentionBrief.Contains(S7T3GammaBody, StringComparison.Ordinal),
                "A newly created intention was auto-added to the existing Alpha grant.");

            await ReviseS7T3IntentionAsync(connectionsExecutable, fixture, S7T3BetaId, S7T3BetaRevisedBody).ConfigureAwait(false);
            var revisedBetaBrief = await RunS7T3BriefAsync(connectionsExecutable, fixture, betaSession, S7T3ProjectBeta, S7T3TaskBeta).ConfigureAwait(false);
            Require(revisedBetaBrief.Contains("BRIEF no_match", StringComparison.Ordinal) &&
                    !revisedBetaBrief.Contains(S7T3BetaId, StringComparison.Ordinal) &&
                    !revisedBetaBrief.Contains(S7T3BetaBody, StringComparison.Ordinal) &&
                    !revisedBetaBrief.Contains(S7T3BetaRevisedBody, StringComparison.Ordinal),
                "A new canonical intention revision replaced or expanded the existing Beta grant's frozen revision.");

            var membershipAfterGrant = await RunOwnerCommandWithInputAsync(connectionsExecutable, "SET" + Environment.NewLine,
                "owner", "collection-set", "--control-pipe", fixture.ControlPipe,
                "--collection-id", S7T3CollectionId,
                "--resource-ids", string.Join(',', S7T3AlphaId, S7T3BetaId, S7T3GammaId)).ConfigureAwait(false);
            Require(membershipAfterGrant.ExitCode == 0 && membershipAfterGrant.StandardOutput.Contains("COLLECTION set", StringComparison.Ordinal),
                "The owner could not add new members to the source collection after issuing the Alpha grant (" + membershipAfterGrant.StandardError.Trim() + ").");
            var memberChangedBrief = await RunS7T3BriefAsync(connectionsExecutable, fixture, alphaSession, S7T3ProjectBeta, S7T3TaskBeta).ConfigureAwait(false);
            Require((memberChangedBrief.Contains("resource_unavailable", StringComparison.Ordinal) ||
                     memberChangedBrief.Contains("BRIEF no_match", StringComparison.Ordinal)) &&
                    !memberChangedBrief.Contains(S7T3BetaId, StringComparison.Ordinal) &&
                    !memberChangedBrief.Contains(S7T3BetaBody, StringComparison.Ordinal) &&
                    !memberChangedBrief.Contains(S7T3GammaId, StringComparison.Ordinal) &&
                    !memberChangedBrief.Contains(S7T3GammaBody, StringComparison.Ordinal),
                "Adding collection members caused an existing Alpha grant to expose the new member or its content.");

            var currentBetaSession = await IssueS4T4GrantAsync(connectionsExecutable, fixture, "s7t3-beta-lock", "memory.context,memory.read",
                new[] { S7T3BetaId }).ConfigureAwait(false);
            var currentBetaBrief = await RunS7T3BriefAsync(connectionsExecutable, fixture, currentBetaSession, S7T3ProjectBeta, S7T3TaskBeta).ConfigureAwait(false);
            Require(currentBetaBrief.Contains($"CONTEXT_INTENTION id={S7T3BetaId} revision=2", StringComparison.Ordinal) &&
                    currentBetaBrief.Contains(S7T3BetaRevisedBody, StringComparison.Ordinal),
                "A fresh Beta grant did not bind and surface the owner-approved current intention revision.");

            var currentAlphaSession = await IssueS7T3CollectionGrantAsync(
                connectionsExecutable, fixture, "s7t3-alpha-current", S7T3CollectionId).ConfigureAwait(false);
            DriveS7T3Wpf(fixture, connectionsExecutable, currentAlphaSession);
            var evidenceDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts", "sprint-7", "evidence");
            Directory.CreateDirectory(evidenceDirectory);
            foreach (var name in new[] { "s7t3-wpf-context-intentions.png", "s7t3-wpf-context-controls.png" })
            {
                var rendered = Path.Combine(fixture.OutputDirectory, name);
                Require(File.Exists(rendered), "The actual shown WPF contextual-intention evidence was not rendered: " + name);
                File.Copy(rendered, Path.Combine(evidenceDirectory, name), overwrite: true);
                Console.WriteLine("S7T3 WPF visual evidence: " + Path.Combine(evidenceDirectory, name));
            }

            VerifyS7T3EncryptedState(fixture, passphrase, recoveryCode!);

            var lockedPass = await OwnerChannelClient.RunIntentionSchedulePassAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            var lockedBrief = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_memory_brief", new
                {
                    sessionFile = currentBetaSession.SessionFile,
                    query = S7T3NoMatchQuery,
                    budgetChars = "8000",
                    contextProject = S7T3ProjectBeta,
                    contextTask = S7T3TaskBeta
                }).ConfigureAwait(false);
            Require(lockedPass.Outcome != OwnerCommandOutcome.Completed &&
                    lockedBrief.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !lockedBrief.Contains(S7T3BetaTitle, StringComparison.Ordinal) &&
                    !lockedBrief.Contains(S7T3BetaRevisedBody, StringComparison.Ordinal) &&
                    !lockedBrief.Contains(S7T3GammaBody, StringComparison.Ordinal),
                "A locked vault ran a private schedule pass or returned intention metadata/content through MCP.");
            Console.WriteLine("PASS real MCP briefs serve only the matching enabled intention at each distinct frozen grant; mismatched context, new intentions, members, revisions and locked requests fail closed");
            Console.WriteLine("PASS actual WPF context trigger controls disable and re-enable matching, then acknowledge stays pending until explicit completion; original motivation remains unchanged");
            Console.WriteLine("PASS synthetic intention/context/due data remains encrypted in the vault; no key file or plaintext context/content appears in vault files");
            Console.WriteLine("LIMIT synthetic Windows-only records and a throwaway encrypted vault; no model/provider/network call, installed-service setup, external notification channel, OS-level isolation or secure-erasure claim was exercised.");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static async Task<S4T4Session> IssueS7T3CollectionGrantAsync(
        string executable,
        SmokeFixture fixture,
        string name,
        string collectionId)
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O", CultureInfo.InvariantCulture);
        var handoff = Path.Combine(fixture.OutputDirectory, name + ".grant-handoff");
        var sessionFile = Path.Combine(fixture.OutputDirectory, name + ".session-handoff");
        var grant = await RunOwnerCommandWithInputAsync(executable, "ISSUE" + Environment.NewLine,
            "owner", "grant", "--control-pipe", fixture.ControlPipe,
            "--collection-id", collectionId,
            "--operations", "memory.context,memory.read",
            "--expires-at-utc", expiry,
            "--handoff-file", handoff).ConfigureAwait(false);
        Require(grant.ExitCode == 0 &&
                grant.StandardOutput.Contains("resource=" + S7T3AlphaId + " zone=s7t2-private-zone revision=1", StringComparison.Ordinal),
            "The collection-derived Alpha grant did not freeze the initial intention ID/revision.");
        VerifyOwnerHandoffAcl(handoff);
        var redeem = await RunOwnerCommandAsync(executable,
            "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
            "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
        Require(redeem.ExitCode == 0 && redeem.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
            "The collection-derived Alpha grant could not be redeemed.");
        VerifyOwnerHandoffAcl(sessionFile);
        return new S4T4Session(ParseSessionId(redeem.StandardOutput), sessionFile);
    }

    private static async Task<string> RunS7T3BriefAsync(
        string executable,
        SmokeFixture fixture,
        S4T4Session session,
        string project,
        string task)
    {
        var response = await RunBridgeToolAsync(executable, fixture.AgentPipe,
            "metabrain_memory_brief", new
            {
                sessionFile = session.SessionFile,
                query = S7T3NoMatchQuery,
                budgetChars = "8000",
                contextProject = project,
                contextTask = task
            }).ConfigureAwait(false);
        using var document = JsonDocument.Parse(response);
        return document.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()
            ?? string.Empty;
    }

    private static async Task ReviseS7T3IntentionAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string body)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s7t3-revise-" + resourceId + ".json");
        await File.WriteAllTextAsync(input,
            S7T2IntentionDraft("S7T3 revised synthetic intention", body, DateTimeOffset.UtcNow), Encoding.UTF8).ConfigureAwait(false);
        var revised = await RunOwnerCommandAsync(executable,
            "owner", "memory-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", S7T2Zone,
            "--expected-revision", "1", "--input-file", input).ConfigureAwait(false);
        Require(revised.ExitCode == 0 && revised.StandardOutput.Contains("revision=2", StringComparison.Ordinal),
            "The owner could not create the newer synthetic intention revision: " + revised.StandardError.Trim());
    }
    private static void DriveS7T3Wpf(SmokeFixture fixture, string connectionsExecutable, S4T4Session alphaSession)
    {
        var session = new OwnerConsoleSession(fixture.ControlPipe);
        session.AttachAsync().GetAwaiter().GetResult();
        Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
            "The actual S7-T3 owner session did not attach to the unlocked service.");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            OwnerConsoleWindow? window = null;
            try
            {
                var activeWindow = new OwnerConsoleWindow(session)
                {
                    Width = 1360,
                    Height = 1320
                };
                window = activeWindow;
                activeWindow.Show();
                activeWindow.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await S7T3WpfInteractionsAsync(activeWindow, session, fixture, connectionsExecutable, alphaSession).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                    finally
                    {
                        await WaitForAsync(() => !session.IntentionActInFlight, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
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
            throw new InvalidOperationException("The shown S7-T3 WPF contextual-intention surface failed: " + failure.Message, failure);
        }
    }

    private static async Task S7T3WpfInteractionsAsync(
        OwnerConsoleWindow window,
        OwnerConsoleSession session,
        SmokeFixture fixture,
        string connectionsExecutable,
        S4T4Session alphaSession)
    {
        SelectS7T2Navigation(window, "Intentions");
        Require(await WaitForAsync(() => !session.IntentionActInFlight, TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The actual S7-T3 intention timeline did not finish its initial service refresh.");
        await session.RefreshIntentionsAsync().ConfigureAwait(true);
        SelectS7T2WpfIntention(window, session, S7T3AlphaId);
        var alpha = session.ScheduledIntentions.Single(item => item.ResourceId == S7T3AlphaId);
        Require(alpha.IsDue && alpha.Notified && alpha.State == "pending" && alpha.ContextTriggerEnabled &&
                window.IntentionContextProjectInput.Text == S7T3ProjectAlpha &&
                window.IntentionContextTaskInput.Text == S7T3TaskAlpha &&
                window.IntentionAcknowledgeButton.IsEnabled,
            "The actual WPF timeline did not show the due/context reminder and its explicit owner controls.");
        SaveS7T2Wpf(window, fixture, "s7t3-wpf-context-intentions.png", window.IntentionsList);

        window.IntentionContextTriggerDisableButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.ScheduledIntentions.SingleOrDefault(item => item.ResourceId == S7T3AlphaId)?.ContextTriggerEnabled == false,
            TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The actual WPF disable control did not persist the disabled context trigger.");
        var disabledSession = await IssueS7T3CollectionGrantAsync(
            connectionsExecutable, fixture, "s7t3-alpha-disabled", S7T3CollectionId).ConfigureAwait(true);
        var disabledBrief = await RunS7T3BriefAsync(connectionsExecutable, fixture, disabledSession, S7T3ProjectAlpha, S7T3TaskAlpha).ConfigureAwait(true);
        Require(disabledBrief.Contains("BRIEF no_match", StringComparison.Ordinal) &&
                !disabledBrief.Contains(S7T3AlphaId, StringComparison.Ordinal) &&
                !disabledBrief.Contains(S7T3AlphaBody, StringComparison.Ordinal),
            "Disabling the WPF context trigger did not stop its MCP reminder (" + disabledBrief.Trim() + ").");

        window.IntentionContextProjectInput.Text = S7T3ProjectAlpha;
        window.IntentionContextTaskInput.Text = S7T3TaskAlpha;
        Require(window.IntentionContextTriggerSetButton.IsEnabled,
            "The WPF context enable control did not enable for an explicit project/task pair.");
        window.IntentionContextTriggerSetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.ScheduledIntentions.SingleOrDefault(item => item.ResourceId == S7T3AlphaId)?.ContextTriggerEnabled == true,
            TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The actual WPF context enable control did not persist the owner-selected pair.");
        var reenabledSession = await IssueS7T3CollectionGrantAsync(
            connectionsExecutable, fixture, "s7t3-alpha-reenabled", S7T3CollectionId).ConfigureAwait(true);
        var reenabledBrief = await RunS7T3BriefAsync(connectionsExecutable, fixture, reenabledSession, S7T3ProjectAlpha, S7T3TaskAlpha).ConfigureAwait(true);
        Require(reenabledBrief.Contains($"CONTEXT_INTENTION id={S7T3AlphaId} revision=1", StringComparison.Ordinal) &&
                reenabledBrief.Contains(S7T3AlphaBody, StringComparison.Ordinal),
            "Re-enabling the WPF context trigger did not restore the matching scoped reminder.");

        SelectS7T2WpfIntention(window, session, S7T3AlphaId);
        SaveS7T2Wpf(window, fixture, "s7t3-wpf-context-controls.png", window.IntentionContextTriggerDisableButton);
        Require(window.IntentionAcknowledgeButton.IsEnabled,
            "The WPF timeline did not enable acknowledgement for the due/context intention.");
        window.IntentionAcknowledgeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.ScheduledIntentions.SingleOrDefault(item => item.ResourceId == S7T3AlphaId)?.Acknowledged == true,
            TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The WPF acknowledgement control did not persist through the owner service.");
        var acknowledged = session.ScheduledIntentions.Single(item => item.ResourceId == S7T3AlphaId);
        Require(acknowledged.State == "pending" && acknowledged.Acknowledged,
            "Reading or acknowledging the due/context reminder completed the intention.");

        window.IntentionRationaleInput.Text = "Synthetic owner completion after the contextual reminder.";
        Require(window.IntentionCompleteButton.IsEnabled,
            "Typing a completion rationale did not enable the separate WPF completion action.");
        window.IntentionCompleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.ScheduledIntentions.All(item => item.ResourceId != S7T3AlphaId),
            TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The explicit WPF completion did not remove the completed intention from the pending timeline.");
        var lifecycle = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "lifecycle-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T3AlphaId).ConfigureAwait(true);
        var memory = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "memory-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T3AlphaId).ConfigureAwait(true);
        Require(lifecycle.ExitCode == 0 && lifecycle.StandardOutput.Contains("intention=done", StringComparison.Ordinal) &&
                lifecycle.StandardOutput.Contains("Synthetic owner completion after the contextual reminder.", StringComparison.Ordinal) &&
                memory.ExitCode == 0 && memory.StandardOutput.Contains(S7T3AlphaBody, StringComparison.Ordinal),
            "Explicit completion did not keep the original motivation while recording the owner's separate rationale.");

        SelectS7T2WpfIntention(window, session, S7T3BetaId);
        var lockResult = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(true);
        Require(lockResult.ExitCode == 0 && lockResult.StandardOutput.Contains("VAULT locked", StringComparison.Ordinal),
            "The actual owner vault lock did not complete during the contextual-intention privacy check.");
        await session.CheckAsync().ConfigureAwait(true);
        Require(session.ReportedVaultState == OwnerVaultState.Locked && session.ScheduledIntentions.Length == 0 &&
                window.IntentionsList.Items.Count == 0 && window.IntentionResourceIdInput.Text.Length == 0 &&
                window.IntentionDueAtInput.Text.Length == 0 && window.IntentionContextProjectInput.Text.Length == 0 &&
                window.IntentionContextTaskInput.Text.Length == 0 && window.IntentionRationaleInput.Text.Length == 0 &&
                !window.IntentionsMessageText.Text.Contains(S7T3BetaTitle, StringComparison.Ordinal) &&
                !window.IntentionsMessageText.Text.Contains(S7T3BetaRevisedBody, StringComparison.Ordinal),
            "Lock did not clear the actual WPF private intention list, context labels, and controls.");
        Console.WriteLine("PASS actual WPF timeline toggles the project/task trigger, acknowledges without completion, explicitly completes with rationale, and clears private state on lock");
    }

    private static void VerifyS7T3EncryptedState(SmokeFixture fixture, string passphrase, string recoveryCode)
    {
        var sensitive = new[]
        {
            S7T3AlphaTitle,
            S7T3AlphaBody,
            S7T3BetaTitle,
            S7T3BetaRevisedBody,
            S7T3GammaBody,
            S7T3ProjectAlpha,
            S7T3TaskAlpha,
            S7T3ProjectBeta,
            S7T3TaskBeta,
            S7T3ProjectGamma,
            S7T3TaskGamma,
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
                        "Synthetic intention/context content appeared in plaintext in an encrypted vault file.");
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
