using System.Security.Cryptography;
using MetaBrain.Application;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

/// <summary>
/// S6-T6 owner-opt-in synthesis schedule loop. The loop runs inside the service
/// process alongside the named-pipe listeners and the source-refresh pass, wakes
/// periodically, and evaluates the owner's stored schedule through exactly the
/// same operation an explicit owner request uses
/// (<see cref="ServiceRequestHandler.OwnerScheduleRunOperation"/>): at most one
/// bounded synthesis job when the schedule is enabled and due, and nothing at
/// all otherwise.
/// <para>
/// The loop holds no policy of its own and no key or queue: it samples the live
/// lock epoch and the refresh-cancellation signal, does nothing while the vault
/// is not unlocked, opens a counted vault operation only when no owner request
/// holds the shared background gate, and drops the lease unused if the epoch
/// moved. A lock therefore pauses the schedule instead of triggering an unlock,
/// and a scheduled run that is interrupted by a lock is left to the schedule's
/// own due window (advanced by the pass itself) instead of being replayed.
/// Because the due window is advanced from the finish of each attempt, a slot
/// missed while locked, while the service was down, or while the previous run
/// was still executing is never queued as extra catch-up runs.
/// </para>
/// </summary>
internal sealed class SynthesisScheduleBackgroundService
{
    private static readonly TimeSpan PollPeriod = TimeSpan.FromSeconds(5);
    private readonly ServiceRequestHandler _handler;
    private readonly IVaultLifecycle _vault;
    private string? _passNote;

    public SynthesisScheduleBackgroundService(ServiceRequestHandler handler, IVaultLifecycle vault)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    }

    /// <summary>
    /// Reports a state of the pass that is not a due evaluation — a paused vault,
    /// a vault held by an owner request, a superseded lifetime signal, or a pass
    /// that failed before it could evaluate. Each distinct note is printed once
    /// (the next evaluating pass clears it), so a paused or failing pass is
    /// visible in the service transcript without turning it into periodic noise,
    /// and every non-sensitive state is reported exactly as its type/code.
    /// Cancellation and authority failures are never turned into a silent
    /// success: they surface here, and no scheduled run is claimed for a pass
    /// that produced none.
    /// </summary>
    private void ReportPassNote(string note)
    {
        if (string.Equals(_passNote, note, StringComparison.Ordinal))
        {
            return;
        }

        _passNote = note;
        Console.WriteLine("SCHEDULE " + note);
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("SCHEDULE pass started (interval-bound owner schedule, 5s poll)");
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
                ReportPassNote("paused: vault is not unlocked");
                await DelayAsync(refreshToken, stoppingToken).ConfigureAwait(false);
                continue;
            }

            // Owner request/reply traffic wins: the pass evaluates the schedule
            // only when no owner pipe request holds the shared gate, and backs off
            // to the next period otherwise. The gate is the same one the source
            // refresh pass uses, so two background writers never race on one
            // encrypted revision.
            if (!BackgroundRefreshGate.TryEnter())
            {
                ReportPassNote("paused: an owner request holds the vault lease");
                await DelayAsync(refreshToken, stoppingToken).ConfigureAwait(false);
                continue;
            }

            var releaseGate = true;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, refreshToken);
            try
            {
                using var operation = _vault.TryBeginOperation();
                if (operation is null)
                {
                    ReportPassNote("paused: no vault lease is available");
                    await DelayAsync(refreshToken, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Stale-lease guard: the vault may have locked and unlocked between
                // the state sample and the lease. A lease for a superseded epoch is
                // dropped unused and the loop resamples.
                if (_vault.LockEpoch != epoch)
                {
                    ReportPassNote("paused: the vault lock epoch moved");
                    await DelayAsync(refreshToken, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                ServiceReply reply;
                try
                {
                    reply = await _handler.HandleAsync(
                        AuthenticatedContext.ForOwner(),
                        new ServiceRequest(
                            ServiceRequestHandler.CurrentProtocolVersion,
                            ServiceRequestHandler.OwnerScheduleRunOperation),
                        operation,
                        linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (refreshToken.IsCancellationRequested || stoppingToken.IsCancellationRequested)
                {
                    await DelayAsync(refreshToken, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                ReportPass(reply);
            }
            catch (ObjectDisposedException)
            {
                releaseGate = false;
                BackgroundRefreshGate.Exit();
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                or ManagedResourceUnavailableException or CryptographicException)
            {
                // A failed-closed pass (a lock between the checks, a malformed
                // record, a torn write) never stops the loop and never becomes a
                // fake run: the next period retries the same due evaluation.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nothing in one pass may silently end scheduling for the rest of
                // the process lifetime, and no unexpected failure may pass
                // unreported: it is named once and the next period retries the
                // same due evaluation. Cancellation is never swallowed here — the
                // pass's own cancellation paths are handled above, and any other
                // cancellation still ends the pass.
                ReportPassNote("pass failed: " + ex.GetType().Name);
            }
            finally
            {
                if (releaseGate)
                {
                    BackgroundRefreshGate.Exit();
                }
            }

            await DelayAsync(refreshToken, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports one pass that attempted a due evaluation. A pass that found the
    /// schedule not due, disabled or absent says nothing, so the service
    /// transcript shows real attempts only and never becomes periodic noise. A
    /// pass whose answer carried no run result at all is reported with the
    /// service's own status and error code, because that is exactly the state a
    /// refusal (locked vault, denied authority, invalid record) must not hide in
    /// silence. Nothing here carries a key, a token or any content.
    /// </summary>
    private void ReportPass(ServiceReply reply)
    {
        var run = reply.ScheduleRun;
        if (run is null)
        {
            ReportPassNote(
                "pass status=" + (reply.Status ?? "(none)") +
                " error=" + (reply.Error ?? "(none)"));
            return;
        }

        // A pass that produced a run result evaluated the owner's schedule, so a
        // later pause or failure is a new transition to report.
        _passNote = null;
        if (run.Outcome is "not_due" or "not_configured" or "disabled")
        {
            return;
        }

        Console.WriteLine(
            "SCHEDULE outcome=" + run.Outcome +
            (run.JobId is null ? string.Empty : " job=" + run.JobId) +
            (run.JobState is null ? string.Empty : " state=" + run.JobState) +
            (run.FailureCode is null ? string.Empty : " failure=" + run.FailureCode));
    }

    private async Task DelayAsync(CancellationToken refreshToken, CancellationToken stoppingToken)
    {
        try
        {
            if (refreshToken.IsCancellationRequested)
            {
                await Task.Delay(PollPeriod, stoppingToken).ConfigureAwait(false);
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, refreshToken);
            await Task.Delay(PollPeriod, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A requested lock (or shutdown) ends the wait; the loop resamples the
            // lock state instead of treating cancellation as a failure.
        }
        catch (ObjectDisposedException)
        {
            // The lifetime signal was replaced (a lock or unlock moved the epoch)
            // while this wait was pending: the wait ends, the reason is reported
            // once, and the loop resamples instead of the pass dying on a
            // superseded signal.
            ReportPassNote("paused: the lifetime signal was replaced");
        }
    }
}
