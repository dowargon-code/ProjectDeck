using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProjectDeck.Core.Deck;

/// <summary>
/// パッチを書かずにできる小さな変更（deckctl rename / dial と、本体の「名前を変更」から使う）。
/// どれも元の JSON は変更せず、複製に対して適用した結果を PatchResult で返す。
/// revision +1・updated_by の記録は DeckPatcher と同じ。DeckStore.Edit に渡して使う。
/// </summary>
public static class DeckEdits
{
    /// <summary>ボタンの表示名（label）だけを変える。ほかの項目はそのまま</summary>
    public static PatchResult RenameButton(JsonObject current, string buttonId, string label, string updatedBy)
    {
        var r = new PatchResult();
        label = label.Trim();
        if (label.Length == 0)
        {
            r.Errors.Add("新しい名前が空です");
            return r;
        }
        var result = (JsonObject)current.DeepClone();
        var button = (result["pages"] as JsonArray)?.OfType<JsonObject>()
            .SelectMany(p => (p["buttons"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .FirstOrDefault(b => GetString(b, "id") == buttonId);
        if (button == null)
        {
            r.Errors.Add($"ボタン \"{buttonId}\" がありません（deckctl get --page <ID> でボタンIDを確認してください）");
            return r;
        }
        if (GetString(button, "label") != label)
        {
            button["label"] = label;
            r.Changed.Add(buttonId);
        }
        return Finish(result, r, updatedBy);
    }

    /// <summary>
    /// ボタンを同じページ内の別の位置に動かす（本体のドラッグ移動）。
    /// 移動先に別のボタンがあれば位置を入れ替える。
    /// </summary>
    public static PatchResult MoveButton(JsonObject current, string buttonId, int row, int col, string updatedBy)
    {
        var r = new PatchResult();
        if (row < 1 || col < 1)
        {
            r.Errors.Add($"移動先の位置が正しくありません（{row}行{col}列）");
            return r;
        }
        var result = (JsonObject)current.DeepClone();
        var (page, button) = FindButton(result, buttonId);
        if (page == null || button == null)
        {
            r.Errors.Add($"ボタン \"{buttonId}\" がありません");
            return r;
        }
        var oldRow = button["row"].TryGetInt64(out var or) ? or : 0;
        var oldCol = button["col"].TryGetInt64(out var oc) ? oc : 0;
        if (oldRow == row && oldCol == col) return Finish(result, r, updatedBy);

        var other = (page["buttons"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(b => b != button && b["row"].TryGetInt64(out var br) && br == row && b["col"].TryGetInt64(out var bc) && bc == col);
        if (other != null)
        {
            other["row"] = oldRow;
            other["col"] = oldCol;
            r.Changed.Add(GetString(other, "id") ?? "");
        }
        button["row"] = row;
        button["col"] = col;
        r.Changed.Insert(0, buttonId);
        return Finish(result, r, updatedBy);
    }

    /// <summary>
    /// ボタンの画像（image）だけを置き換える（本体への画像ファイルのドロップ）。image が null なら画像を外す。
    /// 中身の正しさ（url / file / data / color のどれか1つ など）は DeckStore.Edit の再検証で確かめる。
    /// </summary>
    public static PatchResult SetButtonImage(JsonObject current, string buttonId, JsonObject? image, string updatedBy)
    {
        var r = new PatchResult();
        var result = (JsonObject)current.DeepClone();
        var (_, button) = FindButton(result, buttonId);
        if (button == null)
        {
            r.Errors.Add($"ボタン \"{buttonId}\" がありません");
            return r;
        }
        var before = button["image"]?.ToJsonString();
        if (image == null) button.Remove("image");
        else button["image"] = image.DeepClone();
        if (before != button["image"]?.ToJsonString()) r.Changed.Add(buttonId);
        return Finish(result, r, updatedBy);
    }

    /// <summary>ボタンと、それが載っているページ</summary>
    private static (JsonObject? Page, JsonObject? Button) FindButton(JsonObject root, string buttonId)
    {
        foreach (var page in (root["pages"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var button = (page["buttons"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(b => GetString(b, "id") == buttonId);
            if (button != null) return (page, button);
        }
        return (null, null);
    }

    /// <summary>
    /// ダイヤルを1つ追加・上書きする（同じ index があれば置き換え、ほかのダイヤルはそのまま）。
    /// label を省くと、上書き時は今の名前を残し、新規なら機能名から決める。
    /// step を省くと、上書き時は今の値を残し、新規なら 2。
    /// </summary>
    public static PatchResult SetDial(JsonObject current, int index, string function, int? step, string? label, string? process, string updatedBy)
    {
        var r = new PatchResult();
        var result = (JsonObject)current.DeepClone();
        if (result["dials"] is not JsonArray dials)
        {
            dials = [];
            result["dials"] = dials;
        }
        var existing = dials.OfType<JsonObject>().FirstOrDefault(d => d["index"].TryGetInt64(out var n) && n == index);

        var dial = new JsonObject
        {
            ["index"] = index,
            ["label"] = label ?? (existing != null && GetString(existing, "label") is { } old && GetString(existing, "function") == function
                ? old : DefaultLabel(function)),
            ["function"] = function,
            ["step"] = step ?? (existing != null && existing["step"].TryGetInt64(out var s) ? s : 2),
        };
        if (process != null) dial["process"] = process;
        else if (function == "app_volume" && existing != null && GetString(existing, "process") is { } p) dial["process"] = p;

        if (existing != null)
        {
            if (existing.ToJsonString() != dial.ToJsonString())
            {
                dials[dials.IndexOf(existing)] = dial;
                r.PagesChanged.Add($"(dial {index})");
            }
        }
        else
        {
            // index の順に並べる
            var pos = dials.OfType<JsonObject>().Count(d => d["index"].TryGetInt64(out var n) && n < index);
            dials.Insert(pos, dial);
            r.PagesAdded.Add($"(dial {index})");
        }
        return Finish(result, r, updatedBy);
    }

    /// <summary>ダイヤルを1つ外す</summary>
    public static PatchResult RemoveDial(JsonObject current, int index, string updatedBy)
    {
        var r = new PatchResult();
        var result = (JsonObject)current.DeepClone();
        var dials = result["dials"] as JsonArray;
        var existing = dials?.OfType<JsonObject>().FirstOrDefault(d => d["index"].TryGetInt64(out var n) && n == index);
        if (existing == null)
        {
            r.Warnings.Add($"ダイヤル {index} は割り付けられていません");
            return Finish(result, r, updatedBy);
        }
        dials!.Remove(existing);
        if (dials.Count == 0) result.Remove("dials");
        r.PagesChanged.Add($"(dial {index} を削除)");
        return Finish(result, r, updatedBy);
    }

    /// <summary>機能の既定の表示名</summary>
    public static string DefaultLabel(string function) => function switch
    {
        "master_volume" => "音量",
        "mic_volume" => "マイク",
        "brightness" => "明るさ",
        "app_volume" => "アプリ",
        _ => "",
    };

    private static PatchResult Finish(JsonObject result, PatchResult r, string updatedBy)
    {
        if (r.Errors.Count > 0) return r;
        var rev = result["revision"].TryGetInt64(out var n) ? n : 0;
        result["revision"] = rev + 1;
        result["updated_by"] = updatedBy;
        r.Result = result;
        return r;
    }

    private static string? GetString(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
}
