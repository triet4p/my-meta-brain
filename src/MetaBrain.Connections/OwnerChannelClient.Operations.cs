using System.Globalization;
using System.Text.Json;
using MetaBrain.Application;

namespace MetaBrain.Connections;

/// <summary>One recorded operation as the owner console received it.</summary>
public sealed record OwnerOperationsAuditEntry(
    long Sequence,
    DateTimeOffset RecordedAtUtc,
    string Actor,
    string Action,
    string Outcome,
    string? ErrorCode,
    decimal? CostUsd,
    string? ResourceId);

/// <summary>One recorded failure as the owner console received it.</summary>
public sealed record OwnerOperationsDiagnostic(
    long Sequence,
    DateTimeOffset RecordedAtUtc,
    string Component,
    string Code,
    string OwnerMessage,
    string? ResourceId);

/// <summary>The operational health the service reported.</summary>
public sealed record OwnerOperationsHealth(
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
    string? OperationalLogName,
    long OperationalLogBytes,
    int OperationalLogEntries,
    DateTimeOffset? OperationalLogLastWriteUtc,
    string? LastErrorCode,
    DateTimeOffset GeneratedAtUtc);

/// <summary>The result of one retention choice, as the service reported it.</summary>
public sealed record OwnerOperationsRetention(
    int RetentionDays,
    int MaximumRecords,
    int RemovedAuditEntries,
    int RemovedDiagnosticEntries,
    int AuditEntries,
    int DiagnosticEntries,
    bool OperationalLogPurged,
    int OperationalLogEntriesRemoved);

/// <summary>Result of one operations read or act.</summary>
public sealed record OwnerOperationsResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerOperationsHealth? Health,
    OwnerOperationsAuditEntry[]? Audit,
    OwnerOperationsDiagnostic[]? Diagnostics,
    OwnerOperationsRetention? Retention);

/// <summary>
/// The S8-T4 owner operations surface of the owner channel: the health
/// summary, the private audit trail, the error diagnostics and the retention
/// choice.
/// </summary>
/// <remarks>
/// These four operations exist only on the owner control pipe and only while
/// the vault is unlocked. The agent channel never serves them, and the client
/// carries no key, no token and no private content of its own: every value it
/// returns is an answer of the running service.
/// </remarks>
public static partial class OwnerChannelClient
{
    /// <summary>
    /// Reads the owner's operational health: the vault state, the private
    /// index, the provider configuration, the recorded spend, the size of the
    /// private operations state and the size of the safe operational log.
    /// </summary>
    public static async Task<OwnerOperationsResult> ReadOperationsHealthAsync(
        string controlPipe,
        CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerOperationsHealthOperation),
            "operations_health",
            cancellationToken).ConfigureAwait(false);
        return new OwnerOperationsResult(reply.Outcome, reply.ErrorCode, ProjectOperationsHealth(reply), null, null, null);
    }

    /// <summary>
    /// Reads one page of the private audit trail: who acted, what they acted
    /// on, how it ended, what it cost and which error code it produced.
    /// </summary>
    public static async Task<OwnerOperationsResult> ReadOperationsAuditAsync(
        string controlPipe,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerOperationsAuditOperation,
                OperationsOffset: offset,
                OperationsLimit: limit),
            "operations_audit",
            cancellationToken).ConfigureAwait(false);
        return new OwnerOperationsResult(reply.Outcome, reply.ErrorCode, null, ProjectOperationsAudit(reply), null, null);
    }

    /// <summary>
    /// Reads one page of the error diagnostics: the real error code of every
    /// recorded failure and the owner-facing sentence that says what to do.
    /// </summary>
    public static async Task<OwnerOperationsResult> ReadOperationsDiagnosticsAsync(
        string controlPipe,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerOperationsDiagnosticsOperation,
                OperationsOffset: offset,
                OperationsLimit: limit),
            "operations_diagnostics",
            cancellationToken).ConfigureAwait(false);
        return new OwnerOperationsResult(reply.Outcome, reply.ErrorCode, null, null, ProjectOperationsDiagnostics(reply), null);
    }

    /// <summary>
    /// Applies one retention choice to the private operations state and, when
    /// the owner asks for it, clears the safe operational log. The answer
    /// reports exactly how many entries were removed.
    /// </summary>
    public static async Task<OwnerOperationsResult> ApplyOperationsRetentionAsync(
        string controlPipe,
        int retentionDays,
        int maximumRecords,
        bool purgeOperationalLog,
        CancellationToken cancellationToken)
    {
        var reply = await ReadReplyAsync(
            controlPipe,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerOperationsRetentionOperation,
                RetentionDays: retentionDays,
                RetentionMaxRecords: maximumRecords,
                PurgeOperationalLog: purgeOperationalLog),
            "operations_retention",
            cancellationToken).ConfigureAwait(false);
        return new OwnerOperationsResult(reply.Outcome, reply.ErrorCode, null, null, null, ProjectOperationsRetention(reply));
    }

    private static OwnerOperationsHealth? ProjectOperationsHealth(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("operationsHealth", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? logName = null;
        long logBytes = 0;
        int logEntries = 0;
        DateTimeOffset? logLastWrite = null;
        if (payload.TryGetProperty("operationalLog", out var log) && log.ValueKind == JsonValueKind.Object)
        {
            logName = Text(log, "name");
            logBytes = Int64(log, "bytes");
            logEntries = Int32(log, "entries");
            logLastWrite = Timestamp(log, "lastWriteUtc");
        }

        return new OwnerOperationsHealth(
            Text(payload, "vaultState") ?? "unknown",
            payload.TryGetProperty("privateIndexMaterialized", out var materialized) &&
                materialized.ValueKind == JsonValueKind.True,
            Int32(payload, "privateIndexRows"),
            payload.TryGetProperty("providerConfigured", out var provider) && provider.ValueKind == JsonValueKind.True,
            Decimal(payload, "rollingSpendUsd") ?? 0m,
            Int32(payload, "auditEntries"),
            Int32(payload, "diagnosticEntries"),
            Int32(payload, "retentionDays"),
            Int32(payload, "maximumRecords"),
            Int64(payload, "purgedThroughSequence"),
            logName,
            logBytes,
            logEntries,
            logLastWrite,
            Text(payload, "lastErrorCode"),
            Timestamp(payload, "generatedAtUtc") ?? DateTimeOffset.MinValue);
    }

    private static OwnerOperationsAuditEntry[]? ProjectOperationsAudit(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("operationsAudit", out var payload) ||
            payload.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var entries = new List<OwnerOperationsAuditEntry>();
        foreach (var item in payload.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            entries.Add(new OwnerOperationsAuditEntry(
                Int64(item, "sequence"),
                Timestamp(item, "recordedAtUtc") ?? DateTimeOffset.MinValue,
                Text(item, "actor") ?? "unknown",
                Text(item, "action") ?? "unknown",
                Text(item, "outcome") ?? "unknown",
                Text(item, "errorCode"),
                Decimal(item, "costUsd"),
                Text(item, "resourceId")));
        }

        return entries.ToArray();
    }

    private static OwnerOperationsDiagnostic[]? ProjectOperationsDiagnostics(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("operationsDiagnostics", out var payload) ||
            payload.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var entries = new List<OwnerOperationsDiagnostic>();
        foreach (var item in payload.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            entries.Add(new OwnerOperationsDiagnostic(
                Int64(item, "sequence"),
                Timestamp(item, "recordedAtUtc") ?? DateTimeOffset.MinValue,
                Text(item, "component") ?? "service",
                Text(item, "code") ?? "unknown",
                Text(item, "ownerMessage") ?? string.Empty,
                Text(item, "resourceId")));
        }

        return entries.ToArray();
    }

    private static OwnerOperationsRetention? ProjectOperationsRetention(OwnerReply reply)
    {
        if (reply.Outcome != OwnerCommandOutcome.Completed ||
            !reply.Payload.TryGetProperty("operationsRetention", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new OwnerOperationsRetention(
            Int32(payload, "retentionDays"),
            Int32(payload, "maximumRecords"),
            Int32(payload, "removedAuditEntries"),
            Int32(payload, "removedDiagnosticEntries"),
            Int32(payload, "auditEntries"),
            Int32(payload, "diagnosticEntries"),
            payload.TryGetProperty("operationalLogPurged", out var purged) && purged.ValueKind == JsonValueKind.True,
            Int32(payload, "operationalLogEntriesRemoved"));
    }

    private static decimal? Decimal(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : null;

    /// <summary>
    /// The owner-facing cost text of one recorded operation. The value comes
    /// from the spend the service already charged.
    /// </summary>
    public static string FormatOperationsCost(decimal? costUsd) =>
        costUsd is null
            ? "-"
            : "$" + costUsd.Value.ToString("0.######", CultureInfo.InvariantCulture);
}
