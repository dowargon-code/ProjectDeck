using System.Text.Json.Nodes;

namespace ProjectDeck.Core.Deck;

/// <summary>
/// deckctl get の出力（Grok が読む情報）を作る。
/// ページ一覧、またはページ定義＋_info（行列数・空き位置・次のボタンID）。
/// </summary>
public static class DeckInfo
{
    /// <summary>全ページの一覧（ID・名前・ボタン数・空き数）</summary>
    public static JsonObject PageList(DeckData deck, int rows, int cols)
    {
        var pages = new JsonArray();
        foreach (var p in deck.Pages)
        {
            var o = new JsonObject
            {
                ["id"] = p.Id,
                ["name"] = p.Name,
                ["buttons"] = p.Buttons.Count,
                ["free"] = FreeCells(p, rows, cols).Count,
            };
            if (p.Archived) o["archived"] = true;
            pages.Add(o);
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["revision"] = deck.Revision,
            ["rows"] = rows,
            ["cols"] = cols,
            ["pages"] = pages,
            ["dials"] = DialList(deck),
        };
    }

    /// <summary>ダイヤルの割り付け（index 順）</summary>
    public static JsonArray DialList(DeckData deck)
    {
        var list = new JsonArray();
        foreach (var d in deck.Dials.OrderBy(d => d.Index))
        {
            var o = new JsonObject { ["index"] = d.Index, ["label"] = d.Label, ["function"] = d.Function, ["step"] = d.Step };
            if (d.Process != null) o["process"] = d.Process;
            list.Add(o);
        }
        return list;
    }

    /// <summary>指定ページの定義と _info。ページが無ければ null</summary>
    public static JsonObject? PageDetail(JsonObject root, DeckData deck, string pageId, int rows, int cols)
    {
        var page = deck.FindPage(pageId);
        if (page == null || root["pages"] is not JsonArray pages) return null;
        var pageJson = pages.OfType<JsonObject>().First(o => o["id"]?.GetValue<string>() == pageId);

        var free = new JsonArray();
        foreach (var (r, c) in FreeCells(page, rows, cols))
            free.Add(new JsonObject { ["row"] = r, ["col"] = c });

        return new JsonObject
        {
            ["schema"] = DeckJson.SchemaName,
            ["mode"] = "merge",
            ["revision"] = deck.Revision,
            ["pages"] = new JsonArray(pageJson.DeepClone()),
            ["_info"] = new JsonObject
            {
                ["rows"] = rows,
                ["cols"] = cols,
                ["free"] = free,
                ["next_id"] = NextButtonId(deck, page),
                ["other_pages"] = new JsonArray(deck.Pages.Where(p => p.Id != pageId)
                    .Select(p => (JsonNode?)new JsonObject { ["id"] = p.Id, ["name"] = p.Name }).ToArray()),
            },
        };
    }

    /// <summary>空き位置（左上から右へ、上の行から）</summary>
    public static List<(int Row, int Col)> FreeCells(DeckPage page, int rows, int cols)
    {
        var used = page.Buttons.Select(b => (b.Row, b.Col)).ToHashSet();
        var list = new List<(int, int)>();
        for (var r = 1; r <= rows; r++)
            for (var c = 1; c <= cols; c++)
                if (!used.Contains((r, c))) list.Add((r, c));
        return list;
    }

    /// <summary>次のボタンID（&lt;ページID&gt;-&lt;3桁連番&gt;）。全ページで重複しない番号にする</summary>
    public static string NextButtonId(DeckData deck, DeckPage page, int offset = 0)
    {
        var prefix = page.Id + "-";
        var max = deck.Pages.SelectMany(p => p.Buttons)
            .Where(b => b.Id.StartsWith(prefix, StringComparison.Ordinal))
            .Select(b => int.TryParse(b.Id[prefix.Length..], out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        return $"{prefix}{max + 1 + offset:D3}";
    }
}
