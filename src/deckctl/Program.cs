using System.Reflection;
using System.Text.Json;

// deckctl：Grok のローカル実行から deck.json を安全に取得・更新するコマンド（仕様書 6.3）。
// 出力はすべて JSON。サブコマンド（get / apply / validate / undo / check-image）はフェーズ1で実装する。

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

if (args.Length == 0 || args[0] is "--help" or "-h" or "--version")
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        name = "deckctl",
        version,
        commands = new[] { "get", "apply", "validate", "undo", "check-image" },
    }));
    return 0;
}

// 未実装のサブコマンドは検証エラー扱い（終了コード1）で返す
Console.WriteLine(JsonSerializer.Serialize(new { ok = false, errors = new[] { $"未実装のコマンドです: {args[0]}" } }));
return 1;
