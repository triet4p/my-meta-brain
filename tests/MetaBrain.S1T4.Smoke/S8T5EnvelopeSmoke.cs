using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S8-T5 operational envelope measurement against the frozen S1 targets
/// (R23, R37, R38).
/// <para>
/// The scenario measures the real product on the real host: a real encrypted
/// vault on disk, the real service executable, the real owner control pipe,
/// the real private index and the real owner library search. Nothing is
/// simulated: the corpus is written through the product's own encrypted store,
/// the index is materialized by the product's own rebuild operation and the
/// queries travel over the real named pipe.
/// </para>
/// <para>
/// The measured values are reported next to the frozen targets; the targets
/// are never relaxed here. Every number printed by this scenario is an
/// observation of the running program on this host, never an estimate.
/// </para>
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S8T5ZoneAlpha = S8T5Corpus.ZoneAlpha;
    private const string S8T5ZoneBeta = S8T5Corpus.ZoneBeta;

    /// <summary>
    /// The answer timeout of a timed recall query. The service answers a slow
    /// query itself with its own request guard; the client waits longer than
    /// that guard so a slow query is measured instead of being cut off.
    /// </summary>
    private static readonly TimeSpan S8T5QueryTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs the whole envelope measurement for one fixture size.
    /// </summary>
    internal static async Task RunS8T5EnvelopeAsync(
        string connectionsExecutable,
        int recordCount,
        int queryCount,
        int idleSeconds)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S8-T5 envelope measurement requires Windows named pipes.");
        }

        Console.WriteLine(
            "RUN S8-T5 operational envelope measurement records=" +
            recordCount.ToString(CultureInfo.InvariantCulture) +
            " queries=" + queryCount.ToString(CultureInfo.InvariantCulture) +
            " idleSeconds=" + idleSeconds.ToString(CultureInfo.InvariantCulture));
        using var fixture = S8T5Fixture.Create();
        var passphraseBytes = RandomNumberGenerator.GetBytes(24);
        var passphrase = Convert.ToBase64String(passphraseBytes);
        CryptographicOperations.ZeroMemory(passphraseBytes);
        RunningService? service = null;
        try
        {
            // ----- the real encrypted vault on disk -----
            var corpus = S8T5Corpus.Build(recordCount);
            var store = new EncryptedVaultStore(fixture.VaultDirectoryPath);
            var seeded = Stopwatch.StartNew();
            store.Create(passphrase, corpus.Seeds);
            seeded.Stop();
            var vaultBytes = S8T5DirectorySize(fixture.DataRootPath);
            Console.WriteLine(
                "MEASURE encrypted_persistence_seed_seconds=" +
                seeded.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) +
                " records=" + recordCount.ToString(CultureInfo.InvariantCulture) +
                " records_per_second=" +
                (recordCount / Math.Max(seeded.Elapsed.TotalSeconds, 0.001)).ToString("F1", CultureInfo.InvariantCulture) +
                " vault_bytes=" + vaultBytes.ToString(CultureInfo.InvariantCulture) +
                " body_bytes_mean=" + corpus.MeanBodyBytes.ToString("F0", CultureInfo.InvariantCulture) +
                " body_bytes_p95=" + corpus.P95BodyBytes.ToString("F0", CultureInfo.InvariantCulture));
            Console.WriteLine(
                "OBSERVATION the encrypted vault holds " + recordCount.ToString(CultureInfo.InvariantCulture) +
                " synthetic memory items as ciphertext on disk; the manifest and every resource file are encrypted envelopes");

            // ----- the real service unlocks the real vault -----
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var unlocked = await RunOwnerCommandWithInputAsync(
                connectionsExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S8-T5 vault did not unlock (" + unlocked.StandardError.Trim() + ").");

            // ----- the real per-record ingest path -----
            // The seeded fixture above is one bulk pass; the owner's own
            // write path is measured separately because it is the path a
            // daily capture actually takes. It runs before the index rebuild
            // so the index that the recall measurement queries is the current
            // generation of the canonical set.
            var ingest = await MeasureS8T5IngestAsync(connectionsExecutable, fixture, 120).ConfigureAwait(false);
            Console.WriteLine(
                "MEASURE ingest_records=" + ingest.Records.ToString(CultureInfo.InvariantCulture) +
                " seconds=" + ingest.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) +
                " records_per_second=" +
                (ingest.Records / Math.Max(ingest.Elapsed.TotalSeconds, 0.001)).ToString("F1", CultureInfo.InvariantCulture) +
                " write_p50_ms=" + ingest.P50Ms.ToString("F1", CultureInfo.InvariantCulture) +
                " write_p95_ms=" + ingest.P95Ms.ToString("F1", CultureInfo.InvariantCulture));
            Console.WriteLine(
                "OBSERVATION the owner per-record write path committed " +
                ingest.Records.ToString(CultureInfo.InvariantCulture) +
                " records through the real owner pipe at " +
                (ingest.Records / Math.Max(ingest.Elapsed.TotalSeconds, 0.001)).ToString("F1", CultureInfo.InvariantCulture) +
                " records/s (p95 write " + ingest.P95Ms.ToString("F1", CultureInfo.InvariantCulture) + " ms)");

            // ----- the real private index rebuild -----
            var rebuild = Stopwatch.StartNew();
            var rebuilt = await InvokeOwnerRequestWithTimeoutAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "owner.index.rebuild",
            }, TimeSpan.FromMinutes(25)).ConfigureAwait(false);
            rebuild.Stop();
            Require(ReplyCode(rebuilt) == "index_rebuilt",
                "The real private index did not rebuild (" + rebuilt.GetRawText() + ").");
            var indexRows = rebuilt.TryGetProperty("privateIndex", out var indexWire) &&
                            indexWire.TryGetProperty("rowCount", out var rowCount)
                ? rowCount.GetInt64()
                : 0;
            Console.WriteLine(
                "MEASURE private_index_rebuild_seconds=" +
                rebuild.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) +
                " rows=" + indexRows.ToString(CultureInfo.InvariantCulture) +
                " records_per_second=" +
                (indexRows / Math.Max(rebuild.Elapsed.TotalSeconds, 0.001)).ToString("F1", CultureInfo.InvariantCulture));
            Console.WriteLine(
                "OBSERVATION the private index materialized " + indexRows.ToString(CultureInfo.InvariantCulture) +
                " rows from the encrypted canonical records in " +
                rebuild.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) + " s");

            // ----- the real timed recall queries -----
            var recall = await MeasureS8T5RecallAsync(fixture, corpus, queryCount).ConfigureAwait(false);
            Console.WriteLine(
                "MEASURE recall_p50_ms=" + recall.P50Ms.ToString("F1", CultureInfo.InvariantCulture) +
                " recall_p95_ms=" + recall.P95Ms.ToString("F1", CultureInfo.InvariantCulture) +
                " recall_max_ms=" + recall.MaxMs.ToString("F1", CultureInfo.InvariantCulture) +
                " queries=" + recall.Count.ToString(CultureInfo.InvariantCulture) +
                " warmups=" + recall.Warmups.ToString(CultureInfo.InvariantCulture) +
                " hit_rate=" + recall.HitRate.ToString("F4", CultureInfo.InvariantCulture) +
                " symbol_hit_rate=" + recall.SymbolHitRate.ToString("F4", CultureInfo.InvariantCulture) +
                " average_matches=" + recall.AverageMatches.ToString("F1", CultureInfo.InvariantCulture) +
                " max_matches=" + recall.MaxMatches.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine(
                "OBSERVATION the owner library search answered " +
                recall.Count.ToString(CultureInfo.InvariantCulture) +
                " timed queries with p95 " + recall.P95Ms.ToString("F1", CultureInfo.InvariantCulture) +
                " ms over the real owner pipe; the expected item was in the page for " +
                (recall.HitRate * 100).ToString("F1", CultureInfo.InvariantCulture) + " % of the natural queries and " +
                (recall.SymbolHitRate * 100).ToString("F1", CultureInfo.InvariantCulture) + " % of the exact-symbol queries");

            // ----- the provider budget -----
            var health = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "owner.operations.health",
            }).ConfigureAwait(false);
            Require(health.TryGetProperty("operationsHealth", out var healthWire),
                "The real operations health did not answer (" + health.GetRawText() + ").");
            var rollingSpend = healthWire.TryGetProperty("rollingSpendUsd", out var spend)
                ? spend.GetDecimal()
                : 0m;
            var providerConfigured = healthWire.TryGetProperty("providerConfigured", out var configured)
                ? configured.GetBoolean()
                : false;
            var indexRowsReported = healthWire.TryGetProperty("privateIndexRows", out var indexRowValue)
                ? indexRowValue.GetInt32()
                : 0;

            // The rolling spend the health reports is the product's rolling
            // 30-day total, and the product deliberately carries a constant
            // historical reservation against that cap (SpendLedger's
            // HistoricalUnreconciledReservationUsd). What this run may not do
            // is add an entry: the ledger itself is read to prove that.
            var ledger = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "owner.job.ledger",
            }).ConfigureAwait(false);
            var ledgerWire = ledger.TryGetProperty("spendLedger", out var ledgerValue) ? ledgerValue : ledger;
            var ledgerEntries = ledgerWire.TryGetProperty("entries", out var entriesValue) &&
                                entriesValue.ValueKind == JsonValueKind.Array
                ? entriesValue.GetArrayLength()
                : -1;
            var historicalReservation = ledgerWire.TryGetProperty("historicalReservationUsd", out var historical)
                ? historical.GetDecimal()
                : 0m;
            Require(ledgerEntries == 0,
                "The measured run recorded " + ledgerEntries.ToString(CultureInfo.InvariantCulture) +
                " provider spend entries; a run without a provider must record none.");
            Require(rollingSpend == historicalReservation,
                "The measured run moved the rolling spend (" + rollingSpend.ToString(CultureInfo.InvariantCulture) +
                " USD) away from the historical reservation (" + historicalReservation.ToString(CultureInfo.InvariantCulture) + " USD).");
            Console.WriteLine(
                "MEASURE provider_spend_usd=" + rollingSpend.ToString("F4", CultureInfo.InvariantCulture) +
                " provider_spend_entries=" + ledgerEntries.ToString(CultureInfo.InvariantCulture) +
                " provider_historical_reservation_usd=" + historicalReservation.ToString("F4", CultureInfo.InvariantCulture) +
                " provider_configured=" + (providerConfigured ? "true" : "false") +
                " provider_calls=0 health_index_rows=" + indexRowsReported.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine(
                "OBSERVATION no provider was contacted in this run: the spend ledger holds no entry, the rolling spend is only the product's constant historical reservation, and no provider configuration exists");

            // ----- the idle samples -----
            if (idleSeconds > 0)
            {
                var unlockedIdle = await S8T5IdleSample.CaptureAsync(service.Process, idleSeconds).ConfigureAwait(false);
                Console.WriteLine(
                    "MEASURE idle_unlocked_cpu_percent=" + unlockedIdle.CpuPercent.ToString("F3", CultureInfo.InvariantCulture) +
                    " idle_unlocked_ram_mib=" + unlockedIdle.MaxWorkingSetMiB.ToString("F1", CultureInfo.InvariantCulture) +
                    " idle_unlocked_ram_avg_mib=" + unlockedIdle.AverageWorkingSetMiB.ToString("F1", CultureInfo.InvariantCulture) +
                    " idle_seconds=" + idleSeconds.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine(
                    "OBSERVATION the unlocked service idled at " +
                    unlockedIdle.CpuPercent.ToString("F3", CultureInfo.InvariantCulture) +
                    " % of total host CPU with a peak working set of " +
                    unlockedIdle.MaxWorkingSetMiB.ToString("F1", CultureInfo.InvariantCulture) + " MiB over " +
                    idleSeconds.ToString(CultureInfo.InvariantCulture) + " s");
            }

            // ----- the locked state -----
            var locked = await RunOwnerCommandAsync(
                connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The synthetic S8-T5 vault did not lock (" + locked.StandardError.Trim() + ").");
            if (idleSeconds > 0)
            {
                var lockedIdle = await S8T5IdleSample.CaptureAsync(service.Process, idleSeconds).ConfigureAwait(false);
                Console.WriteLine(
                    "MEASURE idle_locked_cpu_percent=" + lockedIdle.CpuPercent.ToString("F3", CultureInfo.InvariantCulture) +
                    " idle_locked_ram_mib=" + lockedIdle.MaxWorkingSetMiB.ToString("F1", CultureInfo.InvariantCulture) +
                    " idle_locked_ram_avg_mib=" + lockedIdle.AverageWorkingSetMiB.ToString("F1", CultureInfo.InvariantCulture) +
                    " idle_seconds=" + idleSeconds.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine(
                    "OBSERVATION the locked service idled at " +
                    lockedIdle.CpuPercent.ToString("F3", CultureInfo.InvariantCulture) +
                    " % of total host CPU with a peak working set of " +
                    lockedIdle.MaxWorkingSetMiB.ToString("F1", CultureInfo.InvariantCulture) + " MiB over " +
                    idleSeconds.ToString(CultureInfo.InvariantCulture) + " s");
            }

            // ----- the on-disk footprint after the lock -----
            var finalBytes = S8T5DirectorySize(fixture.DataRootPath);
            Console.WriteLine(
                "MEASURE disk_product_bytes=" + finalBytes.ToString(CultureInfo.InvariantCulture) +
                " disk_bytes_per_record=" +
                (finalBytes / Math.Max(recordCount, 1)).ToString("F1", CultureInfo.InvariantCulture));
            Console.WriteLine(
                "OBSERVATION the persistent product data of the synthetic vault measures " +
                finalBytes.ToString(CultureInfo.InvariantCulture) + " bytes on disk for " +
                recordCount.ToString(CultureInfo.InvariantCulture) + " records");
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

                try
                {
                    var error = (await service.StandardErrorDrain.ConfigureAwait(false)).Trim();
                    if (error.Length > 0)
                    {
                        Console.WriteLine("DIAGNOSTIC the service stderr was: " + error);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Sends one request over the real owner control pipe with a caller-chosen
    /// answer timeout. The index rebuild of a large vault legitimately runs
    /// for minutes, so the measurement cannot use the short default answer
    /// timeout of the shared helper.
    /// </summary>
    private static async Task<JsonElement> InvokeOwnerRequestWithTimeoutAsync(string pipeName, object request, TimeSpan timeout)
    {
        using var client = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Impersonation);
        await client.ConnectAsync(5000).ConfigureAwait(false);
        using var reader = new StreamReader(client, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false, true), 4096, leaveOpen: true) { AutoFlush = true };
        var authentication = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Require(authentication is not null && authentication.Contains("\"authenticated\":true", StringComparison.Ordinal),
            "The real owner control pipe did not authenticate the S8-T5 request.");
        await writer.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
        var response = await reader.ReadLineAsync().WaitAsync(timeout).ConfigureAwait(false)
            ?? throw new IOException("The real owner control pipe returned no S8-T5 response.");
        using var document = JsonDocument.Parse(response);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Measures the owner's own per-record write path: the path a daily
    /// capture takes, over the real owner pipe and the real encrypted store.
    /// </summary>
    private static async Task<S8T5IngestSample> MeasureS8T5IngestAsync(
        string connectionsExecutable,
        S8T5Fixture fixture,
        int records)
    {
        var timings = new List<double>(records);
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < records; index++)
        {
            var id = "mem-s8t5-ingest-" + index.ToString("D5", CultureInfo.InvariantCulture);
            var content = S8T5Corpus.ItemContent(
                "S8-T5 ingest record " + index.ToString(CultureInfo.InvariantCulture),
                S8T5Corpus.Body("s8t5ingest" + index.ToString("D5", CultureInfo.InvariantCulture), 2048),
                "lesson",
                "l1");
            var single = Stopwatch.StartNew();
            var reply = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "memory.create",
                resourceId = id,
                zoneId = S8T5ZoneAlpha,
                contentBase64 = Convert.ToBase64String(content),
            }).ConfigureAwait(false);
            single.Stop();
            if (ReplyCode(reply) != "memory_created")
            {
                Console.WriteLine(
                    "DIAGNOSTIC the owner write was refused with code=" + ReplyCode(reply) +
                    " reply=" + reply.GetRawText());
                var logPath = Path.Combine(fixture.DataRootPath, "logs", "operations.log");
                if (File.Exists(logPath))
                {
                    Console.WriteLine(
                        "DIAGNOSTIC the safe operational log was: " +
                        (await File.ReadAllTextAsync(logPath).ConfigureAwait(false)).Trim());
                }
            }

            Require(ReplyCode(reply) == "memory_created",
                "The synthetic S8-T5 ingest record was not written (reply=" + reply.GetRawText() + ").");
            timings.Add(single.Elapsed.TotalMilliseconds);
        }

        watch.Stop();
        return new S8T5IngestSample(records, watch.Elapsed, timings);
    }

    /// <summary>
    /// Measures the real owner library search over the real owner pipe: the
    /// warm-up queries are discarded, the timed ones are reported.
    /// </summary>
    private static async Task<S8T5RecallSample> MeasureS8T5RecallAsync(
        S8T5Fixture fixture,
        S8T5Corpus corpus,
        int queryCount)
    {
        var warmups = Math.Min(100, Math.Max(10, queryCount / 10));
        for (var index = 0; index < warmups; index++)
        {
            var warmup = await InvokeOwnerRequestWithTimeoutAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "owner.library.search",
                query = corpus.Queries[index % corpus.Queries.Count].Text,
                searchOffset = 0,
                searchLimit = 10,
            }, S8T5QueryTimeout).ConfigureAwait(false);
            Require(ReplyCode(warmup) == "library_page",
                "The S8-T5 warm-up query did not answer (" + warmup.GetRawText() + ").");
        }

        var timings = new List<double>(queryCount);
        var matches = new List<int>(queryCount);
        var naturalHits = 0;
        var naturalCount = 0;
        var symbolHits = 0;
        var symbolCount = 0;
        for (var index = 0; index < queryCount; index++)
        {
            var query = corpus.Queries[index % corpus.Queries.Count];
            var watch = Stopwatch.StartNew();
            var reply = await InvokeOwnerRequestWithTimeoutAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "owner.library.search",
                query = query.Text,
                searchOffset = 0,
                searchLimit = 10,
            }, S8T5QueryTimeout).ConfigureAwait(false);
            watch.Stop();
            Require(ReplyCode(reply) == "library_page",
                "The S8-T5 timed query did not answer (" + reply.GetRawText() + ").");
            timings.Add(watch.Elapsed.TotalMilliseconds);
            matches.Add(S8T5PageTotal(reply));

            var found = S8T5PageContains(reply, query.ExpectedResourceId);
            if (query.IsSymbol)
            {
                symbolCount++;
                if (found)
                {
                    symbolHits++;
                }
            }
            else
            {
                naturalCount++;
                if (found)
                {
                    naturalHits++;
                }
            }
        }

        timings.Sort();
        var p50 = S8T5Sample.Percentile(timings, 0.50);
        var p95 = S8T5Sample.Percentile(timings, 0.95);
        return new S8T5RecallSample(
            queryCount,
            warmups,
            p50,
            p95,
            timings.Count == 0 ? 0 : timings[^1],
            naturalCount == 0 ? 0 : (double)naturalHits / naturalCount,
            symbolCount == 0 ? 0 : (double)symbolHits / symbolCount,
            matches.Count == 0 ? 0 : matches.Average(),
            matches.Count == 0 ? 0 : matches.Max());
    }

    /// <summary>
    /// How many records of the owner's scope matched the query, as the
    /// service reported it. It is the size of the candidate set the ranking
    /// had to score, which is what makes a recall answer expensive.
    /// </summary>
    private static int S8T5PageTotal(JsonElement reply)
    {
        if (reply.TryGetProperty("library", out var library) &&
            library.TryGetProperty("total", out var total) &&
            total.ValueKind == JsonValueKind.Number)
        {
            return total.GetInt32();
        }

        return 0;
    }

    private static bool S8T5PageContains(JsonElement reply, string resourceId)
    {
        if (!reply.TryGetProperty("library", out var library) ||
            !library.TryGetProperty("hits", out var hits) ||
            hits.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var hit in hits.EnumerateArray())
        {
            if (hit.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString(), resourceId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static long S8T5DirectorySize(string root)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return total;
    }
}

/// <summary>One measured ingest run over the owner's own write path.</summary>
internal sealed record S8T5IngestSample(int Records, TimeSpan Elapsed, List<double> WriteTimingsMs)
{
    public double P50Ms => S8T5Sample.Percentile(WriteTimingsMs, 0.50);

    public double P95Ms => S8T5Sample.Percentile(WriteTimingsMs, 0.95);
}

/// <summary>One measured recall run over the real owner pipe.</summary>
internal sealed record S8T5RecallSample(
    int Count,
    int Warmups,
    double P50Ms,
    double P95Ms,
    double MaxMs,
    double HitRate,
    double SymbolHitRate,
    double AverageMatches,
    int MaxMatches);

/// <summary>Shared percentile helper for the S8-T5 samples.</summary>
internal static class S8T5Sample
{
    public static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = new List<double>(values);
        sorted.Sort();
        var rank = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        if (rank < 0)
        {
            rank = 0;
        }

        if (rank >= sorted.Count)
        {
            rank = sorted.Count - 1;
        }

        return sorted[rank];
    }
}

/// <summary>
/// One idle window of the real service process: the CPU it burned and the
/// working set it held, sampled once per second.
/// </summary>
internal sealed class S8T5IdleSample
{
    private S8T5IdleSample(double cpuPercent, double maxWorkingSetMiB, double averageWorkingSetMiB)
    {
        CpuPercent = cpuPercent;
        MaxWorkingSetMiB = maxWorkingSetMiB;
        AverageWorkingSetMiB = averageWorkingSetMiB;
    }

    public double CpuPercent { get; }

    public double MaxWorkingSetMiB { get; }

    public double AverageWorkingSetMiB { get; }

    public static async Task<S8T5IdleSample> CaptureAsync(Process process, int seconds)
    {
        ArgumentNullException.ThrowIfNull(process);
        var workingSets = new List<double>(Math.Max(seconds, 1));
        process.Refresh();
        var startCpu = process.TotalProcessorTime;
        var start = DateTimeOffset.UtcNow;
        for (var second = 0; second < seconds; second++)
        {
            await Task.Delay(1000).ConfigureAwait(false);
            process.Refresh();
            workingSets.Add(process.WorkingSet64 / 1024d / 1024d);
        }

        var elapsed = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
        process.Refresh();
        var cpu = process.TotalProcessorTime - startCpu;
        var percent = elapsed <= 0
            ? 0
            : cpu.TotalMilliseconds / (elapsed * Environment.ProcessorCount) * 100d;
        var max = workingSets.Count == 0 ? 0 : workingSets.Max();
        var average = workingSets.Count == 0 ? 0 : workingSets.Average();
        return new S8T5IdleSample(percent, max, average);
    }
}

/// <summary>
/// The synthetic measurement corpus: deterministic bilingual memory items
/// with code symbols and a temporal spread, plus the query set whose expected
/// item is known by construction. It is a measurement fixture, not a
/// representative owner corpus.
/// </summary>
internal sealed class S8T5Corpus
{
    public const string ZoneAlpha = "s8t5-zone-alpha";
    public const string ZoneBeta = "s8t5-zone-beta";

    private S8T5Corpus(List<VaultResourceSeed> seeds, List<S8T5Query> queries, long meanBodyBytes, long p95BodyBytes)
    {
        Seeds = seeds;
        Queries = queries;
        MeanBodyBytes = meanBodyBytes;
        P95BodyBytes = p95BodyBytes;
    }

    public List<VaultResourceSeed> Seeds { get; }

    public List<S8T5Query> Queries { get; }

    public long MeanBodyBytes { get; }

    public long P95BodyBytes { get; }

    /// <summary>
    /// The vocabulary the synthetic documents are built from. Every document
    /// draws its own words from these lists, so the corpus is diverse the way
    /// a real corpus is: a two-word query matches a handful of documents
    /// instead of every document that shares one template sentence.
    /// </summary>
    private static readonly string[] Nouns =
    {
        "manifest", "recall", "index", "vault", "token", "grant", "session", "ledger",
        "catalog", "privacy", "budget", "backup", "migration", "schema", "latency", "corpus",
        "decision", "question", "research", "intention", "synthesis", "provider", "evidence",
        "manifest bieu", "chi muc", "khoa", "goi y", "ky thuat", "chi so",
    };

    private static readonly string[] Verbs =
    {
        "rebuild", "measure", "refuse", "encrypt", "publish", "revoke", "export", "restore",
        "rank", "index", "audit", "lock", "migrate", "verify", "trim", "dedupe",
        "cap nhat", "do lai", "tu choi", "ma hoa", "cong bo", "thu hoi",
    };

    private static readonly string[] Objects =
    {
        "the owner scope", "the private index", "the rolling spend", "the access request",
        "the cited source", "the stored revision", "the audit trail", "the diagnostics",
        "the capture journal", "the published catalog", "the recovery key", "the session token",
        "pham vi owner", "chi muc cong khai", "nhat ky thao tac", "ke hoach von",
    };

    private static readonly string[] Symbols =
    {
        "MAX_REQUEST_INPUT_CHARACTERS", "MAX_AGENT_GRAPH_STEPS", "redeemToken", "scopeGrant",
        "privateIndex", "ownerCatalog", "spendLedger", "sourceSnapshot", "reviewState",
        "purgeLedger", "operationsAudit", "recoveryCode",
    };

    private static readonly string[] Templates =
    {
        "Quyet dinh: {noun} phai {verb} {object} truoc khi tra loi.",
        "Decision: the {noun} must {verb} the {object} before it answers.",
        "Bai hoc: khi {noun} khong {verb} {object}, do lai cach do.",
        "Lesson: when the {noun} does not {verb} the {object}, the owner has to look again.",
        "Loi: {noun} {verb} {object} sai thu tu, va ban phai sua lai.",
        "Bug: the {noun} {verb} the {object} in the wrong order.",
        "Cau hoi: co nen {verb} {object} khi {noun} da duoc khoa?",
        "Question: should the {noun} {verb} the {object} while the vault is locked?",
        "Y tuong: dung {symbol} de {verb} {object} ma van giu {noun}.",
        "Idea: use {symbol} to {verb} the {object} and still keep the {noun} private.",
        "Ghi chu ngay {date}: {noun} {verb} {object} som hon du kien.",
        "Note from {date}: the {noun} {verb} the {object} earlier than expected.",
        "Ky thuat: {noun} chi {verb} {object} khi {symbol} da duoc xac nhan.",
        "Technique: the {noun} only {verb} the {object} after {symbol} was confirmed.",
        "Nghien cuu: {noun} va {symbol} cùng quyết định {verb} {object}.",
        "Research: the {noun} and {symbol} together decide how to {verb} the {object}.",
        "Nhiem vu: {verb} {object} va ghi lai ly do cho {noun}.",
        "Task: {verb} the {object} and write down the reason for the {noun}.",
        "Kiem chung: {noun} {verb} {object} dung ngay {date}.",
        "Check: the {noun} {verb} the {object} exactly on {date}.",
        "Pham vi: {noun} thuoc ve {object}, khong phai ve {symbol}.",
        "Scope: the {noun} belongs to the {object}, not to {symbol}.",
        "Han: {noun} het han o {date} nen {object} phai {verb} lai.",
        "Expiry: the {noun} expires on {date}, so the {object} has to {verb} again.",
        "Chi so: p95 cua {noun} duoc do lai, {symbol} la moc de so sanh.",
        "Metric: the p95 of the {noun} was measured again, {symbol} is the reference.",
    };

    public static S8T5Corpus Build(int recordCount)
    {
        var seeds = new List<VaultResourceSeed>(recordCount);
        var queries = new List<S8T5Query>();
        var sizes = new List<long>(recordCount);
        for (var index = 0; index < recordCount; index++)
        {
            var marker = "s8t5ref" + index.ToString("D6", CultureInfo.InvariantCulture);
            var zone = index % 2 == 0 ? ZoneAlpha : ZoneBeta;
            var kind = (index % 4) switch
            {
                0 => "decision",
                1 => "lesson",
                2 => "question",
                _ => "procedure",
            };
            // Every document draws its own words, so the corpus is diverse and
            // a two-word query matches a handful of documents, not all of them.
            var random = new DeterministicRandom((ulong)(20261021 + index));
            var noun = Nouns[random.Next(0, Nouns.Length)];
            var verb = Verbs[random.Next(0, Verbs.Length)];
            var title = "S8-T5 " + marker + " " + noun + " " + verb;
            var body = Body(marker, BodySize(index, random), random);
            var resourceId = "mem-s8t5-" + index.ToString("D6", CultureInfo.InvariantCulture);
            var recorded = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(index % 4000);
            var content = StoredContent(
                resourceId,
                zone,
                title,
                body,
                kind,
                recorded,
                recorded.AddHours(-1));
            seeds.Add(new VaultResourceSeed(resourceId, zone, content));
            sizes.Add(body.Length);

            // The natural query is the two words the item's own title
            // carries; the symbol query is the exact marker of that item.
            // Both have a known expected answer by construction.
            queries.Add(new S8T5Query(noun + " " + verb, resourceId, IsSymbol: false));
            queries.Add(new S8T5Query(marker, resourceId, IsSymbol: true));
        }

        sizes.Sort();
        var mean = recordCount == 0 ? 0 : (long)sizes.Average();
        var p95 = sizes.Count == 0 ? 0 : sizes[(int)Math.Ceiling(0.95 * sizes.Count) - 1];
        return new S8T5Corpus(seeds, queries, mean, p95);
    }

    private static int BodySize(int index, DeterministicRandom random)
    {
        // Mean close to 4 KiB with a p95 at or below 16 KiB: most bodies are a
        // few KiB, a small tail reaches 16 KiB.
        var baseSize = 1536 + (index % 16) * 256;
        if (index % 25 == 0)
        {
            return 16384;
        }

        return baseSize + random.Next(0, 256);
    }

    public static string Body(string marker, int targetBytes) =>
        Body(marker, targetBytes, new DeterministicRandom(20261021));

    private static string Body(string marker, int targetBytes, DeterministicRandom random)
    {
        var builder = new StringBuilder(targetBytes + 64);
        builder.Append("marker ").Append(marker).Append(". ");
        var date = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            .AddDays(random.Next(0, 300))
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        while (builder.Length < targetBytes)
        {
            var template = Templates[random.Next(0, Templates.Length)];
            builder.Append(
                template
                    .Replace("{noun}", Nouns[random.Next(0, Nouns.Length)], StringComparison.Ordinal)
                    .Replace("{verb}", Verbs[random.Next(0, Verbs.Length)], StringComparison.Ordinal)
                    .Replace("{object}", Objects[random.Next(0, Objects.Length)], StringComparison.Ordinal)
                    .Replace("{symbol}", Symbols[random.Next(0, Symbols.Length)], StringComparison.Ordinal)
                    .Replace("{date}", date, StringComparison.Ordinal))
                .Append(' ');
        }

        return builder.ToString();
    }

    public static byte[] ItemContent(string title, string body, string kind, string level)
    {
        // One instant for both stamps: the recorded instant is the write and
        // the occurred instant is the event the record talks about, which
        // happened before it was written.
        var recorded = DateTimeOffset.UtcNow;
        return ItemContentWithTime(title, body, kind, level, recorded, recorded.AddHours(-1));
    }

    /// <summary>
    /// The canonical stored envelope of one synthetic item: exactly the bytes
    /// the product's own write path commits, so the seeded fixture is a real
    /// canonical record and not a look-alike.
    /// </summary>
    public static byte[] StoredContent(
        string resourceId,
        string zoneId,
        string title,
        string body,
        string kind,
        DateTimeOffset recordedAtUtc,
        DateTimeOffset occurredAtUtc)
    {
        var memoryKind = kind switch
        {
            "decision" => MemoryKind.Decision,
            "question" => MemoryKind.Question,
            "procedure" => MemoryKind.Procedure,
            "observation" => MemoryKind.Observation,
            _ => MemoryKind.Lesson,
        };
        var stored = new StoredMemoryEnvelope(
            MemoryEnvelopeCodec.CurrentSchemaVersion,
            resourceId,
            zoneId,
            memoryKind,
            SpeakerRole.User,
            ExecutorKind.Owner,
            CreationMethod.DirectWrite,
            EvidenceLevel.Unverified,
            AbstractionLevel.L1,
            title,
            body,
            Array.Empty<MemorySourceReference>(),
            occurredAtUtc,
            recordedAtUtc,
            null);
        return MemoryEnvelopeCodec.EncodeStored(stored);
    }

    private static byte[] ItemContentWithTime(
        string title,
        string body,
        string kind,
        string level,
        DateTimeOffset recordedAtUtc,
        DateTimeOffset? occurredAtUtc)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind,
            author = "user",
            creationMethod = "directWrite",
            evidence = "unverified",
            level,
            title,
            body,
            sources = Array.Empty<object>(),
            occurredAtUtc,
            recordedAtUtc,
            applicability = (object?)null,
        });
    }
}

/// <summary>One measured query and the item it is expected to find.</summary>
internal sealed record S8T5Query(string Text, string ExpectedResourceId, bool IsSymbol);

/// <summary>A deterministic generator so every run measures the same corpus.</summary>
internal sealed class DeterministicRandom
{
    private ulong _state;

    public DeterministicRandom(ulong seed)
    {
        _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
    }

    public int Next(int minimumInclusive, int maximumExclusive)
    {
        _state ^= _state << 13;
        _state ^= _state >> 7;
        _state ^= _state << 17;
        var range = (ulong)Math.Max(maximumExclusive - minimumInclusive, 1);
        return minimumInclusive + (int)(_state % range);
    }
}

/// <summary>
/// The S8-T5 fixture: a repository-local data root, a real owner control pipe
/// and a real agent pipe, exactly like every other scenario.
/// </summary>
internal sealed class S8T5Fixture : IDisposable
{
    private bool _disposed;

    private S8T5Fixture(string root, string dataRoot, string settingsPath, string controlPipe, string agentPipe)
    {
        Root = root;
        DataRootPath = dataRoot;
        SettingsPath = settingsPath;
        ControlPipe = controlPipe;
        AgentPipe = agentPipe;
    }

    public string Root { get; }

    public string DataRootPath { get; }

    public string SettingsPath { get; }

    public string ControlPipe { get; }

    public string AgentPipe { get; }

    public string VaultDirectoryPath => Path.Combine(DataRootPath, "vault");

    public static S8T5Fixture Create()
    {
        var repositoryRoot = SmokeFixture.FindRepositoryRoot();
        var root = Path.Combine(repositoryRoot, "runtime", "s8-t5", "run-" + Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "live");
        Directory.CreateDirectory(dataRoot);
        try
        {
            var controlPipe = "MetaBrainS8T5-" + Guid.NewGuid().ToString("N");
            var agentPipe = controlPipe + ".agent";
            var settingsPath = WriteSettings(dataRoot, controlPipe, agentPipe);
            return new S8T5Fixture(root, dataRoot, settingsPath, controlPipe, agentPipe);
        }
        catch
        {
            TryDelete(root);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TryDelete(Root);
    }

    private static string WriteSettings(string dataRoot, string controlPipe, string agentPipe)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var ownerSid = identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows token has no user SID.");
        var path = Path.Combine(dataRoot, "service-settings.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 3,
                ownerSid,
                controlPipe,
                agentPipe,
            }),
            new UTF8Encoding(false));
        return path;
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
