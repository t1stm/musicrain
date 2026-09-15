# Build context is the repo root (see compose.yaml), matching the other pods' Dockerfiles.
# ponytail: no build stage. There is nothing to compile -- the image is the interpreter, the
# dependencies, and seven modules.
FROM python:3.12-slim

WORKDIR /app

COPY ["Platforms/Gaida.Pods.Deezer/requirements.txt", "./"]
RUN pip install --no-cache-dir -r requirements.txt

COPY ["Platforms/Gaida.Pods.Deezer/", "./"]

# Unbuffered, or the log lines sit in a pipe buffer and `docker compose logs` shows nothing.
ENV PYTHONUNBUFFERED=1
EXPOSE 8080
CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8080"]
