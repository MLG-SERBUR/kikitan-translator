// FORK (feed-overlay): native WinForms UI. Replaces the Photino/WebView2 host.
// All settings bind to the shared AppConfig (same config.json as upstream).

using KikitanTranslator.Utility;

namespace KikitanTranslator.WinForms;

public sealed class MainForm : Form
{
    private readonly AppEngine _engine;
    private readonly ConfigObject _cfg;

    private Button _startStop = null!;
    private Label _status = null!;
    private Label _muted = null!;
    private TextBox _feedBox = null!;
    private int _feedLineCount;
    private Button _updateButton = null!;
    private Updater _updater = null!;
    private ComboBox _micCombo = null!;
    private bool _loading = true;

    // FORK (vr-touch): global UI scale factor from AppConfig.UiScale (percent).
    // Applied to base font plus all hardcoded layout sizes so controls grow
    // proportionally and stay touch-usable in VR. 1.0 = previous size.
    // RebuildForUiScale applies it live, so no app restart is needed.
    private float _uiFactor = 1f;
    private Font? _ownedFont;
    private bool _wired;

    private record LangOption(string Code, string Name);

    public MainForm(AppEngine engine)
    {
        _engine = engine;
        _cfg = AppConfig.ConfigObject;

        Text = "KikiVRCTan"; // FORK (feed-overlay): product name
        RebuildForUiScale();
    }

    private void RebuildForUiScale()
    {
        _loading = true;
        try
        {
            _uiFactor = Math.Clamp(_cfg.UiScale, 80, 250) / 100f;
            var familyName = Font.FontFamily.Name;
            _ownedFont?.Dispose();
            _ownedFont = new Font(familyName, 9f * _uiFactor);
            Font = _ownedFont;
            Width = S(980);
            Height = S(720);
            MinimumSize = new Size(S(820), S(560));

            foreach (var c in Controls.OfType<Control>().ToArray())
                c.Dispose();

            BuildLayout();
            LoadFromConfig();
            ApplyLogFont();
            RepopulateFeed();
            if (!_wired)
            {
                WireEngine();
                _wired = true;
            }
        }
        finally { _loading = false; }
    }

    private void RepopulateFeed()
    {
        _feedLineCount = 0;
        foreach (var e in _engine.Feed.GetSnapshot().TakeLast(240))
        {
            _feedBox.AppendText(e.DisplayText(true, _cfg.FeedShowTranscription) + Environment.NewLine);
            _feedLineCount++;
        }
    }

    private void BuildLayout()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill };
        Controls.Add(split);

        var settings = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(S(8)),
        };
        split.Panel1.Controls.Add(settings);

        _startStop = new Button { Text = "Start", Width = S(90), Height = S(28) };
        _startStop.Click += (_, _) =>
        {
            if (_engine.IsRunning) _engine.Stop(); else _engine.Start();
            _startStop.Text = _engine.IsRunning ? "Stop" : "Start";
        };
        _status = new Label { Text = "Stopped", AutoSize = true, Padding = new Padding(0, S(6), 0, 0) };
        _muted = new Label { Text = "", AutoSize = true, ForeColor = Color.DarkRed };
        var clearFeed = new Button { Text = "Clear feed", Width = S(90), Height = S(28) };
        clearFeed.Click += (_, _) => { _engine.Feed.Clear(); _feedBox.Clear(); _feedLineCount = 0; };
        _updateButton = new Button { Text = "Restart to update", Width = S(130), Height = S(28), Visible = false };
        _updateButton.Click += (_, _) => _updater.ApplyAndRestart();

        settings.Controls.Add(Group("Control", c =>
        {
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
            row.Controls.Add(_startStop);
            row.Controls.Add(clearFeed);
            c.Controls.Add(row);
            c.Controls.Add(_status);
            c.Controls.Add(_muted);
            c.Controls.Add(_updateButton);
            c.Controls.Add(Row("Log size:", Num(8, 24, v => { _cfg.LogFontSize = v; ApplyLogFont(); }, () => _cfg.LogFontSize)));
            c.Controls.Add(Row("UI size %:", UiScaleRow()));
        }));

        settings.Controls.Add(Group("Speech", c =>
        {
            c.Controls.Add(Row("From:", SourceCombo()));
            c.Controls.Add(Row("To:", TargetCombo()));
            c.Controls.Add(Row("Recognizer:", RecognizerCombo()));
            c.Controls.Add(Row("Translator:", TranslatorCombo()));
            c.Controls.Add(MicLabeledRow());
            c.Controls.Add(Check("Show transcription", v => _cfg.FeedShowTranscription = v, () => _cfg.FeedShowTranscription));
            c.Controls.Add(Check("Translate mic audio", v => _cfg.DesktopTranslation = v, () => _cfg.DesktopTranslation));
            c.Controls.Add(Check("Transcription only (no translation)", v => _cfg.SpeechToTextOnly = v, () => _cfg.SpeechToTextOnly));
        }));

        settings.Controls.Add(Group("VRChat", c =>
        {
            c.Controls.Add(Check("Send to chatbox", v => _cfg.SendToChatbox = v, () => _cfg.SendToChatbox));
            c.Controls.Add(Check("Translation only (no original)", v => _cfg.TranslationOnly = v, () => _cfg.TranslationOnly));
            c.Controls.Add(Check("Send without waiting for finish", v => _cfg.SendWithoutWaitingForFinish = v, () => _cfg.SendWithoutWaitingForFinish));
            c.Controls.Add(Check("Disable when muted", v => _cfg.DisableWhenMuted = v, () => _cfg.DisableWhenMuted));
            c.Controls.Add(Check("Send user data (OSC)", v => _cfg.SendUserData = v, () => _cfg.SendUserData));
        }));

        settings.Controls.Add(Group("API keys", c =>
        {
            c.Controls.Add(Row("Groq:", KeyBox(v => _cfg.GroqApiKey = v, () => _cfg.GroqApiKey)));
            c.Controls.Add(Row("Gemini:", KeyBox(v => _cfg.GeminiApiKey = v, () => _cfg.GeminiApiKey)));
        }));

        settings.Controls.Add(Group("Overlay", c =>
        {
            c.Controls.Add(Check("Desktop overlay", v => _cfg.DesktopOverlayEnabled = v, () => _cfg.DesktopOverlayEnabled));
            c.Controls.Add(Check("Show timestamps", v => _cfg.OverlayShowTimestamps = v, () => _cfg.OverlayShowTimestamps));
            c.Controls.Add(Check("Show original text", v => _cfg.OverlayShowOriginal = v, () => _cfg.OverlayShowOriginal));
            c.Controls.Add(Check("Show own mic in feed", v => _cfg.FeedShowOwn = v, () => _cfg.FeedShowOwn));
            c.Controls.Add(Row("Anchor:", AnchorCombo()));
            c.Controls.Add(Row("Max entries:", Num(1, 12, v => _cfg.OverlayMaxEntries = v, () => _cfg.OverlayMaxEntries)));
            c.Controls.Add(Row("Font size:", Num(24, 160, v => _cfg.OverlayFontSize = v, () => _cfg.OverlayFontSize)));
            c.Controls.Add(Row("Width (cm):", Num(40, 200, v => _cfg.OverlayWidthCm = v, () => _cfg.OverlayWidthCm)));
            c.Controls.Add(Row("Opacity %:", OpacityRow()));
            c.Controls.Add(Row("Display sec (0=always):", Num(0, 3600, v => _cfg.OverlayDisplayDurationSec = v, () => _cfg.OverlayDisplayDurationSec)));
            c.Controls.Add(Row("Fadeout sec:", Num(0, 30, v => _cfg.OverlayFadeoutDurationSec = v, () => _cfg.OverlayFadeoutDurationSec)));
            c.Controls.Add(Row("Pos X (m):", DblNum(-2, 2, v => _cfg.OverlayPosX = v, () => _cfg.OverlayPosX)));
            c.Controls.Add(Row("Pos Y (m):", DblNum(-2, 2, v => _cfg.OverlayPosY = v, () => _cfg.OverlayPosY)));
            c.Controls.Add(Row("Pos Z (m):", DblNum(-2, 2, v => _cfg.OverlayPosZ = v, () => _cfg.OverlayPosZ)));
        }));

        _feedBox = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, WordWrap = false,
            Font = new Font("Consolas", 10),
            BackColor = Color.FromArgb(20, 20, 20), ForeColor = Color.Gainsboro,
        };
        split.Panel2.Controls.Add(_feedBox);

        // Set after layout so the distance sticks (initializer-time sets can be
        // clamped before the control is sized).
        split.SplitterDistance = S(320);

        _updater = new Updater();
        _updater.StatusChanged += msg => BeginInvoke(() => _status.Text = msg);
        _updater.UpdateReady += () => BeginInvoke(() => _updateButton.Visible = true);
        if (!_wired) Task.Run(() => _updater.CheckAsync());
    }

    private int S(int px) => (int)Math.Round(px * _uiFactor);

    private NumericUpDown UiScaleRow()
    {
        var n = Num(80, 250,
            v =>
            {
                if (_loading) return;
                if (_cfg.UiScale == v) return;
                _cfg.UiScale = v;
                // Rebuild once this event unwinds: the raising control is
                // disposed by the rebuild.
                BeginInvoke((Action)(() =>
                {
                    RebuildForUiScale();
                    _status.Text = $"UI size {_cfg.UiScale}% applied";
                }));
            },
            () => _cfg.UiScale);
        n.Increment = 10;
        return n;
    }

    private GroupBox Group(string title, Action<FlowLayoutPanel> build)
    {
        var g = new GroupBox { Text = title, Width = S(300), Padding = new Padding(S(6)), AutoSize = true };
        var p = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoSize = true,
        };
        build(p);
        g.Controls.Add(p);
        return g;
    }

    private Panel Row(string label, Control control)
    {
        var p = new Panel { Width = S(280), Height = S(30) };
        var l = new Label { Text = label, Width = S(100), Top = S(6), Left = 0, AutoSize = false };
        control.Left = S(104); control.Top = S(3); control.Width = S(170);
        p.Controls.Add(l); p.Controls.Add(control);
        return p;
    }

    private ComboBox SourceCombo()
    {
        var cb = DropDown(Languages.Source.Select(l => new LangOption(l.Code, l.Name)).ToArray());
        cb.SelectedIndexChanged += (_, _) => { if (!_loading && cb.SelectedItem is LangOption o) _cfg.SourceLanguage = o.Code; };
        cb.Tag = "source";
        return cb;
    }

    private ComboBox TargetCombo()
    {
        var cb = DropDown(Languages.Target.Select(l => new LangOption(l.Code, l.Name)).ToArray());
        cb.SelectedIndexChanged += (_, _) => { if (!_loading && cb.SelectedItem is LangOption o) _cfg.TargetLanguage = o.Code; };
        cb.Tag = "target";
        return cb;
    }

    private static ComboBox DropDown(LangOption[] items)
    {
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        cb.Items.AddRange(items);
        cb.DisplayMember = "Name";
        return cb;
    }

    private ComboBox RecognizerCombo()
    {
        var cb = DropDown([new("0", "Bing (default)"), new("1", "Groq"), new("2", "Gemini")]);
        cb.SelectedIndexChanged += (_, _) => { if (!_loading && cb.SelectedItem is LangOption o) _cfg.Recognizer = int.Parse(o.Code); };
        cb.Tag = "recognizer";
        return cb;
    }

    private ComboBox TranslatorCombo()
    {
        var cb = DropDown([new("0", "Google"), new("1", "Groq"), new("2", "Gemini")]);
        cb.SelectedIndexChanged += (_, _) => { if (!_loading && cb.SelectedItem is LangOption o) _cfg.Translator = int.Parse(o.Code); };
        cb.Tag = "translator";
        return cb;
    }

    private ComboBox AnchorCombo()
    {
        var cb = DropDown([new("0", "HMD"), new("1", "Left hand"), new("2", "Right hand")]);
        cb.SelectedIndexChanged += (_, _) => { if (!_loading && cb.SelectedItem is LangOption o) _cfg.OverlayAnchor = int.Parse(o.Code); };
        cb.Tag = "anchor";
        return cb;
    }

    private Panel MicLabeledRow()
    {
        var p = new Panel { Width = S(280), Height = S(30) };
        var l = new Label { Text = "Mic:", Width = S(100), Top = S(6), Left = 0, AutoSize = false };
        _micCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = S(104), Top = S(3), Width = S(110) };
        _micCombo.SelectedIndexChanged += (_, _) =>
        {
            if (!_loading && _micCombo.SelectedItem is string name) _cfg.Microphone = name;
        };
        var refresh = new Button { Text = "Refresh", Left = S(220), Top = S(2), Width = S(58), Height = S(24) };
        refresh.Click += (_, _) => RefreshMics();
        p.Controls.Add(l); p.Controls.Add(_micCombo); p.Controls.Add(refresh);
        return p;
    }

    private static CheckBox Check(string text, Action<bool> set, Func<bool> get)
    {
        var cb = new CheckBox { Text = text, AutoSize = true, Tag = get };
        cb.CheckedChanged += (_, _) => set(cb.Checked);
        return cb;
    }

    private static NumericUpDown Num(int min, int max, Action<int> set, Func<int> get)
    {
        var n = new NumericUpDown { Minimum = min, Maximum = max, Tag = get };
        n.ValueChanged += (_, _) => set((int)n.Value);
        return n;
    }

    private NumericUpDown DblNum(double min, double max, Action<double> set, Func<double> get)
    {
        var n = new NumericUpDown
        {
            Minimum = (decimal)min, Maximum = (decimal)max,
            DecimalPlaces = 2, Increment = 0.05M, Tag = get,
        };
        n.ValueChanged += (_, _) => { if (!_loading) set((double)n.Value); };
        return n;
    }

    private TextBox KeyBox(Action<string> set, Func<string> get)
    {
        var t = new TextBox { PasswordChar = '•', Tag = get, Width = S(170) };
        t.TextChanged += (_, _) => set(t.Text);
        return t;
    }

    private Panel OpacityRow()
    {
        var p = new Panel { Width = S(280), Height = S(30) };
        var tr = new TrackBar { Minimum = 10, Maximum = 100, TickFrequency = 10, Left = 0, Top = 0, Width = S(120) };
        var lab = new Label { Left = S(126), Top = S(6), Width = S(44), Tag = (Func<int>)(() => _cfg.OverlayOpacity) };
        tr.ValueChanged += (_, _) => { if (!_loading) _cfg.OverlayOpacity = tr.Value; lab.Text = tr.Value + "%"; };
        tr.Tag = "opacity";
        p.Controls.Add(tr); p.Controls.Add(lab);
        return p;
    }

    private void LoadFromConfig()
    {
        _loading = true;
        try
        {
            foreach (var gb in Controls.OfType<SplitContainer>().SelectMany(s => s.Panel1.Controls.OfType<FlowLayoutPanel>())
                         .SelectMany(f => f.Controls.OfType<GroupBox>()))
            {
                foreach (var row in gb.Controls.OfType<FlowLayoutPanel>().SelectMany(f => f.Controls.OfType<Control>()))
                    ApplyConfig(row);
            }
            RefreshMics();
        }
        finally { _loading = false; }
    }

    private void ApplyConfig(Control row)
    {
        ApplyOne(row);
        foreach (var c in row.Controls.OfType<Control>())
        {
            if (c is Panel p) ApplyConfig(p);
            else ApplyOne(c);
        }
    }

    // FORK: ApplyOne handles the control itself; ApplyConfig recurses into
    // children. Previously only children were processed, so CheckBoxes added
    // directly to a group never loaded their saved value (visible desync).
    private void ApplyOne(Control c)
    {
        switch (c)
        {
            case ComboBox cb when cb.Tag is "source":
                SelectByCode(cb, _cfg.SourceLanguage); break;
            case ComboBox cb when cb.Tag is "target":
                SelectByCode(cb, _cfg.TargetLanguage); break;
            case ComboBox cb when cb.Tag is "recognizer":
                cb.SelectedIndex = _cfg.Recognizer; break;
            case ComboBox cb when cb.Tag is "translator":
                cb.SelectedIndex = _cfg.Translator; break;
            case ComboBox cb when cb.Tag is "anchor":
                cb.SelectedIndex = _cfg.OverlayAnchor; break;
            case CheckBox chk when chk.Tag is Func<bool> get:
                SetCheckedSilently(chk, get()); break;
            case NumericUpDown n when n.Tag is Func<int> get:
                n.Value = Math.Clamp(get(), n.Minimum, n.Maximum); break;
            case NumericUpDown n when n.Tag is Func<double> getD:
                n.Value = (decimal)Math.Clamp(getD(), (double)n.Minimum, (double)n.Maximum); break;
            case TextBox t when t.Tag is Func<string> get:
                t.Text = get(); break;
            case TrackBar tr when tr.Tag is "opacity":
                tr.Value = Math.Clamp(_cfg.OverlayOpacity, tr.Minimum, tr.Maximum);
                foreach (var lab in tr.Parent!.Controls.OfType<Label>())
                    lab.Text = tr.Value + "%";
                break;
        }
    }

    private void SetCheckedSilently(CheckBox chk, bool value)
    {
        _loading = true;
        chk.Checked = value;
        _loading = false;
    }

    private static void SelectByCode(ComboBox cb, string code)
    {
        for (var i = 0; i < cb.Items.Count; i++)
            if (cb.Items[i] is LangOption o && o.Code == code) { cb.SelectedIndex = i; return; }
        cb.SelectedIndex = 0;
    }

    private void RefreshMics()
    {
        _loading = true;
        try
        {
            _micCombo.Items.Clear();
            foreach (var m in _engine.GetMicrophones()) _micCombo.Items.Add(m.Name);
            var idx = _micCombo.Items.IndexOf(_cfg.Microphone);
            _micCombo.SelectedIndex = idx >= 0 ? idx : -1;
            if (_micCombo.SelectedIndex < 0 && _micCombo.Items.Count > 0)
            {
                var def = _engine.GetMicrophones().FirstOrDefault(d => d.IsDefault);
                _micCombo.SelectedIndex = def.Name != null ? _micCombo.Items.IndexOf(def.Name) : 0;
            }
        }
        finally { _loading = false; }
    }

    private void ApplyLogFont()
    {
        if (_feedBox != null)
            _feedBox.Font = new Font("Consolas", Math.Clamp(_cfg.LogFontSize, 8, 24));
    }

    private void WireEngine()
    {
        _engine.StatusChanged += msg => BeginInvoke(() => _status.Text = msg);
        _engine.MuteChanged += muted => BeginInvoke(() => _muted.Text = muted ? "MUTED" : "");
        _engine.Feed.EntryAdded += entry => BeginInvoke(() =>
        {
            _feedBox.AppendText(entry.DisplayText(true, _cfg.FeedShowTranscription) + Environment.NewLine);
            if (++_feedLineCount > 240)
            {
                // Drop the oldest 40 lines; keep the view pinned to the end.
                var text = _feedBox.Text;
                int cut = 0;
                for (int i = 0; i < 40 && cut < text.Length; i++)
                {
                    int nl = text.IndexOf('\n', cut);
                    if (nl < 0) { cut = text.Length; break; }
                    cut = nl + 1;
                }
                _feedBox.Select(0, cut);
                _feedBox.SelectedText = "";
                _feedBox.SelectionStart = _feedBox.TextLength;
                _feedBox.ScrollToCaret();
                _feedLineCount -= 40;
            }
        });
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _engine.Dispose();
        base.OnFormClosed(e);
    }
}
