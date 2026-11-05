using System.Security.Cryptography;

using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// S7-T4 owner recall policy service: the only path that changes how a memory
/// item is offered to recall.
/// <para>
/// The service never writes canonical content. It cannot change an item's
/// body, evidence, verification, citations, times, attribution or revision,
/// and it cannot delete an item: a demoted, pinned or archived item keeps
/// every byte it had and stays readable by its own ID. What changes is the
/// order in which the item is offered and whether it is offered at all.
/// </para>
/// <para>
/// Every act is an explicit owner act with a rationale, an actor and a UTC
/// time. Nothing here is derived from content, an answer, a relation, a read
/// count or recency: reading an item more often never changes its policy, and
/// a subject that the owner never decided about is offered like every other
/// item. Restoring an archived item returns it to the owner's own recall set
/// and never touches a grant, a collection or a session scope, so a restore
/// can neither widen nor narrow anyone's access.
/// </para>
/// </summary>
public sealed class RecallPolicyService
{
    private readonly TimeProvider _timeProvider;

    public RecallPolicyService(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Applies one explicit owner act to one subject's recall policy. The
    /// subject must be a live memory item or a live passage annotation now: a
    /// policy about a subject that no longer exists is refused rather than
    /// recorded against a name that could be reused.
    /// </summary>
    public RecallPolicyEntry Apply(
        AuthenticatedContext context,
        string subjectResourceId,
        RecallAct act,
        string? rationale,
        IVaultOperation operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        if (context.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Only the owner may set the recall policy.");
        }

        RequireSubject(operation, subjectResourceId);

        return new OwnerRecallPolicyAuthority(_timeProvider).Apply(
            context, subjectResourceId, act, rationale, operation);
    }

    /// <summary>
    /// Reads the recall policy of one subject. A subject the owner never
    /// decided about has no entry, which means it is offered normally.
    /// </summary>
    public RecallPolicyEntry? Read(
        AuthenticatedContext context,
        string subjectResourceId,
        IVaultOperation operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        if (context.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Only the owner may read the recall policy.");
        }

        return new OwnerRecallPolicyAuthority(_timeProvider).Read(subjectResourceId, operation);
    }

    /// <summary>
    /// The recall dispositions of every subject the owner decided about. This
    /// is the owner's own private curation state and never leaves the owner
    /// channel.
    /// </summary>
    public IReadOnlyList<RecallPolicyEntry> List(
        AuthenticatedContext context,
        IVaultOperation operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        if (context.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Only the owner may read the recall policy.");
        }

        var state = OwnerRecallPolicyAuthority.ValidateAndNormalizeState(operation.LoadRecallPolicyState());
        return state.Entries;
    }

    /// <summary>
    /// Resolves a recall subject: a live memory item, or a durable cognitive
    /// annotation, which is the S14-T2 file/passage memory record. A policy
    /// about a subject that no longer exists is refused rather than recorded
    /// against a name that could be reused; a passage annotation is read by its
    /// own encrypted record so its anchor, origin and review status stay
    /// untouched.
    /// </summary>
    private static void RequireSubject(IManagedResourceStore store, string subjectResourceId)
    {
        if (string.IsNullOrEmpty(subjectResourceId) || !DomainIdentifiers.IsLifecycleSubject(subjectResourceId))
        {
            throw new ArgumentException("The recall policy needs a subject memory item or passage annotation.", nameof(subjectResourceId));
        }

        var listed = store.ListResources().FirstOrDefault(resource =>
            string.Equals(resource.ResourceId, subjectResourceId, StringComparison.Ordinal));
        if (listed is null)
        {
            throw new ArgumentException("The recall subject is not a managed resource.", nameof(subjectResourceId));
        }

        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(subjectResourceId, listed.Revision);
            if (DomainIdentifiers.IsCognitiveAnnotationId(subjectResourceId))
            {
                var annotation = CognitiveAnnotationCodec.DecodeStored(raw);
                if (!string.Equals(annotation.Id, subjectResourceId, StringComparison.Ordinal) ||
                    !string.Equals(annotation.ZoneId, listed.ZoneId, StringComparison.Ordinal) ||
                    annotation.History[^1].Revision != listed.Revision)
                {
                    throw new ArgumentException("The recall subject is not a passage annotation.", nameof(subjectResourceId));
                }

                return;
            }

            var stored = MemoryEnvelopeCodec.DecodeStored(raw);
            if (!string.Equals(stored.Id, subjectResourceId, StringComparison.Ordinal))
            {
                throw new ArgumentException("The recall subject is not a memory item.", nameof(subjectResourceId));
            }
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw new ArgumentException("The recall subject is unavailable.", ex);
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }
    }
}
