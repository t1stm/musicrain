# Lirika's studio back end. The build context is Tools/Lirika (compose: ../Tools/Lirika).

# The venv, built where there is a compiler: ctc-forced-aligner is a git source with a C++ extension.
FROM python:3.13-slim-trixie AS build
RUN apt-get update && apt-get install -y --no-install-recommends git g++ && rm -rf /var/lib/apt/lists/*
COPY --from=ghcr.io/astral-sh/uv:0.13.6 /uv /bin/uv
WORKDIR /app

# Which hardware the dependency set is for: pyproject.cpu.toml (the default, so a fresh checkout builds),
# pyproject.amd.toml or pyproject.nvidia.toml, or this host's own pyproject.toml.
ARG PYPROJECT=pyproject.cpu.toml
COPY ${PYPROJECT} pyproject.toml
ENV UV_COMPILE_BYTECODE=1 UV_LINK_MODE=copy
RUN uv sync --no-dev --no-install-project

# The same base, so the venv's interpreter links resolve; ffmpeg decodes the audio.
FROM python:3.13-slim-trixie
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/.venv .venv
COPY *.py ./
# Every model cache Lirika and its libraries use lives under $HOME/.cache: lirika/ (separators, stems, edits,
# textove pages), huggingface/ (Whisper, BuzzASR, the aligner) and torch/ (beat-this). One mount covers all.
# The container runs as the library's owner, whoever that is on the host, so HOME is open to any uid, and
# numba (librosa's) keeps its cache there too rather than failing to write into the root-owned venv.
ENV HOME=/home/lirika LIRIKA_STUDIO_HOST=0.0.0.0 NUMBA_CACHE_DIR=/home/lirika/.cache/numba PYTHONUNBUFFERED=1
RUN mkdir -m 1777 /home/lirika
EXPOSE 8765
CMD ["/app/.venv/bin/python", "studio.py"]
