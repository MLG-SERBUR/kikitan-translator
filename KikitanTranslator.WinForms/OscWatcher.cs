using BuildSoft.OscCore;
using Serilog;
using VRC.OSCQuery;

// FORK (feed-overlay): copied from KikitanTranslator.Photino/OscWatcher.cs (upstream)
// to avoid referencing the Photino host from the WinForms app.
namespace KikitanTranslator.WinForms; 

public class OscWatcher : IDisposable
{
    private OSCQueryService? _oscQuery;
    private OscServer? _oscServer;
    private Thread? _listenThread;
    private bool _running;
    
    public bool? IsMuted { get; private set; }
    
    public event Action<bool>? MuteStatusChanged;
    
    public void Start()
    {
        var tcpPort = Extensions.GetAvailableTcpPort();
        var udpPort = Extensions.GetAvailableUdpPort();
        
        _oscQuery = new OSCQueryServiceBuilder()
            .WithTcpPort(tcpPort)
            .WithUdpPort(udpPort)
            .WithServiceName("Kikitan Translator")
            .WithDefaults()
            .Build();
        
        _oscQuery.AddEndpoint<bool>("/avatar/parameters/MuteSelf", Attributes.AccessValues.ReadOnly, ["Muted status"]);

        _oscServer = new OscServer(udpPort);
        _oscServer.TryAddMethod("/avatar/parameters/MuteSelf", m =>
        {
            if (m.ElementCount > 0)
            {
                var muted = m.ReadBooleanElement(0);
                if (IsMuted != muted)
                    MuteStatusChanged?.Invoke(muted);
                    
                IsMuted = muted;
            }
        });
        
        _oscServer.Start();

        _running = true;
        _listenThread = new Thread(ListenLoop) { IsBackground = true };
        _listenThread.Start();

        // FORK: startup pull, like VRCT's Mic Mute Sync. The UDP listener
        // below only fires on changes, so without this the mute state stays
        // unknown until the first toggle. This does one OSCQuery pull of
        // the current /avatar/parameters/MuteSelf value (single node, not
        // a dump, not polling) and then stops; if VRChat is not up yet or
        // the node is not there, the state stays unknown until a toggle.
        // Runs in the background so app startup never waits on discovery.
        _ = Task.Run(PullInitialMuteAsync);
    }

    private async Task PullInitialMuteAsync()
    {
        try
        {
            var service = await FindVrChatServiceAsync().ConfigureAwait(false);
            if (service == null || !_running || IsMuted != null) return;

            var tree = await Extensions.GetOSCTree(service.address, service.port).ConfigureAwait(false);
            var node = tree?.GetNodeWithPath("/avatar/parameters/MuteSelf");
            if (node?.Value == null || node.Value.Length == 0 || node.Value[0] is not bool muted) return;
            if (!_running || IsMuted == muted) return;

            MuteStatusChanged?.Invoke(muted);
            IsMuted = muted;
            Log.Debug($"[FORK] Startup mute pull: muted={muted}");
        }
        catch (Exception e)
        {
            Log.Debug(e, "[FORK] Startup mute pull failed; mute stays unknown until first toggle");
        }
    }

    private async Task<OSCQueryServiceProfile?> FindVrChatServiceAsync()
    {
        // VRChat advertises its OSCQuery service as "VRChat-Client-<id>".
        static bool IsVrChat(OSCQueryServiceProfile p)
            => p.name?.StartsWith("VRChat-Client", StringComparison.Ordinal) == true;

        var existing = _oscQuery?.GetOSCQueryServices()?.FirstOrDefault(IsVrChat);
        if (existing != null) return existing;

        var found = new TaskCompletionSource<OSCQueryServiceProfile?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(OSCQueryServiceProfile p)
        {
            if (IsVrChat(p)) found.TrySetResult(p);
        }

        try
        {
            if (_oscQuery == null) return null;
            _oscQuery.OnOscQueryServiceAdded += Handler;
            _oscQuery.RefreshServices();
            existing = _oscQuery.GetOSCQueryServices()?.FirstOrDefault(IsVrChat);
            if (existing != null) return existing;

            await Task.WhenAny(found.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            return found.Task.IsCompleted ? found.Task.Result : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            try { if (_oscQuery != null) _oscQuery.OnOscQueryServiceAdded -= Handler; } catch { }
        }
    }

    private void ListenLoop()
    {
        while (_running)
        {
            _oscServer?.Update();
            Thread.Sleep(100);
        }
    }

    public void Dispose()
    {
        _running = false;
        _oscServer?.Dispose();
        _oscQuery?.Dispose();
    }
}