using System.Globalization;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// Sources and organization settings of the owner console (S5-T7).
///
/// Every value here is an answer of the running service on the owner control
/// pipe: the service owns the enrolled allowlist, the held mapping review,
/// the checkpoints, the quarantine records, the availability observations,
/// the encrypted cache scope, the refresh consent, the owner collections and
/// the preserved snapshots. This session sends the owner's explicit acts and
/// renders what came back; it keeps no second source, collection or snapshot
/// store, no visibility policy of its own, no vault file handle and no
/// plaintext file.
///
/// The private locator travels only in the request the owner explicitly
/// sends (enrollment and mapping-review submission carry it; every other act
/// names the source ID only), and no answer the service returns carries one:
/// the wire has no locator field by design. A held mapping-unknown
/// submission is shown with its quarantine reason, its unresolved-field
/// detail and its recorded time — never with a guessed zone, a default
/// format, or an opened transcript. An unregistered format is the service's
/// own explicit unsupported diagnostic, never a silent best effort; a
/// missing source names its remediation (owner relocate with an explicitly
/// supplied candidate, or revoke), never a scan.
///
/// Private view state — the allowlist, the open status, the open ingest
/// outcome, the refresh entries, the collections, the snapshots and the
/// owner's drafts — lives only in this process and is dropped with the
/// private epoch it belonged to: the epoch moves whenever the vault leaves
/// the unlocked state, and an asynchronous answer is applied only while the
/// epoch it was requested under is still current, after the service has been
/// asked for its state again. A late answer for a finished epoch is dropped,
/// so a lock cannot be undone by a reply that was already in flight. Ending
/// the owner session drops the same state.
/// </summary>
internal sealed partial class OwnerConsoleSession
{
    private static readonly TimeSpan SourcesActTimeout = TimeSpan.FromSeconds(20);
    private long _sourcesFormVersion;

    /// <summary>Every enrolled source the service listed, in the order it served them, plus held mapping review.</summary>
    public OwnerSourceEnrollment[] Sources { get; private set; } = Array.Empty<OwnerSourceEnrollment>();

    /// <summary>Owner-facing outcome of the last source listing. Never private content or a locator.</summary>
    public string? SourcesMessage { get; private set; }

    /// <summary>The source status the owner opened, exactly as the service served it.</summary>
    public OwnerSourceEnrollment? OpenSource { get; private set; }

    /// <summary>Owner-facing outcome of the last source act. Never private content, a locator, or a bearer.</summary>
    public string? SourceMessage { get; private set; }

    /// <summary>The ingest outcome the service reported for the last explicit ingest act.</summary>
    public OwnerSourceIngestOutcome? LastIngest { get; private set; }

    /// <summary>Every refresh entry the last explicit refresh pass reported, in the order served.</summary>
    public OwnerSourceRefreshEntry[] RefreshEntries { get; private set; } = Array.Empty<OwnerSourceRefreshEntry>();

    /// <summary>Owner-facing outcome of the last refresh pass. Never private content or a locator.</summary>
    public string? RefreshMessage { get; private set; }

    /// <summary>Every owner collection the service listed, in the order it served them.</summary>
    public OwnerCollectionMapping[] Collections { get; private set; } = Array.Empty<OwnerCollectionMapping>();

    /// <summary>Owner-facing outcome of the last collection act. Never private content.</summary>
    public string? CollectionsMessage { get; private set; }

    /// <summary>Every preserved snapshot descriptor the last snapshot listing served.</summary>
    public OwnerSourceSnapshotDescriptor[] Snapshots { get; private set; } = Array.Empty<OwnerSourceSnapshotDescriptor>();

    /// <summary>The preserved snapshot the last preserve/retain act wrote, exactly as the service served it.</summary>
    public OwnerSourceSnapshotEntry? OpenSnapshot { get; private set; }

    /// <summary>Owner-facing outcome of the last snapshot act. Never snapshot content.</summary>
    public string? SnapshotMessage { get; private set; }

    /// <summary>True while one sources act is in flight; further sources acts wait for its answer.</summary>
    public bool SourcesActInFlight { get; private set; }

    /// <summary>
    /// Increments whenever this session drops the owner's sources drafts (the
    /// enrollment/mapping/collection/snapshot forms) of the epoch that ended.
    /// The host clears its own sources controls when it observes a new value.
    /// </summary>
    public long SourcesFormVersion => _sourcesFormVersion;

    /// <summary>
    /// Reads the enrolled allowlist with each source's life, checkpoint,
    /// refresh consent and encrypted cache scope, plus held mapping review.
    /// Listing never opens a transcript.
    /// </summary>
    public async Task RefreshSourcesAsync()
    {
        if (TryBeginSourcesAct() is not { } epoch)
        {
            return;
        }

        SourcesMessage = null;
        var result = await ExecuteSourcesAsync(
            token => OwnerChannelClient.ListSourcesAsync(ControlPipe, token),
            code => new OwnerSourceListResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplySourcesListAnswer(epoch, result);
        Append(DescribeSourcesActForLog("read sources", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Opens one source's status: its life, checkpoint, quarantine, exact
    /// availability observation and encrypted cache scope.
    /// </summary>
    public async Task OpenSourceAsync(OwnerSourceEnrollment source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (TryBeginSourcesAct() is not { } epoch)
        {
            return;
        }

        SourceMessage = null;
        var result = await ExecuteSourcesAsync(
            token => OwnerChannelClient.ReadSourceStatusAsync(ControlPipe, source.SourceId, token),
            code => new OwnerSourceStatusResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplySourceStatusAnswer(epoch, result);
        Append(DescribeSourcesActForLog("open source", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Submits one source whose zone/format mapping the owner cannot yet
    /// resolve into the private quarantine. The locator travels only in this
    /// request; no transcript is opened and no zone is defaulted.
    /// </summary>
    public async Task SubmitMappingUnknownAsync(
        string sourceId,
        string locator,
        string? zoneId,
        string? format,
        int? formatVersion)
    {
        if (TryBeginSourcesAct() is not { } epoch)
        {
            return;
        }

        SourceMessage = null;
        var result = await ExecuteSourcesAsync(
            token => OwnerChannelClient.SubmitMappingUnknownAsync(ControlPipe, sourceId, locator, zoneId, format, formatVersion, token),
            code => new OwnerSourceStatusResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        var applied = ApplySourceStatusAnswer(epoch, result);
        Append(DescribeSourcesActForLog("hold mapping for review", result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (applied && result.Outcome == OwnerCommandOutcome.Completed)
        {
            await RefreshSourcesAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Withdraws one held mapping-unknown submission without enrolling it.
    /// The held locator is never opened.
    /// </summary>
    public async Task WithdrawMappingUnknownAsync(OwnerSourceEnrollment held)
    {
        ArgumentNullException.ThrowIfNull(held);
        if (TryBeginSourcesAct() is not { } epoch)
        {
            return;
        }

        SourceMessage = null;
        var result = await ExecuteSourcesAsync(
            token => OwnerChannelClient.WithdrawMappingUnknownAsync(ControlPipe, held.SourceId, token),
            code => new OwnerSourceIngestResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        var applied = ApplySourcesWithdrawAnswer(epoch, held, result);
        Append(DescribeSourcesActForLog("withdraw held mapping", result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (applied && result.Outcome == OwnerCommandOutcome.Completed)
        {
            await RefreshSourcesAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Runs one bounded ingest for one enrolled source. The service's own
    /// outcome — advanced, idle, partial, or an explicit refusal including
    /// the unsupported diagnostics — is rendered, never reinterpreted: a
    /// refusal keeps its refusal outcome and error code in the answer, the
    /// message and the activity log.
    /// </summary>
    public async Task IngestSourceAsync(OwnerSourceEnrollment source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (TryBeginSourcesAct() is not { } epoch)
        {
            return;
        }

        SourceMessage = null;
        var result = await ExecuteSourcesAsync(
            token => OwnerChannelClient.IngestSourceAsync(ControlPipe, source.SourceId, token),
            code => new OwnerSourceIngestResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        var applied = ApplySourceIngestAnswer(epoch, result);
        Append(DescribeSourcesActForLog("ingest source", result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (applied && result.Outcome == OwnerCommandOutcome.Completed)
        {
            await RefreshSourcesAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Pauses one enrolled source: stops status reads, ingest and refresh for
    /// that source with no transcript open.
    /// </summary>
    public async Task PauseSourceAsync(OwnerSourceEnrollment source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (TryBeginSourcesAct() is not { } epoch)
        {
            return;
        }

        SourceMessage = null;
        var result = await ExecuteSourcesAsync(
            token => OwnerChannelClient.PauseSourceAsync(ControlPipe, source.SourceId, token),
            code => new OwnerSourceStatusResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        var applied = ApplySourceStatusAnswer(epoch, result);
        Append(DescribeSourcesActForLog("pause source", result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (applied && result.Outcome == OwnerCommandOutcome.Completed)
        {
            await RefreshSourcesAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Resumes one paused source from its persisted checkpoint.
    /// </summary>
    public async Task ResumeSourceAsync(OwnerSourceEnrollment source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (TryBeginSourcesAct() is not { } epoch)
        {
            return;
        }

        SourceMessage = null;
        var result = await ExecuteSourcesAsync(
            token => OwnerChannelClient.ResumeSourceAsync(ControlPipe, source.SourceId, token),
            code => new OwnerSourceStatusResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        var applied = ApplySourceStatusAnswer(epoch, result);
        Append(DescribeSourcesActForLog("resume source", result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (applied && result.Outcome == OwnerCommandOutcome.Completed)
        {
            await RefreshSourcesAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Records the owner's explicit per-source background-refresh consent.
    /// </summary>
    public async Task SetSourceRefreshOptInAsync(OwnerSourceEnrollment source, bool optIn)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (TryBeginSourcesAct() is not { } epoch)
        {
            return;
        }

        SourceMessage = null;
        var result = await ExecuteSourcesAsync(
            token => OwnerChannelClient.SetSourceRefreshOptInAsync(ControlPipe, source.SourceId, optIn, token),
            code => new OwnerSourceStatusResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        var applied = ApplySourceStatusAnswer(epoch, result);
        Append(DescribeSourcesActForLog(optIn ? "opt source into refresh" : "opt source out of refresh", result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (applied && result.Outcome == OwnerCommandOutcome.Completed)
        {
            await RefreshSourcesAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Runs one explicit idempotent refresh pass over the owner allowlist.
    /// </summary>
    public async Task RefreshSourcesPassAsync()
    {
        if (TryBeginSourcesAct() is not { } epoch)
        {
            return;
        }

        RefreshMessage = null;
        var result = await ExecuteSourcesAsync(
            token => OwnerChannelClient.RefreshSourcesAsync(ControlPipe, token),
            code => new OwnerSourceRefreshResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        var applied = ApplySourceRefreshAnswer(epoch, result);
        Append(DescribeSourcesActForLog("refresh allowlist", result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (applied && result.Outcome == OwnerCommandOutcome.Completed)
        {
            await RefreshSourcesAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Reads the owner collections: the collection IDs the owner named and
    /// the member resource IDs each currently holds.
    /// </summary>
    public async Task RefreshCollectionsAsync()
    {
        if (TryBeginSourcesAct() is not { } epoch)
        {
            return;
        }

        CollectionsMessage = null;
        var result = await ExecuteSourcesAsync(
            token => OwnerChannelClient.ListCollectionsAsync(ControlPipe, token),
            code => new OwnerCollectionListResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyCollectionListAnswer(epoch, result);
        Append(DescribeSourcesActForLog("read collections", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Saves one owner collection membership. Adding a member never widens a
    /// frozen grant or session.
    /// </summary>
    public async Task SetCollectionAsync(string collectionId, string[] resourceIds)
    {
        if (TryBeginSourcesAct() is not { } epoch)
        {
            return;
        }

        CollectionsMessage = null;
        var result = await ExecuteSourcesAsync(
            token => OwnerChannelClient.SetCollectionAsync(ControlPipe, collectionId, resourceIds, token),
            code => new OwnerCollectionSetResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        var applied = ApplyCollectionSetAnswer(epoch, result);
        Append(DescribeSourcesActForLog("save collection", result.Outcome, result.ErrorCode));
        RaiseChanged();
        if (applied && result.Outcome == OwnerCommandOutcome.Completed)
        {
            await RefreshCollectionsAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Registers the preservation reference of the selected enrolled source
    /// through the existing S2-T3 registry create: the metadata-only record
    /// the snapshot preserve freezes. The owner names the enrolled locator
    /// explicitly in this act (as with enrollment); the service validates it
    /// against the live enrollment before storing revision 1.
    /// </summary>
    public async Task RegisterSourceReferenceAsync(OwnerSourceEnrollment source, string locator)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(locator);
        if (TryBeginSourcesAct() is not { } epoch)
        {
            return;
        }

        SnapshotMessage = null;
        var result = await ExecuteSourcesAsync(
            token => OwnerChannelClient.RegisterSourceReferenceAsync(ControlPipe, source.SourceId, source.ZoneId, locator, token),
            code => new OwnerSourceReadResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplySourceReferenceAnswer(epoch, source, result);
        Append(DescribeSourcesActForLog("register source reference", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Lists the explicitly preserved snapshots of one enrolled source.
    /// </summary>
    public async Task ListSnapshotsAsync(OwnerSourceEnrollment source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (TryBeginSourcesAct() is not { } epoch)
        {
            return;
        }

        SnapshotMessage = null;
        var result = await ExecuteSourcesAsync(
            token => OwnerChannelClient.ListSourceSnapshotsAsync(ControlPipe, source.SourceId, token),
            code => new OwnerSourceSnapshotListResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplySnapshotListAnswer(epoch, result);
        Append(DescribeSourcesActForLog("list snapshots", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }
    /// <summary>
    /// Opens one explicitly preserved snapshot the last listing served: the
    /// service rechecks the snapshot's own live revision and serves the
    /// frozen metadata. Nothing here caches an old revision and nothing
    /// opens snapshot content — the live read is authoritative, and a stale
    /// revision reports its conflict with its remediation instead of a guess.
    /// </summary>
    public async Task OpenSnapshotAsync(OwnerSourceSnapshotDescriptor snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (TryBeginSourcesAct() is not { } epoch)
        {
            return;
        }

        SnapshotMessage = null;
        var result = await ExecuteSourcesAsync(
            token => OwnerChannelClient.ReadSourceSnapshotAsync(ControlPipe, snapshot.Id, snapshot.Revision, token),
            code => new OwnerSourceSnapshotResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplySnapshotAnswer(epoch, "open snapshot", result);
        Append(DescribeSourcesActForLog("open snapshot", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Explicitly preserves one enrolled source generation with an explicit
    /// future UTC retention. Nothing is migrated automatically.
    /// </summary>
    public async Task PreserveSnapshotAsync(string sourceId, long sourceRevision, DateTimeOffset retainUntilUtc)
    {
        if (TryBeginSourcesAct() is not { } epoch)
        {
            return;
        }

        SnapshotMessage = null;
        var result = await ExecuteSourcesAsync(
            token => OwnerChannelClient.PreserveSourceSnapshotAsync(ControlPipe, sourceId, sourceRevision, retainUntilUtc, token),
            code => new OwnerSourceSnapshotResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplySnapshotAnswer(epoch, "preserve snapshot", result);
        Append(DescribeSourcesActForLog("preserve snapshot", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Extends one preserved snapshot's retention to a new explicit future
    /// UTC instant.
    /// </summary>
    public async Task RetainSnapshotAsync(OwnerSourceSnapshotEntry snapshot, DateTimeOffset retainUntilUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (TryBeginSourcesAct() is not { } epoch)
        {
            return;
        }

        SnapshotMessage = null;
        var result = await ExecuteSourcesAsync(
            token => OwnerChannelClient.RetainSourceSnapshotAsync(ControlPipe, snapshot.Id, snapshot.Revision, retainUntilUtc, token),
            code => new OwnerSourceSnapshotResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplySnapshotAnswer(epoch, "extend snapshot retention", result);
        Append(DescribeSourcesActForLog("extend snapshot retention", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Reports a sources-form value this console refuses to send, so the owner
    /// sees which input is unusable instead of a silently different act.
    /// Never private content or a locator.
    /// </summary>
    public void ReportSourcesFormProblem(string message)
    {
        SourceMessage = message;
        RaiseChanged();
    }

    /// <summary>
    /// Reports a snapshot-form value this console refuses to send. Never
    /// snapshot content.
    /// </summary>
    public void ReportSnapshotFormProblem(string message)
    {
        SnapshotMessage = message;
        RaiseChanged();
    }

    /// <summary>Applies one sources listing requested under <paramref name="epoch"/>.</summary>
    internal bool ApplySourcesListAnswer(long epoch, OwnerSourceListResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSourcesAnswer("sources list");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Sources is not null)
        {
            Sources = result.Sources;
            SourcesMessage = DescribeSourcesListAnswer(result.Sources);
        }
        else
        {
            SourcesMessage = DescribeSourcesAnswer("sources list", result.Outcome, result.ErrorCode);
            ApplySourcesRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one source status requested under <paramref name="epoch"/>.</summary>
    internal bool ApplySourceStatusAnswer(long epoch, OwnerSourceStatusResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSourcesAnswer("source status");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Source is not null)
        {
            OpenSource = result.Source;
            SourceMessage = DescribeSourceStatusAnswer(result.Source);
        }
        else
        {
            SourceMessage = DescribeSourcesAnswer("source status", result.Outcome, result.ErrorCode);
            ApplySourcesRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one held-mapping withdrawal requested under <paramref name="epoch"/>.</summary>
    internal bool ApplySourcesWithdrawAnswer(long epoch, OwnerSourceEnrollment held, OwnerSourceIngestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSourcesAnswer("held mapping withdrawal");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed)
        {
            if (OpenSource is not null && string.Equals(OpenSource.SourceId, held.SourceId, StringComparison.Ordinal))
            {
                OpenSource = null;
            }

            SourceMessage = "The service withdrew the held mapping " + held.SourceId +
                ": the submission was never enrolled, its transcript was never opened, and no zone was defaulted.";
        }
        else
        {
            SourceMessage = DescribeSourcesAnswer("held mapping withdrawal", result.Outcome, result.ErrorCode);
            ApplySourcesRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one ingest outcome requested under <paramref name="epoch"/>.</summary>
    internal bool ApplySourceIngestAnswer(long epoch, OwnerSourceIngestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSourcesAnswer("source ingest");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Ingest is not null)
        {
            LastIngest = result.Ingest;
            SourceMessage = DescribeSourceIngestAnswer(result.Ingest);
        }
        else
        {
            SourceMessage = DescribeSourcesAnswer("source ingest", result.Outcome, result.ErrorCode);
            ApplySourcesRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one refresh pass requested under <paramref name="epoch"/>.</summary>
    internal bool ApplySourceRefreshAnswer(long epoch, OwnerSourceRefreshResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSourcesAnswer("sources refresh");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Entries is not null)
        {
            RefreshEntries = result.Entries;
            RefreshMessage = DescribeSourceRefreshAnswer(result.Entries);
        }
        else
        {
            RefreshMessage = DescribeSourcesAnswer("sources refresh", result.Outcome, result.ErrorCode);
            ApplySourcesRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one collection listing requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyCollectionListAnswer(long epoch, OwnerCollectionListResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSourcesAnswer("collection list");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Collections is not null)
        {
            Collections = result.Collections;
            CollectionsMessage = DescribeCollectionListAnswer(result.Collections);
        }
        else
        {
            CollectionsMessage = DescribeSourcesAnswer("collection list", result.Outcome, result.ErrorCode);
            ApplySourcesRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one collection write requested under <paramref name="epoch"/>.</summary>
    internal bool ApplyCollectionSetAnswer(long epoch, OwnerCollectionSetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSourcesAnswer("collection save");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Collection is not null)
        {
            CollectionsMessage = "The service saved collection " + result.Collection.CollectionId + " with " +
                result.Collection.ResourceIds.Length.ToString(CultureInfo.InvariantCulture) +
                " member(s). Membership is selection metadata only: no frozen grant or session widened.";
        }
        else
        {
            CollectionsMessage = DescribeSourcesAnswer("collection save", result.Outcome, result.ErrorCode);
            ApplySourcesRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one source-reference registration requested under <paramref name="epoch"/>.</summary>
    internal bool ApplySourceReferenceAnswer(long epoch, OwnerSourceEnrollment source, OwnerSourceReadResult result)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSourcesAnswer("source reference registration");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Source is not null)
        {
            SnapshotMessage = "The service registered the preservation reference of " + source.SourceId +
                " revision " + result.Source.Revision.ToString(CultureInfo.InvariantCulture) +
                " in zone " + result.Source.ZoneId +
                ": preservation can now freeze this generation's exact bytes with an explicit retention.";
        }
        else
        {
            SnapshotMessage = DescribeSourcesAnswer("source reference registration", result.Outcome, result.ErrorCode);
            ApplySourcesRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one snapshot listing requested under <paramref name="epoch"/>.</summary>
    internal bool ApplySnapshotListAnswer(long epoch, OwnerSourceSnapshotListResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSourcesAnswer("snapshot list");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Snapshots is not null)
        {
            Snapshots = result.Snapshots;
            SnapshotMessage = DescribeSnapshotListAnswer(result.Snapshots);
        }
        else
        {
            SnapshotMessage = DescribeSourcesAnswer("snapshot list", result.Outcome, result.ErrorCode);
            ApplySourcesRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>Applies one snapshot write requested under <paramref name="epoch"/>.</summary>
    internal bool ApplySnapshotAnswer(long epoch, string act, OwnerSourceSnapshotResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleSourcesAnswer(act);
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Snapshot is not null)
        {
            OpenSnapshot = result.Snapshot;
            SnapshotMessage = DescribeSnapshotAnswer(act, result.Snapshot);
        }
        else
        {
            SnapshotMessage = DescribeSourcesAnswer(act, result.Outcome, result.ErrorCode);
            ApplySourcesRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    /// <summary>
    /// Drops the sources, collection and snapshot views plus the owner's
    /// sources drafts of the epoch that just ended, and moves the private
    /// epoch so an answer already in flight cannot restore any of them.
    /// </summary>
    private void ClearPrivateSourcesState()
    {
        _privateEpoch++;
        Sources = Array.Empty<OwnerSourceEnrollment>();
        SourcesMessage = null;
        OpenSource = null;
        SourceMessage = null;
        LastIngest = null;
        RefreshEntries = Array.Empty<OwnerSourceRefreshEntry>();
        RefreshMessage = null;
        Collections = Array.Empty<OwnerCollectionMapping>();
        CollectionsMessage = null;
        Snapshots = Array.Empty<OwnerSourceSnapshotDescriptor>();
        OpenSnapshot = null;
        SnapshotMessage = null;
        _sourcesFormVersion++;
    }

    private void ApplySourcesRefusal(OwnerCommandOutcome outcome, string? errorCode)
    {
        if (outcome == OwnerCommandOutcome.Refused &&
            errorCode is "vault_locked" or "resource_unavailable" or "source_state_unavailable" or "source_snapshot_unavailable" or "scope_state_unavailable")
        {
            ClearPrivateSourcesState();
        }
    }

    private void AppendIgnoredStaleSourcesAnswer(string act) =>
        Append("ignored a stale " + act + " answer: the unlocked vault state it belonged to is gone, so the cleared sources view stays cleared");

    private long? TryBeginSourcesAct()
    {
        if (!Attached || SourcesActInFlight || ReportedVaultState != OwnerVaultState.Unlocked)
        {
            return null;
        }

        SourcesActInFlight = true;
        SourceMessage = null;
        RaiseChanged();
        return _privateEpoch;
    }

    private async Task<T> ExecuteSourcesAsync<T>(Func<CancellationToken, Task<T>> call, Func<string, T> onFailure)
    {
        try
        {
            using var timeout = new CancellationTokenSource(SourcesActTimeout);
            return await call(timeout.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return onFailure("act_failed:" + ex.GetType().Name);
        }
        finally
        {
            SourcesActInFlight = false;
        }
    }

    private static string DescribeSourcesListAnswer(OwnerSourceEnrollment[] sources)
    {
        if (sources.Length == 0)
        {
            return "The service answered with an empty allowlist: no source is enrolled yet, and nothing is held for mapping review.";
        }

        var held = sources.Count(source => IsMappingUnknown(source));
        var quarantined = sources.Count(source => string.Equals(source.Life, "quarantined", StringComparison.Ordinal) && !IsMappingUnknown(source));
        var paused = sources.Count(source => string.Equals(source.Life, "paused", StringComparison.Ordinal));
        return "The service listed " + sources.Length.ToString(CultureInfo.InvariantCulture) +
            " source(s): quarantined=" + quarantined.ToString(CultureInfo.InvariantCulture) +
            " paused=" + paused.ToString(CultureInfo.InvariantCulture) +
            " held for mapping review=" + held.ToString(CultureInfo.InvariantCulture) +
            ". Listing opened no transcript and echoed no locator.";
    }

    private static string DescribeSourceStatusAnswer(OwnerSourceEnrollment source)
    {
        if (IsMappingUnknown(source))
        {
            return "The service holds " + source.SourceId + " for mapping review (" +
                (source.Quarantine is null ? "mapping_unknown" : source.Quarantine.Reason) + "/" +
                (source.Quarantine is null ? "mapping_unresolved" : source.Quarantine.Detail) +
                "): its transcript was never opened and no zone was defaulted. Confirm its mapping through enrollment once known, or withdraw the submission.";
        }

        var quarantine = source.Quarantine is null
            ? "no quarantine"
            : "quarantine " + source.Quarantine.Reason + "/" + source.Quarantine.Detail;
        return "The service reported " + source.SourceId + " generation " +
            source.Generation.ToString(CultureInfo.InvariantCulture) + " life=" + source.Life +
            " availability=" + source.Availability + " checkpoint=" +
            source.ConsumedOffset.ToString(CultureInfo.InvariantCulture) + " events=" +
            source.EventCount.ToString(CultureInfo.InvariantCulture) + " refreshOptIn=" +
            (source.RefreshOptIn ? "true" : "false") + " " + quarantine +
            " journalBytes=" + source.JournalCiphertextBytes.ToString(CultureInfo.InvariantCulture) +
            " snapshotBytes=" + source.SnapshotCiphertextBytes.ToString(CultureInfo.InvariantCulture) + ".";
    }

    private static string DescribeSourceIngestAnswer(OwnerSourceIngestOutcome ingest) =>
        "The service reported ingest status " + ingest.Status + " at checkpoint " +
        ingest.ConsumedOffset.ToString(CultureInfo.InvariantCulture) +
        (string.IsNullOrEmpty(ingest.DiagnosticCode) ? string.Empty : " diagnostic=" + ingest.DiagnosticCode) +
        ". A refusal names its exact condition with its remediation; an unregistered format stays an explicit unsupported diagnostic.";

    private static string DescribeSourceRefreshAnswer(OwnerSourceRefreshEntry[] entries)
    {
        if (entries.Length == 0)
        {
            return "The service refreshed no source: the allowlist is empty.";
        }

        var ingested = entries.Count(entry => entry.Status.StartsWith("ingest_", StringComparison.Ordinal));
        return "The service refreshed " + entries.Length.ToString(CultureInfo.InvariantCulture) +
            " source(s), of which " + ingested.ToString(CultureInfo.InvariantCulture) +
            " ran an ingest pass; every other source named its skip reason with no read.";
    }

    private static string DescribeCollectionListAnswer(OwnerCollectionMapping[] collections) =>
        collections.Length == 0
            ? "The service answered with no owner collection: name one below to organize resources. Membership never grants access by itself."
            : "The service listed " + collections.Length.ToString(CultureInfo.InvariantCulture) +
                " collection(s). Membership is selection metadata only: no frozen grant or session widened.";

    private static string DescribeSnapshotListAnswer(OwnerSourceSnapshotDescriptor[] snapshots) =>
        snapshots.Length == 0
            ? "The service answered with no preserved snapshot for this source: preservation is explicit and opt-in, never automatic."
            : "The service listed " + snapshots.Length.ToString(CultureInfo.InvariantCulture) +
                " preserved snapshot(s). Each freezes one generation's exact bytes with its own retention.";

    private static string DescribeSnapshotAnswer(string act, OwnerSourceSnapshotEntry snapshot) =>
        "The service " + (act == "preserve snapshot" ? "preserved " : act == "open snapshot" ? "opened " : "retained ") + snapshot.Id +
        " revision " + snapshot.Revision.ToString(CultureInfo.InvariantCulture) +
        " from source " + snapshot.SourceId + " reference revision " +
        snapshot.SourceRevision.ToString(CultureInfo.InvariantCulture) + " generation " +
        snapshot.SourceGeneration.ToString(CultureInfo.InvariantCulture) + " bytes=" +
        snapshot.SourceLength.ToString(CultureInfo.InvariantCulture) +
        " retained until " + snapshot.RetainUntilUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) + ".";

    private static string DescribeSourcesAnswer(string act, OwnerCommandOutcome outcome, string? errorCode)
    {
        var code = errorCode ?? "unknown";
        return outcome switch
        {
            OwnerCommandOutcome.Completed =>
                "The service completed the sources act (" + act + ") but returned no sources payload, so this console shows nothing instead of guessing.",
            OwnerCommandOutcome.Refused => code switch
            {
                "vault_locked" =>
                    "The vault is locked, so the service serves no source, collection or snapshot. Unlock the vault (Vault & key) and read them again.",
                "source_not_enrolled" =>
                    "The service has no source with that ID (code: source_not_enrolled): it was never enrolled and is not held for review. Enroll it with an explicit zone/format mapping, or hold it for mapping review when the mapping is unresolved. Nothing was opened.",
                "source_not_mapping_unknown" =>
                    "The service refused: that source is not held for mapping review (code: source_not_mapping_unknown). Only a held mapping-unknown submission can be withdrawn here.",
                "source_already_enrolled" =>
                    "The service refused: that source ID is already enrolled (code: source_already_enrolled). Resolve the held mapping through enrollment under a fresh ID, or revoke the enrolled source first.",
                "source_locator_in_use" =>
                    "The service refused: that locator is already claimed by another source (code: source_locator_in_use). One locator enrolls under one source ID; withdraw the held submission or relocate the enrolled source instead.",
                "source_enrollment_conflict" =>
                    "The service refused an inconsistent mapping (code: source_enrollment_conflict): the same source ID with a different zone, locator or format is never merged. Re-enroll explicitly under a new generation instead.",
                "source_quarantined" =>
                    "The service refused: that source is quarantined (code: source_quarantined). Resolve it by explicit re-enrollment (a new generation on a fresh checkpoint) or revoke it; nothing was opened.",
                "source_mixed_quarantined" =>
                    "The service refused before opening the transcript (code: source_mixed_quarantined): the source is quarantined, so no read was attempted.",
                "source_generation_changed" =>
                    "The service refused a changed generation (code: source_generation_changed): the bytes no longer match the enrolled anchor, and the prior generation is quarantined with its checkpoint frozen. Re-enroll explicitly to adopt the new bytes.",
                "source_revoked" =>
                    "The service refused: that source is revoked (code: source_revoked). Explicit re-enrollment starts a new generation; nothing was opened.",
                "source_already_revoked" =>
                    "The service refused: that source is already revoked (code: source_already_revoked).",
                "source_already_paused" =>
                    "The service refused: that source is already paused (code: source_already_paused).",
                "source_not_paused" =>
                    "The service refused: that source is not paused (code: source_not_paused). Only a paused source can be resumed.",
                "source_paused" =>
                    "The service refused: that source is paused (code: source_paused). Resume it to read, ingest or refresh it again; the checkpoint is unchanged.",
                "source_missing" =>
                    "The source file is missing at its enrolled locator (code: source_missing). It was not scanned for: relocate it with an explicitly supplied candidate whose bytes match the enrolled generation, or revoke it.",
                "ingest_unsupported_format" =>
                    "The service refused this ingest: the enrolled format has no registered interpreter (code: ingest_unsupported_format). Nothing was saved; enroll the source with a supported format or resolve its mapping explicitly.",
                "ingest_unsupported_record" =>
                    "The service refused this ingest at an unrecognized record (code: ingest_unsupported_record): the run stopped with an explicit diagnostic and saved nothing past that record. Nothing was skipped silently.",
                "source_unreadable" =>
                    "The source file cannot be read at its enrolled locator (code: source_unreadable). Check access and retry; nothing was partially ingested.",
                "source_locator_unreadable" =>
                    "The submitted locator cannot be read or is not an absolute path (code: source_locator_unreadable). No mapping was stored.",
                "source_relocation_missing" =>
                    "The relocation candidate is missing (code: source_relocation_missing). Only an explicitly supplied candidate whose bytes match is adopted.",
                "source_relocation_changed" =>
                    "The relocation candidate's bytes do not match the enrolled generation (code: source_relocation_changed). Nothing was re-pointed.",
                "source_relocation_conflict" =>
                    "The service refused the relocation as conflicting (code: source_relocation_conflict). Nothing was re-pointed.",
                "source_reference_mismatch" =>
                    "The registered source reference does not match the enrollment (code: source_reference_mismatch). Nothing was preserved.",
                "source_snapshot_already_preserved" =>
                    "That generation is already preserved with a different scope or retention (code: source_snapshot_already_preserved). Retention changes go through the retain act.",
                "source_snapshot_revision_conflict" =>
                    "The snapshot revision moved (code: source_snapshot_revision_conflict). Read the live revision and retry.",
                "source_snapshot_purged" =>
                    "That snapshot is purged (code: source_snapshot_purged). Its bytes authorize nothing afterwards.",
                "source_snapshot_expired" =>
                    "That snapshot's retention elapsed (code: source_snapshot_expired). Extend retention explicitly before it lapses.",
                "invalid_collection_request" =>
                    "The service refused this collection as invalid (code: invalid_collection_request): the collection ID or a member resource ID is not one this console can state, or a member is no longer managed. Nothing was saved and membership is unchanged.",
                "invalid_request" =>
                    "The service refused this sources act as invalid (code: invalid_request). Nothing changed; an enroll intent with an unresolved mapping belongs in mapping review, never in a guessed zone.",
                "resource_unavailable" =>
                    "The service did not serve that sources record (code: resource_unavailable). It may have moved or been removed; nothing else was substituted.",
                "source_state_unavailable" =>
                    "The service could not answer this sources read (code: source_state_unavailable). No private content was served.",
                "source_snapshot_unavailable" =>
                    "The service could not answer this snapshot read (code: source_snapshot_unavailable). No snapshot content was served.",
                "scope_state_unavailable" =>
                    "The service could not answer this collection read (code: scope_state_unavailable). No membership was served.",
                _ => "The service refused this sources request (code: " + code + "). Nothing was read or changed."
            },
            OwnerCommandOutcome.OwnerAuthorityDenied =>
                "This endpoint refused owner authority for the sources request (code: " + code + "). An agent channel never serves the owner allowlist, collections or snapshots.",
            OwnerCommandOutcome.ProtocolMismatch =>
                "The endpoint speaks a different protocol revision, so no sources request was performed.",
            _ => "No owner-channel answer reached the service (code: " + code + "), so no sources request was performed."
        };
    }

    private static string DescribeSourcesActForLog(string act, OwnerCommandOutcome outcome, string? errorCode) =>
        "owner act: sources " + act + " -> " + outcome.ToString().ToLowerInvariant() +
        (outcome == OwnerCommandOutcome.Completed ? "" : " (" + (errorCode ?? "unknown") + ")");

    private static bool IsMappingUnknown(OwnerSourceEnrollment source) =>
        source.Quarantine is not null &&
        string.Equals(source.Quarantine.Reason, "mapping_unknown", StringComparison.Ordinal);
}
