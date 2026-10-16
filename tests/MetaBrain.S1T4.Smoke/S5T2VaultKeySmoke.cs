using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;

namespace MetaBrain.S1T4.Smoke;

// S5-T2: the owner key-provisioning, unlock/lock and recovery path the owner
// console drives. The new owner-channel vault commands are the only way the WPF
// console reaches the vault, so this scenario drives them against the real
// service, the real owner CLI and the real agent pipe: an uninitialized vault is
// provisioned with the owner passphrase (recovery material returned once), a
// too-short passphrase and a second provisioning are refused without changing
// the vault, a wrong or missing key and unusable recovery material never open
// the vault, the owner passphrase and the owner-held recovery key both open the
// same vault without restoring or resetting anything, and the lock the console
// asks for invalidates a live session and a pending token, stops private reads
// and private jobs, and is still in force after a restart.
internal static partial class OwnerServiceSmoke
{
    internal static async Task RunS5T2VaultKeyAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S5-T2 owner vault key smoke requires Windows named pipes.");
        }

        Console.WriteLine("RUN S5-T2 owner vault key: provision, wrong/missing key refusal, explicit lock lifecycle, owner-held recovery key");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        var passphrase = NewSyntheticPassphrase();
        var wrongPassphrase = NewSyntheticPassphrase();
        string? recoveryCode = null;
        try
        {
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);

            var initial = await OwnerChannelClient.ReadStatusAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(initial.Availability == OwnerChannelAvailability.Reachable && initial.VaultState == OwnerVaultState.Uninitialized,
                "The owner channel did not report the real uninitialized vault (" + initial + ").");
            Console.WriteLine("PASS the owner channel reports an uninitialized vault before provisioning");

            var shortProvision = await OwnerChannelClient.ProvisionAsync(fixture.ControlPipe, "s5t2-short", CancellationToken.None).ConfigureAwait(false);
            Require(shortProvision.Outcome == OwnerCommandOutcome.Refused && shortProvision.ErrorCode == "credential_required",
                "A too-short provisioning passphrase was not refused as a service answer (" + shortProvision + ").");
            var afterShort = await OwnerChannelClient.ReadStatusAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(afterShort.VaultState == OwnerVaultState.Uninitialized,
                "A refused provisioning passphrase still created a vault (" + afterShort + ").");
            Console.WriteLine("PASS a too-short passphrase is refused and no vault is created");

            var provision = await OwnerChannelClient.ProvisionAsync(fixture.ControlPipe, passphrase, CancellationToken.None).ConfigureAwait(false);
            Require(provision.Outcome == OwnerCommandOutcome.Completed && provision.VaultState == OwnerVaultState.Unlocked,
                "Provisioning the vault through the owner channel did not complete unlocked (" + provision + ").");
            Require(provision.RecoveryCode is { Length: >= 40 },
                "The service did not return usable recovery material for a fresh provisioning.");
            recoveryCode = provision.RecoveryCode!;
            Console.WriteLine("PASS the owner channel provisions the vault and returns recovery material once");

            var secondProvision = await OwnerChannelClient.ProvisionAsync(fixture.ControlPipe, NewSyntheticPassphrase(), CancellationToken.None).ConfigureAwait(false);
            Require(secondProvision.Outcome == OwnerCommandOutcome.Refused && secondProvision.ErrorCode == "vault_already_initialized",
                "A second provisioning was not refused (" + secondProvision + ").");
            Console.WriteLine("PASS a second provisioning is refused and never overwrites the existing vault");

            Require(!FixtureHoldsPlaintext(fixture, passphrase, recoveryCode),
                "Provisioning persisted the plaintext passphrase or recovery key under the fixture root.");
            Console.WriteLine("PASS neither the passphrase nor the recovery key is persisted in plaintext");

            var resourceId = "s5t2-private-resource";
            var zoneId = "s5t2-private-zone";
            var content = Encoding.UTF8.GetBytes("synthetic S5-T2 private body; it must survive lock and recovery unchanged.");
            var contentPath = Path.Combine(fixture.OutputDirectory, "s5t2-body.bin");
            await File.WriteAllBytesAsync(contentPath, content).ConfigureAwait(false);
            var write = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "write", "--control-pipe", fixture.ControlPipe, "--resource-id", resourceId,
                "--zone-id", zoneId, "--expected-revision", "0", "--input-file", contentPath).ConfigureAwait(false);
            Require(write.ExitCode == 0 && write.StandardOutput.Contains("WRITE stored revision=", StringComparison.Ordinal),
                "The real owner CLI did not store the synthetic private resource (exit=" + write.ExitCode + "; stderr=" + write.StandardError.Trim() + ").");
            var revision = ParseTrailingLong(write.StandardOutput, "WRITE stored revision=");
            Require(revision >= 1, "The stored synthetic private resource reported no revision.");

            var liveHandoff = Path.Combine(fixture.OutputDirectory, "s5t2-live.grant-handoff");
            var liveSessionPath = Path.Combine(fixture.OutputDirectory, "s5t2-live.session-handoff");
            var issueLive = await IssueGrantAsync(connectionsExecutable, fixture.ControlPipe, liveHandoff, resourceId).ConfigureAwait(false);
            Require(issueLive.ExitCode == 0, "The real owner CLI did not issue a synthetic scope grant (stderr=" + issueLive.StandardError.Trim() + ").");
            var liveRedeem = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "redeem", "--agent-pipe", fixture.AgentPipe, "--handoff-file", liveHandoff,
                "--session-file", liveSessionPath).ConfigureAwait(false);
            Require(liveRedeem.ExitCode == 0 && File.Exists(liveSessionPath),
                "The real agent pipe did not redeem the synthetic token (exit=" + liveRedeem.ExitCode + "; stderr=" + liveRedeem.StandardError.Trim() + ").");
            var allowedRead = await ReadThroughAgentAsync(connectionsExecutable, fixture.AgentPipe, liveSessionPath, resourceId, revision).ConfigureAwait(false);
            Require(allowedRead.ExitCode == 0 && allowedRead.StandardOutput.Contains("AGENT-BODY-BEGIN", StringComparison.Ordinal),
                "A live scoped session could not read the approved resource while unlocked (exit=" + allowedRead.ExitCode + "; stderr=" + allowedRead.StandardError.Trim() + ").");

            var pendingHandoff = Path.Combine(fixture.OutputDirectory, "s5t2-pending.grant-handoff");
            var pendingIssue = await IssueGrantAsync(connectionsExecutable, fixture.ControlPipe, pendingHandoff, resourceId).ConfigureAwait(false);
            Require(pendingIssue.ExitCode == 0, "The real owner CLI did not issue the pending synthetic token (stderr=" + pendingIssue.StandardError.Trim() + ").");
            Console.WriteLine("PASS unlocked: a live scoped session reads the approved resource and a second token is left pending");

            var locked = await OwnerChannelClient.LockAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(locked.Outcome == OwnerCommandOutcome.Completed && locked.VaultState == OwnerVaultState.Locked,
                "The owner-channel lock did not complete locked (" + locked + ").");
            var afterLock = await OwnerChannelClient.ReadStatusAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(afterLock.VaultState == OwnerVaultState.Locked, "The vault was not locked after the owner-channel lock (" + afterLock + ").");
            Console.WriteLine("PASS the owner-channel lock completes and the service reports locked");

            var deniedLiveRead = await ReadThroughAgentAsync(connectionsExecutable, fixture.AgentPipe, liveSessionPath, resourceId, revision).ConfigureAwait(false);
            Require(deniedLiveRead.ExitCode != 0 && !deniedLiveRead.StandardOutput.Contains("AGENT-BODY-BEGIN", StringComparison.Ordinal),
                "A live scoped session still received private bytes after the lock (exit=" + deniedLiveRead.ExitCode + ").");
            var lockedDecision = await AuthorizeThroughAgentAsync(connectionsExecutable, fixture.AgentPipe, liveSessionPath, resourceId, revision).ConfigureAwait(false);
            Require(lockedDecision.ExitCode == 3 && lockedDecision.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "The lock did not invalidate the live session for a new private authorization (exit=" + lockedDecision.ExitCode + "; stderr=" + lockedDecision.StandardError.Trim() + ").");
            Console.WriteLine("PASS a live scoped session is invalidated by the lock: no private bytes and vault_locked at the next authorization");

            var pendingWhileLocked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "redeem", "--agent-pipe", fixture.AgentPipe, "--handoff-file", pendingHandoff,
                "--session-file", Path.Combine(fixture.OutputDirectory, "s5t2-pending-locked.session-handoff")).ConfigureAwait(false);
            Require(pendingWhileLocked.ExitCode != 0 && pendingWhileLocked.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "A pending token was redeemable while the vault was locked (exit=" + pendingWhileLocked.ExitCode + "; stderr=" + pendingWhileLocked.StandardError.Trim() + ").");
            Console.WriteLine("PASS a pending token cannot be redeemed while the vault is locked");

            var privateReadWhileLocked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "read", "--control-pipe", fixture.ControlPipe, "--resource-id", resourceId,
                "--output-file", Path.Combine(fixture.OutputDirectory, "s5t2-locked-read.bin")).ConfigureAwait(false);
            Require(privateReadWhileLocked.ExitCode == 4 && privateReadWhileLocked.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !File.Exists(Path.Combine(fixture.OutputDirectory, "s5t2-locked-read.bin")),
                "The locked vault still served a private owner read (exit=" + privateReadWhileLocked.ExitCode + "; stderr=" + privateReadWhileLocked.StandardError.Trim() + ").");
            var privateJobWhileLocked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(privateJobWhileLocked.ExitCode != 0 && privateJobWhileLocked.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "A private index job still started while the vault was locked (exit=" + privateJobWhileLocked.ExitCode + "; stderr=" + privateJobWhileLocked.StandardError.Trim() + ").");
            Console.WriteLine("PASS locked: private reads and private jobs do not start through the existing core lifecycle");

            var wrongUnlock = await OwnerChannelClient.UnlockAsync(fixture.ControlPipe, wrongPassphrase, CancellationToken.None).ConfigureAwait(false);
            Require(wrongUnlock.Outcome == OwnerCommandOutcome.Refused && wrongUnlock.ErrorCode == "unlock_denied",
                "A wrong passphrase was not refused as a service answer (" + wrongUnlock + ").");
            var missingUnlock = await OwnerChannelClient.UnlockAsync(fixture.ControlPipe, string.Empty, CancellationToken.None).ConfigureAwait(false);
            Require(missingUnlock.Outcome == OwnerCommandOutcome.Refused,
                "A missing passphrase was not refused (" + missingUnlock + ").");
            var wrongRecovery = await OwnerChannelClient.UnlockWithRecoveryCodeAsync(
                fixture.ControlPipe, recoveryCode + "-tampered", CancellationToken.None).ConfigureAwait(false);
            Require(wrongRecovery.Outcome == OwnerCommandOutcome.Refused && wrongRecovery.ErrorCode == "unlock_denied",
                "Unusable recovery material was not refused as a service answer (" + wrongRecovery + ").");
            var stillLocked = await OwnerChannelClient.ReadStatusAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(stillLocked.VaultState == OwnerVaultState.Locked,
                "A wrong, missing or unusable key still opened the vault (" + stillLocked + ").");
            Console.WriteLine("PASS a wrong, missing or unusable key is refused and leaves the vault locked");

            var unlocked = await OwnerChannelClient.UnlockAsync(fixture.ControlPipe, passphrase, CancellationToken.None).ConfigureAwait(false);
            Require(unlocked.Outcome == OwnerCommandOutcome.Completed && unlocked.VaultState == OwnerVaultState.Unlocked,
                "The owner passphrase did not open the existing vault (" + unlocked + ").");
            var staleSession = await ReadThroughAgentAsync(connectionsExecutable, fixture.AgentPipe, liveSessionPath, resourceId, revision).ConfigureAwait(false);
            Require(staleSession.ExitCode != 0 && !staleSession.StandardOutput.Contains("AGENT-BODY-BEGIN", StringComparison.Ordinal),
                "A pre-lock session was still served after the next unlock (exit=" + staleSession.ExitCode + ").");
            var staleDecision = await AuthorizeThroughAgentAsync(connectionsExecutable, fixture.AgentPipe, liveSessionPath, resourceId, revision).ConfigureAwait(false);
            Require(staleDecision.ExitCode == 3 && staleDecision.StandardError.Contains("session_unknown", StringComparison.Ordinal),
                "A pre-lock session survived into the next unlocked epoch (exit=" + staleDecision.ExitCode + "; stderr=" + staleDecision.StandardError.Trim() + ").");
            Console.WriteLine("PASS the owner passphrase opens the existing vault and the pre-lock session stays gone");

            var relocked = await OwnerChannelClient.LockAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(relocked.Outcome == OwnerCommandOutcome.Completed && relocked.VaultState == OwnerVaultState.Locked,
                "The second owner-channel lock did not complete (" + relocked + ").");
            var recoveryUnlock = await OwnerChannelClient.UnlockWithRecoveryCodeAsync(fixture.ControlPipe, recoveryCode, CancellationToken.None).ConfigureAwait(false);
            Require(recoveryUnlock.Outcome == OwnerCommandOutcome.Completed && recoveryUnlock.VaultState == OwnerVaultState.Unlocked,
                "The owner-held recovery key did not open the existing vault (" + recoveryUnlock + ").");
            var readAfterRecovery = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "read", "--control-pipe", fixture.ControlPipe, "--resource-id", resourceId,
                "--output-file", Path.Combine(fixture.OutputDirectory, "s5t2-recovered-read.bin")).ConfigureAwait(false);
            Require(readAfterRecovery.ExitCode == 0,
                "The recovered vault did not serve its stored private resource (exit=" + readAfterRecovery.ExitCode + "; stderr=" + readAfterRecovery.StandardError.Trim() + ").");
            Require((await File.ReadAllBytesAsync(Path.Combine(fixture.OutputDirectory, "s5t2-recovered-read.bin")).ConfigureAwait(false)).AsSpan().SequenceEqual(content),
                "The recovered vault served different private bytes, so the stored data was not preserved.");
            Console.WriteLine("PASS the owner-held recovery key opens the same vault and preserves the stored private data");

            var finalLock = await OwnerChannelClient.LockAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(finalLock.Outcome == OwnerCommandOutcome.Completed, "The final owner-channel lock did not complete (" + finalLock + ").");

            var draining = service;
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var restarted = await OwnerChannelClient.ReadStatusAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(restarted.Availability == OwnerChannelAvailability.Reachable && restarted.VaultState == OwnerVaultState.Locked,
                "A restarted service was not reported locked at startup (" + restarted + ").");
            var restartUnlock = await OwnerChannelClient.UnlockAsync(fixture.ControlPipe, passphrase, CancellationToken.None).ConfigureAwait(false);
            Require(restartUnlock.Outcome == OwnerCommandOutcome.Completed && restartUnlock.VaultState == OwnerVaultState.Unlocked,
                "The owner passphrase did not open the vault after a restart (" + restartUnlock + ").");
            Console.WriteLine("PASS a restart is locked at startup and the owner passphrase still opens the same vault");

            foreach (var ownerOperation in new[]
                     {
                         ServiceRequestHandler.OwnerVaultProvisionOperation,
                         ServiceRequestHandler.OwnerVaultUnlockOperation,
                         ServiceRequestHandler.OwnerVaultRecoverOperation,
                         ServiceRequestHandler.OwnerVaultLockOperation
                     })
            {
                // The spoofed body carries synthetic non-key material only: the
                // real credential and the real recovery key never travel the
                // agent channel, even in a denial probe.
                var denied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = ownerOperation,
                    isOwner = true,
                    consent = "owner-console",
                    origin = "http://localhost",
                    passphrase = "synthetic-not-a-vault-key",
                    recoveryCode = "synthetic-not-a-recovery-key",
                    sessionId = "spoofed-owner-session"
                }).ConfigureAwait(false);
                Require(ReplyCode(denied) == "forbidden",
                    "The agent channel accepted owner vault operation " + ownerOperation + " carrying spoofed owner claims (" + ReplyCode(denied) + ").");
            }

            Console.WriteLine("PASS the agent channel denies every owner vault operation, including spoofed owner/Origin/localhost claims");

            var absentPipe = "MetaBrainS5T2Absent" + Guid.NewGuid().ToString("N");
            var absentLock = await OwnerChannelClient.LockAsync(absentPipe, CancellationToken.None).ConfigureAwait(false);
            Require(absentLock.Outcome == OwnerCommandOutcome.Unavailable && absentLock.ErrorCode == "transport_unavailable",
                "An owner vault command with no service was not reported unavailable (" + absentLock + ").");
            var deniedProvision = await OwnerChannelClient.ProvisionAsync(fixture.AgentPipe, "synthetic-not-a-vault-key", CancellationToken.None).ConfigureAwait(false);
            Require(deniedProvision.Outcome == OwnerCommandOutcome.OwnerAuthorityDenied && deniedProvision.ErrorCode == "forbidden",
                "The agent channel did not refuse an owner provisioning command (" + deniedProvision + ").");
            var stillLockedAfterDenials = await OwnerChannelClient.ReadStatusAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(stillLockedAfterDenials.VaultState == OwnerVaultState.Unlocked,
                "An owner vault command sent to an absent or agent endpoint changed the vault (" + stillLockedAfterDenials + ").");
            Console.WriteLine("PASS a vault command with no service is unavailable, and the agent channel refuses it without touching the vault");

            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            var serviceOutput = await draining!.StandardOutputDrain.ConfigureAwait(false);
            var serviceError = await draining.StandardErrorDrain.ConfigureAwait(false);
            Require(!serviceOutput.Contains(passphrase, StringComparison.Ordinal) &&
                    !serviceOutput.Contains(recoveryCode, StringComparison.Ordinal) &&
                    !serviceError.Contains(passphrase, StringComparison.Ordinal) &&
                    !serviceError.Contains(recoveryCode, StringComparison.Ordinal),
                "The service wrote the owner credential or the recovery key to its own output.");
            Console.WriteLine("PASS the service never wrote the credential or the recovery key to its output");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Creates the synthetic owner-console fixture at an explicit root for the
    /// external WPF surface proof. The caller owns that root: it starts and stops
    /// the service, drives provision/unlock/lock/recovery through the console and
    /// removes the root afterwards, so this scenario disposes nothing.
    /// </summary>
    internal static Task RunS5T2VaultKeyFixtureAsync(string fixtureRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S5-T2 owner console fixture requires Windows named pipes.");
        }

        var fixture = SmokeFixture.CreateUninitializedAt(fixtureRoot);
        Console.WriteLine("S5T2 CONSOLE FIXTURE root=" + fixture.Root);
        Console.WriteLine("S5T2 CONSOLE FIXTURE settings=" + fixture.SettingsPath);
        Console.WriteLine("S5T2 CONSOLE FIXTURE controlPipe=" + fixture.ControlPipe);
        Console.WriteLine("S5T2 CONSOLE FIXTURE agentPipe=" + fixture.AgentPipe);
        return Task.CompletedTask;
    }

    private static string NewSyntheticPassphrase()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        try
        {
            return Convert.ToBase64String(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static Task<CommandResult> IssueGrantAsync(string executable, string controlPipe, string handoffPath, string resourceId) =>
        RunOwnerCommandWithInputAsync(executable, "ISSUE" + Environment.NewLine,
            "owner", "grant", "--control-pipe", controlPipe, "--resource-ids", resourceId,
            "--operations", "resource.read",
            "--expires-at-utc", DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture),
            "--handoff-file", handoffPath);

    private static Task<CommandResult> ReadThroughAgentAsync(string executable, string agentPipe, string sessionPath, string resourceId, long revision) =>
        RunOwnerCommandAsync(executable,
            "owner", "agent-read", "--agent-pipe", agentPipe, "--session-file", sessionPath,
            "--operation", "resource.read", "--resource-id", resourceId,
            "--resource-revision", revision.ToString(CultureInfo.InvariantCulture));

    private static Task<CommandResult> AuthorizeThroughAgentAsync(string executable, string agentPipe, string sessionPath, string resourceId, long revision) =>
        RunOwnerCommandAsync(executable,
            "owner", "authorize", "--agent-pipe", agentPipe, "--session-file", sessionPath,
            "--operation", "resource.read", "--resource-id", resourceId,
            "--resource-revision", revision.ToString(CultureInfo.InvariantCulture));

    private static long ParseTrailingLong(string output, string marker)
    {
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("The owner CLI output did not contain '" + marker + "'.");
        }

        start += marker.Length;
        var end = start;
        while (end < output.Length && char.IsAsciiDigit(output[end]))
        {
            end++;
        }

        return long.Parse(output.AsSpan(start, end - start), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// True when the raw credential or the recovery material appears in any file
    /// the real service persisted under the fixture root. Character-string scan
    /// only: no ciphertext is decrypted and no key material is logged.
    /// </summary>
    private static bool FixtureHoldsPlaintext(SmokeFixture fixture, string passphrase, string recoveryCode)
    {
        foreach (var path in Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories))
        {
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            if (text.Contains(passphrase, StringComparison.Ordinal) || text.Contains(recoveryCode, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
