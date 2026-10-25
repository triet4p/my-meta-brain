namespace MetaBrain.Connections;

/// <summary>
/// Locates the shared host executable for a packaged product entrypoint. The
/// release layout keeps each product entrypoint in its own directory and the
/// shared runtime in <c>service/</c> beside them — <c>cli/</c>,
/// <c>owner-ui/</c>, <c>mcp/</c> and <c>installer/</c> all resolve the same
/// <c>service/MetaBrain.Connections.exe</c>. A developer build keeps the
/// executables side by side, and the shared host resolves to itself. This is
/// the only place that knows the product layout, so the three frontends and
/// the installer can never disagree about which host belongs to them.
/// </summary>
internal static class ProductHost
{
    internal const string FileName = "MetaBrain.Connections.exe";

    /// <summary>
    /// The host that belongs to the running product executable. An explicit
    /// path (the CLI's <c>--host</c>) is the caller's own decision and is not
    /// resolved here.
    /// </summary>
    internal static string Resolve(string? selfPath = null)
    {
        var self = selfPath ?? Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(self))
        {
            var selfFile = Path.GetFullPath(self);
            var selfDirectory = Path.GetDirectoryName(selfFile);
            if (!string.IsNullOrEmpty(selfDirectory))
            {
                // Release layout: <product directory>/../service/<host>. It is
                // checked before the flat candidate because a self-contained
                // publish of a project that references the host also copies the
                // host's apphost beside the product; the shared service/
                // directory is the one host every frontend must use.
                var productDirectory = Path.GetDirectoryName(selfDirectory);
                if (!string.IsNullOrEmpty(productDirectory))
                {
                    var packaged = Path.Combine(productDirectory, "service", FileName);
                    if (File.Exists(packaged))
                    {
                        return packaged;
                    }
                }

                var flat = Path.Combine(selfDirectory, FileName);
                if (File.Exists(flat))
                {
                    return flat;
                }
            }

            if (string.Equals(Path.GetFileName(selfFile), FileName, StringComparison.OrdinalIgnoreCase))
            {
                return selfFile;
            }
        }

        throw new InstanceLifecycleException(
            "host_missing",
            "The shared service host executable cannot be located.",
            "Unpack or install the release bundle so service/" + FileName + " sits beside this entrypoint, then try again.");
    }
}
