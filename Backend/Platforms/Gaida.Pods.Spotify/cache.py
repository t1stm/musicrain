"""
The search cache: every track a search, playlist or resolve handed back, as the song data the pod answered
with -- never the query, the playlist or anything playable, since Spotify has nothing playable to give.

One row per track in ``cache.db``, the same columns as the Deezer pod's ``results`` table. First seen wins,
nothing is evicted and nothing is held in memory: a row is a few hundred bytes. A later ``/resolve`` of a
remembered track costs no Spotify call, which matters more here than anywhere -- SpotAPI is unofficial,
and every request it does not make is one it cannot have refused.

Nothing here is async. Every method is called from a worker thread (FastAPI's threadpool, or
``StreamingResponse`` iterating a sync generator), so one connection is shared under one lock.
"""

import logging
import sqlite3
import threading
from pathlib import Path
from typing import Any

log = logging.getLogger("gaida.spotify")

PREFIX = "spotify://"

_COLUMNS = ("name", "artist", "album", "duration", "thumbnailUrl")
_REMEMBER = f"INSERT OR IGNORE INTO results (id, {', '.join(_COLUMNS)}) VALUES (?, ?, ?, ?, ?, ?)"

PAGE_MAX = 200
"""The most rows one page of :func:`page` carries."""

SCHEMA_VERSION = 1
"""``PRAGMA user_version`` once the table exists. 0 is a new file."""


class Results:
    def __init__(self, directory: str) -> None:
        path = Path(directory)
        path.mkdir(parents=True, exist_ok=True)

        self._lock = threading.Lock()
        self._db = sqlite3.connect(path / "cache.db", check_same_thread=False)
        self._db.execute("PRAGMA journal_mode=WAL")
        self._db.execute("PRAGMA synchronous=NORMAL")
        self._db.create_function("fold", 1, _fold, deterministic=True)

        with self._lock, self._db:
            if self._db.execute("PRAGMA user_version").fetchone()[0] < SCHEMA_VERSION:
                self._db.execute("""CREATE TABLE IF NOT EXISTS results (
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, artist TEXT NOT NULL, album TEXT,
                    duration TEXT NOT NULL, thumbnailUrl TEXT)""")
                self._db.execute(f"PRAGMA user_version = {SCHEMA_VERSION}")

        log.info("Opened the search cache at %s (%d tracks)", path / "cache.db", self.count())

    def get(self, track_id: str) -> dict[str, Any] | None:
        """A remembered track in pod-result shape, or ``None`` when nothing ever listed it."""
        with self._lock:
            row = self._db.execute(f"SELECT {', '.join(_COLUMNS)} FROM results WHERE id = ?",
                                   (track_id,)).fetchone()

        if row is None:
            return None

        return {"id": PREFIX + track_id, **dict(zip(_COLUMNS, row)), "originalTitle": None, "originalArtist": None}

    def remember(self, dtos: list[dict[str, Any]]) -> None:
        """
        Keeps tracks not seen before. A failed write is logged, not raised: the answer it came from is
        already on its way, and losing the row costs one Spotify call later.
        """
        rows = [(dto["id"].removeprefix(PREFIX), *(dto[column] for column in _COLUMNS)) for dto in dtos]
        if not rows:
            return

        try:
            with self._lock, self._db:
                self._db.executemany(_REMEMBER, rows)
        except sqlite3.Error:
            log.warning("Could not remember %d Spotify tracks", len(rows), exc_info=True)

    def count(self) -> int:
        with self._lock:
            return self._db.execute("SELECT count(*) FROM results").fetchone()[0]

    def browse(self, query: str | None, offset: int, take: int, top: int | None) -> dict[str, Any]:
        """One page for Oko -- see :func:`page`."""
        with self._lock:
            return page(self._db, "results", PREFIX, ", ".join(_COLUMNS), query, offset, take, top)

    def forget(self, track_id: str) -> bool:
        """Drops one remembered track. The next search or resolve that finds it remembers it again."""
        with self._lock, self._db:
            return self._db.execute("DELETE FROM results WHERE id = ?", (track_id,)).rowcount > 0


def page(db: sqlite3.Connection, table: str, prefix: str, columns: str, query: str | None, offset: int,
         take: int, top: int | None) -> dict[str, Any]:
    """
    One page of ``table`` for Oko's cache browser, newest first, in the shape every pod answers with.

    ``top`` pins the pages to the rows page 1 saw, so rows written meanwhile shift nothing; page 1 passes
    ``None`` and gets the pin back. The search folds case in Cyrillic too, through ``fold`` -- SQLite's
    own ``LIKE`` and ``lower()`` fold ASCII only. Caller holds the connection's lock, and ``fold`` is
    registered on it.

    Byte-for-byte the same function as Gaida.Pods.Deezer/cache.py's, for the reason admin.py gives.
    Change one, change the other.

    ponytail: every search is a full scan. Fine at thousands of rows; an FTS5 table is the upgrade.
    """
    needle = (query or "").strip()
    offset, take = max(0, offset), min(max(1, take), PAGE_MAX)
    key = f"'{prefix}' || id"
    if top is None:
        top = db.execute(f"SELECT coalesce(max(rowid), 0) FROM {table}").fetchone()[0]

    where, parameters = "rowid <= ?", [top]
    if needle:
        where += (" AND (instr(fold(coalesce(name, '') || char(10) || coalesce(artist, '') || char(10) || "
                  f"coalesce(album, '')), ?) OR instr({key}, ?))")
        parameters += [_fold(needle), needle]

    total = db.execute(f"SELECT count(*) FROM {table} WHERE {where}", parameters).fetchone()[0]
    cursor = db.execute(f"SELECT {key} AS id, {columns} FROM {table} WHERE {where} "
                        "ORDER BY rowid DESC LIMIT ? OFFSET ?", [*parameters, take, offset])
    names = [column[0] for column in cursor.description]
    return {"top": top, "total": total, "offset": offset, "take": take,
            "rows": [dict(zip(names, row)) for row in cursor]}


def _fold(text: str | None) -> str | None:
    return text.casefold() if text else text
