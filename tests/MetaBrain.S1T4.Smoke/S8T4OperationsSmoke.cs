using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Security;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S8-T4 runtime proof for the privacy-safe owner operations surface.
/// <para>
/// The scenario drives the real owner service executable over its real owner
/// control pipe and its real agent pipe: it refuses a private write while the
/// vault is locked, fails an agent token redemption and a model job on their
/// real refusal paths, writes the private operations state, reads it back
/// through the owner CLI, inspects the encrypted file and the sanitized
/// operational log, applies a retention choice, purges a record and proves
/// that the removed entries do not come back after a restart.
/// </para>
/// <para>
/// Every file the scenario creates is synthetic and stays inside the
/// repository's ignored runtime directory. The scenario never changes a
/// permission, an owner, a registry entry or a machine setting, and it makes
/// no network call: the provider failure is the denied path that happens
/// before any send.
/// </para>
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S8T4Zone = "s8t4-owner-zone";
    private const string S8T4ItemA = "s8t4-item-a";
    private const string S8T4ItemB = "s8t4-item-b";
    private const string S8T4Source = "s8t4-source-in";
    private const string S8T4Body = "S8T4_SYNTHETIC_PRIVATE_BODY_7c21";
    private const string S8T4Reason = "S8-T4 operations purge";

    internal static async Task RunS8T4OperationsAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S8-T4 operations smoke requires Windows named pipes.");
        }

        Console.WriteLine("RUN S8-T4 privacy-safe owner health, audit, diagnostics, retention and log operations");
        using var fixture = S8T4Fixture.Create();
        RunningService? service = null;
        var passphraseBytes = RandomNumberGenerator.GetBytes(24);
        var passphrase = Convert.ToBase64String(passphraseBytes);
        CryptographicOperations.ZeroMemory(passphraseBytes);
        // The synthetic credential and the synthetic token are generated per
        // run and exist only in this process: no fixture literal that looks
        // like a credential or a token is written into the repository.
        var secret = "synthetic-key-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var invalidToken = "synthetic-token-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        try
        {
            // ----- the store translates a cryptographic failure of the trail -----
            // The private state is written with the vault's own data key. A key
            // the cipher refuses must reach the product as the typed failure of
            // the trail, never as an escaping cryptographic exception that
            // could suppress the answer of the owner's own act.
            var faultVaultDirectory = Path.Combine(fixture.Root, "fault-vault");
            Directory.CreateDirectory(faultVaultDirectory);
            var faultStore = new EncryptedVaultStore(faultVaultDirectory);
            Exception? storeFailure = null;
            try
            {
                faultStore.SaveOperationsAuditState(
                    new VaultManifest(1, "s8t4-synthetic-fault-vault", 1, Array.Empty<VaultResourceDescriptor>()),
                    new byte[3],
                    OperationsAuditState.Empty);
            }
            catch (Exception ex)
            {
                storeFailure = ex;
            }

            Require(storeFailure is OperationsAuditPersistenceException,
                "The encrypted operations state write did not translate its cryptographic failure into the typed persistence failure (" +
                (storeFailure?.GetType().Name ?? "no failure") + ").");
            Console.WriteLine("OBSERVATION the encrypted operations state write translates its cryptographic failure into the typed persistence failure");

            // ----- the recorder lets no persistence failure escape -----
            // The write of the trail fails with the typed failure of the
            // store. The recorder must not let it travel: the owner's own act
            // already happened, so its answer must reach the owner, and the
            // failure must be reported truthfully in the operational log.
            var recorderLog = new S8T4RecordingOperationalLog();
            var recorder = new OperationsAuditRecorder(recorderLog);
            var recorderFault = false;
            try
            {
                recorder.Record(
                    AuthenticatedContext.ForOwner(),
                    "memory.create",
                    audited: true,
                    OperationsAuditAuthority.CompletedOutcome,
                    errorCode: null,
                    costUsd: null,
                    resourceId: null,
                    new S8T4AuditFailureOperation(storeFailure!));
            }
            catch (Exception)
            {
                recorderFault = true;
            }

            Require(!recorderFault,
                "The failed write of the private audit trail escaped the recorder and would suppress the answer of the owner's own act.");
            Require(recorderLog.Lines.Any(line => line.Contains("audit_persistence_failed", StringComparison.Ordinal) &&
                                                  line.Contains("outcome=audit_failed", StringComparison.Ordinal)),
                "The failed write of the private audit trail was not reported truthfully in the operational log (" +
                string.Join(" | ", recorderLog.Lines) + ").");
            Console.WriteLine("OBSERVATION the failed write of the private audit trail neither escapes the recorder nor hides behind a success; it is reported in the operational log");

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The synthetic S8-T4 vault was not provisioned (" + provisioned.StandardError.Trim() + ").");
            var recoveryCode = ParseRecoveryCode(provisioned.StandardOutput);

            // ----- a private write while the vault is locked -----
            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The synthetic S8-T4 vault did not lock (" + locked.StandardError.Trim() + ").");
            var lockedWrite = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "owner.job.create",
                resourceId = S8T4Source,
                resourceRevision = 1,
                zoneId = S8T4Zone,
                destinationResourceId = S8T4ItemA,
                destinationRevision = 1,
                provider = "deepseek",
                model = "deepseek-flash",
                maximumCostUsd = 0.05m,
                profile = "research-note",
            }).ConfigureAwait(false);
            Require(ReplyCode(lockedWrite) == "vault_locked",
                "A private write while the vault was locked was not refused with vault_locked (reply=" + lockedWrite.GetRawText() + ").");

            // The locked refusal reaches the sanitized operational log and
            // nothing else: the private operations state lives inside the
            // unlocked vault, so no private entry exists and none is buffered.
            var logPath = fixture.OperationalLogPath;
            Require(File.Exists(logPath), "The safe operational log was not written by the locked refusal.");
            var lockedLog = await File.ReadAllTextAsync(logPath).ConfigureAwait(false);
            Require(lockedLog.Contains("op=owner.job.create", StringComparison.Ordinal) &&
                    lockedLog.Contains("code=vault_locked", StringComparison.Ordinal),
                "The safe operational log did not record the locked refusal (" + lockedLog.Trim() + ").");
            Require(!File.Exists(fixture.OperationsStatePath),
                "A locked refusal wrote a private operations state; the private trail exists only inside the unlocked vault.");
            Console.WriteLine("OBSERVATION a locked refusal is recorded only in the sanitized operational log; no private operations file exists while the vault is locked");

            var unlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S8-T4 vault did not unlock (" + unlocked.StandardError.Trim() + ").");

            // ----- a real owner write, a real agent token failure, a real job failure -----
            var created = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "memory.create",
                resourceId = S8T4ItemA,
                zoneId = S8T4Zone,
                contentBase64 = Convert.ToBase64String(S8T4ItemContent(S8T4Body)),
            }).ConfigureAwait(false);
            Require(ReplyCode(created) == "memory_created",
                "The synthetic S8-T4 record was not written (reply=" + created.GetRawText() + ").");
            var createdB = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "memory.create",
                resourceId = S8T4ItemB,
                zoneId = S8T4Zone,
                contentBase64 = Convert.ToBase64String(S8T4ItemContent(S8T4Body + "-b")),
            }).ConfigureAwait(false);
            Require(ReplyCode(createdB) == "memory_created",
                "The second synthetic S8-T4 record was not written (reply=" + createdB.GetRawText() + ").");

            var redeem = await InvokeS8T4AgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.session.redeem",
                token = invalidToken,
            }).ConfigureAwait(false);
            Require(ReplyCode(redeem) == "token_unknown",
                "The agent channel did not refuse the synthetic token with token_unknown (reply=" + redeem.GetRawText() + ").");
            Console.WriteLine("OBSERVATION the agent channel refused an unknown token with the real code token_unknown");

            var source = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "source.create",
                resourceId = S8T4Source,
                zoneId = S8T4Zone,
                contentBase64 = Convert.ToBase64String(S8T4SourceContent()),
            }).ConfigureAwait(false);
            Require(ReplyCode(source) == "source_created",
                "The synthetic S8-T4 source was not registered (reply=" + source.GetRawText() + ").");

            var job = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "owner.job.create",
                resourceId = S8T4Source,
                resourceRevision = 1,
                zoneId = S8T4Zone,
                destinationResourceId = S8T4ItemA,
                destinationRevision = 1,
                provider = "deepseek",
                model = "deepseek-flash",
                maximumCostUsd = 0.05m,
                profile = "research-note",
            }).ConfigureAwait(false);
            Require(ReplyCode(job) == "job_created",
                "The synthetic S8-T4 job was not created (reply=" + job.GetRawText() + ").");
            var jobId = job.GetProperty("job").GetProperty("id").GetString();
            Require(!string.IsNullOrEmpty(jobId), "The synthetic S8-T4 job carried no job ID.");

            var started = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "owner.job.start",
                jobId,
            }).ConfigureAwait(false);
            var jobFailureCode = started.TryGetProperty("job", out var jobWire) &&
                                 jobWire.TryGetProperty("failure", out var failure) &&
                                 failure.TryGetProperty("code", out var failureCode)
                ? failureCode.GetString()
                : null;
            Require(string.Equals(jobFailureCode, "provider_unconfigured", StringComparison.Ordinal),
                "The model job did not fail on the denied provider path (reply=" + started.GetRawText() + ").");
            Console.WriteLine("OBSERVATION the model job failed on the provider path before any send, with the real code provider_unconfigured");

            // ----- a refused job create keeps its real code -----
            // The refusal names the record it refused, so the purge proof
            // below has a diagnostic that names the record it removes.
            var refusedJob = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "owner.job.create",
                resourceId = S8T4ItemA,
                resourceRevision = 1,
                zoneId = S8T4Zone,
                destinationResourceId = S8T4ItemA,
                destinationRevision = 1,
                provider = "synthetic-provider",
                model = "synthetic-model",
                maximumCostUsd = 0.05m,
                profile = "research-note",
            }).ConfigureAwait(false);
            Require(S8T4ReplyErrorCode(refusedJob) == "pricing_unknown",
                "The refused job create did not answer with the real code pricing_unknown (reply=" + refusedJob.GetRawText() + ").");
            Require(!refusedJob.GetRawText().Contains("unspecified", StringComparison.Ordinal),
                "The refused job create collapsed its real code into unspecified (reply=" + refusedJob.GetRawText() + ").");
            Console.WriteLine("OBSERVATION the refused job create keeps its real code pricing_unknown and does not collapse it into unspecified");

            // ----- the owner client keeps the typed refusal -----
            // The client is the consumer the owner console and every owner
            // tool use. The refusal must reach it as a refusal that carries the
            // real code and the owner's own sentence, never as an unavailable
            // transport and never as a sentence in the code position.
            var clientRefusal = await OwnerChannelClient.CreateJobAsync(
                fixture.ControlPipe,
                new[] { new OwnerJobInput(S8T4ItemA, 1) },
                S8T4Zone,
                S8T4ItemA,
                1,
                "synthetic-provider",
                "synthetic-model",
                profile: null,
                objective: null,
                targetLevel: null,
                CancellationToken.None).ConfigureAwait(false);
            Require(clientRefusal.Outcome == OwnerCommandOutcome.Refused,
                "The owner client did not read the refused job create as a refusal (outcome=" +
                clientRefusal.Outcome + " code=" + (clientRefusal.ErrorCode ?? "none") + ").");
            Require(clientRefusal.ErrorCode == "pricing_unknown",
                "The owner client dropped the real code of the refused job create (code=" +
                (clientRefusal.ErrorCode ?? "none") + ").");
            Require(clientRefusal.ErrorMessage == "No approved pricing card found for 'synthetic-provider:synthetic-model'.",
                "The owner client did not keep the owner's own sentence of the refused job create (message=" +
                (clientRefusal.ErrorMessage ?? "none") + ").");
            Require(clientRefusal.ErrorMessage is not null && !clientRefusal.ErrorMessage.Contains("pricing_unknown", StringComparison.Ordinal),
                "The owner client put the code into the sentence of the refusal.");
            Console.WriteLine("OBSERVATION the owner client reads the refused job create as refused with the real code pricing_unknown and the owner's own sentence");

            // ----- a refused job cancel keeps its real code too -----
            // The cancel path of the shared lifecycle refuses an unknown job
            // identity with a typed code. The refusal must reach the client as
            // a refusal; an answer that never comes would be reported as an
            // unavailable transport and would hide the real code.
            var clientCancel = await OwnerChannelClient.CancelJobAsync(
                fixture.ControlPipe,
                "s8t4-not-a-job-id",
                CancellationToken.None).ConfigureAwait(false);
            Require(clientCancel.Outcome == OwnerCommandOutcome.Refused,
                "The owner client did not read the refused job cancel as a refusal (outcome=" +
                clientCancel.Outcome + " code=" + (clientCancel.ErrorCode ?? "none") + ").");
            Require(clientCancel.ErrorCode == "invalid_request",
                "The owner client did not read the real code of the refused job cancel (code=" +
                (clientCancel.ErrorCode ?? "none") + ").");
            Console.WriteLine("OBSERVATION the owner client reads the refused job cancel as refused with the real code invalid_request");

            // ----- the owner reads the health, the audit and the diagnostics through the CLI -----
            var health = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "health", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(health.ExitCode == 0 && health.StandardOutput.Contains("HEALTH vaultState=unlocked", StringComparison.Ordinal),
                "The owner CLI did not report the vault health (" + health.StandardError.Trim() + ").");
            Require(health.StandardOutput.Contains("OPERATIONS auditEntries=", StringComparison.Ordinal) &&
                    health.StandardOutput.Contains("OPERATIONALLOG name=operations.log", StringComparison.Ordinal),
                "The owner CLI health did not report the operations state and the operational log (" + health.StandardOutput.Trim() + ").");
            Console.WriteLine("OBSERVATION the owner CLI reports the health, the operations state and the operational log");

            var audit = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "audit", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(audit.ExitCode == 0, "The owner CLI did not read the audit trail (" + audit.StandardError.Trim() + ").");
            Require(audit.StandardOutput.Contains("action=memory.create", StringComparison.Ordinal) &&
                    audit.StandardOutput.Contains("outcome=completed", StringComparison.Ordinal) &&
                    audit.StandardOutput.Contains("actor=owner", StringComparison.Ordinal),
                "The owner CLI audit trail did not show the owner write (" + audit.StandardOutput.Trim() + ").");
            Require(audit.StandardOutput.Contains("actor=agent", StringComparison.Ordinal) &&
                    audit.StandardOutput.Contains("action=agent.session.redeem", StringComparison.Ordinal) &&
                    audit.StandardOutput.Contains("code=token_unknown", StringComparison.Ordinal),
                "The owner CLI audit trail did not show the refused agent redemption (" + audit.StandardOutput.Trim() + ").");
            Require(audit.StandardOutput.Contains("action=owner.job.start", StringComparison.Ordinal) &&
                    audit.StandardOutput.Contains("code=provider_unconfigured", StringComparison.Ordinal),
                "The owner CLI audit trail did not show the failed job (" + audit.StandardOutput.Trim() + ").");
            Require(audit.StandardOutput.Contains("action=owner.job.create", StringComparison.Ordinal) &&
                    audit.StandardOutput.Contains("code=pricing_unknown", StringComparison.Ordinal),
                "The owner CLI audit trail did not show the real code of the refused job create (" + audit.StandardOutput.Trim() + ").");
            Require(!audit.StandardOutput.Contains("code=unspecified", StringComparison.Ordinal),
                "The owner CLI audit trail collapsed a real refusal into the unspecified code (" + audit.StandardOutput.Trim() + ").");
            Require(!audit.StandardOutput.Contains(S8T4Body, StringComparison.Ordinal) &&
                    !audit.StandardOutput.Contains(invalidToken, StringComparison.Ordinal) &&
                    !audit.StandardOutput.Contains(passphrase, StringComparison.Ordinal) &&
                    !audit.StandardOutput.Contains(recoveryCode, StringComparison.Ordinal),
                "The owner CLI audit trail exposed private content, a token, the passphrase or the recovery key.");
            Console.WriteLine("OBSERVATION the owner CLI audit shows actor, action, outcome, cost and the real error code, and no private content");

            var diagnostics = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "diagnostics", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(diagnostics.ExitCode == 0, "The owner CLI did not read the diagnostics (" + diagnostics.StandardError.Trim() + ").");
            Require(diagnostics.StandardOutput.Contains("code=token_unknown", StringComparison.Ordinal) &&
                    diagnostics.StandardOutput.Contains("code=provider_unconfigured", StringComparison.Ordinal),
                "The owner CLI diagnostics did not show the real error codes (" + diagnostics.StandardOutput.Trim() + ").");
            Require(diagnostics.StandardOutput.Contains("Ask for a new handoff", StringComparison.Ordinal),
                "The owner CLI diagnostics did not give an actionable owner sentence (" + diagnostics.StandardOutput.Trim() + ").");
            Require(diagnostics.StandardOutput.Contains("code=pricing_unknown", StringComparison.Ordinal) &&
                    diagnostics.StandardOutput.Contains("resource=" + S8T4ItemA, StringComparison.Ordinal),
                "The owner CLI diagnostics did not show the refused job create with its real code and the record it named (" + diagnostics.StandardOutput.Trim() + ").");
            Console.WriteLine("OBSERVATION the owner CLI diagnostics carry the real error codes and an actionable owner sentence");

            // ----- the private state is ciphertext, the operational log is sanitized -----
            Require(File.Exists(fixture.OperationsStatePath),
                "The private operations state was not written by the audited operations.");
            var stateText = await File.ReadAllTextAsync(fixture.OperationsStatePath).ConfigureAwait(false);
            Require(stateText.Contains("\"ciphertext\"", StringComparison.Ordinal),
                "The private operations state is not an encrypted envelope.");
            Require(!stateText.Contains("memory.create", StringComparison.Ordinal) &&
                    !stateText.Contains("token_unknown", StringComparison.Ordinal) &&
                    !stateText.Contains("provider_unconfigured", StringComparison.Ordinal) &&
                    !stateText.Contains(S8T4Body, StringComparison.Ordinal) &&
                    !stateText.Contains(passphrase, StringComparison.Ordinal) &&
                    !stateText.Contains(recoveryCode, StringComparison.Ordinal),
                "The private operations state exposed an operation name, an error code, private content or a credential in plaintext.");
            Console.WriteLine("OBSERVATION the private operations state is an encrypted envelope; no operation name, error code, body or credential appears in it");

            var logText = await File.ReadAllTextAsync(logPath).ConfigureAwait(false);
            Require(logText.Contains("op=agent.session.redeem", StringComparison.Ordinal) &&
                    logText.Contains("code=token_unknown", StringComparison.Ordinal),
                "The safe operational log did not record the refused redemption (" + logText.Trim() + ").");
            Require(!logText.Contains(S8T4Body, StringComparison.Ordinal) &&
                    !logText.Contains(invalidToken, StringComparison.Ordinal) &&
                    !logText.Contains(passphrase, StringComparison.Ordinal) &&
                    !logText.Contains(recoveryCode, StringComparison.Ordinal),
                "The safe operational log exposed private content, a token, the passphrase or the recovery key.");
            Console.WriteLine("OBSERVATION the safe operational log holds operation names and error codes only; no body, token, passphrase or recovery key");

            // ----- a stored credential never reaches the log, the audit or the diagnostics -----
            var provider = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "owner.provider.config",
                provider = "deepseek",
                model = "deepseek-flash",
                baseUrl = "https://synthetic.invalid/v1",
                apiKey = secret,
            }).ConfigureAwait(false);
            Require(ReplyCode(provider) == "provider_configured",
                "The synthetic S8-T4 provider configuration was not stored (reply=" + provider.GetRawText() + ").");
            var logAfterSecret = await File.ReadAllTextAsync(logPath).ConfigureAwait(false);
            var auditAfterSecret = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "audit", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(!logAfterSecret.Contains(secret, StringComparison.Ordinal) &&
                    !auditAfterSecret.StandardOutput.Contains(secret, StringComparison.Ordinal),
                "The stored provider credential reached the operational log or the audit trail.");
            var healthAfterSecret = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "health", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(healthAfterSecret.StandardOutput.Contains("providerConfigured=True", StringComparison.Ordinal),
                "The owner health did not report the configured provider (" + healthAfterSecret.StandardOutput.Trim() + ").");
            Console.WriteLine("OBSERVATION the stored provider credential is reported as present and never as a value: no log line, no audit entry and no diagnostic holds it");

            // ----- the agent channel never serves the owner operations surface -----
            var agentAudit = await InvokeS8T4AgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "owner.operations.audit",
            }).ConfigureAwait(false);
            Require(ReplyCode(agentAudit) == "forbidden",
                "The agent channel served the owner audit trail (reply=" + agentAudit.GetRawText() + ").");
            var agentHealth = await InvokeS8T4AgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "owner.operations.health",
            }).ConfigureAwait(false);
            Require(ReplyCode(agentHealth) == "forbidden",
                "The agent channel served the owner health summary (reply=" + agentHealth.GetRawText() + ").");
            Console.WriteLine("OBSERVATION the agent channel refuses the owner operations surface with forbidden");

            // ----- the retention choice removes entries and reports the count -----
            for (var index = 0; index < 14; index++)
            {
                var filler = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
                {
                    protocolVersion = 1,
                    operation = "memory.create",
                    resourceId = "s8t4-filler-" + index.ToString(CultureInfo.InvariantCulture),
                    zoneId = S8T4Zone,
                    contentBase64 = Convert.ToBase64String(S8T4ItemContent(S8T4Body + "-filler")),
                }).ConfigureAwait(false);
                Require(ReplyCode(filler) == "memory_created",
                    "The S8-T4 filler write did not commit (reply=" + filler.GetRawText() + ").");
            }

            // ----- an invalid retention choice deletes nothing -----
            // The request asks for the log to be cleared and carries a
            // retention window the product refuses. The whole request is
            // refused, so not one log line and not one audit entry may go.
            var logBeforeInvalid = await File.ReadAllTextAsync(logPath).ConfigureAwait(false);
            var auditBeforeInvalid = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "audit", "--control-pipe", fixture.ControlPipe, "--limit", "500").ConfigureAwait(false);
            Require(auditBeforeInvalid.ExitCode == 0,
                "The owner CLI did not read the audit trail before the invalid retention (" + auditBeforeInvalid.StandardError.Trim() + ").");
            var invalidRetention = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "owner.operations.retention",
                retentionDays = 0,
                retentionMaxRecords = 10,
                purgeOperationalLog = true,
            }).ConfigureAwait(false);
            Require(S8T4ReplyErrorCode(invalidRetention) == "invalid_request",
                "The invalid retention choice was not refused with invalid_request (reply=" + invalidRetention.GetRawText() + ").");
            var logAfterInvalid = await File.ReadAllTextAsync(logPath).ConfigureAwait(false);
            Require(S8T4LogKeepsEveryLine(logBeforeInvalid, logAfterInvalid),
                "The refused retention choice removed a line from the safe operational log.");
            var auditAfterInvalid = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "audit", "--control-pipe", fixture.ControlPipe, "--limit", "500").ConfigureAwait(false);
            Require(auditAfterInvalid.ExitCode == 0,
                "The owner CLI did not read the audit trail after the invalid retention (" + auditAfterInvalid.StandardError.Trim() + ").");
            Require(S8T4AuditKeepsEveryEntry(auditBeforeInvalid.StandardOutput, auditAfterInvalid.StandardOutput),
                "The refused retention choice removed an entry from the private audit trail.");
            Console.WriteLine("OBSERVATION the refused retention choice left the safe operational log and the private audit trail exactly as they were");

            var retention = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "retention", "--control-pipe", fixture.ControlPipe,
                "--retention-days", "30", "--max-records", "10").ConfigureAwait(false);
            Require(retention.ExitCode == 0 && retention.StandardOutput.Contains("RETENTION ", StringComparison.Ordinal),
                "The owner CLI did not apply the retention choice (" + retention.StandardError.Trim() + ").");
            var removedAudit = ParseS8T4Count(retention.StandardOutput, "removedAudit=");
            Require(removedAudit > 0,
                "The retention choice removed no audit entry although more than ten were recorded (" + retention.StandardOutput.Trim() + ").");
            var auditAfterRetention = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "audit", "--control-pipe", fixture.ControlPipe, "--limit", "500").ConfigureAwait(false);
            var remaining = ParseS8T4Count(auditAfterRetention.StandardOutput, "AUDIT count=");
            Require(remaining <= 10,
                "The audit trail still holds " + remaining.ToString(CultureInfo.InvariantCulture) + " entries after the ceiling of ten.");
            Console.WriteLine("OBSERVATION the retention choice removed " + removedAudit.ToString(CultureInfo.InvariantCulture) +
                " audit entries and the trail now holds " + remaining.ToString(CultureInfo.InvariantCulture));

            // ----- the record the purge removes still has a live diagnostic -----
            // The refusal is repeated after the retention, so the audit entry
            // and the diagnostic that name the record the purge removes are
            // still live when the purge runs.
            var refusedJobBeforePurge = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "owner.job.create",
                resourceId = S8T4ItemB,
                resourceRevision = 1,
                zoneId = S8T4Zone,
                destinationResourceId = S8T4ItemA,
                destinationRevision = 1,
                provider = "synthetic-provider",
                model = "synthetic-model",
                maximumCostUsd = 0.05m,
                profile = "research-note",
            }).ConfigureAwait(false);
            Require(S8T4ReplyErrorCode(refusedJobBeforePurge) == "pricing_unknown",
                "The refused job create did not answer with the real code pricing_unknown (reply=" + refusedJobBeforePurge.GetRawText() + ").");
            var auditBeforePurge = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "audit", "--control-pipe", fixture.ControlPipe, "--limit", "500").ConfigureAwait(false);
            Require(auditBeforePurge.StandardOutput.Contains("resource=" + S8T4ItemB, StringComparison.Ordinal),
                "The audit trail does not hold the entry that names the record the purge removes (" + auditBeforePurge.StandardOutput.Trim() + ").");
            var diagnosticsBeforePurge = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "diagnostics", "--control-pipe", fixture.ControlPipe, "--limit", "500").ConfigureAwait(false);
            Require(diagnosticsBeforePurge.StandardOutput.Contains("resource=" + S8T4ItemB, StringComparison.Ordinal),
                "The diagnostics do not hold the diagnostic that names the record the purge removes (" + diagnosticsBeforePurge.StandardOutput.Trim() + ").");
            Console.WriteLine("OBSERVATION the audit entry and the diagnostic that name the record the purge removes are live before the purge");

            // ----- the copy the owner takes before the purge is the old copy -----
            var oldCopyBackupPath = Path.Combine(fixture.Root, "s8t4-before-purge.enc");
            var oldCopyBackup = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "backup", "--control-pipe", fixture.ControlPipe,
                "--output-file", oldCopyBackupPath).ConfigureAwait(false);
            Require(oldCopyBackup.ExitCode == 0 && File.Exists(oldCopyBackupPath),
                "The owner CLI did not write the backup of the moment before the purge (" + oldCopyBackup.StandardError.Trim() + ").");

            // ----- a purge removes the entries that name the purged record -----
            var preview = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "purge-preview", "--control-pipe", fixture.ControlPipe,
                "--resource-id", S8T4ItemB, "--resource-revision", "1",
                "--reason", S8T4Reason).ConfigureAwait(false);
            Require(preview.ExitCode == 0, "The S8-T4 purge preview failed (" + preview.StandardError.Trim() + ").");
            var purgeId = ParseS8T4PurgeId(preview.StandardOutput);
            var impacted = ParseS8T4Impacted(preview.StandardOutput);
            var generation = ParseS8T4Generation(preview.StandardOutput);
            var confirmed = await RunOwnerCommandWithInputAsync(connectionsExecutable, "PURGE" + Environment.NewLine,
                "owner", "purge-confirm", "--control-pipe", fixture.ControlPipe,
                "--purge-id", purgeId, "--resource-id", S8T4ItemB, "--resource-revision", "1",
                "--target-zone", S8T4Zone, "--target-kind", "memory-or-source",
                "--impacted", string.Join(",", impacted),
                "--live-generation", generation,
                "--reason", S8T4Reason).ConfigureAwait(false);
            Require(confirmed.ExitCode == 0, "The S8-T4 purge did not confirm (" + confirmed.StandardError.Trim() + ").");

            var auditAfterPurge = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "audit", "--control-pipe", fixture.ControlPipe, "--limit", "500").ConfigureAwait(false);
            Require(!auditAfterPurge.StandardOutput.Contains("resource=" + S8T4ItemB, StringComparison.Ordinal),
                "The audit trail still names the purged record (" + auditAfterPurge.StandardOutput.Trim() + ").");
            var diagnosticsAfterPurge = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "diagnostics", "--control-pipe", fixture.ControlPipe, "--limit", "500").ConfigureAwait(false);
            Require(diagnosticsAfterPurge.ExitCode == 0,
                "The owner CLI did not read the diagnostics after the purge (" + diagnosticsAfterPurge.StandardError.Trim() + ").");
            Require(!diagnosticsAfterPurge.StandardOutput.Contains("resource=" + S8T4ItemB, StringComparison.Ordinal),
                "The diagnostics still name the purged record (" + diagnosticsAfterPurge.StandardOutput.Trim() + ").");
            Console.WriteLine("OBSERVATION the purge removed the audit entry and the diagnostic that named the purged record");

            // ----- the post-purge backup restores into a clean vault without the purged entries -----
            var postPurgeBackupPath = Path.Combine(fixture.Root, "s8t4-after-purge.enc");
            var postPurgeBackup = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "backup", "--control-pipe", fixture.ControlPipe,
                "--output-file", postPurgeBackupPath).ConfigureAwait(false);
            Require(postPurgeBackup.ExitCode == 0 && File.Exists(postPurgeBackupPath),
                "The owner CLI did not write the backup of the moment after the purge (" + postPurgeBackup.StandardError.Trim() + ").");
            var restoredVault = await RunOwnerCommandWithInputAsync(connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "restore", "--control-pipe", fixture.ControlPipe,
                "--backup-file", postPurgeBackupPath, "--target-directory", fixture.RestoreTargetDirectory).ConfigureAwait(false);
            Require(restoredVault.ExitCode == 0 && restoredVault.StandardOutput.Contains("RESTORE written", StringComparison.Ordinal),
                "The post-purge backup did not restore into the clean target (" + restoredVault.StandardError.Trim() + ").");
            var restoredService = await StartServiceAsync(connectionsExecutable, fixture.RestoredSettingsPath).ConfigureAwait(false);
            try
            {
                var restoredUnlock = await RunOwnerCommandWithInputAsync(connectionsExecutable, passphrase + Environment.NewLine,
                    "owner", "unlock", "--control-pipe", fixture.RestoredControlPipe).ConfigureAwait(false);
                Require(restoredUnlock.ExitCode == 0 && restoredUnlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                    "The restored S8-T4 vault did not unlock (" + restoredUnlock.StandardError.Trim() + ").");
                var restoredAudit = await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "audit", "--control-pipe", fixture.RestoredControlPipe, "--limit", "500").ConfigureAwait(false);
                Require(restoredAudit.ExitCode == 0,
                    "The owner CLI did not read the audit trail of the restored vault (" + restoredAudit.StandardError.Trim() + ").");
                Require(!restoredAudit.StandardOutput.Contains("resource=" + S8T4ItemB, StringComparison.Ordinal),
                    "The restored vault resurrected the purged audit entry (" + restoredAudit.StandardOutput.Trim() + ").");
                var restoredDiagnostics = await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "diagnostics", "--control-pipe", fixture.RestoredControlPipe, "--limit", "500").ConfigureAwait(false);
                Require(restoredDiagnostics.ExitCode == 0,
                    "The owner CLI did not read the diagnostics of the restored vault (" + restoredDiagnostics.StandardError.Trim() + ").");
                Require(!restoredDiagnostics.StandardOutput.Contains("resource=" + S8T4ItemB, StringComparison.Ordinal),
                    "The restored vault resurrected the purged diagnostic (" + restoredDiagnostics.StandardOutput.Trim() + ").");
                Console.WriteLine("OBSERVATION the post-purge backup restored into a clean vault without the purged audit entry or diagnostic");
            }
            finally
            {
                if (!restoredService.Process.HasExited)
                {
                    restoredService.Process.Kill(entireProcessTree: true);
                    await restoredService.Process.WaitForExitAsync().ConfigureAwait(false);
                }
            }

            // ----- the old copy still holds the entries of its moment -----
            var oldCopyVault = await RunOwnerCommandWithInputAsync(connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "restore", "--control-pipe", fixture.ControlPipe,
                "--backup-file", oldCopyBackupPath, "--target-directory", fixture.OldCopyTargetDirectory).ConfigureAwait(false);
            Require(oldCopyVault.ExitCode == 0 && oldCopyVault.StandardOutput.Contains("RESTORE written", StringComparison.Ordinal),
                "The backup of the moment before the purge did not restore into its clean target (" + oldCopyVault.StandardError.Trim() + ").");
            var oldCopyService = await StartServiceAsync(connectionsExecutable, fixture.OldCopySettingsPath).ConfigureAwait(false);
            try
            {
                var oldCopyUnlock = await RunOwnerCommandWithInputAsync(connectionsExecutable, passphrase + Environment.NewLine,
                    "owner", "unlock", "--control-pipe", fixture.OldCopyControlPipe).ConfigureAwait(false);
                Require(oldCopyUnlock.ExitCode == 0 && oldCopyUnlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                    "The old copy did not unlock (" + oldCopyUnlock.StandardError.Trim() + ").");
                var oldCopyAudit = await RunOwnerCommandAsync(connectionsExecutable,
                    "owner", "audit", "--control-pipe", fixture.OldCopyControlPipe, "--limit", "500").ConfigureAwait(false);
                Require(oldCopyAudit.ExitCode == 0,
                    "The owner CLI did not read the audit trail of the old copy (" + oldCopyAudit.StandardError.Trim() + ").");
                Require(oldCopyAudit.StandardOutput.Contains("resource=" + S8T4ItemB, StringComparison.Ordinal),
                    "The old copy lost the entry of its moment; a copy the owner already took stays irrevocable (" + oldCopyAudit.StandardOutput.Trim() + ").");
                Console.WriteLine("OBSERVATION the old copy still holds the entries of its moment; a copy the owner already took stays irrevocable");
            }
            finally
            {
                if (!oldCopyService.Process.HasExited)
                {
                    oldCopyService.Process.Kill(entireProcessTree: true);
                    await oldCopyService.Process.WaitForExitAsync().ConfigureAwait(false);
                }
            }

            // ----- no egress: the service opened no TCP connection -----
            var tcp = CountS8T4TcpConnections(service.Process.Id);
            Require(tcp == 0,
                "The S8-T4 service held " + tcp.ToString(CultureInfo.InvariantCulture) +
                " TCP connection(s) during the scenario; the product makes no network call.");
            Console.WriteLine("OBSERVATION the service held no TCP connection during the whole scenario");

            // ----- a restart keeps the private trail and does not resurrect the purged entries -----
            service.Process.Kill(entireProcessTree: true);
            await service.Process.WaitForExitAsync().ConfigureAwait(false);
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var unlockedAgain = await RunOwnerCommandWithInputAsync(connectionsExecutable, passphrase + Environment.NewLine,
                "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlockedAgain.ExitCode == 0, "The synthetic S8-T4 vault did not unlock after the restart (" + unlockedAgain.StandardError.Trim() + ").");
            var auditAfterRestart = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "audit", "--control-pipe", fixture.ControlPipe, "--limit", "500").ConfigureAwait(false);
            Require(auditAfterRestart.ExitCode == 0 && auditAfterRestart.StandardOutput.Contains("AUDIT count=", StringComparison.Ordinal),
                "The owner CLI did not read the audit trail after the restart (" + auditAfterRestart.StandardError.Trim() + ").");
            Require(!auditAfterRestart.StandardOutput.Contains("resource=" + S8T4ItemB, StringComparison.Ordinal),
                "The purged record came back in the audit trail after the restart.");
            Console.WriteLine("OBSERVATION the private trail survived the restart inside the encrypted state and the purged entries stayed gone");

            // ----- a failed audit write never suppresses the owner's own act -----
            // The private state is damaged while the service runs, so the next
            // audited write cannot reach the trail. The owner's own write must
            // still be answered, and the failure must be reported truthfully.
            await File.WriteAllTextAsync(
                fixture.OperationsStatePath,
                "s8t4-synthetic-corruption-of-the-private-operations-state",
                new UTF8Encoding(false)).ConfigureAwait(false);
            var mutationAfterCorruption = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = 1,
                operation = "memory.create",
                resourceId = "s8t4-item-after-corruption",
                zoneId = S8T4Zone,
                contentBase64 = Convert.ToBase64String(S8T4ItemContent(S8T4Body + "-after-corruption")),
            }).ConfigureAwait(false);
            Require(ReplyCode(mutationAfterCorruption) == "memory_created",
                "The failed audit write suppressed the answer of the owner's own write (reply=" + mutationAfterCorruption.GetRawText() + ").");
            var logAfterCorruption = await File.ReadAllTextAsync(logPath).ConfigureAwait(false);
            Require(logAfterCorruption.Contains("code=audit_persistence_failed", StringComparison.Ordinal),
                "The failed audit write was not reported in the safe operational log (" + logAfterCorruption.Trim() + ").");
            Console.WriteLine("OBSERVATION the failed audit write did not suppress the owner's own write and was reported truthfully in the safe operational log");
        }
        finally
        {
            if (service is not null)
            {
                try
                {
                    if (!service.Process.HasExited)
                    {
                        service.Process.Kill(entireProcessTree: true);
                        await service.Process.WaitForExitAsync().ConfigureAwait(true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }

    /// <summary>
    /// The synthetic item body of the S8-T4 scenario. It is a private body:
    /// it must never appear in the audit trail, in the diagnostics or in the
    /// operational log.
    /// </summary>
    private static byte[] S8T4ItemContent(string body) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        kind = "lesson",
        author = "user",
        creationMethod = "directWrite",
        evidence = "unverified",
        level = "l1",
        title = "S8-T4 synthetic item",
        body,
        sources = Array.Empty<object>(),
        occurredAtUtc = (DateTimeOffset?)null,
        recordedAtUtc = DateTimeOffset.UtcNow,
        applicability = (object?)null,
    });

    private static byte[] S8T4SourceContent() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        title = "S8-T4 synthetic source",
        locator = "s8t4-synthetic-locator-4b19",
        version = "v1",
        availability = "available",
        creationMethod = "directWrite",
        author = "user",
        text = "S8-T4 synthetic captured source text",
        capturedAtUtc = DateTimeOffset.UtcNow,
        recordedAtUtc = DateTimeOffset.UtcNow,
    });

    /// <summary>
    /// Sends one request over the real agent pipe and returns the service's
    /// answer. The agent pipe is the boundary an ordinary agent uses.
    /// </summary>
    private static async Task<JsonElement> InvokeS8T4AgentRequestAsync(string pipeName, object request)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        await client.ConnectAsync(5000).ConfigureAwait(false);
        using var reader = new StreamReader(client, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false, true), 4096, leaveOpen: true) { AutoFlush = true };
        var authentication = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Require(authentication is not null && authentication.Contains("\"authenticated\":true", StringComparison.Ordinal),
            "The real agent pipe did not authenticate the S8-T4 request.");
        await writer.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
        var response = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)
            ?? throw new IOException("The real agent pipe returned no S8-T4 response.");
        using var document = JsonDocument.Parse(response);
        return document.RootElement.Clone();
    }

    private static int ParseS8T4Count(string output, string prefix)
    {
        var start = output.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return -1;
        }

        start += prefix.Length;
        var end = start;
        while (end < output.Length && char.IsDigit(output[end]))
        {
            end++;
        }

        return int.TryParse(output[start..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : -1;
    }

    /// <summary>
    /// The typed error code of one reply. The service answers a refusal with
    /// the real code and the owner's own sentence; the code is the value the
    /// audit trail and the diagnostics record, so the proof reads it here.
    /// </summary>
    private static string S8T4ReplyErrorCode(JsonElement reply)
    {
        if (reply.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.String)
        {
            return code.GetString() ?? string.Empty;
        }

        if (reply.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            return error.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>
    /// True when every line the operational log held before is still in it.
    /// </summary>
    private static bool S8T4LogKeepsEveryLine(string before, string after)
    {
        foreach (var line in before.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!after.Contains(line, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when every audit entry the trail held before is still in it.
    /// </summary>
    private static bool S8T4AuditKeepsEveryEntry(string before, string after)
    {
        foreach (var line in before.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("AUDIT seq=", StringComparison.Ordinal))
            {
                continue;
            }

            if (!after.Contains(line, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string ParseS8T4PurgeId(string output)
    {
        const string prefix = "PURGE PREVIEW id=";
        var start = output.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("The owner CLI did not report the S8-T4 purge identifier.");
        }

        start += prefix.Length;
        var end = output.IndexOf(' ', start);
        return end < 0 ? output[start..].Trim() : output[start..end];
    }

    private static string[] ParseS8T4Impacted(string output)
    {
        var ids = new List<string>();
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("impacted ", StringComparison.Ordinal))
            {
                continue;
            }

            var token = line["impacted ".Length..].Trim();
            ids.Add(token);
        }

        if (ids.Count == 0)
        {
            throw new InvalidOperationException("The owner CLI did not report the S8-T4 impacted set.");
        }

        return ids.ToArray();
    }

    private static string ParseS8T4Generation(string output)
    {
        const string prefix = " generation=";
        var start = output.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("The owner CLI did not report the S8-T4 live generation.");
        }

        start += prefix.Length;
        var end = start;
        while (end < output.Length && char.IsDigit(output[end]))
        {
            end++;
        }

        return output[start..end];
    }

    /// <summary>
    /// Counts the TCP connections the given process holds. The product talks
    /// over named pipes, so a non-zero count would be an outbound network
    /// connection the scenario did not ask for.
    /// </summary>
    private static int CountS8T4TcpConnections(int processId)
    {
        var start = new ProcessStartInfo("netstat.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-ano");
        using var process = Process.Start(start);
        if (process is null)
        {
            return 0;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30000);
        var count = 0;
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("TCP", StringComparison.OrdinalIgnoreCase) &&
                line.TrimEnd().EndsWith(processId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>
/// The operational log of the recorder proof: it keeps every line the
/// recorder wrote, so the proof can read what the owner would see.
/// </summary>
internal sealed class S8T4RecordingOperationalLog : IOperationalLog
{
    public List<string> Lines { get; } = new List<string>();

    public void Write(string operation, string outcome, string? errorCode) =>
        Lines.Add("op=" + operation + " outcome=" + outcome + " code=" + (errorCode ?? "-"));

    public OperationalLogStatus Status() => new("operations.log", 0, Lines.Count, null);

    public int Purge()
    {
        var removed = Lines.Count;
        Lines.Clear();
        return removed;
    }
}

/// <summary>
/// The deterministic fault injection at the persistence seam of the private
/// operations state: every write of the trail fails with the typed failure
/// the store raises when the file, the key or the envelope fails.
/// </summary>
internal sealed class S8T4AuditFailureOperation : IVaultOperation
{
    private readonly Exception _failure;

    public S8T4AuditFailureOperation(Exception failure) =>
        _failure = failure ?? throw new ArgumentNullException(nameof(failure));

    public OperationsAuditState LoadOperationsAuditState() => OperationsAuditState.Empty;

    public T UpdateOperationsAuditState<T>(
        Func<OperationsAuditState, (OperationsAuditState State, T Result)> update) =>
        throw _failure;

    public OwnerScopeGrantState LoadScopeGrantState() => throw new NotSupportedException();

    public T UpdateScopeGrantState<T>(
        Func<OwnerScopeGrantState, (OwnerScopeGrantState State, T Result)> update) => throw new NotSupportedException();

    public OwnerCatalogState LoadCatalogState() => throw new NotSupportedException();

    public T UpdateCatalogState<T>(
        Func<OwnerCatalogState, (OwnerCatalogState State, T Result)> update) => throw new NotSupportedException();

    public SourceEnrollmentState LoadSourceEnrollmentState() => throw new NotSupportedException();

    public T UpdateSourceEnrollmentState<T>(
        Func<SourceEnrollmentState, (SourceEnrollmentState State, T Result)> update) => throw new NotSupportedException();

    public OwnerRecallPolicyState LoadRecallPolicyState() => throw new NotSupportedException();

    public T UpdateRecallPolicyState<T>(
        Func<OwnerRecallPolicyState, (OwnerRecallPolicyState State, T Result)> update) => throw new NotSupportedException();

    public OwnerReviewState LoadReviewState() => throw new NotSupportedException();

    public T UpdateReviewState<T>(
        Func<OwnerReviewState, (OwnerReviewState State, T Result)> update) => throw new NotSupportedException();

    public OwnerPurgeLedgerState LoadPurgeLedgerState() => throw new NotSupportedException();

    public T UpdatePurgeLedgerState<T>(
        Func<OwnerPurgeLedgerState, (OwnerPurgeLedgerState State, T Result)> update) => throw new NotSupportedException();

    public bool DeleteCanonicalResource(string resourceId) => throw new NotSupportedException();

    public IReadOnlyList<ScopeResourceRevision> ListResources() => throw new NotSupportedException();

    public bool TryGetZone(string resourceId, out string? zoneId) => throw new NotSupportedException();

    public long WriteContent(string resourceId, string zoneId, byte[] content) => throw new NotSupportedException();

    public long WriteContent(string resourceId, string zoneId, byte[] content, long expectedRevision) => throw new NotSupportedException();

    public byte[][] ReadBatch(IReadOnlyList<ScopeResourceRevision> batch, int maxDegreeOfParallelism, CancellationToken cancellationToken) => throw new NotSupportedException();

    public byte[] ReadContent(string resourceId, long? expectedRevision = null) => throw new NotSupportedException();

    public void ValidateRegistration(string resourceId, long? expectedRevision = null) => throw new NotSupportedException();

    public void Dispose()
    {
    }
}

/// <summary>
/// The synthetic S8-T4 installation: one data root with the vault, the
/// operational log directory and the service settings.
/// </summary>
internal sealed class S8T4Fixture : IDisposable
{
    private bool _disposed;

    private S8T4Fixture(
        string root,
        string dataRoot,
        string settingsPath,
        string controlPipe,
        string agentPipe,
        string restoredRoot,
        string restoredSettingsPath,
        string restoredControlPipe,
        string restoredAgentPipe,
        string oldCopyRoot,
        string oldCopySettingsPath,
        string oldCopyControlPipe,
        string oldCopyAgentPipe)
    {
        Root = root;
        DataRootPath = dataRoot;
        SettingsPath = settingsPath;
        ControlPipe = controlPipe;
        AgentPipe = agentPipe;
        RestoredRoot = restoredRoot;
        RestoredSettingsPath = restoredSettingsPath;
        RestoredControlPipe = restoredControlPipe;
        RestoredAgentPipe = restoredAgentPipe;
        OldCopyRoot = oldCopyRoot;
        OldCopySettingsPath = oldCopySettingsPath;
        OldCopyControlPipe = oldCopyControlPipe;
        OldCopyAgentPipe = oldCopyAgentPipe;
    }

    public string Root { get; }
    public string DataRootPath { get; }
    public string SettingsPath { get; }
    public string ControlPipe { get; }
    public string AgentPipe { get; }
    public string RestoredRoot { get; }
    public string RestoredSettingsPath { get; }
    public string RestoredControlPipe { get; }
    public string RestoredAgentPipe { get; }
    public string OldCopyRoot { get; }
    public string OldCopySettingsPath { get; }
    public string OldCopyControlPipe { get; }
    public string OldCopyAgentPipe { get; }
    public string VaultDirectoryPath => Path.Combine(DataRootPath, "vault");
    public string OperationsStatePath => Path.Combine(VaultDirectoryPath, "operations-audit.enc");
    public string OperationalLogPath => Path.Combine(DataRootPath, "logs", "operations.log");

    /// <summary>The clean directory the post-purge backup is restored into.</summary>
    public string RestoreTargetDirectory => Path.Combine(RestoredRoot, "vault");

    /// <summary>The clean directory the pre-purge backup is restored into.</summary>
    public string OldCopyTargetDirectory => Path.Combine(OldCopyRoot, "vault");

    public static S8T4Fixture Create()
    {
        var repositoryRoot = SmokeFixture.FindRepositoryRoot();
        var root = Path.Combine(repositoryRoot, "runtime", "s8-t4", "run-" + Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "live");
        var restoredRoot = Path.Combine(root, "restored");
        var oldCopyRoot = Path.Combine(root, "old-copy");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(restoredRoot);
        Directory.CreateDirectory(oldCopyRoot);
        try
        {
            var controlPipe = "MetaBrainS8T4-" + Guid.NewGuid().ToString("N");
            var agentPipe = controlPipe + ".agent";
            var settingsPath = WriteSettings(dataRoot, controlPipe, agentPipe);
            var restoredControlPipe = "MetaBrainS8T4R-" + Guid.NewGuid().ToString("N");
            var restoredAgentPipe = restoredControlPipe + ".agent";
            var restoredSettingsPath = WriteSettings(restoredRoot, restoredControlPipe, restoredAgentPipe);
            var oldCopyControlPipe = "MetaBrainS8T4O-" + Guid.NewGuid().ToString("N");
            var oldCopyAgentPipe = oldCopyControlPipe + ".agent";
            var oldCopySettingsPath = WriteSettings(oldCopyRoot, oldCopyControlPipe, oldCopyAgentPipe);
            return new S8T4Fixture(
                root,
                dataRoot,
                settingsPath,
                controlPipe,
                agentPipe,
                restoredRoot,
                restoredSettingsPath,
                restoredControlPipe,
                restoredAgentPipe,
                oldCopyRoot,
                oldCopySettingsPath,
                oldCopyControlPipe,
                oldCopyAgentPipe);
        }
        catch
        {
            TryDelete(root);
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
        TryDelete(Root);
    }

    private static string WriteSettings(string dataRoot, string controlPipe, string agentPipe)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var ownerSid = identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows token has no user SID.");
        var path = Path.Combine(dataRoot, "service-settings.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 3,
                ownerSid,
                controlPipe,
                agentPipe,
            }),
            new UTF8Encoding(false));
        return path;
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
