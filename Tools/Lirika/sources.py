"""
Stage L of the plan, and lirika's two touch points with the running stack.

Finding a song's words online: each site is one function that takes a :class:`Track` and returns
:class:`Candidate` texts whose names match it. :func:`lookup` then accepts the first candidate the
audio agrees with. Also here: what the library knows about a file (``library.db``, read-only), and
handing a result to stih.
"""

from __future__ import annotations

import hashlib
import html
import re
import sqlite3
import time
import urllib.robotparser
from dataclasses import dataclass
from difflib import SequenceMatcher
from pathlib import Path
from urllib.parse import urlsplit

import requests
from unidecode import unidecode

WEB_CACHE = Path.home() / ".cache/lirika/web"
BACKEND_ENV = Path(__file__).resolve().parents[2] / "Backend" / ".env"
DEFAULT_AGENT = "musicrain/1.0 (+https://github.com/t1stm/musicrain)"


# ── the stack's own configuration ───────────────────────────────────────────────────────────────


def backend_env() -> dict[str, str]:
    """Backend/.env, the file compose reads: the library path, the admin token, published ports."""
    values: dict[str, str] = {}
    if BACKEND_ENV.exists():
        for line in BACKEND_ENV.read_text(encoding="utf-8").splitlines():
            key, separator, value = line.partition("=")
            if separator and not key.lstrip().startswith("#"):
                values[key.strip()] = value.strip().strip('"').strip("'")
    return values


@dataclass
class Track:
    titles: list[str]
    artists: list[str]
    length: float | None  # seconds
    id: str | None  # audio://… when the file is in the library
    relative: str | None
    album: str | None = None


def track_of(song: Path) -> Track:
    """What the library says about a file, or what its name says when the library has no row for it."""
    env = backend_env()
    library = Path(env.get("MUSIC_LIBRARY_PATH", "/nonexistent"))
    try:
        relative = song.resolve().relative_to(library.resolve()).as_posix()
    except ValueError:
        relative = None

    if relative and (library / "library.db").exists():
        db = sqlite3.connect(f"file:{library / 'library.db'}?mode=ro", uri=True)
        try:
            row = db.execute("SELECT Id, Length, Album FROM songs WHERE RelativeLocation = ?", (relative,)).fetchone()
            if row:
                titles = [title for (title,) in db.execute(
                    "SELECT Title FROM song_titles WHERE RelativeLocation = ? ORDER BY Position", (relative,))]
                artists = [artist for (artist,) in db.execute(
                    "SELECT Artist FROM song_artists WHERE RelativeLocation = ? ORDER BY Position", (relative,))]
                return Track(titles, artists, row[1] / 1000, f"audio://{row[0]}", relative, row[2])
        finally:
            db.close()

    # The library's own naming convention, "Artist - Title.ext".
    artist, _, title = song.stem.partition(" - ")
    return Track([title or artist], [artist] if title else [], None, None, relative)


def register(song: Path, lrc: str, source: str) -> bool:
    """POSTs a result to stih's local-only /register, which writes it beside the audio and indexes it."""
    env = backend_env()
    track = track_of(song)
    if track.id is None:
        print(f"{song.name}: not in library.db, so stih has no ID for it", flush=True)
        return False

    headers = {"X-Admin-Token": env["ADMIN_TOKEN"]} if env.get("ADMIN_TOKEN") else {}
    url = f"http://127.0.0.1:{env.get('STIH_PORT', '5345')}/register"
    try:
        response = requests.post(url, json={"id": track.id, "lrc": lrc, "source": source}, headers=headers,
                                 timeout=10)
    except requests.RequestException as error:
        print(f"{song.name}: stih is unreachable ({error})", flush=True)
        return False
    if response.status_code != 204:
        print(f"{song.name}: stih answered {response.status_code}: {response.text[:200]}", flush=True)
        return False
    return True


# ── matching ────────────────────────────────────────────────────────────────────────────────────

_SUFFIX = re.compile(r"\s*[(\[](?:feat|ft|remix|live|official|radio|extended|original|version|prod)[^)\]]*[)\]]",
                     re.IGNORECASE)


def plain(text: str) -> str:
    """Romanized, lowercase, letters and single spaces: how two spellings of one name compare."""
    text = unidecode(_SUFFIX.sub("", text)).lower()
    return re.sub(r"\s+", " ", re.sub(r"[^a-z0-9]+", " ", text)).strip()


def same_song(track: Track, artist: str, title: str) -> float:
    """stih's rule from Matching.cs: title 0.65, artist 0.35, and 0.80 is a match."""
    def ratio(a: str, b: str) -> float:
        return SequenceMatcher(None, plain(a), plain(b)).ratio()

    best_title = max((ratio(title, wanted) for wanted in track.titles), default=0.0)
    best_artist = max((ratio(artist, wanted) for wanted in track.artists), default=0.0)
    if not track.artists:  # a bare file name with no " - ": the title has to carry it alone
        return best_title
    return 0.65 * best_title + 0.35 * best_artist


def agreement(drafted: str, text: str) -> float | None:
    """
    The share of the draft's letter 4-grams that the candidate contains. A rough transcription of the
    right song shares most of them; a different song shares the common ones only. ``None`` when the
    draft is too short to judge.
    """
    if len(drafted.split()) < 15:
        return None

    def grams(value: str) -> set[str]:
        letters = plain(value).replace(" ", "")
        return {letters[index:index + 4] for index in range(len(letters) - 3)}

    mine = grams(drafted)
    return len(mine & grams(text)) / len(mine) if mine else None


# ── fetching ────────────────────────────────────────────────────────────────────────────────────

_session = requests.Session()
_session.headers["User-Agent"] = backend_env().get("LRCLIB_USER_AGENT") or DEFAULT_AGENT
_last: dict[str, float] = {}
_robots: dict[str, urllib.robotparser.RobotFileParser] = {}


def fetch(url: str, params: dict | None = None, log=print) -> str | None:
    """
    One polite GET: cached on disk, one request per second per host, robots.txt honoured. Any failure is
    a warning and ``None`` -- a site that changes or blocks costs that site's answer, never the run.
    """
    full = requests.Request("GET", url, params=params).prepare().url
    cached = WEB_CACHE / (hashlib.sha1(full.encode()).hexdigest() + ".txt")
    if cached.exists():
        return cached.read_text(encoding="utf-8")

    parts = urlsplit(full)
    if parts.hostname not in ("127.0.0.1", "localhost"):
        robots = _robots.get(parts.netloc)
        if robots is None:
            robots = _robots[parts.netloc] = urllib.robotparser.RobotFileParser(f"{parts.scheme}://{parts.netloc}/robots.txt")
            try:
                robots.read()
            except OSError:
                robots.allow_all = True
        if not robots.can_fetch(_session.headers["User-Agent"], full):
            log(f"robots.txt disallows {full}")
            return None

    wait = _last.get(parts.netloc, 0.0) + 1.0 - time.monotonic()
    if wait > 0:
        time.sleep(wait)
    try:
        response = _session.get(full, timeout=15)
        _last[parts.netloc] = time.monotonic()
        if response.status_code == 204:
            return None
        response.raise_for_status()
    except requests.RequestException as error:
        log(f"{parts.netloc}: {error}")
        return None

    WEB_CACHE.mkdir(parents=True, exist_ok=True)
    cached.write_text(response.text, encoding="utf-8")
    return response.text


# ── the sites ───────────────────────────────────────────────────────────────────────────────────


@dataclass
class Candidate:
    source: str
    artist: str
    title: str
    text: str
    url: str


MATCH = 0.80
PER_SOURCE = 3  # texts fetched per site at most: the name match has already ranked them


_TEXTOVE_HIT = re.compile(r'<a href="(?P<slug>[^"]+-tekst)" class="gtm-Search"[^>]*>.*?'
                          r'<span itemprop="name">(?P<title>[^<]*)</span>.*?<h4>(?P<artist>[^<]*)</h4>', re.DOTALL)
_TEXTOVE_WORDS = re.compile(r'<div id="maintxt">(?P<body>.*?)</div>', re.DOTALL)


# Bulgaria's official romanization, backwards, longest spelling first. A guess -- "y" may be й, ъ or ь --
# but textove's search is fuzzy, and only the name match decides what is used.
_CYRILLIC = [("sht", "щ"), ("zh", "ж"), ("ts", "ц"), ("ch", "ч"), ("sh", "ш"), ("yu", "ю"), ("ya", "я"),
             ("x", "кс"), ("j", "дж"), *zip("abvgdeziyklmnoprstufhcwq", "абвгдезийклмнопрстуфхцвк")]


def cyrillic(text: str) -> str:
    out, index, lower = [], 0, text.lower()
    while index < len(text):
        for latin, letter in _CYRILLIC:
            if lower.startswith(latin, index):
                out.append(letter.upper() if text[index].isupper() else letter)
                index += len(latin)
                break
        else:
            out.append(text[index])
            index += 1
    return "".join(out)


def textove(track: Track, log=print) -> list[Candidate]:
    """textove.com, the largest Bulgarian lyrics site: a search page, then a song page."""
    out = []
    queries = _queries(track)
    # A track the library only knows in Latin letters is listed there in Cyrillic.
    for query in [cyrillic(query) for query in queries if re.search(r"[a-z]", query, re.IGNORECASE)] + queries:
        page = fetch("https://textove.com/search.html", {"search": query}, log) or ""
        hits = [(same_song(track, html.unescape(hit["artist"]), html.unescape(hit["title"])), hit)
                for hit in _TEXTOVE_HIT.finditer(page)]
        for score, hit in sorted(hits, key=lambda pair: -pair[0]):
            if score < MATCH or len(out) >= PER_SOURCE:
                break
            url = f"https://textove.com/{hit['slug']}"
            words = textove_words(fetch(url, log=log) or "")
            if words:
                out.append(Candidate("textove", html.unescape(hit["artist"]), html.unescape(hit["title"]), words, url))
        if out:
            break
    return out


def textove_words(page: str) -> str | None:
    found = _TEXTOVE_WORDS.search(page)
    if not found:
        return None
    body = re.sub(r"<br\s*/?>", "\n", found["body"], flags=re.IGNORECASE)
    body = re.sub(r"</p>", "\n\n", body, flags=re.IGNORECASE)
    text = html.unescape(re.sub(r"<[^>]+>", "", body))
    # A song with foreign lines has its translation in the same block, after a bare "Превод".
    text = re.split(r"^\s*(?:превод|translation)\s*:?\s*$", text, maxsplit=1, flags=re.IGNORECASE | re.MULTILINE)[0]
    return "\n".join(line.strip() for line in text.splitlines()).strip() or None


SOURCES = {"textove": textove}


def lookup(track: Track, names: tuple[str, ...], drafted: str, normalize, threshold: float,
           log=print) -> Candidate | None:
    """The first site's best candidate the draft agrees with, normalized; ``None`` sends the song to transcription."""
    for name in names:
        best: tuple[float, Candidate] | None = None
        for candidate in SOURCES[name](track, log):
            try:
                lines = normalize(candidate.text)
            except ValueError as error:
                log(f"{name}: {candidate.url}: {error}")
                continue
            score = agreement(drafted, "\n".join(lines))
            log(f"{name}: {candidate.artist} - {candidate.title}: agreement "
                f"{'n/a' if score is None else f'{score:.2f}'}")
            if score is not None and score >= threshold and (best is None or score > best[0]):
                best = (score, candidate)
        if best:
            return best[1]
    return None


def _queries(track: Track) -> list[str]:
    """Artist and title, in each spelling the library has, the first pair first."""
    out = []
    for title in track.titles[:2]:
        for artist in (track.artists[:2] or [""]):
            query = f"{artist} {title}".strip()
            if query not in out:
                out.append(query)
    return out


