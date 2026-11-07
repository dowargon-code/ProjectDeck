using System.Text.Json;
using ProjectDeck.Core;

namespace ProjectDeck.Services;

/// <summary>
/// 設定ではない「状態」（ドラッグで動かした位置・最後に開いていたページ）。state.json に保存する。
/// </summary>
internal sealed class LauncherState
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public string? Page { get; set; }

    public static LauncherState Load()
    {
        try
        {
            if (File.Exists(AppPaths.StateFile))
                return JsonSerializer.Deserialize<LauncherState>(File.ReadAllText(AppPaths.StateFile)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Logger.Warn("state.json を読めません: " + ex.Message);
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(AppPaths.StateFile, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn("state.json を保存できません: " + ex.Message);
        }
    }
}
