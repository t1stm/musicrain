"""
ponytail: the one runnable check for this pod's pure logic -- the parsing, the mapping, the vendored
key derivation and the cache's two rules that would be wrong silently. No fixtures, no network, no
ARL. Run with `pytest`, or with `python test_deezer.py` in the pod image, which has neither.
"""

import asyncio
import json
import os
import struct
import tempfile
from pathlib import Path

from mutagen.flac import FLAC
from mutagen.id3 import ID3

import cache
import classify
import stream
from mapper import duration, to_dto, with_album

# main builds a Cache at import time, so it needs somewhere to build it that is not /cache. Set before
# the import and never after: the module reads its environment exactly once.
os.environ.setdefault("DEEZER_CACHE", tempfile.mkdtemp())
os.environ.setdefault("STIH_URL", "http://stih:8080")
import main  # noqa: E402

TRACK = "3135556"
PLAYLIST = "908622995"


# ── classify ────────────────────────────────────────────────────────────────────────────────────

def test_recognises_tracks():
    for query in (f"deezer://{TRACK}",
                  f"https://www.deezer.com/track/{TRACK}",
                  f"https://www.deezer.com/en/track/{TRACK}?utm_source=x",
                  f"https://deezer.com/us/track/{TRACK}"):
        assert classify.parse(query) == classify.ClassifyResult(200, "id", f"deezer://{TRACK}"), query


def test_recognises_playlists():
    for query in (f"deezer-playlist://{PLAYLIST}",
                  f"https://www.deezer.com/playlist/{PLAYLIST}",
                  f"https://www.deezer.com/fr/playlist/{PLAYLIST}"):
        assert classify.parse(query) == classify.ClassifyResult(
            200, "playlist", f"deezer-playlist://{PLAYLIST}"), query


def test_claims_but_rejects_malformed():
    assert classify.parse("deezer://not-a-number") == classify.ClassifyResult(
        400, error="The Deezer track ID is invalid.")
    assert classify.parse("https://www.deezer.com/album/302127") == classify.ClassifyResult(
        400, error="The Deezer link is not a track or a playlist.")


def test_leaves_other_queries_alone():
    # A share link carries no ID, so claiming it would mean claiming a query this pod cannot answer.
    for query in ("", "   ", "daft punk", "yt://dQw4w9WgXcQ", "https://deezer.page.link/abcdef",
                  "https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT"):
        assert classify.parse(query) == classify.NOT_MINE, query


def test_ids_come_back_bare():
    assert classify.track_id(f"https://www.deezer.com/track/{TRACK}") == TRACK
    # Gaida.API strips the protocol before it asks, so a bare ID is the ordinary case on /resolve.
    assert classify.track_id(TRACK) == TRACK
    assert classify.track_id("audio://abc") is None
    assert classify.playlist_id(f"deezer-playlist://{PLAYLIST}") == PLAYLIST
    assert classify.playlist_id(f"deezer://{TRACK}") is None


# ── mapper ──────────────────────────────────────────────────────────────────────────────────────

def _track(**overrides):
    track = {
        "id": 3135556,
        "title": "Harder, Better, Faster, Stronger",
        "duration": 224,
        "artist": {"name": "Daft Punk"},
        "album": {"title": "Discovery", "cover_small": "small.jpg", "cover_xl": "xl.jpg"},
    }
    track.update(overrides)
    return track


def test_maps_a_track():
    dto = to_dto(_track())
    assert dto == {
        "id": "deezer://3135556",
        "name": "Harder, Better, Faster, Stronger",
        "artist": "Daft Punk",
        "album": "Discovery",
        "duration": "00:03:44",
        # The biggest cover, not the first one -- a small one here is a blurry row in every client.
        "thumbnailUrl": "xl.jpg",
        "originalTitle": None,
        "originalArtist": None,
    }


def test_drops_what_cannot_be_played():
    assert to_dto(None) is None
    assert to_dto({"title": "no id"}) is None
    # A user's own upload has a negative ID that no CDN URL exists for.
    assert to_dto(_track(id=-4258)) is None
    # Unreadable here means licensing rather than uploads, but the row plays nothing either way.
    assert to_dto(_track(readable=False)) is None


def test_credits_every_contributor():
    dto = to_dto(_track(contributors=[{"name": "Daft Punk"}, {"name": "Pharrell Williams"}]))
    assert dto["artist"] == "Daft Punk, Pharrell Williams"
    # The lead is kept even when Deezer left it out of the contributor list.
    assert to_dto(_track(contributors=[{"name": "Pharrell Williams"}]))["artist"] == \
        "Daft Punk, Pharrell Williams"


def test_album_tracks_keep_the_album_they_came_from():
    # An album's own tracks arrive with no nested album, so the record has to be put back or every
    # row of an album view loses its name and its cover.
    record = {"id": 302127, "title": "Discovery", "cover_xl": "xl.jpg"}
    bare = [{"id": 3135556, "title": "One More Time", "duration": 320, "artist": {"name": "Daft Punk"}}]

    dto = to_dto(with_album(record, bare)[0])
    assert dto["album"] == "Discovery"
    assert dto["thumbnailUrl"] == "xl.jpg"

    # A track that did carry one keeps it.
    assert to_dto(with_album(record, [_track()])[0])["album"] == "Discovery"


def test_album_search_keeps_only_that_artists_record():
    def hit(artist, title):
        return {"artist": {"name": artist}, "title": title}

    namesake = hit("Data Punk", "Discovery")
    remaster = hit("Daft Punk", "Discovery (Remastered)")
    exact = hit("Daft Punk", "Discovery")

    # Another artist's record never stands in, however high Deezer ranked it.
    assert main._album_in([namesake, exact], "daft punk", "Discovery") is exact
    # An exact title beats one that only holds it; one that holds it beats nothing.
    assert main._album_in([remaster, exact], "Daft Punk", "discovery") is exact
    assert main._album_in([namesake, remaster], "Daft Punk", "Discovery") is remaster
    assert main._album_in([namesake, hit("Daft Punk", "Homework")], "Daft Punk", "Discovery") is None


def test_duration_is_the_timespan_gaida_parses():
    assert duration(0) == "00:00:00"
    assert duration(224) == "00:03:44"
    assert duration(3661) == "01:01:01"
    assert duration(90061) == "1.01:01:01"
    # Deezer occasionally omits it; a missing duration is zero, never a crash.
    assert duration(None) == "00:00:00"
    assert duration(-5) == "00:00:00"


# ── the vendored crypto ─────────────────────────────────────────────────────────────────────────

def test_blowfish_key_matches_upstream():
    """
    The key derivation is the one piece of streamrip that fails silently: a wrong key decrypts to
    noise, which is a file that downloads, caches and plays as static. Checked against the derivation
    spelled out rather than against the implementation, so a typo in either shows up here.
    """
    import hashlib

    digest = hashlib.md5(TRACK.encode()).hexdigest()
    expected = bytes(ord(a) ^ ord(b) ^ ord(c)
                     for a, b, c in zip(digest[:16], digest[16:], "g4el58wc0zvf9na1"))

    assert stream.blowfish_key(TRACK) == expected
    assert len(stream.blowfish_key(TRACK)) == 16


def test_decrypt_leaves_the_clear_stride_alone():
    """
    Deezer encrypts the first 2048 bytes of every 6144 and a short tail not at all. Round-tripping
    real Blowfish over a synthetic file proves the strides line up -- an off-by-one here corrupts one
    block in three of every song.
    """
    from Cryptodome.Cipher import Blowfish

    key = stream.blowfish_key(TRACK)
    plain = bytes(index % 251 for index in range(6144 + 2048 + 100))

    encrypted = bytearray(plain)
    for start in (0, 6144):
        cipher = Blowfish.new(key, Blowfish.MODE_CBC, b"\x00\x01\x02\x03\x04\x05\x06\x07")
        encrypted[start:start + 2048] = cipher.encrypt(plain[start:start + 2048])

    assert stream._decrypt(TRACK, bytes(encrypted)) == plain


def test_builds_an_lrc_from_deezers_timed_lines():
    assert stream._lrc([
        {"lrc_timestamp": "[00:04.20]", "line": "One more time"},
        {"lrc_timestamp": "[00:08.00]", "line": ""},        # a gap, and it keeps its timestamp
        {"milliseconds": "213000", "duration": "0"},        # the end marker, which has no timestamp
    ]) == "[00:04.20]One more time\n[00:08.00]\n"

    assert stream._lrc([]) is None and stream._lrc(None) is None


# ── cache ───────────────────────────────────────────────────────────────────────────────────────

def _dto(name="Harder, Better, Faster, Stronger"):
    return {"name": name, "artist": "Daft Punk", "album": "Discovery",
            "duration": "00:03:44", "thumbnailUrl": "xl.jpg"}


def test_cache_upgrade_replaces_the_old_format():
    """A promote must not leave the MP3 behind — two copies of every promoted track is the cache cap."""
    with tempfile.TemporaryDirectory() as directory:
        songs = cache.Cache(directory, cache.MAX_BYTES_DEFAULT)
        songs.store(TRACK, b"x" * 10, stream.MP3, _dto())
        assert (Path(directory) / f"{TRACK}.mp3").exists()

        entry = songs.store(TRACK, b"y" * 20, stream.FLAC, _dto())
        assert entry.format == stream.FLAC
        assert not (Path(directory) / f"{TRACK}.mp3").exists()
        assert (Path(directory) / f"{TRACK}.flac").read_bytes() == b"y" * 20
        assert songs.stats() == (1, 20)


def test_cache_evicts_oldest_first():
    with tempfile.TemporaryDirectory() as directory:
        songs = cache.Cache(directory, 25)
        for index, track in enumerate(("1", "2", "3")):
            songs.store(track, b"z" * 10, stream.MP3, _dto())

        # 30 bytes stored against a 25-byte cap, so exactly the oldest one goes.
        assert songs.get("1") is None
        assert songs.get("2") is not None and songs.get("3") is not None
        assert not (Path(directory) / "1.mp3").exists()
        assert not (Path(directory) / "1.json").exists()


def test_cache_survives_a_restart():
    with tempfile.TemporaryDirectory() as directory:
        cache.Cache(directory, cache.MAX_BYTES_DEFAULT).store(TRACK, b"x" * 10, stream.FLAC, _dto())

        reopened = cache.Cache(directory, cache.MAX_BYTES_DEFAULT)
        entry = reopened.get(TRACK)
        assert entry is not None and entry.format == stream.FLAC
        assert entry.to_dto()["id"] == f"deezer://{TRACK}"

        # A sidecar whose audio was deleted underneath us is not a cache hit, and does not linger.
        (Path(directory) / f"{TRACK}.flac").unlink()
        again = cache.Cache(directory, cache.MAX_BYTES_DEFAULT)
        assert again.get(TRACK) is None
        assert not (Path(directory) / f"{TRACK}.json").exists()


def test_cache_recent_caps_newest_first_and_0_lifts_the_cap():
    with tempfile.TemporaryDirectory() as directory:
        songs = cache.Cache(directory, cache.MAX_BYTES_DEFAULT)
        for track in ("1", "2", "3"):
            songs.store(track, b"z" * 10, stream.MP3, _dto())

        assert [entry.id for entry in songs.recent(2)] == ["3", "2"]
        assert [entry.id for entry in songs.recent(0)] == ["3", "2", "1"]


_JPEG = b"\xff\xd8\xff" + b"j" * 500
"""A stand-in cover: what matters is that the bytes come back out of the file unchanged."""

_MP3 = b"\xff\xfb" + b"x" * 100
"""Enough of an MP3 frame header for mutagen to write a tag in front of. Nothing decodes it."""


def _flac_stub() -> bytes:
    """
    The smallest thing mutagen will open as a FLAC: the magic and a STREAMINFO block, no audio frames.

    A real file would be a fixture, and these tests are about the picture block and the comments, both
    of which live in front of the first frame.
    """
    streaminfo = struct.pack(">HH", 4096, 4096) + b"\x00" * 6 + b"\x0a\xc4\x42\xf0" + b"\x00" * 20
    return b"fLaC" + bytes([0x80, 0, 0, 34]) + streaminfo


def test_tags_go_into_the_mp3():
    with tempfile.TemporaryDirectory() as directory:
        songs = cache.Cache(directory, cache.MAX_BYTES_DEFAULT)
        entry = songs.store(TRACK, _MP3, stream.MP3, _dto(), _JPEG, "One more time")

        tag = ID3(Path(directory) / f"{TRACK}.mp3")
        assert tag.getall("APIC")[0].data == _JPEG
        assert tag.getall("USLT")[0].text == "One more time"
        assert str(tag["TIT2"]) == "Harder, Better, Faster, Stronger"
        assert str(tag["TALB"]) == "Discovery"

        # The budget counts what is on disk: a cover is most of a file this size, and a cache that
        # counted the pre-tag length would overrun its cap by every cover it ever wrote.
        assert entry.bytes == (Path(directory) / f"{TRACK}.mp3").stat().st_size > 500


def test_tags_go_into_the_flac():
    with tempfile.TemporaryDirectory() as directory:
        songs = cache.Cache(directory, cache.MAX_BYTES_DEFAULT)
        songs.store(TRACK, _flac_stub(), stream.FLAC, _dto(), _JPEG, "One more time")

        audio = FLAC(Path(directory) / f"{TRACK}.flac")
        assert audio.pictures[0].data == _JPEG and audio.pictures[0].type == 3
        assert audio["LYRICS"] == ["One more time"] and audio["UNSYNCEDLYRICS"] == ["One more time"]
        assert audio["ARTIST"] == ["Daft Punk"]


def test_the_cache_no_longer_writes_an_lrc():
    """stih owns every lyrics file now. The plain block still reaches the tags -- that is the file's own."""
    with tempfile.TemporaryDirectory() as directory:
        songs = cache.Cache(directory, cache.MAX_BYTES_DEFAULT)
        songs.store(TRACK, _MP3, stream.MP3, _dto(), _JPEG, "One more time")

        assert not (Path(directory) / f"{TRACK}.lrc").exists()
        assert ID3(Path(directory) / f"{TRACK}.mp3").getall("USLT")[0].text == "One more time"


def test_a_legacy_lrc_is_cleared_out():
    """Files an older version of this pod wrote are still in deployed volumes; eviction takes them."""
    with tempfile.TemporaryDirectory() as directory:
        songs = cache.Cache(directory, cache.MAX_BYTES_DEFAULT)
        songs.store(TRACK, _MP3, stream.MP3, _dto(), _JPEG, "One more time")

        legacy = Path(directory) / f"{TRACK}.lrc"
        legacy.write_text("[00:04.20]One more time\n", encoding="utf-8")

        assert songs.remove(TRACK) and not legacy.exists()


# ── registering with stih ───────────────────────────────────────────────────────────────────────

class _Posted:
    """One captured POST, standing in for requests.post. ``status`` drives raise_for_status."""

    def __init__(self, status: int = 200, error: Exception | None = None) -> None:
        self.status = status
        self.error = error
        self.calls: list[tuple[str, dict]] = []

    def __call__(self, url, json=None, timeout=None):
        self.calls.append((url, json))
        if self.error is not None:
            raise self.error

        return self

    def raise_for_status(self):
        if self.status >= 400:
            raise RuntimeError(f"stih answered {self.status}")


def _register(lyrics: stream.Lyrics, posted: _Posted, url: str = "http://stih:8080") -> _Posted:
    """Runs one registration against a stubbed transport, with STIH_URL as given."""
    original_post, original_url = main.requests.post, main.STIH_URL
    main.requests.post, main.STIH_URL = posted, url
    try:
        asyncio.run(main._register_lyrics(TRACK, lyrics))
    finally:
        main.requests.post, main.STIH_URL = original_post, original_url

    return posted


def test_a_download_with_lyrics_registers_them_once():
    posted = _register(stream.Lyrics("One more time", "[00:04.20]One more time\n"), _Posted())

    assert len(posted.calls) == 1
    url, body = posted.calls[0]
    assert url == "http://stih:8080/register"
    assert body == {"id": f"deezer://{TRACK}", "source": "Deezer",
                    "text": "One more time", "lrc": "[00:04.20]One more time\n"}


def test_a_track_with_no_lyrics_costs_no_request():
    assert _register(stream.NO_LYRICS, _Posted()).calls == []


def test_an_unset_stih_url_posts_nothing():
    assert _register(stream.Lyrics("One more time", None), _Posted(), url="").calls == []


def test_a_failing_stih_does_not_fail_the_download():
    """The whole point of fire-and-forget: the audio is cached either way."""
    assert len(_register(stream.Lyrics("One more time", None), _Posted(status=500)).calls) == 1
    assert len(_register(stream.Lyrics("One more time", None),
                         _Posted(error=TimeoutError("stih timed out"))).calls) == 1


def test_a_file_that_cannot_be_tagged_is_still_cached():
    """Ten bytes are not a FLAC stream and mutagen says so. The download is the part worth keeping."""
    with tempfile.TemporaryDirectory() as directory:
        songs = cache.Cache(directory, cache.MAX_BYTES_DEFAULT)
        entry = songs.store(TRACK, b"x" * 10, stream.FLAC, _dto(), _JPEG, "One more time")

        assert entry.bytes == 10 and songs.get(TRACK) is not None
        assert (Path(directory) / f"{TRACK}.flac").read_bytes() == b"x" * 10


def test_cache_ignores_a_torn_sidecar():
    with tempfile.TemporaryDirectory() as directory:
        (Path(directory) / "9.json").write_text("{not json")
        (Path(directory) / "8.json").write_text(json.dumps({"id": "8"}))  # missing every other field

        songs = cache.Cache(directory, cache.MAX_BYTES_DEFAULT)
        assert songs.stats() == (0, 0)


if __name__ == "__main__":
    for name, test in sorted(dict(globals()).items()):
        if name.startswith("test_"):
            test()
            print(f"  {name} ok")

    print("selftest OK")
