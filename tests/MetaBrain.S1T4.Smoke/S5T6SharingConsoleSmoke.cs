using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.OwnerUi;

namespace MetaBrain.S1T4.Smoke;

// S5-T6: the catalog/access-request/session console. One synthetic vault is
// provisioned and unlocked through the real owner CLI; catalog publication,
// pending requests, owner narrow/approve/reject, token redemption, scoped
// reads and session revoke/lock flow through the console session the WPF
// window drives, the real owner CLI, the real agent channel and the real MCP
// bridge. The stale-preview, used-token, unapproved member/revision and
// frozen-scope negatives are the service's own answers. Nothing here is a
// mock, and no assertion is a source-text echo.
internal static partial class OwnerServiceSmoke
{
    private const string S5T6Zone = "s5t6-zone";
    private const string S5T6ItemAlpha = "s5t6-item-alpha";
    private const string S5T6ItemBeta = "s5t6-item-beta";
    private const string S5T6CatalogAlpha = "s5t6-catalog-alpha";
    private const string S5T6CatalogBeta = "s5t6-catalog-beta";
    private const string S5T6LabelAlpha = "Synthetic S5-T6 alpha lesson";
    private const string S5T6LabelBeta = "Synthetic S5-T6 beta lesson";
    private const string S5T6DescriptionAlpha = "Owner-written S5-T6 summary alpha; never derived from private text.";
    private const string S5T6DescriptionBeta = "Owner-written S5-T6 summary beta; never derived from private text.";
    private const string S5T6PrivateTitle = "synthetic-s5t6-private-title-3a91";
    private const string S5T6PrivateBodyAlpha = "s5t6-synthetic-private-body-alpha-7d44";
    private const string S5T6PrivateBodyBeta = "s5t6-synthetic-private-body-beta-2e19";
    private const string S5T6RevisedBodyBeta = "s5t6-synthetic-revised-body-beta-9c07";
    private const string S5T6Marker = "s5t6-synthetic-sharing-marker-4f60";

    private static string S5T6ItemDraft(string title, string marker, DateTimeOffset now) =>
        S5T4ItemDraft("claim", "user", "directWrite", "unverified", "l1", title,
            "S5-T6 synthetic body " + marker + " " + S5T6Marker, now);

    /// <summary>
    /// Runs the S5-T6 sharing scenario: catalog preview/publish/withdraw, the
    /// pending request with purpose, owner narrow/reject/approve with the
    /// local token handoff, connector redeem/read, revoke/lock/deny and every
    /// applicable stale/used/unapproved/frozen-scope negative.
    /// </summary>
    internal static async Task RunS5T6SharingAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S5-T6 sharing console smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S5-T6 catalog/access-request/session console: publish, request, narrow/approve/reject, redeem, revoke and negatives");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        var knownBearers = new List<string>();
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "Provision did not create the synthetic S5-T6 vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");

            var now = DateTimeOffset.UtcNow.AddMinutes(-5);
            await S5T4WriteItemAsync(connectionsExecutable, fixture.ControlPipe, fixture.OutputDirectory, S5T6ItemAlpha, S5T6Zone,
                S5T6ItemDraft(S5T6PrivateTitle, S5T6PrivateBodyAlpha, now)).ConfigureAwait(false);
            await S5T4WriteItemAsync(connectionsExecutable, fixture.ControlPipe, fixture.OutputDirectory, S5T6ItemBeta, S5T6Zone,
                S5T6ItemDraft(S5T6PrivateTitle, S5T6PrivateBodyBeta, now)).ConfigureAwait(false);

            var session = new OwnerConsoleSession(fixture.ControlPipe);
            await session.AttachAsync().ConfigureAwait(false);
            Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
                "The console session did not report the real unlocked vault (" + session.ReportedVaultState + ").");

            // ---- discovery knows nothing before publication ----
            var emptyDiscovery = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "catalog.list",
            }).ConfigureAwait(false);
            Require(ReplyCode(emptyDiscovery) == "catalog" &&
                    !emptyDiscovery.GetRawText().Contains(S5T6CatalogAlpha, StringComparison.Ordinal),
                "Empty discovery did not return the approved public envelope (" + ReplyCode(emptyDiscovery) + ").");

            // ---- publication preview mutates nothing and autofills nothing ----
            await session.PreviewCatalogAsync(S5T6CatalogAlpha, S5T6LabelAlpha, S5T6DescriptionAlpha, S5T6ItemAlpha).ConfigureAwait(false);
            Require(session.CatalogPreview is { } catalogPreview &&
                    string.Equals(catalogPreview.CatalogId, S5T6CatalogAlpha, StringComparison.Ordinal) &&
                    string.Equals(catalogPreview.Label, S5T6LabelAlpha, StringComparison.Ordinal) &&
                    string.Equals(catalogPreview.Description, S5T6DescriptionAlpha, StringComparison.Ordinal) &&
                    string.Equals(catalogPreview.ResourceId, S5T6ItemAlpha, StringComparison.Ordinal) &&
                    catalogPreview.ResourceRevision == 1 &&
                    !catalogPreview.Label.Contains(S5T6PrivateTitle, StringComparison.Ordinal) &&
                    !catalogPreview.Description.Contains(S5T6ItemAlpha, StringComparison.Ordinal),
                "The console did not preview the exact owner-typed metadata with the live revision (" + session.CatalogWriteMessage + ").");
            var stillEmpty = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "catalog.list",
            }).ConfigureAwait(false);
            Require(ReplyCode(stillEmpty) == "catalog" &&
                    !stillEmpty.GetRawText().Contains(S5T6CatalogAlpha, StringComparison.Ordinal),
                "A publication preview changed public discovery (" + ReplyCode(stillEmpty) + ").");
            Console.WriteLine("PASS the publication preview shows the exact owner-typed metadata with the live revision and changes no discovery");

            // ---- the owner publishes through the console ----
            await session.PublishCatalogAsync(S5T6CatalogAlpha, S5T6LabelAlpha, S5T6DescriptionAlpha, S5T6ItemAlpha).ConfigureAwait(false);
            Require(session.CatalogEntries.Any(entry =>
                    string.Equals(entry.CatalogId, S5T6CatalogAlpha, StringComparison.Ordinal) &&
                    string.Equals(entry.Label, S5T6LabelAlpha, StringComparison.Ordinal) &&
                    string.Equals(entry.ResourceId, S5T6ItemAlpha, StringComparison.Ordinal) &&
                    entry.Revision == 1),
                "The console did not republish the private mapping after the write (" + session.CatalogWriteMessage + ").");
            var listedAlpha = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "catalog.list",
            }).ConfigureAwait(false);
            var listedText = listedAlpha.GetRawText();
            Require(ReplyCode(listedAlpha) == "catalog" &&
                    listedText.Contains(S5T6CatalogAlpha, StringComparison.Ordinal) &&
                    listedText.Contains(S5T6LabelAlpha, StringComparison.Ordinal) &&
                    !listedText.Contains(S5T6ItemAlpha, StringComparison.Ordinal) &&
                    !listedText.Contains(S5T6PrivateTitle, StringComparison.Ordinal) &&
                    !listedText.Contains(S5T6PrivateBodyAlpha, StringComparison.Ordinal),
                "Published discovery did not serve exactly the approved triplet (" + ReplyCode(listedAlpha) + ").");
            Console.WriteLine("PASS the owner publishes through the console and discovery serves only the approved ID/label/description");

            // ---- an agent requests through the real MCP bridge: receipt only ----
            var requestCall = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_access_request", new
                {
                    catalogIds = S5T6CatalogAlpha,
                    purpose = "synthetic S5-T6 purpose alpha",
                    operations = "resource.read",
                    agentId = "s5t6-synthetic-agent",
                }).ConfigureAwait(false);
            Require(requestCall.Contains("REQUEST submitted id=", StringComparison.Ordinal) &&
                    !requestCall.Contains(S5T6ItemAlpha, StringComparison.Ordinal) &&
                    !requestCall.Contains(S5T6PrivateBodyAlpha, StringComparison.Ordinal) &&
                    !requestCall.Contains("mb1_", StringComparison.Ordinal),
                "The agent request did not return an opaque receipt (" + requestCall.Trim() + ").");
            var requestId = ParseS5T6RequestId(requestCall);

            await session.RefreshAccessRequestsAsync().ConfigureAwait(false);
            var pending = session.AccessRequests.FirstOrDefault(request =>
                string.Equals(request.RequestId, requestId, StringComparison.Ordinal));
            Require(pending is { Status: "pending" } &&
                    string.Equals(pending.Purpose, "synthetic S5-T6 purpose alpha", StringComparison.Ordinal) &&
                    pending.CatalogIds.Contains(S5T6CatalogAlpha, StringComparer.Ordinal),
                "The owner's request list did not show the pending request with its purpose (" + session.AccessRequestsMessage + ").");
            Console.WriteLine("PASS a real bridge request returns an opaque receipt while the owner sees the pending purpose");

            // ---- the owner binds the request to its frozen snapshot ----
            await session.PreviewAccessRequestAsync(pending!).ConfigureAwait(false);
            var bound = session.OpenRequestPreview;
            Require(bound is not null &&
                    string.Equals(bound.RequestId, requestId, StringComparison.Ordinal) &&
                    bound.PreviewId.Length == 32 &&
                    bound.Resources.Length == 1 &&
                    string.Equals(bound.Resources[0].ResourceId, S5T6ItemAlpha, StringComparison.Ordinal) &&
                    bound.Resources[0].Revision == 1 &&
                    bound.Operations.Contains("resource.read", StringComparer.Ordinal) &&
                    bound.Bodies.Length == 1 &&
                    bound.Bodies[0].Text.Contains(S5T6PrivateBodyAlpha, StringComparison.Ordinal),
                "The console did not bind the exact resource/revision, operations and body (" + session.AccessDecisionMessage + ").");
            Console.WriteLine("PASS the owner preview binds the exact resource/revision, operations and body");

            // ---- growing the scope past the bound preview is refused ----
            var widenHandoff = Path.Combine(fixture.OutputDirectory, "s5t6-widen.handoff");
            await session.ApproveAccessRequestAsync(
                new[] { S5T6ItemAlpha, S5T6ItemBeta }, new[] { "resource.read" }, null,
                DateTimeOffset.UtcNow.AddMinutes(20), null, null, null, widenHandoff).ConfigureAwait(false);
            Require(session.AccessDecisionMessage is { } widenMessage &&
                    widenMessage.Contains("request_invalid", StringComparison.Ordinal) &&
                    !File.Exists(widenHandoff),
                "Owner narrowing enlarged the previewed scope (" + session.AccessDecisionMessage + ").");
            var escalateHandoff = Path.Combine(fixture.OutputDirectory, "s5t6-escalate.handoff");
            await session.ApproveAccessRequestAsync(
                null, new[] { "resource.read", "source.read" }, null,
                DateTimeOffset.UtcNow.AddMinutes(20), null, null, null, escalateHandoff).ConfigureAwait(false);
            Require(session.AccessDecisionMessage is { } escalateMessage &&
                    escalateMessage.Contains("request_invalid", StringComparison.Ordinal) &&
                    !File.Exists(escalateHandoff),
                "Owner approval escalated to an unrequested operation (" + session.AccessDecisionMessage + ").");
            Console.WriteLine("PASS narrowing past the bound preview is refused with no grant minted");

            // ---- a revision change between preview and approval fails closed ----
            var revisionInput = Path.Combine(fixture.OutputDirectory, "s5t6-revise-" + Guid.NewGuid().ToString("N") + ".bin");
            var revisionBytes = Encoding.UTF8.GetBytes("S5-T6 synthetic revised body; marker=" + S5T6RevisedBodyBeta + "; never a real owner secret.");
            await File.WriteAllBytesAsync(revisionInput, revisionBytes).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(revisionBytes);
            var revised = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "write", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S5T6ItemAlpha, "--zone-id", S5T6Zone,
                "--expected-revision", "1", "--input-file", revisionInput).ConfigureAwait(false);
            File.Delete(revisionInput);
            Require(revised.ExitCode == 0 && revised.StandardOutput.Contains("WRITE stored revision=2", StringComparison.Ordinal),
                "The synthetic resource was not revised to revision 2 (exit=" + revised.ExitCode + "; stderr=" + revised.StandardError.Trim() + ").");
            var staleHandoff = Path.Combine(fixture.OutputDirectory, "s5t6-stale.handoff");
            await session.ApproveAccessRequestAsync(
                null, new[] { "resource.read" }, null,
                DateTimeOffset.UtcNow.AddMinutes(20), null, null, null, staleHandoff).ConfigureAwait(false);
            Require(session.AccessDecisionMessage is { } staleMessage &&
                    staleMessage.Contains("request_stale", StringComparison.Ordinal) &&
                    !File.Exists(staleHandoff),
                "A stale preview after a revision change minted a grant (" + session.AccessDecisionMessage + ").");
            Console.WriteLine("PASS a revision change between preview and approval fails closed with a fresh preview required");

            // ---- a fresh preview binds the new revision; approval mints the local handoff ----
            await session.RefreshAccessRequestsAsync().ConfigureAwait(false);
            var stillPending = session.AccessRequests.First(request => string.Equals(request.RequestId, requestId, StringComparison.Ordinal));
            await session.PreviewAccessRequestAsync(stillPending).ConfigureAwait(false);
            Require(session.OpenRequestPreview is { } fresh &&
                    fresh.Resources[0].Revision == 2 &&
                    fresh.Bodies[0].Text.Contains(S5T6RevisedBodyBeta, StringComparison.Ordinal),
                "The fresh preview did not bind the bumped revision and body (" + session.AccessDecisionMessage + ").");
            var handoffPath = Path.Combine(fixture.OutputDirectory, "s5t6-console.grant-handoff");
            await session.ApproveAccessRequestAsync(
                null, new[] { "resource.read" }, null,
                DateTimeOffset.UtcNow.AddMinutes(20), null, null, null, handoffPath).ConfigureAwait(false);
            Require(session.LastGrantId is { Length: 32 } &&
                    string.Equals(session.LastHandoffPath, handoffPath, StringComparison.Ordinal) &&
                    File.Exists(handoffPath) &&
                    session.AccessDecisionMessage is { } approvedMessage &&
                    approvedMessage.Contains(session.LastGrantId, StringComparison.Ordinal) &&
                    !approvedMessage.Contains("mb1_", StringComparison.Ordinal),
                "The console approval did not report the grant with its handoff path and no bearer (" + session.AccessDecisionMessage + ").");
            VerifyOwnerHandoffProtection(handoffPath);
            knownBearers.Add(ReadProtectedHandoffTokenString(handoffPath));
            Console.WriteLine("PASS the owner approves the fresh preview and the bearer lands only in the DPAPI-protected local handoff");

            // ---- a repeated approval of the decided request mints nothing ----
            // The approval above consumed its bound preview, and the chained
            // listing cleared it, so a fresh preview of the decided request is
            // what the service refuses.
            await session.RefreshAccessRequestsAsync().ConfigureAwait(false);
            var decided = session.AccessRequests.First(request => string.Equals(request.RequestId, requestId, StringComparison.Ordinal));
            Require(string.Equals(decided.Status, "approved", StringComparison.Ordinal),
                "The approved request did not report its terminal state (" + session.AccessRequestsMessage + ").");
            var repeatHandoff = Path.Combine(fixture.OutputDirectory, "s5t6-repeat.handoff");
            var repeatPreview = await OwnerChannelClient.PreviewAccessRequestAsync(fixture.ControlPipe, requestId, CancellationToken.None).ConfigureAwait(false);
            Require(repeatPreview.Outcome == OwnerCommandOutcome.Refused &&
                    repeatPreview.ErrorCode == "request_not_pending" && repeatPreview.Preview is null,
                "A fresh preview of a decided request was served (" + repeatPreview.Outcome + "/" + repeatPreview.ErrorCode + ").");
            await session.ApproveAccessRequestAsync(
                null, new[] { "resource.read" }, null,
                DateTimeOffset.UtcNow.AddMinutes(20), null, null, null, repeatHandoff).ConfigureAwait(false);
            Require(!File.Exists(repeatHandoff),
                "A repeated approval of a decided request minted a duplicate grant (" + session.AccessDecisionMessage + ").");

            // ---- the connector redeems and reads through the real agent channel ----
            var sessionFile = Path.Combine(fixture.OutputDirectory, "s5t6-console.session-handoff");
            var redeemed = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                "--handoff-file", handoffPath, "--session-file", sessionFile).ConfigureAwait(false);
            Require(redeemed.ExitCode == 0 && redeemed.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
                "The connector could not redeem the console handoff (exit=" + redeemed.ExitCode + "; stderr=" + redeemed.StandardError.Trim() + ").");
            VerifyOwnerHandoffProtection(sessionFile);
            var readBack = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_scoped_read", new
                {
                    sessionFile,
                    operation = "resource.read",
                    resourceId = S5T6ItemAlpha,
                    resourceRevision = "2",
                }).ConfigureAwait(false);
            Require(!readBack.Contains("\"isError\":true", StringComparison.Ordinal) &&
                    readBack.Contains(S5T6RevisedBodyBeta, StringComparison.Ordinal),
                "The redeemed connector session did not read its exact approved body (" + readBack.Trim() + ").");
            await session.RefreshSessionsAsync().ConfigureAwait(false);
            var live = session.AgentSessions.FirstOrDefault(entry =>
                entry.Resources.Any(resource => string.Equals(resource.ResourceId, S5T6ItemAlpha, StringComparison.Ordinal)));
            Require(live is not null &&
                    live.Resources.Length == 1 && live.Resources[0].Revision == 2 &&
                    live.Operations.Contains("resource.read", StringComparer.Ordinal) &&
                    live.ExpiresAtUtc > DateTimeOffset.UtcNow,
                "The session console did not show the effective frozen scope with its expiry (" + session.SessionsMessage + ").");
            Console.WriteLine("PASS the connector redeems the console handoff, reads its exact body, and the session console shows the frozen scope");

            // ---- already-used token replays fail closed ----
            var replayFile = Path.Combine(fixture.OutputDirectory, "s5t6-replay.session-handoff");
            var replayed = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                "--handoff-file", handoffPath, "--session-file", replayFile).ConfigureAwait(false);
            Require(replayed.ExitCode != 0 &&
                    (replayed.StandardError.Contains("token_used", StringComparison.Ordinal) ||
                     replayed.StandardOutput.Contains("token_used", StringComparison.Ordinal)) &&
                    !File.Exists(replayFile),
                "An already-used token redeemed a second session (exit=" + replayed.ExitCode + ").");
            Console.WriteLine("PASS an already-used token never redeems twice");

            // ---- unapproved member, revision, raw and provider refusals ----
            var deniedMember = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_scoped_read", new
                {
                    sessionFile,
                    operation = "resource.read",
                    resourceId = S5T6ItemBeta,
                    resourceRevision = "1",
                }).ConfigureAwait(false);
            Require(deniedMember.Contains("\"isError\":true", StringComparison.Ordinal) &&
                    deniedMember.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !deniedMember.Contains(S5T6PrivateBodyBeta, StringComparison.Ordinal),
                "An unapproved member read was served (" + deniedMember.Trim() + ").");
            var deniedRevision = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_scoped_read", new
                {
                    sessionFile,
                    operation = "resource.read",
                    resourceId = S5T6ItemAlpha,
                    resourceRevision = "1",
                }).ConfigureAwait(false);
            Require(deniedRevision.Contains("\"isError\":true", StringComparison.Ordinal) &&
                    deniedRevision.Contains("resource_unavailable", StringComparison.Ordinal),
                "A superseded revision read was served (" + deniedRevision.Trim() + ").");
            var deniedRaw = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.access.authorize",
                sessionToken = ReadSessionBearer(sessionFile),
                accessOperation = "source.read",
                resourceId = S5T6ItemBeta,
                resourceRevision = 1,
            }).ConfigureAwait(false);
            Require(ReplyCode(deniedRaw) == "authorization_decision" &&
                    deniedRaw.GetProperty("decision").GetProperty("allowed").GetBoolean() == false &&
                    deniedRaw.GetProperty("decision").GetProperty("reason").GetString() == "resource_unavailable",
                "An unapproved source expansion was authorized (" + deniedRaw.GetRawText() + ").");
            var deniedProvider = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.access.authorize",
                sessionToken = ReadSessionBearer(sessionFile),
                accessOperation = "provider.egress",
                provider = "synthetic-provider",
                model = "synthetic-model",
                estimatedCostUsd = 0.10m,
                inputTokens = 1L,
                outputTokens = 1L,
            }).ConfigureAwait(false);
            Require(ReplyCode(deniedProvider) == "authorization_decision" &&
                    deniedProvider.GetProperty("decision").GetProperty("allowed").GetBoolean() == false,
                "An ungranted provider disclosure was authorized (" + deniedProvider.GetRawText() + ").");

            // ---- the owner publishes the sibling, then the catalog withdrawal is observed ----
            await session.PublishCatalogAsync(S5T6CatalogBeta, S5T6LabelBeta, S5T6DescriptionBeta, S5T6ItemBeta).ConfigureAwait(false);
            var bothListed = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "catalog.list",
            }).ConfigureAwait(false);
            Require(ReplyCode(bothListed) == "catalog" &&
                    bothListed.GetRawText().Contains(S5T6CatalogBeta, StringComparison.Ordinal),
                "The sibling publication did not reach discovery (" + ReplyCode(bothListed) + ").");
            var betaEntry = session.CatalogEntries.First(entry => string.Equals(entry.CatalogId, S5T6CatalogBeta, StringComparison.Ordinal));
            Require(betaEntry.Revision == 1, "The sibling mapping did not pin revision 1.");
            await session.WithdrawCatalogAsync(S5T6CatalogBeta).ConfigureAwait(false);
            var afterWithdraw = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "catalog.list",
            }).ConfigureAwait(false);
            var afterWithdrawText = afterWithdraw.GetRawText();
            Require(ReplyCode(afterWithdraw) == "catalog" &&
                    afterWithdrawText.Contains(S5T6CatalogAlpha, StringComparison.Ordinal) &&
                    !afterWithdrawText.Contains(S5T6CatalogBeta, StringComparison.Ordinal),
                "Withdrawal did not converge discovery to no-match for the entry (" + ReplyCode(afterWithdraw) + ").");
            var withdrawnQuery = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "catalog.query",
                query = "S5-T6 beta",
            }).ConfigureAwait(false);
            Require(ReplyCode(withdrawnQuery) == "no_match", "A withdrawn entry still matched discovery queries.");
            Console.WriteLine("PASS unapproved member/revision/raw/provider reads refuse, and the catalog withdrawal is observed on discovery");

            // ---- added members never enter the frozen session scope ----
            var addedInput = Path.Combine(fixture.OutputDirectory, "s5t6-added-" + Guid.NewGuid().ToString("N") + ".bin");
            var addedBytes = Encoding.UTF8.GetBytes("S5-T6 synthetic added body; marker=" + S5T6PrivateBodyBeta + "; never a real owner secret.");
            await File.WriteAllBytesAsync(addedInput, addedBytes).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(addedBytes);
            var addedWrite = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "write", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S5T6ItemBeta, "--zone-id", S5T6Zone,
                "--expected-revision", "1", "--input-file", addedInput).ConfigureAwait(false);
            File.Delete(addedInput);
            Require(addedWrite.ExitCode == 0 && addedWrite.StandardOutput.Contains("WRITE stored revision=2", StringComparison.Ordinal),
                "The sibling revision bump did not persist.");
            await session.RefreshSessionsAsync().ConfigureAwait(false);
            var frozen = session.AgentSessions.First(entry => string.Equals(entry.SessionId, live!.SessionId, StringComparison.Ordinal));
            Require(frozen.Resources.Length == 1 &&
                    string.Equals(frozen.Resources[0].ResourceId, S5T6ItemAlpha, StringComparison.Ordinal) &&
                    frozen.Resources[0].Revision == 2,
                "The frozen session scope moved after an unrelated write (" + session.SessionsMessage + ").");
            var frozenRead = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_scoped_read", new
                {
                    sessionFile,
                    operation = "resource.read",
                    resourceId = S5T6ItemBeta,
                    resourceRevision = "2",
                }).ConfigureAwait(false);
            Require(frozenRead.Contains("\"isError\":true", StringComparison.Ordinal),
                "An added member entered the frozen session scope (" + frozenRead.Trim() + ").");
            Console.WriteLine("PASS added members and revisions never enter the frozen session scope");

            // ---- reject leaves a visible terminal state and mints nothing ----
            var rejectCall = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_access_request", new
                {
                    catalogIds = S5T6CatalogAlpha,
                    purpose = "synthetic S5-T6 purpose rejected",
                    operations = "resource.read",
                }).ConfigureAwait(false);
            var rejectedId = ParseS5T6RequestId(rejectCall);
            await session.RefreshAccessRequestsAsync().ConfigureAwait(false);
            var rejected = session.AccessRequests.First(request => string.Equals(request.RequestId, rejectedId, StringComparison.Ordinal));
            await session.RejectAccessRequestAsync(rejected).ConfigureAwait(false);
            Require(session.AccessRequests.Any(request =>
                    string.Equals(request.RequestId, rejectedId, StringComparison.Ordinal) &&
                    string.Equals(request.Status, "rejected", StringComparison.Ordinal)),
                "The rejected request did not report its terminal state (" + session.AccessDecisionMessage + ").");
            Console.WriteLine("PASS the owner rejects a request and its terminal state is visible with no grant");

            // ---- targeted revoke denies the next read; the session console follows ----
            await session.RevokeSessionAsync(live!).ConfigureAwait(false);
            var afterRevoke = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_scoped_read", new
                {
                    sessionFile,
                    operation = "resource.read",
                    resourceId = S5T6ItemAlpha,
                    resourceRevision = "2",
                }).ConfigureAwait(false);
            Require(afterRevoke.Contains("\"isError\":true", StringComparison.Ordinal) &&
                    !afterRevoke.Contains(S5T6RevisedBodyBeta, StringComparison.Ordinal),
                "A revoked session still read its body (" + afterRevoke.Trim() + ").");
            await session.RefreshSessionsAsync().ConfigureAwait(false);
            Require(!session.AgentSessions.Any(entry => string.Equals(entry.SessionId, live!.SessionId, StringComparison.Ordinal)),
                "The session console still lists the revoked session (" + session.SessionsMessage + ").");
            Console.WriteLine("PASS revoking the live session denies its next read and clears it from the console");

            // ---- agent-channel owner operations stay forbidden ----
            foreach (var ownerOperation in new[]
                     {
                         ServiceRequestHandler.OwnerCatalogPreviewOperation, ServiceRequestHandler.OwnerCatalogPublishOperation,
                         ServiceRequestHandler.OwnerCatalogListOperation, ServiceRequestHandler.OwnerCatalogWithdrawOperation,
                         ServiceRequestHandler.OwnerAccessRequestListOperation, ServiceRequestHandler.OwnerAccessRequestPreviewOperation,
                         ServiceRequestHandler.OwnerAccessRequestApproveOperation, ServiceRequestHandler.OwnerAccessRequestRejectOperation,
                         ServiceRequestHandler.OwnerSessionListOperation, ServiceRequestHandler.OwnerSessionRevokeOperation,
                     })
            {
                var spoofed = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = ownerOperation,
                    isOwner = true,
                    requestId = rejectedId,
                    catalogId = S5T6CatalogAlpha,
                }).ConfigureAwait(false);
                Require(ReplyCode(spoofed) == "forbidden",
                    "The agent channel accepted owner operation " + ownerOperation + " (" + ReplyCode(spoofed) + ").");
            }

            Console.WriteLine("PASS the agent channel denies every owner catalog/request/session operation");

            // ---- the lock boundary ----
            var epochBeforeLock = session.PrivateEpoch;
            var formVersionBeforeLock = session.SharingFormVersion;
            await session.LockAsync().ConfigureAwait(false);
            Require(session.CatalogEntries.Length == 0 && session.CatalogPreview is null &&
                    session.AccessRequests.Length == 0 && session.OpenRequestPreview is null &&
                    session.LastHandoffPath is null && session.AgentSessions.Length == 0 &&
                    session.SharingFormVersion > formVersionBeforeLock &&
                    session.PrivateEpoch != epochBeforeLock,
                "Locking did not clear every sharing view, the bound preview, the handoff display and the drafts.");
            var lateCatalog = new OwnerCatalogListResult(OwnerCommandOutcome.Completed, null, new[]
            {
                new OwnerCatalogMapping(S5T6CatalogAlpha, S5T6LabelAlpha, S5T6DescriptionAlpha, S5T6ItemAlpha, 2, DateTimeOffset.UtcNow),
            });
            Require(!session.ApplyCatalogListAnswer(epochBeforeLock, lateCatalog) && session.CatalogEntries.Length == 0,
                "A late catalog answer restored a view the lock had cleared.");
            var lateRequests = new OwnerAccessRequestListResult(OwnerCommandOutcome.Completed, null, new[] { rejected });
            Require(!session.ApplyAccessRequestListAnswer(epochBeforeLock, lateRequests) && session.AccessRequests.Length == 0,
                "A late request answer restored a view the lock had cleared.");
            var lockedCatalog = await OwnerChannelClient.ListCatalogAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(lockedCatalog.Outcome == OwnerCommandOutcome.Refused && lockedCatalog.ErrorCode == "vault_locked",
                "A locked vault served the owner catalog (" + lockedCatalog.Outcome + "/" + lockedCatalog.ErrorCode + ").");
            var lockedRequests = await OwnerChannelClient.ListAccessRequestsAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(lockedRequests.Outcome == OwnerCommandOutcome.Refused && lockedRequests.ErrorCode == "vault_locked",
                "A locked vault served the access requests (" + lockedRequests.Outcome + "/" + lockedRequests.ErrorCode + ").");
            var lockedSessions = await OwnerChannelClient.ListSessionsAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(lockedSessions.Outcome == OwnerCommandOutcome.Refused,
                "A locked vault served the session list (" + lockedSessions.Outcome + "/" + lockedSessions.ErrorCode + ").");
            await session.RefreshCatalogAsync().ConfigureAwait(false);
            Require(session.CatalogEntries.Length == 0 && session.CatalogMessage is null,
                "The locked console offered a sharing act (" + session.CatalogMessage + ").");
            Console.WriteLine("PASS locking clears every sharing view, the bound preview, the handoff display and the drafts, and a late answer cannot restore them");

            // ---- locked discovery still serves the surviving approved triplet ----
            var lockedDiscovery = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "catalog.list",
            }).ConfigureAwait(false);
            Require(ReplyCode(lockedDiscovery) == "catalog" &&
                    lockedDiscovery.GetRawText().Contains(S5T6CatalogAlpha, StringComparison.Ordinal),
                "Locked discovery did not serve the surviving approved triplet (" + ReplyCode(lockedDiscovery) + ").");

            if (S5T4FixtureHoldsMarker(fixture))
            {
                throw new InvalidOperationException("A synthetic S5-T6 private body marker was persisted in plaintext under the fixture root.");
            }

            Console.WriteLine("PASS no synthetic sharing content was persisted by the console in plaintext");
            Console.WriteLine("PASS S5-T6 console preview/publish, opaque request receipt, bound preview, narrow/approve/reject, handoff redeem/read, revoke/lock/deny and negatives over the real pipes");
            Console.WriteLine("PASS fixture processes, profiles, and protected files were cleaned up");
            Console.WriteLine("LIMIT synthetic S5-T6 fixture only; the catalog entries, requests, handoff and session are synthetic, and the official Codex/OMP client conversation is the separate runtime proof under artifacts/sprint-5/task-6-run01/; same-owner OS compromise remains outside the threat model.");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }

            foreach (var bearer in knownBearers)
            {
                var bytes = Encoding.ASCII.GetBytes(bearer);
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private static string ParseS5T6RequestId(string output)
    {
        const string marker = "REQUEST submitted id=";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        Require(start >= 0, "The bridge request did not return an opaque receipt (" + output.Trim() + ").");
        start += marker.Length;
        var end = output.IndexOf(';', start);
        Require(end > start, "The bridge request receipt was incomplete (" + output.Trim() + ").");
        return output[start..end];
    }

    /// <summary>
    /// Creates the synthetic owner-console fixture at an explicit root for the
    /// external WPF sharing proof. The caller owns that root: it starts and
    /// stops the service, drives the console and removes the root afterwards,
    /// so this scenario disposes nothing and provisions no vault.
    /// </summary>
    internal static Task RunS5T6ConsoleFixtureAsync(string fixtureRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S5-T6 owner console fixture requires Windows named pipes.");
        }

        var fixture = SmokeFixture.CreateUninitializedAt(fixtureRoot);
        Console.WriteLine("S5T6 CONSOLE FIXTURE root=" + fixture.Root);
        Console.WriteLine("S5T6 CONSOLE FIXTURE settings=" + fixture.SettingsPath);
        Console.WriteLine("S5T6 CONSOLE FIXTURE controlPipe=" + fixture.ControlPipe);
        Console.WriteLine("S5T6 CONSOLE FIXTURE agentPipe=" + fixture.AgentPipe);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes the synthetic corpus the sharing proof needs after the console
    /// provisioned the vault: the two memory items the catalog maps. The
    /// caller removes the fixture root afterwards.
    /// </summary>
    internal static async Task S5T6WriteConsoleCorpusAsync(string connectionsExecutable, string fixtureRoot)
    {
        var root = Path.GetFullPath(fixtureRoot);
        var settingsPath = Path.Combine(root, "service", "service-settings.json");
        using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var controlPipe = settings.RootElement.GetProperty("controlPipe").GetString()
            ?? throw new InvalidDataException("The console fixture settings carry no control pipe.");
        var output = Path.Combine(root, "output");
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);

        await S5T4WriteItemAsync(connectionsExecutable, controlPipe, output, S5T6ItemAlpha, S5T6Zone,
            S5T6ItemDraft("Synthetic S5-T6 console alpha", "corpus alpha " + S5T6PrivateBodyAlpha, now)).ConfigureAwait(false);
        await S5T4WriteItemAsync(connectionsExecutable, controlPipe, output, S5T6ItemBeta, S5T6Zone,
            S5T6ItemDraft("Synthetic S5-T6 console beta", "corpus beta " + S5T6PrivateBodyBeta, now)).ConfigureAwait(false);

        Console.WriteLine("S5T6 CONSOLE CORPUS controlPipe=" + controlPipe +
            " alpha=" + S5T6ItemAlpha + " beta=" + S5T6ItemBeta +
            " catalogAlpha=" + S5T6CatalogAlpha + " catalogBeta=" + S5T6CatalogBeta +
            " labelAlpha=" + S5T6LabelAlpha + " labelBeta=" + S5T6LabelBeta +
            " zone=" + S5T6Zone);
    }
}
