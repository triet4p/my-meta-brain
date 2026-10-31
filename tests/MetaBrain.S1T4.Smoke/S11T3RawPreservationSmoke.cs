using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private sealed record S11T3CorpusRun(
        string CaseId,
        string SourceId,
        string SourcePath,
        byte[] OriginalBytes,
        JsonElement ReferenceExpectation,
        JsonElement PreservedExpectation,
        bool OptedIn,
        bool MatchesManifest,
        string ActualOutput);

    private sealed record S11T3Export(string SourceId, long Offset, byte[] ExpectedBytes);

    internal static async Task RunS11T3RawPreservationAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S11-T3 CLI/service smoke requires Windows paths and named pipes.");
        }

        Console.WriteLine("RUN S11-T3 raw preservation through the real owner CLI and service");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        var passphraseBytes = RandomNumberGenerator.GetBytes(24);
        var passphrase = Convert.ToBase64String(passphraseBytes);
        CryptographicOperations.ZeroMemory(passphraseBytes);
        try
        {
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provision = await RunOwnerCommandWithInputAsync(
                connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0 && provision.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The S11-T3 synthetic vault did not provision (exit=" + provision.ExitCode + "; stderr=" + provision.StandardError.Trim() + ").");

            var repositoryRoot = SmokeFixture.FindRepositoryRoot();
            var corpusRoot = Path.Combine(repositoryRoot, "artifacts", "sprint-11", "corpus");
            var manifestPath = Path.Combine(corpusRoot, "manifest.json");
            using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false));
            var corpusRuns = new List<S11T3CorpusRun>();
            var measurementFindings = new List<string>();
            var exports = new List<S11T3Export>();
            var retention = DateTimeOffset.UtcNow.AddDays(30);
            var retentionText = retention.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            var optOutRetrySourceId = "s11t3-codex-v1-unknown-payload-type";

            foreach (var corpusCase in manifest.RootElement.GetProperty("cases").EnumerateArray())
            {
                var caseId = corpusCase.GetProperty("case_id").GetString()
                    ?? throw new InvalidDataException("The approved S11-T1 corpus has a case without an ID.");
                var relativePath = corpusCase.GetProperty("path").GetString()
                    ?? throw new InvalidDataException("The approved S11-T1 corpus has a case without a path.");
                var sourcePath = Path.Combine(fixture.OutputDirectory, caseId + ".jsonl");
                var originalBytes = await File.ReadAllBytesAsync(Path.Combine(corpusRoot, relativePath)).ConfigureAwait(false);
                await File.WriteAllBytesAsync(sourcePath, originalBytes).ConfigureAwait(false);
                var sourceId = "s11t3-" + caseId;
                var expectedReference = corpusCase.GetProperty("expect").GetProperty("reference_only");
                var expectedPreserved = corpusCase.GetProperty("expect").GetProperty("preserved");
                var expectedSpans = expectedPreserved.GetProperty("preserved_spans");
                var optIn = expectedSpans.GetArrayLength() > 0 &&
                    !string.Equals(sourceId, optOutRetrySourceId, StringComparison.Ordinal);

                await EnrollSourceAsync(connectionsExecutable, fixture.ControlPipe, fixture.OutputDirectory,
                    sourceId, corpusCase.GetProperty("format").GetString()!,
                    corpusCase.GetProperty("declared_format_version").GetInt32(), sourcePath).ConfigureAwait(false);

                if (optIn || string.Equals(sourceId, optOutRetrySourceId, StringComparison.Ordinal))
                {
                    var consent = await RunOwnerCommandAsync(
                        connectionsExecutable, "owner", "source-raw-preservation-optin",
                        "--control-pipe", fixture.ControlPipe, "--source-id", sourceId,
                        "--retain-until-utc", retentionText).ConfigureAwait(false);
                    Require(consent.ExitCode == 0 && consent.StandardOutput.Contains("rawPreservationOptIn=true", StringComparison.Ordinal),
                        "The explicit raw-preservation consent operation failed for " + caseId + " (stderr=" + consent.StandardError.Trim() + ").");
                }

                if (string.Equals(sourceId, optOutRetrySourceId, StringComparison.Ordinal))
                {
                    var optedOut = await RunOwnerCommandAsync(
                        connectionsExecutable, "owner", "source-raw-preservation-optout",
                        "--control-pipe", fixture.ControlPipe, "--source-id", sourceId).ConfigureAwait(false);
                    Require(optedOut.ExitCode == 0 && optedOut.StandardOutput.Contains("rawPreservationOptIn=false", StringComparison.Ordinal),
                        "The explicit raw-preservation opt-out operation failed (stderr=" + optedOut.StandardError.Trim() + ").");
                }

                var ingest = await RunOwnerCommandAsync(
                    connectionsExecutable, "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                    "--source-id", sourceId).ConfigureAwait(false);
                Require(ingest.ExitCode == 0 && ingest.StandardOutput.Contains("SOURCE FACETS", StringComparison.Ordinal),
                    "The actual owner source-ingest command failed for " + caseId + " (stdout=" + ingest.StandardOutput.Trim() + "; stderr=" + ingest.StandardError.Trim() + ").");

                var measuredExpectation = optIn ? expectedPreserved : expectedReference;
                var measurementMatches = true;
                try
                {
                    AssertCliMeasurement(caseId, originalBytes, measuredExpectation, optIn, ingest.StandardOutput);
                }
                catch (InvalidOperationException exception)
                {
                    measurementMatches = false;
                    measurementFindings.Add(
                        "case=" + caseId + "; actual_cli=" + ingest.StandardOutput.Trim() +
                        "; contract_mismatch=" + exception.Message);
                }
                if (string.Equals(caseId, "codex-v1-clean", StringComparison.Ordinal))
                {
                    var originalRevision = PrintedToken(ingest.StandardOutput, "journalRevision=");
                    var consent = await RunOwnerCommandAsync(
                        connectionsExecutable, "owner", "source-raw-preservation-optin",
                        "--control-pipe", fixture.ControlPipe, "--source-id", sourceId,
                        "--retain-until-utc", retentionText).ConfigureAwait(false);
                    var unchanged = await RunOwnerCommandAsync(
                        connectionsExecutable, "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                        "--source-id", sourceId).ConfigureAwait(false);
                    Require(consent.ExitCode == 0 && unchanged.ExitCode == 0 &&
                            unchanged.StandardOutput.Contains("SOURCE INGEST status=ingest_idle", StringComparison.Ordinal) &&
                            unchanged.StandardOutput.Contains("rawPreservationOptIn=true", StringComparison.Ordinal) &&
                            unchanged.StandardOutput.Contains("preservation=reference_only", StringComparison.Ordinal) &&
                            PrintedToken(unchanged.StandardOutput, "journalRevision=") == originalRevision,
                        "Owner consent alone upgraded or rewrote a fully supported v1 journal without a v2-only element.");
                }
                else if (string.Equals(caseId, "omp-v3-unknown-record", StringComparison.Ordinal))
                {
                    var originalRevision = PrintedToken(ingest.StandardOutput, "journalRevision=");
                    var optedOut = await RunOwnerCommandAsync(
                        connectionsExecutable, "owner", "source-raw-preservation-optout",
                        "--control-pipe", fixture.ControlPipe, "--source-id", sourceId).ConfigureAwait(false);
                    var unchanged = await RunOwnerCommandAsync(
                        connectionsExecutable, "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                        "--source-id", sourceId).ConfigureAwait(false);
                    Require(optedOut.ExitCode == 0 && unchanged.ExitCode == 0 &&
                            unchanged.StandardOutput.Contains("SOURCE INGEST status=ingest_idle", StringComparison.Ordinal) &&
                            unchanged.StandardOutput.Contains("rawPreservationOptIn=false", StringComparison.Ordinal) &&
                            unchanged.StandardOutput.Contains("preservation=preserved", StringComparison.Ordinal) &&
                            PrintedToken(unchanged.StandardOutput, "journalRevision=") == originalRevision,
                        "Opting out after a v2 write downgraded or rewrote the preserved journal.");
                }
                if (string.Equals(sourceId, optOutRetrySourceId, StringComparison.Ordinal))
                {
                    var unsupportedOffset = measuredExpectation.GetProperty("consumed_offset").GetInt64();
                    var unknownSpan = FindLineSpan(originalBytes, "mystery_payload_v2");
                    Require(unsupportedOffset == unknownSpan.Start &&
                            ingest.StandardOutput.Contains("SOURCE BYTES source_dependent=true", StringComparison.Ordinal) &&
                            ingest.StandardOutput.Contains("preservation=reference_only", StringComparison.Ordinal),
                        "The opt-out unknown record advanced the checkpoint or claimed a vault copy.");

                    var optedInAgain = await RunOwnerCommandAsync(
                        connectionsExecutable, "owner", "source-raw-preservation-optin",
                        "--control-pipe", fixture.ControlPipe, "--source-id", sourceId,
                        "--retain-until-utc", retentionText).ConfigureAwait(false);
                    Require(optedInAgain.ExitCode == 0, "The source could not explicitly opt back into preservation.");
                    var retried = await RunOwnerCommandAsync(
                        connectionsExecutable, "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                        "--source-id", sourceId).ConfigureAwait(false);
                    Require(retried.ExitCode == 0 && retried.StandardOutput.Contains("preservation=preserved", StringComparison.Ordinal) &&
                            retried.StandardOutput.Contains("interpretation=partially_interpreted", StringComparison.Ordinal),
                        "The opted-in retry did not durably preserve the unknown record and continue (stdout=" + retried.StandardOutput.Trim() + ").");
                }

                corpusRuns.Add(new S11T3CorpusRun(
                    caseId, sourceId, sourcePath, originalBytes, expectedReference, expectedPreserved,
                    optIn || string.Equals(sourceId, optOutRetrySourceId, StringComparison.Ordinal),
                    measurementMatches,
                    ingest.StandardOutput));
            }

            Require(corpusRuns.Count == manifest.RootElement.GetProperty("totals").GetProperty("case_count").GetInt32(),
                "The smoke did not exercise every approved S11-T1 corpus case.");
            if (measurementFindings.Count == 0)
            {
                Console.WriteLine("PASS actual owner CLI/service measured all 20 approved corpus cases; opt-out blocked an unknown record and a later explicit opt-in resumed only with preserved bytes");
            }
            else
            {
                Console.WriteLine("OBSERVED actual owner CLI/service outcomes for all 20 approved corpus cases; contract mismatches=" +
                    measurementFindings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    "; independent scenarios continue without changing the approved corpus");
            }
            var chainProbeSourceId = "s11t3-omp-envelope-chain-probe";
            var validOmpBase = await File.ReadAllBytesAsync(
                Path.Combine(corpusRoot, "omp", "omp-v3-clean.jsonl")).ConfigureAwait(false);
            var validParentChain = await File.ReadAllBytesAsync(
                Path.Combine(corpusRoot, "omp", "omp-v3-unknown-parent-link.jsonl")).ConfigureAwait(false);
            var chainProbeBytes = BuildOmpChainProbe(validOmpBase, validParentChain);
            var chainProbePath = Path.Combine(fixture.OutputDirectory, "valid-omp-envelope-chain-probe.jsonl");
            await File.WriteAllBytesAsync(chainProbePath, chainProbeBytes).ConfigureAwait(false);
            await EnrollSourceAsync(connectionsExecutable, fixture.ControlPipe, fixture.OutputDirectory,
                chainProbeSourceId, "omp.session.jsonl", 1, chainProbePath).ConfigureAwait(false);
            var chainProbeConsent = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-raw-preservation-optin",
                "--control-pipe", fixture.ControlPipe, "--source-id", chainProbeSourceId,
                "--retain-until-utc", retentionText).ConfigureAwait(false);
            Require(chainProbeConsent.ExitCode == 0,
                "The supplemental synthetic OMP chain probe could not opt in.");
            var chainProbeIngest = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", chainProbeSourceId).ConfigureAwait(false);
            var chainProbeUnknownSpan = FindLineSpan(chainProbeBytes, "checkpoint_v2");
            Require(chainProbeIngest.ExitCode == 0 &&
                    chainProbeIngest.StandardOutput.Contains("checkpoint consumedOffset=" +
                        chainProbeBytes.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                    chainProbeIngest.StandardOutput.Contains("events=15", StringComparison.Ordinal) &&
                    chainProbeIngest.StandardOutput.Contains("preservedBytes=" +
                        (chainProbeUnknownSpan.End - chainProbeUnknownSpan.Start).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                    chainProbeIngest.StandardOutput.Contains("uninterpretedRecords=1", StringComparison.Ordinal) &&
                    chainProbeIngest.StandardOutput.Contains("code=omp_type_unsupported.checkpoint_v2", StringComparison.Ordinal) &&
                    chainProbeIngest.StandardOutput.Contains("offset=" +
                        chainProbeUnknownSpan.Start.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
                "A valid independently checked OMP envelope did not let the later declared child continue after the unknown node.");
            Require(File.ReadAllBytes(chainProbePath).AsSpan().SequenceEqual(chainProbeBytes),
                "The supplemental chain probe modified its synthetic original source.");
            Console.WriteLine("PASS supplemental synthetic-only OMP probe admitted a validated unknown-envelope identity and its declared child; approved corpus bytes remain unchanged");

            var referenceOnlyBarrierSourceId = "s11t3-omp-barrier-reference-only";
            var referenceOnlyBarrierPath = Path.Combine(fixture.OutputDirectory, "barrier-reference-only.jsonl");
            var referenceOnlyBarrierBytes = await File.ReadAllBytesAsync(
                Path.Combine(corpusRoot, "omp", "omp-v3-session-version-4.jsonl")).ConfigureAwait(false);
            await File.WriteAllBytesAsync(referenceOnlyBarrierPath, referenceOnlyBarrierBytes).ConfigureAwait(false);
            await EnrollSourceAsync(connectionsExecutable, fixture.ControlPipe, fixture.OutputDirectory,
                referenceOnlyBarrierSourceId, "omp.session.jsonl", 1, referenceOnlyBarrierPath).ConfigureAwait(false);
            var referenceBarrier = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", referenceOnlyBarrierSourceId).ConfigureAwait(false);
            var referenceBarrierOffset = FindLineSpan(referenceOnlyBarrierBytes, "\"version\":4").Start;
            Require(referenceBarrier.ExitCode == 0 &&
                    referenceBarrier.StandardOutput.Contains("SOURCE INGEST status=ingest_unsupported_record", StringComparison.Ordinal) &&
                    referenceBarrier.StandardOutput.Contains("checkpoint consumedOffset=" + referenceBarrierOffset.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                    referenceBarrier.StandardOutput.Contains("preservation=reference_only", StringComparison.Ordinal) &&
                    referenceBarrier.StandardOutput.Contains("unprocessedBytes=" +
                        (referenceOnlyBarrierBytes.LongLength - referenceBarrierOffset).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                    referenceBarrier.StandardOutput.Contains("SOURCE BYTES source_dependent=true", StringComparison.Ordinal),
                "The reference-only OMP version barrier advanced or claimed a vault-preserved tail.");


            var folderRoot = Path.Combine(fixture.OutputDirectory, "raw-folder");
            Directory.CreateDirectory(folderRoot);
            var folderUnknownPath = Path.Combine(folderRoot, "unknown.jsonl");
            var unknownOriginal = await File.ReadAllBytesAsync(
                Path.Combine(corpusRoot, "codex", "codex-v1-unknown-envelope-type.jsonl")).ConfigureAwait(false);
            await File.WriteAllBytesAsync(folderUnknownPath, unknownOriginal).ConfigureAwait(false);
            var folderArgs = FolderCommand(fixture.ControlPipe, folderRoot, "**/*.jsonl", "none", recursive: true)
                .Concat(["--raw-preservation-until-utc", retentionText]).ToArray();
            var enrolledFolder = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, "ENROLL" + Environment.NewLine, folderArgs).ConfigureAwait(false);
            Require(enrolledFolder.ExitCode == 0 &&
                    enrolledFolder.StandardOutput.Contains("rawPreservation=opt_in", StringComparison.Ordinal) &&
                    enrolledFolder.StandardOutput.Contains("SOURCE FOLDER enrolled", StringComparison.Ordinal),
                "The folder-level explicit preservation choice was not shown and enrolled (stdout=" + enrolledFolder.StandardOutput.Trim() + "; stderr=" + enrolledFolder.StandardError.Trim() + ").");
            var folderSourceId = FolderMemberSourceId(folderRoot, folderUnknownPath);
            var folderStatus = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", folderSourceId).ConfigureAwait(false);
            Require(folderStatus.ExitCode == 0 && folderStatus.StandardOutput.Contains("rawPreservationOptIn=true", StringComparison.Ordinal) &&
                    folderStatus.StandardOutput.Contains(retentionText, StringComparison.Ordinal),
                "Folder preservation consent was not propagated to its enrolled member.");
            var folderIngest = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-ingest", "--control-pipe", fixture.ControlPipe,
                "--source-id", folderSourceId).ConfigureAwait(false);
            Require(folderIngest.ExitCode == 0 && folderIngest.StandardOutput.Contains("preservation=preserved", StringComparison.Ordinal),
                "A folder-opted-in member did not preserve its unknown source record.");
            Require(File.ReadAllBytes(folderUnknownPath).AsSpan().SequenceEqual(unknownOriginal),
                "Folder member ingestion modified its original source file.");
            Console.WriteLine("PASS explicit folder retention consent propagated through encrypted membership to each enrolled source");

            var crashSourceId = "s11t3-crash-commit";
            var crashSourcePath = Path.Combine(fixture.OutputDirectory, "crash-commit.jsonl");
            var crashSourceBytes = await File.ReadAllBytesAsync(
                Path.Combine(corpusRoot, "codex", "codex-v1-unknown-payload-type.jsonl")).ConfigureAwait(false);
            await File.WriteAllBytesAsync(crashSourcePath, crashSourceBytes).ConfigureAwait(false);
            await EnrollSourceAsync(connectionsExecutable, fixture.ControlPipe, fixture.OutputDirectory,
                crashSourceId, "codex.session.jsonl", 1, crashSourcePath).ConfigureAwait(false);
            var crashConsent = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-raw-preservation-optin", "--control-pipe", fixture.ControlPipe,
                "--source-id", crashSourceId, "--retain-until-utc", retentionText).ConfigureAwait(false);
            Require(crashConsent.ExitCode == 0, "The process-interruption fixture could not opt in.");

            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            var referenceBarrierJournal = ReadJournal(
                fixture.VaultDirectoryPath, passphrase, referenceOnlyBarrierSourceId);
            try
            {
                Require(referenceBarrierJournal is { SchemaVersion: SourceIngestionJournal.CurrentSchemaVersion } &&
                        referenceBarrierJournal.Version2 is null &&
                        referenceBarrierJournal.ConsumedOffset == referenceBarrierOffset &&
                        referenceBarrierJournal.Events.Length == 1 &&
                        referenceBarrierJournal.Events.All(item => item.Span.End <= referenceBarrierOffset),
                    "A reference-only version barrier rewrote the v1 journal or interpreted beyond the barrier.");
            }
            finally
            {
                ClearJournalBytes(referenceBarrierJournal);
            }

            foreach (var run in corpusRuns)
            {
                var journal = ReadJournal(fixture.VaultDirectoryPath, passphrase, run.SourceId);
                try
                {
                    VerifyJournalMeasurement(run, journal, exports);
                }
                finally
                {
                    ClearJournalBytes(journal);
                }
            }
            var chainProbeJournal = ReadJournal(fixture.VaultDirectoryPath, passphrase, chainProbeSourceId);
            try
            {
                var childSpan = FindLineSpan(chainProbeBytes, "\"id\":\"e14\"");
                var child = chainProbeJournal?.Events.SingleOrDefault(item =>
                    item.EventId == "omp-" + chainProbeSourceId + "-e14");
                var preservedNode = chainProbeJournal?.Version2?.PreservedRecords.SingleOrDefault();
                Require(chainProbeJournal is
                        { SchemaVersion: SourceIngestionJournal.PreservationSchemaVersion } &&
                        chainProbeJournal.ConsumedOffset == chainProbeBytes.LongLength &&
                        chainProbeJournal.Events.Length == 15 &&
                        child is { Author: SourceEventAuthor.User } && child.Span == childSpan &&
                        SourceFingerprint.Matches(child.PayloadDigest,
                            chainProbeBytes.AsSpan(checked((int)childSpan.Start),
                                checked((int)(childSpan.End - childSpan.Start)))) &&
                        preservedNode is { ChainIdentity: "e13" } &&
                        preservedNode.Offset == chainProbeUnknownSpan.Start &&
                        preservedNode.Length == chainProbeUnknownSpan.End - chainProbeUnknownSpan.Start &&
                        preservedNode.Bytes.AsSpan().SequenceEqual(chainProbeBytes.AsSpan(
                            checked((int)chainProbeUnknownSpan.Start), preservedNode.Length)) &&
                        SourceFingerprint.Matches(preservedNode.Digest, preservedNode.Bytes) &&
                        !chainProbeJournal.Events.Any(item =>
                            item.Span.Start < chainProbeUnknownSpan.End &&
                            chainProbeUnknownSpan.Start < item.Span.End),
                    "A valid OMP envelope did not preserve the unknown node identity and interpret its declared child without fabricating an event for the node.");
                VerifyPreservedRecords(chainProbeSourceId, chainProbeBytes,
                    chainProbeJournal, exports, expectedBarrier: false);
            }
            finally
            {
                ClearJournalBytes(chainProbeJournal);
            }

            var folderJournal = ReadJournal(fixture.VaultDirectoryPath, passphrase, folderSourceId);
            try
            {
                VerifyPreservedRecords(folderSourceId, unknownOriginal,
                    folderJournal, exports, expectedBarrier: false);
            }
            finally
            {
                ClearJournalBytes(folderJournal);
            }

            var signalPath = Path.Combine(fixture.OutputDirectory, "journal-commit.signal");
            await KillIngestionWorkerAfterJournalCommitAsync(
                fixture.VaultDirectoryPath, passphrase, crashSourceId, signalPath).ConfigureAwait(false);
            Require(File.ReadAllBytes(crashSourcePath).AsSpan().SequenceEqual(crashSourceBytes),
                "The process-interruption ingest modified its original source file.");
            var crashJournal = ReadJournal(fixture.VaultDirectoryPath, passphrase, crashSourceId);
            try
            {
                Require(crashJournal is { SchemaVersion: SourceIngestionJournal.PreservationSchemaVersion } &&
                        crashJournal.ConsumedOffset == crashSourceBytes.Length &&
                        crashJournal.Version2?.PreservedRecords.Length == 1,
                    "A process killed immediately after the atomic journal write did not leave a complete v2 checkpoint/preservation pair.");
                VerifyPreservedRecords(crashSourceId, crashSourceBytes, crashJournal,
                    exports, expectedBarrier: false);
            }
            finally
            {
                ClearJournalBytes(crashJournal);
            }
            Console.WriteLine("PASS process killed after the real encrypted journal commit; restart observed the whole checkpoint and preserved span, never a partial pair");

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var lockedOutput = Path.Combine(fixture.OutputDirectory, "locked-export.bin");
            var lockedRead = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-preserved-record-read", "--control-pipe", fixture.ControlPipe,
                "--source-id", crashSourceId, "--offset", FindLineSpan(crashSourceBytes, "mystery_payload_v2").Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--output-file", lockedOutput).ConfigureAwait(false);
            var lockedFileExists = File.Exists(lockedOutput);
            Require(lockedRead.ExitCode != 0 &&
                    lockedRead.StandardError.Contains("vault_locked", StringComparison.Ordinal) &&
                    !lockedFileExists,
                "A locked restart allowed preserved private bytes to be read or created an output file " +
                "(exit=" + lockedRead.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "; stdout=" + lockedRead.StandardOutput.Trim() + "; stderr=" + lockedRead.StandardError.Trim() +
                "; outputExists=" + lockedFileExists.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").");
            var lockedStatus = await RunOwnerCommandAsync(
                connectionsExecutable, "owner", "source-status", "--control-pipe", fixture.ControlPipe,
                "--source-id", crashSourceId).ConfigureAwait(false);
            Require(lockedStatus.ExitCode != 0 && lockedStatus.StandardError.Contains("vault_locked", StringComparison.Ordinal),
                "Source status served private journal metadata while the vault was locked.");

            var unlock = await RunOwnerCommandWithInputAsync(
                connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0, "The owner could not unlock the restarted preservation vault.");

            foreach (var export in exports)
            {
                var output = Path.Combine(fixture.OutputDirectory, "export-" + exports.IndexOf(export).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin");
                var read = await RunOwnerCommandAsync(
                    connectionsExecutable, "owner", "source-preserved-record-read", "--control-pipe", fixture.ControlPipe,
                    "--source-id", export.SourceId, "--offset", export.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--output-file", output).ConfigureAwait(false);
                Require(read.ExitCode == 0 && File.Exists(output) &&
                        !read.StandardOutput.Contains("mystery_payload_v2", StringComparison.Ordinal) &&
                        File.ReadAllBytes(output).AsSpan().SequenceEqual(export.ExpectedBytes),
                    "Restart/unlock export did not return the exact original bytes for source " + export.SourceId +
                    " at offset " + export.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " (stdout=" + read.StandardOutput.Trim() + "; stderr=" + read.StandardError.Trim() + ").");
            }
            Console.WriteLine("PASS owner exports returned exact original byte spans after service restart and unlock, including invalid UTF-8 and a version-barrier tail");

            var agentOptIn = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceRawPreservationOptInOperation,
                resourceId = crashSourceId,
                rawPreservationRetainUntilUtc = retentionText
            }).ConfigureAwait(false);
            var agentRead = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourcePreservedRecordReadOperation,
                resourceId = crashSourceId,
                sourceRecordOffset = 0
            }).ConfigureAwait(false);
            Require(ReplyCode(agentOptIn) == "forbidden" && ReplyCode(agentRead) == "forbidden",
                "The owner-only raw-preservation consent/read operations created an agent scope or exposed bytes.");
            Console.WriteLine("PASS raw-preservation opt-in and read refused on the agent channel; no new scope or MCP surface");
            if (measurementFindings.Count > 0)
            {
                foreach (var finding in measurementFindings)
                {
                    Console.Error.WriteLine("CORPUS FINDING " + finding);
                }

                throw new InvalidOperationException(
                    "S11-T3 actual product outcomes differ from the approved corpus for " +
                    measurementFindings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " case(s); see the corpus finding above.");
            }
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }


    private static async Task EnrollSourceAsync(
        string executable,
        string controlPipe,
        string outputDirectory,
        string sourceId,
        string format,
        int formatVersion,
        string sourcePath)
    {
        var requestPath = Path.Combine(outputDirectory, "locator-" + sourceId + ".json");
        await File.WriteAllTextAsync(requestPath,
            JsonSerializer.Serialize(new { locator = Path.GetFullPath(sourcePath) }), new UTF8Encoding(false)).ConfigureAwait(false);
        var enrolled = await RunOwnerCommandAsync(
            executable, "owner", "source-enroll", "--control-pipe", controlPipe,
            "--source-id", sourceId, "--zone-id", "s11t3-synthetic-zone",
            "--format", format, "--format-version", formatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--input-file", requestPath).ConfigureAwait(false);
        Require(enrolled.ExitCode == 0 && enrolled.StandardOutput.Contains("SOURCE id=" + sourceId, StringComparison.Ordinal),
            "The actual source enrollment command failed for " + sourceId + " (stdout=" + enrolled.StandardOutput.Trim() + "; stderr=" + enrolled.StandardError.Trim() + ").");
    }

    private static void AssertCliMeasurement(
        string caseId,
        byte[] originalBytes,
        JsonElement expectation,
        bool optedIn,
        string output)
    {
        var interpretation = expectation.GetProperty("interpretation").GetString()!;
        var expectedOffset = expectation.GetProperty("consumed_offset").GetInt64();
        var expectedEventCount = expectation.GetProperty("event_count").GetInt32();
        var spans = expectation.GetProperty("preserved_spans");
        Require(output.Contains("interpretation=" + interpretation, StringComparison.Ordinal) &&
                output.Contains("checkpoint consumedOffset=" + expectedOffset.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                output.Contains("events=" + expectedEventCount.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
            "Actual CLI facets/checkpoint differ from the approved corpus result for " + caseId + " (stdout=" + output.Trim() + ").");
        var preservation = spans.GetArrayLength() > 0 ? "preserved" : "reference_only";
        var expectedDiagnostics = expectation.GetProperty("diagnostics");
        var expectedPendingTail = expectation.GetProperty("pending_tail_bytes").GetInt64();
        var expectedUnaccounted = expectation.GetProperty("unaccounted_tail_bytes").GetInt64();
        var expectedPreservedBytes = spans.EnumerateArray()
            .Sum(item => (long)item.GetProperty("length").GetInt32());
        var expectedBarrier = expectation.GetProperty("version_barrier").GetBoolean() && optedIn;
        var expectedPreservedPending = expectedBarrier ? expectedPreservedBytes : 0;
        var expectedUnprocessed = expectedBarrier ? 0 : expectedUnaccounted - expectedPendingTail;
        var availability = interpretation == "empty" ? "empty" : "available";
        Require(output.Contains("preservation=" + preservation, StringComparison.Ordinal) &&
                output.Contains("availability=" + availability, StringComparison.Ordinal) &&
                output.Contains("derivation=not_indexed", StringComparison.Ordinal) &&
                output.Contains("rawPreservationOptIn=" + (optedIn ? "true" : "false"), StringComparison.Ordinal) &&
                output.Contains("pendingTailBytes=" + expectedPendingTail.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                output.Contains("preservedBytes=" + expectedPreservedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                output.Contains("uninterpretedRecords=" + expectedDiagnostics.GetArrayLength().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                output.Contains("unprocessedBytes=" + expectedUnprocessed.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                output.Contains("preservedPendingBytes=" + expectedPreservedPending.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
            "Actual CLI preservation/availability/interpretation/derivation facets or byte accounting differ for " + caseId + ".");
        if (string.Equals(preservation, "reference_only", StringComparison.Ordinal))
        {
            Require(output.Contains("SOURCE BYTES source_dependent=true", StringComparison.Ordinal),
                "Reference-only source bytes were not labeled source-dependent for " + caseId + ".");
        }

        if (expectedDiagnostics.GetArrayLength() > 0)
        {
            var diagnostic = expectedDiagnostics[expectedDiagnostics.GetArrayLength() - 1];
            var code = diagnostic.GetProperty("code").GetString()!;
            var offset = diagnostic.GetProperty("offset").GetInt64();
            Require(output.Contains("code=" + code, StringComparison.Ordinal) &&
                    output.Contains("offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
                "Actual CLI last diagnostic code/offset differs for " + caseId + ".");
        }

        Require(originalBytes.LongLength > 0 || interpretation == "empty",
            "Only the approved empty-generation case may have zero original bytes.");
    }
    private static string? PrintedToken(string output, string token)
    {
        var start = output.IndexOf(token, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += token.Length;
        var end = start;
        while (end < output.Length && !char.IsWhiteSpace(output[end]))
        {
            end++;
        }

        return output[start..end];
    }

    private static void VerifyJournalMeasurement(
        S11T3CorpusRun run,
        SourceIngestionJournal? journal,
        List<S11T3Export> exports)
    {
        Require(File.ReadAllBytes(run.SourcePath).AsSpan().SequenceEqual(run.OriginalBytes),
            "The source reader or ingestion path modified the original bytes for " + run.CaseId + ".");
        if (run.OriginalBytes.Length == 0)
        {
            Require(journal is null, "The empty-generation source unexpectedly wrote a journal.");
            return;
        }

        if (journal is null)
        {
            throw new InvalidOperationException("The actual source coordinator did not persist a journal for " + run.CaseId + ".");
        }
        var expectation = run.OptedIn ? run.PreservedExpectation : run.ReferenceExpectation;
        if (run.MatchesManifest)
        {
            Require(journal.ConsumedOffset == expectation.GetProperty("consumed_offset").GetInt64() &&
                    journal.Events.Length == expectation.GetProperty("event_count").GetInt32(),
                "Decrypted product journal checkpoint/events differ from the corpus for " + run.CaseId + ".");
            var expectedDiagnostics = expectation.GetProperty("diagnostics").EnumerateArray()
                .Select(item => (Offset: item.GetProperty("offset").GetInt64(), Code: item.GetProperty("code").GetString()!))
                .OrderBy(item => item.Offset).ToArray();
            var actualDiagnostics = journal.Diagnostics.Select(item => (item.Offset, item.Code))
                .OrderBy(item => item.Offset).ToArray();
            Require(actualDiagnostics.SequenceEqual(expectedDiagnostics),
                "Decrypted product journal diagnostics differ from the approved corpus for " + run.CaseId + ".");
        }
        else
        {
            var actualOffset = long.Parse(
                PrintedToken(run.ActualOutput, "consumedOffset=") ?? throw new InvalidOperationException("The actual CLI checkpoint is missing."),
                System.Globalization.CultureInfo.InvariantCulture);
            var actualEventCount = int.Parse(
                PrintedToken(run.ActualOutput, "events=") ?? throw new InvalidOperationException("The actual CLI event count is missing."),
                System.Globalization.CultureInfo.InvariantCulture);
            var actualDiagnosticCount = int.Parse(
                PrintedToken(run.ActualOutput, "uninterpretedRecords=") ?? throw new InvalidOperationException("The actual CLI diagnostic count is missing."),
                System.Globalization.CultureInfo.InvariantCulture);
            var actualDiagnosticCode = PrintedToken(run.ActualOutput, "code=");
            var actualDiagnosticOffset = PrintedToken(run.ActualOutput, "offset=");
            Require(journal.ConsumedOffset == actualOffset && journal.Events.Length == actualEventCount &&
                    journal.Diagnostics.Length == actualDiagnosticCount &&
                    (actualDiagnosticCount == 0 || actualDiagnosticCode is not null &&
                        actualDiagnosticOffset is not null &&
                        journal.Diagnostics.Any(item => item.Code == actualDiagnosticCode &&
                            item.Offset == long.Parse(actualDiagnosticOffset, System.Globalization.CultureInfo.InvariantCulture))),
                "The decrypted actual journal did not match the CLI-observed checkpoint/events/diagnostic for " + run.CaseId + ".");
        }

        var actualPreservedRecords = journal.Version2?.PreservedRecords ?? Array.Empty<PreservedSourceRecord>();
        if (run.MatchesManifest)
        {
            var expectedPreserved = expectation.GetProperty("preserved_spans").EnumerateArray()
                .Select(item => (Offset: item.GetProperty("offset").GetInt64(), Length: item.GetProperty("length").GetInt32()))
                .OrderBy(item => item.Offset).ToArray();
            var actualPreserved = actualPreservedRecords
                .Select(item => (Offset: item.Offset, Length: item.Length)).OrderBy(item => item.Offset).ToArray();
            Require(actualPreserved.SequenceEqual(expectedPreserved),
                "Decrypted product preserved spans differ from the approved corpus for " + run.CaseId + ".");
            var expectBarrier = expectation.GetProperty("version_barrier").GetBoolean() && run.OptedIn;
            Require((journal.Version2?.VersionBarrierOffset is not null) == expectBarrier,
                "The actual journal version-barrier state differs from the approved corpus for " + run.CaseId + ".");
            var expectedSchema = expectedPreserved.Length > 0 || expectBarrier
                ? SourceIngestionJournal.PreservationSchemaVersion
                : SourceIngestionJournal.CurrentSchemaVersion;
            Require(journal.SchemaVersion == expectedSchema,
                "The source journal did not retain v1 for reference-only state or write v2 only for actual preserved/barrier elements: " + run.CaseId + ".");
        }
        else
        {
            var preservedBytes = actualPreservedRecords.Sum(item => (long)item.Length);
            var barrierOffset = journal.Version2?.VersionBarrierOffset;
            var preservedPendingBytes = barrierOffset is { } barrier
                ? actualPreservedRecords.Where(item => item.Offset >= barrier).Sum(item => (long)item.Length)
                : 0;
            var pendingTailBytes = long.Parse(
                PrintedToken(run.ActualOutput, "pendingTailBytes=") ?? throw new InvalidOperationException("The actual CLI pending-tail count is missing."),
                System.Globalization.CultureInfo.InvariantCulture);
            var expectedUnprocessed = journal.AnchorLength - journal.ConsumedOffset -
                pendingTailBytes - preservedPendingBytes;
            var actualPreservedOutput = long.Parse(
                PrintedToken(run.ActualOutput, "preservedBytes=") ?? throw new InvalidOperationException("The actual CLI preserved-byte count is missing."),
                System.Globalization.CultureInfo.InvariantCulture);
            var actualPendingOutput = long.Parse(
                PrintedToken(run.ActualOutput, "preservedPendingBytes=") ?? throw new InvalidOperationException("The actual CLI preserved-pending count is missing."),
                System.Globalization.CultureInfo.InvariantCulture);
            var actualUnprocessedOutput = long.Parse(
                PrintedToken(run.ActualOutput, "unprocessedBytes=") ?? throw new InvalidOperationException("The actual CLI unprocessed-byte count is missing."),
                System.Globalization.CultureInfo.InvariantCulture);
            var actualSchema = actualPreservedRecords.Length > 0 || barrierOffset is not null
                ? SourceIngestionJournal.PreservationSchemaVersion
                : SourceIngestionJournal.CurrentSchemaVersion;
            Require(actualPreservedOutput == preservedBytes &&
                    actualPendingOutput == preservedPendingBytes &&
                    actualUnprocessedOutput == expectedUnprocessed &&
                    journal.SchemaVersion == actualSchema,
                "The decrypted actual journal did not match CLI-observed byte accounting or v1/v2 state for " + run.CaseId + ".");
        }

        foreach (var normalized in journal.Events)
        {
            var start = checked((int)normalized.Span.Start);
            var length = checked((int)(normalized.Span.End - normalized.Span.Start));
            Require(start >= 0 && length > 0 && start + length <= run.OriginalBytes.Length &&
                    SourceFingerprint.Matches(normalized.PayloadDigest, run.OriginalBytes.AsSpan(start, length)),
                "An actual normalized event digest/span does not bind to its original source bytes for " + run.CaseId + ".");
        }

        foreach (var preserved in journal.Version2?.PreservedRecords ?? Array.Empty<PreservedSourceRecord>())
        {
            var start = checked((int)preserved.Offset);
            Require(!journal.Events.Any(normalized =>
                    normalized.Span.Start < preserved.Offset + preserved.Length &&
                    preserved.Offset < normalized.Span.End),
                "A preserved unsupported record was fabricated into a normalized event for " + run.CaseId + ".");
            Require(preserved.Offset + preserved.Length <= run.OriginalBytes.LongLength &&
                    preserved.Bytes.AsSpan().SequenceEqual(run.OriginalBytes.AsSpan(start, preserved.Length)) &&
                    SourceFingerprint.Matches(preserved.Digest, run.OriginalBytes.AsSpan(start, preserved.Length)),
                "An actual encrypted preserved span is not byte-identical to its approved source case: " + run.CaseId + ".");
            exports.Add(new S11T3Export(run.SourceId, preserved.Offset,
                run.OriginalBytes.AsSpan(start, preserved.Length).ToArray()));
        }

        if (string.Equals(run.CaseId, "codex-v1-clean", StringComparison.Ordinal))
        {
            VerifyKnownEvent(journal, run.SourceId, run.OriginalBytes, "\"id\":\"msg-user-1\"",
                "codex-" + run.SourceId + "-o3", SourceEventAuthor.User, run.CaseId);
            VerifyKnownEvent(journal, run.SourceId, run.OriginalBytes, "\"id\":\"msg-assistant-1\"",
                "codex-" + run.SourceId + "-o7", SourceEventAuthor.Assistant, run.CaseId);
            VerifyKnownEvent(journal, run.SourceId, run.OriginalBytes, "\"id\":\"fco-1\"",
                "codex-" + run.SourceId + "-o5", SourceEventAuthor.Tool, run.CaseId);
        }
        else if (string.Equals(run.CaseId, "omp-v3-clean", StringComparison.Ordinal))
        {
            VerifyKnownEvent(journal, run.SourceId, run.OriginalBytes, "\"id\":\"e03\"",
                "omp-" + run.SourceId + "-e03", SourceEventAuthor.User, run.CaseId);
            VerifyKnownEvent(journal, run.SourceId, run.OriginalBytes, "\"id\":\"e04\"",
                "omp-" + run.SourceId + "-e04", SourceEventAuthor.Assistant, run.CaseId);
            VerifyKnownEvent(journal, run.SourceId, run.OriginalBytes, "\"id\":\"e05\"",
                "omp-" + run.SourceId + "-e05", SourceEventAuthor.Tool, run.CaseId);
        }

        if (string.Equals(run.CaseId, "omp-v3-unknown-parent-link", StringComparison.Ordinal))
        {
            Require(journal.Events.Any(item => item.EventId == "omp-" + run.SourceId + "-e14" &&
                    item.Author == SourceEventAuthor.User) &&
                    journal.Version2?.PreservedRecords.Single().ChainIdentity == "e13",
                "An independently validated versioned OMP envelope did not admit its declared child identity.");
        }

        if (string.Equals(run.CaseId, "omp-v3-invalid-envelope-parent-link", StringComparison.Ordinal))
        {
            var expectedSpans = run.PreservedExpectation.GetProperty("preserved_spans")
                .EnumerateArray().ToArray();
            Require(expectedSpans.Length == 2,
                "The invalid-envelope OMP case no longer has its invalid record and dependent child spans.");
            var invalidEnvelopeOffset = expectedSpans[0].GetProperty("offset").GetInt64();
            var unresolvedChildOffset = expectedSpans[1].GetProperty("offset").GetInt64();
            var preservedRecords = journal.Version2?.PreservedRecords ?? Array.Empty<PreservedSourceRecord>();
            var invalidEnvelopeRecord = preservedRecords.Single(item => item.Offset == invalidEnvelopeOffset);
            var unresolvedChildRecord = preservedRecords.Single(item => item.Offset == unresolvedChildOffset);
            Require(journal.Diagnostics.Any(item => item.Code == "omp_record_json_invalid" &&
                        item.Offset == invalidEnvelopeOffset) &&
                    journal.Diagnostics.Any(item => item.Code == "omp_chain_reference_unresolved" &&
                        item.Offset == unresolvedChildOffset) &&
                    !journal.Events.Any(item => item.EventId == "omp-" + run.SourceId + "-e13" ||
                        item.EventId == "omp-" + run.SourceId + "-e14") &&
                    invalidEnvelopeRecord.ChainIdentity is null &&
                    unresolvedChildRecord.ChainIdentity == "e14",
                "The invalid OMP envelope granted identity or its dependent child was interpreted; only the child's own validated identity may be retained.");
        }

        if (journal.Version2?.VersionBarrierOffset is { } versionBarrier)
        {
            Require(!journal.Events.Any(item => item.Span.Start >= versionBarrier),
                "The OMP newer-version barrier allowed interpretation beyond the declared version.");
        }
    }
    private static void VerifyKnownEvent(
        SourceIngestionJournal journal,
        string sourceId,
        byte[] sourceBytes,
        string sourceMarker,
        string expectedEventId,
        SourceEventAuthor expectedAuthor,
        string caseId)
    {
        var span = FindLineSpan(sourceBytes, sourceMarker);
        var normalized = journal.Events.SingleOrDefault(item => item.EventId == expectedEventId);
        Require(normalized is not null && normalized.Author == expectedAuthor &&
                normalized.Span == span &&
                SourceFingerprint.Matches(normalized.PayloadDigest,
                    sourceBytes.AsSpan(checked((int)span.Start), checked((int)(span.End - span.Start)))),
            "Known event identity, declared attribution, exact span or digest changed for " + caseId + ".");
    }

    private static void VerifyPreservedRecords(
        string sourceId,
        byte[] originalBytes,
        SourceIngestionJournal? journal,
        List<S11T3Export> exports,
        bool expectedBarrier)
    {
        var actualJournal = journal ?? throw new InvalidOperationException(
            "The opted-in folder/crash source did not persist a journal.");
        var version2 = actualJournal.Version2 ?? throw new InvalidOperationException(
            "The opted-in folder/crash source did not persist v2 raw bytes.");
        Require(version2.PreservedRecords.Length > 0,
            "The opted-in folder/crash source did not persist original bytes in its encrypted journal.");
        Require((version2.VersionBarrierOffset is not null) == expectedBarrier,
            "The preserved folder/crash source has an unexpected version barrier.");
        foreach (var record in version2.PreservedRecords)
        {
            var start = checked((int)record.Offset);
            Require(record.Offset + record.Length <= originalBytes.LongLength &&
                    record.Bytes.AsSpan().SequenceEqual(originalBytes.AsSpan(start, record.Length)) &&
                    SourceFingerprint.Matches(record.Digest, originalBytes.AsSpan(start, record.Length)),
                "The folder/crash product record bytes do not match the original fixture bytes.");
            exports.Add(new S11T3Export(sourceId, record.Offset,
                originalBytes.AsSpan(start, record.Length).ToArray()));
        }
    }

    private static SourceIngestionJournal? ReadJournal(string vaultDirectory, string passphrase, string sourceId)
    {
        using var lifecycle = new VaultLifecycle(new EncryptedVaultStore(vaultDirectory));
        Require(lifecycle.Unlock(passphrase, useRecoveryCode: false),
            "The smoke could not unlock its own encrypted fixture to inspect actual journal output.");
        using var operation = lifecycle.TryBeginOperation()
            ?? throw new InvalidOperationException("The journal inspection could not acquire the unlocked vault operation.");
        var journalId = SourceIngestionIds.JournalIdFor(sourceId, 1);
        var liveResource = operation.ListResources().FirstOrDefault(item =>
            string.Equals(item.ResourceId, journalId, StringComparison.Ordinal));
        if (liveResource is null)
        {
            return null;
        }

        var encoded = operation.ReadContent(journalId, liveResource.Revision);
        try
        {
            var journal = SourceIngestionJournalCodec.Decode(encoded);
            var containsV2State = Encoding.UTF8.GetString(encoded)
                .Contains("\"version2\"", StringComparison.Ordinal);
            Require(containsV2State == (journal.Version2 is not null),
                "The serialized journal did not omit v2-only state from v1 or retain it for a real v2 journal.");
            return journal;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static void ClearJournalBytes(SourceIngestionJournal? journal)
    {
        foreach (var preserved in journal?.Version2?.PreservedRecords ?? Array.Empty<PreservedSourceRecord>())
        {
            CryptographicOperations.ZeroMemory(preserved.Bytes);
        }
    }

    private static SourceSpan FindLineSpan(byte[] bytes, string marker)
    {
        var markerBytes = Encoding.UTF8.GetBytes(marker);
        var position = bytes.AsSpan().IndexOf(markerBytes);
        Require(position >= 0, "A marker expected in the approved synthetic source was not found: " + marker + ".");
        var start = position;
        while (start > 0 && bytes[start - 1] != (byte)'\n')
        {
            start--;
        }

        var newline = Array.IndexOf(bytes, (byte)'\n', start);
        var end = newline < 0 ? bytes.Length : newline + 1;
        return new SourceSpan(start, end);
    }

    private static byte[] BuildOmpChainProbe(byte[] validBase, byte[] parentChain)
    {
        Require(parentChain.Length > validBase.Length &&
                parentChain.AsSpan(0, validBase.Length).SequenceEqual(validBase),
            "The unknown-parent synthetic source no longer shares the valid OMP baseline prefix.");
        return parentChain;
    }

    private static async Task KillIngestionWorkerAfterJournalCommitAsync(
        string vaultDirectory,
        string passphrase,
        string sourceId,
        string signalPath)
    {
        var executable = Environment.ProcessPath ??
            throw new InvalidOperationException("The S11-T3 process-interruption worker executable path is unavailable.");
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(signalPath)!, Path.GetFileName(signalPath))
        {
            NotifyFilter = NotifyFilters.FileName
        };
        watcher.Created += (_, _) => signal.TrySetResult(true);
        watcher.Renamed += (_, _) => signal.TrySetResult(true);
        watcher.EnableRaisingEvents = true;

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("s11-t3-ingestion-crash-worker");
        start.ArgumentList.Add(vaultDirectory);
        start.ArgumentList.Add(passphrase);
        start.ArgumentList.Add(sourceId);
        start.ArgumentList.Add(signalPath);
        SetDotnetProcessSafety(start);

        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("The S11-T3 commit-boundary worker did not start.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            var exited = process.WaitForExitAsync();
            var winner = await Task.WhenAny(signal.Task, exited).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (winner != signal.Task)
            {
                var output = await standardOutput.ConfigureAwait(false);
                var error = await standardError.ConfigureAwait(false);
                throw new InvalidOperationException(
                    "The journal commit worker exited before the durable write boundary (stdout=" +
                    output.Trim() + "; stderr=" + error.Trim() + ").");
            }

            await signal.Task.ConfigureAwait(false);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            _ = await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        }
    }

    internal static Task RunS11T3IngestionCrashWorkerAsync(
        string vaultDirectory,
        string passphrase,
        string sourceId,
        string signalPath)
    {
        using var lifecycle = new VaultLifecycle(new EncryptedVaultStore(vaultDirectory));
        Require(lifecycle.Unlock(passphrase, useRecoveryCode: false),
            "The S11-T3 commit-boundary worker could not unlock the synthetic vault.");
        using var operation = lifecycle.TryBeginOperation()
            ?? throw new InvalidOperationException("The S11-T3 commit-boundary worker could not lease the vault.");
        var normalizers = new SourceNormalizerRegistry();
        normalizers.Register(new CodexSessionNormalizer());
        normalizers.Register(new OmpSessionNormalizer());
        var coordinator = new SourceIngestionCoordinator(
            new SourceEnrollmentAuthority(), normalizers, new EnrolledSourceFileReader());
        var wrapped = S11T3JournalCommitOperation.Wrap(operation, () =>
        {
            var temporaryPath = signalPath + ".tmp";
            using (var marker = new FileStream(
                       temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough))
            {
                marker.WriteByte(1);
                marker.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, signalPath);
            Console.WriteLine("S11-T3 worker paused after the complete encrypted checkpoint/preservation write");
            new ManualResetEventSlim(initialState: false).Wait();
        });
        _ = coordinator.Ingest(AuthenticatedContext.ForOwner(), sourceId, wrapped, CancellationToken.None);
        throw new InvalidOperationException("The S11-T3 commit-boundary worker unexpectedly returned.");
    }
}

internal class S11T3JournalCommitOperation : DispatchProxy
{
    private IVaultOperation? _inner;
    private Action? _afterJournalCommit;

    public static IVaultOperation Wrap(IVaultOperation inner, Action afterJournalCommit)
    {
        var proxy = DispatchProxy.Create<IVaultOperation, S11T3JournalCommitOperation>();
        ((S11T3JournalCommitOperation)(object)proxy).Initialize(inner, afterJournalCommit);
        return proxy;
    }

    private void Initialize(IVaultOperation inner, Action afterJournalCommit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _afterJournalCommit = afterJournalCommit ?? throw new ArgumentNullException(nameof(afterJournalCommit));
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            throw new InvalidOperationException("The vault operation member is unavailable.");
        }

        object? result;
        try
        {
            result = targetMethod.Invoke(_inner, args ?? Array.Empty<object?>());
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }

        var arguments = args ?? Array.Empty<object?>();
        if (targetMethod.Name == "WriteContent" && arguments.Length > 0 &&
            arguments[0] is string resourceId && resourceId.StartsWith(DomainIdentifiers.IngestIdPrefix, StringComparison.Ordinal))
        {
            _afterJournalCommit!();
        }

        return result;
    }
}
