using System.Security.Cryptography;

namespace MetaBrain.Core.Security;

/// <summary>
/// Server-side agent session minted by exactly one successful token redemption.
/// Memory-only: never persisted, so crash/restart/lock cannot resurrect it.
/// Authority is the frozen owner-approved snapshot; the bearer verifier never
/// leaves the service process and the raw session bearer is disclosed only once,
/// after durable consumption of the redeemed token has been persisted.
/// </summary>
public sealed record AgentSession(
    string SessionId,
    string GrantId,
    string SessionVerifier,
    ScopeResourceRevision[] Resources,
    string[] Operations,
    ScopeResourceRevision[] DestinationResources,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    ScopeGrantEgress? Egress,
    long PolicyGeneration,
    long UnlockEpoch);

/// <summary>
/// Authoritative session snapshot safe to disclose to the session holder.
/// Contains no verifier material. Until scoped resource reads exist (T6),
/// this metadata is the only session authorization surface.
/// </summary>
public sealed record AgentSessionSnapshot(
    string SessionId,
    string GrantId,
    ScopeResourceRevision[] Resources,
    string[] Operations,
    ScopeResourceRevision[] DestinationResources,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    ScopeGrantEgress? Egress,
    long PolicyGeneration,
    long UnlockEpoch);

public enum AgentRedeemStatus
{
    Issued,
    Unknown,
    Used,
    Revoked,
    Expired,
    Superseded
}

public sealed record AgentRedeemResult(
    AgentRedeemStatus Status,
    string? SessionToken,
    AgentSessionSnapshot? Session);

public enum AgentSessionLookupStatus
{
    Valid,
    Unknown,
    Revoked,
    Superseded,
    Expired
}

public sealed record AgentSessionLookupResult(
    AgentSessionLookupStatus Status,
    AgentSessionSnapshot? Session);

internal static class AgentBearerEncoding
{
    public const string GrantTokenPrefix = "mb1_";
    public const string SessionTokenPrefix = "mbs1_";
    public const int GrantTokenLength = 47;
    public const int SessionTokenLength = 48;

    public static byte[]? DecodeGrantToken(string? token) => Decode(token, GrantTokenPrefix, GrantTokenLength);

    public static byte[]? DecodeSessionToken(string? token) => Decode(token, SessionTokenPrefix, SessionTokenLength);

    private static byte[]? Decode(string? token, string prefix, int length)
    {
        if (token is null || token.Length != length || !token.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var decoded = DecodeBase64Url(token.Substring(prefix.Length));
            try
            {
                if (decoded.Length != 32)
                {
                    return null;
                }

                var result = decoded;
                decoded = null;
                return result;
            }
            finally
            {
                if (decoded is not null)
                {
                    CryptographicOperations.ZeroMemory(decoded);
                }
            }
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string EncodeGrantToken(ReadOnlySpan<byte> tokenBytes) =>
        GrantTokenPrefix + EncodeBase64Url(tokenBytes);

    public static string EncodeSessionToken(ReadOnlySpan<byte> tokenBytes) =>
        SessionTokenPrefix + EncodeBase64Url(tokenBytes);

    public static string ComputeVerifier(ReadOnlySpan<byte> tokenBytes)
    {
        var digest = SHA256.HashData(tokenBytes);
        try
        {
            return EncodeBase64Url(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    public static bool VerifierEquals(string left, string right)
    {
        byte[]? leftBytes = null;
        byte[]? rightBytes = null;
        try
        {
            leftBytes = DecodeBase64Url(left);
            rightBytes = DecodeBase64Url(right);
            return leftBytes.Length == 32 && rightBytes.Length == 32 &&
                CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            if (leftBytes is not null)
            {
                CryptographicOperations.ZeroMemory(leftBytes);
            }

            if (rightBytes is not null)
            {
                CryptographicOperations.ZeroMemory(rightBytes);
            }
        }
    }

    public static string EncodeBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }
}
