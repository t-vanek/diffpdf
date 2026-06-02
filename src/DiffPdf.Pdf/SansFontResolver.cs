using PdfSharp.Fonts;

namespace DiffPdf.Pdf;

/// <summary>
/// Minimal single-face font resolver for the diff-PDF header labels. Loads a
/// sans-serif TTF from <c>DIFFPDF_FONT_PATH</c> or a common system location.
/// If no font is found, <see cref="Instance"/> is null and the writer omits
/// text (falling back to colored header strips only).
/// </summary>
public sealed class SansFontResolver : IFontResolver
{
    public const string FaceName = "DiffPdfSans";

    public static readonly SansFontResolver? Instance = TryCreate();

    private readonly byte[] _fontData;

    private SansFontResolver(byte[] fontData) => _fontData = fontData;

    public byte[] GetFont(string faceName) => _fontData;

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new FontResolverInfo(FaceName);

    private static SansFontResolver? TryCreate()
    {
        foreach (var path in CandidatePaths())
        {
            try
            {
                if (File.Exists(path))
                    return new SansFontResolver(File.ReadAllBytes(path));
            }
            catch (IOException)
            {
                // try the next candidate
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var env = Environment.GetEnvironmentVariable("DIFFPDF_FONT_PATH");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;

        yield return "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
        yield return "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf";
        yield return "/usr/share/fonts/truetype/liberation2/LiberationSans-Regular.ttf";
        yield return "/Library/Fonts/Arial.ttf";
        yield return @"C:\Windows\Fonts\arial.ttf";
    }
}
