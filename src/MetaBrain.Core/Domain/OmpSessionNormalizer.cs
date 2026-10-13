using System.Globalization;
using System.Text.Json;
using MetaBrain.Core.Security;

namespace MetaBrain.Core.Domain;

/// <summary>
/// S3-T3 OMP session JSONL normalizer: read-only interpretation of one OMP
/// agent session file (the format the OMP runtime writes under its
/// per-project sessions directory).
/// <para>
/// <b>Supported schema and where it comes from.</b> The persisted envelope is
/// one JSON object per line. The owner-declared enrollment format is
/// <see cref="FormatName"/> version <see cref="FormatVersionValue"/>. Every
/// supported record shape below was observed in exactly one owner-authorized
/// completed OMP session for this project (writer: the OMP runtime; session
/// record <c>version</c> 3) and is grounded in that sample: no upstream OMP
/// format definition is readable under the owner consent, so no unobserved
/// shape is claimed or stretched to fit. The supported record types are
/// <c>title</c> (<c>v</c> 1), <c>session</c> (version 3, the branch root
/// carrying the declared session identity and cwd context), <c>message</c>
/// (roles <c>user</c>, <c>assistant</c>, <c>toolResult</c>), <c>custom</c>
/// (envelope <c>{customType, data}</c>; the customType token is folded into
/// the payload kind, the data is never interpreted), <c>custom_message</c>
/// (envelope <c>{customType, content, display, details, attribution}</c> with
/// declared attribution <c>user</c> or <c>agent</c>),
/// <c>thinking_level_change</c>, <c>service_tier_change</c>,
/// <c>credential_pin</c>, <c>title_change</c> and <c>compaction</c>. Message
/// content parts support the observed shapes <c>text</c>, <c>thinking</c> and
/// <c>toolCall</c>. Record timestamps are ISO-8601 strings on the envelope
/// (<c>updatedAt</c> on the title record); a message body additionally
/// carries its own <c>message.timestamp</c> as Unix epoch milliseconds
/// (numbers), which is the instant the normalized event keeps.
/// </para>
/// <para>
/// <b>Anything else is refused explicitly.</b> A record shape that the
/// authorized sample did not show produces an explicit unsupported diagnostic
/// naming the observed token, and the checkpoint never advances past it: no
/// record is skipped silently, no version or role is guessed because a later
/// writer "probably" looks similar, and nothing is accepted as valid just
/// because it parsed as JSON.
/// </para>
/// <para>
/// <b>Event structure is preserved, not flattened.</b> The OMP session is an
/// id/parentId chain: every event record declares its own <c>id</c> and its
/// <c>parentId</c> (null only at a chain root). This normalizer validates that
/// chain grammar per record — a missing, malformed or absent identity or
/// parent reference is an explicit refusal — and it carries each record's
/// declared identity inside the normalized event ID, so the journal keeps the
/// session's event structure (which declared node produced which event, under
/// which branch) while the exact span and digest still bind every event to
/// the bytes that declare its parent edge (R16/R18). The <c>session</c> record
/// is a first-class identity event carrying the declared session UUID, and its
/// span covers the record that declares the cwd context; <c>compaction</c>
/// records keep their declared <c>firstKeptEntryId</c> chain boundary as a
/// required, validated reference.
/// </para>
/// <para>
/// <b>Attribution is only what the source declares.</b> A <c>message</c>
/// record declaring role <c>user</c> is attributed to
/// <see cref="SourceEventAuthor.User"/> (and must itself declare attribution
/// <c>user</c>); role <c>assistant</c> is Assistant; role <c>toolResult</c> is
/// Tool — a tool output can never be stored as a user statement (R12). A
/// <c>custom_message</c> is attributed by its declared <c>attribution</c>
/// field (<c>user</c> → User, <c>agent</c> → Assistant); any other declared
/// attribution value is refused rather than mapped. Runtime/control records
/// (title, session, custom, config changes, credential pin, compaction) are
/// System. Every accepted record yields exactly one normalized event carrying
/// identity, attribution, the record timestamp, a digest over the exact
/// consumed bytes and the exact consumed span — never a copy of the body,
/// because downstream consumers re-read the original bytes through the span
/// (R16/R18).
/// </para>
/// </summary>
public sealed class OmpSessionNormalizer : ISourceRecordNormalizer
{
    /// <summary>
    /// Owner-declared enrollment format for an OMP agent session file.
    /// </summary>
    public const string FormatName = "omp.session.jsonl";

    /// <summary>
    /// First observed schema generation of that format. The one authorized
    /// session declares session <c>version</c> 3 and title <c>v</c> 1; a
    /// session declaring any other version is refused by this version instead
    /// of being stretched to fit.
    /// </summary>
    public const int FormatVersionValue = 1;

    private const int SupportedSessionVersion = 3;
    private const int SupportedTitleVersion = 1;
    private const int MaximumDeclaredEventIdLength = 64;
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
            return Unsupported("omp_record_blank");
        }

        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(line);
        }
        catch (JsonException)
        {
            return Unsupported("omp_record_json_invalid");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return Unsupported("omp_record_not_object");
        }

        if (!TryReadString(root, "type", out var recordType) || recordType.Length == 0)
        {
            return Unsupported("omp_envelope_type_missing");
        }

        // The digest covers the exact consumed bytes, terminator included, so a
        // stored event stays bound to bytes that still exist at its span.
        var digest = SourceFingerprint.Compute(buffer[..recordEnd]);

        switch (recordType)
        {
            case "title":
                return Title(root, context, recordEnd, digest);
            case "session":
                return Session(root, context, recordEnd, digest);
            case "message":
                return Message(root, context, recordEnd, digest);
            case "custom":
                return Custom(root, context, recordEnd, digest);
            case "custom_message":
                return CustomMessage(root, context, recordEnd, digest);
            case "thinking_level_change":
                return ThinkingLevelChange(root, context, recordEnd, digest);
            case "service_tier_change":
                return ServiceTierChange(root, context, recordEnd, digest);
            case "credential_pin":
                return CredentialPin(root, context, recordEnd, digest);
            case "title_change":
                return TitleChange(root, context, recordEnd, digest);
            case "compaction":
                return Compaction(root, context, recordEnd, digest);
            default:
                return Unsupported(Token("omp_type_unsupported.", recordType));
        }
    }

    /// <summary>
    /// The session header record: the branch root of the id/parentId chain and
    /// the declaration of session identity plus the cwd context. Its event ID
    /// carries the declared session UUID, and its span covers the record that
    /// declares the context, so identity and context stay provably bound to
    /// the source bytes.
    /// </summary>
    private static SourceRecordDecode Session(JsonElement root, SourceRecordContext context, int recordEnd, string digest)
    {
        if (!root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var sessionVersion))
        {
            return Unsupported("omp_session_version_missing");
        }

        if (sessionVersion != SupportedSessionVersion)
        {
            return Unsupported(Token("omp_session_version_unsupported.", sessionVersion.ToString(CultureInfo.InvariantCulture)));
        }

        var identity = FirstString(root, "id");
        if (identity is null || !IsEventToken(identity))
        {
            return Unsupported("omp_session_identity_invalid");
        }

        var eventId = "omp-session-" + identity;
        if (eventId.Length > SourceIngestionLimits.MaximumEventIdLength)
        {
            return Unsupported("omp_event_id_too_long");
        }

        if (!TryReadTimestamp(root, "timestamp", out var occurredAtUtc))
        {
            return Unsupported("omp_record_time_invalid");
        }

        if (!TryReadString(root, "cwd", out var cwd) || cwd.Length == 0)
        {
            return Unsupported("omp_session_context_invalid");
        }

        return Consumed(recordEnd, eventId, SourceEventAuthor.System, "omp.session", digest, occurredAtUtc);
    }

    /// <summary>
    /// The title record: no declared event id, so its identity is anchored to
    /// its byte offset inside the generation (the same fallback the Codex
    /// normalizer uses for records without a declared identity).
    /// </summary>
    private static SourceRecordDecode Title(JsonElement root, SourceRecordContext context, int recordEnd, string digest)
    {
        if (!root.TryGetProperty("v", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var titleVersion))
        {
            return Unsupported("omp_title_version_missing");
        }

        if (titleVersion != SupportedTitleVersion)
        {
            return Unsupported(Token("omp_title_version_unsupported.", titleVersion.ToString(CultureInfo.InvariantCulture)));
        }

        if (!TryReadString(root, "title", out var title) || title.Length == 0 ||
            !TryReadString(root, "source", out var source) || source.Length == 0)
        {
            return Unsupported("omp_title_shape_invalid");
        }

        if (!TryReadTimestamp(root, "updatedAt", out var occurredAtUtc))
        {
            return Unsupported("omp_record_time_invalid");
        }

        var eventId = "omp-" + context.SourceId + "-b" + context.RecordOffset.ToString(CultureInfo.InvariantCulture);
        return Consumed(recordEnd, eventId, SourceEventAuthor.System, "omp.title", digest, occurredAtUtc);
    }

    /// <summary>
    /// The attributed content record. Attribution is the role the source
    /// declared, and the declared event id is carried into the event ID so the
    /// id/parentId chain survives normalization.
    /// </summary>
    private static SourceRecordDecode Message(JsonElement root, SourceRecordContext context, int recordEnd, string digest)
    {
        var declaredId = DeclaredEventId(root, out var idFailure);
        if (declaredId is null)
        {
            return idFailure!;
        }

        if (!TryReadTimestamp(root, "timestamp", out _))
        {
            return Unsupported("omp_record_time_invalid");
        }

        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            return Unsupported("omp_message_shape_invalid");
        }

        if (!TryReadString(message, "role", out var role) || role.Length == 0)
        {
            return Unsupported("omp_message_role_missing");
        }

        switch (role)
        {
            case "user":
                return UserMessage(root, message, context, recordEnd, digest, declaredId);
            case "assistant":
                return AssistantMessage(root, message, context, recordEnd, digest, declaredId);
            case "toolResult":
                return ToolResultMessage(root, message, context, recordEnd, digest, declaredId);
            default:
                return Unsupported(Token("omp_message_role_unsupported.", role));
        }
    }

    /// <summary>
    /// A declared user message is the only owner-attributed shape, and the
    /// source must itself declare attribution <c>user</c>: a user-role record
    /// declaring any other attribution is refused instead of being stored as
    /// an owner statement.
    /// </summary>
    private static SourceRecordDecode UserMessage(
        JsonElement root,
        JsonElement message,
        SourceRecordContext context,
        int recordEnd,
        string digest,
        string declaredId)
    {
        if (!TryReadTimestamp(message, "timestamp", out var occurredAtUtc))
        {
            return Unsupported("omp_message_time_invalid");
        }

        if (message.TryGetProperty("attribution", out var attribution) &&
            attribution.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(message, "attribution", out var attributionValue) ||
                !string.Equals(attributionValue, "user", StringComparison.Ordinal))
            {
                return Unsupported(Token("omp_message_attribution_unsupported.",
                    attributionValue.Length > 0 ? attributionValue : "unnamed"));
            }
        }

        var parts = MessageParts(message);
        if (parts is not null)
        {
            return parts;
        }

        return DeclaredEvent(recordEnd, context, declaredId, SourceEventAuthor.User,
            "omp.message.user", digest, occurredAtUtc);
    }

    /// <summary>
    /// An assistant message: attributed to the assistant that produced it. The
    /// stop reason is a closed observed set, so a writer emitting a new stop
    /// reason is an explicit refusal rather than a stretch of this schema.
    /// </summary>
    private static SourceRecordDecode AssistantMessage(
        JsonElement root,
        JsonElement message,
        SourceRecordContext context,
        int recordEnd,
        string digest,
        string declaredId)
    {
        if (!TryReadTimestamp(message, "timestamp", out var occurredAtUtc))
        {
            return Unsupported("omp_message_time_invalid");
        }

        if (!TryReadString(message, "stopReason", out var stopReason) || stopReason.Length == 0)
        {
            return Unsupported("omp_message_stop_reason_missing");
        }

        switch (stopReason)
        {
            case "stop":
            case "toolUse":
            case "error":
                break;
            default:
                return Unsupported(Token("omp_message_stop_reason_unsupported.", stopReason));
        }

        var parts = MessageParts(message);
        if (parts is not null)
        {
            return parts;
        }

        return DeclaredEvent(recordEnd, context, declaredId, SourceEventAuthor.Assistant,
            "omp.message.assistant", digest, occurredAtUtc);
    }

    /// <summary>
    /// A tool result: attributed to the tool that returned it, never to the
    /// owner. The tool call identity, tool name and error flag are required
    /// because they are what makes the tool attribution provable.
    /// </summary>
    private static SourceRecordDecode ToolResultMessage(
        JsonElement root,
        JsonElement message,
        SourceRecordContext context,
        int recordEnd,
        string digest,
        string declaredId)
    {
        if (!TryReadTimestamp(message, "timestamp", out var occurredAtUtc))
        {
            return Unsupported("omp_message_time_invalid");
        }

        if (!TryReadString(message, "toolCallId", out var toolCallId) || toolCallId.Length == 0 ||
            !TryReadString(message, "toolName", out var toolName) || toolName.Length == 0 ||
            !message.TryGetProperty("isError", out var isError) ||
            isError.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return Unsupported("omp_message_tool_call_invalid");
        }

        var parts = MessageParts(message);
        if (parts is not null)
        {
            return parts;
        }

        return DeclaredEvent(recordEnd, context, declaredId, SourceEventAuthor.Tool,
            "omp.message.toolResult", digest, occurredAtUtc);
    }

    /// <summary>
    /// Content is an array of closed-shape parts. An unobserved part type is
    /// an unobserved content shape and is refused; a tool-call part must
    /// declare its call identity so the tool attribution stays bound to a
    /// concrete call. The part bodies are never interpreted or stored.
    /// </summary>
    private static SourceRecordDecode? MessageParts(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return Unsupported("omp_message_content_invalid");
        }

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
            {
                return Unsupported("omp_message_part_invalid");
            }

            if (!TryReadString(part, "type", out var partType) || partType.Length == 0)
            {
                return Unsupported("omp_message_part_type_missing");
            }

            switch (partType)
            {
                case "text":
                case "thinking":
                    break;
                case "toolCall":
                    if (!TryReadString(part, "id", out var callId) || callId.Length == 0 ||
                        !TryReadString(part, "name", out var callName) || callName.Length == 0)
                    {
                        return Unsupported("omp_message_part_invalid");
                    }

                    if (part.TryGetProperty("arguments", out var arguments) &&
                        arguments.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                    {
                        return Unsupported("omp_message_part_invalid");
                    }

                    break;
                default:
                    return Unsupported(Token("omp_message_part_unsupported.", partType));
            }
        }

        return null;
    }

    /// <summary>
    /// The extension envelope <c>{customType, data}</c>: the envelope is
    /// validated and the customType token is folded into the payload kind; the
    /// opaque data is never interpreted, exactly like the runtime notification
    /// envelopes the Codex normalizer accepts. These records are
    /// system-attributed runtime state.
    /// </summary>
    private static SourceRecordDecode Custom(JsonElement root, SourceRecordContext context, int recordEnd, string digest)
    {
        var declaredId = DeclaredEventId(root, out var idFailure);
        if (declaredId is null)
        {
            return idFailure!;
        }

        if (!TryReadTimestamp(root, "timestamp", out var occurredAtUtc))
        {
            return Unsupported("omp_record_time_invalid");
        }

        if (!TryReadString(root, "customType", out var customType) || customType.Length == 0)
        {
            return Unsupported("omp_custom_type_missing");
        }

        if (!root.TryGetProperty("data", out _))
        {
            return Unsupported("omp_custom_data_missing");
        }

        return DeclaredEvent(recordEnd, context, declaredId, SourceEventAuthor.System,
            "omp.custom." + Subtype(customType), digest, occurredAtUtc);
    }

    /// <summary>
    /// The attributed extension envelope: the declared attribution is the
    /// author, and only the observed attribution values are mapped — anything
    /// else is refused so no runtime record can become an owner statement by
    /// accident.
    /// </summary>
    private static SourceRecordDecode CustomMessage(JsonElement root, SourceRecordContext context, int recordEnd, string digest)
    {
        var declaredId = DeclaredEventId(root, out var idFailure);
        if (declaredId is null)
        {
            return idFailure!;
        }

        if (!TryReadTimestamp(root, "timestamp", out var occurredAtUtc))
        {
            return Unsupported("omp_record_time_invalid");
        }

        if (!TryReadString(root, "customType", out var customType) || customType.Length == 0)
        {
            return Unsupported("omp_custom_message_type_missing");
        }

        if (!TryReadString(root, "attribution", out var attribution) || attribution.Length == 0)
        {
            return Unsupported("omp_custom_message_attribution_missing");
        }

        SourceEventAuthor author;
        switch (attribution)
        {
            case "user":
                author = SourceEventAuthor.User;
                break;
            case "agent":
                author = SourceEventAuthor.Assistant;
                break;
            default:
                return Unsupported(Token("omp_custom_message_attribution_unsupported.", attribution));
        }

        if (!root.TryGetProperty("content", out _))
        {
            return Unsupported("omp_custom_message_content_missing");
        }

        return DeclaredEvent(recordEnd, context, declaredId, author,
            "omp.custom_message." + Subtype(customType), digest, occurredAtUtc);
    }

    private static SourceRecordDecode ThinkingLevelChange(JsonElement root, SourceRecordContext context, int recordEnd, string digest)
    {
        var declaredId = DeclaredEventId(root, out var idFailure);
        if (declaredId is null)
        {
            return idFailure!;
        }

        if (!TryReadTimestamp(root, "timestamp", out var occurredAtUtc))
        {
            return Unsupported("omp_record_time_invalid");
        }

        if (!TryReadString(root, "thinkingLevel", out var level) || level.Length == 0)
        {
            return Unsupported("omp_config_shape_invalid");
        }

        return DeclaredEvent(recordEnd, context, declaredId, SourceEventAuthor.System,
            "omp.thinking_level_change", digest, occurredAtUtc);
    }

    private static SourceRecordDecode ServiceTierChange(JsonElement root, SourceRecordContext context, int recordEnd, string digest)
    {
        var declaredId = DeclaredEventId(root, out var idFailure);
        if (declaredId is null)
        {
            return idFailure!;
        }

        if (!TryReadTimestamp(root, "timestamp", out var occurredAtUtc))
        {
            return Unsupported("omp_record_time_invalid");
        }

        return DeclaredEvent(recordEnd, context, declaredId, SourceEventAuthor.System,
            "omp.service_tier_change", digest, occurredAtUtc);
    }

    private static SourceRecordDecode CredentialPin(JsonElement root, SourceRecordContext context, int recordEnd, string digest)
    {
        var declaredId = DeclaredEventId(root, out var idFailure);
        if (declaredId is null)
        {
            return idFailure!;
        }

        if (!TryReadTimestamp(root, "timestamp", out var occurredAtUtc))
        {
            return Unsupported("omp_record_time_invalid");
        }

        if (!TryReadString(root, "provider", out var provider) || provider.Length == 0 ||
            !TryReadString(root, "hash", out var hash) || hash.Length == 0)
        {
            return Unsupported("omp_config_shape_invalid");
        }

        return DeclaredEvent(recordEnd, context, declaredId, SourceEventAuthor.System,
            "omp.credential_pin", digest, occurredAtUtc);
    }

    private static SourceRecordDecode TitleChange(JsonElement root, SourceRecordContext context, int recordEnd, string digest)
    {
        var declaredId = DeclaredEventId(root, out var idFailure);
        if (declaredId is null)
        {
            return idFailure!;
        }

        if (!TryReadTimestamp(root, "timestamp", out var occurredAtUtc))
        {
            return Unsupported("omp_record_time_invalid");
        }

        if (!TryReadString(root, "title", out var title) || title.Length == 0)
        {
            return Unsupported("omp_config_shape_invalid");
        }

        return DeclaredEvent(recordEnd, context, declaredId, SourceEventAuthor.System,
            "omp.title_change", digest, occurredAtUtc);
    }

    /// <summary>
    /// A compaction record marks the event-structure boundary: its declared
    /// <c>firstKeptEntryId</c> references the chain node where the retained
    /// history begins, so the reference is required and grammar-checked. The
    /// summary bodies are never interpreted or stored.
    /// </summary>
    private static SourceRecordDecode Compaction(JsonElement root, SourceRecordContext context, int recordEnd, string digest)
    {
        var declaredId = DeclaredEventId(root, out var idFailure);
        if (declaredId is null)
        {
            return idFailure!;
        }

        if (!TryReadTimestamp(root, "timestamp", out var occurredAtUtc))
        {
            return Unsupported("omp_record_time_invalid");
        }

        if (!TryReadString(root, "firstKeptEntryId", out var firstKept) || !IsEventToken(firstKept))
        {
            return Unsupported("omp_compaction_boundary_invalid");
        }

        return DeclaredEvent(recordEnd, context, declaredId, SourceEventAuthor.System,
            "omp.compaction", digest, occurredAtUtc);
    }

    /// <summary>
    /// The declared event identity of one record plus its parent reference.
    /// Both are required on every event record: the OMP session is an
    /// id/parentId chain, so a record without its node identity or parent edge
    /// is a shape this schema does not support, never a record to flatten.
    /// </summary>
    private static string? DeclaredEventId(JsonElement root, out SourceRecordDecode? failure)
    {
        failure = null;
        var declaredId = FirstString(root, "id");
        if (declaredId is null)
        {
            failure = Unsupported("omp_event_id_missing");
            return null;
        }

        if (!IsEventToken(declaredId))
        {
            failure = Unsupported("omp_event_id_invalid");
            return null;
        }

        if (!root.TryGetProperty("parentId", out var parent))
        {
            failure = Unsupported("omp_event_parent_missing");
            return null;
        }

        if (parent.ValueKind == JsonValueKind.Null)
        {
            // The chain root declares no parent; that is the branch origin.
            return declaredId;
        }

        if (!TryReadString(root, "parentId", out var parentId) || !IsEventToken(parentId))
        {
            failure = Unsupported("omp_event_parent_invalid");
            return null;
        }

        return declaredId;
    }

    private static SourceRecordDecode DeclaredEvent(
        int recordEnd,
        SourceRecordContext context,
        string declaredId,
        SourceEventAuthor author,
        string payloadKind,
        string digest,
        DateTimeOffset occurredAtUtc)
    {
        var eventId = "omp-" + context.SourceId + "-" + declaredId;
        if (eventId.Length > SourceIngestionLimits.MaximumEventIdLength)
        {
            return Unsupported("omp_event_id_too_long");
        }

        return Consumed(recordEnd, eventId, author, payloadKind, digest, occurredAtUtc);
    }

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

    private static bool TryReadTimestamp(JsonElement element, string propertyName, out DateTimeOffset timestamp)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            timestamp = default;
            return false;
        }

        // The OMP writer persists two timestamp shapes: ISO-8601 strings on
        // the record envelope (and the title record) and Unix epoch
        // milliseconds (numbers) inside message bodies. Both are observed in
        // the authorized sample; both are accepted and normalized to UTC.
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var epochMillis))
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(epochMillis);
            return true;
        }

        if (property.ValueKind == JsonValueKind.String &&
            TryParseTimestamp(property.GetString(), out var parsed))
        {
            timestamp = parsed;
            return true;
        }

        timestamp = default;
        return false;
    }

    private static bool TryParseTimestamp(string? value, out DateTimeOffset timestamp)
    {
        if (value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            timestamp = parsed.ToUniversalTime();
            return true;
        }

        timestamp = default;
        return false;
    }

    /// <summary>
    /// A declared event id or parent reference: a bounded, safe-identifier
    /// token. The chain grammar is part of the supported schema, so an
    /// out-of-grammar reference is an explicit refusal.
    /// </summary>
    private static bool IsEventToken(string value) =>
        value.Length is > 0 and <= MaximumDeclaredEventIdLength &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

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
