using System.Globalization;
using System.Text;

namespace Gaida.Bot.Tools;

/// <summary>Edit distance, as the queue's fuzzy name matching has always defined it.</summary>
/// <remarks>Source StackOverflow: https://stackoverflow.com/questions/6944056/c-sharp-compare-string-similarity</remarks>
public static class LevenshteinDistance
{
    public static int ComputeLean(string? s, string? t) => ComputeStrict(Fold(s), Fold(t));

    /// <summary>
    ///     Lowercase, drop diacritics, and reduce every run of punctuation or whitespace to a single space.
    /// </summary>
    /// <remarks>
    ///     What a user types carries neither accents nor the separator a display name is built with, and the
    ///     raw distance charged for both: "la bomba king africa" sat three edits from the Deezer track
    ///     ("La Bomba" by "King África" — two for the constructed " - ", one for the accent) but only two from
    ///     a YouTube upload whose title happened to be spelled "La Bomba - King Africa", so the copy won.
    /// </remarks>
    private static string? Fold(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        var folded = new StringBuilder(value.Length);
        foreach (var c in value.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;

            if (char.IsLetterOrDigit(c)) folded.Append(c);
            else if (folded.Length > 0 && folded[^1] != ' ') folded.Append(' ');
        }

        return folded.ToString().TrimEnd();
    }

    public static int ComputeStrict(string? s, string? t)
    {
        if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t!.Length;
        if (string.IsNullOrEmpty(t)) return s.Length;

        var n = s.Length;
        var m = t.Length;
        var d = new int[n + 1, m + 1];

        for (var i = 0; i <= n; d[i, 0] = i++) { }
        for (var j = 1; j <= m; d[0, j] = j++) { }

        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
            {
                var cost = t[j - 1] == s[i - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }

        return d[n, m];
    }
}
