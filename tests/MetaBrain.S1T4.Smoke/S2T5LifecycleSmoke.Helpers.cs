using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private static string S2T5ItemDraft(
        string kind,
        string author,
        string creationMethod,
        string evidence,
        string title,
        string body,
        DateTimeOffset? occurredAtUtc,
        string? level = "l1",
        DateTimeOffset? recordedAtUtc = null) => JsonSerializer.Serialize(new
    {
        kind,
        author,
        creationMethod,
        evidence,
        level,
        title,
        body,
        sources = Array.Empty<object>(),
        occurredAtUtc,
        recordedAtUtc = recordedAtUtc ?? (occurredAtUtc ?? DateTimeOffset.UtcNow).AddMinutes(1),
        applicability = (object?)null
    });

    /// <summary>
    /// One lifecycle draft. Only the basis reason and the temporal knowledge
    /// travel here: no identity, no state, no executor, no revision.
    /// </summary>
    private static string S2T5LifecycleDraft(
        string reason,
        object? occurred = null,
        object? recorded = null,
        object? validFrom = null,
        object? validUntil = null,
        object? reviewAfter = null) => JsonSerializer.Serialize(new
    {
        creationMethod = "directWrite",
        reason,
        occurred,
        recorded,
        validFrom,
        validUntil,
        reviewAfter
    });

    /// <summary>
    /// One temporal stamp as known. An exact stamp carries a UTC instant plus
    /// the preserved zone offset; a coarse stamp carries the wall-clock fields;
    /// an unknown stamp carries neither.
    /// </summary>
    private static object S2T5Stamp(
        string precision,
        DateTimeOffset? instantUtc = null,
        int? year = null,
        int? month = null,
        int? day = null,
        int? quarter = null,
        int? zoneOffsetMinutes = null) => new
    {
        precision,
        instantUtc,
        localYear = year,
        localMonth = month,
        localDay = day,
        localQuarter = quarter,
        timeZoneOffsetMinutes = zoneOffsetMinutes
    };

    private static string S2T5TransitionDraft(
        string? rationale = null,
        string? replacementResourceId = null,
        object? occurred = null,
        object? recorded = null,
        object? validFrom = null,
        object? validUntil = null,
        object? reviewAfter = null,
        string? executor = null,
        string? state = null) => JsonSerializer.Serialize(new
    {
        rationale,
        replacementResourceId,
        occurred,
        recorded,
        validFrom,
        validUntil,
        reviewAfter,
        executor,
        state
    });

    private static async Task WriteS2T5ItemDraftAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string zoneId,
        string draftJson,
        long expectedRevision)
    {
        var result = await WriteS2T5ItemDraftResultAsync(executable, fixture, resourceId, zoneId, draftJson).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"MEMORY stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Memory create did not store {resourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}; draft={draftJson}).");
    }

    private static async Task<CommandResult> WriteS2T5ItemDraftResultAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string zoneId,
        string draftJson)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t5-item-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
    }

    private static async Task ReviseS2T5ItemDraftAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string zoneId,
        string draftJson,
        long writeRevision,
        long expectedRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t5-item-revise-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--expected-revision", writeRevision.ToString(CultureInfo.InvariantCulture),
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"MEMORY stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Memory revise did not store {resourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
    }

    private static async Task<CommandResult> CreateS2T5LifecycleAsync(
        string executable,
        SmokeFixture fixture,
        string subjectId,
        string draftJson)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t5-lifecycle-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "lifecycle-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", subjectId,
            "--input-file", input).ConfigureAwait(false);
    }

    private static async Task<CommandResult> ReadS2T5LifecycleAsync(string executable, SmokeFixture fixture, string subjectId)
    {
        return await RunOwnerCommandAsync(executable,
            "owner", "lifecycle-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", subjectId).ConfigureAwait(false);
    }

    private static async Task<CommandResult> ReadS2T5RetainedAsync(
        string executable,
        SmokeFixture fixture,
        string subjectId,
        long revision)
    {
        return await RunOwnerCommandAsync(executable,
            "owner", "lifecycle-retained-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", subjectId,
            "--resource-revision", revision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
    }

    private static async Task<CommandResult> TransitionS2T5LifecycleAsync(
        string executable,
        SmokeFixture fixture,
        string command,
        string subjectId,
        long expectedRevision,
        string? transitionDraft = null)
    {
        var arguments = new List<string>
        {
            "owner", command, "--control-pipe", fixture.ControlPipe,
            "--resource-id", subjectId,
            "--expected-revision", expectedRevision.ToString(CultureInfo.InvariantCulture)
        };
        if (transitionDraft is not null)
        {
            var input = Path.Combine(fixture.OutputDirectory, "s2t5-transition-" + Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllTextAsync(input, transitionDraft, Encoding.UTF8).ConfigureAwait(false);
            arguments.Add("--input-file");
            arguments.Add(input);
        }

        return await RunOwnerCommandAsync(executable, arguments.ToArray()).ConfigureAwait(false);
    }

    private static void VerifyS2T5CiphertextOnly(SmokeFixture fixture, int expectedResources)
    {
        foreach (var path in Directory.GetFiles(fixture.VaultDirectoryPath, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            Require(!text.Contains("Synthetic S2-T5", StringComparison.Ordinal),
                "Private lifecycle/item plaintext leaked into a persisted vault file: " + Path.GetFileName(path));
            foreach (var forbidden in new[] { "s2t5-private-locator", "s2t5-intention-note" })
            {
                Require(!text.Contains(forbidden, StringComparison.Ordinal),
                    "Private lifecycle plaintext leaked into a persisted vault file: " + Path.GetFileName(path));
            }
        }

        var resources = Directory.GetFiles(fixture.VaultResourcesPath, "*.mbv", SearchOption.TopDirectoryOnly);
        Require(resources.Length == expectedResources,
            "The S2-T5 vault did not persist exactly the expected " + expectedResources + " encrypted resources (found " + resources.Length + ": " +
            string.Join(", ", resources.Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal)) + ").");
        foreach (var file in resources)
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            Require(text.Contains("\"ciphertext\"", StringComparison.Ordinal) && text.Contains("\"nonce\"", StringComparison.Ordinal),
                "A vault resource is not in the authenticated envelope form: " + Path.GetFileName(file));
        }

        Require(!Directory.GetFiles(fixture.VaultDirectoryPath, "*", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(fixture.ServiceDirectoryPath, "*", SearchOption.AllDirectories))
            .Any(path => Path.GetFileName(path).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)),
            "The S2-T5 vault or service directory left an atomic-write temporary file.");

        foreach (var draft in Directory.GetFiles(fixture.OutputDirectory, "s2t5-*", SearchOption.TopDirectoryOnly))
        {
            File.Delete(draft);
        }

        Require(!Directory.GetFiles(fixture.OutputDirectory, "s2t5-*", SearchOption.TopDirectoryOnly).Any(),
            "Synthetic plaintext draft/handoff files remained outside the vault after the S2-T5 scenario.");
    }

    /// <summary>
    /// Issues and redeems a memory-read grant over exactly one retained
    /// historical revision archive resource, for the "no newer access by
    /// accident" checks.
    /// </summary>
    private static Task<S2T4Session> IssueS2T5ArchiveGrantAsync(
        string executable,
        SmokeFixture fixture,
        string name,
        string archiveResourceId) =>
        IssueS2T4GrantAsync(executable, fixture, name,
            resourceIds: new[] { archiveResourceId },
            operations: "memory.read",
            destinationResourceIds: null);
}
