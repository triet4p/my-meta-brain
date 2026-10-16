using System.Security.Cryptography;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;

namespace MetaBrain.S1T4.Smoke;

// S5-T1: the owner application boundary on the real surfaces. The new
// owner-channel client is the only way the WPF owner console reaches the
// service, so this scenario drives it against the real service and the real
// owner CLI: reachable/uninitialized -> provisioned/unlocked -> locked ->
// unavailable (absent pipe, stopped service) -> denied on the agent channel ->
// locked again at startup after a restart, and never a stale unlocked state
// behind an unavailable one. The same run drives the real agent pipe and the
// real MCP bridge to prove the application boundary: an agent-channel session
// cannot call owner operations, and body, Origin or localhost owner claims do
// not become owner consent, in a locked or an unlocked vault.
internal static partial class OwnerServiceSmoke
{
    internal static async Task RunS5T1OwnerUiAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S5-T1 owner application boundary smoke requires Windows named pipes.");
        }

        Console.WriteLine("RUN S5-T1 owner application boundary: owner-channel status contract and agent-channel owner-operation denial");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);

            var uninitialized = await OwnerChannelClient.ReadStatusAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(uninitialized.Availability == OwnerChannelAvailability.Reachable &&
                    uninitialized.VaultState == OwnerVaultState.Uninitialized,
                "The owner-channel client did not report the real uninitialized vault state (" + uninitialized + ").");
            Console.WriteLine("PASS owner-channel client reports reachable/uninitialized for a service with no vault");

            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "Provisioning the synthetic S5-T1 vault failed (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");

            var unlocked = await OwnerChannelClient.ReadStatusAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(unlocked.Availability == OwnerChannelAvailability.Reachable && unlocked.VaultState == OwnerVaultState.Unlocked,
                "The owner-channel client did not follow the real unlocked state after provisioning (" + unlocked + ").");
            Console.WriteLine("PASS owner-channel client follows the real unlocked state after provisioning");

            var lockedByOwner = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockedByOwner.ExitCode == 0 && lockedByOwner.StandardOutput.Contains("VAULT locked", StringComparison.Ordinal),
                "The real owner lock did not run (exit=" + lockedByOwner.ExitCode + "; stderr=" + lockedByOwner.StandardError.Trim() + ").");

            var locked = await OwnerChannelClient.ReadStatusAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(locked.Availability == OwnerChannelAvailability.Reachable && locked.VaultState == OwnerVaultState.Locked,
                "The owner-channel client did not report locked after the real owner lock (" + locked + ").");
            Console.WriteLine("PASS owner-channel client reports reachable/locked after the real owner lock");

            var absentPipe = "MetaBrainS5T1Absent" + Guid.NewGuid().ToString("N");
            var absent = await OwnerChannelClient.ReadStatusAsync(absentPipe, CancellationToken.None).ConfigureAwait(false);
            Require(absent.Availability == OwnerChannelAvailability.ServiceUnavailable && absent.ErrorCode == "transport_unavailable",
                "A pipe with no service was not reported unavailable (" + absent + ").");
            Console.WriteLine("PASS an absent owner pipe is reported unavailable, never as a vault state");

            var agentChannel = await OwnerChannelClient.ReadStatusAsync(fixture.AgentPipe, CancellationToken.None).ConfigureAwait(false);
            Require(agentChannel.Availability == OwnerChannelAvailability.OwnerAuthorityDenied && agentChannel.ErrorCode == "forbidden",
                "The agent channel did not refuse the owner status operation (" + agentChannel + ").");
            Console.WriteLine("PASS the agent channel refuses the owner status operation with forbidden");

            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            var stopped = await OwnerChannelClient.ReadStatusAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(stopped.Availability == OwnerChannelAvailability.ServiceUnavailable && stopped.ErrorCode == "transport_unavailable",
                "A stopped service was not reported unavailable (" + stopped + ").");
            Require(stopped.VaultState == OwnerVaultState.Unknown,
                "A stopped service left a stale vault state behind (" + stopped + ").");
            Console.WriteLine("PASS a stopped service is reported unavailable with no stale unlocked state");

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var restarted = await OwnerChannelClient.ReadStatusAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(restarted.Availability == OwnerChannelAvailability.Reachable && restarted.VaultState == OwnerVaultState.Locked,
                "A restarted service was not reported locked at startup (" + restarted + ").");
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false)).Contains("vault=locked", StringComparison.Ordinal),
                "The owner CLI and the owner-channel client disagreed about the restarted vault state.");
            Console.WriteLine("PASS service restart is reported locked at startup and agrees with the real owner CLI");

            foreach (var ownerOnlyOperation in new[]
                     {
                         ServiceRequestHandler.OwnerStatusOperation,
                         ServiceRequestHandler.OwnerVaultUnlockOperation, ServiceRequestHandler.OwnerVaultProvisionOperation,
                         ServiceRequestHandler.OwnerVaultLockOperation, ServiceRequestHandler.OwnerVaultRecoverOperation,
                         ServiceRequestHandler.OwnerLibrarySearchOperation,
                         ServiceRequestHandler.OwnerProposalPublishOperation, ServiceRequestHandler.OwnerProposalRejectOperation,
                         "owner.sessions", "owner.session.revoke", "owner.catalog.publish", "owner.catalog.list",
                         "owner.request.approve", "owner.access.requests", "owner.scope.list", "resource.read", "resource.write"
                     })
            {
                var denied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = ownerOnlyOperation,
                    isOwner = true,
                    consent = "owner-console",
                    origin = "http://localhost",
                    localSid = "S-1-5-21-100-200-300-4000",
                    cwd = "C:/synthetic/untrusted-working-directory",
                    passphrase = "synthetic-not-a-vault-key",
                    sessionId = "spoofed-owner-session",
                    previewId = "spoofed-preview",
                    resourceIds = new[] { SmokeFixture.ResourceB },
                    operations = new[] { "provider.egress" },
                    destinationResourceIds = new[] { SmokeFixture.ResourceB },
                    expiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
                    provider = "spoofed-provider",
                    model = "spoofed-model",
                    maximumCostUsd = 1000m
                }).ConfigureAwait(false);
                Require(ReplyCode(denied) == "forbidden",
                    "The agent channel accepted owner operation " + ownerOnlyOperation +
                    " carrying spoofed owner/consent/Origin claims (" + ReplyCode(denied) + ").");
            }

            Console.WriteLine("PASS the agent channel denies every owner operation, including spoofed owner/consent/Origin/localhost claims");

            var lockedInspect = await InvokeAgentRequestAsync(fixture.AgentPipe, SpoofedAgentSessionInspect()).ConfigureAwait(false);
            Require(ReplyCode(lockedInspect) == "vault_locked",
                "A locked vault did not refuse an agent operation carrying owner-only fields (" + ReplyCode(lockedInspect) + ").");
            Console.WriteLine("PASS a locked vault refuses an agent operation carrying owner-only fields and spoofed owner claims");

            var unlockedByOwner = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlockedByOwner.ExitCode == 0 && unlockedByOwner.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The real owner unlock did not run (exit=" + unlockedByOwner.ExitCode + "; stderr=" + unlockedByOwner.StandardError.Trim() + ").");

            var unlockedAgain = await OwnerChannelClient.ReadStatusAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(unlockedAgain.Availability == OwnerChannelAvailability.Reachable && unlockedAgain.VaultState == OwnerVaultState.Unlocked,
                "The owner-channel client did not report unlocked after the real owner unlock (" + unlockedAgain + ").");
            Console.WriteLine("PASS owner-channel client echoes the real owner unlock (locked -> unlocked)");

            var unlockedInspect = await InvokeAgentRequestAsync(fixture.AgentPipe, SpoofedAgentSessionInspect()).ConfigureAwait(false);
            Require(ReplyCode(unlockedInspect) == "forbidden",
                "An agent operation carrying owner-only fields and spoofed owner claims was not refused while the vault was unlocked (" +
                ReplyCode(unlockedInspect) + ").");
            Console.WriteLine("PASS an agent operation carrying owner-only fields and spoofed owner claims is refused while the vault is unlocked");

            var unlockedRead = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.memory.read",
                sessionToken = "mbs1_synthetic-not-a-real-bearer",
                accessOperation = "memory.read",
                resourceId = "s5t1-spoofed-resource",
                resourceRevision = 1,
                isOwner = true,
                consent = "owner-console",
                origin = "http://localhost",
                passphrase = "synthetic-not-a-vault-key"
            }).ConfigureAwait(false);
            Require(ReplyCode(unlockedRead) == "denied",
                "An agent read with owner-only fields and spoofed owner claims was not denied (" + ReplyCode(unlockedRead) + ").");
            Require(!unlockedRead.TryGetProperty("contentBase64", out var unlockedReadContent) ||
                    unlockedReadContent.ValueKind != JsonValueKind.String,
                "An agent read with owner-only fields and spoofed owner claims returned content.");
            Console.WriteLine("PASS an agent read with owner-only fields and spoofed owner claims denies without content");

            var relocked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(relocked.ExitCode == 0 && relocked.StandardOutput.Contains("VAULT locked", StringComparison.Ordinal),
                "The real owner re-lock did not run (exit=" + relocked.ExitCode + ").");

            var bridgeTools = await RunBridgeToolsListAsync(connectionsExecutable, fixture.AgentPipe).ConfigureAwait(false);
            foreach (var ownerTool in new[]
                     {
                         "metabrain_owner_status", "metabrain_owner_unlock", "metabrain_owner_lock",
                         "metabrain_catalog_publish", "metabrain_request_approve", "metabrain_session_revoke"
                     })
            {
                Require(!bridgeTools.Contains(ownerTool, StringComparison.Ordinal),
                    "The agent MCP surface advertised an owner tool: " + ownerTool);
            }

            Console.WriteLine("PASS the agent MCP surface advertises no owner operation");

            var ownerToolCall = await RunBridgeRawAsync(connectionsExecutable, fixture.AgentPipe,
                """{"jsonrpc":"2.0","id":77,"method":"tools/call","params":{"name":"metabrain_owner_unlock","arguments":{"isOwner":"true","consent":"owner-console","origin":"http://localhost"}}}""").ConfigureAwait(false);
            Require(ownerToolCall.Contains("bridge_unknown_tool", StringComparison.Ordinal),
                "The agent MCP bridge did not refuse an owner tool name.");
            Console.WriteLine("PASS the agent MCP bridge refuses owner tool names with bridge_unknown_tool");

            var stillLocked = await OwnerChannelClient.ReadStatusAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(stillLocked.Availability == OwnerChannelAvailability.Reachable && stillLocked.VaultState == OwnerVaultState.Locked,
                "Denied agent-channel claims changed the owner vault state (" + stillLocked + ").");
            Console.WriteLine("PASS the owner channel still reports locked after every agent-channel denial");
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
    /// external WPF surface proof. The caller owns that root: it starts and
    /// stops the service, provisions/unlocks/locks through the owner CLI and
    /// removes the root afterwards, so this scenario disposes nothing.
    /// </summary>
    internal static Task RunS5T1OwnerUiFixtureAsync(string fixtureRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S5-T1 owner console fixture requires Windows named pipes.");
        }

        var fixture = SmokeFixture.CreateUninitializedAt(fixtureRoot);
        Console.WriteLine("S5T1 CONSOLE FIXTURE root=" + fixture.Root);
        Console.WriteLine("S5T1 CONSOLE FIXTURE settings=" + fixture.SettingsPath);
        Console.WriteLine("S5T1 CONSOLE FIXTURE controlPipe=" + fixture.ControlPipe);
        Console.WriteLine("S5T1 CONSOLE FIXTURE agentPipe=" + fixture.AgentPipe);
        return Task.CompletedTask;
    }

    private static object SpoofedAgentSessionInspect() => new
    {
        protocolVersion = 1,
        operation = "agent.session.inspect",
        sessionToken = "mbs1_synthetic-not-a-real-bearer",
        isOwner = true,
        consent = "owner-console",
        origin = "http://localhost",
        sessionId = "spoofed-owner-session",
        passphrase = "synthetic-not-a-vault-key"
    };
}
