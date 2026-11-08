using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.RegularExpressions;
using ProjectDeck.Core.Settings;
using ProjectDeck.Rendering;
using ProjectDeck.Services;

namespace ProjectDeck;

/// <summary>
/// 設定ダイアログ（仕様書 5.5）。settings.toml の全項目を画面から変更する。
/// OK で保存（呼び出し側が settings.toml に書き、即時反映）、キャンセルで何もしない。
/// </summary>
internal sealed partial class SettingsForm : Form
{
    private readonly Action<string> _openText;
    private readonly string _settingsPath;
    private AppSettings _source;
    private bool _loading;

    /// <summary>OK を押したときの設定</summary>
    public AppSettings Result { get; private set; }

    // レイアウト
    private readonly NumericUpDown _rows = Num(SettingsLimits.Rows);
    private readonly NumericUpDown _cols = Num(SettingsLimits.Cols);
    private readonly NumericUpDown _buttonSize = Num(SettingsLimits.ButtonSize);
    private readonly NumericUpDown _gap = Num(SettingsLimits.Gap);
    private readonly NumericUpDown _radius = Num(SettingsLimits.CornerRadius);
    private readonly PreviewPanel _preview = new();

    // ウィンドウ
    private readonly ComboBox _position = Combo(["左上 (top_left)", "右上 (top_right)", "右下 (bottom_right)"]);
    private readonly ComboBox _monitor = Combo([]);
    private readonly NumericUpDown _margin = Num(SettingsLimits.Margin);
    private readonly CheckBox _topmost = Check("常に最前面に表示");
    private readonly NumericUpDown _opacity = new() { Minimum = 30, Maximum = 100, Width = 70 };
    private readonly CheckBox _hideAfterLaunch = Check("ボタン実行後にランチャーを隠す");

    // 起動
    private readonly CheckBox _autoStart = Check("PC起動時に自動実行する");
    private readonly CheckBox _showOnStart = Check("起動時に表示する（OFF ならトレイに隠して起動）");
    private readonly NumericUpDown _delay = Num(SettingsLimits.DelaySec);

    // 文字
    private readonly ComboBox _font = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly NumericUpDown _fontSize = new() { Minimum = 6, Maximum = 20, DecimalPlaces = 1, Increment = 0.5m, Width = 70 };
    private readonly ComboBox _labelColor = Combo(["自動 (auto)", "白 (white)", "黒 (black)", "指定色"]);
    private readonly TextBox _labelColorHex = new() { Width = 90 };

    // ダイヤル
    private readonly CheckBox _dialVisible = Check("ダイヤルを表示する");

    // ホットキー
    private readonly TextBox _hkToggle = HotkeyBox();

    // 詳細
    private readonly TextBox _deckPath = new() { Width = 300 };
    private readonly NumericUpDown _debounce = Num(SettingsLimits.DebounceMs);
    private readonly NumericUpDown _history = Num(SettingsLimits.History);
    private readonly NumericUpDown _timeout = Num(SettingsLimits.DownloadTimeoutSec);
    private readonly NumericUpDown _maxSize = Num(SettingsLimits.MaxSizeMb);
    private readonly TextBox _proxy = new() { Width = 220 };

    private readonly Button _ok = new() { Text = "OK", DialogResult = DialogResult.None, AutoSize = true, MinimumSize = new Size(80, 0) };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.Firebrick };

    private static readonly Color Invalid = Color.FromArgb(255, 220, 220);

    public SettingsForm(AppSettings settings, string settingsPath, Action<string> openText)
    {
        _source = settings;
        Result = settings.Clone();
        _settingsPath = settingsPath;
        _openText = openText;

        // 寸法はすべて 96dpi（100%）基準で書き、表示倍率（125%・150% など）に合わせて WinForms に拡大させる。
        // デザイナーが生成するコードと同じく、レイアウトを止めた状態で基準DPIを設定してから部品を並べる
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "ProjectDeck 設定";
        Font = new Font("Meiryo UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(600, 460);
        Icon = AppIcon.Create(32);

        BuildUi();
        ResumeLayout(false);
        PerformLayout();
        LoadFrom(settings);
    }

    // ================= 画面の組み立て =================

    private void BuildUi()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(10, 4) };

        // レイアウト
        var layout = Page(tabs, "レイアウト");
        Row(layout, "縦の数", _rows);
        Row(layout, "横の数", _cols);
        Row(layout, "ボタンサイズ", _buttonSize, "px");
        Row(layout, "間隔", _gap, "px");
        Row(layout, "角丸", _radius, "px");
        _preview.Size = _preview.MinimumSize = new Size(260, 150);
        Row(layout, "プレビュー", _preview);

        // ウィンドウ
        var window = Page(tabs, "ウィンドウ");
        Row(window, "表示位置", _position);
        _monitor.Items.Add("primary");
        for (var i = 1; i <= Screen.AllScreens.Length; i++) _monitor.Items.Add(i.ToString());
        _monitor.DropDownStyle = ComboBoxStyle.DropDown;
        Row(window, "モニタ", _monitor, "primary = メイン、1,2,… = 番号");
        Row(window, "画面端からの余白", _margin, "px");
        Row(window, "", _topmost);
        Row(window, "不透明度", _opacity, "%（30～100）");
        Row(window, "", _hideAfterLaunch);

        // 起動
        var startup = Page(tabs, "起動");
        Row(startup, "", _autoStart);
        Row(startup, "", _showOnStart);
        Row(startup, "起動遅延", _delay, "秒（PC起動時のみ）");

        // 文字
        var label = Page(tabs, "文字");
        using (var fonts = new InstalledFontCollection())
            _font.Items.AddRange(fonts.Families.Select(f => (object)f.Name).ToArray());
        Row(label, "フォント", _font);
        Row(label, "サイズ", _fontSize, "pt");
        Row(label, "文字色", _labelColor);
        Row(label, "指定色", _labelColorHex, "#RRGGBB");

        // ダイヤル
        var dial = Page(tabs, "ダイヤル");
        Row(dial, "", _dialVisible);
        Row(dial, "", new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "ダイヤルごとの機能・刻み幅は deck.json の dials で割り付けます。\n（master_volume / mic_volume / app_volume / brightness / none）\n操作：ホイールで調整、Shift+ホイールで1%刻み、クリックでミュート。",
        });

        // ホットキー
        var hotkey = Page(tabs, "ホットキー");
        Row(hotkey, "表示/非表示", _hkToggle);
        Row(hotkey, "", new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "入力欄をクリックしてキーを押すと登録します（Ctrl / Alt / Shift と組み合わせ）。\nBackspace で解除。",
        });

        // 詳細
        var adv = Page(tabs, "詳細");
        var browse = new Button { Text = "参照…", AutoSize = true };
        browse.Click += (_, _) => BrowseDeck();
        Row(adv, "deck.json の場所", _deckPath, browse);
        Row(adv, "読込待ち時間", _debounce, "ms");
        Row(adv, "履歴の世代数", _history);
        Row(adv, "画像のタイムアウト", _timeout, "秒");
        Row(adv, "画像の最大サイズ", _maxSize, "MB");
        Row(adv, "プロキシ", _proxy, "system / none / http://…");

        // 下のボタン
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(6, 6, 6, 8),
        };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 0) };
        var reset = new Button { Text = "既定値に戻す", AutoSize = true };
        var open = new Button { Text = "settings.toml を開く", AutoSize = true };
        reset.Click += (_, _) => LoadFrom(SettingsStore.Parse(SettingsStore.DefaultText()).Settings!);
        open.Click += (_, _) => _openText(_settingsPath);
        _ok.Click += (_, _) => OnOk();
        bottom.Controls.AddRange([_ok, cancel, reset, open, _status]);
        AcceptButton = _ok;
        CancelButton = cancel;

        Controls.Add(tabs);
        Controls.Add(bottom);

        // 値が変わったらプレビューと入力チェックを更新
        foreach (var c in new Control[] { _rows, _cols, _buttonSize, _gap, _radius, _labelColorHex, _proxy, _deckPath,
                     _hkToggle, _monitor, _font })
            c.TextChanged += (_, _) => OnValueChanged();
        foreach (var n in new[] { _rows, _cols, _buttonSize, _gap, _radius })
            n.ValueChanged += (_, _) => OnValueChanged();
        _labelColor.SelectedIndexChanged += (_, _) => OnValueChanged();
        _monitor.SelectedIndexChanged += (_, _) => OnValueChanged();
    }

    private static TableLayoutPanel Page(TabControl tabs, string title)
    {
        var page = new TabPage(title) { Padding = new Padding(12), UseVisualStyleBackColor = true };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(table);
        tabs.TabPages.Add(page);
        return table;
    }

    private static void Row(TableLayoutPanel table, string caption, Control control, object? suffix = null)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 7, 3, 3) }, 0, row);
        // 部品を表のセルに直接置くと幅が縮められるため、必ず FlowLayoutPanel に入れて元の幅を保つ
        var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        control.Margin = new Padding(3, 4, 3, 4);
        // 高DPIでの自動拡大のときに幅が縮められることがあるため、指定した幅を最小幅として固定する
        if (!control.AutoSize) control.MinimumSize = new Size(control.Width, 0);
        flow.Controls.Add(control);
        if (suffix != null)
        {
            flow.Controls.Add(suffix as Control ?? new Label
            {
                Text = suffix.ToString(),
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 7, 3, 3),
            });
        }
        table.Controls.Add(flow, 1, row);
    }

    private static NumericUpDown Num((int Min, int Max) range) => new() { Minimum = range.Min, Maximum = range.Max, Width = 70 };

    private static ComboBox Combo(string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        c.Items.AddRange(items);
        return c;
    }

    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true };

    private static TextBox HotkeyBox()
    {
        var box = new TextBox { Width = 200, ReadOnly = true, BackColor = SystemColors.Window, ShortcutsEnabled = false };
        box.KeyDown += (_, e) =>
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            if (e.KeyCode is Keys.Back or Keys.Delete && e.Modifiers == Keys.None)
            {
                box.Text = "";
                return;
            }
            if (Hotkey.Format(e.KeyData) is { } text) box.Text = text;
        };
        return box;
    }

    // ================= 値の読み書き =================

    private void LoadFrom(AppSettings s)
    {
        _loading = true;
        _rows.Value = s.Layout.Rows;
        _cols.Value = s.Layout.Cols;
        _buttonSize.Value = s.Layout.ButtonSize;
        _gap.Value = s.Layout.Gap;
        _radius.Value = s.Layout.CornerRadius;

        _position.SelectedIndex = Math.Max(0, Array.IndexOf(SettingsLimits.Positions, s.Window.Position));
        _monitor.Text = s.Window.Monitor;
        _margin.Value = s.Window.Margin;
        _topmost.Checked = s.Window.Topmost;
        _opacity.Value = (decimal)Math.Round(s.Window.Opacity * 100);
        _hideAfterLaunch.Checked = s.Window.HideAfterLaunch;

        _autoStart.Checked = s.Startup.AutoStart;
        _showOnStart.Checked = s.Startup.ShowOnStart;
        _delay.Value = s.Startup.DelaySec;

        _font.Text = s.Label.Font;
        _fontSize.Value = (decimal)s.Label.Size;
        var colorIndex = s.Label.Color switch { "auto" => 0, "white" => 1, "black" => 2, _ => 3 };
        _labelColor.SelectedIndex = colorIndex;
        _labelColorHex.Text = colorIndex == 3 ? s.Label.Color : "";

        _dialVisible.Checked = s.Dial.Visible;

        _hkToggle.Text = s.Hotkey.Toggle;

        _deckPath.Text = s.Interface.Path;
        _debounce.Value = s.Interface.DebounceMs;
        _history.Value = s.Interface.History;
        _timeout.Value = s.Image.DownloadTimeoutSec;
        _maxSize.Value = s.Image.MaxSizeMb;
        _proxy.Text = s.Image.Proxy;
        _loading = false;
        OnValueChanged();
    }

    private AppSettings ReadValues()
    {
        var s = _source.Clone();
        s.Layout.Rows = (int)_rows.Value;
        s.Layout.Cols = (int)_cols.Value;
        s.Layout.ButtonSize = (int)_buttonSize.Value;
        s.Layout.Gap = (int)_gap.Value;
        s.Layout.CornerRadius = (int)_radius.Value;

        s.Window.Position = SettingsLimits.Positions[Math.Max(0, _position.SelectedIndex)];
        s.Window.Monitor = _monitor.Text.Trim();
        s.Window.Margin = (int)_margin.Value;
        s.Window.Topmost = _topmost.Checked;
        s.Window.Opacity = (double)_opacity.Value / 100.0;
        s.Window.HideAfterLaunch = _hideAfterLaunch.Checked;

        s.Startup.AutoStart = _autoStart.Checked;
        s.Startup.ShowOnStart = _showOnStart.Checked;
        s.Startup.DelaySec = (int)_delay.Value;

        s.Label.Font = _font.Text.Trim();
        s.Label.Size = (double)_fontSize.Value;
        s.Label.Color = _labelColor.SelectedIndex switch { 0 => "auto", 1 => "white", 2 => "black", _ => _labelColorHex.Text.Trim() };

        s.Dial.Visible = _dialVisible.Checked;

        s.Hotkey.Toggle = _hkToggle.Text;

        s.Interface.Path = _deckPath.Text.Trim();
        s.Interface.DebounceMs = (int)_debounce.Value;
        s.Interface.History = (int)_history.Value;
        s.Image.DownloadTimeoutSec = (int)_timeout.Value;
        s.Image.MaxSizeMb = (int)_maxSize.Value;
        s.Image.Proxy = _proxy.Text.Trim();
        return s;
    }

    // ================= 入力チェック =================

    private void OnValueChanged()
    {
        if (_loading) return;
        _preview.SetLayout((int)_rows.Value, (int)_cols.Value, (int)_buttonSize.Value, (int)_gap.Value, (int)_radius.Value);
        _labelColorHex.Enabled = _labelColor.SelectedIndex == 3;

        var errors = new List<string>();
        Mark(_labelColorHex, _labelColor.SelectedIndex != 3 || ColorRegex().IsMatch(_labelColorHex.Text.Trim()), "指定色は #RRGGBB で入力してください", errors);
        var proxy = _proxy.Text.Trim();
        Mark(_proxy, proxy is "system" or "none" || Uri.TryCreate(proxy, UriKind.Absolute, out var u) && u.Scheme is "http" or "https",
            "プロキシは system / none / http://… で入力してください", errors);
        Mark(_deckPath, _deckPath.Text.Trim().Length > 0, "deck.json の場所を入力してください", errors);
        Mark(_monitor, _monitor.Text.Trim() == "primary" || int.TryParse(_monitor.Text.Trim(), out var n) && n >= 1,
            "モニタは primary または 1 以上の番号です", errors);
        Mark(_font, _font.Text.Trim().Length > 0, "フォントを入力してください", errors);

        // ホットキー（空欄は「使わない」）
        Mark(_hkToggle, _hkToggle.Text.Length == 0 || Hotkey.TryParse(_hkToggle.Text, out _, out _),
            "ホットキーは Ctrl / Alt / Shift とキーの組み合わせにしてください", errors);

        _ok.Enabled = errors.Count == 0;
        _status.Text = errors.FirstOrDefault() ?? "";
    }

    private static void Mark(Control c, bool ok, string message, List<string> errors)
    {
        c.BackColor = ok ? SystemColors.Window : Invalid;
        if (!ok && !errors.Contains(message)) errors.Add(message);
    }

    private void OnOk()
    {
        OnValueChanged();
        if (!_ok.Enabled) return;
        Result = ReadValues();
        DialogResult = DialogResult.OK;
        Close();
    }

    private void BrowseDeck()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "deck.json の場所",
            Filter = "JSON (*.json)|*.json|すべてのファイル|*.*",
            CheckFileExists = false,
            FileName = "deck.json",
        };
        try { dlg.InitialDirectory = Path.GetDirectoryName(Core.PathExpander.Expand(_deckPath.Text)); }
        catch (ArgumentException) { }
        if (dlg.ShowDialog(this) == DialogResult.OK) _deckPath.Text = dlg.FileName;
    }

    /// <summary>ダイアログを開いている間に settings.toml が外部で変更されたとき</summary>
    public void OnExternalChange(AppSettings latest)
    {
        BeginInvoke(() =>
        {
            if (MessageBox.Show(this, "settings.toml が変更されました。読み直しますか？\n（いいえ：画面の入力内容を残します）",
                    "ProjectDeck 設定", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _source = latest;
                LoadFrom(latest);
            }
        });
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorRegex();

    /// <summary>レイアウトのプレビュー（値を変えるとその場で変わる）</summary>
    private sealed class PreviewPanel : Panel
    {
        private int _rows = 3, _cols = 5, _size = 64, _gap = 4, _radius = 8;

        public PreviewPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(0x1b, 0x1d, 0x22);
        }

        public void SetLayout(int rows, int cols, int size, int gap, int radius)
        {
            (_rows, _cols, _size, _gap, _radius) = (rows, cols, size, gap, radius);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const int pad = 4;
            float w = pad * 2 + _cols * _size + (_cols - 1) * _gap;
            float h = 32 + pad * 2 + _rows * _size + (_rows - 1) * _gap;
            // 下の 1 行は大きさの表示に使う
            var textH = Font.Height + 4;
            var scale = Math.Min(1f, Math.Min((Width - 8) / w, (Height - 8 - textH) / h));
            g.TranslateTransform((Width - w * scale) / 2, (Height - textH - h * scale) / 2);
            g.ScaleTransform(scale, scale);
            using (var border = new Pen(Color.FromArgb(0x3a, 0x3f, 0x48)))
                g.DrawRectangle(border, 0, 0, w, h);
            using (var tab = new SolidBrush(Color.FromArgb(0x3d, 0x7b, 0xd9)))
                g.FillRectangle(tab, 6, 5, 66, 22);
            using var fill = new SolidBrush(Color.FromArgb(0x4a, 0x5d, 0x7e));
            for (var r = 0; r < _rows; r++)
                for (var c = 0; c < _cols; c++)
                {
                    using var path = DeckButtonRenderer.RoundedRect(new RectangleF(pad + c * (_size + _gap), 32 + pad + r * (_size + _gap), _size, _size), _radius);
                    g.FillPath(fill, path);
                }
            g.ResetTransform();
            TextRenderer.DrawText(g, $"{(int)w} × {(int)h} px（100% 表示のとき）", Font, new Point(4, Height - textH), Color.FromArgb(0xb8, 0xbe, 0xc8));
        }
    }
}
