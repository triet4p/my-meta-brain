using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    /// <summary>
    /// One S2-T6 collection membership change. Membership lives only in the
    /// encrypted owner scope state, so every change needs the owner's typed
    /// confirmation exactly like a grant issuance.
    /// </summary>
    private static async Task<CommandResult> SetS2T6CollectionAsync(
        string executable,
        SmokeFixture fixture,
        string collectionId,
        params string[] resourceIds)
    {
        var result = await RunOwnerCommandWithInputAsync(executable, "SET" + Environment.NewLine,
            "owner", "collection-set", "--control-pipe", fixture.ControlPipe,
            "--collection-id", collectionId,
            "--resource-ids", string.Join(",", resourceIds)).ConfigureAwait(false);
        Require(result.ExitCode == 0 &&
                result.StandardOutput.Contains($"COLLECTION set id={collectionId}", StringComparison.Ordinal),
            $"The owner could not change the S2-T6 collection membership {collectionId} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
        return result;
    }

    private static async Task<CommandResult> ListS2T6CollectionsAsync(string executable, SmokeFixture fixture)
    {
        var result = await RunOwnerCommandAsync(executable,
            "owner", "collections", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(result.ExitCode == 0,
            $"The owner collection listing failed for S2-T6 (exit={result.ExitCode}; stderr={result.StandardError.Trim()}).");
        return result;
    }

    /// <summary>
    /// Issues and redeems an owner grant over exactly one selector: a
    /// collection, a zone, or explicit resource IDs. The concrete resource
    /// revisions are frozen at preview time.
    /// </summary>
    private static async Task<S2T4Session> IssueS2T6GrantAsync(
        string executable,
        SmokeFixture fixture,
        string name,
        string operations,
        string? collectionId = null,
        string? zoneId = null,
        string[]? resourceIds = null)
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
        if (collectionId is not null)
        {
            arguments.Add("--collection-id");
            arguments.Add(collectionId);
        }

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

        var grant = await RunOwnerCommandWithInputAsync(executable, "ISSUE" + Environment.NewLine, arguments.ToArray()).ConfigureAwait(false);
        Require(grant.ExitCode == 0,
            $"The owner could not issue the synthetic S2-T6 grant {name} (exit={grant.ExitCode}; stdout={grant.StandardOutput.Trim()}; stderr={grant.StandardError.Trim()}).");
        VerifyOwnerHandoffAcl(handoff);
        var redeem = await RunOwnerCommandAsync(executable,
            "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
            "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
        Require(redeem.ExitCode == 0 && redeem.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
            $"Redemption failed for the synthetic S2-T6 grant {name} (exit={redeem.ExitCode}; stderr={redeem.StandardError.Trim()}).");
        VerifyOwnerHandoffAcl(sessionFile);
        return new S2T4Session(ParseSessionId(redeem.StandardOutput), sessionFile);
    }

    /// <summary>
    /// Browses the collection view through the real owner CLI on the agent
    /// channel: the only consumer surface for the permission-filtered view.
    /// </summary>
    private static async Task<CommandResult> AgentS2T6CollectionViewAsync(
        string executable,
        SmokeFixture fixture,
        S2T4Session session) =>
        await RunOwnerCommandAsync(executable,
            "owner", "agent-collection-view", "--agent-pipe", fixture.AgentPipe,
            "--session-file", session.SessionFile).ConfigureAwait(false);

    private static string S2T6ItemDraft(string kind, string title, string body, DateTimeOffset now, string? level = "l1") =>
        JsonSerializer.Serialize(new
        {
            kind,
            author = "user",
            creationMethod = "directWrite",
            evidence = "unverified",
            level,
            title,
            body,
            sources = Array.Empty<object>(),
            occurredAtUtc = now,
            recordedAtUtc = now.AddMinutes(1),
            applicability = (object?)null
        });

    private static string S2T6SourceDraft(string title, string locator, string version, DateTimeOffset now) =>
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

    private static async Task WriteS2T6ItemDraftAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string zoneId,
        string draftJson,
        long expectedRevision)
    {
        var result = await WriteS2T6ItemDraftResultAsync(executable, fixture, resourceId, zoneId, draftJson).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"MEMORY stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Memory create did not store {resourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}; draft={draftJson}).");
    }

    private static async Task<CommandResult> WriteS2T6ItemDraftResultAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string zoneId,
        string draftJson)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t6-item-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
    }

    private static async Task ReviseS2T6ItemDraftAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string zoneId,
        string draftJson,
        long writeRevision,
        long expectedRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t6-item-revise-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--expected-revision", writeRevision.ToString(CultureInfo.InvariantCulture),
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"MEMORY stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Memory revise did not store {resourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
    }

    private static async Task WriteS2T6SourceDraftAsync(
        string executable,
        SmokeFixture fixture,
        string sourceId,
        string zoneId,
        string draftJson,
        long expectedRevision)
    {
        var result = await WriteS2T6SourceDraftResultAsync(executable, fixture, sourceId, zoneId, draftJson).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"SOURCE stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Source create did not store {sourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
    }

    private static async Task<CommandResult> WriteS2T6SourceDraftResultAsync(
        string executable,
        SmokeFixture fixture,
        string sourceId,
        string zoneId,
        string draftJson)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t6-source-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "source-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", sourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
    }

    private static async Task CreateS2T6RelationAsync(
        string executable,
        SmokeFixture fixture,
        string relationId,
        string zoneId,
        string draftJson)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t6-relation-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "relation-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", relationId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("RELATION stored revision=1", StringComparison.Ordinal),
            $"Relation create did not store {relationId} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
    }

    private static async Task<CommandResult> CreateS2T6LifecycleAsync(
        string executable,
        SmokeFixture fixture,
        string subjectId,
        string draftJson)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t6-lifecycle-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "lifecycle-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", subjectId,
            "--input-file", input).ConfigureAwait(false);
    }

    private static async Task<CommandResult> RetainS2T6LifecycleAsync(
        string executable,
        SmokeFixture fixture,
        string subjectId,
        long expectedRevision)
    {
        return await RunOwnerCommandAsync(executable,
            "owner", "lifecycle-retain", "--control-pipe", fixture.ControlPipe,
            "--resource-id", subjectId,
            "--expected-revision", expectedRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
    }

    private static async Task ReviseS2T6SourceDraftAsync(
        string executable,
        SmokeFixture fixture,
        string sourceId,
        string zoneId,
        string draftJson,
        long writeRevision,
        long expectedRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t6-source-revise-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "source-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", sourceId, "--zone-id", zoneId,
            "--expected-revision", writeRevision.ToString(CultureInfo.InvariantCulture),
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"SOURCE stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Source revise did not store {sourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
    }

    private static async Task<CommandResult> ReadS2T6MemoryItemAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        long? revision = null)
    {
        var arguments = new List<string>
        {
            "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId
        };
        if (revision is not null)
        {
            arguments.Add("--resource-revision");
            arguments.Add(revision.Value.ToString(CultureInfo.InvariantCulture));
        }

        return await RunOwnerCommandAsync(executable, arguments.ToArray()).ConfigureAwait(false);
    }

    private static async Task<CommandResult> ReadS2T6SourceAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        long? revision = null)
    {
        var arguments = new List<string>
        {
            "owner", "source-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId
        };
        if (revision is not null)
        {
            arguments.Add("--resource-revision");
            arguments.Add(revision.Value.ToString(CultureInfo.InvariantCulture));
        }

        return await RunOwnerCommandAsync(executable, arguments.ToArray()).ConfigureAwait(false);
    }

    private static async Task<CommandResult> ReadS2T6RetainedAsync(
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

    /// <summary>
    /// Verifies the private organization metadata (collection membership) and
    /// every private item body stayed ciphertext at rest, that the persisted
    /// resource set is exactly the expected one, and that no atomic-write
    /// temporary file or synthetic plaintext draft survived the scenario.
    /// </summary>
    private static void VerifyS2T6CiphertextOnly(SmokeFixture fixture, int expectedResources)
    {
        foreach (var path in Directory.GetFiles(fixture.VaultDirectoryPath, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            foreach (var forbidden in new[]
            {
                "Synthetic S2-T6",
                "s2t6-collection-alpha",
                "s2t6-collection-beta",
                "s2t6-private-locator"
            })
            {
                Require(!text.Contains(forbidden, StringComparison.Ordinal),
                    "Private S2-T6 organization or item plaintext leaked into a persisted vault file: " + Path.GetFileName(path));
            }
        }

        var resources = Directory.GetFiles(fixture.VaultResourcesPath, "*.mbv", SearchOption.TopDirectoryOnly);
        Require(resources.Length == expectedResources,
            "The S2-T6 vault did not persist exactly the expected " + expectedResources + " encrypted resources (found " + resources.Length + ": " +
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
            "The S2-T6 vault or service directory left an atomic-write temporary file.");

        foreach (var draft in Directory.GetFiles(fixture.OutputDirectory, "s2t6-*", SearchOption.TopDirectoryOnly))
        {
            File.Delete(draft);
        }

        Require(!Directory.GetFiles(fixture.OutputDirectory, "s2t6-*", SearchOption.TopDirectoryOnly).Any(),
            "Synthetic plaintext draft/handoff files remained outside the vault after the S2-T6 scenario.");
    }
}
