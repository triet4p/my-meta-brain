using System.Globalization;

namespace MetaBrain.OwnerUi;

/// <summary>
/// One explicit time-zone choice for the intention trigger form. The service
/// contract accepts only an exact ISO-8601 instant with <c>Z</c> or a numeric
/// offset; this console therefore composes that value from a local wall clock
/// plus a time zone the owner chose, and shows the resulting UTC instant
/// before anything is sent. A named timezone is never guessed.
/// </summary>
internal sealed record IntentionTimeOption(string Label, bool UseLocal, TimeSpan Offset)
{
    public override string ToString() => Label;
}

/// <summary>
/// Local wall-clock to explicit-UTC composition for one-time intention
/// triggers. The owner enters a date and a time and picks a time zone; the
/// console resolves the offset for that date (including daylight saving) and
/// yields both the wire value and a human-readable local/UTC preview. A local
/// time that does not exist or occurs twice is refused instead of being
/// silently shifted to a different instant.
/// </summary>
internal static class IntentionTimeEntry
{
    public static IntentionTimeOption LocalOption() => new(
        "Local time (" + OffsetText(TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow)) + ")",
        UseLocal: true,
        Offset: TimeSpan.Zero);

    public static IntentionTimeOption UtcOption() => new("UTC (UTC+00:00)", UseLocal: false, Offset: TimeSpan.Zero);

    public static IntentionTimeOption StoredOffsetOption(TimeSpan offset) => new(
        "Stored offset (UTC" + OffsetText(offset) + ")", UseLocal: false, Offset: offset);

    public static bool TryCompose(
        DateTime? date,
        string? timeText,
        IntentionTimeOption? option,
        out string dueAtIso,
        out string preview,
        out string problem)
    {
        dueAtIso = string.Empty;
        preview = string.Empty;
        problem = string.Empty;
        if (date is not { } day)
        {
            problem = "Choose the date the reminder is due. Nothing was sent.";
            return false;
        }

        if (!DateTime.TryParseExact(
                (timeText ?? string.Empty).Trim(),
                new[] { "H:mm", "HH:mm" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var clock))
        {
            problem = "Write the time as HH:mm (for example 09:30, 24-hour clock). Nothing was sent.";
            return false;
        }

        if (option is null)
        {
            problem = "Choose the time zone the wall clock belongs to. Nothing was sent.";
            return false;
        }

        var local = DateTime.SpecifyKind(day.Date + clock.TimeOfDay, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (option.UseLocal)
        {
            if (TimeZoneInfo.Local.IsInvalidTime(local))
            {
                problem = "That local time does not exist: the clock jumps forward at this date. Choose UTC for this one, or a different time. Nothing was sent.";
                return false;
            }

            if (TimeZoneInfo.Local.IsAmbiguousTime(local))
            {
                problem = "That local time occurs twice: the clock moves back at this date. Choose UTC for this one so the instant is unambiguous. Nothing was sent.";
                return false;
            }

            offset = TimeZoneInfo.Local.GetUtcOffset(local);
        }
        else
        {
            offset = option.Offset;
        }

        var stamp = new DateTimeOffset(local, offset);
        dueAtIso = local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) +
            (offset == TimeSpan.Zero ? "Z" : OffsetText(offset));
        preview = local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (UTC" + OffsetText(offset) + ") = " +
            stamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
            " UTC. The reminder becomes due at that instant; notification and acknowledgement stay separate.";
        return true;
    }

    public static string OffsetText(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") +
        offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
}
