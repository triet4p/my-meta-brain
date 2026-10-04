using System.Globalization;
using System.Security.Cryptography;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private sealed record T5Grant(string GrantId, string HandoffPath);
    private sealed record T5Session(string SessionId, string SessionFile);

    internal static async Task RunT5SessionLifecycleAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S1-T5 lifecycle smoke requires Windows named pipes and DPAPI.");
        }

        using var fixture = SmokeFixture.Create();
        RunningService? service = null;
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);
            var migration = await RunOwnerCommandWithInputAsync(
                connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "migrate", "--config", fixture.SettingsPath).ConfigureAwait(false);
            Require(migration.ExitCode == 0 &&
                    migration.StandardOutput.Contains("MIGRATION encrypted resources=2", StringComparison.Ordinal),
                "The real owner CLI did not initialize the synthetic lifecycle fixture.");

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var unlocked = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The owner could not unlock the isolated lifecycle fixture.");

            await WriteFixtureResourceAsync(connectionsExecutable, fixture, SmokeFixture.ResourceA,
                "synthetic resource A revision two for exact access authorization.", expectedRevision: 2).ConfigureAwait(false);
            var collection = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, "SET" + Environment.NewLine,
                "owner", "collection-set", "--control-pipe", fixture.ControlPipe,
                "--collection-id", SmokeFixture.CollectionId,
                "--resource-ids", SmokeFixture.ResourceA + "," + SmokeFixture.ResourceB).ConfigureAwait(false);
            Require(collection.ExitCode == 0 && collection.StandardOutput.Contains("COLLECTION set", StringComparison.Ordinal),
                "The owner could not establish the synthetic source collection.");

            async Task<T5Grant> IssueGrantAsync(string name, DateTimeOffset expiresAtUtc, params string[] grantOptions)
            {
                var handoff = Path.Combine(fixture.OutputDirectory, name + ".grant-handoff");
                var arguments = new List<string>
                {
                    "owner", "grant", "--control-pipe", fixture.ControlPipe
                };
                arguments.AddRange(grantOptions);
                arguments.Add("--expires-at-utc");
                arguments.Add(expiresAtUtc.ToString("O", CultureInfo.InvariantCulture));
                arguments.Add("--handoff-file");
                arguments.Add(handoff);
                var result = await RunOwnerCommandWithInputAsync(
                    connectionsExecutable, "ISSUE" + Environment.NewLine, arguments.ToArray()).ConfigureAwait(false);
                Require(result.ExitCode == 0,
                    "The owner CLI did not approve a synthetic lifecycle scope grant: " + result.StandardError.Trim());
                VerifyOwnerHandoffAcl(handoff);
                return new T5Grant(ParseIssuedGrantId(result.StandardOutput), handoff);
            }

            async Task<T5Session> RedeemAsync(T5Grant grant, string name)
            {
                var sessionFile = Path.Combine(fixture.OutputDirectory, name + ".session-handoff");
                var result = await RunOwnerCommandAsync(
                    connectionsExecutable,
                    "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                    "--handoff-file", grant.HandoffPath, "--session-file", sessionFile).ConfigureAwait(false);
                Require(result.ExitCode == 0 && result.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
                    "A fresh owner-approved grant did not redeem over the real agent pipe (exit=" +
                    result.ExitCode + "; stderr=" + result.StandardError.Trim() + ").");
                VerifyOwnerHandoffAcl(sessionFile);
                return new T5Session(ParseSessionId(result.StandardOutput), sessionFile);
            }

            async Task<CommandResult> AuthorizeAsync(
                T5Session session,
                string operation,
                string? resourceId = null,
                long? resourceRevision = null,
                string? destinationResourceId = null,
                long? destinationRevision = null,
                string? provider = null,
                string? model = null,
                decimal? estimatedCostUsd = null,
                long? inputTokens = null,
                long? outputTokens = null)
            {
                var arguments = new List<string>
                {
                    "owner", "authorize", "--agent-pipe", fixture.AgentPipe,
                    "--session-file", session.SessionFile, "--operation", operation
                };
                void Add(string option, string? value)
                {
                    if (value is not null)
                    {
                        arguments.Add(option);
                        arguments.Add(value);
                    }
                }

                Add("--resource-id", resourceId);
                Add("--resource-revision", resourceRevision?.ToString(CultureInfo.InvariantCulture));
                Add("--destination-resource-id", destinationResourceId);
                Add("--destination-revision", destinationRevision?.ToString(CultureInfo.InvariantCulture));
                Add("--provider", provider);
                Add("--model", model);
                Add("--estimated-cost-usd", estimatedCostUsd?.ToString("0.###", CultureInfo.InvariantCulture));
                Add("--input-tokens", inputTokens?.ToString(CultureInfo.InvariantCulture));
                Add("--output-tokens", outputTokens?.ToString(CultureInfo.InvariantCulture));
                return await RunOwnerCommandAsync(connectionsExecutable, arguments.ToArray()).ConfigureAwait(false);
            }

            static void RequireAllowed(CommandResult result, string label)
            {
                Require(result.ExitCode == 0 && result.StandardOutput.Contains("ALLOW operation=", StringComparison.Ordinal),
                    "The expected scoped agent authorization was denied: " + label + ".");
            }

            static void RequireDenied(CommandResult result, string expected, string label)
            {
                Require(result.ExitCode == 3 &&
                        (result.StandardError.Contains(expected, StringComparison.Ordinal) ||
                         result.StandardOutput.Contains(expected, StringComparison.Ordinal)),
                    "The expected lifecycle denial was not observed for " + label + " (expected " + expected + ").");
            }

            async Task<T5Grant> IssueAndCheckGrantAsync(string name, params string[] grantOptions) =>
                await IssueGrantAsync(name, DateTimeOffset.UtcNow.AddMinutes(20), grantOptions).ConfigureAwait(false);

            var readGrant = await IssueAndCheckGrantAsync("read-a",
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            var proposalGrant = await IssueAndCheckGrantAsync("proposal-b",
                "--resource-ids", SmokeFixture.ResourceB, "--operations", "proposal.create",
                "--destination-resource-ids", SmokeFixture.ResourceA).ConfigureAwait(false);
            var linkGrant = await IssueAndCheckGrantAsync("link-a-b",
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "link.create",
                "--destination-resource-ids", SmokeFixture.ResourceB).ConfigureAwait(false);
            var readSession = await RedeemAsync(readGrant, "read-a").ConfigureAwait(false);
            var proposalSession = await RedeemAsync(proposalGrant, "proposal-b").ConfigureAwait(false);
            var linkSession = await RedeemAsync(linkGrant, "link-a-b").ConfigureAwait(false);

            var listed = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "sessions", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(listed.ExitCode == 0 && listed.StandardOutput.Contains("SESSIONS count=3", StringComparison.Ordinal) &&
                    listed.StandardOutput.Contains("grant=" + readGrant.GrantId, StringComparison.Ordinal) &&
                    listed.StandardOutput.Contains("grant=" + proposalGrant.GrantId, StringComparison.Ordinal) &&
                    listed.StandardOutput.Contains("grant=" + linkGrant.GrantId, StringComparison.Ordinal) &&
                    listed.StandardOutput.Contains("operations=resource.read", StringComparison.Ordinal) &&
                    listed.StandardOutput.Contains("operations=proposal.create", StringComparison.Ordinal) &&
                    listed.StandardOutput.Contains("operations=link.create", StringComparison.Ordinal) &&
                    listed.StandardOutput.Contains("source resource=" + SmokeFixture.ResourceA + " zone=" + SmokeFixture.Zone + " revision=2", StringComparison.Ordinal) &&
                    listed.StandardOutput.Contains("destination resource=" + SmokeFixture.ResourceA + " zone=" + SmokeFixture.Zone + " revision=2", StringComparison.Ordinal) &&
                    listed.StandardOutput.Contains("destination resource=" + SmokeFixture.ResourceB + " zone=" + SmokeFixture.Zone + " revision=1", StringComparison.Ordinal),
                "Owner session listing did not expose the separate immutable operation/resource/revision snapshots.");

            RequireAllowed(await AuthorizeAsync(readSession, "resource.read", SmokeFixture.ResourceA, 2).ConfigureAwait(false), "exact resource A revision 2");
            RequireDenied(await AuthorizeAsync(readSession, "resource.read", SmokeFixture.ResourceA, 1).ConfigureAwait(false),
                "resource_unavailable", "stale resource A revision");
            RequireDenied(await AuthorizeAsync(readSession, "resource.read", SmokeFixture.ResourceB, 1).ConfigureAwait(false),
                "resource_unavailable", "resource outside the read grant");
            RequireDenied(await AuthorizeAsync(readSession, "proposal.create", destinationResourceId: SmokeFixture.ResourceA,
                destinationRevision: 2).ConfigureAwait(false), "resource_unavailable", "ungranted operation");
            RequireAllowed(await AuthorizeAsync(proposalSession, "proposal.create",
                destinationResourceId: SmokeFixture.ResourceA, destinationRevision: 2).ConfigureAwait(false), "exact proposal destination");
            RequireDenied(await AuthorizeAsync(proposalSession, "proposal.create",
                destinationResourceId: SmokeFixture.ResourceA, destinationRevision: 1).ConfigureAwait(false),
                "resource_unavailable", "stale proposal destination revision");
            RequireAllowed(await AuthorizeAsync(linkSession, "link.create", SmokeFixture.ResourceA, 2,
                SmokeFixture.ResourceB, 1).ConfigureAwait(false), "exact link source and destination");
            RequireDenied(await AuthorizeAsync(linkSession, "link.create", SmokeFixture.ResourceA, 1,
                SmokeFixture.ResourceB, 1).ConfigureAwait(false), "resource_unavailable", "stale link source revision");
            RequireDenied(await AuthorizeAsync(linkSession, "link.create", SmokeFixture.ResourceA, 2,
                SmokeFixture.ResourceB, 2).ConfigureAwait(false), "resource_unavailable", "stale link destination revision");

            var revoked = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "revoke-session", "--control-pipe", fixture.ControlPipe,
                "--session-id", readSession.SessionId).ConfigureAwait(false);
            Require(revoked.ExitCode == 0 && revoked.StandardOutput.Contains("SESSION revoked", StringComparison.Ordinal),
                "Owner-only targeted session revocation did not complete.");
            RequireDenied(await AuthorizeAsync(readSession, "resource.read", SmokeFixture.ResourceA, 2).ConfigureAwait(false),
                "session_revoked", "revoked session reuse");
            RequireAllowed(await AuthorizeAsync(proposalSession, "proposal.create",
                destinationResourceId: SmokeFixture.ResourceA, destinationRevision: 2).ConfigureAwait(false),
                "unrevoked sibling session");

            var agentRevokeAttempt = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "owner.session.revoke",
                sessionId = proposalSession.SessionId,
                isOwner = true
            }).ConfigureAwait(false);
            var ownerAuthorizeAttempt = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "agent.access.authorize",
                sessionToken = "mbs1_synthetic-untrusted-token",
                accessOperation = "resource.read",
                resourceId = SmokeFixture.ResourceA,
                resourceRevision = 2
            }).ConfigureAwait(false);
            Require(ReplyCode(agentRevokeAttempt) == "forbidden" && ReplyCode(ownerAuthorizeAttempt) == "forbidden",
                "The owner and agent IPC channels did not reject each other's session lifecycle operations.");

            var egressGrant = await IssueAndCheckGrantAsync("egress-bounded",
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "provider.egress",
                "--provider", "synthetic-provider", "--model", "synthetic-model", "--max-cost-usd", "0.25").ConfigureAwait(false);
            var egressSession = await RedeemAsync(egressGrant, "egress-bounded").ConfigureAwait(false);
            RequireDenied(await AuthorizeAsync(egressSession, "provider.egress", provider: "synthetic-provider",
                model: "synthetic-model", estimatedCostUsd: 0.10m).ConfigureAwait(false),
                "egress_budget_unknown", "missing token-budget estimate");
            RequireDenied(await AuthorizeAsync(egressSession, "provider.egress", provider: "synthetic-provider",
                model: "synthetic-model", estimatedCostUsd: 0.25m, inputTokens: 10_000, outputTokens: 2_000).ConfigureAwait(false),
                "untrusted_egress_context", "in-cap request without a trusted provider context");
            RequireDenied(await AuthorizeAsync(egressSession, "provider.egress", provider: "synthetic-provider",
                model: "other-model", estimatedCostUsd: 0.10m, inputTokens: 1, outputTokens: 1).ConfigureAwait(false),
                "egress_not_granted", "provider/model mismatch");
            RequireDenied(await AuthorizeAsync(egressSession, "provider.egress", provider: "synthetic-provider",
                model: "synthetic-model", estimatedCostUsd: 0.251m, inputTokens: 1, outputTokens: 1).ConfigureAwait(false),
                "egress_cost_exceeded", "per-job cost ceiling");
            RequireDenied(await AuthorizeAsync(egressSession, "provider.egress", provider: "synthetic-provider",
                model: "synthetic-model", estimatedCostUsd: 0.10m, inputTokens: 10_001, outputTokens: 1).ConfigureAwait(false),
                "egress_budget_exceeded", "input-token ceiling");
            RequireDenied(await AuthorizeAsync(egressSession, "provider.egress", provider: "synthetic-provider",
                model: "synthetic-model", estimatedCostUsd: 0.10m, inputTokens: 1, outputTokens: 2_001).ConfigureAwait(false),
                "egress_budget_exceeded", "output-token ceiling");
            var overCapHandoff = Path.Combine(fixture.OutputDirectory, "egress-over-cap.grant-handoff");
            var overCapGrant = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, "ISSUE" + Environment.NewLine,
                "owner", "grant", "--control-pipe", fixture.ControlPipe,
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "provider.egress",
                "--provider", "synthetic-provider", "--model", "synthetic-model", "--max-cost-usd", "0.251",
                "--expires-at-utc", DateTimeOffset.UtcNow.AddMinutes(20).ToString("O", CultureInfo.InvariantCulture),
                "--handoff-file", overCapHandoff).ConfigureAwait(false);
            Require(overCapGrant.ExitCode == 3 && overCapGrant.StandardError.Contains("invalid_grant_request", StringComparison.Ordinal) &&
                    !File.Exists(overCapHandoff),
                "The owner granted an egress job cap above the frozen $0.25 limit.");

            var unknownBytes = RandomNumberGenerator.GetBytes(32);
            var unknownSessionToken = "mbs1_" + Convert.ToBase64String(unknownBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            CryptographicOperations.ZeroMemory(unknownBytes);
            var unknownSession = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.access.authorize",
                sessionToken = unknownSessionToken,
                accessOperation = "resource.read",
                resourceId = SmokeFixture.ResourceA,
                resourceRevision = 2
            }).ConfigureAwait(false);
            Require(ReplyCode(unknownSession) == "authorization_decision" &&
                    unknownSession.GetProperty("decision").GetProperty("reason").GetString() == "session_unknown",
                "A stale/unknown session bearer did not fail closed at agent access authorization.");

            var stalePendingGrant = await IssueAndCheckGrantAsync("policy-stale-pending",
                "--resource-ids", SmokeFixture.ResourceB, "--operations", "resource.read").ConfigureAwait(false);
            var generationChange = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, "SET" + Environment.NewLine,
                "owner", "collection-set", "--control-pipe", fixture.ControlPipe,
                "--collection-id", SmokeFixture.CollectionId,
                "--resource-ids", SmokeFixture.ResourceA).ConfigureAwait(false);
            Require(generationChange.ExitCode == 0 && generationChange.StandardOutput.Contains("COLLECTION set", StringComparison.Ordinal),
                "The owner could not advance the durable policy generation for the revocation check.");
            RequireDenied(await AuthorizeAsync(proposalSession, "proposal.create",
                destinationResourceId: SmokeFixture.ResourceA, destinationRevision: 2).ConfigureAwait(false),
                "session_revoked", "session invalidated by policy generation change");
            var staleRedemption = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                "--handoff-file", stalePendingGrant.HandoffPath,
                "--session-file", Path.Combine(fixture.OutputDirectory, "policy-stale-pending.session-handoff")).ConfigureAwait(false);
            RequireDenied(staleRedemption, "token_revoked", "pending grant invalidated by policy generation change");

            var expiry = DateTimeOffset.UtcNow.AddSeconds(7);
            var expiringGrant = await IssueGrantAsync("expiring", expiry,
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            var expiringSession = await RedeemAsync(expiringGrant, "expiring").ConfigureAwait(false);
            RequireAllowed(await AuthorizeAsync(expiringSession, "resource.read", SmokeFixture.ResourceA, 2).ConfigureAwait(false),
                "session before expiry");
            var expiryDelay = expiry - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(150);
            if (expiryDelay > TimeSpan.Zero)
            {
                await Task.Delay(expiryDelay).ConfigureAwait(false);
            }

            RequireDenied(await AuthorizeAsync(expiringSession, "resource.read", SmokeFixture.ResourceA, 2).ConfigureAwait(false),
                "session_expired", "session after immutable grant expiry");

            var beforeLockGrant = await IssueAndCheckGrantAsync("before-lock",
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            var pendingLockGrant = await IssueAndCheckGrantAsync("pending-lock",
                "--resource-ids", SmokeFixture.ResourceB, "--operations", "resource.read").ConfigureAwait(false);
            var beforeLockSession = await RedeemAsync(beforeLockGrant, "before-lock").ConfigureAwait(false);
            RequireAllowed(await AuthorizeAsync(beforeLockSession, "resource.read", SmokeFixture.ResourceA, 2).ConfigureAwait(false),
                "session before owner lock");
            var locked = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0 && locked.StandardOutput.Contains("VAULT locked", StringComparison.Ordinal),
                "The owner did not lock the vault before testing lifecycle invalidation.");
            RequireDenied(await AuthorizeAsync(beforeLockSession, "resource.read", SmokeFixture.ResourceA, 2).ConfigureAwait(false),
                "vault_locked", "authorization while locked");
            var pendingWhileLocked = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                "--handoff-file", pendingLockGrant.HandoffPath,
                "--session-file", Path.Combine(fixture.OutputDirectory, "pending-lock.session-handoff")).ConfigureAwait(false);
            RequireDenied(pendingWhileLocked, "vault_locked", "redemption while locked");
            var unlockAfterLock = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlockAfterLock.ExitCode == 0, "The owner could not unlock after lock-boundary checks.");
            RequireDenied(await AuthorizeAsync(beforeLockSession, "resource.read", SmokeFixture.ResourceA, 2).ConfigureAwait(false),
                "session_unknown", "process-local session cleared by lock");
            var supersededLockToken = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                "--handoff-file", pendingLockGrant.HandoffPath,
                "--session-file", Path.Combine(fixture.OutputDirectory, "pending-lock-after-unlock.session-handoff")).ConfigureAwait(false);
            RequireDenied(supersededLockToken, "token_superseded", "pre-lock grant after next unlock epoch");

            var persistenceGrant = await IssueAndCheckGrantAsync("persistence-live",
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            var persistenceSession = await RedeemAsync(persistenceGrant, "persistence-live").ConfigureAwait(false);
            var persistencePending = await IssueAndCheckGrantAsync("persistence-pending",
                "--resource-ids", SmokeFixture.ResourceB, "--operations", "resource.read").ConfigureAwait(false);
            var scopeStatePath = Path.Combine(fixture.VaultDirectoryPath, "scope-grants.enc");
            using (new FileStream(scopeStatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                RequireDenied(await AuthorizeAsync(persistenceSession, "resource.read", SmokeFixture.ResourceA, 2).ConfigureAwait(false),
                    "authorization_unavailable", "scope-state read failure");
                var failedConsume = await RunOwnerCommandAsync(
                    connectionsExecutable, "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                    "--handoff-file", persistencePending.HandoffPath,
                    "--session-file", Path.Combine(fixture.OutputDirectory, "persistence-failed.session-handoff")).ConfigureAwait(false);
                RequireDenied(failedConsume, "token_unavailable", "durable grant consumption failure");
                Require(!File.Exists(Path.Combine(fixture.OutputDirectory, "persistence-failed.session-handoff")),
                    "A session bearer was disclosed even though durable token consumption failed.");
            }

            RequireAllowed(await AuthorizeAsync(persistenceSession, "resource.read", SmokeFixture.ResourceA, 2).ConfigureAwait(false),
                "session authorization after temporary persistence failure");
            var lockBeforeUnlockFailure = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockBeforeUnlockFailure.ExitCode == 0, "The owner could not lock before the durable unlock-failure check.");
            var unlockFailureObserved = false;
            using (new FileStream(scopeStatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var failedUnlock = await RunOwnerCommandWithInputAsync(
                    connectionsExecutable, passphrase + Environment.NewLine,
                    "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
                var status = await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false);
                unlockFailureObserved = failedUnlock.ExitCode == 3 && status.Contains("vault=locked", StringComparison.Ordinal);
            }

            Require(unlockFailureObserved, "A failed durable owner unlock left the vault unlocked or reported success.");
            var unlockAfterFailure = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlockAfterFailure.ExitCode == 0, "The owner could not unlock after releasing the synthetic exclusive file handle.");
            RequireDenied(await AuthorizeAsync(persistenceSession, "resource.read", SmokeFixture.ResourceA, 2).ConfigureAwait(false),
                "session_unknown", "session cleared by the second owner lock");
            var persistenceOldToken = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                "--handoff-file", persistencePending.HandoffPath,
                "--session-file", Path.Combine(fixture.OutputDirectory, "persistence-old-epoch.session-handoff")).ConfigureAwait(false);
            RequireDenied(persistenceOldToken, "token_superseded", "pending grant after failed-then-successful unlock epoch");

            var restartGrant = await IssueAndCheckGrantAsync("restart-consumed",
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            var restartSession = await RedeemAsync(restartGrant, "restart-consumed").ConfigureAwait(false);
            var restartPending = await IssueAndCheckGrantAsync("restart-pending",
                "--resource-ids", SmokeFixture.ResourceB, "--operations", "resource.read").ConfigureAwait(false);
            RequireAllowed(await AuthorizeAsync(restartSession, "resource.read", SmokeFixture.ResourceA, 2).ConfigureAwait(false),
                "session before service restart");
            var usedReplay = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                "--handoff-file", restartGrant.HandoffPath,
                "--session-file", Path.Combine(fixture.OutputDirectory, "restart-replay.session-handoff")).ConfigureAwait(false);
            RequireDenied(usedReplay, "token_used", "durably consumed grant replay before restart");

            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false))
                    .Contains("vault=locked", StringComparison.Ordinal),
                "The service did not restart with its vault locked.");
            RequireDenied(await AuthorizeAsync(restartSession, "resource.read", SmokeFixture.ResourceA, 2).ConfigureAwait(false),
                "vault_locked", "authorization against restarted locked service");
            var lockedRestartRedeem = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                "--handoff-file", restartPending.HandoffPath,
                "--session-file", Path.Combine(fixture.OutputDirectory, "restart-pending-locked.session-handoff")).ConfigureAwait(false);
            RequireDenied(lockedRestartRedeem, "vault_locked", "pending token redemption against restarted locked service");
            var restartUnlock = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(restartUnlock.ExitCode == 0, "The owner could not unlock the restarted fixture service.");
            RequireDenied(await AuthorizeAsync(restartSession, "resource.read", SmokeFixture.ResourceA, 2).ConfigureAwait(false),
                "session_unknown", "pre-restart memory-only session");
            var restartOldEpochToken = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                "--handoff-file", restartPending.HandoffPath,
                "--session-file", Path.Combine(fixture.OutputDirectory, "restart-pending-old-epoch.session-handoff")).ConfigureAwait(false);
            RequireDenied(restartOldEpochToken, "token_superseded", "pre-restart pending grant");
            var restartReplayAfterUnlock = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                "--handoff-file", restartGrant.HandoffPath,
                "--session-file", Path.Combine(fixture.OutputDirectory, "restart-replay-after-unlock.session-handoff")).ConfigureAwait(false);
            RequireDenied(restartReplayAfterUnlock, "token_used", "consumed grant replay after restart");

            var afterRestartGrant = await IssueAndCheckGrantAsync("after-restart",
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            var afterRestartSession = await RedeemAsync(afterRestartGrant, "after-restart").ConfigureAwait(false);
            RequireAllowed(await AuthorizeAsync(afterRestartSession, "resource.read", SmokeFixture.ResourceA, 2).ConfigureAwait(false),
                "fresh owner-approved session after restart");

            Console.WriteLine("PASS S1-T5 real named-pipe lifecycle: owner session list/revoke, exact operation/resource/revision authorization, expiry/generation/lock/restart invalidation, owner/agent channel separation, durable failure denial, and frozen egress ceilings");
            Console.WriteLine("LIMIT egress requests are authorization-only synthetic IPC; no provider adapter, managed-resource retrieval/decryption, provider/model call, or rolling-spend ledger is exercised or claimed.");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
                service = null;
            }
        }
    }

    private static string ParseSessionId(string output)
    {
        const string prefix = "SESSION id=";
        var start = output.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("The session handoff output omitted its opaque session identifier.");
        }

        start += prefix.Length;
        var end = output.IndexOfAny([' ', '\r', '\n'], start);
        return end < 0 ? output[start..] : output[start..end];
    }
}
