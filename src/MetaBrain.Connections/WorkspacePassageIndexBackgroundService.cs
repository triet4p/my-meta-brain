using System.Security.Cryptography;
using MetaBrain.Core.Index;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

/// <summary>
/// Coalesces successful workspace mutations into an unlocked-epoch incremental
/// passage-index refresh. The model path is owner-selected, process-memory-only,
/// and discarded at every lock or host exit.
/// </summary>
internal sealed class WorkspacePassageIndexBackgroundService
{
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly IVaultLifecycle _vault;
    private readonly IWorkspacePassageIndexEngine _index;
    private string? _modelDirectory;
    private long _modelEpoch = -1;
    private bool _pending;

    internal WorkspacePassageIndexBackgroundService(
        IVaultLifecycle vault,
        IWorkspacePassageIndexEngine index)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    internal void ObserveExplicitModelDirectory(string? modelDirectory, bool generationIsCurrent = false)
    {
        if (string.IsNullOrWhiteSpace(modelDirectory) ||
            !TryGetUnlockedEpoch(out var epoch))
        {
            return;
        }

        lock (_stateGate)
        {
            if (!IsUnlockedAtEpoch(epoch))
            {
                return;
            }

            ResetForEpochLocked(epoch);
            _modelDirectory = modelDirectory;
            if (generationIsCurrent)
            {
                _pending = false;
            }
        }

        Signal();
    }
    internal void RequestAfterSuccessfulMutation()
    {
        if (!TryGetUnlockedEpoch(out var epoch))
        {
            return;
        }

        lock (_stateGate)
        {
            if (!IsUnlockedAtEpoch(epoch))
            {
                return;
            }

            ResetForEpochLocked(epoch);
            _pending = true;
        }

        Signal();
    }


    internal void ClearForEpochTransition()
    {
        long epoch;
        try
        {
            epoch = _vault.LockEpoch;
        }
        catch (ObjectDisposedException)
        {
            epoch = -1;
        }

        lock (_stateGate)
        {
            _modelDirectory = null;
            _pending = false;
            _modelEpoch = epoch;
        }

        Signal();
    }

    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
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

                lock (_stateGate)
                {
                    ResetForEpochLocked(epoch);
                }

                if (!string.Equals(_vault.State, "unlocked", StringComparison.Ordinal) || refreshToken.IsCancellationRequested)
                {
                    await _signal.WaitAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                string? modelDirectory;
                bool hasPendingWork;
                lock (_stateGate)
                {
                    modelDirectory = _modelDirectory;
                    hasPendingWork = _pending && modelDirectory is not null;
                    if (hasPendingWork)
                    {
                        _pending = false;
                    }
                }

                if (!hasPendingWork || modelDirectory is null)
                {
                    await WaitForSignalAsync(refreshToken, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, refreshToken);
                var retryAfterSnapshotMove = false;
                try
                {
                    using var operation = _vault.TryBeginOperation();
                    if (operation is null || _vault.LockEpoch != epoch ||
                        !string.Equals(_vault.State, "unlocked", StringComparison.Ordinal))
                    {
                        ClearForEpochTransition();
                        continue;
                    }

                    _ = _index.RebuildIncremental(
                        operation,
                        modelDirectory,
                        BackgroundRefreshGate.EnterForWorkSlice,
                        linkedCancellation.Token);
                }
                catch (OperationCanceledException) when (
                    stoppingToken.IsCancellationRequested || refreshToken.IsCancellationRequested)
                {
                    ClearForEpochTransition();
                }
                catch (WorkspacePassageIndexException ex)
                {
                    Console.Error.WriteLine("workspace passage background refresh refused (" + ex.Code + ").");
                    retryAfterSnapshotMove = string.Equals(
                        ex.Code, "workspace_index_canonical_moved", StringComparison.Ordinal);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                    CryptographicException or ArgumentException or ObjectDisposedException)
                {
                    var code = ex is InvalidDataException invalidData
                        ? invalidData.Message
                        : ex is CryptographicException
                            ? "workspace_index_generation_authentication_failed"
                            : "workspace_index_background_refresh_failed";
                    Console.Error.WriteLine("workspace passage background refresh failed (" + code + ").");
                }

                if (stoppingToken.IsCancellationRequested || refreshToken.IsCancellationRequested ||
                    _vault.LockEpoch != epoch || !string.Equals(_vault.State, "unlocked", StringComparison.Ordinal))
                {
                    ClearForEpochTransition();
                    continue;
                }

                if (retryAfterSnapshotMove)
                {
                    lock (_stateGate)
                    {
                        if (_modelEpoch == epoch && _modelDirectory is not null)
                        {
                            _pending = true;
                        }
                    }
                }

                lock (_stateGate)
                {
                    hasPendingWork = _pending && _modelDirectory is not null;
                }

                if (!hasPendingWork)
                {
                    await WaitForSignalAsync(refreshToken, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_stateGate)
            {
                _modelDirectory = null;
                _pending = false;
                _modelEpoch = -1;
            }
        }
    }

    private bool TryGetUnlockedEpoch(out long epoch)
    {
        try
        {
            epoch = _vault.LockEpoch;
            return string.Equals(_vault.State, "unlocked", StringComparison.Ordinal) &&
                !_vault.RefreshCancellation.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            epoch = -1;
            return false;
        }
    }

    private bool IsUnlockedAtEpoch(long epoch)
    {
        try
        {
            return _vault.LockEpoch == epoch &&
                string.Equals(_vault.State, "unlocked", StringComparison.Ordinal) &&
                !_vault.RefreshCancellation.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void ResetForEpochLocked(long epoch)
    {
        if (_modelEpoch == epoch)
        {
            return;
        }

        _modelDirectory = null;
        _pending = false;
        _modelEpoch = epoch;
    }

    private async Task WaitForSignalAsync(CancellationToken refreshToken, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, refreshToken);
        try
        {
            await _signal.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (refreshToken.IsCancellationRequested || stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }
}
