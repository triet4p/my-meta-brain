using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MetaBrain.S1T4.Smoke;

internal static class S15T1E5CandidateEvaluation
{
    private const string CandidateRevision = "614241f622f53c4eeff9890bdc4f31cfecc418b3";
    private const string DatasetId = "s15-t1-synthetic-search-v2";
    private const int MaximumSequenceLength = 512;
    private const int EmbeddingDimensions = 384;
    private const int WarmupQueries = 10;
    private const int TimedQueries = 100;
    private const int ResultCount = 5;
    private const string FrozenCorpusSha256 = "4ec9b83ef3b0483c70cdaa51be2cedd611d31430643f8e0b97aacfb8908fa806";
    private const string FrozenQueriesSha256 = "261c017433882d54aece88f5740007ce026e9320d26ad96dbceeb30cb4a5f9be";
    private const string FrozenProtocolSha256 = "dcfd438916bf2365c79430ace8ff5483143a4d9b474ee80fbe21db066da89493";

    private static readonly Asset[] PinnedAssets =
    [
        new("onnx/model.onnx", 470_268_510, "ca456c06b3a9505ddfd9131408916dd79290368331e7d76bb621f1cba6bc8665", "SHA-256"),
        new("onnx/tokenizer.json", 17_082_730, "0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39", "SHA-256"),
        new("onnx/sentencepiece.bpe.model", 5_069_051, "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865", "SHA-256"),
        new("onnx/config.json", 653, "698e598c24523f3857295b11abec0703d19ea180", "Git blob SHA-1"),
        new("onnx/special_tokens_map.json", 167, "e0b1d18ecd0ae4ff1d47bd297d910c0cf83e504b", "Git blob SHA-1"),
        new("onnx/tokenizer_config.json", 443, "059214673d9d6d2ee319411e2ffec8c024b816d5", "Git blob SHA-1"),
        new("1_Pooling/config.json", 200, "acbd4014202bf4dbc6c03229bcbeef7c4feafb58", "Git blob SHA-1"),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private sealed class CorpusManifest
    {
        public string DatasetId { get; init; } = string.Empty;
        public bool SyntheticOnly { get; init; }
        public string AllowedFolderName { get; init; } = string.Empty;
        public string OutsideFolderName { get; init; } = string.Empty;
        public int DistractorFileCount { get; init; }
        public int DistractorBytesPerFile { get; init; }
        public List<CorpusFile> Files { get; init; } = [];
    }

    private sealed class CorpusFile
    {
        public string Key { get; init; } = string.Empty;
        public string Folder { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Revision1Content { get; init; } = string.Empty;
        public string? Revision2Content { get; init; }
    }

    private sealed class QueryManifest
    {
        public string DatasetId { get; init; } = string.Empty;
        public int DevelopmentCount { get; init; }
        public int HeldOutCount { get; init; }
        public List<EvaluationQuery> Queries { get; init; } = [];
    }

    private sealed class EvaluationQuery
    {
        public string Id { get; init; } = string.Empty;
        public string Split { get; init; } = string.Empty;
        public string Family { get; init; } = string.Empty;
        public string Query { get; init; } = string.Empty;
        public string Scope { get; init; } = string.Empty;
        public string Baseline { get; init; } = string.Empty;
        public List<ExpectedEvidence> ExpectedEvidence { get; init; } = [];
        public string? WideExpectedPath { get; init; }
    }

    private sealed class ExpectedEvidence
    {
        public string Path { get; init; } = string.Empty;
        public int Revision { get; init; }
        public string Anchor { get; init; } = string.Empty;
    }

    private sealed record Asset(string RelativePath, long ExpectedBytes, string ExpectedHash, string Algorithm);

    private sealed record CandidateDocument(
        string Path,
        string Folder,
        int Revision,
        string Content,
        long SourceBytes,
        float[]? Embedding = null);


    private sealed class OracleBundle
    {
        public string Schema { get; init; } = string.Empty;
        public string TaskId { get; init; } = string.Empty;
        public string AttemptId { get; init; } = string.Empty;
        public string AgentId { get; init; } = string.Empty;
        public string ModelRevision { get; init; } = string.Empty;
        public OracleTokenizer Tokenizer { get; init; } = new();
        public OracleInputs Inputs { get; init; } = new();
        public OracleTiming Timing { get; init; } = new();
        public List<OracleCase> Cases { get; init; } = [];
    }

    private sealed class OracleTokenizer
    {
        public string Library { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string Runtime { get; init; } = string.Empty;
        public string TokenizerJsonSha256 { get; init; } = string.Empty;
        public string LoadApi { get; init; } = string.Empty;
        public string EncodeApi { get; init; } = string.Empty;
        public string SpecialTokenSemantics { get; init; } = string.Empty;
        public string PrefixSemantics { get; init; } = string.Empty;
        public string TruncationSemantics { get; init; } = string.Empty;
        public string PaddingSemantics { get; init; } = string.Empty;
        public int MaximumSequenceLength { get; init; }
        public int BosTokenId { get; init; }
        public int EosTokenId { get; init; }
        public int PadTokenId { get; init; }
    }

    private sealed class OracleInputs
    {
        public string DatasetId { get; init; } = string.Empty;
        public string CorpusManifestSha256 { get; init; } = string.Empty;
        public string QueriesManifestSha256 { get; init; } = string.Empty;
        public string ProtocolSha256 { get; init; } = string.Empty;
        public int QueryCount { get; init; }
        public int CurrentPassageCount { get; init; }
        public int BoundaryCount { get; init; }
        public int CaseCount { get; init; }
    }

    private sealed class OracleTiming
    {
        public int TokenizationInputCount { get; init; }
        public double TokenizationMilliseconds { get; init; }
    }

    private sealed class OracleCase
    {
        public string CaseId { get; init; } = string.Empty;
        public string InputSha256 { get; init; } = string.Empty;
        public int[] RawIds { get; init; } = [];
        public int[] ModelIds { get; init; } = [];
        public int RawTokenCount { get; init; }
        public int TruncatedTokenCount { get; init; }
    }

    private sealed record PoolingAudit(
        int WordEmbeddingDimension,
        bool MeanTokens,
        bool ClassToken,
        bool MaxTokens,
        bool MeanSqrtLengthTokens);

    private sealed record OracleInput(string CaseId, string Text);

    private sealed record RuntimeSample(long PrivateBytes, long WorkingSetBytes, long PeakWorkingSetBytes, double ProcessCpuMilliseconds);
    private sealed record EmbeddingResult(
        float[] Vector,
        int TokenCount,
        int TruncatedTokenCount,
        double OnnxInferenceMilliseconds,
        double PoolingMilliseconds,
        int PaddedTokenCount);
    private sealed record HiddenStateSnapshot(int BatchSize, int SequenceLength, float[] Values);
    private sealed record NumericalComparison(
        int ComparedValues,
        double MaximumAbsoluteDifference,
        double MaximumRelativeDifference,
        bool WithinTolerance);

    private sealed record RankedDocument(string Path, int Revision, double CosineScore);

    private sealed record QueryEvaluation(
        EvaluationQuery Query,
        IReadOnlyList<RankedDocument> Rankings,
        IReadOnlyList<object> ExpectedEvidenceRanks,
        bool FoundAt1,
        bool FoundAt5,
        int TokenCount,
        double OnnxInferenceMilliseconds,
        double SearchMilliseconds);


    public static async Task RunAsync(
        string repositoryRoot,
        string modelDirectory,
        string oracleTokenIdsPath,
        string taskId,
        string attemptId,
        string agentId)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("The pinned S15-T1 ONNX candidate evaluation requires Windows x64.");
        }

        var root = Path.GetFullPath(repositoryRoot);
        var modelRoot = Path.GetFullPath(modelDirectory);
        if (!Directory.Exists(root) || !Directory.Exists(modelRoot))
        {
            throw new DirectoryNotFoundException("The repository root or pinned model directory does not exist.");
        }

        var admittedAssets = VerifyPinnedAssets(modelRoot);
        var evaluationRoot = Path.Combine(root, "artifacts", "sprint-15", "evaluation", "lexical-v2");
        var inputRoot = Path.Combine(root, "tests", "MetaBrain.S1T4.Smoke", "Fixtures", "S15T1", "lexical-v2");
        var corpusPath = Path.Combine(inputRoot, "corpus.json");
        var queriesPath = Path.Combine(inputRoot, "queries.json");
        var protocolPath = Path.Combine(inputRoot, "protocol.md");
        var protocolSha256 = ComputeSha256(protocolPath);
        var corpusSha256 = ComputeSha256(corpusPath);
        var queriesSha256 = ComputeSha256(queriesPath);
        Require(string.Equals(corpusSha256, FrozenCorpusSha256, StringComparison.Ordinal) &&
                string.Equals(queriesSha256, FrozenQueriesSha256, StringComparison.Ordinal) &&
                string.Equals(protocolSha256, FrozenProtocolSha256, StringComparison.Ordinal),
            "The source-controlled frozen lexical-v2 inputs or protocol hash changed.");
        var corpus = JsonSerializer.Deserialize<CorpusManifest>(await File.ReadAllTextAsync(corpusPath).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("The frozen lexical-v2 corpus manifest could not be read.");
        var querySet = JsonSerializer.Deserialize<QueryManifest>(await File.ReadAllTextAsync(queriesPath).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("The frozen lexical-v2 query manifest could not be read.");

        ValidateFrozenManifest(corpus, querySet);
        var documents = BuildDocuments(corpus);
        var documentsByPath = documents.ToDictionary(document => document.Path, StringComparer.Ordinal);
        var expectedEvidenceCount = querySet.Queries.Sum(query => query.ExpectedEvidence.Count);
        var lexicalHitLabels = querySet.Queries.Count(query => string.Equals(query.Baseline, "hit", StringComparison.Ordinal));
        var lexicalMissLabels = querySet.Queries.Count(query => string.Equals(query.Baseline, "miss", StringComparison.Ordinal));
        var lexicalScopeNegativeLabels = querySet.Queries.Count(query => string.Equals(query.Baseline, "scope-negative", StringComparison.Ordinal));
        Require(expectedEvidenceCount == 27 && lexicalHitLabels == 13 && lexicalMissLabels == 18 && lexicalScopeNegativeLabels == 4,
            "The lexical-v2 evidence/baseline labels differ from the frozen 27/13/18/4 comparison contract.");

        var modelConfigPadTokenId = AuditModelConfig(modelRoot);
        var poolingAudit = AuditPoolingConfig(modelRoot);

        using var process = Process.GetCurrentProcess();
        var beforeOracleTokenIds = ReadProcessSample(process);
        var oracleTokenIdsPathFull = Path.GetFullPath(oracleTokenIdsPath);
        var oracleFileLength = new FileInfo(oracleTokenIdsPathFull).Length;
        var oracleReadTimer = Stopwatch.StartNew();
        var oracleBundle = JsonSerializer.Deserialize<OracleBundle>(
                await File.ReadAllTextAsync(oracleTokenIdsPathFull).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("The pinned HF token-ID array file could not be read.");
        var oracleSequences = ValidateOracleBundle(
            oracleBundle,
            taskId,
            attemptId,
            agentId,
            admittedAssets.Single(asset => string.Equals(asset.Path, "onnx/tokenizer.json", StringComparison.Ordinal)).ActualHash,
            corpusSha256,
            queriesSha256,
            protocolSha256,
            querySet.Queries,
            documents);
        oracleReadTimer.Stop();
        var oracleFileSha256 = ComputeSha256(oracleTokenIdsPathFull);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var afterOracleTokenIds = ReadProcessSample(process);
        Console.WriteLine("Verified pinned HF token-ID arrays=" + oracleBundle.Cases.Count.ToString(CultureInfo.InvariantCulture) +
            "; query=" + oracleBundle.Inputs.QueryCount.ToString(CultureInfo.InvariantCulture) +
            "; current passages=" + oracleBundle.Inputs.CurrentPassageCount.ToString(CultureInfo.InvariantCulture) +
            "; boundary=" + oracleBundle.Inputs.BoundaryCount.ToString(CultureInfo.InvariantCulture) +
            "; tokenizer=" + oracleBundle.Tokenizer.Library + " " + oracleBundle.Tokenizer.Version +
            "; transfer bytes=" + oracleFileLength.ToString(CultureInfo.InvariantCulture) +
            "; verified file SHA-256=" + oracleFileSha256 + ".");

        var modelPath = Path.Combine(modelRoot, "onnx", "model.onnx");
        var modelLoadTimer = Stopwatch.StartNew();
        using var session = new InferenceSession(modelPath);
        modelLoadTimer.Stop();
        var sessionContract = ValidateSessionContract(session);
        var afterModelLoad = ReadProcessSample(process);

        var passageEmbeddingMilliseconds = new List<double>(documents.Count);
        var queryEncodingAndRankingMilliseconds = new List<double>(querySet.Queries.Count);
        var encodedTokenCounts = new List<int>(documents.Count + querySet.Queries.Count);
        var timedOnnxInferenceMilliseconds = new List<double>(TimedQueries);
        var timedPoolingMilliseconds = new List<double>(TimedQueries);
        var truncatedSequenceCount = 0;
        var truncatedTokenCount = 0;
        var firstPassageInferenceMilliseconds = 0d;
        var firstPassageEndToEndMilliseconds = 0d;
        var observedPaddingTokenCount = 0;
        var embeddedDocuments = new List<CandidateDocument>(documents.Count);
        for (var index = 0; index < documents.Count; index++)
        {
            var document = documents[index];
            var stopwatch = Stopwatch.StartNew();
            var passageOracleCase = oracleSequences["passage/" + document.Path];
            var embedding = EncodeAndEmbed(
                session,
                sessionContract,
                passageOracleCase,
                oracleBundle.Tokenizer.PadTokenId);
            stopwatch.Stop();
            if (index == 0)
            {
                firstPassageInferenceMilliseconds = embedding.OnnxInferenceMilliseconds;
                firstPassageEndToEndMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            }

            encodedTokenCounts.Add(embedding.TokenCount);
            truncatedSequenceCount += embedding.TruncatedTokenCount > 0 ? 1 : 0;
            observedPaddingTokenCount += embedding.PaddedTokenCount;
            truncatedTokenCount += embedding.TruncatedTokenCount;
            passageEmbeddingMilliseconds.Add(stopwatch.Elapsed.TotalMilliseconds);
            embeddedDocuments.Add(document with { Embedding = embedding.Vector });
        }

        var afterDocumentEmbeddings = ReadProcessSample(process);
        var queryInferenceMilliseconds = new List<double>(querySet.Queries.Count);
        var queryEvaluations = new List<QueryEvaluation>(querySet.Queries.Count);
        foreach (var query in querySet.Queries)
        {
            var stopwatch = Stopwatch.StartNew();
            var queryOracleCase = oracleSequences["query/" + query.Id];
            var queryEmbedding = EncodeAndEmbed(
                session,
                sessionContract,
                queryOracleCase,
                oracleBundle.Tokenizer.PadTokenId);
            var rankings = RankDocuments(queryEmbedding.Vector, embeddedDocuments, corpus.AllowedFolderName);
            stopwatch.Stop();
            queryInferenceMilliseconds.Add(queryEmbedding.OnnxInferenceMilliseconds);
            queryEncodingAndRankingMilliseconds.Add(stopwatch.Elapsed.TotalMilliseconds);
            encodedTokenCounts.Add(queryEmbedding.TokenCount);
            truncatedSequenceCount += queryEmbedding.TruncatedTokenCount > 0 ? 1 : 0;
            truncatedTokenCount += queryEmbedding.TruncatedTokenCount;
            observedPaddingTokenCount += queryEmbedding.PaddedTokenCount;

            var expectedRanks = new List<object>(query.ExpectedEvidence.Count);
            var foundAt1 = false;
            var foundAt5 = false;
            foreach (var evidence in query.ExpectedEvidence)
            {
                Require(documentsByPath.TryGetValue(evidence.Path, out var source),
                    "Frozen expected source is absent from the current corpus: " + evidence.Path);
                Require(source!.Revision == evidence.Revision,
                    "Frozen expected revision is not current for " + query.Id + " at " + evidence.Path + ".");
                Require(source.Content.Contains(evidence.Anchor, StringComparison.Ordinal),
                    "Frozen evidence anchor is absent from its current synthetic source for " + query.Id + ".");
                var rank = rankings.FindIndex(candidate =>
                    string.Equals(candidate.Path, evidence.Path, StringComparison.Ordinal) && candidate.Revision == evidence.Revision);
                var oneBasedRank = rank < 0 ? (int?)null : rank + 1;
                foundAt1 |= oneBasedRank == 1;
                foundAt5 |= oneBasedRank is <= ResultCount;
                expectedRanks.Add(new
                {
                    path = evidence.Path,
                    expectedRevision = evidence.Revision,
                    rank = oneBasedRank,
                    anchorPresentInCurrentSyntheticSource = true,
                });
            }

            if (query.WideExpectedPath is not null)
            {
                Require(documentsByPath.TryGetValue(query.WideExpectedPath, out var wideWitness) &&
                        !string.Equals(wideWitness!.Folder, corpus.AllowedFolderName, StringComparison.Ordinal),
                    "A frozen out-of-scope witness is absent or unexpectedly inside the scoped corpus.");
            }

            queryEvaluations.Add(new QueryEvaluation(
                query,
                rankings.Take(ResultCount).ToArray(),
                expectedRanks,
                foundAt1,
                foundAt5,
                queryEmbedding.TokenCount,
                queryEmbedding.OnnxInferenceMilliseconds,
                stopwatch.Elapsed.TotalMilliseconds));
        }

        var afterCorrectnessQueries = ReadProcessSample(process);
        var warmupSamples = new List<double>(WarmupQueries);
        for (var index = 0; index < WarmupQueries; index++)
        {
            var query = querySet.Queries[index % querySet.Queries.Count];
            var stopwatch = Stopwatch.StartNew();
            var embedding = EncodeAndEmbed(
                session,
                sessionContract,
                oracleSequences["query/" + query.Id],
                oracleBundle.Tokenizer.PadTokenId);
            _ = RankDocuments(embedding.Vector, embeddedDocuments, corpus.AllowedFolderName);
            stopwatch.Stop();
            warmupSamples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        var beforeTimedSample = ReadProcessSample(process);
        var cpuBefore = beforeTimedSample.ProcessCpuMilliseconds;
        var timedSamples = new double[TimedQueries];
        var timedQueriesTimer = Stopwatch.StartNew();
        for (var index = 0; index < TimedQueries; index++)
        {
            var query = querySet.Queries[index % querySet.Queries.Count];
            var stopwatch = Stopwatch.StartNew();
            var embedding = EncodeAndEmbed(
                session,
                sessionContract,
                oracleSequences["query/" + query.Id],
                oracleBundle.Tokenizer.PadTokenId);
            _ = RankDocuments(embedding.Vector, embeddedDocuments, corpus.AllowedFolderName);
            stopwatch.Stop();
            timedSamples[index] = stopwatch.Elapsed.TotalMilliseconds;
            timedOnnxInferenceMilliseconds.Add(embedding.OnnxInferenceMilliseconds);
            timedPoolingMilliseconds.Add(embedding.PoolingMilliseconds);
        }
        timedQueriesTimer.Stop();
        var afterTimedSample = ReadProcessSample(process);
        var cpuMilliseconds = afterTimedSample.ProcessCpuMilliseconds - cpuBefore;
        var cpuPercentOfOneCore = timedQueriesTimer.Elapsed.TotalMilliseconds > 0d
            ? cpuMilliseconds / timedQueriesTimer.Elapsed.TotalMilliseconds * 100d
            : 0d;
        var paddedInputProbe = RunPaddedInputProbe(
            session,
            sessionContract,
            querySet.Queries,
            documents,
            oracleSequences,
            oracleBundle.Tokenizer.PadTokenId,
            modelConfigPadTokenId);
        Console.WriteLine("Padded-input compatibility probe=" + JsonSerializer.Serialize(paddedInputProbe, JsonOptions));

        var evidenceHitsAt1 = queryEvaluations.Count(result => result.FoundAt1);
        var evidenceHitsAt5 = queryEvaluations.Count(result => result.FoundAt5);
        var familyMetrics = queryEvaluations
            .GroupBy(result => result.Query.Family, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new
            {
                family = group.Key,
                queryCount = group.Count(),
                evidenceQueryCount = group.Count(result => result.Query.ExpectedEvidence.Count > 0),
                expectedEvidenceFoundAt1 = group.Count(result => result.FoundAt1),
                expectedEvidenceFoundAt5 = group.Count(result => result.FoundAt5),
            })
            .ToArray();
        var queryDetails = queryEvaluations.Select(result => new
        {
            id = result.Query.Id,
            query = result.Query.Query,
            family = result.Query.Family,
            frozenLiteralBaseline = result.Query.Baseline,
            evidenceLabelCount = result.Query.ExpectedEvidence.Count,
            expectedEvidence = result.ExpectedEvidenceRanks,
            expectedEvidenceFoundAt1 = result.FoundAt1,
            expectedEvidenceFoundAt5 = result.FoundAt5,
            scopedCandidateCount = embeddedDocuments.Count(document =>
                string.Equals(document.Folder, result.Query.Scope, StringComparison.Ordinal)),
            top5 = result.Rankings.Select((candidate, index) => new
            {
                rank = index + 1,
                path = candidate.Path,
                revision = candidate.Revision,
                cosineScore = candidate.CosineScore,
            }),
            tokenCount = result.TokenCount,
            onnxInferenceMilliseconds = result.OnnxInferenceMilliseconds,
            nearestCandidateScoreIsDiagnosticOnly = result.Query.ExpectedEvidence.Count == 0,
            outOfScopeWitness = result.Query.WideExpectedPath,
            searchLatencyMs = result.SearchMilliseconds,
        }).ToArray();

        var resultPath = Path.Combine(evaluationRoot, "e5-candidate-results-" + attemptId + ".json");
        var result = new
        {
            schema = "s15-t1-e5-hf-oracle-candidate-results-v3",
            taskId,
            attemptId,
            agentId,
            jobId = "not exposed by the worker runtime",
            observedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            evaluation = new
            {
                route = "standalone-local-cpu-model-comparison-with-official-hf-tokenizer-id-arrays",
                modelRevision = CandidateRevision,
                modelLicenseMetadata = "MIT tag in pinned Hugging Face metadata; no legal redistribution review performed",
                datasetId = DatasetId,
                corpusManifestSha256 = corpusSha256,
                queriesManifestSha256 = queriesSha256,
                protocolSha256,
                expectedProtocolSha256 = FrozenProtocolSha256,
                expectedCorpusManifestSha256 = FrozenCorpusSha256,
                expectedQueriesManifestSha256 = FrozenQueriesSha256,
                syntheticOnly = corpus.SyntheticOnly,
                heldOutQueries = querySet.Queries.Count,
                currentWorkspaceFiles = documents.Count,
                currentWorkspaceTextBytes = documents.Sum(document => document.SourceBytes),
                retrievalUnit = "one current complete synthetic source file per passage; 100 deterministic filler files included",
                queryPrefix = "query: ",
                passagePrefix = "passage: ",
                truncation = oracleBundle.Tokenizer.TruncationSemantics + "; scored sequences=" +
                    encodedTokenCounts.Count.ToString(CultureInfo.InvariantCulture) +
                    ", max tokens=" + encodedTokenCounts.Max().ToString(CultureInfo.InvariantCulture) +
                    ", truncated sequences=" + truncatedSequenceCount.ToString(CultureInfo.InvariantCulture) +
                    ", truncated tokens=" + truncatedTokenCount.ToString(CultureInfo.InvariantCulture),
                tokenizerPaddingTokensObserved = observedPaddingTokenCount,
                pooling = "mean selected 384-D token outputs whose attention mask is 1, including BOS/EOS, then L2 normalization",
                candidateThresholdApplied = false,
                lexicalMergeApplied = false,
                productCliOrMcpCandidatePathExercised = false,
                sourceAnchorCheck = "frozen path/current revision and exact anchor substring checked against current synthetic source; no product byte-span/citation contract claimed",
                scopeHandling = "only the manifest's inside folder is ranked; out-of-folder witnesses are checked for exclusion, not scored as eligible results",
                agentScopeCandidateBehaviorMeasured = false,
                unapprovedTargetsWereNotUsedForOptimization = true,
                fullQueryLatencyIncludesOracleTokenization = false,
            },
            admission = new
            {
                allSevenPinnedFilesMatched = true,
                candidateAssetBytes = admittedAssets.Sum(asset => asset.Bytes),
                files = admittedAssets,
            },
            tokenizer = new
            {
                oracleBundlePath = Path.GetRelativePath(root, oracleTokenIdsPathFull),
                oracleBundleBytes = oracleFileLength,
                oracleBundleSha256 = oracleFileSha256,
                oracleBundleReadAndValidationMilliseconds = oracleReadTimer.Elapsed.TotalMilliseconds,
                oracleTokenizationMilliseconds = oracleBundle.Timing.TokenizationMilliseconds,
                oracleTokenizationInputCount = oracleBundle.Timing.TokenizationInputCount,
                caseCount = oracleBundle.Cases.Count,
                queryCount = oracleBundle.Inputs.QueryCount,
                currentPassageCount = oracleBundle.Inputs.CurrentPassageCount,
                boundaryCaseCount = oracleBundle.Inputs.BoundaryCount,
                rawTokenCountTotal = oracleBundle.Cases.Sum(item => item.RawTokenCount),
                truncatedSequenceCount = oracleBundle.Cases.Count(item => item.TruncatedTokenCount > 0),
                truncatedTokenCount = oracleBundle.Cases.Sum(item => item.TruncatedTokenCount),
                library = oracleBundle.Tokenizer.Library,
                version = oracleBundle.Tokenizer.Version,
                runtime = oracleBundle.Tokenizer.Runtime,
                tokenizerJsonSha256 = oracleBundle.Tokenizer.TokenizerJsonSha256,
                loadApi = oracleBundle.Tokenizer.LoadApi,
                encodeApi = oracleBundle.Tokenizer.EncodeApi,
                specialTokenSemantics = oracleBundle.Tokenizer.SpecialTokenSemantics,
                prefixSemantics = oracleBundle.Tokenizer.PrefixSemantics,
                truncationSemantics = oracleBundle.Tokenizer.TruncationSemantics,
                paddingSemantics = oracleBundle.Tokenizer.PaddingSemantics,
                maximumSequenceLength = oracleBundle.Tokenizer.MaximumSequenceLength,
                bosTokenId = oracleBundle.Tokenizer.BosTokenId,
                eosTokenId = oracleBundle.Tokenizer.EosTokenId,
                padTokenId = oracleBundle.Tokenizer.PadTokenId,
                inputCaseHashesAndIdsVerifiedAgainstCurrentFrozenInputs = true,
                plaintextInputTextsWrittenToOracleBundle = false,
                runtimeSentencePieceTokenizerUsed = false,
            },
            poolingConfig = poolingAudit,
            model = new
            {
                runtime = "Microsoft.ML.OnnxRuntime CPU package 1.31.1; default CPU execution provider",
                onnxRuntimeAssemblyVersion = typeof(InferenceSession).Assembly.GetName().Version?.ToString(),
                inputs = sessionContract.Inputs,
                selectedTokenOutput = sessionContract.OutputName,
                outputDimensions = sessionContract.OutputDimensions,
                paddingCompatibilityProbe = paddedInputProbe,
                modelConfigPadTokenId,
                tokenizerJsonPadTokenId = oracleBundle.Tokenizer.PadTokenId,
                loadMilliseconds = modelLoadTimer.Elapsed.TotalMilliseconds,
                firstPassageInferenceMilliseconds = firstPassageInferenceMilliseconds,
                firstPassageEmbeddingEndToEndMilliseconds = firstPassageEndToEndMilliseconds,
            },
            lexicalComparator = new
            {
                source = "historical actual owner CLI/service lexical-v2 run in artifacts/sprint-15/task-1.md and A1 handoff; not rerun or integrated with this standalone model process",
                frozenManifestLiteralHitLabels = lexicalHitLabels,
                frozenManifestLiteralMissLabels = lexicalMissLabels,
                frozenManifestFolderScopeNegativeLabels = lexicalScopeNegativeLabels,
                expectedEvidenceQueries = expectedEvidenceCount,
                historicalActualCliExpectedEvidenceReturned = 13,
                historicalActualCliExpectedEvidenceQueries = 27,
                historicalActualCliP50Milliseconds = 395.25,
                historicalActualCliP95Milliseconds = 464.77,
                historicalEarlierSameProtocolP95Milliseconds = 1111.10,
            },
            quality = new
            {
                expectedEvidenceQueries = expectedEvidenceCount,
                candidateExpectedEvidenceFoundAt1 = evidenceHitsAt1,
                candidateRecallAt1 = (double)evidenceHitsAt1 / expectedEvidenceCount,
                candidateExpectedEvidenceFoundAt5 = evidenceHitsAt5,
                candidateRecallAt5 = (double)evidenceHitsAt5 / expectedEvidenceCount,
                queryFamilies = familyMetrics,
                queries = queryDetails,
                negativeInterpretation = "all unlabeled hard negatives and scope negatives still have nearest neighbors; no threshold or calibrated abstention was applied, so negative safety is unresolved and not a pass",
            },
            screeningBudgets = new
            {
                decisionSource = "Main-relayed delegated S15-T1 screening decision dated 2026-11-08; unchanged frozen inputs; no tuning",
                requiredEvidenceRecallAt5 = "at least 25/27",
                evidenceRecallAt5Observed = evidenceHitsAt5 + "/" + expectedEvidenceCount,
                evidenceRecallAt5Pass = evidenceHitsAt5 >= 25,
                maximumSevenModelTokenizerConfigAssetsBytes = 500L * 1024 * 1024,
                sevenModelTokenizerConfigAssetsBytes = admittedAssets.Sum(asset => asset.Bytes),
                modelTokenizerConfigAssetBudgetPass = admittedAssets.Sum(asset => asset.Bytes) <= 500L * 1024 * 1024,
                maximumProcessPrivateBoundaryDeltaMiB = 768d,
                processPrivateBoundaryDeltaMiB = ToMiB(afterTimedSample.PrivateBytes - beforeOracleTokenIds.PrivateBytes),
                processPrivateBoundaryBudgetPass = ToMiB(afterTimedSample.PrivateBytes - beforeOracleTokenIds.PrivateBytes) <= 768d,
                maximumWarmInProcessQueryP95Milliseconds = 25d,
                warmOnnxAndRankingP95Milliseconds = Percentile(timedSamples, 0.95),
                warmOnnxAndRankingBudgetPass = Percentile(timedSamples, 0.95) <= 25d,
                fullProductCliMcpHybridPrivacyAndNegativeAbstentionAcceptance = false,
            },
            resources = new
            {
                logicalProcessorCount = Environment.ProcessorCount,
                runtimeFramework = RuntimeInformation.FrameworkDescription,
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                oracleTokenIdFileBytes = oracleFileLength,
                oracleTokenIdFileSha256 = oracleFileSha256,
                oracleTokenIdReadAndValidationMilliseconds = oracleReadTimer.Elapsed.TotalMilliseconds,
                hfTokenizationMilliseconds = oracleBundle.Timing.TokenizationMilliseconds,
                onnxSessionLoadMilliseconds = modelLoadTimer.Elapsed.TotalMilliseconds,
                passageEmbeddingEndToEndMilliseconds = SummarizeSamples(passageEmbeddingMilliseconds),
                queryOnnxInferenceMilliseconds = SummarizeSamples(queryInferenceMilliseconds),
                queryOnnxAndRankingMilliseconds = SummarizeSamples(queryEncodingAndRankingMilliseconds),
                warmupQueryOnnxAndRankingMilliseconds = SummarizeSamples(warmupSamples),
                timedQueryOnnxInferenceMilliseconds = SummarizeSamples(timedOnnxInferenceMilliseconds),
                timedQueryPoolingMilliseconds = SummarizeSamples(timedPoolingMilliseconds),
                querySearchWarmupCount = WarmupQueries,
                querySearchTimedCount = TimedQueries,
                warmQueryOnnxAndRankingMilliseconds = SummarizeSamples(timedSamples),
                querySearchWallMilliseconds = timedQueriesTimer.Elapsed.TotalMilliseconds,
                processCpuMilliseconds = cpuMilliseconds,
                processCpuPercentOfOneCore = cpuPercentOfOneCore,
                processCpuPercentOfLogicalProcessors = cpuPercentOfOneCore / Environment.ProcessorCount,
                processMemoryMiB = new
                {
                    beforeOracleTokenIds = SampleMemory(beforeOracleTokenIds),
                    afterOracleTokenIds = SampleMemory(afterOracleTokenIds),
                    afterModelLoad = SampleMemory(afterModelLoad),
                    afterPassageEmbeddings = SampleMemory(afterDocumentEmbeddings),
                    afterPassageAndCorrectnessQueries = SampleMemory(afterCorrectnessQueries),
                    beforeTimedQueries = SampleMemory(beforeTimedSample),
                    afterTimedQueries = SampleMemory(afterTimedSample),
                    privateBytesDeltaReadingOracleIds = ToMiB(afterOracleTokenIds.PrivateBytes - beforeOracleTokenIds.PrivateBytes),
                    privateBytesDeltaModelLoad = ToMiB(afterModelLoad.PrivateBytes - afterOracleTokenIds.PrivateBytes),
                    privateBytesDeltaAfterTimedQueries = ToMiB(afterTimedSample.PrivateBytes - beforeOracleTokenIds.PrivateBytes),
                    peakWorkingSetMiB = ToMiB(afterTimedSample.PeakWorkingSetBytes),
                    lifetimePeakIncludesProcessStartupAndOracleArrayTransfer = true,
                },
                limitation = "single-process boundary samples; HF tokenizer ran in a separate offline Python process; no isolated native peak tracing, indexing benchmark, installed product footprint, corpus scaling, or production traffic sample",
            },
            distribution = new
            {
                selectedForProductUse = false,
                productDependencyOrExportDecision = "not made; recommendation is recorded separately for Main",
                automaticDownloadOrProviderEgress = false,
                modelFilesAreRepoLocalStagingOnly = true,
                sixPackagePayloadBytesPreviouslyVerified = 163_378_929L,
                modelAndTokenizerAssetsBytes = admittedAssets.Sum(asset => asset.Bytes),
                combinedPinnedModelAndPackagePayloadBytes = admittedAssets.Sum(asset => asset.Bytes) + 163_378_929L,
                installedProductFootprintMeasured = false,
                packageLicenseLimit = "Package-level MIT/BSD declarations and ORT/Tokenizers third-party notices were inspected; this is not a complete redistribution legal review.",
            },
        };

        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, JsonOptions)).ConfigureAwait(false);
        Console.WriteLine("S15-T1 HF-oracle token-ID model comparison completed; standalone evaluator only, not CLI/MCP integration or gate approval.");
        Console.WriteLine("Assets admitted=7 bytes=" + admittedAssets.Sum(asset => asset.Bytes).ToString(CultureInfo.InvariantCulture) +
            "; verified oracle=" + oracleBundle.Tokenizer.Library + " " + oracleBundle.Tokenizer.Version +
            "; exact arrays=" + oracleBundle.Cases.Count.ToString(CultureInfo.InvariantCulture) +
            "; pad ID=" + oracleBundle.Tokenizer.PadTokenId.ToString(CultureInfo.InvariantCulture) + ".");
        Console.WriteLine("Candidate recall@1=" + evidenceHitsAt1.ToString(CultureInfo.InvariantCulture) + "/" + expectedEvidenceCount.ToString(CultureInfo.InvariantCulture) +
            " recall@5=" + evidenceHitsAt5.ToString(CultureInfo.InvariantCulture) + "/" + expectedEvidenceCount.ToString(CultureInfo.InvariantCulture) +
            "; historical lexical actual CLI=" + 13.ToString(CultureInfo.InvariantCulture) + "/27.");
        Console.WriteLine("Model load ms=" + modelLoadTimer.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture) +
            "; first passage ms=" + firstPassageInferenceMilliseconds.ToString("F2", CultureInfo.InvariantCulture) +
            "; query search warm p50/p95 ms=" + Percentile(timedSamples, 0.50).ToString("F2", CultureInfo.InvariantCulture) +
            "/" + Percentile(timedSamples, 0.95).ToString("F2", CultureInfo.InvariantCulture) +
            "; process CPU one-core %=" + cpuPercentOfOneCore.ToString("F2", CultureInfo.InvariantCulture) + ".");
        Console.WriteLine("Results: " + resultPath);
    }

    private static IReadOnlyList<AdmittedAsset> VerifyPinnedAssets(string modelRoot)
    {
        var admitted = new List<AdmittedAsset>(PinnedAssets.Length);
        foreach (var asset in PinnedAssets)
        {
            var path = Path.Combine(modelRoot, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new FileNotFoundException("Pinned model admission failed; file missing: " + asset.RelativePath, path);
            }

            if (info.Length != asset.ExpectedBytes)
            {
                throw new InvalidDataException("Pinned model admission size mismatch for " + asset.RelativePath +
                    ": actual=" + info.Length.ToString(CultureInfo.InvariantCulture) +
                    " expected=" + asset.ExpectedBytes.ToString(CultureInfo.InvariantCulture));
            }

            var actualHash = string.Equals(asset.Algorithm, "SHA-256", StringComparison.Ordinal)
                ? ComputeSha256(path)
                : ComputeGitBlobSha1(path, info.Length);
            if (!string.Equals(actualHash, asset.ExpectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Pinned model admission hash mismatch for " + asset.RelativePath +
                    ": actual=" + actualHash + " expected=" + asset.ExpectedHash);
            }

            admitted.Add(new AdmittedAsset(asset.RelativePath, info.Length, asset.Algorithm, actualHash));
            Console.WriteLine("Admitted " + asset.RelativePath + " bytes=" + info.Length.ToString(CultureInfo.InvariantCulture) +
                " " + asset.Algorithm + "=" + actualHash);
        }

        if (admitted.Count != 7)
        {
            throw new InvalidDataException("The pinned model manifest must contain exactly seven admitted files.");
        }

        return admitted;
    }

    private sealed record AdmittedAsset(string Path, long Bytes, string Algorithm, string ActualHash);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var algorithm = SHA256.Create();
        return Convert.ToHexString(algorithm.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string ComputeGitBlobSha1(string path, long expectedBytes)
    {
        var header = Encoding.ASCII.GetBytes("blob " + expectedBytes.ToString(CultureInfo.InvariantCulture) + "\0");
        using var stream = File.OpenRead(path);
        using var algorithm = SHA1.Create();
        algorithm.TransformBlock(header, 0, header.Length, header, 0);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            algorithm.TransformBlock(buffer, 0, read, buffer, 0);
        }

        algorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(algorithm.Hash ?? throw new CryptographicException("Git blob SHA-1 was not produced."))
            .ToLowerInvariant();
    }

    private static void ValidateFrozenManifest(CorpusManifest corpus, QueryManifest querySet)
    {
        Require(corpus.SyntheticOnly, "The S15-T1 candidate runner accepts only a synthetic corpus.");
        Require(string.Equals(corpus.DatasetId, DatasetId, StringComparison.Ordinal) &&
                string.Equals(querySet.DatasetId, DatasetId, StringComparison.Ordinal),
            "The frozen lexical-v2 dataset identity changed.");
        Require(string.Equals(corpus.AllowedFolderName, "inside", StringComparison.Ordinal) &&
                string.Equals(corpus.OutsideFolderName, "outside", StringComparison.Ordinal),
            "The frozen lexical-v2 scope contract changed.");
        Require(corpus.Files.Count == 12 && corpus.DistractorFileCount == 100 && corpus.DistractorBytesPerFile == 1_024,
            "The frozen lexical-v2 corpus workload changed.");
        Require(querySet.DevelopmentCount == 0 && querySet.HeldOutCount == 35 && querySet.Queries.Count == 35 &&
                querySet.Queries.All(query => string.Equals(query.Split, "held-out", StringComparison.Ordinal)),
            "The frozen lexical-v2 held-out query contract changed.");
        Require(querySet.Queries.All(query => string.Equals(query.Scope, corpus.AllowedFolderName, StringComparison.Ordinal)),
            "The candidate evaluator only implements the frozen inside-folder query scope.");
        Require(querySet.Queries.Select(query => query.Id).Distinct(StringComparer.Ordinal).Count() == querySet.Queries.Count,
            "The frozen query IDs are not unique.");
    }

    private static List<CandidateDocument> BuildDocuments(CorpusManifest corpus)
    {
        var documents = new List<CandidateDocument>(corpus.Files.Count + corpus.DistractorFileCount);
        foreach (var file in corpus.Files)
        {
            var revision = file.Revision2Content is null ? 1 : 2;
            var content = file.Revision2Content ?? file.Revision1Content;
            var path = file.Folder + "/" + file.Name;
            documents.Add(new CandidateDocument(path, file.Folder, revision, content, Encoding.UTF8.GetByteCount(content)));
        }

        for (var index = 1; index <= corpus.DistractorFileCount; index++)
        {
            var token = Encoding.ASCII.GetBytes("qzxvnoise" + index.ToString("D3", CultureInfo.InvariantCulture) + " ");
            var bytes = new byte[corpus.DistractorBytesPerFile];
            for (var offset = 0; offset < bytes.Length;)
            {
                var copyLength = Math.Min(token.Length, bytes.Length - offset);
                token.AsSpan(0, copyLength).CopyTo(bytes.AsSpan(offset));
                offset += copyLength;
            }

            var name = "distractor-" + index.ToString("D3", CultureInfo.InvariantCulture) + ".txt";
            documents.Add(new CandidateDocument(
                corpus.AllowedFolderName + "/" + name,
                corpus.AllowedFolderName,
                1,
                Encoding.ASCII.GetString(bytes),
                bytes.LongLength));
        }

        documents.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        var sourceBytes = documents.Sum(document => document.SourceBytes);
        Require(documents.Count == 112 && sourceBytes == 105_386,
            "The current frozen workspace size does not match the 112-file / 105,386-byte v2 workload.");
        return documents;
    }
    private static List<OracleInput> BuildOracleInputs(
        IReadOnlyList<EvaluationQuery> queries,
        IReadOnlyList<CandidateDocument> documents)
    {
        var inputs = new List<OracleInput>(queries.Count + documents.Count + 6);
        foreach (var query in queries)
        {
            inputs.Add(new OracleInput("query/" + query.Id, "query: " + query.Query));
        }

        foreach (var document in documents)
        {
            inputs.Add(new OracleInput("passage/" + document.Path, "passage: " + document.Content));
        }

        inputs.Add(new OracleInput("edge/whitespace", " \t  repeated\u00A0whitespace\r\n line  "));
        inputs.Add(new OracleInput("edge/unicode", "T\u00F4i mu\u1ED1n t\u00ECm l\u1EA1i th\u00F4ng tin \u2014 caf\u00E9 e\u0301; \u6771\u4EAC \U0001F600"));
        inputs.Add(new OracleInput("edge/special", "<s> <pad> </s> <unk> <mask>"));
        foreach (var repeatedTokenCount in new[] { 509, 510, 511 })
        {
            inputs.Add(new OracleInput(
                "edge/truncate-" + repeatedTokenCount.ToString(CultureInfo.InvariantCulture) + "-tokens",
                string.Join(" ", Enumerable.Repeat("a", repeatedTokenCount))));
        }

        return inputs;
    }

    private static Dictionary<string, OracleCase> ValidateOracleBundle(
        OracleBundle bundle,
        string taskId,
        string attemptId,
        string agentId,
        string expectedTokenizerSha256,
        string corpusSha256,
        string queriesSha256,
        string protocolSha256,
        IReadOnlyList<EvaluationQuery> queries,
        IReadOnlyList<CandidateDocument> documents)
    {
        Require(string.Equals(taskId, "S15-T1", StringComparison.Ordinal) &&
                string.Equals(bundle.Schema, "s15-t1-hf-token-ids-v1", StringComparison.Ordinal) &&
                string.Equals(bundle.TaskId, taskId, StringComparison.Ordinal) &&
                string.Equals(bundle.AttemptId, attemptId, StringComparison.Ordinal) &&
                string.Equals(bundle.AgentId, agentId, StringComparison.Ordinal) &&
                string.Equals(bundle.ModelRevision, CandidateRevision, StringComparison.Ordinal),
            "The HF oracle array task, attempt, agent, or model identity does not match this invocation.");
        Require(string.Equals(bundle.Tokenizer.Library, "Hugging Face tokenizers", StringComparison.Ordinal) &&
                string.Equals(bundle.Tokenizer.Version, "0.22.1", StringComparison.Ordinal) &&
                string.Equals(bundle.Tokenizer.TokenizerJsonSha256, expectedTokenizerSha256, StringComparison.Ordinal) &&
                string.Equals(bundle.Tokenizer.LoadApi, "tokenizers.Tokenizer.from_file(local pinned tokenizer.json)", StringComparison.Ordinal) &&
                string.Equals(bundle.Tokenizer.EncodeApi, "Tokenizer.encode(text, add_special_tokens=True)", StringComparison.Ordinal) &&
                string.Equals(bundle.Tokenizer.SpecialTokenSemantics,
                    "tokenizer.json TemplateProcessing adds BOS <s>=0 and EOS </s>=2; literal special-token strings follow the pinned tokenizer.json pipeline",
                    StringComparison.Ordinal) &&
                string.Equals(bundle.Tokenizer.PrefixSemantics,
                    "frozen query inputs prepend exactly 'query: '; current passage inputs prepend exactly 'passage: '; boundary inputs have no added prefix",
                    StringComparison.Ordinal) &&
                string.Equals(bundle.Tokenizer.TruncationSemantics,
                    "right-truncate the full special-token-inclusive ID list to 512; when truncated replace the final retained ID with EOS=2",
                    StringComparison.Ordinal) &&
                string.Equals(bundle.Tokenizer.PaddingSemantics,
                    "oracle arrays are unpadded; ONNX evaluator uses tokenizer.json pad ID 1 and attention-mask value 0 only for batch/static padding",
                    StringComparison.Ordinal) &&
                bundle.Tokenizer.MaximumSequenceLength == MaximumSequenceLength &&
                bundle.Tokenizer.BosTokenId == 0 &&
                bundle.Tokenizer.EosTokenId == 2 &&
                bundle.Tokenizer.PadTokenId == 1,
            "The HF tokenizer package, pinned tokenizer.json, API, semantics, length, or special/pad ID contract is invalid.");
        Require(string.Equals(bundle.Inputs.DatasetId, DatasetId, StringComparison.Ordinal) &&
                string.Equals(bundle.Inputs.CorpusManifestSha256, corpusSha256, StringComparison.Ordinal) &&
                string.Equals(bundle.Inputs.QueriesManifestSha256, queriesSha256, StringComparison.Ordinal) &&
                string.Equals(bundle.Inputs.ProtocolSha256, protocolSha256, StringComparison.Ordinal) &&
                bundle.Inputs.QueryCount == 35 &&
                bundle.Inputs.CurrentPassageCount == 112 &&
                bundle.Inputs.BoundaryCount == 6 &&
                bundle.Inputs.CaseCount == 153 &&
                bundle.Tokenizer.Runtime.Length > 0 &&
                bundle.Timing.TokenizationInputCount == 153 &&
                double.IsFinite(bundle.Timing.TokenizationMilliseconds) &&
                bundle.Timing.TokenizationMilliseconds >= 0d,
            "The HF oracle array input hashes, counts, or runtime timing do not match the frozen fixture.");

        var expectedInputs = BuildOracleInputs(queries, documents);
        Require(expectedInputs.Count == 153 && bundle.Cases.Count == expectedInputs.Count,
            "The HF oracle array bundle does not contain exactly 153 frozen and boundary inputs.");
        var casesById = new Dictionary<string, OracleCase>(bundle.Cases.Count, StringComparer.Ordinal);
        foreach (var oracleCase in bundle.Cases)
        {
            Require(casesById.TryAdd(oracleCase.CaseId, oracleCase),
                "The HF oracle array bundle contains a duplicate case ID: " + oracleCase.CaseId);
        }

        foreach (var input in expectedInputs)
        {
            Require(casesById.TryGetValue(input.CaseId, out var oracleCase),
                "The HF oracle array bundle omits frozen case " + input.CaseId + ".");
            var expectedInputSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Text))).ToLowerInvariant();
            Require(string.Equals(oracleCase!.InputSha256, expectedInputSha256, StringComparison.Ordinal),
                "The HF oracle input hash does not match the current frozen text in " + input.CaseId + ".");
            Require(oracleCase.RawIds.Length == oracleCase.RawTokenCount &&
                    oracleCase.RawIds.Length >= 2 &&
                    oracleCase.RawIds[0] == bundle.Tokenizer.BosTokenId &&
                    oracleCase.RawIds[^1] == bundle.Tokenizer.EosTokenId &&
                    oracleCase.ModelIds.Length > 0 &&
                    oracleCase.ModelIds.Length <= MaximumSequenceLength &&
                    oracleCase.TruncatedTokenCount == oracleCase.RawIds.Length - oracleCase.ModelIds.Length &&
                    oracleCase.RawIds.All(id => id >= 0 && id < 250_002) &&
                    oracleCase.ModelIds.All(id => id >= 0 && id < 250_002),
                "The HF oracle token IDs or special/truncation metadata are invalid in " + input.CaseId + ".");

            var expectedModelIds = oracleCase.RawIds.Take(MaximumSequenceLength).ToArray();
            if (oracleCase.RawIds.Length > MaximumSequenceLength)
            {
                expectedModelIds[^1] = bundle.Tokenizer.EosTokenId;
            }

            Require(oracleCase.ModelIds.SequenceEqual(expectedModelIds),
                "The HF oracle model IDs do not follow the frozen right-truncation contract in " + input.CaseId + ".");
        }

        Require(casesById["edge/truncate-509-tokens"].RawTokenCount == 511 &&
                casesById["edge/truncate-510-tokens"].RawTokenCount == 512 &&
                casesById["edge/truncate-511-tokens"].RawTokenCount == 513,
            "The pinned HF tokenizer did not preserve the expected BOS/EOS-inclusive truncation boundary vectors.");
        return casesById;
    }

    private static int AuditModelConfig(string modelRoot)
    {
        var modelConfigPath = Path.Combine(modelRoot, "onnx", "config.json");
        var tokenizerConfigPath = Path.Combine(modelRoot, "onnx", "tokenizer_config.json");
        using var modelConfig = JsonDocument.Parse(File.ReadAllText(modelConfigPath));
        using var tokenizerConfig = JsonDocument.Parse(File.ReadAllText(tokenizerConfigPath));
        var model = modelConfig.RootElement;
        var tokenizer = tokenizerConfig.RootElement;
        Require(model.GetProperty("hidden_size").GetInt32() == EmbeddingDimensions &&
                model.GetProperty("max_position_embeddings").GetInt32() == MaximumSequenceLength &&
                model.GetProperty("vocab_size").GetInt32() >= 250_002 &&
                string.Equals(model.GetProperty("tokenizer_class").GetString(), "XLMRobertaTokenizer", StringComparison.Ordinal),
            "The pinned model config no longer matches the 384-dimensional, 512-token XLM-R contract.");
        Require(tokenizer.GetProperty("model_max_length").GetInt32() == MaximumSequenceLength &&
                string.Equals(tokenizer.GetProperty("tokenizer_class").GetString(), "XLMRobertaTokenizer", StringComparison.Ordinal) &&
                string.Equals(tokenizer.GetProperty("bos_token").GetString(), "<s>", StringComparison.Ordinal) &&
                string.Equals(tokenizer.GetProperty("eos_token").GetString(), "</s>", StringComparison.Ordinal) &&
                string.Equals(tokenizer.GetProperty("pad_token").GetString(), "<pad>", StringComparison.Ordinal) &&
                string.Equals(tokenizer.GetProperty("unk_token").GetString(), "<unk>", StringComparison.Ordinal),
            "The pinned tokenizer config no longer matches its XLM-R length and special-token contract.");
        var modelPadTokenId = model.GetProperty("pad_token_id").GetInt32();
        Require(modelPadTokenId == 0, "The pinned model config pad-token ID changed.");
        return modelPadTokenId;
    }
    private static PoolingAudit AuditPoolingConfig(string modelRoot)
    {
        using var stream = File.OpenRead(Path.Combine(modelRoot, "1_Pooling", "config.json"));
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var audit = new PoolingAudit(
            root.GetProperty("word_embedding_dimension").GetInt32(),
            root.GetProperty("pooling_mode_mean_tokens").GetBoolean(),
            root.GetProperty("pooling_mode_cls_token").GetBoolean(),
            root.GetProperty("pooling_mode_max_tokens").GetBoolean(),
            root.GetProperty("pooling_mode_mean_sqrt_len_tokens").GetBoolean());
        Require(audit.WordEmbeddingDimension == EmbeddingDimensions &&
                audit.MeanTokens &&
                !audit.ClassToken &&
                !audit.MaxTokens &&
                !audit.MeanSqrtLengthTokens,
            "The pinned Sentence-Transformers pooling config does not specify 384-dimensional mean-token pooling only.");
        return audit;
    }



    private sealed record SessionContract(
        IReadOnlyList<object> Inputs,
        string OutputName,
        int[] OutputDimensions);

    private static SessionContract ValidateSessionContract(InferenceSession session)
    {
        var inputNames = session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal);
        Require(inputNames.Contains("input_ids") && inputNames.Contains("attention_mask"),
            "The pinned ONNX graph must expose input_ids and attention_mask inputs.");
        Require(inputNames.All(name => name is "input_ids" or "attention_mask" or "token_type_ids"),
            "The pinned ONNX graph exposes an unsupported required input: " +
            string.Join(",", inputNames.Where(name => name is not ("input_ids" or "attention_mask" or "token_type_ids"))));
        foreach (var input in session.InputMetadata)
        {
            Require(input.Value.Dimensions.Length == 2,
                "The pinned ONNX graph input must have [batch, sequence] shape: " + input.Key);
            Require(input.Value.ElementType == typeof(long) || input.Value.ElementType == typeof(int),
                "The pinned ONNX graph has an unsupported integer element type: " + input.Key +
                "=" + input.Value.ElementType);
        }

        var hiddenOutputs = session.OutputMetadata
            .Where(output => output.Value.Dimensions.Length == 3 &&
                (output.Value.Dimensions[2] < 0 || output.Value.Dimensions[2] == EmbeddingDimensions) &&
                output.Value.ElementType == typeof(float))
            .ToArray();
        Require(hiddenOutputs.Length == 1,
            "The pinned ONNX graph must expose exactly one float32 token-hidden-state output with 384 dimensions.");
        var selectedOutput = hiddenOutputs[0];
        return new SessionContract(
            session.InputMetadata.Select(input => (object)new
            {
                name = input.Key,
                elementType = input.Value.ElementType.ToString(),
                dimensions = input.Value.Dimensions,
            }).ToArray(),
            selectedOutput.Key,
            selectedOutput.Value.Dimensions);
    }

    private static EmbeddingResult EncodeAndEmbed(
        InferenceSession session,
        SessionContract contract,
        OracleCase oracleCase,
        int padTokenId)
    {
        var ids = oracleCase.ModelIds;
        var tokenCount = ids.Length;
        var inputs = new List<NamedOnnxValue>(session.InputMetadata.Count);
        var paddedTokenCount = 0;
        foreach (var input in session.InputMetadata)
        {
            var modelSequenceLength = input.Value.Dimensions[1];
            var inputSequenceLength = modelSequenceLength > 0 ? modelSequenceLength : ids.Length;
            paddedTokenCount = Math.Max(paddedTokenCount, inputSequenceLength - ids.Length);
            inputs.Add(CreateInput(input.Key, input.Value, ids, padTokenId));
        }

        var inferenceTimer = Stopwatch.StartNew();
        using var outputs = session.Run(inputs);
        inferenceTimer.Stop();
        var selected = outputs.SingleOrDefault(output => string.Equals(output.Name, contract.OutputName, StringComparison.Ordinal))
            ?? throw new InvalidDataException("ONNX inference did not return its validated token-hidden-state output.");
        if (selected.AsTensor<float>() is not DenseTensor<float> outputTensor)
        {
            throw new InvalidDataException("ONNX token-hidden-state output is not a dense float tensor.");
        }

        var poolingTimer = Stopwatch.StartNew();
        var dimensions = outputTensor.Dimensions;
        Require(dimensions.Length == 3 && dimensions[0] == 1 && dimensions[2] == EmbeddingDimensions && dimensions[1] >= ids.Length,
            "ONNX token-hidden-state output shape does not match the validated [1, sequence, 384] contract.");
        var buffer = outputTensor.Buffer.Span;
        Require(buffer.Length >= dimensions[1] * EmbeddingDimensions,
            "ONNX token-hidden-state output buffer is shorter than its reported shape.");
        var pooled = new float[EmbeddingDimensions];
        for (var tokenIndex = 0; tokenIndex < ids.Length; tokenIndex++)
        {
            var offset = tokenIndex * EmbeddingDimensions;
            for (var dimension = 0; dimension < EmbeddingDimensions; dimension++)
            {
                pooled[dimension] += buffer[offset + dimension];
            }
        }

        double normSquared = 0d;
        var inverseTokenCount = 1f / ids.Length;
        for (var dimension = 0; dimension < pooled.Length; dimension++)
        {
            pooled[dimension] *= inverseTokenCount;
            normSquared += (double)pooled[dimension] * pooled[dimension];
        }

        Require(normSquared > 0d && double.IsFinite(normSquared),
            "The masked-mean pooled E5 embedding has a non-finite or zero norm.");
        var inverseNorm = 1d / Math.Sqrt(normSquared);
        for (var dimension = 0; dimension < pooled.Length; dimension++)
        {
            pooled[dimension] = (float)(pooled[dimension] * inverseNorm);
        }

        poolingTimer.Stop();
        return new EmbeddingResult(
            pooled,
            tokenCount,
            oracleCase.TruncatedTokenCount,
            inferenceTimer.Elapsed.TotalMilliseconds,
            poolingTimer.Elapsed.TotalMilliseconds,
            paddedTokenCount);
    }
    private static object RunPaddedInputProbe(
        InferenceSession session,
        SessionContract contract,
        IReadOnlyList<EvaluationQuery> queries,
        IReadOnlyList<CandidateDocument> documents,
        IReadOnlyDictionary<string, OracleCase> oracleSequences,
        int tokenizerJsonPadTokenId,
        int modelConfigPadTokenId)
    {
        const double absoluteTolerance = 1e-5;
        const double relativeTolerance = 1e-5;
        Require(tokenizerJsonPadTokenId == 1 && modelConfigPadTokenId == 0,
            "The pinned pad-ID discrepancy changed before the compatibility probe.");

        var shortQuery = queries
            .OrderBy(query => query.Query.Length)
            .ThenBy(query => query.Id, StringComparer.Ordinal)
            .First();
        var longDocument = documents
            .OrderByDescending(document => document.Content.Length)
            .ThenBy(document => document.Path, StringComparer.Ordinal)
            .First();
        var shortOracleCase = oracleSequences["query/" + shortQuery.Id];
        var shortIds = shortOracleCase.ModelIds;
        var shortTruncatedTokenCount = shortOracleCase.TruncatedTokenCount;
        var longOracleCase = oracleSequences["passage/" + longDocument.Path];
        var longIds = longOracleCase.ModelIds;
        var longTruncatedTokenCount = longOracleCase.TruncatedTokenCount;
        Require(shortIds.Length < longIds.Length,
            "The deterministic short/long padding probe did not produce different sequence lengths.");

        var singleShortInputs = session.InputMetadata
            .Select(input => CreateInput(input.Key, input.Value, shortIds, tokenizerJsonPadTokenId))
            .ToList();
        var singleLongInputs = session.InputMetadata
            .Select(input => CreateInput(input.Key, input.Value, longIds, tokenizerJsonPadTokenId))
            .ToList();
        var shortTimer = Stopwatch.StartNew();
        var shortOutput = RunHiddenStates(session, contract, singleShortInputs, 1, shortIds.Length);
        shortTimer.Stop();
        var longTimer = Stopwatch.StartNew();
        var longOutput = RunHiddenStates(session, contract, singleLongInputs, 1, longIds.Length);
        longTimer.Stop();

        var paddedLength = longIds.Length;
        var batchSequences = new[] { shortIds, longIds };
        var batchInputs = session.InputMetadata
            .Select(input => CreateBatchInput(
                input.Key,
                input.Value,
                batchSequences,
                paddedLength,
                tokenizerJsonPadTokenId))
            .ToList();
        var batchTimer = Stopwatch.StartNew();
        HiddenStateSnapshot batchOutput;
        try
        {
            batchOutput = RunHiddenStates(session, contract, batchInputs, 2, paddedLength);
        }
        catch (OnnxRuntimeException exception)
        {
            batchTimer.Stop();
            return new
            {
                batchRunSucceeded = false,
                batchRunMilliseconds = batchTimer.Elapsed.TotalMilliseconds,
                batchFailureType = exception.GetType().FullName,
                batchFailure = exception.Message,
                modelConfigPadTokenId,
                tokenizerJsonPadTokenId,
                inputPaddingTokenId = tokenizerJsonPadTokenId,
                attentionMaskPaddingValue = 0,
                batchSize = 2,
                paddedSequenceLength = paddedLength,
                inputTextOmitted = true,
            };
        }

        batchTimer.Stop();
        var shortValidOutputComparison = CompareValues(
            CopyValidTokenValues(shortOutput, 0, shortIds.Length),
            CopyValidTokenValues(batchOutput, 0, shortIds.Length),
            absoluteTolerance,
            relativeTolerance);
        var longValidOutputComparison = CompareValues(
            CopyValidTokenValues(longOutput, 0, longIds.Length),
            CopyValidTokenValues(batchOutput, 1, longIds.Length),
            absoluteTolerance,
            relativeTolerance);
        var shortPooledComparison = CompareValues(
            PoolAndNormalize(shortOutput, 0, shortIds.Length),
            PoolAndNormalize(batchOutput, 0, shortIds.Length),
            absoluteTolerance,
            relativeTolerance);
        var longPooledComparison = CompareValues(
            PoolAndNormalize(longOutput, 0, longIds.Length),
            PoolAndNormalize(batchOutput, 1, longIds.Length),
            absoluteTolerance,
            relativeTolerance);
        return new
        {
            batchRunSucceeded = true,
            inputTextOmitted = true,
            modelConfigPadTokenId,
            tokenizerJsonPadTokenId,
            inputPaddingTokenId = tokenizerJsonPadTokenId,
            attentionMaskPaddingValue = 0,
            batchSize = 2,
            paddedSequenceLength = paddedLength,
            tolerances = new
            {
                absolute = absoluteTolerance,
                relative = relativeTolerance,
                rule = "each value passes when absoluteDelta <= absoluteTolerance + relativeTolerance * abs(unpaddedValue)",
            },
            shortCase = new
            {
                caseId = "query/" + shortQuery.Id,
                validTokenCount = shortIds.Length,
                paddedTokenCount = paddedLength - shortIds.Length,
                truncatedTokenCount = shortTruncatedTokenCount,
                unpaddedInferenceMilliseconds = shortTimer.Elapsed.TotalMilliseconds,
                validTokenOutputComparison = shortValidOutputComparison,
                pooledEmbeddingComparison = shortPooledComparison,
            },
            longCase = new
            {
                caseId = "passage/" + longDocument.Path,
                validTokenCount = longIds.Length,
                paddedTokenCount = paddedLength - longIds.Length,
                truncatedTokenCount = longTruncatedTokenCount,
                unpaddedInferenceMilliseconds = longTimer.Elapsed.TotalMilliseconds,
                validTokenOutputComparison = longValidOutputComparison,
                pooledEmbeddingComparison = longPooledComparison,
            },
            paddedBatchInferenceMilliseconds = batchTimer.Elapsed.TotalMilliseconds,
            allValidTokenOutputsWithinTolerance =
                shortValidOutputComparison.WithinTolerance && longValidOutputComparison.WithinTolerance,
            allPooledEmbeddingsWithinTolerance =
                shortPooledComparison.WithinTolerance && longPooledComparison.WithinTolerance,
        };
    }

    private static NamedOnnxValue CreateBatchInput(
        string name,
        NodeMetadata metadata,
        IReadOnlyList<int[]> sequences,
        int sequenceLength,
        int padTokenId)
    {
        var batchSize = sequences.Count;
        var modelDimensions = metadata.Dimensions;
        Require(modelDimensions.Length == 2 &&
                (modelDimensions[0] < 0 || modelDimensions[0] == batchSize) &&
                (modelDimensions[1] < 0 || modelDimensions[1] == sequenceLength),
            "The pinned ONNX graph does not accept the requested dynamic batch/sequence dimensions.");
        var dimensions = new[] { batchSize, sequenceLength };
        var values = new long[checked(batchSize * sequenceLength)];
        if (string.Equals(name, "input_ids", StringComparison.Ordinal))
        {
            Array.Fill(values, padTokenId);
            for (var row = 0; row < batchSize; row++)
            {
                Array.Copy(sequences[row], 0, values, row * sequenceLength, sequences[row].Length);
            }
        }
        else if (string.Equals(name, "attention_mask", StringComparison.Ordinal))
        {
            for (var row = 0; row < batchSize; row++)
            {
                Array.Fill(values, 1L, row * sequenceLength, sequences[row].Length);
            }
        }
        else if (!string.Equals(name, "token_type_ids", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unsupported pinned ONNX input name: " + name);
        }

        if (metadata.ElementType == typeof(long))
        {
            return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(values, dimensions));
        }

        Require(metadata.ElementType == typeof(int),
            "Unsupported pinned ONNX integer tensor type: " + metadata.ElementType);
        var intValues = new int[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            intValues[index] = checked((int)values[index]);
        }

        return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(intValues, dimensions));
    }

    private static HiddenStateSnapshot RunHiddenStates(
        InferenceSession session,
        SessionContract contract,
        IReadOnlyList<NamedOnnxValue> inputs,
        int expectedBatchSize,
        int expectedSequenceLength)
    {
        using var outputs = session.Run(inputs);
        var selected = outputs.SingleOrDefault(output => string.Equals(output.Name, contract.OutputName, StringComparison.Ordinal))
            ?? throw new InvalidDataException("ONNX inference did not return its validated token-hidden-state output.");
        if (selected.AsTensor<float>() is not DenseTensor<float> outputTensor)
        {
            throw new InvalidDataException("ONNX token-hidden-state output is not a dense float tensor.");
        }

        var dimensions = outputTensor.Dimensions;
        Require(dimensions.Length == 3 &&
                dimensions[0] == expectedBatchSize &&
                dimensions[1] == expectedSequenceLength &&
                dimensions[2] == EmbeddingDimensions,
            "ONNX token-hidden-state output shape differs from the requested batch/sequence probe.");
        return new HiddenStateSnapshot(
            dimensions[0],
            dimensions[1],
            outputTensor.Buffer.Span.ToArray());
    }

    private static float[] CopyValidTokenValues(HiddenStateSnapshot output, int row, int tokenCount)
    {
        var values = new float[checked(tokenCount * EmbeddingDimensions)];
        for (var tokenIndex = 0; tokenIndex < tokenCount; tokenIndex++)
        {
            var sourceOffset = checked((row * output.SequenceLength + tokenIndex) * EmbeddingDimensions);
            var destinationOffset = tokenIndex * EmbeddingDimensions;
            Array.Copy(output.Values, sourceOffset, values, destinationOffset, EmbeddingDimensions);
        }

        return values;
    }

    private static float[] PoolAndNormalize(HiddenStateSnapshot output, int row, int tokenCount)
    {
        var pooled = new float[EmbeddingDimensions];
        for (var tokenIndex = 0; tokenIndex < tokenCount; tokenIndex++)
        {
            var offset = checked((row * output.SequenceLength + tokenIndex) * EmbeddingDimensions);
            for (var dimension = 0; dimension < EmbeddingDimensions; dimension++)
            {
                pooled[dimension] += output.Values[offset + dimension];
            }
        }

        var inverseTokenCount = 1f / tokenCount;
        double normSquared = 0d;
        for (var dimension = 0; dimension < pooled.Length; dimension++)
        {
            pooled[dimension] *= inverseTokenCount;
            normSquared += (double)pooled[dimension] * pooled[dimension];
        }

        Require(normSquared > 0d && double.IsFinite(normSquared),
            "The padded-input probe produced a non-finite or zero-norm pooled embedding.");
        var inverseNorm = 1d / Math.Sqrt(normSquared);
        for (var dimension = 0; dimension < pooled.Length; dimension++)
        {
            pooled[dimension] = (float)(pooled[dimension] * inverseNorm);
        }

        return pooled;
    }

    private static NumericalComparison CompareValues(
        IReadOnlyList<float> expected,
        IReadOnlyList<float> actual,
        double absoluteTolerance,
        double relativeTolerance)
    {
        Require(expected.Count == actual.Count, "Numerical probe output lengths differ.");
        var maximumAbsoluteDifference = 0d;
        var maximumRelativeDifference = 0d;
        var withinTolerance = true;
        for (var index = 0; index < expected.Count; index++)
        {
            var expectedValue = expected[index];
            var actualValue = actual[index];
            Require(float.IsFinite(expectedValue) && float.IsFinite(actualValue),
                "Numerical probe output contains a non-finite hidden value.");
            var absoluteDifference = Math.Abs((double)expectedValue - actualValue);
            var relativeDifference = absoluteDifference / Math.Max(Math.Abs((double)expectedValue), 1e-12);
            maximumAbsoluteDifference = Math.Max(maximumAbsoluteDifference, absoluteDifference);
            maximumRelativeDifference = Math.Max(maximumRelativeDifference, relativeDifference);
            if (absoluteDifference > absoluteTolerance + relativeTolerance * Math.Abs((double)expectedValue))
            {
                withinTolerance = false;
            }
        }

        return new NumericalComparison(
            expected.Count,
            maximumAbsoluteDifference,
            maximumRelativeDifference,
            withinTolerance);
    }

    private static NamedOnnxValue CreateInput(string name, NodeMetadata metadata, int[] ids, int padTokenId)
    {
        var modelDimensions = metadata.Dimensions;
        var batchSize = modelDimensions[0] > 0 ? modelDimensions[0] : 1;
        Require(batchSize == 1, "The pinned graph has a fixed batch dimension other than one.");
        var sequenceLength = modelDimensions[1] > 0 ? modelDimensions[1] : ids.Length;
        Require(sequenceLength >= ids.Length && sequenceLength <= MaximumSequenceLength,
            "The pinned graph sequence dimension cannot represent the 512-token E5 contract.");
        var dimensions = new[] { batchSize, sequenceLength };
        var values = new long[sequenceLength];
        if (string.Equals(name, "input_ids", StringComparison.Ordinal))
        {
            Array.Fill(values, padTokenId);
            for (var index = 0; index < ids.Length; index++)
            {
                values[index] = ids[index];
            }
        }
        else if (string.Equals(name, "attention_mask", StringComparison.Ordinal))
        {
            Array.Fill(values, 0L);
            Array.Fill(values, 1L, 0, ids.Length);
        }
        else if (string.Equals(name, "token_type_ids", StringComparison.Ordinal))
        {
            Array.Fill(values, 0L);
        }
        else
        {
            throw new InvalidDataException("Unsupported pinned ONNX input name: " + name);
        }

        if (metadata.ElementType == typeof(long))
        {
            return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(values, dimensions));
        }
        Require(metadata.ElementType == typeof(int),
            "Unsupported pinned ONNX integer tensor type: " + metadata.ElementType);

        var intValues = new int[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            intValues[index] = checked((int)values[index]);
        }

        return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(intValues, dimensions));
    }

    private static List<RankedDocument> RankDocuments(
        float[] queryEmbedding,
        IReadOnlyList<CandidateDocument> documents,
        string allowedFolder)
    {
        var eligible = new List<RankedDocument>(documents.Count);
        foreach (var document in documents)
        {
            if (!string.Equals(document.Folder, allowedFolder, StringComparison.Ordinal))
            {
                continue;
            }

            var passageEmbedding = document.Embedding
                ?? throw new InvalidDataException("A current passage embedding was not computed: " + document.Path);
            double score = 0d;
            for (var dimension = 0; dimension < queryEmbedding.Length; dimension++)
            {
                score += (double)queryEmbedding[dimension] * passageEmbedding[dimension];
            }

            eligible.Add(new RankedDocument(document.Path, document.Revision, score));
        }

        eligible.Sort((left, right) =>
        {
            var scoreOrder = right.CosineScore.CompareTo(left.CosineScore);
            return scoreOrder != 0 ? scoreOrder : StringComparer.Ordinal.Compare(left.Path, right.Path);
        });
        return eligible;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }

    private static RuntimeSample ReadProcessSample(Process process)
    {
        process.Refresh();
        return new RuntimeSample(
            process.PrivateMemorySize64,
            process.WorkingSet64,
            process.PeakWorkingSet64,
            process.TotalProcessorTime.TotalMilliseconds);
    }

    private static object SampleMemory(RuntimeSample sample) => new
    {
        privateBytes = sample.PrivateBytes,
        privateMiB = ToMiB(sample.PrivateBytes),
        workingSetBytes = sample.WorkingSetBytes,
        workingSetMiB = ToMiB(sample.WorkingSetBytes),
        lifetimePeakWorkingSetBytes = sample.PeakWorkingSetBytes,
        lifetimePeakWorkingSetMiB = ToMiB(sample.PeakWorkingSetBytes),
    };

    private static double ToMiB(long bytes) => bytes / (1024d * 1024d);

    private static object SummarizeSamples(IReadOnlyList<double> samples) => new
    {
        count = samples.Count,
        p50Milliseconds = Percentile(samples, 0.50),
        p95Milliseconds = Percentile(samples, 0.95),
        minimumMilliseconds = samples.Count == 0 ? 0d : samples.Min(),
        maximumMilliseconds = samples.Count == 0 ? 0d : samples.Max(),
    };

    private static double Percentile(IReadOnlyList<double> samples, double percentile)
    {
        if (samples.Count == 0)
        {
            return 0d;
        }

        var sorted = samples.ToArray();
        Array.Sort(sorted);
        var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}
