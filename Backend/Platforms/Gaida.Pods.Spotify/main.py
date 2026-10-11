"""
The Spotify platform pod.

Spotify hands out metadata, never audio: every result here is a name for Gaida.API's resolver to look up
against the library or YouTube, which is why there is no ``/content`` route and no ``contentUrl`` in the
DTO. ``/random`` is 404 too -- a random Spotify track is a random resolve, and the pods that own audio
answer that question directly.

Backed by SpotAPI (https://github.com/Aran404/SpotAPI), which reaches Spotify's own web endpoints and
needs no client ID, no secret and no premium account. It is also unofficial: when Spotify changes those
endpoints the calls below start failing, and every one of them degrades to "found nothing" rather than
taking the route down.

Every track it hands back is remembered in :mod:`cache`, so a later ``/resolve`` of it is answered
without asking Spotify again. That cache is the pod's only state.
"""

import json
import logging
import os
from typing import Any, Iterator

from fastapi import APIRouter, FastAPI, Response
from fastapi.responses import JSONResponse
from starlette.responses import StreamingResponse
from spotapi import Public

import admin
import cache
import classify
from mapper import to_dto

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(name)s: %(message)s")
log = logging.getLogger("gaida.spotify")

SEARCH_LIMIT = int(os.environ.get("SPOTIFY_SEARCH_LIMIT") or 15)
"""
How many search hits to hand back. Each one costs Gaida.API a resolve against the library or YouTube, so
this is a budget rather than a page size -- Spotify's own first page is 100.
"""

app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)

songs = cache.Results(os.environ.get("SPOTIFY_CACHE") or "/cache")


@app.get("/classify")
def classify_query(query: str | None = None) -> Response:
    result = classify.parse(query)
    if result.status == 200:
        return JSONResponse({"kind": result.kind, "id": result.id, "error": None})
    if result.status == 400:
        return JSONResponse({"kind": None, "id": None, "error": result.error}, status_code=400)

    return Response(status_code=404)


@app.get("/resolve")
def resolve(id: str | None = None) -> Response:
    track_id = classify.track_id(id or "")
    if not track_id:
        return Response(status_code=404)

    remembered = songs.get(track_id)
    if remembered is not None:
        return JSONResponse(remembered)

    track = _ask(lambda: Public.song_info(track_id), f"track {track_id}")
    dto = to_dto(((track or {}).get("data") or {}).get("trackUnion"))
    if not dto:
        return Response(status_code=404)

    songs.remember([dto])
    return JSONResponse(dto)


@app.get("/playlist")
def playlist(url: str | None = None) -> Response:
    playlist_id = classify.playlist_id(url or "")
    if not playlist_id:
        return JSONResponse([])

    # Streamed rather than assembled: Spotify pages a playlist a few hundred at a time and every track
    # then needs its own lookup upstream, so the sooner the first one leaves, the sooner that starts.
    return StreamingResponse(_json_array(_playlist_tracks(playlist_id)), media_type="application/json")


@app.get("/search")
def search(q: str | None = None) -> Response:
    query = (q or "").strip()
    if not query:
        return JSONResponse([])

    dtos = [dto for dto in (to_dto((item.get("item") or {}).get("data")) for item in _search_hits(query)) if dto]
    songs.remember(dtos)
    return JSONResponse(dtos)


# Spotify has no audio and nothing to pick from at random. HttpPlatform reads both 404s as
# "route not supported" and moves on.
@app.get("/random")
@app.get("/content")
def unsupported() -> Response:
    return Response(status_code=404)


# ── the admin surface ───────────────────────────────────────────────────────────────────────────
# Everything here is gone without ADMIN_TOKEN, along with the rest of /Admin. See ADMIN_PLAN.md and
# CACHE_BROWSER_PLAN.md. Read-only but for the one delete.

operations = APIRouter()


@operations.get("/results")
def browse_results(q: str | None = None, offset: int = 0, take: int = 100, top: int | None = None) -> Response:
    """One page of the remembered tracks, newest first. A GET, so Oko reads it without auditing it."""
    return JSONResponse(songs.browse(q, offset, take, top))


@operations.post("/forget")
def forget(id: str | None = None) -> Response:
    track_id = classify.track_id(id or "")
    if not track_id:
        return JSONResponse({"error": "That is not a Spotify track ID."}, status_code=400)

    return JSONResponse({"forgotten": 1 if songs.forget(track_id) else 0})


admin.install(app, lambda: {"service": "gaida-spotify", "searchLimit": SEARCH_LIMIT, "results": songs.count()},
              operations)


def _search_hits(query: str) -> list[dict[str, Any]]:
    """
    The first page of song hits, trimmed to :data:`SEARCH_LIMIT`.

    SpotAPI's generator pages 100 at a time, so the first ``next`` is exactly one request; closing it
    there returns the pooled client without asking for a second page nobody wants.
    """
    hits = _ask(lambda: next(iter(Public.song_search(query)), []), f"search {query!r}")
    return list(hits or [])[:SEARCH_LIMIT]


def _playlist_tracks(playlist_id: str) -> Iterator[dict[str, Any]]:
    """Playlist entries as Spotify pages them, mapped and with everything that is not a track dropped."""
    pages = _ask(lambda: Public.playlist_info(playlist_id), f"playlist {playlist_id}")
    if pages is None:
        return

    try:
        for page in pages:
            # Remembered a page at a time, before any of it is sent: a client that hangs up halfway
            # still leaves every page it was handed in the cache.
            dtos = [dto for dto in (to_dto((item.get("itemV2") or {}).get("data"))
                                    for item in page.get("items") or []) if dto]
            songs.remember(dtos)
            yield from dtos
    except Exception:
        # Mid-stream: the entries already written stay written, which is the same contract the .NET
        # pods' Guarded() gives -- a short playlist rather than a torn response.
        log.warning("Spotify stopped paging playlist %s", playlist_id, exc_info=True)


def _json_array(items: Iterator[dict[str, Any]]) -> Iterator[bytes]:
    yield b"["
    for index, item in enumerate(items):
        yield (b"," if index else b"") + json.dumps(item).encode()
    yield b"]"


def _ask(call, what: str):
    """Runs one SpotAPI call, turning a refusal into ``None`` and a log line that names what was asked."""
    try:
        return call()
    except Exception as error:
        log.warning("Spotify refused %s: %s", what, error)
        return None
