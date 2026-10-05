namespace MetaBrain.S1T4.Smoke;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var scenario = args.Length == 2 &&
            (string.Equals(args[0], "scope-issue", StringComparison.Ordinal) ||
             string.Equals(args[0], "agent-session", StringComparison.Ordinal) ||
             string.Equals(args[0], "s1-t5", StringComparison.Ordinal) ||
             string.Equals(args[0], "s1-t6", StringComparison.Ordinal))
            ? args[0]
            : null;
        var executable = scenario is not null ? args[1] : args.Length == 1 ? args[0] : null;
        if (executable is null || !File.Exists(executable))
        {
            Console.Error.WriteLine("Usage: MetaBrain.S1T4.Smoke <MetaBrain.Connections.exe> | scope-issue|agent-session|s1-t5|s1-t6 <MetaBrain.Connections.exe>");
            return 2;
        }

        try
        {
            var fullPath = Path.GetFullPath(executable);
            switch (scenario)
            {
                case "scope-issue":
                    await OwnerServiceSmoke.RunScopeIssueAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s1-t5":
                    await OwnerServiceSmoke.RunT5SessionLifecycleAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s1-t6":
                    await OwnerServiceSmoke.RunT6ScopedReadAsync(fullPath).ConfigureAwait(false);
                    break;
                case "agent-session":
                    await OwnerServiceSmoke.RunAgentSessionAsync(fullPath).ConfigureAwait(false);
                    break;
                default:
                    await OwnerServiceSmoke.RunAsync(fullPath).ConfigureAwait(false);
                    break;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL owner service smoke (" + ex.GetType().Name + ": " + ex.Message + ")");
            return 1;
        }
    }
}
