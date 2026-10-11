using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string S2T7Zone = "s2t7-zone-alpha";

    private static string S2T7ItemDraft(
        string kind,
        string author,
        string creationMethod,
        string evidence,
        string title,
        string body,
        DateTimeOffset now,
        string? level = "l1") =>
        S2T5ItemDraft(kind, author, creationMethod, evidence, title, body, now, level);

    private static string S2T7SourceDraft(string title, string locator, string version, DateTimeOffset now) =>
        JsonSerializer.Serialize(new
        {
            title,
            locator,
            version,
            availability = "available",
            creationMethod = "directWrite",
            capturedAtUtc = now,
            recordedAtUtc = now.AddMinutes(1)
        });

    private static string S2T7LifecycleDraft(string reason) => S2T5LifecycleDraft(reason);

    private static string S2T7TransitionDraft(string rationale) => S2T5TransitionDraft(rationale);

    private static async Task WriteS2T7ItemDraftAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string zoneId,
        string draftJson,
        long expectedRevision) =>
        await WriteS2T5ItemDraftAsync(executable, fixture, resourceId, zoneId, draftJson, expectedRevision).ConfigureAwait(false);

    private static async Task<CommandResult> WriteS2T7ItemDraftResultAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string zoneId,
        string draftJson) =>
        await WriteS2T5ItemDraftResultAsync(executable, fixture, resourceId, zoneId, draftJson).ConfigureAwait(false);

    private static async Task ReviseS2T7ItemDraftAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string zoneId,
        string draftJson,
        long writeRevision,
        long expectedRevision) =>
        await ReviseS2T5ItemDraftAsync(executable, fixture, resourceId, zoneId, draftJson, writeRevision, expectedRevision).ConfigureAwait(false);

    private static async Task WriteS2T7SourceDraftAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string zoneId,
        string draftJson,
        long expectedRevision)
    {
        var result = await WriteS2T7SourceDraftResultAsync(executable, fixture, resourceId, zoneId, draftJson).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"SOURCE stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Source create did not store {resourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
    }

    private static async Task<CommandResult> WriteS2T7SourceDraftResultAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string zoneId,
        string draftJson)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t7-source-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "source-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
    }

    private static async Task CreateS2T7RelationAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string zoneId,
        string draftJson) =>
        await CreateS2T4RelationAsync(executable, fixture, resourceId, zoneId, draftJson).ConfigureAwait(false);

    private static async Task<CommandResult> CreateS2T7LifecycleAsync(
        string executable,
        SmokeFixture fixture,
        string subjectId,
        string draftJson)
    {
        var result = await CreateS2T5LifecycleAsync(executable, fixture, subjectId, draftJson).ConfigureAwait(false);
        Require(result.ExitCode == 0,
            $"Lifecycle create failed for {subjectId} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
        return result;
    }

    private static async Task<CommandResult> TransitionS2T7LifecycleAsync(
        string executable,
        SmokeFixture fixture,
        string command,
        string subjectId,
        long expectedRevision,
        string? transitionDraft = null)
    {
        var result = await TransitionS2T5LifecycleAsync(executable, fixture, command, subjectId, expectedRevision, transitionDraft).ConfigureAwait(false);
        Require(result.ExitCode == 0,
            $"The lifecycle {command} transition failed for {subjectId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
        return result;
    }

    private static async Task<S2T4Session> IssueS2T7GrantAsync(
        string executable,
        SmokeFixture fixture,
        string name,
        string operations,
        string[]? resourceIds = null) =>
        await IssueS2T6GrantAsync(executable, fixture, name, operations, resourceIds: resourceIds).ConfigureAwait(false);

    private static async Task<CommandResult> S2T7IndexStatusAsync(string executable, SmokeFixture fixture) =>
        await RunOwnerCommandAsync(executable,
            "owner", "index-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);

    private static async Task<CommandResult> S2T7IndexRebuildAsync(string executable, SmokeFixture fixture) =>
        await RunOwnerCommandAsync(executable,
            "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);

    /// <summary>
    /// Parses the materialized index rows from an owner index reply into one
    /// comparable map: resource ID to (zone, revision, kind, state, links).
    /// </summary>
    private static Dictionary<string, (string Zone, long Revision, string Kind, string State, string Links)> ParseS2T7IndexRows(string text)
    {
        var rows = new Dictionary<string, (string, long, string, string, string)>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n').Select(value => value.Trim()))
        {
            if (!line.StartsWith("INDEX ROW id=", StringComparison.Ordinal))
            {
                continue;
            }

            var id = string.Empty;
            var zone = string.Empty;
            var revision = 0L;
            var kind = string.Empty;
            var state = string.Empty;
            var links = string.Empty;
            foreach (var field in line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (field.StartsWith("id=", StringComparison.Ordinal))
                {
                    id = field["id=".Length..];
                }
                else if (field.StartsWith("zone=", StringComparison.Ordinal))
                {
                    zone = field["zone=".Length..];
                }
                else if (field.StartsWith("revision=", StringComparison.Ordinal))
                {
                    revision = long.Parse(field["revision=".Length..], CultureInfo.InvariantCulture);
                }
                else if (field.StartsWith("kind=", StringComparison.Ordinal))
                {
                    kind = field["kind=".Length..];
                }
                else if (field.StartsWith("state=", StringComparison.Ordinal))
                {
                    state = field["state=".Length..];
                }
                else if (field.StartsWith("links=", StringComparison.Ordinal))
                {
                    links = field["links=".Length..];
                }
            }

            Require(!string.IsNullOrEmpty(id), "A materialized index row carried no resource ID (line=" + line + ").");
            rows[id] = (zone, revision, kind, state, links);
        }

        return rows;
    }

    /// <summary>
    /// Reads each fixture resource's live canonical revision through the real
    /// read operation that addresses it, so the index is compared against
    /// canonical itself rather than against a second private copy.
    /// </summary>
    private static async Task<Dictionary<string, long>> ReadS2T7CanonicalRevisionsAsync(
        string executable,
        SmokeFixture fixture,
        (string Id, string Command, string ReadAs)[] reads)
    {
        var canonical = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (resourceId, command, readAs) in reads)
        {
            var read = await RunOwnerCommandAsync(executable,
                "owner", command, "--control-pipe", fixture.ControlPipe,
                "--resource-id", readAs).ConfigureAwait(false);
            Require(read.ExitCode == 0 && read.StandardOutput.Contains("revision=", StringComparison.Ordinal),
                "The canonical read-back for " + resourceId + " failed (exit=" + read.ExitCode + "; stdout=" + read.StandardOutput.Trim() + "; stderr=" + read.StandardError.Trim() + ").");
            canonical[resourceId] = ParseS2T7Revision(read.StandardOutput);
        }

        return canonical;
    }

    private static long ParseS2T7Revision(string text)
    {
        const string marker = "revision=";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Require(start >= 0, "A canonical read did not report its revision (stdout=" + text.Trim() + ").");
        start += marker.Length;
        var end = text.IndexOfAny(['\r', '\n', ' '], start);
        return long.Parse(end < 0 ? text[start..] : text[start..end], CultureInfo.InvariantCulture);
    }

    private static string ParseS2T7Digest(string text)
    {
        const string marker = "digest=";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Require(start >= 0, "An index reply did not report its canonical fingerprint (stdout=" + text.Trim() + ").");
        start += marker.Length;
        var end = text.IndexOfAny(['\r', '\n', ' '], start);
        return end < 0 ? text[start..] : text[start..end];
    }

    private static long ParseS2T7RowCount(string text)
    {
        const string marker = "rows=";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Require(start >= 0, "An index reply did not report its row count (stdout=" + text.Trim() + ").");
        start += marker.Length;
        var end = text.IndexOfAny(['\r', '\n', ' '], start);
        return long.Parse(end < 0 ? text[start..] : text[start..end], CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Tampering goes through the decoded envelope fields, not raw file bytes:
    /// the envelope is JSON, so a raw offset flip can land inside a \uXXXX
    /// escape or in the ignored padding bits of the final base64 group and
    /// leave the authenticated bytes identical, which proves nothing about the
    /// ciphertext boundary. Each field is decoded from JSON (so \u002B-style
    /// escapes are resolved), one bit is flipped, and the field is re-encoded.
    /// </summary>
    private static async Task MutateS2T7AuthenticatedEnvelopeAsync(string path, byte[] original)
    {
        var fields = new[] { "nonce", "ciphertext", "tag" };
        foreach (var field in fields)
        {
            var envelope = JsonNode.Parse(original)?.AsObject()
                ?? throw new InvalidOperationException("The S2-T7 canonical envelope is not a JSON object.");
            var value = envelope[field]?.GetValue<string>();
            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException("The S2-T7 canonical envelope did not carry the " + field + " field.");
            }

            var decoded = Convert.FromBase64String(value);
            decoded[0] ^= 0x01;
            envelope[field] = Convert.ToBase64String(decoded);
            CryptographicOperations.ZeroMemory(decoded);
            var mutated = envelope.ToJsonString();
            await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(mutated)).ConfigureAwait(false);
            var reread = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var beforeBytes = S2T7DecodeAuthenticated(original);
            var afterBytes = S2T7DecodeAuthenticated(reread);
            Require(S2T7AuthenticatedBytesChanged(original, reread),
                "The S2-T7 tamper mutation on " + field + " did not change the decoded authenticated bytes (decoded-before=" +
                (beforeBytes?.Length.ToString(CultureInfo.InvariantCulture) ?? "null") + ", decoded-after=" +
                (afterBytes?.Length.ToString(CultureInfo.InvariantCulture) ?? "null") + ", decode-failure=" + (S2T7DecodeFailure ?? "none") +
                ", raw-lengths=" + original.Length + "->" + reread.Length + ").");
        }
    }

    /// <summary>
    /// True only when the decoded nonce/ciphertext/tag bytes actually differ.
    /// An unparseable envelope returns false: a mutation that cannot be shown
    /// to change authenticated bytes is not treated as a tamper proof.
    /// </summary>
    private static bool S2T7AuthenticatedBytesChanged(byte[] original, byte[] mutated)
    {
        var before = S2T7DecodeAuthenticated(original);
        var after = S2T7DecodeAuthenticated(mutated);
        return before is not null && after is not null && !before.AsSpan().SequenceEqual(after);
    }

    private static string? S2T7DecodeFailure;

    private static byte[]? S2T7DecodeAuthenticated(byte[] envelopeBytes)
    {
        S2T7DecodeFailure = null;
        try
        {
            using var document = JsonDocument.Parse(envelopeBytes);
            var root = document.RootElement;
            var parts = new List<byte>();
            foreach (var field in new[] { "nonce", "ciphertext", "tag" })
            {
                if (!root.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
                {
                    S2T7DecodeFailure = "missing " + field;
                    return null;
                }

                parts.AddRange(Convert.FromBase64String(value.GetString() ?? string.Empty));
            }

            return parts.ToArray();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            S2T7DecodeFailure = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Counts every private file under the fixture roots, so the spill boundary
    /// is stated as an observed number rather than a claim.
    /// </summary>
    private static List<string> ListS2T7PrivateFiles(SmokeFixture fixture) =>
        new[] { fixture.VaultDirectoryPath, fixture.VaultResourcesPath, fixture.ServiceDirectoryPath, fixture.OutputDirectory }
            .Where(Directory.Exists)
            .SelectMany(root => Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    private static int CountS2T7PrivateFiles(SmokeFixture fixture) => ListS2T7PrivateFiles(fixture).Count;

    /// <summary>
    /// Scans every private index spill path and its sibling directories for a
    /// persisted synthetic marker and for any SQLite sidecar. The memory-only
    /// engine must leave nothing on disk: no database file, no WAL, no rollback
    /// journal, no sort temp and no page cache. Synthetic plaintext drafts and
    /// handoff files are removed first, exactly like the other S2 scenarios,
    /// and their removal is then verified.
    /// </summary>
    private static void VerifyS2T7NoPrivateIndexSpill(SmokeFixture fixture, params string[] forbiddenMarkers)
    {
        foreach (var draft in Directory.GetFiles(fixture.OutputDirectory, "s2t4-*", SearchOption.TopDirectoryOnly)
                     .Concat(Directory.GetFiles(fixture.OutputDirectory, "s2t5-*", SearchOption.TopDirectoryOnly))
                     .Concat(Directory.GetFiles(fixture.OutputDirectory, "s2t7-*", SearchOption.TopDirectoryOnly))
                     .Concat(Directory.GetFiles(fixture.OutputDirectory, "*.grant-handoff", SearchOption.TopDirectoryOnly))
                     .Concat(Directory.GetFiles(fixture.OutputDirectory, "*.session-handoff", SearchOption.TopDirectoryOnly)))
        {
            File.Delete(draft);
        }

        var inspected = 0;
        foreach (var root in new[]
                 {
                     fixture.VaultDirectoryPath,
                     fixture.VaultResourcesPath,
                     fixture.ServiceDirectoryPath,
                     fixture.OutputDirectory
                 })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                inspected++;
                var text = File.ReadAllText(path, Encoding.UTF8);
                foreach (var marker in forbiddenMarkers)
                {
                    Require(!text.Contains(marker, StringComparison.Ordinal),
                        "Private S2-T7 index or canonical plaintext leaked into a persisted file: " + path);
                }
            }

            var sidecars = Directory.GetFiles(root, "*.sqlite*", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(root, "*.db", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(root, "*-wal", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(root, "*-journal", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(root, "etilqs*", SearchOption.AllDirectories))
                .ToArray();
            Require(sidecars.Length == 0,
                "The private index engine persisted a database/journal/temp file on disk: " +
                string.Join(", ", sidecars.Select(Path.GetFileName)));
        }

        Require(!Directory.GetFiles(fixture.OutputDirectory, "s2t4-*", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(fixture.OutputDirectory, "s2t5-*", SearchOption.TopDirectoryOnly))
                .Concat(Directory.GetFiles(fixture.OutputDirectory, "s2t7-*", SearchOption.TopDirectoryOnly))
                .Any(),
            "Synthetic plaintext draft files remained outside the vault after the S2-T7 scenario.");
        Console.WriteLine($"INDEX SPILL files-inspected={inspected} sqlite-sidecars=0");
    }
}
