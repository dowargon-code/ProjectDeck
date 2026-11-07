namespace ProjectDeck.Core.Deck;

// deck.json を検証したあとの「読み取り専用」のモデル（描画・実行用）。
// 書き換えは JsonNode のまま行う（未知の項目も消さずに保つため）。DeckPatcher を参照。

/// <summary>deck.json 全体</summary>
public sealed record DeckData(
    string Schema,
    long Revision,
    string? UpdatedBy,
    IReadOnlyList<DeckPage> Pages,
    IReadOnlyList<DeckDial> Dials)
{
    /// <summary>ボタンの総数（大量削除の判定に使う）</summary>
    public int ButtonCount => Pages.Sum(p => p.Buttons.Count);

    public DeckPage? FindPage(string id) => Pages.FirstOrDefault(p => p.Id == id);
}

/// <summary>ページ（案件）</summary>
public sealed record DeckPage(
    string Id,
    string Name,
    string? Color,
    IReadOnlyDictionary<string, string> Vars,
    bool Archived,
    IReadOnlyList<DeckButton> Buttons)
{
    public DeckButton? ButtonAt(int row, int col) => Buttons.FirstOrDefault(b => b.Row == row && b.Col == col);
}

/// <summary>ボタン。Json は元の定義（差分判定に使う）</summary>
public sealed record DeckButton(
    string Id,
    int Row,
    int Col,
    string Label,
    ButtonImage? Image,
    string? LabelColor,
    DeckAction Action,
    string Json);

/// <summary>ボタン画像の指定（url / file / data / color のどれか1つ）</summary>
public sealed record ButtonImage(
    string? Url,
    string? Query,
    string? License,
    string? File,
    string? Data,
    string? Color);

/// <summary>ボタンを押したときの動作</summary>
public sealed record DeckAction(
    string Type,
    string? Path,
    string? Args,
    string? Workdir,
    string? Url,
    int DelayMs,
    IReadOnlyList<DeckAction> Steps,
    string? Target)
{
    /// <summary>アイコン表示・存在確認に使う代表パス（multi は最初のパス付き手順）</summary>
    public string? PrimaryPath => Type switch
    {
        ActionTypes.OpenFolder or ActionTypes.OpenFile or ActionTypes.Run => Path,
        ActionTypes.Multi => Steps.Select(s => s.PrimaryPath).FirstOrDefault(p => p != null),
        _ => null,
    };
}

/// <summary>ダイヤル割付（フェーズ3で使用）</summary>
public sealed record DeckDial(int Index, string Label, string Function, int Step, string? Process);

/// <summary>アクション種別の定数</summary>
public static class ActionTypes
{
    public const string OpenFolder = "open_folder";
    public const string OpenFile = "open_file";
    public const string Run = "run";
    public const string OpenUrl = "open_url";
    public const string Multi = "multi";
    public const string Page = "page";

    public static readonly string[] All = [OpenFolder, OpenFile, Run, OpenUrl, Multi, Page];
}

/// <summary>ダイヤル機能の定数</summary>
public static class DialFunctions
{
    public static readonly string[] All = ["master_volume", "mic_volume", "brightness", "app_volume", "none"];
}
