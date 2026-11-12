using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MetaBrain.Core.Security;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string S16T4WhyQuery = "Why did we choose the retention policy";
    private const string S16T4ActReason = "Owner accepted this synthetic ranking relation.";

    public static async Task RunS16T4RelationalRankingAsync(string connectionsExecutable, string cliExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S16-T4 relational-ranking smoke is Windows-only.");
        }

        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        try
        {
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var passphrase = CreateSyntheticPassphrase();
            var provision = await RunOwnerCommandWithInputAsync(
                cliExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The S16-T4 synthetic vault could not be provisioned.");
            var unlock = await RunOwnerCommandWithInputAsync(
                cliExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0 && unlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The S16-T4 synthetic vault could not be unlocked.");

            var inside = await CreateFolderAsync(cliExecutable, fixture, "root", "inside", 0).ConfigureAwait(false);
            var outside = await CreateFolderAsync(cliExecutable, fixture, "root", "outside", inside.TreeRevision).ConfigureAwait(false);
            var treeRevision = outside.TreeRevision;
            async Task<S16T4File> ImportFileAsync(string parentId, string name, string content)
            {
                var input = await WriteWorkspaceInputAsync(
                    fixture, "s16t4-input-" + Guid.NewGuid().ToString("N"), Encoding.UTF8.GetBytes(content)).ConfigureAwait(false);
                var imported = await ImportWorkspaceFileAsync(
                    cliExecutable, fixture, parentId, name, input, treeRevision).ConfigureAwait(false);
                treeRevision = imported.TreeRevision;
                return new S16T4File(imported.FileId, 1, imported.Sha256);
            }

            async Task<S16T4File> ReviseAsync(S16T4File file, string content)
            {
                var input = await WriteWorkspaceInputAsync(
                    fixture, "s16t4-revise-" + Guid.NewGuid().ToString("N"), Encoding.UTF8.GetBytes(content)).ConfigureAwait(false);
                var revised = await RunOwnerCommandAsync(
                    cliExecutable, "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                    "--file-id", file.Id,
                    "--expected-content-revision", file.Revision.ToString(CultureInfo.InvariantCulture),
                    "--input-file", input).ConfigureAwait(false);
                Require(revised.ExitCode == 0 &&
                        WorkspaceNumber(revised.StandardOutput, "contentRevision") == file.Revision + 1,
                    "An S16-T4 synthetic source could not advance to its next content revision.");
                treeRevision = WorkspaceNumber(revised.StandardOutput, "revision");
                return new S16T4File(
                    file.Id,
                    file.Revision + 1,
                    WorkspaceToken(revised.StandardOutput, "sha256"));
            }

            var seed = await ImportFileAsync(
                inside.FolderId, S16T4WhyQuery + ".md", "Synthetic question seed for relational ranking.").ConfigureAwait(false);
            var support = await ImportFileAsync(
                inside.FolderId, "support-evidence.md", "S16T4_SUPPORT_EVIDENCE").ConfigureAwait(false);
            var contradiction = await ImportFileAsync(
                inside.FolderId, "contradiction-evidence.md", "S16T4_CONTRADICTION_EVIDENCE").ConfigureAwait(false);
            var bridge = await ImportFileAsync(
                inside.FolderId, "bridge-evidence.md", "S16T4_BRIDGE_EVIDENCE").ConfigureAwait(false);
            var deep = await ImportFileAsync(
                inside.FolderId, "deep-evidence.md", "S16T4_DEEP_EVIDENCE").ConfigureAwait(false);
            var duplicate = await ImportFileAsync(
                inside.FolderId, "duplicate-evidence.md", "S16T4_DUPLICATE_EVIDENCE").ConfigureAwait(false);
            var related = await ImportFileAsync(
                inside.FolderId, "related-identity.md", "S16T4_RELATED_IDENTITY").ConfigureAwait(false);
            var older = await ImportFileAsync(
                inside.FolderId, "aa-old.md", "S16T4_OLDER_REVISION_ONE").ConfigureAwait(false);
            var newer = await ImportFileAsync(
                inside.FolderId, "zz-new.md", "S16T4_NEWER_REVISION_ONE").ConfigureAwait(false);
            newer = await ReviseAsync(newer, "S16T4_NEWER_REVISION_TWO").ConfigureAwait(false);
            var identityHistory = await ImportFileAsync(
                inside.FolderId, "identity-history.md", "S16T4_IDENTITY_HISTORY_ONE").ConfigureAwait(false);
            var outsideFile = await ImportFileAsync(
                outside.FolderId, "outside-private.md", "S16T4_OUTSIDE_PRIVATE_MARKER").ConfigureAwait(false);

            object FileEndpoint(S16T4File file) => S16T2FileEndpoint(file.Id, file.Revision, file.Sha256);
            async Task CreateAcceptedAsync(string id, string kind, object from, object to)
            {
                var created = await CreateS16T3RelationAsync(
                    cliExecutable, fixture, id, kind, "proposed", from, to, S16T4ActReason).ConfigureAwait(false);
                Require(created.ExitCode == 0, "The S16-T4 relation draft could not be created: " + id + ".");
                var accepted = await RunOwnerCommandAsync(
                    cliExecutable, "owner", "relation-accept", "--control-pipe", fixture.ControlPipe,
                    "--resource-id", id, "--expected-revision", "1", "--reason", S16T4ActReason).ConfigureAwait(false);
                Require(accepted.ExitCode == 0 && accepted.StandardOutput.Contains("state=accepted", StringComparison.Ordinal),
                    "The S16-T4 relation could not be accepted: " + id + ".");
            }

            await CreateAcceptedAsync("rel-s16t4-support", "supports", FileEndpoint(seed), FileEndpoint(support)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t4-contradiction", "contradicts", FileEndpoint(seed), FileEndpoint(contradiction)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t4-bridge", "supports", FileEndpoint(seed), FileEndpoint(bridge)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t4-deep", "derivedFrom", FileEndpoint(bridge), FileEndpoint(deep)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t4-duplicate-b", "supports", FileEndpoint(seed), FileEndpoint(duplicate)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t4-duplicate-a", "supports", FileEndpoint(seed), FileEndpoint(duplicate)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t4-related", "relatedTo", FileEndpoint(seed), FileEndpoint(related)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t4-01-older", "supports", FileEndpoint(seed), FileEndpoint(older)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t4-02-newer", "supports", FileEndpoint(seed), FileEndpoint(newer)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t4-identity-history", "relatedTo", FileEndpoint(seed), FileEndpoint(identityHistory)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t4-outside", "supports", FileEndpoint(seed), FileEndpoint(outsideFile)).ConfigureAwait(false);
            identityHistory = await ReviseAsync(identityHistory, "S16T4_IDENTITY_HISTORY_TWO").ConfigureAwait(false);

            await VerifyAgentChannelCannotReachHybridSearchAsync(fixture).ConfigureAwait(false);

            var process = service.Process;
            var memoryBefore = ReadS15T5ProcessSample(process);
            var measured = Stopwatch.StartNew();
            var page = await HybridSearchJsonAsync(
                cliExecutable, fixture, S16T4WhyQuery, inside.FolderId, modelDirectory: null,
                field: WorkspaceHybridSearchFields.Name, limit: 20).ConfigureAwait(false);

            var directHits = page.GetProperty("hits").EnumerateArray().ToArray();
            var ranked = page.GetProperty("rankedEvidence").EnumerateArray().ToArray();
            var rawCandidates = page.GetProperty("relationCandidates").EnumerateArray().ToArray();
            Require(page.GetProperty("total").GetInt32() == 1 &&
                    page.GetProperty("literalTotal").GetInt32() == 1 &&
                    page.GetProperty("semanticCandidateTotal").GetInt32() == 0 &&
                    directHits.Length == 1 &&
                    string.Equals(directHits[0].GetProperty("path").GetString(), "inside/" + S16T4WhyQuery + ".md", StringComparison.Ordinal) &&
                    string.Equals(page.GetProperty("semanticStatus").GetString(),
                        WorkspaceHybridSemanticStatus.NotApplicableField, StringComparison.Ordinal),
                "S16-T4 changed the direct hit page, counts, or field-scoped semantic status.");

            var paths = ranked
                .Select(entry => entry.GetProperty("path").GetString() ?? string.Empty)
                .ToArray();
            Require(ranked.Select(entry => entry.GetProperty("rank").GetInt32()).SequenceEqual(
                        Enumerable.Range(1, ranked.Length)),
                "Ranked evidence is not one contiguous deterministic rank order.");
            Require(paths[0] == "inside/" + S16T4WhyQuery + ".md" &&
                    ranked[0].GetProperty("directRank").GetInt32() == 1 &&
                    ranked[0].GetProperty("evidenceClass").GetString() == WorkspaceHybridEvidenceClass.LiteralMatch &&
                    ranked[0].GetProperty("relationPath").GetArrayLength() == 0,
                "The literal direct hit was not ranked first or its direct identity was not explained.");

            var contradictionEntry = ranked.SingleOrDefault(entry =>
                entry.GetProperty("path").GetString() == "inside/contradiction-evidence.md");
            var supportEntry = ranked.SingleOrDefault(entry =>
                entry.GetProperty("path").GetString() == "inside/support-evidence.md");
            Require(contradictionEntry.ValueKind == JsonValueKind.Object &&
                    contradictionEntry.GetProperty("evidenceClass").GetString() ==
                    WorkspaceHybridEvidenceClass.ContradictingEvidence &&
                    contradictionEntry.GetProperty("reasons").EnumerateArray().Any(reason =>
                        reason.GetString() == "relation_contradicts") &&
                    contradictionEntry.GetProperty("directRank").ValueKind == JsonValueKind.Null &&
                    contradictionEntry.GetProperty("relationPath").GetArrayLength() == 1,
                "A contradiction was dropped, reclassified, or left without its stored path and reason.");
            Require(supportEntry.ValueKind == JsonValueKind.Object &&
                    Array.IndexOf(paths, "inside/contradiction-evidence.md") <
                    Array.IndexOf(paths, "inside/support-evidence.md"),
                "A why/decision question did not prioritise the explicit contradiction/support evidence over identity links.");

            var deepEntry = ranked.SingleOrDefault(entry =>
                entry.GetProperty("path").GetString() == "inside/deep-evidence.md");
            Require(deepEntry.ValueKind == JsonValueKind.Object &&
                    deepEntry.GetProperty("relationPath").GetArrayLength() == 2 &&
                    deepEntry.GetProperty("relationPath")[0].GetProperty("kind").GetString() == "supports" &&
                    deepEntry.GetProperty("relationPath")[1].GetProperty("kind").GetString() == "derived_from" &&
                    deepEntry.GetProperty("relationPath")[1].GetProperty("fromPath").GetString() == "inside/bridge-evidence.md",
                "A two-hop relational evidence path was not exposed hop-by-hop.");

            var duplicateEntries = ranked.Where(entry =>
                entry.GetProperty("path").GetString() == "inside/duplicate-evidence.md").ToArray();
            Require(duplicateEntries.Length == 1 &&
                    duplicateEntries[0].GetProperty("reasons").GetArrayLength() == 1 &&
                    duplicateEntries[0].GetProperty("reasons")[0].GetString() == "relation_supports" &&
                    duplicateEntries[0].GetProperty("relationPath").GetArrayLength() == 1,
                "Two accepted links to the same source produced duplicate evidence or an inflated reason set.");

            var olderRank = Array.IndexOf(paths, "inside/aa-old.md");
            var newerRank = Array.IndexOf(paths, "inside/zz-new.md");
            Require(olderRank > 0 && newerRank > 0 && olderRank < newerRank &&
                    ranked[newerRank].GetProperty("contentRevision").GetInt64() == 2 &&
                    ranked[olderRank].GetProperty("contentRevision").GetInt64() == 1,
                "A newer content revision outranked an older peer solely by recency.");

            Require(ranked.All(entry =>
                {
                    var names = entry.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
                    return names.SetEquals(new[]
                    {
                        "rank", "fileId", "path", "contentRevision", "fileSha256", "isCurrent",
                        "evidenceClass", "reasons", "directRank", "relationPath"
                    });
                }) &&
                ranked.All(entry => entry.GetProperty("path").GetString()!.StartsWith("inside/", StringComparison.Ordinal)),
                "Ranked evidence exposed a score/confidence field or an endpoint outside the owner folder scope.");

            var pageText = page.GetRawText();
            Require(!pageText.Contains(outsideFile.Id, StringComparison.Ordinal) &&
                    !pageText.Contains("outside/outside-private.md", StringComparison.Ordinal) &&
                    !pageText.Contains("S16T4_OUTSIDE_PRIVATE_MARKER", StringComparison.Ordinal) &&
                    !pageText.Contains("rel-s16t4-outside", StringComparison.Ordinal) &&
                    !pageText.Contains(S16T4ActReason, StringComparison.Ordinal),
                "An out-of-scope endpoint or a stored relation reason leaked into ranked evidence.");

            var directEvidenceCount = ranked.Count(entry => entry.GetProperty("directRank").ValueKind != JsonValueKind.Null);
            var relationEvidenceCount = ranked.Length - directEvidenceCount;
            Require(directEvidenceCount == 1 &&
                    relationEvidenceCount == rawCandidates.Length &&
                    rawCandidates.All(candidate =>
                    {
                        var names = candidate.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
                        return names.SetEquals(new[] { "fileId", "path", "contentRevision", "fileSha256", "isCurrent" });
                    }),
                "Ranked evidence and the retained bounded candidate discovery list disagree or changed candidate fields.");

            var relevantEvidence = new HashSet<string>(new[]
            {
                "inside/support-evidence.md", "inside/contradiction-evidence.md", "inside/bridge-evidence.md",
                "inside/deep-evidence.md", "inside/aa-old.md", "inside/zz-new.md", "inside/duplicate-evidence.md"
            }, StringComparer.Ordinal);
            var baselineRelevant = directHits
                .Select(hit => hit.GetProperty("path").GetString() ?? string.Empty)
                .Count(relevantEvidence.Contains);
            var treatedRelevant = paths.Count(relevantEvidence.Contains);
            Require(treatedRelevant == relevantEvidence.Count &&
                    baselineRelevant < treatedRelevant,
                "The frozen why/decision case did not improve relevant-evidence coverage over direct hits alone (baseline=" +
                baselineRelevant.ToString(CultureInfo.InvariantCulture) + "; treated=" +
                treatedRelevant.ToString(CultureInfo.InvariantCulture) + ").");
            Console.WriteLine(
                "OBSERVATION S16-T4 frozen why-case quality baselineDirectRelevant=" + baselineRelevant.ToString(CultureInfo.InvariantCulture) +
                "/" + relevantEvidence.Count.ToString(CultureInfo.InvariantCulture) +
                " rankedRelevant=" + treatedRelevant.ToString(CultureInfo.InvariantCulture) +
                "/" + relevantEvidence.Count.ToString(CultureInfo.InvariantCulture) +
                " baselineRecall=" + (baselineRelevant / (double)relevantEvidence.Count).ToString("F3", CultureInfo.InvariantCulture) +
                " rankedRecall=" + (treatedRelevant / (double)relevantEvidence.Count).ToString("F3", CultureInfo.InvariantCulture) +
                " delta=" + ((treatedRelevant - baselineRelevant) / (double)relevantEvidence.Count).ToString("F3", CultureInfo.InvariantCulture) +
                " contradictionRetained=true duplicatePathEntries=1; no threshold or confidence score is asserted");

            var bounded = await HybridSearchJsonAsync(
                cliExecutable, fixture, S16T4WhyQuery, inside.FolderId, modelDirectory: null,
                field: WorkspaceHybridSearchFields.Name, limit: 2).ConfigureAwait(false);
            var boundedRanked = bounded.GetProperty("rankedEvidence").EnumerateArray().ToArray();
            Require(bounded.GetProperty("relationCandidateCount").GetInt32() == 2 &&
                    bounded.GetProperty("relationExpansionBoundReached").GetBoolean() &&
                    boundedRanked.Length == 3 &&
                    boundedRanked.Skip(1).Select(entry => entry.GetProperty("fileId").GetString()).Distinct(StringComparer.Ordinal).Count() == 2,
                "The request limit did not bound relation evidence or duplicate sources appeared at the bound.");
            Console.WriteLine("PASS S16-T4 bounds ranked relation evidence by the existing request limit");

            var allRevisions = await HybridSearchJsonAsync(
                cliExecutable, fixture, S16T4WhyQuery, inside.FolderId, modelDirectory: null,
                field: WorkspaceHybridSearchFields.Name, current: WorkspaceHybridSearchCurrent.All, limit: 20).ConfigureAwait(false);
            measured.Stop();
            var memoryAfter = ReadS15T5ProcessSample(process);
            var historyEntry = allRevisions.GetProperty("rankedEvidence").EnumerateArray()
                .SingleOrDefault(entry => entry.GetProperty("fileId").GetString() == identityHistory.Id);
            Require(historyEntry.ValueKind == JsonValueKind.Object &&
                    historyEntry.GetProperty("contentRevision").GetInt64() == 1 &&
                    !historyEntry.GetProperty("isCurrent").GetBoolean() &&
                    !ranked.Any(entry => entry.GetProperty("fileId").GetString() == identityHistory.Id),
                "Currentness selection or the identity-only pinned revision changed in ranked evidence.");
            Console.WriteLine("PASS S16-T4 preserves exact-revision currentness and identity-only relation pins");

            var textResult = await RunOwnerCommandAsync(
                cliExecutable,
                HybridArguments(
                    fixture, S16T4WhyQuery, inside.FolderId, modelDirectory: null, format: "text",
                    field: WorkspaceHybridSearchFields.Name, limit: 20)).ConfigureAwait(false);
            Require(textResult.ExitCode == 0 &&
                    textResult.StandardOutput.Contains("rankedEvidenceCount=", StringComparison.Ordinal) &&
                    textResult.StandardOutput.Contains("RANKED_EVIDENCE rank=", StringComparison.Ordinal) &&
                    textResult.StandardOutput.Contains("relation_contradicts", StringComparison.Ordinal) &&
                    textResult.StandardOutput.Contains("contradiction-evidence.md", StringComparison.Ordinal) &&
                    !textResult.StandardOutput.Contains("outside-private.md", StringComparison.Ordinal),
                "The owner CLI text surface did not render the ranked relation evidence or leaked an out-of-scope path.");
            Console.WriteLine("PASS S16-T4 owner CLI text output renders ranked reasons and paths");

            Console.WriteLine(
                "OBSERVATION S16-T4 resources cases=3 elapsedMs=" +
                measured.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture) +
                " servicePrivateBytesBefore=" + memoryBefore.PrivateBytes.ToString(CultureInfo.InvariantCulture) +
                " servicePrivateBytesAfter=" + memoryAfter.PrivateBytes.ToString(CultureInfo.InvariantCulture) +
                " serviceWorkingSetBefore=" + memoryBefore.WorkingSet.ToString(CultureInfo.InvariantCulture) +
                " serviceWorkingSetAfter=" + memoryAfter.WorkingSet.ToString(CultureInfo.InvariantCulture) +
                "; synthetic project-local fixture, no latency or memory gate implied");

            var lockResult = await RunOwnerCommandAsync(
                cliExecutable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockResult.ExitCode == 0 && lockResult.StandardOutput.Contains("VAULT locked", StringComparison.Ordinal),
                "The S16-T4 synthetic vault could not be locked.");
            var lockedSearch = await RunOwnerCommandAsync(
                cliExecutable,
                HybridArguments(
                    fixture, S16T4WhyQuery, inside.FolderId, modelDirectory: null, format: "json",
                    field: WorkspaceHybridSearchFields.Name, limit: 20)).ConfigureAwait(false);
            Require(lockedSearch.ExitCode != 0 &&
                    lockedSearch.StandardError.Contains("vault_locked", StringComparison.Ordinal) &&
                    !lockedSearch.StandardOutput.Contains("rankedEvidence", StringComparison.Ordinal) &&
                    !lockedSearch.StandardOutput.Contains(support.Id, StringComparison.Ordinal),
                "A locked hybrid search exposed ranked relational evidence.");
            Console.WriteLine("PASS S16-T4 ranked evidence remains inside the owner unlock/lock boundary");

            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private sealed record S16T4File(string Id, long Revision, string Sha256);
}
