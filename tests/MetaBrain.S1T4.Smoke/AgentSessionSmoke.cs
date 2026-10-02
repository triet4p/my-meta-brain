using System.Globalization;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const int ScopeStateKdfIterations = 600_000;
    private const int ScopeStateKeyBytes = 32;
    private const int ScopeStateNonceBytes = 12;
    private const int ScopeStateTagBytes = 16;

    private sealed record IssuedSmokeGrant(string GrantId, string HandoffPath, byte[] Token);

    internal static async Task RunAgentSessionAsync(string connectionsExecutable)
    {
        using var fixture = SmokeFixture.Create();
        RunningService? service = null;
        string? passphrase = null;
        string? recoveryCode = null;
        var commandOutput = new List<string>();
        var serviceOutput = new List<string>();
        var issuedGrants = new List<IssuedSmokeGrant>();
        var knownBearers = new List<string>();
        string? replayedPreviewToken = null;

        async Task<CommandResult> RunCliAsync(params string[] arguments)
        {
            var result = await RunOwnerCommandAsync(connectionsExecutable, arguments).ConfigureAwait(false);
            commandOutput.Add(result.StandardOutput);
            commandOutput.Add(result.StandardError);
            return result;
        }

        async Task<CommandResult> RunCliWithInputAsync(string input, params string[] arguments)
        {
            var result = await RunOwnerCommandWithInputAsync(connectionsExecutable, input, arguments).ConfigureAwait(false);
            commandOutput.Add(result.StandardOutput);
            commandOutput.Add(result.StandardError);
            return result;
        }

        async Task StopAndCaptureServiceAsync()
        {
            if (service is null)
            {
                return;
            }

            var stopping = service;
            await StopServiceAsync(stopping).ConfigureAwait(false);
            service = null;
            serviceOutput.Add(await stopping.StandardOutputDrain.ConfigureAwait(false));
            serviceOutput.Add(await stopping.StandardErrorDrain.ConfigureAwait(false));
        }

        async Task<IssuedSmokeGrant> IssueGrantAsync(string name, DateTimeOffset expiresAtUtc, params string[] grantOptions)
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

            var result = await RunCliWithInputAsync("ISSUE" + Environment.NewLine, arguments.ToArray()).ConfigureAwait(false);
            Require(result.ExitCode == 0, "The actual owner CLI failed to approve a synthetic scope grant: " + result.StandardError.Trim());
            var grantId = ParseIssuedGrantId(result.StandardOutput);
            var token = ReadProtectedHandoffToken(handoff);
            issuedGrants.Add(new IssuedSmokeGrant(grantId, handoff, token));
            Require(IsOpaqueScopeToken(token), "The owner CLI did not write a valid opaque token to the protected handoff.");
            VerifyOwnerHandoffAcl(handoff);
            return issuedGrants[^1];
        }

        try
        {
            var sentinelPath = Path.Combine(fixture.Root, "unmanaged-source-sentinel.txt");
            const string sentinelContent = "Synthetic user-owned source; migration must leave it unchanged.";
            await File.WriteAllTextAsync(sentinelPath, sentinelContent, new UTF8Encoding(false)).ConfigureAwait(false);

            passphrase = "synthetic T4 migration passphrase; never production data";
            var migration = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "migrate", "--config", fixture.SettingsPath).ConfigureAwait(false);
            Require(migration.ExitCode == 0 && migration.StandardOutput.Contains("MIGRATION encrypted resources=2", StringComparison.Ordinal),
                "The real owner CLI did not migrate the two owned synthetic source resources (exit=" +
                migration.ExitCode + "; stderr=" + migration.StandardError.Trim() + ").");
            recoveryCode = ParseRecoveryCode(migration.StandardOutput);
            Require(recoveryCode.Length >= 40, "The synthetic vault did not provision a recovery key.");
            VerifyPersistedOutputs(fixture, passphrase, recoveryCode);
            Require(await File.ReadAllTextAsync(sentinelPath).ConfigureAwait(false) == sentinelContent,
                "The migration modified an unrelated synthetic source sentinel.");

            await RewriteSettingsAsSchemaTwoAsync(fixture.SettingsPath).ConfigureAwait(false);
            var legacySettings = await File.ReadAllBytesAsync(fixture.SettingsPath).ConfigureAwait(false);
            using (new FileStream(fixture.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var rejectedSettingsCutover = false;
                try
                {
                    service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    rejectedSettingsCutover = true;
                }

                Require(rejectedSettingsCutover &&
                        (await File.ReadAllBytesAsync(fixture.SettingsPath).ConfigureAwait(false)).AsSpan().SequenceEqual(legacySettings),
                    "A failed schema-2 settings cutover did not leave the original configuration intact.");
            }

            Require(service is null && !HasTemporaryFiles(fixture.ServiceDirectoryPath),
                "A failed settings cutover left a partial replacement or temporary file.");
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var migratedSettings = JsonNode.Parse(await File.ReadAllTextAsync(fixture.SettingsPath).ConfigureAwait(false))!.AsObject();
            Require(migratedSettings["schemaVersion"]?.GetValue<int>() == 3 &&
                    migratedSettings["controlPipe"]?.GetValue<string>() == fixture.ControlPipe &&
                    migratedSettings["agentPipe"]?.GetValue<string>() == fixture.AgentPipe,
                "The real service did not persist the schema-2 configuration as distinct schema-3 owner and agent pipes.");
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false))
                    .Contains("vault=locked", StringComparison.Ordinal),
                "A settings migration did not leave the owner service locked.");
            Require(!HasTemporaryFiles(fixture.ServiceDirectoryPath), "The settings upgrade left an atomic-write temporary file.");

            var unlock = await RunCliWithInputAsync(passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0, "The migrated synthetic vault did not unlock.");
            var collection = await RunCliWithInputAsync("SET" + Environment.NewLine,
                "owner", "collection-set", "--control-pipe", fixture.ControlPipe,
                "--collection-id", SmokeFixture.CollectionId, "--resource-ids", SmokeFixture.ResourceA).ConfigureAwait(false);
            Require(collection.ExitCode == 0, "The owner could not establish the synthetic collection before issuing grants.");

            var longExpiry = DateTimeOffset.UtcNow.AddHours(3);
            var grantA = await IssueGrantAsync("migration-zone", longExpiry,
                "--zone-id", SmokeFixture.Zone, "--operations", "resource.read").ConfigureAwait(false);
            await WriteFixtureResourceAsync(connectionsExecutable, fixture, SmokeFixture.ResourceA,
                "A content advanced after the first immutable grant.", expectedRevision: 2).ConfigureAwait(false);
            await WriteFixtureResourceAsync(connectionsExecutable, fixture, SmokeFixture.ResourceB,
                "B content advanced after the first immutable grant.", expectedRevision: 2).ConfigureAwait(false);
            var grantB = await IssueGrantAsync("migration-collection-egress", longExpiry,
                "--collection-id", SmokeFixture.CollectionId,
                "--operations", "proposal.create,link.create,provider.egress",
                "--destination-resource-ids", SmokeFixture.ResourceB,
                "--provider", "synthetic-provider", "--model", "synthetic-model", "--max-cost-usd", "0.25").ConfigureAwait(false);
            Require(issuedGrants.Count == 2, "The migration fixture did not create exactly two legacy grants.");

            var resourceCiphertextsBefore = HashResourceCiphertexts(fixture.VaultResourcesPath);
            var expectedMigratedState = RewriteScopeStateAsSchemaTwo(
                fixture.VaultDirectoryPath, passphrase, Path.Combine(fixture.VaultDirectoryPath, "scope-grants.enc"),
                grantA.GrantId, grantB.GrantId);
            var schemaTwoStatePath = Path.Combine(fixture.VaultDirectoryPath, "scope-grants.enc");
            var preUpgradeLock = await RunCliAsync("owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(preUpgradeLock.ExitCode == 0, "The owner could not lock before the legacy encrypted-state upgrade.");

            var schemaTwoBytes = await File.ReadAllBytesAsync(schemaTwoStatePath).ConfigureAwait(false);
            using (new FileStream(schemaTwoStatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failedScopeCutover = await RunCliWithInputAsync(passphrase + Environment.NewLine,
                    "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
                Require(failedScopeCutover.ExitCode == 3 &&
                        (await File.ReadAllBytesAsync(schemaTwoStatePath).ConfigureAwait(false)).AsSpan().SequenceEqual(schemaTwoBytes),
                    "A failed encrypted scope-state cutover did not preserve the complete schema-2 state.");
            }
            CryptographicOperations.ZeroMemory(schemaTwoBytes);

            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false))
                    .Contains("vault=locked", StringComparison.Ordinal) &&
                    !HasTemporaryFiles(fixture.VaultDirectoryPath) &&
                    HashResourceCiphertexts(fixture.VaultResourcesPath).SequenceEqual(resourceCiphertextsBefore),
                "A failed scope-state cutover exposed an unlocked vault, changed resources, or left a temporary file.");
            Require(await File.ReadAllTextAsync(sentinelPath).ConfigureAwait(false) == sentinelContent,
                "A failed scope-state cutover changed the unrelated synthetic source sentinel.");

            var legacyUnlock = await RunCliWithInputAsync(passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(legacyUnlock.ExitCode == 0,
                "The original schema-2 encrypted grant state could not be retried after its blocked atomic cutover (exit=" +
                legacyUnlock.ExitCode + "; stderr=" + legacyUnlock.StandardError.Trim() + ").");
            var migratedState = ReadScopeGrantState(fixture.VaultDirectoryPath, passphrase,
                Path.Combine(fixture.VaultDirectoryPath, "scope-grants.enc"));
            AssertMigratedScopeState(expectedMigratedState, migratedState, grantA.GrantId, grantB.GrantId);
            Require(HashResourceCiphertexts(fixture.VaultResourcesPath).SequenceEqual(resourceCiphertextsBefore) &&
                    !HasTemporaryFiles(fixture.VaultDirectoryPath) && !HasTemporaryFiles(fixture.ServiceDirectoryPath),
                "Successful schema migration changed resource ciphertext or left an incomplete temporary file.");
            Require(await File.ReadAllTextAsync(sentinelPath).ConfigureAwait(false) == sentinelContent,
                "The schema migration changed an unrelated synthetic source sentinel.");
            await VerifyReadAsync(connectionsExecutable, fixture, SmokeFixture.ResourceA,
                Encoding.UTF8.GetBytes("A content advanced after the first immutable grant.")).ConfigureAwait(false);
            await VerifyReadAsync(connectionsExecutable, fixture, SmokeFixture.ResourceB,
                Encoding.UTF8.GetBytes("B content advanced after the first immutable grant.")).ConfigureAwait(false);

            var grantListing = await RunCliAsync("owner", "grants", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(grantListing.ExitCode == 0 && grantListing.StandardOutput.Contains("GRANTS count=2", StringComparison.Ordinal),
                "Both old-schema grants were not retained in the authenticated owner grant list.");

            var tokenA = Encoding.ASCII.GetString(grantA.Token);
            var tokenB = Encoding.ASCII.GetString(grantB.Token);
            knownBearers.Add(tokenA);
            knownBearers.Add(tokenB);
            var malformed = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.redeem",
                token = "malformed-token"
            }).ConfigureAwait(false);
            var unknownTokenBytes = RandomNumberGenerator.GetBytes(32);
            var unknownToken = "mb1_" + Convert.ToBase64String(unknownTokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            CryptographicOperations.ZeroMemory(unknownTokenBytes);
            knownBearers.Add(unknownToken);
            var unknown = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.redeem",
                token = unknownToken
            }).ConfigureAwait(false);
            Require(ReplyCode(malformed) == "token_unknown" && ReplyCode(unknown) == "token_unknown",
                "Malformed and valid-format unknown bearer tokens were not rejected as unknown.");

            var ownerPipeAgentOperation = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.redeem",
                token = tokenA
            }).ConfigureAwait(false);
            Require(ReplyCode(ownerPipeAgentOperation) == "forbidden",
                "The owner control pipe accepted an agent-channel token redemption operation.");
            var ownerPipeInspect = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.inspect",
                sessionToken = tokenA
            }).ConfigureAwait(false);
            Require(ReplyCode(ownerPipeInspect) == "forbidden",
                "The owner control pipe accepted an agent-channel session inspection operation.");
            foreach (var ownerOnlyOperation in new[]
                     {
                         "vault.unlock", "vault.provision", "vault.lock", "owner.scope.preview", "owner.scope.issue",
                         "owner.scope.collection.set", "owner.scope.collection.list", "owner.scope.list",
                         "owner.status", "resource.read", "resource.write"
                     })
            {
                var denied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = ownerOnlyOperation,
                    isOwner = true,
                    localSid = "S-1-5-21-100-200-300-4000",
                    cwd = "C:/synthetic/untrusted-working-directory",
                    token = tokenA,
                    sessionToken = tokenA,
                    resourceIds = new[] { SmokeFixture.ResourceB },
                    operations = new[] { "provider.egress" },
                    destinationResourceIds = new[] { SmokeFixture.ResourceB },
                    expiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
                    provider = "spoofed-provider",
                    model = "spoofed-model",
                    maximumCostUsd = 1000m,
                    passphrase = "synthetic-not-a-vault-key"
                }).ConfigureAwait(false);
                Require(ReplyCode(denied) == "forbidden", "The separate agent pipe accepted owner-only operation " + ownerOnlyOperation + ".");
            }

            var spoofedRedeem = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.redeem",
                token = tokenA,
                sessionToken = tokenA,
                isOwner = true,
                localSid = "S-1-5-21-100-200-300-4000",
                cwd = "C:/synthetic/untrusted-working-directory",
                resourceIds = new[] { SmokeFixture.ResourceB },
                operations = new[] { "provider.egress" },
                destinationResourceIds = new[] { SmokeFixture.ResourceB },
                expiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
                provider = "spoofed-provider",
                model = "spoofed-model",
                maximumCostUsd = 1000m,
                scope = new { resourceIds = new[] { SmokeFixture.ResourceB }, operations = new[] { "provider.egress" } }
            }).ConfigureAwait(false);
            Require(ReplyCode(spoofedRedeem) == "forbidden",
                "Owner, SID, working-directory, scope, operation, or expiry body fields altered the token redemption authority.");

            var collectionAfterDenials = await RunCliAsync("owner", "collections", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(collectionAfterDenials.ExitCode == 0 &&
                    collectionAfterDenials.StandardOutput.Contains(SmokeFixture.ResourceA, StringComparison.Ordinal) &&
                    !collectionAfterDenials.StandardOutput.Contains(SmokeFixture.ResourceB, StringComparison.Ordinal),
                "Denied agent-channel or spoofed-body requests changed the owner-approved collection.");
            var replayPreview = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "owner.scope.preview",
                resourceIds = new[] { SmokeFixture.ResourceB },
                operations = new[] { "resource.read" },
                expiresAtUtc = DateTimeOffset.UtcNow.AddHours(3)
            }).ConfigureAwait(false);
            Require(ReplyCode(replayPreview) == "scope_preview",
                "The owner pipe did not create a server-held one-approval scope preview.");
            var approvedPreviewId = replayPreview.GetProperty("scopePreview").GetProperty("previewId").GetString()
                ?? throw new InvalidOperationException("The owner preview omitted its server-generated ID.");
            var firstPreviewIssue = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "owner.scope.issue",
                previewId = approvedPreviewId
            }).ConfigureAwait(false);
            Require(ReplyCode(firstPreviewIssue) == "token_issued",
                "The authenticated owner could not issue the server-previewed scope.");
            replayedPreviewToken = RequireString(firstPreviewIssue, "token");
            knownBearers.Add(replayedPreviewToken);
            var previewTokenBytes = Encoding.ASCII.GetBytes(replayedPreviewToken);
            try
            {
                Require(IsOpaqueScopeToken(previewTokenBytes), "The direct owner issue response did not contain an opaque synthetic bearer.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(previewTokenBytes);
            }

            var repeatedPreviewIssue = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "owner.scope.issue",
                previewId = approvedPreviewId
            }).ConfigureAwait(false);
            Require(ReplyCode(repeatedPreviewIssue) == "scope_preview_unavailable" &&
                    repeatedPreviewIssue.TryGetProperty("token", out var repeatedPreviewToken) &&
                    repeatedPreviewToken.ValueKind == JsonValueKind.Null,
                "A completed owner approval preview was reused to issue a second grant.");
            var grantCountAfterPreviewReplay = await RunCliAsync("owner", "grants", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(grantCountAfterPreviewReplay.ExitCode == 0 &&
                    grantCountAfterPreviewReplay.StandardOutput.Contains("GRANTS count=3", StringComparison.Ordinal),
                "A rejected preview replay created a second persistent grant.");


            var sessionAPath = Path.Combine(fixture.OutputDirectory, "session-a.handoff");
            var redeemA = await RunCliAsync("owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                "--handoff-file", grantA.HandoffPath, "--session-file", sessionAPath).ConfigureAwait(false);
            Require(redeemA.ExitCode == 0 && File.Exists(sessionAPath),
                "The actual owner CLI did not redeem the migrated handoff into a protected session file.");
            VerifyOwnerHandoffAcl(sessionAPath);
            var sessionHandoffBytes = await File.ReadAllBytesAsync(sessionAPath).ConfigureAwait(false);
            try
            {
                Require(sessionHandoffBytes.AsSpan().IndexOf("mbs1_"u8) < 0,
                    "The protected agent-session handoff persisted its bearer without DPAPI protection.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sessionHandoffBytes);
            }

            var inspectA = await RunCliAsync("owner", "session", "--agent-pipe", fixture.AgentPipe,
                "--session-file", sessionAPath).ConfigureAwait(false);
            Require(inspectA.ExitCode == 0 &&
                    inspectA.StandardOutput.Contains(SmokeFixture.ResourceA, StringComparison.Ordinal) &&
                    inspectA.StandardOutput.Contains(SmokeFixture.ResourceB, StringComparison.Ordinal) &&
                    inspectA.StandardOutput.Contains("revision=1", StringComparison.Ordinal) &&
                    !inspectA.StandardOutput.Contains("revision=2", StringComparison.Ordinal) &&
                    inspectA.StandardOutput.Contains("operations=resource.read", StringComparison.Ordinal),
                "The actual owner CLI could not inspect the immutable bounded session snapshot.");

            var raceReplies = await Task.WhenAll(
                InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = "agent.session.redeem",
                    token = tokenB
                }),
                InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = "agent.session.redeem",
                    token = tokenB
                })).ConfigureAwait(false);
            var winners = raceReplies.Where(reply => ReplyCode(reply) == "session_issued").ToArray();
            var losers = raceReplies.Where(reply => ReplyCode(reply) == "token_used").ToArray();
            Require(winners.Length == 1 && losers.Length == 1,
                "Two concurrent requests for one persisted grant did not produce exactly one session and one replay denial.");
            AssertSessionScope(RequireSession(winners[0]), grantB.GrantId,
                [(SmokeFixture.ResourceA, 2)], [(SmokeFixture.ResourceB, 2)],
                ["link.create", "proposal.create", "provider.egress"], new SyntheticEgress("synthetic-provider", "synthetic-model", 0.25m));
            var raceWinnerSessionBearer = RequireString(winners[0], "sessionToken");
            knownBearers.Add(raceWinnerSessionBearer);
            var winnerInspect = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.inspect",
                sessionToken = raceWinnerSessionBearer
            }).ConfigureAwait(false);
            Require(ReplyCode(winnerInspect) == "session_valid", "The raced grant winner did not receive a live process-memory session.");
            AssertSessionScope(RequireSession(winnerInspect), grantB.GrantId,
                [(SmokeFixture.ResourceA, 2)], [(SmokeFixture.ResourceB, 2)],
                ["link.create", "proposal.create", "provider.egress"], new SyntheticEgress("synthetic-provider", "synthetic-model", 0.25m));


            var consumedTokenAReplay = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.redeem",
                token = tokenA
            }).ConfigureAwait(false);
            var consumedTokenBReplay = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.redeem",
                token = tokenB
            }).ConfigureAwait(false);
            Require(ReplyCode(consumedTokenAReplay) == "token_used" && ReplyCode(consumedTokenBReplay) == "token_used",
                "A consumed legacy grant token was not persistently tombstoned against replay.");

            var expiryAt = DateTimeOffset.UtcNow.AddSeconds(12);
            var expiringSessionGrant = await IssueGrantAsync("session-expiry-grant", expiryAt,
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            var expiringSessionReply = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.redeem",
                token = Encoding.ASCII.GetString(expiringSessionGrant.Token),
                isOwner = true,
                ownerSid = "S-1-5-21-100-200-300-4000",
                localSid = "S-1-5-21-100-200-300-4000",
                cwd = "C:/synthetic/untrusted-working-directory",
                scope = new
                {
                    resourceIds = new[] { SmokeFixture.ResourceB },
                    operations = new[] { "provider.egress" },
                    provider = "spoofed-provider",
                    model = "spoofed-model",
                    maximumCostUsd = 1000m
                },
                expiry = DateTimeOffset.MaxValue
            }).ConfigureAwait(false);
            Require(ReplyCode(expiringSessionReply) == "session_issued",
                "A current owner-approved grant could not create a session before its expiry.");
            AssertSessionScope(RequireSession(expiringSessionReply), expiringSessionGrant.GrantId,
                [(SmokeFixture.ResourceA, 2)], [], ["resource.read"], null);
            var expiringSessionBearer = RequireString(expiringSessionReply, "sessionToken");
            knownBearers.Add(expiringSessionBearer);

            var expiredGrant = await IssueGrantAsync("expired-grant", expiryAt,
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            var expiryDelay = expiryAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(150);
            if (expiryDelay > TimeSpan.Zero)
            {
                await Task.Delay(expiryDelay).ConfigureAwait(false);
            }

            var expiredReply = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.redeem",
                token = Encoding.ASCII.GetString(expiredGrant.Token)
            }).ConfigureAwait(false);
            Require(ReplyCode(expiredReply) == "token_expired", "An expired one-use grant was not rejected by the agent redemption boundary.");
            var expiredSession = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.inspect",
                sessionToken = expiringSessionBearer
            }).ConfigureAwait(false);
            Require(ReplyCode(expiredSession) == "session_expired",
                "A session did not expire at the immutable grant expiry recorded in its snapshot.");

            var revokedGrant = await IssueGrantAsync("revoked-grant", DateTimeOffset.UtcNow.AddHours(3),
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            var changedCollection = await RunCliWithInputAsync("SET" + Environment.NewLine,
                "owner", "collection-set", "--control-pipe", fixture.ControlPipe,
                "--collection-id", SmokeFixture.CollectionId, "--resource-ids", SmokeFixture.ResourceB).ConfigureAwait(false);
            Require(changedCollection.ExitCode == 0, "An owner collection policy change did not complete.");
            var revokedTokenReply = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.redeem",
                token = Encoding.ASCII.GetString(revokedGrant.Token)
            }).ConfigureAwait(false);
            Require(ReplyCode(revokedTokenReply) == "token_revoked", "A scope grant issued before an owner policy change was not revoked.");
            var revokedSession = await RunCliAsync("owner", "session", "--agent-pipe", fixture.AgentPipe,
                "--session-file", sessionAPath).ConfigureAwait(false);
            Require(revokedSession.ExitCode == 3 && revokedSession.StandardError.Contains("session_revoked", StringComparison.Ordinal),
                "An active session was not invalidated by a persisted owner policy-generation change.");
            var revokedRaceSession = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.inspect",
                sessionToken = raceWinnerSessionBearer
            }).ConfigureAwait(false);
            Require(ReplyCode(revokedRaceSession) == "session_revoked",
                "The other active session was not invalidated by the same owner policy-generation change.");

            var pendingLockGrant = await IssueGrantAsync("lock-pending-grant", DateTimeOffset.UtcNow.AddHours(3),
                "--resource-ids", SmokeFixture.ResourceB, "--operations", "resource.read").ConfigureAwait(false);
            var lockResult = await RunCliAsync("owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockResult.ExitCode == 0, "The owner could not explicitly lock the vault after revocation.");
            var lockedRedeem = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.redeem",
                token = Encoding.ASCII.GetString(pendingLockGrant.Token)
            }).ConfigureAwait(false);
            Require(ReplyCode(lockedRedeem) == "vault_locked", "An agent grant redemption was served while the owner vault was locked.");
            var unlockAfterLock = await RunCliWithInputAsync(passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlockAfterLock.ExitCode == 0, "The owner could not unlock after the explicit lock.");
            var supersededByLock = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.redeem",
                token = Encoding.ASCII.GetString(pendingLockGrant.Token)
            }).ConfigureAwait(false);
            Require(ReplyCode(supersededByLock) == "token_superseded", "A pre-lock grant survived the next unlock epoch.");
            var clearedSession = await RunCliAsync("owner", "session", "--agent-pipe", fixture.AgentPipe,
                "--session-file", sessionAPath).ConfigureAwait(false);
            Require(clearedSession.ExitCode == 3 && clearedSession.StandardError.Contains("session_unknown", StringComparison.Ordinal),
                "The process-local agent session survived the owner lock boundary.");

            var interruptedGrant = await IssueGrantAsync("interrupted-response-grant", DateTimeOffset.UtcNow.AddHours(3),
                "--resource-ids", SmokeFixture.ResourceB, "--operations", "resource.read").ConfigureAwait(false);
            var afterRestartGrant = await IssueGrantAsync("restart-pending-grant", DateTimeOffset.UtcNow.AddHours(3),
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            var beforeAbandonedRedeem = await File.ReadAllBytesAsync(schemaTwoStatePath).ConfigureAwait(false);
            var abandonedRequest = JsonSerializer.Serialize(new
            {
                protocolVersion = 1,
                operation = "agent.session.redeem",
                token = Encoding.ASCII.GetString(interruptedGrant.Token)
            });
            await SendAndStopAfterDurableConsumptionAsync(fixture.AgentPipe, abandonedRequest, schemaTwoStatePath,
                beforeAbandonedRedeem, StopAndCaptureServiceAsync).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(beforeAbandonedRedeem);
            Require(service is null, "The synthetic service process was not terminated after its unobserved redemption response.");

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false))
                    .Contains("vault=locked", StringComparison.Ordinal),
                "The service process restart restored an unlocked vault.");
            var restartUnlock = await RunCliWithInputAsync(passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(restartUnlock.ExitCode == 0, "The owner could not unlock after service-process restart.");
            var abandonedReplay = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.redeem",
                token = Encoding.ASCII.GetString(interruptedGrant.Token)
            }).ConfigureAwait(false);
            var pendingAfterRestart = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.redeem",
                token = Encoding.ASCII.GetString(afterRestartGrant.Token)
            }).ConfigureAwait(false);
            Require(ReplyCode(abandonedReplay) == "token_used" && ReplyCode(pendingAfterRestart) == "token_superseded",
                "A crash after durable grant consumption lost the tombstone or revived a pending old-epoch grant.");
            var sessionAfterRestart = await RunCliAsync("owner", "session", "--agent-pipe", fixture.AgentPipe,
                "--session-file", sessionAPath).ConfigureAwait(false);
            Require(sessionAfterRestart.ExitCode == 3 && sessionAfterRestart.StandardError.Contains("session_unknown", StringComparison.Ordinal),
                "An in-memory session was resurrected after the service process restarted.");
            Require(await File.ReadAllTextAsync(sentinelPath).ConfigureAwait(false) == sentinelContent &&
                    HashResourceCiphertexts(fixture.VaultResourcesPath).SequenceEqual(resourceCiphertextsBefore),
                "The redemption/restart path changed an unrelated source or encrypted resource content.");

            var finalState = ReadScopeGrantState(fixture.VaultDirectoryPath, passphrase, schemaTwoStatePath);
            Require(finalState["schemaVersion"]?.GetValue<int>() == 3 &&
                    finalState["consumedTokenVerifiers"] is JsonArray consumed && consumed.Count == 4,
                "The final encrypted state did not contain exactly the four durably consumed grant tombstones.");
            var finalGrants = await RunCliAsync("owner", "grants", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(finalGrants.ExitCode == 0 && finalGrants.StandardOutput.Contains("GRANTS count=9", StringComparison.Ordinal),
                "Successful, expired, revoked, locked, and restarted grant transitions did not retain the expected owner audit records.");
            await StopAndCaptureServiceAsync().ConfigureAwait(false);
            Require(service is null && !HasTemporaryFiles(fixture.ServiceDirectoryPath),
                "The T4 service did not stop cleanly with every scoped write temporary file removed.");
            var stateJson = finalState.ToJsonString();
            foreach (var grant in issuedGrants)
            {
                Require(!stateJson.Contains(Encoding.ASCII.GetString(grant.Token), StringComparison.Ordinal),
                    "An issued raw grant bearer was stored in decrypted persistent scope state.");
            }
            Require(replayedPreviewToken is not null &&
                    !stateJson.Contains(replayedPreviewToken, StringComparison.Ordinal),
                "The one-approval test bearer was stored in decrypted persistent scope state.");

            var files = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories);
            var bearerSecrets = issuedGrants.Select(grant => grant.Token).ToArray();
            foreach (var file in files)
            {
                var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                try
                {
                    var fileText = Encoding.UTF8.GetString(bytes);
                    Require(!fileText.Contains("mbs1_", StringComparison.Ordinal) &&
                            !fileText.Contains(passphrase, StringComparison.Ordinal) &&
                            !fileText.Contains(recoveryCode, StringComparison.Ordinal),
                        "A raw bearer or vault credential was persisted in a synthetic fixture file.");

                    foreach (var bearer in bearerSecrets)
                    {
                        Require(!ContainsBytes(bytes, bearer), "A raw grant bearer was persisted in a synthetic fixture file.");
                    }

                    foreach (var sessionBearer in knownBearers)
                    {
                        Require(!fileText.Contains(sessionBearer, StringComparison.Ordinal),
                            "A raw session or grant bearer was persisted in a synthetic fixture file.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            foreach (var output in commandOutput.Concat(serviceOutput))
            {
                Require(!output.Contains("mbs1_", StringComparison.Ordinal) &&
                        !output.Contains(passphrase, StringComparison.Ordinal) &&
                        !output.Contains(recoveryCode, StringComparison.Ordinal),
                    "An owner CLI or service log exposed a bearer marker or vault credential.");
                foreach (var bearer in issuedGrants)
                {
                    Require(!output.Contains(Encoding.ASCII.GetString(bearer.Token), StringComparison.Ordinal),
                        "An owner CLI or service log exposed an opaque grant bearer.");
                }

                foreach (var sessionBearer in knownBearers)
                {
                    Require(!output.Contains(sessionBearer, StringComparison.Ordinal),
                        "An owner CLI or service log exposed a raw session or grant bearer.");
                }
            }

            Console.WriteLine("PASS T4 real IPC: schema-2 settings and encrypted grant-state atomic migration/retry; preserved sources and resource ciphertext; legacy grants redeemed into frozen snapshots; owner/agent pipe separation and spoof denial; concurrent one-use redemption; expiry, policy revocation, lock epoch, durable consume-before-disclose, restart tombstones, protected handoffs, and bearer-free outputs/files");
        }
        finally
        {
            await StopAndCaptureServiceAsync().ConfigureAwait(false);
            foreach (var grant in issuedGrants)
            {
                CryptographicOperations.ZeroMemory(grant.Token);
            }

        }
    }

    private static async Task RewriteSettingsAsSchemaTwoAsync(string settingsPath)
    {
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath).ConfigureAwait(false))!.AsObject();
        settings["schemaVersion"] = 2;
        settings.Remove("agentPipe");
        await File.WriteAllTextAsync(settingsPath, settings.ToJsonString(), new UTF8Encoding(false)).ConfigureAwait(false);
    }

    private static JsonObject RewriteScopeStateAsSchemaTwo(
        string vaultDirectory, string passphrase, string scopeStatePath, string firstGrantId, string secondGrantId)
    {
        var vaultId = ReadSyntheticVaultId(vaultDirectory);
        var dataKey = UnwrapSyntheticVaultDataKey(vaultDirectory, passphrase, vaultId);
        byte[]? currentCiphertext = null;
        byte[]? statePlaintext = null;
        byte[]? legacyPlaintext = null;
        byte[]? legacyCiphertext = null;
        try
        {
            currentCiphertext = File.ReadAllBytes(scopeStatePath);
            statePlaintext = DecryptScopeState(currentCiphertext, dataKey, vaultId, associatedSchemaVersion: 3);
            var legacyState = JsonNode.Parse(Encoding.UTF8.GetString(statePlaintext))!.AsObject();
            var expected = (JsonObject)legacyState.DeepClone();
            var grants = legacyState["grants"]!.AsArray();
            Require(grants.Count == 2, "The synthetic pre-migration state did not contain exactly two grants.");
            var originalGeneration = legacyState["policyGeneration"]!.GetValue<long>();
            var finalLegacyGeneration = originalGeneration + grants.Count;
            var originalGrantIds = grants.Select(grant => grant!["grantId"]!.GetValue<string>()).ToArray();
            Require(originalGrantIds.Contains(firstGrantId, StringComparer.Ordinal) &&
                    originalGrantIds.Contains(secondGrantId, StringComparer.Ordinal),
                "The synthetic schema-2 fixture did not contain both individually issued grant IDs.");

            legacyState["schemaVersion"] = 2;
            legacyState["policyGeneration"] = finalLegacyGeneration;
            legacyState.Remove("unlockEpoch");
            legacyState.Remove("consumedTokenVerifiers");
            for (var index = 0; index < grants.Count; index++)
            {
                var legacyGrant = grants[index]!.AsObject();
                legacyGrant["policyGeneration"] = originalGeneration + index + 1;
                legacyGrant.Remove("unlockEpoch");

                var migratedGrant = expected["grants"]![index]!.AsObject();
                migratedGrant["policyGeneration"] = finalLegacyGeneration;
                migratedGrant["unlockEpoch"] = 1;
            }

            expected["schemaVersion"] = 3;
            expected["policyGeneration"] = finalLegacyGeneration;
            expected["unlockEpoch"] = 1;
            expected["consumedTokenVerifiers"] = new JsonArray();

            legacyPlaintext = Encoding.UTF8.GetBytes(legacyState.ToJsonString());
            legacyCiphertext = EncryptScopeState(legacyPlaintext, dataKey, vaultId, associatedSchemaVersion: 2);
            ReplaceSyntheticFileAtomically(scopeStatePath, legacyCiphertext);
            return expected;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            ClearSyntheticBytes(currentCiphertext);
            ClearSyntheticBytes(statePlaintext);
            ClearSyntheticBytes(legacyPlaintext);
            ClearSyntheticBytes(legacyCiphertext);
        }
    }

    private static JsonObject ReadScopeGrantState(string vaultDirectory, string passphrase, string scopeStatePath)
    {
        var vaultId = ReadSyntheticVaultId(vaultDirectory);
        var dataKey = UnwrapSyntheticVaultDataKey(vaultDirectory, passphrase, vaultId);
        byte[]? ciphertext = null;
        byte[]? plaintext = null;
        try
        {
            ciphertext = File.ReadAllBytes(scopeStatePath);
            plaintext = DecryptScopeState(ciphertext, dataKey, vaultId, associatedSchemaVersion: 3);
            return JsonNode.Parse(Encoding.UTF8.GetString(plaintext))!.AsObject();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            ClearSyntheticBytes(ciphertext);
            ClearSyntheticBytes(plaintext);
        }
    }

    private static void AssertMigratedScopeState(JsonObject expected, JsonObject actual, string firstGrantId, string secondGrantId)
    {
        Require(actual["schemaVersion"]?.GetValue<int>() == 3 &&
                actual["policyGeneration"]?.GetValue<long>() == expected["policyGeneration"]!.GetValue<long>() &&
                actual["unlockEpoch"]?.GetValue<long>() == 1,
            "The migrated grant state did not normalize its v3 schema, policy generation, and unlock epoch.");
        var expectedCollections = expected["collections"]!.AsArray();
        var actualCollections = actual["collections"]!.AsArray();
        Require(expectedCollections.Count == actualCollections.Count &&
                expectedCollections.Select(collection => collection!["collectionId"]!.GetValue<string>())
                    .SequenceEqual(actualCollections.Select(collection => collection!["collectionId"]!.GetValue<string>()), StringComparer.Ordinal),
            "The v2-to-v3 migration changed or dropped an owner collection.");
        for (var index = 0; index < expectedCollections.Count; index++)
        {
            Require(expectedCollections[index]!["resourceIds"]!.ToJsonString() == actualCollections[index]!["resourceIds"]!.ToJsonString(),
                "The v2-to-v3 migration changed the concrete collection membership.");
        }

        var expectedGrants = expected["grants"]!.AsArray();
        var actualGrants = actual["grants"]!.AsArray();
        Require(expectedGrants.Count == 2 && actualGrants.Count == expectedGrants.Count,
            "The v2-to-v3 migration dropped an existing grant.");
        foreach (var grantId in new[] { firstGrantId, secondGrantId })
        {
            var prior = expectedGrants.Single(grant => grant!["grantId"]!.GetValue<string>() == grantId)!.AsObject();
            var migrated = actualGrants.Single(grant => grant!["grantId"]!.GetValue<string>() == grantId)!.AsObject();
            var priorVerifier = prior["tokenVerifier"]?.GetValue<string>();
            var migratedVerifier = migrated["tokenVerifier"]?.GetValue<string>();
            Require(priorVerifier is not null && migratedVerifier is not null,
                "The synthetic schema-2 grant or its migrated record did not contain a token verifier.");
            Require(migratedVerifier == priorVerifier &&
                    migrated["policyGeneration"]!.GetValue<long>() == expected["policyGeneration"]!.GetValue<long>() &&
                    migrated["unlockEpoch"]!.GetValue<long>() == 1 &&
                    migrated["expiresAtUtc"]!.GetValue<string>() == prior["expiresAtUtc"]!.GetValue<string>() &&
                    migrated["resources"]!.ToJsonString() == prior["resources"]!.ToJsonString() &&
                    migrated["operations"]!.ToJsonString() == prior["operations"]!.ToJsonString() &&
                    migrated["destinationResources"]!.ToJsonString() == prior["destinationResources"]!.ToJsonString() &&
                    JsonNode.DeepEquals(migrated["egress"], prior["egress"]),
                "The v2-to-v3 migration changed a grant verifier, expiry, policy, resource revision, operation, destination, or egress bound.");
        }

        Require(actual["consumedTokenVerifiers"] is JsonArray consumed && consumed.Count == 0,
            "The v2-to-v3 migration did not initialize the durable one-use tombstone set.");
    }

    private static string ReadSyntheticVaultId(string vaultDirectory)
    {
        using var header = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(vaultDirectory, "vault-header.json")));
        return header.RootElement.GetProperty("vaultId").GetString() ?? throw new InvalidDataException("Synthetic vault header has no identifier.");
    }

    private static byte[] UnwrapSyntheticVaultDataKey(string vaultDirectory, string passphrase, string vaultId)
    {
        using var header = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(vaultDirectory, "vault-header.json")));
        var wrapper = header.RootElement.GetProperty("passphraseKey");
        var iterations = wrapper.GetProperty("kdfIterations").GetInt32();
        Require(iterations == ScopeStateKdfIterations, "The synthetic vault uses an unexpected KDF cost.");
        var salt = Convert.FromBase64String(wrapper.GetProperty("salt").GetString()!);
        var nonce = Convert.FromBase64String(wrapper.GetProperty("nonce").GetString()!);
        var ciphertext = Convert.FromBase64String(wrapper.GetProperty("ciphertext").GetString()!);
        var tag = Convert.FromBase64String(wrapper.GetProperty("tag").GetString()!);
        var wrappingKey = Rfc2898DeriveBytes.Pbkdf2(passphrase.AsSpan(), salt, iterations, HashAlgorithmName.SHA256, ScopeStateKeyBytes);
        var aad = Encoding.UTF8.GetBytes($"MetaBrain|vault-key|1|{vaultId}|passphrase|{iterations}|{Convert.ToBase64String(salt)}");
        var dataKey = new byte[ScopeStateKeyBytes];
        try
        {
            using var aes = new AesGcm(wrappingKey, ScopeStateTagBytes);
            aes.Decrypt(nonce, ciphertext, tag, dataKey, aad);
            return dataKey;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(dataKey);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(wrappingKey);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    private static byte[] DecryptScopeState(byte[] envelopeBytes, byte[] dataKey, string vaultId, int associatedSchemaVersion)
    {
        using var document = JsonDocument.Parse(envelopeBytes);
        var envelope = document.RootElement;
        Require(envelope.GetProperty("formatVersion").GetInt32() == 1, "The synthetic encrypted state envelope format is unexpected.");
        var nonce = Convert.FromBase64String(envelope.GetProperty("nonce").GetString()!);
        var ciphertext = Convert.FromBase64String(envelope.GetProperty("ciphertext").GetString()!);
        var tag = Convert.FromBase64String(envelope.GetProperty("tag").GetString()!);
        var plaintext = new byte[ciphertext.Length];
        var aad = Encoding.UTF8.GetBytes($"MetaBrain|vault-resource|1|{vaultId}|scope-grants|{associatedSchemaVersion}|1");
        try
        {
            using var aes = new AesGcm(dataKey, ScopeStateTagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    private static byte[] EncryptScopeState(byte[] plaintext, byte[] dataKey, string vaultId, int associatedSchemaVersion)
    {
        var nonce = RandomNumberGenerator.GetBytes(ScopeStateNonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[ScopeStateTagBytes];
        var aad = Encoding.UTF8.GetBytes($"MetaBrain|vault-resource|1|{vaultId}|scope-grants|{associatedSchemaVersion}|1");
        try
        {
            using var aes = new AesGcm(dataKey, ScopeStateTagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                formatVersion = 1,
                nonce = Convert.ToBase64String(nonce),
                ciphertext = Convert.ToBase64String(ciphertext),
                tag = Convert.ToBase64String(tag)
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    private static void ReplaceSyntheticFileAtomically(string path, byte[] encryptedContents)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".scope-fixture-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var security = FileSystemAclExtensions.GetAccessControl(new FileInfo(path), AccessControlSections.Access | AccessControlSections.Owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                FileSystemAclExtensions.SetAccessControl(new FileInfo(temporary), security);
                Require(FileSystemAclExtensions.GetAccessControl(new FileInfo(temporary), AccessControlSections.Access).AreAccessRulesProtected,
                    "The synthetic encrypted-state replacement did not retain a protected ACL.");
                stream.Write(encryptedContents);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string[] HashResourceCiphertexts(string resourcesDirectory) =>
        Directory.GetFiles(resourcesDirectory, "*.mbv", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
            .ToArray();

    private static bool HasTemporaryFiles(string directory) =>
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Any(path => Path.GetFileName(path).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));

    private static async Task<JsonElement> InvokeAgentRequestAsync(string pipeName, object request)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var authentication = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        Require(authentication is not null && authentication.Contains("\"authenticated\":true", StringComparison.Ordinal),
            "The separate agent pipe did not authenticate the synthetic local client.");
        var requestJson = JsonSerializer.Serialize(request);
        await writer.WriteLineAsync(requestJson.AsMemory(), timeout.Token).ConfigureAwait(false);
        var reply = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        if (reply is null)
        {
            throw new InvalidOperationException("The actual agent pipe closed before its one-line response.");
        }

        using var document = JsonDocument.Parse(reply);
        return document.RootElement.Clone();
    }

    private static async Task SendAndStopAfterDurableConsumptionAsync(
        string pipeName, string requestJson, string scopeStatePath, byte[] previousState, Func<Task> stopService)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var authentication = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        Require(authentication is not null && authentication.Contains("\"authenticated\":true", StringComparison.Ordinal),
            "The abandoned-response client did not authenticate on the agent pipe.");
        await writer.WriteLineAsync(requestJson.AsMemory(), timeout.Token).ConfigureAwait(false);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var persisted = await File.ReadAllBytesAsync(scopeStatePath, timeout.Token).ConfigureAwait(false);
                try
                {
                    if (!persisted.AsSpan().SequenceEqual(previousState))
                    {
                        await stopService().ConfigureAwait(false);
                        return;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(persisted);
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }

        throw new TimeoutException("The agent pipe did not durably update the owned scope-state file before the response-abandonment deadline.");
    }

    private static string ReplyCode(JsonElement reply)
    {
        if (reply.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
        {
            return status.GetString()!;
        }

        if (reply.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            return error.GetString()!;
        }

        throw new InvalidOperationException("The real pipe response omitted both a status and an error code.");
    }

    private static JsonElement RequireSession(JsonElement reply)
    {
        Require(reply.TryGetProperty("agentSession", out var session) && session.ValueKind == JsonValueKind.Object,
            "The agent pipe did not return its bounded immutable session snapshot.");
        return session;
    }

    private sealed record SyntheticEgress(string Provider, string Model, decimal MaximumCostUsd);

    private static void AssertSessionScope(
        JsonElement session,
        string expectedGrantId,
        (string ResourceId, long Revision)[] expectedResources,
        (string ResourceId, long Revision)[] expectedDestinations,
        string[] expectedOperations,
        SyntheticEgress? expectedEgress)
    {
        Require(session.GetProperty("grantId").GetString() == expectedGrantId,
            "The agent session was not bound to the consumed owner grant ID.");
        AssertSessionResources(session.GetProperty("resources"), expectedResources);
        AssertSessionResources(session.GetProperty("destinationResources"), expectedDestinations);
        var operations = session.GetProperty("operations").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Require(operations.SequenceEqual(expectedOperations, StringComparer.Ordinal),
            "The agent session operation allowlist differed from the owner-approved immutable snapshot.");
        var egress = session.GetProperty("egress");
        if (expectedEgress is null)
        {
            Require(egress.ValueKind == JsonValueKind.Null, "An unapproved egress scope appeared in the agent session.");
        }
        else
        {
            Require(egress.ValueKind == JsonValueKind.Object &&
                    egress.GetProperty("provider").GetString() == expectedEgress.Provider &&
                    egress.GetProperty("model").GetString() == expectedEgress.Model &&
                    egress.GetProperty("maximumCostUsd").GetDecimal() == expectedEgress.MaximumCostUsd,
                "The session egress scope did not preserve the owner-approved provider, model, and cost bound.");
        }
    }

    private static void AssertSessionResources(JsonElement resources, (string ResourceId, long Revision)[] expected)
    {
        var actual = resources.EnumerateArray().ToArray();
        Require(actual.Length == expected.Length, "The agent session contained a different number of concrete resources.");
        for (var index = 0; index < expected.Length; index++)
        {
            Require(actual[index].GetProperty("resourceId").GetString() == expected[index].ResourceId &&
                    actual[index].GetProperty("revision").GetInt64() == expected[index].Revision &&
                    actual[index].GetProperty("zoneId").GetString() == SmokeFixture.Zone,
                "The agent session changed a concrete resource identity, zone, or revision snapshot.");
        }
    }

    private static string RequireString(JsonElement reply, string propertyName)
    {
        Require(reply.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String,
            "The successful agent response omitted its session bearer.");
        return value.GetString()!;
    }


    private static void ClearSyntheticBytes(byte[]? bytes)
    {
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
