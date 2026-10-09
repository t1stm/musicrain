# Gaida.Platforms.YouTube

YouTube as a platform: keyword search, playlist expansion, random picks and audio. IDs are prefixed `yt://`, playlists `yt-playlist://`. Both search and content are layered — a cache in front, and more than one way to reach YouTube behind it — so a change at YouTube's end degrades the platform instead of taking it down.

This project is the platform, not a deployable. [Gaida.Pods.YouTube](../Gaida.Pods.YouTube) wraps it in HTTP; [Gaida.Bot](../../Services/Gaida.Bot) uses it in-process.

`YOUTUBE_CACHE_DB` points at the search cache database (a `.json` path still works: the database goes beside it, with the extension swapped), `YOUTUBE_CACHE` at the directory of downloaded audio.

## Using it

Not a deployable — it is referenced by [Gaida.Pods.YouTube](../Gaida.Pods.YouTube) and by [Gaida.Bot](../../Services/Gaida.Bot). `yt-dlp` and `ffmpeg` are expected on `PATH`, since the fallback getter is a process.

```bash
dotnet test Tests/Pods.Tests         # from Backend/ — cacher and getter tests
```

## Interesting techniques

- **Priority chains rather than conditionals.** Search providers and content getters are ordered by a `Priority` property at `Initialize()`, then tried in turn until one answers. Content goes local cache (99), then [YoutubeExplode](https://github.com/Tyrrrz/YoutubeExplode) (40), then [yt-dlp](https://github.com/yt-dlp/yt-dlp) (20) — the slow, reliable one last. Adding a source is adding a class, not editing a branch.
- **A search cache that survives the API it wraps.** Results are stored keyed by query, so a repeat search costs nothing and a YouTube outage still answers for everything seen before.
- **A cache that is never in memory.** Every search result ever seen is one row in `YouTube.db`, inserted once and looked up by ID. The JSON file it replaced was rewritten whole on every new search and held whole on the heap; at half a million entries that was 173 MB per write. A `YouTube.json` beside the database is imported once, on the first start with no database, and left where it is.
- **Span-based prefix matching.** ID identifiers are matched through `HashSet<string>.GetAlternateLookup<ReadOnlySpan<char>>()`, so classifying a query allocates nothing.

## Technologies worth a look

- [YoutubeExplode](https://github.com/Tyrrrz/YoutubeExplode) — YouTube's own endpoints, no API key and no quota
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) as the fallback getter, invoked as a process and expected on `PATH`
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) and [Dapper](https://github.com/DapperLib/Dapper) for the search cache, through the shared [Gaida.Sqlite](../../Gaida%20Library/Gaida.Sqlite)
- [Serilog](https://serilog.net/), through the shared [Gaida.Core](../../Gaida%20Library/Gaida.Core) abstractions

## Project structure

```
.
├── Cache/
├── Getters/
└── Search Providers/
```

[Cache](Cache) holds `YouTubeCacher`, the SQLite search cache.

[Search Providers](Search%20Providers) holds two: the cached one, and the YoutubeExplode one behind it.

[Getters](Getters) holds the local-cache getter and the YoutubeExplode getter. The third, yt-dlp, is shared and lives in [Gaida.Core/Platforms/Cross Platform](../../Gaida%20Library/Gaida.Core/Platforms/Cross%20Platform) — it works for more than one site.
