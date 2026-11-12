using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Core.Security;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string S16T3Query = "s16t3_relational_seed_marker";
    private const string S16T3ActReason = "Owner reviewed this synthetic relation candidate.";

    public static async Task RunS16T3RelationalExpansionAsync(string connectionsExecutable, string cliExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S16-T3 relational-search smoke is Windows-only.");
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
                "The S16-T3 synthetic vault could not be provisioned.");
            var unlock = await RunOwnerCommandWithInputAsync(
                cliExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0 && unlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The S16-T3 synthetic vault could not be unlocked.");

            var inside = await CreateFolderAsync(cliExecutable, fixture, "root", "inside", 0).ConfigureAwait(false);
            var outside = await CreateFolderAsync(cliExecutable, fixture, "root", "outside", inside.TreeRevision).ConfigureAwait(false);
            var treeRevision = outside.TreeRevision;
            async Task<S16T3File> ImportFileAsync(string parentId, string name, string content)
            {
                var input = await WriteWorkspaceInputAsync(
                    fixture, "s16t3-input-" + name, Encoding.UTF8.GetBytes(content)).ConfigureAwait(false);
                var imported = await ImportWorkspaceFileAsync(
                    cliExecutable, fixture, parentId, name, input, treeRevision).ConfigureAwait(false);
                treeRevision = imported.TreeRevision;
                return new S16T3File(imported.FileId, 1, imported.Sha256);
            }

            var seed = await ImportFileAsync(inside.FolderId, "seed.md", "Synthetic query seed: " + S16T3Query).ConfigureAwait(false);
            var firstHop = await ImportFileAsync(inside.FolderId, "first-hop.md", "Synthetic first-hop evidence, unrelated to the query.").ConfigureAwait(false);
            var refutation = await ImportFileAsync(inside.FolderId, "refutation.md", "Synthetic second-hop refutation, unrelated to the query.").ConfigureAwait(false);
            var thirdHop = await ImportFileAsync(inside.FolderId, "third-hop.md", "Synthetic candidate beyond the two-hop bound.").ConfigureAwait(false);
            var derived = await ImportFileAsync(inside.FolderId, "derived.md", "Synthetic derived evidence.").ConfigureAwait(false);
            var superseded = await ImportFileAsync(inside.FolderId, "superseded.md", "Synthetic superseded evidence.").ConfigureAwait(false);
            var addressed = await ImportFileAsync(inside.FolderId, "addressed.md", "Synthetic addressed evidence.").ConfigureAwait(false);
            var identity = await ImportFileAsync(inside.FolderId, "identity.md", "Synthetic identity-only neighbor.").ConfigureAwait(false);
            var applicable = await ImportFileAsync(inside.FolderId, "applicable.md", "Synthetic applicable evidence.").ConfigureAwait(false);
            var pending = await ImportFileAsync(inside.FolderId, "pending.md", "Synthetic pending neighbor.").ConfigureAwait(false);
            var rejected = await ImportFileAsync(inside.FolderId, "rejected.md", "Synthetic rejected neighbor.").ConfigureAwait(false);
            var retracted = await ImportFileAsync(inside.FolderId, "retracted.md", "Synthetic retracted neighbor.").ConfigureAwait(false);
            var stale = await ImportFileAsync(inside.FolderId, "stale.md", "Synthetic evidence that will become stale.").ConfigureAwait(false);
            var staleRoot = await ImportFileAsync(inside.FolderId, "stale-root.md", "Synthetic identity path to stale evidence.").ConfigureAwait(false);
            var identityHistory = await ImportFileAsync(inside.FolderId, "identity-history.md", "Synthetic identity link with an older content pin.").ConfigureAwait(false);
            var incoming = await ImportFileAsync(inside.FolderId, "incoming-only.md", "Synthetic incoming-only neighbor.").ConfigureAwait(false);
            var folderOnlyChild = await ImportFileAsync(inside.FolderId, "folder-only-child.md", "Folder membership is not relation evidence.").ConfigureAwait(false);
            var outsideFile = await ImportFileAsync(outside.FolderId, "private-outside.md", "S16T3_OUTSIDE_PRIVATE_MARKER").ConfigureAwait(false);
            var afterOutside = await ImportFileAsync(inside.FolderId, "after-outside.md", "S16T3_MUST_NOT_TRAVERSE_THROUGH_OUTSIDE").ConfigureAwait(false);
            var fanout = new[]
            {
                await ImportFileAsync(inside.FolderId, "fanout-a.md", "Synthetic irrelevant fan-out A.").ConfigureAwait(false),
                await ImportFileAsync(inside.FolderId, "fanout-b.md", "Synthetic irrelevant fan-out B.").ConfigureAwait(false),
                await ImportFileAsync(inside.FolderId, "fanout-c.md", "Synthetic irrelevant fan-out C.").ConfigureAwait(false),
                await ImportFileAsync(inside.FolderId, "fanout-d.md", "Synthetic irrelevant fan-out D.").ConfigureAwait(false)
            };

            object FileEndpoint(S16T3File file) => S16T2FileEndpoint(file.Id, file.Revision, file.Sha256);
            async Task CreateAcceptedAsync(string id, string kind, object from, object to)
            {
                var created = await CreateS16T3RelationAsync(
                    cliExecutable, fixture, id, kind, "proposed", from, to).ConfigureAwait(false);
                Require(created.ExitCode == 0, "The S16-T3 relation draft could not be created: " + id + ".");
                var accepted = await RunOwnerCommandAsync(
                    cliExecutable, "owner", "relation-accept", "--control-pipe", fixture.ControlPipe,
                    "--resource-id", id, "--expected-revision", "1", "--reason", S16T3ActReason).ConfigureAwait(false);
                Require(accepted.ExitCode == 0 && accepted.StandardOutput.Contains("state=accepted", StringComparison.Ordinal),
                    "The S16-T3 relation could not be accepted: " + id + ".");
            }

            await CreateAcceptedAsync("rel-s16t3-01-supports", "supports", FileEndpoint(seed), FileEndpoint(firstHop)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t3-02-contradicts", "contradicts", FileEndpoint(firstHop), FileEndpoint(refutation)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t3-03-derived", "derivedFrom", FileEndpoint(seed), FileEndpoint(derived)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t3-04-supersedes", "supersedes", FileEndpoint(seed), FileEndpoint(superseded)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t3-05-addresses", "addresses", FileEndpoint(seed), FileEndpoint(addressed)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t3-06-related", "relatedTo", FileEndpoint(seed), FileEndpoint(identity)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t3-07-applies-file", "appliesTo", FileEndpoint(seed), FileEndpoint(applicable)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t3-08-cycle-back", "relatedTo", FileEndpoint(firstHop), FileEndpoint(seed)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t3-09-cycle-forward", "relatedTo", FileEndpoint(refutation), FileEndpoint(firstHop)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t3-10-cycle-cross", "relatedTo", FileEndpoint(firstHop), FileEndpoint(refutation)).ConfigureAwait(false);
            foreach (var neighbor in fanout.Select((file, index) => (file, index)))
            {
                await CreateAcceptedAsync(
                    "rel-s16t3-fanout-" + neighbor.index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
                    "relatedTo", FileEndpoint(seed), FileEndpoint(neighbor.file)).ConfigureAwait(false);
            }

            var folderStatus = await RunOwnerCommandAsync(
                cliExecutable, "owner", "workspace-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(folderStatus.ExitCode == 0, "The live S16-T3 workspace status could not be read.");
            var folderEndpoint = S16T2FolderEndpoint(inside.FolderId, 1, WorkspaceNumber(folderStatus.StandardOutput, "revision"));
            await CreateAcceptedAsync("rel-s16t3-folder-applies", "appliesTo", FileEndpoint(seed), folderEndpoint).ConfigureAwait(false);

            var pendingDraft = await CreateS16T3RelationAsync(
                cliExecutable, fixture, "rel-s16t3-pending", "relatedTo", "proposed",
                FileEndpoint(seed), FileEndpoint(pending)).ConfigureAwait(false);
            Require(pendingDraft.ExitCode == 0, "The pending relation fixture could not be created.");

            var rejectedDraft = await CreateS16T3RelationAsync(
                cliExecutable, fixture, "rel-s16t3-rejected", "supports", "proposed",
                FileEndpoint(seed), FileEndpoint(rejected)).ConfigureAwait(false);
            Require(rejectedDraft.ExitCode == 0, "The rejected relation fixture could not be created.");
            var rejectedTransition = await RunOwnerCommandAsync(
                cliExecutable, "owner", "relation-reject", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "rel-s16t3-rejected", "--expected-revision", "1",
                "--reason", S16T3ActReason).ConfigureAwait(false);
            Require(rejectedTransition.ExitCode == 0 && rejectedTransition.StandardOutput.Contains("state=rejected", StringComparison.Ordinal),
                "The rejected relation fixture did not reach its terminal state.");

            await CreateAcceptedAsync("rel-s16t3-retracted", "supports", FileEndpoint(seed), FileEndpoint(retracted)).ConfigureAwait(false);
            var retractedTransition = await RunOwnerCommandAsync(
                cliExecutable, "owner", "relation-unlink", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "rel-s16t3-retracted", "--expected-revision", "2",
                "--reason", S16T3ActReason).ConfigureAwait(false);
            Require(retractedTransition.ExitCode == 0 && retractedTransition.StandardOutput.Contains("state=retracted", StringComparison.Ordinal),
                "The retracted relation fixture did not reach its explicit unlink state.");

            await CreateAcceptedAsync("rel-s16t3-stale-root", "relatedTo", FileEndpoint(seed), FileEndpoint(staleRoot)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t3-stale", "supports", FileEndpoint(staleRoot), FileEndpoint(stale)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t3-third-hop", "supports", FileEndpoint(refutation), FileEndpoint(thirdHop)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t3-identity-history", "relatedTo", FileEndpoint(seed), FileEndpoint(identityHistory)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t3-outside", "supports", FileEndpoint(seed), FileEndpoint(outsideFile)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t3-outside-transit", "relatedTo", FileEndpoint(outsideFile), FileEndpoint(afterOutside)).ConfigureAwait(false);
            await CreateAcceptedAsync("rel-s16t3-incoming", "supports", FileEndpoint(incoming), FileEndpoint(seed)).ConfigureAwait(false);

            async Task ReviseAsync(S16T3File file, string content, string inputName)
            {
                var input = await WriteWorkspaceInputAsync(fixture, inputName, Encoding.UTF8.GetBytes(content)).ConfigureAwait(false);
                var revised = await RunOwnerCommandAsync(
                    cliExecutable, "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                    "--file-id", file.Id, "--expected-content-revision", file.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--input-file", input).ConfigureAwait(false);
                Require(revised.ExitCode == 0 && WorkspaceNumber(revised.StandardOutput, "contentRevision") == file.Revision + 1,
                    "The S16-T3 synthetic source could not advance to its next content revision.");
                treeRevision = WorkspaceNumber(revised.StandardOutput, "revision");
            }

            await ReviseAsync(stale, "Revised content makes the accepted support stale.", "s16t3-stale-revision-2.txt").ConfigureAwait(false);
            await ReviseAsync(identityHistory, "Identity remains current while its old content pin stays auditable.", "s16t3-identity-revision-2.txt").ConfigureAwait(false);
            var staleRead = await RunOwnerCommandAsync(
                cliExecutable, "owner", "relation-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "rel-s16t3-stale").ConfigureAwait(false);
            Require(staleRead.ExitCode == 0 && staleRead.StandardOutput.Contains("review=needsReview", StringComparison.Ordinal),
                "The accepted content-sensitive edge did not preserve its stale review state.");
            var identityRead = await RunOwnerCommandAsync(
                cliExecutable, "owner", "relation-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "rel-s16t3-identity-history").ConfigureAwait(false);
            Require(identityRead.ExitCode == 0 && identityRead.StandardOutput.Contains("review=current", StringComparison.Ordinal),
                "The identity-only related_to edge became stale when its file content advanced.");

            var addressedRead = await RunOwnerCommandAsync(
                cliExecutable, "owner", "relation-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "rel-s16t3-05-addresses").ConfigureAwait(false);
            Require(addressedRead.ExitCode == 0 &&
                    addressedRead.StandardOutput.Contains("state=accepted", StringComparison.Ordinal) &&
                    addressedRead.StandardOutput.Contains("review=current", StringComparison.Ordinal),
                "The accepted addresses edge did not retain its current state.");
            var applicableRead = await RunOwnerCommandAsync(
                cliExecutable, "owner", "relation-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "rel-s16t3-07-applies-file").ConfigureAwait(false);
            Require(applicableRead.ExitCode == 0 &&
                    applicableRead.StandardOutput.Contains("state=accepted", StringComparison.Ordinal) &&
                    applicableRead.StandardOutput.Contains("review=current", StringComparison.Ordinal),
                "The accepted applies_to edge did not retain its current state.");

            await VerifyAgentChannelCannotReachHybridSearchAsync(fixture).ConfigureAwait(false);
            var page = await HybridSearchJsonAsync(
                cliExecutable, fixture, S16T3Query, inside.FolderId, modelDirectory: null, limit: 20).ConfigureAwait(false);
            var directHits = page.GetProperty("hits").EnumerateArray().ToArray();
            var relationCandidates = page.GetProperty("relationCandidates").EnumerateArray().ToArray();
            var expectedCurrent = new HashSet<string>(new[]
            {
                firstHop.Id, refutation.Id, derived.Id, superseded.Id, addressed.Id, identity.Id,
                applicable.Id, staleRoot.Id, fanout[0].Id, fanout[1].Id, fanout[2].Id, fanout[3].Id
            }, StringComparer.Ordinal);
            var expectedLabels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [firstHop.Id] = "first-hop.md",
                [refutation.Id] = "refutation.md",
                [derived.Id] = "derived.md",
                [superseded.Id] = "superseded.md",
                [addressed.Id] = "addressed.md",
                [identity.Id] = "identity.md",
                [applicable.Id] = "applicable.md",
                [staleRoot.Id] = "stale-root.md",
                [fanout[0].Id] = "fanout-a.md",
                [fanout[1].Id] = "fanout-b.md",
                [fanout[2].Id] = "fanout-c.md",
                [fanout[3].Id] = "fanout-d.md"
            };
            var returnedCurrent = relationCandidates
                .Select(candidate => candidate.GetProperty("fileId").GetString() ?? string.Empty)
                .ToHashSet(StringComparer.Ordinal);
            Require(page.GetProperty("total").GetInt32() == 1 &&
                    page.GetProperty("literalTotal").GetInt32() == 1 &&
                    page.GetProperty("semanticCandidateTotal").GetInt32() == 0 &&
                    directHits.Length == 1 &&
                    page.GetProperty("semanticStatus").GetString() == WorkspaceHybridSemanticStatus.AbstainedExactQuery,
                "Relational expansion changed the existing hybrid seed counts, exact-query semantics, or direct hit page.");
            Require(page.GetProperty("relationCandidateCount").GetInt32() == expectedCurrent.Count &&
                    page.GetProperty("relationExpansionBoundReached").GetBoolean() &&
                    returnedCurrent.SetEquals(expectedCurrent),
                "The current-scope relation expansion missed multi-hop evidence/refutation or exposed an ineligible endpoint (count=" +
                page.GetProperty("relationCandidateCount").GetInt32() + "; bound=" +
                page.GetProperty("relationExpansionBoundReached").GetBoolean() + "; expected=" +
                string.Join(",", expectedLabels.OrderBy(label => label.Value, StringComparer.Ordinal)
                    .Select(label => label.Value + "=" + label.Key)) + "; actual=" +
                string.Join(",", relationCandidates.Select(candidate =>
                    candidate.GetProperty("path").GetString() + "=" + candidate.GetProperty("fileId").GetString())) + ").");
            Require(relationCandidates.All(candidate => candidate.GetProperty("path").GetString()!.StartsWith("inside/", StringComparison.Ordinal)) &&
                    relationCandidates.All(candidate =>
                        candidate.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
                            .SetEquals(new[] { "fileId", "path", "contentRevision", "fileSha256", "isCurrent" })),
                "A relation candidate disclosed traversal metadata, relation context, or a path outside the exact owner folder scope.");
            var pageText = page.GetRawText();
            foreach (var privateValue in new[]
                     {
                        outsideFile.Id, "outside/private-outside.md", "S16T3_OUTSIDE_PRIVATE_MARKER",
                        afterOutside.Id, "inside/after-outside.md", "S16T3_MUST_NOT_TRAVERSE_THROUGH_OUTSIDE",
                        incoming.Id, "inside/incoming-only.md", folderOnlyChild.Id, "inside/folder-only-child.md",
                        stale.Id, "inside/stale.md", thirdHop.Id, "inside/third-hop.md",
                        "rel-s16t3-outside", "synthetic relation candidate",
                        "Synthetic relation reason must not appear in candidate discovery."
                     })
            {
                Require(!pageText.Contains(privateValue, StringComparison.Ordinal),
                    "A scope, relation-path, title, or reason disclosure appeared in the hybrid response: " + privateValue + ".");
            }
            Console.WriteLine("PASS S16-T3 owner CLI/service discovers accepted two-hop supports/contradicts candidates while preserving exact hybrid counts and stored edge direction");

            var allRevisions = await HybridSearchJsonAsync(
                cliExecutable, fixture, S16T3Query, inside.FolderId, modelDirectory: null,
                current: WorkspaceHybridSearchCurrent.All, limit: 20).ConfigureAwait(false);
            var allCandidates = allRevisions.GetProperty("relationCandidates").EnumerateArray().ToArray();
            var historyCandidate = allCandidates.SingleOrDefault(candidate =>
                string.Equals(candidate.GetProperty("fileId").GetString(), identityHistory.Id, StringComparison.Ordinal));
            Require(allRevisions.GetProperty("relationCandidateCount").GetInt32() == expectedCurrent.Count + 1 &&
                    allRevisions.GetProperty("relationExpansionBoundReached").GetBoolean() &&
                    historyCandidate.ValueKind == JsonValueKind.Object &&
                    historyCandidate.GetProperty("contentRevision").GetInt64() == identityHistory.Revision &&
                    !historyCandidate.GetProperty("isCurrent").GetBoolean() &&
                    !allCandidates.Any(candidate => candidate.GetProperty("fileId").GetString() == stale.Id) &&
                    !allCandidates.Any(candidate => candidate.GetProperty("fileId").GetString() == thirdHop.Id),
                "Currentness selection, identity-only old pins, stale evidence, or the two-hop bound changed.");
            Console.WriteLine("PASS S16-T3 preserves exact-revision currentness, identity-only related_to and stale accepted evidence semantics");

            var bounded = await HybridSearchJsonAsync(
                cliExecutable, fixture, S16T3Query, inside.FolderId, modelDirectory: null, limit: 2).ConfigureAwait(false);
            var boundedCandidates = bounded.GetProperty("relationCandidates").EnumerateArray().ToArray();
            Require(boundedCandidates.Length == 2 &&
                    bounded.GetProperty("relationCandidateCount").GetInt32() == 2 &&
                    bounded.GetProperty("relationExpansionBoundReached").GetBoolean() &&
                    boundedCandidates.All(candidate => !candidate.TryGetProperty("evidence", out _) &&
                        !candidate.TryGetProperty("reason", out _) && !candidate.TryGetProperty("relationId", out _)),
                "Relation fan-out escaped the request candidate/context bound or disclosed relation-specific context.");
            Console.WriteLine("PASS S16-T3 bounds fan-out candidates and context, and cycle edges do not duplicate candidates or loop");

            var lockResult = await RunOwnerCommandAsync(
                cliExecutable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockResult.ExitCode == 0 && lockResult.StandardOutput.Contains("VAULT locked", StringComparison.Ordinal),
                "The S16-T3 synthetic vault could not be locked.");
            var lockedSearch = await RunOwnerCommandAsync(
                cliExecutable,
                HybridArguments(fixture, S16T3Query, inside.FolderId, modelDirectory: null, format: "json", limit: 20)).ConfigureAwait(false);
            Require(lockedSearch.ExitCode != 0 &&
                    lockedSearch.StandardError.Contains("vault_locked", StringComparison.Ordinal) &&
                    !lockedSearch.StandardOutput.Contains("relationCandidates", StringComparison.Ordinal) &&
                    !lockedSearch.StandardOutput.Contains(firstHop.Id, StringComparison.Ordinal),
                "A locked hybrid search exposed relational candidate data.");
            Console.WriteLine("PASS S16-T3 relation expansion remains inside the owner unlock/lock boundary");

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

    private static async Task<CommandResult> CreateS16T3RelationAsync(
        string cliExecutable,
        SmokeFixture fixture,
        string relationId,
        string kind,
        string state,
        object from,
        object to)
    {
        var bytes = S16T2RelationDraft(
            kind, state, from, to,
            "Synthetic relation reason must not appear in candidate discovery.", "owner");
        var path = Path.Combine(fixture.OutputDirectory, "s16t3-relation-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            return await RunOwnerCommandAsync(
                cliExecutable, "owner", "relation-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", relationId, "--zone-id", "s16t3-zone", "--input-file", path).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed record S16T3File(string Id, long Revision, string Sha256);
}
