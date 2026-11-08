using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MetaBrain.Core.Index;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Security;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string S15T5ModelRevision = "614241f622f53c4eeff9890bdc4f31cfecc418b3";
    private const string S15T5AnchorQuery = "t5 selected source anchor phrase";
    private static readonly TimeSpan S15T5LongCommandTimeout = TimeSpan.FromMinutes(6);

    public static async Task RunS15T5WorkspaceSearchAsync(string connectionsExecutable, string cliExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S15-T5 product smoke is Windows-only.");
        }

        RunS15T5DecodedCacheBoundsSmoke();
        var repositoryRoot = SmokeFixture.FindRepositoryRoot();
        var modelDirectory = Path.Combine(repositoryRoot, "runtime", "s15-t1-model-staging",
            S15T5ModelRevision, "model");
        Require(File.Exists(Path.Combine(modelDirectory, "onnx", "model.onnx")) &&
                File.Exists(Path.Combine(modelDirectory, "onnx", "tokenizer.json")),
            "The approved repository-local S15-T3 model assets are unavailable; no download was attempted.");

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
                "The actual owner CLI did not provision the synthetic S15-T5 vault.");
            var unlock = await RunOwnerCommandWithInputAsync(
                cliExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0 && unlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S15-T5 vault did not unlock.");

            var folder = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-mkdir", "--control-pipe", fixture.ControlPipe,
                "--parent-id", "root", "--name", "s15-t5-synthetic",
                "--expected-tree-revision", "0").ConfigureAwait(false);
            Require(folder.ExitCode == 0 && folder.StandardOutput.Contains("MKDIR id=", StringComparison.Ordinal),
                "The actual owner CLI did not create the synthetic S15-T5 workspace folder.");
            var folderId = WorkspaceToken(folder.StandardOutput, "id");
            var treeRevision = WorkspaceNumber(folder.StandardOutput, "revision");

            var anchorBytes = Encoding.UTF8.GetBytes(
                "Synthetic document before the t5 selected source anchor phrase and after it.\n");
            var anchorInput = await WriteWorkspaceInputAsync(fixture, "s15-t5-anchor-v1.txt", anchorBytes)
                .ConfigureAwait(false);
            var anchorFile = await ImportWorkspaceFileAsync(
                cliExecutable, fixture, folderId, "anchor.txt", anchorInput, treeRevision).ConfigureAwait(false);
            treeRevision = anchorFile.TreeRevision;

            var slowV1 = BuildS15T2LongProse();
            var slowInput = await WriteWorkspaceInputAsync(fixture, "s15-t5-long-v1.txt", slowV1)
                .ConfigureAwait(false);
            var slowFile = await ImportWorkspaceFileAsync(
                cliExecutable, fixture, folderId, "long.txt", slowInput, treeRevision).ConfigureAwait(false);
            treeRevision = slowFile.TreeRevision;

            var beforeSelection = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            var preSelectionIndexDirectory = Path.Combine(fixture.VaultDirectoryPath, "workspace-passage-index");
            Require(beforeSelection.ExitCode == 0 &&
                    beforeSelection.StandardOutput.Contains("state=not_materialized", StringComparison.Ordinal) &&
                    (!Directory.Exists(preSelectionIndexDirectory) ||
                     !Directory.EnumerateFiles(preSelectionIndexDirectory, "*.building", SearchOption.TopDirectoryOnly).Any()),
                "Workspace mutations without an explicit model selection started an implicit model or index build.");

            var rebuild = await RunOwnerCommandAsync(cliExecutable, S15T5LongCommandTimeout,
                "owner", "workspace-index-rebuild", "--control-pipe", fixture.ControlPipe,
                "--model-directory", modelDirectory).ConfigureAwait(false);
            Require(rebuild.ExitCode == 0 && rebuild.StandardOutput.Contains("state=ready", StringComparison.Ordinal) &&
                    rebuild.StandardOutput.Contains("stale=false", StringComparison.Ordinal),
                "The explicit owner passage-index rebuild did not publish a current synthetic generation (stderr=" +
                rebuild.StandardError.Trim() + ").");

            var generationDirectory = Path.Combine(fixture.VaultDirectoryPath, "workspace-passage-index");
            var generationPath = Path.Combine(generationDirectory, "passages.v1.bin");
            var initialGenerationHash = HashFile(generationPath);
            await RunS15T5OpenRankBrowserSmokeAsync(
                cliExecutable, fixture, folderId, modelDirectory, anchorFile.FileId, anchorBytes)
                .ConfigureAwait(false);

            var statusOnly = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            var hybridOnly = await RunOwnerCommandAsync(cliExecutable, S15T5LongCommandTimeout,
                "owner", "workspace-search", "--control-pipe", fixture.ControlPipe,
                "--query", S15T5AnchorQuery, "--hybrid", "true", "--folder-id", folderId,
                "--model-directory", modelDirectory, "--format", "json").ConfigureAwait(false);
            Require(statusOnly.ExitCode == 0 && hybridOnly.ExitCode == 0 &&
                    HashFile(generationPath) == initialGenerationHash &&
                    !Directory.EnumerateFiles(generationDirectory, "*.building", SearchOption.TopDirectoryOnly).Any(),
                "Status or search alone triggered a background generation rewrite.");

            var failedInput = await WriteWorkspaceInputAsync(fixture, "s15-t5-failed-revision.txt",
                Encoding.UTF8.GetBytes("This failed mutation must not request indexing.\n")).ConfigureAwait(false);
            var failedMutation = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                "--file-id", anchorFile.FileId, "--expected-content-revision", "99",
                "--input-file", failedInput).ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);
            Require(failedMutation.ExitCode != 0 &&
                    failedMutation.StandardError.Contains("workspace_conflict", StringComparison.Ordinal) &&
                    HashFile(generationPath) == initialGenerationHash &&
                    !Directory.EnumerateFiles(generationDirectory, "*.building", SearchOption.TopDirectoryOnly).Any(),
                "A refused owner mutation requested background indexing or changed the published generation.");
            Console.WriteLine("PASS owner search/status and refused mutation do not schedule a passage rebuild");

            var anchorV2 = Encoding.UTF8.GetBytes(
                "Synthetic document revision two before the t5 selected source anchor phrase and after it.\n");
            var anchorV2Input = await WriteWorkspaceInputAsync(fixture, "s15-t5-anchor-v2.txt", anchorV2)
                .ConfigureAwait(false);
            var anchorRevision = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                "--file-id", anchorFile.FileId, "--expected-content-revision", "1",
                "--input-file", anchorV2Input).ConfigureAwait(false);
            Require(anchorRevision.ExitCode == 0 && anchorRevision.StandardOutput.Contains("contentRevision=2", StringComparison.Ordinal),
                "A successful owner content revision did not complete.");
            treeRevision = WorkspaceNumber(anchorRevision.StandardOutput, "revision");
            var afterSuccessfulMutation = await WaitForS15T5CurrentIndexAsync(
                cliExecutable, fixture, treeRevision).ConfigureAwait(false);
            var afterSmallRefreshHash = HashFile(generationPath);
            Require(afterSuccessfulMutation.Contains("state=ready", StringComparison.Ordinal) &&
                    !string.Equals(afterSmallRefreshHash, initialGenerationHash, StringComparison.Ordinal),
                "A successful owner content mutation did not publish a fresh encrypted generation.");
            Console.WriteLine("PASS a successful owner content revision triggers current background indexing");

            var slowV2 = AppendS15T5Marker(slowV1, "S15_T5_COALESCED_REVISION_TWO");
            var slowV2Input = await WriteWorkspaceInputAsync(fixture, "s15-t5-long-v2.txt", slowV2)
                .ConfigureAwait(false);
            var slowRevision2 = await RunOwnerCommandAsync(cliExecutable, S15T5LongCommandTimeout,
                "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                "--file-id", slowFile.FileId, "--expected-content-revision", "1",
                "--input-file", slowV2Input).ConfigureAwait(false);
            Require(slowRevision2.ExitCode == 0 && slowRevision2.StandardOutput.Contains("contentRevision=2", StringComparison.Ordinal),
                "The first coalescing-probe content revision failed.");
            var (stagePath, stageBytes) = await WaitForFlushedStageRecordAsync(
                generationDirectory, S15T2StageWaitTimeout).ConfigureAwait(false);
            Require(stageBytes >= S15T2MinimumFlushedStageBytes,
                "The owner-mutation-triggered background worker did not expose a flushed encrypted staging generation.");
            var stageMemory = ReadS15T5ProcessSample(service.Process);

            var responsiveReadPath = Path.Combine(fixture.OutputDirectory, "s15-t5-responsive-read.bin");
            var readTimer = Stopwatch.StartNew();
            var concurrentRead = await RunOwnerCommandAsync(cliExecutable, TimeSpan.FromSeconds(30),
                "owner", "workspace-read", "--control-pipe", fixture.ControlPipe,
                "--file-id", anchorFile.FileId, "--revision", "2", "--output-file", responsiveReadPath)
                .ConfigureAwait(false);
            readTimer.Stop();
            var concurrentReadBytes = await File.ReadAllBytesAsync(responsiveReadPath).ConfigureAwait(false);
            Require(concurrentRead.ExitCode == 0 && concurrentRead.StandardOutput.Contains("READ content bytes=", StringComparison.Ordinal) &&
                    concurrentReadBytes.AsSpan().SequenceEqual(anchorV2) && File.Exists(stagePath),
                "An owner document read did not complete with exact bytes while background indexing held a flushed stage.");
            Console.WriteLine("OBSERVATION owner document read during background indexing elapsedMs=" +
                readTimer.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture) +
                " (30-second smoke safety timeout; no product latency gate implied)");

            var slowV3 = AppendS15T5Marker(slowV2, "S15_T5_COALESCED_REVISION_THREE");
            var slowV3Input = await WriteWorkspaceInputAsync(fixture, "s15-t5-long-v3.txt", slowV3)
                .ConfigureAwait(false);
            var slowRevision3 = await RunOwnerCommandAsync(cliExecutable, S15T5LongCommandTimeout,
                "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                "--file-id", slowFile.FileId, "--expected-content-revision", "2",
                "--input-file", slowV3Input).ConfigureAwait(false);
            Require(slowRevision3.ExitCode == 0 && slowRevision3.StandardOutput.Contains("contentRevision=3", StringComparison.Ordinal),
                "The second coalescing-probe content revision failed.");
            treeRevision = WorkspaceNumber(slowRevision3.StandardOutput, "revision");
            var coalescedStatus = await WaitForS15T5CurrentIndexAsync(
                cliExecutable, fixture, treeRevision).ConfigureAwait(false);
            var indexedIdleMemory = ReadS15T5ProcessSample(service.Process);
            var indexedQuery = await HybridSearchJsonAsync(
                cliExecutable, fixture, "S15 T5 coalesced revision three", folderId, modelDirectory,
                limit: WorkspaceHybridSearchService.MaximumLimit).ConfigureAwait(false);
            var coalescedHit = indexedQuery.GetProperty("hits").EnumerateArray().FirstOrDefault(hit =>
                string.Equals(hit.GetProperty("fileId").GetString(), slowFile.FileId, StringComparison.Ordinal) &&
                hit.GetProperty("contentRevision").GetInt64() == 3);
            Require(coalescedStatus.Contains("state=ready", StringComparison.Ordinal) &&
                    !coalescedStatus.Contains("stale=true", StringComparison.Ordinal) &&
                    coalescedHit.ValueKind == JsonValueKind.Object,
                "A mutation arriving during the background build was not retained through the currentness retry.");
            await VerifyS15T5HybridSourceAsync(cliExecutable, fixture, coalescedHit).ConfigureAwait(false);
            Console.WriteLine("PASS coalesced background refresh retained the in-flight mutation and exact source revision");

            var slowV4 = AppendS15T5Marker(slowV3, "S15_T5_LOCK_CANCELLATION_PROBE");
            var slowV4Input = await WriteWorkspaceInputAsync(fixture, "s15-t5-long-v4.txt", slowV4)
                .ConfigureAwait(false);
            var beforeLockCancellationHash = HashFile(generationPath);
            var slowRevision4 = await RunOwnerCommandAsync(cliExecutable, S15T5LongCommandTimeout,
                "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                "--file-id", slowFile.FileId, "--expected-content-revision", "3",
                "--input-file", slowV4Input).ConfigureAwait(false);
            Require(slowRevision4.ExitCode == 0 && slowRevision4.StandardOutput.Contains("contentRevision=4", StringComparison.Ordinal),
                "The background lock-cancellation probe mutation failed.");
            var (cancelledStagePath, cancelledStageBytes) = await WaitForFlushedStageRecordAsync(
                generationDirectory, S15T2StageWaitTimeout).ConfigureAwait(false);
            var cancellationStageMemory = ReadS15T5ProcessSample(service.Process);
            var lockDuringBackground = await RunOwnerCommandAsync(cliExecutable, S15T5LongCommandTimeout,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockDuringBackground.ExitCode == 0 &&
                    lockDuringBackground.StandardOutput.Contains("VAULT locked", StringComparison.Ordinal),
                "Owner lock did not cancel and drain active background indexing.");
            Require(!File.Exists(cancelledStagePath) &&
                    !Directory.EnumerateFiles(generationDirectory, "*.building", SearchOption.TopDirectoryOnly).Any() &&
                    HashFile(generationPath) == beforeLockCancellationHash,
                "Vault lock did not remove the background stage while preserving published ciphertext byte-for-byte.");
            Console.WriteLine("OBSERVATION background memory stagePrivateBytes=" +
                stageMemory.PrivateBytes.ToString(CultureInfo.InvariantCulture) +
                " postIndexIdlePrivateBytes=" + indexedIdleMemory.PrivateBytes.ToString(CultureInfo.InvariantCulture) +
                " cancellationStagePrivateBytes=" + cancellationStageMemory.PrivateBytes.ToString(CultureInfo.InvariantCulture) +
                " cancelledStageBytes=" + cancelledStageBytes.ToString(CultureInfo.InvariantCulture) +
                " publishedUnchanged=true");
            Console.WriteLine("PASS vault lock cancels background work, removes its stage, and retains the previous ciphertext");

            var unlockAgain = await RunOwnerCommandWithInputAsync(cliExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlockAgain.ExitCode == 0 && unlockAgain.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic vault did not unlock after background lock cancellation.");
            var anchorV3 = Encoding.UTF8.GetBytes("Synthetic document revision three after explicit model reselection.\n");
            var anchorV3Input = await WriteWorkspaceInputAsync(fixture, "s15-t5-anchor-v3.txt", anchorV3)
                .ConfigureAwait(false);
            var anchorRevision3 = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                "--file-id", anchorFile.FileId, "--expected-content-revision", "2",
                "--input-file", anchorV3Input).ConfigureAwait(false);
            Require(anchorRevision3.ExitCode == 0 && anchorRevision3.StandardOutput.Contains("contentRevision=3", StringComparison.Ordinal),
                "The post-unlock successful mutation failed.");
            treeRevision = WorkspaceNumber(anchorRevision3.StandardOutput, "revision");
            var noModelStatus = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);
            Require(noModelStatus.ExitCode == 0 &&
                    noModelStatus.StandardOutput.Contains("state=stale", StringComparison.Ordinal) &&
                    !noModelStatus.StandardOutput.Contains("stale=false", StringComparison.Ordinal) &&
                    !Directory.EnumerateFiles(generationDirectory, "*.building", SearchOption.TopDirectoryOnly).Any(),
                "A post-unlock mutation ran background indexing without a newly explicit in-memory model selection.");
            var explicitSelection = await RunOwnerCommandAsync(cliExecutable, S15T5LongCommandTimeout,
                "owner", "workspace-search", "--control-pipe", fixture.ControlPipe,
                "--query", "t5 post lock explicit model selection", "--hybrid", "true",
                "--folder-id", folderId, "--model-directory", modelDirectory, "--format", "json")
                .ConfigureAwait(false);
            Require(explicitSelection.ExitCode == 3 &&
                    explicitSelection.StandardError.Contains("workspace_index_stale", StringComparison.Ordinal),
                "The stale query did not refuse before an explicit model selection could unlock pending work.");
            var reselectedStatus = await WaitForS15T5CurrentIndexAsync(
                cliExecutable, fixture, treeRevision).ConfigureAwait(false);
            Require(reselectedStatus.Contains("stale=false", StringComparison.Ordinal),
                "An explicitly supplied model directory did not resume the pending successful-mutation refresh.");
            Console.WriteLine("PASS lock clears epoch model selection; a successful mutation stays stale until explicit reselection");

            var validGeneration = await File.ReadAllBytesAsync(generationPath).ConfigureAwait(false);
            var tamperedGeneration = (byte[])validGeneration.Clone();
            tamperedGeneration[60] ^= 0x80;
            await File.WriteAllBytesAsync(generationPath, tamperedGeneration).ConfigureAwait(false);
            var corruptedStatus = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-index-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(corruptedStatus.ExitCode != 0 &&
                    corruptedStatus.StandardError.Contains("workspace_index_unavailable", StringComparison.Ordinal),
                "An authenticated-generation corruption was not refused by owner status.");

            var failedAfterCorruption = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                "--file-id", anchorFile.FileId, "--expected-content-revision", "99",
                "--input-file", failedInput).ConfigureAwait(false);
            Require(failedAfterCorruption.ExitCode != 0 &&
                    failedAfterCorruption.StandardError.Contains("workspace_conflict", StringComparison.Ordinal),
                "The corruption negative's failed mutation unexpectedly succeeded.");
            await Task.Delay(300).ConfigureAwait(false);
            Require((await File.ReadAllBytesAsync(generationPath).ConfigureAwait(false)).AsSpan().SequenceEqual(tamperedGeneration),
                "A status request or refused mutation replaced the corrupted published generation.");

            var corruptTriggerInput = await WriteWorkspaceInputAsync(fixture, "s15-t5-corrupt-trigger.txt",
                Encoding.UTF8.GetBytes("S15_T5_CORRUPT_GENERATION_TRIGGER\n")).ConfigureAwait(false);
            var corruptTrigger = await RunOwnerCommandAsync(cliExecutable,
                "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                "--file-id", anchorFile.FileId, "--expected-content-revision", "3",
                "--input-file", corruptTriggerInput).ConfigureAwait(false);
            Require(corruptTrigger.ExitCode == 0 && corruptTrigger.StandardOutput.Contains("contentRevision=4", StringComparison.Ordinal),
                "The successful corruption-path trigger mutation failed.");
            await Task.Delay(500).ConfigureAwait(false);
            Require((await File.ReadAllBytesAsync(generationPath).ConfigureAwait(false)).AsSpan().SequenceEqual(tamperedGeneration) &&
                    !Directory.EnumerateFiles(generationDirectory, "*.building", SearchOption.TopDirectoryOnly).Any(),
                "Incremental corruption handling replaced the authenticated old generation or left a staging artifact.");

            var backgroundErrors = service.StandardErrorDrain;
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            var errorOutput = await backgroundErrors.ConfigureAwait(false);
            Require(errorOutput.Contains("workspace passage background refresh refused (workspace_index_canonical_moved).", StringComparison.Ordinal) &&
                    errorOutput.Contains("workspace passage background refresh failed (workspace_index_generation_authentication_failed).", StringComparison.Ordinal),
                "The service did not observe both the coalesced snapshot retry and fail-closed incremental authentication refusal.");
            Console.WriteLine("PASS incremental generation corruption remains fail-closed without replacing published bytes");
            Console.WriteLine("PASS service observed canonical-moved retry and authenticated-generation refusal");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static async Task RunS15T5OpenRankBrowserSmokeAsync(
        string cliExecutable,
        SmokeFixture fixture,
        string folderId,
        string modelDirectory,
        string expectedFileId,
        byte[] expectedSource)
    {
        Process? openProcess = null;
        S13T1Browser? browser = null;
        try
        {
            var started = await StartS15T5OpenRankAsync(
                cliExecutable, fixture, folderId, modelDirectory).ConfigureAwait(false);
            openProcess = started.Process;
            var hit = started.Hit;
            Require(string.Equals(hit.GetProperty("fileId").GetString(), expectedFileId, StringComparison.Ordinal) &&
                    hit.GetProperty("contentRevision").GetInt64() == 1,
                "The selected rank did not resolve to the expected exact file revision.");
            var fileId = hit.GetProperty("fileId").GetString()!;
            var revision = hit.GetProperty("contentRevision").GetInt64();
            var offset = hit.GetProperty("byteOffset").GetInt64();
            var length = hit.GetProperty("byteLength").GetInt64();
            var anchorSha256 = hit.GetProperty("anchorSha256").GetString()!;
            Require(offset >= 0 && length > 0 && offset <= expectedSource.LongLength &&
                    length <= expectedSource.LongLength - offset,
                "The CLI open-rank hit did not carry an in-range exact byte anchor.");
            var expectedMark = Encoding.UTF8.GetString(expectedSource.AsSpan(checked((int)offset), checked((int)length)));
            Require(string.Equals(
                    Convert.ToHexString(SHA256.HashData(expectedSource.AsSpan(
                        checked((int)offset), checked((int)length)))).ToLowerInvariant(),
                    anchorSha256, StringComparison.Ordinal),
                "The CLI open-rank anchor digest did not match the exact expected revision bytes.");

            var fields = ParseS15T5OpenAnnouncement(started.OpenLine);
            Require(fields["id"] == fileId && fields["revision"] == revision.ToString(CultureInfo.InvariantCulture) &&
                    fields["anchorOffset"] == offset.ToString(CultureInfo.InvariantCulture) &&
                    fields["anchorLength"] == length.ToString(CultureInfo.InvariantCulture) &&
                    fields["anchorSha256"] == anchorSha256,
                "The open-rank CLI did not pass the selected result's exact revision and byte anchor to document open.");

            var sourceReadback = await ReadWorkspaceFileAsync(
                cliExecutable, fixture, fileId, revision, "s15-t5-open-rank-source.bin").ConfigureAwait(false);
            Require(sourceReadback.AsSpan().SequenceEqual(expectedSource),
                "The owner CLI source read did not return the exact revision selected by --open-rank.");

            var profile = Path.Combine(fixture.OutputDirectory, "s15-t5-chromium-" + Guid.NewGuid().ToString("N"));
            browser = await S13T1Browser.StartAsync(S13T1ChromePath(), profile, started.Url).ConfigureAwait(false);
            var selectedMark = await browser.WaitForAsync(
                "document.querySelector('#selected-anchor')?.textContent || ''",
                value => value.Length > 0,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            var status = await browser.WaitForAsync(
                "document.getElementById('status').textContent",
                value => value.Contains("selected revision-pinned search hit", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Require(string.Equals(selectedMark, expectedMark, StringComparison.Ordinal) &&
                    status.Contains("exact bound source revision", StringComparison.Ordinal),
                "The actual browser did not display the exact search-selected source anchor in its pinned revision.");
            Console.WriteLine("PASS actual CLI --open-rank launched the exact revision/byte anchor in Chromium via the document bridge");
        }
        finally
        {
            if (browser is not null)
            {
                await browser.DisposeAsync().ConfigureAwait(false);
            }

            if (openProcess is not null)
            {
                await S13T1StopOpenAsync(openProcess).ConfigureAwait(false);
            }
        }
    }

    private static async Task<(Process Process, JsonElement Hit, string OpenLine, string Url)> StartS15T5OpenRankAsync(
        string cliExecutable,
        SmokeFixture fixture,
        string folderId,
        string modelDirectory)
    {
        var start = new ProcessStartInfo(cliExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "owner", "workspace-search", "--control-pipe", fixture.ControlPipe,
                     "--query", S15T5AnchorQuery, "--hybrid", "true", "--folder-id", folderId,
                     "--model-directory", modelDirectory, "--format", "json", "--open-rank", "1",
                     "--open-browser", "false",
                 })
        {
            start.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = start };
        Require(process.Start(), "The actual owner CLI open-rank process did not start.");
        process.StandardInput.Close();
        try
        {
            var pageLine = await process.StandardOutput.ReadLineAsync()
                .WaitAsync(S15T5LongCommandTimeout).ConfigureAwait(false);
            if (pageLine is null)
            {
                var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                throw new InvalidOperationException("The hybrid CLI did not return its search page before open-rank (stderr=" + error.Trim() + ").");
            }

            using var pageDocument = JsonDocument.Parse(pageLine);
            var hits = pageDocument.RootElement.GetProperty("hits");
            var hit = hits.EnumerateArray().FirstOrDefault(candidate =>
                candidate.TryGetProperty("rank", out var rank) && rank.GetInt32() == 1);
            Require(hit.ValueKind == JsonValueKind.Object,
                "The actual hybrid CLI returned no rank-1 result for the open-rank browser scenario.");
            var openLine = await process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (openLine is null || !openLine.StartsWith("OPEN document ", StringComparison.Ordinal))
            {
                var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                throw new InvalidOperationException("The actual CLI did not start the selected document session (line=" +
                    (openLine ?? "<none>") + "; stderr=" + error.Trim() + ").");
            }

            var urlMatch = Regex.Match(openLine, @"(?:^| )url=(\S+)$");
            Require(urlMatch.Success, "The open-rank document session did not announce its browser URL.");
            return (process, hit.Clone(), openLine, urlMatch.Groups[1].Value);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync().WaitAsync(CleanupTimeout).ConfigureAwait(false);
            }
            catch
            {
            }

            process.Dispose();
            throw;
        }
    }

    private static Dictionary<string, string> ParseS15T5OpenAnnouncement(string line)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(line, @"(?:^| )(?<name>[A-Za-z][A-Za-z0-9]*)=(?<value>\S+)"))
        {
            fields[match.Groups["name"].Value] = match.Groups["value"].Value;
        }

        return fields;
    }

    private static async Task<string> WaitForS15T5CurrentIndexAsync(
        string cliExecutable,
        SmokeFixture fixture,
        long expectedWorkspaceRevision)
    {
        var deadline = DateTime.UtcNow + S15T2StageWaitTimeout;
        string lastOutput = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            var status = await RunOwnerCommandAsync(cliExecutable, S15T5LongCommandTimeout,
                "owner", "workspace-index-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            if (status.ExitCode == 0)
            {
                lastOutput = status.StandardOutput;
                if (lastOutput.Contains("stale=false", StringComparison.Ordinal) &&
                    WorkspaceNumber(lastOutput, "revision") == expectedWorkspaceRevision)
                {
                    return lastOutput;
                }
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new InvalidOperationException("The T5 background index did not become current at workspace revision " +
            expectedWorkspaceRevision.ToString(CultureInfo.InvariantCulture) + " (last status=" + lastOutput.Trim() + ").");
    }

    private static async Task VerifyS15T5HybridSourceAsync(
        string cliExecutable,
        SmokeFixture fixture,
        JsonElement hit)
    {
        var fileId = hit.GetProperty("fileId").GetString()!;
        var revision = hit.GetProperty("contentRevision").GetInt64();
        var source = await ReadWorkspaceFileAsync(cliExecutable, fixture, fileId, revision,
            "s15-t5-coalesced-source.bin").ConfigureAwait(false);
        Require(string.Equals(Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant(),
                hit.GetProperty("fileSha256").GetString(), StringComparison.Ordinal),
            "The coalesced hybrid query's whole-file digest did not match its exact source revision.");
        var offset = hit.GetProperty("byteOffset").GetInt64();
        var length = hit.GetProperty("byteLength").GetInt64();
        Require(offset >= 0 && length > 0 && offset <= source.LongLength && length <= source.LongLength - offset,
            "The coalesced hybrid query returned an out-of-range source anchor.");
        var span = source.AsSpan(checked((int)offset), checked((int)length));
        Require(string.Equals(Convert.ToHexString(SHA256.HashData(span)).ToLowerInvariant(),
                hit.GetProperty("anchorSha256").GetString(), StringComparison.Ordinal) &&
                Encoding.UTF8.GetString(span).Contains("S15_T5_COALESCED_REVISION_THREE", StringComparison.Ordinal),
            "The coalesced current source anchor did not include its exact revision-three marker.");
    }

    private static void RunS15T5DecodedCacheBoundsSmoke()
    {
        var cache = new DecodedWorkspacePassageCache();
        var tokenIds = new[] { 7, 11, 13 };
        var vector = Enumerable.Repeat(0.25f, 384).ToArray();
        DecodedWorkspacePassageGroup MakeGroup(string fileId, int estimatedBytes, int tokenValue) =>
            new(fileId, 1, "model-v1",
                [new DecodedWorkspacePassage(
                    new WorkspacePassageAnchor(fileId, 1, 0, 1, new string('a', 64), "text", "unknown", 0),
                    [tokenValue],
                    (float[])vector.Clone(),
                    0)],
                estimatedBytes);

        var groupA = MakeGroup("cache-a", DecodedWorkspacePassageCache.CapacityBytes / 3, tokenIds[0]);
        var groupB = MakeGroup("cache-b", DecodedWorkspacePassageCache.CapacityBytes / 3, tokenIds[1]);
        var groupC = MakeGroup("cache-c", DecodedWorkspacePassageCache.CapacityBytes / 2, tokenIds[2]);
        cache.Add(groupA);
        cache.Add(groupB);
        Require(cache.TryGet("cache-a", 1, "model-v1", out _),
            "The decoded passage cache did not find an exact file/revision/model key.");
        cache.Add(groupC);
        Require(cache.Bytes <= DecodedWorkspacePassageCache.CapacityBytes &&
                cache.TryGet("cache-a", 1, "model-v1", out _) &&
                !cache.TryGet("cache-b", 1, "model-v1", out _) &&
                cache.TryGet("cache-c", 1, "model-v1", out _) &&
                groupB.Passages[0].TokenIds.All(value => value == 0) &&
                groupB.Passages[0].Vector.All(value => value == 0),
            "The decoded passage LRU exceeded its cap or failed to zero the evicted group's numeric arrays.");

        var oversized = MakeGroup("cache-too-large", DecodedWorkspacePassageCache.CapacityBytes + 1, 17);
        cache.Add(oversized);
        Require(cache.Bytes <= DecodedWorkspacePassageCache.CapacityBytes &&
                oversized.Passages[0].TokenIds.All(value => value == 0) &&
                oversized.Passages[0].Vector.All(value => value == 0),
            "The decoded passage cache retained an oversized group or failed to zero it.");
        cache.Clear();
        Require(cache.Bytes == 0 &&
                groupA.Passages[0].TokenIds.All(value => value == 0) &&
                groupA.Passages[0].Vector.All(value => value == 0) &&
                groupC.Passages[0].TokenIds.All(value => value == 0) &&
                groupC.Passages[0].Vector.All(value => value == 0),
            "Clearing the decoded passage cache did not zero all resident numeric arrays.");
        Console.WriteLine("PASS decoded passage LRU byte cap, eviction and lock-clear zeroing");
    }

    private static byte[] AppendS15T5Marker(byte[] source, string marker)
    {
        var suffix = Encoding.UTF8.GetBytes("\n" + marker + "\n");
        return source.Concat(suffix).ToArray();
    }

    private static (long PrivateBytes, long WorkingSet, long PeakWorkingSet) ReadS15T5ProcessSample(Process process)
    {
        process.Refresh();
        return (process.PrivateMemorySize64, process.WorkingSet64, process.PeakWorkingSet64);
    }
}
