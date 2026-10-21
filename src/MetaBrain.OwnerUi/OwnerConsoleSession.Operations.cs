using System.Globalization;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// The owner operations view of the console (S8-T4): the health of the running
/// service, the private audit trail, the error diagnostics and the retention
/// choice.
/// </summary>
/// <remarks>
/// Every value here is an answer of the running service on the owner control
/// pipe. The console holds no key, no token and no private content of its own:
/// it shows the actor, the action, the outcome, the recorded cost and the real
/// error code the service reported, and it holds nothing else. The private
/// views of this screen belong to one unlocked epoch: they are dropped when
/// the vault leaves that epoch, and an answer that arrives afterwards is
/// ignored instead of restoring them.
/// </remarks>
internal sealed partial class OwnerConsoleSession
{
    private static readonly TimeSpan OperationsActTimeout = TimeSpan.FromSeconds(30);

    /// <summary>True while one operations act is in flight; further acts wait for its answer.</summary>
    public bool OperationsActInFlight { get; private set; }

    /// <summary>The operational health the service reported; null before the first answer.</summary>
    public OwnerOperationsHealth? OperationsHealth { get; private set; }

    /// <summary>The private audit trail, newest first. Never a body, a title or a secret.</summary>
    public OwnerOperationsAuditEntry[] OperationsAudit { get; private set; } = Array.Empty<OwnerOperationsAuditEntry>();

    /// <summary>The error diagnostics, newest first, with the owner-facing sentence.</summary>
    public OwnerOperationsDiagnostic[] OperationsDiagnostics { get; private set; } = Array.Empty<OwnerOperationsDiagnostic>();

    /// <summary>The owner-facing outcome of the last operations act.</summary>
    public string? OperationsMessage { get; private set; }

    /// <summary>The retention window the service currently applies, in days.</summary>
    public int OperationsRetentionDays { get; private set; } = 30;

    /// <summary>The record ceiling the service currently applies.</summary>
    public int OperationsMaximumRecords { get; private set; } = 500;

    /// <summary>
    /// Reads the health summary, the audit trail and the diagnostics in one
    /// owner act. A refusal leaves the private views cleared and says which
    /// condition stopped the read.
    /// </summary>
    public async Task RefreshOperationsAsync()
    {
        var epoch = TryBeginOperationsAct();
        if (epoch is null)
        {
            return;
        }

        OwnerOperationsResult health;
        OwnerOperationsResult audit;
        OwnerOperationsResult diagnostics;
        try
        {
            health = await ExecuteOperationsCallAsync(
                token => OwnerChannelClient.ReadOperationsHealthAsync(ControlPipe, token)).ConfigureAwait(true);
            audit = await ExecuteOperationsCallAsync(
                token => OwnerChannelClient.ReadOperationsAuditAsync(ControlPipe, 0, 100, token)).ConfigureAwait(true);
            diagnostics = await ExecuteOperationsCallAsync(
                token => OwnerChannelClient.ReadOperationsDiagnosticsAsync(ControlPipe, 0, 100, token)).ConfigureAwait(true);
        }
        finally
        {
            OperationsActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        if (epoch != _privateEpoch)
        {
            Append("ignored a stale operations answer: the unlocked vault state it belonged to is gone, so the cleared operations view stays cleared");
            RaiseChanged();
            return;
        }

        ApplyOperationsAnswers(health, audit, diagnostics);
        Append("owner act: operations read -> completed");
        RaiseChanged();
    }

    /// <summary>
    /// Applies one retention choice to the private operations state and, when
    /// the owner asks for it, clears the safe operational log.
    /// </summary>
    public async Task ApplyOperationsRetentionAsync(int retentionDays, int maximumRecords, bool purgeOperationalLog)
    {
        var epoch = TryBeginOperationsAct();
        if (epoch is null)
        {
            return;
        }

        OwnerOperationsResult result;
        try
        {
            result = await ExecuteOperationsCallAsync(
                token => OwnerChannelClient.ApplyOperationsRetentionAsync(
                    ControlPipe,
                    retentionDays,
                    maximumRecords,
                    purgeOperationalLog,
                    token)).ConfigureAwait(true);
        }
        finally
        {
            OperationsActInFlight = false;
        }

        await ProbeAsync().ConfigureAwait(true);
        if (epoch != _privateEpoch)
        {
            Append("ignored a stale operations retention answer: the unlocked vault state it belonged to is gone");
            RaiseChanged();
            return;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Retention is not null)
        {
            OperationsRetentionDays = result.Retention.RetentionDays;
            OperationsMaximumRecords = result.Retention.MaximumRecords;
            OperationsMessage =
                "The service applied the retention choice: it keeps " +
                result.Retention.RetentionDays.ToString(CultureInfo.InvariantCulture) +
                " days and at most " +
                result.Retention.MaximumRecords.ToString(CultureInfo.InvariantCulture) +
                " records, removed " +
                result.Retention.RemovedAuditEntries.ToString(CultureInfo.InvariantCulture) +
                " audit entries and " +
                result.Retention.RemovedDiagnosticEntries.ToString(CultureInfo.InvariantCulture) +
                " diagnostics, and now holds " +
                result.Retention.AuditEntries.ToString(CultureInfo.InvariantCulture) +
                " audit entries and " +
                result.Retention.DiagnosticEntries.ToString(CultureInfo.InvariantCulture) +
                " diagnostics" +
                (result.Retention.OperationalLogPurged
                    ? ". The safe operational log was cleared (" +
                      result.Retention.OperationalLogEntriesRemoved.ToString(CultureInfo.InvariantCulture) +
                      " lines removed)."
                    : ". The safe operational log was left as it is.");
            Append("owner act: operations retention -> completed");
        }
        else
        {
            OperationsMessage = DescribeOperationsAnswer("apply the retention choice", result.Outcome, result.ErrorCode);
            Append("owner act: operations retention -> " +
                result.Outcome.ToString().ToLowerInvariant() +
                " (" + (result.ErrorCode ?? "unknown") + ")");
        }

        RaiseChanged();
    }

    /// <summary>
    /// Drops the private views of this screen. They belong to one unlocked
    /// epoch: a lock, an ended session or a lost service clears them, and the
    /// epoch moves so an answer still in flight cannot restore them.
    /// </summary>
    private void ClearPrivateOperationsState()
    {
        OperationsHealth = null;
        OperationsAudit = Array.Empty<OwnerOperationsAuditEntry>();
        OperationsDiagnostics = Array.Empty<OwnerOperationsDiagnostic>();
    }

    private void ApplyOperationsAnswers(
        OwnerOperationsResult health,
        OwnerOperationsResult audit,
        OwnerOperationsResult diagnostics)
    {
        if (health.Outcome != OwnerCommandOutcome.Completed || health.Health is null)
        {
            OperationsMessage = DescribeOperationsAnswer("read the operations health", health.Outcome, health.ErrorCode);
            return;
        }

        OperationsHealth = health.Health;
        OperationsRetentionDays = health.Health.RetentionDays;
        OperationsMaximumRecords = health.Health.MaximumRecords;
        OperationsAudit = audit.Audit ?? Array.Empty<OwnerOperationsAuditEntry>();
        OperationsDiagnostics = diagnostics.Diagnostics ?? Array.Empty<OwnerOperationsDiagnostic>();
        OperationsMessage =
            "The service reported the health of this vault: the private index is " +
            (health.Health.PrivateIndexMaterialized ? "built" : "not built") +
            ", the provider is " +
            (health.Health.ProviderConfigured ? "configured" : "not configured") +
            ", the rolling spend is " +
            OwnerChannelClient.FormatOperationsCost(health.Health.RollingSpendUsd) +
            ", and the private operations state holds " +
            health.Health.AuditEntries.ToString(CultureInfo.InvariantCulture) +
            " audit entries and " +
            health.Health.DiagnosticEntries.ToString(CultureInfo.InvariantCulture) +
            " diagnostics.";
    }

    /// <summary>
    /// Starts one operations act only while the service reports the vault
    /// unlocked and no other operations act is in flight. The private epoch
    /// the request belongs to is returned, or null when no act is offered.
    /// </summary>
    private long? TryBeginOperationsAct()
    {
        if (!Attached || OperationsActInFlight || ReportedVaultState != OwnerVaultState.Unlocked)
        {
            OperationsMessage = "The operations view needs the unlocked vault: the service serves the audit trail, the diagnostics and the health summary only while the vault is open. Unlock it in Vault & key, then try again.";
            RaiseChanged();
            return null;
        }

        OperationsActInFlight = true;
        OperationsMessage = null;
        RaiseChanged();
        return _privateEpoch;
    }

    private async Task<OwnerOperationsResult> ExecuteOperationsCallAsync(
        Func<CancellationToken, Task<OwnerOperationsResult>> call)
    {
        try
        {
            using var timeout = new CancellationTokenSource(OperationsActTimeout);
            return await call(timeout.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new OwnerOperationsResult(
                OwnerCommandOutcome.Unavailable,
                "act_failed:" + ex.GetType().Name,
                null,
                null,
                null,
                null);
        }
    }

    private static string DescribeOperationsAnswer(string act, OwnerCommandOutcome outcome, string? errorCode)
    {
        var code = errorCode ?? "unknown";
        return outcome switch
        {
            OwnerCommandOutcome.Completed =>
                "The service completed the request but returned no operations payload, so this console shows nothing instead of guessing.",
            OwnerCommandOutcome.Refused => code switch
            {
                "vault_locked" =>
                    "The vault is locked, so the service answered no private operations read. Unlock the vault (Vault & key) and try again.",
                "forbidden" =>
                    "The service refused this request (code: forbidden). Only the owner channel may read the private operations state.",
                "invalid_request" =>
                    "The service refused this request as invalid (code: invalid_request). Nothing was read and nothing was changed.",
                _ => "The service refused to " + act + " (code: " + code + "). Nothing private was read or changed."
            },
            OwnerCommandOutcome.OwnerAuthorityDenied =>
                "This endpoint refused owner authority for the request (code: " + code + "). An agent channel never serves the private operations state.",
            OwnerCommandOutcome.ProtocolMismatch =>
                "The endpoint speaks a different protocol revision, so no operations request was performed.",
            _ => "No owner-channel answer reached the service (code: " + code + "), so the request was not performed."
        };
    }
}
