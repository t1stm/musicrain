# Stih

The lyrics service — `stih` in [compose.yaml](../../compose.yaml). Стих, a verse; the name follows Дунав, Село, Дом and Око.

One service fetches the words, writes the files, keeps the index and answers the frontend. Nothing else in the stack grows a lyrics feature: the platform pods say what a track is and push what they already have, and everything else goes through `GET /Audio/Lyrics/Get?id=…`.

| Variable | What it does |
| --- | --- |
| `MUSIC_LIBRARY` | The music library, shared read-write with `gaida-local`. A library track's words are written beside its audio. |
| `LYRICS_DATA` | This service's own volume: `Lyrics.json` and the words for tracks with no library folder. |
| `LRCLIB_URL` | Empty disables every outbound lookup — it still serves the files it has. |
| `LRCLIB_USER_AGENT` | How lrclib.net reaches you if this deployment misbehaves. They ask for it explicitly. |
| `Local__Url` / `Deezer__Url` | The pods that own `audio://` and `deezer://`. A prefix with no URL answers `204`. |
| `LYRICS_RETRY_DAYS` | How long a "found nothing" is believed before it is tried again. Default 30. |
| `LYRICS_SWEEP` | `false` turns the backfill off; the on-demand path keeps working. |
| `LYRICS_SWEEP_DELAY_MS` / `LYRICS_SWEEP_IDLE_HOURS` | One request per delay; sleep this long once the library is done. Default 1000 / 6. |

## Running it

```bash
docker compose up stih        # from Backend/
dotnet run --project Services/Stih -- --self-check
```

`--self-check` runs the parser, the matcher, the index and the path fence with no volumes and no network, and the Dockerfile runs it at build time — a broken parser fails the image rather than the first listener.

## The two stores

Lyrics are files named after the track they belong to, one of two extensions, and a track never has both — synchronized wins, because a timed file contains the plain words too and two copies invite disagreement.

```
/music/Rock/Rammstein/Rammstein - Sonne.flac
/music/Rock/Rammstein/Rammstein - Sonne.lrc     ← synchronized, written by stih
/lyrics/deezer/3135556.lrc                      ← a Deezer track, in stih's own volume
```

A library track's words go **into the library** because that is what makes them outlive this stack: any other player reads them, a backup catches them, and moving the folder moves the words with it. `Lyrics.json` in `/lyrics` is the index — one row per track ever looked at, hit or miss — and it exists to avoid asking LRCLIB twice, not to be the truth about what is on disk. The file is the truth; `gaida-local` reconciles its own `Info.json` from `File.Exists` on every scan.

## Interesting techniques

- **One client, one queue.** This is the stack's only LRCLIB client, and it is single-instance for that reason: [LrcLib.cs](LrcLib.cs) holds a `SemaphoreSlim(1, 1)` around every outbound call so there is never a second in flight, sends a `User-Agent` on every request, and reads a `429`'s `Retry-After` into a `PausedUntil` the sweep parks on. A second replica would break exactly the promise lrclib.net asks for.
- **Rows are only written after a real answer.** A timeout, a `429` or an unreachable pod writes nothing, so a bad afternoon costs a retry rather than a permanent "no lyrics" for a track that has them. That one rule is why the client reports whether a failure was clean rather than just returning `null`.
- **Single-flight per track.** A room of twelve people opening the same song costs one request — the `ConcurrentDictionary<string, Lazy<Task<…>>>` from [Dunav's CacheService](../Dunav/CacheService.cs), entered by ID and removed as soon as it answers so a `/register` arriving a second later is not served a stale answer.
- **An ID and nothing else.** [Tracks.cs](Tracks.cs) turns an ID into a name, an artist and a length by asking the pod that owns the prefix, over the same `/resolve` route Gaida.API uses. That is why matching quality is the same whatever the caller knew, and why adding YouTube one day is one entry in one table.
- **The library's own matching rules, reused rather than reinvented.** [Matching.cs](Matching.cs) normalizes both sides with the same `TitleNormalizer` and `LevenshteinDistance` the library's matcher uses, weights title against artist 0.65/0.35, and requires the same 0.80. What it does not have is the library's weak band: a wrong song you can skip, but wrong words scrolling in time with the music is the one failure a listener cannot ignore.
- **A ±1 second length gate.** The rule that throws out the live version sharing a title. LRCLIB reports whole seconds, so it is a gate on rounding rather than on performance length.
- **A path fence around the one dangerous write.** Writing into someone's music library is the only thing here that could do real damage, so the relative path a pod hands back is rejected unless the resolved absolute path is still inside `MUSIC_LIBRARY`, and the extension is one of the two. A pod that starts answering with `../../etc/passwd` gets a log line and nothing else.
- **A sweep with no resume marker.** `Info.json` and `Lyrics.json` are the progress. A pod restarted mid-sweep asks `gaida-local` for a page of tracks with no lyrics beside them and gets the ones it had not reached — there is no cursor to corrupt, and the work list costs one request per two hundred tracks.

## Project structure

```
.
├── Program.cs       — host, CORS, the two routes
├── Lyrics.cs        — the flow: index → file → resolve → LRCLIB → write → stamp
├── LyricsIndex.cs   — Lyrics.json
├── LrcLib.cs        — the only client of lrclib.net
├── Matching.cs      — which candidate is this track. Pure
├── LrcParser.cs     — LRC text → lines. Pure
├── Tracks.cs        — ID → what the track is, by asking the owning pod
├── Sweep.cs         — the backfill
├── Dtos.cs          — the wire shapes, in and out
├── Admin.cs, SelfCheck.cs
```

Two routes: `GET /Audio/Lyrics/Get` is the public one, mapped on its full public path so nginx proxies it through untouched. `POST /register` is how the platform pods hand over what they downloaded, and it is local only in three layers — it is not under `/Audio` so no nginx location reaches it, the published port is bound to `127.0.0.1`, and with `ADMIN_TOKEN` set it wants the same header Oko uses.
