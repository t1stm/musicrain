# Gaida.Pods.Deezer

The Deezer pod — `gaida-deezer` in [compose.yaml](../../compose.yaml). The other Python pod, and the only one that both finds tracks and owns their audio: `/content` hands back a real stream, so a `deezer://` ID reaches the client and plays from Deezer rather than being looked up again on YouTube.

Metadata comes from Deezer's public REST API and needs nothing. Audio needs `DEEZER_ARL`, an account cookie. Without one every route still works except `/content`, and `DEEZER_RESOLVE=true` in compose tells Gaida.API to treat the pod as metadata-only — which is also what a fresh checkout runs on.

MP3 320 is what every download asks for. FLAC happens only when the caller says so — the listener picked the FLAC codec, or an operator promoted one track in Oko.

| Variable | What it does |
| --- | --- |
| `DEEZER_ARL` | Account cookie. Unset, the pod is metadata-only. |
| `DEEZER_CACHE` | Where downloaded audio lives. |
| `DEEZER_CACHE_MAX_BYTES` | Disk budget, default 20 GiB — roughly 2000 tracks at MP3 320 or 500 at FLAC. |
| `DEEZER_SEARCH_LIMIT` / `DEEZER_PLAYLIST_LIMIT` / `DEEZER_ALBUM_TRACK_LIMIT` | Page sizes, default 15 / 1000 / 200. |
| `STIH_URL` | Where downloaded lyrics are registered. Unset, this pod keeps its words to itself. |

## Running it

```bash
docker compose up gaida-deezer       # from Backend/
```

Or on the host:

```bash
pip install -r requirements.txt
DEEZER_CACHE=./cache uvicorn main:app --port 8084
pytest                               # test_deezer.py, pytest installed separately
```

> [!NOTE]
> With no `DEEZER_ARL` the pod runs metadata-only and `/content` is the only route that stops working. Leave `DEEZER_RESOLVE=true` in that case, so Gaida.API looks its hits up elsewhere instead of handing clients a `deezer://` ID it cannot play.

## Interesting techniques

- **Blowfish decryption in strides.** Deezer encrypts the first 2048 bytes of every 6144 and leaves the rest alone, with a per-track key derived from an MD5 of the track ID. [stream.py](stream.py) decrypts chunk by chunk as bytes arrive, so nothing is buffered whole.
- **Tagged before it is renamed into place.** Deezer's CDN serves a bare stream — no ID3 header, no Vorbis comment, no picture — so [tags.py](tags.py) writes the names, the cover and the lyrics into the file while it is still a `.part`, using streamrip's own mapping. That is what makes an imported track arrive in the music library with its own artwork instead of the copy the importer fetches as a fallback, and it is why the cache measures an entry's size off the disk rather than from the downloaded bytes: a cover is 100 KB of the budget.
- **Lyrics are handed over, not stored.** Deezer's gateway gives up a track's words for free after each download, and they are the best answer anyone in this stack will get for that recording — matched to it by Deezer's own track ID rather than by name. So the plain block goes into the file's tags, where it is part of the file's quality, and both halves are posted to `stih`'s `/register`, which owns every lyrics file and indexes them. The POST is fire-and-forget on a 5 second timeout: a download that succeeded must not fail because the lyrics service is restarting, and the next play registers the same words again. Nothing is pulled per listener — there is no `GET /lyrics` here, because asking Deezer's gateway about tracks nobody is listening to is exactly the traffic an ARL gets flagged for. The consequence worth stating: a Deezer track nobody has ever played has no Deezer lyrics anywhere in the stack, and `stih` answers it from LRCLIB.
- **Vendored rather than depended on.** The download and decrypt logic comes from [streamrip](https://github.com/nathom/streamrip) (GPL-3.0) — four functions, copied with their original names recorded in the module docstring. Depending on the package would pull in rich, click, Pillow, m3u8, appdirs and a TOML config object for a CLI nothing here runs.
- **Blocking client, async server.** `deezer-py` is a `requests` wrapper, so every call to it runs through `asyncio.to_thread` and the event loop keeps serving cache hits while a download is in flight.
- **A self-describing cache.** [cache.py](cache.py) writes two files per track in one flat directory — `<id>.mp3` or `<id>.flac` beside `<id>.json`. The sidecar is what lets Oko's table and the local pod's import read a cached track's name, artist and format without asking Deezer again, and a restarted pod rebuilds its whole index from one directory scan.
- **A disk budget, not a memory one.** The cache is sized against free space on its volume, and the oldest entries go first when the ceiling is reached. Losing the whole thing costs re-downloads and nothing else — promoted tracks live in the music library instead.
- **Every route degrades to "found nothing".** Deezer's gateway half is unofficial, so a change at their end leaves routes up and empty rather than failing the request.

## Technologies worth a look

- [FastAPI](https://fastapi.tiangolo.com/) and [Uvicorn](https://www.uvicorn.org/)
- [deezer-py](https://gitlab.com/RemixDev/deezer-py) — the public REST API and the internal gateway in one client, pinned to 1.3.6 to match what streamrip pins
- [mutagen](https://mutagen.readthedocs.io/) for the tags — ID3 frames on the MP3, Vorbis comments and a picture block on the FLAC, and the package streamrip tags with
- [pycryptodomex](https://www.pycryptodome.org/) for Blowfish and AES. `pycryptodomex` rather than `pycryptodome` because the import path is `Cryptodome.*`, which is what streamrip's code expects
- [streamrip](https://github.com/nathom/streamrip), the source of the download logic and of the tag mapping

## Project structure

The pod is flat: [main.py](main.py) for the routes, [stream.py](stream.py) for downloading, decrypting and fetching the artwork and lyrics, [cache.py](cache.py) for the on-disk cache, [tags.py](tags.py) for writing them into the file, [classify.py](classify.py) for recognising Deezer URLs and IDs, [mapper.py](mapper.py) for the shared DTO, [admin.py](admin.py) for the admin surface, [test_deezer.py](test_deezer.py) for the tests, and a [Dockerfile](Dockerfile).

Finding an ARL cookie is documented in [streamrip's wiki](https://github.com/nathom/streamrip/wiki/Finding-Your-Deezer-ARL-Cookie), which is also where this pod's download logic comes from.

## License

This pod is licensed [GPL-3.0-only](LICENSE), unlike the rest of the repository, which is public domain under the Unlicense. The download and decrypt logic in [stream.py](stream.py) and the tag mapping in [tags.py](tags.py) are vendored from [streamrip](https://github.com/nathom/streamrip) by nathom, which is GPL-3.0-only; the original function names are recorded in each module's docstring. The pod runs as its own container and speaks to the rest of the stack over HTTP, so the copyleft ends at this directory.
