using MetaBrain.Application;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

/// <summary>
/// S3-T6/S11-T4 owner-opt-in background incremental refresh loop. It runs in the service
/// process and shares the exact coordinator path with manual `source-refresh`. Standalone
/// sources require per-source consent; an opted-in folder may enumerate only its stored root
/// and include/exclude rules to admit matching regular members. Every read still uses the
/// enrolled-source reader and its final-path/reparse checks. The loop does not scan home or
/// profile locations, unlock autonomously, or use a plaintext key or private queue. It opens a
/// counted vault operation only while unlocked and commits through the encrypted journal
/// checkpoint. Source pause, revoke and explicit opt-out veto folder consent; folder opt-out
/// and vault lock cancel the active pass at a read boundary, zero partial bytes and leave the
/// checkpoint frozen. The persisted checkpoint makes the next pass/restart idempotent.
/// </summary>
internal sealed class SourceRefreshBackgroundService
{
    private static readonly TimeSpan RefreshPeriod = TimeSpan.FromSeconds(5);
    private readonly ServiceRequestHandler _handler;
    private readonly IVaultLifecycle _vault;

    public SourceRefreshBackgroundService(ServiceRequestHandler handler, IVaultLifecycle vault)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var lastEpoch = -1L;
        while (!stoppingToken.IsCancellationRequested)
        {
            CancellationToken refreshToken;
            long epoch;
            try
            {
                refreshToken = _vault.RefreshCancellation;
                epoch = _vault.LockEpoch;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (!string.Equals(_vault.State, "unlocked", StringComparison.Ordinal) || refreshToken.IsCancellationRequested)
            {
                lastEpoch = epoch;
                await DelayWithoutRefreshSignal(refreshToken, stoppingToken).ConfigureAwait(false);
                continue;
            }

            lastEpoch = epoch;
            // Serialize with owner request/reply traffic: the background pass opens
            // its counted vault operation only when no owner pipe request holds one.
            // The pipe layer owns request leases; this loop must never race a manual
            // refresh/ingest on the same journal revision, so it backs off whenever
            // the vault reports an active operation and retries next period from the
            // persisted checkpoint without duplicating committed events.
            if (!BackgroundRefreshGate.TryEnter())
            {
                await DelayWithoutRefreshSignal(refreshToken, stoppingToken).ConfigureAwait(false);
                continue;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, refreshToken);
            var passToken = linked.Token;
            var releaseGate = true;
            try
            {
                using var operation = _vault.TryBeginOperation();
                if (operation is null)
                {
                    await DelayWithoutRefreshSignal(refreshToken, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Stale-lease guard: the vault may have locked and unlocked between
                // the state check and the lease acquisition. The pass runs only when
                // the live epoch still matches the sampled one; otherwise the lease
                // is dropped unused and the loop resamples.
                if (_vault.LockEpoch != lastEpoch)
                {
                    await DelayWithoutRefreshSignal(refreshToken, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                ServiceReply reply;
                try
                {
                    reply = await _handler.HandleAsync(
                        AuthenticatedContext.ForOwner(),
                        new ServiceRequest(
                            ServiceRequestHandler.CurrentProtocolVersion,
                            ServiceRequestHandler.SourceRefreshOperation),
                        operation,
                        passToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (refreshToken.IsCancellationRequested || stoppingToken.IsCancellationRequested)
                {
                    await DelayWithoutRefreshSignal(refreshToken, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                _ = reply;
            }
            catch (ObjectDisposedException)
            {
                releaseGate = false;
                BackgroundRefreshGate.Exit();
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // A failed-closed background pass (malformed state, torn journal)
                // never stops the loop: the next period retries from the persisted
                // checkpoint without duplicating committed events.
            }
            finally
            {
                if (releaseGate)
                {
                    BackgroundRefreshGate.Exit();
                }
            }
            try
            {
                await Task.Delay(RefreshPeriod, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            if (!refreshToken.IsCancellationRequested)
            {
                continue;
            }

            await DelayWithoutRefreshSignal(refreshToken, stoppingToken).ConfigureAwait(false);
        }
    }

    private static async Task DelayWithoutRefreshSignal(CancellationToken refreshToken, CancellationToken stoppingToken)
    {
        try
        {
            if (refreshToken.IsCancellationRequested)
            {
                await Task.Delay(RefreshPeriod, stoppingToken).ConfigureAwait(false);
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, refreshToken);
            await Task.Delay(RefreshPeriod, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
