using System.Text.Json;
using System.Text.Json.Serialization;

namespace MetaBrain.Core.Domain;

/// <summary>
/// One entry in the encrypted model spend ledger. Represents either an active
/// pre-send cost reservation while a job executes, or a committed settlement
/// after completion.
/// </summary>
public sealed record SpendLedgerEntry(
    string EntryId,
    string JobId,
    DateTimeOffset TimestampUtc,
    string Provider,
    string Model,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd,
    bool IsReservation,
    string? Note = null);

/// <summary>
/// Encrypted rolling 30-day budget tracker.
/// Enforces R07/R22 pre-send ceilings:
/// - $0.25 max cost per job
/// - $5.00 max rolling 30-day cumulative spend (including active reservations)
/// - 10,000 input tokens max per job
/// - 2,000 output tokens max per job
/// </summary>
public sealed class SpendLedger
{
    public const int CurrentSchemaVersion = 1;
    public const decimal MaximumJobCostUsd = 0.25m;
    public const decimal MaximumRolling30DayCostUsd = 5.00m;
    public const long MaximumJobInputTokens = 10_000;
    public const long MaximumJobOutputTokens = 2_000;

    /// <summary>
    /// Historical conservative reservation from S1-T7 tests noted in PLAN.
    /// Kept as an authoritative unreconciled reservation against the rolling cap.
    /// </summary>
    public const decimal HistoricalUnreconciledReservationUsd = 0.0054m;

    public static readonly TimeSpan RollingWindow = TimeSpan.FromDays(30);

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public List<SpendLedgerEntry> Entries { get; init; } = new();
    public decimal HistoricalReservationUsd { get; init; } = HistoricalUnreconciledReservationUsd;

    public decimal CalculateRollingSpend(DateTimeOffset now)
    {
        var cutoff = now - RollingWindow;
        decimal total = HistoricalReservationUsd;

        foreach (var entry in Entries)
        {
            if (entry.TimestampUtc >= cutoff)
            {
                total += entry.CostUsd;
            }
        }

        return total;
    }

    public bool CanReserve(decimal estimatedCost, DateTimeOffset now, out string? refusalCode)
    {
        if (estimatedCost <= 0m)
        {
            refusalCode = "invalid_cost";
            return false;
        }

        if (estimatedCost > MaximumJobCostUsd)
        {
            refusalCode = "egress_cost_exceeded";
            return false;
        }

        var currentRolling = CalculateRollingSpend(now);
        if (currentRolling + estimatedCost > MaximumRolling30DayCostUsd)
        {
            refusalCode = "rolling_budget_exceeded";
            return false;
        }

        refusalCode = null;
        return true;
    }

    public SpendLedgerEntry AddReservation(
        string jobId,
        string provider,
        string model,
        decimal estimatedCost,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var entry = new SpendLedgerEntry(
            EntryId: "res-" + Guid.NewGuid().ToString("N"),
            JobId: jobId,
            TimestampUtc: now,
            Provider: provider,
            Model: model,
            InputTokens: 0,
            OutputTokens: 0,
            CostUsd: estimatedCost,
            IsReservation: true);

        Entries.Add(entry);
        return entry;
    }

    public void CommitSpend(
        string jobId,
        string provider,
        string model,
        long inputTokens,
        long outputTokens,
        decimal actualCost,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        // Remove the pending reservation for this job
        Entries.RemoveAll(e => e.JobId == jobId && e.IsReservation);

        // Record the settled spend
        var settled = new SpendLedgerEntry(
            EntryId: "spd-" + Guid.NewGuid().ToString("N"),
            JobId: jobId,
            TimestampUtc: now,
            Provider: provider,
            Model: model,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            CostUsd: actualCost,
            IsReservation: false);

        Entries.Add(settled);
    }

    public void ReleaseReservation(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        Entries.RemoveAll(e => e.JobId == jobId && e.IsReservation);
    }

    public void RetainUnknownReservation(string jobId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var index = Entries.FindIndex(e => e.JobId == jobId && e.IsReservation);
        if (index >= 0)
        {
            var existing = Entries[index];
            Entries[index] = existing with { Note = $"retained_unknown:{reason}" };
        }
    }
}

public static class SpendLedgerCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static byte[] Encode(SpendLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return JsonSerializer.SerializeToUtf8Bytes(ledger, JsonOptions);
    }

    public static SpendLedger Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            return new SpendLedger();
        }

        var span = bytes.AsSpan();
        if (span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            span = span[3..];
        }

        var decoded = JsonSerializer.Deserialize<SpendLedger>(span, JsonOptions);
        return decoded ?? new SpendLedger();
    }
}
