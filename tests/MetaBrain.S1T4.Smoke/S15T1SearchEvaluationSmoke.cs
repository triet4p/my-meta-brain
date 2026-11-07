using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private sealed record S15T1Corpus(
        string DatasetId,
        bool SyntheticOnly,
        string AllowedFolderName,
        string OutsideFolderName,
        List<S15T1CorpusFile> Files)
    {
        public int DistractorFileCount { get; init; }
        public int DistractorBytesPerFile { get; init; }
        public List<S15T1AgentScopeItem>? AgentScopeItems { get; init; }
    }

    private sealed record S15T1CorpusFile(
        string Key,
        string Folder,
        string Name,
        string Revision1Content,
        string? Revision2Content);

    private sealed record S15T1QuerySet(
        string DatasetId,
        int DevelopmentCount,
        int HeldOutCount,
        List<S15T1Query> Queries)
    {
        public List<S15T1AgentScopeQuery>? AgentScopeQueries { get; init; }
    }
    private sealed record S15T1Query(
        string Id,
        string Split,
        string Family,
        string Query,
        string Scope,
        string Baseline,
        List<S15T1ExpectedEvidence> ExpectedEvidence,
        string? WideExpectedPath);

    private sealed record S15T1ExpectedEvidence(string Path, long Revision, string Anchor);
    private sealed record S15T1AgentScopeItem(
        string ResourceId,
        string Zone,
        string Title,
        string Body,
        bool Granted);

    private sealed record S15T1AgentScopeQuery(
        string Id,
        string Scope,
        string Query,
        string ExpectedResult,
        string? ExpectedResourceId,
        string? ExpectedEvidenceAnchor,
        string? ForbiddenResourceId,
        string? ForbiddenBodyMarker);

    private sealed record S15T1ImportedFile(
        S15T1CorpusFile CorpusFile,
        string FileId,
        string Path,
        long CurrentRevision,
        string CurrentContent);

    private static readonly JsonSerializerOptions S15T1JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task RunS15T1SearchEvaluationAsync(string serviceExecutable)
    {

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S15-T1 CLI/service search evaluation is Windows-only.");
        }

        var executable = Path.Combine(AppContext.BaseDirectory, "MetaBrain.Cli.exe");
        Require(File.Exists(executable), "The packaged MetaBrain.Cli.exe was not present in the smoke output.");

        var root = SmokeFixture.FindRepositoryRoot();
        var fixtureDirectory = Path.Combine(root, "tests", "MetaBrain.S1T4.Smoke", "Fixtures", "S15T1", "lexical-v2");
        var corpusPath = Path.Combine(fixtureDirectory, "corpus.json");
        var queryPath = Path.Combine(fixtureDirectory, "queries.json");
        var corpusBytes = await File.ReadAllBytesAsync(corpusPath).ConfigureAwait(false);
        var queryBytes = await File.ReadAllBytesAsync(queryPath).ConfigureAwait(false);
        var corpusSha256 = Convert.ToHexString(SHA256.HashData(corpusBytes)).ToLowerInvariant();
        var querySha256 = Convert.ToHexString(SHA256.HashData(queryBytes)).ToLowerInvariant();
        Console.WriteLine(
            "S15-T1 source fixtures dataset=lexical-v2 corpus=" + corpusPath +
            " sha256=" + corpusSha256 +
            " queries=" + queryPath +
            " sha256=" + querySha256);
        var corpus = JsonSerializer.Deserialize<S15T1Corpus>(corpusBytes, S15T1JsonOptions)
            ?? throw new InvalidDataException("The S15-T1 synthetic corpus could not be read.");
        var querySet = JsonSerializer.Deserialize<S15T1QuerySet>(queryBytes, S15T1JsonOptions)
            ?? throw new InvalidDataException("The S15-T1 frozen query set could not be read.");

        Require(corpus.SyntheticOnly, "The S15-T1 corpus must remain synthetic-only.");
        Require(string.Equals(corpus.DatasetId, querySet.DatasetId, StringComparison.Ordinal),
            "The S15-T1 corpus and query-set dataset IDs do not match.");
        Require(querySet.Queries.Count == querySet.DevelopmentCount + querySet.HeldOutCount,
            "The S15-T1 query counts do not match the frozen split counts.");
        Require(querySet.Queries.Count(query => string.Equals(query.Split, "development", StringComparison.Ordinal)) == querySet.DevelopmentCount &&
                querySet.Queries.Count(query => string.Equals(query.Split, "held-out", StringComparison.Ordinal)) == querySet.HeldOutCount,
            "The S15-T1 development and held-out split sizes changed.");

        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        try
        {
            service = await StartServiceAsync(serviceExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var passphrase = CreateSyntheticPassphrase();
            var provision = await RunOwnerCommandWithInputAsync(
                executable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The owner CLI did not provision the synthetic S15-T1 vault (stderr=" + provision.StandardError.Trim() + ").");

            var unlock = await RunOwnerCommandWithInputAsync(
                executable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0 && unlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S15-T1 vault did not unlock for scoped-memory evaluation (stderr=" + unlock.StandardError.Trim() + ").");

            var allowedFolder = await CreateFolderAsync(
                executable, fixture, "root", corpus.AllowedFolderName, 0).ConfigureAwait(false);
            var outsideFolder = await CreateFolderAsync(
                executable, fixture, "root", corpus.OutsideFolderName, allowedFolder.TreeRevision).ConfigureAwait(false);

            var folderIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [corpus.AllowedFolderName] = allowedFolder.FolderId,
                [corpus.OutsideFolderName] = outsideFolder.FolderId,
            };
            var treeRevision = outsideFolder.TreeRevision;
            var imported = new Dictionary<string, S15T1ImportedFile>(StringComparer.Ordinal);

            foreach (var corpusFile in corpus.Files)
            {
                Require(folderIds.TryGetValue(corpusFile.Folder, out var folderId),
                    "A corpus file names an unknown synthetic folder: " + corpusFile.Folder);
                var revision1 = Encoding.UTF8.GetBytes(corpusFile.Revision1Content);
                var inputPath = await WriteWorkspaceInputAsync(
                    fixture, "s15t1-" + corpusFile.Key + "-revision-1.txt", revision1).ConfigureAwait(false);
                var import = await ImportWorkspaceFileAsync(
                    executable,
                    fixture,
                    folderId!,
                    corpusFile.Name,
                    inputPath,
                    treeRevision).ConfigureAwait(false);
                treeRevision = import.TreeRevision;

                var currentContent = corpusFile.Revision1Content;
                long currentRevision = 1;
                if (corpusFile.Revision2Content is { } revision2Content)
                {
                    var revision2Input = await WriteWorkspaceInputAsync(
                        fixture,
                        "s15t1-" + corpusFile.Key + "-revision-2.txt",
                        Encoding.UTF8.GetBytes(revision2Content)).ConfigureAwait(false);
                    var revise = await RunOwnerCommandAsync(
                        executable,
                        "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                        "--file-id", import.FileId,
                        "--expected-content-revision", "1",
                        "--input-file", revision2Input).ConfigureAwait(false);
                    Require(revise.ExitCode == 0 && revise.StandardOutput.Contains("contentRevision=2", StringComparison.Ordinal),
                        "The synthetic temporal file did not advance to revision 2 (stderr=" + revise.StandardError.Trim() + ").");
                    treeRevision = WorkspaceNumber(revise.StandardOutput, "revision");
                    currentContent = revision2Content;
                    currentRevision = 2;
                }

                var path = corpusFile.Folder + "/" + corpusFile.Name;
                Require(imported.TryAdd(corpusFile.Key, new S15T1ImportedFile(
                    corpusFile, import.FileId, path, currentRevision, currentContent)),
                    "The S15-T1 corpus repeats a file key: " + corpusFile.Key);
            }
            for (var index = 1; index <= corpus.DistractorFileCount; index++)
            {
                var fileName = "distractor-" + index.ToString("D3", CultureInfo.InvariantCulture) + ".txt";
                var content = CreateDistractorBytes(index, corpus.DistractorBytesPerFile);
                var inputPath = await WriteWorkspaceInputAsync(
                    fixture, "s15t1-" + fileName, content).ConfigureAwait(false);
                var import = await ImportWorkspaceFileAsync(
                    executable,
                    fixture,
                    allowedFolder.FolderId,
                    fileName,
                    inputPath,
                    treeRevision).ConfigureAwait(false);
                treeRevision = import.TreeRevision;
            }

            var byPath = imported.Values.ToDictionary(file => file.Path, StringComparer.Ordinal);
            foreach (var query in querySet.Queries)
            {
                foreach (var expected in query.ExpectedEvidence)
                {
                    Require(byPath.TryGetValue(expected.Path, out var evidenceFile),
                        "A query label refers to an absent corpus path: " + expected.Path);
                    Require(expected.Revision == evidenceFile!.CurrentRevision,
                        "A query label refers to a non-current fixture revision: " + query.Id);
                    Require(evidenceFile.CurrentContent.Contains(expected.Anchor, StringComparison.Ordinal),
                        "A labeled evidence anchor is absent from its current synthetic source: " + query.Id);
                }
            }

            var allowedWitness = imported.Values.First(file => file.CorpusFile.Folder == corpus.AllowedFolderName);
            await VerifyAgentChannelCannotReachWorkspaceAsync(fixture, allowedWitness.FileId).ConfigureAwait(false);

            var expectedEvidenceReturned = 0;
            var literalHitQueries = 0;
            var lexicalMissQueries = 0;
            var semanticMissQueries = 0;
            var scopeNegativeQueries = 0;
            foreach (var query in querySet.Queries)
            {
                var folderFilter = query.Scope switch
                {
                    "inside" => allowedFolder.FolderId,
                    "all" => null,
                    _ => throw new InvalidDataException("The S15-T1 query has an unknown scope: " + query.Scope),
                };
                var result = await SearchJsonAsync(executable, fixture, query.Query, folderFilter).ConfigureAwait(false);
                var hits = result.GetProperty("hits").EnumerateArray().ToArray();
                var total = result.GetProperty("total").GetInt32();
                Require(total == hits.Length, "The S15-T1 result total did not match its returned hit list: " + query.Id);

                foreach (var hit in hits)
                {
                    await VerifyHitSourceAsync(executable, fixture, query, hit, byPath).ConfigureAwait(false);
                }

                var matchedExpected = query.ExpectedEvidence.Any(expected =>
                    hits.Any(hit => string.Equals(
                        hit.GetProperty("path").GetString(), expected.Path, StringComparison.Ordinal) &&
                        hit.GetProperty("contentRevision").GetInt64() == expected.Revision));

                switch (query.Baseline)
                {
                    case "hit":
                        Require(matchedExpected,
                            "The exact lexical baseline missed the expected source for " + query.Id + " (query=" + query.Query + ").");
                        literalHitQueries++;
                        if (matchedExpected)
                        {
                            expectedEvidenceReturned++;
                        }
                        break;
                    case "miss":
                        Require(total == 0,
                            "The frozen lexical miss unexpectedly returned a hit for " + query.Id + " (query=" + query.Query + ").");
                        lexicalMissQueries++;
                        if (query.ExpectedEvidence.Count > 0)
                        {
                            semanticMissQueries++;
                        }
                        break;
                    case "scope-negative":
                        Require(total == 0 && hits.Length == 0,
                            "A filtered scope-negative query returned outside-folder output for " + query.Id + ".");
                        Require(query.WideExpectedPath is not null,
                            "A scope-negative query is missing its unfiltered synthetic witness.");
                        var wide = await SearchJsonAsync(executable, fixture, query.Query, null).ConfigureAwait(false);
                        var wideHits = wide.GetProperty("hits").EnumerateArray().ToArray();
                        Require(wide.GetProperty("total").GetInt32() == wideHits.Length &&
                                wideHits.Length == 1 &&
                                string.Equals(wideHits[0].GetProperty("path").GetString(), query.WideExpectedPath, StringComparison.Ordinal),
                            "The unfiltered owner query did not return exactly the expected synthetic outside-folder witness for " + query.Id + ".");
                        await VerifyHitSourceAsync(executable, fixture, query, wideHits[0], byPath).ConfigureAwait(false);
                        scopeNegativeQueries++;
                        break;
                    default:
                        throw new InvalidDataException("The S15-T1 query has an unknown baseline expectation: " + query.Baseline);
                }

                var observed = hits.Length == 0
                    ? "miss"
                    : string.Join(",", hits.Select((hit, index) =>
                        hit.GetProperty("path").GetString() + "@" +
                        hit.GetProperty("contentRevision").GetInt64().ToString(CultureInfo.InvariantCulture) +
                        ":" + hit.GetProperty("field").GetString() + ":rank" +
                        (index + 1).ToString(CultureInfo.InvariantCulture)));
                Console.WriteLine(
                    "BASELINE id=" + query.Id +
                    " split=" + query.Split +
                    " family=" + query.Family +
                    " expected=" + query.Baseline +
                    " evidenceReturned=" + (matchedExpected ? "yes" : "no") +
                    " total=" + total.ToString(CultureInfo.InvariantCulture) +
                    " observed=" + observed);
            }

            Console.WriteLine(
                "PASS S15-T1 frozen current CLI/service lexical baseline: dataset=lexical-v2" +
                " files=" + (corpus.Files.Count + corpus.DistractorFileCount).ToString(CultureInfo.InvariantCulture) +
                " queries=" + querySet.Queries.Count.ToString(CultureInfo.InvariantCulture) +
                " development=" + querySet.DevelopmentCount.ToString(CultureInfo.InvariantCulture) +
                " heldOut=" + querySet.HeldOutCount.ToString(CultureInfo.InvariantCulture) +
                " literalHits=" + literalHitQueries.ToString(CultureInfo.InvariantCulture) +
                " lexicalMisses=" + lexicalMissQueries.ToString(CultureInfo.InvariantCulture) +
                " labeledParaphraseMisses=" + semanticMissQueries.ToString(CultureInfo.InvariantCulture) +
                " expectedEvidenceReturnedByLiteral=" + expectedEvidenceReturned.ToString(CultureInfo.InvariantCulture) +
                " scopeNegatives=" + scopeNegativeQueries.ToString(CultureInfo.InvariantCulture) +
                " agentScopeQueries=" + (querySet.AgentScopeQueries?.Count ?? 0).ToString(CultureInfo.InvariantCulture) +
                "; no quality/resource threshold or owner acceptance is implied");
            var currentCorpusBytes = corpus.Files.Sum(file =>
                (long)Encoding.UTF8.GetByteCount(file.Revision2Content ?? file.Revision1Content)) +
                (long)corpus.DistractorFileCount * corpus.DistractorBytesPerFile;
            await MeasureS15T1LatencyAndMemoryAsync(
                executable,
                fixture,
                service!,
                querySet,
                allowedFolder.FolderId,
                corpus.Files.Count + corpus.DistractorFileCount,
                currentCorpusBytes).ConfigureAwait(false);

            var agentScopeItems = corpus.AgentScopeItems
                ?? throw new InvalidDataException("The S15-T1 v2 corpus is missing its frozen agent-scope items.");
            var agentScopeQueries = querySet.AgentScopeQueries
                ?? throw new InvalidDataException("The S15-T1 v2 query set is missing its frozen agent-scope checks.");
            await RunS15T1AgentScopeSmokeAsync(
                executable, serviceExecutable, fixture, agentScopeItems, agentScopeQueries).ConfigureAwait(false);
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static async Task MeasureS15T1LatencyAndMemoryAsync(
        string executable,
        SmokeFixture fixture,
        RunningService service,
        S15T1QuerySet querySet,
        string allowedFolderId,
        int currentFileCount,
        long currentCorpusBytes)
    {
        const int warmupCount = 10;
        const int timedQueryCount = 100;
        var latenciesMs = new double[timedQueryCount];
        var baselineProcess = service.Process;
        baselineProcess.Refresh();
        var privateBytesBefore = baselineProcess.PrivateMemorySize64;
        var workingSetBefore = baselineProcess.WorkingSet64;
        var peakWorkingSetBefore = baselineProcess.PeakWorkingSet64;
        var maxPrivateBytesAtBoundaries = privateBytesBefore;
        var maxWorkingSetAtBoundaries = workingSetBefore;

        void SampleServiceMemory()
        {
            baselineProcess.Refresh();
            maxPrivateBytesAtBoundaries = Math.Max(maxPrivateBytesAtBoundaries, baselineProcess.PrivateMemorySize64);
            maxWorkingSetAtBoundaries = Math.Max(maxWorkingSetAtBoundaries, baselineProcess.WorkingSet64);
        }

        for (var index = 0; index < warmupCount; index++)
        {
            var query = querySet.Queries[index % querySet.Queries.Count];
            var folderId = query.Scope == "inside" ? allowedFolderId : null;
            var result = await SearchJsonAsync(executable, fixture, query.Query, folderId).ConfigureAwait(false);
            Require(result.GetProperty("total").GetInt32() == result.GetProperty("hits").GetArrayLength(),
                "An S15-T1 warm-up search returned an inconsistent result total.");
            SampleServiceMemory();
        }

        for (var index = 0; index < timedQueryCount; index++)
        {
            var query = querySet.Queries[index % querySet.Queries.Count];
            var folderId = query.Scope == "inside" ? allowedFolderId : null;
            var start = Stopwatch.GetTimestamp();
            var result = await SearchJsonAsync(executable, fixture, query.Query, folderId).ConfigureAwait(false);
            latenciesMs[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Require(result.GetProperty("total").GetInt32() == result.GetProperty("hits").GetArrayLength(),
                "An S15-T1 timed search returned an inconsistent result total.");
            SampleServiceMemory();
        }

        baselineProcess.Refresh();
        var privateBytesAfter = baselineProcess.PrivateMemorySize64;
        var workingSetAfter = baselineProcess.WorkingSet64;
        var peakWorkingSetAfter = baselineProcess.PeakWorkingSet64;
        Array.Sort(latenciesMs);
        var p50 = latenciesMs[(int)Math.Ceiling(0.50 * timedQueryCount) - 1];
        var p95 = latenciesMs[(int)Math.Ceiling(0.95 * timedQueryCount) - 1];
        Console.WriteLine(
            "RESOURCE-BASELINE dataset=lexical-v2 surface=owner-workspace-search-cli-roundtrip" +
            " files=" + currentFileCount.ToString(CultureInfo.InvariantCulture) +
            " currentCorpusBytes=" + currentCorpusBytes.ToString(CultureInfo.InvariantCulture) +
            " warmup=" + warmupCount.ToString(CultureInfo.InvariantCulture) +
            " timed=" + timedQueryCount.ToString(CultureInfo.InvariantCulture) +
            " latencyMsP50=" + p50.ToString("F2", CultureInfo.InvariantCulture) +
            " latencyMsP95=" + p95.ToString("F2", CultureInfo.InvariantCulture) +
            " servicePrivateMiBBefore=" + (privateBytesBefore / (1024d * 1024d)).ToString("F2", CultureInfo.InvariantCulture) +
            " servicePrivateMiBMaxAtBoundaries=" + (maxPrivateBytesAtBoundaries / (1024d * 1024d)).ToString("F2", CultureInfo.InvariantCulture) +
            " servicePrivateMiBAfter=" + (privateBytesAfter / (1024d * 1024d)).ToString("F2", CultureInfo.InvariantCulture) +
            " serviceWorkingSetMiBBefore=" + (workingSetBefore / (1024d * 1024d)).ToString("F2", CultureInfo.InvariantCulture) +
            " serviceWorkingSetMiBMaxAtBoundaries=" + (maxWorkingSetAtBoundaries / (1024d * 1024d)).ToString("F2", CultureInfo.InvariantCulture) +
            " serviceWorkingSetMiBAfter=" + (workingSetAfter / (1024d * 1024d)).ToString("F2", CultureInfo.InvariantCulture) +
            " serviceLifetimePeakWorkingSetMiBBefore=" + (peakWorkingSetBefore / (1024d * 1024d)).ToString("F2", CultureInfo.InvariantCulture) +
            " serviceLifetimePeakWorkingSetMiBAfter=" + (peakWorkingSetAfter / (1024d * 1024d)).ToString("F2", CultureInfo.InvariantCulture) +
            "; boundary samples exclude transient CLI-process/native peaks and larger-corpus extrapolation");
    }

    private static async Task RunS15T1AgentScopeSmokeAsync(
        string executable,
        string bridgeExecutable,
        SmokeFixture fixture,
        IReadOnlyList<S15T1AgentScopeItem> items,
        IReadOnlyList<S15T1AgentScopeQuery> scopeQueries)
    {
        Require(items.Count == 3 && scopeQueries.Count == 3,
            "The frozen S15-T1 agent-scope set must contain three source items and three queries.");
        Require(items.Select(item => item.ResourceId).Distinct(StringComparer.Ordinal).Count() == items.Count,
            "The S15-T1 agent-scope corpus repeats a memory resource ID.");
        var grantedIds = items.Where(item => item.Granted).Select(item => item.ResourceId).ToArray();
        Require(grantedIds.Length == 1, "The S15-T1 agent-scope fixture must grant exactly one synthetic item.");
        var itemsById = items.ToDictionary(item => item.ResourceId, StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var zoneId = item.Zone switch
            {
                "alpha" => S4T2ZoneAlpha,
                "beta" => S4T2ZoneBeta,
                _ => throw new InvalidDataException("An S15-T1 agent-scope item names an unknown synthetic zone: " + item.Zone),
            };
            await WriteS4T2ItemAsync(executable, fixture, item.ResourceId, zoneId,
                S4T2ItemDraft("lesson", "user", "directWrite", "unverified",
                    item.Title, item.Body, now.AddMinutes(-index - 1)), 1).ConfigureAwait(false);
        }

        var rebuild = await RunOwnerCommandAsync(executable,
            "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(rebuild.ExitCode == 0 && rebuild.StandardOutput.Contains("INDEX rebuilt", StringComparison.Ordinal) &&
                rebuild.StandardOutput.Contains("rows=" + items.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            "The S15-T1 scoped synthetic memory index did not rebuild all frozen rows (stdout=" + rebuild.StandardOutput.Trim() + "; stderr=" + rebuild.StandardError.Trim() + ").");
        var session = await IssueS4T2GrantAsync(
            executable, fixture, "s15t1-agent-search", "memory.search", grantedIds).ConfigureAwait(false);

        var grantedItem = items.Single(item => item.Granted);
        var grantedQueries = 0;
        var deniedQueries = 0;
        foreach (var query in scopeQueries)
        {
            if (string.Equals(query.ExpectedResult, "hit", StringComparison.Ordinal))
            {
                var expectedResourceId = query.ExpectedResourceId;
                var expectedEvidenceAnchor = query.ExpectedEvidenceAnchor;
                if (query.Scope != "granted" ||
                    expectedResourceId is null ||
                    expectedEvidenceAnchor is null ||
                    !itemsById.TryGetValue(expectedResourceId, out var expectedItem) ||
                    !expectedItem.Granted ||
                    !expectedItem.Body.Contains(query.Query, StringComparison.Ordinal) ||
                    !expectedItem.Body.Contains(expectedEvidenceAnchor, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The frozen S15-T1 granted query lacks its approved synthetic source evidence: " + query.Id);
                }

                var ownerSearch = await AgentS4T2SearchAsync(executable, fixture, session, query.Query).ConfigureAwait(false);
                Require(ownerSearch.ExitCode == 0 &&
                        ownerSearch.StandardOutput.Contains("HIT id=" + expectedResourceId, StringComparison.Ordinal),
                    "The approved synthetic memory was not searchable through owner agent-search for " + query.Id + ".");
                var bridgeSearch = await RunBridgeToolAsync(bridgeExecutable, fixture.AgentPipe,
                    "metabrain_memory_search", new { sessionFile = session.SessionFile, query = query.Query }).ConfigureAwait(false);
                Require(bridgeSearch.Contains(expectedResourceId, StringComparison.Ordinal) &&
                        !bridgeSearch.Contains("mb1_", StringComparison.Ordinal) &&
                        !bridgeSearch.Contains("mbs1_", StringComparison.Ordinal),
                    "The MCP memory-search tool did not return the approved hit without bearer material for " + query.Id + ".");
                grantedQueries++;
                continue;
            }

            var forbiddenResourceId = query.ForbiddenResourceId;
            var forbiddenBodyMarker = query.ForbiddenBodyMarker;
            if (!string.Equals(query.ExpectedResult, "no-match", StringComparison.Ordinal) ||
                query.ExpectedResourceId is not null ||
                forbiddenResourceId is null ||
                forbiddenBodyMarker is null ||
                !itemsById.TryGetValue(forbiddenResourceId, out var forbiddenItem) ||
                forbiddenItem.Granted ||
                !forbiddenItem.Body.Contains(query.Query, StringComparison.Ordinal) ||
                !forbiddenItem.Body.Contains(forbiddenBodyMarker, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The frozen S15-T1 out-of-scope query lacks its synthetic witness: " + query.Id);
            }


            var expectedScope = query.Scope switch
            {
                "same-zone-ungranted" when string.Equals(forbiddenItem.Zone, grantedItem.Zone, StringComparison.Ordinal) => true,
                "different-zone-ungranted" when !string.Equals(forbiddenItem.Zone, grantedItem.Zone, StringComparison.Ordinal) => true,
                _ => false,
            };
            Require(expectedScope, "The S15-T1 out-of-scope query's zone does not match its frozen scope: " + query.Id);
            var ownerDenied = await AgentS4T2SearchAsync(executable, fixture, session, query.Query).ConfigureAwait(false);
            Require(ownerDenied.ExitCode == 0 &&
                    ownerDenied.StandardOutput.Contains("SEARCH no_match", StringComparison.Ordinal) &&
                    !ownerDenied.StandardOutput.Contains("HIT id=", StringComparison.Ordinal) &&
                    !ownerDenied.StandardOutput.Contains(forbiddenResourceId, StringComparison.Ordinal) &&
                    !ownerDenied.StandardOutput.Contains(forbiddenBodyMarker, StringComparison.Ordinal),
                "The frozen owner agent-search scope disclosed an ungranted memory for " + query.Id + ".");
            var bridgeDenied = await RunBridgeToolAsync(bridgeExecutable, fixture.AgentPipe,
                "metabrain_memory_search", new { sessionFile = session.SessionFile, query = query.Query }).ConfigureAwait(false);
            Require(bridgeDenied.Contains("SEARCH no_match", StringComparison.Ordinal) &&
                    !bridgeDenied.Contains(forbiddenResourceId, StringComparison.Ordinal) &&
                    !bridgeDenied.Contains(forbiddenBodyMarker, StringComparison.Ordinal) &&
                    !bridgeDenied.Contains("mb1_", StringComparison.Ordinal) &&
                    !bridgeDenied.Contains("mbs1_", StringComparison.Ordinal),
                "The MCP memory-search tool disclosed an ungranted memory for " + query.Id + ".");
            deniedQueries++;
        }

        Require(grantedQueries == 1 && deniedQueries == 2,
            "The frozen S15-T1 agent-scope set must exercise one granted hit and two denied negatives.");
        var bridgeList = await RunBridgeToolsListAsync(bridgeExecutable, fixture.AgentPipe).ConfigureAwait(false);
        Require(bridgeList.Contains("metabrain_memory_search", StringComparison.Ordinal),
            "The real MCP bridge did not advertise scoped memory search.");
        Console.WriteLine(
            "PASS S15-T1 frozen-grant owner agent-search and MCP memory-search: queries=" +
            scopeQueries.Count.ToString(CultureInfo.InvariantCulture) +
            " granted=" + grantedQueries.ToString(CultureInfo.InvariantCulture) +
            " outOfScope=" + deniedQueries.ToString(CultureInfo.InvariantCulture) +
            " same-zone-and-other-zone negatives disclose no IDs or bodies");
    }

    private static byte[] CreateDistractorBytes(int index, int length)
    {
        if (index <= 0 || length <= 0)
        {
            throw new ArgumentOutOfRangeException(index <= 0 ? nameof(index) : nameof(length));
        }

        var token = Encoding.ASCII.GetBytes("qzxvnoise" + index.ToString("D3", CultureInfo.InvariantCulture) + " ");
        var content = new byte[length];
        for (var offset = 0; offset < content.Length;)
        {
            var copyLength = Math.Min(token.Length, content.Length - offset);
            token.AsSpan(0, copyLength).CopyTo(content.AsSpan(offset));
            offset += copyLength;
        }

        return content;
    }

    private static async Task<(string FolderId, long TreeRevision)> CreateFolderAsync(
        string executable,
        SmokeFixture fixture,
        string parentId,
        string name,
        long treeRevision)
    {
        var result = await RunOwnerCommandAsync(
            executable,
            "owner", "workspace-mkdir", "--control-pipe", fixture.ControlPipe,
            "--parent-id", parentId,
            "--name", name,
            "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        Require(result.ExitCode == 0, "The synthetic workspace folder could not be created: " + name + ".");
        return (WorkspaceToken(result.StandardOutput, "id"), WorkspaceNumber(result.StandardOutput, "revision"));
    }

    private static async Task<JsonElement> SearchJsonAsync(
        string executable,
        SmokeFixture fixture,
        string query,
        string? folderId)
    {
        var arguments = new List<string>
        {
            "owner", "workspace-search", "--control-pipe", fixture.ControlPipe,
            "--query", query, "--format", "json",
        };
        if (folderId is not null)
        {
            arguments.Add("--folder-id");
            arguments.Add(folderId);
        }

        var result = await RunOwnerCommandAsync(executable, arguments.ToArray()).ConfigureAwait(false);
        Require(result.ExitCode == 0,
            "The actual owner CLI workspace search failed for a frozen query (stderr=" + result.StandardError.Trim() + ").");
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.Clone();
    }

    private static async Task VerifyHitSourceAsync(
        string executable,
        SmokeFixture fixture,
        S15T1Query query,
        JsonElement hit,
        IReadOnlyDictionary<string, S15T1ImportedFile> filesByPath)
    {
        var path = hit.GetProperty("path").GetString()
            ?? throw new InvalidDataException("A workspace search hit has no path.");
        Require(filesByPath.TryGetValue(path, out var source), "The CLI returned a path outside the synthetic corpus: " + path);
        Require(string.Equals(hit.GetProperty("field").GetString(), "content", StringComparison.Ordinal),
            "The frozen S15-T1 queries should return source-content hits, not metadata-only matches.");
        var revision = hit.GetProperty("contentRevision").GetInt64();
        Require(revision == source!.CurrentRevision,
            "The CLI returned a non-current revision for " + query.Id + " at " + path + ".");
        if (!hit.TryGetProperty("byteOffset", out var offsetValue) || offsetValue.ValueKind != JsonValueKind.Number ||
            !hit.TryGetProperty("byteLength", out var lengthValue) || lengthValue.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidDataException("A content hit omitted its original-byte anchor for " + query.Id + ".");
        }

        var bytes = await ReadWorkspaceFileAsync(
            executable,
            fixture,
            hit.GetProperty("fileId").GetString() ?? throw new InvalidDataException("A workspace hit has no file ID."),
            revision,
            "s15t1-read-" + query.Id + ".bin").ConfigureAwait(false);
        var fileDigest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Require(string.Equals(fileDigest, hit.GetProperty("fileSha256").GetString(), StringComparison.OrdinalIgnoreCase),
            "The hit's whole-file digest did not match the exact CLI readback for " + query.Id + ".");

        var offset = offsetValue.GetInt64();
        var length = lengthValue.GetInt64();
        Require(offset >= 0 && length > 0 && offset <= bytes.LongLength && length <= bytes.LongLength - offset,
            "The returned anchor range is outside the exact source bytes for " + query.Id + ".");
        var span = bytes.AsSpan(checked((int)offset), checked((int)length));
        var matchedText = new UTF8Encoding(false, true).GetString(span);
        Require(string.Equals(matchedText, query.Query, StringComparison.OrdinalIgnoreCase),
            "The returned byte range does not equal the frozen literal query for " + query.Id + ".");
        var anchorDigest = Convert.ToHexString(SHA256.HashData(span)).ToLowerInvariant();
        Require(string.Equals(anchorDigest, hit.GetProperty("anchorSha256").GetString(), StringComparison.OrdinalIgnoreCase),
            "The returned anchor digest did not match exact source bytes for " + query.Id + ".");
    }
}
