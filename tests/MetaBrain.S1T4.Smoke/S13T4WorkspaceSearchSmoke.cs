using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S13-T4 exercises exact workspace search through the owner CLI and opens one
/// revision-pinned text hit in actual Chromium. All files and the vault fixture
/// are synthetic and confined to the existing SmokeFixture.
/// </summary>
internal static partial class OwnerServiceSmoke
{
    public static async Task RunS13T4WorkspaceSearchAsync(string serviceExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S13-T4 workspace-search smoke is Windows-only.");
        }

        var executable = Path.Combine(AppContext.BaseDirectory, "MetaBrain.Cli.exe");
        Require(File.Exists(executable), "The packaged MetaBrain.Cli.exe was not present in the smoke output.");

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
                "The owner CLI did not provision the synthetic S13-T4 vault (stderr=" + provision.StandardError.Trim() + ").");

            var selected = await RunOwnerCommandAsync(executable,
                "owner", "workspace-mkdir", "--control-pipe", fixture.ControlPipe,
                "--parent-id", "root", "--name", "selected-area", "--expected-tree-revision", "0").ConfigureAwait(false);
            Require(selected.ExitCode == 0,
                "The synthetic selected folder could not be created (exit=" + selected.ExitCode.ToString(CultureInfo.InvariantCulture) +
                "; stdout=" + selected.StandardOutput.Trim() + "; stderr=" + selected.StandardError.Trim() + ").");
            var selectedId = WorkspaceToken(selected.StandardOutput, "id");
            var treeRevision = WorkspaceNumber(selected.StandardOutput, "revision");

            var nested = await RunOwnerCommandAsync(executable,
                "owner", "workspace-mkdir", "--control-pipe", fixture.ControlPipe,
                "--parent-id", selectedId, "--name", "nested", "--expected-tree-revision",
                treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(nested.ExitCode == 0, "The synthetic nested folder could not be created.");
            var nestedId = WorkspaceToken(nested.StandardOutput, "id");
            treeRevision = WorkspaceNumber(nested.StandardOutput, "revision");

            var outside = await RunOwnerCommandAsync(executable,
                "owner", "workspace-mkdir", "--control-pipe", fixture.ControlPipe,
                "--parent-id", "root", "--name", "outside-area", "--expected-tree-revision",
                treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(outside.ExitCode == 0, "The synthetic outside folder could not be created.");
            var outsideId = WorkspaceToken(outside.StandardOutput, "id");
            treeRevision = WorkspaceNumber(outside.StandardOutput, "revision");

            const string content = "# HeadingAnchorExact\r\n\r\nSymbol: ReadyStateExact;\r\nPhrase: Owner Workspace Search Exact.\r\n";
            const string fileName = "T4 exact-symbol.md";
            var input = await WriteWorkspaceInputAsync(fixture, "s13t4-selected.md", Encoding.UTF8.GetBytes(content)).ConfigureAwait(false);
            var imported = await ImportWorkspaceFileAsync(executable, fixture, nestedId, fileName, input, treeRevision)
                .ConfigureAwait(false);
            var fileId = imported.FileId;
            treeRevision = imported.TreeRevision;

            var outsideBytes = Encoding.UTF8.GetBytes("outsideFolderOnlyPhrase\r\n");
            var outsideInput = await WriteWorkspaceInputAsync(fixture, "s13t4-outside.txt", outsideBytes).ConfigureAwait(false);
            var outsideImport = await ImportWorkspaceFileAsync(
                executable, fixture, outsideId, "outside.txt", outsideInput, treeRevision).ConfigureAwait(false);
            treeRevision = outsideImport.TreeRevision;
            const string utf16Text = "Unicode UTF16AnchorExact.";
            var utf16Bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(utf16Text)).ToArray();
            var utf16Input = await WriteWorkspaceInputAsync(fixture, "s13t4-utf16.txt", utf16Bytes).ConfigureAwait(false);
            var utf16Import = await ImportWorkspaceFileAsync(
                executable, fixture, nestedId, "utf16-note.txt", utf16Input, treeRevision).ConfigureAwait(false);
            treeRevision = utf16Import.TreeRevision;
            const string utf16BeText = "Unicode UTF16BEAnchorExact.";
            var utf16BeBytes = new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(utf16BeText)).ToArray();
            var utf16BeInput = await WriteWorkspaceInputAsync(fixture, "s13t4-utf16be.txt", utf16BeBytes).ConfigureAwait(false);
            var utf16BeImport = await ImportWorkspaceFileAsync(
                executable, fixture, nestedId, "utf16be-note.txt", utf16BeInput, treeRevision).ConfigureAwait(false);
            treeRevision = utf16BeImport.TreeRevision;

            const string unicodeText = "𐐀BoundaryOnly;\r\ne\u0301CombiningBoundaryOnly;\r\n";
            var unicodeInput = await WriteWorkspaceInputAsync(
                fixture, "s13t4-unicode-symbols.txt", Encoding.UTF8.GetBytes(unicodeText)).ConfigureAwait(false);
            var unicodeImport = await ImportWorkspaceFileAsync(
                executable, fixture, nestedId, "unicode-symbols.txt", unicodeInput, treeRevision).ConfigureAwait(false);
            treeRevision = unicodeImport.TreeRevision;

            async Task<JsonElement> SearchJsonAsync(string query, string? folderFilter = null)
            {
                var arguments = new List<string>
                {
                    "owner", "workspace-search", "--control-pipe", fixture.ControlPipe,
                    "--query", query, "--format", "json",
                };
                if (folderFilter is not null)
                {
                    arguments.Add("--folder-id");
                    arguments.Add(folderFilter);
                }

                var result = await RunOwnerCommandAsync(executable, arguments.ToArray()).ConfigureAwait(false);
                Require(result.ExitCode == 0, "The owner CLI workspace search failed for " + query + " (stderr=" + result.StandardError.Trim() + ").");
                using var document = JsonDocument.Parse(result.StandardOutput);
                return document.RootElement.Clone();
            }

            var partialUnicodeSymbol = await SearchJsonAsync("BoundaryOnly", nestedId).ConfigureAwait(false);
            Require(partialUnicodeSymbol.GetProperty("total").GetInt32() == 0,
                "Identifier matching split an astral Unicode letter or a combining mark boundary.");
            var exactUnicodeSymbol = await SearchJsonAsync("𐐀BoundaryOnly", nestedId).ConfigureAwait(false);
            Require(exactUnicodeSymbol.GetProperty("hits").EnumerateArray().Single().GetProperty("field").GetString() == "content",
                "An exact symbol containing an astral Unicode identifier character was not found.");

            var headingResult = await SearchJsonAsync("HeadingAnchorExact", nestedId).ConfigureAwait(false);
            var heading = headingResult.GetProperty("hits").EnumerateArray().Single();
            Require(heading.GetProperty("field").GetString() == "heading" && heading.GetProperty("line").GetInt32() == 1,
                "The shared search did not report the Markdown heading at its exact source line.");
            var headingOffset = heading.GetProperty("byteOffset").GetInt64();
            var headingLength = heading.GetProperty("byteLength").GetInt64();

            var pathResult = await SearchJsonAsync("nested", selectedId).ConfigureAwait(false);
            Require(pathResult.GetProperty("hits").EnumerateArray().Any(hit => hit.GetProperty("field").GetString() == "path"),
                "A logical folder path match was not reported independently from a filename match.");

            var phraseResult = await SearchJsonAsync("owner workspace search exact", selectedId).ConfigureAwait(false);
            var phrase = phraseResult.GetProperty("hits").EnumerateArray().Single();
            Require(phrase.GetProperty("field").GetString() == "content" && phrase.GetProperty("line").GetInt32() == 4,
                "The exact phrase search did not report its source line and content location.");

            var symbolResult = await SearchJsonAsync("readystateexact", nestedId).ConfigureAwait(false);
            var symbol = symbolResult.GetProperty("hits").EnumerateArray().Single();
            Require(symbol.GetProperty("field").GetString() == "content" && symbol.GetProperty("line").GetInt32() == 3,
                "The case-insensitive exact symbol search did not report a content hit.");
            var symbolOffset = symbol.GetProperty("byteOffset").GetInt64();
            var symbolLength = symbol.GetProperty("byteLength").GetInt64();
            var symbolRevision = symbol.GetProperty("contentRevision").GetInt64();
            var symbolDigest = symbol.GetProperty("anchorSha256").GetString() ?? string.Empty;
            Require(symbolRevision == 1 && symbolLength == Encoding.UTF8.GetByteCount("ReadyStateExact") && symbolDigest.Length == 64,
                "The structured search result did not pin the exact file revision and matched byte range.");
            var utf16Result = await SearchJsonAsync("utf16anchorexact", nestedId).ConfigureAwait(false);
            var utf16Hit = utf16Result.GetProperty("hits").EnumerateArray().Single();
            var utf16Offset = utf16Hit.GetProperty("byteOffset").GetInt64();
            var utf16Length = utf16Hit.GetProperty("byteLength").GetInt64();
            var utf16Readback = await ReadWorkspaceFileAsync(
                executable, fixture, utf16Import.FileId, 1, "s13t4-utf16-readback.txt").ConfigureAwait(false);
            Require(utf16Hit.GetProperty("field").GetString() == "content" &&
                    utf16Offset >= 2 &&
                    utf16Length == Encoding.Unicode.GetByteCount("UTF16AnchorExact") &&
                    Encoding.Unicode.GetString(utf16Readback.AsSpan(checked((int)utf16Offset), checked((int)utf16Length))) == "UTF16AnchorExact" &&
                    Convert.ToHexString(SHA256.HashData(utf16Readback.AsSpan(checked((int)utf16Offset), checked((int)utf16Length))))
                        .Equals(utf16Hit.GetProperty("anchorSha256").GetString(), StringComparison.OrdinalIgnoreCase),
                "A BOM-marked UTF-16 match did not carry its exact original-byte range and digest.");
            var utf16Open = await S13T4StartAnchorOpenAsync(
                executable,
                fixture,
                utf16Import.FileId,
                1,
                utf16Offset,
                utf16Length,
                utf16Hit.GetProperty("anchorSha256").GetString() ?? string.Empty).ConfigureAwait(false);
            await S13T1StopOpenAsync(utf16Open.Process).ConfigureAwait(false);

            RequireBytes(
                await ReadWorkspaceFileAsync(
                    executable,
                    fixture,
                    utf16Import.FileId,
                    utf16Hit.GetProperty("contentRevision").GetInt64(),
                    "s13t4-utf16-after-open.txt").ConfigureAwait(false),
                utf16Readback,
                "opening a UTF-16 search anchor did not rewrite original bytes");

            var utf16BeResult = await SearchJsonAsync("utf16beanchorexact", nestedId).ConfigureAwait(false);
            var utf16BeHit = utf16BeResult.GetProperty("hits").EnumerateArray().Single();
            var utf16BeOffset = utf16BeHit.GetProperty("byteOffset").GetInt64();
            var utf16BeLength = utf16BeHit.GetProperty("byteLength").GetInt64();
            var utf16BeReadback = await ReadWorkspaceFileAsync(
                executable, fixture, utf16BeImport.FileId, 1, "s13t4-utf16be-readback.txt").ConfigureAwait(false);
            Require(
                utf16BeOffset >= 2 &&
                utf16BeLength == Encoding.BigEndianUnicode.GetByteCount("UTF16BEAnchorExact") &&
                Encoding.BigEndianUnicode.GetString(utf16BeReadback.AsSpan(
                    checked((int)utf16BeOffset), checked((int)utf16BeLength))) == "UTF16BEAnchorExact" &&
                Convert.ToHexString(SHA256.HashData(utf16BeReadback.AsSpan(
                    checked((int)utf16BeOffset), checked((int)utf16BeLength))))
                    .Equals(utf16BeHit.GetProperty("anchorSha256").GetString(), StringComparison.OrdinalIgnoreCase),
                "A BOM-marked big-endian UTF-16 match did not carry its exact original-byte range and digest.");

            var nameResult = await SearchJsonAsync(fileName, nestedId).ConfigureAwait(false);
            var nameHits = nameResult.GetProperty("hits").EnumerateArray().ToArray();
            Require(nameHits.Length > 0 && nameHits[0].GetProperty("field").GetString() == "name" &&
                    nameHits[0].GetProperty("evidence").GetString() == fileName,
                "An exact filename match was not ranked as the leading name result.");

            var textSearch = await RunOwnerCommandAsync(executable,
                "owner", "workspace-search", "--control-pipe", fixture.ControlPipe,
                "--query", "ReadyStateExact").ConfigureAwait(false);
            Require(textSearch.ExitCode == 0 && textSearch.StandardOutput.Contains("field=content", StringComparison.Ordinal) &&
                    textSearch.StandardOutput.Contains("line=3", StringComparison.Ordinal) &&
                    textSearch.StandardOutput.Contains("byteOffset=" + symbolOffset.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                    textSearch.StandardOutput.Contains("anchorSha256=" + symbolDigest, StringComparison.Ordinal),
                "Text search output omitted the source line, pinned byte offset or range digest.");

            var outsideScoped = await SearchJsonAsync("outsideFolderOnlyPhrase", nestedId).ConfigureAwait(false);
            Require(outsideScoped.GetProperty("total").GetInt32() == 0,
                "A nested folder filter widened search into a sibling folder.");
            var outsideWide = await SearchJsonAsync("outsideFolderOnlyPhrase").ConfigureAwait(false);
            Require(outsideWide.GetProperty("total").GetInt32() == 1 &&
                    outsideWide.GetProperty("hits")[0].GetProperty("path").GetString() == "outside-area/outside.txt",
                "An unfiltered owner workspace query did not find the outside-folder file.");

            var synonymMiss = await SearchJsonAsync("owner workspace lookup exact", selectedId).ConfigureAwait(false);
            Require(synonymMiss.GetProperty("total").GetInt32() == 0,
                "Workspace search expanded an exact literal query through semantic or synonym matching.");

            var invalidFolder = await RunOwnerCommandAsync(executable,
                "owner", "workspace-search", "--control-pipe", fixture.ControlPipe,
                "--query", "ReadyStateExact", "--folder-id", fileId).ConfigureAwait(false);
            Require(invalidFolder.ExitCode == 3 && invalidFolder.StandardError.Contains("workspace_filter_not_folder", StringComparison.Ordinal),
                "A file ID was silently accepted as a folder filter.");

            var originalBytes = await ReadWorkspaceFileAsync(executable, fixture, fileId, symbolRevision, "s13t4-search-source.md")
                .ConfigureAwait(false);
            Require(Encoding.UTF8.GetString(originalBytes.AsSpan(checked((int)symbolOffset), checked((int)symbolLength))) == "ReadyStateExact" &&
                    Convert.ToHexString(SHA256.HashData(originalBytes.AsSpan(checked((int)symbolOffset), checked((int)symbolLength))))
                        .Equals(symbolDigest, StringComparison.OrdinalIgnoreCase),
                "CLI readback did not independently verify the exact search hit bytes and digest.");
            Require(
                Encoding.UTF8.GetString(originalBytes.AsSpan(checked((int)headingOffset), checked((int)headingLength))) == "HeadingAnchorExact" &&
                Convert.ToHexString(SHA256.HashData(originalBytes.AsSpan(checked((int)headingOffset), checked((int)headingLength))))
                    .Equals(heading.GetProperty("anchorSha256").GetString(), StringComparison.OrdinalIgnoreCase),
                "A Markdown heading hit did not carry its exact source-byte range and digest.");
            Require(Convert.ToHexString(SHA256.HashData(originalBytes)).Equals(
                    symbol.GetProperty("fileSha256").GetString(), StringComparison.OrdinalIgnoreCase),
                "The search hit's whole-file digest did not match an exact CLI read of its pinned revision.");
            Console.WriteLine("PASS exact filename, path, Markdown heading, phrase and symbol search return independently verifiable revision-pinned locations");
            Console.WriteLine("PASS folder filters narrow owner search; invalid folder kinds and non-exact synonym queries fail honestly");

            var mismatchedAnchor = await RunOwnerCommandAsync(executable,
                "open", fileId, "--control-pipe", fixture.ControlPipe,
                "--revision", symbolRevision.ToString(CultureInfo.InvariantCulture),
                "--anchor-offset", symbolOffset.ToString(CultureInfo.InvariantCulture),
                "--anchor-length", symbolLength.ToString(CultureInfo.InvariantCulture),
                "--anchor-sha256", new string('0', 64), "--no-browser", "--lifetime-seconds", "30").ConfigureAwait(false);
            Require(mismatchedAnchor.ExitCode == 3 && mismatchedAnchor.StandardError.Contains("workspace_anchor_mismatch", StringComparison.Ordinal),
                "An open request with a mismatched anchor digest was not refused.");

            var missing = await RunOwnerCommandAsync(executable,
                "open", "missing-s13t4-file", "--control-pipe", fixture.ControlPipe,
                "--revision", symbolRevision.ToString(CultureInfo.InvariantCulture),
                "--anchor-offset", symbolOffset.ToString(CultureInfo.InvariantCulture),
                "--anchor-length", symbolLength.ToString(CultureInfo.InvariantCulture),
                "--anchor-sha256", symbolDigest, "--no-browser", "--lifetime-seconds", "30").ConfigureAwait(false);
            Require(missing.ExitCode == 3 && missing.StandardError.Contains("workspace_not_found", StringComparison.Ordinal),
                "Opening an anchor for a missing file did not fail with the live workspace refusal.");

            var opened = await S13T4StartAnchorOpenAsync(
                executable, fixture, fileId, symbolRevision, symbolOffset, symbolLength, symbolDigest).ConfigureAwait(false);
            var profile = Path.Combine(Path.GetTempPath(), "MetaBrain-S13T4-Chrome-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using var browser = await S13T1Browser.StartAsync(S13T1ChromePath(), profile, opened.Url).ConfigureAwait(false);
                var highlighted = await browser.WaitForAsync(
                    "document.getElementById('selected-anchor') ? document.getElementById('selected-anchor').textContent : ''",
                    value => value == "ReadyStateExact",
                    TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                var location = await browser.EvaluateStringAsync(
                    "document.querySelector('meta[name=\"mb-location\"]').content").ConfigureAwait(false);
                var locationParts = location.Split(',');
                Require(highlighted == "ReadyStateExact" && locationParts.Length == 3 &&
                        int.Parse(locationParts[1], CultureInfo.InvariantCulture) == symbol.GetProperty("textOffset").GetInt32() &&
                        int.Parse(locationParts[2], CultureInfo.InvariantCulture) == symbol.GetProperty("textLength").GetInt32(),
                    "Actual Chromium did not render and highlight the exact selected revision-pinned source hit.");

                Require(await browser.EvaluateStringAsync(
                            "window.location.search === '' && window.location.hash === '' ? 'none' : 'present'")
                            .ConfigureAwait(false) == "none",
                    "The browser URL carried a query or selected location instead of using the content-free shell metadata.");
                Require(await browser.EvaluateStringAsync("document.querySelector('input[type=search], select') === null ? 'none' : 'present'")
                            .ConfigureAwait(false) == "none",
                    "The document browser gained a search or folder-management control.");
                Console.WriteLine("PASS actual Chromium opened the CLI-selected exact revision and rendered the highlighted source range at its location");
            }
            finally
            {
                await S13T1StopOpenAsync(opened.Process).ConfigureAwait(false);
                S13T1TryDeleteDirectory(profile);
            }

            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, fileId, symbolRevision, "s13t4-after-browser.md")
                    .ConfigureAwait(false),
                originalBytes,
                "opening a search anchor did not rewrite original bytes");

            var revisedBytes = Encoding.UTF8.GetBytes("# New revision\r\nNo original selected symbol.\r\n");
            var revisedInput = await WriteWorkspaceInputAsync(fixture, "s13t4-revised.md", revisedBytes).ConfigureAwait(false);
            var revised = await RunOwnerCommandAsync(executable,
                "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                "--file-id", fileId, "--expected-content-revision", symbolRevision.ToString(CultureInfo.InvariantCulture),
                "--input-file", revisedInput).ConfigureAwait(false);
            Require(revised.ExitCode == 0 && revised.StandardOutput.Contains("contentRevision=2", StringComparison.Ordinal),
                "The synthetic anchor file could not be advanced for the stale-anchor negative.");

            var stale = await RunOwnerCommandAsync(executable,
                "open", fileId, "--control-pipe", fixture.ControlPipe,
                "--revision", symbolRevision.ToString(CultureInfo.InvariantCulture),
                "--anchor-offset", symbolOffset.ToString(CultureInfo.InvariantCulture),
                "--anchor-length", symbolLength.ToString(CultureInfo.InvariantCulture),
                "--anchor-sha256", symbolDigest, "--no-browser", "--lifetime-seconds", "30").ConfigureAwait(false);
            Require(stale.ExitCode == 3 && stale.StandardError.Contains("workspace_anchor_stale", StringComparison.Ordinal),
                "A stale revision-pinned anchor silently rebound or failed without its stale-anchor refusal.");

            Console.WriteLine("PASS missing, mismatched and stale anchors are refused without silently rebinding to newer content");

            var lockResult = await RunOwnerCommandAsync(
                executable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockResult.ExitCode == 0, "The synthetic vault did not lock after the workspace search checks.");
            var lockedSearch = await RunOwnerCommandAsync(
                executable, "owner", "workspace-search", "--control-pipe", fixture.ControlPipe,
                "--query", "ReadyStateExact", "--format", "json").ConfigureAwait(false);
            Require(lockedSearch.ExitCode != 0 &&
                    lockedSearch.StandardError.Contains("vault_locked", StringComparison.Ordinal) &&
                    string.IsNullOrEmpty(lockedSearch.StandardOutput),
                "Workspace search served private results while the vault was locked.");
            Console.WriteLine("PASS a locked vault refuses workspace search through the existing owner authority");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static async Task<(Process Process, string Url)> S13T4StartAnchorOpenAsync(
        string executable,
        SmokeFixture fixture,
        string fileId,
        long revision,
        long byteOffset,
        long byteLength,
        string anchorSha256)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("open");
        start.ArgumentList.Add(fileId);
        start.ArgumentList.Add("--control-pipe");
        start.ArgumentList.Add(fixture.ControlPipe);
        start.ArgumentList.Add("--revision");
        start.ArgumentList.Add(revision.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--anchor-offset");
        start.ArgumentList.Add(byteOffset.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--anchor-length");
        start.ArgumentList.Add(byteLength.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--anchor-sha256");
        start.ArgumentList.Add(anchorSha256);
        start.ArgumentList.Add("--no-browser");
        start.ArgumentList.Add("--lifetime-seconds");
        start.ArgumentList.Add("180");

        var process = new Process { StartInfo = start };
        Require(process.Start(), "The anchored document bridge process did not start.");
        process.StandardInput.Close();
        try
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (line is null)
            {
                var error = process.HasExited ? await process.StandardError.ReadToEndAsync().ConfigureAwait(false) : string.Empty;
                throw new InvalidOperationException("The anchored document bridge did not announce its session; stderr=" + error.Trim());
            }

            Require(line.StartsWith("OPEN document ", StringComparison.Ordinal),
                "The anchored document bridge did not announce an open session: " + line);
            var match = Regex.Match(line, @"url=(\S+)$");
            Require(match.Success, "The anchored document bridge announcement carried no URL: " + line);
            return (process, match.Groups[1].Value);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            process.Dispose();
            throw;
        }
    }
}
