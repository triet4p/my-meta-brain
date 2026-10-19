namespace MetaBrain.Connections;

/// <summary>
/// S7-T1 decision-history projection for the owner timeline: the recorded
/// lifecycle states plus the append-only history and retained archives of one
/// subject, exactly as the service served them. This client renders answers;
/// it never derives a state, a date, or a timezone.
/// </summary>
public sealed record OwnerTimelineStamp(
    string Precision,
    DateTimeOffset? InstantUtc,
    int? LocalYear,
    int? LocalMonth,
    int? LocalDay,
    int? LocalQuarter,
    int? TimeZoneOffsetMinutes);

public sealed record OwnerTimelineChange(
    string Aspect,
    string Act,
    string FromState,
    string ToState,
    string Executor,
    DateTimeOffset RecordedAtUtc,
    string? Rationale,
    string? ReplacementResourceId);

public sealed record OwnerTimelineRetained(
    long SubjectRevision,
    string ArchiveResourceId,
    string Executor,
    DateTimeOffset RecordedAtUtc);

public sealed record OwnerTimelineDetail(
    string Id,
    string ZoneId,
    long Revision,
    string SubjectResourceId,
    string SubjectKind,
    string Publication,
    string? Question,
    string? Intention,
    bool Notified,
    DateTimeOffset? NotifiedAtUtc,
    bool Acknowledged,
    DateTimeOffset? AcknowledgedAtUtc,
    string? SupersededBy,
    OwnerTimelineStamp? Occurred,
    OwnerTimelineStamp? Recorded,
    OwnerTimelineStamp? ValidFrom,
    OwnerTimelineStamp? ValidUntil,
    OwnerTimelineStamp? ReviewAfter,
    string Executor,
    string CreationMethod,
    string? Reason,
    DateTimeOffset RecordedAtUtc,
    OwnerTimelineChange[] History,
    OwnerTimelineRetained[] RetainedRevisions);

public sealed record OwnerTimelineResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerTimelineDetail? Timeline);

public sealed record OwnerRetainedRevisionSource(
    string? SourceId,
    string? Locator,
    string Availability,
    long? SourceRevision);

public sealed record OwnerRetainedRevisionDetail(
    string Id,
    string ZoneId,
    long SubjectRevision,
    long ArchiveRevision,
    string ArchiveResourceId,
    string Kind,
    string? Title,
    string Body,
    string Author,
    string Executor,
    string CreationMethod,
    string Evidence,
    string Level,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    OwnerRetainedRevisionSource[] Sources);

public sealed record OwnerRetainedRevisionResult(
    OwnerCommandOutcome Outcome,
    string? ErrorCode,
    OwnerRetainedRevisionDetail? Retained);
