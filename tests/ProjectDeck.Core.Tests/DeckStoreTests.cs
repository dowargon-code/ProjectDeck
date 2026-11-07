using ProjectDeck.Core.Deck;

namespace ProjectDeck.Core.Tests;

public sealed class DeckStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdtest_" + Guid.NewGuid().ToString("N"));
    private readonly DeckStore _store;

    public DeckStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new DeckStore(Path.Combine(_dir, "deck.json"), Path.Combine(_dir, "history"), 3);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string AddPatch(string id, int row, int col) => $$$"""
        {"schema":"projectdeck/1","mode":"merge","updated_by":"test","pages":[{"id":"p","name":"P","buttons":[
          {"id":"{{{id}}}","row":{{{row}}},"col":{{{col}}},"label":"{{{id}}}","action":{"type":"open_url","url":"https://example.com"}}]}]}
        """;

    [Fact]
    public void ファイルが無い状態から適用して書き込む()
    {
        var o = _store.ApplyPatch(AddPatch("p-001", 1, 1));
        Assert.True(o.Ok, string.Join("\n", o.Errors));
        Assert.Equal(0, o.ExitCode);
        Assert.Equal(1, o.Revision);
        Assert.True(_store.ValidateCurrent().Ok);
        Assert.Empty(_store.HistoryFiles()); // 元が無いのでバックアップは無し
    }

    [Fact]
    public void dry_runは書き込まない()
    {
        var o = _store.ApplyPatch(AddPatch("p-001", 1, 1), dryRun: true);
        Assert.True(o.Ok);
        Assert.False(File.Exists(_store.DeckPath));
    }

    [Fact]
    public void 検証エラーならファイルを変更しない()
    {
        _store.ApplyPatch(AddPatch("p-001", 1, 1));
        var before = File.ReadAllText(_store.DeckPath);
        var o = _store.ApplyPatch(AddPatch("p-002", 1, 1)); // 同じ位置
        Assert.False(o.Ok);
        Assert.Equal(1, o.ExitCode);
        Assert.Equal(before, File.ReadAllText(_store.DeckPath));
    }

    [Fact]
    public void 履歴と元に戻す()
    {
        _store.ApplyPatch(AddPatch("p-001", 1, 1));
        _store.ApplyPatch(AddPatch("p-002", 1, 2));
        Assert.Single(_store.HistoryFiles());
        var o = _store.Undo();
        Assert.True(o.Ok, string.Join("\n", o.Errors));
        var deck = _store.ValidateCurrent().Deck!;
        Assert.Single(deck.Pages[0].Buttons);
        Assert.Equal(3, deck.Revision); // 戻しても revision は増える
    }

    [Fact]
    public void 履歴は指定世代数まで()
    {
        for (var i = 1; i <= 6; i++)
        {
            _store.ApplyPatch(AddPatch($"p-{i:D3}", 1, i));
            Thread.Sleep(5);
        }
        Assert.Equal(3, _store.HistoryFiles().Count);
    }

    [Fact]
    public void 大量削除はforceが必要()
    {
        for (var i = 1; i <= 5; i++) _store.ApplyPatch(AddPatch($"p-{i:D3}", 1, i));
        var del = """
            {"schema":"projectdeck/1","mode":"merge","pages":[{"id":"p","buttons":[
              {"id":"p-001","delete":true},{"id":"p-002","delete":true},{"id":"p-003","delete":true},
              {"id":"p-004","delete":true},{"id":"p-005","delete":true}]}]}
            """;
        var o = _store.ApplyPatch(del);
        Assert.False(o.Ok);
        Assert.True(o.NeedsForce);
        Assert.True(_store.ApplyPatch(del, force: true).Ok);
    }

    [Fact]
    public void ロック中は終了コード2()
    {
        using (_store.AcquireLock())
        {
            var o = _store.ApplyPatch(AddPatch("p-001", 1, 1));
            Assert.Equal(2, o.ExitCode);
        }
    }

    [Fact]
    public void 次のボタンIDと空き位置()
    {
        _store.ApplyPatch(AddPatch("p-001", 1, 1));
        _store.ApplyPatch(AddPatch("p-007", 1, 3));
        var deck = _store.ValidateCurrent().Deck!;
        var page = deck.Pages[0];
        Assert.Equal("p-008", DeckInfo.NextButtonId(deck, page));
        var free = DeckInfo.FreeCells(page, 2, 3);
        Assert.Equal((1, 2), free[0]);
        Assert.Equal(4, free.Count);
    }
}
