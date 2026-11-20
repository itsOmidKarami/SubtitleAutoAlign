# Per-library auto-alignment, TV episodes, and the settings bug

Short design, approved 2026-11-20.

## Why

The plugin only auto-aligns subtitles for movies; TV episodes are ignored by
the watcher (`if (e.Item is not Movie movie) return;`). Also, on Jellyfin 12.4
the settings page does not show saved values after a save or refresh (the
Timeout field comes back empty), so settings may not be saving at all.

## Goals

- Auto-align new subtitles for TV episodes as well as movies.
- Let the user choose which libraries the watcher handles, as one checkbox per
  library under the existing "Enable automatic alignment" toggle.
- Find and fix the settings save/load bug.
- Release from the fork so it installs from the fork's manifest.

## Non-goals

- No change to the "Align all subtitles" task: it ignores the library
  checkboxes and behaves as today.
- No upstream pull request unless the owner asks.
- Music videos, trailers and extras stay out of auto-alignment.

## Approaches

1. **Patch the watcher and settings page in a fork** (chosen). Small change to
   existing code.
2. Separate sidecar container: duplicates what the plugin already does.
3. Upstream PR only: depends on the upstream author's timing.

## Decisions

- Library selection is checkboxes, not radio buttons, so several libraries can
  be watched at once (owner's choice).
- The config stores the libraries the user *unticked*
  (`DisabledLibraryIds`), so a library added later is watched by default
  (builder's call).
- The watcher accepts `Movie` and `Episode` items whose library is not
  disabled (builder's call).
- Debug the settings bug first, by reproducing it in a Jellyfin 12.4
  container, before adding the library list on top of the same save code.
- Release as `0.2.0.0`; manifest and README URLs point at
  `itsOmidKarami/SubtitleAutoAlign`. Commit, push and release each wait for
  the owner's go.

## Left to the builder

- How to map an item to its library (`ILibraryManager` collection folders vs.
  path prefix), verified against Jellyfin 12.x.
- Test shape for the watcher filter and the config page fix.
