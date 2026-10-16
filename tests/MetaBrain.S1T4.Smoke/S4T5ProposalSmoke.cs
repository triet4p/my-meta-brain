using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Core.Domain;

namespace MetaBrain.S1T4.Smoke;

// S4-T5: agent proposal write interface through the existing
// core/application/connections boundaries. One synthetic owner vault is
// provisioned and unlocked through the real owner CLI over the real
// named-pipe service. A real task fixture yields a lesson; the agent
// proposes creating it (and later revising an existing item at an exact
// revision precondition) through the real agent CLI and the real MCP
// bridge, into an owner-approved inbox destination. Every positive and
// negative scenario runs through the real surfaces: a read-only token
// cannot write, a denied destination or an out-of-scope/guessed reference
// denies generically, a stale revision rejects without mutation, a fake
// user confirmation stays an unconfirmed claim, and a repeated operation
// yields one stable proposal while a conflicting key reuse errors without
// mutation. The owner then reads the exact pending proposal with its
// provenance. All evidence is observed from the real service and real
// persisted files; nothing here is a mock.
internal static partial class OwnerServiceSmoke
{
    private const string S4T5PrivateZone = "s4t5-private-zone";
    private const string S4T5InboxZone = "s4t5-inbox-zone";
    private const string S4T5Inbox = "s4t5-inbox";
    private const string S4T5ItemAlpha = "s4t5-item-alpha";
    private const string S4T5Source = "s4t5-source-alpha";
    private const string S4T5ItemOutside = "s4t5-item-outside";
    private const string S4T5AlphaBody = "Synthetic S4-T5 body alpha: the return-to-project playbook; never a real owner secret.";
    private const string S4T5OutsideBody = "Synthetic S4-T5 body S4T5-OUTSIDE-SENTINEL-4c17: kept outside every grant; never a real owner secret.";

    private static string S4T5ItemDraftJson(string title, string body, DateTimeOffset occurred, object? sources, string kind = "lesson", string author = "assistant", string evidence = "unverified") =>
        JsonSerializer.Serialize(new
        {
            kind,
            author,
            creationMethod = "paraphrase",
            evidence,
            level = "l1",
            title,
            body,
            sources = sources ?? Array.Empty<object>(),
            occurredAtUtc = occurred,
            recordedAtUtc = occurred.AddMinutes(1),
            applicability = new { project = "synthetic-s4t5-project", environment = "synthetic-s4t5-env", version = "s4t5-v1" }
        });

    private static string S4T5SourceDraftJson(string title, string locator, string version, string availability, DateTimeOffset now) =>
        JsonSerializer.Serialize(new
        {
            title,
            locator,
            version,
            availability,
            creationMethod = "directWrite",
            capturedAtUtc = now,
            recordedAtUtc = now.AddMinutes(1)
        });

    private static object S4T5SourceReference(string? sourceId = null, long sourceRevision = 1L) => new
    {
        sourceId = sourceId ?? S4T5Source,
        locator = (string?)null,
        availability = "available",
        sourceRevision,
        span = new { start = 0L, end = 64L }
    };

    /// <summary>
    /// One S4-T5 proposal draft JSON: the create/revise kind, the caller
    /// idempotency key, the approved inbox destination, the revise subject
    /// with its exact revision precondition, the proposed item content, and
    /// the optional user-confirmation claim.
    /// </summary>
    private static string S4T5ProposalDraftJson(
        string kind,
        string idempotencyKey,
        string? subjectResourceId,
        long? subjectRevision,
        string itemJson,
        bool? userConfirmed = null) =>
        JsonSerializer.Serialize(new
        {
            kind,
            idempotencyKey,
            destinationResourceId = S4T5Inbox,
            destinationRevision = 1L,
            subjectResourceId,
            subjectRevision,
            item = JsonSerializer.Deserialize<JsonElement>(itemJson),
            userConfirmed
        });

    internal static async Task RunS4T5ProposalAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S4-T5 proposal smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S4-T5 agent proposal write interface (create/revise, inbox destination, never auto-publish)");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "Provision did not create the S4-T5 synthetic vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");
            var unlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0 && unlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S4-T5 vault did not unlock (exit=" + unlock.ExitCode + "; stderr=" + unlock.StandardError.Trim() + ").");

            var now = DateTimeOffset.UtcNow.AddMinutes(-5);
            await WriteS4T5ResourceAsync(connectionsExecutable, fixture, S4T5Source, S4T5PrivateZone,
                S4T5SourceDraftJson("Synthetic S4-T5 registry title", "s4t5-enrolled-synthetic-locator", "synthetic-source-v1", "available", now),
                "owner", "source-create", "SOURCE").ConfigureAwait(false);
            await WriteS4T5ResourceAsync(connectionsExecutable, fixture, S4T5Inbox, S4T5InboxZone,
                S4T5ItemDraftJson("Synthetic S4-T5 inbox marker",
                    "Synthetic S4-T5 inbox marker body: the owner-approved proposal destination; never a real owner secret.", now, null),
                "owner", "memory-create", "MEMORY").ConfigureAwait(false);
            await WriteS4T5ResourceAsync(connectionsExecutable, fixture, S4T5ItemAlpha, S4T5PrivateZone,
                S4T5ItemDraftJson("Synthetic S4-T5 alpha lesson", S4T5AlphaBody, now, new[] { S4T5SourceReference() }),
                "owner", "memory-create", "MEMORY").ConfigureAwait(false);
            await WriteS4T5ResourceAsync(connectionsExecutable, fixture, S4T5ItemOutside, S4T5PrivateZone,
                S4T5ItemDraftJson("Synthetic S4-T5 outside lesson", S4T5OutsideBody, now, null),
                "owner", "memory-create", "MEMORY").ConfigureAwait(false);

            var rebuild = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(rebuild.ExitCode == 0 && rebuild.StandardOutput.Contains("INDEX rebuilt schema=2", StringComparison.Ordinal),
                "The S4-T5 private index rebuild did not materialize schema=2 (exit=" + rebuild.ExitCode + "; stderr=" + rebuild.StandardError.Trim() + ").");

            // Owner-approved proposal authorization: the operation plus the
            // separate concrete inbox destination. A read-only grant carries
            // the operation nowhere; a wrong-destination grant approves a
            // different inbox; the cross-grant grant approves the same inbox
            // under a second, distinct owner authorization.
            var proposeSession = await IssueS4T5GrantAsync(connectionsExecutable, fixture, "s4t5-propose", "proposal.create",
                new[] { S4T5ItemAlpha, S4T5Source }, new[] { S4T5Inbox }).ConfigureAwait(false);
            var readOnlySession = await IssueS4T5GrantAsync(connectionsExecutable, fixture, "s4t5-readonly", "memory.read",
                new[] { S4T5ItemAlpha }, Array.Empty<string>()).ConfigureAwait(false);
            var wrongDestinationSession = await IssueS4T5GrantAsync(connectionsExecutable, fixture, "s4t5-wrongdest", "proposal.create",
                new[] { S4T5ItemAlpha, S4T5Source }, new[] { S4T5ItemOutside }).ConfigureAwait(false);
            var crossGrantSession = await IssueS4T5GrantAsync(connectionsExecutable, fixture, "s4t5-crossgrant", "proposal.create",
                new[] { S4T5Source }, new[] { S4T5Inbox }).ConfigureAwait(false);

            // A real task fixture yields a lesson: the agent proposes creating
            // it, citing the approved source revision, through the real CLI.
            var createDraftPath = await WriteS4T5ProposalDraftAsync(fixture, "s4t5-create",
                S4T5ProposalDraftJson("create", "s4t5-key-create-1", null, null,
                    S4T5ItemDraftJson("Synthetic S4-T5 proposed lesson",
                        "Synthetic S4-T5 body proposed: the return-to-project lesson the synthetic task produced; never a real owner secret.",
                        now, new[] { S4T5SourceReference() }))).ConfigureAwait(false);
            var created = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", proposeSession.SessionFile,
                "--input-file", createDraftPath).ConfigureAwait(false);
            Require(created.ExitCode == 0 &&
                    created.StandardOutput.Contains("PROPOSAL id=prop-", StringComparison.Ordinal) &&
                    created.StandardOutput.Contains("kind=create", StringComparison.Ordinal) &&
                    created.StandardOutput.Contains($"destination={S4T5Inbox}", StringComparison.Ordinal) &&
                    created.StandardOutput.Contains("executor=agent", StringComparison.Ordinal) &&
                    created.StandardOutput.Contains("evidence=unverified", StringComparison.Ordinal) &&
                    created.StandardOutput.Contains("userConfirmedClaim=false", StringComparison.Ordinal) &&
                    created.StandardOutput.Contains($"reference id={S4T5Source} revision=1", StringComparison.Ordinal) &&
                    !created.StandardOutput.Contains("mb1_", StringComparison.Ordinal),
                "The agent create proposal was not stored as pending (exit=" + created.ExitCode + "; stdout=" + created.StandardOutput.Trim() + "; stderr=" + created.StandardError.Trim() + ").");
            var createId = S4T5ProposalId(created.StandardOutput);
            Require(!string.IsNullOrEmpty(createId) && createId.StartsWith("prop-", StringComparison.Ordinal),
                "The stored create proposal did not mint a prop- identity.");
            Console.WriteLine("PASS agent proposes a created lesson with an approved source reference (pending only)");

            // Repeated operation: the same key and content resolve to the one
            // stable proposal (same identity, same revision), never a second.
            var replay = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", proposeSession.SessionFile,
                "--input-file", createDraftPath).ConfigureAwait(false);
            Require(replay.ExitCode == 0 && replay.StandardOutput.Contains($"PROPOSAL id={createId}", StringComparison.Ordinal) &&
                    replay.StandardOutput.Contains("revision=1", StringComparison.Ordinal),
                "The repeated create operation did not resolve to the same proposal (exit=" + replay.ExitCode + "; stdout=" + replay.StandardOutput.Trim() + "; stderr=" + replay.StandardError.Trim() + ").");
            var listed = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "proposal-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(listed.ExitCode == 0 && listed.StandardOutput.Contains($"PROPOSAL id={createId} revision=1", StringComparison.Ordinal) &&
                    CountLines(listed.StandardOutput, "PROPOSAL id=") == 1,
                "The repeated create operation produced a duplicate proposal (stdout=" + listed.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS repeated operation yields one stable proposal (idempotent)");

            // Conflicting reuse: the same key with different content is an
            // error that publishes nothing and mutates nothing.
            var conflictDraftPath = await WriteS4T5ProposalDraftAsync(fixture, "s4t5-create-conflict",
                S4T5ProposalDraftJson("create", "s4t5-key-create-1", null, null,
                    S4T5ItemDraftJson("Synthetic S4-T5 conflicting lesson",
                        "Synthetic S4-T5 body conflicting: the same idempotency key reused for different content; never a real owner secret.",
                        now, new[] { S4T5SourceReference() }))).ConfigureAwait(false);
            var conflict = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", proposeSession.SessionFile,
                "--input-file", conflictDraftPath).ConfigureAwait(false);
            Require(conflict.ExitCode == 3 && conflict.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !conflict.StandardOutput.Contains("PROPOSAL id=", StringComparison.Ordinal),
                "A conflicting key reuse was accepted (exit=" + conflict.ExitCode + "; stdout=" + conflict.StandardOutput.Trim() + "; stderr=" + conflict.StandardError.Trim() + ").");
            var afterConflict = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "proposal-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", createId).ConfigureAwait(false);
            Require(afterConflict.ExitCode == 0 && afterConflict.StandardOutput.Contains("the return-to-project lesson the synthetic task produced", StringComparison.Ordinal) &&
                    !afterConflict.StandardOutput.Contains("conflicting lesson", StringComparison.Ordinal) &&
                    afterConflict.StandardOutput.Contains($"PROPOSAL id={createId}", StringComparison.Ordinal) &&
                    afterConflict.StandardOutput.Contains("revision=1", StringComparison.Ordinal),
                "A conflicting key reuse mutated the stored proposal (stdout=" + afterConflict.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS conflicting key reuse errors without publishing or mutating");

            // Revise proposal: the subject item at the exact live revision.
            var reviseDraftPath = await WriteS4T5ProposalDraftAsync(fixture, "s4t5-revise",
                S4T5ProposalDraftJson("revise", "s4t5-key-revise-1", S4T5ItemAlpha, 1L,
                    S4T5ItemDraftJson("Synthetic S4-T5 alpha lesson revision two",
                        "Synthetic S4-T5 body alpha revision two: the proposed revision of the playbook; never a real owner secret.",
                        now, new[] { S4T5SourceReference() }))).ConfigureAwait(false);
            var revised = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", proposeSession.SessionFile,
                "--input-file", reviseDraftPath).ConfigureAwait(false);
            Require(revised.ExitCode == 0 && revised.StandardOutput.Contains("kind=revise", StringComparison.Ordinal) &&
                    revised.StandardOutput.Contains($"subject={S4T5ItemAlpha} subjectRevision=1", StringComparison.Ordinal) &&
                    revised.StandardOutput.Contains("executor=agent", StringComparison.Ordinal),
                "The agent revise proposal was not stored as pending (exit=" + revised.ExitCode + "; stdout=" + revised.StandardOutput.Trim() + "; stderr=" + revised.StandardError.Trim() + ").");
            var reviseId = S4T5ProposalId(revised.StandardOutput);
            Console.WriteLine("PASS agent proposes a revision at the exact subject revision precondition");

            // Stale revision: a precondition the vault does not hold denies,
            // and the subject stays exactly as it was (revision and body).
            var staleDraftPath = await WriteS4T5ProposalDraftAsync(fixture, "s4t5-revise-stale",
                S4T5ProposalDraftJson("revise", "s4t5-key-revise-stale", S4T5ItemAlpha, 999L,
                    S4T5ItemDraftJson("Synthetic S4-T5 alpha stale revision",
                        "Synthetic S4-T5 body stale: a precondition the vault does not hold; never a real owner secret.",
                        now, null))).ConfigureAwait(false);
            var stale = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", proposeSession.SessionFile,
                "--input-file", staleDraftPath).ConfigureAwait(false);
            Require(stale.ExitCode == 3 && stale.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !stale.StandardOutput.Contains("PROPOSAL id=", StringComparison.Ordinal),
                "A stale revision precondition was accepted (exit=" + stale.ExitCode + "; stdout=" + stale.StandardOutput.Trim() + ").");
            var subjectAfterStale = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S4T5ItemAlpha).ConfigureAwait(false);
            Require(subjectAfterStale.ExitCode == 0 &&
                    subjectAfterStale.StandardOutput.Contains($"MEMORY id={S4T5ItemAlpha}", StringComparison.Ordinal) &&
                    subjectAfterStale.StandardOutput.Contains("revision=1", StringComparison.Ordinal) &&
                    subjectAfterStale.StandardOutput.Contains(S4T5AlphaBody, StringComparison.Ordinal),
                "A rejected stale proposal mutated the subject item (stdout=" + subjectAfterStale.StandardOutput.Trim() + ").");
            var listedAfterStale = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "proposal-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(CountLines(listedAfterStale.StandardOutput, "PROPOSAL id=") == 2,
                "A rejected stale proposal wrote a record (stdout=" + listedAfterStale.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS stale revision rejects without publishing or mutating");

            // Authorization boundaries: a read-only token cannot write; the
            // approved destination must be the one the session holds; a
            // reference outside the approved scope (or guessed) is the same
            // generic denial.
            var readOnlyPropose = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", readOnlySession.SessionFile,
                "--input-file", createDraftPath).ConfigureAwait(false);
            Require(readOnlyPropose.ExitCode == 3 && readOnlyPropose.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !readOnlyPropose.StandardOutput.Contains("PROPOSAL id=", StringComparison.Ordinal),
                "A read-only token wrote a proposal (exit=" + readOnlyPropose.ExitCode + "; stderr=" + readOnlyPropose.StandardError.Trim() + ").");
            var wrongDestinationPropose = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", wrongDestinationSession.SessionFile,
                "--input-file", createDraftPath).ConfigureAwait(false);
            Require(wrongDestinationPropose.ExitCode == 3 && wrongDestinationPropose.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "A session wrote to an inbox destination it was not granted (exit=" + wrongDestinationPropose.ExitCode + ").");
            var outsideReferenceDraftPath = await WriteS4T5ProposalDraftAsync(fixture, "s4t5-create-outside-ref",
                S4T5ProposalDraftJson("create", "s4t5-key-outside-ref", null, null,
                    S4T5ItemDraftJson("Synthetic S4-T5 outside reference lesson",
                        "Synthetic S4-T5 body outside reference: cites an item the grant never approved; never a real owner secret.",
                        now, new[] { S4T5SourceReference(S4T5ItemOutside) }))).ConfigureAwait(false);
            var outsideReference = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", proposeSession.SessionFile,
                "--input-file", outsideReferenceDraftPath).ConfigureAwait(false);
            Require(outsideReference.ExitCode == 3 && outsideReference.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !outsideReference.StandardOutput.Contains(S4T5ItemOutside, StringComparison.Ordinal),
                "A proposal cited a reference outside the approved scope (exit=" + outsideReference.ExitCode + ").");
            var guessedSubjectDraftPath = await WriteS4T5ProposalDraftAsync(fixture, "s4t5-revise-guessed",
                S4T5ProposalDraftJson("revise", "s4t5-key-guessed-subject", "s4t5-guessed-item", 1L,
                    S4T5ItemDraftJson("Synthetic S4-T5 guessed subject",
                        "Synthetic S4-T5 body guessed subject: a revision target the grant never approved; never a real owner secret.",
                        now, null))).ConfigureAwait(false);
            var guessedSubject = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", proposeSession.SessionFile,
                "--input-file", guessedSubjectDraftPath).ConfigureAwait(false);
            Require(guessedSubject.ExitCode == 3 && guessedSubject.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "A revise proposal named a guessed subject (exit=" + guessedSubject.ExitCode + ").");
            var guessedSourceDraftPath = await WriteS4T5ProposalDraftAsync(fixture, "s4t5-create-guessed-ref",
                S4T5ProposalDraftJson("create", "s4t5-key-guessed-ref", null, null,
                    S4T5ItemDraftJson("Synthetic S4-T5 guessed reference lesson",
                        "Synthetic S4-T5 body guessed reference: cites a source the grant never approved; never a real owner secret.",
                        now, new[] { S4T5SourceReference("s4t5-guessed-source") }))).ConfigureAwait(false);
            var guessedSource = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", proposeSession.SessionFile,
                "--input-file", guessedSourceDraftPath).ConfigureAwait(false);
            Require(guessedSource.ExitCode == 3 && guessedSource.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "A proposal cited a guessed source (exit=" + guessedSource.ExitCode + ").");
            var listedAfterDenials = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "proposal-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(CountLines(listedAfterDenials.StandardOutput, "PROPOSAL id=") == 2,
                "A denied proposal wrote a record (stdout=" + listedAfterDenials.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS read-only, denied destination, out-of-scope and guessed references all deny without a record");

            // Cross-grant isolation: the same key under a second, distinct
            // owner authorization can neither read back nor overwrite the
            // first grant's proposal; it records its own. The cross grant
            // also proves the revise subject must sit inside its own scope.
            var crossGrantPropose = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", crossGrantSession.SessionFile,
                "--input-file", createDraftPath).ConfigureAwait(false);
            Require(crossGrantPropose.ExitCode == 0 && crossGrantPropose.StandardOutput.Contains("PROPOSAL id=prop-", StringComparison.Ordinal),
                "The second grant could not record its own proposal (exit=" + crossGrantPropose.ExitCode + "; stderr=" + crossGrantPropose.StandardError.Trim() + ").");
            var crossGrantId = S4T5ProposalId(crossGrantPropose.StandardOutput);
            Require(!string.IsNullOrEmpty(crossGrantId) && !string.Equals(crossGrantId, createId, StringComparison.Ordinal),
                "The same idempotency key resolved across two distinct grants (stdout=" + crossGrantPropose.StandardOutput.Trim() + ").");
            var firstStillStable = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "proposal-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", createId).ConfigureAwait(false);
            Require(firstStillStable.ExitCode == 0 && firstStillStable.StandardOutput.Contains($"PROPOSAL id={createId}", StringComparison.Ordinal) &&
                    firstStillStable.StandardOutput.Contains("revision=1", StringComparison.Ordinal),
                "A second grant's reuse of the key disturbed the first proposal (stdout=" + firstStillStable.StandardOutput.Trim() + ").");
            var crossGrantRevise = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", crossGrantSession.SessionFile,
                "--input-file", reviseDraftPath).ConfigureAwait(false);
            Require(crossGrantRevise.ExitCode == 3 && crossGrantRevise.StandardError.Contains("resource_unavailable", StringComparison.Ordinal),
                "A revise subject outside the granting scope was accepted (exit=" + crossGrantRevise.ExitCode + ").");
            Console.WriteLine("PASS idempotency identity stays inside one owner authorization (no cross-grant read or overwrite)");

            // Fake user confirmation: belief-grade evidence is refused
            // outright, and the claim flag is stored as a claim only — the
            // proposal stays an AI, unconfirmed record.
            var confirmedEvidenceDraftPath = await WriteS4T5ProposalDraftAsync(fixture, "s4t5-create-confirmed-evidence",
                S4T5ProposalDraftJson("create", "s4t5-key-confirmed-evidence", null, null,
                    S4T5ItemDraftJson("Synthetic S4-T5 claimed confirmed lesson",
                        "Synthetic S4-T5 body claimed confirmed: a payload asserting user-confirmed evidence; never a real owner secret.",
                        now, null, "lesson", "user", "userConfirmed"))).ConfigureAwait(false);
            var confirmedEvidence = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", proposeSession.SessionFile,
                "--input-file", confirmedEvidenceDraftPath).ConfigureAwait(false);
            Require(confirmedEvidence.ExitCode == 3 && confirmedEvidence.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !confirmedEvidence.StandardOutput.Contains("PROPOSAL id=", StringComparison.Ordinal),
                "An agent proposal asserted user-confirmed evidence (exit=" + confirmedEvidence.ExitCode + ").");
            var claimedDraftPath = await WriteS4T5ProposalDraftAsync(fixture, "s4t5-create-claimed",
                S4T5ProposalDraftJson("create", "s4t5-key-claimed-confirmed", null, null,
                    S4T5ItemDraftJson("Synthetic S4-T5 claimed confirmed lesson",
                        "Synthetic S4-T5 body claimed confirmed: the payload claims the user confirmed; never a real owner secret.",
                        now, null),
                    userConfirmed: true)).ConfigureAwait(false);
            var claimed = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", proposeSession.SessionFile,
                "--input-file", claimedDraftPath).ConfigureAwait(false);
            Require(claimed.ExitCode == 0 && claimed.StandardOutput.Contains("userConfirmedClaim=true", StringComparison.Ordinal) &&
                    claimed.StandardOutput.Contains("evidence=unverified", StringComparison.Ordinal) &&
                    claimed.StandardOutput.Contains("executor=agent", StringComparison.Ordinal),
                "The claimed confirmation was not stored as an unconfirmed AI record (exit=" + claimed.ExitCode + "; stdout=" + claimed.StandardOutput.Trim() + "; stderr=" + claimed.StandardError.Trim() + ").");
            Console.WriteLine("PASS fake user confirmation stays a claim: evidence unverified, executor agent");

            // MCP bridge: the propose tool is advertised and honors the same
            // authorization boundaries.
            var bridgeList = await RunBridgeToolsListAsync(connectionsExecutable, fixture.AgentPipe).ConfigureAwait(false);
            Require(bridgeList.Contains("metabrain_propose", StringComparison.Ordinal),
                "The MCP bridge did not advertise metabrain_propose.");
            var bridgeDraft = S4T5ProposalDraftJson("create", "s4t5-key-bridge-1", null, null,
                S4T5ItemDraftJson("Synthetic S4-T5 bridge proposed lesson",
                    "Synthetic S4-T5 body bridge proposed: proposed through the MCP bridge; never a real owner secret.",
                    now, new[] { S4T5SourceReference() }));
            var bridgePropose = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_propose", new { sessionFile = proposeSession.SessionFile, draft = bridgeDraft }).ConfigureAwait(false);
            Require(bridgePropose.Contains("PROPOSAL id=prop-", StringComparison.Ordinal) &&
                    bridgePropose.Contains("kind=create", StringComparison.Ordinal) &&
                    !bridgePropose.Contains("mb1_", StringComparison.Ordinal),
                "The MCP bridge propose did not store the pending proposal (stdout=" + bridgePropose.Trim() + ").");
            var bridgeDenied = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_propose", new { sessionFile = readOnlySession.SessionFile, draft = bridgeDraft }).ConfigureAwait(false);
            Require(bridgeDenied.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !bridgeDenied.Contains("PROPOSAL id=", StringComparison.Ordinal),
                "The MCP bridge let a read-only session write a proposal (stdout=" + bridgeDenied.Trim() + ").");
            Console.WriteLine("PASS MCP bridge propose stores pending proposals and denies read-only sessions");

            // Revoke and lock: the next proposal attempt denies; nothing is
            // written after either boundary.
            var revokeSession = await IssueS4T5GrantAsync(connectionsExecutable, fixture, "s4t5-revoke", "proposal.create",
                new[] { S4T5Source }, new[] { S4T5Inbox }).ConfigureAwait(false);
            Require(!string.IsNullOrEmpty(revokeSession.SessionId), "Redemption did not report the proposal session ID.");
            var revoke = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "revoke-session", "--control-pipe", fixture.ControlPipe,
                "--session-id", revokeSession.SessionId).ConfigureAwait(false);
            Require(revoke.ExitCode == 0, "The owner could not revoke the S4-T5 proposal session.");
            var revokedPropose = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", revokeSession.SessionFile,
                "--input-file", createDraftPath).ConfigureAwait(false);
            Require(revokedPropose.ExitCode == 3 && revokedPropose.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !revokedPropose.StandardOutput.Contains("PROPOSAL id=", StringComparison.Ordinal),
                "A revoked session still wrote a proposal (exit=" + revokedPropose.ExitCode + ").");
            var lockSession = await IssueS4T5GrantAsync(connectionsExecutable, fixture, "s4t5-lock", "proposal.create",
                new[] { S4T5Source }, new[] { S4T5Inbox }).ConfigureAwait(false);
            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The owner could not lock after the S4-T5 walkthrough.");
            var lockedPropose = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", lockSession.SessionFile,
                "--input-file", createDraftPath).ConfigureAwait(false);
            Require(lockedPropose.ExitCode == 3 && !lockedPropose.StandardOutput.Contains("PROPOSAL id=", StringComparison.Ordinal),
                "A locked vault wrote a proposal (exit=" + lockedPropose.ExitCode + ").");
            Console.WriteLine("PASS revoke and lock deny the next proposal without writing");

            // Persistence across restart: the canonical proposals survive
            // locked/restart, the owner reads the exact pending proposal with
            // its provenance, and the durable record set is unchanged.
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadS4T5StatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false)).Contains("vault=locked", StringComparison.Ordinal),
                "The restarted service did not return locked.");
            var lockedProposeAfterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", lockSession.SessionFile,
                "--input-file", createDraftPath).ConfigureAwait(false);
            Require(lockedProposeAfterRestart.ExitCode == 3,
                "A proposal was written while the restarted vault was locked (exit=" + lockedProposeAfterRestart.ExitCode + ").");
            var reunlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(reunlock.ExitCode == 0 && reunlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The passphrase did not unlock the restarted vault.");
            var ownerRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "proposal-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", createId).ConfigureAwait(false);
            Require(ownerRead.ExitCode == 0 &&
                    ownerRead.StandardOutput.Contains($"PROPOSAL id={createId}", StringComparison.Ordinal) &&
                    ownerRead.StandardOutput.Contains("revision=1", StringComparison.Ordinal) &&
                    ownerRead.StandardOutput.Contains("kind=create", StringComparison.Ordinal) &&
                    ownerRead.StandardOutput.Contains($"destination={S4T5Inbox}", StringComparison.Ordinal) &&
                    ownerRead.StandardOutput.Contains("executor=agent", StringComparison.Ordinal) &&
                    ownerRead.StandardOutput.Contains("evidence=unverified", StringComparison.Ordinal) &&
                    ownerRead.StandardOutput.Contains($"reference id={S4T5Source} revision=1", StringComparison.Ordinal) &&
                    ownerRead.StandardOutput.Contains("the return-to-project lesson the synthetic task produced", StringComparison.Ordinal) &&
                    ownerRead.StandardOutput.Contains("idempotencyKey=s4t5-key-create-1", StringComparison.Ordinal),
                "The stored proposal did not survive restart/unlock with its provenance (exit=" + ownerRead.ExitCode + "; stdout=" + ownerRead.StandardOutput.Trim() + "; stderr=" + ownerRead.StandardError.Trim() + ").");
            var reviseRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "proposal-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", reviseId).ConfigureAwait(false);
            Require(reviseRead.ExitCode == 0 &&
                    reviseRead.StandardOutput.Contains("kind=revise", StringComparison.Ordinal) &&
                    reviseRead.StandardOutput.Contains($"subject={S4T5ItemAlpha} subjectRevision=1", StringComparison.Ordinal) &&
                    reviseRead.StandardOutput.Contains("executor=agent", StringComparison.Ordinal),
                "The stored revise proposal did not survive restart with its precondition (exit=" + reviseRead.ExitCode + "; stdout=" + reviseRead.StandardOutput.Trim() + ").");
            var listedAfterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "proposal-list", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(listedAfterRestart.ExitCode == 0 &&
                    listedAfterRestart.StandardOutput.Contains($"PROPOSAL id={createId}", StringComparison.Ordinal) &&
                    listedAfterRestart.StandardOutput.Contains($"PROPOSAL id={reviseId}", StringComparison.Ordinal) &&
                    CountLines(listedAfterRestart.StandardOutput, "PROPOSAL id=") == 5,
                "The durable proposal set changed across restart (stdout=" + listedAfterRestart.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS proposals persist across restart; owner reads exact pending content with provenance");

            // Nothing published, nothing mutated: the subject item still
            // renders exactly as before every proposal, and the rebuild
            // materializes the pending proposals as their own record kind.
            var finalSubject = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S4T5ItemAlpha).ConfigureAwait(false);
            Require(finalSubject.ExitCode == 0 &&
                    finalSubject.StandardOutput.Contains($"MEMORY id={S4T5ItemAlpha}", StringComparison.Ordinal) &&
                    finalSubject.StandardOutput.Contains("revision=1", StringComparison.Ordinal) &&
                    finalSubject.StandardOutput.Contains(S4T5AlphaBody, StringComparison.Ordinal) &&
                    finalSubject.StandardOutput.Contains("evidence=unverified", StringComparison.Ordinal),
                "A proposal mutated or published the subject item (stdout=" + finalSubject.StandardOutput.Trim() + ").");
            var finalRebuild = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(finalRebuild.ExitCode == 0 && finalRebuild.StandardOutput.Contains("INDEX rebuilt schema=2", StringComparison.Ordinal) &&
                    finalRebuild.StandardOutput.Contains("kind=proposal", StringComparison.Ordinal) &&
                    finalRebuild.StandardOutput.Contains($"INDEX ROW id={createId}", StringComparison.Ordinal),
                "The rebuild did not materialize the pending proposal records (exit=" + finalRebuild.ExitCode + "; stdout=" + finalRebuild.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS proposals never publish or mutate canonical content; index materializes pending proposals");

            Console.WriteLine("PASS S4-T5 agent proposal write interface walkthrough");
            Console.WriteLine("PASS fixture processes, profiles, and protected files were cleaned up");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static int CountLines(string output, string marker)
    {
        var count = 0;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains(marker, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static async Task<string> ReadS4T5StatusAsync(string executable, string pipe)
    {
        var result = await RunOwnerCommandAsync(executable, "owner", "status", "--control-pipe", pipe).ConfigureAwait(false);
        Require(result.ExitCode == 0, "The actual owner CLI status request failed.");
        return result.StandardOutput;
    }

    private static async Task WriteS4T5ResourceAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string zoneId,
        string draftJson,
        string command,
        string createCommand,
        string marker)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s4t5-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            command, createCommand, "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"{marker} stored revision=1", StringComparison.Ordinal),
            $"S4-T5 create did not store {resourceId} at revision 1 (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
        File.Delete(input);
    }

    private static async Task<string> WriteS4T5ProposalDraftAsync(SmokeFixture fixture, string name, string draftJson)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s4t5-proposal-" + name + "-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return input;
    }

    private static string S4T5ProposalId(string output)
    {
        const string prefix = "PROPOSAL id=";
        var start = output.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += prefix.Length;
        var end = output.IndexOfAny([' ', '\r', '\n'], start);
        return (end < 0 ? output[start..] : output[start..end]).Trim();
    }

    private sealed record S4T5Session(string SessionId, string SessionFile);

    private static Task<S4T5Session> IssueS4T5GrantAsync(
        string executable,
        SmokeFixture fixture,
        string name,
        string operations,
        string[] resourceIds,
        string[] destinationIds) =>
        IssueOwnerGrantAsync(executable, fixture.ControlPipe, fixture.AgentPipe, fixture.OutputDirectory,
            name, operations, resourceIds, destinationIds);

    /// <summary>
    /// The same owner-approved grant and one-time redemption, addressed by the
    /// endpoints and output directory directly. The S5-T4 console fixture writer
    /// drives an already-created fixture root that it did not create, so it
    /// cannot hold a <see cref="SmokeFixture"/> and passes the endpoints it read
    /// from that root's settings instead.
    /// </summary>
    private static async Task<S4T5Session> IssueOwnerGrantAsync(
        string executable,
        string controlPipe,
        string agentPipe,
        string outputDirectory,
        string name,
        string operations,
        string[] resourceIds,
        string[] destinationIds)
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O", CultureInfo.InvariantCulture);
        var handoff = Path.Combine(outputDirectory, name + ".grant-handoff");
        var sessionFile = Path.Combine(outputDirectory, name + ".session-handoff");
        var args = new List<string>
        {
            "owner", "grant", "--control-pipe", controlPipe,
            "--resource-ids", string.Join(",", resourceIds),
            "--operations", operations,
            "--expires-at-utc", expiry,
            "--handoff-file", handoff,
        };
        if (destinationIds.Length > 0)
        {
            args.Add("--destination-resource-ids");
            args.Add(string.Join(",", destinationIds));
        }

        var grant = await RunOwnerCommandWithInputAsync(executable, "ISSUE" + Environment.NewLine, args.ToArray()).ConfigureAwait(false);
        Require(grant.ExitCode == 0,
            $"The owner could not issue the synthetic S4-T5 grant {name} (exit={grant.ExitCode}; stdout={grant.StandardOutput.Trim()}; stderr={grant.StandardError.Trim()}).");
        VerifyOwnerHandoffAcl(handoff);
        var redeem = await RunOwnerCommandAsync(executable,
            "owner", "redeem", "--agent-pipe", agentPipe,
            "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
        Require(redeem.ExitCode == 0 && redeem.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
            $"Redemption failed for the synthetic S4-T5 grant {name} (exit={redeem.ExitCode}; stderr={redeem.StandardError.Trim()}).");
        VerifyOwnerHandoffAcl(sessionFile);
        return new S4T5Session(ParseSessionId(redeem.StandardOutput), sessionFile);
    }
}
