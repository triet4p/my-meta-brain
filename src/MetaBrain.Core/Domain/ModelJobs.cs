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

    public static bool IsSupported(string? profile) =>
        profile is null or "" or CodingSession or ResearchNote or Trend;
}

/// <summary>
/// Immutable snapshot of the approved scope boundaries for one model job.
/// Revalidated before reading source data, before provider call, and before committing output.
/// </summary>
public sealed record JobScopeSnapshot(
    string SourceResourceId,
    long SourceRevision,
    string DestinationZoneId,
    string DestinationResourceId,
    long DestinationRevision,
    string Provider,
    string Model,
    decimal MaximumCostUsd,
    long MaxInputTokens,
    long MaxOutputTokens,
    string? Profile = null);
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

public sealed record ModelJobStructuredItem(
    string Kind,
    string Title,
    string Body,
    string Excerpt,
    MemoryApplicability? Applicability = null,
    string? OccurredAtSourceText = null);
public sealed record ModelJobOutput(
    string Summary,
    ModelJobStructuredItem[] Items);

public static class JobCodec
{
    public const int CurrentSchemaVersion = 1;
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
        var record = JsonSerializer.Deserialize<StoredJobRecord>(span, JsonOptions);
        if (record is null || record.SchemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException("The stored job record schema is invalid.");
        }

        return record;
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

        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            errorCode = "malformed_output";
            errorMessage = "Model output is empty.";
            return false;
        }

        // Strict JSON parse
        ModelJobOutput parsed;
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

        foreach (var item in parsed.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Kind) || !AllowedKinds.Contains(item.Kind))
            {
                errorCode = "schema_violation";
                errorMessage = $"Item kind '{item.Kind}' is not an approved kind.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > 256)
            {
                errorCode = "schema_violation";
                errorMessage = "Item title must be non-empty and at most 256 characters.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.Body) || item.Body.Length > 16384)
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
}
