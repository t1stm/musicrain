# Gaida.Pods.MusicDatabase

The local library pod — `gaida-local` in [compose.yaml](../../compose.yaml). It wraps [Gaida.Platforms.MusicDatabase](../Gaida.Platforms.MusicDatabase) in the pod HTTP contract and adds the routes only a library can answer: `/browse` for the folder tree, `/artist`, `/album` and `/variant` for the lookups the resolver uses when it prefers a local copy over an upload.

It is the one pod that is not replicable — it is pinned to the library volume — and the one that owns state an operator edits, so its admin surface is larger than the others'.

| Variable | What it does |
| --- | --- |
| `STORAGE` | The music library root, mounted read-write: the scanner rewrites `Info.json` in place. |
| `ALBUM_COVERS` | Where extracted art is written, and what nginx serves as `/Album_Covers`. |
| `DOMAIN` | Public prefix substituted into each cover URL. Needs a scheme — a bare host is not a URL the API can fetch. |
| `DEEZER_URL` | The Deezer pod, for `/Admin/import-deezer` only. Unset, that one route answers 400 and nothing else changes. |

## Running it

```bash
docker compose up gaida-local        # from Backend/
```

Or on the host, with the library somewhere convenient and `ffmpeg` and `wvunpack` (from `wavpack`) on `PATH` — `ffprobe` reads the tags, and `wvunpack` is what decodes a hybrid WavPack track against its `.wvc` correction file:

```bash
STORAGE=../../data/music ALBUM_COVERS=./Album_Covers DOMAIN=http://localhost:8081 \
  dotnet run --project Platforms/Gaida.Pods.MusicDatabase --urls http://localhost:8081
```

The scan runs in the background, so the pod answers before it has finished reading the tree. Its tests are in [Tests/Pods.Tests](../../Tests/Pods.Tests); `--self-check` runs the pure-logic ones with no library mounted.

## Lyrics

This pod does not fetch lyrics and has never heard of LRCLIB. **`stih`** does both, and it writes the files it finds straight into this pod's volume, beside the audio — `Rock/Rammstein/Rammstein - Sonne.lrc` next to `Rock/Rammstein/Rammstein - Sonne.flac`, `.txt` when the words are not timed. That is why a `.lrc` can appear in the library with nothing here having written it, and why both containers run as the same `LIBRARY_UID:LIBRARY_GID`.

What this pod owns is the record of it. Each `Info.json` entry carries three fields, all `null` by default:

| Field | Values | Meaning |
| --- | --- | --- |
| `LyricsType` | `null`, `"Unsynchronized"`, `"Synchronized"` | Which file sits beside the audio. |
| `LyricsSource` | `null`, `"Deezer"`, `"LRCLIB"` | Where it came from. `null` beside a non-null type means it was already in the folder. |
| `LyricsChecked` | `null` or a date | The day `stih` last reported finding nothing. |

The file on disk settles any disagreement: every scan reconciles both of the first two fields from `File.Exists`, so a `.lrc` deleted by hand disappears from `Info.json` at the next boot whatever `stih`'s own index says.

Two routes serve `stih`, plus one field on an existing one:

| Route | What it answers |
| --- | --- |
| `GET /lyrics/missing?take=200&retryDays=30` | Tracks with no lyrics beside them, oldest-checked first. The names are the untransliterated tags — LRCLIB holds tracks under the names they were released with. |
| `POST /lyrics/stamp?id=&type=&source=` | Records what `stih` wrote, or, with `type` omitted, that it found nothing. It writes no lyrics file. |
| `GET /resolve?id=` | Also returns `relativeLocation`, `lyricsType` and `lyricsSource`, so `stih` learns where to write in the same call that tells it the track's name and length. |

Both are ordinary routes rather than `/Admin` ones: this is service-to-service traffic on the internal network, nginx proxies neither, and gating them behind `ADMIN_TOKEN` would make lyrics stop working in a deployment that has chosen not to run the admin panel.

## Interesting techniques

- **A scan that does not block the boot.** `Initialize()` starts the library scan and returns, so the pod is listening while it reads thousands of folders. Folders are parsed with `Parallel.ForEachAsync` into a `ConcurrentBag`; folder order stops being stable, and nothing downstream depends on it.
- **Environment variables copied out of configuration.** The platform layer reads `STORAGE`, `ALBUM_COVERS` and `DOMAIN` as process environment variables, so `Program.cs` copies them out of `IConfiguration` at startup. That keeps the library usable from a CLI or a bot with no host builder, while a container still configures it the normal way.
- **Admin routes split by HTTP verb on purpose.** Reads are `GET` so they pass through Oko's plain read proxy; anything that changes the library is a `POST` through its audited action proxy.
- **Repeated query parameters as an ordered list.** `/variant` takes repeated `title=` and `artist=` parameters — the variant list, in preference order, without inventing a body format for a GET.
- **The only outbound call in the pod is an import.** `/Admin/import-deezer` pulls a track's metadata and bytes out of the Deezer pod and writes them into the library's `Deezer/` folder, promoting a cached track into permanent storage. Nothing else here makes a request.
- **A self-check with no dependencies.** `--self-check` runs the pure-logic checks and exits, so the image can be verified in CI with no library mounted and no port bound.

## Technologies worth a look

- [ASP.NET Core minimal APIs](https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis) on [.NET 10](https://dotnet.microsoft.com/), streaming results as `IAsyncEnumerable<T>`
- [Serilog](https://serilog.net/) with the console sink
- `Gaida.Admin`'s `MapAdmin`, which returns `null` without `ADMIN_TOKEN` — a missing secret disables the admin surface rather than exposing it

## Project structure

```
.
└── Album_Covers/
```

[Album_Covers](Album_Covers) is the default export directory for extracted art when `ALBUM_COVERS` is unset — useful when running the pod outside a container. In compose it is a volume instead.

Everything else is [Program.cs](Program.cs): the routes, the admin surface and the self-check. The pod holds no logic of its own beyond wiring.
