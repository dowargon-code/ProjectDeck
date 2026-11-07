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
    /// deck.json・settings.toml・history・cache・logs・inbox をここに置く。
    /// </summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ProjectDeck");

    /// <summary>ユーザー設定ファイル</summary>
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.toml");

    /// <summary>
    /// 配布物に同梱した設定ファイルの原本（config\settings.toml を build.ps1 が exe の隣にコピーしたもの）。
    /// データフォルダに settings.toml が無いとき、ここからコピーして使い始める。
    /// </summary>
    public static string BundledSettingsFile => Path.Combine(AppDirectory, "settings.toml");
}
