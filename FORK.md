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

## Distribution

No installer — KikiVRCTan is portable. The workflow publishes a
self-contained, single-file win-x64 build (`PublishSingleFile`; the zip holds
just the two exes, `silero_vad.onnx`, and `version.txt`) as
`KikiVRCTan-portable.zip`, attached to the `feed-overlay-latest` prerelease
(and as a workflow artifact).

Download, extract anywhere, run `KikitanTranslator.WinForms.exe`. Everything
(app, `config.json`, `logs/`) stays in that one folder: no %APPDATA%, no
registry, no install step, no admin rights. To update, extract the new zip
over the same folder — the zip contains no `config.json`, so your settings
survive.

A simple self-updater (`Updater.cs`) checks `version.txt` on the release at
startup; on mismatch it downloads the zip in the background and shows a
"Restart to update" button, which applies the update via a batch script
(preserving `config.json`) and restarts the app.

## Tests

`tests/FeedTests` — Linux-runnable: feed pipe round-trip (skipped on Linux,
named MMFs are Windows-only) and `FeedRenderer` scenarios saved as PNGs for
visual inspection. The WinForms UI and OpenVR overlay need real Windows.
