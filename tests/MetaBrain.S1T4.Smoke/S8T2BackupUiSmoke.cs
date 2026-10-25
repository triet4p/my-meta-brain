using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using MetaBrain.Connections;
using MetaBrain.OwnerUi;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S8-T2 runtime proof for the owner console's backup, restore and export
/// view.
/// <para>
/// The scenario drives the real owner service executable over its real owner
/// control pipe and then drives the real WPF owner window on its own STA
/// thread: the owner picks the backup file, the restore source and target,
/// the restore credential and the export scope through the real controls, and
/// the console answers with the service's own answer. Every resulting file is
/// read back from disk: the encrypted backup is inspected as ciphertext, the
/// restored vault is opened by a second service that reads the restored
/// record, and the readable export is read as plain text outside the
/// application.
/// </para>
/// <para>
/// The refusal paths are exercised on the same window: a wrong credential, a
/// missing credential, a target that already exists, an export file that
/// already exists and an empty form. Locking the vault through the real
/// button clears the chosen paths, the export selection and the credential
/// field, keeps the restore available and disables the two acts that need
/// the unlocked vault.
/// </para>
/// <para>
/// Everything the scenario creates is synthetic and stays inside the
/// repository's ignored runtime directory. It never changes a permission, an
/// owner, a registry entry or a machine setting.
/// </para>
/// </summary>
internal static partial class OwnerServiceSmoke
{
    /// <summary>
    /// The file and folder pickers of the automated run. The real Windows
    /// dialogs need an interactive desktop, so the scenario substitutes a
    /// picker that returns real paths inside its own fixture. The paths it
    /// returns are the ones the console then uses for the real request and
    /// the real file, and the default picker of the product is the real
    /// Windows dialog.
    /// </summary>
    private sealed class S8T2UiFilePicker : IOwnerFilePicker
    {
        private readonly string _directory;
        private readonly Dictionary<string, string> _chosen = new(StringComparer.Ordinal);

        public S8T2UiFilePicker(string directory)
        {
            _directory = directory;
        }

        public string? PickSaveFile(string title, string suggestedFileName)
        {
            var path = Path.Combine(_directory, suggestedFileName);
            _chosen[title] = path;
            return path;
        }

        public string? PickOpenFile(string title)
        {
            return _chosen.TryGetValue(title, out var path) ? path : null;
        }

        public string? PickFolder(string title)
        {
            var path = Path.Combine(_directory, "picked-" + title.GetHashCode(StringComparison.Ordinal).ToString("x8", CultureInfo.InvariantCulture));
            _chosen[title] = path;
            return path;
        }
    }

    internal static async Task RunS8T2BackupUiAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S8-T2 owner console smoke requires Windows named pipes, DPAPI and WPF.");
        }

        Console.WriteLine("RUN S8-T2 owner console backup, clean restore and scoped export");
        using var fixture = S8T2Fixture.Create();
        RunningService? live = null;
        RunningService? restored = null;
        RunningService? recovered = null;
        var passphraseBytes = RandomNumberGenerator.GetBytes(24);
        var passphrase = Convert.ToBase64String(passphraseBytes);
        CryptographicOperations.ZeroMemory(passphraseBytes);
        try
        {
            live = await StartServiceAsync(connectionsExecutable, fixture.LiveSettingsPath).ConfigureAwait(true);
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(true);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The synthetic S8-T2 console vault was not provisioned (" + provisioned.StandardError.Trim() + ").");
            var recoveryCode = ParseRecoveryCode(provisioned.StandardOutput);
            var unlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(true);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S8-T2 console vault did not unlock (" + unlocked.StandardError.Trim() + ").");

            await WriteS8T2UiDatasetAsync(connectionsExecutable, fixture).ConfigureAwait(true);
            Console.WriteLine("PASS S8-T2 the synthetic vault for the console proof holds two items, two sources and a relation");

            var session = new OwnerConsoleSession(fixture.ControlPipe);
            await session.AttachAsync().ConfigureAwait(true);
            Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
                "The console session did not report the real unlocked vault (" + session.ReportedVaultState + ").");

            var repositoryRoot = SmokeFixture.FindRepositoryRoot();
            var evidenceDirectory = Path.Combine(repositoryRoot, "runtime", "s8-t2", "ui-evidence");
            Directory.CreateDirectory(evidenceDirectory);

            await DriveS8T2BackupUiAsync(
                session,
                fixture,
                evidenceDirectory,
                connectionsExecutable,
                passphrase,
                recoveryCode).ConfigureAwait(true);

            // The restored vault is a real vault: a second service opens it and
            // serves the record the console restored.
            restored = await StartServiceAsync(connectionsExecutable, fixture.RestoredSettingsPath).ConfigureAwait(true);
            var restoredUnlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.RestoredControlPipe).ConfigureAwait(true);
            Require(restoredUnlock.ExitCode == 0 && restoredUnlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The vault the console restored did not unlock with the owner's passphrase (" + restoredUnlock.StandardError.Trim() + ").");
            var restoredItem = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.RestoredControlPipe,
                "--resource-id", S8T2ItemA).ConfigureAwait(true);
            Require(restoredItem.ExitCode == 0 && restoredItem.StandardOutput.Contains(S8T2ItemABody, StringComparison.Ordinal),
                "The vault the console restored lost the record (" + restoredItem.StandardError.Trim() + ").");
            Console.WriteLine("PASS S8-T2 the vault the console restored is opened by a second service and serves the restored record");

            Console.WriteLine("LIMIT the automated run substitutes the Windows file and folder dialog with a fixture picker, because the real dialog needs an interactive desktop; the default picker of the product is the real Windows dialog and the path it returns drives the same real request and the same real file.");
        }
        finally
        {
            foreach (var service in new[] { recovered, restored, live })
            {
                if (service is null)
                {
                    continue;
                }

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
    /// The synthetic dataset of the console proof: one in-scope source, one
    /// out-of-scope source, one item that cites both and one item outside the
    /// scope the owner will pick. The dataset stays small on purpose, so the
    /// service-ranked export picker really offers every record of the vault.
    /// </summary>
    private static async Task WriteS8T2UiDatasetAsync(string executable, S8T2Fixture fixture)
    {
        var sourceIn = JsonSerializer.SerializeToUtf8Bytes(new
        {
            title = S8T2SourceInTitle,
            locator = "s8t2-synthetic-in-locator",
            version = "v1",
            availability = "available",
            creationMethod = "directWrite",
            capturedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
            recordedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        var sourceInCreated = await RunOwnerCommandAsync(executable,
            "owner", "source-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", S8T2SourceIn, "--zone-id", S8T2Zone,
            "--input-file", await WriteS8T2TempAsync(fixture, sourceIn, "ui-source-in.json").ConfigureAwait(true)).ConfigureAwait(true);
        Require(sourceInCreated.ExitCode == 0 && sourceInCreated.StandardOutput.Contains("SOURCE stored revision=1", StringComparison.Ordinal),
            "The synthetic S8-T2 in-scope source was not registered (" + sourceInCreated.StandardError.Trim() + ").");

        var sourceOut = JsonSerializer.SerializeToUtf8Bytes(new
        {
            title = S8T2SourceOutTitle,
            locator = S8T2SourceOutLocator,
            version = "v1",
            availability = "available",
            creationMethod = "directWrite",
            capturedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
            recordedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        var sourceOutCreated = await RunOwnerCommandAsync(executable,
            "owner", "source-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", S8T2SourceOut, "--zone-id", S8T2Zone,
            "--input-file", await WriteS8T2TempAsync(fixture, sourceOut, "ui-source-out.json").ConfigureAwait(true)).ConfigureAwait(true);
        Require(sourceOutCreated.ExitCode == 0 && sourceOutCreated.StandardOutput.Contains("SOURCE stored revision=1", StringComparison.Ordinal),
            "The synthetic S8-T2 out-of-scope source was not registered (" + sourceOutCreated.StandardError.Trim() + ").");

        var itemA = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "lesson",
            author = "user",
            creationMethod = "directWrite",
            evidence = "unverified",
            level = "l1",
            title = "S8T2 synthetic item A",
            body = S8T2ItemABody,
            sources = Array.Empty<object>(),
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = DateTimeOffset.UtcNow,
            applicability = (object?)null,
        });
        var itemACreated = await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", S8T2ItemA, "--zone-id", S8T2Zone,
            "--input-file", await WriteS8T2TempAsync(fixture, itemA, "ui-item-a.json").ConfigureAwait(true)).ConfigureAwait(true);
        Require(itemACreated.ExitCode == 0 && itemACreated.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
            "The synthetic S8-T2 item A was not stored (" + itemACreated.StandardError.Trim() + ").");

        // The revised item cites one in-scope source and one source outside
        // the scope the owner will export: the second citation must never
        // travel.
        var revisedA = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "lesson",
            author = "user",
            creationMethod = "directWrite",
            evidence = "unverified",
            level = "l1",
            title = "S8T2 synthetic item A",
            body = S8T2ItemABody,
            sources = new[]
            {
                new { sourceId = S8T2SourceIn, sourceRevision = 1L },
                new { sourceId = S8T2SourceOut, sourceRevision = 1L },
            },
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = DateTimeOffset.UtcNow,
            applicability = (object?)null,
        });
        var revisedACreated = await RunOwnerCommandAsync(executable,
            "owner", "memory-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", S8T2ItemA, "--zone-id", S8T2Zone,
            "--expected-revision", "1",
            "--input-file", await WriteS8T2TempAsync(fixture, revisedA, "ui-item-a-revised.json").ConfigureAwait(true)).ConfigureAwait(true);
        Require(revisedACreated.ExitCode == 0 && revisedACreated.StandardOutput.Contains("MEMORY stored revision=2", StringComparison.Ordinal),
            "The synthetic S8-T2 item A did not reach its second revision (" + revisedACreated.StandardError.Trim() + ").");

        var itemB = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "lesson",
            author = "user",
            creationMethod = "directWrite",
            evidence = "unverified",
            level = "l1",
            title = "S8T2 synthetic item B",
            body = S8T2ItemBBody,
            sources = Array.Empty<object>(),
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = DateTimeOffset.UtcNow,
            applicability = (object?)null,
        });
        var itemBCreated = await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", S8T2ItemB, "--zone-id", S8T2Zone,
            "--input-file", await WriteS8T2TempAsync(fixture, itemB, "ui-item-b.json").ConfigureAwait(true)).ConfigureAwait(true);
        Require(itemBCreated.ExitCode == 0 && itemBCreated.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
            "The synthetic S8-T2 item B was not stored (" + itemBCreated.StandardError.Trim() + ").");

        var relation = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "relatedTo",
            state = "proposed",
            from = new { resourceId = S8T2ItemA, endpointKind = "memoryItem", revision = 2, span = (object?)null },
            to = new { resourceId = S8T2ItemB, endpointKind = "memoryItem", revision = 1, span = (object?)null },
            creationMethod = "directWrite",
            reason = "S8-T2 synthetic relation between two items.",
            stateReason = (string?)null,
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = DateTimeOffset.UtcNow,
        });
        var relationCreated = await RunOwnerCommandAsync(executable,
            "owner", "relation-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", S8T2Relation, "--zone-id", S8T2Zone,
            "--input-file", await WriteS8T2TempAsync(fixture, relation, "ui-relation.json").ConfigureAwait(true)).ConfigureAwait(true);
        Require(relationCreated.ExitCode == 0 && relationCreated.StandardOutput.Contains("RELATION stored revision=1", StringComparison.Ordinal),
            "The synthetic S8-T2 relation was not stored (" + relationCreated.StandardError.Trim() + ").");
    }

    /// <summary>
    /// Drives the real owner window on its own STA thread: the real controls,
    /// the real service, the real files. Every act is the window's own click
    /// handler, and every answer is read back from the session and from disk.
    /// </summary>
    private static async Task DriveS8T2BackupUiAsync(
        OwnerConsoleSession session,
        S8T2Fixture fixture,
        string evidenceDirectory,
        string connectionsExecutable,
        string passphrase,
        string recoveryCode)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(fixture);
        var picker = new S8T2UiFilePicker(fixture.OutputDirectory);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            OwnerConsoleWindow? window = null;
            try
            {
                window = new OwnerConsoleWindow(session, filePicker: picker);
                window.Width = 1280;
                window.Height = 1000;
                window.Show();
                window.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await RunS8T2BackupUiActsAsync(
                            window,
                            session,
                            fixture,
                            evidenceDirectory,
                            connectionsExecutable,
                            passphrase,
                            recoveryCode).ConfigureAwait(true);
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
                System.Windows.Threading.Dispatcher.Run();
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
                "The S8-T2 owner console proof failed: " + failure.Message + "\n" + failure.StackTrace,
                failure);
        }
    }

    private static async Task RunS8T2BackupUiActsAsync(
        OwnerConsoleWindow window,
        OwnerConsoleSession session,
        S8T2Fixture fixture,
        string evidenceDirectory,
        string connectionsExecutable,
        string passphrase,
        string recoveryCode)
    {
        // ---- the library index the export picker reads from ----
        NavigateS8T2Ui(window, "Library");
        ClickS8T1A(window.LibraryRebuildButton);
        Require(await WaitForS8T1AAsync(
                () => !session.LibraryActInFlight && session.LibraryIndex is { RowCount: >= 3 },
                TimeSpan.FromSeconds(60)),
            "Rebuilding the library index through the real button did not materialize the synthetic rows (" + session.LibraryMessage + ").");

        // ---- the backup & export view ----
        NavigateS8T2Ui(window, "Backup & export");
        Require(window.BackupExportPanel.Visibility == System.Windows.Visibility.Visible,
            "The backup and export view did not open.");
        Require(window.BackupPathInput.IsFocused,
            "Navigating to Backup & export did not focus the first actionable control for keyboard use.");
        SaveS8T1AWpf(window, evidenceDirectory, "s8t2-ui-01-backup-export.png", window.ContentScrollViewer);

        // The backup and the export need the unlocked vault; the restore does
        // not, because it reads a backup file and writes a new folder.
        Require(window.BackupRunButton.IsEnabled, "The backup action was disabled while the vault was unlocked.");
        Require(window.RestoreRunButton.IsEnabled, "The restore action was disabled while the vault was unlocked.");

        // ---- the export: pick the scope by title, write a readable copy ----
        window.ExportSearchInput.Text = "S8T2";
        ClickS8T1A(window.ExportSearchButton);
        var candidateMessage = await WaitForS8T2UiMessageAsync(
            null,
            () => session.ExportCandidateMessage,
            TimeSpan.FromSeconds(30),
            "the export candidate search");
        Require(candidateMessage.Contains("ranked", StringComparison.Ordinal),
            "The export candidate search did not answer with service-ranked records (" + candidateMessage + ").");
        var itemIndex = Array.FindIndex(session.ExportCandidates, hit => string.Equals(hit.Id, S8T2ItemA, StringComparison.Ordinal));
        Require(itemIndex >= 0,
            "The service-ranked export candidates did not offer the synthetic item (candidates: " +
            string.Join(", ", session.ExportCandidates.Select(hit => hit.Id + "/" + hit.Title + "/" + hit.Kind)) + ").");
        window.ExportCandidatesList.SelectedItems.Add(window.ExportCandidatesList.Items[itemIndex]);

        // The console asks the service to name the sources the chosen record
        // cites, so the owner can decide whether a cited source may leave the
        // vault with it.
        Require(await WaitForS8T1AAsync(
                () => session.ExportSourceCandidates.Length > 0,
                TimeSpan.FromSeconds(30)),
            "The console did not ask the service to name the cited sources of the selected record.");
        var sourceIndex = Array.FindIndex(session.ExportCandidates, hit => string.Equals(hit.Id, S8T2SourceIn, StringComparison.Ordinal));
        Require(sourceIndex >= 0,
            "The service did not name the in-scope source of the selected record (candidates: " +
            string.Join(", ", session.ExportCandidates.Select(hit => hit.Id + "/" + hit.Title + "/" + hit.Kind)) + ").");
        window.ExportCandidatesList.SelectedItems.Add(window.ExportCandidatesList.Items[sourceIndex]);
        Require(window.ExportScopeText.Text.Contains("2 record(s) selected", StringComparison.Ordinal),
            "The export scope did not show the two chosen records (" + window.ExportScopeText.Text + ").");
        Require(window.ExportScopeText.Text.Contains("S8T2 synthetic item A", StringComparison.Ordinal),
            "The export scope did not name the chosen record by its title (" + window.ExportScopeText.Text + ").");
        Require(window.ExportScopeText.Text.Contains(S8T2SourceInTitle, StringComparison.Ordinal),
            "The export scope did not name the chosen source by its title (" + window.ExportScopeText.Text + ").");
        Require(window.ExportRunButton.IsEnabled,
            "The export action stayed disabled although the owner chose a scope.");

        var exportPath = Path.Combine(fixture.OutputDirectory, "s8t2-ui-export.md");
        window.ExportPathInput.Text = exportPath;
        window.ExportIncludeRelationsCheck.IsChecked = true;
        window.ExportIncludeLifecycleCheck.IsChecked = true;
        var exportBefore = session.ExportMessage;
        ClickS8T1A(window.ExportRunButton);
        var exportMessage = await WaitForS8T2UiMessageAsync(
            exportBefore,
            () => session.ExportMessage,
            TimeSpan.FromSeconds(60),
            "the export");
        Require(exportMessage.Contains("wrote the readable copy", StringComparison.Ordinal),
            "The export did not complete through the real control (" + exportMessage + ").");
        Require(File.Exists(exportPath), "The export file was not written to the chosen path.");
        var exportText = await File.ReadAllTextAsync(exportPath).ConfigureAwait(true);
        Require(exportText.StartsWith("# Meta Brain", StringComparison.Ordinal),
            "The export file is not a readable document.");
        Require(exportText.Contains(S8T2ItemABody, StringComparison.Ordinal) &&
                exportText.Contains(S8T2SourceInTitle, StringComparison.Ordinal),
            "The export file lost the records the owner selected.");
        Require(!exportText.Contains(S8T2SourceOutTitle, StringComparison.Ordinal) &&
                !exportText.Contains(S8T2SourceOutLocator, StringComparison.Ordinal) &&
                !exportText.Contains(S8T2SourceOut, StringComparison.Ordinal) &&
                !exportText.Contains(S8T2ItemBBody, StringComparison.Ordinal),
            "The export file disclosed a record or a citation outside the selected scope.");
        Require(exportText.Contains("withheld", StringComparison.OrdinalIgnoreCase),
            "The export file did not report the citation it withheld.");
        Console.WriteLine("PASS S8-T2 the console wrote a scoped readable export with the out-of-scope citation withheld whole");
        SaveS8T1AWpf(window, evidenceDirectory, "s8t2-ui-02-export-written.png", window.ExportSectionPanel);

        // An export never overwrites a file the owner already has.
        var existingPath = Path.Combine(fixture.OutputDirectory, "s8t2-ui-existing.md");
        await File.WriteAllTextAsync(existingPath, "S8T2_SYNTHETIC_EXISTING_FILE_5b21").ConfigureAwait(true);
        window.ExportPathInput.Text = existingPath;
        var overwriteBefore = session.ExportMessage;
        ClickS8T1A(window.ExportRunButton);
        var overwriteMessage = await WaitForS8T2UiMessageAsync(
            overwriteBefore,
            () => session.ExportMessage,
            TimeSpan.FromSeconds(30),
            "the refusal of an existing export file");
        Require(overwriteMessage.Contains("already exists", StringComparison.Ordinal),
            "The console did not refuse to overwrite an existing export file (" + overwriteMessage + ").");
        Require((await File.ReadAllTextAsync(existingPath).ConfigureAwait(true)).Contains("S8T2_SYNTHETIC_EXISTING_FILE_5b21", StringComparison.Ordinal),
            "The refused export overwrote the file the owner already had.");
        Console.WriteLine("PASS S8-T2 the console refused to overwrite an existing export file and left it untouched");

        // ---- the encrypted backup ----
        var backupPath = Path.Combine(fixture.OutputDirectory, "s8t2-ui-backup.mbbackup");
        window.BackupPathInput.Text = backupPath;
        var backupBefore = session.BackupMessage;
        ClickS8T1A(window.BackupRunButton);
        var backupMessage = await WaitForS8T2UiMessageAsync(
            backupBefore,
            () => session.BackupMessage,
            TimeSpan.FromSeconds(60),
            "the encrypted backup");
        Require(backupMessage.Contains("wrote the encrypted backup", StringComparison.Ordinal),
            "The backup did not complete through the real control (" + backupMessage + ").");
        Require(File.Exists(backupPath), "The backup file was not written to the chosen path.");
        var backupBytes = await File.ReadAllBytesAsync(backupPath).ConfigureAwait(true);
        var backupText = Encoding.UTF8.GetString(backupBytes);
        Require(backupText.Contains("\"ciphertext\"", StringComparison.Ordinal),
            "The backup file the console wrote is not an encrypted container.");
        Require(!backupText.Contains(S8T2ItemABody, StringComparison.Ordinal) &&
                !backupText.Contains(S8T2SourceInTitle, StringComparison.Ordinal) &&
                !backupText.Contains(passphrase, StringComparison.Ordinal) &&
                !backupText.Contains(recoveryCode, StringComparison.Ordinal),
            "The backup file the console wrote exposed private content, the passphrase or the recovery key in plaintext.");
        Require(Directory.GetFiles(fixture.OutputDirectory, "*.tmp", SearchOption.TopDirectoryOnly).Length == 0,
            "The backup the console took left a temporary output behind.");
        Console.WriteLine("PASS S8-T2 the console wrote an encrypted backup that holds no readable record, no passphrase and no recovery key");

        // ---- the restore: wrong credential, missing credential, existing target, then the real one ----
        window.RestoreBackupPathInput.Text = backupPath;
        var wrongTarget = Path.Combine(fixture.RestoredRoot, "ui-wrong-key-vault");
        window.RestoreTargetPathInput.Text = wrongTarget;
        window.RestoreCredentialInput.Password = "S8T2_UI_WRONG_PASSPHRASE_7c4e";
        var wrongBefore = session.RestoreMessage;
        ClickS8T1A(window.RestoreRunButton);
        var wrongMessage = await WaitForS8T2UiMessageAsync(
            wrongBefore,
            () => session.RestoreMessage,
            TimeSpan.FromSeconds(60),
            "the refused restore");
        Require(wrongMessage.Contains("refused this credential", StringComparison.Ordinal),
            "The console did not refuse the wrong restore credential (" + wrongMessage + ").");
        Require(!Directory.Exists(wrongTarget), "The refused restore wrote a target directory.");
        Require(window.RestoreCredentialInput.Password.Length == 0,
            "The restore credential stayed in the field after the request.");
        Console.WriteLine("PASS S8-T2 a wrong restore credential fails closed, writes nothing and the credential field is cleared");

        // A missing credential never reaches the vault.
        var missingTarget = Path.Combine(fixture.RestoredRoot, "ui-missing-key-vault");
        window.RestoreTargetPathInput.Text = missingTarget;
        var missingBefore = session.RestoreMessage;
        ClickS8T1A(window.RestoreRunButton);
        var missingMessage = await WaitForS8T2UiMessageAsync(
            missingBefore,
            () => session.RestoreMessage,
            TimeSpan.FromSeconds(30),
            "the refused restore without a credential");
        Require(missingMessage.Contains("No restore was attempted", StringComparison.Ordinal),
            "The console did not refuse the restore without a credential (" + missingMessage + ").");
        Require(!Directory.Exists(missingTarget), "The refused restore without a credential wrote a target directory.");

        // A target that already exists is refused: the live vault is never
        // replaced by a restore. The console refuses it before it sends
        // anything, and the service refuses it too — that refusal is proven
        // here over the owner channel itself.
        window.RestoreTargetPathInput.Text = fixture.VaultDirectoryPath;
        window.RestoreCredentialInput.Password = passphrase;
        var existingTargetBefore = session.RestoreMessage;
        ClickS8T1A(window.RestoreRunButton);
        var existingTargetMessage = await WaitForS8T2UiMessageAsync(
            existingTargetBefore,
            () => session.RestoreMessage,
            TimeSpan.FromSeconds(60),
            "the refused restore into an existing folder");
        Require(existingTargetMessage.Contains("already exists", StringComparison.Ordinal),
            "The console did not refuse a restore into a folder that already exists (" + existingTargetMessage + ").");
        var serviceExistingTarget = await RunOwnerCommandWithInputAsync(connectionsExecutable, passphrase + Environment.NewLine,
            "owner", "restore", "--control-pipe", fixture.ControlPipe,
            "--backup-file", backupPath, "--target-directory", fixture.VaultDirectoryPath).ConfigureAwait(true);
        Require(serviceExistingTarget.ExitCode != 0,
            "The service accepted a restore into a folder that already exists (" + serviceExistingTarget.StandardOutput.Trim() + ").");
        var liveAfterRefusedRestore = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", S8T2ItemA).ConfigureAwait(true);
        Require(liveAfterRefusedRestore.ExitCode == 0 && liveAfterRefusedRestore.StandardOutput.Contains(S8T2ItemABody, StringComparison.Ordinal),
            "The refused restore changed the live vault (" + liveAfterRefusedRestore.StandardError.Trim() + ").");
        Console.WriteLine("PASS S8-T2 a restore into an existing folder is refused by the console and by the service, and the live vault still serves its record");

        // The real restore: the right passphrase into a clean folder.
        window.RestoreTargetPathInput.Text = fixture.RestoreTargetDirectory;
        window.RestoreCredentialInput.Password = passphrase;
        var restoreBefore = session.RestoreMessage;
        ClickS8T1A(window.RestoreRunButton);
        var restoreMessage = await WaitForS8T2UiMessageAsync(
            restoreBefore,
            () => session.RestoreMessage,
            TimeSpan.FromSeconds(120),
            "the clean restore");
        Require(restoreMessage.Contains("restored the backup", StringComparison.Ordinal),
            "The restore did not complete through the real control (" + restoreMessage + ").");
        Require(Directory.Exists(fixture.RestoreTargetDirectory), "The restore did not write the clean vault folder.");
        Require(Directory.GetFiles(fixture.RestoreTargetDirectory, "*", SearchOption.AllDirectories).Length > 0,
            "The restored vault folder holds no files.");
        Require(window.RestoreCredentialInput.Password.Length == 0,
            "The restore credential stayed in the field after a successful restore.");
        SaveS8T1AWpf(window, evidenceDirectory, "s8t2-ui-03-restored.png", window.RestoreSectionPanel);

        // The recovery key opens the same backup into another clean folder.
        var recoveryTarget = Path.Combine(fixture.RestoredRoot, "ui-recovery-vault");
        window.RestoreTargetPathInput.Text = recoveryTarget;
        window.RestoreCredentialModeInput.SelectedIndex = 1;
        window.RestoreCredentialInput.Password = recoveryCode;
        var recoveryBefore = session.RestoreMessage;
        ClickS8T1A(window.RestoreRunButton);
        var recoveryMessage = await WaitForS8T2UiMessageAsync(
            recoveryBefore,
            () => session.RestoreMessage,
            TimeSpan.FromSeconds(120),
            "the recovery key restore");
        Require(recoveryMessage.Contains("restored the backup", StringComparison.Ordinal),
            "The recovery key did not restore the backup through the real control (" + recoveryMessage + ").");
        Require(Directory.Exists(recoveryTarget), "The recovery key restore did not write the clean vault folder.");
        Console.WriteLine("PASS S8-T2 the recovery key restores the same backup into a second clean folder through the real control");

        // ---- lock: the private drafts go, the restore stays available ----
        NavigateS8T2Ui(window, "Vault & key");
        ClickS8T1A(window.LockButton);
        Require(await WaitForS8T1AAsync(
                () => session.ReportedVaultState == OwnerVaultState.Locked,
                TimeSpan.FromSeconds(30)),
            "The real lock button did not lock the vault.");
        NavigateS8T2Ui(window, "Backup & export");
        Require(await WaitForS8T1AAsync(
                () => window.ExportCandidatesList.Items.Count == 0 &&
                      window.BackupPathInput.Text.Length == 0 &&
                      window.RestoreBackupPathInput.Text.Length == 0 &&
                      window.RestoreTargetPathInput.Text.Length == 0 &&
                      window.ExportPathInput.Text.Length == 0 &&
                      window.RestoreCredentialInput.Password.Length == 0,
                TimeSpan.FromSeconds(15)),
            "Locking the vault left a chosen path, the export selection or the credential on screen.");
        Require(!window.BackupRunButton.IsEnabled,
            "The backup action stayed enabled while the vault was locked.");
        Require(!window.ExportRunButton.IsEnabled,
            "The export action stayed enabled while the vault was locked.");
        Require(window.RestoreRunButton.IsEnabled,
            "The restore action was disabled while the vault was locked, although the service serves it.");
        SaveS8T1AWpf(window, evidenceDirectory, "s8t2-ui-04-locked.png", window.ContentScrollViewer);
        Console.WriteLine("PASS S8-T2 locking through the real button clears the paths, the export selection and the credential, and keeps the restore available");

        // ---- the retry after the lock: unlock and export again ----
        NavigateS8T2Ui(window, "Vault & key");
        window.UnlockPassphraseInput.Password = passphrase;
        ClickS8T1A(window.UnlockButton);
        Require(await WaitForS8T1AAsync(
                () => session.ReportedVaultState == OwnerVaultState.Unlocked,
                TimeSpan.FromSeconds(30)),
            "The real unlock button did not unlock the vault again.");
        // The private index belongs to the unlocked epoch that built it, so
        // the owner rebuilds it after the unlock — the same explicit act the
        // Library view asks for.
        NavigateS8T2Ui(window, "Library");
        ClickS8T1A(window.LibraryRebuildButton);
        Require(await WaitForS8T1AAsync(
                () => !session.LibraryActInFlight && session.LibraryIndex is { RowCount: >= 3 },
                TimeSpan.FromSeconds(60)),
            "Rebuilding the library index after the unlock did not materialize the synthetic rows (" + session.LibraryMessage + ").");
        NavigateS8T2Ui(window, "Backup & export");
        window.ExportSearchInput.Text = "S8T2";
        var retrySearchBefore = session.ExportCandidateMessage;
        ClickS8T1A(window.ExportSearchButton);
        var retrySearchMessage = await WaitForS8T2UiMessageAsync(
            retrySearchBefore,
            () => session.ExportCandidateMessage,
            TimeSpan.FromSeconds(30),
            "the export candidate search after the unlock");
        Require(retrySearchMessage.Contains("ranked", StringComparison.Ordinal),
            "The export candidate search after the unlock did not answer with service-ranked records (" + retrySearchMessage + ").");
        var retryIndex = Array.FindIndex(session.ExportCandidates, hit => string.Equals(hit.Id, S8T2ItemA, StringComparison.Ordinal));
        Require(retryIndex >= 0,
            "The export picker offered no record after the unlock (candidates: " +
            string.Join(", ", session.ExportCandidates.Select(hit => hit.Id + "/" + hit.Title)) + ").");
        window.ExportCandidatesList.SelectedItems.Add(window.ExportCandidatesList.Items[retryIndex]);
        // The console asks the service to name the cited sources of the
        // chosen record; the export waits for that answer to finish.
        Require(await WaitForS8T1AAsync(
                () => !session.BackupActInFlight && session.ExportSourceCandidates.Length > 0,
                TimeSpan.FromSeconds(30)),
            "The console did not finish naming the cited sources after the unlock.");
        var retryPath = Path.Combine(fixture.OutputDirectory, "s8t2-ui-export-retry.md");
        window.ExportPathInput.Text = retryPath;
        var retryBefore = session.ExportMessage;
        ClickS8T1A(window.ExportRunButton);
        var retryMessage = await WaitForS8T2UiMessageAsync(
            retryBefore,
            () => session.ExportMessage,
            TimeSpan.FromSeconds(60),
            "the export after the unlock");
        Require(retryMessage.Contains("wrote the readable copy", StringComparison.Ordinal),
            "The export after the unlock did not complete (" + retryMessage + ").");
        Require(File.Exists(retryPath), "The export after the unlock wrote no file.");
        var retryText = await File.ReadAllTextAsync(retryPath).ConfigureAwait(true);
        Require(retryText.Contains(S8T2ItemABody, StringComparison.Ordinal),
            "The export after the unlock lost the selected record.");
        Console.WriteLine("PASS S8-T2 the export works again after an explicit unlock and a fresh scope choice");
    }

    private static void NavigateS8T2Ui(OwnerConsoleWindow window, string entry)
    {
        ArgumentNullException.ThrowIfNull(window);
        foreach (var item in window.Navigation.Items.OfType<ListBoxItem>())
        {
            if (string.Equals(item.Content as string, entry, StringComparison.Ordinal))
            {
                window.Navigation.SelectedItem = item;
                return;
            }
        }

        throw new InvalidOperationException("The WPF console has no navigation entry named " + entry + ".");
    }

    /// <summary>
    /// Waits for a new owner-facing answer of one act. The answer has to
    /// differ from the one the act had before, so a stale message can never
    /// pass for the answer of the click.
    /// </summary>
    private static async Task<string> WaitForS8T2UiMessageAsync(
        string? before,
        Func<string?> current,
        TimeSpan timeout,
        string what)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var value = current();
            if (value is not null && !string.Equals(value, before, StringComparison.Ordinal))
            {
                return value;
            }

            await Task.Delay(150).ConfigureAwait(true);
        }

        throw new InvalidOperationException("The " + what + " did not produce an owner-facing answer in time.");
    }
}
