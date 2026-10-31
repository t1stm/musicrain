"""lirika: line-synced lyrics generated from the audio.

    uv run lirika.py AUDIO... --lang bg|en [--text FILE] [--no-lookup] [--out FILE|-] [--force]
                              [--register] [--keep] [--by NAME]

Writes AUDIO's .lrc beside it, or hands it to stih with --register. Exit code 0 when everything was
written cleanly, 2 when something was written but some lines need a look, 1 on a failure.

Setup and the details: README.md.
"""

from __future__ import annotations

import argparse
import hashlib
import re
import shutil
import subprocess
import sys
from dataclasses import dataclass, field, replace
from functools import cache
from pathlib import Path
from typing import NamedTuple

import numpy as np

import sources

VERSION = "0.1"
RATE = 16_000
CACHE = Path.home() / ".cache/lirika"
SEPARATOR = "mel_band_roformer_karaoke_becruily.ckpt"  # lead vocal against everything else
VOCALS = "model_bs_roformer_ep_317_sdr_12.9755.ckpt"  # every voice against the instruments
# The model's YAML asks for 8 overlapping passes. 2 is ~4x faster and the lead stem differs by -28 dB.
# Autocast stays off: in fp16 this RoFormer produces non-finite samples.
OVERLAP = 2
MMS = "MahmoudAshraf/mms-300m-1130-forced-aligner"
# Bulgarian XLS-R 300M, Cyrillic letters, normalized input. On the 20 Bulgarian corpus songs it beat MMS:
# 73.5% of lines within 200 ms against 70.4%, mean song MAE 0.31 s against 0.40 s, 1 hallucinated line
# against 6.
BULGARIAN = "DrishtiSharma/wav2vec2-large-xls-r-300m-bg-d2"


@dataclass(frozen=True)
class Tuning:
    """Every knob the evaluation turns. The defaults are the plan's starting points until eval.py says otherwise."""

    activity_db: float = 35.0  # a frame is sung within this many dB of the song's 95th-percentile frame
    merge_gap: float = 0.3  # seconds; shorter silences inside a phrase are bridged
    min_region: float = 0.2
    pad: float = 0.1
    snap: float = 0.15  # a line start moves to a vocal onset at most this far away
    line_gap: float = 0.4  # transcribe mode: a pause this long starts a new line
    max_words: int = 12
    max_line: float = 6.0
    min_line: float = 0.3
    gap_marker: float = 3.0  # an empty [mm:ss.xx] marks a silence at least this long
    low_score: float = -2.5  # mean CTC log-probability per frame below this is flagged (swept: -1.5 flagged half of English)
    min_coverage: float = 0.7  # aligned lines must cover this much of the sung time
    min_sung_lines: int = 4  # transcribe mode: fewer lines than this is an instrumental, not a song
    instrumental_score: float = -3.0  # ...and so is a median line score below this
    asr_input: str = "mix"  # "mix" or "lead" -- R4 in the plan
    # Align mode: "lead", "vocals" or "mix". The karaoke split drops duet partners and second voices, whose
    # lines then get forced into the wrong place; on the corpus "vocals" had the fewest large misses.
    align_on: str = "vocals"
    # Share of the draft's 4-grams a found text must contain. On the corpus the right texts scored 0.45 and
    # up, the same-name wrong songs 0.17 and down.
    lookup_threshold: float = 0.35
    # Map mode: transcribe what is sung, then give each sung phrase the words of the lyrics line it matches.
    # The text's line order, missing repeats and other versions stop mattering; only its words are used.
    map_text: bool = True
    map_match: float = 0.55  # how alike a sung phrase and a lyrics line must be, romanized, to be that line
    # Whisper drops repeats and whole lines. Lead singing no mapped line covers, if at least this long, is
    # offered every lyrics line; the best one is kept when its mean word score reaches fill_score.
    settle_lines: bool = True
    fill_gaps: bool = True
    fill_fragments: bool = False  # may a filled line replace an unmatched Whisper fragment?
    fill_min: float = 1.5
    # On "Пробвай се" (normalized input) the right lines had margins of -0.15 to -0.26, wrong lines and
    # places with no lead singing -0.36 and below.
    fill_margin: float = -0.3


@dataclass(frozen=True)
class Language:
    whisper: str
    mms: str
    asr: str
    blocklist: tuple[str, ...]
    sources: tuple[str, ...]
    aligner: str = MMS
    normalize: bool = False  # zero-mean, unit-variance aligner input
    # Words Whisper signs off with over music. Unlike the blocklist, which drops a window, these only drop
    # an unmatched run of a mapped song: it is not in the lyrics, so nothing real is lost.
    signoffs: tuple[str, ...] = ()


# Whisper's well-known inventions over silence and music: subtitle credits and channel sign-offs.
_BG_BLOCK = (r"абонира", r"субтит", r"благодаря (ви )?за (гледането|вниманието)", r"amara\.org", r"превод и")
_BG_SIGNOFFS = (r"благодар", r"абонира", r"субтит", r"превод")
_EN_SIGNOFFS = (r"\bthank", r"subscrib", r"subtitle")
_EN_BLOCK = (r"thanks? (you )?for watching", r"subscribe", r"amara\.org", r"subtitles? by", r"transcri(bed|ption) by")

LANGUAGES = {
    # No Deezer source: its gateway lyrics for tracks nobody plays is the traffic an ARL gets flagged for
    # (Gaida.Pods.Deezer/README.md). English has no site yet; LRCLIB, through stih, already covers it well.
    # BuzzASR: Whisper large-v3 fine-tuned on Bulgarian. On four pilot songs its drafts held 79-87% of the
    # lyrics against 73-87% for stock large-v3, 71-84% for large-v2 and 42-67% for Parakeet-TDT-0.6B-v3.
    "bg": Language("bg", "bul", "BuzzASR/bulgarian", _BG_BLOCK, ("textove",), BULGARIAN, True, _BG_SIGNOFFS),
    "en": Language("en", "eng", "openai/whisper-large-v2", _EN_BLOCK, (), signoffs=_EN_SIGNOFFS),
}


@dataclass
class Line:
    text: str
    start: float
    end: float
    score: float
    flags: list[str] = field(default_factory=list)
    margin: float = 0.0  # see Word.margin


@dataclass
class Result:
    lines: list[Line]
    mode: str  # what the words came from: "text", "txt", "lrc", "online:<site>" or "transcribe"
    lrc: str
    flags: list[str]  # song-level

    @property
    def flagged(self) -> bool:
        return bool(self.flags) or any(line.flags for line in self.lines)


# ── audio ───────────────────────────────────────────────────────────────────────────────────────


def pcm(path: Path, rate: int = RATE) -> np.ndarray:
    """Mono float32 at `rate`, through ffmpeg: WavPack (hybrid too), MP3, Ogg, anything it opens."""
    raw = subprocess.run(
        ["ffmpeg", "-v", "error", "-i", str(path), "-ac", "1", "-ar", str(rate), "-f", "f32le", "-"],
        check=True, capture_output=True).stdout
    return np.frombuffer(raw, np.float32).copy()


def fingerprint(path: Path) -> str:
    digest = hashlib.sha1()
    with path.open("rb") as file:
        while chunk := file.read(1 << 20):
            digest.update(chunk)
    return digest.hexdigest()


class Models:
    """Loaded once per process and kept: a batch pays every model load exactly once."""

    def __init__(self, aligner: str | None = None, normalize: bool | None = None) -> None:
        """
        `aligner` and `normalize` override every language's own (see Language): any CTC model on the
        Hugging Face hub. MMS works on romanized text; a model whose vocabulary is Cyrillic letters gets
        the words as they are written.
        """
        self._override = (aligner, normalize)
        self.aligner_name = aligner or MMS
        self.normalize = bool(normalize)
        self._aligners: dict[str, tuple] = {}
        self._separators: dict[str, object] = {}
        self._whisper: dict[str, tuple] = {}
        # A song's draft depends on the audio and the model only, so an edit-and-regenerate loop reuses it.
        self.drafts: dict[tuple, list] = {}

    def separator(self, model: str):
        if model not in self._separators:
            from audio_separator.separator import Separator

            (CACHE / "separating").mkdir(parents=True, exist_ok=True)
            separator = Separator(
                output_dir=str(CACHE / "separating"), model_file_dir=str(CACHE / "models"),
                output_format="FLAC", log_level=40,
                mdxc_params={"segment_size": 256, "override_model_segment_size": False, "batch_size": 1,
                             "overlap": OVERLAP, "pitch_shift": 0})
            separator.load_model(model_filename=model)
            self._separators[model] = separator
        return self._separators[model]

    def whisper(self, name: str):
        if name not in self._whisper:
            import torch
            from transformers import WhisperForConditionalGeneration, WhisperProcessor

            # One ASR model at a time: a mixed-language batch would otherwise hold two 3.4 GB models.
            self._whisper.clear()
            torch.cuda.empty_cache()
            where, dtype = device()
            if "parakeet" in name:  # NVIDIA's transducer: no language to force, it detects 25 itself
                from transformers import AutoModelForTDT, AutoProcessor

                model = AutoModelForTDT.from_pretrained(name, dtype=dtype).to(where).eval()
                self._whisper[name] = (AutoProcessor.from_pretrained(name), model)
            else:
                model = WhisperForConditionalGeneration.from_pretrained(name, dtype=dtype).to(where).eval()
                self._whisper[name] = (WhisperProcessor.from_pretrained(name), model)
        return self._whisper[name]

    def use(self, language: Language) -> None:
        """The aligner `language` wants, unless the constructor overrode it."""
        aligner, normalize = self._override
        self.aligner_name = aligner or language.aligner
        self.normalize = language.normalize if normalize is None else normalize

    def mms(self):
        """(model, tokenizer, letters) of the current aligner: `letters` is None for a romanizing one."""
        if self.aligner_name not in self._aligners:
            from ctc_forced_aligner import load_alignment_model

            where, dtype = device()
            model, tokenizer = load_alignment_model(where, model_path=self.aligner_name, dtype=dtype)
            alphabet = {key for key in tokenizer.get_vocab() if len(key) == 1 and key.isalpha()}
            cyrillic = any("\u0400" <= letter <= "\u04ff" for letter in alphabet)
            self._aligners[self.aligner_name] = (model, tokenizer, alphabet if cyrillic else None)
        return self._aligners[self.aligner_name]


def stem(song: Path, models: Models, model: str = SEPARATOR, keep: bool = False) -> Path:
    """A model's vocal stem: separated once per file content and model, and cached from then on."""
    directory = CACHE / fingerprint(song)
    voice = directory / f"{'lead' if model == SEPARATOR else 'vocals'}.{model}.flac"
    if voice.exists():
        return voice

    directory.mkdir(parents=True, exist_ok=True)
    mix = directory / "mix.wav"
    if not mix.exists():
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(song), "-ac", "2", "-ar", "44100", str(mix)],
                       check=True)
    separator = models.separator(model)
    written = separator.separate(str(mix), {"Vocals": "voice", "Instrumental": "rest"})
    produced = {Path(name).stem: Path(separator.output_dir) / Path(name).name for name in written}
    shutil.move(produced["voice"], voice)
    _release()
    if keep:
        shutil.move(produced["rest"], directory / f"rest.{model}.flac")
    else:
        produced["rest"].unlink(missing_ok=True)
        mix.unlink()
    return voice


@cache
def device():
    """(device, dtype) the models run in: the GPU in half precision, or the CPU in full when there is no GPU.

    ROCm builds of torch answer to "cuda" too. Half precision stays on the GPU: the CPU's fp16 kernels are
    slow where they exist at all.
    """
    import torch

    return ("cuda", torch.float16) if torch.cuda.is_available() else ("cpu", torch.float32)


def _release() -> None:
    """Hands PyTorch's cached activation memory back after a heavy stage, so the next one starts lean."""
    import torch

    torch.cuda.empty_cache()


def activity(audio: np.ndarray, tuning: Tuning) -> list[tuple[float, float]]:
    """Where the lead stem is sung: an energy gate relative to the song itself (R6 in the plan)."""
    hop = RATE // 50  # 20 ms frames
    frames = len(audio) // hop
    if frames == 0:
        return []
    rms = np.sqrt(np.mean(audio[: frames * hop].reshape(frames, hop) ** 2, axis=1))
    db = 20 * np.log10(rms + 1e-9)
    sung = db > np.percentile(db, 95) - tuning.activity_db

    regions: list[list[float]] = []
    for index in np.flatnonzero(np.diff(np.concatenate(([0], sung.astype(np.int8), [0])))):
        if not regions or len(regions[-1]) == 2:
            regions.append([index * 0.02])
        else:
            regions[-1].append(index * 0.02)

    merged: list[list[float]] = []
    for start, end in regions:
        if merged and start - merged[-1][1] < tuning.merge_gap:
            merged[-1][1] = end
        else:
            merged.append([start, end])

    length = len(audio) / RATE
    return [(max(0.0, start - tuning.pad), min(length, end + tuning.pad))
            for start, end in merged if end - start >= tuning.min_region]


def onsets(audio: np.ndarray, regions: list[tuple[float, float]]) -> np.ndarray:
    import librosa

    found = librosa.onset.onset_detect(y=audio, sr=RATE, hop_length=RATE // 100, backtrack=True, units="time")
    return np.array([time for time in found if inside(time, regions)])


def inside(time: float, regions: list[tuple[float, float]]) -> bool:
    return any(start <= time <= end for start, end in regions)


def overlap(start: float, end: float, regions: list[tuple[float, float]]) -> float:
    """Seconds of [start, end] that lie inside the regions."""
    return sum(max(0.0, min(end, b) - max(start, a)) for a, b in regions)


# ── text ────────────────────────────────────────────────────────────────────────────────────────

_STAMP = re.compile(r"\[(?:\d+:)?\d{1,2}:\d{1,2}(?:[.:]\d{1,3})?\]")
_TAG = re.compile(r"^\s*\[[a-z#]+:.*\]\s*$", re.IGNORECASE)  # .*: a title may have brackets of its own
_LABEL = re.compile(
    r"^\s*[\[(]?\s*(?P<kind>припев|рефрен|chorus|refrain|куплет|verse|bridge|бридж|pre-?chorus|intro|outro|hook|кода)"
    r"\s*\d*\s*(?:[\])]\s*[:.\-–—]?|[:.\-–—]|$|(?=\s*[\[(]?\s*(?:[xх×]\s*\d|\d+\s*[xх×])))\s*", re.IGNORECASE)
# (x2), (х2) with a Cyrillic х, x2, ×2, 2x -- at the end of a line, after a space or a bracket.
_REPEAT = re.compile(r"(?:^|\s+|\s*[\[(]\s*)(?:[xх×]\s*(?P<a>\d+)|(?P<b>\d+)\s*[xх×])\s*[\])]?\s*$", re.IGNORECASE)
_CHORUS = {"припев", "рефрен", "chorus", "refrain"}


def _repeats(words: str) -> tuple[str, int]:
    found = _REPEAT.search(words)
    if not found:
        return words, 1
    return words[: found.start()].rstrip(), int(found["a"] or found["b"])


def normalize(text: str) -> list[str]:
    """
    Lyrics as a site or a person writes them, turned into the lines that are actually sung.

    Labels go, ``(х2)`` repeats its line, and a bare ``Припев`` after the first chorus repeats that
    chorus. Raises ``ValueError`` when the text refers to something it never wrote out: guessing would
    misplace every line after it.
    """
    raw = [_STAMP.sub("", line).strip() for line in text.lstrip("﻿").replace("\r\n", "\n").split("\n")]
    raw = [line for line in raw if not _TAG.match(line)]

    lines: list[str] = []
    chorus: list[str] | None = None  # the first chorus, once written out
    recording = False
    for index, line in enumerate(raw):
        if not line:
            recording = False
            continue

        label = _LABEL.match(line)
        words = line[label.end():].strip() if label else line
        is_chorus = bool(label) and label["kind"].lower() in _CHORUS

        if label and (not words or _REPEAT.fullmatch(words)):
            header = index + 1 < len(raw) and raw[index + 1] != "" and not _LABEL.match(raw[index + 1])
            if header:  # "Припев:" with the chorus under it
                recording = is_chorus and chorus is None
                if recording:
                    chorus = []
            elif is_chorus:  # a bare "Припев" standing for the chorus written out earlier
                if not chorus:
                    raise ValueError(f"'{line}' refers to a chorus the text never writes out")
                lines.extend(chorus * _repeats(words)[1])
                recording = False
            continue

        if label:
            recording = is_chorus and chorus is None
            if recording:
                chorus = []
        if _REPEAT.fullmatch(words):
            raise ValueError(f"'{line}' repeats something, and it is unclear what")

        words, times = _repeats(words)
        words = re.sub(r"\s+", " ", words)
        lines.extend([words] * times)
        if recording:
            chorus.extend([words] * times)

    return lines


# ── alignment ───────────────────────────────────────────────────────────────────────────────────


@dataclass
class Word:
    text: str
    start: float
    end: float
    score: float
    # The forced path against the best path the aligner would take unconstrained, per frame. The right
    # text costs almost nothing; the wrong text fights the audio. A plain score cannot tell the two apart:
    # a few letters of any line find somewhere to sit.
    margin: float = 0.0


def emissions(audio: np.ndarray, models: Models):
    import torch
    from ctc_forced_aligner import generate_emissions

    model, _, _ = models.mms()
    if models.normalize:
        audio = (audio - audio.mean()) / np.sqrt(audio.var() + 1e-7)
    found, stride = generate_emissions(model, torch.from_numpy(audio).to(*device()), batch_size=8)
    return found, stride / 1000  # stride in seconds


def _viterbi(frames, tokens: list[str], tokenizer):
    """
    ctc-forced-aligner's get_alignments with two changes. Its vocabulary-size check is gone: it refuses
    tokenizers with added tokens (<s>, </s>) such as the Bulgarian XLS-R models'. And the Viterbi pass is
    torchaudio's: ctc-forced-aligner's C++ copy sizes its back-pointers as (S+1)*(T-L), writes past them
    when the audio is not much longer than the text, and corrupts the heap -- a segfault in libc later.
    <star> is the column generate_emissions appended last.
    """
    import torch
    import torchaudio.functional
    from ctc_forced_aligner import merge_repeats

    dictionary = {key.lower(): value for key, value in tokenizer.get_vocab().items() if value < frames.shape[1] - 1}
    dictionary["<star>"] = frames.shape[1] - 1
    indices = [dictionary[token] for token in " ".join(tokens).split(" ") if token in dictionary]
    # CTC needs a blank between two equal letters: fewer frames than letters plus those has no path.
    doubled = sum(1 for a, b in zip(indices, indices[1:]) if a == b)
    if not indices or frames.shape[0] < len(indices) + doubled:
        raise RuntimeError("targets length is too long for CTC")
    blank = dictionary.get("<blank>", tokenizer.pad_token_id)
    path, scores = torchaudio.functional.forced_align(
        frames.unsqueeze(0).float().cpu(), torch.tensor([indices], dtype=torch.int32), blank=blank)
    names = {value: key for key, value in dictionary.items()}
    return merge_repeats(path[0].tolist(), names), scores[0].numpy(), names[blank]


def align(words: list[str], frames, stride: float, models: Models, iso: str, offset: float = 0.0) -> list[Word | None]:
    """
    One timing per word, ``None`` for a word with nothing to align (a number, a lone dash). ``<star>``
    sits between every pair of words, so ad-libs and backing vocals are absorbed instead of pulling
    words out of place.
    """
    from ctc_forced_aligner import get_spans, get_uroman_tokens, text_normalize

    _, tokenizer, letters = models.mms()
    if letters is None:
        tokens = [get_uroman_tokens([text_normalize(word.replace("ѝ", "и"), iso)], iso)[0] for word in words]
    else:
        # A Cyrillic model spells the word as written; a Latin word (an English hook) is spelled the way a
        # Bulgarian would write it down.
        from sources import cyrillic

        tokens = [" ".join(letter for letter in cyrillic(word).lower() if letter in letters) for word in words]
    timed = [index for index, token in enumerate(tokens) if token]
    if not timed:
        return [None] * len(words)

    starred: list[str] = []
    for index in timed:
        starred += ["<star>", tokens[index]]
    starred.append("<star>")  # whatever follows the last word is absorbed, not stretched into it

    segments, scores, blank = _viterbi(frames, starred, tokenizer)
    free = frames[:, :-1].max(dim=1).values.float().cpu().numpy()
    out: list[Word | None] = [None] * len(words)
    # Every other span is a word. Its first and last letters, not the span: postprocess_results pads a
    # span back to the middle of the silence before it, which moves a line after a long gap seconds early.
    for index, span in zip(timed, get_spans(starred, segments, blank)[1::2]):
        letters = [segment for segment in span if segment.label != blank]
        first, last = letters[0].start, letters[-1].end + 1
        out[index] = Word(words[index], first * stride + offset, last * stride + offset,
                          float(scores[first:last].mean()), float((scores[first:last] - free[first:last]).mean()))
    return out


def align_lines(lines: list[str], frames, stride: float, models: Models, iso: str, tuning: Tuning,
                raw_words: list | None = None) -> list[Line]:
    """Align mode: the whole text against the whole song, one pass, lines kept as written where they can be."""
    words, owner = [], []
    for number, line in enumerate(lines):
        for word in line.split():
            words.append(word)
            owner.append(number)

    aligned = align(words, frames, stride, models, iso)
    if raw_words is not None:
        raw_words += [(word, f"line {who}") for word, who in zip(aligned, owner) if word is not None]
    out = []
    for number, line in enumerate(lines):
        mine = [word for word, who in zip(aligned, owner) if who == number and word is not None]
        if not mine:
            continue  # nothing sung in it: "* * *", a lone number
        out += shaped(line, mine, tuning)
    return out


def shaped(text: str, words: list[Word], tuning: Tuning) -> list[Line]:
    """
    One written line as it is, unless it is a whole stanza typed on one line -- lyrics sites have those --
    in which case it is broken at its pauses like a transcription.
    """
    # A rap line runs past 12 words; a typed-in stanza past 24. Not by duration: a misaligned line stretched
    # across a gap is long too, and splitting it scatters words into the gap.
    if len(words) > 2 * tuning.max_words:
        return halves(words, tuning)
    return [Line(text, words[0].start, words[-1].end, float(np.mean([word.score for word in words])))]


def halves(words: list[Word], tuning: Tuning) -> list[Line]:
    """
    Too many words for one line, cut at its longest pause, and each half again, until no part has more
    than `max_words`. Cutting at every pause instead turns a recitation's dramatic pauses into one-word
    lines. A cut leaves at least three words on each side where it can.
    """
    if len(words) <= tuning.max_words:
        return [_line(words)]
    pauses = [(words[index + 1].start - words[index].end, index) for index in range(len(words) - 1)]
    _, cut = max([pause for pause in pauses if 2 <= pause[1] <= len(words) - 4] or pauses)
    return halves(words[:cut + 1], tuning) + halves(words[cut + 1:], tuning)


# ── transcription ───────────────────────────────────────────────────────────────────────────────


def windows(regions: list[tuple[float, float]], audio: np.ndarray, longest: float = 30.0) -> list[tuple[float, float]]:
    """Activity regions merged up to 30 s; a single longer region is cut at its quietest 20 ms."""
    merged: list[list[float]] = []
    for start, end in regions:
        if merged and end - merged[-1][0] <= longest:
            merged[-1][1] = end
        else:
            merged.append([start, end])

    out = []
    for start, end in merged:
        while end - start > longest:
            low, high = int((start + longest - 10) * RATE), int((start + longest) * RATE)
            piece = audio[low:high]
            hop = RATE // 50
            energy = np.add.reduceat(piece ** 2, np.arange(0, len(piece), hop))
            cut = (low + int(np.argmin(energy)) * hop) / RATE
            out.append((start, cut))
            start = cut
        out.append((start, end))
    return out


def _repetitive(text: str) -> bool:
    """Whisper stuck in a loop. Songs repeat themselves too, so only a window of almost nothing but repeats."""
    words = re.findall(r"\w+", text.lower())
    return len(words) >= 12 and len(set(words)) / len(words) < 0.25


class Window(NamedTuple):
    start: float
    end: float
    text: str  # what the pipeline uses: empty when the window was dropped
    raw: str  # what Whisper said


def draft(audio: np.ndarray, spans: list[tuple[float, float]], language: Language, models: Models,
          model_name: str | None = None) -> list[Window]:
    """Whisper over each window. Blocklisted and looping windows keep their raw text but use none."""
    import torch

    processor, model = models.whisper(model_name or language.asr)
    torch.manual_seed(0)  # the temperature fallback samples; the same song should transcribe the same way
    out = []
    for first in range(0, len(spans), 8):
        batch = spans[first:first + 8]
        pieces = [audio[int(start * RATE):int(end * RATE)] for start, end in batch]
        if "parakeet" in (model_name or language.asr):
            inputs = processor(pieces, sampling_rate=RATE)
            inputs.to(device()[0], dtype=device()[1])
            with torch.inference_mode():
                ids = model.generate(**inputs, return_dict_in_generate=True).sequences
        else:
            features = processor(pieces, sampling_rate=RATE, return_tensors="pt").input_features
            with torch.inference_mode():
                ids = model.generate(
                    features.to(*device()), language=language.whisper, task="transcribe",
                    condition_on_prev_tokens=False, temperature=(0.0, 0.2, 0.4, 0.6, 0.8, 1.0),
                    compression_ratio_threshold=1.35, logprob_threshold=-1.0, no_speech_threshold=0.5)
        # A batch Whisper judged all silence comes back as an empty float tensor, which decode refuses.
        for (start, end), raw in zip(batch, processor.batch_decode(ids.long(), skip_special_tokens=True)):
            raw = raw.strip()
            dropped = _repetitive(raw) or any(re.search(pattern, raw, re.IGNORECASE) for pattern in language.blocklist)
            out.append(Window(start, end, "" if dropped else raw, raw))
    _release()
    return out


def sung_words(drafted: list[Window], frames, stride: float, regions, models: Models, iso: str,
               raw_words: list | None = None) -> list[list[Word]]:
    """Each window's draft aligned to the audio, words outside the sung regions dropped. One list per window."""
    out = []
    for number, (start, end, text, _) in enumerate(drafted):
        if not text:
            continue
        piece = frames[int(start / stride):int(end / stride)]
        aligned = [word for word in align(text.split(), piece, stride, models, iso, offset=start) if word is not None]
        if raw_words is not None:
            raw_words += [(word, f"window {number}") for word in aligned]
        out.append([word for word in aligned if inside((word.start + word.end) / 2, regions)])
    return out


def transcribe_lines(drafted: list[Window], frames, stride: float, regions, models: Models,
                     iso: str, tuning: Tuning, raw_words: list | None = None) -> list[Line]:
    """Transcribe mode: align each window's draft, drop words outside sung regions, break at pauses."""
    lines: list[Line] = []
    for words in sung_words(drafted, frames, stride, regions, models, iso, raw_words):
        lines += split(words, tuning)

    merged: list[Line] = []
    for line in lines:
        if merged and line.end - line.start < tuning.min_line:
            last = merged[-1]
            merged[-1] = Line(f"{last.text} {line.text}", last.start, line.end, (last.score + line.score) / 2)
        else:
            merged.append(line)
    return merged


def map_lines(words: list[Word], lines: list[str], tuning: Tuning) -> list[Line]:
    """
    The sung words, cut into the lyrics' lines. A dynamic programme over the transcription: every run of
    consecutive words is scored against every line of the text, a matching run takes that line's words and
    the time of its first sung word, and a run that matches nothing keeps Whisper's words and a flag. Lines
    may be used any number of times and in any order -- a chorus written once is sung three times -- with a
    small bonus for following the text's own order, which settles near-ties between similar lines.
    """
    from difflib import SequenceMatcher

    from sources import plain

    if not words:
        return []
    targets = [plain(line) for line in lines]
    sung = [plain(word.text) for word in words]
    longest = max(len(line.split()) for line in lines) * 2 + 2
    follow = 0.3  # bonus, in words' worth of score, for taking the line after the previous one

    # best[i] maps "line of the last run" (-1: unmatched) to (score, start of that run, previous state).
    best: list[dict[int, tuple[float, int, int]]] = [{-1: (0.0, 0, -1)}] + [{} for _ in words]
    for end in range(1, len(words) + 1):
        for length in range(1, min(longest, end) + 1):
            start = end - length
            if not best[start]:
                continue
            chunk = " ".join(sung[start:end])
            before = max(best[start].items(), key=lambda item: item[1][0])
            # Left unmatched, one word at a time: worth nothing, costs nothing.
            if length == 1:
                candidate = (before[1][0], start, before[0])
                if candidate[0] > best[end].get(-1, (-1e9,))[0]:
                    best[end][-1] = candidate
            matcher = SequenceMatcher(None, chunk, "", autojunk=False)
            for number, target in enumerate(targets):
                matcher.set_seq2(target)
                if matcher.real_quick_ratio() < tuning.map_match or matcher.quick_ratio() < tuning.map_match:
                    continue
                similarity = matcher.ratio()
                if similarity < tuning.map_match:
                    continue
                gain = length * (similarity - tuning.map_match)
                previous = best[start].get(number - 1)
                if previous and previous[0] + follow >= before[1][0]:
                    candidate = (previous[0] + gain + follow, start, number - 1)
                else:
                    candidate = (before[1][0] + gain, start, before[0])
                if candidate[0] > best[end].get(number, (-1e9,))[0]:
                    best[end][number] = candidate

    # Walk back from the best final state.
    runs, end = [], len(words)
    state = max(best[end].items(), key=lambda item: item[1][0])[0]
    while end > 0:
        _, start, previous = best[end][state]
        runs.append((start, end, state))
        end, state = start, previous
    runs.reverse()

    out: list[Line] = []
    loose: list[Word] = []
    for start, end, number in runs:
        chunk = words[start:end]
        if number < 0:
            loose += chunk
            continue
        if loose:
            out.append(_loose(loose))
            loose = []
        out.append(Line(lines[number], chunk[0].start, chunk[-1].end, float(np.mean([word.score for word in chunk]))))
    if loose:
        out.append(_loose(loose))
    return out


def place(text: str, low: float, high: float, frames, stride: float, models: Models, iso: str) -> Line | None:
    """One lyrics line aligned by its own words somewhere between `low` and `high`, or None."""
    words = text.split()
    piece = frames[int(low / stride):int(high / stride)]
    if len(piece) < 10:
        return None
    try:
        timed = [word for word in align(words, piece, stride, models, iso, offset=low) if word is not None]
    except (RuntimeError, AssertionError):  # more letters than the span has frames: it does not fit here
        return None
    if not timed:
        return None
    weights = [word.end - word.start for word in timed]
    return Line(text, timed[0].start, timed[-1].end, float(np.mean([word.score for word in timed])),
                margin=float(np.average([word.margin for word in timed], weights=weights)))


def settle(lines: list[Line], length: float, frames, stride: float, models: Models, iso: str,
           tuning: Tuning) -> list[Line]:
    """
    Every mapped line re-placed by its own lyrics words, between the line before it and the line after.
    The mapping placed it by Whisper's words, and where Whisper left a repeat out, the words it did write
    were stretched over the gap -- "Про-про" landing on the second "Искаш ли". Lines Whisper wrote and no
    lyrics line matched keep their place.
    """
    out: list[Line] = []
    for index, line in enumerate(lines):
        # Bounded by lyrics lines only: an unmatched Whisper fragment between them is not a fixed point.
        low = next((other.end for other in reversed(out) if not other.flags), 0.0)
        high = next((other.start for other in lines[index + 1:] if not other.flags), length)
        placed = None if line.flags else place(line.text, low, high, frames, stride, models, iso)
        out.append(placed if placed is not None and placed.margin >= tuning.fill_margin else line)
    return out


def fill(lines: list[Line], lyrics: list[str], regions, frames, stride: float, models: Models, iso: str,
         tuning: Tuning, log=print) -> list[Line]:
    """
    Lead singing no line covers gets the lyrics line that fits it best, if any fits well, again and again
    until nothing more does: the repeats and lines Whisper never wrote. A filled line is flagged, so a
    reviewer knows the aligner chose it, not the transcription.
    """
    # Whisper's unmatched fragments are no claim on the audio: "Пробай се, пробай ме" is often the
    # "Про-про-пробвай се" the lyrics have, too garbled to match. They count as gaps.
    taken = sorted((line.start - 0.2, line.end + 0.2) for line in lines
                   if not tuning.fill_fragments or "not in the lyrics" not in line.flags)
    spans = []
    for start, end in regions:
        for low, high in taken:
            if high <= start or low >= end:
                continue
            if low > start:
                spans.append((start, low))
            start = max(start, high)
        if end > start:
            spans.append((start, end))

    added: list[Line] = []
    candidates = list(dict.fromkeys(lyrics))
    while spans:
        low, high = spans.pop()
        if high - low < tuning.fill_min:
            continue
        found = [line for text in candidates if len(text.split()) >= 2
                 and (line := place(text, low, high, frames, stride, models, iso)) is not None]
        # Ranked by words times how far above the bar: a one-word line fits almost anywhere, so a good
        # score alone would let it win every gap.
        gain = lambda line: len(line.text.split()) * (line.margin - tuning.fill_margin)
        best = max(found, key=gain, default=None)
        if best is None or gain(best) <= 0:
            continue
        log(f"filled {stamp(best.start)} {best.text[:40]!r} (margin {best.margin:.2f})")
        best.flags.append("filled in")
        added.append(best)
        spans += [(low, best.start - 0.2), (best.end + 0.2, high)]
    # A fragment a filled line now stands on is gone; the rest stay, flagged.
    kept = [line for line in lines if "not in the lyrics" not in line.flags
            or not any(overlap(line.start, line.end, [(other.start, other.end)]) > (line.end - line.start) / 2
                       for other in added)]
    return sorted(kept + added, key=lambda line: line.start)


def signed_off(line: Line, language: Language) -> bool:
    """An unmatched run with Whisper's sign-off in it: "Обожавам Ти! Благодаря" over a song's last held note."""
    return "not in the lyrics" in line.flags and any(
        re.search(pattern, line.text, re.IGNORECASE) for pattern in language.signoffs)


def _stanza(line: Line, frames, stride: float, models: Models, iso: str, tuning: Tuning) -> list[Line]:
    """A matched lyrics line that is really a typed-in stanza: its own words aligned in its own span, split at pauses."""
    words = line.text.split()
    if len(words) <= 2 * tuning.max_words or "not in the lyrics" in line.flags:
        return [line]
    start = max(0.0, line.start - 0.2)
    timed = [word for word in align(words, frames[int(start / stride):int((line.end + 0.2) / stride)], stride, models,
                                    iso, offset=start) if word is not None]
    return halves(timed, tuning) if timed else [line]


def _loose(words: list[Word]) -> Line:
    """Sung words no lyrics line matched: an ad-lib, a verse the text lacks. Kept, in Whisper's words, flagged."""
    line = _line(words)
    line.flags.append("not in the lyrics")
    line.margin = float(np.average([word.margin for word in words], weights=[word.end - word.start for word in words]))
    return line


def split(words: list[Word], tuning: Tuning) -> list[Line]:
    """Words into lines: a pause, too many words or too long a stretch starts the next one."""
    lines, current = [], []
    for word in words:
        if current and (word.start - current[-1].end >= tuning.line_gap or len(current) >= tuning.max_words
                        or word.end - current[0].start > tuning.max_line):
            lines.append(_line(current))
            current = []
        current.append(word)
    return lines + [_line(current)] if current else lines


def _line(words: list[Word]) -> Line:
    return Line(" ".join(word.text for word in words), words[0].start, words[-1].end,
                float(np.mean([word.score for word in words])))


# ── line times, flags, LRC ──────────────────────────────────────────────────────────────────────


def finish(lines: list[Line], regions, onset_times: np.ndarray, tuning: Tuning) -> list[str]:
    """Snaps starts, extends ends across held notes, flags what looks wrong. Returns song-level flags."""
    for line in lines:
        near = onset_times[np.abs(onset_times - line.start) <= tuning.snap] if len(onset_times) else onset_times
        if len(near):
            line.start = float(near[np.argmin(np.abs(near - line.start))])
        else:
            line.flags.append("no onset")

    for index, line in enumerate(lines):
        following = lines[index + 1].start if index + 1 < len(lines) else None
        sung = line.end - line.start  # first word to last word, before the end is held across the note
        held = next((end for start, end in regions if start <= line.end <= end), line.end)
        line.end = max(line.end, held)
        if following is not None:
            line.end = min(line.end, following - 0.05)
            if following <= line.start:
                line.flags.append("overlaps the next line")

        length = line.end - line.start
        if length < tuning.min_line:
            line.flags.append("short")
        elif sung > 10:
            line.flags.append("long")
        if line.score < tuning.low_score:
            line.flags.append(f"low score {line.score:.2f}")
        if length > 0 and overlap(line.start, line.end, regions) < length / 2:
            line.flags.append("outside the sung regions")

    sung = sum(end - start for start, end in regions)
    covered = sum(overlap(line.start, max(line.end, line.start), regions) for line in lines)
    return [f"lines cover {covered / sung:.0%} of the singing"] if sung and covered / sung < tuning.min_coverage else []


def stamp(seconds: float) -> str:
    """``[mm:ss.xx]``, and ``[h:mm:ss.xx]`` past 99 minutes, which stih's LrcParser also reads."""
    hundredths = max(0, round(seconds * 100))
    minutes, hundredths = divmod(hundredths, 6000)
    if minutes < 100:
        return f"[{minutes:02d}:{hundredths // 100:02d}.{hundredths % 100:02d}]"
    hours, minutes = divmod(minutes, 60)
    return f"[{hours}:{minutes:02d}:{hundredths // 100:02d}.{hundredths % 100:02d}]"


def tidy(text: str) -> str:
    """The written words' punctuation, one way: ", " after every comma, "..." for any run of dots."""
    text = re.sub(r"\s*(?:\.{2,}|…)", "...", text)
    text = re.sub(r"\s*,\s*", ", ", text)
    return re.sub(r"\s+", " ", text).strip()


def header(length: float, track: sources.Track | None = None, by: str | None = None, asr: str | None = None,
           aligner: str | None = None, edited: bool = False) -> list[str]:
    """
    The ID tags. A tag with no value is left out; `by` is the LRC file's author, not the song's. Comment tags
    ([#:…]) name the transcription and alignment models and say whether a person changed the lines since.
    """
    minutes, seconds = divmod(round(length), 60)
    tags = {"ti": track.titles[0] if track and track.titles else None,  # the first is the name the library shows
            "ar": track.artists[0] if track and track.artists else None,
            "al": track.album if track else None,
            "length": f"{minutes:02d}:{seconds:02d}", "by": by, "re": "musicrain/Lirika", "ve": VERSION}
    out = [f"[{tag}:{value.strip()}]" for tag, value in tags.items() if value and value.strip()]
    comments = (("asr", asr), ("aligner", aligner), ("edited", "yes" if edited else "no"))
    return out + [f"[#:{name}:{value}]" for name, value in comments if value]


def lrc(lines: list[Line], length: float, tuning: Tuning, track: sources.Track | None = None,
        by: str | None = None, asr: str | None = None, aligner: str | None = None) -> str:
    """The ID tags (see header), then the lines. The studio turns [#:edited:no] to yes when the lines are changed."""
    out = header(length, track, by, asr, aligner)
    for index, line in enumerate(lines):
        out.append(f"{stamp(line.start)}{tidy(line.text)}")
        following = lines[index + 1].start if index + 1 < len(lines) else length
        if following - line.end >= tuning.gap_marker:
            out.append(stamp(line.end))  # stih reads an empty stamp as "the line has stopped"
    return "\n".join(out) + "\n"


# ── one file ────────────────────────────────────────────────────────────────────────────────────


def reference(song: Path, given: Path | None, force: bool) -> tuple[str | None, str]:
    """The trusted texts, in the plan's order: --text, the .txt beside, the .lrc being replaced."""
    if given:
        return given.read_text(encoding="utf-8"), "text"
    for suffix, origin in ((".txt", "txt"), (".lrc", "lrc")):
        path = song.with_suffix(suffix)
        if path.exists() and (suffix == ".txt" or force):
            return path.read_text(encoding="utf-8", errors="replace"), origin
    return None, ""


def _drafted(song: Path, audio: np.ndarray, spans, language: Language, models: Models, tuning: Tuning) -> list[Window]:
    key = (str(song), language.asr, tuning.asr_input)
    if key not in models.drafts:
        models.drafts[key] = draft(audio, spans, language, models)
    return models.drafts[key]


def generate(song: Path, language: Language, models: Models, tuning: Tuning = Tuning(), text: str | None = None,
             origin: str = "", lookup: bool = True, keep: bool = False, log=print, dump: Path | None = None,
             by: str | None = None) -> Result:
    """Everything but the writing. eval.py calls this directly. `dump` also writes the models' raw output there."""
    models.use(language)
    track = sources.track_of(song)
    lead = pcm(stem(song, models, keep=keep))
    length = len(lead) / RATE
    regions = activity(lead, tuning)

    drafted = None
    if text is None and lookup:
        asr_audio = pcm(song) if tuning.asr_input == "mix" else lead
        drafted = _drafted(song, asr_audio, windows(regions, lead), language, models, tuning)
        hit = sources.lookup(track, language.sources, " ".join(window.text for window in drafted),
                             normalize, tuning.lookup_threshold, log)
        if hit:
            text, origin = hit.text, f"online:{hit.source}"

    if text is not None and tuning.map_text:
        mode = f"mapped:{origin}"
        voice = pcm(stem(song, models, VOCALS, keep))
        regions = activity(voice, tuning)
        found_onsets = onsets(voice, regions)
        frames, stride = emissions(voice, models)
        if drafted is None:
            asr_audio = pcm(song) if tuning.asr_input == "mix" else lead
            drafted = _drafted(song, asr_audio, windows(activity(lead, tuning), lead), language, models, tuning)
        raw_words = []
        sung = [word for words in sung_words(drafted, frames, stride, regions, models, language.mms, raw_words)
                for word in words]
        lyrics = normalize(text)
        lines = [line for line in map_lines(sung, lyrics, tuning) if not signed_off(line, language)]
        if tuning.settle_lines:
            lines = settle(lines, len(voice) / RATE, frames, stride, models, language.mms, tuning)
        if tuning.fill_gaps:
            lines = fill(lines, lyrics, activity(lead, tuning), frames, stride, models, language.mms, tuning, log)
        lines = [part for line in lines for part in _stanza(line, frames, stride, models, language.mms, tuning)]
    elif text is not None:
        mode = origin
        voice = lead if tuning.align_on != "vocals" else pcm(stem(song, models, VOCALS, keep))
        if voice is not lead:
            regions = activity(voice, tuning)
        found_onsets = onsets(voice, regions)
        frames, stride = emissions(pcm(song) if tuning.align_on == "mix" else voice, models)
        raw_words: list = []
        lines = align_lines(normalize(text), frames, stride, models, language.mms, tuning, raw_words)
    else:
        mode = "transcribe"
        found_onsets = onsets(lead, regions)
        frames, stride = emissions(lead, models)
        if drafted is None:
            asr_audio = pcm(song) if tuning.asr_input == "mix" else lead
            drafted = _drafted(song, asr_audio, windows(regions, lead), language, models, tuning)
        raw_words = []
        lines = transcribe_lines(drafted, frames, stride, regions, models, language.mms, tuning, raw_words)

    asr = language.asr if drafted is not None else None  # align mode with no lookup never transcribes
    if dump is not None:
        if drafted is None:  # align mode had its words already; Whisper's take is still worth seeing
            asr_audio = pcm(song) if tuning.asr_input == "mix" else lead
            drafted = _drafted(song, asr_audio, windows(activity(lead, tuning), lead), language, models, tuning)
        _dump(dump, song, drafted, raw_words)

    flags = finish(lines, regions, found_onsets, tuning)
    if mode == "transcribe" and (len(lines) < tuning.min_sung_lines
                                 or np.median([line.score for line in lines]) < tuning.instrumental_score):
        # A kaval or a clarinet survives the karaoke split as "lead", and Whisper hears words in it.
        return Result([], mode, "", ["nothing sung"])
    return Result(lines, mode, lrc(lines, length, tuning, track, by, asr, models.aligner_name), flags)


def _dump(directory: Path, song: Path, drafted: list[Window], raw_words: list) -> None:
    """Whisper's raw text per window, and the aligner's raw word timings before any snapping."""
    directory.mkdir(parents=True, exist_ok=True)
    with (directory / f"{song.stem}.whisper.txt").open("w", encoding="utf-8") as file:
        for window in drafted:
            note = "" if window.text or not window.raw else "    (dropped: blocklist or loop)"
            file.write(f"{stamp(window.start)} - {stamp(window.end)}{note}\n{window.raw}\n\n")
    with (directory / f"{song.stem}.words.tsv").open("w", encoding="utf-8") as file:
        file.write("start\tend\tscore\tword\tfrom\n")
        for word, where in raw_words:
            file.write(f"{word.start:.3f}\t{word.end:.3f}\t{word.score:.3f}\t{word.text}\t{where}\n")


def process(song: Path, args, models: Models) -> int:
    existing = song.with_suffix(".lrc")
    if existing.exists() and not args.force and args.out is None:
        print(f"{song.name}: has a .lrc already, skipped (--force replaces it)", file=sys.stderr)
        return 0

    language = LANGUAGES[args.lang]
    text, origin = reference(song, args.text, args.force)
    try:
        result = generate(song, language, models, text=text, origin=origin, lookup=not args.no_lookup,
                          keep=args.keep, log=lambda message: print(f"  {message}", file=sys.stderr), dump=args.dump,
                          by=args.by)
    except ValueError as error:  # a reference text with a repeat nobody can resolve
        print(f"{song.name}: {error}", file=sys.stderr)
        return 1

    if not result.lines:
        print(f"{song.name}: nothing sung found, nothing written", file=sys.stderr)
        return 0

    print(f"{song.name}: {len(result.lines)} lines from {result.mode}", file=sys.stderr)
    for flag in result.flags:
        print(f"  song: {flag}", file=sys.stderr)
    for line in result.lines:
        if line.flags:
            print(f"  {stamp(line.start)} {line.text[:50]!r}: {', '.join(line.flags)}", file=sys.stderr)

    if args.register:
        if not sources.register(song, result.lrc, "Generated"):
            return 1
    elif args.out is not None and str(args.out) == "-":
        sys.stdout.write(result.lrc)
    else:
        (args.out or existing).write_text(result.lrc, encoding="utf-8")
    return 2 if result.flagged else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("audio", type=Path, nargs="+")
    parser.add_argument("--lang", required=True, choices=sorted(LANGUAGES))
    parser.add_argument("--text", type=Path, help="reference lyrics, plain or LRC; one AUDIO only")
    parser.add_argument("--no-lookup", action="store_true", help="skip looking the words up online")
    parser.add_argument("--out", type=Path, help="default: AUDIO with .lrc; '-' for stdout; one AUDIO only")
    parser.add_argument("--force", action="store_true", help="replace an existing .lrc, re-timing its words")
    parser.add_argument("--register", action="store_true", help="hand the result to stih instead of writing it")
    parser.add_argument("--keep", action="store_true", help="keep the instrumental stem and the decoded mix")
    parser.add_argument("--dump", type=Path, help="also write Whisper's raw text and the raw word timings here")
    parser.add_argument("--by", help="the LRC file's author, written as its [by:] tag")
    args = parser.parse_args()
    if len(args.audio) > 1 and (args.text or args.out):
        parser.error("--text and --out take one AUDIO")

    models = Models()
    worst = 0
    for song in args.audio:
        try:
            code = process(song, args, models)
        except subprocess.CalledProcessError as error:
            print(f"{song.name}: ffmpeg failed: {error.stderr.decode(errors='replace').strip()}", file=sys.stderr)
            code = 1
        worst = max(worst, code) if worst != 1 else 1
    return worst


if __name__ == "__main__":
    sys.exit(main())
