using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectDeck.Core.Settings;

/// <summary>
/// settings.toml の「値の部分だけ」を書き換える（仕様書 5.5「TOMLのコメント保持」）。
/// 手で書いたコメント・空行・並び順はそのまま残す。
/// 対象は [section] と key = value だけの単純な設定ファイル（配列・インラインテーブル・複数行文字列は扱わない）。
/// </summary>
public static partial class TomlLineEditor
{
    /// <summary>
    /// [section] の key の値を literal（TOML の値表記）に置き換える。
    /// key が無ければセクションの末尾に追加、セクションが無ければファイル末尾に追加する。
    /// </summary>
    public static string SetValue(string text, string section, string key, string literal)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

        var current = "";
        var sectionStart = -1;   // [section] の行
        var sectionEnd = -1;     // セクション内の最後の「中身のある行」
        for (var i = 0; i < lines.Count; i++)
        {
            var header = HeaderRegex().Match(lines[i]);
            if (header.Success)
            {
                current = header.Groups[1].Value.Trim();
                if (current == section) { sectionStart = i; sectionEnd = i; }
                continue;
            }
            if (current != section) continue;
            if (lines[i].Trim().Length > 0) sectionEnd = i;

            var kv = KeyValueRegex().Match(lines[i]);
            if (kv.Success && kv.Groups["key"].Value == key)
            {
                // 値の後ろのコメント（# ...）は残す
                var rest = lines[i][(kv.Groups["value"].Index)..];
                var (_, comment) = SplitComment(rest);
                var spacing = comment.Length > 0 ? SpacingBeforeComment(rest, literal) : "";
                lines[i] = lines[i][..kv.Groups["value"].Index] + literal + spacing + comment;
                return string.Join(newline, lines);
            }
        }

        if (sectionStart >= 0)
        {
            lines.Insert(sectionEnd + 1, $"{key} = {literal}");
        }
        else
        {
            // 末尾の空行を整えてからセクションを追加
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key} = {literal}");
            lines.Add("");
        }
        return string.Join(newline, lines);
    }

    // ---- 値の表記 ----

    public static string Literal(bool value) => value ? "true" : "false";
    public static string Literal(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Literal(double value) =>
        value.ToString(value == Math.Floor(value) ? "0.0" : "0.0###", CultureInfo.InvariantCulture);

    public static string Literal(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in value)
        {
            sb.Append(ch switch
            {
                '\\' => @"\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => ch.ToString(),
            });
        }
        return sb.Append('"').ToString();
    }

    /// <summary>値の部分を「値」と「# コメント」に分ける（文字列の中の # は無視）</summary>
    private static (string value, string comment) SplitComment(string rest)
    {
        var inBasic = false;
        var inLiteral = false;
        for (var i = 0; i < rest.Length; i++)
        {
            var ch = rest[i];
            if (inBasic)
            {
                if (ch == '\\') i++;
                else if (ch == '"') inBasic = false;
            }
            else if (inLiteral)
            {
                if (ch == '\'') inLiteral = false;
            }
            else if (ch == '"') inBasic = true;
            else if (ch == '\'') inLiteral = true;
            else if (ch == '#') return (rest[..i], rest[i..]);
        }
        return (rest, "");
    }

    /// <summary>コメントの開始位置をなるべく元と揃える（最低1つ空白）</summary>
    private static string SpacingBeforeComment(string rest, string literal)
    {
        var (value, _) = SplitComment(rest);
        var width = Math.Max(1, value.Length - literal.Length);
        return new string(' ', width);
    }

    [GeneratedRegex(@"^\s*\[([^\[\]]+)\]\s*(#.*)?$")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^\s*(?<key>[A-Za-z0-9_-]+)\s*=\s*(?<value>.*)$")]
    private static partial Regex KeyValueRegex();
}
