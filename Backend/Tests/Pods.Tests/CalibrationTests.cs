using System.Globalization;
using System.Text;
using Dapper;
using Gaida.Platforms.MusicDatabase.Manager;
using Gaida.Sqlite;

namespace Pods.Tests;

/// <summary>
///     Not a test — the calibration pass. The thresholds in <see cref="MusicManager" /> are made up until this
///     has been read: a library is not a spec, it is whatever tagging habits fifteen years of files left behind.
///     <para>
///         Scores every title in the YouTube cache against the real library and dumps score / verdict / both
///         titles as CSV. False positives are the failure mode that makes this feature annoying rather than
///         useful, so read the CSV for the score at which the wrong answers start: <c>StrongMatch</c> goes just
///         above it and <c>WeakMatch</c> covers whatever honest band is left below — including none, in which
///         case setting them equal switches the weak band off.
///     </para>
///     <para>
///         Run it with the library and cache pointed at:
///         <c>
///             STORAGE=~/Music YOUTUBE_CACHE_DB=./cache/YouTube.db VARIANT_CALIBRATION_CSV=/tmp/variants.csv
///             dotnet test --filter CalibrationPass
///         </c>
///         . Without those it is a no-op, so ordinary runs stay green.
///     </para>
/// </summary>
public class CalibrationTests
{
    [Fact]
    public async Task CalibrationPass()
    {
        // Both named outright, never defaulted: LyricsTests sets STORAGE for the whole process, and any test that
        // initializes the YouTube platform creates ./cache/YouTube.db, so between them a default would run this
        // pass mid-suite with its thresholds at zero.
        var storage = Environment.GetEnvironmentVariable("STORAGE");
        var cachePath = Environment.GetEnvironmentVariable("YOUTUBE_CACHE_DB");
        if (storage is null || cachePath is null || !File.Exists(cachePath)) return;

        // Every candidate has to reach the CSV, including the ones the verdict would drop, or the score at
        // which the wrong answers start is exactly the thing the pass cannot see.
        var strong = MusicManager.StrongMatch;
        var weak = MusicManager.WeakMatch;
        MusicManager.StrongMatch = 0;
        MusicManager.WeakMatch = 0;

        try
        {
            var manager = new CalibrationMusicManager();
            await manager.LoadLibrary();

            var output = Environment.GetEnvironmentVariable("VARIANT_CALIBRATION_CSV") ?? "variants.csv";
            var csv = new StringBuilder("score,verdict,ytTags,libTags,deltaSeconds,youTubeTitle,libraryTitle\n");
            var scored = 0;

            await using var cache = Database.Open(Database.At(cachePath));

            // The cache runs to a few hundred thousand entries and each one is a full library scan, so the
            // pass reads a fixed-seed sample by default. VARIANT_CALIBRATION_LIMIT=0 reads all of it.
            var limit = int.TryParse(Environment.GetEnvironmentVariable("VARIANT_CALIBRATION_LIMIT"), out var parsed)
                ? parsed
                : 2000;
            var entries = (await cache.QueryAsync<(string? Name, string? Artist, string? Duration)>(
                "SELECT Name, Artist, Duration FROM results ORDER BY rowid")).ToArray();
            if (limit > 0 && entries.Length > limit)
            {
                new Random(1).Shuffle(entries);
                entries = entries[..limit];
            }

            foreach (var entry in entries)
            {
                var name = entry.Name ?? string.Empty;
                if (name.Length == 0) continue;

                var length = TimeSpan.TryParse(entry.Duration, CultureInfo.InvariantCulture, out var duration)
                    ? duration
                    : TimeSpan.Zero;
                var match = manager.FindLocalVariant(name, entry.Artist ?? string.Empty, length);
                if (match is null) continue;

                scored++;
                csv.Append(CultureInfo.InvariantCulture,
                        $"{match.Score:F3},{match.Kind},{string.Join(' ', match.YouTubeTags)},")
                    .Append(CultureInfo.InvariantCulture,
                        $"{string.Join(' ', match.LibraryTags)},{match.DurationDelta.TotalSeconds:F0},")
                    .Append(CultureInfo.InvariantCulture,
                        $"{Quote(name)},{Quote($"{match.Song.DisplayArtist} - {match.Song.DisplayTitle}")}\n");
            }

            await File.WriteAllTextAsync(output, csv.ToString());
            Assert.True(scored > 0, $"The YouTube cache at {cachePath} produced no candidates to calibrate on.");
        }
        finally
        {
            MusicManager.StrongMatch = strong;
            MusicManager.WeakMatch = weak;
        }
    }

    private static string Quote(string? text)
    {
        return $"\"{(text ?? string.Empty).Replace("\"", "\"\"")}\"";
    }

    /// <summary>Loads the real library without the cover extraction pass, which writes files this does not want.</summary>
    private sealed class CalibrationMusicManager() : MusicManager(Serilog.Core.Logger.None)
    {
        public Task LoadLibrary()
        {
            return Load();
        }
    }
}