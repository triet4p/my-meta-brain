namespace MetaBrain.S1T4.Smoke;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var scopeGrantRun = args.Length == 2 && string.Equals(args[0], "scope-issue", StringComparison.Ordinal);
        var executable = scopeGrantRun ? args[1] : args.Length == 1 ? args[0] : null;
        if (executable is null || !File.Exists(executable))
        {
            Console.Error.WriteLine("Usage: MetaBrain.S1T4.Smoke <MetaBrain.Connections.exe> | scope-issue <MetaBrain.Connections.exe>");
            return 2;
        }

        try
        {
            var fullPath = Path.GetFullPath(executable);
            if (scopeGrantRun)
            {
                await OwnerServiceSmoke.RunScopeIssueAsync(fullPath).ConfigureAwait(false);
            }
            else
            {
                await OwnerServiceSmoke.RunAsync(fullPath).ConfigureAwait(false);
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
