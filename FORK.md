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

Velopack, like upstream. The app is versioned `2.1.0` (fork line) and branded
**KikiVRCTan** (Velopack packId/title, Start menu entry, window title,
`KikiVRCTan-win-Setup.exe`). Repo, assemblies, namespaces and pipe names keep
the kikitan names.

- `VelopackApp.Build().Run()` in `Program.cs`; update checks against this
  fork's GitHub releases (download in background, apply on next launch —
  never restarts mid-session).
- `dotnet msbuild KikitanTranslator.WinForms/KikitanTranslator.WinForms.csproj -t:PackInstaller -p:Configuration=Release`
  publishes self-contained win-x64 (no .NET runtime prerequisite) and runs
  `vpk pack`. Output: `./Releases` (`Setup.exe`, nupkg, `RELEASES`; the
  portable zip is deleted — setup only).
- Install location: `%LocalAppData%\KikiVRCTan` by default. To install into
  the current directory instead: `KikiVRCTan-win-Setup.exe --installto "%CD%"`
  (official Velopack override).
- Config lives at `%APPDATA%\Kikitan Translator\config.json` regardless of
  install location, so settings (and the old install's) carry over
  automatically — nothing to copy.
- vpk's Windows packer only runs on Windows, so the installer is built by
  `.github/workflows/winforms-installer.yml` on `windows-latest`
  (manual dispatch; uploads just the Setup.exe as artifact `KikiVRCTan-setup`).
  The same workflow runs `tests/FeedTests` on Linux.

## Tests

`tests/FeedTests` — Linux-runnable: feed pipe round-trip (skipped on Linux,
named MMFs are Windows-only) and `FeedRenderer` scenarios saved as PNGs for
visual inspection. The WinForms UI and OpenVR overlay need real Windows.
