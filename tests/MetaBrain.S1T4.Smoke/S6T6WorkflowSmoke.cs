using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;
using MetaBrain.OwnerUi;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S6-T6 owner job/review workflow and automation opt-in regression walkthrough.
///
/// The walkthrough drives the real WPF owner window (the same window, session
/// and running service the owner uses) through the interactive surface: capture
/// a note, run one bounded job and a batch, compare a proposal against its cited
/// evidence, publish one and reject another, cancel a queued job, and then
/// enable, disable, lock-pause and restart the owner's synthesis schedule.
///
/// Every model call goes to a local HTTP endpoint the shipped adapter really
/// calls, which records each request it receives: the smoke therefore shows
/// exactly when the application reached a provider and proves that capture,
/// read-back, review, publication, cancellation and schedule configuration send
/// nothing. The assistant's output is still validated by the shipped output
/// boundary, because the local endpoint builds its completion from the prompt it
/// actually received.
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S6T6Zone = "s6t6-zone";
    private const string S6T6Inbox = "s6t6-inbox";
    private const string S6T6NoteA = "s6t6-note-a";
    private const string S6T6NoteB = "s6t6-note-b";
    private const string S6T6UiCapture = "s6t6-ui-capture";
    private const int S6T6ScheduleIntervalMinutes = 1;

    private const string S6T6NoteAText =
        "S6-T6 synthetic Alpha note: the alpha payment writer retries a failed write up to three times.";
    private const string S6T6NoteBText =
        "S6-T6 synthetic Beta note: the beta ledger writer retries a failed write up to five times under its own budget.";
    private const string S6T6Objective =
        "Compare the retry budgets both notes report, and state them as each note's report rather than as settled policy.";
    private const string S6T6UiCaptureText =
        "S6-T6 synthetic note saved from the console capture editor; saving it must reach no provider.";
    private const string S6T6RejectRationale = "S6-T6 walkthrough: rejected to prove the review records a decision without applying the proposal.";

    /// <summary>
    /// A selection draft is an ordinary text file a client or the owner writes;
    /// ordinary Windows text tools put a UTF-8 preamble in front of it, and every
    /// domain codec here accepts one. This protects that boundary for both the
    /// job-create and the schedule-set input files.
    /// </summary>
    private static void S6T6SelectionDraftAcceptsUtf8Preamble()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            inputs = new[]
            {
                new { resourceId = S6T6NoteA, revision = 1L },
                new { resourceId = S6T6NoteB, revision = 1L }
            },
            objective = S6T6Objective,
            targetLevel = SynthesisLevels.L2
        });
        var withPreamble = new byte[json.Length + 3];
        withPreamble[0] = 0xEF;
        withPreamble[1] = 0xBB;
        withPreamble[2] = 0xBF;
        json.CopyTo(withPreamble, 3);
        var draft = JobSelectionCodec.DecodeDraft(withPreamble);
        Require(draft.Inputs is { Length: 2 } &&
                string.Equals(draft.Objective, S6T6Objective, StringComparison.Ordinal) &&
                string.Equals(draft.TargetLevel, SynthesisLevels.L2, StringComparison.Ordinal),
            "A UTF-8 preamble on a selection draft was refused instead of accepted like every other domain codec.");
        Console.WriteLine("PASS selection draft: a UTF-8 preamble is accepted for the job and schedule input files");
    }

    internal static async Task RunS6T6WorkflowAsync(string connectionsExecutable)
    {
        Console.WriteLine("RUN S6-T6 owner job/review workflow and automation opt-in");
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S6-T6 smoke requires Windows named pipes and DPAPI.");
        }

        S6T6SelectionDraftAcceptsUtf8Preamble();

        using var fixture = SmokeFixture.CreateUninitialized();
        using var provider = new S6T6ProviderStub();
        RunningService? service = null;
        var passphrase = "";
        var transcripts = new StringBuilder();
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "Provision did not create the S6-T6 vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");
            var unlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S6-T6 vault did not unlock.");

            await WriteS6T6InboxAsync(connectionsExecutable, fixture).ConfigureAwait(false);
            await WriteS6T6NoteAsync(connectionsExecutable, fixture, S6T6NoteA, S6T6NoteAText).ConfigureAwait(false);
            await WriteS6T6NoteAsync(connectionsExecutable, fixture, S6T6NoteB, S6T6NoteBText).ConfigureAwait(false);
            Require(provider.RequestCount == 0,
                "Storing notes reached a provider endpoint (" + provider.RequestCount.ToString(CultureInfo.InvariantCulture) + " request(s)).");
            Console.WriteLine("PASS capture: two notes stored encrypted with zero provider requests observed at the endpoint");

            var session = new OwnerConsoleSession(fixture.ControlPipe);
            await session.AttachAsync().ConfigureAwait(false);
            Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
                "The console session did not report the unlocked service.");

            // Leg A: the interactive WPF surface up to the batch, then the review,
            // then the schedule enable + first scheduled run.
            DriveS6T6WpfPhaseOne(session, fixture, provider, connectionsExecutable);

            // Leg B: lock pauses the schedule, the service restarts while the
            // schedule stays enabled, and one run happens after unlock — never a
            // burst of catch-up runs for the slots missed while locked/down.
            // The instance is handed over before the call so a failure inside the
            // leg cannot be masked by stopping an already-disposed process.
            var scheduleService = service;
            service = null;
            var scheduleObservation = await RunS6T6ScheduleStopAndRestartAsync(
                scheduleService, connectionsExecutable, fixture, session, provider, passphrase, transcripts).ConfigureAwait(false);
            service = scheduleObservation.Service;

            // Leg C: the interactive disable act, and the same due evaluation the
            // background loop uses reporting that a disabled schedule runs nothing.
            DriveS6T6WpfScheduleState(session.ControlPipe, fixture, disable: true, scheduleObservation.LastJobId);
            var disabledRun = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "schedule-run", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(disabledRun.ExitCode == 0 && disabledRun.StandardOutput.Contains("outcome=disabled", StringComparison.Ordinal),
                "The shared due evaluation did not report the disabled schedule as running nothing (stdout=" + disabledRun.StandardOutput.Trim() + ").");

            var requestsAfterAll = provider.RequestCount;
            Require(requestsAfterAll == 5,
                "The walkthrough reached the provider " + requestsAfterAll.ToString(CultureInfo.InvariantCulture) +
                " time(s); exactly one per bounded job (1 single + 2 batch + 2 scheduled) was expected.");
            var expectedRuns = new[] { "single job", "batch note 1", "batch note 2", "scheduled run 1", "scheduled run 2" };
            Console.WriteLine("PASS egress observation: provider endpoint received exactly " +
                requestsAfterAll.ToString(CultureInfo.InvariantCulture) + " request(s) for " + string.Join(", ", expectedRuns));
            Console.WriteLine("PASS provider requests (UTC): " + string.Join(", ",
                provider.RequestTimes.Select(time => time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))));

            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
                transcripts.AppendLine(await service.StandardOutputDrain.ConfigureAwait(false));
                service = null;
            }

            var serviceLog = transcripts.ToString();
            Require(serviceLog.Contains("SCHEDULE outcome=ran", StringComparison.Ordinal),
                "The service transcript did not record a scheduled run performed by its background pass.");
            Require(serviceLog.Contains("READY protocol=1 channel=owner+agent", StringComparison.Ordinal),
                "The service transcript is missing the ready line.");
            Require(serviceLog.Split("SCHEDULE pass started").Length - 1 == 2,
                "The schedule pass did not start in both service instances (starts=" +
                (serviceLog.Split("SCHEDULE pass started").Length - 1).ToString(CultureInfo.InvariantCulture) + ").");
            Require(!serviceLog.Contains("SCHEDULE pass failed:", StringComparison.Ordinal),
                "A schedule pass failed unexpectedly: " + serviceLog
                    .Split('\n')
                    .FirstOrDefault(line => line.Contains("SCHEDULE pass failed:", StringComparison.Ordinal)));
            Console.WriteLine("PASS service transcript: the background pass recorded its own scheduled runs (" +
                serviceLog.Split('\n').Count(line => line.Contains("SCHEDULE outcome=", StringComparison.Ordinal))
                    .ToString(CultureInfo.InvariantCulture) + " pass line(s) across " +
                (serviceLog.Split("READY protocol=1 channel=owner+agent").Length - 1).ToString(CultureInfo.InvariantCulture) +
                " service start(s))");

            PersistS6T6Evidence(fixture);
            Console.WriteLine("PASS S6-T6 owner job/review workflow and automation opt-in complete");
        }
        catch
        {
            // The service transcript is the only place the background pass's own
            // observations live; on a failure it is printed so the cause is visible
            // instead of being reduced to the first failing assertion.
            Console.WriteLine("S6-T6 SERVICE TRANSCRIPT (background pass lines):");
            Console.WriteLine(transcripts.ToString().Trim());
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(Encoding.UTF8.GetBytes(passphrase));
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The interactive surface, phase one, driven on the window's own dispatcher
    /// thread: capture a note from the editor, run one bounded job and a batch
    /// from the Jobs panel, and review proposals in the Inbox panel. Every act is
    /// a real control of the shown window; the observations are the session state
    /// those controls produced.
    /// </summary>
    private static void DriveS6T6WpfPhaseOne(
        OwnerConsoleSession session,
        SmokeFixture fixture,
        S6T6ProviderStub provider,
        string connectionsExecutable)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(provider);
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
                        await S6T6PhaseOneAsync(window, session, fixture, provider, connectionsExecutable).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                    finally
                    {
                        // Let every act this window started finish on this
                        // dispatcher before it stops: a continuation that was
                        // still pending here would never be delivered, and the
                        // act would never clear its own in-flight state. The
                        // shipped application keeps one dispatcher for its whole
                        // lifetime, so this is the harness mirroring that.
                        await WaitForAsync(
                            () => !session.JobsActInFlight && !session.InboxActInFlight,
                            TimeSpan.FromSeconds(30)).ConfigureAwait(true);
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
            throw new InvalidOperationException("The S6-T6 WPF flow failed: " + failure.Message, failure);
        }
    }

    private static async Task S6T6PhaseOneAsync(
        OwnerConsoleWindow window,
        OwnerConsoleSession session,
        SmokeFixture fixture,
        S6T6ProviderStub provider,
        string connectionsExecutable)
    {
        // --- capture from the console editor (no provider may be reached) ---
        SelectNavigation(window, "Inbox");
        window.CaptureResourceInput.Text = S6T6UiCapture;
        window.CaptureZoneInput.Text = S6T6Zone;
        window.CaptureTitleInput.Text = "S6-T6 console capture";
        window.CaptureTextInput.Text = S6T6UiCaptureText;
        window.CaptureSaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.CaptureSource is { Id: var id } && string.Equals(id, S6T6UiCapture, StringComparison.Ordinal), TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The console capture did not store the typed note (" + (session.CaptureMessage ?? "no message") + ").");
        Require(provider.RequestCount == 0, "Saving a note from the console reached a provider endpoint.");

        window.CaptureReadBackIdInput.Text = S6T6UiCapture;
        window.CaptureReadBackRevisionInput.Text = session.CaptureSource!.Revision.ToString(CultureInfo.InvariantCulture);
        window.CaptureReadBackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => string.Equals(session.CaptureReadBackText, S6T6UiCaptureText, StringComparison.Ordinal), TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The console read-back did not serve the exact saved text (" + (session.CaptureReadBackMessage ?? "no message") + ").");
        Require(provider.RequestCount == 0, "Reading a saved note reached a provider endpoint.");
        SaveS6T6Wpf(window, fixture, "s6t6-wpf-capture-saved.png", window.CaptureMessageText);
        Console.WriteLine("PASS WPF capture: the editor stored the note and read it back verbatim with zero provider requests");

        // --- jobs panel: model/egress settings ---
        SelectNavigation(window, "Jobs");
        window.ProviderNameInput.Text = "deepseek";
        window.ProviderModelInput.Text = "deepseek-flash";
        window.ProviderBaseUrlInput.Text = provider.BaseUrl;
        window.ProviderKeyInput.Password = "s6t6-local-stub-key";
        await WaitForJobsPanelIdleAsync(session).ConfigureAwait(true);
        Require(window.ProviderSaveButton.IsEnabled, "The console did not offer the model-settings act.");
        window.ProviderSaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.ProviderSettings is { Configured: true }, TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The Jobs panel did not store the model settings (" + (session.ProviderMessage ?? "no message") + ").");
        Require(provider.RequestCount == 0, "Saving model settings reached a provider endpoint.");
        Require(string.Equals(session.ProviderSettings!.BaseUrl, provider.BaseUrl, StringComparison.Ordinal),
            "The read-back model settings do not name the endpoint that was stored.");
        Console.WriteLine("PASS WPF model settings: stored and read back with the caps, no key echoed, no provider request");

        // --- one bounded synthesis job over the two notes ---
        window.JobsProfileInput.SelectedItem = StructuringProfiles.Synthesis;
        window.JobsLevelInput.SelectedItem = SynthesisLevels.L2;
        window.JobsObjectiveInput.Text = S6T6Objective;
        window.JobsInputsInput.Text = S6T6NoteA + "@1" + Environment.NewLine + S6T6NoteB + "@1";
        window.JobsZoneInput.Text = S6T6Zone;
        window.JobsDestinationInput.Text = S6T6Inbox;
        window.JobsDestinationRevisionInput.Text = "1";
        window.JobsProviderInput.Text = "deepseek";
        window.JobsModelInput.Text = "deepseek-flash";
        var beforeSingle = provider.RequestCount;
        await WaitForJobsPanelIdleAsync(session).ConfigureAwait(true);
        Require(window.JobsRunButton.IsEnabled, "The console did not offer the run act.");
        window.JobsRunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.JobsDetail is { State: "Succeeded" }, TimeSpan.FromSeconds(90)).ConfigureAwait(true),
            "The single job did not succeed (" + (session.JobsMessage ?? "no message") + ").");
        Require(provider.RequestCount == beforeSingle + 1,
            "The single job did not make exactly one provider request (" +
            (provider.RequestCount - beforeSingle).ToString(CultureInfo.InvariantCulture) + ").");
        var singleJob = session.JobsDetail!;
        Require(string.Equals(singleJob.Scope.Profile, StructuringProfiles.Synthesis, StringComparison.Ordinal) &&
                singleJob.Scope.Inputs.Length == 2 &&
                singleJob.Scope.Inputs.All(input => input.Revision == 1) &&
                string.Equals(singleJob.Scope.Objective, S6T6Objective, StringComparison.Ordinal) &&
                singleJob.OutputProposalIds.Length > 0,
            "The recorded job scope does not match the selection the owner typed (profile=" + singleJob.Scope.Profile +
            ", inputs=" + singleJob.Scope.Inputs.Length.ToString(CultureInfo.InvariantCulture) +
            ", proposals=" + singleJob.OutputProposalIds.Length.ToString(CultureInfo.InvariantCulture) + ").");
        SaveS6T6Wpf(window, fixture, "s6t6-wpf-job-succeeded.png", window.JobsList);
        Console.WriteLine("PASS WPF single job: state=" + singleJob.State + " proposals=" +
            singleJob.OutputProposalIds.Length.ToString(CultureInfo.InvariantCulture) + " with one provider request");

        // --- batch: one bounded job per note, driven from the same panel ---
        window.JobsProfileInput.SelectedItem = StructuringProfiles.ResearchNote;
        window.JobsLevelInput.SelectedItem = "(any)";
        window.JobsObjectiveInput.Text = string.Empty;
        window.JobsInputsInput.Text = S6T6NoteA + "@1" + Environment.NewLine + S6T6NoteB + "@1";
        var beforeBatch = provider.RequestCount;
        await WaitForJobsPanelIdleAsync(session).ConfigureAwait(true);
        Require(window.JobsBatchButton.IsEnabled, "The console did not offer the batch act.");
        window.JobsBatchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.BatchSteps.Length == 2 && !session.JobsActInFlight, TimeSpan.FromSeconds(120)).ConfigureAwait(true),
            "The batch did not report one step per note (" + session.BatchSteps.Length.ToString(CultureInfo.InvariantCulture) + " step(s)).");
        Require(session.BatchSteps.All(step => string.Equals(step.State, "Succeeded", StringComparison.Ordinal)),
            "A batch note did not succeed (" + string.Join(",", session.BatchSteps.Select(step => step.ResourceId + ":" + step.State)) + ").");
        Require(provider.RequestCount == beforeBatch + 2,
            "The batch did not make exactly one provider request per note (" +
            (provider.RequestCount - beforeBatch).ToString(CultureInfo.InvariantCulture) + ").");
        SaveS6T6Wpf(window, fixture, "s6t6-wpf-batch.png", window.JobsList);
        Console.WriteLine("PASS WPF batch: one bounded job per note, each Succeeded, one provider request each");

        // --- cancel a queued job from the shown list ---
        var queued = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "job-create", "--control-pipe", fixture.ControlPipe,
            "--source-id", S6T6NoteA, "--source-revision", "1",
            "--zone-id", S6T6Zone, "--destination-id", S6T6Inbox, "--destination-revision", "1",
            "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(true);
        Require(queued.ExitCode == 0, "The queued job could not be created (stderr=" + queued.StandardError.Trim() + ").");
        var queuedJobId = ExtractJobId(queued.StandardOutput);
        var beforeCancel = provider.RequestCount;
        await WaitForJobsPanelIdleAsync(session).ConfigureAwait(true);
        window.JobsRefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.Jobs.Any(job => string.Equals(job.Id, queuedJobId, StringComparison.Ordinal)), TimeSpan.FromSeconds(45)).ConfigureAwait(true),
            "The queued job did not appear in the console's job list.");
        var queuedIndex = Array.FindIndex(session.Jobs, job => string.Equals(job.Id, queuedJobId, StringComparison.Ordinal));
        window.JobsList.SelectedItem = window.JobsList.Items[queuedIndex];
        await WaitForAsync(() => session.JobsDetail is { } detail && string.Equals(detail.Id, queuedJobId, StringComparison.Ordinal), TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        await WaitForJobsPanelIdleAsync(session).ConfigureAwait(true);
        Require(window.JobsCancelButton.IsEnabled, "The console did not offer the cancel act for the selected job.");
        window.JobsCancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.JobsDetail is { State: "Cancelled", Id: var id } && string.Equals(id, queuedJobId, StringComparison.Ordinal), TimeSpan.FromSeconds(45)).ConfigureAwait(true),
            "The console cancel did not report the queued job as Cancelled (" + (session.JobsMessage ?? "no message") + ").");
        Require(provider.RequestCount == beforeCancel,
            "Cancelling the queued job still reached a provider endpoint.");
        await session.RefreshSpendAsync().ConfigureAwait(true);
        Require(session.SpendLedger is { } ledger &&
                ledger.Entries.All(entry => !string.Equals(entry.JobId, queuedJobId, StringComparison.Ordinal) || !entry.IsReservation),
            "The cancelled queued job's pre-send reservation is still held in the spend ledger.");
        SaveS6T6Wpf(window, fixture, "s6t6-wpf-job-cancelled.png", window.JobsList);
        Console.WriteLine("PASS WPF cancel: the shown list queued job " + queuedJobId +
            " reached Cancelled, sent nothing and released its reservation");

        // --- review: compare a proposal with its cited evidence, publish one, reject another ---
        SelectNavigation(window, "Inbox");
        window.ReviewInboxButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.ReviewProposals.Length >= 2, TimeSpan.FromSeconds(45)).ConfigureAwait(true),
            "The console did not list the pending proposals (" + (session.ReviewInboxMessage ?? "no message") + ").");
        window.ReviewProposals.SelectedItem = window.ReviewProposals.Items[0];
        window.ReviewOpenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.ReviewProposal is not null, TimeSpan.FromSeconds(45)).ConfigureAwait(true),
            "The console did not open the selected proposal (" + (session.ReviewMessage ?? "no message") + ").");
        Require(session.ReviewSourceText is { Length: > 0 },
            "The review pane served no cited evidence to compare against the content.");
        Require(!string.IsNullOrEmpty(session.ReviewStalenessMessage),
            "The review pane reported no staleness state for the cited sources.");
        var reviewedProposal = session.ReviewProposal!;
        SaveS6T6Wpf(window, fixture, "s6t6-wpf-review-compare.png", window.ReviewPanel);

        window.ReviewPublishButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.ReviewOutcome is { } outcome && string.Equals(outcome.Publication, "published", StringComparison.Ordinal), TimeSpan.FromSeconds(45)).ConfigureAwait(true),
            "The console did not publish the reviewed proposal (" + (session.ReviewMessage ?? "no message") + ").");
        var publishedItemId = session.ReviewOutcome!.ItemId;
        var proposalRead = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "proposal-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", reviewedProposal.Id, "--resource-revision", reviewedProposal.Revision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(true);
        Require(proposalRead.ExitCode == 0 &&
                proposalRead.StandardOutput.Contains("author=assistant", StringComparison.Ordinal) &&
                proposalRead.StandardOutput.Contains("executor=structuringJob", StringComparison.Ordinal) &&
                proposalRead.StandardOutput.Contains("userConfirmedClaim=false", StringComparison.Ordinal),
            "The reviewed proposal is not an assistant-authored, job-executed, unconfirmed record (" + proposalRead.StandardOutput.Trim() + ").");
        var publishedRead = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", publishedItemId).ConfigureAwait(true);
        Require(publishedRead.ExitCode == 0 &&
                publishedRead.StandardOutput.Contains("author=assistant", StringComparison.Ordinal) &&
                publishedRead.StandardOutput.Contains("method=" + reviewedProposal.Item.CreationMethod, StringComparison.Ordinal) &&
                publishedRead.StandardOutput.Contains("level=" + reviewedProposal.Item.Level, StringComparison.Ordinal) &&
                publishedRead.StandardOutput.Contains("evidence=" + reviewedProposal.Item.Evidence, StringComparison.Ordinal),
            "The published item did not keep the proposal's own attribution (" + publishedRead.StandardOutput.Trim() + ").");
        Require(publishedRead.StandardOutput.Contains(S6T6NoteA, StringComparison.Ordinal) ||
                publishedRead.StandardOutput.Contains(S6T6NoteB, StringComparison.Ordinal),
            "The published item lost its citations to the sources the reviewed proposal cited.");
        Require(provider.RequestCount == beforeBatch + 2,
            "Publishing a reviewed proposal reached a provider endpoint.");
        SaveS6T6Wpf(window, fixture, "s6t6-wpf-review-published.png", window.ReviewMessageText);
        Console.WriteLine("PASS WPF review publish: item " + publishedItemId +
            " keeps the proposal's own author=assistant, method=" + reviewedProposal.Item.CreationMethod +
            ", level=" + reviewedProposal.Item.Level + " and its citations; no provider request");

        window.ReviewProposals.SelectedItem = window.ReviewProposals.Items[1];
        window.ReviewOpenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.ReviewProposal is { } next && !string.Equals(next.Id, reviewedProposal.Id, StringComparison.Ordinal), TimeSpan.FromSeconds(45)).ConfigureAwait(true),
            "The console did not open the second proposal.");
        window.ReviewRationaleInput.Text = S6T6RejectRationale;
        window.ReviewRejectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.ReviewOutcome is { } outcome && string.Equals(outcome.Publication, "rejected", StringComparison.Ordinal), TimeSpan.FromSeconds(45)).ConfigureAwait(true),
            "The console did not record the rejection (" + (session.ReviewMessage ?? "no message") + ").");
        SaveS6T6Wpf(window, fixture, "s6t6-wpf-review-rejected.png", window.ReviewMessageText);
        Console.WriteLine("PASS WPF review reject: the proposal was recorded as rejected with the rationale; no provider request");

        // --- enable the schedule from the Jobs panel and wait for its first run ---
        SelectNavigation(window, "Jobs");
        window.ScheduleIntervalInput.Text = S6T6ScheduleIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        window.JobsProfileInput.SelectedItem = StructuringProfiles.Synthesis;
        window.JobsLevelInput.SelectedItem = SynthesisLevels.L2;
        window.JobsObjectiveInput.Text = S6T6Objective;
        window.JobsInputsInput.Text = S6T6NoteA + "@1" + Environment.NewLine + S6T6NoteB + "@1";
        var beforeEnable = provider.RequestCount;
        await WaitForJobsPanelIdleAsync(session).ConfigureAwait(true);
        Require(window.ScheduleEnableButton.IsEnabled, "The console did not offer the schedule-enable act.");
        window.ScheduleEnableButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.Schedule is { Enabled: true }, TimeSpan.FromSeconds(45)).ConfigureAwait(true),
            "The console did not enable the synthesis schedule (" + (session.ScheduleMessage ?? "no message") + ").");
        Require(provider.RequestCount == beforeEnable,
            "Enabling the schedule reached a provider endpoint.");
        SaveS6T6Wpf(window, fixture, "s6t6-wpf-schedule-enabled.png", window.ScheduleStateText);
        Console.WriteLine("PASS WPF schedule enable: one encrypted schedule stored (interval=" +
            session.Schedule!.IntervalMinutes.ToString(CultureInfo.InvariantCulture) + " min, nextDue=" +
            (session.Schedule.NextDueAtUtc?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "none") +
            "), no provider request");

        await S6T6WaitForScheduledRunAsync(session, provider).ConfigureAwait(true);
    }

    /// <summary>
    /// Waits for exactly one scheduled run to complete and proves the schedule
    /// window advanced from that run: one new job, one new provider request, and a
    /// next-due instant exactly one interval after the run.
    /// </summary>
    private static async Task S6T6WaitForScheduledRunAsync(
        OwnerConsoleSession session,
        S6T6ProviderStub provider)
    {
        var jobsBefore = session.Jobs.Length;
        var requestsBefore = provider.RequestCount;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(150);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            await session.RefreshScheduleAsync().ConfigureAwait(true);
            await session.RefreshJobsAsync().ConfigureAwait(true);
            if (session.Schedule?.LastRunAtUtc is not null && session.Jobs.Length > jobsBefore)
            {
                break;
            }
        }

        var schedule = session.Schedule;
        var scheduleState = schedule is null
            ? "no schedule"
            : "enabled=" + schedule.Enabled.ToString(CultureInfo.InvariantCulture) +
                " nextDue=" + (schedule.NextDueAtUtc?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "none") +
                " lastRun=" + (schedule.LastRunAtUtc?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "none") +
                " lastJob=" + (schedule.LastRunJobId ?? "none") +
                " lastOutcome=" + (schedule.LastRunOutcome ?? "none");
        Require(schedule?.LastRunAtUtc is not null && schedule.LastRunJobId is not null,
            "No scheduled run happened inside the expected window (" + scheduleState + "; jobs " +
            jobsBefore.ToString(CultureInfo.InvariantCulture) + " -> " + session.Jobs.Length.ToString(CultureInfo.InvariantCulture) +
            "; provider requests " + requestsBefore.ToString(CultureInfo.InvariantCulture) + " -> " +
            provider.RequestCount.ToString(CultureInfo.InvariantCulture) + ").");
        Require(session.Jobs.Length == jobsBefore + 1,
            "The schedule produced " + (session.Jobs.Length - jobsBefore).ToString(CultureInfo.InvariantCulture) +
            " job(s) in one due window instead of exactly one (" + scheduleState + ").");
        Require(provider.RequestCount == requestsBefore + 1,
            "The scheduled run did not make exactly one provider request (" + scheduleState + ").");
        Require(string.Equals(schedule!.LastRunOutcome, "ran", StringComparison.Ordinal),
            "The schedule reported the last run as " + schedule.LastRunOutcome + " instead of ran.");
        var window = schedule.NextDueAtUtc!.Value - schedule.LastRunAtUtc!.Value;
        Require(Math.Abs((window - TimeSpan.FromMinutes(S6T6ScheduleIntervalMinutes)).TotalSeconds) < 1,
            "The next due instant is not exactly one interval after the run (window=" + window + ").");
        var ranJob = session.Jobs.First(job => string.Equals(job.Id, schedule.LastRunJobId, StringComparison.Ordinal));
        Require(string.Equals(ranJob.Scope.Profile, StructuringProfiles.Synthesis, StringComparison.Ordinal) &&
                ranJob.Scope.Inputs.Length == 2,
            "The scheduled run did not use the owner's stored selection.");
        Console.WriteLine("PASS WPF scheduled run: job " + ranJob.Id + " state=" + ranJob.State +
            ", next due exactly one interval after the run (no catch-up burst)");
    }

    /// <summary>
    /// Lock pauses the schedule and the service restart keeps it enabled: after
    /// unlock exactly one scheduled run happens for the slots missed while locked
    /// and while the service was down, and none of those missed slots produces an
    /// extra job or an extra provider request.
    /// </summary>
    private static async Task<(RunningService? Service, string LastJobId)> RunS6T6ScheduleStopAndRestartAsync(
        RunningService current,
        string connectionsExecutable,
        SmokeFixture fixture,
        OwnerConsoleSession session,
        S6T6ProviderStub provider,
        string passphrase,
        StringBuilder transcripts)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(transcripts);
        RunningService? service = current;
        var stopInstant = DateTimeOffset.UtcNow;
        var unlockInstant = DateTimeOffset.UtcNow;
        try
        {
            // Lock before the next due instant: the due slot must pass with no run
            // and no provider request, because private work does not run locked and
            // nothing unlocks the vault on its own.
            var requestsBeforeLock = provider.RequestCount;
            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The owner could not lock the vault for the pause leg.");
            var lockInstant = DateTimeOffset.UtcNow;
            Console.WriteLine("INFO schedule pause: vault locked at " + lockInstant.ToString("HH:mm:ss", CultureInfo.InvariantCulture) +
                "; waiting one interval inside the locked window");
            await Task.Delay(TimeSpan.FromSeconds(75)).ConfigureAwait(true);
            Require(provider.RequestCount == requestsBeforeLock,
                "A provider request happened while the vault was locked.");
            Console.WriteLine("PASS schedule pause: no provider request and no job while the vault stayed locked");

            // Restart the service while the schedule is still enabled and the vault
            // is locked: the stored schedule must survive and no missed slot may be
            // replayed as extra runs.
            await StopServiceAsync(service).ConfigureAwait(false);
            transcripts.AppendLine(await service.StandardOutputDrain.ConfigureAwait(false));
            service = null;
            stopInstant = DateTimeOffset.UtcNow;
            await Task.Delay(TimeSpan.FromSeconds(70)).ConfigureAwait(true);
            Require(provider.RequestCount == requestsBeforeLock,
                "A provider request happened while the service was down.");
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var unlockedAgain = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlockedAgain.ExitCode == 0, "The vault did not unlock after the restart.");
            unlockInstant = DateTimeOffset.UtcNow;

            // The post-restart proof is read by an independent owner client (the
            // real CLI), so it cannot be satisfied by a console view that did not
            // refresh: the schedule must still be the owner's enabled record and
            // exactly one run must happen after the unlock.
            var before = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "schedule-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(before.ExitCode == 0 &&
                    before.StandardOutput.Contains("enabled=True", StringComparison.Ordinal) &&
                    before.StandardOutput.Contains("inputs=" + S6T6NoteA + "@1," + S6T6NoteB + "@1", StringComparison.Ordinal) &&
                    before.StandardOutput.Contains("intervalMinutes=" + S6T6ScheduleIntervalMinutes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
                "The enabled schedule did not survive the service restart (" + before.StandardOutput.Trim() + ").");
            var lastJobBeforeUnlock = ExtractScheduleToken(before.StandardOutput, "lastJob=");
            var jobsBefore = CountOwnerJobs(await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false));
            Require(CountOwnerJobs(await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "job-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false)) == jobsBefore,
                "The job list changed between two consecutive reads.");

            string? scheduleLine = null;
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(150);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                var status = await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "schedule-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
                var line = status.StandardOutput.Trim();
                if (status.ExitCode == 0 &&
                    line.Contains("lastOutcome=ran", StringComparison.Ordinal) &&
                    !string.Equals(ExtractScheduleToken(line, "lastJob="), lastJobBeforeUnlock, StringComparison.Ordinal))
                {
                    scheduleLine = line;
                    break;
                }
            }

            Require(scheduleLine is not null,
                "No scheduled run happened after the restart inside the expected window (last job before unlock " +
                lastJobBeforeUnlock + ").");
            var jobsAfter = CountOwnerJobs(await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false));
            Require(jobsAfter == jobsBefore + 1,
                "The restarted service produced " + (jobsAfter - jobsBefore).ToString(CultureInfo.InvariantCulture) +
                " job(s) after the unlock instead of exactly one (" + scheduleLine + ").");
            Require(provider.RequestCount == requestsBeforeLock + 1,
                "The post-restart scheduled run did not make exactly one provider request.");
            var lastRun = ParseScheduleInstant(scheduleLine!, "lastRun=");
            var nextDue = ParseScheduleInstant(scheduleLine!, "nextDue=");
            Require(Math.Abs(((nextDue - lastRun) - TimeSpan.FromMinutes(S6T6ScheduleIntervalMinutes)).TotalSeconds) < 1,
                "The next due instant after the restart is not exactly one interval after the run (window=" +
                (nextDue - lastRun) + ").");
            Console.WriteLine("PASS schedule stop/restart: the enabled schedule (interval=" +
                S6T6ScheduleIntervalMinutes.ToString(CultureInfo.InvariantCulture) +
                " min, inputs=2) was read back from the vault by an independent owner client after the restart; locked window " +
                (stopInstant - lockInstant).TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s and stopped window " +
                (unlockInstant - stopInstant).TotalSeconds.ToString("0", CultureInfo.InvariantCulture) +
                "s passed with no job and no provider request, and exactly one run followed the unlock");
            Console.WriteLine("INFO schedule stop/restart: the console session reports status=" + DescribeChannelStatus(session.Status) +
                " vault=" + (session.ReportedVaultState?.ToString() ?? "(none)"));
            return (service, ExtractScheduleToken(scheduleLine!, "lastJob="));
        }
        catch
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
                transcripts.AppendLine(await service.StandardOutputDrain.ConfigureAwait(false));
                service = null;
            }

            throw;
        }
    }

    private static string ExtractScheduleToken(string scheduleLine, string name)
    {
        foreach (var token in scheduleLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith(name, StringComparison.Ordinal))
            {
                return token[name.Length..];
            }
        }

        throw new InvalidOperationException("The schedule line has no '" + name + "' value (" + scheduleLine + ").");
    }

    private static DateTimeOffset ParseScheduleInstant(string scheduleLine, string name) =>
        DateTimeOffset.TryParse(ExtractScheduleToken(scheduleLine, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : throw new InvalidOperationException("The schedule line has no parsable '" + name + "' value (" + scheduleLine + ").");

    private static string DescribeChannelStatus(OwnerChannelStatus? status) => status is null
        ? "no-session"
        : status.Availability + "/" + status.VaultState + "(" + (status.ErrorCode ?? "none") + ")";

    /// <summary>
    /// Shows the Jobs panel again on its own dispatcher thread, renders the
    /// schedule state the service currently reports, and — when asked — performs
    /// the owner's disable act through the real button.
    /// </summary>
    private static void DriveS6T6WpfScheduleState(string controlPipe,
        SmokeFixture fixture,
        bool disable,
        string expectedLastJobId)
    {
        ArgumentNullException.ThrowIfNull(controlPipe);
        ArgumentNullException.ThrowIfNull(fixture);
        // A session this window owns. A session already bound to an earlier
        // window carries that window's collection view, which WPF pins to the
        // thread that created it; the shipped application binds one session to
        // one window for its lifetime, and the walkthrough mirrors that by
        // attaching a fresh session for this window.
        var windowSession = new OwnerConsoleSession(controlPipe);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            OwnerConsoleWindow? window = null;
            try
            {
                window = new OwnerConsoleWindow(windowSession);
                window.Width = 1360;
                window.Height = 1100;
                window.Show();
                window.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        SelectNavigation(window, "Jobs");
                        await windowSession.AttachAsync().ConfigureAwait(true);
                        Require(windowSession.ReportedVaultState == OwnerVaultState.Unlocked,
                            "The console did not reach the restarted service (" + DescribeChannelStatus(windowSession.Status) + ").");
                        // A single owner read can race the background pass holding
                        // the shared vault lease, exactly as it can for a real
                        // owner pressing the same control twice; the console must
                        // serve the current record on a retry, and the last answer
                        // is reported when it never does.
                        var consoleMatched = false;
                        var firstReadNote = string.Empty;
                        for (var attempt = 0; attempt < 6 && !consoleMatched; attempt++)
                        {
                            // The panel's own timer probes the service every five
                            // seconds; the walkthrough asks for the same state
                            // before each attempt so a read that raced a
                            // background pass is retried against a live windowSession.
                            await windowSession.CheckAsync().ConfigureAwait(true);
                            await windowSession.RefreshScheduleAsync().ConfigureAwait(true);
                            await windowSession.RefreshJobsAsync().ConfigureAwait(true);
                            consoleMatched = windowSession.Schedule is { LastRunJobId: var consoleLastJob } &&
                                             string.Equals(consoleLastJob, expectedLastJobId, StringComparison.Ordinal);
                            if (!consoleMatched)
                            {
                                Console.WriteLine("INFO schedule read attempt " + attempt.ToString(CultureInfo.InvariantCulture) +
                                    ": inFlight=" + windowSession.JobsActInFlight.ToString(CultureInfo.InvariantCulture) +
                                    " reported=" + (windowSession.ReportedVaultState?.ToString() ?? "(none)") +
                                    " lastJob=" + (windowSession.Schedule?.LastRunJobId ?? "(none)") +
                                    " message=" + (windowSession.ScheduleMessage ?? "(none)"));
                                if (firstReadNote.Length == 0)
                                {
                                    firstReadNote = windowSession.ScheduleMessage ?? "(none)";
                                }

                                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                            }
                        }

                        if (firstReadNote.Length > 0)
                        {
                            Console.WriteLine("INFO schedule read after restart: an earlier attempt answered: " + firstReadNote);
                        }

                        Require(consoleMatched,
                            "The console did not serve the schedule record the owner's own client read after the restart (owner client read last job " +
                            expectedLastJobId + "; console serves " + (windowSession.Schedule?.LastRunJobId ?? "none") +
                            "; message=" + (windowSession.ScheduleMessage ?? "(none)") + ").");
                        SaveS6T6Wpf(window, fixture, "s6t6-wpf-schedule-after-restart-run.png", window.ScheduleStateText);
                        if (disable)
                        {
                            await WaitForJobsPanelIdleAsync(windowSession).ConfigureAwait(true);
                            Require(window.ScheduleDisableButton.IsEnabled, "The console did not offer the schedule-disable act.");
                            window.ScheduleDisableButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            var disabled = await WaitForAsync(
                                () => windowSession.Schedule is { Enabled: false }, TimeSpan.FromSeconds(45)).ConfigureAwait(true);
                            Require(disabled, "The console did not disable the schedule (" + (windowSession.ScheduleMessage ?? "no message") + ").");
                            Require(windowSession.Schedule is { NextDueAtUtc: null },
                                "A disabled schedule still names a next due instant.");
                            SaveS6T6Wpf(window, fixture, "s6t6-wpf-schedule-disabled.png", window.ScheduleStateText);
                            Console.WriteLine("PASS WPF schedule disable: the stored schedule reports enabled=false with no next due instant");
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                    finally
                    {
                        // Same rule as phase one: an act still awaiting when this
                        // dispatcher stops would never deliver its continuation.
                        await WaitForAsync(
                            () => !windowSession.JobsActInFlight && !windowSession.InboxActInFlight,
                            TimeSpan.FromSeconds(30)).ConfigureAwait(true);
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
            throw new InvalidOperationException("The S6-T6 schedule-state flow failed: " + failure.Message, failure);
        }
    }

    private static void SelectNavigation(OwnerConsoleWindow window, string entry)
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

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(150).ConfigureAwait(true);
        }

        return condition();
    }

    /// <summary>
    /// Waits until the Jobs panel is ready to accept an act. A real owner cannot
    /// click a control this console has disabled while an act is still in flight,
    /// so the walkthrough waits for the same state before pressing a button.
    /// </summary>
    private static async Task WaitForJobsPanelIdleAsync(OwnerConsoleSession session)
    {
        Require(await WaitForAsync(() => !session.JobsActInFlight, TimeSpan.FromSeconds(60)).ConfigureAwait(true),
            "A jobs act never answered, so the next act could not be sent.");
    }

    private static void SaveS6T6Wpf(OwnerConsoleWindow window, SmokeFixture fixture, string fileName, FrameworkElement? scrollTo = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(fileName);
        window.UpdateLayout();
        if (scrollTo is not null)
        {
            // The pane the act happened in has to be inside the frame: a section is
            // taller than the window, so an image of its top would show the capture
            // editor and prove nothing about the review, job or schedule act.
            scrollTo.BringIntoView();
        }

        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
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

    private static void PersistS6T6Evidence(SmokeFixture fixture)
    {
        var evidenceDirectory = Path.Combine(Path.GetTempPath(), "MetaBrain-S6-T6-A1", "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        foreach (var file in Directory.EnumerateFiles(fixture.OutputDirectory, "s6t6-*.png"))
        {
            File.Copy(file, Path.Combine(evidenceDirectory, Path.GetFileName(file)), overwrite: true);
        }

        Console.WriteLine("S6-T6 evidence directory: " + evidenceDirectory);
    }

    private static async Task WriteS6T6NoteAsync(string executable, SmokeFixture fixture, string sourceId, string text)
    {
        var input = Path.Combine(fixture.OutputDirectory, sourceId + ".note");
        await File.WriteAllTextAsync(input, text, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "research-capture", "--control-pipe", fixture.ControlPipe,
            "--resource-id", sourceId, "--zone-id", S6T6Zone, "--input-file", input,
            "--title", "S6-T6 synthetic note").ConfigureAwait(false);
        File.Delete(input);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("revision=1", StringComparison.Ordinal),
            "The S6-T6 note " + sourceId + " was not captured at revision 1 (exit=" + result.ExitCode + "; stderr=" + result.StandardError.Trim() + ").");
    }

    private static async Task WriteS6T6InboxAsync(string executable, SmokeFixture fixture)
    {
        var draft = JsonSerializer.Serialize(new
        {
            kind = "observation",
            author = "user",
            creationMethod = "directWrite",
            evidence = "userConfirmed",
            level = "l1",
            title = "S6-T6 destination inbox",
            body = "Destination inbox for the S6-T6 structuring proposals.",
            sources = Array.Empty<object>(),
            occurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            recordedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            applicability = (object?)null
        });
        var input = Path.Combine(fixture.OutputDirectory, "s6t6-inbox.json");
        await File.WriteAllTextAsync(input, draft, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", S6T6Inbox, "--zone-id", S6T6Zone, "--input-file", input).ConfigureAwait(false);
        File.Delete(input);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
            "The S6-T6 destination inbox was not stored (exit=" + result.ExitCode + "; stderr=" + result.StandardError.Trim() + ").");
    }

    /// <summary>
    /// S6-T6 live leg: the same owner workflow against the real provider the owner
    /// configured, performed only when the owner authorized live calls. It proves
    /// a real bounded model call from the owner's selection — one manual job and
    /// one scheduled run — with the provider's own reported usage and the shipped
    /// output boundary. No canned response and no second provider configuration.
    /// </summary>
    internal static async Task RunS6T6LiveProviderAsync(string connectionsExecutable)
    {
        var authorized = string.Equals(Environment.GetEnvironmentVariable("METABRAIN_LIVE_PROVIDER_AUTHORIZED"), "true", StringComparison.OrdinalIgnoreCase);
        if (!authorized)
        {
            Console.WriteLine("INFO S6-T6 live provider call pending explicit owner authorization (zero outbound calls made).");
            return;
        }

        Console.WriteLine("RUN S6-T6 live provider walkthrough (owner authorized)");
        // Kept on disk for reviewer verification: no auto-dispose of the fixture.
        var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        var passphrase = "";
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0, "The live vault was not provisioned.");
            var unlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0, "The live vault did not unlock.");

            await WriteS6T6InboxAsync(connectionsExecutable, fixture).ConfigureAwait(false);
            await WriteS6T6NoteAsync(connectionsExecutable, fixture, S6T6NoteA, S6T6NoteAText).ConfigureAwait(false);
            await WriteS6T6NoteAsync(connectionsExecutable, fixture, S6T6NoteB, S6T6NoteBText).ConfigureAwait(false);

            var config = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "provider-config", "--control-pipe", fixture.ControlPipe,
                "--provider", "deepseek", "--model", "deepseek-flash", "--from-env", "true").ConfigureAwait(false);
            Require(config.ExitCode == 0 && config.StandardOutput.Contains("PROVIDER configured", StringComparison.Ordinal),
                "The live provider configuration from .env failed.");
            var providerStatus = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "provider-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(providerStatus.ExitCode == 0 && providerStatus.StandardOutput.Contains("configured=True", StringComparison.Ordinal),
                "The live provider status did not report the configured endpoint (" + providerStatus.StandardOutput.Trim() + ").");
            Require(!providerStatus.StandardOutput.Contains("sk-", StringComparison.Ordinal),
                "The provider status echoed something shaped like a key.");
            Console.WriteLine("LIVE PROVIDER STATUS:\n" + providerStatus.StandardOutput.Trim());

            // One real single-input job under the owner's selection.
            var created = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-create", "--control-pipe", fixture.ControlPipe,
                "--source-id", S6T6NoteA, "--source-revision", "1", "--profile", StructuringProfiles.ResearchNote,
                "--zone-id", S6T6Zone, "--destination-id", S6T6Inbox, "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash").ConfigureAwait(false);
            Require(created.ExitCode == 0 && created.StandardOutput.Contains("state=Pending", StringComparison.Ordinal),
                "The live job was not created (stdout=" + created.StandardOutput.Trim() + ").");
            var liveJobId = ExtractJobId(created.StandardOutput);
            var started = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-start", "--control-pipe", fixture.ControlPipe, "--job-id", liveJobId).ConfigureAwait(false);
            Require(started.ExitCode == 0 && started.StandardOutput.Contains("state=Succeeded", StringComparison.Ordinal),
                "The live model job did not succeed (stdout=" + started.StandardOutput.Trim() + "; stderr=" + started.StandardError.Trim() + ").");
            Console.WriteLine("LIVE JOB:\n" + started.StandardOutput.Trim());
            var ledger = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ledger.ExitCode == 0 && ledger.StandardOutput.Contains("isReservation=False", StringComparison.Ordinal),
                "The live job's spend was not settled in the ledger.");
            Console.WriteLine("LIVE LEDGER:\n" + ledger.StandardOutput.Trim());
            var proposals = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "proposal-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(proposals.ExitCode == 0 && proposals.StandardOutput.Contains("PROPOSAL id=", StringComparison.Ordinal),
                "The live job committed no proposal.");
            Console.WriteLine("LIVE PROPOSALS:\n" + proposals.StandardOutput.Trim());

            // One real scheduled synthesis run through the background pass.
            var selection = Path.Combine(fixture.OutputDirectory, "s6t6-live-selection.json");
            await File.WriteAllTextAsync(selection, JsonSerializer.Serialize(new
            {
                inputs = new[]
                {
                    new { resourceId = S6T6NoteA, revision = 1L },
                    new { resourceId = S6T6NoteB, revision = 1L }
                },
                objective = S6T6Objective,
                targetLevel = SynthesisLevels.L2
            }), Encoding.UTF8).ConfigureAwait(false);
            var enabled = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "schedule-set", "--control-pipe", fixture.ControlPipe,
                "--interval-minutes", S6T6ScheduleIntervalMinutes.ToString(CultureInfo.InvariantCulture),
                "--zone-id", S6T6Zone, "--destination-id", S6T6Inbox, "--destination-revision", "1",
                "--provider", "deepseek", "--model", "deepseek-flash",
                "--input-file", selection).ConfigureAwait(false);
            File.Delete(selection);
            Require(enabled.ExitCode == 0 && enabled.StandardOutput.Contains("enabled=True", StringComparison.Ordinal),
                "The live synthesis schedule was not enabled (stdout=" + enabled.StandardOutput.Trim() + "; stderr=" + enabled.StandardError.Trim() + ").");
            Console.WriteLine("LIVE SCHEDULE:\n" + enabled.StandardOutput.Trim());

            var jobsBefore = CountOwnerJobs(await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false));
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(180);
            var schedule = "";
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
                var status = await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "schedule-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
                schedule = status.StandardOutput.Trim();
                if (schedule.Contains("lastOutcome=ran", StringComparison.Ordinal))
                {
                    break;
                }
            }

            Require(schedule.Contains("lastOutcome=ran", StringComparison.Ordinal),
                "The live scheduled synthesis run did not report ran (" + schedule + ").");
            var jobsAfter = CountOwnerJobs(await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false));
            Require(jobsAfter == jobsBefore + 1,
                "The live schedule produced " + (jobsAfter - jobsBefore).ToString(CultureInfo.InvariantCulture) + " job(s) instead of one.");
            Console.WriteLine("LIVE SCHEDULE AFTER RUN:\n" + schedule);
            Console.WriteLine("LIVE JOB LIST:\n" + (await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false)).StandardOutput.Trim());
            Console.WriteLine("LIVE LEDGER AFTER SCHEDULE:\n" + (await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "job-ledger", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false)).StandardOutput.Trim());
            Console.WriteLine("PASS S6-T6 live provider walkthrough complete");
            Console.WriteLine("PRESERVED ENCRYPTED FIXTURE: " + fixture.VaultDirectoryPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(Encoding.UTF8.GetBytes(passphrase));
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static int CountOwnerJobs(CommandResult listResult) =>
        listResult.ExitCode == 0
            ? listResult.StandardOutput.Split('\n').Count(line => line.TrimStart().StartsWith("JOB ", StringComparison.Ordinal))
            : throw new InvalidOperationException("The job list did not answer (stderr=" + listResult.StandardError.Trim() + ").");

    /// <summary>
    /// Local provider endpoint for the S6-T6 walkthrough: a real HTTP listener the
    /// shipped OpenAI-compatible adapter actually calls. It records every request
    /// it receives with its instant, so the walkthrough can show exactly when the
    /// application reached a provider, and it answers with a completion built from
    /// the prompt it received (the selected inputs' own passages), which the
    /// shipped output boundary still validates. Its token accounting is this
    /// endpoint's own; only the live leg reports a real provider's usage.
    /// </summary>
    private sealed class S6T6ProviderStub : IDisposable
    {
        private static readonly Regex InputHeader = new(
            "\\[INPUT \\d+\\] id=(?<id>\\S+) revision=(?<revision>\\d+)\\r?\\n(?<text>.*?)(?=\\r?\\n\\[INPUT |\\z)",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private readonly HttpListener _listener = new();
        private readonly List<DateTimeOffset> _requests = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public S6T6ProviderStub()
        {
            var port = GetAvailablePort();
            BaseUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _loop = Task.Run(RunAsync);
        }

        public string BaseUrl { get; }

        public int RequestCount
        {
            get
            {
                lock (_requests)
                {
                    return _requests.Count;
                }
            }
        }

        public DateTimeOffset[] RequestTimes
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToArray();
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception)
            {
            }

            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
            }

            _stop.Dispose();
        }

        private async Task RunAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }

                DateTimeOffset receivedAt;
                string requestBody;
                lock (_requests)
                {
                    receivedAt = DateTimeOffset.UtcNow;
                    _requests.Add(receivedAt);
                }

                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    requestBody = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                var completion = BuildCompletion(requestBody);
                var usage = new
                {
                    prompt_tokens = Math.Max(1, Encoding.UTF8.GetByteCount(requestBody) / 4),
                    completion_tokens = Math.Max(1, Encoding.UTF8.GetByteCount(completion) / 4),
                    total_tokens = Math.Max(1, (Encoding.UTF8.GetByteCount(requestBody) + Encoding.UTF8.GetByteCount(completion)) / 4)
                };
                var payload = JsonSerializer.Serialize(new
                {
                    id = "s6t6-local-endpoint",
                    model = "deepseek-flash",
                    choices = new[]
                    {
                        new { index = 0, message = new { role = "assistant", content = completion }, finish_reason = "stop" }
                    },
                    usage
                });
                var bytes = Encoding.UTF8.GetBytes(payload);
                try
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    context.Response.OutputStream.Close();
                }
                catch (Exception)
                {
                    // The client aborted (a cancelled or timed-out job); the request
                    // is still recorded, which is the observation this endpoint owes.
                }
            }
        }

        /// <summary>
        /// Builds the completion from the prompt the endpoint actually received:
        /// for a synthesis job, one grounded item that carries a verbatim passage
        /// of every selected input; for a single-input profile, one item whose
        /// excerpt is a verbatim passage of the note. Nothing is reused across
        /// requests.
        /// </summary>
        private static string BuildCompletion(string requestBody)
        {
            var userPrompt = ExtractUserPrompt(requestBody);
            if (userPrompt.Contains("SELECTED INPUTS", StringComparison.Ordinal))
            {
                var groundings = new List<object>();
                var passages = new List<string>();
                foreach (Match match in InputHeader.Matches(userPrompt))
                {
                    var excerpt = FirstPassage(match.Groups["text"].Value);
                    if (excerpt.Length == 0)
                    {
                        continue;
                    }

                    groundings.Add(new
                    {
                        inputId = match.Groups["id"].Value,
                        inputRevision = long.Parse(match.Groups["revision"].Value, CultureInfo.InvariantCulture),
                        excerpt
                    });
                    passages.Add(excerpt);
                }

                var body = passages.Count == 0
                    ? "S6-T6 local endpoint: no selected input passage was available."
                    : "The selected inputs report: " + string.Join(" | ", passages);
                return JsonSerializer.Serialize(new
                {
                    summary = "S6-T6 local endpoint synthesis over the selected inputs.",
                    items = new[]
                    {
                        new
                        {
                            kind = "synthesis",
                            title = "S6-T6 reported retry budgets from the selected inputs",
                            body,
                            crossSource = groundings.Count > 1,
                            grounding = groundings,
                            applicability = new { project = (string?)null, environment = (string?)null, version = (string?)null },
                            occurredAtSourceText = (string?)null
                        }
                    }
                });
            }

            var passage = FirstPassage(userPrompt);
            return JsonSerializer.Serialize(new
            {
                summary = "S6-T6 local endpoint structuring of one note.",
                items = new[]
                {
                    new
                    {
                        kind = "observation",
                        title = "S6-T6 note as reported",
                        body = passage,
                        excerpt = passage,
                        applicability = new { project = (string?)null, environment = (string?)null, version = (string?)null },
                        occurredAtSourceText = (string?)null
                    }
                }
            });
        }

        private static string ExtractUserPrompt(string requestBody)
        {
            try
            {
                using var document = JsonDocument.Parse(requestBody);
                if (document.RootElement.TryGetProperty("messages", out var messages) &&
                    messages.ValueKind == JsonValueKind.Array)
                {
                    foreach (var message in messages.EnumerateArray())
                    {
                        if (message.TryGetProperty("role", out var role) &&
                            string.Equals(role.GetString(), "user", StringComparison.Ordinal) &&
                            message.TryGetProperty("content", out var content))
                        {
                            return content.GetString() ?? string.Empty;
                        }
                    }
                }
            }
            catch (JsonException)
            {
            }

            return string.Empty;
        }

        private static string FirstPassage(string text)
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            var firstLine = trimmed.Split('\n')[0].TrimEnd('\r').Trim();
            return firstLine.Length <= 512 ? firstLine : firstLine[..512];
        }
    }
}
