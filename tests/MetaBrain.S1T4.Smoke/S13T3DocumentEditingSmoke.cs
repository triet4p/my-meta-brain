using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S13-T3 exercises explicit workspace text editing through the actual owner
/// CLI, shared local bridge, and installed Chromium browser. Fixtures are
/// synthetic and confined to a temporary SmokeFixture directory.
/// </summary>
internal static partial class OwnerServiceSmoke
{
    public static async Task RunS13T3DocumentEditingAsync(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S13-T3 document-editing smoke is Windows-only.");
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
                "The owner CLI did not provision the synthetic S13-T3 vault (stderr=" + provision.StandardError.Trim() + ").");

            var mkdir = await RunOwnerCommandAsync(executable,
                "owner", "workspace-mkdir", "--control-pipe", fixture.ControlPipe,
                "--parent-id", "root", "--name", "s13-t3-documents",
                "--expected-tree-revision", "0").ConfigureAwait(false);
            Require(mkdir.ExitCode == 0 && mkdir.StandardOutput.Contains("MKDIR id=", StringComparison.Ordinal),
                "The owner CLI did not create the synthetic T3 workspace folder (stderr=" + mkdir.StandardError.Trim() + ").");
            var folderId = WorkspaceToken(mkdir.StandardOutput, "id");
            var treeRevision = WorkspaceNumber(mkdir.StandardOutput, "revision");

            var markdownBytes = Encoding.UTF8.GetBytes("# Original markdown\r\n\r\nCLI baseline\r\n");
            var textBytes = Encoding.UTF8.GetBytes("plain original\r\nsecond line\r\n");
            var csvBytes = Encoding.UTF8.GetBytes("key,value\r\none,alpha\r\n");
            var jsonlBytes = Encoding.UTF8.GetBytes("{\"event\":\"start\"}\n");
            var unsupportedBytes = Encoding.UTF8.GetBytes("unsupported synthetic text\n");
            var utf16Bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("UTF-16 synthetic\r\n")).ToArray();
            var mixedNewlineBytes = Encoding.UTF8.GetBytes("line one\r\nline two\nline three\r\n");
            var binaryBytes = new byte[] { 0x00, 0xFF, 0x80, 0xC0, 0xAF };

            async Task<string> ImportAsync(string name, string inputName, byte[] bytes)
            {
                var inputPath = await WriteWorkspaceInputAsync(fixture, inputName, bytes).ConfigureAwait(false);
                var imported = await ImportWorkspaceFileAsync(
                    executable, fixture, folderId, name, inputPath, treeRevision).ConfigureAwait(false);
                treeRevision = imported.TreeRevision;
                return imported.FileId;
            }

            var markdownId = await ImportAsync("editable.md", "s13t3-markdown.md", markdownBytes).ConfigureAwait(false);
            var textId = await ImportAsync("notes.txt", "s13t3-plain.txt", textBytes).ConfigureAwait(false);
            var csvId = await ImportAsync("table.csv", "s13t3-table.csv", csvBytes).ConfigureAwait(false);
            var jsonlId = await ImportAsync("events.jsonl", "s13t3-events.jsonl", jsonlBytes).ConfigureAwait(false);
            var unsupportedId = await ImportAsync("notes.xyz", "s13t3-unsupported.xyz", unsupportedBytes).ConfigureAwait(false);
            var utf16Id = await ImportAsync("utf16.txt", "s13t3-utf16.txt", utf16Bytes).ConfigureAwait(false);
            var mixedId = await ImportAsync("mixed.txt", "s13t3-mixed.txt", mixedNewlineBytes).ConfigureAwait(false);
            var binaryId = await ImportAsync("binary.bin", "s13t3-binary.bin", binaryBytes).ConfigureAwait(false);

            const string sourceId = "s13t3-original-source";
            var sourceCreate = await RunOwnerCommandAsync(executable,
                "owner", "source-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", sourceId, "--zone-id", "s13t3-source-zone",
                "--title", "Synthetic original source", "--locator", "synthetic:s13t3-original",
                "--version", "synthetic-source-v1", "--availability", "available").ConfigureAwait(false);
            Require(sourceCreate.ExitCode == 0 && sourceCreate.StandardOutput.Contains("SOURCE id=" + sourceId, StringComparison.Ordinal),
                "The synthetic source-session read-only fixture could not be registered (stderr=" + sourceCreate.StandardError.Trim() + ").");

            var cliMarkdown = Encoding.UTF8.GetBytes("# CLI revision\r\n\r\nCLI-only baseline\r\n");
            var cliMarkdownPath = await WriteWorkspaceInputAsync(fixture, "s13t3-cli-revision.md", cliMarkdown).ConfigureAwait(false);
            var cliRevise = await RunOwnerCommandAsync(executable,
                "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                "--file-id", markdownId, "--expected-content-revision", "1",
                "--input-file", cliMarkdownPath).ConfigureAwait(false);
            Require(cliRevise.ExitCode == 0 && cliRevise.StandardOutput.Contains("contentRevision=2", StringComparison.Ordinal),
                "The CLI-only workspace revise did not create content revision 2 (stderr=" + cliRevise.StandardError.Trim() + ").");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, null, "s13t3-cli-readback.md").ConfigureAwait(false),
                cliMarkdown, "CLI-only revise before any browser was opened");
            Console.WriteLine("PASS CLI-only workspace-revise created revision 2 before any browser/editor was opened");

            async Task RunBrowserSessionAsync(string fileId, Func<S13T1Browser, string, string, Task> scenario)
            {
                var opened = await S13T1StartOpenAsync(executable, fixture, fileId, lifetimeSeconds: 180).ConfigureAwait(false);
                var profile = Path.Combine(Path.GetTempPath(), "MetaBrain-S13T3-Chrome-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await using var browser = await S13T1Browser.StartAsync(S13T1ChromePath(), profile, opened.Url)
                        .ConfigureAwait(false);
                    await browser.WaitForAsync("document.getElementById('status').textContent",
                        value => value.Length > 0, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                    await scenario(browser, opened.Url, opened.Token).ConfigureAwait(false);
                }
                finally
                {
                    await S13T1StopOpenAsync(opened.Process).ConfigureAwait(false);
                    S13T1TryDeleteDirectory(profile);
                }
            }

            static async Task ClickAsync(S13T1Browser browser, string id)
            {
                var expression = "document.getElementById(" + JsonSerializer.Serialize(id) + ").click(); true";
                await browser.EvaluateAsync(expression).ConfigureAwait(false);
            }

            static async Task SetEditorAsync(S13T1Browser browser, string text)
            {
                var expression = "(() => { const editor = document.getElementById('editor'); editor.value = " +
                    JsonSerializer.Serialize(text) + "; editor.dispatchEvent(new Event('input', { bubbles: true })); return true; })()";
                await browser.EvaluateAsync(expression).ConfigureAwait(false);
            }

            async Task LockAndRestartAsync()
            {
                var locked = await RunOwnerCommandAsync(executable, "owner", "lock", "--control-pipe", fixture.ControlPipe)
                    .ConfigureAwait(false);
                Require(locked.ExitCode == 0 && locked.StandardOutput.Contains("VAULT locked", StringComparison.Ordinal),
                    "The synthetic vault did not lock before its process restart.");
                await StopServiceAsync(service!).ConfigureAwait(false);
                service = null;
                service = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
                var status = await RunOwnerCommandAsync(executable, "owner", "status", "--control-pipe", fixture.ControlPipe)
                    .ConfigureAwait(false);
                Require(status.ExitCode == 0 && status.StandardOutput.Contains("vault=locked", StringComparison.Ordinal),
                    "The restarted T3 service did not start locked.");
                await UnlockWorkspaceAsync(executable, fixture, passphrase).ConfigureAwait(false);
            }

            await LockAndRestartAsync().ConfigureAwait(false);
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, null, "s13t3-cli-after-restart.md").ConfigureAwait(false),
                cliMarkdown, "CLI-only revision after service restart");
            Console.WriteLine("PASS CLI-only content revision survives a locked service restart and explicit unlock");

            var browserMarkdown = Encoding.UTF8.GetBytes("# Browser revision\r\n\r\nSaved by the explicit browser editor\r\n");
            var browserMarkdownText = "# Browser revision\n\nSaved by the explicit browser editor\n";
            await RunBrowserSessionAsync(markdownId, async (browser, url, token) =>
            {
                var originalView = await S13T1ReadDocumentAsync(url, token).ConfigureAwait(false);
                Require(originalView.GetProperty("contentRevision").GetInt64() == 2 &&
                        originalView.GetProperty("text").GetString() == "# CLI revision\r\n\r\nCLI-only baseline\r\n",
                    "The actual browser was not bound to the CLI-only revision after restart.");
                var originalBytes = await ReadWorkspaceFileAsync(executable, fixture, markdownId, null, "s13t3-before-toggle.md")
                    .ConfigureAwait(false);

                await ClickAsync(browser, "source").ConfigureAwait(false);
                await browser.WaitForAsync("document.getElementById('preview-view').textContent",
                    value => value.Contains("# CLI revision", StringComparison.Ordinal), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                await ClickAsync(browser, "source").ConfigureAwait(false);
                await browser.WaitForAsync("document.getElementById('preview-view').textContent",
                    value => value.Contains("CLI-only baseline", StringComparison.Ordinal), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, null, "s13t3-after-toggle.md").ConfigureAwait(false),
                    originalBytes, "preview/source toggle did not rewrite bytes");

                await ClickAsync(browser, "edit").ConfigureAwait(false);
                await browser.WaitForAsync("document.getElementById('editor').hidden ? 'hidden' : 'visible'",
                    value => value == "visible", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                var editorText = await browser.EvaluateStringAsync("document.getElementById('editor').value").ConfigureAwait(false);
                Require(editorText == "# CLI revision\n\nCLI-only baseline\n",
                    "The explicit editor did not load the raw Markdown text from its bound revision.");
                await ClickAsync(browser, "save").ConfigureAwait(false);
                var noOpStatus = await browser.WaitForAsync("document.getElementById('status').textContent",
                    value => value.Contains("No change", StringComparison.Ordinal), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Require(noOpStatus.Contains("revision is still 2", StringComparison.Ordinal),
                    "A no-op editor save did not keep the bound content revision.");
                RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, null, "s13t3-noop.md").ConfigureAwait(false),
                    originalBytes, "no-op editor save bytes");

                await SetEditorAsync(browser, browserMarkdownText).ConfigureAwait(false);
                await ClickAsync(browser, "save").ConfigureAwait(false);
                var saved = await browser.WaitForAsync("document.getElementById('status').textContent",
                    value => value.Contains("Saved revision 3", StringComparison.Ordinal), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Require(saved.Contains("Saved revision 3", StringComparison.Ordinal), "The browser did not report its explicit save.");
                RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, null, "s13t3-browser-save.md").ConfigureAwait(false),
                    browserMarkdown, "browser Markdown save with original CRLF convention");

                var draft = browserMarkdownText + "unsaved draft\n";
                await SetEditorAsync(browser, draft).ConfigureAwait(false);
                await ClickAsync(browser, "cancel-edit").ConfigureAwait(false);
                var cancelStatus = await browser.WaitForAsync("document.getElementById('status').textContent",
                    value => value.Contains("unsaved", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Require(cancelStatus.Contains("nothing was saved", StringComparison.Ordinal),
                    "Cancel did not explain that the dirty browser draft was not saved.");
                Require(await browser.EvaluateStringAsync("document.getElementById('discard-confirm').hidden ? 'hidden' : 'visible'")
                        .ConfigureAwait(false) == "visible",
                    "Cancel did not require explicit confirmation before discarding the unsaved draft.");
                await ClickAsync(browser, "keep-editing").ConfigureAwait(false);
                await browser.WaitForAsync("document.getElementById('status').textContent",
                    value => value.Contains("unsaved draft remains", StringComparison.Ordinal), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                await ClickAsync(browser, "preview").ConfigureAwait(false);
                await browser.WaitForAsync("document.getElementById('status').textContent",
                    value => value.Contains("unsaved draft remains in this page", StringComparison.Ordinal), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                await ClickAsync(browser, "edit").ConfigureAwait(false);
                var resumedDraft = await browser.WaitForAsync("document.getElementById('editor').value",
                    value => value == draft, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Require(resumedDraft == draft, "Returning to edit did not preserve the in-page unsaved draft.");

                var storage = await browser.EvaluateAsync("(async () => ({ local: localStorage.length, session: sessionStorage.length, " +
                    "cookies: document.cookie.length, cache: (await caches.keys()).length, " +
                    "indexedDb: indexedDB.databases ? (await indexedDB.databases()).length : 0 }))()").ConfigureAwait(false);
                Require(storage.GetProperty("local").GetInt32() == 0 && storage.GetProperty("session").GetInt32() == 0 &&
                        storage.GetProperty("cookies").GetInt32() == 0 && storage.GetProperty("cache").GetInt32() == 0 &&
                        storage.GetProperty("indexedDb").GetInt32() == 0,
                    "The dirty draft appeared in browser-origin storage or a service cache.");

                await ClickAsync(browser, "cancel-edit").ConfigureAwait(false);
                await browser.WaitForAsync("document.getElementById('discard-confirm').hidden ? 'hidden' : 'visible'",
                    value => value == "visible", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                await ClickAsync(browser, "confirm-discard").ConfigureAwait(false);
                var discarded = await browser.WaitForAsync("document.getElementById('status').textContent",
                    value => value.Contains("Draft discarded from this page", StringComparison.Ordinal), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Require(discarded.Contains("stored document bytes were not changed", StringComparison.Ordinal),
                    "Explicit discard did not explain that stored bytes remained unchanged.");
                RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, null, "s13t3-discarded-draft.md").ConfigureAwait(false),
                    browserMarkdown, "discarded browser draft bytes");
            }).ConfigureAwait(false);
            Console.WriteLine("PASS actual Chromium preview/source toggle, no-op save, explicit revision save, in-memory draft, cancel confirmation and discard");

            await LockAndRestartAsync().ConfigureAwait(false);
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, null, "s13t3-browser-after-restart.md").ConfigureAwait(false),
                browserMarkdown, "browser-saved Markdown after locked service restart and unlock");
            Console.WriteLine("PASS browser-saved revision 3 survives service restart and explicit unlock");

            var externalMarkdown = Encoding.UTF8.GetBytes("# CLI concurrent revision\r\n\r\nExternal writer wins\r\n");
            var externalMarkdownPath = await WriteWorkspaceInputAsync(fixture, "s13t3-external-revision.md", externalMarkdown).ConfigureAwait(false);
            await RunBrowserSessionAsync(markdownId, async (browser, url, token) =>
            {
                var bound = await S13T1ReadDocumentAsync(url, token).ConfigureAwait(false);
                Require(bound.GetProperty("contentRevision").GetInt64() == 3 &&
                        bound.GetProperty("text").GetString() == "# Browser revision\r\n\r\nSaved by the explicit browser editor\r\n",
                    "The reopened browser did not display the saved Markdown revision after restart.");
                var loaded = await browser.WaitForAsync("document.getElementById('preview-view').textContent",
                    value => value.Contains("Saved by the explicit browser editor", StringComparison.Ordinal), TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                Require(loaded.Contains("Saved by the explicit browser editor", StringComparison.Ordinal),
                    "The reopened actual browser did not render the persisted browser edit.");
                await ClickAsync(browser, "edit").ConfigureAwait(false);
                await browser.WaitForAsync("document.getElementById('editor').hidden ? 'hidden' : 'visible'",
                    value => value == "visible", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                const string conflictDraft = "# Stale browser draft\n\nMust remain in this page\n";
                await SetEditorAsync(browser, conflictDraft).ConfigureAwait(false);

                var external = await RunOwnerCommandAsync(executable,
                    "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                    "--file-id", markdownId, "--expected-content-revision", "3",
                    "--input-file", externalMarkdownPath).ConfigureAwait(false);
                Require(external.ExitCode == 0 && external.StandardOutput.Contains("contentRevision=4", StringComparison.Ordinal),
                    "The independent CLI writer did not create the concurrent content revision.");

                await ClickAsync(browser, "save").ConfigureAwait(false);
                var conflictStatus = await browser.WaitForAsync("document.getElementById('status').textContent",
                    value => value.Contains("revision", StringComparison.OrdinalIgnoreCase) &&
                             value.Contains("draft", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Require(conflictStatus.Contains("draft", StringComparison.OrdinalIgnoreCase),
                    "The browser did not report the stale-save conflict while retaining its draft.");
                var preservedDraft = await browser.EvaluateStringAsync("document.getElementById('editor').value").ConfigureAwait(false);
                Require(preservedDraft == conflictDraft, "A revision conflict cleared or rewrote the browser draft.");
                RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, null, "s13t3-concurrent-readback.md").ConfigureAwait(false),
                    externalMarkdown, "concurrent conflict did not overwrite latest CLI bytes");
            }).ConfigureAwait(false);
            Console.WriteLine("PASS reopened actual browser after restart; concurrent CLI revision causes conflict without overwrite or draft loss");

            var sourceOpen = await RunOwnerCommandAsync(executable, "open", sourceId, "--control-pipe", fixture.ControlPipe)
                .ConfigureAwait(false);
            Require(sourceOpen.ExitCode == 3 && sourceOpen.StandardError.Contains("workspace_not_found", StringComparison.Ordinal) &&
                    !sourceOpen.StandardOutput.Contains("OPEN document", StringComparison.Ordinal),
                "The original synthetic source registry entry was served as an editable workspace document.");
            Console.WriteLine("PASS a registered synthetic original source ID is outside the workspace-only bridge and cannot be opened for editing");

            await RunBrowserSessionAsync(csvId, async (browser, url, token) =>
            {
                var view = await S13T1ReadDocumentAsync(url, token).ConfigureAwait(false);
                Require(view.GetProperty("format").GetString() == "csv" && view.GetProperty("editable").GetBoolean(),
                    "The supported CSV workspace document was not explicitly editable.");
                await ClickAsync(browser, "edit").ConfigureAwait(false);
                await browser.WaitForAsync("document.getElementById('editor').hidden ? 'hidden' : 'visible'",
                    value => value == "visible", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Require(await browser.EvaluateStringAsync("document.getElementById('editor').value").ConfigureAwait(false) ==
                        "key,value\none,alpha\n",
                    "CSV Edit did not expose the raw CSV text (or substituted a grid editor).");
                const string invalidCsv = "key,value\none,\"unterminated";
                await SetEditorAsync(browser, invalidCsv).ConfigureAwait(false);
                await ClickAsync(browser, "save").ConfigureAwait(false);
                var refusal = await browser.WaitForAsync("document.getElementById('status').textContent",
                    value => value.Contains("edited CSV", StringComparison.Ordinal), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Require(refusal.Contains("draft stays in this page", StringComparison.Ordinal),
                    "An invalid CSV edit did not explain the refusal and draft retention.");
                Require(await browser.EvaluateStringAsync("document.getElementById('editor').value").ConfigureAwait(false) == invalidCsv,
                    "The invalid CSV draft was cleared after the refusal.");
                RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, csvId, null, "s13t3-invalid-csv.md").ConfigureAwait(false),
                    csvBytes, "invalid CSV refusal left source bytes untouched");

                const string validCsv = "key,value\none,alpha\ntwo,beta\n";
                await SetEditorAsync(browser, validCsv).ConfigureAwait(false);
                await ClickAsync(browser, "save").ConfigureAwait(false);
                await browser.WaitForAsync("document.getElementById('status').textContent",
                    value => value.Contains("Saved revision 2", StringComparison.Ordinal), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, csvId, null, "s13t3-valid-csv.csv").ConfigureAwait(false),
                    Encoding.UTF8.GetBytes("key,value\r\none,alpha\r\ntwo,beta\r\n"), "valid raw CSV save with original CRLF convention");
            }).ConfigureAwait(false);
            Console.WriteLine("PASS actual raw-text CSV editor rejects malformed quotes with draft preserved, then saves valid CSV");

            await RunBrowserSessionAsync(textId, async (browser, url, token) =>
            {
                var view = await S13T1ReadDocumentAsync(url, token).ConfigureAwait(false);
                Require(view.GetProperty("format").GetString() == "text" && view.GetProperty("editable").GetBoolean(),
                    "The supported plain-text workspace document was not editable.");
                await ClickAsync(browser, "edit").ConfigureAwait(false);
                await browser.WaitForAsync("document.getElementById('editor').hidden ? 'hidden' : 'visible'",
                    value => value == "visible", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                await browser.EvaluateAsync("(() => { const editor = document.getElementById('editor'); editor.value = 'bad-' + String.fromCharCode(0); editor.dispatchEvent(new Event('input', { bubbles: true })); return true; })()")
                    .ConfigureAwait(false);
                await ClickAsync(browser, "save").ConfigureAwait(false);
                var nulRefusal = await browser.WaitForAsync("document.getElementById('status').textContent",
                    value => value.Contains("NUL", StringComparison.Ordinal), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Require(nulRefusal.Contains("draft stays in this page", StringComparison.Ordinal) &&
                        await browser.EvaluateStringAsync("document.getElementById('editor').value.charCodeAt(4)").ConfigureAwait(false) == "0",
                    "A NUL-containing browser draft was not explained and preserved in memory.");
                var serverEncodingRefusal = await S13T1SaveAsync(url, token, 1, "bad-\0server").ConfigureAwait(false);
                Require(serverEncodingRefusal.Status == 422 &&
                        serverEncodingRefusal.JsonBody.GetProperty("error").GetString() == "invalid_encoding" &&
                        serverEncodingRefusal.JsonBody.GetProperty("message").GetString()!.Contains("draft stays in this page", StringComparison.Ordinal),
                    "The shared save API did not reject an invalid-encoding NUL draft with guidance.");
                RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, textId, null, "s13t3-nul-readback.txt").ConfigureAwait(false),
                    textBytes, "invalid-encoding NUL refusal bytes");

                await browser.EvaluateAsync("(() => { const editor = document.getElementById('editor'); editor.value = 'bad-' + String.fromCharCode(0xD800); editor.dispatchEvent(new Event('input', { bubbles: true })); return true; })()")
                    .ConfigureAwait(false);
                await ClickAsync(browser, "save").ConfigureAwait(false);
                var surrogateRefusal = await browser.WaitForAsync("document.getElementById('status').textContent",
                    value => value.Contains("unpaired Unicode surrogate", StringComparison.Ordinal), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Require(surrogateRefusal.Contains("draft stays in this page", StringComparison.Ordinal) &&
                        await browser.EvaluateStringAsync("document.getElementById('editor').value.charCodeAt(4)").ConfigureAwait(false) == "55296",
                    "An unpaired UTF-16 draft was not refused with the exact draft retained in memory.");
                RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, textId, null, "s13t3-surrogate-readback.txt").ConfigureAwait(false),
                    textBytes, "invalid UTF-16 scalar refusal bytes");

                await SetEditorAsync(browser, "plain rewritten\nsecond rewritten\n").ConfigureAwait(false);
                await ClickAsync(browser, "save").ConfigureAwait(false);
                await browser.WaitForAsync("document.getElementById('status').textContent",
                    value => value.Contains("Saved revision 2", StringComparison.Ordinal), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, textId, null, "s13t3-plain-save.txt").ConfigureAwait(false),
                    Encoding.UTF8.GetBytes("plain rewritten\r\nsecond rewritten\r\n"), "plain text save preserving source CRLF policy");
            }).ConfigureAwait(false);
            Console.WriteLine("PASS plain-text NUL/unpaired-surrogate refusals retain drafts; valid explicit save preserves CRLF");

            await RunBrowserSessionAsync(jsonlId, async (browser, url, token) =>
            {
                var view = await S13T1ReadDocumentAsync(url, token).ConfigureAwait(false);
                Require(view.GetProperty("format").GetString() == "jsonl" && view.GetProperty("editable").GetBoolean(),
                    "Workspace JSONL did not follow the supported T1 edit matrix.");
                await ClickAsync(browser, "edit").ConfigureAwait(false);
                await browser.WaitForAsync("document.getElementById('editor').hidden ? 'hidden' : 'visible'",
                    value => value == "visible", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                await SetEditorAsync(browser, "{\"event\":\"edited\"}\n").ConfigureAwait(false);
                await ClickAsync(browser, "save").ConfigureAwait(false);
                await browser.WaitForAsync("document.getElementById('status').textContent",
                    value => value.Contains("Saved revision 2", StringComparison.Ordinal), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, jsonlId, null, "s13t3-jsonl-save.jsonl").ConfigureAwait(false),
                    Encoding.UTF8.GetBytes("{\"event\":\"edited\"}\n"), "workspace JSONL explicit text save");
            }).ConfigureAwait(false);
            Console.WriteLine("PASS workspace JSONL follows the existing editable support matrix");

            async Task VerifyReadOnlyAsync(string fileId, string expectedState, byte[] originalBytes, string readbackName)
            {
                await RunBrowserSessionAsync(fileId, async (browser, url, token) =>
                {
                    var view = await S13T1ReadDocumentAsync(url, token).ConfigureAwait(false);
                    Require(view.GetProperty("state").GetString() == expectedState && !view.GetProperty("editable").GetBoolean(),
                        "The actual browser reported the wrong read-only state for " + expectedState + ".");
                    var reason = view.GetProperty("readOnlyReason").GetString();
                    Require(!string.IsNullOrWhiteSpace(reason), "The read-only preview did not provide an explanatory reason.");
                    var message = await browser.WaitForAsync("document.getElementById('status').textContent",
                        value => value.Contains(reason!, StringComparison.Ordinal), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    Require(message.Contains("Read-only:", StringComparison.Ordinal),
                        "The actual browser did not explain why this source is read-only.");
                    Require(await browser.EvaluateStringAsync("document.getElementById('edit').disabled ? 'disabled' : 'enabled'")
                            .ConfigureAwait(false) == "disabled",
                        "The browser Edit control was not disabled for unsupported input.");
                    var refused = await S13T1SaveAsync(url, token, view.GetProperty("contentRevision").GetInt64(),
                        "synthetic attempted edit").ConfigureAwait(false);
                    Require(refused.Status == 422 && refused.JsonBody.GetProperty("error").GetString() == "document_not_editable",
                        "The shared API did not truthfully refuse an unsupported edit.");
                    RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, fileId, null, readbackName).ConfigureAwait(false),
                        originalBytes, "read-only refusal preserved source bytes");
                }).ConfigureAwait(false);
            }

            await VerifyReadOnlyAsync(unsupportedId, "read_only_format", unsupportedBytes, "s13t3-unsupported-readback.xyz").ConfigureAwait(false);
            await VerifyReadOnlyAsync(utf16Id, "read_only_encoding", utf16Bytes, "s13t3-utf16-readback.txt").ConfigureAwait(false);
            await VerifyReadOnlyAsync(mixedId, "read_only_newlines", mixedNewlineBytes, "s13t3-mixed-readback.txt").ConfigureAwait(false);
            await VerifyReadOnlyAsync(binaryId, "binary", binaryBytes, "s13t3-binary-readback.bin").ConfigureAwait(false);
            Console.WriteLine("PASS unsupported format, UTF-16, mixed-newline and binary edits are explained and refused without rewriting source bytes");
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
