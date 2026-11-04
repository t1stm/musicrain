"""The lyrics studio's back end: textove's words for a library song, lirika on an edited text, and saving.

    docker compose up -d lirika      # the pod, behind nginx at /Audio/Lirika (Backend/compose.yaml)
    uv run studio.py                 # on the host, for working on this file; asks the Dom at DOM_URL

Every request needs a Dom bearer token belonging to staff with the `studio` permission; Dom's /Me decides,
per request, so a revoked token or a lost permission is refused on the next one. Anyone else gets 401 or
403. It resolves every track through library.db, so no request can name a file outside the music library.
The page is musicrain's /studio, which plays the song through the real player and shows the draft in the
real lyrics pane. /generate takes the song's settings (KNOBS) and answers one JSON object per line as it goes:
each {"stage"}, then a {"result"} or an {"error"}.
"""

from __future__ import annotations

import json
import multiprocessing
import os
import re
import sqlite3
import threading
import time
import traceback
from collections.abc import Iterator
from concurrent.futures import ProcessPoolExecutor
from concurrent.futures.process import BrokenProcessPool
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

import requests

import sources
import lirika

HOST = os.environ.get("LIRIKA_STUDIO_HOST", "127.0.0.1")
PORT = int(os.environ.get("LIRIKA_STUDIO_PORT", 8765))
EDITED = lirika.CACHE / "edited"
# Inside the container there is no Backend/.env: compose hands over MUSIC_LIBRARY and DOM_URL instead.
LIBRARY = Path(os.environ.get("MUSIC_LIBRARY") or sources.backend_env().get("MUSIC_LIBRARY_PATH", "/nonexistent")).resolve()
DOM = os.environ.get("DOM_URL") or f"http://127.0.0.1:{sources.backend_env().get('DOM_PORT', '5343')}"

# The models live in a worker process that exits after IDLE seconds without a request. ROCm's runtime spins
# one CPU core (rocr::core::Runtime::AsyncEventsLoop) for as long as a process has the GPU open, idle or
# not, and the models hold ~8 GB of VRAM; neither should stay while nobody is editing. This process never
# touches the GPU.
IDLE = float(os.environ.get("LIRIKA_STUDIO_IDLE", 300))
gpu = threading.Lock()  # one generation at a time: the models share one GPU
_pool: ProcessPoolExecutor | None = None
_last = 0.0
_stages = None  # the worker's stage labels, on their way here; both processes hold it
_models = None  # the worker's own


def who(headers) -> tuple[HTTPStatus, str, str] | None:
    """None when the caller is staff with `studio`; otherwise the status, code and message to refuse with.
    Asks Dom every time, as Oko does: a revoked token or a lost permission is refused on the next request."""
    authorization = headers.get("Authorization", "")
    if not authorization.lower().startswith("bearer "):
        return HTTPStatus.UNAUTHORIZED, "unauthorized", "Sign in first."
    try:
        answer = requests.get(f"{DOM}/Audio/Accounts/Me", headers={"Authorization": authorization}, timeout=5)
        if answer.status_code == HTTPStatus.UNAUTHORIZED:
            return HTTPStatus.UNAUTHORIZED, "unauthorized", "Sign in again."
        if not answer.ok:
            raise requests.RequestException(f"Dom answered {answer.status_code}")
        permissions = answer.json().get("permissions") or []
    except (requests.RequestException, ValueError, AttributeError):
        return HTTPStatus.SERVICE_UNAVAILABLE, "unavailable", "Accounts cannot be checked right now."
    if "studio" not in permissions:
        return HTTPStatus.FORBIDDEN, "forbidden", "The studio needs the studio permission."
    return None


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


def stems(track_id: str) -> dict:
    """The stems already separated for a track. Regenerate separates the ones its settings use."""
    song = song_of(track_id)
    return {"stems": [kind for kind, model in lirika.STEMS.items() if lirika.stem_file(song, model).exists()]}


def stem_of(track_id: str, kind: str) -> Path:
    if kind not in lirika.STEMS:
        raise LookupError(f"No stem called {kind!r}: only {', '.join(lirika.STEMS)}.")
    path = lirika.stem_file(song_of(track_id), lirika.STEMS[kind])
    if not path.exists():
        raise LookupError(f"The {kind} stem is not separated yet: a Regenerate that uses it separates it.")
    return path


def _pick(name: str, label: str, hint: str, choices, *needs: str, default=None) -> dict:
    return {"name": name, "label": label, "hint": hint, "needs": needs,
            "default": getattr(lirika.Tuning(), name) if default is None else default,
            "choices": [{"value": value, "label": text} for value, text in choices]}


def _dial(name: str, label: str, hint: str, low: float, high: float, step: float, *needs: str) -> dict:
    return {"name": name, "label": label, "hint": hint, "needs": needs, "default": getattr(lirika.Tuning(), name),
            "min": low, "max": high, "step": step}


_ON_OFF = ((True, "On"), (False, "Off"))
_STEM_CHOICES = (("lead", "Lead vocal"), ("vocals", "All voices"))

# Everything a request may set, and what the page shows for it: the page draws its controls from this table,
# so a new knob is one entry here. `needs` names the knobs it does nothing without. All but the language are
# fields of lirika.Tuning, whose comments say what the defaults rest on.
KNOBS = [
    _pick("language", "Language", "What is sung. Picks the transcription and timing models.",
          (("bg", "Bulgarian"), ("en", "English")), default="bg"),
    _pick("map_text", "Line order",
          "Follow the singing lets lines come in any order, any number of times, and leans on Whisper. "
          "As written skips Whisper, and needs the text complete and in sung order.",
          ((True, "Follow the singing"), (False, "As written"))),
    _pick("gate", "Sung parts from",
          "The stem that says where the song is sung: what Whisper transcribes and which gaps get filled. "
          "All voices keeps the duet partners and second voices the lead vocal drops.",
          _STEM_CHOICES, "map_text"),
    _pick("asr_input", "Whisper hears", "The audio Whisper transcribes. Try a stem when instruments drown the words.",
          (("mix", "The song"), *_STEM_CHOICES), "map_text"),
    _pick("align_on", "Words timed on",
          "The audio the aligner times words on. Try the lead vocal when backing voices pull lines off.",
          (*_STEM_CHOICES, ("mix", "The song"))),
    _pick("settle_lines", "Re-place lines",
          "Places each matched line again by its own words, between its neighbours. Helps where Whisper skipped a repeat.",
          _ON_OFF, "map_text"),
    _pick("fill_gaps", "Fill gaps",
          "Offers the text's lines to singing no line covers: the repeats Whisper missed. Filled lines get flagged.",
          _ON_OFF, "map_text"),
    _dial("activity_db", "Singing threshold, dB",
          "How far under the song's loud parts still counts as singing. Raise it for quiet passages, lower it when "
          "breaths or bleed count.", 15, 60, 1),
    _dial("map_match", "Line match",
          "How alike a sung phrase and a text line must be. Lower it when Whisper garbles the words, raise it when "
          "wrong lines get picked.", 0.3, 0.9, 0.05, "map_text"),
    _dial("fill_margin", "Placement bar",
          "How well a line must fit to be re-placed or filled in. Closer to 0 is stricter.", -1, 0, 0.05, "map_text"),
    _dial("snap", "Snap to a note, s", "How far a line's start may move to the nearest note start.", 0.05, 0.5, 0.05),
    _dial("max_words", "Longest line, words",
          "A typed line over twice this long counts as a stanza, and is cut at its pauses down to this.", 4, 40, 1),
]


def settings_of(asked: dict) -> dict:
    """The knobs' defaults with what the request changed, each checked: they reach the pipeline as they are."""
    if not isinstance(asked, dict):
        raise ValueError("The settings are not an object.")
    knobs = {knob["name"]: knob for knob in KNOBS}
    chosen = {name: knob["default"] for name, knob in knobs.items()}
    for name, value in asked.items():
        knob = knobs.get(name)
        if knob is None:
            raise ValueError(f"No setting called {name!r}.")
        if "choices" in knob:
            if value not in [choice["value"] for choice in knob["choices"]]:
                raise ValueError(f"{knob['label']} cannot be {value!r}.")
            chosen[name] = value
        else:
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not knob["min"] <= value <= knob["max"]:
                raise ValueError(f"{knob['label']} runs from {knob['min']} to {knob['max']}, not {value!r}.")
            chosen[name] = type(knob["default"])(value)
    return chosen


def _worker_starts(stages) -> None:
    global _stages
    _stages = stages


def _generate_in_worker(song: str, text: str, settings: dict, by: str | None) -> dict:
    """Runs in the worker process, where the models are loaded once and kept until it exits."""
    global _models
    if _models is None:
        _models = lirika.Models()
    messages: list[str] = []
    tuning = {name: value for name, value in settings.items() if name != "language"}
    result = lirika.generate(Path(song), lirika.LANGUAGES[settings["language"]], _models, lirika.Tuning(**tuning),
                             text=text, origin="edited", log=messages.append, by=by, stage=_stages.put)
    return {"mode": result.mode, "lrc": result.lrc, "songFlags": result.flags, "log": messages,
            "lines": [{"start": round(line.start, 2), "end": round(line.end, 2), "text": lirika.tidy(line.text),
                       "flags": line.flags} for line in result.lines]}


def _generation(song: Path, text: str, settings: dict, by: str | None):
    """Each stage as the worker reaches it, then its result or what went wrong."""
    global _pool, _stages, _last
    if gpu.locked():
        yield {"stage": "Waiting for the Regenerate before this one"}
    with gpu:
        try:
            if _pool is None:
                yield {"stage": "Starting the worker"}
                context = multiprocessing.get_context("spawn")
                # Simple: put() writes straight to the pipe, so a finished worker's stages are all readable here.
                _stages = context.SimpleQueue()
                _pool = ProcessPoolExecutor(1, mp_context=context, initializer=_worker_starts, initargs=(_stages,))
            future = _pool.submit(_generate_in_worker, str(song), text, settings, by)
            while not future.done() or not _stages.empty():
                if _stages.empty():
                    time.sleep(0.1)
                else:
                    yield {"stage": _stages.get()}
            yield {"result": future.result()}
        except Exception as error:  # the answer's status went out long ago: failing is one more event
            traceback.print_exc()
            if isinstance(error, BrokenProcessPool):  # killed, most likely for memory: the next one starts afresh
                _pool = None
            yield {"error": {"code": "failed", "message": str(error) or type(error).__name__}}
        finally:
            _last = time.monotonic()


def generate(track_id: str, text: str, settings: dict, by: str | None = None):
    """Refuses here what it can, as a plain 4xx and before minutes of separating; the rest is `_generation`'s."""
    song = song_of(track_id)
    chosen = settings_of(settings)
    if not lirika.normalize(text):
        raise ValueError("The text has no lines to time.")
    return _generation(song, text, chosen, by)


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
    """Keeps the edit in the cache, for the next session's "Your last saved edit", and writes the .lrc beside the
    audio, replacing whatever was there. stih's scan records it within a minute, as Generated."""
    song = song_of(track_id)
    EDITED.mkdir(parents=True, exist_ok=True)
    (EDITED / f"{song.stem}.lrc").write_text(lrc, encoding="utf-8")
    (EDITED / f"{song.stem}.txt").write_text(text, encoding="utf-8")
    song.with_suffix(".lrc").write_text(lrc, encoding="utf-8")
    return {"lrc": str(song.with_suffix(".lrc")), "text": str(EDITED / f"{song.stem}.txt")}


class Handler(BaseHTTPRequestHandler):
    def parse_request(self) -> bool:  # before any route: a refused client gets no further
        if not super().parse_request():
            return False
        if self.command == "OPTIONS":  # a CORS preflight carries no credentials
            return True
        if refusal := who(self.headers):
            status, code, message = refusal
            self._send(status, {"error": {"code": code, "message": message}})
            return False
        return True

    def do_OPTIONS(self) -> None:  # the CORS preflight every request sends first: they all carry Authorization
        self._send(HTTPStatus.NO_CONTENT, None)

    def do_GET(self) -> None:
        url = urlsplit(self.path)
        path = url.path.removeprefix("/Audio/Lirika")  # behind nginx; the host run has no prefix
        query = {key: values[0] for key, values in parse_qs(url.query).items()}
        if path == "/health":
            self._answer(lambda: {"ok": True})
        elif path == "/knobs":
            self._answer(lambda: KNOBS)
        elif path == "/text":
            self._answer(lambda: texts(query.get("id", "")))
        elif path == "/link":
            self._answer(lambda: from_link(query.get("url", "")))
        elif path == "/stems":
            self._answer(lambda: stems(query.get("id", "")))
        elif path == "/stem":
            # ponytail: the whole file in one answer, no Range. The page plays it from a blob, which seeks.
            self._answer(lambda: stem_of(query.get("id", ""), query.get("kind", "")).read_bytes(), "audio/flac")
        else:
            self._refuse(HTTPStatus.NOT_FOUND, "not_found", "No such route.")

    def do_POST(self) -> None:
        try:
            body = json.loads(self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"{}")
        except json.JSONDecodeError:
            return self._refuse(HTTPStatus.BAD_REQUEST, "invalid_request", "The body is not JSON.")
        path = urlsplit(self.path).path.removeprefix("/Audio/Lirika")
        if path == "/generate":
            self._answer(lambda: generate(body.get("id", ""), body.get("text", ""), body.get("settings", {}),
                                          body.get("by") or None))
        elif path == "/save":
            self._answer(lambda: save(body.get("id", ""), body.get("lrc", ""), body.get("text", "")))
        else:
            self._refuse(HTTPStatus.NOT_FOUND, "not_found", "No such route.")

    def _answer(self, work, content_type: str = "application/json; charset=utf-8") -> None:
        try:
            answer = work()
            if isinstance(answer, Iterator):
                self._stream(answer)
            else:
                self._send(HTTPStatus.OK, answer, content_type)
        except LookupError as error:
            self._refuse(HTTPStatus.NOT_FOUND, "not_found", str(error))
        except ValueError as error:  # normalize() refusing an unresolvable repeat in the text
            self._refuse(HTTPStatus.BAD_REQUEST, "invalid_request", str(error))

    def _refuse(self, status: HTTPStatus, code: str, message: str) -> None:
        """The stack's error envelope."""
        self._send(status, {"error": {"code": code, "message": message}})

    def _stream(self, events) -> None:
        """One JSON object per line, as each happens. HTTP/1.0 with no length: closing the connection ends it."""
        self._start(HTTPStatus.OK)
        self.send_header("Content-Type", "application/x-ndjson; charset=utf-8")
        self.end_headers()
        here = True
        for event in events:  # to the end even with the page gone: the worker cannot be stopped, and holds the GPU
            if here:
                try:
                    self.wfile.write(json.dumps(event, ensure_ascii=False).encode() + b"\n")
                except OSError:
                    here = False

    def _start(self, status: HTTPStatus) -> None:
        self.send_response(status)
        # Any origin, for Dom's reason: the credential is an explicit Authorization header, never a cookie.
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Authorization, Content-Type")
        self.send_header("Access-Control-Max-Age", "7200")

    def _send(self, status: HTTPStatus, payload, content_type: str = "application/json; charset=utf-8") -> None:
        self._start(status)
        if isinstance(payload, bytes):
            body = payload
        else:
            body = b"" if payload is None else json.dumps(payload, ensure_ascii=False).encode()
        if payload is not None:
            self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format: str, *args) -> None:
        print(f"studio: {format % args}", flush=True)


if __name__ == "__main__":
    print(f"lirika studio on http://{HOST}:{PORT} for staff with the studio permission (asking {DOM}); "
          f"library {LIBRARY}", flush=True)
    threading.Thread(target=_reap, daemon=True).start()
    ThreadingHTTPServer((HOST, PORT), Handler).serve_forever()
