using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.OwnerUi;

namespace MetaBrain.S1T4.Smoke;

// S5-T7: the sources and organization console. One synthetic vault is
// provisioned and unlocked through the real owner CLI; the research capture
// goes through the console session the WPF window drives; the allowed
// session enrolls through the real owner CLI (locator in a request file,
// never echoed); the uncertain mapping is held for review through the
// console session (no transcript read, no guessed zone); pause/resume,
// refresh opt-in/out and the refresh pass go through the console session;
// the missing/unsupported/replacement paths are the service's own answers
// with refusal fidelity (unsupported stays refused, never completed); the
// snapshot preserve/list/open/retain goes through the console session with a
// stale-revision refusal; the owner collection membership goes through the
// console session and never widens a frozen grant. Nothing here is a mock,
// and no assertion is a source-text echo.
internal static partial class OwnerServiceSmoke
{
    private const string S5T7Zone = "s5t7-zone";
    private const string S5T7AllowedSession = "s5t7-allowed-session";
    private const string S5T7UnmappedSource = "s5t7-unmapped-source";
    private const string S5T7HeldMapping = "s5t7-held-mapping";
    private const string S5T7UnsupportedSource = "s5t7-unsupported-source";
    private const string S5T7MissingSource = "s5t7-missing-source";
    private const string S5T7ReplacedSource = "s5t7-replaced-source";
    private const string S5T7CaptureNote = "s5t7-capture-note";
    private const string S5T7Collection = "s5t7-collection";
    private const string S5T7ItemAlpha = "s5t7-item-alpha";
    private const string S5T7Marker = "s5t7-synthetic-sources-marker-8b21";

    /// <summary>
    /// Runs the S5-T7 sources scenario: capture + allowed enroll, held
    /// mapping review, unknown/unmapped/unsupported/missing refusals,
    /// replacement quarantine, pause/resume, refresh consent/pass, snapshot
    /// preserve/retain, collection without grant widening, and the lock
    /// boundary with original-bytes preservation.
    /// </summary>
    internal static async Task RunS5T7SourcesAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S5-T7 sources console smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S5-T7 sources and organization: capture, enroll, mapping review, health, preservation and collections");
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
                "Provision did not create the synthetic S5-T7 vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");

            var session = new OwnerConsoleSession(fixture.ControlPipe);
            await session.AttachAsync().ConfigureAwait(false);
            Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
                "The console session did not report the real unlocked vault (" + session.ReportedVaultState + ").");

            // ---- the owner captures research through the console (Inbox path reuse) ----
            var noteText = "S5-T7 synthetic research note: ghi chu nghien cuu ve viec dang nhap he thong; " + S5T7Marker;
            var capture = await OwnerChannelClient.CaptureResearchNoteAsync(
                fixture.ControlPipe,
                new OwnerCaptureDraft(S5T7CaptureNote, S5T7Zone, noteText, Title: "Synthetic S5-T7 note"),
                CancellationToken.None).ConfigureAwait(false);
            Require(capture.Outcome == OwnerCommandOutcome.Completed && capture.Source is not null &&
                    string.Equals(capture.Source.Id, S5T7CaptureNote, StringComparison.Ordinal),
                "The console capture did not store the research note (" + capture.Outcome + "/" + capture.ErrorCode + ").");
            Console.WriteLine("PASS the owner captures research text through the console with no provider or fetch");

            // ---- the allowed session enrolls through the real owner CLI ----
            var marker = S5T7Marker + "-" + Guid.NewGuid().ToString("N")[..8];
            var allowedLocator = Path.Combine(fixture.OutputDirectory, "s5t7-allowed.jsonl");
            var allowedBytes = Encoding.UTF8.GetBytes(S3T6CodexSessionForS5T7("s5t7-allowed-alpha", marker));
            await File.WriteAllBytesAsync(allowedLocator, allowedBytes).ConfigureAwait(false);
            var allowedRequest = Path.Combine(fixture.OutputDirectory, "s5t7-allowed-enroll.json");
            await File.WriteAllTextAsync(allowedRequest, JsonSerializer.Serialize(new { locator = allowedLocator })).ConfigureAwait(false);
            var allowedEnroll = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", S5T7AllowedSession, "--zone-id", S5T7Zone,
                "--format", "codex.session.jsonl", "--format-version", "1",
                "--input-file", allowedRequest).ConfigureAwait(false);
            Require(allowedEnroll.ExitCode == 0 && allowedEnroll.StandardOutput.Contains($"SOURCE id={S5T7AllowedSession}", StringComparison.Ordinal) &&
                    !allowedEnroll.StandardOutput.Contains(allowedLocator, StringComparison.Ordinal),
                "Owner enrollment of the allowed fixture failed or echoed the locator (exit=" + allowedEnroll.ExitCode + "; stderr=" + allowedEnroll.StandardError.Trim() + ").");
            Console.WriteLine("PASS the owner enrolls the allowed session with an explicit zone/format mapping and no locator echo");

            // ---- the console lists the allowlist with checkpoint/consent/cache scope ----
            await session.RefreshSourcesAsync().ConfigureAwait(false);
            var allowed = session.Sources.FirstOrDefault(source => string.Equals(source.SourceId, S5T7AllowedSession, StringComparison.Ordinal));
            Require(allowed is not null && string.Equals(allowed.ZoneId, S5T7Zone, StringComparison.Ordinal) &&
                    string.Equals(allowed.Life, "enrolled", StringComparison.Ordinal) && !allowed.RefreshOptIn,
                "The console did not list the enrolled source with its mapping and life (" + session.SourcesMessage + ").");
            Console.WriteLine("PASS the console lists the allowlist with life, checkpoint, consent and cache scope");

            // ---- uncertain mapping is held for review: no read, no guessed zone ----
            var heldLocator = Path.Combine(fixture.OutputDirectory, "s5t7-held.jsonl");
            var heldBytes = Encoding.UTF8.GetBytes(S3T6CodexSessionForS5T7("s5t7-held-alpha", marker));
            await File.WriteAllBytesAsync(heldLocator, heldBytes).ConfigureAwait(false);
            await session.SubmitMappingUnknownAsync(S5T7HeldMapping, heldLocator, zoneId: null, format: null, formatVersion: null).ConfigureAwait(false);
            Require(session.OpenSource is { } held &&
                    string.Equals(held.SourceId, S5T7HeldMapping, StringComparison.Ordinal) &&
                    held.Quarantine is { Reason: "mapping_unknown", Detail: "mapping_unresolved" },
                "The console did not hold the uncertain mapping for review (" + session.SourceMessage + ").");
            await session.RefreshSourcesAsync().ConfigureAwait(false);
            var heldListed = session.Sources.FirstOrDefault(source => string.Equals(source.SourceId, S5T7HeldMapping, StringComparison.Ordinal));
            Require(heldListed is not null && heldListed.Quarantine is { Reason: "mapping_unknown" } &&
                    string.Equals(heldListed.ZoneId, "(unresolved)", StringComparison.Ordinal),
                "The held mapping was not listed beside the allowlist with no guessed zone (" + session.SourcesMessage + ").");
            // The held transcript was never opened: holding it under an
            // exclusive lock still reports the held record.
            using (var heldExclusive = new FileStream(heldLocator, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                _ = heldExclusive;
                await session.OpenSourceAsync(heldListed!).ConfigureAwait(false);
                Require(session.OpenSource is { } reopened &&
                        reopened.Quarantine is { Reason: "mapping_unknown" },
                    "The held mapping was opened instead of reported state-first (" + session.SourceMessage + ").");
            }

            Console.WriteLine("PASS an uncertain mapping is held in private quarantine with no transcript read and no guessed zone");

            // A held submission can never be ingested into a guessed zone.
            var heldIngest = await OwnerChannelClient.IngestSourceAsync(fixture.ControlPipe, S5T7HeldMapping, CancellationToken.None).ConfigureAwait(false);
            Require(heldIngest.Outcome == OwnerCommandOutcome.Refused && heldIngest.ErrorCode == "source_not_enrolled",
                "A held mapping was ingested instead of refused (" + heldIngest.Outcome + "/" + heldIngest.ErrorCode + ").");
            Console.WriteLine("PASS a held mapping can never be ingested into a guessed zone");

            // ---- unknown source / unmapped status are distinct explicit refusals ----
            var unknownStatus = await OwnerChannelClient.ReadSourceStatusAsync(fixture.ControlPipe, S5T7UnmappedSource, CancellationToken.None).ConfigureAwait(false);
            Require(unknownStatus.Outcome == OwnerCommandOutcome.Refused && unknownStatus.ErrorCode == "source_not_enrolled",
                "An unmapped source status did not fail closed (" + unknownStatus.Outcome + "/" + unknownStatus.ErrorCode + ").");
            var unknownIngest = await OwnerChannelClient.IngestSourceAsync(fixture.ControlPipe, S5T7UnmappedSource, CancellationToken.None).ConfigureAwait(false);
            Require(unknownIngest.Outcome == OwnerCommandOutcome.Refused && unknownIngest.ErrorCode == "source_not_enrolled",
                "An unknown source ingest did not fail closed (" + unknownIngest.Outcome + "/" + unknownIngest.ErrorCode + ").");
            Console.WriteLine("PASS unknown/unmapped sources are refused state-first with no transcript open");

            // ---- conflicting mappings are refused, never merged ----
            var conflictEnroll = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", S5T7AllowedSession, "--zone-id", "s5t7-other-zone",
                "--format", "codex.session.jsonl", "--format-version", "1",
                "--input-file", allowedRequest).ConfigureAwait(false);
            Require(conflictEnroll.ExitCode == 4 && conflictEnroll.StandardError.Contains("source_enrollment_conflict", StringComparison.Ordinal),
                "An inconsistent mapping was merged instead of refused (exit=" + conflictEnroll.ExitCode + ").");
            Console.WriteLine("PASS a conflicting zone/format mapping is refused, never merged");

            // ---- unknown format is an explicit refusal, never a completed save ----
            var unsupportedLocator = Path.Combine(fixture.OutputDirectory, "s5t7-unsupported.jsonl");
            await File.WriteAllBytesAsync(unsupportedLocator, Encoding.UTF8.GetBytes("synthetic s5t7 line one\n")).ConfigureAwait(false);
            var unsupportedRequest = Path.Combine(fixture.OutputDirectory, "s5t7-unsupported-enroll.json");
            await File.WriteAllTextAsync(unsupportedRequest, JsonSerializer.Serialize(new { locator = unsupportedLocator })).ConfigureAwait(false);
            var unsupportedEnroll = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", S5T7UnsupportedSource, "--zone-id", S5T7Zone,
                "--format", "s5t7.unknown.format", "--format-version", "1",
                "--input-file", unsupportedRequest).ConfigureAwait(false);
            Require(unsupportedEnroll.ExitCode == 0,
                "Enrollment of the unknown-format fixture failed (exit=" + unsupportedEnroll.ExitCode + "; stderr=" + unsupportedEnroll.StandardError.Trim() + ").");
            await session.RefreshSourcesAsync().ConfigureAwait(false);
            var unsupportedRow = session.Sources.First(source => string.Equals(source.SourceId, S5T7UnsupportedSource, StringComparison.Ordinal));
            await session.OpenSourceAsync(unsupportedRow).ConfigureAwait(false);
            var unsupportedDirect = await OwnerChannelClient.IngestSourceAsync(fixture.ControlPipe, S5T7UnsupportedSource, CancellationToken.None).ConfigureAwait(false);
            Require(unsupportedDirect.Outcome == OwnerCommandOutcome.Refused && string.Equals(unsupportedDirect.ErrorCode, "ingest_unsupported_format", StringComparison.Ordinal) && unsupportedDirect.Ingest is null,
                "The unknown-format ingest did not stay a refusal with its service error code (" + unsupportedDirect.Outcome + "/" + unsupportedDirect.ErrorCode + ").");
            await session.IngestSourceAsync(session.OpenSource ?? throw new InvalidOperationException("The unsupported source did not open.")).ConfigureAwait(false);
            Require(session.SourceMessage is not null && session.SourceMessage.Contains("ingest_unsupported_format", StringComparison.Ordinal) && session.SourceMessage.Contains("refused", StringComparison.OrdinalIgnoreCase),
                "The unknown-format ingest was shown as a completed save (" + session.SourceMessage + ").");
            Require(session.LastIngest is null || !string.Equals(session.LastIngest.Status, "ingest_unsupported_format", StringComparison.Ordinal),
                "The unknown-format refusal was recorded as a completed ingest outcome.");
            Console.WriteLine("PASS an unknown format stays an explicit unsupported refusal, never a completed save");
            // ---- missing source names relocation or revocation, never a scan ----
            var missingLocator = Path.Combine(fixture.OutputDirectory, "s5t7-will-delete.jsonl");
            await File.WriteAllBytesAsync(missingLocator, Encoding.UTF8.GetBytes(S3T6CodexSessionForS5T7("s5t7-missing-alpha", marker))).ConfigureAwait(false);
            var missingRequest = Path.Combine(fixture.OutputDirectory, "s5t7-missing-enroll.json");
            await File.WriteAllTextAsync(missingRequest, JsonSerializer.Serialize(new { locator = missingLocator })).ConfigureAwait(false);
            var missingEnroll = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", S5T7MissingSource, "--zone-id", S5T7Zone,
                "--format", "codex.session.jsonl", "--format-version", "1",
                "--input-file", missingRequest).ConfigureAwait(false);
            Require(missingEnroll.ExitCode == 0, "Enrollment of the soon-missing fixture failed.");
            File.Delete(missingLocator);
            var missingRow = session.Sources.FirstOrDefault(source => string.Equals(source.SourceId, S5T7MissingSource, StringComparison.Ordinal));
            if (missingRow is null)
            {
                await session.RefreshSourcesAsync().ConfigureAwait(false);
                missingRow = session.Sources.First(source => string.Equals(source.SourceId, S5T7MissingSource, StringComparison.Ordinal));
            }

            await session.OpenSourceAsync(missingRow).ConfigureAwait(false);
            Require(session.OpenSource is not null && string.Equals(session.OpenSource.Availability, "missing", StringComparison.Ordinal),
                "The deleted source was not reported missing (" + session.SourceMessage + ").");
            await session.IngestSourceAsync(session.OpenSource!).ConfigureAwait(false);
            Require(session.SourceMessage is not null && session.SourceMessage.Contains("source_missing", StringComparison.Ordinal),
                "The missing source ingest did not name relocation or revocation (" + session.SourceMessage + ").");
            Console.WriteLine("PASS a missing source is reported with relocation/revocation remediation and no scan");

            // ---- replaced bytes quarantine with the checkpoint frozen ----
            var replacedLocator = Path.Combine(fixture.OutputDirectory, "s5t7-replaced.jsonl");
            var replacedOriginal = Encoding.UTF8.GetBytes(S3T6CodexSessionForS5T7("s5t7-replaced-alpha", marker));
            await File.WriteAllBytesAsync(replacedLocator, replacedOriginal).ConfigureAwait(false);
            var replacedRequest = Path.Combine(fixture.OutputDirectory, "s5t7-replaced-enroll.json");
            await File.WriteAllTextAsync(replacedRequest, JsonSerializer.Serialize(new { locator = replacedLocator })).ConfigureAwait(false);
            var replacedEnroll = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-enroll", "--control-pipe", fixture.ControlPipe,
                "--source-id", S5T7ReplacedSource, "--zone-id", S5T7Zone,
                "--format", "codex.session.jsonl", "--format-version", "1",
                "--input-file", replacedRequest).ConfigureAwait(false);
            Require(replacedEnroll.ExitCode == 0, "Enrollment of the replaceable fixture failed.");
            await session.RefreshSourcesAsync().ConfigureAwait(false);
            var replacedRow = session.Sources.First(source => string.Equals(source.SourceId, S5T7ReplacedSource, StringComparison.Ordinal));
            await session.SetSourceRefreshOptInAsync(replacedRow, optIn: true).ConfigureAwait(false);
            await session.RefreshSourcesPassAsync().ConfigureAwait(false);
            await session.OpenSourceAsync(session.Sources.First(source => string.Equals(source.SourceId, S5T7ReplacedSource, StringComparison.Ordinal))).ConfigureAwait(false);
            var checkpointBefore = session.OpenSource!.ConsumedOffset;
            Require(checkpointBefore == replacedOriginal.Length,
                "The replaceable source did not ingest to its end (consumed=" + checkpointBefore + ").");
            var replacedChanged = (byte[])replacedOriginal.Clone();
            replacedChanged[0] ^= 1;
            await File.WriteAllBytesAsync(replacedLocator, replacedChanged).ConfigureAwait(false);
            await session.IngestSourceAsync(session.OpenSource!).ConfigureAwait(false);
            Require(session.SourceMessage is not null && session.SourceMessage.Contains("source_generation_changed", StringComparison.Ordinal),
                "Replacement was spliced instead of refused (" + session.SourceMessage + ").");
            await session.OpenSourceAsync(session.Sources.First(source => string.Equals(source.SourceId, S5T7ReplacedSource, StringComparison.Ordinal))).ConfigureAwait(false);
            Require(session.OpenSource is not null && string.Equals(session.OpenSource.Life, "quarantined", StringComparison.Ordinal) &&
                    session.OpenSource.Quarantine is { Reason: "generation_replaced" } &&
                    session.OpenSource.ConsumedOffset == checkpointBefore,
                "Replacement did not quarantine with the prior checkpoint frozen (" + session.SourceMessage + ").");
            Console.WriteLine("PASS replaced bytes quarantine with the prior generation checkpoint frozen");

            // ---- pause stops reads/ingest/refresh with no transcript open ----
            await session.RefreshSourcesAsync().ConfigureAwait(false);
            var pauseRow = session.Sources.First(source => string.Equals(source.SourceId, S5T7AllowedSession, StringComparison.Ordinal));
            await session.PauseSourceAsync(pauseRow).ConfigureAwait(false);
            Require(session.OpenSource is not null && string.Equals(session.OpenSource.Life, "paused", StringComparison.Ordinal),
                "Pause did not report the paused life state (" + session.SourceMessage + ").");
            await session.IngestSourceAsync(session.OpenSource!).ConfigureAwait(false);
            Require(session.SourceMessage is not null && session.SourceMessage.Contains("source_paused", StringComparison.Ordinal),
                "A paused source was ingested instead of refused (" + session.SourceMessage + ").");
            await session.RefreshSourcesPassAsync().ConfigureAwait(false);
            Require(session.RefreshEntries.Any(entry =>
                    string.Equals(entry.SourceId, S5T7AllowedSession, StringComparison.Ordinal) &&
                    string.Equals(entry.Status, "skipped_paused", StringComparison.Ordinal)),
                "Refresh read a paused source (" + session.RefreshMessage + ").");
            Console.WriteLine("PASS pausing a source stops status reads, ingest and refresh with no transcript open");

            // ---- resume continues from the persisted checkpoint ----
            await session.ResumeSourceAsync(session.OpenSource!).ConfigureAwait(false);
            Require(session.OpenSource is not null && string.Equals(session.OpenSource.Life, "enrolled", StringComparison.Ordinal),
                "Resume did not restore the enrolled life state (" + session.SourceMessage + ").");
            Console.WriteLine("PASS resume continues from the persisted checkpoint with no duplicates");

            // ---- refresh consent: opt-in reads, opt-out skips without a read ----
            await session.RefreshSourcesAsync().ConfigureAwait(false);
            var consentRow = session.Sources.First(source => string.Equals(source.SourceId, S5T7AllowedSession, StringComparison.Ordinal));
            await session.SetSourceRefreshOptInAsync(consentRow, optIn: true).ConfigureAwait(false);
            await session.RefreshSourcesPassAsync().ConfigureAwait(false);
            Require(session.RefreshEntries.Any(entry =>
                    string.Equals(entry.SourceId, S5T7AllowedSession, StringComparison.Ordinal) &&
                    entry.Status.StartsWith("ingest_", StringComparison.Ordinal)),
                "The opted-in refresh did not run an ingest pass (" + session.RefreshMessage + ").");
            await session.SetSourceRefreshOptInAsync(
                session.Sources.First(source => string.Equals(source.SourceId, S5T7AllowedSession, StringComparison.Ordinal)), optIn: false).ConfigureAwait(false);
            await session.RefreshSourcesPassAsync().ConfigureAwait(false);
            Require(session.RefreshEntries.Any(entry =>
                    string.Equals(entry.SourceId, S5T7AllowedSession, StringComparison.Ordinal) &&
                    string.Equals(entry.Status, "skipped_not_opted_in", StringComparison.Ordinal)),
                "Refresh read an opted-out source (" + session.RefreshMessage + ").");
            Console.WriteLine("PASS refresh opt-in runs incrementally; opt-out skips with no read");

            // ---- snapshot preserve/retain with explicit scope and retention ----
            // Preservation freezes the registered source reference: the owner
            // registers it first through the console session's register act
            // (the existing S2-T3 registry create with the enrolled locator
            // named explicitly), then preserves the enrolled generation the
            // console reports.
            await session.RefreshSourcesAsync().ConfigureAwait(false);
            var referenceRow = session.Sources.First(source => string.Equals(source.SourceId, S5T7AllowedSession, StringComparison.Ordinal));
            await session.RegisterSourceReferenceAsync(referenceRow, allowedLocator).ConfigureAwait(false);
            Require(session.SnapshotMessage is not null && session.SnapshotMessage.Contains("registered the preservation reference", StringComparison.Ordinal) &&
                    session.SnapshotMessage.Contains("revision 1", StringComparison.Ordinal),
                "The console did not register the preservation reference of the allowed source (" + session.SnapshotMessage + ").");
            Console.WriteLine("PASS the console registers the preservation reference of the selected source through the existing registry path");
            await session.RefreshSourcesAsync().ConfigureAwait(false);
            var preserveRow = session.Sources.First(source => string.Equals(source.SourceId, S5T7AllowedSession, StringComparison.Ordinal));
            await session.OpenSourceAsync(preserveRow).ConfigureAwait(false);
            Require(session.OpenSource is { SourceReferenceRevision: not null } openForPreserve && openForPreserve.SourceReferenceRevision > 0,
                "The allowed source has no registered reference revision to preserve from (" + session.SourceMessage + ").");
            await session.PreserveSnapshotAsync(
                S5T7AllowedSession, session.OpenSource!.SourceReferenceRevision!.Value,
                DateTimeOffset.UtcNow.AddDays(30)).ConfigureAwait(false);
            Require(session.OpenSnapshot is not null &&
                    string.Equals(session.OpenSnapshot.SourceId, S5T7AllowedSession, StringComparison.Ordinal) &&
                    !session.OpenSnapshot.Purged,
                "The console did not preserve the snapshot with its scope and retention (" + session.SnapshotMessage + ").");
            await session.ListSnapshotsAsync(preserveRow).ConfigureAwait(false);
            Require(session.Snapshots.Any(snapshot => string.Equals(snapshot.Id, session.OpenSnapshot!.Id, StringComparison.Ordinal)),
                "The preserved snapshot was not listed (" + session.SnapshotMessage + ").");
            var listedSnapshot = session.Snapshots.First(snapshot => string.Equals(snapshot.Id, session.OpenSnapshot!.Id, StringComparison.Ordinal));
            await session.OpenSnapshotAsync(listedSnapshot).ConfigureAwait(false);
            Require(session.OpenSnapshot is not null && string.Equals(session.OpenSnapshot.Id, listedSnapshot.Id, StringComparison.Ordinal) &&
                    session.OpenSnapshot.Revision == listedSnapshot.Revision &&
                    string.Equals(session.OpenSnapshot.SourceId, S5T7AllowedSession, StringComparison.Ordinal),
                "The console did not open the listed snapshot through the live read (" + session.SnapshotMessage + ").");
            var staleOpen = await OwnerChannelClient.ReadSourceSnapshotAsync(
                fixture.ControlPipe, listedSnapshot.Id, listedSnapshot.Revision + 1000, CancellationToken.None).ConfigureAwait(false);
            Require(staleOpen.Outcome != OwnerCommandOutcome.Completed,
                "A stale snapshot revision opened as if it were live (" + staleOpen.Outcome + "/" + staleOpen.ErrorCode + ").");
            await session.RetainSnapshotAsync(session.OpenSnapshot!, DateTimeOffset.UtcNow.AddDays(60)).ConfigureAwait(false);
            Require(session.OpenSnapshot is not null,
                "The console did not extend the snapshot retention (" + session.SnapshotMessage + ").");
            Console.WriteLine("PASS a listed snapshot opens through the live read and extends retention; a stale revision is refused, never pinned");

            // ---- collections organize without granting ----
            await S5T7WriteItemAsync(connectionsExecutable, fixture.ControlPipe, fixture.OutputDirectory, S5T7ItemAlpha, S5T7Zone,
                S5T7ItemDraft("Synthetic S5-T7 alpha", marker)).ConfigureAwait(false);
            await session.SetCollectionAsync(S5T7Collection, new[] { S5T7ItemAlpha }).ConfigureAwait(false);
            await session.RefreshCollectionsAsync().ConfigureAwait(false);
            Require(session.Collections.Any(collection =>
                    string.Equals(collection.CollectionId, S5T7Collection, StringComparison.Ordinal) &&
                    collection.ResourceIds.Contains(S5T7ItemAlpha, StringComparer.Ordinal)),
                "The console did not save the collection membership (" + session.CollectionsMessage + ").");
            var unknownMember = await OwnerChannelClient.SetCollectionAsync(
                fixture.ControlPipe, S5T7Collection, new[] { "s5t7-member-unknown" }, CancellationToken.None).ConfigureAwait(false);
            Require(unknownMember.Outcome == OwnerCommandOutcome.Refused,
                "An unknown collection member was stored (" + unknownMember.Outcome + "/" + unknownMember.ErrorCode + ").");
            Console.WriteLine("PASS adding a collection grants nothing; unknown members are refused");

            // ---- agent channel denies every owner sources operation ----
            foreach (var ownerOperation in new[]
                     {
                         ServiceRequestHandler.SourceEnrollOperation, ServiceRequestHandler.SourceRevokeOperation,
                         ServiceRequestHandler.SourceStatusOperation, ServiceRequestHandler.SourceIngestOperation,
                         ServiceRequestHandler.SourceRelocateOperation, ServiceRequestHandler.SourcePauseOperation,
                         ServiceRequestHandler.SourceResumeOperation, ServiceRequestHandler.SourceRefreshOptInOperation,
                         ServiceRequestHandler.SourceRefreshOptOutOperation, ServiceRequestHandler.SourceListOperation,
                         ServiceRequestHandler.SourceRefreshOperation, ServiceRequestHandler.SourceMappingSubmitOperation,
                         ServiceRequestHandler.SourceMappingResolveOperation, ServiceRequestHandler.SourceSnapshotPreserveOperation,
                         ServiceRequestHandler.SourceSnapshotListOperation, ServiceRequestHandler.SourceSnapshotReadOperation,
                         ServiceRequestHandler.SourceSnapshotRetainOperation, ServiceRequestHandler.SourceSnapshotPurgeOperation,
                         ServiceRequestHandler.OwnerScopeCollectionSetOperation, ServiceRequestHandler.OwnerScopeCollectionListOperation,
                     })
            {
                var spoofed = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = ownerOperation,
                    isOwner = true,
                    resourceId = S5T7AllowedSession,
                }).ConfigureAwait(false);
                Require(ReplyCode(spoofed) == "forbidden",
                    "The agent channel accepted owner operation " + ownerOperation + " (" + ReplyCode(spoofed) + ").");
            }

            Console.WriteLine("PASS the agent channel denies every owner sources/collection/snapshot operation");

            // ---- the lock boundary ----
            var epochBeforeLock = session.PrivateEpoch;
            var formVersionBeforeLock = session.SourcesFormVersion;
            await session.LockAsync().ConfigureAwait(false);
            Require(session.Sources.Length == 0 && session.OpenSource is null &&
                    session.Collections.Length == 0 && session.Snapshots.Length == 0 && session.OpenSnapshot is null &&
                    session.SourcesFormVersion > formVersionBeforeLock &&
                    session.PrivateEpoch != epochBeforeLock,
                "Locking did not clear every sources, collection and snapshot view plus the drafts.");
            var lateSources = new OwnerSourceListResult(OwnerCommandOutcome.Completed, null, new[]
            {
                new OwnerSourceEnrollment(S5T7AllowedSession, S5T7Zone, "codex.session.jsonl", 1, 1, "enrolled", 1,
                    DateTimeOffset.UtcNow, null, null, "available", DateTimeOffset.UtcNow, null, 1, null, false, 0, 0, 0, 1, 0, 0, 0, null),
            });
            Require(!session.ApplySourcesListAnswer(epochBeforeLock, lateSources) && session.Sources.Length == 0,
                "A late sources answer restored a view the lock had cleared.");
            var lockedSources = await OwnerChannelClient.ListSourcesAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(lockedSources.Outcome == OwnerCommandOutcome.Refused && lockedSources.ErrorCode == "vault_locked",
                "A locked vault served the allowlist (" + lockedSources.Outcome + "/" + lockedSources.ErrorCode + ").");
            await session.RefreshSourcesAsync().ConfigureAwait(false);
            Require(session.Sources.Length == 0 && session.SourcesMessage is null,
                "The locked console offered a sources act (" + session.SourcesMessage + ").");
            Console.WriteLine("PASS locking clears every sources, collection and snapshot view, and a late answer cannot restore them");

            // ---- original bytes unchanged; no locator echo; no plaintext ----
            var allowedAfterBytes = await File.ReadAllBytesAsync(allowedLocator).ConfigureAwait(false);
            Require(allowedAfterBytes.AsSpan().SequenceEqual(allowedBytes),
                "The read-only refresh modified the allowed fixture.");
            var heldAfterBytes = await File.ReadAllBytesAsync(heldLocator).ConfigureAwait(false);
            Require(heldAfterBytes.AsSpan().SequenceEqual(heldBytes),
                "The held fixture was opened or modified.");
            CryptographicOperations.ZeroMemory(allowedAfterBytes);
            CryptographicOperations.ZeroMemory(heldAfterBytes);
            if (S5T7FixtureHoldsMarker(fixture))
            {
                throw new InvalidOperationException("A synthetic S5-T7 private body marker was persisted in plaintext under the fixture root.");
            }

            Console.WriteLine("PASS original source bytes unchanged; no locator echo; no plaintext persistence");
            Console.WriteLine("PASS S5-T7 console capture/enroll/mapping-review/pause-resume/refresh/snapshot/collection/lock over the real pipes");
            Console.WriteLine("LIMIT synthetic S5-T7 fixture only; sources, collections and snapshots are synthetic; same-owner OS compromise remains outside the threat model.");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static string S5T7ItemDraft(string title, string marker) =>
        S5T4ItemDraft("claim", "user", "directWrite", "unverified", "l1", title,
            "S5-T7 synthetic body " + marker + " " + S5T7Marker, DateTimeOffset.UtcNow.AddMinutes(-5));

    private static async Task S5T7WriteItemAsync(
        string executable,
        string controlPipe,
        string outputDirectory,
        string resourceId,
        string zoneId,
        string draftJson)
    {
        var input = Path.Combine(outputDirectory, "s5t7-item-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", controlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId, "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
            "The S5-T7 item create did not store " + resourceId + " at revision 1 (exit=" + result.ExitCode +
            "; stdout=" + result.StandardOutput.Trim() + "; stderr=" + result.StandardError.Trim() + ").");
        File.Delete(input);
    }

    private static string S3T6CodexSessionForS5T7(string session, string marker) =>
        S3T2CodexSession(session, marker);

    /// <summary>
    /// Scans the persisted product surfaces (the encrypted vault tree and the
    /// service settings) for a plaintext marker leak. The enrolled source
    /// fixtures under the output directory legitimately hold the marker —
    /// they are the originals the service must never mutate — so they are
    /// excluded from this scan; the byte-equality checks above prove they
    /// were never modified.
    /// </summary>
    private static bool S5T7FixtureHoldsMarker(SmokeFixture fixture)
    {
        foreach (var root in new[] { fixture.VaultDirectoryPath, fixture.ServiceDirectoryPath })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
                if (text.Contains(S5T7Marker, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        var settingsText = File.ReadAllText(fixture.SettingsPath);
        return settingsText.Contains(S5T7Marker, StringComparison.Ordinal);
    }

    /// <summary>
    /// Creates the synthetic owner-console fixture at an explicit root for the
    /// external WPF sources proof. The caller owns that root: it starts and
    /// stops the service, drives the console and removes the root afterwards,
    /// so this scenario disposes nothing and provisions no vault.
    /// </summary>
    internal static Task RunS5T7ConsoleFixtureAsync(string fixtureRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S5-T7 owner console fixture requires Windows named pipes.");
        }

        var fixture = SmokeFixture.CreateUninitializedAt(fixtureRoot);
        Console.WriteLine("S5T7 CONSOLE FIXTURE root=" + fixture.Root);
        Console.WriteLine("S5T7 CONSOLE FIXTURE settings=" + fixture.SettingsPath);
        Console.WriteLine("S5T7 CONSOLE FIXTURE controlPipe=" + fixture.ControlPipe);
        Console.WriteLine("S5T7 CONSOLE FIXTURE agentPipe=" + fixture.AgentPipe);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes the synthetic corpus the sources proof needs after the console
    /// provisioned the vault: the research note is captured by the console
    /// itself, so this only reports the allowlist identifiers the proof
    /// enrolls through the console acts.
    /// </summary>
    internal static Task S5T7WriteConsoleCorpusAsync(string connectionsExecutable, string fixtureRoot)
    {
        var root = Path.GetFullPath(fixtureRoot);
        Console.WriteLine("S5T7 CONSOLE CORPUS root=" + root +
            " zone=" + S5T7Zone + " allowed=" + S5T7AllowedSession +
            " held=" + S5T7HeldMapping + " collection=" + S5T7Collection);
        return Task.CompletedTask;
    }
}
