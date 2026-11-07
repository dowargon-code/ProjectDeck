using Microsoft.Win32;
using ProjectDeck.Core;

namespace ProjectDeck.Services;

/// <summary>
/// PC起動（ログオン）時の自動実行（仕様書 5.6）。
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run に登録／解除する（管理者権限は不要）。
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ProjectDeck";

    /// <summary>自動実行で起動されたことを示す引数（起動遅延に使う）</summary>
    public const string Argument = "--autostart";

    /// <summary>
    /// 設定に合わせて登録・解除する。exe の場所が変わっていれば登録を更新する。
    /// 開発中のビルド（bin\ フォルダ）は登録しない。戻り値は警告（無ければ null）
    /// </summary>
    public static string? Sync(bool enabled)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return null;
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            var current = key.GetValue(ValueName) as string;

            if (!enabled)
            {
                if (current != null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                    Logger.Info("自動実行を解除しました");
                }
                return null;
            }

            if (exe.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
                return "開発中のビルド（bin フォルダ）のため、PC起動時の自動実行は登録しません";

            var command = $"\"{exe}\" {Argument}";
            if (!string.Equals(current, command, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, command);
                Logger.Info($"自動実行を登録しました: {command}");
            }
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Logger.Warn("自動実行の登録に失敗: " + ex.Message);
            return "PC起動時の自動実行を登録できませんでした: " + ex.Message;
        }
    }
}
