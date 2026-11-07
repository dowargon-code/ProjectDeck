using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProjectDeck.Core.Deck;

/// <summary>検証結果</summary>
public sealed class ValidationResult
{
    /// <summary>検証OKのときのモデル</summary>
    public DeckData? Deck { get; set; }

    /// <summary>解析した JSON（書き換え用）</summary>
    public JsonObject? Root { get; set; }

    /// <summary>エラー（1件でもあれば反映しない）</summary>
    public List<string> Errors { get; } = [];

    /// <summary>警告（反映はする）</summary>
    public List<string> Warnings { get; } = [];

    public bool Ok => Errors.Count == 0 && Deck != null;
}

/// <summary>
/// deck.json の検証（仕様書 7章）。
/// ①JSON構文 ②スキーマ（必須項目・型） ③位置重複 ④IDの重複 ⑤アクション種別。
/// 本体と deckctl で同じ処理を使うので、「deckctl で OK なら本体でも必ず反映できる」。
/// </summary>
public static partial class DeckValidator
{
    /// <summary>文字列から検証する</summary>
    public static ValidationResult Validate(string text)
    {
        var node = DeckJson.TryParse(text, out var error);
        if (node == null)
        {
            var r = new ValidationResult();
            r.Errors.Add(error ?? "JSONが空です");
            return r;
        }
        return Validate(node);
    }

    /// <summary>解析済みの JSON から検証する</summary>
    public static ValidationResult Validate(JsonNode node)
    {
        var r = new ValidationResult();
        if (node is not JsonObject root)
        {
            r.Errors.Add("最上位は { } のオブジェクトである必要があります");
            return r;
        }
        r.Root = root;

        var schema = Str(root, "schema", "", r, required: true);
        if (schema != null && schema != DeckJson.SchemaName)
            r.Errors.Add($"schema は \"{DeckJson.SchemaName}\" である必要があります（\"{schema}\"）");

        var revision = Int(root, "revision", "", r, required: true) ?? 0;
        var updatedBy = Str(root, "updated_by", "", r);

        var pages = new List<DeckPage>();
        var pageIds = new HashSet<string>();
        var buttonIds = new HashSet<string>();
        if (root["pages"] is JsonArray pageArray)
        {
            for (var i = 0; i < pageArray.Count; i++)
            {
                var page = ValidatePage(pageArray[i], $"pages[{i}]", r, pageIds, buttonIds);
                if (page != null) pages.Add(page);
            }
        }
        else
        {
            r.Errors.Add(root.ContainsKey("pages") ? "pages は配列である必要があります" : "pages がありません");
        }

        var dials = new List<DeckDial>();
        if (root["dials"] is JsonArray dialArray)
        {
            for (var i = 0; i < dialArray.Count; i++)
            {
                var dial = ValidateDial(dialArray[i], $"dials[{i}]", r);
                if (dial != null) dials.Add(dial);
            }
        }
        else if (root.ContainsKey("dials") && root["dials"] != null)
        {
            r.Errors.Add("dials は配列である必要があります");
        }

        // page アクションの移動先が存在するか（存在しなくても反映はする）
        foreach (var p in pages)
            foreach (var b in p.Buttons)
                foreach (var target in PageTargets(b.Action))
                    if (!pageIds.Contains(target))
                        r.Warnings.Add($"{b.Id}: 移動先のページ \"{target}\" がありません");

        if (r.Errors.Count == 0)
            r.Deck = new DeckData(schema!, revision, updatedBy, pages, dials);
        return r;
    }

    /// <summary>グリッド（行数×列数）からはみ出しているボタンの警告</summary>
    public static List<string> GridWarnings(DeckData deck, int rows, int cols)
    {
        var list = new List<string>();
        foreach (var p in deck.Pages.Where(p => !p.Archived))
            foreach (var b in p.Buttons.Where(b => b.Row > rows || b.Col > cols))
                list.Add($"{p.Name}: {b.Id}（{b.Row}行{b.Col}列）はグリッド {rows}×{cols} の外のため表示されません");
        return list;
    }

    // ---------------- ページ ----------------

    private static DeckPage? ValidatePage(JsonNode? node, string path, ValidationResult r,
        HashSet<string> pageIds, HashSet<string> buttonIds)
    {
        if (node is not JsonObject obj)
        {
            r.Errors.Add($"{path}: オブジェクトである必要があります");
            return null;
        }
        var errorCount = r.Errors.Count;
        var id = Str(obj, "id", path, r, required: true);
        if (id != null)
        {
            if (!PageIdRegex().IsMatch(id))
                r.Errors.Add($"{path}.id \"{id}\": ページIDは英小文字・数字・ハイフンのみ使えます");
            else if (!pageIds.Add(id))
                r.Errors.Add($"{path}.id \"{id}\": ページIDが重複しています");
            path = $"ページ \"{id}\"";
        }
        var name = Str(obj, "name", path, r, required: true);
        var color = Str(obj, "color", path, r);
        if (color != null && !ColorRegex().IsMatch(color))
            r.Warnings.Add($"{path}.color \"{color}\": #RRGGBB 形式ではないため既定色で表示します");
        var archived = Bool(obj, "archived", path, r) ?? false;

        var vars = new Dictionary<string, string>();
        if (obj["vars"] is JsonObject varObj)
        {
            foreach (var (k, v) in varObj)
            {
                if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) vars[k] = s;
                else r.Errors.Add($"{path}.vars.{k}: 文字列である必要があります");
            }
        }
        else if (obj["vars"] != null)
        {
            r.Errors.Add($"{path}.vars: オブジェクトである必要があります");
        }

        var buttons = new List<DeckButton>();
        var positions = new Dictionary<(int, int), string>();
        if (obj["buttons"] is JsonArray buttonArray)
        {
            for (var i = 0; i < buttonArray.Count; i++)
            {
                var b = ValidateButton(buttonArray[i], $"{path}.buttons[{i}]", r, vars);
                if (b == null) continue;
                if (!buttonIds.Add(b.Id))
                    r.Errors.Add($"ボタンID \"{b.Id}\" が重複しています");
                if (positions.TryGetValue((b.Row, b.Col), out var other))
                    r.Errors.Add($"{path}: {b.Id} と {other} が同じ位置（{b.Row}行{b.Col}列）です");
                else
                    positions[(b.Row, b.Col)] = b.Id;
                buttons.Add(b);
            }
        }
        else if (obj["buttons"] != null)
        {
            r.Errors.Add($"{path}.buttons: 配列である必要があります");
        }

        if (r.Errors.Count != errorCount || id == null || name == null) return null;
        return new DeckPage(id, name, color, vars, archived, buttons);
    }

    // ---------------- ボタン ----------------

    private static DeckButton? ValidateButton(JsonNode? node, string path, ValidationResult r,
        IReadOnlyDictionary<string, string> vars)
    {
        if (node is not JsonObject obj)
        {
            r.Errors.Add($"{path}: オブジェクトである必要があります");
            return null;
        }
        var errorCount = r.Errors.Count;
        var id = Str(obj, "id", path, r, required: true);
        if (id != null) path = $"ボタン \"{id}\"";
        if (obj.ContainsKey("delete"))
            r.Errors.Add($"{path}: \"delete\" はパッチ（mode: merge）でのみ使えます");

        var row = Int(obj, "row", path, r, required: true);
        var col = Int(obj, "col", path, r, required: true);
        if (row is < 1) r.Errors.Add($"{path}.row: 1以上である必要があります");
        if (col is < 1) r.Errors.Add($"{path}.col: 1以上である必要があります");
        var label = Str(obj, "label", path, r, required: true);

        var labelColor = Str(obj, "label_color", path, r);
        if (labelColor != null && labelColor is not ("auto" or "white" or "black") && !ColorRegex().IsMatch(labelColor))
            r.Errors.Add($"{path}.label_color: auto / white / black / #RRGGBB のどれかを指定してください");

        ButtonImage? image = null;
        if (obj["image"] is JsonObject imgObj)
        {
            var ip = path + ".image";
            image = new ButtonImage(
                Str(imgObj, "url", ip, r), Str(imgObj, "query", ip, r), Str(imgObj, "license", ip, r),
                Str(imgObj, "file", ip, r), Str(imgObj, "data", ip, r), Str(imgObj, "color", ip, r));
            var kinds = new[] { image.Url, image.File, image.Data, image.Color }.Count(x => x != null);
            if (kinds > 1)
                r.Errors.Add($"{ip}: url / file / data / color はどれか1つだけ指定してください");
            if (image.Url != null && !(Uri.TryCreate(image.Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
                r.Errors.Add($"{ip}.url: http(s) のURLである必要があります");
            if (image.Color != null && !ColorRegex().IsMatch(image.Color))
                r.Errors.Add($"{ip}.color: #RRGGBB 形式で指定してください");
        }
        else if (obj["image"] != null)
        {
            r.Errors.Add($"{path}.image: オブジェクトである必要があります");
        }

        var action = ValidateAction(obj["action"], path + ".action", r, vars, depth: 0, required: true);

        if (r.Errors.Count != errorCount || id == null || row == null || col == null || label == null || action == null)
            return null;
        return new DeckButton(id, (int)row, (int)col, label, image, labelColor, action,
            obj.ToJsonString(DeckJson.CompactOptions));
    }

    // ---------------- アクション ----------------

    private static DeckAction? ValidateAction(JsonNode? node, string path, ValidationResult r,
        IReadOnlyDictionary<string, string> vars, int depth, bool required)
    {
        if (node is not JsonObject obj)
        {
            if (node != null || required) r.Errors.Add($"{path}: オブジェクトである必要があります");
            return null;
        }
        var errorCount = r.Errors.Count;
        var type = Str(obj, "type", path, r, required: true);
        if (type != null && !ActionTypes.All.Contains(type))
        {
            r.Errors.Add($"{path}.type \"{type}\": 使える種類は {string.Join(" / ", ActionTypes.All)} です");
            return null;
        }

        string? p = null, args = null, workdir = null, url = null, target = null;
        var delay = 0;
        var steps = new List<DeckAction>();
        switch (type)
        {
            case ActionTypes.OpenFolder:
            case ActionTypes.OpenFile:
                p = Str(obj, "path", path, r, required: true);
                break;
            case ActionTypes.Run:
                p = Str(obj, "path", path, r, required: true);
                args = Str(obj, "args", path, r);
                workdir = Str(obj, "workdir", path, r);
                break;
            case ActionTypes.OpenUrl:
                url = Str(obj, "url", path, r, required: true);
                break;
            case ActionTypes.Page:
                target = Str(obj, "target", path, r, required: true);
                break;
            case ActionTypes.Multi:
                delay = (int)(Int(obj, "delay_ms", path, r) ?? 300);
                if (delay is < 0 or > 60000) r.Errors.Add($"{path}.delay_ms: 0～60000 で指定してください");
                if (depth >= 3)
                {
                    r.Errors.Add($"{path}: multi の入れ子が深すぎます");
                }
                else if (obj["steps"] is JsonArray stepArray && stepArray.Count > 0)
                {
                    for (var i = 0; i < stepArray.Count; i++)
                    {
                        var s = ValidateAction(stepArray[i], $"{path}.steps[{i}]", r, vars, depth + 1, required: true);
                        if (s != null) steps.Add(s);
                    }
                }
                else
                {
                    r.Errors.Add($"{path}.steps: 1つ以上の手順が必要です");
                }
                break;
        }

        foreach (var v in new[] { p, workdir }.Where(x => x != null).SelectMany(x => PathExpander.UnresolvedVars(x!, vars)))
            r.Warnings.Add($"{path}: 変数 ${{{v}}} がページの vars にありません");

        if (r.Errors.Count != errorCount || type == null) return null;
        return new DeckAction(type, p, args, workdir, url, delay, steps, target);
    }

    // ---------------- ダイヤル ----------------

    private static DeckDial? ValidateDial(JsonNode? node, string path, ValidationResult r)
    {
        if (node is not JsonObject obj)
        {
            r.Errors.Add($"{path}: オブジェクトである必要があります");
            return null;
        }
        var errorCount = r.Errors.Count;
        var index = Int(obj, "index", path, r, required: true);
        if (index is < 1 or > 4) r.Errors.Add($"{path}.index: 1～4 で指定してください");
        var label = Str(obj, "label", path, r) ?? "";
        var function = Str(obj, "function", path, r, required: true);
        if (function != null && !DialFunctions.All.Contains(function))
            r.Errors.Add($"{path}.function \"{function}\": 使える機能は {string.Join(" / ", DialFunctions.All)} です");
        var step = Int(obj, "step", path, r) ?? 2;
        var process = Str(obj, "process", path, r);
        if (r.Errors.Count != errorCount || index == null || function == null) return null;
        return new DeckDial((int)index, label, function, (int)step, process);
    }

    private static IEnumerable<string> PageTargets(DeckAction a) =>
        a.Type == ActionTypes.Page && a.Target != null ? [a.Target] : a.Steps.SelectMany(PageTargets);

    // ---------------- 値の取り出し ----------------

    private static string? Str(JsonObject obj, string key, string path, ValidationResult r, bool required = false)
    {
        var node = obj[key];
        if (node == null)
        {
            if (required) r.Errors.Add($"{Join(path, key)}: 必須項目がありません");
            return null;
        }
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String) return v.GetValue<string>();
        r.Errors.Add($"{Join(path, key)}: 文字列である必要があります");
        return null;
    }

    private static long? Int(JsonObject obj, string key, string path, ValidationResult r, bool required = false)
    {
        var node = obj[key];
        if (node == null)
        {
            if (required) r.Errors.Add($"{Join(path, key)}: 必須項目がありません");
            return null;
        }
        if (node.TryGetInt64(out var n)) return n;
        r.Errors.Add($"{Join(path, key)}: 整数である必要があります");
        return null;
    }

    private static bool? Bool(JsonObject obj, string key, string path, ValidationResult r)
    {
        var node = obj[key];
        if (node == null) return null;
        if (node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False) return v.GetValue<bool>();
        r.Errors.Add($"{Join(path, key)}: true / false である必要があります");
        return null;
    }

    private static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex PageIdRegex();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    internal static partial Regex ColorRegex();
}
