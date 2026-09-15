<img src="static/favicon.svg" width="64" alt="">

# musicrain

musicrain is a self-hosted music player for the web. A SvelteKit app built to static files, talking to the Gaida backend in [this repository](../README.md) over `/Audio`. It plays from the local library, YouTube and Deezer, keeps a queue, holds playlists and accounts, and can put several listeners in a room on the same track at the same position — inside about 50 ms of each other, network aside.

The same build runs in three places: as an ordinary site, as an installable PWA, and embedded in a Discord voice channel as an Activity. Nothing branches on that beyond URL rewriting; see [Running as a Discord Activity](#running-as-a-discord-activity) below.

Currently running at <https://music.gergov.bg/>.

## Getting started

**Prerequisites:** [Node.js](https://nodejs.org/) 20 or newer.

```bash
npm install
npm run dev
```

Vite serves on <http://localhost:5173>, bound to every interface so a phone on the same network can open it.

### Pointing it at your own API

`PUBLIC_API_URL` decides which backend musicrain talks to, read through [`$env/static/public`](https://svelte.dev/docs/kit/$env-static-public) in [src/lib/discord.ts](src/lib/discord.ts) and inlined at build time.

Vite reads [.env](.env) first, then `.env.local` on top of it. `.env` is committed and holds placeholders; `.env.local` is gitignored and is where this machine's real values belong — `VITE_DISCORD_CLIENT_ID` included:

```bash
echo 'PUBLIC_API_URL=http://localhost:5340/Audio' >> .env.local
```

That address is the backend on compose's defaults. Either file works — editing `.env` in place is fine for a fork that has one deployment — but only `.env.local` stays out of the repository.

> [!NOTE]
> The value is baked into the bundle, so changing it means rebuilding rather than restarting — and inside a Discord Activity it is ignored entirely, since the CSP only allows the `/.proxy` mappings.

| Script | What it does |
| --- | --- |
| `npm run dev` | Vite dev server with HMR |
| `npm run prepare` | `svelte-kit sync` — regenerates the `$env` and route types; npm runs it on install |
| `npm test` | Vitest over jsdom, once |
| `npm run build` | `tsc` then a static build into `build/` |
| `npm run preview` | Serves that build |
| `npm run lint` / `lint:fix` | ESLint, with Prettier as a rule |
| `npm run type-check` | `tsc --noEmit` |

The build is plain files — `adapter-static` with an `index.html` fallback — so deploying is copying `build/` to a web root. `npm run deploy` does exactly that with `rsync`, and its target path is this deployment's; change it or ignore it.

## Interesting techniques

- **Streaming JSON, parsed as it arrives.** The API writes search results element by element, and [src/lib/streamJson.ts](src/lib/streamJson.ts) scans the body with a [ReadableStream](https://developer.mozilla.org/en-US/docs/Web/API/ReadableStream) reader piped through [TextDecoderStream](https://developer.mozilla.org/en-US/docs/Web/API/TextDecoderStream), yielding each object from an [async generator](https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Statements/async_function*) as its closing brace lands. Only the unfinished tail is kept, so a long response never turns scanning into quadratic string work.
- **Position from the audio clock, not the media element.** `timeupdate` fires about four times a second and some browsers quantise it further. [src/components/player/layers/audio/Audio.svelte](src/components/player/layers/audio/Audio.svelte) carries the position on [AudioContext.currentTime](https://developer.mozilla.org/en-US/docs/Web/API/BaseAudioContext/currentTime) between reports and re-anchors on each one, then subtracts [baseLatency](https://developer.mozilla.org/en-US/docs/Web/API/AudioContext/baseLatency) and [outputLatency](https://developer.mozilla.org/en-US/docs/Web/API/AudioContext/outputLatency) so the number describes the sound reaching the ear.
- **Round-trip-compensated room sync.** [src/lib/syncClock.ts](src/lib/syncClock.ts) adds half the measured round trip to the server's reported position before comparing, then corrects with a proportional loop on [playbackRate](https://developer.mozilla.org/en-US/docs/Web/API/HTMLMediaElement/playbackRate) — a couple of percent, with [preservesPitch](https://developer.mozilla.org/en-US/docs/Web/API/HTMLMediaElement/preservesPitch) keeping it inaudible. Link estimates are a minimum over a 90-second window, not a mean: a round trip can only ever be inflated, so averaging folds every queueing spike into the answer.
- **The autoplay policy, detected.** An [AudioContext](https://developer.mozilla.org/en-US/docs/Web/API/AudioContext) that starts `suspended` means this device is silent until a gesture resumes it. The room gate waits for a real `false` rather than for "not true", so nobody joins a room on a guess.
- **OS transport controls** through the [Media Session API](https://developer.mozilla.org/en-US/docs/Web/API/Media_Session_API): lock screen, keyboard media keys, and position state fed from the interpolated clock.
- **One history entry per open layer.** [src/lib/backWatcher.svelte.ts](src/lib/backWatcher.svelte.ts) pushes an entry behind every sheet and modal via SvelteKit's `pushState` (a raw [History.pushState](https://developer.mozilla.org/en-US/docs/Web/API/History/pushState) desynchronises the router's own index), so the back button, an Android back gesture and Escape all close the innermost one first. Ordering lives in [src/lib/backStack.ts](src/lib/backStack.ts) so it can be tested with no router and no history.
- **Sliders on [pointer capture](https://developer.mozilla.org/en-US/docs/Web/API/Element/setPointerCapture).** Seek and volume keep receiving events after the finger leaves the track, so a drag off the element does not strand the handle.
- **A menu with no JavaScript** — the header account panel is a native [popover](https://developer.mozilla.org/en-US/docs/Web/API/Popover_API), which brings light dismiss and the top layer for free.
- **A height-based responsive mode.** [src/app.css](src/app.css) defines `micro` as [`@media (max-height: 320px)`](https://developer.mozilla.org/en-US/docs/Web/CSS/@media/height). Height rather than width, because a phone is narrow *and tall* and wants the compact page, while 320px of height means there is no room for a page at all. An iframe measures its own size, so one rule covers Discord picture-in-picture with no SDK layout-mode event.
- **Platform-honest details in CSS.** [`color-scheme: dark`](https://developer.mozilla.org/en-US/docs/Web/CSS/color-scheme) once at the root so native widgets stop rendering light-on-dark, [`-webkit-tap-highlight-color: transparent`](https://developer.mozilla.org/en-US/docs/Web/CSS/-webkit-tap-highlight-color) with a real press state in its place, and [prefers-reduced-motion](https://developer.mozilla.org/en-US/docs/Web/CSS/@media/prefers-reduced-motion) respected throughout.
- **Installability without a cache.** [src/service-worker.ts](src/service-worker.ts) registers an empty [fetch handler](https://developer.mozilla.org/en-US/docs/Web/API/ServiceWorkerGlobalScope/fetch_event) — the minimum Chrome wants before it offers "Install" — next to a [web app manifest](https://developer.mozilla.org/en-US/docs/Web/Manifest). No precaching, so there is nothing to invalidate.
- **Lyrics that follow the ear, not the display.** The pane's `requestAnimationFrame` loop reads `audio.positionNow()` — the interpolated position with this device's output latency already subtracted — rather than the 10 Hz `currentSeconds` the UI ticks on, because at 100 ms granularity a line lands visibly late against the voice. [src/lib/lyrics.ts](src/lib/lyrics.ts) binary-searches the timestamps and the state is written only when the line actually changes, so a Svelte effect runs per line rather than sixty times a second.
- **Searches cancelled, not raced.** Every in-flight request is tied to an [AbortController](https://developer.mozilla.org/en-US/docs/Web/API/AbortController), so a new query drops the old stream instead of interleaving with it.

## Technologies worth a look

- [SvelteKit](https://svelte.dev/docs/kit) on [adapter-static](https://svelte.dev/docs/kit/adapter-static) with an `index.html` fallback — a client-routed SPA that deploys as plain files
- [Svelte 5 runes](https://svelte.dev/docs/svelte/what-are-runes). State classes live in `.svelte.ts` files outside components, which is what lets the clock and the queue be tested with no DOM
- [Tailwind CSS v4](https://tailwindcss.com/) configured in CSS, not JavaScript: the palette, radii and fonts are `@theme` tokens, and `@custom-variant` adds `micro` and `dark`. Loaded through [@tailwindcss/vite](https://tailwindcss.com/docs/installation/using-vite) with the [forms](https://github.com/tailwindlabs/tailwindcss-forms) and [typography](https://github.com/tailwindlabs/tailwindcss-typography) plugins
- [Vite](https://vitejs.dev/) and [Vitest](https://vitest.dev/) over [jsdom](https://github.com/jsdom/jsdom)
- [@discord/embedded-app-sdk](https://github.com/discord/embedded-app-sdk) for the Activity build
- [svelte-hero-icons](https://www.npmjs.com/package/svelte-hero-icons)
- Variable fonts from Fontsource: [Unbounded](https://fontsource.org/fonts/unbounded) for display, [Golos Text](https://fontsource.org/fonts/golos-text) for body, [JetBrains Mono](https://fontsource.org/fonts/jetbrains-mono) for numerics

## Project structure

```
.
├── src/
│   ├── components/
│   │   ├── browse/
│   │   ├── chat/
│   │   ├── header/
│   │   ├── home/
│   │   ├── player/
│   │   │   └── layers/
│   │   ├── playlist/
│   │   ├── queue/
│   │   ├── search/
│   │   └── session/
│   ├── lib/
│   ├── requests/
│   ├── routes/
│   │   └── (app)/
│   │       ├── album/
│   │       ├── artist/
│   │       ├── browse/
│   │       ├── playlist/
│   │       ├── playlists/
│   │       ├── room/
│   │       ├── rooms/
│   │       └── search/
│   └── state/
├── static/
├── tools/
├── eslint.config.js
├── package.json
├── svelte.config.js
├── tailwind.config.js
├── tsconfig.json
└── vite.config.ts
```

[src/state](src/state) holds the rune-based state classes — audio, queue, room session, account, playlists, quality, search, lyrics. They are plain classes with `$state` fields, so a component reads them directly and a test imports them without mounting anything.

[src/components/player/layers](src/components/player/layers) splits the player by concern rather than by screen: audio graph, transport controls, seek bar, volume, quality picker and track info are separate components composed into both the compact bar and the full-screen player.

[src/lib](src/lib) is the logic with no UI attached: the two clocks, the back stack, the JSON stream scanner, slider interaction handling, the active-lyric search, the shared seek, source-badge lookup and the Discord URL rewriting. Most of it has a `.test.ts` beside it.

[src/routes/(app)](src/routes/%28app%29) is a [SvelteKit group](https://svelte.dev/docs/kit/advanced-routing#Advanced-layouts-group) — every page shares the app shell layout without adding a path segment.

[static](static) is served at the site root: the SVG mark and its PNG fallback, the 192px and 512px PWA icons, and the web app manifest.

[tools](tools) holds `roomSim.mjs`, a room simulator for exercising the sync loop without opening a dozen browser tabs.

## Running as a Discord Activity

1. Set `VITE_DISCORD_CLIENT_ID` in `.env.local` (your application's ID).
2. In the Developer Portal, enable Activities and add these **URL Mappings**:

   | Prefix | Target |
   | ------ | ------ |
   | `/`    | the musicrain host (`music.gergov.bg`, or your dev tunnel host) |
   | `/api` | `api.gergov.bg` |

   The activity iframe blocks every host that isn't mapped, so the client rewrites API URLs to `/.proxy/<prefix>` when it detects it's embedded ([src/lib/discord.ts](src/lib/discord.ts)). Artwork lives on ytimg and the cover host, neither of which is mapped, so thumbnails are taken from `/Audio/Cover?id=` instead — the API fetches and caches them and serves them from its own origin.

3. For local development, serve `npm run dev` over HTTPS — e.g. `cloudflared tunnel --url http://localhost:5173` — and point the `/` mapping at the tunnel host. Then launch the activity from a voice channel.

Outside Discord nothing changes: `isDiscordActivity` is false, URLs stay absolute and the SDK is never constructed.
