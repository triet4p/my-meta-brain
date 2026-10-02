namespace MetaBrain.S1T4.Smoke;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: MetaBrain.S1T4.Smoke <MetaBrain.Connections.exe>");
            return 2;
        }

        try
        {
            await OwnerServiceSmoke.RunAsync(Path.GetFullPath(args[0])).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL owner service smoke (" + ex.GetType().Name + ": " + ex.Message + ")");
            return 1;
        }
    }
}
