using ProjectDeck.Core.Settings;

namespace ProjectDeck.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void 同梱の既定値を読める()
    {
        var r = SettingsStore.Parse(SettingsStore.DefaultText());
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        Assert.Empty(r.Warnings);
        var s = r.Settings!;
        Assert.Equal(3, s.Layout.Rows);
        Assert.Equal(5, s.Layout.Cols);
        Assert.Equal("bottom_right", s.Window.Position);
        Assert.Equal(1.0, s.Window.Opacity);
        Assert.Equal("Ctrl+Alt+Space", s.Hotkey.Toggle);
        Assert.EndsWith(@"ProjectDeck\deck.json", s.DeckPath);
    }

    [Fact]
    public void 範囲外は補正して警告_型違いはエラー()
    {
        var r = SettingsStore.Parse("[layout]\nrows = 0\ncols = 99\n");
        Assert.True(r.Ok);
        Assert.Equal(1, r.Settings!.Layout.Rows);
        Assert.Equal(12, r.Settings.Layout.Cols);
        Assert.Equal(2, r.Warnings.Count);

        var bad = SettingsStore.Parse("[layout]\nrows = \"3\"\n");
        Assert.False(bad.Ok);

        var syntax = SettingsStore.Parse("[layout\nrows = 3\n");
        Assert.False(syntax.Ok);
    }

    [Fact]
    public void 値だけ書き換えてコメントを残す()
    {
        var text = "# 先頭のコメント\n\n[layout]\nrows = 3               # 縦の数\ncols = 5\n\n[window]\ntopmost = true   # 最前面\n";
        var updated = TomlLineEditor.SetValue(text, "layout", "rows", "4");
        Assert.Equal("# 先頭のコメント\n\n[layout]\nrows = 4               # 縦の数\ncols = 5\n\n[window]\ntopmost = true   # 最前面\n", updated);

        updated = TomlLineEditor.SetValue(updated, "window", "topmost", "false");
        Assert.Contains("topmost = false  # 最前面", updated);

        // 無いキーはセクション末尾に、無いセクションはファイル末尾に追加
        updated = TomlLineEditor.SetValue(updated, "layout", "gap", "6");
        Assert.Contains("cols = 5\ngap = 6\n", updated);
        updated = TomlLineEditor.SetValue(updated, "dial", "visible", "false");
        Assert.EndsWith("[dial]\nvisible = false\n", updated);
        Assert.True(SettingsStore.Parse(updated).Ok);
    }

    [Fact]
    public void 文字列の中のシャープはコメント扱いしない()
    {
        var text = "[label]\ncolor = \"#FF0000\"  # 文字色\n";
        var updated = TomlLineEditor.SetValue(text, "label", "color", TomlLineEditor.Literal("#00FF00"));
        Assert.Equal("[label]\ncolor = \"#00FF00\"  # 文字色\n", updated);
    }

    [Fact]
    public void 設定全体の保存で既定ファイルのコメントが残る()
    {
        var text = SettingsStore.DefaultText();
        var s = SettingsStore.Parse(text).Settings!;
        s.Layout.Rows = 4;
        s.Window.Opacity = 0.85;
        s.Interface.Path = @"D:\Deck\deck.json";
        var saved = SettingsStore.Apply(text, s);
        Assert.Contains("rows = 4               # 縦の数", saved);
        Assert.Contains("opacity = 0.85", saved);
        Assert.Contains("# ProjectDeck ユーザー設定", saved);
        var reread = SettingsStore.Parse(saved).Settings!;
        Assert.Equal(4, reread.Layout.Rows);
        Assert.Equal(0.85, reread.Window.Opacity);
        Assert.Equal(@"D:\Deck\deck.json", reread.Interface.Path);
        // 変えていない行はそのまま
        Assert.Equal(text.Split('\n').Length, saved.Split('\n').Length);
    }

    [Fact]
    public void パスの展開()
    {
        var vars = new Dictionary<string, string> { ["root"] = "D:/Work/A社" };
        Assert.Equal(@"D:\Work\A社\01_仕様書", PathExpander.Expand("${root}/01_仕様書", vars));
        Assert.Equal(@"\\nas01\share\x", PathExpander.Expand("//nas01/share/x"));
        Assert.DoesNotContain("%", PathExpander.Expand("%USERPROFILE%/x"));
    }
}
