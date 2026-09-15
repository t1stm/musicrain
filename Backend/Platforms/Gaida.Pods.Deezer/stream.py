"""
Turning a Deezer track ID into decrypted audio bytes.

Vendored from streamrip (https://github.com/nathom/streamrip, GPL-3.0) rather than depended on: the
part of it this pod needs is the two functions below, and the package itself pulls in rich, click,
Pillow, m3u8, appdirs and a TOML config object for a CLI nothing here runs. The originals are
``DeezerClient.get_downloadable`` and ``DeezerClient._get_encrypted_file_url`` in
``streamrip/client/deezer.py``, and ``DeezerDownloadable._download`` /
``_generate_blowfish_key`` / ``_decrypt_chunk`` in ``streamrip/client/downloadable.py``.

Everything here is synchronous and blocking -- deezer-py is a ``requests`` wrapper. :mod:`main` runs
it through ``asyncio.to_thread`` so the event loop keeps serving cache hits while a download runs.
"""

import binascii
import hashlib
import logging
import re
import threading
from dataclasses import dataclass

import deezer
import requests
from Cryptodome.Cipher import AES, Blowfish

log = logging.getLogger("gaida.deezer")

MP3 = "mp3"
FLAC = "flac"

_QUALITIES = [
    # Deezer's own format name, the FILESIZE_* field that says whether it exists, our extension.
    ("MP3_128", "FILESIZE_MP3_128", MP3),
    ("MP3_320", "FILESIZE_MP3_320", MP3),
    ("FLAC", "FILESIZE_FLAC", FLAC),
]

_MP3_320 = 1
_FLAC = 2

CONTENT_TYPES = {MP3: "audio/mpeg", FLAC: "audio/flac"}

_ENCRYPTED = re.compile("/m(?:obile|edia)/")
"""A CDN path with this in it hands back Blowfish-encrypted bytes. Upstream's own test."""

_BLOWFISH_SECRET = "g4el58wc0zvf9na1"
_ENCRYPTED_URL_KEY = b"jo6aey6haid2Teih"

_CHUNK = 2048
_STRIDE = 3 * _CHUNK
"""Deezer encrypts the first 2048 bytes of every 6144 and leaves the rest alone."""


class NotStreamable(Exception):
    """Deezer will not hand over this track: no licence, wrong country, or nothing left to fall back to."""


@dataclass(frozen=True)
class Download:
    """One finished download: the decrypted bytes and which of the two formats they are."""

    data: bytes
    format: str


@dataclass(frozen=True)
class Lyrics:
    """One track's words: the plain block that goes in the tag, and the timed lines that go beside it."""

    text: str | None
    lrc: str | None


NO_LYRICS = Lyrics(None, None)
"""What every refusal answers -- no ARL, no lyrics for this track, a gateway that has changed."""


class Client:
    """
    The logged-in Deezer client, or an anonymous one.

    Anonymous is a supported state, not a broken one: ``api`` is Deezer's public REST API and needs no
    credentials at all, so search, resolve and playlist work with no ARL configured. Only :meth:`download`
    needs the login, because ``get_track_url`` signs its request with the account's licence token.
    """

    def __init__(self, arl: str | None) -> None:
        self._arl = (arl or "").strip()
        self._client = deezer.Deezer()
        # deezer-py is one requests.Session shared by api and gw, so every call through it is
        # serialised. The work behind the lock is network-bound and this pod is one operator's
        # worth of traffic; ponytail: a client pool is the upgrade if that ever stops being true.
        self._lock = threading.Lock()
        self._logged_in = False

    @property
    def can_download(self) -> bool:
        """Whether an ARL was configured at all. Says nothing about whether it still works."""
        return bool(self._arl)

    @property
    def api(self):
        """Deezer's public REST API. No credentials, no login, no lock -- see :meth:`call`."""
        return self._client.api

    def call(self, function, *args, **kwargs):
        """One deezer-py call, serialised against every other one on the shared session."""
        with self._lock:
            return function(*args, **kwargs)

    def download(self, track_id: str, prefer_flac: bool) -> Download:
        """
        The decrypted audio for one track.

        :param prefer_flac: ask for quality 2. Falls back through 320 and 128 when the track or the
            account cannot do it, which is streamrip's ``lower_quality_if_not_available``.
        :raises NotStreamable: no ARL, or Deezer refused every format.
        """
        if not self._arl:
            raise NotStreamable("No DEEZER_ARL is configured, so this pod has no audio to hand over.")

        with self._lock:
            try:
                return self._download(track_id, prefer_flac)
            except NotStreamable:
                raise
            except Exception as error:
                # An ARL session expires; the symptom is a gateway error rather than a 401, so a
                # single forced re-login is cheaper than trying to tell the causes apart.
                log.warning("Deezer download of %s failed (%s), logging in again", track_id, error)
                self._logged_in = False
                return self._download(track_id, prefer_flac)

    def lyrics(self, track_id: str) -> Lyrics:
        """
        Everything Deezer has to say about one track's words.

        ``song.getLyrics`` is a gateway call, so it needs the same login the download does -- which is
        why this is only ever asked after one. A track with no lyrics is an error there rather than an
        empty body, and it is the common answer, so every failure is the same quiet :data:`NO_LYRICS`.
        """
        if not self._arl:
            return NO_LYRICS

        with self._lock:
            try:
                self._login()
                found = self._client.gw.get_track_lyrics(track_id) or {}
            except Exception as error:
                log.info("Deezer has no lyrics for %s (%s)", track_id, error)
                return NO_LYRICS

        return Lyrics(found.get("LYRICS_TEXT") or None, _lrc(found.get("LYRICS_SYNC_JSON")))

    # ── everything below runs under self._lock ──────────────────────────────────────────────────

    def _download(self, track_id: str, prefer_flac: bool) -> Download:
        self._login()

        url, extension = self._url(track_id, _FLAC if prefer_flac else _MP3_320)
        response = self._client.session.get(url, allow_redirects=True, stream=True, timeout=60)
        response.raise_for_status()
        data = response.content

        # Upstream's check: Deezer answers a refusal with a short JSON body under the same 200.
        if len(data) < 20_000 and data.lstrip()[:1] in (b"{", b"["):
            raise NotStreamable(f"Deezer refused the audio for {track_id}: {data[:200]!r}")

        return Download(_decrypt(track_id, data) if _ENCRYPTED.search(url) else data, extension)

    def _login(self) -> None:
        if self._logged_in:
            return

        if not self._client.login_via_arl(self._arl):
            raise NotStreamable("Deezer rejected DEEZER_ARL. It has expired or was copied wrong.")

        self._logged_in = True
        log.info("Logged in to Deezer")

    def _url(self, track_id: str, quality: int, retried: bool = False) -> tuple[str, str]:
        """
        A CDN URL for the best format at or below ``quality``, and the extension it will be in.

        Vendored from ``DeezerClient.get_downloadable``. The two error paths matter as much as the
        happy one: a licence error means the account cannot do this format, and a geolocation error
        means this recording is not available here but Deezer's own ``FALLBACK`` may be.
        """
        info = self._client.gw.get_track(track_id)

        while quality > 0 and not int(info.get(_QUALITIES[quality][1]) or 0):
            log.info("Deezer has no %s for %s, dropping a quality", _QUALITIES[quality][0], track_id)
            quality -= 1

        format_name, size_field, extension = _QUALITIES[quality]
        if not int(info.get(size_field) or 0):
            raise NotStreamable(f"Deezer lists no downloadable format for {track_id}.")

        try:
            url = self._client.get_track_url(info["TRACK_TOKEN"], format_name)
        except deezer.WrongLicense:
            if quality == 0:
                raise NotStreamable(
                    f"The Deezer account cannot stream any format of {track_id}."
                ) from None

            # Not a fallback down the list on its own: the account's ceiling is what was hit, and the
            # next quality down is exactly what it is allowed to have.
            log.info("The Deezer account cannot stream %s; trying the next quality down", format_name)
            return self._url(track_id, quality - 1, retried)
        except deezer.WrongGeolocation:
            fallback = (info.get("FALLBACK") or {}).get("SNG_ID")
            if retried or not fallback:
                raise NotStreamable(f"Deezer does not offer {track_id} in this country.") from None

            log.info("Deezer offers %s here instead of %s", fallback, track_id)
            return self._url(str(fallback), quality, retried=True)

        if url is None:
            url = _encrypted_url(track_id, info["MD5_ORIGIN"], info["MEDIA_VERSION"])

        return url, extension


def fetch_artwork(url: str | None) -> bytes | None:
    """
    The cover image behind a DTO's ``thumbnailUrl``, or ``None`` when there is nothing to fetch.

    A plain ``requests.get`` rather than the client's session: this is a CDN image over a URL the public
    API already handed us, so it touches none of deezer-py's shared state and needs neither the login
    nor the lock that serialises every call that does.
    """
    if not url:
        return None

    try:
        response = requests.get(url, timeout=15)
        response.raise_for_status()
        return response.content or None
    except Exception:
        log.warning("Could not fetch the Deezer cover at %s", url, exc_info=True)
        return None


def _lrc(lines: list[dict] | None) -> str | None:
    """
    Deezer's timed lines as the contents of an ``.lrc`` file, or ``None`` when it sent none.

    Every entry carries its own bracketed ``lrc_timestamp``, so this is a join rather than a format. The
    entries with no words are Deezer's own gaps and are kept: a timestamped empty line is what clears a
    player's display between verses. The trailing entry that carries only a duration has no timestamp at
    all, and is the one thing dropped.
    """
    if not lines:
        return None

    timed = [f"{line.get('lrc_timestamp')}{line.get('line') or ''}"
             for line in lines if line.get("lrc_timestamp")]

    return "\n".join(timed) + "\n" if timed else None


def _encrypted_url(track_id: str, track_hash: str, media_version: str) -> str:
    """
    The legacy mobile CDN path, for when ``get_track_url`` has nothing to say. Vendored verbatim from
    ``DeezerClient._get_encrypted_file_url``; the key and the layout are Deezer's, not ours.
    """
    url_bytes = b"\xa4".join((
        track_hash.encode(),
        b"1",  # format number: the legacy path only ever serves this one
        str(track_id).encode(),
        str(media_version).encode(),
    ))

    info = bytearray(hashlib.md5(url_bytes).hexdigest().encode())
    info.extend(b"\xa4")
    info.extend(url_bytes)
    info.extend(b"\xa4")
    info.extend(b"." * (16 - len(info) % 16))  # AES-ECB takes whole blocks

    path = binascii.hexlify(AES.new(_ENCRYPTED_URL_KEY, AES.MODE_ECB).encrypt(info)).decode()
    return f"https://e-cdns-proxy-{track_hash[0]}.dzcdn.net/mobile/1/{path}"


def blowfish_key(track_id: str) -> bytes:
    """The per-track Blowfish key, from ``DeezerDownloadable._generate_blowfish_key``."""
    digest = hashlib.md5(str(track_id).encode()).hexdigest()
    return "".join(
        chr(ord(a) ^ ord(b) ^ ord(c)) for a, b, c in zip(digest[:16], digest[16:], _BLOWFISH_SECRET)
    ).encode()


def _decrypt(track_id: str, data: bytes) -> bytes:
    """
    One encrypted Deezer file, decrypted in place of upstream's chunk-by-chunk write.

    Every 6144-byte stride has its first 2048 bytes Blowfish-CBC encrypted and the remaining 4096 in
    the clear; a trailing stride shorter than 2048 bytes is never encrypted. This is why the pod
    buffers the body rather than streaming it through -- so does streamrip, for the same reason.
    """
    key = blowfish_key(track_id)
    out = bytearray(data)

    for start in range(0, len(out), _STRIDE):
        block = bytes(out[start:start + _CHUNK])
        if len(block) < _CHUNK:
            break

        cipher = Blowfish.new(key, Blowfish.MODE_CBC, b"\x00\x01\x02\x03\x04\x05\x06\x07")
        out[start:start + _CHUNK] = cipher.decrypt(block)

    return bytes(out)
