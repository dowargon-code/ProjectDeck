namespace ProjectDeck.Core.Settings;

// settings.toml（ユーザー設定）の内容（仕様書 6.1）。
// 既定値は config\settings.toml と同じにしておく。

public sealed class AppSettings
{
    public LayoutSettings Layout { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    public StartupSettings Startup { get; set; } = new();
    public DialSettings Dial { get; set; } = new();
    public LabelSettings Label { get; set; } = new();
    public HotkeySettings Hotkey { get; set; } = new();
    public InterfaceSettings Interface { get; set; } = new();
    public ImageSettings Image { get; set; } = new();

    /// <summary>複製（設定ダイアログでの編集用）</summary>
    public AppSettings Clone() => new()
    {
        Layout = Layout with { },
        Window = Window with { },
        Startup = Startup with { },
        Dial = Dial with { },
        Label = Label with { },
        Hotkey = Hotkey with { },
        Interface = Interface with { },
        Image = Image with { },
    };

    /// <summary>deck.json の実際のパス（環境変数を展開済み）</summary>
    public string DeckPath => PathExpander.Expand(Interface.Path);
}

public sealed record LayoutSettings
{
    public int Rows { get; set; } = 3;
    public int Cols { get; set; } = 5;
    public int ButtonSize { get; set; } = 64;
    public int Gap { get; set; } = 4;
    public int CornerRadius { get; set; } = 8;
}

public sealed record WindowSettings
{
    /// <summary>top_left / top_right / bottom_right</summary>
    public string Position { get; set; } = "bottom_right";

    /// <summary>primary または 1,2,...</summary>
    public string Monitor { get; set; } = "primary";

    public int Margin { get; set; } = 8;
    public bool Topmost { get; set; } = true;
    public double Opacity { get; set; } = 1.0;
    public bool HideAfterLaunch { get; set; }
}

public sealed record StartupSettings
{
    public bool AutoStart { get; set; } = true;
    public bool ShowOnStart { get; set; } = true;
    public int DelaySec { get; set; }
}

public sealed record DialSettings
{
    public bool Visible { get; set; } = true;
}

public sealed record LabelSettings
{
    public string Font { get; set; } = "Meiryo UI";
    public double Size { get; set; } = 9;

    /// <summary>auto / white / black / #RRGGBB</summary>
    public string Color { get; set; } = "auto";
}

public sealed record HotkeySettings
{
    /// <summary>表示/非表示（Windows 全体で有効）</summary>
    public string Toggle { get; set; } = "Ctrl+Alt+Space";
}

public sealed record InterfaceSettings
{
    public string Path { get; set; } = @"%USERPROFILE%\ProjectDeck\deck.json";
    public int DebounceMs { get; set; } = 300;
    public int History { get; set; } = 30;
}

public sealed record ImageSettings
{
    public int DownloadTimeoutSec { get; set; } = 10;
    public int MaxSizeMb { get; set; } = 5;

    /// <summary>system / none / "http://proxy:8080"</summary>
    public string Proxy { get; set; } = "system";
}

/// <summary>各項目の入力範囲（読込時の補正と設定ダイアログで共通）</summary>
public static class SettingsLimits
{
    public static readonly (int Min, int Max) Rows = (1, 10);
    public static readonly (int Min, int Max) Cols = (1, 12);
    public static readonly (int Min, int Max) ButtonSize = (32, 160);
    public static readonly (int Min, int Max) Gap = (0, 20);
    public static readonly (int Min, int Max) CornerRadius = (0, 32);
    public static readonly (int Min, int Max) Margin = (0, 200);
    public static readonly (double Min, double Max) Opacity = (0.3, 1.0);
    public static readonly (int Min, int Max) DelaySec = (0, 300);
    public static readonly (double Min, double Max) LabelSize = (6, 20);
    public static readonly (int Min, int Max) DebounceMs = (50, 5000);
    public static readonly (int Min, int Max) History = (1, 1000);
    public static readonly (int Min, int Max) DownloadTimeoutSec = (1, 120);
    public static readonly (int Min, int Max) MaxSizeMb = (1, 50);

    public static readonly string[] Positions = ["top_left", "top_right", "bottom_right"];
}
