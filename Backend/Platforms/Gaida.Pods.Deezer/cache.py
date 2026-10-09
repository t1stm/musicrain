"""
The downloaded-song cache and the search cache: this pod's only state, and the only reason it needs a
volume.

One audio file per track in one flat directory, ``<id>.mp3`` or ``<id>.flac``, and one row per track in
``cache.db`` beside them. The row is what makes the cache self-describing: Oko's table and the local pod's
import both read a cached track's name, artist and format out of it rather than asking Deezer again, and
a pod that restarts rebuilds its whole index from one query.

Every track a search, playlist, artist or album listed is one row in ``results`` in the same file, so a
later ``/resolve`` of it costs no Deezer call -- the same job ``YouTube.db`` does for the YouTube pod.
Those rows are never held in memory and never evicted: a row is a few hundred bytes, not a song.

Older versions wrote a ``<id>.json`` sidecar per track instead. A ``cache.db`` that is new imports them
once and leaves them where they are, so an older image pointed at this volume still finds its index.

Nothing here is async. Every method is called from a worker thread (``asyncio.to_thread`` in
:mod:`main`), which is also where the download that feeds it runs.
"""

import asyncio
import json
import logging
import os
import sqlite3
import threading
import time
from dataclasses import astuple, dataclass, fields
from pathlib import Path
from typing import Any

import tags

log = logging.getLogger("gaida.deezer")

MAX_BYTES_DEFAULT = 21_474_836_480
"""20 GiB, the same order as Dunav's disk budget. Sized against free space, not against memory."""


@dataclass(frozen=True)
class Entry:
    """One cached track. The DTO fields are exactly what :mod:`mapper` produced when it was fetched."""

    id: str
    format: str
    bytes: int
    at: float
    name: str
    artist: str
    album: str | None
    duration: str
    thumbnailUrl: str | None

    @property
    def filename(self) -> str:
        return f"{self.id}.{self.format}"

    def to_dto(self) -> dict[str, Any]:
        """The cached metadata back in pod-result shape, so a cache hit can answer ``/resolve`` too."""
        return {
            "id": "deezer://" + self.id,
            "name": self.name,
            "artist": self.artist,
            "album": self.album,
            "duration": self.duration,
            "thumbnailUrl": self.thumbnailUrl,
            "originalTitle": None,
            "originalArtist": None,
        }


_COLUMNS = [field.name for field in fields(Entry)]
_INSERT = f"INSERT OR REPLACE INTO entries VALUES ({', '.join('?' * len(_COLUMNS))})"

_RESULT_COLUMNS = ("name", "artist", "album", "duration", "thumbnailUrl")
_REMEMBER = f"INSERT OR IGNORE INTO results (id, {', '.join(_RESULT_COLUMNS)}) VALUES (?, ?, ?, ?, ?, ?)"

SCHEMA_VERSION = 2
"""
``PRAGMA user_version`` once every table exists and the sidecars are imported. 0 is a new file, 1 one
written before the ``results`` table.
"""


class Cache:
    """
    Every downloaded track, indexed in memory and backed by the directory.

    The index is not an optimisation: Oko polls ``/Admin/snapshot`` every two seconds while its panel
    is open, and answering that by scanning a directory of thousands of files would make an open admin
    tab the most expensive thing this pod does.
    """

    def __init__(self, directory: str, max_bytes: int) -> None:
        self.directory = Path(directory)
        self.max_bytes = max(0, max_bytes)
        self._entries: dict[str, Entry] = {}

        # Guards the index and the directory against the several worker threads that reach them.
        # The per-track asyncio locks below are a different job: they stop two listeners downloading
        # the same track, and are held across an await this one must never be.
        self._lock = threading.Lock()
        self._downloads: dict[str, asyncio.Lock] = {}

        self.directory.mkdir(parents=True, exist_ok=True)

        # One connection for every worker thread, so every statement runs under self._lock.
        self._db = sqlite3.connect(self.directory / "cache.db", check_same_thread=False)
        self._db.execute("PRAGMA journal_mode=WAL")
        self._db.execute("PRAGMA synchronous=NORMAL")
        self._load()

    # ── reading ─────────────────────────────────────────────────────────────────────────────────

    def get(self, track_id: str) -> Entry | None:
        with self._lock:
            return self._entries.get(track_id)

    def path(self, entry: Entry) -> Path:
        return self.directory / entry.filename

    def stats(self) -> tuple[int, int]:
        """How many tracks are cached and how many bytes they take."""
        with self._lock:
            return len(self._entries), sum(entry.bytes for entry in self._entries.values())

    def recent(self, limit: int) -> list[Entry]:
        """The newest entries, for the admin table. A limit of 0 or less means all of them."""
        with self._lock:
            entries = sorted(self._entries.values(), key=lambda entry: entry.at, reverse=True)

        return entries[:limit] if limit > 0 else entries

    def result(self, track_id: str) -> dict[str, Any] | None:
        """A track some search listed, in pod-result shape, or ``None`` when none ever did."""
        with self._lock:
            row = self._db.execute(f"SELECT {', '.join(_RESULT_COLUMNS)} FROM results WHERE id = ?",
                                   (track_id,)).fetchone()

        if row is None:
            return None

        return {"id": "deezer://" + track_id, **dict(zip(_RESULT_COLUMNS, row)),
                "originalTitle": None, "originalArtist": None}

    # ── writing ─────────────────────────────────────────────────────────────────────────────────

    def remember(self, results: list[dict[str, Any]]) -> None:
        """
        Keeps search results not seen before. One already remembered keeps what it was first seen as,
        like the YouTube pod's cache.

        A failed write is logged, not raised: the search it came from already has its answer, and
        losing the row costs one Deezer call on a later ``/resolve``, nothing else.
        """
        rows = [(dto["id"].removeprefix("deezer://"), *(dto[column] for column in _RESULT_COLUMNS))
                for dto in results]
        try:
            with self._lock, self._db:
                self._db.executemany(_REMEMBER, rows)
        except sqlite3.Error:
            log.warning("Could not remember %d search results", len(rows), exc_info=True)

    def store(self, track_id: str, data: bytes, audio_format: str, dto: dict[str, Any],
              cover: bytes | None = None, lyrics: str | None = None) -> Entry:
        """
        Writes one downloaded track, replacing whatever was cached for it, and evicts down to the cap.

        The audio goes to a ``.part`` first and is renamed into place, so a crash mid-write never
        leaves a truncated file that the next request would happily serve as a whole song. The tags go
        on while it is still that ``.part``: mutagen rewrites the file to make room for a picture, and
        doing that to a file ``/content`` is already serving is the one version of this that can be read
        half-written. It is also why ``bytes`` is measured off the disk rather than from ``data`` -- a
        100 KB cover is 100 KB of the cache's budget.

        The timed lines are not here. stih owns every lyrics file in this stack and indexes them; a
        second copy in the audio cache would be one more thing to keep in step with it. The plain block
        still goes into the file's tags, because that is part of the file's quality rather than a copy --
        it is what a track imported into the library carries into its tags.

        :param cover: the artwork bytes, embedded in the file.
        :param lyrics: the plain lyrics, embedded in the file.
        """
        target = self.directory / f"{track_id}.{audio_format}"
        partial = target.with_suffix(target.suffix + ".part")
        partial.write_bytes(data)

        # Nothing to embed, nothing to rewrite. ponytail: that skips the names too, for a track with
        # neither a cover nor lyrics -- which a Deezer album object does not produce, since every one of
        # them carries artwork. Drop the guard if one ever does.
        if cover or lyrics:
            tags.embed(partial, audio_format, dto, cover, lyrics)

        entry = Entry(
            id=track_id,
            format=audio_format,
            bytes=partial.stat().st_size,
            at=time.time(),
            name=dto.get("name") or "Unknown title",
            artist=dto.get("artist") or "Unknown artist",
            album=dto.get("album"),
            duration=dto.get("duration") or "00:00:00",
            thumbnailUrl=dto.get("thumbnailUrl"),
        )

        os.replace(partial, target)

        with self._lock:
            previous = self._entries.get(track_id)
            self._entries[track_id] = entry
            # A promote rewrites the same track in the other format, so the old file is now orphaned.
            if previous is not None and previous.format != audio_format:
                _remove(self.directory / previous.filename)

            with self._db:
                self._db.execute(_INSERT, astuple(entry))

        self._evict_to_cap()

        log.info("Cached %s as %s (%d bytes)", track_id, audio_format, entry.bytes)
        return entry

    def remove(self, track_id: str) -> bool:
        """Deletes every file one track owns. ``False`` when it was not cached."""
        with self._lock:
            entry = self._entries.pop(track_id, None)
            self._forget([track_id])

        if entry is None:
            return False

        self._delete(entry)
        log.info("Evicted %s from the cache", track_id)
        return True

    def clear(self) -> int:
        """Deletes everything. Returns how many tracks went."""
        with self._lock:
            entries = list(self._entries.values())
            self._entries.clear()
            with self._db:
                self._db.execute("DELETE FROM entries")

        for entry in entries:
            self._delete(entry)

        log.info("Evicted all %d cached tracks", len(entries))
        return len(entries)

    # ── single-flight ───────────────────────────────────────────────────────────────────────────

    def download_lock(self, track_id: str) -> asyncio.Lock:
        """
        The asyncio lock for one track, so two listeners starting it at once download it once.

        ponytail: the locks are never collected -- one small object per distinct track ever asked for,
        which is bounded by the cache's own size in every realistic use. A weak-valued dictionary is
        the upgrade if a pod ever serves millions of distinct ids without caching them.
        """
        with self._lock:
            lock = self._downloads.get(track_id)
            if lock is None:
                lock = self._downloads[track_id] = asyncio.Lock()

            return lock

    # ── internals ───────────────────────────────────────────────────────────────────────────────

    def _load(self) -> None:
        """One query at startup. A row without its audio is not cached, and does not stay."""
        with self._lock:
            version = self._db.execute("PRAGMA user_version").fetchone()[0]
            if version < 1:
                self._create()
            if version < SCHEMA_VERSION:
                with self._db:
                    self._db.execute("""CREATE TABLE IF NOT EXISTS results (
                        id TEXT PRIMARY KEY, name TEXT NOT NULL, artist TEXT NOT NULL, album TEXT,
                        duration TEXT NOT NULL, thumbnailUrl TEXT)""")
                    self._db.execute(f"PRAGMA user_version = {SCHEMA_VERSION}")

            rows = [Entry(*row) for row in self._db.execute(f"SELECT {', '.join(_COLUMNS)} FROM entries")]
            loaded = {entry.id: entry for entry in rows if (self.directory / entry.filename).exists()}
            self._forget([entry.id for entry in rows if entry.id not in loaded])

        for entry in rows:
            if entry.id not in loaded:
                self._delete(entry)

        self._entries = loaded
        log.info("Loaded %d cached tracks from %s", len(loaded), self.directory)

    def _create(self) -> None:
        """
        The entries table, and every sidecar an older version of this pod wrote. The import and the version
        bump commit together, so a crash halfway leaves user_version at 0 and the next start imports again.
        """
        imported: list[Entry] = []
        for sidecar in self.directory.glob("*.json"):
            try:
                imported.append(Entry(**json.loads(sidecar.read_text(encoding="utf-8"))))
            except (OSError, ValueError, TypeError):
                log.warning("Ignoring unreadable cache sidecar %s", sidecar.name)

        with self._db:
            self._db.execute("""CREATE TABLE IF NOT EXISTS entries (
                id TEXT PRIMARY KEY, format TEXT NOT NULL, bytes INTEGER NOT NULL, at REAL NOT NULL,
                name TEXT NOT NULL, artist TEXT NOT NULL, album TEXT, duration TEXT NOT NULL,
                thumbnailUrl TEXT)""")
            self._db.executemany(_INSERT, [astuple(entry) for entry in imported])
            self._db.execute("PRAGMA user_version = 1")

        if imported:
            log.info("Imported %d cache sidecars into %s", len(imported), self.directory / "cache.db")

    def _forget(self, track_ids: list[str]) -> None:
        """Drops rows. Caller holds ``self._lock``."""
        if track_ids:
            with self._db:
                self._db.executemany("DELETE FROM entries WHERE id = ?", [(track_id,) for track_id in track_ids])

    def _delete(self, entry: Entry) -> None:
        """
        Every file one cached track owns: the audio. Its row is the caller's to drop.

        The ``.lrc`` and the JSON sidecar are gone -- stih holds the timed lines now, and ``cache.db`` the
        metadata -- but deleting them stays, because files an older version of this pod wrote are still
        sitting in deployed volumes and this is the cheapest way to clear them out over time. Drop the two
        lines once every deployment has rotated through its cache.
        """
        _remove(self.directory / entry.filename)
        _remove(self.directory / f"{entry.id}.json")
        _remove(self.directory / f"{entry.id}.lrc")

    def _evict_to_cap(self) -> None:
        """
        Deletes the oldest tracks until the cache is under its byte cap.

        ponytail: sorts the whole index on every write. At tens of thousands of entries that is under a
        millisecond and it runs once per download; a heap is the upgrade if the cap ever grows enough
        for that to stop being true.
        """
        if not self.max_bytes:
            return

        with self._lock:
            total = sum(entry.bytes for entry in self._entries.values())
            if total <= self.max_bytes:
                return

            doomed: list[Entry] = []
            for entry in sorted(self._entries.values(), key=lambda entry: entry.at):
                if total <= self.max_bytes:
                    break

                doomed.append(entry)
                del self._entries[entry.id]
                total -= entry.bytes

            self._forget([entry.id for entry in doomed])

        for entry in doomed:
            self._delete(entry)

        log.info("Evicted %d track(s) to stay under %d bytes", len(doomed), self.max_bytes)


def _remove(path: Path) -> None:
    """Deleting something already gone is the outcome that was wanted, not a failure."""
    try:
        path.unlink(missing_ok=True)
    except OSError:
        log.warning("Could not delete %s", path, exc_info=True)
