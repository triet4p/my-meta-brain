using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Index;
using MetaBrain.Core.Security;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// In-process harness for the S3-T2 Codex phase: the same production object
/// graph the named-pipe service builds (encrypted store, vault lifecycle,
/// handler, authorities, enrolled-source reader) with the real
/// <see cref="CodexSessionNormalizer"/> attached through the real registry.
/// "Restart" rebuilds the lifecycle and handler over the same vault
/// directory, which is exactly what a service process restart does.
/// </summary>
internal sealed class S3T2CodexCoordinatorHarness : IDisposable
{
    private const string SyntheticPassphrase = "s3t2-synthetic-vault-passphrase";
    private readonly string _root;
    private readonly EncryptedVaultStore _store;
    private readonly SqlitePrivateIndexStore _index;
    private bool _disposed;

    private S3T2CodexCoordinatorHarness(
        string root,
        EncryptedVaultStore store,
        SqlitePrivateIndexStore index,
        VaultLifecycle lifecycle,
        ServiceRequestHandler handler)
    {
        _root = root;
        _store = store;
        _index = index;
        Lifecycle = lifecycle;
        Handler = handler;
    }

    public string Root => _root;

    public string VaultDirectoryPath => Path.Combine(_root, "service", "vault");

    public VaultLifecycle Lifecycle { get; private set; }

    public ServiceRequestHandler Handler { get; private set; }

    public static S3T2CodexCoordinatorHarness Create()
    {
        var ownerSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows token has no user SID.");
        var root = Path.Combine(Path.GetTempPath(), "MetaBrain-S3-T2-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root) || File.Exists(root))
        {
            throw new IOException("The generated synthetic harness path already exists.");
        }

        Directory.CreateDirectory(root);
        try
        {
            SmokeFixture.SetRestrictedDirectoryAcl(root, ownerSid);
            var serviceDirectory = Path.Combine(root, "service");
            Directory.CreateDirectory(serviceDirectory);
            SmokeFixture.SetRestrictedDirectoryAcl(serviceDirectory, ownerSid);

            var store = new EncryptedVaultStore(Path.Combine(serviceDirectory, "vault"), ownerSid.Value);
            var lifecycle = new VaultLifecycle(store);
            if (!lifecycle.Provision(SyntheticPassphrase, out var recoveryCode) || recoveryCode is null)
            {
                throw new InvalidOperationException("The synthetic harness vault was not created.");
            }

            RequireHarness(lifecycle.Unlock(SyntheticPassphrase, useRecoveryCode: false),
                "The synthetic harness vault did not unlock.");
            var index = new SqlitePrivateIndexStore();
            return new S3T2CodexCoordinatorHarness(
                root, store, index, lifecycle, BuildHandler(lifecycle, index));
        }
        catch
        {
            DeleteS3T2Root(root);
            throw;
        }
    }

    public void Restart()
    {
        Lifecycle.Dispose();
        var lifecycle = new VaultLifecycle(_store);
        RequireHarness(lifecycle.Unlock(SyntheticPassphrase, useRecoveryCode: false),
            "The restarted harness vault did not unlock.");
        Lifecycle = lifecycle;
        Handler = BuildHandler(lifecycle, _index);
    }

    public async Task<bool> UnlockAsync() => await Task.FromResult(Lifecycle.Unlock(SyntheticPassphrase, useRecoveryCode: false)).ConfigureAwait(false);

    public SourceIngestionJournal? DecodeJournal(string sourceId, long generation)
    {
        var journalId = SourceIngestionIds.JournalIdFor(sourceId, generation);
        using var operation = Lifecycle.TryBeginOperation()
            ?? throw new InvalidOperationException("The harness vault locked before the journal decode.");
        var revision = operation.ListResources().FirstOrDefault(resource =>
            string.Equals(resource.ResourceId, journalId, StringComparison.Ordinal)) is { } live ? live.Revision : 0;
        if (revision <= 0)
        {
            return null;
        }

        var raw = operation.ReadContent(journalId, revision);
        try
        {
            return SourceIngestionJournalCodec.Decode(raw);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    public async Task<ServiceReply> OwnerSendAsync(ServiceRequest request, CancellationToken cancellationToken = default)
    {
        var needsOperation =
            request.Operation is ServiceRequestHandler.SourceEnrollOperation or
                ServiceRequestHandler.SourceRevokeOperation or
                ServiceRequestHandler.SourceStatusOperation or
                ServiceRequestHandler.SourceIngestOperation or
                ServiceRequestHandler.SourceRelocateOperation or
                ServiceRequestHandler.SourcePauseOperation or
                ServiceRequestHandler.SourceResumeOperation or
                ServiceRequestHandler.SourceRefreshOptInOperation or
                ServiceRequestHandler.SourceRefreshOptOutOperation or
                ServiceRequestHandler.SourceListOperation or
                ServiceRequestHandler.SourceRefreshOperation or
                ServiceRequestHandler.OwnerIndexRebuildOperation;
        var operation = needsOperation ? Lifecycle.TryBeginOperation() : null;
        try
        {
            return await Handler.HandleAsync(
                AuthenticatedContext.ForOwner(), request, operation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operation?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Lifecycle.Dispose();
        _index.Dispose();
        DeleteS3T2Root(_root);
    }

    private static ServiceRequestHandler BuildHandler(VaultLifecycle lifecycle, IPrivateIndexStore privateIndex)
    {
        // The real shipped registry content: the Codex parser, and nothing
        // else, attached through the same registry the service uses.
        var normalizers = new SourceNormalizerRegistry();
        normalizers.Register(new CodexSessionNormalizer());
        return new ServiceRequestHandler(
            new GrantAuthority(new InMemoryGrantStore()),
            lifecycle,
            new OwnerScopeGrantAuthority(),
            projections: null,
            privateIndex: privateIndex,
            sourceNormalizers: normalizers,
            sourceReader: new EnrolledSourceFileReader());
    }

    private static void RequireHarness(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void DeleteS3T2Root(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var temporaryDirectory = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var expectedPrefix = temporaryDirectory + Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullRoot).StartsWith("MetaBrain-S3-T2-", StringComparison.Ordinal) ||
            !Directory.Exists(fullRoot))
        {
            return;
        }

        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The synthetic harness root became a reparse point; refusing recursive cleanup.");
        }

        Directory.Delete(fullRoot, recursive: true);
    }
}

internal static partial class OwnerServiceSmoke
{
    /// <summary>
    /// Phase B: the real stack in-process with the real Codex normalizer, and
    /// the encrypted journal decoded to prove attribution, session identity,
    /// timestamps, exact spans and digests, plus the full boundary matrix.
    /// </summary>
    private static async Task RunS3T2CodexCoordinatorPhaseAsync()
    {
        using var harness = S3T2CodexCoordinatorHarness.Create();
        const string zoneId = "s3t2-codex-coordinator-zone";
        const string sourceId = "s3t2-codex-coordinator-source";
        var locator = Path.Combine(harness.Root, "codex-session.jsonl");
        var marker = "S3T2-CODEX-COORDINATOR-MARKER-" + Guid.NewGuid().ToString("N")[..12];
        const string sessionId = "s3t2-coordinator-session";

        ServiceRequest EnrollRequest() => new(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.SourceEnrollOperation,
            sourceId,
            zoneId,
            SourceLocator: locator,
            SourceFormat: CodexSessionNormalizer.FormatName,
            SourceFormatVersion: CodexSessionNormalizer.FormatVersionValue);

        ServiceRequest Simple(string operation) => new(
            ServiceRequestHandler.CurrentProtocolVersion, operation, sourceId);

        await File.WriteAllTextAsync(locator, S3T2CodexSession(sessionId, marker)).ConfigureAwait(false);
        var enrolled = await harness.OwnerSendAsync(EnrollRequest()).ConfigureAwait(false);
        Require(enrolled.Status == "source_enrolled" && enrolled.SourceEnrollment is { Generation: 1 },
            "In-process enrollment of the Codex fixture did not start generation 1 (status=" + enrolled.Status + "; error=" + enrolled.Error + ").");

        var first = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(first.Status == "ingest_advanced" && first.SourceIngestion is { EventCount: S3T2CodexRecordCount },
            "The first in-process Codex ingest did not accept every record (status=" + first.Status + "; events=" + first.SourceIngestion?.EventCount + "; error=" + first.Error + ").");

        var journal = harness.DecodeJournal(sourceId, 1)
            ?? throw new InvalidOperationException("The Codex ingest did not persist an encrypted journal.");
        Require(journal.Events.Length == S3T2CodexRecordCount,
            "The persisted Codex journal does not carry one event per record (" + journal.Events.Length + ").");
        Require(journal.ConsumedOffset == new FileInfo(locator).Length,
            "The Codex checkpoint did not advance to the file end.");

        // Attribution: the distribution the declared roles and record shapes
        // imply, and nothing else.
        var authors = journal.Events.GroupBy(candidate => candidate.Author)
            .ToDictionary(group => group.Key, group => group.Count());
        Require(authors.GetValueOrDefault(SourceEventAuthor.User) == 1,
            "The Codex journal does not attribute exactly one record to the user.");
        Require(authors.GetValueOrDefault(SourceEventAuthor.Assistant) == 3,
            "The Codex journal does not attribute the assistant message, reasoning and tool call to the assistant.");
        Require(authors.GetValueOrDefault(SourceEventAuthor.Tool) == 1,
            "The Codex journal does not attribute the tool output to the tool.");
        Require(authors.GetValueOrDefault(SourceEventAuthor.System) == 9,
            "The Codex journal does not attribute the runtime/control records to the system.");
        Require(authors.GetValueOrDefault(SourceEventAuthor.Unknown) == 0,
            "The Codex journal left a record with unknown attribution.");

        // Session identity is a first-class event carrying the declared UUID.
        var sessionEvent = journal.Events.FirstOrDefault(candidate =>
            string.Equals(candidate.EventId, "codex-session-" + sessionId, StringComparison.Ordinal));
        Require(sessionEvent is not null,
            "The Codex journal does not carry the session identity event.");

        // Attribution, timestamps, spans and digests all come from the exact
        // source bytes at the stored span: the journal is replayed against the
        // original file, never against a stored copy.
        var sourceBytes = await File.ReadAllBytesAsync(locator).ConfigureAwait(false);
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var normalized in journal.Events)
        {
            Require(normalized.Span.Start >= 0 && normalized.Span.End > normalized.Span.Start &&
                    normalized.Span.End <= sourceBytes.Length,
                "A Codex event span does not address bytes inside the source.");
            var slice = sourceBytes.AsSpan((int)normalized.Span.Start, (int)(normalized.Span.End - normalized.Span.Start));
            Require(SourceFingerprint.Compute(slice) == normalized.PayloadDigest,
                "A Codex event digest does not match the source bytes at its span.");
            Require(normalized.OccurredAtUtc is { } occurredAtUtc && occurredAtUtc.Offset == TimeSpan.Zero,
                "A Codex event lost its UTC timestamp.");
            var root = JsonSerializer.Deserialize<JsonElement>(slice);
            {
                var recordTimestamp = root.GetProperty("timestamp").GetString();
                Require(recordTimestamp is not null && DateTimeOffset.TryParse(recordTimestamp, out var parsedTimestamp) &&
                        parsedTimestamp.ToUniversalTime() == normalized.OccurredAtUtc,
                    "A Codex event timestamp does not match its source record.");
                Require(ExpectedAuthorForRecord(root) == normalized.Author,
                    "A Codex event attribution does not match the record declared at its span.");
                Require(ExpectedKindForRecord(root) == normalized.PayloadKind,
                    "A Codex event payload kind does not match the record at its span.");
                kinds.Add(normalized.PayloadKind);
            }
        }

        Require(kinds.Contains("codex.session_meta") &&
                kinds.Contains("codex.response_item.message.user") &&
                kinds.Contains("codex.response_item.message.assistant") &&
                kinds.Contains("codex.response_item.message.developer") &&
                kinds.Contains("codex.response_item.reasoning") &&
                kinds.Contains("codex.response_item.function_call") &&
                kinds.Contains("codex.response_item.function_call_output") &&
                kinds.Contains("codex.event_msg.item_completed") &&
                kinds.Contains("codex.turn_context") &&
                kinds.Contains("codex.world_state") &&
                kinds.Contains("codex.token_usage_record"),
            "The Codex journal does not carry the expected payload kinds.");
        Console.WriteLine("PASS the encrypted journal replays attribution, timestamps, spans and digests from the exact source bytes");

        // The tool result is attributed to the tool, and the only user-attributed
        // events are records that declare role user.
        var toolOutputs = journal.Events.Where(candidate =>
            string.Equals(candidate.PayloadKind, "codex.response_item.function_call_output", StringComparison.Ordinal)).ToArray();
        Require(toolOutputs.Length == 1 && toolOutputs[0].Author == SourceEventAuthor.Tool,
            "The tool output was not kept as a tool-authored event.");
        foreach (var normalized in journal.Events.Where(candidate => candidate.Author == SourceEventAuthor.User))
        {
            var slice = sourceBytes.AsSpan((int)normalized.Span.Start, (int)(normalized.Span.End - normalized.Span.Start));
            var root = JsonSerializer.Deserialize<JsonElement>(slice);
            {
                var payload = root.GetProperty("payload");
                Require(payload.GetProperty("role").GetString() == "user",
                    "A user-attributed event does not come from a record declaring role user.");
            }
        }
        Console.WriteLine("PASS tool results stay tool-attributed and never become user statements");

        // Idempotent re-import, then append.
        var revisionBefore = first.SourceIngestion!.JournalRevision;
        var reimport = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(reimport.Status == "ingest_idle" &&
                reimport.SourceIngestion!.JournalRevision == revisionBefore &&
                reimport.SourceIngestion!.EventCount == S3T2CodexRecordCount,
            "Re-import of the Codex session was not idempotent (status=" + reimport.Status + ").");
        await File.AppendAllTextAsync(locator, S3T2CodexAppendRecords(sessionId, marker)).ConfigureAwait(false);
        var appended = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(appended.Status == "ingest_advanced" &&
                appended.SourceIngestion!.EventCount == S3T2CodexRecordCount + S3T2CodexAppendRecordCount,
            "The appended Codex records were not consumed exactly once (status=" + appended.Status + "; events=" + appended.SourceIngestion?.EventCount + ").");
        var afterAppend = harness.DecodeJournal(sourceId, 1)!;
        var authorsAfterAppend = afterAppend.Events.GroupBy(candidate => candidate.Author)
            .ToDictionary(group => group.Key, group => group.Count());
        Require(authorsAfterAppend.GetValueOrDefault(SourceEventAuthor.User) == 2 &&
                authorsAfterAppend.GetValueOrDefault(SourceEventAuthor.Assistant) == 4 &&
                authorsAfterAppend.GetValueOrDefault(SourceEventAuthor.Tool) == 1,
            "The appended records did not extend the attribution distribution as declared.");
        Console.WriteLine("PASS re-import is idempotent and append extends the attribution exactly as declared");

        // Partial tail: never committed as complete, consumed exactly once.
        var partialRecord = S3T2CodexLine("2026-10-12T00:00:17.000Z", 17, "response_item",
            "{\"type\":\"message\",\"id\":\"msg-partial\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + marker + " partial\"}]}");
        await File.AppendAllTextAsync(locator, partialRecord[..^1]).ConfigureAwait(false);
        var partial = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(partial.Status == "ingest_partial_pending" &&
                partial.SourceIngestion!.ConsumedOffset < new FileInfo(locator).Length &&
                partial.SourceIngestion!.EventCount == S3T2CodexRecordCount + S3T2CodexAppendRecordCount,
            "The partial Codex tail was committed or lost (status=" + partial.Status + ").");
        await File.AppendAllTextAsync(locator, "\n").ConfigureAwait(false);
        var completed = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(completed.Status == "ingest_advanced" &&
                completed.SourceIngestion!.EventCount == S3T2CodexRecordCount + S3T2CodexAppendRecordCount + 1,
            "The completed Codex record after the partial tail was not consumed exactly once (status=" + completed.Status + ").");
        Console.WriteLine("PASS a partial Codex tail stays unconsumed and is consumed exactly once");

        // Boundary matrix: every uninterpretable shape is an explicit,
        // named, persistent refusal at its exact offset.
        await RunS3T2CodexRefusalAsync(harness, "s3t2-codex-b-unknown-type", zoneId,
            S3T2CodexSession("s3t2-b-unknown", marker) + S3T2CodexLine("2026-10-12T00:00:14.000Z", 1, "brand_new_record_type", "{\"anything\":true}"),
            "codex_type_unsupported.brand_new_record_type", S3T2CodexRecordCount);
        await RunS3T2CodexRefusalAsync(harness, "s3t2-codex-b-blank", zoneId,
            S3T2CodexSession("s3t2-b-blank", marker) + "\n", "codex_record_blank", S3T2CodexRecordCount);
        await RunS3T2CodexRefusalAsync(harness, "s3t2-codex-b-json", zoneId,
            S3T2CodexSession("s3t2-b-json", marker) + "{not json}\n", "codex_record_json_invalid", S3T2CodexRecordCount);
        await RunS3T2CodexRefusalAsync(harness, "s3t2-codex-b-not-object", zoneId,
            S3T2CodexSession("s3t2-b-notobject", marker) + "[1,2,3]\n", "codex_record_not_object", S3T2CodexRecordCount);
        await RunS3T2CodexRefusalAsync(harness, "s3t2-codex-b-no-type", zoneId,
            S3T2CodexSession("s3t2-b-notype", marker) + "{\"timestamp\":\"2026-10-12T00:00:14.000Z\",\"payload\":{}}\n",
            "codex_envelope_type_missing", S3T2CodexRecordCount);
        await RunS3T2CodexRefusalAsync(harness, "s3t2-codex-b-time", zoneId,
            S3T2CodexSession("s3t2-b-time", marker) + "{\"timestamp\":\"not-a-time\",\"ordinal\":1,\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\"}}\n",
            "codex_record_time_invalid", S3T2CodexRecordCount);
        await RunS3T2CodexRefusalAsync(harness, "s3t2-codex-b-ordinal", zoneId,
            S3T2CodexSession("s3t2-b-ordinal", marker) + "{\"timestamp\":\"2026-10-12T00:00:14.000Z\",\"ordinal\":-1,\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\"}}\n",
            "codex_record_ordinal_invalid", S3T2CodexRecordCount);
        await RunS3T2CodexRefusalAsync(harness, "s3t2-codex-b-payload", zoneId,
            S3T2CodexSession("s3t2-b-payload", marker) + "{\"timestamp\":\"2026-10-12T00:00:14.000Z\",\"ordinal\":1,\"type\":\"event_msg\",\"payload\":[]}\n",
            "codex_payload_not_object", S3T2CodexRecordCount);
        await RunS3T2CodexRefusalAsync(harness, "s3t2-codex-b-identity", zoneId,
            "{\"timestamp\":\"2026-10-12T00:00:00.000Z\",\"type\":\"session_meta\",\"payload\":{\"cwd\":\"C:\\\\synthetic\"}}\n",
            "codex_session_identity_invalid", 0);
        await RunS3T2CodexRefusalAsync(harness, "s3t2-codex-b-item-type", zoneId,
            S3T2CodexSession("s3t2-b-itemtype", marker) + S3T2CodexLine("2026-10-12T00:00:14.000Z", 1, "response_item", "{\"type\":\"unobserved_mystery_tool\",\"call_id\":\"call-x\"}"),
            "codex_item_type_unsupported.unobserved_mystery_tool", S3T2CodexRecordCount);
        await RunS3T2CodexRefusalAsync(harness, "s3t2-codex-b-role", zoneId,
            S3T2CodexSession("s3t2-b-role", marker) + S3T2CodexLine("2026-10-12T00:00:14.000Z", 1, "response_item", "{\"type\":\"message\",\"role\":\"robot\",\"content\":[]}"),
            "codex_message_role_unsupported.robot", S3T2CodexRecordCount);
        await RunS3T2CodexRefusalAsync(harness, "s3t2-codex-b-no-role", zoneId,
            S3T2CodexSession("s3t2-b-norole", marker) + S3T2CodexLine("2026-10-12T00:00:14.000Z", 1, "response_item", "{\"type\":\"message\",\"content\":[]}"),
            "codex_message_role_missing", S3T2CodexRecordCount);
        await RunS3T2CodexRefusalAsync(harness, "s3t2-codex-b-content", zoneId,
            S3T2CodexSession("s3t2-b-content", marker) + S3T2CodexLine("2026-10-12T00:00:14.000Z", 1, "response_item", "{\"type\":\"message\",\"role\":\"user\",\"content\":\"plain string\"}"),
            "codex_message_content_invalid", S3T2CodexRecordCount);
        await RunS3T2CodexRefusalAsync(harness, "s3t2-codex-b-call", zoneId,
            S3T2CodexSession("s3t2-b-call", marker) + S3T2CodexLine("2026-10-12T00:00:14.000Z", 1, "response_item", "{\"type\":\"function_call\",\"name\":\"shell\",\"arguments\":\"{}\"}"),
            "codex_call_identity_invalid", S3T2CodexRecordCount);
        await RunS3T2CodexRefusalAsync(harness, "s3t2-codex-b-output", zoneId,
            S3T2CodexSession("s3t2-b-output", marker) + S3T2CodexLine("2026-10-12T00:00:14.000Z", 1, "response_item", "{\"type\":\"function_call_output\",\"call_id\":\"call-1\"}"),
            "codex_call_output_invalid", S3T2CodexRecordCount);
        await RunS3T2CodexRefusalAsync(harness, "s3t2-codex-b-array-output", zoneId,
            S3T2CodexSession("s3t2-b-arrayoutput", marker) + S3T2CodexLine("2026-10-12T00:00:14.000Z", 14, "response_item", "{\"type\":\"function_call_output\",\"call_id\":\"call-1\",\"output\":[{\"type\":\"input_text\",\"text\":\"structured\"}]}"),
            null, S3T2CodexRecordCount + 1);
        Console.WriteLine("PASS every uninterpretable Codex record shape is refused explicitly with a named diagnostic at its exact offset");

        // Restart resumes from the persisted checkpoint.
        var beforeRestart = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceStatusOperation)).ConfigureAwait(false);
        harness.Restart();
        var afterRestart = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(afterRestart.Status == "ingest_idle" &&
                afterRestart.SourceIngestion!.ConsumedOffset == beforeRestart.SourceIngestion!.ConsumedOffset &&
                afterRestart.SourceIngestion!.EventCount == beforeRestart.SourceIngestion!.EventCount,
            "The restart did not resume from the persisted Codex checkpoint (status=" + afterRestart.Status + ").");
        await File.AppendAllTextAsync(locator, S3T2CodexLine("2026-10-12T00:00:16.000Z", 16, "response_item",
            "{\"type\":\"message\",\"id\":\"msg-assistant-3\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"" + marker + " restarted answer\"}],\"phase\":\"final_answer\"}")).ConfigureAwait(false);
        var afterRestartAppend = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(afterRestartAppend.Status == "ingest_advanced" &&
                afterRestartAppend.SourceIngestion!.EventCount == afterRestart.SourceIngestion!.EventCount + 1,
            "Post-restart ingestion did not continue from the correct offset (status=" + afterRestartAppend.Status + ").");
        Console.WriteLine("PASS restart resumes the Codex checkpoint without duplicates");

        // Original bytes and metadata unchanged; encrypted persistence.
        var expectedBytes = await File.ReadAllBytesAsync(locator).ConfigureAwait(false);
        var lastWrite = File.GetLastWriteTimeUtc(locator);
        _ = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(File.GetLastWriteTimeUtc(locator) == lastWrite,
            "A read-only Codex ingest modified the source metadata.");
        var journalAfter = harness.DecodeJournal(sourceId, 1)!;
        Require(journalAfter.Events.Length == afterRestartAppend.SourceIngestion!.EventCount,
            "The Codex journal lost or duplicated events on re-read.");
        var vaultFiles = Directory.GetFiles(harness.VaultDirectoryPath, "*", SearchOption.AllDirectories);
        Require(vaultFiles.Length >= 3, "The in-process vault did not persist its private files.");
        foreach (var path in vaultFiles)
        {
            var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            Require(!text.Contains(marker, StringComparison.Ordinal) && !text.Contains(locator, StringComparison.Ordinal),
                "An encrypted vault file persisted plaintext payload or locator: " + Path.GetFileName(path) + ".");
        }

        Require((await File.ReadAllBytesAsync(locator).ConfigureAwait(false)).AsSpan().SequenceEqual(expectedBytes),
            "The read-only Codex adapter modified the original source bytes.");
        Console.WriteLine("PASS encrypted journals, original bytes and metadata unchanged across read-only ingestion");

        var expectedPrefixBytes = Encoding.UTF8.GetBytes(S3T2CodexSession("s3t2-coordinator-session", marker));
        Require(expectedPrefixBytes.AsSpan().SequenceEqual(expectedBytes.AsSpan(0, expectedPrefixBytes.Length)),
            "The ingested Codex prefix no longer matches the fixture bytes the journal was derived from.");
    }

    /// <summary>
    /// One boundary fixture on its own enrollment: ingestion must stop with the
    /// named diagnostic at the exact offset, persist exactly one diagnostic,
    /// accept the prefix records before it, and stay blocked on re-import.
    /// </summary>
    private static async Task RunS3T2CodexRefusalAsync(
        S3T2CodexCoordinatorHarness harness,
        string sourceId,
        string zoneId,
        string content,
        string? expectedCode,
        int expectedEvents)
    {
        var locator = Path.Combine(harness.Root, sourceId + ".jsonl");
        await File.WriteAllTextAsync(locator, content).ConfigureAwait(false);
        var enrolled = await harness.OwnerSendAsync(new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.SourceEnrollOperation,
            sourceId,
            zoneId,
            SourceLocator: locator,
            SourceFormat: CodexSessionNormalizer.FormatName,
            SourceFormatVersion: CodexSessionNormalizer.FormatVersionValue)).ConfigureAwait(false);
        Require(enrolled.Status == "source_enrolled",
            "The boundary source enrollment failed (source=" + sourceId + "; error=" + enrolled.Error + ").");

        var ingest = await harness.OwnerSendAsync(new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.SourceIngestOperation,
            sourceId)).ConfigureAwait(false);
        if (expectedCode is null)
        {
            // A supported shape: the record is accepted, not refused.
            Require(ingest.Status == "ingest_advanced" && ingest.SourceIngestion!.EventCount == expectedEvents,
                "A supported tool-output shape was refused (source=" + sourceId + "; status=" + ingest.Status + "; error=" + ingest.Error + ").");
            return;
        }

        Require(ingest.Status == "ingest_unsupported_record" &&
                ingest.SourceIngestion!.EventCount == expectedEvents &&
                ingest.SourceIngestion!.DiagnosticCount == 1,
            "The boundary source did not stop with exactly one diagnostic (source=" + sourceId + "; status=" + ingest.Status + "; events=" + ingest.SourceIngestion?.EventCount + "; diagnostics=" + ingest.SourceIngestion?.DiagnosticCount + ").");

        var journal = harness.DecodeJournal(sourceId, 1)!;
        Require(journal.Diagnostics.Length == 1,
            "The boundary source did not persist exactly one diagnostic (source=" + sourceId + ").");
        Require(string.Equals(journal.Diagnostics[0].Code, expectedCode, StringComparison.Ordinal),
            "The boundary diagnostic does not name the condition (source=" + sourceId + "; code=" + journal.Diagnostics[0].Code + "; expected=" + expectedCode + ").");
        Require(journal.Diagnostics[0].Offset == ingest.SourceIngestion!.ConsumedOffset,
            "The boundary diagnostic offset does not match the stopped checkpoint (source=" + sourceId + ").");

        var again = await harness.OwnerSendAsync(new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.SourceIngestOperation,
            sourceId)).ConfigureAwait(false);
        Require(again.Status == "ingest_unsupported_record" &&
                again.SourceIngestion!.ConsumedOffset == ingest.SourceIngestion!.ConsumedOffset &&
                again.SourceIngestion!.DiagnosticCount == 1,
            "The boundary source was not left blocked at the same offset (source=" + sourceId + ").");
    }

    /// <summary>
    /// The attribution a record's own declarations imply, used to prove the
    /// stored attribution was never rewritten by the adapter.
    /// </summary>
    private static SourceEventAuthor ExpectedAuthorForRecord(JsonElement record)
    {
        var type = record.GetProperty("type").GetString();
        var payload = record.GetProperty("payload");
        switch (type)
        {
            case "session_meta":
            case "turn_context":
            case "world_state":
            case "token_usage_record":
            case "compacted":
            case "inter_agent_communication":
            case "inter_agent_communication_metadata":
            case "retained_context":
            case "security_risk_score":
            case "realtime_item":
                return SourceEventAuthor.System;
            case "event_msg":
                return SourceEventAuthor.System;
            case "response_item":
                var payloadType = payload.GetProperty("type").GetString();
                if (string.Equals(payloadType, "message", StringComparison.Ordinal))
                {
                    return payload.GetProperty("role").GetString() switch
                    {
                        "user" => SourceEventAuthor.User,
                        "assistant" => SourceEventAuthor.Assistant,
                        _ => SourceEventAuthor.System
                    };
                }

                if (string.Equals(payloadType, "reasoning", StringComparison.Ordinal) ||
                    string.Equals(payloadType, "function_call", StringComparison.Ordinal) ||
                    string.Equals(payloadType, "custom_tool_call", StringComparison.Ordinal) ||
                    string.Equals(payloadType, "local_shell_call", StringComparison.Ordinal) ||
                    string.Equals(payloadType, "tool_search_call", StringComparison.Ordinal) ||
                    string.Equals(payloadType, "web_search_call", StringComparison.Ordinal) ||
                    string.Equals(payloadType, "image_generation_call", StringComparison.Ordinal) ||
                    string.Equals(payloadType, "agent_message", StringComparison.Ordinal))
                {
                    return SourceEventAuthor.Assistant;
                }

                return SourceEventAuthor.Tool;
            default:
                throw new InvalidOperationException("Unexpected record type in the boundary expectation.");
        }
    }

    private static string ExpectedKindForRecord(JsonElement record)
    {
        var type = record.GetProperty("type").GetString()!;
        var payload = record.GetProperty("payload");
        if (string.Equals(type, "response_item", StringComparison.Ordinal))
        {
            var payloadType = payload.GetProperty("type").GetString()!;
            return string.Equals(payloadType, "message", StringComparison.Ordinal)
                ? "codex.response_item.message." + payload.GetProperty("role").GetString()
                : "codex.response_item." + payloadType;
        }

        if (string.Equals(type, "event_msg", StringComparison.Ordinal))
        {
            return "codex.event_msg." + payload.GetProperty("type").GetString();
        }

        return "codex." + type;
    }
}
