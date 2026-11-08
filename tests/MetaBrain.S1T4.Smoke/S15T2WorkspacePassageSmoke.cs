using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;
using Tokenizers.DotNet;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private sealed record S15T2Source(string Name, byte[] Bytes, string FileId, long Revision);
    private sealed record S15T2Anchor(long Offset, long Length, string Sha256, string Kind, string Attribution, int ChunkOrdinal);
    private sealed record S15T2Coverage(long PassageCount, long PostingCount, int MaxFinalTokenCount, int OverflowInputs, string[] OverflowSourceNames);
    private static readonly TimeSpan S15T2LongCommandTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan S15T2StageWaitTimeout = TimeSpan.FromSeconds(180);
    // Largest one-record frame the product can write: frame + nonce + maximum payload + tag.
    private const long S15T2MaximumRecordFrameBytes = 5 + 12 + 8192 + 16;
    // The writer buffers 64 KiB, so the first on-disk growth already exceeds many record frames.
    private const long S15T2MinimumFlushedStageBytes = 65536;
    private const int S15T2LongProseBytes = 150_000;
    public static async Task RunS15T2WorkspacePassageIndexAsync(string connectionsExecutable, string cliExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S15-T2 product smoke is Windows-only.");
        }

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
                "The actual product CLI did not provision the synthetic workspace vault (stderr=" + provision.StandardError.Trim() + ").");

            var treeStatus = await ReadWorkspaceStatusAsync(cliExecutable, fixture).ConfigureAwait(false);
            var treeRevision = WorkspaceNumber(treeStatus, "revision");
            var root = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-mkdir", "--control-pipe", fixture.ControlPipe,
                "--parent-id", "root", "--name", "s15-t2-fixtures",
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(root.ExitCode == 0 && root.StandardOutput.Contains("MKDIR id=", StringComparison.Ordinal),
                "The actual product CLI did not initialize the synthetic workspace (stderr=" + root.StandardError.Trim() + ").");
            var folderId = WorkspaceToken(root.StandardOutput, "id");
            treeRevision = WorkspaceNumber(root.StandardOutput, "revision");

            var oversizedJsonlPath = Path.Combine(fixture.OutputDirectory, "s15-t2-oversized.jsonl");
            await File.WriteAllBytesAsync(oversizedJsonlPath, new byte[1_048_577]).ConfigureAwait(false);
            var oversizedImport = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-import", "--control-pipe", fixture.ControlPipe,
                "--parent-id", folderId, "--name", "oversized.jsonl", "--input-file", oversizedJsonlPath,
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(oversizedImport.ExitCode == 4 && oversizedImport.StandardError.Contains("workspace_too_large", StringComparison.Ordinal),
                "The workspace CLI did not explicitly refuse a JSONL source above its original-byte limit (exit=" +
                oversizedImport.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + oversizedImport.StandardError.Trim() + ").");

            var sources = new List<S15T2Source>();
            async Task<S15T2Source> ImportAsync(string name, byte[] bytes)
            {
                var input = Path.Combine(fixture.OutputDirectory, "s15-t2-input-" +
                    sources.Count.ToString(CultureInfo.InvariantCulture) + ".bin");
                await File.WriteAllBytesAsync(input, bytes).ConfigureAwait(false);
                var imported = await ImportWorkspaceFileAsync(
                    cliExecutable, fixture, folderId, name, input, treeRevision).ConfigureAwait(false);
                treeRevision = imported.TreeRevision;
                var source = new S15T2Source(name, bytes, imported.FileId, 1);
                sources.Add(source);
                return source;
            }

            var markdown = Encoding.UTF8.GetBytes(
                "# S15-T2 heading π\n\nSynthetic body café and byte-anchored text.\n\n```csharp\nvar value = \"fixture\";\n```\n");
            var markdownSource = await ImportAsync("s15-t2-markdown.md", markdown).ConfigureAwait(false);

            var csv = Encoding.UTF8.GetBytes("id,label\r\n1,\"quoted, value and line\r\nsecond line\"\r\n2,plain\r\n");
            var csvSource = await ImportAsync("s15-t2-table.csv", csv).ConfigureAwait(false);

            var codexJsonl = Encoding.UTF8.GetBytes(
                "{\"type\":\"response_item\",\"timestamp\":\"2025-01-01T00:00:00Z\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"known user record\"}]}}\n");
            var codexSource = await ImportAsync("codex.session.jsonl", codexJsonl).ConfigureAwait(false);

            var unknownJsonl = Encoding.UTF8.GetBytes(
                "{\"text\":\"raw unknown record\"}\n[1,\"raw array\"]\nnull\n");
            var unknownJsonlSource = await ImportAsync("unregistered.jsonl", unknownJsonl).ConfigureAwait(false);

            var utf8BomMarkdown = Encoding.UTF8.GetPreamble()
                .Concat(Encoding.UTF8.GetBytes("# BOM heading café 😀\n\nBOM body Ω.\n"))
                .ToArray();
            var utf8BomSource = await ImportAsync("s15-t2-bom.md", utf8BomMarkdown).ConfigureAwait(false);

            var utf16Text = Encoding.Unicode.GetPreamble()
                .Concat(Encoding.Unicode.GetBytes("UTF-16LE source Ω e\u0301 😀 byte anchor.\r\n"))
                .ToArray();
            var utf16Source = await ImportAsync("s15-t2-utf16.txt", utf16Text).ConfigureAwait(false);

            var longProse = BuildS15T2LongProse();
            var longProseSource = await ImportAsync("s15-t2-long.txt", longProse).ConfigureAwait(false);

            var headers = Enumerable.Range(0, 320).Select(index => "field" + index.ToString("D3", CultureInfo.InvariantCulture)).ToArray();
            var values = Enumerable.Range(0, headers.Length).Select(index => "value" + index.ToString("D3", CultureInfo.InvariantCulture)).ToArray();
            var wideCsv = Encoding.UTF8.GetBytes(string.Join(',', headers) + "\r\n" + string.Join(',', values) + "\r\n");
            var wideCsvSource = await ImportAsync("s15-t2-wide.csv", wideCsv).ConfigureAwait(false);

            
            var unsupportedBytes = new byte[] { 0x00, 0xFF, 0xFE, 0x81, 0x80, 0xC0, 0xAF, 0xED, 0xA0, 0x80 };
            var unsupportedSource = await ImportAsync("s15-t2-opaque.bin", unsupportedBytes).ConfigureAwait(false);

            var annotationAnchor = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-anchor-create", "--control-pipe", fixture.ControlPipe,
                "--file-id", markdownSource.FileId, "--expected-content-revision", "1",
                "--offset", "0", "--length", Encoding.UTF8.GetByteCount("# S15-T2 heading π\n").ToString(CultureInfo.InvariantCulture),
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(annotationAnchor.ExitCode == 0 && annotationAnchor.StandardOutput.Contains("state=live", StringComparison.Ordinal),
                "The synthetic owner annotation fixture could not create its existing workspace anchor.");
            var annotationInput = Path.Combine(fixture.OutputDirectory, "s15-t2-annotation.json");
            var annotationDraft = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                anchorId = WorkspaceToken(annotationAnchor.StandardOutput, "id"),
                facets = new[] { "experience" }
            });
            await File.WriteAllBytesAsync(annotationInput, annotationDraft).ConfigureAwait(false);
            var annotationCreate = await RunOwnerCommandAsync(cliExecutable,
                "owner", "cognitive-annotation-create", "--control-pipe", fixture.ControlPipe,
                "--file-id", markdownSource.FileId, "--input-file", annotationInput).ConfigureAwait(false);
            Require(annotationCreate.ExitCode == 0 && annotationCreate.StandardOutput.Contains("origin=Owner", StringComparison.Ordinal),
                "The synthetic owner annotation could not be created before passage indexing.");
            var annotationId = WorkspaceToken(annotationCreate.StandardOutput, "id");
            var annotationBefore = await ReadS15T2OwnerAnnotationAsync(cliExecutable, fixture, annotationId).ConfigureAwait(false);

            const string canonicalMemoryId = "s15-t2-canonical-sentinel";
            const string canonicalMemoryTitle = "Synthetic S15-T2 canonical sentinel";
            const string canonicalMemoryBody = "Orchid beacon phrase. Synthetic canonical owner memory for preservation proof.";
            const string canonicalMemoryQuery = "orchid beacon phrase";
            var canonicalMemoryCreate = await RunOwnerCommandAsync(cliExecutable,
                "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", canonicalMemoryId, "--zone-id", "s15-t2-zone",
                "--body", canonicalMemoryBody, "--kind", "observation", "--author", "user",
                "--method", "directWrite", "--evidence", "reportedSource", "--level", "l1",
                "--title", canonicalMemoryTitle).ConfigureAwait(false);
            Require(canonicalMemoryCreate.ExitCode == 0 &&
                    canonicalMemoryCreate.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
                "The synthetic canonical-memory sentinel could not be created before workspace indexing.");

            var canonicalRebuild = await RunOwnerCommandAsync(cliExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(canonicalRebuild.ExitCode == 0 && canonicalRebuild.StandardOutput.Contains("INDEX rebuilt", StringComparison.Ordinal),
                "The existing canonical memory index could not be materialized before passage indexing.");
            var canonicalBefore = await ReadS15T2CanonicalMemoryAsync(
                cliExecutable, fixture, canonicalMemoryId, canonicalMemoryQuery).ConfigureAwait(false);
            RequireS15T2CanonicalMemory(canonicalBefore, canonicalMemoryId, canonicalMemoryTitle, canonicalMemoryBody);


            foreach (var operation in new[]
                     {
                         ServiceRequestHandler.OwnerWorkspacePassageIndexStatusOperation,
                         ServiceRequestHandler.OwnerWorkspacePassageIndexRebuildOperation,
                         ServiceRequestHandler.OwnerWorkspacePassageAnchorsOperation
                     })
            {
                var denied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                    operation
                }).ConfigureAwait(false);
                Require(ReplyCode(denied) == "forbidden",
                    "The agent pipe reached an owner-only workspace passage-index operation.");
            }

            var status = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(status.ExitCode == 0 && status.StandardOutput.Contains("state=not_materialized", StringComparison.Ordinal),
                "The product owner CLI did not report the missing encrypted passage generation (exit=" +
                status.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + status.StandardError.Trim() + ").");
            Console.WriteLine("PASS actual CLI workspace-index-status reports not_materialized");

            var missingModel = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-rebuild", "--control-pipe", fixture.ControlPipe,
                "--model-directory", Path.Combine(fixture.OutputDirectory, "missing-model")).ConfigureAwait(false);
            Require(missingModel.ExitCode == 3 && missingModel.StandardError.Contains("workspace_index_model_missing", StringComparison.Ordinal),
                "A missing local model did not fail closed with the explicit model-missing refusal (exit=" +
                missingModel.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + missingModel.StandardError.Trim() + ").");
            Console.WriteLine("PASS actual CLI workspace-index-rebuild refuses a missing local model");

            var invalidModelDirectory = Path.Combine(fixture.OutputDirectory, "invalid-model");
            var invalidOnnxDirectory = Path.Combine(invalidModelDirectory, "onnx");
            Directory.CreateDirectory(invalidOnnxDirectory);
            await File.WriteAllBytesAsync(Path.Combine(invalidOnnxDirectory, "model.onnx"), new byte[] { 0 }).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(invalidOnnxDirectory, "tokenizer.json"), new byte[] { 0 }).ConfigureAwait(false);
            var invalidModel = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-rebuild", "--control-pipe", fixture.ControlPipe,
                "--model-directory", invalidModelDirectory).ConfigureAwait(false);
            Require(invalidModel.ExitCode == 3 && invalidModel.StandardError.Contains("workspace_index_model_invalid", StringComparison.Ordinal),
                "An invalid local model asset set did not fail closed with the explicit model-invalid refusal (exit=" +
                invalidModel.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + invalidModel.StandardError.Trim() + ").");
            Console.WriteLine("PASS actual CLI workspace-index-rebuild refuses invalid local model assets");

            var modelDirectory = Path.Combine(SmokeFixture.FindRepositoryRoot(),
                "runtime", "s15-t1-model-staging", "614241f622f53c4eeff9890bdc4f31cfecc418b3", "model");
            var expectedCoverage = MeasureTokenCoverage(sources, modelDirectory);
            Require(expectedCoverage.OverflowInputs > 0 &&
                    expectedCoverage.OverflowSourceNames.Contains(wideCsvSource.Name, StringComparer.Ordinal) &&
                    expectedCoverage.MaxFinalTokenCount <= 512,
                "The wide CSV did not drive actual token-budget overflow and lossless scalar splitting (overflow inputs=" +
                expectedCoverage.OverflowInputs.ToString(CultureInfo.InvariantCulture) + "; max final IDs=" +
                expectedCoverage.MaxFinalTokenCount.ToString(CultureInfo.InvariantCulture) + ").");

            var firstRebuildStarted = DateTime.UtcNow;
            var rebuilt = await RunOwnerCommandAsync(cliExecutable, S15T2LongCommandTimeout,
                "owner", "workspace-index-rebuild", "--control-pipe", fixture.ControlPipe,
                "--model-directory", modelDirectory).ConfigureAwait(false);
            var firstRebuildElapsed = DateTime.UtcNow - firstRebuildStarted;
            Require(rebuilt.ExitCode == 0 && rebuilt.StandardOutput.Contains("state=partial", StringComparison.Ordinal) &&
                    rebuilt.StandardOutput.Contains("unsupported=1", StringComparison.Ordinal),
                "The product CLI did not publish a successful partial generation with explicit unsupported-file coverage (exit=" +
                rebuilt.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + rebuilt.StandardError.Trim() + ").");
            Require(WorkspaceNumber(rebuilt.StandardOutput, "passages") == expectedCoverage.PassageCount &&
                    WorkspaceNumber(rebuilt.StandardOutput, "postings") == expectedCoverage.PostingCount,
                "The actual encrypted generation lost parser/tokenizer content or token-overflow subchunks.");
            Console.WriteLine("PASS actual product rebuild keeps all measured token segments and reports opaque-file coverage explicitly");
            Console.WriteLine("NOTE actual rebuild wrote " +
                expectedCoverage.PassageCount.ToString(CultureInfo.InvariantCulture) + " passages and " +
                expectedCoverage.PostingCount.ToString(CultureInfo.InvariantCulture) + " postings in " +
                ((int)firstRebuildElapsed.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s");

            foreach (var source in sources.Where(source => source.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) == false))
            {
                var anchors = await ReadAndValidateAnchorsAsync(cliExecutable, fixture, source, source.Revision).ConfigureAwait(false);
                if (source == markdownSource)
                {
                    Require(anchors.Any(anchor => anchor.Kind == "markdown-heading") &&
                            anchors.Any(anchor => anchor.Kind == "markdown-code"),
                        "Markdown headings or fenced code blocks did not retain structural anchors.");
                }
                else if (source == csvSource)
                {
                    Require(anchors.Any(anchor => anchor.Kind == "csv-header") &&
                            anchors.Any(anchor => anchor.Kind == "csv-row" &&
                                Encoding.UTF8.GetString(source.Bytes.AsSpan(checked((int)anchor.Offset), checked((int)anchor.Length)))
                                    .Contains("quoted, value and line\r\nsecond line", StringComparison.Ordinal)),
                        "CSV header or quoted multiline row anchors did not cover their exact original records.");
                }
                else if (source == codexSource)
                {
                    Require(anchors.Any(anchor => anchor.Kind == "jsonl-record" && anchor.Attribution == "user"),
                        "The known Codex JSONL user record did not retain its declared attribution.");
                }
                else if (source == unknownJsonlSource)
                {
                    Require(anchors.Count > 0 && anchors.All(anchor => anchor.Attribution == "unknown"),
                        "Unknown raw JSONL shapes received an unearned source attribution.");
                }
                else if (source == utf8BomSource)
                {
                    Require(anchors.Any(anchor => anchor.Kind == "markdown-heading" && anchor.Offset == 3),
                        "The UTF-8 BOM was not excluded from the first exact heading byte range.");
                }
                else if (source == utf16Source)
                {
                    Require(anchors.Any(anchor => anchor.Kind == "text" && anchor.Offset >= 2),
                        "The UTF-16LE source did not produce a byte-based text anchor after its BOM.");
                }
                else if (source == longProseSource)
                {
                    Require(anchors.All(anchor => anchor.Kind == "text"),
                        "The long plain-text source did not produce byte-based text anchors.");
                }
                else if (source == wideCsvSource)
                {
                    var rowAnchors = anchors.Where(anchor => anchor.Kind == "csv-row").ToArray();
                    var parserRows = WorkspacePassageParser.Parse(
                            wideCsvSource.FileId, 1, wideCsvSource.Name, wideCsvSource.Bytes, CancellationToken.None)
                        .Count(draft => draft.Kind == "csv-row");
                    Require(expectedCoverage.OverflowSourceNames.Contains(wideCsvSource.Name, StringComparer.Ordinal) &&
                            rowAnchors.Length > parserRows &&
                            rowAnchors.Select(anchor => anchor.ChunkOrdinal).Distinct().Count() == rowAnchors.Length &&
                            rowAnchors.All(anchor => anchor.Offset == rowAnchors[0].Offset &&
                                anchor.Length == rowAnchors[0].Length && anchor.Sha256 == rowAnchors[0].Sha256),
                        "Actual CSV token overflow did not retain complete text, distinct chunk identity, and the exact record span.");
                }
            }
            Console.WriteLine("PASS actual anchor readback covers Markdown/code, CSV multiline, known/raw JSONL, UTF-8 BOM and UTF-16 bytes");
            Console.WriteLine("PASS actual model-token overflow splits losslessly with exact full CSV record anchors");
            Console.WriteLine("PASS actual token-budget subchunks retain complete source-record identity without claiming narrower token spans");

            VerifyNoPlaintextGeneration(fixture.VaultDirectoryPath, sources);
            var annotationAfter = await ReadS15T2OwnerAnnotationAsync(cliExecutable, fixture, annotationId).ConfigureAwait(false);
            Require(annotationAfter == annotationBefore, "Workspace passage indexing changed the owner's annotation contract fields.");
            RequireS15T2OwnerAnnotation(annotationAfter, annotationId, annotationAnchor.StandardOutput, markdownSource.FileId);
            var canonicalAfter = await ReadS15T2CanonicalMemoryAsync(
                cliExecutable, fixture, canonicalMemoryId, canonicalMemoryQuery).ConfigureAwait(false);
            Require(canonicalAfter == canonicalBefore,
                "Workspace passage indexing changed the canonical-memory search/detail contract fields.");
            RequireS15T2CanonicalMemory(canonicalAfter, canonicalMemoryId, canonicalMemoryTitle, canonicalMemoryBody);
            Console.WriteLine("PASS direct owner annotation readback preserves identity, revision, review, evidence and exact anchor fields");
            Console.WriteLine("PASS canonical memory search/detail preserves seeded identity, revision, content and evidence");

            // --- stale detection, explicit rebuild and exact new-revision anchors ---
            var generationDirectory = Path.Combine(fixture.VaultDirectoryPath, "workspace-passage-index");
            var generationPath = Path.Combine(generationDirectory, "passages.v1.bin");
            var builtPassages = WorkspaceNumber(rebuilt.StandardOutput, "passages");
            var builtPostings = WorkspaceNumber(rebuilt.StandardOutput, "postings");

            // Releasing the explicit model selection keeps this stale-generation probe deterministic.
            var staleProbeLock = await RunOwnerCommandAsync(
                cliExecutable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(staleProbeLock.ExitCode == 0 &&
                    staleProbeLock.StandardOutput.Contains("VAULT locked", StringComparison.Ordinal),
                "The vault did not lock before the stale-generation probe.");
            var staleProbeUnlock = await RunOwnerCommandWithInputAsync(
                cliExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(staleProbeUnlock.ExitCode == 0 &&
                    staleProbeUnlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The vault did not unlock before the stale-generation probe.");
            treeRevision = WorkspaceNumber(
                await ReadWorkspaceStatusAsync(cliExecutable, fixture).ConfigureAwait(false), "revision");
            var staleFolder = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-mkdir", "--control-pipe", fixture.ControlPipe,
                "--parent-id", folderId, "--name", "s15-t2-stale-probe",
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(staleFolder.ExitCode == 0 && staleFolder.StandardOutput.Contains("MKDIR id=", StringComparison.Ordinal),
                "The synthetic workspace tree mutation for stale detection failed (stderr=" + staleFolder.StandardError.Trim() + ").");
            treeRevision = WorkspaceNumber(staleFolder.StandardOutput, "revision");

            var treeStale = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(treeStale.ExitCode == 0 &&
                    treeStale.StandardOutput.Contains("state=stale", StringComparison.Ordinal) &&
                    treeStale.StandardOutput.Contains("stale=true", StringComparison.Ordinal) &&
                    treeStale.StandardOutput.Contains("refusal=workspace_index_stale", StringComparison.Ordinal) &&
                    WorkspaceNumber(treeStale.StandardOutput, "revision") == treeRevision &&
                    WorkspaceNumber(treeStale.StandardOutput, "passages") == builtPassages &&
                    WorkspaceNumber(treeStale.StandardOutput, "postings") == builtPostings,
                "A workspace tree revision change was not reported as a stale passage generation (exit=" +
                treeStale.ExitCode.ToString(CultureInfo.InvariantCulture) + "; output=" + treeStale.StandardOutput.Trim() + ").");
            var treeStaleAnchors = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-anchors", "--control-pipe", fixture.ControlPipe,
                "--file-id", markdownSource.FileId, "--revision", "1", "--limit", "64").ConfigureAwait(false);
            Require(treeStaleAnchors.ExitCode != 0 &&
                    treeStaleAnchors.StandardError.Contains("workspace_index_stale", StringComparison.Ordinal),
                "An anchor read against the stale passage generation was not refused (exit=" +
                treeStaleAnchors.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + treeStaleAnchors.StandardError.Trim() + ").");
            Console.WriteLine("PASS actual tree revision change reports stale status and refuses anchors");

            var revisedUtf16Text = Encoding.Unicode.GetPreamble()
                .Concat(Encoding.Unicode.GetBytes("Revised UTF-16LE source Ω2 e\u0301 marker.\r\n"))
                .ToArray();
            var revisedUtf16Path = Path.Combine(fixture.OutputDirectory, "s15-t2-utf16-revision-2.bin");
            await File.WriteAllBytesAsync(revisedUtf16Path, revisedUtf16Text).ConfigureAwait(false);
            var revised = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                "--file-id", utf16Source.FileId, "--expected-content-revision", "1",
                "--input-file", revisedUtf16Path).ConfigureAwait(false);
            Require(revised.ExitCode == 0 && revised.StandardOutput.Contains("contentRevision=2", StringComparison.Ordinal),
                "The synthetic workspace revise to content revision 2 failed (stderr=" + revised.StandardError.Trim() + ").");
            treeRevision = WorkspaceNumber(revised.StandardOutput, "revision");
            var utf16Position = sources.IndexOf(utf16Source);
            utf16Source = utf16Source with { Bytes = revisedUtf16Text, Revision = 2 };
            sources[utf16Position] = utf16Source;

            var revisedStale = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(revisedStale.ExitCode == 0 &&
                    revisedStale.StandardOutput.Contains("state=stale", StringComparison.Ordinal) &&
                    WorkspaceNumber(revisedStale.StandardOutput, "revision") == treeRevision &&
                    WorkspaceNumber(revisedStale.StandardOutput, "passages") == builtPassages,
                "A workspace content revision change was not reported as a stale passage generation.");
            var unbuiltAnchors = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-anchors", "--control-pipe", fixture.ControlPipe,
                "--file-id", utf16Source.FileId, "--revision", "2").ConfigureAwait(false);
            Require(unbuiltAnchors.ExitCode != 0 &&
                    unbuiltAnchors.StandardError.Contains("workspace_index_stale", StringComparison.Ordinal),
                "Anchors for the unbuilt content revision 2 were not refused as stale.");
            Console.WriteLine("PASS actual content revision change reports stale status and refuses anchors until rebuilt");

            var expectedRebuiltCoverage = MeasureTokenCoverage(sources, modelDirectory);
            var revisedRebuild = await RunOwnerCommandAsync(cliExecutable, S15T2LongCommandTimeout,
                "owner", "workspace-index-rebuild", "--control-pipe", fixture.ControlPipe,
                "--model-directory", modelDirectory).ConfigureAwait(false);
            Require(revisedRebuild.ExitCode == 0 &&
                    revisedRebuild.StandardOutput.Contains("state=partial", StringComparison.Ordinal) &&
                    WorkspaceNumber(revisedRebuild.StandardOutput, "revision") == treeRevision &&
                    WorkspaceNumber(revisedRebuild.StandardOutput, "passages") == expectedRebuiltCoverage.PassageCount &&
                    WorkspaceNumber(revisedRebuild.StandardOutput, "postings") == expectedRebuiltCoverage.PostingCount,
                "The explicit rebuild after the content revision change did not publish the current generation (exit=" +
                revisedRebuild.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + revisedRebuild.StandardError.Trim() + ").");
            var revisedAnchors = await ReadAndValidateAnchorsAsync(cliExecutable, fixture, utf16Source, 2).ConfigureAwait(false);
            Require(revisedAnchors.Any(anchor => anchor.Kind == "text" && anchor.Offset >= 2),
                "The rebuilt generation did not carry the exact content revision 2 byte anchors.");
            _ = await ReadAndValidateAnchorsAsync(cliExecutable, fixture, markdownSource, 1).ConfigureAwait(false);
            var retiredRevision = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-anchors", "--control-pipe", fixture.ControlPipe,
                "--file-id", utf16Source.FileId, "--revision", "1").ConfigureAwait(false);
            Require(retiredRevision.ExitCode != 0 &&
                    retiredRevision.StandardError.Contains("workspace_index_anchor_revision_unavailable", StringComparison.Ordinal),
                "The rebuilt generation did not refuse anchors for the retired content revision (stderr=" + retiredRevision.StandardError.Trim() + ").");
            Console.WriteLine("PASS explicit rebuild publishes exact content revision 2 anchors and refuses the retired revision");

            // --- active rebuild, in-flight stage inspection, lock cancellation and drain ---
            var publishedBeforeCancellation = HashFile(generationPath);
            var cancelledRebuild = RunOwnerCommandAsync(cliExecutable, S15T2LongCommandTimeout,
                "owner", "workspace-index-rebuild", "--control-pipe", fixture.ControlPipe,
                "--model-directory", modelDirectory);
            var stageStart = DateTime.UtcNow;
            var (stagePath, stageBytes) = await WaitForFlushedStageRecordAsync(
                generationDirectory, S15T2StageWaitTimeout).ConfigureAwait(false);
            Require(stageBytes >= S15T2MinimumFlushedStageBytes && stageBytes > S15T2MaximumRecordFrameBytes,
                "The observed in-flight passage stage did not pass a record boundary.");
            var sharedReadFailure = TryReadLockedStage(stagePath, FileShare.ReadWrite | FileShare.Delete);
            var exclusiveReadFailure = TryReadLockedStage(stagePath, FileShare.None);
            Require(sharedReadFailure is not null && exclusiveReadFailure is not null,
                "The in-flight encrypted passage stage was readable while the rebuild held it, so its in-flight privacy could not be bounded by exclusive access.");
            RequireNoPlaintextVaultFiles(fixture.VaultDirectoryPath, stagePath, S15T2PlaintextMarkers());
            var lockDuringRebuild = await RunOwnerCommandAsync(cliExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockDuringRebuild.ExitCode == 0 &&
                    lockDuringRebuild.StandardOutput.Contains("VAULT locked; in-flight private operations drained", StringComparison.Ordinal),
                "The owner lock during an active rebuild did not drain and complete (stderr=" + lockDuringRebuild.StandardError.Trim() + ").");
            var cancelledResult = await cancelledRebuild.ConfigureAwait(false);
            var cancelledAfter = DateTime.UtcNow - stageStart;
            Require(cancelledResult.ExitCode != 0 && cancelledResult.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "The in-flight rebuild was not cancelled by the lock (exit=" +
                cancelledResult.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + cancelledResult.StandardError.Trim() + ").");
            Require(!Directory.EnumerateFiles(generationDirectory, "*.building", SearchOption.TopDirectoryOnly).Any(),
                "The cancelled rebuild left a staging artifact beside the published generation.");
            Require(HashFile(generationPath) == publishedBeforeCancellation,
                "The cancelled rebuild changed the published encrypted generation.");
            Console.WriteLine("NOTE active rebuild exposed a flushed stage of " +
                stageBytes.ToString(CultureInfo.InvariantCulture) + " bytes; concurrent reads were refused (" +
                sharedReadFailure + "," + exclusiveReadFailure + "); the lock cancelled it after " +
                ((int)cancelledAfter.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms");
            Console.WriteLine("PASS active rebuild stage stays unreadable and no readable vault path carries source plaintext in flight");
            Console.WriteLine("PASS owner lock cancels the active rebuild, drains it, removes staging and leaves the published generation byte-identical");

            var lockedStatus = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockedStatus.ExitCode != 0 && lockedStatus.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "The locked vault did not refuse the passage index status (stderr=" + lockedStatus.StandardError.Trim() + ").");
            var lockedAnchors = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-anchors", "--control-pipe", fixture.ControlPipe,
                "--file-id", markdownSource.FileId, "--revision", "1").ConfigureAwait(false);
            Require(lockedAnchors.ExitCode != 0 && lockedAnchors.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "The locked vault did not refuse the passage anchor read (stderr=" + lockedAnchors.StandardError.Trim() + ").");
            var lockedRebuild = await RunOwnerCommandAsync(cliExecutable, S15T2LongCommandTimeout,
                "owner", "workspace-index-rebuild", "--control-pipe", fixture.ControlPipe,
                "--model-directory", modelDirectory).ConfigureAwait(false);
            Require(lockedRebuild.ExitCode != 0 && lockedRebuild.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "The locked vault did not refuse the passage index rebuild (stderr=" + lockedRebuild.StandardError.Trim() + ").");
            Console.WriteLine("PASS locked vault denies passage index status, anchors and rebuild with vault_locked");

            await StopServiceAsync(service).ConfigureAwait(false);
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var restartedState = await ReadStatusAsync(cliExecutable, fixture.ControlPipe).ConfigureAwait(false);
            Require(restartedState.Contains("vault=locked", StringComparison.Ordinal),
                "The restarted service did not start with the vault still locked (status=" + restartedState.Trim() + ").");
            var lockedAfterRestart = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockedAfterRestart.ExitCode != 0 && lockedAfterRestart.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "The restarted locked service served the passage index status.");
            Console.WriteLine("PASS the restarted service stays locked and still denies passage index access");

            var unlockAfterRestart = await RunOwnerCommandWithInputAsync(cliExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlockAfterRestart.ExitCode == 0 && unlockAfterRestart.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The explicit unlock after the locked restart failed (stderr=" + unlockAfterRestart.StandardError.Trim() + ").");
            var recoveredStatus = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(recoveredStatus.ExitCode == 0 &&
                    recoveredStatus.StandardOutput.Contains("state=partial", StringComparison.Ordinal) &&
                    recoveredStatus.StandardOutput.Contains("stale=false", StringComparison.Ordinal) &&
                    WorkspaceNumber(recoveredStatus.StandardOutput, "revision") == treeRevision &&
                    WorkspaceNumber(recoveredStatus.StandardOutput, "passages") == expectedRebuiltCoverage.PassageCount,
                "The persisted passage generation did not authenticate again after lock, restart and unlock (output=" +
                recoveredStatus.StandardOutput.Trim() + ").");
            _ = await ReadAndValidateAnchorsAsync(cliExecutable, fixture, utf16Source, 2).ConfigureAwait(false);
            Console.WriteLine("PASS lock + locked restart + explicit unlock re-authenticates the persisted generation and exact anchors");

            var recoveredRebuild = await RunOwnerCommandAsync(cliExecutable, S15T2LongCommandTimeout,
                "owner", "workspace-index-rebuild", "--control-pipe", fixture.ControlPipe,
                "--model-directory", modelDirectory).ConfigureAwait(false);
            Require(recoveredRebuild.ExitCode == 0 &&
                    WorkspaceNumber(recoveredRebuild.StandardOutput, "passages") == expectedRebuiltCoverage.PassageCount &&
                    WorkspaceNumber(recoveredRebuild.StandardOutput, "postings") == expectedRebuiltCoverage.PostingCount,
                "The passage index did not rebuild after the locked restart and unlock (stderr=" + recoveredRebuild.StandardError.Trim() + ").");
            Console.WriteLine("PASS the passage index rebuilds again after lock, locked restart and explicit unlock");
            VerifyNoPlaintextGeneration(fixture.VaultDirectoryPath, sources);

            // --- synthetic generation authentication negatives ---
            var stableGeneration = await File.ReadAllBytesAsync(generationPath).ConfigureAwait(false);
            var stableGenerationHash = HashFile(generationPath);

            async Task RequireGenerationRefusedAsync(string label, byte[] replacement)
            {
                await File.WriteAllBytesAsync(generationPath, replacement).ConfigureAwait(false);
                try
                {
                    var refused = await RunOwnerCommandAsync(cliExecutable,
                        "owner", "workspace-index-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
                    Require(refused.ExitCode != 0 &&
                            refused.StandardError.Contains("workspace_index_unavailable", StringComparison.Ordinal),
                        "The " + label + " generation authentication negative was not refused (exit=" +
                        refused.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + refused.StandardError.Trim() + ").");
                }
                finally
                {
                    await File.WriteAllBytesAsync(generationPath, stableGeneration).ConfigureAwait(false);
                }

                var restored = await RunOwnerCommandAsync(cliExecutable,
                    "owner", "workspace-index-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
                Require(restored.ExitCode == 0 && restored.StandardOutput.Contains("state=partial", StringComparison.Ordinal),
                    "The published passage generation did not authenticate again after the " + label + " negative.");
            }

            var headerTamper = (byte[])stableGeneration.Clone();
            headerTamper[0] ^= 0xFF;
            await RequireGenerationRefusedAsync("header", headerTamper).ConfigureAwait(false);
            var identityTamper = (byte[])stableGeneration.Clone();
            identityTamper[24] ^= 0xFF;
            await RequireGenerationRefusedAsync("record identity", identityTamper).ConfigureAwait(false);
            var recordTamper = (byte[])stableGeneration.Clone();
            recordTamper[60] ^= 0xFF;
            await RequireGenerationRefusedAsync("record ciphertext", recordTamper).ConfigureAwait(false);
            var footerTamper = (byte[])stableGeneration.Clone();
            footerTamper[^20] ^= 0xFF;
            await RequireGenerationRefusedAsync("footer ciphertext", footerTamper).ConfigureAwait(false);
            var foreignKeyGeneration = await ReadForeignVaultGenerationAsync(
                connectionsExecutable, cliExecutable, modelDirectory).ConfigureAwait(false);
            Require(foreignKeyGeneration.Length > 40 &&
                    foreignKeyGeneration.AsSpan(0, 8).SequenceEqual("MBWPASS1"u8) &&
                    !foreignKeyGeneration.AsSpan().SequenceEqual(stableGeneration) &&
                    BitConverter.ToInt64(foreignKeyGeneration, 12) + 5 + 12 +
                        BitConverter.ToInt32(foreignKeyGeneration, 20) + 16 == foreignKeyGeneration.LongLength,
                "The foreign vault generation was not a structurally complete passage generation.");
            await RequireGenerationRefusedAsync("foreign vault key", foreignKeyGeneration).ConfigureAwait(false);
            Require(HashFile(generationPath) == stableGenerationHash,
                "The published passage generation did not return to its authenticated bytes after the authentication negatives.");
            Console.WriteLine("PASS synthetic header/identity/record/footer tampering and a foreign-vault-key generation are refused, and the original authenticates again");

            // --- malformed and incomplete known JSONL refusals preserve the authentic old generation ---
            // Clear the selected model so the mutation itself cannot replace the generation before the refusal check.
            var malformedProbeLock = await RunOwnerCommandAsync(
                cliExecutable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(malformedProbeLock.ExitCode == 0 &&
                    malformedProbeLock.StandardOutput.Contains("VAULT locked", StringComparison.Ordinal),
                "The vault did not lock before the malformed-JSONL preservation probe.");
            var malformedProbeUnlock = await RunOwnerCommandWithInputAsync(
                cliExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(malformedProbeUnlock.ExitCode == 0 &&
                    malformedProbeUnlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The vault did not unlock before the malformed-JSONL preservation probe.");
            treeRevision = WorkspaceNumber(
                await ReadWorkspaceStatusAsync(cliExecutable, fixture).ConfigureAwait(false), "revision");
            var knownJsonlFolder = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-mkdir", "--control-pipe", fixture.ControlPipe,
                "--parent-id", folderId, "--name", "s15-t2-known-jsonl",
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(knownJsonlFolder.ExitCode == 0 && knownJsonlFolder.StandardOutput.Contains("MKDIR id=", StringComparison.Ordinal),
                "The synthetic known-JSONL folder could not be created.");
            var knownJsonlFolderId = WorkspaceToken(knownJsonlFolder.StandardOutput, "id");
            treeRevision = WorkspaceNumber(knownJsonlFolder.StandardOutput, "revision");

            var malformedJsonl = Encoding.UTF8.GetBytes(
                "{\"type\":\"response_item\",\"timestamp\":\"2025-01-01T00:00:00Z\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"first known record\"}]}}\n" +
                "{\"type\":\"response_item\",\"timestamp\":\n");
            var malformedPath = Path.Combine(fixture.OutputDirectory, "s15-t2-known-malformed.jsonl");
            await File.WriteAllBytesAsync(malformedPath, malformedJsonl).ConfigureAwait(false);
            var malformedImport = await ImportWorkspaceFileAsync(
                cliExecutable, fixture, knownJsonlFolderId, "codex.session.jsonl", malformedPath, treeRevision).ConfigureAwait(false);
            treeRevision = malformedImport.TreeRevision;

            var malformedRefusal = await RunOwnerCommandAsync(cliExecutable, S15T2LongCommandTimeout,
                "owner", "workspace-index-rebuild", "--control-pipe", fixture.ControlPipe,
                "--model-directory", modelDirectory).ConfigureAwait(false);
            Require(malformedRefusal.ExitCode != 0 &&
                    malformedRefusal.StandardError.Contains("workspace_index_jsonl_parser_invalid", StringComparison.Ordinal),
                "The malformed known JSONL rebuild was not refused with its content code (exit=" +
                malformedRefusal.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + malformedRefusal.StandardError.Trim() + ").");
            Require(HashFile(generationPath) == stableGenerationHash,
                "A refused malformed-JSONL rebuild replaced the published encrypted generation.");
            var preservedAfterMalformed = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(preservedAfterMalformed.ExitCode == 0 &&
                    preservedAfterMalformed.StandardOutput.Contains("state=stale", StringComparison.Ordinal),
                "The old passage generation did not authenticate after the refused malformed-JSONL rebuild (stderr=" +
                preservedAfterMalformed.StandardError.Trim() + ").");
            Console.WriteLine("PASS actual malformed known JSONL is refused as invalid content and the authentic old generation is preserved");

            var incompleteJsonl = Encoding.UTF8.GetBytes(
                "{\"type\":\"response_item\",\"timestamp\":\"2025-01-01T00:00:00Z\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"unterminated known record\"}]}}");
            var incompletePath = Path.Combine(fixture.OutputDirectory, "s15-t2-known-incomplete.jsonl");
            await File.WriteAllBytesAsync(incompletePath, incompleteJsonl).ConfigureAwait(false);
            var incompleteRevise = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                "--file-id", malformedImport.FileId, "--expected-content-revision", "1",
                "--input-file", incompletePath).ConfigureAwait(false);
            Require(incompleteRevise.ExitCode == 0 && incompleteRevise.StandardOutput.Contains("contentRevision=2", StringComparison.Ordinal),
                "The synthetic known JSONL could not be revised to the unterminated record (stderr=" + incompleteRevise.StandardError.Trim() + ").");
            treeRevision = WorkspaceNumber(incompleteRevise.StandardOutput, "revision");

            var incompleteRefusal = await RunOwnerCommandAsync(cliExecutable, S15T2LongCommandTimeout,
                "owner", "workspace-index-rebuild", "--control-pipe", fixture.ControlPipe,
                "--model-directory", modelDirectory).ConfigureAwait(false);
            Require(incompleteRefusal.ExitCode != 0 &&
                    incompleteRefusal.StandardError.Contains("workspace_index_jsonl_incomplete_record", StringComparison.Ordinal),
                "The incomplete known JSONL rebuild was not refused with its content code (exit=" +
                incompleteRefusal.ExitCode.ToString(CultureInfo.InvariantCulture) + "; stderr=" + incompleteRefusal.StandardError.Trim() + ").");
            Require(HashFile(generationPath) == stableGenerationHash,
                "A refused incomplete-JSONL rebuild replaced the published encrypted generation.");
            Require(!Directory.EnumerateFiles(generationDirectory, "*.building", SearchOption.TopDirectoryOnly).Any(),
                "A refused rebuild left a staging artifact beside the published generation.");
            var preservedAfterIncomplete = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(preservedAfterIncomplete.ExitCode == 0 &&
                    preservedAfterIncomplete.StandardOutput.Contains("state=stale", StringComparison.Ordinal) &&
                    WorkspaceNumber(preservedAfterIncomplete.StandardOutput, "passages") == expectedRebuiltCoverage.PassageCount,
                "The old passage generation did not authenticate after the refused incomplete-JSONL rebuild.");
            var preservedAnchors = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-anchors", "--control-pipe", fixture.ControlPipe,
                "--file-id", markdownSource.FileId, "--revision", "1").ConfigureAwait(false);
            Require(preservedAnchors.ExitCode != 0 &&
                    preservedAnchors.StandardError.Contains("workspace_index_stale", StringComparison.Ordinal),
                "The preserved old generation did not authenticate its records before refusing stale anchors (stderr=" +
                preservedAnchors.StandardError.Trim() + ").");
            Console.WriteLine("PASS actual incomplete known JSONL is refused as an incomplete record and the authentic old generation is preserved");
            Console.WriteLine("NOTE current workspace tree revision=" +
                treeRevision.ToString(CultureInfo.InvariantCulture) + " with the refused known-JSONL fixture present");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static async Task<List<S15T2Anchor>> ReadAndValidateAnchorsAsync(
        string cliExecutable,
        SmokeFixture fixture,
        S15T2Source source,
        long revision)
    {
        var result = await RunOwnerCommandAsync(cliExecutable,
            "owner", "workspace-index-anchors", "--control-pipe", fixture.ControlPipe,
            "--file-id", source.FileId, "--revision", revision.ToString(CultureInfo.InvariantCulture),
            "--offset", "0", "--limit", "512").ConfigureAwait(false);
        Require(result.ExitCode == 0,
            "The actual product CLI could not read anchors for " + source.Name + " (stderr=" + result.StandardError.Trim() + ").");
        return VerifyAnchorBytes(result.StandardOutput, source.FileId, revision, source.Bytes);
    }

    private static List<S15T2Anchor> VerifyAnchorBytes(string output, string fileId, long revision, byte[] source)
    {
        var anchors = new List<S15T2Anchor>();
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                     .Where(line => line.StartsWith("ANCHOR ", StringComparison.Ordinal)))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(field => field.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
            if (!fields.TryGetValue("file", out var anchorFile) || anchorFile != fileId ||
                !fields.TryGetValue("revision", out var anchorRevision) ||
                anchorRevision != revision.ToString(CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException("An actual owner anchor lost its source file or revision identity.");
            }

            var offset = long.Parse(fields["offset"], CultureInfo.InvariantCulture);
            var length = long.Parse(fields["length"], CultureInfo.InvariantCulture);
            Require(offset >= 0 && length > 0 && offset <= source.LongLength && length <= source.LongLength - offset,
                "The product returned an out-of-range source anchor.");
            var digest = Convert.ToHexStringLower(SHA256.HashData(source.AsSpan(checked((int)offset), checked((int)length))));
            Require(string.Equals(digest, fields["sha256"], StringComparison.Ordinal),
                "The product anchor digest did not identify the exact original source-byte range.");
            anchors.Add(new S15T2Anchor(
                offset,
                length,
                digest,
                fields["kind"],
                fields["attribution"],
                int.Parse(fields["chunk"], CultureInfo.InvariantCulture)));
        }

        Require(anchors.Count > 0, "The product did not return any verifiable source anchors.");
        return anchors;
    }

    private static S15T2Coverage MeasureTokenCoverage(IReadOnlyList<S15T2Source> sources, string modelDirectory)
    {
        using var tokenizer = new Tokenizer(Path.Combine(modelDirectory, "onnx", "tokenizer.json"));
        long passages = 0;
        long postings = 0;
        var maxFinalTokens = 0;
        var overflowInputs = 0;
        var overflowSourceNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var draft in WorkspacePassageParser.Parse(
                         source.FileId, source.Revision, source.Name, source.Bytes, CancellationToken.None))
            {
                var pending = new Stack<(int Start, int Length)>();
                var finalSegments = new List<(int Start, int Length)>();
                pending.Push((0, draft.Text.Length));
                var splitInput = false;
                while (pending.Count > 0)
                {
                    var segment = pending.Pop();
                    var input = "passage: " + draft.Text.Substring(segment.Start, segment.Length);
                    var ids = tokenizer.Encode(input).ToArray();
                    if (ids.Length > 512)
                    {
                        splitInput = true;
                        var split = SplitAtScalarMidpoint(draft.Text, segment.Start, segment.Length);
                        Require(split > segment.Start && split < segment.Start + segment.Length,
                            "The synthetic token overflow could not split at Unicode scalar boundaries.");
                        var rightLength = segment.Start + segment.Length - split;
                        pending.Push((split, rightLength));
                        pending.Push((segment.Start, split - segment.Start));
                        Array.Clear(ids);
                        continue;
                    }

                    Require(ids.Length > 0, "The pinned tokenizer returned an empty passage input.");
                    maxFinalTokens = Math.Max(maxFinalTokens, ids.Length);
                    postings += ids.Distinct().LongCount();
                    passages++;
                    finalSegments.Add(segment);
                    Array.Clear(ids);
                }

                if (splitInput)
                {
                    overflowSourceNames.Add(source.Name);
                    overflowInputs++;
                    var reconstructed = string.Concat(finalSegments.OrderBy(segment => segment.Start)
                        .Select(segment => draft.Text.Substring(segment.Start, segment.Length)));
                    Require(string.Equals(reconstructed, draft.Text, StringComparison.Ordinal),
                        "Token-budget subsegments dropped or reordered semantic text.");
                }
            }
        }

        return new S15T2Coverage(passages, postings, maxFinalTokens, overflowInputs, overflowSourceNames.ToArray());
    }

    private static int SplitAtScalarMidpoint(string text, int start, int length)
    {
        var end = checked(start + length);
        var scalarCount = 0;
        for (var cursor = start; cursor < end; scalarCount++)
        {
            var status = System.Text.Rune.DecodeFromUtf16(text.AsSpan(cursor, end - cursor), out _, out var consumed);
            Require(status == System.Buffers.OperationStatus.Done, "A parser-produced semantic input contained invalid UTF-16.");
            cursor += consumed;
        }

        if (scalarCount < 2)
        {
            return start;
        }

        var target = scalarCount / 2;
        var count = 0;
        var position = start;
        while (position < end && count < target)
        {
            _ = System.Text.Rune.DecodeFromUtf16(text.AsSpan(position, end - position), out _, out var consumed);
            position += consumed;
            count++;
        }

        return position;
    }



    private sealed record S15T2OwnerAnnotation(
        string Id,
        string Revision,
        string Origin,
        string Review,
        string Evidence,
        string Facets,
        string AnchorId,
        string FileId,
        string ContentRevision,
        string Offset,
        string Length,
        string Digest,
        string State,
        string Availability);

    private sealed record S15T2CanonicalMemory(
        string SearchId,
        string SearchRevision,
        string SearchSnippet,
        string DetailId,
        string DetailRevision,
        string Kind,
        string Evidence,
        string Title,
        string Body);

    private static async Task<S15T2OwnerAnnotation> ReadS15T2OwnerAnnotationAsync(
        string cliExecutable,
        SmokeFixture fixture,
        string annotationId)
    {
        var result = await RunOwnerCommandAsync(cliExecutable,
            "owner", "cognitive-annotation-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", annotationId).ConfigureAwait(false);
        Require(result.ExitCode == 0,
            "The owner could not read the seeded annotation (stderr=" + result.StandardError.Trim() + ").");
        var output = result.StandardOutput;
        return new S15T2OwnerAnnotation(
            S15T2OutputField(output, "ANNOTATION ", "id"),
            S15T2OutputField(output, "ANNOTATION ", "revision"),
            S15T2OutputField(output, "ANNOTATION ", "origin"),
            S15T2OutputField(output, "ANNOTATION ", "review"),
            S15T2OutputField(output, "ANNOTATION ", "evidence"),
            S15T2OutputLine(output, "FACETS ")[7..],
            S15T2OutputField(output, "ANCHOR ", "id"),
            S15T2OutputField(output, "ANCHOR ", "file"),
            S15T2OutputField(output, "ANCHOR ", "contentRevision"),
            S15T2OutputField(output, "ANCHOR ", "offset"),
            S15T2OutputField(output, "ANCHOR ", "length"),
            S15T2OutputField(output, "ANCHOR ", "sha256"),
            S15T2OutputField(output, "ANCHOR ", "state"),
            S15T2OutputField(output, "ANCHOR ", "availability"));
    }

    private static void RequireS15T2OwnerAnnotation(
        S15T2OwnerAnnotation annotation,
        string expectedId,
        string anchorOutput,
        string expectedFileId)
    {
        Require(annotation.Id == expectedId && annotation.Revision == "1" &&
                annotation.Origin == "Owner" && annotation.Review == "Accepted" &&
                annotation.Evidence == "Unknown" && annotation.Facets == "Experience" &&
                annotation.AnchorId == WorkspaceToken(anchorOutput, "id") &&
                annotation.FileId == expectedFileId && annotation.ContentRevision == "1" &&
                annotation.Offset == "0" &&
                annotation.Length == Encoding.UTF8.GetByteCount("# S15-T2 heading π\n").ToString(CultureInfo.InvariantCulture) &&
                annotation.State == "live" && annotation.Availability == "Available",
            "Owner annotation readback did not preserve its seeded semantic fields and exact live source anchor.");
    }

    private static async Task<S15T2CanonicalMemory> ReadS15T2CanonicalMemoryAsync(
        string cliExecutable,
        SmokeFixture fixture,
        string memoryId,
        string query)
    {
        var search = await RunOwnerCommandAsync(cliExecutable,
            "owner", "library", "--control-pipe", fixture.ControlPipe, "--query", query).ConfigureAwait(false);
        Require(search.ExitCode == 0,
            "The existing canonical-memory search failed (stderr=" + search.StandardError.Trim() + ").");
        var detail = await RunOwnerCommandAsync(cliExecutable,
            "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", memoryId).ConfigureAwait(false);
        Require(detail.ExitCode == 0,
            "The existing canonical-memory detail read failed (stderr=" + detail.StandardError.Trim() + ").");
        return new S15T2CanonicalMemory(
            S15T2OutputField(search.StandardOutput, "HIT ", "id"),
            S15T2OutputField(search.StandardOutput, "HIT ", "revision"),
            S15T2OutputValue(search.StandardOutput, "snippet="),
            S15T2OutputField(detail.StandardOutput, "MEMORY ", "id"),
            S15T2OutputField(detail.StandardOutput, "MEMORY ", "revision"),
            S15T2OutputField(detail.StandardOutput, "MEMORY ", "kind"),
            S15T2OutputField(detail.StandardOutput, "MEMORY ", "evidence"),
            S15T2OutputValue(detail.StandardOutput, "title="),
            S15T2OutputValue(detail.StandardOutput, "body="));
    }

    private static void RequireS15T2CanonicalMemory(
        S15T2CanonicalMemory memory,
        string expectedId,
        string expectedTitle,
        string expectedBody)
    {
        Require(memory.SearchId == expectedId && memory.SearchRevision == "1" &&
                memory.SearchSnippet.Contains("orchid beacon phrase", StringComparison.OrdinalIgnoreCase) &&
                memory.DetailId == expectedId && memory.DetailRevision == "1" &&
                memory.Kind == "observation" && memory.Evidence == "reportedSource" &&
                memory.Title == expectedTitle && memory.Body == expectedBody,
            "Canonical-memory search/detail did not return the seeded ID, revision, content and evidence.");
    }

    private static string S15T2OutputLine(string output, string prefix) =>
        output.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))
        ?? throw new InvalidOperationException("The owner CLI omitted a required semantic output line: " + prefix);

    private static string S15T2OutputField(string output, string linePrefix, string fieldName)
    {
        var line = S15T2OutputLine(output, linePrefix);
        var prefix = fieldName + "=";
        var field = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith(prefix, StringComparison.Ordinal));
        return field is null
            ? throw new InvalidOperationException("The owner CLI omitted a required semantic field: " + fieldName)
            : field[prefix.Length..];
    }

    private static string S15T2OutputValue(string output, string prefix) =>
        S15T2OutputLine(output, prefix)[prefix.Length..];

    private static void VerifyNoPlaintextGeneration(string vaultRoot, IReadOnlyList<S15T2Source> sources)
    {
        var generationDirectory = Path.Combine(vaultRoot, "workspace-passage-index");
        var generationPath = Path.Combine(generationDirectory, "passages.v1.bin");
        Require(File.Exists(generationPath), "The encrypted passage generation was not published on disk.");
        var files = Directory.GetFiles(generationDirectory, "*", SearchOption.TopDirectoryOnly);
        Require(files.Length == 1 && string.Equals(files[0], generationPath, StringComparison.OrdinalIgnoreCase),
            "A passage staging/spill artifact remained beside the published encrypted generation.");

        var markers = S15T2PlaintextMarkers();
        foreach (var path in Directory.EnumerateFiles(vaultRoot, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(path);
            foreach (var marker in markers)
            {
                Require(bytes.AsSpan().IndexOf(marker) < 0,
                    "Synthetic source plaintext was found in a vault generation, index, cache, or workspace file.");
            }
        }

        Require(sources.Count > 0, "The ciphertext-only check had no synthetic source fixtures.");
    }

    private static byte[][] S15T2PlaintextMarkers() =>
    [
        Encoding.UTF8.GetBytes("Synthetic body café and byte-anchored text."),
        Encoding.UTF8.GetBytes("known user record"),
        Encoding.UTF8.GetBytes("raw unknown record"),
        Encoding.UTF8.GetBytes("BOM body Ω."),
        Encoding.Unicode.GetBytes("UTF-16LE source"),
        Encoding.UTF8.GetBytes("s15-t2-long-prose-marker 8d21"),
        Encoding.Unicode.GetBytes("Revised UTF-16LE source")
    ];

    /// <summary>
    /// Deterministic synthetic prose long enough that one real rebuild spends
    /// observable time between its first flushed record and its commit.
    /// </summary>
    private static byte[] BuildS15T2LongProse()
    {
        var builder = new StringBuilder(S15T2LongProseBytes + 1024);
        builder.Append("s15-t2-long-prose-marker 8d21\n");
        const string sample = "Synthetic workspace prose keeps the active rebuild long enough to observe its encrypted stage. ";
        while (builder.Length < S15T2LongProseBytes)
        {
            builder.Append(sample);
            builder.Append((builder.Length % 89).ToString(CultureInfo.InvariantCulture));
            builder.Append('\n');
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// Attempts one concurrent read of the active rebuild's stage file. A null
    /// result means the read succeeded, which would break the ciphertext-only
    /// expectation; otherwise the observed failure is returned.
    /// </summary>
    private static string? TryReadLockedStage(string path, FileShare share)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share, 4096, FileOptions.None);
            _ = stream.ReadByte();
            return null;
        }
        catch (IOException ex)
        {
            return "0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture);
        }
    }

    private static async Task<(string Path, long Length)> WaitForFlushedStageRecordAsync(
        string generationDirectory,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var stage = Directory.EnumerateFiles(generationDirectory, "*.building", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (stage is not null)
            {
                var length = new FileInfo(stage).Length;
                if (length >= S15T2MinimumFlushedStageBytes)
                {
                    return (stage, length);
                }
            }

            await Task.Delay(2).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "The active passage rebuild did not expose an encrypted stage beyond a record boundary within " +
            timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " seconds.");
    }

    private static void RequireNoPlaintextVaultFiles(string vaultRoot, string excludedPath, IReadOnlyList<byte[]> markers)
    {
        var excluded = Path.GetFullPath(excludedPath);
        foreach (var path in Directory.EnumerateFiles(vaultRoot, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFullPath(path), excluded, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            foreach (var marker in markers)
            {
                Require(bytes.AsSpan().IndexOf(marker) < 0,
                    "Synthetic source plaintext was found in a readable vault file while a rebuild was in flight.");
            }
        }
    }

    /// <summary>
    /// Builds one structurally complete passage generation in a second
    /// synthetic vault with its own key, so the first fixture can prove the
    /// generation is bound to the vault key rather than to a file layout.
    /// </summary>
    private static async Task<byte[]> ReadForeignVaultGenerationAsync(
        string connectionsExecutable,
        string cliExecutable,
        string modelDirectory)
    {
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        try
        {
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var passphrase = CreateSyntheticPassphrase();
            var provision = await RunOwnerCommandWithInputAsync(cliExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The second synthetic vault could not be provisioned (stderr=" + provision.StandardError.Trim() + ").");
            var status = await ReadWorkspaceStatusAsync(cliExecutable, fixture).ConfigureAwait(false);
            var treeRevision = WorkspaceNumber(status, "revision");
            var folder = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-mkdir", "--control-pipe", fixture.ControlPipe,
                "--parent-id", "root", "--name", "s15-t2-foreign",
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(folder.ExitCode == 0 && folder.StandardOutput.Contains("MKDIR id=", StringComparison.Ordinal),
                "The second synthetic workspace could not be initialized (stderr=" + folder.StandardError.Trim() + ").");
            var rebuild = await RunOwnerCommandAsync(cliExecutable, S15T2LongCommandTimeout,
                "owner", "workspace-index-rebuild", "--control-pipe", fixture.ControlPipe,
                "--model-directory", modelDirectory).ConfigureAwait(false);
            Require(rebuild.ExitCode == 0 && rebuild.StandardOutput.Contains("state=ready", StringComparison.Ordinal),
                "The second synthetic vault did not publish a passage generation (stderr=" + rebuild.StandardError.Trim() + ").");
            var generationPath = Path.Combine(fixture.VaultDirectoryPath, "workspace-passage-index", "passages.v1.bin");
            Require(File.Exists(generationPath), "The second synthetic vault published no passage generation file.");
            return await File.ReadAllBytesAsync(generationPath).ConfigureAwait(false);
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }
}
