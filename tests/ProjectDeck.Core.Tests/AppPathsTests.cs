using ProjectDeck.Core;

namespace ProjectDeck.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void 設定ファイルはデータフォルダの中にある()
    {
        Assert.Equal(AppPaths.DataDirectory, Path.GetDirectoryName(AppPaths.SettingsFile));
        Assert.EndsWith("ProjectDeck", AppPaths.DataDirectory);
    }
}
