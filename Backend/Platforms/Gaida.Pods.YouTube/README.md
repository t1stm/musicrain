# Gaida.Pods.YouTube

The YouTube pod — `gaida-youtube` in [compose.yaml](../../compose.yaml). It wraps [Gaida.Platforms.YouTube](../Gaida.Platforms.YouTube) in the pod HTTP contract: `/classify`, `/resolve`, `/search`, `/playlist`, `/random` and `/content`.

It owns two caches on disk — the JSON search cache and the downloaded audio — and it does not scale horizontally. A second replica shares one egress IP and only reaches YouTube's rate limit sooner. The image needs `yt-dlp` and `ffmpeg` on `PATH`.

| Variable | What it does |
| --- | --- |
| `YOUTUBE_CACHE_DB` | The search cache file. |
| `YOUTUBE_CACHE` | The directory of downloaded audio. Point it somewhere new and the pod starts empty and re-downloads everything. |

## Running it

```bash
docker compose up gaida-youtube      # from Backend/
```

Or on the host, with `yt-dlp` and `ffmpeg` on `PATH`:

```bash
YOUTUBE_CACHE_DB=./YouTube.json YOUTUBE_CACHE=./webm \
  dotnet run --project Platforms/Gaida.Pods.YouTube --urls http://localhost:8082
```

```bash
dotnet run --project Platforms/Gaida.Pods.YouTube -- --self-check
```

## Interesting techniques

- **Query classification as its own route.** [Classify.cs](Classify.cs) is the YouTube half of what used to be a central `QueryParser` in Gaida.API, moved here so each platform owns the shapes it recognises. It answers `200` for a recognised query, `400` for something recognisably YouTube's but malformed, and `404` for "not mine" — which the API defaults to a keyword search. Pure string parsing, so it needs no network and no platform instance.
- **A parser that reads what people actually paste.** `yt://` and `yt-playlist://` IDs, `watch`, `youtu.be`, `shorts`, `embed`, `live` and `playlist` URLs. A bare ID is not claimed: ordinary words fit the alphabet ("Innervision" is 11 characters), so it stays a keyword search.
- **Source-generated regexes.** `Classify` is a `partial class` using `[GeneratedRegex]`, so the patterns are compiled at build time rather than at first match.
- **A self-check with no dependencies.** `--self-check` runs the classifier's checks and exits, so the image is verifiable in CI with no cache volume and no port bound.
- **An admin payload that is only a request ring.** This pod holds no state an operator edits, so its `/Admin` surface reports the recent-request ring and nothing else.

## Technologies worth a look

- [ASP.NET Core minimal APIs](https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis) on [.NET 10](https://dotnet.microsoft.com/)
- [YoutubeExplode](https://github.com/Tyrrrz/YoutubeExplode) and [yt-dlp](https://github.com/yt-dlp/yt-dlp), through the platform project
- [GeneratedRegex](https://learn.microsoft.com/dotnet/standard/base-types/regular-expression-source-generators) source generation
- [Serilog](https://serilog.net/) with [Serilog.AspNetCore](https://github.com/serilog/serilog-aspnetcore)

## Project structure

The pod is flat — [Program.cs](Program.cs) for the routes, [Classify.cs](Classify.cs) and [ClassifySelfCheck.cs](ClassifySelfCheck.cs) for query parsing and its checks, [Dtos.cs](Dtos.cs) for the wire types, and a [Dockerfile](Dockerfile) that builds from the repository's `Backend/` directory so it can reach the shared library.
