using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Core.Domain;

namespace MetaBrain.S1T4.Smoke;

// S4-T3: scoped item detail (get-by-ID) with stored citations plus honest
// current/superseded/unknown availability warnings, and independent
// source/snapshot expansion through the existing core/application/connections
// boundaries. One synthetic owner vault is provisioned and unlocked through
// the real owner CLI over the real named-pipe service. Memory items cite a
// registered source revision with validated spans; an owner grant with
// memory.read opens detail only, an explicit source.read grant opens the
// registry revision, and an explicit source.snapshot.read grant opens the
// preserved snapshot. Every positive/negative scenario below runs through the
// real owner surface (memory-read/source-read) and the real agent surface
// (agent-memory-read/agent-source-read/agent-source-snapshot-read plus the
// matching MCP bridge tools). All evidence is observed from the real service
// and real persisted files; nothing here is a mock.
internal static partial class OwnerServiceSmoke
{
    private const string S4T3Zone = "s4t3-private-zone";
    private const string S4T3ItemCited = "s4t3-item-cited";
    private const string S4T3ItemPlain = "s4t3-item-plain";
    private const string S4T3ItemOutside = "s4t3-item-outside";
    private const string S4T3Source = "s4t3-source-alpha";
    private const string S4T3GuessedItem = "s4t3-guessed-item";
    private const string S4T3GuessedSource = "s4t3-guessed-source";

    private static string S4T3ItemDraft(string title, string body, DateTimeOffset occurred, object? sources) =>
        JsonSerializer.Serialize(new
        {
            kind = "lesson",
            author = "user",
            creationMethod = "directWrite",
            evidence = "unverified",
            level = "l1",
            title,
            body,
            sources = sources ?? Array.Empty<object>(),
            occurredAtUtc = occurred,
            recordedAtUtc = occurred.AddMinutes(1),
            applicability = (object?)null
        });

    private static string S4T3SourceDraft(string title, string locator, string version, string availability, DateTimeOffset now) =>
        JsonSerializer.Serialize(new
        {
            title,
            locator,
            version,
            availability,
            creationMethod = "directWrite",
            capturedAtUtc = now,
            recordedAtUtc = now.AddMinutes(1)
        });

    internal static async Task RunS4T3DetailAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S4-T3 detail smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S4-T3 scoped item detail with citations, warnings, and independent source expansion");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
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
                "Provision did not create the S4-T3 synthetic vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");
            var unlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0 && unlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S4-T3 vault did not unlock (exit=" + unlock.ExitCode + "; stderr=" + unlock.StandardError.Trim() + ").");

            var now = DateTimeOffset.UtcNow.AddMinutes(-5);
            const string registryTitle = "Synthetic S4-T3 registry title";
            const string enrolledLocator = "s4t3-enrolled-synthetic-locator";
            await WriteS4T3SourceAsync(connectionsExecutable, fixture, S4T3Source, S4T3Zone,
                S4T3SourceDraft(registryTitle, enrolledLocator, "synthetic-source-v1", "available", now), 1).ConfigureAwait(false);
            await WriteS4T3ItemAsync(connectionsExecutable, fixture, S4T3ItemCited, S4T3Zone,
                S4T3ItemDraft("Synthetic S4-T3 cited lesson",
                    "Synthetic S4-T3 body: the approved snapshot text for detail; never a real owner secret.",
                    now,
                    new[]
                    {
                        new
                        {
                            sourceId = S4T3Source,
                            locator = (string?)null,
                            availability = "available",
                            sourceRevision = 1L,
                            span = new { start = 0L, end = 64L }
                        }
                    }), 1).ConfigureAwait(false);
            await WriteS4T3ItemAsync(connectionsExecutable, fixture, S4T3ItemPlain, S4T3Zone,
                S4T3ItemDraft("Synthetic S4-T3 plain lesson",
                    "Synthetic S4-T3 body: an item without citations; never a real owner secret.",
                    now, null), 1).ConfigureAwait(false);
            await WriteS4T3ItemAsync(connectionsExecutable, fixture, S4T3ItemOutside, S4T3Zone,
                S4T3ItemDraft("Synthetic S4-T3 outside lesson",
                    "Synthetic S4-T3 body: kept outside every grant; never a real owner secret.",
                    now, null), 1).ConfigureAwait(false);

            var rebuild = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(rebuild.ExitCode == 0 && rebuild.StandardOutput.Contains("INDEX rebuilt rows=", StringComparison.Ordinal) &&
                    !rebuild.StandardOutput.Contains("rows=0", StringComparison.Ordinal),
                "The S4-T3 private index rebuild did not publish a materialized generation (exit=" + rebuild.ExitCode + "; stdout=" + rebuild.StandardOutput.Trim() + "; stderr=" + rebuild.StandardError.Trim() + ").");

            // Owner detail first: exact approved revision, stored span, current warning-free citation.
            var ownerDetail = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S4T3ItemCited, "--resource-revision", "1").ConfigureAwait(false);
            Require(ownerDetail.ExitCode == 0 &&
                    ownerDetail.StandardOutput.Contains($"MEMORY id={S4T3ItemCited}", StringComparison.Ordinal) &&
                    ownerDetail.StandardOutput.Contains("revision=1", StringComparison.Ordinal) &&
                    ownerDetail.StandardOutput.Contains("the approved snapshot text for detail", StringComparison.Ordinal) &&
                    ownerDetail.StandardOutput.Contains($"citation id={S4T3Source} revision=1 span=0-64 status=current", StringComparison.Ordinal),
                "Owner detail did not serve the exact approved revision with its stored citation (exit=" + ownerDetail.ExitCode + "; stdout=" + ownerDetail.StandardOutput.Trim() + "; stderr=" + ownerDetail.StandardError.Trim() + ").");
            Require(!ownerDetail.StandardOutput.Contains(registryTitle, StringComparison.Ordinal) &&
                    !ownerDetail.StandardOutput.Contains(enrolledLocator, StringComparison.Ordinal),
                "Owner item detail disclosed registry-private title/locator.");
            Console.WriteLine("PASS owner detail serves the exact approved revision with a current citation");

            // Two distinct agent grants: item-only (detail without expansion) and source-only (expansion without detail).
            var itemSession = await IssueS4T3GrantAsync(connectionsExecutable, fixture, "s4t3-item", "memory.read,memory.search",
                new[] { S4T3ItemCited, S4T3ItemPlain }).ConfigureAwait(false);
            var sourceSession = await IssueS4T3GrantAsync(connectionsExecutable, fixture, "s4t3-source", "source.read",
                new[] { S4T3Source }).ConfigureAwait(false);

            // Search then detail through the owner surface and the item grant: the hit address resolves via get-by-ID.
            var search = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-search", "--agent-pipe", fixture.AgentPipe,
                "--session-file", itemSession.SessionFile, "--query", "approved snapshot",
                "--offset", "0", "--limit", "10").ConfigureAwait(false);
            Require(search.ExitCode == 0 && search.StandardOutput.Contains($"HIT id={S4T3ItemCited} revision=1", StringComparison.Ordinal),
                "Search did not surface the cited item address (exit=" + search.ExitCode + "; stdout=" + search.StandardOutput.Trim() + "; stderr=" + search.StandardError.Trim() + ").");
            var detail = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", itemSession.SessionFile,
                "--resource-id", S4T3ItemCited, "--resource-revision", "1").ConfigureAwait(false);
            Require(detail.ExitCode == 0 &&
                    detail.StandardOutput.Contains($"MEMORY id={S4T3ItemCited}", StringComparison.Ordinal) &&
                    detail.StandardOutput.Contains("the approved snapshot text for detail", StringComparison.Ordinal) &&
                    detail.StandardOutput.Contains($"citation id={S4T3Source} revision=1 span=0-64 status=current", StringComparison.Ordinal),
                "Item-grant detail did not serve the exact approved revision with its stored citation (exit=" + detail.ExitCode + "; stdout=" + detail.StandardOutput.Trim() + "; stderr=" + detail.StandardError.Trim() + ").");
            Require(!detail.StandardOutput.Contains(registryTitle, StringComparison.Ordinal) &&
                    !detail.StandardOutput.Contains(enrolledLocator, StringComparison.Ordinal),
                "Item-grant detail disclosed registry-private title/locator.");
            Console.WriteLine("PASS search-to-detail serves the exact approved revision without registry disclosure");

            // Readable item does not grant source expansion: the same item grant denies the source API.
            var itemGrantSource = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-source-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", itemSession.SessionFile,
                "--resource-id", S4T3Source, "--resource-revision", "1").ConfigureAwait(false);
            Require(itemGrantSource.ExitCode == 3 && itemGrantSource.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !itemGrantSource.StandardOutput.Contains(registryTitle, StringComparison.Ordinal),
                "An item-only grant expanded the cited source (exit=" + itemGrantSource.ExitCode + "; stdout=" + itemGrantSource.StandardOutput.Trim() + "; stderr=" + itemGrantSource.StandardError.Trim() + ").");
            Console.WriteLine("PASS readable item does not grant source expansion");

            // The authorized expansion grant opens the exact registry revision through the agent surface.
            var sourceDetail = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-source-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", sourceSession.SessionFile,
                "--resource-id", S4T3Source, "--resource-revision", "1").ConfigureAwait(false);
            Require(sourceDetail.ExitCode == 0 &&
                    sourceDetail.StandardOutput.Contains($"SOURCE id={S4T3Source}", StringComparison.Ordinal) &&
                    sourceDetail.StandardOutput.Contains("revision=1", StringComparison.Ordinal) &&
                    sourceDetail.StandardOutput.Contains("availability=available", StringComparison.Ordinal),
                "Source-grant expansion did not open the exact registry revision (exit=" + sourceDetail.ExitCode + "; stdout=" + sourceDetail.StandardOutput.Trim() + "; stderr=" + sourceDetail.StandardError.Trim() + ").");
            var sourceCrossItem = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", sourceSession.SessionFile,
                "--resource-id", S4T3ItemCited, "--resource-revision", "1").ConfigureAwait(false);
            Require(sourceCrossItem.ExitCode == 3 && sourceCrossItem.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !sourceCrossItem.StandardOutput.Contains("the approved snapshot text for detail", StringComparison.Ordinal),
                "A source-only grant read the item body (exit=" + sourceCrossItem.ExitCode + "; stdout=" + sourceCrossItem.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS authorized source expansion opens the registry revision; source grant opens no items");

            // MCP bridge: distinct memory/source tools honor the same independent scopes.
            var bridgeList = await RunBridgeToolsListAsync(connectionsExecutable, fixture.AgentPipe).ConfigureAwait(false);
            Require(bridgeList.Contains("metabrain_memory_read", StringComparison.Ordinal) &&
                    bridgeList.Contains("metabrain_source_read", StringComparison.Ordinal) &&
                    bridgeList.Contains("metabrain_source_snapshot_read", StringComparison.Ordinal),
                "The MCP bridge did not advertise the S4-T3 detail/expansion tools.");
            var bridgeDetail = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_memory_read", new { sessionFile = itemSession.SessionFile, resourceId = S4T3ItemCited, resourceRevision = "1" }).ConfigureAwait(false);
            Require(bridgeDetail.Contains("the approved snapshot text for detail", StringComparison.Ordinal) &&
                    !bridgeDetail.Contains(registryTitle, StringComparison.Ordinal) &&
                    !bridgeDetail.Contains(enrolledLocator, StringComparison.Ordinal) &&
                    !bridgeDetail.Contains("mb1_", StringComparison.Ordinal),
                "The MCP bridge detail did not serve the approved body without registry/bearer material.");
            var bridgeDeniedSource = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_source_read", new { sessionFile = itemSession.SessionFile, resourceId = S4T3Source, resourceRevision = "1" }).ConfigureAwait(false);
            Require(bridgeDeniedSource.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !bridgeDeniedSource.Contains(registryTitle, StringComparison.Ordinal),
                "The MCP bridge let an item grant expand the source.");
            var bridgeSource = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_source_read", new { sessionFile = sourceSession.SessionFile, resourceId = S4T3Source, resourceRevision = "1" }).ConfigureAwait(false);
            Require(bridgeSource.Contains(S4T3Source, StringComparison.Ordinal) &&
                    !bridgeSource.Contains("mb1_", StringComparison.Ordinal),
                "The MCP bridge did not serve the authorized source expansion.");
            Console.WriteLine("PASS MCP bridge detail/expansion honors the independent scopes");

            // Wrong scope, wrong revision, and guessed IDs share one generic denial with no private existence signal.
            var wrongScope = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", itemSession.SessionFile,
                "--resource-id", S4T3ItemOutside, "--resource-revision", "1").ConfigureAwait(false);
            Require(wrongScope.ExitCode == 3 && wrongScope.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !wrongScope.StandardOutput.Contains("kept outside every grant", StringComparison.Ordinal),
                "An out-of-scope item read was served or leaked its body.");
            var wrongRevision = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", itemSession.SessionFile,
                "--resource-id", S4T3ItemCited, "--resource-revision", "999").ConfigureAwait(false);
            Require(wrongRevision.ExitCode == 3 && wrongRevision.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "A wrong-revision detail read was served.");
            var guessedItem = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", itemSession.SessionFile,
                "--resource-id", S4T3GuessedItem, "--resource-revision", "1").ConfigureAwait(false);
            Require(guessedItem.ExitCode == 3 && guessedItem.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !guessedItem.StandardOutput.Contains("MEMORY id=", StringComparison.Ordinal),
                "A guessed item ID was not denied generically.");
            var guessedSource = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-source-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", sourceSession.SessionFile,
                "--resource-id", S4T3GuessedSource, "--resource-revision", "1").ConfigureAwait(false);
            Require(guessedSource.ExitCode == 3 && guessedSource.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !guessedSource.StandardOutput.Contains("SOURCE id=", StringComparison.Ordinal),
                "A guessed source ID was not denied generically.");
            var missingRevisionCompare = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-source-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", sourceSession.SessionFile,
                "--resource-id", S4T3Source, "--resource-revision", "999").ConfigureAwait(false);
            Require(missingRevisionCompare.ExitCode == 3 && missingRevisionCompare.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "A wrong source revision was served.");
            Console.WriteLine("PASS wrong scope/revision and guessed IDs deny generically without existence signals");

            // Source mutation boundary: revise the registered source, then detail must warn superseded
            // while still serving the exact approved snapshot bytes (no silent body replacement).
            // The registered revision moves from available/v1 to unavailable/v2 while the stored
            // citation anchor stays pinned at revision 1: detail keeps the frozen bytes and warns.
            await WriteS4T3SourceReviseAsync(connectionsExecutable, fixture, S4T3Source, S4T3Zone,
                S4T3SourceDraft(registryTitle, enrolledLocator, "synthetic-source-v2", "unavailable", now), 1, 2).ConfigureAwait(false);
            var superseded = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", itemSession.SessionFile,
                "--resource-id", S4T3ItemCited, "--resource-revision", "1").ConfigureAwait(false);
            Require(superseded.ExitCode == 0 &&
                    superseded.StandardOutput.Contains("the approved snapshot text for detail", StringComparison.Ordinal) &&
                    superseded.StandardOutput.Contains($"citation id={S4T3Source} revision=1 span=0-64 status=superseded", StringComparison.Ordinal) &&
                    superseded.StandardOutput.Contains("warning=citation_source_superseded", StringComparison.Ordinal),
                "Detail after the source revision did not warn superseded while serving the frozen bytes (exit=" + superseded.ExitCode + "; stdout=" + superseded.StandardOutput.Trim() + ").");
            var ownerSuperseded = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S4T3ItemCited, "--resource-revision", "1").ConfigureAwait(false);
            Require(ownerSuperseded.ExitCode == 0 &&
                    ownerSuperseded.StandardOutput.Contains("status=superseded", StringComparison.Ordinal) &&
                    ownerSuperseded.StandardOutput.Contains("warning=citation_source_superseded", StringComparison.Ordinal),
                "Owner detail after the source revision did not carry the superseded warning.");
            Console.WriteLine("PASS source mutation warns superseded without silently replacing the snapshot body");

            // New canonical revisions need approval again: revise the item and prove the frozen grant
            // cannot reach revision 2 while a fresh grant can.
            await WriteS4T3ItemReviseAsync(connectionsExecutable, fixture, S4T3ItemCited, S4T3Zone,
                S4T3ItemDraft("Synthetic S4-T3 cited lesson",
                    "Synthetic S4-T3 body: revised text that the frozen grant must never see; never a real owner secret.",
                    now,
                    new[]
                    {
                        new
                        {
                            sourceId = S4T3Source,
                            locator = (string?)null,
                            availability = "unavailable",
                            sourceRevision = 2L,
                            span = new { start = 0L, end = 64L }
                        }
                    }), 1, 2).ConfigureAwait(false);
            var frozenStale = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", itemSession.SessionFile,
                "--resource-id", S4T3ItemCited, "--resource-revision", "2").ConfigureAwait(false);
            Require(frozenStale.ExitCode == 3 && frozenStale.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !frozenStale.StandardOutput.Contains("must never see", StringComparison.Ordinal),
                "The frozen grant reached the unapproved new revision.");
            var frozenOld = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", itemSession.SessionFile,
                "--resource-id", S4T3ItemCited, "--resource-revision", "1").ConfigureAwait(false);
            Require(frozenOld.ExitCode == 3 && frozenOld.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !frozenOld.StandardOutput.Contains("must never see", StringComparison.Ordinal) &&
                    !frozenOld.StandardOutput.Contains("the approved snapshot text for detail", StringComparison.Ordinal),
                "The superseded frozen item revision stayed servable instead of failing closed.");
            var freshItemSession = await IssueS4T3GrantAsync(connectionsExecutable, fixture, "s4t3-item-fresh", "memory.read,memory.search",
                new[] { S4T3ItemCited }).ConfigureAwait(false);
            var freshDetail = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", freshItemSession.SessionFile,
                "--resource-id", S4T3ItemCited, "--resource-revision", "2").ConfigureAwait(false);
            Require(freshDetail.ExitCode == 0 &&
                    freshDetail.StandardOutput.Contains("must never see", StringComparison.Ordinal) &&
                    freshDetail.StandardOutput.Contains($"citation id={S4T3Source} revision=2 span=0-64 status=current", StringComparison.Ordinal),
                "A fresh grant on the new revision did not serve its exact body with a current citation.");
            Console.WriteLine("PASS new revisions need fresh approval; frozen grants never drift onto replacement bodies");
            // Live enrolled physical source boundary: a real owner-enrolled synthetic
            // Codex-format session file enrolled under the same source ID that backs a
            // cited registry revision. Deleting the file flips the owner status
            // probe to missing (registered availability to unavailable) while the next
            // detail read keeps serving the exact frozen body (never wrong bytes),
            // and ingest refuses explicitly with source_missing. Rewriting with
            // different bytes flips the probe to changed and ingest refuses without
            // advancing. Restoring plus explicit re-enrollment starts generation 2
            // and status returns to available.
            const string enrolledSourceId = "s4t3-source-enrolled";
            const string enrolledItemId = "s4t3-item-enrolled";
            var enrolledMarker = "S4T3-ENROLLED-" + Guid.NewGuid().ToString("N")[..12];
            var enrolledSessionId = "s4t3-session-" + Guid.NewGuid().ToString("N")[..12];
            var enrolledPath = Path.Combine(fixture.OutputDirectory, "s4t3-enrolled-session.jsonl");
            var enrolledBytes = Encoding.UTF8.GetBytes(S4T3EnrolledSession(enrolledSessionId, enrolledMarker));
            await File.WriteAllBytesAsync(enrolledPath, enrolledBytes).ConfigureAwait(false);
            var enrolledRequestPath = Path.Combine(fixture.OutputDirectory, "s4t3-enrolled-request.json");
            await File.WriteAllTextAsync(enrolledRequestPath, JsonSerializer.Serialize(new { locator = enrolledPath })).ConfigureAwait(false);
            var enrolled = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", enrolledSourceId, "--zone-id", S4T3Zone,
                "--format", CodexSessionNormalizer.FormatName, "--format-version", "1",
                "--input-file", enrolledRequestPath).ConfigureAwait(false);
            Require(enrolled.ExitCode == 0 &&
                    enrolled.StandardOutput.Contains($"SOURCE id={enrolledSourceId}", StringComparison.Ordinal) &&
                    enrolled.StandardOutput.Contains("generation=1", StringComparison.Ordinal) &&
                    !enrolled.StandardOutput.Contains(enrolledPath, StringComparison.Ordinal),
                "Owner enrollment of the live S4-T3 enrolled fixture failed or echoed the private locator (exit=" + enrolled.ExitCode + "; stdout=" + enrolled.StandardOutput.Trim() + "; stderr=" + enrolled.StandardError.Trim() + ").");
            await WriteS4T3SourceAsync(connectionsExecutable, fixture, enrolledSourceId, S4T3Zone,
                S4T3SourceDraft("Synthetic S4-T3 enrolled registry title", enrolledPath, "synthetic-enrolled-v1", "available", now), 1).ConfigureAwait(false);
            await WriteS4T3ItemAsync(connectionsExecutable, fixture, enrolledItemId, S4T3Zone,
                S4T3ItemDraft("Synthetic S4-T3 enrolled lesson",
                    "Synthetic S4-T3 body: enrolled-source frozen bytes; never a real owner secret.",
                    now,
                    new[]
                    {
                        new
                        {
                            sourceId = enrolledSourceId,
                            locator = (string?)null,
                            availability = "available",
                            sourceRevision = 1L,
                            span = new { start = 0L, end = 32L }
                        }
                    }), 1).ConfigureAwait(false);
            var enrolledGrant = await IssueS4T3GrantAsync(connectionsExecutable, fixture, "s4t3-enrolled-item", "memory.read",
                new[] { enrolledItemId }).ConfigureAwait(false);
            var enrolledSourceGrant = await IssueS4T3GrantAsync(connectionsExecutable, fixture, "s4t3-enrolled-source", "source.read",
                new[] { enrolledSourceId }).ConfigureAwait(false);
            var enrolledDetail = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", enrolledGrant.SessionFile,
                "--resource-id", enrolledItemId, "--resource-revision", "1").ConfigureAwait(false);
            Require(enrolledDetail.ExitCode == 0 &&
                    enrolledDetail.StandardOutput.Contains("enrolled-source frozen bytes", StringComparison.Ordinal) &&
                    enrolledDetail.StandardOutput.Contains($"citation id={enrolledSourceId} revision=1 span=0-32 status=current", StringComparison.Ordinal),
                "Enrolled-source detail did not serve the frozen body with a current citation (exit=" + enrolledDetail.ExitCode + "; stdout=" + enrolledDetail.StandardOutput.Trim() + ").");
            var enrolledSourceRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-source-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", enrolledSourceGrant.SessionFile,
                "--resource-id", enrolledSourceId, "--resource-revision", "1").ConfigureAwait(false);
            Require(enrolledSourceRead.ExitCode == 0 &&
                    enrolledSourceRead.StandardOutput.Contains($"SOURCE id={enrolledSourceId}", StringComparison.Ordinal) &&
                    enrolledSourceRead.StandardOutput.Contains("availability=available", StringComparison.Ordinal) &&
                    enrolledSourceRead.StandardOutput.Contains("Synthetic S4-T3 enrolled registry title", StringComparison.Ordinal),
                "The enrolled source grant did not open the exact registry revision (exit=" + enrolledSourceRead.ExitCode + "; stdout=" + enrolledSourceRead.StandardOutput.Trim() + ").");
            var enrolledStatus = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", enrolledSourceId).ConfigureAwait(false);
            Require(enrolledStatus.ExitCode == 0 && enrolledStatus.StandardOutput.Contains("availability=available", StringComparison.Ordinal),
                "The live enrolled source did not report available before the missing leg (exit=" + enrolledStatus.ExitCode + "; stdout=" + enrolledStatus.StandardOutput.Trim() + ").");
            File.Delete(enrolledPath);
            var missingStatus = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", enrolledSourceId).ConfigureAwait(false);
            Require(missingStatus.ExitCode == 0 && missingStatus.StandardOutput.Contains("availability=missing", StringComparison.Ordinal),
                "Deleting the live enrolled file did not flip the owner status probe to missing (exit=" + missingStatus.ExitCode + "; stdout=" + missingStatus.StandardOutput.Trim() + ").");
            // Locator match lets the availability sync advance the registry: the
            // probe reports missing with sourceReferenceRevision=2, so the pinned
            // registry revision 2 reads unavailable while the item citation (rev 1)
            // renders superseded — frozen bytes stay servable, never merged.
            Require(missingStatus.StandardOutput.Contains("sourceReferenceRevision=2", StringComparison.Ordinal),
                "The missing probe did not advance the registered availability revision (stdout=" + missingStatus.StandardOutput.Trim() + ").");
            var missingRegistry = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", enrolledSourceId, "--resource-revision", "2").ConfigureAwait(false);
            Require(missingRegistry.ExitCode == 0 &&
                    missingRegistry.StandardOutput.Contains($"SOURCE id={enrolledSourceId}", StringComparison.Ordinal) &&
                    missingRegistry.StandardOutput.Contains("availability=unavailable", StringComparison.Ordinal),
                "The registered source did not report unavailable at the availability revision after the file went missing (exit=" + missingRegistry.ExitCode + "; stdout=" + missingRegistry.StandardOutput.Trim() + ").");
            var missingFrozenSource = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-source-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", enrolledSourceGrant.SessionFile,
                "--resource-id", enrolledSourceId, "--resource-revision", "1").ConfigureAwait(false);
            Require(missingFrozenSource.ExitCode == 3 && missingFrozenSource.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "The frozen source grant drifted onto the post-missing revision instead of failing closed.");
            var missingDetail = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", enrolledGrant.SessionFile,
                "--resource-id", enrolledItemId, "--resource-revision", "1").ConfigureAwait(false);
            Require(missingDetail.ExitCode == 0 &&
                    missingDetail.StandardOutput.Contains("enrolled-source frozen bytes", StringComparison.Ordinal) &&
                    missingDetail.StandardOutput.Contains($"citation id={enrolledSourceId} revision=1 span=0-32 status=superseded", StringComparison.Ordinal) &&
                    missingDetail.StandardOutput.Contains("warning=citation_source_superseded", StringComparison.Ordinal) &&
                    !missingDetail.StandardOutput.Contains(enrolledMarker, StringComparison.Ordinal),
                "Detail after the enrolled file went missing did not keep serving the exact frozen bytes with a superseded warning (exit=" + missingDetail.ExitCode + "; stdout=" + missingDetail.StandardOutput.Trim() + ").");
            var missingIngest = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", enrolledSourceId).ConfigureAwait(false);
            Require(missingIngest.ExitCode == 4 && missingIngest.StandardError.Contains("source_missing", StringComparison.Ordinal),
                "Ingest of the deleted enrolled source did not refuse explicitly with source_missing (exit=" + missingIngest.ExitCode + "; stdout=" + missingIngest.StandardOutput.Trim() + "; stderr=" + missingIngest.StandardError.Trim() + ").");
            var changedBytes = Encoding.UTF8.GetBytes(S4T3EnrolledSession(enrolledSessionId, "S4T3-CHANGED-" + Guid.NewGuid().ToString("N")[..12]));
            await File.WriteAllBytesAsync(enrolledPath, changedBytes).ConfigureAwait(false);
            var changedStatus = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", enrolledSourceId).ConfigureAwait(false);
            Require(changedStatus.ExitCode == 0 && changedStatus.StandardOutput.Contains("availability=changed", StringComparison.Ordinal),
                "Rewriting the live enrolled file with different bytes did not flip the probe to changed (exit=" + changedStatus.ExitCode + "; stdout=" + changedStatus.StandardOutput.Trim() + ").");
            Require(changedStatus.StandardOutput.Contains("sourceReferenceRevision=2", StringComparison.Ordinal),
                "The changed probe did not keep the registered availability revision (stdout=" + changedStatus.StandardOutput.Trim() + ").");
            var changedIngest = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", enrolledSourceId).ConfigureAwait(false);
            Require(changedIngest.ExitCode != 0 && !changedIngest.StandardOutput.Contains("status=ingest_advanced", StringComparison.Ordinal),
                "Ingest of the rewritten enrolled source silently advanced instead of refusing (exit=" + changedIngest.ExitCode + "; stdout=" + changedIngest.StandardOutput.Trim() + "; stderr=" + changedIngest.StandardError.Trim() + ").");
            Require(changedIngest.StandardError.Contains("source_generation_changed", StringComparison.Ordinal) ||
                    changedIngest.StandardError.Contains("source_unreadable", StringComparison.Ordinal) ||
                    changedIngest.StandardError.Contains("source_changed", StringComparison.Ordinal) ||
                    changedIngest.StandardError.Contains("generation_", StringComparison.Ordinal),
                "Ingest of the rewritten enrolled source did not refuse with an explicit generation error (stderr=" + changedIngest.StandardError.Trim() + ").");
            var changedDetail = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", enrolledGrant.SessionFile,
                "--resource-id", enrolledItemId, "--resource-revision", "1").ConfigureAwait(false);
            Require(changedDetail.ExitCode == 0 &&
                    changedDetail.StandardOutput.Contains("enrolled-source frozen bytes", StringComparison.Ordinal) &&
                    changedDetail.StandardOutput.Contains($"citation id={enrolledSourceId} revision=1 span=0-32 status=superseded", StringComparison.Ordinal) &&
                    changedDetail.StandardOutput.Contains("warning=citation_source_superseded", StringComparison.Ordinal) &&
                    !changedDetail.StandardOutput.Contains("S4T3-CHANGED-", StringComparison.Ordinal),
                "Detail after the enrolled file changed did not keep serving the frozen bytes with a superseded warning (exit=" + changedDetail.ExitCode + "; stdout=" + changedDetail.StandardOutput.Trim() + ").");
            await File.WriteAllBytesAsync(enrolledPath, enrolledBytes).ConfigureAwait(false);
            // The changed-file ingest quarantined the source (generation_truncated):
            // status now reports that life explicitly with no availability probe
            // and no silent recovery — this is the authority failing closed.
            var quarantinedStatus = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", enrolledSourceId).ConfigureAwait(false);
            Require(quarantinedStatus.ExitCode == 0 &&
                    quarantinedStatus.StandardOutput.Contains("life=quarantined", StringComparison.Ordinal) &&
                    quarantinedStatus.StandardOutput.Contains("quarantine=generation_truncated", StringComparison.Ordinal),
                "The changed-file ingest did not quarantine the source explicitly (exit=" + quarantinedStatus.ExitCode + "; stdout=" + quarantinedStatus.StandardOutput.Trim() + ").");
            var quarantinedDetail = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", enrolledGrant.SessionFile,
                "--resource-id", enrolledItemId, "--resource-revision", "1").ConfigureAwait(false);
            Require(quarantinedDetail.ExitCode == 0 &&
                    quarantinedDetail.StandardOutput.Contains("enrolled-source frozen bytes", StringComparison.Ordinal) &&
                    quarantinedDetail.StandardOutput.Contains($"citation id={enrolledSourceId} revision=1 span=0-32 status=superseded", StringComparison.Ordinal) &&
                    quarantinedDetail.StandardOutput.Contains("warning=citation_source_superseded", StringComparison.Ordinal),
                "Detail while quarantined did not keep serving the frozen bytes with a warning (exit=" + quarantinedDetail.ExitCode + "; stdout=" + quarantinedDetail.StandardOutput.Trim() + ").");
            // Explicit owner revoke clears the path, then explicit re-enrollment of
            // the restored bytes starts generation 2 on a fresh checkpoint with
            // status back to available — the only supported re-enrollment route
            // out of quarantine.
            var revokedEnrolled = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-revoke", "--control-pipe", fixture.ControlPipe,
                "--source-id", enrolledSourceId).ConfigureAwait(false);
            Require(revokedEnrolled.ExitCode == 0 && revokedEnrolled.StandardOutput.Contains("life=revoked", StringComparison.Ordinal),
                "Owner revoke of the quarantined enrolled source failed (exit=" + revokedEnrolled.ExitCode + "; stdout=" + revokedEnrolled.StandardOutput.Trim() + "; stderr=" + revokedEnrolled.StandardError.Trim() + ").");
            var reEnrolled = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", enrolledSourceId, "--zone-id", S4T3Zone,
                "--format", CodexSessionNormalizer.FormatName, "--format-version", "1",
                "--input-file", enrolledRequestPath).ConfigureAwait(false);
            Require(reEnrolled.ExitCode == 0 && reEnrolled.StandardOutput.Contains("generation=2", StringComparison.Ordinal),
                "Explicit re-enrollment after revoke did not start generation 2 (exit=" + reEnrolled.ExitCode + "; stdout=" + reEnrolled.StandardOutput.Trim() + "; stderr=" + reEnrolled.StandardError.Trim() + ").");
            var reEnrollStatus = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", enrolledSourceId).ConfigureAwait(false);
            Require(reEnrollStatus.ExitCode == 0 &&
                    reEnrollStatus.StandardOutput.Contains("availability=available", StringComparison.Ordinal) &&
                    reEnrollStatus.StandardOutput.Contains("generation=2", StringComparison.Ordinal),
                "The re-enrolled source did not report available at generation 2 (exit=" + reEnrollStatus.ExitCode + "; stdout=" + reEnrollStatus.StandardOutput.Trim() + ").");
            var reEnrollIngest = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", enrolledSourceId).ConfigureAwait(false);
            Require(reEnrollIngest.ExitCode == 0 && reEnrollIngest.StandardOutput.Contains("status=ingest_advanced", StringComparison.Ordinal),
                "Ingest of the re-enrolled generation did not advance on the fresh checkpoint (exit=" + reEnrollIngest.ExitCode + "; stdout=" + reEnrollIngest.StandardOutput.Trim() + "; stderr=" + reEnrollIngest.StandardError.Trim() + ").");
            var reEnrollDetail = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", enrolledGrant.SessionFile,
                "--resource-id", enrolledItemId, "--resource-revision", "1").ConfigureAwait(false);
            Require(reEnrollDetail.ExitCode == 0 &&
                    reEnrollDetail.StandardOutput.Contains("enrolled-source frozen bytes", StringComparison.Ordinal) &&
                    reEnrollDetail.StandardOutput.Contains($"citation id={enrolledSourceId} revision=1 span=0-32 status=superseded", StringComparison.Ordinal) &&
                    reEnrollDetail.StandardOutput.Contains("warning=citation_source_superseded", StringComparison.Ordinal),
                "Detail after re-enrollment did not keep serving the frozen generation-1 bytes with a warning (exit=" + reEnrollDetail.ExitCode + "; stdout=" + reEnrollDetail.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS live enrolled source missing/changed/re-enrollment keeps frozen bytes and never serves wrong bytes");

            // Snapshot expansion stays independent: the snapshot operation authorizes only a
            // snapshot ID, so neither the item grant nor the source grant can name a preserved
            // snapshot ID through the snapshot reader. Both deny generically without serving
            // preserved bytes; the positive path for the two new snapshot surfaces is proven
            // below against a live enrolled source with its own separately authorized grant.
            var snapshotDeniedByItem = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-source-snapshot-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", itemSession.SessionFile,
                "--snapshot-id", "snap-" + S4T3Source + "-g1", "--snapshot-revision", "1").ConfigureAwait(false);
            Require(snapshotDeniedByItem.ExitCode == 3 && snapshotDeniedByItem.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "An item grant expanded a preserved snapshot.");
            var snapshotDeniedBySource = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-source-snapshot-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", sourceSession.SessionFile,
                "--snapshot-id", "snap-" + S4T3Source + "-g1", "--snapshot-revision", "1").ConfigureAwait(false);
            Require(snapshotDeniedBySource.ExitCode == 3 && snapshotDeniedBySource.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "A source-reference grant expanded a preserved snapshot.");
            // Positive path for the two new snapshot surfaces: preserve the exact live
            // enrolled bytes under owner selection, then expand through the separately
            // authorized snapshot grant only — the new CLI and the new MCP tool must
            // both serve the exact bytes at the exact snapshot revision, while the
            // item and source grants keep denying the same snapshot address.
            var retainUntilValue = DateTimeOffset.UtcNow.AddHours(2);
            // The re-enroll status probe advanced the registry to revision 3
            // (availability sync on the fresh generation); preserve names it.
            var liveEnrolledRevision = S4T3SnapshotField(reEnrollStatus.StandardOutput, "sourceReferenceRevision");
            Require(liveEnrolledRevision == "3",
                "The re-enrolled registry did not pin revision 3 (stdout=" + reEnrollStatus.StandardOutput.Trim() + ").");
            var preserveReply = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceSnapshotPreserveOperation,
                resourceId = enrolledSourceId,
                resourceRevision = long.Parse(liveEnrolledRevision, CultureInfo.InvariantCulture),
                snapshotRetainUntilUtc = retainUntilValue
            }).ConfigureAwait(false);
            Require(S4T3ReplyCode(preserveReply) == "source_snapshot_preserved",
                "Owner-selected preservation of the live enrolled source failed (reply=" + preserveReply.GetRawText() + "; liveRevision=" + liveEnrolledRevision + ").");
            var preservedSnapshot = preserveReply.GetProperty("sourceSnapshot");
            var snapshotId = preservedSnapshot.GetProperty("id").GetString() ?? string.Empty;
            Require(SourceSnapshotIds.IsSnapshotId(snapshotId) &&
                    string.Equals(snapshotId, SourceSnapshotIds.For(enrolledSourceId, 2), StringComparison.Ordinal),
                "The preserved snapshot ID did not name the re-enrolled generation (reply=" + preserveReply.GetRawText() + ").");
            var snapshotDigest = preservedSnapshot.GetProperty("sourceDigest").GetString() ?? string.Empty;
            Require(string.Equals(snapshotDigest, S4T3Digest(enrolledBytes), StringComparison.Ordinal),
                "The preserved snapshot digest did not match the exact enrolled bytes.");
            Require(preservedSnapshot.GetProperty("sourceLength").GetInt64() == enrolledBytes.Length,
                "The preserved snapshot length did not match the exact enrolled bytes.");
            var snapshotSession = await IssueS4T3GrantAsync(connectionsExecutable, fixture, "s4t3-snapshot", "source.snapshot.read",
                new[] { snapshotId }).ConfigureAwait(false);
            var snapshotExport = Path.Combine(fixture.OutputDirectory, "s4t3-snapshot-export.bin");
            var ownerSnapshotRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-snapshot-read", "--control-pipe", fixture.ControlPipe,
                "--snapshot-id", snapshotId, "--snapshot-revision", "1",
                "--output-file", snapshotExport).ConfigureAwait(false);
            Require(ownerSnapshotRead.ExitCode == 0 && ownerSnapshotRead.StandardOutput.Contains($"id={snapshotId}", StringComparison.Ordinal),
                "The owner snapshot read did not serve the preserved snapshot (exit=" + ownerSnapshotRead.ExitCode + "; stdout=" + ownerSnapshotRead.StandardOutput.Trim() + ").");
            var exportedBytes = await File.ReadAllBytesAsync(snapshotExport).ConfigureAwait(false);
            try
            {
                Require(exportedBytes.AsSpan().SequenceEqual(enrolledBytes),
                    "The owner snapshot export did not match the exact enrolled bytes.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(exportedBytes);
                File.Delete(snapshotExport);
            }
            var agentSnapshot = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-source-snapshot-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", snapshotSession.SessionFile,
                "--snapshot-id", snapshotId, "--snapshot-revision", "1").ConfigureAwait(false);
            Require(agentSnapshot.ExitCode == 0 &&
                    agentSnapshot.StandardOutput.Contains($"SOURCE SNAPSHOT id={snapshotId}", StringComparison.Ordinal) &&
                    agentSnapshot.StandardOutput.Contains("revision=1", StringComparison.Ordinal) &&
                    agentSnapshot.StandardOutput.Contains($"digest={snapshotDigest}", StringComparison.Ordinal),
                "The new agent-source-snapshot-read CLI did not serve the exact preserved snapshot (exit=" + agentSnapshot.ExitCode + "; stdout=" + agentSnapshot.StandardOutput.Trim() + "; stderr=" + agentSnapshot.StandardError.Trim() + ").");
            var bridgeSnapshot = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_source_snapshot_read", new { sessionFile = snapshotSession.SessionFile, snapshotId, snapshotRevision = "1" }).ConfigureAwait(false);
            string bridgeSnapshotText;
            using (var bridgeSnapshotDocument = JsonDocument.Parse(bridgeSnapshot))
            {
                bridgeSnapshotText = bridgeSnapshotDocument.RootElement
                    .GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
            }

            Require(bridgeSnapshotText.Contains(snapshotId, StringComparison.Ordinal) &&
                    bridgeSnapshotText.Contains(snapshotDigest, StringComparison.Ordinal) &&
                    !bridgeSnapshotText.Contains("mb1_", StringComparison.Ordinal),
                "The new metabrain_source_snapshot_read bridge tool did not serve the exact preserved snapshot (text=" + bridgeSnapshotText + ").");
            var snapshotDeniedLockedGrant = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-source-snapshot-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", itemSession.SessionFile,
                "--snapshot-id", snapshotId, "--snapshot-revision", "1").ConfigureAwait(false);
            Require(snapshotDeniedLockedGrant.ExitCode == 3 && snapshotDeniedLockedGrant.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "An item grant expanded the newly preserved snapshot.");
            var snapshotDeniedSourceGrant = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-source-snapshot-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", sourceSession.SessionFile,
                "--snapshot-id", snapshotId, "--snapshot-revision", "1").ConfigureAwait(false);
            Require(snapshotDeniedSourceGrant.ExitCode == 3 && snapshotDeniedSourceGrant.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "A source-reference grant expanded the newly preserved snapshot.");
            Console.WriteLine("PASS snapshot expansion stays independent of item/source grants");
            Console.WriteLine("PASS new snapshot CLI and MCP tool serve the exact preserved bytes through a separately authorized grant");
            var revoke = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "revoke-session", "--control-pipe", fixture.ControlPipe,
                "--session-id", itemSession.SessionId).ConfigureAwait(false);
            Require(revoke.ExitCode == 0, "The owner could not revoke the S4-T3 item session.");
            var revokedDetail = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", itemSession.SessionFile,
                "--resource-id", S4T3ItemCited, "--resource-revision", "1").ConfigureAwait(false);
            Require(revokedDetail.ExitCode == 3 && revokedDetail.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !revokedDetail.StandardOutput.Contains("MEMORY id=", StringComparison.Ordinal),
                "A revoked session still served cached detail (exit=" + revokedDetail.ExitCode + "; stdout=" + revokedDetail.StandardOutput.Trim() + ").");
            var revokedSource = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-source-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", itemSession.SessionFile,
                "--resource-id", S4T3Source, "--resource-revision", "1").ConfigureAwait(false);
            Require(revokedSource.ExitCode == 3 && revokedSource.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "A revoked session still expanded the source.");
            Console.WriteLine("PASS revocation between search and get denies cached detail/expansion");

            // Lock denies detail/source reads and cached serving on a live session.
            var lockSession = await IssueS4T3GrantAsync(connectionsExecutable, fixture, "s4t3-lock", "memory.read",
                new[] { S4T3ItemPlain }).ConfigureAwait(false);
            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The owner could not lock after the S4-T3 walkthrough.");
            var lockedDetail = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-memory-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", lockSession.SessionFile,
                "--resource-id", S4T3ItemPlain, "--resource-revision", "1").ConfigureAwait(false);
            Require(lockedDetail.ExitCode == 3 && !lockedDetail.StandardOutput.Contains("MEMORY id=", StringComparison.Ordinal),
                "A locked vault served item detail (exit=" + lockedDetail.ExitCode + "; stdout=" + lockedDetail.StandardOutput.Trim() + ").");
            var lockedSource = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-source-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", sourceSession.SessionFile,
                "--resource-id", S4T3Source, "--resource-revision", "2").ConfigureAwait(false);
            Require(lockedSource.ExitCode == 3 && !lockedSource.StandardOutput.Contains("SOURCE id=", StringComparison.Ordinal),
                "A locked vault served source expansion (exit=" + lockedSource.ExitCode + "; stdout=" + lockedSource.StandardOutput.Trim() + ").");
            var lockedOwner = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S4T3ItemPlain).ConfigureAwait(false);
            Require(lockedOwner.ExitCode != 0,
                "A locked vault served owner detail.");
            Console.WriteLine("PASS lock denies detail/source reads and cached serving");

            Console.WriteLine("PASS S4-T3 scoped detail walkthrough with independent expansion and honest warnings");
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

    private static async Task WriteS4T3ItemAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson, long expectedRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s4t3-item-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"MEMORY stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Memory create did not store {resourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
        File.Delete(input);
    }

    private static async Task WriteS4T3ItemReviseAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson, long writeRevision, long expectedRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s4t3-item-revise-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--expected-revision", writeRevision.ToString(CultureInfo.InvariantCulture),
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"MEMORY stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Memory revise did not store {resourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
        File.Delete(input);
    }

    private static async Task WriteS4T3SourceAsync(string executable, SmokeFixture fixture, string sourceId, string zoneId, string draftJson, long expectedRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s4t3-source-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "source-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", sourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"SOURCE stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Source create did not store {sourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
        File.Delete(input);
    }

    private static async Task WriteS4T3SourceReviseAsync(string executable, SmokeFixture fixture, string sourceId, string zoneId, string draftJson, long writeRevision, long expectedRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s4t3-source-revise-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "source-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", sourceId, "--zone-id", zoneId,
            "--expected-revision", writeRevision.ToString(CultureInfo.InvariantCulture),
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"SOURCE stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Source revise did not store {sourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
        File.Delete(input);
    }

    private sealed record S4T3Session(string SessionId, string SessionFile);

    private static async Task<S4T3Session> IssueS4T3GrantAsync(string executable, SmokeFixture fixture, string name, string operations, string[] resourceIds)
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O", CultureInfo.InvariantCulture);
        var handoff = Path.Combine(fixture.OutputDirectory, name + ".grant-handoff");
        var sessionFile = Path.Combine(fixture.OutputDirectory, name + ".session-handoff");
        var grant = await RunOwnerCommandWithInputAsync(executable, "ISSUE" + Environment.NewLine,
            "owner", "grant", "--control-pipe", fixture.ControlPipe,
            "--resource-ids", string.Join(",", resourceIds),
            "--operations", operations,
            "--expires-at-utc", expiry,
            "--handoff-file", handoff).ConfigureAwait(false);
        Require(grant.ExitCode == 0,
            $"The owner could not issue the synthetic S4-T3 grant {name} (exit={grant.ExitCode}; stdout={grant.StandardOutput.Trim()}; stderr={grant.StandardError.Trim()}).");
        VerifyOwnerHandoffAcl(handoff);
        var redeem = await RunOwnerCommandAsync(executable,
            "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
            "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
        Require(redeem.ExitCode == 0 && redeem.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
            $"Redemption failed for the synthetic S4-T3 grant {name} (exit={redeem.ExitCode}; stderr={redeem.StandardError.Trim()}).");
        VerifyOwnerHandoffAcl(sessionFile);
        return new S4T3Session(ParseSessionId(redeem.StandardOutput), sessionFile);
    }
    private static string S4T3SnapshotField(string output, string field)
    {
        foreach (var part in output.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith(field + "=", StringComparison.Ordinal))
            {
                return part[(field.Length + 1)..];
            }
        }

        return string.Empty;
    }

    private static string S4T3ReplyCode(JsonElement reply) =>
        reply.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
            ? error.GetString() ?? string.Empty
            : reply.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                ? status.GetString() ?? string.Empty
                : string.Empty;

    private static string S4T3LiveRevision(params string[] statusOutputs)
    {
        long live = 1;
        foreach (var output in statusOutputs)
        {
            var field = S4T3SnapshotField(output, "sourceReferenceRevision");
            if (long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision) && revision > live)
            {
                live = revision;
            }
        }

        return live.ToString(CultureInfo.InvariantCulture);
    }

    private static string S4T3Digest(byte[] content)
    {
        var hash = SHA256.HashData(content);
        try
        {
            return Convert.ToBase64String(hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static string S4T3EnrolledSession(string sessionId, string marker) =>
        string.Concat(
            S4T3EnrolledLine("2026-10-12T00:00:00.000Z", 0, "session_meta",
                "{\"id\":\"" + sessionId + "\",\"session_id\":\"s4t3-parent-thread\",\"timestamp\":\"2026-10-12T00:00:00.000Z\"," +
                "\"cwd\":\"C:\\\\synthetic\\\\s4t3-fixture\",\"originator\":\"MetaBrain-S4T3-Smoke\",\"cli_version\":\"fixture-1.0\"," +
                "\"source\":\"cli\",\"git\":{}}"),
            S4T3EnrolledLine("2026-10-12T00:00:01.000Z", 1, "event_msg",
                "{\"type\":\"task_started\",\"turn_id\":\"turn-1\",\"started_at\":1,\"model_context_window\":1000,\"collaboration_mode_kind\":\"default\"}"),
            S4T3EnrolledLine("2026-10-12T00:00:02.000Z", 2, "response_item",
                "{\"type\":\"message\",\"id\":\"msg-user-1\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + marker + " user request\"}]}"),
            S4T3EnrolledLine("2026-10-12T00:00:03.000Z", 3, "response_item",
                "{\"type\":\"message\",\"id\":\"msg-assistant-1\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"" + marker + " final answer\"}],\"phase\":\"final_answer\"}"),
            S4T3EnrolledLine("2026-10-12T00:00:04.000Z", 4, "response_item",
                "{\"type\":\"function_call_output\",\"id\":\"fco-1\",\"call_id\":\"call-1\",\"name\":\"shell\",\"output\":\"" + marker + " tool output text\"}"),
            S4T3EnrolledLine("2026-10-12T00:00:05.000Z", 5, "token_usage_record",
                "{\"thread_id\":\"s4t3-parent-thread\",\"turn_id\":\"turn-1\",\"session_id\":\"" + sessionId + "\",\"usage\":{\"input_tokens\":1,\"output_tokens\":2,\"total_tokens\":3}}"),
            S4T3EnrolledLine("2026-10-12T00:00:06.000Z", 6, "event_msg",
                "{\"type\":\"task_complete\",\"turn_id\":\"turn-1\",\"last_agent_message\":\"" + marker + " final answer\",\"started_at\":1,\"completed_at\":2,\"duration_ms\":1000,\"time_to_first_token_ms\":10}"));

    private static string S4T3EnrolledAppend(string sessionId, string marker) =>
        S4T3EnrolledLine("2026-10-12T00:00:07.000Z", 7, "response_item",
            "{\"type\":\"message\",\"id\":\"msg-user-2\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + marker + " follow-up\"}]}");

    private static string S4T3EnrolledLine(string timestamp, int ordinal, string type, string payload) =>
        "{\"timestamp\":\"" + timestamp + "\",\"ordinal\":" + ordinal.ToString(CultureInfo.InvariantCulture) +
        ",\"type\":\"" + type + "\",\"payload\":" + payload + "}\n";
}
