namespace ProjectDeck;

static class Program
{
    /// <summary>
    /// ランチャー本体の入口。
    /// フェーズ1でグリッド表示・deck.json 監視・常駐などを実装していく（今は空のウィンドウのみ）。
    /// </summary>
    [STAThread]
    static void Main()
    {
        // 高DPI設定や既定フォントの初期化（WinForms 標準）
        ApplicationConfiguration.Initialize();
        Application.Run(new Form { Text = "ProjectDeck", ClientSize = new Size(344, 234) });
    }
}
