using System.Text.Json;
using MetaBrain.Application;

namespace MetaBrain.Connections;

/// <summary>
/// What an owner-application client observed when it used the owner control
/// channel. These are transport facts, never a second policy interpretation:
/// the owner application holds no vault key, opens no vault file and caches no
/// private content, and it reaches the shared core/application policy only by
/// asking the running service over the authenticated owner control pipe.
/// </summary>
public enum OwnerChannelAvailability
{
    /// <summary>No owner-channel answer: no service, unknown pipe, ACL refusal or timeout.</summary>
    ServiceUnavailable = 0,

    /// <summary>
    /// The endpoint answered but refused owner authority for this request
    /// (for example the agent channel, or a caller that is not the owner SID).
    /// </summary>
    OwnerAuthorityDenied = 1,

    /// <summary>The endpoint answered with a different protocol revision.</summary>
    ProtocolMismatch = 2,

    /// <summary>The owner control channel answered with the running owner status.</summary>
    Reachable = 3
}

/// <summary>
/// The vault state exactly as the service reported it. <see cref="Unknown"/>
/// covers "no answer" and any value this client does not recognize; it is never
/// treated as unlocked.
/// </summary>
public enum OwnerVaultState
{
    Unknown = 0,
    Uninitialized = 1,
    Locked = 2,
    Locking = 3,
    Unlocked = 4
}

/// <summary>
/// The owner-channel status an owner application may present. <see cref="VaultState"/>
/// is only meaningful together with <see cref="OwnerChannelAvailability.Reachable"/>.
/// </summary>
public sealed record OwnerChannelStatus(
    OwnerChannelAvailability Availability,
    OwnerVaultState VaultState,
    string? ErrorCode);

/// <summary>
/// How an owner vault command ended, as the owner application observed it.
/// <see cref="Refused"/> is a concrete answer of the running service: the
/// endpoint understood the command and declined it, for example a wrong or
/// missing key (<c>unlock_denied</c>), a passphrase outside the accepted
/// policy (<c>credential_required</c>) or an already initialized vault
/// (<c>vault_already_initialized</c>). The vault state did not change in that
/// case, and the owner application must present it as a refusal, never as an
/// unavailable service.
/// </summary>
public enum OwnerVaultCommandOutcome
{
    /// <summary>The service performed the owner vault command.</summary>
    Completed = 0,

    /// <summary>The service answered and refused the command; the vault state did not change.</summary>
    Refused = 1,

    /// <summary>The endpoint answered but refused owner authority for this request.</summary>
    OwnerAuthorityDenied = 2,

    /// <summary>The endpoint answered with a different protocol revision.</summary>
    ProtocolMismatch = 3,

    /// <summary>No owner-channel answer: no service, unknown pipe, access denial, timeout or malformed reply.</summary>
    Unavailable = 4
}

/// <summary>
/// Result of one owner vault command. <see cref="RecoveryCode"/> carries the
/// one-time recovery material the service returns for a fresh provisioning;
/// the owner application shows it once and persists it nowhere, and it stays
/// null for every other command.
/// </summary>
public sealed record OwnerVaultCommandResult(
    OwnerVaultCommandOutcome Outcome,
    OwnerVaultState VaultState,
    string? ErrorCode,
    string? RecoveryCode);

/// <summary>
/// The owner-application client of the authenticated owner control channel.
/// It owns no key material and no authority: every call is an ordinary service
/// request whose owner authority the service derives from the caller's Windows
/// token on the owner pipe. Identity-shaped body fields, Origin/localhost or
/// agent claims are never consulted here or by the service.
/// </summary>
public static class OwnerChannelClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Protocol revision this client speaks; the service refuses other revisions.</summary>
    public static int ProtocolVersion => ServiceRequestHandler.CurrentProtocolVersion;

    /// <summary>True when the value can be used as a Windows named-pipe name.</summary>
    public static bool IsValidControlPipe(string? controlPipe) => ServiceSettingsLoader.IsSafeIdentifier(controlPipe);

    /// <summary>
    /// Asks the running service for its owner status over the owner control pipe
    /// and classifies the answer. Transport failures, refusals and unrecognized
    /// replies are reported honestly instead of being folded into a success.
    /// </summary>
    public static async Task<OwnerChannelStatus> ReadStatusAsync(string controlPipe, CancellationToken cancellationToken)
    {
        var facts = await ReadFactsAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerStatusOperation),
            "running",
            cancellationToken).ConfigureAwait(false);

        return facts.Availability == OwnerChannelAvailability.Reachable
            ? new OwnerChannelStatus(OwnerChannelAvailability.Reachable, facts.VaultState,
                facts.VaultState == OwnerVaultState.Unknown ? "vault_state_unknown" : null)
            : new OwnerChannelStatus(facts.Availability, OwnerVaultState.Unknown, facts.ErrorCode);
    }

    /// <summary>
    /// Creates the owner vault with the owner-chosen passphrase. The service
    /// mints the recovery material once and returns it in the result; nothing
    /// is persisted by this client.
    /// </summary>
    public static Task<OwnerVaultCommandResult> ProvisionAsync(string controlPipe, string passphrase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        return ExecuteVaultCommandAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerVaultProvisionOperation,
                Passphrase: passphrase),
            "provisioned",
            cancellationToken);
    }

    /// <summary>Opens the existing vault with the owner passphrase.</summary>
    public static Task<OwnerVaultCommandResult> UnlockAsync(string controlPipe, string passphrase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        return ExecuteVaultCommandAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerVaultUnlockOperation,
                Passphrase: passphrase),
            "unlocked",
            cancellationToken);
    }

    /// <summary>
    /// Opens the existing vault with the recovery material the owner kept
    /// outside the vault. This opens the same vault; it restores nothing and
    /// resets nothing.
    /// </summary>
    public static Task<OwnerVaultCommandResult> UnlockWithRecoveryCodeAsync(string controlPipe, string recoveryCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recoveryCode);
        return ExecuteVaultCommandAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerVaultRecoverOperation,
                RecoveryCode: recoveryCode),
            "unlocked",
            cancellationToken);
    }

    /// <summary>
    /// Asks the service to lock the vault. The service owns the lock lifecycle:
    /// it drains in-flight private operations, clears the key, invalidates
    /// pending tokens and live sessions and stops private work.
    /// </summary>
    public static Task<OwnerVaultCommandResult> LockAsync(string controlPipe, CancellationToken cancellationToken) =>
        ExecuteVaultCommandAsync(
            controlPipe,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerVaultLockOperation),
            "locked",
            cancellationToken);

    private static async Task<OwnerVaultCommandResult> ExecuteVaultCommandAsync(
        string controlPipe,
        ServiceRequest request,
        string expectedStatus,
        CancellationToken cancellationToken)
    {
        var facts = await ReadFactsAsync(controlPipe, request, expectedStatus, cancellationToken).ConfigureAwait(false);
        return new OwnerVaultCommandResult(
            MapOutcome(facts),
            facts.Availability == OwnerChannelAvailability.Reachable ? facts.VaultState : OwnerVaultState.Unknown,
            facts.ErrorCode,
            facts.RecoveryCode);
    }

    private sealed record ReplyFacts(
        OwnerChannelAvailability Availability,
        OwnerVaultState VaultState,
        string? ErrorCode,
        string? RecoveryCode);

    private static async Task<ReplyFacts> ReadFactsAsync(
        string controlPipe,
        ServiceRequest request,
        string expectedStatus,
        CancellationToken cancellationToken)
    {
        if (!IsValidControlPipe(controlPipe))
        {
            throw new ArgumentException("The owner control pipe name is invalid.", nameof(controlPipe));
        }

        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var result = await PipeClient.InvokeOwnerAsync(controlPipe, requestJson, cancellationToken).ConfigureAwait(false);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(result.Json);
        }
        catch (JsonException)
        {
            return Unavailable("malformed_reply");
        }

        using (document)
        {
            var reply = document.RootElement;
            if (reply.ValueKind != JsonValueKind.Object)
            {
                return Unavailable("malformed_reply");
            }

            if (reply.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                var code = error.GetString()!;
                return code switch
                {
                    "forbidden" or "unauthorized" => new ReplyFacts(OwnerChannelAvailability.OwnerAuthorityDenied, OwnerVaultState.Unknown, code, null),
                    "unsupported_protocol" => new ReplyFacts(OwnerChannelAvailability.ProtocolMismatch, OwnerVaultState.Unknown, code, null),
                    _ => Unavailable(code)
                };
            }

            if (!HasText(reply, "status", expectedStatus))
            {
                return Unavailable("unexpected_reply");
            }

            // The owner vault operations are answered on the owner plane only. A
            // reply claiming any other plane never becomes an owner session or an
            // owner vault act in this client.
            if (!HasText(reply, "principal", "owner"))
            {
                return new ReplyFacts(OwnerChannelAvailability.OwnerAuthorityDenied, OwnerVaultState.Unknown, "owner_plane_mismatch", null);
            }

            var state = reply.TryGetProperty("vaultState", out var vault) && vault.ValueKind == JsonValueKind.String
                ? MapVaultState(vault.GetString())
                : OwnerVaultState.Unknown;
            var recoveryCode = reply.TryGetProperty("recoveryCode", out var recovery) && recovery.ValueKind == JsonValueKind.String
                ? recovery.GetString()
                : null;
            return new ReplyFacts(OwnerChannelAvailability.Reachable, state, null, recoveryCode);
        }
    }

    private static OwnerVaultCommandOutcome MapOutcome(ReplyFacts facts) => facts.Availability switch
    {
        OwnerChannelAvailability.Reachable => OwnerVaultCommandOutcome.Completed,
        OwnerChannelAvailability.OwnerAuthorityDenied => OwnerVaultCommandOutcome.OwnerAuthorityDenied,
        OwnerChannelAvailability.ProtocolMismatch => OwnerVaultCommandOutcome.ProtocolMismatch,
        _ => IsLocalTransportCode(facts.ErrorCode) ? OwnerVaultCommandOutcome.Unavailable : OwnerVaultCommandOutcome.Refused
    };

    /// <summary>
    /// Codes this client fabricates when there was no usable owner-channel
    /// answer. Every other error code came back from a running service that
    /// understood the request and declined it.
    /// </summary>
    private static bool IsLocalTransportCode(string? errorCode) => errorCode is
        "malformed_reply" or "unexpected_reply" or "transport_unavailable" or "transport_denied" or "transport_timeout";

    private static OwnerVaultState MapVaultState(string? reported) => reported switch
    {
        "uninitialized" => OwnerVaultState.Uninitialized,
        "locked" => OwnerVaultState.Locked,
        "locking" => OwnerVaultState.Locking,
        "unlocked" => OwnerVaultState.Unlocked,
        _ => OwnerVaultState.Unknown
    };

    private static bool HasText(JsonElement reply, string property, string expected) =>
        reply.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static ReplyFacts Unavailable(string errorCode) =>
        new(OwnerChannelAvailability.ServiceUnavailable, OwnerVaultState.Unknown, errorCode, null);
}
