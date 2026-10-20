using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Index;
using MetaBrain.Core.Security;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// In-process harness for the S3-T3 OMP phase: the same production object
/// graph the named-pipe service builds (encrypted store, vault lifecycle,
/// handler, authorities, enrolled-source reader) with the shipped normalizer
/// registry content attached — the real Codex and OMP parsers through the
/// same factory the service binary uses. "Restart" rebuilds the lifecycle and
/// handler over the same vault directory, which is exactly what a service
/// process restart does.
/// </summary>
internal sealed class S3T3OmpCoordinatorHarness : IDisposable
{
    private const string SyntheticPassphrase = "s3t3-synthetic-vault-passphrase";
    private readonly string _root;
    private readonly EncryptedVaultStore _store;
    private readonly SqlitePrivateIndexStore _index;
    private bool _disposed;

    private S3T3OmpCoordinatorHarness(
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

    public static S3T3OmpCoordinatorHarness Create()
    {
        var ownerSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows token has no user SID.");
        var root = Path.Combine(Path.GetTempPath(), "MetaBrain-S3-T3-" + Guid.NewGuid().ToString("N"));
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

            var store = new EncryptedVaultStore(Path.Combine(serviceDirectory, "vault"));
            var lifecycle = new VaultLifecycle(store);
            if (!lifecycle.Provision(SyntheticPassphrase, out var recoveryCode) || recoveryCode is null)
            {
                throw new InvalidOperationException("The synthetic harness vault was not created.");
            }

            RequireHarness(lifecycle.Unlock(SyntheticPassphrase, useRecoveryCode: false),
                "The synthetic harness vault did not unlock.");
            var index = new SqlitePrivateIndexStore();
            return new S3T3OmpCoordinatorHarness(
                root, store, index, lifecycle, BuildHandler(lifecycle, index));
        }
        catch
        {
            DeleteS3T3Root(root);
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
        DeleteS3T3Root(_root);
    }

    private static ServiceRequestHandler BuildHandler(VaultLifecycle lifecycle, IPrivateIndexStore privateIndex)
    {
        // The real shipped registry content: the Codex parser and the OMP
        // parser through the same factory the service binary uses.
        return new ServiceRequestHandler(
            new GrantAuthority(new InMemoryGrantStore()),
            lifecycle,
            new OwnerScopeGrantAuthority(),
            projections: null,
            privateIndex: privateIndex,
            sourceNormalizers: SourceNormalizerFactory.CreateShipped(),
            sourceReader: new EnrolledSourceFileReader());
    }

    private static void RequireHarness(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void DeleteS3T3Root(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var temporaryDirectory = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var expectedPrefix = temporaryDirectory + Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullRoot).StartsWith("MetaBrain-S3-T3-", StringComparison.Ordinal) ||
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
    /// Phase B: the real stack in-process with the shipped registry, and the
    /// encrypted journal decoded to prove attribution, session identity, the
    /// declared id/parentId chain and cwd context, timestamps, exact spans and
    /// digests, plus the full boundary matrix.
    /// </summary>
    private static async Task RunS3T3OmpCoordinatorPhaseAsync()
    {
        using var harness = S3T3OmpCoordinatorHarness.Create();
        const string zoneId = "s3t3-omp-coordinator-zone";
        const string sourceId = "s3t3-omp-coordinator-source";
        var locator = Path.Combine(harness.Root, "omp-session.jsonl");
        var marker = "S3T3-OMP-COORDINATOR-MARKER-" + Guid.NewGuid().ToString("N")[..12];
        const string sessionId = "s3t3-coordinator-session";

        ServiceRequest EnrollRequest() => new(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.SourceEnrollOperation,
            sourceId,
            zoneId,
            SourceLocator: locator,
            SourceFormat: OmpSessionNormalizer.FormatName,
            SourceFormatVersion: OmpSessionNormalizer.FormatVersionValue);

        ServiceRequest Simple(string operation) => new(
            ServiceRequestHandler.CurrentProtocolVersion, operation, sourceId);

        await File.WriteAllTextAsync(locator, S3T3OmpBase(sessionId, marker)).ConfigureAwait(false);
        var enrolled = await harness.OwnerSendAsync(EnrollRequest()).ConfigureAwait(false);
        Require(enrolled.Status == "source_enrolled" && enrolled.SourceEnrollment is { Generation: 1 },
            "In-process enrollment of the OMP fixture did not start generation 1 (status=" + enrolled.Status + "; error=" + enrolled.Error + ").");

        var first = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(first.Status == "ingest_advanced" && first.SourceIngestion is { EventCount: S3T3OmpRecordCount },
            "The first in-process OMP ingest did not accept every record (status=" + first.Status + "; events=" + first.SourceIngestion?.EventCount + "; error=" + first.Error + ").");

        var journal = harness.DecodeJournal(sourceId, 1)
            ?? throw new InvalidOperationException("The OMP ingest did not persist an encrypted journal.");
        Require(journal.Events.Length == S3T3OmpRecordCount,
            "The persisted OMP journal does not carry one event per record (" + journal.Events.Length + ").");
        Require(journal.ConsumedOffset == new FileInfo(locator).Length,
            "The OMP checkpoint did not advance to the file end.");

        // Attribution: the distribution the declared roles, attributions and
        // record shapes imply, and nothing else.
        var authors = journal.Events.GroupBy(candidate => candidate.Author)
            .ToDictionary(group => group.Key, group => group.Count());
        Require(authors.GetValueOrDefault(SourceEventAuthor.User) == 2,
            "The OMP journal does not attribute exactly the declared user message and the user-attributed custom_message to the user.");
        Require(authors.GetValueOrDefault(SourceEventAuthor.Assistant) == 2,
            "The OMP journal does not attribute the assistant message and the agent-attributed custom_message to the assistant.");
        Require(authors.GetValueOrDefault(SourceEventAuthor.Tool) == 1,
            "The OMP journal does not attribute the tool result to the tool.");
        Require(authors.GetValueOrDefault(SourceEventAuthor.System) == 9,
            "The OMP journal does not attribute the runtime/control records to the system.");
        Require(authors.GetValueOrDefault(SourceEventAuthor.Unknown) == 0,
            "The OMP journal left a record with unknown attribution.");

        // The session identity is a first-class event carrying the declared
        // session UUID; its span covers the record that declares the context.
        var sessionEvent = journal.Events.FirstOrDefault(candidate =>
            string.Equals(candidate.EventId, "omp-session-" + sessionId, StringComparison.Ordinal));
        Require(sessionEvent is { PayloadKind: "omp.session" },
            "The OMP journal does not carry the session identity event with the session payload kind.");

        // Every event replays against the exact source bytes at its span:
        // identity, attribution, timestamp, kind and digest all come from the
        // record the span addresses, and the id/parentId chain is proven
        // node-by-node over the original file.
        var sourceBytes = await File.ReadAllBytesAsync(locator).ConfigureAwait(false);
        var ordered = journal.Events.OrderBy(candidate => candidate.Span.Start).ToArray();
        Require(ordered.Length > 0 && ordered[0].Span.Start == 0 && ordered[^1].Span.End == sourceBytes.Length,
            "The OMP event spans do not cover the source from the first byte to the last.");
        for (var index = 0; index < ordered.Length; index++)
        {
            if (index > 0)
            {
                Require(ordered[index].Span.Start == ordered[index - 1].Span.End,
                    "The OMP event spans overlap or leave an unconsumed gap between accepted records.");
            }
        }

        var declaredNodes = new HashSet<string>(StringComparer.Ordinal) { sessionId };
        var chainRoots = 0;
        foreach (var normalized in ordered)
        {
            var slice = sourceBytes.AsSpan((int)normalized.Span.Start, (int)(normalized.Span.End - normalized.Span.Start));
            Require(SourceFingerprint.Compute(slice) == normalized.PayloadDigest,
                "An OMP event digest does not match the source bytes at its span.");
            var record = JsonSerializer.Deserialize<JsonElement>(slice);
            var recordType = record.GetProperty("type").GetString();
            Require(ExpectedOmpAuthorForRecord(record) == normalized.Author,
                "An OMP event attribution does not match the record declared at its span.");
            Require(ExpectedOmpKindForRecord(record) == normalized.PayloadKind,
                "An OMP event payload kind does not match the record at its span.");
            Require(normalized.OccurredAtUtc is { } occurredAtUtc && occurredAtUtc.Offset == TimeSpan.Zero,
                "An OMP event lost its UTC timestamp.");
            var timeProperty = string.Equals(recordType, "title", StringComparison.Ordinal) ? "updatedAt" : "timestamp";
            var declaredTime = record.GetProperty(timeProperty).GetString();
            Require(declaredTime is not null && DateTimeOffset.TryParse(declaredTime, out var parsedTime) &&
                    parsedTime.ToUniversalTime() == normalized.OccurredAtUtc,
                "An OMP event timestamp does not match its source record.");

            if (string.Equals(recordType, "session", StringComparison.Ordinal))
            {
                var declaredCwd = record.GetProperty("cwd").GetString();
                Require(string.Equals(declaredCwd, "C:\\synthetic\\omp-fixture", StringComparison.Ordinal),
                    "The OMP session event span does not address the record declaring the cwd context.");
                Require(string.Equals(normalized.EventId, "omp-session-" + sessionId, StringComparison.Ordinal),
                    "The OMP session event identity does not carry the declared session id.");
                continue;
            }

            if (string.Equals(recordType, "title", StringComparison.Ordinal))
            {
                // The title record declares no event id; its identity is
                // anchored to its byte offset inside the generation.
                Require(string.Equals(normalized.EventId,
                        "omp-" + sourceId + "-b" + normalized.Span.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        StringComparison.Ordinal),
                    "The OMP title event identity is not anchored to its declared offset.");
                continue;
            }

            var declaredId = record.GetProperty("id").GetString();

            Require(string.Equals(normalized.EventId, "omp-" + sourceId + "-" + declaredId, StringComparison.Ordinal),
                "An OMP event identity does not carry the declared id/parentId chain node.");

            var parent = record.GetProperty("parentId");
            if (parent.ValueKind == JsonValueKind.Null)
            {
                chainRoots++;
            }
            else
            {
                var parentId = parent.GetString();
                Require(parentId is not null && declaredNodes.Contains(parentId),
                    "An OMP event declares a parent that is not an earlier declared chain node.");
            }

            Require(declaredId is not null && declaredNodes.Add(declaredId),
                "The OMP session re-declared an event id inside one generation.");
        }

        Require(chainRoots >= 1,
            "The OMP fixture did not declare a chain root (a null parentId).");
        Console.WriteLine("PASS the encrypted journal replays attribution, timestamps, spans, digests and the id/parentId chain from the exact source bytes");

        // A true branch: the consumer round-trip must resolve the exact
        // parent edges, the forked children and the compaction boundary from
        // the original spans, not from a flattened role list.
        await RunS3T3OmpBranchPhaseAsync(harness, zoneId, marker).ConfigureAwait(false);

        // The declared cwd context survives on the session event's span, and
        // the session event is the only record without a chain parent edge.
        Require(journal.Events.Count(candidate =>
                string.Equals(candidate.PayloadKind, "omp.session", StringComparison.Ordinal)) == 1,
            "The OMP journal does not carry exactly one session identity event.");

        // Tool output stays tool-attributed; user-attributed events come only
        // from records the source declares as user-attributed.
        var toolOutputs = journal.Events.Where(candidate =>
            string.Equals(candidate.PayloadKind, "omp.message.toolResult", StringComparison.Ordinal)).ToArray();
        Require(toolOutputs.Length == 1 && toolOutputs[0].Author == SourceEventAuthor.Tool,
            "The tool result was not kept as a tool-authored event.");
        foreach (var normalized in journal.Events.Where(candidate => candidate.Author == SourceEventAuthor.User))
        {
            var slice = sourceBytes.AsSpan((int)normalized.Span.Start, (int)(normalized.Span.End - normalized.Span.Start));
            var record = JsonSerializer.Deserialize<JsonElement>(slice);
            var recordType = record.GetProperty("type").GetString();
            var declaresUser = string.Equals(recordType, "message", StringComparison.Ordinal)
                ? string.Equals(record.GetProperty("message").GetProperty("role").GetString(), "user", StringComparison.Ordinal) &&
                  string.Equals(record.GetProperty("message").GetProperty("attribution").GetString(), "user", StringComparison.Ordinal)
                : string.Equals(recordType, "custom_message", StringComparison.Ordinal) &&
                  string.Equals(record.GetProperty("attribution").GetString(), "user", StringComparison.Ordinal);
            Require(declaresUser,
                "A user-attributed event does not come from a record declaring user attribution.");
        }
        Console.WriteLine("PASS tool results stay tool-attributed and only declared user records become user statements");

        // Idempotent re-import, then append.
        var revisionBefore = first.SourceIngestion!.JournalRevision;
        var reimport = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(reimport.Status == "ingest_idle" &&
                reimport.SourceIngestion!.JournalRevision == revisionBefore &&
                reimport.SourceIngestion!.EventCount == S3T3OmpRecordCount,
            "Re-import of the OMP session was not idempotent (status=" + reimport.Status + ").");
        await File.AppendAllTextAsync(locator, S3T3OmpAppendRecords(marker)).ConfigureAwait(false);
        var appended = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(appended.Status == "ingest_advanced" &&
                appended.SourceIngestion!.EventCount == S3T3OmpRecordCount + S3T3OmpAppendRecordCount,
            "The appended OMP records were not consumed exactly once (status=" + appended.Status + "; events=" + appended.SourceIngestion?.EventCount + ").");
        var afterAppend = harness.DecodeJournal(sourceId, 1)!;
        var authorsAfterAppend = afterAppend.Events.GroupBy(candidate => candidate.Author)
            .ToDictionary(group => group.Key, group => group.Count());
        Require(authorsAfterAppend.GetValueOrDefault(SourceEventAuthor.User) == 3 &&
                authorsAfterAppend.GetValueOrDefault(SourceEventAuthor.Assistant) == 3 &&
                authorsAfterAppend.GetValueOrDefault(SourceEventAuthor.Tool) == 1,
            "The appended records did not extend the attribution distribution as declared.");
        Console.WriteLine("PASS re-import is idempotent and append extends the attribution exactly as declared");

        // Partial tail: never committed as complete, consumed exactly once.
        var partialRecord = S3T3OmpEventLine("e16", "e14", "2026-10-12T00:00:16.000Z",
            "\"message\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"" + marker + " partial\"}],\"steering\":false,\"attribution\":\"user\",\"timestamp\":\"2026-10-12T00:00:16.000Z\"}");
        await File.AppendAllTextAsync(locator, partialRecord[..^1]).ConfigureAwait(false);
        var partial = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(partial.Status == "ingest_partial_pending" &&
                partial.SourceIngestion!.ConsumedOffset < new FileInfo(locator).Length &&
                partial.SourceIngestion!.EventCount == S3T3OmpRecordCount + S3T3OmpAppendRecordCount,
            "The partial OMP tail was committed or lost (status=" + partial.Status + ").");
        await File.AppendAllTextAsync(locator, "\n").ConfigureAwait(false);
        var completed = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(completed.Status == "ingest_advanced" &&
                completed.SourceIngestion!.EventCount == S3T3OmpRecordCount + S3T3OmpAppendRecordCount + 1,
            "The completed OMP record after the partial tail was not consumed exactly once (status=" + completed.Status + ").");
        Console.WriteLine("PASS a partial OMP tail stays unconsumed and is consumed exactly once");

        // Boundary matrix: every uninterpretable shape is an explicit, named,
        // persistent refusal at its exact offset.
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-unknown-type", zoneId,
            S3T3OmpBase("s3t3-b-unknown", marker) + "{\"type\":\"brand_new_omp_record\",\"id\":\"e99\",\"parentId\":\"e12\",\"timestamp\":\"2026-10-12T00:00:20.000Z\",\"anything\":true}\n",
            "omp_type_unsupported.brand_new_omp_record", S3T3OmpRecordCount);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-blank", zoneId,
            S3T3OmpBase("s3t3-b-blank", marker) + "\n", "omp_record_blank", S3T3OmpRecordCount);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-json", zoneId,
            S3T3OmpBase("s3t3-b-json", marker) + "{not json}\n", "omp_record_json_invalid", S3T3OmpRecordCount);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-not-object", zoneId,
            S3T3OmpBase("s3t3-b-notobject", marker) + "[1,2,3]\n", "omp_record_not_object", S3T3OmpRecordCount);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-no-type", zoneId,
            S3T3OmpBase("s3t3-b-notype", marker) + "{\"id\":\"e98\",\"parentId\":\"e12\",\"timestamp\":\"2026-10-12T00:00:20.000Z\"}\n",
            "omp_envelope_type_missing", S3T3OmpRecordCount);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-time", zoneId,
            S3T3OmpBase("s3t3-b-time", marker) + "{\"type\":\"title_change\",\"id\":\"e97\",\"parentId\":\"e12\",\"timestamp\":\"not-a-time\",\"title\":\"x\",\"source\":\"auto\"}\n",
            "omp_record_time_invalid", S3T3OmpRecordCount);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-no-id", zoneId,
            S3T3OmpBase("s3t3-b-noid", marker) + "{\"type\":\"title_change\",\"parentId\":\"e12\",\"timestamp\":\"2026-10-12T00:00:20.000Z\",\"title\":\"x\",\"source\":\"auto\"}\n",
            "omp_event_id_missing", S3T3OmpRecordCount);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-bad-id", zoneId,
            S3T3OmpBase("s3t3-b-badid", marker) + "{\"type\":\"title_change\",\"id\":\"bad id!\",\"parentId\":\"e12\",\"timestamp\":\"2026-10-12T00:00:20.000Z\",\"title\":\"x\",\"source\":\"auto\"}\n",
            "omp_event_id_invalid", S3T3OmpRecordCount);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-no-parent", zoneId,
            S3T3OmpBase("s3t3-b-noparent", marker) + "{\"type\":\"title_change\",\"id\":\"e96\",\"timestamp\":\"2026-10-12T00:00:20.000Z\",\"title\":\"x\",\"source\":\"auto\"}\n",
            "omp_event_parent_missing", S3T3OmpRecordCount);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-bad-parent", zoneId,
            S3T3OmpBase("s3t3-b-badparent", marker) + "{\"type\":\"title_change\",\"id\":\"e95\",\"parentId\":42,\"timestamp\":\"2026-10-12T00:00:20.000Z\",\"title\":\"x\",\"source\":\"auto\"}\n",
            "omp_event_parent_invalid", S3T3OmpRecordCount);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-role", zoneId,
            S3T3OmpBase("s3t3-b-role", marker) + S3T3OmpEventLine("e94", "e12", "2026-10-12T00:00:20.000Z",
                "\"message\",\"message\":{\"role\":\"system\",\"content\":[{\"type\":\"text\",\"text\":\"mystery\"}],\"timestamp\":\"2026-10-12T00:00:20.000Z\"}"),
            "omp_message_role_unsupported.system", S3T3OmpRecordCount);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-part", zoneId,
            S3T3OmpBase("s3t3-b-part", marker) + S3T3OmpEventLine("e93", "e12", "2026-10-12T00:00:20.000Z",
                "\"message\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"image\",\"url\":\"x\"}],\"attribution\":\"user\",\"timestamp\":\"2026-10-12T00:00:20.000Z\"}"),
            "omp_message_part_unsupported.image", S3T3OmpRecordCount);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-stop", zoneId,
            S3T3OmpBase("s3t3-b-stop", marker) + S3T3OmpEventLine("e92", "e12", "2026-10-12T00:00:20.000Z",
                "\"message\",\"message\":{\"role\":\"assistant\",\"content\":[],\"stopReason\":\"length\",\"timestamp\":\"2026-10-12T00:00:20.000Z\"}"),
            "omp_message_stop_reason_unsupported.length", S3T3OmpRecordCount);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-user-attribution", zoneId,
            S3T3OmpBase("s3t3-b-userattr", marker) + S3T3OmpEventLine("e91", "e12", "2026-10-12T00:00:20.000Z",
                "\"message\",\"message\":{\"role\":\"user\",\"content\":[],\"attribution\":\"agent\",\"timestamp\":\"2026-10-12T00:00:20.000Z\"}"),
            "omp_message_attribution_unsupported.agent", S3T3OmpRecordCount);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-custom-attribution", zoneId,
            S3T3OmpBase("s3t3-b-customattr", marker) + S3T3OmpEventLine("e90", "e12", "2026-10-12T00:00:20.000Z",
                "\"custom_message\",\"customType\":\"mystery\",\"content\":\"x\",\"display\":true,\"details\":null,\"attribution\":\"bot\""),
            "omp_custom_message_attribution_unsupported.bot", S3T3OmpRecordCount);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-session-version", zoneId,
            "{\"type\":\"session\",\"version\":4,\"id\":\"s3t3-b-v4\",\"timestamp\":\"2026-10-12T00:00:00.000Z\",\"cwd\":\"C:\\\\synthetic\\\\omp-fixture\",\"title\":\"t\",\"titleSource\":\"auto\"}\n",
            "omp_session_version_unsupported.4", 0);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-title-version", zoneId,
            "{\"type\":\"title\",\"v\":2,\"title\":\"t\",\"source\":\"auto\",\"updatedAt\":\"2026-10-12T00:00:00.000Z\",\"pad\":\" \"}\n",
            "omp_title_version_unsupported.2", 0);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-no-cwd", zoneId,
            "{\"type\":\"session\",\"version\":3,\"id\":\"s3t3-b-nocwd\",\"timestamp\":\"2026-10-12T00:00:00.000Z\",\"title\":\"t\",\"titleSource\":\"auto\"}\n",
            "omp_session_context_invalid", 0);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-compaction-boundary", zoneId,
            string.Concat(S3T3OmpBaseLines("s3t3-b-compaction", marker).Take(13)) +
            S3T3OmpEventLine("e89", "e11", "2026-10-12T00:00:20.000Z",
                "\"compaction\",\"summary\":\"x\",\"shortSummary\":\"y\",\"tokensBefore\":1,\"tokensAfter\":1,\"method\":\"m\",\"details\":{},\"fromExtension\":false,\"preserveData\":{}"),
            "omp_compaction_boundary_invalid", 13);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-tool-call", zoneId,
            string.Concat(S3T3OmpBaseLines("s3t3-b-toolcall", marker).Take(6)) +
            S3T3OmpEventLine("e88", "e04", "2026-10-12T00:00:20.000Z",
                "\"message\",\"message\":{\"role\":\"toolResult\",\"toolName\":\"bash\",\"content\":[],\"details\":{},\"isError\":false,\"timestamp\":\"2026-10-12T00:00:20.000Z\"}"),
            "omp_message_tool_call_invalid", 6);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-message-shape", zoneId,
            S3T3OmpBase("s3t3-b-msgshape", marker) +
            "{\"type\":\"message\",\"id\":\"e87\",\"parentId\":\"e12\",\"timestamp\":\"2026-10-12T00:00:20.000Z\"}\n",
            "omp_message_shape_invalid", S3T3OmpRecordCount);
        await RunS3T3OmpRefusalAsync(harness, "s3t3-omp-b-custom-data", zoneId,
            S3T3OmpBase("s3t3-b-customdata", marker) +
            "{\"type\":\"custom\",\"customType\":\"mystery_state\",\"id\":\"e86\",\"parentId\":\"e12\",\"timestamp\":\"2026-10-12T00:00:20.000Z\"}\n",
            "omp_custom_data_missing", S3T3OmpRecordCount);
        Console.WriteLine("PASS every uninterpretable OMP record shape is refused explicitly with a named diagnostic at its exact offset");

        // Restart resumes from the persisted checkpoint.
        var beforeRestart = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceStatusOperation)).ConfigureAwait(false);
        harness.Restart();
        var afterRestart = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(afterRestart.Status == "ingest_idle" &&
                afterRestart.SourceIngestion!.ConsumedOffset == beforeRestart.SourceIngestion!.ConsumedOffset &&
                afterRestart.SourceIngestion!.EventCount == beforeRestart.SourceIngestion!.EventCount,
            "The restart did not resume from the persisted OMP checkpoint (status=" + afterRestart.Status + ").");
        await File.AppendAllTextAsync(locator, S3T3OmpEventLine("e17", "e16", "2026-10-12T00:00:18.000Z",
            "\"message\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"" + marker + " restarted answer\"}],\"stopReason\":\"stop\",\"timestamp\":\"2026-10-12T00:00:18.000Z\"}")).ConfigureAwait(false);
        var afterRestartAppend = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(afterRestartAppend.Status == "ingest_advanced" &&
                afterRestartAppend.SourceIngestion!.EventCount == afterRestart.SourceIngestion!.EventCount + 1,
            "Post-restart ingestion did not continue from the correct offset (status=" + afterRestartAppend.Status + ").");
        Console.WriteLine("PASS restart resumes the OMP checkpoint without duplicates");

        // Original bytes and metadata unchanged; encrypted persistence.
        var expectedBytes = await File.ReadAllBytesAsync(locator).ConfigureAwait(false);
        var lastWrite = File.GetLastWriteTimeUtc(locator);
        _ = await harness.OwnerSendAsync(Simple(ServiceRequestHandler.SourceIngestOperation)).ConfigureAwait(false);
        Require(File.GetLastWriteTimeUtc(locator) == lastWrite,
            "A read-only OMP ingest modified the source metadata.");
        var journalAfter = harness.DecodeJournal(sourceId, 1)!;
        Require(journalAfter.Events.Length == afterRestartAppend.SourceIngestion!.EventCount,
            "The OMP journal lost or duplicated events on re-read.");
        var vaultFiles = Directory.GetFiles(harness.VaultDirectoryPath, "*", SearchOption.AllDirectories);
        Require(vaultFiles.Length >= 3, "The in-process vault did not persist its private files.");
        foreach (var path in vaultFiles)
        {
            var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            Require(!text.Contains(marker, StringComparison.Ordinal) && !text.Contains(locator, StringComparison.Ordinal),
                "An encrypted vault file persisted plaintext payload or locator: " + Path.GetFileName(path) + ".");
        }

        Require((await File.ReadAllBytesAsync(locator).ConfigureAwait(false)).AsSpan().SequenceEqual(expectedBytes),
            "The read-only OMP adapter modified the original source bytes.");
        Console.WriteLine("PASS encrypted journals, original bytes and metadata unchanged across read-only ingestion");

        var expectedPrefixBytes = System.Text.Encoding.UTF8.GetBytes(S3T3OmpBase("s3t3-coordinator-session", marker));
        Require(expectedPrefixBytes.AsSpan().SequenceEqual(expectedBytes.AsSpan(0, expectedPrefixBytes.Length)),
            "The ingested OMP prefix no longer matches the fixture bytes the journal was derived from.");
    }

    /// <summary>
    /// One boundary fixture on its own enrollment: ingestion must stop with the
    /// named diagnostic at the exact offset, persist exactly one diagnostic,
    /// accept the prefix records before it, and stay blocked on re-import.
    /// </summary>
    private static async Task RunS3T3OmpRefusalAsync(
        S3T3OmpCoordinatorHarness harness,
        string sourceId,
        string zoneId,
        string content,
        string expectedCode,
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
            SourceFormat: OmpSessionNormalizer.FormatName,
            SourceFormatVersion: OmpSessionNormalizer.FormatVersionValue)).ConfigureAwait(false);
        Require(enrolled.Status == "source_enrolled",
            "The boundary source enrollment failed (source=" + sourceId + "; error=" + enrolled.Error + ").");

        var ingest = await harness.OwnerSendAsync(new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.SourceIngestOperation,
            sourceId)).ConfigureAwait(false);
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
    /// A true branch fixture: the id/parentId chain forks, and the consumer
    /// round-trip (journal event → declared node id → span → original record →
    /// declared parent edge) must resolve the exact fork, every parent edge
    /// and the compaction boundary from the original bytes. This is what keeps
    /// ingestion a faithful reference to the source event structure instead of
    /// a flattened role list.
    /// </summary>
    private static async Task RunS3T3OmpBranchPhaseAsync(S3T3OmpCoordinatorHarness harness, string zoneId, string marker)
    {
        const string sourceId = "s3t3-omp-branch-source";
        const string sessionId = "s3t3-omp-branch-session";
        var locator = Path.Combine(harness.Root, "omp-branch-session.jsonl");
        var content = string.Concat(
            "{\"type\":\"title\",\"v\":1,\"title\":\"" + sessionId + "\",\"source\":\"auto\",\"updatedAt\":\"2026-10-12T00:00:00.000Z\",\"pad\":\" \"}\n",
            "{\"type\":\"session\",\"version\":3,\"id\":\"" + sessionId + "\",\"timestamp\":\"2026-10-12T00:00:00.500Z\",\"cwd\":\"C:\\\\synthetic\\\\omp-branch\",\"title\":\"" + sessionId + "\",\"titleSource\":\"auto\"}\n",
            S3T3OmpEventLine("b01", null, "2026-10-12T00:00:01.000Z",
                "\"thinking_level_change\",\"thinkingLevel\":\"high\",\"configured\":true"),
            S3T3OmpEventLine("b02", "b01", "2026-10-12T00:00:02.000Z",
                "\"message\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"" + marker + " branch request\"}],\"steering\":false,\"attribution\":\"user\",\"timestamp\":\"2026-10-12T00:00:02.000Z\"}"),
            S3T3OmpEventLine("b03", "b02", "2026-10-12T00:00:03.000Z",
                "\"message\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"toolCall\",\"id\":\"call-b1\",\"name\":\"read\",\"arguments\":{\"path\":\"C:\\\\synthetic\\\\omp-branch\\\\a.txt\"},\"intent\":\"read\"}],\"stopReason\":\"toolUse\",\"timestamp\":\"2026-10-12T00:00:03.000Z\"}"),
            // The fork: one parent, two children.
            S3T3OmpEventLine("b04", "b03", "2026-10-12T00:00:04.000Z",
                "\"message\",\"message\":{\"role\":\"toolResult\",\"toolCallId\":\"call-b1\",\"toolName\":\"read\",\"content\":[{\"type\":\"text\",\"text\":\"" + marker + " branch tool output\"}],\"details\":{},\"isError\":false,\"timestamp\":\"2026-10-12T00:00:04.000Z\"}"),
            S3T3OmpEventLine("b05", "b03", "2026-10-12T00:00:05.000Z",
                "\"message\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"" + marker + " branch second answer\"}],\"stopReason\":\"stop\",\"timestamp\":\"2026-10-12T00:00:05.000Z\"}"),
            S3T3OmpEventLine("b06", "b04", "2026-10-12T00:00:06.000Z",
                "\"custom\",\"customType\":\"tool_execution_start\",\"data\":{\"toolCallId\":\"call-b1\",\"toolName\":\"read\",\"startedAt\":\"2026-10-12T00:00:03.800Z\",\"args\":{},\"intent\":\"read\"}"),
            S3T3OmpEventLine("b07", "b05", "2026-10-12T00:00:07.000Z",
                "\"custom_message\",\"customType\":\"async-result\",\"content\":\"" + marker + " branch async result\",\"display\":false,\"details\":null,\"attribution\":\"agent\""),
            S3T3OmpEventLine("b08", "b07", "2026-10-12T00:00:08.000Z",
                "\"compaction\",\"summary\":\"" + marker + " branch summary\",\"shortSummary\":\"" + marker + " branch short\",\"firstKeptEntryId\":\"b02\",\"tokensBefore\":50,\"tokensAfter\":20,\"method\":\"openai-remote\",\"details\":{},\"fromExtension\":false,\"preserveData\":{}"));
        await File.WriteAllTextAsync(locator, content).ConfigureAwait(false);
        var enrolled = await harness.OwnerSendAsync(new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.SourceEnrollOperation,
            sourceId,
            zoneId,
            SourceLocator: locator,
            SourceFormat: OmpSessionNormalizer.FormatName,
            SourceFormatVersion: OmpSessionNormalizer.FormatVersionValue)).ConfigureAwait(false);
        Require(enrolled.Status == "source_enrolled",
            "The branch fixture enrollment failed (error=" + enrolled.Error + ").");

        var ingest = await harness.OwnerSendAsync(new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.SourceIngestOperation,
            sourceId)).ConfigureAwait(false);
        Require(ingest.Status == "ingest_advanced" &&
                ingest.SourceIngestion!.EventCount == 10 &&
                ingest.SourceIngestion!.ConsumedOffset == new FileInfo(locator).Length,
            "The branch fixture was not consumed exactly once (status=" + ingest.Status + "; events=" + ingest.SourceIngestion?.EventCount + ").");

        var journal = harness.DecodeJournal(sourceId, 1)!;
        var sourceBytes = await File.ReadAllBytesAsync(locator).ConfigureAwait(false);

        // Consumer round-trip: every journal event declares its node through
        // its id, and the original record at its span declares the exact parent
        // edge. Resolve the whole graph that way and prove the fork.
        var declaredIds = new HashSet<string>(StringComparer.Ordinal);
        var parentOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? compactionFirstKept = null;
        foreach (var normalized in journal.Events.OrderBy(candidate => candidate.Span.Start))
        {
            var slice = sourceBytes.AsSpan((int)normalized.Span.Start, (int)(normalized.Span.End - normalized.Span.Start));
            var record = JsonSerializer.Deserialize<JsonElement>(slice);
            var type = record.GetProperty("type").GetString();
            if (string.Equals(type, "session", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(type, "title", StringComparison.Ordinal))
            {
                continue;
            }

            var declaredId = record.GetProperty("id").GetString() ?? string.Empty;
            if (declaredId.Length == 0 || !declaredIds.Add(declaredId))
            {
                Require(false, "The branch round-trip re-declared an event id.");
            }

            Require(string.Equals(normalized.EventId, "omp-" + sourceId + "-" + declaredId, StringComparison.Ordinal),
                "The branch journal event does not carry its declared chain node.");
            var parent = record.GetProperty("parentId");
            var parentId = parent.ValueKind == JsonValueKind.Null ? null : parent.GetString();
            parentOf[declaredId] = parentId;
            if (string.Equals(type, "compaction", StringComparison.Ordinal))
            {
                compactionFirstKept = record.GetProperty("firstKeptEntryId").GetString();
            }
        }

        Require(declaredIds.Count == 8,
            "The branch fixture did not declare the expected event nodes (" + declaredIds.Count + ").");
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (node, parent) in parentOf)
        {
            if (parent is null)
            {
                continue;
            }

            Require(declaredIds.Contains(parent),
                "The branch round-trip left an unresolved parent edge (" + node + " -> " + parent + ").");
            if (!children.TryGetValue(parent, out var siblings))
            {
                siblings = new List<string>();
                children[parent] = siblings;
            }

            siblings.Add(node);
        }

        Require(children.TryGetValue("b03", out var forked) && forked.Count == 2 &&
                forked.Contains("b04", StringComparer.Ordinal) && forked.Contains("b05", StringComparer.Ordinal),
            "The branch round-trip did not resolve the declared fork (b03 -> b04, b05) from the original spans.");
        Require(compactionFirstKept is { Length: > 0 } kept && string.Equals(kept, "b02", StringComparison.Ordinal) &&
                declaredIds.Contains(kept),
            "The compaction boundary did not round-trip to a declared chain node.");
        Require(parentOf.ContainsKey("b01"),
            "The branch chain root was lost.");
        Require(parentOf.GetValueOrDefault("b01") is null,
            "The branch chain root must declare a null parent.");
        Require(parentOf.GetValueOrDefault("b06") == "b04" && parentOf.GetValueOrDefault("b07") == "b05" &&
                parentOf.GetValueOrDefault("b08") == "b07",
            "The branch round-trip did not resolve the descendant edges exactly.");
        Console.WriteLine("PASS a true branch fixture round-trips the declared parent edges, fork and compaction boundary from the journal spans");
    }

    /// <summary>
    /// The attribution a record's own declarations imply, used to prove the
    /// stored attribution was never rewritten by the adapter.
    /// </summary>
    private static SourceEventAuthor ExpectedOmpAuthorForRecord(JsonElement record)
    {
        var type = record.GetProperty("type").GetString();
        switch (type)
        {
            case "title":
            case "session":
            case "custom":
            case "thinking_level_change":
            case "service_tier_change":
            case "credential_pin":
            case "title_change":
            case "compaction":
                return SourceEventAuthor.System;
            case "custom_message":
                return string.Equals(record.GetProperty("attribution").GetString(), "user", StringComparison.Ordinal)
                    ? SourceEventAuthor.User
                    : SourceEventAuthor.Assistant;
            case "message":
                return record.GetProperty("message").GetProperty("role").GetString() switch
                {
                    "user" => SourceEventAuthor.User,
                    "assistant" => SourceEventAuthor.Assistant,
                    _ => SourceEventAuthor.Tool
                };
            default:
                throw new InvalidOperationException("Unexpected record type in the boundary expectation.");
        }
    }

    private static string ExpectedOmpKindForRecord(JsonElement record)
    {
        var type = record.GetProperty("type").GetString()!;
        switch (type)
        {
            case "message":
                return "omp.message." + record.GetProperty("message").GetProperty("role").GetString();
            case "custom":
                return "omp.custom." + record.GetProperty("customType").GetString();
            case "custom_message":
                return "omp.custom_message." + record.GetProperty("customType").GetString();
            default:
                return "omp." + type;
        }
    }
}
