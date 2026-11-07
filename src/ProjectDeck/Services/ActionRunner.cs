using System.Diagnostics;
using ProjectDeck.Core;
using ProjectDeck.Core.Deck;

namespace ProjectDeck.Services;

/// <summary>
/// ボタンのアクションを実行する（仕様書 6.4）。
/// ファイルを開く処理は UI を止めないよう別スレッドで行う。
/// </summary>
internal static class ActionRunner
{
    /// <param name="action">実行するアクション</param>
    /// <param name="vars">ページの vars（${名前} の展開用）</param>
    /// <param name="switchPage">page アクション用（UI スレッドで呼ばれるよう呼び出し側で Invoke すること）</param>
    /// <param name="onError">失敗時の通知</param>
    public static Task RunAsync(DeckAction action, IReadOnlyDictionary<string, string> vars,
        Action<string> switchPage, Action<string> onError) =>
        Task.Run(async () =>
        {
            try
            {
                await RunOneAsync(action, vars, switchPage);
            }
            catch (Exception ex)
            {
                Logger.Warn($"実行失敗 {action.Type}: {ex.Message}");
                onError(ex.Message);
            }
        });

    private static async Task RunOneAsync(DeckAction a, IReadOnlyDictionary<string, string> vars, Action<string> switchPage)
    {
        switch (a.Type)
        {
            case ActionTypes.OpenFolder:
            {
                var path = PathExpander.Expand(a.Path!, vars);
                if (!Directory.Exists(path)) throw new IOException($"フォルダがありません: {path}");
                Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                Logger.Info($"フォルダを開く: {path}");
                break;
            }
            case ActionTypes.OpenFile:
            {
                var path = PathExpander.Expand(a.Path!, vars);
                if (!File.Exists(path)) throw new IOException($"ファイルがありません: {path}");
                Start(new ProcessStartInfo(path) { UseShellExecute = true });
                Logger.Info($"ファイルを開く: {path}");
                break;
            }
            case ActionTypes.Run:
            {
                var path = PathExpander.Expand(a.Path!, vars);
                var psi = new ProcessStartInfo(path) { UseShellExecute = true };
                if (!string.IsNullOrEmpty(a.Args)) psi.Arguments = PathExpander.ExpandArgs(a.Args, vars);
                if (!string.IsNullOrEmpty(a.Workdir)) psi.WorkingDirectory = PathExpander.Expand(a.Workdir, vars);
                else if (Path.IsPathRooted(path)) psi.WorkingDirectory = Path.GetDirectoryName(path) ?? "";
                Start(psi);
                Logger.Info($"起動: {path} {psi.Arguments}");
                break;
            }
            case ActionTypes.OpenUrl:
                Start(new ProcessStartInfo(a.Url!) { UseShellExecute = true });
                Logger.Info($"URLを開く: {a.Url}");
                break;
            case ActionTypes.Page:
                switchPage(a.Target!);
                break;
            case ActionTypes.Multi:
                for (var i = 0; i < a.Steps.Count; i++)
                {
                    if (i > 0 && a.DelayMs > 0) await Task.Delay(a.DelayMs);
                    await RunOneAsync(a.Steps[i], vars, switchPage);
                }
                break;
        }
    }

    private static void Start(ProcessStartInfo psi)
    {
        using var _ = Process.Start(psi);
    }

    /// <summary>ツールチップに出す説明（展開後のパスなど）</summary>
    public static string Describe(DeckAction a, IReadOnlyDictionary<string, string> vars) => a.Type switch
    {
        ActionTypes.OpenFolder or ActionTypes.OpenFile => PathExpander.Expand(a.Path!, vars),
        ActionTypes.Run => PathExpander.Expand(a.Path!, vars) + (string.IsNullOrEmpty(a.Args) ? "" : " " + a.Args),
        ActionTypes.OpenUrl => a.Url!,
        ActionTypes.Page => $"ページ移動: {a.Target}",
        ActionTypes.Multi => $"順に実行（{a.Steps.Count}件）:\n" + string.Join("\n", a.Steps.Select(s => "・" + Describe(s, vars))),
        _ => a.Type,
    };
}
