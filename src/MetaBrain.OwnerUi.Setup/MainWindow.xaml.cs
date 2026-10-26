using System.IO;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace MetaBrain.OwnerUi.Setup;

public partial class MainWindow : Window
{
    private const string PayloadResourceName = "MetaBrain.OwnerUi.Setup.Payload.zip";
    private static readonly Assembly SetupAssembly = typeof(MainWindow).Assembly;
    private static readonly string CandidateIdentity = Metadata("OwnerUiSetupCandidateIdentity");
    private static readonly string ExpectedPayloadSha256 = Metadata("OwnerUiSetupPayloadSha256");
    private bool _operationRunning;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Meta Brain Owner UI Setup " + SetupIdentity.ProductVersion;
        VersionText.Text = "Owner UI setup · version " + SetupIdentity.ProductVersion;
        var workArea = SystemParameters.WorkArea;
        MaxWidth = workArea.Width;
        MaxHeight = workArea.Height;
        MinWidth = Math.Min(580d, workArea.Width);
        MinHeight = Math.Min(420d, workArea.Height);
        Width = Math.Min(620d, workArea.Width);
        Height = Math.Min(640d, workArea.Height);
        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appRoot = Path.Combine(local, "MetaBrain", "App");
        var dataRoot = Path.Combine(local, "MetaBrain", "Data");
        var statePath = Path.Combine(appRoot, "install-state.json");
        if (File.Exists(statePath))
        {
            try
            {
                using var state = JsonDocument.Parse(File.ReadAllText(statePath));
                if (state.RootElement.TryGetProperty("dataRoot", out var existingData) && existingData.ValueKind == JsonValueKind.String)
                {
                    dataRoot = existingData.GetString() ?? dataRoot;
                }
            }
            catch (JsonException exception)
            {
                StatusText.Text = "The selected application location contains unreadable Meta Brain state (" + exception.Message + "). Choose a supported install or resolve the existing installation before continuing.";
            }
        }

        InstallRootBox.Text = appRoot;
        DataRootBox.Text = dataRoot;
        UpdateUninstallAvailability();
    }

    private static string Metadata(string key) => SetupAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == key)?.Value
        ?? throw new InvalidOperationException("Setup candidate metadata is missing: " + key);

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        string appRoot;
        string dataRoot;
        try
        {
            if (string.IsNullOrWhiteSpace(InstallRootBox.Text) || string.IsNullOrWhiteSpace(DataRootBox.Text))
            {
                throw new InvalidOperationException("Choose both an application location and an owner data location.");
            }

            appRoot = Path.GetFullPath(InstallRootBox.Text.Trim());
            dataRoot = Path.GetFullPath(DataRootBox.Text.Trim());
            if (PathsOverlap(appRoot, dataRoot))
            {
                throw new InvalidOperationException("Application and owner data locations must be separate sibling locations; neither can contain the other.");
            }

            await RunInstallAsync(appRoot, dataRoot);
        }
        catch (Exception exception)
        {
            var cleanupWarning = StatusText.Text.Contains("Temporary setup payload cleanup could not complete:", StringComparison.Ordinal)
                ? Environment.NewLine + StatusText.Text
                : string.Empty;
            StatusText.Text = "Setup did not complete: " + exception.Message + cleanupWarning + " Correct the issue and retry. No success is reported.";
            MessageBox.Show(this, StatusText.Text, "Owner UI setup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (LaunchAfterInstallBox.IsChecked == true)
        {
            var ownerUi = Path.Combine(appRoot, "owner-ui", "MetaBrain.OwnerUi.exe");
            try
            {
                using var process = Process.Start(new ProcessStartInfo(ownerUi) { UseShellExecute = true })
                    ?? throw new InvalidOperationException("Windows did not start the installed Owner UI.");
                StatusText.Text = "Owner UI installed and opened. Setup did not start or unlock a service.";
            }
            catch (Exception exception)
            {
                StatusText.Text = "Owner UI installation completed successfully, but Windows could not open the installed console: " + exception.Message;
                MessageBox.Show(this, StatusText.Text, "Owner UI could not be opened", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async Task RunInstallAsync(string appRoot, string dataRoot)
    {
        SetBusy(true, "Verifying the embedded release payload…");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "MetaBrain-OwnerUI-Setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            await ExtractPayloadAsync(temporaryRoot);
            var installer = Path.Combine(temporaryRoot, "installer", "MetaBrain.Installer.exe");
            var result = await RunSharedInstallerAsync(installer, temporaryRoot, new[]
            {
                "install", "--payload", temporaryRoot,
                "--install-root", appRoot,
                "--data-root", dataRoot,
                "--component", "owner-ui",
                "--candidate-id", CandidateIdentity
            });
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException("The shared installer refused or failed the Owner UI install (exit " + result.ExitCode + "). " + result.Output);
            }

            StatusText.Text = "Owner UI installed at " + Path.Combine(appRoot, "owner-ui", "MetaBrain.OwnerUi.exe") + ". Shared runtime and data locations are preserved.";
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                StatusText.Text += Environment.NewLine + "Temporary setup payload cleanup could not complete: " + temporaryRoot + " — " + exception.Message;
            }

            SetBusy(false, StatusText.Text);
        }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        string appRoot;
        string statePath;
        try
        {
            if (string.IsNullOrWhiteSpace(InstallRootBox.Text))
            {
                throw new InvalidOperationException("Choose the application location that contains the registered installation.");
            }

            appRoot = Path.GetFullPath(InstallRootBox.Text.Trim());
            statePath = Path.Combine(appRoot, "install-state.json");
            if (!File.Exists(statePath)) throw new InvalidOperationException("No registered shared installation was found at the selected application location.");
            if ((new DirectoryInfo(appRoot).Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Refusing removal through an application-location reparse point.");
            }

            using var currentState = JsonDocument.Parse(File.ReadAllText(statePath));
            var components = currentState.RootElement.TryGetProperty("components", out var componentArray) &&
                componentArray.ValueKind == JsonValueKind.Array
                ? componentArray.EnumerateArray().Select(component => component.GetString()).ToArray()
                : Array.Empty<string?>();
            if (!components.Contains("owner-ui", StringComparer.Ordinal))
            {
                throw new InvalidOperationException("The selected shared installation does not register an Owner UI component.");
            }

            if (!currentState.RootElement.TryGetProperty("dataRoot", out var existingDataRoot) ||
                existingDataRoot.ValueKind != JsonValueKind.String ||
                PathsOverlap(appRoot, existingDataRoot.GetString() ?? string.Empty))
            {
                throw new InvalidOperationException("The shared installation has an invalid or overlapping data location; no removal was attempted.");
            }

            if (string.Equals(appRoot.TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetPathRoot(appRoot)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to remove a drive-root application location.");
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Removal refused before changing files: " + exception.Message;
            MessageBox.Show(this, StatusText.Text, "Owner UI removal refused", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var confirm = MessageBox.Show(this,
            "Remove only the Owner UI from this shared installation? Other registered components and the owner data location will be kept.",
            "Confirm Owner UI removal", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            SetBusy(true, "Removing the Owner UI through the shared installer…");
            var installer = Path.Combine(appRoot, "installer", "MetaBrain.Installer.exe");
            if (!File.Exists(installer)) throw new FileNotFoundException("The matching shared installer is missing. Reinstall a matching release before removing this component.", installer);
            var result = await RunSharedInstallerAsync(installer, appRoot, new[] { "uninstall", "--component", "owner-ui", "--defer-root-removal" });
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException("The shared installer refused or failed Owner UI removal (exit " + result.ExitCode + "). " + result.Output);
            }

            var state = File.Exists(statePath) ? JsonDocument.Parse(File.ReadAllText(statePath)) : null;
            using (state)
            {
                var hasComponents = state?.RootElement.TryGetProperty("components", out var components) == true &&
                    components.ValueKind == JsonValueKind.Array && components.GetArrayLength() > 0;
                if (!hasComponents)
                {
                    if (Directory.Exists(appRoot)) Directory.Delete(appRoot, recursive: true);
                    if (Directory.Exists(appRoot)) throw new IOException("The final application directory remains; close related applications and retry cleanup.");
                }
            }

            UninstallButton.IsEnabled = false;
            StatusText.Text = "Owner UI removed. The shared runtime remains when another component is registered; the owner data location was preserved.";
        }
        catch (Exception exception)
        {
            StatusText.Text = "Removal did not complete: " + exception.Message + " No success is reported.";
            MessageBox.Show(this, StatusText.Text, "Owner UI removal failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, StatusText.Text);
        }
    }

    private static async Task ExtractPayloadAsync(string temporaryRoot)
    {
        await using var payload = SetupAssembly.GetManifestResourceStream(PayloadResourceName)
            ?? throw new InvalidOperationException("This setup executable does not contain its release payload.");
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(payload)).ToLowerInvariant();
        if (!string.Equals(digest, ExpectedPayloadSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The embedded candidate payload failed its SHA-256 integrity check.");
        payload.Position = 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: false);
        archive.ExtractToDirectory(temporaryRoot);
    }

    private static async Task<(int ExitCode, string Output)> RunSharedInstallerAsync(string executable, string workingDirectory, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The shared installer could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, (await stdout) + Environment.NewLine + (await stderr));
    }

    private static bool PathsOverlap(string first, string second)
    {
        var firstFull = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var secondFull = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return firstFull.StartsWith(secondFull, StringComparison.OrdinalIgnoreCase) ||
               secondFull.StartsWith(firstFull, StringComparison.OrdinalIgnoreCase);
    }

    private void BrowseInstall_Click(object sender, RoutedEventArgs e) => BrowseFolder(InstallRootBox);
    private void BrowseData_Click(object sender, RoutedEventArgs e) => BrowseFolder(DataRootBox);

    private void BrowseFolder(System.Windows.Controls.TextBox target)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder", FolderName = target.Text, Multiselect = false };
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_operationRunning) Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_operationRunning) Close();
    }

    private void InstallRoot_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateUninstallAvailability();

    private void UpdateUninstallAvailability()
    {
        if (_operationRunning)
        {
            UninstallButton.IsEnabled = false;
            return;
        }

        var installRoot = InstallRootBox.Text.Trim();
        UninstallButton.IsEnabled = !string.IsNullOrWhiteSpace(installRoot) &&
            File.Exists(Path.Combine(installRoot, "install-state.json"));
    }

    private void SetBusy(bool busy, string status)
    {
        _operationRunning = busy;
        InstallRootBox.IsEnabled = !busy;
        DataRootBox.IsEnabled = !busy;
        BrowseInstallButton.IsEnabled = !busy;
        BrowseDataButton.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy;
        UpdateUninstallAvailability();
        CancelButton.Content = busy ? "Working…" : "Cancel";
        CancelButton.IsEnabled = !busy;
        StatusText.Text = status;
    }
}
