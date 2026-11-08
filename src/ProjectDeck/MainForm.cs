using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ProjectDeck.Core;
using ProjectDeck.Core.Deck;
using ProjectDeck.Core.Images;
using ProjectDeck.Core.Settings;
using ProjectDeck.Native;
using ProjectDeck.Rendering;
using ProjectDeck.Services;

namespace ProjectDeck;

/// <summary>読込みランプの状態（仕様書 5.3）</summary>
internal enum LampState { Idle, Loading, Done, Warning, Error }

/// <summary>
/// ランチャー本体の画面。ツールバー・ボタングリッド・ダイヤル部をすべて1枚に自前で描画する（仕様書 9.3）。
/// 処理は役割ごとに分けたファイル（MainForm.*.cs）にある：
///   MainForm.cs        … 寸法・描画
///   MainForm.Deck.cs   … deck.json / settings.toml の監視・読込・書込、ランプ
///   MainForm.Input.cs  … マウス・D&amp;D・メニュー・ホットキー・表示位置
/// </summary>
internal sealed partial class MainForm : Form
{
    // ---- 色（モックに合わせる） ----
    private static readonly Color BackColorMain = Color.FromArgb(0x1b, 0x1d, 0x22);
    private static readonly Color BorderColor = Color.FromArgb(0x3a, 0x3f, 0x48);
    private static readonly Color TabInactive = Color.FromArgb(0x2c, 0x30, 0x38);
    private static readonly Color TabInactiveText = Color.FromArgb(0xb8, 0xbe, 0xc8);
    private static readonly Color Accent = Color.FromArgb(0x3d, 0x7b, 0xd9);
    private static readonly Color IconOff = Color.FromArgb(0x7d, 0x83, 0x8d);
    private static readonly Color EmptyFill = Color.FromArgb(0x22, 0x25, 0x2b);
    private static readonly Color EmptyText = Color.FromArgb(0x5a, 0x60, 0x6b);
    private static readonly Color DialBack = Color.FromArgb(0x15, 0x17, 0x1b);

    private readonly SettingsStore _settingsStore;
    private AppSettings _settings;
    private readonly LauncherState _state;
    private readonly ShellWorker _shell;
    private readonly ImageFetcher _images;
    private readonly DialManager _dials;
    private readonly Dictionary<string, (string Json, Bitmap Bmp)> _renderCache = [];
    private readonly Dictionary<string, Image?> _pictureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolTip _tip = new() { InitialDelay = 500, ReshowDelay = 100, AutoPopDelay = 15000 };
    private string? _pageId;

    public MainForm(SettingsStore settingsStore, SettingsLoadResult initialSettings)
    {
        _settingsStore = settingsStore;
        _settings = initialSettings.Settings ?? SettingsStore.Parse(SettingsStore.DefaultText()).Settings!;
        _settingsErrors.AddRange(initialSettings.Errors);
        _settingsWarnings.AddRange(initialSettings.Warnings);
        _state = LauncherState.Load();
        _pageId = _state.Page;

        Text = "ProjectDeck";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = BackColorMain;
        DoubleBuffered = true;
        AllowDrop = true;
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Icon = AppIcon.Create(32);

        _shell = new ShellWorker(96);
        _shell.Completed += _ => { if (IsHandleCreated && !IsDisposed) BeginInvoke(Invalidate); };

        // image.url の画像（取得が終わったら再描画し、ランプを更新）
        _images = new ImageFetcher(new ImageCache(AppPaths.ImageCacheDirectory), _settings.Image);
        _images.Completed += _ => { if (IsHandleCreated && !IsDisposed) BeginInvoke(OnImageCompleted); };

        // ダイヤル（音量・マイク・明るさ）。値が変わったらダイヤル部だけ描き直す
        _dials = new DialManager();
        _dials.Changed += () => { if (IsHandleCreated && !IsDisposed) BeginInvoke(InvalidateDials); };

        InitDeck();
        _dials.Rebuild(_deck?.Dials ?? []);
        InitMenus();
        InitTimers();
        ApplyWindowSettings();
        UpdateLayoutSize();
        ApplyPosition();
        SyncAutoStart();
    }

    // ================= 寸法 =================

    private float S => DeviceDpi / 96f;
    private int Px(float v) => (int)Math.Round(v * S);

    private int Pad => Px(4);
    private int ToolbarH => Px(32);
    private int Bs => Px(_settings.Layout.ButtonSize);
    private int GapPx => Px(_settings.Layout.Gap);
    private int Rows => _settings.Layout.Rows;
    private int Cols => _settings.Layout.Cols;
    private int GridWidth => Cols * Bs + (Cols - 1) * GapPx;
    private int GridBottom => ToolbarH + Pad + Rows * Bs + (Rows - 1) * GapPx;
    private int DialH => Px(40);
    private bool DialVisible => _settings.Dial.Visible;

    private Rectangle CellRect(int row, int col) =>
        new(Pad + (col - 1) * (Bs + GapPx), ToolbarH + Pad + (row - 1) * (Bs + GapPx), Bs, Bs);

    // ツールバー右側の部品（右から ≡・⚙・◎・📌・ランプ）。22px 角を 2px 間隔で並べる
    private const int ToolSize = 22;
    private Rectangle ToolRect(int indexFromRight) =>
        new(ClientSize.Width - Px(4) - (indexFromRight + 1) * Px(ToolSize) - indexFromRight * Px(2), Px(5), Px(ToolSize), Px(ToolSize));
    private Rectangle MenuRect => ToolRect(0);
    private Rectangle GearRect => ToolRect(1);
    private Rectangle DialToggleRect => ToolRect(2);
    private Rectangle PinRect => ToolRect(3);
    private Rectangle LampRect => new(PinRect.Left - Px(2) - Px(14), Px(5), Px(14), Px(ToolSize)); // ランプは点だけなので幅を詰める
    private int TabsRight => LampRect.Left - Px(2);

    /// <summary>ツールバーのアイコンの拡大率（元の 18px 角の絵を 22px 角に合わせる）</summary>
    private float K => S * ToolSize / 18f;

    /// <summary>行列数・ボタンサイズ・ダイヤル表示に合わせてウィンドウの大きさを決める</summary>
    private void UpdateLayoutSize()
    {
        var width = Math.Max(Pad * 2 + GridWidth, Px(170));
        var height = GridBottom + Pad + (DialVisible ? DialH + Pad : 0);
        ClientSize = new Size(width, height);
        Invalidate();
    }

    /// <summary>位置（行・列）にあるセル。グリッド外なら null</summary>
    private (int Row, int Col)? CellAt(Point p)
    {
        for (var r = 1; r <= Rows; r++)
            for (var c = 1; c <= Cols; c++)
                if (CellRect(r, c).Contains(p)) return (r, c);
        return null;
    }

    private DeckPage? CurrentPage => _deck?.Pages.FirstOrDefault(p => p.Id == _pageId && !p.Archived);

    private IReadOnlyList<DeckPage> VisiblePages => _deck?.Pages.Where(p => !p.Archived).ToList() ?? [];

    // ================= 描画 =================

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        using (var border = new Pen(BorderColor))
            g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        DrawTabs(g);
        DrawToolbarButtons(g);
        DrawGrid(g);
        if (DialVisible) DrawDials(g);
    }

    private readonly List<(Rectangle Rect, string PageId)> _tabRects = [];

    private void DrawTabs(Graphics g)
    {
        _tabRects.Clear();
        using var font = new Font("Meiryo UI", 12 * S, GraphicsUnit.Pixel);
        var pages = VisiblePages;
        var x = Px(6);
        var y = Px(5);
        var h = Px(22);
        if (pages.Count == 0)
        {
            TextRenderer.DrawText(g, _deck == null ? "（読込中）" : "（ページなし）", font, new Point(x, y + Px(1)), IconOff);
            return;
        }

        // 幅を計算し、はみ出す場合は選択中のタブが見える位置から並べる
        var widths = pages.Select(p => Math.Max(Px(44), TextRenderer.MeasureText(g, p.Name, font, Size.Empty, TextFormatFlags.NoPadding).Width + Px(14))).ToList();
        var gap = Px(3);
        var overflow = x + widths.Sum(w => w + gap) - gap > TabsRight;
        var marker = overflow ? Px(10) : 0; // 隠れたタブがあることを示す ‹ › の幅
        var right = TabsRight - marker;
        var current = Math.Max(0, pages.ToList().FindIndex(p => p.Id == _pageId));
        // 左の ‹ は、左に隠れたタブがあるときだけ場所を取る
        int Span(int from) => widths.Skip(from).Take(current - from + 1).Sum(w => w + gap) - gap;
        var start = 0;
        if (x + Span(0) > right)
        {
            start = 1;
            while (start < current && x + marker + Span(start) > right) start++;
        }
        var left = start > 0 ? x + marker : x;

        x = left;
        var last = start - 1;
        for (var i = start; i < pages.Count; i++)
        {
            if (x + widths[i] > right) break;
            last = i;
            var rect = new Rectangle(x, y, widths[i], h);
            var active = pages[i].Id == _pageId;
            var color = active
                ? (DeckButtonRenderer.TryParseColor(pages[i].Color, out var c) ? c : Accent)
                : TabInactive;
            using (var path = DeckButtonRenderer.RoundedRect(rect, Px(5)))
            using (var brush = new SolidBrush(color))
                g.FillPath(brush, path);
            TextRenderer.DrawText(g, pages[i].Name, font, rect, active ? Color.White : TabInactiveText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            _tabRects.Add((rect, pages[i].Id));
            x += widths[i] + gap;
        }

        // 左右に隠れたタブがあれば ‹ › を出す（タブの上でホイールを回すと切り替わる）
        if (overflow)
        {
            const TextFormatFlags center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            if (start > 0)
                TextRenderer.DrawText(g, "‹", font, new Rectangle(Px(6), y, marker, h), TabInactiveText, center);
            if (last < pages.Count - 1)
                TextRenderer.DrawText(g, "›", font, new Rectangle(right, y, marker, h), TabInactiveText, center);
        }
    }

    private void DrawToolbarButtons(Graphics g)
    {
        // 読込みランプ
        var lamp = LampColor();
        var center = new PointF(LampRect.X + LampRect.Width / 2f, LampRect.Y + LampRect.Height / 2f);
        var visible = !(Lamp == LampState.Loading && !_blinkOn);
        if (visible)
        {
            using var glow = new SolidBrush(Color.FromArgb(64, lamp));
            using var dot = new SolidBrush(lamp);
            g.FillEllipse(glow, center.X - 7.5f * S, center.Y - 7.5f * S, 15 * S, 15 * S);
            g.FillEllipse(dot, center.X - 4.5f * S, center.Y - 4.5f * S, 9 * S, 9 * S);
        }

        // 最前面（📌）
        DrawToggle(g, PinRect, TopMost, (gg, c, col) =>
        {
            using var b = new SolidBrush(col);
            gg.FillEllipse(b, c.X - 3.2f * K, c.Y - 6.2f * K, 6.4f * K, 6.4f * K);
            gg.FillPolygon(b, [new PointF(c.X - 1 * K, c.Y - 3 * K), new PointF(c.X + 1 * K, c.Y - 3 * K),
                new PointF(c.X + 0.6f * K, c.Y + 4 * K), new PointF(c.X - 0.6f * K, c.Y + 4 * K)]);
        });

        // ダイヤル表示（◎）
        DrawToggle(g, DialToggleRect, DialVisible, (gg, c, col) =>
        {
            using var p = new Pen(col, 1.6f * K);
            gg.DrawEllipse(p, c.X - 5 * K, c.Y - 5 * K, 10 * K, 10 * K);
            gg.DrawLine(p, c.X, c.Y, c.X + 3 * K, c.Y - 3 * K);
        });

        // 設定（⚙）
        DrawToggle(g, GearRect, false, (gg, c, col) =>
        {
            using var p = new Pen(col, 1.6f * K);
            for (var i = 0; i < 8; i++)
            {
                var a = i * Math.PI / 4;
                gg.DrawLine(p, c.X + (float)Math.Cos(a) * 3.5f * K, c.Y + (float)Math.Sin(a) * 3.5f * K,
                    c.X + (float)Math.Cos(a) * 6f * K, c.Y + (float)Math.Sin(a) * 6f * K);
            }
            gg.DrawEllipse(p, c.X - 3.5f * K, c.Y - 3.5f * K, 7 * K, 7 * K);
        }, TabInactiveText);

        // メニュー（≡）
        DrawToggle(g, MenuRect, false, (gg, c, col) =>
        {
            using var p = new Pen(col, 1.4f * K);
            for (var i = -1; i <= 1; i++) gg.DrawLine(p, c.X - 4 * K, c.Y + i * 3.5f * K, c.X + 4 * K, c.Y + i * 3.5f * K);
        }, TabInactiveText);
    }

    private void DrawToggle(Graphics g, Rectangle rect, bool on, Action<Graphics, PointF, Color> drawIcon, Color? offColor = null)
    {
        using (var path = DeckButtonRenderer.RoundedRect(rect, Px(5)))
        using (var brush = new SolidBrush(on ? Accent : TabInactive))
            g.FillPath(brush, path);
        drawIcon(g, new PointF(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f), on ? Color.White : offColor ?? IconOff);
    }

    private void DrawGrid(Graphics g)
    {
        var page = CurrentPage;
        var radius = Px(_settings.Layout.CornerRadius);
        using var plusFont = new Font("Meiryo UI", 20 * S * _settings.Layout.ButtonSize / 64f, GraphicsUnit.Pixel);
        using var dash = new Pen(BorderColor) { DashPattern = [4, 3] };
        using var emptyBrush = new SolidBrush(EmptyFill);

        for (var r = 1; r <= Rows; r++)
        {
            for (var c = 1; c <= Cols; c++)
            {
                var rect = CellRect(r, c);
                var button = page?.ButtonAt(r, c);
                if (button == null)
                {
                    var target = _dropTarget == (r, c);
                    using var path = DeckButtonRenderer.RoundedRect(rect, radius);
                    g.FillPath(emptyBrush, path);
                    if (target)
                    {
                        using var hl = new Pen(Accent, 2 * S);
                        g.DrawPath(hl, path);
                    }
                    else
                    {
                        g.DrawPath(dash, path);
                    }
                    TextRenderer.DrawText(g, "＋", plusFont, rect, target ? Accent : EmptyText,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    continue;
                }

                var bmp = GetButtonBitmap(button, page!);
                if (_pressed == (r, c))
                {
                    // 押した瞬間に少し縮む
                    var shrink = (int)(Bs * 0.04f);
                    rect.Inflate(-shrink, -shrink);
                }
                g.DrawImage(bmp, rect);
                if (_hover == (r, c) && _pressed == null)
                {
                    using var path = DeckButtonRenderer.RoundedRect(rect, radius);
                    using var hl = new Pen(Color.FromArgb(110, 255, 255, 255), 1.5f * S);
                    g.DrawPath(hl, path);
                }
            }
        }
    }

    /// <summary>ダイヤル部（最大4個）。ホイール・クリックの当たり判定用に位置を覚えておく</summary>
    private readonly List<(Rectangle Rect, int Index)> _dialRects = [];

    private void DrawDials(Graphics g)
    {
        _dialRects.Clear();
        var area = new Rectangle(Pad, GridBottom + Pad, ClientSize.Width - Pad * 2, DialH);
        using (var path = DeckButtonRenderer.RoundedRect(area, Px(6)))
        using (var brush = new SolidBrush(DialBack))
            g.FillPath(brush, path);

        using var labelFont = new Font("Meiryo UI", 11 * S, GraphicsUnit.Pixel);
        using var valueFont = new Font("Meiryo UI", 12 * S, FontStyle.Bold, GraphicsUnit.Pixel);
        var dials = _dials.Dials;
        if (dials.Count == 0)
        {
            TextRenderer.DrawText(g, "ダイヤル未設定（deck.json の dials）", labelFont, area, IconOff,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var slot = area.Width / Math.Max(3, dials.Count);
        for (var i = 0; i < dials.Count; i++)
        {
            var (def, ctl) = dials[i];
            var cell = new Rectangle(area.X + slot * i, area.Y, slot, area.Height);
            _dialRects.Add((cell, i));

            var accent = DialColor(def.Function);
            var value = ctl.Value;
            var off = ctl.Unavailable != null || ctl.Muted;
            var cx = cell.X + Px(20);
            var cy = cell.Y + cell.Height / 2;
            var r = Px(12);

            // つまみ
            using (var knob = new LinearGradientBrush(new Rectangle(cx - r, cy - r, r * 2, r * 2),
                       Color.FromArgb(0x6a, 0x70, 0x7b), Color.FromArgb(0x2a, 0x2d, 0x33), 60f))
                g.FillEllipse(knob, cx - r, cy - r, r * 2, r * 2);

            // 外周の目盛り（左下 -135° ～ 右下 +135°）と、今の値までの弧
            var ring = new RectangleF(cx - r - Px(3), cy - r - Px(3), (r + Px(3)) * 2, (r + Px(3)) * 2);
            using (var track = new Pen(Color.FromArgb(0x3a, 0x3f, 0x48), 2.4f * S))
                g.DrawArc(track, ring, 135, 270);
            if (value is { } v && v > 0)
            {
                using var arc = new Pen(off ? IconOff : accent, 2.4f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(arc, ring, 135, 270f * v / 100f);
            }

            // つまみの指針
            if (value is { } pv)
            {
                var angle = (135 + 270.0 * pv / 100) * Math.PI / 180;
                using var needle = new Pen(off ? IconOff : accent, 2.4f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(needle, cx, cy, cx + (float)Math.Cos(angle) * (r - Px(3)), cy + (float)Math.Sin(angle) * (r - Px(3)));
            }

            // 名前と値
            var tx = cx + r + Px(8);
            var label = def.Label.Length > 0 ? def.Label : DefaultDialLabel(def.Function);
            TextRenderer.DrawText(g, label, labelFont, new Rectangle(tx, cy - Px(16), cell.Right - tx, Px(15)),
                Color.FromArgb(0xc8, 0xcd, 0xd5), TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            var (text, color) = ctl.Unavailable != null ? ("--", IconOff)
                : ctl.Muted ? ("ミュート", Color.FromArgb(0xe5, 0x53, 0x4b))
                : ($"{value}%", accent);
            TextRenderer.DrawText(g, text, valueFont, new Rectangle(tx, cy, cell.Right - tx, Px(16)),
                color, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>ダイヤル部だけ描き直す</summary>
    private void InvalidateDials()
    {
        if (DialVisible) Invalidate(new Rectangle(0, GridBottom, ClientSize.Width, ClientSize.Height - GridBottom));
    }

    /// <summary>位置にあるダイヤル。無ければ null</summary>
    private (DeckDial Def, IDialControl Control)? DialAt(Point p)
    {
        if (!DialVisible) return null;
        foreach (var (rect, i) in _dialRects)
            if (rect.Contains(p) && i < _dials.Dials.Count) return _dials.Dials[i];
        return null;
    }

    private static Color DialColor(string function) => function switch
    {
        "brightness" => Color.FromArgb(0xe3, 0xb3, 0x41),
        "mic_volume" => Color.FromArgb(0x57, 0xab, 0x5a),
        "app_volume" => Color.FromArgb(0xb4, 0x8e, 0xad),
        _ => Color.FromArgb(0x7a, 0xb7, 0xff),
    };

    private static string DefaultDialLabel(string function) => function switch
    {
        "master_volume" => "音量",
        "mic_volume" => "マイク",
        "brightness" => "明るさ",
        "app_volume" => "アプリ音量",
        _ => "（なし）",
    };

    // ================= ボタン画像 =================

    private Bitmap GetButtonBitmap(DeckButton b, DeckPage page)
    {
        var picture = GetPicture(b, page);
        ShellWorker.PathInfo? info = null;
        if (b.Action.PrimaryPath is { } p)
            info = _shell.Get(PathExpander.Expand(p, page.Vars));
        var missing = info is { Exists: false };
        var icon = picture == null && b.Image?.Color == null ? info?.Icon : null;

        var style = new LabelStyle(_settings.Label.Font, _settings.Label.Size, _settings.Label.Color);
        var key = $"{b.Json}|{page.Color}|{Bs}|{_settings.Layout.CornerRadius}|{icon != null}|{missing}|{picture != null}|{style}|{DeviceDpi}";
        if (_renderCache.TryGetValue(key, out var cached)) return cached.Bmp;

        var bmp = DeckButtonRenderer.Render(new ButtonVisual(b, page.Color, picture, icon, missing), Bs, Px(_settings.Layout.CornerRadius), style, S);
        _renderCache[key] = (b.Json, bmp);
        return bmp;
    }

    /// <summary>image.file / image.data の画像（url はフェーズ2で対応）</summary>
    private Image? GetPicture(DeckButton b, DeckPage page)
    {
        var img = b.Image;
        if (img == null) return null;
        if (img.Url != null)
        {
            var bmp = _images.Get(img.Url, img.Query, img.License);
            if (bmp == null) UpdateBlink(); // 取得中はランプを黄点滅
            return bmp;
        }
        string? key = img.File != null ? "file:" + PathExpander.Expand(img.File, page.Vars)
            : img.Data != null ? "data:" + img.Data.GetHashCode() : null;
        if (key == null) return null;
        if (_pictureCache.TryGetValue(key, out var cached)) return cached;

        Image? loaded = null;
        try
        {
            if (img.File != null)
            {
                var bytes = File.ReadAllBytes(PathExpander.Expand(img.File, page.Vars));
                loaded = LoadImage(bytes);
            }
            else if (img.Data != null)
            {
                var data = img.Data;
                var comma = data.IndexOf(',');
                if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0) data = data[(comma + 1)..];
                loaded = LoadImage(Convert.FromBase64String(data));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            _imageWarnings.Add($"{b.Id}: 画像を読み込めません（{ex.Message}）");
            Logger.Warn($"画像を読み込めません {b.Id}: {ex.Message}");
        }
        _pictureCache[key] = loaded;
        return loaded;
    }

    private static Bitmap LoadImage(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var img = Image.FromStream(ms);
        return new Bitmap(img);
    }

    /// <summary>描画キャッシュを捨てる（設定変更・DPI変更時）</summary>
    private void ClearRenderCache()
    {
        foreach (var (_, bmp) in _renderCache.Values) bmp.Dispose();
        _renderCache.Clear();
    }

    /// <summary>今の deck で使っていないキャッシュを捨てる（変わったボタンだけ作り直すため）</summary>
    private void TrimRenderCache()
    {
        if (_deck == null) return;
        var jsons = _deck.Pages.SelectMany(p => p.Buttons).Select(b => b.Json).ToHashSet();
        foreach (var (key, entry) in _renderCache.ToList())
        {
            if (jsons.Contains(entry.Json)) continue;
            entry.Bmp.Dispose();
            _renderCache.Remove(key);
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ClearRenderCache();
        UpdateLayoutSize();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 では角丸ウィンドウにする（Windows 10 では何もしない）
        var pref = NativeMethods.DWMWCP_ROUND;
        _ = NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        RegisterHotkeys();
    }
}
