using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string RequestCatalogA = "owner-approved-request-a";
    private const string RequestCatalogB = "owner-approved-request-b";
    private const string RequestLabelA = "Approved synthetic request lesson A";
    private const string RequestDescriptionA = "Owner-written request summary A; never derived from private text.";
    private const string RequestLabelB = "Approved synthetic request lesson B";
    private const string RequestDescriptionB = "Owner-written request summary B; never derived from private text.";
    private const string RequestBodyMarkerA = "synthetic-request-private-body-a1b2";
    private const string RequestBodyMarkerB = "synthetic-request-private-body-c3d4";
    private const string RequestRevisionMarker = "synthetic-request-private-revision-e5f6";
    private const string RequestSourceMarker = "synthetic-request-private-source-7890";
    private const string RequestProvider = "synthetic-request-provider";
    private const string RequestModel = "synthetic-request-model";
    private const string RequestDisclosure = "synthetic disclosure context for the declared provider and model";

    internal static async Task RunT8AccessRequestAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S1-T8 request smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S1-T8 access request and owner approval");
        using var fixture = SmokeFixture.Create();
        RunningService? service = null;
        var knownBearers = new List<string>();
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
                "The real owner CLI did not initialize the synthetic request fixture.");

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var unlocked = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The owner could not unlock the isolated request fixture.");

            await WriteRequestBodyAsync(connectionsExecutable, fixture, SmokeFixture.ResourceA, RequestBodyMarkerA).ConfigureAwait(false);
            await WriteRequestBodyAsync(connectionsExecutable, fixture, SmokeFixture.ResourceB, RequestBodyMarkerB).ConfigureAwait(false);
            await PublishRequestCatalogAsync(connectionsExecutable, fixture, RequestCatalogA, RequestLabelA, RequestDescriptionA, SmokeFixture.ResourceA).ConfigureAwait(false);
            await PublishRequestCatalogAsync(connectionsExecutable, fixture, RequestCatalogB, RequestLabelB, RequestDescriptionB, SmokeFixture.ResourceB).ConfigureAwait(false);

            static string ParseRequestId(string output)
            {
                const string prefix = "REQUEST submitted id=";
                var start = output.IndexOf(prefix, StringComparison.Ordinal);
                Require(start >= 0, "The agent request CLI did not return an opaque receipt.");
                start += prefix.Length;
                var end = output.IndexOf(';', start);
                Require(end > start, "The agent request receipt was incomplete.");
                return output[start..end];
            }

            static string ParseRequestPreviewId(string output, string requestId)
            {
                const string prefix = "REQUEST PREVIEW id=";
                var start = output.IndexOf(prefix, StringComparison.Ordinal);
                Require(start >= 0, "The owner request preview did not identify the request.");
                var idStart = start + prefix.Length;
                var idEnd = output.IndexOf(' ', idStart);
                Require(idEnd > idStart && output[idStart..idEnd] == requestId,
                    "The owner request preview did not bind the shown snapshot to the request.");
                const string previewMarker = "preview=";
                var previewStart = output.IndexOf(previewMarker, idEnd, StringComparison.Ordinal);
                Require(previewStart >= 0, "The owner request preview omitted its server preview identifier.");
                previewStart += previewMarker.Length;
                var previewEnd = output.IndexOfAny(['\r', '\n', ' '], previewStart);
                if (previewEnd < 0)
                {
                    previewEnd = output.Length;
                }

                var previewId = output[previewStart..previewEnd].Trim();
                Require(Guid.TryParseExact(previewId, "N", out _), "The owner request preview ID was not opaque.");
                return previewId;
            }

            async Task<string> SubmitRequestAsync(string name, params string[] args)
            {
                var full = new List<string> { "owner", "request", "--agent-pipe", fixture.AgentPipe };
                full.AddRange(args);
                var result = await RunOwnerCommandAsync(connectionsExecutable, full.ToArray()).ConfigureAwait(false);
                Require(result.ExitCode == 0, "The agent request did not return an opaque receipt: " + result.StandardError.Trim());
                var requestId = ParseRequestId(result.StandardOutput);
                Require(!result.StandardOutput.Contains(RequestBodyMarkerA, StringComparison.Ordinal) &&
                        !result.StandardOutput.Contains(RequestBodyMarkerB, StringComparison.Ordinal) &&
                        !result.StandardOutput.Contains(SmokeFixture.ResourceA, StringComparison.Ordinal) &&
                        !result.StandardOutput.Contains(SmokeFixture.ResourceB, StringComparison.Ordinal) &&
                        !result.StandardOutput.Contains("mb1_", StringComparison.Ordinal) &&
                        !result.StandardOutput.Contains("mbs1_", StringComparison.Ordinal),
                    "The agent request receipt disclosed a private body, resource ID, or bearer (" + name + ").");
                return requestId;
            }

            async Task<CommandResult> PreviewRedirectedAsync(string requestId)
            {
                return await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "request-preview", "--control-pipe", fixture.ControlPipe,
                    "--request-id", requestId).ConfigureAwait(false);
            }

            async Task<AttachedConsoleResult> PreviewAttachedAsync(string requestId)
            {
                return await RunAttachedPreviewAsync(connectionsExecutable, fixture.ControlPipe, requestId).ConfigureAwait(false);
            }


            async Task<(string GrantId, string Handoff)> ApproveRequestAsync(string requestId, string previewId, string name, params string[] args)
            {
                var handoff = Path.Combine(fixture.OutputDirectory, name + ".grant-handoff");
                var full = new List<string>
                {
                    "owner", "request-approve", "--control-pipe", fixture.ControlPipe,
                    "--request-id", requestId, "--preview-id", previewId
                };
                full.AddRange(args);
                full.Add("--handoff-file");
                full.Add(handoff);
                var result = await RunOwnerCommandWithInputAsync(connectionsExecutable, "APPROVE" + Environment.NewLine, full.ToArray()).ConfigureAwait(false);
                Require(result.ExitCode == 0, "The owner could not approve the narrowed request scope: " + result.StandardError.Trim());
                Require(!result.StandardOutput.Contains("mb1_", StringComparison.Ordinal),
                    "The owner approval exposed a raw bearer on its output stream.");
                VerifyOwnerHandoffAcl(handoff);
                const string prefix = "grant=";
                var start = result.StandardOutput.IndexOf(prefix, StringComparison.Ordinal);
                Require(start >= 0, "The owner approval did not report its grant identifier.");
                start += prefix.Length;
                var end = result.StandardOutput.IndexOf(';', start);
                Require(end > start, "The owner approval grant identifier was incomplete.");
                return (result.StandardOutput[start..end], handoff);
            }

            async Task<(string SessionId, string SessionFile)> RedeemHandoffAsync(string handoff, string name)
            {
                var sessionFile = Path.Combine(fixture.OutputDirectory, name + ".session-handoff");
                var result = await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                    "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
                Require(result.ExitCode == 0 && result.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
                    "An owner-approved request grant did not redeem over the real agent pipe: " + result.StandardError.Trim());
                knownBearers.Add(ReadSessionBearer(sessionFile));
                VerifyOwnerHandoffAcl(sessionFile);
                return (ParseSessionId(result.StandardOutput), sessionFile);
            }

            async Task<(int ExitCode, byte[] Body, string StandardOutput, string StandardError)> AgentReadAsync(string sessionFile, string resourceId, long revision)
            {
                var result = await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "agent-read", "--agent-pipe", fixture.AgentPipe,
                    "--session-file", sessionFile, "--operation", "resource.read",
                    "--resource-id", resourceId, "--resource-revision", revision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                const string begin = "AGENT-BODY-BEGIN";
                const string end = "AGENT-BODY-END";
                var beginIndex = result.StandardOutput.IndexOf(begin, StringComparison.Ordinal);
                var endIndex = result.StandardOutput.IndexOf(end, StringComparison.Ordinal);
                var body = beginIndex >= 0 && endIndex > beginIndex
                    ? Convert.FromBase64String(result.StandardOutput[(beginIndex + begin.Length)..endIndex].Trim())
                    : Array.Empty<byte>();
                return (result.ExitCode, body, result.StandardOutput, result.StandardError);
            }

            var expiry = DateTimeOffset.UtcNow.AddMinutes(20).ToString("O", CultureInfo.InvariantCulture);
            var requestA = await SubmitRequestAsync("request-a",
                "--catalog-ids", RequestCatalogA,
                "--purpose", "synthetic request purpose alpha",
                "--agent-id", "synthetic-agent-alpha",
                "--agent-context", "synthetic agent context alpha",
                "--provider", RequestProvider,
                "--model", RequestModel,
                "--disclosure-context", RequestDisclosure,
                "--operations", "resource.read").ConfigureAwait(false);
            var requestB = await SubmitRequestAsync("request-b",
                "--catalog-ids", RequestCatalogA + "," + RequestCatalogB,
                "--purpose", "synthetic request purpose beta",
                "--agent-id", "synthetic-agent-beta",
                "--operations", "resource.read,source.read").ConfigureAwait(false);

            var rejected = await SubmitRequestAsync("request-reject",
                "--catalog-ids", RequestCatalogB,
                "--purpose", "synthetic request purpose rejected",
                "--operations", "resource.read").ConfigureAwait(false);

            var unknownRequest = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.access.request",
                catalogIds = new[] { "fixture-not-present" },
                purpose = "synthetic unknown catalog probe",
                operations = new[] { "resource.read" },
            }).ConfigureAwait(false);
            Require(ReplyCode(unknownRequest) == "request_unavailable",
                "An unknown catalog ID request did not fail closed without an existence oracle.");
            var privateRequest = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.access.request",
                catalogIds = new[] { SmokeFixture.ResourceA },
                purpose = "synthetic private ID probe",
                operations = new[] { "resource.read" },
            }).ConfigureAwait(false);
            Require(ReplyCode(privateRequest) == "request_unavailable",
                "A private resource ID request did not fail closed without an existence oracle.");
            Require(JsonSerializer.Serialize(unknownRequest) == JsonSerializer.Serialize(privateRequest),
                "Unknown and private catalog requests returned distinguishable wire bodies.");
            var spoofedRequest = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.access.request",
                catalogIds = new[] { RequestCatalogA },
                purpose = "synthetic spoofed approval probe",
                operations = new[] { "resource.read" },
                isOwner = true,
                previewId = new string('0', 32),
            }).ConfigureAwait(false);
            Require(ReplyCode(spoofedRequest) == "forbidden",
                "Agent-supplied owner/approval fields altered the request authority.");

            var ownerDirect = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "agent.access.request",
                catalogIds = new[] { RequestCatalogA },
                purpose = "synthetic owner-channel request probe",
            }).ConfigureAwait(false);
            Require(ReplyCode(ownerDirect) == "forbidden",
                "The owner channel accepted an agent request operation.");

            var listed = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "requests", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(listed.ExitCode == 0 && listed.StandardOutput.Contains($"REQUEST id={requestA}", StringComparison.Ordinal) &&
                    listed.StandardOutput.Contains($"REQUEST id={requestB}", StringComparison.Ordinal),
                "The owner request list did not expose the pending requests.");

            var redirectedA = await PreviewRedirectedAsync(requestA);
            Require(redirectedA.ExitCode == 3 && redirectedA.StandardError.Contains("preview_redirect_denied", StringComparison.Ordinal),
                "A redirected owner preview without an attached console emitted instead of failing closed.");
            Require(!redirectedA.StandardOutput.Contains(RequestBodyMarkerA, StringComparison.Ordinal) &&
                    !redirectedA.StandardError.Contains(RequestBodyMarkerA, StringComparison.Ordinal) &&
                    !Directory.EnumerateFiles(fixture.OutputDirectory, "*.preview.bin", SearchOption.AllDirectories).Any(),
                "A redirected owner preview leaked a private body or left a preview file.");
            var previewA = await PreviewAttachedAsync(requestA);
            Require(previewA.ExitCode == 0, "The owner could not preview the first request on an attached console.");
            Require(previewA.Transcript.Contains($"REQUEST PREVIEW id={requestA}", StringComparison.Ordinal) &&
                    previewA.Transcript.Contains($"source resource={SmokeFixture.ResourceA}", StringComparison.Ordinal) &&
                    previewA.Transcript.Contains("revision=2", StringComparison.Ordinal) &&
                    previewA.Transcript.Contains("operations=resource.read", StringComparison.Ordinal) &&
                    previewA.Transcript.Contains("purpose=synthetic request purpose alpha", StringComparison.Ordinal) &&
                    previewA.Transcript.Contains($"disclosure provider={RequestProvider} model={RequestModel} context={RequestDisclosure}", StringComparison.Ordinal) &&
                    previewA.Transcript.Contains($"--- preview body resource={SmokeFixture.ResourceA} ---", StringComparison.Ordinal) &&
                    previewA.Transcript.Contains(RequestBodyMarkerA, StringComparison.Ordinal),
                "The owner preview did not show the exact resource/revision, purpose, disclosure, or body on the attached owner console.");
            Require(!Directory.EnumerateFiles(fixture.OutputDirectory, "*.preview.bin", SearchOption.AllDirectories).Any(),
                "The owner preview persisted a private plaintext preview file.");

            var previewB = await PreviewAttachedAsync(requestB);
            Require(previewB.ExitCode == 0, "The owner could not preview the second request on an attached console.");
            var previewBId = ParseRequestPreviewId(previewB.Transcript, requestB);
            var previewAId = ParseRequestPreviewId(previewA.Transcript, requestA);
            Require(previewB.Transcript.Contains($"source resource={SmokeFixture.ResourceA}", StringComparison.Ordinal) &&
                    previewB.Transcript.Contains($"source resource={SmokeFixture.ResourceB}", StringComparison.Ordinal) &&
                    previewB.Transcript.Contains(RequestBodyMarkerA, StringComparison.Ordinal) &&
                    previewB.Transcript.Contains(RequestBodyMarkerB, StringComparison.Ordinal),
                "The two-catalog preview did not resolve both exact resource revisions and bodies.");

            var tamperWrite = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "write", "--control-pipe", fixture.ControlPipe,
                "--resource-id", SmokeFixture.ResourceB, "--zone-id", SmokeFixture.Zone,
                "--input-file", await WriteTempInputAsync(fixture, RequestRevisionMarker).ConfigureAwait(false)).ConfigureAwait(false);
            Require(tamperWrite.ExitCode == 0 && tamperWrite.StandardOutput.Contains("WRITE stored revision=3", StringComparison.Ordinal),
                "The stale-preview revision bump did not persist.");

            var staleApproveHandoff = Path.Combine(fixture.OutputDirectory, "stale-approve.handoff");
            var staleApprove = await RunOwnerCommandWithInputAsync(connectionsExecutable, "APPROVE" + Environment.NewLine,
                "owner", "request-approve", "--control-pipe", fixture.ControlPipe,
                "--request-id", requestB, "--preview-id", previewBId,
                "--expires-at-utc", expiry, "--handoff-file", staleApproveHandoff).ConfigureAwait(false);
            Require(staleApprove.ExitCode == 3 && staleApprove.StandardError.Contains("request_stale", StringComparison.Ordinal) &&
                    !File.Exists(staleApproveHandoff),
                "A stale preview after a resource revision change minted or persisted a grant.");

            var freshB = await PreviewAttachedAsync(requestB);
            Require(freshB.ExitCode == 0, "The owner could not take a fresh preview after the revision change on an attached console.");
            var freshBId = ParseRequestPreviewId(freshB.Transcript, requestB);
            Require(freshB.Transcript.Contains($"source resource={SmokeFixture.ResourceB}", StringComparison.Ordinal) &&
                    freshB.Transcript.Contains("revision=3", StringComparison.Ordinal) &&
                    freshB.Transcript.Contains(RequestRevisionMarker, StringComparison.Ordinal),
                "The fresh preview did not show the bumped revision and body.");
            Require(!Directory.EnumerateFiles(fixture.OutputDirectory, "*.preview.bin", SearchOption.AllDirectories).Any(),
                "A preview pass left a private plaintext preview file on disk.");
            var collectionProbe = await RunOwnerCommandWithInputAsync(connectionsExecutable, "SET" + Environment.NewLine,
                "owner", "collection-set", "--control-pipe", fixture.ControlPipe,
                "--collection-id", SmokeFixture.CollectionId,
                "--resource-ids", SmokeFixture.ResourceA + "," + SmokeFixture.ResourceB).ConfigureAwait(false);
            Require(collectionProbe.ExitCode == 0,
                "The owner could not change the synthetic collection membership: " + collectionProbe.StandardError.Trim());
            var groupStaleHandoff = Path.Combine(fixture.OutputDirectory, "group-stale-approve.handoff");
            var groupStale = await RunOwnerCommandWithInputAsync(connectionsExecutable, "APPROVE" + Environment.NewLine,
                "owner", "request-approve", "--control-pipe", fixture.ControlPipe,
                "--request-id", requestB, "--preview-id", freshBId,
                "--expires-at-utc", expiry, "--handoff-file", groupStaleHandoff).ConfigureAwait(false);
            Require(groupStale.ExitCode == 3 && groupStale.StandardError.Contains("request_stale", StringComparison.Ordinal) &&
                    !File.Exists(groupStaleHandoff),
                "A collection-membership change during review did not invalidate the held preview.");
            var regrouped = await PreviewAttachedAsync(requestB);
            Require(regrouped.ExitCode == 0, "The owner could not take a fresh preview after the collection change on an attached console.");
            var regroupedId = ParseRequestPreviewId(regrouped.Transcript, requestB);
            Require(!Directory.EnumerateFiles(fixture.OutputDirectory, "*.preview.bin", SearchOption.AllDirectories).Any(),
                "A preview pass left a private plaintext preview file on disk.");

            // Catalog withdrawal between preview and approval must fail closed; a later
            // republication that restores the same mapping must not revive the held preview.
            var withdrawB = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "catalog-withdraw", "--control-pipe", fixture.ControlPipe,
                "--catalog-id", RequestCatalogB).ConfigureAwait(false);
            Require(withdrawB.ExitCode == 0,
                "The owner could not withdraw the synthetic catalog: " + withdrawB.StandardError.Trim());
            var withdrawnStaleHandoff = Path.Combine(fixture.OutputDirectory, "withdrawn-stale-approve.handoff");
            var withdrawnStale = await RunOwnerCommandWithInputAsync(connectionsExecutable, "APPROVE" + Environment.NewLine,
                "owner", "request-approve", "--control-pipe", fixture.ControlPipe,
                "--request-id", requestB, "--preview-id", regroupedId,
                "--expires-at-utc", expiry, "--handoff-file", withdrawnStaleHandoff).ConfigureAwait(false);
            Require(withdrawnStale.ExitCode == 3 && withdrawnStale.StandardError.Contains("request_stale", StringComparison.Ordinal) &&
                    !File.Exists(withdrawnStaleHandoff),
                "A catalog withdrawal between preview and approval did not fail closed.");
            await PublishRequestCatalogAsync(connectionsExecutable, fixture, RequestCatalogB, RequestLabelB, RequestDescriptionB, SmokeFixture.ResourceB).ConfigureAwait(false);
            var republishedStaleHandoff = Path.Combine(fixture.OutputDirectory, "republished-stale-approve.handoff");
            var republishedStale = await RunOwnerCommandWithInputAsync(connectionsExecutable, "APPROVE" + Environment.NewLine,
                "owner", "request-approve", "--control-pipe", fixture.ControlPipe,
                "--request-id", requestB, "--preview-id", regroupedId,
                "--expires-at-utc", expiry, "--handoff-file", republishedStaleHandoff).ConfigureAwait(false);
            Require(republishedStale.ExitCode == 3 && republishedStale.StandardError.Contains("request_stale", StringComparison.Ordinal) &&
                    !File.Exists(republishedStaleHandoff),
                "A withdrawn-then-republished catalog revived a stale preview.");
            var recataloged = await PreviewAttachedAsync(requestB);
            Require(recataloged.ExitCode == 0, "The owner could not take a fresh preview after catalog republication on an attached console.");
            var freshBIdAfterCatalog = ParseRequestPreviewId(recataloged.Transcript, requestB);
            Require(recataloged.Transcript.Contains($"source resource={SmokeFixture.ResourceB}", StringComparison.Ordinal) &&
                    recataloged.Transcript.Contains("revision=3", StringComparison.Ordinal),
                "The post-republication preview did not show the exact live revision.");
            Require(!Directory.EnumerateFiles(fixture.OutputDirectory, "*.preview.bin", SearchOption.AllDirectories).Any(),
                "A preview pass left a private plaintext preview file on disk.");
            // The collection-membership change above advanced the scope policy generation,
            // which correctly invalidated the earlier preview of request A as well. Take a
            // fresh preview for the remaining narrow/approve checks of request A.
            var previewAFresh = await PreviewAttachedAsync(requestA);
            Require(previewAFresh.ExitCode == 0, "The owner could not re-preview the first request after the review changes on an attached console.");
            previewAId = ParseRequestPreviewId(previewAFresh.Transcript, requestA);
            Require(previewAFresh.Transcript.Contains($"source resource={SmokeFixture.ResourceA}", StringComparison.Ordinal) &&
                    previewAFresh.Transcript.Contains("revision=2", StringComparison.Ordinal),
                "The re-preview did not show the exact live resource/revision.");
            Require(!Directory.EnumerateFiles(fixture.OutputDirectory, "*.preview.bin", SearchOption.AllDirectories).Any(),
                "A preview pass left a private plaintext preview file on disk.");
            var widenAttempt = await RunOwnerCommandWithInputAsync(connectionsExecutable, "APPROVE" + Environment.NewLine,
                "owner", "request-approve", "--control-pipe", fixture.ControlPipe,
                "--request-id", requestA, "--preview-id", previewAId,
                "--resource-ids", SmokeFixture.ResourceA + "," + SmokeFixture.ResourceB,
                "--operations", "resource.read",
                "--expires-at-utc", expiry,
                "--handoff-file", Path.Combine(fixture.OutputDirectory, "widen-approve.handoff")).ConfigureAwait(false);
            Require(widenAttempt.ExitCode == 3 && widenAttempt.StandardError.Contains("request_invalid", StringComparison.Ordinal),
                "Owner narrowing enlarged the previewed scope.");
            var escalateAttempt = await RunOwnerCommandWithInputAsync(connectionsExecutable, "APPROVE" + Environment.NewLine,
                "owner", "request-approve", "--control-pipe", fixture.ControlPipe,
                "--request-id", requestA, "--preview-id", previewAId,
                "--operations", "resource.read,source.read",
                "--expires-at-utc", expiry,
                "--handoff-file", Path.Combine(fixture.OutputDirectory, "escalate-approve.handoff")).ConfigureAwait(false);
            Require(escalateAttempt.ExitCode == 3 && escalateAttempt.StandardError.Contains("request_invalid", StringComparison.Ordinal),
                "Owner approval escalated to an unrequested operation.");

            var grantA = await ApproveRequestAsync(requestA, previewAId, "request-a",
                "--operations", "resource.read",
                "--expires-at-utc", expiry).ConfigureAwait(false);
            knownBearers.Add(ReadProtectedHandoffTokenString(grantA.Handoff));
            var grantB = await ApproveRequestAsync(requestB, freshBIdAfterCatalog, "request-b",
                "--resource-ids", SmokeFixture.ResourceA,
                "--operations", "resource.read",
                "--expires-at-utc", expiry).ConfigureAwait(false);
            knownBearers.Add(ReadProtectedHandoffTokenString(grantB.Handoff));
            Require(grantA.GrantId != grantB.GrantId, "Two approved requests received the same grant.");

            var repeatApprove = await RunOwnerCommandWithInputAsync(connectionsExecutable, "APPROVE" + Environment.NewLine,
                "owner", "request-approve", "--control-pipe", fixture.ControlPipe,
                "--request-id", requestA, "--preview-id", previewAId,
                "--operations", "resource.read",
                "--expires-at-utc", expiry,
                "--handoff-file", Path.Combine(fixture.OutputDirectory, "repeat-approve.handoff")).ConfigureAwait(false);
            Require(repeatApprove.ExitCode == 3 &&
                    (repeatApprove.StandardError.Contains("request_not_pending", StringComparison.Ordinal) ||
                     repeatApprove.StandardError.Contains("request_stale", StringComparison.Ordinal)),
                "A repeated approval of a decided request minted a duplicate grant.");

            var reject = await RunOwnerCommandWithInputAsync(connectionsExecutable, "REJECT" + Environment.NewLine,
                "owner", "request-reject", "--control-pipe", fixture.ControlPipe,
                "--request-id", rejected).ConfigureAwait(false);
            Require(reject.ExitCode == 0 && reject.StandardOutput.Contains($"REQUEST rejected id={rejected}", StringComparison.Ordinal),
                "The owner could not reject the third request.");
            var rejectedStatus = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "request-status", "--agent-pipe", fixture.AgentPipe,
                "--request-id", rejected).ConfigureAwait(false);
            Require(rejectedStatus.ExitCode == 0 && rejectedStatus.StandardOutput.Contains("status=access_request_rejected", StringComparison.Ordinal),
                "A rejected request did not report its terminal status to the agent.");

            var sessionA = await RedeemHandoffAsync(grantA.Handoff, "request-a").ConfigureAwait(false);
            var sessionB = await RedeemHandoffAsync(grantB.Handoff, "request-b").ConfigureAwait(false);

            var allowedA = await AgentReadAsync(sessionA.SessionFile, SmokeFixture.ResourceA, 2).ConfigureAwait(false);
            Require(allowedA.ExitCode == 0 && allowedA.Body.Length > 0, "The first approved session could not read its exact resource/revision.");
            var allowedBytesA = allowedA.Body;
            try
            {
                Require(Encoding.UTF8.GetString(allowedBytesA).Contains(RequestBodyMarkerA, StringComparison.Ordinal),
                    "The first approved session did not receive its exact private body.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(allowedBytesA);
            }

            var allowedB = await AgentReadAsync(sessionB.SessionFile, SmokeFixture.ResourceA, 2).ConfigureAwait(false);
            Require(allowedB.ExitCode == 0 && allowedB.Body.Length > 0, "The narrowed second session could not read its exact resource/revision.");
            CryptographicOperations.ZeroMemory(allowedB.Body);

            var deniedExcluded = await AgentReadAsync(sessionB.SessionFile, SmokeFixture.ResourceB, 3).ConfigureAwait(false);
            Require(deniedExcluded.ExitCode == 4 && deniedExcluded.Body.Length == 0 &&
                    deniedExcluded.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "A narrowed/excluded resource read was served.");
            var deniedSourceProbe = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.resource.read",
                sessionToken = ReadSessionBearer(sessionB.SessionFile),
                accessOperation = "source.read",
                resourceId = SmokeFixture.ResourceA,
                resourceRevision = 2,
            }).ConfigureAwait(false);
            Require(ReplyCode(deniedSourceProbe) == "denied", "An ungranted source operation expanded a readable summary.");
            var deniedCross = await AgentReadAsync(sessionA.SessionFile, SmokeFixture.ResourceB, 3).ConfigureAwait(false);
            Require(deniedCross.ExitCode == 4 && deniedCross.Body.Length == 0, "A cross-scope resource read was served.");

            var agentConfirm = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "owner.access.request.approve",
                previewId = previewAId,
                requestId = requestA,
                operations = new[] { "resource.read" },
                expiresAtUtc = expiry,
            }).ConfigureAwait(false);
            Require(ReplyCode(agentConfirm) == "forbidden", "The agent channel minted through an approval operation.");
            var agentReadSpoof = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "owner.access.requests",
            }).ConfigureAwait(false);
            Require(ReplyCode(agentReadSpoof) == "forbidden", "The agent channel listed private access requests.");

            var raceHandoffs = new[]
            {
                Path.Combine(fixture.OutputDirectory, "race-approve-a.handoff"),
                Path.Combine(fixture.OutputDirectory, "race-approve-b.handoff"),
            };
            var raceRequest = await SubmitRequestAsync("request-race",
                "--catalog-ids", RequestCatalogA,
                "--purpose", "synthetic request purpose race",
                "--operations", "resource.read").ConfigureAwait(false);
            var freshRace = await PreviewAttachedAsync(raceRequest);
            Require(freshRace.ExitCode == 0, "The owner could not preview the race request on an attached console.");
            var racePreview = ParseRequestPreviewId(freshRace.Transcript, raceRequest);
            var firstRace = RunOwnerCommandWithInputAsync(connectionsExecutable, "APPROVE" + Environment.NewLine,
                "owner", "request-approve", "--control-pipe", fixture.ControlPipe,
                "--request-id", raceRequest, "--preview-id", racePreview,
                "--operations", "resource.read",
                "--expires-at-utc", expiry, "--handoff-file", raceHandoffs[0]);
            var secondRace = RunOwnerCommandWithInputAsync(connectionsExecutable, "APPROVE" + Environment.NewLine,
                "owner", "request-approve", "--control-pipe", fixture.ControlPipe,
                "--request-id", raceRequest, "--preview-id", racePreview,
                "--operations", "resource.read",
                "--expires-at-utc", expiry, "--handoff-file", raceHandoffs[1]);
            var raceResults = await Task.WhenAll(firstRace, secondRace).ConfigureAwait(false);
            var raceWinners = raceResults.Where(result => result.ExitCode == 0).ToArray();
            Require(raceWinners.Length <= 1, "Concurrent repeat approvals minted duplicate grants.");

            VerifyRequestPersistence(fixture, passphrase);
            await VerifyRequestRecoveryAsync(connectionsExecutable, fixture, passphrase).ConfigureAwait(false);
            Require(!Directory.EnumerateFiles(fixture.OutputDirectory, "*.preview.bin", SearchOption.AllDirectories).Any(),
                "A private plaintext preview file survived to the lock boundary.");

            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The owner could not lock before locked-request checks.");
            Require(!Directory.EnumerateFiles(fixture.OutputDirectory, "*.preview.bin", SearchOption.AllDirectories).Any(),
                "A private plaintext preview file survived the owner lock.");
            var lockedList = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "requests", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockedList.ExitCode == 3 && lockedList.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "A locked vault exposed encrypted access requests.");
            var lockedRequest = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.access.request",
                catalogIds = new[] { RequestCatalogA },
                purpose = "synthetic locked request probe",
                operations = new[] { "resource.read" },
            }).ConfigureAwait(false);
            Require(ReplyCode(lockedRequest) == "vault_locked",
                "A request submitted while locked did not fail closed at the lock boundary.");
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false))
                    .Contains("vault=locked", StringComparison.Ordinal),
                "The service did not restart locked.");
            var restartUnlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(restartUnlock.ExitCode == 0, "The owner could not unlock the restarted request fixture.");
            var afterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "requests", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(afterRestart.ExitCode == 0 && afterRestart.StandardOutput.Contains("REQUESTS count=", StringComparison.Ordinal),
                "Encrypted access requests did not survive restart and unlock.");

            Console.WriteLine("PASS S1-T8 request receipt discloses no body, IDs, or bearer");
            Console.WriteLine("PASS S1-T8 owner preview shows exact revisions, bodies, and disclosure before approval");
            Console.WriteLine("PASS S1-T8 narrow/approve/reject binds the previewed snapshot with no scope growth");
            Console.WriteLine("PASS S1-T8 two independent sessions read only their exact approved scope");
            Console.WriteLine("PASS S1-T8 stale catalog/revision/collection changes fail closed with a fresh preview");
            Console.WriteLine("PASS S1-T8 access request and owner approval over the authenticated named pipes");
            Console.WriteLine("PASS fixture processes, profiles, and protected files were cleaned up");
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

    private static async Task WriteRequestBodyAsync(string executable, SmokeFixture fixture, string resourceId, string marker)
    {
        var input = Path.Combine(fixture.OutputDirectory, "request-body-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = Encoding.UTF8.GetBytes($"synthetic request fixture body; marker={marker}; never a real owner secret.");
        await File.WriteAllBytesAsync(input, bytes).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(bytes);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "write", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", SmokeFixture.Zone,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0, "The request fixture could not store its synthetic private body.");
        File.Delete(input);
    }

    private static async Task<string> WriteTempInputAsync(SmokeFixture fixture, string marker)
    {
        var input = Path.Combine(fixture.OutputDirectory, "request-revision-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = Encoding.UTF8.GetBytes($"synthetic request revised body; marker={marker}; never a real owner secret.");
        await File.WriteAllBytesAsync(input, bytes).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(bytes);
        return input;
    }

    private static async Task PublishRequestCatalogAsync(
        string executable, SmokeFixture fixture, string catalogId, string label, string description, string resourceId)
    {
        var result = await RunOwnerCommandWithInputAsync(executable, "PUBLISH\n",
            "owner", "catalog-publish", "--control-pipe", fixture.ControlPipe,
            "--catalog-id", catalogId, "--label", label, "--description", description,
            "--resource-id", resourceId).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"CATALOG published id={catalogId}", StringComparison.Ordinal),
            "The request fixture could not publish its synthetic catalog entry.");
    }

    private static void VerifyRequestPersistence(SmokeFixture fixture, string passphrase)
    {
        var scopePath = Path.Combine(fixture.VaultDirectoryPath, "scope-grants.enc");
        Require(File.Exists(scopePath), "The encrypted request state was not persisted.");
        var persisted = File.ReadAllBytes(scopePath);
        try
        {
            var text = Encoding.UTF8.GetString(persisted);
            Require(!text.Contains(RequestBodyMarkerA, StringComparison.Ordinal) &&
                    !text.Contains(RequestBodyMarkerB, StringComparison.Ordinal) &&
                    !text.Contains(RequestRevisionMarker, StringComparison.Ordinal) &&
                    !text.Contains(RequestSourceMarker, StringComparison.Ordinal) &&
                    !text.Contains(RequestProvider, StringComparison.Ordinal) &&
                    !text.Contains(RequestModel, StringComparison.Ordinal) &&
                    !text.Contains(RequestDisclosure, StringComparison.Ordinal) &&
                    !text.Contains("synthetic request purpose alpha", StringComparison.Ordinal) &&
                    !text.Contains("synthetic request purpose beta", StringComparison.Ordinal),
                "The encrypted request state exposed a private body, purpose, or disclosure marker.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(persisted);
        }

        var projectionPath = Path.Combine(Path.GetDirectoryName(fixture.VaultDirectoryPath)!, "published-catalog.json");
        Require(File.Exists(projectionPath), "The public catalog projection was not persisted.");
        var projection = File.ReadAllText(projectionPath);
        Require(projection.Contains(RequestCatalogA, StringComparison.Ordinal) &&
                projection.Contains(RequestCatalogB, StringComparison.Ordinal),
            "The public projection omitted an approved request catalog.");
        Require(!projection.Contains(RequestBodyMarkerA, StringComparison.Ordinal) &&
                !projection.Contains(RequestBodyMarkerB, StringComparison.Ordinal) &&
                !projection.Contains(SmokeFixture.ResourceA, StringComparison.Ordinal) &&
                !projection.Contains(SmokeFixture.ResourceB, StringComparison.Ordinal) &&
                !projection.Contains(SmokeFixture.Zone, StringComparison.Ordinal),
            "The public projection leaked a private request body, mapping, or zone.");
    }

    private static string ReadProtectedHandoffTokenString(string path)
    {
        var token = ReadProtectedHandoffToken(path);
        try
        {
            return Encoding.ASCII.GetString(token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    private static async Task VerifyRequestRecoveryAsync(string executable, SmokeFixture fixture, string passphrase)
    {
        var scopePath = Path.Combine(fixture.VaultDirectoryPath, "scope-grants.enc");
        var original = await File.ReadAllBytesAsync(scopePath).ConfigureAwait(false);
        try
        {
            var json = Encoding.UTF8.GetString(original);
            const string marker = "\"ciphertext\":\"";
            var start = json.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            Require(start >= marker.Length && start < json.Length, "The encrypted request envelope had no ciphertext field.");
            var changed = json[start] == 'A' ? 'B' : 'A';
            var tampered = Encoding.UTF8.GetBytes(json[..start] + changed + json[(start + 1)..]);
            await File.WriteAllBytesAsync(scopePath, tampered).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(tampered);
            var denied = await RunOwnerCommandAsync(executable,
                "owner", "requests", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(denied.ExitCode == 3 && denied.StandardError.Contains("request_unavailable", StringComparison.Ordinal),
                "Tampered encrypted request state was served instead of failing closed.");
        }
        finally
        {
            await File.WriteAllBytesAsync(scopePath, original).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(original);
        }

        var restored = await RunOwnerCommandAsync(executable,
            "owner", "requests", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(restored.ExitCode == 0 && restored.StandardOutput.Contains("REQUESTS count=", StringComparison.Ordinal),
            "Restoring the synthetic request ciphertext did not restore owner readability.");

        var locked = await RunOwnerCommandAsync(executable,
            "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(locked.ExitCode == 0, "The owner could not lock before locked-request checks.");
        var lockedList = await RunOwnerCommandAsync(executable,
            "owner", "requests", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(lockedList.ExitCode == 3 && lockedList.StandardError.Contains("vault_locked", StringComparison.Ordinal),
            "A locked vault exposed encrypted access requests.");
    }
}
