using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MetaBrain.Connections;

/// <summary>
/// S8-T5 the nonce sequence of one private-index generation.
/// <para>
/// Every record of a generation is sealed with its own nonce: a random
/// per-generation prefix followed by a per-record counter. The nonces of one
/// generation are unique by construction, and the counter's space is checked
/// before it is used, so a (key, nonce) pair is never reused inside one
/// generation. Across generations the nonces differ by a random 64-bit
/// prefix, which is a probabilistic guarantee (a collision needs two random
/// prefixes to agree), not an absolute one.
/// </para>
/// </summary>
internal sealed class PrivateIndexNonceSequence
{
    private const int PrefixBytes = 8;
    private const int CounterBytes = 4;
    private const int NonceBytes = PrefixBytes + CounterBytes;

    private readonly byte[] _prefix;
    private ulong _counter;

    /// <summary>
    /// The sequence of a new generation: a fresh random prefix and a counter
    /// that starts at zero.
    /// </summary>
    internal PrivateIndexNonceSequence()
        : this(RandomNumberGenerator.GetBytes(PrefixBytes), 0)
    {
    }

    /// <summary>
    /// A sequence with a given prefix and counter. The writer uses the other
    /// constructor; this one lets the boundary of the counter's space be
    /// exercised without writing four billion records.
    /// </summary>
    internal PrivateIndexNonceSequence(byte[] prefix, ulong counter)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (prefix.Length != PrefixBytes)
        {
            throw new ArgumentException("The private index nonce prefix must be eight bytes.", nameof(prefix));
        }

        if (counter > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(counter));
        }

        _prefix = prefix;
        _counter = counter;
    }

    /// <summary>The number of nonces the sequence has issued.</summary>
    internal ulong Issued => _counter;

    /// <summary>
    /// The next nonce of the generation. The counter's space is checked
    /// before the nonce is built: a generation that would issue the same
    /// (key, nonce) pair twice fails closed instead of wrapping around.
    /// </summary>
    internal byte[] Next()
    {
        if (_counter > uint.MaxValue)
        {
            throw new InvalidOperationException("The private index generation exhausted its record nonce space.");
        }

        var nonce = new byte[NonceBytes];
        _prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(PrefixBytes, CounterBytes), (uint)_counter);
        _counter++;
        return nonce;
    }
}
