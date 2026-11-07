using System.Text.RegularExpressions;

namespace ProjectDeck.Core;

/// <summary>
/// deck.json / settings.toml に書かれたパスを実際のパスに変換する（仕様書 6.2「パス表記のルール」）。
/// ・${名前} をページの vars で置換
/// ・%USERPROFILE% などの環境変数を展開
/// ・「/」区切りを「\」に変換（//nas01/share → \\nas01\share）
/// </summary>
public static partial class PathExpander
{
    public static string Expand(string path, IReadOnlyDictionary<string, string>? vars = null)
    {
        var s = path.Trim();
        if (vars is { Count: > 0 })
        {
            // vars の中で別の変数を使っている場合に備えて数回繰り返す
            for (var i = 0; i < 5 && s.Contains("${"); i++)
            {
                s = VarRegex().Replace(s, m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
            }
        }
        s = Environment.ExpandEnvironmentVariables(s);
        return s.Replace('/', '\\');
    }

    /// <summary>run の args 用：変数と環境変数だけ展開する（「/」は引数の記号として使われるので変換しない）</summary>
    public static string ExpandArgs(string args, IReadOnlyDictionary<string, string>? vars = null)
    {
        var s = args;
        if (vars is { Count: > 0 })
            s = VarRegex().Replace(s, m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v.Replace('/', '\\') : m.Value);
        return Environment.ExpandEnvironmentVariables(s);
    }

    /// <summary>展開されずに残った ${名前} を返す（検証の警告用）</summary>
    public static IEnumerable<string> UnresolvedVars(string path, IReadOnlyDictionary<string, string>? vars)
    {
        foreach (Match m in VarRegex().Matches(path))
        {
            var name = m.Groups[1].Value;
            if (vars == null || !vars.ContainsKey(name)) yield return name;
        }
    }

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex VarRegex();
}
