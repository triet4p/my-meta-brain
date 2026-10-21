namespace MetaBrain.Core.Security;

/// <summary>
/// S8-T2 the owner-selected scope of one plaintext export.
/// <para>
/// An export is a disclosure, so it is never implicit: the owner names the
/// records that leave the vault, and nothing else is written to the file. The
/// scope is a set of canonical resource IDs; an empty scope is refused
/// rather than read as "everything".
/// </para>
/// </summary>
public sealed record OwnerExportScope(
    string[] ResourceIds,
    bool IncludeRelations,
    bool IncludeLifecycleStates)
{
    /// <summary>The largest number of records one export may carry.</summary>
    public const int MaximumRecords = 1000;

    /// <summary>
    /// The normalized scope: the distinct, ordered record IDs. An empty or
    /// duplicated selection is normalized here, and the caller still has to
    /// decide what to do with an empty selection.
    /// </summary>
    public string[] NormalizedResourceIds() =>
        ResourceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
}

/// <summary>
/// What the owner learns from one completed export. It counts what was
/// written and what was deliberately withheld; it never carries record
/// content.
/// </summary>
public sealed record OwnerExportSummary(
    int RecordCount,
    int RelationCount,
    int WithheldCitationCount,
    int WithheldRelationCount,
    int WithheldRecordCount,
    long Bytes);

/// <summary>
/// S8-T2 the disclosure policy of a plaintext export.
/// <para>
/// A citation is private provenance: it names the source a record was written
/// from and where in that source the evidence sits. It may cross into a
/// readable export only when the source it points at is inside the owner's
/// selected scope. A citation to a source outside the scope is withheld
/// whole: neither the source ID, nor the locator, nor the span leaves the
/// vault, because naming any of them would disclose the out-of-scope record
/// it points at.
/// </para>
/// </summary>
public static class OwnerExportPolicy
{
    /// <summary>
    /// True when a citation may be written into the export: the source it
    /// names has to be one of the selected records.
    /// </summary>
    public static bool IsCitationInScope(string? sourceId, IReadOnlySet<string> selectedResourceIds)
    {
        ArgumentNullException.ThrowIfNull(selectedResourceIds);
        return sourceId is not null && selectedResourceIds.Contains(sourceId);
    }
}
