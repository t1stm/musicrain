"""The lyrics studio's back end: textove's words for a library song, map mode on an edited text, and saving.

    uv run studio.py          # then open /studio in the frontend's dev server (npm run dev)

Local only: it binds 127.0.0.1, answers only browser pages served from localhost, and resolves every
track through library.db, so no request can name a file outside the music library. The page is the
frontend's /studio route, which exists in dev builds only; it plays the song through the real player and
shows the draft in the real lyrics pane.
"""

from __future__ import annotations

import json
import multiprocessing
import os
import re
import sqlite3
import threading
import time
from concurrent.futures import ProcessPoolExecutor
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

import sources
import lirika

PORT = 8765
EDITED = lirika.CACHE / "edited"
LIBRARY = Path(sources.backend_env()["MUSIC_LIBRARY_PATH"]).resolve()
LOCAL_ORIGIN = re.compile(r"^http://(localhost|127\.0\.0\.1)(:\d+)?$")

# The models live in a worker process that exits after IDLE seconds without a request. ROCm's runtime spins
# one CPU core (rocr::core::Runtime::AsyncEventsLoop) for as long as a process has the GPU open, idle or
# not, and the models hold ~8 GB of VRAM; neither should stay while nobody is editing. This process never
# touches the GPU.
IDLE = float(os.environ.get("LIRIKA_STUDIO_IDLE", 300))
gpu = threading.Lock()  # one generation at a time: the models share one GPU
_pool: ProcessPoolExecutor | None = None
_last = 0.0
_models = None  # the worker's own


def song_of(track_id: str) -> Path:
    """An audio:// ID to its file, through library.db only."""
    if not track_id.startswith("audio://"):
        raise LookupError("Only library tracks (audio://) have files to work on.")
    db = sqlite3.connect(f"file:{LIBRARY / 'library.db'}?mode=ro", uri=True)
    try:
        row = db.execute("SELECT RelativeLocation FROM songs WHERE Id = ?", (track_id.removeprefix("audio://"),)).fetchone()
    finally:
        db.close()
    if not row:
        raise LookupError(f"{track_id} is not in library.db.")
    song = (LIBRARY / row[0]).resolve()
    if not song.is_relative_to(LIBRARY) or not song.is_file():
        raise LookupError(f"{track_id} has no file in the library.")
    return song


def texts(track_id: str) -> dict:
    """Every text the words could start from, the user's own last edit first."""
    song = song_of(track_id)
    candidates = []
    if (edited := EDITED / f"{song.stem}.txt").exists():
        candidates.append({"source": "edited", "label": "Your last saved edit", "text": edited.read_text(encoding="utf-8")})
    if (beside := song.with_suffix(".txt")).exists():
        candidates.append({"source": "txt", "label": "The .txt beside the audio",
                           "text": beside.read_text(encoding="utf-8", errors="replace")})
    for found in sources.textove(sources.track_of(song), log=lambda message: None):
        candidates.append({"source": "textove", "label": f"textove: {found.artist} - {found.title}", "text": found.text,
                           "url": found.url})
    saved = EDITED / f"{song.stem}.lrc"
    return {"song": song.stem, "candidates": candidates,
            "saved": saved.read_text(encoding="utf-8") if saved.exists() else None}


def from_link(link: str) -> dict:
    """The words on one textove page the user found by hand, when the search missed it under another name."""
    if not re.fullmatch(r"https://(www\.)?textove\.com/[\w.-]+", link.strip()):
        raise LookupError("Only a textove.com song page, e.g. https://textove.com/artist-song-tekst.")
    words = sources.textove_words(sources.fetch(link.strip(), log=lambda message: None) or "")
    if not words:
        raise LookupError("That page has no lyrics block, or textove did not answer.")
    return {"source": "textove", "label": f"textove: {link.strip().rsplit('/', 1)[-1]}", "text": words,
            "url": link.strip()}


def _generate_in_worker(song: str, text: str, by: str | None) -> dict:
    """Runs in the worker process, where the models are loaded once and kept until it exits."""
    global _models
    if _models is None:
        _models = lirika.Models()
    messages: list[str] = []
    result = lirika.generate(Path(song), lirika.LANGUAGES["bg"], _models, text=text, origin="edited",
                             log=messages.append, by=by)
    return {"mode": result.mode, "lrc": result.lrc, "songFlags": result.flags, "log": messages,
            "lines": [{"start": round(line.start, 2), "end": round(line.end, 2), "text": lirika.tidy(line.text),
                       "flags": line.flags} for line in result.lines]}


def generate(track_id: str, text: str, by: str | None = None) -> dict:
    global _pool, _last
    song = song_of(track_id)
    with gpu:
        if _pool is None:
            _pool = ProcessPoolExecutor(1, mp_context=multiprocessing.get_context("spawn"))
        try:
            return _pool.submit(_generate_in_worker, str(song), text, by).result()
        finally:
            _last = time.monotonic()


def _reap() -> None:
    """Ends an idle worker, and with it the spinning ROCm thread and the VRAM."""
    global _pool
    while True:
        time.sleep(15)
        if _pool is not None and time.monotonic() - _last > IDLE and gpu.acquire(blocking=False):
            try:
                _pool.shutdown(wait=True)
                _pool = None
                print(f"studio: models unloaded after {IDLE:.0f} s idle", flush=True)
            finally:
                gpu.release()


def save(track_id: str, lrc: str, text: str) -> dict:
    song = song_of(track_id)
    EDITED.mkdir(parents=True, exist_ok=True)
    (EDITED / f"{song.stem}.lrc").write_text(lrc, encoding="utf-8")
    (EDITED / f"{song.stem}.txt").write_text(text, encoding="utf-8")
    return {"lrc": str(EDITED / f"{song.stem}.lrc"), "text": str(EDITED / f"{song.stem}.txt")}


class Handler(BaseHTTPRequestHandler):
    def do_OPTIONS(self) -> None:  # the CORS preflight a JSON POST sends first
        self._send(HTTPStatus.NO_CONTENT, None)

    def do_GET(self) -> None:
        url = urlsplit(self.path)
        query = {key: values[0] for key, values in parse_qs(url.query).items()}
        if url.path == "/health":
            self._answer(lambda: {"ok": True})
        elif url.path == "/text":
            self._answer(lambda: texts(query.get("id", "")))
        elif url.path == "/link":
            self._answer(lambda: from_link(query.get("url", "")))
        else:
            self._send(HTTPStatus.NOT_FOUND, {"error": "No such route."})

    def do_POST(self) -> None:
        try:
            body = json.loads(self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"{}")
        except json.JSONDecodeError:
            return self._send(HTTPStatus.BAD_REQUEST, {"error": "The body is not JSON."})
        path = urlsplit(self.path).path
        if path == "/generate":
            self._answer(lambda: generate(body.get("id", ""), body.get("text", ""), body.get("by") or None))
        elif path == "/save":
            self._answer(lambda: save(body.get("id", ""), body.get("lrc", ""), body.get("text", "")))
        else:
            self._send(HTTPStatus.NOT_FOUND, {"error": "No such route."})

    def _answer(self, work) -> None:
        try:
            self._send(HTTPStatus.OK, work())
        except LookupError as error:
            self._send(HTTPStatus.NOT_FOUND, {"error": str(error)})
        except ValueError as error:  # normalize() refusing an unresolvable repeat in the text
            self._send(HTTPStatus.BAD_REQUEST, {"error": str(error)})

    def _send(self, status: HTTPStatus, payload) -> None:
        self.send_response(status)
        origin = self.headers.get("Origin", "")
        if LOCAL_ORIGIN.match(origin):
            self.send_header("Access-Control-Allow-Origin", origin)
            self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
            self.send_header("Access-Control-Allow-Headers", "Content-Type")
            self.send_header("Access-Control-Allow-Private-Network", "true")
        body = b"" if payload is None else json.dumps(payload, ensure_ascii=False).encode()
        if payload is not None:
            self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format: str, *args) -> None:
        print(f"studio: {format % args}", flush=True)


if __name__ == "__main__":
    print(f"lirika studio on http://127.0.0.1:{PORT}; open /studio in the frontend's dev server", flush=True)
    threading.Thread(target=_reap, daemon=True).start()
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
