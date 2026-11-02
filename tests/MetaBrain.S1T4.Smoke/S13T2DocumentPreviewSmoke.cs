using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string S13T2ScriptMarker = "synthetic-s13t2-script-marker-8f31";
    private const string S13T2MarkdownMarker = "synthetic-s13t2-markdown-marker-5c27";

    public static async Task RunS13T2DocumentPreviewAsync(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S13-T2 document-preview smoke is Windows-only.");
        }

        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        try
        {
            service = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            var passphrase = CreateSyntheticPassphrase();
            var provision = await RunOwnerCommandWithInputAsync(
                executable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The owner CLI did not provision the synthetic S13-T2 workspace (stderr=" + provision.StandardError.Trim() + ").");

            var mkdir = await RunOwnerCommandAsync(executable,
                "owner", "workspace-mkdir", "--control-pipe", fixture.ControlPipe,
                "--parent-id", "root", "--name", "s13-t2-preview",
                "--expected-tree-revision", "0").ConfigureAwait(false);
            Require(mkdir.ExitCode == 0 && mkdir.StandardOutput.Contains("MKDIR id=", StringComparison.Ordinal),
                "The owner CLI did not create the S13-T2 synthetic workspace folder.");
            var folderId = WorkspaceToken(mkdir.StandardOutput, "id");
            var treeRevision = WorkspaceNumber(mkdir.StandardOutput, "revision");

            using var externalProbe = new TcpListener(IPAddress.Loopback, 0);
            externalProbe.Start();
            var externalUrl = "http://127.0.0.1:" + ((IPEndPoint)externalProbe.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) + "/private-image";
            var markdownText =
                "# Safe preview\n\nA **bold** and *emphasized* " + S13T2MarkdownMarker + ".\n\n" +
                "![remote pixel](" + externalUrl + ")\n\n" +
                "[remote link](" + externalUrl + ")\n\n" +
                "<script>window.__mbS13T2 = '" + S13T2ScriptMarker + "';</script>\n\n" +
                "<img src=\"" + externalUrl + "\" onerror=\"window.__mbS13T2 = 'executed'\">\n\n" +
                "| key | value |\n| --- | --- |\n| row | `inert code` |\n";
            var markdownBytes = Encoding.UTF8.GetBytes(markdownText);

            var csvHeaders = Enumerable.Range(0, 66).Select(index => "column-" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
            var firstCsvRow = Enumerable.Range(0, csvHeaders.Length)
                .Select(index => "value-" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
            firstCsvRow[0] = "alpha";
            firstCsvRow[1] = "value, with comma";
            firstCsvRow[2] = "line one\r\nline two";
            firstCsvRow[64] = "last-page-cell-64";
            firstCsvRow[65] = "last-page-cell-65";
            var secondCsvRow = Enumerable.Range(0, csvHeaders.Length)
                .Select(index => "second-" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
            var tailCsvRows = Enumerable.Range(2, 100)
                .Select(rowIndex => Enumerable.Range(0, csvHeaders.Length)
                    .Select(column => "tail-" + rowIndex.ToString(CultureInfo.InvariantCulture) + "-" +
                        column.ToString(CultureInfo.InvariantCulture)).ToArray());
            var csvDataRows = new[] { firstCsvRow, secondCsvRow }.Concat(tailCsvRows).ToArray();

            static string CsvField(string value) =>
                value.IndexOfAny([',', '"', '\r', '\n']) < 0
                    ? value
                    : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

            var csvText = string.Join(',', csvHeaders.Select(CsvField)) + "\r\n" +
                string.Join("\r\n", csvDataRows.Select(row => string.Join(',', row.Select(CsvField)))) + "\r\n";
            var csvBytes = Encoding.UTF8.GetBytes(csvText);

            const string largePrefix = "synthetic-large-start\n";
            var largeText = largePrefix + new string('L', (1024 * 1024) - largePrefix.Length);
            var largeBytes = Encoding.UTF8.GetBytes(largeText);
            Require(largeBytes.Length == 1024 * 1024, "The synthetic paging fixture did not reach the existing one-megabyte envelope.");

            var jsonlText = string.Join('\n', Enumerable.Range(0, 30000)
                .Select(index => "{\"index\":" + index.ToString(CultureInfo.InvariantCulture) + "}")) + "\n";
            var jsonlBytes = Encoding.UTF8.GetBytes(jsonlText);
            var binaryBytes = new byte[] { 0x00, 0xFF, 0xFE, 0x80, 0x81, 0xC0, 0xAF };

            async Task<string> ImportAsync(string name, byte[] bytes)
            {
                var inputPath = await WriteWorkspaceInputAsync(fixture, "s13t2-" + name, bytes).ConfigureAwait(false);
                var imported = await ImportWorkspaceFileAsync(
                    executable, fixture, folderId, name, inputPath, treeRevision).ConfigureAwait(false);
                treeRevision = imported.TreeRevision;
                return imported.FileId;
            }

            var markdownId = await ImportAsync("safe.md", markdownBytes).ConfigureAwait(false);
            var csvId = await ImportAsync("table.csv", csvBytes).ConfigureAwait(false);
            var largeId = await ImportAsync("large.txt", largeBytes).ConfigureAwait(false);
            var largeReadback = await ReadWorkspaceFileAsync(
                executable, fixture, largeId, null, "s13t2-large-preflight.txt").ConfigureAwait(false);
            RequireBytes(largeReadback, largeBytes, "one-megabyte workspace preflight read");
            Console.WriteLine("PASS exact-cap one-megabyte workspace content reads back byte-for-byte before preview");
            var jsonlId = await ImportAsync("events.jsonl", jsonlBytes).ConfigureAwait(false);
            var binaryId = await ImportAsync("opaque.bin", binaryBytes).ConfigureAwait(false);
            Console.WriteLine("PASS S13-T2 synthetic workspace contains malicious Markdown, wide CSV, bounded text/JSONL and opaque binary");

            async Task RunBrowserSessionAsync(
                string fileId,
                Func<S13T1Browser, string, string, Task> exercise)
            {
                var opened = await S13T1StartOpenAsync(executable, fixture, fileId, lifetimeSeconds: 240).ConfigureAwait(false);
                var profile = Path.Combine(Path.GetTempPath(), "MetaBrain-S13T2-Chrome-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await using var browser = await S13T1Browser.StartAsync(S13T1ChromePath(), profile, opened.Url)
                        .ConfigureAwait(false);
                    await exercise(browser, opened.Url, opened.Token).ConfigureAwait(false);
                }
                finally
                {
                    await S13T1StopOpenAsync(opened.Process).ConfigureAwait(false);
                    S13T1TryDeleteDirectory(profile);
                }
            }

            await RunBrowserSessionAsync(markdownId, async (browser, url, token) =>
            {
                var shell = await S13T1RequestAsync(url).ConfigureAwait(false);
                Require(shell.Status == 200 &&
                        shell.CacheControl is not null && shell.CacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase) &&
                        shell.ContentSecurityPolicy is not null &&
                        shell.ContentSecurityPolicy.Contains("default-src 'none'", StringComparison.Ordinal) &&
                        shell.ContentSecurityPolicy.Contains("img-src 'none'", StringComparison.Ordinal) &&
                        shell.ContentSecurityPolicy.Contains("frame-ancestors 'none'", StringComparison.Ordinal),
                    "The content-free document shell did not retain the no-store and restrictive CSP contract.");
                Require(!Encoding.UTF8.GetString(shell.Body).Contains(S13T2MarkdownMarker, StringComparison.Ordinal) &&
                        !url.Contains(token, StringComparison.Ordinal) && !url.Contains(markdownId, StringComparison.Ordinal),
                    "Markdown content, the session capability or the file id appeared in the shell URL or response.");

                var pageBody = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    offset = 0,
                    limit = 16 * 1024,
                    mode = "preview",
                    columnOffset = 0,
                });
                var missing = await S13T1RequestAsync(url + "api/document/page",
                    origin: url.TrimEnd('/'), body: pageBody, contentType: "application/json").ConfigureAwait(false);
                var wrong = await S13T2PageRequestAsync(url, "wrong-session-capability", 0, 0, "preview").ConfigureAwait(false);
                var foreignOrigin = await S13T2PageRequestAsync(
                    url, token, 0, 0, "preview", origin: "http://evil.example").ConfigureAwait(false);
                var crossSite = await S13T2PageRequestAsync(
                    url, token, 0, 0, "preview", site: "cross-site").ConfigureAwait(false);
                var foreignHost = await S13T2PageRequestAsync(
                    url, token, 0, 0, "preview", host: "evil.example").ConfigureAwait(false);
                var query = await S13T1RequestAsync(url + "api/document/page?file=other", token,
                    origin: url.TrimEnd('/'), body: pageBody, contentType: "application/json").ConfigureAwait(false);
                var wrongBindingBody = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    offset = 0,
                    limit = 16 * 1024,
                    mode = "preview",
                    columnOffset = 0,
                    fileId = "another-document",
                });
                var wrongBinding = await S13T2PageRequestAsync(
                    url, token, 0, 0, "preview", bodyOverride: wrongBindingBody).ConfigureAwait(false);
                var wrongRevisionBody = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    offset = 0,
                    limit = 16 * 1024,
                    mode = "preview",
                    columnOffset = 0,
                    revision = 999,
                });
                var wrongRevision = await S13T2PageRequestAsync(
                    url, token, 0, 0, "preview", bodyOverride: wrongRevisionBody).ConfigureAwait(false);
                Require(missing.Status == 401 && wrong.Status == 403 &&
                        foreignOrigin.Status == 403 && crossSite.Status == 403 && foreignHost.Status == 403 &&
                        query.Status == 400 && wrongBinding.Status == 400 && wrongRevision.Status == 400,
                    "The document page route did not refuse missing/wrong capability, foreign origin/site/host, query, or document/revision overrides.");

                var page = await S13T2PageRequestAsync(url, token, 0, 0, "preview").ConfigureAwait(false);
                Require(page.Status == 200 &&
                        page.CacheControl is not null && page.CacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase) &&
                        page.JsonBody.GetProperty("format").GetString() == "markdown" &&
                        page.JsonBody.GetProperty("text").GetString()!.Contains(S13T2MarkdownMarker, StringComparison.Ordinal),
                    "The authenticated page response was not the exact bound Markdown revision.");
                Console.WriteLine("PASS S13-T2 authenticated page binds one revision and refuses cross-origin/out-of-session request variants");

                var status = await browser.WaitForAsync("document.getElementById('status').textContent",
                    value => value.Contains("Preview of the bound revision", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                var rendered = await browser.EvaluateAsync("({" +
                    "heading: document.querySelector('#preview-view h1')?.textContent || ''," +
                    "strong: document.querySelector('#preview-view strong')?.textContent || ''," +
                    "tableHeaders: document.querySelectorAll('#preview-view thead th').length," +
                    "imageLabel: document.getElementById('preview-view').textContent.includes('Image: remote pixel')," +
                    "scriptValue: String(window.__mbS13T2)," +
                    "activeElements: document.querySelectorAll('img,iframe,object,embed,form,video,audio').length," +
                    "links: document.querySelectorAll('a[href]').length," +
                    "injectedScripts: document.querySelectorAll('script').length - 1" +
                    "})").ConfigureAwait(false);
                Require(status.Contains("Preview of the bound revision", StringComparison.Ordinal) &&
                        rendered.GetProperty("heading").GetString() == "Safe preview" &&
                        rendered.GetProperty("strong").GetString() == "bold" &&
                        rendered.GetProperty("tableHeaders").GetInt32() == 2 &&
                        rendered.GetProperty("imageLabel").GetBoolean() &&
                        rendered.GetProperty("scriptValue").GetString() == "undefined" &&
                        rendered.GetProperty("activeElements").GetInt32() == 0 &&
                        rendered.GetProperty("links").GetInt32() == 0 &&
                        rendered.GetProperty("injectedScripts").GetInt32() == 0,
                    "The real browser did not format Markdown inertly or it created active/remote content.");

                var frameBlocked = await browser.EvaluateStringAsync(
                    "(async()=>{const frame=document.createElement('iframe');" +
                    "const loaded=new Promise(resolve=>frame.addEventListener('load',resolve,{once:true}));" +
                    "frame.src=location.origin+'/';document.body.appendChild(frame);" +
                    "await Promise.race([loaded,new Promise(resolve=>setTimeout(resolve,2000))]);" +
                    "return String(!frame.contentDocument || frame.contentDocument.title !== 'Meta Brain document');})()")
                    .ConfigureAwait(false);
                Require(frameBlocked == "true", "The bridge shell was allowed to render inside a browser frame.");
                var browserStorage = await browser.EvaluateStringAsync(
                    "(async()=>String(navigator.serviceWorker.controller === null && " +
                    "(await navigator.serviceWorker.getRegistrations()).length === 0 && " +
                    "localStorage.length === 0 && sessionStorage.length === 0 && document.cookie.length === 0 && " +
                    "(await caches.keys()).length === 0 && (await indexedDB.databases()).length === 0))()")
                    .ConfigureAwait(false);
                Require(browserStorage == "true",
                    "The preview registered a service worker or wrote browser storage, cache, cookie or IndexedDB data.");
                await Task.Delay(300).ConfigureAwait(false);
                Require(!externalProbe.Pending(), "Markdown preview attempted a remote image/resource request.");

                await browser.EvaluateAsync("document.getElementById('source').click()").ConfigureAwait(false);
                var raw = await browser.WaitForAsync(
                    "document.getElementById('preview-view').textContent",
                    value => value.Contains("![remote pixel]", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Require(raw.Contains("<script>window.__mbS13T2", StringComparison.Ordinal),
                    "The source toggle did not show the original inert Markdown source.");
                var persisted = await ReadWorkspaceFileAsync(executable, fixture, markdownId, null, "s13t2-markdown-readback.md")
                    .ConfigureAwait(false);
                RequireBytes(persisted, markdownBytes, "Markdown preview/source toggle preservation");
                Console.WriteLine("PASS actual Chromium Markdown formatting, malicious HTML/image/link/frame negatives, no egress, and unchanged source bytes");
            }).ConfigureAwait(false);

            await RunBrowserSessionAsync(csvId, async (browser, url, token) =>
            {
                var firstPage = await S13T2PageRequestAsync(url, token, 0, 0, "preview").ConfigureAwait(false);
                var first = firstPage.JsonBody;
                Require(firstPage.Status == 200 &&
                        first.GetProperty("csvColumnCount").GetInt32() == 66 &&
                        first.GetProperty("csvHeader").GetArrayLength() == 64 &&
                        first.GetProperty("csvRows").GetArrayLength() == 100 &&
                        first.GetProperty("nextOffset").GetInt32() == 100 &&
                        first.GetProperty("nextColumnOffset").GetInt32() == 64 &&
                        first.GetProperty("csvRows")[0][1].GetString() == "value, with comma" &&
                        first.GetProperty("csvRows")[0][2].GetString() == "line one\r\nline two",
                    "The CSV first page lost its 100-row/64-column bounds, quoted cells, multiline data, header context or cursor.");
                var lastColumns = await S13T2PageRequestAsync(url, token, 0, 64, "preview").ConfigureAwait(false);
                Require(lastColumns.Status == 200 &&
                        lastColumns.JsonBody.GetProperty("csvHeader").GetArrayLength() == 2 &&
                        lastColumns.JsonBody.GetProperty("csvHeader")[0].GetString() == "column-64" &&
                        lastColumns.JsonBody.GetProperty("csvRows")[0][0].GetString() == "last-page-cell-64",
                    "The CSV column page did not preserve the remaining columns and row context.");
                var lastRows = await S13T2PageRequestAsync(url, token, 100, 64, "preview").ConfigureAwait(false);
                Require(lastRows.Status == 200 &&
                        lastRows.JsonBody.GetProperty("csvRows").GetArrayLength() == 2 &&
                        lastRows.JsonBody.GetProperty("csvRows")[0][0].GetString() == "tail-100-64" &&
                        lastRows.JsonBody.GetProperty("nextOffset").ValueKind == JsonValueKind.Null,
                    "The CSV continuation did not bound rows while retaining the selected column page.");

                await browser.WaitForAsync("document.querySelectorAll('#preview-view thead th').length",
                    value => value == "64", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                var firstHeader = await browser.EvaluateStringAsync(
                    "document.querySelector('#preview-view thead th').textContent").ConfigureAwait(false);
                Require(firstHeader == "column-0", "The actual browser did not render the CSV header as a table header.");
                await browser.EvaluateAsync("document.getElementById('next-column').click()").ConfigureAwait(false);
                var lastHeader = await browser.WaitForAsync(
                    "document.querySelector('#preview-view thead th').textContent",
                    value => value == "column-64", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Require(lastHeader == "column-64", "The browser CSV column pager did not show the remaining table columns.");
                await browser.EvaluateAsync("document.getElementById('next').click()").ConfigureAwait(false);
                var lastRowCell = await browser.WaitForAsync(
                    "document.querySelector('#preview-view tbody tr td')?.textContent || ''",
                    value => value == "tail-100-64", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Require(lastRowCell == "tail-100-64",
                    "The actual browser CSV row pager did not preserve the selected columns on the continuation.");
                await browser.EvaluateAsync("document.getElementById('source').click()").ConfigureAwait(false);
                var raw = await browser.WaitForAsync(
                    "document.querySelector('#preview-view pre')?.textContent || ''",
                    value => value.StartsWith("column-0,column-1", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Require(raw.StartsWith("column-0,column-1", StringComparison.Ordinal),
                    "The CSV source toggle did not remain available after paging the table columns.");
                var persisted = await ReadWorkspaceFileAsync(executable, fixture, csvId, null, "s13t2-csv-readback.csv")
                    .ConfigureAwait(false);
                RequireBytes(persisted, csvBytes, "CSV table/source toggle preservation");
                Console.WriteLine("PASS actual Chromium CSV header/column table and bounded 100-row/64-column pages preserve quoted source bytes");
            }).ConfigureAwait(false);

            await RunBrowserSessionAsync(jsonlId, async (browser, url, token) =>
            {
                var page = await S13T2PageRequestAsync(url, token, 0, 0, "preview").ConfigureAwait(false);
                var data = page.JsonBody;
                Require(page.Status == 200 && data.GetProperty("format").GetString() == "jsonl" &&
                        data.GetProperty("text").GetString()!.Length <= 16 * 1024 &&
                        data.GetProperty("nextOffset").ValueKind == JsonValueKind.Number,
                    "The JSONL preview did not return one bounded text page with a continuation cursor.");
                await browser.WaitForAsync("document.getElementById('preview-view').textContent.length",
                    value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length) &&
                             length is > 0 and <= 16 * 1024,
                    TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                Console.WriteLine("PASS actual browser JSONL preview is bounded to one text page");
            }).ConfigureAwait(false);

            await RunBrowserSessionAsync(binaryId, async (browser, url, token) =>
            {
                var page = await S13T2PageRequestAsync(url, token, 0, 0, "preview").ConfigureAwait(false);
                Require(page.Status == 200 &&
                        page.JsonBody.GetProperty("state").GetString() == "binary" &&
                        page.JsonBody.GetProperty("text").ValueKind == JsonValueKind.Null &&
                        !page.JsonBody.GetProperty("editable").GetBoolean(),
                    "Opaque binary was not reported as a truthful read-only no-text state.");
                var status = await browser.WaitForAsync("document.getElementById('status').textContent",
                    value => value.Contains("not text", StringComparison.Ordinal), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                Require(status.Contains("Read-only", StringComparison.Ordinal) &&
                        status.Contains("not text", StringComparison.Ordinal),
                    "The browser did not show the opaque binary as a truthful read-only no-text state.");
                Console.WriteLine("PASS unsupported binary API state remains binary and the browser shows read-only/no text");
            }).ConfigureAwait(false);

            await RunBrowserSessionAsync(largeId, async (browser, url, token) =>
            {
                var first = await S13T2PageRequestAsync(url, token, 0, 0, "preview").ConfigureAwait(false);
                var firstData = first.JsonBody;
                Require(first.Status == 200 &&
                        firstData.GetProperty("text").GetString()!.Length <= 16 * 1024 &&
                        firstData.GetProperty("nextOffset").GetInt32() > 0,
                    "The one-megabyte text file was not served as a bounded first page.");
                var nextOffset = firstData.GetProperty("nextOffset").GetInt32();
                var second = await S13T2PageRequestAsync(url, token, nextOffset, 0, "preview").ConfigureAwait(false);
                Require(second.Status == 200 &&
                        second.JsonBody.GetProperty("text").GetString()!.Length <= 16 * 1024 &&
                        second.JsonBody.GetProperty("text").GetString()!.All(character => character == 'L'),
                    "The large text continuation was not a bounded page of the same bound revision.");
                await browser.WaitForAsync("document.getElementById('preview-view').textContent.length",
                    value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length) &&
                             length is > 0 and <= 16 * 1024,
                    TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                await browser.EvaluateAsync(
                    "(() => { document.getElementById('next').click(); document.getElementById('cancel').click(); return true; })()")
                    .ConfigureAwait(false);
                var cancelled = await browser.WaitForAsync("document.getElementById('status').textContent",
                    value => value.Contains("request cancelled", StringComparison.Ordinal), TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                Require(cancelled.Contains("cancelled", StringComparison.Ordinal),
                    "Cancelling a page request did not stop the browser-side preview operation.");
                var persisted = await ReadWorkspaceFileAsync(executable, fixture, largeId, null, "s13t2-large-readback.txt")
                    .ConfigureAwait(false);
                RequireBytes(persisted, largeBytes, "large text paging/cancel preservation");

                var lockResult = await RunOwnerCommandAsync(
                    executable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
                Require(lockResult.ExitCode == 0, "The owner did not lock the synthetic vault for the S13-T2 negative.");
                var lockedRead = await S13T2PageRequestAsync(url, token, 0, 0, "preview").ConfigureAwait(false);
                Require(lockedRead.Status == 423 && lockedRead.JsonBody.GetProperty("error").GetString() == "vault_locked",
                    "A locked vault still served a paged document response.");
                await browser.EvaluateAsync("location.reload()").ConfigureAwait(false);
                var lockedStatus = await browser.WaitForAsync("document.getElementById('status').textContent",
                    value => value.Contains("vault is locked", StringComparison.Ordinal), TimeSpan.FromSeconds(20))
                    .ConfigureAwait(false);
                Require(lockedStatus.Contains("vault is locked", StringComparison.Ordinal),
                    "The actual browser did not fail closed after the owner locked the vault.");
                Console.WriteLine("PASS one-megabyte paging/cancel is per bound file, preserves bytes, and locked read refuses with 423");
            }).ConfigureAwait(false);

            Require(!externalProbe.Pending(), "An external Markdown resource request remained queued after preview sessions.");
            Console.WriteLine("OBS Chromium profile directories were temporary and removed after the smoke; no absolute erasure claim is made for browser-managed memory, DevTools/extensions or OS swap.");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static async Task<S13T1Response> S13T2PageRequestAsync(
        string url,
        string token,
        int offset,
        int columnOffset,
        string mode,
        string? origin = null,
        string? site = null,
        string? host = null,
        byte[]? bodyOverride = null)
    {
        var body = bodyOverride ?? JsonSerializer.SerializeToUtf8Bytes(new
        {
            offset,
            limit = 16 * 1024,
            mode,
            columnOffset,
        });
        return await S13T1RequestAsync(url + "api/document/page", token,
            origin: origin ?? url.TrimEnd('/'), site: site, host: host,
            body: body, contentType: "application/json").ConfigureAwait(false);
    }
}
