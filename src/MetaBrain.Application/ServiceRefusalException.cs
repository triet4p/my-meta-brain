namespace MetaBrain.Application;

/// <summary>
/// The typed refusal of one owner operation (S8-T4 correction).
/// </summary>
/// <remarks>
/// <para>
/// A refusal has two parts: the real code the product knows
/// (<c>source_unavailable</c>, <c>pricing_unknown</c>, <c>budget_exceeded</c>,
/// …) and the owner-facing sentence that explains it. The sentence is free
/// text and can carry a resource ID or a path, so it must never be the value
/// the audit trail and the diagnostics record. This exception keeps the two
/// apart: the code travels in <see cref="Code"/>, the sentence travels in the
/// message.
/// </para>
/// <para>
/// The exception deliberately does not derive from
/// <see cref="ArgumentException"/>: every caller that catches the ordinary
/// argument failures has to be migrated explicitly, so no refusal can keep
/// answering with a sentence in the code position.
/// </para>
/// </remarks>
public sealed class ServiceRefusalException : Exception
{
    public ServiceRefusalException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("A refusal needs a real error code.", nameof(code));
        }

        Code = code;
    }

    /// <summary>The real error code of the refusal.</summary>
    public string Code { get; }
}
