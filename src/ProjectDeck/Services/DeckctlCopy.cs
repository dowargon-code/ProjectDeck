using ProjectDeck.Core;

namespace ProjectDeck.Services;

/// <summary>
/// deckctl 一式を、データフォルダの決まった場所（%USERPROFILE%\ProjectDeck\deckctl\）にコピーする（仕様書 6.3）。
/// Grok はいつもこの場所の deckctl.exe を呼ぶので、ランチャー本体の置き場所（dist・C:\Tools など）が
/// 変わっても、Grok のスキルを書き換えずに済む。ランチャーを起動するたびに、変わったファイルだけ上書きする。
/// </summary>
internal static class DeckctlCopy
{
    /// <summary>deckctl を動かすのに必要なファイル（ランチャー本体の exe の隣にあるもの）</summary>
    private static readonly string[] Files =
    [
        "deckctl.exe",
        "deckctl.dll",
        "deckctl.deps.json",
        "deckctl.runtimeconfig.json",
        "ProjectDeck.Core.dll",
        "Tomlyn.dll",
    ];

    /// <summary>コピー先（Grok のスキルに書く場所）</summary>
    public static string TargetDirectory => Path.Combine(AppPaths.DataDirectory, "deckctl");

    /// <summary>コピーする。戻り値は警告（無ければ null）</summary>
    public static string? Sync()
    {
        var source = AppPaths.AppDirectory;
        var testMode = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROJECTDECK_HOME"));

        // 開発中のビルド（bin フォルダ）からは、普段使いのコピーを書き換えない
        // （動作確認用フォルダ PROJECTDECK_HOME で動かしているときは、そちらにコピーする）
        if (!testMode && source.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
            return null;

        var missing = Files.Where(f => !File.Exists(Path.Combine(source, f))).ToList();
        if (missing.Count > 0)
            return $"deckctl を {TargetDirectory} にコピーできません（ランチャーの隣に {string.Join(", ", missing)} がありません）";

        try
        {
            Directory.CreateDirectory(TargetDirectory);
            var copied = 0;
            foreach (var name in Files)
            {
                var from = new FileInfo(Path.Combine(source, name));
                var to = new FileInfo(Path.Combine(TargetDirectory, name));
                if (to.Exists && to.Length == from.Length && to.LastWriteTimeUtc == from.LastWriteTimeUtc) continue;

                // 一時ファイルに書いてから置き換える（コピー中に Grok が呼んでも壊れたファイルを掴まない）
                var temp = to.FullName + ".tmp";
                from.CopyTo(temp, overwrite: true);
                File.SetLastWriteTimeUtc(temp, from.LastWriteTimeUtc);
                File.Move(temp, to.FullName, overwrite: true);
                copied++;
            }
            WriteReadme();
            if (copied > 0) Logger.Info($"deckctl を {TargetDirectory} に更新しました（{copied} ファイル、元：{source}）");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // deckctl の実行中などで上書きできないときは、次の起動で取り直す
            Logger.Warn("deckctl をコピーできません: " + ex.Message);
            return $"deckctl を {TargetDirectory} に更新できませんでした（次の起動で再試行します）: {ex.Message}";
        }
    }

    private static void WriteReadme()
    {
        var path = Path.Combine(TargetDirectory, "README.txt");
        if (File.Exists(path)) return;
        File.WriteAllText(path,
            "このフォルダの deckctl は、ProjectDeck（ランチャー）が起動するたびに自動でコピーしたものです。\r\n" +
            "Grok などの AI はここの deckctl.exe を呼びます。ランチャー本体の置き場所が変わっても、この場所は変わりません。\r\n" +
            "手で編集・削除しないでください（消しても次の起動で作り直されます）。\r\n",
            new System.Text.UTF8Encoding(true));
    }
}
