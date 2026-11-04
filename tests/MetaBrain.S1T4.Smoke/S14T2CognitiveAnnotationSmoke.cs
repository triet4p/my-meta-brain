using System.Globalization;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Core.Domain;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string S14T2SourceMarker = "S14T2_SOURCE_SECRET_5a0c";
    private const string S14T2CorrectionReason = "owner corrected overlapping passage facets";
    private const string S14T2RejectReason = "the suggestion does not match this passage";

    public static async Task RunS14T2CognitiveAnnotationsAsync(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S14-T2 annotation smoke is Windows-only.");
        }

        using var fixture = S14T2Fixture.Create();
        RunningService? live = null;
        RunningService? restored = null;
        try
        {
            live = await StartServiceAsync(executable, fixture.LiveSettingsPath).ConfigureAwait(false);
            var passphrase = CreateSyntheticPassphrase();
            var provision = await RunOwnerCommandWithInputAsync(
                executable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The owner CLI did not provision the synthetic S14-T2 vault.");

            var sourceBytes = Encoding.UTF8.GetBytes("prefix " + S14T2SourceMarker + " suffix\n");
            var sourcePath = Path.Combine(fixture.OutputDirectory, "s14-t2-source.txt");
            await File.WriteAllBytesAsync(sourcePath, sourceBytes).ConfigureAwait(false);
            var workspaceStatus = await S14T2OwnerAsync(executable, fixture.ControlPipe, "workspace-status").ConfigureAwait(false);
            Require(workspaceStatus.ExitCode == 0, "The workspace status operation failed before the annotation scenario.");
            var treeRevision = WorkspaceNumber(workspaceStatus.StandardOutput, "revision");
            var imported = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-import", "--parent-id", "root", "--name", "s14-t2-source.txt",
                "--input-file", sourcePath, "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(imported.ExitCode == 0 && imported.StandardOutput.Contains("IMPORT id=", StringComparison.Ordinal),
                "The actual owner CLI did not import the synthetic workspace file.");
            var fileId = WorkspaceToken(imported.StandardOutput, "id");
            treeRevision = WorkspaceNumber(imported.StandardOutput, "revision");
            var unreadableDraft = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-create", "--file-id", fileId,
                "--input-file", fixture.OutputDirectory).ConfigureAwait(false);
            Require(unreadableDraft.ExitCode == 4 &&
                    unreadableDraft.StandardError.Contains("cognitive_annotation_input_unreadable", StringComparison.Ordinal),
                "The annotation CLI did not report an unreadable draft path without an unhandled access exception.");


            var anchorOffset = "prefix ".Length;
            var anchorLength = S14T2SourceMarker.Length;
            var anchor = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-anchor-create", "--file-id", fileId,
                "--expected-content-revision", "1",
                "--offset", anchorOffset.ToString(CultureInfo.InvariantCulture),
                "--length", anchorLength.ToString(CultureInfo.InvariantCulture),
                "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(anchor.ExitCode == 0 && anchor.StandardOutput.Contains("state=live", StringComparison.Ordinal),
                "The service did not pin the selected source passage to a live workspace anchor.");
            var anchorId = WorkspaceToken(anchor.StandardOutput, "id");

            var treeBeforeAnnotations = await S14T2OwnerAsync(executable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false);
            Require(treeBeforeAnnotations.ExitCode == 0 && CountFileLines(treeBeforeAnnotations.StandardOutput, fileId) == 1,
                "The synthetic source was not represented by exactly one workspace file.");
            var initialNodeLines = S12T5NodeLines(treeBeforeAnnotations.StandardOutput);

            var firstSuggestionDraft = JsonSerializer.SerializeToUtf8Bytes(new
            {
                anchorId,
                facets = Array.Empty<string>(),
                suggestion = true
            });
            var strictShape = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.OwnerCognitiveAnnotationCreateOperation,
                resourceId = fileId,
                contentBase64 = Convert.ToBase64String(firstSuggestionDraft),
                zoneId = "workspace"
            }).ConfigureAwait(false);
            Require(ReplyCode(strictShape) == "invalid_request",
                "The annotation service accepted a create request with an unowned field.");

            foreach (var operation in new[]
                     {
                         ServiceRequestHandler.OwnerCognitiveAnnotationCreateOperation,
                         ServiceRequestHandler.OwnerCognitiveAnnotationListOperation,
                         ServiceRequestHandler.OwnerCognitiveAnnotationReadOperation,
                         ServiceRequestHandler.OwnerCognitiveAnnotationCorrectOperation,
                         ServiceRequestHandler.OwnerCognitiveAnnotationAcceptOperation,
                         ServiceRequestHandler.OwnerCognitiveAnnotationRejectOperation
                     })
            {
                var denied = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                    operation
                }).ConfigureAwait(false);
                Require(ReplyCode(denied) == "forbidden",
                    "The agent pipe accepted owner-only cognitive annotation operation " + operation + ".");
            }

            var suggestionInput = Path.Combine(fixture.OutputDirectory, "suggestion.json");
            await File.WriteAllBytesAsync(suggestionInput, firstSuggestionDraft).ConfigureAwait(false);
            var firstSuggestion = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-create", "--file-id", fileId, "--input-file", suggestionInput).ConfigureAwait(false);
            Require(firstSuggestion.ExitCode == 0 && firstSuggestion.StandardOutput.Contains("origin=AiSuggestion", StringComparison.Ordinal) &&
                    firstSuggestion.StandardOutput.Contains("review=Pending", StringComparison.Ordinal) &&
                    firstSuggestion.StandardOutput.Contains("kind=Unknown", StringComparison.Ordinal) &&
                    firstSuggestion.StandardOutput.Contains("author=Unknown", StringComparison.Ordinal) &&
                    firstSuggestion.StandardOutput.Contains("evidence=Unknown", StringComparison.Ordinal),
                "A pending suggestion did not remain distinct from owner confirmation or preserve omitted Unknown axes.");
            var firstSuggestionId = WorkspaceToken(firstSuggestion.StandardOutput, "id");

            var ownerInput = Path.Combine(fixture.OutputDirectory, "owner-annotation.json");
            await File.WriteAllBytesAsync(ownerInput, JsonSerializer.SerializeToUtf8Bytes(new
            {
                anchorId,
                facets = new[] { "experience" }
            })).ConfigureAwait(false);
            var ownerAnnotation = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-create", "--file-id", fileId, "--input-file", ownerInput).ConfigureAwait(false);
            Require(ownerAnnotation.ExitCode == 0 && ownerAnnotation.StandardOutput.Contains("origin=Owner", StringComparison.Ordinal) &&
                    ownerAnnotation.StandardOutput.Contains("review=Accepted", StringComparison.Ordinal),
                "An owner-authored annotation did not record owner origin and confirmation.");

            var secondSuggestionInput = Path.Combine(fixture.OutputDirectory, "suggestion-to-reject.json");
            await File.WriteAllBytesAsync(secondSuggestionInput, JsonSerializer.SerializeToUtf8Bytes(new
            {
                anchorId,
                facets = new[] { "world" },
                suggestion = true
            })).ConfigureAwait(false);
            var secondSuggestion = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-create", "--file-id", fileId, "--input-file", secondSuggestionInput).ConfigureAwait(false);
            Require(secondSuggestion.ExitCode == 0 && secondSuggestion.StandardOutput.Contains("review=Pending", StringComparison.Ordinal),
                "The second AI suggestion was not created pending owner review.");
            var secondSuggestionId = WorkspaceToken(secondSuggestion.StandardOutput, "id");

            var list = await S14T2OwnerAsync(executable, fixture.ControlPipe, "cognitive-annotation-list").ConfigureAwait(false);
            Require(list.ExitCode == 0 && list.StandardOutput.Contains("ANNOTATIONS count=3", StringComparison.Ordinal) &&
                    list.StandardOutput.Contains(firstSuggestionId, StringComparison.Ordinal),
                "The owner annotation list did not return all canonical annotation identities.");
            var treeAfterAnnotations = await S14T2OwnerAsync(executable, fixture.ControlPipe, "workspace-tree").ConfigureAwait(false);
            Require(treeAfterAnnotations.ExitCode == 0 && CountFileLines(treeAfterAnnotations.StandardOutput, fileId) == 1 &&
                    S12T5NodeLines(treeAfterAnnotations.StandardOutput).SequenceEqual(initialNodeLines),
                "Multiple annotations/views duplicated or mutated the one canonical workspace file.");

            var correctionInput = Path.Combine(fixture.OutputDirectory, "correction.json");
            await File.WriteAllBytesAsync(correctionInput, JsonSerializer.SerializeToUtf8Bytes(new
            {
                facets = new[] { "world", "experience" },
                reason = S14T2CorrectionReason
            })).ConfigureAwait(false);
            var corrected = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-correct", "--resource-id", firstSuggestionId,
                "--expected-revision", "1", "--input-file", correctionInput).ConfigureAwait(false);
            Require(corrected.ExitCode == 0 && corrected.StandardOutput.Contains("revision=2", StringComparison.Ordinal) &&
                    corrected.StandardOutput.Contains("origin=AiSuggestion", StringComparison.Ordinal) &&
                    corrected.StandardOutput.Contains("review=Pending", StringComparison.Ordinal) &&
                    corrected.StandardOutput.Contains("FACETS World,Experience", StringComparison.Ordinal) &&
                    corrected.StandardOutput.Contains("kind=Unknown", StringComparison.Ordinal) &&
                    corrected.StandardOutput.Contains("author=Unknown", StringComparison.Ordinal) &&
                    corrected.StandardOutput.Contains("evidence=Unknown", StringComparison.Ordinal),
                "The correction did not preserve the AI origin, pending review, overlapping facets and omitted Unknown axes.");

            var staleReview = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-accept", "--resource-id", firstSuggestionId,
                "--expected-revision", "1").ConfigureAwait(false);
            Require(staleReview.ExitCode != 0 && staleReview.StandardError.Contains("revision_conflict", StringComparison.Ordinal),
                "A stale optimistic review overwrote a newer correction instead of returning revision_conflict.");
            var unchangedAfterConflict = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", firstSuggestionId).ConfigureAwait(false);
            Require(unchangedAfterConflict.ExitCode == 0 && unchangedAfterConflict.StandardOutput.Contains("revision=2", StringComparison.Ordinal) &&
                    unchangedAfterConflict.StandardOutput.Contains("review=Pending", StringComparison.Ordinal),
                "The rejected stale review changed the current annotation.");

            var accepted = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-accept", "--resource-id", firstSuggestionId,
                "--expected-revision", "2", "--reason", "owner accepted the corrected suggestion").ConfigureAwait(false);
            Require(accepted.ExitCode == 0 && accepted.StandardOutput.Contains("revision=3", StringComparison.Ordinal) &&
                    accepted.StandardOutput.Contains("origin=AiSuggestion", StringComparison.Ordinal) &&
                    accepted.StandardOutput.Contains("review=Accepted", StringComparison.Ordinal) &&
                    accepted.StandardOutput.Contains("HISTORY revision=3 action=Accepted", StringComparison.Ordinal),
                "Owner acceptance did not append a review revision while retaining the AI suggestion origin.");

            var rejected = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-reject", "--resource-id", secondSuggestionId,
                "--expected-revision", "1", "--reason", S14T2RejectReason).ConfigureAwait(false);
            Require(rejected.ExitCode == 0 && rejected.StandardOutput.Contains("review=Rejected", StringComparison.Ordinal) &&
                    rejected.StandardOutput.Contains("HISTORY revision=2 action=Rejected", StringComparison.Ordinal),
                "Owner rejection did not record the explicit decision and reason in revision history.");

            var revisedSourcePath = Path.Combine(fixture.OutputDirectory, "s14-t2-source-revised.txt");
            var revisedSourceBytes = Encoding.UTF8.GetBytes("prefix a revised source passage suffix\n");
            await File.WriteAllBytesAsync(revisedSourcePath, revisedSourceBytes).ConfigureAwait(false);
            var sourceRevised = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-revise", "--file-id", fileId, "--expected-content-revision", "1",
                "--input-file", revisedSourcePath).ConfigureAwait(false);
            Require(sourceRevised.ExitCode == 0 && sourceRevised.StandardOutput.Contains("contentRevision=2", StringComparison.Ordinal),
                "The owner could not replace the source passage with a new content revision.");
            var staleRead = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", firstSuggestionId).ConfigureAwait(false);
            Require(staleRead.ExitCode == 0 && staleRead.StandardOutput.Contains("state=stale", StringComparison.Ordinal) &&
                    staleRead.StandardOutput.Contains("availability=Available", StringComparison.Ordinal),
                "Reading an annotation after source revision did not report its original anchor as stale.");
            var anchors = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-anchor-list", "--file-id", fileId).ConfigureAwait(false);
            Require(anchors.ExitCode == 0 && anchors.StandardOutput.Contains("count=1", StringComparison.Ordinal) &&
                    anchors.StandardOutput.Contains("state=stale", StringComparison.Ordinal),
                "The source revision did not preserve exactly one stale anchor at its original revision.");

            var rebuilt = await S14T2OwnerAsync(executable, fixture.ControlPipe, "index-rebuild").ConfigureAwait(false);
            Require(rebuilt.ExitCode == 0 && rebuilt.StandardOutput.Contains("INDEX rebuilt", StringComparison.Ordinal),
                "The owner could not rebuild the private index after annotation changes.");
            var afterRebuild = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", firstSuggestionId).ConfigureAwait(false);
            Require(afterRebuild.ExitCode == 0 && afterRebuild.StandardOutput.Contains("revision=3", StringComparison.Ordinal) &&
                    afterRebuild.StandardOutput.Contains("review=Accepted", StringComparison.Ordinal) &&
                    afterRebuild.StandardOutput.Contains("FACETS World,Experience", StringComparison.Ordinal) &&
                    afterRebuild.StandardOutput.Contains("state=stale", StringComparison.Ordinal),
                "Index rebuild lost an owner correction, review decision, overlapping facets or stale anchor state.");

            var locked = await S14T2OwnerAsync(executable, fixture.ControlPipe, "lock").ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The owner vault could not lock after annotation changes.");
            var lockedRead = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", firstSuggestionId).ConfigureAwait(false);
            Require(lockedRead.ExitCode != 0 && lockedRead.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "The annotation service served private state while the vault was locked.");
            await S14T2UnlockAsync(executable, fixture.ControlPipe, passphrase).ConfigureAwait(false);
            Require((await S14T2OwnerAsync(executable, fixture.ControlPipe,
                    "cognitive-annotation-read", "--resource-id", firstSuggestionId).ConfigureAwait(false)).ExitCode == 0,
                "The owner could not read the annotation after lock/unlock.");

            await StopServiceAsync(live).ConfigureAwait(false);
            live = await StartServiceAsync(executable, fixture.LiveSettingsPath).ConfigureAwait(false);
            var restartedStatus = await ReadStatusAsync(executable, fixture.ControlPipe).ConfigureAwait(false);
            Require(restartedStatus.Contains("vault=locked", StringComparison.Ordinal),
                "The restarted annotation service did not begin locked.");
            var restartLockedRead = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", firstSuggestionId).ConfigureAwait(false);
            Require(restartLockedRead.ExitCode != 0 && restartLockedRead.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "A service restart exposed annotations before owner unlock.");
            await S14T2UnlockAsync(executable, fixture.ControlPipe, passphrase).ConfigureAwait(false);
            var restartedRead = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", firstSuggestionId).ConfigureAwait(false);
            Require(restartedRead.ExitCode == 0 && restartedRead.StandardOutput.Contains("review=Accepted", StringComparison.Ordinal) &&
                    restartedRead.StandardOutput.Contains("state=stale", StringComparison.Ordinal),
                "The owner could not read the same confirmed annotation and stale anchor after service restart/unlock.");
            Console.WriteLine("PASS encrypted annotations, review history, source staleness and owner-only service behavior survive lock/unlock and process restart");

            var backupPath = Path.Combine(fixture.BackupDirectory, "s14-t2-annotations.mbbackup");
            var backup = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "backup", "--output-file", backupPath).ConfigureAwait(false);
            Require(backup.ExitCode == 0 && backup.StandardOutput.Contains("BACKUP written", StringComparison.Ordinal) &&
                    File.Exists(backupPath),
                "The actual owner CLI did not create the encrypted annotation backup.");
            RequireNoS12T5Plaintext(await File.ReadAllBytesAsync(backupPath).ConfigureAwait(false),
                new[] { S14T2SourceMarker, S14T2CorrectionReason, S14T2RejectReason, "AiSuggestion", "World,Experience" },
                "S14-T2 encrypted backup");
            foreach (var path in Directory.GetFiles(Path.Combine(fixture.LiveRoot, "vault"), "*", SearchOption.AllDirectories))
            {
                RequireNoS12T5Plaintext(await File.ReadAllBytesAsync(path).ConfigureAwait(false),
                    new[] { S14T2SourceMarker, S14T2CorrectionReason, S14T2RejectReason, "AiSuggestion", "World,Experience" },
                    "S14-T2 vault file " + Path.GetFileName(path));
            }

            var exportPath = Path.Combine(fixture.OutputDirectory, "selected-annotation.md");
            var exported = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "export", "--resource-ids", firstSuggestionId, "--output-file", exportPath).ConfigureAwait(false);
            Require(exported.ExitCode == 0 && exported.StandardOutput.Contains("EXPORT written", StringComparison.Ordinal) &&
                    File.Exists(exportPath),
                "The explicit owner-selected plaintext annotation export failed.");
            var exportText = await File.ReadAllTextAsync(exportPath).ConfigureAwait(false);
            Require(exportText.Contains(firstSuggestionId, StringComparison.Ordinal) &&
                    exportText.Contains("Record: cognitive annotation", StringComparison.Ordinal) &&
                    exportText.Contains("Origin: AiSuggestion", StringComparison.Ordinal) &&
                    exportText.Contains("Review: Accepted", StringComparison.Ordinal) &&
                    exportText.Contains("state=stale", StringComparison.Ordinal) &&
                    exportText.Contains("action=Corrected", StringComparison.Ordinal) &&
                    !exportText.Contains(S14T2SourceMarker, StringComparison.Ordinal),
                "The selected plaintext export omitted review/anchor history or disclosed source bytes.");

            var restore = await S12T5RestoreAsync(
                executable, fixture.ControlPipe, backupPath, fixture.RestoreTarget, passphrase).ConfigureAwait(false);
            Require(restore.ExitCode == 0 && restore.StandardOutput.Contains("RESTORE written", StringComparison.Ordinal),
                "The actual owner restore operation did not restore the encrypted annotation backup.");
            restored = await StartServiceAsync(executable, fixture.RestoreSettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(executable, fixture.RestoreControlPipe).ConfigureAwait(false))
                    .Contains("vault=locked", StringComparison.Ordinal),
                "The separately restored vault did not start locked.");
            await S14T2UnlockAsync(executable, fixture.RestoreControlPipe, passphrase).ConfigureAwait(false);
            var restoredTree = await S14T2OwnerAsync(executable, fixture.RestoreControlPipe, "workspace-tree").ConfigureAwait(false);
            Require(restoredTree.ExitCode == 0 && CountFileLines(restoredTree.StandardOutput, fileId) == 1,
                "The encrypted backup restore changed the one workspace file identity.");
            var restoredList = await S14T2OwnerAsync(executable, fixture.RestoreControlPipe,
                "cognitive-annotation-list").ConfigureAwait(false);
            Require(restoredList.ExitCode == 0 && restoredList.StandardOutput.Contains("ANNOTATIONS count=3", StringComparison.Ordinal),
                "The backup restore did not reproduce all canonical annotation records.");
            var restoredRead = await S14T2OwnerAsync(executable, fixture.RestoreControlPipe,
                "cognitive-annotation-read", "--resource-id", firstSuggestionId).ConfigureAwait(false);
            Require(restoredRead.ExitCode == 0 && restoredRead.StandardOutput.Contains("revision=3", StringComparison.Ordinal) &&
                    restoredRead.StandardOutput.Contains("origin=AiSuggestion", StringComparison.Ordinal) &&
                    restoredRead.StandardOutput.Contains("review=Accepted", StringComparison.Ordinal) &&
                    restoredRead.StandardOutput.Contains("state=stale", StringComparison.Ordinal),
                "The restored annotation lost its canonical correction, owner review or exact stale anchor.");
            var restoredRebuild = await S14T2OwnerAsync(executable, fixture.RestoreControlPipe,
                "index-rebuild").ConfigureAwait(false);
            Require(restoredRebuild.ExitCode == 0 && restoredRebuild.StandardOutput.Contains("INDEX rebuilt", StringComparison.Ordinal),
                "The restored vault could not rebuild its derived private index.");
            var restoredAfterRebuild = await S14T2OwnerAsync(executable, fixture.RestoreControlPipe,
                "cognitive-annotation-read", "--resource-id", firstSuggestionId).ConfigureAwait(false);
            Require(restoredAfterRebuild.ExitCode == 0 &&
                    restoredAfterRebuild.StandardOutput.Contains("review=Accepted", StringComparison.Ordinal) &&
                    restoredAfterRebuild.StandardOutput.Contains("FACETS World,Experience", StringComparison.Ordinal),
                "A rebuild of the separately restored vault lost the durable owner correction or review decision.");
            Console.WriteLine("PASS encrypted backup/restore and explicit scoped plaintext export preserve versioned annotations without copying source bytes");
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
    }

    private static async Task<CommandResult> S14T2OwnerAsync(
        string executable,
        string controlPipe,
        string command,
        params string[] arguments)
    {
        var full = new List<string>(arguments.Length + 4)
        {
            "owner",
            command,
            "--control-pipe",
            controlPipe
        };
        full.AddRange(arguments);
        return await RunOwnerCommandAsync(executable, full.ToArray()).ConfigureAwait(false);
    }

    private static async Task S14T2UnlockAsync(string executable, string controlPipe, string passphrase)
    {
        var unlock = await RunOwnerCommandWithInputAsync(executable, passphrase + Environment.NewLine,
            "owner", "unlock", "--control-pipe", controlPipe).ConfigureAwait(false);
        Require(unlock.ExitCode == 0 && unlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
            "The owner could not unlock the S14-T2 annotation vault.");
    }

    private static int CountFileLines(string tree, string fileId) =>
        tree.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.StartsWith("FILE id=" + fileId + " ", StringComparison.Ordinal));
}

internal sealed class S14T2Fixture : IDisposable
{
    private bool _disposed;

    private S14T2Fixture(
        string root,
        string liveRoot,
        string liveSettingsPath,
        string controlPipe,
        string agentPipe,
        string outputDirectory,
        string backupDirectory,
        string restoreRoot,
        string restoreSettingsPath,
        string restoreControlPipe)
    {
        Root = root;
        LiveRoot = liveRoot;
        LiveSettingsPath = liveSettingsPath;
        ControlPipe = controlPipe;
        AgentPipe = agentPipe;
        OutputDirectory = outputDirectory;
        BackupDirectory = backupDirectory;
        RestoreRoot = restoreRoot;
        RestoreSettingsPath = restoreSettingsPath;
        RestoreControlPipe = restoreControlPipe;
    }

    public string Root { get; }
    public string LiveRoot { get; }
    public string LiveSettingsPath { get; }
    public string ControlPipe { get; }
    public string AgentPipe { get; }
    public string OutputDirectory { get; }
    public string BackupDirectory { get; }
    public string RestoreRoot { get; }
    public string RestoreSettingsPath { get; }
    public string RestoreControlPipe { get; }
    public string RestoreTarget => Path.Combine(RestoreRoot, "vault");

    public static S14T2Fixture Create()
    {
        var repositoryRoot = SmokeFixture.FindRepositoryRoot();
        var root = Path.Combine(repositoryRoot, "runtime", "s14-t2", "run-" + Guid.NewGuid().ToString("N"));
        var liveRoot = Path.Combine(root, "live");
        var restoreRoot = Path.Combine(root, "restored");
        var outputDirectory = Path.Combine(root, "output");
        var backupDirectory = Path.Combine(outputDirectory, "backups");
        foreach (var directory in new[] { liveRoot, restoreRoot, outputDirectory, backupDirectory })
        {
            Directory.CreateDirectory(directory);
        }

        var controlPipe = "MetaBrainS14T2-" + Guid.NewGuid().ToString("N");
        var restoreControlPipe = "MetaBrainS14T2R-" + Guid.NewGuid().ToString("N");
        var liveSettingsPath = S12T5Fixture.WriteSettingsForTest(liveRoot, controlPipe);
        var restoreSettingsPath = S12T5Fixture.WriteSettingsForTest(restoreRoot, restoreControlPipe);
        return new S14T2Fixture(root, liveRoot, liveSettingsPath, controlPipe, controlPipe + ".agent",
            outputDirectory, backupDirectory, restoreRoot, restoreSettingsPath, restoreControlPipe);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            var repositoryRoot = SmokeFixture.FindRepositoryRoot();
            var ownedPrefix = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(repositoryRoot, "runtime", "s14-t2"))) + Path.DirectorySeparatorChar;
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
            if (!fullRoot.StartsWith(ownedPrefix, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullRoot).StartsWith("run-", StringComparison.Ordinal))
            {
                return;
            }

            if (Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
