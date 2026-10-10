// FORK (feed-overlay): pipeline orchestration for the native WinForms host.
// Mirrors KikitanTranslator.Photino.Manager (upstream) without the web UI:
// same recognizers/translators/capture, plus the translation feed and the
// overlay feed pipe. Base is untouched; the feed is attached as a plain
// IOutput via Custom.

using System.Diagnostics;
using KikitanTranslator.Base;
using KikitanTranslator.Base.Outputs;
using KikitanTranslator.Base.Recognizers;
using KikitanTranslator.Base.Translators;
using KikitanTranslator.Capture;
using KikitanTranslator.Recognizers;
using KikitanTranslator.Utility;
using Serilog;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Structs;
using Velopack;
using Velopack.Sources;

namespace KikitanTranslator.WinForms;

public sealed class AppEngine : IDisposable
{
    public TranslationFeed Feed { get; } = new();

    public event Action<string>? StatusChanged;
    public event Action<bool>? MuteChanged;

    private Kikitan? _microphoneKikitan;
    private Kikitan? _desktopKikitan;
    private ITranslator? _translator;
    private Microphone? _mic;
    private Loopback? _loopback;
    private FeedPipeWriter? _feedWriter;
    private OscWatcher? _oscWatcher;

    private bool _running;
    private bool _isMuted;
    private bool _disposed;
    private System.Threading.Timer? _restartDebounce;

    public bool IsRunning => _running;
    public bool IsMuted => _isMuted;

    public DeviceInfo[] GetMicrophones()
    {
        var engine = new MiniAudioEngine(backendPriority: [MiniAudioBackend.Wasapi, MiniAudioBackend.Oss]);
        engine.UpdateAudioDevicesInfo();
        return engine.CaptureDevices;
    }

    public void Initialize()
    {
        var silero = Path.Combine(AppContext.BaseDirectory, "silero_vad.onnx");
        var errorHandler = new WinErrorHandler(msg => StatusChanged?.Invoke(msg));

        _mic = new Microphone(silero, errorHandler);
        _loopback = new Loopback(silero);

        _oscWatcher = new OscWatcher();
        _oscWatcher.MuteStatusChanged += muted =>
        {
            _isMuted = muted;
            MuteChanged?.Invoke(muted);
        };
        _oscWatcher.Start();

        Feed.EntryAdded += _ => PushFeedToOverlay();
        AppConfig.OnUpdate += OnConfigUpdated;

        try
        {
            _feedWriter = new FeedPipeWriter();
            PushFeedToOverlay();
        }
        catch (Exception e)
        {
            Log.Warning(e, "[FORK] Overlay feed pipe unavailable; overlay will not receive the feed");
        }

        LaunchOverlay();
        WatchMicrophone();
        CheckForUpdates();
    }

    // FORK (feed-overlay): Velopack update check against the fork's releases.
    // Downloads in the background; the update applies on the next launch
    // (VelopackApp hook in Program.cs). Never restarts mid-session.
    private void CheckForUpdates()
    {
        Task.Run(async () =>
        {
            try
            {
                var mgr = new UpdateManager(new GithubSource(
                    "https://github.com/MLG-SERBUR/kikitan-translator", null, true));
                var update = await mgr.CheckForUpdatesAsync();
                if (update == null) return;

                Log.Information("[FORK] Update available: {V}; downloading", update.TargetFullRelease.Version);
                await mgr.DownloadUpdatesAsync(update);
                StatusChanged?.Invoke($"Update {update.TargetFullRelease.Version} downloaded — restart to install");
            }
            catch (Exception e)
            {
                Log.Debug(e, "[FORK] Update check failed");
            }
        });
    }

    private void LaunchOverlay()
    {
        var overlayExe = Path.Combine(AppContext.BaseDirectory, "KikitanTranslator.Overlay.exe");
        if (!File.Exists(overlayExe))
        {
            Log.Warning("[FORK] KikitanTranslator.Overlay.exe not found next to the app; overlay disabled");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(overlayExe) { CreateNoWindow = true, UseShellExecute = false });
            Log.Information("[FORK] Overlay process launched");
        }
        catch (Exception e)
        {
            Log.Warning(e, "[FORK] Failed to launch overlay process");
        }
    }

    private void WatchMicrophone()
    {
        Task.Run(async () =>
        {
            while (!_disposed)
            {
                await Task.Delay(5000);
                if (_disposed) break;

                try
                {
                    var configured = AppConfig.ConfigObject.Microphone;
                    if (configured.Length == 0) continue;

                    var engine = new MiniAudioEngine(backendPriority: [MiniAudioBackend.Wasapi, MiniAudioBackend.Oss]);
                    engine.UpdateAudioDevicesInfo();
                    if (engine.CaptureDevices.Any(d => d.Name == configured)) continue;

                    var devices = engine.CaptureDevices.ToList();
                    if (devices.Count == 0) continue;

                    var fallback = devices.FirstOrDefault(d => d.IsDefault);
                    if (fallback.Name == null) fallback = devices[0];

                    Log.Warning("[FORK] Configured mic gone; falling back to {Mic}", fallback.Name);
                    AppConfig.ConfigObject.Microphone = fallback.Name;
                }
                catch (Exception e)
                {
                    Log.Debug(e, "[FORK] Mic watch tick failed");
                }
            }
        });
    }

    private void OnConfigUpdated()
    {
        // Debounce: a trackbar drag fires many updates; restart once it settles.
        _restartDebounce?.Dispose();
        _restartDebounce = new System.Threading.Timer(_ =>
        {
            PushFeedToOverlay(); // overlay-only settings apply without restart
            if (_running) RestartIfRunning();
        }, null, 600, Timeout.Infinite);
    }

    private FeedPipeConfigDto CurrentOverlayConfig() => new()
    {
        MaxEntries = AppConfig.ConfigObject.OverlayMaxEntries,
        FontSize = AppConfig.ConfigObject.OverlayFontSize,
        Opacity = AppConfig.ConfigObject.OverlayOpacity,
        Anchor = AppConfig.ConfigObject.OverlayAnchor,
        WidthCm = AppConfig.ConfigObject.OverlayWidthCm,
        ShowTimestamps = AppConfig.ConfigObject.OverlayShowTimestamps,
        DesktopOverlay = AppConfig.ConfigObject.DesktopOverlayEnabled,
        DisplayDurationSec = AppConfig.ConfigObject.OverlayDisplayDurationSec,
        FadeoutDurationSec = AppConfig.ConfigObject.OverlayFadeoutDurationSec,
        PosX = AppConfig.ConfigObject.OverlayPosX,
        PosY = AppConfig.ConfigObject.OverlayPosY,
        PosZ = AppConfig.ConfigObject.OverlayPosZ,
        ShowOriginal = AppConfig.ConfigObject.OverlayShowOriginal,
    };

    private void PushFeedToOverlay()
    {
        try
        {
            var cfg = AppConfig.ConfigObject;
            var entries = Feed.GetSnapshot()
                .TakeLast(Math.Max(1, cfg.OverlayMaxEntries))
                .ToArray();
            _feedWriter?.Write(entries, CurrentOverlayConfig());
        }
        catch (Exception e)
        {
            Log.Debug(e, "[FORK] Feed pipe write failed");
        }
    }

    public void Start()
    {
        if (_running) { RestartIfRunning(); return; }
        if (_mic == null || _loopback == null) return;

        var cfg = AppConfig.ConfigObject;
        Log.Information("[FORK] Start: recognizer={R} translator={T} desktop={D} chatbox={C}",
            cfg.Recognizer, cfg.Translator, cfg.DesktopTranslation, cfg.SendToChatbox);

        IRecognizer rMic = cfg.Recognizer switch
        {
            1 => new GroqRecognizer(_mic),
            2 => new Gemini(_mic),
            _ => new Bing(_mic),
        };

        _translator = CreateTranslator();
        if (_translator == null) return;

        var errorHandler = new WinErrorHandler(msg => StatusChanged?.Invoke(msg));

        _microphoneKikitan = new Kikitan(rMic, _translator, errorHandler, false);
        _microphoneKikitan.AddOutput(new Custom((r, t, f) => { if (f) Feed.Add(false, r, t); }, false));

        if (cfg.SendToChatbox)
        {
            var chatbox = new Chatbox();
            _microphoneKikitan.AddOutput(new Custom((r, t, f) =>
            {
                if (cfg.DisableWhenMuted && _isMuted)
                {
                    if (f) Log.Debug("[FORK] Chatbox output skipped (muted)");
                    return;
                }
                chatbox.Send(r, t, f);
            }, true));
        }

        if (cfg.SendUserData) _microphoneKikitan.AddOutput(new OSC("/microphone"));

        _microphoneKikitan.Start();

        if (cfg.DesktopTranslation)
        {
            IRecognizer rDesktop = cfg.Recognizer switch
            {
                1 => new GroqRecognizer(_loopback),
                2 => new Gemini(_loopback),
                _ => new Bing(_loopback),
            };

            _desktopKikitan = new Kikitan(rDesktop, _translator, errorHandler, true);
            _desktopKikitan.AddOutput(new Custom((r, t, f) => { if (f) Feed.Add(true, r, t); }, false));
            if (cfg.SendUserData) _desktopKikitan.AddOutput(new OSC("/desktop"));
            _desktopKikitan.Start();
        }

        _running = true;
        StatusChanged?.Invoke("Running");
    }

    public void Stop()
    {
        _microphoneKikitan?.Dispose();
        _microphoneKikitan = null;
        _desktopKikitan?.Dispose();
        _desktopKikitan = null;
        _running = false;
        StatusChanged?.Invoke("Stopped");
    }

    public void RestartIfRunning()
    {
        if (!_running) return;
        Stop();
        Start();
    }

    private static ITranslator? CreateTranslator()
    {
        var cfg = AppConfig.ConfigObject;
        if (cfg.Translator == 1)
        {
            if (string.IsNullOrEmpty(cfg.GroqApiKey))
            {
                Log.Error("[FORK] Groq API key missing");
                return null;
            }
            return new GroqTranslator();
        }
        if (cfg.Translator == 2) return new GeminiStub();
        return new GoogleTranslate();
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _restartDebounce?.Dispose();
        _feedWriter?.Dispose();
        _oscWatcher?.Dispose();
    }

    private sealed class WinErrorHandler(Action<string> onError) : IErrorHandler
    {
        public void OnError(string message)
        {
            Log.Error("[FORK] {Msg}", message);
            onError(message);
        }
    }
}
