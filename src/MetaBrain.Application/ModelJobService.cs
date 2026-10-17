using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

public sealed record ProviderConfig(
    string Provider,
    string Model,
    string BaseUrl,
    string ApiKey);

public sealed class ModelJobService
{
    public const string ProviderConfigResourceId = "sec-provider-config";
    public const string SpendLedgerResourceId = "sec-spend-ledger";
    public const string SystemZoneId = "core";

    public const decimal ApprovedReservationCostUsd = 0.0054m;

    private readonly IModelProviderAdapter _providerAdapter;
    private readonly IVaultLifecycle _vault;
    private readonly object _gate = new();
    private readonly HashSet<string> _activeJobIds = new();
    private CancellationTokenSource _inFlightCts = new();

    public ModelJobService(
        IVaultLifecycle vault,
        IModelProviderAdapter? providerAdapter = null)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _providerAdapter = providerAdapter ?? new OpenAiCompatibleProviderAdapter();
    }

    public JobRecord CreateJob(
        AuthenticatedContext context,
        string sourceResourceId,
        long sourceRevision,
        string destinationZoneId,
        string destinationResourceId,
        long destinationRevision,
        string provider,
        string model,
        decimal? maxCostUsd,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureUnlocked();
        EnsureTrusted(context);

        if (string.IsNullOrWhiteSpace(sourceResourceId) || !DomainIdentifiers.IsDomainIdentifier(sourceResourceId))
        {
            throw new ArgumentException("The source resource ID must be an opaque domain identifier.");
        }

        if (string.IsNullOrWhiteSpace(destinationZoneId) || string.IsNullOrWhiteSpace(destinationResourceId))
        {
            throw new ArgumentException("The destination zone and destination resource ID are required.");
        }
        // Validate source resource exists and is registered in a zone
        if (!store.TryGetZone(sourceResourceId, out var registeredSourceZone) || string.IsNullOrEmpty(registeredSourceZone))
        {
            throw new ArgumentException("The source resource is not registered in any zone.");
        }
        ValidateSourceEnrollment(sourceResourceId, store);


        // Validate destination resource exists and matches requested destination zone
        if (!store.TryGetZone(destinationResourceId, out var registeredDestZone) || string.IsNullOrEmpty(registeredDestZone))
        {
            throw new ArgumentException("The destination resource is not registered in any zone.");
        }
        if (!string.Equals(registeredDestZone, destinationZoneId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The destination resource is registered in zone '{registeredDestZone}', which does not match requested destination zone '{destinationZoneId}'.");
        }

        // 1. Validate pricing and approved model with known tokenizer/context (fails closed with pricing_unknown or unknown_context)
        if (!ModelPricingCatalog.TryGetApprovedModel(provider, model, out var priceCard, out var modelFailureReason) || priceCard is null)
        {
            throw new ArgumentException($"{modelFailureReason}: No approved pricing card found for '{provider}:{model}'.");
        }

        // 2. Read source note text from store and validate exact revision
        var sourceText = ReadSourceText(sourceResourceId, sourceRevision, store);

        // 3. Grounded framing-inclusive input token estimation with documented tokenizer upper bound
        var systemPrompt = BuildStructuringSystemPrompt();
        var estimatedInputTokens = EstimateFramedInputTokens(provider, model, systemPrompt, sourceText, (int)SpendLedger.MaximumJobOutputTokens);
        if (estimatedInputTokens > SpendLedger.MaximumJobInputTokens)
        {
            throw new ArgumentException($"Estimated framed input tokens ({estimatedInputTokens}) exceed the whole-job cap of {SpendLedger.MaximumJobInputTokens}.");
        }
        var maxOutputTokens = SpendLedger.MaximumJobOutputTokens;
        var now = DateTimeOffset.UtcNow;

        // Use the approved full-cap reservation cost of $0.0054 (covers 10k in / 2k out at peak cache-miss prices)
        var reservationCost = ApprovedReservationCostUsd;
        if (maxCostUsd.HasValue && maxCostUsd.Value < reservationCost)
        {
            throw new ArgumentException($"The specified maximum cost (${maxCostUsd.Value}) is less than the required pre-send reservation (${reservationCost}).");
        }

        // 3. Pre-send budget & rolling ledger check
        string jobId;
        lock (_gate)
        {
            var ledger = LoadSpendLedger(store);
            if (!ledger.CanReserve(reservationCost, now, out var refusalCode))
            {
                throw new InvalidOperationException(refusalCode ?? "budget_exceeded");
            }

            jobId = DomainIdentifiers.JobIdPrefix + Guid.NewGuid().ToString("N");
            ledger.AddReservation(jobId, provider, model, reservationCost, now);
            SaveSpendLedger(ledger, store);
        }

        var scope = new JobScopeSnapshot(
            SourceResourceId: sourceResourceId,
            SourceRevision: sourceRevision,
            DestinationZoneId: destinationZoneId,
            DestinationResourceId: destinationResourceId,
            DestinationRevision: destinationRevision,
            Provider: provider,
            Model: model,
            MaximumCostUsd: maxCostUsd ?? SpendLedger.MaximumJobCostUsd,
            MaxInputTokens: SpendLedger.MaximumJobInputTokens,
            MaxOutputTokens: maxOutputTokens);

        var stored = new StoredJobRecord(
            SchemaVersion: JobCodec.CurrentSchemaVersion,
            Id: jobId,
            ZoneId: destinationZoneId,
            Kind: JobKind.Structuring,
            State: JobState.Pending,
            Scope: scope,
            Usage: null,
            Failure: null,
            OutputProposalIds: null,
            CreatedAtUtc: now,
            StartedAtUtc: null,
            CompletedAtUtc: null);

        var payload = JobCodec.EncodeStored(stored);
        try
        {
            var revision = store.WriteContent(jobId, destinationZoneId, payload, expectedRevision: 0);
            return ToJobRecord(stored, revision);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public async Task<JobRecord> StartJobAsync(
        AuthenticatedContext context,
        string jobId,
        IManagedResourceStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureUnlocked();
        EnsureTrusted(context);

        if (!DomainIdentifiers.IsJobId(jobId))
        {
            throw new ArgumentException("The job ID must be a valid job identifier.");
        }

        var stored = ReadStoredJob(jobId, store, out var liveRevision);
        if (stored.State != JobState.Pending)
        {
            throw new InvalidOperationException($"Job '{jobId}' is in state '{stored.State}' and cannot be started.");
        }

        // 1. Revalidate scope snapshot & source note before source access
        EnsureUnlocked();
        if (!store.TryGetZone(stored.Scope.SourceResourceId, out var registeredSourceZone) || string.IsNullOrEmpty(registeredSourceZone))
        {
            lock (_gate)
            {
                var ledger = LoadSpendLedger(store);
                ledger.ReleaseReservation(jobId);
                SaveSpendLedger(ledger, store);
            }
            return FailJob(stored, liveRevision, "source_unavailable", "The source resource is not registered in any zone.", store);
        }

        if (!store.TryGetZone(stored.Scope.DestinationResourceId, out var registeredDestZone) || string.IsNullOrEmpty(registeredDestZone) ||
            !string.Equals(registeredDestZone, stored.Scope.DestinationZoneId, StringComparison.Ordinal))
        {
            lock (_gate)
            {
                var ledger = LoadSpendLedger(store);
                ledger.ReleaseReservation(jobId);
                SaveSpendLedger(ledger, store);
            }
            return FailJob(stored, liveRevision, "destination_unavailable", "The destination resource is not registered in the specified destination zone.", store);
        }

        string sourceText;
        try
        {
            sourceText = ReadSourceText(stored.Scope.SourceResourceId, stored.Scope.SourceRevision, store);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                var ledger = LoadSpendLedger(store);
                ledger.ReleaseReservation(jobId);
                SaveSpendLedger(ledger, store);
            }
            return FailJob(stored, liveRevision, "source_unavailable", ex.Message, store);
        }
        try
        {
            ValidateSourceEnrollment(stored.Scope.SourceResourceId, store);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                var ledger = LoadSpendLedger(store);
                ledger.ReleaseReservation(jobId);
                SaveSpendLedger(ledger, store);
            }
            return FailJob(stored, liveRevision, "source_unavailable", ex.Message, store);
        }


        lock (_gate)
        {
            _activeJobIds.Add(jobId);
        }

        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            stored = stored with { State = JobState.Executing, StartedAtUtc = startedAt };
            liveRevision = WriteJobRecord(stored, liveRevision, store);

            // 2. Revalidate scope and consent before provider send
            EnsureUnlocked();
            CancellationToken inFlightToken;
            lock (_gate)
            {
                inFlightToken = _inFlightCts.Token;
            }
            if (inFlightToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                lock (_gate)
                {
                    var ledger = LoadSpendLedger(store);
                    ledger.ReleaseReservation(jobId);
                    SaveSpendLedger(ledger, store);
                }
                return AbortJob(stored, liveRevision, "cancelled", "Job execution was cancelled before send.", store);
            }

            var providerConfig = LoadProviderConfig(store);
            if (providerConfig is null ||
                !string.Equals(providerConfig.Provider, stored.Scope.Provider, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(providerConfig.Model, stored.Scope.Model, StringComparison.OrdinalIgnoreCase))
            {
                lock (_gate)
                {
                    var ledger = LoadSpendLedger(store);
                    ledger.ReleaseReservation(jobId);
                    SaveSpendLedger(ledger, store);
                }

                stored = stored with
                {
                    State = JobState.Failed,
                    Failure = new JobFailureReason("provider_unconfigured", "No matching provider configuration found in vault."),
                    CompletedAtUtc = DateTimeOffset.UtcNow
                };
                liveRevision = WriteJobRecord(stored, liveRevision, store);
                return ToJobRecord(stored, liveRevision);
            }

            var systemPrompt = BuildStructuringSystemPrompt();
            var userPrompt = sourceText;

            // Bounded framing-inclusive pre-send checks
            var estimatedFramedTokens = EstimateFramedInputTokens(stored.Scope.Provider, stored.Scope.Model, systemPrompt, userPrompt, (int)stored.Scope.MaxOutputTokens);
            if (estimatedFramedTokens > stored.Scope.MaxInputTokens)
            {
                lock (_gate)
                {
                    var ledger = LoadSpendLedger(store);
                    ledger.ReleaseReservation(jobId);
                    SaveSpendLedger(ledger, store);
                }
                return FailJob(stored, liveRevision, "input_cap_exceeded", $"Framed input tokens ({estimatedFramedTokens}) exceed limit of {stored.Scope.MaxInputTokens}.", store);
            }

            var maxTokens = (int)Math.Min(stored.Scope.MaxOutputTokens, SpendLedger.MaximumJobOutputTokens);
            var request = new ModelProviderRequest(
                Provider: stored.Scope.Provider,
                Model: stored.Scope.Model,
                BaseUrl: providerConfig.BaseUrl,
                ApiKey: providerConfig.ApiKey,
                SystemPrompt: systemPrompt,
                UserPrompt: userPrompt,
                MaxTokens: maxTokens,
                Timeout: TimeSpan.FromSeconds(60));

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(inFlightToken, cancellationToken);
            ModelProviderResponse response;
            try
            {
                response = await _providerAdapter.CompleteAsync(request, linkedCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                response = new ModelProviderResponse(
                    Success: false,
                    Content: null,
                    InputTokens: 0,
                    OutputTokens: 0,
                    CachedInputTokens: 0,
                    ErrorCode: "provider_error",
                    ErrorMessage: ex.Message,
                    RequestSent: true);
            }

            var completedAt = DateTimeOffset.UtcNow;

            // 3. Handle provider send completion
            if (!response.Success)
            {
                var isCancelled = string.Equals(response.ErrorCode, "cancelled", StringComparison.OrdinalIgnoreCase);

                lock (_gate)
                {
                    var ledger = LoadSpendLedger(store);
                    if (response.RequestSent)
                    {
                        // Ambiguous post-send failure: request was dispatched across network.
                        // Retain conservative pre-send reservation to protect rolling budget ceiling!
                        ledger.RetainUnknownReservation(jobId, response.ErrorCode ?? "unknown_post_send");
                    }
                    else
                    {
                        // Proven not sent: pre-send failure safely releases budget reservation.
                        ledger.ReleaseReservation(jobId);
                    }
                    SaveSpendLedger(ledger, store);
                }

                stored = stored with
                {
                    State = isCancelled ? JobState.Cancelled : JobState.Failed,
                    Failure = new JobFailureReason(response.ErrorCode ?? "provider_error", response.ErrorMessage ?? "Provider request failed."),
                    CompletedAtUtc = completedAt
                };
                liveRevision = WriteJobRecord(stored, liveRevision, store);
                return ToJobRecord(stored, liveRevision);
            }

            // 4. Update rolling spend ledger with actual tokens & cost
            ModelPricingCatalog.TryGetPrice(stored.Scope.Provider, stored.Scope.Model, out var priceCard);
            var actualCost = priceCard?.ComputeCost(response.InputTokens, response.OutputTokens, response.CachedInputTokens) ?? 0m;

            lock (_gate)
            {
                var spendLedger = LoadSpendLedger(store);
                spendLedger.CommitSpend(
                    jobId: jobId,
                    provider: stored.Scope.Provider,
                    model: stored.Scope.Model,
                    inputTokens: response.InputTokens,
                    outputTokens: response.OutputTokens,
                    actualCost: actualCost,
                    now: completedAt);
                SaveSpendLedger(spendLedger, store);
            }

            var usage = new JobUsage(response.InputTokens, response.OutputTokens, actualCost);
            stored = stored with { Usage = usage };

            // 5. Cap check on actual reported usage: cap violations do not erase spend
            if (response.InputTokens > stored.Scope.MaxInputTokens)
            {
                return FailJob(stored, liveRevision, "input_cap_exceeded", $"Actual input tokens ({response.InputTokens}) exceeded limit of {stored.Scope.MaxInputTokens}.", store);
            }
            if (response.OutputTokens > stored.Scope.MaxOutputTokens)
            {
                return FailJob(stored, liveRevision, "output_cap_exceeded", $"Actual output tokens ({response.OutputTokens}) exceeded limit of {stored.Scope.MaxOutputTokens}.", store);
            }

            // 5. Revalidate scope & vault before commit
            EnsureUnlocked();
            if (inFlightToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                return AbortJob(stored, liveRevision, "cancelled", "Job execution was cancelled before commit.", store);
            }
            // Revalidate source availability & revision before commit: lock/revoke/revision change blocks commit
            if (!store.TryGetZone(stored.Scope.SourceResourceId, out var currentSourceZone) || string.IsNullOrEmpty(currentSourceZone))
            {
                return FailJob(stored, liveRevision, "source_unavailable", "Source resource removed before commit.", store);
            }
            try
            {
                _ = ReadSourceText(stored.Scope.SourceResourceId, stored.Scope.SourceRevision, store);
            }
            catch (Exception ex)
            {
                return FailJob(stored, liveRevision, "source_unavailable", $"Source resource unavailable before commit: {ex.Message}", store);
            }
        try
        {
            ValidateSourceEnrollment(stored.Scope.SourceResourceId, store);
        }
        catch (Exception ex)
        {
            var detail = ex.Message.Contains("revoked", StringComparison.OrdinalIgnoreCase)
                ? $"Source resource '{stored.Scope.SourceResourceId}' was revoked before commit: {ex.Message}"
                : $"Source resource unavailable before commit: {ex.Message}";
            return FailJob(stored, liveRevision, "source_unavailable", detail, store);
        }


            // 6. Validated output boundary
            if (!JobOutputValidator.TryValidate(response.Content ?? string.Empty, sourceText, out var output, out var errorCode, out var errorMessage))
            {
                stored = stored with
                {
                    State = JobState.Failed,
                    Failure = new JobFailureReason(errorCode ?? "malformed_output", errorMessage ?? "Model output validation failed."),
                    CompletedAtUtc = completedAt
                };
                liveRevision = WriteJobRecord(stored, liveRevision, store);
                return ToJobRecord(stored, liveRevision);
            }

            // Revalidate destination before commit
            if (!store.TryGetZone(stored.Scope.DestinationResourceId, out var destZone) || string.IsNullOrEmpty(destZone) ||
                !string.Equals(destZone, stored.Scope.DestinationZoneId, StringComparison.Ordinal))
            {
                return FailJob(stored, liveRevision, "destination_unavailable", "Destination resource was removed or zone changed before commit.", store);
            }

        // 7. Commit structured items into proposals inside the destination zone/inbox
        var proposalIds = new List<string>();
        var itemIndex = 0;
        foreach (var item in output!.Items)
        {
            itemIndex++;
            var idempotencyKey = $"job-{jobId}-item-{itemIndex}";
            var proposalId = ProposalValidator.DeriveProposalId(jobId, stored.Scope.DestinationResourceId, idempotencyKey);

            var memoryKind = MapToMemoryKind(item.Kind);
            var evidence = memoryKind == MemoryKind.Question ? EvidenceLevel.Unverified : EvidenceLevel.ReportedSource;
            var sourceRef = new MemorySourceReference(
                SourceId: stored.Scope.SourceResourceId,
                Locator: null,
                Availability: SourceAvailability.Available,
                SourceRevision: stored.Scope.SourceRevision,
                Span: null);

            var memoryDraft = new MemoryItemDraft(
                Kind: memoryKind,
                Author: SpeakerRole.Assistant,
                CreationMethod: CreationMethod.VerbatimExtract,
                Evidence: evidence,
                Level: AbstractionLevel.L1,
                Title: item.Title,
                Body: item.Body,
                Sources: new[] { sourceRef },
                OccurredAtUtc: null,
                RecordedAtUtc: completedAt,
                Applicability: null);

            var storedProposal = new StoredProposal(
                SchemaVersion: ProposalCodec.CurrentSchemaVersion,
                Id: proposalId,
                ZoneId: stored.Scope.DestinationZoneId,
                Kind: ProposalKind.Create,
                DestinationResourceId: stored.Scope.DestinationResourceId,
                DestinationRevision: stored.Scope.DestinationRevision,
                SubjectResourceId: null,
                SubjectRevision: null,
                IdempotencyKey: idempotencyKey,
                PayloadHash: string.Empty,
                Item: memoryDraft,
                Executor: ExecutorKind.StructuringJob,
                RecordedAtUtc: completedAt,
                ClaimedUserConfirmed: false);

            storedProposal = storedProposal with { PayloadHash = ProposalValidator.ComputePayloadHash(storedProposal) };
            var proposalPayload = ProposalCodec.EncodeStored(storedProposal);
            try
            {
                store.WriteContent(proposalId, stored.Scope.DestinationZoneId, proposalPayload, expectedRevision: 0);
                proposalIds.Add(proposalId);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(proposalPayload);
            }
        }

        stored = stored with
        {
            State = JobState.Succeeded,
            OutputProposalIds = proposalIds.ToArray(),
            CompletedAtUtc = completedAt
        };

            liveRevision = WriteJobRecord(stored, liveRevision, store);
            return ToJobRecord(stored, liveRevision);
        }
        finally
        {
            lock (_gate)
            {
                _activeJobIds.Remove(jobId);
            }
        }
    }

    public JobRecord CancelJob(
        AuthenticatedContext context,
        string jobId,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureUnlocked();
        EnsureTrusted(context);

        if (!DomainIdentifiers.IsJobId(jobId))
        {
            throw new ArgumentException("The job ID must be a valid job identifier.");
        }

        var stored = ReadStoredJob(jobId, store, out var liveRevision);
        if (stored.State is JobState.Succeeded or JobState.Failed or JobState.Cancelled)
        {
            return ToJobRecord(stored, liveRevision);
        }
        lock (_gate)
        {
            var ledger = LoadSpendLedger(store);
            if (stored.State == JobState.Executing)
            {
                ledger.RetainUnknownReservation(jobId, "cancelled");
                _inFlightCts.Cancel();
            }
            else
            {
                ledger.ReleaseReservation(jobId);
            }
            SaveSpendLedger(ledger, store);
        }
        stored = stored with
        {
            State = JobState.Cancelled,
            Failure = new JobFailureReason("cancelled", "The job was cancelled by owner request."),
            CompletedAtUtc = DateTimeOffset.UtcNow
        };

        liveRevision = WriteJobRecord(stored, liveRevision, store);
        return ToJobRecord(stored, liveRevision);
    }

    public JobRecord GetJob(string jobId, IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        EnsureUnlocked();

        var stored = ReadStoredJob(jobId, store, out var liveRevision);
        return ToJobRecord(stored, liveRevision);
    }

    public IReadOnlyList<JobRecord> ListJobs(IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        EnsureUnlocked();

        var list = new List<JobRecord>();
        var resources = store.ListResources();
        foreach (var res in resources)
        {
            if (DomainIdentifiers.IsJobId(res.ResourceId))
            {
                var stored = ReadStoredJob(res.ResourceId, store, out var liveRevision);
                list.Add(ToJobRecord(stored, liveRevision));
            }
        }

        return list.OrderByDescending(j => j.CreatedAtUtc).ToList();
    }

    public void SaveProviderConfig(
        AuthenticatedContext context,
        string provider,
        string model,
        string baseUrl,
        string apiKey,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        EnsureUnlocked();
        EnsureTrusted(context);

        var config = new ProviderConfig(provider, model, baseUrl, apiKey);
        var json = JsonSerializer.SerializeToUtf8Bytes(config);
        try
        {
            var existingRevision = ResolveRevisionOrZero(store, ProviderConfigResourceId);
            store.WriteContent(ProviderConfigResourceId, SystemZoneId, json, expectedRevision: existingRevision);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    public ProviderConfig? LoadProviderConfig(IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        EnsureUnlocked();

        try
        {
            var bytes = store.ReadContent(ProviderConfigResourceId, null);
            if (bytes.Length == 0) return null;
            return JsonSerializer.Deserialize<ProviderConfig>(bytes);
        }
        catch (ManagedResourceUnavailableException)
        {
            return null;
        }
    }

    public SpendLedger LoadSpendLedger(IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        EnsureUnlocked();

        try
        {
            var bytes = store.ReadContent(SpendLedgerResourceId, null);
            return SpendLedgerCodec.Decode(bytes);
        }
        catch (ManagedResourceUnavailableException)
        {
            return new SpendLedger();
        }
    }

    public void SaveSpendLedger(SpendLedger ledger, IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(store);
        EnsureUnlocked();

        var bytes = SpendLedgerCodec.Encode(ledger);
        try
        {
            var existingRevision = ResolveRevisionOrZero(store, SpendLedgerResourceId);
            store.WriteContent(SpendLedgerResourceId, SystemZoneId, bytes, expectedRevision: existingRevision);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void AbortInFlightOnLockOrRestart()
    {
        lock (_gate)
        {
            _inFlightCts.Cancel();
            _inFlightCts.Dispose();
            _inFlightCts = new CancellationTokenSource();
            _activeJobIds.Clear();
        }
    }

    private static string ReadSourceText(string sourceResourceId, long sourceRevision, IManagedResourceStore store)
    {
        var content = store.ReadContent(sourceResourceId, sourceRevision);
        var storedSource = RegisteredSourceCodec.DecodeStored(content);
        if (storedSource.Text is null)
        {
            throw new ArgumentException("The registered source has no verbatim text content.");
        }

        return storedSource.Text;
    }

    private StoredJobRecord ReadStoredJob(string jobId, IManagedResourceStore store, out long liveRevision)
    {
        var content = store.ReadContent(jobId, null);
        var stored = JobCodec.DecodeStored(content);
        liveRevision = ResolveLiveRevision(store, jobId);

        bool isExecutingLocally;
        lock (_gate)
        {
            isExecutingLocally = _activeJobIds.Contains(jobId);
        }

        if (stored.State == JobState.Executing && !isExecutingLocally)
        {
            lock (_gate)
            {
                var ledger = LoadSpendLedger(store);
                ledger.RetainUnknownReservation(jobId, "interrupted_by_restart");
                SaveSpendLedger(ledger, store);
            }

            stored = stored with
            {
                State = JobState.Failed,
                Failure = new JobFailureReason("interrupted_by_restart", "Job was executing when service restarted or crashed; execution aborted and reservation retained."),
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
            liveRevision = WriteJobRecord(stored, liveRevision, store);
        }

        return stored;
    }

    private static long WriteJobRecord(StoredJobRecord record, long expectedRevision, IManagedResourceStore store)
    {
        var payload = JobCodec.EncodeStored(record);
        try
        {
            return store.WriteContent(record.Id, record.ZoneId, payload, expectedRevision: expectedRevision);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static long ResolveLiveRevision(IManagedResourceReader store, string resourceId)
    {
        if (store is IManagedResourceStore writable)
        {
            var found = writable.ListResources().FirstOrDefault(r => r.ResourceId == resourceId);
            if (found is not null) return found.Revision;
        }

        return 1;
    }

    private static long ResolveRevisionOrZero(IManagedResourceStore store, string resourceId)
    {
        var found = store.ListResources().FirstOrDefault(r => r.ResourceId == resourceId);
        return found?.Revision ?? 0;
    }

    private static JobRecord ToJobRecord(StoredJobRecord stored, long revision) => new(
        Id: stored.Id,
        ZoneId: stored.ZoneId,
        Revision: revision,
        SchemaVersion: stored.SchemaVersion,
        Kind: stored.Kind,
        State: stored.State,
        Scope: stored.Scope,
        Usage: stored.Usage,
        Failure: stored.Failure,
        OutputProposalIds: stored.OutputProposalIds,
        CreatedAtUtc: stored.CreatedAtUtc,
        StartedAtUtc: stored.StartedAtUtc,
        CompletedAtUtc: stored.CompletedAtUtc);

    private static JobRecord AbortJob(StoredJobRecord stored, long liveRevision, string code, string message, IManagedResourceStore store)
    {
        stored = stored with
        {
            State = JobState.Cancelled,
            Failure = new JobFailureReason(code, message),
            CompletedAtUtc = DateTimeOffset.UtcNow
        };
        liveRevision = WriteJobRecord(stored, liveRevision, store);
        return ToJobRecord(stored, liveRevision);
    }

    private static JobRecord FailJob(StoredJobRecord stored, long liveRevision, string code, string message, IManagedResourceStore store)
    {
        stored = stored with
        {
            State = JobState.Failed,
            Failure = new JobFailureReason(code, message),
            CompletedAtUtc = DateTimeOffset.UtcNow
        };
        liveRevision = WriteJobRecord(stored, liveRevision, store);
        return ToJobRecord(stored, liveRevision);
    }

    private static MemoryKind MapToMemoryKind(string kind) => kind.ToLowerInvariant() switch
    {
        "claim" => MemoryKind.Claim,
        "observation" => MemoryKind.Observation,
        "question" => MemoryKind.Question,
        "hypothesis" => MemoryKind.Hypothesis,
        "decision" => MemoryKind.Decision,
        "lesson" => MemoryKind.Lesson,
        _ => MemoryKind.Claim
    };

    private static string BuildStructuringSystemPrompt() =>
        "You are an expert research structuring assistant for Meta Brain.\n" +
        "Analyze the provided research note and extract structured items strictly conforming to the JSON schema.\n" +
        "Rules:\n" +
        "1. Each item must have:\n" +
        "   - kind: one of \"claim\", \"observation\", \"question\", \"hypothesis\", \"decision\", \"lesson\"\n" +
        "   - title: concise title (<256 chars)\n" +
        "   - body: detailed explanation (<16384 chars)\n" +
        "   - excerpt: verbatim quote copied directly from the text to serve as proof\n" +
        "2. The output MUST be valid JSON with format:\n" +
        "   {\"summary\": \"<brief summary>\", \"items\": [{\"kind\": \"...\", \"title\": \"...\", \"body\": \"...\", \"excerpt\": \"...\"}]}\n" +
        "3. Do not include markdown code fences or explanatory text outside the JSON.";

    public static long EstimateFramedInputTokens(string provider, string model, string systemPrompt, string sourceText, int maxOutputTokens = 2000)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(sourceText);

        if (!ModelPricingCatalog.TryGetApprovedModel(provider, model, out var priceCard, out var modelFailureReason) || priceCard is null)
        {
            throw new ArgumentException($"{modelFailureReason}: No approved pricing card found for '{provider}:{model}'.");
        }

        var boundedMaxTokens = (int)Math.Min(Math.Max(1, maxOutputTokens), SpendLedger.MaximumJobOutputTokens);
        var fullJsonString = OpenAiCompatibleProviderAdapter.BuildRequestJson(model, systemPrompt, sourceText, boundedMaxTokens);
        var fullJsonBytes = (long)Encoding.UTF8.GetByteCount(fullJsonString);

        // Grounded Tokenizer Bound & Official DeepSeek-V4.1-Flash Citations:
        // 1. Context Window: 1,000,000 tokens (1M tokens) for deepseek-flash (DeepSeek-V4.1-Flash-1017)
        //    per official DeepSeek documentation (https://api-docs.deepseek.com/quick_start/pricing).
        // 2. Tokenizer Architecture: PreTrainedTokenizerFast with ByteLevel BPE from Hugging Face
        //    deepseek-ai/DeepSeek-V4.1-Flash (tokenizer.json, tokenizer_config.json).
        //    In ByteLevel BPE, all 256 individual byte values (0x00..0xFF) have explicit single-character
        //    entries in the base vocabulary (verified: 0 missing bytes in model.vocab).
        //    Because BPE merges only combine >= 2 tokens into 1 token and never split or expand,
        //    each UTF-8 byte maps to at most 1 token: token_count <= utf8_byte_count is a proven
        //    mathematical theorem for any valid UTF-8 string.
        // 3. Chat Template Special Tokens & Framing:
        //    Official DeepSeek-V4.1 chat template (chat_template.jinja, encoding/encoding.py):
        //    - BOS: <｜begin▁of▁sentence｜> (1 token, id 0, with delimiter U+2581 LOWER ONE EIGHTH BLOCK)
        //    - System turn delimiter: <｜System｜> (1 token, id 128799)
        //    - Reasoning effort prefix: Reasoning Effort: 75 (range 1-100, the higher the value, the more thorough the reasoning)\n\n (25 tokens)
        //    - User turn delimiter: <｜User｜> (1 token, id 128803)
        //    - Assistant turn delimiter & priming: <｜Assistant｜><think> (2 tokens, ids 128804, 128821)
        //    Total special/framing tokens = exactly 30 tokens in thinking mode (or 5 tokens in chat mode).
        //    We bound template and role metadata overhead to exactly 32 tokens (allowing for 100-budget effort string).
        // 4. Formally Grounded Request Bound:
        //    Tied directly to the actual request: systemPrompt bytes + sourceText bytes + template framing tokens (32).
        //    Also bounded against wire JSON HTTP serialization bytes to guarantee pre-send conservatism across all layers.
        var systemBytes = (long)Encoding.UTF8.GetByteCount(systemPrompt);
        var sourceBytes = (long)Encoding.UTF8.GetByteCount(sourceText);
        const long templateSpecialTokensBound = 32;
        var fullRequestTokenBound = Math.Max(fullJsonBytes, systemBytes + sourceBytes + templateSpecialTokensBound);

        // Verify request fits within model context window
        if (fullRequestTokenBound + boundedMaxTokens > priceCard.ContextWindow)
        {
            throw new ArgumentException($"Framed request ({fullRequestTokenBound} tokens + {boundedMaxTokens} output) exceeds model context window of {priceCard.ContextWindow}.");
        }

        return fullRequestTokenBound;
    }

    public static long EstimateFramedInputTokens(string systemPrompt, string sourceText)
        => EstimateFramedInputTokens("deepseek", "deepseek-flash", systemPrompt, sourceText, (int)SpendLedger.MaximumJobOutputTokens);

    private void EnsureUnlocked()
    {
        if (!string.Equals(_vault.State, "unlocked", StringComparison.Ordinal))
        {
            throw new ManagedResourceUnavailableException();
        }
    }

    private static void EnsureTrusted(AuthenticatedContext context)
    {
        if (context.Kind != PrincipalKind.Owner)
        {
            throw new InvalidOperationException("Only trusted owner context can execute model job operations.");
        }
    }

    private static void ValidateSourceEnrollment(string sourceResourceId, IManagedResourceStore store)
    {
        if (store is ISourceEnrollmentPersistence persistence)
        {
            SourceEnrollmentState state;
            try
            {
                state = SourceEnrollmentAuthority.ValidateAndNormalizeState(persistence.LoadSourceEnrollmentState());
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or CryptographicException)
            {
                throw new InvalidOperationException($"Source enrollment state is unavailable or corrupt: {ex.Message}", ex);
            }

            var enrollment = SourceEnrollmentAuthority.FindEnrollment(state, sourceResourceId);
            if (enrollment is not null && enrollment.RevokedAtUtc is not null)
            {
                throw new ArgumentException($"The source resource '{sourceResourceId}' is revoked.");
            }
            if (SourceEnrollmentAuthority.FindQuarantine(state, sourceResourceId) is not null)
            {
                throw new ArgumentException($"The source resource '{sourceResourceId}' is quarantined.");
            }
        }
    }
}
