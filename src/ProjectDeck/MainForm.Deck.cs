using System.Text.Json.Nodes;
using ProjectDeck.Core;
using ProjectDeck.Core.Deck;
using ProjectDeck.Core.Settings;
using ProjectDeck.Services;
using Timer = System.Windows.Forms.Timer;

namespace ProjectDeck;

// deck.json / settings.toml の監視・読込・書込と、読込みランプ（仕様書 5.3・7章）
internal sealed partial class MainForm
{
    private DeckStore _deckStore = null!;
    private DeckData? _deck;
    private string? _deckText;   // 最後に反映した内容
    private string? _deckHash;   // 同上のハッシュ（自分で書いた内容を二重に読まないため）
    private string? _settingsHash;

    private FileSystemWatcher? _deckWatcher;
    private FileSystemWatcher? _settingsWatcher;
    private readonly Timer _deckDebounce = new();
    private readonly Timer _settingsDebounce = new();
    private readonly Timer _blinkTimer = new() { Interval = 400 };
    private readonly Timer _doneTimer = new() { Interval = 3000 };
    private bool _blinkOn = true;
    private bool _loading;
    private bool _justDone;

    // ランプに出すメッセージ（種類ごと）
    private readonly List<string> _deckErrors = [];
    private readonly List<string> _deckWarnings = [];
    private readonly List<string> _settingsErrors = [];
    private readonly List<string> _settingsWarnings = [];
    private readonly List<string> _miscWarnings = [];   // ホットキー・自動実行
    private readonly List<string> _imageWarnings = [];  // 画像の読込失敗

    private void InitDeck()
    {
        _deckStore = CreateDeckStore();
        _settingsHash = _settingsStore.ReadText() is { } t ? DeckStore.Hash(t) : null;
        LoadDeckInitial();
        StartWatchers();
    }

    private DeckStore CreateDeckStore() =>
        new(_settings.DeckPath, AppPaths.HistoryDirectory, _settings.Interface.History);

    private void InitTimers()
    {
        _deckDebounce.Interval = _settings.Interface.DebounceMs;
        _deckDebounce.Tick += (_, _) => { _deckDebounce.Stop(); ReloadDeck(); };
        _settingsDebounce.Interval = _settings.Interface.DebounceMs;
        _settingsDebounce.Tick += (_, _) => { _settingsDebounce.Stop(); ReloadSettings(); };
        _blinkTimer.Tick += (_, _) => { _blinkOn = !_blinkOn; Invalidate(LampRect); };
        _doneTimer.Tick += (_, _) => { _doneTimer.Stop(); _justDone = false; Invalidate(LampRect); };
    }

    // ================= 監視 =================

    private void StartWatchers()
    {
        _deckWatcher?.Dispose();
        _settingsWatcher?.Dispose();
        _deckWatcher = CreateWatcher(_deckStore.DeckPath, () => RestartTimer(_deckDebounce));
        _settingsWatcher = CreateWatcher(_settingsStore.SettingsPath, () => RestartTimer(_settingsDebounce));
    }

    private FileSystemWatcher? CreateWatcher(string file, Action onChange)
    {
        try
        {
            var full = Path.GetFullPath(file);
            var dir = Path.GetDirectoryName(full)!;
            Directory.CreateDirectory(dir);
            var w = new FileSystemWatcher(dir, Path.GetFileName(full))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            // 一時ファイル → 置き換え（Rename）で書く道具も多いので Renamed も見る
            FileSystemEventHandler h = (_, _) => BeginInvokeSafe(onChange);
            w.Changed += h;
            w.Created += h;
            w.Renamed += (_, _) => BeginInvokeSafe(onChange);
            w.EnableRaisingEvents = true;
            return w;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Logger.Warn($"監視を開始できません {file}: {ex.Message}");
            _miscWarnings.Add($"{file} を監視できません: {ex.Message}");
            return null;
        }
    }

    private void BeginInvokeSafe(Action a)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(a);
    }

    private void RestartTimer(Timer t)
    {
        // 変更を検知したらランプを黄にして、debounce_ms 待ってから読む（書きかけ対策）
        SetLoading(true);
        t.Stop();
        t.Start();
    }

    // ================= deck.json の読込 =================

    /// <summary>
    /// 起動時の読込。deck.json が壊れていても起動できるよう、最後の正常な履歴か空の状態で表示する（仕様書 10章）
    /// </summary>
    private void LoadDeckInitial()
    {
        var text = SafeRead();
        if (text == null)
        {
            _deck = DeckValidator.Validate(DeckJson.EmptyDeck()).Deck;
            return;
        }
        var v = DeckValidator.Validate(text);
        if (v.Ok)
        {
            AcceptDeck(text, v, fromApp: true, quiet: true);
            return;
        }

        _deckErrors.AddRange(v.Errors);
        _deckHash = DeckStore.Hash(text);
        foreach (var file in _deckStore.HistoryFiles())
        {
            try
            {
                var hv = DeckValidator.Validate(File.ReadAllText(file));
                if (!hv.Ok) continue;
                _deck = hv.Deck;
                _deckText = DeckJson.Serialize(hv.Root!);
                _deckWarnings.Add($"deck.json が正しくないため、履歴 {Path.GetFileName(file)} の内容で表示しています");
                break;
            }
            catch (IOException) { }
        }
        _deck ??= DeckValidator.Validate(DeckJson.EmptyDeck()).Deck;
        Logger.Error("起動時の deck.json が正しくありません: " + string.Join(" / ", v.Errors));
    }

    private string? SafeRead()
    {
        try
        {
            return _deckStore.ReadText();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _deckErrors.Add("deck.json を読めません: " + ex.Message);
            return null;
        }
    }

    /// <summary>変更を検知した deck.json を読み直す（読込・検証は別スレッド）</summary>
    private void ReloadDeck()
    {
        var store = _deckStore;
        Task.Run(() =>
        {
            try
            {
                var text = store.ReadText();
                return (text, text == null ? null : DeckValidator.Validate(text), (string?)null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ((string?)null, (ValidationResult?)null, ex.Message);
            }
        }).ContinueWith(t =>
        {
            if (IsDisposed || store != _deckStore) return;
            var (text, v, error) = t.Result;
            if (error != null)
            {
                SetDeckErrors(["deck.json を読めません: " + error]);
            }
            else if (text == null)
            {
                // 削除された：表示はそのまま（次に書かれたら読む）
                SetLoading(false);
            }
            else if (DeckStore.Hash(text) == _deckHash)
            {
                SetLoading(false); // 自分で書いた内容・内容に変化なし
            }
            else if (!v!.Ok)
            {
                _deckHash = null;
                SetDeckErrors(v.Errors);
                Logger.Warn("deck.json の検証エラー（反映しません）: " + string.Join(" / ", v.Errors));
            }
            else
            {
                AcceptDeck(text, v, fromApp: false);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>検証OKの deck.json を画面に反映する</summary>
    private void AcceptDeck(string text, ValidationResult v, bool fromApp, bool quiet = false)
    {
        var newDeck = v.Deck!;

        // 大量削除の保護：Grok が既存ボタンを書き漏らしたときの保険（自分の書き込みは確認済み）
        if (!fromApp && _deck != null && _deckText != null)
        {
            var removed = _deck.ButtonCount - newDeck.ButtonCount;
            if (removed >= DeckStore.MassDeleteThreshold && !ConfirmMassDelete(removed, newDeck.UpdatedBy))
            {
                RevertDeck(text);
                return;
            }
        }

        // 変更前の状態を履歴に保存（deckctl が保存済みなら重複しない。元に戻した結果は保存しない）
        if (_deckText != null && _deckText != text && newDeck.UpdatedBy != "undo")
        {
            try { _deckStore.SaveHistory(_deckText); }
            catch (IOException ex) { Logger.Warn("履歴を保存できません: " + ex.Message); }
        }

        var oldDeck = _deck;
        _deck = newDeck;
        _deckText = text;
        _deckHash = DeckStore.Hash(text);
        _deckErrors.Clear();
        _deckWarnings.Clear();
        _deckWarnings.AddRange(v.Warnings);
        _deckWarnings.AddRange(DeckValidator.GridWarnings(newDeck, Rows, Cols));
        _imageWarnings.Clear();
        foreach (var img in _pictureCache.Values) img?.Dispose();
        _pictureCache.Clear();
        _shell.RecheckAll();

        if (CurrentPage == null)
        {
            _pageId = VisiblePages.FirstOrDefault()?.Id;
        }
        TrimRenderCache();
        if (!quiet)
        {
            Logger.Info($"deck.json を反映 rev={newDeck.Revision} by={newDeck.UpdatedBy} {Summarize(oldDeck, newDeck)}");
            FlashDone();
        }
        SetLoading(false);
        Invalidate();
    }

    private static string Summarize(DeckData? before, DeckData after)
    {
        if (before == null) return "";
        var old = before.Pages.SelectMany(p => p.Buttons).ToDictionary(b => b.Id, b => b.Json);
        var now = after.Pages.SelectMany(p => p.Buttons).ToDictionary(b => b.Id, b => b.Json);
        var added = now.Keys.Count(k => !old.ContainsKey(k));
        var deleted = old.Keys.Count(k => !now.ContainsKey(k));
        var changed = now.Count(kv => old.TryGetValue(kv.Key, out var j) && j != kv.Value);
        return $"+{added} ~{changed} -{deleted}";
    }

    private bool ConfirmMassDelete(int removed, string? by)
    {
        ShowLauncher();
        var r = MessageBox.Show(this,
            $"deck.json の変更で、ボタンが {removed} 個減ります（更新者: {by ?? "不明"}）。\n\n" +
            "反映しますか？\n\n［はい］反映する\n［いいえ］deck.json を直前の状態に戻す（今回の内容は履歴に残します）",
            "ProjectDeck - 大量削除の確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        return r == DialogResult.Yes;
    }

    /// <summary>deck.json を直前に反映した内容に書き戻す（今回の内容は履歴に残す）</summary>
    private void RevertDeck(string rejectedText)
    {
        try
        {
            using (_deckStore.AcquireLock())
            {
                _deckStore.SaveHistory(rejectedText);
                _deckStore.WriteAtomic(_deckText!);
            }
            _deckHash = DeckStore.Hash(_deckText!);
            Logger.Warn("大量削除の変更を取り消して直前の状態に戻しました");
            SetLoading(false);
        }
        catch (Exception ex) when (ex is IOException or DeckLockException or UnauthorizedAccessException)
        {
            SetDeckErrors(["直前の状態に戻せませんでした: " + ex.Message]);
        }
    }

    // ================= deck.json への書込（D&D登録・削除・取込・元に戻す） =================

    /// <summary>パッチを適用して書き込み、すぐ画面に反映する。大量削除なら確認する</summary>
    private bool ApplyAppPatch(JsonObject patch, string what)
    {
        var outcome = _deckStore.ApplyPatch(patch);
        if (outcome.NeedsForce)
        {
            if (MessageBox.Show(this, outcome.Errors.Last().Replace("--force を付けて実行してください", "反映しますか？"),
                    "ProjectDeck", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return false;
            outcome = _deckStore.ApplyPatch(patch, force: true);
        }
        return FinishWrite(outcome, what);
    }

    private bool FinishWrite(ApplyOutcome outcome, string what)
    {
        if (!outcome.Ok)
        {
            Logger.Warn($"{what}に失敗: " + string.Join(" / ", outcome.Errors));
            MessageBox.Show(this, $"{what}できませんでした。\n\n" + string.Join("\n", outcome.Errors.Take(10)),
                "ProjectDeck", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        Logger.Info($"{what}: +{outcome.Added.Count} ~{outcome.Changed.Count} -{outcome.Deleted.Count}");
        if (outcome.NewText != null)
            AcceptDeck(outcome.NewText, DeckValidator.Validate(outcome.NewText), fromApp: true);
        return true;
    }

    private void UndoDeck()
    {
        var outcome = _deckStore.Undo();
        if (FinishWrite(outcome, "元に戻す"))
            Notify("元に戻しました", outcome.Warnings.FirstOrDefault() ?? "");
    }

    /// <summary>クリップボードから取込（"mode": "merge" は部分更新、それ以外は全体の置き換え）</summary>
    private void ImportClipboard()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (System.Runtime.InteropServices.ExternalException) { text = ""; }
        var json = DeckJson.ExtractJson(text);
        if (json == null)
        {
            MessageBox.Show(this, "クリップボードに JSON がありません。", "ProjectDeck - 取込", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var node = DeckJson.TryParse(json, out var error);
        if (node is not JsonObject obj)
        {
            MessageBox.Show(this, "JSON を読めません。\n\n" + (error ?? "最上位が { } ではありません"), "ProjectDeck - 取込",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ApplyOutcome outcome;
        if (DeckPatcher.IsPatch(obj))
        {
            obj["updated_by"] ??= "clipboard";
            outcome = _deckStore.ApplyPatch(obj);
            if (outcome.NeedsForce && ConfirmForce(outcome)) outcome = _deckStore.ApplyPatch(obj, force: true);
        }
        else
        {
            if (MessageBox.Show(this, "部分更新（\"mode\": \"merge\"）ではない JSON です。\ndeck.json 全体をこの内容で置き換えますか？",
                    "ProjectDeck - 取込", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            outcome = _deckStore.ReplaceAll(json, "clipboard");
            if (outcome.NeedsForce && ConfirmForce(outcome)) outcome = _deckStore.ReplaceAll(json, "clipboard", force: true);
        }
        if (FinishWrite(outcome, "クリップボードから取込"))
        {
            var summary = $"追加 {outcome.Added.Count}・変更 {outcome.Changed.Count}・削除 {outcome.Deleted.Count}";
            if (outcome.PagesAdded.Count > 0) summary += $"・新しいページ {string.Join(", ", outcome.PagesAdded)}";
            Notify("取り込みました", summary);
        }
    }

    private bool ConfirmForce(ApplyOutcome o) =>
        MessageBox.Show(this, o.Errors.Last().Replace("--force を付けて実行してください", "反映しますか？"), "ProjectDeck",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    // ================= settings.toml =================

    private void ReloadSettings()
    {
        var text = _settingsStore.ReadText();
        if (text == null || DeckStore.Hash(text) == _settingsHash)
        {
            SetLoading(false);
            return;
        }
        _settingsHash = DeckStore.Hash(text);
        var r = SettingsStore.Parse(text);
        if (!r.Ok)
        {
            _settingsErrors.Clear();
            _settingsErrors.AddRange(r.Errors);
            Logger.Warn("settings.toml のエラー（反映しません）: " + string.Join(" / ", r.Errors));
            SetLoading(false);
            return;
        }
        ApplySettings(r.Settings!, r.Warnings);
        _settingsForm?.OnExternalChange(r.Settings!);
        FlashDone();
        SetLoading(false);
    }

    /// <summary>設定ダイアログの OK・📌・◎ などアプリ自身が設定を変えたとき</summary>
    private void SaveSettings(AppSettings s)
    {
        try
        {
            var text = _settingsStore.Save(s);
            _settingsHash = DeckStore.Hash(text);
            ApplySettings(s, SettingsStore.Parse(text).Warnings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "settings.toml を保存できませんでした。\n" + ex.Message, "ProjectDeck", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>設定を画面・動作に反映する</summary>
    private void ApplySettings(AppSettings s, IEnumerable<string> warnings)
    {
        var old = _settings;
        _settings = s;
        _settingsErrors.Clear();
        _settingsWarnings.Clear();
        _settingsWarnings.AddRange(warnings);

        if (old.Layout != s.Layout || old.Label != s.Label) ClearRenderCache();
        if (old.Hotkey != s.Hotkey) RegisterHotkeys();
        if (old.Startup.AutoStart != s.Startup.AutoStart) SyncAutoStart();
        _deckDebounce.Interval = s.Interface.DebounceMs;
        _settingsDebounce.Interval = s.Interface.DebounceMs;

        if (!string.Equals(old.DeckPath, s.DeckPath, StringComparison.OrdinalIgnoreCase) || old.Interface.History != s.Interface.History)
        {
            _deckStore = CreateDeckStore();
            _deck = null;
            _deckText = null;
            _deckHash = null;
            _deckErrors.Clear();
            LoadDeckInitial();
            StartWatchers();
        }

        if (_deck != null)
        {
            _deckWarnings.RemoveAll(w => w.Contains("グリッド"));
            _deckWarnings.AddRange(DeckValidator.GridWarnings(_deck, Rows, Cols));
        }

        ApplyWindowSettings();
        UpdateLayoutSize();
        if (old.Window.Position != s.Window.Position || old.Window.Monitor != s.Window.Monitor || old.Window.Margin != s.Window.Margin)
        {
            _state.X = _state.Y = null;
            _state.Save();
        }
        ApplyPosition();
        Invalidate();
    }

    private void SyncAutoStart()
    {
        _miscWarnings.RemoveAll(w => w.Contains("自動実行"));
        if (AutoStart.Sync(_settings.Startup.AutoStart) is { } w) _miscWarnings.Add(w);
        Invalidate(LampRect);
    }

    // ================= ランプ =================

    private LampState Lamp
    {
        get
        {
            if (_deckErrors.Count > 0 || _settingsErrors.Count > 0) return LampState.Error;
            if (_loading) return LampState.Loading;
            if (_deckWarnings.Count + _settingsWarnings.Count + _miscWarnings.Count + _imageWarnings.Count > 0) return LampState.Warning;
            return _justDone ? LampState.Done : LampState.Idle;
        }
    }

    private Color LampColor() => Lamp switch
    {
        LampState.Loading => Color.FromArgb(0xff, 0xd4, 0x00),
        LampState.Done => Color.FromArgb(0x57, 0xab, 0x5a),
        LampState.Warning => Color.FromArgb(0xf0, 0x8c, 0x2e),
        LampState.Error => Color.FromArgb(0xe5, 0x53, 0x4b),
        _ => Color.FromArgb(0x5a, 0x60, 0x6b),
    };

    private void SetLoading(bool loading)
    {
        _loading = loading;
        if (loading)
        {
            _blinkOn = true;
            _blinkTimer.Start();
        }
        else
        {
            _blinkTimer.Stop();
        }
        Invalidate(LampRect);
    }

    private void FlashDone()
    {
        _justDone = true;
        _doneTimer.Stop();
        _doneTimer.Start();
    }

    private void SetDeckErrors(IEnumerable<string> errors)
    {
        _deckErrors.Clear();
        _deckErrors.AddRange(errors);
        SetLoading(false);
        Invalidate();
    }

    /// <summary>ランプの説明（クリック・ツールチップ用）</summary>
    private string LampMessage()
    {
        var parts = new List<string>();
        void Add(string title, List<string> items)
        {
            if (items.Count == 0) return;
            parts.Add($"■ {title}\n" + string.Join("\n", items.Take(15).Select(i => "・" + i)) + (items.Count > 15 ? $"\n…ほか {items.Count - 15} 件" : ""));
        }
        Add($"deck.json のエラー（反映していません）\n{_deckStore.DeckPath}", _deckErrors);
        Add($"settings.toml のエラー（反映していません）\n{_settingsStore.SettingsPath}", _settingsErrors);
        Add("deck.json の警告", _deckWarnings);
        Add("settings.toml の警告", _settingsWarnings);
        Add("画像", _imageWarnings);
        Add("その他", _miscWarnings);
        if (parts.Count == 0)
        {
            return Lamp switch
            {
                LampState.Loading => "読込中…",
                _ => $"正常（revision {_deck?.Revision}）\n{_deckStore.DeckPath}",
            };
        }
        return string.Join("\n\n", parts);
    }
}
