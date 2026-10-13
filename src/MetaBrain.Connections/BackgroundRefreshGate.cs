namespace MetaBrain.Connections;

/// <summary>
/// S3-T6-A2 mutual exclusion between owner pipe request leases and the
/// owner-opt-in background refresh pass. The vault lifecycle counts every open
/// lease but does not serialize them: two concurrent leases on the same journal
/// revision would race (the loser reports a journal conflict instead of
/// overwriting the winner). Owner request/reply traffic wins: a pipe request
/// waits out an in-flight background pass (bounded by its own request guard),
/// while the background loop backs off whenever a pipe request holds the gate.
/// A requested lock still stops the in-flight pass promptly through the
/// lifecycle's refresh-cancellation signal, so the wait never holds the vault
/// open. The gate carries no key, no content and no checkpoint — only mutual
/// exclusion — so lock/restart semantics are unchanged.
/// </summary>
internal static class BackgroundRefreshGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool TryEnter() => Gate.Wait(0);

    public static Task WaitAsync(CancellationToken cancellationToken) => Gate.WaitAsync(cancellationToken);

    public static void Release() => Gate.Release();

    public static void Exit() => Gate.Release();

    public static bool HasActiveOwnerOperation() => Gate.CurrentCount == 0;
}
