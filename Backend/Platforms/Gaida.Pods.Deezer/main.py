"""
The Deezer platform pod.

Unlike the Spotify pod next door, this one owns audio: ``/content`` hands back a real stream, so a
Deezer result is played from Deezer rather than looked up again on YouTube. Metadata comes from
Deezer's public REST API, which needs no credentials at all; the audio needs an ARL cookie from a real
account, and without one every route here still works except ``/content``.

MP3 320 is what every download asks for. FLAC happens only when the caller says so -- Gaida.API passes
``format=flac`` when the listener has picked the FLAC codec, and Oko's promote button passes it for one
track at a time. Everything that is downloaded is cached on disk (see :mod:`cache`), so the second play
of a track costs nothing and a promoted FLAC stays a FLAC.

deezer-py is the client (https://gitlab.com/RemixDev/deezer-py); the download and decrypt logic in
:mod:`stream` is vendored from streamrip. Both reach Deezer's own endpoints and are unofficial: when
Deezer changes them these calls start failing, and every one of them degrades to "found nothing"
rather than taking the route down.
"""

import asyncio
import json
import logging
import os
from typing import Any, Iterator

import requests
from fastapi import APIRouter, FastAPI, Response
from fastapi.responses import FileResponse, JSONResponse
from starlette.responses import StreamingResponse

import admin
import cache
import classify
import stream
from mapper import to_dto, with_album

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(name)s: %(message)s")
log = logging.getLogger("gaida.deezer")

SEARCH_LIMIT = int(os.environ.get("DEEZER_SEARCH_LIMIT") or 15)
"""
How many search hits to hand back. Unlike the Spotify pod's budget these are playable as they are, so
this is an ordinary page size -- Deezer's own default is 25.
"""

PLAYLIST_LIMIT = int(os.environ.get("DEEZER_PLAYLIST_LIMIT") or 1000)
"""
How much of a playlist to read. Deezer takes -1 for "all of it", but a 10,000-track editorial playlist
is not something anyone queues on purpose, and every entry costs the client a row.
"""

ALBUM_TRACK_LIMIT = int(os.environ.get("DEEZER_ALBUM_TRACK_LIMIT") or 200)
"""
How much of an album to read. A box set is the only thing that comes near this; a record is a dozen.
"""

ARTIST_LIMIT = int(os.environ.get("DEEZER_ARTIST_LIMIT") or 50)
"""
How many of an artist's top tracks to hand back -- a page of their own on the artist view, so more
than a search's worth.
"""

app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)

client = stream.Client(os.environ.get("DEEZER_ARL"))
songs = cache.Cache(
    os.environ.get("DEEZER_CACHE") or "/cache",
    int(os.environ.get("DEEZER_CACHE_MAX_BYTES") or cache.MAX_BYTES_DEFAULT),
)

STIH_URL = (os.environ.get("STIH_URL") or "").rstrip("/")
"""
Where downloaded lyrics are registered. Unset, this pod keeps its words to itself: they still go into
the file's tags, stih just falls back to LRCLIB for these tracks -- the same shape ``DEEZER_URL`` has in
gaida-local.
"""

REGISTER_TIMEOUT = 5
"""Seconds. A download that succeeded must not sit waiting on a lyrics service that is restarting."""

if not STIH_URL:
    log.info("STIH_URL is unset: downloaded lyrics go into the file's tags only, and are not registered")

ADMIN_ROWS = int(os.environ.get("DEEZER_ADMIN_ROWS") or 200)
"""
How many cached tracks the admin snapshot carries. Oko polls it every two seconds while its panel is
open, so this is a payload budget rather than a page size. 0 lifts the cap and sends every entry --
fine for a cache of a few hundred, a megabyte of JSON every two seconds for one of tens of thousands.

ponytail: the operator filters these rows in the browser. A server-side /Admin/cache?q= search is the
upgrade when the cache holds more tracks than fit here.
"""


# ── the pod contract ────────────────────────────────────────────────────────────────────────────

@app.get("/classify")
def classify_query(query: str | None = None) -> Response:
    result = classify.parse(query)
    if result.status == 200:
        return JSONResponse({"kind": result.kind, "id": result.id, "error": None})
    if result.status == 400:
        return JSONResponse({"kind": None, "id": None, "error": result.error}, status_code=400)

    return Response(status_code=404)


@app.get("/resolve")
async def resolve(id: str | None = None) -> Response:
    track_id = classify.track_id(id or "")
    if not track_id:
        return Response(status_code=404)

    # A cached track already carries everything /resolve returns, so a replay of something in the
    # cache is answered without touching Deezer at all.
    cached = songs.get(track_id)
    if cached is not None:
        return JSONResponse(cached.to_dto())

    track = await _ask(lambda: client.api.get_track(track_id), f"track {track_id}")
    dto = to_dto(track)
    return JSONResponse(dto) if dto else Response(status_code=404)


@app.get("/search")
async def search(q: str | None = None) -> Response:
    query = (q or "").strip()
    if not query:
        return JSONResponse([])

    found = await _ask(lambda: client.api.search_track(query, limit=SEARCH_LIMIT), f"search {query!r}")
    return JSONResponse(_mapped((found or {}).get("data") or []))


@app.get("/playlist")
async def playlist(url: str | None = None) -> Response:
    playlist_id = classify.playlist_id(url or "")
    if not playlist_id:
        return JSONResponse([])

    tracks = await _ask(lambda: client.api.get_playlist_tracks(playlist_id, limit=PLAYLIST_LIMIT),
                        f"playlist {playlist_id}")

    # Streamed rather than assembled, matching the Spotify pod: the client renders the first tracks
    # while the rest of the array is still being written.
    entries = _mapped((tracks or {}).get("data") or [])
    return StreamingResponse(_json_array(entries), media_type="application/json")


@app.get("/artist")
async def artist(term: str | None = None) -> Response:
    """
    An artist's top tracks, best-known first.

    Found as the artist, then their chart, rather than as a track search: a bare name also matches
    titles and other people's features, and Deezer's ``artist:"..."`` field syntax returns next to
    nothing for most names now (two hits for Eminem). An exact name wins over Deezer's first hit, so a
    term does not land on a busier namesake.
    """
    credit = (term or "").strip()
    if not credit:
        return JSONResponse([])

    found = await _ask(lambda: client.api.search_artist(credit, limit=5), f"artist {credit!r}")
    hits = (found or {}).get("data") or []
    if not hits:
        return JSONResponse([])

    chosen = next((hit for hit in hits if _same(hit.get("name"), credit)), hits[0])
    top = await _ask(lambda: client.api.get_artist_top(chosen["id"], limit=ARTIST_LIMIT),
                     f"artist top {chosen['id']}")
    return JSONResponse(_mapped((top or {}).get("data") or []))


@app.get("/album")
async def album(artist: str | None = None, album: str | None = None) -> Response:
    """
    One album's tracks, found by name.

    Gaida.API asks this only when the library has nothing -- which is the ordinary case rather than a
    rare one, since the library holds an album only when a playlist file defines it. A miss here is
    therefore the end of the line, and it is an empty array: an album this catalogue does not have is
    not a failure.
    """
    wanted, credit = (album or "").strip(), (artist or "").strip()
    if not wanted or not credit:
        return JSONResponse([])

    # A plain "artist album" query, narrowed here: Deezer's artist:"..." album:"..." field syntax, which
    # used to do the narrowing, now finds nothing even for Daft Punk's Discovery.
    query = f"{credit} {wanted}"
    found = await _ask(lambda: client.api.search_album(query, limit=10), f"album {query!r}")
    record = _album_in((found or {}).get("data") or [], credit, wanted)
    if record is None:
        log.info("Deezer has no album %r by %r", wanted, credit)
        return JSONResponse([])

    tracks = await _ask(lambda: client.api.get_album_tracks(record["id"], limit=ALBUM_TRACK_LIMIT),
                        f"album tracks {record['id']}")

    # Deezer's own order, which for an album is the running order -- the one place a pod's ordering is
    # already what the listener wants. Streamed like /playlist above.
    return StreamingResponse(_json_array(_mapped(with_album(record, (tracks or {}).get("data") or []))),
                             media_type="application/json")


@app.get("/content")
async def content(id: str | None = None, format: str | None = None) -> Response:
    """
    The audio, from the cache when it is there and from Deezer when it is not.

    ``format=flac`` is a request for quality 2 and upgrades a track already cached as MP3 -- it is the
    same path Oko's promote button takes. Any other value, and no value at all, means MP3 320: a cached
    FLAC is still served as it is, since re-downloading a worse copy of something already on disk would
    be slower *and* worse.
    """
    track_id = classify.track_id(id or "")
    if not track_id:
        return Response(status_code=404)

    entry = await _cached(track_id, prefer_flac=(format or "").lower() == stream.FLAC)
    if entry is None:
        return Response(status_code=404)

    return FileResponse(
        songs.path(entry),
        media_type=stream.CONTENT_TYPES.get(entry.format, "application/octet-stream"),
        filename=f"{track_id}.{entry.format}",
        content_disposition_type="attachment",
    )


# Deezer has nothing to offer at random that Gaida.API asks it for -- /random belongs to the pods that
# own a catalogue. HttpPlatform reads the 404 as "route not supported" and moves on.
@app.get("/random")
def unsupported() -> Response:
    return Response(status_code=404)


# ── the admin surface ───────────────────────────────────────────────────────────────────────────
# Everything here is gone without ADMIN_TOKEN, along with the rest of /Admin. See ADMIN_PLAN.md.

operations = APIRouter()


@operations.post("/promote")
async def promote(id: str | None = None) -> Response:
    """Re-fetches one track as FLAC, replacing the cached MP3. Needs an account that can stream it."""
    return await _refetch(id, prefer_flac=True)


@operations.post("/demote")
async def demote(id: str | None = None) -> Response:
    """Re-fetches one track as MP3 320, replacing the cached FLAC — the cache's own space, back."""
    return await _refetch(id, prefer_flac=False)


@operations.post("/evict")
async def evict(id: str | None = None) -> Response:
    track_id = classify.track_id(id or "")
    if not track_id:
        return JSONResponse({"error": "That is not a Deezer track ID."}, status_code=400)

    removed = await asyncio.to_thread(songs.remove, track_id)
    return JSONResponse({"evicted": 1 if removed else 0})


@operations.post("/evict-all")
async def evict_all() -> Response:
    return JSONResponse({"evicted": await asyncio.to_thread(songs.clear)})


def snapshot() -> dict[str, Any]:
    """What an operator sees. Read straight off the in-memory index — nothing here touches the disk."""
    count, total = songs.stats()
    return {
        "service": "gaida-deezer",
        # The panel says "metadata only" off this: no ARL means /content answers 404 and Gaida.API
        # resolves Deezer results against the library or YouTube instead.
        "canDownload": client.can_download,
        "searchLimit": SEARCH_LIMIT,
        "count": count,
        "totalBytes": total,
        "maxBytes": songs.max_bytes,
        "directory": str(songs.directory),
        "rowLimit": ADMIN_ROWS,
        "entries": [
            {
                "id": entry.id,
                "name": entry.name,
                "artist": entry.artist,
                "album": entry.album,
                "duration": entry.duration,
                "format": entry.format,
                "bytes": entry.bytes,
                "at": entry.at,
            }
            for entry in songs.recent(ADMIN_ROWS)
        ],
    }


admin.install(app, snapshot, operations)


# ── internals ───────────────────────────────────────────────────────────────────────────────────

async def _cached(track_id: str, prefer_flac: bool) -> cache.Entry | None:
    """
    The cache entry for a track, downloading it first when it is missing or in the wrong format.

    The lock is per track, so two listeners starting the same song download it once and the second one
    finds it cached — while every other track carries on being served in parallel.
    """
    async with songs.download_lock(track_id):
        entry = songs.get(track_id)
        if entry is not None and not (prefer_flac and entry.format != stream.FLAC):
            return entry

        return await _fetch(track_id, prefer_flac)


async def _fetch(track_id: str, prefer_flac: bool) -> cache.Entry | None:
    """One download, decrypt and cache write, off the event loop. ``None`` when Deezer said no."""
    # Checked before the metadata lookup, not inside client.download: with no ARL every /content is a
    # 404 anyway, and asking Deezer about a track nobody can stream is a request for nothing.
    if not client.can_download:
        return None

    metadata = await _ask(lambda: client.api.get_track(track_id), f"track {track_id}")
    dto = to_dto(metadata)
    if dto is None:
        return None

    try:
        downloaded = await asyncio.to_thread(client.download, track_id, prefer_flac)
    except stream.NotStreamable as refusal:
        log.warning("Deezer will not stream %s: %s", track_id, refusal)
        return None
    except Exception:
        log.warning("Downloading %s from Deezer failed", track_id, exc_info=True)
        return None

    # Both after the download, so a track Deezer refuses costs neither request. Two threads because both
    # are blocking calls -- one CDN GET, one gateway call behind the client's own lock.
    cover, lyrics = await asyncio.gather(
        asyncio.to_thread(stream.fetch_artwork, dto["thumbnailUrl"]),
        asyncio.to_thread(client.lyrics, track_id),
    )

    entry = await asyncio.to_thread(songs.store, track_id, downloaded.data, downloaded.format, dto,
                                    cover, lyrics.text)
    await _register_lyrics(track_id, lyrics)
    return entry


async def _register_lyrics(track_id: str, lyrics: stream.Lyrics) -> None:
    """
    Hands one track's words to stih, which owns every lyrics file in this stack.

    Fire and forget on purpose: a download that succeeded must not fail because the lyrics service is
    restarting, and there is nothing here worth retrying -- the next play of this track registers the
    same words again, and stih's own LRCLIB fall-through covers it in the meantime.

    Deezer's lyrics win over anything stih found on its own: they came with the audio, from the same
    source, matched to the recording by Deezer's own track ID. No title matching, no length gate, no
    chance of the wrong song's words.
    """
    if not STIH_URL or not (lyrics.text or lyrics.lrc):
        return

    body = {"id": f"deezer://{track_id}", "source": "Deezer", "text": lyrics.text, "lrc": lyrics.lrc}
    try:
        await asyncio.to_thread(
            lambda: requests.post(f"{STIH_URL}/register", json=body, timeout=REGISTER_TIMEOUT)
            .raise_for_status())
    except Exception:
        log.warning("Registering lyrics for %s with stih failed", track_id, exc_info=True)


async def _refetch(id: str | None, prefer_flac: bool) -> Response:
    """The body of promote and demote: one re-download in the named format, or the reason it failed."""
    track_id = classify.track_id(id or "")
    if not track_id:
        return JSONResponse({"error": "That is not a Deezer track ID."}, status_code=400)

    if not client.can_download:
        return JSONResponse({"error": "No DEEZER_ARL is configured, so this pod cannot download."},
                            status_code=400)

    async with songs.download_lock(track_id):
        entry = await _fetch(track_id, prefer_flac)

    if entry is None:
        wanted = "FLAC" if prefer_flac else "MP3 320"
        return JSONResponse({"error": f"Deezer would not hand over {wanted} for this track."},
                            status_code=502)

    return JSONResponse({"id": entry.id, "format": entry.format, "bytes": entry.bytes})


def _mapped(tracks: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Every entry Deezer listed that is actually a playable catalogue track, in its own order."""
    return [dto for dto in (to_dto(track) for track in tracks) if dto]


def _same(name: str | None, wanted: str) -> bool:
    return (name or "").casefold() == wanted.casefold()


def _album_in(hits: list[dict[str, Any]], artist: str, title: str) -> dict[str, Any] | None:
    """
    The record an album search meant: credited to exactly that artist, since a plain query happily
    ranks a compilation or a namesake with the right words first -- then the exact title, else the
    first whose title holds it ("Discovery (Remastered)"). None rather than a guess at another record.
    """
    theirs = [hit for hit in hits if _same((hit.get("artist") or {}).get("name"), artist)]
    return next((hit for hit in theirs if _same(hit.get("title"), title)), None) or \
        next((hit for hit in theirs if title.casefold() in (hit.get("title") or "").casefold()), None)


def _json_array(items: list[dict[str, Any]]) -> Iterator[bytes]:
    yield b"["
    for index, item in enumerate(items):
        yield (b"," if index else b"") + json.dumps(item).encode()
    yield b"]"


async def _ask(call, what: str):
    """
    Runs one deezer-py call off the event loop, turning a refusal into ``None`` and a log line that
    names what was asked. deezer-py is ``requests``, so every one of these blocks its thread.
    """
    try:
        return await asyncio.to_thread(client.call, call)
    except Exception as error:
        log.warning("Deezer refused %s: %s", what, error)
        return None
