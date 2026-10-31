"""Phase 0: does every model in the pipeline run on this machine, and how fast?

    uv run python smoke.py SONG [--work DIR]

Separates SONG's lead vocal, runs Whisper over its loudest 30 s (lead stem and mix), and aligns the
.txt beside SONG when there is one, otherwise computes the MMS emissions alone. Nothing is written
next to SONG. Run it twice: the first run downloads the models and compiles kernels.
"""

import argparse
import subprocess
import tempfile
import time
from pathlib import Path

import numpy as np
import torch

from lirika import device

MODELS = Path.home() / ".cache/lirika/models"
SEPARATOR = "mel_band_roformer_karaoke_becruily.ckpt"
# The model's YAML asks for 8 overlapping passes. 2 is ~4x faster and the lead stem differs by -28 dB.
# Autocast stays off: in fp16 this RoFormer produces non-finite samples.
OVERLAP = 2
WHISPER = "openai/whisper-large-v3"
RATE = 16_000


def decode(path: Path, rate: int = RATE) -> np.ndarray:
    """Mono float32 at `rate`, through ffmpeg: WavPack, MP3, Ogg, anything it opens."""
    raw = subprocess.run(
        ["ffmpeg", "-v", "error", "-i", str(path), "-ac", "1", "-ar", str(rate), "-f", "f32le", "-"],
        check=True, capture_output=True).stdout
    return np.frombuffer(raw, np.float32)


def stage(label: str, started: float) -> float:
    now = time.perf_counter()
    print(f"  {label:<28} {now - started:6.1f} s   peak VRAM {torch.cuda.max_memory_allocated() / 2**30:4.1f} GiB")
    return now


def separate(song: Path, work: Path) -> Path:
    from audio_separator.separator import Separator

    t = time.perf_counter()
    mix = work / "mix.wav"
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(song), "-ac", "2", "-ar", "44100", str(mix)], check=True)
    t = stage("decode", t)

    separator = Separator(output_dir=str(work), model_file_dir=str(MODELS), output_format="FLAC",
                          log_level=40, mdxc_params={"segment_size": 256, "override_model_segment_size": False,
                                                     "batch_size": 1, "overlap": OVERLAP, "pitch_shift": 0})
    separator.load_model(model_filename=SEPARATOR)
    print(f"  separator device: {separator.torch_device}")
    t = stage("separator load", t)

    stems = [work / Path(name).name for name in separator.separate(str(mix))]
    stage("separate", t)
    for path in stems:
        print(f"    stem: {path.name}")
    # ponytail: picked by name; phase 1 checks per model which stem is the lead.
    return next(path for path in stems if "(Vocals)" in path.name)


def loudest(audio: np.ndarray, seconds: int = 30) -> slice:
    """The `seconds`-long window with the most energy, at one-second resolution."""
    energy = np.add.reduceat(audio ** 2, np.arange(0, len(audio), RATE))
    best = int(np.argmax(np.convolve(energy, np.ones(seconds), "valid"))) if len(energy) > seconds else 0
    return slice(best * RATE, (best + seconds) * RATE)


def whisper(windows: dict[str, np.ndarray], language: str) -> None:
    from transformers import WhisperForConditionalGeneration, WhisperProcessor

    t = time.perf_counter()
    processor = WhisperProcessor.from_pretrained(WHISPER)
    model = WhisperForConditionalGeneration.from_pretrained(WHISPER, dtype=device()[1]).to(device()[0]).eval()
    t = stage("whisper load", t)

    for name, audio in windows.items():
        features = processor(audio, sampling_rate=RATE, return_tensors="pt").input_features
        with torch.inference_mode():
            ids = model.generate(
                features.to(*device()), language=language, task="transcribe",
                condition_on_prev_tokens=False, temperature=(0.0, 0.2, 0.4, 0.6, 0.8, 1.0),
                compression_ratio_threshold=1.35, logprob_threshold=-1.0, no_speech_threshold=0.5)
        t = stage(f"whisper window ({name})", t)
        text = processor.batch_decode(ids, skip_special_tokens=True)[0].strip()
        print(f"    {text[:160]}")

    del model
    torch.cuda.empty_cache()


def mms(audio: np.ndarray, lyrics: Path | None) -> None:
    from ctc_forced_aligner import (generate_emissions, get_alignments, get_spans, load_alignment_model,
                                    postprocess_results, preprocess_text)

    t = time.perf_counter()
    model, tokenizer = load_alignment_model(device()[0], dtype=device()[1])
    t = stage("mms load", t)

    emissions, stride = generate_emissions(model, torch.from_numpy(audio).to(*device()), batch_size=8)
    t = stage("mms emissions", t)
    if lyrics is None:
        return

    tokens, text = preprocess_text(lyrics.read_text(encoding="utf-8"), romanize=True, language="bul")
    segments, scores, blank = get_alignments(emissions, tokens, tokenizer)
    words = postprocess_results(text, get_spans(tokens, segments, blank), stride, scores)
    stage("align (CPU Viterbi)", t)
    print(f"    {len(words)} words, mean score {np.mean([w['score'] for w in words]):.2f}")
    for word in words[:6]:
        print(f"    {word['start']:7.2f} - {word['end']:7.2f}  {word['text']}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("song", type=Path)
    parser.add_argument("--work", type=Path, help="keep the stems here instead of a temporary directory")
    parser.add_argument("--lang", default="bg")
    args = parser.parse_args()

    if torch.cuda.is_available():  # gcnArchName is the gfx target, on ROCm builds only
        arch = getattr(torch.cuda.get_device_properties(0), "gcnArchName", "")
        print(f"torch {torch.__version__}, {torch.cuda.get_device_name(0)} {arch}")
    else:
        print(f"torch {torch.__version__}, no GPU: everything runs on the CPU, slowly")

    with tempfile.TemporaryDirectory() as temporary:
        work = args.work or Path(temporary)
        work.mkdir(parents=True, exist_ok=True)
        started = time.perf_counter()

        lead_path = separate(args.song, work)
        lead, mix = decode(lead_path), decode(args.song)
        window = loudest(lead)
        whisper({"lead": lead[window], "mix": mix[window]}, args.lang)

        lyrics = args.song.with_suffix(".txt")
        mms(lead, lyrics if lyrics.exists() else None)
        print(f"  {'total':<28} {time.perf_counter() - started:6.1f} s")


if __name__ == "__main__":
    main()
