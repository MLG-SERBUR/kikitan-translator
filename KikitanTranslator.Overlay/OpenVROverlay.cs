using Valve.VR;
using Application = OVRSharp.Application;

namespace KikitanTranslator.Overlay;

public class ImageData
{
    public MemoryStream image;
    public int time;
}

public class OpenVROverlay : Application
{
    private OVRSharp.Overlay overlay;
    private List<ImageData> images;

    private float overlayDistance = -1.5f;
    private float verticalOffset = -0.55f;

    // FORK (feed-overlay): 0=HMD, 1=left hand, 2=right hand. Feed display state.
    private int _anchor;
    private readonly object _feedLock = new();
    private MemoryStream? _pendingFeed;
    private FeedPipeConfig? _pendingFeedConfig;
    private DateTime _legacyHideAt = DateTime.MinValue;
    private bool _feedActive;
    // FORK (feed-overlay): VRCT-like display duration + fadeout.
    private FeedPipeConfig? _feedConfig;
    private DateTime _feedHideAt = DateTime.MaxValue;
    private float _baseAlpha = 0.85f;

    public OpenVROverlay() : base(ApplicationType.Overlay)
    {
        Console.WriteLine("Starting overlay...");

        overlay = new OVRSharp.Overlay("kikitan-overlay", "Kikitan Overlay");

        overlay.WidthInMeters = 0.8f;
        overlay.Alpha = 0.8f;
        overlay.SetFlag(VROverlayFlags.IsPremultiplied, false);
        overlay.SetFlag(VROverlayFlags.SendVRSmoothScrollEvents, false);

        images = new List<ImageData>();
    }

    /// <summary>
    /// FORK (feed-overlay): queue a freshly rendered feed frame. Displayed
    /// persistently (no auto-hide); replaces any previous feed frame.
    /// </summary>
    public void ShowFeed(MemoryStream png, FeedPipeConfig config)
    {
        lock (_feedLock)
        {
            _pendingFeed?.Dispose();
            _pendingFeed = png;
            _pendingFeedConfig = config;
        }
    }

    private void UpdateOverlayTransforms()
    {
        var system = OpenVR.System;
        if (system == null)
        {
            Console.WriteLine("OpenVR system not initialized.");
            return;
        }

        // FORK (feed-overlay): anchor presets. Hand anchors fall back to HMD
        // when the controller isn't tracked.
        uint device = OpenVR.k_unTrackedDeviceIndex_Hmd;
        float x = 0f, y = verticalOffset, z = overlayDistance;

        if (_anchor == 1 || _anchor == 2)
        {
            var role = _anchor == 1 ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand;
            uint hand = system.GetTrackedDeviceIndexForControllerRole(role);
            if (hand != OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                device = hand;
                x = 0f;
                y = 0.14f;
                z = -0.24f;
            }
        }

        if (device == OpenVR.k_unTrackedDeviceIndex_Hmd)
            x = system.GetEyeToHeadTransform(EVREye.Eye_Left).m3;

        // FORK (feed-overlay): VRCT-like position offset, relative to the anchor.
        if (_feedConfig != null)
        {
            x += (float)_feedConfig.PosX;
            y += (float)_feedConfig.PosY;
            z += (float)_feedConfig.PosZ;
        }

        HmdMatrix34_t overlayTransform = new HmdMatrix34_t
        {
            m0 = 1, m1 = 0, m2 = 0, m3 = x,
            m4 = 0, m5 = 1, m6 = 0, m7 = y,
            m8 = 0, m9 = 0, m10 = 1, m11 = z
        };

        var err = OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(
            overlay.Handle, device, ref overlayTransform);

        if (err != EVROverlayError.None)
        {
            Console.WriteLine($"Overlay transform error: {err}");
        }
    }

    public void AddToQueue(MemoryStream data, int time)
    {
        images.Add(new ImageData { image = data, time = time });
    }

    public void ImageLoop()
    {
        // FORK (feed-overlay): restructured loop. Transforms update every tick
        // (required for hand tracking); feed frames display persistently; the
        // legacy single-message path keeps its timed show/hide via deadlines.
        string fileName = Path.Combine(Path.GetTempPath(), "kikitan_overlay.png");

        while (true)
        {
            UpdateOverlayTransforms();

            MemoryStream? feed = null;
            FeedPipeConfig? feedConfig = null;
            lock (_feedLock)
            {
                feed = _pendingFeed;
                _pendingFeed = null;
                feedConfig = _pendingFeedConfig;
                _pendingFeedConfig = null;
            }

            if (feed != null)
            {
                try
                {
                    using (feed)
                    using (Image image = Image.FromStream(feed))
                        image.Save(fileName);

                    if (feedConfig != null)
                    {
                        _anchor = Math.Clamp(feedConfig.Anchor, 0, 2);
                        overlay.WidthInMeters = Math.Clamp(feedConfig.WidthCm / 100f, 0.2f, 3f);
                        _baseAlpha = Math.Clamp(feedConfig.Opacity / 100f, 0.1f, 1f);
                        _feedConfig = feedConfig;
                        // FORK (feed-overlay): VRCT-like display duration (0 = always).
                        _feedHideAt = feedConfig.DisplayDurationSec > 0
                            ? DateTime.Now.AddSeconds(feedConfig.DisplayDurationSec)
                            : DateTime.MaxValue;
                    }

                    overlay.Alpha = _baseAlpha;
                    overlay.Show();
                    overlay.SetTextureFromFile(fileName);
                    _feedActive = true;
                    _legacyHideAt = DateTime.MinValue;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Feed display failed: {e.Message}");
                }
            }
            else if (!_feedActive && images.Count > 0)
            {
                try
                {
                    using (Image image = Image.FromStream(images[0].image))
                        image.Save(fileName);

                    overlay.WidthInMeters = 0.8f;
                    overlay.Alpha = 0.8f;
                    overlay.Show();
                    overlay.SetTextureFromFile(fileName);
                    _legacyHideAt = DateTime.Now.AddMilliseconds(images[0].time);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Legacy overlay display failed: {e.Message}");
                }

                images.RemoveAt(0);
            }

            if (!_feedActive && _legacyHideAt != DateTime.MinValue
                && DateTime.Now >= _legacyHideAt && images.Count == 0)
            {
                Console.WriteLine("Hiding overlay...");
                overlay.Hide();
                _legacyHideAt = DateTime.MinValue;
            }

            // FORK (feed-overlay): VRCT-like fadeout once the display duration elapses.
            if (_feedActive)
            {
                var now = DateTime.Now;
                if (now >= _feedHideAt)
                {
                    overlay.Hide();
                    _feedActive = false;
                }
                else if (_feedConfig is { FadeoutDurationSec: > 0 } && _feedHideAt != DateTime.MaxValue)
                {
                    var fadeStart = _feedHideAt.AddSeconds(-_feedConfig.FadeoutDurationSec);
                    if (now >= fadeStart)
                    {
                        double t = (now - fadeStart).TotalSeconds / _feedConfig.FadeoutDurationSec;
                        overlay.Alpha = _baseAlpha * (float)Math.Max(0, 1 - t);
                    }
                }
            }

            Thread.Sleep(16);
        }
    }
}