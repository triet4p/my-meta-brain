using System.Text.Json;
using System.Text.Json.Serialization;

namespace MetaBrain.Core.Domain;

/// <summary>
/// Policy bounds of the owner-enabled synthesis schedule (S6-T6). The bounds are
/// shared product policy: the service, the owner CLI and the owner UI all apply
/// the same floor and ceiling, so a schedule can never be configured to run more
/// often than the floor and an enabled schedule cannot silently become job spam.
/// The floor is a policy floor, not a claim about provider rate limits.
/// </summary>
public static class SynthesisSchedulePolicy
{
    /// <summary>Smallest owner-selectable gap between two scheduled runs (one minute).</summary>
    public const int MinimumIntervalMinutes = 1;

    /// <summary>Largest owner-selectable gap between two scheduled runs (seven days).</summary>
    public const int MaximumIntervalMinutes = 7 * 24 * 60;

    public static bool IsValidInterval(int intervalMinutes) =>
        intervalMinutes is >= MinimumIntervalMinutes and <= MaximumIntervalMinutes;
}

/// <summary>
/// The owner's explicit, opt-in synthesis schedule, stored as one encrypted
/// record inside the vault's system zone through the same shared store the
/// provider configuration and the spend ledger use. The record is the whole
/// schedule: the exact selected inputs (each pinned to one revision), the
/// objective, the requested level, the destination inbox, the provider/model,
/// the interval, and — read back to the owner — when the next run is due and
/// what the last scheduled run did.
/// <para>
/// The record is data, never authority: an enabled schedule runs the same
/// bounded owner-scoped synthesis job the owner can run by hand, with the same
/// pre-send caps, and its output is a pending proposal exactly like a manual
/// job. A missed slot is never replayed as extra runs, and nothing here can
/// unlock the vault.
/// </para>
/// </summary>
public sealed record StoredSynthesisSchedule(
    int SchemaVersion,
    bool Enabled,
    int IntervalMinutes,
    JobInputReference[] Inputs,
    string Objective,
    string TargetLevel,
    string DestinationZoneId,
    string DestinationResourceId,
    long DestinationRevision,
    string Provider,
    string Model,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? NextDueAtUtc,
    DateTimeOffset? LastRunAtUtc,
    string? LastRunJobId,
    string? LastRunOutcome)
{
    /// <summary>An enabled schedule always names the instant its next run is due.</summary>
    public bool IsCoherent => !Enabled || NextDueAtUtc is not null;
}

/// <summary>
/// The encrypted storage contract of one synthesis schedule. Every field is
/// validated on decode and an incoherent record fails closed instead of being
/// treated as a usable schedule.
/// </summary>
public static class SynthesisScheduleCodec
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumPayloadBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static byte[] Encode(StoredSynthesisSchedule record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!record.IsCoherent)
        {
            throw new ArgumentException("synthesis_schedule_invalid: an enabled schedule must name its next due instant.");
        }

        return JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
    }

    public static StoredSynthesisSchedule Decode(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length is <= 0 or > MaximumPayloadBytes)
        {
            throw new ArgumentException("synthesis_schedule_invalid: the stored schedule payload is empty or oversized.");
        }

        var span = JobCodec.StripUtf8Preamble(json);
        var offset = json.Length - span.Length;
        StoredSynthesisSchedule? record;
        try
        {
            record = JsonSerializer.Deserialize<StoredSynthesisSchedule>(new ReadOnlySpan<byte>(json, offset, span.Length), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("synthesis_schedule_invalid: the stored schedule schema is invalid.", ex);
        }

        if (record is null ||
            record.SchemaVersion != CurrentSchemaVersion ||
            record.Inputs is null ||
            record.Inputs.Length is < 2 or > MemoryEnvelopeCodec.MaximumSources ||
            string.IsNullOrWhiteSpace(record.Objective) ||
            record.Objective.Length > JobSelectionCodec.MaximumObjectiveLength ||
            !SynthesisLevels.TryParse(record.TargetLevel, out _) ||
            string.IsNullOrWhiteSpace(record.DestinationZoneId) ||
            string.IsNullOrWhiteSpace(record.DestinationResourceId) ||
            record.DestinationRevision < 1 ||
            string.IsNullOrWhiteSpace(record.Provider) ||
            string.IsNullOrWhiteSpace(record.Model) ||
            !SynthesisSchedulePolicy.IsValidInterval(record.IntervalMinutes) ||
            !record.IsCoherent)
        {
            throw new ArgumentException("synthesis_schedule_invalid: the stored schedule is not a coherent bounded synthesis schedule.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in record.Inputs)
        {
            if (input is null ||
                string.IsNullOrWhiteSpace(input.ResourceId) ||
                (!DomainIdentifiers.IsDomainIdentifier(input.ResourceId) && !SourceSnapshotIds.IsSnapshotId(input.ResourceId)) ||
                input.Revision < 1 ||
                !seen.Add(input.ResourceId))
            {
                throw new ArgumentException("synthesis_schedule_invalid: every scheduled input must be a distinct opaque identifier pinned to a positive revision.");
            }
        }

        return record;
    }
}
