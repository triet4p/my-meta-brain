using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Security;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S12-T3 runtime proof for stable workspace identity and the owner mutation
/// set: create, rename, move, recursive copy, revision-gated explicit replace,
/// previewed tombstone delete and group restore through the actual owner CLI
/// and shared service, including restart, lock, negative preconditions, a
/// refused copy that leaves no staged residue, and agent-channel denial.
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S12T3Alpha = "alpha-s12t3";
    private const string S12T3AlphaRenamed = "alpha-s12t3-renamed";
    private const string S12T3Inner = "inner-s12t3";
    private const string S12T3Note = "note-s12t3.md";
    private const string S12T3NoteRenamed = "note-s12t3-renamed.md";
    private const string S12T3Deep = "deep-s12t3.bin";
    private const string S12T3Copy = "alpha-s12t3-copy";
    private const string S12T3NoteCopy = "note-s12t3-copy.md";
    private const string S12T3RollbackFolder = "rollback-s12t3";
    private const string S12T3RollbackPayload = "rollback-s12t3.bin";
    private const string S12T3NoteMarker = "synthetic-s12t3-note-marker-5a17c2";
    private const string S12T3NoteRevisionMarker = "synthetic-s12t3-note-revision-marker-9b03f4";
    private static readonly byte[] S12T3DeepBytes = { 0x00, 0xFF, 0xFE, 0x81, 0xC0, 0xAF, 0xED, 0xA0, 0x80, 0x0A };
    private static readonly byte[] S12T3PinnedRange = Encoding.ASCII.GetBytes("synthetic");

    public static async Task RunS12T3WorkspaceMutationsAsync(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S12-T3 workspace smoke is Windows-only.");
        }

        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        var workspaceRoot = Path.Combine(fixture.VaultDirectoryPath, "workspace");
        try
        {
            service = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            var passphrase = CreateSyntheticPassphrase();
            var provision = await RunOwnerCommandWithInputAsync(
                executable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The owner CLI did not provision the S12-T3 vault (stderr=" + provision.StandardError.Trim() + ").");

            var initial = await ReadWorkspaceStatusAsync(executable, fixture).ConfigureAwait(false);
            Require(initial.Contains("state=uninitialized", StringComparison.Ordinal),
                "A fresh unlocked vault did not report an uninitialized workspace.");

            var noteBytes = Encoding.UTF8.GetBytes(
                "# synthetic S12-T3 note\n\nmarker " + S12T3NoteMarker + "\nmore text for anchored ranges\n");
            var noteRevisionBytes = Encoding.UTF8.GetBytes(
                "# synthetic S12-T3 note (replaced)\n\nmarker " + S12T3NoteRevisionMarker + "\nmore text for anchored ranges\n");
            var notePath = await WriteWorkspaceInputAsync(fixture, "input-note-s12t3.md", noteBytes).ConfigureAwait(false);
            var noteRevisionPath = await WriteWorkspaceInputAsync(
                fixture, "input-note-revision-s12t3.md", noteRevisionBytes).ConfigureAwait(false);
            var inputBytesBefore = await File.ReadAllBytesAsync(notePath).ConfigureAwait(false);
            var inputWriteTimeBefore = File.GetLastWriteTimeUtc(notePath);

            var mkdirAlpha = await WorkspaceAsync(executable, fixture,
                "workspace-mkdir", "--parent-id", "root", "--name", S12T3Alpha,
                "--expected-tree-revision", "0").ConfigureAwait(false);
            Require(mkdirAlpha.ExitCode == 0 && mkdirAlpha.StandardOutput.Contains("MKDIR id=", StringComparison.Ordinal),
                "The owner CLI did not create the S12-T3 folder (stderr=" + mkdirAlpha.StandardError.Trim() + ").");
            var alphaId = WorkspaceToken(mkdirAlpha.StandardOutput, "id");

            var mkdirInner = await WorkspaceAsync(executable, fixture,
                "workspace-mkdir", "--parent-id", alphaId, "--name", S12T3Inner,
                "--expected-tree-revision", TreeRevision(mkdirAlpha.StandardOutput)).ConfigureAwait(false);
            Require(mkdirInner.ExitCode == 0, "The owner CLI did not create the nested folder (stderr=" + mkdirInner.StandardError.Trim() + ").");
            var innerId = WorkspaceToken(mkdirInner.StandardOutput, "id");

            var importNote = await WorkspaceAsync(executable, fixture,
                "workspace-import", "--parent-id", alphaId, "--name", S12T3Note,
                "--input-file", notePath, "--expected-tree-revision", TreeRevision(mkdirInner.StandardOutput)).ConfigureAwait(false);
            Require(importNote.ExitCode == 0, "The owner CLI did not import the note (stderr=" + importNote.StandardError.Trim() + ").");
            var noteId = WorkspaceToken(importNote.StandardOutput, "id");
            var deepPath = await WriteWorkspaceInputAsync(fixture, "input-deep-s12t3.bin", S12T3DeepBytes).ConfigureAwait(false);
            var importDeep = await WorkspaceAsync(executable, fixture,
                "workspace-import", "--parent-id", innerId, "--name", S12T3Deep,
                "--input-file", deepPath, "--expected-tree-revision", TreeRevision(importNote.StandardOutput)).ConfigureAwait(false);
            Require(importDeep.ExitCode == 0, "The owner CLI did not import the binary (stderr=" + importDeep.StandardError.Trim() + ").");
            var deepId = WorkspaceToken(importDeep.StandardOutput, "id");

            var anchor = await WorkspaceAsync(executable, fixture,
                "workspace-anchor-create", "--file-id", noteId,
                "--expected-content-revision", "1",
                "--offset", "0", "--length", S12T3PinnedRange.Length.ToString(CultureInfo.InvariantCulture),
                "--expected-tree-revision", TreeRevision(importDeep.StandardOutput)).ConfigureAwait(false);
            Require(anchor.ExitCode == 0 && anchor.StandardOutput.Contains("state=live", StringComparison.Ordinal),
                "The owner CLI did not pin the citation anchor (stderr=" + anchor.StandardError.Trim() + ").");
            var anchorId = WorkspaceToken(anchor.StandardOutput, "id");
            Require(string.Equals(WorkspaceToken(anchor.StandardOutput, "sha256"),
                    DigestRange(noteBytes, 0, S12T3PinnedRange.Length), StringComparison.Ordinal),
                "The anchor did not record the digest of exactly the anchored bytes.");

            var tree = await TreeAsync(executable, fixture).ConfigureAwait(false);
            Require(tree.Contains("FOLDER id=" + alphaId + " parent=" + RootId(tree), StringComparison.Ordinal),
                "The created folder is not a child of the workspace root.");
            Require(tree.Contains("FILE id=" + deepId + " parent=" + innerId, StringComparison.Ordinal),
                "The nested binary is not under its folder.");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, noteId, 1, "s12t3-note-rev1.md").ConfigureAwait(false),
                noteBytes, "imported note");
            Console.WriteLine("PASS create keeps stable ids and a revision-pinned anchor digests the exact anchored bytes");

            // Rename keeps identity, membership, content revision and anchors.
            var renameFolder = await WorkspaceAsync(executable, fixture,
                "workspace-rename", "--node-id", alphaId, "--name", S12T3AlphaRenamed,
                "--expected-tree-revision", await TreeRevisionAsync(executable, fixture).ConfigureAwait(false),
                "--expected-node-revision", "1").ConfigureAwait(false);
            Require(renameFolder.ExitCode == 0 && renameFolder.StandardOutput.Contains("RENAME id=" + alphaId, StringComparison.Ordinal),
                "The owner CLI did not rename the folder (stderr=" + renameFolder.StandardError.Trim() + ").");
            var renameFile = await WorkspaceAsync(executable, fixture,
                "workspace-rename", "--node-id", noteId, "--name", S12T3NoteRenamed,
                "--expected-tree-revision", TreeRevision(renameFolder.StandardOutput),
                "--expected-node-revision", "1").ConfigureAwait(false);
            Require(renameFile.ExitCode == 0, "The owner CLI did not rename the file (stderr=" + renameFile.StandardError.Trim() + ").");
            Require(WorkspaceNumber(renameFile.StandardOutput, "nodeRevision") == 2,
                "Renaming the file did not advance only its own node revision.");
            tree = await TreeAsync(executable, fixture).ConfigureAwait(false);
            Require(tree.Contains("FOLDER id=" + alphaId + " parent=" + RootId(tree), StringComparison.Ordinal) &&
                    tree.Contains("FOLDER id=" + innerId + " parent=" + alphaId, StringComparison.Ordinal) &&
                    tree.Contains("FILE id=" + noteId + " parent=" + alphaId + " name=" + JsonSerializer.Serialize(S12T3NoteRenamed), StringComparison.Ordinal) &&
                    tree.Contains("FILE id=" + deepId + " parent=" + innerId, StringComparison.Ordinal),
                "Renaming changed identity, membership or descendant ids instead of only the exact name.");
            Require(tree.Contains("FILE id=" + noteId + " parent=" + alphaId + " name=" + JsonSerializer.Serialize(S12T3NoteRenamed) + " revision=2 contentRevision=1", StringComparison.Ordinal),
                "Renaming the file changed its content revision instead of only its node revision.");
            var anchorsAfterRename = await WorkspaceAsync(executable, fixture,
                "workspace-anchor-list", "--file-id", noteId).ConfigureAwait(false);
            Require(anchorsAfterRename.ExitCode == 0 && anchorsAfterRename.StandardOutput.Contains("id=" + anchorId + " ", StringComparison.Ordinal) &&
                    anchorsAfterRename.StandardOutput.Contains("state=live", StringComparison.Ordinal),
                "Renaming the file rebound or dropped its citation anchor.");
            Console.WriteLine("PASS rename preserves ids, descendants, content revision and anchors");

            // Move keeps identity, descendant ids and bytes; cycles are refused.
            var moveInner = await WorkspaceAsync(executable, fixture,
                "workspace-move", "--node-id", innerId, "--parent-id", "root",
                "--expected-tree-revision", await TreeRevisionAsync(executable, fixture).ConfigureAwait(false),
                "--expected-node-revision", "1").ConfigureAwait(false);
            Require(moveInner.ExitCode == 0, "The owner CLI did not move the nested folder (stderr=" + moveInner.StandardError.Trim() + ").");
            var moveAlpha = await WorkspaceAsync(executable, fixture,
                "workspace-move", "--node-id", alphaId, "--parent-id", innerId,
                "--expected-tree-revision", TreeRevision(moveInner.StandardOutput),
                "--expected-node-revision", "2").ConfigureAwait(false);
            Require(moveAlpha.ExitCode == 0, "The owner CLI did not move the folder under its sibling (stderr=" + moveAlpha.StandardError.Trim() + ").");
            tree = await TreeAsync(executable, fixture).ConfigureAwait(false);
            Require(tree.Contains("FOLDER id=" + innerId + " parent=" + RootId(tree), StringComparison.Ordinal) &&
                    tree.Contains("FOLDER id=" + alphaId + " parent=" + innerId, StringComparison.Ordinal) &&
                    tree.Contains("FILE id=" + deepId + " parent=" + innerId, StringComparison.Ordinal) &&
                    tree.Contains("FILE id=" + noteId + " parent=" + alphaId, StringComparison.Ordinal),
                "Moving a folder changed its own id, its descendants or their parent relations.");
            var cycle = await WorkspaceAsync(executable, fixture,
                "workspace-move", "--node-id", innerId, "--parent-id", alphaId,
                "--expected-tree-revision", await TreeRevisionAsync(executable, fixture).ConfigureAwait(false),
                "--expected-node-revision", "2").ConfigureAwait(false);
            RequireWorkspaceRefusal(cycle, WorkspaceErrorCodes.Cycle, "a folder moved into its own descendant");
            var intoFile = await WorkspaceAsync(executable, fixture,
                "workspace-move", "--node-id", alphaId, "--parent-id", noteId,
                "--expected-tree-revision", await TreeRevisionAsync(executable, fixture).ConfigureAwait(false),
                "--expected-node-revision", "3").ConfigureAwait(false);
            RequireWorkspaceRefusal(intoFile, WorkspaceErrorCodes.InvalidParent, "a file used as a move destination");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, deepId, 1, "s12t3-deep-after-move.bin").ConfigureAwait(false),
                S12T3DeepBytes, "binary after the move");
            Console.WriteLine("PASS move preserves ids, descendant relations and bytes; cycles and file destinations are refused");

            // Recursive copy: fresh identities, independent revisions, no anchors.
            var copy = await WorkspaceAsync(executable, fixture,
                "workspace-copy", "--node-id", alphaId, "--parent-id", "root", "--name", S12T3Copy,
                "--expected-tree-revision", await TreeRevisionAsync(executable, fixture).ConfigureAwait(false)).ConfigureAwait(false);
            Require(copy.ExitCode == 0 && copy.StandardOutput.Contains("COPY id=", StringComparison.Ordinal),
                "The owner CLI did not copy the folder (stderr=" + copy.StandardError.Trim() + ").");
            var copyRootId = WorkspaceToken(copy.StandardOutput, "id");
            Require(!string.Equals(copyRootId, alphaId, StringComparison.Ordinal),
                "Copying a folder reused the source identity.");
            Require(WorkspaceNumber(copy.StandardOutput, "folders") == 1 && WorkspaceNumber(copy.StandardOutput, "files") == 1,
                "Copying a one-folder/one-file subtree reported the wrong created shape.");
            var treeRevisionAfterCopy = TreeRevision(copy.StandardOutput);
            tree = await TreeAsync(executable, fixture).ConfigureAwait(false);
            var copiedNoteId = ChildId(tree, copyRootId, S12T3NoteRenamed);
            Require(!string.Equals(copiedNoteId, noteId, StringComparison.Ordinal),
                "The copied file reused the source identity.");
            Require(tree.Contains("FOLDER id=" + copyRootId + " parent=" + RootId(tree) + " name=" + JsonSerializer.Serialize(S12T3Copy), StringComparison.Ordinal),
                "The copy did not take the requested name at the destination.");
            Require(tree.Contains("FILE id=" + copiedNoteId + " parent=" + copyRootId + " name=" + JsonSerializer.Serialize(S12T3NoteRenamed), StringComparison.Ordinal),
                "The copied file is not inside the copied folder.");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, copiedNoteId, null, "s12t3-copy-note.md").ConfigureAwait(false),
                noteBytes, "copied file content");
            Require(tree.Contains("FILE id=" + copiedNoteId + " parent=" + copyRootId + " name=" + JsonSerializer.Serialize(S12T3NoteRenamed) + " revision=1 contentRevision=1", StringComparison.Ordinal),
                "The copy did not start its own content revision 1.");
            var copyAnchors = await WorkspaceAsync(executable, fixture,
                "workspace-anchor-list", "--file-id", copiedNoteId).ConfigureAwait(false);
            Require(copyAnchors.ExitCode == 0 && copyAnchors.StandardOutput.Contains("ANCHORS file=" + copiedNoteId + " count=0", StringComparison.Ordinal),
                "The copy inherited a cross-document citation anchor.");
            var copyIntoOwnSubtree = await WorkspaceAsync(executable, fixture,
                "workspace-copy", "--node-id", alphaId, "--parent-id", alphaId,
                "--expected-tree-revision", treeRevisionAfterCopy).ConfigureAwait(false);
            RequireWorkspaceRefusal(copyIntoOwnSubtree, WorkspaceErrorCodes.Cycle, "a folder copied into itself");
            var copyOccupied = await WorkspaceAsync(executable, fixture,
                "workspace-copy", "--node-id", noteId, "--parent-id", alphaId,
                "--expected-tree-revision", treeRevisionAfterCopy).ConfigureAwait(false);
            RequireWorkspaceRefusal(copyOccupied, WorkspaceErrorCodes.NameConflict, "a copy with an occupied destination name");
            Console.WriteLine("PASS folder copy creates fresh ids and membership with independent revisions and no inherited anchors");

            // Explicit replacement: new content revision on the same id, anchor stale, old bytes retained.
            var replace = await WorkspaceAsync(executable, fixture,
                "workspace-revise", "--file-id", noteId,
                "--expected-content-revision", "1",
                "--input-file", noteRevisionPath).ConfigureAwait(false);
            Require(replace.ExitCode == 0 && replace.StandardOutput.Contains("contentRevision=2", StringComparison.Ordinal),
                "The explicit replace did not create content revision 2 (stderr=" + replace.StandardError.Trim() + ").");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, noteId, 1, "s12t3-note-old-rev.md").ConfigureAwait(false),
                noteBytes, "retained content revision 1 after replacement");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, noteId, 2, "s12t3-note-new-rev.md").ConfigureAwait(false),
                noteRevisionBytes, "content revision 2 after replacement");
            var staleAnchor = await WorkspaceAsync(executable, fixture,
                "workspace-anchor-list", "--file-id", noteId).ConfigureAwait(false);
            Require(staleAnchor.ExitCode == 0 &&
                    staleAnchor.StandardOutput.Contains("id=" + anchorId + " file=" + noteId + " contentRevision=1", StringComparison.Ordinal) &&
                    staleAnchor.StandardOutput.Contains("state=stale", StringComparison.Ordinal),
                "The replacement did not mark the old anchor stale at its original revision.");
            var staleRevision = await TreeRevisionAsync(executable, fixture).ConfigureAwait(false);
            var failedReplace = await WorkspaceAsync(executable, fixture,
                "workspace-revise", "--file-id", noteId,
                "--expected-content-revision", "1",
                "--input-file", noteRevisionPath).ConfigureAwait(false);
            RequireWorkspaceRefusal(failedReplace, WorkspaceErrorCodes.Conflict, "a stale content revision");
            Require(await TreeRevisionAsync(executable, fixture).ConfigureAwait(false) == staleRevision,
                "A refused replacement changed the committed workspace revision.");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, noteId, 2, "s12t3-note-after-refusal.md").ConfigureAwait(false),
                noteRevisionBytes, "content revision 2 after a refused replacement");
            Console.WriteLine("PASS explicit replace advances the same id, retains old bytes and marks old anchors stale without rebinding");

            // Negative preconditions: stale tree, root authority, anchor range.
            var staleTree = await WorkspaceAsync(executable, fixture,
                "workspace-rename", "--node-id", noteId, "--name", "stale-s12t3",
                "--expected-tree-revision", "1",
                "--expected-node-revision", "2").ConfigureAwait(false);
            RequireWorkspaceRefusal(staleTree, WorkspaceErrorCodes.Conflict, "a stale tree revision");
            var staleNode = await WorkspaceAsync(executable, fixture,
                "workspace-rename", "--node-id", noteId, "--name", "stale-node-s12t3",
                "--expected-tree-revision", staleRevision,
                "--expected-node-revision", "1").ConfigureAwait(false);
            RequireWorkspaceRefusal(staleNode, WorkspaceErrorCodes.Conflict, "a stale node revision");
            tree = await TreeAsync(executable, fixture).ConfigureAwait(false);
            var rootId = RootId(tree);
            var renameRoot = await WorkspaceAsync(executable, fixture,
                "workspace-rename", "--node-id", rootId, "--name", "root-s12t3",
                "--expected-tree-revision", staleRevision,
                "--expected-node-revision", "1").ConfigureAwait(false);
            RequireWorkspaceRefusal(renameRoot, WorkspaceErrorCodes.RootImmutable, "the workspace root");
            var deleteRootPreview = await WorkspaceAsync(executable, fixture,
                "workspace-delete-preview", "--node-id", rootId).ConfigureAwait(false);
            RequireWorkspaceRefusal(deleteRootPreview, WorkspaceErrorCodes.RootImmutable, "a delete preview of the workspace root");
            var badAnchor = await WorkspaceAsync(executable, fixture,
                "workspace-anchor-create", "--file-id", noteId,
                "--expected-content-revision", "2",
                "--offset", "100000", "--length", "4",
                "--expected-tree-revision", staleRevision).ConfigureAwait(false);
            RequireWorkspaceRefusal(badAnchor, WorkspaceErrorCodes.AnchorInvalid, "an anchor range past the revision end");
            var staleAnchorRevision = await WorkspaceAsync(executable, fixture,
                "workspace-anchor-create", "--file-id", noteId,
                "--expected-content-revision", "1",
                "--offset", "0", "--length", "4",
                "--expected-tree-revision", staleRevision).ConfigureAwait(false);
            RequireWorkspaceRefusal(staleAnchorRevision, WorkspaceErrorCodes.Conflict, "an anchor over a stale content revision");
            Console.WriteLine("PASS stale revisions, root authority and out-of-range anchors refuse without partial writes");

            // Tombstone delete, occupied restore refusal, fresh id at the freed name, restore.
            var deletePreview = await WorkspaceAsync(executable, fixture,
                "workspace-delete-preview", "--node-id", noteId).ConfigureAwait(false);
            Require(deletePreview.ExitCode == 0 && deletePreview.StandardOutput.Contains("descendants=0", StringComparison.Ordinal),
                "The file delete preview reported unexpected descendants (stderr=" + deletePreview.StandardError.Trim() + ").");
            var filePreviewToken = WorkspaceToken(deletePreview.StandardOutput, "preview");
            var wrongToken = await WorkspaceAsync(executable, fixture,
                "workspace-delete", "--node-id", noteId,
                "--expected-tree-revision", staleRevision,
                "--expected-node-revision", "2",
                "--preview", new string('0', filePreviewToken.Length)).ConfigureAwait(false);
            RequireWorkspaceRefusal(wrongToken, WorkspaceErrorCodes.Conflict, "a delete with a mismatched preview");
            var deleteFile = await WorkspaceAsync(executable, fixture,
                "workspace-delete", "--node-id", noteId,
                "--expected-tree-revision", staleRevision,
                "--expected-node-revision", "2",
                "--preview", filePreviewToken).ConfigureAwait(false);
            Require(deleteFile.ExitCode == 0 && deleteFile.StandardOutput.Contains("DELETE id=" + noteId, StringComparison.Ordinal),
                "The owner CLI did not tombstone the file (stderr=" + deleteFile.StandardError.Trim() + ").");
            Require(WorkspaceNumber(deleteFile.StandardOutput, "files") == 1,
                "The tombstone did not report the one file that left the tree.");
            var deletedRevision = TreeRevision(deleteFile.StandardOutput);
            var deletedStatus = await ReadWorkspaceStatusAsync(executable, fixture).ConfigureAwait(false);
            Require(deletedStatus.Contains("deleted=1", StringComparison.Ordinal),
                "The status did not report the retained tombstone.");
            var readDeleted = await WorkspaceAsync(executable, fixture,
                "workspace-read", "--file-id", noteId).ConfigureAwait(false);
            RequireWorkspaceRefusal(readDeleted, WorkspaceErrorCodes.Deleted, "a read of a tombstoned file");
            var anchorDeleted = await WorkspaceAsync(executable, fixture,
                "workspace-anchor-list", "--file-id", noteId).ConfigureAwait(false);
            RequireWorkspaceRefusal(anchorDeleted, WorkspaceErrorCodes.Deleted, "the anchors of a tombstoned file");

            var replacementNamePath = await WriteWorkspaceInputAsync(
                fixture, "input-note-name-reuse-s12t3.md", Encoding.UTF8.GetBytes("replacement for the freed name")).ConfigureAwait(false);
            var newAtFreedName = await WorkspaceAsync(executable, fixture,
                "workspace-import", "--parent-id", alphaId, "--name", S12T3NoteRenamed,
                "--input-file", replacementNamePath,
                "--expected-tree-revision", deletedRevision).ConfigureAwait(false);
            Require(newAtFreedName.ExitCode == 0, "A new file could not take the freed exact name (stderr=" + newAtFreedName.StandardError.Trim() + ").");
            var newFileId = WorkspaceToken(newAtFreedName.StandardOutput, "id");
            Require(!string.Equals(newFileId, noteId, StringComparison.Ordinal),
                "A new file at the deleted path reused the tombstoned identity.");
            var occupiedRestore = await WorkspaceAsync(executable, fixture,
                "workspace-restore", "--node-id", noteId,
                "--expected-tree-revision", TreeRevision(newAtFreedName.StandardOutput)).ConfigureAwait(false);
            RequireWorkspaceRefusal(occupiedRestore, WorkspaceErrorCodes.RestoreConflict, "a restore into an occupied location");

            var deleteReusePreview = await WorkspaceAsync(executable, fixture,
                "workspace-delete-preview", "--node-id", newFileId).ConfigureAwait(false);
            Require(deleteReusePreview.ExitCode == 0, "The second delete preview failed (stderr=" + deleteReusePreview.StandardError.Trim() + ").");
            var deleteReuse = await WorkspaceAsync(executable, fixture,
                "workspace-delete", "--node-id", newFileId,
                "--expected-tree-revision", TreeRevision(newAtFreedName.StandardOutput),
                "--expected-node-revision", "1",
                "--preview", WorkspaceToken(deleteReusePreview.StandardOutput, "preview")).ConfigureAwait(false);
            Require(deleteReuse.ExitCode == 0, "The second delete failed (stderr=" + deleteReuse.StandardError.Trim() + ").");
            var restoreFile = await WorkspaceAsync(executable, fixture,
                "workspace-restore", "--node-id", noteId,
                "--expected-tree-revision", TreeRevision(deleteReuse.StandardOutput)).ConfigureAwait(false);
            Require(restoreFile.ExitCode == 0 && restoreFile.StandardOutput.Contains("RESTORE id=" + noteId, StringComparison.Ordinal),
                "The owner CLI did not restore the tombstone (stderr=" + restoreFile.StandardError.Trim() + ").");
            Require(WorkspaceNumber(restoreFile.StandardOutput, "restored") == 1,
                "Restoring one tombstone reported the wrong restored count.");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, noteId, 2, "s12t3-restored-note.md").ConfigureAwait(false),
                noteRevisionBytes, "restored latest content");
            var restoredAnchors = await WorkspaceAsync(executable, fixture,
                "workspace-anchor-list", "--file-id", noteId).ConfigureAwait(false);
            Require(restoredAnchors.ExitCode == 0 && restoredAnchors.StandardOutput.Contains("id=" + anchorId, StringComparison.Ordinal) &&
                    restoredAnchors.StandardOutput.Contains("state=stale", StringComparison.Ordinal),
                "Restoring the file lost or rebound its anchor.");
            var tombstones = await WorkspaceAsync(executable, fixture, "workspace-tombstones").ConfigureAwait(false);
            Require(tombstones.ExitCode == 0 &&
                    tombstones.StandardOutput.Contains("TOMBSTONES count=1", StringComparison.Ordinal) &&
                    tombstones.StandardOutput.Contains("id=" + newFileId, StringComparison.Ordinal),
                "The tombstone listing did not report exactly the still-deleted node.");
            Console.WriteLine("PASS delete is a revision-checked tombstone; the freed name takes a fresh id and restore refuses an occupied location");

            // Recursive folder delete requires the preview and restores as one group.
            var folderPreview = await WorkspaceAsync(executable, fixture,
                "workspace-delete-preview", "--node-id", alphaId).ConfigureAwait(false);
            Require(folderPreview.ExitCode == 0 && folderPreview.StandardOutput.Contains("descendants=1", StringComparison.Ordinal),
                "The folder delete preview did not list the live descendant (stderr=" + folderPreview.StandardError.Trim() + ").");
            Require(folderPreview.StandardOutput.Contains("id=" + noteId, StringComparison.Ordinal),
                "The folder delete preview did not identify the descendant it would tombstone.");
            var folderPreviewToken = WorkspaceToken(folderPreview.StandardOutput, "preview");
            var folderTreeRevision = PreviewTreeRevision(folderPreview.StandardOutput);
            var folderDelete = await WorkspaceAsync(executable, fixture,
                "workspace-delete", "--node-id", alphaId,
                "--expected-tree-revision", folderTreeRevision,
                "--expected-node-revision", "3",
                "--preview", folderPreviewToken).ConfigureAwait(false);
            Require(folderDelete.ExitCode == 0 && folderDelete.StandardOutput.Contains("DELETE id=" + alphaId, StringComparison.Ordinal),
                "The owner CLI did not tombstone the folder group (stderr=" + folderDelete.StandardError.Trim() + ").");
            Require(WorkspaceNumber(folderDelete.StandardOutput, "folders") == 1 &&
                    WorkspaceNumber(folderDelete.StandardOutput, "files") == 1,
                "The recursive tombstone did not report the whole previewed group.");
            var groupDeleteId = WorkspaceToken(folderDelete.StandardOutput, "deletion");
            var groupedTombstones = await WorkspaceAsync(executable, fixture, "workspace-tombstones").ConfigureAwait(false);
            Require(groupedTombstones.ExitCode == 0 &&
                    CountOccurrences(groupedTombstones.StandardOutput, "deletion=" + groupDeleteId) == 2,
                "The recursive delete did not keep one deletion group for the folder and its descendant.");
            var groupTree = await TreeAsync(executable, fixture).ConfigureAwait(false);
            Require(!groupTree.Contains("FOLDER id=" + alphaId, StringComparison.Ordinal) &&
                    !groupTree.Contains("FILE id=" + noteId, StringComparison.Ordinal),
                "A tombstoned folder or its descendant still appeared in the live tree.");
            var restoreFolder = await WorkspaceAsync(executable, fixture,
                "workspace-restore", "--node-id", alphaId,
                "--expected-tree-revision", TreeRevision(folderDelete.StandardOutput)).ConfigureAwait(false);
            Require(restoreFolder.ExitCode == 0 && WorkspaceNumber(restoreFolder.StandardOutput, "restored") == 2,
                "Restoring the folder group did not reinstate exactly the two tombstoned nodes (stderr=" +
                restoreFolder.StandardError.Trim() + ").");
            tree = await TreeAsync(executable, fixture).ConfigureAwait(false);
            Require(tree.Contains("FOLDER id=" + alphaId + " parent=" + innerId, StringComparison.Ordinal) &&
                    tree.Contains("FILE id=" + noteId + " parent=" + alphaId, StringComparison.Ordinal),
                "The restored folder group did not return with its original identities and relations.");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, noteId, 2, "s12t3-restored-group-note.md").ConfigureAwait(false),
                noteRevisionBytes, "restored group content");
            var restoreLive = await WorkspaceAsync(executable, fixture,
                "workspace-restore", "--node-id", noteId,
                "--expected-tree-revision", TreeRevision(restoreFolder.StandardOutput)).ConfigureAwait(false);
            RequireWorkspaceRefusal(restoreLive, WorkspaceErrorCodes.NotDeleted, "a restore of a live node");
            Console.WriteLine("PASS recursive folder delete requires the bound preview and restores the whole group into free locations");

            // A copy that cannot read one source revision stages nothing.
            var rollbackFolder = await WorkspaceAsync(executable, fixture,
                "workspace-mkdir", "--parent-id", "root", "--name", S12T3RollbackFolder,
                "--expected-tree-revision", await TreeRevisionAsync(executable, fixture).ConfigureAwait(false)).ConfigureAwait(false);
            Require(rollbackFolder.ExitCode == 0, "The rollback folder was not created (stderr=" + rollbackFolder.StandardError.Trim() + ").");
            var rollbackFolderId = WorkspaceToken(rollbackFolder.StandardOutput, "id");
            var blobsBefore = BlobNames(workspaceRoot);
            var rollbackPayload = RandomNumberGenerator.GetBytes(8192);
            var rollbackPath = await WriteWorkspaceInputAsync(fixture, "input-" + S12T3RollbackPayload, rollbackPayload).ConfigureAwait(false);
            var rollbackImport = await WorkspaceAsync(executable, fixture,
                "workspace-import", "--parent-id", rollbackFolderId, "--name", S12T3RollbackPayload,
                "--input-file", rollbackPath,
                "--expected-tree-revision", TreeRevision(rollbackFolder.StandardOutput)).ConfigureAwait(false);
            Require(rollbackImport.ExitCode == 0, "The rollback source file was not stored (stderr=" + rollbackImport.StandardError.Trim() + ").");
            var blobsAdded = BlobNames(workspaceRoot).Except(blobsBefore, StringComparer.Ordinal).ToArray();
            Require(blobsAdded.Length == 1, "The rollback source did not stage exactly one content ciphertext.");
            var rollbackBlobPath = Path.Combine(workspaceRoot, "blobs", blobsAdded[0]);
            var rollbackBlobOriginal = await File.ReadAllBytesAsync(rollbackBlobPath).ConfigureAwait(false);
            TamperCommittedStateCiphertext(rollbackBlobPath);
            var rollbackRevision = TreeRevision(rollbackImport.StandardOutput);
            var inventoryBeforeRefusedCopy = WorkspaceInventory(workspaceRoot);
            var refusedCopy = await WorkspaceAsync(executable, fixture,
                "workspace-copy", "--node-id", rollbackFolderId, "--parent-id", "root", "--name", "rollback-copy-s12t3",
                "--expected-tree-revision", rollbackRevision).ConfigureAwait(false);
            RequireWorkspaceRefusal(refusedCopy, WorkspaceErrorCodes.Unavailable, "a copy over an unreadable source revision");
            Require(string.Equals(inventoryBeforeRefusedCopy, WorkspaceInventory(workspaceRoot), StringComparison.Ordinal),
                "A refused copy left staged ciphertext or changed the committed workspace.");
            Require(await TreeRevisionAsync(executable, fixture).ConfigureAwait(false) == rollbackRevision,
                "A refused copy advanced the committed workspace revision.");
            await File.WriteAllBytesAsync(rollbackBlobPath, rollbackBlobOriginal).ConfigureAwait(false);
            var acceptedCopy = await WorkspaceAsync(executable, fixture,
                "workspace-copy", "--node-id", rollbackFolderId, "--parent-id", "root", "--name", "rollback-copy-s12t3",
                "--expected-tree-revision", rollbackRevision).ConfigureAwait(false);
            Require(acceptedCopy.ExitCode == 0, "The copy did not succeed once the source ciphertext was restored (stderr=" +
                acceptedCopy.StandardError.Trim() + ").");
            var rollbackCopyRoot = WorkspaceToken(acceptedCopy.StandardOutput, "id");
            var rollbackCopyFile = ChildId(await TreeAsync(executable, fixture).ConfigureAwait(false), rollbackCopyRoot, S12T3RollbackPayload);
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, rollbackCopyFile, null, "s12t3-rollback-copy.bin").ConfigureAwait(false),
                rollbackPayload, "copied opaque payload");
            Console.WriteLine("PASS a copy that cannot read a source revision refuses with no staged residue and succeeds once readable");

            // Lock stops mutations; restart preserves identity, revisions and tombstones.
            var beforeRestartTree = await TreeAsync(executable, fixture).ConfigureAwait(false);
            var beforeRestartStatus = await ReadWorkspaceStatusAsync(executable, fixture).ConfigureAwait(false);
            var beforeRestartRevision = WorkspaceNumber(beforeRestartStatus, "revision");
            var lockResult = await RunOwnerCommandAsync(executable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockResult.ExitCode == 0, "The owner CLI did not lock the vault.");
            var lockedRename = await WorkspaceAsync(executable, fixture,
                "workspace-rename", "--node-id", noteId, "--name", "locked-s12t3",
                "--expected-tree-revision", beforeRestartRevision.ToString(CultureInfo.InvariantCulture),
                "--expected-node-revision", "4").ConfigureAwait(false);
            Require(lockedRename.ExitCode != 0 && lockedRename.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "A locked vault accepted a workspace mutation.");
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(executable, fixture.ControlPipe).ConfigureAwait(false)).Contains("vault=locked", StringComparison.Ordinal),
                "The restarted service did not begin locked.");
            await UnlockWorkspaceAsync(executable, fixture, passphrase).ConfigureAwait(false);
            var restartedStatus = await ReadWorkspaceStatusAsync(executable, fixture).ConfigureAwait(false);
            Require(restartedStatus.Contains("revision=" + beforeRestartRevision.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
                "The restarted epoch reopened a different committed workspace revision.");
            Require(string.Equals(beforeRestartTree, await TreeAsync(executable, fixture).ConfigureAwait(false), StringComparison.Ordinal),
                "The restart changed identities, names, parents or revisions.");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, noteId, 2, "s12t3-restart-note.md").ConfigureAwait(false),
                noteRevisionBytes, "content after restart");
            var restartedAnchors = await WorkspaceAsync(executable, fixture,
                "workspace-anchor-list", "--file-id", noteId).ConfigureAwait(false);
            Require(restartedAnchors.ExitCode == 0 && restartedAnchors.StandardOutput.Contains("state=stale", StringComparison.Ordinal),
                "The restart rebound or dropped the retained anchor state.");
            var restartedTombstones = await WorkspaceAsync(executable, fixture, "workspace-tombstones").ConfigureAwait(false);
            Require(restartedTombstones.ExitCode == 0 && restartedTombstones.StandardOutput.Contains("id=" + newFileId, StringComparison.Ordinal),
                "The restart lost the retained tombstone.");
            Console.WriteLine("PASS lock refuses mutations and restart preserves ids, revisions, anchors and tombstones");

            // The external plaintext import is an independent copy, never written back.
            Require((await File.ReadAllBytesAsync(notePath).ConfigureAwait(false)).AsSpan().SequenceEqual(inputBytesBefore),
                "A workspace act modified the external plaintext source file.");
            Require(File.GetLastWriteTimeUtc(notePath) == inputWriteTimeBefore,
                "A workspace act rewrote the external plaintext source file's timestamp.");

            await VerifyAgentChannelCannotReachWorkspaceMutationAsync(fixture, noteId).ConfigureAwait(false);

            RequireNoPlaintextWorkspaceState(workspaceRoot,
                new[]
                {
                    S12T3AlphaRenamed,
                    S12T3NoteRenamed,
                    S12T3Inner,
                    S12T3NoteMarker,
                    S12T3NoteRevisionMarker
                },
                new[] { S12T3DeepBytes, noteBytes, noteRevisionBytes, S12T3PinnedRange, rollbackPayload });
            Console.WriteLine("PASS S12-T3 stable identity, mutation semantics, tombstones, anchors and restart");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static Task<CommandResult> WorkspaceAsync(string executable, SmokeFixture fixture, params object[] arguments)
    {
        var all = new List<string>
        {
            "owner",
            Convert.ToString(arguments[0], CultureInfo.InvariantCulture) ?? string.Empty,
            "--control-pipe",
            fixture.ControlPipe
        };
        all.AddRange(arguments.Skip(1).Select(value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
        return RunOwnerCommandAsync(executable, all.ToArray());
    }

    private static async Task<string> TreeAsync(string executable, SmokeFixture fixture)
    {
        var result = await WorkspaceAsync(executable, fixture, "workspace-tree").ConfigureAwait(false);
        Require(result.ExitCode == 0, "The owner workspace-tree failed (stderr=" + result.StandardError.Trim() + ").");
        return result.StandardOutput;
    }

    private static async Task<long> TreeRevisionAsync(string executable, SmokeFixture fixture) =>
        WorkspaceNumber(await ReadWorkspaceStatusAsync(executable, fixture).ConfigureAwait(false), "revision");

    private static long TreeRevision(string commandOutput) => WorkspaceNumber(commandOutput, "revision");

    /// <summary>
    /// The tree revision of a delete preview. It is read from the summary line
    /// rather than the whole output because every previewed descendant line
    /// carries its own node revision, and the delete must present the tree
    /// revision the preview was computed at.
    /// </summary>
    private static long PreviewTreeRevision(string commandOutput)
    {
        foreach (var line in commandOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("DELETE PREVIEW ", StringComparison.Ordinal))
            {
                return WorkspaceNumber(line, "revision");
            }
        }

        throw new InvalidOperationException("The delete preview did not report its summary line.");
    }

    private static string RootId(string tree)
    {
        foreach (var line in tree.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("FOLDER id=", StringComparison.Ordinal) && line.Contains(" parent=- ", StringComparison.Ordinal))
            {
                return WorkspaceToken(line, "id");
            }
        }

        throw new InvalidOperationException("The workspace tree did not report a root folder.");
    }

    private static string ChildId(string tree, string parentId, string name)
    {
        var expected = "parent=" + parentId + " name=" + JsonSerializer.Serialize(name) + " ";
        foreach (var line in tree.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains(expected, StringComparison.Ordinal))
            {
                return WorkspaceToken(line, "id");
            }
        }

        throw new InvalidOperationException("The workspace tree did not report child '" + name + "' of " + parentId + ".");
    }

    private static string[] BlobNames(string workspaceRoot)
    {
        var blobDirectory = Path.Combine(workspaceRoot, "blobs");
        return Directory.Exists(blobDirectory)
            ? Directory.GetFiles(blobDirectory, "*", SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetFileName(path) ?? string.Empty)
                .Where(name => !IsTemporaryFileName(name))
                .ToArray()
            : Array.Empty<string>();
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string DigestRange(byte[] content, int offset, int length) =>
        Convert.ToHexStringLower(SHA256.HashData(content.AsSpan(offset, length)));

    /// <summary>
    /// Attempts the owner-only rename over the agent channel. The agent
    /// channel has no owner authority, so the service must refuse it before any
    /// private state is read or changed.
    /// </summary>
    private static async Task VerifyAgentChannelCannotReachWorkspaceMutationAsync(SmokeFixture fixture, string nodeId)
    {
        var requestJson = JsonSerializer.Serialize(new
        {
            protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
            operation = ServiceRequestHandler.OwnerWorkspaceNodeRenameOperation,
            resourceId = nodeId,
            workspaceName = "agent-rename-s12t3",
            workspaceTreeRevision = 1,
            workspaceNodeRevision = 1
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var result = await PipeClient.InvokeAgentAsync(fixture.AgentPipe, requestJson, CancellationToken.None).ConfigureAwait(false);
        Require(result.Success && result.Json.Contains("forbidden", StringComparison.Ordinal),
            "The agent channel did not refuse an owner workspace mutation (reply=" + result.Json + ").");
        Console.WriteLine("PASS the agent/MCP channel cannot reach owner workspace mutations");
    }
}
