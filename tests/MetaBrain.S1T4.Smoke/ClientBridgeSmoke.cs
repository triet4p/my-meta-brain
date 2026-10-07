using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string BridgeCatalogA = "owner-approved-bridge-a";
    private const string BridgeCatalogB = "owner-approved-bridge-b";
    private const string BridgeLabelA = "Approved synthetic bridge lesson A";
    private const string BridgeDescriptionA = "Owner-written bridge summary A; never derived from private text.";
    private const string BridgeLabelB = "Approved synthetic bridge lesson B";
    private const string BridgeDescriptionB = "Owner-written bridge summary B; never derived from private text.";
    private const string BridgeBodyA = "synthetic-bridge-private-body-a1b2";
    private const string BridgeBodyB = "synthetic-bridge-private-body-c3d4";
    private const string BridgePurposeA = "synthetic bridge purpose alpha";
    private const string BridgePurposeB = "synthetic bridge purpose beta";

    internal static async Task RunT9BridgeAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S1-T9 bridge smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S1-T9 MCP bridge and headless owner workflow");
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
                "The real owner CLI did not initialize the synthetic bridge fixture.");

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var unlocked = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The owner could not unlock the isolated bridge fixture.");

            await WriteBridgeBodyAsync(connectionsExecutable, fixture, SmokeFixture.ResourceA, BridgeBodyA).ConfigureAwait(false);
            await WriteBridgeBodyAsync(connectionsExecutable, fixture, SmokeFixture.ResourceB, BridgeBodyB).ConfigureAwait(false);
            await PublishBridgeCatalogAsync(connectionsExecutable, fixture, BridgeCatalogA, BridgeLabelA, BridgeDescriptionA, SmokeFixture.ResourceA).ConfigureAwait(false);
            await PublishBridgeCatalogAsync(connectionsExecutable, fixture, BridgeCatalogB, BridgeLabelB, BridgeDescriptionB, SmokeFixture.ResourceB).ConfigureAwait(false);
            var bridgeList = await RunBridgeToolsListAsync(connectionsExecutable, fixture.AgentPipe).ConfigureAwait(false);
            Require(bridgeList.Contains("metabrain_catalog_list", StringComparison.Ordinal) &&
                    bridgeList.Contains("metabrain_access_request", StringComparison.Ordinal) &&
                    bridgeList.Contains("metabrain_redeem", StringComparison.Ordinal) &&
                    bridgeList.Contains("metabrain_scoped_read", StringComparison.Ordinal) &&
                    bridgeList.Contains("metabrain_session_inspect", StringComparison.Ordinal) &&
                    bridgeList.Contains("metabrain_request_status", StringComparison.Ordinal),
                "The MCP bridge did not advertise the agent-only tool surface.");
            Require(!bridgeList.Contains("mb1_", StringComparison.Ordinal) && !bridgeList.Contains("mbs1_", StringComparison.Ordinal),
                "The bridge tool list exposed bearer material.");

            // Same bridge: catalog list over the agent pipe shows only approved triplets.
            var catalogOut = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_catalog_list", new { }).ConfigureAwait(false);
            Require(catalogOut.Contains(BridgeCatalogA, StringComparison.Ordinal) &&
                    catalogOut.Contains(BridgeCatalogB, StringComparison.Ordinal) &&
                    !catalogOut.Contains(SmokeFixture.ResourceA, StringComparison.Ordinal) &&
                    !catalogOut.Contains(SmokeFixture.ResourceB, StringComparison.Ordinal) &&
                    !catalogOut.Contains(BridgeBodyA, StringComparison.Ordinal) &&
                    !catalogOut.Contains(BridgeBodyB, StringComparison.Ordinal),
                "Bridge catalog discovery leaked a private mapping or body.");

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

            // Same bridge: access request returns an opaque receipt only.
            var requestOut = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_access_request",
                new { catalogIds = BridgeCatalogA, purpose = BridgePurposeA, operations = "resource.read", agentId = "synthetic-bridge-alpha" }).ConfigureAwait(false);
            Require(requestOut.Contains("REQUEST submitted id=", StringComparison.Ordinal) &&
                    !requestOut.Contains(BridgeBodyA, StringComparison.Ordinal) &&
                    !requestOut.Contains(SmokeFixture.ResourceA, StringComparison.Ordinal) &&
                    !requestOut.Contains("mb1_", StringComparison.Ordinal) &&
                    !requestOut.Contains("mbs1_", StringComparison.Ordinal),
                "The bridge access request disclosed a private body, ID, or bearer.");
            var requestId = ParseBridgeRequestId(requestOut);

            // Owner CLI (piped stdin confirms, matching existing smoke convention): preview is
            // redirect-denied; list shows pending; reject path covered on a second request.
            var requestB = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_access_request",
                new { catalogIds = BridgeCatalogA + "," + BridgeCatalogB, purpose = BridgePurposeB, operations = "resource.read", agentId = "synthetic-bridge-beta" }).ConfigureAwait(false);
            var requestIdB = ParseBridgeRequestId(requestB);
            var listed = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "requests", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(listed.ExitCode == 0 && listed.StandardOutput.Contains($"REQUEST id={requestId}", StringComparison.Ordinal) &&
                    listed.StandardOutput.Contains($"REQUEST id={requestIdB}", StringComparison.Ordinal),
                "The owner request list did not expose both bridge requests.");

            var previewDenied = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "request-preview", "--control-pipe", fixture.ControlPipe,
                "--request-id", requestId).ConfigureAwait(false);
            Require(previewDenied.ExitCode == 3 && previewDenied.StandardError.Contains("preview_redirect_denied", StringComparison.Ordinal),
                "The redirected owner preview did not fail closed on the headless bridge path.");

            // Narrow approve request B to resource A only via piped APPROVE; reject path on request A.
            // The approval binds a server-side preview snapshot (same authority as the
            // console preview; the attached-console body display is T8-accepted and not
            // re-proven here because this headless client path refuses redirected bodies).
            var previewIdB = await TakeRequestPreviewIdAsync(connectionsExecutable, fixture, requestIdB).ConfigureAwait(false);
            var expiry = DateTimeOffset.UtcNow.AddMinutes(20).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            var handoffB = Path.Combine(fixture.OutputDirectory, "bridge-b.grant-handoff");
            var approveB = await RunOwnerCommandWithInputAsync(connectionsExecutable, "APPROVE" + Environment.NewLine,
                "owner", "request-approve", "--control-pipe", fixture.ControlPipe,
                "--request-id", requestIdB, "--preview-id", previewIdB,
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read",
                "--expires-at-utc", expiry, "--handoff-file", handoffB).ConfigureAwait(false);
            Require(approveB.ExitCode == 0 && approveB.StandardOutput.Contains("REQUEST approved", StringComparison.Ordinal) &&
                    !approveB.StandardOutput.Contains("mb1_", StringComparison.Ordinal) && File.Exists(handoffB),
                "The owner could not narrow-approve the second bridge request.");
            VerifyOwnerHandoffAcl(handoffB);

            var rejectA = await RunOwnerCommandWithInputAsync(connectionsExecutable, "REJECT" + Environment.NewLine,
                "owner", "request-reject", "--control-pipe", fixture.ControlPipe,
                "--request-id", requestId).ConfigureAwait(false);
            Require(rejectA.ExitCode == 0 && rejectA.StandardOutput.Contains($"REQUEST rejected id={requestId}", StringComparison.Ordinal),
                "The owner could not reject the first bridge request.");

            // Same bridge: redeem handoff path (no bearer in args) then inspect + scoped read.
            var sessionB = Path.Combine(fixture.OutputDirectory, "bridge-b.session-handoff");
            var redeemOut = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_redeem", new { handoffFile = handoffB, sessionFile = sessionB }).ConfigureAwait(false);
            Require(redeemOut.Contains("SESSION issued", StringComparison.Ordinal) &&
                    !redeemOut.Contains("mb1_", StringComparison.Ordinal) &&
                    !redeemOut.Contains("mbs1_", StringComparison.Ordinal) && File.Exists(sessionB),
                "The bridge redeem did not issue a session from the DPAPI handoff path.");
            VerifyOwnerHandoffAcl(sessionB);
            var sessionIdB = ParseBridgeSessionId(redeemOut);

            var inspectOut = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_session_inspect", new { sessionFile = sessionB }).ConfigureAwait(false);
            Require(inspectOut.Contains($"SESSION id={sessionIdB}", StringComparison.Ordinal),
                "The bridge session inspect did not report the issued session.");

            var scopedOut = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_scoped_read",
                new { sessionFile = sessionB, operation = "resource.read", resourceId = SmokeFixture.ResourceA, resourceRevision = "2" }).ConfigureAwait(false);
            Require(scopedOut.Contains(BridgeBodyA, StringComparison.Ordinal) &&
                    !scopedOut.Contains("READ content bytes=", StringComparison.Ordinal) &&
                    !scopedOut.Contains("mb1_", StringComparison.Ordinal) &&
                    !scopedOut.Contains("mbs1_", StringComparison.Ordinal),
                "The bridge scoped read did not deliver the exact approved body in memory.");
            Require(!File.Exists(Path.Combine(fixture.OutputDirectory, "bridge-b.read.bin")),
                "The default bridge read persisted a private plaintext file.");
            var deniedOut = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_scoped_read",
                new { sessionFile = sessionB, operation = "resource.read", resourceId = SmokeFixture.ResourceB, resourceRevision = "2" }).ConfigureAwait(false);
            Require(deniedOut.Contains("resource_unavailable", StringComparison.Ordinal) && !deniedOut.Contains(BridgeBodyB, StringComparison.Ordinal),
                "A narrowed/excluded bridge read was served.");
            // Raw agent-read stdout path streams the same approved body without files.
            var binaryCli = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", sessionB, "--operation", "resource.read",
                "--resource-id", SmokeFixture.ResourceA, "--resource-revision", "2").ConfigureAwait(false);
            Require(binaryCli.ExitCode == 0 && binaryCli.StandardOutput.Contains("AGENT-BODY-BEGIN", StringComparison.Ordinal) &&
                    binaryCli.StandardOutput.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes($"synthetic bridge fixture body; marker={BridgeBodyA}; never a real owner secret.")), StringComparison.Ordinal),
                "The agent-read stdout path did not stream the exact approved body.");
            // Owner inspect/revoke + lock: same bridge read denies after revoke and after lock.
            // Binary boundary first: store non-UTF8 revision 3, then a fresh request on
            // catalog A resolves it and proves the bridge carries it losslessly as base64.
            await WriteBridgeBinaryBodyAsync(connectionsExecutable, fixture, SmokeFixture.ResourceA).ConfigureAwait(false);
            var binaryRequest = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_access_request",
                new { catalogIds = BridgeCatalogA, purpose = BridgePurposeB, operations = "resource.read", agentId = "synthetic-bridge-binary" }).ConfigureAwait(false);
            var binaryRequestId = ParseBridgeRequestId(binaryRequest);
            var binaryPreviewId = await TakeRequestPreviewIdAsync(connectionsExecutable, fixture, binaryRequestId).ConfigureAwait(false);
            var binaryHandoff = Path.Combine(fixture.OutputDirectory, "bridge-binary.grant-handoff");
            var binaryApprove = await RunOwnerCommandWithInputAsync(connectionsExecutable, "APPROVE" + Environment.NewLine,
                "owner", "request-approve", "--control-pipe", fixture.ControlPipe,
                "--request-id", binaryRequestId, "--preview-id", binaryPreviewId,
                "--resource-ids", SmokeFixture.ResourceA, "--operations", "resource.read",
                "--expires-at-utc", expiry, "--handoff-file", binaryHandoff).ConfigureAwait(false);
            Require(binaryApprove.ExitCode == 0, "The owner could not approve the binary revision grant.");
            var binarySession = Path.Combine(fixture.OutputDirectory, "bridge-binary.session-handoff");
            var binaryRedeem = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_redeem", new { handoffFile = binaryHandoff, sessionFile = binarySession }).ConfigureAwait(false);
            Require(binaryRedeem.Contains("SESSION issued", StringComparison.Ordinal), "The binary grant did not redeem.");
            var binaryOut = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_scoped_read",
                new { sessionFile = binarySession, operation = "resource.read", resourceId = SmokeFixture.ResourceA, resourceRevision = "3" }).ConfigureAwait(false);
            Require(binaryOut.Contains("BINARY-NONUTF8", StringComparison.Ordinal) &&
                    binaryOut.Contains(Convert.ToBase64String(new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x41, 0x42, 0x43, 0x44 }), StringComparison.Ordinal),
                "An authorized binary body was denied or mislabeled on the bridge read path.");
            var sessions = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "sessions", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(sessions.ExitCode == 0 && sessions.StandardOutput.Contains($"SESSION id={sessionIdB}", StringComparison.Ordinal),
                "The owner session list did not show the bridge session.");
            var revoke = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "revoke-session", "--control-pipe", fixture.ControlPipe,
                "--session-id", sessionIdB).ConfigureAwait(false);
            Require(revoke.ExitCode == 0, "The owner could not revoke the bridge session.");
            var revokedRead = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_scoped_read",
                new { sessionFile = sessionB, operation = "resource.read", resourceId = SmokeFixture.ResourceA, resourceRevision = "2" }).ConfigureAwait(false);
            Require(revokedRead.Contains("resource_unavailable", StringComparison.Ordinal) && !revokedRead.Contains(BridgeBodyA, StringComparison.Ordinal),
                "A revoked bridge session still read.");

            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The owner could not lock after the bridge walkthrough.");
            var lockedResult = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_scoped_read",
                new { sessionFile = sessionB, operation = "resource.read", resourceId = SmokeFixture.ResourceA, resourceRevision = "2" }).ConfigureAwait(false);
            Require(lockedResult.Contains("resource_unavailable", StringComparison.Ordinal) && !lockedResult.Contains(BridgeBodyA, StringComparison.Ordinal),
                "A locked vault served a bridge read.");

            Console.WriteLine("PASS S1-T9 bridge advertises the agent-only tool surface with no bearer material");
            Console.WriteLine("PASS S1-T9 bridge catalog/request/redeem/read/inspect/revoke/lock walkthrough over the same stdio bridge");
            Console.WriteLine("PASS S1-T9 bridge unknown-tool/bearer/malformed/owner-op negatives fail closed");
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

    private static async Task WriteBridgeBodyAsync(string executable, SmokeFixture fixture, string resourceId, string marker)
    {
        var input = Path.Combine(fixture.OutputDirectory, "bridge-body-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = Encoding.UTF8.GetBytes($"synthetic bridge fixture body; marker={marker}; never a real owner secret.");
        await File.WriteAllBytesAsync(input, bytes).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(bytes);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "write", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", SmokeFixture.Zone,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0, "The bridge fixture could not store its synthetic private body.");
        File.Delete(input);
    }

    private static async Task WriteBridgeBinaryBodyAsync(string executable, SmokeFixture fixture, string resourceId)
    {
        var input = Path.Combine(fixture.OutputDirectory, "bridge-binary-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x41, 0x42, 0x43, 0x44 };
        await File.WriteAllBytesAsync(input, bytes).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(bytes);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "write", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", SmokeFixture.Zone,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("WRITE stored revision=3", StringComparison.Ordinal),
            "The bridge fixture could not store its synthetic binary revision.");
        File.Delete(input);
    }

    private static async Task PublishBridgeCatalogAsync(
        string executable, SmokeFixture fixture, string catalogId, string label, string description, string resourceId)
    {
        var result = await RunOwnerCommandWithInputAsync(executable, "PUBLISH\n",
            "owner", "catalog-publish", "--control-pipe", fixture.ControlPipe,
            "--catalog-id", catalogId, "--label", label, "--description", description,
            "--resource-id", resourceId).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"CATALOG published id={catalogId}", StringComparison.Ordinal),
            "The bridge fixture could not publish its synthetic catalog entry.");
    }

    private static async Task<string> TakeRequestPreviewIdAsync(string executable, SmokeFixture fixture, string requestId)
    {
        var preview = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
        {
            protocolVersion = 1,
            operation = "owner.access.request.preview",
            requestId,
        }).ConfigureAwait(false);
        Require(ReplyCode(preview) == "access_request_preview", "The owner could not preview the bridge request over the owner pipe.");
        var previewId = preview.GetProperty("accessRequestPreview").GetProperty("previewId").GetString();
        Require(Guid.TryParseExact(previewId, "N", out _), "The bridge preview ID was not opaque.");
        return previewId!;
    }


    private static string ParseBridgeRequestId(string output)
    {
        const string prefix = "REQUEST submitted id=";
        var start = output.IndexOf(prefix, StringComparison.Ordinal);
        Require(start >= 0, "The bridge request did not return an opaque receipt.");
        start += prefix.Length;
        var end = output.IndexOf(';', start);
        Require(end > start, "The bridge request receipt was incomplete.");
        return output[start..end];
    }

    private static string ParseBridgeSessionId(string output)
    {
        const string prefix = "SESSION id=";
        var start = output.IndexOf(prefix, StringComparison.Ordinal);
        Require(start >= 0, "The bridge redeem did not report its session identifier.");
        start += prefix.Length;
        var end = output.IndexOfAny(new[] { ' ', '\r', '\n', ';' }, start);
        return (end < 0 ? output[start..] : output[start..end]).Trim();
    }

    private static async Task<string> RunBridgeToolsListAsync(string executable, string agentPipe)
    {
        var raw = await RunBridgeRawAsync(executable, agentPipe,
            """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""").ConfigureAwait(false);
        return raw;
    }

    private static async Task<string> RunBridgeToolAsync(string executable, string agentPipe, string name, object args)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 7,
            method = "tools/call",
            @params = new { name, arguments = args },
        });
        return await RunBridgeRawAsync(executable, agentPipe, payload).ConfigureAwait(false);
    }

    private static async Task<string> RunBridgeRawAsync(string executable, string agentPipe, string line)
    {
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("agent-mcp");
        start.ArgumentList.Add("--agent-pipe");
        start.ArgumentList.Add(agentPipe);
        using var process = new System.Diagnostics.Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("The MCP bridge process did not start.");
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch
            {
            }

            throw;
        }

        Require(process.ExitCode == 0, "The MCP bridge process did not exit cleanly.");
        var text = await output.ConfigureAwait(false);
        var errText = await error.ConfigureAwait(false);
        Require(string.IsNullOrWhiteSpace(errText), "The MCP bridge wrote unexpected stderr.");
        return text;
    }
}
