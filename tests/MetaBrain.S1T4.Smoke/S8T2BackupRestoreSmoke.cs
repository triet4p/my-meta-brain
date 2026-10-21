using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S8-T2 runtime proof for the encrypted backup, the clean restore and the
/// owner-scoped plaintext export.
/// <para>
/// The scenario drives the real owner service executable over its real owner
/// control pipe: it writes a synthetic dataset with revisions, a relation, a
/// source, a lifecycle record, an archive, a deletion and a live grant, takes
/// one encrypted snapshot while the vault is being written to, inspects the
/// ciphertext and the temporary outputs, restores the snapshot into a clean
/// directory with the right credential, with the wrong credential, with the
/// recovery code and with a tampered container, and then starts a second
/// service against the restored vault to read the restored records, rebuild
/// the private index and prove that no grant, session or used token came
/// back. The export is read back as plain text and checked for the records
/// the owner selected and for the citations the owner did not.
/// </para>
/// <para>
/// Everything the scenario creates is synthetic and stays inside the
/// repository's ignored runtime directory. The scenario never changes a
/// permission, an owner, a registry entry or a machine setting.
/// </para>
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S8T2Zone = "s8t2-owner-zone";
    private const string S8T2ItemA = "s8t2-item-a";
    private const string S8T2ItemB = "s8t2-item-b";
    private const string S8T2Purged = "s8t2-item-purged";
    private const string S8T2SourceIn = "s8t2-source-in";
    private const string S8T2SourceOut = "s8t2-source-out";
    private const string S8T2Relation = "rel-s8t2-a-b";
    private const string S8T2ItemABody = "S8T2_SYNTHETIC_ITEM_A_BODY_4c19";
    private const string S8T2ItemBBody = "S8T2_SYNTHETIC_ITEM_B_BODY_77e2";
    private const string S8T2SourceInTitle = "S8T2_SYNTHETIC_SOURCE_IN_TITLE_1a5f";
    private const string S8T2SourceOutTitle = "S8T2_SYNTHETIC_SOURCE_OUT_TITLE_9d33";
    private const string S8T2SourceOutLocator = "s8t2-synthetic-out-locator-6b08";

    internal static async Task RunS8T2BackupRestoreAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S8-T2 backup smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S8-T2 encrypted backup, clean restore, key negatives and owner-scoped export");
        using var fixture = S8T2Fixture.Create();
        RunningService? live = null;
        RunningService? restored = null;
        var passphraseBytes = RandomNumberGenerator.GetBytes(24);
        var passphrase = Convert.ToBase64String(passphraseBytes);
        CryptographicOperations.ZeroMemory(passphraseBytes);
        var wrongPassphrase = "S8T2_WRONG_PASSPHRASE_5f20";
        try
        {
            live = await StartServiceAsync(connectionsExecutable, fixture.LiveSettingsPath).ConfigureAwait(false);
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The synthetic S8-T2 vault was not provisioned (" + provisioned.StandardError.Trim() + ").");
            var recoveryCode = ParseRecoveryCode(provisioned.StandardOutput);
            var unlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S8-T2 vault did not unlock (" + unlocked.StandardError.Trim() + ").");

            await WriteS8T2DatasetAsync(connectionsExecutable, fixture).ConfigureAwait(false);

            // A live grant and a redeemed session exist before the snapshot:
            // the restore must not bring either of them back.
            var handoff = Path.Combine(fixture.OutputDirectory, "s8t2.grant-handoff");
            var sessionFile = Path.Combine(fixture.OutputDirectory, "s8t2.session-handoff");
            var expiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O", CultureInfo.InvariantCulture);
            var grant = await RunOwnerCommandWithInputAsync(connectionsExecutable, "ISSUE" + Environment.NewLine,
                "owner", "grant", "--control-pipe", fixture.ControlPipe,
                "--resource-ids", S8T2ItemA,
                "--operations", "memory.read",
                "--expires-at-utc", expiry,
                "--handoff-file", handoff).ConfigureAwait(false);
            Require(grant.ExitCode == 0, "The owner could not issue the S8-T2 scoped grant (" + grant.StandardError.Trim() + ").");
            var redeem = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
            Require(redeem.ExitCode == 0 && redeem.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
                "The S8-T2 scoped grant could not be redeemed (" + redeem.StandardError.Trim() + ").");

            // The snapshot is taken while the vault is being written to.
            var backupPath = Path.Combine(fixture.OutputDirectory, "s8t2-backup.mbbackup");
            var writing = true;
            var written = 0;
            var writer = Task.Run(async () =>
            {
                var index = 0;
                while (Volatile.Read(ref writing) && index < 500)
                {
                    var draft = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        kind = "lesson",
                        author = "user",
                        creationMethod = "directWrite",
                        evidence = "unverified",
                        level = "l1",
                        title = "S8T2 concurrent title " + index.ToString(CultureInfo.InvariantCulture),
                        body = "S8T2_SYNTHETIC_CONCURRENT_BODY_" + index.ToString(CultureInfo.InvariantCulture),
                        sources = Array.Empty<object>(),
                        occurredAtUtc = (DateTimeOffset?)null,
                        recordedAtUtc = DateTimeOffset.UtcNow,
                        applicability = (object?)null,
                    });
                    var reply = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
                    {
                        protocolVersion = 1,
                        operation = "memory.create",
                        resourceId = "s8t2-concurrent-" + index.ToString(CultureInfo.InvariantCulture),
                        zoneId = S8T2Zone,
                        contentBase64 = Convert.ToBase64String(draft),
                    }).ConfigureAwait(false);
                    if (ReplyCode(reply) == "memory_created")
                    {
                        Interlocked.Increment(ref written);
                    }

                    index++;
                    await Task.Delay(5).ConfigureAwait(false);
                }
            });

            var backup = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "backup", "--control-pipe", fixture.ControlPipe,
                "--output-file", backupPath).ConfigureAwait(false);
            Volatile.Write(ref writing, false);
            await writer.ConfigureAwait(false);
            Require(backup.ExitCode == 0 && backup.StandardOutput.Contains("BACKUP written", StringComparison.Ordinal),
                "The concurrent S8-T2 backup did not complete (" + backup.StandardError.Trim() + ").");
            Require(written > 0, "The S8-T2 concurrent writer never committed a record, so the backup ran against a quiet vault.");
            Console.WriteLine("OBSERVATION the backup ran while the vault was being written to; committed concurrent writes=" +
                written.ToString(CultureInfo.InvariantCulture));

            // The already-used token is refused as *used*, not as unknown: the
            // consumed-token verifier is what refuses it, and the verifier
            // list is what the backup has to carry over.
            var liveRedeem = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                "--handoff-file", handoff, "--session-file", Path.Combine(fixture.OutputDirectory, "s8t2.redeem-again-live")).ConfigureAwait(false);
            Require(liveRedeem.ExitCode != 0 &&
                    liveRedeem.StandardError.Contains("token_used", StringComparison.Ordinal) &&
                    !liveRedeem.StandardError.Contains("token_unknown", StringComparison.Ordinal),
                "The live S8-T2 vault did not refuse the already used token as used (exit=" +
                liveRedeem.ExitCode.ToString(CultureInfo.InvariantCulture) +
                " err=" + liveRedeem.StandardError.Trim() + ").");
            Console.WriteLine("OBSERVATION the live vault refuses the already used token with token_used, not token_unknown");

            // The backup file is ciphertext: no record content, no credential.
            var backupBytes = await File.ReadAllBytesAsync(backupPath).ConfigureAwait(false);
            var backupText = Encoding.UTF8.GetString(backupBytes);
            Require(!backupText.Contains(S8T2ItemABody, StringComparison.Ordinal) &&
                    !backupText.Contains(S8T2SourceInTitle, StringComparison.Ordinal) &&
                    !backupText.Contains(S8T2SourceOutLocator, StringComparison.Ordinal) &&
                    !backupText.Contains(passphrase, StringComparison.Ordinal) &&
                    !backupText.Contains(recoveryCode, StringComparison.Ordinal),
                "The S8-T2 backup file exposed private content, the passphrase or the recovery key in plaintext.");
            Require(backupText.Contains("\"ciphertext\"", StringComparison.Ordinal),
                "The S8-T2 backup file is not an encrypted container.");
            var leftovers = Directory.GetFiles(fixture.OutputDirectory, "*.tmp", SearchOption.TopDirectoryOnly);
            Require(leftovers.Length == 0,
                "The S8-T2 backup left a temporary output behind (" + string.Join(",", leftovers) + ").");
            var vaultPlaintext = Directory.GetFiles(fixture.VaultDirectoryPath, "*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".enc", StringComparison.Ordinal) || path.EndsWith(".mbv", StringComparison.Ordinal))
                .Any(path => File.ReadAllText(path).Contains(S8T2ItemABody, StringComparison.Ordinal));
            Require(!vaultPlaintext, "The live S8-T2 vault stored a private record body in plaintext.");
            Console.WriteLine("OBSERVATION the backup file is ciphertext=" + backupBytes.Length.ToString(CultureInfo.InvariantCulture) +
                " bytes and no temporary output remained");

            // A wrong credential fails closed and writes nothing.
            var wrongTarget = Path.Combine(fixture.RestoredRoot, "wrong-key-vault");
            var wrong = await RunOwnerCommandWithInputAsync(connectionsExecutable, wrongPassphrase + Environment.NewLine,
                "owner", "restore", "--control-pipe", fixture.ControlPipe,
                "--backup-file", backupPath, "--target-directory", wrongTarget).ConfigureAwait(false);
            Require(wrong.ExitCode != 0 && wrong.StandardError.Contains("restore_denied", StringComparison.Ordinal),
                "A wrong passphrase restored the S8-T2 backup (" + wrong.StandardError.Trim() + ").");
            Require(!Directory.Exists(wrongTarget), "A refused S8-T2 restore wrote a target directory.");

            // The lost-key case: neither the passphrase nor the recovery code opens it.
            var lostTarget = Path.Combine(fixture.RestoredRoot, "lost-key-vault");
            var lost = await RunOwnerCommandWithInputAsync(connectionsExecutable, wrongPassphrase + Environment.NewLine,
                "owner", "restore-with-recovery-code", "--control-pipe", fixture.ControlPipe,
                "--backup-file", backupPath, "--target-directory", lostTarget).ConfigureAwait(false);
            Require(lost.ExitCode != 0 && lost.StandardError.Contains("restore_denied", StringComparison.Ordinal),
                "A lost recovery key restored the S8-T2 backup (" + lost.StandardError.Trim() + ").");
            Require(!Directory.Exists(lostTarget), "A refused S8-T2 recovery restore wrote a target directory.");

            // A tampered container fails closed as well.
            var tamperedPath = Path.Combine(fixture.OutputDirectory, "s8t2-tampered.mbbackup");
            await File.WriteAllBytesAsync(tamperedPath, TamperS8T2Container(backupBytes)).ConfigureAwait(false);
            var tamperedTarget = Path.Combine(fixture.RestoredRoot, "tampered-vault");
            var tampered = await RunOwnerCommandWithInputAsync(connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "restore", "--control-pipe", fixture.ControlPipe,
                "--backup-file", tamperedPath, "--target-directory", tamperedTarget).ConfigureAwait(false);
            Require(tampered.ExitCode != 0 && tampered.StandardError.Contains("restore_corrupt", StringComparison.Ordinal),
                "A tampered S8-T2 backup was restored (" + tampered.StandardError.Trim() + ").");
            Require(!Directory.Exists(tamperedTarget), "A refused S8-T2 restore of a tampered backup wrote a target directory.");

            // A missing credential never reaches the vault.
            var missingTarget = Path.Combine(fixture.RestoredRoot, "missing-key-vault");
            var missing = await RunOwnerCommandWithInputAsync(connectionsExecutable, string.Empty,
                "owner", "restore", "--control-pipe", fixture.ControlPipe,
                "--backup-file", backupPath, "--target-directory", missingTarget).ConfigureAwait(false);
            Require(missing.ExitCode != 0 && missing.StandardError.Contains("credential_required", StringComparison.Ordinal),
                "A missing credential restored the S8-T2 backup (" + missing.StandardError.Trim() + ").");
            Require(!Directory.Exists(missingTarget), "A refused S8-T2 restore without a credential wrote a target directory.");

            // A truncated container fails closed as well.
            var truncatedPath = Path.Combine(fixture.OutputDirectory, "s8t2-truncated.mbbackup");
            await File.WriteAllBytesAsync(truncatedPath, backupBytes[..(backupBytes.Length / 2)]).ConfigureAwait(false);
            var truncatedTarget = Path.Combine(fixture.RestoredRoot, "truncated-vault");
            var truncated = await RunOwnerCommandWithInputAsync(connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "restore", "--control-pipe", fixture.ControlPipe,
                "--backup-file", truncatedPath, "--target-directory", truncatedTarget).ConfigureAwait(false);
            Require(truncated.ExitCode != 0 && truncated.StandardError.Contains("restore_corrupt", StringComparison.Ordinal),
                "A truncated S8-T2 backup was restored (" + truncated.StandardError.Trim() + ").");
            Require(!Directory.Exists(truncatedTarget), "A refused S8-T2 restore of a truncated backup wrote a target directory.");

            // The recovery code opens the same backup into another clean location.
            var recoveryTarget = Path.Combine(fixture.RestoredRoot, "recovery-vault");
            var recovered = await RunOwnerCommandWithInputAsync(connectionsExecutable, recoveryCode + Environment.NewLine,
                "owner", "restore-with-recovery-code", "--control-pipe", fixture.ControlPipe,
                "--backup-file", backupPath, "--target-directory", recoveryTarget).ConfigureAwait(false);
            Require(recovered.ExitCode == 0 && recovered.StandardOutput.Contains("RESTORE written", StringComparison.Ordinal),
                "The S8-T2 recovery code did not restore the backup (" + recovered.StandardError.Trim() + ").");

            // The passphrase restores into the clean location the second service reads.
            var restoredVault = await RunOwnerCommandWithInputAsync(connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "restore", "--control-pipe", fixture.ControlPipe,
                "--backup-file", backupPath, "--target-directory", fixture.RestoreTargetDirectory).ConfigureAwait(false);
            Require(restoredVault.ExitCode == 0 && restoredVault.StandardOutput.Contains("RESTORE written", StringComparison.Ordinal),
                "The S8-T2 passphrase did not restore the backup (" + restoredVault.StandardError.Trim() + ").");

            // A second service reads the restored vault.
            restored = await StartServiceAsync(connectionsExecutable, fixture.RestoredSettingsPath).ConfigureAwait(false);
            var restoredUnlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.RestoredControlPipe).ConfigureAwait(false);
            Require(restoredUnlock.ExitCode == 0 && restoredUnlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The restored S8-T2 vault did not unlock with the owner's passphrase (" + restoredUnlock.StandardError.Trim() + ").");

            var restoredItem = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.RestoredControlPipe,
                "--resource-id", S8T2ItemA).ConfigureAwait(false);
            Require(restoredItem.ExitCode == 0 && restoredItem.StandardOutput.Contains(S8T2ItemABody, StringComparison.Ordinal),
                "The restored S8-T2 record lost its body (" + restoredItem.StandardError.Trim() + ").");
            Require(restoredItem.StandardOutput.Contains("revision=3", StringComparison.Ordinal),
                "The restored S8-T2 record lost its revision (" + restoredItem.StandardOutput.Trim() + ").");

            var restoredSource = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-read", "--control-pipe", fixture.RestoredControlPipe,
                "--resource-id", S8T2SourceIn).ConfigureAwait(false);
            Require(restoredSource.ExitCode == 0 && restoredSource.StandardOutput.Contains(S8T2SourceInTitle, StringComparison.Ordinal),
                "The restored S8-T2 source was lost (" + restoredSource.StandardError.Trim() + ").");

            var restoredRelation = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "relation-read", "--control-pipe", fixture.RestoredControlPipe,
                "--resource-id", S8T2Relation).ConfigureAwait(false);
            Require(restoredRelation.ExitCode == 0 && restoredRelation.StandardOutput.Contains(S8T2ItemA, StringComparison.Ordinal),
                "The restored S8-T2 relation was lost (" + restoredRelation.StandardError.Trim() + ").");

            var restoredLifecycle = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-read", "--control-pipe", fixture.RestoredControlPipe,
                "--resource-id", S8T2ItemA).ConfigureAwait(false);
            Require(restoredLifecycle.ExitCode == 0 && restoredLifecycle.StandardOutput.Contains("publication=", StringComparison.Ordinal),
                "The restored S8-T2 lifecycle record was lost (" + restoredLifecycle.StandardError.Trim() + ").");

            var restoredArchive = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-retained-read", "--control-pipe", fixture.RestoredControlPipe,
                "--resource-id", S8T2ItemA, "--resource-revision", "3").ConfigureAwait(false);
            Require(restoredArchive.ExitCode == 0 && restoredArchive.StandardOutput.Contains("S8T2_SYNTHETIC_ARCHIVE_MARKER", StringComparison.Ordinal),
                "The restored S8-T2 archive did not keep its retained revision (" + restoredArchive.StandardError.Trim() + ").");

            var restoredPurged = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.RestoredControlPipe,
                "--resource-id", S8T2Purged).ConfigureAwait(false);
            Require(restoredPurged.ExitCode != 0,
                "The restored S8-T2 vault resurrected a purged record (" + restoredPurged.StandardOutput.Trim() + ").");

            // The private index is rebuilt from the restored canonical set.
            var rebuilt = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.RestoredControlPipe).ConfigureAwait(false);
            Require(rebuilt.ExitCode == 0 && rebuilt.StandardOutput.Contains("INDEX rebuilt", StringComparison.Ordinal),
                "The restored S8-T2 vault could not rebuild its private index (" + rebuilt.StandardError.Trim() + ").");
            var rebuiltRows = rebuilt.StandardOutput.Contains("rows=", StringComparison.Ordinal)
                ? rebuilt.StandardOutput[(rebuilt.StandardOutput.IndexOf("rows=", StringComparison.Ordinal) + 5)..]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]
                : "0";
            Require(rebuiltRows != "0", "The restored S8-T2 private index rebuilt no rows.");

            // No live grant, no session and no redeemable token came back.
            var restoredGrants = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "grants", "--control-pipe", fixture.RestoredControlPipe).ConfigureAwait(false);
            Require(restoredGrants.ExitCode == 0 && restoredGrants.StandardOutput.Contains("GRANTS count=0", StringComparison.Ordinal),
                "The restored S8-T2 vault resurrected a live grant (" + restoredGrants.StandardOutput.Trim() + ").");
            var restoredSessions = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "sessions", "--control-pipe", fixture.RestoredControlPipe).ConfigureAwait(false);
            Require(restoredSessions.ExitCode == 0 && restoredSessions.StandardOutput.Contains("SESSIONS count=0", StringComparison.Ordinal),
                "The restored S8-T2 vault resurrected a live session (" + restoredSessions.StandardOutput.Trim() + ").");
            var restoredRedeem = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "redeem", "--agent-pipe", fixture.RestoredAgentPipe,
                "--handoff-file", handoff, "--session-file", Path.Combine(fixture.OutputDirectory, "s8t2.redeem-after-restore")).ConfigureAwait(false);
            // The refusal has to name the consumed token, not an unknown one:
            // "token_used" can only come from the consumed-token verifier that
            // the restore carried over, while "token_unknown" would mean the
            // verifier list was lost together with the live grant.
            Require(restoredRedeem.ExitCode != 0 &&
                    restoredRedeem.StandardError.Contains("token_used", StringComparison.Ordinal) &&
                    !restoredRedeem.StandardError.Contains("token_unknown", StringComparison.Ordinal),
                "The restored S8-T2 vault did not refuse the already used token as used (exit=" +
                restoredRedeem.ExitCode.ToString(CultureInfo.InvariantCulture) +
                " out=" + restoredRedeem.StandardOutput.Trim() +
                " err=" + restoredRedeem.StandardError.Trim() + ").");
            Console.WriteLine("OBSERVATION the restored vault refuses the already used token with token_used, so the consumed-token verifier survived the restore");

            // The owner-selected export: readable, scoped, and free of out-of-scope citations.
            var exportPath = Path.Combine(fixture.OutputDirectory, "s8t2-export.md");
            var export = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "export", "--control-pipe", fixture.ControlPipe,
                "--resource-ids", S8T2ItemA + "," + S8T2SourceIn,
                "--output-file", exportPath,
                "--include-relations", "true",
                "--include-lifecycle-states", "true").ConfigureAwait(false);
            Require(export.ExitCode == 0 && export.StandardOutput.Contains("EXPORT written", StringComparison.Ordinal),
                "The S8-T2 export did not complete (" + export.StandardError.Trim() + ").");
            var exportText = await File.ReadAllTextAsync(exportPath).ConfigureAwait(false);
            Require(exportText.StartsWith("# Meta Brain", StringComparison.Ordinal),
                "The S8-T2 export is not a readable document.");
            Require(exportText.Contains(S8T2ItemABody, StringComparison.Ordinal) &&
                    exportText.Contains(S8T2SourceInTitle, StringComparison.Ordinal),
                "The S8-T2 export lost the records the owner selected.");
            Require(!exportText.Contains(S8T2SourceOutTitle, StringComparison.Ordinal) &&
                    !exportText.Contains(S8T2SourceOutLocator, StringComparison.Ordinal) &&
                    !exportText.Contains(S8T2SourceOut, StringComparison.Ordinal) &&
                    !exportText.Contains(S8T2ItemBBody, StringComparison.Ordinal),
                "The S8-T2 export disclosed a record or a citation outside the selected scope.");
            Require(exportText.Contains("withheld", StringComparison.OrdinalIgnoreCase),
                "The S8-T2 export did not report the citation it withheld.");
            Console.WriteLine("OBSERVATION the S8-T2 export wrote the selected records only and withheld the out-of-scope citation");

            // The live vault is untouched by the backup: it still serves its
            // records, and locking still closes every private read.
            var liveAfterBackup = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S8T2ItemA).ConfigureAwait(false);
            Require(liveAfterBackup.ExitCode == 0 && liveAfterBackup.StandardOutput.Contains(S8T2ItemABody, StringComparison.Ordinal),
                "The S8-T2 backup changed the live vault (" + liveAfterBackup.StandardError.Trim() + ").");
            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0 && locked.StandardOutput.Contains("VAULT locked", StringComparison.Ordinal),
                "The live S8-T2 vault did not lock (" + locked.StandardError.Trim() + ").");
            var lockedRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S8T2ItemA).ConfigureAwait(false);
            Require(lockedRead.ExitCode != 0,
                "A locked S8-T2 vault still served a private record (" + lockedRead.StandardOutput.Trim() + ").");

            Console.WriteLine("LIMIT the S8-T2 export is a plaintext copy outside the vault: a later purge, lock or export does not revoke a copy that was already made, and the export says so in its own header.");
        }
        finally
        {
            foreach (var service in new[] { restored, live })
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
                        await service.Process.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }

    private static async Task WriteS8T2DatasetAsync(string executable, S8T2Fixture fixture)
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
            "--input-file", await WriteS8T2TempAsync(fixture, sourceIn, "source-in.json").ConfigureAwait(false)).ConfigureAwait(false);
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
            "--input-file", await WriteS8T2TempAsync(fixture, sourceOut, "source-out.json").ConfigureAwait(false)).ConfigureAwait(false);
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
            "--input-file", await WriteS8T2TempAsync(fixture, itemA, "item-a.json").ConfigureAwait(false)).ConfigureAwait(false);
        Require(itemACreated.ExitCode == 0 && itemACreated.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
            "The synthetic S8-T2 item A was not stored (" + itemACreated.StandardError.Trim() + ").");

        // The revised item cites one in-scope source and one source outside the
        // scope the owner will export: the second citation must never travel.
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
            "--input-file", await WriteS8T2TempAsync(fixture, revisedA, "item-a-revised.json").ConfigureAwait(false)).ConfigureAwait(false);
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
            "--input-file", await WriteS8T2TempAsync(fixture, itemB, "item-b.json").ConfigureAwait(false)).ConfigureAwait(false);
        Require(itemBCreated.ExitCode == 0 && itemBCreated.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
            "The synthetic S8-T2 item B was not stored (" + itemBCreated.StandardError.Trim() + ").");

        var purged = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "lesson",
            author = "user",
            creationMethod = "directWrite",
            evidence = "unverified",
            level = "l1",
            title = "S8T2 synthetic purged item",
            body = "S8T2_SYNTHETIC_PURGED_BODY_2f88",
            sources = Array.Empty<object>(),
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = DateTimeOffset.UtcNow,
            applicability = (object?)null,
        });
        var purgedCreated = await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", S8T2Purged, "--zone-id", S8T2Zone,
            "--input-file", await WriteS8T2TempAsync(fixture, purged, "item-purged.json").ConfigureAwait(false)).ConfigureAwait(false);
        Require(purgedCreated.ExitCode == 0, "The synthetic S8-T2 purged item was not stored (" + purgedCreated.StandardError.Trim() + ").");

        var lifecycle = JsonSerializer.SerializeToUtf8Bytes(new
        {
            creationMethod = "directWrite",
            reason = "S8T2 synthetic lifecycle record.",
            occurred = (object?)null,
            recorded = (object?)null,
            validFrom = (object?)null,
            validUntil = (object?)null,
            reviewAfter = (object?)null,
        });
        var lifecycleCreated = await RunOwnerCommandAsync(executable,
            "owner", "lifecycle-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", S8T2ItemA,
            "--input-file", await WriteS8T2TempAsync(fixture, lifecycle, "lifecycle.json").ConfigureAwait(false)).ConfigureAwait(false);
        Require(lifecycleCreated.ExitCode == 0 && lifecycleCreated.StandardOutput.Contains("LIFECYCLE stored", StringComparison.Ordinal),
            "The synthetic S8-T2 lifecycle record was not stored (" + lifecycleCreated.StandardError.Trim() + ").");

        var relation = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "relatedTo",
            state = "proposed",
            from = new { resourceId = S8T2ItemA, endpointKind = "memoryItem", revision = 2, span = (object?)null },
            to = new { resourceId = S8T2ItemB, endpointKind = "memoryItem", revision = 1, span = (object?)null },
            creationMethod = "directWrite",
            reason = "S8T2 synthetic relation between two items.",
            stateReason = (string?)null,
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = DateTimeOffset.UtcNow,
        });
        var relationCreated = await RunOwnerCommandAsync(executable,
            "owner", "relation-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", S8T2Relation, "--zone-id", S8T2Zone,
            "--input-file", await WriteS8T2TempAsync(fixture, relation, "relation.json").ConfigureAwait(false)).ConfigureAwait(false);
        Require(relationCreated.ExitCode == 0 && relationCreated.StandardOutput.Contains("RELATION stored revision=1", StringComparison.Ordinal),
            "The synthetic S8-T2 relation was not stored (" + relationCreated.StandardError.Trim() + ").");

        // A retained revision: the archive resource holds the older body.
        var archivedDraft = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "lesson",
            author = "user",
            creationMethod = "directWrite",
            evidence = "unverified",
            level = "l1",
            title = "S8T2 synthetic item A",
            body = S8T2ItemABody + " S8T2_SYNTHETIC_ARCHIVE_MARKER_3b6e",
            sources = new[]
            {
                new { sourceId = S8T2SourceIn, sourceRevision = 1L },
                new { sourceId = S8T2SourceOut, sourceRevision = 1L },
            },
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = DateTimeOffset.UtcNow,
            applicability = (object?)null,
        });
        var archived = await RunOwnerCommandAsync(executable,
            "owner", "memory-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", S8T2ItemA, "--zone-id", S8T2Zone,
            "--expected-revision", "2",
            "--input-file", await WriteS8T2TempAsync(fixture, archivedDraft, "item-a-archived.json").ConfigureAwait(false)).ConfigureAwait(false);
        Require(archived.ExitCode == 0 && archived.StandardOutput.Contains("MEMORY stored revision=3", StringComparison.Ordinal),
            "The synthetic S8-T2 item A did not reach its archive revision (" + archived.StandardError.Trim() + ").");
        var retained = await RunOwnerCommandAsync(executable,
                "owner", "lifecycle-retain", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S8T2ItemA, "--expected-revision", "1").ConfigureAwait(false);
        Require(retained.ExitCode == 0 && retained.StandardOutput.Contains("RETAINED", StringComparison.Ordinal),
            "The synthetic S8-T2 archive was not retained (" + retained.StandardError.Trim() + ").");

        // A purged record: the deletion has to survive the restore.
        var preview = await RunOwnerCommandAsync(executable,
            "owner", "purge-preview", "--control-pipe", fixture.ControlPipe,
            "--resource-id", S8T2Purged, "--resource-revision", "1",
            "--reason", "S8T2 synthetic purge reason.").ConfigureAwait(false);
        Require(preview.ExitCode == 0 && preview.StandardOutput.Contains("PURGE PREVIEW", StringComparison.Ordinal),
            "The synthetic S8-T2 purge preview did not run (" + preview.StandardError.Trim() + ").");
        var purgeId = ParseS8T2Value(preview.StandardOutput, "PURGE PREVIEW id=", ' ');
        var impacted = ParseS8T2Impacted(preview.StandardOutput);
        var generation = ParseS8T2Value(preview.StandardOutput, "generation=", ' ');
        var purgeArguments = new[]
        {
            "owner", "purge-confirm", "--control-pipe", fixture.ControlPipe,
            "--purge-id", purgeId, "--resource-id", S8T2Purged, "--resource-revision", "1",
            "--target-zone", S8T2Zone, "--target-kind", "memory-or-source",
            "--impacted", string.Join(",", impacted),
            "--live-generation", generation,
            "--reason", "S8T2 synthetic purge reason.",
        };
        var confirmed = await RunOwnerCommandWithInputAsync(executable, "PURGE" + Environment.NewLine, purgeArguments).ConfigureAwait(false);
        Require(confirmed.ExitCode == 0,
            "The synthetic S8-T2 purge could not be confirmed (exit=" + confirmed.ExitCode.ToString(CultureInfo.InvariantCulture) +
            " out=" + confirmed.StandardOutput.Trim() + " err=" + confirmed.StandardError.Trim() +
            " args=" + string.Join(" ", purgeArguments) + ").");
    }

    private static async Task<string> WriteS8T2TempAsync(S8T2Fixture fixture, byte[] content, string name)
    {
        var path = Path.Combine(fixture.OutputDirectory, name);
        await File.WriteAllBytesAsync(path, content).ConfigureAwait(false);
        return path;
    }

    private static string ParseS8T2Value(string output, string marker, char terminator)
    {
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        Require(start >= 0, "The S8-T2 output did not carry the expected marker " + marker + ".");
        start += marker.Length;
        var end = output.IndexOfAny([terminator, '\r', '\n'], start);
        return (end < 0 ? output[start..] : output[start..end]).Trim();
    }

    private static string[] ParseS8T2Impacted(string output)
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

        Require(tokens.Count > 0, "The S8-T2 purge preview named no impacted copies.");
        return tokens.ToArray();
    }

    /// <summary>
    /// Flips one character inside the encrypted payload of a backup container.
    /// The container stays valid JSON, so the restore has to notice the change
    /// through the authenticated envelope itself.
    /// </summary>
    private static byte[] TamperS8T2Container(byte[] container)
    {
        using var document = JsonDocument.Parse(container);
        var root = document.RootElement;
        var ciphertext = root.GetProperty("ciphertext").GetString() ?? string.Empty;
        Require(ciphertext.Length > 16, "The S8-T2 backup container had no ciphertext to tamper with.");
        var index = ciphertext.Length / 2;
        var replacement = ciphertext[index] == 'A' ? 'B' : 'A';
        var tampered = ciphertext[..index] + replacement + ciphertext[(index + 1)..];
        var patched = new
        {
            formatVersion = root.GetProperty("formatVersion").GetInt32(),
            backupId = root.GetProperty("backupId").GetString(),
            createdAtUtc = root.GetProperty("createdAtUtc").GetString(),
            vaultId = root.GetProperty("vaultId").GetString(),
            keyEnvelope = root.GetProperty("keyEnvelope"),
            nonce = root.GetProperty("nonce").GetString(),
            tag = root.GetProperty("tag").GetString(),
            ciphertext = tampered,
        };
        return JsonSerializer.SerializeToUtf8Bytes(patched);
    }
}

/// <summary>
/// The S8-T2 fixture. It creates its directories inside the repository's
/// ignored runtime directory, writes the ordinary service settings file for
/// the current owner and never touches a permission, an owner or a machine
/// setting. The legacy smoke fixture is not used here because it changes
/// directory and file permissions.
/// </summary>
internal sealed class S8T2Fixture : IDisposable
{
    private bool _disposed;

    private S8T2Fixture(
        string root,
        string liveRoot,
        string liveSettingsPath,
        string controlPipe,
        string agentPipe,
        string outputDirectory,
        string vaultDirectoryPath,
        string restoredRoot,
        string restoredSettingsPath,
        string restoredControlPipe,
        string restoredAgentPipe,
        string restoreTargetDirectory)
    {
        Root = root;
        LiveRoot = liveRoot;
        LiveSettingsPath = liveSettingsPath;
        ControlPipe = controlPipe;
        AgentPipe = agentPipe;
        OutputDirectory = outputDirectory;
        VaultDirectoryPath = vaultDirectoryPath;
        RestoredRoot = restoredRoot;
        RestoredSettingsPath = restoredSettingsPath;
        RestoredControlPipe = restoredControlPipe;
        RestoredAgentPipe = restoredAgentPipe;
        RestoreTargetDirectory = restoreTargetDirectory;
    }

    public string Root { get; }
    public string LiveRoot { get; }
    public string LiveSettingsPath { get; }
    public string ControlPipe { get; }
    public string AgentPipe { get; }
    public string OutputDirectory { get; }
    public string VaultDirectoryPath { get; }
    public string RestoredRoot { get; }
    public string RestoredSettingsPath { get; }
    public string RestoredControlPipe { get; }
    public string RestoredAgentPipe { get; }
    public string RestoreTargetDirectory { get; }

    public static S8T2Fixture Create()
    {
        var repositoryRoot = SmokeFixture.FindRepositoryRoot();
        var root = Path.Combine(repositoryRoot, "runtime", "s8-t2", "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var liveRoot = Path.Combine(root, "live");
            var outputDirectory = Path.Combine(root, "output");
            var restoredRoot = Path.Combine(root, "restored");
            Directory.CreateDirectory(liveRoot);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(restoredRoot);

            var controlPipe = "MetaBrainS8T2-" + Guid.NewGuid().ToString("N");
            var agentPipe = controlPipe + ".agent";
            var liveSettingsPath = WriteSettings(liveRoot, controlPipe, agentPipe);
            var restoredControlPipe = "MetaBrainS8T2R-" + Guid.NewGuid().ToString("N");
            var restoredAgentPipe = restoredControlPipe + ".agent";
            var restoredSettingsPath = WriteSettings(restoredRoot, restoredControlPipe, restoredAgentPipe);
            return new S8T2Fixture(
                root,
                liveRoot,
                liveSettingsPath,
                controlPipe,
                agentPipe,
                outputDirectory,
                Path.Combine(liveRoot, "vault"),
                restoredRoot,
                restoredSettingsPath,
                restoredControlPipe,
                restoredAgentPipe,
                Path.Combine(restoredRoot, "vault"));
        }
        catch
        {
            TryDelete(root);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TryDelete(Root);
    }

    private static string WriteSettings(string dataRoot, string controlPipe, string agentPipe)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var ownerSid = identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows token has no user SID.");
        var path = Path.Combine(dataRoot, "service-settings.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 3,
                ownerSid,
                controlPipe,
                agentPipe,
            }),
            new UTF8Encoding(false));
        return path;
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
