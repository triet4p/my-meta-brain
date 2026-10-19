using MetaBrain.Core.Domain;

namespace MetaBrain.Core.Security;

/// <summary>
/// S7-T4 owner recall policy for one subject memory item: how prominently the
/// item may be surfaced, whether the owner set it aside, and whether the owner
/// protected it from being forgotten.
/// <para>
/// The policy is owner-authored curation, not truth. It never changes an
/// item's body, evidence, verification, citations, times or attribution, and
/// it never deletes anything: a demoted, pinned or archived item keeps every
/// byte it had. The only thing the policy changes is the order in which the
/// item is offered and whether it is offered at all.
/// </para>
/// <para>
/// Transitions are explicit owner acts with a rationale, an actor and a UTC
/// time, exactly like the lifecycle acts: nothing is derived from content, an
/// answer, a relation, a read count or recency. A read never changes this
/// state, so reading an item more often can never make it rank higher or
/// raise its truth.
/// </para>
/// </summary>
public enum RecallDisposition
{
    /// <summary>No owner decision: the item is offered like every other item.</summary>
    Normal = 0,

    /// <summary>
    /// The owner lowered this item's priority. It is still offered and still
    /// findable, but it sorts below the items the owner did not demote.
    /// </summary>
    Demoted = 1,

    /// <summary>
    /// The owner protected this item: it is ranked with the protected items
    /// first, and it cannot be demoted or set aside without being released
    /// from that protection first.
    /// </summary>
    Pinned = 2,

    /// <summary>
    /// The owner set this item aside. It is not offered by default, but it is
    /// never deleted: it stays in the vault, stays readable by its own ID, and
    /// comes back when the owner restores it.
    /// </summary>
    Archived = 3
}

/// <summary>
/// The explicit owner acts on a subject's recall policy (R28). Every act is a
/// named, owner-initiated decision; no act is ever derived from content, an
/// answer, a relation, a read count or recency.
/// </summary>
public enum RecallAct
{
    /// <summary>An unknown act.</summary>
    Unknown = 0,

    /// <summary>Lower this item's priority. It stays findable.</summary>
    Demote = 1,

    /// <summary>Protect this item: it ranks first and cannot be set aside.</summary>
    Pin = 2,

    /// <summary>Set this item aside: it is not offered by default.</summary>
    Archive = 3,

    /// <summary>Return this item to the owner's ordinary recall set.</summary>
    Restore = 4
}

/// <summary>
/// One recorded owner act on a subject's recall policy: the disposition the
/// act moved the subject from, the disposition it moved it to, the executor,
/// the UTC time and the owner's own rationale.
/// </summary>
public sealed record RecallPolicyChange(
    RecallDisposition From,
    RecallDisposition To,
    ExecutorKind Executor,
    DateTimeOffset RecordedAtUtc,
    string? Rationale);

/// <summary>
/// The owner recall policy of one subject memory item, with the bounded act
/// history that explains how the current disposition was reached.
/// </summary>
public sealed record RecallPolicyEntry(
    string SubjectResourceId,
    RecallDisposition Disposition,
    ExecutorKind Executor,
    DateTimeOffset RecordedAtUtc,
    string? Rationale,
    RecallPolicyChange[] History)
{
    public const int MaximumHistory = 32;
}

/// <summary>
/// The whole owner recall policy of a vault: one entry per subject the owner
/// has made a decision about, plus the schema version of the stored file.
/// <para>
/// This is one encrypted owner state beside the scope-grant, catalog and
/// source-enrollment states. It is not canonical content: it never appears in
/// the manifest, never changes an item's revision, and never invalidates the
/// private index. It only records what the owner decided about how an item is
/// offered, so a demote or an archive takes effect on the next query instead
/// of after an index rebuild.
/// </para>
/// </summary>
public sealed record OwnerRecallPolicyState(
    int SchemaVersion,
    RecallPolicyEntry[] Entries)
{
    public const int CurrentSchemaVersion = 1;

    public static OwnerRecallPolicyState Empty { get; } = new(
        CurrentSchemaVersion, Array.Empty<RecallPolicyEntry>());
}

/// <summary>
/// Stable wire tokens for the owner recall policy, so every surface names the
/// same owner decision with the same word.
/// </summary>
public static class RecallTokens
{
    /// <summary>The owner decision token of one disposition.</summary>
    public static string Disposition(RecallDisposition disposition) => disposition switch
    {
        RecallDisposition.Demoted => "demoted",
        RecallDisposition.Pinned => "pinned",
        RecallDisposition.Archived => "archived",
        _ => "normal"
    };

    /// <summary>Parses one owner decision token; an unknown token is refused.</summary>
    public static bool TryParseDisposition(string? token, out RecallDisposition disposition)
    {
        switch (token)
        {
            case "normal":
                disposition = RecallDisposition.Normal;
                return true;
            case "demoted":
                disposition = RecallDisposition.Demoted;
                return true;
            case "pinned":
                disposition = RecallDisposition.Pinned;
                return true;
            case "archived":
                disposition = RecallDisposition.Archived;
                return true;
            default:
                disposition = RecallDisposition.Normal;
                return false;
        }
    }

    /// <summary>The owner act token of one named act.</summary>
    public static string Act(RecallAct act) => act switch
    {
        RecallAct.Demote => "demote",
        RecallAct.Pin => "pin",
        RecallAct.Archive => "archive",
        RecallAct.Restore => "restore",
        _ => "unknown"
    };

    /// <summary>Parses one owner act token; an unknown token is refused.</summary>
    public static bool TryParseAct(string? token, out RecallAct act)
    {
        switch (token)
        {
            case "demote":
                act = RecallAct.Demote;
                return true;
            case "pin":
                act = RecallAct.Pin;
                return true;
            case "archive":
                act = RecallAct.Archive;
                return true;
            case "restore":
                act = RecallAct.Restore;
                return true;
            default:
                act = RecallAct.Unknown;
                return false;
        }
    }
}

/// <summary>
/// Encrypted persistence for the owner recall policy, implemented by the vault
/// lifecycle so a policy act shares the same atomic update and lock/epoch
/// semantics as scope grants, the owner catalog and source enrollment.
/// </summary>
public interface IRecallPolicyPersistence
{
    OwnerRecallPolicyState LoadRecallPolicyState();

    T UpdateRecallPolicyState<T>(
        Func<OwnerRecallPolicyState, (OwnerRecallPolicyState State, T Result)> update);
}

/// <summary>
/// The owner recall policy of one vault, indexed by subject for query time.
/// <para>
/// The index is a read-only snapshot taken when a recall query starts. It
/// carries the owner's decision and nothing else, so applying it can never
/// change what an item contains. A subject the owner never decided about is
/// Normal: the policy can only ever reduce an item's prominence by the
/// owner's own explicit act, never raise it on its own.
/// </para>
/// </summary>
public sealed class RecallPolicyIndex
{
    private readonly Dictionary<string, RecallPolicyEntry> _entries;

    private RecallPolicyIndex(Dictionary<string, RecallPolicyEntry> entries) =>
        _entries = entries;

    public static RecallPolicyIndex Empty { get; } = new(new Dictionary<string, RecallPolicyEntry>(StringComparer.Ordinal));

    public static RecallPolicyIndex From(OwnerRecallPolicyState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var validated = OwnerRecallPolicyAuthority.ValidateAndNormalizeState(state);
        var entries = new Dictionary<string, RecallPolicyEntry>(validated.Entries.Length, StringComparer.Ordinal);
        foreach (var entry in validated.Entries)
        {
            entries[entry.SubjectResourceId] = entry;
        }

        return new RecallPolicyIndex(entries);
    }

    /// <summary>The owner's disposition for one subject, or Normal when there is none.</summary>
    public RecallDisposition DispositionOf(string subjectResourceId) =>
        subjectResourceId is not null && _entries.TryGetValue(subjectResourceId, out var entry)
            ? entry.Disposition
            : RecallDisposition.Normal;

    /// <summary>
    /// The order tier the disposition gives the subject: protected items come
    /// first, ordinary items next, demoted items after them, and set-aside
    /// items last. A demoted item is still offered; it only sorts below the
    /// items the owner did not demote. A set-aside item is offered only under
    /// an explicit expansion, and then sorts behind everything else.
    /// </summary>
    public static int TierOf(RecallDisposition disposition) => disposition switch
    {
        RecallDisposition.Pinned => 0,
        RecallDisposition.Demoted => 2,
        RecallDisposition.Archived => 3,
        _ => 1
    };
}

/// <summary>
/// S7-T4 owner recall policy authority: the only way a subject's disposition
/// changes is an explicit owner act through this authority. Every act is
/// recorded with its actor, UTC time and the owner's own rationale, and the
/// recorded history is bounded so the stored policy stays one small file.
/// <para>
/// The authority never touches canonical content: it cannot change an item's
/// body, evidence, verification, revision, citations or attribution, and it
/// cannot delete an item. It only records how the item is offered. Restoring
/// an archived item therefore returns the item to the owner's own recall set
/// and never widens or narrows any grant, collection or session scope.
/// </para>
/// </summary>
public sealed class OwnerRecallPolicyAuthority
{
    public const int MaximumSubjects = 20000;
    public const int MaximumRationaleLength = 1000;

    private static readonly HashSet<RecallDisposition> KnownDispositions = new()
    {
        RecallDisposition.Normal,
        RecallDisposition.Demoted,
        RecallDisposition.Pinned,
        RecallDisposition.Archived
    };

    private readonly TimeProvider _timeProvider;

    public OwnerRecallPolicyAuthority(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Applies one explicit owner act to one subject's recall policy.
    /// <para>
    /// Each act names one transition. An act that would leave the subject
    /// where it already is is refused rather than silently accepted, so the
    /// owner always sees what their act did. A protected subject cannot be
    /// demoted or set aside: the owner releases that protection with an
    /// explicit restore first, so a rare important lesson stays findable
    /// until the owner says otherwise. Demoting or setting aside an item
    /// requires the owner's own reason, so the ranking can always be
    /// explained.
    /// </para>
    /// </summary>
    public RecallPolicyEntry Apply(
        AuthenticatedContext actor,
        string subjectResourceId,
        RecallAct act,
        string? rationale,
        IRecallPolicyPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(subjectResourceId);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);

        if (!IsValidSubjectId(subjectResourceId))
        {
            throw new ArgumentException("The recall policy names a valid subject memory item.", nameof(subjectResourceId));
        }

        if (act == RecallAct.Unknown)
        {
            throw new ArgumentException("The recall act is unknown.", nameof(act));
        }

        var reason = NormalizeRationale(rationale, act);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();

        var applied = persistence.UpdateRecallPolicyState<RecallPolicyEntry>(current => ApplyLocked(
            current, subjectResourceId, act, reason, actor, now));
        return applied;
    }

    /// <summary>
    /// Reads the recall policy of one subject. A subject the owner never
    /// decided about has no entry, which means it is offered normally.
    /// </summary>
    public RecallPolicyEntry? Read(string subjectResourceId, IRecallPolicyPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(subjectResourceId);
        ArgumentNullException.ThrowIfNull(persistence);

        var state = ValidateAndNormalizeState(persistence.LoadRecallPolicyState());
        return state.Entries.FirstOrDefault(entry =>
            string.Equals(entry.SubjectResourceId, subjectResourceId, StringComparison.Ordinal));
    }

    private (OwnerRecallPolicyState State, RecallPolicyEntry Applied) ApplyLocked(
        OwnerRecallPolicyState state,
        string subjectResourceId,
        RecallAct act,
        string? rationale,
        AuthenticatedContext actor,
        DateTimeOffset now)
    {
        var validated = ValidateAndNormalizeState(state);
        var index = Array.FindIndex(validated.Entries, entry =>
            string.Equals(entry.SubjectResourceId, subjectResourceId, StringComparison.Ordinal));
        var existing = index >= 0 ? validated.Entries[index] : null;
        var from = existing?.Disposition ?? RecallDisposition.Normal;
        var to = TargetOf(act, from);

        if (from == to)
        {
            throw new ArgumentException("The subject already has that recall disposition.", nameof(act));
        }

        var change = new RecallPolicyChange(from, to, ExecutorOf(actor), now, rationale);
        var updated = new RecallPolicyEntry(
            subjectResourceId,
            to,
            ExecutorOf(actor),
            now,
            rationale,
            AppendHistory(existing?.History ?? Array.Empty<RecallPolicyChange>(), change));

        var entries = validated.Entries.ToArray();
        if (to == RecallDisposition.Normal)
        {
            // Restoring removes the entry: the subject returns to the owner's
            // ordinary recall set, and no remembered decision is left behind to
            // explain a difference that no longer exists.
            var remaining = entries.Where((_, position) => position != index).ToArray();
            return (new OwnerRecallPolicyState(validated.SchemaVersion, remaining), updated);
        }

        if (index >= 0)
        {
            entries[index] = updated;
        }
        else
        {
            if (entries.Length >= MaximumSubjects)
            {
                throw new ArgumentException("The recall policy holds the maximum number of subjects.", nameof(subjectResourceId));
            }

            entries = entries.Append(updated).ToArray();
        }

        return (new OwnerRecallPolicyState(validated.SchemaVersion, entries), updated);
    }

    /// <summary>
    /// The disposition one act moves the subject to, or the refusal that act
    /// earns. An act never crosses a protection the owner set.
    /// </summary>
    private static RecallDisposition TargetOf(RecallAct act, RecallDisposition from)
    {
        switch (act)
        {
            case RecallAct.Demote:
                if (from == RecallDisposition.Pinned)
                {
                    throw new ArgumentException("A pinned subject is protected; restore it before demoting it.", nameof(act));
                }

                if (from == RecallDisposition.Archived)
                {
                    throw new ArgumentException("An archived subject is set aside; restore it before demoting it.", nameof(act));
                }

                return RecallDisposition.Demoted;

            case RecallAct.Pin:
                if (from == RecallDisposition.Archived)
                {
                    throw new ArgumentException("An archived subject is set aside; restore it before pinning it.", nameof(act));
                }

                return RecallDisposition.Pinned;

            case RecallAct.Archive:
                if (from == RecallDisposition.Pinned)
                {
                    throw new ArgumentException("A pinned subject is protected; restore it before setting it aside.", nameof(act));
                }

                return RecallDisposition.Archived;

            case RecallAct.Restore:
                if (from == RecallDisposition.Normal)
                {
                    throw new ArgumentException("The subject has no recall decision to restore.", nameof(act));
                }

                return RecallDisposition.Normal;

            default:
                throw new ArgumentException("The recall act is unknown.", nameof(act));
        }
    }

    private static RecallPolicyChange[] AppendHistory(RecallPolicyChange[] history, RecallPolicyChange change)
    {
        var appended = history.Append(change).ToArray();
        return appended.Length <= RecallPolicyEntry.MaximumHistory
            ? appended
            : appended[^RecallPolicyEntry.MaximumHistory..];
    }

    /// <summary>
    /// Validates and normalizes a stored policy: a known schema version, known
    /// dispositions, one entry per subject, and a bounded history. A stored
    /// file that cannot be validated is refused rather than repaired into a
    /// different policy than the owner set.
    /// </summary>
    public static OwnerRecallPolicyState ValidateAndNormalizeState(OwnerRecallPolicyState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != OwnerRecallPolicyState.CurrentSchemaVersion)
        {
            throw new InvalidDataException("The recall policy schema version is unsupported.");
        }

        if (state.Entries.Length > MaximumSubjects)
        {
            throw new InvalidDataException("The recall policy holds more subjects than the store allows.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new RecallPolicyEntry[state.Entries.Length];
        for (var index = 0; index < state.Entries.Length; index++)
        {
            var entry = state.Entries[index];
            if (entry is null || !IsValidSubjectId(entry.SubjectResourceId) || !seen.Add(entry.SubjectResourceId))
            {
                throw new InvalidDataException("The recall policy names an invalid or repeated subject.");
            }

            if (!KnownDispositions.Contains(entry.Disposition))
            {
                throw new InvalidDataException("The recall policy holds an unknown disposition.");
            }

            var history = entry.History ?? Array.Empty<RecallPolicyChange>();
            if (history.Length > RecallPolicyEntry.MaximumHistory)
            {
                throw new InvalidDataException("The recall policy history exceeds its bound.");
            }

            entries[index] = entry with { History = history };
        }

        return new OwnerRecallPolicyState(state.SchemaVersion, entries);
    }

    private static string? NormalizeRationale(string? rationale, RecallAct act)
    {
        if (act is RecallAct.Demote or RecallAct.Archive && string.IsNullOrWhiteSpace(rationale))
        {
            throw new ArgumentException("Demoting or setting an item aside requires the owner's reason.", nameof(rationale));
        }

        if (rationale is null)
        {
            return null;
        }

        var trimmed = rationale.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Length > MaximumRationaleLength)
        {
            throw new ArgumentException("The recall rationale is too long.", nameof(rationale));
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("The recall rationale contains control characters.", nameof(rationale));
        }

        return trimmed;
    }

    private static bool IsValidSubjectId(string subjectResourceId) =>
        !string.IsNullOrEmpty(subjectResourceId) &&
        subjectResourceId.Length <= 200 &&
        subjectResourceId.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static ExecutorKind ExecutorOf(AuthenticatedContext context) =>
        context.Kind == PrincipalKind.Owner ? ExecutorKind.Owner : ExecutorKind.Agent;

    private static void EnsureOwner(AuthenticatedContext context)
    {
        if (context.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Only the owner may set the recall policy.");
        }
    }
}
