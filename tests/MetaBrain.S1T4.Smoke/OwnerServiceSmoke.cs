using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Runtime.ExceptionServices;

namespace MetaBrain.S1T4.Smoke;

internal static class OwnerServiceSmoke
{
    private static readonly TimeSpan ServiceReadyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

    public static async Task RunAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The retained owner service smoke is Windows-only.");
        }

        var fixture = SmokeFixture.Create();
        using var service = new Process
        {
            StartInfo = CreateServiceStartInfo(connectionsExecutable, fixture.SettingsPath)
        };
        var started = false;
        Task? standardOutputDrain = null;
        Task<string>? standardErrorDrain = null;
        Exception? failure = null;
        try
        {
            started = service.Start();
            if (!started)
            {
                throw new InvalidOperationException("The owner service process did not start.");
            }

            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            standardOutputDrain = DrainServiceOutputAsync(service.StandardOutput, ready);
            standardErrorDrain = service.StandardError.ReadToEndAsync();
            var exited = service.WaitForExitAsync();
            var startup = await Task.WhenAny(ready.Task, exited).WaitAsync(ServiceReadyTimeout).ConfigureAwait(false);
            if (startup == exited)
            {
                throw new InvalidOperationException("The owner service exited before it became ready.");
            }

            await ready.Task.ConfigureAwait(false);
            var status = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(status.ExitCode == 0 && status.StandardOutput.Contains("SERVICE running", StringComparison.Ordinal),
                "The owner CLI did not report the live service as running.");
            Console.WriteLine("PASS actual owner CLI status (exit 0)");

            var readPath = Path.Combine(fixture.OutputDirectory, "owner-read.bin");
            var read = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", SmokeFixture.ResourceId, "--output-file", readPath).ConfigureAwait(false);
            Require(read.ExitCode == 0 && File.Exists(readPath) &&
                    File.ReadAllBytes(readPath).AsSpan().SequenceEqual(fixture.ExpectedContent),
                "The owner CLI did not return the synthetic managed resource bytes.");
            Console.WriteLine($"PASS actual owner CLI managed-resource read (exit 0; bytes={fixture.ExpectedContent.Length})");

            var deniedPath = Path.Combine(fixture.OutputDirectory, "denied-read.bin");
            var denied = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", SmokeFixture.UnknownResourceId, "--output-file", deniedPath).ConfigureAwait(false);
            Require(denied.ExitCode == 4 && !File.Exists(deniedPath) &&
                    denied.StandardError.Contains("ERROR resource_unavailable", StringComparison.Ordinal),
                "An unknown managed-resource request was not denied without creating an output file.");
            Console.WriteLine("PASS unknown managed-resource denial (exit 4; generic error; no content file)");
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (started)
            {
                try
                {
                    await StopOwnedProcessAsync(service, standardOutputDrain, standardErrorDrain).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = failure is null ? ex : new AggregateException(failure, ex);
                }
            }

            try
            {
                fixture.Dispose();
            }
            catch (Exception ex)
            {
                failure = failure is null ? ex : new AggregateException(failure, ex);
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        Console.WriteLine("PASS service process reaped and synthetic fixture removed");
        Console.WriteLine("LIMIT owner-pipe SID access is not owner intent or agent isolation; agent access remains unavailable until token cutover. No vault encryption or token behavior is claimed.");
    }

    private static ProcessStartInfo CreateServiceStartInfo(string executable, string settingsPath)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("serve-console");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(settingsPath);
        return start;
    }

    private static async Task DrainServiceOutputAsync(StreamReader reader, TaskCompletionSource<bool> ready)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.Equals(line, "READY protocol=1 channel=owner-only", StringComparison.Ordinal))
            {
                ready.TrySetResult(true);
            }
        }
    }

    private static async Task<CommandResult> RunOwnerCommandAsync(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("An owner CLI process did not start.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(CommandTimeout).ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError).WaitAsync(CleanupTimeout).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            var cleanupFailure = await ReapProcessAsync(process, standardOutput, standardError).ConfigureAwait(false);
            if (cleanupFailure is not null)
            {
                throw new AggregateException(failure, cleanupFailure);
            }

            throw;
        }

        return new CommandResult(process.ExitCode, await standardOutput.ConfigureAwait(false), await standardError.ConfigureAwait(false));
    }

    private static async Task StopOwnedProcessAsync(Process process, Task? standardOutputDrain, Task<string>? standardErrorDrain)
    {
        var failure = await ReapProcessAsync(process, standardOutputDrain, standardErrorDrain).ConfigureAwait(false);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static async Task<Exception?> ReapProcessAsync(Process process, Task? standardOutputDrain, Task<string>? standardErrorDrain)
    {
        Exception? failure = null;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }
        catch (Win32Exception) when (process.HasExited)
        {
        }
        catch (Exception ex)
        {
            RecordFailure(ref failure, ex);
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(CleanupTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordFailure(ref failure, ex);
        }

        if (standardOutputDrain is not null)
        {
            try
            {
                await standardOutputDrain.WaitAsync(CleanupTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordFailure(ref failure, ex);
            }
        }

        if (standardErrorDrain is not null)
        {
            try
            {
                _ = await standardErrorDrain.WaitAsync(CleanupTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordFailure(ref failure, ex);
            }
        }

        return failure;
    }

    private static void RecordFailure(ref Exception? failure, Exception next)
    {
        failure = failure is null ? next : new AggregateException(failure, next);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class SmokeFixture : IDisposable
{
    private static readonly SecurityIdentifier SystemSid = new("S-1-5-18");
    private readonly SecurityIdentifier _ownerSid;
    private bool _disposed;

    private SmokeFixture(string root, SecurityIdentifier ownerSid, string settingsPath, string controlPipe, string outputDirectory, byte[] expectedContent)
    {
        Root = root;
        _ownerSid = ownerSid;
        SettingsPath = settingsPath;
        ControlPipe = controlPipe;
        OutputDirectory = outputDirectory;
        ExpectedContent = expectedContent;
    }

    public const string ResourceId = "fixture-owner";
    public const string UnknownResourceId = "fixture-unknown";
    public string Root { get; }
    public string SettingsPath { get; }
    public string ControlPipe { get; }
    public string OutputDirectory { get; }
    public byte[] ExpectedContent { get; }

    public static SmokeFixture Create()
    {
        var ownerSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows token has no user SID.");
        var root = Path.Combine(Path.GetTempPath(), "MetaBrain-S1-CLEAN-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root) || File.Exists(root))
        {
            throw new IOException("The generated synthetic fixture path already exists.");
        }

        Directory.CreateDirectory(root);
        try
        {
            SetRestrictedDirectoryAcl(root, ownerSid);
            var serviceDirectory = Path.Combine(root, "service");
            var resourceRoot = Path.Combine(serviceDirectory, "resources");
            var outputDirectory = Path.Combine(root, "output");
            Directory.CreateDirectory(serviceDirectory);
            Directory.CreateDirectory(resourceRoot);
            Directory.CreateDirectory(outputDirectory);
            SetRestrictedDirectoryAcl(serviceDirectory, ownerSid);
            SetRestrictedDirectoryAcl(resourceRoot, ownerSid);
            SetRestrictedDirectoryAcl(outputDirectory, ownerSid);

            const string resourceFileName = "owner-fixture.txt";
            var resourcePath = Path.Combine(resourceRoot, resourceFileName);
            var expectedContent = Encoding.UTF8.GetBytes("synthetic S1-CLEAN owner-read resource 41bd08.");
            File.WriteAllBytes(resourcePath, expectedContent);
            SetRestrictedFileAcl(resourcePath, ownerSid);

            var controlPipe = "MetaBrainS1Clean-" + Guid.NewGuid().ToString("N");
            var settingsPath = Path.Combine(serviceDirectory, "service-settings.json");
            var settings = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                ownerSid = ownerSid.Value,
                controlPipe,
                managedResources = new[]
                {
                    new { resourceId = ResourceId, zoneId = "fixture-zone", fileName = resourceFileName }
                }
            });
            File.WriteAllText(settingsPath, settings, new UTF8Encoding(false));
            SetRestrictedFileAcl(settingsPath, ownerSid);

            return new SmokeFixture(root, ownerSid, settingsPath, controlPipe, outputDirectory, expectedContent);
        }
        catch
        {
            DeleteOwnedRoot(root);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DeleteOwnedRoot(Root);
    }

    private static void SetRestrictedDirectoryAcl(string path, SecurityIdentifier ownerSid)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(ownerSid);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(ownerSid, FileSystemRights.FullControl, inheritance,
            PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inheritance,
            PropagationFlags.None, AccessControlType.Allow));
        System.IO.FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
    }

    private static void SetRestrictedFileAcl(string path, SecurityIdentifier ownerSid)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(ownerSid);
        security.AddAccessRule(new FileSystemAccessRule(ownerSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        System.IO.FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
    }

    private static void DeleteOwnedRoot(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var temporaryDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var expectedPrefix = temporaryDirectory + Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullRoot).StartsWith("MetaBrain-S1-CLEAN-", StringComparison.Ordinal) ||
            !Directory.Exists(fullRoot))
        {
            return;
        }

        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The synthetic fixture root became a reparse point; refusing recursive cleanup.");
        }

        Directory.Delete(fullRoot, recursive: true);
    }
}
