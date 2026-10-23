using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.OwnerUi;

namespace MetaBrain.S1T4.Smoke;

// S5-T3: the owner library search/browse/filter and evidence-detail surface the
// owner console drives. One synthetic owner vault is provisioned and unlocked
// through the real owner CLI over the real named-pipe service; a bilingual,
// multi-zone, multi-kind/level/time corpus with citations (current, superseded,
// unknown/missing) is written through the real owner CLI and materialized by the
// real index rebuild. The console's own session — the exact class the WPF window
// drives — then reads library pages, item detail, source detail and title
// revisions over the real owner control pipe: the service applies every filter
// the owner named, the ranking comes from the service's own search
// implementation, a collection only narrows, the lock clears the private views
// and a late answer cannot restore them. Nothing here is a mock, and no
// assertion is a source-text echo: every observation comes from the real
// service, the real client or the real console session.
internal static partial class OwnerServiceSmoke
{
    private const string S5T3ZoneAlpha = "s5t3-zone-alpha";
    private const string S5T3ZoneBeta = "s5t3-zone-beta";
    private const string S5T3Collection = "s5t3-collection";
    private const string S5T3SourceCurrent = "s5t3-source-current";
    private const string S5T3SourceMoves = "s5t3-source-moves";
    private const string S5T3SourceUnregistered = "s5t3-source-unregistered";
    private const string S5T3Login = "s5t3-doc-login";
    private const string S5T3Deploy = "s5t3-doc-deploy";
    private const string S5T3Symbol = "s5t3-doc-symbol";
    private const string S5T3Retired = "s5t3-doc-retired";
    private const string S5T3RankFix = "s5t3-doc-rank-fix";
    private const string S5T3RankMention = "s5t3-doc-rank-mention";
    private const string S5T3UnknownSource = "s5t3-doc-unknown-source";
    private const string S5T3Superseded = "s5t3-doc-superseded";
    private const string S5T3Assistant = "s5t3-doc-assistant";
    private const string S5T3BetaDoc = "s5t3-doc-beta-zone";
    private const string S5T3Marker = "s5t3-synthetic-private-marker-9f21";
    private const string S5T3LoginVietnamese = "cach dang nhap he thong sau khi quen mat khau";
    private const string S5T3RenamedTitle = "Synthetic S5-T3 renamed login lesson";
    private const int S5T3CorpusResources = 12;

    private static string S5T3ItemDraft(
        string kind,
        string author,
        string creationMethod,
        string evidence,
        string level,
        string title,
        string body,
        DateTimeOffset occurred,
        object? sources = null) =>
        JsonSerializer.Serialize(new
        {
            kind,
            author,
            creationMethod,
            evidence,
            level,
            title,
            body,
            sources = sources ?? Array.Empty<object>(),
            occurredAtUtc = occurred,
            recordedAtUtc = occurred.AddMinutes(1),
            applicability = (object?)null
        });

    private static string S5T3SourceDraft(string title, string locator, string version, string availability, DateTimeOffset now) =>
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

    /// <summary>The one citation the login document carries: a live registry revision.</summary>
    private static object[] S5T3CurrentSourceAnchor() => new object[]
    {
        new
        {
            sourceId = S5T3SourceCurrent,
            locator = (string?)null,
            availability = "available",
            sourceRevision = 1L,
            span = new { start = 0L, end = 32L }
        }
    };

    /// <summary>A reference that names a source and no revision: it stays unknown.</summary>
    private static object[] S5T3UnknownSourceAnchor() => new object[]
    {
        new
        {
            sourceId = S5T3SourceUnregistered,
            locator = (string?)null,
            availability = "unavailable",
            sourceRevision = (long?)null,
            span = (object?)null
        }
    };

    internal static async Task RunS5T3LibraryAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S5-T3 library smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S5-T3 owner library search/browse/filter and evidence detail over the real owner control pipe");
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
                "Provision did not create the S5-T3 synthetic vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");

            var now = DateTimeOffset.UtcNow.AddMinutes(-5);
            await S5T3WriteCorpusAsync(connectionsExecutable, fixture.ControlPipe, fixture.OutputDirectory, now).ConfigureAwait(false);
            Console.WriteLine("PASS the synthetic bilingual/zone/kind/level/time corpus is stored and the owner collection holds its two members");

            var rebuild = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(rebuild.ExitCode == 0 && rebuild.StandardOutput.Contains("INDEX rebuilt rows=", StringComparison.Ordinal) &&
                    !rebuild.StandardOutput.Contains("rows=0", StringComparison.Ordinal),
                "The S5-T3 private index rebuild did not publish a materialized generation (exit=" + rebuild.ExitCode + "; stdout=" + rebuild.StandardOutput.Trim() + "; stderr=" + rebuild.StandardError.Trim() + ").");

            // The console's own session: the library surface under test is the exact
            // class the WPF window drives, over the real owner control pipe.
            var session = new OwnerConsoleSession(fixture.ControlPipe);
            await session.AttachAsync().ConfigureAwait(false);
            Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
                "The console session did not report the real unlocked vault (" + session.ReportedVaultState + ").");
            Console.WriteLine("PASS the owner console session reports the real unlocked vault");

            var loginPage = await S5T3SearchAsync(session, new OwnerLibraryQuery(Text: "login", Limit: 10)).ConfigureAwait(false);
            Require(loginPage is { Total: 2 } &&
                    S5T3HasHit(loginPage, S5T3Login) &&
                    S5T3HasHit(loginPage, S5T3BetaDoc),
                "The bilingual query 'login' did not recall both the Vietnamese login body and the beta-zone login body (" + S5T3Describe(loginPage) + ").");
            Console.WriteLine("PASS an English query recalls a Vietnamese library body, and the whole unlocked vault is searched");

            var loginAlpha = await S5T3SearchAsync(session, new OwnerLibraryQuery(Text: "login", ZoneId: S5T3ZoneAlpha, Limit: 10)).ConfigureAwait(false);
            Require(loginAlpha is { Total: 1 } && S5T3HasHit(loginAlpha, S5T3Login),
                "The zone filter alpha did not restrict 'login' to that zone (" + S5T3Describe(loginAlpha) + ").");
            var loginBeta = await S5T3SearchAsync(session, new OwnerLibraryQuery(Text: "login", ZoneId: S5T3ZoneBeta, Limit: 10)).ConfigureAwait(false);
            Require(loginBeta is { Total: 1 } && S5T3HasHit(loginBeta, S5T3BetaDoc) &&
                    loginBeta.Hits.All(hit => string.Equals(hit.ZoneId, S5T3ZoneBeta, StringComparison.Ordinal)),
                "The zone filter beta did not return exactly the beta-zone hit the service reported (" + S5T3Describe(loginBeta) + ").");
            Console.WriteLine("PASS the zone filter narrows the search and the page carries the zone the service reported");

            var deployPage = await S5T3SearchAsync(session, new OwnerLibraryQuery(Text: "trien khai", Limit: 10)).ConfigureAwait(false);
            Require(deployPage is { Total: 1 } && S5T3HasHit(deployPage, S5T3Deploy),
                "The Vietnamese query 'trien khai' did not recall the deployment document (" + S5T3Describe(deployPage) + ").");
            var symbolPage = await S5T3SearchAsync(session, new OwnerLibraryQuery(Text: "redeem_token", Limit: 10)).ConfigureAwait(false);
            Require(symbolPage is not null && S5T3HasHit(symbolPage, S5T3Symbol),
                "The symbol query 'redeem_token' did not recall the camelCase document (" + S5T3Describe(symbolPage) + ").");
            Require(string.Equals(symbolPage!.Hits[0].Id, S5T3Symbol, StringComparison.Ordinal),
                "The symbol query 'redeem_token' did not rank the camelCase document first (" + S5T3Describe(symbolPage) + ").");
            Console.WriteLine("S5T3-PROBE symbol query ranked " + string.Join(",", symbolPage.Hits.Select(hit => hit.Id + "=" + hit.Score)) +
                " (the service's own expansion ring is symmetric, so a Vietnamese body sharing one expanded token may follow, never lead)");
            Console.WriteLine("PASS Vietnamese and symbol queries are answered by the service's own tokenizer");

            var ranked = await S5T3SearchAsync(session, new OwnerLibraryQuery(Text: "fix", Limit: 10)).ConfigureAwait(false);
            Require(ranked is { Total: 2 } &&
                    ranked.Hits.Length == 2 &&
                    string.Equals(ranked.Hits[0].Id, S5T3RankFix, StringComparison.Ordinal) &&
                    ranked.Hits[0].Score > ranked.Hits[1].Score,
                "The service's own ranking did not put the repeated-term hit first with a higher score (" + S5T3Describe(ranked) + ").");
            Console.WriteLine("PASS ranking is the service's own answer (repeated-term hit first, descending score), never the console's");

            var browsed = await S5T3SearchAsync(session, new OwnerLibraryQuery(Limit: 10)).ConfigureAwait(false);
            Require(browsed is { Total: 10 } && browsed.Hits.Length == 10 && !browsed.HasMore,
                "Browsing with no text did not return every memory item in the vault (" + S5T3Describe(browsed) + ").");
            Require(string.Equals(browsed!.Hits[0].Id, S5T3Assistant, StringComparison.Ordinal),
                "The browse page was not ordered by the item's own recorded instant (" + S5T3Describe(browsed) + ").");
            Console.WriteLine("PASS a browse with no text returns the whole vault in the service's recorded-time order");

            var kindFiltered = await S5T3SearchAsync(session, new OwnerLibraryQuery(Text: "trien khai", Kind: "procedure", Limit: 10)).ConfigureAwait(false);
            Require(kindFiltered is { Total: 1 } && S5T3HasHit(kindFiltered, S5T3Deploy),
                "The kind filter did not restrict to procedures (" + S5T3Describe(kindFiltered) + ").");
            var kindExcluded = await S5T3SearchAsync(session, new OwnerLibraryQuery(Text: "trien khai", Kind: "lesson", Limit: 10)).ConfigureAwait(false);
            Require(kindExcluded is { Total: 0 },
                "The kind filter lesson still returned the procedure document (" + S5T3Describe(kindExcluded) + ").");
            var levelExcluded = await S5T3SearchAsync(session, new OwnerLibraryQuery(Text: "retired rollout", Level: "l1", Limit: 10)).ConfigureAwait(false);
            Require(levelExcluded is { Total: 0 },
                "The level filter l1 still returned the l2 document (" + S5T3Describe(levelExcluded) + ").");
            var levelIncluded = await S5T3SearchAsync(session, new OwnerLibraryQuery(Text: "retired rollout", Level: "l2", Limit: 10)).ConfigureAwait(false);
            Require(levelIncluded is { Total: 1 } && S5T3HasHit(levelIncluded, S5T3Retired),
                "The level filter l2 did not return the l2 document (" + S5T3Describe(levelIncluded) + ").");
            Console.WriteLine("PASS the kind and abstraction-level filters are applied by the service");

            var olderWindow = await S5T3SearchAsync(session,
                new OwnerLibraryQuery(Text: "retired rollout", FromUtc: now.AddDays(-120).ToUniversalTime(), Limit: 10)).ConfigureAwait(false);
            Require(olderWindow is { Total: 1 } && S5T3HasHit(olderWindow, S5T3Retired),
                "The inclusive From bound that covers the item excluded it (" + S5T3Describe(olderWindow) + ").");
            var recentWindow = await S5T3SearchAsync(session,
                new OwnerLibraryQuery(Text: "retired rollout", FromUtc: now.AddDays(-30).ToUniversalTime(), Limit: 10)).ConfigureAwait(false);
            Require(recentWindow is { Total: 0 },
                "The From bound that excludes the item still returned it (" + S5T3Describe(recentWindow) + ").");
            var toWindow = await S5T3SearchAsync(session,
                new OwnerLibraryQuery(Text: "retired rollout", ToUtc: now.AddDays(-30).ToUniversalTime(), Limit: 10)).ConfigureAwait(false);
            Require(toWindow is { Total: 1 } && S5T3HasHit(toWindow, S5T3Retired),
                "The To bound that covers the item excluded it (" + S5T3Describe(toWindow) + ").");
            var toWindowExcluded = await S5T3SearchAsync(session,
                new OwnerLibraryQuery(Text: "retired rollout", ToUtc: now.AddDays(-120).ToUniversalTime(), Limit: 10)).ConfigureAwait(false);
            Require(toWindowExcluded is { Total: 0 },
                "The To bound that excludes the item still returned it (" + S5T3Describe(toWindowExcluded) + ").");
            Console.WriteLine("PASS the inclusive time window is applied by the service");

            var byCollection = await S5T3SearchAsync(session, new OwnerLibraryQuery(CollectionId: S5T3Collection, Limit: 10)).ConfigureAwait(false);
            Require(byCollection is { Total: 2 } &&
                    S5T3HasHit(byCollection, S5T3Deploy) &&
                    S5T3HasHit(byCollection, S5T3Retired) &&
                    byCollection.Hits.All(hit =>
                        string.Equals(hit.Id, S5T3Deploy, StringComparison.Ordinal) ||
                        string.Equals(hit.Id, S5T3Retired, StringComparison.Ordinal)),
                "The collection filter did not return exactly the collection's members (" + S5T3Describe(byCollection) + ").");
            var collectionNarrowed = await S5T3SearchAsync(session,
                new OwnerLibraryQuery(Text: "login", CollectionId: S5T3Collection, Limit: 10)).ConfigureAwait(false);
            Require(collectionNarrowed is { Total: 0 },
                "The collection filter widened the search instead of narrowing it (" + S5T3Describe(collectionNarrowed) + ").");
            Console.WriteLine("PASS a collection filter selects the owner's own members and grants nothing: it only narrows");

            var missingCollection = await OwnerChannelClient.SearchLibraryAsync(fixture.ControlPipe,
                new OwnerLibraryQuery(CollectionId: "s5t3-no-such-collection", Limit: 10), CancellationToken.None).ConfigureAwait(false);
            Require(missingCollection.Outcome == OwnerCommandOutcome.Refused && missingCollection.ErrorCode == "collection_unknown" && missingCollection.Page is null,
                "An unknown owner collection was not refused as a service answer (" + missingCollection.Outcome + "/" + missingCollection.ErrorCode + ").");
            var badKind = await OwnerChannelClient.SearchLibraryAsync(fixture.ControlPipe,
                new OwnerLibraryQuery(Text: "login", Kind: "notakind", Limit: 10), CancellationToken.None).ConfigureAwait(false);
            Require(badKind.Outcome == OwnerCommandOutcome.Refused && badKind.ErrorCode == "invalid_request",
                "An unknown kind token was not refused by the service (" + badKind.Outcome + "/" + badKind.ErrorCode + ").");
            var badLevel = await OwnerChannelClient.SearchLibraryAsync(fixture.ControlPipe,
                new OwnerLibraryQuery(Text: "login", Level: "l9", Limit: 10), CancellationToken.None).ConfigureAwait(false);
            Require(badLevel.Outcome == OwnerCommandOutcome.Refused && badLevel.ErrorCode == "invalid_request",
                "An unknown level token was not refused by the service (" + badLevel.Outcome + "/" + badLevel.ErrorCode + ").");
            var badPage = await OwnerChannelClient.SearchLibraryAsync(fixture.ControlPipe,
                new OwnerLibraryQuery(Text: "login", Limit: 0), CancellationToken.None).ConfigureAwait(false);
            Require(badPage.Outcome == OwnerCommandOutcome.Refused && badPage.ErrorCode == "invalid_request",
                "An out-of-range page size was not refused by the service (" + badPage.Outcome + "/" + badPage.ErrorCode + ").");
            Console.WriteLine("PASS unknown collections, unknown filter tokens and out-of-range paging are refused by the service");

            // Detail at the exact revision the page named, with attribution and a current citation.
            var loginHit = loginPage!.Hits.First(hit => string.Equals(hit.Id, S5T3Login, StringComparison.Ordinal));
            await session.OpenLibraryItemAsync(loginHit).ConfigureAwait(false);
            var loginDetail = session.LibraryItem;
            Require(loginDetail is not null &&
                    string.Equals(loginDetail.Id, S5T3Login, StringComparison.Ordinal) &&
                    loginDetail.Revision == loginHit.Revision &&
                    string.Equals(loginDetail.Kind, "lesson", StringComparison.Ordinal) &&
                    string.Equals(loginDetail.Level, "l1", StringComparison.Ordinal) &&
                    string.Equals(loginDetail.Author, "user", StringComparison.Ordinal) &&
                    string.Equals(loginDetail.Executor, "owner", StringComparison.Ordinal) &&
                    string.Equals(loginDetail.CreationMethod, "directWrite", StringComparison.Ordinal) &&
                    string.Equals(loginDetail.Evidence, "unverified", StringComparison.Ordinal) &&
                    loginDetail.Body.Contains(S5T3LoginVietnamese, StringComparison.Ordinal),
                "The item detail did not serve the hit's exact revision with its stored attribution (" + S5T3DescribeDetail(loginDetail) + ").");
            Require(loginDetail!.Citations.Length == 1 &&
                    string.Equals(loginDetail.Citations[0].SourceId, S5T3SourceCurrent, StringComparison.Ordinal) &&
                    loginDetail.Citations[0].SourceRevision == 1 &&
                    string.Equals(loginDetail.Citations[0].Status, "current", StringComparison.Ordinal) &&
                    loginDetail.Citations[0].Warning is null,
                "The current citation was not projected as current and warning-free (" + S5T3DescribeCitations(loginDetail) + ").");
            Console.WriteLine("PASS detail serves the exact revision with stored evidence, attribution and a current citation");

            await session.OpenLibrarySourceAsync(S5T3SourceCurrent, 1).ConfigureAwait(false);
            Require(session.LibrarySource is { } openedSource &&
                    string.Equals(openedSource.Id, S5T3SourceCurrent, StringComparison.Ordinal) &&
                    openedSource.Revision == 1 &&
                    string.Equals(openedSource.Availability, "available", StringComparison.Ordinal) &&
                    string.Equals(openedSource.Title, "Synthetic S5-T3 current registry title", StringComparison.Ordinal),
                "Opening the citation's source did not serve that registry revision (" + S5T3DescribeSource(session.LibrarySource) + ").");
            Console.WriteLine("PASS a citation opens the exact source registry revision it names");

            var unknownHit = browsed!.Hits.First(hit => string.Equals(hit.Id, S5T3UnknownSource, StringComparison.Ordinal));
            await session.OpenLibraryItemAsync(unknownHit).ConfigureAwait(false);
            Require(session.LibraryItem is { } unknownDetail &&
                    unknownDetail.Citations.Length == 1 &&
                    string.Equals(unknownDetail.Citations[0].Status, "unknown", StringComparison.Ordinal) &&
                    string.Equals(unknownDetail.Citations[0].Availability, "unavailable", StringComparison.Ordinal) &&
                    unknownDetail.Citations[0].SourceRevision is null,
                "A citation naming no registered revision was not reported unknown with its declared availability (" + S5T3DescribeDetail(session.LibraryItem) + ").");
            Console.WriteLine("PASS a missing source is reported unknown/unavailable (a loosely declared reference keeps its declared availability), never verified");

            // Revising the source moves the anchor: the citation becomes superseded, the
            // old registry revision fails closed and the live one is served.
            var draftPath = await S5T3WriteDraftAsync(fixture.OutputDirectory,
                S5T3SourceDraft("Synthetic S5-T3 moving registry title v2", "s5t3-synthetic-moving-locator", "synthetic-v2", "available", now)).ConfigureAwait(false);
            var sourceRevised = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-revise", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S5T3SourceMoves, "--zone-id", S5T3ZoneAlpha,
                "--expected-revision", "1", "--input-file", draftPath).ConfigureAwait(false);
            Require(sourceRevised.ExitCode == 0 && sourceRevised.StandardOutput.Contains("SOURCE stored revision=2", StringComparison.Ordinal),
                "The real owner CLI did not revise the moving source (exit=" + sourceRevised.ExitCode + "; stdout=" + sourceRevised.StandardOutput.Trim() + "; stderr=" + sourceRevised.StandardError.Trim() + ").");
            var supersededHit = browsed.Hits.First(hit => string.Equals(hit.Id, S5T3Superseded, StringComparison.Ordinal));
            await session.OpenLibraryItemAsync(supersededHit).ConfigureAwait(false);
            Require(session.LibraryItem is { } supersededDetail &&
                    supersededDetail.Citations.Length == 1 &&
                    string.Equals(supersededDetail.Citations[0].Status, "superseded", StringComparison.Ordinal) &&
                    string.Equals(supersededDetail.Citations[0].Warning, "citation_source_superseded", StringComparison.Ordinal),
                "The moved source anchor was not reported superseded with its warning (" + S5T3DescribeDetail(session.LibraryItem) + ").");
            var movedRevision = await OwnerChannelClient.ReadSourceAsync(fixture.ControlPipe, S5T3SourceMoves, 1, CancellationToken.None).ConfigureAwait(false);
            Require(movedRevision.Outcome == OwnerCommandOutcome.Refused && movedRevision.Source is null,
                "A superseded registry revision was served instead of being refused (" + movedRevision.Outcome + "/" + movedRevision.ErrorCode + ").");
            Console.WriteLine("S5T3-PROBE owner source read at the moved revision: outcome=" + movedRevision.Outcome + " code=" + (movedRevision.ErrorCode ?? "none"));
            await session.OpenLibrarySourceAsync(S5T3SourceMoves, 1).ConfigureAwait(false);
            Require(session.LibrarySource is null && session.LibraryMessage is not null &&
                    session.LibraryMessage.Contains(movedRevision.ErrorCode!, StringComparison.Ordinal),
                "The console did not report the service's own refusal for the moved registry revision (" + (session.LibraryMessage ?? "no message") + ").");
            await session.OpenLibrarySourceAsync(S5T3SourceMoves, 2).ConfigureAwait(false);
            Require(session.LibrarySource is { Revision: 2 },
                "The live registry revision of the revised source was not served (" + S5T3DescribeSource(session.LibrarySource) + ").");
            Console.WriteLine("PASS a moved anchor reports superseded; the old registry revision fails closed and the live one is served");

            // Title edit through the existing authorized revise operation: same ID, new revision.
            await session.OpenLibraryItemAsync(loginHit).ConfigureAwait(false);
            await session.RenameLibraryItemAsync(S5T3RenamedTitle).ConfigureAwait(false);
            Require(session.LibraryItem is { } renamed &&
                    string.Equals(renamed.Id, S5T3Login, StringComparison.Ordinal) &&
                    renamed.Revision == loginHit.Revision + 1 &&
                    string.Equals(renamed.Title, S5T3RenamedTitle, StringComparison.Ordinal) &&
                    string.Equals(renamed.Body, loginDetail.Body, StringComparison.Ordinal) &&
                    string.Equals(renamed.Author, "user", StringComparison.Ordinal) &&
                    string.Equals(renamed.Evidence, "unverified", StringComparison.Ordinal) &&
                    renamed.Citations.Length == 1 &&
                    string.Equals(renamed.Citations[0].Status, "current", StringComparison.Ordinal),
                "The title rename did not keep the ID, body, attribution and citation at a new revision (" + S5T3DescribeDetail(session.LibraryItem) + ").");
            Require(session.LibraryPage is null && session.LibraryMessage is not null &&
                    session.LibraryMessage.Contains("same item, one new revision", StringComparison.Ordinal),
                "The page the write invalidated was not dropped with an explanation (" + session.LibraryMessage + ").");
            Console.WriteLine("PASS a title edit is one new revision of the same item, and the page it invalidated is dropped, not patched");

            // The generation the write moved past is refused, and the owner's rebuild makes the new revision searchable.
            var staleAfterWrite = await OwnerChannelClient.SearchLibraryAsync(fixture.ControlPipe,
                new OwnerLibraryQuery(Text: "login", Limit: 10), CancellationToken.None).ConfigureAwait(false);
            Require(staleAfterWrite.Outcome == OwnerCommandOutcome.Refused && staleAfterWrite.ErrorCode == "search_index_stale" && staleAfterWrite.Page is null,
                "A search of the generation a write had moved past was served instead of refused (" + staleAfterWrite.Outcome + "/" + staleAfterWrite.ErrorCode + ").");
            await session.RebuildLibraryIndexAsync().ConfigureAwait(false);
            Require(session.LibraryIndex is { } rebuiltAfterWrite &&
                    rebuiltAfterWrite.RowCount == S5T3CorpusResources &&
                    !rebuiltAfterWrite.CanonicalMoved &&
                    !rebuiltAfterWrite.Stale,
                "The console's rebuild after a write did not report a fresh generation (" + S5T3DescribeIndex(session.LibraryIndex) + ").");
            var renamedSearch = await S5T3SearchAsync(session, new OwnerLibraryQuery(Text: "login", Limit: 10)).ConfigureAwait(false);
            Require(renamedSearch is { Total: 2 } &&
                    renamedSearch.Hits.Any(hit =>
                        string.Equals(hit.Id, S5T3Login, StringComparison.Ordinal) &&
                        hit.Revision == loginHit.Revision + 1),
                "A rebuilt generation did not serve the renamed item at its new revision (" + S5T3Describe(renamedSearch) + ").");
            Console.WriteLine("PASS a write makes the service refuse the moved generation, and the owner's rebuild makes the new revision searchable");

            // Attribution preservation (R12): an assistant-authored record cannot become
            // user-verified by saving it, and the refusal writes nothing.
            var assistantSearch = await OwnerChannelClient.SearchLibraryAsync(fixture.ControlPipe,
                new OwnerLibraryQuery(Text: "assistant synthesis", Limit: 10), CancellationToken.None).ConfigureAwait(false);
            Require(assistantSearch.Page is { Total: 1 } && assistantSearch.Page.Hits[0].Id == S5T3Assistant,
                "The assistant-authored item was not reachable for the attribution check (" + assistantSearch.Page?.Total + ").");
            await session.OpenLibraryItemAsync(assistantSearch.Page!.Hits[0]).ConfigureAwait(false);
            var assistantItem = session.LibraryItem
                ?? throw new InvalidOperationException("The assistant-authored item detail was not served.");
            Require(string.Equals(assistantItem.Author, "assistant", StringComparison.Ordinal) &&
                    string.Equals(assistantItem.Evidence, "unverified", StringComparison.Ordinal),
                "The assistant-authored item was not served with its stored attribution (" + S5T3DescribeDetail(assistantItem) + ").");
            var upgradedDraft = JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = assistantItem.Kind,
                author = assistantItem.Author,
                creationMethod = assistantItem.CreationMethod,
                evidence = "userConfirmed",
                level = assistantItem.Level,
                title = assistantItem.Title,
                body = assistantItem.Body,
                sources = Array.Empty<object>(),
                occurredAtUtc = assistantItem.OccurredAtUtc?.ToUniversalTime(),
                recordedAtUtc = assistantItem.RecordedAtUtc.ToUniversalTime(),
                applicability = (object?)null
            });
            try
            {
                var upgraded = await InvokeAgentRequestAsync(fixture.ControlPipe, new
                {
                    protocolVersion = 1,
                    operation = "memory.revise",
                    resourceId = assistantItem.Id,
                    zoneId = assistantItem.ZoneId,
                    resourceRevision = assistantItem.Revision,
                    contentBase64 = Convert.ToBase64String(upgradedDraft)
                }).ConfigureAwait(false);
                Require(ReplyCode(upgraded) == "invalid_request",
                    "An evidence upgrade of an assistant-authored record was accepted (" + ReplyCode(upgraded) + ").");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(upgradedDraft);
            }

            var afterUpgrade = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S5T3Assistant, "--resource-revision", assistantItem.Revision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(afterUpgrade.ExitCode == 0 &&
                    afterUpgrade.StandardOutput.Contains("evidence=unverified", StringComparison.Ordinal) &&
                    afterUpgrade.StandardOutput.Contains("author=assistant", StringComparison.Ordinal),
                "The refused evidence upgrade still changed the stored record (stdout=" + afterUpgrade.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS an assistant-authored record keeps its author and evidence: saving never makes it user-verified");

            // An agent session scoped to one item cannot reach a collection member.
            var scopedSession = await IssueS4T2GrantAsync(connectionsExecutable, fixture, "s5t3-scope", "memory.read", new[] { S5T3Symbol });
            var scopedAllowed = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.memory.read",
                sessionToken = ReadSessionBearer(scopedSession.SessionFile),
                accessOperation = "memory.read",
                resourceId = S5T3Symbol,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            Require(ReplyCode(scopedAllowed) == "memory",
                "The discriminator read of the granted item was denied, so the session itself is broken (" + ReplyCode(scopedAllowed) + ").");
            var scopedDenied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.memory.read",
                sessionToken = ReadSessionBearer(scopedSession.SessionFile),
                accessOperation = "memory.read",
                resourceId = S5T3Deploy,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            Require(ReplyCode(scopedDenied) != "memory" && !scopedDenied.GetRawText().Contains(S5T3Deploy, StringComparison.Ordinal),
                "An agent session reached a collection member it was never granted (" + ReplyCode(scopedDenied) + ").");
            Console.WriteLine("PASS collection membership grants no agent access: a member stays unreadable without its own grant");

            foreach (var ownerOperation in new[]
                     {
                         ServiceRequestHandler.OwnerLibrarySearchOperation,
                         ServiceRequestHandler.OwnerRelationListOperation,
                         ServiceRequestHandler.OwnerCatalogPreviewOperation,
                         ServiceRequestHandler.OwnerAccessRequestPreviewOperation,
                         ServiceRequestHandler.OwnerSessionListOperation,
                         ServiceRequestHandler.SourceListOperation,
                         ServiceRequestHandler.SourceStatusOperation,
                         ServiceRequestHandler.SourceMappingSubmitOperation,
                         ServiceRequestHandler.SourceSnapshotListOperation,
                         ServiceRequestHandler.OwnerScopeCollectionListOperation,
                         ServiceRequestHandler.MemoryReadOperation,
                         ServiceRequestHandler.SourceReadOperation,
                         ServiceRequestHandler.OwnerIndexRebuildOperation
                     })
            {
                var denied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = ownerOperation,
                    isOwner = true,
                    consent = "owner-console",
                    origin = "http://localhost",
                    query = "login",
                    resourceId = S5T3Login,
                    sessionId = "spoofed-owner-session"
                }).ConfigureAwait(false);
                Require(ReplyCode(denied) == "forbidden",
                    "The agent channel accepted owner library operation " + ownerOperation + " (" + ReplyCode(denied) + ").");
            }

            Console.WriteLine("PASS the agent channel denies every owner library operation, including spoofed owner/Origin/localhost claims");

            // Lock: private views are cleared, the epoch moves, and a late answer is dropped.
            var preLockSearch = await S5T3SearchAsync(session, new OwnerLibraryQuery(Text: "login", Limit: 10)).ConfigureAwait(false);
            Require(preLockSearch is { Total: 2 },
                "The pre-lock search did not return the vault's matches (" + S5T3Describe(preLockSearch) + ").");
            var preLockHit = preLockSearch!.Hits.First(hit => string.Equals(hit.Id, S5T3Login, StringComparison.Ordinal));
            await session.OpenLibraryItemAsync(preLockHit).ConfigureAwait(false);
            await session.OpenLibrarySourceAsync(S5T3SourceCurrent, 1).ConfigureAwait(false);
            Require(session.LibraryPage is not null && session.LibraryItem is not null && session.LibrarySource is not null,
                "A private page, item and source were not in place before the lock, so the lock boundary could not be exercised.");
            var epochBeforeLock = session.PrivateEpoch;
            var locked = await OwnerChannelClient.LockAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(locked.Outcome == OwnerCommandOutcome.Completed && locked.VaultState == OwnerVaultState.Locked,
                "The owner-channel lock did not complete locked (" + locked.Outcome + "/" + locked.ErrorCode + ").");
            await session.CheckAsync().ConfigureAwait(false);
            Require(session.ReportedVaultState == OwnerVaultState.Locked,
                "The console session did not observe the locked vault (" + session.ReportedVaultState + ").");
            Require(session.LibraryPage is null && session.LibraryItem is null && session.LibrarySource is null &&
                    session.LibraryIndex is null && session.LibraryQuery.Text is null,
                "The lock did not clear the private library views and the owner's search text.");
            Require(session.PrivateEpoch != epochBeforeLock,
                "The private epoch did not move when the private views were cleared.");
            var latePage = await OwnerChannelClient.SearchLibraryAsync(fixture.ControlPipe,
                new OwnerLibraryQuery(Text: "login", Limit: 10), CancellationToken.None).ConfigureAwait(false);
            Require(latePage.Outcome == OwnerCommandOutcome.Refused && latePage.ErrorCode == "vault_locked" && latePage.Page is null,
                "A library search against the locked vault was not refused as vault_locked (" + latePage.Outcome + "/" + latePage.ErrorCode + ").");
            var lateItem = await OwnerChannelClient.ReadMemoryItemAsync(fixture.ControlPipe, S5T3Login, 2, CancellationToken.None).ConfigureAwait(false);
            Require(lateItem.Outcome != OwnerCommandOutcome.Completed && lateItem.Item is null,
                "A locked vault still served private item detail (" + lateItem.Outcome + "/" + lateItem.ErrorCode + ").");

            // The stale-reply boundary itself: real completed answers of the finished
            // epoch must not repopulate the views the lock already cleared.
            var stalePageApplied = session.ApplyLibraryPageAnswer(epochBeforeLock,
                new OwnerLibrarySearchResult(OwnerCommandOutcome.Completed, null, preLockSearch));
            var staleItemApplied = session.ApplyItemAnswer(epochBeforeLock,
                new OwnerItemReadResult(OwnerCommandOutcome.Completed, null, loginDetail), renamed: false);
            Require(!stalePageApplied && !staleItemApplied && session.LibraryPage is null && session.LibraryItem is null,
                "A completed library answer for the finished unlocked epoch repopulated the cleared private view.");
            Require(session.Activity.Any(entry => entry.Contains("ignored a stale search answer", StringComparison.Ordinal)) &&
                    session.Activity.Any(entry => entry.Contains("ignored a stale item detail answer", StringComparison.Ordinal)),
                "The console did not record that it dropped the stale library answers.");
            await session.SearchLibraryAsync().ConfigureAwait(false);
            Require(session.LibraryPage is null,
                "A library act ran while the service reported the vault locked.");
            Console.WriteLine("PASS the lock clears the private views, refuses further private reads and drops completed answers of the finished epoch");
            Console.WriteLine("S5T3-PROBE locked detail answer outcome=" + lateItem.Outcome + " code=" + (lateItem.ErrorCode ?? "(none)") +
                "; locked search outcome=" + latePage.Outcome + " code=" + (latePage.ErrorCode ?? "(none)"));

            // A fresh unlock reuses the unchanged published generation: the
            // library answers again without an explicit rebuild, and the
            // explicit rebuild stays available as the recovery path.
            var unlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0, "The synthetic vault did not unlock again (stderr=" + unlock.StandardError.Trim() + ").");
            await session.CheckAsync().ConfigureAwait(false);
            Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
                "The console session did not observe the re-unlocked vault (" + session.ReportedVaultState + ").");
            var afterUnlock = await S5T3SearchAsync(session, new OwnerLibraryQuery(Text: "login", Limit: 10)).ConfigureAwait(false);
            Require(afterUnlock is { Total: 2 },
                "A search in the new unlocked epoch did not reuse the unchanged published index (" +
                S5T3Describe(afterUnlock) + "; message=" + (session.LibraryMessage ?? "none") + ").");
            await session.RebuildLibraryIndexAsync().ConfigureAwait(false);
            Require(session.LibraryIndex is { } materialized &&
                    materialized.RowCount == S5T3CorpusResources &&
                    !materialized.CanonicalMoved &&
                    !materialized.Stale,
                "The console's rebuild did not report the service's materialized generation (" + S5T3DescribeIndex(session.LibraryIndex) + ").");
            var afterRebuild = await S5T3SearchAsync(session, new OwnerLibraryQuery(Text: "login", Limit: 10)).ConfigureAwait(false);
            Require(afterRebuild is { Total: 2 },
                "Searching after the console rebuilt the index did not return the vault's matches (" + S5T3Describe(afterRebuild) + ").");
            Console.WriteLine("PASS a re-unlock reuses the unchanged index without a rebuild, and the explicit rebuild still works");

            // No plaintext private content is persisted, and the console's own log
            // carries no private body text.
            Require(!S5T3FixtureHoldsMarker(fixture), "A private body marker was found in plaintext under the fixture root.");
            Require(!session.Activity.Any(entry => entry.Contains(S5T3Marker, StringComparison.Ordinal)),
                "The console activity log carried private body text.");
            Console.WriteLine("PASS no plaintext private content was persisted, and the console log carries no private body text");

            var draining = service;
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            var serviceOutput = await draining!.StandardOutputDrain.ConfigureAwait(false);
            var serviceError = await draining.StandardErrorDrain.ConfigureAwait(false);
            Require(!serviceOutput.Contains(passphrase, StringComparison.Ordinal) && !serviceError.Contains(passphrase, StringComparison.Ordinal),
                "The service wrote the owner passphrase to its own output.");
            Console.WriteLine("PASS the service never wrote the owner passphrase to its output");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Creates the synthetic owner-console fixture at an explicit root for the
    /// external WPF surface proof. The caller owns that root: it starts and stops
    /// the service, provisions/unlocks through the console, writes the corpus
    /// through the owner CLI while the service holds the key, drives the console
    /// and removes the root afterwards, so this scenario disposes nothing and
    /// provisions no vault.
    /// </summary>
    internal static Task RunS5T3LibraryFixtureAsync(string fixtureRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S5-T3 owner console fixture requires Windows named pipes.");
        }

        var fixture = SmokeFixture.CreateUninitializedAt(fixtureRoot);
        Console.WriteLine("S5T3 CONSOLE FIXTURE root=" + fixture.Root);
        Console.WriteLine("S5T3 CONSOLE FIXTURE settings=" + fixture.SettingsPath);
        Console.WriteLine("S5T3 CONSOLE FIXTURE controlPipe=" + fixture.ControlPipe);
        Console.WriteLine("S5T3 CONSOLE FIXTURE agentPipe=" + fixture.AgentPipe);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes the S5-T3 synthetic corpus into an already unlocked console
    /// fixture through the real owner CLI. The caller runs this after the console
    /// provisioned the vault (the service then holds the key) and removes the
    /// fixture root afterwards.
    /// </summary>
    internal static async Task S5T3WriteConsoleCorpusAsync(string connectionsExecutable, string fixtureRoot)
    {
        var root = Path.GetFullPath(fixtureRoot);
        var settingsPath = Path.Combine(root, "service", "service-settings.json");
        using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var controlPipe = settings.RootElement.GetProperty("controlPipe").GetString()
            ?? throw new InvalidDataException("The console fixture settings carry no control pipe.");
        await S5T3WriteCorpusAsync(connectionsExecutable, controlPipe, Path.Combine(root, "output"), DateTimeOffset.UtcNow.AddMinutes(-5)).ConfigureAwait(false);
    }

    private static async Task S5T3WriteCorpusAsync(string executable, string controlPipe, string outputDirectory, DateTimeOffset now)
    {
        await WriteS5T3SourceAsync(executable, controlPipe, outputDirectory, S5T3SourceCurrent, S5T3ZoneAlpha,
            S5T3SourceDraft("Synthetic S5-T3 current registry title", "s5t3-synthetic-current-locator", "synthetic-v1", "available", now), 1).ConfigureAwait(false);
        await WriteS5T3SourceAsync(executable, controlPipe, outputDirectory, S5T3SourceMoves, S5T3ZoneAlpha,
            S5T3SourceDraft("Synthetic S5-T3 moving registry title", "s5t3-synthetic-moving-locator", "synthetic-v1", "available", now), 1).ConfigureAwait(false);
        await WriteS5T3ItemAsync(executable, controlPipe, outputDirectory, S5T3Login, S5T3ZoneAlpha,
            S5T3ItemDraft("lesson", "user", "directWrite", "unverified", "l1",
                "Synthetic S5-T3 login lesson",
                "S5-T3 synthetic body: " + S5T3LoginVietnamese + "; " + S5T3Marker,
                now.AddDays(-6), S5T3CurrentSourceAnchor()), 1).ConfigureAwait(false);
        await WriteS5T3ItemAsync(executable, controlPipe, outputDirectory, S5T3Deploy, S5T3ZoneAlpha,
            S5T3ItemDraft("procedure", "user", "directWrite", "unverified", "l2",
                "Synthetic S5-T3 trien khai procedure",
                "S5-T3 synthetic body: cac buoc trien khai ban moi len may chu; " + S5T3Marker,
                now.AddDays(-5)), 1).ConfigureAwait(false);
        await WriteS5T3ItemAsync(executable, controlPipe, outputDirectory, S5T3Symbol, S5T3ZoneAlpha,
            S5T3ItemDraft("lesson", "user", "directWrite", "unverified", "l1",
                "Synthetic S5-T3 redeemToken lesson",
                "S5-T3 synthetic body: the redeemToken round trip keeps the opaque bearer out of arguments; " + S5T3Marker,
                now.AddDays(-4)), 1).ConfigureAwait(false);
        await WriteS5T3ItemAsync(executable, controlPipe, outputDirectory, S5T3RankFix, S5T3ZoneAlpha,
            S5T3ItemDraft("lesson", "user", "directWrite", "unverified", "l1",
                "Synthetic S5-T3 repair runbook",
                "S5-T3 synthetic body: fix fix fix the broken pipeline with a fix checklist and fix verification; " + S5T3Marker,
                now.AddDays(-3)), 1).ConfigureAwait(false);
        await WriteS5T3ItemAsync(executable, controlPipe, outputDirectory, S5T3RankMention, S5T3ZoneAlpha,
            S5T3ItemDraft("lesson", "user", "directWrite", "unverified", "l1",
                "Synthetic S5-T3 pipeline note",
                "S5-T3 synthetic body: one short note mentioning a fix in passing among unrelated pipeline steps; " + S5T3Marker,
                now.AddDays(-3).AddMinutes(1)), 1).ConfigureAwait(false);
        await WriteS5T3ItemAsync(executable, controlPipe, outputDirectory, S5T3UnknownSource, S5T3ZoneAlpha,
            S5T3ItemDraft("lesson", "user", "directWrite", "reportedSource", "l1",
                "Synthetic S5-T3 unregistered source lesson",
                "S5-T3 synthetic body: an anchor that names no registered revision; " + S5T3Marker,
                now.AddDays(-2), S5T3UnknownSourceAnchor()), 1).ConfigureAwait(false);
        await WriteS5T3ItemAsync(executable, controlPipe, outputDirectory, S5T3Superseded, S5T3ZoneAlpha,
            S5T3ItemDraft("lesson", "user", "directWrite", "unverified", "l1",
                "Synthetic S5-T3 moving anchor lesson",
                "S5-T3 synthetic body: an anchor pinned at the first revision of a source that moves; " + S5T3Marker,
                now.AddDays(-2).AddMinutes(1),
                new object[]
                {
                    new
                    {
                        sourceId = S5T3SourceMoves,
                        locator = (string?)null,
                        availability = "available",
                        sourceRevision = 1L,
                        span = new { start = 0L, end = 32L }
                    }
                }), 1).ConfigureAwait(false);
        await WriteS5T3ItemAsync(executable, controlPipe, outputDirectory, S5T3Assistant, S5T3ZoneAlpha,
            S5T3ItemDraft("synthesis", "assistant", "synthesis", "unverified", "l1",
                "Synthetic S5-T3 assistant synthesis",
                "S5-T3 synthetic body: a synthesis an assistant wrote, kept honestly as assistant-authored; " + S5T3Marker,
                now.AddDays(-1)), 1).ConfigureAwait(false);
        await WriteS5T3ItemAsync(executable, controlPipe, outputDirectory, S5T3Retired, S5T3ZoneAlpha,
            S5T3ItemDraft("lesson", "user", "directWrite", "unverified", "l2",
                "Synthetic S5-T3 retired rollout lesson",
                "S5-T3 synthetic body: the retired rollout playbook from two quarters ago; " + S5T3Marker,
                now.AddDays(-90)), 1).ConfigureAwait(false);
        await WriteS5T3ItemAsync(executable, controlPipe, outputDirectory, S5T3BetaDoc, S5T3ZoneBeta,
            S5T3ItemDraft("lesson", "user", "directWrite", "unverified", "l1",
                "Synthetic S5-T3 beta zone lesson",
                "S5-T3 synthetic body: login procedures kept in the beta zone; " + S5T3Marker,
                now.AddDays(-30)), 1).ConfigureAwait(false);

        var collection = await RunOwnerCommandWithInputAsync(executable,
            "SET" + Environment.NewLine,
            "owner", "collection-set", "--control-pipe", controlPipe,
            "--collection-id", S5T3Collection, "--resource-ids", S5T3Deploy + "," + S5T3Retired).ConfigureAwait(false);
        Require(collection.ExitCode == 0 && collection.StandardOutput.Contains("COLLECTION set id=" + S5T3Collection, StringComparison.Ordinal),
            "The real owner CLI did not save the synthetic owner collection (exit=" + collection.ExitCode + "; stderr=" + collection.StandardError.Trim() + ").");
    }

    private static async Task<OwnerLibraryPage?> S5T3SearchAsync(OwnerConsoleSession session, OwnerLibraryQuery query)
    {
        session.SetLibraryQuery(query);
        await session.SearchLibraryAsync().ConfigureAwait(false);
        var page = session.LibraryPage;
        Console.WriteLine("S5T3-PROBE search text=" + (query.Text ?? "(browse)") + " zone=" + (query.ZoneId ?? "-") +
            " collection=" + (query.CollectionId ?? "-") + " kind=" + (query.Kind ?? "-") + " level=" + (query.Level ?? "-") +
            " current=" + query.Current + " from=" + (query.FromUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "-") +
            " to=" + (query.ToUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "-") +
            " -> " + (page is null
                ? "no page (" + (session.LibraryMessage ?? "no message") + ")"
                : "total=" + page.Total + " hits=" + string.Join(",", page.Hits.Select(hit => hit.Id))));
        return page;
    }

    private static bool S5T3HasHit(OwnerLibraryPage? page, string resourceId) =>
        page is not null && page.Hits.Any(hit => string.Equals(hit.Id, resourceId, StringComparison.Ordinal));

    private static string S5T3Describe(OwnerLibraryPage? page) =>
        page is null
            ? "no page"
            : "total=" + page.Total + " hits=" + string.Join(",", page.Hits.Select(hit => hit.Id + "@" + hit.Revision));

    private static string S5T3DescribeDetail(OwnerItemDetail? item) =>
        item is null
            ? "no item"
            : "id=" + item.Id + " rev=" + item.Revision + " kind=" + item.Kind + " level=" + item.Level + " author=" + item.Author +
                " evidence=" + item.Evidence + " title=" + (item.Title ?? "(none)") + " citations=[" + S5T3DescribeCitations(item) + "]";

    private static string S5T3DescribeCitations(OwnerItemDetail item) =>
        string.Join(";", item.Citations.Select(citation =>
            (citation.SourceId ?? "(none)") + "@" + (citation.SourceRevision?.ToString(CultureInfo.InvariantCulture) ?? "-") +
            "=" + citation.Status + (citation.Warning is null ? string.Empty : "(" + citation.Warning + ")")));

    private static string S5T3DescribeSource(OwnerSourceDetail? source) =>
        source is null ? "no source" : "id=" + source.Id + " rev=" + source.Revision + " availability=" + source.Availability;

    private static string S5T3DescribeIndex(OwnerIndexReport? index) =>
        index is null ? "no index" : "schema=" + index.SchemaVersion + " rows=" + index.RowCount + " stale=" + index.Stale;

    private static async Task WriteS5T3ItemAsync(
        string executable,
        string controlPipe,
        string outputDirectory,
        string resourceId,
        string zoneId,
        string draftJson,
        long expectedRevision)
    {
        var input = Path.Combine(outputDirectory, "s5t3-item-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", controlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("MEMORY stored revision=" + expectedRevision.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            "Memory create did not store " + resourceId + " at revision " + expectedRevision.ToString(CultureInfo.InvariantCulture) +
            " (exit=" + result.ExitCode + "; stdout=" + result.StandardOutput.Trim() + "; stderr=" + result.StandardError.Trim() + ").");
        File.Delete(input);
    }

    private static async Task WriteS5T3SourceAsync(
        string executable,
        string controlPipe,
        string outputDirectory,
        string resourceId,
        string zoneId,
        string draftJson,
        long expectedRevision)
    {
        var input = Path.Combine(outputDirectory, "s5t3-source-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "source-create", "--control-pipe", controlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("SOURCE stored revision=" + expectedRevision.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            "Source create did not store " + resourceId + " at revision " + expectedRevision.ToString(CultureInfo.InvariantCulture) +
            " (exit=" + result.ExitCode + "; stdout=" + result.StandardOutput.Trim() + "; stderr=" + result.StandardError.Trim() + ").");
        File.Delete(input);
    }

    private static async Task<string> S5T3WriteDraftAsync(string outputDirectory, string draftJson)
    {
        var input = Path.Combine(outputDirectory, "s5t3-draft-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return input;
    }

    private static bool S5T3FixtureHoldsMarker(SmokeFixture fixture)
    {
        foreach (var path in Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories))
        {
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            if (text.Contains(S5T3Marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
