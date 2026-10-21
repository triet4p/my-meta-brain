using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// One record the owner may put into the export scope. The console offers
/// these by their title, so the owner never types or remembers a resource
/// identifier; the identity behind the title is the service's own.
/// </summary>
internal sealed record OwnerExportScopeCandidate(
    string Id,
    string Title,
    string Kind,
    long Revision,
    string Detail);

/// <summary>
/// The owner console's backup, restore and export view state (S8-T2).
/// <para>
/// Every answer here is an answer of the running service on the owner control
/// pipe: the service owns the snapshot, the container format, the key
/// envelope, the restore policy, the export scope and the withheld
/// citations. This session never opens a vault file, never derives or holds a
/// key, never decrypts a container and never decides what may leave the
/// vault; it carries the owner's own choices to the service and renders what
/// the service said.
/// </para>
/// <para>
/// The private epoch is the lock boundary. The export candidate list is
/// private content — it holds the titles of the owner's own records — and it
/// is dropped with the epoch it belonged to, together with the drafts of the
/// backup, restore and export forms. An answer that arrives after the epoch
/// moved is dropped instead of restoring cleared state, so a lock cannot be
/// undone by a reply that was already in flight.
/// </para>
/// <para>
/// The restore credential is the one exception to "this session remembers
/// nothing": it travels from the owner's own input control into exactly one
/// request and is never stored here, never logged into the activity list and
/// never written to a file.
/// </para>
/// </summary>
internal sealed partial class OwnerConsoleSession
{
    private static readonly TimeSpan BackupActTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RestoreActTimeout = TimeSpan.FromMinutes(5);

    private bool _backupActInFlight;

    /// <summary>Owner-facing outcome of the last backup act.</summary>
    public string? BackupMessage { get; private set; }

    /// <summary>Owner-facing outcome of the last restore act.</summary>
    public string? RestoreMessage { get; private set; }

    /// <summary>Owner-facing outcome of the last export act.</summary>
    public string? ExportMessage { get; private set; }

    /// <summary>
    /// Records the service ranked for the export scope, exactly as the service
    /// described them. The owner picks the scope by title; no resource ID ever
    /// has to be typed or remembered.
    /// </summary>
    public OwnerExportScopeCandidate[] ExportCandidates { get; private set; } = Array.Empty<OwnerExportScopeCandidate>();

    /// <summary>
    /// The sources the selected records cite, named by the service. They are
    /// offered separately because a citation to a source outside the scope is
    /// withheld whole: the owner can only bring a source into the scope by
    /// choosing it here.
    /// </summary>
    public OwnerExportScopeCandidate[] ExportSourceCandidates { get; private set; } = Array.Empty<OwnerExportScopeCandidate>();

    /// <summary>Owner-facing outcome of the last export candidate search.</summary>
    public string? ExportCandidateMessage { get; private set; }

    /// <summary>Owner-facing outcome of the last cited-source refresh.</summary>
    public string? ExportSourceMessage { get; private set; }

    /// <summary>True while one backup, restore or export act is in flight.</summary>
    public bool BackupActInFlight => _backupActInFlight;

    /// <summary>
    /// Increments whenever this session drops the drafts of the backup, restore
    /// and export forms. The host clears its own input controls when it
    /// observes a new value, so no path, no selection and no credential of a
    /// finished epoch stays on screen.
    /// </summary>
    public long BackupFormVersion { get; private set; }

    /// <summary>
    /// Ranks the owner's own records for the export scope through the same
    /// owner library search the Library view uses, so the scope is picked from
    /// service data instead of a remembered resource ID. The service still
    /// decides what is in scope and what is withheld.
    /// </summary>
    public async Task SearchExportCandidatesAsync(string query)
    {
        if (BackupActRefusal(requireUnlocked: true, "search the records for the export scope") is { } searchRefusal)
        {
            ExportCandidateMessage = searchRefusal;
            RaiseChanged();
            return;
        }

        if (TryBeginBackupAct(requireUnlocked: true) is not { } epoch)
        {
            return;
        }

        ExportCandidateMessage = null;
        var libraryQuery = new OwnerLibraryQuery(
            Text: string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
            Limit: 25);
        var result = await ExecuteBackupCallAsync(
            BackupActTimeout,
            token => OwnerChannelClient.SearchLibraryAsync(ControlPipe, libraryQuery, token),
            code => new OwnerLibrarySearchResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        if (epoch != _privateEpoch)
        {
            Append("ignored a stale export candidate answer; the private view stays cleared");
            RaiseChanged();
            return;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Page is not null)
        {
            ExportCandidates = result.Page.Hits.Select(ToExportCandidate).ToArray();
            ExportSourceCandidates = Array.Empty<OwnerExportScopeCandidate>();
            ExportCandidateMessage = ExportCandidates.Length == 0
                ? "The service found no record for that search. Type another word, or rebuild the library index in Library first."
                : "The service ranked " + ExportCandidates.Length.ToString(CultureInfo.InvariantCulture) +
                    " record(s). Choose the ones you want to export; the service will carry exactly those and withhold every citation to a record outside your selection.";
        }
        else
        {
            ExportCandidates = Array.Empty<OwnerExportScopeCandidate>();
            ExportSourceCandidates = Array.Empty<OwnerExportScopeCandidate>();
            ExportCandidateMessage = result.Outcome == OwnerCommandOutcome.Completed
                ? "The service answered the search but returned no record, so this console shows nothing instead of guessing what it would have exported."
                : result.ErrorCode is "index_not_materialized" or "search_index_stale"
                    ? "The service cannot search the library index yet (" + result.ErrorCode +
                        "). Rebuild the index in Library, then search again. Nothing was exported."
                    : DescribeExportScopeSearchOutcome(result.Outcome, result.ErrorCode);
        }

        Append("search export candidates -> " + result.Outcome.ToString().ToLowerInvariant() +
            (result.Outcome == OwnerCommandOutcome.Completed ? string.Empty : " (" + (result.ErrorCode ?? "unknown") + ")"));
        RaiseChanged();
    }

    /// <summary>
    /// Names the sources the selected records cite, so the owner can decide
    /// whether a cited source may leave the vault with them. The service
    /// serves the citations of every selected record and the registry entry
    /// of every cited source; the console never names a source the service
    /// did not name, and it never brings a source into the scope by itself.
    /// </summary>
    public async Task RefreshExportSourceCandidatesAsync(IReadOnlyList<(string Id, long Revision)> selectedRecords)
    {
        ArgumentNullException.ThrowIfNull(selectedRecords);
        if (BackupActRefusal(requireUnlocked: true, "name the sources the selected records cite") is { } sourceRefusal)
        {
            ExportSourceMessage = sourceRefusal;
            RaiseChanged();
            return;
        }

        if (TryBeginBackupAct(requireUnlocked: true) is not { } epoch)
        {
            return;
        }

        ExportSourceMessage = null;
        var cited = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var record in selectedRecords)
        {
            var item = await ExecuteBackupCallAsync(
                BackupActTimeout,
                token => OwnerChannelClient.ReadMemoryItemAsync(ControlPipe, record.Id, record.Revision, token),
                code => new OwnerItemReadResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
            if (item.Outcome != OwnerCommandOutcome.Completed || item.Item is null)
            {
                continue;
            }

            foreach (var citation in item.Item.Citations)
            {
                if (string.IsNullOrEmpty(citation.SourceId))
                {
                    continue;
                }

                cited.TryAdd(citation.SourceId!, citation.SourceRevision);
            }
        }

        var sources = new List<OwnerExportScopeCandidate>();
        foreach (var entry in cited.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var source = await ExecuteBackupCallAsync(
                BackupActTimeout,
                token => OwnerChannelClient.ReadSourceAsync(ControlPipe, entry.Key, null, token),
                code => new OwnerSourceReadResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);
            if (source.Outcome != OwnerCommandOutcome.Completed || source.Source is null)
            {
                continue;
            }

            sources.Add(new OwnerExportScopeCandidate(
                source.Source.Id,
                string.IsNullOrWhiteSpace(source.Source.Title) ? "(no title)" : source.Source.Title!,
                "source",
                source.Source.Revision,
                "source  |  availability=" + source.Source.Availability +
                "  |  version=" + (source.Source.Version ?? "none") +
                "  |  cited by the selected record(s)"));
        }

        await ProbeAsync().ConfigureAwait(true);
        if (epoch != _privateEpoch)
        {
            Append("ignored a stale cited-source answer; the private view stays cleared");
            RaiseChanged();
            return;
        }

        ExportSourceCandidates = sources.ToArray();
        ExportCandidates = ExportCandidates
            .Where(candidate => !cited.ContainsKey(candidate.Id))
            .Concat(sources)
            .ToArray();
        ExportSourceMessage = sources.Count == 0
            ? "The selected records cite no source the service could name, so the export will carry no citation."
            : "The service named " + sources.Count.ToString(CultureInfo.InvariantCulture) +
              " source(s) the selected records cite. A citation to a source you do not choose here is withheld whole, so choose the source if it may leave the vault with the record.";
        Append("named the cited sources for the export scope -> " + sources.Count.ToString(CultureInfo.InvariantCulture));
        RaiseChanged();
    }

    private static OwnerExportScopeCandidate ToExportCandidate(OwnerLibraryHit hit) =>
        new(
            hit.Id,
            string.IsNullOrWhiteSpace(hit.Title) ? "(no title)" : hit.Title,
            hit.Kind,
            hit.Revision,
            "kind=" + hit.Kind + "  level=" + hit.Level + "  zone=" + hit.ZoneId +
            "  revision=" + hit.Revision.ToString(CultureInfo.InvariantCulture) +
            "  publication=" + hit.Publication);

    /// <summary>
    /// Asks the service for one encrypted snapshot of the unlocked vault and
    /// writes it to the file the owner chose. The service seals the snapshot
    /// and owns the key envelope; the live vault is not changed.
    /// </summary>
    public async Task BackupVaultAsync(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            BackupMessage = "No backup was taken: choose the file the snapshot should be written to.";
            RaiseChanged();
            return;
        }

        if (File.Exists(backupPath.Trim()))
        {
            BackupMessage = "No backup was taken: that file already exists. A backup never overwrites a file you already have — choose another name.";
            RaiseChanged();
            return;
        }

        if (BackupActRefusal(requireUnlocked: true, "take the encrypted backup") is { } backupRefusal)
        {
            BackupMessage = backupRefusal;
            RaiseChanged();
            return;
        }

        if (TryBeginBackupAct(requireUnlocked: true) is not { } epoch)
        {
            return;
        }

        var result = await ExecuteBackupCallAsync(
            BackupActTimeout,
            token => OwnerChannelClient.BackupVaultAsync(ControlPipe, backupPath.Trim(), token),
            code => new OwnerBackupResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        if (epoch != _privateEpoch)
        {
            Append("ignored a stale backup answer; the private view stays cleared");
            RaiseChanged();
            return;
        }

        BackupMessage = DescribeBackupOutcome(result.Outcome, result.ErrorCode, result.Backup);
        Append("owner act: backup -> " + result.Outcome.ToString().ToLowerInvariant() +
            (result.Outcome == OwnerCommandOutcome.Completed ? string.Empty : " (" + (result.ErrorCode ?? "unknown") + ")"));
        RaiseChanged();
    }

    /// <summary>
    /// Asks the service to turn one encrypted backup into a clean vault
    /// directory. The credential travels in exactly this one request and is
    /// not kept here. A wrong credential, a damaged container and a lost key
    /// fail closed: nothing is written and no key is reset.
    /// </summary>
    public async Task RestoreVaultAsync(
        string backupPath,
        string targetPath,
        string? credential,
        bool useRecoveryCode)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || string.IsNullOrWhiteSpace(targetPath))
        {
            RestoreMessage = "No restore was attempted: choose the backup file and the new folder it should be restored into.";
            RaiseChanged();
            return;
        }

        if (string.IsNullOrEmpty(credential))
        {
            RestoreMessage = "No restore was attempted: type the " +
                (useRecoveryCode ? "recovery key" : "owner passphrase") +
                " that opens this backup. A backup file on its own opens nothing.";
            RaiseChanged();
            return;
        }

        if (Directory.Exists(targetPath.Trim()) || File.Exists(targetPath.Trim()))
        {
            // A restore writes a new folder. The service refuses an existing
            // target as well, and the live vault is never replaced; this
            // check only tells the owner why before a request is sent.
            RestoreMessage = "No restore was attempted: that folder already exists. A restore writes a brand-new vault folder and never replaces a folder you already have — choose a folder that does not exist yet.";
            RaiseChanged();
            return;
        }

        if (BackupActRefusal(requireUnlocked: false, "restore the backup") is { } restoreRefusal)
        {
            RestoreMessage = restoreRefusal;
            RaiseChanged();
            return;
        }

        if (TryBeginBackupAct(requireUnlocked: false) is not { } epoch)
        {
            return;
        }

        var result = await ExecuteBackupCallAsync(
            RestoreActTimeout,
            token => OwnerChannelClient.RestoreVaultAsync(
                ControlPipe,
                backupPath.Trim(),
                targetPath.Trim(),
                credential,
                useRecoveryCode,
                token),
            code => new OwnerRestoreResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        if (epoch != _privateEpoch)
        {
            Append("ignored a stale restore answer; the private view stays cleared");
            RaiseChanged();
            return;
        }

        RestoreMessage = DescribeRestoreOutcome(result.Outcome, result.ErrorCode, result.Restore);
        Append("owner act: restore -> " + result.Outcome.ToString().ToLowerInvariant() +
            (result.Outcome == OwnerCommandOutcome.Completed ? string.Empty : " (" + (result.ErrorCode ?? "unknown") + ")"));
        RaiseChanged();
    }

    /// <summary>
    /// Asks the service to build the readable document for the records the
    /// owner selected, and writes that document to the file the owner chose.
    /// The file is written once and never over an existing file: a copy that
    /// already left the vault is not revoked by a later lock, purge or export,
    /// and a second export never silently replaces the first one.
    /// </summary>
    public async Task ExportRecordsAsync(
        string[] resourceIds,
        bool includeRelations,
        bool includeLifecycleStates,
        string outputPath)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);
        if (resourceIds.Length == 0)
        {
            ExportMessage = "No export was made: choose at least one record to export. An export with an empty scope is refused rather than read as \"everything\".";
            RaiseChanged();
            return;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            ExportMessage = "No export was made: choose the file the readable copy should be written to.";
            RaiseChanged();
            return;
        }

        if (File.Exists(outputPath.Trim()))
        {
            ExportMessage = "No export was made: that file already exists. Choose another name — an export never overwrites a file you already have.";
            RaiseChanged();
            return;
        }

        if (BackupActRefusal(requireUnlocked: true, "write the readable copy") is { } exportRefusal)
        {
            ExportMessage = exportRefusal;
            RaiseChanged();
            return;
        }

        if (TryBeginBackupAct(requireUnlocked: true) is not { } epoch)
        {
            return;
        }

        var result = await ExecuteBackupCallAsync(
            BackupActTimeout,
            token => OwnerChannelClient.ExportRecordsAsync(
                ControlPipe,
                resourceIds,
                includeRelations,
                includeLifecycleStates,
                token),
            code => new OwnerExportResult(OwnerCommandOutcome.Unavailable, code, null, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        if (epoch != _privateEpoch)
        {
            Append("ignored a stale export answer; the private view stays cleared");
            RaiseChanged();
            return;
        }

        if (result.Outcome != OwnerCommandOutcome.Completed || result.Export is null || result.Document is null)
        {
            ExportMessage = DescribeExportOutcome(result.Outcome, result.ErrorCode, null);
            Append("owner act: export -> " + result.Outcome.ToString().ToLowerInvariant() +
                (result.Outcome == OwnerCommandOutcome.Completed ? string.Empty : " (" + (result.ErrorCode ?? "unknown") + ")"));
            RaiseChanged();
            return;
        }

        var report = result.Export;
        var document = result.Document;
        try
        {
            await using (var output = new FileStream(outputPath.Trim(), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await output.WriteAsync(document).ConfigureAwait(true);
                await output.FlushAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ExportMessage = "The service built the export, but this console could not write the file (code: export_file_unwritable). " +
                "Choose another folder or file name and try again; nothing was overwritten.";
            Append("owner act: export -> the file could not be written (nothing was overwritten)");
            RaiseChanged();
            return;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(document);
        }

        ExportMessage = DescribeExportOutcome(result.Outcome, result.ErrorCode, report);
        Append("owner act: export -> " + result.Outcome.ToString().ToLowerInvariant());
        RaiseChanged();
    }

    /// <summary>
    /// The reason an act is not offered right now, in owner words, or null
    /// when the act may run. An act that is not offered is never dropped
    /// silently: the owner always learns why and what to do next.
    /// </summary>
    private string? BackupActRefusal(bool requireUnlocked, string act)
    {
        if (!Attached)
        {
            return "This console did not " + act + ": no owner session is attached to the service. Connect to the service first.";
        }

        if (_backupActInFlight)
        {
            return "This console did not " + act + ": another backup, restore or export is still running. Wait for it to finish and try again.";
        }

        if (requireUnlocked && ReportedVaultState != OwnerVaultState.Unlocked)
        {
            return "This console did not " + act + ": the vault is locked, so the service answers no private read. Unlock it in Vault & key and try again.";
        }

        return null;
    }

    /// <summary>
    /// Starts one backup, restore or export act. The backup and the export
    /// need the unlocked vault, because the service reads the encrypted
    /// records; the restore does not, because it opens a backup file and
    /// writes a new folder, and the owner may restore while the vault is
    /// locked. Returns the private epoch the request belongs to, or null when
    /// no act is offered.
    /// </summary>
    private long? TryBeginBackupAct(bool requireUnlocked)
    {
        if (!Attached || _backupActInFlight)
        {
            return null;
        }

        if (requireUnlocked && ReportedVaultState != OwnerVaultState.Unlocked)
        {
            return null;
        }

        _backupActInFlight = true;
        RaiseChanged();
        return _privateEpoch;
    }

    private async Task<T> ExecuteBackupCallAsync<T>(
        TimeSpan timeoutValue,
        Func<CancellationToken, Task<T>> call,
        Func<string, T> onFailure)
    {
        try
        {
            using var timeout = new CancellationTokenSource(timeoutValue);
            return await call(timeout.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return onFailure("act_failed:" + ex.GetType().Name);
        }
        finally
        {
            // The act is over in every case, so the next owner act is offered
            // again instead of waiting for an act that already ended.
            _backupActInFlight = false;
        }
    }

    /// <summary>
    /// Drops the private part of this view: the ranked records of the export
    /// scope and the drafts of the three forms. The forms' inputs live in the
    /// host, which clears them when the form version moves. The outcome
    /// messages of the acts stay: they are the owner's own feedback about
    /// what the service did, and they carry no private record content.
    /// </summary>
    private void ClearPrivateBackupState()
    {
        _backupActInFlight = false;
        ExportCandidates = Array.Empty<OwnerExportScopeCandidate>();
        ExportSourceCandidates = Array.Empty<OwnerExportScopeCandidate>();
        ExportCandidateMessage = null;
        ExportSourceMessage = null;
        BackupFormVersion++;
    }

    /// <summary>
    /// The owner-facing answer of one search for the export scope. The search
    /// reads nothing that leaves the vault, so its refusals are about the
    /// index and the vault state, never about a record.
    /// </summary>
    private static string DescribeExportScopeSearchOutcome(OwnerCommandOutcome outcome, string? errorCode)
    {
        var code = errorCode ?? "unknown";
        return outcome switch
        {
            OwnerCommandOutcome.Completed =>
                "The service answered the search but returned no record, so this console shows nothing instead of guessing what it would have exported.",
            OwnerCommandOutcome.Refused when code.StartsWith("act_failed", StringComparison.Ordinal) =>
                "The search did not reach the service (code: " + code + "). Nothing was searched and nothing was exported.",
            OwnerCommandOutcome.Refused =>
                "The service refused the search (code: " + code + "). No record was searched and nothing was exported.",
            OwnerCommandOutcome.OwnerAuthorityDenied =>
                "This endpoint refused owner authority for the search (code: " + code + "). An agent channel never searches the owner library.",
            OwnerCommandOutcome.ProtocolMismatch =>
                "The endpoint speaks a different protocol revision, so no search was performed.",
            _ => "No owner-channel answer reached the service for the search (code: " + code + "), so nothing was searched and nothing was exported."
        };
    }

    private static string DescribeBackupOutcome(
        OwnerCommandOutcome outcome,
        string? errorCode,
        OwnerBackupReport? report)
    {
        var code = errorCode ?? "unknown";
        return outcome switch
        {
            OwnerCommandOutcome.Completed when report is not null =>
                "The service wrote the encrypted backup to " + report.Path + " (" +
                report.Bytes.ToString(CultureInfo.InvariantCulture) + " bytes, " +
                report.RecordCount.ToString(CultureInfo.InvariantCulture) + " record(s)). " +
                "The file holds the sealed snapshot and the wrapped key envelope only: no data key, no passphrase, no recovery key and no readable record. " +
                "Keep it where you keep your recovery key — a backup file on its own opens nothing, a backup file with your passphrase opens everything.",
            OwnerCommandOutcome.Completed =>
                "The service completed the backup but reported no file, so this console shows nothing instead of guessing where your backup went.",
            OwnerCommandOutcome.Refused when code.StartsWith("act_failed", StringComparison.Ordinal) =>
                "The backup did not reach the service (code: " + code + "). Nothing was written.",
            OwnerCommandOutcome.Refused => code switch
            {
                "vault_locked" =>
                    "The service refused the backup because the vault is locked (code: vault_locked). Unlock the vault in Vault & key, then take the backup.",
                "backup_failed" =>
                    "The service could not write the backup file (code: backup_failed). Check that the folder exists and that you can write to it, then try again. Nothing else changed.",
                "invalid_request" =>
                    "The service refused the backup request as invalid (code: invalid_request). Choose a file name and try again.",
                _ => "The service refused the backup (code: " + code + "). No file was written and the live vault is unchanged."
            },
            OwnerCommandOutcome.OwnerAuthorityDenied =>
                "This endpoint refused owner authority for the backup (code: " + code + "). An agent channel never takes a backup.",
            OwnerCommandOutcome.ProtocolMismatch =>
                "The endpoint speaks a different protocol revision, so no backup was attempted.",
            _ => "No owner-channel answer reached the service for the backup (code: " + code + "), so no file was written."
        };
    }

    private static string DescribeRestoreOutcome(
        OwnerCommandOutcome outcome,
        string? errorCode,
        OwnerRestoreReport? report)
    {
        var code = errorCode ?? "unknown";
        return outcome switch
        {
            OwnerCommandOutcome.Completed when report is not null =>
                "The service restored the backup into " + report.Path + ": " +
                report.RecordCount.ToString(CultureInfo.InvariantCulture) + " record(s) at manifest revision " +
                report.ManifestRevision.ToString(CultureInfo.InvariantCulture) + ". " +
                "The restored vault carries no live grant and no session, and " +
                report.ConsumedTokensPreserved.ToString(CultureInfo.InvariantCulture) +
                " already-used token(s) stay used. Your live vault is untouched: to work with the restored copy, point a vault directory setting at it — this running service still serves the live vault.",
            OwnerCommandOutcome.Completed =>
                "The service completed the restore but reported no restored folder, so this console shows nothing instead of guessing where it went.",
            OwnerCommandOutcome.Refused when code.StartsWith("act_failed", StringComparison.Ordinal) =>
                "The restore did not reach the service (code: " + code + "). Nothing was written.",
            OwnerCommandOutcome.Refused => code switch
            {
                "restore_denied" =>
                    "The service refused this credential (code: restore_denied). Nothing was written, no key was reset and no fallback key was invented. Check the passphrase, or use the recovery key you saved outside the vault.",
                "restore_corrupt" =>
                    "The service refused this backup file as damaged (code: restore_corrupt). The file was changed or cut after it was written, so nothing was restored. Take a new backup.",
                "restore_failed" =>
                    "The service could not write the restored vault (code: restore_failed). Nothing was written and no partial vault is left behind: the target folder may already exist, or the parent folder may not be writable. Choose a new folder that does not exist yet.",
                "restore_unavailable" =>
                    "The service cannot restore right now (code: restore_unavailable). Nothing was written.",
                "invalid_request" =>
                    "The service refused the restore request as invalid (code: invalid_request). Choose a backup file, a new folder that does not exist yet, and one credential.",
                _ => "The service refused the restore (code: " + code + "). Nothing was written and the live vault is unchanged."
            },
            OwnerCommandOutcome.OwnerAuthorityDenied =>
                "This endpoint refused owner authority for the restore (code: " + code + "). An agent channel never restores a vault.",
            OwnerCommandOutcome.ProtocolMismatch =>
                "The endpoint speaks a different protocol revision, so no restore was attempted.",
            _ => "No owner-channel answer reached the service for the restore (code: " + code + "), so nothing was written."
        };
    }

    private static string DescribeExportOutcome(
        OwnerCommandOutcome outcome,
        string? errorCode,
        OwnerExportReport? report)
    {
        var code = errorCode ?? "unknown";
        return outcome switch
        {
            OwnerCommandOutcome.Completed when report is not null =>
                "The service wrote the readable copy of " +
                report.RecordCount.ToString(CultureInfo.InvariantCulture) + " record(s) and " +
                report.RelationCount.ToString(CultureInfo.InvariantCulture) + " relation(s); " +
                report.WithheldCitationCount.ToString(CultureInfo.InvariantCulture) +
                " citation(s) to records outside your selection were withheld whole. " +
                "The file is plaintext: a copy that has left the vault is not revoked by a later lock, purge or export.",
            OwnerCommandOutcome.Completed =>
                "The service completed the export but reported no document, so this console shows nothing instead of guessing what was written.",
            OwnerCommandOutcome.Refused when code.StartsWith("act_failed", StringComparison.Ordinal) =>
                "The export did not reach the service (code: " + code + "). No file was written.",
            OwnerCommandOutcome.Refused => code switch
            {
                "invalid_request" =>
                    "The service refused the export as invalid (code: invalid_request). Choose at least one record; an export with an empty scope is refused rather than read as \"everything\".",
                "vault_locked" =>
                    "The service refused the export because the vault is locked (code: vault_locked). Unlock the vault in Vault & key, then export.",
                "export_failed" =>
                    "The service could not build the export (code: export_failed). No file was written.",
                _ => "The service refused the export (code: " + code + "). No file was written."
            },
            OwnerCommandOutcome.OwnerAuthorityDenied =>
                "This endpoint refused owner authority for the export (code: " + code + "). An agent channel never exports the owner's records.",
            OwnerCommandOutcome.ProtocolMismatch =>
                "The endpoint speaks a different protocol revision, so no export was attempted.",
            _ => "No owner-channel answer reached the service for the export (code: " + code + "), so no file was written."
        };
    }
}
