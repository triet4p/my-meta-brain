using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string S13T5DocumentMarker = "synthetic-s13t5-document-marker-6ab2";
    private const string S13T5LaterPageMarker = "synthetic-s13t5-later-page-marker-91c4";
    private const string S13T5SavedMarker = "synthetic-s13t5-saved-revision-8f21";
    private const string S13T5DraftMarker = "synthetic-s13t5-unsaved-draft-b537";
    private const string S13T5PromptMarker = "synthetic-s13t5-prompt-draft-73ed";
    private const string S13T5ExitMarker = "synthetic-s13t5-cli-exit-draft-214b";

    public static async Task RunS13T5DocumentLifecycleAsync(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S13-T5 document-lifecycle smoke is Windows-only.");
        }

        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        Process? openProcess = null;
        S13T1Browser? browser = null;
        var profile = Path.Combine(Path.GetTempPath(), "MetaBrain-S13T5-Chrome-" + Guid.NewGuid().ToString("N"));
        try
        {
            service = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            var passphrase = CreateSyntheticPassphrase();
            var provision = await RunOwnerCommandWithInputAsync(
                executable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The S13-T5 synthetic vault was not provisioned.");

            var folder = await RunOwnerCommandAsync(executable,
                "owner", "workspace-mkdir", "--control-pipe", fixture.ControlPipe,
                "--parent-id", "root", "--name", "s13-t5-lifecycle",
                "--expected-tree-revision", "0").ConfigureAwait(false);
            Require(folder.ExitCode == 0, "The synthetic lifecycle workspace folder was not created.");
            var folderId = WorkspaceToken(folder.StandardOutput, "id");
            var treeRevision = WorkspaceNumber(folder.StandardOutput, "revision");

            var sourceText = string.Join('\n', Enumerable.Range(0, 1600).Select(index =>
                index == 0 ? S13T5DocumentMarker :
                index == 400 ? S13T5LaterPageMarker :
                "synthetic-row-" + index.ToString("D4", CultureInfo.InvariantCulture) + "-" + new string('x', 40)));
            var sourceBytes = Encoding.UTF8.GetBytes(sourceText);
            var sourcePath = await WriteWorkspaceInputAsync(fixture, "s13t5-lifecycle.txt", sourceBytes).ConfigureAwait(false);
            var imported = await ImportWorkspaceFileAsync(
                executable, fixture, folderId, "lifecycle.txt", sourcePath, treeRevision).ConfigureAwait(false);
            File.Delete(sourcePath);

            for (var index = 0; index < 24; index++)
            {
                var id = "s13-t5-index-" + index.ToString("D3", CultureInfo.InvariantCulture);
                var body = "Synthetic S13-T5 indexing fixture " + index.ToString(CultureInfo.InvariantCulture) + " " + new string('i', 8192);
                var created = await RunOwnerCommandAsync(executable,
                    "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
                    "--resource-id", id, "--zone-id", "s13-t5-synthetic-zone", "--body", body).ConfigureAwait(false);
                Require(created.ExitCode == 0 && created.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
                    "A synthetic S13-T5 indexing record was not stored (index=" + index.ToString(CultureInfo.InvariantCulture) + ").");
            }

            var opened = await S13T1StartOpenAsync(executable, fixture, imported.FileId, lifetimeSeconds: 240).ConfigureAwait(false);
            openProcess = opened.Process;
            browser = await S13T1Browser.StartAsync(S13T1ChromePath(), profile, opened.Url).ConfigureAwait(false);
            var initialStatus = await browser.WaitForAsync("document.getElementById('status')?.textContent || ''",
                value => value.Contains("Preview of the bound revision", StringComparison.Ordinal) ||
                    value.Contains("Bound source page.", StringComparison.Ordinal), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            var initialContent = await browser.EvaluateStringAsync("document.getElementById('preview-view').textContent").ConfigureAwait(false);
            Require((initialStatus.Contains("Preview of the bound revision", StringComparison.Ordinal) ||
                     initialStatus.Contains("Bound source page.", StringComparison.Ordinal)) &&
                    initialContent.Contains(S13T5DocumentMarker, StringComparison.Ordinal),
                "The actual browser did not render the synthetic document before indexing.");

            var indexing = await RunMeasuredIndexRebuildAsync(
                executable, fixture.ControlPipe, service.Process.Id, openProcess!.Id, browser!.ProcessId).ConfigureAwait(false);
            Require(indexing.ExitCode == 0 && indexing.StandardOutput.Contains("INDEX rebuilt", StringComparison.Ordinal) &&
                    indexing.IndexCli.Samples > 0 && indexing.BridgeCli.Samples > 0 &&
                    indexing.Service.Samples > 0 && indexing.Browser.Samples > 0,
                "The real private-index rebuild or its concurrent CLI/service/browser resource samples were missing (stderr=" +
                indexing.StandardError.Trim() + ").");
            Console.WriteLine(
                "OBS S13-T5 indexing elapsedMs=" + indexing.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                " samples=" + indexing.IndexCli.Samples.ToString(CultureInfo.InvariantCulture) +
                " indexCli(wsPeakB=" + indexing.IndexCli.WorkingSetPeakBytes.ToString(CultureInfo.InvariantCulture) +
                ",privatePeakB=" + indexing.IndexCli.PrivatePeakBytes.ToString(CultureInfo.InvariantCulture) + ")" +
                " bridgeCli(wsPeakB=" + indexing.BridgeCli.WorkingSetPeakBytes.ToString(CultureInfo.InvariantCulture) +
                ",privatePeakB=" + indexing.BridgeCli.PrivatePeakBytes.ToString(CultureInfo.InvariantCulture) + ")" +
                " service(wsPeakB=" + indexing.Service.WorkingSetPeakBytes.ToString(CultureInfo.InvariantCulture) +
                ",privatePeakB=" + indexing.Service.PrivatePeakBytes.ToString(CultureInfo.InvariantCulture) + ")" +
                " browserProcess(wsPeakB=" + indexing.Browser.WorkingSetPeakBytes.ToString(CultureInfo.InvariantCulture) +
                ",privatePeakB=" + indexing.Browser.PrivatePeakBytes.ToString(CultureInfo.InvariantCulture) + ")");

            var storage = await ReadBrowserStorageAsync(browser).ConfigureAwait(false);
            Require(storage.GetProperty("localStorage").GetInt32() == 0 &&
                    storage.GetProperty("sessionStorage").GetInt32() == 0 &&
                    storage.GetProperty("cookies").GetInt32() == 0 &&
                    storage.GetProperty("cacheStorage").GetInt32() == 0 &&
                    storage.GetProperty("indexedDb").GetInt32() == 0 &&
                    storage.GetProperty("serviceWorkers").GetInt32() == 0,
                "The document bridge or its browser page persisted a capability or content in app-origin storage.");
            var vaultFiles = await ObserveVaultFilesAsync(fixture.VaultDirectoryPath, S13T5DocumentMarker).ConfigureAwait(false);
            Require(vaultFiles.FileCount > 0 && !vaultFiles.ContainsPlaintextMarker,
                "The synthetic document marker appeared in a plaintext file under the encrypted vault root.");
            var browserProfile = ObserveDirectoryFootprint(profile);
            Console.WriteLine(
                "OBS S13-T5 encryptedVaultFiles=" + vaultFiles.FileCount.ToString(CultureInfo.InvariantCulture) +
                " encryptedVaultBytes=" + vaultFiles.TotalBytes.ToString(CultureInfo.InvariantCulture) +
                " documentMarkerOnDisk=" + vaultFiles.ContainsPlaintextMarker.ToString().ToLowerInvariant() +
                " appOriginStorage=empty browserProfileFiles=" + browserProfile.FileCount.ToString(CultureInfo.InvariantCulture) +
                " browserProfileBytes=" + browserProfile.TotalBytes.ToString(CultureInfo.InvariantCulture));

            await browser.EvaluateAsync("document.getElementById('edit').click()").ConfigureAwait(false);
            await browser.WaitForAsync("document.getElementById('editor').hidden ? 'hidden' : 'visible'",
                value => value == "visible", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            await browser.EvaluateAsync("document.getElementById('editor').value += " + JsonSerializer.Serialize("\n" + S13T5SavedMarker)).ConfigureAwait(false);
            await browser.EvaluateAsync("""
                (() => {
                    const original = window.fetch.bind(window);
                    window.__s13t5SaveWaiting = false;
                    window.__s13t5SaveArm = true;
                    window.__s13t5ReleaseSave = null;
                    window.__s13t5SaveReply = '';
                    window.fetch = async (input, init) => {
                        const response = await original(input, init);
                        if (window.__s13t5SaveArm && String(input).endsWith('/api/document/save')) {
                            window.__s13t5SaveArm = false;
                            const payload = await response.text();
                            window.__s13t5SaveReply = payload;
                            window.__s13t5SaveWaiting = true;
                            await new Promise(resolve => window.__s13t5ReleaseSave = resolve);
                            return new Response(payload, { status: response.status, headers: { 'Content-Type': 'application/json' } });
                        }
                        return response;
                    };
                    document.getElementById('save').click();
                    return true;
                })()
                """).ConfigureAwait(false);
            await browser.WaitForAsync("String(window.__s13t5SaveWaiting)",
                value => value == "true", TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            var saveLock = await RunOwnerCommandAsync(executable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(saveLock.ExitCode == 0, "An unsaved browser draft blocked or prevented owner lock during the save race.");
            await WaitForBrowserRevocationAsync(browser, S13T5DocumentMarker, S13T5SavedMarker).ConfigureAwait(false);
            await browser.EvaluateAsync("window.__s13t5ReleaseSave && window.__s13t5ReleaseSave()").ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false);
            await RequireBrowserClearedAsync(browser, S13T5DocumentMarker, S13T5SavedMarker).ConfigureAwait(false);
            using (var saveReply = JsonDocument.Parse(await browser.EvaluateStringAsync("window.__s13t5SaveReply").ConfigureAwait(false)))
            {
                Require(saveReply.RootElement.GetProperty("status").GetString() == "saved",
                    "The in-flight save did not report the committed revision before lock revoked its browser reply.");
            }
            await StopOpenProcessAsync(openProcess).ConfigureAwait(false);
            openProcess = null;
            await UnlockWorkspaceAsync(executable, fixture, passphrase).ConfigureAwait(false);
            var expectedSavedBytes = Encoding.UTF8.GetBytes(sourceText + "\n" + S13T5SavedMarker);
            await RequireWorkspaceBytesAsync(executable, fixture, imported.FileId, expectedSavedBytes,
                "s13t5-save-race-readback.txt").ConfigureAwait(false);
            Console.WriteLine("PASS lock closes the real bridge during an in-flight save, clears an unsaved draft without prompting, drops its delayed reply, and retains the saved revision");

            await browser.NavigateAsync("about:blank").ConfigureAwait(false);
            await browser.EvaluateAsync("history.back()").ConfigureAwait(false);
            await WaitForBrowserBackAsync(browser).ConfigureAwait(false);
            await RequireBrowserClearedAsync(browser, S13T5DocumentMarker, S13T5SavedMarker).ConfigureAwait(false);
            await browser.EvaluateAsync("window.__s13t5ReloadMarker = 'present'").ConfigureAwait(false);
            await browser.ReloadAsync().ConfigureAwait(false);
            await browser.WaitForAsync("window.__s13t5ReloadMarker || 'cleared'",
                value => value == "cleared", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await RequireBrowserClearedAsync(browser, S13T5DocumentMarker, S13T5SavedMarker).ConfigureAwait(false);
            var oldShell = await S13T1RequestOrNullAsync(opened.Url).ConfigureAwait(false);
            Require(oldShell is null, "A closed document bridge still served its shell after lock.");
            // The previous session is process-local and its listener is gone; it
            // remains unusable after a new unlock even though its file still exists.
            S13T1Response? staleRead = null;
            try
            {
                staleRead = await S13T1RequestAsync(opened.Url + "api/document", opened.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
            }
            Require(staleRead is null, "Browser back or a post-unlock request revived the locked bridge session.");
            var promptSession = await S13T1StartOpenAsync(executable, fixture, imported.FileId, lifetimeSeconds: 240).ConfigureAwait(false);
            openProcess = promptSession.Process;
            await browser.NavigateAsync(promptSession.Url).ConfigureAwait(false);
            await browser.WaitForAsync("document.getElementById('status')?.textContent || ''",
                value => value.Contains("Preview of the bound revision", StringComparison.Ordinal) ||
                    value.Contains("Bound source page.", StringComparison.Ordinal), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            await browser.EvaluateAsync("document.getElementById('edit').click()").ConfigureAwait(false);
            await browser.WaitForAsync("document.getElementById('editor').hidden ? 'hidden' : 'visible'",
                value => value == "visible", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            await browser.EvaluateAsync("document.getElementById('editor').value += " +
                JsonSerializer.Serialize("\n" + S13T5PromptMarker)).ConfigureAwait(false);
            await browser.EvaluateAsync("document.getElementById('cancel-edit').click()").ConfigureAwait(false);
            await browser.WaitForAsync("document.getElementById('discard-confirm').hidden ? 'hidden' : 'visible'",
                value => value == "visible", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var promptLock = await RunOwnerCommandAsync(
                executable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(promptLock.ExitCode == 0,
                "The active unsaved-draft discard prompt blocked the owner lock.");
            await WaitForBrowserRevocationAsync(browser, S13T5DocumentMarker, S13T5PromptMarker).ConfigureAwait(false);
            await StopOpenProcessAsync(openProcess).ConfigureAwait(false);
            openProcess = null;
            await UnlockWorkspaceAsync(executable, fixture, passphrase).ConfigureAwait(false);
            await RequireWorkspaceBytesAsync(executable, fixture, imported.FileId, expectedSavedBytes,
                "s13t5-prompt-lock-readback.txt").ConfigureAwait(false);
            Console.WriteLine("PASS an open unsaved-draft prompt does not block owner lock and its browser content is cleared");

            await browser.NavigateAsync("about:blank").ConfigureAwait(false);
            await browser.EvaluateAsync("history.back()").ConfigureAwait(false);
            await WaitForBrowserBackAsync(browser).ConfigureAwait(false);
            await RequireBrowserClearedAsync(browser, S13T5DocumentMarker, S13T5PromptMarker).ConfigureAwait(false);


            var reopened = await S13T1StartOpenAsync(executable, fixture, imported.FileId, lifetimeSeconds: 240).ConfigureAwait(false);
            openProcess = reopened.Process;
            await browser.NavigateAsync(reopened.Url).ConfigureAwait(false);
            await browser.WaitForAsync("document.getElementById('status')?.textContent || ''",
                value => value.Contains("Preview of the bound revision", StringComparison.Ordinal) ||
                    value.Contains("Bound source page.", StringComparison.Ordinal), TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            await browser.EvaluateAsync("""
                (() => {
                    const original = window.fetch.bind(window);
                    window.__s13t5ReadWaiting = false;
                    window.__s13t5ReadArm = true;
                    window.__s13t5ReleaseRead = null;
                    window.fetch = async (input, init) => {
                        const response = await original(input, init);
                        if (window.__s13t5ReadArm && String(input).endsWith('/api/document/page')) {
                            window.__s13t5ReadArm = false;
                            const payload = await response.text();
                            window.__s13t5ReadWaiting = true;
                            await new Promise(resolve => window.__s13t5ReleaseRead = resolve);
                            return new Response(payload, { status: response.status, headers: { 'Content-Type': 'application/json' } });
                        }
                        return response;
                    };
                    document.getElementById('next').click();
                    return true;
                })()
                """).ConfigureAwait(false);
            await browser.WaitForAsync("String(window.__s13t5ReadWaiting)",
                value => value == "true", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            var readLock = await RunOwnerCommandAsync(executable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(readLock.ExitCode == 0, "The owner vault did not lock during the pending browser read.");
            await WaitForBrowserRevocationAsync(browser, S13T5DocumentMarker, S13T5LaterPageMarker).ConfigureAwait(false);
            await browser.EvaluateAsync("window.__s13t5ReleaseRead && window.__s13t5ReleaseRead()").ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false);
            await RequireBrowserClearedAsync(browser, S13T5DocumentMarker, S13T5LaterPageMarker).ConfigureAwait(false);
            await StopOpenProcessAsync(openProcess).ConfigureAwait(false);
            openProcess = null;
            await UnlockWorkspaceAsync(executable, fixture, passphrase).ConfigureAwait(false);
            await RequireWorkspaceBytesAsync(executable, fixture, imported.FileId, expectedSavedBytes,
                "s13t5-read-race-readback.txt").ConfigureAwait(false);
            Console.WriteLine("PASS lock cancels a pending bounded page read and an intentionally delayed stale page response cannot repopulate the browser");

            var disconnected = await S13T1StartOpenAsync(executable, fixture, imported.FileId, lifetimeSeconds: 240).ConfigureAwait(false);
            openProcess = disconnected.Process;
            await browser.NavigateAsync(disconnected.Url).ConfigureAwait(false);
            await browser.WaitForAsync("document.getElementById('status')?.textContent || ''",
                value => value.Contains("Preview of the bound revision", StringComparison.Ordinal) ||
                    value.Contains("Bound source page.", StringComparison.Ordinal), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            var stopping = service ?? throw new InvalidOperationException("The S13-T5 service is not running.");
            await StopServiceAsync(stopping).ConfigureAwait(false);
            service = null;
            await WaitForBrowserRevocationAsync(browser, S13T5DocumentMarker, S13T5SavedMarker).ConfigureAwait(false);
            await StopOpenProcessAsync(openProcess).ConfigureAwait(false);
            openProcess = null;

            service = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            var restartedStatus = await RunOwnerCommandAsync(executable,
                "owner", "status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(restartedStatus.ExitCode == 0 && restartedStatus.StandardOutput.Contains("vault=locked", StringComparison.Ordinal),
                "The shared service did not restart locked after the active document bridge disconnected.");
            await UnlockWorkspaceAsync(executable, fixture, passphrase).ConfigureAwait(false);
            staleRead = null;
            try
            {
                staleRead = await S13T1RequestAsync(disconnected.Url + "api/document", disconnected.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
            }
            Require(staleRead is null, "A service restart and new unlock revived the old document bridge capability.");
            await RequireWorkspaceBytesAsync(executable, fixture, imported.FileId, expectedSavedBytes,
                "s13t5-restart-readback.txt").ConfigureAwait(false);
            await browser.NavigateAsync("about:blank").ConfigureAwait(false);
            await browser.EvaluateAsync("history.back()").ConfigureAwait(false);
            await WaitForBrowserBackAsync(browser).ConfigureAwait(false);
            await RequireBrowserClearedAsync(browser, S13T5DocumentMarker, S13T5SavedMarker).ConfigureAwait(false);
            Console.WriteLine("PASS service disconnect/restart starts locked; unlock does not revive the prior bridge or browser back entry, while the saved revision survives");
            var forcedExit = await S13T1StartOpenAsync(executable, fixture, imported.FileId, lifetimeSeconds: 240).ConfigureAwait(false);
            openProcess = forcedExit.Process;
            await browser.NavigateAsync(forcedExit.Url).ConfigureAwait(false);
            await browser.WaitForAsync("document.getElementById('status')?.textContent || ''",
                value => value.Contains("Preview of the bound revision", StringComparison.Ordinal) ||
                    value.Contains("Bound source page.", StringComparison.Ordinal), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            await browser.EvaluateAsync("document.getElementById('edit').click()").ConfigureAwait(false);
            await browser.WaitForAsync("document.getElementById('editor').hidden ? 'hidden' : 'visible'",
                value => value == "visible", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            await browser.EvaluateAsync("document.getElementById('editor').value += " +
                JsonSerializer.Serialize("\n" + S13T5ExitMarker)).ConfigureAwait(false);
            var exitedProcess = openProcess;
            exitedProcess.Kill(entireProcessTree: true);
            await exitedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            await WaitForBrowserRevocationAsync(browser, S13T5DocumentMarker, S13T5ExitMarker).ConfigureAwait(false);
            var exitedShell = await S13T1RequestOrNullAsync(forcedExit.Url).ConfigureAwait(false);
            Require(exitedShell is null, "The foreground CLI still served the document shell after process exit.");
            staleRead = null;
            try
            {
                staleRead = await S13T1RequestAsync(forcedExit.Url + "api/document", forcedExit.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
            }
            Require(staleRead is null, "The foreground CLI still served document bytes after process exit.");
            await StopOpenProcessAsync(exitedProcess).ConfigureAwait(false);
            openProcess = null;
            await browser.NavigateAsync("about:blank").ConfigureAwait(false);
            await browser.EvaluateAsync("history.back()").ConfigureAwait(false);
            await WaitForBrowserBackAsync(browser).ConfigureAwait(false);
            await RequireBrowserClearedAsync(browser, S13T5DocumentMarker, S13T5ExitMarker).ConfigureAwait(false);
            await RequireWorkspaceBytesAsync(executable, fixture, imported.FileId, expectedSavedBytes,
                "s13t5-cli-exit-readback.txt").ConfigureAwait(false);
            Console.WriteLine("PASS foreground CLI process exit revokes read/save authority, clears an unsaved editor and back cannot restore its content");


            var expiring = await S13T1StartOpenAsync(executable, fixture, imported.FileId, lifetimeSeconds: 4).ConfigureAwait(false);
            openProcess = expiring.Process;
            await browser.NavigateAsync(expiring.Url).ConfigureAwait(false);
            await browser.WaitForAsync("document.getElementById('status')?.textContent || ''",
                value => value.Contains("Preview of the bound revision", StringComparison.Ordinal) ||
                    value.Contains("Bound source page.", StringComparison.Ordinal), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            await browser.EvaluateAsync("document.getElementById('edit').click()").ConfigureAwait(false);
            await browser.WaitForAsync("document.getElementById('editor').hidden ? 'hidden' : 'visible'",
                value => value == "visible", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            await browser.EvaluateAsync("document.getElementById('editor').value += " + JsonSerializer.Serialize("\n" + S13T5DraftMarker)).ConfigureAwait(false);
            await openProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            var expiryOutput = await openProcess.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            Require(openProcess.ExitCode == 0 && expiryOutput.Contains("OPEN expired", StringComparison.Ordinal),
                "The explicit CLI session lifetime did not terminate the actual foreground bridge.");
            await WaitForBrowserRevocationAsync(browser, S13T5DocumentMarker, S13T5DraftMarker).ConfigureAwait(false);
            await StopOpenProcessAsync(openProcess).ConfigureAwait(false);
            openProcess = null;
            await browser.NavigateAsync("about:blank").ConfigureAwait(false);
            await browser.EvaluateAsync("history.back()").ConfigureAwait(false);
            await WaitForBrowserBackAsync(browser).ConfigureAwait(false);
            await RequireBrowserClearedAsync(browser, S13T5DocumentMarker, S13T5DraftMarker).ConfigureAwait(false);
            S13T1Response? expiredRead = null;
            try
            {
                expiredRead = await S13T1RequestAsync(expiring.Url + "api/document", expiring.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
            }
            Require(expiredRead is null, "The expired foreground CLI bridge still served the document.");
            Console.WriteLine("PASS CLI lifetime exit clears a dirty real-browser editor and reload/back cannot restore app-cached content or capability");
        }
        finally
        {
            if (browser is not null)
            {
                await browser.DisposeAsync().ConfigureAwait(false);
            }

            S13T1TryDeleteDirectory(profile);
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

    private static async Task WaitForBrowserRevocationAsync(S13T1Browser browser, string documentMarker, string draftMarker)
    {
        var status = await browser.WaitForAsync("document.getElementById('status')?.textContent || ''",
            value => value.Contains("session ended", StringComparison.Ordinal) ||
                value.Contains("no longer reachable", StringComparison.Ordinal),
            TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        Require(status.Contains("session ended", StringComparison.Ordinal) ||
                status.Contains("no longer reachable", StringComparison.Ordinal),
            "The browser did not observe session revocation.");
        await RequireBrowserClearedAsync(browser, documentMarker, draftMarker).ConfigureAwait(false);
    }

    private static async Task RequireBrowserClearedAsync(S13T1Browser browser, string documentMarker, string draftMarker)
    {
        var snapshotText = await browser.EvaluateStringAsync("JSON.stringify({" +
            "body: document.body?.innerText || '', " +
            "preview: document.getElementById('preview-view')?.textContent || '', " +
            "editor: document.getElementById('editor')?.value || '', " +
            "name: document.getElementById('name')?.textContent || '', " +
            "token: Boolean(document.querySelector('meta[name=mb-session]'))})").ConfigureAwait(false);
        using var snapshot = JsonDocument.Parse(snapshotText);
        var root = snapshot.RootElement;
        var body = root.GetProperty("body").GetString() ?? string.Empty;
        var preview = root.GetProperty("preview").GetString() ?? string.Empty;
        var editor = root.GetProperty("editor").GetString() ?? string.Empty;
        var name = root.GetProperty("name").GetString() ?? string.Empty;
        Require(!body.Contains(documentMarker, StringComparison.Ordinal) &&
                !body.Contains(draftMarker, StringComparison.Ordinal) &&
                preview.Length == 0 && editor.Length == 0 && name.Length == 0 &&
                !root.GetProperty("token").GetBoolean(),
            "A revoked or restored browser page retained document text, draft text or its capability.");
    }

    private static async Task WaitForBrowserBackAsync(S13T1Browser browser)
    {
        await browser.WaitForAsync("document.body ? document.body.innerText : ''",
            value => value.Length > 0, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadBrowserStorageAsync(S13T1Browser browser)
    {
        var value = await browser.EvaluateStringAsync("(async () => JSON.stringify({" +
            "localStorage: localStorage.length, sessionStorage: sessionStorage.length, " +
            "cookies: document.cookie.length, cacheStorage: (await caches.keys()).length, " +
            "indexedDb: (await indexedDB.databases()).length, " +
            "serviceWorkers: (await navigator.serviceWorker.getRegistrations()).length}))()")
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static async Task RequireWorkspaceBytesAsync(
        string executable,
        SmokeFixture fixture,
        string fileId,
        byte[] expected,
        string outputName)
    {
        var path = Path.Combine(fixture.OutputDirectory, outputName);
        var actual = await ReadWorkspaceFileAsync(executable, fixture, fileId, null, outputName).ConfigureAwait(false);
        try
        {
            RequireBytes(actual, expected, "persisted saved workspace revision");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task StopOpenProcessAsync(Process process)
    {
        if (!process.HasExited)
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }

        await S13T1StopOpenAsync(process).ConfigureAwait(false);
    }

    private static async Task<IndexResourceObservation> RunMeasuredIndexRebuildAsync(
        string executable,
        string controlPipe,
        int serviceProcessId,
        int bridgeProcessId,
        int browserProcessId)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "owner", "index-rebuild", "--control-pipe", controlPipe })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        Require(process.Start(), "The actual owner CLI did not start the synthetic index rebuild.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var indexCli = new ProcessMemoryPeak();
        var bridgeCli = new ProcessMemoryPeak();
        var service = new ProcessMemoryPeak();
        var browser = new ProcessMemoryPeak();
        var timer = Stopwatch.StartNew();

        void Sample()
        {
            indexCli.Observe(process.Id);
            bridgeCli.Observe(bridgeProcessId);
            service.Observe(serviceProcessId);
            browser.Observe(browserProcessId);
        }

        Sample();
        try
        {
            while (!process.HasExited)
            {
                Sample();
                if (timer.Elapsed > TimeSpan.FromMinutes(2))
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                    throw new TimeoutException("The synthetic index rebuild exceeded its smoke safety timeout.");
                }

                await Task.Delay(15).ConfigureAwait(false);
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            timer.Stop();
            return new IndexResourceObservation(
                process.ExitCode,
                await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false),
                timer.ElapsedMilliseconds,
                indexCli,
                bridgeCli,
                service,
                browser);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async Task<VaultFileObservation> ObserveVaultFilesAsync(string root, string marker)
    {
        var markerBytes = Encoding.UTF8.GetBytes(marker);
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        long totalBytes = 0;
        var containsMarker = false;
        foreach (var path in files)
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            try
            {
                totalBytes += bytes.LongLength;
                containsMarker |= bytes.AsSpan().IndexOf(markerBytes) >= 0;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        CryptographicOperations.ZeroMemory(markerBytes);
        return new VaultFileObservation(files.Length, totalBytes, containsMarker);
    }

    private static ProfileFootprint ObserveDirectoryFootprint(string path)
    {
        var files = Directory.Exists(path)
            ? Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
        long totalBytes = 0;
        foreach (var file in files)
        {
            try
            {
                totalBytes += new FileInfo(file).Length;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return new ProfileFootprint(files.Length, totalBytes);
    }

    private sealed record IndexResourceObservation(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        long ElapsedMilliseconds,
        ProcessMemoryPeak IndexCli,
        ProcessMemoryPeak BridgeCli,
        ProcessMemoryPeak Service,
        ProcessMemoryPeak Browser);

    private sealed record VaultFileObservation(int FileCount, long TotalBytes, bool ContainsPlaintextMarker);

    private sealed record ProfileFootprint(int FileCount, long TotalBytes);

    private sealed class ProcessMemoryPeak
    {
        public long WorkingSetPeakBytes { get; private set; }
        public long PrivatePeakBytes { get; private set; }
        public int Samples { get; private set; }

        public void Observe(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Refresh();
                if (process.HasExited)
                {
                    return;
                }

                WorkingSetPeakBytes = Math.Max(WorkingSetPeakBytes, process.WorkingSet64);
                PrivatePeakBytes = Math.Max(PrivatePeakBytes, process.PrivateMemorySize64);
                Samples++;
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }
        }
    }
}
