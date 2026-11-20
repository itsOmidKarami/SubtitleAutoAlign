# Per-library auto-alignment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Auto-align subtitles for TV episodes as well as movies, limited to libraries the user ticks, and fix the settings page not showing saved values.

**Architecture:** The hosted `SubtitleDownloadWatcher` gains a pure filter (item type + library) fed by `ILibraryManager.GetCollectionFolders`. The config gets `DisabledLibraryIds`; the config page renders one checkbox per library from `ApiClient.getVirtualFolders()`. The settings bug is reproduced in a Jellyfin 12.4 container first and fixed at its cause.

**Tech Stack:** C# / .NET 10, Jellyfin.Controller 12.1.0, xUnit, plain JS config page, Docker for the 12.4 repro.

**Spec:** `docs/superpowers/specs/2026-11-20-library-autoalign-design.md`

## Global Constraints

- Repo: `~/Projects/SubtitleAutoAlign`, remotes `origin` = `itsOmidKarami/SubtitleAutoAlign`, `upstream` = `Mathis-Z/SubtitleAutoAlign`.
- No commits per task. One commit at the end, only on the owner's go. Push, enabling Actions on the fork, and running the Release workflow each need the owner's go.
- No upstream pull request.
- Plugin GUID stays `8f5c9d3a-6b1e-4a2f-9c7d-2e1b4f6a8c3d`. Release version `0.2.0.0`. `TARGET_ABI` stays `12.1.0.0`.
- "Align all subtitles" task behaviour is unchanged and ignores the library checkboxes.
- Default config (fresh install or upgrade from 0.1.5) watches every library.

## Review Focus

1. Upgrade from 0.1.5: an existing XML config without `DisabledLibraryIds` must load as "all libraries watched", not fail or watch nothing. Test in Task 2.
2. Library ID format: the web API returns IDs as 32-hex strings without dashes; `BaseItem.Id` is a `Guid`. Matching must be format-insensitive. Test in Task 2.
3. An item with no collection folder (e.g. not yet attached to a library) keeps today's behaviour: watched. Test in Task 2.
4. Saving the page with the Timeout field empty must not store 0/null and must not fail silently. Covered in Task 1.
5. A library removed in Jellyfin but still in `DisabledLibraryIds` must not break the page or the watcher (stale IDs are ignored). Test in Task 2.

---

### Task 1: Reproduce and fix the settings save/load bug

**Files:**
- Modify: `src/Jellyfin.Plugin.SubtitleAutoAlign/Configuration/configPage.html` (load/save script)
- Possibly modify: `src/Jellyfin.Plugin.SubtitleAutoAlign/Configuration/PluginConfiguration.cs`
- Test: `tests/Jellyfin.Plugin.SubtitleAutoAlign.IntegrationTests/` (container round-trip, if the cause is server-side)

**Interfaces:**
- Produces: a working `loadConfig(view)` / `saveConfig(view)` pair that Task 3 extends.

- [ ] **Step 1: Reproduce.** `dotnet publish src/Jellyfin.Plugin.SubtitleAutoAlign -c Release -o /tmp/saa-out`, run `jellyfin/jellyfin:12.4` with it under `/config/plugins/Subtitle Auto Align_0.2.0.0/`, finish the setup wizard, open the plugin page in the browser pane. Record: does the Timeout field show 300 on first load; after Save; after reload. Capture the GET/POST `/Plugins/8f5c9d3a-6b1e-4a2f-9c7d-2e1b4f6a8c3d/Configuration` requests and the XML in `/config/plugins/configurations/`.
- [ ] **Step 2: Name the root cause** in one sentence with evidence (request body, response status, XML content, or console error). Do not fix before this.
- [ ] **Step 3: Fix at the cause.** Whatever it is, also: `saveConfig` falls back to `300` when the Timeout field is empty or not a positive integer, and both `getPluginConfiguration` and `updatePluginConfiguration` promises get a rejection handler that calls `Dashboard.hideLoadingMsg()` and `Dashboard.alert(...)`.
- [ ] **Step 4: Verify** in the 12.4 container: set Timeout 450 and untick auto-align, Save shows Jellyfin's saved toast, reload shows 450 and unticked, XML holds `<TimeoutSeconds>450</TimeoutSeconds>`. Empty Timeout + Save stores 300.

### Task 2: Library filter and episode support in the watcher

**Files:**
- Modify: `src/Jellyfin.Plugin.SubtitleAutoAlign/Configuration/PluginConfiguration.cs`
- Create: `src/Jellyfin.Plugin.SubtitleAutoAlign/Services/AutoAlignScope.cs`
- Modify: `src/Jellyfin.Plugin.SubtitleAutoAlign/EventSubscribers/SubtitleDownloadWatcher.cs` (`OnItemUpdated`)
- Test: `tests/Jellyfin.Plugin.SubtitleAutoAlign.Tests/AutoAlignScopeTests.cs`

**Interfaces:**
- Produces: `PluginConfiguration.DisabledLibraryIds : string[]` (default `Array.Empty<string>()`), XML element name `DisabledLibraryIds`.
- Produces: `public static bool AutoAlignScope.IsWatchedType(BaseItem item)` — true for `Movie` and `Episode` only.
- Produces: `public static bool AutoAlignScope.IsInEnabledLibrary(IEnumerable<Guid> itemLibraryIds, IEnumerable<string> disabledLibraryIds)` — parses disabled IDs with `Guid.TryParse` (skips unparsable), returns true when the item has no library IDs or when any of its library IDs is not disabled.

- [ ] **Step 1: Write failing tests** in `AutoAlignScopeTests`:
  - `IsWatchedType_Movie_True`, `IsWatchedType_Episode_True`, `IsWatchedType_Trailer_False` (use `new Movie()`, `new Episode()`, `new Trailer()`).
  - `IsInEnabledLibrary_NoDisabled_True`.
  - `IsInEnabledLibrary_LibraryDisabled_False`: library `g`, disabled `[g.ToString("N")]`.
  - `IsInEnabledLibrary_DashedAndUndashedIdsMatch_False`: library `g`, disabled `[g.ToString("D").ToUpperInvariant()]`.
  - `IsInEnabledLibrary_NoLibraries_True`: `[]`, disabled `[anything]`.
  - `IsInEnabledLibrary_StaleOrGarbageDisabledIds_Ignored_True`: library `g`, disabled `[Guid.NewGuid().ToString("N"), "not-a-guid"]`.
  - `Config_DefaultDisabledLibraryIds_IsEmpty`.
  - `Config_XmlWithoutDisabledLibraryIds_DeserializesToEmpty`: `XmlSerializer(typeof(PluginConfiguration))` on a 0.1.5-shaped XML with only the five old elements.
- [ ] **Step 2: Run** `dotnet test tests/Jellyfin.Plugin.SubtitleAutoAlign.Tests --filter AutoAlignScopeTests`. Expected: build failure / FAIL.
- [ ] **Step 3: Implement** `AutoAlignScope` and the config property.
- [ ] **Step 4: Wire the watcher.** In `OnItemUpdated`, replace the `Movie` check with `AutoAlignScope.IsWatchedType(e.Item)`, use `Video`'s `Path`, and after the snapshot update (next to the existing `EnableAutoAlign` check, same reasoning) return early when `!AutoAlignScope.IsInEnabledLibrary(_libraryManager.GetCollectionFolders(e.Item).Select(f => f.Id), config.DisabledLibraryIds)`, logging the item name at Information. Rename the `movie` local to `video`; keep log wording otherwise.
- [ ] **Step 5: Run** `dotnet test tests/Jellyfin.Plugin.SubtitleAutoAlign.Tests`. Expected: all pass.

### Task 3: Library checkboxes on the settings page

**Files:**
- Modify: `src/Jellyfin.Plugin.SubtitleAutoAlign/Configuration/configPage.html`

**Interfaces:**
- Consumes: `DisabledLibraryIds` (Task 2), fixed `loadConfig`/`saveConfig` (Task 1).

- [ ] **Step 1: Markup.** Under the "Enable automatic alignment" container add a `<div id="LibraryList">` with heading text "Watch these libraries" and description "New subtitles in unticked libraries are not aligned automatically. Libraries you add later are watched." Update the page intro to "…newly downloaded subtitles for movies and TV episodes…".
- [ ] **Step 2: Load.** In `loadConfig`, fetch `ApiClient.getVirtualFolders()` together with the config; render one `emby-checkbox` per folder (`data-library-id = folder.ItemId`, label `folder.Name`), ticked unless its ID is in `DisabledLibraryIds` (compare lowercased, dashes stripped).
- [ ] **Step 3: Save.** In `saveConfig`, set `config.DisabledLibraryIds` to exactly the `data-library-id` of every unticked box; IDs of libraries no longer in Jellyfin are dropped.
- [ ] **Step 4: Verify** in the 12.4 container with a Movies and a Shows library: untick Shows, Save, reload → Shows unticked, XML lists its ID. Tick it again → list empty.

### Task 4: End-to-end check, docs, release prep

**Files:**
- Modify: `manifest.json` (`imageUrl` → `https://raw.githubusercontent.com/itsOmidKarami/SubtitleAutoAlign/main/icon.png`), `README.md` (install URL → `https://raw.githubusercontent.com/itsOmidKarami/SubtitleAutoAlign/main/manifest.json`, describe TV + library checkboxes, note the fork).

- [ ] **Step 1: E2E in 12.4.** Shows library with one episode + speech clip from `tests/.../Fixtures/us-now-5min.mp4`; drop a shifted `.en.srt` next to it, refresh the item; expect `…en.autoaligned.srt` and the log line `Successfully aligned subtitle`. Untick the library, add another subtitle, refresh; expect no alignment and the "not in a watched library" log line.
- [ ] **Step 2: Full suite** `dotnet test` (unit) passes; `curl` the new README install URL after push returns 200.
- [ ] **Step 3: Owner's go**, then: one commit, push to `origin/main`, enable Actions on the fork, run Release with version `0.2.0.0`, changelog "TV episodes, per-library auto-align, settings page fix". Verify the manifest URL returns 200 and the zip checksum matches.
