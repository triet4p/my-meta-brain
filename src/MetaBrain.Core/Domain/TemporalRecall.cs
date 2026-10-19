namespace MetaBrain.Core.Domain;

/// <summary>
/// S7-T1 temporal recall policy over the existing S2-T5 validity/revision
/// semantics. This policy reads the stored lifecycle record only: it never
/// invents a state, a date, or a timezone, and it never changes what the
/// temporal schema means.
/// <para>
/// Honesty rules enforced here:
/// <list type="bullet">
/// <item><description>An unknown stamp bounds nothing: unknown validity is
/// unbounded, never fabricated into a date.</description></item>
/// <item><description>A coarse stamp bounds only the UTC period its own
/// wall-clock fields plus its own recorded offset cover. A coarse stamp with
/// no recorded zone bounds nothing instead of being reinterpreted in another
/// zone.</description></item>
/// <item><description>Validity containment is inclusive on both ends: an
/// instant equal to an exact bound counts as inside.</description></item>
/// <item><description>State at an instant replays only the recorded history
/// acts at or before that instant over the kind-derived initial states, so an
/// as-of answer reflects what was recorded then, never what is known
/// now.</description></item>
/// </list>
/// </para>
/// </summary>
public static class TemporalRecall
{
    /// <summary>
    /// True when <paramref name="instant"/> falls inside the validity window
    /// described by <paramref name="validFrom"/>/<paramref name="validUntil"/>.
    /// A null (unknown) bound is unbounded. A coarse bound with no recorded
    /// zone is unbounded on that side: the wall clock cannot honestly be
    /// placed without its zone.
    /// </summary>
    public static bool ValidityContains(TemporalStamp? validFrom, TemporalStamp? validUntil, DateTimeOffset instant)
    {
        if (validFrom is not null &&
            BoundStartUtc(validFrom) is { } start &&
            instant < start)
        {
            return false;
        }

        if (validUntil is not null &&
            BoundEndExclusiveUtc(validUntil) is { } end &&
            instant >= end)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// The inclusive UTC start of a validity bound, or null when the bound
    /// cannot honestly place an instant (unknown precision, or a coarse date
    /// with no recorded zone).
    /// </summary>
    public static DateTimeOffset? BoundStartUtc(TemporalStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        return stamp.Precision switch
        {
            TimestampPrecision.Exact => stamp.InstantUtc,
            TimestampPrecision.Day or TimestampPrecision.Month or TimestampPrecision.Quarter or TimestampPrecision.Year
                => PeriodStartUtc(stamp),
            _ => null,
        };
    }

    /// <summary>
    /// The exclusive UTC end of a validity bound (one tick past an exact
    /// instant so an instant equal to an exact bound still counts as inside),
    /// or null when the bound cannot honestly place an instant.
    /// </summary>
    public static DateTimeOffset? BoundEndExclusiveUtc(TemporalStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        return stamp.Precision switch
        {
            TimestampPrecision.Exact => stamp.InstantUtc?.AddTicks(1),
            TimestampPrecision.Day or TimestampPrecision.Month or TimestampPrecision.Quarter or TimestampPrecision.Year
                => PeriodEndExclusiveUtc(stamp),
            _ => null,
        };
    }

    /// <summary>
    /// The publication/question/intention states of <paramref name="record"/>
    /// as recorded at <paramref name="asOf"/>: the kind-derived initial
    /// states replayed through only the history acts recorded at or before
    /// that instant. Later acts never leak into an as-of answer.
    /// </summary>
    public static (PublicationState Publication, QuestionState? Question, IntentionState? Intention) StatesAt(
        StoredLifecycleRecord record,
        DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(record);
        var publication = PublicationState.Proposal;
        QuestionState? question = record.SubjectKind == MemoryKind.Question ? QuestionState.Open : null;
        IntentionState? intention = record.SubjectKind == MemoryKind.Intention ? IntentionState.Pending : null;

        foreach (var change in record.History)
        {
            if (change is null || change.RecordedAtUtc > asOf)
            {
                continue;
            }

            switch (change.Aspect)
            {
                case LifecycleAspect.Publication:
                    publication = ParsePublication(change.ToState);
                    break;
                case LifecycleAspect.Question:
                    question = ParseQuestion(change.ToState);
                    break;
                case LifecycleAspect.Intention:
                    intention = ParseIntention(change.ToState);
                    break;
                default:
                    break;
            }
        }

        return (publication, question, intention);
    }

    private static PublicationState ParsePublication(string token) =>
        string.Equals(token, LifecycleCodec.NoneState, StringComparison.Ordinal)
            ? PublicationState.Proposal
            : LifecycleTokens.ParsePublication(token);

    private static QuestionState ParseQuestion(string token) =>
        string.Equals(token, LifecycleCodec.NoneState, StringComparison.Ordinal)
            ? QuestionState.Open
            : LifecycleTokens.ParseQuestion(token);

    private static IntentionState ParseIntention(string token) =>
        string.Equals(token, LifecycleCodec.NoneState, StringComparison.Ordinal)
            ? IntentionState.Pending
            : LifecycleTokens.ParseIntention(token);

    private static DateTimeOffset? PeriodStartUtc(TemporalStamp stamp)
    {
        if (stamp.TimeZoneOffsetMinutes is not { } offset || offset is < -840 or > 840)
        {
            return null;
        }

        var zone = TimeSpan.FromMinutes(offset);
        try
        {
            return stamp.Precision switch
            {
                TimestampPrecision.Day when IsCalendarDay(stamp.LocalYear, stamp.LocalMonth, stamp.LocalDay) =>
                    new DateTimeOffset(stamp.LocalYear!.Value, stamp.LocalMonth!.Value, stamp.LocalDay!.Value, 0, 0, 0, zone),
                TimestampPrecision.Month when IsCalendarMonth(stamp.LocalYear, stamp.LocalMonth) =>
                    new DateTimeOffset(stamp.LocalYear!.Value, stamp.LocalMonth!.Value, 1, 0, 0, 0, zone),
                TimestampPrecision.Quarter when IsCalendarQuarter(stamp.LocalYear, stamp.LocalQuarter) =>
                    new DateTimeOffset(stamp.LocalYear!.Value, (stamp.LocalQuarter!.Value - 1) * 3 + 1, 1, 0, 0, 0, zone),
                TimestampPrecision.Year when stamp.LocalYear is { } year =>
                    new DateTimeOffset(year, 1, 1, 0, 0, 0, zone),
                _ => null,
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTimeOffset? PeriodEndExclusiveUtc(TemporalStamp stamp)
    {
        if (PeriodStartUtc(stamp) is not { } start)
        {
            return null;
        }

        try
        {
            return stamp.Precision switch
            {
                TimestampPrecision.Day => start.AddDays(1),
                TimestampPrecision.Month => start.AddMonths(1),
                TimestampPrecision.Quarter => start.AddMonths(3),
                TimestampPrecision.Year => start.AddYears(1),
                _ => null,
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool IsCalendarDay(int? year, int? month, int? day) =>
        year is not null && month is { } monthValue && monthValue is >= 1 and <= 12 &&
        day is { } dayValue && dayValue >= 1 && dayValue <= DateTime.DaysInMonth(year.Value, monthValue);

    private static bool IsCalendarMonth(int? year, int? month) =>
        year is not null && month is >= 1 and <= 12;

    private static bool IsCalendarQuarter(int? year, int? quarter) =>
        year is not null && quarter is >= 1 and <= 4;
}
