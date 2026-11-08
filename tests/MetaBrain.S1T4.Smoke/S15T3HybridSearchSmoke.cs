using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Security;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string S15T3ModelRevision = "614241f622f53c4eeff9890bdc4f31cfecc418b3";
    private static readonly TimeSpan S15T3LongCommandTimeout = TimeSpan.FromMinutes(6);

    public static async Task RunS15T3HybridSearchAsync(string connectionsExecutable, string cliExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S15-T3 product smoke is Windows-only.");
        }

        var root = SmokeFixture.FindRepositoryRoot();
        var fixtureDirectory = Path.Combine(root, "tests", "MetaBrain.S1T4.Smoke", "Fixtures", "S15T1", "lexical-v2");
        var corpusBytes = await File.ReadAllBytesAsync(Path.Combine(fixtureDirectory, "corpus.json")).ConfigureAwait(false);
        var queryBytes = await File.ReadAllBytesAsync(Path.Combine(fixtureDirectory, "queries.json")).ConfigureAwait(false);
        var corpus = JsonSerializer.Deserialize<S15T1Corpus>(corpusBytes, S15T1JsonOptions)
            ?? throw new InvalidDataException("The S15-T3 synthetic corpus could not be read.");
        var querySet = JsonSerializer.Deserialize<S15T1QuerySet>(queryBytes, S15T1JsonOptions)
            ?? throw new InvalidDataException("The S15-T3 frozen query set could not be read.");
        Require(corpus.SyntheticOnly, "The S15-T3 corpus must remain synthetic-only.");
        Require(string.Equals(corpus.DatasetId, querySet.DatasetId, StringComparison.Ordinal),
            "The S15-T3 corpus and query-set dataset IDs do not match.");
        var modelDirectory = Path.Combine(root, "runtime", "s15-t1-model-staging", S15T3ModelRevision, "model");

        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        try
        {
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var passphrase = CreateSyntheticPassphrase();
            var provision = await RunOwnerCommandWithInputAsync(
                cliExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The owner CLI did not provision the synthetic S15-T3 vault (stderr=" + provision.StandardError.Trim() + ").");
            var unlock = await RunOwnerCommandWithInputAsync(
                cliExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0 && unlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S15-T3 vault did not unlock (stderr=" + unlock.StandardError.Trim() + ").");

            var allowedFolder = await CreateFolderAsync(cliExecutable, fixture, "root", corpus.AllowedFolderName, 0).ConfigureAwait(false);
            var outsideFolder = await CreateFolderAsync(
                cliExecutable, fixture, "root", corpus.OutsideFolderName, allowedFolder.TreeRevision).ConfigureAwait(false);
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
                    fixture, "s15t3-" + corpusFile.Key + "-revision-1.txt", revision1).ConfigureAwait(false);
                var import = await ImportWorkspaceFileAsync(
                    cliExecutable, fixture, folderId!, corpusFile.Name, inputPath, treeRevision).ConfigureAwait(false);
                treeRevision = import.TreeRevision;
                var currentContent = corpusFile.Revision1Content;
                long currentRevision = 1;
                if (corpusFile.Revision2Content is { } revision2Content)
                {
                    var revision2Input = await WriteWorkspaceInputAsync(
                        fixture, "s15t3-" + corpusFile.Key + "-revision-2.txt", Encoding.UTF8.GetBytes(revision2Content))
                        .ConfigureAwait(false);
                    var revise = await RunOwnerCommandAsync(
                        cliExecutable,
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
                    "The S15-T3 corpus repeats a file key: " + corpusFile.Key);
            }

            for (var index = 1; index <= corpus.DistractorFileCount; index++)
            {
                var fileName = "distractor-" + index.ToString("D3", CultureInfo.InvariantCulture) + ".txt";
                var content = CreateDistractorBytes(index, corpus.DistractorBytesPerFile);
                var inputPath = await WriteWorkspaceInputAsync(fixture, "s15t3-" + fileName, content).ConfigureAwait(false);
                var import = await ImportWorkspaceFileAsync(
                    cliExecutable, fixture, allowedFolder.FolderId, fileName, inputPath, treeRevision).ConfigureAwait(false);
                treeRevision = import.TreeRevision;
            }

            // Two byte-identical files give the deterministic tie order a real
            // witness: equal score, equal length, so only the path can decide.
            var tieContent = Encoding.UTF8.GetBytes(
                "S15_T3_TIE_MARKER identical synthetic content for deterministic tie order.\n");
            foreach (var name in new[] { "tie-a.txt", "tie-b.txt" })
            {
                var inputPath = await WriteWorkspaceInputAsync(fixture, "s15t3-" + name, tieContent).ConfigureAwait(false);
                var import = await ImportWorkspaceFileAsync(
                    cliExecutable, fixture, allowedFolder.FolderId, name, inputPath, treeRevision).ConfigureAwait(false);
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

            await VerifyAgentChannelCannotReachHybridSearchAsync(fixture).ConfigureAwait(false);

            // Before the first rebuild, a natural-language hybrid query refuses
            // explicitly: the semantic channel is never silently dropped.
            var notMaterialized = await RunOwnerCommandAsync(cliExecutable,
                HybridArguments(fixture, "teach me how local retrieval works", allowedFolder.FolderId, modelDirectory))
                .ConfigureAwait(false);
            Require(notMaterialized.ExitCode == 3 &&
                    notMaterialized.StandardError.Contains("workspace_index_not_materialized", StringComparison.Ordinal) &&
                    !notMaterialized.StandardOutput.Contains("HYBRID_SEARCH", StringComparison.Ordinal),
                "A hybrid query without a materialized generation did not refuse explicitly (exit=" +
                notMaterialized.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" +
                notMaterialized.StandardError.Trim() + ").");
            Console.WriteLine("PASS hybrid search refuses explicitly before any passage generation exists");

            var firstRebuild = await RunOwnerCommandAsync(
                cliExecutable,
                new[]
                {
                    "owner", "workspace-index-rebuild", "--control-pipe", fixture.ControlPipe,
                    "--model-directory", modelDirectory,
                },
                standardInput: null,
                timeout: S15T3LongCommandTimeout).ConfigureAwait(false);
            Require(firstRebuild.ExitCode == 0 && firstRebuild.StandardOutput.Contains("WORKSPACE_INDEX", StringComparison.Ordinal),
                "The S15-T3 passage generation did not build (stderr=" + firstRebuild.StandardError.Trim() + ").");

            // The retained lexical path must keep answering exactly as before;
            // --hybrid is an additional pass, not a replacement.
            var lexicalExact = await SearchJsonAsync(cliExecutable, fixture, "mbx_rev_17", allowedFolder.FolderId)
                .ConfigureAwait(false);
            var lexicalExactHits = lexicalExact.GetProperty("hits").EnumerateArray().ToArray();
            Require(lexicalExact.GetProperty("total").GetInt32() == 1 &&
                    lexicalExactHits.Length == 1 &&
                    string.Equals(lexicalExactHits[0].GetProperty("path").GetString(), "inside/symbols.md", StringComparison.Ordinal) &&
                    lexicalExactHits[0].GetProperty("contentRevision").GetInt64() == 1 &&
                    !lexicalExact.TryGetProperty("semanticStatus", out _),
                "The retained non-hybrid lexical search changed its exact symbol answer.");
            var lexicalDate = await SearchJsonAsync(cliExecutable, fixture, "2026-11-12", allowedFolder.FolderId)
                .ConfigureAwait(false);
            var lexicalDateHits = lexicalDate.GetProperty("hits").EnumerateArray().ToArray();
            Require(lexicalDate.GetProperty("total").GetInt32() == 1 &&
                    lexicalDateHits.Length == 1 &&
                    string.Equals(lexicalDateHits[0].GetProperty("path").GetString(), "inside/temporal.md", StringComparison.Ordinal) &&
                    lexicalDateHits[0].GetProperty("contentRevision").GetInt64() == 2,
                "The retained non-hybrid lexical search changed its exact date answer.");
            Console.WriteLine("PASS the retained non-hybrid exact lexical path answers unchanged");

            var cache = new Dictionary<(string FileId, long Revision), byte[]>();
            var expectedEvidenceFound = 0;
            var expectedEvidenceTotal = 0;
            var literalQueries = 0;
            var paraphraseQueries = 0;
            var negativeQueries = 0;
            foreach (var query in querySet.Queries.Where(query => !string.Equals(query.Baseline, "scope-negative", StringComparison.Ordinal)))
            {
                var exactLiteral = S15T3ExactLiteralQueries.Contains(query.Id);
                var result = await HybridSearchJsonAsync(
                    cliExecutable, fixture, query.Query, allowedFolder.FolderId, modelDirectory, limit: WorkspaceHybridSearchService.MaximumLimit)
                    .ConfigureAwait(false);
                var hits = result.GetProperty("hits").EnumerateArray().ToArray();
                var total = result.GetProperty("total").GetInt32();
                var literalTotal = result.GetProperty("literalTotal").GetInt32();
                var semanticCandidateTotal = result.GetProperty("semanticCandidateTotal").GetInt32();
                Require(total >= hits.Length && hits.Length == Math.Min(WorkspaceHybridSearchService.MaximumLimit, total),
                    "The hybrid total and page disagree for " + query.Id + ".");
                Require(literalTotal + semanticCandidateTotal == total,
                    "The hybrid literal/candidate counts do not add up to the total for " + query.Id + ".");
                Require(result.GetProperty("hasMore").GetBoolean() == (hits.Length < total),
                    "The hybrid hasMore flag disagrees with the total for " + query.Id + ".");
                Require(exactLiteral
                        ? semanticCandidateTotal == 0 && literalTotal == total
                        : literalTotal + semanticCandidateTotal == total,
                    "An identifier-shaped query reported semantic candidates for " + query.Id + ".");
                Require(string.Equals(result.GetProperty("semanticStatus").GetString(),
                        exactLiteral ? WorkspaceHybridSemanticStatus.AbstainedExactQuery : WorkspaceHybridSemanticStatus.Applied,
                        StringComparison.Ordinal),
                    "The hybrid semantic status is wrong for " + query.Id + " (" +
                    result.GetProperty("semanticStatus").GetString() + ").");

                var semanticOnlySeen = false;
                for (var index = 0; index < hits.Length; index++)
                {
                    var hit = hits[index];
                    Require(hit.GetProperty("rank").GetInt32() == index + 1,
                        "The hybrid rank is not contiguous for " + query.Id + ".");
                    var channels = hit.GetProperty("channels").EnumerateArray().Select(value => value.GetString()!).ToArray();
                    var literal = channels.Any(channel => !string.Equals(channel, WorkspaceHybridChannels.Semantic, StringComparison.Ordinal));
                    Require(string.Equals(
                            hit.GetProperty("evidenceClass").GetString(),
                            literal ? WorkspaceHybridEvidenceClass.LiteralMatch : WorkspaceHybridEvidenceClass.SemanticCandidate,
                            StringComparison.Ordinal),
                        "A hybrid hit's evidence class disagrees with its channels for " + query.Id + ".");
                    if (literal)
                    {
                        Require(!semanticOnlySeen,
                            "A literal hit was displaced below a semantic-only neighbour for " + query.Id + ".");
                    }
                    else
                    {
                        semanticOnlySeen = true;
                        Require(exactLiteral == false,
                            "An identifier-shaped query returned a semantic neighbour for " + query.Id + ".");
                    }

                    Require(!hit.GetProperty("path").GetString()!.StartsWith("outside/", StringComparison.Ordinal),
                        "A folder-scoped hybrid page returned an outside-folder hit for " + query.Id + ".");
                    await VerifyHybridHitAsync(cliExecutable, fixture, hit, byPath, cache).ConfigureAwait(false);
                }
                var matched = false;
                var matchedRank = -1;
                foreach (var expected in query.ExpectedEvidence)
                {
                    for (var index = 0; index < hits.Length; index++)
                    {
                        var hit = hits[index];
                        if (!string.Equals(hit.GetProperty("path").GetString(), expected.Path, StringComparison.Ordinal) ||
                            hit.GetProperty("contentRevision").GetInt64() != expected.Revision)
                        {
                            continue;
                        }

                        if (await ExpectedEvidenceMatchesAsync(cliExecutable, fixture, query, expected, hit, cache)
                                .ConfigureAwait(false))
                        {
                            matched = true;
                            matchedRank = index + 1;
                            break;
                        }
                    }

                    if (matched)
                    {
                        break;
                    }
                }

                if (query.ExpectedEvidence.Count > 0)
                {
                    expectedEvidenceTotal++;
                    if (string.Equals(query.Baseline, "miss", StringComparison.Ordinal))
                    {
                        paraphraseQueries++;
                    }
                    else
                    {
                        literalQueries++;
                    }

                    Require(matched,
                        "The hybrid ranking did not return the labeled evidence for " + query.Id + " (query=" + query.Query + ").");
                    expectedEvidenceFound++;
                }
                else
                {
                    negativeQueries++;
                    Require(total == 0 && hits.Length == 0,
                        "Frozen negative " + query.Id + " returned " + total.ToString(CultureInfo.InvariantCulture) + " hybrid candidates.");
                }

                Console.WriteLine(
                    "HYBRID id=" + query.Id +
                    " family=" + query.Family +
                    " expected=" + query.Baseline +
                    " total=" + total.ToString(CultureInfo.InvariantCulture) +
                    " status=" + result.GetProperty("semanticStatus").GetString() +
                    " evidenceRank=" + (matched ? matchedRank.ToString(CultureInfo.InvariantCulture) : "none") +
                    " observed=" + string.Join(",", hits.Take(3).Select(hit =>
                        hit.GetProperty("path").GetString() + "@" +
                        hit.GetProperty("contentRevision").GetInt64().ToString(CultureInfo.InvariantCulture) + ":" +
                        string.Join("+", hit.GetProperty("channels").EnumerateArray().Select(value => value.GetString())))));
            }

            Require(expectedEvidenceFound == expectedEvidenceTotal,
                "The hybrid ranking returned labeled evidence for " + expectedEvidenceFound.ToString(CultureInfo.InvariantCulture) +
                " of " + expectedEvidenceTotal.ToString(CultureInfo.InvariantCulture) + " frozen queries.");
            Console.WriteLine(
                "PASS S15-T3 frozen hybrid evidence: dataset=lexical-v2 queries=" + querySet.Queries.Count.ToString(CultureInfo.InvariantCulture) +
                " labeledEvidence=" + expectedEvidenceTotal.ToString(CultureInfo.InvariantCulture) +
                " literal=" + literalQueries.ToString(CultureInfo.InvariantCulture) +
                " paraphraseOrNearMiss=" + paraphraseQueries.ToString(CultureInfo.InvariantCulture) +
                " negatives=" + negativeQueries.ToString(CultureInfo.InvariantCulture) +
                "; no similarity threshold or owner acceptance is implied");

            await RunS15T3UnrelatedNegativesAsync(cliExecutable, fixture, allowedFolder.FolderId, modelDirectory).ConfigureAwait(false);
            await RunS15T3ScopeNegativesAsync(cliExecutable, fixture, querySet, allowedFolder.FolderId, modelDirectory).ConfigureAwait(false);
            await RunS15T3TieAndDedupAsync(cliExecutable, fixture, allowedFolder.FolderId, modelDirectory).ConfigureAwait(false);
            await RunS15T3FiltersAsync(cliExecutable, fixture, allowedFolder.FolderId, modelDirectory).ConfigureAwait(false);
            await RunS15T3ModelRefusalsAsync(cliExecutable, fixture, allowedFolder.FolderId, modelDirectory).ConfigureAwait(false);
            await RunS15T3CancellationAsync(
                cliExecutable, fixture, allowedFolder.FolderId, modelDirectory, passphrase).ConfigureAwait(false);
            await RunS15T3StaleAsync(cliExecutable, fixture, allowedFolder.FolderId, modelDirectory).ConfigureAwait(false);
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static readonly HashSet<string> S15T3ExactLiteralQueries = new(StringComparer.Ordinal)
    {
        "H01", "H02", "H05", "H10", "H11", "H12", "H22", "H31", "H32", "H33", "H34",
    };

    private static string[] HybridArguments(
        SmokeFixture fixture,
        string query,
        string? folderId,
        string? modelDirectory,
        string format = "json",
        string? field = null,
        string? current = null,
        string? contentDateFrom = null,
        string? contentDateTo = null,
        int? offset = null,
        int? limit = null,
        string? purpose = null,
        bool? includeArchived = null)
    {
        var arguments = new List<string>
        {
            "owner", "workspace-search", "--control-pipe", fixture.ControlPipe,
            "--query", query, "--hybrid", "true", "--format", format,
        };
        if (folderId is not null)
        {
            arguments.Add("--folder-id");
            arguments.Add(folderId);
        }

        if (modelDirectory is not null)
        {
            arguments.Add("--model-directory");
            arguments.Add(modelDirectory);
        }

        if (field is not null)
        {
            arguments.Add("--field");
            arguments.Add(field);
        }

        if (current is not null)
        {
            arguments.Add("--current");
            arguments.Add(current);
        }

        if (contentDateFrom is not null)
        {
            arguments.Add("--content-date-from");
            arguments.Add(contentDateFrom);
        }

        if (contentDateTo is not null)
        {
            arguments.Add("--content-date-to");
            arguments.Add(contentDateTo);
        }

        if (offset is { } offsetValue)
        {
            arguments.Add("--offset");
            arguments.Add(offsetValue.ToString(CultureInfo.InvariantCulture));
        }

        if (limit is { } limitValue)
        {
            arguments.Add("--limit");
            arguments.Add(limitValue.ToString(CultureInfo.InvariantCulture));
        }
        if (purpose is not null)
        {
            arguments.Add("--purpose");
            arguments.Add(purpose);
        }

        if (includeArchived is { } archivedValue)
        {
            arguments.Add("--include-archived");
            arguments.Add(archivedValue.ToString().ToLowerInvariant());
        }

        return arguments.ToArray();
    }

    private static async Task<JsonElement> HybridSearchJsonAsync(
        string executable,
        SmokeFixture fixture,
        string query,
        string? folderId,
        string? modelDirectory,
        string? field = null,
        string? current = null,
        string? contentDateFrom = null,
        string? contentDateTo = null,
        int? offset = null,
        int? limit = null,
        string? purpose = null,
        bool? includeArchived = null)
    {
        var result = await RunOwnerCommandAsync(
            executable,
            HybridArguments(
                fixture,
                query,
                folderId,
                modelDirectory,
                "json",
                field,
                current,
                contentDateFrom,
                contentDateTo,
                offset,
                limit,
                purpose,
                includeArchived),
            standardInput: null,
            timeout: S15T3LongCommandTimeout).ConfigureAwait(false);
        Require(result.ExitCode == 0,
            "The actual owner CLI hybrid search failed for '" + query + "' (stderr=" + result.StandardError.Trim() + ").");
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.Clone();
    }

    private static async Task<byte[]> ReadHybridSourceAsync(
        string executable,
        SmokeFixture fixture,
        string fileId,
        long revision,
        Dictionary<(string FileId, long Revision), byte[]> cache)
    {
        if (cache.TryGetValue((fileId, revision), out var cached))
        {
            return cached;
        }

        var bytes = await ReadWorkspaceFileAsync(
            executable, fixture, fileId, revision,
            "s15t3-read-" + fileId + "-" + revision.ToString(CultureInfo.InvariantCulture) + ".bin").ConfigureAwait(false);
        cache[(fileId, revision)] = bytes;
        return bytes;
    }

    private static async Task VerifyHybridHitAsync(
        string executable,
        SmokeFixture fixture,
        JsonElement hit,
        IReadOnlyDictionary<string, S15T1ImportedFile> byPath,
        Dictionary<(string FileId, long Revision), byte[]> cache)
    {
        var path = hit.GetProperty("path").GetString()
            ?? throw new InvalidDataException("A hybrid hit has no path.");
        Require(path.StartsWith("inside/", StringComparison.Ordinal) ||
                string.Equals(path, "outside/decoy.md", StringComparison.Ordinal),
            "A hybrid hit escaped the synthetic corpus: " + path);
        var revision = hit.GetProperty("contentRevision").GetInt64();
        Require(revision >= 1, "A hybrid hit has no positive content revision.");
        var fileId = hit.GetProperty("fileId").GetString()
            ?? throw new InvalidDataException("A hybrid hit has no file ID.");
        var bytes = await ReadHybridSourceAsync(executable, fixture, fileId, revision, cache).ConfigureAwait(false);
        var fileDigest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Require(string.Equals(fileDigest, hit.GetProperty("fileSha256").GetString(), StringComparison.OrdinalIgnoreCase),
            "A hybrid hit's whole-file digest does not match its exact source revision: " + path + "@" +
            revision.ToString(CultureInfo.InvariantCulture) + ".");

        var channels = hit.GetProperty("channels").EnumerateArray().Select(value => value.GetString()!).ToArray();
        var semantic = channels.Contains(WorkspaceHybridChannels.Semantic);
        Require(hit.GetProperty("similarity").ValueKind == (semantic ? JsonValueKind.Number : JsonValueKind.Null),
            "A hybrid hit's similarity is inconsistent with its channels: " + path + ".");

        if (hit.GetProperty("byteOffset").ValueKind != JsonValueKind.Number)
        {
            Require(hit.GetProperty("field").GetString() is "name" or "path",
                "A hybrid metadata hit does not name a metadata field: " + path + ".");
            return;
        }

        var offset = hit.GetProperty("byteOffset").GetInt64();
        var length = hit.GetProperty("byteLength").GetInt64();
        Require(offset >= 0 && length > 0 && offset <= bytes.LongLength && length <= bytes.LongLength - offset,
            "A hybrid anchor range is outside its exact source revision: " + path + ".");
        var span = bytes.AsSpan(checked((int)offset), checked((int)length));
        var anchorDigest = Convert.ToHexString(SHA256.HashData(span)).ToLowerInvariant();
        Require(string.Equals(anchorDigest, hit.GetProperty("anchorSha256").GetString(), StringComparison.OrdinalIgnoreCase),
            "A hybrid anchor digest does not match exact source bytes: " + path + ".");
        var evidence = hit.GetProperty("evidence").GetString() ?? string.Empty;
        Require(evidence.Length > 0 && evidence.Length <= WorkspaceHybridSearchService.EvidenceSnippetChars,
            "A hybrid hit's evidence snippet is empty or above its bound: " + path + ".");
        Require(WorkspaceSearch.TryDecodeSearchText(bytes, out var text),
            "A hybrid text hit's source revision is not decodable text: " + path + ".");
        Require(text.Contains(evidence, StringComparison.Ordinal),
            "A hybrid evidence snippet is not an exact substring of its source revision: " + path + ".");
    }

    private static async Task<bool> ExpectedEvidenceMatchesAsync(
        string executable,
        SmokeFixture fixture,
        S15T1Query query,
        S15T1ExpectedEvidence expected,
        JsonElement hit,
        Dictionary<(string FileId, long Revision), byte[]> cache)
    {
        if (hit.GetProperty("byteOffset").ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var fileId = hit.GetProperty("fileId").GetString()!;
        var revision = hit.GetProperty("contentRevision").GetInt64();
        var bytes = await ReadHybridSourceAsync(executable, fixture, fileId, revision, cache).ConfigureAwait(false);
        var offset = hit.GetProperty("byteOffset").GetInt64();
        var length = hit.GetProperty("byteLength").GetInt64();
        if (offset < 0 || length < 1 || offset > bytes.LongLength || length > bytes.LongLength - offset ||
            !WorkspaceSearch.TryDecodeSearchText(bytes, out var _))
        {
            return false;
        }

        var anchor = new UTF8Encoding(false, true).GetString(bytes.AsSpan(checked((int)offset), checked((int)length)));
        return string.Equals(anchor, query.Query, StringComparison.OrdinalIgnoreCase) ||
            anchor.Contains(expected.Anchor, StringComparison.Ordinal) ||
            expected.Anchor.Contains(anchor, StringComparison.Ordinal);
    }

    private static async Task VerifyAgentChannelCannotReachHybridSearchAsync(SmokeFixture fixture)
    {
        var requestJson = JsonSerializer.Serialize(new
        {
            protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
            operation = ServiceRequestHandler.OwnerWorkspaceHybridSearchOperation,
            query = "synthetic owner search"
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var result = await PipeClient.InvokeAgentAsync(fixture.AgentPipe, requestJson, CancellationToken.None).ConfigureAwait(false);
        Require(result.Success && result.Json.Contains("forbidden", StringComparison.Ordinal),
            "The agent channel did not refuse the owner hybrid search operation (reply=" + result.Json + ").");
        Console.WriteLine("PASS the agent channel cannot reach the owner hybrid search");
    }

    /// <summary>
    /// Independent, non-frozen unrelated queries: they have no evidence
    /// anywhere in the synthetic corpus. The dense local channel always has a
    /// nearest neighbour, so these checks record what is actually returned
    /// instead of asserting an abstention that no approved rule can justify.
    /// Every returned hit must still be in scope and carry an exact anchor.
    /// </summary>
    private static async Task RunS15T3UnrelatedNegativesAsync(
        string executable,
        SmokeFixture fixture,
        string allowedFolderId,
        string modelDirectory)
    {
        var cache = new Dictionary<(string FileId, long Revision), byte[]>();
        var observations = new List<string>();
        foreach (var query in new[]
                 {
                     "what is the weather forecast in a distant city tomorrow",
                     "quantum chromodynamics lattice spacing measurement",
                     "which football team won the regional championship in 1998",
                 })
        {
            var result = await HybridSearchJsonAsync(
                executable, fixture, query, allowedFolderId, modelDirectory,
                limit: WorkspaceHybridSearchService.MaximumLimit).ConfigureAwait(false);
            var hits = result.GetProperty("hits").EnumerateArray().ToArray();
            var total = result.GetProperty("total").GetInt32();
            Require(total >= hits.Length, "An unrelated hybrid query returned an inconsistent total.");
            Require(string.Equals(result.GetProperty("semanticStatus").GetString(),
                    WorkspaceHybridSemanticStatus.Applied, StringComparison.Ordinal),
                "An unrelated natural-language query did not apply the semantic channel.");
            // Candidate-discovery contract: an unrelated query is not a match,
            // and every row must say so instead of being counted as evidence.
            Require(result.GetProperty("literalTotal").GetInt32() == 0 &&
                    result.GetProperty("semanticCandidateTotal").GetInt32() == total &&
                    hits.All(hit => string.Equals(
                        hit.GetProperty("evidenceClass").GetString(),
                        WorkspaceHybridEvidenceClass.SemanticCandidate,
                        StringComparison.Ordinal)),
                "An unrelated hybrid query presented its neighbours as literal matches.");
            foreach (var hit in hits)
            {
                Require(!hit.GetProperty("path").GetString()!.StartsWith("outside/", StringComparison.Ordinal),
                    "An unrelated scoped hybrid query returned an outside-folder hit.");
                await VerifyHybridHitAsync(executable, fixture, hit,
                    new Dictionary<string, S15T1ImportedFile>(StringComparer.Ordinal), cache).ConfigureAwait(false);
            }

            observations.Add(
                JsonSerializer.Serialize(query) + ":total=" + result.GetProperty("total").GetInt32().ToString(CultureInfo.InvariantCulture) +
                ":top=" + (hits.Length == 0 ? "none" : hits[0].GetProperty("path").GetString()));
        }

        Console.WriteLine(
            "PASS S15-T3 unrelated natural-language queries are candidates, not matches: " +
            string.Join("; ", observations) +
            "; every row is evidenceClass=" + WorkspaceHybridEvidenceClass.SemanticCandidate +
            " with literalTotal=0, so candidate discovery is explicit and no general abstention is claimed");
    }

    private static async Task RunS15T3ScopeNegativesAsync(
        string executable,
        SmokeFixture fixture,
        S15T1QuerySet querySet,
        string allowedFolderId,
        string modelDirectory)
    {
        var scopeNegatives = querySet.Queries
            .Where(query => string.Equals(query.Baseline, "scope-negative", StringComparison.Ordinal))
            .ToArray();
        Require(scopeNegatives.Length == 4, "The frozen S15-T3 scope negatives changed.");
        foreach (var query in scopeNegatives)
        {
            var result = await RunOwnerCommandAsync(
                executable,
                HybridArguments(fixture, query.Query, allowedFolderId, modelDirectory, "json", limit: WorkspaceHybridSearchService.MaximumLimit),
                standardInput: null,
                timeout: S15T3LongCommandTimeout).ConfigureAwait(false);
            Require(result.ExitCode == 0, "The scoped hybrid negative failed for " + query.Id + ".");
            using var document = JsonDocument.Parse(result.StandardOutput);
            var page = document.RootElement;
            Require(page.GetProperty("total").GetInt32() == 0 &&
                    page.GetProperty("literalTotal").GetInt32() == 0 &&
                    page.GetProperty("semanticCandidateTotal").GetInt32() == 0 &&
                    page.GetProperty("hits").GetArrayLength() == 0,
                "A folder-scoped hybrid negative disclosed an out-of-scope candidate for " + query.Id + ".");
            Require(page.GetProperty("semanticStatus").GetString() == WorkspaceHybridSemanticStatus.AbstainedExactQuery &&
                    !result.StandardOutput.Contains("outside/decoy.md", StringComparison.Ordinal),
                "A folder-scoped hybrid negative disclosed an outside-folder path or a semantic answer for " + query.Id + ".");

            var wide = await HybridSearchJsonAsync(
                executable, fixture, query.Query, null, modelDirectory, limit: WorkspaceHybridSearchService.MaximumLimit)
                .ConfigureAwait(false);
            var wideHits = wide.GetProperty("hits").EnumerateArray().ToArray();
            Require(wide.GetProperty("total").GetInt32() == wideHits.Length && wideHits.Length == 1 &&
                    string.Equals(wideHits[0].GetProperty("path").GetString(), query.WideExpectedPath, StringComparison.Ordinal),
                "The unfiltered hybrid query did not return exactly the expected outside-folder witness for " + query.Id + ".");
        }

        // A natural-language query that the outside decoy would answer
        // semantically must still disclose nothing from outside the scope.
        const string prose = "synthetic excluded-scope decoy marker outside the selected folder";
        var scoped = await RunOwnerCommandAsync(
            executable,
            HybridArguments(fixture, prose, allowedFolderId, modelDirectory, "json", limit: WorkspaceHybridSearchService.MaximumLimit),
            standardInput: null,
            timeout: S15T3LongCommandTimeout).ConfigureAwait(false);
        Require(scoped.ExitCode == 0, "The scoped prose hybrid query failed.");
        using (var scopedDocument = JsonDocument.Parse(scoped.StandardOutput))
        {
            foreach (var hit in scopedDocument.RootElement.GetProperty("hits").EnumerateArray())
            {
                Require(!hit.GetProperty("path").GetString()!.StartsWith("outside/", StringComparison.Ordinal),
                    "A scoped prose hybrid query disclosed an outside-folder path.");
                Require(!hit.GetProperty("evidence").GetString()!.Contains("S15_OUTSIDE_ONLY_", StringComparison.Ordinal),
                    "A scoped prose hybrid query disclosed an outside-folder snippet.");
            }
        }

        Require(!scoped.StandardOutput.Contains("outside/decoy.md", StringComparison.Ordinal),
            "A scoped prose hybrid query leaked the outside-folder decoy path.");
        var unscoped = await HybridSearchJsonAsync(
            executable, fixture, prose, null, modelDirectory, limit: WorkspaceHybridSearchService.MaximumLimit)
            .ConfigureAwait(false);
        Require(unscoped.GetProperty("total").GetInt32() > 0 &&
                unscoped.GetProperty("hits").EnumerateArray().Any(hit =>
                    string.Equals(hit.GetProperty("path").GetString(), "outside/decoy.md", StringComparison.Ordinal)),
            "The unfiltered prose witness did not show the outside decoy that the scoped query withheld.");
        Console.WriteLine(
            "PASS S15-T3 scope applies before ranking and disclosure: folderScopeNegatives=" +
            scopeNegatives.Length.ToString(CultureInfo.InvariantCulture) +
            " proseDecoyWithheld=true totalsAndSnippetsClean=true");
    }

    private static async Task RunS15T3TieAndDedupAsync(
        string executable,
        SmokeFixture fixture,
        string allowedFolderId,
        string modelDirectory)
    {
        var first = await RunOwnerCommandAsync(
            executable,
            HybridArguments(fixture, "S15_T3_TIE_MARKER", allowedFolderId, modelDirectory, "json", limit: 20),
            standardInput: null,
            timeout: S15T3LongCommandTimeout).ConfigureAwait(false);
        var second = await RunOwnerCommandAsync(
            executable,
            HybridArguments(fixture, "S15_T3_TIE_MARKER", allowedFolderId, modelDirectory, "json", limit: 20),
            standardInput: null,
            timeout: S15T3LongCommandTimeout).ConfigureAwait(false);
        Require(first.ExitCode == 0 && second.ExitCode == 0, "The deterministic tie searches failed.");
        using var firstDocument = JsonDocument.Parse(first.StandardOutput);
        using var secondDocument = JsonDocument.Parse(second.StandardOutput);
        var firstHits = firstDocument.RootElement.GetProperty("hits").EnumerateArray().ToArray();
        var tiePaths = firstHits.Select(hit => hit.GetProperty("path").GetString()!).ToArray();
        Require(tiePaths.SequenceEqual(new[] { "inside/tie-a.txt", "inside/tie-b.txt" }),
            "The byte-identical tie did not resolve by path ordinal (" + string.Join(",", tiePaths) + ").");
        Require(
            firstHits.Select(hit => hit.GetProperty("rank").GetInt32()).SequenceEqual(new[] { 1, 2 }) &&
            firstDocument.RootElement.GetProperty("total").GetInt32() == 2,
            "The deterministic tie order is not a stable two-hit page.");
        Require(string.Equals(first.StandardOutput, second.StandardOutput, StringComparison.Ordinal),
            "Two identical tie searches did not produce byte-identical pages.");

        var dedup = await HybridSearchJsonAsync(
            executable, fixture, "I need to retrieve saved information", allowedFolderId, modelDirectory,
            limit: WorkspaceHybridSearchService.MaximumLimit).ConfigureAwait(false);
        var dedupHits = dedup.GetProperty("hits").EnumerateArray()
            .Where(hit => string.Equals(hit.GetProperty("path").GetString(), "inside/search-intent.md", StringComparison.Ordinal))
            .ToArray();
        Require(dedupHits.Length > 0, "The dedicated dedup query returned no search-intent hit.");
        var merged = dedupHits.Where(hit => hit.GetProperty("channels").EnumerateArray().Any(value =>
                string.Equals(value.GetString(), WorkspaceHybridChannels.Lexical, StringComparison.Ordinal)) &&
            hit.GetProperty("channels").EnumerateArray().Any(value =>
                string.Equals(value.GetString(), WorkspaceHybridChannels.Semantic, StringComparison.Ordinal))).ToArray();
        Require(merged.Length == 1,
            "The literal/semantic overlap was not merged into one hit (" + merged.Length.ToString(CultureInfo.InvariantCulture) + ").");
        var keys = dedup.GetProperty("hits").EnumerateArray()
            .Select(hit => hit.GetProperty("fileId").GetString() + "@" +
                hit.GetProperty("contentRevision").GetInt64().ToString(CultureInfo.InvariantCulture) + ":" +
                (hit.GetProperty("byteOffset").ValueKind == JsonValueKind.Number
                    ? hit.GetProperty("byteOffset").GetInt64().ToString(CultureInfo.InvariantCulture)
                    : "metadata") + "+" +
                (hit.GetProperty("byteLength").ValueKind == JsonValueKind.Number
                    ? hit.GetProperty("byteLength").GetInt64().ToString(CultureInfo.InvariantCulture)
                    : "metadata"))
            .ToArray();
        Require(keys.Distinct(StringComparer.Ordinal).Count() == keys.Length,
            "The hybrid page returned the same exact anchor twice.");
        Console.WriteLine(
            "PASS S15-T3 deterministic ties and cross-channel dedupe: tieOrder=tie-a,tie-b identicalRuns=true mergedChannels=1 uniqueAnchors=true");
    }

    private static async Task RunS15T3FiltersAsync(
        string executable,
        SmokeFixture fixture,
        string allowedFolderId,
        string modelDirectory)
    {
        const string headingQuery = "Synthetic preference evidence";
        var heading = await HybridSearchJsonAsync(
            executable, fixture, headingQuery, allowedFolderId, modelDirectory, field: "heading",
            limit: WorkspaceHybridSearchService.MaximumLimit).ConfigureAwait(false);
        Require(string.Equals(heading.GetProperty("semanticStatus").GetString(),
                WorkspaceHybridSemanticStatus.NotApplicableField, StringComparison.Ordinal),
            "The field filter did not mark the semantic channel not applicable.");
        var headingHits = heading.GetProperty("hits").EnumerateArray().ToArray();
        Require(headingHits.Length == 1 &&
                string.Equals(headingHits[0].GetProperty("field").GetString(), "heading", StringComparison.Ordinal) &&
                string.Equals(headingHits[0].GetProperty("path").GetString(), "inside/preferences.md", StringComparison.Ordinal) &&
                !headingHits[0].GetProperty("channels").EnumerateArray().Any(value =>
                    string.Equals(value.GetString(), WorkspaceHybridChannels.Semantic, StringComparison.Ordinal)),
            "The heading field filter did not restrict the page to the exact heading hit.");

        const string retired = "S15_RETIRED_PLAN_7C4D";
        var currentOnly = await HybridSearchJsonAsync(
            executable, fixture, retired, allowedFolderId, modelDirectory, current: "current",
            limit: WorkspaceHybridSearchService.MaximumLimit).ConfigureAwait(false);
        Require(currentOnly.GetProperty("total").GetInt32() == 0,
            "A currentness=current hybrid query returned retired content.");
        var history = await HybridSearchJsonAsync(
            executable, fixture, retired, allowedFolderId, modelDirectory, current: "history",
            limit: WorkspaceHybridSearchService.MaximumLimit).ConfigureAwait(false);
        var historyHits = history.GetProperty("hits").EnumerateArray().ToArray();
        Require(historyHits.Length == 1 &&
                historyHits[0].GetProperty("contentRevision").GetInt64() == 1 &&
                historyHits[0].GetProperty("stale").GetBoolean() &&
                string.Equals(historyHits[0].GetProperty("path").GetString(), "inside/temporal-2.md", StringComparison.Ordinal),
            "The currentness=history hybrid query did not return the retained revision as stale.");
        var all = await HybridSearchJsonAsync(
            executable, fixture, "Synthetic supplementary time record", allowedFolderId, modelDirectory, current: "all",
            limit: WorkspaceHybridSearchService.MaximumLimit).ConfigureAwait(false);
        var allHits = all.GetProperty("hits").EnumerateArray().ToArray();
        var staleLiteralSeen = false;
        var currentLiteralSeen = 0;
        var semanticSeen = false;
        foreach (var hit in allHits)
        {
            var literal = hit.GetProperty("channels").EnumerateArray().Any(value =>
                !string.Equals(value.GetString(), WorkspaceHybridChannels.Semantic, StringComparison.Ordinal));
            if (!literal)
            {
                semanticSeen = true;
                Require(!hit.GetProperty("stale").GetBoolean(),
                    "The currentness=all hybrid order returned a retained semantic-only hit.");
                continue;
            }

            Require(!semanticSeen, "The currentness=all hybrid order put a literal hit below a semantic-only hit.");
            if (hit.GetProperty("stale").GetBoolean())
            {
                staleLiteralSeen = true;
                continue;
            }

            Require(!staleLiteralSeen, "The currentness=all hybrid order put a current literal hit below a retained literal hit.");
            currentLiteralSeen++;
        }

        Require(staleLiteralSeen && currentLiteralSeen > 0,
            "The currentness=all hybrid query did not exercise both current and retained literal evidence.");

        var dated = await HybridSearchJsonAsync(
            executable, fixture, "review", allowedFolderId, modelDirectory,
            contentDateFrom: "2026-04-01", contentDateTo: "2026-04-30",
            limit: WorkspaceHybridSearchService.MaximumLimit).ConfigureAwait(false);
        var datedHits = dated.GetProperty("hits").EnumerateArray().ToArray();
        Require(dated.GetProperty("total").GetInt32() == datedHits.Length && datedHits.Length > 0,
            "The inclusive content-date window returned no April evidence.");
        foreach (var hit in datedHits)
        {
            Require(string.Equals(hit.GetProperty("path").GetString(), "inside/temporal-2.md", StringComparison.Ordinal),
                "The content-date window returned a file without the in-window content date.");
            Require((hit.GetProperty("evidence").GetString() ?? string.Empty).Contains("2026-04", StringComparison.Ordinal),
                "A content-date hit's evidence carries no in-window content date.");
        }

        var outsideWindow = await HybridSearchJsonAsync(
            executable, fixture, "review", allowedFolderId, modelDirectory,
            contentDateFrom: "2030-01-01", contentDateTo: "2030-01-02",
            limit: WorkspaceHybridSearchService.MaximumLimit).ConfigureAwait(false);
        Require(outsideWindow.GetProperty("total").GetInt32() == 0,
            "A content-date window with no matching date still returned candidates.");
        var inverted = await RunOwnerCommandAsync(
            executable,
            HybridArguments(fixture, "review", allowedFolderId, modelDirectory, "json",
                contentDateFrom: "2026-05-01", contentDateTo: "2026-04-30"),
            standardInput: null,
            timeout: S15T3LongCommandTimeout).ConfigureAwait(false);
        Require(inverted.ExitCode == 2,
            "An inverted content-date window was not refused as a usage error (exit=" +
            inverted.ExitCode.ToString(CultureInfo.InvariantCulture) + ").");

        var limited = await HybridSearchJsonAsync(
            executable, fixture, "I need to retrieve saved information", allowedFolderId, modelDirectory,
            limit: 2).ConfigureAwait(false);
        var full = await HybridSearchJsonAsync(
            executable, fixture, "I need to retrieve saved information", allowedFolderId, modelDirectory,
            limit: WorkspaceHybridSearchService.MaximumLimit).ConfigureAwait(false);
        Require(limited.GetProperty("hits").GetArrayLength() == 2 &&
                limited.GetProperty("total").GetInt32() == full.GetProperty("total").GetInt32() &&
                limited.GetProperty("hasMore").GetBoolean(),
            "A bounded page changed the scoped total or ignored the page bound.");
        var beyond = await HybridSearchJsonAsync(
            executable, fixture, "I need to retrieve saved information", allowedFolderId, modelDirectory,
            offset: full.GetProperty("total").GetInt32() + 5, limit: 5).ConfigureAwait(false);
        Require(beyond.GetProperty("hits").GetArrayLength() == 0 && !beyond.GetProperty("hasMore").GetBoolean() &&
                beyond.GetProperty("total").GetInt32() == full.GetProperty("total").GetInt32(),
            "A page beyond the scoped total changed the total or reported more results.");
        Console.WriteLine(
            "PASS S15-T3 hybrid filters: field=heading currentness=current/history/all contentDateInclusive=true " +
            "contentDateBoundary=0 pageBoundKeepsTotal=true");
    }

    private static async Task RunS15T3ModelRefusalsAsync(
        string executable,
        SmokeFixture fixture,
        string allowedFolderId,
        string modelDirectory)
    {
        const string prose = "teach me how local retrieval works";
        var missingModelDirectory = Path.Combine(fixture.OutputDirectory, "s15t3-missing-model");
        var missing = await RunOwnerCommandAsync(
            executable,
            HybridArguments(fixture, prose, allowedFolderId, missingModelDirectory),
            standardInput: null,
            timeout: S15T3LongCommandTimeout).ConfigureAwait(false);
        Require(missing.ExitCode == 3 &&
                missing.StandardError.Contains("workspace_index_model_missing", StringComparison.Ordinal) &&
                !missing.StandardOutput.Contains("HYBRID_SEARCH", StringComparison.Ordinal),
            "A missing local model did not refuse explicitly (stderr=" + missing.StandardError.Trim() + ").");

        var noModel = await RunOwnerCommandAsync(
            executable,
            HybridArguments(fixture, prose, allowedFolderId, null),
            standardInput: null,
            timeout: S15T3LongCommandTimeout).ConfigureAwait(false);
        Require(noModel.ExitCode == 3 &&
                noModel.StandardError.Contains("workspace_index_model_missing", StringComparison.Ordinal),
            "A hybrid query without a model directory did not refuse explicitly.");

        var invalidDirectory = Path.Combine(fixture.OutputDirectory, "s15t3-invalid-model");
        Directory.CreateDirectory(Path.Combine(invalidDirectory, "onnx"));
        await File.WriteAllBytesAsync(Path.Combine(invalidDirectory, "onnx", "model.onnx"), [0]).ConfigureAwait(false);
        var invalid = await RunOwnerCommandAsync(
            executable,
            HybridArguments(fixture, prose, allowedFolderId, invalidDirectory),
            standardInput: null,
            timeout: S15T3LongCommandTimeout).ConfigureAwait(false);
        Require(invalid.ExitCode == 3 &&
                invalid.StandardError.Contains("workspace_index_model_invalid", StringComparison.Ordinal) &&
                !invalid.StandardOutput.Contains("HYBRID_SEARCH", StringComparison.Ordinal),
            "An invalid local model did not refuse explicitly (stderr=" + invalid.StandardError.Trim() + ").");

        var exact = await HybridSearchJsonAsync(
            executable, fixture, "S15_T1_HASH_A9", allowedFolderId, null,
            limit: WorkspaceHybridSearchService.MaximumLimit).ConfigureAwait(false);
        Require(string.Equals(exact.GetProperty("semanticStatus").GetString(),
                WorkspaceHybridSemanticStatus.AbstainedExactQuery, StringComparison.Ordinal) &&
                exact.GetProperty("hits").EnumerateArray().Any(hit =>
                    string.Equals(hit.GetProperty("path").GetString(), "inside/symbols-2.md", StringComparison.Ordinal)),
            "An identifier query without a model did not answer from the retained exact path.");
        Console.WriteLine(
            "PASS S15-T3 missing/invalid model and no-model exact path are explicit: modelMissing=true modelInvalid=true " +
            "exactWithoutModel=abstained_exact_query noSilentSubstitution=true");
    }

    private static async Task RunS15T3CancellationAsync(
        string executable,
        SmokeFixture fixture,
        string allowedFolderId,
        string modelDirectory,
        string passphrase)
    {
        const string prose = "When should this be reviewed again?";
        var measuredStart = Stopwatch.GetTimestamp();
        var measured = await HybridSearchJsonAsync(
            executable, fixture, prose, allowedFolderId, modelDirectory,
            limit: WorkspaceHybridSearchService.MaximumLimit).ConfigureAwait(false);
        var measuredMs = Stopwatch.GetElapsedTime(measuredStart).TotalMilliseconds;
        Require(measured.GetProperty("total").GetInt32() > 0, "The cancellation probe query returned no evidence.");

        var lockResult = await RunOwnerCommandAsync(executable, "owner", "lock", "--control-pipe", fixture.ControlPipe)
            .ConfigureAwait(false);
        Require(lockResult.ExitCode == 0, "The owner did not lock the synthetic vault for the hybrid negative.");
        var locked = await RunOwnerCommandAsync(
            executable,
            HybridArguments(fixture, prose, allowedFolderId, modelDirectory),
            standardInput: null,
            timeout: S15T3LongCommandTimeout).ConfigureAwait(false);
        Require(locked.ExitCode == 3 &&
                locked.StandardError.Contains("vault_locked", StringComparison.Ordinal) &&
                !locked.StandardOutput.Contains("HYBRID_SEARCH", StringComparison.Ordinal) &&
                !locked.StandardOutput.Contains("HIT ", StringComparison.Ordinal),
            "A locked vault still answered a hybrid search (exit=" +
            locked.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + locked.StandardError.Trim() + ").");

        var unlock = await RunOwnerCommandWithInputAsync(
            executable, passphrase + Environment.NewLine,
            "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(unlock.ExitCode == 0 && unlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
            "The synthetic vault did not unlock after the locked hybrid denial.");

        // The search loads and hashes the pinned model on every call, so a
        // concurrent lock lands inside real work. The delay is derived from the
        // measured duration instead of a fixed guess.
        var delayMs = Math.Max(50, Math.Min(300, (int)(measuredMs / 3)));
        var searchTask = RunOwnerCommandAsync(
            executable,
            HybridArguments(fixture, prose, allowedFolderId, modelDirectory),
            standardInput: null,
            timeout: S15T3LongCommandTimeout);
        await Task.Delay(delayMs).ConfigureAwait(false);
        var concurrentLock = await RunOwnerCommandAsync(executable, "owner", "lock", "--control-pipe", fixture.ControlPipe)
            .ConfigureAwait(false);
        var cancelled = await searchTask.ConfigureAwait(false);
        Require(concurrentLock.ExitCode == 0, "The concurrent owner lock failed.");
        Require(cancelled.ExitCode != 0 &&
                cancelled.StandardError.Contains("vault_locked", StringComparison.Ordinal) &&
                !cancelled.StandardOutput.Contains("HYBRID_SEARCH", StringComparison.Ordinal) &&
                !cancelled.StandardOutput.Contains("HIT ", StringComparison.Ordinal),
            "An in-flight hybrid search was not cancelled by the vault lock (exit=" +
            cancelled.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + cancelled.StandardError.Trim() +
            "; measuredMs=" + measuredMs.ToString("F0", CultureInfo.InvariantCulture) + ").");

        var unlockAgain = await RunOwnerCommandWithInputAsync(
            executable, passphrase + Environment.NewLine,
            "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(unlockAgain.ExitCode == 0 && unlockAgain.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
            "The synthetic vault did not unlock after the cancelled hybrid search.");
        var recovered = await HybridSearchJsonAsync(
            executable, fixture, prose, allowedFolderId, modelDirectory,
            limit: WorkspaceHybridSearchService.MaximumLimit).ConfigureAwait(false);
        Require(recovered.GetProperty("total").GetInt32() > 0,
            "The hybrid search did not recover after the cancelled request.");
        Console.WriteLine(
            "PASS S15-T3 cancellation and lock safety: lockedRefusal=true inFlightLockCancelled=true " +
            "noPartialDisclosure=true recovered=true measuredMs=" + measuredMs.ToString("F0", CultureInfo.InvariantCulture) +
            " lockDelayMs=" + delayMs.ToString(CultureInfo.InvariantCulture) +
            "; the integrated CLI timer includes local model hashing/load and is not an approved SLA");
    }

    private static async Task RunS15T3StaleAsync(
        string executable,
        SmokeFixture fixture,
        string allowedFolderId,
        string modelDirectory)
    {
        var status = await ReadWorkspaceStatusAsync(executable, fixture).ConfigureAwait(false);
        var treeRevision = WorkspaceNumber(status, "revision");
        var mkdir = await RunOwnerCommandAsync(
            executable, "owner", "workspace-mkdir", "--control-pipe", fixture.ControlPipe,
            "--parent-id", "root", "--name", "s15t3-stale-probe",
            "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        Require(mkdir.ExitCode == 0, "The stale probe folder could not be created.");

        var stale = await RunOwnerCommandAsync(
            executable,
            HybridArguments(fixture, "When should this be reviewed again?", allowedFolderId, modelDirectory),
            standardInput: null,
            timeout: S15T3LongCommandTimeout).ConfigureAwait(false);
        Require(stale.ExitCode == 3 &&
                stale.StandardError.Contains("workspace_index_stale", StringComparison.Ordinal) &&
                !stale.StandardOutput.Contains("HYBRID_SEARCH", StringComparison.Ordinal),
            "A stale passage generation still served a hybrid answer (exit=" +
            stale.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + stale.StandardError.Trim() + ").");

        var rebuild = await RunOwnerCommandAsync(
            executable,
            new[]
            {
                "owner", "workspace-index-rebuild", "--control-pipe", fixture.ControlPipe,
                "--model-directory", modelDirectory,
            },
            standardInput: null,
            timeout: S15T3LongCommandTimeout).ConfigureAwait(false);
        Require(rebuild.ExitCode == 0, "The stale generation did not rebuild (stderr=" + rebuild.StandardError.Trim() + ").");
        var recovered = await HybridSearchJsonAsync(
            executable, fixture, "When should this be reviewed again?", allowedFolderId, modelDirectory,
            limit: WorkspaceHybridSearchService.MaximumLimit).ConfigureAwait(false);
        Require(recovered.GetProperty("total").GetInt32() > 0,
            "The rebuilt generation did not serve the hybrid query again.");
        Console.WriteLine("PASS S15-T3 stale generation refuses explicitly and a rebuild restores hybrid answers");
    }
}
