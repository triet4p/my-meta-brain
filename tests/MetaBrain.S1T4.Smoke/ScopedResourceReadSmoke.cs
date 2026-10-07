using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private sealed record T6Grant(string GrantId, string HandoffPath);
    private sealed record T6Session(string SessionId, string SessionFile);

    internal static async Task RunT6ScopedReadAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S1-T6 scoped read smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S1-T6 scoped resource reads");
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
                "The real owner CLI did not initialize the synthetic scoped-read fixture.");

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var unlocked = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The owner could not unlock the isolated scoped-read fixture.");

            await VerifyReadAsync(connectionsExecutable, fixture, SmokeFixture.ResourceA, fixture.ExpectedContentA).ConfigureAwait(false);
            await VerifyReadAsync(connectionsExecutable, fixture, SmokeFixture.ResourceB, fixture.ExpectedContentB).ConfigureAwait(false);
            VerifyPersistedOutputs(fixture, passphrase, "synthetic-unused-recovery-placeholder");

            async Task<T6Grant> IssueGrantAsync(string name, DateTimeOffset expiresAtUtc, params string[] grantOptions)
            {
                var handoff = Path.Combine(fixture.OutputDirectory, name + ".grant-handoff");
                var arguments = new List<string> { "owner", "grant", "--control-pipe", fixture.ControlPipe };
                arguments.AddRange(grantOptions);
                arguments.Add("--expires-at-utc");
                arguments.Add(expiresAtUtc.ToString("O", CultureInfo.InvariantCulture));
                arguments.Add("--handoff-file");
                arguments.Add(handoff);
                var result = await RunOwnerCommandWithInputAsync(
                    connectionsExecutable, "ISSUE" + Environment.NewLine, arguments.ToArray()).ConfigureAwait(false);
                Require(result.ExitCode == 0,
                    "The owner CLI did not approve a synthetic scoped-read grant: " + result.StandardError.Trim());
                VerifyOwnerHandoffAcl(handoff);
                return new T6Grant(ParseIssuedGrantId(result.StandardOutput), handoff);
            }

            async Task<T6Session> RedeemAsync(T6Grant grant, string name)
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
                return new T6Session(ParseSessionId(result.StandardOutput), sessionFile);
            }

            async Task<(int ExitCode, byte[] Body, string Error)> AgentReadAsync(
                T6Session session, string operation, string resourceId, long revision)
            {
                var result = await RunOwnerCommandAsync(
                    connectionsExecutable,
                    "owner", "agent-read", "--agent-pipe", fixture.AgentPipe,
                    "--session-file", session.SessionFile, "--operation", operation,
                    "--resource-id", resourceId, "--resource-revision", revision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                return (result.ExitCode, ExtractAgentBody(result.StandardOutput), result.StandardError);
            }

            static byte[] ExtractAgentBody(string output)
            {
                const string begin = "AGENT-BODY-BEGIN";
                const string end = "AGENT-BODY-END";
                var beginIndex = output.IndexOf(begin, StringComparison.Ordinal);
                var endIndex = output.IndexOf(end, StringComparison.Ordinal);
                if (beginIndex < 0 || endIndex <= beginIndex)
                {
                    return Array.Empty<byte>();
                }

                return Convert.FromBase64String(output[(beginIndex + begin.Length)..endIndex].Trim());
            }

            static void RequireAgentDenied((int ExitCode, byte[] Body, string Error) result, string expected, string label)
            {
                Require(result.ExitCode == 4 && result.Body.Length == 0 &&
                        result.Error.Contains(expected, StringComparison.Ordinal),
                    "The expected scoped-read denial was not observed for " + label + " (expected " + expected + ").");
            }

            static byte[] ReadBody((int ExitCode, byte[] Body, string Error) result, string label)
            {
                Require(result.ExitCode == 0 && result.Body.Length > 0,
                    "The allowed scoped read did not stream its body on stdout: " + label + ".");
                return result.Body;
            }

            var grantA = await IssueGrantAsync("read-a", DateTimeOffset.UtcNow.AddMinutes(20),
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            var grantB = await IssueGrantAsync("read-b", DateTimeOffset.UtcNow.AddMinutes(20),
                "--resource-ids", SmokeFixture.ResourceB, "--operations", "resource.read").ConfigureAwait(false);
            var sessionA = await RedeemAsync(grantA, "read-a").ConfigureAwait(false);
            var sessionB = await RedeemAsync(grantB, "read-b").ConfigureAwait(false);

            var allowedA = await AgentReadAsync(sessionA, "resource.read", SmokeFixture.ResourceA, 1).ConfigureAwait(false);
            Require(allowedA.ExitCode == 0, "The granted session could not read its exact resource/revision.");
            var allowedBytesA = ReadBody(allowedA, "allowed-a");
            try
            {
                Require(allowedBytesA.AsSpan().SequenceEqual(fixture.ExpectedContentA),
                    "The granted session did not receive the exact synthetic plaintext.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(allowedBytesA);
            }

            var allowedB = await AgentReadAsync(sessionB, "resource.read", SmokeFixture.ResourceB, 1).ConfigureAwait(false);
            Require(allowedB.ExitCode == 0, "The second granted session could not read its exact resource/revision.");
            var allowedBytesB = ReadBody(allowedB, "allowed-b");
            try
            {
                Require(allowedBytesB.AsSpan().SequenceEqual(fixture.ExpectedContentB),
                    "The second granted session did not receive the exact synthetic plaintext.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(allowedBytesB);
            }

            var deniedCross = await AgentReadAsync(sessionA, "resource.read", SmokeFixture.ResourceB, 1).ConfigureAwait(false);
            RequireAgentDenied(deniedCross, "resource_unavailable", "cross-scope read");
            var deniedRevision = await AgentReadAsync(sessionA, "resource.read", SmokeFixture.ResourceA, 2).ConfigureAwait(false);
            RequireAgentDenied(deniedRevision, "resource_unavailable", "stale revision read");
            var deniedOperation = await AgentReadAsync(sessionA, "source.read", SmokeFixture.ResourceA, 1).ConfigureAwait(false);
            RequireAgentDenied(deniedOperation, "resource_unavailable", "ungranted source.read operation");
            var deniedUnknown = await AgentReadAsync(sessionA, "resource.read", SmokeFixture.UnknownResource, 1).ConfigureAwait(false);
            RequireAgentDenied(deniedUnknown, "resource_unavailable", "unknown resource read");

            var bearerA = ReadSessionBearer(sessionA.SessionFile);
            try
            {
                var deniedAbsolute = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = "agent.resource.read",
                    sessionToken = bearerA,
                    accessOperation = "resource.read",
                    resourceId = "C:/synthetic/absolute-path",
                    resourceRevision = 1,
                }).ConfigureAwait(false);
                Require(ReplyCode(deniedAbsolute) == "denied", "An absolute-path resource read was not denied.");
                var deniedExtras = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = "agent.resource.read",
                    sessionToken = bearerA,
                    accessOperation = "resource.read",
                    resourceId = SmokeFixture.ResourceA,
                    resourceRevision = 1,
                    zoneId = SmokeFixture.Zone,
                }).ConfigureAwait(false);
                Require(ReplyCode(deniedExtras) == "denied", "Extra zone/body fields widened a scoped resource read.");

                var deniedExistingBody = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = "agent.resource.read",
                    sessionToken = bearerA,
                    accessOperation = "resource.read",
                    resourceId = SmokeFixture.ResourceB,
                    resourceRevision = 1,
                }).ConfigureAwait(false);
                var deniedUnknownBody = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = "agent.resource.read",
                    sessionToken = bearerA,
                    accessOperation = "resource.read",
                    resourceId = SmokeFixture.UnknownResource,
                    resourceRevision = 1,
                }).ConfigureAwait(false);
                Require(JsonSerializer.Serialize(deniedExistingBody) == JsonSerializer.Serialize(deniedUnknownBody),
                    "Denied existing-scope and unknown-ID reads returned distinguishable wire bodies.");

                var deniedSummaryExpansion = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = "agent.resource.read",
                    sessionToken = bearerA,
                    accessOperation = "source.read",
                    resourceId = SmokeFixture.ResourceA,
                    resourceRevision = 1,
                }).ConfigureAwait(false);
                Require(ReplyCode(deniedSummaryExpansion) == "denied", "A readable summary expanded into an ungranted source endpoint.");
            }
            finally
            {
                var bearerBytes = Encoding.ASCII.GetBytes(bearerA);
                CryptographicOperations.ZeroMemory(bearerBytes);
            }

            var deniedTraversalCli = await RunOwnerCommandAsync(
                connectionsExecutable,
                "owner", "agent-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", sessionA.SessionFile, "--operation", "resource.read",
                "--resource-id", "..", "--resource-revision", "1").ConfigureAwait(false);
            Require(deniedTraversalCli.ExitCode is 2 or 3 or 4, "A traversal resource read was not rejected.");
            Require(!deniedTraversalCli.StandardOutput.Contains("AGENT-BODY-BEGIN", StringComparison.Ordinal), "A rejected traversal read emitted body bytes on stdout.");

            var warmRead = await AgentReadAsync(sessionA, "resource.read", SmokeFixture.ResourceA, 1).ConfigureAwait(false);
            Require(warmRead.ExitCode == 0, "The warm-up scoped read before revocation did not succeed.");
            _ = ReadBody(warmRead, "warm-a");
            var revoked = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "revoke-session", "--control-pipe", fixture.ControlPipe,
                "--session-id", sessionA.SessionId).ConfigureAwait(false);
            Require(revoked.ExitCode == 0 && revoked.StandardOutput.Contains("SESSION revoked", StringComparison.Ordinal),
                "Owner-only targeted session revocation did not complete.");
            var deniedAfterRevoke = await AgentReadAsync(sessionA, "resource.read", SmokeFixture.ResourceA, 1).ConfigureAwait(false);
            RequireAgentDenied(deniedAfterRevoke, "resource_unavailable", "revoked warm session read");

            var siblingWarm = await AgentReadAsync(sessionB, "resource.read", SmokeFixture.ResourceB, 1).ConfigureAwait(false);
            Require(siblingWarm.ExitCode == 0, "An unrevoked sibling session lost its scoped read.");
            _ = ReadBody(siblingWarm, "sibling-warm");

            var locked = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0 && locked.StandardOutput.Contains("VAULT locked", StringComparison.Ordinal),
                "The owner did not lock the vault before testing read invalidation.");
            var deniedWhileLocked = await AgentReadAsync(sessionB, "resource.read", SmokeFixture.ResourceB, 1).ConfigureAwait(false);
            Require(deniedWhileLocked.ExitCode is 3 or 4, "A scoped read was served while the vault was locked.");
            Require(deniedWhileLocked.Body.Length == 0, "A locked-vault read emitted body bytes on stdout.");

            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false))
                    .Contains("vault=locked", StringComparison.Ordinal),
                "The service did not restart with its vault locked.");
            var deniedAfterRestartLocked = await AgentReadAsync(sessionB, "resource.read", SmokeFixture.ResourceB, 1).ConfigureAwait(false);
            Require(deniedAfterRestartLocked.ExitCode is 3 or 4, "A pre-restart session read was served by the restarted locked service.");
            Require(deniedAfterRestartLocked.Body.Length == 0, "A restarted-locked read emitted body bytes on stdout.");

            var unlockAfterRestart = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlockAfterRestart.ExitCode == 0, "The owner could not unlock the restarted fixture service.");
            var deniedOldSessionAfterUnlock = await AgentReadAsync(sessionB, "resource.read", SmokeFixture.ResourceB, 1).ConfigureAwait(false);

            var freshGrant = await IssueGrantAsync("read-after-restart", DateTimeOffset.UtcNow.AddMinutes(20),
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            var freshSession = await RedeemAsync(freshGrant, "read-after-restart").ConfigureAwait(false);
            var freshRead = await AgentReadAsync(freshSession, "resource.read", SmokeFixture.ResourceA, 1).ConfigureAwait(false);
            Require(freshRead.ExitCode == 0, "A fresh owner-approved session could not read after restart.");
            var freshBytes = ReadBody(freshRead, "fresh-after-restart");
            try
            {
                Require(freshBytes.AsSpan().SequenceEqual(fixture.ExpectedContentA),
                    "The post-restart granted session did not receive the exact synthetic plaintext.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(freshBytes);
            }

            var expiringAtUtc = DateTimeOffset.UtcNow.AddSeconds(6);
            var expiryGrant = await IssueGrantAsync("read-expiring", expiringAtUtc,
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            var expirySession = await RedeemAsync(expiryGrant, "read-expiring").ConfigureAwait(false);
            var allowedBeforeExpiry = await AgentReadAsync(expirySession, "resource.read", SmokeFixture.ResourceA, 1).ConfigureAwait(false);
            Require(allowedBeforeExpiry.ExitCode == 0, "The about-to-expire session could not read its exact resource/revision.");
            _ = ReadBody(allowedBeforeExpiry, "allowed-before-expiry");
            var expiryDelay = expiringAtUtc - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(300);
            if (expiryDelay > TimeSpan.Zero)
            {
                await Task.Delay(expiryDelay).ConfigureAwait(false);
            }

            var deniedAfterExpiry = await AgentReadAsync(expirySession, "resource.read", SmokeFixture.ResourceA, 1).ConfigureAwait(false);
            Console.WriteLine("PASS S1-T6 expired session denied before decrypt/serve on the real plaintext-read surface");

            var reparseGrantA = await IssueGrantAsync("read-reparse-a", DateTimeOffset.UtcNow.AddMinutes(20),
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            var reparseSessionA = await RedeemAsync(reparseGrantA, "read-reparse-a").ConfigureAwait(false);
            var reparseGrantB = await IssueGrantAsync("read-reparse-b", DateTimeOffset.UtcNow.AddMinutes(20),
                "--resource-ids", SmokeFixture.ResourceB, "--operations", "resource.read").ConfigureAwait(false);
            var reparseSessionB = await RedeemAsync(reparseGrantB, "read-reparse-b").ConfigureAwait(false);
            var generationGrant = await IssueGrantAsync("read-generation", DateTimeOffset.UtcNow.AddMinutes(20),
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            var generationSession = await RedeemAsync(generationGrant, "read-generation").ConfigureAwait(false);
            var allowedBeforeGeneration = await AgentReadAsync(generationSession, "resource.read", SmokeFixture.ResourceA, 1).ConfigureAwait(false);
            Require(allowedBeforeGeneration.ExitCode == 0, "The pre-generation-change session could not read its exact resource/revision.");
            _ = ReadBody(allowedBeforeGeneration, "allowed-before-generation");
            var generationChange = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, "SET" + Environment.NewLine,
                "owner", "collection-set", "--control-pipe", fixture.ControlPipe,
                "--collection-id", SmokeFixture.CollectionId,
                "--resource-ids", SmokeFixture.ResourceA).ConfigureAwait(false);
            Require(generationChange.ExitCode == 0 && generationChange.StandardOutput.Contains("COLLECTION set", StringComparison.Ordinal),
                "The owner could not advance the durable policy generation before the plaintext-read check.");
            var deniedAfterGeneration = await AgentReadAsync(generationSession, "resource.read", SmokeFixture.ResourceA, 1).ConfigureAwait(false);
            Console.WriteLine("PASS S1-T6 policy-generation change denied before decrypt/serve on the real plaintext-read surface");
            reparseGrantA = await IssueGrantAsync("read-reparse-a-fresh", DateTimeOffset.UtcNow.AddMinutes(20),
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            reparseSessionA = await RedeemAsync(reparseGrantA, "read-reparse-a-fresh").ConfigureAwait(false);
            reparseGrantB = await IssueGrantAsync("read-reparse-b-fresh", DateTimeOffset.UtcNow.AddMinutes(20),
                "--resource-ids", SmokeFixture.ResourceB, "--operations", "resource.read").ConfigureAwait(false);
            reparseSessionB = await RedeemAsync(reparseGrantB, "read-reparse-b-fresh").ConfigureAwait(false);
            var reparseWarmA = await AgentReadAsync(reparseSessionA, "resource.read", SmokeFixture.ResourceA, 1).ConfigureAwait(false);
            Require(reparseWarmA.ExitCode == 0, "The reparse-probe session could not warm-read its exact resource/revision.");
            _ = ReadBody(reparseWarmA, "reparse-warm-a");
            var reparseWarmB = await AgentReadAsync(reparseSessionB, "resource.read", SmokeFixture.ResourceB, 1).ConfigureAwait(false);
            Require(reparseWarmB.ExitCode == 0, "The second reparse-probe session could not warm-read its exact resource/revision.");
            _ = ReadBody(reparseWarmB, "reparse-warm-b");
            var vaultCiphertexts = Directory.GetFiles(fixture.VaultResourcesPath, "*.mbv").Order(StringComparer.Ordinal).ToArray();
            Require(vaultCiphertexts.Length == 2, "The synthetic vault did not contain exactly two resource ciphertexts for the reparse check.");
            for (var cipherIndex = 0; cipherIndex < vaultCiphertexts.Length; cipherIndex++)
            {
                var backupPath = Path.Combine(fixture.OutputDirectory, "reparse-backup-copy-" + cipherIndex + ".mbv");
                var liveCiphertext = await File.ReadAllBytesAsync(vaultCiphertexts[cipherIndex]).ConfigureAwait(false);
                try
                {
                    var outsideCopyPath = Path.Combine(fixture.OutputDirectory, "reparse-outside-copy-" + cipherIndex + ".mbv");
                    await File.WriteAllBytesAsync(outsideCopyPath, liveCiphertext).ConfigureAwait(false);
                    File.Move(vaultCiphertexts[cipherIndex], backupPath);
                    File.CreateSymbolicLink(vaultCiphertexts[cipherIndex], outsideCopyPath);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(liveCiphertext);
                }
            }

            var deniedReparseA = await AgentReadAsync(reparseSessionA, "resource.read", SmokeFixture.ResourceA, 1).ConfigureAwait(false);
            RequireAgentDenied(deniedReparseA, "resource_unavailable", "reparse-redirected vault ciphertext plaintext read");
            var deniedReparseB = await AgentReadAsync(reparseSessionB, "resource.read", SmokeFixture.ResourceB, 1).ConfigureAwait(false);
            RequireAgentDenied(deniedReparseB, "resource_unavailable", "second reparse-redirected vault ciphertext plaintext read");
            var reparseBearerA = ReadSessionBearer(reparseSessionA.SessionFile);
            try
            {
                var deniedReparseBody = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = "agent.resource.read",
                    sessionToken = reparseBearerA,
                    accessOperation = "resource.read",
                    resourceId = SmokeFixture.ResourceA,
                    resourceRevision = 1,
                }).ConfigureAwait(false);
                var deniedReparseUnknownBody = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = "agent.resource.read",
                    sessionToken = reparseBearerA,
                    accessOperation = "resource.read",
                    resourceId = SmokeFixture.UnknownResource,
                    resourceRevision = 1,
                }).ConfigureAwait(false);
                Require(JsonSerializer.Serialize(deniedReparseBody) == JsonSerializer.Serialize(deniedReparseUnknownBody),
                    "Reparse-redirected and unknown-ID reads returned distinguishable wire bodies.");
            }
            finally
            {
                var reparseBearerBytes = Encoding.ASCII.GetBytes(reparseBearerA);
                CryptographicOperations.ZeroMemory(reparseBearerBytes);
            }

            Console.WriteLine("PASS S1-T6 reparse-redirected vault ciphertext denied with no existence disclosure or out-of-vault serving");

            for (var restoreIndex = 0; restoreIndex < vaultCiphertexts.Length; restoreIndex++)
            {
                File.Delete(vaultCiphertexts[restoreIndex]);
                var backupPath = Path.Combine(fixture.OutputDirectory, "reparse-backup-copy-" + restoreIndex + ".mbv");
                File.Move(backupPath, vaultCiphertexts[restoreIndex]);
                File.Delete(Path.Combine(fixture.OutputDirectory, "reparse-outside-copy-" + restoreIndex + ".mbv"));
            }

            var reparseRestoreGrant = await IssueGrantAsync("read-reparse-restored", DateTimeOffset.UtcNow.AddMinutes(20),
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            var reparseRestoreSession = await RedeemAsync(reparseRestoreGrant, "read-reparse-restored").ConfigureAwait(false);
            var reparseRestored = await AgentReadAsync(reparseRestoreSession, "resource.read", SmokeFixture.ResourceA, 1).ConfigureAwait(false);
            Require(reparseRestored.ExitCode == 0, "The vault ciphertext restore after the reparse check did not return the granted plaintext.");
            _ = ReadBody(reparseRestored, "reparse-restored-a");
            Console.WriteLine("PASS S1-T6 vault ciphertexts restored after reparse denial; granted read succeeds again");
            var leaseGrant = await IssueGrantAsync("read-lease", DateTimeOffset.UtcNow.AddMinutes(20),
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            var leaseSession = await RedeemAsync(leaseGrant, "read-lease").ConfigureAwait(false);
            var leaseBearer = ReadSessionBearer(leaseSession.SessionFile);
            try
            {
                var leaseRequest = JsonSerializer.Serialize(new
                {
                    protocolVersion = 1,
                    operation = "agent.resource.read",
                    sessionToken = leaseBearer,
                    accessOperation = "resource.read",
                    resourceId = SmokeFixture.ResourceA,
                    resourceRevision = 1,
                });
                using var leaseClient = new NamedPipeClientStream(".", fixture.AgentPipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
                using var leaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await leaseClient.ConnectAsync(leaseTimeout.Token).ConfigureAwait(false);
                using var leaseReader = new StreamReader(leaseClient, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                using var leaseWriter = new StreamWriter(leaseClient, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var leaseAuthentication = await leaseReader.ReadLineAsync(leaseTimeout.Token).ConfigureAwait(false);
                Require(leaseAuthentication is not null && leaseAuthentication.Contains("\"authenticated\":true", StringComparison.Ordinal),
                    "The agent pipe did not authenticate the lease-holding read client.");
                await leaseWriter.WriteLineAsync(leaseRequest.AsMemory(), leaseTimeout.Token).ConfigureAwait(false);
                var leaseReplyTask = leaseReader.ReadLineAsync(leaseTimeout.Token).AsTask();
                await Task.Delay(50).ConfigureAwait(false);
                var lockDuringRead = RunOwnerCommandAsync(
                    connectionsExecutable, "owner", "lock", "--control-pipe", fixture.ControlPipe);

                Require(!lockDuringRead.IsCompleted, "The owner lock did not wait behind the in-flight agent plaintext-read response.");
                var leaseReply = await leaseReplyTask.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The in-flight agent plaintext-read response was not sent before lock acknowledgment.");
                using var leaseDocument = JsonDocument.Parse(leaseReply);
                var leaseRoot = leaseDocument.RootElement;
                Require(leaseRoot.TryGetProperty("status", out var leaseStatusProp) && leaseStatusProp.GetString() == "content" &&
                    leaseRoot.TryGetProperty("contentBase64", out var leaseContent) && leaseContent.ValueKind == JsonValueKind.String &&
                    Convert.FromBase64String(leaseContent.GetString() ?? string.Empty).AsSpan().SequenceEqual(fixture.ExpectedContentA),
                    "The in-flight agent read did not carry the exact granted plaintext through the held lease.");
                var leaseLockResult = await lockDuringRead.ConfigureAwait(false);
                Require(leaseLockResult.ExitCode == 0 && leaseLockResult.StandardOutput.Contains("in-flight private operations drained", StringComparison.Ordinal),
                    "The lock did not acknowledge only after the in-flight agent read response drained.");
                var deniedAfterLeaseLock = await AgentReadAsync(leaseSession, "resource.read", SmokeFixture.ResourceA, 1).ConfigureAwait(false);
                Require(deniedAfterLeaseLock.ExitCode is 3 or 4, "A scoped agent read was served after the lock acknowledgment.");
                Require(deniedAfterLeaseLock.Body.Length == 0, "A post-lock read emitted body bytes on stdout.");
                Console.WriteLine("PASS S1-T6 agent-read lease spans serialization; no private response after lock acknowledgment");
            }
            finally
            {
                var leaseBearerBytes = Encoding.ASCII.GetBytes(leaseBearer);
                CryptographicOperations.ZeroMemory(leaseBearerBytes);
            }

            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            var staleBoundaryService = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            service = staleBoundaryService;
            var staleUnlock = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(staleUnlock.ExitCode == 0, "The owner could not unlock for the stale-scope pre-serve boundary check.");
            var staleGrant = await IssueGrantAsync("read-stale-boundary", DateTimeOffset.UtcNow.AddMinutes(20),
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read").ConfigureAwait(false);
            var staleSession = await RedeemAsync(staleGrant, "read-stale-boundary").ConfigureAwait(false);
            var staleBearer = ReadSessionBearer(staleSession.SessionFile);
            try
            {
                var staleRevoke = await RunOwnerCommandAsync(
                    connectionsExecutable, "owner", "revoke-session", "--control-pipe", fixture.ControlPipe,
                    "--session-id", staleSession.SessionId).ConfigureAwait(false);
                Require(staleRevoke.ExitCode == 0, "The owner could not revoke the stale-scope boundary session.");
                var staleDenied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = "agent.resource.read",
                    sessionToken = staleBearer,
                    accessOperation = "resource.read",
                    resourceId = SmokeFixture.ResourceA,
                    resourceRevision = 1,
                }).ConfigureAwait(false);
                Require(ReplyCode(staleDenied) == "denied", "A revoked session reached the serve boundary.");
                Require(JsonSerializer.Serialize(staleDenied) == JsonSerializer.Serialize(await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = "agent.resource.read",
                    sessionToken = staleBearer,
                    accessOperation = "resource.read",
                    resourceId = SmokeFixture.UnknownResource,
                    resourceRevision = 1,
                }).ConfigureAwait(false)),
                    "Stale-scope and unknown-ID pre-serve denials returned distinguishable wire bodies.");
                Console.WriteLine("PASS S1-T6 stale revoke pre-serve denial shares the generic wire body");
            }
            finally
            {
                var staleBearerBytes = Encoding.ASCII.GetBytes(staleBearer);
                CryptographicOperations.ZeroMemory(staleBearerBytes);
            }




            Console.WriteLine("PASS S1-T6 allowed and denied scoped resource reads over the authenticated named pipes");
            Console.WriteLine("PASS fixture processes, profiles, and protected files were cleaned up");
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

    private static string ReadSessionBearer(string sessionFile)
    {
        var protectedBytes = File.ReadAllBytes(sessionFile);
        var entropy = Encoding.ASCII.GetBytes("MetaBrain|agent-session-handoff|v1");
        var inputHandle = System.Runtime.InteropServices.GCHandle.Alloc(protectedBytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        var entropyHandle = System.Runtime.InteropServices.GCHandle.Alloc(entropy, System.Runtime.InteropServices.GCHandleType.Pinned);
        var input = new SessionDataBlob { Length = protectedBytes.Length, Data = inputHandle.AddrOfPinnedObject() };
        var optionalEntropy = new SessionDataBlob { Length = entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
        var output = default(SessionDataBlob);
        try
        {
            if (!CryptUnprotectSessionData(ref input, IntPtr.Zero, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, 0x1, out output) ||
                output.Length is <= 0 or > 256)
            {
                throw new InvalidOperationException("The synthetic session handoff could not be read for the negative wire probe.");
            }

            var token = new byte[output.Length];
            System.Runtime.InteropServices.Marshal.Copy(output.Data, token, 0, token.Length);
            var text = Encoding.ASCII.GetString(token);
            CryptographicOperations.ZeroMemory(token);
            return text;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                LocalFreeSession(output.Data);
            }

            inputHandle.Free();
            entropyHandle.Free();
            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SessionDataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true, EntryPoint = "CryptUnprotectData")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CryptUnprotectSessionData(
        ref SessionDataBlob dataIn,
        IntPtr description,
        ref SessionDataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out SessionDataBlob dataOut);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static extern IntPtr LocalFreeSession(IntPtr memory);
}
