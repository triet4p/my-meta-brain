using System.Security.Cryptography;
using MetaBrain.Application;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

/// <summary>
/// The service's private time-trigger pass. It holds no key, queue or intention data:
/// each unlocked pass enters the ordinary counted vault operation and calls the same
/// owner schedule operation used by deterministic controlled-time verification.
/// </summary>
internal sealed class IntentionScheduleBackgroundService
{
    private static readonly TimeSpan PollPeriod = TimeSpan.FromSeconds(5);
    private readonly ServiceRequestHandler _handler;
    private readonly IVaultLifecycle _vault;
    private string? _lastFailure;

    public IntentionScheduleBackgroundService(ServiceRequestHandler handler, IVaultLifecycle vault)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("INTENTION-SCHEDULER pass started (private time triggers, 5s poll)");
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
                await DelayAsync(stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (!BackgroundRefreshGate.TryEnter())
            {
                await DelayAsync(stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, refreshToken);
                using var operation = _vault.TryBeginOperation();
                if (operation is null || _vault.LockEpoch != epoch)
                {
                    continue;
                }

                var reply = await _handler.HandleAsync(
                    AuthenticatedContext.ForOwner(),
                    new ServiceRequest(
                        ServiceRequestHandler.CurrentProtocolVersion,
                        ServiceRequestHandler.OwnerIntentionScheduleRunOperation),
                    operation,
                    linked.Token).ConfigureAwait(false);
                if (reply.Status == "intention_schedule_pass")
                {
                    _lastFailure = null;
                }
                else
                {
                    ReportFailure(reply.Error ?? reply.Status ?? "unavailable");
                }
            }
            catch (OperationCanceledException) when (refreshToken.IsCancellationRequested || stoppingToken.IsCancellationRequested)
            {
                // The pass did no further work after the locked epoch or service lifetime ended.
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException)
            {
                ReportFailure(ex.GetType().Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ReportFailure(ex.GetType().Name);
            }
            finally
            {
                BackgroundRefreshGate.Exit();
            }

            await DelayAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private void ReportFailure(string reason)
    {
        if (string.Equals(_lastFailure, reason, StringComparison.Ordinal))
        {
            return;
        }

        _lastFailure = reason;
        Console.WriteLine("INTENTION-SCHEDULER pass unavailable (" + reason + ")");
    }

    private static async Task DelayAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(PollPeriod, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown is sampled at the top of the loop.
        }
    }
}
