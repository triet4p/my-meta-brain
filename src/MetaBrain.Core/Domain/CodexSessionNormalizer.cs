using System.Globalization;
using System.Text.Json;
using MetaBrain.Core.Security;

namespace MetaBrain.Core.Domain;

/// <summary>
/// S3-T2 Codex session JSONL normalizer: read-only interpretation of one Codex
/// <c>rollout</c> session file (the format the Codex CLI and Codex Desktop
/// write under their sessions directory).
/// <para>
/// <b>Supported schema and where it comes from.</b> The persisted envelope is
/// one JSON object per line carrying <c>{timestamp, ordinal?, type, payload}</c>
/// — confirmed both by an owner-authorized real session (writer
/// <c>Codex Desktop 0.153.0-alpha.5</c>) and by the upstream Codex sources
/// (<c>RolloutLine</c>/<c>RolloutItemWire</c>). The envelope-level record types
/// supported here are <c>session_meta</c>, <c>response_item</c>,
/// <c>event_msg</c>, <c>turn_context</c>, <c>world_state</c>,
/// <c>token_usage_record</c>, <c>compacted</c>,
/// <c>inter_agent_communication</c>, <c>inter_agent_communication_metadata</c>,
/// <c>retained_context</c>, <c>security_risk_score</c> and
/// <c>realtime_item</c>. The <c>response_item</c> payload types supported here
/// are <c>message</c> (roles <c>user</c>/<c>assistant</c>/<c>developer</c>),
/// <c>reasoning</c>, <c>function_call</c>, <c>function_call_output</c>,
/// <c>custom_tool_call</c>, <c>custom_tool_call_output</c>,
/// <c>local_shell_call</c>, <c>tool_search_call</c>, <c>tool_search_output</c>,
/// <c>web_search_call</c>, <c>image_generation_call</c>, <c>agent_message</c>,
/// <c>additional_tools</c>, <c>compaction</c>, <c>context_compaction</c>,
/// <c>configuration_update</c> and <c>compaction_trigger</c>. The
/// <c>event_msg</c> payload types observed directly were <c>task_started</c>,
/// <c>item_completed</c>, <c>token_count</c> and <c>task_complete</c>.
/// </para>
/// <para>
/// <b>Anything else is refused explicitly.</b> A record shape that neither the
/// authorized sample nor the upstream definition shows produces an explicit
/// unsupported diagnostic naming the observed token, and the checkpoint never
/// advances past it: no record is skipped silently, no shape is guessed
/// because a later version "probably" looks similar, and nothing is accepted
/// as valid just because it parsed as JSON.
/// </para>
/// <para>
/// <b>Attribution is only what the source declares.</b> A normalized event is
/// attributed <see cref="SourceEventAuthor.User"/> solely when the source
/// declares role <c>user</c> on a message record. Tool calls are attributed to
/// the assistant that issued them, tool outputs to the tool that returned
/// them, runtime notifications and control records to the system, so a tool
/// result can never be rewritten as an owner statement (R12). Every accepted
/// record yields exactly one normalized event carrying identity, attribution,
/// the record timestamp, a digest over the exact consumed bytes and the exact
/// consumed span — never a copy of the body, because downstream consumers
/// re-read the original bytes through the span (R16/R18).
/// </para>
/// </summary>
public sealed class CodexSessionNormalizer : ISourceRecordNormalizer
{
    /// <summary>
    /// Owner-declared enrollment format for a Codex session rollout file.
    /// </summary>
    public const string FormatName = "codex.session.jsonl";

    /// <summary>
    /// First observed schema generation of that format. A schema the authorized
    /// sample and the upstream definition did not show is refused by this
    /// version instead of being stretched to fit.
    /// </summary>
    public const int FormatVersionValue = 1;

    private const int MaximumEventIdLength = 128;
    private const int MaximumSessionIdentityLength = 100;
    private const int MaximumDiagnosticCodeLength = 64;
    private const int MaximumSubtypeLength = 48;

    public string Format => FormatName;

    public int FormatVersion => FormatVersionValue;

    public SourceRecordDecode Decode(ReadOnlySpan<byte> buffer, SourceRecordContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Framing: one record per line. A trailing record without its
        // terminator is an incomplete record, never a complete one, so it stays
        // unconsumed until the rest of the line exists.
        var newline = buffer.IndexOf((byte)'\n');
        if (newline < 0)
        {
            return new SourceRecordDecode(SourceRecordDecodeKind.NeedMoreBytes);
        }

        var recordEnd = newline + 1;
        var line = buffer[..newline];
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        if (line.IsEmpty)
        {
            // A blank line is not a skip: it is a record nobody can interpret,
            // so ingestion stops explicitly instead of quietly stepping over it.
            return Unsupported("codex_record_blank");
        }

        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(line);
        }
        catch (JsonException)
        {
            return Unsupported("codex_record_json_invalid");
        }

        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Unsupported("codex_record_not_object");
            }

            if (!TryReadString(root, "type", out var recordType) || recordType.Length == 0)
            {
                return Unsupported("codex_envelope_type_missing");
            }

            if (!TryReadString(root, "timestamp", out var timestamp))
            {
                return Unsupported("codex_record_time_missing");
            }

            if (!TryParseTimestamp(timestamp, out var occurredAtUtc))
            {
                return Unsupported("codex_record_time_invalid");
            }

            long? ordinal = null;
            if (root.TryGetProperty("ordinal", out var ordinalElement))
            {
                if (ordinalElement.ValueKind != JsonValueKind.Number ||
                    !ordinalElement.TryGetInt64(out var parsedOrdinal) || parsedOrdinal < 0)
                {
                    return Unsupported("codex_record_ordinal_invalid");
                }

                ordinal = parsedOrdinal;
            }

            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return Unsupported("codex_payload_not_object");
            }

            // The digest covers the exact consumed bytes, terminator included,
            // so a stored event stays bound to bytes that still exist at its span.
            var digest = SourceFingerprint.Compute(buffer[..recordEnd]);
            var defaultEventId = EventIdFor(context.SourceId, ordinal, context.RecordOffset);

            switch (recordType)
            {
                case "session_meta":
                    return SessionMeta(payload, recordEnd, digest, occurredAtUtc);
                case "response_item":
                    return ResponseItem(payload, defaultEventId, recordEnd, digest, occurredAtUtc);
                case "event_msg":
                    return EventMessage(payload, defaultEventId, recordEnd, digest, occurredAtUtc);
                case "turn_context":
                case "world_state":
                case "token_usage_record":
                case "compacted":
                case "inter_agent_communication":
                case "inter_agent_communication_metadata":
                case "retained_context":
                case "security_risk_score":
                case "realtime_item":
                    return Consumed(recordEnd, defaultEventId, SourceEventAuthor.System,
                        "codex." + recordType, digest, occurredAtUtc);
                default:
                    return Unsupported(Token("codex_type_unsupported.", recordType));
            }
        }
    }

    /// <summary>
    /// Session identity is retained as a first-class event whose ID carries the
    /// session UUID the source itself declared, so every other event of the
    /// same enrolled generation is provably part of that session without any
    /// body text being copied into the journal.
    /// </summary>
    private static SourceRecordDecode SessionMeta(
        JsonElement payload,
        int recordEnd,
        string digest,
        DateTimeOffset occurredAtUtc)
    {
        const string eventIdPrefix = "codex-session-";
        var identity = FirstString(payload, "id", "session_id");
        if (identity is null || !IsSessionIdentity(identity) ||
            eventIdPrefix.Length + identity.Length > MaximumEventIdLength)
        {
            return Unsupported("codex_session_identity_invalid");
        }

        return Consumed(recordEnd, eventIdPrefix + identity, SourceEventAuthor.System,
            "codex.session_meta", digest, occurredAtUtc);
    }

    private static SourceRecordDecode ResponseItem(
        JsonElement payload,
        string defaultEventId,
        int recordEnd,
        string digest,
        DateTimeOffset occurredAtUtc)
    {
        if (!TryReadString(payload, "type", out var payloadType) || payloadType.Length == 0)
        {
            return Unsupported("codex_item_type_missing");
        }

        switch (payloadType)
        {
            case "message":
                return Message(payload, defaultEventId, recordEnd, digest, occurredAtUtc);
            case "reasoning":
                return Consumed(recordEnd, defaultEventId, SourceEventAuthor.Assistant,
                    "codex.response_item.reasoning", digest, occurredAtUtc);
            case "function_call":
                return Requires(payload, "call_id", "name", "arguments")
                    ? Consumed(recordEnd, defaultEventId, SourceEventAuthor.Assistant,
                        "codex.response_item.function_call", digest, occurredAtUtc)
                    : Unsupported("codex_call_identity_invalid");
            case "function_call_output":
                return IsToolOutput(payload)
                    ? Consumed(recordEnd, defaultEventId, SourceEventAuthor.Tool,
                        "codex.response_item.function_call_output", digest, occurredAtUtc)
                    : Unsupported("codex_call_output_invalid");
            case "custom_tool_call":
                return Requires(payload, "call_id", "name", "input")
                    ? Consumed(recordEnd, defaultEventId, SourceEventAuthor.Assistant,
                        "codex.response_item.custom_tool_call", digest, occurredAtUtc)
                    : Unsupported("codex_custom_call_invalid");
            case "custom_tool_call_output":
                return Requires(payload, "call_id") && IsToolOutput(payload)
                    ? Consumed(recordEnd, defaultEventId, SourceEventAuthor.Tool,
                        "codex.response_item.custom_tool_call_output", digest, occurredAtUtc)
                    : Unsupported("codex_custom_call_output_invalid");
            case "local_shell_call":
                return Requires(payload, "status") && payload.TryGetProperty("action", out var action) &&
                       action.ValueKind == JsonValueKind.Object
                    ? Consumed(recordEnd, defaultEventId, SourceEventAuthor.Assistant,
                        "codex.response_item.local_shell_call", digest, occurredAtUtc)
                    : Unsupported("codex_shell_call_invalid");
            case "tool_search_call":
                return Requires(payload, "execution", "arguments")
                    ? Consumed(recordEnd, defaultEventId, SourceEventAuthor.Assistant,
                        "codex.response_item.tool_search_call", digest, occurredAtUtc)
                    : Unsupported("codex_tool_search_call_invalid");
            case "tool_search_output":
                return Requires(payload, "status", "execution", "tools")
                    ? Consumed(recordEnd, defaultEventId, SourceEventAuthor.Tool,
                        "codex.response_item.tool_search_output", digest, occurredAtUtc)
                    : Unsupported("codex_tool_search_output_invalid");
            case "web_search_call":
                return Consumed(recordEnd, defaultEventId, SourceEventAuthor.Assistant,
                    "codex.response_item.web_search_call", digest, occurredAtUtc);
            case "image_generation_call":
                return Requires(payload, "status", "result")
                    ? Consumed(recordEnd, defaultEventId, SourceEventAuthor.Assistant,
                        "codex.response_item.image_generation_call", digest, occurredAtUtc)
                    : Unsupported("codex_image_generation_invalid");
            case "agent_message":
                return Requires(payload, "author", "recipient", "content")
                    ? Consumed(recordEnd, defaultEventId, SourceEventAuthor.Assistant,
                        "codex.response_item.agent_message", digest, occurredAtUtc)
                    : Unsupported("codex_agent_message_invalid");
            case "additional_tools":
                return Requires(payload, "role", "tools")
                    ? Consumed(recordEnd, defaultEventId, SourceEventAuthor.System,
                        "codex.response_item.additional_tools", digest, occurredAtUtc)
                    : Unsupported("codex_additional_tools_invalid");
            case "compaction":
                return Requires(payload, "encrypted_content")
                    ? Consumed(recordEnd, defaultEventId, SourceEventAuthor.System,
                        "codex.response_item.compaction", digest, occurredAtUtc)
                    : Unsupported("codex_compaction_invalid");
            case "configuration_update":
                return payload.TryGetProperty("reasoning", out var reasoning) &&
                       reasoning.ValueKind == JsonValueKind.Object
                    ? Consumed(recordEnd, defaultEventId, SourceEventAuthor.System,
                        "codex.response_item.configuration_update", digest, occurredAtUtc)
                    : Unsupported("codex_configuration_update_invalid");
            case "compaction_trigger":
            case "context_compaction":
                return Consumed(recordEnd, defaultEventId, SourceEventAuthor.System,
                    "codex.response_item." + payloadType, digest, occurredAtUtc);
            default:
                return Unsupported(Token("codex_item_type_unsupported.", payloadType));
        }
    }

    /// <summary>
    /// A message record's attribution is the role the source declared. Only a
    /// declared <c>user</c> role is attributed to the owner, so tool output,
    /// reasoning and developer instructions can never surface as a user
    /// statement even when their text is quoted inside a message.
    /// </summary>
    private static SourceRecordDecode Message(
        JsonElement payload,
        string defaultEventId,
        int recordEnd,
        string digest,
        DateTimeOffset occurredAtUtc)
    {
        if (!TryReadString(payload, "role", out var role) || role.Length == 0)
        {
            return Unsupported("codex_message_role_missing");
        }

        SourceEventAuthor author;
        switch (role)
        {
            case "user":
                author = SourceEventAuthor.User;
                break;
            case "assistant":
                author = SourceEventAuthor.Assistant;
                break;
            case "developer":
                // Declared developer/system instructions, not owner statements.
                author = SourceEventAuthor.System;
                break;
            default:
                return Unsupported(Token("codex_message_role_unsupported.", role));
        }

        if (!payload.TryGetProperty("content", out var content) ||
            content.ValueKind is not (JsonValueKind.Null or JsonValueKind.Array))
        {
            return Unsupported("codex_message_content_invalid");
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object || !TryReadString(part, "type", out _))
                {
                    return Unsupported("codex_message_content_invalid");
                }
            }
        }

        return Consumed(recordEnd, defaultEventId, author,
            "codex.response_item.message." + role, digest, occurredAtUtc);
    }

    /// <summary>
    /// Runtime notifications are system records even when their payload echoes
    /// a message item (for example an <c>item_completed</c> event carrying a
    /// <c>UserMessage</c> item): the attributed content event is the message
    /// record itself, and the notification stays a notification.
    /// </summary>
    private static SourceRecordDecode EventMessage(
        JsonElement payload,
        string defaultEventId,
        int recordEnd,
        string digest,
        DateTimeOffset occurredAtUtc)
    {
        if (!TryReadString(payload, "type", out var payloadType) || payloadType.Length == 0)
        {
            return Unsupported("codex_event_msg_type_missing");
        }

        var kind = "codex.event_msg." + Subtype(payloadType);
        return Consumed(recordEnd, defaultEventId, SourceEventAuthor.System, kind, digest, occurredAtUtc);
    }

    private static string EventIdFor(string sourceId, long? ordinal, long recordOffset) =>
        "codex-" + sourceId + (ordinal is { } position
            ? "-o" + position.ToString(CultureInfo.InvariantCulture)
            : "-b" + recordOffset.ToString(CultureInfo.InvariantCulture));

    private static SourceRecordDecode Consumed(
        int recordEnd,
        string eventId,
        SourceEventAuthor author,
        string payloadKind,
        string digest,
        DateTimeOffset occurredAtUtc)
    {
        var draft = new SourceEventDraft(eventId, author, occurredAtUtc, payloadKind, digest);
        return new SourceRecordDecode(SourceRecordDecodeKind.Consumed, recordEnd, draft);
    }

    private static SourceRecordDecode Unsupported(string code) =>
        new(SourceRecordDecodeKind.Unsupported, 0, null, code);

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string? FirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryReadString(element, propertyName, out var value) && value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Every named property must be present. Presence is what makes the
    /// declared attribution provable: a tool call without its call identity or
    /// a tool output without its result is not a record to interpret, it is a
    /// record to refuse.
    /// </summary>
    private static bool Requires(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property) ||
                property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A tool output body is either a plain string or an array of structured
    /// content items, as the upstream format defines. The tool result stays
    /// attributed to the tool; only its digest and span are stored.
    /// </summary>
    private static bool IsToolOutput(JsonElement payload)
    {
        if (!payload.TryGetProperty("output", out var output))
        {
            return false;
        }

        if (output.ValueKind == JsonValueKind.String)
        {
            return true;
        }

        if (output.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !TryReadString(item, "type", out _))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            timestamp = parsed.ToUniversalTime();
            return true;
        }

        timestamp = default;
        return false;
    }

    private static bool IsSessionIdentity(string value)
    {
        if (value.Length is 0 or > MaximumSessionIdentityLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Bounded schema-vocabulary token for a diagnostic code. The token is a
    /// record or role name, never payload content, and is capped so the code
    /// stays inside the shared diagnostic-code grammar.
    /// </summary>
    private static string Token(string prefix, string value)
    {
        var room = MaximumDiagnosticCodeLength - prefix.Length;
        var kept = 0;
        while (kept < value.Length && kept < room && kept < MaximumDiagnosticCodeLength)
        {
            var character = value[kept];
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                break;
            }

            kept++;
        }

        return prefix + (kept == 0 ? "unnamed" : value[..kept]);
    }

    /// <summary>
    /// Bounded schema-vocabulary subtype for a payload kind. The subtype names
    /// the declared record shape; an untypable subtype refuses the record
    /// instead of inventing a kind.
    /// </summary>
    private static string Subtype(string value)
    {
        var kept = 0;
        while (kept < value.Length && kept < MaximumSubtypeLength)
        {
            var character = value[kept];
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                break;
            }

            kept++;
        }

        return kept == 0 ? "unnamed" : value[..kept];
    }
}
