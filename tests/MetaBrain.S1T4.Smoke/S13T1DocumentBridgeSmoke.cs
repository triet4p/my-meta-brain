using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S13-T1 runtime proof for the foreground document surface: the actual owner
/// CLI and shared service serve exactly one workspace file at one content
/// revision to a real browser, refuse a missing, wrong, cross-origin,
/// cross-site or replayed request, keep the original bytes on a no-op save,
/// preserve CRLF/BOM on a real save, refuse a stale-revision conflict without
/// overwriting, stop serving after a lock, and die with the explicit session
/// lifetime. The browser is the installed Chromium engine driven over CDP; no
/// mockup, test double or in-process substitute stands in for it.
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S13T1MarkdownMarker = "synthetic-document-bridge-7c41a9";
    private const string S13T1ExternalMarker = "synthetic-external-writer-2f88";

    private static readonly HttpClient S13T1Http = new(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    public static async Task RunS13T1DocumentBridgeAsync(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S13-T1 document-bridge smoke is Windows-only.");
        }

        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        Process? openProcess = null;
        S13T1Browser? browser = null;
        var chromeProfile = Path.Combine(Path.GetTempPath(), "MetaBrain-S13T1-Chrome-" + Guid.NewGuid().ToString("N"));
        try
        {
            service = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            var passphrase = CreateSyntheticPassphrase();
            var provision = await RunOwnerCommandWithInputAsync(
                executable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The owner CLI did not provision the S13-T1 vault (stderr=" + provision.StandardError.Trim() + ").");

            var mkdir = await RunOwnerCommandAsync(executable,
                "owner", "workspace-mkdir", "--control-pipe", fixture.ControlPipe,
                "--parent-id", "root", "--name", "s13-t1-documents",
                "--expected-tree-revision", "0").ConfigureAwait(false);
            Require(mkdir.ExitCode == 0 && mkdir.StandardOutput.Contains("MKDIR id=", StringComparison.Ordinal),
                "The owner CLI did not create the S13-T1 document folder (stderr=" + mkdir.StandardError.Trim() + ").");
            var folderId = WorkspaceToken(mkdir.StandardOutput, "id");
            var treeRevision = WorkspaceNumber(mkdir.StandardOutput, "revision");

            var markdown = Encoding.UTF8.GetBytes(
                "# Synthetic document\r\n\r\nmarker " + S13T1MarkdownMarker + "\r\n- item\r\n");
            var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("bom line\r\nsecond\r\n")).ToArray();
            var mixed = Encoding.UTF8.GetBytes("mixed-a\r\nmixed-b\nmixed-c");
            var bareMixed = Encoding.UTF8.GetBytes("bare-a\nbare-b\rbare-c");
            var carriageReturn = Encoding.UTF8.GetBytes("cr-a\rcr-b\r");
            var utf16 = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("utf16 marker\r\n")).ToArray();
            var binary = new byte[] { 0x00, 0xFF, 0xFE, 0x80, 0x81, 0xC0, 0xAF };
            var csv = Encoding.UTF8.GetBytes("id,label\n1,alpha\n2,beta\n");
            var jsonl = Encoding.UTF8.GetBytes("{\"index\":1}\n{\"index\":2}\n");

            var markdownPath = await WriteWorkspaceInputAsync(fixture, "s13t1-note.md", markdown).ConfigureAwait(false);
            var bomPath = await WriteWorkspaceInputAsync(fixture, "s13t1-bom.md", bom).ConfigureAwait(false);
            var mixedPath = await WriteWorkspaceInputAsync(fixture, "s13t1-mixed.txt", mixed).ConfigureAwait(false);
            var bareMixedPath = await WriteWorkspaceInputAsync(fixture, "s13t1-bare-mixed.txt", bareMixed).ConfigureAwait(false);
            var carriageReturnPath = await WriteWorkspaceInputAsync(fixture, "s13t1-cr.txt", carriageReturn).ConfigureAwait(false);
            var utf16Path = await WriteWorkspaceInputAsync(fixture, "s13t1-utf16.txt", utf16).ConfigureAwait(false);
            var binaryPath = await WriteWorkspaceInputAsync(fixture, "s13t1-blob.bin", binary).ConfigureAwait(false);
            var csvPath = await WriteWorkspaceInputAsync(fixture, "s13t1-table.csv", csv).ConfigureAwait(false);
            var jsonlPath = await WriteWorkspaceInputAsync(fixture, "s13t1-events.jsonl", jsonl).ConfigureAwait(false);

            async Task<string> ImportAsync(string name, string path)
            {
                var imported = await ImportWorkspaceFileAsync(
                    executable, fixture, folderId, name, path, treeRevision).ConfigureAwait(false);
                treeRevision = imported.TreeRevision;
                return imported.FileId;
            }

            var markdownId = await ImportAsync("note.md", markdownPath).ConfigureAwait(false);
            var bomId = await ImportAsync("bom.md", bomPath).ConfigureAwait(false);
            var mixedId = await ImportAsync("mixed.txt", mixedPath).ConfigureAwait(false);
            var bareMixedId = await ImportAsync("bare-mixed.txt", bareMixedPath).ConfigureAwait(false);
            var carriageReturnId = await ImportAsync("cr.txt", carriageReturnPath).ConfigureAwait(false);
            var utf16Id = await ImportAsync("utf16.txt", utf16Path).ConfigureAwait(false);
            var binaryId = await ImportAsync("blob.bin", binaryPath).ConfigureAwait(false);
            var csvId = await ImportAsync("table.csv", csvPath).ConfigureAwait(false);
            var jsonlId = await ImportAsync("events.jsonl", jsonlPath).ConfigureAwait(false);
            Console.WriteLine("PASS S13-T1 synthetic workspace imports cover markdown/BOM/mixed/UTF-16/binary/CSV/JSONL");

            var opened = await S13T1StartOpenAsync(executable, fixture, markdownId, lifetimeSeconds: 180).ConfigureAwait(false);
            openProcess = opened.Process;
            var url = opened.Url;
            var token = opened.Token;
            Require(!url.Contains("token", StringComparison.OrdinalIgnoreCase) && url.EndsWith('/'),
                "The opened URL carried a capability or did not end at the bridge root: " + url);

            var shell = await S13T1RequestAsync(url).ConfigureAwait(false);
            Require(shell.Status == 200 && shell.ContentSecurityPolicy is not null &&
                    shell.ContentSecurityPolicy.Contains("default-src 'none'", StringComparison.Ordinal) &&
                    shell.CacheControl is not null && shell.CacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase),
                "The document shell did not carry the settled no-store/CSP policy.");
            Require(!Encoding.UTF8.GetString(shell.Body).Contains(S13T1MarkdownMarker, StringComparison.Ordinal),
                "The shell response contained document text before an authenticated session read.");
            Console.WriteLine("PASS shell is content-free, no-store and CSP-locked; URL carries no capability");

            var missing = await S13T1RequestAsync(url + "api/document").ConfigureAwait(false);
            var wrong = await S13T1RequestAsync(url + "api/document", "AAAA").ConfigureAwait(false);
            var evilOrigin = await S13T1RequestAsync(url + "api/document", token, origin: "http://evil.example").ConfigureAwait(false);
            var crossSite = await S13T1RequestAsync(url + "api/document", token, site: "cross-site").ConfigureAwait(false);
            var evilHost = await S13T1RequestAsync(url + "api/document", token, host: "evil.example").ConfigureAwait(false);
            var query = await S13T1RequestAsync(url + "api/document?file=<other>", token).ConfigureAwait(false);
            Require(missing.Status == 401 && wrong.Status == 403, "Missing/wrong session capabilities were not refused (missing=" +
                missing.Status.ToString(CultureInfo.InvariantCulture) + ", wrong=" + wrong.Status.ToString(CultureInfo.InvariantCulture) + ").");
            Require(evilOrigin.Status == 403 && crossSite.Status == 403 && evilHost.Status == 403,
                "A foreign Origin, cross-site metadata or foreign Host was not refused (origin=" +
                evilOrigin.Status.ToString(CultureInfo.InvariantCulture) + ", site=" + crossSite.Status.ToString(CultureInfo.InvariantCulture) +
                ", host=" + evilHost.Status.ToString(CultureInfo.InvariantCulture) + ").");
            Require(query.Status == 400, "A query string bypassed the bound-document route.");
            Console.WriteLine("PASS missing/wrong token, foreign Origin, cross-site metadata, foreign Host and query route are refused");

            var document = await S13T1ReadDocumentAsync(url, token).ConfigureAwait(false);
            Require(string.Equals(document.GetProperty("name").GetString(), "note.md", StringComparison.Ordinal) &&
                    string.Equals(document.GetProperty("format").GetString(), "markdown", StringComparison.Ordinal) &&
                    string.Equals(document.GetProperty("encoding").GetString(), "utf-8", StringComparison.Ordinal) &&
                    string.Equals(document.GetProperty("newlines").GetString(), "crlf", StringComparison.Ordinal) &&
                    document.GetProperty("editable").GetBoolean() &&
                    string.Equals(document.GetProperty("text").GetString(), Encoding.UTF8.GetString(markdown), StringComparison.Ordinal),
                "The bound document read did not report the exact markdown revision.");
            var boundRevision = document.GetProperty("contentRevision").GetInt64();

            var unchanged = await S13T1SaveAsync(url, token, boundRevision, Encoding.UTF8.GetString(markdown)).ConfigureAwait(false);
            Require(unchanged.Status == 200 && unchanged.JsonBody.GetProperty("status").GetString() == "unchanged" &&
                    unchanged.JsonBody.GetProperty("contentRevision").GetInt64() == boundRevision,
                "A no-op save did not report the unchanged bound revision.");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, boundRevision, "s13t1-unchanged.md").ConfigureAwait(false),
                markdown, "no-op save bytes");
            Console.WriteLine("PASS a no-op save keeps the bound revision and exact original bytes");

            var replay = await S13T1SaveAsync(url, token, boundRevision,
                Encoding.UTF8.GetString(markdown).Replace("\r\n", "\n", StringComparison.Ordinal) + "replayed\r\n").ConfigureAwait(false);
            Require(replay.Status == 200, "A first explicit save was refused.");
            var savedRevision = replay.JsonBody.GetProperty("contentRevision").GetInt64();
            var replayAgain = await S13T1SaveAsync(url, token, boundRevision,
                Encoding.UTF8.GetString(markdown).Replace("\r\n", "\n", StringComparison.Ordinal) + "replayed\r\n").ConfigureAwait(false);
            Require(replayAgain.Status == 409 && replayAgain.JsonBody.GetProperty("error").GetString() == "workspace_conflict",
                "Replaying a consumed save did not conflict.");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, savedRevision, "s13t1-saved.md").ConfigureAwait(false),
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(markdown) + "replayed\r\n"), "saved revision bytes");
            Console.WriteLine("PASS explicit save advances one revision and a replay conflicts without a second write");

            // The browser is the installed Chromium engine. It reads through the
            // same route and performs preview, edit and save through the DOM.
            browser = await S13T1Browser.StartAsync(S13T1ChromePath(), chromeProfile, url)
                .ConfigureAwait(false);
            var status = await browser.WaitForAsync("document.getElementById('status').textContent",
                value => value.Contains("Preview of the bound revision", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Require(status.Contains("Preview of the bound revision", StringComparison.Ordinal),
                "The browser did not reach the bound-revision preview: " + status);
            var name = await browser.EvaluateStringAsync("document.getElementById('name').textContent").ConfigureAwait(false);
            var preview = await browser.EvaluateStringAsync("document.getElementById('preview-view').textContent").ConfigureAwait(false);
            Require(string.Equals(name, "note.md", StringComparison.Ordinal) &&
                    preview.Contains(S13T1MarkdownMarker, StringComparison.Ordinal),
                "The browser preview did not render the bound document text.");
            await browser.EvaluateAsync("document.getElementById('edit').click()").ConfigureAwait(false);
            await browser.WaitForAsync(
                "document.getElementById('editor').hidden ? 'hidden' : 'visible'",
                value => value == "visible", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            await browser.EvaluateAsync(
                "(() => { const editor = document.getElementById('editor'); editor.value = editor.value + 'browser-edit\\n'; return editor.value.length; })()")
                .ConfigureAwait(false);
            await browser.EvaluateAsync("document.getElementById('save').click()").ConfigureAwait(false);
            var savedStatus = await browser.WaitForAsync("document.getElementById('status').textContent",
                value => value.StartsWith("Saved revision ", StringComparison.Ordinal), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            var browserRevision = long.Parse(
                Regex.Match(savedStatus, @"revision (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
            Require(browserRevision == savedRevision + 1,
                "The browser save did not advance exactly one content revision (reported " + savedStatus + ").");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, browserRevision, "s13t1-browser.md").ConfigureAwait(false),
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(markdown) + "replayed\r\nbrowser-edit\r\n"), "browser-edited bytes");
            Console.WriteLine("PASS an actual browser rendered, edited and saved one explicit bound revision");

            // A second writer advances the document outside the session. The
            // browser's next save must conflict and keep its in-page draft.
            var externalPath = await WriteWorkspaceInputAsync(fixture, "s13t1-external.md",
                Encoding.UTF8.GetBytes("# external writer\r\nmarker " + S13T1ExternalMarker + "\r\n")).ConfigureAwait(false);
            var external = await RunOwnerCommandAsync(executable,
                "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                "--file-id", markdownId, "--expected-content-revision", browserRevision.ToString(CultureInfo.InvariantCulture),
                "--input-file", externalPath).ConfigureAwait(false);
            Require(external.ExitCode == 0 && external.StandardOutput.Contains("REVISE id=", StringComparison.Ordinal),
                "The external writer did not advance the document (stderr=" + external.StandardError.Trim() + ").");
            await browser.EvaluateAsync(
                "(() => { const editor = document.getElementById('editor'); editor.value = editor.value + 'conflict-draft'; return editor.value.length; })()")
                .ConfigureAwait(false);
            await browser.EvaluateAsync("document.getElementById('save').click()").ConfigureAwait(false);
            var conflictStatus = await browser.WaitForAsync("document.getElementById('status').textContent",
                value => value.Contains("document changed", StringComparison.Ordinal), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            var draftKept = (await browser.EvaluateStringAsync("document.getElementById('editor').value").ConfigureAwait(false))
                .Contains("conflict-draft", StringComparison.Ordinal);
            Require(conflictStatus.Contains("document changed", StringComparison.Ordinal) && draftKept,
                "The browser conflict was not explained with the draft kept in the page.");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, null, "s13t1-after-conflict.md").ConfigureAwait(false),
                Encoding.UTF8.GetBytes("# external writer\r\nmarker " + S13T1ExternalMarker + "\r\n"), "conflict preserved bytes");
            Console.WriteLine("PASS a concurrent external revision conflicts in the browser and is not overwritten");

            // Lock ends the foreground session, so a request racing the
            // monitor is either refused while the service is locked or loses
            // its listener when the bridge revokes the session.
            var lockedRead = await S13T1RequestAsync(url + "api/document", token).ConfigureAwait(false);
            Require(lockedRead.Status == 200, "The pre-lock document read did not succeed.");
            var lockResult = await RunOwnerCommandAsync(executable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockResult.ExitCode == 0, "The owner vault did not lock (stderr=" + lockResult.StandardError.Trim() + ").");
            S13T1Response? lockedGet = null;
            S13T1Response? lockedSave = null;
            try
            {
                lockedGet = await S13T1RequestAsync(url + "api/document", token).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
            }

            try
            {
                lockedSave = await S13T1SaveAsync(url, token, browserRevision, "never").ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
            }

            Require(lockedGet is null ||
                    lockedGet.Status == 423 && lockedGet.JsonBody.GetProperty("error").GetString() == "vault_locked",
                "A post-lock read returned private content instead of a refusal or a closed bridge.");
            Require(lockedSave is null || lockedSave.Status == 423,
                "A post-lock save was not refused or the bridge did not close.");
            var lockedStatus = await browser.WaitForAsync("document.getElementById('status').textContent",
                value => value.Contains("session ended", StringComparison.Ordinal) ||
                    value.Contains("no longer reachable", StringComparison.Ordinal), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            var cleared = await browser.EvaluateStringAsync(
                "document.getElementById('preview-view').textContent.length + " +
                "document.getElementById('editor').value.length + document.getElementById('name').textContent.length + " +
                "(document.querySelector('meta[name=mb-session]') === null ? 0 : 1)")
                .ConfigureAwait(false);
            Require((lockedStatus.Contains("session ended", StringComparison.Ordinal) ||
                     lockedStatus.Contains("no longer reachable", StringComparison.Ordinal)) && cleared == "0",
                "The lock did not clear browser document content, draft and capability.");
            Console.WriteLine("PASS lock revokes reads/saves, stops the bridge, and clears actual browser content and draft");

            await browser.DisposeAsync().ConfigureAwait(false);
            browser = null;
            await S13T1StopOpenAsync(openProcess).ConfigureAwait(false);
            openProcess = null;

            await UnlockWorkspaceAsync(executable, fixture, passphrase).ConfigureAwait(false);

            // Read-only and unsupported documents are truthful, never fake-editable.
            foreach (var (fileId, expectedName, expectedState) in new[]
            {
                (bomId, "bom.md", "editable"),
                (mixedId, "mixed.txt", "read_only_newlines"),
                (bareMixedId, "bare-mixed.txt", "read_only_newlines"),
                (carriageReturnId, "cr.txt", "editable"),
                (utf16Id, "utf16.txt", "read_only_encoding"),
                (binaryId, "blob.bin", "binary"),
                (csvId, "table.csv", "editable"),
                (jsonlId, "events.jsonl", "editable"),
            })
            {
                var readOnlyOpen = await S13T1StartOpenAsync(executable, fixture, fileId, lifetimeSeconds: 30).ConfigureAwait(false);
                try
                {
                    var view = await S13T1ReadDocumentAsync(readOnlyOpen.Url, readOnlyOpen.Token).ConfigureAwait(false);
                    Require(string.Equals(view.GetProperty("name").GetString(), expectedName, StringComparison.Ordinal) &&
                            string.Equals(view.GetProperty("state").GetString(), expectedState, StringComparison.Ordinal) &&
                            view.GetProperty("editable").GetBoolean() == (expectedState == "editable"),
                        "The support matrix reported the wrong state for " + expectedName + ": " + view.GetRawText());
                    if (!view.GetProperty("editable").GetBoolean())
                    {
                        var refused = await S13T1SaveAsync(readOnlyOpen.Url, readOnlyOpen.Token,
                            view.GetProperty("contentRevision").GetInt64(), "refused").ConfigureAwait(false);
                        Require(refused.Status == 422 &&
                                refused.JsonBody.GetProperty("error").GetString() == "document_not_editable",
                            "A read-only document accepted a save: " + expectedName + " -> " + refused.Status.ToString(CultureInfo.InvariantCulture) + ".");
                    }
                    if (expectedState == "editable" && expectedName == "bom.md")
                    {
                        var noOp = await S13T1SaveAsync(readOnlyOpen.Url, readOnlyOpen.Token,
                            view.GetProperty("contentRevision").GetInt64(), view.GetProperty("text").GetString()!)
                            .ConfigureAwait(false);
                        Require(noOp.Status == 200 && noOp.JsonBody.GetProperty("status").GetString() == "unchanged",
                            "A no-op save of the BOM document did not report unchanged.");
                        RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, fileId, null, "s13t1-bom-readback.md").ConfigureAwait(false),
                            bom, "BOM no-op bytes");
                        var modifiedBom = await S13T1SaveAsync(readOnlyOpen.Url, readOnlyOpen.Token,
                            view.GetProperty("contentRevision").GetInt64(),
                            view.GetProperty("text").GetString()! + "bom-added\n").ConfigureAwait(false);
                        Require(modifiedBom.Status == 200 && modifiedBom.JsonBody.GetProperty("status").GetString() == "saved",
                            "A BOM document edit was refused.");
                        RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, fileId, null, "s13t1-bom-modified.md").ConfigureAwait(false),
                            bom.Concat(Encoding.UTF8.GetBytes("bom-added\r\n")).ToArray(), "BOM-preserved edit bytes");
                    }

                    if (expectedState == "editable" && expectedName == "cr.txt")
                    {
                        var noOp = await S13T1SaveAsync(readOnlyOpen.Url, readOnlyOpen.Token,
                            view.GetProperty("contentRevision").GetInt64(), view.GetProperty("text").GetString()!)
                            .ConfigureAwait(false);
                        Require(noOp.Status == 200 && noOp.JsonBody.GetProperty("status").GetString() == "unchanged",
                            "A no-op save of the CR-only document did not report unchanged.");
                        RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, fileId, null, "s13t1-cr-readback.txt").ConfigureAwait(false),
                            carriageReturn, "CR no-op bytes");
                        var modifiedCr = await S13T1SaveAsync(readOnlyOpen.Url, readOnlyOpen.Token,
                            view.GetProperty("contentRevision").GetInt64(),
                            view.GetProperty("text").GetString()! + "cr-added\n").ConfigureAwait(false);
                        Require(modifiedCr.Status == 200 && modifiedCr.JsonBody.GetProperty("status").GetString() == "saved",
                            "A CR-only document edit was refused.");
                        RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, fileId, null, "s13t1-cr-modified.txt").ConfigureAwait(false),
                            carriageReturn.Concat(Encoding.UTF8.GetBytes("cr-added\r")).ToArray(), "CR-preserved edit bytes");
                    }
                }
                finally
                {
                    await S13T1StopOpenAsync(readOnlyOpen.Process).ConfigureAwait(false);
                }
            }

            Console.WriteLine("PASS BOM/CRLF/CR, mixed-newline, UTF-16, binary, CSV and JSONL states match the settled matrix");
            Console.WriteLine("PASS bare-LF + bare-CR-only mixed file is read_only_newlines and its save is refused");

            // The explicit lifetime is the shutdown proof available to a
            // non-interactive harness; Ctrl+C uses the same cancellation token.
            var expiring = await S13T1StartOpenAsync(executable, fixture, csvId, lifetimeSeconds: 4).ConfigureAwait(false);
            await expiring.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            var closing = await expiring.Process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            Require(expiring.Process.ExitCode == 0 && closing.Contains("OPEN expired", StringComparison.Ordinal),
                "The explicit document-session lifetime did not end cleanly: " + closing.Trim());
            var expired = await S13T1RequestOrNullAsync(expiring.Url).ConfigureAwait(false);
            Require(expired is null, "The expired bridge still answered a request.");
            Console.WriteLine("PASS explicit session lifetime ends the foreground bridge and the port stops answering");
        }
        finally
        {
            if (browser is not null)
            {
                await browser.DisposeAsync().ConfigureAwait(false);
            }

            S13T1TryDeleteDirectory(chromeProfile);

            if (openProcess is not null)
            {
                await S13T1StopOpenAsync(openProcess).ConfigureAwait(false);
            }

            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private sealed record S13T1Response(int Status, byte[] Body, string? ContentSecurityPolicy, string? CacheControl)
    {
        public JsonElement JsonBody => JsonDocument.Parse(Body).RootElement.Clone();
    }

    private static async Task<S13T1Response> S13T1RequestAsync(
        string url,
        string? token = null,
        string? origin = null,
        string? site = null,
        string? host = null,
        byte[]? body = null,
        string? contentType = null)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("X-MetaBrain-Session", token);
        }

        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (site is not null)
        {
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", site);
        }

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/json");
        }

        using var response = await S13T1Http.SendAsync(request).ConfigureAwait(false);
        var content = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        response.Headers.TryGetValues("Content-Security-Policy", out var policies);
        response.Headers.TryGetValues("Cache-Control", out var cache);
        return new S13T1Response((int)response.StatusCode, content, policies?.FirstOrDefault(), cache?.FirstOrDefault());
    }

    private static async Task<S13T1Response?> S13T1RequestOrNullAsync(string url)
    {
        try
        {
            return await S13T1RequestAsync(url).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static async Task<JsonElement> S13T1ReadDocumentAsync(string url, string token)
    {
        var response = await S13T1RequestAsync(url + "api/document", token).ConfigureAwait(false);
        Require(response.Status == 200, "The bound document read failed with " + response.Status.ToString(CultureInfo.InvariantCulture) + ".");
        return JsonDocument.Parse(response.Body).RootElement.Clone();
    }

    private static async Task<S13T1Response> S13T1SaveAsync(string url, string token, long expectedRevision, string text)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { expectedRevision, text });
        return await S13T1RequestAsync(url + "api/document/save", token,
            origin: url.TrimEnd('/'), body: body, contentType: "application/json").ConfigureAwait(false);
    }

    private static async Task<(Process Process, string Url, string Token)> S13T1StartOpenAsync(
        string executable,
        SmokeFixture fixture,
        string fileId,
        int lifetimeSeconds)
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
        start.ArgumentList.Add("--no-browser");
        start.ArgumentList.Add("--lifetime-seconds");
        start.ArgumentList.Add(lifetimeSeconds.ToString(CultureInfo.InvariantCulture));
        var process = new Process { StartInfo = start };
        Require(process.Start(), "The document bridge process did not start.");
        process.StandardInput.Close();
        try
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (line is null)
            {
                var standardError = process.HasExited
                    ? await process.StandardError.ReadToEndAsync().ConfigureAwait(false)
                    : string.Empty;
                throw new InvalidOperationException(
                    "The document bridge did not announce a bound session: <no line>; stderr=" + standardError.Trim());
            }

            Require(line.StartsWith("OPEN document ", StringComparison.Ordinal),
                "The document bridge did not announce a bound session: " + line);
            var announcement = line;
            var urlMatch = Regex.Match(announcement, @"url=(\S+)$");
            Require(urlMatch.Success, "The document bridge announcement carried no URL: " + announcement);
            var url = urlMatch.Groups[1].Value;
            var shell = await S13T1RequestAsync(url).ConfigureAwait(false);
            Require(shell.Status == 200, "The document bridge shell was not reachable.");
            var tokenMatch = Regex.Match(Encoding.UTF8.GetString(shell.Body), "name=\"mb-session\" content=\"([^\"]+)\"");
            Require(tokenMatch.Success, "The document bridge shell carried no session capability.");
            return (process, url, tokenMatch.Groups[1].Value);
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

    private static async Task S13T1StopOpenAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void S13T1TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static string S13T1ChromePath()
    {
        var configured = Environment.GetEnvironmentVariable("CHROME_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string[] candidates =
        [
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("The S13-T1 smoke needs an installed Chromium browser; set CHROME_PATH.");
    }

    private sealed class S13T1Browser : IAsyncDisposable
    {
        private readonly Process process;
        private readonly ClientWebSocket socket;
        private int commandId;

        private S13T1Browser(Process process, ClientWebSocket socket)
        {
            this.process = process;
            this.socket = socket;
        }
        public int ProcessId => process.Id;

        public static async Task<S13T1Browser> StartAsync(string chromePath, string profileDirectory, string url)
        {
            Directory.CreateDirectory(profileDirectory);
            var start = new ProcessStartInfo(chromePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
            {
                "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
                "--disable-background-networking", "--disable-component-update", "--disable-sync",
                "--disable-extensions", "--metrics-recording-only", "--remote-debugging-port=0",
                "--user-data-dir=" + profileDirectory, url,
            })
            {
                start.ArgumentList.Add(argument);
            }

            var browser = new Process { StartInfo = start };
            Require(browser.Start(), "The installed Chromium browser did not start.");
            try
            {
                var activePortPath = Path.Combine(profileDirectory, "DevToolsActivePort");
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline &&
                       (!File.Exists(activePortPath) || new FileInfo(activePortPath).Length == 0))
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }

                Require(File.Exists(activePortPath), "The headless browser never exposed its DevTools endpoint.");
                var port = (await File.ReadAllLinesAsync(activePortPath).ConfigureAwait(false))[0].Trim();
                var listJson = await S13T1Http.GetStringAsync("http://127.0.0.1:" + port + "/json/list").ConfigureAwait(false);
                using var pages = JsonDocument.Parse(listJson);
                var page = pages.RootElement.EnumerateArray()
                    .FirstOrDefault(page => page.GetProperty("type").GetString() == "page" &&
                        page.GetProperty("url").GetString()?.StartsWith(url, StringComparison.Ordinal) == true);
                Require(page.ValueKind == JsonValueKind.Object, "The headless browser did not open the document bridge target.");
                var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!), CancellationToken.None)
                    .ConfigureAwait(false);
                var instance = new S13T1Browser(browser, socket);
                await instance.CommandAsync("Runtime.enable", null).ConfigureAwait(false);
                return instance;
            }
            catch
            {
                try
                {
                    if (!browser.HasExited)
                    {
                        browser.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                browser.Dispose();
                throw;
            }
        }
        public async Task NavigateAsync(string url)
        {
            await CommandAsync("Page.navigate", new Dictionary<string, object?>
            {
                ["url"] = url,
            }).ConfigureAwait(false);
        }
        public async Task ReloadAsync()
        {
            await CommandAsync("Page.reload", new Dictionary<string, object?> { ["ignoreCache"] = true })
                .ConfigureAwait(false);
        }

        public async Task<string> EvaluateStringAsync(string expression)
        {
            var value = await EvaluateAsync(expression).ConfigureAwait(false);
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
        }

        public async Task<JsonElement> EvaluateAsync(string expression)
        {
            var reply = await CommandAsync("Runtime.evaluate", new Dictionary<string, object?>
            {
                ["expression"] = expression,
                ["awaitPromise"] = true,
                ["returnByValue"] = true,
            }).ConfigureAwait(false);
            if (!reply.TryGetProperty("result", out var result))
            {
                throw new InvalidOperationException("The browser evaluate reply had no result.");
            }

            if (result.TryGetProperty("exceptionDetails", out var exception))
            {
                throw new InvalidOperationException("The browser evaluate failed: " + exception.GetRawText());
            }

            // Runtime.evaluate wraps the JavaScript value in a RemoteObject.
            // With returnByValue the actual JSON sits in its "value" member;
            // an undefined result simply has no value member.
            return result.TryGetProperty("result", out var remote) &&
                   remote.ValueKind == JsonValueKind.Object &&
                   remote.TryGetProperty("value", out var value)
                ? value.Clone()
                : default;
        }

        public async Task<string> WaitForAsync(string expression, Func<string, bool> accept, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            var last = string.Empty;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    last = await EvaluateStringAsync(expression).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // The page may still be navigating or its execution context
                    // may not hold the document yet; retry until the deadline.
                    await Task.Delay(150).ConfigureAwait(false);
                    continue;
                }

                if (accept(last))
                {
                    return last;
                }

                await Task.Delay(150).ConfigureAwait(false);
            }

            throw new InvalidOperationException("The browser condition did not hold in time; last value: " + last);
        }

        private async Task<JsonElement> CommandAsync(string method, object? parameters)
        {
            var id = ++commandId;
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new Dictionary<string, object?>(),
            });
            await socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, CancellationToken.None)
                .ConfigureAwait(false);
            while (true)
            {
                var document = await ReceiveAsync().ConfigureAwait(false);
                if (document.RootElement.TryGetProperty("id", out var idValue) && idValue.GetInt32() == id)
                {
                    return document.RootElement.Clone();
                }
            }
        }

        private async Task<JsonDocument> ReceiveAsync()
        {
            var buffer = new byte[64 * 1024];
            using var content = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("The browser DevTools endpoint closed unexpectedly.");
                }

                content.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return JsonDocument.Parse(content.ToArray());
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                socket.Dispose();
            }
            catch
            {
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
