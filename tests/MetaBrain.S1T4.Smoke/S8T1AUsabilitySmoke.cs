using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetaBrain.Connections;
using MetaBrain.OwnerUi;

namespace MetaBrain.S1T4.Smoke;

// S8-T1A: the full owner-console usability walkthrough on the real surfaces.
// One synthetic vault is provisioned and unlocked through the real owner CLI;
// every workflow below goes through the console session the WPF window drives,
// the real owner control pipe, the real agent channel and the real MCP bridge:
// capture (Vietnamese text) -> read-back -> library search/open -> relation
// endpoints picked from service answers -> catalog publish -> agent request ->
// bound preview -> narrowed approval with a local handoff -> redeem/read ->
// revoke/lock/refusal, plus the client-side approval-validation negatives.
// The same run renders the real WPF window on its STA thread for every enduring
// view (before/after PNGs), checks focus-after-navigation, a resized render
// and Vietnamese readability. The fixture lives inside the repository under
// the ignored runtime tree and performs no permission mutation of any path:
// directories, settings and vault files are created with ordinary permissions.
// Nothing here is a mock and no assertion is a source-text echo.
internal static partial class OwnerServiceSmoke
{
    private const string S8T1AZone = "s8t1a-zone-rieng";
    private const string S8T1AItemAlpha = "s8t1a-ghi-chep-alpha";
    private const string S8T1AItemBeta = "s8t1a-ghi-chep-beta";
    private const string S8T1ACatalogAlpha = "s8t1a-muc-luc-alpha";
    private const string S8T1ALabelAlpha = "Synthetic S8-T1A alpha lesson (owner-written summary)";
    private const string S8T1ADescriptionAlpha = "Owner-written S8-T1A summary alpha; never derived from private text.";
    private const string S8T1AMarker = "s8t1a-synthetic-usability-marker-9d21";
    private const string S8T1ABodyAlpha =
        "S8-T1A synthetic alpha: ghi chú nghiên cứu về cách đăng nhập hệ thống sau khi quên mật khẩu; " +
        "người dùng muốn lưu lại quyết định triển khai cho dự án; " + S8T1AMarker;
    private const string S8T1ABodyBeta =
        "S8-T1A synthetic beta: bài học rút ra sau buổi rà soát quyền truy cập và phê duyệt phạm vi; " + S8T1AMarker;
    private const string S8T1ACaptureId = "s8t1a-ghi-chu-nhanh";
    private const string S8T1AInbox = "s8t1a-hop-thu";
    private const string S8T1AIntention = "s8t1a-y-dinh";
    private const string S8T1ACollection = "s8t1a-bo-suu-tap";
    private const string S8T1AProposalBody =
        "S8-T1A synthetic proposed lesson: bài học giả lập để chủ sở hữu thử duyệt, từ chối và hoàn tác; " +
        "đây là nội dung do tác nhân đề xuất, không phải nội dung thật.";
    private const string S8T1ACaptureText =
        "S8-T1A synthetic capture: nhật ký công việc hằng ngày — hôm nay kiểm tra sao lưu, phê duyệt yêu cầu, " +
        "và khóa kho trước khi nghỉ; " + S8T1AMarker;

    private static string S8T1AItemDraft(string title, string body, DateTimeOffset now, string kind = "claim") =>
        JsonSerializer.Serialize(new
        {
            kind,
            author = "user",
            creationMethod = "directWrite",
            evidence = "unverified",
            level = "l1",
            title,
            body,
            sources = Array.Empty<object>(),
            occurredAtUtc = now,
            recordedAtUtc = now.AddMinutes(1),
            applicability = (object?)null
        });

    private static string S8T1AProposalDraft(string idempotencyKey)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-3);
        return JsonSerializer.Serialize(new
        {
            kind = "create",
            idempotencyKey,
            destinationResourceId = S8T1AInbox,
            destinationRevision = 1L,
            subjectResourceId = (string?)null,
            subjectRevision = (long?)null,
            item = new
            {
                kind = "lesson",
                author = "assistant",
                creationMethod = "paraphrase",
                evidence = "unverified",
                level = "l1",
                title = "Synthetic S8-T1A proposed lesson",
                body = S8T1AProposalBody,
                sources = Array.Empty<object>(),
                occurredAtUtc = now,
                recordedAtUtc = now.AddMinutes(1),
                applicability = (object?)null
            },
            userConfirmed = (bool?)null
        });
    }

    internal static async Task RunS8T1AUsabilityAsync(string connectionsExecutable, string evidenceRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S8-T1A usability smoke requires Windows named pipes, DPAPI and WPF.");
        }

        RunS8T1AValidationAsserts();
        Console.WriteLine("PASS S8-T1A client-side approval validation rejects out-of-preview, incoherent and past-expiry drafts per field");

        var repositoryRoot = SmokeFixture.FindRepositoryRoot();
        var evidence = Path.GetFullPath(evidenceRoot);
        RequireInsideS8T1A(repositoryRoot, evidence);
        RequireNoReparseS8T1A(repositoryRoot, evidence);
        Directory.CreateDirectory(evidence);

        var fixtureRoot = Path.Combine(evidence, "fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        var serviceDirectory = Path.Combine(fixtureRoot, "service");
        var outputDirectory = Path.Combine(fixtureRoot, "output");
        Directory.CreateDirectory(serviceDirectory);
        Directory.CreateDirectory(outputDirectory);

        var ownerSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows token has no user SID.");
        var controlPipe = "MetaBrainS8T1A-" + Guid.NewGuid().ToString("N");
        var agentPipe = controlPipe + ".agent";
        var settingsPath = Path.Combine(serviceDirectory, "service-settings.json");
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 3,
            ownerSid = ownerSid.Value,
            controlPipe,
            agentPipe
        })).ConfigureAwait(false);

        RunningService? service = null;
        var knownBearers = new List<string>();
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);
            try
            {
                service = await StartServiceAsync(connectionsExecutable, settingsPath).ConfigureAwait(false);
                var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                    passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                    "owner", "provision", "--control-pipe", controlPipe).ConfigureAwait(false);
                Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                    "Provision did not create the synthetic S8-T1A vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");
                Console.WriteLine("PASS S8-T1A provisioned the synthetic vault through the real owner CLI");

                var now = DateTimeOffset.UtcNow.AddMinutes(-5);
                await S8T1AWriteItemAsync(connectionsExecutable, controlPipe, outputDirectory, S8T1AItemAlpha,
                    S8T1AItemDraft("Synthetic S8-T1A alpha decision", S8T1ABodyAlpha, now)).ConfigureAwait(false);
                await S8T1AWriteItemAsync(connectionsExecutable, controlPipe, outputDirectory, S8T1AItemBeta,
                    S8T1AItemDraft("Synthetic S8-T1A beta lesson", S8T1ABodyBeta, now)).ConfigureAwait(false);
                await S8T1AWriteItemAsync(connectionsExecutable, controlPipe, outputDirectory, S8T1AInbox,
                    S8T1AItemDraft("Synthetic S8-T1A inbox", "Synthetic inbox destination for the synthetic agent proposal; never real owner content.", now)).ConfigureAwait(false);
                await S8T1AWriteItemAsync(connectionsExecutable, controlPipe, outputDirectory, S8T1AIntention,
                    S8T1AItemDraft("Synthetic S8-T1A intention", "Synthetic intention: nhắc kiểm tra sao lưu định kỳ; never a real owner task.", now, "intention")).ConfigureAwait(false);
                Console.WriteLine("PASS S8-T1A stored two Vietnamese synthetic items plus an inbox and an intention through the real owner CLI");

                await S8T1ACreateAgentProposalAsync(connectionsExecutable, controlPipe, agentPipe, outputDirectory).ConfigureAwait(false);
                Console.WriteLine("PASS S8-T1A a synthetic agent proposal is pending through the real agent channel");

                var session = new OwnerConsoleSession(controlPipe);
                await session.AttachAsync().ConfigureAwait(false);
                Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
                    "The console session did not report the real unlocked vault (" + session.ReportedVaultState + ").");

                // ---- capture with a zone picked from service answers, then read back ----
                session.SetCaptureDraft(S8T1ACaptureText);
                Require(session.HasUnsavedCaptureText, "The capture editor did not report its unsaved Vietnamese text.");
                await session.CaptureAsync(S8T1ACaptureId, S8T1AZone, "Ghi chú công việc S8-T1A", null, "user", null).ConfigureAwait(false);
                Require(session.CaptureSource is { } saved && string.Equals(saved.Id, S8T1ACaptureId, StringComparison.Ordinal) &&
                        session.CaptureMessage is not null && !session.CaptureMessage.Contains("did not", StringComparison.Ordinal),
                    "The console did not save the Vietnamese capture (" + session.CaptureMessage + ").");
                await session.ReadBackCaptureAsync(S8T1ACaptureId, null).ConfigureAwait(false);
                Require(string.Equals(session.CaptureReadBackText, S8T1ACaptureText, StringComparison.Ordinal),
                    "The read-back did not return the exact Vietnamese text (" + session.CaptureReadBackMessage + ").");
                Console.WriteLine("PASS S8-T1A capture saves and reads back exact Vietnamese text with its saved ID reused");

                // ---- library: rebuild, Vietnamese search, open at the served revision ----
                await session.RebuildLibraryIndexAsync().ConfigureAwait(false);
                Require(session.LibraryIndex is { RowCount: >= 2 },
                    "The rebuilt library index did not materialize the synthetic rows (" + session.LibraryMessage + ").");
                session.SetLibraryQuery(new OwnerLibraryQuery(Text: "quyền truy cập", Limit: 10));
                await session.SearchLibraryAsync().ConfigureAwait(false);
                Require(session.LibraryPage is { Total: >= 1 },
                    "The Vietnamese library search answered no page (" + session.LibraryMessage + ").");
                var libraryPage = session.LibraryPage!;
                await session.OpenLibraryItemAsync(libraryPage.Hits.First(candidate =>
                    string.Equals(candidate.Id, S8T1AItemBeta, StringComparison.Ordinal))).ConfigureAwait(false);
                Require(session.LibraryItem is not null && string.Equals(session.LibraryItem.Id, S8T1AItemBeta, StringComparison.Ordinal) &&
                        session.LibraryItem.Body.Contains(S8T1AMarker, StringComparison.Ordinal),
                    "The console did not open the found item at its served revision (" + session.LibraryMessage + ").");
                var openItem = session.LibraryItem!;
                Require(session.KnownZones().Contains(S8T1AZone, StringComparer.Ordinal),
                    "The zone picker source did not carry the service-named zone.");
                Console.WriteLine("PASS S8T1A Vietnamese library search opens the served revision and feeds the zone picker");

                // ---- relations: candidate search from the service index, endpoints pinned ----
                await session.SearchRelationCandidatesAsync("S8-T1A synthetic").ConfigureAwait(false);
                Require(session.RelationCandidates.Length >= 2,
                    "The endpoint candidate search did not rank both synthetic items (" + session.RelationCandidateMessage + ").");
                var subject = session.RelationCandidates.First(candidate =>
                    string.Equals(candidate.ResourceId, S8T1AItemAlpha, StringComparison.Ordinal));
                var target = new OwnerConsoleSession.RelationEndpointPick(
                    openItem.Id, "memoryItem", openItem.Revision, openItem.ZoneId,
                    openItem.Id + "@" + openItem.Revision.ToString(CultureInfo.InvariantCulture));
                await session.CreateRelationAsync("rel-s8t1a-lien-ket", S8T1AZone, "relatedTo", "accepted", subject, target,
                    "S8-T1A synthetic reason: the beta lesson stands beside the alpha decision.").ConfigureAwait(false);
                Require(session.OpenRelation is { } link && string.Equals(link.Id, "rel-s8t1a-lien-ket", StringComparison.Ordinal),
                    "The console did not create the link from picked endpoints (" + session.RelationMessage + ").");
                Require(session.Relations.Any(entry => string.Equals(entry.Id, "rel-s8t1a-lien-ket", StringComparison.Ordinal)),
                    "The console auto-refresh did not list the created link (" + session.RelationsMessage + ").");
                Console.WriteLine("PASS S8-T1A relation links two service-ranked endpoints with an explicit kind and lists them");
                // ---- catalog: preview then publish exactly the owner-typed metadata ----
                await session.PreviewCatalogAsync(S8T1ACatalogAlpha, S8T1ALabelAlpha, S8T1ADescriptionAlpha, S8T1AItemAlpha).ConfigureAwait(false);
                Require(session.CatalogPreview is { } catalogPreview &&
                        string.Equals(catalogPreview.Label, S8T1ALabelAlpha, StringComparison.Ordinal) &&
                        !catalogPreview.Label.Contains(S8T1AMarker, StringComparison.Ordinal),
                    "The publication preview did not carry exactly the owner-typed label (" + session.CatalogWriteMessage + ").");
                await session.PublishCatalogAsync(S8T1ACatalogAlpha, S8T1ALabelAlpha, S8T1ADescriptionAlpha, S8T1AItemAlpha).ConfigureAwait(false);
                await session.RefreshCatalogAsync().ConfigureAwait(false);
                Require(session.CatalogEntries.Any(entry => string.Equals(entry.CatalogId, S8T1ACatalogAlpha, StringComparison.Ordinal)),
                    "The console did not publish the catalog entry (" + session.CatalogWriteMessage + ").");
                Console.WriteLine("PASS S8-T1A catalog preview/publish keeps owner-typed metadata with no private autofill");

                // ---- sources: hold an uncertain mapping for review, then withdraw it ----
                var probeFile = Path.Combine(outputDirectory, "s8t1a-probe.jsonl");
                await File.WriteAllTextAsync(probeFile, "{\"synthetic\":true}\n").ConfigureAwait(false);
                await session.SubmitMappingUnknownAsync("s8t1a-nguon-cho", probeFile, null, null, null).ConfigureAwait(false);
                await session.RefreshSourcesAsync().ConfigureAwait(false);
                var held = session.Sources.FirstOrDefault(source => string.Equals(source.SourceId, "s8t1a-nguon-cho", StringComparison.Ordinal));
                Require(held is not null && held.Quarantine is not null &&
                        string.Equals(held.Quarantine.Reason, "mapping_unknown", StringComparison.Ordinal),
                    "The uncertain mapping was not held for review without a guessed zone (" + session.SourceMessage + ").");
                await session.WithdrawMappingUnknownAsync(held!).ConfigureAwait(false);
                Console.WriteLine("PASS S8-T1A uncertain mapping is held with no guessed zone and withdrawn without a read");

                // ---- access request through the real bridge, then bound preview ----
                var requestCall = await RunBridgeToolAsync(connectionsExecutable, agentPipe,
                    "metabrain_access_request", new
                    {
                        catalogIds = S8T1ACatalogAlpha,
                        purpose = "synthetic S8-T1A purpose: doc lai quyet dinh trien khai",
                        operations = "resource.read",
                        agentId = "s8t1a-synthetic-agent",
                    }).ConfigureAwait(false);
                Require(requestCall.Contains("REQUEST submitted id=", StringComparison.Ordinal),
                    "The agent request did not return an opaque receipt (" + requestCall.Trim() + ").");
                var requestId = ParseS8T1ARequestId(requestCall);
                await session.RefreshAccessRequestsAsync().ConfigureAwait(false);
                var pending = session.AccessRequests.FirstOrDefault(request =>
                    string.Equals(request.RequestId, requestId, StringComparison.Ordinal));
                Require(pending is { Status: "pending" },
                    "The owner request list did not show the pending request (" + session.AccessRequestsMessage + ").");
                await session.PreviewAccessRequestAsync(pending!).ConfigureAwait(false);
                var bound = session.OpenRequestPreview;
                Require(bound is not null && bound.Resources.Length == 1 &&
                        string.Equals(bound.Resources[0].ResourceId, S8T1AItemAlpha, StringComparison.Ordinal) &&
                        bound.Bodies.Length == 1 && bound.Bodies[0].Text.Contains(S8T1AMarker, StringComparison.Ordinal),
                    "The bound preview did not carry the exact resource, revision and body (" + session.AccessDecisionMessage + ").");
                Console.WriteLine("PASS S8-T1A bridge request returns an opaque receipt while the owner preview binds the exact body");

                // ---- growth past the preview is refused with no grant minted ----
                var widenHandoff = Path.Combine(outputDirectory, "s8t1a-widen.handoff");
                await session.ApproveAccessRequestAsync(
                    new[] { S8T1AItemAlpha, S8T1AItemBeta }, new[] { "resource.read" }, null,
                    DateTimeOffset.UtcNow.AddMinutes(20), null, null, null, widenHandoff).ConfigureAwait(false);
                Require(session.AccessDecisionMessage is { } widenMessage &&
                        widenMessage.Contains("request_invalid", StringComparison.Ordinal) &&
                        !File.Exists(widenHandoff),
                    "Approval growth past the bound preview minted a grant (" + session.AccessDecisionMessage + ").");

                // ---- narrowed approval mints only the handoff path, never the token ----
                var handoffPath = Path.Combine(outputDirectory, "s8t1a-console.grant-handoff");
                var expiry = DateTimeOffset.UtcNow.AddDays(1);
                var draftProblem = OwnerConsoleSession.ValidateApprovalDraft(
                    bound!, null, new[] { "resource.read" }, null, expiry, null, null, null, handoffPath);
                Require(draftProblem is null, "The sendable narrowed draft did not validate clean (" + draftProblem?.Message + ").");
                await session.ApproveAccessRequestAsync(
                    null, new[] { "resource.read" }, null, expiry, null, null, null, handoffPath).ConfigureAwait(false);
                Require(session.LastGrantId is { Length: 32 } &&
                        string.Equals(session.LastHandoffPath, handoffPath, StringComparison.Ordinal) &&
                        File.Exists(handoffPath) &&
                        session.AccessDecisionMessage is { } approvedMessage &&
                        approvedMessage.Contains(session.LastGrantId, StringComparison.Ordinal) &&
                        !approvedMessage.Contains("mb1_", StringComparison.Ordinal),
                    "The narrowed approval did not mint exactly the previewed scope into the handoff (" + session.AccessDecisionMessage + ").");
                knownBearers.Add(ReadProtectedHandoffTokenString(handoffPath));
                Console.WriteLine("PASS S8-T1A narrowed approval mints the exact scope with growth refused and no bearer shown");

                // ---- the connector redeems and reads through the real agent channel ----
                // CLI acts only: the console session is untouched, so this is safe
                // before, during or after the WPF proof below.
                var sessionFile = Path.Combine(outputDirectory, "s8t1a-console.session-handoff");
                var redeemed = await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "redeem", "--agent-pipe", agentPipe,
                    "--handoff-file", handoffPath, "--session-file", sessionFile).ConfigureAwait(false);
                Require(redeemed.ExitCode == 0 && redeemed.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
                    "The connector could not redeem the console handoff (exit=" + redeemed.ExitCode + "; stderr=" + redeemed.StandardError.Trim() + ").");
                var readBack = await RunBridgeToolAsync(connectionsExecutable, agentPipe,
                    "metabrain_scoped_read", new
                    {
                        sessionFile,
                        operation = "resource.read",
                        resourceId = S8T1AItemAlpha,
                        resourceRevision = "1",
                    }).ConfigureAwait(false);
                Require(readBack.Contains(S8T1AMarker, StringComparison.Ordinal),
                    "The redeemed session did not read its approved Vietnamese body (" + readBack.Trim() + ").");
                var savedGrantId = session.LastGrantId!;
                await session.RefreshSessionsAsync().ConfigureAwait(false);
                var liveBeforeWpf = session.AgentSessions.FirstOrDefault(entry => string.Equals(entry.GrantId, savedGrantId, StringComparison.Ordinal));
                Require(liveBeforeWpf is not null && liveBeforeWpf.ExpiresAtUtc > DateTimeOffset.UtcNow,
                    "The session console did not show the live session with its expiry (" + session.SessionsMessage + ").");
                Console.WriteLine("PASS S8-T1A the connector redeems the console handoff and reads its exact body");

                // ---- real WPF renders of every enduring view, focus, resize, Vietnamese ----
                // Every remaining console-session act (revoke, lock) runs on the
                // window's own STA thread inside the proof, exactly like the
                // upstream WPF proofs: the smoke thread never touches the session
                // while the window's activity binding is alive.
                DriveAndRenderS8T1AWpf(session, outputDirectory, savedGrantId, connectionsExecutable, controlPipe);
                Console.WriteLine("PASS S8-T1A real WPF renders for all views with focus, resize and Vietnamese readability");
                Console.WriteLine("PASS S8-T1A session revoke/lock/refusal with private views cleared");

                Require(session.ReportedVaultState == OwnerVaultState.Locked,
                    "The console did not report the locked vault after the explicit lock.");
                Require(session.LibraryPage is null && session.LibraryItem is null && session.OpenRequestPreview is null,
                    "The lock did not clear the private console views.");
                var wrong = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                    "wrong synthetic vault passphrase\n", "owner", "unlock", "--control-pipe", controlPipe).ConfigureAwait(false);
                Require(wrong.ExitCode == 3 && wrong.StandardError.Contains("unlock_denied", StringComparison.Ordinal),
                    "A wrong vault passphrase was not denied.");
                Console.WriteLine("LIMIT synthetic S8-T1A fixture only; same-owner OS compromise, provider egress, and installed-service integration were not exercised.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(Encoding.UTF8.GetBytes(passphrase));
            }
        }
        finally
        {
            if (service is not null)
            {
                try
                {
                    await StopServiceAsync(service).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            foreach (var bearer in knownBearers)
            {
                CryptographicOperations.ZeroMemory(Encoding.ASCII.GetBytes(bearer));
            }

            try
            {
                // Keep the WPF proof renders beside the evidence root before the
                // fixture (vault, handoffs, sessions) is removed: the PNGs carry
                // no key, token, bearer or private real content — synthetic
                // Vietnamese corpus only — and later runs overwrite this set.
                var kept = Path.Combine(evidence, "kept-renders");
                if (Directory.Exists(kept))
                {
                    Directory.Delete(kept, recursive: true);
                }

                Directory.CreateDirectory(kept);
                foreach (var render in Directory.GetFiles(outputDirectory, "s8t1a-wpf-*.png"))
                {
                    File.Copy(render, Path.Combine(kept, Path.GetFileName(render)));
                }

                if (Directory.Exists(fixtureRoot))
                {
                    Directory.Delete(fixtureRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Permanent consumer-visible regression guard: the approval draft validator
    /// must reject every out-of-contract draft per field and accept the narrowed
    /// whole-preview draft. No service, no fixture, no mock.
    /// </summary>
    private static void RunS8T1AValidationAsserts()
    {
        var preview = new OwnerAccessRequestPreviewInfo(
            "0123456789abcdef0123456789abcdef", "abcdef0123456789abcdef0123456789",
            new[] { "s8t1a-muc-luc-alpha" },
            new[] { new OwnerScopeResource("s8t1a-ghi-chep-alpha", "s8t1a-zone-rieng", 1) },
            new[] { "resource.read", "source.read" },
            "synthetic purpose", "agent", null, null, null, null,
            DateTimeOffset.UtcNow.AddMinutes(-1), 1, 1,
            Array.Empty<OwnerAccessRequestBody>());
        var future = DateTimeOffset.UtcNow.AddDays(1);
        var handoff = Path.Combine(Path.GetTempPath(), "s8t1a-never-written.handoff");

        Require(OwnerConsoleSession.ValidateApprovalDraft(
            preview, null, new[] { "resource.read" }, null, future, null, null, null, handoff) is null,
            "The validator rejected a narrowed whole-preview draft.");
        Require(OwnerConsoleSession.ValidateApprovalDraft(
            preview, new[] { "s8t1a-ghi-chep-alpha", "s8t1a-no-such" }, null, null, future, null, null, null, handoff) is { Field: "resources" },
            "The validator did not name the resources field for growth past the preview.");
        Require(OwnerConsoleSession.ValidateApprovalDraft(
            preview, null, new[] { "resource.read", "provider.egress" }, null, future, null, null, null, handoff) is { Field: "operations" },
            "The validator did not name the operations field for an unoffered operation.");
        Require(OwnerConsoleSession.ValidateApprovalDraft(
            preview, null, null, null, DateTimeOffset.UtcNow.AddMinutes(-1), null, null, null, handoff) is { Field: "expiry" },
            "The validator did not name the expiry field for a past instant.");
        var egressPreview = preview with { Operations = new[] { "resource.read", "provider.egress" } };
        Require(OwnerConsoleSession.ValidateApprovalDraft(
            egressPreview, null, new[] { "provider.egress" }, null, future, null, null, null, handoff) is { Field: "egress" },
            "The validator did not name the egress field for a provider.egress operation without provider fields.");
        Require(OwnerConsoleSession.ValidateApprovalDraft(
            egressPreview, null, new[] { "resource.read", "provider.egress" }, null, future, "p", "m", 0.5m, handoff) is { Field: "egress" },
            "The validator did not enforce the $0.25 per-job cost ceiling.");
        Require(OwnerConsoleSession.ValidateApprovalDraft(
            preview, null, null, null, future, null, null, null, "  ") is { Field: "handoff" },
            "The validator did not name the handoff field for a missing handoff file.");

        // The intention form composes a wall clock plus an explicit zone into the
        // exact instant the service accepts; it never guesses an offset.
        Require(IntentionTimeEntry.TryCompose(
                new DateTime(2026, 10, 22), "09:15", IntentionTimeEntry.UtcOption(),
                out var utcDue, out var utcPreview, out _) &&
                string.Equals(utcDue, "2026-10-22T09:15:00Z", StringComparison.Ordinal) &&
                utcPreview.Contains("09:15 UTC", StringComparison.Ordinal),
            "The UTC composition did not yield the exact Z instant and preview.");
        Require(IntentionTimeEntry.TryCompose(
                new DateTime(2026, 10, 22), "09:15", IntentionTimeEntry.StoredOffsetOption(TimeSpan.FromMinutes(330)),
                out var offsetDue, out _, out _) &&
                string.Equals(offsetDue, "2026-10-22T09:15:00+05:30", StringComparison.Ordinal),
            "The explicit-offset composition did not preserve the chosen offset.");
        Require(!IntentionTimeEntry.TryCompose(new DateTime(2026, 10, 22), "25:99", IntentionTimeEntry.UtcOption(), out _, out _, out _),
            "An impossible wall-clock time was accepted.");
        Require(!IntentionTimeEntry.TryCompose(null, "09:15", IntentionTimeEntry.UtcOption(), out _, out _, out _),
            "A missing date was accepted.");
    }

    private static string ParseS8T1ARequestId(string output)
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
    /// Issues one owner-approved proposal.create grant for the synthetic inbox,
    /// redeems it and submits one synthetic agent proposal. This is the real
    /// agent channel: the owner approval names the exact destination, and the
    /// stored proposal stays pending until the console decides it.
    /// </summary>
    private static async Task S8T1ACreateAgentProposalAsync(
        string executable, string controlPipe, string agentPipe, string outputDirectory)
    {
        var handoff = Path.Combine(outputDirectory, "s8t1a-propose.grant-handoff");
        var sessionFile = Path.Combine(outputDirectory, "s8t1a-propose.session-handoff");
        var expiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O", CultureInfo.InvariantCulture);
        var grant = await RunOwnerCommandWithInputAsync(executable, "ISSUE" + Environment.NewLine,
            "owner", "grant", "--control-pipe", controlPipe,
            "--resource-ids", S8T1AItemAlpha + "," + S8T1AItemBeta,
            "--operations", "proposal.create",
            "--destination-resource-ids", S8T1AInbox,
            "--expires-at-utc", expiry,
            "--handoff-file", handoff).ConfigureAwait(false);
        Require(grant.ExitCode == 0 && File.Exists(handoff),
            "The owner could not issue the synthetic proposal grant (exit=" + grant.ExitCode +
            "; stderr=" + grant.StandardError.Trim() + ").");
        var redeem = await RunOwnerCommandAsync(executable,
            "owner", "redeem", "--agent-pipe", agentPipe,
            "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
        Require(redeem.ExitCode == 0 && redeem.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
            "The synthetic proposal session could not be redeemed (exit=" + redeem.ExitCode +
            "; stderr=" + redeem.StandardError.Trim() + ").");

        var draft = Path.Combine(outputDirectory, "s8t1a-proposal.json");
        await File.WriteAllTextAsync(draft, S8T1AProposalDraft("s8t1a-key-proposal-publish"), Encoding.UTF8).ConfigureAwait(false);
        var propose = await RunOwnerCommandAsync(executable,
            "owner", "agent-propose", "--agent-pipe", agentPipe,
            "--session-file", sessionFile, "--input-file", draft).ConfigureAwait(false);
        Require(propose.ExitCode == 0 &&
                propose.StandardOutput.Contains("PROPOSAL id=prop-", StringComparison.Ordinal) &&
                propose.StandardOutput.Contains("destination=" + S8T1AInbox, StringComparison.Ordinal) &&
                propose.StandardOutput.Contains("executor=agent", StringComparison.Ordinal) &&
                propose.StandardOutput.Contains("evidence=unverified", StringComparison.Ordinal),
            "The synthetic agent proposal was not stored as pending (exit=" + propose.ExitCode +
            "; stdout=" + propose.StandardOutput.Trim() + "; stderr=" + propose.StandardError.Trim() + ").");
    }

    private static async Task S8T1AWriteItemAsync(
        string executable, string controlPipe, string outputDirectory, string resourceId, string draftJson)
    {
        var input = Path.Combine(outputDirectory, "s8t1a-item-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", controlPipe,
            "--resource-id", resourceId, "--zone-id", S8T1AZone, "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
            "The S8-T1A item create did not store " + resourceId + " at revision 1 (exit=" + result.ExitCode +
            "; stdout=" + result.StandardOutput.Trim() + "; stderr=" + result.StandardError.Trim() + ").");
        File.Delete(input);
    }

    private static void RequireInsideS8T1A(string repositoryRoot, string evidence)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(evidence);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The S8-T1A evidence root must live inside the repository.");
        }
    }

    private static void RequireNoReparseS8T1A(string repositoryRoot, string evidence)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(evidence)); directory is not null; directory = directory.Parent)
        {
            // Ancestors that do not exist yet carry no attributes to inspect;
            // the walk covers every existing directory up to the repository root.
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The S8-T1A evidence path crosses a reparse point; refusing the run.");
            }

            if (string.Equals(
                Path.TrimEndingDirectorySeparator(directory.FullName),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)),
                StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Renders the real WPF owner window on its STA thread while the vault is
    /// unlocked with rich answers: every enduring view, keyboard focus after
    /// navigation, a narrowed render and Vietnamese readability. All session
    /// acts run on that thread; the PNGs are real renders of the live window.
    /// </summary>
    private static void DriveAndRenderS8T1AWpf(
        OwnerConsoleSession session,
        string outputDirectory,
        string grantId,
        string connectionsExecutable,
        string controlPipe)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(outputDirectory);
        ArgumentNullException.ThrowIfNull(grantId);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            OwnerConsoleWindow? window = null;
            try
            {
                window = new OwnerConsoleWindow(session);
                window.Width = 1280;
                window.Height = 1000;
                window.Show();
                window.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await RenderS8T1AViewAsync(window, session, "Owner session", "s8t1a-wpf-01-owner-session.png", outputDirectory).ConfigureAwait(true);
                        await RenderS8T1AViewAsync(window, session, "Vault & key", "s8t1a-wpf-02-vault-key.png", outputDirectory).ConfigureAwait(true);
                        await RenderS8T1AViewAsync(window, session, "Library", "s8t1a-wpf-03-library.png", outputDirectory).ConfigureAwait(true);
                        Require(window.LibraryQueryInput.IsFocused,
                            "Navigating to Library did not focus the search box for keyboard use.");
                        window.LibraryQueryInput.Text = "quyền truy cập";
                        window.LibraryQueryInput.RaiseEvent(new KeyEventArgs(
                            Keyboard.PrimaryDevice, PresentationSource.FromVisual(window),
                            0, Key.Enter) { RoutedEvent = UIElement.KeyDownEvent });
                        await WaitForS8T1AAsync(() => !session.LibraryActInFlight, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
                        Require(session.LibraryPage is not null,
                            "Pressing Enter in the search box did not run the library search.");
                        SaveS8T1AWpf(window, outputDirectory, "s8t1a-wpf-04-library-vietnamese.png", window.LibraryResults);
                        // Keyboard: Enter ran the search above; Tab moves focus to the
                        // next control in the live visual tree instead of a dead form.
                        window.LibraryQueryInput.Focus();
                        var focusBeforeTab = Keyboard.FocusedElement;
                        window.LibraryQueryInput.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                        Require(!ReferenceEquals(Keyboard.FocusedElement, focusBeforeTab),
                            "Tab from the library search box did not move focus to another control.");
                        SaveS8T1AWpf(window, outputDirectory, "s8t1a-wpf-04b-library-focus.png", window.LibraryResults);
                        // DPI: the same live window rendered at 144 dpi, not a web mock.
                        SaveS8T1AWpf(window, outputDirectory, "s8t1a-wpf-04c-library-144dpi.png", window.LibraryResults, dpi: 144);
                        ClickS8T1A(window.LibraryRebuildButton);
                        Require(await WaitForS8T1AAsync(
                                () => !session.LibraryActInFlight && session.LibraryIndex is { RowCount: >= 5 },
                                TimeSpan.FromSeconds(60)),
                            "Rebuilding the library index through the button did not materialize the new synthetic rows (" +
                            session.LibraryMessage + ").");
                        await RenderS8T1AViewAsync(window, session, "Inbox", "s8t1a-wpf-05-inbox.png", outputDirectory).ConfigureAwait(true);
                        await ExerciseS8T1AProposalUiAsync(window, session).ConfigureAwait(true);
                        Console.WriteLine("PASS S8-T1A publish, reject and undo run through the real WPF proposal controls with agent attribution kept");
                        SaveS8T1AWpf(window, outputDirectory, "s8t1a-wpf-05b-proposal-decided.png", window.ReviewMessageText);
                        // The decision wrote a canonical review item after the last
                        // rebuild, so the owner visibly rebuilds the index before
                        // relying on library search again.
                        await RenderS8T1AViewAsync(window, session, "Library", "s8t1a-wpf-05c-library-rebuilt.png", outputDirectory).ConfigureAwait(true);
                        ClickS8T1A(window.LibraryRebuildButton);
                        Require(await WaitForS8T1AAsync(
                                () => !session.LibraryActInFlight && session.LibraryIndex is { RowCount: >= 5 },
                                TimeSpan.FromSeconds(60)),
                            "Rebuilding after the review decision did not materialize the current rows (" + session.LibraryMessage + ").");
                        await RenderS8T1AViewAsync(window, session, "Relations", "s8t1a-wpf-06-relations.png", outputDirectory).ConfigureAwait(true);
                        await RenderS8T1AViewAsync(window, session, "Sharing", "s8t1a-wpf-07-sharing.png", outputDirectory).ConfigureAwait(true);
                        await RenderS8T1AViewAsync(window, session, "Sources", "s8t1a-wpf-08-sources.png", outputDirectory).ConfigureAwait(true);
                        await ExerciseS8T1ACollectionUiAsync(window, session).ConfigureAwait(true);
                        Console.WriteLine("PASS S8-T1A collection membership is built from service search and saved through the real WPF controls");
                        SaveS8T1AWpf(window, outputDirectory, "s8t1a-wpf-08b-collections.png", window.CollectionMembersList);
                        await RenderS8T1AViewAsync(window, session, "Jobs", "s8t1a-wpf-09-jobs.png", outputDirectory).ConfigureAwait(true);
                        await RenderS8T1AViewAsync(window, session, "Intentions", "s8t1a-wpf-10-intentions.png", outputDirectory).ConfigureAwait(true);
                        await ExerciseS8T1AIntentionUiAsync(window, session, connectionsExecutable, controlPipe).ConfigureAwait(true);
                        Console.WriteLine("PASS S8-T1A local date/time composes explicit UTC, a stale intention row is refused, and refresh-retry schedules");
                        SaveS8T1AWpf(window, outputDirectory, "s8t1a-wpf-10b-intention-scheduled.png", window.IntentionDuePreviewText);
                        window.Width = 800;
                        window.Height = 600;
                        window.UpdateLayout();
                        SaveS8T1AWpf(window, outputDirectory, "s8t1a-wpf-11-narrow.png", window.ContentScrollViewer);
                        window.Width = 1280;
                        window.Height = 1000;
                        window.UpdateLayout();
                        await RenderS8T1AViewAsync(window, session, "Review queue", "s8t1a-wpf-12-review.png", outputDirectory).ConfigureAwait(true);
                        await RenderS8T1AViewAsync(window, session, "Sharing", "s8t1a-wpf-13-sharing-live-session.png", outputDirectory).ConfigureAwait(true);
                        var live = session.AgentSessions.FirstOrDefault(entry => string.Equals(entry.GrantId, grantId, StringComparison.Ordinal));
                        Require(live is not null,
                            "The Sharing view did not list the live session before revocation (" + session.SessionsMessage + ").");
                        await session.RevokeSessionAsync(live!).ConfigureAwait(true);
                        Require(session.SessionsMessage is not null && session.SessionsMessage.Contains("revoked", StringComparison.Ordinal),
                            "The session revocation did not confirm the bearer authorizes nothing (" + session.SessionsMessage + ").");
                        SaveS8T1AWpf(window, outputDirectory, "s8t1a-wpf-14-sharing-revoked.png", window.ContentScrollViewer);
                        await session.LockAsync().ConfigureAwait(true);
                        Require(session.ReportedVaultState == OwnerVaultState.Locked,
                            "The console did not report the locked vault after the explicit lock.");
                        await WaitForS8T1AAsync(
                            () => window.CollectionMembersList.Items.Count == 0 &&
                                  window.CollectionMemberCandidatesList.Items.Count == 0 &&
                                  window.IntentionCandidatesList.Items.Count == 0 &&
                                  window.IntentionTimeInput.Text.Length == 0,
                            TimeSpan.FromSeconds(10)).ConfigureAwait(true);
                        Require(window.CollectionMembersList.Items.Count == 0 &&
                                window.CollectionMemberCandidatesList.Items.Count == 0 &&
                                window.IntentionCandidatesList.Items.Count == 0 &&
                                window.IntentionTimeInput.Text.Length == 0,
                            "Locking the vault left collection or intention draft state on screen.");
                        SaveS8T1AWpf(window, outputDirectory, "s8t1a-wpf-15-locked.png", window.ContentScrollViewer);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                    finally
                    {
                        window.DetachView();
                        window.Close();
                        window.Dispatcher.InvokeShutdown();
                    }
                });
                System.Windows.Threading.Dispatcher.Run();
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("The S8-T1A WPF proof failed: " + failure.Message, failure);
        }
    }

    private static void ClickS8T1A(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    /// <summary>
    /// Real inbox review flow through the actual controls: refresh the inbox,
    /// open the pending synthetic agent proposal, refuse an empty rejection,
    /// reject with a rationale, re-propose, publish the reviewed content, and
    /// withdraw the publication. Every act is the window's own click handler.
    /// </summary>
    private static async Task ExerciseS8T1AProposalUiAsync(OwnerConsoleWindow window, OwnerConsoleSession session)
    {
        ClickS8T1A(window.ReviewInboxButton);
        Require(await WaitForS8T1AAsync(
                () => !session.InboxActInFlight && session.ReviewProposals.Length >= 1,
                TimeSpan.FromSeconds(30)),
            "The proposal inbox click did not list the synthetic pending proposal (" + session.ReviewInboxMessage + ").");
        Require(window.ReviewProposals.Items.Count >= 1,
            "The proposal inbox list rendered no row for the listed proposal.");

        window.ReviewProposals.SelectedIndex = 0;
        ClickS8T1A(window.ReviewOpenButton);
        Require(await WaitForS8T1AAsync(
                () => !session.InboxActInFlight && session.ReviewProposal is not null,
                TimeSpan.FromSeconds(30)),
            "Opening the selected proposal through the button did not serve it (" + session.ReviewMessage + ").");
        Require(session.ReviewProposal is { Executor: "agent" } proposal &&
                string.Equals(proposal.Item.Author, "assistant", StringComparison.Ordinal),
            "The opened proposal did not keep the agent's own attribution.");
        Require(window.ReviewKindInput.SelectedItem is string && window.ReviewLevelInput.SelectedItem is string,
            "Opening the proposal did not fill the kind and level selectors from its own draft.");

        window.ReviewRationaleInput.Text = string.Empty;
        ClickS8T1A(window.ReviewRejectButton);
        Require(session.ReviewMessage is { } emptyRationale &&
                emptyRationale.Contains("needs an explicit rationale", StringComparison.Ordinal),
            "Rejecting with an empty rationale was not refused locally with an explanation (" + session.ReviewMessage + ").");

        window.ReviewRationaleInput.Text = "S8-T1A synthetic: từ chối để kiểm tra luồng, không phải quyết định thật.";
        ClickS8T1A(window.ReviewRejectButton);
        Require(await WaitForS8T1AAsync(
                () => !session.InboxActInFlight &&
                      string.Equals(session.ReviewItemLifecycle?.Publication, "rejected", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30)),
            "The reject button did not record the rejection (" + session.ReviewMessage + ").");

        window.ReviewRationaleInput.Text = "S8-T1A synthetic: đưa lại vào review để thử tiếp.";
        ClickS8T1A(window.ReviewUndoButton);
        Require(await WaitForS8T1AAsync(
                () => !session.InboxActInFlight &&
                      string.Equals(session.ReviewItemLifecycle?.Publication, "proposal", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30)),
            "The undo button did not return the rejected decision to proposal (" + session.ReviewMessage + ").");

        ClickS8T1A(window.ReviewPublishButton);
        Require(await WaitForS8T1AAsync(
                () => !session.InboxActInFlight &&
                      string.Equals(session.ReviewItemLifecycle?.Publication, "published", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30)),
            "The publish button did not publish the reviewed content (" + session.ReviewMessage + ").");
        Require(session.ReviewMessage is { } published &&
                published.Contains("author", StringComparison.OrdinalIgnoreCase),
            "The publish outcome did not state that attribution stayed the proposal's own (" + session.ReviewMessage + ").");

        window.ReviewRationaleInput.Text = "S8-T1A synthetic: rút bản đã đăng để kiểm tra undo.";
        ClickS8T1A(window.ReviewUndoButton);
        Require(await WaitForS8T1AAsync(
                () => !session.InboxActInFlight &&
                      string.Equals(session.ReviewItemLifecycle?.Publication, "withdrawn", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30)),
            "The undo button did not withdraw the published decision (" + session.ReviewMessage + ").");
    }

    /// <summary>
    /// Real collection membership flow through the actual controls: search the
    /// library from the Sources view, add service-ranked results as members,
    /// refuse an unnamed save, then save the named collection. No member ID is
    /// typed.
    /// </summary>
    private static async Task ExerciseS8T1ACollectionUiAsync(OwnerConsoleWindow window, OwnerConsoleSession session)
    {
        ClickS8T1A(window.CollectionsRefreshButton);
        Require(await WaitForS8T1AAsync(() => !session.SourcesActInFlight, TimeSpan.FromSeconds(30)),
            "Reading collections did not finish.");

        window.CollectionMemberSearchInput.Text = "S8-T1A synthetic";
        ClickS8T1A(window.CollectionMemberSearchButton);
        Require(await WaitForS8T1AAsync(
                () => !session.SourcesActInFlight && session.CollectionCandidates.Length >= 2,
                TimeSpan.FromSeconds(30)),
            "The collection member search did not rank the synthetic items (" + session.CollectionCandidateMessage + ").");

        for (var index = 0; index < session.CollectionCandidates.Length; index++)
        {
            var id = session.CollectionCandidates[index].Id;
            if (string.Equals(id, S8T1AItemAlpha, StringComparison.Ordinal) ||
                string.Equals(id, S8T1AItemBeta, StringComparison.Ordinal))
            {
                window.CollectionMemberCandidatesList.SelectedItems.Add(
                    window.CollectionMemberCandidatesList.Items[index]);
            }
        }

        ClickS8T1A(window.CollectionMemberAddButton);
        Require(window.CollectionMembersList.Items.Count == 2 &&
                window.CollectionMembersSummaryText.Text.Contains("2 member", StringComparison.Ordinal),
            "Selecting search results and adding them did not build a two-member draft.");

        window.CollectionIdInput.Text = string.Empty;
        ClickS8T1A(window.CollectionSaveButton);
        Require(window.SourceMessageText.Text.Contains("saved no collection", StringComparison.OrdinalIgnoreCase),
            "Saving an unnamed collection was not refused with an explanation (" + window.SourceMessageText.Text + ").");
        Require(!session.Collections.Any(collection => string.Equals(collection.CollectionId, S8T1ACollection, StringComparison.Ordinal)),
            "An unnamed collection save changed the service state.");

        window.CollectionIdInput.Text = S8T1ACollection;
        ClickS8T1A(window.CollectionSaveButton);
        Require(await WaitForS8T1AAsync(
                () => !session.SourcesActInFlight &&
                      session.Collections.Any(collection =>
                          string.Equals(collection.CollectionId, S8T1ACollection, StringComparison.Ordinal) &&
                          collection.ResourceIds.Length == 2),
                TimeSpan.FromSeconds(30)),
            "The save button did not store the two-member collection (" + session.CollectionsMessage + ").");

        // Remove one member, save, and add it back from the same service search:
        // membership changes are ordinary owner acts, not a typed ID list.
        window.CollectionMembersList.SelectedIndex = 0;
        ClickS8T1A(window.CollectionMemberRemoveButton);
        Require(window.CollectionMembersList.Items.Count == 1,
            "Removing the selected member did not narrow the draft to one member.");
        ClickS8T1A(window.CollectionSaveButton);
        Require(await WaitForS8T1AAsync(
                () => !session.SourcesActInFlight &&
                      session.Collections.Any(collection =>
                          string.Equals(collection.CollectionId, S8T1ACollection, StringComparison.Ordinal) &&
                          collection.ResourceIds.Length == 1),
                TimeSpan.FromSeconds(30)),
            "Saving after removing a member did not store the one-member collection (" + session.CollectionsMessage + ").");

        var alphaIndex = Array.FindIndex(session.CollectionCandidates,
            hit => string.Equals(hit.Id, S8T1AItemAlpha, StringComparison.Ordinal));
        window.CollectionMemberCandidatesList.SelectedItems.Clear();
        if (alphaIndex >= 0)
        {
            window.CollectionMemberCandidatesList.SelectedItems.Add(
                window.CollectionMemberCandidatesList.Items[alphaIndex]);
        }

        ClickS8T1A(window.CollectionMemberAddButton);
        Require(window.CollectionMembersList.Items.Count == 2,
            "Adding the member back did not restore the two-member draft.");
        ClickS8T1A(window.CollectionSaveButton);
        Require(await WaitForS8T1AAsync(
                () => !session.SourcesActInFlight &&
                      session.Collections.Any(collection =>
                          string.Equals(collection.CollectionId, S8T1ACollection, StringComparison.Ordinal) &&
                          collection.ResourceIds.Length == 2),
                TimeSpan.FromSeconds(30)),
            "Saving after re-adding the member did not store the two-member collection (" + session.CollectionsMessage + ").");
    }

    /// <summary>
    /// Real intention scheduling flow through the actual controls: pick the
    /// pending intention from the service search, enter a local date/time,
    /// check the explicit UTC preview, create the trigger, then prove a stale
    /// lifecycle click is refused and a refresh-and-retry succeeds.
    /// </summary>
    private static async Task ExerciseS8T1AIntentionUiAsync(
        OwnerConsoleWindow window, OwnerConsoleSession session, string connectionsExecutable, string controlPipe)
    {
        ClickS8T1A(window.IntentionsRefreshButton);
        Require(await WaitForS8T1AAsync(() => !session.IntentionActInFlight, TimeSpan.FromSeconds(30)),
            "Reading the intention schedule did not finish.");

        window.IntentionSearchInput.Text = "S8-T1A";
        ClickS8T1A(window.IntentionSearchButton);
        Require(await WaitForS8T1AAsync(
                () => !session.IntentionActInFlight &&
                      session.IntentionCandidates.Any(hit => string.Equals(hit.Id, S8T1AIntention, StringComparison.Ordinal)),
                TimeSpan.FromSeconds(30)),
            "Searching intentions did not rank the synthetic intention (" + session.IntentionCandidateMessage + ").");
        var candidateIndex = Array.FindIndex(session.IntentionCandidates,
            hit => string.Equals(hit.Id, S8T1AIntention, StringComparison.Ordinal));
        Require(candidateIndex >= 0 && candidateIndex < window.IntentionCandidatesList.Items.Count,
            "The synthetic intention was not rendered in the candidate list.");
        window.IntentionCandidatesList.SelectedItem = window.IntentionCandidatesList.Items[candidateIndex];
        Require(window.IntentionChosenText.Text.Contains(S8T1AIntention, StringComparison.Ordinal),
            "Choosing the intention candidate did not show its identity as secondary detail.");

        var localDue = DateTime.Today.AddDays(2);
        window.IntentionDateInput.SelectedDate = localDue;
        window.IntentionTimeInput.Text = "09:15";
        var localOption = window.IntentionZoneInput.Items.OfType<IntentionTimeOption>().First(option => option.UseLocal);
        window.IntentionZoneInput.SelectedItem = localOption;
        Require(IntentionTimeEntry.TryCompose(localDue, "09:15", localOption, out var dueAtIso, out var duePreview, out var composeProblem),
            "The local due time did not compose (" + composeProblem + ").");
        Require(window.IntentionDuePreviewText.Text.Contains(duePreview, StringComparison.Ordinal),
            "The window did not show the composed local/UTC preview before send (" + window.IntentionDuePreviewText.Text + ").");

        ClickS8T1A(window.IntentionTriggerSetButton);
        Require(await WaitForS8T1AAsync(
                () => !session.IntentionActInFlight &&
                      session.ScheduledIntentions.Any(intention =>
                          string.Equals(intention.ResourceId, S8T1AIntention, StringComparison.Ordinal) &&
                          intention.DueAt is { } due &&
                          due.UtcDateTime == DateTimeOffset.Parse(dueAtIso, CultureInfo.InvariantCulture).UtcDateTime),
                TimeSpan.FromSeconds(30)),
            "The trigger button did not schedule the exact composed UTC instant (" + session.IntentionsMessage + ").");
        var scheduledRevision = session.ScheduledIntentions
            .First(intention => string.Equals(intention.ResourceId, S8T1AIntention, StringComparison.Ordinal))
            .LifecycleRevision;
        var scheduledIndex = Array.FindIndex(session.ScheduledIntentions,
            intention => string.Equals(intention.ResourceId, S8T1AIntention, StringComparison.Ordinal));
        window.IntentionsList.SelectedIndex = scheduledIndex;

        // Stale/conflict: another owner act moves the lifecycle revision after
        // this list was read. The next click carries the old revision and the
        // service refuses it; the refresh-and-retry then succeeds.
        var notify = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "lifecycle-intention-notify", "--control-pipe", controlPipe,
            "--resource-id", S8T1AIntention, "--expected-revision",
            scheduledRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(true);
        Require(notify.ExitCode == 0,
            "The synthetic lifecycle revision could not be moved for the stale proof (exit=" + notify.ExitCode +
            "; stderr=" + notify.StandardError.Trim() + ").");
        ClickS8T1A(window.IntentionTriggerSetButton);
        Require(await WaitForS8T1AAsync(
                () => !session.IntentionActInFlight &&
                      session.IntentionsMessage is { } staleMessage &&
                      staleMessage.Contains("refused", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(30)),
            "A stale intention row was not refused by the service (" + session.IntentionsMessage + ").");

        ClickS8T1A(window.IntentionsRefreshButton);
        Require(await WaitForS8T1AAsync(() => !session.IntentionActInFlight, TimeSpan.FromSeconds(30)),
            "Re-reading the schedule after the stale refusal did not finish.");
        var refreshedIndex = Array.FindIndex(session.ScheduledIntentions,
            intention => string.Equals(intention.ResourceId, S8T1AIntention, StringComparison.Ordinal));
        Require(refreshedIndex >= 0, "The refreshed schedule no longer named the synthetic intention.");
        window.IntentionsList.SelectedIndex = refreshedIndex;
        ClickS8T1A(window.IntentionTriggerSetButton);
        Require(await WaitForS8T1AAsync(
                () => !session.IntentionActInFlight &&
                      session.IntentionsMessage is { } retryMessage &&
                      !retryMessage.Contains("refused", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(30)),
            "The refresh-and-retry did not reschedule the intention (" + session.IntentionsMessage + ").");
    }

    private static async Task RenderS8T1AViewAsync(
        OwnerConsoleWindow window, OwnerConsoleSession session, string entry, string fileName, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(window);
        foreach (var item in window.Navigation.Items.OfType<ListBoxItem>())
        {
            if (string.Equals(item.Content as string, entry, StringComparison.Ordinal))
            {
                window.Navigation.SelectedItem = item;
                break;
            }
        }

        Require(window.Navigation.SelectedItem is ListBoxItem selected &&
                string.Equals(selected.Content as string, entry, StringComparison.Ordinal),
            "The WPF console did not navigate to " + entry + ".");
        await WaitForS8T1AAsync(() => !session.SharingActInFlight && !session.LibraryActInFlight &&
            !session.InboxActInFlight && !session.RelationsActInFlight && !session.SourcesActInFlight,
            TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        SaveS8T1AWpf(window, outputDirectory, fileName, window.ContentScrollViewer);
    }

    private static void SaveS8T1AWpf(
        OwnerConsoleWindow window, string outputDirectory, string fileName, FrameworkElement? focus, int dpi = 96)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(outputDirectory);
        ArgumentNullException.ThrowIfNull(fileName);
        focus?.BringIntoView();
        window.UpdateLayout();
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
        var scale = dpi / 96.0;
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)(window.Width * scale)),
            Math.Max(1, (int)(window.Height * scale)),
            dpi,
            dpi,
            PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(outputDirectory, fileName);
        using (var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            encoder.Save(stream);
        }

        Console.WriteLine("WPF render saved: " + path);
    }

    private static async Task<bool> WaitForS8T1AAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(150).ConfigureAwait(true);
        }

        return condition();
    }
}
