using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    public static async Task RunS17T3WorkspaceSharingCliAsync(string connectionsExecutable, string cliExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S17-T3 protected owner handoff smoke is Windows-only.");
        }

        using var fixture = S12T5Fixture.Create();
        RunningService? live = null;
        const string passphrase = "synthetic S17-T3 passphrase; never production data";
        try
        {
            live = await StartServiceAsync(connectionsExecutable, fixture.LiveSettingsPath).ConfigureAwait(false);
            var provision = await RunOwnerCommandWithInputAsync(
                cliExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0, "The actual CLI did not provision the synthetic S17-T3 vault.");
            var catalogBefore = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "catalog-list").ConfigureAwait(false);
            Require(catalogBefore.ExitCode == 0 &&
                    catalogBefore.StandardOutput.Contains("CATALOG count=0", StringComparison.Ordinal),
                "The synthetic catalog was not empty before the owner folder-sharing workflow.");

            var treeRevision = await S12T5RevisionAsync(cliExecutable, fixture.ControlPipe).ConfigureAwait(false);
            var selectedFolder = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-mkdir", "--parent-id", "root", "--name", "s17t3-selected",
                "--expected-tree-revision", treeRevision).ConfigureAwait(false);
            Require(selectedFolder.ExitCode == 0, "The synthetic sharing folder could not be created.");
            var selectedFolderId = WorkspaceToken(selectedFolder.StandardOutput, "id");
            treeRevision = WorkspaceNumber(selectedFolder.StandardOutput, "revision");

            var excludedFolder = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-mkdir", "--parent-id", selectedFolderId, "--name", "s17t3-excluded",
                "--expected-tree-revision", treeRevision).ConfigureAwait(false);
            Require(excludedFolder.ExitCode == 0, "The synthetic excluded folder could not be created.");
            var excludedFolderId = WorkspaceToken(excludedFolder.StandardOutput, "id");
            treeRevision = WorkspaceNumber(excludedFolder.StandardOutput, "revision");

            var includedBytes = Encoding.UTF8.GetBytes("Synthetic included workspace bytes; no private corpus.");
            var excludedBytes = Encoding.UTF8.GetBytes("Synthetic excluded workspace bytes; no private corpus.");
            var included = await S12T5ImportAsync(cliExecutable, fixture, selectedFolderId,
                "s17t3-included.txt", includedBytes, treeRevision).ConfigureAwait(false);
            var excluded = await S12T5ImportAsync(cliExecutable, fixture, excludedFolderId,
                "s17t3-excluded.txt", excludedBytes, included.TreeRevision).ConfigureAwait(false);

            var expiresAtUtc = DateTimeOffset.UtcNow.AddHours(2).ToString("O", CultureInfo.InvariantCulture);
            var broadPreview = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-share-preview", "--folder-id", selectedFolderId,
                "--expires-at-utc", expiresAtUtc,
                "--operations", "workspace.file.read,provider.egress",
                "--provider", "synthetic-provider", "--model", "synthetic-model", "--max-cost-usd", "0.25").ConfigureAwait(false);
            var includedDescriptor =
                $"MEMBER file={included.FileId} revision=1 length={includedBytes.Length} " +
                $"sha256={Convert.ToHexString(SHA256.HashData(includedBytes)).ToLowerInvariant()}";
            var excludedDescriptor =
                $"MEMBER file={excluded.FileId} revision=1 length={excludedBytes.Length} " +
                $"sha256={Convert.ToHexString(SHA256.HashData(excludedBytes)).ToLowerInvariant()}";
            Require(broadPreview.ExitCode == 0 &&
                    broadPreview.StandardOutput.Contains(includedDescriptor, StringComparison.Ordinal) &&
                    broadPreview.StandardOutput.Contains(excludedDescriptor, StringComparison.Ordinal) &&
                    broadPreview.StandardOutput.Contains("egress=synthetic-provider/synthetic-model", StringComparison.Ordinal) &&
                    broadPreview.StandardOutput.Contains("snapshot=frozen", StringComparison.Ordinal) &&
                    broadPreview.StandardOutput.Contains("updates=members moved out or deleted lose access;", StringComparison.Ordinal) &&
                    !broadPreview.StandardOutput.Contains("s17t3-included.txt", StringComparison.Ordinal) &&
                    !broadPreview.StandardOutput.Contains("s17t3-excluded.txt", StringComparison.Ordinal),
                "The actual CLI preview did not show exact members, pinned revisions, length/digest, update semantics and explicit egress without publishing private names.");
            var broadPreviewId = WorkspaceToken(broadPreview.StandardOutput, "id");

            var narrowed = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-share-narrow", "--preview-id", broadPreviewId,
                "--exclude-folder-ids", excludedFolderId).ConfigureAwait(false);
            Require(narrowed.ExitCode == 0 &&
                    narrowed.StandardOutput.Contains(included.FileId, StringComparison.Ordinal) &&
                    !narrowed.StandardOutput.Contains(excluded.FileId, StringComparison.Ordinal) &&
                    narrowed.StandardOutput.Contains("EXCLUDED FOLDER id=" + excludedFolderId, StringComparison.Ordinal) &&
                    narrowed.StandardOutput.Contains("oldPreviewInvalidated=true", StringComparison.Ordinal),
                "The actual CLI narrowing did not replace the old preview with the identity-excluded exact scope.");
            var narrowedPreviewId = WorkspaceToken(narrowed.StandardOutput, "id");
            var handoffPath = Path.Combine(fixture.OutputDirectory, "s17t3-approved.handoff");
            var approval = await RunOwnerCommandWithInputAsync(cliExecutable, "APPROVE" + Environment.NewLine,
                "owner", "workspace-share-approve", "--control-pipe", fixture.ControlPipe,
                "--preview-id", narrowedPreviewId, "--handoff-file", handoffPath).ConfigureAwait(false);
            Require(approval.ExitCode == 0 &&
                    approval.StandardOutput.Contains("WORKSPACE GRANT issued", StringComparison.Ordinal) &&
                    approval.StandardOutput.Contains("token redacted", StringComparison.Ordinal) &&
                    !approval.StandardOutput.Contains("mb1_", StringComparison.Ordinal) &&
                    !approval.StandardError.Contains("mb1_", StringComparison.Ordinal),
                "The actual CLI did not issue the approved workspace grant with a redacted bearer.");
            var grantId = WorkspaceToken(approval.StandardOutput, "id").TrimEnd(';');
            var handoffToken = ReadProtectedHandoffToken(handoffPath);
            try
            {
                Require(IsOpaqueScopeToken(handoffToken), "The protected handoff did not contain an opaque scope token.");
                VerifyOwnerHandoffProtection(handoffPath);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(handoffToken);
            }

            var grantList = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-share-grants").ConfigureAwait(false);
            Require(grantList.ExitCode == 0 &&
                    grantList.StandardOutput.Contains("WORKSPACE GRANT id=" + grantId, StringComparison.Ordinal) &&
                    grantList.StandardOutput.Contains("pinnedRevision=1", StringComparison.Ordinal) &&
                    grantList.StandardOutput.Contains("available=True", StringComparison.Ordinal) &&
                    grantList.StandardOutput.Contains("egress=synthetic-provider/synthetic-model", StringComparison.Ordinal) &&
                    !grantList.StandardOutput.Contains(excluded.FileId, StringComparison.Ordinal),
                $"Owner scope inspection did not report the exact pinned member, egress and exclusion state (grant={grantId}; includedFile={included.FileId}; excludedFile={excluded.FileId}; stdout={grantList.StandardOutput}; stderr={grantList.StandardError}).");

            var newBytes = Encoding.UTF8.GetBytes("Synthetic new member added after approval; it must not widen the grant.");
            var newMember = await S12T5ImportAsync(cliExecutable, fixture, selectedFolderId,
                "s17t3-new-member.txt", newBytes, excluded.TreeRevision).ConfigureAwait(false);
            var revisedBytes = Encoding.UTF8.GetBytes("Synthetic revision two; the grant stays pinned to revision one.");
            var revisionPath = await S12T5WriteInputAsync(fixture, "s17t3-revision-two.bin", revisedBytes).ConfigureAwait(false);
            var revise = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-revise", "--file-id", included.FileId,
                "--expected-content-revision", "1", "--input-file", revisionPath).ConfigureAwait(false);
            Require(revise.ExitCode == 0, "The synthetic member could not advance to revision two.");
            grantList = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-share-grants").ConfigureAwait(false);
            Require(grantList.ExitCode == 0 &&
                    grantList.StandardOutput.Contains("file=" + included.FileId + " pinnedRevision=1 currentRevision=2", StringComparison.Ordinal) &&
                    grantList.StandardOutput.Contains("stale=True available=True", StringComparison.Ordinal) &&
                    !grantList.StandardOutput.Contains(newMember.FileId, StringComparison.Ordinal),
                "Owner scope inspection did not surface pinned-revision staleness without adding a new member.");

            var sessionPath = Path.Combine(fixture.OutputDirectory, "s17t3-session.handoff");
            var redeemed = await RunOwnerCommandAsync(cliExecutable,
                "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
                "--handoff-file", handoffPath, "--session-file", sessionPath).ConfigureAwait(false);
            Require(redeemed.ExitCode == 0 && redeemed.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
                "The actual CLI did not redeem the protected folder-grant handoff.");
            VerifyOwnerHandoffProtection(sessionPath);
            var sessionId = ParseSessionId(redeemed.StandardOutput);
            var sessionInspection = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "sessions", "--session-id", sessionId).ConfigureAwait(false);
            Require(sessionInspection.ExitCode == 0 &&
                    sessionInspection.StandardOutput.Contains("WORKSPACE MEMBER file=" + included.FileId + " pinnedRevision=1", StringComparison.Ordinal) &&
                    !sessionInspection.StandardOutput.Contains(excluded.FileId, StringComparison.Ordinal) &&
                    sessionInspection.StandardOutput.Contains("egress=synthetic-provider/synthetic-model", StringComparison.Ordinal),
                "Owner session inspection did not show the exact pinned workspace scope and approved egress.");

            var sessionRevoke = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "revoke-session", "--session-id", sessionId).ConfigureAwait(false);
            Require(sessionRevoke.ExitCode == 0 && sessionRevoke.StandardOutput.Contains("SESSION revoked", StringComparison.Ordinal),
                "The owner CLI could not revoke the inspected workspace session.");
            var revokedSessionInspection = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "sessions", "--session-id", sessionId).ConfigureAwait(false);
            Require(revokedSessionInspection.ExitCode != 0 &&
                    revokedSessionInspection.StandardError.Contains("session_unknown", StringComparison.Ordinal),
                "A revoked session remained available through owner inspection.");

            var grantRevoke = await RunOwnerCommandWithInputAsync(cliExecutable, "REVOKE" + Environment.NewLine,
                "owner", "workspace-share-revoke", "--control-pipe", fixture.ControlPipe,
                "--grant-id", grantId).ConfigureAwait(false);
            Require(grantRevoke.ExitCode == 0 && grantRevoke.StandardOutput.Contains("WORKSPACE GRANT revoked", StringComparison.Ordinal),
                "The owner CLI could not revoke the workspace folder grant.");

            var rejectPreview = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-share-preview", "--folder-id", selectedFolderId,
                "--expires-at-utc", expiresAtUtc).ConfigureAwait(false);
            Require(rejectPreview.ExitCode == 0, "The owner CLI could not create a preview for the reject path.");
            var rejectPreviewId = WorkspaceToken(rejectPreview.StandardOutput, "id");
            var rejected = await RunOwnerCommandWithInputAsync(cliExecutable, "REJECT" + Environment.NewLine,
                "owner", "workspace-share-reject", "--control-pipe", fixture.ControlPipe,
                "--preview-id", rejectPreviewId).ConfigureAwait(false);
            Require(rejected.ExitCode == 0 && rejected.StandardOutput.Contains("no grant created", StringComparison.Ordinal),
                "The owner CLI reject path did not discard its preview without issuing a grant.");
            var rejectedHandoff = Path.Combine(fixture.OutputDirectory, "s17t3-rejected.handoff");
            var rejectedReuse = await RunOwnerCommandWithInputAsync(cliExecutable, "APPROVE" + Environment.NewLine,
                "owner", "workspace-share-approve", "--control-pipe", fixture.ControlPipe,
                "--preview-id", rejectPreviewId, "--handoff-file", rejectedHandoff).ConfigureAwait(false);
            Require(rejectedReuse.ExitCode != 0 &&
                    rejectedReuse.StandardError.Contains("scope_preview_unavailable", StringComparison.Ordinal),
                "A rejected preview could still issue a grant.");
            if (File.Exists(rejectedHandoff))
            {
                File.Delete(rejectedHandoff);
            }

            var stalePreview = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-share-preview", "--folder-id", selectedFolderId,
                "--expires-at-utc", expiresAtUtc).ConfigureAwait(false);
            Require(stalePreview.ExitCode == 0, "The owner CLI could not create a preview for the stale-preview negative.");
            var stalePreviewId = WorkspaceToken(stalePreview.StandardOutput, "id");
            var currentTreeRevision = await S12T5RevisionAsync(cliExecutable, fixture.ControlPipe).ConfigureAwait(false);
            var driftFolder = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-mkdir", "--parent-id", selectedFolderId, "--name", "s17t3-tree-drift",
                "--expected-tree-revision", currentTreeRevision).ConfigureAwait(false);
            Require(driftFolder.ExitCode == 0, "The synthetic tree drift could not be introduced.");
            var staleHandoff = Path.Combine(fixture.OutputDirectory, "s17t3-stale.handoff");
            var staleApproval = await RunOwnerCommandWithInputAsync(cliExecutable, "APPROVE" + Environment.NewLine,
                "owner", "workspace-share-approve", "--control-pipe", fixture.ControlPipe,
                "--preview-id", stalePreviewId, "--handoff-file", staleHandoff).ConfigureAwait(false);
            Require(staleApproval.ExitCode != 0 &&
                    staleApproval.StandardError.Contains("request_stale", StringComparison.Ordinal) &&
                    !staleApproval.StandardOutput.Contains("WORKSPACE GRANT issued", StringComparison.Ordinal),
                "A stale workspace preview was not refused before grant issuance.");
            if (File.Exists(staleHandoff))
            {
                File.Delete(staleHandoff);
            }

            var lockPreview = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-share-preview", "--folder-id", selectedFolderId,
                "--expires-at-utc", expiresAtUtc).ConfigureAwait(false);
            Require(lockPreview.ExitCode == 0, "The owner CLI could not create a preview for the lock negative.");
            var lockPreviewId = WorkspaceToken(lockPreview.StandardOutput, "id");
            var locked = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe, "lock").ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The owner could not lock the synthetic vault before the approval negative.");
            var lockedHandoff = Path.Combine(fixture.OutputDirectory, "s17t3-locked.handoff");
            var lockedApproval = await RunOwnerCommandWithInputAsync(cliExecutable, "APPROVE" + Environment.NewLine,
                "owner", "workspace-share-approve", "--control-pipe", fixture.ControlPipe,
                "--preview-id", lockPreviewId, "--handoff-file", lockedHandoff).ConfigureAwait(false);
            Require(lockedApproval.ExitCode != 0 &&
                    lockedApproval.StandardError.Contains("vault_locked", StringComparison.Ordinal) &&
                    !lockedApproval.StandardOutput.Contains("WORKSPACE GRANT issued", StringComparison.Ordinal),
                "The locked service accepted a workspace folder grant approval.");
            if (File.Exists(lockedHandoff))
            {
                File.Delete(lockedHandoff);
            }

            var unlocked = await RunOwnerCommandWithInputAsync(cliExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0, "The owner could not unlock after the lock-negative check.");
            var lockedPreviewReuse = await RunOwnerCommandWithInputAsync(cliExecutable, "APPROVE" + Environment.NewLine,
                "owner", "workspace-share-approve", "--control-pipe", fixture.ControlPipe,
                "--preview-id", lockPreviewId,
                "--handoff-file", Path.Combine(fixture.OutputDirectory, "s17t3-lock-reuse.handoff")).ConfigureAwait(false);
            Require(lockedPreviewReuse.ExitCode != 0 &&
                    lockedPreviewReuse.StandardError.Contains("scope_preview_unavailable", StringComparison.Ordinal),
                "A pending folder preview survived vault lock/unlock.");
            var remainingGrants = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "workspace-share-grants").ConfigureAwait(false);
            Require(remainingGrants.ExitCode == 0 && remainingGrants.StandardOutput.Contains("WORKSPACE GRANTS count=1", StringComparison.Ordinal),
                "Preview rejection, stale approval or lock produced an unintended folder grant.");
            var catalogAfter = await S12T5OwnerAsync(cliExecutable, fixture.ControlPipe,
                "catalog-list").ConfigureAwait(false);
            Require(catalogAfter.ExitCode == 0 &&
                    catalogAfter.StandardOutput.Contains("CATALOG count=0", StringComparison.Ordinal),
                "The owner folder-sharing workflow unexpectedly published private metadata to the catalog.");

            var outputs = new[]
            {
                provision, catalogBefore, broadPreview, narrowed, approval, grantList, revise, redeemed,
                sessionInspection, sessionRevoke, revokedSessionInspection, grantRevoke,
                rejectPreview, rejected, rejectedReuse, stalePreview, staleApproval, lockPreview,
                locked, lockedApproval, unlocked, lockedPreviewReuse, remainingGrants, catalogAfter
            };
            Require(outputs.All(output =>
                    !output.StandardOutput.Contains(passphrase, StringComparison.Ordinal) &&
                    !output.StandardError.Contains(passphrase, StringComparison.Ordinal) &&
                    !output.StandardOutput.Contains("mb1_", StringComparison.Ordinal) &&
                    !output.StandardError.Contains("mb1_", StringComparison.Ordinal)),
                "The CLI exposed the synthetic owner passphrase or a raw bearer in its output.");
            var operationalLog = await File.ReadAllBytesAsync(
                Path.Combine(fixture.LiveRoot, "logs", "operations.log")).ConfigureAwait(false);
            var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
            var loggedToken = ReadProtectedHandoffToken(handoffPath);
            try
            {
                Require(!ContainsBytes(operationalLog, passphraseBytes) &&
                        !ContainsBytes(operationalLog, loggedToken),
                    "The safe operational log exposed the synthetic owner passphrase or raw bearer.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passphraseBytes);
                CryptographicOperations.ZeroMemory(loggedToken);
                CryptographicOperations.ZeroMemory(operationalLog);
            }

            Console.WriteLine("PASS actual CLI folder preview, post-preview narrowing, explicit egress, protected handoff, redemption, exact owner session inspection/revoke, grant inspection/revoke, reject, stale-preview and lock negatives; no synthetic secret in CLI output or safe operational log");
            Console.WriteLine("LIMIT synthetic project-local vault/workspace only; no private corpus, catalog publication, provider call, MCP normal client, browser, ACL or host mutation was exercised.");
        }
        finally
        {
            if (live is not null)
            {
                await StopServiceAsync(live).ConfigureAwait(false);
            }
        }
    }
}
