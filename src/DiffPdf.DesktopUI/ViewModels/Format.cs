using System.Globalization;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Shared display formatting: similarity percent (never rounds a sub-100 % value up to a misleading
/// "100 %") and Czech plural agreement.</summary>
public static class Format
{
    /// <summary>A similarity fraction as a percent. Exact 100 % stays "100 %"; anything below keeps one decimal
    /// so a differing pair never misleadingly reads "100 %" (e.g. 0.996 → "99,6 %").</summary>
    public static string Percent(double fraction) =>
        fraction >= 1.0 ? "100 %" : fraction.ToString("0.0 %", CultureInfo.CurrentCulture);

    /// <summary>Czech plural agreement: 1 → <paramref name="one"/>, 2–4 → <paramref name="few"/>, otherwise
    /// <paramref name="many"/>. Returns the count with the matching word, e.g.
    /// <c>Plural(1, "odlišný", "odlišné", "odlišných")</c> → "1 odlišný".</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        string word = n == 1 ? one : n is >= 2 and <= 4 ? few : many;
        return $"{n} {word}";
    }
}
