using System.Globalization;
using System.Text.Json;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// S9-T4 the exact memory drafts the owner console sends: the same JSON the
/// shared domain codec reads, built from the owner's typed fields — the same
/// bytes the CLI friendly options build. The service remains the only
/// validator and its refusal is reported verbatim.
/// </summary>
internal static class OwnerMemoryDrafts
{
    internal static byte[]? BuildCreate(
        string? kind,
        string? author,
        string? method,
        string? evidence,
        string? level,
        string? title,
        string body)
    {
        // An empty title box is no title at all: the shared codec treats null
        // as absent, while an empty string fails validation. Same absent-field
        // rule as the lifecycle drafts; the CLI sends null when omitted.
        var cleanTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = kind ?? "claim",
                author = author ?? "user",
                creationMethod = method ?? "directWrite",
                evidence = evidence ?? "unverified",
                level = level ?? "l1",
                title = cleanTitle,
                body,
                sources = Array.Empty<object>(),
                occurredAtUtc = (DateTimeOffset?)null,
                recordedAtUtc = DateTimeOffset.UtcNow,
                applicability = (object?)null
            });
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static byte[] BuildRevise(OwnerItemDetail item, string body)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(body);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = item.Kind,
            author = item.Author,
            creationMethod = item.CreationMethod,
            evidence = item.Evidence,
            level = item.Level,
            title = item.Title,
            body,
            sources = item.Sources.Select(source => new
            {
                sourceId = source.SourceId,
                locator = source.Locator,
                availability = source.Availability,
                sourceRevision = source.SourceRevision,
                span = source.SpanStart is { } start && source.SpanEnd is { } end
                    ? new { start, end }
                    : null
            }).ToArray(),
            occurredAtUtc = item.OccurredAtUtc?.ToUniversalTime(),
            recordedAtUtc = DateTimeOffset.UtcNow,
            applicability = item.Project is null && item.Environment is null && item.Version is null
                ? null
                : new { project = item.Project, environment = item.Environment, version = item.Version }
        });
    }

    internal static byte[]? BuildFeedback(string targetResourceId, long targetRevision, string kind, string? note)
    {
        if (string.IsNullOrWhiteSpace(targetResourceId) || targetRevision < 1 || string.IsNullOrWhiteSpace(kind))
        {
            return null;
        }

        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                targetResourceId,
                targetRevision,
                kind,
                note,
                creationMethod = "directWrite"
            });
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static byte[] BuildLifecycleCreate(string? reason, string? method)
    {
        // An empty box is no reason/method at all: the shared codec treats a
        // null as absent, while an empty string fails validation. The CLI
        // friendly path builds the same null for an omitted option.
        var cleanReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var cleanMethod = string.IsNullOrWhiteSpace(method) ? null : method.Trim();
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            creationMethod = cleanMethod ?? "directWrite",
            reason = cleanReason,
            occurred = (object?)null,
            recorded = (object?)null,
            validFrom = (object?)null,
            validUntil = (object?)null,
            reviewAfter = (object?)null
        });
    }

    internal static byte[] BuildLifecycleTransition(string? reason, string? replacementResourceId)
    {
        return BuildLifecycleTransition(reason, replacementResourceId, null, null, null);
    }

    /// <summary>
    /// Builds the exact transition draft the shared domain codec reads, with
    /// the owner's explicit validity instants when the act changes temporal
    /// validity (SetValidity only — every other act carries null stamps like
    /// the CLI friendly path). Each stamp is an exact UTC instant the owner
    /// typed; the service validates precision, order and subject fit.
    /// </summary>
    internal static byte[] BuildLifecycleTransition(
        string? reason,
        string? replacementResourceId,
        DateTimeOffset? validFromUtc,
        DateTimeOffset? validUntilUtc,
        DateTimeOffset? reviewAfterUtc)
    {
        // Same absent-field rule as the create draft: an empty rationale box
        // is no rationale (publish needs none), and an empty replacement box
        // names no item (only a supersession names one). The CLI sends null
        // for both when the option is omitted.
        var rationale = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var replacement = string.IsNullOrWhiteSpace(replacementResourceId) ? null : replacementResourceId.Trim();
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            rationale,
            replacementResourceId = replacement,
            occurred = (object?)null,
            recorded = (object?)null,
            validFrom = Stamp(validFromUtc),
            validUntil = Stamp(validUntilUtc),
            reviewAfter = Stamp(reviewAfterUtc),
            intentionDueAt = (object?)null,
            clearIntentionDueAt = false,
            contextTrigger = (object?)null,
            disableContextTrigger = false
        });
    }

    private static object? Stamp(DateTimeOffset? instantUtc) =>
        instantUtc is null
            ? null
            : new { precision = "exact", instantUtc, zoneOffsetMinutes = 0 };
}
