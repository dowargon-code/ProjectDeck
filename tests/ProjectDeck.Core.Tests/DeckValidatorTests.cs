using ProjectDeck.Core.Deck;

namespace ProjectDeck.Core.Tests;

public class DeckValidatorTests
{
    [Fact]
    public void 仕様書のサンプルは検証OK()
    {
        var r = DeckValidator.Validate(Samples.SpecDeck);
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        var page = Assert.Single(r.Deck!.Pages);
        Assert.Equal("a-mes", page.Id);
        Assert.Equal(4, page.Buttons.Count);
        var multi = page.Buttons.Single(b => b.Id == "a-mes-011").Action;
        Assert.Equal(3, multi.Steps.Count);
        Assert.Equal(500, multi.DelayMs);
        Assert.Equal("${root}", multi.PrimaryPath);
        Assert.Equal(3, r.Deck.Dials.Count);
    }

    [Fact]
    public void 構文エラーは行番号付きで返す()
    {
        var r = DeckValidator.Validate("{\n  \"schema\": \"projectdeck/1\",\n  \"revision\": 1,\n}");
        Assert.False(r.Ok);
        Assert.Contains("4行目", r.Errors[0]);
    }

    [Fact]
    public void 必須項目と型の誤りを検出()
    {
        var r = DeckValidator.Validate("""
            {"schema":"projectdeck/1","revision":"1","pages":[
              {"id":"A社","buttons":[{"id":"x","row":1,"col":1,"label":"a","action":{"type":"open_folder"}}]}
            ]}
            """);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("revision") && e.Contains("整数"));
        Assert.Contains(r.Errors, e => e.Contains("英小文字"));
        Assert.Contains(r.Errors, e => e.Contains("name"));
        Assert.Contains(r.Errors, e => e.Contains("path") && e.Contains("必須"));
    }

    [Fact]
    public void open_urlはhttpかhttpsだけ()
    {
        static ValidationResult With(string url) => DeckValidator.Validate($$$"""
            {"schema":"projectdeck/1","revision":1,"pages":[{"id":"p","name":"P","buttons":[
              {"id":"p-001","row":1,"col":1,"label":"a","action":{"type":"open_url","url":"{{{url}}}"}}]}]}
            """);
        Assert.True(With("https://example.com/a?b=1").Ok);
        Assert.True(With("http://nas01/share").Ok);
        Assert.Contains(With("C:/Windows/System32/calc.exe").Errors, e => e.Contains("http(s)"));
        Assert.Contains(With("file:///C:/x.txt").Errors, e => e.Contains("http(s)"));
        Assert.Contains(With("example.com").Errors, e => e.Contains("http(s)"));
    }

    [Fact]
    public void 位置とIDの重複を検出()
    {
        var r = DeckValidator.Validate("""
            {"schema":"projectdeck/1","revision":1,"pages":[
              {"id":"p","name":"P","buttons":[
                {"id":"p-001","row":1,"col":1,"label":"a","action":{"type":"open_url","url":"https://example.com"}},
                {"id":"p-002","row":1,"col":1,"label":"b","action":{"type":"open_url","url":"https://example.com"}}]},
              {"id":"q","name":"Q","buttons":[
                {"id":"p-001","row":1,"col":1,"label":"c","action":{"type":"page","target":"p"}}]}
            ]}
            """);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("同じ位置"));
        Assert.Contains(r.Errors, e => e.Contains("重複"));
    }

    [Fact]
    public void 不明なアクション種別はエラー()
    {
        var r = DeckValidator.Validate("""
            {"schema":"projectdeck/1","revision":1,"pages":[{"id":"p","name":"P","buttons":[
              {"id":"p-001","row":1,"col":1,"label":"a","action":{"type":"launch","path":"x"}}]}]}
            """);
        Assert.Contains(r.Errors, e => e.Contains("launch"));
    }

    [Fact]
    public void 画像の指定は1種類だけ()
    {
        var r = DeckValidator.Validate("""
            {"schema":"projectdeck/1","revision":1,"pages":[{"id":"p","name":"P","buttons":[
              {"id":"p-001","row":1,"col":1,"label":"a","image":{"url":"https://x/y.png","color":"#ff0000"},
               "action":{"type":"open_url","url":"https://example.com"}}]}]}
            """);
        Assert.Contains(r.Errors, e => e.Contains("どれか1つ"));
    }

    [Fact]
    public void 未定義の変数とグリッド外は警告()
    {
        var r = DeckValidator.Validate("""
            {"schema":"projectdeck/1","revision":1,"pages":[{"id":"p","name":"P","buttons":[
              {"id":"p-001","row":4,"col":1,"label":"a","action":{"type":"open_folder","path":"${base}/x"}}]}]}
            """);
        Assert.True(r.Ok);
        Assert.Contains(r.Warnings, w => w.Contains("${base}"));
        Assert.Single(DeckValidator.GridWarnings(r.Deck!, 3, 5));
    }

    [Fact]
    public void ダイヤルの検証()
    {
        static ValidationResult V(string dials) => DeckValidator.Validate(
            "{\"schema\":\"projectdeck/1\",\"revision\":1,\"pages\":[],\"dials\":" + dials + "}");

        var ok = V("""[{"index":1,"label":"音量","function":"master_volume","step":2},{"index":2,"label":"Spotify","function":"app_volume","process":"spotify"}]""");
        Assert.True(ok.Ok, string.Join("\n", ok.Errors));
        Assert.Equal("spotify", ok.Deck!.Dials[1].Process);

        Assert.Contains(V("""[{"index":1,"function":"app_volume"}]""").Errors, e => e.Contains("process"));
        Assert.Contains(V("""[{"index":1,"function":"master_volume","step":0}]""").Errors, e => e.Contains("step"));
        Assert.Contains(V("""[{"index":5,"function":"master_volume"}]""").Errors, e => e.Contains("1～4"));
        Assert.Contains(V("""[{"index":1,"function":"master_volume"},{"index":1,"function":"mic_volume"}]""").Errors, e => e.Contains("重複"));
        Assert.Contains(V("""[{"index":1,"function":"bass"}]""").Errors, e => e.Contains("bass"));
    }
}
