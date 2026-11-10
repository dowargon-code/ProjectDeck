using System.Diagnostics;
using System.Management;
using NAudio.CoreAudioApi;
using ProjectDeck.Core;
using ProjectDeck.Core.Deck;

namespace ProjectDeck.Services;

/// <summary>
/// ダイヤル1つ分の操作対象（音量・マイク・アプリ別音量・明るさ）。仕様書 8章。
/// 値は 0～100（%）。Changed はどのスレッドからも呼ばれうるので、受け取り側で Invoke すること。
/// </summary>
internal interface IDialControl : IDisposable
{
    /// <summary>使えない理由（使えるときは null）</summary>
    string? Unavailable { get; }

    /// <summary>今の値 0～100。読めないときは null</summary>
    int? Value { get; }

    bool Muted { get; }
    bool CanMute { get; }

    /// <summary>値を増減する（%）</summary>
    void Change(int deltaPercent);

    void ToggleMute();

    /// <summary>今の値を読み直す（通知が来ないもの用。定期的に呼ばれる）</summary>
    void Refresh();

    event Action? Changed;
}

/// <summary>既定の出力（スピーカー）・入力（マイク）機器の音量。Windows Core Audio API</summary>
internal sealed class EndpointVolumeDial : IDialControl
{
    private readonly MMDevice? _device;
    private readonly AudioEndpointVolume? _volume;
    private readonly string? _unavailable;

    public event Action? Changed;

    public EndpointVolumeDial(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try
        {
            if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
            {
                _unavailable = flow == DataFlow.Render ? "出力機器（スピーカー）がありません" : "入力機器（マイク）がありません";
                return;
            }
            _device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            _volume = _device.AudioEndpointVolume;
            // 他のアプリ・キーボードで音量が変わったときも表示を追従させる
            _volume.OnVolumeNotification += OnNotify;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _unavailable = "音量を取得できません: " + ex.Message;
            Logger.Warn(_unavailable);
        }
    }

    private void OnNotify(AudioVolumeNotificationData data) => Changed?.Invoke();

    public string? Unavailable => _unavailable;
    public int? Value => _volume == null ? null : (int)Math.Round(_volume.MasterVolumeLevelScalar * 100);
    public bool Muted => _volume?.Mute ?? false;
    public bool CanMute => _volume != null;

    public void Change(int deltaPercent)
    {
        if (_volume == null) return;
        var next = Math.Clamp((Value ?? 0) + deltaPercent, 0, 100);
        _volume.MasterVolumeLevelScalar = next / 100f;
        if (deltaPercent > 0 && _volume.Mute) _volume.Mute = false; // 上げたらミュート解除（キーボードの音量キーと同じ）
        Changed?.Invoke();
    }

    public void ToggleMute()
    {
        if (_volume == null) return;
        _volume.Mute = !_volume.Mute;
        Changed?.Invoke();
    }

    public void Refresh() { } // 通知で追従するので不要

    public void Dispose()
    {
        if (_volume != null) _volume.OnVolumeNotification -= OnNotify;
        _volume?.Dispose();
        _device?.Dispose();
    }
}

/// <summary>
/// アプリ別の音量（音量ミキサーのアプリごとの値）。process はプロセス名（例："spotify"、".exe" は付けても付けなくてもよい）。
/// アプリが音を出していないときは「未起動」として扱う。
/// </summary>
internal sealed class AppVolumeDial(MMDeviceEnumerator enumerator, string process) : IDialControl
{
    private readonly string _process = Path.GetFileNameWithoutExtension(process.Trim());
    private int? _value;
    private bool _muted;
    private string? _unavailable = "読込中";

    public event Action? Changed;

    public string? Unavailable => _unavailable;
    public int? Value => _value;
    public bool Muted => _muted;
    public bool CanMute => _unavailable == null;

    /// <summary>対象アプリの音声セッションに対して処理する。見つからなければ false</summary>
    private bool WithSessions(Action<SimpleAudioVolume> action)
    {
        try
        {
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return false;
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            var found = false;
            for (var i = 0; i < sessions.Count; i++)
            {
                // セッションは COM オブジェクトなので毎回解放する（2秒ごとに呼ばれる）
                using var s = sessions[i];
                try
                {
                    if (s.IsSystemSoundsSession) continue;
                    string name;
                    using (var proc = Process.GetProcessById((int)s.GetProcessID)) name = proc.ProcessName;
                    if (!string.Equals(name, _process, StringComparison.OrdinalIgnoreCase)) continue;
                    action(s.SimpleAudioVolume);
                    found = true;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    // 終了直後のプロセス・消えたセッション。ほかのセッションの確認は続ける
                }
            }
            return found;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Logger.Warn($"アプリ別音量（{_process}）を操作できません: {ex.Message}");
            return false;
        }
    }

    public void Refresh()
    {
        int? value = null;
        var muted = false;
        var found = WithSessions(v =>
        {
            value ??= (int)Math.Round(v.Volume * 100);
            muted = v.Mute;
        });
        var unavailable = found ? null : $"「{_process}」は音を出していません（起動していないか、まだ再生していない）";
        if (value != _value || muted != _muted || unavailable != _unavailable)
        {
            (_value, _muted, _unavailable) = (value, muted, unavailable);
            Changed?.Invoke();
        }
    }

    public void Change(int deltaPercent)
    {
        var next = Math.Clamp((_value ?? 0) + deltaPercent, 0, 100);
        WithSessions(v =>
        {
            v.Volume = next / 100f;
            if (deltaPercent > 0) v.Mute = false;
        });
        Refresh();
    }

    public void ToggleMute()
    {
        var mute = !_muted;
        WithSessions(v => v.Mute = mute);
        Refresh();
    }

    public void Dispose() { }
}

/// <summary>
/// 画面の明るさ（WMI の WmiMonitorBrightness）。ノートPCの内蔵画面だけが対象で、外部モニタは対象外（仕様書 8章）。
/// WMI は遅いことがあるので、読み書きは別スレッドで行い、ホイールの連続操作は最後の値だけを書く。
/// </summary>
internal sealed class BrightnessDial : IDialControl
{
    private readonly object _gate = new();
    private int? _value;
    private int? _pendingWrite;
    private bool _writing;
    private bool _unsupported; // 一度「非対応」と分かったら、それ以降は問い合わせない（デスクトップPCなど）
    private string? _unavailable = "読込中";

    public event Action? Changed;

    public string? Unavailable => _unavailable;
    public int? Value => _value;
    public bool Muted => false;
    public bool CanMute => false; // 明るさにミュートは無い

    public BrightnessDial() => Refresh();

    public void Refresh()
    {
        if (_unsupported) return;
        Task.Run(() =>
        {
            int? value = null;
            string? unavailable;
            try
            {
                using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\wmi"), new SelectQuery("WmiMonitorBrightness"));
                foreach (ManagementObject o in searcher.Get())
                {
                    using (o) value ??= Convert.ToInt32(o["CurrentBrightness"]);
                }
                unavailable = value == null ? "この PC の画面は明るさを変えられません（外部モニタは対象外）" : null;
                if (value == null) _unsupported = true;
            }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                unavailable = "この PC の画面は明るさを変えられません（外部モニタは対象外）";
                if (!_unsupported) Logger.Info("明るさを取得できません（以降は問い合わせません）: " + ex.Message);
                _unsupported = true;
            }
            lock (_gate)
            {
                if (_writing || _pendingWrite != null) return; // 書き込み中は表示を巻き戻さない
                if (value == _value && unavailable == _unavailable) return;
                (_value, _unavailable) = (value, unavailable);
            }
            Changed?.Invoke();
        });
    }

    public void Change(int deltaPercent)
    {
        lock (_gate)
        {
            if (_value == null) return;
            _value = Math.Clamp(_value.Value + deltaPercent, 0, 100);
            _pendingWrite = _value;
            if (_writing) { Changed?.Invoke(); return; }
            _writing = true;
        }
        Changed?.Invoke();
        Task.Run(WriteLoop);
    }

    /// <summary>まとめて書く（書いている間に来た操作は、最後の値だけ書く）</summary>
    private void WriteLoop()
    {
        while (true)
        {
            int target;
            lock (_gate)
            {
                if (_pendingWrite == null)
                {
                    _writing = false;
                    return;
                }
                target = _pendingWrite.Value;
                _pendingWrite = null;
            }
            try
            {
                using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\wmi"), new SelectQuery("WmiMonitorBrightnessMethods"));
                foreach (ManagementObject o in searcher.Get())
                {
                    using (o) o.InvokeMethod("WmiSetBrightness", [(uint)1, (byte)target]);
                }
            }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                Logger.Warn("明るさを変更できません: " + ex.Message);
            }
        }
    }

    public void ToggleMute() { }
    public void Dispose() { }
}

/// <summary>操作できないダイヤル（function: none、または音声機能が使えないとき）。理由を表示するだけ</summary>
internal sealed class NoneDial(string reason = "割り当てなし") : IDialControl
{
    public string? Unavailable => reason;
    public int? Value => null;
    public bool Muted => false;
    public bool CanMute => false;
    public void Change(int deltaPercent) { }
    public void ToggleMute() { }
    public void Refresh() { }
    public void Dispose() { }
    public event Action? Changed { add { } remove { } }
}

/// <summary>
/// deck.json の dials からダイヤルを作り、既定の音声機器が変わったら作り直す。
/// UI スレッドで作ること（機器の変更通知を UI スレッドで受けるため）。
/// </summary>
internal sealed class DialManager : IDisposable
{
    private readonly MMDeviceEnumerator? _enumerator;
    private readonly MMDeviceNotificationClient? _notify;
    private readonly string? _audioUnavailable;
    private IReadOnlyList<DeckDial> _defs = [];
    private List<(DeckDial Def, IDialControl Control)> _dials = [];

    /// <summary>値・状態が変わった（どのスレッドからも呼ばれる）</summary>
    public event Action? Changed;

    public DialManager()
    {
        // 音声サービス（Windows Audio）が止まっているなどで Core Audio が使えなくても、ランチャー自体は起動する
        try
        {
            _enumerator = new MMDeviceEnumerator();
            // 既定の機器が切り替わった（ヘッドセットを挿したなど）ら作り直す。UI スレッドに戻して通知される
            _notify = _enumerator.CreateNotificationClient(true);
            _notify.DefaultDeviceChanged += (_, _) => Rebuild(_defs, force: true);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException)
        {
            _audioUnavailable = "音声機能を使えません（Windows Audio サービスが動いていない可能性）: " + ex.Message;
            Logger.Warn(_audioUnavailable);
            _notify?.Dispose();
            _enumerator?.Dispose();
            _enumerator = null;
            _notify = null;
        }
    }

    /// <summary>音声機能が使えないときの理由（使えるときは null）。ランプの警告に使う</summary>
    public string? AudioUnavailable => _audioUnavailable;

    public IReadOnlyList<(DeckDial Def, IDialControl Control)> Dials => _dials;

    /// <summary>通知が来ないもの（アプリ別音量・明るさ）があるか。あれば定期的に Refresh する</summary>
    public bool NeedsPolling => _dials.Any(d => d.Control is AppVolumeDial or BrightnessDial);

    public void Rebuild(IReadOnlyList<DeckDial> defs, bool force = false)
    {
        var ordered = defs.OrderBy(d => d.Index).Take(4).ToList();
        if (!force && ordered.SequenceEqual(_defs)) return;
        foreach (var (_, c) in _dials) c.Dispose();
        _defs = ordered;
        _dials = ordered.Select(d => (d, Create(d))).ToList();
        foreach (var (_, c) in _dials) c.Changed += OnChanged;
        RefreshPolled();
        OnChanged();
    }

    private IDialControl Create(DeckDial d) => d.Function switch
    {
        "brightness" => new BrightnessDial(),
        "none" => new NoneDial(),
        _ when _enumerator == null => new NoneDial("音声機能を使えません"),
        "master_volume" => new EndpointVolumeDial(_enumerator!, DataFlow.Render),
        "mic_volume" => new EndpointVolumeDial(_enumerator!, DataFlow.Capture),
        "app_volume" => new AppVolumeDial(_enumerator!, d.Process ?? ""),
        _ => new NoneDial(),
    };

    public void RefreshPolled()
    {
        foreach (var (_, c) in _dials)
            if (c is AppVolumeDial or BrightnessDial) c.Refresh();
    }

    private void OnChanged() => Changed?.Invoke();

    public void Dispose()
    {
        foreach (var (_, c) in _dials) c.Dispose();
        _notify?.Dispose();
        _enumerator?.Dispose();
    }
}
