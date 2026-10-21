using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;
using MetaBrain.OwnerUi;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S6-T4 regression walkthrough for explicit bounded multi-level synthesis. The
/// provider is stubbed — the boundary under test is the synthesis scope,
/// grounding, lineage and review behavior, not the model — while every scenario
/// runs the real job service, validators, encrypted store and proposal path.
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S6T4ZoneAlpha = "s6t4-zone-alpha";
    private const string S6T4ZoneBeta = "s6t4-zone-beta";
    private const string S6T4ZoneInbox = "s6t4-zone-inbox";
    private const string S6T4AlphaNote = "s6t4-project-alpha-note";
    private const string S6T4BetaNote = "s6t4-project-beta-note";
    private const string S6T4InboxId = "s6t4-synthesis-inbox";
    private const string S6T4LevelTwoItem = "s6t4-level-two-output";
    private const string S6T4CycleA = "s6t4-cycle-a";
    private const string S6T4CycleB = "s6t4-cycle-b";
    private const string S6T4TransitiveRootA = "s6t4-transitive-root-a";
    private const string S6T4TransitiveRootB = "s6t4-transitive-root-b";
    private const string S6T4TransitiveItem = "s6t4-transitive-level-two";
    private const string S6T4TransitiveRootAText =
        "Transitive root A: the payment writer retries a failed write up to three times under the default policy.";
    private const string S6T4TransitiveRootBText =
        "Transitive root B: the payment writer retries a failed write up to five times under its own wrapper budget.";
    private const string S6T4TransitiveBody =
        "An L2 output that stands on both transitive roots and nothing else.";

    private const string S6T4ReviewZoneAlpha = "s6t4-review-zone-alpha";
    private const string S6T4ReviewZoneBeta = "s6t4-review-zone-beta";
    private const string S6T4ReviewZoneInbox = "s6t4-review-zone-inbox";
    private const string S6T4ReviewAlphaNote = "s6t4-review-alpha-note";
    private const string S6T4ReviewBetaNote = "s6t4-review-beta-note";
    private const string S6T4ReviewInboxId = "s6t4-review-inbox";
    private const string S6T4ReviewLevelTwoItem = "s6t4-review-level-two";
    private const string S6T4ReviewAlphaText =
        "S6-T4 review-surface synthetic Alpha note: the Alpha writer retries a failed write up to three times.";
    private const string S6T4ReviewAlphaRevisedText =
        "S6-T4 review-surface synthetic Alpha note, revision two: the Alpha writer retries a failed write up to four times.";
    private const string S6T4ReviewBetaText =
        "S6-T4 review-surface synthetic Beta note: the Beta writer retries a failed write up to five times.";
    private const string S6T4ReviewLevelTwoBody =
        "S6-T4 review-surface L2 output: both synthetic project notes report a retry budget for the payment writer.";

    private const string S6T4AlphaText =
        "Project Alpha research note. Finding: the alpha pipeline retries writes up to three times.\n" +
        "Limitation: alpha never measured throughput above 100 requests per second.";

    private const string S6T4BetaText =
        "Project Beta research note. Finding: the beta pipeline retries writes up to three times.\n" +
        "Limitation: beta measured throughput above 100 requests per second.";

    private const string S6T4AlphaExcerpt = "the alpha pipeline retries writes up to three times";
    private const string S6T4BetaExcerpt = "the beta pipeline retries writes up to three times";
    private const string S6T4LevelTwoExcerpt = "retry-up-to-three-times behaviour";

    private const string S6T4LevelTwoBody =
        "Both project notes report retry-up-to-three-times behaviour, with different measured throughput limits.";

    private const string S6T4Objective =
        "Compare what the projects report about retry behavior and throughput limits, and keep what stays unmeasured explicit.";

    internal static async Task RunS6T4SynthesisAsync(string connectionsExecutable)
    {
        Console.WriteLine("RUN S6-T4 explicit bounded multi-level synthesis walkthrough");
        using var fixture = SmokeFixture.CreateUninitialized();
        var passphrase = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

        var store = new EncryptedVaultStore(Path.Combine(fixture.OutputDirectory, "s6t4-vault"));
        var lifecycle = new VaultLifecycle(store);
        lifecycle.Provision(passphrase, out _);
        lifecycle.Unlock(passphrase, false);

        try
        {
            using var operation = lifecycle.TryBeginOperation()
                ?? throw new InvalidOperationException("The S6-T4 fixture vault is not unlocked.");
            var enrollment = new SourceEnrollmentAuthority();
            SeedS6T4Fixture(operation, enrollment, fixture.OutputDirectory, DateTimeOffset.UtcNow);

            RunS6T4ValidatorChecks();
            RunS6T4LegacyJobRecordCheck();
            RunS6T4ScopeChecks(lifecycle, operation);
            await RunS6T4JobChecksAsync(lifecycle, operation, fixture.OutputDirectory).ConfigureAwait(false);
            RunS6T4TransitiveReviewChecks(operation, fixture.OutputDirectory);
        }
        finally
        {
            await lifecycle.LockAsync().ConfigureAwait(false);
        }

        await RunS6T4ReviewSurfaceAsync(connectionsExecutable).ConfigureAwait(false);
        Console.WriteLine("PASS S6-T4 explicit bounded multi-level synthesis walkthrough complete");
    }

    private static void SeedS6T4Fixture(
        IVaultOperation operation,
        SourceEnrollmentAuthority enrollment,
        string outputDirectory,
        DateTimeOffset now)
    {
        operation.WriteContent(
            ModelJobService.ProviderConfigResourceId,
            ModelJobService.SystemZoneId,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new ProviderConfig("deepseek", "deepseek-flash", "https://api.deepseek.com", "test-key"))),
            expectedRevision: 0);

        WriteS6T4Source(operation, S6T4AlphaNote, S6T4ZoneAlpha, "S6-T4 Project Alpha note", S6T4AlphaText, outputDirectory, now, expectedRevision: 0);
        WriteS6T4Source(operation, S6T4BetaNote, S6T4ZoneBeta, "S6-T4 Project Beta note", S6T4BetaText, outputDirectory, now, expectedRevision: 0);
        EnrollS6T4Source(enrollment, operation, S6T4AlphaNote, S6T4ZoneAlpha, outputDirectory);
        EnrollS6T4Source(enrollment, operation, S6T4BetaNote, S6T4ZoneBeta, outputDirectory);

        var inbox = new StoredMemoryEnvelope(
            SchemaVersion: MemoryEnvelopeCodec.CurrentSchemaVersion,
            Id: S6T4InboxId,
            ZoneId: S6T4ZoneInbox,
            Kind: MemoryKind.Observation,
            Author: SpeakerRole.User,
            Executor: ExecutorKind.Owner,
            CreationMethod: CreationMethod.DirectWrite,
            Evidence: EvidenceLevel.ReportedSource,
            Level: AbstractionLevel.L0,
            Title: "S6-T4 synthesis inbox",
            Body: "Destination inbox for pending synthesis proposals.",
            Sources: Array.Empty<MemorySourceReference>(),
            OccurredAtUtc: null,
            RecordedAtUtc: now,
            Applicability: null);
        operation.WriteContent(S6T4InboxId, S6T4ZoneInbox, MemoryEnvelopeCodec.EncodeStored(inbox), expectedRevision: 0);

        // An already existing L2 synthesis output of both project notes: the
        // lower-level result a higher-level synthesis must stand on.
        var levelTwo = new StoredMemoryEnvelope(
            SchemaVersion: MemoryEnvelopeCodec.CurrentSchemaVersion,
            Id: S6T4LevelTwoItem,
            ZoneId: S6T4ZoneInbox,
            Kind: MemoryKind.Synthesis,
            Author: SpeakerRole.Assistant,
            Executor: ExecutorKind.StructuringJob,
            CreationMethod: CreationMethod.Synthesis,
            Evidence: EvidenceLevel.ReportedSource,
            Level: AbstractionLevel.L2,
            Title: "S6-T4 L2 synthesis of both project notes",
            Body: S6T4LevelTwoBody,
            Sources: new[]
            {
                new MemorySourceReference(S6T4AlphaNote, null, SourceAvailability.Available, 1, null),
                new MemorySourceReference(S6T4BetaNote, null, SourceAvailability.Available, 1, null)
            },
            OccurredAtUtc: null,
            RecordedAtUtc: now,
            Applicability: null);
        operation.WriteContent(S6T4LevelTwoItem, S6T4ZoneInbox, MemoryEnvelopeCodec.EncodeStored(levelTwo), expectedRevision: 0);

        // A pair whose citations point at each other: an inconsistent lineage a
        // bounded synthesis must refuse instead of propagating.
        WriteS6T4MemoryItem(operation, S6T4CycleA, "S6-T4 cycle A", "Cycle item A.", S6T4CycleB, now);
        WriteS6T4MemoryItem(operation, S6T4CycleB, "S6-T4 cycle B", "Cycle item B.", S6T4CycleA, now);

        // A second, independent derivation: two enrolled roots and one
        // intermediate L2 item standing on both. A pending proposal that cites
        // only that item is the transitive-staleness fixture — nothing it cites
        // directly changes while a root underneath the item does.
        WriteS6T4Source(operation, S6T4TransitiveRootA, S6T4ZoneAlpha, "S6-T4 transitive root A", S6T4TransitiveRootAText, outputDirectory, now, expectedRevision: 0);
        WriteS6T4Source(operation, S6T4TransitiveRootB, S6T4ZoneBeta, "S6-T4 transitive root B", S6T4TransitiveRootBText, outputDirectory, now, expectedRevision: 0);
        EnrollS6T4Source(enrollment, operation, S6T4TransitiveRootA, S6T4ZoneAlpha, outputDirectory);
        EnrollS6T4Source(enrollment, operation, S6T4TransitiveRootB, S6T4ZoneBeta, outputDirectory);
        var transitive = new StoredMemoryEnvelope(
            SchemaVersion: MemoryEnvelopeCodec.CurrentSchemaVersion,
            Id: S6T4TransitiveItem,
            ZoneId: S6T4ZoneInbox,
            Kind: MemoryKind.Synthesis,
            Author: SpeakerRole.Assistant,
            Executor: ExecutorKind.StructuringJob,
            CreationMethod: CreationMethod.Synthesis,
            Evidence: EvidenceLevel.ReportedSource,
            Level: AbstractionLevel.L2,
            Title: "S6-T4 transitive intermediate L2",
            Body: S6T4TransitiveBody,
            Sources: new[]
            {
                new MemorySourceReference(S6T4TransitiveRootA, null, SourceAvailability.Available, 1, null),
                new MemorySourceReference(S6T4TransitiveRootB, null, SourceAvailability.Available, 1, null)
            },
            OccurredAtUtc: null,
            RecordedAtUtc: now,
            Applicability: null);
        operation.WriteContent(S6T4TransitiveItem, S6T4ZoneInbox, MemoryEnvelopeCodec.EncodeStored(transitive), expectedRevision: 0);
    }

    private static void WriteS6T4Source(
        IVaultOperation operation,
        string resourceId,
        string zoneId,
        string title,
        string text,
        string outputDirectory,
        DateTimeOffset now,
        long expectedRevision)
    {
        var source = new StoredRegisteredSource(
            SchemaVersion: RegisteredSourceCodec.CurrentSchemaVersion,
            Id: resourceId,
            ZoneId: zoneId,
            Executor: ExecutorKind.Owner,
            CreationMethod: CreationMethod.DirectWrite,
            Title: title,
            Locator: Path.Combine(outputDirectory, resourceId + ".note"),
            Version: "1",
            Availability: SourceAvailability.Available,
            CapturedAtUtc: now,
            RecordedAtUtc: now,
            Author: SpeakerRole.External,
            Text: text);
        operation.WriteContent(resourceId, zoneId, RegisteredSourceCodec.EncodeStored(source), expectedRevision);
    }

    private static void WriteS6T4MemoryItem(
        IVaultOperation operation,
        string resourceId,
        string title,
        string body,
        string citedSourceId,
        DateTimeOffset now)
    {
        var item = new StoredMemoryEnvelope(
            SchemaVersion: MemoryEnvelopeCodec.CurrentSchemaVersion,
            Id: resourceId,
            ZoneId: S6T4ZoneInbox,
            Kind: MemoryKind.Claim,
            Author: SpeakerRole.Assistant,
            Executor: ExecutorKind.Owner,
            CreationMethod: CreationMethod.Paraphrase,
            Evidence: EvidenceLevel.ReportedSource,
            Level: AbstractionLevel.L1,
            Title: title,
            Body: body,
            Sources: new[] { new MemorySourceReference(citedSourceId, null, SourceAvailability.Available, 1, null) },
            OccurredAtUtc: null,
            RecordedAtUtc: now,
            Applicability: null);
        operation.WriteContent(resourceId, S6T4ZoneInbox, MemoryEnvelopeCodec.EncodeStored(item), expectedRevision: 0);
    }

    private static void EnrollS6T4Source(
        SourceEnrollmentAuthority enrollment,
        IVaultOperation operation,
        string sourceId,
        string zoneId,
        string outputDirectory)
    {
        var digest = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("s6t4-anchor-" + sourceId)));
        enrollment.Enroll(
            AuthenticatedContext.ForOwner(),
            sourceId,
            zoneId,
            Path.Combine(outputDirectory, sourceId + ".jsonl"),
            "research.note",
            1,
            128,
            digest,
            operation);
    }

    /// <summary>
    /// The synthesis output boundary: every item must carry an item-local
    /// grounding set naming selected inputs at their pinned revisions with their
    /// verbatim text, and an item that spans inputs must ground in two of them.
    /// </summary>
    private static void RunS6T4ValidatorChecks()
    {
        var inputs = new[]
        {
            new JobInputText(S6T4AlphaNote, 1, S6T4AlphaText, IsSourceText: true),
            new JobInputText(S6T4BetaNote, 1, S6T4BetaText, IsSourceText: true)
        };

        static void RequireRefusal(string rawOutput, JobInputText[] candidateInputs, string expectedCode, string scenario)
        {
            var valid = JobOutputValidator.TryValidateSynthesis(rawOutput, candidateInputs, out var output, out var code, out var message);
            Require(!valid && output is null && string.Equals(code, expectedCode, StringComparison.Ordinal),
                scenario + " did not fail closed with " + expectedCode + " (valid=" + valid + ", code=" + code + ", message=" + message + ").");
        }

        static string Grounding(string inputId, long revision, string excerpt) =>
            "{\"inputId\":\"" + inputId + "\",\"inputRevision\":" + revision + ",\"excerpt\":\"" + excerpt + "\"}";

        static string Item(string kind, string grounding, bool crossSource, string occurredAtSourceText = "null") =>
            "{\"summary\":\"S6-T4 validator fixture\",\"items\":[{\"kind\":\"" + kind + "\",\"title\":\"S6-T4 validator item\"," +
            "\"body\":\"Grounded validator fixture body.\",\"crossSource\":" + (crossSource ? "true" : "false") +
            ",\"grounding\":[" + grounding + "],\"occurredAtSourceText\":" + occurredAtSourceText + "}]}";

        var grounded = Item(
            "synthesis",
            Grounding(S6T4AlphaNote, 1, S6T4AlphaExcerpt) + "," + Grounding(S6T4BetaNote, 1, S6T4BetaExcerpt),
            crossSource: true);
        Require(JobOutputValidator.TryValidateSynthesis(grounded, inputs, out var validated, out var validationCode, out var validationMessage) &&
                validated is { Items.Length: 1 } && validationCode is null,
            "A grounded cross-source synthesis item was refused (code=" + validationCode + ", message=" + validationMessage + ").");
        Console.WriteLine("PASS synthesis grounding: a cross-source item grounded in two selected inputs validates");

        RequireRefusal(
            Item("synthesis", Grounding(S6T4AlphaNote, 1, S6T4AlphaExcerpt), crossSource: true),
            inputs,
            "grounding_failure",
            "An item declared to span inputs while grounding in one input");

        RequireRefusal(
            Item("synthesis", Grounding(S6T4AlphaNote, 1, "the alpha pipeline retries writes up to five times"), crossSource: false),
            inputs,
            "grounding_failure",
            "A paraphrased excerpt");

        RequireRefusal(
            Item("synthesis", Grounding("s6t4-not-selected", 1, S6T4AlphaExcerpt), crossSource: false),
            inputs,
            "ungrounded_input",
            "A grounding entry naming an input the owner did not select");

        RequireRefusal(
            Item("synthesis", Grounding(S6T4AlphaNote, 2, S6T4AlphaExcerpt), crossSource: false),
            inputs,
            "ungrounded_input",
            "A grounding entry pinned to a revision the job did not select");

        RequireRefusal(
            Item("synthesis", Grounding(S6T4AlphaNote, 1, S6T4AlphaExcerpt), crossSource: false, occurredAtSourceText: "\"2026-10-18T00:00:00Z\""),
            inputs,
            "timestamp_not_applicable",
            "A synthesis item claiming a source occurrence time");

        RequireRefusal(
            Item(
                "synthesis",
                Grounding(S6T4AlphaNote, 1, S6T4AlphaExcerpt) + "," + Grounding(S6T4AlphaNote, 1, S6T4AlphaExcerpt),
                crossSource: false),
            inputs,
            "schema_violation",
            "A repeated grounding excerpt counted as a second source");

        RequireRefusal(
            Item("result", Grounding(S6T4AlphaNote, 1, S6T4AlphaExcerpt), crossSource: false),
            inputs,
            "schema_violation",
            "A synthesis item declaring a single-input structuring kind");
        Console.WriteLine("PASS synthesis grounding boundaries: cross-source, verbatim text, selection, pinned revision, time and kind are enforced");
    }

    /// <summary>
    /// A job record written before the scope carried an explicit input set is
    /// read back with its single recorded source folded into that input set, so
    /// the scope shape change strands no existing encrypted job record.
    /// </summary>
    private static void RunS6T4LegacyJobRecordCheck()
    {
        var legacy =
            "{\"schemaVersion\":1,\"id\":\"job-s6t4-legacy\",\"zoneId\":\"s6t4-zone-inbox\",\"kind\":\"structuring\",\"state\":\"succeeded\"," +
            "\"scope\":{\"sourceResourceId\":\"" + S6T4AlphaNote + "\",\"sourceRevision\":1,\"destinationZoneId\":\"s6t4-zone-inbox\"," +
            "\"destinationResourceId\":\"" + S6T4InboxId + "\",\"destinationRevision\":1,\"provider\":\"deepseek\",\"model\":\"deepseek-flash\"," +
            "\"maximumCostUsd\":0.25,\"maxInputTokens\":10000,\"maxOutputTokens\":2000,\"profile\":\"research-note\"}," +
            "\"usage\":{\"inputTokens\":10,\"outputTokens\":20,\"costUsd\":0.0001},\"failure\":null,\"outputProposalIds\":[]," +
            "\"createdAtUtc\":\"2026-10-18T00:00:00+00:00\",\"startedAtUtc\":null,\"completedAtUtc\":null}";

        var decoded = JobCodec.DecodeStored(Encoding.UTF8.GetBytes(legacy));
        Require(decoded.SchemaVersion == JobCodec.CurrentSchemaVersion &&
                decoded.Scope.Inputs.Length == 1 &&
                string.Equals(decoded.Scope.Inputs[0].ResourceId, S6T4AlphaNote, StringComparison.Ordinal) &&
                decoded.Scope.Inputs[0].Revision == 1 &&
                string.Equals(decoded.Scope.Profile, StructuringProfiles.ResearchNote, StringComparison.Ordinal) &&
                decoded.Usage is { InputTokens: 10, OutputTokens: 20 },
            "A pre-input-set job record was not migrated into the current scope shape on read.");
        Console.WriteLine("PASS job record migration: a single-source job record stays readable through the explicit input set");
    }

    /// <summary>
    /// The explicit synthesis scope: several selected inputs, an owner-stated
    /// objective, an owner-requested level, a lower-level synthesis input for
    /// L3+, and an acyclic lineage.
    /// </summary>
    private static void RunS6T4ScopeChecks(IVaultLifecycle lifecycle, IVaultOperation operation)
    {
        var jobs = new ModelJobService(lifecycle, new SynthesisStubAdapter());

        static void RequireCreateRefusal(Func<JobRecord> create, string expectedCode, string scenario)
        {
            try
            {
                _ = create();
                throw new InvalidOperationException(scenario + " did not fail closed.");
            }
            catch (ServiceRefusalException ex) when (string.Equals(ex.Code, expectedCode, StringComparison.Ordinal))
            {
            }
        }

        JobRecord Create(JobInputReference[] inputs, string? objective, string? level) =>
            jobs.CreateJob(
                AuthenticatedContext.ForOwner(),
                inputs,
                S6T4ZoneInbox,
                S6T4InboxId,
                1,
                "deepseek",
                "deepseek-flash",
                0.25m,
                operation,
                profile: StructuringProfiles.Synthesis,
                objective: objective,
                targetLevel: level);

        var bothNotes = new[] { new JobInputReference(S6T4AlphaNote, 1), new JobInputReference(S6T4BetaNote, 1) };
        RequireCreateRefusal(
            () => Create(new[] { new JobInputReference(S6T4AlphaNote, 1) }, S6T4Objective, SynthesisLevels.L2),
            "synthesis_requires_multiple_inputs",
            "A one-input synthesis job");
        RequireCreateRefusal(
            () => Create(bothNotes, null, SynthesisLevels.L2),
            "objective_required",
            "A synthesis job without an owner objective");
        RequireCreateRefusal(
            () => Create(bothNotes, S6T4Objective, "l1"),
            "synthesis_level_required",
            "A synthesis job requesting a level below L2");
        RequireCreateRefusal(
            () => Create(bothNotes, S6T4Objective, SynthesisLevels.L3Plus),
            "synthesis_level_ungrounded",
            "An L3+ synthesis grounded only in raw sources");
        RequireCreateRefusal(
            () => Create(new[] { new JobInputReference(S6T4CycleA, 1), new JobInputReference(S6T4CycleB, 1) }, S6T4Objective, SynthesisLevels.L2),
            "lineage_cycle",
            "A synthesis whose selected input lineage is cyclic");
        RequireCreateRefusal(
            () => Create(new[] { new JobInputReference(S6T4AlphaNote, 1), new JobInputReference(S6T4BetaNote, 1), new JobInputReference(S6T4AlphaNote, 1) }, S6T4Objective, SynthesisLevels.L2),
            "invalid_job_inputs",
            "A synthesis selecting the same input twice");
        Console.WriteLine("PASS synthesis scope: multiple distinct inputs, owner objective, L2/L3+ level, level grounding and acyclic lineage are enforced");
    }

    /// <summary>
    /// The committed output of a synthesis job: derived authorship, the owner's
    /// level, reported-source evidence, one citation per grounded input at its
    /// pinned revision, a source that moves mid-job blocking the commit, and
    /// stale marking that never rewrites the output.
    /// </summary>
    private static async Task RunS6T4JobChecksAsync(IVaultLifecycle lifecycle, IVaultOperation operation, string outputDirectory)
    {
        var jobs = new ModelJobService(lifecycle, new SynthesisStubAdapter());
        var bothNotes = new[] { new JobInputReference(S6T4AlphaNote, 1), new JobInputReference(S6T4BetaNote, 1) };

        var l2Job = jobs.CreateJob(
            AuthenticatedContext.ForOwner(),
            bothNotes,
            S6T4ZoneInbox,
            S6T4InboxId,
            1,
            "deepseek",
            "deepseek-flash",
            0.25m,
            operation,
            profile: StructuringProfiles.Synthesis,
            objective: S6T4Objective,
            targetLevel: SynthesisLevels.L2);
        Require(l2Job.State == JobState.Pending &&
                l2Job.Scope.Inputs.Length == 2 &&
                l2Job.Scope.TargetLevel == AbstractionLevel.L2 &&
                !string.IsNullOrWhiteSpace(l2Job.Scope.Objective),
            "The L2 synthesis scope did not record the selected inputs, the owner objective and the requested level.");

        var l2Result = await jobs.StartJobAsync(AuthenticatedContext.ForOwner(), l2Job.Id, operation).ConfigureAwait(false);
        Require(l2Result.State == JobState.Succeeded && l2Result.OutputProposalIds is { Length: 1 },
            "The L2 synthesis job did not commit exactly one proposal (state=" + l2Result.State + ", failure=" + l2Result.Failure?.Code + ": " + l2Result.Failure?.Message + ").");

        var proposals = new ProposalService();
        var l2Proposal = proposals.Read(l2Result.OutputProposalIds![0], null, operation);
        Require(l2Proposal.Item.Kind == MemoryKind.Synthesis &&
                l2Proposal.Item.CreationMethod == CreationMethod.Synthesis &&
                l2Proposal.Item.Level == AbstractionLevel.L2 &&
                l2Proposal.Item.Evidence == EvidenceLevel.ReportedSource &&
                l2Proposal.Item.Author == SpeakerRole.Assistant &&
                l2Proposal.Item.OccurredAtUtc is null &&
                !l2Proposal.ClaimedUserConfirmed,
            "The committed synthesis proposal did not keep derived authorship, synthesis creation, the owner's level and reported-source evidence (a level never upgrades evidence).");
        var l2Sources = l2Proposal.Item.Sources ?? Array.Empty<MemorySourceReference>();
        Require(l2Sources.Length == 2 &&
                l2Sources.All(citation => citation.SourceRevision == 1 && citation.Span is not null) &&
                l2Sources
                    .Select(citation => citation.SourceId)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .SequenceEqual(new[] { S6T4AlphaNote, S6T4BetaNote }, StringComparer.Ordinal),
            "The committed synthesis proposal did not cite both selected inputs at their pinned revisions with spans.");
        Console.WriteLine("PASS synthesis commit: the L2 output cites both selected inputs at pinned revisions with reported-source evidence and the owner's level");

        var l3Job = jobs.CreateJob(
            AuthenticatedContext.ForOwner(),
            new[] { new JobInputReference(S6T4LevelTwoItem, 1), new JobInputReference(S6T4BetaNote, 1) },
            S6T4ZoneInbox,
            S6T4InboxId,
            1,
            "deepseek",
            "deepseek-flash",
            0.25m,
            operation,
            profile: StructuringProfiles.Synthesis,
            objective: S6T4Objective,
            targetLevel: SynthesisLevels.L3Plus);
        var l3Result = await jobs.StartJobAsync(AuthenticatedContext.ForOwner(), l3Job.Id, operation).ConfigureAwait(false);
        Require(l3Result.State == JobState.Succeeded && l3Result.OutputProposalIds is { Length: 1 },
            "The L3 synthesis over an existing L2 output did not commit exactly one proposal (state=" + l3Result.State + ", failure=" + l3Result.Failure?.Code + ": " + l3Result.Failure?.Message + ").");

        var l3Proposal = proposals.Read(l3Result.OutputProposalIds![0], null, operation);
        var l3Sources = l3Proposal.Item.Sources ?? Array.Empty<MemorySourceReference>();
        Require(l3Proposal.Item.Level == AbstractionLevel.L3Plus &&
                l3Proposal.Item.CreationMethod == CreationMethod.Synthesis &&
                l3Proposal.Item.Evidence == EvidenceLevel.ReportedSource &&
                l3Sources.Length == 2,
            "The L3 synthesis proposal did not keep the owner's level, synthesis creation, reported-source evidence and both citations.");
        var itemCitation = l3Sources.FirstOrDefault(citation => string.Equals(citation.SourceId, S6T4LevelTwoItem, StringComparison.Ordinal));
        var sourceCitation = l3Sources.FirstOrDefault(citation => string.Equals(citation.SourceId, S6T4BetaNote, StringComparison.Ordinal));
        Require(itemCitation is { SourceRevision: 1, Span: null } && sourceCitation is { SourceRevision: 1, Span: not null },
            "The L3 lineage did not pin the lower-level item without a span and the source with a span.");

        var levelTwoStored = MemoryEnvelopeCodec.DecodeStored(operation.ReadContent(S6T4LevelTwoItem, 1));
        Require(levelTwoStored.Sources.Length == 2 &&
                levelTwoStored.Sources.All(citation => citation.SourceRevision == 1) &&
                levelTwoStored.Sources
                    .Select(citation => citation.SourceId)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .SequenceEqual(new[] { S6T4AlphaNote, S6T4BetaNote }, StringComparer.Ordinal),
            "The L3 output's lineage did not trace through the L2 output to both permitted project notes.");
        Console.WriteLine("PASS multi-level lineage: an L3 output stands on an L2 output and a source, both pinned, tracing to the permitted roots");

        // A selected input that moves while the provider answers blocks the
        // commit: no proposal, the spend stays recorded, sources stay intact.
        var revising = new ModelJobService(
            lifecycle,
            new RevisioningSynthesisStubAdapter(() => WriteS6T4Source(
                operation,
                S6T4BetaNote,
                S6T4ZoneBeta,
                "S6-T4 Project Beta note (revised)",
                S6T4BetaText + "\nRevision 2 marker.",
                outputDirectory,
                DateTimeOffset.UtcNow,
                expectedRevision: 1)));
        var blockedJob = revising.CreateJob(
            AuthenticatedContext.ForOwner(),
            bothNotes,
            S6T4ZoneInbox,
            S6T4InboxId,
            1,
            "deepseek",
            "deepseek-flash",
            0.25m,
            operation,
            profile: StructuringProfiles.Synthesis,
            objective: S6T4Objective,
            targetLevel: SynthesisLevels.L2);
        var blockedResult = await revising.StartJobAsync(AuthenticatedContext.ForOwner(), blockedJob.Id, operation).ConfigureAwait(false);
        Require(blockedResult.State == JobState.Failed &&
                string.Equals(blockedResult.Failure?.Code, "source_unavailable", StringComparison.Ordinal) &&
                (blockedResult.Failure?.Message.Contains("unavailable before commit", StringComparison.Ordinal) == true ||
                 blockedResult.Failure?.Message.Contains("revoked before commit", StringComparison.Ordinal) == true),
            "A selected input that moved mid-job did not block the commit (state=" + blockedResult.State + ", failure=" + blockedResult.Failure?.Code + ": " + blockedResult.Failure?.Message + ").");
        Require(blockedResult.OutputProposalIds is null or { Length: 0 },
            "A synthesis proposal was committed although one selected input moved while the provider was answering.");

        var ledger = jobs.LoadSpendLedger(operation);
        Require(ledger.Entries.Any(entry => string.Equals(entry.JobId, blockedJob.Id, StringComparison.Ordinal) && !entry.IsReservation),
            "The billable spend of the blocked synthesis job was not recorded.");
        var alphaAfterBlock = RegisteredSourceCodec.DecodeStored(operation.ReadContent(S6T4AlphaNote, 1));
        Require(string.Equals(alphaAfterBlock.Text, S6T4AlphaText, StringComparison.Ordinal),
            "A selected input's preserved text was lost or corrupted by the blocked job.");
        Console.WriteLine("PASS mid-job source change: the commit was blocked, no proposal was written, spend stayed recorded and the other input text was preserved");

        var staleReview = ProposalService.EvaluateReview(l2Proposal, operation);
        Require(staleReview.NeedsReview &&
                staleReview.Reasons.HasFlag(ProposalReviewReason.CitedSourceRevisionChanged) &&
                staleReview.StaleSourceIds.Contains(S6T4BetaNote, StringComparer.Ordinal),
            "A moved cited source did not mark the committed synthesis output as needing review (needsReview=" + staleReview.NeedsReview + ", reasons=" + staleReview.Reasons + ").");

        var reread = proposals.Read(l2Proposal.Id, null, operation);
        Require(reread.Revision == l2Proposal.Revision &&
                string.Equals(reread.PayloadHash, l2Proposal.PayloadHash, StringComparison.Ordinal) &&
                string.Equals(reread.Item.Body, l2Proposal.Item.Body, StringComparison.Ordinal),
            "A cited source change rewrote the committed proposal instead of marking it for review.");

        new SourceEnrollmentAuthority().Revoke(AuthenticatedContext.ForOwner(), S6T4AlphaNote, operation);
        var revokedReview = ProposalService.EvaluateReview(proposals.Read(l2Proposal.Id, null, operation), operation);
        Require(revokedReview.Reasons.HasFlag(ProposalReviewReason.CitedSourceUnavailable) &&
                revokedReview.StaleSourceIds.Contains(S6T4AlphaNote, StringComparer.Ordinal),
            "A revoked cited source did not mark the committed synthesis output as needing review (reasons=" + revokedReview.Reasons + ").");
        Console.WriteLine("PASS stale marking: a moved or revoked cited source marks the output as needing review without rewriting it");
    }

    /// <summary>
    /// Transitive citation staleness: a pending proposal whose only direct
    /// citation is an unchanged intermediate item must still be marked for
    /// review when a root that item stands on is revoked or moves. The proposal
    /// record, the intermediate item and its own citations stay exactly as
    /// recorded — only the served review state changes.
    /// </summary>
    private static void RunS6T4TransitiveReviewChecks(IVaultOperation operation, string outputDirectory)
    {
        var proposals = new ProposalService();
        var draft = Encoding.UTF8.GetBytes(
            "{\"kind\":\"create\",\"idempotencyKey\":\"s6t4-transitive-review-1\",\"destinationResourceId\":\"" + S6T4InboxId + "\"," +
            "\"destinationRevision\":1,\"item\":{\"kind\":\"synthesis\",\"author\":\"assistant\",\"creationMethod\":\"synthesis\"," +
            "\"evidence\":\"reportedSource\",\"level\":\"l3Plus\",\"title\":\"S6-T4 transitive review item\"," +
            "\"body\":\"An L3+ item that stands on the intermediate L2 item alone.\",\"sources\":[{\"sourceId\":\"" + S6T4TransitiveItem +
            "\",\"locator\":null,\"availability\":\"available\",\"sourceRevision\":1,\"span\":null}]," +
            "\"occurredAtUtc\":null,\"recordedAtUtc\":\"2026-10-18T00:00:00+00:00\",\"applicability\":null},\"userConfirmed\":false}");
        Proposal proposal;
        try
        {
            proposal = proposals.CreateAgentProposal(AuthenticatedContext.ForOwner(), "s6t4-transitive-grant", draft, operation);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft);
        }

        var baseline = ProposalService.EvaluateReview(proposal, operation);
        Require(!baseline.NeedsReview && baseline.Reasons == ProposalReviewReason.None,
            "A proposal whose only direct citation is unchanged over current roots was already reported as needing review (reasons=" + baseline.Reasons + ").");

        // The root is revoked while the cited item stays exactly at the revision
        // the proposal pins: only that item's own lineage changed.
        new SourceEnrollmentAuthority().Revoke(AuthenticatedContext.ForOwner(), S6T4TransitiveRootB, operation);
        var revoked = ProposalService.EvaluateReview(proposals.Read(proposal.Id, null, operation), operation);
        Require(revoked.NeedsReview &&
                revoked.Reasons == ProposalReviewReason.CitedSourceLineageChanged &&
                revoked.StaleSourceIds.Length == 1 &&
                revoked.StaleSourceIds.Contains(S6T4TransitiveRootB, StringComparer.Ordinal),
            "A revoked root beneath an unchanged cited item did not mark the proposal for review transitively (reasons=" + revoked.Reasons + ", stale=" + string.Join(",", revoked.StaleSourceIds) + ").");
        RequireS6T4IntermediateUnchanged(operation);
        Console.WriteLine("PASS transitive staleness: a revoked root under an unchanged cited item marks the proposal for review");

        // The other root moves: the proposal's own pin and the cited item are
        // still untouched, and the served state names the ancestor that moved.
        WriteS6T4Source(operation, S6T4TransitiveRootA, S6T4ZoneAlpha, "S6-T4 transitive root A (revised)",
            S6T4TransitiveRootAText + "\nRevision 2 marker.", outputDirectory, DateTimeOffset.UtcNow, expectedRevision: 1);
        var moved = ProposalService.EvaluateReview(proposals.Read(proposal.Id, null, operation), operation);
        Require(moved.NeedsReview &&
                moved.Reasons == ProposalReviewReason.CitedSourceLineageChanged &&
                moved.StaleSourceIds.Contains(S6T4TransitiveRootA, StringComparer.Ordinal) &&
                moved.StaleSourceIds.Contains(S6T4TransitiveRootB, StringComparer.Ordinal),
            "A moved root beneath an unchanged cited item did not mark the proposal for review transitively (reasons=" + moved.Reasons + ", stale=" + string.Join(",", moved.StaleSourceIds) + ").");
        RequireS6T4IntermediateUnchanged(operation);

        var reread = proposals.Read(proposal.Id, null, operation);
        Require(reread.Revision == proposal.Revision &&
                string.Equals(reread.PayloadHash, proposal.PayloadHash, StringComparison.Ordinal) &&
                string.Equals(reread.Item.Body, proposal.Item.Body, StringComparison.Ordinal) &&
                reread.Item.Sources is { Length: 1 } rereadSources &&
                rereadSources[0].SourceRevision == 1,
            "A transitive source change rewrote the proposal or refreshed its citations instead of marking it for review.");
        Console.WriteLine("PASS transitive staleness: a moved root under an unchanged cited item marks the proposal for review without rewriting it");
    }

    /// <summary>The cited intermediate item keeps its revision, its body and its own citations.</summary>
    private static void RequireS6T4IntermediateUnchanged(IVaultOperation operation)
    {
        var stored = MemoryEnvelopeCodec.DecodeStored(operation.ReadContent(S6T4TransitiveItem, 1));
        Require(string.Equals(stored.Body, S6T4TransitiveBody, StringComparison.Ordinal) &&
                stored.Sources.Length == 2 &&
                stored.Sources.All(citation => citation.SourceRevision == 1),
            "A transitive root change rewrote the cited intermediate item instead of only marking the proposal for review.");
    }

    /// <summary>
    /// The real owner service and the real console session: a pending proposal
    /// that cites a memory item is reviewed beside that item's exact pinned
    /// revision text, and a root under the cited item moving is served as a
    /// transitive review need. Nothing here is a mock: the vault is provisioned
    /// and unlocked through the owner CLI, the intermediate item and the pending
    /// proposal go through the real service and the real agent channel under a
    /// real owner-approved grant, and every pane value comes from the console
    /// session the WPF window drives over the owner pipe.
    /// </summary>
    internal static async Task RunS6T4ReviewSurfaceAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S6-T4 review-surface smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S6-T4 review surface: the cited memory item beside the proposal, and transitive staleness through the real service and console session");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        var passphrase = "";
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "Provision did not create the S6-T4 review-surface vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");

            var now = DateTimeOffset.UtcNow.AddMinutes(-5);
            await WriteS6T4ReviewSourceAsync(connectionsExecutable, fixture, S6T4ReviewAlphaNote, S6T4ReviewZoneAlpha, S6T4ReviewAlphaText, now).ConfigureAwait(false);
            await WriteS6T4ReviewSourceAsync(connectionsExecutable, fixture, S6T4ReviewBetaNote, S6T4ReviewZoneBeta, S6T4ReviewBetaText, now).ConfigureAwait(false);
            await WriteS6T4ItemAsync(connectionsExecutable, fixture, S6T4ReviewInboxId, S6T4ReviewZoneInbox,
                S6T4ItemDraftJson("observation", "user", "directWrite", "unverified", "l0",
                    "S6-T4 review-surface inbox", "The destination inbox for the pending review-surface proposal.", now)).ConfigureAwait(false);
            await WriteS6T4ItemAsync(connectionsExecutable, fixture, S6T4ReviewLevelTwoItem, S6T4ReviewZoneInbox,
                S6T4ItemDraftJson("synthesis", "assistant", "synthesis", "reportedSource", "l2",
                    "S6-T4 review-surface L2 output", S6T4ReviewLevelTwoBody, now,
                    new object[]
                    {
                        new { sourceId = S6T4ReviewAlphaNote, locator = (string?)null, availability = "available", sourceRevision = 1L, span = (object?)null },
                        new { sourceId = S6T4ReviewBetaNote, locator = (string?)null, availability = "available", sourceRevision = 1L, span = (object?)null }
                    })).ConfigureAwait(false);
            Console.WriteLine("PASS review surface: the synthetic notes, the inbox and the L2 item are stored through the real owner CLI");

            var grant = await IssueOwnerGrantAsync(connectionsExecutable, fixture.ControlPipe, fixture.AgentPipe, fixture.OutputDirectory,
                "s6t4-review", "proposal.create", new[] { S6T4ReviewLevelTwoItem }, new[] { S6T4ReviewInboxId }).ConfigureAwait(false);
            var draftPath = Path.Combine(fixture.OutputDirectory, "s6t4-review-proposal.json");
            await File.WriteAllTextAsync(draftPath, S6T4ProposalDraftJson(now), Encoding.UTF8).ConfigureAwait(false);
            var proposed = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-propose", "--agent-pipe", fixture.AgentPipe,
                "--session-file", grant.SessionFile, "--input-file", draftPath).ConfigureAwait(false);
            Require(proposed.ExitCode == 0 && proposed.StandardOutput.Contains("level=l3Plus", StringComparison.Ordinal) &&
                    proposed.StandardOutput.Contains("reference id=" + S6T4ReviewLevelTwoItem + " revision=1", StringComparison.Ordinal),
                "The pending review-surface proposal was not stored as proposed (exit=" + proposed.ExitCode + "; stdout=" + proposed.StandardOutput.Trim() + "; stderr=" + proposed.StandardError.Trim() + ").");
            var proposalId = S4T5ProposalId(proposed.StandardOutput);
            File.Delete(draftPath);

            var session = new OwnerConsoleSession(fixture.ControlPipe);
            await session.AttachAsync().ConfigureAwait(false);
            Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
                "The console session did not report the real unlocked service (" + session.ReportedVaultState + ").");

            await session.OpenReviewProposalAsync(new OwnerProposalIdentity(proposalId, 1, S6T4ReviewZoneInbox)).ConfigureAwait(false);
            Require(session.ReviewProposal is { Revision: 1 } opened && string.Equals(opened.Id, proposalId, StringComparison.Ordinal),
                "The console session did not open the pending proposal the service holds (" + session.ReviewMessage + ").");
            Require(session.ReviewCitedItem is { } cited &&
                    string.Equals(cited.Id, S6T4ReviewLevelTwoItem, StringComparison.Ordinal) &&
                    cited.Revision == 1 &&
                    string.Equals(cited.Body, S6T4ReviewLevelTwoBody, StringComparison.Ordinal) &&
                    string.Equals(cited.Level, "l2", StringComparison.Ordinal) &&
                    string.Equals(session.ReviewSourceText, S6T4ReviewLevelTwoBody, StringComparison.Ordinal) &&
                    session.ReviewSource is null,
                "The review pane did not serve the cited memory item's exact pinned revision text beside the proposal (" + session.ReviewSourceMessage + ").");
            Require(session.ReviewStalenessMessage is { } current && current.StartsWith("Citations unchanged", StringComparison.Ordinal),
                "The review pane did not report the citations as current before a root moved (" + session.ReviewStalenessMessage + ").");
            Console.WriteLine("PASS review surface: the cited L2 memory item is served at its pinned revision beside the proposal");

            // A root underneath the cited item moves: the pane must report the
            // transitive review need beside the same pinned, unchanged item.
            var revisedPath = Path.Combine(fixture.OutputDirectory, "s6t4-review-alpha-rev2.json");
            await File.WriteAllTextAsync(revisedPath,
                S6T4SourceDraftJson("S6-T4 review-surface synthetic note (revised)", "2", S6T4ReviewAlphaRevisedText,
                    DateTimeOffset.UtcNow.AddMinutes(-1)), Encoding.UTF8).ConfigureAwait(false);
            var revised = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-revise", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S6T4ReviewAlphaNote, "--zone-id", S6T4ReviewZoneAlpha,
                "--expected-revision", "1", "--input-file", revisedPath).ConfigureAwait(false);
            Require(revised.ExitCode == 0,
                "The cited source could not be revised (exit=" + revised.ExitCode + "; stdout=" + revised.StandardOutput.Trim() + "; stderr=" + revised.StandardError.Trim() + ").");

            await session.OpenReviewProposalAsync(new OwnerProposalIdentity(proposalId, 1, S6T4ReviewZoneInbox)).ConfigureAwait(false);
            Require(session.ReviewStalenessMessage is { } stale &&
                    stale.Contains("NEEDS REVIEW", StringComparison.Ordinal) &&
                    stale.Contains("citedSourceLineageChanged", StringComparison.Ordinal) &&
                    stale.Contains(S6T4ReviewAlphaNote, StringComparison.Ordinal),
                "The review pane did not report the transitive review need after a root under the cited item moved (" + session.ReviewStalenessMessage + ").");
            Require(session.ReviewCitedItem is { Revision: 1 } stillCited &&
                    string.Equals(stillCited.Body, S6T4ReviewLevelTwoBody, StringComparison.Ordinal) &&
                    session.ReviewProposal is { Revision: 1 } reread &&
                    reread.Item.Sources is { Length: 1 } rereadSources &&
                    rereadSources[0].SourceRevision == 1,
                "The transitive review need rewrote the cited item or the proposal record instead of only reporting it (" + session.ReviewSourceMessage + ").");
            Console.WriteLine("PASS review surface: a moved root under the unchanged cited item is served as a transitive review need");

            // Locking clears the private review view, the cited item included.
            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0,
                "Locking the review-surface vault failed (exit=" + locked.ExitCode + "; stderr=" + locked.StandardError.Trim() + ").");
            await session.CheckAsync().ConfigureAwait(false);
            Require(session.ReportedVaultState != OwnerVaultState.Unlocked &&
                    session.ReviewProposal is null && session.ReviewCitedItem is null &&
                    session.ReviewSourceText is null && session.ReviewStalenessMessage is null,
                "Locking did not clear the cited item and the private review view (" + session.ReportedVaultState + ").");
            Console.WriteLine("PASS review surface: locking the vault clears the cited item and the private review view");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static string S6T4ItemDraftJson(
        string kind,
        string author,
        string creationMethod,
        string evidence,
        string level,
        string title,
        string body,
        DateTimeOffset recordedAtUtc,
        object[]? sources = null) =>
        JsonSerializer.Serialize(new
        {
            kind,
            author,
            creationMethod,
            evidence,
            level,
            title,
            body,
            sources = sources ?? Array.Empty<object>(),
            occurredAtUtc = (DateTimeOffset?)null,
            recordedAtUtc,
            applicability = (object?)null
        });

    /// <summary>
    /// A registered-source revise draft: the same shape the owner CLI accepts for
    /// <c>source-revise</c>, carrying the replacement verbatim text at the new
    /// revision.
    /// </summary>
    private static string S6T4SourceDraftJson(string title, string version, string text, DateTimeOffset recordedAtUtc) =>
        JsonSerializer.Serialize(new
        {
            title,
            locator = (string?)null,
            version,
            availability = "available",
            creationMethod = "directWrite",
            capturedAtUtc = recordedAtUtc,
            recordedAtUtc,
            author = "external",
            text
        });

    private static string S6T4ProposalDraftJson(DateTimeOffset now) =>
        JsonSerializer.Serialize(new
        {
            kind = "create",
            idempotencyKey = "s6t4-review-surface-1",
            destinationResourceId = S6T4ReviewInboxId,
            destinationRevision = 1L,
            subjectResourceId = (string?)null,
            subjectRevision = (long?)null,
            item = new
            {
                kind = "synthesis",
                author = "assistant",
                creationMethod = "synthesis",
                evidence = "reportedSource",
                level = "l3Plus",
                title = "S6-T4 review-surface proposed L3+ output",
                body = "S6-T4 review-surface proposed body: it stands on the cited L2 output alone.",
                sources = new object[]
                {
                    new
                    {
                        sourceId = S6T4ReviewLevelTwoItem,
                        locator = (string?)null,
                        availability = "available",
                        sourceRevision = 1L,
                        span = (object?)null
                    }
                },
                occurredAtUtc = (DateTimeOffset?)null,
                recordedAtUtc = now,
                applicability = (object?)null
            },
            userConfirmed = false
        });

    private static async Task WriteS6T4ReviewSourceAsync(
        string connectionsExecutable,
        SmokeFixture fixture,
        string sourceId,
        string zoneId,
        string text,
        DateTimeOffset now)
    {
        var input = Path.Combine(fixture.OutputDirectory, sourceId + ".note");
        await File.WriteAllTextAsync(input, text, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "research-capture", "--control-pipe", fixture.ControlPipe,
            "--resource-id", sourceId, "--zone-id", zoneId, "--input-file", input,
            "--title", "S6-T4 review-surface synthetic note").ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("revision=1", StringComparison.Ordinal),
            "The review-surface note " + sourceId + " was not captured at revision 1 (exit=" + result.ExitCode +
            "; stdout=" + result.StandardOutput.Trim() + "; stderr=" + result.StandardError.Trim() + ").");
    }

    private static async Task WriteS6T4ItemAsync(
        string connectionsExecutable,
        SmokeFixture fixture,
        string resourceId,
        string zoneId,
        string draftJson)
    {
        var input = Path.Combine(fixture.OutputDirectory, resourceId + ".item.json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId, "--input-file", input).ConfigureAwait(false);
        File.Delete(input);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
            "The review-surface item " + resourceId + " was not stored at revision 1 (exit=" + result.ExitCode +
            "; stdout=" + result.StandardOutput.Trim() + "; stderr=" + result.StandardError.Trim() + ").");
    }

    /// <summary>Deterministic stand-in for the provider boundary.</summary>
    private class SynthesisStubAdapter : IModelProviderAdapter
    {
        public virtual Task<ModelProviderResponse> CompleteAsync(ModelProviderRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelProviderResponse(
                Success: true,
                Content: BuildOutput(request.UserPrompt),
                InputTokens: 240,
                OutputTokens: 160,
                CachedInputTokens: 0,
                ErrorCode: null,
                ErrorMessage: null,
                RequestSent: true));

        protected static string BuildOutput(string userPrompt)
        {
            var grounding = new List<string>();
            if (userPrompt.Contains(S6T4AlphaExcerpt, StringComparison.Ordinal))
            {
                grounding.Add("{\"inputId\":\"" + S6T4AlphaNote + "\",\"inputRevision\":1,\"excerpt\":\"" + S6T4AlphaExcerpt + "\"}");
            }

            if (userPrompt.Contains(S6T4LevelTwoExcerpt, StringComparison.Ordinal))
            {
                grounding.Add("{\"inputId\":\"" + S6T4LevelTwoItem + "\",\"inputRevision\":1,\"excerpt\":\"" + S6T4LevelTwoExcerpt + "\"}");
            }

            if (userPrompt.Contains(S6T4BetaExcerpt, StringComparison.Ordinal))
            {
                grounding.Add("{\"inputId\":\"" + S6T4BetaNote + "\",\"inputRevision\":1,\"excerpt\":\"" + S6T4BetaExcerpt + "\"}");
            }

            var body = grounding.Count > 1
                ? "Both grounded inputs report the same retry behavior, while their measured throughput limits differ and one limit stays unmeasured."
                : "One grounded input reports a retry behavior; nothing else is compared here.";
            return "{\"summary\":\"S6-T4 stub synthesis\",\"items\":[{\"kind\":\"synthesis\",\"title\":\"Retry behavior and unmeasured limits\"," +
                "\"body\":\"" + body + "\",\"crossSource\":" + (grounding.Count > 1 ? "true" : "false") +
                ",\"grounding\":[" + string.Join(",", grounding) + "]}]}";
        }
    }

    /// <summary>Stub adapter that changes a selected input while it answers.</summary>
    private sealed class RevisioningSynthesisStubAdapter : SynthesisStubAdapter
    {
        private readonly Action _change;

        public RevisioningSynthesisStubAdapter(Action change) => _change = change;

        public override Task<ModelProviderResponse> CompleteAsync(ModelProviderRequest request, CancellationToken cancellationToken)
        {
            _change();
            return base.CompleteAsync(request, cancellationToken);
        }
    }
}
