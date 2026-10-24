using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private sealed record S2T4Session(string SessionId, string SessionFile);

    private static string S2T4ItemDraft(string kind, string title, string body, DateTimeOffset now) => JsonSerializer.Serialize(new
    {
        kind,
        author = "user",
        creationMethod = "directWrite",
        evidence = "unverified",
        level = "l1",
        title,
        body,
        sources = Array.Empty<object>(),
        occurredAtUtc = now,
        recordedAtUtc = now.AddMinutes(1),
        applicability = (object?)null
    });

    private static string S2T4RelationDraft(
        string kind,
        string state,
        string fromResourceId,
        string fromEndpointKind,
        long fromRevision,
        object? fromSpan,
        string toResourceId,
        string toEndpointKind,
        long toRevision,
        object? toSpan,
        string reason,
        DateTimeOffset now,
        string? requestEvidence = null) => JsonSerializer.Serialize(new
    {
        kind,
        state,
        from = new
        {
            resourceId = fromResourceId,
            endpointKind = fromEndpointKind,
            revision = fromRevision,
            span = fromSpan
        },
        to = new
        {
            resourceId = toResourceId,
            endpointKind = toEndpointKind,
            revision = toRevision,
            span = toSpan
        },
        creationMethod = "directWrite",
        reason,
        requestEvidence,
        stateReason = (string?)null,
        occurredAtUtc = (DateTimeOffset?)null,
        recordedAtUtc = now.AddMinutes(1)
    });

    private static string S2T4TransitionDraft(string? stateReason) => JsonSerializer.Serialize(new
    {
        stateReason
    });

    private static async Task WriteS2T4SourceDraftAsync(string executable, SmokeFixture fixture, string sourceId, string zoneId, string draftJson, long expectedRevision)
    {
        var result = await WriteS2T4SourceDraftResultAsync(executable, fixture, sourceId, zoneId, draftJson).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"SOURCE stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Source create did not store {sourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
    }

    private static async Task<CommandResult> WriteS2T4SourceDraftResultAsync(string executable, SmokeFixture fixture, string sourceId, string zoneId, string draftJson)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t4-source-draft-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "source-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", sourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
    }

    private static async Task ReviseS2T4SourceDraftAsync(string executable, SmokeFixture fixture, string sourceId, string zoneId, string draftJson, long writeRevision, long expectedRevision)
    {
        var result = await ReviseS2T4SourceDraftResultAsync(executable, fixture, sourceId, zoneId, draftJson, writeRevision).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"SOURCE stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Source revise did not store {sourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
    }

    private static async Task<CommandResult> ReviseS2T4SourceDraftResultAsync(string executable, SmokeFixture fixture, string sourceId, string zoneId, string draftJson, long writeRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t4-source-revise-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "source-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", sourceId, "--zone-id", zoneId,
            "--expected-revision", writeRevision.ToString(CultureInfo.InvariantCulture),
            "--input-file", input).ConfigureAwait(false);
    }

    private static async Task WriteS2T4ItemDraftAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson, long expectedRevision)
    {
        var result = await WriteS2T4ItemDraftResultAsync(executable, fixture, resourceId, zoneId, draftJson).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"MEMORY stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Memory create did not store {resourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
    }

    private static async Task<CommandResult> WriteS2T4ItemDraftResultAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t4-item-draft-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
    }

    private static async Task CreateS2T4RelationAsync(string executable, SmokeFixture fixture, string relationId, string zoneId, string draftJson)
    {
        var result = await CreateS2T4RelationResultAsync(executable, fixture, relationId, zoneId, draftJson).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"RELATION stored revision=1", StringComparison.Ordinal),
            $"Relation create did not store {relationId} at revision 1 (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}; draft={draftJson}).");
    }

    private static async Task<CommandResult> CreateS2T4RelationResultAsync(string executable, SmokeFixture fixture, string relationId, string zoneId, string draftJson)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t4-relation-draft-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "relation-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", relationId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
    }

    private static async Task<CommandResult> TransitionS2T4RelationAsync(
        string executable,
        SmokeFixture fixture,
        string transitionCommand,
        string relationId,
        long expectedRevision,
        string? stateReason)
    {
        var result = await TransitionS2T4RelationResultAsync(executable, fixture, transitionCommand, relationId, expectedRevision, stateReason).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("RELATION stored revision=", StringComparison.Ordinal),
            $"Relation {transitionCommand} did not store {relationId} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
        return result;
    }

    private static async Task<CommandResult> TransitionS2T4RelationResultAsync(
        string executable,
        SmokeFixture fixture,
        string transitionCommand,
        string relationId,
        long expectedRevision,
        string? stateReason)
    {
        var arguments = new List<string>
        {
            "owner", transitionCommand, "--control-pipe", fixture.ControlPipe,
            "--resource-id", relationId,
            "--expected-revision", expectedRevision.ToString(CultureInfo.InvariantCulture)
        };
        if (stateReason is not null)
        {
            var input = Path.Combine(fixture.OutputDirectory, "s2t4-transition-" + Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllTextAsync(input, S2T4TransitionDraft(stateReason), Encoding.UTF8).ConfigureAwait(false);
            arguments.Add("--input-file");
            arguments.Add(input);
        }

        return await RunOwnerCommandAsync(executable, arguments.ToArray()).ConfigureAwait(false);
    }

    private static async Task<CommandResult> ReadS2T4RelationAsync(string executable, SmokeFixture fixture, string relationId)
    {
        return await RunOwnerCommandAsync(executable,
            "owner", "relation-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", relationId).ConfigureAwait(false);
    }

    /// <summary>
    /// Issues and redeems a grant that holds <c>link.create</c> for one
    /// subject/destination pair plus <c>relation.read</c> on one relation.
    /// </summary>
    private static async Task<S2T4Session> IssueS2T4LinkGrantAsync(
        string executable,
        SmokeFixture fixture,
        string name,
        string relationId,
        string fromResourceId,
        string toResourceId) =>
        await IssueS2T4GrantAsync(executable, fixture, name,
            resourceIds: new[] { relationId, fromResourceId },
            operations: "link.create,relation.read,memory.read,source.read",
            destinationResourceIds: new[] { toResourceId }).ConfigureAwait(false);

    /// <summary>
    /// Issues and redeems a grant that holds <c>relation.read</c> on one
    /// relation plus read operations on the given endpoint resources.
    /// </summary>
    private static async Task<S2T4Session> IssueS2T4RelationReadGrantAsync(
        string executable,
        SmokeFixture fixture,
        string name,
        string relationId,
        params string[] endpointIds) =>
        await IssueS2T4GrantAsync(executable, fixture, name,
            resourceIds: new[] { relationId }.Concat(endpointIds).ToArray(),
            operations: "relation.read,memory.read,source.read",
            destinationResourceIds: null).ConfigureAwait(false);

    /// <summary>
    /// Issues and redeems an item-only grant: no source read operation, so a
    /// relation whose other endpoint is a registered source stays denied.
    /// </summary>
    private static async Task<S2T4Session> IssueS2T4MemoryOnlyGrantAsync(
        string executable,
        SmokeFixture fixture,
        string name,
        string relationId,
        string itemId) =>
        await IssueS2T4GrantAsync(executable, fixture, name,
            resourceIds: new[] { relationId, itemId },
            operations: "relation.read,memory.read",
            destinationResourceIds: null).ConfigureAwait(false);

    /// <summary>
    /// Issues and redeems a zone-resolved grant for the zone-change negative.
    /// </summary>
    private static async Task<S2T4Session> IssueS2T4ZoneGrantAsync(
        string executable,
        SmokeFixture fixture,
        string name,
        string zoneId) =>
        await IssueS2T4GrantAsync(executable, fixture, name,
            resourceIds: null,
            operations: "relation.read,memory.read,source.read",
            destinationResourceIds: null,
            zoneId: zoneId).ConfigureAwait(false);

    private static async Task<S2T4Session> IssueS2T4GrantAsync(
        string executable,
        SmokeFixture fixture,
        string name,
        string[]? resourceIds,
        string operations,
        string[]? destinationResourceIds,
        string? zoneId = null)
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O", CultureInfo.InvariantCulture);
        var handoff = Path.Combine(fixture.OutputDirectory, name + ".grant-handoff");
        var sessionFile = Path.Combine(fixture.OutputDirectory, name + ".session-handoff");
        var arguments = new List<string>
        {
            "owner", "grant", "--control-pipe", fixture.ControlPipe,
            "--operations", operations,
            "--expires-at-utc", expiry,
            "--handoff-file", handoff
        };
        if (zoneId is not null)
        {
            arguments.Add("--zone-id");
            arguments.Add(zoneId);
        }

        if (resourceIds is { Length: > 0 })
        {
            arguments.Add("--resource-ids");
            arguments.Add(string.Join(",", resourceIds));
        }

        if (destinationResourceIds is { Length: > 0 })
        {
            arguments.Add("--destination-resource-ids");
            arguments.Add(string.Join(",", destinationResourceIds));
        }

        var grant = await RunOwnerCommandWithInputAsync(executable, "ISSUE" + Environment.NewLine, arguments.ToArray()).ConfigureAwait(false);
        Require(grant.ExitCode == 0,
            $"The owner could not issue the synthetic S2-T4 grant {name} (exit={grant.ExitCode}; stderr={grant.StandardError.Trim()}).");
        VerifyOwnerHandoffProtection(handoff);
        var redeem = await RunOwnerCommandAsync(executable,
            "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
            "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
        Require(redeem.ExitCode == 0 && redeem.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
            $"Redemption failed for the synthetic S2-T4 grant {name} (exit={redeem.ExitCode}; stderr={redeem.StandardError.Trim()}).");
        VerifyOwnerHandoffProtection(sessionFile);
        return new S2T4Session(ParseSessionId(redeem.StandardOutput), sessionFile);
    }

    private static void VerifyS2T4CiphertextOnly(SmokeFixture fixture, int expectedResources)
    {
        foreach (var path in Directory.GetFiles(fixture.VaultDirectoryPath, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            Require(!text.Contains("Synthetic S2-T4", StringComparison.Ordinal),
                "Private relation/item/source plaintext leaked into a persisted vault file: " + Path.GetFileName(path));
            foreach (var forbidden in new[] { "s2t4-private-source-one-locator", "s2t4-private-source-two-locator" })
            {
                Require(!text.Contains(forbidden, StringComparison.Ordinal),
                    "A private source locator leaked into a persisted vault file: " + Path.GetFileName(path));
            }
        }

        var resources = Directory.GetFiles(fixture.VaultResourcesPath, "*.mbv", SearchOption.TopDirectoryOnly);
        Require(resources.Length == expectedResources,
            "The S2-T4 vault did not persist exactly the expected " + expectedResources + " encrypted resources (found " + resources.Length + ").");
        foreach (var file in resources)
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            Require(text.Contains("\"ciphertext\"", StringComparison.Ordinal) && text.Contains("\"nonce\"", StringComparison.Ordinal),
                "A vault resource is not in the authenticated envelope form: " + Path.GetFileName(file));
        }

        Require(!Directory.GetFiles(fixture.VaultDirectoryPath, "*", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(fixture.ServiceDirectoryPath, "*", SearchOption.AllDirectories))
            .Any(path => Path.GetFileName(path).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)),
            "The S2-T4 vault or service directory left an atomic-write temporary file.");

        foreach (var draft in Directory.GetFiles(fixture.OutputDirectory, "s2t4-*", SearchOption.TopDirectoryOnly))
        {
            File.Delete(draft);
        }

        Require(!Directory.GetFiles(fixture.OutputDirectory, "s2t4-*", SearchOption.TopDirectoryOnly).Any(),
            "Synthetic plaintext draft/handoff files remained outside the vault after the S2-T4 scenario.");
    }
}
