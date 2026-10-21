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
/// owner names the records that leave the vault; the export writes exactly
/// those records, the relations that connect them, and the lifecycle state
/// the owner asked for. Everything else stays in the vault.
/// </para>
/// <para>
/// A citation is private provenance. It is written only when the source it
/// names is itself one of the selected records; a citation to a source
/// outside the scope is withheld whole, because naming the source, its
/// locator or its span would disclose the out-of-scope record it points at.
/// The same rule holds for a relation endpoint outside the scope.
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
        IManagedResourceStore store,
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
        var builder = new StringBuilder();
        var recordCount = 0;
        var relationCount = 0;
        var withheldCitations = 0;
        var withheldRelations = 0;
        var withheldRecords = 0;

        foreach (var resourceId in selected)
        {
            var section = RenderRecord(context, store, resourceId, selectedSet, scope, ref withheldCitations, ref withheldRecords);
            if (section is null)
            {
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
            var relations = new StringBuilder();
            foreach (var result in new RelationService().List(context, store, _ => true))
            {
                var relation = result.Relation;
                if (relation is null)
                {
                    continue;
                }

                var fromInScope = selectedSet.Contains(relation.From.ResourceId);
                var toInScope = selectedSet.Contains(relation.To.ResourceId);
                var selectedItself = selectedSet.Contains(relation.Id);
                if (!selectedItself && !(fromInScope && toInScope))
                {
                    continue;
                }

                if (!fromInScope || !toInScope)
                {
                    withheldRelations++;
                }

                relationCount++;
                relations.Append("### Relation ").Append(relation.Id).AppendLine();
                relations.Append("- Revision: ").Append(relation.Revision.ToString(CultureInfo.InvariantCulture)).AppendLine();
                relations.Append("- Kind: ").Append(relation.Kind.ToString()).AppendLine();
                relations.Append("- State: ").Append(relation.State.ToString()).AppendLine();
                relations.Append("- From: ").Append(EndpointText(relation.From, fromInScope)).AppendLine();
                relations.Append("- To: ").Append(EndpointText(relation.To, toInScope)).AppendLine();
                relations.Append("- Reason: ").AppendLine(relation.Reason ?? "(none)");
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
            .AppendLine(" (a relation with an endpoint outside the selected scope; the endpoint stays in the vault)");
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
        IManagedResourceStore store,
        string resourceId,
        IReadOnlySet<string> selectedSet,
        OwnerExportScope scope,
        ref int withheldCitations,
        ref int withheldRecords)
    {
        byte[]? raw = null;
        try
        {
            raw = store.ReadContent(resourceId, null);
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

                builder.Append("- Record: relation").AppendLine();
                builder.Append("- Zone: ").Append(relation.ZoneId).AppendLine();
                builder.Append("- Kind: ").Append(relation.Kind.ToString()).AppendLine();
                builder.Append("- State: ").Append(relation.State.ToString()).AppendLine();
                builder.Append("- From: ").Append(EndpointText(relation.From, selectedSet.Contains(relation.From.ResourceId))).AppendLine();
                builder.Append("- To: ").Append(EndpointText(relation.To, selectedSet.Contains(relation.To.ResourceId))).AppendLine();
                builder.Append("- Reason: ").AppendLine(relation.Reason ?? "(none)");
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

    private static string EndpointText(RelationEndpoint endpoint, bool inScope) =>
        inScope
            ? $"{endpoint.ResourceId}@{endpoint.Revision} ({endpoint.EndpointKind})"
            : "(withheld: outside the selected scope)";

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
