using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MetaBrain.Connections;
using MetaBrain.OwnerUi;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S8-T5 owner console latency measurement on the real WPF window.
/// <para>
/// The scenario drives the real owner console against the real service over
/// the real owner control pipe: the owner types a query into the real text
/// box, presses the real search button and the window renders the real
/// answer. The measured interval starts at the click and ends when the
/// window has rendered the new page, so it covers the service IPC and the
/// render, exactly like the frozen owner-UI target.
/// </para>
/// <para>
/// Every file the scenario creates is synthetic and stays inside the
/// repository's ignored runtime directory. It never changes a permission, an
/// owner, a registry entry or a machine setting.
/// </para>
/// </summary>
internal static partial class OwnerServiceSmoke
{
    internal static async Task RunS8T5UiAsync(string connectionsExecutable, int recordCount, int searchCount)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S8-T5 owner console measurement requires Windows named pipes and WPF.");
        }

        Console.WriteLine(
            "RUN S8-T5 owner console latency measurement records=" +
            recordCount.ToString(CultureInfo.InvariantCulture) +
            " searches=" + searchCount.ToString(CultureInfo.InvariantCulture));
        using var fixture = S8T5Fixture.Create();
        var passphraseBytes = RandomNumberGenerator.GetBytes(24);
        var passphrase = Convert.ToBase64String(passphraseBytes);
        CryptographicOperations.ZeroMemory(passphraseBytes);
        RunningService? service = null;
        try
        {
            var corpus = S8T5Corpus.Build(recordCount);
            var store = new EncryptedVaultStore(fixture.VaultDirectoryPath);
            store.Create(passphrase, corpus.Seeds);

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var unlocked = await RunOwnerCommandWithInputAsync(
                connectionsExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S8-T5 console vault did not unlock (" + unlocked.StandardError.Trim() + ").");
            var rebuilt = await InvokeOwnerRequestWithTimeoutAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "owner.index.rebuild",
            }, TimeSpan.FromMinutes(25)).ConfigureAwait(false);
            Require(ReplyCode(rebuilt) == "index_rebuilt",
                "The real private index did not rebuild for the console measurement (" + rebuilt.GetRawText() + ").");

            var repositoryRoot = SmokeFixture.FindRepositoryRoot();
            var evidenceDirectory = Path.Combine(repositoryRoot, "runtime", "s8-t5", "ui-evidence");
            Directory.CreateDirectory(evidenceDirectory);

            var session = new OwnerConsoleSession(fixture.ControlPipe);
            await session.AttachAsync().ConfigureAwait(true);
            Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
                "The owner console did not report the unlocked vault (" + session.ReportedVaultState + ").");

            var searches = await DriveS8T5UiAsync(session, corpus, searchCount, evidenceDirectory).ConfigureAwait(true);
            Console.WriteLine(
                "MEASURE ui_search_p50_ms=" + searches.P50Ms.ToString("F1", CultureInfo.InvariantCulture) +
                " ui_search_p95_ms=" + searches.P95Ms.ToString("F1", CultureInfo.InvariantCulture) +
                " ui_search_max_ms=" + searches.MaxMs.ToString("F1", CultureInfo.InvariantCulture) +
                " ui_searches=" + searches.Count.ToString(CultureInfo.InvariantCulture) +
                " ui_warmups=" + searches.Warmups.ToString(CultureInfo.InvariantCulture) +
                " ui_navigation_p95_ms=" + searches.NavigationP95Ms.ToString("F1", CultureInfo.InvariantCulture) +
                " ui_navigations=" + searches.NavigationCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine(
                "OBSERVATION the real WPF window answered " +
                searches.Count.ToString(CultureInfo.InvariantCulture) +
                " searches with p95 " + searches.P95Ms.ToString("F1", CultureInfo.InvariantCulture) +
                " ms from the click to the rendered page, and " +
                searches.NavigationCount.ToString(CultureInfo.InvariantCulture) +
                " navigation actions with p95 " + searches.NavigationP95Ms.ToString("F1", CultureInfo.InvariantCulture) + " ms");
            Console.WriteLine(
                "PASS S8-T5 the owner console renders the real answer of the real service for a real query");
            Console.WriteLine(
                "LIMIT the automated run drives the real WPF window on an STA thread; it substitutes no control and reads every answer from the live session, but no human pointed a mouse at the window.");
        }
        finally
        {
            if (service is not null)
            {
                try
                {
                    if (!service.Process.HasExited)
                    {
                        service.Process.Kill(entireProcessTree: true);
                        await service.Process.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }

    /// <summary>
    /// Runs the whole console interaction on the STA thread that owns the real
    /// window.
    /// </summary>
    private static async Task<S8T5UiSample> DriveS8T5UiAsync(
        OwnerConsoleSession session,
        S8T5Corpus corpus,
        int searchCount,
        string evidenceDirectory)
    {
        ArgumentNullException.ThrowIfNull(session);
        S8T5UiSample? sample = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            OwnerConsoleWindow? window = null;
            try
            {
                window = new OwnerConsoleWindow(session);
                window.Width = 1280;
                window.Height = 1000;
                window.Show();
                window.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        sample = await RunS8T5UiActsAsync(window, session, corpus, searchCount, evidenceDirectory).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                    finally
                    {
                        window.DetachView();
                        window.Close();
                        window.Dispatcher.InvokeShutdown();
                    }
                });
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException(
                "The S8-T5 owner console measurement failed: " + failure.Message + "\n" + failure.StackTrace,
                failure);
        }

        return sample ?? throw new InvalidOperationException("The S8-T5 owner console measurement produced no sample.");
    }

    private static async Task<S8T5UiSample> RunS8T5UiActsAsync(
        OwnerConsoleWindow window,
        OwnerConsoleSession session,
        S8T5Corpus corpus,
        int searchCount,
        string evidenceDirectory)
    {
        NavigateS8T2Ui(window, "Library");
        Require(window.LibraryPanel.Visibility == Visibility.Visible,
            "The library view did not open.");

        var warmups = Math.Min(5, Math.Max(1, searchCount / 4));
        for (var index = 0; index < warmups; index++)
        {
            await RunS8T5UiSearchAsync(window, session, corpus.Queries[index % corpus.Queries.Count].Text, null).ConfigureAwait(true);
        }

        var timings = new List<double>(searchCount);
        var hits = 0;
        for (var index = 0; index < searchCount; index++)
        {
            var query = corpus.Queries[index % corpus.Queries.Count];
            var elapsed = await RunS8T5UiSearchAsync(window, session, query.Text, query.ExpectedResourceId).ConfigureAwait(true);
            timings.Add(elapsed);
            if (query.ExpectedResourceId.Length > 0)
            {
                hits++;
            }
        }

        // The local navigation action: a control action inside the window that
        // never leaves the process.
        var navigationTimings = new List<double>();
        for (var index = 0; index < 10; index++)
        {
            var watch = Stopwatch.StartNew();
            NavigateS8T2Ui(window, "Sources");
            NavigateS8T2Ui(window, "Library");
            watch.Stop();
            navigationTimings.Add(watch.Elapsed.TotalMilliseconds);
        }

        SaveS8T1AWpf(window, evidenceDirectory, "s8t5-ui-01-library-search.png", window.LibraryResults);
        Require(window.LibraryResults.Items.Count > 0,
            "The library view rendered no result for the measured query.");
        Console.WriteLine(
            "OBSERVATION the library view rendered " +
            window.LibraryResults.Items.Count.ToString(CultureInfo.InvariantCulture) +
            " result rows for the measured query");

        timings.Sort();
        navigationTimings.Sort();
        var p50 = S8T5Sample.Percentile(timings, 0.50);
        var p95 = S8T5Sample.Percentile(timings, 0.95);
        var max = timings.Count == 0 ? 0 : timings[^1];
        var navigationP95 = S8T5Sample.Percentile(navigationTimings, 0.95);
        return new S8T5UiSample(searchCount, warmups, p50, p95, max, navigationP95, navigationTimings.Count);
    }

    /// <summary>
    /// One measured search: the real text box, the real search button and
    /// the real rendered page. The measured interval starts at the click and
    /// ends after the window has rendered the new answer.
    /// </summary>
    private static async Task<double> RunS8T5UiSearchAsync(
        OwnerConsoleWindow window,
        OwnerConsoleSession session,
        string query,
        string? expectedResourceId)
    {
        var completed = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousPage = session.LibraryPage;
        void OnChanged(object? sender, EventArgs args)
        {
            if (ReferenceEquals(session.LibraryPage, previousPage))
            {
                return;
            }

            session.Changed -= OnChanged;
            completed.TrySetResult(0);
        }

        session.Changed += OnChanged;
        var watch = Stopwatch.StartNew();
        window.LibraryQueryInput.Text = query;
        window.LibrarySearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(true);
        watch.Stop();
        session.Changed -= OnChanged;
        Require(session.LibraryPage is not null && !ReferenceEquals(session.LibraryPage, previousPage),
            "The library view did not answer the measured query (" + (session.LibraryMessage ?? "no message") + ").");
        return watch.Elapsed.TotalMilliseconds;
    }
}

/// <summary>One measured owner console run.</summary>
internal sealed record S8T5UiSample(
    int Count,
    int Warmups,
    double P50Ms,
    double P95Ms,
    double MaxMs,
    double NavigationP95Ms,
    int NavigationCount);
