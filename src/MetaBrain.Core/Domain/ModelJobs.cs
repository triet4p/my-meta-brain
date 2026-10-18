using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace MetaBrain.Core.Domain;

public enum JobState
{
    Unknown = 0,
    Pending = 1,
    Executing = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5
}

public enum JobKind
{
    Unknown = 0,
    Structuring = 1
}
public static class StructuringProfiles
{
    public const string CodingSession = "coding-session";
    public const string ResearchNote = "research-note";
    public const string Trend = "trend";
    public const string Synthesis = "synthesis";

    public static bool IsSupported(string? profile) =>
        profile is null or "" or CodingSession or ResearchNote or Trend or Synthesis;

    public static bool IsSynthesis(string? profile) =>
        string.Equals(profile, Synthesis, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Abstraction level an owner may request from one bounded synthesis job. The
/// level belongs to the owner's explicit request, never to model output: a job
/// cannot raise its own level, and no level is evidence of truth (R13).
/// </summary>
public static class SynthesisLevels
{
    public const string L2 = "l2";
    public const string L3Plus = "l3+";

    public static bool TryParse(string? value, out AbstractionLevel level)
    {
        level = AbstractionLevel.Unknown;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case L2:
                level = AbstractionLevel.L2;
                return true;
            case L3Plus:
            case "l3":
                level = AbstractionLevel.L3Plus;
                return true;
            default:
                return false;
        }
    }

    public static string TokenFor(AbstractionLevel level) => level switch
    {
        AbstractionLevel.L2 => L2,
        AbstractionLevel.L3Plus => L3Plus,
        _ => throw new ArgumentException("Only an L2 or L3+ item can be a synthesis result.")
    };
}

/// <summary>
/// One input the owner explicitly selected for a job, pinned to the exact
/// revision that job may read. A selection is an explicit owner choice: no
/// query, glob, or discovery step ever adds an input to a job (R13/R21).
/// </summary>
public sealed record JobInputReference(string ResourceId, long Revision);

/// <summary>
/// One selected input together with the exact text a synthesis item may ground
/// itself in. <see cref="IsSourceText"/> is true when that text is a registered
/// source's own verbatim text (directly or through a preserved snapshot), in
/// which case a committed citation can carry a span into it; a memory item's
/// body is not source text, so its citation pins the item revision alone.
/// </summary>
public sealed record JobInputText(string ResourceId, long Revision, string Text, bool IsSourceText);

/// <summary>
/// Owner-supplied synthesis selection: the explicit input set, the objective
/// the owner asks the bounded job to address, and the level the owner requests
/// for the result. An empty objective is not a licensed agenda: the job is
/// refused instead of inventing one.
/// </summary>
public sealed record JobSelectionDraft(
    JobInputReference[]? Inputs,
    string? Objective = null,
    string? TargetLevel = null);

public static class JobSelectionCodec
{
    public const int MaximumPayloadBytes = 64 * 1024;
    public const int MaximumObjectiveLength = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static JobSelectionDraft DecodeDraft(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length is <= 0 or > MaximumPayloadBytes)
        {
            throw new ArgumentException("The job selection draft exceeds its size limit.");
        }

        JobSelectionDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<JobSelectionDraft>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The job selection draft schema is invalid.", ex);
        }

        return draft ?? throw new ArgumentException("The job selection draft schema is invalid.");
    }
}

/// <summary>
/// Immutable snapshot of the approved scope boundaries for one model job:
/// the exact inputs the owner selected each pinned to one revision, the
/// destination inbox, the provider/model, and the caps that applied. Every
/// input is revalidated before reading data, before the provider send, and
/// before committing output; for a multi-input synthesis every input is
/// revalidated, so one changed or revoked input blocks the next step.
/// </summary>
public sealed record JobScopeSnapshot(
    JobInputReference[] Inputs,
    string DestinationZoneId,
    string DestinationResourceId,
    long DestinationRevision,
    string Provider,
    string Model,
    decimal MaximumCostUsd,
    long MaxInputTokens,
    long MaxOutputTokens,
    string? Profile = null,
    string? Objective = null,
    AbstractionLevel? TargetLevel = null);
public sealed record JobUsage(
    long InputTokens,
    long OutputTokens,
    decimal CostUsd);

public sealed record JobFailureReason(
    string Code,
    string Message);

/// <summary>
/// Server-persisted encrypted job record stored in the vault via IManagedResourceStore.
/// Resource ID begins with "job-".
/// </summary>
public sealed record StoredJobRecord(
    int SchemaVersion,
    string Id,
    string ZoneId,
    JobKind Kind,
    JobState State,
    JobScopeSnapshot Scope,
    JobUsage? Usage,
    JobFailureReason? Failure,
    string[]? OutputProposalIds,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

/// <summary>
/// Owner-facing read model: stored job record joined with live revision.
/// </summary>
public sealed record JobRecord(
    string Id,
    string ZoneId,
    long Revision,
    int SchemaVersion,
    JobKind Kind,
    JobState State,
    JobScopeSnapshot Scope,
    JobUsage? Usage,
    JobFailureReason? Failure,
    string[]? OutputProposalIds,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

/// <summary>
/// One grounded passage of a synthesis item: the exact selected input it was
/// copied from, that input's pinned revision, and the verbatim text. An
/// item-local grounding set is the complete evidence boundary of a synthesis
/// item, so a claim spanning two inputs must ground in both (R13/R21).
/// </summary>
public sealed record ModelJobGrounding(
    string InputId,
    long InputRevision,
    string Excerpt);

public sealed record ModelJobStructuredItem(
    string Kind,
    string Title,
    string Body,
    string Excerpt,
    MemoryApplicability? Applicability = null,
    string? OccurredAtSourceText = null,
    ModelJobGrounding[]? Grounding = null,
    bool CrossSource = false);
public sealed record ModelJobOutput(
    string Summary,
    ModelJobStructuredItem[] Items);

public static class JobCodec
{
    public const int CurrentSchemaVersion = 2;
    public const int LegacySingleSourceSchemaVersion = 1;
    public const int MaximumPayloadBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static byte[] EncodeStored(StoredJobRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
    }

    public static StoredJobRecord DecodeStored(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumPayloadBytes)
        {
            throw new ArgumentException("The stored job record exceeds maximum payload length.");
        }

        var span = StripUtf8Preamble(json);
        var offset = json.Length - span.Length;
        using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(json, offset, span.Length));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The stored job record schema is invalid.");
        }

        var record = JsonSerializer.Deserialize<StoredJobRecord>(root, JsonOptions);
        if (record is null)
        {
            throw new ArgumentException("The stored job record schema is invalid.");
        }

        if (record.SchemaVersion == LegacySingleSourceSchemaVersion)
        {
            record = MigrateSingleSourceRecord(record, root);
        }

        if (record.SchemaVersion != CurrentSchemaVersion ||
            record.Scope is null ||
            record.Scope.Inputs is null ||
            record.Scope.Inputs.Length == 0)
        {
            throw new ArgumentException("The stored job record schema is invalid.");
        }

        return record;
    }

    /// <summary>
    /// Reads a single-source job record written before the job scope carried an
    /// explicit input set. The one recorded source resource/revision is folded
    /// into the current input list and the schema version is updated in memory,
    /// so an existing encrypted job record stays readable instead of being
    /// stranded by the shape change. Nothing is written back: the stored bytes
    /// keep their original revision until the job is next written.
    /// </summary>
    private static StoredJobRecord MigrateSingleSourceRecord(StoredJobRecord record, JsonElement root)
    {
        if (!root.TryGetProperty("scope", out var scope) ||
            scope.ValueKind != JsonValueKind.Object ||
            !scope.TryGetProperty("sourceResourceId", out var sourceResourceId) ||
            sourceResourceId.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(sourceResourceId.GetString()) ||
            !scope.TryGetProperty("sourceRevision", out var sourceRevision) ||
            !sourceRevision.TryGetInt64(out var revision) ||
            revision < 1)
        {
            throw new ArgumentException("The legacy stored job record schema is invalid.");
        }

        return record with
        {
            SchemaVersion = CurrentSchemaVersion,
            Scope = record.Scope with
            {
                Inputs = new[] { new JobInputReference(sourceResourceId.GetString()!, revision) }
            }
        };
    }

    public static ModelJobOutput DecodeOutput(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var output = JsonSerializer.Deserialize<ModelJobOutput>(json, JsonOptions);
        if (output is null)
        {
            throw new ArgumentException("The model output could not be deserialized.");
        }

        return output;
    }

    public static ReadOnlySpan<byte> StripUtf8Preamble(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return bytes[3..];
        }

        return bytes;
    }
}

public static class JobOutputValidator
{
    private static readonly HashSet<string> AllowedKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "claim", "observation", "question", "hypothesis", "decision", "lesson",
        "problem", "attempt", "result", "intention"
    };

    private static readonly HashSet<string> SynthesisKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "synthesis", "claim", "observation", "question", "hypothesis", "decision", "lesson"
    };

    private const int MaximumTitleLength = 256;
    private const int MaximumBodyLength = 16384;
    private const int MaximumExcerptLength = 4096;

    public static bool TryValidate(
        string rawOutput,
        string sourceText,
        out ModelJobOutput? validatedOutput,
        out string? errorCode,
        out string? errorMessage)
    {
        validatedOutput = null;
        errorCode = null;
        errorMessage = null;

        if (!TryParseOutput(rawOutput, out var parsed, out errorCode, out errorMessage) || parsed is null)
        {
            return false;
        }

        foreach (var item in parsed.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Kind) || !AllowedKinds.Contains(item.Kind))
            {
                errorCode = "schema_violation";
                errorMessage = $"Item kind '{item.Kind}' is not an approved kind.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > MaximumTitleLength)
            {
                errorCode = "schema_violation";
                errorMessage = "Item title must be non-empty and at most 256 characters.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.Body) || item.Body.Length > MaximumBodyLength)
            {
                errorCode = "schema_violation";
                errorMessage = "Item body must be non-empty and at most 16,384 characters.";
                return false;
            }

            // Excerpt grounding: excerpt must be present and exist verbatim in the input source text
            if (string.IsNullOrWhiteSpace(item.Excerpt) || !sourceText.Contains(item.Excerpt, StringComparison.Ordinal))
            {
                errorCode = "grounding_failure";
                errorMessage = "Item excerpt is not grounded in verbatim source text.";
                return false;
            }

            if (item.OccurredAtSourceText is { } occurredAtSourceText &&
                (!item.Excerpt.Contains(occurredAtSourceText, StringComparison.Ordinal) ||
                 !HasExplicitTimestampOffset(occurredAtSourceText) ||
                 !DateTimeOffset.TryParse(
                     occurredAtSourceText,
                     CultureInfo.InvariantCulture,
                     DateTimeStyles.None,
                     out _)))
            {
                errorCode = "timestamp_ungrounded";
                errorMessage = "Item occurrence time must be an exact, offset-qualified timestamp in its verbatim excerpt.";
                return false;
            }

            if (item.Applicability is not null)
            {
                if (!string.IsNullOrWhiteSpace(item.Applicability.Version) &&
                    !sourceText.Contains(item.Applicability.Version, StringComparison.Ordinal))
                {
                    errorCode = "applicability_ungrounded";
                    errorMessage = $"Item applicability version '{item.Applicability.Version}' is not grounded in verbatim source text.";
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(item.Applicability.Project) &&
                    !sourceText.Contains(item.Applicability.Project, StringComparison.OrdinalIgnoreCase))
                {
                    errorCode = "applicability_ungrounded";
                    errorMessage = $"Item applicability project '{item.Applicability.Project}' is not grounded in source text.";
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(item.Applicability.Environment) &&
                    !sourceText.Contains(item.Applicability.Environment, StringComparison.OrdinalIgnoreCase))
                {
                    errorCode = "applicability_ungrounded";
                    errorMessage = $"Item applicability environment '{item.Applicability.Environment}' is not grounded in source text.";
                    return false;
                }
            }

            // No deployed inference from test pass:
            // A result item cannot claim deployed / deployment if source text only contains test execution without deployment tool evidence.
            if (string.Equals(item.Kind, "result", StringComparison.OrdinalIgnoreCase) &&
                (item.Title.Contains("deployed", StringComparison.OrdinalIgnoreCase) ||
                 item.Body.Contains("deployed to production", StringComparison.OrdinalIgnoreCase) ||
                 item.Body.Contains("deployed and active", StringComparison.OrdinalIgnoreCase)))
            {
                var containsDeploymentToolOutput = sourceText.Contains("[TOOL: deploy", StringComparison.OrdinalIgnoreCase) ||
                                                    sourceText.Contains("deployment succeeded", StringComparison.OrdinalIgnoreCase) ||
                                                    sourceText.Contains("published to production", StringComparison.OrdinalIgnoreCase);
                if (!containsDeploymentToolOutput)
                {
                    errorCode = "unverified_inference";
                    errorMessage = "Item describes deployment as an observed result, but source text only records test execution without deployment tool evidence.";
                    return false;
                }
            }

            // Note on authority boundary: Output cannot gain shell, grant, or key authority.
            // Security is enforced structurally at the proposal sink: output is converted
            // strictly to StoredProposal records in the destination inbox with fixed non-owner
            // attribution (Author=SpeakerRole.Assistant, Executor=ExecutorKind.StructuringJob,
            // ClaimedUserConfirmed=false). Consumer execution paths treat proposal content as inert
            // text data; no shell execution, grant creation, or vault key manipulation can be invoked.
        }

        validatedOutput = parsed;
        return true;
    }

    private static bool HasExplicitTimestampOffset(string value)
    {
        var timeStart = value.IndexOf('T');
        if (timeStart < 0)
        {
            timeStart = value.IndexOf(' ');
        }

        if (timeStart < 0 || value.IndexOf(':', timeStart + 1) < 0)
        {
            return false;
        }

        if (value.EndsWith('Z') || value.EndsWith('z'))
        {
            return true;
        }

        var offsetStart = value.Length - 6;
        return offsetStart > timeStart &&
            (value[offsetStart] is '+' or '-') &&
            char.IsAsciiDigit(value[offsetStart + 1]) &&
            char.IsAsciiDigit(value[offsetStart + 2]) &&
            value[offsetStart + 3] == ':' &&
            char.IsAsciiDigit(value[offsetStart + 4]) &&
            char.IsAsciiDigit(value[offsetStart + 5]);
    }

    /// <summary>
    /// Parses one model output document. Shared by the single-input and the
    /// multi-input validators so both apply the same JSON boundary.
    /// </summary>
    private static bool TryParseOutput(string rawOutput, out ModelJobOutput? parsed, out string? errorCode, out string? errorMessage)
    {
        parsed = null;
        errorCode = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            errorCode = "malformed_output";
            errorMessage = "Model output is empty.";
            return false;
        }

        try
        {
            // Some models enclose JSON in ```json ... ``` code blocks; strip code fences cleanly if present
            var cleanJson = rawOutput.Trim();
            var firstFence = cleanJson.IndexOf("```", StringComparison.Ordinal);
            if (firstFence >= 0)
            {
                var firstNewline = cleanJson.IndexOf('\n', firstFence);
                var lastFence = cleanJson.LastIndexOf("```", StringComparison.Ordinal);
                if (firstNewline > 0 && lastFence > firstNewline)
                {
                    cleanJson = cleanJson.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
                }
            }

            parsed = JobCodec.DecodeOutput(cleanJson);
        }
        catch (Exception ex)
        {
            errorCode = "malformed_output";
            errorMessage = "Model output is not valid JSON conforming to output schema: " + ex.Message;
            return false;
        }

        if (parsed.Items is null || parsed.Items.Length == 0)
        {
            errorCode = "schema_violation";
            errorMessage = "Model output contains no structured items.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates one bounded synthesis output against the owner-selected input
    /// set. Every item must carry an item-local grounding set whose entries name
    /// a selected input at its pinned revision and copy a verbatim passage from
    /// that input: the grounding set is the item's complete evidence boundary,
    /// so an item declared to span inputs must ground in at least two distinct
    /// inputs. A synthesis item carries no source occurrence time, because it is
    /// a derived statement about its inputs rather than an observed event. The
    /// validator checks verbatim presence within the named input, not semantic
    /// support, and never reads a level or an input count as evidence.
    /// </summary>
    public static bool TryValidateSynthesis(
        string rawOutput,
        IReadOnlyList<JobInputText> inputs,
        out ModelJobOutput? validatedOutput,
        out string? errorCode,
        out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        validatedOutput = null;
        errorCode = null;
        errorMessage = null;

        if (inputs.Count == 0)
        {
            errorCode = "schema_violation";
            errorMessage = "A synthesis output requires at least one selected input.";
            return false;
        }

        if (!TryParseOutput(rawOutput, out var parsed, out errorCode, out errorMessage) || parsed is null)
        {
            return false;
        }

        foreach (var item in parsed.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Kind) || !SynthesisKinds.Contains(item.Kind))
            {
                errorCode = "schema_violation";
                errorMessage = $"Synthesis item kind '{item.Kind}' is not an approved synthesis kind.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > MaximumTitleLength)
            {
                errorCode = "schema_violation";
                errorMessage = "Item title must be non-empty and at most 256 characters.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.Body) || item.Body.Length > MaximumBodyLength)
            {
                errorCode = "schema_violation";
                errorMessage = "Item body must be non-empty and at most 16,384 characters.";
                return false;
            }

            if (item.OccurredAtSourceText is not null)
            {
                errorCode = "timestamp_not_applicable";
                errorMessage = "A synthesis item is a derived statement about its inputs, so it carries no source occurrence time.";
                return false;
            }

            if (item.Grounding is null ||
                item.Grounding.Length is 0 or > MemoryEnvelopeCodec.MaximumSources)
            {
                errorCode = "schema_violation";
                errorMessage = "A synthesis item requires a bounded, non-empty grounding set.";
                return false;
            }

            var groundedInputs = new HashSet<string>(StringComparer.Ordinal);
            var seenExcerpts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var grounding in item.Grounding)
            {
                var input = inputs.FirstOrDefault(candidate => string.Equals(candidate.ResourceId, grounding.InputId, StringComparison.Ordinal));
                if (input is null)
                {
                    errorCode = "ungrounded_input";
                    errorMessage = $"Item grounding names input '{grounding.InputId}', which the owner did not select for this job.";
                    return false;
                }

                if (grounding.InputRevision != input.Revision)
                {
                    errorCode = "ungrounded_input";
                    errorMessage = $"Item grounding names input '{grounding.InputId}' at revision {grounding.InputRevision}, but the job is pinned to revision {input.Revision}.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(grounding.Excerpt) || grounding.Excerpt.Length > MaximumExcerptLength)
                {
                    errorCode = "schema_violation";
                    errorMessage = "An item grounding excerpt must be non-empty and bounded.";
                    return false;
                }

                if (!input.Text.Contains(grounding.Excerpt, StringComparison.Ordinal))
                {
                    errorCode = "grounding_failure";
                    errorMessage = $"An item grounding excerpt is not verbatim text of input '{grounding.InputId}'.";
                    return false;
                }

                if (!seenExcerpts.Add(grounding.InputId + "\u0001" + grounding.Excerpt))
                {
                    errorCode = "schema_violation";
                    errorMessage = "A repeated grounding excerpt does not count as a second source.";
                    return false;
                }

                groundedInputs.Add(grounding.InputId);
            }

            if (item.CrossSource && groundedInputs.Count < 2)
            {
                errorCode = "grounding_failure";
                errorMessage = "An item declared to span inputs must ground in at least two distinct selected inputs.";
                return false;
            }

            if (!ApplicabilityIsGrounded(item, out var applicabilityError))
            {
                errorCode = "applicability_ungrounded";
                errorMessage = applicabilityError;
                return false;
            }
        }

        validatedOutput = parsed;
        return true;
    }

    private static bool ApplicabilityIsGrounded(ModelJobStructuredItem item, out string? errorMessage)
    {
        errorMessage = null;
        if (item.Applicability is null)
        {
            return true;
        }

        var excerpts = item.Grounding ?? Array.Empty<ModelJobGrounding>();
        if (!string.IsNullOrWhiteSpace(item.Applicability.Version) &&
            !excerpts.Any(excerpt => excerpt.Excerpt.Contains(item.Applicability.Version, StringComparison.Ordinal)))
        {
            errorMessage = $"Item applicability version '{item.Applicability.Version}' is not grounded in the item's own excerpts.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(item.Applicability.Project) &&
            !excerpts.Any(excerpt => excerpt.Excerpt.Contains(item.Applicability.Project, StringComparison.OrdinalIgnoreCase)))
        {
            errorMessage = $"Item applicability project '{item.Applicability.Project}' is not grounded in the item's own excerpts.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(item.Applicability.Environment) &&
            !excerpts.Any(excerpt => excerpt.Excerpt.Contains(item.Applicability.Environment, StringComparison.OrdinalIgnoreCase)))
        {
            errorMessage = $"Item applicability environment '{item.Applicability.Environment}' is not grounded in the item's own excerpts.";
            return false;
        }

        return true;
    }
}
