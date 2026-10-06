using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string CatalogA = "owner-approved-catalog-a";
    private const string CatalogB = "owner-approved-catalog-b";
    private const string CatalogLabelA = "Approved synthetic lesson A";
    private const string CatalogDescriptionA = "Owner-written short summary A; never derived from private text.";
    private const string CatalogLabelB = "Approved synthetic lesson B";
    private const string CatalogDescriptionB = "Owner-written short summary B; never derived from private text.";
    private const string PrivateTitleMarker = "synthetic-sensitive-private-title-9f27";
    private const string PrivateBodyMarker = "synthetic-sensitive-private-body-4c1d";
    private const string PrivatePathMarker = "synthetic-sensitive-source-path-88e0";

    internal static async Task RunT7CatalogAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S1-T7 catalog smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S1-T7 catalog publication and discovery");
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
                "The real owner CLI did not initialize the synthetic catalog fixture.");

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var unlocked = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The owner could not unlock the isolated catalog fixture.");

            var markerResource = "fixture-sensitive-catalog-source";
            var markerBytes = Encoding.UTF8.GetBytes(
                $"private title marker {PrivateTitleMarker}; body marker {PrivateBodyMarker}; path marker {PrivatePathMarker}; never publish implicitly.");
            var markerInput = Path.Combine(fixture.OutputDirectory, "sensitive-marker-input.bin");
            await File.WriteAllBytesAsync(markerInput, markerBytes).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(markerBytes);
            var markerWrite = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "write", "--control-pipe", fixture.ControlPipe,
                "--resource-id", markerResource, "--zone-id", SmokeFixture.Zone,
                "--input-file", markerInput).ConfigureAwait(false);
            Require(markerWrite.ExitCode == 0 && markerWrite.StandardOutput.Contains("WRITE stored revision=1", StringComparison.Ordinal),
                "The owner could not store the synthetic sensitive private marker resource.");
            File.Delete(markerInput);

            bool DiscoveryShows(JsonElement reply, string catalogId) =>
                reply.TryGetProperty("publishedCatalog", out var entries) &&
                entries.ValueKind == JsonValueKind.Array &&
                entries.EnumerateArray().Any(entry =>
                    entry.TryGetProperty("catalogId", out var id) &&
                    string.Equals(id.GetString(), catalogId, StringComparison.Ordinal));

            void RequireDiscoveryMetadataOnly(JsonElement reply)
            {
                var text = JsonSerializer.Serialize(reply);
                Require(!text.Contains(markerResource, StringComparison.Ordinal) &&
                        !text.Contains(SmokeFixture.Zone, StringComparison.Ordinal) &&
                        !text.Contains(PrivateTitleMarker, StringComparison.Ordinal) &&
                        !text.Contains(PrivateBodyMarker, StringComparison.Ordinal) &&
                        !text.Contains(PrivatePathMarker, StringComparison.Ordinal) &&
                        !text.Contains(SmokeFixture.ResourceA, StringComparison.Ordinal) &&
                        !text.Contains(SmokeFixture.ResourceB, StringComparison.Ordinal),
                    "Discovery exposed a private resource mapping or synthetic sensitive marker.");
                if (reply.TryGetProperty("publishedCatalog", out var entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in entries.EnumerateArray())
                    {
                        var properties = entry.EnumerateObject().Select(property => property.Name).ToArray();
                        Require(properties.Length == 3 &&
                                properties.Contains("catalogId", StringComparer.Ordinal) &&
                                properties.Contains("label", StringComparer.Ordinal) &&
                                properties.Contains("description", StringComparer.Ordinal),
                            "A published catalog entry carried a field outside the approved ID/label/description triplet.");
                    }
                }
            }

            async Task<JsonElement> DiscoveryListAsync(string pipe) =>
                await InvokeAgentRequestAsync(pipe, new
                {
                    protocolVersion = 1,
                    operation = "catalog.list",
                }).ConfigureAwait(false);

            async Task<JsonElement> DiscoveryQueryAsync(string pipe, string query) =>
                await InvokeAgentRequestAsync(pipe, new
                {
                    protocolVersion = 1,
                    operation = "catalog.query",
                    query,
                }).ConfigureAwait(false);

            // Before publication: two independent no-session discovery clients observe no private existence.
            var emptyListA = await DiscoveryListAsync(fixture.AgentPipe).ConfigureAwait(false);
            var emptyListB = await DiscoveryListAsync(fixture.AgentPipe).ConfigureAwait(false);
            Require(ReplyCode(emptyListA) == "catalog" && ReplyCode(emptyListB) == "catalog",
                "Empty catalog discovery did not return the approved public envelope.");
            RequireDiscoveryMetadataOnly(emptyListA);
            RequireDiscoveryMetadataOnly(emptyListB);
            Require(JsonSerializer.Serialize(emptyListA) == JsonSerializer.Serialize(emptyListB),
                "Two independent discovery clients observed different pre-publication catalogs.");
            var emptyQuery = await DiscoveryQueryAsync(fixture.AgentPipe, "Approved synthetic lesson").ConfigureAwait(false);
            Require(ReplyCode(emptyQuery) == "no_match", "Pre-publication catalog query did not return generic no-match.");
            RequireDiscoveryMetadataOnly(emptyQuery);

            // Sensitive private markers exist but are unreachable through discovery.
            var markerQuery = await DiscoveryQueryAsync(fixture.AgentPipe, PrivateBodyMarker).ConfigureAwait(false);
            Require(ReplyCode(markerQuery) == "no_match", "A private body marker query matched public discovery.");
            var resourceQuery = await DiscoveryQueryAsync(fixture.AgentPipe, markerResource).ConfigureAwait(false);
            Require(ReplyCode(resourceQuery) == "no_match", "A private resource ID query matched public discovery.");

            // Owner preview shows exact text without publishing or minting access.
            var preview = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "catalog-preview", "--control-pipe", fixture.ControlPipe,
                "--catalog-id", CatalogA, "--label", CatalogLabelA, "--description", CatalogDescriptionA,
                "--resource-id", SmokeFixture.ResourceA).ConfigureAwait(false);
            Require(preview.ExitCode == 0 && preview.StandardOutput.Contains($"CATALOG PREVIEW id={CatalogA}", StringComparison.Ordinal),
                "The owner catalog preview did not show the exact pending publication.");
            var stillEmpty = await DiscoveryListAsync(fixture.AgentPipe).ConfigureAwait(false);
            Require(ReplyCode(stillEmpty) == "catalog", "Preview changed the public discovery projection.");

            // Owner spoof through discovery extras is denied with generic no-match.
            var spoofedList = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "catalog.list",
                catalogId = "owner-spoof-field",
            }).ConfigureAwait(false);
            Require(ReplyCode(spoofedList) == "no_match", "Owner/identity body fields altered catalog discovery.");
            var spoofedQuery = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "catalog.query",
                query = "Approved",
                sessionToken = "mbs1_spoofed-bearer-value-for-tests-only-0000",
            }).ConfigureAwait(false);
            Require(ReplyCode(spoofedQuery) == "no_match", "Bearer/identity payload altered catalog discovery.");

            // Unauthorized publication through the agent pipe is denied at the channel boundary.
            var agentPublish = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "owner.catalog.publish",
                catalogId = "agent-spoofed-entry",
                label = CatalogLabelA,
                description = CatalogDescriptionA,
                resourceId = SmokeFixture.ResourceA,
            }).ConfigureAwait(false);
            Require(ReplyCode(agentPublish) == "forbidden", "The agent channel accepted an owner publication operation.");

            // Publication requires explicit owner confirmation: cancel leaves discovery empty.
            var cancelled = await RunOwnerCommandWithInputAsync(connectionsExecutable, "NO\n",
                "owner", "catalog-publish", "--control-pipe", fixture.ControlPipe,
                "--catalog-id", CatalogA, "--label", CatalogLabelA, "--description", CatalogDescriptionA,
                "--resource-id", SmokeFixture.ResourceA).ConfigureAwait(false);
            Require(cancelled.ExitCode == 3 && cancelled.StandardOutput.Contains("CANCELLED", StringComparison.Ordinal),
                "A declined owner publication did not cancel cleanly.");
            var emptyAfterCancel = await DiscoveryListAsync(fixture.AgentPipe).ConfigureAwait(false);
            Require(ReplyCode(emptyAfterCancel) == "catalog", "A cancelled publication changed public discovery.");

            // Owner publishes A; both discovery clients observe exactly the approved triplet.
            var publishA = await RunOwnerCommandWithInputAsync(connectionsExecutable, "PUBLISH\n",
                "owner", "catalog-publish", "--control-pipe", fixture.ControlPipe,
                "--catalog-id", CatalogA, "--label", CatalogLabelA, "--description", CatalogDescriptionA,
                "--resource-id", SmokeFixture.ResourceA).ConfigureAwait(false);
            Require(publishA.ExitCode == 0 && publishA.StandardOutput.Contains($"CATALOG published id={CatalogA}", StringComparison.Ordinal),
                "The owner could not publish the first approved catalog entry: " + publishA.StandardError.Trim());
            var listedAA = await DiscoveryListAsync(fixture.AgentPipe).ConfigureAwait(false);
            var listedAB = await DiscoveryListAsync(fixture.AgentPipe).ConfigureAwait(false);
            Require(ReplyCode(listedAA) == "catalog" && ReplyCode(listedAB) == "catalog",
                "Published catalog discovery did not return the approved envelope.");
            RequireDiscoveryMetadataOnly(listedAA);
            Require(DiscoveryShows(listedAA, CatalogA) && DiscoveryShows(listedAB, CatalogA),
                "Two independent discovery clients did not both observe the published entry.");
            Require(JsonSerializer.Serialize(listedAA) == JsonSerializer.Serialize(listedAB),
                "Two independent discovery clients observed different published catalogs.");
            var queryA = await DiscoveryQueryAsync(fixture.AgentPipe, "lesson A").ConfigureAwait(false);
            Require(ReplyCode(queryA) == "catalog" && DiscoveryShows(queryA, CatalogA),
                "An approved-metadata query did not return the published entry.");
            RequireDiscoveryMetadataOnly(queryA);
            var unrelatedQuery = await DiscoveryQueryAsync(fixture.AgentPipe, "unrelated synthetic phrase").ConfigureAwait(false);
            Require(ReplyCode(unrelatedQuery) == "no_match", "An unrelated query did not return generic no-match.");
            RequireDiscoveryMetadataOnly(unrelatedQuery);

            // Guessed private IDs never match discovery, and catalog grants no body/source access.
            var guessedPrivate = await DiscoveryQueryAsync(fixture.AgentPipe, SmokeFixture.ResourceA).ConfigureAwait(false);
            Require(ReplyCode(guessedPrivate) == "no_match", "A guessed private resource ID matched public discovery.");
            var bodyProbe = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.resource.read",
                accessOperation = "resource.read",
                resourceId = CatalogA,
                resourceRevision = 1,
            }).ConfigureAwait(false);
            Require(ReplyCode(bodyProbe) is "denied" or "session_unknown" or "authorization_unavailable",
                "A catalog ID unexpectedly resolved on the private read path.");
            var noSessionRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-read", "--agent-pipe", fixture.AgentPipe,
                "--session-file", Path.Combine(fixture.OutputDirectory, "missing-session.bin"),
                "--operation", "resource.read", "--resource-id", SmokeFixture.ResourceA,
                "--resource-revision", "1",
                "--output-file", Path.Combine(fixture.OutputDirectory, "catalog-no-session.bin")).ConfigureAwait(false);
            Require(noSessionRead.ExitCode == 3, "A body read without a session unexpectedly succeeded.");

            // Owner publishes B; discovery serves both approved triplets.
            var publishB = await RunOwnerCommandWithInputAsync(connectionsExecutable, "PUBLISH\n",
                "owner", "catalog-publish", "--control-pipe", fixture.ControlPipe,
                "--catalog-id", CatalogB, "--label", CatalogLabelB, "--description", CatalogDescriptionB,
                "--resource-id", markerResource).ConfigureAwait(false);
            Require(publishB.ExitCode == 0 && publishB.StandardOutput.Contains($"CATALOG published id={CatalogB}", StringComparison.Ordinal),
                "The owner could not publish the second approved catalog entry: " + publishB.StandardError.Trim());
            var listedBoth = await DiscoveryListAsync(fixture.AgentPipe).ConfigureAwait(false);
            Require(ReplyCode(listedBoth) == "catalog" && DiscoveryShows(listedBoth, CatalogA) && DiscoveryShows(listedBoth, CatalogB),
                "Discovery did not serve both approved entries.");
            RequireDiscoveryMetadataOnly(listedBoth);

            // Owner private listing exposes the encrypted mapping (owner channel only); discovery stays public-only.
            var ownerList = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "catalog-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ownerList.ExitCode == 0 && ownerList.StandardOutput.Contains($"CATALOG id={CatalogA}", StringComparison.Ordinal) &&
                    ownerList.StandardOutput.Contains($"CATALOG id={CatalogB}", StringComparison.Ordinal) &&
                    ownerList.StandardOutput.Contains("CATALOG count=2", StringComparison.Ordinal),
                "The owner catalog listing did not show both private mappings.");

            VerifyCatalogPersistence(fixture, markerResource);

            // Malformed private mapping and projection fail closed, then restore cleanly.
            await VerifyCatalogRecoveryAsync(connectionsExecutable, fixture).ConfigureAwait(false);

            // Locked discovery still serves the approved projection with no key/decrypt.
            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The owner could not lock before locked-discovery checks.");
            var lockedList = await DiscoveryListAsync(fixture.AgentPipe).ConfigureAwait(false);
            Require(ReplyCode(lockedList) == "catalog" && DiscoveryShows(lockedList, CatalogA) && DiscoveryShows(lockedList, CatalogB),
                "Locked discovery did not serve the approved projection.");
            RequireDiscoveryMetadataOnly(lockedList);
            var lockedQuery = await DiscoveryQueryAsync(fixture.AgentPipe, "lesson B").ConfigureAwait(false);
            Require(ReplyCode(lockedQuery) == "catalog" && DiscoveryShows(lockedQuery, CatalogB),
                "Locked discovery query did not serve approved metadata.");
            var lockedPublish = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "catalog-publish", "--control-pipe", fixture.ControlPipe,
                "--catalog-id", "locked-entry", "--label", CatalogLabelA, "--description", CatalogDescriptionA,
                "--resource-id", SmokeFixture.ResourceA).ConfigureAwait(false);
            Require(lockedPublish.ExitCode != 0, "Owner publication was served while the vault was locked.");

            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false))
                    .Contains("vault=locked", StringComparison.Ordinal),
                "The service did not restart locked.");

            // Startup discovery (locked, no key) serves the approved projection before any unlock.
            var startupList = await DiscoveryListAsync(fixture.AgentPipe).ConfigureAwait(false);
            Require(ReplyCode(startupList) == "catalog" && DiscoveryShows(startupList, CatalogA) && DiscoveryShows(startupList, CatalogB),
                "Restarted locked discovery did not serve the approved projection.");
            RequireDiscoveryMetadataOnly(startupList);
            var startupQuery = await DiscoveryQueryAsync(fixture.AgentPipe, "lesson A").ConfigureAwait(false);
            Require(ReplyCode(startupQuery) == "catalog" && DiscoveryShows(startupQuery, CatalogA),
                "Restarted locked discovery query did not serve approved metadata.");

            var unlockAgain = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlockAgain.ExitCode == 0, "The owner could not unlock after restart.");

            // A fresh discovery connection observes the projection; then withdrawal converges on the next serve boundary.
            var warmBeforeReply = await DiscoveryListAsync(fixture.AgentPipe).ConfigureAwait(false);
            Require(ReplyCode(warmBeforeReply) == "catalog" && DiscoveryShows(warmBeforeReply, CatalogB),
                "The established discovery path did not observe the published entry before withdrawal.");

            var withdrawB = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "catalog-withdraw", "--control-pipe", fixture.ControlPipe,
                "--catalog-id", CatalogB).ConfigureAwait(false);
            Require(withdrawB.ExitCode == 0 && withdrawB.StandardOutput.Contains($"CATALOG withdrawn id={CatalogB}", StringComparison.Ordinal),
                "The owner could not withdraw the second catalog entry.");

            // Serve-boundary proof: overlapping a repeated withdrawal with fresh discovery still converges to no-match.
            var withdrawRaceB = RunOwnerCommandAsync(connectionsExecutable,
                "owner", "catalog-withdraw", "--control-pipe", fixture.ControlPipe,
                "--catalog-id", CatalogB);
            var freshRaceReply = await DiscoveryListAsync(fixture.AgentPipe).ConfigureAwait(false);
            var withdrawRaceResult = await withdrawRaceB.ConfigureAwait(false);
            Require(withdrawRaceResult.ExitCode == 3 && withdrawRaceResult.StandardError.Contains("catalog_unknown", StringComparison.Ordinal),
                "A repeated withdrawal did not fail closed as already withdrawn.");
            Require(ReplyCode(freshRaceReply) == "catalog" && DiscoveryShows(freshRaceReply, CatalogA) &&
                !DiscoveryShows(freshRaceReply, CatalogB),
                "A fresh discovery connection kept serving withdrawn metadata.");
            var queryWithdrawn = await DiscoveryQueryAsync(fixture.AgentPipe, "lesson B").ConfigureAwait(false);
            Require(ReplyCode(queryWithdrawn) == "no_match", "A withdrawn entry still matched discovery queries.");
            var queryLive = await DiscoveryQueryAsync(fixture.AgentPipe, "lesson A").ConfigureAwait(false);
            Require(ReplyCode(queryLive) == "catalog" && DiscoveryShows(queryLive, CatalogA),
                "The surviving approved entry stopped matching after a sibling withdrawal.");
            // Withdrawn resource mapping must not resurrect across restart.
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var restartLocked = await DiscoveryListAsync(fixture.AgentPipe).ConfigureAwait(false);
            Require(ReplyCode(restartLocked) == "catalog" && DiscoveryShows(restartLocked, CatalogA) &&
                    !DiscoveryShows(restartLocked, CatalogB),
                "Withdrawn catalog metadata resurrected on the restarted locked service.");
            var restartUnlock = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(restartUnlock.ExitCode == 0, "The owner could not unlock after the withdrawal restart.");
            var ownerAfterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "catalog-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(ownerAfterRestart.ExitCode == 0 && ownerAfterRestart.StandardOutput.Contains($"CATALOG id={CatalogA}", StringComparison.Ordinal) &&
                    !ownerAfterRestart.StandardOutput.Contains($"CATALOG id={CatalogB}", StringComparison.Ordinal) &&
                    ownerAfterRestart.StandardOutput.Contains("CATALOG count=1", StringComparison.Ordinal),
                "The withdrawn catalog entry resurrected in the owner mapping after restart.");

            // Concurrency/backpressure: overlap fresh discovery connections after withdrawal; every boundary converges.
            var parallelReads = await Task.WhenAll(
                DiscoveryListAsync(fixture.AgentPipe),
                DiscoveryQueryAsync(fixture.AgentPipe, "lesson A"),
                DiscoveryListAsync(fixture.AgentPipe)).ConfigureAwait(false);
            Require(parallelReads.All(reply => ReplyCode(reply) == "catalog" && DiscoveryShows(reply, CatalogA)),
                "Concurrent discovery connections diverged after withdrawal.");
            var survivorFinal = await DiscoveryListAsync(fixture.AgentPipe).ConfigureAwait(false);
            Require(ReplyCode(survivorFinal) == "catalog" && DiscoveryShows(survivorFinal, CatalogA),
                "The surviving entry stopped being served after concurrent reads.");

            // Unknown/guessed catalog IDs share one generic owner code; discovery stays no-match.
            var unknownWithdraw = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "catalog-withdraw", "--control-pipe", fixture.ControlPipe,
                "--catalog-id", "fixture-not-present").ConfigureAwait(false);
            Require(unknownWithdraw.ExitCode == 3 && unknownWithdraw.StandardError.Contains("catalog_unknown", StringComparison.Ordinal),
                "Withdrawing an unknown catalog ID did not fail closed.");
            var unknownQuery = await DiscoveryQueryAsync(fixture.AgentPipe, "fixture-not-present").ConfigureAwait(false);
            Require(ReplyCode(unknownQuery) == "no_match", "An unknown catalog query did not return generic no-match.");

            Console.WriteLine("PASS S1-T7 unpublished private existence never leaks through discovery");
            Console.WriteLine("PASS S1-T7 approved metadata only after publish; body/source still denied without a session");
            Console.WriteLine("PASS S1-T7 locked/startup/restart discovery serves approved projection with no key or decrypt");
            Console.WriteLine("PASS S1-T7 withdrawal converges on list/query/fresh/restart with no resurrection");
            Console.WriteLine("PASS S1-T7 catalog publication and discovery over the authenticated named pipes");
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

    private static void VerifyCatalogPersistence(SmokeFixture fixture, string markerResource)
    {
        var projectionPath = Path.Combine(Path.GetDirectoryName(fixture.VaultDirectoryPath)!, "published-catalog.json");
        Require(File.Exists(projectionPath), "The public catalog projection file was not persisted.");
        var projectionText = File.ReadAllText(projectionPath);
        Require(projectionText.Contains(CatalogA, StringComparison.Ordinal) &&
                projectionText.Contains(CatalogLabelA, StringComparison.Ordinal) &&
                projectionText.Contains(CatalogDescriptionA, StringComparison.Ordinal) &&
                projectionText.Contains(CatalogB, StringComparison.Ordinal) &&
                projectionText.Contains(CatalogLabelB, StringComparison.Ordinal) &&
                projectionText.Contains(CatalogDescriptionB, StringComparison.Ordinal),
            "The public projection omitted exact owner-approved metadata.");
        Require(!projectionText.Contains(markerResource, StringComparison.Ordinal) &&
                !projectionText.Contains(SmokeFixture.ResourceA, StringComparison.Ordinal) &&
                !projectionText.Contains(SmokeFixture.ResourceB, StringComparison.Ordinal) &&
                !projectionText.Contains(SmokeFixture.Zone, StringComparison.Ordinal) &&
                !projectionText.Contains(PrivateTitleMarker, StringComparison.Ordinal) &&
                !projectionText.Contains(PrivateBodyMarker, StringComparison.Ordinal) &&
                !projectionText.Contains(PrivatePathMarker, StringComparison.Ordinal),
            "The public projection leaked a private mapping or sensitive marker.");

        var catalogPath = Path.Combine(fixture.VaultDirectoryPath, "owner-catalog.enc");
        Require(File.Exists(catalogPath), "The encrypted private catalog mapping was not persisted.");
        var catalogBytes = File.ReadAllBytes(catalogPath);
        try
        {
            Require(!ContainsBytes(catalogBytes, Encoding.UTF8.GetBytes(markerResource)) &&
                    !ContainsBytes(catalogBytes, Encoding.UTF8.GetBytes(SmokeFixture.ResourceA)) &&
                    !ContainsBytes(catalogBytes, Encoding.UTF8.GetBytes(CatalogLabelA)) &&
                    !ContainsBytes(catalogBytes, Encoding.UTF8.GetBytes(CatalogDescriptionA)) &&
                    !ContainsBytes(catalogBytes, Encoding.UTF8.GetBytes(PrivateTitleMarker)) &&
                    !ContainsBytes(catalogBytes, Encoding.UTF8.GetBytes(PrivateBodyMarker)) &&
                    !ContainsBytes(catalogBytes, Encoding.UTF8.GetBytes(PrivatePathMarker)),
                "The encrypted private catalog mapping exposed plaintext markers.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(catalogBytes);
        }
    }

    private static async Task VerifyCatalogRecoveryAsync(string executable, SmokeFixture fixture)
    {
        var projectionPath = Path.Combine(Path.GetDirectoryName(fixture.VaultDirectoryPath)!, "published-catalog.json");
        var catalogPath = Path.Combine(fixture.VaultDirectoryPath, "owner-catalog.enc");
        var originalProjection = await File.ReadAllBytesAsync(projectionPath).ConfigureAwait(false);
        var originalCatalog = await File.ReadAllBytesAsync(catalogPath).ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(projectionPath, "{invalid json", Encoding.UTF8).ConfigureAwait(false);
            var malformedDiscovery = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "catalog.list",
            }).ConfigureAwait(false);
            Require(ReplyCode(malformedDiscovery) == "no_match",
                "A malformed public projection was served instead of failing closed to no-match.");
            await File.WriteAllBytesAsync(projectionPath, originalProjection).ConfigureAwait(false);
            var restoredDiscovery = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "catalog.list",
            }).ConfigureAwait(false);
            Require(ReplyCode(restoredDiscovery) == "catalog", "Restoring the projection did not restore discovery.");

            var tampered = (byte[])originalCatalog.Clone();
            var text = Encoding.UTF8.GetString(tampered);
            const string marker = "\"ciphertext\":\"";
            var start = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            Require(start >= marker.Length && start < text.Length, "The encrypted catalog envelope had no ciphertext field.");
            var changed = text[start] == 'A' ? 'B' : 'A';
            await File.WriteAllBytesAsync(catalogPath, Encoding.UTF8.GetBytes(text[..start] + changed + text[(start + 1)..])).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(tampered);
            var tamperedList = await RunOwnerCommandAsync(executable,
                "owner", "catalog-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(tamperedList.ExitCode == 3 && tamperedList.StandardError.Contains("catalog_state_unavailable", StringComparison.Ordinal),
                "Tampered encrypted catalog state was served instead of failing closed.");
            Console.WriteLine("PASS S1-T7 malformed projection and tampered private mapping fail closed with clean recovery");
        }
        finally
        {
            await File.WriteAllBytesAsync(projectionPath, originalProjection).ConfigureAwait(false);
            await File.WriteAllBytesAsync(catalogPath, originalCatalog).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(originalProjection);
            CryptographicOperations.ZeroMemory(originalCatalog);
        }

        var recovered = await RunOwnerCommandAsync(executable,
            "owner", "catalog-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(recovered.ExitCode == 0 && recovered.StandardOutput.Contains("CATALOG count=2", StringComparison.Ordinal),
            "Restoring the synthetic catalog ciphertext did not restore owner readability.");
    }
}
