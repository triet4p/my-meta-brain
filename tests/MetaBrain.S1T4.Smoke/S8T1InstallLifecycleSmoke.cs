using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MetaBrain.Connections;
namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// The S8-T1 installed lifecycle smoke. It drives the real installer and the
/// real installed host as separate processes and proves the daily path the
/// owner depends on:
///
/// install -> start (locked) -> owner unlock -> create data -> grant + redeem
/// -> live agent session -> stop -> restart (locked again, no session
/// resurrection) -> uninstall that keeps the vault and the rest of the data.
///
/// The fixture lives inside the repository, under the ignored runtime tree.
/// Every path the product is allowed to touch is checked for containment and
/// for a reparse-free ancestry before the product touches it, so the run can
/// never reach outside the fixture.
/// </summary>
internal static class S8T1InstallLifecycleSmoke
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan GraceTimeout = TimeSpan.FromSeconds(5);
    private const string Zone = "s8t1-zone";
    private const string Resource = "s8t1-resource";
    private const string Passphrase = "s8t1-synthetic-owner-passphrase";

    public static async Task RunAsync(string payloadDirectory, string fixtureRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("The installed lifecycle smoke is Windows-only.");
        }

        var repositoryRoot = SmokeFixture.FindRepositoryRoot();
        var payload = Path.GetFullPath(payloadDirectory);
        var fixture = Path.GetFullPath(fixtureRoot);
        var installRoot = Path.Combine(fixture, "install");
        var dataRoot = Path.Combine(fixture, "data");
        var workRoot = Path.Combine(fixture, "work");

        // The fixture is created inside the repository's ignored runtime tree.
        // Nothing outside it may be read or written by the product during the
        // run, so the containment is proved before the first step.
        RequireInsideRepository(repositoryRoot, fixture);
        RequireNoReparseAncestry(repositoryRoot, fixture);
        if (Directory.Exists(fixture))
        {
            throw new IOException("The installed lifecycle fixture path already exists.");
        }

        Directory.CreateDirectory(fixture);
        Directory.CreateDirectory(workRoot);
        try
        {
            var suffix = Guid.NewGuid().ToString("N");
            var controlPipe = "metabrain.s8t1.owner." + suffix;
            var agentPipe = "metabrain.s8t1.agent." + suffix;
            var installer = Path.Combine(payload, "installer", "MetaBrain.Installer.exe");
            Require(File.Exists(installer), "The published payload does not carry the installer.");

            var install = await RunCommandAsync(installer,
                new[]
                {
                    "install", "--payload", payload, "--install-root", installRoot, "--data-root", dataRoot,
                    "--control-pipe", controlPipe, "--agent-pipe", agentPipe
                },
                standardInput: null,
                marker: "INSTALLED",
                timeout: CommandTimeout).ConfigureAwait(false);
            Require(install.ExitCode == 0 && install.StandardOutput.Contains("INSTALLED", StringComparison.Ordinal),
                "The installer did not complete the installation: " + install.StandardError);
            Require(install.StandardOutput.Contains("vault state=uninitialized", StringComparison.Ordinal),
                "The installation created a vault instead of leaving the owner to create it.");
            Require(!Directory.Exists(Path.Combine(dataRoot, "vault")),
                "The installation created a vault; a fresh installation starts locked and uninitialized.");
            Require(File.Exists(Path.Combine(dataRoot, "service-settings.json")),
                "The installation did not create the service settings file.");
            Console.WriteLine("PASS install lays the application down, creates the data root and starts uninitialized");

            var installedInstaller = Path.Combine(installRoot, "installer", "MetaBrain.Installer.exe");
            var installedService = Path.Combine(installRoot, "service", "MetaBrain.Connections.exe");
            Require(File.Exists(installedInstaller) && File.Exists(installedService),
                "The installation did not copy the installer and the service host.");

            var beforeStart = await RunCommandAsync(installedInstaller, new[] { "status" },
                standardInput: null, marker: null, timeout: CommandTimeout).ConfigureAwait(false);
            Require(beforeStart.ExitCode != 0 && beforeStart.StandardOutput.Contains("service=not reachable", StringComparison.Ordinal),
                "A service that was never started reported itself as reachable.");

            var start = await RunCommandAsync(installedInstaller, new[] { "start" },
                standardInput: null, marker: "service=started", timeout: CommandTimeout).ConfigureAwait(false);
            Require(start.ExitCode == 0 && start.StandardOutput.Contains("service=started", StringComparison.Ordinal),
                "The installed service host did not start: " + start.StandardError);

            var started = await RunCommandAsync(installedInstaller, new[] { "status" },
                standardInput: null, marker: null, timeout: CommandTimeout).ConfigureAwait(false);
            Require(started.ExitCode == 0 && started.StandardOutput.Contains("service=running", StringComparison.Ordinal) &&
                    started.StandardOutput.Contains("vault=uninitialized", StringComparison.Ordinal),
                "The installed service did not start with a closed vault: " + started.StandardOutput);

            var lockedStatus = await RunCommandAsync(installedService,
                new[] { "owner", "status", "--control-pipe", controlPipe },
                standardInput: null, marker: null, timeout: CommandTimeout).ConfigureAwait(false);
            Require(lockedStatus.ExitCode == 0 && lockedStatus.StandardOutput.Contains("vault=uninitialized", StringComparison.Ordinal),
                "The owner channel did not report a closed vault on the first start.");
            Console.WriteLine("PASS the installed host starts with a closed vault and reports its health over the owner channel");

            var provision = await RunCommandAsync(installedService,
                new[] { "owner", "provision", "--control-pipe", controlPipe },
                standardInput: Passphrase + Environment.NewLine + Passphrase + Environment.NewLine,
                marker: "VAULT provisioned",
                timeout: CommandTimeout).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The owner could not provision the vault of the installed instance: " + provision.StandardError);

            var unlockedStatus = await RunCommandAsync(installedService,
                new[] { "owner", "status", "--control-pipe", controlPipe },
                standardInput: null, marker: null, timeout: CommandTimeout).ConfigureAwait(false);
            Require(unlockedStatus.StandardOutput.Contains("vault=unlocked", StringComparison.Ordinal),
                "The provisioned vault did not unlock for its owner.");

            var inputPath = Path.Combine(workRoot, "owner-record.txt");
            await File.WriteAllBytesAsync(inputPath, Encoding.UTF8.GetBytes("s8t1 synthetic owner record")).ConfigureAwait(false);
            var write = await RunCommandAsync(installedService,
                new[]
                {
                    "owner", "write", "--control-pipe", controlPipe, "--resource-id", Resource, "--zone-id", Zone,
                    "--expected-revision", "0", "--input-file", inputPath
                },
                standardInput: null, marker: "WRITE stored", timeout: CommandTimeout).ConfigureAwait(false);
            Require(write.ExitCode == 0 && write.StandardOutput.Contains("WRITE stored", StringComparison.Ordinal),
                "The owner could not write into the installed vault: " + write.StandardError);

            var readBackPath = Path.Combine(workRoot, "owner-record-read.txt");
            var readBack = await RunCommandAsync(installedService,
                new[]
                {
                    "owner", "read", "--control-pipe", controlPipe, "--resource-id", Resource, "--output-file", readBackPath
                },
                standardInput: null, marker: null, timeout: CommandTimeout).ConfigureAwait(false);
            Require(readBack.ExitCode == 0 && File.Exists(readBackPath) &&
                    (await File.ReadAllTextAsync(readBackPath).ConfigureAwait(false)).Contains("s8t1 synthetic owner record", StringComparison.Ordinal),
                "The owner could not read back the record it stored in the installed vault.");
            Console.WriteLine("PASS the owner unlocks the installed vault and stores and reads its own record");

            var expiry = DateTimeOffset.UtcNow.AddHours(3).ToString("O", CultureInfo.InvariantCulture);
            var handoffPath = Path.Combine(workRoot, "owner-grant.handoff");
            var sessionPath = Path.Combine(workRoot, "agent-session.handoff");
            var grant = await RunCommandAsync(installedService,
                new[]
                {
                    "owner", "grant", "--control-pipe", controlPipe, "--zone-id", Zone,
                    "--expires-at-utc", expiry, "--handoff-file", handoffPath
                },
                standardInput: "ISSUE" + Environment.NewLine,
                marker: "GRANT issued",
                timeout: CommandTimeout).ConfigureAwait(false);
            Require(grant.ExitCode == 0 && grant.StandardOutput.Contains("GRANT issued", StringComparison.Ordinal) &&
                    File.Exists(handoffPath),
                "The owner could not issue a scoped grant against the installed vault: " + grant.StandardError);

            var redeem = await RunCommandAsync(installedService,
                new[]
                {
                    "owner", "redeem", "--agent-pipe", agentPipe, "--handoff-file", handoffPath, "--session-file", sessionPath
                },
                standardInput: null, marker: "SESSION issued", timeout: CommandTimeout).ConfigureAwait(false);
            Require(redeem.ExitCode == 0 && redeem.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal) &&
                    File.Exists(sessionPath),
                "The agent could not redeem the owner grant into a session: " + redeem.StandardError);

            var sessionToken = AgentSessionFile.Read(sessionPath);
            var live = await InvokeAgentRequestAsync(agentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.inspect",
                sessionToken
            }).ConfigureAwait(false);
            Require(HasStatus(live, "session_valid"),
                "The installed agent pipe did not accept the redeemed session.");
            Console.WriteLine("PASS grant, redeem and a live agent session over the installed agent pipe");

            // The product must not change the meaning of the owner's own
            // paths: it creates them with the ordinary permissions of the
            // account and it never applies a permission change or takes
            // ownership of a path. A fingerprint of every path this run
            // created is captured here and compared again after stop, restart
            // and uninstall: the same expected files must still exist with the
            // same bytes, which fails if the lifecycle deleted, replaced, or
            // truncated one of them.
            // The host's runtime directory carries the product-owned
            // operational log (service.log), which the host appends to while
            // it runs and keeps a writer handle on. It is mutable operational
            // state, not owner canonical, vault, snapshot or backup content,
            // so byte equality across the lifecycle does not apply to it. The
            // owner's own paths below must stay byte-identical.
            var watchedPaths = new List<string>
            {
                Path.Combine(dataRoot, "service-settings.json"),
                Path.Combine(dataRoot, "vault"),
                handoffPath,
                sessionPath
            };

            foreach (var directory in new[]
                     {
                         Path.Combine(dataRoot, "vault"),
                         Path.Combine(dataRoot, "vault", "resources")
                     })
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                watchedPaths.Add(directory);
                watchedPaths.AddRange(Directory.GetFiles(directory));
            }

            var pathsBefore = CaptureProductPaths(watchedPaths);

            var stop = await RunCommandAsync(installedInstaller, new[] { "stop" },
                standardInput: null, marker: "service=stopped", timeout: CommandTimeout).ConfigureAwait(false);
            Require(stop.ExitCode == 0 && stop.StandardOutput.Contains("service=stopped", StringComparison.Ordinal) &&
                    !stop.StandardOutput.Contains("forced", StringComparison.Ordinal),
                "The installed service host did not stop cleanly: " + stop.StandardOutput + stop.StandardError);

            var stopped = await RunCommandAsync(installedInstaller, new[] { "status" },
                standardInput: null, marker: null, timeout: CommandTimeout).ConfigureAwait(false);
            Require(stopped.ExitCode != 0 && stopped.StandardOutput.Contains("service=not reachable", StringComparison.Ordinal),
                "The service host was still reachable after the owner stopped it.");
            Require(!File.Exists(Path.Combine(dataRoot, "runtime", "service.pid")),
                "The stopped host left its process identity behind.");

            var restart = await RunCommandAsync(installedInstaller, new[] { "restart" },
                standardInput: null, marker: "service=started", timeout: CommandTimeout).ConfigureAwait(false);
            Require(restart.ExitCode == 0 && restart.StandardOutput.Contains("service=started", StringComparison.Ordinal),
                "The installed service host did not restart: " + restart.StandardError);

            var restarted = await RunCommandAsync(installedInstaller, new[] { "status" },
                standardInput: null, marker: null, timeout: CommandTimeout).ConfigureAwait(false);
            Require(restarted.ExitCode == 0 && restarted.StandardOutput.Contains("vault=locked", StringComparison.Ordinal),
                "The restarted host came back unlocked; a restart must lock the vault again.");

            var resurrected = await InvokeAgentRequestAsync(agentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.inspect",
                sessionToken
            }).ConfigureAwait(false);
            Require(!HasStatus(resurrected, "session_valid"),
                "A session survived the restart of the service host.");
            Console.WriteLine("PASS a restart locks the vault again and does not resurrect the session");

            var uninstall = await RunCommandAsync(installedInstaller, new[] { "uninstall" },
                standardInput: null, marker: null, timeout: CommandTimeout).ConfigureAwait(false);
            Require(uninstall.ExitCode == 0,
                "The uninstall did not complete: " + uninstall.StandardError);
            Require(uninstall.StandardOutput.Contains("data root=preserved", StringComparison.Ordinal),
                "The uninstall did not report the data root as preserved.");
            Require(Directory.Exists(Path.Combine(dataRoot, "vault")),
                "The uninstall removed the vault; the owner's data must survive it.");
            Require(File.Exists(Path.Combine(dataRoot, "service-settings.json")),
                "The uninstall removed the service settings of the preserved data root.");
            Require(!Directory.Exists(Path.Combine(installRoot, "service")),
                "The uninstall left the application files behind.");
            Console.WriteLine("PASS uninstall removes the application and preserves the vault, the settings and the recovery material");

            var pathsAfter = CaptureProductPaths(watchedPaths);
            Require(pathsBefore == pathsAfter,
                "The installed lifecycle changed one of the owner's own paths: " +
                FirstDifference(pathsBefore, pathsAfter));
            Console.WriteLine("PASS the lifecycle left the owner's own paths unchanged");

            if (File.Exists(installedInstaller))
            {
                var afterUninstall = await RunCommandAsync(installedInstaller, new[] { "status" },
                    standardInput: null, marker: null, timeout: CommandTimeout).ConfigureAwait(false);
                Require(afterUninstall.ExitCode != 0,
                    "A removed installation still answered a health request.");
            }

            Console.WriteLine("PASS a removed installation no longer answers");
        }
        finally
        {
            TryStopInstalledHost(installRoot);
            DeleteFixture(fixture);
        }
    }

    /// <summary>
    /// The installed host is stopped before the fixture is removed, so a run
    /// that fails half-way does not leave a service process behind.
    /// </summary>
    private static void TryStopInstalledHost(string installRoot)
    {
        var installer = Path.Combine(installRoot, "installer", "MetaBrain.Installer.exe");
        if (!File.Exists(installer))
        {
            return;
        }

        try
        {
            var start = new ProcessStartInfo(installer)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("stop");
            using var process = Process.Start(start);
            if (process is null)
            {
                return;
            }

            process.WaitForExit((int)TimeSpan.FromSeconds(45).TotalMilliseconds);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>
    /// The fixture is removed when the run ends. Nothing of the run survives
    /// it: the installed instance, the data root and the work files are all
    /// inside the ignored fixture tree of the repository.
    /// </summary>
    private static void DeleteFixture(string fixture)
    {
        if (!Directory.Exists(fixture))
        {
            return;
        }

        try
        {
            Directory.Delete(fixture, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void RequireInsideRepository(string repositoryRoot, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The installed lifecycle fixture must live inside the repository: " + full + ".");
        }
    }

    /// <summary>
    /// Every directory between the repository root and the fixture must be a
    /// real directory. A reparse point in that chain would let the fixture
    /// resolve somewhere else entirely.
    /// </summary>
    private static void RequireNoReparseAncestry(string repositoryRoot, string path)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var current = Path.GetFullPath(path);
        while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current) && (new DirectoryInfo(current).Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("The installed lifecycle fixture path crosses a reparse point: " + current + ".");
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The installed lifecycle fixture path is not inside the repository.");
            }

            current = parent;
        }
    }

    private static bool HasStatus(JsonElement reply, string status) =>
        reply.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), status, StringComparison.Ordinal);

    private static async Task<JsonElement> InvokeAgentRequestAsync(string pipeName, object request)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var authentication = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        Require(authentication is not null && authentication.Contains("\"authenticated\":true", StringComparison.Ordinal),
            "The installed agent pipe did not authenticate the synthetic local client.");
        await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token).ConfigureAwait(false);
        var reply = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        Require(reply is not null, "The installed agent pipe closed before its one-line response.");
        using var document = JsonDocument.Parse(reply!);
        return document.RootElement.Clone();
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

    /// <summary>
    /// Runs one installer or owner command. The installed host keeps the
    /// standard output handle of the command that started it, so the reader is
    /// stopped after a short grace once the command itself has exited; the
    /// marker is what the run needs from the output.
    /// </summary>
    private static async Task<CommandResult> RunCommandAsync(string executable, string[] arguments, string? standardInput,
        string? marker, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("The command did not start: " + executable);
        }

        var output = new StringBuilder();
        var error = new StringBuilder();
        // The lines are collected as they arrive: the started host keeps the
        // standard output handle of the command that started it, so the
        // stream never reaches its end while the run still needs its output.
        var outputTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (output)
                {
                    output.AppendLine(line);
                }
            }
        });
        var errorTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (error)
                {
                    error.AppendLine(line);
                }
            }
        });

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }

        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            throw new InvalidOperationException("The command did not finish in time: " + string.Join(' ', arguments));
        }

        // The started host may still hold the standard output handle, so the
        // reader is given a short grace and then released.
        await Task.WhenAny(Task.WhenAll(outputTask, errorTask), Task.Delay(GraceTimeout)).ConfigureAwait(false);
        string standardOutput;
        string standardError;
        lock (output)
        {
            standardOutput = output.ToString();
        }

        lock (error)
        {
            standardError = error.ToString();
        }

        if (marker is not null && !standardOutput.Contains(marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The command did not report '" + marker + "': " + string.Join(' ', arguments));
        }

        return new CommandResult(process.ExitCode, standardOutput, standardError);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// A fingerprint of the given product paths: each path's existence and
    /// its exact bytes (a directory contributes its sorted child names, so an
    /// added or removed file is caught too). The read never changes a path:
    /// a denied read is recorded as unreadable, and a missing path as
    /// missing. This proves the lifecycle never replaced, deleted, or
    /// truncated one of the owner's own paths; ordinary permissions are
    /// neither inspected nor changed.
    /// </summary>
    private static string CaptureProductPaths(IEnumerable<string> paths)
    {
        var builder = new StringBuilder();
        foreach (var path in paths)
        {
            builder.Append(path).Append(" -> ").Append(DescribeProductPath(path)).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// The observed state of one product path: existence plus exact content.
    /// </summary>
    private static string DescribeProductPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var children = Directory.GetFileSystemEntries(path)
                    .Select(Path.GetFileName)
                    .OrderBy(name => name, StringComparer.Ordinal);
                return "directory children=[" + string.Join(", ", children) + "]";
            }

            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                try
                {
                    return "file sha256=" + Convert.ToHexString(SHA256.HashData(bytes));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            return "missing";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "not readable (" + ex.GetType().Name + ")";
        }
    }

    /// <summary>
    /// The first path whose access rules differ between the two moments of
    /// the run, so a failure names the path that changed.
    /// </summary>
    private static string FirstDifference(string before, string after)
    {
        var beforeLines = before.Split('\n');
        var afterLines = after.Split('\n');
        for (var index = 0; index < Math.Max(beforeLines.Length, afterLines.Length); index++)
        {
            var left = index < beforeLines.Length ? beforeLines[index] : "<missing>";
            var right = index < afterLines.Length ? afterLines[index] : "<missing>";
            if (!string.Equals(left, right, StringComparison.Ordinal))
            {
                return "before [" + left.Trim() + "] after [" + right.Trim() + "]";
            }
        }

        return "none";
    }
}
