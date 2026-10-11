# Gaida.Pods.Spotify

The Spotify pod — `gaida-spotify` in [compose.yaml](../../compose.yaml). One of the two Python pods, and a metadata-only one: Spotify hands out names, never audio.

That shapes the whole pod. There is no `/content` route and no `contentUrl` in its DTO, and `/random` is a `404` — a random Spotify track is a random resolve, and the pods that own audio answer that question directly. Every hit it returns is a name for [Gaida.API](../../Services/Gaida.API)'s resolver to look up against the library, Deezer or YouTube, so a `spotify://` ID never reaches a client.

It needs no credentials at all. [SpotAPI](https://github.com/Aran404/SpotAPI) reaches Spotify's own web endpoints, so there is no client ID, no secret, no premium account, and nothing secret to put in `.env`. `SPOTIFY_SEARCH_LIMIT` (default 15) is the only knob.

Its one piece of state is a search cache. Every track a search, playlist or resolve hands back is a row in `cache.db` on the `/cache` volume (`SPOTIFY_CACHE_PATH`, default `data/spotify-cache`), first seen wins, so a later `/resolve` of it costs no Spotify call. It keeps the song data only, never the query or the playlist. Oko pages through it, searches it, and can forget a row.

## Running it

```bash
docker compose up gaida-spotify      # from Backend/
```

Or on the host:

```bash
pip install -r requirements.txt
SPOTIFY_CACHE=./cache uvicorn main:app --port 8083
pytest                               # test_classify.py and test_cache.py, pytest installed separately
```

## Interesting techniques

- **A search limit that is a budget, not a page size.** Each hit costs Gaida.API a resolve against another platform, so the default is 15 where Spotify's own first page is 100.
- **Every route degrades to "found nothing".** SpotAPI is unofficial, so a change at Spotify's end has to leave the route up and empty rather than returning a 500 that a client cannot act on.
- **Results streamed as they arrive.** Search and playlist responses are `StreamingResponse`s writing one JSON object at a time, which is what lets the frontend render a row before the last hit lands. Same wire shape as the .NET pods' `IAsyncEnumerable<T>`.
- **Pinned unofficial dependencies, with the reason in the file.** [requirements.txt](requirements.txt) pins `spotapi` because an upgrade to an unofficial client is a change to test rather than take, and pins `pymongo`, `redis` and `websockets` because SpotAPI imports all three at module load and declares none of them — without them the install fails on `import spotapi` rather than on first use. Nothing here talks to Mongo or Redis.
- **FastAPI with its docs turned off.** The app is constructed with `docs_url=None, redoc_url=None, openapi_url=None`: this is an internal pod on a container network, and its contract is [API.md](../../API.md).

## Technologies worth a look

- [FastAPI](https://fastapi.tiangolo.com/) and [Uvicorn](https://www.uvicorn.org/)
- [SpotAPI](https://github.com/Aran404/SpotAPI) — Spotify's own web endpoints, no key and no account
- [Starlette](https://www.starlette.io/) `StreamingResponse`, underneath FastAPI

## Project structure

The pod is flat: [main.py](main.py) for the routes, [classify.py](classify.py) for recognising Spotify URLs and IDs, [mapper.py](mapper.py) for turning SpotAPI's shapes into the shared DTO, [cache.py](cache.py) for the search cache, [admin.py](admin.py) for the admin surface, [test_classify.py](test_classify.py) and [test_cache.py](test_cache.py) for the tests, and a [Dockerfile](Dockerfile).
