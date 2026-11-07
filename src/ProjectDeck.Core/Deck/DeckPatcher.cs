using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProjectDeck.Core.Deck;

/// <summary>パッチ適用の結果</summary>
public sealed class PatchResult
{
    /// <summary>適用後の deck.json（エラー時は null）</summary>
    public JsonObject? Result { get; set; }

    public List<string> Added { get; } = [];
    public List<string> Changed { get; } = [];
    public List<string> Deleted { get; } = [];
    public List<string> PagesAdded { get; } = [];
    public List<string> PagesChanged { get; } = [];
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];

    public bool Ok => Errors.Count == 0 && Result != null;

    /// <summary>何も変わらなかったか</summary>
    public bool NoChange => Added.Count + Changed.Count + Deleted.Count + PagesAdded.Count + PagesChanged.Count == 0;
}

/// <summary>
/// パッチ（"mode": "merge" の部分JSON）を deck.json に適用する（仕様書 6.3・7章）。
/// ・同じIDのボタンは置き換え、新しいIDは追加、"delete": true は削除
/// ・既存ページの name / color / vars / archived はパッチに書いたものだけ上書き（null で項目を削除）
/// ・新しいページは name が必須
/// ・dials を書いた場合は丸ごと置き換え
/// 元の JSON は変更せず、複製に対して適用する。
/// </summary>
public static class DeckPatcher
{
    /// <summary>パッチかどうか（"mode": "merge" を持つか）</summary>
    public static bool IsPatch(JsonNode? node) =>
        node is JsonObject o && o["mode"] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == "merge";

    public static PatchResult Apply(JsonObject current, JsonNode? patchNode)
    {
        var r = new PatchResult();
        if (patchNode is not JsonObject patch)
        {
            r.Errors.Add("パッチの最上位は { } のオブジェクトである必要があります");
            return r;
        }
        if (GetString(patch, "schema") != DeckJson.SchemaName)
            r.Errors.Add($"パッチの schema は \"{DeckJson.SchemaName}\" である必要があります");
        if (!IsPatch(patch))
            r.Errors.Add("パッチには \"mode\": \"merge\" が必要です");
        if (r.Errors.Count > 0) return r;

        var result = (JsonObject)current.DeepClone();
        if (result["pages"] is not JsonArray pages)
        {
            pages = [];
            result["pages"] = pages;
        }

        if (patch["pages"] is JsonArray patchPages)
        {
            foreach (var pp in patchPages)
            {
                if (pp is not JsonObject patchPage || GetString(patchPage, "id") is not { } pageId)
                {
                    r.Errors.Add("パッチの pages の各要素には id（文字列）が必要です");
                    continue;
                }
                if (patchPage["delete"] is JsonValue)
                {
                    r.Errors.Add($"ページ \"{pageId}\": ページは削除できません。終了した案件は \"archived\": true にしてください");
                    continue;
                }
                var page = FindById(pages, pageId);
                if (page == null)
                {
                    if (GetString(patchPage, "name") == null)
                    {
                        r.Errors.Add($"ページ \"{pageId}\" は存在しないため新規作成になりますが、name がありません");
                        continue;
                    }
                    page = new JsonObject { ["id"] = pageId };
                    MergePageProps(page, patchPage);
                    pages.Add(page);
                    r.PagesAdded.Add(pageId);
                }
                else if (MergePageProps(page, patchPage))
                {
                    r.PagesChanged.Add(pageId);
                }

                if (patchPage["buttons"] is JsonArray patchButtons)
                    MergeButtons(pages, page, pageId, patchButtons, r);
                else if (patchPage["buttons"] != null)
                    r.Errors.Add($"ページ \"{pageId}\": buttons は配列である必要があります");
            }
        }
        else if (patch["pages"] != null)
        {
            r.Errors.Add("パッチの pages は配列である必要があります");
        }

        if (patch.ContainsKey("dials"))
        {
            result["dials"] = patch["dials"]?.DeepClone();
            r.PagesChanged.Add("(dials)");
        }

        if (r.Errors.Count > 0) return r;

        // revision を +1、updated_by を記録
        var rev = result["revision"].TryGetInt64(out var n) ? n : 0;
        result["revision"] = rev + 1;
        result["updated_by"] = GetString(patch, "updated_by") ?? "unknown";
        r.Result = result;
        return r;
    }

    /// <summary>ページの項目（id・buttons・delete 以外）を上書き。変化があれば true</summary>
    private static bool MergePageProps(JsonObject page, JsonObject patchPage)
    {
        var changed = false;
        foreach (var (key, value) in patchPage)
        {
            if (key is "id" or "buttons" or "delete") continue;
            var before = page[key]?.ToJsonString();
            if (value == null) page.Remove(key);
            else page[key] = value.DeepClone();
            if (before != page[key]?.ToJsonString()) changed = true;
        }
        return changed;
    }

    private static void MergeButtons(JsonArray pages, JsonObject page, string pageId, JsonArray patchButtons, PatchResult r)
    {
        if (page["buttons"] is not JsonArray buttons)
        {
            buttons = [];
            page["buttons"] = buttons;
        }
        foreach (var pb in patchButtons)
        {
            if (pb is not JsonObject patchButton || GetString(patchButton, "id") is not { } id)
            {
                r.Errors.Add($"ページ \"{pageId}\": buttons の各要素には id（文字列）が必要です");
                continue;
            }

            if (patchButton["delete"] is JsonValue dv && dv.GetValueKind() == JsonValueKind.True)
            {
                var target = FindById(buttons, id);
                if (target == null)
                {
                    r.Warnings.Add($"削除しようとしたボタン \"{id}\" はページ \"{pageId}\" にありません");
                    continue;
                }
                buttons.Remove(target);
                r.Deleted.Add(id);
                continue;
            }

            var replacement = (JsonObject)patchButton.DeepClone();
            replacement.Remove("delete");
            var existing = FindById(buttons, id);
            if (existing != null)
            {
                // 同じIDは置き換え（並び順は保つ）
                var same = existing.ToJsonString() == replacement.ToJsonString();
                buttons[buttons.IndexOf(existing)] = replacement;
                if (!same) r.Changed.Add(id);
                continue;
            }

            // 別のページにある同じIDは「移動」として扱う
            var moved = false;
            foreach (var other in pages.OfType<JsonObject>())
            {
                if (other["buttons"] is JsonArray ob && FindById(ob, id) is { } found)
                {
                    ob.Remove(found);
                    moved = true;
                }
            }
            buttons.Add(replacement);
            (moved ? r.Changed : r.Added).Add(id);
        }
    }

    private static JsonObject? FindById(JsonArray array, string id) =>
        array.OfType<JsonObject>().FirstOrDefault(o => GetString(o, "id") == id);

    private static string? GetString(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
}
