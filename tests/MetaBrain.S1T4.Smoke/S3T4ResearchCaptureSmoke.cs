using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Index;
using MetaBrain.Core.Security;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    /// <summary>
    /// S3-T4 owner research capture: verbatim unstructured text plus zone (and
    /// optional owner-supplied title/URL/author/date) is persisted encrypted
    /// through the S2-T3 source registry before any structuring job could run.
    /// The whole scenario drives the real service process and the real owner
    /// CLI over the real named pipes: bilingual synthetic note capture,
    /// exact-text read, registry metadata read, agent-channel refusal, a
    /// downstream citation anchor, locked-vault refusal with a proved
    /// no-write/no-plaintext boundary, service restart plus unlock, and a
    /// full-vault plaintext scan. Every note is locally generated synthetic
    /// text; no private source, network, URL fetch or provider is involved.
    /// </summary>
    internal static async Task RunS3T4ResearchCaptureAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S3-T4 research capture smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S3-T4 owner research capture of verbatim text");
        await RunS3T4ResearchCapturePhaseAsync(connectionsExecutable).ConfigureAwait(false);
    }

    private static async Task RunS3T4ResearchCapturePhaseAsync(string connectionsExecutable)
    {
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false))
                    .Contains("vault=uninitialized", StringComparison.Ordinal),
                "The S3-T4 service fixture did not start uninitialized.");
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "Provision did not create the S3-T4 synthetic vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");

            const string zoneId = "s3t4-research-zone";
            const string noteId = "s3t4-note-paper";
            const string bareNoteId = "s3t4-note-bare";
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var textMarker = "S3T4-RESEARCH-TEXT-" + suffix;
            var titleMarker = "S3T4 research title " + suffix;
            var noteUrl = "https://example.invalid/s3t4/paper/" + suffix;
            var linkMarker = "https://example.invalid/s3t4/embedded-link/" + suffix;
            var noteText = string.Concat(
                "Ghi chú research (S3-T4) — ", textMarker, "\r\n",
                "Claim: tác giả báo cáo phương pháp cải thiện độ chính xác trên tác vụ ngắn; chưa rõ với session dài.\n",
                "Câu hỏi: liệu kết quả còn đúng khi tác vụ dài hơn và dữ liệu Việt–Anh hỗn hợp?\n",
                "Link: ", linkMarker, "\n",
                "Mixed line: English notes with Vietnamese dấu — nghiên cứu, thử nghiệm, bằng chứng.\n");
            var noteBytes = Encoding.UTF8.GetBytes(noteText);
            var notePath = Path.Combine(fixture.OutputDirectory, "s3t4-note.txt");
            await File.WriteAllBytesAsync(notePath, noteBytes).ConfigureAwait(false);

            var grantsBefore = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "grants", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(grantsBefore.ExitCode == 0 && grantsBefore.StandardOutput.Contains("GRANTS count=", StringComparison.Ordinal),
                "The pre-capture grant listing failed (exit=" + grantsBefore.ExitCode + ").");

            // Capture with every optional owner-supplied field present. The
            // reply carries metadata and revision only: the text is never
            // echoed back, and nothing about the note is classified.
            var capture = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-capture", "--control-pipe", fixture.ControlPipe,
                "--resource-id", noteId, "--zone-id", zoneId, "--input-file", notePath,
                "--title", titleMarker, "--source-url", noteUrl,
                "--source-author", "user", "--source-date", "2026-10-01T00:00:00Z").ConfigureAwait(false);
            Require(capture.ExitCode == 0 &&
                    capture.StandardOutput.Contains($"RESEARCH captured id={noteId}", StringComparison.Ordinal) &&
                    capture.StandardOutput.Contains($"zone={zoneId}", StringComparison.Ordinal) &&
                    capture.StandardOutput.Contains("author=user", StringComparison.Ordinal) &&
                    capture.StandardOutput.Contains($"title={titleMarker}", StringComparison.Ordinal) &&
                    capture.StandardOutput.Contains($"locator={noteUrl}", StringComparison.Ordinal) &&
                    capture.StandardOutput.Contains("capturedUtc=", StringComparison.Ordinal) &&
                    capture.StandardOutput.Contains("revision=1", StringComparison.Ordinal) &&
                    capture.StandardOutput.Contains($"textBytes={noteBytes.Length}", StringComparison.Ordinal),
                "The owner research capture of the bilingual note failed (exit=" + capture.ExitCode +
                "; stdout=" + capture.StandardOutput.Trim() + "; stderr=" + capture.StandardError.Trim() + ").");
            Require(!capture.StandardOutput.Contains(textMarker, StringComparison.Ordinal) &&
                    !capture.StandardError.Contains(textMarker, StringComparison.Ordinal),
                "The capture reply echoed the captured note text.");
            Console.WriteLine("PASS owner captures a bilingual research note with declared author/title/URL/date through the real owner CLI");

            var grantsAfter = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "grants", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(grantsAfter.ExitCode == 0 &&
                    string.Equals(grantsBefore.StandardOutput, grantsAfter.StandardOutput, StringComparison.Ordinal),
                "The research capture changed owner grant state.");
            Console.WriteLine("PASS capture changes no grant state: no agent authority is minted by saving a note");

            // Exact-text read: the CLI writes the decoded bytes so the
            // comparison is byte-for-byte, and prints the stored metadata.
            var readBack = await S3T4ResearchReadAsync(connectionsExecutable, fixture, noteId, expectedText: noteBytes).ConfigureAwait(false);
            Require(readBack.StandardOutput.Contains($"SOURCE id={noteId}", StringComparison.Ordinal) &&
                    readBack.StandardOutput.Contains("author=user", StringComparison.Ordinal) &&
                    readBack.StandardOutput.Contains($"zone={zoneId}", StringComparison.Ordinal) &&
                    readBack.StandardOutput.Contains("revision=1", StringComparison.Ordinal) &&
                    readBack.StandardOutput.Contains($"bytes={noteBytes.Length}", StringComparison.Ordinal),
                "The research read did not return the stored metadata (stdout=" + readBack.StandardOutput.Trim() + ").");
            Require(!readBack.StandardOutput.Contains(textMarker, StringComparison.Ordinal),
                "The metadata read echoed the captured note text.");
            Console.WriteLine("PASS research read returns the exact note bytes plus its metadata and source zone");

            // The captured note is an ordinary registered source for every
            // metadata-consumer surface; the registry read never carries text.
            var sourceRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", noteId).ConfigureAwait(false);
            Require(sourceRead.ExitCode == 0 &&
                    sourceRead.StandardOutput.Contains($"SOURCE id={noteId}", StringComparison.Ordinal) &&
                    sourceRead.StandardOutput.Contains("author=user", StringComparison.Ordinal) &&
                    sourceRead.StandardOutput.Contains("availability=available", StringComparison.Ordinal),
                "The captured note was not readable as registered source metadata (exit=" + sourceRead.ExitCode +
                "; stdout=" + sourceRead.StandardOutput.Trim() + ").");
            Require(!sourceRead.StandardOutput.Contains(textMarker, StringComparison.Ordinal),
                "The source metadata read leaked the captured note text.");
            Console.WriteLine("PASS the captured note is a registered source whose metadata reads never carry its text");

            // No optional metadata: author/URL/date stay unknown. The URL that
            // appears inside the text is a link in the note, never adopted as
            // the source locator, and nothing fetches it.
            var bareText = string.Concat(
                "Ghi chú không metadata (S3-T4) — ", textMarker, "-bare\n",
                "Đoạn tham khảo: ", linkMarker, "\n");
            var bareBytes = Encoding.UTF8.GetBytes(bareText);
            var barePath = Path.Combine(fixture.OutputDirectory, "s3t4-note-bare.txt");
            await File.WriteAllBytesAsync(barePath, bareBytes).ConfigureAwait(false);
            var bareCapture = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-capture", "--control-pipe", fixture.ControlPipe,
                "--resource-id", bareNoteId, "--zone-id", zoneId, "--input-file", barePath).ConfigureAwait(false);
            Require(bareCapture.ExitCode == 0 &&
                    bareCapture.StandardOutput.Contains($"RESEARCH captured id={bareNoteId}", StringComparison.Ordinal) &&
                    bareCapture.StandardOutput.Contains("author=unknown", StringComparison.Ordinal) &&
                    !bareCapture.StandardOutput.Contains("capturedUtc=", StringComparison.Ordinal),
                "The metadata-free capture did not keep author/date unknown (exit=" + bareCapture.ExitCode +
                "; stdout=" + bareCapture.StandardOutput.Trim() + ").");
            var bareReadBack = await S3T4ResearchReadAsync(connectionsExecutable, fixture, bareNoteId, expectedText: bareBytes).ConfigureAwait(false);
            Require(bareReadBack.StandardOutput.Contains("author=unknown", StringComparison.Ordinal) &&
                    !bareReadBack.StandardOutput.Contains(linkMarker, StringComparison.Ordinal) &&
                    !bareReadBack.StandardOutput.Contains("capturedUtc=", StringComparison.Ordinal),
                "An omitted URL/author/date was not kept unknown (stdout=" + bareReadBack.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS omitted URL/author/date stay unknown; a link inside the text is never adopted as metadata");

            // Explicit capture refusals: an unknown author role, a duplicate
            // ID (create-only, nothing overwritten), text that is not valid
            // UTF-8, and a source date that cannot precede its capture are all
            // refused with nothing saved. A frozen revision reads exactly; a
            // stale revision fails closed.
            var badRole = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-capture", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "s3t4-note-badrole", "--zone-id", zoneId,
                "--input-file", notePath, "--source-author", "robot").ConfigureAwait(false);
            Require(badRole.ExitCode == 4 && badRole.StandardError.Contains("invalid_request", StringComparison.Ordinal) &&
                    !badRole.StandardOutput.Contains("RESEARCH captured", StringComparison.Ordinal),
                "An unknown author role was not refused (exit=" + badRole.ExitCode + ").");
            var duplicate = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-capture", "--control-pipe", fixture.ControlPipe,
                "--resource-id", noteId, "--zone-id", zoneId,
                "--input-file", notePath).ConfigureAwait(false);
            Require(duplicate.ExitCode == 4 && duplicate.StandardError.Contains("invalid_request", StringComparison.Ordinal) &&
                    !duplicate.StandardOutput.Contains("RESEARCH captured", StringComparison.Ordinal),
                "A duplicate capture ID was not refused by the create-only gate (exit=" + duplicate.ExitCode + ").");
            var duplicateRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "source-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", noteId).ConfigureAwait(false);
            Require(duplicateRead.ExitCode == 0 && duplicateRead.StandardOutput.Contains("revision=1", StringComparison.Ordinal),
                "The duplicate capture attempt changed the stored note revision.");
            var badUtf8Path = Path.Combine(fixture.OutputDirectory, "s3t4-note-badutf8.bin");
            await File.WriteAllBytesAsync(badUtf8Path, new byte[] { 0xFF, 0xFE, 0x41 }).ConfigureAwait(false);
            var badUtf8 = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-capture", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "s3t4-note-badutf8", "--zone-id", zoneId,
                "--input-file", badUtf8Path).ConfigureAwait(false);
            Require(badUtf8.ExitCode == 4 && badUtf8.StandardError.Contains("invalid_request", StringComparison.Ordinal) &&
                    !badUtf8.StandardOutput.Contains("RESEARCH captured", StringComparison.Ordinal),
                "Non-UTF-8 capture text was not refused (exit=" + badUtf8.ExitCode + ").");
            var futureDate = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-capture", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "s3t4-note-futuredate", "--zone-id", zoneId,
                "--input-file", notePath, "--source-date", "2030-01-01T00:00:00Z").ConfigureAwait(false);
            Require(futureDate.ExitCode == 4 && futureDate.StandardError.Contains("invalid_request", StringComparison.Ordinal) &&
                    !futureDate.StandardOutput.Contains("RESEARCH captured", StringComparison.Ordinal),
                "A source date that cannot precede its capture was not refused (exit=" + futureDate.ExitCode + ").");
            var ghostBadRole = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "s3t4-note-badrole").ConfigureAwait(false);
            Require(ghostBadRole.ExitCode != 0 && !ghostBadRole.StandardOutput.Contains("RESEARCH text", StringComparison.Ordinal),
                "A refused capture left a readable note behind.");
            Console.WriteLine("PASS capture refusals are explicit (unknown author role, duplicate ID, invalid UTF-8, future source date) and save nothing");

            await S3T4ResearchReadAsync(connectionsExecutable, fixture, noteId, expectedText: noteBytes, revision: "1").ConfigureAwait(false);
            var staleRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", noteId, "--resource-revision", "2").ConfigureAwait(false);
            Require(staleRead.ExitCode != 0 && !staleRead.StandardOutput.Contains("RESEARCH text", StringComparison.Ordinal),
                "A stale revision read did not fail closed.");
            Console.WriteLine("PASS a frozen note revision reads exactly and a stale revision fails closed");

            // The agent channel cannot reach capture or read at all.
            var agentCapture = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "research.capture",
                resourceId = "s3t4-note-agent",
                zoneId,
                contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("agent attempt"))
            }).ConfigureAwait(false);
            Require(ReplyCode(agentCapture) == "forbidden",
                "The agent channel reached a research capture operation.");
            var agentRead = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "research.read",
                resourceId = noteId
            }).ConfigureAwait(false);
            Require(ReplyCode(agentRead) == "forbidden",
                "The agent channel reached a research read operation.");
            Console.WriteLine("PASS the agent channel is refused for research capture and read by the transport allowlist");

            // Downstream source references: a memory item can anchor a citation
            // to the note's live revision, and a stale anchor fails closed.
            var citationDraft = JsonSerializer.Serialize(new
            {
                kind = "observation",
                author = "user",
                creationMethod = "verbatimExtract",
                evidence = "reportedSource",
                level = "l0",
                title = "S3-T4 citation probe",
                body = "Synthetic citation probe referencing the captured research note.",
                sources = new[]
                {
                    new { sourceId = noteId, sourceRevision = 1, span = new { start = 0, end = 120 } }
                },
                recordedAtUtc = DateTimeOffset.UtcNow
            });
            var citationPath = Path.Combine(fixture.OutputDirectory, "s3t4-citation.json");
            await File.WriteAllTextAsync(citationPath, citationDraft).ConfigureAwait(false);
            var cited = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "s3t4-citation-item", "--zone-id", zoneId,
                "--input-file", citationPath).ConfigureAwait(false);
            Require(cited.ExitCode == 0 && cited.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
                "A memory item could not cite the captured note at its live revision (exit=" + cited.ExitCode +
                "; stdout=" + cited.StandardOutput.Trim() + "; stderr=" + cited.StandardError.Trim() + ").");
            var staleDraft = citationDraft.Replace("\"sourceRevision\":1", "\"sourceRevision\":2", StringComparison.Ordinal);
            var stalePath = Path.Combine(fixture.OutputDirectory, "s3t4-citation-stale.json");
            await File.WriteAllTextAsync(stalePath, staleDraft).ConfigureAwait(false);
            var stale = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "s3t4-citation-stale", "--zone-id", zoneId,
                "--input-file", stalePath).ConfigureAwait(false);
            Require(stale.ExitCode != 0 && !stale.StandardOutput.Contains("MEMORY stored", StringComparison.Ordinal),
                "A stale citation anchor was accepted against the captured note.");
            Console.WriteLine("PASS the captured note anchors downstream citations at its live revision; a stale anchor fails closed");

            // The derived private index re-materializes over captured notes.
            var rebuild = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(rebuild.ExitCode == 0 && rebuild.StandardOutput.Contains("INDEX rebuilt", StringComparison.Ordinal),
                "The private index rebuild failed over the captured notes (exit=" + rebuild.ExitCode +
                "; stderr=" + rebuild.StandardError.Trim() + ").");
            Console.WriteLine("PASS the private index rebuilds over captured notes without plaintext");

            // Locked vault: capture/read are refused before anything is
            // written, and the whole service tree is byte-identical after the
            // refusals (no plaintext draft, queue, or half-saved note).
            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The owner could not lock the S3-T4 vault.");
            var treeBefore = S3T4SnapshotTree(fixture.ServiceDirectoryPath);
            var lockedText = "Locked draft attempt " + textMarker + " must never be saved.";
            var lockedPath = Path.Combine(fixture.OutputDirectory, "s3t4-note-locked.txt");
            await File.WriteAllTextAsync(lockedPath, lockedText).ConfigureAwait(false);
            var lockedCapture = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-capture", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "s3t4-note-locked", "--zone-id", zoneId,
                "--input-file", lockedPath).ConfigureAwait(false);
            Require(lockedCapture.ExitCode == 4 &&
                    lockedCapture.StandardError.Contains("vault_locked", StringComparison.Ordinal) &&
                    !lockedCapture.StandardOutput.Contains("RESEARCH captured", StringComparison.Ordinal),
                "A locked vault did not refuse the research capture clearly (exit=" + lockedCapture.ExitCode +
                "; stdout=" + lockedCapture.StandardOutput.Trim() + "; stderr=" + lockedCapture.StandardError.Trim() + ").");
            Require(!lockedCapture.StandardOutput.Contains(textMarker, StringComparison.Ordinal) &&
                    !lockedCapture.StandardError.Contains(lockedText, StringComparison.Ordinal),
                "The locked capture attempt echoed or persisted draft text.");
            var lockedRead = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", noteId).ConfigureAwait(false);
            Require(lockedRead.ExitCode == 4 && lockedRead.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "A locked vault did not refuse the research read (exit=" + lockedRead.ExitCode + ").");
            var treeAfter = S3T4SnapshotTree(fixture.ServiceDirectoryPath);
            Require(treeBefore.SequenceEqual(treeAfter, StringComparer.Ordinal),
                "A locked capture/read attempt changed the service tree (a plaintext draft or half-saved write).");
            Console.WriteLine("PASS a locked vault refuses capture and read with no write, no saved report and no plaintext draft");

            // Real restart: the encrypted note survives process restart and
            // unlock, and the refused locked attempt saved nothing.
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var restartStatus = await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false);
            Require(restartStatus.Contains("vault=locked", StringComparison.Ordinal),
                "The restarted S3-T4 service did not start locked (status=" + restartStatus.Trim() + ").");
            var unlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0, "The owner could not unlock the restarted S3-T4 vault.");
            await S3T4ResearchReadAsync(connectionsExecutable, fixture, noteId, expectedText: noteBytes).ConfigureAwait(false);
            await S3T4ResearchReadAsync(connectionsExecutable, fixture, bareNoteId, expectedText: bareBytes).ConfigureAwait(false);
            var ghost = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "research-read", "--control-pipe", fixture.ControlPipe,
                "--resource-id", "s3t4-note-locked").ConfigureAwait(false);
            Require(ghost.ExitCode != 0 && !ghost.StandardOutput.Contains("RESEARCH text", StringComparison.Ordinal),
                "The refused locked capture left a readable note behind.");
            Console.WriteLine("PASS captured text survives restart and unlock byte-for-byte, and the refused attempt saved nothing");

            // No provider path: the capture surface has no provider/model/budget
            // option, and a raw body that carries one is refused outright, so
            // saving a note can never depend on (or trigger) a provider call.
            var providerAttempt = await S3T4InvokeOwnerRawAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "research.capture",
                resourceId = "s3t4-note-provider",
                zoneId,
                contentBase64 = Convert.ToBase64String(noteBytes),
                provider = "synthetic-provider",
                model = "synthetic-model"
            }).ConfigureAwait(false);
            Require(ReplyCode(providerAttempt) == "invalid_request",
                "A capture body carrying provider/model fields was not refused (code=" + ReplyCode(providerAttempt) + ").");
            Console.WriteLine("PASS capture refuses provider/model fields: saving a note never needs or triggers a provider");

            // encryption at rest: nothing plaintext under the vault, settings
            // or service tree, and no temp/draft residue anywhere.
            var vaultFiles = Directory.GetFiles(fixture.VaultDirectoryPath, "*", SearchOption.AllDirectories);
            Require(vaultFiles.Length >= 3, "The encrypted vault did not persist its private files.");
            foreach (var path in vaultFiles)
            {
                var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                Require(!text.Contains(textMarker, StringComparison.Ordinal) &&
                        !text.Contains(linkMarker, StringComparison.Ordinal) &&
                        !text.Contains(noteUrl, StringComparison.Ordinal) &&
                        !text.Contains(titleMarker, StringComparison.Ordinal),
                    "An encrypted vault file persisted plaintext note content or metadata: " + Path.GetFileName(path) + ".");
            }

            var settingsText = await File.ReadAllTextAsync(fixture.SettingsPath).ConfigureAwait(false);
            Require(!settingsText.Contains(textMarker, StringComparison.Ordinal) &&
                    !settingsText.Contains(noteUrl, StringComparison.Ordinal),
                "The service settings persisted research capture content.");
            Require(!HasTemporaryFiles(fixture.VaultDirectoryPath) && !HasTemporaryFiles(fixture.ServiceDirectoryPath),
                "Research capture left a plaintext temp file in the vault or service tree.");
            Console.WriteLine("PASS research capture is encrypted end to end with no plaintext note, draft or temp file");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Reads one captured note through the real owner CLI with --output-file
    /// and compares the written bytes exactly.
    /// </summary>
    private static async Task<CommandResult> S3T4ResearchReadAsync(
        string executable,
        SmokeFixture fixture,
        string noteId,
        byte[] expectedText,
        string? revision = null)
    {
        var output = Path.Combine(fixture.OutputDirectory, noteId + "-" + Guid.NewGuid().ToString("N") + ".out");
        var arguments = new List<string>
        {
            "owner", "research-read", "--control-pipe", fixture.ControlPipe,
            "--resource-id", noteId
        };
        if (revision is not null)
        {
            arguments.Add("--resource-revision");
            arguments.Add(revision);
        }

        arguments.Add("--output-file");
        arguments.Add(output);
        var read = await RunOwnerCommandAsync(executable, arguments.ToArray()).ConfigureAwait(false);
        Require(read.ExitCode == 0 && File.Exists(output),
            "The actual owner CLI did not return the captured note (id=" + noteId + "; exit=" + read.ExitCode +
            "; stderr=" + read.StandardError.Trim() + ").");
        try
        {
            var actual = await File.ReadAllBytesAsync(output).ConfigureAwait(false);
            Require(actual.AsSpan().SequenceEqual(expectedText),
                "The research read did not reproduce the exact captured text bytes (id=" + noteId + ").");
        }
        finally
        {
            File.Delete(output);
        }

        return read;
    }

    /// <summary>
    /// Ordered (relative path, length, last-write ticks) snapshot of one owned
    /// tree: two snapshots must be identical for "nothing was written".
    /// </summary>
    private static string[] S3T4SnapshotTree(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => path[(root.Length + 1)..])
            .Order(StringComparer.Ordinal)
            .Select(relative =>
            {
                var info = new FileInfo(Path.Combine(root, relative));
                return relative + "|" + info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                       "|" + info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            })
            .ToArray();

    private static async Task<JsonElement> S3T4InvokeOwnerRawAsync(string pipeName, object request)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var authentication = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        Require(authentication is not null && authentication.Contains("\"authenticated\":true", StringComparison.Ordinal),
            "The owner pipe did not authenticate the synthetic raw client.");
        await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token).ConfigureAwait(false);
        var reply = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        if (reply is null)
        {
            throw new InvalidOperationException("The actual owner pipe closed before its one-line response.");
        }

        using var document = JsonDocument.Parse(reply);
        return document.RootElement.Clone();
    }
}
