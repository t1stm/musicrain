# Gaida.Platforms.MusicDatabase

The local music library, as library code. It scans a folder tree of audio files, reads their tags, extracts their album art, and answers searches, artist and album lookups, folder browsing and random picks out of what it found. IDs it hands out are prefixed `audio://`, and its content getter serves the file straight off disk.

This project is the platform, not a deployable. [Gaida.Pods.MusicDatabase](../Gaida.Pods.MusicDatabase) wraps it in HTTP; [Gaida.Bot](../../Services/Gaida.Bot) uses it in-process.

Two environment variables shape it: `STORAGE` is the library root, `ALBUM_COVERS` where extracted art is written, and `DOMAIN` is the public prefix substituted into each track's cover URL.

## Using it

Not a deployable — it is referenced by [Gaida.Pods.MusicDatabase](../Gaida.Pods.MusicDatabase) and by [Gaida.Bot](../../Services/Gaida.Bot), both of which construct `MusicManager` and let it scan. `ffprobe`, `metaflac` and `wvunpack` — the last from `wavpack` — are expected on `PATH`.

```bash
dotnet test Tests/Pods.Tests         # from Backend/ — the matcher's calibration lives here
```

## Interesting techniques

- **A calibrated match, not a similarity score.** [MusicManager.Match.cs](Manager/MusicManager.Match.cs) weights title against artist 0.65/0.35 over Levenshtein distance and grades the result `Same`, `Variant` or `Weak`. The thresholds come from a 2000-title pass over the real library — every match at 0.806 and above was right, and the wrong answers start at 0.783 — and the file records which titles set them, so they can be re-derived when the library's tagging habits change. A weak match also has to agree on length within 20 seconds; a strong one never has to, since uploads carry intros.
- **A versioned scan.** Each entry is stamped with the tag-reading pass that produced it. Bumping `ScanVersion` re-reads every entry stamped below it, once, on the next load — which is how the scanner learned to read the album tag without a manual migration.
- **Covers deduplicated by content hash.** [CoverExtractor.cs](Manager/CoverExtractor.cs) pulls embedded art out of every folder in parallel and names each file by its hash, so one album's art is written once however many tracks carry it.
- **A cover URL with a placeholder in it.** Entries are stored with `$[DOMAIN]` in place of the host and substituted on load, so the same library serves correct absolute URLs on localhost and in production with no rewrite pass.
- **Four tag readers behind one interface.** `ffprobe` supplies the metadata; embedded art comes from [Id3v2.cs](Manager/Id3v2.cs) through TagLib#, or from `metaflac` and `wvunpack` for the two formats it does not cover.
- **The file is the authority, the index is its projection.** Lyrics live as `.lrc` or `.txt` files beside the audio, written by `stih` rather than by anything here. [MusicManager.Lyrics.cs](Manager/MusicManager.Lyrics.cs) rewrites each entry's `LyricsType` and `LyricsSource` from two `File.Exists` calls on every scan — cheap enough to do for every song, and it means a hand-deleted sidecar cannot leave an entry pointing at nothing. Deliberately not a `ScanVersion` bump: that would re-run `ffprobe` over the whole library to learn something two `stat` calls already know.
- **A single edit gate.** Admin edits serialise on one `SemaphoreSlim` for the whole library rather than one per folder — edits arrive at the rate a person clicks Save, and the work under it is a dictionary lookup and one small file write.

## Technologies worth a look

- [TagLib#](https://github.com/mono/taglib-sharp) (`taglib-sharp-netstandard2.0`) for ID3v2 tags and embedded pictures
- [FFmpeg](https://ffmpeg.org/) — `ffprobe` for metadata, `metaflac` for FLAC art
- [WavPack](https://www.wavpack.com/) — `wvunpack` for WavPack art, and for decoding a hybrid track against its `.wvc` correction file
- [Serilog](https://serilog.net/), through the shared [Gaida.Core](../../Gaida%20Library/Gaida.Core) abstractions

## Project structure

```
.
├── Getters/
├── Manager/
└── Search Providers/
```

[Manager](Manager) is the library itself: the scan, the tag readers, the cover extractor and the matcher. `MusicManager` is a partial class split across [MusicManager.cs](Manager/MusicManager.cs) (scan, load, edits), [MusicManager.Match.cs](Manager/MusicManager.Match.cs) (matching and its calibration constants) and [MusicManager.Lyrics.cs](Manager/MusicManager.Lyrics.cs) (sidecar reconciliation, and the two answers `stih` asks for).

[Search Providers](Search%20Providers) turns the in-memory song list into search, artist, album, browse and random results.

[Getters](Getters) holds a single `MusicGetter` at priority 99 — the library is always the fastest source, so nothing outranks it.
