using System.Globalization;
using System.Text;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// One readable export document and the counts of what it disclosed and what
/// it deliberately withheld. The document itself is the only place the
/// selected content appears in plaintext.
/// </summary>
public sealed record OwnerExportDocument(
    string Text,
    int RecordCount,
    int RelationCount,
    int WithheldCitationCount,
    int WithheldRelationCount,
    int WithheldRecordCount);

/// <summary>
/// S8-T2 the owner-selected plaintext export.
/// <para>
/// An export is a disclosure, so it is never implicit and never a dump. The
/// owner selects managed vault ResourceIds; workspace tree selectors belong
/// to a separate export surface. The export writes those selected records and
/// lifecycle state, and includes a managed edge only when both readable endpoints
/// and its required evidence revision are in the selected scope.
/// </para>
/// <para>
/// A citation is private provenance. It is written only when the source it
/// names is itself one of the selected records; a citation to an outside source
/// is withheld whole. Relation edges follow the same scope rule: without both
/// readable endpoints and required evidence in selected revisions, an edge is
/// withheld whole and only the omission count is reported, with no excluded
/// endpoint ID, name, or stub.
/// </para>
/// <para>
/// The export is a copy outside the vault. Deleting the file, purging the
/// source records or locking the vault does not revoke a copy that was
/// already made; the export says so in its own header.
/// </para>
/// </summary>
public sealed class OwnerExportService
{
    private const string ExportFormat = "metabrain-owner-export/1";
    private readonly LifecycleService _lifecycle = new();

    public OwnerExportDocument Build(
        AuthenticatedContext context,
        IVaultOperation store,
        OwnerExportScope scope)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scope);
        if (context.Kind != PrincipalKind.Owner)
        {
            throw new ArgumentException("Only the owner can export private records.", nameof(context));
        }

        var selected = scope.NormalizedResourceIds();
        if (selected.Length == 0)
        {
            throw new ArgumentException("An export needs at least one selected record.", nameof(scope));
        }

        if (selected.Length > OwnerExportScope.MaximumRecords)
        {
            throw new ArgumentException("The selected scope exceeds the export record limit.", nameof(scope));
        }

        var selectedSet = new HashSet<string>(selected, StringComparer.Ordinal);
        var exportedRevisions = scope.IncludeRelations
            ? new Dictionary<string, long>(StringComparer.Ordinal)
            : null;
        if (exportedRevisions is not null)
        {
            foreach (var resource in store.ListResources())
            {
                if (selectedSet.Contains(resource.ResourceId))
                {
                    exportedRevisions[resource.ResourceId] = resource.Revision;
                }
            }
        }
        var builder = new StringBuilder();
        var recordCount = 0;
        var relationCount = 0;
        var withheldCitations = 0;
        var withheldRelations = 0;
        var withheldRecords = 0;

        foreach (var resourceId in selected)
        {
            var section = exportedRevisions is null
                ? RenderRecord(
                    context, store, resourceId, null, selectedSet, scope, exportedRevisions,
                    ref withheldCitations, ref withheldRecords)
                : exportedRevisions.TryGetValue(resourceId, out var expectedRevision)
                    ? RenderRecord(
                        context, store, resourceId, expectedRevision, selectedSet, scope, exportedRevisions,
                        ref withheldCitations, ref withheldRecords)
                    : null;
            if (section is null)
            {
                exportedRevisions?.Remove(resourceId);
                withheldRecords++;
                builder.Append("## ").Append(resourceId).AppendLine(" — not exported");
                builder.AppendLine();
                builder.AppendLine("This record is not in a readable export form. It stays in the vault.");
                builder.AppendLine();
                continue;
            }

            recordCount++;
            builder.Append(section);
        }

        if (scope.IncludeRelations)
        {
            var revisionScope = exportedRevisions!;
            var relations = new StringBuilder();
            foreach (var result in new RelationService().List(context, store, _ => true, workspace: store))
            {
                var relation = result.Relation;
                if (relation is null)
                {
                    continue;
                }

                var fromSelected = selectedSet.Contains(relation.From.ResourceId);
                var toSelected = selectedSet.Contains(relation.To.ResourceId);
                var selectedItself = selectedSet.Contains(relation.Id);
                if (!selectedItself && !fromSelected && !toSelected)
                {
                    continue;
                }

                var fromInScope = revisionScope.ContainsKey(relation.From.ResourceId);
                var toInScope = revisionScope.ContainsKey(relation.To.ResourceId);
                if (!fromInScope || !toInScope || !RequiredEvidenceIsSelected(relation, revisionScope))
                {
                    withheldRelations++;
                    continue;
                }

                relationCount++;
                relations.Append("### Relation ").Append(relation.Id).AppendLine();
                relations.Append("- Revision: ").Append(relation.Revision.ToString(CultureInfo.InvariantCulture)).AppendLine();
                relations.Append("- Kind: ").Append(relation.Kind.ToString()).AppendLine();
                relations.Append("- State: ").Append(relation.State.ToString()).AppendLine();
                relations.Append("- Origin: ").Append(relation.Origin.ToString()).AppendLine();
                relations.Append("- Executor: ").Append(relation.Executor.ToString()).AppendLine();
                relations.Append("- Creation method: ").Append(relation.CreationMethod.ToString()).AppendLine();
                relations.Append("- From: ").Append(EndpointText(relation.From)).AppendLine();
                relations.Append("- To: ").Append(EndpointText(relation.To)).AppendLine();
                relations.Append("- Reason: ").AppendLine(relation.Reason);
                relations.Append("- State reason: ").AppendLine(relation.StateReason ?? "(none)");
                if (relation.History is { Length: > 0 } history)
                {
                    relations.AppendLine("History:");
                    foreach (var act in history)
                    {
                        relations.Append("- ").Append(act.Action.ToString())
                            .Append(" state=").Append(act.State.ToString())
                            .Append(" actor=").Append(act.Actor.ToString())
                            .Append(" method=").Append(act.Method.ToString())
                            .Append(" expectedRevision=").Append(act.ExpectedRelationRevision.ToString(CultureInfo.InvariantCulture))
                            .Append(" recordedAtUtc=").Append(act.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture))
                            .Append(" reason=").AppendLine(act.Reason);
                    }
                }

                relations.AppendLine();
            }

            if (relationCount > 0)
            {
                builder.AppendLine("## Relations");
                builder.AppendLine();
                builder.Append(relations);
            }
        }

        if (scope.IncludeLifecycleStates)
        {
            var lifecycle = new StringBuilder();
            var lifecycleCount = 0;
            foreach (var resourceId in selected)
            {
                LifecycleRecord? record;
                try
                {
                    record = _lifecycle.Read(context, resourceId, null, store);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    record = null;
                }

                if (record is null)
                {
                    continue;
                }

                lifecycleCount++;
                lifecycle.Append("### Lifecycle of ").Append(resourceId).AppendLine();
                lifecycle.Append("- Publication: ").Append(record.Publication.ToString()).AppendLine();
                lifecycle.Append("- Question: ").Append(record.Question?.ToString() ?? "none").AppendLine();
                lifecycle.Append("- Intention: ").Append(record.Intention?.ToString() ?? "none").AppendLine();
                lifecycle.Append("- Notified: ").Append(record.Notified ? "yes" : "no").AppendLine();
                lifecycle.Append("- Acknowledged: ").Append(record.Acknowledged ? "yes" : "no").AppendLine();
                lifecycle.Append("- Valid from: ").Append(record.ValidFrom?.ToString() ?? "none").AppendLine();
                lifecycle.Append("- Valid until: ").Append(record.ValidUntil?.ToString() ?? "none").AppendLine();
                lifecycle.Append("- Retained revisions: ").Append(record.RetainedRevisions.Length.ToString(CultureInfo.InvariantCulture)).AppendLine();
                lifecycle.AppendLine();
            }

            if (lifecycleCount > 0)
            {
                builder.AppendLine("## Lifecycle states");
                builder.AppendLine();
                builder.Append(lifecycle);
            }
        }

        var header = new StringBuilder();
        header.AppendLine("# Meta Brain — owner export");
        header.AppendLine();
        header.Append("- Export format: ").AppendLine(ExportFormat);
        header.Append("- Created (UTC): ").AppendLine(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        header.Append("- Selected records: ").Append(selected.Length.ToString(CultureInfo.InvariantCulture)).AppendLine();
        header.Append("- Exported records: ").Append(recordCount.ToString(CultureInfo.InvariantCulture)).AppendLine();
        header.Append("- Exported relations: ").Append(relationCount.ToString(CultureInfo.InvariantCulture)).AppendLine();
        header.Append("- Withheld citations: ").Append(withheldCitations.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" (a citation to a source outside the selected scope; the source, its locator and its span stay in the vault)");
        header.Append("- Withheld relations: ").Append(withheldRelations.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" (an edge whose endpoints or required evidence are outside the selected revisions; no omitted endpoint IDs or names are exported)");
        header.Append("- Withheld records: ").Append(withheldRecords.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" (a selected record that has no readable export form)");
        header.Append("- Disclosure: every selected record is written in full and in plaintext, exactly as the vault stores it.").AppendLine();
        header.Append("- Not included: records outside the selected scope, live grants, sessions, tokens, keys, credentials and the private index.").AppendLine();
        header.Append("- This copy lives outside the vault: a later purge, lock or export does not revoke it.").AppendLine();

        var text = header.ToString() + Environment.NewLine + builder.ToString();
        return new OwnerExportDocument(
            text,
            recordCount,
            relationCount,
            withheldCitations,
            withheldRelations,
            withheldRecords);
    }

    private static string? RenderRecord(
        AuthenticatedContext context,
        IVaultOperation store,
        string resourceId,
        long? expectedRevision,
        IReadOnlySet<string> selectedSet,
        OwnerExportScope scope,
        IReadOnlyDictionary<string, long>? exportedRevisions,
        ref int withheldCitations,
        ref int withheldRecords)
    {
        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(resourceId, expectedRevision);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            var builder = new StringBuilder();
            builder.Append("## ").Append(resourceId).AppendLine();
            builder.AppendLine();

            try
            {
                var memory = MemoryEnvelopeCodec.DecodeStored(raw);
                if (!string.Equals(memory.Id, resourceId, StringComparison.Ordinal))
                {
                    return null;
                }

                builder.Append("- Record: memory item").AppendLine();
                builder.Append("- Zone: ").Append(memory.ZoneId).AppendLine();
                builder.Append("- Kind: ").Append(memory.Kind.ToString()).AppendLine();
                builder.Append("- Title: ").Append(memory.Title ?? "(none)").AppendLine();
                builder.Append("- Author: ").Append(memory.Author.ToString()).AppendLine();
                builder.Append("- Executor: ").Append(memory.Executor.ToString()).AppendLine();
                builder.Append("- Creation method: ").Append(memory.CreationMethod.ToString()).AppendLine();
                builder.Append("- Evidence: ").Append(memory.Evidence.ToString()).AppendLine();
                builder.Append("- Level: ").Append(memory.Level.ToString()).AppendLine();
                builder.Append("- Occurred (UTC): ").Append(memory.OccurredAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "none").AppendLine();
                builder.Append("- Recorded (UTC): ").Append(memory.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture)).AppendLine();
                var inScopeCitations = 0;
                foreach (var source in memory.Sources)
                {
                    if (!OwnerExportPolicy.IsCitationInScope(source.SourceId, selectedSet))
                    {
                        withheldCitations++;
                        continue;
                    }

                    inScopeCitations++;
                    builder.Append("- Citation: source=").Append(source.SourceId)
                        .Append(" revision=").Append(source.SourceRevision?.ToString(CultureInfo.InvariantCulture) ?? "unknown")
                        .Append(" availability=").Append(source.Availability?.ToString() ?? "unknown");
                    if (!string.IsNullOrEmpty(source.Locator))
                    {
                        builder.Append(" locator=").Append(source.Locator);
                    }

                    builder.AppendLine();
                }

                builder.Append("- Citations in scope: ").Append(inScopeCitations.ToString(CultureInfo.InvariantCulture)).AppendLine();
                builder.AppendLine();
                builder.AppendLine("Body:");
                builder.AppendLine();
                builder.AppendLine(Fenced(memory.Body));
                builder.AppendLine();
                return builder.ToString();
            }
            catch (ArgumentException)
            {
            }

            try
            {
                var source = RegisteredSourceCodec.DecodeStored(raw);
                if (!string.Equals(source.Id, resourceId, StringComparison.Ordinal))
                {
                    return null;
                }

                builder.Append("- Record: registered source").AppendLine();
                builder.Append("- Zone: ").Append(source.ZoneId).AppendLine();
                builder.Append("- Title: ").Append(source.Title ?? "(none)").AppendLine();
                builder.Append("- Locator: ").Append(source.Locator ?? "(none)").AppendLine();
                builder.Append("- Version: ").Append(source.Version ?? "(none)").AppendLine();
                builder.Append("- Availability: ").Append(source.Availability.ToString()).AppendLine();
                builder.Append("- Executor: ").Append(source.Executor.ToString()).AppendLine();
                builder.Append("- Creation method: ").Append(source.CreationMethod.ToString()).AppendLine();
                builder.Append("- Author: ").Append(source.Author?.ToString() ?? "none").AppendLine();
                builder.Append("- Captured (UTC): ").Append(source.CapturedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "none").AppendLine();
                builder.Append("- Recorded (UTC): ").Append(source.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture)).AppendLine();
                builder.AppendLine();
                if (!string.IsNullOrEmpty(source.Text))
                {
                    builder.AppendLine("Captured text:");
                    builder.AppendLine();
                    builder.AppendLine(Fenced(source.Text));
                    builder.AppendLine();
                }

                return builder.ToString();
            }
            catch (ArgumentException)
            {
            }

            try
            {
                var relation = RelationCodec.DecodeStored(raw);
                if (!string.Equals(relation.Id, resourceId, StringComparison.Ordinal))
                {
                    return null;
                }

                if (exportedRevisions is null ||
                    !exportedRevisions.ContainsKey(relation.From.ResourceId) ||
                    !exportedRevisions.ContainsKey(relation.To.ResourceId) ||
                    !RequiredEvidenceIsSelected(relation, exportedRevisions))
                {
                    return null;
                }

                builder.Append("- Record: relation").AppendLine();
                builder.Append("- Zone: ").Append(relation.ZoneId).AppendLine();
                builder.Append("- Kind: ").Append(relation.Kind.ToString()).AppendLine();
                builder.Append("- State: ").Append(relation.State.ToString()).AppendLine();
                builder.Append("- Origin: ").Append(relation.Origin.ToString()).AppendLine();
                builder.Append("- Executor: ").Append(relation.Executor.ToString()).AppendLine();
                builder.Append("- Creation method: ").Append(relation.CreationMethod.ToString()).AppendLine();
                builder.Append("- From: ").Append(EndpointText(relation.From)).AppendLine();
                builder.Append("- To: ").Append(EndpointText(relation.To)).AppendLine();
                builder.Append("- Reason: ").AppendLine(relation.Reason);
                builder.Append("- State reason: ").AppendLine(relation.StateReason ?? "(none)");
                if (relation.History is { Length: > 0 } history)
                {
                    foreach (var act in history)
                    {
                        builder.Append("- History: ").Append(act.Action.ToString())
                            .Append(" state=").Append(act.State.ToString())
                            .Append(" actor=").Append(act.Actor.ToString())
                            .Append(" method=").Append(act.Method.ToString())
                            .Append(" expectedRevision=").Append(act.ExpectedRelationRevision.ToString(CultureInfo.InvariantCulture))
                            .Append(" recordedAtUtc=").Append(act.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture))
                            .Append(" reason=").AppendLine(act.Reason);
                    }
                }
                builder.AppendLine();
                return builder.ToString();
            }
            catch (ArgumentException)
            {
            }

            try
            {
                var lifecycle = LifecycleCodec.DecodeStored(raw);
                if (!string.Equals(lifecycle.Id, resourceId, StringComparison.Ordinal))
                {
                    return null;
                }

                builder.Append("- Record: lifecycle").AppendLine();
                builder.Append("- Zone: ").Append(lifecycle.ZoneId).AppendLine();
                builder.Append("- Subject: ").Append(lifecycle.SubjectResourceId).AppendLine();
                builder.Append("- Publication: ").Append(lifecycle.Publication.ToString()).AppendLine();
                builder.Append("- Question: ").Append(lifecycle.Question?.ToString() ?? "none").AppendLine();
                builder.Append("- Intention: ").Append(lifecycle.Intention?.ToString() ?? "none").AppendLine();
                builder.Append("- Retained revisions: ").Append(lifecycle.RetainedRevisions.Length.ToString(CultureInfo.InvariantCulture)).AppendLine();
                builder.AppendLine();
                return builder.ToString();
            }
            catch (ArgumentException)
            {
            }

            try
            {
                var annotation = CognitiveAnnotationCodec.DecodeStored(raw);
                if (!string.Equals(annotation.Id, resourceId, StringComparison.Ordinal))
                {
                    return null;
                }

                var anchorState = "unavailable";
                var availability = SourceAvailability.Unavailable;
                try
                {
                    var currentAnchor = store.ReadWorkspaceAnchors(annotation.Anchor.FileId)
                        .FirstOrDefault(anchor => string.Equals(
                            anchor.AnchorId, annotation.Anchor.AnchorId, StringComparison.Ordinal));
                    if (currentAnchor is not null)
                    {
                        if (!string.Equals(currentAnchor.FileId, annotation.Anchor.FileId, StringComparison.Ordinal) ||
                            currentAnchor.ContentRevision != annotation.Anchor.ContentRevision ||
                            currentAnchor.Offset != annotation.Anchor.Offset ||
                            currentAnchor.Length != annotation.Anchor.Length ||
                            !string.Equals(currentAnchor.Sha256, annotation.Anchor.Sha256, StringComparison.Ordinal))
                        {
                            return null;
                        }

                        anchorState = currentAnchor.State;
                        availability = SourceAvailability.Available;
                    }
                }
                catch (WorkspaceRefusalException ex) when (
                    ex.Code is WorkspaceErrorCodes.NotFound or WorkspaceErrorCodes.Deleted)
                {
                    availability = SourceAvailability.Unavailable;
                }

                builder.Append("- Record: cognitive annotation").AppendLine();
                builder.Append("- Zone: ").Append(annotation.ZoneId).AppendLine();
                builder.Append("- Revision: ").Append(annotation.History[^1].Revision.ToString(CultureInfo.InvariantCulture)).AppendLine();
                builder.Append("- Origin: ").Append(annotation.Origin.ToString()).AppendLine();
                builder.Append("- Review: ").Append(annotation.ReviewStatus.ToString()).AppendLine();
                builder.Append("- Facets: ").Append(annotation.Facets.Length == 0
                    ? "none"
                    : string.Join(",", annotation.Facets)).AppendLine();
                builder.Append("- Kind: ").Append(annotation.Kind.ToString()).AppendLine();
                builder.Append("- Author: ").Append(annotation.Author.ToString()).AppendLine();
                builder.Append("- Executor: ").Append(annotation.Executor.ToString()).AppendLine();
                builder.Append("- Creation method: ").Append(annotation.CreationMethod.ToString()).AppendLine();
                builder.Append("- Evidence: ").Append(annotation.Evidence.ToString()).AppendLine();
                builder.Append("- Level: ").Append(annotation.Level.ToString()).AppendLine();
                builder.Append("- Anchor: file=").Append(annotation.Anchor.FileId)
                    .Append(" contentRevision=").Append(annotation.Anchor.ContentRevision.ToString(CultureInfo.InvariantCulture))
                    .Append(" offset=").Append(annotation.Anchor.Offset.ToString(CultureInfo.InvariantCulture))
                    .Append(" length=").Append(annotation.Anchor.Length.ToString(CultureInfo.InvariantCulture))
                    .Append(" sha256=").Append(annotation.Anchor.Sha256)
                    .Append(" state=").Append(anchorState)
                    .Append(" availability=").Append(availability.ToString()).AppendLine();
                builder.Append("- Source bytes: not included; the anchor remains a reference.").AppendLine();
                builder.AppendLine("Revision history:");
                foreach (var version in annotation.History)
                {
                    builder.Append("- ").Append(version.Revision.ToString(CultureInfo.InvariantCulture))
                        .Append(" action=").Append(version.Action.ToString())
                        .Append(" previousReview=").Append(version.PreviousReviewStatus.ToString())
                        .Append(" origin=").Append(version.State.Origin.ToString())
                        .Append(" review=").Append(version.State.ReviewStatus.ToString())
                        .Append(" facets=").Append(version.State.Facets.Length == 0 ? "none" : string.Join(",", version.State.Facets))
                        .Append(" kind=").Append(version.State.Kind.ToString())
                        .Append(" author=").Append(version.State.Author.ToString())
                        .Append(" executor=").Append(version.State.Executor.ToString())
                        .Append(" method=").Append(version.State.CreationMethod.ToString())
                        .Append(" evidence=").Append(version.State.Evidence.ToString())
                        .Append(" level=").Append(version.State.Level.ToString())
                        .Append(" actor=").Append(version.Actor.ToString())
                        .Append(" recordedAtUtc=").Append(version.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture))
                        .Append(" reason=").AppendLine(version.Reason ?? "(none)");
                }

                builder.AppendLine();
                return builder.ToString();
            }
            catch (ArgumentException)
            {
            }

            return null;
        }
        finally
        {
            if (raw is { Length: > 0 })
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
            }
        }
    }

    private static bool RequiredEvidenceIsSelected(
        Relation relation,
        IReadOnlyDictionary<string, long> exportedRevisions) =>
        RequiredEvidenceIsSelected(relation.Kind, relation.From, relation.To, exportedRevisions);

    private static bool RequiredEvidenceIsSelected(
        StoredRelation relation,
        IReadOnlyDictionary<string, long> exportedRevisions) =>
        RequiredEvidenceIsSelected(relation.Kind, relation.From, relation.To, exportedRevisions);

    private static bool RequiredEvidenceIsSelected(
        RelationKind kind,
        RelationEndpoint from,
        RelationEndpoint to,
        IReadOnlyDictionary<string, long> exportedRevisions)
    {
        var evidenceSide = RelationSemantics.EvidenceSide(kind);
        if (evidenceSide == RelationEvidenceSide.None)
        {
            return true;
        }

        var endpoint = evidenceSide == RelationEvidenceSide.From ? from : to;
        return exportedRevisions.TryGetValue(endpoint.ResourceId, out var revision) &&
            revision == endpoint.Revision;
    }

    private static string EndpointText(RelationEndpoint endpoint)
    {
        var text = new StringBuilder()
            .Append(endpoint.ResourceId)
            .Append('@')
            .Append(endpoint.Revision.ToString(CultureInfo.InvariantCulture))
            .Append(" (")
            .Append(endpoint.EndpointKind.ToString());
        if (endpoint.Span is { } span)
        {
            text.Append(" offset=").Append(span.Start.ToString(CultureInfo.InvariantCulture))
                .Append(" length=").Append((span.End - span.Start).ToString(CultureInfo.InvariantCulture));
        }

        if (endpoint.WorkspacePin?.Sha256 is { } sha256)
        {
            text.Append(" sha256=").Append(sha256);
        }

        if (endpoint.WorkspacePin?.TreeRevision is { } treeRevision)
        {
            text.Append(" treeRevision=").Append(treeRevision.ToString(CultureInfo.InvariantCulture));
        }

        return text.Append(')').ToString();
    }

    /// <summary>
    /// Wraps the owner's own text in a fence that is longer than any run of
    /// backticks inside it, so the text is reproduced exactly and cannot end
    /// the block early.
    /// </summary>
    private static string Fenced(string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var character in text)
        {
            if (character == '`')
            {
                run++;
                if (run > longest)
                {
                    longest = run;
                }
            }
            else
            {
                run = 0;
            }
        }

        var fence = new string('`', Math.Max(3, longest + 1));
        return fence + "text" + Environment.NewLine + text + Environment.NewLine + fence;
    }
}
