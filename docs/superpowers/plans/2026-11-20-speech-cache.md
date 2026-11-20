# Speech cache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reuse ffsubsync's saved speech analysis per video, and optionally pre-analyse new videos in ticked libraries.

**Architecture:** A `SpeechCache` owns the cache folder and work links. `SubtitleAlignmentService` uses it for alignments and gains `PreAnalyseAsync`. The watcher feeds a two-priority queue from `ItemUpdated` (alignment), `ItemAdded` (pre-analysis) and cleans up on `ItemRemoved`.

**Tech Stack:** C# / .NET 10, Jellyfin.Controller 12.1.0, xUnit, plain JS config page.

**Spec:** `docs/superpowers/specs/2026-11-20-speech-cache-design.md`

## Global Constraints

- One commit at the end on the owner's go; no push or release without it.
- Plugin GUID unchanged; next release `0.3.0.0`.
- No files written into media folders.
- Existing behaviour unchanged when no cache entry exists and nothing is ticked for pre-analysis, apart from the analysis now being saved.

## Review Focus

1. Video replaced or still being copied when analysed: size/mtime in the key must make the old analysis miss. Test in Task 1.
2. ffsubsync fails or times out: no `.npz` stored, work folder removed. Test in Task 2.
3. Cache folder unwritable or symlink creation fails: alignment still runs uncached against the real video path. Test in Task 2.
4. Pre-analysis flood (a whole season added): alignments still go first. Test in Task 3.
5. Upgrade from 0.2.0.0 XML without `PreAnalyseLibraryIds`: loads as empty (off). Test in Task 3.

---

### Task 1: SpeechCache

**Files:** Create `src/.../Services/SpeechCache.cs`; Test `tests/.../SpeechCacheTests.cs`.

**Interfaces (produces):**
- `public sealed class SpeechCache(string directory)`
- `string? TryGet(string videoPath)` — path of a valid entry, or null (also null when the video is missing).
- `string? CreateWorkLink(string videoPath)` — new work folder with a symlink named `ref<ext>`; returns the link path, or null on failure.
- `static string ProducedPath(string workLink)` — `Path.ChangeExtension(workLink, ".npz")`.
- `void Store(string videoPath, string producedNpz)` — moves it to the entry path, deleting other entries for the same video.
- `void DeleteWorkLink(string workLink)` — deletes the work folder, never throws.
- `void Remove(string videoPath)` — deletes all entries for the video.

- [ ] Tests: `TryGet_NoEntry_Null`, `Store_ThenTryGet_ReturnsEntry`, `TryGet_VideoChangedAfterStore_Null` (rewrite the video with different length), `Store_ReplacesOlderEntryForSameVideo`, `Remove_DeletesEntries`, `CreateWorkLink_PointsAtVideo` (link target equals video path, `ProducedPath` ends `ref.npz`), `DeleteWorkLink_RemovesFolder`.
- [ ] RED, implement, GREEN.

### Task 2: Use the cache in alignment; add pre-analysis

**Files:** Modify `FfSubSyncArgumentBuilder.cs`, `SubtitleAlignmentService.cs`, `ISubtitleAlignmentService.cs`, `PluginServiceRegistrator.cs`, `tests/.../FakeProcessRunner.cs` (add `Action<IReadOnlyList<string>>? OnRun`).

**Interfaces (produces):**
- `FfSubSyncArgumentBuilder.Build(string reference, string? subtitlePath, string? outputPath, string? extraArguments = null, string? ffmpegDirectory = null, bool serializeSpeech = false)` — `-i`/`-o` only when given; `--serialize-speech` when set.
- `SubtitleAlignmentService` ctor gains optional last parameter `SpeechCache? speechCache = null`.
- `Task<bool> ISubtitleAlignmentService.PreAnalyseAsync(string videoPath, CancellationToken)` — true when an analysis is (now) cached.

- [ ] Tests (`SpeechCacheAlignmentTests`): cached entry → first argument is the `.npz` and no `--serialize-speech`; no entry → reference is a work link, `--serialize-speech` present, and the `.npz` the fake writes is stored; non-zero exit → nothing stored, work folder gone; `CreateWorkLink` failure (cache dir is a file) → reference is the real video path, alignment still succeeds; `PreAnalyseAsync` with no entry runs once without `-i` and stores; with an entry it does not run.
- [ ] Existing argument-builder and service tests still pass (adjust only call shapes).
- [ ] Register `SpeechCache` in DI at `Path.Combine(IApplicationPaths.CachePath, "subtitle-auto-align")` and pass it to the service.

### Task 3: Priority queue, pre-analysis trigger, cleanup, settings

**Files:** Create `Services/PriorityWorkQueue.cs`; modify `SubtitleDownloadWatcher.cs`, `AutoAlignScope.cs`, `PluginConfiguration.cs`, `configPage.html`, `README.md`.

**Interfaces (produces):**
- `PriorityWorkQueue<THigh, TLow>`: `bool TryEnqueueHigh(THigh)`, `bool TryEnqueueLow(TLow)`, `ValueTask<(THigh? High, TLow? Low)> DequeueAsync(CancellationToken)` — always returns a pending high item before any low one; `Complete()`.
- `AutoAlignScope.IsInSelectedLibrary(IEnumerable<Guid> itemLibraryIds, IEnumerable<string>? selectedLibraryIds)` — true only when an item library is selected.
- `PluginConfiguration.PreAnalyseLibraryIds : string[]` default empty.

- [ ] Tests: `HighBeforeLow_WhenBothQueued`, `LowServed_WhenNoHigh`, `DequeueWaits_UntilItemArrives`; `IsInSelectedLibrary` (none selected → false, match in any format → true, null → false); XML without `PreAnalyseLibraryIds` → empty.
- [ ] Watcher: replace the channel with the queue; `ItemAdded` → Movie/Episode with a path in a selected library → `TryEnqueueLow(path)`; `ItemRemoved` → `SpeechCache.Remove(path)`; worker calls `AlignAsync` or `PreAnalyseAsync`.
- [ ] Config page: section "Analyse new videos in advance" with its own library checkboxes (ticked = selected), description about CPU cost; saves `PreAnalyseLibraryIds`.
- [ ] Verify in Jellyfin 12.4: second subtitle for a video uses the cache (log + speed), adding a video to a ticked library logs pre-analysis and creates an entry, removing it deletes the entry, settings round-trip.
