<img src="Frontend/static/favicon.svg" width="64" alt="">

# musicrain

musicrain is a self-hosted music player for the web. A SvelteKit app built to static files, it plays from a local file library, YouTube, Spotify and Deezer, keeps a queue, holds playlists and accounts, and can put several listeners in a room on the same track at the same position — inside about 50 ms of each other. The same build runs as an ordinary site, as an installable PWA, or embedded in a Discord voice channel as an Activity.

Currently running at <https://music.gergov.bg/>.

Behind it is **Gaida**, the self-hosted backend in this repository: one public API in front of several interchangeable "platform pods", one per music service. Every pod speaks the same small HTTP contract, so adding a service means adding a container, not touching the API. Two of the four pods are Python; the rest is .NET 10. The endpoint contract is in [Backend/API.md](Backend/API.md), the room protocol in [Backend/MULTIPLAYER_API.md](Backend/MULTIPLAYER_API.md), and the whole stack comes up from [Backend/compose.yaml](Backend/compose.yaml).

## Getting started

> [!IMPORTANT]
> Clone with submodules — the Discord bot builds against a pinned DSharpPlus branch.
> `git clone --recurse-submodules <url>`, or `git submodule update --init` in a clone you already have.

**Prerequisites:** [Node.js](https://nodejs.org/) 20 or newer for musicrain, and [Docker](https://docs.docker.com/get-docker/) with Compose v2 for the backend. Building the .NET services outside a container also wants the [.NET 10 SDK](https://dotnet.microsoft.com/download).

Run musicrain:

```bash
cd Frontend
npm install
npm run dev
```

Vite serves on <http://localhost:5173>, bound to every interface so a phone on the same network can open it. [Frontend/README.md](Frontend/README.md) has the full script list, the Discord Activity setup and the frontend's own structure.

> [!IMPORTANT]
> `Frontend/.env` ships placeholders, so point musicrain at your own API before it can play anything: `echo 'PUBLIC_API_URL=http://localhost:5340/Audio' >> Frontend/.env.local` — the backend on compose's defaults. The value is inlined at build time, so changing it needs a rebuild rather than a restart.

Then the backend:

```bash
cd Backend
docker compose up --build
```

The compose defaults are what a fresh checkout runs on: no secrets, no accounts, every volume under `Backend/data/`, and the API on <http://localhost:5340>. Put music in `Backend/data/music` — or point `MUSIC_LIBRARY_PATH` at where it already lives — and the library pod picks it up on its next start. Spotify and Deezer search work with no credentials; Deezer audio is the one thing that needs a cookie.

Host-specific values and secrets live in `Backend/.env`, which compose reads on its own and git ignores. [Backend/README.md](Backend/README.md#configuration) lists them, and [Backend/nginx.example.conf](Backend/nginx.example.conf) shows the path routing that goes in front of the stack.

## Interesting techniques

- **Streaming JSON arrays, parsed as they arrive.** The API writes search results element by element over chunked transfer, and [Frontend/src/lib/streamJson.ts](Frontend/src/lib/streamJson.ts) scans the body with a [ReadableStream](https://developer.mozilla.org/en-US/docs/Web/API/ReadableStream) reader piped through [TextDecoderStream](https://developer.mozilla.org/en-US/docs/Web/API/TextDecoderStream), yielding each object from an [async generator](https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Statements/async_function*) the moment its closing brace lands.
- **Playback position from the audio clock, not the media element.** [Frontend/src/components/player/layers/audio/Audio.svelte](Frontend/src/components/player/layers/audio/Audio.svelte) interpolates between `timeupdate` reports using [AudioContext.currentTime](https://developer.mozilla.org/en-US/docs/Web/API/BaseAudioContext/currentTime), then subtracts [baseLatency](https://developer.mozilla.org/en-US/docs/Web/API/AudioContext/baseLatency) and [outputLatency](https://developer.mozilla.org/en-US/docs/Web/API/AudioContext/outputLatency) to get the position actually reaching the ear.
- **Round-trip-compensated clock sync.** [Frontend/src/lib/syncClock.ts](Frontend/src/lib/syncClock.ts) corrects the server's reported position by half the measured round trip and steers [playbackRate](https://developer.mozilla.org/en-US/docs/Web/API/HTMLMediaElement/playbackRate) through a proportional loop, with [preservesPitch](https://developer.mozilla.org/en-US/docs/Web/API/HTMLMediaElement/preservesPitch) keeping the correction inaudible. Link estimates are a minimum over a 90-second window rather than a mean, because a round trip can only ever be inflated.
- **Native OS transport controls** via the [Media Session API](https://developer.mozilla.org/en-US/docs/Web/API/Media_Session_API) — lock screen, keyboard media keys and position state.
- **One history entry per open layer.** [Frontend/src/lib/backWatcher.svelte.ts](Frontend/src/lib/backWatcher.svelte.ts) pushes a [history entry](https://developer.mozilla.org/en-US/docs/Web/API/History/pushState) behind every sheet and modal, so the back button, an Android back gesture and Escape all close the innermost one.
- **A height-based responsive mode.** [Frontend/src/app.css](Frontend/src/app.css) defines a `micro` variant as [`@media (max-height: 320px)`](https://developer.mozilla.org/en-US/docs/Web/CSS/@media/height), which covers Discord picture-in-picture, a popped-out window and a phone on its side in one rule, with no SDK layout-mode event.
- **Installability without a cache.** [Frontend/src/service-worker.ts](Frontend/src/service-worker.ts) registers an empty [fetch handler](https://developer.mozilla.org/en-US/docs/Web/API/ServiceWorkerGlobalScope/fetch_event) — the minimum Chrome wants before it offers "Install" — next to a [web app manifest](https://developer.mozilla.org/en-US/docs/Web/Manifest). No precaching, so there is nothing to invalidate.
- **One writer, many readers, over a single file.** On the backend, [`StreamSpreader`](Backend/Gaida%20Library/Gaida.Core/Streams/StreamSpreader.cs) lets an in-progress download serve every client that asked for it, using `FileShare.ReadWrite | FileShare.Delete` so the file can be evicted while readers still hold it — the bytes live until the last descriptor closes.
- **Request coalescing at the cache.** [Dunav](Backend/Services/Dunav) keys in-flight fetches in a `ConcurrentDictionary<string, Lazy<Task<…>>>`, so a thousand clients racing for a cold track cause exactly one encode.
- **A calibrated fuzzy matcher.** Local-library matching weights title against artist 0.65/0.35, penalises unrecognised bracket tags, and uses thresholds derived from a 2000-title pass over the real library rather than from taste.

## Technologies worth a look

- [SvelteKit](https://svelte.dev/docs/kit) on [adapter-static](https://svelte.dev/docs/kit/adapter-static), with [Svelte 5 runes](https://svelte.dev/docs/svelte/what-are-runes) — state classes live in `.svelte.ts` files outside components, which is what lets the sync clock and the queue be unit-tested with no DOM
- [Tailwind CSS v4](https://tailwindcss.com/) configured in CSS rather than JavaScript: the palette, radii and fonts are `@theme` tokens
- [Vite](https://vitejs.dev/) and [Vitest](https://vitest.dev/) over [jsdom](https://github.com/jsdom/jsdom)
- [@discord/embedded-app-sdk](https://github.com/discord/embedded-app-sdk) for running inside a Discord voice channel
- Variable fonts from Fontsource: [Unbounded](https://fontsource.org/fonts/unbounded) for display, [Golos Text](https://fontsource.org/fonts/golos-text) for body, [JetBrains Mono](https://fontsource.org/fonts/jetbrains-mono) for numerics
- [.NET 10](https://dotnet.microsoft.com/) with ASP.NET Core minimal APIs on the backend, using `IAsyncEnumerable<T>` responses as the streaming primitive, and [Serilog](https://serilog.net/) throughout
- [TagLib#](https://github.com/mono/taglib-sharp) for library tags, [YoutubeExplode](https://github.com/Tyrrrz/YoutubeExplode) with [yt-dlp](https://github.com/yt-dlp/yt-dlp) as fallback, and [FFmpeg](https://ffmpeg.org/) for transcoding
- [FastAPI](https://fastapi.tiangolo.com/) for the two Python pods, over [SpotAPI](https://github.com/Aran404/SpotAPI) and [deezer-py](https://gitlab.com/RemixDev/deezer-py) — both reach their service's own web endpoints, so neither needs an API key
- [DSharpPlus](https://github.com/DSharpPlus/DSharpPlus) on its `voice-rewrite` branch, tracked as a submodule, for the Discord bot

## Project structure

```
.
├── Frontend/
│   ├── src/
│   │   ├── components/
│   │   ├── lib/
│   │   ├── requests/
│   │   ├── routes/
│   │   └── state/
│   ├── static/
│   └── tools/
└── Backend/
    ├── data/
    │   ├── covers/
    │   ├── deezer-audio/
    │   ├── music/
    │   ├── youtube-audio/
    │   └── youtube-cache/
    ├── DSharpPlus/
    ├── Gaida Library/
    │   ├── Gaida.Admin/
    │   ├── Gaida.CLI/
    │   └── Gaida.Core/
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
    └── Tests/
        ├── Gaida.Tests/
        └── Pods.Tests/
```

[Frontend](Frontend) is musicrain itself, with its own [README](Frontend/README.md) covering the scripts, the Discord Activity setup and the per-directory layout. [Frontend/src/state](Frontend/src/state) holds the rune-based state classes — audio, queue, room session, account, playlists. [Frontend/src/lib](Frontend/src/lib) is the logic with no UI attached: the two clocks, the back stack, the JSON stream scanner, the Discord URL rewriting. [Frontend/static](Frontend/static) is served at the site root: favicons, the PWA icons and the web app manifest.

[Backend/Platforms](Backend/Platforms) holds the pods. A `Gaida.Platforms.*` project is library code — search providers and content getters against one service. A `Gaida.Pods.*` project is the container that wraps one of them in HTTP, or, for Spotify and Deezer, a standalone Python app. Each has its own README.

[Backend/Services](Backend/Services) holds everything that is not a pod, named in Bulgarian: **Gaida.API** is the public front door and the transcoder, **Dunav** the fan-out download cache, **Selo** the room server, **Dom** accounts and playlists, **Oko** the admin panel, and **Gaida.Bot** a Discord bot on the same library.

[Backend/data](Backend/data) is where the compose defaults mount their volumes — the music library, extracted album art served as `/Album_Covers`, and the YouTube and Deezer audio caches. All of it is gitignored, and only the album covers are ever served directly by nginx.

[Backend/DSharpPlus](Backend/DSharpPlus) is a git submodule pinned to the library's `voice-rewrite` branch, needed for the bot's voice support.
