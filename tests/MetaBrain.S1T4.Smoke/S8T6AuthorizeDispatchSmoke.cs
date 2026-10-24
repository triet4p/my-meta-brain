namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    internal static async Task RunS8T6AuthorizeDispatchAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S8-T6 authorization dispatch smoke requires the Windows connections executable.");
        }

        Console.WriteLine("RUN S8-T6 authorization and revoke-session dispatch without a service pipe server");
        var sessionFile = Path.Combine(
            Path.GetTempPath(),
            "metabrain-s8t6-missing-session-" + Guid.NewGuid().ToString("N") + ".session");
        var authorizeResponse = await RunBridgeToolAsync(
            connectionsExecutable,
            "MetaBrainS8T6AuthorizeNoPipe.agent",
            "metabrain_authorize",
            new
            {
                sessionFile,
                operation = "memory.read",
                resourceId = "s8t6-authorize-dispatch-probe",
                resourceRevision = "1",
            }).ConfigureAwait(false);

        Require(authorizeResponse.Contains("\"isError\":true", StringComparison.Ordinal) &&
                authorizeResponse.Contains("ERROR session_unreadable", StringComparison.Ordinal) &&
                !authorizeResponse.Contains("Usage: owner", StringComparison.Ordinal),
            "The MCP authorization call did not pass CLI dispatch and argument parsing before reading its session file.");

        var missingPipe = "MetaBrainS8T6NoServer" + Guid.NewGuid().ToString("N");
        var revokeResult = await RunOwnerCommandAsync(
            connectionsExecutable,
            "owner", "revoke-session",
            "--control-pipe", missingPipe,
            "--session-id", Guid.NewGuid().ToString("N")).ConfigureAwait(false);
        Require(revokeResult.ExitCode == 3 &&
                revokeResult.StandardError.Contains("ERROR transport_unavailable", StringComparison.Ordinal) &&
                !revokeResult.StandardError.Contains("Usage: owner", StringComparison.Ordinal),
            "The owner revoke-session command did not pass dispatch and argument parsing before its missing-pipe refusal.");

        Console.WriteLine("PASS metabrain_authorize reaches the owner CLI parser without starting or contacting a service pipe");
        Console.WriteLine("PASS owner revoke-session reaches the command handler and refuses a missing pipe without starting a pipe server");
    }
}
