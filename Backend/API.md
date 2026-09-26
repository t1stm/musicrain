# Public Audio API

The example production base URL is `https://api.example.com`. All endpoints below are rooted at `/Audio` and use JSON unless they stream audio.

## Discovery result

`GET /Audio/Search?query={term}`, `GET /Audio/RandomResults?count={count}`, `GET /Audio/Artist/Local?term={artist}`, `GET /Audio/Artist/Deezer?term={artist}`, `GET /Audio/Artist/YouTube?term={artist}`, and `GET /Audio/Album?artist={artist}&album={album}` all return an array with exactly this shape:

```json
{
  "id": "audio://example-id",
  "name": "Track title",
  "artist": "Artist name",
  "album": "Album title",
  "contentUrl": "https://api.example.com/Audio/DownloadRaw?id=audio%3A%2F%2Fexample-id",
  "duration": "00:03:45",
  "thumbnailUrl": null,
  "originalTitle": "Заглавие",
  "originalArtist": "Изпълнител"
}
```

`id`, `name`, `artist`, `contentUrl`, and `duration` are always non-null. Local-library IDs always begin with `audio://`; YouTube IDs use `yt://`, and Deezer IDs `deezer://`. `album`, `thumbnailUrl`, `originalTitle` and `originalArtist` may be `null`. `contentUrl` is an absolute downloadable raw-audio URL.

`originalTitle` and `originalArtist` carry the untransliterated title and artist as the source tagged them. **`name` and `artist` are sourced from them when present**, falling back to the romanized form, so a track tagged in Cyrillic or Japanese renders in its own script by default and `name` will usually equal `originalTitle`. The romanized form is not on the wire — it is used server-side for search and matching only. Clients that want the romanized string should not expect it here.

- `Search` accepts normal text (including Cyrillic), local IDs, the server's `yt://` and `deezer://` IDs, and playlist URLs (YouTube, Spotify and Deezer).
- A text search also asks Spotify's catalogue, which has no audio of its own, so each hit it contributes is looked up in the library, then on Deezer, then on YouTube before it reaches the response. Those entries are ordinary results with `audio://`, `deezer://` or `yt://` IDs — a `spotify://` ID never reaches a client — and a hit that was already returned by the platform it resolved to is not repeated. Hits nothing playable was found for are left out.
- Deezer results are *not* resolved away: that platform has its own audio, so a `deezer://` ID reaches the client and plays from Deezer. A deployment without Deezer credentials runs its pod in metadata-only mode, where Deezer hits are resolved to `audio://` or `yt://` exactly like Spotify's and no `deezer://` ID is ever returned.
- `RandomResults` accepts `count` from 1 through 200, inclusive. An invalid count returns `400 {"error":{"code":"invalid_count","message":"..."}}`.
- `RandomResults` also accepts `youTubeShare` from 0 through 1 (default `0.4`): the share of results drawn from YouTube, with the local library supplying the rest and backfilling anything YouTube is short of. Out of range returns `400 {"error":{"code":"invalid_share","message":"..."}}`.
- `Artist/Local` returns `200 []` when the artist is empty or unmatched, and uses `artist`, then `name`, then `id` as its stable sort order. `Artist/YouTube` is a keyword search and comes back in YouTube's relevance order. `Artist/Deezer` is the top tracks of the Deezer artist whose name matches `term` (Deezer's best match when none does exactly), best-known first; it returns `200 []` when the pod runs metadata-only, since its IDs would not play.
- `Album` returns `200 []` when either parameter is empty, or when neither the library nor Deezer has the album. The library is asked first and Deezer only when the library has none, so the rows are all `audio://` or all `deezer://`, never mixed. Artist matching is `Artist/Local`'s in the library; Deezer answers only with a record credited to exactly `artist` (ignoring case) whose title is `album` or contains it. Order is the album's own: the library serves the order in the playlist file that defines the album, Deezer its catalogue track order.
- The library answers `Album` only for albums it holds as a playlist file (an `.m3u`/`.m3u8` beside the tracks), not for every album name it has tagged. A result's `album` field is therefore not a promise that `Album` returns library rows for it — the answer may come from Deezer, and may be `[]`. Treat `album` as a link worth offering and the endpoint as the thing that decides.

### Streamed responses

These six endpoints write their array element by element (`Transfer-Encoding: chunked`, no `Content-Length`), so a client reading the body incrementally can render each track as it arrives instead of waiting for the last one. It is an ordinary JSON array either way — a client that calls `response.json()` needs no change.

Two consequences for clients that do read it incrementally:

- The status code is decided before the first element. Once the array has started, an upstream failure can only end it early, so a truncated result set is short, never an error body. Every validation error (`invalid_count`, `invalid_share`) still arrives as a normal `400` with nothing written.
- Ordering is the producer's, and it is not grouped by service. `Search` asks every platform at once and writes each hit the moment it lands, so a library track, a YouTube video and a Deezer track can arrive in any order — relevance, service and everything else is the client's to sort. `RandomResults` interleaves its YouTube and library picks as they arrive rather than shuffling a finished list; the requested `youTubeShare` still holds, and the library still backfills whatever YouTube is short of.

## Mixed query resolver

`GET /Audio/FindQueryType?query={value}` resolves pasted values. The response has a machine-readable `kind` discriminator.

| Input | `kind` | Response fields |
| --- | --- | --- |
| `audio://...` | `local` | `query` is the canonical local ID; `result` is one discovery result |
| YouTube video URL, `yt://...`, or 11-character video ID | `youtubeVideo` | `query` is canonical `yt://...`; `result` is one discovery result |
| YouTube playlist URL, `yt-playlist://...`, or recognised playlist ID | `youtubePlaylist` | `query` is a canonical playlist URL; `playlistId`. No entries — send `query` to `Search` |
| Spotify track URL, `spotify:track:...`, or `spotify://...` | `local` or `youtubeVideo` | Spotify has no audio, so the track is looked up in the library, then on YouTube; `kind` and `result` describe whatever was found. `404 not_found` when nothing was |
| Spotify playlist URL, `spotify:playlist:...`, or `spotify-playlist://...` | `spotifyPlaylist` | `query` is the canonical `spotify-playlist://...`; `playlistId`. No entries — send `query` to `Search` |
| Deezer track URL or `deezer://...` | `deezerTrack` | `query` is the canonical `deezer://...`; `result` is one discovery result. In metadata-only mode this comes back as `local` or `youtubeVideo` instead, like a Spotify track |
| Deezer playlist URL or `deezer-playlist://...` | `deezerPlaylist` | `query` is the canonical `deezer-playlist://...`; `playlistId`. No entries — send `query` to `Search` |
| Ordinary text | `search` | `query` is the trimmed text; call `Search` with it |

Examples:

```json
{"kind":"youtubeVideo","query":"yt://dQw4w9WgXcQ","result":{"id":"yt://dQw4w9WgXcQ","name":"...","artist":"...","contentUrl":"...","duration":"00:03:33","thumbnailUrl":"..."}}
```

```json
{"kind":"youtubePlaylist","query":"https://www.youtube.com/playlist?list=PL...","playlistId":"PL..."}
```

A playlist resolution carries no entries. This endpoint answers what a pasted value *is*, and the answer for a playlist — `kind`, the canonical `query`, `playlistId` — is everything classify already knows, so it comes back in one fan-out. To get the tracks, send the canonical `query` back to `Search`, which routes a playlist claim to the same lookup and streams the entries as they resolve.

That is the only route to a playlist's tracks: `results` used to be assembled whole here and has been removed. It lived inside an envelope, so there was nothing to stream into, and a Spotify playlist costs one lookup per track (the library first, then Deezer, then YouTube) — the slowest thing the API does, behind a response that could not begin until the last one landed.

Malformed `audio://`, `yt://`, `yt-playlist://`, or unsupported URLs return a JSON error such as:

```json
{"error":{"code":"invalid_query","message":"The YouTube video ID is invalid."}}
```

Known but missing local/video IDs return `404` with `code: "not_found"`. A temporary resolver failure returns `503` with `code: "resolver_unavailable"`; no resolver error is sent as HTML or an empty body.

## Local variants

`GET /Audio/Local/Variant?name={video title}&artist={channel title}&duration=00:04:32` asks whether the local library already has the recording a YouTube result names. It searches the in-memory library only — no YouTube call, no cache lookup — so it is cheap enough to run after every roll. `204` when nothing is worth offering, `400 invalid_query` for a missing `name` or an unparseable `duration`.

```json
{
  "match": "same",
  "score": 0.97,
  "durationDeltaSeconds": 11,
  "youTubeTags": [],
  "libraryTags": [],
  "result": {"id": "audio://ramsonne-x9", "name": "Sonne", "artist": "Rammstein", "duration": "00:04:32"}
}
```

`result` is an ordinary discovery result. `match` is `same` (the tag sets agree), `variant` (a tagged upload answered with the plain library copy) or `weak` (offer it, but say so). Renditions run one way only: an `(Instrumental)` upload may be answered with your plain copy, a plain upload is never answered with your instrumental, and `(Remastered)` counts as the same performance. `durationDeltaSeconds` is library minus upload and is reported, never a reason to reject a `same` or `variant` — uploads carry intros.

## Lyrics

`GET /Audio/Lyrics/Get?id={id}` returns the words for one track. An ID and nothing else: the service asks the pod that owns the ID what the track is called and how long it runs, so a caller never has to keep a copy of the matching rules. `204` when there are none anywhere — the ordinary answer for a great many tracks, and not an error. `400 invalid_query` for a missing or malformed `id`.

```json
{
  "type": "Synchronized",
  "source": "LRCLIB",
  "lines": [{ "at": 0.0, "text": "" }, { "at": 12.34, "text": "Alle warten auf das Licht" }],
  "text": "Alle warten auf das Licht\n…",
  "matched": { "title": "Sonne", "artist": "Rammstein", "length": 272 }
}
```

`type` is `Synchronized` or `Unsynchronized`. `lines` is present for both: unsynchronized words come back with every `at` as `null`, so a client renders one component either way and only the highlighting branches. `text` is always the plain block with the timestamps stripped, for someone copying the words out. `source` is `Deezer`, `LRCLIB`, or `null` for a file that was already sitting in the library folder. `matched` is what the words actually belong to, which is not always what was asked for — it is what lets someone see they have been given the live version's lyrics.

Where the words come from, in order: a file this stack already has for the track; a `.lrc` or `.txt` someone put in the library folder by hand; then [LRCLIB](https://lrclib.net), matched on length to within a second and on the library's own title-and-artist rules, with anything below a strong match rejected — wrong words scrolling in time with the music is the one failure a listener cannot ignore. A hit for a library track is written beside the audio, so it is there for every listener afterwards and for any other player that opens the folder. A miss is remembered for a month and then tried again, because LRCLIB grows.

Responses are `Cache-Control: public, max-age=86400`. YouTube IDs answer `204`: there is no source for them yet, and saying so is the honest state.

## Artwork

`GET /Audio/Cover?id={id}` returns the track's artwork as an image, for any local or YouTube ID. The API fetches the upstream thumbnail once and keeps it on disk, so repeat requests never touch the platform layer; the first request streams to the caller while it downloads rather than waiting for the whole image. An ID with no artwork returns `404 {"error":{"code":"not_found","message":"..."}}`, a missing ID `400 invalid_query`.

Responses are `Cache-Control: public, max-age=604800`. The cache directory comes from the `THUMBNAIL_CACHE` environment variable and defaults to `gaida-thumbnails` under the system temp directory.

This is what an embedded Discord activity uses for every thumbnail: the discovery endpoints hand out absolute ytimg and cover-host URLs, and the activity iframe can only reach hosts it has a URL mapping for.

## Library tree

`GET /Audio/Browse?path={folder}` returns one level of the local library's folder tree, so a client
can open folders one at a time instead of pulling the whole thing. Omit `path`, or send an empty
one, for the root.

```json
{
  "path": "Bulgarian/Естрада",
  "folders": [
    { "name": "Братя Аргирови", "path": "Bulgarian/Естрада/Братя Аргирови", "songs": 5 }
  ],
  "files": []
}
```

`folders` are the immediate subfolders, sorted case-insensitively; `songs` counts every track
anywhere beneath the folder, not only the ones directly in it. `files` are the tracks sitting
directly in `path`, as ordinary discovery results — the same shape `Search` returns, so a browsed
track plays and queues exactly like a searched one. Only the local library is in the tree; there
are no `yt://` results here.

Leading, trailing and duplicated slashes are trimmed, and backslashes are read as separators. The
answer never touches the filesystem — the path is matched against the in-memory library's stored
locations — so a path nobody has, `..` included, is an empty folder rather than an error. There is
no failure response.

## Accounts

`/Audio/Accounts/*` is served by Dom, which owns accounts and playlists. It calls no
other service and is the only part of the stack holding data that does not rebuild itself.

Everything here sends a password or a bearer token in the request body or an `Authorization`
header. **These endpoints must only be reachable over TLS.**

```
POST /Audio/Accounts/Register   {"username":"…","password":"…"}   201
POST /Audio/Accounts/Login      {"username":"…","password":"…"}   200
GET  /Audio/Accounts/Me         Authorization: Bearer …           200
POST /Audio/Accounts/Logout     Authorization: Bearer …           204
```

And, for the account holder changing their own account (every one needs the bearer token):

```
GET   /Audio/Accounts/Settings                                                  200
PATCH /Audio/Accounts/Settings           {"quality":{…},"chatName":null}        200
POST  /Audio/Accounts/SignOutEverywhere                                         200
POST  /Audio/Accounts/Password           {"current":"…","password":"…"}         200
POST  /Audio/Accounts/Rename             {"username":"…","password":"…"}        200
POST  /Audio/Accounts/Delete             {"password":"…"}                       204
```

`Register` and `Login` answer with the account and a token:

```json
{"username":"Радост","token":"KZ8m…","expiresUtc":"2026-10-04T18:22:41.9+00:00"}
```

The token is 32 random bytes, base64url. Send it as `Authorization: Bearer <token>`. It lasts 30
days from its last use: any authenticated request slides the expiry forward, so an account in regular
use is never signed out mid-session. The slide is recorded at most once a day per token, so the
expiry a client reads may trail the real one by up to a day. `Logout` revokes one token — the other
devices signed in to the same account stay signed in — and answers `204` whether or not the token was
still live.

`Me` returns `{"username":"Радост","createdUtc":"…","expiresUtc":"…"}`. Because the expiry slides, the
`expiresUtc` a client kept from `Login` only understates it; `Me` is where the current one is read.

A username is 2–32 characters with no whitespace and no control characters; any script is accepted,
so `Радост` and `ラジオ` are ordinary usernames. Two accounts may not differ only by case, and
`Login` matches case-insensitively. A password is 8–256 characters.

Passwords are stored as PBKDF2-SHA256, 210 000 iterations, per-user 16-byte salt, with the
iteration count recorded per user so it can be raised later without a migration. A client may hash
before sending, but that is not a security boundary and the server does not assume it: whatever
arrives is treated as the secret and hashed again on arrival.

Errors use the same envelope as the rest of the API:

| Status | `code` | When |
| --- | --- | --- |
| 400 | `invalid_request` | the username or password breaks a rule above; the message says which |
| 409 | `username_taken` | that name, case-insensitively, already exists |
| 401 | `invalid_credentials` | wrong password **or** no such account — deliberately the same answer to both |
| 401 | `unauthorized` | the endpoint needs a bearer token and did not get a live one |
| 403 | `invalid_credentials` | `Password`, `Rename` or `Delete` got the wrong current password |

```json
{"error":{"code":"username_taken","message":"That username is taken. Pick another."}}
```

Nothing rate-limits `Register` or `Login`.

### Settings

The frontend's preferences, kept on the account so every device signed in to it reads the same
ones. Dom does not know what any of them mean: it stores whatever JSON object the client sends, and
the client validates every value it reads back. A new preference is a frontend change only. There is
no size cap beyond the request body limit.

`GET` answers `{"settings":{…},"updatedUtc":"…"}`, or `{"settings":null,"updatedUtc":null}` for an
account that has never saved any — which is not the same as `{}`, and the frontend uses the
difference to fill a new account from the device that signed in.

`PATCH` merges: each top-level key sent replaces that key, a key sent as `null` is removed, and keys
not sent are left alone. So a tab left open for hours overwrites only what it changed, not what
another device changed since. It answers with the merged settings, in the same shape as `GET`. A
body that is not a JSON object is `400 invalid_request`.

What the frontend keeps there today — each key is optional:

```json
{"quality":{"codec":"Opus","bitrate":192},"chatName":"Радост","lyricsOpen":true}
```

### Changing the account

`Password`, `Rename` and `Delete` ask for the current password as well as the token, so a stolen
token alone cannot lock the owner out. A wrong one is `403 invalid_credentials` — not `401`, because
the token is fine, and a client treats `401` as "you are signed out".

- `Password` revokes every token the account holds, the caller's included — each was issued against
  the old password — and answers with a fresh session for the caller, in the shape `Login` uses.
- `SignOutEverywhere` revokes every token but the caller's and answers `{"revoked":3}`, the number of
  live sessions it ended. It needs no password: it only takes access away.
- `Rename` follows the username rules above (`400`, or `409 username_taken`), carries the account's
  playlists across, and answers `{"username":"…"}`. Existing tokens stay valid.
- `Delete` removes the account, its playlists and their covers, and answers `204`. It is a `POST`
  because it carries a body.

## Playlists

`/Audio/Playlists/*` is served by Dom as well. A playlist is a named, ordered list of tracks that
belongs to one account and is either public or not. The tracks are **snapshots** taken when they
were saved, not references — a playlist renders without a single call to the rest of the stack, and
a track retagged in the library keeps the name it was saved under.

```
GET    /Audio/Playlists/Public                                        200
GET    /Audio/Playlists/Mine       Authorization: Bearer …            200
GET    /Audio/Playlists/{id}       Authorization: Bearer … (optional) 200
POST   /Audio/Playlists            Bearer  {"name":"…","isPublic":false,"tracks":[…]}  201
PATCH  /Audio/Playlists/{id}       Bearer  {"name":"…"} / {"isPublic":true} / {"tracks":[…]}  200
POST   /Audio/Playlists/{id}/Tracks Bearer {"id":"…","name":"…","artist":"…",…}      200
DELETE /Audio/Playlists/{id}       Bearer                             204
```

`Public` and `Mine` return summaries, newest change first:

```json
{
  "id": "p_9f31a04c7b2e5d18", "name": "Late shift", "owner": "kris", "isPublic": true,
  "trackCount": 14, "duration": "00:51:07",
  "coverUrl": "/Audio/Playlists/p_9f31a04c7b2e5d18/Cover",
  "firstTrackId": "local://…", "firstTrackThumbnailUrl": "https://…",
  "createdUtc": "…", "updatedUtc": "…"
}
```

`coverUrl` is `null` until somebody uploads one, and is a **path, not an absolute URL**: inside the
Discord activity the API is reachable only under the frame's own `/.proxy` prefix, so the client is
the one that knows its own base. `firstTrackThumbnailUrl` is `null` on an empty playlist. Artwork
falls back first to the first track's thumbnail and then to the client's own "no artwork" image.

`GET /Audio/Playlists/{id}` returns the same fields plus `tracks`:

```json
{"id":"…","tracks":[{"id":"local://…","name":"…","artist":"…","album":null,"duration":"00:03:41","thumbnailUrl":"https://…"}]}
```

The bearer token is optional on that one: a public playlist is a link that works for anybody. A
playlist you may not see answers `404`, never `403` — a 403 would confirm it exists. The same is
true of one you do not own on `PATCH` and `DELETE`.

On `PATCH`, a field that is absent is a field left alone; `{"isPublic":true}` changes visibility and
nothing else. `tracks`, when sent, replaces the list — reordering and removing are both a `PATCH`
with the list you want.

`POST /Audio/Playlists/{id}/Tracks` puts one track on the end without the client reading the list
first. The body is one track, in the same shape as an entry in `tracks`. A track whose `id` is
already in the playlist is not added a second time; `added` says which happened, and `playlist` is
the summary as it now stands:

```json
{"added":true,"playlist":{"id":"p_9f31a04c7b2e5d18","name":"Late shift","trackCount":15,…}}
```

It answers `404` for a playlist you do not own, like `PATCH`.

### Covers

```
PUT /Audio/Playlists/{id}/Cover   Bearer, multipart/form-data   200 {"coverUrl":"…"}
GET /Audio/Playlists/{id}/Cover                                 image, or 404
```

`PUT` takes one file — PNG, JPEG or WebP, at most 2 MB — under any field name, and replaces
whatever cover the playlist had. Only the owner may send one. `GET` serves it from Dom's own origin
with `Cache-Control: public, max-age=604800`, which is why a client that has just replaced a cover
should cache-bust with the playlist's `updatedUtc`. A private playlist's cover is as private as the
playlist: no cover, no playlist, and one you may not see are all `404`. Deleting a playlist deletes
its cover.

A name is 1–80 characters. Every track needs an `id` and a `name`; `duration` is a `hh:mm:ss`
`TimeSpan` string like everywhere else in this API, and anything unparseable is stored as zero. Bad
input answers `400 invalid_request` with a message that says which rule; a missing or dead token
answers `401 unauthorized`.

## Audio downloads and CORS

`GET /Audio/Download/{codec}/{bitrate}?id={id}` streams `Opus`, `Vorbis`, `FLAC`, `MP3`, or `AAC`; the frontend default is `/Audio/Download/Opus/112`. Asking for `FLAC` also changes what is fetched upstream: a platform with a choice of source qualities (Deezer, which otherwise downloads MP3 320) is told to fetch its lossless copy, since encoding FLAC out of a lossy source is a bigger file that sounds no better. `DownloadRaw` names no codec and takes whatever that platform already holds. The stream advertises and supports standard HTTP `Range` requests; a seek request waits for the first cached encode to finish, then receives a normal `206` byte-range response. `DownloadRaw` is used by `contentUrl` and sends an attachment filename.

`GET /Audio/Preload/{codec}/{bitrate}?id={id}` starts that same encode without a body, so a Download that follows finds it already running. `202` means this call started it, `200` that it was already under way — repeats are cheap and only push the cache entry's expiry back. The frontend calls it 20 s before a track ends and when the skip button is hovered.

Preload is not just a nicety: it moves the encode for the next track into the current track's playtime, which is what stops a multiplayer room from stampeding the encoder every time everyone advances at once.

`Download`, `DownloadRaw` and `Preload` are served by the caching tier, which coalesces concurrent requests for the same `codec`/`bitrate`/`id` into a single encode and serves byte-range requests once an encode has finished. Range requests on an encode still in progress are answered `200` with `Accept-Ranges: none`; a completed one advertises `bytes` and answers `206`.

CORS allows the deployed `example.com` frontend (including subdomains) and `localhost`, `127.0.0.1`, and `::1` Vite origins at any port. Additional exact origins may be configured with `Cors:AllowedOrigins`. Production absolute links come from `PublicApiBaseUrl` (example: `https://api.example.com`).

## `/Admin/*` — not part of this API

Every service in the stack also answers `GET /Admin/snapshot`, `GET /Admin/requests`,
`GET /Admin/events` and a handful of service-specific `POST /Admin/*` actions. **No frontend should
ever call these.** They are the admin panel's contract, not the public one:

- they answer `404` to anyone without the `X-Admin-Token` header, so they are invisible rather than
  merely refused, and they are not mapped at all when `ADMIN_TOKEN` is unset;
- nginx does not route to them — no `/Admin` prefix appears in `nginx.example.conf`, and the
  services publish only on `127.0.0.1`;
- they expose and edit things this API deliberately does not: every account in Dom, every room in
  Selo, the cache Dunav holds, and the names and albums in the local library.

The contract, the reasoning and the shape of each service's payload are in
[ADMIN_PLAN.md](ADMIN_PLAN.md).
