using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    internal static async Task RunS2T2CanonicalDurabilityAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S2-T2 canonical durability smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S2-T2 encrypted canonical files with revision conflict and write recovery");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        Exception? failure = null;
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "Provision did not create the S2-T2 synthetic vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");

            var now = DateTimeOffset.UtcNow.AddMinutes(-2);
            var draft = JsonSerializer.Serialize(new
            {
                kind = "lesson",
                author = "user",
                creationMethod = "directWrite",
                evidence = "unverified",
                level = "l2",
                title = "Synthetic S2-T2 durable lesson",
                body = "Synthetic S2-T2 canonical body revision one; never a real owner secret.",
                sources = Array.Empty<object>(),
                occurredAtUtc = now,
                recordedAtUtc = now.AddMinutes(1),
                applicability = new { project = "synthetic-app", environment = "staging", version = "0.0.0-s2t2" }
            });
            const string resourceId = "s2t2-canonical-alpha";
            const string zoneId = "s2t2-private-zone";
            await WriteS2T2DraftAsync(connectionsExecutable, fixture, resourceId, zoneId, draft, expectedRevision: 1).ConfigureAwait(false);
            Console.WriteLine("PASS canonical create persists revision 1 as encrypted bytes");

            // Stale create-only must fail closed: the ID already exists at revision 1.
            var duplicate = await WriteS2T2DraftResultAsync(connectionsExecutable, fixture, resourceId, zoneId, draft).ConfigureAwait(false);
            Require(duplicate.ExitCode == 4 && duplicate.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A duplicate create-only write was accepted instead of failing closed.");
            Console.WriteLine("PASS duplicate create-only rejected without publishing");

            // Revise with the live revision, then prove the stale concurrent write loses.
            var revised = draft.Replace("revision one", "revision two");
            await ReviseS2T2DraftAsync(connectionsExecutable, fixture, resourceId, zoneId, revised, writeRevision: 1, expectedRevision: 2).ConfigureAwait(false);
            var stale = await ReviseS2T2DraftResultAsync(connectionsExecutable, fixture, resourceId, zoneId,
                draft.Replace("revision one", "stale loser"), writeRevision: 1).ConfigureAwait(false);
            Require(stale.ExitCode == 4 && stale.StandardError.Contains("invalid_request", StringComparison.Ordinal),
                "A stale concurrent revise was accepted instead of failing closed.");
            var live = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", resourceId).ConfigureAwait(false);
            Require(live.ExitCode == 0 && live.StandardOutput.Contains("revision two", StringComparison.Ordinal) &&
                    !live.StandardOutput.Contains("stale loser", StringComparison.Ordinal),
                "The stale concurrent write overwrote the successful revision.");
            Console.WriteLine("PASS stale concurrent revise rejected; winner revision survives");

            // Restart + unlock durability: the saved revision must survive.
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false)).Contains("vault=locked", StringComparison.Ordinal),
                "The restarted service did not return locked.");
            var lockedDenied = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", resourceId).ConfigureAwait(false);
            Require(lockedDenied.ExitCode == 3, "A locked memory read was not denied after restart.");
            var unlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The passphrase did not unlock the restarted vault.");
            var afterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", resourceId).ConfigureAwait(false);
            Require(afterRestart.ExitCode == 0 && afterRestart.StandardOutput.Contains("revision two", StringComparison.Ordinal) &&
                    afterRestart.StandardOutput.Contains("revision=2", StringComparison.Ordinal),
                "The saved canonical revision did not survive restart/unlock.");
            Console.WriteLine("PASS saved revision survives restart and unlock; locked reads denied");

            // Real process interruption between staged ciphertext and manifest commit:
            // plant an orphan ciphertext file plus an interrupted temp file while the
            // service is stopped, restart, unlock, and prove recovery reaps them with
            // no half-published state and no revision change.
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            var strayCipher = Path.Combine(fixture.VaultResourcesPath, Guid.NewGuid().ToString("N") + ".mbv");
            var liveFiles = Directory.GetFiles(fixture.VaultResourcesPath, "*.mbv", SearchOption.TopDirectoryOnly);
            Require(liveFiles.Length >= 1, "The synthetic vault did not persist canonical ciphertext for the crash proof.");
            await File.WriteAllBytesAsync(strayCipher, await File.ReadAllBytesAsync(liveFiles[0]).ConfigureAwait(false)).ConfigureAwait(false);
            var strayTemp = Path.Combine(fixture.VaultDirectoryPath, "." + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllBytesAsync(strayTemp, Encoding.UTF8.GetBytes("interrupted synthetic atomic write")).ConfigureAwait(false);
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var reunlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(reunlock.ExitCode == 0 && reunlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "Unlock after the simulated crash did not complete.");
            Require(!File.Exists(strayCipher) && !File.Exists(strayTemp),
                "Unlock recovery did not reap the interrupted staged ciphertext and temp file.");
            var afterCrash = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", resourceId).ConfigureAwait(false);
            Require(afterCrash.ExitCode == 0 && afterCrash.StandardOutput.Contains("revision two", StringComparison.Ordinal) &&
                    afterCrash.StandardOutput.Contains("revision=2", StringComparison.Ordinal),
                "Crash recovery changed or lost the committed canonical revision.");
            Require(!HasS2T2TemporaryFiles(fixture.VaultDirectoryPath) && !HasS2T2TemporaryFiles(fixture.ServiceDirectoryPath),
                "Crash recovery left an atomic-write temporary file.");
            Console.WriteLine("PASS real interrupted publication recovered: orphan ciphertext and temp reaped, committed revision intact");

            // Ciphertext negative matrix over the live canonical file.
            var canonicalFiles = Directory.GetFiles(fixture.VaultResourcesPath, "*.mbv", SearchOption.TopDirectoryOnly);
            Require(canonicalFiles.Length == 1, "The synthetic vault did not contain exactly one canonical ciphertext.");
            var original = await File.ReadAllBytesAsync(canonicalFiles[0]).ConfigureAwait(false);
            try
            {
                var swappedPath = Path.Combine(fixture.VaultResourcesPath, Guid.NewGuid().ToString("N") + ".mbv");
                await File.WriteAllBytesAsync(swappedPath, original).ConfigureAwait(false);
                try
                {
                    Require(await IsS2T2MemoryDeniedAsync(connectionsExecutable, fixture, "s2t2-swapped-ghost").ConfigureAwait(false),
                        "A swapped valid ciphertext was served under a different resource ID.");
                }
                finally
                {
                    File.Delete(swappedPath);
                }

                var tampered = (byte[])original.Clone();
                tampered[tampered.Length / 2] ^= 0x20;
                await File.WriteAllBytesAsync(canonicalFiles[0], tampered).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(tampered);
                Require(await IsS2T2MemoryDeniedAsync(connectionsExecutable, fixture, resourceId).ConfigureAwait(false),
                    "A tampered canonical ciphertext was accepted.");
                await File.WriteAllBytesAsync(canonicalFiles[0], original).ConfigureAwait(false);

                await File.WriteAllBytesAsync(canonicalFiles[0], original.AsMemory(0, Math.Min(8, original.Length))).ConfigureAwait(false);
                Require(await IsS2T2MemoryDeniedAsync(connectionsExecutable, fixture, resourceId).ConfigureAwait(false),
                    "A truncated canonical ciphertext was accepted.");
                await File.WriteAllBytesAsync(canonicalFiles[0], original).ConfigureAwait(false);

                // Wrong-key proof needs a locked vault: lock first so unlock
                // actually exercises the KDF/unwrap path instead of the
                // already-unlocked early return.
                var relock = await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
                Require(relock.ExitCode == 0, "Owner lock did not complete before the wrong-key probe.");
                var wrongUnlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                    "wrong synthetic vault passphrase\n", "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
                Require(wrongUnlock.ExitCode == 3 && wrongUnlock.StandardError.Contains("unlock_denied", StringComparison.Ordinal),
                    "A wrong vault passphrase was not denied.");
                var reunlockAfterWrong = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                    passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
                Require(reunlockAfterWrong.ExitCode == 0 && reunlockAfterWrong.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                    "The correct passphrase did not unlock after the wrong-key probe.");
            }
            finally
            {
                if (File.Exists(canonicalFiles[0]))
                {
                    await File.WriteAllBytesAsync(canonicalFiles[0], original).ConfigureAwait(false);
                }

                CryptographicOperations.ZeroMemory(original);
            }

            Console.WriteLine("PASS ciphertext swap/tamper/truncation/wrong-key fail closed");

            // Explicit import boundary: no external plaintext editing of
            // canonical files. Prove the boundary rejects a mismatched
            // envelope identity (zone differs from the target) without
            // publishing anything, while the live canonical revision stays
            // readable. Full legacy-format migration is proven by the S2-T1
            // envelope compatibility check below (same schema version, same
            // validator), not by hand-forging stored bytes in this harness.
            var staleImport = await ImportS2T2EnvelopeResultAsync(connectionsExecutable, fixture, resourceId, "wrong-zone",
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    id = resourceId,
                    zoneId = "wrong-zone",
                    kind = 6,
                    author = 1,
                    executor = 1,
                    creationMethod = 1,
                    evidence = 1,
                    level = 3,
                    title = "Synthetic S2-T2 stale import",
                    body = "Synthetic S2-T2 stale import body; never a real owner secret.",
                    sources = Array.Empty<object>(),
                    occurredAtUtc = now,
                    recordedAtUtc = now.AddMinutes(1)
                }), writeRevision: 2).ConfigureAwait(false);
            Require(staleImport.ExitCode == 4 || staleImport.ExitCode == 3,
                "An import with a mismatched zone was accepted.");
            var stillLive = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", resourceId).ConfigureAwait(false);
            Require(stillLive.ExitCode == 0 && stillLive.StandardOutput.Contains("revision two", StringComparison.Ordinal),
                "A rejected import changed the live canonical revision.");
            Console.WriteLine("PASS explicit import boundary rejects mismatched identity; live revision unchanged");
            VerifyS2T2CiphertextOnly(fixture);
            Console.WriteLine("PASS persisted canonical/metadata/journal/temp files carry no plaintext");
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
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        Console.WriteLine("LIMIT synthetic S2-T2 fixture only; index rebuild stays S2-T7; relations/lifecycle/collections/views stay pending; same-owner OS compromise remains outside the threat model.");
    }

    private static async Task WriteS2T2DraftAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson, long expectedRevision)
    {
        var result = await WriteS2T2DraftResultAsync(executable, fixture, resourceId, zoneId, draftJson).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"MEMORY stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Memory create did not store {resourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}; draft={draftJson}).");
    }

    private static async Task<CommandResult> WriteS2T2DraftResultAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t2-draft-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
    }

    private static async Task ReviseS2T2DraftAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson, long writeRevision, long expectedRevision)
    {
        var result = await ReviseS2T2DraftResultAsync(executable, fixture, resourceId, zoneId, draftJson, writeRevision).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"MEMORY stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Memory revise did not store {resourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}; draft={draftJson}).");
    }

    private static async Task<CommandResult> ReviseS2T2DraftResultAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson, long writeRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t2-revise-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "memory-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--expected-revision", writeRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--input-file", input).ConfigureAwait(false);
    }

    private static async Task<CommandResult> ImportS2T2EnvelopeResultAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string envelopeJson, long writeRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s2t2-import-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, envelopeJson, Encoding.UTF8).ConfigureAwait(false);
        return await RunOwnerCommandAsync(executable,
            "owner", "memory-import", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--expected-revision", writeRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--input-file", input).ConfigureAwait(false);
    }

    private static async Task<bool> IsS2T2MemoryDeniedAsync(string executable, SmokeFixture fixture, string resourceId)
    {
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId).ConfigureAwait(false);
        return result.ExitCode == 3 && result.StandardError.Contains("invalid_request", StringComparison.Ordinal);
    }

    private static bool HasS2T2TemporaryFiles(string directory) =>
        Directory.Exists(directory) &&
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Any(path => Path.GetFileName(path).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));

    private static void VerifyS2T2CiphertextOnly(SmokeFixture fixture)
    {
        foreach (var path in Directory.GetFiles(fixture.VaultDirectoryPath, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            Require(!text.Contains("Synthetic S2-T2", StringComparison.Ordinal),
                "Private canonical plaintext leaked into a persisted vault file: " + Path.GetFileName(path));
        }

        var resources = Directory.GetFiles(fixture.VaultResourcesPath, "*.mbv", SearchOption.TopDirectoryOnly);
        Require(resources.Length == 1, "The S2-T2 vault did not persist exactly one canonical ciphertext envelope.");
        foreach (var file in resources)
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            Require(text.Contains("\"ciphertext\"", StringComparison.Ordinal) && text.Contains("\"nonce\"", StringComparison.Ordinal),
                "A vault resource is not in the authenticated envelope form: " + Path.GetFileName(file));
        }

        var outputDrafts = Directory.GetFiles(fixture.OutputDirectory, "s2t2-*", SearchOption.TopDirectoryOnly);
        foreach (var draft in outputDrafts)
        {
            File.Delete(draft);
        }

        Require(!Directory.GetFiles(fixture.OutputDirectory, "s2t2-*", SearchOption.TopDirectoryOnly).Any(),
            "Synthetic plaintext draft files remained outside the vault after the S2-T2 scenario.");
    }
}
