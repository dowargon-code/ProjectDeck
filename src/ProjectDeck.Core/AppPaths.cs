namespace ProjectDeck.Core;

/// <summary>
/// ProjectDeck が使うフォルダ・ファイルの場所をまとめたもの。
/// 本体（ProjectDeck.exe）と deckctl.exe の両方から同じ場所を参照する。
/// </summary>
public static class AppPaths
{
    /// <summary>exe が置かれているフォルダ（配布時は dist\ProjectDeck\、開発時は bin\...）</summary>
    public static string AppDirectory => AppContext.BaseDirectory;

    /// <summary>
    /// 利用者ごとのデータフォルダ（既定：%USERPROFILE%\ProjectDeck）。
    /// 環境変数 PROJECTDECK_HOME で変更できる（テスト用）。
    /// settings.toml・history・cache・logs・inbox をここに置く（deck.json の場所は settings.toml で指定）。
    /// </summary>
    public static string DataDirectory =>
        Environment.GetEnvironmentVariable("PROJECTDECK_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ProjectDeck");

    /// <summary>ユーザー設定ファイル</summary>
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.toml");

    /// <summary>deck.json の変更履歴</summary>
    public static string HistoryDirectory => Path.Combine(DataDirectory, "history");

    /// <summary>Grok が作るパッチの置き場所（適用後は inbox\done\ へ移す）</summary>
    public static string InboxDirectory => Path.Combine(DataDirectory, "inbox");

    /// <summary>動作ログ</summary>
    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>画像キャッシュ（フェーズ2）</summary>
    public static string ImageCacheDirectory => Path.Combine(DataDirectory, "cache", "images");

    /// <summary>ウィンドウ位置などの状態（設定ではないので settings.toml とは分ける）</summary>
    public static string StateFile => Path.Combine(DataDirectory, "state.json");

    /// <summary>
    /// 配布物に同梱した設定ファイルの原本（config\settings.toml を build.ps1 が exe の隣にコピーしたもの）。
    /// データフォルダに settings.toml が無いとき、ここからコピーして使い始める。
    /// </summary>
    public static string BundledSettingsFile => Path.Combine(AppDirectory, "settings.toml");
}
