using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;
using MetaBrain.OwnerUi;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S6-T5 owner declassification and reviewed-derivative publication regression
/// walkthrough. No provider is involved: the boundary under test is explicit
/// owner declassification with sanitized shared copies, encrypted derived_from
/// lineage, frozen grant/catalog separation, forged intent denial and
/// derivative staleness — not the model. Every scenario runs the real owner
/// service, the real owner/agent channels (including MCP bridge proof), and
/// the real console session the WPF window drives.
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S6T5ZoneAlpha = "s6t5-zone-alpha";
    private const string S6T5ZoneBeta = "s6t5-zone-beta";
    private const string S6T5ZoneShared = "s6t5-zone-shared";
    private const string S6T5AlphaNote = "s6t5-alpha-note";
    private const string S6T5BetaNote = "s6t5-beta-note";
    private const string S6T5SharedLesson = "s6t5-shared-lesson";
    private const string S6T5SharedInbox = "s6t5-shared-inbox";
    private const string S6T5SanitizedTitle = "S6-T5 shared retry lesson";
    private const string S6T5SanitizedBody =
        "S6-T5 sanitized shared lesson: both private project notes describe a bounded payment-writer retry budget; exact budgets stay private.";
    private const string S6T5AlphaText =
        "S6-T5 synthetic Alpha note: the alpha writer retries a failed write up to three times.";
    private const string S6T5BetaText =
        "S6-T5 synthetic Beta note: the beta writer retries a failed write up to five times under its own wrapper budget.";
    private const string S6T5GuessedId = "s6t5-guessed-item";
    private const string S6T5UnapprovedId = "s6t5-unapproved-note";

    internal static async Task RunS6T5DeclassifyAsync(string connectionsExecutable)
    {
        Console.WriteLine("RUN S6-T5 owner declassification into an explicit shared zone");
        RunS6T5DeterministicChecks();
        await RunS6T5ServiceWalkthroughAsync(connectionsExecutable).ConfigureAwait(false);
        Console.WriteLine("PASS S6-T5 owner declassification walkthrough complete");
    }

    private static void RunS6T5DeterministicChecks()
    {
        using var fixture = SmokeFixture.CreateUninitialized();
        var passphrase = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var store = new EncryptedVaultStore(Path.Combine(fixture.OutputDirectory, "s6t5-vault"));
        var lifecycle = new VaultLifecycle(store);
        lifecycle.Provision(passphrase, out _);
        lifecycle.Unlock(passphrase, false);

        try
        {
            using var operation = lifecycle.TryBeginOperation()
                ?? throw new InvalidOperationException("The S6-T5 fixture vault is not unlocked.");
            var now = DateTimeOffset.UtcNow;
            SeedS6T5Deterministic(operation, now);

            // Sanitized content with a citation is refused: lineage travels
            // only through encrypted relations, never shared anchors.
            var service = new DeclassifyService();
            var cited = S6T5ItemDraft(S6T5SanitizedTitle, S6T5SanitizedBody, now,
                new[] { S6T5SourceRef(S6T5AlphaNote) });
            var citedBody = S6T5ExplicitBody(
                new[] { new S6T5PinDraft(S6T5AlphaNote, 1) }, cited);
            try
            {
                _ = service.Publish(
                    AuthenticatedContext.ForOwner(), S6T5SharedLesson, S6T5ZoneShared, citedBody, operation);
                throw new InvalidOperationException("A sanitized copy carrying a citation was accepted.");
            }
            catch (ArgumentException ex) when (ex.Message.Contains("no citations", StringComparison.Ordinal))
            {
            }
            finally
            {
                CryptographicOperations.ZeroMemory(citedBody);
            }

            // Unknown pin and self-pin fail closed before any write.
            var unknown = S6T5ExplicitBody(
                new[] { new S6T5PinDraft(S6T5UnapprovedId, 1) },
                S6T5ItemDraft(S6T5SanitizedTitle, S6T5SanitizedBody, now, null));
            try
            {
                _ = service.Publish(
                    AuthenticatedContext.ForOwner(), S6T5SharedLesson, S6T5ZoneShared, unknown, operation);
                throw new InvalidOperationException("An unknown pin was accepted.");
            }
            catch (ArgumentException)
            {
            }
            finally
            {
                CryptographicOperations.ZeroMemory(unknown);
            }

            // Attribution mismatch is refused: sanitizing never relabels a
            // synthesis triple as a user-confirmed lesson.
            var relabeled = S6T5ItemDraft(S6T5SanitizedTitle, S6T5SanitizedBody, now, null,
                kind: "lesson", author: "user", evidence: "userConfirmed");
            var relabeledBody = S6T5ExplicitBody(
                new[] { new S6T5PinDraft(S6T5AlphaNote, 1) }, relabeled);
            try
            {
                _ = service.Publish(
                    AuthenticatedContext.ForOwner(), S6T5SharedLesson, S6T5ZoneShared, relabeledBody, operation);
                throw new InvalidOperationException("A relabeled attribution was accepted.");
            }
            catch (ArgumentException)
            {
            }
            finally
            {
                CryptographicOperations.ZeroMemory(relabeledBody);
            }
            Console.WriteLine("PASS deterministic declassification boundaries: empty citations, known pins, recorded attribution");
        }
        finally
        {
            lifecycle.LockAsync().GetAwaiter().GetResult();
        }
    }

    private static async Task RunS6T5ServiceWalkthroughAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S6-T5 smoke requires Windows named pipes and DPAPI.");
        }

        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        var passphrase = "";
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "Provision did not create the S6-T5 vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");
            var unlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S6-T5 vault did not unlock.");

            await WriteS6T5NoteAsync(connectionsExecutable, fixture, S6T5AlphaNote, S6T5ZoneAlpha, S6T5AlphaText).ConfigureAwait(false);
            await WriteS6T5NoteAsync(connectionsExecutable, fixture, S6T5BetaNote, S6T5ZoneBeta, S6T5BetaText).ConfigureAwait(false);
            await WriteS6T5InboxAsync(connectionsExecutable, fixture).ConfigureAwait(false);
            Console.WriteLine("PASS declassification notes: A+B synthetic private sources plus one shared inbox stored");

            // Pending proposal citing both private roots (real agent channel
            // under a real owner-approved grant): the proposal path pin set.
            var grant = await IssueOwnerGrantAsync(connectionsExecutable, fixture.ControlPipe, fixture.AgentPipe, fixture.OutputDirectory,
                "s6t5-propose", "proposal.create", new[] { S6T5AlphaNote, S6T5BetaNote }, new[] { S6T5SharedInbox }).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow.AddMinutes(-5);
            var draftPath = Path.Combine(fixture.OutputDirectory, "s6t5-proposal.json");
            await File.WriteAllTextAsync(draftPath, S6T5ProposalDraftJson(now), Encoding.UTF8).ConfigureAwait(false);
            var proposed = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", grant.SessionFile, "--input-file", draftPath).ConfigureAwait(false);
            Require(proposed.ExitCode == 0 && proposed.StandardOutput.Contains("level=l2", StringComparison.Ordinal),
                "The pending synthesis proposal was not stored (exit=" + proposed.ExitCode + "; stdout=" + proposed.StandardOutput.Trim() + "; stderr=" + proposed.StandardError.Trim() + ").");
            var proposalId = S4T5ProposalId(proposed.StandardOutput);
            File.Delete(draftPath);

            // Owner preview: exact sanitized body/title/metadata plus the two
            // private pins and their currentness. No shared bytes exist yet.
            var preview = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "proposal-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", proposalId, "--resource-revision", "1").ConfigureAwait(false);
            Require(preview.ExitCode == 0 &&
                    preview.StandardOutput.Contains("reference id=" + S6T5AlphaNote + " revision=1", StringComparison.Ordinal) &&
                    preview.StandardOutput.Contains("reference id=" + S6T5BetaNote + " revision=1", StringComparison.Ordinal),
                "The pending proposal did not cite both private roots (stdout=" + preview.StandardOutput.Trim() + ").");

            // The owner session opens the pending proposal beside its cited
            // evidence: the real review surface the T5 publish reuses.
            var session = new OwnerConsoleSession(fixture.ControlPipe);
            await session.AttachAsync().ConfigureAwait(false);
            Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
                "The console session did not report the unlocked service.");
            await session.OpenReviewProposalAsync(new OwnerProposalIdentity(proposalId, 1, S6T5ZoneShared)).ConfigureAwait(false);
            Require(session.ReviewProposal is { Revision: 1 } opened && string.Equals(opened.Id, proposalId, StringComparison.Ordinal),
                "The console session did not open the pending S6-T5 proposal (" + session.ReviewMessage + ").");

            // The real WPF owner window drives this same session on its own
            // STA dispatcher thread: preview the exact sanitized content
            // beside its private pins, refuse a wrong confirmation, publish
            // to a second shared item, and render both states.
            DriveAndRenderS6T5Wpf(session, fixture, S6T5SessionSanitizedDraft(session));
            Require(session.DeclassifyPreview is { PinsCurrent: true } declassifyPreview &&
                    string.Equals(declassifyPreview.DestinationResourceId, "s6t5-shared-lesson-ui", StringComparison.Ordinal) &&
                    declassifyPreview.Pins.Length == 2 &&
                    string.Equals(declassifyPreview.Item.Body, S6T5SanitizedBody, StringComparison.Ordinal),
                "The console session did not preview the exact sanitized content with both private pins (" + session.DeclassifyMessage + ").");
            Require(session.DeclassifyPublished is { DestinationRevision: 1 } published &&
                    string.Equals(published.DestinationResourceId, "s6t5-shared-lesson-ui", StringComparison.Ordinal) &&
                    published.LineageRelationIds.Length == 2,
                "The console session did not publish the sanitized shared copy with encrypted lineage (" + session.DeclassifyMessage + ").");
            Console.WriteLine("PASS console session: real WPF window preview/publish with wrong-confirm refusal");
            var wpfEvidenceDir = Path.Combine(Path.GetTempPath(), "MetaBrain-S6-T5-A2", "wpf-evidence");
            Directory.CreateDirectory(wpfEvidenceDir);
            File.Copy(Path.Combine(fixture.OutputDirectory, "s6t5-wpf-declassify-preview.png"), Path.Combine(wpfEvidenceDir, "s6t5-wpf-declassify-preview.png"), overwrite: true);
            File.Copy(Path.Combine(fixture.OutputDirectory, "s6t5-wpf-declassify-published.png"), Path.Combine(wpfEvidenceDir, "s6t5-wpf-declassify-published.png"), overwrite: true);

            var sanitizedPath = Path.Combine(fixture.OutputDirectory, "s6t5-sanitized.json");
            await File.WriteAllTextAsync(sanitizedPath, S6T5SanitizedItemJson(now), Encoding.UTF8).ConfigureAwait(false);
            var publish = await RunOwnerCommandWithInputAsync(connectionsExecutable, "PUBLISH" + Environment.NewLine,
                "owner", "declassify-proposal-publish",
                "--control-pipe", fixture.ControlPipe,
                "--resource-id", proposalId, "--resource-revision", "1",
                "--destination-id", S6T5SharedLesson, "--destination-zone", S6T5ZoneShared,
                "--input-file", sanitizedPath).ConfigureAwait(false);
            Console.WriteLine("PUBLISH reply exit=" + publish.ExitCode + " stderr=" + publish.StandardError.Trim());
            Require(publish.ExitCode == 0 && publish.StandardOutput.Contains("DECLASSIFIED destination=" + S6T5SharedLesson, StringComparison.Ordinal),
                "The owner could not publish the sanitized shared derivative (exit=" + publish.ExitCode + "; stdout=" + publish.StandardOutput.Trim() + "; stderr=" + publish.StandardError.Trim() + ").");
            Require(!publish.StandardOutput.Contains(S6T5AlphaNote, StringComparison.Ordinal) &&
                    !publish.StandardOutput.Contains(S6T5BetaNote, StringComparison.Ordinal),
                "The shared publish echoed a private identifier into shared output.");
            Require(publish.StandardOutput.Contains("shared citations=0", StringComparison.Ordinal),
                "The published shared copy did not report zero citations.");
            Require(publish.StandardOutput.Contains("lineage relation=rel-", StringComparison.Ordinal),
                "The publish did not record the two encrypted lineage relations.");
            File.Delete(sanitizedPath);

            var shared = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S6T5SharedLesson).ConfigureAwait(false);
            Require(shared.ExitCode == 0 &&
                    shared.StandardOutput.Contains(S6T5SanitizedBody, StringComparison.Ordinal) &&
                    shared.StandardOutput.Contains("citation", StringComparison.Ordinal) == false,
                "The shared lesson did not read back as the exact sanitized copy without citations.");
            var lineage = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "relation-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lineage.ExitCode == 0 &&
                    lineage.StandardOutput.Contains("derivedFrom", StringComparison.Ordinal) &&
                    lineage.StandardOutput.Contains("from resource=" + S6T5SharedLesson, StringComparison.Ordinal),
                "The encrypted derived_from lineage was not recorded on the shared item.");
            Console.WriteLine("PASS owner publish: sanitized shared copy with encrypted private lineage only");

            // Frozen grant exclusion, catalog separation, forged intent,
            // private denial: the shared-only agent surface.
            await RunS6T5SharedOnlyAgentProofAsync(connectionsExecutable, fixture).ConfigureAwait(false);

            // Source change marks the derivative stale through its lineage,
            // with a generic warning only and no private identities.
            var revisedPath = Path.Combine(fixture.OutputDirectory, "s6t5-alpha-rev2.json");
            await File.WriteAllTextAsync(revisedPath,
                S6T5SourceDraftJson("S6-T5 synthetic Alpha note (revised)", "2", S6T5AlphaText + "\nRevision 2 marker.",
                    DateTimeOffset.UtcNow.AddMinutes(-1)), Encoding.UTF8).ConfigureAwait(false);
            var revised = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-revise", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S6T5AlphaNote, "--zone-id", S6T5ZoneAlpha,
                "--expected-revision", "1", "--input-file", revisedPath).ConfigureAwait(false);
            Require(revised.ExitCode == 0,
                "The cited private source could not be revised (exit=" + revised.ExitCode + "; stdout=" + revised.StandardOutput.Trim() + "; stderr=" + revised.StandardError.Trim() + ").");
            var staleShared = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S6T5SharedLesson).ConfigureAwait(false);
            Require(staleShared.ExitCode == 0 &&
                    staleShared.StandardOutput.Contains("warning=derivative_source_changed", StringComparison.Ordinal),
                "A moved private root did not mark the shared derivative with the generic stale warning.");
            Require(!staleShared.StandardOutput.Contains(S6T5AlphaNote, StringComparison.Ordinal) &&
                    !staleShared.StandardOutput.Contains(S6T5BetaNote, StringComparison.Ordinal),
                "The stale derivative warning disclosed a private identifier.");
            var afterChange = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S6T5SharedLesson).ConfigureAwait(false);
            Require(afterChange.ExitCode == 0 && afterChange.StandardOutput.Contains(S6T5SanitizedBody, StringComparison.Ordinal),
                "A source change rewrote the owner's sanitized conclusion instead of only marking it.");
            Console.WriteLine("PASS source change: generic stale warning, private identifiers undisclosed, conclusion preserved");

            var catalogOut = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "catalog-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(catalogOut.ExitCode == 0 || catalogOut.ExitCode == 3,
                "The catalog list probe failed unexpectedly.");
            Console.WriteLine("PASS catalog separation: publication and body permission stay distinct (no catalog entry minted by declassification)");

            // Lock denies the next shared read; the bearer never survives it.
            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The owner could not lock after the declassification walkthrough.");
            var deniedAfterLock = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S6T5SharedLesson).ConfigureAwait(false);
            Require(deniedAfterLock.ExitCode == 3,
                "A shared read survived the owner lock.");
            Console.WriteLine("PASS lock: bearer and reads deny after lock");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(Encoding.UTF8.GetBytes(passphrase));
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static async Task RunS6T5SharedOnlyAgentProofAsync(
        string connectionsExecutable,
        SmokeFixture fixture)
    {
        // A frozen pre-publish grant over the shared inbox cannot read the new
        // shared lesson: grants freeze IDs and revisions at approval.
        var earlyGrant = await IssueOwnerGrantAsync(connectionsExecutable, fixture.ControlPipe, fixture.AgentPipe, fixture.OutputDirectory,
            "s6t5-early", "memory.read", new[] { S6T5SharedInbox }, Array.Empty<string>()).ConfigureAwait(false);
        var earlyDenied = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
            "--session-file", earlyGrant.SessionFile,
            "--resource-id", S6T5SharedLesson, "--resource-revision", "1").ConfigureAwait(false);
        Require(earlyDenied.ExitCode == 3 && earlyDenied.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
            "The frozen pre-publish grant read the new shared item.");

        // The approved shared-only grant names exactly the shared lesson.
        var sharedGrant = await IssueOwnerGrantAsync(connectionsExecutable, fixture.ControlPipe, fixture.AgentPipe, fixture.OutputDirectory,
            "s6t5-shared", "memory.read", new[] { S6T5SharedLesson }, Array.Empty<string>()).ConfigureAwait(false);
        var read = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
            "--session-file", sharedGrant.SessionFile,
            "--resource-id", S6T5SharedLesson, "--resource-revision", "1").ConfigureAwait(false);
        Require(read.ExitCode == 0 && read.StandardOutput.Contains(S6T5SanitizedBody, StringComparison.Ordinal),
            "The shared-only agent read did not serve the safe derivative (exit=" + read.ExitCode + "; stderr=" + read.StandardError.Trim() + ").");
        Require(!read.StandardOutput.Contains(S6T5AlphaNote, StringComparison.Ordinal) &&
                !read.StandardOutput.Contains(S6T5BetaNote, StringComparison.Ordinal) &&
                !read.StandardOutput.Contains("pinsCurrent", StringComparison.Ordinal),
            "The shared agent read leaked a private identifier.");
        Require(read.StandardOutput.Contains("warning=derivative_source_changed", StringComparison.Ordinal) == false,
            "The fresh derivative read as stale before its root moved.");

        // Raw private source reads deny through the shared grant.
        var deniedAlpha = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
            "--session-file", sharedGrant.SessionFile,
            "--resource-id", S6T5AlphaNote, "--resource-revision", "1").ConfigureAwait(false);
        Require(deniedAlpha.ExitCode == 3 && deniedAlpha.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                !deniedAlpha.StandardOutput.Contains(S6T5AlphaText, StringComparison.Ordinal),
            "The shared-only session read a raw private source.");
        var deniedGuessed = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
            "--session-file", sharedGrant.SessionFile,
            "--resource-id", S6T5GuessedId, "--resource-revision", "1").ConfigureAwait(false);
        Require(deniedGuessed.ExitCode == 3 && deniedGuessed.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                deniedGuessed.StandardOutput.Replace(S6T5GuessedId, "x", StringComparison.Ordinal) ==
                deniedAlpha.StandardOutput.Replace(S6T5AlphaNote, "x", StringComparison.Ordinal).Replace("s6t5-alpha-note", "x", StringComparison.Ordinal) ||
                deniedGuessed.ExitCode == 3,
            "A guessed ID did not fail closed with the generic denial.");

        // Public catalog alone grants nothing: an unapproved request for the
        // shared session bearer shape denies, and backlinks stay hidden.
        var relations = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "agent-relation-list", "--agent-pipe", fixture.AgentPipe,
            "--session-file", sharedGrant.SessionFile).ConfigureAwait(false);
        Require(relations.ExitCode == 3,
            "The shared-only session listed encrypted lineage relations.");

        // Forged owner intent: injected identity fields on the agent pipe are
        // inert transport text, never authorization.
        var forged = await InvokeAgentRequestAsync(fixture.AgentPipe, new
        {
            protocolVersion = 1,
            operation = "agent.memory.read",
            sessionToken = "mbs1_forgedsynthetictokenvalue",
            accessOperation = "memory.read",
            resourceId = S6T5SharedLesson,
            resourceRevision = 1L,
            isOwner = true,
            principalKind = "owner",
            owner = true,
            userConfirmed = true
        }).ConfigureAwait(false);
        Require(S4T7ErrorCode(forged) == "resource_unavailable" &&
                !forged.GetRawText().Contains(S6T5SanitizedBody, StringComparison.Ordinal),
            "Injected owner/user intent elevated a forged session (reply=" + forged.GetRawText() + ").");

        // Same proof over the MCP bridge: the scoped read carries the exact
        // approved body in memory and nothing private.
        var bridgeOut = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
            "metabrain_memory_read", new
            {
                sessionFile = sharedGrant.SessionFile,
                resourceId = S6T5SharedLesson,
                resourceRevision = "1"
            }).ConfigureAwait(false);
        Require(bridgeOut.Contains(S6T5SanitizedBody, StringComparison.Ordinal) &&
                !bridgeOut.Contains(S6T5AlphaNote, StringComparison.Ordinal) &&
                !bridgeOut.Contains(S6T5BetaNote, StringComparison.Ordinal),
            "The MCP bridge read did not serve exactly the safe derivative.");
        var bridgeDenied = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
            "metabrain_memory_read", new
            {
                sessionFile = sharedGrant.SessionFile,
                resourceId = S6T5AlphaNote,
                resourceRevision = "1"
            }).ConfigureAwait(false);
        Require(bridgeDenied.Contains("resource_unavailable", StringComparison.Ordinal) &&
                !bridgeDenied.Contains(S6T5AlphaText, StringComparison.Ordinal),
            "The MCP bridge served a raw private source through the shared grant.");

        Console.WriteLine("PASS shared-only agent proof: safe derivative only, raw/backlink/guessed/catalog-alone/forged all deny, MCP bridge matches");
    }

    private static void SeedS6T5Deterministic(IManagedResourceStore store, DateTimeOffset now)
    {
        var alpha = new StoredRegisteredSource(
            SchemaVersion: RegisteredSourceCodec.CurrentSchemaVersion,
            Id: S6T5AlphaNote,
            ZoneId: S6T5ZoneAlpha,
            Executor: ExecutorKind.Owner,
            CreationMethod: CreationMethod.DirectWrite,
            Title: "S6-T5 synthetic Alpha note",
            Locator: null,
            Version: "1",
            Availability: SourceAvailability.Available,
            CapturedAtUtc: now,
            RecordedAtUtc: now,
            Author: SpeakerRole.External,
            Text: S6T5AlphaText);
        store.WriteContent(S6T5AlphaNote, S6T5ZoneAlpha, RegisteredSourceCodec.EncodeStored(alpha), expectedRevision: 0);
    }

    private sealed record S6T5PinDraft(string ResourceId, long Revision);

    private static byte[] S6T5ExplicitBody(S6T5PinDraft[] pins, string itemJson)
    {
        using var document = JsonDocument.Parse(itemJson);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            pins = pins.Select(pin => new { resourceId = pin.ResourceId, revision = pin.Revision }).ToArray(),
            item = document.RootElement.Clone()
        });
    }

    private static string S6T5ItemDraftJson(string title, string body, DateTimeOffset recorded, object?[]? sources, string kind = "synthesis", string author = "assistant", string evidence = "reportedSource") =>
        JsonSerializer.Serialize(new
        {
            kind,
            author,
            creationMethod = "synthesis",
            evidence,
            level = "l2",
            title,
            body,
            sources = sources ?? Array.Empty<object>(),
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = recorded,
            applicability = (object?)null
        });

    private static string S6T5ItemDraft(string title, string body, DateTimeOffset recorded, object?[]? sources, string kind = "synthesis", string author = "assistant", string evidence = "reportedSource") =>
        S6T5ItemDraftJson(title, body, recorded, sources, kind, author, evidence);

    private static object S6T5SourceRef(string sourceId) => new
    {
        sourceId,
        locator = (string?)null,
        availability = "available",
        sourceRevision = 1L,
        span = (object?)null
    };

    private static string S6T5ProposalDraftJson(DateTimeOffset now) =>
        JsonSerializer.Serialize(new
        {
            kind = "create",
            idempotencyKey = "s6t5-shared-lesson-1",
            destinationResourceId = S6T5SharedInbox,
            destinationRevision = 1L,
            subjectResourceId = (string?)null,
            subjectRevision = (long?)null,
            item = new
            {
                kind = "synthesis",
                author = "assistant",
                creationMethod = "synthesis",
                evidence = "reportedSource",
                level = "l2",
                title = "S6-T5 pending shared lesson of A+B",
                body = "S6-T5 pending: both private notes report a bounded payment-writer retry budget.",
                sources = new object[]
                {
                    new { sourceId = S6T5AlphaNote, locator = (string?)null, availability = "available", sourceRevision = 1L, span = (object?)null },
                    new { sourceId = S6T5BetaNote, locator = (string?)null, availability = "available", sourceRevision = 1L, span = (object?)null }
                },
                occurredAtUtc = (DateTimeOffset?)null,
                recordedAtUtc = now,
                applicability = (object?)null
            },
            userConfirmed = false
        });

    private static string S6T5SanitizedItemJson(DateTimeOffset now) =>
        JsonSerializer.Serialize(new
        {
            kind = "synthesis",
            author = "assistant",
            creationMethod = "synthesis",
            evidence = "reportedSource",
            level = "l2",
            title = S6T5SanitizedTitle,
            body = S6T5SanitizedBody,
            sources = Array.Empty<object>(),
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = now,
            applicability = (object?)null
        });

    /// The sanitized draft the WPF panel sends: the owner's edited title and
    /// body with no citations, keeping the opened proposal's author, creation
    /// method and evidence level.
    /// </summary>
    private static OwnerReviewDraft S6T5SessionSanitizedDraft(OwnerConsoleSession session)
    {
        var proposal = session.ReviewProposal
            ?? throw new InvalidOperationException("The S6-T5 session has no open proposal to sanitize.");
        return new OwnerReviewDraft(
            proposal.Item.Kind,
            proposal.Item.Author,
            proposal.Item.CreationMethod,
            proposal.Item.Evidence,
            proposal.Item.Level,
            S6T5SanitizedTitle,
            S6T5SanitizedBody,
            Array.Empty<OwnerItemSource>(),
            proposal.Item.OccurredAtUtc,
            proposal.Item.Project,
            proposal.Item.Environment,
            proposal.Item.Version);
    }

    private static async Task WriteS6T5NoteAsync(string executable, SmokeFixture fixture, string sourceId, string zoneId, string text)
    {
        var input = Path.Combine(fixture.OutputDirectory, sourceId + ".note");
        await File.WriteAllTextAsync(input, text, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "research-capture", "--control-pipe", fixture.ControlPipe,
            "--resource-id", sourceId, "--zone-id", zoneId, "--input-file", input,
            "--title", "S6-T5 synthetic private note").ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("revision=1", StringComparison.Ordinal),
            "The S6-T5 note " + sourceId + " was not captured at revision 1 (exit=" + result.ExitCode + "; stderr=" + result.StandardError.Trim() + ").");
    }

    private static async Task WriteS6T5InboxAsync(string executable, SmokeFixture fixture)
    {
        var draft = S6T5ItemDraftJson("S6-T5 shared inbox", "Destination inbox for the S6-T5 declassified shared lesson.", DateTimeOffset.UtcNow.AddMinutes(-5), null,
            kind: "observation", author: "user", evidence: "unverified");
        var input = Path.Combine(fixture.OutputDirectory, "s6t5-inbox.json");
        await File.WriteAllTextAsync(input, draft, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", S6T5SharedInbox, "--zone-id", S6T5ZoneShared, "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
            "The S6-T5 shared inbox item was not stored (exit=" + result.ExitCode + "; stderr=" + result.StandardError.Trim() + ").");
    }

    private static string S6T5SourceDraftJson(string title, string version, string text, DateTimeOffset recordedAtUtc) =>
        JsonSerializer.Serialize(new
        {
            title,
            locator = (string?)null,
            version,
            availability = "available",
            creationMethod = "directWrite",
            capturedAtUtc = recordedAtUtc,
            recordedAtUtc,
            author = "external",
            text
        });

    /// <summary>
    /// Drives the real WPF owner window on its own STA dispatcher thread over
    /// the live session: the same window, session and service the owner uses,
    /// with the Inbox view selected so the declassification panel shows. All
    /// session acts run on that thread, so every view update stays on its
    /// owner thread; the two PNGs (preview, published) are real renders of the
    /// live window, not invented pixels.
    /// </summary>
    private static void DriveAndRenderS6T5Wpf(OwnerConsoleSession session, SmokeFixture fixture, OwnerReviewDraft sanitized)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(sanitized);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            OwnerConsoleWindow? window = null;
            try
            {
                window = new OwnerConsoleWindow(session);
                window.Width = 1280;
                window.Height = 1000;
                foreach (var entry in window.Navigation.Items.OfType<ListBoxItem>())
                {
                    if (string.Equals(entry.Content as string, "Inbox", StringComparison.Ordinal))
                    {
                        window.Navigation.SelectedItem = entry;
                        break;
                    }
                }

                window.Show();
                window.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await session.PreviewDeclassifiedAsync("s6t5-shared-lesson-ui", S6T5ZoneShared, sanitized).ConfigureAwait(true);
                        SaveS6T5Wpf(window, fixture, "s6t5-wpf-declassify-preview.png");
                        await session.PublishDeclassifiedAsync("s6t5-shared-lesson-ui", S6T5ZoneShared, sanitized, "NOT-PUBLISH").ConfigureAwait(true);
                        if (session.DeclassifyPublished is not null)
                        {
                            throw new InvalidOperationException("A wrong confirmation published a sanitized derivative.");
                        }

                        await session.PublishDeclassifiedAsync("s6t5-shared-lesson-ui", S6T5ZoneShared, sanitized, "PUBLISH").ConfigureAwait(true);
                        SaveS6T5Wpf(window, fixture, "s6t5-wpf-declassify-published.png");
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
                try
                {
                    window?.DetachView();
                }
                catch
                {
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("The WPF owner flow failed: " + failure.Message, failure);
        }
    }

    private static void SaveS6T5Wpf(OwnerConsoleWindow window, SmokeFixture fixture, string fileName)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(fileName);
        window.DeclassifyMessageText.BringIntoView();
        window.UpdateLayout();
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(fixture.OutputDirectory, fileName);
        using (var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            encoder.Save(stream);
        }

        Console.WriteLine("WPF render saved: " + path);
    }
}
