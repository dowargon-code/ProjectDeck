using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using ProjectDeck.Core;
using ProjectDeck.Core.Deck;
using ProjectDeck.Native;
using ProjectDeck.Rendering;
using ProjectDeck.Services;

namespace ProjectDeck;

// マウス・D&D・メニュー・ホットキー・表示位置・トレイ
internal sealed partial class MainForm
{
    private (int Row, int Col)? _pressed;
    private (int Row, int Col)? _hover;
    private (int Row, int Col)? _dropTarget;
    private string? _tipText;
    private bool _exiting;
    private SettingsForm? _settingsForm;

    private ContextMenuStrip _menu = null!;
    private ContextMenuStrip _buttonMenu = null!;
    private NotifyIcon _tray = null!;
    private DeckButton? _menuButton;

    private const int HotkeyToggle = 1;

    // ================= メニュー・トレイ =================

    private void InitMenus()
    {
        _menu = new ContextMenuStrip();
        _menu.Items.Add("deck.json を開く", null, (_, _) => OpenText(_deckStore.DeckPath));
        _menu.Items.Add("settings.toml を開く", null, (_, _) => OpenText(_settingsStore.SettingsPath));
        _menu.Items.Add("データフォルダを開く", null, (_, _) => StartShell("explorer.exe", $"\"{AppPaths.DataDirectory}\""));
        _menu.Items.Add("既定位置に戻す", null, (_, _) => ResetPosition());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("終了", null, (_, _) => ExitApp());

        _buttonMenu = new ContextMenuStrip();
        var openLocation = new ToolStripMenuItem("場所を開く", null, (_, _) => OpenButtonLocation());
        var copyPath = new ToolStripMenuItem("パスをコピー", null, (_, _) => CopyButtonPath());
        _buttonMenu.Items.Add(openLocation);
        _buttonMenu.Items.Add(copyPath);
        _buttonMenu.Items.Add(new ToolStripSeparator());
        _buttonMenu.Items.Add("削除", null, (_, _) => DeleteButton());
        _buttonMenu.Opening += (_, _) =>
        {
            var target = ButtonTarget(_menuButton);
            openLocation.Enabled = target != null && !target.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            copyPath.Enabled = target != null;
            copyPath.Text = target?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? "URLをコピー" : "パスをコピー";
        };

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("表示", null, (_, _) => ShowLauncher());
        trayMenu.Items.Add("設定…", null, (_, _) => OpenSettings());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("終了", null, (_, _) => ExitApp());
        _tray = new NotifyIcon
        {
            Icon = AppIcon.Create(16),
            Text = "ProjectDeck",
            ContextMenuStrip = trayMenu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleLauncher(); };
    }

    private void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (text.Length == 0) text = title;
        _tray.ShowBalloonTip(3000, "ProjectDeck - " + title, text, icon);
    }

    // ================= マウス =================

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        if (PinRect.Contains(e.Location)) { ToggleTopmost(); return; }
        if (DialToggleRect.Contains(e.Location)) { ToggleDial(); return; }
        if (GearRect.Contains(e.Location)) { OpenSettings(); return; }
        if (MenuRect.Contains(e.Location)) { _menu.Show(this, new Point(MenuRect.Left, MenuRect.Bottom)); return; }
        if (LampRect.Contains(e.Location)) { ShowLampMessage(); return; }

        foreach (var (rect, id) in _tabRects)
        {
            if (rect.Contains(e.Location))
            {
                SelectPage(id);
                return;
            }
        }

        var cell = CellAt(e.Location);
        if (cell != null && CurrentPage?.ButtonAt(cell.Value.Row, cell.Value.Col) != null)
        {
            _pressed = cell;
            Invalidate(CellRect(cell.Value.Row, cell.Value.Col));
            return;
        }

        // ツールバーの空き部分・余白のドラッグでウィンドウを移動
        if (e.Y < ToolbarH || cell == null)
        {
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessageW(Handle, NativeMethods.WM_NCLBUTTONDOWN, NativeMethods.HTCAPTION, IntPtr.Zero);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var cell = CellAt(e.Location);
        if (e.Button == MouseButtons.Left && _pressed != null)
        {
            var pressed = _pressed.Value;
            _pressed = null;
            Invalidate(CellRect(pressed.Row, pressed.Col));
            if (cell == pressed && CurrentPage is { } page && page.ButtonAt(pressed.Row, pressed.Col) is { } b)
                RunButton(b, page);
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            if (cell != null && CurrentPage?.ButtonAt(cell.Value.Row, cell.Value.Col) is { } b)
            {
                _menuButton = b;
                _buttonMenu.Show(this, e.Location);
            }
            else
            {
                _menu.Show(this, e.Location);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var cell = CellAt(e.Location);
        var hover = cell != null && CurrentPage?.ButtonAt(cell.Value.Row, cell.Value.Col) != null ? cell : null;
        if (hover != _hover)
        {
            if (_hover is { } old) Invalidate(CellRect(old.Row, old.Col));
            _hover = hover;
            if (_hover is { } now) Invalidate(CellRect(now.Row, now.Col));
        }

        // ツールチップ：ボタンは実行内容、ランプは状態
        string? tip = null;
        if (hover is { } h && CurrentPage is { } page && page.ButtonAt(h.Row, h.Col) is { } b)
            tip = $"{b.Label}\n{ActionRunner.Describe(b.Action, page.Vars)}";
        else if (LampRect.Contains(e.Location)) tip = LampMessage();
        else if (PinRect.Contains(e.Location)) tip = TopMost ? "最前面：ON" : "最前面：OFF";
        else if (DialToggleRect.Contains(e.Location)) tip = DialVisible ? "ダイヤルを隠す" : "ダイヤルを表示";
        else if (GearRect.Contains(e.Location)) tip = "設定";
        else if (MenuRect.Contains(e.Location)) tip = "メニュー";
        if (tip != _tipText)
        {
            _tipText = tip;
            _tip.SetToolTip(this, tip);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is { } old) Invalidate(CellRect(old.Row, old.Col));
        _hover = null;
        if (_pressed is { } p)
        {
            _pressed = null;
            Invalidate(CellRect(p.Row, p.Col));
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        // タブの上（ツールバー左側）でホイール → ページ切替
        if (e.Y < ToolbarH && e.X < TabsRight) StepPage(e.Delta > 0 ? -1 : 1);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Hide();
    }

    // ================= ページ =================

    private void SelectPage(string id)
    {
        if (_deck?.FindPage(id) is not { Archived: false }) return;
        _pageId = id;
        _state.Page = id;
        _state.Save();
        _hover = null;
        Invalidate();
    }

    private void StepPage(int delta)
    {
        var pages = VisiblePages;
        if (pages.Count == 0) return;
        var i = pages.ToList().FindIndex(p => p.Id == _pageId);
        i = ((i < 0 ? 0 : i + delta) % pages.Count + pages.Count) % pages.Count;
        SelectPage(pages[i].Id);
    }

    // ================= ボタンの実行・右クリック =================

    private void RunButton(DeckButton b, DeckPage page)
    {
        _ = ActionRunner.RunAsync(b.Action, page.Vars,
            target => BeginInvokeSafe(() =>
            {
                if (_deck?.FindPage(target) is { Archived: false }) SelectPage(target);
                else Notify("ページ移動", $"ページ \"{target}\" がありません", ToolTipIcon.Warning);
            }),
            error => BeginInvokeSafe(() =>
            {
                Notify(b.Label, error, ToolTipIcon.Warning);
                // 存在しなくなったパスは暗く表示し直す
                _shell.RecheckAll();
                Invalidate();
            }));
        if (_settings.Window.HideAfterLaunch && b.Action.Type != ActionTypes.Page) Hide();
    }

    /// <summary>ボタンの対象パス（展開済み）または URL</summary>
    private string? ButtonTarget(DeckButton? b)
    {
        if (b == null || CurrentPage is not { } page) return null;
        if (b.Action.PrimaryPath is { } p) return PathExpander.Expand(p, page.Vars);
        return b.Action.Type == ActionTypes.OpenUrl ? b.Action.Url : null;
    }

    private void OpenButtonLocation()
    {
        var target = ButtonTarget(_menuButton);
        if (target == null) return;
        if (File.Exists(target) || Directory.Exists(target))
            StartShell("explorer.exe", $"/select,\"{target}\"");
        else
            Notify("場所を開く", $"見つかりません: {target}", ToolTipIcon.Warning);
    }

    private void CopyButtonPath()
    {
        if (ButtonTarget(_menuButton) is { } target) Clipboard.SetText(target);
    }

    private void DeleteButton()
    {
        if (_menuButton is not { } b || CurrentPage is not { } page) return;
        if (MessageBox.Show(this, $"ボタン「{b.Label}」を削除しますか？\n（削除前の状態は履歴に残ります）", "ProjectDeck",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        var patch = NewPatch();
        patch["pages"] = new JsonArray(new JsonObject
        {
            ["id"] = page.Id,
            ["buttons"] = new JsonArray(new JsonObject { ["id"] = b.Id, ["delete"] = true }),
        });
        ApplyAppPatch(patch, "ボタンの削除");
    }

    private static JsonObject NewPatch() => new()
    {
        ["schema"] = DeckJson.SchemaName,
        ["mode"] = "merge",
        ["updated_by"] = "app",
    };

    // ================= D&D 登録（仕様書 F-10） =================

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        var cell = CellAt(PointToClient(new Point(e.X, e.Y)));
        var ok = e.Data?.GetDataPresent(DataFormats.FileDrop) == true && cell != null
                 && CurrentPage?.ButtonAt(cell.Value.Row, cell.Value.Col) == null;
        e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        var target = ok ? cell : null;
        if (target != _dropTarget)
        {
            _dropTarget = target;
            Invalidate();
        }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _dropTarget = null;
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        var start = _dropTarget;
        _dropTarget = null;
        Invalidate();
        if (start == null || e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        RegisterDroppedFiles(files, start.Value);
    }

    /// <summary>ドロップしたファイル・フォルダをボタンにする。複数ならドロップ位置から空きに順に置く</summary>
    private void RegisterDroppedFiles(string[] files, (int Row, int Col) start)
    {
        var deck = _deck ?? DeckValidator.Validate(DeckJson.EmptyDeck()).Deck!;
        var page = CurrentPage;
        var pageJson = new JsonObject();
        if (page == null)
        {
            // ページが1つも無いときは「共通」ページを作る
            page = new DeckPage("common", "共通", null, new Dictionary<string, string>(), false, []);
            pageJson["id"] = page.Id;
            pageJson["name"] = page.Name;
            _pageId = page.Id;
        }
        else
        {
            pageJson["id"] = page.Id;
        }

        var free = DeckInfo.FreeCells(page, Rows, Cols)
            .SkipWhile(c => (c.Row, c.Col) != start).ToList();
        var buttons = new JsonArray();
        for (var i = 0; i < files.Length && i < free.Count; i++)
        {
            buttons.Add(new JsonObject
            {
                ["id"] = DeckInfo.NextButtonId(deck, page, i),
                ["row"] = free[i].Row,
                ["col"] = free[i].Col,
                ["label"] = LabelFor(files[i]),
                ["action"] = ActionFor(files[i], page.Vars),
            });
        }
        if (files.Length > free.Count)
            Notify("登録", $"空きが足りないため {files.Length - free.Count} 件は登録しませんでした", ToolTipIcon.Warning);
        if (buttons.Count == 0) return;
        pageJson["buttons"] = buttons;
        var patch = NewPatch();
        patch["pages"] = new JsonArray(pageJson);
        ApplyAppPatch(patch, "ボタンの登録");
    }

    private static string LabelFor(string path)
    {
        var name = Directory.Exists(path)
            ? new DirectoryInfo(path).Name
            : Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name)) name = path; // ドライブ直下など
        return name.Length > 20 ? name[..20] : name;
    }

    private static JsonObject ActionFor(string path, IReadOnlyDictionary<string, string> vars)
    {
        var type = Directory.Exists(path) ? ActionTypes.OpenFolder
            : Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".bat" or ".cmd" ? ActionTypes.Run
            : ActionTypes.OpenFile;
        return new JsonObject { ["type"] = type, ["path"] = ToDeckPath(path, vars) };
    }

    /// <summary>deck.json に書くパス：「/」区切り。ページの root 配下なら ${root}/... にする</summary>
    private static string ToDeckPath(string path, IReadOnlyDictionary<string, string> vars)
    {
        var p = path.Replace('\\', '/');
        if (vars.TryGetValue("root", out var root))
        {
            var r = PathExpander.Expand(root).Replace('\\', '/').TrimEnd('/');
            if (r.Length > 0 && p.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase))
                return "${root}" + p[r.Length..];
        }
        return p;
    }

    // ================= ツールバーのボタン =================

    private void ToggleTopmost()
    {
        var s = _settings.Clone();
        s.Window.Topmost = !TopMost;
        SaveSettings(s);
    }

    private void ToggleDial()
    {
        var s = _settings.Clone();
        s.Dial.Visible = !s.Dial.Visible;
        SaveSettings(s);
    }

    private void ShowLampMessage()
    {
        var lamp = Lamp;
        MessageBox.Show(this, LampMessage(), "ProjectDeck - 読込みの状態", MessageBoxButtons.OK,
            lamp == LampState.Error ? MessageBoxIcon.Error : lamp == LampState.Warning ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    private void OpenSettings()
    {
        if (_settingsForm != null)
        {
            _settingsForm.Activate();
            return;
        }
        ShowLauncher();
        using var form = new SettingsForm(_settings.Clone(), _settingsStore.SettingsPath, OpenText);
        _settingsForm = form;
        try
        {
            if (form.ShowDialog(this) == DialogResult.OK) SaveSettings(form.Result);
        }
        finally
        {
            _settingsForm = null;
        }
    }

    private void ApplyWindowSettings()
    {
        TopMost = _settings.Window.Topmost;
        Opacity = _settings.Window.Opacity;
    }

    // ================= 表示位置（仕様書 5.4） =================

    /// <summary>記憶した位置があればそこ、無ければ既定位置（作業領域＝タスクバーを除いた範囲が基準）</summary>
    private void ApplyPosition()
    {
        if (_state is { X: { } x, Y: { } y } && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(x, y, Width, Height))))
        {
            Location = new Point(x, y);
            return;
        }
        var screen = TargetScreen();
        var area = screen.WorkingArea;
        var m = Px(_settings.Window.Margin);
        Location = _settings.Window.Position switch
        {
            "top_left" => new Point(area.Left + m, area.Top + m),
            "top_right" => new Point(area.Right - Width - m, area.Top + m),
            _ => new Point(area.Right - Width - m, area.Bottom - Height - m),
        };
    }

    private Screen TargetScreen()
    {
        if (int.TryParse(_settings.Window.Monitor, out var n) && n >= 1 && n <= Screen.AllScreens.Length)
            return Screen.AllScreens[n - 1];
        return Screen.PrimaryScreen ?? Screen.AllScreens[0];
    }

    private void ResetPosition()
    {
        _state.X = _state.Y = null;
        _state.Save();
        ApplyPosition();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // 既定位置のときは大きさが変わっても画面端（タスクバーの上）に揃える
        if (_state != null && _state.X == null && IsHandleCreated) ApplyPosition();
    }

    // ================= 表示/非表示・ホットキー =================

    public void ShowLauncher()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        NativeMethods.SetForegroundWindow(Handle);
    }

    private void ToggleLauncher()
    {
        if (Visible && ActiveForm == this) Hide();
        else ShowLauncher();
    }

    private void RegisterHotkeys()
    {
        if (!IsHandleCreated) return;
        _miscWarnings.RemoveAll(w => w.StartsWith("ホットキー", StringComparison.Ordinal));
        Register(HotkeyToggle, _settings.Hotkey.Toggle, "表示/非表示");
        Invalidate(LampRect);

        void Register(int id, string text, string name)
        {
            NativeMethods.UnregisterHotKey(Handle, id);
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!Hotkey.TryParse(text, out var mods, out var key))
            {
                _miscWarnings.Add($"ホットキー（{name}）\"{text}\" の書き方が正しくありません");
                Logger.Warn(_miscWarnings[^1]);
                return;
            }
            if (!NativeMethods.RegisterHotKey(Handle, id, mods | NativeMethods.MOD_NOREPEAT, (uint)key))
            {
                _miscWarnings.Add($"ホットキー（{name}）{text} を登録できません（他のアプリが使用中の可能性）");
                Logger.Warn(_miscWarnings[^1]);
            }
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            if ((int)m.WParam == HotkeyToggle) ToggleLauncher();
            return;
        }
        if (m.Msg == NativeMethods.WM_EXITSIZEMOVE)
        {
            // ドラッグで動かした位置を記憶
            _state.X = Left;
            _state.Y = Top;
            _state.Save();
        }
        base.WndProc(ref m);
    }

    // ================= 終了 =================

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 などでは閉じずに隠す（常駐）。終了はメニューから
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tray.Visible = false;
        _tray.Dispose();
        _deckWatcher?.Dispose();
        _settingsWatcher?.Dispose();
        _shell.Dispose();
        Logger.Info("終了");
        base.OnFormClosed(e);
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    // ================= 補助 =================

    /// <summary>テキストファイルを開く（関連付けが無ければメモ帳）</summary>
    private void OpenText(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Notify("開く", $"ファイルがありません: {path}", ToolTipIcon.Warning);
                return;
            }
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            StartShell("notepad.exe", $"\"{path}\"");
        }
    }

    private void StartShell(string file, string args)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            Notify("起動", ex.Message, ToolTipIcon.Warning);
        }
    }
}
