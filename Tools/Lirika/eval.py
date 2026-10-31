"""Section 6 of the plan: how good is lirika, measured against lyrics people synced by hand.

    uv run python eval.py corpus [--per-language 20]
    uv run python eval.py run align|transcribe [--set name=value ...] [--asr MODEL] [--tag NAME]
    uv run python eval.py lookup [--tag NAME]

`corpus` picks songs that already have a synced .lrc in the library and writes corpus.csv. `run` puts
each through lirika and scores the line starts against that .lrc; `lookup` scores the online text
lookup on its own. Every result is one CSV row per song (or candidate) in results/, so comparing
settings is a sort. Lead stems are cached, so only the first run separates anything.
"""

from __future__ import annotations

import argparse
import csv
import random
import re
import sqlite3
import statistics
import sys
from dataclasses import replace
from difflib import SequenceMatcher
from pathlib import Path

import numpy as np

import sources
import lirika

HERE = Path(__file__).parent
CORPUS = HERE / "corpus.csv"
RESULTS = HERE / "results"
# Hand-checked references, one <song stem>.lrc each: a person listened and fixed every line start. They
# replace the library's .lrc and are scored as they are, with no offset removed. Outside the repository
# because they hold lyrics.
TRUTH = Path.home() / ".cache/lirika/truth"
FOLDERS = {"bg": ("Bulgarian/", "Deezer/"), "en": ("Rap/", "Rock/", "Pop/", "Metal/")}
_STAMP = re.compile(r"\[(?:(\d+):)?(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]")
_OFFSET = re.compile(r"^\s*\[offset:\s*([+-]?\d+)\s*\]", re.IGNORECASE)


def read_lrc(text: str) -> tuple[list[tuple[float, str]], list[float]]:
    """stih's LrcParser rules: sung lines as (start, text), and the empty stamps that end them."""
    offset, lines, gaps = 0.0, [], []
    for raw in text.splitlines():
        if found := _OFFSET.match(raw):
            offset = int(found[1]) / 1000
            continue
        stamps = list(_STAMP.finditer(raw))
        if not stamps:
            continue
        words = raw[stamps[-1].end():].strip()
        for stamp in stamps:
            hours, minutes, seconds, fraction = stamp.groups()
            fraction = fraction or ""
            at = int(hours or 0) * 3600 + int(minutes) * 60 + int(seconds) + (int(fraction) / 10 ** len(fraction) if fraction else 0)
            (lines.append((max(0.0, at + offset), words)) if words else gaps.append(max(0.0, at + offset)))
    lines.sort()
    return lines, sorted(gaps)


_ASS = re.compile(r"^Dialogue:\s*\d+,(\d+):(\d{2}):(\d{2})[.](\d{2}),(\d+):(\d{2}):(\d{2})[.](\d{2}),(?:[^,]*,){6}(.*)$")
_SRT = re.compile(r"(\d+):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d+):(\d{2}):(\d{2})[,.](\d{3})")
_NOTE = re.compile(r"\s*[(\[][^)\]]*[)\]]\s*")


def read_timed(path: Path) -> list[tuple[float, float | None, str]]:
    """
    A hand-checked file as (start, end, words): Aegisub's .ass (centiseconds), .srt (milliseconds) or .lrc
    (no ends). Bracketed notes are the checker's comments, not lyrics: a line that is only a note --
    "(Background Hey!)" -- is not a sung line.
    """
    text = path.read_text(encoding="utf-8-sig", errors="replace")
    out: list[tuple[float, float | None, str]] = []
    if path.suffix == ".ass":
        for found in map(_ASS.match, text.splitlines()):
            if found:
                h, m, s_, c, h2, m2, s2, c2 = map(int, found.groups()[:8])
                words = re.sub(r"\{[^}]*\}", "", found[9]).replace("\\N", " ")
                out.append((h * 3600 + m * 60 + s_ + c / 100, h2 * 3600 + m2 * 60 + s2 + c2 / 100, words))
    elif path.suffix == ".srt":
        for block in re.split(r"\n\s*\n", text):
            rows = block.strip().splitlines()
            stamp = next((row for row in rows if _SRT.search(row)), None)
            if stamp:
                h, m, s_, ms, h2, m2, s2, ms2 = map(int, _SRT.search(stamp).groups())
                words = " ".join(rows[rows.index(stamp) + 1:])
                out.append((h * 3600 + m * 60 + s_ + ms / 1000, h2 * 3600 + m2 * 60 + s2 + ms2 / 1000, words))
    else:
        lines, gaps = read_lrc(text)
        stamps = sorted([start for start, _ in lines] + gaps)
        out = [(start, next((t for t in stamps if t > start), None), words) for start, words in lines]
    return [(start, end, _NOTE.sub(" ", words).strip()) for start, end, words in out
            if _NOTE.sub(" ", words).strip()]


def compare(truth_path: Path, generated_path: Path) -> None:
    """Every hand-checked line against the generated file: right, mistimed, wrong words, missed or extra."""
    truth = read_timed(truth_path)
    generated = read_timed(generated_path)
    same = lambda a, b: SequenceMatcher(None, sources.plain(a), sources.plain(b)).ratio()

    # Pair the two in order: alike words count, so do close starts; nothing pairs more than 5 s apart.
    rows, cols = len(truth), len(generated)
    best = [[0.0] * (cols + 1) for _ in range(rows + 1)]
    for i in range(rows - 1, -1, -1):
        for j in range(cols - 1, -1, -1):
            options = [best[i + 1][j], best[i][j + 1]]
            gap = abs(generated[j][0] - truth[i][0])
            if gap <= 5:
                options.append(best[i + 1][j + 1] + same(truth[i][2], generated[j][2]) + 1 - gap / 5)
            best[i][j] = max(options)
    pairs, i, j = {}, 0, 0
    while i < rows and j < cols:
        gap = abs(generated[j][0] - truth[i][0])
        if gap <= 5 and best[i][j] == best[i + 1][j + 1] + same(truth[i][2], generated[j][2]) + 1 - gap / 5:
            pairs[i] = j
            i, j = i + 1, j + 1
        elif best[i][j] == best[i + 1][j]:
            i += 1
        else:
            j += 1

    tally = {"right": 0, "mistimed": 0, "wrong words": 0, "missed": 0, "extra": cols - len(pairs)}
    starts, ends = [], []
    for i, (start, end, words) in enumerate(truth):
        if i not in pairs:
            tally["missed"] += 1
            print(f"  missed      {start:7.2f}            {words[:60]}")
            continue
        g_start, g_end, g_words = generated[pairs[i]]
        error = g_start - start
        starts.append(abs(error))
        if end is not None and g_end is not None:
            ends.append(g_end - end)
        kind = "wrong words" if same(words, g_words) < 0.85 else "mistimed" if abs(error) > 0.2 else "right"
        tally[kind] += 1
        if kind != "right":
            detail = f"  got {g_words[:40]!r}" if kind == "wrong words" else ""
            print(f"  {kind:<11} {start:7.2f} {error:+6.2f} s  {words[:50]}{detail}")
    for j in sorted(set(range(cols)) - set(pairs.values())):
        print(f"  extra       {generated[j][0]:7.2f}            {generated[j][2][:60]}")

    sung = len(truth)
    print(f"{sung} sung lines: " + ", ".join(f"{count} {kind}" for kind, count in tally.items()))
    if starts:
        print(f"starts of paired lines: within 200 ms {sum(e <= 0.2 for e in starts) / len(starts):.0%}, "
              f"within 500 ms {sum(e <= 0.5 for e in starts) / len(starts):.0%}, median {np.median(starts):.2f} s")
    if ends:
        print(f"ends (generated minus checked): median {np.median(ends):+.2f} s, "
              f"within 500 ms {sum(abs(e) <= 0.5 for e in ends) / len(ends):.0%}")


def language_of(text: str) -> str | None:
    letters = [char for char in text if char.isalpha()]
    if not letters:
        return None
    cyrillic = sum("Ѐ" <= char <= "ӿ" for char in letters) / len(letters)
    if cyrillic > 0.6:
        return "bg"
    english = len(re.findall(r"\b(the|you|and|i|me|my|to|it)\b", text.lower()))
    return "en" if cyrillic < 0.01 and english >= 10 else None


def corpus(per_language: int) -> None:
    library = Path(sources.backend_env()["MUSIC_LIBRARY_PATH"])
    db = sqlite3.connect(f"file:{library / 'library.db'}?mode=ro", uri=True)
    rows = db.execute("SELECT RelativeLocation, Length FROM songs WHERE LyricsType = 'Synchronized' "
                      "AND Length BETWEEN 90000 AND 420000 ORDER BY RelativeLocation").fetchall()
    random.seed(20261030)
    random.shuffle(rows)

    chosen = []
    for language, folders in FOLDERS.items():
        picked = 0
        for relative, _ in rows:
            if picked == per_language or not relative.startswith(folders):
                continue
            song = library / relative
            lrc = song.with_suffix(".lrc")
            if not lrc.exists():
                continue
            lines, _ = read_lrc(lrc.read_text(encoding="utf-8", errors="replace"))
            if len(lines) < 8 or language_of(" ".join(text for _, text in lines)) != language:
                continue
            chosen.append({"path": str(song), "language": language, "checked": ""})
            picked += 1

    with CORPUS.open("w", newline="", encoding="utf-8") as file:
        writer = csv.DictWriter(file, ["path", "language", "checked"])
        writer.writeheader()
        writer.writerows(chosen)
    print(f"{len(chosen)} songs in {CORPUS.name}. Mark 'checked' with y/n after listening to five line starts.")


def songs() -> list[dict]:
    with CORPUS.open(encoding="utf-8") as file:
        return [row for row in csv.DictReader(file) if row["checked"] != "n"]


def match(predicted: list[float], reference: list[float], window: float = 1.0) -> list[tuple[int, int]]:
    """Monotonic one-to-one pairs within `window` seconds, greedy from the start."""
    pairs, i, j = [], 0, 0
    while i < len(reference) and j < len(predicted):
        if abs(predicted[j] - reference[i]) <= window:
            pairs.append((j, i))
            i, j = i + 1, j + 1
        elif predicted[j] < reference[i]:
            j += 1
        else:
            i += 1
    return pairs


def instrumental(reference: list[float], gaps: list[float], length: float) -> list[tuple[float, float]]:
    """Where the reference says nobody sings: the intro, and every gap marker up to the next line."""
    spans = [(0.0, reference[0] - 2.0)] if reference else []
    for gap in gaps:
        after = next((start for start in reference if start > gap), length)
        if after - gap >= 3.0:
            spans.append((gap + 0.5, after - 1.0))
    return spans


def offsets_from(path: Path) -> dict[str, float]:
    """Each song's reference offset, as an align-mode run measured it (its median signed error)."""
    if not path.exists():
        return {}
    with path.open(encoding="utf-8") as file:
        return {row["song"]: float(row["offset"]) for row in csv.DictReader(file) if row.get("offset")}


def run(mode: str, tuning: lirika.Tuning, asr: str | None, tag: str, offsets: dict[str, float],
        aligner: str | None = None, normalize: bool | None = None, only: str | None = None) -> None:
    """
    No reference here is the truth: LRCLIB files were fetched automatically and may belong to another
    master, and labels synced many of Deezer's loosely. So every song is scored after removing its
    reference's own constant offset, and each reference gets a "sung" score -- the share of its line
    starts that land on singing in the vocal stem -- so the loose ones can be left out of a comparison.
    Absolute accuracy needs references a person has checked (the `checked` column).
    """
    models = lirika.Models(aligner, normalize)
    rows, per_line = [], []
    for song in songs():
        if only and song["language"] != only:
            continue
        path, language = Path(song["path"]), lirika.LANGUAGES[song["language"]]
        if asr:
            language = replace(language, asr=asr)
        checked = (TRUTH / f"{path.stem}.lrc").exists()
        source = TRUTH / f"{path.stem}.lrc" if checked else path.with_suffix(".lrc")
        reference, gaps = read_lrc(source.read_text(encoding="utf-8", errors="replace"))
        raw_starts = [start for start, _ in reference]
        text = "\n".join(words for _, words in reference) if mode == "align" else None

        result = lirika.generate(path, language, models, tuning, text=text, origin="eval", lookup=False,
                                 log=lambda message: None)
        predicted = [line.start for line in result.lines]
        voices = lirika.pcm(lirika.stem(path, models, lirika.VOCALS))
        length = len(voices) / lirika.RATE

        if mode == "align":
            # Same order, same words: walk both lists and pair equal texts.
            pairs, j = [], 0
            generated = [line.text for line in result.lines]
            for i, (_, words) in enumerate(reference):
                normalized = lirika.normalize(words)
                if j < len(generated) and normalized and generated[j] == normalized[0]:
                    pairs.append((j, i))
                    j += 1
            offset = statistics.median(predicted[j] - raw_starts[i] for j, i in pairs) if pairs else 0.0
        else:
            offset = offsets.get(path.name, 0.0)
        if checked:
            offset = 0.0
        starts = [start + offset for start in raw_starts]
        gaps = [gap + offset for gap in gaps]
        if mode != "align":
            pairs = match(predicted, starts)

        regions = lirika.activity(voices, lirika.Tuning())
        sung = sum(lirika.overlap(start, start + 0.75, regions) >= 0.25 for start in starts) / len(starts)
        absolute = [abs(predicted[j] - starts[i]) for j, i in pairs]
        # What a reviewer sees: the lines nobody flagged should be right, and the wrong ones should be flagged.
        clean = [abs(predicted[j] - starts[i]) for j, i in pairs if not result.lines[j].flags]
        wrong = [bool(result.lines[j].flags) for j, i in pairs if abs(predicted[j] - starts[i]) > 0.5]
        silent = instrumental(starts, gaps, length)
        row = {
            "song": path.name, "language": song["language"], "reference": len(starts), "generated": len(predicted),
            "matched": len(pairs), "checked": checked, "offset": round(offset, 3), "sung": round(sung, 3),
            "mae": round(statistics.mean(absolute), 3) if absolute else "",
            "within_200": round(sum(error <= 0.2 for error in absolute) / len(starts), 3),
            "within_500": round(sum(error <= 0.5 for error in absolute) / len(starts), 3),
            "raw_within_200": round(sum(abs(predicted[j] - raw_starts[i]) <= 0.2 for j, i in pairs) / len(starts), 3),
            "precision_500": round(sum(error <= 0.5 for error in absolute) / len(predicted), 3) if predicted else "",
            "hallucinated": sum(any(a <= start <= b for a, b in silent) for start in predicted),
            "flagged_lines": sum(bool(line.flags) for line in result.lines),
            "unflagged_within_200": round(sum(error <= 0.2 for error in clean) / len(clean), 3) if clean else "",
            "unflagged_within_500": round(sum(error <= 0.5 for error in clean) / len(clean), 3) if clean else "",
            "wrong_flagged": f"{sum(wrong)}/{len(wrong)}",
            "song_flags": "; ".join(result.flags),
        }
        rows.append(row)
        per_line += [{"song": path.name, "language": song["language"], "error": round(predicted[j] - starts[i], 3),
                      "flags": "; ".join(re.sub(r" -?\d.*", "", flag) for flag in result.lines[j].flags)}
                     for j, i in pairs]
        print(f"{row['song'][:48]:<48} {row['within_200']:>6} {row['within_500']:>6} mae {row['mae']!s:>6} "
              f"offset {row['offset']:+.2f} sung {row['sung']:.2f} halluc {row['hallucinated']}", flush=True)

    write(rows, f"{mode}-{tag}")
    write(per_line, f"{mode}-{tag}-lines")
    summary(rows)
    reasons(per_line)
    print("references whose line starts mostly land on singing (sung >= 0.85):")
    summary([row for row in rows if row["sung"] >= 0.85], worst=False)
    if any(row["checked"] for row in rows):
        print("hand-checked references only -- the only absolute numbers here:")
        summary([row for row in rows if row["checked"]], worst=False)


def lookup(tag: str) -> None:
    """Every candidate every site offers, with how well it agrees with the draft and with the truth."""
    models = lirika.Models()
    rows = []
    for song in songs():
        path, language = Path(song["path"]), lirika.LANGUAGES[song["language"]]
        if not language.sources:
            continue
        reference, _ = read_lrc(path.with_suffix(".lrc").read_text(encoding="utf-8", errors="replace"))
        truth = "\n".join(words for _, words in reference)

        lead = lirika.pcm(lirika.stem(path, models))
        regions = lirika.activity(lead, lirika.Tuning())
        drafted = " ".join(window.text for window in lirika.draft(lirika.pcm(path), lirika.windows(regions, lead), language, models))
        track = sources.track_of(path)
        offered = 0
        for name in language.sources:
            for candidate in sources.SOURCES[name](track, log=lambda message: None):
                offered += 1
                rows.append({"song": path.name, "language": song["language"], "source": name,
                             "candidate": f"{candidate.artist} - {candidate.title}",
                             "draft_agreement": _round(sources.agreement(drafted, candidate.text)),
                             "true_agreement": _round(sources.agreement(truth, candidate.text))})
        if not offered:
            rows.append({"song": path.name, "language": song["language"], "source": "", "candidate": "",
                         "draft_agreement": "", "true_agreement": ""})
        print(f"{path.name[:48]:<48} {offered} candidates", flush=True)

    write(rows, f"lookup-{tag}")
    # A candidate is the right song when it holds most of the true words' 4-grams.
    for threshold in (0.3, 0.4, 0.5, 0.6):
        accepted = [row for row in rows if row["draft_agreement"] != "" and row["draft_agreement"] >= threshold]
        wrong = [row for row in accepted if row["true_agreement"] == "" or row["true_agreement"] < 0.5]
        hit = {row["song"] for row in accepted} - {row["song"] for row in wrong}
        print(f"threshold {threshold}: {len(hit)}/{len({row['song'] for row in rows})} songs found, "
              f"{len(wrong)} false accepts")


def reasons(per_line: list[dict]) -> None:
    """How well each flag reason predicts a line that is really off: a flag nobody can trust is review time."""
    off = lambda line: abs(line["error"]) > 0.5
    base = sum(map(off, per_line)) / len(per_line)
    print(f"lines off by > 0.5 s: {base:.1%} of {len(per_line)}")
    for reason in sorted({part for line in per_line for part in line["flags"].split("; ") if part}):
        mine = [line for line in per_line if reason in line["flags"].split("; ")]
        print(f"  {reason:<26} on {len(mine):4d} lines, {sum(map(off, mine)) / len(mine):.1%} of them off")


def breaks() -> None:
    """
    Which rule puts line breaks where people do? The reference's words are aligned with their line breaks
    hidden, and each rule guesses the breaks back. Only the references' text structure is used here, not
    their timing, so a reference from another master or a loose sync costs nothing.
    """
    from beat_this.inference import Audio2Beats

    tracker = Audio2Beats(checkpoint_path="final0", device=lirika.device()[0], dbn=False)
    models = lirika.Models()
    rules = {"gap 0.4 s": lambda gap, start, end, bar, beat: gap >= 0.4}
    for fraction in (0.25, 0.5, 1.0):
        # Silence of at least `fraction` of a beat that reaches a bar line, or ends within a beat before one
        # (a pickup: the next line starts just ahead of its downbeat).
        rules[f"bar, silence >= {fraction} beat"] = (
            lambda gap, start, end, bar, beat, fraction=fraction: gap >= fraction * beat and bar is not None)
    scores = {name: {lang: [0, 0, 0] for lang in ("bg", "en")} for name in rules}  # true pos, predicted, real

    for song in songs():
        path, language = Path(song["path"]), lirika.LANGUAGES[song["language"]]
        reference, _ = read_lrc(path.with_suffix(".lrc").read_text(encoding="utf-8", errors="replace"))
        lines = lirika.normalize("\n".join(words for _, words in reference))
        voice = lirika.pcm(lirika.stem(path, models, lirika.VOCALS))
        frames, stride = lirika.emissions(voice, models)
        raw: list = []
        lirika.align_lines(lines, frames, stride, models, language.mms, lirika.Tuning(), raw)
        words = [(word, int(where.split()[1])) for word, where in raw]

        _, downbeats = tracker(lirika.pcm(path, 22050), 22050)
        beats, _ = tracker(lirika.pcm(path, 22050), 22050)
        beat = float(np.median(np.diff(beats))) if len(beats) > 1 else 0.5

        for (word, line), (following, next_line) in zip(words, words[1:]):
            gap = following.start - word.end
            bar = next((d for d in downbeats if word.end < d <= following.start + beat), None)
            real = next_line != line
            for name, rule in rules.items():
                guess = rule(gap, word.end, following.start, bar, beat)
                tally = scores[name][song["language"]]
                tally[0] += guess and real
                tally[1] += guess
                tally[2] += real
        print(f"{path.name[:50]:<50} {len(words)} words, {60 / beat:.0f} BPM, {len(downbeats)} bars", flush=True)

    for name, by_language in scores.items():
        parts = []
        for language, (hit, guessed, real) in by_language.items():
            precision, recall = hit / max(1, guessed), hit / max(1, real)
            f1 = 2 * precision * recall / max(1e-9, precision + recall)
            parts.append(f"{language} P {precision:.0%} R {recall:.0%} F1 {f1:.0%}")
        print(f"{name:<26} " + "   ".join(parts))


def _round(value: float | None):
    return "" if value is None else round(value, 3)


def write(rows: list[dict], name: str) -> None:
    RESULTS.mkdir(exist_ok=True)
    path = RESULTS / f"{name}.csv"
    with path.open("w", newline="", encoding="utf-8") as file:
        writer = csv.DictWriter(file, list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    print(f"wrote {path.relative_to(HERE)}")


def summary(rows: list[dict], worst: bool = True) -> None:
    for language in ("bg", "en", None):
        mine = [row for row in rows if language is None or row["language"] == language]
        if not mine:
            continue
        lines = sum(row["reference"] for row in mine)
        within = lambda key: sum(row[key] * row["reference"] for row in mine) / lines
        maes = [row["mae"] for row in mine if row["mae"] != ""]
        flagged = sum(row["flagged_lines"] for row in mine)
        unflagged = [row for row in mine if row["unflagged_within_200"] != ""]
        clean = lambda key: sum(row[key] * (row["generated"] - row["flagged_lines"]) for row in unflagged) / max(
            1, sum(row["generated"] - row["flagged_lines"] for row in unflagged))
        caught = [int(part) for row in mine for part in row["wrong_flagged"].split("/")]
        print(f"{language or 'all'}: {len(mine)} songs, {lines} lines, within 200 ms {within('within_200'):.1%}, "
              f"within 500 ms {within('within_500'):.1%}, mean of song MAEs {np.mean(maes):.3f} s, "
              f"hallucinated {sum(row['hallucinated'] for row in mine)}")
        print(f"    flagged {flagged / max(1, sum(row['generated'] for row in mine)):.1%} of lines; unflagged lines "
              f"within 200 ms {clean('unflagged_within_200'):.1%}, within 500 ms {clean('unflagged_within_500'):.1%}; "
              f"lines off by > 0.5 s that were flagged {sum(caught[0::2])}/{sum(caught[1::2])}")
    if worst:
        print("worst five by within_200:")
        for row in sorted(rows, key=lambda row: row["within_200"])[:5]:
            print(f"  {row['song'][:60]:<60} {row['within_200']:.0%} (offset {row['offset']:+.2f}, sung {row['sung']:.2f})")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("corpus").add_argument("--per-language", type=int, default=20)
    runner = commands.add_parser("run")
    runner.add_argument("mode", choices=["align", "transcribe"])
    runner.add_argument("--set", action="append", default=[], help="a Tuning field, name=value")
    runner.add_argument("--asr", help="Whisper model instead of the language's default")
    runner.add_argument("--tag", default="baseline")
    runner.add_argument("--aligner", help="CTC model for the timing, instead of the language's own")
    runner.add_argument("--normalize", action="store_true", help="zero-mean, unit-variance aligner input")
    runner.add_argument("--language", choices=["bg", "en"], help="only this language's songs")
    runner.add_argument("--offsets", type=Path, default=RESULTS / "align-vocals.csv",
                        help="transcribe mode: the align run whose per-song reference offsets to apply")
    commands.add_parser("lookup").add_argument("--tag", default="baseline")
    commands.add_parser("breaks")
    comparer = commands.add_parser("compare", help="a hand-checked .ass/.srt/.lrc against a generated .lrc")
    comparer.add_argument("truth", type=Path)
    comparer.add_argument("generated", type=Path)
    args = parser.parse_args()

    if args.command == "corpus":
        corpus(args.per_language)
    elif args.command == "breaks":
        breaks()
    elif args.command == "compare":
        compare(args.truth, args.generated)
    elif args.command == "lookup":
        lookup(args.tag)
    else:
        tuning = lirika.Tuning()
        for setting in args.set:
            name, _, value = setting.partition("=")
            current = getattr(tuning, name)
            tuning = replace(tuning, **{name: type(current)(value)})
        run(args.mode, tuning, args.asr, args.tag, offsets_from(args.offsets) if args.mode == "transcribe" else {},
            args.aligner, args.normalize or None, args.language)


if __name__ == "__main__":
    sys.exit(main())
