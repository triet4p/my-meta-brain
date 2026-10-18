using System.Globalization;
using MetaBrain.Connections;

namespace MetaBrain.OwnerUi;

/// <summary>
/// Owner declassification of the proposal under review into an explicit
/// shared zone (S6-T5).
///
/// The owner previews the exact sanitized body, title and metadata the shared
/// zone would receive beside the private pins it would stand on, then
/// explicitly publishes with a typed PUBLISH confirmation. The shared copy
/// carries no citations: private lineage travels only through encrypted
/// derived_from relations the shared-only sessions cannot open. This session
/// keeps no second store and mints no access; the destination zone comes from
/// the owner's explicit input on this screen, never from a grant or a tag.
/// Private preview state drops with the private epoch like every other
/// review view.
/// </summary>
internal sealed partial class OwnerConsoleSession
{
    /// <summary>The last declassification preview the service served, if any.</summary>
    public OwnerDeclassifyPreview? DeclassifyPreview { get; private set; }

    /// <summary>The last completed declassification publication, if any.</summary>
    public OwnerDeclassifyOutcome? DeclassifyPublished { get; private set; }

    /// <summary>Owner-facing outcome of the last declassification act. Never private content.</summary>
    public string? DeclassifyMessage { get; private set; }

    /// <summary>
    /// Previews the sanitized publication of the proposal under review into
    /// the owner's explicit destination: the exact shared content beside its
    /// private pins and their currentness. Writes nothing and mints nothing.
    /// </summary>
    public async Task PreviewDeclassifiedAsync(string destinationId, string destinationZone, OwnerReviewDraft sanitized)
    {
        ArgumentNullException.ThrowIfNull(destinationId);
        ArgumentNullException.ThrowIfNull(destinationZone);
        ArgumentNullException.ThrowIfNull(sanitized);
        var proposal = ReviewProposal;
        if (proposal is null || TryBeginInboxAct() is not { } epoch)
        {
            return;
        }

        DeclassifyMessage = null;
        var result = await ExecuteInboxAsync(
            token => OwnerChannelClient.PreviewDeclassifyAsync(
                ControlPipe, destinationId, destinationZone,
                DeclassifyPinsOf(proposal), sanitized, token),
            code => new OwnerDeclassifyPreviewResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyDeclassifyPreviewAnswer(epoch, result);
        Append(DescribeInboxActForLog("preview declassification", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    /// <summary>
    /// Publishes the sanitized content of the proposal under review into the
    /// owner's explicit destination once the owner types PUBLISH for exactly
    /// that content. The proposal's cited sources become the encrypted
    /// lineage pins; the shared copy itself carries no citations.
    /// </summary>
    public async Task PublishDeclassifiedAsync(
        string destinationId, string destinationZone, OwnerReviewDraft sanitized, string confirmation)
    {
        ArgumentNullException.ThrowIfNull(destinationId);
        ArgumentNullException.ThrowIfNull(destinationZone);
        ArgumentNullException.ThrowIfNull(sanitized);
        ArgumentNullException.ThrowIfNull(confirmation);
        var proposal = ReviewProposal;
        if (proposal is null || TryBeginInboxAct() is not { } epoch)
        {
            return;
        }

        if (!string.Equals(confirmation, "PUBLISH", StringComparison.Ordinal))
        {
            InboxActInFlight = false;
            DeclassifyMessage = "The shared zone is unchanged: publishing a sanitized derivative needs the exact confirmation PUBLISH for exactly this content.";
            RaiseChanged();
            return;
        }

        DeclassifyMessage = null;
        var result = await ExecuteInboxAsync(
            token => OwnerChannelClient.PublishDeclassifyProposalAsync(
                ControlPipe, proposal, destinationId, destinationZone, sanitized, token),
            code => new OwnerDeclassifyResult(OwnerCommandOutcome.Unavailable, code, null)).ConfigureAwait(true);

        await ProbeAsync().ConfigureAwait(true);
        ApplyDeclassifyAnswer(epoch, result);
        Append(DescribeInboxActForLog("publish declassification", result.Outcome, result.ErrorCode));
        RaiseChanged();
    }

    internal bool ApplyDeclassifyPreviewAnswer(long epoch, OwnerDeclassifyPreviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer("declassification preview");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Preview is not null)
        {
            DeclassifyPreview = result.Preview;
            DeclassifyMessage = "Preview only: the shared zone holds nothing yet. " +
                result.Preview.Pins.Length.ToString(CultureInfo.InvariantCulture) +
                " pinned private source(s), pins current: " +
                result.Preview.PinsCurrent.ToString(CultureInfo.InvariantCulture) + ".";
        }
        else
        {
            DeclassifyPreview = null;
            DeclassifyMessage = DescribeInboxAnswer("declassification preview", result.Outcome, result.ErrorCode);
            ApplyInboxRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }

    internal bool ApplyDeclassifyAnswer(long epoch, OwnerDeclassifyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (epoch != _privateEpoch)
        {
            AppendIgnoredStaleInboxAnswer("declassification publication");
            return false;
        }

        if (result.Outcome == OwnerCommandOutcome.Completed && result.Published is not null)
        {
            DeclassifyPublished = result.Published;
            DeclassifyMessage = "Published the sanitized shared copy " + result.Published.DestinationResourceId +
                " at revision " + result.Published.DestinationRevision.ToString(CultureInfo.InvariantCulture) +
                " with " + result.Published.LineageRelationIds.Length.ToString(CultureInfo.InvariantCulture) +
                " encrypted lineage relation(s); the shared copy carries no citations.";
        }
        else
        {
            DeclassifyPublished = null;
            DeclassifyMessage = DescribeInboxAnswer("declassification publication", result.Outcome, result.ErrorCode);
            ApplyInboxRefusal(result.Outcome, result.ErrorCode);
        }

        return true;
    }
    private static OwnerDeclassifyPin[] DeclassifyPinsOf(OwnerProposalDetail proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        // The preview pins are the cited sources of the proposal the owner
        // already opened on this screen; the publish call re-derives the same
        // set server-side from the recorded proposal, so a pin shown here can
        // never steer the lineage the service records.
        return proposal.Item.Sources
            .Where(source => !string.IsNullOrEmpty(source.SourceId) && source.SourceRevision is > 0)
            .Select(source => new OwnerDeclassifyPin(source.SourceId!, source.SourceRevision!.Value))
            .ToArray();
    }
    private void ClearPrivateDeclassifyState()
    {
        DeclassifyPreview = null;
        DeclassifyPublished = null;
        DeclassifyMessage = null;
    }
}
