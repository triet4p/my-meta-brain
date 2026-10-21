using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// The safe operational log of one installation: a plain, bounded, sanitized
/// record of what the service did and how it ended.
/// </summary>
/// <remarks>
/// The log carries only the operation token, the outcome token and the real
/// error code. It never carries a title, a body, a query, a path, a resource
/// ID, a credential, a token or any payload, so it can be read without
/// unlocking the vault and without disclosing private content. It is not a
/// private store: the private audit trail is the encrypted operations state
/// inside the vault, and this log is the nonsensitive operational trace that
/// also covers the refusals that happen while the vault is locked.
/// </remarks>
public interface IOperationalLog
{
    /// <summary>Records one sanitized line. The values are already bounded tokens.</summary>
    void Write(string operation, string outcome, string? errorCode);

    /// <summary>The current size and age of the log, for the owner health view.</summary>
    OperationalLogStatus Status();

    /// <summary>
    /// Removes every recorded line and reports how many were removed. The
    /// owner asks for this explicitly; nothing else clears the log.
    /// </summary>
    int Purge();
}

/// <summary>The size and age of the safe operational log.</summary>
public sealed record OperationalLogStatus(
    string Name,
    long Bytes,
    int Entries,
    DateTimeOffset? LastWriteUtc);

/// <summary>One recorded operation as the owner sees it.</summary>
public sealed record OperationsAuditWire(
    long Sequence,
    DateTimeOffset RecordedAtUtc,
    string Actor,
    string Action,
    string Outcome,
    string? ErrorCode,
    decimal? CostUsd,
    string? ResourceId);

/// <summary>One recorded failure as the owner sees it, with the next action.</summary>
public sealed record OperationsDiagnosticWire(
    long Sequence,
    DateTimeOffset RecordedAtUtc,
    string Component,
    string Code,
    string OwnerMessage,
    string? ResourceId);

/// <summary>The owner's operational health: what works, what failed and what is kept.</summary>
public sealed record OperationsHealthWire(
    string VaultState,
    bool PrivateIndexMaterialized,
    int PrivateIndexRows,
    bool ProviderConfigured,
    decimal RollingSpendUsd,
    int AuditEntries,
    int DiagnosticEntries,
    int RetentionDays,
    int MaximumRecords,
    long PurgedThroughSequence,
    OperationsOperationalLogWire? OperationalLog,
    string? LastErrorCode,
    DateTimeOffset GeneratedAtUtc);

/// <summary>The safe operational log as the owner sees it.</summary>
public sealed record OperationsOperationalLogWire(
    string Name,
    long Bytes,
    int Entries,
    DateTimeOffset? LastWriteUtc);

/// <summary>The result of one retention choice.</summary>
public sealed record OperationsRetentionWire(
    int RetentionDays,
    int MaximumRecords,
    int RemovedAuditEntries,
    int RemovedDiagnosticEntries,
    int AuditEntries,
    int DiagnosticEntries,
    bool OperationalLogPurged,
    int OperationalLogEntriesRemoved);

/// <summary>
/// The owner operations service of S8-T4: the health summary, the private
/// audit trail, the error diagnostics and the retention choice.
/// </summary>
/// <remarks>
/// Every surface here is owner-only and unlocked-only. The service holds no
/// key of its own: it reads and writes the private operations state through
/// the unlocked lease, exactly like the other dedicated owner states, and it
/// never serves an audit entry, a diagnostic or a cost to an agent caller.
/// </remarks>
public sealed class OperationsService
{
    private readonly TimeProvider _timeProvider;

    public OperationsService(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// The owner's health summary: the state of the vault, the private index,
    /// the provider configuration, the recorded spend, the size of the private
    /// operations state and the size of the safe operational log.
    /// </summary>
    public OperationsHealthWire Health(
        AuthenticatedContext context,
        IVaultOperation operation,
        string vaultState,
        bool privateIndexMaterialized,
        int privateIndexRows,
        bool providerConfigured,
        decimal rollingSpendUsd,
        IOperationalLog? operationalLog)
    {
        EnsureOwner(context);
        ArgumentNullException.ThrowIfNull(operation);

        var state = OperationsAuditAuthority.ValidateAndNormalizeState(operation.LoadOperationsAuditState());
        var lastError = state.Diagnostics.Length == 0
            ? null
            : state.Diagnostics[^1].Code;
        return new OperationsHealthWire(
            vaultState,
            privateIndexMaterialized,
            privateIndexRows,
            providerConfigured,
            rollingSpendUsd,
            state.Audit.Length,
            state.Diagnostics.Length,
            state.RetentionDays,
            state.MaximumRecords,
            state.PurgedThroughSequence,
            ProjectLog(operationalLog),
            lastError,
            _timeProvider.GetUtcNow().ToUniversalTime());
    }

    /// <summary>
    /// One page of the private audit trail, newest first. The entries carry
    /// the actor, the action, the outcome, the recorded cost and the real
    /// error code, and nothing else.
    /// </summary>
    public OperationsAuditWire[] Audit(
        AuthenticatedContext context,
        IVaultOperation operation,
        int offset,
        int limit)
    {
        EnsureOwner(context);
        ArgumentNullException.ThrowIfNull(operation);
        if (offset < 0 || limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "The audit page bounds are invalid.");
        }

        var state = OperationsAuditAuthority.ValidateAndNormalizeState(operation.LoadOperationsAuditState());
        return state.Audit
            .OrderByDescending(entry => entry.Sequence)
            .Skip(offset)
            .Take(limit)
            .Select(entry => new OperationsAuditWire(
                entry.Sequence,
                entry.RecordedAtUtc,
                entry.Actor,
                entry.Action,
                entry.Outcome,
                entry.ErrorCode,
                entry.CostUsd,
                entry.ResourceId))
            .ToArray();
    }

    /// <summary>
    /// One page of the error diagnostics, newest first. Every entry carries
    /// the real error code and the owner-facing sentence that says what to do.
    /// </summary>
    public OperationsDiagnosticWire[] Diagnostics(
        AuthenticatedContext context,
        IVaultOperation operation,
        int offset,
        int limit)
    {
        EnsureOwner(context);
        ArgumentNullException.ThrowIfNull(operation);
        if (offset < 0 || limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "The diagnostics page bounds are invalid.");
        }

        var state = OperationsAuditAuthority.ValidateAndNormalizeState(operation.LoadOperationsAuditState());
        return state.Diagnostics
            .OrderByDescending(entry => entry.Sequence)
            .Skip(offset)
            .Take(limit)
            .Select(entry => new OperationsDiagnosticWire(
                entry.Sequence,
                entry.RecordedAtUtc,
                entry.Component,
                entry.Code,
                entry.OwnerMessage,
                entry.ResourceId))
            .ToArray();
    }

    /// <summary>
    /// Applies one retention choice to the private operations state and, when
    /// the owner asks for it, clears the safe operational log. The answer
    /// reports exactly how many entries were removed, so a retention pass is
    /// never silent.
    /// </summary>
    /// <remarks>
    /// The whole request is validated before the first removal: an invalid
    /// retention choice is refused with nothing deleted, so a refused request
    /// can never take a log line or an audit entry with it. The private state
    /// is updated before the log is cleared, so a failed encrypted write
    /// leaves the operational log exactly as it was.
    /// </remarks>
    public OperationsRetentionWire ApplyRetention(
        AuthenticatedContext context,
        IVaultOperation operation,
        int retentionDays,
        int maximumRecords,
        bool purgeOperationalLog,
        IOperationalLog? operationalLog)
    {
        EnsureOwner(context);
        ArgumentNullException.ThrowIfNull(operation);

        // The complete request is validated first: the retention act deletes,
        // so an invalid choice must be refused before anything is removed.
        OperationsAuditAuthority.ValidateRetentionChoice(retentionDays, maximumRecords);

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var outcome = operation.UpdateOperationsAuditState(state =>
        {
            var (applied, removedAudit, removedDiagnostics) =
                OperationsAuditAuthority.ApplyRetentionChoice(state, retentionDays, maximumRecords, now);
            return (applied, (applied, removedAudit, removedDiagnostics));
        });

        var (state2, removedAudit, removedDiagnostics) = outcome;

        // The private state is updated first: if the encrypted write fails,
        // the operational log still holds every line it had.
        var removedLogEntries = 0;
        var logPurged = false;
        if (purgeOperationalLog && operationalLog is not null)
        {
            removedLogEntries = operationalLog.Purge();
            logPurged = true;
        }

        return new OperationsRetentionWire(
            state2.RetentionDays,
            state2.MaximumRecords,
            removedAudit,
            removedDiagnostics,
            state2.Audit.Length,
            state2.Diagnostics.Length,
            logPurged,
            removedLogEntries);
    }

    private static OperationsOperationalLogWire? ProjectLog(IOperationalLog? operationalLog)
    {
        if (operationalLog is null)
        {
            return null;
        }

        var status = operationalLog.Status();
        return new OperationsOperationalLogWire(
            status.Name,
            status.Bytes,
            status.Entries,
            status.LastWriteUtc);
    }

    private static void EnsureOwner(AuthenticatedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Only the owner may read the private operations state.");
        }
    }
}

/// <summary>
/// The recorder of the private operations state (S8-T4).
/// </summary>
/// <remarks>
/// The recorder writes two things and nothing else: the private audit entry
/// and the owner-facing diagnostic, both inside the unlocked vault, and the
/// sanitized line of the safe operational log. A refusal that happened while
/// the vault was locked has no private entry at all: the private state only
/// exists inside the unlocked vault, so the recorder never keeps a deferred
/// plaintext queue, never holds a second key and never writes the refusal
/// anywhere except the sanitized operational log.
/// </remarks>
public sealed class OperationsAuditRecorder
{
    private readonly IOperationalLog? _operationalLog;
    private readonly TimeProvider _timeProvider;

    public OperationsAuditRecorder(IOperationalLog? operationalLog = null, TimeProvider? timeProvider = null)
    {
        _operationalLog = operationalLog;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Records one finished operation: the private audit entry, the private
    /// diagnostic when the operation failed, and the sanitized operational log
    /// line.
    /// </summary>
    /// <remarks>
    /// A failure to write the private state never fails the request: the
    /// owner's own operation already happened, and its answer must not be
    /// replaced by a failure of the trail. The failure is not hidden either:
    /// the recorder writes one truthful line in the safe operational log with
    /// the code <c>audit_persistence_failed</c>, so the owner can tell an
    /// incomplete trail from a refused operation. Only the two typed
    /// failures of the private state are caught here: the persistence failure
    /// of the store and the vault that locked between the request and this
    /// write. Anything else is a defect and keeps travelling.
    /// </remarks>
    public void Record(
        AuthenticatedContext context,
        string operation,
        bool audited,
        string outcome,
        string? errorCode,
        decimal? costUsd,
        string? resourceId,
        IVaultOperation? vaultOperation)
    {
        ArgumentNullException.ThrowIfNull(context);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var actor = context.Kind == PrincipalKind.Owner
            ? OperationsAuditAuthority.OwnerActor
            : context.Kind == PrincipalKind.Agent
                ? OperationsAuditAuthority.AgentActor
                : OperationsAuditAuthority.ServiceActor;

        if (!audited)
        {
            return;
        }

        // Only a real error code is recorded. A few product paths answer with a
        // sentence instead of a code; that sentence is the owner's answer in
        // the reply, and it never enters the audit trail, the diagnostics or
        // the operational log.
        var code = NormalizeErrorCode(errorCode);

        _operationalLog?.Write(operation, outcome, code);

        if (vaultOperation is null)
        {
            return;
        }

        try
        {
            vaultOperation.UpdateOperationsAuditState(state =>
            {
                var next = OperationsAuditAuthority.AppendAudit(state, new OperationsAuditEntry(
                    state.NextSequence,
                    now,
                    actor,
                    operation,
                    outcome,
                    code,
                    costUsd,
                    resourceId));

                if (outcome == OperationsAuditAuthority.CompletedOutcome || string.IsNullOrWhiteSpace(code))
                {
                    return (next, 0);
                }

                var withDiagnostic = OperationsAuditAuthority.AppendDiagnostic(next, new OperationsDiagnosticEntry(
                    next.NextSequence,
                    now,
                    OwnerGuidance.ComponentFor(operation),
                    code,
                    OwnerGuidance.ForCode(code),
                    resourceId));
                return (withDiagnostic, 0);
            });
        }
        catch (Exception ex) when (ex is OperationsAuditPersistenceException or ManagedResourceUnavailableException)
        {
            // The private operations state could not be written: the file is
            // unreadable, the key or the envelope failed, or the vault locked
            // between the request and this write. The owner's own operation
            // already happened, so its answer must survive. The failure is
            // reported truthfully in the sanitized operational log, which is
            // the one trace that survives a locked vault.
            _operationalLog?.Write(
                operation,
                OperationsAuditAuthority.AuditWriteFailedOutcome,
                OperationsAuditAuthority.AuditPersistenceFailedCode);
        }
    }

    /// <summary>
    /// Normalizes one error value into a code. A real code is a short token of
    /// lowercase letters, digits, dots, dashes and underscores; anything else
    /// is a sentence and is recorded as <c>unspecified</c>, so no free text
    /// and nothing derived from a secret reaches the private state or the
    /// operational log.
    /// </summary>
    private static string? NormalizeErrorCode(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return null;
        }

        if (errorCode.Length > OperationsAuditAuthority.MaximumCodeLength)
        {
            return "unspecified";
        }

        foreach (var character in errorCode)
        {
            var allowed = character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_';
            if (!allowed)
            {
                return "unspecified";
            }
        }

        return errorCode;
    }
}
