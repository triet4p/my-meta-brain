using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string S16T5Zone = "s16t5-zone";
    private const string S16T5RelationReason = "Synthetic owner-selected relation evidence.";
    private const string S16T5OwnerReviewReason = "Synthetic owner removed and restored this relation.";
    private const string S16T5AnnotationText = "Synthetic owner-selected annotation passage.";

    public static async Task RunS16T5OwnerRelationWorkflowAsync(string connectionsExecutable, string cliExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S16-T5 owner relation CLI smoke is Windows-only.");
        }

        using var fixture = S12T5Fixture.Create();
        RunningService? live = null;
        try
        {
            live = await StartServiceAsync(connectionsExecutable, fixture.LiveSettingsPath).ConfigureAwait(false);
            var passphrase = CreateSyntheticPassphrase();
            var provision = await RunOwnerCommandWithInputAsync(
                cliExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The actual owner CLI did not provision the synthetic S16-T5 vault.");

            var treeRevision = await S12T5RevisionAsync(cliExecutable, fixture.ControlPipe).ConfigureAwait(false);
            var folder = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-mkdir", "--parent-id", "root", "--name", "s16t5-relations",
                "--expected-tree-revision", treeRevision).ConfigureAwait(false);
            Require(folder.ExitCode == 0, "The synthetic relation folder could not be created.");
            var folderId = WorkspaceToken(folder.StandardOutput, "id");
            treeRevision = WorkspaceNumber(folder.StandardOutput, "revision");

            var fileABytes = Encoding.UTF8.GetBytes("# Synthetic assertion\n" + S16T5AnnotationText + "\n");
            var evidenceBytes = Encoding.UTF8.GetBytes("Synthetic retained endpoint evidence.");
            var fileBBytes = Encoding.UTF8.GetBytes("# Synthetic evidence\n" + Encoding.UTF8.GetString(evidenceBytes) + "\nOriginal revision.\n");
            var fileA = await S12T5ImportAsync(cliExecutable, fixture, folderId,
                "s16t5-assertion.md", fileABytes, treeRevision).ConfigureAwait(false);
            var fileB = await S12T5ImportAsync(cliExecutable, fixture, folderId,
                "s16t5-evidence.md", fileBBytes, fileA.TreeRevision).ConfigureAwait(false);
            treeRevision = fileB.TreeRevision;
            var baselineTree = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false);
            Require(baselineTree.ExitCode == 0, "The imported workspace tree could not be listed before relation data was added.");
            var baselineNodeCount = WorkspaceNumber(baselineTree.StandardOutput, "nodes");

            var evidenceOffset = fileBBytes.AsSpan().IndexOf(evidenceBytes);
            var annotationOffset = fileABytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(S16T5AnnotationText));
            Require(evidenceOffset >= 0 && annotationOffset >= 0,
                "A synthetic workspace passage was not present in its original bytes.");

            const string supportRelationId = "rel-s16t5-support";
            const string folderRelationId = "rel-s16t5-folder-applies";
            var support = await CreateS16T5RelationAsync(
                cliExecutable, fixture, supportRelationId, "supports",
                S16T5FileEndpoint(fileA.FileId, 1, Sha256Hex(fileABytes)),
                S16T5PassageEndpoint(fileB.FileId, 1, evidenceOffset, evidenceBytes.Length, Sha256Hex(evidenceBytes)),
                S16T5RelationReason).ConfigureAwait(false);
            Require(support.ExitCode == 0 && support.StandardOutput.Contains("review=current", StringComparison.Ordinal) &&
                    support.StandardOutput.Contains("kind=workspacePassage revision=1", StringComparison.Ordinal),
                "The owner CLI did not create and inspect the exact pinned file-to-passage relation.");

            var folderRelation = await CreateS16T5RelationAsync(
                cliExecutable, fixture, folderRelationId, "appliesTo",
                S16T5FolderEndpoint(folderId, 1, treeRevision),
                S16T5FileEndpoint(fileA.FileId, 1, Sha256Hex(fileABytes)),
                "Synthetic applicability names this folder, not each child.").ConfigureAwait(false);
            Require(folderRelation.ExitCode == 0 && folderRelation.StandardOutput.Contains("kind=workspaceFolder", StringComparison.Ordinal),
                "The explicit folder applicability relation could not be created.");

            var fileNeighborhood = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "relation-list", "--workspace-node-id", fileA.FileId).ConfigureAwait(false);
            Require(fileNeighborhood.ExitCode == 0 && fileNeighborhood.StandardOutput.Contains("RELATIONS count=2", StringComparison.Ordinal) &&
                    CountRelationLines(fileNeighborhood.StandardOutput, supportRelationId) == 1 &&
                    CountRelationLines(fileNeighborhood.StandardOutput, folderRelationId) == 1,
                "The file neighborhood did not list each explicit relation exactly once.");
            var folderNeighborhood = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "relation-list", "--workspace-node-id", folderId).ConfigureAwait(false);
            Require(folderNeighborhood.ExitCode == 0 && folderNeighborhood.StandardOutput.Contains("RELATIONS count=1", StringComparison.Ordinal) &&
                    CountRelationLines(folderNeighborhood.StandardOutput, folderRelationId) == 1 &&
                    !folderNeighborhood.StandardOutput.Contains(supportRelationId, StringComparison.Ordinal),
                "Folder relation listing expanded folder membership into a child file relation.");
            var inspected = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "relation-read", "--resource-id", supportRelationId, "--resource-revision", "1").ConfigureAwait(false);
            Require(inspected.ExitCode == 0 && inspected.StandardOutput.Contains("origin=owner", StringComparison.OrdinalIgnoreCase) &&
                    inspected.StandardOutput.Contains("reason=" + S16T5RelationReason, StringComparison.Ordinal) &&
                    inspected.StandardOutput.Contains("review=current", StringComparison.Ordinal) &&
                    inspected.StandardOutput.Contains("revision=1", StringComparison.Ordinal),
                "Relation inspection did not expose the shared record's provenance, revision, and review state.");

            var oldEndpointPath = Path.Combine(fixture.OutputDirectory, "s16t5-endpoint-revision-1.bin");
            var oldEndpointRead = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-read", "--file-id", fileB.FileId, "--revision", 1,
                "--output-file", oldEndpointPath).ConfigureAwait(false);
            Require(oldEndpointRead.ExitCode == 0 && oldEndpointRead.StandardOutput.Contains("revision=1", StringComparison.Ordinal),
                "The relation's endpoint could not be read at its pinned revision.");
            RequireBytes(await File.ReadAllBytesAsync(oldEndpointPath).ConfigureAwait(false), fileBBytes,
                "the exact relation endpoint revision");

            var anchor = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-anchor-create", "--file-id", fileA.FileId,
                "--expected-content-revision", 1,
                "--offset", annotationOffset,
                "--length", Encoding.UTF8.GetByteCount(S16T5AnnotationText),
                "--expected-tree-revision", treeRevision).ConfigureAwait(false);
            Require(anchor.ExitCode == 0 && anchor.StandardOutput.Contains("state=live", StringComparison.Ordinal),
                "The synthetic annotation passage could not be anchored.");
            var anchorId = WorkspaceToken(anchor.StandardOutput, "id");
            var annotationDraft = await S12T5WriteInputAsync(fixture, "s16t5-owner-annotation.json",
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    anchorId,
                    facets = new[] { "experience" },
                    kind = "lesson",
                    author = "user",
                    creationMethod = "directWrite",
                    evidence = "reportedSource",
                    level = "l1"
                })).ConfigureAwait(false);
            var ownerAnnotation = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "cognitive-annotation-create", "--file-id", fileA.FileId,
                "--input-file", annotationDraft).ConfigureAwait(false);
            Require(ownerAnnotation.ExitCode == 0 && ownerAnnotation.StandardOutput.Contains("review=Accepted", StringComparison.Ordinal),
                "The owner annotation was not saved as owner-reviewed state.");
            var ownerAnnotationId = WorkspaceToken(ownerAnnotation.StandardOutput, "id");

            var suggestionDraft = await S12T5WriteInputAsync(fixture, "s16t5-suggestion-annotation.json",
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    anchorId,
                    facets = new[] { "experience" },
                    kind = "lesson",
                    author = "assistant",
                    creationMethod = "paraphrase",
                    evidence = "reportedSource",
                    level = "l1",
                    suggestion = true
                })).ConfigureAwait(false);
            var suggestion = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "cognitive-annotation-create", "--file-id", fileA.FileId,
                "--input-file", suggestionDraft).ConfigureAwait(false);
            Require(suggestion.ExitCode == 0 && suggestion.StandardOutput.Contains("review=Pending", StringComparison.Ordinal),
                "The synthetic AI annotation did not remain pending review.");
            var suggestionId = WorkspaceToken(suggestion.StandardOutput, "id");

            var filtered = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "cognitive-annotation-query", "--facet", "experience", "--kind", "lesson").ConfigureAwait(false);
            Require(filtered.ExitCode == 0 && filtered.StandardOutput.Contains("ANNOTATION QUERY count=2", StringComparison.Ordinal) &&
                    CountAnnotationRows(filtered.StandardOutput) == 2 &&
                    filtered.StandardOutput.Contains("id=" + ownerAnnotationId, StringComparison.Ordinal) &&
                    filtered.StandardOutput.Contains("id=" + suggestionId, StringComparison.Ordinal),
                "The combined cognitive filter did not return each matching annotation once.");
            var pendingFiltered = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "cognitive-annotation-query", "--facet", "experience", "--kind", "lesson",
                "--review-status", "pending", "--origin", "ai-suggestion").ConfigureAwait(false);
            Require(pendingFiltered.ExitCode == 0 && pendingFiltered.StandardOutput.Contains("ANNOTATION QUERY count=1", StringComparison.Ordinal) &&
                    CountAnnotationRows(pendingFiltered.StandardOutput) == 1 &&
                    pendingFiltered.StandardOutput.Contains("id=" + suggestionId, StringComparison.Ordinal) &&
                    !pendingFiltered.StandardOutput.Contains("id=" + ownerAnnotationId, StringComparison.Ordinal),
                "Cognitive AND filters did not isolate the one pending AI suggestion.");

            var tree = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false);
            Require(tree.ExitCode == 0 && tree.StandardOutput.Contains("WORKSPACE revision=", StringComparison.Ordinal) &&
                    WorkspaceNumber(tree.StandardOutput, "nodes") == baselineNodeCount &&
                    CountWorkspaceNodeLines(tree.StandardOutput, folderId) == 1 &&
                    CountWorkspaceNodeLines(tree.StandardOutput, fileA.FileId) == 1 &&
                    CountWorkspaceNodeLines(tree.StandardOutput, fileB.FileId) == 1,
                "Folder listing duplicated a workspace node for relation or passage-annotation data.");

            var revisedBytes = Encoding.UTF8.GetBytes("# Synthetic evidence revised\nReplacement evidence, not a rebound pin.\n");
            var revisedPath = await S12T5WriteInputAsync(fixture, "s16t5-evidence-revision-2.bin", revisedBytes).ConfigureAwait(false);
            var revise = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-revise", "--file-id", fileB.FileId,
                "--expected-content-revision", 1, "--input-file", revisedPath).ConfigureAwait(false);
            Require(revise.ExitCode == 0 && revise.StandardOutput.Contains("contentRevision=2", StringComparison.Ordinal),
                "The relation endpoint could not be revised through the owner CLI.");

            var retainedReadPath = Path.Combine(fixture.OutputDirectory, "s16t5-endpoint-revision-1-after-revise.bin");
            var retainedRead = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-read", "--file-id", fileB.FileId, "--revision", 1,
                "--output-file", retainedReadPath).ConfigureAwait(false);
            Require(retainedRead.ExitCode == 0 && retainedRead.StandardOutput.Contains("revision=1", StringComparison.Ordinal),
                "A retained endpoint read silently substituted the newer content revision.");
            RequireBytes(await File.ReadAllBytesAsync(retainedReadPath).ConfigureAwait(false), fileBBytes,
                "the retained relation endpoint revision after source revise");
            var currentReadPath = Path.Combine(fixture.OutputDirectory, "s16t5-endpoint-current.bin");
            var currentRead = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-read", "--file-id", fileB.FileId, "--output-file", currentReadPath).ConfigureAwait(false);
            Require(currentRead.ExitCode == 0 && currentRead.StandardOutput.Contains("revision=2", StringComparison.Ordinal),
                "The current endpoint read did not select the revised content revision.");
            RequireBytes(await File.ReadAllBytesAsync(currentReadPath).ConfigureAwait(false), revisedBytes,
                "the current relation endpoint revision");

            var stale = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "relation-read", "--resource-id", supportRelationId).ConfigureAwait(false);
            Require(stale.ExitCode == 0 && stale.StandardOutput.Contains("review=needsReview", StringComparison.Ordinal) &&
                    stale.StandardOutput.Contains("endpointRevisionChanged", StringComparison.Ordinal) &&
                    stale.StandardOutput.Contains("kind=workspacePassage revision=1", StringComparison.Ordinal),
                "A changed source did not leave the relation pinned to its original passage and needs-review state.");
            var staleAccept = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "relation-accept", "--resource-id", supportRelationId,
                "--expected-revision", 1, "--reason", S16T5OwnerReviewReason).ConfigureAwait(false);
            Require(staleAccept.ExitCode != 0 && staleAccept.StandardError.Contains("relation_needs_review", StringComparison.Ordinal),
                "The owner could accept a relation whose pinned endpoint needs review.");

            var removed = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "relation-unlink", "--resource-id", supportRelationId,
                "--expected-revision", 1, "--reason", S16T5OwnerReviewReason).ConfigureAwait(false);
            Require(removed.ExitCode == 0 && removed.StandardOutput.Contains("state=retracted", StringComparison.Ordinal),
                "The owner could not remove the stale relation with its explicit reason.");
            var undone = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "relation-relink", "--resource-id", supportRelationId,
                "--expected-revision", 2, "--reason", S16T5OwnerReviewReason).ConfigureAwait(false);
            Require(undone.ExitCode == 0 && undone.StandardOutput.Contains("state=proposed", StringComparison.Ordinal) &&
                    undone.StandardOutput.Contains("history action=restored", StringComparison.Ordinal) &&
                    undone.StandardOutput.Contains("review=needsReview", StringComparison.Ordinal),
                "Undo did not restore the relation as proposed while preserving its stale evidence review state.");

            var locked = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe, "lock").ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The owner CLI could not lock the synthetic vault.");
            var lockedStatus = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe, "status").ConfigureAwait(false);
            Require(lockedStatus.ExitCode == 0 && lockedStatus.StandardOutput.Contains("vault=locked", StringComparison.Ordinal),
                "The service did not report the locked state before private-read denials.");
            var lockedRelationRead = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "relation-read", "--resource-id", supportRelationId).ConfigureAwait(false);
            var lockedNeighborhood = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "relation-list", "--workspace-node-id", fileB.FileId).ConfigureAwait(false);
            var lockedEndpointRead = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-read", "--file-id", fileB.FileId, "--revision", 1).ConfigureAwait(false);
            Require(lockedRelationRead.ExitCode != 0 && lockedNeighborhood.ExitCode != 0 && lockedEndpointRead.ExitCode != 0 &&
                    !lockedRelationRead.StandardOutput.Contains("RELATION id=", StringComparison.Ordinal) &&
                    !lockedNeighborhood.StandardOutput.Contains("RELATIONS count=", StringComparison.Ordinal) &&
                    lockedEndpointRead.StandardOutput.Length == 0,
                "A locked owner relation, neighborhood, or endpoint read returned private data.");

            Console.WriteLine("PASS actual owner CLI/service file/folder relation neighborhood, provenance/review, exact endpoint revisions, cognitive filters, no-duplicate tree listing, needs-review, remove/undo, and lock denial");
            Console.WriteLine("LIMIT synthetic project-local vault and synthetic workspace bytes only; optional browser document open, MCP, providers, and real owner corpus were not exercised.");
        }
        finally
        {
            if (live is not null)
            {
                await StopServiceAsync(live).ConfigureAwait(false);
            }
        }
    }

    private static async Task<CommandResult> CreateS16T5RelationAsync(
        string cliExecutable,
        S12T5Fixture fixture,
        string relationId,
        string kind,
        object from,
        object to,
        string reason)
    {
        var draft = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind,
            state = "proposed",
            from,
            to,
            creationMethod = "directWrite",
            reason,
            stateReason = (string?)null,
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = DateTimeOffset.UtcNow,
            requestEvidence = (string?)null,
            origin = "owner"
        });
        var path = await S12T5WriteInputAsync(fixture,
            "s16t5-relation-" + Guid.NewGuid().ToString("N") + ".json", draft).ConfigureAwait(false);
        try
        {
            return await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "relation-create", "--resource-id", relationId, "--zone-id", S16T5Zone,
                "--input-file", path).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static object S16T5FileEndpoint(string resourceId, long revision, string sha256) => new
    {
        resourceId,
        endpointKind = "workspaceFile",
        revision,
        span = (object?)null,
        workspacePin = new { sha256, treeRevision = (long?)null }
    };

    private static object S16T5PassageEndpoint(string resourceId, long revision, int offset, int length, string sha256) => new
    {
        resourceId,
        endpointKind = "workspacePassage",
        revision,
        span = new { start = (long)offset, end = (long)offset + length },
        workspacePin = new { sha256, treeRevision = (long?)null }
    };

    private static object S16T5FolderEndpoint(string resourceId, long nodeRevision, long treeRevision) => new
    {
        resourceId,
        endpointKind = "workspaceFolder",
        revision = nodeRevision,
        span = (object?)null,
        workspacePin = new { sha256 = (string?)null, treeRevision = (long?)treeRevision }
    };


    private static int CountRelationLines(string text, string relationId) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.StartsWith("RELATION id=" + relationId + " ", StringComparison.Ordinal));

    private static int CountAnnotationRows(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.StartsWith("ANNOTATION id=", StringComparison.Ordinal));

    private static int CountWorkspaceNodeLines(string text, string nodeId) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Count(line => (line.StartsWith("FILE id=" + nodeId + " ", StringComparison.Ordinal) ||
                            line.StartsWith("FOLDER id=" + nodeId + " ", StringComparison.Ordinal)));
}
