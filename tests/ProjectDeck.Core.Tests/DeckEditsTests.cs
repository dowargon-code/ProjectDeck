using System.Text.Json.Nodes;
using ProjectDeck.Core.Deck;

namespace ProjectDeck.Core.Tests;

public sealed class DeckEditsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdtest_" + Guid.NewGuid().ToString("N"));
    private readonly DeckStore _store;

    private const string Deck = """
        {"schema":"projectdeck/1","revision":5,"pages":[{"id":"p","name":"P","buttons":[
          {"id":"p-001","row":1,"col":1,"label":"資料","image":{"color":"#336699"},"action":{"type":"open_url","url":"https://example.com"}}]}],
         "dials":[{"index":1,"label":"音量","function":"master_volume","step":2}]}
        """;

    public DeckEditsTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new DeckStore(Path.Combine(_dir, "deck.json"), Path.Combine(_dir, "history"), 3);
        File.WriteAllText(_store.DeckPath, Deck);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private DeckData Current() => _store.ValidateCurrent().Deck!;

    [Fact]
    public void 名前だけを変えてほかの項目は残す()
    {
        var o = _store.Edit(c => DeckEdits.RenameButton(c, "p-001", " 仕様書 ", "test"));
        Assert.True(o.Ok, string.Join("\n", o.Errors));
        Assert.Equal(["p-001"], o.Changed);
        Assert.Equal(6, o.Revision);
        var b = Current().Pages[0].Buttons[0];
        Assert.Equal("仕様書", b.Label);
        Assert.Equal("#336699", b.Image?.Color);
        Assert.Equal("https://example.com", b.Action.Url);
        Assert.Single(_store.HistoryFiles());
    }

    [Fact]
    public void 無いボタンや空の名前はエラーで変更しない()
    {
        Assert.False(_store.Edit(c => DeckEdits.RenameButton(c, "p-999", "x", "test")).Ok);
        Assert.False(_store.Edit(c => DeckEdits.RenameButton(c, "p-001", "  ", "test")).Ok);
        Assert.Equal(Deck, File.ReadAllText(_store.DeckPath));
    }

    [Fact]
    public void ダイヤルを追加すると番号順に並び既定の名前が付く()
    {
        var o = _store.Edit(c => DeckEdits.SetDial(c, 3, "mic_volume", null, null, null, "test"));
        Assert.True(o.Ok, string.Join("\n", o.Errors));
        o = _store.Edit(c => DeckEdits.SetDial(c, 2, "brightness", 5, null, null, "test"));
        Assert.True(o.Ok, string.Join("\n", o.Errors));
        var dials = Current().Dials;
        Assert.Equal([1, 2, 3], dials.Select(d => d.Index));
        Assert.Equal("明るさ", dials[1].Label);
        Assert.Equal(5, dials[1].Step);
        Assert.Equal("マイク", dials[2].Label);
        Assert.Equal(2, dials[2].Step);
    }

    [Fact]
    public void 同じ番号は上書きしstepと名前は省けば残す()
    {
        var o = _store.Edit(c => DeckEdits.SetDial(c, 1, "master_volume", 4, null, null, "test"));
        Assert.True(o.Ok);
        var d = Current().Dials.Single();
        Assert.Equal(4, d.Step);
        Assert.Equal("音量", d.Label);

        // 機能を変えたら名前も機能に合わせる
        _store.Edit(c => DeckEdits.SetDial(c, 1, "app_volume", null, null, "spotify", "test"));
        d = Current().Dials.Single();
        Assert.Equal(("app_volume", "アプリ", 4, "spotify"), (d.Function, d.Label, d.Step, d.Process));
    }

    [Fact]
    public void 検証に通らないダイヤルは書き込まない()
    {
        Assert.False(_store.Edit(c => DeckEdits.SetDial(c, 2, "app_volume", null, null, null, "test")).Ok); // process なし
        Assert.False(_store.Edit(c => DeckEdits.SetDial(c, 2, "master_volume", 0, null, null, "test")).Ok); // step 範囲外
        Assert.False(_store.Edit(c => DeckEdits.SetDial(c, 5, "master_volume", null, null, null, "test")).Ok); // index 範囲外
        Assert.Equal(Deck, File.ReadAllText(_store.DeckPath));
    }

    [Fact]
    public void ダイヤルを外す()
    {
        var o = _store.Edit(c => DeckEdits.RemoveDial(c, 1, "test"));
        Assert.True(o.Ok);
        Assert.Empty(Current().Dials);
        Assert.False(JsonNode.Parse(File.ReadAllText(_store.DeckPath))!.AsObject().ContainsKey("dials"));

        // 無い番号は警告だけ
        o = _store.Edit(c => DeckEdits.RemoveDial(c, 2, "test"), dryRun: true);
        Assert.True(o.Ok);
        Assert.NotEmpty(o.Warnings);
    }

    [Fact]
    public void getの一覧にダイヤルが出る()
    {
        var list = DeckInfo.PageList(Current(), 3, 5);
        var dials = list["dials"]!.AsArray();
        Assert.Single(dials);
        Assert.Equal("master_volume", dials[0]!["function"]!.GetValue<string>());
    }
}
