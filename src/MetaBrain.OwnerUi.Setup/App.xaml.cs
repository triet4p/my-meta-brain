using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace MetaBrain.OwnerUi.Setup;

public partial class App : Application
{
    private const string PayloadResourceName = "MetaBrain.OwnerUi.Setup.Payload.zip";

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        if (e.Args.Length > 0 && e.Args[0] is "--help" or "-h")
        {
            MessageBox.Show("Meta Brain Owner UI Setup " + SetupIdentity.ProductVersion + "\nGraphical install/update/remove for the Owner UI. The setup is unsigned; SHA-256 does not authenticate its publisher.", "Owner UI setup help", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] is "--version" or "-v")
        {
            MessageBox.Show("Meta Brain Owner UI Setup " + SetupIdentity.ProductVersion, "Owner UI setup version", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--verify-payload")
        {
            try
            {
                VerifyPayload(e.Args[1]);
                Shutdown(0);
            }
            catch (Exception exception)
            {
                File.WriteAllText(e.Args[1], JsonSerializer.Serialize(new { error = exception.Message }));
                Shutdown(1);
            }

            return;
        }

        new MainWindow().Show();
    }

    private static void VerifyPayload(string outputPath)
    {
        var assembly = typeof(App).Assembly;
        using var payload = assembly.GetManifestResourceStream(PayloadResourceName)
            ?? throw new InvalidOperationException("Embedded Owner UI payload is missing.");
        var payloadBytes = payload.Length;
        var payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        payload.Position = 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).Select(entry =>
        {
            using var stream = entry.Open();
            return new { path = entry.FullName.Replace('\\', '/'), bytes = entry.Length, sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() };
        }).OrderBy(file => file.path, StringComparer.Ordinal).ToArray();
        File.WriteAllText(outputPath, JsonSerializer.Serialize(new { bytes = payloadBytes, sha256 = payloadHash, files }));
    }
}
