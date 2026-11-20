# Speech analysis cache and opt-in pre-analysis

Short design, approved 2026-11-20.

## Why

On the owner's NAS (Intel N100) most of an alignment is ffsubsync's audio
analysis: a 3 h 17 min movie took 63 s per subtitle, and three subtitles for
one movie queued for over 3 minutes. Reusing a saved analysis cut that to
9-10 s with byte-identical output (bundled ffsubsync 0.5.1,
`--serialize-speech`, then the `.npz` as reference). Episodes gain little
(7 s to 4 s).

## Goals

- Reuse a video's saved analysis for every alignment after its first,
  including "Align all subtitles". Always on.
- Optionally analyse newly added movies/episodes before any subtitle exists,
  chosen per library; every library starts unticked.
- Real alignments always run before pre-analysis.

## Non-goals

- Analysing videos already in the library.
- Choosing the audio track (ffsubsync keeps using the first one).

## Approaches

1. **Extend the plugin's existing service, queue and settings** (chosen).
2. Separate sidecar service: duplicates the plugin's watcher and queue.

## Decisions

- Cache lives in Jellyfin's data folder (`<DataPath>/subtitle-auto-align`; not CachePath, which Jellyfin prunes after 30 days),
  never next to media. File name `<sha256(video path)>-<size>-<mtime ticks>.npz`,
  so a replaced or still-copying video never matches an old analysis.
- ffsubsync writes the `.npz` next to its reference, so it runs against a
  symlink to the video inside a temporary work folder in the cache folder.
- Pre-analysis is a library checkbox list only ("Analyse new videos in
  advance"); nothing ticked means off. Stored as inclusions
  (`PreAnalyseLibraryIds`), unlike alignment's exclusions, because it is opt-in.
- Triggered by `ILibraryManager.ItemAdded` for Movie/Episode items.
- Saved analyses are deleted on `ILibraryManager.ItemRemoved`.
- One worker: alignment jobs are always taken before pre-analysis jobs.
