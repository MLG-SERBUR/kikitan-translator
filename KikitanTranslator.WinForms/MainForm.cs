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
    private ListView _feedView = null!;
    private ComboBox _micCombo = null!;
    private bool _loading = true;

    private record LangOption(string Code, string Name);

    public MainForm(AppEngine engine)
    {
        _engine = engine;
        _cfg = AppConfig.ConfigObject;

        Text = "KikiVRCTan"; // FORK (feed-overlay): product name
        Width = 980;
        Height = 720;
        MinimumSize = new Size(820, 560);

        BuildLayout();
        LoadFromConfig();
        WireEngine();

        _loading = false;
    }

    private void BuildLayout()
    {
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 44, Padding = new Padding(8),
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
        };
        _startStop = new Button { Text = "Start", Width = 90, Height = 28 };
        _startStop.Click += (_, _) =>
        {
            if (_engine.IsRunning) _engine.Stop(); else _engine.Start();
            _startStop.Text = _engine.IsRunning ? "Stop" : "Start";
        };
        _status = new Label { Text = "Stopped", AutoSize = true, Padding = new Padding(8, 6, 0, 0) };
        _muted = new Label { Text = "", AutoSize = true, Padding = new Padding(8, 6, 0, 0), ForeColor = Color.DarkRed };
        var clearFeed = new Button { Text = "Clear feed", Width = 90, Height = 28 };
        clearFeed.Click += (_, _) => { _engine.Feed.Clear(); _feedView.Items.Clear(); };
        top.Controls.AddRange([_startStop, _status, _muted, clearFeed]);
        Controls.Add(top);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 330 };
        Controls.Add(split);

        var settings = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(8),
        };
        split.Panel1.Controls.Add(settings);

        settings.Controls.Add(Group("Speech", c =>
        {
            c.Controls.Add(Row("From:", SourceCombo()));
            c.Controls.Add(Row("To:", TargetCombo()));
            c.Controls.Add(Row("Recognizer:", RecognizerCombo()));
            c.Controls.Add(Row("Translator:", TranslatorCombo()));
            c.Controls.Add(MicLabeledRow());
            c.Controls.Add(Check("Speech-to-text only", v => _cfg.SpeechToTextOnly = v, () => _cfg.SpeechToTextOnly));
            c.Controls.Add(Check("Translate desktop audio", v => _cfg.DesktopTranslation = v, () => _cfg.DesktopTranslation));
        }));

        settings.Controls.Add(Group("VRChat", c =>
        {
            c.Controls.Add(Check("Send to chatbox", v => _cfg.SendToChatbox = v, () => _cfg.SendToChatbox));
            c.Controls.Add(Check("Translation only (no original)", v => _cfg.TranslationOnly = v, () => _cfg.TranslationOnly));
            c.Controls.Add(Check("Send without waiting for finish", v => _cfg.SendWithoutWaitingForFinish = v, () => _cfg.SendWithoutWaitingForFinish));
            c.Controls.Add(Check("Disable when muted", v => _cfg.DisableWhenMuted = v, () => _cfg.DisableWhenMuted));
            c.Controls.Add(Check("Send user data (OSC)", v => _cfg.SendUserData = v, () => _cfg.SendUserData));
            c.Controls.Add(Row("Chatbox ms/char:", Num(1, 200, v => _cfg.ChatboxWaitPerCharMs = v, () => _cfg.ChatboxWaitPerCharMs)));
            c.Controls.Add(Row("OSC port:", Num(1024, 65535, v => _cfg.OscPort = v, () => _cfg.OscPort)));
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

        _feedView = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            GridLines = false, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _feedView.Columns.Add("Time", 70);
        _feedView.Columns.Add("From", 70);
        _feedView.Columns.Add("Original", 280);
        _feedView.Columns.Add("Translation", 280);
        split.Panel2.Controls.Add(_feedView);
    }

    private static GroupBox Group(string title, Action<FlowLayoutPanel> build)
    {
        var g = new GroupBox { Text = title, Width = 300, Padding = new Padding(6), AutoSize = true };
        var p = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoSize = true,
        };
        build(p);
        g.Controls.Add(p);
        return g;
    }

    private static Panel Row(string label, Control control)
    {
        var p = new Panel { Width = 280, Height = 30 };
        var l = new Label { Text = label, Width = 100, Top = 6, Left = 0, AutoSize = false };
        control.Left = 104; control.Top = 3; control.Width = 170;
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
        var p = new Panel { Width = 280, Height = 30 };
        var l = new Label { Text = "Mic:", Width = 100, Top = 6, Left = 0, AutoSize = false };
        _micCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 104, Top = 3, Width = 110 };
        _micCombo.SelectedIndexChanged += (_, _) =>
        {
            if (!_loading && _micCombo.SelectedItem is string name) _cfg.Microphone = name;
        };
        var refresh = new Button { Text = "Refresh", Left = 220, Top = 2, Width = 58, Height = 24 };
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

    private static TextBox KeyBox(Action<string> set, Func<string> get)
    {
        var t = new TextBox { PasswordChar = '•', Tag = get, Width = 170 };
        t.TextChanged += (_, _) => set(t.Text);
        return t;
    }

    private Panel OpacityRow()
    {
        var p = new Panel { Width = 280, Height = 30 };
        var tr = new TrackBar { Minimum = 10, Maximum = 100, TickFrequency = 10, Left = 0, Top = 0, Width = 120 };
        var lab = new Label { Left = 126, Top = 6, Width = 44, Tag = (Func<int>)(() => _cfg.OverlayOpacity) };
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
        foreach (var c in row.Controls.OfType<Control>())
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
                case Panel p:
                    ApplyConfig(p); break;
            }
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

    private void WireEngine()
    {
        _engine.StatusChanged += msg => BeginInvoke(() => _status.Text = msg);
        _engine.MuteChanged += muted => BeginInvoke(() => _muted.Text = muted ? "MUTED" : "");
        _engine.Feed.EntryAdded += entry => BeginInvoke(() =>
        {
            var item = new ListViewItem(entry.Timestamp.ToString("HH:mm:ss"));
            item.SubItems.Add(entry.FromDesktop ? "Desktop" : "Mic");
            item.SubItems.Add(entry.Original);
            item.SubItems.Add(entry.Translation);
            _feedView.Items.Add(item);
            while (_feedView.Items.Count > 220) _feedView.Items.RemoveAt(0);
            _feedView.EnsureVisible(_feedView.Items.Count - 1);
        });
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _engine.Dispose();
        base.OnFormClosed(e);
    }
}
