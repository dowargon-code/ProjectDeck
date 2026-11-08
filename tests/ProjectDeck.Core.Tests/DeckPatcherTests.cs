using System.Text.Json.Nodes;
using ProjectDeck.Core.Deck;

namespace ProjectDeck.Core.Tests;

public class DeckPatcherTests
{
    private static JsonObject Current() => (JsonObject)JsonNode.Parse(Samples.SpecDeck)!;

    [Fact]
    public void 追加_置換_削除と無変更部分の保持()
    {
        var current = Current();
        var patch = JsonNode.Parse("""
            {"schema":"projectdeck/1","mode":"merge","updated_by":"grok","pages":[{"id":"a-mes","buttons":[
              {"id":"a-mes-012","row":2,"col":1,"label":"試験","action":{"type":"open_folder","path":"${root}/03_試験"}},
              {"id":"a-mes-002","row":1,"col":3,"label":"I/O一覧","action":{"type":"open_file","path":"${root}/02_設計/IO一覧.xlsx"}},
              {"id":"a-mes-005","delete":true}
            ]}]}
            """);
        var r = DeckPatcher.Apply(current, patch);
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        Assert.Equal(["a-mes-012"], r.Added);
        Assert.Equal(["a-mes-002"], r.Changed);
        Assert.Equal(["a-mes-005"], r.Deleted);

        var v = DeckValidator.Validate(r.Result!);
        Assert.True(v.Ok);
        Assert.Equal(44, v.Deck!.Revision);
        Assert.Equal("grok", v.Deck.UpdatedBy);
        // 触れていないボタンとダイヤルは元のまま
        var before = DeckValidator.Validate(current).Deck!;
        Assert.Equal(before.Pages[0].Buttons.Single(b => b.Id == "a-mes-011").Json,
            v.Deck.Pages[0].Buttons.Single(b => b.Id == "a-mes-011").Json);
        Assert.Equal(3, v.Deck.Dials.Count);
        // 元の JSON は変更しない
        Assert.Equal(43, current["revision"]!.GetValue<int>());
    }

    [Fact]
    public void 新しいページの作成とアーカイブ()
    {
        var patch = JsonNode.Parse("""
            {"schema":"projectdeck/1","mode":"merge","pages":[
              {"id":"b-scada","name":"B工場 SCADA","vars":{"root":"//nas01/案件/B工場"},"buttons":[
                {"id":"b-scada-001","row":1,"col":1,"label":"仕様書","action":{"type":"open_folder","path":"${root}/仕様書"}}]},
              {"id":"a-mes","archived":true}
            ]}
            """);
        var r = DeckPatcher.Apply(Current(), patch);
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        Assert.Equal(["b-scada"], r.PagesAdded);
        Assert.Equal(["a-mes"], r.PagesChanged);
        var deck = DeckValidator.Validate(r.Result!).Deck!;
        Assert.True(deck.FindPage("a-mes")!.Archived);
        Assert.Equal("B工場 SCADA", deck.FindPage("b-scada")!.Name);
    }

    [Fact]
    public void 名前の無い新規ページとmode無しはエラー()
    {
        var r1 = DeckPatcher.Apply(Current(), JsonNode.Parse("""{"schema":"projectdeck/1","mode":"merge","pages":[{"id":"new"}]}"""));
        Assert.Contains(r1.Errors, e => e.Contains("name"));
        var r2 = DeckPatcher.Apply(Current(), JsonNode.Parse("""{"schema":"projectdeck/1","pages":[]}"""));
        Assert.Contains(r2.Errors, e => e.Contains("merge"));
    }

    [Fact]
    public void 別ページの同じIDは移動扱い()
    {
        var patch = JsonNode.Parse("""
            {"schema":"projectdeck/1","mode":"merge","pages":[{"id":"common","name":"共通","buttons":[
              {"id":"a-mes-001","row":1,"col":1,"label":"仕様書","action":{"type":"open_folder","path":"D:/x"}}]}]}
            """);
        var r = DeckPatcher.Apply(Current(), patch);
        Assert.True(r.Ok);
        Assert.Equal(["a-mes-001"], r.Changed);
        var deck = DeckValidator.Validate(r.Result!);
        Assert.True(deck.Ok);
        Assert.DoesNotContain(deck.Deck!.FindPage("a-mes")!.Buttons, b => b.Id == "a-mes-001");
    }
}
