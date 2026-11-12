using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Core.Security;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S16-T2 runtime proof for typed workspace relations through the actual owner
/// CLI and service, the Connections bridge and packaged MetaBrain.Mcp.exe
/// stdio entrypoint, index rebuild, encrypted backup/restore and scope-safe export.
/// Every fixture and export is synthetic and confined to the smoke runtime directory.
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S16T2Zone = "s16t2-zone";
    private const string S16T2FolderName = "s16t2-relations";
    private const string S16T2MoveTargetName = "s16t2-move-target";
    private const string S16T2FileAName = "s16t2-a.md";
    private const string S16T2FileBName = "s16t2-b.md";
    private const string S16T2FileCName = "s16t2-c.md";
    private const string S16T2FileBRevisedName = "s16t2-b-revised.md";
    private const string S16T2FileARevisedName = "s16t2-a-revised.md";
    private const string S16T2Evidence = "synthetic original evidence span";
    private const string S16T2FreshEvidence = "synthetic refreshed evidence span";
    private const string S16T2SupportReason = "Synthetic S16-T2 support relation reason.";
    private const string S16T2ActReason = "Synthetic S16-T2 owner-reviewed relation act.";
    private const string S16T2CycleReason = "Synthetic S16-T2 cycle relation reason.";
    private const string S16T2ImportReason = "Synthetic S16-T2 imported file relation reason.";
    private const string S16T2AiReason = "Synthetic S16-T2 AI relatedness suggestion.";

    public static async Task RunS16T2WorkspaceRelationsAsync(
        string connectionsExecutable, string cliExecutable, string mcpExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S16-T2 relation smoke requires Windows named pipes and DPAPI.");
        }

        using var fixture = S12T5Fixture.Create();
        RunningService? live = null;
        RunningService? restored = null;
        try
        {
            live = await StartServiceAsync(connectionsExecutable, fixture.LiveSettingsPath).ConfigureAwait(false);
            var passphrase = CreateSyntheticPassphrase();
            var provision = await RunOwnerCommandWithInputAsync(
                cliExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The actual product CLI did not provision the synthetic S16-T2 vault (stderr=" + provision.StandardError.Trim() + ").");

            var treeRevision = await S12T5RevisionAsync(cliExecutable, fixture.ControlPipe).ConfigureAwait(false);
            var folderCreate = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-mkdir", "--parent-id", "root", "--name", S16T2FolderName,
                "--expected-tree-revision", treeRevision).ConfigureAwait(false);
            Require(folderCreate.ExitCode == 0, "The S16-T2 workspace folder could not be created (stderr=" + folderCreate.StandardError.Trim() + ").");
            var folderId = WorkspaceToken(folderCreate.StandardOutput, "id");
            treeRevision = WorkspaceNumber(folderCreate.StandardOutput, "revision");
            var moveTargetCreate = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-mkdir", "--parent-id", "root", "--name", S16T2MoveTargetName,
                "--expected-tree-revision", treeRevision).ConfigureAwait(false);
            Require(moveTargetCreate.ExitCode == 0, "The S16-T2 destination folder could not be created.");
            var moveTargetId = WorkspaceToken(moveTargetCreate.StandardOutput, "id");
            treeRevision = WorkspaceNumber(moveTargetCreate.StandardOutput, "revision");

            var fileABytes = Encoding.UTF8.GetBytes("# Synthetic S16-T2 A\nOwner-selected relation fixture.\n");
            var fileBBytes = Encoding.UTF8.GetBytes("# Synthetic S16-T2 B\n" + S16T2Evidence + "\nOther synthetic bytes.\n");
            var fileCBytes = Encoding.UTF8.GetBytes("# Synthetic S16-T2 C\nDownstream relation fixture.\n");
            var importA = await S12T5ImportAsync(
                cliExecutable, fixture, folderId, S16T2FileAName, fileABytes, treeRevision).ConfigureAwait(false);
            var importB = await S12T5ImportAsync(
                cliExecutable, fixture, folderId, S16T2FileBName, fileBBytes, importA.TreeRevision).ConfigureAwait(false);
            var importC = await S12T5ImportAsync(
                cliExecutable, fixture, folderId, S16T2FileCName, fileCBytes, importB.TreeRevision).ConfigureAwait(false);
            var fileAId = importA.FileId;
            var fileBId = importB.FileId;
            var fileCId = importC.FileId;
            treeRevision = importC.TreeRevision;
            var folderWorkspaceRevision = 1L;
            var evidenceBytes = Encoding.UTF8.GetBytes(S16T2Evidence);
            var evidenceOffset = fileBBytes.AsSpan().IndexOf(evidenceBytes);
            Require(evidenceOffset >= 0, "The synthetic passage evidence was not present in the imported original bytes.");

            var fileAEndpoint = S16T2FileEndpoint(fileAId, 1, Sha256Hex(fileABytes));
            var fileBEndpoint = S16T2FileEndpoint(fileBId, 1, Sha256Hex(fileBBytes));
            var passageBEndpoint = S16T2PassageEndpoint(
                fileBId, 1, evidenceOffset, evidenceBytes.Length, Sha256Hex(evidenceBytes));
            var fileCEndpoint = S16T2FileEndpoint(fileCId, 1, Sha256Hex(fileCBytes));
            var evidenceA = Encoding.UTF8.GetBytes("Owner-selected relation fixture.");
            var evidenceAOffset = fileABytes.AsSpan().IndexOf(evidenceA);
            Require(evidenceAOffset >= 0, "The synthetic same-file passage evidence was not present in the original bytes.");
            var passageAEndpoint = S16T2PassageEndpoint(
                fileAId, 1, evidenceAOffset, evidenceA.Length, Sha256Hex(evidenceA));

            const string managedContradictionLeft = "s16t2-managed-contradicts-left";
            const string managedContradictionRight = "s16t2-managed-contradicts-right";
            await CreateS16T2MemoryAsync(cliExecutable, fixture.ControlPipe,
                managedContradictionLeft, "Synthetic managed contradiction left").ConfigureAwait(false);
            await CreateS16T2MemoryAsync(cliExecutable, fixture.ControlPipe,
                managedContradictionRight, "Synthetic managed contradiction right").ConfigureAwait(false);
            var managedContradictionId = "rel-s16t2-managed-contradicts";
            var managedContradiction = await CreateS16T2RelationAsync(
                cliExecutable, fixture, managedContradictionId, "contradicts", "proposed",
                S16T2ManagedEndpoint(managedContradictionLeft), S16T2ManagedEndpoint(managedContradictionRight),
                "Synthetic legacy managed contradiction identity.", "owner").ConfigureAwait(false);
            Require(managedContradiction.ExitCode == 0,
                "The managed legacy identity-only contradicts relation could not be created.");

            var aiSuggestion = await CreateS16T2RelationAsync(
                cliExecutable, fixture, "rel-s16t2-ai-related", "relatedTo", "accepted",
                fileAEndpoint, fileBEndpoint, S16T2AiReason, "aiSuggestion").ConfigureAwait(false);
            Require(aiSuggestion.ExitCode == 0 && aiSuggestion.StandardOutput.Contains("kind=relatedTo", StringComparison.Ordinal) &&
                    aiSuggestion.StandardOutput.Contains("state=proposed", StringComparison.Ordinal) &&
                    aiSuggestion.StandardOutput.Contains("origin=aiSuggestion", StringComparison.Ordinal),
                "An AI-origin relatedness suggestion was not stored as a pending identity edge (stderr=" + aiSuggestion.StandardError.Trim() + ").");
            var aiRelationId = "rel-s16t2-ai-related";

            var imported = await CreateS16T2RelationAsync(
                cliExecutable, fixture, "rel-s16t2-imported", "relatedTo", "accepted",
                fileAEndpoint, fileBEndpoint, S16T2ImportReason, "importedFileLink").ConfigureAwait(false);
            Require(imported.ExitCode == 0 && imported.StandardOutput.Contains("state=proposed", StringComparison.Ordinal) &&
                    imported.StandardOutput.Contains("origin=importedFileLink", StringComparison.Ordinal),
                "An imported-file relation did not retain its distinct pending origin.");
            var importedRelationId = "rel-s16t2-imported";

            var support = await CreateS16T2RelationAsync(
                cliExecutable, fixture, "rel-s16t2-support", "supports", "proposed",
                fileAEndpoint, passageBEndpoint, S16T2SupportReason, "owner").ConfigureAwait(false);
            Require(support.ExitCode == 0 && support.StandardOutput.Contains("kind=supports", StringComparison.Ordinal) &&
                    support.StandardOutput.Contains("kind=workspacePassage", StringComparison.Ordinal) &&
                    support.StandardOutput.Contains("sha256=" + Sha256Hex(evidenceBytes), StringComparison.Ordinal),
                "The shared service did not accept the exact original-byte passage pin.");
            var supportRelationId = "rel-s16t2-support";

            var contradiction = await CreateS16T2RelationAsync(
                cliExecutable, fixture, "rel-s16t2-contradicts", "contradicts", "proposed",
                fileAEndpoint, fileBEndpoint, "Synthetic S16-T2 contradiction pins both files.", "owner").ConfigureAwait(false);
            Require(contradiction.ExitCode == 0, "The workspace contradicts edge could not be created with both endpoint pins.");
            var contradictionRelationId = "rel-s16t2-contradicts";

            var folderApplicability = await CreateS16T2RelationAsync(
                cliExecutable, fixture, "rel-s16t2-folder-applies", "appliesTo", "proposed",
                S16T2FolderEndpoint(folderId, folderWorkspaceRevision, treeRevision), fileAEndpoint,
                "Synthetic applicability is about the folder identity only.", "owner").ConfigureAwait(false);
            Require(folderApplicability.ExitCode == 0, "The explicit folder applicability edge could not be created.");
            var folderRelationId = "rel-s16t2-folder-applies";
            var addressesRelationId = "rel-s16t2-addresses";
            var addresses = await CreateS16T2RelationAsync(
                cliExecutable, fixture, addressesRelationId, "addresses", "proposed",
                fileAEndpoint, fileCEndpoint, "Synthetic addressed target evidence.", "owner").ConfigureAwait(false);
            Require(addresses.ExitCode == 0,
                "The workspace addresses edge was not created through the owner relation path.");
            var addressesInspect = await ReadS16T2RelationAsync(
                cliExecutable, fixture.ControlPipe, addressesRelationId).ConfigureAwait(false);
            Require(addressesInspect.ExitCode == 0 &&
                    addressesInspect.StandardOutput.Contains("kind=addresses", StringComparison.Ordinal) &&
                    addressesInspect.StandardOutput.Contains("review=current", StringComparison.Ordinal),
                "The owner could not inspect a current workspace addresses edge.");

            var addressesDependentId = "rel-s16t2-addresses-dependent";
            var addressesDependent = await CreateS16T2RelationAsync(
                cliExecutable, fixture, addressesDependentId, "supports", "proposed",
                S16T2ManagedEndpoint(managedContradictionLeft), fileCEndpoint,
                "Synthetic downstream evidence depends on the addressed file.", "owner").ConfigureAwait(false);
            Require(addressesDependent.ExitCode == 0,
                "The downstream support relation for addresses propagation could not be created.");


            var cycleEdge = await CreateS16T2RelationAsync(
                cliExecutable, fixture, "rel-s16t2-cycle-edge", "derivedFrom", "proposed",
                fileAEndpoint, fileBEndpoint, S16T2CycleReason, "owner").ConfigureAwait(false);
            Require(cycleEdge.ExitCode == 0, "The synthetic first derivation edge could not be created.");
            var cycleRelationId = "rel-s16t2-cycle-edge";
            var cycleRetract = await TransitionS16T2Async(
                cliExecutable, fixture.ControlPipe, "relation-unlink", cycleRelationId, 1, S16T2ActReason).ConfigureAwait(false);
            Require(cycleRetract.ExitCode == 0 && cycleRetract.StandardOutput.Contains("state=retracted", StringComparison.Ordinal),
                "The all-stored-state cycle fixture could not be retracted.");
            var reverseCycle = await CreateS16T2RelationAsync(
                cliExecutable, fixture, "rel-s16t2-cycle-reverse", "supersedes", "proposed",
                fileBEndpoint, passageAEndpoint, S16T2CycleReason, "owner").ConfigureAwait(false);
            Require(reverseCycle.ExitCode == 4 && reverseCycle.StandardError.Contains("relation_cycle", StringComparison.Ordinal),
                "A supersedes edge closed a derived_from cycle through a retracted stored edge.");
            var cycleRestore = await TransitionS16T2Async(
                cliExecutable, fixture.ControlPipe, "relation-relink", cycleRelationId, 2, S16T2ActReason).ConfigureAwait(false);
            Require(cycleRestore.ExitCode == 0 && cycleRestore.StandardOutput.Contains("state=proposed", StringComparison.Ordinal),
                "The retracted relation could not be restored to a proposed edge with an attributed act.");

            var reverseIdentity = await CreateS16T2RelationAsync(
                cliExecutable, fixture, "rel-s16t2-related-reverse", "relatedTo", "proposed",
                fileBEndpoint, fileAEndpoint, "Synthetic identity-only reverse relation.", "owner").ConfigureAwait(false);
            Require(reverseIdentity.ExitCode == 0,
                "A reciprocal related_to identity edge was incorrectly treated as a constrained-kind cycle.");
            var rejectedRelationId = "rel-s16t2-workspace-rejected";
            var rejectedDraft = await CreateS16T2RelationAsync(
                cliExecutable, fixture, rejectedRelationId, "relatedTo", "proposed",
                fileAEndpoint, fileCEndpoint, "Synthetic owner rejection fixture.", "owner").ConfigureAwait(false);
            Require(rejectedDraft.ExitCode == 0, "The workspace rejection fixture could not be created.");
            var rejected = await TransitionS16T2Async(
                cliExecutable, fixture.ControlPipe, "relation-reject", rejectedRelationId, 1, S16T2ActReason).ConfigureAwait(false);
            Require(rejected.ExitCode == 0 && rejected.StandardOutput.Contains("state=rejected", StringComparison.Ordinal) &&
                    rejected.StandardOutput.Contains("history action=rejected", StringComparison.Ordinal) &&
                    rejected.StandardOutput.Contains("expectedRevision=1", StringComparison.Ordinal),
                "Workspace rejection did not record the owner act and exact revision precondition.");
            var rejectedTerminalAttempt = await TransitionS16T2Async(
                cliExecutable, fixture.ControlPipe, "relation-relink", rejectedRelationId, 2, S16T2ActReason).ConfigureAwait(false);
            var rejectedRead = await ReadS16T2RelationAsync(cliExecutable, fixture.ControlPipe, rejectedRelationId).ConfigureAwait(false);
            Require(rejectedTerminalAttempt.ExitCode == 4 &&
                    rejectedTerminalAttempt.StandardError.Contains("invalid_request", StringComparison.Ordinal) &&
                    rejectedRead.ExitCode == 0 && rejectedRead.StandardOutput.Contains("state=rejected", StringComparison.Ordinal) &&
                    rejectedRead.StandardOutput.Contains("revision=2", StringComparison.Ordinal) &&
                    rejectedRead.StandardOutput.Contains("history action=rejected", StringComparison.Ordinal) &&
                    !rejectedRead.StandardOutput.Contains("history action=restored", StringComparison.Ordinal),
                "A rejected workspace relation was silently restored or lost its terminal rejection history.");

            var badDigest = await CreateS16T2RelationAsync(
                cliExecutable, fixture, "rel-s16t2-bad-digest", "supports", "proposed",
                fileAEndpoint,
                S16T2PassageEndpoint(fileBId, 1, evidenceOffset, evidenceBytes.Length, new string('0', 64)),
                "Synthetic invalid passage digest.", "owner").ConfigureAwait(false);
            Require(badDigest.ExitCode == 4 && badDigest.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A passage relation with a digest that did not match the retained original bytes was accepted.");
            var wrongRevision = await CreateS16T2RelationAsync(
                cliExecutable, fixture, "rel-s16t2-wrong-revision", "relatedTo", "proposed",
                fileAEndpoint, S16T2FileEndpoint(fileBId, 99, Sha256Hex(fileBBytes)),
                "Synthetic stale endpoint precondition.", "owner").ConfigureAwait(false);
            Require(wrongRevision.ExitCode == 4 && wrongRevision.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A workspace edge was created against a non-retained endpoint revision.");
            var selfRelation = await CreateS16T2RelationAsync(
                cliExecutable, fixture, "rel-s16t2-self", "relatedTo", "proposed",
                fileAEndpoint, fileAEndpoint, "Synthetic same logical endpoint.", "owner").ConfigureAwait(false);
            Require(selfRelation.ExitCode == 4 && selfRelation.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A workspace relation connected a file selector to itself.");

            var missingActReason = await TransitionS16T2Async(
                cliExecutable, fixture.ControlPipe, "relation-accept", supportRelationId, 1, reason: null).ConfigureAwait(false);
            Require(missingActReason.ExitCode == 4 && missingActReason.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A workspace acceptance without an attributed act reason was accepted.");
            var accepted = await TransitionS16T2Async(
                cliExecutable, fixture.ControlPipe, "relation-accept", supportRelationId, 1, S16T2ActReason).ConfigureAwait(false);
            Require(accepted.ExitCode == 0 && accepted.StandardOutput.Contains("state=accepted", StringComparison.Ordinal) &&
                    accepted.StandardOutput.Contains("history action=accepted", StringComparison.Ordinal) &&
                    accepted.StandardOutput.Contains("expectedRevision=1", StringComparison.Ordinal),
                "The owner confirmation did not append its revision-preconditioned attributed history act.");
            var staleRevisionTransition = await TransitionS16T2Async(
                cliExecutable, fixture.ControlPipe, "relation-unlink", supportRelationId, 1, S16T2ActReason).ConfigureAwait(false);
            Require(staleRevisionTransition.ExitCode == 4 && staleRevisionTransition.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A relation transition with a stale relation revision was accepted.");
            var missingRetractionReason = await TransitionS16T2Async(
                cliExecutable, fixture.ControlPipe, "relation-unlink", supportRelationId, 2, reason: null).ConfigureAwait(false);
            Require(missingRetractionReason.ExitCode == 4 && missingRetractionReason.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A workspace retraction without an attributed act reason was accepted.");
            var retracted = await TransitionS16T2Async(
                cliExecutable, fixture.ControlPipe, "relation-unlink", supportRelationId, 2, S16T2ActReason).ConfigureAwait(false);
            Require(retracted.ExitCode == 0 && retracted.StandardOutput.Contains("state=retracted", StringComparison.Ordinal),
                "The owner could not retract a workspace relation with a reason.");
            var restoredProposal = await TransitionS16T2Async(
                cliExecutable, fixture.ControlPipe, "relation-relink", supportRelationId, 3, S16T2ActReason).ConfigureAwait(false);
            Require(restoredProposal.ExitCode == 0 && restoredProposal.StandardOutput.Contains("state=proposed", StringComparison.Ordinal) &&
                    restoredProposal.StandardOutput.Contains("history action=restored", StringComparison.Ordinal) &&
                    restoredProposal.StandardOutput.Contains("expectedRevision=3", StringComparison.Ordinal),
                "Restoring a workspace relation did not return it to proposed with immutable history.");
            var reviseManagedContradiction = await RunOwnerCommandAsync(cliExecutable,
                "owner", "memory-revise", "--control-pipe", fixture.ControlPipe,
                "--resource-id", managedContradictionRight, "--zone-id", S16T2Zone,
                "--expected-revision", "1",
                "--body", "Synthetic revised managed contradiction endpoint.",
                "--kind", "claim", "--author", "user", "--method", "directWrite",
                "--evidence", "unverified", "--level", "l1", "--title", "Managed contradiction right v2").ConfigureAwait(false);
            Require(reviseManagedContradiction.ExitCode == 0 &&
                    reviseManagedContradiction.StandardOutput.Contains("revision=2", StringComparison.Ordinal),
                "The managed identity endpoint could not be revised for legacy contradicts regression coverage.");
            var managedContradictionAfterRevision = await ReadS16T2RelationAsync(
                cliExecutable, fixture.ControlPipe, managedContradictionId).ConfigureAwait(false);
            Require(managedContradictionAfterRevision.ExitCode == 0 &&
                    managedContradictionAfterRevision.StandardOutput.Contains("review=current", StringComparison.Ordinal) &&
                    !managedContradictionAfterRevision.StandardOutput.Contains("endpointRevisionChanged", StringComparison.Ordinal),
                "Managed contradicts lost its legacy identity-only behavior after an endpoint revision change.");

            var beforeRename = await S12T5RevisionAsync(cliExecutable, fixture.ControlPipe).ConfigureAwait(false);
            var renameFolder = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-rename", "--node-id", folderId, "--name", "s16t2-relations-renamed",
                "--expected-tree-revision", beforeRename, "--expected-node-revision", 1).ConfigureAwait(false);
            Require(renameFolder.ExitCode == 0, "The workspace relation folder could not be renamed.");
            var moveFolder = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-move", "--node-id", folderId, "--parent-id", moveTargetId,
                "--expected-tree-revision", WorkspaceNumber(renameFolder.StandardOutput, "revision"),
                "--expected-node-revision", 2).ConfigureAwait(false);
            Require(moveFolder.ExitCode == 0, "The workspace relation folder could not be moved.");
            var moveFile = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-move", "--node-id", fileBId, "--parent-id", moveTargetId,
                "--expected-tree-revision", WorkspaceNumber(moveFolder.StandardOutput, "revision"),
                "--expected-node-revision", 1).ConfigureAwait(false);
            Require(moveFile.ExitCode == 0, "The workspace relation endpoint file could not be moved.");
            foreach (var relationId in new[]
                     {
                         aiRelationId, importedRelationId, supportRelationId, contradictionRelationId,
                         folderRelationId, addressesRelationId, addressesDependentId,
                         rejectedRelationId, managedContradictionId
                     })
            {
                var afterMove = await ReadS16T2RelationAsync(cliExecutable, fixture.ControlPipe, relationId).ConfigureAwait(false);
                Require(afterMove.ExitCode == 0 && afterMove.StandardOutput.Contains("review=current", StringComparison.Ordinal),
                    "Rename/move changed or dropped stable workspace relation " + relationId + ".");
            }
            Console.WriteLine("PASS actual CLI/service create, inspect, origin separation, revision-gated accept/reject/retract/restore, cycle checks and stable rename/move identity");

            var revisedBBytes = Encoding.UTF8.GetBytes("# Synthetic S16-T2 B revised\n" + S16T2FreshEvidence + "\nNew original bytes.\n");
            var revisedBPath = await S12T5WriteInputAsync(fixture, "s16t2-revised-b.bin", revisedBBytes).ConfigureAwait(false);
            var reviseB = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-revise", "--file-id", fileBId, "--expected-content-revision", 1,
                "--input-file", revisedBPath).ConfigureAwait(false);
            Require(reviseB.ExitCode == 0 && reviseB.StandardOutput.Contains("contentRevision=2", StringComparison.Ordinal),
                "The synthetic workspace evidence file could not advance to content revision 2.");
            var supportAfterB = await ReadS16T2RelationAsync(cliExecutable, fixture.ControlPipe, supportRelationId).ConfigureAwait(false);
            Require(supportAfterB.ExitCode == 0 && supportAfterB.StandardOutput.Contains("review=needsReview", StringComparison.Ordinal) &&
                    supportAfterB.StandardOutput.Contains("endpointRevisionChanged", StringComparison.Ordinal) &&
                    supportAfterB.StandardOutput.Contains("stale=" + fileBId, StringComparison.Ordinal) &&
                    supportAfterB.StandardOutput.Contains("kind=workspacePassage revision=1", StringComparison.Ordinal),
                "A changed passage endpoint was not reported stale with its original revision and selector retained (exit=" +
                supportAfterB.ExitCode + "; stdout=" + supportAfterB.StandardOutput.Trim() +
                "; stderr=" + supportAfterB.StandardError.Trim() + ").");
            var aiAfterB = await ReadS16T2RelationAsync(cliExecutable, fixture.ControlPipe, aiRelationId).ConfigureAwait(false);
            Require(aiAfterB.ExitCode == 0 && aiAfterB.StandardOutput.Contains("kind=relatedTo", StringComparison.Ordinal) &&
                    aiAfterB.StandardOutput.Contains("state=proposed", StringComparison.Ordinal) &&
                    aiAfterB.StandardOutput.Contains("review=current", StringComparison.Ordinal),
                "An identity-only AI suggestion became evidence or stale after content changed.");
            var importedAfterB = await ReadS16T2RelationAsync(cliExecutable, fixture.ControlPipe, importedRelationId).ConfigureAwait(false);
            Require(importedAfterB.ExitCode == 0 && importedAfterB.StandardOutput.Contains("origin=importedFileLink", StringComparison.Ordinal) &&
                    importedAfterB.StandardOutput.Contains("review=current", StringComparison.Ordinal),
                "An imported-file identity edge lost its origin or was treated as content evidence.");
            var staleAcceptance = await TransitionS16T2Async(
                cliExecutable, fixture.ControlPipe, "relation-accept", supportRelationId, 4, S16T2ActReason).ConfigureAwait(false);
            Require(staleAcceptance.ExitCode == 4 && staleAcceptance.StandardError.Contains("relation_needs_review", StringComparison.Ordinal),
                "A stale workspace relation was owner-confirmed without fresh evidence.");

            var revisedABytes = Encoding.UTF8.GetBytes("# Synthetic S16-T2 A revised\nFresh owner-selected content.\n");
            var revisedAPath = await S12T5WriteInputAsync(fixture, "s16t2-revised-a.bin", revisedABytes).ConfigureAwait(false);
            var reviseA = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-revise", "--file-id", fileAId, "--expected-content-revision", 1,
                "--input-file", revisedAPath).ConfigureAwait(false);
            Require(reviseA.ExitCode == 0 && reviseA.StandardOutput.Contains("contentRevision=2", StringComparison.Ordinal),
                "The synthetic assertion file could not advance to content revision 2.");
            var addressesAfterA = await ReadS16T2RelationAsync(
                cliExecutable, fixture.ControlPipe, addressesRelationId).ConfigureAwait(false);
            Require(addressesAfterA.ExitCode == 0 &&
                    addressesAfterA.StandardOutput.Contains("review=needsReview", StringComparison.Ordinal) &&
                    addressesAfterA.StandardOutput.Contains("endpointRevisionChanged", StringComparison.Ordinal) &&
                    addressesAfterA.StandardOutput.Contains("stale=" + fileAId, StringComparison.Ordinal),
                "The addresses evidence endpoint did not become stale after its file revision changed.");
            var addressesDependentAfterA = await ReadS16T2RelationAsync(
                cliExecutable, fixture.ControlPipe, addressesDependentId).ConfigureAwait(false);
            Require(addressesDependentAfterA.ExitCode == 0 &&
                    addressesDependentAfterA.StandardOutput.Contains("review=needsReview", StringComparison.Ordinal) &&
                    addressesDependentAfterA.StandardOutput.Contains("dependencyNeedsReview", StringComparison.Ordinal) &&
                    addressesDependentAfterA.StandardOutput.Contains("stale=" + fileCId, StringComparison.Ordinal),
                "An addresses assertion did not propagate review to the edge whose evidence is its dependent file.");
            var sameFileCycle = await CreateS16T2RelationAsync(
                cliExecutable, fixture, "rel-s16t2-cycle-samefile-revision-passage", "derivedFrom", "proposed",
                S16T2FileEndpoint(fileAId, 2, Sha256Hex(revisedABytes)), passageAEndpoint,
                "Synthetic same-file revision and passage DAG-node identity check.", "owner").ConfigureAwait(false);
            Require(sameFileCycle.ExitCode == 4 && sameFileCycle.StandardError.Contains("relation_cycle", StringComparison.Ordinal),
                "A constrained edge between two revisions/selectors of one stable file was not rejected as a DAG self-cycle.");
            var managedContradictionStillIdentity = await ReadS16T2RelationAsync(
                cliExecutable, fixture.ControlPipe, managedContradictionId).ConfigureAwait(false);
            Require(managedContradictionStillIdentity.ExitCode == 0 &&
                    managedContradictionStillIdentity.StandardOutput.Contains("review=current", StringComparison.Ordinal),
                "A workspace-only contradicts pin rule changed legacy managed identity behavior.");
            var contradictionAfterBoth = await ReadS16T2RelationAsync(cliExecutable, fixture.ControlPipe, contradictionRelationId).ConfigureAwait(false);
            Require(contradictionAfterBoth.ExitCode == 0 && contradictionAfterBoth.StandardOutput.Contains("review=needsReview", StringComparison.Ordinal) &&
                    contradictionAfterBoth.StandardOutput.Contains("stale=" + fileAId + "," + fileBId, StringComparison.Ordinal),
                "A workspace contradicts edge did not pin and report both changed file endpoints.");
            var supportAfterBoth = await ReadS16T2RelationAsync(cliExecutable, fixture.ControlPipe, supportRelationId).ConfigureAwait(false);
            Require(supportAfterBoth.ExitCode == 0 && supportAfterBoth.StandardOutput.Contains("stale=" + fileAId + "," + fileBId, StringComparison.Ordinal),
                "The content-sensitive support edge did not retain and report both stale workspace endpoints.");
            var folderAfterA = await ReadS16T2RelationAsync(cliExecutable, fixture.ControlPipe, folderRelationId).ConfigureAwait(false);
            Require(folderAfterA.ExitCode == 0 && folderAfterA.StandardOutput.Contains("review=needsReview", StringComparison.Ordinal) &&
                    folderAfterA.StandardOutput.Contains("stale=" + fileAId, StringComparison.Ordinal),
                "Explicit folder applicability was incorrectly treated as evidence for unchanged children or not stale on its pinned file endpoint.");

            var refreshedEvidence = Encoding.UTF8.GetBytes(S16T2FreshEvidence);
            var refreshedOffset = revisedBBytes.AsSpan().IndexOf(refreshedEvidence);
            Require(refreshedOffset >= 0, "The refreshed synthetic evidence span was not present in revision 2.");
            var refreshed = await CreateS16T2RelationAsync(
                cliExecutable, fixture, "rel-s16t2-support-refreshed", "supports", "proposed",
                S16T2FileEndpoint(fileAId, 2, Sha256Hex(revisedABytes)),
                S16T2PassageEndpoint(fileBId, 2, refreshedOffset, refreshedEvidence.Length, Sha256Hex(refreshedEvidence)),
                "Synthetic explicit fresh-evidence relation version.", "owner").ConfigureAwait(false);
            Require(refreshed.ExitCode == 0 && refreshed.StandardOutput.Contains("review=current", StringComparison.Ordinal) &&
                    refreshed.StandardOutput.Contains("revision=2", StringComparison.Ordinal),
                "Explicitly creating a new edge at current revisions did not refresh the evidence without rewriting old pins (exit=" +
                refreshed.ExitCode + "; stdout=" + refreshed.StandardOutput.Trim() + "; stderr=" + refreshed.StandardError.Trim() + ").");
            var refreshedRelationId = "rel-s16t2-support-refreshed";
            Console.WriteLine("PASS workspace contradicts pins both endpoints; addresses staleness propagates; managed contradicts stays identity-only; same-file passage/revision cycles are refused");


            var workspaceExportDestination = Path.Combine(fixture.OutputDirectory, "s16t2-workspace-export");
            var workspacePreview = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-export-preview", "--node-id", fileAId, "--content-revision", 2,
                "--destination", workspaceExportDestination).ConfigureAwait(false);
            Require(workspacePreview.ExitCode == 0 && workspacePreview.StandardOutput.Contains("EXPORT PREVIEW", StringComparison.Ordinal),
                "The selected-workspace export preview failed (stderr=" + workspacePreview.StandardError.Trim() + ").");
            var workspacePreviewToken = WorkspaceToken(workspacePreview.StandardOutput, "token");
            var workspaceExport = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-export", "--node-id", fileAId, "--content-revision", 2,
                "--destination", workspaceExportDestination, "--preview", workspacePreviewToken,
                "--confirm", "EXPORT").ConfigureAwait(false);
            Require(workspaceExport.ExitCode == 0 && workspaceExport.StandardOutput.Contains("EXPORT written", StringComparison.Ordinal),
                "The actual workspace export did not write its preview-bound synthetic selection.");
            var workspaceSidecarPath = Path.Combine(workspaceExportDestination, "metabrain-workspace-export.json");
            var workspaceSidecarBytes = await File.ReadAllBytesAsync(workspaceSidecarPath).ConfigureAwait(false);
            using (var sidecar = JsonDocument.Parse(workspaceSidecarBytes))
            {
                var root = sidecar.RootElement;
                Require(root.GetProperty("relations").GetArrayLength() == 0 &&
                        root.GetProperty("relationSupport").GetString() is { } supportText &&
                        supportText.Contains("no edge, history", StringComparison.OrdinalIgnoreCase) &&
                        !supportText.Contains("not implemented", StringComparison.OrdinalIgnoreCase),
                    "The workspace export did not truthfully report its explicit relation omission boundary.");
                Require(root.GetProperty("notes").EnumerateArray().Any(note =>
                        note.GetString()?.Contains("Relation records are deliberately omitted", StringComparison.Ordinal) == true),
                    "The workspace export sidecar did not disclose that relation edges/history were omitted.");
            }
            var sidecarText = Encoding.UTF8.GetString(workspaceSidecarBytes);
            Require(!sidecarText.Contains(fileBId, StringComparison.Ordinal) &&
                    !sidecarText.Contains("s16t2-b.md", StringComparison.Ordinal) &&
                    !sidecarText.Contains(supportRelationId, StringComparison.Ordinal) &&
                    !sidecarText.Contains(aiRelationId, StringComparison.Ordinal),
                "The workspace plaintext export disclosed an unselected relation endpoint or edge identity.");
            var subtreeExportDestination = Path.Combine(fixture.OutputDirectory, "s16t2-workspace-subtree-export");
            var subtreePreview = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-export-preview", "--node-id", moveTargetId,
                "--destination", subtreeExportDestination).ConfigureAwait(false);
            Require(subtreePreview.ExitCode == 0,
                "The workspace subtree export preview could not select both relation endpoints.");
            var subtreeToken = WorkspaceToken(subtreePreview.StandardOutput, "token");
            var subtreeExport = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-export", "--node-id", moveTargetId,
                "--destination", subtreeExportDestination, "--preview", subtreeToken,
                "--confirm", "EXPORT").ConfigureAwait(false);
            Require(subtreeExport.ExitCode == 0,
                "The workspace subtree export could not write its preview-bound selection.");
            var subtreeSidecarBytes = await File.ReadAllBytesAsync(
                Path.Combine(subtreeExportDestination, "metabrain-workspace-export.json")).ConfigureAwait(false);
            using (var subtreeSidecar = JsonDocument.Parse(subtreeSidecarBytes))
            {
                var subtreeRoot = subtreeSidecar.RootElement;
                var selectedFileIds = subtreeRoot.GetProperty("nodes").EnumerateArray()
                    .Select(node => node.GetProperty("nodeId").GetString() ?? string.Empty)
                    .ToHashSet(StringComparer.Ordinal);
                Require(selectedFileIds.Contains(fileAId) && selectedFileIds.Contains(fileBId) &&
                        selectedFileIds.Contains(fileCId) &&
                        subtreeRoot.GetProperty("relations").GetArrayLength() == 0 &&
                        subtreeRoot.GetProperty("relationSupport").GetString() is { } relationSelection &&
                        relationSelection.Contains("not relation records", StringComparison.Ordinal),
                    "The subtree export did not demonstrate selected endpoint content alongside the no-relation-selector boundary.");
            }
            var subtreeSidecarText = Encoding.UTF8.GetString(subtreeSidecarBytes);
            Require(!subtreeSidecarText.Contains(supportRelationId, StringComparison.Ordinal) &&
                    !subtreeSidecarText.Contains(addressesRelationId, StringComparison.Ordinal),
                "The selector-less workspace export disclosed canonical relation records.");
            Console.WriteLine("PASS managed owner export includes fully selected edges, withholds partial/stale-evidence edges; workspace subtree export selects content but no relation records");

            var managedSubject = "s16t2-agent-subject";
            var managedObject = "s16t2-agent-object";
            await CreateS16T2MemoryAsync(cliExecutable, fixture.ControlPipe, managedSubject, "Synthetic S16-T2 agent subject").ConfigureAwait(false);
            await CreateS16T2MemoryAsync(cliExecutable, fixture.ControlPipe, managedObject, "Synthetic S16-T2 agent object").ConfigureAwait(false);
            var handoffPath = Path.Combine(fixture.OutputDirectory, "s16t2-agent-grant.handoff");
            var sessionPath = Path.Combine(fixture.OutputDirectory, "s16t2-agent-session.handoff");
            var grant = await RunOwnerCommandWithInputAsync(cliExecutable, "ISSUE" + Environment.NewLine,
                "owner", "grant", "--control-pipe", fixture.ControlPipe,
                "--resource-ids", managedSubject,
                "--operations", "link.create,memory.read",
                "--destination-resource-ids", managedObject,
                "--expires-at-utc", DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture),
                "--handoff-file", handoffPath).ConfigureAwait(false);
            Require(grant.ExitCode == 0, "The synthetic owner could not authorize the existing scoped MCP link path.");
            VerifyOwnerHandoffProtection(handoffPath);
            var redeemed = await RunOwnerCommandAsync(cliExecutable,
                "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                "--handoff-file", handoffPath, "--session-file", sessionPath).ConfigureAwait(false);
            Require(redeemed.ExitCode == 0 && redeemed.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
                "The existing MCP relation grant did not redeem.");
            VerifyOwnerHandoffProtection(sessionPath);

            var agentDraft = S16T2RelationDraft(
                "supports", "accepted",
                S16T2ManagedEndpoint(managedSubject), S16T2ManagedEndpoint(managedObject),
                "Synthetic agent assertion, never owner confirmation.", "owner",
                "The synthetic requester asks to record this relation.");
            var agentCall = await RunPackagedMcpToolAsync(
                mcpExecutable, fixture.LiveSettingsPath, sessionPath, Encoding.UTF8.GetString(agentDraft)).ConfigureAwait(false);
            var agentRelationId = S4T6RelationIdFromOutput(agentCall.Text);
            Require(agentCall.ExitCode == 0 && !agentCall.IsError &&
                    agentRelationId.StartsWith("rel-", StringComparison.Ordinal) &&
                    agentCall.Text.Contains("kind=supports", StringComparison.Ordinal) &&
                    agentCall.Text.Contains("state=proposed", StringComparison.Ordinal),
                "The packaged MCP stdio entrypoint did not keep the agent assertion proposed.");
            var agentOwnerRead = await ReadS16T2RelationAsync(cliExecutable, fixture.ControlPipe, agentRelationId).ConfigureAwait(false);
            Require(agentOwnerRead.ExitCode == 0 && agentOwnerRead.StandardOutput.Contains("executor=agent", StringComparison.Ordinal) &&
                    agentOwnerRead.StandardOutput.Contains("origin=agentSuggestion", StringComparison.Ordinal) &&
                    agentOwnerRead.StandardOutput.Contains("state=proposed", StringComparison.Ordinal),
                "The owner read did not distinguish an agent suggestion from owner acceptance.");
            var supportAcceptExport = Path.Combine(fixture.OutputDirectory, "s16t2-export-one-endpoint.md");
            var oneEndpointExport = await RunOwnerCommandAsync(cliExecutable,
                "owner", "export", "--control-pipe", fixture.ControlPipe,
                "--resource-ids", "s16t2-agent-subject", "--include-relations", "true",
                "--output-file", supportAcceptExport).ConfigureAwait(false);
            Require(oneEndpointExport.ExitCode == 0 && oneEndpointExport.StandardOutput.Contains("withheldRelations=1", StringComparison.Ordinal),
                "The scoped owner export did not disclose the omitted partial edge as a count.");
            var oneEndpointText = await File.ReadAllTextAsync(supportAcceptExport, Encoding.UTF8).ConfigureAwait(false);
            Require(!oneEndpointText.Contains("s16t2-agent-object", StringComparison.Ordinal) &&
                    !oneEndpointText.Contains("rel-", StringComparison.Ordinal),
                "A scoped plaintext export emitted an out-of-scope endpoint or relation stub.");
            var bothEndpointExportPath = Path.Combine(fixture.OutputDirectory, "s16t2-export-both-endpoints.md");
            var bothEndpointExport = await RunOwnerCommandAsync(cliExecutable,
                "owner", "export", "--control-pipe", fixture.ControlPipe,
                "--resource-ids", "s16t2-agent-subject,s16t2-agent-object", "--include-relations", "true",
                "--output-file", bothEndpointExportPath).ConfigureAwait(false);
            Require(bothEndpointExport.ExitCode == 0 && bothEndpointExport.StandardOutput.Contains("relations=1", StringComparison.Ordinal),
                "The explicit two-endpoint owner export did not include its selected relation.");
            var bothEndpointText = await File.ReadAllTextAsync(bothEndpointExportPath, Encoding.UTF8).ConfigureAwait(false);
            Require(bothEndpointText.Contains("Origin: AgentSuggestion", StringComparison.Ordinal) &&
                    bothEndpointText.Contains("s16t2-agent-subject", StringComparison.Ordinal) &&
                    bothEndpointText.Contains("s16t2-agent-object", StringComparison.Ordinal),
                "The in-scope owner export lost relation provenance or selected endpoints.");
            var revisedManagedEvidence = await RunOwnerCommandAsync(cliExecutable,
                "owner", "memory-revise", "--control-pipe", fixture.ControlPipe,
                "--resource-id", managedObject, "--zone-id", S16T2Zone,
                "--expected-revision", "1",
                "--body", "Synthetic revised managed evidence endpoint.",
                "--kind", "claim", "--author", "user", "--method", "directWrite",
                "--evidence", "unverified", "--level", "l1", "--title", "Managed MCP endpoint v2").ConfigureAwait(false);
            Require(revisedManagedEvidence.ExitCode == 0 &&
                    revisedManagedEvidence.StandardOutput.Contains("revision=2", StringComparison.Ordinal),
                "The managed evidence endpoint could not be revised for export-scope coverage.");
            var staleAgentRelation = await ReadS16T2RelationAsync(
                cliExecutable, fixture.ControlPipe, agentRelationId).ConfigureAwait(false);
            Require(staleAgentRelation.ExitCode == 0 &&
                    staleAgentRelation.StandardOutput.Contains("review=needsReview", StringComparison.Ordinal) &&
                    staleAgentRelation.StandardOutput.Contains("endpointRevisionChanged", StringComparison.Ordinal),
                "A managed support edge did not retain its pinned evidence revision for selection.");
            var staleEvidenceExportPath = Path.Combine(fixture.OutputDirectory, "s16t2-export-stale-evidence.md");
            var staleEvidenceExport = await RunOwnerCommandAsync(cliExecutable,
                "owner", "export", "--control-pipe", fixture.ControlPipe,
                "--resource-ids", "s16t2-agent-subject,s16t2-agent-object", "--include-relations", "true",
                "--output-file", staleEvidenceExportPath).ConfigureAwait(false);
            Require(staleEvidenceExport.ExitCode == 0 &&
                    staleEvidenceExport.StandardOutput.Contains("withheldRelations=1", StringComparison.Ordinal) &&
                    staleEvidenceExport.StandardOutput.Contains("relations=0", StringComparison.Ordinal),
                "The owner export included an edge whose pinned evidence revision was not selected.");
            var staleEvidenceExportText = await File.ReadAllTextAsync(staleEvidenceExportPath, Encoding.UTF8).ConfigureAwait(false);
            Require(!staleEvidenceExportText.Contains(agentRelationId, StringComparison.Ordinal),
                "The scoped plaintext export exposed the ID of an edge withheld for an unselected evidence revision.");

            var agentWorkspaceDraft = S16T2RelationDraft(
                "supports", "accepted", fileAEndpoint, fileBEndpoint,
                "Synthetic denied workspace-sharing attempt.", "owner",
                "The agent must not attest or share workspace relations.");
            try
            {
                var deniedWorkspaceRelation = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                    "metabrain_relation_create", new { sessionFile = sessionPath, draft = Encoding.UTF8.GetString(agentWorkspaceDraft) }).ConfigureAwait(false);
                Require(deniedWorkspaceRelation.Contains("resource_unavailable", StringComparison.Ordinal) &&
                        !deniedWorkspaceRelation.Contains(fileAId, StringComparison.Ordinal) &&
                        !deniedWorkspaceRelation.Contains(fileBId, StringComparison.Ordinal) &&
                        !deniedWorkspaceRelation.Contains("s16t2-a.md", StringComparison.Ordinal) &&
                        !deniedWorkspaceRelation.Contains("s16t2-b.md", StringComparison.Ordinal),
                    "The MCP bridge disclosed or created a workspace relation outside the approved managed-resource scope.");
                var packagedDenied = await RunPackagedMcpToolAsync(
                    mcpExecutable, fixture.LiveSettingsPath, sessionPath, Encoding.UTF8.GetString(agentWorkspaceDraft)).ConfigureAwait(false);
                Require(packagedDenied.ExitCode == 0 && packagedDenied.IsError &&
                        packagedDenied.Text.Contains("resource_unavailable", StringComparison.Ordinal) &&
                        !packagedDenied.Text.Contains(fileAId, StringComparison.Ordinal) &&
                        !packagedDenied.Text.Contains(fileBId, StringComparison.Ordinal) &&
                        !packagedDenied.Text.Contains("s16t2-a.md", StringComparison.Ordinal) &&
                        !packagedDenied.Text.Contains("s16t2-b.md", StringComparison.Ordinal),
                    "MetaBrain.Mcp.exe stdio did not generically deny workspace relation sharing without endpoint disclosure.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(agentDraft);
                CryptographicOperations.ZeroMemory(agentWorkspaceDraft);
            }
            Console.WriteLine("PASS MetaBrain.Mcp.exe stdio keeps agent claims proposed and generically denies workspace relation sharing without endpoint disclosure; Connections agent-mcp bridge denies the same scope");

            var indexRebuild = await RunOwnerCommandAsync(cliExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(indexRebuild.ExitCode == 0 && indexRebuild.StandardOutput.Contains("INDEX rebuilt", StringComparison.Ordinal),
                "The private index could not rebuild with canonical workspace relations present.");
            var relationList = await RunOwnerCommandAsync(cliExecutable,
                "owner", "relation-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(relationList.ExitCode == 0 && relationList.StandardOutput.Contains("RELATIONS count=", StringComparison.Ordinal),
                "The owner relation list did not read canonical edges after index rebuild.");
            foreach (var relationId in new[]
                     {
                         aiRelationId, importedRelationId, supportRelationId, contradictionRelationId,
                         folderRelationId, addressesRelationId, addressesDependentId, rejectedRelationId,
                         managedContradictionId, cycleRelationId, refreshedRelationId, agentRelationId
                     })
            {
                Require(relationList.StandardOutput.Contains("RELATION id=" + relationId, StringComparison.Ordinal),
                    "Index rebuild or list projection lost canonical edge " + relationId + ".");
            }

            var backupPath = Path.Combine(fixture.BackupDirectory, "s16t2-relations.mbbackup");
            var backup = await RunOwnerCommandAsync(cliExecutable,
                "owner", "backup", "--control-pipe", fixture.ControlPipe,
                "--output-file", backupPath).ConfigureAwait(false);
            Require(backup.ExitCode == 0 && backup.StandardOutput.Contains("BACKUP written", StringComparison.Ordinal),
                "The owner could not back up canonical workspace relations and history.");
            var backupBytes = await File.ReadAllBytesAsync(backupPath).ConfigureAwait(false);
            foreach (var privateMarker in new[] { S16T2SupportReason, S16T2AiReason, S16T2Evidence, S16T2FreshEvidence })
            {
                Require(Encoding.UTF8.GetString(backupBytes).IndexOf(privateMarker, StringComparison.Ordinal) < 0,
                    "The encrypted relation backup exposed synthetic plaintext marker " + privateMarker + ".");
            }

            var restore = await S12T5RestoreAsync(
                cliExecutable, fixture.ControlPipe, backupPath, fixture.PostTarget, passphrase).ConfigureAwait(false);
            Require(restore.ExitCode == 0 && restore.StandardOutput.Contains("RESTORE written", StringComparison.Ordinal),
                "The encrypted workspace-relation backup did not restore (stderr=" + restore.StandardError.Trim() + ").");
            restored = await StartServiceAsync(connectionsExecutable, fixture.PostSettingsPath).ConfigureAwait(false);
            await S12T5UnlockAsync(cliExecutable, fixture.PostControlPipe, passphrase).ConfigureAwait(false);
            var restoredList = await RunOwnerCommandAsync(cliExecutable,
                "owner", "relation-list", "--control-pipe", fixture.PostControlPipe).ConfigureAwait(false);
            Require(restoredList.ExitCode == 0, "The restored owner could not list canonical relations.");
            foreach (var relationId in new[]
                     {
                         aiRelationId, importedRelationId, supportRelationId, contradictionRelationId,
                         folderRelationId, addressesRelationId, addressesDependentId, rejectedRelationId,
                         managedContradictionId, cycleRelationId, refreshedRelationId, agentRelationId
                     })
            {
                Require(restoredList.StandardOutput.Contains("RELATION id=" + relationId, StringComparison.Ordinal),
                    "Backup/restore lost canonical edge " + relationId + ".");
            }
            var restoredSupport = await ReadS16T2RelationAsync(cliExecutable, fixture.PostControlPipe, supportRelationId).ConfigureAwait(false);
            Require(restoredSupport.ExitCode == 0 && restoredSupport.StandardOutput.Contains("review=needsReview", StringComparison.Ordinal) &&
                    restoredSupport.StandardOutput.Contains("expectedRevision=3", StringComparison.Ordinal),
                "Backup/restore changed stale evidence or immutable relation history.");
            var restoredFresh = await ReadS16T2RelationAsync(cliExecutable, fixture.PostControlPipe, refreshedRelationId).ConfigureAwait(false);
            Require(restoredFresh.ExitCode == 0 && restoredFresh.StandardOutput.Contains("review=current", StringComparison.Ordinal),
                "Backup/restore lost the explicitly refreshed relation evidence.");
            var restoredWorkspaceTree = await S12T5TreeAsync(cliExecutable, fixture.PostControlPipe).ConfigureAwait(false);
            Require(restoredWorkspaceTree.Contains("id=" + fileAId, StringComparison.Ordinal) &&
                    restoredWorkspaceTree.Contains("id=" + fileBId, StringComparison.Ordinal),
                "Backup/restore changed stable workspace endpoint identities.");
            Console.WriteLine("PASS encrypted backup/restore preserves canonical edges, pins and prospective history after private-index rebuild");
        }
        finally
        {
            if (restored is not null)
            {
                await StopServiceAsync(restored).ConfigureAwait(false);
            }

            if (live is not null)
            {
                await StopServiceAsync(live).ConfigureAwait(false);
            }
        }

        Console.WriteLine("LIMIT synthetic Windows fixture: actual CLI/service, Connections bridge and packaged MetaBrain.Mcp.exe stdio used a synthetic JSON-RPC driver; no normal OMP/Codex client or model/embedding provider ran and no OMP profile/settings changed. Workspace content export selects no relation records; encrypted backup preserves the canonical edges/history.");
    }

    private static async Task CreateS16T2MemoryAsync(string cliExecutable, string controlPipe, string resourceId, string title)
    {
        var created = await RunOwnerCommandAsync(cliExecutable,
            "owner", "memory-create", "--control-pipe", controlPipe,
            "--resource-id", resourceId, "--zone-id", S16T2Zone,
            "--body", "Synthetic S16-T2 memory endpoint; not real owner content.",
            "--kind", "claim", "--author", "user", "--method", "directWrite",
            "--evidence", "unverified", "--level", "l1", "--title", title).ConfigureAwait(false);
        Require(created.ExitCode == 0 && created.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
            "The synthetic managed MCP endpoint could not be created (stderr=" + created.StandardError.Trim() + ").");
    }

    private static async Task<CommandResult> CreateS16T2RelationAsync(
        string cliExecutable,
        S12T5Fixture fixture,
        string relationId,
        string kind,
        string state,
        object from,
        object to,
        string reason,
        string origin,
        string? requestEvidence = null)
    {
        var bytes = S16T2RelationDraft(kind, state, from, to, reason, origin, requestEvidence);
        var path = Path.Combine(fixture.OutputDirectory, "s16t2-relation-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            return await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "relation-create", "--resource-id", relationId, "--zone-id", S16T2Zone,
                "--input-file", path).ConfigureAwait(false);
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

    private static byte[] S16T2RelationDraft(
        string kind,
        string state,
        object from,
        object to,
        string reason,
        string origin,
        string? requestEvidence = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind,
            state,
            from,
            to,
            creationMethod = "directWrite",
            reason,
            stateReason = (string?)null,
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc = DateTimeOffset.UtcNow,
            requestEvidence,
            origin
        });

    private static object S16T2FileEndpoint(string resourceId, long revision, string sha256) => new
    {
        resourceId,
        endpointKind = "workspaceFile",
        revision,
        span = (object?)null,
        workspacePin = new { sha256, treeRevision = (long?)null }
    };

    private static object S16T2PassageEndpoint(
        string resourceId,
        long revision,
        int offset,
        int length,
        string sha256) => new
    {
        resourceId,
        endpointKind = "workspacePassage",
        revision,
        span = new { start = (long)offset, end = (long)offset + length },
        workspacePin = new { sha256, treeRevision = (long?)null }
    };

    private static object S16T2FolderEndpoint(string resourceId, long nodeRevision, long treeRevision) => new
    {
        resourceId,
        endpointKind = "workspaceFolder",
        revision = nodeRevision,
        span = (object?)null,
        workspacePin = new { sha256 = (string?)null, treeRevision = (long?)treeRevision }
    };

    private static object S16T2ManagedEndpoint(string resourceId) => new
    {
        resourceId,
        endpointKind = "memoryItem",
        revision = 1L,
        span = (object?)null
    };

    private static Task<CommandResult> TransitionS16T2Async(
        string cliExecutable,
        string controlPipe,
        string operation,
        string relationId,
        long expectedRevision,
        string? reason) =>
        reason is null
            ? RunOwnerCommandAsync(cliExecutable,
                "owner", operation, "--control-pipe", controlPipe,
                "--resource-id", relationId, "--expected-revision", expectedRevision.ToString(CultureInfo.InvariantCulture))
            : RunOwnerCommandAsync(cliExecutable,
                "owner", operation, "--control-pipe", controlPipe,
                "--resource-id", relationId, "--expected-revision", expectedRevision.ToString(CultureInfo.InvariantCulture),
                "--reason", reason);

    private static Task<CommandResult> ReadS16T2RelationAsync(string cliExecutable, string controlPipe, string relationId) =>
        RunOwnerCommandAsync(cliExecutable,
            "owner", "relation-read", "--control-pipe", controlPipe, "--resource-id", relationId);

    private static async Task<(int ExitCode, bool IsError, string Text, string StandardError)> RunPackagedMcpToolAsync(
        string mcpExecutable,
        string settingsPath,
        string sessionPath,
        string draft)
    {
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = mcpExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(settingsPath);

        using var process = new System.Diagnostics.Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("MetaBrain.Mcp.exe did not start.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"synthetic-s16-t2-stdio-driver","version":"1.0.0"}}}""").ConfigureAwait(false);
        await process.StandardInput.WriteLineAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""").ConfigureAwait(false);
        var call = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new
            {
                name = "metabrain_relation_create",
                arguments = new { sessionFile = sessionPath, draft }
            }
        });
        await process.StandardInput.WriteLineAsync(call).ConfigureAwait(false);
        process.StandardInput.Close();

        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }

                throw new TimeoutException("MetaBrain.Mcp.exe did not exit after its stdio request reached EOF.");
            }
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var responseLine = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(line =>
            {
                using var response = JsonDocument.Parse(line);
                return response.RootElement.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.Number && id.GetInt32() == 3;
            });
        if (responseLine is null)
        {
            throw new InvalidDataException(
                "MetaBrain.Mcp.exe emitted no tools/call response (exit=" + process.ExitCode +
                "; stderr=" + stderr.Trim() + ").");
        }

        using var document = JsonDocument.Parse(responseLine);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            return (process.ExitCode, true, error.GetRawText(), stderr);
        }

        var result = root.GetProperty("result");
        var text = result.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
        var isError = result.TryGetProperty("isError", out var errorValue) && errorValue.GetBoolean();
        return (process.ExitCode, isError, text, stderr);
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
