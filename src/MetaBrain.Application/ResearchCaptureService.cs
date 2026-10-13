using System.Security.Cryptography;
using System.Text;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// S3-T4 research capture authority: one owner operation persists the verbatim
/// unstructured research text as an encrypted registered source before any
/// structuring/model job runs, so a provider failure can never lose the note
/// (R19). The source is written through the S2-T3 registry path (create-only,
/// revision-gated, executor from the trusted context), which keeps capture
/// independent from memory items, grants, and any provider: nothing is
/// classified, summarized, or extracted here, and a URL is only ever stored
/// as a locator that is never fetched (R16/R18/R19). Unknown stays unknown:
/// omitted title, URL, author role, and source date are never inferred from
/// the text, the URL, or each other, and the text is never relabeled as an
/// owner belief — it remains a source (R12).
/// <para>
/// The captured text must be valid UTF-8 and fits inside one registered
/// source record (bounded by <see cref="RegisteredSourceCodec.MaximumTextChars"/>
/// and the stored 1 MiB cap); reads return the exact same characters, so the
/// note survives restart/unlock byte-for-byte for its text content.
/// </para>
/// </summary>
public sealed class ResearchCaptureService
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Persists one captured research note. The owner supplies the stable ID
    /// and zone; the verbatim text bytes are decoded strictly and stored in
    /// the encrypted record. Availability is <see cref="SourceAvailability.Available"/>
    /// as a fact of this write (the vault now holds the text), the creation
    /// method is <see cref="CreationMethod.DirectWrite"/> (the owner typed or
    /// pasted the text into this capture, no derivation), version stays
    /// unknown, and a missing write means nothing was published.
    /// </summary>
    public RegisteredSource Capture(
        AuthenticatedContext context,
        string resourceId,
        string zoneId,
        byte[] textUtf8,
        string? title,
        string? locator,
        SpeakerRole? author,
        DateTimeOffset? capturedAtUtc,
        IManagedResourceStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(textUtf8);
        if (string.IsNullOrEmpty(resourceId) || string.IsNullOrEmpty(zoneId))
        {
            throw new ArgumentException("The captured source ID and zone are required.");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(textUtf8);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ArgumentException("The captured research text must be valid UTF-8.", ex);
        }

        if (capturedAtUtc is { } captured && captured.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The captured source date must use UTC.");
        }

        var draft = new RegisteredSourceDraft(
            title,
            locator,
            Version: null,
            SourceAvailability.Available,
            CreationMethod.DirectWrite,
            capturedAtUtc,
            DateTimeOffset.UtcNow,
            author,
            text);
        var draftJson = RegisteredSourceCodec.EncodeDraft(draft);
        try
        {
            return new SourceRegistryService().Create(context, resourceId, zoneId, draftJson, store);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draftJson);
        }
    }

    /// <summary>
    /// Reads one captured note with the live (or exact requested) revision.
    /// The S2-T3 read path resolves the descriptor revision without extra
    /// decrypts; <see cref="RegisteredSource.Text"/> is null when the ID names
    /// a metadata-only registration rather than a captured note, and the
    /// caller decides how to report that.
    /// </summary>
    public RegisteredSource Read(
        AuthenticatedContext context,
        string resourceId,
        long? expectedRevision,
        IManagedResourceReader store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        return new SourceRegistryService().Read(context, resourceId, expectedRevision, store);
    }
}
