using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MetaBrain.Connections;
using MetaBrain.OwnerUi;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S8-T3 runtime proof for the owner console's *Vault format* card.
/// <para>
/// The scenario starts the real service on a copy of the genuine prior-format
/// vault that <c>scripts/s8-t3/prepare-old-vaults.ps1</c> wrote from the
/// pinned historical commit, and then drives the real WPF owner window on its
/// own STA thread. The owner reads the card, types the credential into the
/// card's own password field, clicks its own upgrade and rollback buttons and
/// reads the answers the running service returned: an interrupted upgrade is
/// reported and resumed, a wrong and a missing credential are refused with
/// nothing written and the field cleared, the vault stays locked after every
/// maintenance act, the upgrade is not offered while the vault is unlocked,
/// the rollback restores only the files the upgrade wrote and reports the
/// older format again, and the discard drops the rollback point.
/// </para>
/// <para>
/// The preserved data is read through the real Library view: the window
/// rebuilds the private index, searches, opens the migrated record and shows
/// its body, revision and attribution. Every render is a real render of the
/// live window and holds no secret and no private content beyond the
/// synthetic bodies this fixture wrote.
/// </para>
/// <para>
/// Every file the scenario creates is synthetic and stays inside the
/// repository's ignored runtime directory. It never changes a permission, an
/// owner, a registry entry or a machine setting.
/// </para>
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S8T3UiItemTitleA = "S8T3_SYNTHETIC_PRIOR_ITEM_A_TITLE";
    private const string S8T3UiInterruption = "S8-T3 console interruption";

    internal static async Task RunS8T3UiAsync(string connectionsExecutable, string artifactRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S8-T3 owner console proof requires Windows named pipes and WPF.");
        }

        var fullArtifacts = Path.GetFullPath(artifactRoot);
        Require(Directory.Exists(Path.Combine(fullArtifacts, "canonical-v1")),
            "The S8-T3 canonical-v1 artifact is missing. Run scripts/s8-t3/prepare-old-vaults.ps1 first; this proof never fabricates the prior dataset.");
        Console.WriteLine("RUN S8-T3 owner console vault-format card against a genuine prior-format vault");

        using var fixture = S8T3Fixture.Create(fullArtifacts);
        RunningService? service = null;
        try
        {
            // A real interrupted upgrade: the engine rewrites one file and is
            // killed before the next, exactly at the boundary the card reports
            // and resumes. This is fixture setup; the resume is the owner act.
            InterruptS8T3CanonicalUpgrade(fixture.CanonicalVaultPath);
            Require(Directory.Exists(Path.Combine(fixture.CanonicalVaultPath, "rollback")),
                "The interrupted S8-T3 upgrade left no rollback point for the card to report.");

            service = await StartServiceAsync(connectionsExecutable, fixture.CanonicalSettingsPath).ConfigureAwait(true);
            var session = new OwnerConsoleSession(fixture.CanonicalControlPipe);
            await session.AttachAsync().ConfigureAwait(true);
            Require(session.ReportedVaultState == OwnerVaultState.Locked,
                "The historical S8-T3 vault did not report itself locked (" + session.ReportedVaultState + ").");
            Require(session.VaultFormat is { State: "interrupted" },
                "The service did not report the interrupted S8-T3 upgrade to the console (" +
                (session.VaultFormat?.State ?? "null") + ").");

            var repositoryRoot = SmokeFixture.FindRepositoryRoot();
            var evidenceDirectory = Path.Combine(repositoryRoot, "runtime", "s8-t3", "ui-evidence");
            Directory.CreateDirectory(evidenceDirectory);

            await DriveS8T3UiAsync(session, fixture, evidenceDirectory).ConfigureAwait(true);

            RequireNoPlaintext(fixture.CanonicalVaultPath, S8T3BodyA, "the vault the console migrated");
            RequireNoTemporaryFiles(fixture.CanonicalVaultPath);
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
    /// Leaves the copied historical vault after the first rewritten file, with
    /// its journal and its encrypted rollback point on disk. The key is zeroed
    /// as soon as the fixture no longer needs it.
    /// </summary>
    private static void InterruptS8T3CanonicalUpgrade(string vaultPath)
    {
        byte[]? key = null;
        try
        {
            var store = new EncryptedVaultStore(vaultPath);
            Require(store.TryUnlock(S8T3Passphrase, useRecoveryCode: false, out key, out var manifest) &&
                    key is not null && manifest is not null,
                "The S8-T3 console proof could not open the historical vault for the interruption fixture.");
            var engine = new VaultSchemaUpgradeEngine(store)
            {
                OnFileMigrated = index =>
                {
                    if (index == 0)
                    {
                        throw new InvalidOperationException(S8T3UiInterruption);
                    }
                }
            };
            var interrupted = false;
            try
            {
                engine.Upgrade(manifest!, key!);
            }
            catch (InvalidOperationException ex) when (string.Equals(ex.Message, S8T3UiInterruption, StringComparison.Ordinal))
            {
                interrupted = true;
            }

            Require(interrupted, "The S8-T3 console interruption point was never reached.");
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    /// <summary>
    /// Runs the whole card interaction on one STA thread that owns the real
    /// window, so every control, every click and every render is the product's
    /// own code path.
    /// </summary>
    private static async Task DriveS8T3UiAsync(
        OwnerConsoleSession session,
        S8T3Fixture fixture,
        string evidenceDirectory)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(fixture);
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
                        await RunS8T3UiActsAsync(window, session, fixture, evidenceDirectory).ConfigureAwait(true);
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
                "The S8-T3 owner console proof failed: " + failure.Message + "\n" + failure.StackTrace,
                failure);
        }
    }

    private static async Task RunS8T3UiActsAsync(
        OwnerConsoleWindow window,
        OwnerConsoleSession session,
        S8T3Fixture fixture,
        string evidenceDirectory)
    {
        var vaultPath = fixture.CanonicalVaultPath;

        // ----- the card reads the interrupted upgrade and offers both acts -----
        NavigateS8T2Ui(window, "Backup & export");
        Require(window.BackupExportPanel.Visibility == Visibility.Visible,
            "The backup and export view did not open.");
        Require(await WaitForS8T1AAsync(
                () => window.VaultFormatStateText.Text.Contains("upgrade was interrupted", StringComparison.Ordinal),
                TimeSpan.FromSeconds(15)),
            "The card did not show the interrupted upgrade the service reported (" + window.VaultFormatStateText.Text + ").");
        Require(window.VaultFormatUpgradeButton.IsEnabled,
            "The upgrade action was disabled while the vault was locked.");
        Require(window.VaultFormatRollbackButton.IsEnabled,
            "The rollback action was disabled although an interrupted upgrade left a rollback point.");
        Require(window.VaultFormatDiscardCheck.IsEnabled,
            "The discard choice was disabled although the interrupted upgrade left a rollback point.");
        Require(window.VaultFormatCredentialInput.Password.Length == 0,
            "The format card held a credential before the owner typed one.");
        SaveS8T1AWpf(window, evidenceDirectory, "s8t3-ui-01-interrupted-locked.png", window.VaultFormatSectionPanel);
        Console.WriteLine("PASS S8-T3 the card reads the interrupted upgrade, offers the upgrade and the rollback, and holds no credential");

        // ----- a missing credential never reaches the vault -----
        var missingBefore = session.VaultFormatMessage;
        ClickS8T1A(window.VaultFormatUpgradeButton);
        var missingMessage = await WaitForS8T3FormatMessageAsync(
            missingBefore,
            session,
            window,
            TimeSpan.FromSeconds(10),
            "the refused upgrade without a credential");
        Require(missingMessage.Contains("No format upgrade was attempted", StringComparison.Ordinal),
            "The card did not refuse an upgrade with no credential (" + missingMessage + ").");
        Require(session.ReportedVaultState == OwnerVaultState.Locked && session.VaultFormat is { State: "interrupted" },
            "The empty-field refusal changed the vault state or its format record.");
        Console.WriteLine("PASS S8-T3 the card refused an upgrade with no credential and sent nothing");

        // ----- a wrong credential is refused, nothing is written, the field clears -----
        var fingerprintBefore = VaultFingerprint(vaultPath);
        var headerBefore = HashFile(Path.Combine(vaultPath, "vault-header.json"));
        var wrongBefore = session.VaultFormatMessage;
        window.VaultFormatCredentialInput.Password = S8T3WrongPassphrase;
        ClickS8T1A(window.VaultFormatUpgradeButton);
        var wrongMessage = await WaitForS8T3FormatMessageAsync(
            wrongBefore,
            session,
            window,
            TimeSpan.FromSeconds(60),
            "the refused upgrade with a wrong credential");
        Require(wrongMessage.Contains("refused this credential", StringComparison.Ordinal) &&
                wrongMessage.Contains("Nothing was written", StringComparison.Ordinal),
            "The card did not report the service's refusal of the wrong credential (" + wrongMessage + ").");
        Require(await WaitForS8T1AAsync(() => window.VaultFormatCredentialInput.Password.Length == 0, TimeSpan.FromSeconds(10)),
            "The format credential stayed in the field after the refused upgrade.");
        Require(session.ReportedVaultState == OwnerVaultState.Locked,
            "The refused upgrade changed the reported vault state.");
        Require(session.VaultFormat is { State: "interrupted" },
            "The refused upgrade changed the reported format state (" + (session.VaultFormat?.State ?? "null") + ").");
        Require(string.Equals(fingerprintBefore, VaultFingerprint(vaultPath), StringComparison.Ordinal),
            "The refused upgrade changed a vault file.");
        Require(!window.VaultFormatDiscardCheck.IsChecked.GetValueOrDefault(),
            "A refused upgrade left the discard choice ticked.");
        SaveS8T1AWpf(window, evidenceDirectory, "s8t3-ui-02-wrong-credential-cleared.png", window.VaultFormatSectionPanel);
        Console.WriteLine("PASS S8-T3 a wrong credential typed into the card is refused, writes nothing, clears the field and leaves the vault locked");

        // ----- the genuine old vault is resumed through the card -----
        var resumeBefore = session.VaultFormatMessage;
        window.VaultFormatCredentialInput.Password = S8T3Passphrase;
        ClickS8T1A(window.VaultFormatUpgradeButton);
        var resumeMessage = await WaitForS8T3FormatMessageAsync(
            resumeBefore,
            session,
            window,
            TimeSpan.FromSeconds(120),
            "the resumed upgrade");
        Require(resumeMessage.Contains("raised to the format this build writes", StringComparison.Ordinal) &&
                resumeMessage.Contains("resuming an interrupted upgrade", StringComparison.Ordinal),
            "The card did not report the resumed upgrade of the genuine old vault (" + resumeMessage + ").");
        Require(await WaitForS8T1AAsync(
                () => window.VaultFormatCredentialInput.Password.Length == 0 &&
                      window.VaultFormatStateText.Text.Contains("Stored format: current", StringComparison.Ordinal) &&
                      window.VaultFormatStateText.Text.Contains("rollback point", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30)),
            "The card did not clear the credential and show the current format with its rollback point (" +
            window.VaultFormatStateText.Text + ").");
        Require(session.ReportedVaultState == OwnerVaultState.Locked,
            "The upgrade left the vault unlocked instead of locked.");
        Require(session.VaultFormat is { State: "current", RollbackAvailable: true },
            "The session did not report a current format with a rollback point.");
        Require(string.Equals(headerBefore, HashFile(Path.Combine(vaultPath, "vault-header.json")), StringComparison.Ordinal),
            "The card's upgrade changed the key envelope, which would break old backups.");
        RequireNoPlaintext(vaultPath, S8T3BodyA, "the vault the card resumed");
        SaveS8T1AWpf(window, evidenceDirectory, "s8t3-ui-03-resumed-current.png", window.VaultFormatSectionPanel);
        Console.WriteLine("PASS S8-T3 the card resumed the interrupted upgrade of the genuine old vault, kept the key envelope, cleared the credential, stayed locked and showed the current format");

        // ----- the act is not offered while the vault is unlocked -----
        NavigateS8T2Ui(window, "Vault & key");
        window.UnlockPassphraseInput.Password = S8T3Passphrase;
        ClickS8T1A(window.UnlockButton);
        Require(await WaitForS8T1AAsync(
                () => session.ReportedVaultState == OwnerVaultState.Unlocked,
                TimeSpan.FromSeconds(30)),
            "The explicit unlock through the real button did not unlock the vault.");
        NavigateS8T2Ui(window, "Backup & export");
        Require(await WaitForS8T1AAsync(
                () => !window.VaultFormatUpgradeButton.IsEnabled && !window.VaultFormatRollbackButton.IsEnabled,
                TimeSpan.FromSeconds(15)),
            "The card offered a format act while the vault was unlocked.");
        SaveS8T1AWpf(window, evidenceDirectory, "s8t3-ui-04-unlocked-card-disabled.png", window.VaultFormatSectionPanel);
        Console.WriteLine("PASS S8-T3 the card offers no format act while the vault is unlocked");

        // ----- the preserved record is read through the real Library view -----
        NavigateS8T2Ui(window, "Library");
        await OpenS8T3PreservedItemAsync(window, session).ConfigureAwait(true);
        Require(window.LibraryDetailBodyText.Text.Contains(S8T3BodyA, StringComparison.Ordinal),
            "The Library view did not show the preserved body of the migrated record (" + window.LibraryDetailBodyText.Text + ").");
        Require(window.LibraryDetailSummaryText.Text.Contains("id=" + S8T3ItemA, StringComparison.Ordinal) &&
                window.LibraryDetailSummaryText.Text.Contains("revision=1", StringComparison.Ordinal) &&
                window.LibraryDetailSummaryText.Text.Contains("author=user", StringComparison.Ordinal),
            "The Library view lost the record's identity, revision or attribution (" + window.LibraryDetailSummaryText.Text + ").");
        Require(window.LibraryDetailTitleText.Text.Contains(S8T3UiItemTitleA, StringComparison.Ordinal),
            "The Library view did not show the preserved title (" + window.LibraryDetailTitleText.Text + ").");
        SaveS8T1AWpf(window, evidenceDirectory, "s8t3-ui-05-library-preserved-after-upgrade.png", window.LibraryDetailPanel);
        Console.WriteLine("PASS S8-T3 the real Library view reads the preserved body, revision and attribution of the record the card migrated");

        // ----- lock again, then roll back through the card -----
        NavigateS8T2Ui(window, "Vault & key");
        ClickS8T1A(window.LockButton);
        Require(await WaitForS8T1AAsync(
                () => session.ReportedVaultState == OwnerVaultState.Locked,
                TimeSpan.FromSeconds(30)),
            "The real lock button did not lock the vault before the rollback.");
        NavigateS8T2Ui(window, "Backup & export");
        Require(await WaitForS8T1AAsync(
                () => window.VaultFormatUpgradeButton.IsEnabled && window.VaultFormatRollbackButton.IsEnabled,
                TimeSpan.FromSeconds(15)),
            "The card did not offer the upgrade and the rollback after the vault was locked again.");
        var rollbackBefore = session.VaultFormatMessage;
        window.VaultFormatCredentialInput.Password = S8T3Passphrase;
        ClickS8T1A(window.VaultFormatRollbackButton);
        var rollbackMessage = await WaitForS8T3FormatMessageAsync(
            rollbackBefore,
            session,
            window,
            TimeSpan.FromSeconds(120),
            "the format rollback");
        Require(rollbackMessage.Contains("Restored", StringComparison.Ordinal) &&
                rollbackMessage.Contains("file(s)", StringComparison.Ordinal),
            "The card did not report the rollback's restored files (" + rollbackMessage + ").");
        Require(await WaitForS8T1AAsync(
                () => window.VaultFormatCredentialInput.Password.Length == 0 &&
                      window.VaultFormatStateText.Text.Contains("an upgrade is available", StringComparison.Ordinal) &&
                      !window.VaultFormatRollbackButton.IsEnabled,
                TimeSpan.FromSeconds(30)),
            "The card did not clear the credential and show the older format with no rollback point (" +
            window.VaultFormatStateText.Text + ").");
        Require(session.ReportedVaultState == OwnerVaultState.Locked,
            "The rollback left the vault unlocked instead of locked.");
        Require(session.VaultFormat is { State: "upgrade_available", RollbackAvailable: false },
            "The session did not report the older format with a consumed rollback point.");
        SaveS8T1AWpf(window, evidenceDirectory, "s8t3-ui-06-rollback-upgrade-available.png", window.VaultFormatSectionPanel);
        Console.WriteLine("PASS S8-T3 the card rolled the vault back while locked, reported the restored files and the older format, and consumed the rollback point");

        // ----- the preserved record still reads after the rollback -----
        NavigateS8T2Ui(window, "Vault & key");
        window.UnlockPassphraseInput.Password = S8T3Passphrase;
        ClickS8T1A(window.UnlockButton);
        Require(await WaitForS8T1AAsync(
                () => session.ReportedVaultState == OwnerVaultState.Unlocked,
                TimeSpan.FromSeconds(30)),
            "The vault did not unlock after the rollback.");
        NavigateS8T2Ui(window, "Library");
        await OpenS8T3PreservedItemAsync(window, session).ConfigureAwait(true);
        Require(window.LibraryDetailBodyText.Text.Contains(S8T3BodyA, StringComparison.Ordinal) &&
                window.LibraryDetailSummaryText.Text.Contains("revision=1", StringComparison.Ordinal),
            "The Library view lost the preserved record after the rollback (" + window.LibraryDetailBodyText.Text + ").");
        SaveS8T1AWpf(window, evidenceDirectory, "s8t3-ui-07-library-preserved-after-rollback.png", window.LibraryDetailPanel);
        Console.WriteLine("PASS S8-T3 the rolled-back vault still serves the preserved record through the real Library view");

        // ----- upgrade again, then discard the point through the card -----
        NavigateS8T2Ui(window, "Vault & key");
        ClickS8T1A(window.LockButton);
        Require(await WaitForS8T1AAsync(
                () => session.ReportedVaultState == OwnerVaultState.Locked,
                TimeSpan.FromSeconds(30)),
            "The real lock button did not lock the vault before the second upgrade.");
        NavigateS8T2Ui(window, "Backup & export");
        var reupgradeBefore = session.VaultFormatMessage;
        window.VaultFormatCredentialInput.Password = S8T3Passphrase;
        ClickS8T1A(window.VaultFormatUpgradeButton);
        var reupgradeMessage = await WaitForS8T3FormatMessageAsync(
            reupgradeBefore,
            session,
            window,
            TimeSpan.FromSeconds(120),
            "the upgrade after the rollback");
        Require(reupgradeMessage.Contains("raised to the format this build writes", StringComparison.Ordinal),
            "The card did not upgrade the old format again (" + reupgradeMessage + ").");
        Require(await WaitForS8T1AAsync(
                () => window.VaultFormatCredentialInput.Password.Length == 0 &&
                      window.VaultFormatStateText.Text.Contains("Stored format: current", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30)),
            "The card did not clear the credential and show the current format after the second upgrade.");
        SaveS8T1AWpf(window, evidenceDirectory, "s8t3-ui-08-reupgraded-current.png", window.VaultFormatSectionPanel);

        var discardBefore = session.VaultFormatMessage;
        window.VaultFormatDiscardCheck.IsChecked = true;
        window.VaultFormatCredentialInput.Password = S8T3Passphrase;
        ClickS8T1A(window.VaultFormatRollbackButton);
        var discardMessage = await WaitForS8T3FormatMessageAsync(
            discardBefore,
            session,
            window,
            TimeSpan.FromSeconds(120),
            "the discard of the rollback point");
        Require(discardMessage.Contains("rollback point was discarded", StringComparison.Ordinal),
            "The card did not discard the rollback point (" + discardMessage + ").");
        Require(await WaitForS8T1AAsync(
                () => window.VaultFormatCredentialInput.Password.Length == 0 &&
                      !window.VaultFormatDiscardCheck.IsChecked.GetValueOrDefault() &&
                      window.VaultFormatStateText.Text.Contains("Stored format: current", StringComparison.Ordinal) &&
                      !window.VaultFormatStateText.Text.Contains("rollback point", StringComparison.Ordinal) &&
                      !window.VaultFormatRollbackButton.IsEnabled &&
                      window.VaultFormatUpgradeButton.IsEnabled,
                TimeSpan.FromSeconds(30)),
            "The card did not clear the credential and the discard choice, or still offered a consumed rollback point (" +
            window.VaultFormatStateText.Text + ").");
        Require(session.ReportedVaultState == OwnerVaultState.Locked,
            "Discarding the rollback point left the vault unlocked instead of locked.");
        Require(session.VaultFormat is { State: "current", RollbackAvailable: false },
            "The session did not report the current format with a consumed rollback point.");
        SaveS8T1AWpf(window, evidenceDirectory, "s8t3-ui-09-discard-consumed.png", window.VaultFormatSectionPanel);
        Console.WriteLine("PASS S8-T3 the card discarded the rollback point while locked, cleared the credential and the discard choice, and kept the vault current");
    }

    /// <summary>
    /// Waits for a new owner-facing answer of one format act. The answer has
    /// to differ from the previous one, so a stale message never passes for
    /// the answer of the click; a slow act reports its live state instead of
    /// failing silently.
    /// </summary>
    private static async Task<string> WaitForS8T3FormatMessageAsync(
        string? before,
        OwnerConsoleSession session,
        OwnerConsoleWindow window,
        TimeSpan timeout,
        string what)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var nextReport = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var value = session.VaultFormatMessage;
            if (value is not null && !string.Equals(value, before, StringComparison.Ordinal))
            {
                return value;
            }

            if (DateTimeOffset.UtcNow >= nextReport)
            {
                nextReport = DateTimeOffset.UtcNow.AddSeconds(10);
                Console.WriteLine(
                    "WAIT " + what + ": vault=" + (session.ReportedVaultState?.ToString() ?? "null") +
                    " format=" + (session.VaultFormat?.State ?? "null") +
                    " busy=" + session.BackupActInFlight +
                    " upgradeEnabled=" + window.VaultFormatUpgradeButton.IsEnabled +
                    " credentialLength=" + window.VaultFormatCredentialInput.Password.Length +
                    " message=" + (session.VaultFormatMessage ?? "null"));
            }

            await Task.Delay(150).ConfigureAwait(true);
        }

        throw new InvalidOperationException(
            "The " + what + " did not produce an owner-facing answer in time (vault=" +
            (session.ReportedVaultState?.ToString() ?? "null") + ", format=" +
            (session.VaultFormat?.State ?? "null") + ", busy=" + session.BackupActInFlight +
            ", upgradeEnabled=" + window.VaultFormatUpgradeButton.IsEnabled +
            ", message=" + (session.VaultFormatMessage ?? "null") + ").");
    }

    /// <summary>
    /// Reads the migrated record through the real Library view: rebuild the
    /// index from the unlocked vault, search by the synthetic title, select
    /// the result and open it with the keyboard, exactly as an owner does.
    /// </summary>
    private static async Task OpenS8T3PreservedItemAsync(OwnerConsoleWindow window, OwnerConsoleSession session)
    {
        Require(window.LibraryPanel.Visibility == Visibility.Visible,
            "The Library view was not shown, so no library read could be made on screen.");
        ClickS8T1A(window.LibraryRebuildButton);
        Require(await WaitForS8T1AAsync(
                () => !session.LibraryActInFlight && session.LibraryIndex is { RowCount: >= 2 },
                TimeSpan.FromSeconds(60)),
            "Rebuilding the library index through the real button did not materialize the historical rows (" +
            session.LibraryMessage + ").");

        var pageBefore = session.LibraryPage;
        window.LibraryQueryInput.Text = "S8T3";
        ClickS8T1A(window.LibrarySearchButton);
        Require(await WaitForS8T1AAsync(
                () => !session.LibraryActInFlight && session.LibraryPage is not null && !ReferenceEquals(session.LibraryPage, pageBefore),
                TimeSpan.FromSeconds(30)),
            "The library search did not answer a page for the historical rows (" + session.LibraryMessage + ").");
        var index = session.LibraryPage is null
            ? -1
            : Array.FindIndex(session.LibraryPage.Hits, hit => string.Equals(hit.Id, S8T3ItemA, StringComparison.Ordinal));
        Require(index >= 0,
            "The library search did not offer the migrated record (hits: " +
            string.Join(", ", session.LibraryPage?.Hits.Select(hit => hit.Id) ?? Array.Empty<string>()) + ").");

        window.LibraryResults.SelectedIndex = index;
        window.LibraryResults.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(window),
            0,
            Key.Enter) { RoutedEvent = UIElement.KeyDownEvent });
        Require(await WaitForS8T1AAsync(
                () => !session.LibraryActInFlight &&
                      session.LibraryItem is { } item &&
                      string.Equals(item.Id, S8T3ItemA, StringComparison.Ordinal),
                TimeSpan.FromSeconds(30)),
            "Opening the migrated record from the real result list did not load its detail (" + session.LibraryMessage + ").");
        Require(await WaitForS8T1AAsync(
                () => window.LibraryDetailBodyText.Text.Contains(S8T3BodyA, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10)),
            "The Library detail panel did not render the migrated body.");
        Require(window.LibraryDetailBodyText.IsVisible,
            "The Library detail body was not on screen, so the read was not an on-screen read.");
    }
}
