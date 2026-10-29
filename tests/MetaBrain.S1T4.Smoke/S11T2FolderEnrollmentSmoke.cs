using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using MetaBrain.Application;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;
using MetaBrain.Connections;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    internal static async Task RunS11T2FolderEnrollmentAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S11-T2 folder enrollment smoke requires Windows paths and named pipes.");
        }

        Console.WriteLine("RUN S11-T2 folder enrollment through the real owner CLI and service");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        try
        {
            await VerifyFolderEnrollmentRecoveryAsync(
                Path.Combine(fixture.OutputDirectory, "recovery-fixtures")).ConfigureAwait(false);
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provision = await RunOwnerCommandWithInputAsync(
                connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The S11-T2 synthetic vault did not provision (exit=" + provision.ExitCode + "; stderr=" + provision.StandardError.Trim() + ").");
            var agentPreview = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "source.folder.preview"
            }).ConfigureAwait(false);
            var agentEnroll = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "source.folder.enroll"
            }).ConfigureAwait(false);
            Require(ReplyCode(agentPreview) == "forbidden" && ReplyCode(agentEnroll) == "forbidden",
                "The new owner folder operations were exposed on the agent pipe.");

            var repositoryRoot = SmokeFixture.FindRepositoryRoot();
            var corpusRoot = Path.Combine(repositoryRoot, "artifacts", "sprint-11", "corpus", "folders", "tree");
            var junction = Path.Combine(corpusRoot, "linked-outside");
            Require(Directory.Exists(corpusRoot) &&
                    (File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0,
                "The finalized T1 corpus junction is unavailable; the reparse-point negative cannot be exercised.");

            var corpusFiles = new[]
            {
                Path.Combine(corpusRoot, "notes", "codex-a.jsonl"),
                Path.Combine(corpusRoot, "notes", "summary.md"),
                Path.Combine(corpusRoot, "notes", "ignore-me.tmp"),
                Path.Combine(corpusRoot, "notes", "draft", "omp-partial.jsonl")
            };
            var firstCorpusSourceId = FolderMemberSourceId(corpusRoot, corpusFiles[0]);
            var corpusHashes = corpusFiles.ToDictionary(path => path, ComputeFileHash, StringComparer.OrdinalIgnoreCase);

            // Hold one source with an exclusive share mode. A preview may stat
            // and enumerate it, but must not attempt to read its contents.
            CommandResult cancelledPreview;
            using (var held = new FileStream(corpusFiles[0], FileMode.Open, FileAccess.Read, FileShare.None))
            {
                _ = held;
                cancelledPreview = await RunOwnerCommandWithInputAsync(
                    connectionsExecutable,
                    "CANCEL" + Environment.NewLine,
                    FolderCommand(fixture.ControlPipe, corpusRoot, "**", "none", recursive: true)).ConfigureAwait(false);
            }

            Require(cancelledPreview.ExitCode == 0 &&
                    cancelledPreview.StandardOutput.Contains("FOLDER PREVIEW entries=5 included=4", StringComparison.Ordinal) &&
                    cancelledPreview.StandardOutput.Contains("reason=excluded_reparse_point", StringComparison.Ordinal) &&
                    !cancelledPreview.StandardOutput.Contains("outside-only.jsonl", StringComparison.Ordinal) &&
                    cancelledPreview.StandardOutput.Contains("CANCELLED; folder membership unchanged", StringComparison.Ordinal),
                "The preview did not enumerate four regular files, exclude the junction target, or honor cancellation (stdout=" + cancelledPreview.StandardOutput.Trim() + "; stderr=" + cancelledPreview.StandardError.Trim() + ").");
            var emptySources = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(emptySources.ExitCode == 0 && emptySources.StandardOutput.Contains("SOURCES count=0", StringComparison.Ordinal),
                "Cancelling after folder preview changed the source allowlist.");
            var disconnectedPreview = await RunOwnerCommandWithInputAsync(
                connectionsExecutable,
                string.Empty,
                FolderCommand(fixture.ControlPipe, corpusRoot, "**", "none", recursive: true)).ConfigureAwait(false);
            Require(disconnectedPreview.ExitCode == 0 &&
                    disconnectedPreview.StandardOutput.Contains("FOLDER PREVIEW entries=5 included=4", StringComparison.Ordinal) &&
                    disconnectedPreview.StandardOutput.Contains("CANCELLED; folder membership unchanged", StringComparison.Ordinal),
                "EOF at the confirmation prompt did not cancel folder enrollment (stdout=" + disconnectedPreview.StandardOutput.Trim() + "; stderr=" + disconnectedPreview.StandardError.Trim() + ").");
            emptySources = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(emptySources.ExitCode == 0 && emptySources.StandardOutput.Contains("SOURCES count=0", StringComparison.Ordinal),
                "Disconnecting before confirmation changed the source allowlist.");
            var absentPreviewRegistrySource = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", firstCorpusSourceId).ConfigureAwait(false);
            Require(absentPreviewRegistrySource.ExitCode != 0 &&
                    absentPreviewRegistrySource.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "Cancelling before confirmation left a canonical source registration.");
            Console.WriteLine("PASS EOF before confirmation cancels enrollment without changing source membership");
            Console.WriteLine("PASS preview enumerates the synthetic four-file tree without reading contents; junction and outside target stay excluded; cancellation writes nothing");

            var reparseTarget = Path.Combine(fixture.OutputDirectory, "reparse-target");
            var reparseTargetChild = Path.Combine(reparseTarget, "selected-child");
            var reparseParent = Path.Combine(fixture.OutputDirectory, "reparse-parent");
            Directory.CreateDirectory(reparseTargetChild);
            await File.WriteAllTextAsync(
                Path.Combine(reparseTargetChild, "should-not-appear.jsonl"),
                "{\"synthetic\":true}\n",
                new UTF8Encoding(false)).ConfigureAwait(false);
            await CreateJunctionAsync(reparseParent, reparseTarget).ConfigureAwait(false);
            try
            {
                var selectedBelowJunction = await RunOwnerCommandAsync(
                    connectionsExecutable,
                    FolderCommand(fixture.ControlPipe, Path.Combine(reparseParent, "selected-child"),
                        "**/*.jsonl", "none", recursive: true)).ConfigureAwait(false);
                Require(selectedBelowJunction.ExitCode != 0 &&
                        selectedBelowJunction.StandardError.Contains("source_folder_reparse_point", StringComparison.Ordinal) &&
                        !selectedBelowJunction.StandardOutput.Contains("should-not-appear.jsonl", StringComparison.Ordinal),
                    "A selected normal directory below a junction ancestor was enumerated instead of refused (stdout=" +
                    selectedBelowJunction.StandardOutput.Trim() + "; stderr=" + selectedBelowJunction.StandardError.Trim() + ").");
                emptySources = await RunOwnerCommandAsync(
                    connectionsExecutable, "owner", "source-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
                Require(emptySources.ExitCode == 0 && emptySources.StandardOutput.Contains("SOURCES count=0", StringComparison.Ordinal),
                    "A reparse-ancestor refusal changed source membership.");
                Console.WriteLine("PASS a normal selected subfolder below a synthetic junction is refused without admitting its target");
            }
            finally
            {
                RemoveSyntheticJunction(reparseParent);
            }

            var missingRoot = Path.Combine(fixture.OutputDirectory, "missing-folder");
            var missing = await RunOwnerCommandAsync(
                connectionsExecutable,
                FolderCommand(fixture.ControlPipe, missingRoot, "**", "none", recursive: true)).ConfigureAwait(false);
            Require(missing.ExitCode != 0 && missing.StandardError.Contains("source_folder_missing", StringComparison.Ordinal),
                "A missing folder did not fail with a specific refusal (stderr=" + missing.StandardError.Trim() + ").");

            // A refusal on one file must not commit anchors for earlier files.
            CommandResult lockedFile;
            using (var held = new FileStream(corpusFiles[0], FileMode.Open, FileAccess.Read, FileShare.None))
            {
                _ = held;
                lockedFile = await RunOwnerCommandWithInputAsync(
                    connectionsExecutable,
                    "ENROLL" + Environment.NewLine,
                    FolderCommand(fixture.ControlPipe, corpusRoot, "**", "none", recursive: true)).ConfigureAwait(false);
            }

            Require(lockedFile.ExitCode != 0 && lockedFile.StandardError.Contains("source_locator_unreadable", StringComparison.Ordinal),
                "A writer-locked source was not refused at the existing enrolled-source reader boundary (stderr=" + lockedFile.StandardError.Trim() + ").");
            emptySources = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(emptySources.ExitCode == 0 && emptySources.StandardOutput.Contains("SOURCES count=0", StringComparison.Ordinal),
                "A failed folder batch left partial source membership behind.");
            var absentRegistrySource = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", firstCorpusSourceId).ConfigureAwait(false);
            Require(absentRegistrySource.ExitCode != 0 &&
                    absentRegistrySource.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A file-lock refusal left a partial canonical source registration.");

            var enrolledCorpus = await RunOwnerCommandWithInputAsync(
                connectionsExecutable,
                "ENROLL" + Environment.NewLine,
                FolderCommand(fixture.ControlPipe, corpusRoot, "**", "none", recursive: true)).ConfigureAwait(false);
            Require(enrolledCorpus.ExitCode == 0 && enrolledCorpus.StandardOutput.Contains("members=4", StringComparison.Ordinal) &&
                    enrolledCorpus.StandardOutput.Contains("SOURCE FOLDER enrolled", StringComparison.Ordinal),
                "One confirmation did not enroll the four previewed corpus members (stdout=" + enrolledCorpus.StandardOutput.Trim() + "; stderr=" + enrolledCorpus.StandardError.Trim() + ").");
            foreach (var sourcePath in corpusFiles)
            {
                await RequireRegisteredFolderMemberAsync(
                    connectionsExecutable, fixture.ControlPipe, FolderMemberSourceId(corpusRoot, sourcePath),
                    "s11t2-synthetic-zone", sourcePath).ConfigureAwait(false);
            }
            Console.WriteLine("PASS confirmed folder members are canonical encrypted source-registry records");
            var fourSources = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(fourSources.ExitCode == 0 && fourSources.StandardOutput.Contains("SOURCES count=4", StringComparison.Ordinal),
                "Folder enrollment did not reuse the existing source allowlist for all four members.");

            // An exact duplicate is idempotent even if a source is currently
            // locked against reading: membership metadata, not file contents,
            // determines that the folder was already enrolled.
            CommandResult duplicateCorpus;
            using (var held = new FileStream(corpusFiles[0], FileMode.Open, FileAccess.Read, FileShare.None))
            {
                _ = held;
                duplicateCorpus = await RunOwnerCommandWithInputAsync(
                    connectionsExecutable,
                    "ENROLL" + Environment.NewLine,
                    FolderCommand(fixture.ControlPipe, corpusRoot, "**", "none", recursive: true)).ConfigureAwait(false);
            }

            Require(duplicateCorpus.ExitCode == 0 &&
                    duplicateCorpus.StandardOutput.Contains("SOURCE FOLDER already_enrolled", StringComparison.Ordinal) &&
                    duplicateCorpus.StandardOutput.Contains("members=4", StringComparison.Ordinal),
                "Duplicate enrollment changed membership or did not return an explicit idempotent result.");
            await RequireRegisteredFolderMemberAsync(
                connectionsExecutable, fixture.ControlPipe, firstCorpusSourceId,
                "s11t2-synthetic-zone", corpusFiles[0]).ConfigureAwait(false);

            // A second selected folder demonstrates explicit include/exclude
            // rules and that files added after confirmation are not adopted by
            // a duplicate enrollment.
            var selectedRoot = Path.Combine(fixture.OutputDirectory, "selected-folder");
            var nested = Path.Combine(selectedRoot, "nested");
            Directory.CreateDirectory(nested);
            var alpha = Path.Combine(selectedRoot, "alpha.jsonl");
            var beta = Path.Combine(nested, "beta.jsonl");
            var skipped = Path.Combine(selectedRoot, "skip-this.jsonl");
            await File.WriteAllTextAsync(alpha, "{\"type\":\"message\",\"id\":\"synthetic-alpha\"}\n", new UTF8Encoding(false)).ConfigureAwait(false);
            await File.WriteAllTextAsync(beta, "{\"type\":\"message\",\"id\":\"synthetic-beta\"}\n", new UTF8Encoding(false)).ConfigureAwait(false);
            await File.WriteAllTextAsync(skipped, "{\"type\":\"message\",\"id\":\"synthetic-skipped\"}\n", new UTF8Encoding(false)).ConfigureAwait(false);
            var localHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [alpha] = ComputeFileHash(alpha),
                [beta] = ComputeFileHash(beta),
                [skipped] = ComputeFileHash(skipped)
            };

            var localArgs = FolderCommand(fixture.ControlPipe, selectedRoot, "**/*.jsonl", "**/skip*.jsonl", recursive: true);
            var enrolledLocal = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, "ENROLL" + Environment.NewLine, localArgs).ConfigureAwait(false);
            Require(enrolledLocal.ExitCode == 0 && enrolledLocal.StandardOutput.Contains("members=2", StringComparison.Ordinal) &&
                    enrolledLocal.StandardOutput.Contains("excluded_by_pattern", StringComparison.Ordinal),
                "Explicit include/exclude selection did not enroll only two files (stdout=" + enrolledLocal.StandardOutput.Trim() + "; stderr=" + enrolledLocal.StandardError.Trim() + ").");
            foreach (var sourcePath in new[] { alpha, beta })
            {
                await RequireRegisteredFolderMemberAsync(
                    connectionsExecutable, fixture.ControlPipe, FolderMemberSourceId(selectedRoot, sourcePath),
                    "s11t2-synthetic-zone", sourcePath).ConfigureAwait(false);
            }
            var skippedSource = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", FolderMemberSourceId(selectedRoot, skipped)).ConfigureAwait(false);
            Require(skippedSource.ExitCode != 0 &&
                    skippedSource.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "An excluded file was added to the canonical source registry.");

            var encryptedEnrollmentPath = Path.Combine(fixture.VaultDirectoryPath, "source-enrollment.enc");
            var encryptedEnrollmentText = File.ReadAllText(encryptedEnrollmentPath, Encoding.UTF8);
            Require(!encryptedEnrollmentText.Contains(corpusRoot, StringComparison.OrdinalIgnoreCase) &&
                    !encryptedEnrollmentText.Contains(selectedRoot, StringComparison.OrdinalIgnoreCase) &&
                    !encryptedEnrollmentText.Contains("codex-a.jsonl", StringComparison.OrdinalIgnoreCase) &&
                    !encryptedEnrollmentText.Contains("alpha.jsonl", StringComparison.OrdinalIgnoreCase),
                "The source-folder membership or member locators were persisted outside the encrypted enrollment state.");

            var addedAfterConfirmation = Path.Combine(nested, "new.jsonl");
            await File.WriteAllTextAsync(addedAfterConfirmation, "{\"type\":\"message\",\"id\":\"synthetic-new\"}\n", new UTF8Encoding(false)).ConfigureAwait(false);
            var unapprovedMember = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, "ENROLL" + Environment.NewLine, localArgs).ConfigureAwait(false);
            Require(unapprovedMember.ExitCode != 0 &&
                    unapprovedMember.StandardError.Contains("source_folder_membership_changed", StringComparison.Ordinal),
                "A newly discovered file was silently added to previously confirmed folder membership.");
            var unapprovedSourceId = FolderMemberSourceId(selectedRoot, addedAfterConfirmation);
            var unregisteredNewMember = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", unapprovedSourceId).ConfigureAwait(false);
            Require(unregisteredNewMember.ExitCode != 0 &&
                    unregisteredNewMember.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "Rejecting changed membership still created a canonical source-registry record for the new file.");
            File.Delete(addedAfterConfirmation);

            var sixSources = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(sixSources.ExitCode == 0 && sixSources.StandardOutput.Contains("SOURCES count=6", StringComparison.Ordinal),
                "A rejected new member changed the source allowlist.");

            var lockVault = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockVault.ExitCode == 0, "The S11-T2 vault could not be locked.");
            var lockedVaultOperation = await RunOwnerCommandWithInputAsync(
                connectionsExecutable,
                "ENROLL" + Environment.NewLine,
                localArgs).ConfigureAwait(false);
            Require(lockedVaultOperation.ExitCode != 0 && lockedVaultOperation.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "Folder preview/enrollment was not refused while the vault was locked.");
            var unlock = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0, "The S11-T2 vault did not unlock after the lock refusal.");

            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var restartedStatus = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(restartedStatus.ExitCode == 0 && restartedStatus.StandardOutput.Contains("vault=locked", StringComparison.Ordinal),
                "The restarted service did not start locked.");
            unlock = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0, "The persisted folder membership could not be unlocked after service restart.");

            var afterRestart = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(afterRestart.ExitCode == 0 && afterRestart.StandardOutput.Contains("SOURCES count=6", StringComparison.Ordinal),
                "The encrypted folder memberships or their source entries did not survive service restart.");
            await RequireRegisteredFolderMemberAsync(
                connectionsExecutable, fixture.ControlPipe, FolderMemberSourceId(selectedRoot, alpha),
                "s11t2-synthetic-zone", alpha).ConfigureAwait(false);
            var duplicateAfterRestart = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, "ENROLL" + Environment.NewLine, localArgs).ConfigureAwait(false);
            Require(duplicateAfterRestart.ExitCode == 0 &&
                    duplicateAfterRestart.StandardOutput.Contains("SOURCE FOLDER already_enrolled", StringComparison.Ordinal),
                "The encrypted folder membership index was not available for duplicate detection after restart.");

            foreach (var (path, expectedHash) in corpusHashes.Concat(localHashes))
            {
                Require(string.Equals(ComputeFileHash(path), expectedHash, StringComparison.Ordinal),
                    "Folder enrollment modified an original synthetic source: " + Path.GetFileName(path));
            }

            Console.WriteLine("PASS multiple-file enrollment, idempotent duplicate, include/exclude, no unapproved new member, missing folder, source/vault lock refusals, cancellation, reparse exclusion, restart persistence, and source-byte preservation");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static string[] FolderCommand(
        string controlPipe,
        string root,
        string includes,
        string excludes,
        bool recursive) =>
    [
        "owner", "source-folder-enroll", "--control-pipe", controlPipe,
        "--folder", root, "--zone-id", "s11t2-synthetic-zone",
        "--format", "codex.session.jsonl", "--format-version", "1",
        "--recursive", recursive ? "true" : "false",
        "--include-globs", includes, "--exclude-globs", excludes
    ];

    private static string FolderMemberSourceId(string root, string locator)
    {
        var normalizedRoot = SourceFolderEnrollmentGrammar.NormalizeRoot(root);
        return SourceFolderEnrollmentGrammar.MemberSourceIdFor(
            normalizedRoot, Path.GetRelativePath(normalizedRoot, locator));
    }

    private static async Task RequireRegisteredFolderMemberAsync(
        string executable,
        string controlPipe,
        string sourceId,
        string zoneId,
        string locator)
    {
        var registered = await RunOwnerCommandAsync(executable,
            "owner", "source-read", "--control-pipe", controlPipe,
            "--resource-id", sourceId).ConfigureAwait(false);
        Require(registered.ExitCode == 0 &&
                registered.StandardOutput.Contains($"SOURCE id={sourceId}", StringComparison.Ordinal) &&
                registered.StandardOutput.Contains($"zone={zoneId}", StringComparison.Ordinal) &&
                registered.StandardOutput.Contains($"locator={locator}", StringComparison.Ordinal) &&
                registered.StandardOutput.Contains("availability=available", StringComparison.Ordinal) &&
                registered.StandardOutput.Contains("revision=1", StringComparison.Ordinal) &&
                !registered.StandardOutput.Contains("\"type\":\"message\"", StringComparison.Ordinal),
            "The confirmed folder member is missing or incorrect in the canonical source registry (stdout=" +
            registered.StandardOutput.Trim() + "; stderr=" + registered.StandardError.Trim() + ").");
    }


    private static async Task CreateJunctionAsync(string junctionPath, string targetPath)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(junctionPath);
        start.ArgumentList.Add(targetPath);
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("The synthetic junction command did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        _ = await stdout.ConfigureAwait(false);
        _ = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0 || !Directory.Exists(junctionPath) ||
            (File.GetAttributes(junctionPath) & FileAttributes.ReparsePoint) == 0)
        {
            throw new InvalidOperationException("The synthetic ancestor junction could not be created (exit=" + process.ExitCode + ").");
        }
    }

    private static void RemoveSyntheticJunction(string junctionPath)
    {
        if ((Directory.Exists(junctionPath) || File.Exists(junctionPath)) &&
            (File.GetAttributes(junctionPath) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(junctionPath);
        }
    }

    private static async Task VerifyFolderEnrollmentRecoveryAsync(string root)
    {
        Directory.CreateDirectory(root);
        await VerifyPersistentManifestDeleteFailureAsync(Path.Combine(root, "manifest-delete-failure")).ConfigureAwait(false);
        await VerifyProcessInterruptionRecoveryAsync(Path.Combine(root, "process-interruption")).ConfigureAwait(false);
        Console.WriteLine("PASS durable recovery across real manifest-delete failure and process termination; incomplete canonical members are removed before operations resume");
    }

    private static async Task VerifyPersistentManifestDeleteFailureAsync(string root)
    {
        var vaultDirectory = Path.Combine(root, "vault");
        var sourceRoot = Path.Combine(root, "sources");
        await CreateSyntheticRecoverySourcesAsync(sourceRoot).ConfigureAwait(false);
        var passphrase = NewSyntheticPassphrase();
        using var lifecycle = new VaultLifecycle(new EncryptedVaultStore(vaultDirectory));
        Require(lifecycle.Provision(passphrase, out _), "The manifest-failure recovery vault did not provision.");

        var operation = lifecycle.TryBeginOperation() ??
            throw new InvalidOperationException("The manifest-failure recovery vault did not grant an operation.");
        using var cancellation = new CancellationTokenSource();
        FileStream? manifestLock = null;
        var wrappedOperation = S11T2RealStorageOperation.Wrap(operation, () =>
        {
            manifestLock = new FileStream(
                Path.Combine(vaultDirectory, "manifest.enc"),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            cancellation.Cancel();
        });

        try
        {
            var coordinator = CreateRecoveryCoordinator();
            var preview = coordinator.PreviewFolder(
                AuthenticatedContext.ForOwner(),
                RecoveryFolderSelection(sourceRoot),
                wrappedOperation,
                CancellationToken.None);
            SourceOperationException? recoveryFailure = null;
            try
            {
                _ = coordinator.EnrollFolder(
                    AuthenticatedContext.ForOwner(),
                    preview.PreviewId,
                    "ENROLL",
                    wrappedOperation,
                    cancellation.Token);
            }
            catch (SourceOperationException ex) when (ex.Code == "source_folder_recovery_pending")
            {
                recoveryFailure = ex;
            }

            var successfulCanonicalWrites =
                ((S11T2RealStorageOperation)(object)wrappedOperation).SuccessfulCanonicalWrites;
            Require((recoveryFailure?.InnerException is IOException or UnauthorizedAccessException) &&
                    successfulCanonicalWrites == 1,
                "The real encrypted manifest did not reject compensation after a committed canonical source write (code=" +
                (recoveryFailure?.Code ?? "none") + "; inner=" +
                (recoveryFailure?.InnerException?.GetType().Name ?? "none") +
                "; successfulCreates=" + successfulCanonicalWrites + ").");
            var deniedOperation = lifecycle.TryBeginOperation();
            if (deniedOperation is not null)
            {
                deniedOperation.Dispose();
                throw new InvalidOperationException("The vault accepted a new operation while durable folder recovery was pending.");
            }
            Console.WriteLine("PASS production storage rejected manifest compensation after one committed canonical create; durable recovery remained required");
        }
        finally
        {
            manifestLock?.Dispose();
            operation.Dispose();
        }

        AssertPendingRecoveryState(vaultDirectory, passphrase, expectedCanonicalRecords: 1);
        await lifecycle.LockAsync().ConfigureAwait(false);
        using var recoveredLifecycle = new VaultLifecycle(new EncryptedVaultStore(vaultDirectory));
        Require(recoveredLifecycle.Unlock(passphrase, useRecoveryCode: false),
            "The vault did not unlock after the persistent manifest failure was removed.");
        AssertNoIncompleteFolderBatch(recoveredLifecycle, "manifest-delete failure");
    }

    private static async Task VerifyProcessInterruptionRecoveryAsync(string root)
    {
        var vaultDirectory = Path.Combine(root, "vault");
        var sourceRoot = Path.Combine(root, "sources");
        var signalPath = Path.Combine(root, "first-canonical-create.signal");
        await CreateSyntheticRecoverySourcesAsync(sourceRoot).ConfigureAwait(false);
        var passphrase = NewSyntheticPassphrase();
        using (var provisioned = new VaultLifecycle(new EncryptedVaultStore(vaultDirectory)))
        {
            Require(provisioned.Provision(passphrase, out _), "The process-interruption recovery vault did not provision.");
        }

        await KillRecoveryWorkerAfterFirstCreateAsync(vaultDirectory, passphrase, sourceRoot, signalPath).ConfigureAwait(false);
        AssertPendingRecoveryState(vaultDirectory, passphrase, expectedCanonicalRecords: 1);
        using var recoveredLifecycle = new VaultLifecycle(new EncryptedVaultStore(vaultDirectory));
        Require(recoveredLifecycle.Unlock(passphrase, useRecoveryCode: false),
            "The interrupted enrollment vault did not recover during process restart.");
        AssertNoIncompleteFolderBatch(recoveredLifecycle, "process interruption");
        Console.WriteLine("PASS process was killed after a durable canonical create and restart recovered the pending encrypted batch before serving operations");
    }

    internal static Task RunS11T2RecoveryCrashWorkerAsync(
        string vaultDirectory,
        string passphrase,
        string sourceRoot,
        string signalPath)
    {
        using var lifecycle = new VaultLifecycle(new EncryptedVaultStore(vaultDirectory));
        Require(lifecycle.Unlock(passphrase, useRecoveryCode: false),
            "The process-interruption child could not unlock its synthetic vault.");
        using var operation = lifecycle.TryBeginOperation() ??
            throw new InvalidOperationException("The process-interruption child did not acquire a vault operation.");
        var coordinator = CreateRecoveryCoordinator();
        var preview = coordinator.PreviewFolder(
            AuthenticatedContext.ForOwner(),
            RecoveryFolderSelection(sourceRoot),
            operation,
            CancellationToken.None);
        var wrappedOperation = S11T2RealStorageOperation.Wrap(operation, () =>
        {
            var temporaryPath = signalPath + ".tmp";
            using (var marker = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       1,
                       FileOptions.WriteThrough))
            {
                marker.WriteByte(1);
                marker.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, signalPath);
            Console.WriteLine("S11-T2 process-interruption fixture paused after its first committed canonical create");
            new ManualResetEventSlim(initialState: false).Wait();
        });
        _ = coordinator.EnrollFolder(
            AuthenticatedContext.ForOwner(),
            preview.PreviewId,
            "ENROLL",
            wrappedOperation,
            CancellationToken.None);
        throw new InvalidOperationException("The process-interruption fixture unexpectedly completed enrollment.");
    }

    private static async Task KillRecoveryWorkerAfterFirstCreateAsync(
        string vaultDirectory,
        string passphrase,
        string sourceRoot,
        string signalPath)
    {
        var executable = Environment.ProcessPath ??
            throw new InvalidOperationException("The smoke executable path is unavailable for the process-interruption fixture.");
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(signalPath)!, Path.GetFileName(signalPath))
        {
            NotifyFilter = NotifyFilters.FileName
        };
        watcher.Created += (_, _) => signal.TrySetResult(true);
        watcher.Renamed += (_, _) => signal.TrySetResult(true);
        watcher.EnableRaisingEvents = true;

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("s11-t2-recovery-crash-worker");
        start.ArgumentList.Add(vaultDirectory);
        start.ArgumentList.Add(passphrase);
        start.ArgumentList.Add(sourceRoot);
        start.ArgumentList.Add(signalPath);
        SetDotnetProcessSafety(start);

        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("The process-interruption worker did not start.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            var exited = process.WaitForExitAsync();
            var winner = await Task.WhenAny(signal.Task, exited).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (winner != signal.Task)
            {
                throw new InvalidOperationException(
                    "The process-interruption worker exited before its first canonical create (stderr bytes=" +
                    (await standardError.ConfigureAwait(false)).Length + ").");
            }

            await signal.Task.ConfigureAwait(false);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            _ = await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        }
    }

    private static void SetDotnetProcessSafety(ProcessStartInfo start)
    {
        var repositoryRoot = SmokeFixture.FindRepositoryRoot();
        var dotnetHome = Path.Combine(repositoryRoot, "runtime", "dotnet-home");
        var temporary = Path.Combine(repositoryRoot, "artifacts", "sprint-11", ".temp");
        start.Environment["DOTNET_CLI_HOME"] = Path.Combine(repositoryRoot, "artifacts", "sprint-11", ".dotnet-cli");
        start.Environment["NUGET_PACKAGES"] = Path.Combine(dotnetHome, ".nuget", "packages");
        start.Environment["HOME"] = dotnetHome;
        start.Environment["TEMP"] = temporary;
        start.Environment["TMP"] = temporary;
        start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        start.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1";
    }

    private static void AssertPendingRecoveryState(
        string vaultDirectory,
        string passphrase,
        int expectedCanonicalRecords)
    {
        var store = new EncryptedVaultStore(vaultDirectory);
        if (!store.TryUnlock(passphrase, useRecoveryCode: false, out var dataKey, out var manifest) ||
            dataKey is null || manifest is null)
        {
            throw new InvalidOperationException("The synthetic interrupted vault could not be inspected.");
        }

        try
        {
            var state = SourceEnrollmentAuthority.ValidateAndNormalizeState(
                store.LoadSourceEnrollmentState(manifest, dataKey));
            var resources = store.ListResources(manifest);
            Require(state.PendingFolderSourceIds is { Length: 2 } &&
                    state.Enrollments.Length == 0 &&
                    state.Folders is { Length: 0 } &&
                    resources.Count == expectedCanonicalRecords,
                "The persisted journal and committed canonical subset did not match the interruption boundary.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private static void AssertNoIncompleteFolderBatch(VaultLifecycle lifecycle, string scenario)
    {
        using var operation = lifecycle.TryBeginOperation() ??
            throw new InvalidOperationException("The recovered vault did not resume operations after " + scenario + ".");
        var state = operation.LoadSourceEnrollmentState();
        Require(operation.ListResources().Count == 0 &&
                state.Enrollments.Length == 0 &&
                state.Folders is { Length: 0 } &&
                state.PendingFolderSourceIds is { Length: 0 },
            "Unlock recovery exposed a partial canonical folder batch after " + scenario + ".");
    }

    private static SourceIngestionCoordinator CreateRecoveryCoordinator() =>
        new(
            new SourceEnrollmentAuthority(),
            new SourceNormalizerRegistry(),
            new S11T2SyntheticSnapshotReader(),
            new SourceFolderFileEnumerator());

    private static SourceFolderEnrollmentDraft RecoveryFolderSelection(string sourceRoot) =>
        new(
            sourceRoot,
            "s11t2-recovery-zone",
            "codex.session.jsonl",
            1,
            Recursive: true,
            IncludePatterns: ["**/*.jsonl"],
            ExcludePatterns: Array.Empty<string>());

    private static async Task CreateSyntheticRecoverySourcesAsync(string root)
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "alpha.jsonl"),
            "{\"synthetic\":\"alpha\"}\n",
            new UTF8Encoding(false)).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(root, "beta.jsonl"),
            "{\"synthetic\":\"beta\"}\n",
            new UTF8Encoding(false)).ConfigureAwait(false);
    }


    private sealed class S11T2SyntheticSnapshotReader : ISourceSnapshotReader
    {
        public SourceSnapshotResult TryReadSnapshot(string locator) =>
            new(SourceSnapshotStatus.Snapshot, File.ReadAllBytes(locator));
    }

    private static string ComputeFileHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

public class S11T2RealStorageOperation : DispatchProxy
{
    private IVaultOperation? _inner;
    private Action? _afterFirstCanonicalWrite;
    private int _successfulCanonicalWrites;

    public int SuccessfulCanonicalWrites => Volatile.Read(ref _successfulCanonicalWrites);

    public static IVaultOperation Wrap(IVaultOperation inner, Action afterFirstCanonicalWrite)
    {
        var proxy = DispatchProxy.Create<IVaultOperation, S11T2RealStorageOperation>();
        ((S11T2RealStorageOperation)(object)proxy).Initialize(inner, afterFirstCanonicalWrite);
        return proxy;
    }

    private void Initialize(IVaultOperation inner, Action afterFirstCanonicalWrite)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _afterFirstCanonicalWrite = afterFirstCanonicalWrite ?? throw new ArgumentNullException(nameof(afterFirstCanonicalWrite));
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            throw new InvalidOperationException("A real storage operation member is required.");
        }

        object? result;
        try
        {
            result = targetMethod.Invoke(_inner, args ?? Array.Empty<object?>());
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        var arguments = args ?? Array.Empty<object?>();
        if (targetMethod.Name == "WriteContent" &&
            arguments.Length == 4 &&
            arguments[3] is long expectedRevision &&
            expectedRevision == 0 &&
            Interlocked.Increment(ref _successfulCanonicalWrites) == 1)
        {
            _afterFirstCanonicalWrite!();
        }

        return result;
    }
}
