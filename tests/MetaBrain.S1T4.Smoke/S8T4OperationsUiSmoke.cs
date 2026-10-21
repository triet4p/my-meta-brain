using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MetaBrain.Connections;
using MetaBrain.OwnerUi;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S8-T4 runtime proof for the owner console's Operations &amp; audit view.
/// <para>
/// The scenario starts the real service on a synthetic vault, lets the owner
/// act on it through the real owner channel, and then drives the real WPF
/// window on its own STA thread: the owner opens the Operations view, reads
/// the health, the audit trail and the diagnostics the service reported,
/// applies a retention choice, and then locks the vault and sees the private
/// views of this view disappear. Every render is a real render of the live
/// window and holds no secret and no private content beyond the synthetic
/// fixture.
/// </para>
/// <para>
/// Every file the scenario creates is synthetic and stays inside the
/// repository's ignored runtime directory. It never changes a permission, an
/// owner, a registry entry or a machine setting.
/// </para>
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S8T4UiZone = "s8t4-ui-zone";
    private const string S8T4UiItem = "s8t4-ui-item";
    private const string S8T4UiBody = "S8T4_SYNTHETIC_UI_BODY_5e10";
    
    internal static async Task RunS8T4OperationsUiAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S8-T4 owner console proof requires Windows named pipes and WPF.");
        }

        Console.WriteLine("RUN S8-T4 owner console operations and audit view against a synthetic vault");
        using var fixture = S8T4Fixture.Create();
        RunningService? service = null;
        var passphraseBytes = RandomNumberGenerator.GetBytes(24);
        var passphrase = Convert.ToBase64String(passphraseBytes);
        CryptographicOperations.ZeroMemory(passphraseBytes);
        // The synthetic token is generated per run and exists only in this
        // process: no fixture literal that looks like a token is written into
        // the repository.
        var invalidToken = "synthetic-token-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        try
        {
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0, "The synthetic S8-T4 console vault was not provisioned (" + provisioned.StandardError.Trim() + ").");
            var unlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0, "The synthetic S8-T4 console vault did not unlock (" + unlocked.StandardError.Trim() + ").");

            // The owner acts through the real owner channel before the console
            // looks: one written record and one refused agent redemption, so
            // the view has an owner action and a real failure to show.
            var created = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "memory.create",
                resourceId = S8T4UiItem,
                zoneId = S8T4UiZone,
                contentBase64 = Convert.ToBase64String(S8T4ItemContent(S8T4UiBody)),
            }).ConfigureAwait(false);
            Require(ReplyCode(created) == "memory_created",
                "The synthetic S8-T4 console record was not written (reply=" + created.GetRawText() + ").");
            var createdRevision = created.TryGetProperty("resourceRevision", out var revisionValue) &&
                                  revisionValue.ValueKind == JsonValueKind.Number
                ? revisionValue.GetInt64()
                : 0;
            Require(createdRevision > 0,
                "The synthetic S8-T4 console record carried no revision (reply=" + created.GetRawText() + ").");
            var redeem = await InvokeS8T4AgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.redeem",
                token = invalidToken,
            }).ConfigureAwait(false);
            Require(ReplyCode(redeem) == "token_unknown",
                "The agent channel did not refuse the synthetic token (reply=" + redeem.GetRawText() + ").");

            var repositoryRoot = SmokeFixture.FindRepositoryRoot();
            var evidenceDirectory = Path.Combine(repositoryRoot, "runtime", "s8-t4", "ui-evidence");
            Directory.CreateDirectory(evidenceDirectory);

            var session = new OwnerConsoleSession(fixture.ControlPipe);
            await session.AttachAsync().ConfigureAwait(true);
            Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
                "The owner console did not report the unlocked vault (" + session.ReportedVaultState + ").");
            await DriveS8T4UiAsync(session, fixture, evidenceDirectory, connectionsExecutable, createdRevision).ConfigureAwait(true);

            var stateText = File.Exists(fixture.OperationsStatePath)
                ? await File.ReadAllTextAsync(fixture.OperationsStatePath).ConfigureAwait(false)
                : string.Empty;
            Require(!stateText.Contains(S8T4UiBody, StringComparison.Ordinal),
                "The private operations state exposed the synthetic record body in plaintext.");
            var logText = await File.ReadAllTextAsync(fixture.OperationalLogPath).ConfigureAwait(false);
            Require(!logText.Contains(S8T4UiBody, StringComparison.Ordinal) &&
                    !logText.Contains(invalidToken, StringComparison.Ordinal),
                "The safe operational log exposed the synthetic body or the synthetic token.");
            Console.WriteLine("PASS S8-T4 the private operations state and the safe operational log hold no private content");
            Console.WriteLine("LIMIT the automated run drives the real WPF window on an STA thread; it substitutes no control and reads every answer from the live session, but no human pointed a mouse at the card.");
        }
        finally
        {
            if (service is not null)
            {
                try
                {
                    if (!service.Process.HasExited)
                    {
                        service.Process.Kill(entireProcessTree: true);
                        await service.Process.WaitForExitAsync().ConfigureAwait(true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }

    /// <summary>
    /// Runs the whole view interaction on one STA thread that owns the real
    /// window, so every control, every click and every render is the product's
    /// own code path.
    /// </summary>
    private static async Task DriveS8T4UiAsync(
        OwnerConsoleSession session,
        S8T4Fixture fixture,
        string evidenceDirectory,
        string connectionsExecutable,
        long createdRevision)
    {
        ArgumentNullException.ThrowIfNull(session);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            OwnerConsoleWindow? window = null;
            try
            {
                window = new OwnerConsoleWindow(session);
                window.Width = 1280;
                window.Height = 1000;
                window.Show();
                window.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await RunS8T4UiActsAsync(window, session, evidenceDirectory, connectionsExecutable, createdRevision).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                    finally
                    {
                        window.DetachView();
                        window.Close();
                        window.Dispatcher.InvokeShutdown();
                    }
                });
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException(
                "The S8-T4 owner console proof failed: " + failure.Message + "\n" + failure.StackTrace,
                failure);
        }
    }

    private static async Task RunS8T4UiActsAsync(
        OwnerConsoleWindow window,
        OwnerConsoleSession session,
        string evidenceDirectory,
        string connectionsExecutable,
        long createdRevision)
    {
        // ----- the view opens and reads the health, the audit and the diagnostics -----
        NavigateS8T2Ui(window, "Operations & audit");
        Require(window.OperationsPanel.Visibility == Visibility.Visible,
            "The operations and audit view did not open.");
        Require(await WaitForS8T1AAsync(
                () => session.OperationsHealth is not null && session.OperationsAudit.Length > 0,
                TimeSpan.FromSeconds(30)),
            "The operations view did not read the health and the audit trail (" + session.OperationsMessage + ").");
        Require(window.OperationsHealthText.Text.Contains("Vault: unlocked", StringComparison.Ordinal),
            "The operations view did not show the unlocked vault (" + window.OperationsHealthText.Text + ").");
        Require(window.OperationsAuditList.Items.Count > 0,
            "The operations view rendered no audit row for the recorded owner action.");
        Require(window.OperationsDiagnosticsList.Items.Count > 0,
            "The operations view rendered no diagnostic row for the refused redemption.");
        Require(session.OperationsDiagnostics.Any(entry => entry.Code == "token_unknown"),
            "The operations view did not show the real token error code (" + session.OperationsMessage + ").");
        SaveS8T1AWpf(window, evidenceDirectory, "s8t4-ui-01-operations-health.png", window.ContentScrollViewer);
        Console.WriteLine("PASS S8-T4 the operations view shows the health, the actor and action of the recorded operations and the real error code of the failure");

        // ----- the retention choice is two pickers and one button -----
        window.OperationsRetentionDaysInput.SelectedItem = 7;
        window.OperationsRetentionRecordsInput.SelectedItem = 100;
        var messageBefore = session.OperationsMessage;
        ClickS8T1A(window.OperationsRetentionButton);
        Require(await WaitForS8T1AAsync(
                () => !session.OperationsActInFlight &&
                      session.OperationsMessage is not null &&
                      !string.Equals(session.OperationsMessage, messageBefore, StringComparison.Ordinal),
                TimeSpan.FromSeconds(30)),
            "The retention choice did not report an answer (" + session.OperationsMessage + ").");
        Require(session.OperationsMessage!.Contains("applied the retention choice", StringComparison.Ordinal),
            "The retention choice did not report what it did (" + session.OperationsMessage + ").");
        Require(session.OperationsRetentionDays == 7 && session.OperationsMaximumRecords == 100,
            "The service did not keep the retention choice the owner picked (" +
            session.OperationsRetentionDays.ToString(CultureInfo.InvariantCulture) + "/" +
            session.OperationsMaximumRecords.ToString(CultureInfo.InvariantCulture) + ").");
        SaveS8T1AWpf(window, evidenceDirectory, "s8t4-ui-02-operations-retention.png", window.ContentScrollViewer);
        Console.WriteLine("PASS S8-T4 the owner applied a retention choice through the pickers and the service reported exactly what it removed");

        // ----- the jobs panel shows the real code and the sentence of a refusal -----
        // The service refuses a job whose provider and model have no approved
        // pricing card, before any provider is contacted. The console must
        // show the real code and the owner's own sentence, and it must not
        // show the refusal as an unavailable transport.
        NavigateS8T2Ui(window, "Jobs");
        window.JobsProfileInput.SelectedItem = "research-note";
        window.JobsZoneInput.Text = S8T4UiZone;
        window.JobsDestinationInput.Text = S8T4UiItem;
        window.JobsDestinationRevisionInput.Text = createdRevision.ToString(CultureInfo.InvariantCulture);
        window.JobsProviderInput.Text = "synthetic-provider";
        window.JobsModelInput.Text = "synthetic-model";
        window.SetJobInputSelection((S8T4UiItem, createdRevision));
        await WaitForS8T1AAsync(
                () => !session.JobsActInFlight,
                TimeSpan.FromSeconds(30))
            .ConfigureAwait(true);
        Require(window.JobsRunButton.IsEnabled, "The console did not offer the run act.");
        window.JobsRunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForS8T1AAsync(
                () => !session.JobsActInFlight && session.JobsMessage is not null &&
                      session.JobsMessage.Contains("pricing_unknown", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30)),
            "The refused job run did not show the real code of the refusal (" + (session.JobsMessage ?? "no message") + ").");
        Require(session.JobsMessage!.Contains(
                "No approved pricing card found for 'synthetic-provider:synthetic-model'.",
                StringComparison.Ordinal),
            "The refused job run did not show the owner's own sentence of the refusal (" + session.JobsMessage + ").");
        Require(!session.JobsMessage!.Contains("unavailable", StringComparison.Ordinal) &&
                !session.JobsMessage.Contains("unexpected_reply", StringComparison.Ordinal),
            "The refused job run was shown as an unavailable transport (" + session.JobsMessage + ").");
        Require(window.JobsMessageText.Text.Contains("pricing_unknown", StringComparison.Ordinal),
            "The jobs panel did not render the real code of the refusal (" + window.JobsMessageText.Text + ").");
        SaveS8T1AWpf(window, evidenceDirectory, "s8t4-ui-04-jobs-refused.png", window.JobsMessageText);
        Console.WriteLine("PASS S8-T4 the jobs panel shows the real code pricing_unknown and the owner's own sentence of the refused run");

        // ----- the refused run is recorded in the audit trail with the same code -----
        NavigateS8T2Ui(window, "Operations & audit");
        window.OperationsRefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForS8T1AAsync(
                () => session.OperationsAudit.Any(entry =>
                    string.Equals(entry.Action, "owner.job.create", StringComparison.Ordinal) &&
                    string.Equals(entry.ErrorCode, "pricing_unknown", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(30)),
            "The operations view did not record the real code of the refused job run.");
        SaveS8T1AWpf(window, evidenceDirectory, "s8t4-ui-05-operations-refused-job.png", window.ContentScrollViewer);
        Console.WriteLine("PASS S8-T4 the audit trail of the operations view records the refused job run with the real code pricing_unknown");

        // ----- the lock clears the private views of this view -----
        var locked = await RunOwnerCommandAsync(
            connectionsExecutable,
            "owner", "lock", "--control-pipe", session.ControlPipe).ConfigureAwait(true);
        Require(locked.ExitCode == 0, "The synthetic S8-T4 console vault did not lock (" + locked.StandardError.Trim() + ").");
        Require(await WaitForS8T1AAsync(
                () => session.OperationsHealth is null && session.OperationsAudit.Length == 0,
                TimeSpan.FromSeconds(30)),
            "The lock did not clear the private operations views of the console.");
        Require(window.OperationsHealthText.Text.Contains("needs the unlocked vault", StringComparison.Ordinal),
            "The operations view still shows a private health answer after the lock (" + window.OperationsHealthText.Text + ").");
        Require(window.OperationsAuditList.Items.Count == 0,
            "The operations view still shows audit rows after the lock.");
        Require(window.OperationsRefreshButton.IsEnabled == false,
            "The operations view still offers a private read after the lock.");
        SaveS8T1AWpf(window, evidenceDirectory, "s8t4-ui-03-operations-locked.png", window.ContentScrollViewer);
        Console.WriteLine("PASS S8-T4 the lock cleared the private health, the audit rows and the private reads of the operations view");
    }
}
