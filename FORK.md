# Fork notes — `feed-overlay` branch

Fork of [YusufOzmen01/kikitan-translator](https://github.com/YusufOzmen01/kikitan-translator)
(`upstream` remote). This branch adds a VRCT-style in-memory translation feed
and a native Windows UI, without touching the speech/translation backend.

## What changed

**New: `KikitanTranslator.WinForms`** — native WinForms host, no webview/WebView2
(upstream's `KikitanTranslator.Photino` project is untouched and still builds).

- `Program.cs` — entry point: Serilog `Logger.Initialize()`, `AppConfig.Load()`,
  runs `MainForm`.
- `AppEngine.cs` — pipeline orchestration, mirrors upstream `Photino/Manager`
  (same recognizers/translators/capture, chatbox + OSC outputs, mic fallback
  watcher, overlay process launch). Adds the translation feed as a plain
  `IOutput` (`Custom`) on both mic and desktop pipelines, and pushes feed
  frames to the overlay through the `kikitan-feed` pipe.
- `TranslationFeed.cs` — thread-safe in-memory ring buffer (200 entries,
  like VRCT: no persistence). Incoming (desktop) speech only by default;
  `feed_show_own` opts the user's own mic transcription back in.
- `FeedPipeWriter.cs` — JSON over a named MMF (`kikitan-pipe` is upstream's
  legacy single-message pipe; `kikitan-feed` is new and carries entries +
  overlay config).
- `MainForm.cs` — settings UI (languages, recognizer/translator, mic,
  VRChat/chatbox options, API keys, overlay settings) bound to the shared
  `AppConfig` (`%APPDATA%/Kikitan Translator/config.json`, same file as
  upstream), plus a live feed list.
- `Languages.cs` — generated from upstream
  `KikitanTranslator.Photino/UserInterface/src/util/constants.ts`.
- `OscWatcher.cs` — copied from upstream Photino (mute detection), namespace
  adjusted.

**`KikitanTranslator.Base`** — one additive edit, marked `// FORK`:

- `Utility/AppConfig.cs`: overlay/feed settings
  (`overlay_max_entries`, `overlay_font_size`, `overlay_opacity`,
  `overlay_anchor`, `overlay_width_cm`, `overlay_show_timestamps`,
  `overlay_show_original`, `overlay_display_duration_sec`,
  `overlay_fadeout_duration_sec`, `overlay_pos_x/y/z`,
  `desktop_overlay_enabled`). Unknown to upstream builds, which ignore extra
  JSON members.

**`KikitanTranslator.Overlay`** — additive + small marked edits:

- `FeedPipe.cs` (new) — JSON DTOs + tolerant `FeedPipeReader`
  (returns null when the pipe is absent).
- `FeedRenderer.cs` (new) — SkiaSharp multi-line feed renderer, extracted
  from `OverlayServer` so it can be tested in isolation (`tests/FeedTests`).
  Font size is fixed from config; **the box grows with content** (up to a cap,
  oldest lines trimmed) instead of shrinking text. Desktop entries render
  light-blue, mic entries white, timestamps gray.
- `OverlayServer.cs` — `StartFeedReader` poll loop; `SendFeed` routes rendered
  feed frames to the desktop form / VR overlay.
- `OpenVROverlay.cs` — anchor presets (HMD / left hand / right hand, with HMD
  fallback when a controller isn't tracked), VRCT-like position offsets,
  display duration + fadeout, persistent feed display, per-tick transform
  updates for hand tracking, width/opacity from config. Legacy timed
  single-message path preserved.
- `Form1.cs` — `SetFeedImage` (persistent, optional display duration) /
  `ClearImage`; feed drawn larger than legacy messages.
- `Program.cs` — watchdog also accepts the WinForms host process; VR overlay
  loop moved to a background thread so the desktop form runs alongside it.

`sln` — WinForms project added. The WinForms build also builds + stages
`KikitanTranslator.Overlay.exe` next to its own exe.

## Upstream sync strategy

- Every fork change is marked `// FORK (feed-overlay):` — grep for it.
- Rule: **additive only** in shared projects (`Base`, `Overlay`). New files or
  new blocks; no signature changes, no restructures. The only modified shared
  file with logic is `OverlayServer.cs`/`OpenVROverlay.cs`/`Form1.cs`
  (overlay), each change fenced by `// FORK` comments.
- To pull upstream: `git fetch upstream && git merge upstream/main`
  (resolve; the `// FORK` blocks are small and localized). The Photino web UI
  is untouched, so its churn never conflicts with the WinForms host.

## Installer

Inno Setup (`installer/kikivrctan.iss`), producing a single
`KikiVRCTan-Setup.exe` — no zip to extract. Branded **KikiVRCTan** (Start menu
entry, window title, setup name). Repo, assemblies, namespaces and pipe names
keep the kikitan names.

- Install location: the folder the setup is run from, when that folder holds
  nothing but the setup itself; otherwise `%LocalAppData%\KikiVRCTan`. The
  user can change it on the directory page. Per-user, no elevation.
- Everything lives in that one folder: app, `config.json`, `logs/`. No
  `%APPDATA%` usage. On first run next to the exe, an existing config is
  migrated from the legacy `%APPDATA%\Kikitan Translator\config.json`.
- No auto-updater (Velopack was removed — its LocalAppData model fought the
  above). To update: download the new setup and run it over the same folder.
- Publish is self-contained win-x64 (no .NET runtime prerequisite).
- Inno's compiler only runs on Windows, so the installer is built by
  `.github/workflows/winforms-installer.yml` on `windows-latest`
  (manual dispatch; uploads just `KikiVRCTan-Setup.exe` as artifact
  `KikiVRCTan-setup`). The same workflow runs `tests/FeedTests` on Linux.
  Local build: publish both projects to
  `KikitanTranslator.WinForms/publish/app`, then
  `iscc installer\kikivrctan.iss`.

## Tests

`tests/FeedTests` — Linux-runnable: feed pipe round-trip (skipped on Linux,
named MMFs are Windows-only) and `FeedRenderer` scenarios saved as PNGs for
visual inspection. The WinForms UI and OpenVR overlay need real Windows.
