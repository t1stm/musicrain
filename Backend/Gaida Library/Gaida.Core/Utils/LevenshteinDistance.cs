namespace Gaida.Core.Utils;

public static class LevenshteinDistance
{
    public static string? RemoveFormatting(string? str)
    {
        return
            str is null ? null : string.Concat(str.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }

    /// <summary>
    ///     How alike two strings are, from 0 to 1. Length-relative, unlike an absolute edit budget: a long
    ///     transliterated title accumulates several ambiguous letters (ю→yu|iu, я→ya|ia, щ→sht|sh) and a fixed
    ///     distance rejects it.
    /// </summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        return 1d - (double)ComputeStrict(a, b) / Math.Max(a.Length, b.Length);
    }

    public static int ComputeStrict(ReadOnlySpan<char> s, ReadOnlySpan<char> t)
    {
        var n = s.Length;
        var m = t.Length;

        if (n == 0) return m;
        if (m == 0) return n;

        var m1 = m + 1;
        const int maxStackSize = 1 << 10;

        var prev = m1 > maxStackSize ? new int[m1] : stackalloc int[m1];
        var curr = m1 > maxStackSize ? new int[m1] : stackalloc int[m1];

        for (var j = 0; j <= m; j++)
            prev[j] = j;

        for (var i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= m; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            var temp = prev;
            prev = curr;
            curr = temp;
        }

        return prev[m];
    }
}