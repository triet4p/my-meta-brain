using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

// S2-T6: organization memberships (collections/zones) and the permission-filtered
// collection view. One synthetic owner vault is provisioned and unlocked through the
// real owner CLI, then one owner and two differently-granted agent sessions browse the
// same collection while a third session is granted the enclosing zone. Every mutation
// goes through the real service+CLI; every denial is the generic agent error family.
internal static partial class OwnerServiceSmoke
{
    // Synthetic identifiers. The private markers must stay out of every persisted
    // vault file (verified at the end of the scenario).
    private const string S2t6Zone = "s2t6-zone-alpha";
    private const string S2t6Shared = "s2t6-shared";
    private const string S2t6AlphaOnly = "s2t6-alpha-only";
    private const string S2t6BetaOnly = "s2t6-beta-only";
    private const string S2t6Hidden = "s2t6-hidden";
    private const string S2t6Descendant = "s2t6-descendant";
    private const string S2t6ZoneChild = "s2t6-zone-child";
    private const string S2t6SourceOne = "s2t6-src-one";
    private const string S2t6Relation = "rel-s2t6-supports";
    private const string S2t6Lifecycle = "lc-s2t6-shared";
    private const string S2t6Archive = "arc-s2t6-shared-r1";
    private const string S2t6CollectionAlpha = "s2t6-collection-alpha";
    private const string S2t6CollectionBeta = "s2t6-collection-beta";
    private const string S2t6CatalogPublic = "s2t6-catalog-public";

    internal static async Task RunS2T6CollectionsAsync(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S2-T6 collections smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S2-T6 collections, permission-filtered views and catalog distinctness");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        Exception? failure = null;
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);

            service = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            var provisioned = await RunOwnerCommandWithInputAsync(executable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "Provision did not create the S2-T6 synthetic vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");

            service = await RunS2T6ScenarioAsync(executable, fixture, passphrase, service).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (service is not null)
            {
                try
                {
                    await StopServiceAsync(service).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = failure is null ? ex : new AggregateException(failure, ex);
                }
            }

            try
            {
                fixture.Dispose();
            }
            catch (Exception ex)
            {
                failure = failure is null ? ex : new AggregateException(failure, ex);
            }
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        Console.WriteLine("LIMIT synthetic S2-T6 fixture only; the owner channel is the only membership/catalog mutation surface here so an agent organization view and MCP collection tools stay in later tasks; the rebuildable private index/ranking/search stays S2-T7 work; same-owner OS compromise remains outside the threat model.");
    }

    private static async Task<RunningService?> RunS2T6ScenarioAsync(string executable, SmokeFixture fixture, string passphrase, RunningService service)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-30);
        var sharedTitleOne = "Synthetic S2-T6 shared title one";
        var sharedBodyOne = "Synthetic S2-T6 shared body one; never a real owner secret.";
        var sharedTitleTwo = "Synthetic S2-T6 shared title two";
        var sharedBodyTwo = "Synthetic S2-T6 shared body two; never a real owner secret.";

        // Phase 1 - owner organization. Two collections share one member, so the
        // same canonical resource appears in two memberships and must still be one
        // grant, one canonical revision, and one listed member per collection.
        await WriteS2T6ItemDraftAsync(executable, fixture, S2t6Shared, S2t6Zone,
            S2T6ItemDraft("claim", sharedTitleOne, sharedBodyOne, now), 1).ConfigureAwait(false);
        await WriteS2T6ItemDraftAsync(executable, fixture, S2t6AlphaOnly, S2t6Zone,
            S2T6ItemDraft("claim", "Synthetic S2-T6 alpha-only title", "Synthetic S2-T6 alpha-only body; never a real owner secret.", now), 1).ConfigureAwait(false);
        await WriteS2T6ItemDraftAsync(executable, fixture, S2t6BetaOnly, S2t6Zone,
            S2T6ItemDraft("claim", "Synthetic S2-T6 beta-only title", "Synthetic S2-T6 beta-only body; never a real owner secret.", now), 1).ConfigureAwait(false);
        await WriteS2T6ItemDraftAsync(executable, fixture, S2t6Hidden, S2t6Zone,
            S2T6ItemDraft("claim", "Synthetic S2-T6 hidden title", "Synthetic S2-T6 hidden body; never a real owner secret.", now), 1).ConfigureAwait(false);
        await WriteS2T6SourceDraftAsync(executable, fixture, S2t6SourceOne, S2t6Zone,
            S2T6SourceDraft("Synthetic S2-T6 private source title", "s2t6-private-locator", "s2t6-source-v1", now), 1).ConfigureAwait(false);
        // One accepted relation: the item supports the registered source. A grant
        // over the collection must not read either endpoint through the relation.
        await CreateS2T6RelationAsync(executable, fixture, S2t6Relation, S2t6Zone,
            S2T4RelationDraft("supports", "accepted", S2t6Shared, "memoryItem", 1L, null, S2t6SourceOne, "registeredSource", 1L, new { start = 0L, end = 512L },
                "Synthetic S2-T6 support reason; never a real owner secret.", now)).ConfigureAwait(false);

        await SetS2T6CollectionAsync(executable, fixture, S2t6CollectionAlpha, S2t6Shared, S2t6AlphaOnly, S2t6Hidden).ConfigureAwait(false);
        await SetS2T6CollectionAsync(executable, fixture, S2t6CollectionBeta, S2t6Shared, S2t6BetaOnly).ConfigureAwait(false);
        var collections = await ListS2T6CollectionsAsync(executable, fixture).ConfigureAwait(false);
        Require(collections.StandardOutput.Contains($"COLLECTION id={S2t6CollectionAlpha} resourceIds={S2t6AlphaOnly},{S2t6Hidden},{S2t6Shared}", StringComparison.Ordinal) &&
                collections.StandardOutput.Contains($"COLLECTION id={S2t6CollectionBeta} resourceIds={S2t6BetaOnly},{S2t6Shared}", StringComparison.Ordinal) &&
                collections.StandardOutput.Contains("COLLECTIONS count=2", StringComparison.Ordinal),
            "The owner collection listing did not show each synthetic membership exactly once (stdout=" + collections.StandardOutput.Trim() + ").");
        var collectionMembers = collections.StandardOutput.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("COLLECTION id=", StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf(" resourceIds=", StringComparison.Ordinal) + " resourceIds=".Length)..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();
        Require(collectionMembers.Length == 2,
            "The owner collection listing did not return both collections (stdout=" + collections.StandardOutput.Trim() + ").");
        foreach (var members in collectionMembers)
        {
            Require(members.Length > 0 && members.Distinct(StringComparer.Ordinal).Count() == members.Length,
                "A collection listed the same member more than once.");
        }

        Require(collectionMembers.Count(members => members.Contains(S2t6Shared, StringComparer.Ordinal)) == 2,
            "The shared canonical member was not present in both memberships exactly once each (stdout=" + collections.StandardOutput.Trim() + ").");
        Console.WriteLine("PASS two collections share one canonical member and list each membership exactly once");

        // Phase 2 - catalog distinctness. The published catalog entry is owner text
        // only; the private mapping (which collection/zone/resource it points at)
        // stays owner-channel-only and encrypted.
        var catalogPreview = await RunOwnerCommandAsync(executable,
            "owner", "catalog-preview", "--control-pipe", fixture.ControlPipe,
            "--catalog-id", S2t6CatalogPublic, "--label", "Approved synthetic S2-T6 lesson",
            "--description", "Owner-written short S2-T6 summary; never derived from private text.",
            "--resource-id", S2t6Shared).ConfigureAwait(false);
        Require(catalogPreview.ExitCode == 0 && catalogPreview.StandardOutput.Contains($"CATALOG PREVIEW id={S2t6CatalogPublic}", StringComparison.Ordinal) &&
                catalogPreview.StandardOutput.Contains($"maps resource={S2t6Shared}", StringComparison.Ordinal),
            "The owner could not preview the S2-T6 catalog publication (exit=" + catalogPreview.ExitCode + "; stdout=" + catalogPreview.StandardOutput.Trim() + ").");
        var catalogPublish = await RunOwnerCommandWithInputAsync(executable, "PUBLISH" + Environment.NewLine,
            "owner", "catalog-publish", "--control-pipe", fixture.ControlPipe,
            "--catalog-id", S2t6CatalogPublic, "--label", "Approved synthetic S2-T6 lesson",
            "--description", "Owner-written short S2-T6 summary; never derived from private text.",
            "--resource-id", S2t6Shared).ConfigureAwait(false);
        Require(catalogPublish.ExitCode == 0 && catalogPublish.StandardOutput.Contains($"CATALOG published id={S2t6CatalogPublic}", StringComparison.Ordinal),
            "The owner could not publish the S2-T6 catalog entry (exit=" + catalogPublish.ExitCode + "; stderr=" + catalogPublish.StandardError.Trim() + ").");
        var publicList = await InvokeAgentRequestAsync(fixture.AgentPipe, new
        {
            protocolVersion = 1,
            operation = "catalog.list"
        }).ConfigureAwait(false);
        Require(publicList.TryGetProperty("publishedCatalog", out var published) &&
                published.ValueKind == JsonValueKind.Array && published.GetArrayLength() == 1 && ReplyCode(publicList) == "catalog",
            "Agent discovery did not see exactly the one published S2-T6 catalog entry (reply=" + publicList.GetRawText() + ").");
        var entryProperties = published[0].EnumerateObject().Select(property => property.Name).ToArray();
        Require(entryProperties.Length == 3 &&
                entryProperties.Contains("catalogId", StringComparer.Ordinal) &&
                entryProperties.Contains("label", StringComparer.Ordinal) &&
                entryProperties.Contains("description", StringComparer.Ordinal),
            "A published catalog entry carried a field outside the approved ID/label/description triplet.");
        var publicText = publicList.GetRawText();
        foreach (var forbidden in new[] { S2t6Shared, S2t6CollectionAlpha, S2t6CollectionBeta, S2t6Zone, sharedTitleOne, "s2t6-private-locator" })
        {
            Require(!publicText.Contains(forbidden, StringComparison.Ordinal),
                "Agent catalog discovery disclosed a private S2-T6 mapping: " + forbidden);
        }

        var ownerCatalog = await RunOwnerCommandAsync(executable,
            "owner", "catalog-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(ownerCatalog.ExitCode == 0 && ownerCatalog.StandardOutput.Contains($"CATALOG id={S2t6CatalogPublic}", StringComparison.Ordinal) &&
                ownerCatalog.StandardOutput.Contains($"resource={S2t6Shared}", StringComparison.Ordinal) &&
                ownerCatalog.StandardOutput.Contains("CATALOG count=1", StringComparison.Ordinal),
            "The owner catalog listing did not show the private S2-T6 mapping (exit=" + ownerCatalog.ExitCode + "; stdout=" + ownerCatalog.StandardOutput.Trim() + ").");
        var catalogWithdraw = await RunOwnerCommandAsync(executable,
            "owner", "catalog-withdraw", "--control-pipe", fixture.ControlPipe,
            "--catalog-id", S2t6CatalogPublic).ConfigureAwait(false);
        Require(catalogWithdraw.ExitCode == 0 && catalogWithdraw.StandardOutput.Contains($"CATALOG withdrawn id={S2t6CatalogPublic}", StringComparison.Ordinal),
            "The owner could not withdraw the S2-T6 catalog entry (exit=" + catalogWithdraw.ExitCode + "; stderr=" + catalogWithdraw.StandardError.Trim() + ").");
        var withdrawnList = await InvokeAgentRequestAsync(fixture.AgentPipe, new
        {
            protocolVersion = 1,
            operation = "catalog.list"
        }).ConfigureAwait(false);
        Require(withdrawnList.TryGetProperty("status", out var withdrawnStatus) && withdrawnStatus.GetString() == "catalog" &&
                withdrawnList.TryGetProperty("publishedCatalog", out var withdrawnEntries) &&
                withdrawnEntries.ValueKind == JsonValueKind.Array && withdrawnEntries.GetArrayLength() == 0,
            "Withdrawing the S2-T6 catalog entry did not empty agent discovery (reply=" + withdrawnList.GetRawText() + ").");
        var ownerAfterWithdraw = await RunOwnerCommandAsync(executable,
            "owner", "catalog-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(ownerAfterWithdraw.ExitCode == 0 && ownerAfterWithdraw.StandardOutput.Contains("CATALOG count=0", StringComparison.Ordinal),
            "Withdrawal did not remove the private S2-T6 mapping (stdout=" + ownerAfterWithdraw.StandardOutput.Trim() + ").");
        Console.WriteLine("PASS the owner-published catalog triplet stays distinct from the private owner mapping and converges to no_match on withdrawal");

        // Phase 3 - owner and two agents browse the same collection. Agent one holds
        // the whole collection; agent two holds a narrowed explicit subset of the same
        // collection. Both are frozen snapshots, so each view is exact and no member,
        // title, zone, kind or count the session was not approved for can appear.
        var agentOne = await IssueS2T6GrantAsync(executable, fixture, "s2t6-agent-one",
            "memory.read,source.read,relation.read,resource.read", collectionId: S2t6CollectionAlpha).ConfigureAwait(false);
        var agentTwo = await IssueS2T6GrantAsync(executable, fixture, "s2t6-agent-two",
            "memory.read,source.read,relation.read,resource.read", resourceIds: new[] { S2t6Shared, S2t6AlphaOnly }).ConfigureAwait(false);

        var oneView = await AgentS2T6CollectionViewAsync(executable, fixture, agentOne).ConfigureAwait(false);
        Require(oneView.ExitCode == 0, "Agent one could not browse the shared collection (exit=" + oneView.ExitCode + "; stderr=" + oneView.StandardError.Trim() + ").");
        RequireS2T6ViewStdout(oneView.StandardOutput, new[]
        {
            ("s2t6-alpha-only", 1L),
            ("s2t6-hidden", 1L),
            ("s2t6-shared", 1L)
        }, "Agent one's permission-filtered view of the shared collection");

        var twoView = await AgentS2T6CollectionViewAsync(executable, fixture, agentTwo).ConfigureAwait(false);
        Require(twoView.ExitCode == 0, "Agent two could not browse its narrowed subset of the shared collection (exit=" + twoView.ExitCode + "; stderr=" + twoView.StandardError.Trim() + ").");
        RequireS2T6ViewStdout(twoView.StandardOutput, new[]
        {
            ("s2t6-alpha-only", 1L),
            ("s2t6-shared", 1L)
        }, "Agent two's permission-filtered subset view of the same collection");
        Console.WriteLine("PASS two agents browse the same collection with different grants and each view carries only its approved IDs and revisions");

        // Phase 4 - no automatic authority. Membership in a collection grants no
        // read of an unapproved member, no read of a relation/lifecycle/archive that
        // merely points at an approved member, and no view of hidden metadata.
        var oneWire = await InvokeAgentRequestAsync(fixture.AgentPipe, new
        {
            protocolVersion = 1,
            operation = "agent.collection.view",
            sessionToken = ReadSessionBearer(agentOne.SessionFile)
        }).ConfigureAwait(false);
        var oneWireText = oneWire.GetRawText();
        Require(ReplyCode(oneWire) == "collection_view" && oneWireText.Contains("\"collectionView\"", StringComparison.Ordinal),
            "Agent one's wire collection view did not return the approved view envelope (reply=" + oneWireText + ").");

        // The view envelope carries exactly session, grant, operations and the
        // approved ID/revision pairs: no count, total, paging, title, zone, kind,
        // collection name or member outside the granted snapshot.
        var viewProperties = oneWire.GetProperty("collectionView").EnumerateObject().Select(property => property.Name).ToArray();
        Require(viewProperties.Length == 4 &&
                viewProperties.Contains("sessionId", StringComparer.Ordinal) &&
                viewProperties.Contains("grantId", StringComparer.Ordinal) &&
                viewProperties.Contains("operations", StringComparer.Ordinal) &&
                viewProperties.Contains("resources", StringComparer.Ordinal),
            "The wire collection view carried a field outside the approved view contract: " + string.Join(",", viewProperties));
        foreach (var resource in oneWire.GetProperty("collectionView").GetProperty("resources").EnumerateArray())
        {
            var resourceProperties = resource.EnumerateObject().Select(property => property.Name).ToArray();
            Require(resourceProperties.Length == 2 &&
                    resourceProperties.Contains("resourceId", StringComparer.Ordinal) &&
                    resourceProperties.Contains("revision", StringComparer.Ordinal),
                "An approved scope point carried a field outside the approved ID/revision pair: " + string.Join(",", resourceProperties));
        }

        foreach (var forbidden in new[] { S2t6CollectionAlpha, S2t6CollectionBeta, S2t6BetaOnly, S2t6Zone, S2t6CatalogPublic, sharedTitleOne, "s2t6-private-locator" })
        {
            Require(!oneWireText.Contains(forbidden, StringComparison.Ordinal),
                "The wire collection view disclosed a private value outside the approved scope: " + forbidden);
        }
        // Source and relation semantics grant no authority: the relation points at an
        // approved member and a registered source, but neither the relation nor the
        // source is in the granted snapshot.
        var agentRelationRead = await InvokeAgentRequestAsync(fixture.AgentPipe, new
        {
            protocolVersion = 1,
            operation = "agent.relation.read",
            sessionToken = ReadSessionBearer(agentOne.SessionFile),
            accessOperation = "relation.read",
            resourceId = S2t6Relation,
            resourceRevision = 1L
        }).ConfigureAwait(false);
        RequireS2T6Denied(agentRelationRead, "resource_unavailable",
            "A relation over an approved member opened a read the collection grant never approved");
        var agentSourceRead = await InvokeAgentRequestAsync(fixture.AgentPipe, new
        {
            protocolVersion = 1,
            operation = "agent.source.read",
            sessionToken = ReadSessionBearer(agentOne.SessionFile),
            accessOperation = "source.read",
            resourceId = S2t6SourceOne,
            resourceRevision = 1L
        }).ConfigureAwait(false);
        RequireS2T6Denied(agentSourceRead, "resource_unavailable",
            "A registered source outside the granted snapshot was served");
        var lifecycleOnAgentChannel = await InvokeAgentRequestAsync(fixture.AgentPipe, new
        {
            protocolVersion = 1,
            operation = "lifecycle.read",
            sessionToken = ReadSessionBearer(agentOne.SessionFile),
            resourceId = S2t6Shared,
            resourceRevision = 1L
        }).ConfigureAwait(false);
        Require(ReplyCode(lifecycleOnAgentChannel) == "forbidden",
            "The lifecycle operation was reachable on the agent channel (reply=" + lifecycleOnAgentChannel.GetRawText() + ").");
        var lifecycleResourceRead = await InvokeAgentRequestAsync(fixture.AgentPipe, new
        {
            protocolVersion = 1,
            operation = "agent.resource.read",
            sessionToken = ReadSessionBearer(agentOne.SessionFile),
            accessOperation = "memory.read",
            resourceId = S2t6Lifecycle,
            resourceRevision = 1L
        }).ConfigureAwait(false);
        RequireS2T6Denied(lifecycleResourceRead, "resource_unavailable",
            "A lifecycle record over an approved member opened a read the collection grant never approved");

        // Direct denied reads stay denied, and the denial shape discloses nothing.
        var oneSharedRead = await S2T6AgentReadAsync(fixture, agentOne, "memory.read", S2t6Shared, 1L).ConfigureAwait(false);
        Require(ReplyCode(oneSharedRead) == "memory",
            "Agent one could not read a member its own grant approved (reply=" + oneSharedRead.GetRawText() + ").");
        var oneBetaDenied = await S2T6AgentReadAsync(fixture, agentOne, "memory.read", S2t6BetaOnly, 1L).ConfigureAwait(false);
        RequireS2T6Denied(oneBetaDenied, "resource_unavailable",
            "Agent one read a collection member its grant never approved");
        var twoHiddenDenied = await S2T6AgentReadAsync(fixture, agentTwo, "memory.read", S2t6Hidden, 1L).ConfigureAwait(false);
        RequireS2T6Denied(twoHiddenDenied, "resource_unavailable",
            "Agent two read a member its narrowed subset grant never approved");
        var twoSharedRead = await S2T6AgentReadAsync(fixture, agentTwo, "memory.read", S2t6Shared, 1L).ConfigureAwait(false);
        Require(ReplyCode(twoSharedRead) == "memory" && twoSharedRead.TryGetProperty("memoryItem", out var twoSharedItem) &&
                twoSharedItem.ValueKind == JsonValueKind.Object && twoSharedItem.GetProperty("title").GetString() == sharedTitleOne,
            "Agent two did not read the approved shared member at its frozen revision (reply=" + twoSharedRead.GetRawText() + ").");
        var unknownDenied = await S2T6AgentReadAsync(fixture, agentOne, "memory.read", "s2t6-not-present", 1L).ConfigureAwait(false);
        RequireS2T6Denied(unknownDenied, "resource_unavailable",
            "An unknown resource produced a distinguishable denial (existence oracle)");
        var bogusSession = await InvokeAgentRequestAsync(fixture.AgentPipe, new
        {
            protocolVersion = 1,
            operation = "agent.collection.view",
            sessionToken = "mb2_not-a-real-session-bearer"
        }).ConfigureAwait(false);
        Require(ReplyCode(bogusSession) == "session_unknown",
            "A bogus session bearer did not fail closed on the collection view (reply=" + bogusSession.GetRawText() + ").");
        var steeredView = await InvokeAgentRequestAsync(fixture.AgentPipe, new
        {
            protocolVersion = 1,
            operation = "agent.collection.view",
            sessionToken = ReadSessionBearer(agentOne.SessionFile),
            resourceId = S2t6BetaOnly
        }).ConfigureAwait(false);
        Require(ReplyCode(steeredView) == "forbidden",
            "A request field steered the collection view toward another scope (reply=" + steeredView.GetRawText() + ").");
        Console.WriteLine("PASS collection membership grants no read of unapproved members, relations, lifecycle records or sources, and direct denials stay generic");

        // Phase 5 - a zone-granted session is a wider, still-frozen snapshot. It is
        // snapshotted before the descendants, lifecycle record and archive exist, so
        // those later resources must not enter its view.
        var agentThree = await IssueS2T6GrantAsync(executable, fixture, "s2t6-agent-three",
            "memory.read,source.read,relation.read,resource.read", zoneId: S2t6Zone).ConfigureAwait(false);
        var threeView = await AgentS2T6CollectionViewAsync(executable, fixture, agentThree).ConfigureAwait(false);
        Require(threeView.ExitCode == 0, "The zone-granted agent could not browse its scope (exit=" + threeView.ExitCode + "; stderr=" + threeView.StandardError.Trim() + ").");
        RequireS2T6ViewStdout(threeView.StandardOutput, new[]
        {
            (S2t6Relation, 1L),
            (S2t6AlphaOnly, 1L),
            (S2t6BetaOnly, 1L),
            (S2t6Hidden, 1L),
            (S2t6Shared, 1L),
            (S2t6SourceOne, 1L)
        }, "The zone-granted agent's permission-filtered view");
        var threeRelationRead = await S2T6AgentReadAsync(fixture, agentThree, "relation.read", S2t6Relation, 1L).ConfigureAwait(false);
        Require(ReplyCode(threeRelationRead) == "relation",
            "The zone-granted agent could not read a relation its own zone approved (reply=" + threeRelationRead.GetRawText() + ").");
        var threeSourceRead = await S2T6AgentReadAsync(fixture, agentThree, "source.read", S2t6SourceOne, 1L).ConfigureAwait(false);
        Require(ReplyCode(threeSourceRead) == "source",
            "The zone-granted agent could not read a registered source its own zone approved (reply=" + threeSourceRead.GetRawText() + ").");
        Console.WriteLine("PASS the zone-granted view is exact for its frozen snapshot and reads its own relation and source");

        // Phase 7 - new zone descendants and derived records never widen a frozen
        // grant. Two items, one lifecycle record and one retained archive are created
        // inside the zone after the zone snapshot, and none of them can be read.
        await WriteS2T6ItemDraftAsync(executable, fixture, S2t6Descendant, S2t6Zone,
            S2T6ItemDraft("claim", "Synthetic S2-T6 descendant title", "Synthetic S2-T6 descendant body; never a real owner secret.", now), 1).ConfigureAwait(false);
        await WriteS2T6ItemDraftAsync(executable, fixture, S2t6ZoneChild, S2t6Zone,
            S2T6ItemDraft("claim", "Synthetic S2-T6 zone child title", "Synthetic S2-T6 zone child body; never a real owner secret.", now), 1).ConfigureAwait(false);
        var lifecycleCreate = await CreateS2T6LifecycleAsync(executable, fixture, S2t6Shared,
            S2T5LifecycleDraft("Synthetic S2-T6 lifecycle basis; never a real owner secret.")).ConfigureAwait(false);
        Require(lifecycleCreate.ExitCode == 0 && lifecycleCreate.StandardOutput.Contains($"subject={S2t6Shared}", StringComparison.Ordinal),
            "The owner could not create the S2-T6 lifecycle record (exit=" + lifecycleCreate.ExitCode + "; stderr=" + lifecycleCreate.StandardError.Trim() + ").");
        var retain = await RetainS2T6LifecycleAsync(executable, fixture, S2t6Shared, 1).ConfigureAwait(false);
        Require(retain.ExitCode == 0 && retain.StandardOutput.Contains($"RETAINED revision=1 archive={S2t6Archive}", StringComparison.Ordinal),
            "The owner could not retain the S2-T6 shared revision 1 (exit=" + retain.ExitCode + "; stdout=" + retain.StandardOutput.Trim() + "; stderr=" + retain.StandardError.Trim() + ").");

        var threeViewAfterDescendants = await AgentS2T6CollectionViewAsync(executable, fixture, agentThree).ConfigureAwait(false);
        Require(threeViewAfterDescendants.ExitCode == 0,
            "The zone-granted agent's view stopped working after new zone descendants (exit=" + threeViewAfterDescendants.ExitCode + "; stderr=" + threeViewAfterDescendants.StandardError.Trim() + ").");
        RequireS2T6ViewStdout(threeViewAfterDescendants.StandardOutput, new[]
        {
            (S2t6Relation, 1L),
            (S2t6AlphaOnly, 1L),
            (S2t6BetaOnly, 1L),
            (S2t6Hidden, 1L),
            (S2t6Shared, 1L),
            (S2t6SourceOne, 1L)
        }, "The zone-granted agent's view after new zone descendants");
        // The lifecycle record advanced to revision 2 when it recorded the retain
        // act, so the live revision is probed: the denial must be a scope denial,
        // not a stale-revision artifact.
        foreach (var (descendantId, descendantRevision) in new[]
                 {
                     (S2t6Descendant, 1L),
                     (S2t6ZoneChild, 1L),
                     (S2t6Lifecycle, 2L),
                     (S2t6Archive, 1L)
                 })
        {
            var descendantDenied = await S2T6AgentReadAsync(fixture, agentThree, "memory.read", descendantId, descendantRevision).ConfigureAwait(false);
            RequireS2T6Denied(descendantDenied, "resource_unavailable",
                "A new zone descendant opened a read inside a frozen zone grant: " + descendantId + " revision " + descendantRevision);
        }

        var oneLifecycleDenied = await S2T6AgentReadAsync(fixture, agentOne, "memory.read", S2t6Lifecycle, 2L).ConfigureAwait(false);
        RequireS2T6Denied(oneLifecycleDenied, "resource_unavailable",
            "A lifecycle record created after the collection grant opened a read");
        var oneArchiveDenied = await S2T6AgentReadAsync(fixture, agentOne, "memory.read", S2t6Archive, 1L).ConfigureAwait(false);
        RequireS2T6Denied(oneArchiveDenied, "resource_unavailable",
            "A retained archive created after the collection grant opened a read");
        Console.WriteLine("PASS new zone descendants, the derived lifecycle record and the retained archive never widen a frozen grant");

        // Phase 6 - identity survives a title revision. The item keeps its ID and
        // moves to revision 2; the frozen snapshots still name revision 1, so both
        // the stale and the newer revision fail closed under the old sessions.
        await ReviseS2T6ItemDraftAsync(executable, fixture, S2t6Shared, S2t6Zone,
            S2T6ItemDraft("claim", sharedTitleTwo, sharedBodyTwo, now), 1, 2).ConfigureAwait(false);
        var ownerReadAfterRevise = await ReadS2T6MemoryItemAsync(executable, fixture, S2t6Shared).ConfigureAwait(false);
        Require(ownerReadAfterRevise.ExitCode == 0 && ownerReadAfterRevise.StandardOutput.Contains($"MEMORY id={S2t6Shared}", StringComparison.Ordinal) &&
                ownerReadAfterRevise.StandardOutput.Contains("revision=2", StringComparison.Ordinal) &&
                ownerReadAfterRevise.StandardOutput.Contains($"title={sharedTitleTwo}", StringComparison.Ordinal),
            "The owner did not read the same ID at its new revision and title after the revise (stdout=" + ownerReadAfterRevise.StandardOutput.Trim() + ").");
        var oneViewAfterRevise = await AgentS2T6CollectionViewAsync(executable, fixture, agentOne).ConfigureAwait(false);
        Require(oneViewAfterRevise.ExitCode == 0,
            "Agent one's view stopped working after the title revision (exit=" + oneViewAfterRevise.ExitCode + "; stderr=" + oneViewAfterRevise.StandardError.Trim() + ").");
        RequireS2T6ViewStdout(oneViewAfterRevise.StandardOutput, new[]
        {
            ("s2t6-alpha-only", 1L),
            ("s2t6-hidden", 1L),
            ("s2t6-shared", 1L)
        }, "Agent one's view after the title revision");
        var staleRevision = await S2T6AgentReadAsync(fixture, agentOne, "memory.read", S2t6Shared, 1L).ConfigureAwait(false);
        RequireS2T6Denied(staleRevision, "resource_unavailable",
            "A frozen snapshot still read the superseded revision after the revise");
        var unseenRevision = await S2T6AgentReadAsync(fixture, agentOne, "memory.read", S2t6Shared, 2L).ConfigureAwait(false);
        RequireS2T6Denied(unseenRevision, "resource_unavailable",
            "A frozen snapshot read a revision created after its grant");
        var threeViewAfterRevise = await AgentS2T6CollectionViewAsync(executable, fixture, agentThree).ConfigureAwait(false);
        Require(threeViewAfterRevise.ExitCode == 0,
            "The zone-granted agent's view stopped working after the title revision (exit=" + threeViewAfterRevise.ExitCode + "; stderr=" + threeViewAfterRevise.StandardError.Trim() + ").");
        RequireS2T6ViewStdout(threeViewAfterRevise.StandardOutput, new[]
        {
            (S2t6Relation, 1L),
            (S2t6AlphaOnly, 1L),
            (S2t6BetaOnly, 1L),
            (S2t6Hidden, 1L),
            (S2t6Shared, 1L),
            (S2t6SourceOne, 1L)
        }, "The zone-granted agent's view after the title revision");
        Console.WriteLine("PASS IDs survive a title/path revision and frozen snapshots deny both the superseded and the newer revision");

        // Phase 8 - membership mutation advances the policy generation and fails every
        // live session closed instead of silently re-scoping it.
        await SetS2T6CollectionAsync(executable, fixture, S2t6CollectionAlpha,
            S2t6AlphaOnly, S2t6Descendant, S2t6Hidden, S2t6Shared).ConfigureAwait(false);
        foreach (var (session, label) in new[] { (agentOne, "Agent one"), (agentTwo, "Agent two"), (agentThree, "The zone-granted agent") })
        {
            var revokedView = await AgentS2T6CollectionViewAsync(executable, fixture, session).ConfigureAwait(false);
            Require(revokedView.ExitCode == 3 && revokedView.StandardError.Contains("session_revoked", StringComparison.Ordinal),
                label + " kept browsing after the owner changed collection membership (exit=" + revokedView.ExitCode + "; stderr=" + revokedView.StandardError.Trim() + ").");
        }

        // A fresh grant from the expanded collection is exactly the new membership at
        // live revisions, and it denies the superseded revision and the other collection.
        var agentFour = await IssueS2T6GrantAsync(executable, fixture, "s2t6-agent-four",
            "memory.read,source.read,relation.read,resource.read", collectionId: S2t6CollectionAlpha).ConfigureAwait(false);
        var fourView = await AgentS2T6CollectionViewAsync(executable, fixture, agentFour).ConfigureAwait(false);
        Require(fourView.ExitCode == 0, "The fresh grant could not browse the expanded collection (exit=" + fourView.ExitCode + "; stderr=" + fourView.StandardError.Trim() + ").");
        RequireS2T6ViewStdout(fourView.StandardOutput, new[]
        {
            ("s2t6-alpha-only", 1L),
            ("s2t6-descendant", 1L),
            ("s2t6-hidden", 1L),
            ("s2t6-shared", 2L)
        }, "The fresh grant's view of the expanded collection");
        var fourSharedRead = await S2T6AgentReadAsync(fixture, agentFour, "memory.read", S2t6Shared, 2L).ConfigureAwait(false);
        Require(ReplyCode(fourSharedRead) == "memory" && fourSharedRead.TryGetProperty("memoryItem", out var fourSharedItem) &&
                fourSharedItem.ValueKind == JsonValueKind.Object && fourSharedItem.GetProperty("title").GetString() == sharedTitleTwo,
            "The fresh grant did not read the approved live revision with its new title (reply=" + fourSharedRead.GetRawText() + ").");
        var fourStaleDenied = await S2T6AgentReadAsync(fixture, agentFour, "memory.read", S2t6Shared, 1L).ConfigureAwait(false);
        RequireS2T6Denied(fourStaleDenied, "resource_unavailable",
            "The fresh grant read a superseded revision");
        var fourBetaDenied = await S2T6AgentReadAsync(fixture, agentFour, "memory.read", S2t6BetaOnly, 1L).ConfigureAwait(false);
        RequireS2T6Denied(fourBetaDenied, "resource_unavailable",
            "The fresh grant read a member of the other collection");
        Console.WriteLine("PASS a membership change fails live sessions closed and a fresh grant is exactly the new membership at live revisions");

        // Phase 9 - source revision and retained-revision identity. The registered
        // source's locator is its path: revising it keeps the ID, and a grant over the
        // archive scope reads only that archived revision.
        await ReviseS2T6SourceDraftAsync(executable, fixture, S2t6SourceOne, S2t6Zone,
            S2T6SourceDraft("Synthetic S2-T6 private source title", "s2t6-private-locator-revised", "s2t6-source-v2", now), 1, 2).ConfigureAwait(false);
        var ownerSourceRead = await ReadS2T6SourceAsync(executable, fixture, S2t6SourceOne).ConfigureAwait(false);
        Require(ownerSourceRead.ExitCode == 0 && ownerSourceRead.StandardOutput.Contains($"SOURCE id={S2t6SourceOne}", StringComparison.Ordinal) &&
                ownerSourceRead.StandardOutput.Contains("revision=2", StringComparison.Ordinal) &&
                ownerSourceRead.StandardOutput.Contains("locator=s2t6-private-locator-revised", StringComparison.Ordinal),
            "The owner did not read the same source ID at its revised locator (stdout=" + ownerSourceRead.StandardOutput.Trim() + ").");
        var threeSourceStale = await S2T6AgentReadAsync(fixture, agentThree, "source.read", S2t6SourceOne, 1L).ConfigureAwait(false);
        RequireS2T6Denied(threeSourceStale, "resource_unavailable",
            "A frozen snapshot read the superseded source revision");
        var retainedRead = await ReadS2T6RetainedAsync(executable, fixture, S2t6Shared, 1).ConfigureAwait(false);
        Require(retainedRead.ExitCode == 0 && retainedRead.StandardOutput.Contains($"RETAINED archive={S2t6Archive}", StringComparison.Ordinal) &&
                retainedRead.StandardOutput.Contains("subjectRevision=1", StringComparison.Ordinal) &&
                retainedRead.StandardOutput.Contains(sharedTitleOne, StringComparison.Ordinal),
            "The retained historical revision was not retrievable at its exact revision (exit=" + retainedRead.ExitCode + "; stdout=" + retainedRead.StandardOutput.Trim() + ").");
        Console.WriteLine("PASS IDs survive a source path revision and the retained archive serves only its exact archived revision");

        // Phase 10 - lock, restart and encryption. Lock denies owner and agent reads,
        // restart does not resurrect memory-only sessions, unlock restores state, and
        // nothing private survives as plaintext.
        var locked = await RunOwnerCommandAsync(executable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(locked.ExitCode == 0, "Owner lock did not complete for the S2-T6 lock boundary check.");
        var lockedCollections = await RunOwnerCommandAsync(executable,
            "owner", "collections", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(lockedCollections.ExitCode == 3 && lockedCollections.StandardError.Contains("vault_locked", StringComparison.Ordinal),
            "A locked vault listed owner collections (exit=" + lockedCollections.ExitCode + "; stderr=" + lockedCollections.StandardError.Trim() + ").");
        var lockedView = await AgentS2T6CollectionViewAsync(executable, fixture, agentFour).ConfigureAwait(false);
        Require(lockedView.ExitCode == 3 && lockedView.StandardError.Contains("vault_locked", StringComparison.Ordinal),
            "A locked vault served an agent collection view (exit=" + lockedView.ExitCode + "; stderr=" + lockedView.StandardError.Trim() + ").");
        var lockedDiscovery = await InvokeAgentRequestAsync(fixture.AgentPipe, new
        {
            protocolVersion = 1,
            operation = "catalog.list"
        }).ConfigureAwait(false);
        Require(lockedDiscovery.TryGetProperty("status", out var lockedStatus) && lockedStatus.GetString() == "catalog" &&
                lockedDiscovery.TryGetProperty("publishedCatalog", out var lockedEntries) &&
                lockedEntries.ValueKind == JsonValueKind.Array && lockedEntries.GetArrayLength() == 0,
            "Locked catalog discovery still served a published entry (reply=" + lockedDiscovery.GetRawText() + ").");

        // The restart happens on the same synthetic fixture, so the running
        // service is stopped first exactly like the S2-T4 durability check.
        await StopServiceAsync(service).ConfigureAwait(false);
        var restarted = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
        var restartedStatus = await ReadStatusAsync(executable, fixture.ControlPipe).ConfigureAwait(false);
        Require(restartedStatus.Contains("vault=locked", StringComparison.Ordinal),
            "The restarted service did not return locked for the S2-T6 durability check.");

        // A locked vault fails closed before any session lookup, so the
        // memory-only session resurrection check happens after the unlock: the
        // bearer is still valid but the session it named is gone.
        var reunlock = await RunOwnerCommandWithInputAsync(executable,
            passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(reunlock.ExitCode == 0 && reunlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
            "The passphrase did not unlock the restarted S2-T6 vault (exit=" + reunlock.ExitCode + "; stderr=" + reunlock.StandardError.Trim() + ").");
        var resurrectedSession = await AgentS2T6CollectionViewAsync(executable, fixture, agentOne).ConfigureAwait(false);
        Require(resurrectedSession.ExitCode == 3 && resurrectedSession.StandardError.Contains("session_unknown", StringComparison.Ordinal),
            "Restart resurrected a memory-only agent session (exit=" + resurrectedSession.ExitCode + "; stderr=" + resurrectedSession.StandardError.Trim() + ").");
        var collectionsAfterRestart = await ListS2T6CollectionsAsync(executable, fixture).ConfigureAwait(false);
        Require(collectionsAfterRestart.ExitCode == 0 && collectionsAfterRestart.StandardOutput.Contains("COLLECTIONS count=2", StringComparison.Ordinal),
            "The owner collection memberships did not survive restart and unlock (stdout=" + collectionsAfterRestart.StandardOutput.Trim() + ").");
        var sharedAfterRestart = await ReadS2T6MemoryItemAsync(executable, fixture, S2t6Shared).ConfigureAwait(false);
        Require(sharedAfterRestart.ExitCode == 0 && sharedAfterRestart.StandardOutput.Contains("revision=2", StringComparison.Ordinal) &&
                sharedAfterRestart.StandardOutput.Contains($"title={sharedTitleTwo}", StringComparison.Ordinal),
            "The revised shared item did not survive restart and unlock (stdout=" + sharedAfterRestart.StandardOutput.Trim() + ").");
        var retainedAfterRestart = await ReadS2T6RetainedAsync(executable, fixture, S2t6Shared, 1).ConfigureAwait(false);
        Require(retainedAfterRestart.ExitCode == 0 && retainedAfterRestart.StandardOutput.Contains(sharedTitleOne, StringComparison.Ordinal),
            "The retained archive did not survive restart and unlock (stdout=" + retainedAfterRestart.StandardOutput.Trim() + ").");
        var catalogAfterRestart = await InvokeAgentRequestAsync(fixture.AgentPipe, new
        {
            protocolVersion = 1,
            operation = "catalog.list"
        }).ConfigureAwait(false);
        Require(catalogAfterRestart.TryGetProperty("status", out var restartedCatalogStatus) && restartedCatalogStatus.GetString() == "catalog" &&
                catalogAfterRestart.TryGetProperty("publishedCatalog", out var restartedCatalogEntries) &&
                restartedCatalogEntries.ValueKind == JsonValueKind.Array && restartedCatalogEntries.GetArrayLength() == 0,
            "The withdrawn catalog entry came back after restart and unlock (reply=" + catalogAfterRestart.GetRawText() + ").");
        Console.WriteLine("PASS lock denies owner and agent reads, restart drops memory-only sessions, and unlock restores memberships, revisions and archives");

        await StopServiceAsync(restarted).ConfigureAwait(false);
        VerifyS2T6CiphertextOnly(fixture, expectedResources: 10);
        Console.WriteLine("PASS the private memberships and every private body stayed ciphertext at rest with exactly the expected resource set");
        return null;
    }

    /// <summary>
    /// Asserts one denied agent reply is the generic denial for the expected
    /// reason, so a denial can never be told apart from an unknown resource.
    /// </summary>
    private static void RequireS2T6Denied(JsonElement reply, string expectedError, string label)
    {
        Require(reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String && error.GetString() == expectedError,
            label + " did not fail closed with " + expectedError + " (reply=" + reply.GetRawText() + ").");
    }

    /// <summary>
    /// Reads one resource on the agent channel through the real named pipe with the
    /// exact bearer of the given session, returning the raw reply for the caller's
    /// assertions. Denials are the generic agent family.
    /// </summary>
    private static async Task<JsonElement> S2T6AgentReadAsync(
        SmokeFixture fixture,
        S2T4Session session,
        string accessOperation,
        string resourceId,
        long revision) =>
        await InvokeAgentRequestAsync(fixture.AgentPipe, new
        {
            protocolVersion = 1,
            operation = accessOperation switch
            {
                "relation.read" => "agent.relation.read",
                "source.read" => "agent.source.read",
                _ => "agent.memory.read"
            },
            sessionToken = ReadSessionBearer(session.SessionFile),
            accessOperation,
            resourceId,
            resourceRevision = revision
        }).ConfigureAwait(false);

    /// <summary>
    /// Asserts the owner CLI's collection-view output carries exactly the approved
    /// resource/revision pairs in canonical order, with no count line, no member
    /// duplication, and no title, zone, kind or collection name.
    /// </summary>
    private static void RequireS2T6ViewStdout(
        string stdout,
        (string ResourceId, long Revision)[] expected,
        string label)
    {
        var lines = stdout.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        Require(lines.Length > 0, label + " printed no collection view.");
        Require(lines[0].StartsWith("COLLECTION VIEW session=", StringComparison.Ordinal) &&
                lines[0].Contains(" grant=", StringComparison.Ordinal) &&
                lines[0].Contains(" generation=", StringComparison.Ordinal),
            label + " did not print the approved session/grant/generation header (stdout=" + stdout.Trim() + ").");
        Require(!stdout.Contains("count", StringComparison.Ordinal) && !stdout.Contains("total", StringComparison.Ordinal),
            label + " printed a count or total line.");
        foreach (var forbidden in new[] { S2t6Zone, S2t6CollectionAlpha, S2t6CollectionBeta, "s2t6-catalog" })
        {
            Require(!stdout.Contains(forbidden, StringComparison.Ordinal),
                label + " disclosed metadata outside the approved scope: " + forbidden);
        }

        var approved = lines
            .Where(line => line.StartsWith("approved resource=", StringComparison.Ordinal))
            .ToArray();
        Require(approved.Length == expected.Length,
            label + " printed " + approved.Length + " approved resources instead of " + expected.Length + " (stdout=" + stdout.Trim() + ").");
        Require(approved.Distinct(StringComparer.Ordinal).Count() == approved.Length,
            label + " printed the same approved resource more than once (stdout=" + stdout.Trim() + ").");
        foreach (var (resource, revision) in expected)
        {
            Require(approved.Contains($"approved resource={resource} revision={revision}", StringComparer.Ordinal),
                label + " is missing the approved pair " + resource + " revision " + revision + " (stdout=" + stdout.Trim() + ").");
        }

        var operations = lines
            .Where(line => line.StartsWith("operations=", StringComparison.Ordinal))
            .ToArray();
        Require(operations.Length == 1 && operations[0] == "operations=memory.read,relation.read,resource.read,source.read",
            label + " did not print the granted operations exactly once (stdout=" + stdout.Trim() + ").");
        Require(lines.Count(line => !line.StartsWith("COLLECTION VIEW session=", StringComparison.Ordinal) &&
                !line.StartsWith("approved resource=", StringComparison.Ordinal) &&
                !line.StartsWith("operations=", StringComparison.Ordinal)) == 0,
            label + " printed a line outside the approved view contract (stdout=" + stdout.Trim() + ").");
    }
}
