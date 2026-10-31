# Lirika

Lirika turns a song file into line-synced lyrics: an `.lrc` file whose every line starts when it is
sung. It works for Bulgarian (`bg`) and English (`en`). *Лирика* is Bulgarian for lyric poetry.

When it has the song's words, from you or from textove.com, it times those words to the audio. When it
has none, it transcribes the song and times the transcription.

## Setup

You need Linux on x86-64, [uv](https://docs.astral.sh/uv/) and `ffmpeg` on the `PATH`. A GPU is
optional: without one, everything runs on the CPU, more slowly.

Pick the file for your hardware, copy it to `pyproject.toml`, and install:

```bash
cd Tools/Lirika
cp pyproject.amd.toml pyproject.toml     # or pyproject.nvidia.toml, or pyproject.cpu.toml
uv sync
```

| File | Hardware | Notes |
| --- | --- | --- |
| `pyproject.amd.toml` | AMD GPU (ROCm) | Needs the `amdgpu` driver and your user in the `render` and `video` groups. Installs kernels for every AMD GPU (~6.5 GB). The comment in the file shows how to keep only yours (~320 MB). |
| `pyproject.nvidia.toml` | NVIDIA GPU (CUDA 13.0) | Needs driver 580 or newer. For an older driver, change the index to `cu126`. |
| `pyproject.cpu.toml` | No GPU | Everything runs on the CPU. |

`pyproject.toml` and `uv.lock` are your machine's own, and git ignores both. A GPU run wants about
8 GB of VRAM, because the models stay loaded for the whole batch.

## Usage

```bash
uv run lirika.py "Artist - Song.flac" --lang bg            # writes "Artist - Song.lrc" beside it
uv run lirika.py SONG --lang en --text lyrics.txt          # use words you already have
uv run lirika.py SONG --lang bg --out - --no-lookup        # print it, and make no web requests
uv run lirika.py SONG1 SONG2 SONG3 --lang bg               # a batch: the models load once
```

| Option | What it does |
| --- | --- |
| `--lang bg\|en` | The language that is sung. Required. |
| `--text FILE` | The words, as plain text or LRC. One song only. |
| `--no-lookup` | Do not look the words up online. |
| `--out FILE` | Where to write the result; `-` prints it. By default, the `.lrc` goes beside the audio. One song only. |
| `--force` | Replace an existing `.lrc`, re-timing its words. Without it, a song that has one is skipped. |
| `--register` | Hand the result to the stih lyrics service instead of writing it. See below. |
| `--keep` | Keep the instrumental stem and the decoded mix. |
| `--dump DIR` | Also write the raw transcription and word timings into `DIR`. |
| `--by NAME` | The LRC file's author, written as its `[by:]` tag. |

**Where the words come from.** The first source that has them wins:

1. `--text`.
2. A `.txt` with the same name beside the audio.
3. With `--force`, the `.lrc` being replaced.
4. textove.com (Bulgarian only), when what it has matches what is sung.
5. Nothing: the song is transcribed.

Section labels such as "Припев:" or "Chorus" are dropped. Repeats such as "(x2)" are written out. A
chorus that the text names but never writes out is an error, so write it out once.

**The file's tags.** Each `.lrc` starts with these, and a tag with no value is left out:

```
[ti:Забрави]                    title, artist and album: from library.db, or from an
[ar:Руслан Мъйнов]              "Artist - Title" file name outside the library
[al:Дърпай шалтера]
[length:03:52]
[by:…]                          the LRC file's author, with --by or the studio's "by"
[re:musicrain/Lirika]
[ve:0.1]
[#:asr:BuzzASR/bulgarian]       the transcription model, when one was used
[#:aligner:DrishtiSharma/…]     the alignment model
[#:edited:no]                   the studio sets yes when you save changed lines
```

**Results and exit codes.** Lirika flags lines it is unsure of and lists them on stderr with their times,
so you know where to listen. Flags include a low alignment score, a line outside the sung parts, no
note starting near the line, and a sung line that is not in the text. An instrumental gets no file.

| Code | Meaning |
| --- | --- |
| `0` | Written cleanly. |
| `2` | Written, but some lines are flagged. |
| `1` | Failed. |

## Registering with stih

`--register` sends each result to the stih lyrics service, which saves it beside the audio and makes it
show in the player straight away. stih must be running on this machine, as part of the backend's
compose stack. Lirika reads stih's port and `ADMIN_TOKEN` from `Backend/.env`. Only songs in the music
library (`library.db`) can be registered.

Without `--register`, Lirika writes the `.lrc` itself. If stih has already looked for that song and
found nothing, the player may not show the file for up to 30 days (`LYRICS_RETRY_DAYS`), so use
`--register` for songs people have already played.

## Lyrics studio

The studio is a local page for correcting lyrics by hand, for Bulgarian library songs. It uses the
frontend's own player and lyrics pane as the preview.

```bash
cd Tools/Lirika && uv run studio.py      # the back end, on 127.0.0.1:8765
cd Frontend && npm run dev             # then open http://localhost:5173/studio
```

1. Search for a library song in the studio and play it. The studio works on whichever library song is
   playing.
2. Pick a starting text: your last saved edit, a `.txt` beside the audio, or a textove.com match. If
   textove has the song under another name, paste its page link. Edit the text and press
   **Regenerate**.
3. Open the full player. Every change to the LRC box shows in its lyrics pane straight away.
   - `Alt+Enter` sets the line under the cursor to the playhead.
   - `Alt+↑` and `Alt+↓` move it by 0.1 s.
   - Flagged lines are listed under the box. Click one to jump to it.
4. **Save** writes `~/.cache/lirika/edited/<song>.lrc`, with the text beside it as `<song>.txt`. If
   you changed the lines since Lirika wrote them, it sets `[#:edited:yes]`.

The studio exists in development builds only. A production build of the frontend leaves it
out. `studio.py` listens on 127.0.0.1 only and answers only pages served from `localhost`.

The studio unloads the models after 5 minutes without a Regenerate, which frees the GPU memory. The
next Regenerate takes 15–25 s longer while they load. Set `LIRIKA_STUDIO_IDLE` to a number of seconds to
change the wait.

## Models and caches

The models download on first use:

| Model | Used for | Size |
| --- | --- | --- |
| Karaoke separator (mel-band RoFormer) | The lead vocal | 1.7 GB |
| Vocal separator (BS-RoFormer) | Every voice, for timing | 0.6 GB |
| BuzzASR/bulgarian (bg), Whisper large-v2 (en) | Transcription | ~3 GB each |
| Bulgarian XLS-R 300M (bg), MMS 300M (en) | Timing the words | ~1.2 GB each |

| Path | What it holds |
| --- | --- |
| `~/.cache/lirika/models` | The separators |
| `~/.cache/huggingface` | Transcription and timing models |
| `~/.cache/lirika/<sha1>/` | Stems, per audio file, so a second run skips separation |
| `~/.cache/lirika/web/` | Fetched web pages |
| `~/.cache/lirika/edited/` | The studio's saved edits |

## Troubleshooting

- **These warnings are harmless:** `CUDAExecutionProvider not available` (from ONNX, which is not
  used), `xnack 'Off' was requested` (from the ROCm compiler), and transformers' deprecation notices
  about `generation_config`.
- **The first run is slow.** It downloads the models and, on AMD, compiles GPU kernels.
- **An AMD RX 9070 resets under load:** set `TORCH_BLAS_PREFER_HIPBLASLT=0`.
- **On AMD, an idle process keeps one CPU core busy.** This comes from the ROCm runtime, for as long as
  a process has the GPU open. It stops when Lirika exits, or when the studio unloads its models.

## Development

| File | What it is |
| --- | --- |
| `lirika.py` | The command line and every stage of the pipeline |
| `sources.py` | The textove.com lookup, the `library.db` lookup and the call to stih |
| `studio.py` | The studio's back end |
| `test_lirika.py` | Tests that need no GPU and no models: `uv run python test_lirika.py` |
| `eval.py` | Accuracy measurements. `uv run eval.py compare CHECKED.lrc GENERATED.lrc` scores a result against a hand-checked `.lrc`, `.srt` or `.ass` |
| `smoke.py` | Checks that every model runs on this machine, and times each one |

When you add a dependency, add it to all three `pyproject.*.toml` files and to your own
`pyproject.toml`. `test_lirika.py` fails if their shared part differs.
