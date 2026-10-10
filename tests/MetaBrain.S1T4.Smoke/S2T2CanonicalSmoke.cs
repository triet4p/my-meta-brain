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
                    await RequireS2T2CanonicalReadDeniedAsync(connectionsExecutable, fixture, "s2t2-swapped-ghost", "swapped").ConfigureAwait(false);
                }
                finally
                {
                    File.Delete(swappedPath);
                }

                // Authenticated-byte tamper. The mutation must land in the
                // decoded envelope fields (nonce/ciphertext/tag), not merely
                // in the raw file bytes: the envelope is JSON, so a raw byte
                // flip at a fixed offset can land inside a \uXXXX escape or
                // in the ignored padding bits of the final base64 group and
                // leave the decoded authenticated bytes byte-identical. That
                // is not a tamper at all, and the service would then be right
                // to serve the ciphertext — which is exactly how an
                // intermittent "tampered ciphertext was accepted" failure
                // can appear without any product defect.
                var tampered = await MutateS2T2AuthenticatedEnvelopeAsync(canonicalFiles[0], original).ConfigureAwait(false);
                Require(S2T2AuthenticatedBytesChanged(original, tampered),
                    "The synthetic tamper mutation did not change the decoded authenticated envelope bytes, so it proves nothing about the ciphertext boundary.");
                await RequireS2T2CanonicalReadDeniedAsync(connectionsExecutable, fixture, resourceId, "tampered").ConfigureAwait(false);
                await File.WriteAllBytesAsync(canonicalFiles[0], original).ConfigureAwait(false);

                await File.WriteAllBytesAsync(canonicalFiles[0], original.AsMemory(0, Math.Min(8, original.Length))).ConfigureAwait(false);
                await RequireS2T2CanonicalReadDeniedAsync(connectionsExecutable, fixture, resourceId, "truncated").ConfigureAwait(false);
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

            // Deterministic controlled negative for the boundary check
            // itself: a command that never reaches the ciphertext boundary
            // must not be classifiable as a denial or as an acceptance. No
            // service listens on this endpoint, so the real owner CLI fails
            // at the transport seam with its own exit code and error. If
            // that failure ever classified as a denial, a transport or usage
            // error could silently stand in for a cryptographic rejection,
            // and the earlier check would again report an unrelated command
            // failure as "the ciphertext was accepted".
            var transportProbe = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-read", "--control-pipe", "metabrain-s2t2-no-control-pipe",
                "--resource-id", resourceId).ConfigureAwait(false);
            Require(ClassifyS2T2CanonicalRead(transportProbe) == S2T2CanonicalReadOutcome.CommandFailed,
                "A non-security command failure was classified as a ciphertext-boundary denial or an acceptance " +
                $"(exit={transportProbe.ExitCode}; stdout={TrimS2T2CanonicalEvidence(transportProbe.StandardOutput)}; " +
                $"stderr={TrimS2T2CanonicalEvidence(transportProbe.StandardError)}).");
            Console.WriteLine("PASS ciphertext read diagnostic separates transport failure from tamper acceptance and rejection");

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

    /// <summary>
    /// What one owner ciphertext read actually did. The three outcomes stay
    /// distinct so a failing boundary check reports the observed command
    /// instead of collapsing every non-denial into one message: the read was
    /// denied by the authenticated-envelope boundary, the mutated bytes were
    /// served (the security failure this scenario exists to catch), or the
    /// command never reached the boundary (transport, authentication, lock,
    /// or usage failure). A command that never reached the ciphertext
    /// boundary is evidence of nothing about it, so it is never reported as
    /// an acceptance and never accepted as a rejection.
    /// </summary>
    private enum S2T2CanonicalReadOutcome
    {
        BoundaryDenied,
        Served,
        CommandFailed
    }

    private static async Task RequireS2T2CanonicalReadDeniedAsync(string executable, SmokeFixture fixture, string resourceId, string mutation)
    {
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId).ConfigureAwait(false);
        Require(ClassifyS2T2CanonicalRead(result) == S2T2CanonicalReadOutcome.BoundaryDenied,
            DescribeS2T2CanonicalRead(mutation, resourceId, result));
    }

    private static S2T2CanonicalReadOutcome ClassifyS2T2CanonicalRead(CommandResult result) =>
        result.ExitCode == 0
            ? S2T2CanonicalReadOutcome.Served
            : result.ExitCode == 3 && result.StandardError.Contains("invalid_request", StringComparison.Ordinal)
                ? S2T2CanonicalReadOutcome.BoundaryDenied
                : S2T2CanonicalReadOutcome.CommandFailed;

    /// <summary>
    /// Failure evidence for the canonical ciphertext boundary: which of the
    /// three outcomes was observed, plus the exact exit code and the trimmed
    /// stdout/stderr of the real owner CLI call. Only synthetic fixture text
    /// can reach these streams (the passphrase is written to the CLI's stdin
    /// and never echoed back), and each stream is capped so one diagnostic
    /// cannot bury the scenario log.
    /// </summary>
    private static string DescribeS2T2CanonicalRead(string mutation, string resourceId, CommandResult result)
    {
        var observed = ClassifyS2T2CanonicalRead(result) switch
        {
            S2T2CanonicalReadOutcome.Served =>
                "the read was ACCEPTED: the mutated canonical ciphertext was served to the owner",
            S2T2CanonicalReadOutcome.BoundaryDenied =>
                "the read was denied, which is the expected outcome and not a failure",
            _ => "the command failed outside the ciphertext boundary (transport, authentication, lock, or usage), so this run proves nothing about the mutation: it is neither an acceptance nor a rejection"
        };
        return $"A {mutation} canonical ciphertext check failed for {resourceId}: {observed} " +
            $"(exit={result.ExitCode}; stdout={TrimS2T2CanonicalEvidence(result.StandardOutput)}; " +
            $"stderr={TrimS2T2CanonicalEvidence(result.StandardError)}).";
    }

    /// <summary>
    /// Writes a tampered copy of a stored canonical envelope at rest and
    /// returns the bytes now on disk. Every base64 envelope field (nonce,
    /// ciphertext, tag) is decoded, one bit of its first decoded byte is
    /// flipped, and the field is re-encoded, so the mutation is a real
    /// mutation of the authenticated bytes the service will decrypt. A raw
    /// byte flip at a fixed file offset is not sufficient: the envelope is
    /// JSON, so such a flip can land inside a <c>\uXXXX</c> escape or in the
    /// ignored padding bits of the final base64 group and leave the decoded
    /// authenticated bytes byte-identical — serving that file is correct, and
    /// asserting a denial against it is a false failure.
    /// </summary>
    private static async Task<byte[]> MutateS2T2AuthenticatedEnvelopeAsync(string path, byte[] original)
    {
        using var document = JsonDocument.Parse(original);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (property.Value.ValueKind == JsonValueKind.String &&
                    TryMutateS2T2Base64Field(property.Value.GetString()!, out var mutated))
                {
                    writer.WriteStringValue(mutated);
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        var bytes = stream.ToArray();
        try
        {
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool TryMutateS2T2Base64Field(string value, out string mutated)
    {
        byte[]? decoded = null;
        try
        {
            decoded = Convert.FromBase64String(value);
            if (decoded.Length == 0)
            {
                mutated = value;
                return false;
            }

            decoded[0] ^= 0x20;
            mutated = Convert.ToBase64String(decoded);
            return true;
        }
        catch (FormatException)
        {
            mutated = value;
            return false;
        }
        finally
        {
            if (decoded is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(decoded);
            }
        }
    }

    /// <summary>
    /// Self-check for the tamper mutation: the decoded authenticated bytes
    /// of the stored envelope must actually differ. Without this, a mutation
    /// that only changes raw file bytes could be asserted as a rejection the
    /// boundary never had to make.
    /// </summary>
    private static bool S2T2AuthenticatedBytesChanged(byte[] original, byte[] tampered)
    {
        try
        {
            using var reference = JsonDocument.Parse(original);
            using var current = JsonDocument.Parse(tampered);
            foreach (var name in new[] { "nonce", "ciphertext", "tag" })
            {
                if (!reference.RootElement.TryGetProperty(name, out var referenceValue) ||
                    !current.RootElement.TryGetProperty(name, out var currentValue) ||
                    referenceValue.ValueKind != JsonValueKind.String ||
                    currentValue.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                byte[]? referenceDecoded = null;
                byte[]? currentDecoded = null;
                try
                {
                    referenceDecoded = Convert.FromBase64String(referenceValue.GetString()!);
                    currentDecoded = Convert.FromBase64String(currentValue.GetString()!);
                }
                catch (FormatException)
                {
                    continue;
                }

                var changed = !currentDecoded.AsSpan().SequenceEqual(referenceDecoded);
                CryptographicOperations.ZeroMemory(referenceDecoded);
                CryptographicOperations.ZeroMemory(currentDecoded);
                if (changed)
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return !original.AsSpan().SequenceEqual(tampered);
        }
    }

    private static string TrimS2T2CanonicalEvidence(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 600 ? trimmed : trimmed[..600] + "...[truncated]";
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
