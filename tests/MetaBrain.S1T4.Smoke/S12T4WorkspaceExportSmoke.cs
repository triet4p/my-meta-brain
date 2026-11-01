using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Security;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S12-T4 runtime proof for the owner original read/export and portability
/// path through the actual owner CLI and shared service: destination and scope
/// preview, typed plaintext confirmation with a binding token, exact retained
/// bytes for text/empty/binary/UTF-16 originals, a pinned older revision in
/// place of the revised latest, a versioned metadata document carrying exactly
/// the in-scope identities/hierarchy/anchors, destination collision and
/// traversal/reparse refusal, stale preview and lock refusal, restart stability,
/// and agent-channel denial. Every destination is synthetic and lives only in
/// the test fixture's output directory.
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S12T4Alpha = "export-alpha-s12t4";
    private const string S12T4Nested = "nested-s12t4";
    private const string S12T4Note = "note-s12t4.md";
    private const string S12T4Empty = "empty-s12t4.bin";
    private const string S12T4Binary = "binary-s12t4.bin";
    private const string S12T4Utf16 = "utf16-s12t4.txt";
    private const string S12T4Deep = "deep-s12t4.jsonl";
    private const string S12T4OutsideFolder = "outside-s12t4";
    private const string S12T4OutsideFile = "outside-note-s12t4.md";
    private const string S12T4HostileFolder = "hostile-name-s12t4";
    private const string S12T4DeviceFolder = "device-name-s12t4";
    private const string S12T4CaseFolder = "case-collision-s12t4";
    private const string S12T4DeletedFolder = "deleted-s12t4";
    private const string S12T4DeletedFile = "deleted-note-s12t4.md";
    private const string S12T4StaleFolder = "stale-preview-s12t4";
    private const string S12T4StaleFile = "stale-note-s12t4.md";
    private const string S12T4StaleExtra = "stale-extra-s12t4.md";
    private const string S12T4Sidecar = "metabrain-workspace-export.json";
    private const string S12T4NoteMarker = "synthetic-s12t4-note-marker-4c81e7";
    private const string S12T4NoteRevisionMarker = "synthetic-s12t4-note-revision-marker-a2d90b";
    private const string S12T4OutsideMarker = "synthetic-s12t4-outside-marker-71ff35";
    private static readonly byte[] S12T4PinnedRange = Encoding.ASCII.GetBytes("synthetic");
    private static readonly byte[] S12T4BinaryBytes = { 0x00, 0xFF, 0xFE, 0x81, 0xC0, 0xAF, 0xED, 0xA0, 0x80, 0x0A, 0x7F };

    public static async Task RunS12T4WorkspaceExportAsync(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S12-T4 export smoke is Windows-only.");
        }

        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        var workspaceRoot = Path.Combine(fixture.VaultDirectoryPath, "workspace");
        var outputDirectory = fixture.OutputDirectory;
        try
        {
            service = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            var passphrase = CreateSyntheticPassphrase();
            var provision = await RunOwnerCommandWithInputAsync(
                executable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The owner CLI did not provision the S12-T4 vault (stderr=" + provision.StandardError.Trim() + ").");

            var noteBytes = Encoding.UTF8.GetBytes(
                "synthetic " + S12T4NoteMarker + "\nsecond line for the anchored range\n");
            var noteRevisionBytes = Encoding.UTF8.GetBytes(
                "# synthetic S12-T4 note (replaced)\n\nmarker " + S12T4NoteRevisionMarker + "\n");
            var emptyBytes = Array.Empty<byte>();
            var utf16Bytes = Concatenate(Encoding.Unicode.GetPreamble(),
                Encoding.Unicode.GetBytes("UTF-16 original text " + S12T4NoteMarker + "\r\n"));
            var deepBytes = Encoding.UTF8.GetBytes(
                "{\"kind\":\"synthetic-deep\",\"marker\":\"" + S12T4NoteMarker + "\"}\n");
            var outsideBytes = Encoding.UTF8.GetBytes("outside scope marker " + S12T4OutsideMarker + "\n");

            var mkdirAlpha = await WorkspaceAsync(executable, fixture,
                "workspace-mkdir", "--parent-id", "root", "--name", S12T4Alpha,
                "--expected-tree-revision", "0").ConfigureAwait(false);
            Require(mkdirAlpha.ExitCode == 0 && mkdirAlpha.StandardOutput.Contains("MKDIR id=", StringComparison.Ordinal),
                "The owner CLI did not create the S12-T4 export folder (stderr=" + mkdirAlpha.StandardError.Trim() + ").");
            var alphaId = WorkspaceToken(mkdirAlpha.StandardOutput, "id");
            var treeRevision = TreeRevision(mkdirAlpha.StandardOutput);

            var mkdirNested = await WorkspaceAsync(executable, fixture,
                "workspace-mkdir", "--parent-id", alphaId, "--name", S12T4Nested,
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(mkdirNested.ExitCode == 0, "The owner CLI did not create the nested export folder (stderr=" +
                mkdirNested.StandardError.Trim() + ").");
            var nestedId = WorkspaceToken(mkdirNested.StandardOutput, "id");
            treeRevision = TreeRevision(mkdirNested.StandardOutput);

            var noteImport = await S12T4ImportAsync(executable, fixture, alphaId, S12T4Note, noteBytes,
                treeRevision, "input-s12t4-note.md").ConfigureAwait(false);
            var noteId = noteImport.FileId;
            treeRevision = noteImport.TreeRevision;
            var noteInputPath = Path.Combine(outputDirectory, "input-s12t4-note.md");
            var noteInputBytesBefore = await File.ReadAllBytesAsync(noteInputPath).ConfigureAwait(false);
            var noteInputWriteTimeBefore = File.GetLastWriteTimeUtc(noteInputPath);

            var emptyImport = await S12T4ImportAsync(executable, fixture, alphaId, S12T4Empty, emptyBytes,
                treeRevision, "input-s12t4-empty.bin").ConfigureAwait(false);
            treeRevision = emptyImport.TreeRevision;
            var binaryImport = await S12T4ImportAsync(executable, fixture, nestedId, S12T4Binary, S12T4BinaryBytes,
                treeRevision, "input-s12t4-binary.bin").ConfigureAwait(false);
            treeRevision = binaryImport.TreeRevision;
            var utf16Import = await S12T4ImportAsync(executable, fixture, alphaId, S12T4Utf16, utf16Bytes,
                treeRevision, "input-s12t4-utf16.txt").ConfigureAwait(false);
            treeRevision = utf16Import.TreeRevision;
            var deepImport = await S12T4ImportAsync(executable, fixture, nestedId, S12T4Deep, deepBytes,
                treeRevision, "input-s12t4-deep.jsonl").ConfigureAwait(false);
            treeRevision = deepImport.TreeRevision;

            var mkdirOutside = await WorkspaceAsync(executable, fixture,
                "workspace-mkdir", "--parent-id", "root", "--name", S12T4OutsideFolder,
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(mkdirOutside.ExitCode == 0, "The owner CLI did not create the out-of-scope folder (stderr=" +
                mkdirOutside.StandardError.Trim() + ").");
            var outsideFolderId = WorkspaceToken(mkdirOutside.StandardOutput, "id");
            treeRevision = TreeRevision(mkdirOutside.StandardOutput);
            var outsideImport = await S12T4ImportAsync(executable, fixture, outsideFolderId, S12T4OutsideFile,
                outsideBytes, treeRevision, "input-s12t4-outside.md").ConfigureAwait(false);
            treeRevision = outsideImport.TreeRevision;

            var outsideAnchor = await WorkspaceAsync(executable, fixture,
                "workspace-anchor-create", "--file-id", outsideImport.FileId,
                "--expected-content-revision", "1",
                "--offset", "0", "--length", S12T4PinnedRange.Length.ToString(CultureInfo.InvariantCulture),
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(outsideAnchor.ExitCode == 0 && outsideAnchor.StandardOutput.Contains("state=live", StringComparison.Ordinal),
                "The owner CLI did not pin the out-of-scope anchor (stderr=" + outsideAnchor.StandardError.Trim() + ").");
            var outsideAnchorId = WorkspaceToken(outsideAnchor.StandardOutput, "id");
            treeRevision = await TreeRevisionAsync(executable, fixture).ConfigureAwait(false);

            var noteAnchor = await WorkspaceAsync(executable, fixture,
                "workspace-anchor-create", "--file-id", noteId,
                "--expected-content-revision", "1",
                "--offset", "0", "--length", S12T4PinnedRange.Length.ToString(CultureInfo.InvariantCulture),
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(noteAnchor.ExitCode == 0 && noteAnchor.StandardOutput.Contains("state=live", StringComparison.Ordinal),
                "The owner CLI did not pin the in-scope anchor (stderr=" + noteAnchor.StandardError.Trim() + ").");
            var noteAnchorId = WorkspaceToken(noteAnchor.StandardOutput, "id");
            Require(string.Equals(WorkspaceToken(noteAnchor.StandardOutput, "sha256"),
                    DigestRange(noteBytes, 0, S12T4PinnedRange.Length), StringComparison.Ordinal),
                "The anchor did not record the digest of exactly the anchored bytes.");
            treeRevision = await TreeRevisionAsync(executable, fixture).ConfigureAwait(false);

            var reviseNote = await WorkspaceAsync(executable, fixture,
                "workspace-revise", "--file-id", noteId, "--expected-content-revision", "1",
                "--input-file", await WriteWorkspaceInputAsync(fixture, "input-s12t4-note-revision.md", noteRevisionBytes).ConfigureAwait(false)).ConfigureAwait(false);
            Require(reviseNote.ExitCode == 0 && reviseNote.StandardOutput.Contains("contentRevision=2", StringComparison.Ordinal),
                "The owner CLI did not revise the note to revision 2 (stderr=" + reviseNote.StandardError.Trim() + ").");
            treeRevision = TreeRevision(reviseNote.StandardOutput);

            var mkdirDeleted = await WorkspaceAsync(executable, fixture,
                "workspace-mkdir", "--parent-id", "root", "--name", S12T4DeletedFolder,
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(mkdirDeleted.ExitCode == 0, "The owner CLI did not create the tombstone folder (stderr=" +
                mkdirDeleted.StandardError.Trim() + ").");
            var deletedFolderId = WorkspaceToken(mkdirDeleted.StandardOutput, "id");
            treeRevision = TreeRevision(mkdirDeleted.StandardOutput);
            var deletedImport = await S12T4ImportAsync(executable, fixture, deletedFolderId, S12T4DeletedFile,
                Encoding.UTF8.GetBytes("deleted marker " + S12T4NoteMarker + "\n"), treeRevision,
                "input-s12t4-deleted.md").ConfigureAwait(false);
            treeRevision = deletedImport.TreeRevision;
            var deletePreview = await WorkspaceAsync(executable, fixture,
                "workspace-delete-preview", "--node-id", deletedFolderId).ConfigureAwait(false);
            Require(deletePreview.ExitCode == 0, "The delete preview failed (stderr=" + deletePreview.StandardError.Trim() + ").");
            var deleteSummary = S12T4Summary(deletePreview.StandardOutput, "DELETE PREVIEW ");
            var deleteFolder = await WorkspaceAsync(executable, fixture,
                "workspace-delete", "--node-id", deletedFolderId,
                "--expected-tree-revision", WorkspaceNumber(deleteSummary, "revision").ToString(CultureInfo.InvariantCulture),
                "--expected-node-revision", WorkspaceNumber(deleteSummary, "nodeRevision").ToString(CultureInfo.InvariantCulture),
                "--preview", WorkspaceToken(deleteSummary, "preview")).ConfigureAwait(false);
            Require(deleteFolder.ExitCode == 0 && deleteFolder.StandardOutput.Contains("DELETE id=" + deletedFolderId, StringComparison.Ordinal),
                "The owner CLI did not tombstone the folder group (stderr=" + deleteFolder.StandardError.Trim() + ").");
            treeRevision = TreeRevision(deleteFolder.StandardOutput);

            var mkdirHostile = await WorkspaceAsync(executable, fixture,
                "workspace-mkdir", "--parent-id", "root", "--name", S12T4HostileFolder,
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(mkdirHostile.ExitCode == 0, "The owner CLI did not create the hostile-name folder.");
            var hostileFolderId = WorkspaceToken(mkdirHostile.StandardOutput, "id");
            treeRevision = TreeRevision(mkdirHostile.StandardOutput);
            var hostileImport = await S12T4ImportAsync(executable, fixture, hostileFolderId, "bad\\name-s12t4",
                Encoding.UTF8.GetBytes("separator name " + S12T4NoteMarker + "\n"), treeRevision,
                "input-s12t4-hostile.md").ConfigureAwait(false);
            treeRevision = hostileImport.TreeRevision;

            var mkdirDevice = await WorkspaceAsync(executable, fixture,
                "workspace-mkdir", "--parent-id", "root", "--name", S12T4DeviceFolder,
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(mkdirDevice.ExitCode == 0, "The owner CLI did not create the device-name folder.");
            treeRevision = TreeRevision(mkdirDevice.StandardOutput);
            var deviceImport = await S12T4ImportAsync(executable, fixture, WorkspaceToken(mkdirDevice.StandardOutput, "id"),
                "CON", Encoding.UTF8.GetBytes("reserved device name " + S12T4NoteMarker + "\n"), treeRevision,
                "input-s12t4-device.md").ConfigureAwait(false);
            treeRevision = deviceImport.TreeRevision;

            var mkdirCase = await WorkspaceAsync(executable, fixture,
                "workspace-mkdir", "--parent-id", "root", "--name", S12T4CaseFolder,
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(mkdirCase.ExitCode == 0, "The owner CLI did not create the case-collision folder.");
            var caseFolderId = WorkspaceToken(mkdirCase.StandardOutput, "id");
            treeRevision = TreeRevision(mkdirCase.StandardOutput);
            var caseUpper = await S12T4ImportAsync(executable, fixture, caseFolderId, "Case-s12t4.md",
                Encoding.UTF8.GetBytes("upper case sibling\n"), treeRevision, "input-s12t4-case-upper.md").ConfigureAwait(false);
            treeRevision = caseUpper.TreeRevision;
            var caseLower = await S12T4ImportAsync(executable, fixture, caseFolderId, "case-s12t4.md",
                Encoding.UTF8.GetBytes("lower case sibling\n"), treeRevision, "input-s12t4-case-lower.md").ConfigureAwait(false);
            treeRevision = caseLower.TreeRevision;

            var mkdirStale = await WorkspaceAsync(executable, fixture,
                "workspace-mkdir", "--parent-id", "root", "--name", S12T4StaleFolder,
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(mkdirStale.ExitCode == 0, "The owner CLI did not create the stale-preview folder.");
            var staleFolderId = WorkspaceToken(mkdirStale.StandardOutput, "id");
            treeRevision = TreeRevision(mkdirStale.StandardOutput);
            var staleImport = await S12T4ImportAsync(executable, fixture, staleFolderId, S12T4StaleFile,
                Encoding.UTF8.GetBytes("stale marker " + S12T4NoteMarker + "\n"), treeRevision,
                "input-s12t4-stale.md").ConfigureAwait(false);
            treeRevision = staleImport.TreeRevision;

            var statusBefore = await ReadWorkspaceStatusAsync(executable, fixture).ConfigureAwait(false);
            var revisionBefore = WorkspaceNumber(statusBefore, "revision");
            Require(revisionBefore == treeRevision,
                "The tracked tree revision drifted from workspace-status before the export checks (status=" +
                revisionBefore.ToString(CultureInfo.InvariantCulture) + ", tracked=" +
                treeRevision.ToString(CultureInfo.InvariantCulture) + ").");

            // A file export pins exactly the retained revision the owner names,
            // even though a newer revision exists.
            var fileDestination = Path.Combine(outputDirectory, "export-file-s12t4");
            var filePreview = await S12T4PreviewAsync(executable, fixture, noteId, 1, fileDestination).ConfigureAwait(false);
            Require(filePreview.ExitCode == 0 && filePreview.StandardOutput.Contains("EXPORT DISCLOSURE", StringComparison.Ordinal),
                "The owner CLI did not preview the file export with its plaintext disclosure (stderr=" +
                filePreview.StandardError.Trim() + ").");
            var fileSummary = S12T4Summary(filePreview.StandardOutput, "EXPORT PREVIEW ");
            Require(WorkspaceNumber(fileSummary, "files") == 1 && WorkspaceNumber(fileSummary, "contentRevision") == 1,
                "The file preview did not pin revision 1 of exactly one file.");
            Require(WorkspaceNumber(fileSummary, "bytes") == noteBytes.LongLength &&
                WorkspaceNumber(fileSummary, "anchors") == 1,
                "The file preview reported the wrong size or anchor count.");
            Require(fileSummary.Contains("destination=\"" + fileDestination.Replace("\\", "\\\\") + "\"", StringComparison.Ordinal),
                "The file preview did not name the exact destination.");
            Require(!Directory.Exists(fileDestination) && !File.Exists(fileDestination),
                "The preview created the destination instead of only describing it.");
            Console.WriteLine("PASS the file export preview discloses exactly the pinned revision, destination and token without writing");

            var refusedConfirmation = await S12T4ExportAsync(executable, fixture, noteId, 1, fileDestination,
                WorkspaceToken(fileSummary, "token"), "no").ConfigureAwait(false);
            RequireWorkspaceRefusal(refusedConfirmation, WorkspaceErrorCodes.ExportConfirmation,
                "an export without the typed EXPORT confirmation");
            Require(!Directory.Exists(fileDestination) && !File.Exists(fileDestination),
                "A refused export still created the destination.");

            var tamperedToken = S12T4FlipToken(WorkspaceToken(fileSummary, "token"));
            var refusedToken = await S12T4ExportAsync(executable, fixture, noteId, 1, fileDestination,
                tamperedToken, "EXPORT").ConfigureAwait(false);
            RequireWorkspaceRefusal(refusedToken, WorkspaceErrorCodes.Conflict,
                "an export whose preview token was not the one previewed");
            Require(!Directory.Exists(fileDestination) && !File.Exists(fileDestination),
                "A stale-token export still created the destination.");

            var fileExport = await S12T4ExportAsync(executable, fixture, noteId, 1, fileDestination,
                WorkspaceToken(fileSummary, "token"), "EXPORT").ConfigureAwait(false);
            Require(fileExport.ExitCode == 0 && fileExport.StandardOutput.Contains("EXPORT written", StringComparison.Ordinal),
                "The owner CLI did not write the pinned file export (stderr=" + fileExport.StandardError.Trim() + ").");
            var fileExportLine = S12T4Summary(fileExport.StandardOutput, "EXPORT written ");
            Require(WorkspaceNumber(fileExportLine, "files") == 1 && WorkspaceNumber(fileExportLine, "folders") == 0,
                "The file export reported the wrong scope counts.");
            RequireDirectoryEntries(fileDestination, new[] { S12T4Note, S12T4Sidecar });
            RequireBytes(await File.ReadAllBytesAsync(Path.Combine(fileDestination, S12T4Note)).ConfigureAwait(false),
                noteBytes, "pinned revision 1 export");
            Require(new FileInfo(Path.Combine(fileDestination, S12T4Note)).Length == noteBytes.LongLength,
                "The exported file length is not the pinned revision length.");
            var fileSidecarPath = Path.Combine(fileDestination, S12T4Sidecar);
            Require(string.Equals(Sha256File(fileSidecarPath), WorkspaceToken(fileExportLine, "sidecarSha256"), StringComparison.Ordinal),
                "The reported sidecar digest is not the digest of the written metadata document.");

            using (var fileSidecar = JsonDocument.Parse(await File.ReadAllBytesAsync(fileSidecarPath).ConfigureAwait(false)))
            {
                var root = fileSidecar.RootElement;
                Require(string.Equals(root.GetProperty("format").GetString(), "metabrain-workspace-export/1", StringComparison.Ordinal) &&
                    root.GetProperty("metadataSchemaVersion").GetInt32() == 1,
                    "The metadata document does not carry its own export/metadata format version.");
                Require(root.GetProperty("workspaceId").GetString() is { Length: 32 } workspaceId &&
                    workspaceId.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f')),
                    "The metadata document does not carry the workspace identity.");
                Require(root.GetProperty("treeRevision").GetInt64() == WorkspaceNumber(fileSummary, "revision"),
                    "The metadata document does not carry the previewed tree revision.");
                var selected = root.GetProperty("selected");
                Require(string.Equals(selected.GetProperty("nodeId").GetString(), noteId, StringComparison.Ordinal) &&
                    selected.GetProperty("contentRevision").GetInt64() == 1 &&
                    selected.GetProperty("nodeRevision").GetInt64() == WorkspaceNumber(fileSummary, "nodeRevision"),
                    "The metadata document does not name the selected file and its pinned revision.");
                var nodes = root.GetProperty("nodes");
                Require(nodes.GetArrayLength() == 1, "The file metadata document listed nodes outside its scope.");
                var node = nodes[0];
                Require(node.GetProperty("parentId").ValueKind == JsonValueKind.Null,
                    "The file metadata document disclosed the parent identity outside the selected scope.");
                Require(string.Equals(node.GetProperty("path").GetString(), S12T4Note, StringComparison.Ordinal) &&
                    node.GetProperty("contentRevision").GetInt64() == 1 &&
                    string.Equals(node.GetProperty("sha256").GetString(), Sha256Hex(noteBytes), StringComparison.Ordinal),
                    "The file metadata document does not carry the exact path, revision and digest.");
                Require(node.GetProperty("retainedRevisions").GetArrayLength() == 2,
                    "The file metadata document dropped the retained revision list.");
                var anchors = root.GetProperty("anchors");
                Require(anchors.GetArrayLength() == 1 &&
                    string.Equals(anchors[0].GetProperty("anchorId").GetString(), noteAnchorId, StringComparison.Ordinal) &&
                    string.Equals(anchors[0].GetProperty("state").GetString(), "stale", StringComparison.Ordinal) &&
                    anchors[0].GetProperty("contentRevision").GetInt64() == 1,
                    "The file metadata document did not carry the in-scope stale anchor truthfully.");
                Require(root.GetProperty("relations").GetArrayLength() == 0,
                    "The metadata document invented relations for a format that has none.");
                var sidecarBytes = await File.ReadAllBytesAsync(fileSidecarPath).ConfigureAwait(false);
                S12T4RequireAbsent(sidecarBytes, alphaId, "the selected file's parent identity");
                S12T4RequireAbsent(sidecarBytes, outsideAnchorId, "an anchor of a document outside the scope");
                S12T4RequireAbsent(sidecarBytes, S12T4OutsideMarker, "out-of-scope content");
                S12T4RequireAbsent(sidecarBytes, S12T4OutsideFile, "an out-of-scope document name");
            }

            Console.WriteLine("PASS a revised file exports its own pinned revision and a versioned sidecar with only the in-scope identity");

            // A folder export preserves exact logical names, hierarchy, empty,
            // binary and UTF-16 bytes, and it pins the revision current at the
            // preview instead of following later content.
            var folderDestination = Path.Combine(outputDirectory, "export-folder-s12t4");
            var folderPreview = await S12T4PreviewAsync(executable, fixture, alphaId, null, folderDestination).ConfigureAwait(false);
            Require(folderPreview.ExitCode == 0, "The owner CLI did not preview the folder export (stderr=" +
                folderPreview.StandardError.Trim() + ").");
            var folderSummary = S12T4Summary(folderPreview.StandardOutput, "EXPORT PREVIEW ");
            Require(WorkspaceNumber(folderSummary, "files") == 5 && WorkspaceNumber(folderSummary, "folders") == 1,
                "The folder preview reported the wrong scope counts.");
            Require(WorkspaceNumber(folderSummary, "anchors") == 1 &&
                WorkspaceNumber(folderSummary, "tombstonesExcluded") == 0,
                "The folder preview reported the wrong anchor or tombstone count.");
            Require(folderPreview.StandardOutput.Contains("path=\"nested-s12t4/deep-s12t4.jsonl\"", StringComparison.Ordinal),
                "The folder preview did not state the exact nested destination path.");
            Require(!Directory.Exists(folderDestination),
                "The folder preview created the destination instead of only describing it.");

            var folderExport = await S12T4ExportAsync(executable, fixture, alphaId, null, folderDestination,
                WorkspaceToken(folderSummary, "token"), "EXPORT").ConfigureAwait(false);
            Require(folderExport.ExitCode == 0, "The owner CLI did not write the folder export (stderr=" +
                folderExport.StandardError.Trim() + ").");
            var folderExportLine = S12T4Summary(folderExport.StandardOutput, "EXPORT written ");
            Require(WorkspaceNumber(folderExportLine, "files") == 5 && WorkspaceNumber(folderExportLine, "folders") == 1,
                "The folder export reported the wrong scope counts.");
            RequireDirectoryEntries(folderDestination, new[]
            {
                S12T4Note,
                S12T4Empty,
                S12T4Utf16,
                S12T4Nested + "/" + S12T4Binary,
                S12T4Nested + "/" + S12T4Deep,
                S12T4Sidecar
            });
            RequireDirectorySet(folderDestination, new[] { S12T4Nested });
            RequireBytes(await File.ReadAllBytesAsync(Path.Combine(folderDestination, S12T4Note)).ConfigureAwait(false),
                noteRevisionBytes, "folder export of the revised note");
            RequireBytes(await File.ReadAllBytesAsync(Path.Combine(folderDestination, S12T4Empty)).ConfigureAwait(false),
                emptyBytes, "folder export of the empty original");
            RequireBytes(await File.ReadAllBytesAsync(Path.Combine(folderDestination, S12T4Utf16)).ConfigureAwait(false),
                utf16Bytes, "folder export of the UTF-16 original");
            RequireBytes(await File.ReadAllBytesAsync(Path.Combine(folderDestination, S12T4Nested, S12T4Binary)).ConfigureAwait(false),
                S12T4BinaryBytes, "folder export of the opaque binary original");
            RequireBytes(await File.ReadAllBytesAsync(Path.Combine(folderDestination, S12T4Nested, S12T4Deep)).ConfigureAwait(false),
                deepBytes, "folder export of the nested JSONL original");

            var folderSidecarPath = Path.Combine(folderDestination, S12T4Sidecar);
            var folderSidecarBytes = await File.ReadAllBytesAsync(folderSidecarPath).ConfigureAwait(false);
            using (var folderSidecar = JsonDocument.Parse(folderSidecarBytes))
            {
                var root = folderSidecar.RootElement;
                Require(string.Equals(root.GetProperty("format").GetString(), "metabrain-workspace-export/1", StringComparison.Ordinal),
                    "The folder metadata document does not carry its format version.");
                Require(root.GetProperty("selected").GetProperty("nodeId").GetString() == alphaId,
                    "The folder metadata document does not name the selected folder.");
                var nodes = root.GetProperty("nodes");
                Require(nodes.GetArrayLength() == 7,
                    "The folder metadata document does not describe exactly the selected hierarchy.");
                var expectedPaths = new HashSet<string>(StringComparer.Ordinal)
                {
                    string.Empty,
                    S12T4Nested,
                    S12T4Note,
                    S12T4Empty,
                    S12T4Utf16,
                    S12T4Nested + "/" + S12T4Binary,
                    S12T4Nested + "/" + S12T4Deep
                };
                foreach (var node in nodes.EnumerateArray())
                {
                    Require(expectedPaths.Remove(node.GetProperty("path").GetString() ?? string.Empty),
                        "The folder metadata document listed a path outside the selected scope or duplicated one.");
                    Require(!string.IsNullOrEmpty(node.GetProperty("nodeId").GetString()) &&
                        node.GetProperty("revision").GetInt64() >= 1,
                        "A metadata node lacks its stable identity or revision.");
                }

                Require(expectedPaths.Count == 0, "The folder metadata document did not preserve the whole hierarchy.");
                var rootNode = nodes[0];
                Require(rootNode.GetProperty("parentId").ValueKind == JsonValueKind.Null,
                    "The folder metadata document disclosed the selected folder's parent identity.");
                Require(nodes.EnumerateArray().Any(node =>
                        string.Equals(node.GetProperty("path").GetString(), S12T4Nested, StringComparison.Ordinal) &&
                        string.Equals(node.GetProperty("parentId").GetString(), alphaId, StringComparison.Ordinal)),
                    "The folder metadata document did not preserve a child's parent identity.");
                Require(nodes.EnumerateArray().Any(node =>
                        string.Equals(node.GetProperty("path").GetString(), S12T4Note, StringComparison.Ordinal) &&
                        node.GetProperty("contentRevision").GetInt64() == 2 &&
                        node.GetProperty("retainedRevisions").GetArrayLength() == 2),
                    "The folder metadata document did not pin the current revision and retain the older one.");
                var anchors = root.GetProperty("anchors");
                Require(anchors.GetArrayLength() == 1 &&
                    string.Equals(anchors[0].GetProperty("anchorId").GetString(), noteAnchorId, StringComparison.Ordinal),
                    "The folder metadata document did not carry exactly the in-scope anchors.");
                Require(root.GetProperty("relations").GetArrayLength() == 0,
                    "The folder metadata document invented relations.");
                S12T4RequireAbsent(folderSidecarBytes, outsideAnchorId, "an anchor outside the selected folder");
                S12T4RequireAbsent(folderSidecarBytes, S12T4OutsideFile, "an out-of-scope document name");
                S12T4RequireAbsent(folderSidecarBytes, S12T4OutsideMarker, "out-of-scope content");
                S12T4RequireAbsent(folderSidecarBytes, S12T4HostileFolder, "an out-of-scope folder name");
                S12T4RequireAbsent(folderSidecarBytes, S12T4CaseFolder, "an out-of-scope folder name");
                S12T4RequireAbsent(folderSidecarBytes, "bad\\name-s12t4", "an out-of-scope logical name");
                S12T4RequireAbsent(folderSidecarBytes, "CON", "an out-of-scope logical name");
            }

            Require(WorkspaceNumber(await ReadWorkspaceStatusAsync(executable, fixture).ConfigureAwait(false), "revision") == revisionBefore,
                "An export changed the committed workspace revision.");
            Console.WriteLine("PASS a folder export preserves exact names, hierarchy, empty/binary/UTF-16 bytes and only in-scope metadata");

            // Destination handling: existing entries, traversal, missing
            // parents, device namespaces and reparse points refuse, and nothing
            // outside the fixture's own directories is touched.
            var existing = await S12T4PreviewAsync(executable, fixture, noteId, null, folderDestination).ConfigureAwait(false);
            RequireWorkspaceRefusal(existing, WorkspaceErrorCodes.ExportDestination, "an existing destination directory");
            var caseCollision = await S12T4PreviewAsync(executable, fixture, noteId, null,
                Path.Combine(outputDirectory, "EXPORT-FOLDER-S12T4")).ConfigureAwait(false);
            RequireWorkspaceRefusal(caseCollision, WorkspaceErrorCodes.ExportDestination,
                "a destination that only differs by case from an existing entry");
            var existingFile = Path.Combine(outputDirectory, "existing-entry-s12t4");
            await File.WriteAllBytesAsync(existingFile, Encoding.UTF8.GetBytes("unrelated\n")).ConfigureAwait(false);
            var overFile = await S12T4PreviewAsync(executable, fixture, noteId, null, existingFile).ConfigureAwait(false);
            RequireWorkspaceRefusal(overFile, WorkspaceErrorCodes.ExportDestination, "an existing unrelated file at the destination");
            RequireBytes(await File.ReadAllBytesAsync(existingFile).ConfigureAwait(false),
                Encoding.UTF8.GetBytes("unrelated\n"), "existing unrelated file after a refused export");
            var relativeDestination = await S12T4PreviewAsync(executable, fixture, noteId, null, "relative-s12t4").ConfigureAwait(false);
            RequireWorkspaceRefusal(relativeDestination, WorkspaceErrorCodes.ExportDestination, "a relative destination");
            var missingParent = await S12T4PreviewAsync(executable, fixture, noteId, null,
                Path.Combine(outputDirectory, "missing-parent-s12t4", "child-s12t4")).ConfigureAwait(false);
            RequireWorkspaceRefusal(missingParent, WorkspaceErrorCodes.ExportDestination, "a destination whose parent does not exist");
            var traversal = Path.Combine(outputDirectory, "..", "escape-s12t4");
            var traversalResult = await S12T4PreviewAsync(executable, fixture, noteId, null, traversal).ConfigureAwait(false);
            RequireWorkspaceRefusal(traversalResult, WorkspaceErrorCodes.ExportDestination, "a destination with a traversal segment");
            Require(!Directory.Exists(Path.GetFullPath(traversal)) && !File.Exists(Path.GetFullPath(traversal)),
                "A traversal destination created an entry outside the selected destination.");

            var junctionTarget = Path.Combine(outputDirectory, "junction-target-s12t4");
            var junctionLink = Path.Combine(outputDirectory, "junction-link-s12t4");
            Directory.CreateDirectory(junctionTarget);
            if (TryCreateJunction(junctionLink, junctionTarget))
            {
                var throughJunction = await S12T4PreviewAsync(executable, fixture, noteId, null,
                    Path.Combine(junctionLink, "child-s12t4")).ConfigureAwait(false);
                RequireWorkspaceRefusal(throughJunction, WorkspaceErrorCodes.ExportDestination,
                    "a destination below a reparse point");
                // A junction is a reparse point: remove the link (never the
                // target) so the fixture's own recursive cleanup cannot follow it.
                Directory.Delete(junctionLink, recursive: false);
            }
            else
            {
                Console.WriteLine("LIMIT the host did not allow creating a junction, so the reparse-point destination refusal was not exercised");
            }

            var traversalSegment = await S12T4PreviewAsync(executable, fixture, noteId, null,
                Path.Combine(outputDirectory, "segment-s12t4", "..", "segment-child-s12t4")).ConfigureAwait(false);
            RequireWorkspaceRefusal(traversalSegment, WorkspaceErrorCodes.ExportDestination,
                "a destination containing an inner traversal segment");

            var rootPreview = await S12T4PreviewAsync(executable, fixture, RootId(await TreeAsync(executable, fixture).ConfigureAwait(false)),
                null, Path.Combine(outputDirectory, "export-root-s12t4")).ConfigureAwait(false);
            RequireWorkspaceRefusal(rootPreview, WorkspaceErrorCodes.RootImmutable, "an export of the workspace root");

            var deadFolder = await S12T4PreviewAsync(executable, fixture, deletedFolderId, null,
                Path.Combine(outputDirectory, "export-deleted-s12t4")).ConfigureAwait(false);
            RequireWorkspaceRefusal(deadFolder, WorkspaceErrorCodes.Deleted, "a tombstoned folder export");
            var deadFile = await S12T4PreviewAsync(executable, fixture, deletedImport.FileId, null,
                Path.Combine(outputDirectory, "export-deleted-file-s12t4")).ConfigureAwait(false);
            RequireWorkspaceRefusal(deadFile, WorkspaceErrorCodes.Deleted, "a tombstoned file export");
            Require(!Directory.Exists(Path.Combine(outputDirectory, "export-deleted-s12t4")) &&
                !Directory.Exists(Path.Combine(outputDirectory, "export-deleted-file-s12t4")),
                "A refused tombstone export created a destination.");

            var hostileName = await S12T4PreviewAsync(executable, fixture, hostileFolderId, null,
                Path.Combine(outputDirectory, "export-hostile-s12t4")).ConfigureAwait(false);
            RequireWorkspaceRefusal(hostileName, WorkspaceErrorCodes.ExportName,
                "a scope holding a logical name with a path separator");
            var deviceName = await S12T4PreviewAsync(executable, fixture, deviceImport.FileId, null,
                Path.Combine(outputDirectory, "export-device-s12t4")).ConfigureAwait(false);
            RequireWorkspaceRefusal(deviceName, WorkspaceErrorCodes.ExportName,
                "a logical name that is a reserved device name");
            var caseNames = await S12T4PreviewAsync(executable, fixture, caseFolderId, null,
                Path.Combine(outputDirectory, "export-case-s12t4")).ConfigureAwait(false);
            RequireWorkspaceRefusal(caseNames, WorkspaceErrorCodes.ExportCollision,
                "two sibling names that collide only by case on the destination");
            var missingRevision = await S12T4PreviewAsync(executable, fixture, noteId, 9,
                Path.Combine(outputDirectory, "export-missing-revision-s12t4")).ConfigureAwait(false);
            RequireWorkspaceRefusal(missingRevision, WorkspaceErrorCodes.NotFound,
                "an export of a retained revision that does not exist");
            var folderRevision = await S12T4PreviewAsync(executable, fixture, alphaId, 1,
                Path.Combine(outputDirectory, "export-folder-revision-s12t4")).ConfigureAwait(false);
            RequireWorkspaceRefusal(folderRevision, WorkspaceErrorCodes.ExportRevision,
                "a revision selector on a folder export");
            Console.WriteLine("PASS destination collisions, traversal, reparse points, tombstones, unsafe names and bad revisions refuse");

            // A preview is bound to the exact workspace revision: any change
            // between preview and export refuses instead of exporting a scope
            // the owner did not see.
            var staleDestination = Path.Combine(outputDirectory, "stale-export-s12t4");
            var stalePreview = await S12T4PreviewAsync(executable, fixture, staleFolderId, null, staleDestination).ConfigureAwait(false);
            Require(stalePreview.ExitCode == 0, "The stale-preview folder export preview failed (stderr=" +
                stalePreview.StandardError.Trim() + ").");
            var staleSummary = S12T4Summary(stalePreview.StandardOutput, "EXPORT PREVIEW ");
            var staleExtra = await S12T4ImportAsync(executable, fixture, staleFolderId, S12T4StaleExtra,
                Encoding.UTF8.GetBytes("later marker " + S12T4NoteMarker + "\n"),
                WorkspaceNumber(staleSummary, "revision"), "input-s12t4-stale-extra.md").ConfigureAwait(false);
            var staleExport = await S12T4ExportAsync(executable, fixture, staleFolderId, null, staleDestination,
                WorkspaceToken(staleSummary, "token"), "EXPORT").ConfigureAwait(false);
            RequireWorkspaceRefusal(staleExport, WorkspaceErrorCodes.Conflict, "an export after the workspace changed");
            Require(!Directory.Exists(staleDestination), "A stale-preview export created the destination.");
            var freshStalePreview = await S12T4PreviewAsync(executable, fixture, staleFolderId, null, staleDestination).ConfigureAwait(false);
            Require(freshStalePreview.ExitCode == 0, "The refreshed preview failed (stderr=" +
                freshStalePreview.StandardError.Trim() + ").");
            var freshStaleSummary = S12T4Summary(freshStalePreview.StandardOutput, "EXPORT PREVIEW ");
            var freshStaleExport = await S12T4ExportAsync(executable, fixture, staleFolderId, null, staleDestination,
                WorkspaceToken(freshStaleSummary, "token"), "EXPORT").ConfigureAwait(false);
            Require(freshStaleExport.ExitCode == 0 && WorkspaceNumber(freshStaleSummary, "files") == 2,
                "The refreshed preview did not export the changed scope (stderr=" + freshStaleExport.StandardError.Trim() + ").");
            Console.WriteLine("PASS a stale preview refuses while its destination is untouched, and a refreshed preview exports the changed scope");

            var revisionAfterChanges = WorkspaceNumber(
                await ReadWorkspaceStatusAsync(executable, fixture).ConfigureAwait(false), "revision");

            // Lock stops both the preview and the act; export never runs on a
            // locked vault.
            var lockResult = await RunOwnerCommandAsync(executable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockResult.ExitCode == 0, "The owner CLI did not lock the vault.");
            var lockedPreview = await S12T4PreviewAsync(executable, fixture, noteId, null,
                Path.Combine(outputDirectory, "export-locked-s12t4")).ConfigureAwait(false);
            Require(lockedPreview.ExitCode != 0 && lockedPreview.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "A locked vault served an export preview.");
            var lockedExport = await S12T4ExportAsync(executable, fixture, noteId, null,
                Path.Combine(outputDirectory, "export-locked-s12t4"), new string('0', 64), "EXPORT").ConfigureAwait(false);
            Require(lockedExport.ExitCode != 0 && lockedExport.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "A locked vault served a plaintext export.");
            await UnlockWorkspaceAsync(executable, fixture, passphrase).ConfigureAwait(false);
            Console.WriteLine("PASS a locked vault refuses both the export preview and the plaintext act");

            // Restart and unlock: the same scope exports byte-identical files,
            // and the vault itself is unchanged by every export act.
            // The metadata document records when it was written, so only the
            // exported original bytes must be identical across the two epochs.
            var folderHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in Directory.GetFiles(folderDestination, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(folderDestination, path).Replace('\\', '/');
                if (string.Equals(relative, S12T4Sidecar, StringComparison.Ordinal))
                {
                    continue;
                }

                folderHashes[relative] = Sha256File(path);
            }

            var vaultNoteRevision1 = await ReadWorkspaceFileAsync(executable, fixture, noteId, 1, "s12t4-vault-note-rev1.md").ConfigureAwait(false);
            var vaultNoteRevision2 = await ReadWorkspaceFileAsync(executable, fixture, noteId, 2, "s12t4-vault-note-rev2.md").ConfigureAwait(false);
            RequireBytes(vaultNoteRevision1, noteBytes, "vault note revision 1 before restart");
            RequireBytes(vaultNoteRevision2, noteRevisionBytes, "vault note revision 2 before restart");

            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(executable, fixture.ControlPipe).ConfigureAwait(false)).Contains("vault=locked", StringComparison.Ordinal),
                "The restarted service did not begin locked.");
            await UnlockWorkspaceAsync(executable, fixture, passphrase).ConfigureAwait(false);
            var restartedStatus = await ReadWorkspaceStatusAsync(executable, fixture).ConfigureAwait(false);
            Require(WorkspaceNumber(restartedStatus, "revision") == revisionAfterChanges,
                "The restarted epoch did not reopen the committed workspace revision.");

            var restartDestination = Path.Combine(outputDirectory, "export-folder-restart-s12t4");
            var restartPreview = await S12T4PreviewAsync(executable, fixture, alphaId, null, restartDestination).ConfigureAwait(false);
            Require(restartPreview.ExitCode == 0, "The post-restart export preview failed (stderr=" +
                restartPreview.StandardError.Trim() + ").");
            var restartSummary = S12T4Summary(restartPreview.StandardOutput, "EXPORT PREVIEW ");
            Require(WorkspaceNumber(restartSummary, "revision") == revisionAfterChanges,
                "The post-restart preview saw a different workspace revision than the committed one.");
            var restartExport = await S12T4ExportAsync(executable, fixture, alphaId, null, restartDestination,
                WorkspaceToken(restartSummary, "token"), "EXPORT").ConfigureAwait(false);
            Require(restartExport.ExitCode == 0, "The post-restart export failed (stderr=" +
                restartExport.StandardError.Trim() + ").");
            RequireDirectoryEntries(restartDestination, new[]
            {
                S12T4Note,
                S12T4Empty,
                S12T4Utf16,
                S12T4Nested + "/" + S12T4Binary,
                S12T4Nested + "/" + S12T4Deep,
                S12T4Sidecar
            });
            foreach (var pair in folderHashes)
            {
                var restartedPath = Path.Combine(restartDestination, pair.Key.Replace('/', Path.DirectorySeparatorChar));
                Require(File.Exists(restartedPath), "The post-restart export lost '" + pair.Key + "'.");
                Require(string.Equals(Sha256File(restartedPath), pair.Value, StringComparison.Ordinal),
                    "The post-restart export of '" + pair.Key + "' is not byte-identical to the first export.");
            }

            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, noteId, 1, "s12t4-vault-note-rev1-after.md").ConfigureAwait(false),
                vaultNoteRevision1, "vault note revision 1 after restart");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, noteId, 2, "s12t4-vault-note-rev2-after.md").ConfigureAwait(false),
                vaultNoteRevision2, "vault note revision 2 after restart");
            Require(WorkspaceNumber(await ReadWorkspaceStatusAsync(executable, fixture).ConfigureAwait(false), "revision") == revisionAfterChanges,
                "The workspace revision changed across export and restart.");
            Console.WriteLine("PASS after restart and unlock the same scope exports byte-identical originals and the vault is unchanged");

            // The external plaintext import sources are independent copies; no
            // export writes back to them.
            RequireBytes(await File.ReadAllBytesAsync(noteInputPath).ConfigureAwait(false), noteInputBytesBefore,
                "external plaintext import source after export");
            Require(File.GetLastWriteTimeUtc(noteInputPath) == noteInputWriteTimeBefore,
                "An export rewrote the external plaintext import source's timestamp.");

            await VerifyAgentChannelCannotReachWorkspaceExportAsync(fixture, noteId, folderDestination).ConfigureAwait(false);

            RequireNoPlaintextWorkspaceState(workspaceRoot,
                new[]
                {
                    S12T4Alpha,
                    S12T4Nested,
                    S12T4Note,
                    S12T4NoteMarker,
                    S12T4NoteRevisionMarker,
                    S12T4OutsideFolder,
                    S12T4OutsideFile,
                    S12T4OutsideMarker,
                    S12T4HostileFolder,
                    "bad\\name-s12t4",
                    S12T4CaseFolder
                },
                new[] { noteBytes, noteRevisionBytes, S12T4BinaryBytes, utf16Bytes, deepBytes, outsideBytes });
            foreach (var exported in new[] { fileDestination, folderDestination, restartDestination, staleDestination })
            {
                foreach (var path in Directory.GetFiles(exported, "*", SearchOption.AllDirectories))
                {
                    Require(!IsTemporaryFileName(Path.GetFileName(path)),
                        "An export left a temporary plaintext file: " + path);
                }
            }

            Console.WriteLine("PASS S12-T4 original read/export, pinned revisions, portability metadata and destination safety");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static async Task<(long TreeRevision, string FileId)> S12T4ImportAsync(
        string executable,
        SmokeFixture fixture,
        string parentId,
        string name,
        byte[] content,
        long expectedTreeRevision,
        string inputName)
    {
        var inputPath = await WriteWorkspaceInputAsync(fixture, inputName, content).ConfigureAwait(false);
        var result = await WorkspaceAsync(executable, fixture,
            "workspace-import", "--parent-id", parentId, "--name", name, "--input-file", inputPath,
            "--expected-tree-revision", expectedTreeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        Require(result.ExitCode == 0, "The owner CLI did not import '" + name + "' (stderr=" +
            result.StandardError.Trim() + ").");
        return (TreeRevision(result.StandardOutput), WorkspaceToken(result.StandardOutput, "id"));
    }

    private static Task<CommandResult> S12T4PreviewAsync(
        string executable,
        SmokeFixture fixture,
        string nodeId,
        long? contentRevision,
        string destination)
    {
        var arguments = new List<object>
        {
            "workspace-export-preview",
            "--node-id",
            nodeId,
            "--destination",
            destination
        };
        if (contentRevision.HasValue)
        {
            arguments.Add("--content-revision");
            arguments.Add(contentRevision.Value);
        }

        return WorkspaceAsync(executable, fixture, arguments.ToArray());
    }

    private static Task<CommandResult> S12T4ExportAsync(
        string executable,
        SmokeFixture fixture,
        string nodeId,
        long? contentRevision,
        string destination,
        string previewToken,
        string confirmation)
    {
        var arguments = new List<object>
        {
            "workspace-export",
            "--node-id",
            nodeId,
            "--destination",
            destination,
            "--preview",
            previewToken,
            "--confirm",
            confirmation
        };
        if (contentRevision.HasValue)
        {
            arguments.Add("--content-revision");
            arguments.Add(contentRevision.Value);
        }

        return WorkspaceAsync(executable, fixture, arguments.ToArray());
    }

    private static string S12T4Summary(string output, string prefix)
    {
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line;
            }
        }

        throw new InvalidOperationException("The command output did not carry a '" + prefix.Trim() + "' line.");
    }

    private static string S12T4FlipToken(string token)
    {
        Require(token.Length == 64, "A preview token was not one SHA-256 hex string.");
        var last = token[^1] == 'a' ? 'b' : 'a';
        return token[..^1] + last;
    }

    private static void S12T4RequireAbsent(byte[] haystack, string token, string label) =>
        Require(haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(token)) < 0,
            "The exported metadata document disclosed " + label + " ('" + token + "').");

    private static void RequireDirectoryEntries(string directory, IReadOnlyList<string> expectedRelativePaths)
    {
        Require(Directory.Exists(directory), "The export destination was not created: " + directory);
        var actual = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var expected = expectedRelativePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Require(actual.SequenceEqual(expected, StringComparer.Ordinal),
            "The export destination does not hold exactly the previewed entries (actual=" +
            string.Join(",", actual) + "; expected=" + string.Join(",", expected) + ").");
    }

    private static void RequireDirectorySet(string directory, IReadOnlyList<string> expectedRelativePaths)
    {
        var actual = Directory.GetDirectories(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var expected = expectedRelativePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Require(actual.SequenceEqual(expected, StringComparer.Ordinal),
            "The export destination does not hold exactly the previewed folders (actual=" +
            string.Join(",", actual) + "; expected=" + string.Join(",", expected) + ").");
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string Sha256Hex(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static byte[] Concatenate(byte[] first, byte[] second)
    {
        var combined = new byte[checked(first.Length + second.Length)];
        first.CopyTo(combined, 0);
        second.CopyTo(combined, first.Length);
        return combined;
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(linkPath);
            start.ArgumentList.Add(targetPath);
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return false;
            }

            return process.ExitCode == 0 && Directory.Exists(linkPath) &&
                (new DirectoryInfo(linkPath).Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts the owner-only export over the agent channel. The agent channel
    /// has no owner authority, so the service must refuse both the preview and
    /// the act before any private scope is read or written.
    /// </summary>
    private static async Task VerifyAgentChannelCannotReachWorkspaceExportAsync(
        SmokeFixture fixture,
        string nodeId,
        string destination)
    {
        foreach (var operation in new[]
                 {
                     ServiceRequestHandler.OwnerWorkspaceExportPreviewOperation,
                     ServiceRequestHandler.OwnerWorkspaceExportOperation
                 })
        {
            var requestJson = JsonSerializer.Serialize(new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation,
                resourceId = nodeId,
                workspaceExportDestination = destination,
                workspaceExportToken = new string('0', 64),
                workspaceExportConfirmation = "EXPORT"
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var result = await PipeClient.InvokeAgentAsync(fixture.AgentPipe, requestJson, CancellationToken.None).ConfigureAwait(false);
            Require(result.Success && result.Json.Contains("forbidden", StringComparison.Ordinal),
                "The agent channel did not refuse the owner workspace export '" + operation + "' (reply=" + result.Json + ").");
        }

        Console.WriteLine("PASS the agent/MCP channel cannot reach the owner workspace export");
    }
}
