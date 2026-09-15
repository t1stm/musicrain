using System.Globalization;
using System.Text.RegularExpressions;

namespace Stih;

/// <summary>
///     One parser for every source: files in the library, files in <c>/lyrics</c>, LRCLIB's
///     <c>syncedLyrics</c>, and the LRC the Deezer pod builds from Deezer's sync JSON. Pure, no I/O.
/// </summary>
public static partial class LrcParser
{
    /// <summary><c>[mm:ss.xx]</c>, <c>[mm:ss.xxx]</c>, <c>[mm:ss]</c> and <c>[h:mm:ss.xx]</c>, in one shape.</summary>
    [GeneratedRegex(@"\[(?:(\d+):)?(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimestampRegex();

    /// <summary><c>[offset:±ms]</c> — the one metadata tag that changes the output rather than describing it.</summary>
    [GeneratedRegex(@"^\s*\[offset:\s*([+-]?\d+)\s*\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetRegex();

    /// <summary>
    ///     Turns LRC or plain text into lines, and says which of the two it was.
    /// </summary>
    /// <remarks>
    ///     A timestamp with no words is kept as an empty line: those are the instrumental gaps, and a
    ///     client needs them to know the previous line has stopped being sung. With no timestamp anywhere
    ///     the text is plain and every <c>At</c> is <c>null</c>; with some, the untimed lines are dropped —
    ///     they are the <c>[ar:]</c>/<c>[ti:]</c> header, not words anybody sings.
    /// </remarks>
    public static (LyricsKind Kind, IReadOnlyList<LyricLineDto> Lines) Parse(string? content)
    {
        var text = content ?? string.Empty;
        var offset = TimeSpan.Zero;
        var timed = new List<(TimeSpan At, int Order, string Text)>();
        var plain = new List<string>();

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (OffsetRegex().Match(raw) is { Success: true } tag)
            {
                // Negative shifts the words earlier, which is what an offset is for.
                offset = TimeSpan.FromMilliseconds(int.Parse(tag.Groups[1].Value, CultureInfo.InvariantCulture));
                continue;
            }

            var stamps = TimestampRegex().Matches(raw);
            if (stamps.Count == 0)
            {
                plain.Add(raw.Trim());
                continue;
            }

            // Several timestamps on one line — [00:12.34][01:45.00] same refrain — are one line each.
            var words = raw[(stamps[^1].Index + stamps[^1].Length)..].Trim();
            foreach (Match stamp in stamps)
                timed.Add((At(stamp) + offset, timed.Count, words));
        }

        if (timed.Count == 0)
            return (LyricsKind.Unsynchronized, [.. Trimmed(plain).Select(line => new LyricLineDto(null, line))]);

        return (LyricsKind.Synchronized, [.. timed
            .OrderBy(line => line.At)
            .ThenBy(line => line.Order)
            // Clamped at zero: an offset larger than the first timestamp would otherwise put a line
            // before the song starts, which no player can seek to.
            .Select(line => new LyricLineDto(Math.Max(0, line.At.TotalSeconds), line.Text))]);
    }

    /// <summary>The plain block, timestamps stripped — what someone copying the words out should get.</summary>
    public static string TextOf(IReadOnlyList<LyricLineDto> lines)
    {
        return string.Join('\n', lines.Select(line => line.Text)).Trim();
    }

    private static TimeSpan At(Match stamp)
    {
        var hours = stamp.Groups[1].Success ? int.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = int.Parse(stamp.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(stamp.Groups[3].Value, CultureInfo.InvariantCulture);

        // "34" in [00:12.34] is hundredths, "340" is milliseconds: the unit is the digit count.
        var fraction = stamp.Groups[4].Success ? stamp.Groups[4].Value : string.Empty;
        var milliseconds = fraction.Length switch
        {
            0 => 0,
            1 => int.Parse(fraction, CultureInfo.InvariantCulture) * 100,
            2 => int.Parse(fraction, CultureInfo.InvariantCulture) * 10,
            _ => int.Parse(fraction[..3], CultureInfo.InvariantCulture)
        };

        return new TimeSpan(0, hours, minutes, seconds, milliseconds);
    }

    /// <summary>Drops the blank run at each end of a plain file, and the metadata header if it has one.</summary>
    private static IEnumerable<string> Trimmed(List<string> lines)
    {
        var start = 0;
        var end = lines.Count;
        while (start < end && IsNoise(lines[start])) start++;
        while (end > start && lines[end - 1].Length == 0) end--;

        return lines.GetRange(start, end - start);
    }

    private static bool IsNoise(string line)
    {
        return line.Length == 0 || (line.StartsWith('[') && line.EndsWith(']'));
    }
}
