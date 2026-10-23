using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S8-T5 qualitative recall measurement on the owner-permitted corpus
/// (R23, R37).
/// <para>
/// The corpus is the fixed six-document sample the owner authorized for
/// read-only testing access under <c>F:/ai-ml</c> (recorded in the S1-T1
/// artifact). The scenario reads those files, stores them in a synthetic
/// encrypted vault, indexes them with the product's own rebuild and asks the
/// real owner channel the four query categories the S1-T1 query plan named:
/// a pre-existing decision, a Vietnamese/English paraphrase across project
/// areas, an exact Python symbol, and a research-note question against an
/// implemented result.
/// </para>
/// <para>
/// What this scenario measures is **program-observed behaviour**: which
/// documents the product's own lexical pipeline returns for a query, in which
/// order and with which score. Whether that answer is the answer the owner
/// would judge correct is the owner's own judgement (R37) and is not claimed
/// here: no owner walkthrough took place. The files are read in place, never
/// copied outside the encrypted synthetic vault, and no excerpt of them is
/// printed.
/// </para>
/// </summary>
internal static partial class OwnerServiceSmoke
{
    /// <summary>
    /// The fixed owner-permitted sample of the S1-T1 artifact, in the same
    /// order and with the same labels. The paths are the recorded manifest;
    /// nothing outside it is read.
    /// </summary>
    private static readonly (string Label, string Path)[] S8T5OwnerCorpus =
    {
        ("decision-log", @"F:\ai-ml\my-meta-brain\.agents\memory\decisions.md"),
        ("requirements-registry", @"F:\ai-ml\my-meta-brain\docs\REQUIREMENTS.md"),
        ("databricks-plan", @"F:\ai-ml\ecommerce-agent-databricks\docs\PLAN.md"),
        ("databricks-persistence-contract", @"F:\ai-ml\ecommerce-agent-databricks\docs\contracts\conversation-persistence.md"),
        ("databricks-orchestrator-source", @"F:\ai-ml\ecommerce-agent-databricks\agent_core\orchestrator.py"),
        ("bridge-research-kickoff", @"F:\ai-ml\bridge-research\docs\bridge-research-kickoff.md"),
    };

    /// <summary>
    /// The four query categories of the S1-T1 query plan, with the document
    /// the plan names as the expected answer.
    /// </summary>
    private static readonly (string Category, string Query, string Expected)[] S8T5OwnerQueries =
    {
        ("decision-rationale", "quyet dinh giu manifest lam nguon su that duy nhat cua vault", "decision-log"),
        ("decision-rationale-en", "keep the manifest as the single source of truth of the vault", "decision-log"),
        ("paraphrase-bilingual", "ghi chu nghien cuu khoi dong du an va ket qua da trien khai", "bridge-research-kickoff"),
        ("paraphrase-bilingual-en", "research note kickoff and what was actually implemented", "bridge-research-kickoff"),
        ("exact-symbol", "MAX_REQUEST_INPUT_CHARACTERS", "databricks-orchestrator-source"),
        ("exact-symbol", "MAX_AGENT_GRAPH_STEPS", "databricks-orchestrator-source"),
        ("time-window", "2026-01-17 kickoff", "bridge-research-kickoff"),
        ("time-window", "2026-09-28", "decision-log"),
    };

    internal static async Task RunS8T5RelevanceAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S8-T5 owner-corpus measurement requires Windows named pipes.");
        }

        Console.WriteLine("RUN S8-T5 qualitative recall measurement on the owner-permitted corpus");
        using var fixture = S8T5Fixture.Create();
        var passphraseBytes = RandomNumberGenerator.GetBytes(24);
        var passphrase = Convert.ToBase64String(passphraseBytes);
        CryptographicOperations.ZeroMemory(passphraseBytes);
        RunningService? service = null;
        try
        {
            // ----- the owner-permitted sample, read in place -----
            var seeds = new List<VaultResourceSeed>(S8T5OwnerCorpus.Length);
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            var totalBytes = 0;
            foreach (var (label, path) in S8T5OwnerCorpus)
            {
                if (!File.Exists(path))
                {
                    Console.WriteLine("LIMIT the owner-permitted sample file is not present on this host: " + label);
                    return;
                }

                var text = await File.ReadAllTextAsync(path, Encoding.UTF8).ConfigureAwait(false);
                totalBytes += text.Length;
                var resourceId = "mem-s8t5-owner-" + label.Replace('-', '.');
                labels[resourceId] = label;
                var recorded = File.GetLastWriteTimeUtc(path);
                var content = S8T5Corpus.StoredContent(
                    resourceId,
                    "s8t5-owner-zone",
                    label,
                    text,
                    "observation",
                    recorded,
                    recorded.AddHours(-1));
                seeds.Add(new VaultResourceSeed(resourceId, "s8t5-owner-zone", content));
            }

            Console.WriteLine(
                "MEASURE owner_corpus_documents=" + S8T5OwnerCorpus.Length.ToString(CultureInfo.InvariantCulture) +
                " owner_corpus_characters=" + totalBytes.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine(
                "OBSERVATION the owner-permitted sample of " +
                S8T5OwnerCorpus.Length.ToString(CultureInfo.InvariantCulture) +
                " documents was read in place and stored as ciphertext in the synthetic vault; no excerpt is printed");

            var store = new EncryptedVaultStore(fixture.VaultDirectoryPath);
            store.Create(passphrase, seeds);

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var unlocked = await RunOwnerCommandWithInputAsync(
                connectionsExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S8-T5 owner-corpus vault did not unlock (" + unlocked.StandardError.Trim() + ").");
            var rebuilt = await InvokeOwnerRequestWithTimeoutAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "owner.index.rebuild",
            }, TimeSpan.FromMinutes(25)).ConfigureAwait(false);
            Require(ReplyCode(rebuilt) == "index_rebuilt",
                "The real private index did not rebuild for the owner-corpus measurement (" + rebuilt.GetRawText() + ").");

            // ----- the real queries of the recorded plan -----
            var observed = 0;
            var expected = 0;
            foreach (var (category, query, expectedLabel) in S8T5OwnerQueries)
            {
                var watch = Stopwatch.StartNew();
                var reply = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
                {
                    protocolVersion = 1,
                    operation = "owner.library.search",
                    query,
                    searchOffset = 0,
                    searchLimit = 10,
                }).ConfigureAwait(false);
                watch.Stop();
                Require(ReplyCode(reply) == "library_page",
                    "The owner-corpus query did not answer (" + reply.GetRawText() + ").");
                var hits = S8T5LabeledHits(reply, labels);
                var found = hits.Any(hit => string.Equals(hit.Label, expectedLabel, StringComparison.Ordinal));
                if (string.Equals(query, "2026-09-28", StringComparison.Ordinal))
                {
                    Require(
                        hits.Count >= 2 &&
                        string.Equals(hits[0].Label, "decision-log", StringComparison.Ordinal) &&
                        string.Equals(hits[1].Label, "requirements-registry", StringComparison.Ordinal) &&
                        hits[0].Score == hits[1].Score,
                        "Equal-score date results no longer break ties by resource ID.");
                }
                observed++;
                if (found)
                {
                    expected++;
                }

                Console.WriteLine(
                    "MEASURE owner_query category=" + category +
                    " expected=" + expectedLabel +
                    " expected_found=" + (found ? "true" : "false") +
                    " elapsed_ms=" + watch.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) +
                    " returned=" + string.Join(",", hits.Take(3).Select(hit => hit.Label + ":" + hit.Score.ToString("F2", CultureInfo.InvariantCulture))));
            }

            Console.WriteLine(
                "MEASURE owner_query_expected_found=" +
                expected.ToString(CultureInfo.InvariantCulture) + "/" +
                observed.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine(
                "OBSERVATION the product's own lexical pipeline returned the document the recorded plan named for " +
                expected.ToString(CultureInfo.InvariantCulture) + " of " +
                observed.ToString(CultureInfo.InvariantCulture) +
                " recorded queries; this is program-observed behaviour, not the owner's judgement of relevance");
            Console.WriteLine(
                "LIMIT no owner walkthrough took place: whether these answers are the answers the owner would judge correct, stale, or missing (R37) is an outstanding owner action, and the six-document sample is a purposive sample, not the owner's corpus distribution");
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

    private static List<(string Label, double Score)> S8T5LabeledHits(
        JsonElement reply,
        IReadOnlyDictionary<string, string> labels)
    {
        var hits = new List<(string Label, double Score)>();
        if (!reply.TryGetProperty("library", out var library) ||
            !library.TryGetProperty("hits", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return hits;
        }

        foreach (var hit in list.EnumerateArray())
        {
            var id = hit.TryGetProperty("id", out var idValue) ? idValue.GetString() ?? string.Empty : string.Empty;
            var score = hit.TryGetProperty("score", out var scoreValue) && scoreValue.ValueKind == JsonValueKind.Number
                ? scoreValue.GetDouble()
                : 0;
            hits.Add((labels.TryGetValue(id, out var label) ? label : id, score));
        }

        return hits;
    }
}
