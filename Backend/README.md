# Gaida Backend

The backend is a set of small HTTP services behind one public API. Four of them are **platform pods** — the local file library, YouTube, Spotify and Deezer — and each answers the same handful of routes: `/classify`, `/resolve`, `/search`, `/playlist`, `/random` and `/content`. A pod that cannot do something answers `404` there, so support is discovered rather than declared, and [Gaida.API](Services/Gaida.API) can front a new service by pointing an environment variable at its container.

The rest are services with jobs of their own: transcoding, caching, rooms, accounts and an admin panel. Everything is .NET 10 except the Spotify and Deezer pods, which are Python.

The frontend-facing contract is in [API.md](API.md), the room protocol in [MULTIPLAYER_API.md](MULTIPLAYER_API.md), and the stack comes up from [compose.yaml](compose.yaml) — which carries the operational notes for every service in its comments. [nginx.example.conf](nginx.example.conf) shows the path routing in front of it.

## Services

| Service | Role |
| --- | --- |
| [Gaida.API](Services/Gaida.API) | The public front door. Fans a search out to every pod, resolves metadata-only results into playable ones, and transcodes on the way out. The only stateless service, so the only one worth scaling. |
| [Dunav](Services/Dunav) | The fan-out download cache. One upstream fetch per key, an on-disk body served to every client that asked, LRU eviction against a disk budget. |
| [Selo](Services/Selo) | Rooms. WebSocket sessions holding several listeners on one shared clock. |
| [Dom](Services/Dom) | Accounts and playlists. Talks to nothing, and the one volume that holds real user data. |
| [Stih](Services/Stih) | Lyrics. The stack's only LRCLIB client: it fetches the words, writes them beside the audio and indexes what it has. |
| [Oko](Services/Oko) | The admin panel. Reads every other service, holds no state of its own. |
| [Gaida.Bot](Services/Gaida.Bot) | A Discord bot playing from the same library, over HTTP like any other client. Several accounts can play in one guild at once, and Oko watches it like any other service. |

## Getting started

**Prerequisites:** Docker with Compose v2. Working on the .NET services outside a container also wants the [.NET 10 SDK](https://dotnet.microsoft.com/download), with `ffmpeg`, `wvunpack` (from `wavpack`) and `yt-dlp` on `PATH`.

```bash
docker compose up --build
```

The Discord bot is not in that: it needs a token to do anything, so it sits behind a profile.

```bash
docker compose --profile bot up -d --build gaida-bot
```

That is the whole stack on compose's defaults — no secrets, no credentials, every volume under `data/`. Name services to bring up part of it (`docker compose up gaida-api gaida-local`); the pods are independent of each other, and Gaida.API treats a pod that is not there as one that answered nothing.

| Service | Host port | Bound to |
| --- | --- | --- |
| Gaida.API | 5340 | `127.0.0.1` |
| Dunav | 5341 | `127.0.0.1` |
| Selo | 5342 | `127.0.0.1` |
| Dom | 5343 | `127.0.0.1` |
| Stih | 5345 | `127.0.0.1` |
| Oko | 5344 | every interface |

The pods themselves publish nothing: they are reachable only from the compose network, by service name.

> [!WARNING]
> Oko is the one port not bound to the loopback interface, so the host firewall is the only thing in front of it — and it reads every other service, including Dom's accounts. Its Basic auth sends the password base64-encoded, not encrypted. Firewall it, or move it behind nginx with TLS.

A first request, once something is in the library:

```bash
curl "http://localhost:5340/Audio/Search?query=radiohead"
```

## Configuration

Every host-specific value and every secret lives in `.env` beside [compose.yaml](compose.yaml), which compose reads on its own and `.gitignore` keeps out of the repository. The defaults below are what a fresh checkout runs on; the file's own comments carry the rest.

| Variable | Default | What it does |
| --- | --- | --- |
| `MUSIC_LIBRARY_PATH` | `./data/music` | The music library, mounted read-write — the scanner rewrites `Info.json` in place. |
| `ALBUM_COVERS_PATH` | `./data/covers` | Where extracted album art is written, and what nginx serves as `/Album_Covers`. |
| `PUBLIC_DOMAIN` | `http://localhost` | Public prefix substituted into every cover URL. Needs the scheme. |
| `PUBLIC_API_BASE_URL` | `http://localhost:5340` | What the API hands out as `contentUrl`. |
| `ADMIN_TOKEN` | *(unset)* | The shared secret Oko authenticates with. Unset, every `/Admin/*` route answers 404. |
| `ADMIN_USERNAME` / `ADMIN_PASSWORD` | *(unset)* | Oko's own Basic auth. |
| `DEEZER_ARL` | *(unset)* | Deezer account cookie. Unset, the pod is metadata-only. |
| `DEEZER_RESOLVE` | `true` | Tells Gaida.API to resolve Deezer hits elsewhere — what metadata-only mode needs. |
| `DUNAV_MAX_BYTES` | 20 GiB | Disk budget for the download cache, evicted LRU. |
| `YOUTUBE_RANDOM_SHARE` | `0.4` | Share of `RandomResults` drawn from YouTube, the library backfilling the rest. |
| `BOT_CONFIG_FILE` | `./Services/Gaida.Bot/.env.json` | The Discord bot's accounts, mounted read-only — the same file `dotnet run` reads. |
| `BOT_CONFIGURATION` | *(unset)* | Those accounts as a JSON array instead, overriding the file. With neither, `gaida-bot` says so and exits. |

> [!NOTE]
> A missing secret disables a surface rather than exposing it. Without `ADMIN_TOKEN` the whole admin surface is 404 and Oko renders every target as down; without `DEEZER_ARL` every Deezer route works except `/content`. Both are what a fresh checkout runs on.

## Tests

```bash
dotnet test Tests/Gaida.Tests          # shared library and the services
dotnet test Tests/Pods.Tests           # platform code, including the matcher's calibration
pytest Platforms/Gaida.Pods.Spotify Platforms/Gaida.Pods.Deezer
```

`pytest` is not in either pod's `requirements.txt` — those pin what the image ships. Install it separately.

The .NET pods and services also carry `--self-check`, which runs their pure-logic checks and exits, with no library mounted and no port bound:

```bash
dotnet run --project Platforms/Gaida.Pods.YouTube -- --self-check
```

## Interesting techniques

- **One writer, many readers, over a single file.** [`StreamSpreader`](Gaida%20Library/Gaida.Core/Streams/StreamSpreader.cs) is a `Stream` that lets an in-progress download serve every client that asked for it. `FileShare.ReadWrite | FileShare.Delete` on both sides is what makes that legal on Windows and lets the file be evicted while readers still hold it — unlinking removes the directory entry, and the bytes live until the last descriptor closes.
- **Request coalescing.** [`CacheService`](Services/Dunav/CacheService.cs) keys in-flight fetches in a `ConcurrentDictionary<string, Lazy<Task<CacheEntry?>>>`, so a thousand clients racing for a cold track cause exactly one upstream fetch. Same trick as `ManagerService.GetOrStartEncoderAsync`, which coalesces encodes.
- **Streaming responses end to end.** Search returns `IAsyncEnumerable<PlatformResult>`, which ASP.NET Core writes as a chunked JSON array flushed element by element. The status code is decided before the first element, so a failure mid-array truncates the result set instead of producing an error body.
- **Bounded parallel projection that keeps order.** `Streaming.SelectParallel` runs N resolves at once over a stream and still emits in the producer's order, so a playlist's first track is playable long before its last is looked up.
- **Crash-safe cache writes.** [`YouTubeCacher`](Platforms/Gaida.Platforms.YouTube/Cache/YouTubeCacher.cs) writes a full snapshot to `<file>.tmp` and renames it into place. The older truncate-and-append was faster and could corrupt the file if the process died between the two.
- **Span lookups without allocating.** `HashSet<string>.GetAlternateLookup<ReadOnlySpan<char>>()` matches platform ID prefixes on the hot path with no substring allocation.
- **A calibrated fuzzy matcher.** [`MusicManager.Match`](Platforms/Gaida.Platforms.MusicDatabase/Manager/MusicManager.Match.cs) weights title against artist 0.65/0.35 over Levenshtein distance and grades a match `Same`, `Variant` or `Weak`. The thresholds come from a 2000-title pass over the real library, and the file records which titles set them.
- **Cyrillic romanization for search only.** [`Romanize`](Gaida%20Library/Gaida.Core/Utils/Romanize.cs) transliterates for matching; the API still returns the original script, so a track tagged in Cyrillic renders in Cyrillic.
- **Live admin feeds over Server-Sent Events.** [`Fleet`](Services/Oko/Fleet.cs) uses `System.Net.ServerSentEvents` and runs nothing on a timer — every call is driven by an open browser tab, so closing the panel stops all of it.
- **An admin surface that defaults to absent.** `MapAdmin` returns `null` without `ADMIN_TOKEN`, and every `/Admin/*` route answers 404. A missing secret disables the surface rather than exposing it.

## Technologies worth a look

- [.NET 10](https://dotnet.microsoft.com/) minimal APIs, with `IAsyncEnumerable<T>` as the streaming primitive and `.slnx` as the solution format
- [Serilog](https://serilog.net/) with [Serilog.Expressions](https://github.com/serilog/serilog-expressions) for structured logging
- [TagLib#](https://github.com/mono/taglib-sharp) — reads ID3v2, FLAC and WavPack tags out of the library
- [YoutubeExplode](https://github.com/Tyrrrz/YoutubeExplode), with [yt-dlp](https://github.com/yt-dlp/yt-dlp) as the fallback getter
- [FFmpeg](https://ffmpeg.org/) for on-the-fly transcoding
- [WavPack](https://www.wavpack.com/) — `wvunpack`, the only decoder that reads a `.wvc` correction file, so hybrid tracks decode lossless
- [FastAPI](https://fastapi.tiangolo.com/) and [Uvicorn](https://www.uvicorn.org/) for the Python pods
- [SpotAPI](https://github.com/Aran404/SpotAPI) and [deezer-py](https://gitlab.com/RemixDev/deezer-py) — both reach their service's own web endpoints, so neither needs a client ID or a secret
- [DSharpPlus](https://github.com/DSharpPlus/DSharpPlus) nightlies — the core library, `Commands`, `Interactivity` and `Voice`, which passes Opus through without decoding it
- [xUnit](https://xunit.net/) and [coverlet](https://github.com/coverlet-coverage/coverlet)

## Project structure

```
.
├── data/
│   ├── covers/
│   ├── deezer-audio/
│   ├── music/
│   ├── youtube-audio/
│   └── youtube-cache/
├── Gaida Library/
│   ├── Gaida.Admin/
│   ├── Gaida.CLI/
│   └── Gaida.Core/
│       ├── FFmpeg/
│       ├── Platforms/
│       ├── Streams/
│       └── Utils/
├── Platforms/
│   ├── Gaida.Platforms.MusicDatabase/
│   ├── Gaida.Platforms.YouTube/
│   ├── Gaida.Pods.Deezer/
│   ├── Gaida.Pods.MusicDatabase/
│   ├── Gaida.Pods.Spotify/
│   └── Gaida.Pods.YouTube/
├── scripts/
├── Services/
│   ├── Dom/
│   ├── Dunav/
│   ├── Gaida.API/
│   ├── Gaida.Bot/
│   ├── Oko/
│   └── Selo/
├── Tests/
│   ├── Gaida.Tests/
│   └── Pods.Tests/
├── API.md
├── MULTIPLAYER_API.md
├── compose.yaml
├── Gaida.slnx
└── nginx.example.conf
```

[Gaida Library/Gaida.Core](Gaida%20Library/Gaida.Core) is the shared library every service builds on. `Platforms/` holds the abstract `Platform`, its `ISupports*` capability interfaces and `HttpPlatform`, the adapter that makes a remote pod look like an in-process one. `Streams/` holds `StreamSpreader`. `Utils/` holds the matching, romanization and parallel-streaming helpers.

[Platforms](Platforms) splits each service in two. `Gaida.Platforms.*` is library code — search providers and content getters. `Gaida.Pods.*` is the deployable that wraps one of them in HTTP, or, for Spotify and Deezer, a standalone Python app. Each of the six has its own README.

[Services](Services) holds everything that is not a pod. Names are Bulgarian: *Dunav* the Danube, *Selo* a village, *Dom* a home, *Oko* an eye.

[data](data) is where the compose defaults mount their volumes — the music library, extracted album art, and the YouTube and Deezer audio caches. Gitignored, and sized in the tens of gigabytes on a real deployment. Only `covers/` is served directly by nginx, as `/Album_Covers`.

[Tests](Tests) is split by what it covers: `Gaida.Tests` for the shared library and the services, `Pods.Tests` for platform code. The .NET pods also carry a `--self-check` flag that runs their pure-logic checks with no library and no listening host.

