using System.Diagnostics;
using System.Text.Json;
using Gaida.Core.Utils;
using Gaida.Platforms.MusicDatabase;
using Gaida.Platforms.MusicDatabase.Manager;

namespace Pods.Tests;

public class MusicInfoFormatTests
{
    [Fact]
    public void ReadsTheFourFieldFormatWithTheOriginalAtIndexZero()
    {
        // RomanizedTitle comes first in every Info.json written by the old scanner. Index 0 has to be the
        // original regardless, which is the one way this shim can go quietly wrong.
        const string legacy = """
                              [{
                                "ID": "брdo-vch-YO",
                                "RomanizedTitle": "Do vchera",
                                "RomanizedAuthor": "Bratya Argirovi",
                                "OriginalAuthor": "Братя Аргирови",
                                "OriginalTitle": "До вчера",
                                "RelativeLocation": "Bulgarian/x.wv",
                                "Length": 221986
                              }]
                              """;

        var songs = JsonSerializer.Deserialize<List<MusicInfo>>(legacy, MusicInfo.SerializerOptions)!;
        var song = songs[0];

        Assert.True(song.WasLegacy);
        Assert.Equal("До вчера", song.Titles[0]);
        Assert.Equal("Братя Аргирови", song.Artists[0]);
        Assert.Contains("Do vchera", song.Titles);
        Assert.Contains("Bratya Argirovi", song.Artists);
        Assert.True(song.ContainsRomanized);
        Assert.Equal(TimeSpan.FromMilliseconds(221986), song.Duration);
    }

    [Fact]
    public void WritesArraysAndNeverTheOldFieldNames()
    {
        var song = new MusicInfo { Id = "id", Titles = ["До вчера", "Do vchera"], Artists = ["Братя Аргирови"] };

        var json = JsonSerializer.Serialize(song, MusicInfo.SerializerOptions);

        Assert.Contains("\"Titles\"", json);
        Assert.DoesNotContain("Romanized", json);
        Assert.DoesNotContain("OriginalTitle", json);
    }

    [Fact]
    public void DropsTheDuplicateRowWhenNothingWasTransliterated()
    {
        // 54% of the library stored the same string twice. A Latin title romanizes to itself.
        var song = new MusicInfo { Titles = MusicInfo.Variants("Wings of Fire", "Wings of Fire") };

        Assert.Equal(["Wings of Fire"], song.Titles);
        Assert.False(song.ContainsRomanized);
    }

    [Fact]
    public void ReadsAnEntryWrittenBeforeScanWasStampedAsPassZero()
    {
        // What every Info.json on disk looks like today: no Scan, and no Album, because the tag reader
        // never asked ffprobe for one. Pass 0 is what puts it in front of the backfill.
        const string unstamped = """
                                 [{"ID":"quyoure-ab","Titles":["You're My Best Friend"],"Artists":["Queen"],
                                   "RelativeLocation":"Queen/x.mp3","Length":175000}]
                                 """;

        var song = JsonSerializer.Deserialize<List<MusicInfo>>(unstamped, MusicInfo.SerializerOptions)![0];

        Assert.Equal(0, song.Scan);
        Assert.Null(song.Album);
        Assert.True(song.Scan < MusicManager.ScanVersion);
    }

    [Fact]
    public void WritesTheScanStampAndTheAlbumBackToTheFile()
    {
        var song = new MusicInfo
        {
            Id = "id", Titles = ["Come Undone"], Artists = ["Duran Duran"],
            Album = "Duran Duran", Scan = MusicManager.ScanVersion
        };

        var json = JsonSerializer.Serialize(song, MusicInfo.SerializerOptions);

        Assert.Contains($"\"Scan\": {MusicManager.ScanVersion}", json);
        Assert.Contains("\"Album\": \"Duran Duran\"", json);
    }

    [Fact]
    public void KeepsTheTagSpellingAheadOfThePathSpelling()
    {
        var song = new MusicInfo { Titles = ["You're My Best Friend"] };

        song.AddNames("You_re My Best Friend", "Queen", "Queen");

        Assert.Equal("You're My Best Friend", song.Title);
        Assert.Contains("You_re My Best Friend", song.Titles);
        Assert.Equal(["Queen"], song.Artists);
    }

    [Theory]
    // A tag crediting someone else first loses to the filename, and stays on as a variant.
    [InlineData("Ash Riser", "Kendrick Lamar", "Kendrick Lamar")]
    [InlineData("Galin", "Преслава", "Преслава")]
    [InlineData("Andrea", "Андреа", "Андреа")]
    [InlineData("Dragan Kojic Keba", "Dragan Kojić", "Dragan Kojić")]
    // The same name, better spelled: the tag leads.
    [InlineData("A$AP Rocky feat. ScHoolboy Q", "A_AP Rocky feat. ScHoolboy Q", "A$AP Rocky, ScHoolboy Q")]
    [InlineData("Boney M.", "Boney M", "Boney M.")]
    [InlineData("Трамвай №5", "Трамвай 5", "Трамвай №5")]
    [InlineData("Ана-Мария", "Ана Мария", "Ана-Мария")]
    [InlineData("Los Del Río", "Los Del Rio", "Los Del Río")]
    [InlineData("Djoko, Kolter", "Djoko", "Djoko, Kolter")]
    // Never towards the poorer spelling.
    [InlineData("Dragana Mirkovic", "Dragana Mirković", "Dragana Mirković")]
    public void LetsTheFilenameLeadWhenTheTagNamesSomeoneElse(string tag, string path, string lead)
    {
        var song = new MusicInfo { Artists = MusicInfo.Variants(tag) };

        song.AddNames("Title", path, path);

        Assert.Equal(lead, song.Artist);
        Assert.Contains(tag, song.Artists);
        Assert.Contains(path, song.Artists);
    }

    [Theory]
    [InlineData("Слави Трифонов & Ку-ку Бенд", "Слави Трифонов, Ку-ку Бенд")]
    [InlineData("Meek Mill feat. Drake", "Meek Mill, Drake")]
    [InlineData("Деси и Тони Стораро", "Деси, Тони Стораро")]
    [InlineData("Крисия Тодорова, Ку-ку Бенд & Слави Трифонов", "Крисия Тодорова, Ку-ку Бенд, Слави Трифонов")]
    public void LeadsASharedCreditWithCommasAndKeepsTheJoinedForm(string credit, string lead)
    {
        var song = new MusicInfo();

        song.AddNames("Title", credit, "Folder");

        Assert.Equal(lead, song.Artist);
        Assert.Contains(credit, song.Artists);
    }

    [Fact]
    public void KeepsACyrillicFilenameTitleAheadOfARomanizedTag()
    {
        var song = new MusicInfo { Titles = MusicInfo.Variants("Kitka rychenica") };

        song.AddNames("Китка ръченица", "Оркестър Кристали", "Оркестър Кристали");

        Assert.Equal("Китка ръченица", song.Title);
        Assert.Contains("Kitka rychenica", song.Titles);
    }

    [Fact]
    public void RederivingKeepsTheIdAndWhatAPersonTyped()
    {
        var scanned = new MusicInfo { Id = "tetaka-ni-7Q", Titles = ["Така ни се пада"], Artists = ["Galin", "Преслава"] };
        var typed = new MusicInfo { Titles = ["Επιμένω"], Artists = ["Χρήστος Κυριαζής", "Christos Kiriazis"] };

        var fresh = new MusicInfo { Titles = ["Така ни се пада"], Artists = MusicInfo.Variants("Преслава", "Galin") };
        scanned.Rederive(fresh);
        typed.Rederive(new MusicInfo { Titles = ["Epimeno"], Artists = ["Christos Kiriazis"] });

        Assert.Equal("tetaka-ni-7Q", scanned.Id);
        Assert.Equal("Преслава", scanned.Artist);
        Assert.Contains("Galin", scanned.Artists);
        Assert.Equal("Επιμένω", typed.Title);
        Assert.Equal("Χρήστος Κυριαζής", typed.Artist);
    }

    [Theory]
    [InlineData("Îðê. Öàðèìèð", true)]
    [InlineData("����� - ��������", true)]
    [InlineData("Àë÷î", true)]
    [InlineData("ROSALÍA", false)]
    [InlineData("Équinoxe", false)]
    [InlineData("Makèz", false)]
    public void RecognisesACp1251TagReadAsSomethingElse(string tag, bool garbled)
    {
        Assert.Equal(garbled, MediaInfo.Garbled(tag));
    }

    [Fact]
    public void SplitsTheAuthorOffAtTheFirstSeparatorOnly()
    {
        var (title, author, folder) = MusicManager.PathNames("/music/Bulgarian/Щурците/Щурците - Клетва - Live.wv");

        Assert.Equal("Клетва - Live", title);
        Assert.Equal("Щурците", author);
        Assert.Equal("Щурците", folder);
    }

    [Fact]
    public void BuildsUrlSafeIdsForNonLatinSongs()
    {
        var song = new MusicInfo { Titles = ["До вчера", "Do vchera"], Artists = ["Братя Аргирови"] };

        var id = song.UpdateRandomId();

        Assert.All(id, character => Assert.True(char.IsAsciiLetterOrDigit(character) || character == '-', id));
    }

    [Theory]
    [InlineData("Слави Трифонов & Ку-ку бенд", "Ку-ку бенд")]
    [InlineData("Деси и Тони Стораро", "Тони Стораро")]
    [InlineData("Alisia feat. Konstantin", "Konstantin")]
    [InlineData("Годжи, Гацо Бацов & Сашо Роман", "Гацо Бацов")]
    [InlineData("Mike + The Mechanics", "The Mechanics")]
    public void SplitsCompoundArtistsAndKeepsTheJoinedForm(string artist, string part)
    {
        var parts = TitleNormalizer.SplitArtists(artist);

        Assert.Equal(artist, parts[0]);
        Assert.Contains(part, parts);
    }

    [Fact]
    public void FindsASongByItsFeaturedArtistAlone()
    {
        // The merged ARTISTS tag is one string on the entry; search splits it, so every performer in it
        // — the featured one included — finds the song.
        var song = new MusicInfo { Artists = ["Stiliyan, Jamaikata, Alex Toploto"] };

        Assert.Contains("alextoploto", song.Search.Artists);
        Assert.Contains("jamaikata", song.Search.Artists);
        Assert.Contains("stiliyan", song.Search.Artists);
    }

    [Theory]
    [InlineData("Rad&Co")]
    [InlineData("Sun with Rain")]
    [InlineData("Malcolm X")]
    [InlineData("Daft Punk")]
    public void LeavesSingleArtistNamesWhole(string artist)
    {
        Assert.Equal([artist], TitleNormalizer.SplitArtists(artist));
    }
}

public class MediaInfoTests
{
    [Fact]
    public async Task ReadsAnOggsVorbisComments()
    {
        // ffprobe keeps an Ogg's comments on the stream rather than the container, and the scan only asked the
        // container: 22 of the library's 26 .ogg files were indexed under their path names alone.
        var path = Path.Combine(Path.GetTempPath(), $"gaida-{Guid.NewGuid():N}.ogg");
        if (!await Ffmpeg($"-f lavfi -i anullsrc=r=8000:cl=mono -t 0.2 -c:a libvorbis -metadata title=Overflow " +
                          $"-metadata artist=Tester -metadata ARTISTS=Tester;Guest -y \"{path}\"")) return;

        try
        {
            var info = MediaInfo.GetInformation(path);

            Assert.Equal("Overflow", info.Title);
            Assert.Equal(["Tester", "Tester, Guest"], info.Artists);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LeavesAMissingTitleToThePath()
    {
        // ATL's default is the filename as the title, which would lead in front of the names AddNames parses
        // out of the path -- the whole "Author - Title" as one title.
        var path = Path.Combine(Path.GetTempPath(), $"Tester - Untitled {Guid.NewGuid():N}.mp3");
        if (!await Ffmpeg($"-f lavfi -i anullsrc=r=8000:cl=mono -t 0.2 -metadata artist=Tester -y \"{path}\"")) return;

        try
        {
            Assert.Empty(MediaInfo.GetInformation(path).Titles);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("DJ Damyan;Selina", "DJ Damyan, Selina")]
    [InlineData("Stiliyan;Jamaikata;Alex Toploto", "Stiliyan, Jamaikata, Alex Toploto")]
    [InlineData("Preslava", "Preslava")]
    [InlineData(null, null)]
    public void KeepsEveryValueOfARepeatedTag(string? probed, string? expected)
    {
        // A FLAC carries one ARTISTS comment per performer and ATL joins them with ";". Reading that
        // as a single name left the library showing the last performer alone as the artist.
        Assert.Equal(expected, MediaInfo.Merge(probed));
    }

    /// <returns><c>false</c> when ffmpeg is not installed, so the suite stays green without it.</returns>
    private static async Task<bool> Ffmpeg(string arguments)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo("ffmpeg", "-v quiet " + arguments));
            if (process is null) return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}