"""
Everything a downloaded file carries besides its audio: the names, the cover and the lyrics.

Deezer's CDN serves a bare stream -- no ID3 header, no Vorbis comment, no picture -- so a file that
leaves this pod untagged is one whose cover exists only in the DTO beside it. The mapping is
streamrip's ``metadata/tagger.py`` (``USLT``/``APIC`` for MP3, uppercase Vorbis keys and a ``Picture``
block for FLAC, ID3v2.3 on save), cut to the fields this pod knows.

The timed lyrics are not here: FLAC has no frame for them, so they go beside the audio as an ``.lrc``
instead -- see :meth:`cache.Cache.store`.

Nothing here raises. A tag that will not write is a worse file, not a failed download.
"""

import logging
from pathlib import Path
from typing import Any

from mutagen.flac import FLAC, Picture
from mutagen.id3 import APIC, ID3, TALB, TIT2, TPE1, USLT

log = logging.getLogger("gaida.deezer")

FLAC_FORMAT = "flac"
"""
Matches :data:`stream.FLAC`, spelled again rather than imported: this module is mutagen and a path, and
importing the download client for one string would drag deezer-py and Blowfish in behind it.
"""

_FRONT_COVER = 3
"""The picture type both formats spell the same way."""

_UTF8 = 3
"""ID3's encoding number for UTF-8. Anything else mangles half the catalogue's titles."""


def embed(path: Path, audio_format: str, dto: dict[str, Any], cover: bytes | None,
          lyrics: str | None) -> None:
    """Writes the names, the artwork and the lyrics into one freshly downloaded file, in place."""
    try:
        if audio_format == FLAC_FORMAT:
            _flac(path, dto, cover, lyrics)
        else:
            _mp3(path, dto, cover, lyrics)
    except Exception:
        # Deezer's own bytes are what they are: a container mutagen will not open is a file that still
        # plays, and losing the download over its tags would be the worse trade.
        log.warning("Could not tag %s", path.name, exc_info=True)


def _mp3(path: Path, dto: dict[str, Any], cover: bytes | None, lyrics: str | None) -> None:
    tag = ID3()
    tag.add(TIT2(encoding=_UTF8, text=dto.get("name") or ""))
    tag.add(TPE1(encoding=_UTF8, text=dto.get("artist") or ""))
    if dto.get("album"):
        tag.add(TALB(encoding=_UTF8, text=dto["album"]))

    if lyrics:
        # ``lang`` is required by the frame and Deezer never says which language the lines are in, so
        # "eng" is a placeholder rather than a claim. Players key on the frame, not on this.
        tag.add(USLT(encoding=_UTF8, lang="eng", desc="", text=lyrics))

    if cover:
        tag.add(APIC(encoding=_UTF8, mime=_mime(cover), type=_FRONT_COVER, desc="Cover", data=cover))

    # A download with no ID3 header at all is the ordinary case, so this writes a tag rather than
    # updating one. v2_version=3 is streamrip's own save and what TagLib on the library side reads best.
    tag.save(path, v2_version=3)


def _flac(path: Path, dto: dict[str, Any], cover: bytes | None, lyrics: str | None) -> None:
    audio = FLAC(path)
    audio["TITLE"] = dto.get("name") or ""
    audio["ARTIST"] = dto.get("artist") or ""
    if dto.get("album"):
        audio["ALBUM"] = dto["album"]

    if lyrics:
        # Two spellings of one block: LYRICS is what Vorbis players look for, UNSYNCEDLYRICS is where a
        # converted ID3 tag lands.
        audio["LYRICS"] = lyrics
        audio["UNSYNCEDLYRICS"] = lyrics

    if cover:
        picture = Picture()
        picture.type = _FRONT_COVER
        picture.mime = _mime(cover)
        picture.data = cover
        audio.add_picture(picture)

    audio.save()


def _mime(image: bytes) -> str:
    """
    Deezer serves JPEG, but the byte that says so is free to check and a mislabelled picture is the kind
    of thing every reader on the other side handles differently.
    """
    return "image/png" if image.startswith(b"\x89PNG") else "image/jpeg"
