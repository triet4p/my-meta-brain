using MetaBrain.Core.Domain;

namespace MetaBrain.Connections;

/// <summary>
/// Shipped format normalizers for source ingestion (S3-T2 Codex, S3-T3 OMP).
/// The coordinator itself stays format-agnostic: it dispatches on the
/// owner-declared (format, version) of an enrolled source and refuses
/// anything this factory does not register, so an unregistered format is an
/// explicit unsupported diagnostic rather than a best-effort read.
/// <para>
/// The Codex session parser (S3-T2) and the OMP session parser (S3-T3) are
/// registered here because this is the one place the product decides which
/// real-world source schemas it can interpret. Each supported schema is
/// exactly what an owner-authorized sample (plus, for Codex, the upstream
/// format definitions) shows; everything else is refused explicitly by the
/// normalizer and never silently skipped.
/// </para>
/// </summary>
internal static class SourceNormalizerFactory
{
    public static SourceNormalizerRegistry CreateShipped()
    {
        var normalizers = new SourceNormalizerRegistry();
        normalizers.Register(new CodexSessionNormalizer());
        normalizers.Register(new OmpSessionNormalizer());
        return normalizers;
    }
}
