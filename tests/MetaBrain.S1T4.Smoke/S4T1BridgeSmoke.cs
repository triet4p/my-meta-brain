using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string S4T1CatalogA = "owner-approved-s4t1-a";
    private const string S4T1CatalogB = "owner-approved-s4t1-b";
    private const string S4T1LabelA = "Approved synthetic S4-T1 lesson A";
    private const string S4T1DescriptionA = "Owner-written S4-T1 bridge summary A; never derived from private text.";
    private const string S4T1LabelB = "Approved synthetic S4-T1 lesson B";
    private const string S4T1DescriptionB = "Owner-written S4-T1 bridge summary B; never derived from private text.";
    private const string S4T1BodyA = "synthetic-s4t1-private-body-a1b2";
    private const string S4T1BodyB = "synthetic-s4t1-private-body-c3d4";
    private const string S4T1PurposeA = "synthetic s4-t1 purpose alpha";
    private const string S4T1PurposeB = "synthetic s4-t1 purpose beta";

    internal static async Task RunS4T1BridgeAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S4-T1 bridge smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S4-T1 MCP transport and capability dispatch");
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
                "The real owner CLI did not initialize the synthetic S4-T1 fixture.");

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var unlocked = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The owner could not unlock the isolated S4-T1 fixture.");

            await WriteS4T1BodyAsync(connectionsExecutable, fixture, SmokeFixture.ResourceA, S4T1BodyA, writeRevision: 1).ConfigureAwait(false);
            await WriteS4T1BodyAsync(connectionsExecutable, fixture, SmokeFixture.ResourceB, S4T1BodyB, writeRevision: 1).ConfigureAwait(false);
            await PublishS4T1CatalogAsync(connectionsExecutable, fixture, S4T1CatalogA, S4T1LabelA, S4T1DescriptionA, SmokeFixture.ResourceA).ConfigureAwait(false);
            await PublishS4T1CatalogAsync(connectionsExecutable, fixture, S4T1CatalogB, S4T1LabelB, S4T1DescriptionB, SmokeFixture.ResourceB).ConfigureAwait(false);

            // S4-T1 transport: initialize negotiates the client's offered protocol version.
            var initCurrent = await RunBridgeRawAsync(connectionsExecutable, fixture.AgentPipe,
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"synthetic-s4t1","version":"1.0.0"}}}""").ConfigureAwait(false);
            Require(initCurrent.Contains("\"protocolVersion\":\"2025-11-25\"", StringComparison.Ordinal),
                "The S4-T1 bridge did not negotiate the current protocol version.");
            var initLegacy = await RunBridgeRawAsync(connectionsExecutable, fixture.AgentPipe,
                """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"synthetic-s4t1","version":"1.0.0"}}}""").ConfigureAwait(false);
            Require(initLegacy.Contains("\"protocolVersion\":\"2025-06-18\"", StringComparison.Ordinal),
                "The S4-T1 bridge forced one protocol revision instead of honoring the client's offered version.");
            var initUnknown = await RunBridgeRawAsync(connectionsExecutable, fixture.AgentPipe,
                """{"jsonrpc":"2.0","id":3,"method":"initialize","params":{"protocolVersion":"1999-01-01","capabilities":{},"clientInfo":{"name":"synthetic-s4t1","version":"1.0.0"}}}""").ConfigureAwait(false);
            Require(initUnknown.Contains("\"protocolVersion\":\"2025-11-25\"", StringComparison.Ordinal),
                "The S4-T1 bridge did not fall back to the documented revision on an unknown offer.");
            var notified = await RunBridgeRawAsync(connectionsExecutable, fixture.AgentPipe,
                """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""").ConfigureAwait(false);
            Require(string.IsNullOrWhiteSpace(notified),
                "The S4-T1 bridge answered a notification instead of staying silent.");
            var ping = await RunBridgeRawAsync(connectionsExecutable, fixture.AgentPipe,
                """{"jsonrpc":"2.0","id":4,"method":"ping","params":{}}""").ConfigureAwait(false);
            Require(ping.Contains("\"result\":{}", StringComparison.Ordinal),
                "The S4-T1 bridge did not answer ping.");

            // S4-T1 capability dispatch: the agent-only tool surface now carries authorize.
            var bridgeList = await RunBridgeToolsListAsync(connectionsExecutable, fixture.AgentPipe).ConfigureAwait(false);
            foreach (var tool in new[]
                     {
                         "metabrain_catalog_list", "metabrain_catalog_query", "metabrain_access_request",
                         "metabrain_request_status", "metabrain_redeem", "metabrain_session_inspect",
                         "metabrain_scoped_read", "metabrain_authorize",
                     })
            {
                Require(bridgeList.Contains(tool, StringComparison.Ordinal),
                    "The S4-T1 bridge did not advertise " + tool + ".");
            }

            Require(!bridgeList.Contains("mb1_", StringComparison.Ordinal) && !bridgeList.Contains("mbs1_", StringComparison.Ordinal),
                "The bridge tool list exposed bearer material.");

            // Same bridge: catalog list shows only approved triplets (no bodies, no resource IDs).
            var catalogOut = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_catalog_list", new { }).ConfigureAwait(false);
            Require(catalogOut.Contains(S4T1CatalogA, StringComparison.Ordinal) &&
                    catalogOut.Contains(S4T1CatalogB, StringComparison.Ordinal) &&
                    !catalogOut.Contains(SmokeFixture.ResourceA, StringComparison.Ordinal) &&
                    !catalogOut.Contains(SmokeFixture.ResourceB, StringComparison.Ordinal) &&
                    !catalogOut.Contains(S4T1BodyA, StringComparison.Ordinal) &&
                    !catalogOut.Contains(S4T1BodyB, StringComparison.Ordinal),
                "Bridge catalog discovery leaked a private mapping or body.");

            // Same bridge: catalog query matches published metadata only; unknown text is no_match.
            var queryOut = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_catalog_query", new { query = "S4-T1" }).ConfigureAwait(false);
            Require(queryOut.Contains(S4T1CatalogA, StringComparison.Ordinal) &&
                    !queryOut.Contains(S4T1BodyA, StringComparison.Ordinal),
                "The bridge catalog query did not serve published metadata.");
            var queryMiss = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_catalog_query", new { query = "no-such-synthetic-catalog-zzz" }).ConfigureAwait(false);
            Require(!queryMiss.Contains(S4T1BodyA, StringComparison.Ordinal) &&
                    !queryMiss.Contains(SmokeFixture.ResourceA, StringComparison.Ordinal),
                "A catalog miss leaked private material.");

            // Same bridge: unknown tool + malformed params + raw bearer fail closed.
            var unknownTool = await RunBridgeRawAsync(connectionsExecutable, fixture.AgentPipe,
                """{"jsonrpc":"2.0","id":91,"method":"tools/call","params":{"name":"metabrain_nope","arguments":{}}}""").ConfigureAwait(false);
            Require(unknownTool.Contains("bridge_unknown_tool", StringComparison.Ordinal),
                "The bridge accepted an unknown tool name.");
            var bearerTool = await RunBridgeRawAsync(connectionsExecutable, fixture.AgentPipe,
                """{"jsonrpc":"2.0","id":92,"method":"tools/call","params":{"name":"metabrain_redeem","arguments":{"handoffFile":"mb1_FAKEBEARER","sessionFile":"out"}}}""").ConfigureAwait(false);
            Require(bearerTool.Contains("bridge_raw_bearer_denied", StringComparison.Ordinal),
                "The bridge accepted raw bearer material in tool arguments.");
            var malformed = await RunBridgeRawAsync(connectionsExecutable, fixture.AgentPipe, "not-json").ConfigureAwait(false);
            Require(malformed.Contains("bridge_parse_error", StringComparison.Ordinal),
                "The bridge did not fail closed on malformed JSON-RPC.");
            var unknownMethod = await RunBridgeRawAsync(connectionsExecutable, fixture.AgentPipe,
                """{"jsonrpc":"2.0","id":93,"method":"resources/read","params":{}}""").ConfigureAwait(false);
            Require(unknownMethod.Contains("bridge_unknown_method", StringComparison.Ordinal),
                "The bridge accepted an unknown JSON-RPC method.");

            // Same bridge: T1-scope request needs only catalog IDs + purpose; provider/model
            // disclosure travels when supplied. Receipt is opaque only.
            var requestOut = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_access_request",
                new { catalogIds = S4T1CatalogA, purpose = S4T1PurposeA }).ConfigureAwait(false);
            Require(requestOut.Contains("REQUEST submitted id=", StringComparison.Ordinal) &&
                    !requestOut.Contains(S4T1BodyA, StringComparison.Ordinal) &&
                    !requestOut.Contains(SmokeFixture.ResourceA, StringComparison.Ordinal) &&
                    !requestOut.Contains("mb1_", StringComparison.Ordinal) &&
                    !requestOut.Contains("mbs1_", StringComparison.Ordinal),
                "The bridge access request disclosed a private body, ID, or bearer.");
            var requestId = ParseBridgeRequestId(requestOut);

            var requestB = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_access_request",
                new
                {
                    catalogIds = S4T1CatalogA + "," + S4T1CatalogB,
                    purpose = S4T1PurposeB,
                    operations = "resource.read",
                    agentId = "synthetic-s4t1-beta",
                    provider = "synthetic-s4t1-provider",
                    model = "synthetic-s4t1-model",
                    disclosureContext = "synthetic s4-t1 disclosure",
                }).ConfigureAwait(false);
            var requestIdB = ParseBridgeRequestId(requestB);
            var listed = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "requests", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(listed.ExitCode == 0 && listed.StandardOutput.Contains($"REQUEST id={requestId}", StringComparison.Ordinal) &&
                    listed.StandardOutput.Contains($"REQUEST id={requestIdB}", StringComparison.Ordinal),
                "The owner request list did not expose both S4-T1 requests.");

            // Narrow approve request B to resource A only; reject request A.
            var previewIdB = await TakeRequestPreviewIdAsync(connectionsExecutable, fixture, requestIdB).ConfigureAwait(false);
            var expiry = DateTimeOffset.UtcNow.AddMinutes(20).ToString("O", CultureInfo.InvariantCulture);
            var handoffB = Path.Combine(fixture.OutputDirectory, "s4t1-b.grant-handoff");
            var approveB = await RunOwnerCommandWithInputAsync(connectionsExecutable, "APPROVE" + Environment.NewLine,
                "owner", "request-approve", "--control-pipe", fixture.ControlPipe,
                "--request-id", requestIdB, "--preview-id", previewIdB,
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read",
                "--expires-at-utc", expiry, "--handoff-file", handoffB).ConfigureAwait(false);
            Require(approveB.ExitCode == 0 && approveB.StandardOutput.Contains("REQUEST approved", StringComparison.Ordinal) &&
                    !approveB.StandardOutput.Contains("mb1_", StringComparison.Ordinal) && File.Exists(handoffB),
                "The owner could not narrow-approve the second S4-T1 request.");
            VerifyOwnerHandoffAcl(handoffB);

            var rejectA = await RunOwnerCommandWithInputAsync(connectionsExecutable, "REJECT" + Environment.NewLine,
                "owner", "request-reject", "--control-pipe", fixture.ControlPipe,
                "--request-id", requestId).ConfigureAwait(false);
            Require(rejectA.ExitCode == 0 && rejectA.StandardOutput.Contains($"REQUEST rejected id={requestId}", StringComparison.Ordinal),
                "The owner could not reject the first S4-T1 request.");

            // Same bridge: redeem handoff path (no bearer in args) then inspect + scoped read + authorize.
            var sessionB = Path.Combine(fixture.OutputDirectory, "s4t1-b.session-handoff");
            var redeemOut = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_redeem", new { handoffFile = handoffB, sessionFile = sessionB }).ConfigureAwait(false);
            Require(redeemOut.Contains("SESSION issued", StringComparison.Ordinal) &&
                    !redeemOut.Contains("mb1_", StringComparison.Ordinal) &&
                    !redeemOut.Contains("mbs1_", StringComparison.Ordinal) && File.Exists(sessionB),
                "The bridge redeem did not issue a session from the DPAPI handoff path.");
            VerifyOwnerHandoffAcl(sessionB);
            var sessionIdB = ParseBridgeSessionId(redeemOut);

            // Replay the same handoff: the single-use token must not redeem twice.
            var replaySession = Path.Combine(fixture.OutputDirectory, "s4t1-b.replay-session-handoff");
            var replayOut = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_redeem", new { handoffFile = handoffB, sessionFile = replaySession }).ConfigureAwait(false);
            Require(!replayOut.Contains("SESSION issued", StringComparison.Ordinal),
                "A replayed S4-T1 token redeemed a second session.");

            var inspectOut = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_session_inspect", new { sessionFile = sessionB }).ConfigureAwait(false);
            Require(inspectOut.Contains($"SESSION id={sessionIdB}", StringComparison.Ordinal),
                "The bridge session inspect did not report the issued session.");

            var scopedOut = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_scoped_read",
                new { sessionFile = sessionB, operation = "resource.read", resourceId = SmokeFixture.ResourceA, resourceRevision = "2" }).ConfigureAwait(false);
            Require(scopedOut.Contains(S4T1BodyA, StringComparison.Ordinal) &&
                    !scopedOut.Contains("READ content bytes=", StringComparison.Ordinal) &&
                    !scopedOut.Contains("mb1_", StringComparison.Ordinal) &&
                    !scopedOut.Contains("mbs1_", StringComparison.Ordinal),
                "The bridge scoped read did not deliver the exact approved body in memory.");
            Require(!File.Exists(Path.Combine(fixture.OutputDirectory, "s4t1-b.read.bin")),
                "The default bridge read persisted a private plaintext file.");
            var deniedOut = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_scoped_read",
                new { sessionFile = sessionB, operation = "resource.read", resourceId = SmokeFixture.ResourceB, resourceRevision = "2" }).ConfigureAwait(false);
            Require(deniedOut.Contains("resource_unavailable", StringComparison.Ordinal) && !deniedOut.Contains(S4T1BodyB, StringComparison.Ordinal),
                "A narrowed/excluded S4-T1 read was served.");

            // S4-T1 capability dispatch: authorize answers the allow/deny decision without content.
            var allowOut = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_authorize",
                new { sessionFile = sessionB, operation = "resource.read", resourceId = SmokeFixture.ResourceA, resourceRevision = "2" }).ConfigureAwait(false);
            Require(allowOut.Contains("ALLOW", StringComparison.Ordinal) && !allowOut.Contains(S4T1BodyA, StringComparison.Ordinal),
                "The bridge authorize did not allow the approved operation.");
            var denyOut = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_authorize",
                new { sessionFile = sessionB, operation = "resource.read", resourceId = SmokeFixture.ResourceB, resourceRevision = "2" }).ConfigureAwait(false);
            Require(denyOut.Contains("DENY", StringComparison.Ordinal) && !denyOut.Contains(S4T1BodyB, StringComparison.Ordinal),
                "The bridge authorize did not deny the out-of-scope operation.");

            // Past-expiry approve must stay a JSON deny (not a dropped pipe) and
            // must leave the owner channel usable: the request stays undecided,
            // no grant is minted, and the next valid approve on the same live
            // service still succeeds.
            var expiryRequest = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_access_request",
                new { catalogIds = S4T1CatalogA, purpose = S4T1PurposeA, operations = "resource.read", agentId = "synthetic-s4t1-expiry" }).ConfigureAwait(false);
            var expiryRequestId = ParseBridgeRequestId(expiryRequest);
            var expiryPreviewId = await TakeRequestPreviewIdAsync(connectionsExecutable, fixture, expiryRequestId).ConfigureAwait(false);
            var pastExpiry = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O", CultureInfo.InvariantCulture);
            var expiredHandoff = Path.Combine(fixture.OutputDirectory, "s4t1-expired.grant-handoff");
            var expiredApprove = await RunOwnerCommandWithInputAsync(connectionsExecutable, "APPROVE" + Environment.NewLine,
                "owner", "request-approve", "--control-pipe", fixture.ControlPipe,
                "--request-id", expiryRequestId, "--preview-id", expiryPreviewId,
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read",
                "--expires-at-utc", pastExpiry, "--handoff-file", expiredHandoff).ConfigureAwait(false);
            Require(expiredApprove.ExitCode == 3 && expiredApprove.StandardError.Contains("request_invalid", StringComparison.Ordinal) &&
                    !File.Exists(expiredHandoff),
                "A past-expiry S4-T1 approval did not fail closed with a structured deny (got exit=" + expiredApprove.ExitCode + " stderr=" + expiredApprove.StandardError.Trim() + ").");
            var survival = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "requests", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(survival.ExitCode == 0 && survival.StandardOutput.Contains($"REQUEST id={expiryRequestId}", StringComparison.Ordinal),
                "The owner channel did not survive a past-expiry S4-T1 approval (got exit=" + survival.ExitCode + " stderr=" + survival.StandardError.Trim() + ").");

            var sessions = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "sessions", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(sessions.ExitCode == 0 && sessions.StandardOutput.Contains($"SESSION id={sessionIdB}", StringComparison.Ordinal),
                "The owner session list did not show the S4-T1 session.");
            var revoke = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "revoke-session", "--control-pipe", fixture.ControlPipe,
                "--session-id", sessionIdB).ConfigureAwait(false);
            Require(revoke.ExitCode == 0, "The owner could not revoke the S4-T1 session.");
            var revokedRead = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_scoped_read",
                new { sessionFile = sessionB, operation = "resource.read", resourceId = SmokeFixture.ResourceA, resourceRevision = "2" }).ConfigureAwait(false);
            Require(revokedRead.Contains("resource_unavailable", StringComparison.Ordinal) && !revokedRead.Contains(S4T1BodyA, StringComparison.Ordinal),
                "A revoked S4-T1 session still read.");
            var revokedAuth = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_authorize",
                new { sessionFile = sessionB, operation = "resource.read", resourceId = SmokeFixture.ResourceA, resourceRevision = "2" }).ConfigureAwait(false);
            Require(revokedAuth.Contains("DENY", StringComparison.Ordinal) && !revokedAuth.Contains(S4T1BodyA, StringComparison.Ordinal),
                "A revoked S4-T1 session still authorized.");

            // Owner lock: a fresh session read denies while locked.
            var lockRequest = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_access_request",
                new { catalogIds = S4T1CatalogA, purpose = S4T1PurposeA, operations = "resource.read", agentId = "synthetic-s4t1-lock" }).ConfigureAwait(false);
            var lockRequestId = ParseBridgeRequestId(lockRequest);
            var lockPreviewId = await TakeRequestPreviewIdAsync(connectionsExecutable, fixture, lockRequestId).ConfigureAwait(false);
            var lockHandoff = Path.Combine(fixture.OutputDirectory, "s4t1-lock.grant-handoff");
            var lockApprove = await RunOwnerCommandWithInputAsync(connectionsExecutable, "APPROVE" + Environment.NewLine,
                "owner", "request-approve", "--control-pipe", fixture.ControlPipe,
                "--request-id", lockRequestId, "--preview-id", lockPreviewId,
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read",
                "--expires-at-utc", expiry, "--handoff-file", lockHandoff).ConfigureAwait(false);
            Require(lockApprove.ExitCode == 0, "The owner could not approve the S4-T1 lock-scope grant.");
            var lockSession = Path.Combine(fixture.OutputDirectory, "s4t1-lock.session-handoff");
            var lockRedeem = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_redeem", new { handoffFile = lockHandoff, sessionFile = lockSession }).ConfigureAwait(false);
            Require(lockRedeem.Contains("SESSION issued", StringComparison.Ordinal), "The S4-T1 lock-scope grant did not redeem.");

            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The owner could not lock after the S4-T1 walkthrough.");
            var lockedResult = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_scoped_read",
                new { sessionFile = lockSession, operation = "resource.read", resourceId = SmokeFixture.ResourceA, resourceRevision = "2" }).ConfigureAwait(false);
            Require(lockedResult.Contains("resource_unavailable", StringComparison.Ordinal) && !lockedResult.Contains(S4T1BodyA, StringComparison.Ordinal),
                "A locked vault served an S4-T1 bridge read.");

            Console.WriteLine("PASS S4-T1 bridge negotiates client protocol versions with a documented fallback");
            Console.WriteLine("PASS S4-T1 bridge advertises the T1 capability surface with no bearer material");
            Console.WriteLine("PASS S4-T1 bridge catalog/request/redeem/read/authorize/inspect/revoke/lock walkthrough over the same stdio bridge");
            Console.WriteLine("PASS S4-T1 bridge replay/unknown-tool/bearer/malformed/method negatives fail closed");
            Console.WriteLine("PASS fixture processes, profiles, and protected files were cleaned up");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteS4T1BodyAsync(string executable, SmokeFixture fixture, string resourceId, string marker, long writeRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s4t1-body-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = Encoding.UTF8.GetBytes($"synthetic bridge fixture body; marker={marker}; never a real owner secret.");
        await File.WriteAllBytesAsync(input, bytes).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(bytes);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "write", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", SmokeFixture.Zone,
            "--expected-revision", writeRevision.ToString(CultureInfo.InvariantCulture),
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0, "The S4-T1 fixture could not store its synthetic private body.");
        File.Delete(input);
    }

    private static async Task PublishS4T1CatalogAsync(
        string executable, SmokeFixture fixture, string catalogId, string label, string description, string resourceId)
    {
        var result = await RunOwnerCommandWithInputAsync(executable, "PUBLISH\n",
            "owner", "catalog-publish", "--control-pipe", fixture.ControlPipe,
            "--catalog-id", catalogId, "--label", label, "--description", description,
            "--resource-id", resourceId).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"CATALOG published id={catalogId}", StringComparison.Ordinal),
            "The S4-T1 fixture could not publish its synthetic catalog entry.");
    }
}
