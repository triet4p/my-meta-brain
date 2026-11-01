using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Security;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S12-T2 runtime proof for the encrypted workspace: the actual owner CLI and
/// the actual shared service persist logical folders, retained original-byte
/// file revisions and the exact source bytes of markdown/CSV/JSONL/UTF-16
/// text/opaque binary/empty files, keep every name and byte out of plaintext on
/// disk, and refuse wrong keys, tampering, truncation, stale preconditions,
/// unrepresentable names and unsupported formats without losing saved data.
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S12T2FolderName = "docs-s12t2-7f3a";
    private const string S12T2MarkdownName = "notes-s12t2-md";
    private const string S12T2CsvName = "table-s12t2-csv";
    private const string S12T2JsonlName = "events-s12t2-jsonl";
    private const string S12T2TextName = "text-s12t2-utf16";
    private const string S12T2BinaryName = "opaque-s12t2-binary";
    private const string S12T2EmptyName = "empty-s12t2-zero";
    private const string S12T2MarkdownMarker = "synthetic-markdown-marker-4f19d2";
    private const string S12T2JsonlMarker = "synthetic-jsonl-marker-8a31c7";
    private const string S12T2TextMarker = "synthetic-utf16-marker-2d6e40";
    private static readonly byte[] S12T2BinaryMarker = { 0x00, 0xFF, 0xFE, 0x80, 0x81, 0xC0, 0xAF, 0xED, 0xA0, 0x80 };

    public static async Task RunS12T2WorkspaceAsync(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S12-T2 workspace smoke is Windows-only.");
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
                "The owner CLI did not provision the S12-T2 workspace vault (stderr=" + provision.StandardError.Trim() + ").");

            var initial = await ReadWorkspaceStatusAsync(executable, fixture).ConfigureAwait(false);
            Require(initial.Contains("state=uninitialized", StringComparison.Ordinal) &&
                    initial.Contains("revision=0", StringComparison.Ordinal),
                "A fresh unlocked vault did not report an uninitialized workspace.");
            Require(!Directory.Exists(workspaceRoot),
                "An uninitialized workspace wrote private state before the first act.");
            Console.WriteLine("PASS workspace starts uninitialized and writes nothing before the first act");

            var markdown = Encoding.UTF8.GetBytes(
                "# Synthetic workspace note\n\nmarker " + S12T2MarkdownMarker + "\n");
            var csv = Encoding.UTF8.GetBytes("id,label\r\n1,\"quoted, value\"\r\n2,plain\r\n");
            var jsonl = Encoding.UTF8.GetBytes(
                "{\"index\":1,\"marker\":\"" + S12T2JsonlMarker + "\"}\n{\"index\":2}\n");
            var utf16 = new byte[] { 0xFF, 0xFE }
                .Concat(Encoding.Unicode.GetBytes("synthetic UTF-16LE text: " + S12T2TextMarker + "\r\n"))
                .ToArray();
            var binary = new byte[] { 0x00, 0xFF, 0xFE, 0x01, 0x80, 0x81, 0xC0, 0xAF, 0x0A, 0x00, 0x7F }
                .Concat(S12T2BinaryMarker)
                .ToArray();
            var empty = Array.Empty<byte>();

            var markdownPath = await WriteWorkspaceInputAsync(fixture, "input-markdown.md", markdown).ConfigureAwait(false);
            var csvPath = await WriteWorkspaceInputAsync(fixture, "input-table.csv", csv).ConfigureAwait(false);
            var jsonlPath = await WriteWorkspaceInputAsync(fixture, "input-events.jsonl", jsonl).ConfigureAwait(false);
            var textPath = await WriteWorkspaceInputAsync(fixture, "input-text-utf16.txt", utf16).ConfigureAwait(false);
            var binaryPath = await WriteWorkspaceInputAsync(fixture, "input-opaque.bin", binary).ConfigureAwait(false);
            var emptyPath = await WriteWorkspaceInputAsync(fixture, "input-empty.dat", empty).ConfigureAwait(false);

            var mkdir = await RunOwnerCommandAsync(executable,
                "owner", "workspace-mkdir", "--control-pipe", fixture.ControlPipe,
                "--parent-id", "root", "--name", S12T2FolderName,
                "--expected-tree-revision", "0").ConfigureAwait(false);
            Require(mkdir.ExitCode == 0 && mkdir.StandardOutput.Contains("MKDIR id=", StringComparison.Ordinal),
                "The owner CLI did not create the first logical folder (stderr=" + mkdir.StandardError.Trim() + ").");
            var folderId = WorkspaceToken(mkdir.StandardOutput, "id");
            var treeRevision = WorkspaceNumber(mkdir.StandardOutput, "revision");
            Require(treeRevision == 2, "The first folder did not commit on the initialized empty tree (revision=" +
                treeRevision.ToString(CultureInfo.InvariantCulture) + ").");
            Console.WriteLine("PASS first logical folder committed under the root selector");

            async Task<(string FileId, string Sha256)> ImportAsync(string importName, string importPath)
            {
                var imported = await ImportWorkspaceFileAsync(
                    executable, fixture, folderId, importName, importPath, treeRevision).ConfigureAwait(false);
                treeRevision = imported.TreeRevision;
                return (imported.FileId, imported.Sha256);
            }

            var (markdownId, markdownSha) = await ImportAsync(S12T2MarkdownName, markdownPath).ConfigureAwait(false);
            var (csvId, _) = await ImportAsync(S12T2CsvName, csvPath).ConfigureAwait(false);
            var (jsonlId, _) = await ImportAsync(S12T2JsonlName, jsonlPath).ConfigureAwait(false);
            var (textId, _) = await ImportAsync(S12T2TextName, textPath).ConfigureAwait(false);
            var (binaryId, _) = await ImportAsync(S12T2BinaryName, binaryPath).ConfigureAwait(false);
            var (emptyId, emptySha) = await ImportAsync(S12T2EmptyName, emptyPath).ConfigureAwait(false);
            Require(string.Equals(markdownSha, Convert.ToHexStringLower(SHA256.HashData(markdown)), StringComparison.Ordinal),
                "The import digest did not identify the exact source bytes.");
            Require(string.Equals(emptySha, Convert.ToHexStringLower(SHA256.HashData(empty)), StringComparison.Ordinal),
                "The zero-byte import did not report the empty digest.");
            Console.WriteLine("PASS markdown/CSV/JSONL/UTF-16 text/opaque binary/zero-byte files imported as original bytes");

            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, null, "read-markdown-latest.md").ConfigureAwait(false),
                markdown, "markdown");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, csvId, null, "read-table-latest.csv").ConfigureAwait(false),
                csv, "CSV");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, jsonlId, null, "read-events-latest.jsonl").ConfigureAwait(false),
                jsonl, "JSONL");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, textId, null, "read-text-latest.txt").ConfigureAwait(false),
                utf16, "UTF-16 text");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, binaryId, null, "read-opaque-latest.bin").ConfigureAwait(false),
                binary, "opaque binary");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, emptyId, null, "read-empty-latest.dat").ConfigureAwait(false),
                empty, "zero-byte file");
            Console.WriteLine("PASS every format reads back byte-for-byte through the create-only owner read");

            var revisedMarkdown = Encoding.UTF8.GetBytes(
                "# Synthetic workspace note (revision 2)\n\nmarker " + S12T2MarkdownMarker + "-rev2\n");
            var revisedPath = await WriteWorkspaceInputAsync(fixture, "input-markdown-rev2.md", revisedMarkdown).ConfigureAwait(false);
            var revise = await RunOwnerCommandAsync(executable,
                "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                "--file-id", markdownId, "--expected-content-revision", "1",
                "--input-file", revisedPath).ConfigureAwait(false);
            Require(revise.ExitCode == 0 && revise.StandardOutput.Contains("REVISE id=", StringComparison.Ordinal),
                "The owner CLI did not append a retained content revision (stderr=" + revise.StandardError.Trim() + ").");
            Require(WorkspaceNumber(revise.StandardOutput, "contentRevision") == 2,
                "The content revision did not advance to 2.");
            treeRevision = WorkspaceNumber(revise.StandardOutput, "revision");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, 1, "read-markdown-rev1.md").ConfigureAwait(false),
                markdown, "retained markdown revision 1");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, 2, "read-markdown-rev2.md").ConfigureAwait(false),
                revisedMarkdown, "markdown revision 2");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, null, "read-markdown-rev2-latest.md").ConfigureAwait(false),
                revisedMarkdown, "latest markdown revision");
            Console.WriteLine("PASS earlier content revisions stay readable after a revision-gated replace");

            var staleTree = await RunOwnerCommandAsync(executable,
                "owner", "workspace-mkdir", "--control-pipe", fixture.ControlPipe,
                "--parent-id", folderId, "--name", "stale-s12t2",
                "--expected-tree-revision", "2").ConfigureAwait(false);
            RequireWorkspaceRefusal(staleTree, WorkspaceErrorCodes.Conflict, "a stale tree revision");
            var staleContent = await RunOwnerCommandAsync(executable,
                "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
                "--file-id", markdownId, "--expected-content-revision", "1",
                "--input-file", revisedPath).ConfigureAwait(false);
            RequireWorkspaceRefusal(staleContent, WorkspaceErrorCodes.Conflict, "a stale content revision");
            var duplicateName = await RunOwnerCommandAsync(executable,
                "owner", "workspace-import", "--control-pipe", fixture.ControlPipe,
                "--parent-id", folderId, "--name", S12T2MarkdownName,
                "--input-file", emptyPath,
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            RequireWorkspaceRefusal(duplicateName, WorkspaceErrorCodes.NameConflict, "an exact duplicate sibling name");
            var invalidParent = await RunOwnerCommandAsync(executable,
                "owner", "workspace-mkdir", "--control-pipe", fixture.ControlPipe,
                "--parent-id", markdownId, "--name", "under-a-file",
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            RequireWorkspaceRefusal(invalidParent, WorkspaceErrorCodes.InvalidParent, "a file used as a parent folder");
            var longName = new string('n', 300);
            var invalidName = await RunOwnerCommandAsync(executable,
                "owner", "workspace-mkdir", "--control-pipe", fixture.ControlPipe,
                "--parent-id", folderId, "--name", longName,
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            RequireWorkspaceRefusal(invalidName, WorkspaceErrorCodes.InvalidName, "a name past the logical-name bound");

            // Ordinal, case-sensitive sibling comparison: a case-different name
            // is a distinct name, not a collision.
            var (caseVariantId, _) = await ImportAsync(S12T2MarkdownName.ToUpperInvariant(), emptyPath).ConfigureAwait(false);
            Require(caseVariantId.Length == 32, "The case-variant sibling did not receive its own node id.");

            var tooLargePath = Path.Combine(fixture.OutputDirectory, "input-too-large.bin");
            await File.WriteAllBytesAsync(tooLargePath, new byte[(1024 * 1024) + 1]).ConfigureAwait(false);
            var tooLarge = await RunOwnerCommandAsync(executable,
                "owner", "workspace-import", "--control-pipe", fixture.ControlPipe,
                "--parent-id", folderId, "--name", "too-large-s12t2",
                "--input-file", tooLargePath,
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(tooLarge.ExitCode != 0 && tooLarge.StandardError.Contains(WorkspaceErrorCodes.TooLarge, StringComparison.Ordinal),
                "A file past the bounded envelope was not refused with workspace_too_large.");
            Console.WriteLine("PASS stale tree/content preconditions, duplicate and ill-formed names, non-folder parents, size bound refused");

            var tree = await RunOwnerCommandAsync(executable, "owner", "workspace-tree", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(tree.ExitCode == 0 && tree.StandardOutput.Contains("FOLDER id=", StringComparison.Ordinal) &&
                    tree.StandardOutput.Contains("FILE id=", StringComparison.Ordinal),
                "The owner tree did not report the committed folders and files.");
            Require(tree.StandardOutput.Contains("name=" + JsonSerializer.Serialize(S12T2FolderName), StringComparison.Ordinal) &&
                    tree.StandardOutput.Contains("name=" + JsonSerializer.Serialize(S12T2MarkdownName), StringComparison.Ordinal),
                "The owner tree did not report the exact logical names.");
            Require(tree.StandardOutput.Contains("contentRevision=2", StringComparison.Ordinal),
                "The owner tree did not report the retained content revision.");

            RequireNoPlaintextWorkspaceState(workspaceRoot,
                new[]
                {
                    S12T2FolderName,
                    S12T2MarkdownName,
                    S12T2CsvName,
                    S12T2JsonlName,
                    S12T2TextName,
                    S12T2BinaryName,
                    S12T2EmptyName,
                    S12T2MarkdownMarker,
                    S12T2JsonlMarker,
                    S12T2TextMarker
                },
                new[] { S12T2BinaryMarker, markdown, revisedMarkdown, csv, jsonl });
            Console.WriteLine("PASS names, paths, metadata, scratch and original bytes are absent from every plaintext file on disk");

            var committedRevision = treeRevision;
            var inventoryBeforeRestart = WorkspaceInventory(workspaceRoot);

            var lockResult = await RunOwnerCommandAsync(executable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockResult.ExitCode == 0, "The owner CLI did not lock the vault.");
            var lockedWorkspace = await RunOwnerCommandAsync(executable, "owner", "workspace-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(lockedWorkspace.ExitCode != 0 && lockedWorkspace.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "A locked vault served workspace state.");
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;

            service = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(executable, fixture.ControlPipe).ConfigureAwait(false)).Contains("vault=locked", StringComparison.Ordinal),
                "The restarted service did not begin locked.");
            var wrong = await RunOwnerCommandWithInputAsync(executable,
                "wrong synthetic workspace passphrase" + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(wrong.ExitCode != 0 && wrong.StandardError.Contains("unlock_denied", StringComparison.Ordinal),
                "A wrong key was not denied at unlock.");
            Require(string.Equals(inventoryBeforeRestart, WorkspaceInventory(workspaceRoot), StringComparison.Ordinal),
                "A wrong key attempt changed the committed workspace files.");
            await UnlockWorkspaceAsync(executable, fixture, passphrase).ConfigureAwait(false);
            var afterRestart = await ReadWorkspaceStatusAsync(executable, fixture).ConfigureAwait(false);
            Require(afterRestart.Contains("state=ready", StringComparison.Ordinal) &&
                    afterRestart.Contains("revision=" + committedRevision.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
                "The unlocked epoch did not reopen the same committed workspace revision.");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, 1, "restart-markdown-rev1.md").ConfigureAwait(false),
                markdown, "markdown revision 1 after restart");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, null, "restart-markdown-latest.md").ConfigureAwait(false),
                revisedMarkdown, "latest markdown after restart");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, binaryId, null, "restart-opaque.bin").ConfigureAwait(false),
                binary, "opaque binary after restart");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, emptyId, null, "restart-empty.dat").ConfigureAwait(false),
                empty, "zero-byte file after restart");
            Console.WriteLine("PASS lock, restart, denied wrong key and unlock preserve exact bytes, names, revisions and encrypted metadata");

            var committedStatePath = CommittedStatePath(workspaceRoot);
            var committedHead = await File.ReadAllBytesAsync(Path.Combine(workspaceRoot, "head.json")).ConfigureAwait(false);
            var committedState = await File.ReadAllBytesAsync(committedStatePath).ConfigureAwait(false);
            var orphanStateName = Guid.NewGuid().ToString("N") + ".mbs";
            var orphanBlobName = Guid.NewGuid().ToString("N") + ".mbw";
            var liveBlob = Directory.GetFiles(Path.Combine(workspaceRoot, "blobs"), "*", SearchOption.TopDirectoryOnly).First();
            File.Copy(committedStatePath, Path.Combine(workspaceRoot, "state", orphanStateName));
            File.Copy(liveBlob, Path.Combine(workspaceRoot, "blobs", orphanBlobName));
            await File.WriteAllBytesAsync(Path.Combine(workspaceRoot, "state", ".dead-s12t2.tmp"), new byte[] { 1, 2, 3 }).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(workspaceRoot, "blobs", ".dead-s12t2.tmp"), new byte[] { 4, 5, 6 }).ConfigureAwait(false);
            await RelockAndUnlockAsync(executable, fixture, passphrase).ConfigureAwait(false);
            Require(File.Exists(committedStatePath), "Crash recovery deleted the committed state generation.");
            Require(!File.Exists(Path.Combine(workspaceRoot, "state", orphanStateName)),
                "Crash recovery kept an unreferenced state generation.");
            Require(!File.Exists(Path.Combine(workspaceRoot, "blobs", orphanBlobName)),
                "Crash recovery kept unreferenced content ciphertext.");
            RequireNoWorkspaceTemporaryFiles(workspaceRoot);
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, null, "recovered-markdown.md").ConfigureAwait(false),
                revisedMarkdown, "markdown after interrupted-commit recovery");
            Console.WriteLine("PASS interrupted-commit residue (unreferenced generation/blob and atomic-write temps) is reclaimed without touching committed data");

            TamperCommittedStateCiphertext(committedStatePath);
            var inventoryAfterTamper = WorkspaceInventory(workspaceRoot);
            await RelockAndUnlockAsync(executable, fixture, passphrase).ConfigureAwait(false);
            var tamperedStatus = await RunOwnerCommandAsync(executable, "owner", "workspace-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            RequireWorkspaceRefusal(tamperedStatus, WorkspaceErrorCodes.Unavailable, "a tampered committed state");
            var tamperedTree = await RunOwnerCommandAsync(executable, "owner", "workspace-tree", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            RequireWorkspaceRefusal(tamperedTree, WorkspaceErrorCodes.Unavailable, "a tree read over tampered state");
            var tamperedRead = await RunOwnerCommandAsync(executable,
                "owner", "workspace-read", "--control-pipe", fixture.ControlPipe,
                "--file-id", markdownId, "--output-file", Path.Combine(fixture.OutputDirectory, "tamper-read.md")).ConfigureAwait(false);
            RequireWorkspaceRefusal(tamperedRead, WorkspaceErrorCodes.Unavailable, "a byte read over tampered state");
            Require(string.Equals(inventoryAfterTamper, WorkspaceInventory(workspaceRoot), StringComparison.Ordinal),
                "A failed tamper open rewrote or pruned the saved workspace.");
            await File.WriteAllBytesAsync(committedStatePath, committedState).ConfigureAwait(false);
            await RelockAndUnlockAsync(executable, fixture, passphrase).ConfigureAwait(false);
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, null, "restored-markdown.md").ConfigureAwait(false),
                revisedMarkdown, "markdown after restoring the tampered state");
            Console.WriteLine("PASS tampered ciphertext fails closed, prunes nothing, and the retained data opens again once restored");

            var truncatedState = committedState[..(committedState.Length / 2)];
            await File.WriteAllBytesAsync(committedStatePath, truncatedState).ConfigureAwait(false);
            var inventoryAfterTruncation = WorkspaceInventory(workspaceRoot);
            await RelockAndUnlockAsync(executable, fixture, passphrase).ConfigureAwait(false);
            var truncatedStatus = await RunOwnerCommandAsync(executable, "owner", "workspace-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            RequireWorkspaceRefusal(truncatedStatus, WorkspaceErrorCodes.Unavailable, "a truncated committed state");
            Require(string.Equals(inventoryAfterTruncation, WorkspaceInventory(workspaceRoot), StringComparison.Ordinal),
                "A truncated state open rewrote or pruned the saved workspace.");
            await File.WriteAllBytesAsync(committedStatePath, committedState).ConfigureAwait(false);
            await RelockAndUnlockAsync(executable, fixture, passphrase).ConfigureAwait(false);
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, binaryId, null, "truncation-restored.bin").ConfigureAwait(false),
                binary, "opaque binary after restoring the truncated state");
            Console.WriteLine("PASS truncated ciphertext fails closed and the retained data opens again once restored");

            var headPath = Path.Combine(workspaceRoot, "head.json");
            var newerHead = JsonSerializer.Serialize(new { formatVersion = 2, generation = HeadGeneration(committedHead) });
            await File.WriteAllBytesAsync(headPath, new UTF8Encoding(false).GetBytes(newerHead)).ConfigureAwait(false);
            await RelockAndUnlockAsync(executable, fixture, passphrase).ConfigureAwait(false);
            var newerStatus = await RunOwnerCommandAsync(executable, "owner", "workspace-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            RequireWorkspaceRefusal(newerStatus, WorkspaceErrorCodes.UnsupportedVersion, "a newer workspace format pointer");
            await File.WriteAllBytesAsync(headPath, committedHead).ConfigureAwait(false);
            await RelockAndUnlockAsync(executable, fixture, passphrase).ConfigureAwait(false);
            var migrated = await ReadWorkspaceStatusAsync(executable, fixture).ConfigureAwait(false);
            Require(migrated.Contains("revision=" + committedRevision.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
                "The pointer version refusal did not leave the committed revision unchanged.");
            RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, markdownId, 1, "post-version-markdown-rev1.md").ConfigureAwait(false),
                markdown, "markdown revision 1 after the version refusal");
            Console.WriteLine("PASS a newer workspace format is refused before mutation, with no auto-migration");

            service = await VerifyInterruptedCommitAsync(
                executable,
                fixture,
                workspaceRoot,
                service ?? throw new InvalidOperationException("The interrupted-commit check needs a running service."),
                passphrase,
                markdownId,
                treeRevision,
                markdown,
                revisedMarkdown).ConfigureAwait(false);

            await VerifyAgentChannelCannotReachWorkspaceAsync(fixture, markdownId).ConfigureAwait(false);

            // The last step proves the evidence itself: the vault directory
            // still holds only ciphertext and the committed workspace opens.
            RequireNoPlaintextWorkspaceState(workspaceRoot,
                new[] { S12T2FolderName, S12T2MarkdownName, S12T2MarkdownMarker, S12T2JsonlMarker, S12T2TextMarker },
                new[] { S12T2BinaryMarker, markdown, revisedMarkdown });
            Console.WriteLine("PASS S12-T2 encrypted workspace persistence, conflicts, fail-closed recovery and agent denial");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static async Task<(long TreeRevision, string FileId, string Sha256)> ImportWorkspaceFileAsync(
        string executable,
        SmokeFixture fixture,
        string parentId,
        string name,
        string inputPath,
        long expectedTreeRevision)
    {
        var result = await RunOwnerCommandAsync(executable,
            "owner", "workspace-import", "--control-pipe", fixture.ControlPipe,
            "--parent-id", parentId, "--name", name, "--input-file", inputPath,
            "--expected-tree-revision", expectedTreeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("IMPORT id=", StringComparison.Ordinal),
            "The owner CLI did not import '" + name + "' (stderr=" + result.StandardError.Trim() + ").");
        return (WorkspaceNumber(result.StandardOutput, "revision"),
            WorkspaceToken(result.StandardOutput, "id"),
            WorkspaceToken(result.StandardOutput, "sha256"));
    }

    private static async Task<byte[]> ReadWorkspaceFileAsync(
        string executable,
        SmokeFixture fixture,
        string fileId,
        long? revision,
        string outputName)
    {
        var outputPath = Path.Combine(fixture.OutputDirectory, outputName);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        var arguments = new List<string>
        {
            "owner", "workspace-read", "--control-pipe", fixture.ControlPipe,
            "--file-id", fileId, "--output-file", outputPath
        };
        if (revision.HasValue)
        {
            arguments.Add("--revision");
            arguments.Add(revision.Value.ToString(CultureInfo.InvariantCulture));
        }

        var result = await RunOwnerCommandAsync(executable, arguments.ToArray()).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("READ content bytes=", StringComparison.Ordinal),
            "The owner read of workspace file " + fileId + " failed (stderr=" + result.StandardError.Trim() + ").");
        return await File.ReadAllBytesAsync(outputPath).ConfigureAwait(false);
    }

    private static async Task<string> ReadWorkspaceStatusAsync(string executable, SmokeFixture fixture)
    {
        var result = await RunOwnerCommandAsync(executable, "owner", "workspace-status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(result.ExitCode == 0, "The owner workspace-status failed (stderr=" + result.StandardError.Trim() + ").");
        return result.StandardOutput;
    }

    private static async Task UnlockWorkspaceAsync(string executable, SmokeFixture fixture, string passphrase)
    {
        var result = await RunOwnerCommandWithInputAsync(executable,
            passphrase + Environment.NewLine,
            "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
            "The correct passphrase did not unlock the workspace vault (stderr=" + result.StandardError.Trim() + ").");
    }

    /// <summary>
    /// Locks and unlocks the same running service so the next workspace read
    /// reopens the committed state from disk. It is the exact lock/unlock
    /// lifecycle a restart uses, without paying a process restart per case.
    /// </summary>
    private static async Task RelockAndUnlockAsync(string executable, SmokeFixture fixture, string passphrase)
    {
        var lockResult = await RunOwnerCommandAsync(executable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
        Require(lockResult.ExitCode == 0, "The owner vault did not lock between workspace checks.");
        await UnlockWorkspaceAsync(executable, fixture, passphrase).ConfigureAwait(false);
    }

    private static async Task<string> WriteWorkspaceInputAsync(SmokeFixture fixture, string fileName, byte[] content)
    {
        var path = Path.Combine(fixture.OutputDirectory, fileName);
        await File.WriteAllBytesAsync(path, content).ConfigureAwait(false);
        return path;
    }

    private static async Task VerifyAgentChannelCannotReachWorkspaceAsync(SmokeFixture fixture, string fileId)
    {
        var requestJson = JsonSerializer.Serialize(new
        {
            protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
            operation = ServiceRequestHandler.OwnerWorkspaceFileReadOperation,
            resourceId = fileId
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var result = await PipeClient.InvokeAgentAsync(fixture.AgentPipe, requestJson, CancellationToken.None).ConfigureAwait(false);
        Require(result.Success && result.Json.Contains("forbidden", StringComparison.Ordinal),
            "The agent channel did not refuse an owner workspace operation (reply=" + result.Json + ").");
        Console.WriteLine("PASS the agent/MCP channel cannot reach owner workspace persistence");
    }

    private static void RequireNoPlaintextWorkspaceState(
        string workspaceRoot,
        IReadOnlyList<string> textMarkers,
        IReadOnlyList<byte[]> byteMarkers)
    {
        Require(Directory.Exists(workspaceRoot), "The committed workspace directory does not exist.");
        var files = Directory.GetFiles(workspaceRoot, "*", SearchOption.AllDirectories);
        Require(files.Length >= 3, "The committed workspace does not hold the expected ciphertext files.");
        var encodings = new[] { new UTF8Encoding(false), Encoding.Unicode };
        foreach (var path in files)
        {
            Require(!IsTemporaryFileName(Path.GetFileName(path)),
                "The committed workspace left an atomic-write temporary file: " + path);
            var bytes = File.ReadAllBytes(path);
            try
            {
                foreach (var marker in textMarkers)
                {
                    foreach (var encoding in encodings)
                    {
                        Require(bytes.AsSpan().IndexOf(encoding.GetBytes(marker)) < 0,
                            "Plaintext '" + marker + "' appeared in " + path + ".");
                    }
                }

                foreach (var marker in byteMarkers)
                {
                    Require(bytes.AsSpan().IndexOf(marker) < 0,
                        "Original bytes appeared in plaintext in " + path + ".");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private static void RequireNoWorkspaceTemporaryFiles(string workspaceRoot)
    {
        foreach (var path in Directory.GetFiles(workspaceRoot, "*", SearchOption.AllDirectories))
        {
            Require(!IsTemporaryFileName(Path.GetFileName(path)),
                "An atomic-write temporary file survived recovery: " + path);
        }
    }

    private static bool IsTemporaryFileName(string name) =>
        name.Length > 4 && name[0] == '.' && name.EndsWith(".tmp", StringComparison.Ordinal);

    private static string WorkspaceInventory(string workspaceRoot)
    {
        var files = Directory.GetFiles(workspaceRoot, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        var text = new StringBuilder();
        foreach (var path in files)
        {
            text.Append(Path.GetRelativePath(workspaceRoot, path)).Append(':')
                .Append(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).Append('\n');
        }

        return text.ToString();
    }

    private static string CommittedStatePath(string workspaceRoot)
    {
        var headPath = Path.Combine(workspaceRoot, "head.json");
        Require(File.Exists(headPath), "The committed workspace pointer is missing.");
        using var document = JsonDocument.Parse(File.ReadAllText(headPath));
        var generation = document.RootElement.GetProperty("generation").GetString()
            ?? throw new InvalidOperationException("The workspace pointer carries no generation.");
        return Path.Combine(workspaceRoot, "state", generation + ".mbs");
    }

    private static string HeadGeneration(byte[] head)
    {
        using var document = JsonDocument.Parse(head);
        return document.RootElement.GetProperty("generation").GetString()
            ?? throw new InvalidOperationException("The workspace pointer carries no generation.");
    }

    /// <summary>
    /// Flips one character inside the authenticated ciphertext while leaving
    /// the envelope valid JSON, so the refusal is the AEAD check, not a parse
    /// error. The envelope is re-serialized rather than text-replaced because
    /// the JSON writer escapes some base64 characters.
    /// </summary>
    private static void TamperCommittedStateCiphertext(string statePath)
    {
        var text = File.ReadAllText(statePath);
        var envelope = JsonNode.Parse(text)?.AsObject()
            ?? throw new InvalidOperationException("The workspace state is not a JSON envelope.");
        var ciphertext = envelope["ciphertext"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The workspace state carries no ciphertext.");
        Require(ciphertext.Length > 16, "The workspace state ciphertext is too small to tamper.");
        var index = ciphertext.Length / 2;
        var replacement = ciphertext[index] == 'A' ? 'B' : 'A';
        var tampered = ciphertext[..index] + replacement + ciphertext[(index + 1)..];
        Require(!string.Equals(tampered, ciphertext, StringComparison.Ordinal), "The synthetic tamper changed nothing.");
        envelope["ciphertext"] = tampered;
        File.WriteAllText(statePath, envelope.ToJsonString(), new UTF8Encoding(false));
    }

    private static void RequireBytes(byte[] actual, byte[] expected, string label) =>
        Require(actual.AsSpan().SequenceEqual(expected),
            "The " + label + " bytes did not round-trip exactly (expected " +
            expected.Length.ToString(CultureInfo.InvariantCulture) + " bytes, read " +
            actual.Length.ToString(CultureInfo.InvariantCulture) + ").");

    private static void RequireWorkspaceRefusal(CommandResult result, string code, string label) =>
        Require(result.ExitCode != 0 && result.StandardError.Contains(code, StringComparison.Ordinal),
            "The act over " + label + " was not refused with " + code +
            " (exit=" + result.ExitCode.ToString(CultureInfo.InvariantCulture) +
            "; stderr=" + result.StandardError.Trim() + ").");

    private static string WorkspaceToken(string output, string key)
    {
        foreach (var token in output.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith(key + "=", StringComparison.Ordinal))
            {
                return token[(key.Length + 1)..];
            }
        }

        throw new InvalidOperationException("The command output did not carry " + key + ".");
    }

    private static long WorkspaceNumber(string output, string key) =>
        long.Parse(WorkspaceToken(output, key), CultureInfo.InvariantCulture);

    /// <summary>
    /// Attempts a real interruption of one revise commit: it watches the
    /// workspace directories for the staging temporary file the atomic commit
    /// creates, and kills the service at that moment. If the commit finished
    /// before the watcher observed staging, the observed outcome is reported
    /// and the workspace must still be consistent (the commit is atomic either
    /// way). The deterministic staged-residue case above already covers the
    /// unreachable-generation reclamation.
    /// </summary>
    private static async Task<RunningService> VerifyInterruptedCommitAsync(
        string executable,
        SmokeFixture fixture,
        string workspaceRoot,
        RunningService service,
        string passphrase,
        string fileId,
        long expectedTreeRevision,
        byte[] originalMarkdown,
        byte[] revisedMarkdown)
    {
        var payloadPath = await WriteWorkspaceInputAsync(
            fixture, "input-interrupted-revise.md",
            Encoding.UTF8.GetBytes("# interrupted commit payload " + new string('x', (1024 * 1024) - 64) + "\n")).ConfigureAwait(false);
        var command = RunOwnerCommandAsync(executable,
            "owner", "workspace-revise", "--control-pipe", fixture.ControlPipe,
            "--file-id", fileId, "--expected-content-revision", "2",
            "--input-file", payloadPath);
        var interrupted = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline && !command.IsCompleted)
        {
            var staging = Directory.EnumerateFiles(workspaceRoot, "*", SearchOption.AllDirectories)
                .FirstOrDefault(path => IsTemporaryFileName(Path.GetFileName(path)));
            if (staging is not null)
            {
                try
                {
                    service.Process.Kill(entireProcessTree: true);
                    interrupted = true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }

                break;
            }

            await Task.Yield();
        }

        var result = await command.ConfigureAwait(false);
        if (interrupted)
        {
            await service.Process.WaitForExitAsync().ConfigureAwait(false);
            service = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            await UnlockWorkspaceAsync(executable, fixture, passphrase).ConfigureAwait(false);
            Console.WriteLine("NOTE the service was killed inside the commit staging window; the committed workspace reopened below");
        }
        else
        {
            Console.WriteLine("NOTE the interrupted-commit watcher did not observe a staging window; command exit=" +
                result.ExitCode.ToString(CultureInfo.InvariantCulture) + " (workspace consistency is still asserted)");
        }

        RequireNoWorkspaceTemporaryFiles(workspaceRoot);
        var status = await ReadWorkspaceStatusAsync(executable, fixture).ConfigureAwait(false);
        Require(status.Contains("state=ready", StringComparison.Ordinal),
            "The workspace did not reopen after an interrupted commit.");
        var treeRevision = WorkspaceNumber(status, "revision");
        Require(treeRevision == expectedTreeRevision || treeRevision == expectedTreeRevision + 1,
            "An interrupted commit exposed a state that is neither the previous nor the fully committed revision (revision=" +
            treeRevision.ToString(CultureInfo.InvariantCulture) + ").");
        RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, fileId, 1, "interrupted-rev1.md").ConfigureAwait(false),
            originalMarkdown, "markdown revision 1 after an interrupted commit");
        RequireBytes(await ReadWorkspaceFileAsync(executable, fixture, fileId, 2, "interrupted-rev2.md").ConfigureAwait(false),
            revisedMarkdown, "markdown revision 2 after an interrupted commit");
        Console.WriteLine("PASS an interrupted commit leaves either the previous or the fully committed revision and no temp residue");
        return service;
    }

    private static string CreateSyntheticPassphrase()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        try
        {
            return Convert.ToBase64String(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
