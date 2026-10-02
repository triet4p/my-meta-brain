using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MetaBrain.S1T4.Smoke;

internal static class OwnerServiceSmoke
{
    private static readonly TimeSpan ServiceReadyTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
    private sealed record RunningService(Process Process, Task ReadyDrain, Task<string> StandardErrorDrain);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(
        SafePipeHandle pipe,
        IntPtr buffer,
        uint bufferSize,
        out uint bytesRead,
        out uint totalBytesAvailable,
        out uint bytesLeftThisMessage);

    public static async Task RunAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The owner vault service smoke is Windows-only.");
        }

        var fixture = SmokeFixture.Create();
        Exception? failure = null;
        RunningService? service = null;
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);
            var migration = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "migrate", "--config", fixture.SettingsPath).ConfigureAwait(false);
            Require(migration.ExitCode == 0 && migration.StandardOutput.Contains("MIGRATION encrypted resources=2", StringComparison.Ordinal),
                "The actual owner CLI did not migrate both synthetic resources into the encrypted vault (exit=" +
                migration.ExitCode + "; stderr=" + migration.StandardError.Trim() + ").");
            var recoveryCode = ParseRecoveryCode(migration.StandardOutput);
            Require(recoveryCode.Length >= 40, "The migration did not provision a usable recovery key.");
            VerifyPersistedOutputs(fixture, passphrase, recoveryCode);
            Console.WriteLine("PASS owner CLI migration, user passphrase/recovery provisioning, and ciphertext-only persistence");

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var status = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "status", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(status.ExitCode == 0 && status.StandardOutput.Contains("vault=locked", StringComparison.Ordinal) &&
                    status.StandardOutput.Contains("no token or approval support", StringComparison.Ordinal),
                "A restarted owner service did not report a locked vault and unavailable sharing support.");
            Console.WriteLine("PASS actual service startup locked; status disclaims token/approval support");

            var missing = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(missing.ExitCode == 3 && missing.StandardError.Contains("credential_required", StringComparison.Ordinal),
                "Unlock without a supplied key was not denied.");
            var wrong = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                "wrong synthetic vault passphrase\n", "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(wrong.ExitCode == 3 && wrong.StandardError.Contains("unlock_denied", StringComparison.Ordinal),
                "A wrong vault passphrase was not denied.");
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false)).Contains("vault=locked", StringComparison.Ordinal),
                "A denied unlock changed the vault to unlocked.");
            Require(await IsReadDeniedAsync(connectionsExecutable, fixture, SmokeFixture.ResourceA).ConfigureAwait(false),
                "A private resource read succeeded with a missing or wrong key.");
            Console.WriteLine("PASS missing/wrong key denial and locked read denial");

            var unlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The correct owner passphrase did not unlock the vault.");
            await VerifyReadAsync(connectionsExecutable, fixture, SmokeFixture.ResourceA, fixture.ExpectedContentA).ConfigureAwait(false);
            await VerifyReadAsync(connectionsExecutable, fixture, SmokeFixture.ResourceB, fixture.ExpectedContentB).ConfigureAwait(false);
            Console.WriteLine("PASS actual owner CLI authenticated resource reads and ciphertext round-trip");

            var replacement = Encoding.UTF8.GetBytes("synthetic revised private resource body; never a real owner secret.");
            var replacementPath = Path.Combine(fixture.OutputDirectory, "replacement-input.bin");
            await File.WriteAllBytesAsync(replacementPath, replacement).ConfigureAwait(false);
            var write = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "write", "--control-pipe", fixture.ControlPipe,
                "--resource-id", SmokeFixture.ResourceA, "--zone-id", SmokeFixture.Zone,
                "--input-file", replacementPath).ConfigureAwait(false);
            Require(write.ExitCode == 0 && write.StandardOutput.Contains("WRITE stored revision=2", StringComparison.Ordinal),
                "The owner CLI did not persist a revision-checked encrypted resource write.");
            await VerifyReadAsync(connectionsExecutable, fixture, SmokeFixture.ResourceA, replacement).ConfigureAwait(false);
            Console.WriteLine("PASS encrypted owner write/read revision transition");

            var lockingRace = await VerifyLockRaceAsync(connectionsExecutable, fixture).ConfigureAwait(false);
            Console.WriteLine(lockingRace);
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false)).Contains("vault=locked", StringComparison.Ordinal),
                "The service restarted unlocked instead of returning to the locked state.");
            Require(await IsReadDeniedAsync(connectionsExecutable, fixture, SmokeFixture.ResourceB).ConfigureAwait(false),
                "A restarted locked service returned private content.");
            Console.WriteLine("PASS process restart returns locked and denies resource reads");

            var recovery = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                recoveryCode + Environment.NewLine, "owner", "recover", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(recovery.ExitCode == 0 && recovery.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The owner-controlled recovery key did not unlock the vault.");
            var expectedAfterRace = await File.ReadAllBytesAsync(fixture.RaceInputPath).ConfigureAwait(false);
            try
            {
                await VerifyReadAsync(connectionsExecutable, fixture, SmokeFixture.ResourceA, expectedAfterRace).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedAfterRace);
            }
            Console.WriteLine("PASS recovery key unlock after restart without fake recovery");

            await VerifyCiphertextTamperDenialsAsync(connectionsExecutable, fixture).ConfigureAwait(false);
            Console.WriteLine("PASS modified, truncated, and swapped valid resource ciphertext denied");

            var finalLock = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(finalLock.ExitCode == 0 && finalLock.StandardOutput.Contains("in-flight private operations drained", StringComparison.Ordinal),
                "The owner lock command did not complete its operation drain.");
            Require(await IsReadDeniedAsync(connectionsExecutable, fixture, SmokeFixture.ResourceA).ConfigureAwait(false),
                "A private read remained available after the final lock.");
            Require(await IsWriteDeniedAsync(connectionsExecutable, fixture, SmokeFixture.ResourceA, fixture.RaceInputPath).ConfigureAwait(false),
                "A private write remained available after the final lock.");
            Console.WriteLine("PASS locked boundary denies subsequent reads and writes; service process reaped");
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            await VerifyFreshProvisionAsync(connectionsExecutable).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (service is not null)
            {
                try
                {
                    await StopServiceAsync(service).ConfigureAwait(false);
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

        Console.WriteLine("LIMIT synthetic Windows owner-service fixture only; same-owner OS compromise can read plaintext/key while unlocked, .NET/OS memory and deleted-media erasure are not guaranteed; no S3 ingestion, S6 jobs, tokens, approvals, provider, or installed-service integration was exercised.");
    }

    private static async Task VerifyFreshProvisionAsync(string executable)
    {
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        try
        {
            service = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(executable, fixture.ControlPipe).ConfigureAwait(false))
                    .Contains("vault=uninitialized", StringComparison.Ordinal),
                "A service with no vault did not report the uninitialized state.");

            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);
            var provision = await RunOwnerCommandWithInputAsync(executable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned; currently unlocked", StringComparison.Ordinal),
                "The actual owner provision command did not create and unlock a new vault.");
            var recoveryCode = ParseRecoveryCode(provision.StandardOutput);
            Require(recoveryCode.Length >= 40, "Fresh provisioning did not return a usable recovery key.");
            var settings = File.ReadAllText(fixture.SettingsPath);
            var persisted = Directory.GetFiles(fixture.VaultDirectoryPath, "*", SearchOption.AllDirectories)
                .Select(path => (Path: path, Text: File.ReadAllText(path)))
                .ToArray();
            Require(persisted.Length == 2 && !settings.Contains(passphrase, StringComparison.Ordinal) &&
                    !settings.Contains(recoveryCode, StringComparison.Ordinal) &&
                    persisted.All(file => !file.Text.Contains(passphrase, StringComparison.Ordinal) &&
                                          !file.Text.Contains(recoveryCode, StringComparison.Ordinal)),
                "Fresh provisioning persisted a plaintext passphrase or recovery key.");

            var locked = await RunOwnerCommandAsync(executable, "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The freshly provisioned vault did not lock.");
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;

            service = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(executable, fixture.ControlPipe).ConfigureAwait(false))
                    .Contains("vault=locked", StringComparison.Ordinal),
                "A newly provisioned vault did not restart locked.");
            var unlock = await RunOwnerCommandWithInputAsync(executable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0 && unlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The passphrase issued by fresh provisioning could not unlock the restarted vault.");
            Console.WriteLine("PASS actual owner provision, one-time recovery-key handoff, no plaintext key persistence, and restart unlock");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> VerifyLockRaceAsync(string executable, SmokeFixture fixture)
    {
        var largeContent = new byte[1024 * 1024];
        for (var index = 0; index < largeContent.Length; index++)
        {
            largeContent[index] = (byte)('A' + index % 26);
        }

        await File.WriteAllBytesAsync(fixture.RaceInputPath, largeContent).ConfigureAwait(false);
        var seedWrite = await RunOwnerCommandAsync(executable,
            "owner", "write", "--control-pipe", fixture.ControlPipe,
            "--resource-id", SmokeFixture.ResourceA, "--zone-id", SmokeFixture.Zone,
            "--input-file", fixture.RaceInputPath).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(largeContent);
        Require(seedWrite.ExitCode == 0, "Could not prepare the bounded large synthetic record for the lock race.");

        using var blockedRead = await BeginUnconsumedResourceReadAsync(fixture.ControlPipe, SmokeFixture.ResourceA).ConfigureAwait(false);
        var lockCommand = RunOwnerCommandAsync(executable, "owner", "lock", "--control-pipe", fixture.ControlPipe);
        var lockingObserved = false;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < until)
        {
            var status = await ReadStatusAsync(executable, fixture.ControlPipe).ConfigureAwait(false);
            if (status.Contains("vault=locking", StringComparison.Ordinal))
            {
                lockingObserved = true;
                break;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        Require(lockingObserved && !lockCommand.IsCompleted,
            "The lock did not visibly wait behind an in-flight authenticated resource response.");
        var racingWrite = await IsWriteDeniedAsync(executable, fixture, SmokeFixture.ResourceA, fixture.RaceInputPath).ConfigureAwait(false);
        Require(racingWrite, "A write started after lock began was not denied at the lifecycle boundary.");
        var racingRead = await IsReadDeniedAsync(executable, fixture, SmokeFixture.ResourceB).ConfigureAwait(false);
        Require(racingRead, "A read started after lock began was not denied at the lifecycle boundary.");

        var largeReply = await blockedRead.ReadResponseAsync().ConfigureAwait(false);
        Require(largeReply, "The held in-flight read response was not fully sent after its pipe was drained.");
        var lockResult = await lockCommand.WaitAsync(CommandTimeout).ConfigureAwait(false);
        Require(lockResult.ExitCode == 0 && lockResult.StandardOutput.Contains("in-flight private operations drained", StringComparison.Ordinal),
            "The lock did not complete after the in-flight read response drained.");
        Require(await IsReadDeniedAsync(executable, fixture, SmokeFixture.ResourceA).ConfigureAwait(false),
            "A read succeeded after the lock acknowledgment.");
        Require(await IsWriteDeniedAsync(executable, fixture, SmokeFixture.ResourceA, fixture.RaceInputPath).ConfigureAwait(false),
            "A write succeeded after the lock acknowledgment.");
        return "PASS deterministic service race: 1 MiB read held response open; lock waited in `locking`; concurrent read/write denied; lock acknowledged only after the active response drained";
    }

    private static async Task VerifyCiphertextTamperDenialsAsync(string executable, SmokeFixture fixture)
    {
        var paths = Directory.GetFiles(fixture.VaultResourcesPath, "*.mbv").Order(StringComparer.Ordinal).ToArray();
        Require(paths.Length == 2, "The synthetic vault did not contain exactly two public resource ciphertexts.");
        var originalA = await File.ReadAllBytesAsync(paths[0]).ConfigureAwait(false);
        var originalB = await File.ReadAllBytesAsync(paths[1]).ConfigureAwait(false);
        try
        {
            await File.WriteAllBytesAsync(paths[0], originalB).ConfigureAwait(false);
            await File.WriteAllBytesAsync(paths[1], originalA).ConfigureAwait(false);
            Require(await IsReadDeniedAsync(executable, fixture, SmokeFixture.ResourceA).ConfigureAwait(false) &&
                    await IsReadDeniedAsync(executable, fixture, SmokeFixture.ResourceB).ConfigureAwait(false),
                "Swapped valid resource ciphertext was served under a different allowed resource ID.");

            await File.WriteAllBytesAsync(paths[0], originalA).ConfigureAwait(false);
            await File.WriteAllBytesAsync(paths[1], originalB).ConfigureAwait(false);
            var modified = (byte[])originalA.Clone();
            modified[modified.Length / 2] ^= 0x20;
            await File.WriteAllBytesAsync(paths[0], modified).ConfigureAwait(false);
            Require(await AnyPublicResourceReadDeniedAsync(executable, fixture).ConfigureAwait(false),
                "A modified resource ciphertext was accepted.");

            await File.WriteAllBytesAsync(paths[0], originalA).ConfigureAwait(false);
            await File.WriteAllBytesAsync(paths[1], originalB).ConfigureAwait(false);
            await File.WriteAllBytesAsync(paths[0], originalA.AsMemory(0, Math.Min(8, originalA.Length))).ConfigureAwait(false);
            Require(await AnyPublicResourceReadDeniedAsync(executable, fixture).ConfigureAwait(false),
                "A truncated resource ciphertext was accepted.");

            await File.WriteAllBytesAsync(paths[0], originalA).ConfigureAwait(false);
            await File.WriteAllBytesAsync(paths[1], originalB).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(paths[0]))
            {
                await File.WriteAllBytesAsync(paths[0], originalA).ConfigureAwait(false);
            }

            if (File.Exists(paths[1]))
            {
                await File.WriteAllBytesAsync(paths[1], originalB).ConfigureAwait(false);
            }

            CryptographicOperations.ZeroMemory(originalA);
            CryptographicOperations.ZeroMemory(originalB);
        }
    }

    private static async Task<bool> AnyPublicResourceReadDeniedAsync(string executable, SmokeFixture fixture)
    {
        var deniedA = await IsReadDeniedAsync(executable, fixture, SmokeFixture.ResourceA).ConfigureAwait(false);
        var deniedB = await IsReadDeniedAsync(executable, fixture, SmokeFixture.ResourceB).ConfigureAwait(false);
        return deniedA || deniedB;
    }

    private static async Task<bool> IsReadDeniedAsync(string executable, SmokeFixture fixture, string resourceId)
    {
        var output = Path.Combine(fixture.OutputDirectory, "denied-" + Guid.NewGuid().ToString("N") + ".bin");
        var result = await RunOwnerCommandAsync(executable,
            "owner", "read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--output-file", output).ConfigureAwait(false);
        var noOutput = !File.Exists(output);
        if (!noOutput)
        {
            File.Delete(output);
        }

        return result.ExitCode == 4 && noOutput && result.StandardError.Contains("resource_unavailable", StringComparison.Ordinal);
    }

    private static async Task<bool> IsWriteDeniedAsync(string executable, SmokeFixture fixture, string resourceId, string inputPath)
    {
        var result = await RunOwnerCommandAsync(executable,
            "owner", "write", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", SmokeFixture.Zone,
            "--input-file", inputPath).ConfigureAwait(false);
        return result.ExitCode == 4 && result.StandardError.Contains("resource_unavailable", StringComparison.Ordinal);
    }

    private static async Task VerifyReadAsync(string executable, SmokeFixture fixture, string resourceId, byte[] expected)
    {
        var output = Path.Combine(fixture.OutputDirectory, "read-" + Guid.NewGuid().ToString("N") + ".bin");
        var read = await RunOwnerCommandAsync(executable,
            "owner", "read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--output-file", output).ConfigureAwait(false);
        Require(read.ExitCode == 0 && File.Exists(output) &&
                (await File.ReadAllBytesAsync(output).ConfigureAwait(false)).AsSpan().SequenceEqual(expected),
            "The actual owner CLI did not return the expected synthetic resource bytes.");
        File.Delete(output);
    }

    private static async Task<string> ReadStatusAsync(string executable, string pipe)
    {
        var result = await RunOwnerCommandAsync(executable, "owner", "status", "--control-pipe", pipe).ConfigureAwait(false);
        Require(result.ExitCode == 0, "The actual owner CLI status request failed.");
        return result.StandardOutput;
    }

    private static string ParseRecoveryCode(string output)
    {
        const string marker = "RECOVERY KEY (save outside the vault; shown once): ";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("The one-time recovery key was not returned to the owner.");
        }

        start += marker.Length;
        var end = output.IndexOfAny(['\r', '\n'], start);
        return (end < 0 ? output[start..] : output[start..end]).Trim();
    }

    private static async Task<RunningService> StartServiceAsync(string executable, string settingsPath)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("serve-console");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(settingsPath);
        var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The encrypted owner service process did not start.");
        }

        process.StandardInput.Close();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputDrain = DrainServiceOutputAsync(process.StandardOutput, ready);
        var standardErrorDrain = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExitAsync();
        try
        {
            var startup = await Task.WhenAny(ready.Task, exited).WaitAsync(ServiceReadyTimeout).ConfigureAwait(false);
            if (startup == exited)
            {
                var error = await standardErrorDrain.ConfigureAwait(false);
                throw new InvalidOperationException("The encrypted owner service exited before READY (stderr bytes=" + error.Length + ").");
            }

            await ready.Task.ConfigureAwait(false);
            return new RunningService(process, outputDrain, standardErrorDrain);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync().WaitAsync(CleanupTimeout).ConfigureAwait(false);
                await Task.WhenAll(outputDrain, standardErrorDrain).WaitAsync(CleanupTimeout).ConfigureAwait(false);
            }
            catch
            {
            }

            process.Dispose();
            throw;
        }

    }

    private static async Task DrainServiceOutputAsync(StreamReader reader, TaskCompletionSource<bool> ready)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("READY protocol=1 channel=owner-only", StringComparison.Ordinal))
            {
                ready.TrySetResult(true);
            }
        }
    }

    private static async Task<CommandResult> RunOwnerCommandAsync(string executable, params string[] arguments) =>
        await RunOwnerCommandAsync(executable, arguments, standardInput: null).ConfigureAwait(false);

    private static Task<CommandResult> RunOwnerCommandWithInputAsync(string executable, string standardInput, params string[] arguments) =>
        RunOwnerCommandAsync(executable, arguments, standardInput);

    private static async Task<CommandResult> RunOwnerCommandAsync(string executable, string[] arguments, string? standardInput)
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
            throw new InvalidOperationException("The actual owner CLI process did not start.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync().WaitAsync(CommandTimeout).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync().WaitAsync(CleanupTimeout).ConfigureAwait(false);
                await Task.WhenAll(standardOutput, standardError).WaitAsync(CleanupTimeout).ConfigureAwait(false);
            }
            catch
            {
            }

            throw;
        }
        return new CommandResult(process.ExitCode,
            await standardOutput.ConfigureAwait(false), await standardError.ConfigureAwait(false));
    }

    private static async Task StopServiceAsync(RunningService service)
    {
        var process = service.Process;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(CleanupTimeout).ConfigureAwait(false);
            await service.ReadyDrain.ConfigureAwait(false);
            _ = await service.StandardErrorDrain.ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void VerifyPersistedOutputs(SmokeFixture fixture, string passphrase, string recoveryCode)
    {
        var settings = File.ReadAllText(fixture.SettingsPath);
        foreach (var privateValue in new[]
                 {
                     SmokeFixture.ResourceA, SmokeFixture.ResourceB, SmokeFixture.Zone,
                     SmokeFixture.FileA, SmokeFixture.FileB, SmokeFixture.MetadataMarker,
                     passphrase, recoveryCode
                 })
        {
            Require(!settings.Contains(privateValue, StringComparison.Ordinal),
                "The replacement service settings persisted private metadata or a key.");
        }

        Require(!File.Exists(fixture.LegacyResourceAPath) && !File.Exists(fixture.LegacyResourceBPath),
            "The configured plaintext source files remained after successful encrypted cutover.");
        var vaultFiles = Directory.GetFiles(fixture.VaultDirectoryPath, "*", SearchOption.AllDirectories);
        Require(vaultFiles.Length >= 4, "The vault header, encrypted manifest, and resource ciphertext were not persisted.");
        foreach (var path in vaultFiles)
        {
            var content = File.ReadAllText(path);
            foreach (var privateValue in new[]
                     {
                         SmokeFixture.ResourceA, SmokeFixture.ResourceB, SmokeFixture.Zone,
                         SmokeFixture.FileA, SmokeFixture.FileB, SmokeFixture.MetadataMarker,
                         passphrase, recoveryCode
                     })
            {
                Require(!Path.GetFileName(path).Contains(privateValue, StringComparison.Ordinal) &&
                        !content.Contains(privateValue, StringComparison.Ordinal),
                    "The encrypted vault persisted a private metadata marker or plaintext key.");
            }
        }
    }

    private static async Task<PendingRead> BeginUnconsumedResourceReadAsync(string pipeName, string resourceId)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        await client.ConnectAsync(5000).ConfigureAwait(false);
        var reader = new StreamReader(client, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        var writer = new StreamWriter(client, new UTF8Encoding(false, true), 4096, leaveOpen: true) { AutoFlush = true };
        var authentication = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Require(authentication is not null && authentication.Contains("\"authenticated\":true", StringComparison.Ordinal),
            "The owner pipe did not authenticate the synthetic race client.");
        var request = JsonSerializer.Serialize(new { protocolVersion = 1, operation = "resource.read", resourceId });
        await writer.WriteLineAsync(request).ConfigureAwait(false);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (!PeekNamedPipe(client.SafePipeHandle, IntPtr.Zero, 0, out _, out var available, out _))
            {
                throw new InvalidOperationException("Could not inspect the synthetic pipe's buffered response.");
            }

            if (available > 0)
            {
                return new PendingRead(client, reader, writer);
            }

            await Task.Delay(5).ConfigureAwait(false);
        }

        reader.Dispose();
        writer.Dispose();
        client.Dispose();
        throw new TimeoutException("The service did not begin the large resource response needed to hold an active read lease.");
    }

    private sealed class PendingRead : IDisposable
    {
        private readonly NamedPipeClientStream _client;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        public PendingRead(NamedPipeClientStream client, StreamReader reader, StreamWriter writer)
        {
            _client = client;
            _reader = reader;
            _writer = writer;
        }

        public async Task<bool> ReadResponseAsync()
        {
            var response = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (response is null)
            {
                return false;
            }

            using var document = JsonDocument.Parse(response);
            return document.RootElement.TryGetProperty("status", out var status) && status.GetString() == "content" &&
                   document.RootElement.TryGetProperty("contentBase64", out var content) &&
                   content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 1_000_000 };
        }

        public void Dispose()
        {
            _writer.Dispose();
            _reader.Dispose();
            _client.Dispose();
        }
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
    private bool _disposed;

    private SmokeFixture(string root, string settingsPath, string controlPipe, string outputDirectory,
        byte[] expectedContentA, byte[] expectedContentB)
    {
        Root = root;
        SettingsPath = settingsPath;
        ControlPipe = controlPipe;
        OutputDirectory = outputDirectory;
        ExpectedContentA = expectedContentA;
        ExpectedContentB = expectedContentB;
        ServiceDirectoryPath = Path.Combine(root, "service");
        VaultDirectoryPath = Path.Combine(ServiceDirectoryPath, "vault");
        VaultResourcesPath = Path.Combine(VaultDirectoryPath, "resources");
        LegacyResourceAPath = Path.Combine(ServiceDirectoryPath, "resources", FileA);
        LegacyResourceBPath = Path.Combine(ServiceDirectoryPath, "resources", FileB);
        RaceInputPath = Path.Combine(outputDirectory, "race-write.bin");
    }

    public const string ResourceA = "fixture-private-resource-a";
    public const string ResourceB = "fixture-private-resource-b";
    public const string UnknownResource = "fixture-not-present";
    public const string Zone = "fixture-secret-zone";
    public const string FileA = "private-title-alpha.txt";
    public const string FileB = "private-title-beta.txt";
    public const string MetadataMarker = "synthetic-private-title-marker-7e31";

    public string Root { get; }
    public string SettingsPath { get; }
    public string ControlPipe { get; }
    public string OutputDirectory { get; }
    public string ServiceDirectoryPath { get; }
    public string VaultDirectoryPath { get; }
    public string VaultResourcesPath { get; }
    public string LegacyResourceAPath { get; }
    public string LegacyResourceBPath { get; }
    public string RaceInputPath { get; }
    public byte[] ExpectedContentA { get; }
    public byte[] ExpectedContentB { get; }

    public static SmokeFixture Create() => Create(legacySettings: true);

    public static SmokeFixture CreateUninitialized() => Create(legacySettings: false);

    private static SmokeFixture Create(bool legacySettings)
    {
        var ownerSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows token has no user SID.");
        var root = Path.Combine(Path.GetTempPath(), "MetaBrain-S1-T2-Vault-" + Guid.NewGuid().ToString("N"));
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

            var expectedContentA = legacySettings
                ? Encoding.UTF8.GetBytes("synthetic private fixture body A; metadata=" + MetadataMarker)
                : Array.Empty<byte>();
            var expectedContentB = legacySettings
                ? Encoding.UTF8.GetBytes("synthetic private fixture body B; ciphertext identity must remain bound.")
                : Array.Empty<byte>();
            if (legacySettings)
            {
                var pathA = Path.Combine(resourceRoot, FileA);
                var pathB = Path.Combine(resourceRoot, FileB);
                File.WriteAllBytes(pathA, expectedContentA);
                File.WriteAllBytes(pathB, expectedContentB);
                SetRestrictedFileAcl(pathA, ownerSid);
                SetRestrictedFileAcl(pathB, ownerSid);
            }

            var controlPipe = "MetaBrainS1T2Vault-" + Guid.NewGuid().ToString("N");
            var settingsPath = Path.Combine(serviceDirectory, "service-settings.json");
            var settings = legacySettings
                ? JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    ownerSid = ownerSid.Value,
                    controlPipe,
                    managedResources = new[]
                    {
                        new { resourceId = ResourceA, zoneId = Zone, fileName = FileA },
                        new { resourceId = ResourceB, zoneId = Zone, fileName = FileB }
                    }
                })
                : JsonSerializer.Serialize(new { schemaVersion = 2, ownerSid = ownerSid.Value, controlPipe });
            File.WriteAllText(settingsPath, settings, new UTF8Encoding(false));
            SetRestrictedFileAcl(settingsPath, ownerSid);

            return new SmokeFixture(root, settingsPath, controlPipe, outputDirectory, expectedContentA, expectedContentB);
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
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-544"), FileSystemRights.FullControl,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        System.IO.FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
    }

    private static void SetRestrictedFileAcl(string path, SecurityIdentifier ownerSid)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(ownerSid);
        security.AddAccessRule(new FileSystemAccessRule(ownerSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-544"), FileSystemRights.FullControl, AccessControlType.Allow));
        System.IO.FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
    }

    private static void DeleteOwnedRoot(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var temporaryDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var expectedPrefix = temporaryDirectory + Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullRoot).StartsWith("MetaBrain-S1-T2-Vault-", StringComparison.Ordinal) ||
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
