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
        if (!IsValidControlPipe(controlPipe))
        {
            throw new ArgumentException("The owner control pipe name is invalid.", nameof(controlPipe));
        }

        var requestJson = JsonSerializer.Serialize(
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerStatusOperation),
            JsonOptions);
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
                    "forbidden" or "unauthorized" => new OwnerChannelStatus(OwnerChannelAvailability.OwnerAuthorityDenied, OwnerVaultState.Unknown, code),
                    "unsupported_protocol" => new OwnerChannelStatus(OwnerChannelAvailability.ProtocolMismatch, OwnerVaultState.Unknown, code),
                    _ => Unavailable(code)
                };
            }

            if (!HasText(reply, "status", "running"))
            {
                return Unavailable("unexpected_reply");
            }

            // owner.status is answered on the owner plane only. A reply claiming
            // any other plane never becomes an owner session in this client.
            if (!HasText(reply, "principal", "owner"))
            {
                return new OwnerChannelStatus(OwnerChannelAvailability.OwnerAuthorityDenied, OwnerVaultState.Unknown, "owner_plane_mismatch");
            }

            if (!reply.TryGetProperty("vaultState", out var vault) || vault.ValueKind != JsonValueKind.String)
            {
                return new OwnerChannelStatus(OwnerChannelAvailability.Reachable, OwnerVaultState.Unknown, "vault_state_unknown");
            }

            var state = MapVaultState(vault.GetString());
            return new OwnerChannelStatus(OwnerChannelAvailability.Reachable, state, state == OwnerVaultState.Unknown ? "vault_state_unknown" : null);
        }
    }

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

    private static OwnerChannelStatus Unavailable(string errorCode) =>
        new(OwnerChannelAvailability.ServiceUnavailable, OwnerVaultState.Unknown, errorCode);
}
