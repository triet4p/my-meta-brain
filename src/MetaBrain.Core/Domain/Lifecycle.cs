using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;


namespace MetaBrain.Core.Domain;

/// <summary>
/// How precisely a time is known. Only <see cref="Exact"/> carries an instant;
/// coarser precisions carry the wall-clock fields that are actually known, and
/// <see cref="Unknown"/> carries nothing. Nothing coarser is ever promoted
/// into a date: an unknown time stays unknown instead of being fabricated
/// from a period such as "next quarter" (R26).
/// </summary>
public enum TimestampPrecision
{
    Unknown = 0,
    Exact = 1,
    Day = 2,
    Month = 3,
    Quarter = 4,
    Year = 5
}

/// <summary>
/// One time value exactly as it is known: an exact UTC instant, the wall-clock
/// fields known at a coarser precision, or nothing at all. The original
/// timezone offset travels with the value so a local wall clock is never
/// silently reinterpreted in another zone; an unknown zone stays null. A
/// coarse stamp never claims an instant, and an <see cref="TimestampPrecision.Unknown"/>
/// stamp never claims a date.
/// </summary>
public sealed record TemporalStamp(
    TimestampPrecision Precision,
    DateTimeOffset? InstantUtc,
    int? LocalYear,
    int? LocalMonth,
    int? LocalDay,
    int? LocalQuarter,
    int? TimeZoneOffsetMinutes);

/// <summary>
/// The one-time due-instant policy for owner intention triggers. A trigger
/// requires an exact ISO-8601 date-time with an explicit <c>Z</c> or numeric
/// UTC offset; it is not a recurring wall-clock schedule or a named timezone.
/// </summary>
public static class IntentionTimeTrigger
{
    public static bool TryParse(string? text, out TemporalStamp? stamp)
    {
        stamp = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var offsetStart = text.EndsWith('Z') ? text.Length - 1 : text.Length - 6;
        if (offsetStart < 19 ||
            text[4] != '-' || text[7] != '-' ||
            text[10] != 'T' || text[13] != ':' || text[16] != ':')
        {
            return false;
        }

        for (var index = 0; index < 19; index++)
        {
            if (index is 4 or 7 or 10 or 13 or 16)
            {
                continue;
            }

            if (!IsAsciiDigit(text[index]))
            {
                return false;
            }
        }

        var cursor = 19;
        if (cursor < offsetStart && text[cursor] == '.')
        {
            cursor++;
            var fractionStart = cursor;
            while (cursor < offsetStart && IsAsciiDigit(text[cursor]))
            {
                cursor++;
            }

            if (cursor == fractionStart || cursor - fractionStart > 7)
            {
                return false;
            }
        }

        if (cursor != offsetStart)
        {
            return false;
        }

        if (text.EndsWith('Z'))
        {
            if (offsetStart != text.Length - 1)
            {
                return false;
            }
        }
        else if (text[offsetStart] is not '+' and not '-' ||
                 text[offsetStart + 3] != ':' ||
                 !IsAsciiDigit(text[offsetStart + 1]) ||
                 !IsAsciiDigit(text[offsetStart + 2]) ||
                 !IsAsciiDigit(text[offsetStart + 4]) ||
                 !IsAsciiDigit(text[offsetStart + 5]))
        {
            return false;
        }
        else
        {
            var hours = (text[offsetStart + 1] - '0') * 10 + text[offsetStart + 2] - '0';
            var minutes = (text[offsetStart + 4] - '0') * 10 + text[offsetStart + 5] - '0';
            if (hours > 14 || minutes > 59 || (hours == 14 && minutes != 0))
            {
                return false;
            }
        }

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
        {
            return false;
        }

        stamp = new TemporalStamp(
            TimestampPrecision.Exact,
            instant.ToUniversalTime(),
            null,
            null,
            null,
            null,
            (int)instant.Offset.TotalMinutes);
        return true;
    }

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';
}

/// <summary>
/// Publication lifecycle from the specification: proposal, published,
/// rejected, withdrawn. Publication state is domain state of the record, not
/// the owner-published catalog projection, and it is never inferred from
/// content, evidence, or reads.
/// </summary>
public enum PublicationState
{
    Unknown = 0,
    Proposal = 1,
    Published = 2,
    Rejected = 3,
    Withdrawn = 4
}

/// <summary>
/// Question lifecycle: open, resolved, reopened. A question is never resolved
/// by an AI answer or by an <c>addresses</c> relation: resolution is an
/// explicit recorded act with an actor and a rationale (R12).
/// </summary>
public enum QuestionState
{
    Unknown = 0,
    Open = 1,
    Resolved = 2,
    Reopened = 3
}

/// <summary>
/// Intention lifecycle: pending, done, cancelled, superseded. Notification and
/// acknowledgement are separate recorded markers on the same record and never
/// a done state (R27); supersession names the replacing item explicitly.
/// </summary>
public enum IntentionState
{
    Unknown = 0,
    Pending = 1,
    Done = 2,
    Cancelled = 3,
    Superseded = 4
}
/// <summary>
/// One owner-authored project/task context trigger. Each supplied label must
/// match the context explicitly supplied to the brief; a missing label is not
/// a wildcard and a disabled trigger is never surfaced.
/// </summary>
public sealed record IntentionContextTrigger(bool Enabled, string? Project, string? Task)
{
    public const int MaximumContextLabelLength = 160;

    public bool Matches(string? project, string? task) =>
        Enabled &&
        (Project is null || string.Equals(Project, project, StringComparison.OrdinalIgnoreCase)) &&
        (Task is null || string.Equals(Task, task, StringComparison.OrdinalIgnoreCase)) &&
        (Project is not null || Task is not null);

    public static void Validate(IntentionContextTrigger? trigger)
    {
        if (trigger is null)
        {
            return;
        }

        ValidateLabel(trigger.Project);
        ValidateLabel(trigger.Task);
        if (trigger.Project is null && trigger.Task is null)
        {
            throw new ArgumentException("A contextual intention trigger names a project, a task, or both.");
        }
    }

    private static void ValidateLabel(string? label)
    {
        if (label is not null &&
            (label.Length is 0 or > MaximumContextLabelLength ||
             !string.Equals(label, label.Trim(), StringComparison.Ordinal) ||
             label.Any(char.IsControl)))
        {
            throw new ArgumentException("A contextual intention label must be trimmed, non-empty, and within its length limit.");
        }
    }
}

/// <summary>
/// Which facet of a lifecycle record an act touched. The history records the
/// facet so a read reconstructs every state change of the subject.
/// </summary>
public enum LifecycleAspect
{
    Unknown = 0,
    Publication = 1,
    Question = 2,
    Intention = 3,
    Notification = 4,
    Temporal = 5,
    History = 6,
    Schedule = 7
}


/// <summary>
/// The explicit acts a caller can perform on a lifecycle record. Every act is
/// server-authorized through a named owner operation; no act is ever derived
/// from content, an answer, a relation, or recency.
/// </summary>
public enum LifecycleAct
{
    Unknown = 0,
    Publish = 1,
    RejectProposal = 2,
    Withdraw = 3,
    RePropose = 4,
    ResolveQuestion = 5,
    ReopenQuestion = 6,
    CompleteIntention = 7,
    CancelIntention = 8,
    SupersedeIntention = 9,
    Notify = 10,
    Acknowledge = 11,
    SetValidity = 12,
    RetainRevision = 13,
    SetIntentionDue = 14,
    ClearIntentionDue = 15,
    SetIntentionContextTrigger = 16,
    DisableIntentionContextTrigger = 17
}

/// <summary>
/// Client-supplied lifecycle draft: no identity, executor, state, or revision.
/// Identity is derived server-side from the subject memory item, executor
/// comes from the trusted <see cref="Security.AuthenticatedContext"/>, and
/// revision comes from the vault descriptor. Initial states are derived from
/// the subject's kind, so a record can never be created already resolved,
/// done, published, or superseded. Unknown temporal fields stay null.
/// </summary>
public sealed record LifecycleDraft(
    CreationMethod? CreationMethod,
    string? Reason,
    TemporalStamp? Occurred,
    TemporalStamp? Recorded,
    TemporalStamp? ValidFrom,
    TemporalStamp? ValidUntil,
    TemporalStamp? ReviewAfter);

/// <summary>
/// Client-supplied transition draft. Only the rationale, the explicit
/// supersession pointer, and the fields for the named act travel here; the
/// subject, kind, state and revision never do.
/// </summary>
public sealed record LifecycleTransitionDraft(
    string? Rationale,
    string? ReplacementResourceId,
    TemporalStamp? Occurred,
    TemporalStamp? Recorded,
    TemporalStamp? ValidFrom,
    TemporalStamp? ValidUntil,
    TemporalStamp? ReviewAfter,
    TemporalStamp? IntentionDueAt = null,
    bool ClearIntentionDueAt = false,
    IntentionContextTrigger? ContextTrigger = null,
    bool DisableContextTrigger = false);

/// <summary>
/// One retained historical revision of the subject: the exact subject
/// revision, the canonical archive resource that holds its bytes, the
/// executor who retained it, and the UTC time. Retention is an explicit
/// recorded act, so no content copy is ever made behind the owner's back.
/// </summary>
public sealed record LifecycleRetainedRevision(
    long SubjectRevision,
    string ArchiveResourceId,
    ExecutorKind Executor,
    DateTimeOffset RecordedAtUtc);

/// <summary>
/// Server-persisted lifecycle record (encrypted bytes). Revision lives in the
/// vault descriptor, not duplicated here; readers join it via ListResources.
/// One record covers one subject memory item: publication, question or
/// intention state, validity, and independently configured intention triggers.
/// </summary>
public sealed record StoredLifecycleRecord(
    int SchemaVersion,
    string Id,
    string ZoneId,
    string SubjectResourceId,
    MemoryKind SubjectKind,
    PublicationState Publication,
    QuestionState? Question,
    IntentionState? Intention,
    bool Notified,
    DateTimeOffset? NotifiedAtUtc,
    bool Acknowledged,
    DateTimeOffset? AcknowledgedAtUtc,
    string? SupersededBy,
    TemporalStamp? Occurred,
    TemporalStamp? Recorded,
    TemporalStamp? ValidFrom,
    TemporalStamp? ValidUntil,
    TemporalStamp? ReviewAfter,
    ExecutorKind Executor,
    CreationMethod CreationMethod,
    string? Reason,
    DateTimeOffset RecordedAtUtc,
    LifecycleStateChange[] History,
    LifecycleRetainedRevision[] RetainedRevisions,
    TemporalStamp? IntentionDueAt = null,
    IntentionContextTrigger? ContextTrigger = null);

/// <summary>
/// One recorded act: the facet, the act, the states it moved between, the
/// executor, the UTC time, the explicit rationale, and the explicit
/// supersession pointer. Acts that change no state (notify, acknowledge,
/// validity) record <c>none</c> states. The history is append-only and
/// bounded, so the record stays one revision-gated canonical resource.
/// </summary>
public sealed record LifecycleStateChange(
    LifecycleAspect Aspect,
    LifecycleAct Act,
    string FromState,
    string ToState,
    ExecutorKind Executor,
    DateTimeOffset RecordedAtUtc,
    string? Rationale,
    string? ReplacementResourceId);

/// <summary>
/// Owner-facing read model: stored record plus the live descriptor revision.
/// </summary>
public sealed record LifecycleRecord(
    string Id,
    string ZoneId,
    long Revision,
    int SchemaVersion,
    string SubjectResourceId,
    MemoryKind SubjectKind,
    PublicationState Publication,
    QuestionState? Question,
    IntentionState? Intention,
    bool Notified,
    DateTimeOffset? NotifiedAtUtc,
    bool Acknowledged,
    DateTimeOffset? AcknowledgedAtUtc,
    string? SupersededBy,
    TemporalStamp? Occurred,
    TemporalStamp? Recorded,
    TemporalStamp? ValidFrom,
    TemporalStamp? ValidUntil,
    TemporalStamp? ReviewAfter,
    ExecutorKind Executor,
    CreationMethod CreationMethod,
    string? Reason,
    DateTimeOffset RecordedAtUtc,
    LifecycleStateChange[] History,
    LifecycleRetainedRevision[] RetainedRevisions,
    TemporalStamp? IntentionDueAt = null,
    IntentionContextTrigger? ContextTrigger = null);

public static class LifecycleCodec
{
    public const int CurrentSchemaVersion = 3;
    public const int MaximumReasonLength = 2000;
    public const int MaximumRationaleLength = 2000;
    public const int MaximumHistoryEntries = 256;
    public const int MaximumRetainedRevisions = 64;
    public const string NoneState = "none";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static LifecycleDraft DecodeDraft(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        LifecycleDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<LifecycleDraft>(StripUtf8Preamble(json), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The lifecycle draft schema is invalid.", ex);
        }

        if (draft is null)
        {
            throw new ArgumentException("The lifecycle draft schema is invalid.");
        }

        return draft;
    }

    /// <summary>
    /// Encodes one lifecycle draft exactly as <see cref="DecodeDraft"/> reads
    /// it. The S5-T4 owner review act creates the lifecycle record of the item
    /// it just wrote, so that draft is built server-side instead of travelling
    /// through a request body; it is the same schema and the same validation.
    /// </summary>
    public static byte[] EncodeDraft(LifecycleDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return JsonSerializer.SerializeToUtf8Bytes(draft, JsonOptions);
    }

    public static LifecycleTransitionDraft DecodeTransitionDraft(byte[]? json)
    {
        if (json is null || json.Length == 0)
        {
            return new LifecycleTransitionDraft(null, null, null, null, null, null, null);
        }

        LifecycleTransitionDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<LifecycleTransitionDraft>(StripUtf8Preamble(json), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The lifecycle transition schema is invalid.", ex);
        }

        return draft ?? new LifecycleTransitionDraft(null, null, null, null, null, null, null);
    }
    public static byte[] EncodeTransitionDraft(LifecycleTransitionDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return JsonSerializer.SerializeToUtf8Bytes(draft, JsonOptions);
    }


    public static byte[] EncodeStored(StoredLifecycleRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        LifecycleValidator.ValidateStored(record);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        if (bytes.Length is <= 0 or > 1024 * 1024)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("The lifecycle record exceeds its storage limit.");
        }

        return bytes;
    }

    public static StoredLifecycleRecord DecodeStored(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        StoredLifecycleRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<StoredLifecycleRecord>(json, JsonOptions);
        }
        catch (JsonException)
        {
            throw new ArgumentException("The lifecycle record schema is invalid.");
        }

        if (record is null)
        {
            throw new ArgumentException("The lifecycle record schema is invalid.");
        }
        if (record.SchemaVersion == 1)
        {
            // v2 introduced exact time triggers; v3 adds encrypted project/task
            // context triggers. Legacy records retain validity and have neither.
            record = record with
            {
                SchemaVersion = CurrentSchemaVersion,
                IntentionDueAt = null,
                ContextTrigger = null
            };
        }
        else if (record.SchemaVersion == 2)
        {
            record = record with { SchemaVersion = CurrentSchemaVersion, ContextTrigger = null };
        }

        LifecycleValidator.ValidateStored(record);
        return record;
    }

    internal static JsonSerializerOptions Options => JsonOptions;

    private static ReadOnlySpan<byte> StripUtf8Preamble(byte[] json) =>
        json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF
            ? new ReadOnlySpan<byte>(json, 3, json.Length - 3)
            : json;
}

public static class LifecycleValidator
{
    public static void ValidateDraft(LifecycleDraft draft, string subjectResourceId)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!DomainIdentifiers.IsDomainIdentifier(subjectResourceId))
        {
            throw new ArgumentException("The lifecycle subject must be an opaque memory item identifier.");
        }

        if (draft.CreationMethod is null || draft.CreationMethod == CreationMethod.Unknown)
        {
            throw new ArgumentException("A known creation method is required.");
        }

        ValidateReason(draft.Reason);
        ValidateTime(occurred: draft.Occurred, recorded: draft.Recorded,
            validFrom: draft.ValidFrom, validUntil: draft.ValidUntil, reviewAfter: draft.ReviewAfter);
    }

    /// <summary>
    /// Temporal knowledge about the subject must agree with the subject's own
    /// envelope: an occurred stamp can only be exact when the envelope carries
    /// the instant, and a recorded stamp must be the envelope's recorded
    /// instant. The zone offset is extra knowledge the envelope cannot hold.
    /// </summary>
    public static void ValidateAgainstSubject(LifecycleDraft draft, StoredMemoryEnvelope subject)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(subject);
        ValidateAgainstSubject(draft.Occurred, draft.Recorded, draft.ValidFrom, draft.ValidUntil, draft.ReviewAfter, subject);
    }

    public static void ValidateAgainstSubject(
        LifecycleTransitionDraft transition,
        StoredMemoryEnvelope subject)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(subject);
        ValidateAgainstSubject(transition.Occurred, transition.Recorded, transition.ValidFrom, transition.ValidUntil, transition.ReviewAfter, subject);
    }

    public static void ValidateTransition(
        LifecycleTransitionDraft transition,
        LifecycleAct act,
        StoredLifecycleRecord record)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(record);
        if (act == LifecycleAct.Unknown || LifecycleTransitions.AspectOf(act) == LifecycleAspect.Unknown)
        {
            throw new ArgumentException("The lifecycle act is unknown.");
        }

        // A transition draft never re-declares subject, kind, or state, and it
        // only carries the fields its own act uses.
        if (!LifecycleTransitions.AllowsTemporalUpdate(act) &&
            (transition.Occurred is not null || transition.Recorded is not null || transition.ValidFrom is not null ||
             transition.ValidUntil is not null || transition.ReviewAfter is not null))
        {
            throw new ArgumentException("This lifecycle act does not change temporal validity.");
        }

        if (!LifecycleTransitions.AllowsScheduleUpdate(act) &&
            (transition.IntentionDueAt is not null || transition.ClearIntentionDueAt))
        {
            throw new ArgumentException("This lifecycle act does not change an intention time trigger.");
        }
        if (!LifecycleTransitions.AllowsContextTriggerUpdate(act) &&
            (transition.ContextTrigger is not null || transition.DisableContextTrigger))
        {
            throw new ArgumentException("This lifecycle act does not change an intention context trigger.");
        }

        if (act == LifecycleAct.SetIntentionContextTrigger)
        {
            if (transition.ContextTrigger is not { Enabled: true } || transition.DisableContextTrigger)
            {
                throw new ArgumentException("Enabling an intention context trigger requires its explicit project/task context.");
            }

            IntentionContextTrigger.Validate(transition.ContextTrigger);
        }
        else if (act == LifecycleAct.DisableIntentionContextTrigger &&
                 (!transition.DisableContextTrigger || transition.ContextTrigger is not null || record.ContextTrigger is not { Enabled: true }))
        {
            throw new ArgumentException("Disabling an intention context trigger requires one enabled trigger.");
        }

        if (act == LifecycleAct.SetIntentionDue)
        {
            if (transition.IntentionDueAt is null || transition.ClearIntentionDueAt)
            {
                throw new ArgumentException("Setting an intention time trigger requires one exact due instant.");
            }

            ValidateIntentionDueAt(transition.IntentionDueAt);
        }
        else if (act == LifecycleAct.ClearIntentionDue &&
                 (!transition.ClearIntentionDueAt || transition.IntentionDueAt is not null))
        {
            throw new ArgumentException("Clearing an intention time trigger carries only the explicit clear marker.");
        }

        if (!LifecycleTransitions.RequiresReplacement(act) && transition.ReplacementResourceId is not null)
        {
            throw new ArgumentException("This lifecycle act does not name a replacement item.");
        }

        if (LifecycleTransitions.RequiresRationale(act))
        {
            ValidateRationale(transition.Rationale, required: true);
        }
        else
        {
            if (transition.Rationale is not null)
            {
                ValidateRationale(transition.Rationale, required: false);
            }
        }

        if (!LifecycleTransitions.IsAllowed(act, record))
        {
            throw new ArgumentException("The lifecycle transition is not allowed from the current state.");
        }
    }

    public static void ValidateStored(StoredLifecycleRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.SchemaVersion != LifecycleCodec.CurrentSchemaVersion)
        {
            throw new ArgumentException("The lifecycle record schema version is unsupported.");
        }

        if (string.IsNullOrEmpty(record.SubjectResourceId) ||
            !DomainIdentifiers.IsDomainIdentifier(record.SubjectResourceId))
        {
            throw new ArgumentException("The lifecycle subject must be an opaque memory item identifier.");
        }

        // The record ID is derived from the subject: exactly one lifecycle
        // record per subject, and never an ID a caller chose freely.
        if (!string.Equals(record.Id, DomainIdentifiers.LifecycleIdFor(record.SubjectResourceId), StringComparison.Ordinal) ||
            !DomainIdentifiers.IsLifecycleId(record.Id))
        {
            throw new ArgumentException("The lifecycle record ID must be the subject's reserved lifecycle identifier.");
        }

        if (!DomainIdentifiers.IsSafeIdentifier(record.ZoneId))
        {
            throw new ArgumentException("The lifecycle zone must be an opaque safe identifier.");
        }

        if (!Enum.IsDefined(record.SubjectKind) || record.SubjectKind == MemoryKind.Unknown)
        {
            throw new ArgumentException("A known subject memory kind is required.");
        }

        if (!Enum.IsDefined(record.Publication) || record.Publication == PublicationState.Unknown)
        {
            throw new ArgumentException("A known publication state is required.");
        }

        if (!Enum.IsDefined(record.Executor) || record.Executor == ExecutorKind.Unknown)
        {
            throw new ArgumentException("A known executor is required.");
        }

        if (!Enum.IsDefined(record.CreationMethod) || record.CreationMethod == CreationMethod.Unknown)
        {
            throw new ArgumentException("A known creation method is required.");
        }

        ValidateReason(record.Reason);
        ValidateTime(record.Occurred, record.Recorded, record.ValidFrom, record.ValidUntil, record.ReviewAfter);
        ValidateIntentionDueAt(record.IntentionDueAt);
        IntentionContextTrigger.Validate(record.ContextTrigger);

        ValidateRetainedRevisions(record);

        if (record.Recorded is not null && record.Recorded.Precision != TimestampPrecision.Exact)
        {
            throw new ArgumentException("A recorded time is an exact instant in the subject envelope.");
        }

        if (record.RecordedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Lifecycle times must use UTC.");
        }

        if (record.RecordedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw new ArgumentException("The lifecycle record time cannot be in the future.");
        }

        // Kind applicability: the question lifecycle only exists on a question
        // subject, the intention lifecycle only on an intention subject.
        var questionApplies = record.SubjectKind == MemoryKind.Question;
        if (questionApplies)
        {
            if (record.Question is null || !Enum.IsDefined(record.Question.Value) || record.Question.Value == QuestionState.Unknown)
            {
                throw new ArgumentException("A question subject carries a known question state.");
            }
        }
        else if (record.Question is not null)
        {
            throw new ArgumentException("Only a question subject carries a question state.");
        }

        var intentionApplies = record.SubjectKind == MemoryKind.Intention;
        if (intentionApplies)
        {
            if (record.Intention is null || !Enum.IsDefined(record.Intention.Value) || record.Intention.Value == IntentionState.Unknown)
            {
                throw new ArgumentException("An intention subject carries a known intention state.");
            }
        }
        else if (record.Intention is not null)
        {
            throw new ArgumentException("Only an intention subject carries an intention state.");
        }
        if (!intentionApplies && record.IntentionDueAt is not null)
        {
            throw new ArgumentException("Only an intention subject carries a time trigger.");
        }
        if (!intentionApplies && record.ContextTrigger is not null)
        {
            throw new ArgumentException("Only an intention subject carries a context trigger.");
        }


        // Notification markers only exist on an intention subject and always
        // carry the time the marker was recorded.
        if (!intentionApplies && (record.Notified || record.NotifiedAtUtc is not null || record.Acknowledged || record.AcknowledgedAtUtc is not null))
        {
            throw new ArgumentException("Only an intention subject carries notification markers.");
        }

        if (record.Notified != (record.NotifiedAtUtc is not null) || record.Acknowledged != (record.AcknowledgedAtUtc is not null))
        {
            throw new ArgumentException("A notification marker carries its recorded time.");
        }

        if (record.NotifiedAtUtc is { } notifiedAt)
        {
            if (notifiedAt.Offset != TimeSpan.Zero || notifiedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                throw new ArgumentException("A notification time must be UTC and not in the future.");
            }
        }

        if (record.AcknowledgedAtUtc is { } acknowledgedAt)
        {
            if (acknowledgedAt.Offset != TimeSpan.Zero || acknowledgedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                throw new ArgumentException("An acknowledgement time must be UTC and not in the future.");
            }
        }

        // Supersession is explicit: a superseded intention names its
        // replacement, and no other state names one.
        if (record.Intention == IntentionState.Superseded && record.SupersededBy is null)
        {
            throw new ArgumentException("A superseded intention names the item that replaces it.");
        }

        if (record.SupersededBy is not null)
        {
            if (record.Intention != IntentionState.Superseded)
            {
                throw new ArgumentException("Only a superseded intention names a replacement item.");
            }

            if (!DomainIdentifiers.IsDomainIdentifier(record.SupersededBy))
            {
                throw new ArgumentException("The replacement item must be an opaque memory item identifier.");
            }
        }

        ValidateHistory(record);
    }

    /// <summary>
    /// Retained revisions are append-only, bounded, and always addressed by the
    /// archive ID derived from the subject and the exact revision retained.
    /// </summary>
    private static void ValidateRetainedRevisions(StoredLifecycleRecord record)
    {
        var retained = record.RetainedRevisions ?? Array.Empty<LifecycleRetainedRevision>();
        if (retained.Length > LifecycleCodec.MaximumRetainedRevisions)
        {
            throw new ArgumentException("The lifecycle record retains too many revisions.");
        }

        var seen = new HashSet<long>();
        foreach (var entry in retained)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.SubjectRevision < 1 || !seen.Add(entry.SubjectRevision))
            {
                throw new ArgumentException("A retained revision names one positive subject revision exactly once.");
            }

            if (string.IsNullOrEmpty(entry.ArchiveResourceId) ||
                !string.Equals(entry.ArchiveResourceId,
                    DomainIdentifiers.ArchiveIdFor(record.SubjectResourceId, entry.SubjectRevision), StringComparison.Ordinal) ||
                !DomainIdentifiers.IsArchiveId(entry.ArchiveResourceId))
            {
                throw new ArgumentException("A retained revision is stored under the archive ID derived from its subject and revision.");
            }

            if (!Enum.IsDefined(entry.Executor) || entry.Executor == ExecutorKind.Unknown)
            {
                throw new ArgumentException("A retained revision names the executor who retained it.");
            }

            if (entry.RecordedAtUtc.Offset != TimeSpan.Zero || entry.RecordedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                throw new ArgumentException("A retained revision time must be UTC and not in the future.");
            }
        }
    }

    private static void ValidateHistory(StoredLifecycleRecord record)
    {
        var history = record.History ?? Array.Empty<LifecycleStateChange>();
        if (history.Length > LifecycleCodec.MaximumHistoryEntries)
        {
            throw new ArgumentException("The lifecycle history is full; the record cannot be extended further.");
        }

        var lastPublication = PublicationState.Unknown;
        var lastQuestion = QuestionState.Unknown;
        var lastIntention = IntentionState.Unknown;
        var previous = record.RecordedAtUtc;
        foreach (var change in history)
        {
            ArgumentNullException.ThrowIfNull(change);
            if (change.Aspect == LifecycleAspect.Unknown || LifecycleTransitions.AspectOf(change.Act) != change.Aspect)
            {
                throw new ArgumentException("A lifecycle history entry names a known aspect for its act.");
            }

            if (!Enum.IsDefined(change.Executor) || change.Executor == ExecutorKind.Unknown)
            {
                throw new ArgumentException("A lifecycle history entry names a known executor.");
            }

            if (change.RecordedAtUtc.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException("Lifecycle history times must use UTC.");
            }

            if (change.RecordedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5) || change.RecordedAtUtc < previous)
            {
                throw new ArgumentException("A lifecycle history entry time is out of order or in the future.");
            }

            if (LifecycleTransitions.RequiresRationale(change.Act))
            {
                ValidateRationale(change.Rationale, required: true);
            }
            else if (change.Rationale is not null)
            {
                ValidateRationale(change.Rationale, required: false);
            }

            if (LifecycleTransitions.RequiresReplacement(change.Act))
            {
                if (change.ReplacementResourceId is null ||
                    !DomainIdentifiers.IsDomainIdentifier(change.ReplacementResourceId))
                {
                    throw new ArgumentException("A supersession history entry names the replacement item.");
                }
            }
            else if (change.ReplacementResourceId is not null)
            {
                throw new ArgumentException("Only a supersession history entry names a replacement item.");
            }

            if (!LifecycleTransitions.ChangesState(change.Act))
            {
                if (!string.Equals(change.FromState, LifecycleCodec.NoneState, StringComparison.Ordinal) ||
                    !string.Equals(change.ToState, LifecycleCodec.NoneState, StringComparison.Ordinal))
                {
                    throw new ArgumentException("A history entry that changes no state records no states.");
                }
            }
            else
            {
                if (string.Equals(change.FromState, LifecycleCodec.NoneState, StringComparison.Ordinal) ||
                    string.Equals(change.ToState, LifecycleCodec.NoneState, StringComparison.Ordinal) ||
                    !IsStateEdge(change.Aspect, change.Act, change.FromState, change.ToState))
                {
                    throw new ArgumentException("A lifecycle history entry records the states its act allows.");
                }

                switch (change.Aspect)
                {
                    case LifecycleAspect.Publication:
                        lastPublication = LifecycleTokens.ParsePublication(change.ToState);
                        break;
                    case LifecycleAspect.Question:
                        lastQuestion = LifecycleTokens.ParseQuestion(change.ToState);
                        break;
                    default:
                        lastIntention = LifecycleTokens.ParseIntention(change.ToState);
                        break;
                }
            }

            previous = change.RecordedAtUtc;
        }

        // The stored state and the recorded history can never disagree.
        if (lastPublication != PublicationState.Unknown && lastPublication != record.Publication)
        {
            throw new ArgumentException("The stored publication state disagrees with the recorded history.");
        }

        if (questionAppliesCheck(record) && lastQuestion != QuestionState.Unknown && lastQuestion != record.Question)
        {
            throw new ArgumentException("The stored question state disagrees with the recorded history.");
        }

        if (intentionAppliesCheck(record) && lastIntention != IntentionState.Unknown && lastIntention != record.Intention)
        {
            throw new ArgumentException("The stored intention state disagrees with the recorded history.");
        }

        static bool questionAppliesCheck(StoredLifecycleRecord value) => value.SubjectKind == MemoryKind.Question;
        static bool intentionAppliesCheck(StoredLifecycleRecord value) => value.SubjectKind == MemoryKind.Intention;
    }

    private static bool IsStateEdge(LifecycleAspect aspect, LifecycleAct act, string from, string to) => aspect switch
    {
        LifecycleAspect.Publication => LifecycleTransitions.AllowsPublicationEdge(act,
            LifecycleTokens.ParsePublication(from), LifecycleTokens.ParsePublication(to)),
        LifecycleAspect.Question => LifecycleTransitions.AllowsQuestionEdge(act,
            LifecycleTokens.ParseQuestion(from), LifecycleTokens.ParseQuestion(to)),
        LifecycleAspect.Intention => LifecycleTransitions.AllowsIntentionEdge(act,
            LifecycleTokens.ParseIntention(from), LifecycleTokens.ParseIntention(to)),
        _ => false
    };

    private static void ValidateAgainstSubject(
        TemporalStamp? occurred,
        TemporalStamp? recorded,
        TemporalStamp? validFrom,
        TemporalStamp? validUntil,
        TemporalStamp? reviewAfter,
        StoredMemoryEnvelope subject)
    {
        ValidateTime(occurred, recorded, validFrom, validUntil, reviewAfter);
        if (recorded is not null &&
            (recorded.Precision != TimestampPrecision.Exact ||
             recorded.InstantUtc != subject.RecordedAtUtc))
        {
            throw new ArgumentException("A recorded lifecycle time must be the subject envelope's recorded instant.");
        }

        if (occurred is not null && subject.OccurredAtUtc is not null &&
            (occurred.Precision != TimestampPrecision.Exact || occurred.InstantUtc != subject.OccurredAtUtc))
        {
            throw new ArgumentException("An occurred lifecycle time must be the subject envelope's occurred instant.");
        }
    }

    private static void ValidateTime(
        TemporalStamp? occurred,
        TemporalStamp? recorded,
        TemporalStamp? validFrom,
        TemporalStamp? validUntil,
        TemporalStamp? reviewAfter)
    {
        ValidateStamp(occurred);
        ValidateStamp(recorded);
        ValidateStamp(validFrom);
        ValidateStamp(validUntil);
        ValidateStamp(reviewAfter);
        ValidateOrder(validFrom, validUntil);
    }
    private static void ValidateIntentionDueAt(TemporalStamp? stamp)
    {
        if (stamp is null)
        {
            return;
        }

        ValidateStamp(stamp);
        if (stamp.Precision != TimestampPrecision.Exact || stamp.InstantUtc is null ||
            stamp.TimeZoneOffsetMinutes is null)
        {
            throw new ArgumentException("An intention time trigger requires one exact instant and its explicit UTC offset.");
        }
    }


    private static void ValidateStamp(TemporalStamp? stamp)
    {
        if (stamp is null)
        {
            return;
        }

        switch (stamp.Precision)
        {
            case TimestampPrecision.Unknown:
                if (stamp.InstantUtc is not null || stamp.LocalYear is not null || stamp.LocalMonth is not null ||
                    stamp.LocalDay is not null || stamp.LocalQuarter is not null)
                {
                    throw new ArgumentException("An unknown time carries no date fields; unknown is never fabricated.");
                }

                break;
            case TimestampPrecision.Exact:
                if (stamp.InstantUtc is null || stamp.InstantUtc.Value.Offset != TimeSpan.Zero ||
                    stamp.LocalYear is not null || stamp.LocalMonth is not null || stamp.LocalDay is not null ||
                    stamp.LocalQuarter is not null)
                {
                    throw new ArgumentException("An exact time carries a UTC instant and no wall-clock fields.");
                }

                break;
            case TimestampPrecision.Day:
                if (stamp.InstantUtc is not null || stamp.LocalQuarter is not null ||
                    !IsCalendarDay(stamp.LocalYear, stamp.LocalMonth, stamp.LocalDay))
                {
                    throw new ArgumentException("A day time carries a known calendar day.");
                }

                break;
            case TimestampPrecision.Month:
                if (stamp.InstantUtc is not null || stamp.LocalDay is not null || stamp.LocalQuarter is not null ||
                    stamp.LocalYear is null || stamp.LocalMonth is not { } month || month is < 1 or > 12)
                {
                    throw new ArgumentException("A month time carries a known year and month.");
                }

                break;
            case TimestampPrecision.Quarter:
                if (stamp.InstantUtc is not null || stamp.LocalDay is not null || stamp.LocalMonth is not null ||
                    stamp.LocalYear is null || stamp.LocalQuarter is not { } quarter || quarter is < 1 or > 4)
                {
                    throw new ArgumentException("A quarter time carries a known year and quarter.");
                }

                break;
            default:
                if (stamp.InstantUtc is not null || stamp.LocalDay is not null || stamp.LocalMonth is not null ||
                    stamp.LocalQuarter is not null || stamp.LocalYear is null)
                {
                    throw new ArgumentException("A year time carries a known year.");
                }

                break;
        }

        if (stamp.TimeZoneOffsetMinutes is { } offset && (offset < -840 || offset > 840))
        {
            throw new ArgumentException("A timezone offset must be a real UTC offset.");
        }
    }

    private static bool IsCalendarDay(int? year, int? month, int? day) =>
        year is not null && month is { } monthValue && monthValue is >= 1 and <= 12 &&
        day is { } dayValue && dayValue >= 1 && dayValue <= DateTime.DaysInMonth(year.Value, monthValue);

    /// <summary>
    /// Two validity bounds cannot be in the wrong order. Instants compare
    /// directly; coarser bounds compare by the widest period they can cover,
    /// so a coarse stamp is never narrowed into a fabricated date to compare.
    /// </summary>
    private static void ValidateOrder(TemporalStamp? validFrom, TemporalStamp? validUntil)
    {
        if (validFrom is null || validUntil is null)
        {
            return;
        }

        var fromEarliest = EarliestYear(validFrom);
        var untilLatest = LatestYear(validUntil);
        if (fromEarliest is not null && untilLatest is not null && fromEarliest.Value > untilLatest.Value)
        {
            throw new ArgumentException("A validity window cannot start after it ends.");
        }
    }

    private static int? EarliestYear(TemporalStamp stamp) => stamp.Precision switch
    {
        TimestampPrecision.Exact or TimestampPrecision.Day or TimestampPrecision.Month or TimestampPrecision.Quarter
            or TimestampPrecision.Year => stamp.Precision == TimestampPrecision.Exact
                ? stamp.InstantUtc!.Value.UtcDateTime.Year
                : stamp.LocalYear,
        _ => null
    };

    private static int? LatestYear(TemporalStamp stamp) => stamp.Precision switch
    {
        TimestampPrecision.Year or TimestampPrecision.Quarter or TimestampPrecision.Month or TimestampPrecision.Day
            => stamp.LocalYear,
        TimestampPrecision.Exact => stamp.InstantUtc!.Value.UtcDateTime.Year,
        _ => null
    };

    private static void ValidateReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason) || reason!.Length > LifecycleCodec.MaximumReasonLength ||
            reason.Any(char.IsControl))
        {
            throw new ArgumentException("The lifecycle reason must be 1-2000 characters without control characters.");
        }
    }

    private static void ValidateRationale(string? rationale, bool required)
    {
        if (rationale is null)
        {
            if (required)
            {
                throw new ArgumentException("This lifecycle transition requires an explicit rationale.");
            }

            return;
        }

        if (rationale.Length is 0 or > LifecycleCodec.MaximumRationaleLength || rationale.Any(char.IsControl))
        {
            throw new ArgumentException("The lifecycle rationale must be 1-2000 characters without control characters.");
        }
    }
}

/// <summary>
/// Wire tokens for the domain states. One token per state, in one place, so a
/// stored record, a history entry, and a served reply never disagree.
/// </summary>
public static class LifecycleTokens
{
    public static string Publication(PublicationState state) => state switch
    {
        PublicationState.Proposal => "proposal",
        PublicationState.Published => "published",
        PublicationState.Rejected => "rejected",
        PublicationState.Withdrawn => "withdrawn",
        _ => "unknown"
    };

    public static string Question(QuestionState state) => state switch
    {
        QuestionState.Open => "open",
        QuestionState.Resolved => "resolved",
        QuestionState.Reopened => "reopened",
        _ => "unknown"
    };

    public static string Intention(IntentionState state) => state switch
    {
        IntentionState.Pending => "pending",
        IntentionState.Done => "done",
        IntentionState.Cancelled => "cancelled",
        IntentionState.Superseded => "superseded",
        _ => "unknown"
    };

    public static string Aspect(LifecycleAspect aspect) => aspect switch
    {
        LifecycleAspect.Publication => "publication",
        LifecycleAspect.Question => "question",
        LifecycleAspect.Intention => "intention",
        LifecycleAspect.Notification => "notification",
        LifecycleAspect.Temporal => "temporal",
        LifecycleAspect.History => "history",
        LifecycleAspect.Schedule => "schedule",
        _ => "unknown"
    };

    public static PublicationState ParsePublication(string token) => token switch
    {
        "proposal" => PublicationState.Proposal,
        "published" => PublicationState.Published,
        "rejected" => PublicationState.Rejected,
        "withdrawn" => PublicationState.Withdrawn,
        "unknown" => PublicationState.Unknown,
        _ => throw new ArgumentException("The publication state token is unknown.")
    };

    public static QuestionState ParseQuestion(string token) => token switch
    {
        "open" => QuestionState.Open,
        "resolved" => QuestionState.Resolved,
        "reopened" => QuestionState.Reopened,
        "unknown" => QuestionState.Unknown,
        _ => throw new ArgumentException("The question state token is unknown.")
    };

    public static IntentionState ParseIntention(string token) => token switch
    {
        "pending" => IntentionState.Pending,
        "done" => IntentionState.Done,
        "cancelled" => IntentionState.Cancelled,
        "superseded" => IntentionState.Superseded,
        "unknown" => IntentionState.Unknown,
        _ => throw new ArgumentException("The intention state token is unknown.")
    };

    public static string Subject(MemoryKind kind) => kind switch
    {
        MemoryKind.Claim => "claim",
        MemoryKind.Observation => "observation",
        MemoryKind.Question => "question",
        MemoryKind.Hypothesis => "hypothesis",
        MemoryKind.Decision => "decision",
        MemoryKind.Lesson => "lesson",
        MemoryKind.Procedure => "procedure",
        MemoryKind.Synthesis => "synthesis",
        MemoryKind.Preference => "preference",
        MemoryKind.Intention => "intention",
        _ => "unknown"
    };
}

/// <summary>
/// The one lifecycle policy table shared by the service, the validator, and
/// the history check. Publication returns to proposal only from an explicit
/// rejection or withdrawal, so a published record is never silently reverted;
/// questions resolve and reopen explicitly; intentions complete, cancel, or
/// name a replacement, and done/cancelled/superseded are terminal because the
/// record's history already carries every act with its actor and rationale.
/// Notify and acknowledge only set markers and never a state, so a reminder is
/// never completion. Nothing here is ever derived from content, an answer, a
/// relation, or recency.
/// </summary>
public static class LifecycleTransitions
{
    public static LifecycleAspect AspectOf(LifecycleAct act) => act switch
    {
        LifecycleAct.Publish or LifecycleAct.RejectProposal or LifecycleAct.Withdraw or LifecycleAct.RePropose
            => LifecycleAspect.Publication,
        LifecycleAct.ResolveQuestion or LifecycleAct.ReopenQuestion => LifecycleAspect.Question,
        LifecycleAct.CompleteIntention or LifecycleAct.CancelIntention or LifecycleAct.SupersedeIntention
            => LifecycleAspect.Intention,
        LifecycleAct.Notify or LifecycleAct.Acknowledge => LifecycleAspect.Notification,
        LifecycleAct.SetValidity => LifecycleAspect.Temporal,
        LifecycleAct.SetIntentionDue or LifecycleAct.ClearIntentionDue or
        LifecycleAct.SetIntentionContextTrigger or LifecycleAct.DisableIntentionContextTrigger => LifecycleAspect.Schedule,
        LifecycleAct.RetainRevision => LifecycleAspect.History,
        _ => LifecycleAspect.Unknown
    };

    public static bool ChangesState(LifecycleAct act) => act
        is LifecycleAct.Publish
        or LifecycleAct.RejectProposal
        or LifecycleAct.Withdraw
        or LifecycleAct.RePropose
        or LifecycleAct.ResolveQuestion
        or LifecycleAct.ReopenQuestion
        or LifecycleAct.CompleteIntention
        or LifecycleAct.CancelIntention
        or LifecycleAct.SupersedeIntention;

    public static bool RequiresRationale(LifecycleAct act) => act
        is LifecycleAct.RejectProposal
        or LifecycleAct.Withdraw
        or LifecycleAct.ResolveQuestion
        or LifecycleAct.ReopenQuestion
        or LifecycleAct.CompleteIntention
        or LifecycleAct.CancelIntention
        or LifecycleAct.SupersedeIntention;

    public static bool RequiresReplacement(LifecycleAct act) => act == LifecycleAct.SupersedeIntention;

    public static bool AllowsTemporalUpdate(LifecycleAct act) => act == LifecycleAct.SetValidity;
    public static bool AllowsScheduleUpdate(LifecycleAct act) =>
        act is LifecycleAct.SetIntentionDue or LifecycleAct.ClearIntentionDue;
    public static bool AllowsContextTriggerUpdate(LifecycleAct act) =>
        act is LifecycleAct.SetIntentionContextTrigger or LifecycleAct.DisableIntentionContextTrigger;


    /// <summary>
    /// Retaining a revision is allowed from any state: a superseded, cancelled,
    /// or done subject still has a history worth retrieving, and the archive
    /// is written for a revision that exists now.
    /// </summary>
    public static bool AllowsRetention(LifecycleAct act) => act == LifecycleAct.RetainRevision;

    public static bool AllowsPublicationEdge(LifecycleAct act, PublicationState from, PublicationState to) => act switch
    {
        LifecycleAct.Publish => from == PublicationState.Proposal && to == PublicationState.Published,
        LifecycleAct.RejectProposal => from == PublicationState.Proposal && to == PublicationState.Rejected,
        LifecycleAct.Withdraw => from == PublicationState.Published && to == PublicationState.Withdrawn,
        LifecycleAct.RePropose => to == PublicationState.Proposal &&
            (from == PublicationState.Rejected || from == PublicationState.Withdrawn),
        _ => false
    };

    public static bool AllowsQuestionEdge(LifecycleAct act, QuestionState from, QuestionState to) => act switch
    {
        LifecycleAct.ResolveQuestion => to == QuestionState.Resolved &&
            (from == QuestionState.Open || from == QuestionState.Reopened),
        LifecycleAct.ReopenQuestion => from == QuestionState.Resolved && to == QuestionState.Reopened,
        _ => false
    };

    public static bool AllowsIntentionEdge(LifecycleAct act, IntentionState from, IntentionState to) => act switch
    {
        LifecycleAct.CompleteIntention => from == IntentionState.Pending && to == IntentionState.Done,
        LifecycleAct.CancelIntention => from == IntentionState.Pending && to == IntentionState.Cancelled,
        LifecycleAct.SupersedeIntention => from == IntentionState.Pending && to == IntentionState.Superseded,
        _ => false
    };

    /// <summary>
    /// Whether an act is legal from a record's current states. An act on an
    /// aspect the subject kind does not carry is refused, so a question state
    /// can never be written onto a claim or an intention.
    /// </summary>
    public static bool IsAllowed(LifecycleAct act, StoredLifecycleRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return AspectOf(act) switch
        {
            LifecycleAspect.Publication => AllowsPublicationEdge(act, record.Publication, TargetPublication(act)),
            LifecycleAspect.Question => record.Question is { } question &&
                AllowsQuestionEdge(act, question, TargetQuestion(act)),
            LifecycleAspect.Intention => record.Intention is { } intention &&
                AllowsIntentionEdge(act, intention, TargetIntention(act)),
            LifecycleAspect.Notification => record.Intention is not null,
            LifecycleAspect.History => true,
            LifecycleAspect.Temporal => true,
            LifecycleAspect.Schedule => record.Intention == IntentionState.Pending,
            _ => false
        };
    }

    public static PublicationState TargetPublication(LifecycleAct act) => act switch
    {
        LifecycleAct.Publish => PublicationState.Published,
        LifecycleAct.RejectProposal => PublicationState.Rejected,
        LifecycleAct.Withdraw => PublicationState.Withdrawn,
        LifecycleAct.RePropose => PublicationState.Proposal,
        _ => PublicationState.Unknown
    };

    public static QuestionState TargetQuestion(LifecycleAct act) => act switch
    {
        LifecycleAct.ResolveQuestion => QuestionState.Resolved,
        LifecycleAct.ReopenQuestion => QuestionState.Reopened,
        _ => QuestionState.Unknown
    };

    public static IntentionState TargetIntention(LifecycleAct act) => act switch
    {
        LifecycleAct.CompleteIntention => IntentionState.Done,
        LifecycleAct.CancelIntention => IntentionState.Cancelled,
        LifecycleAct.SupersedeIntention => IntentionState.Superseded,
        _ => IntentionState.Unknown
    };
}
