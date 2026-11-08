using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProjectDeck.Core.Deck;

/// <summary>JSON の読み書きに関する共通処理</summary>
public static partial class DeckJson
{
    /// <summary>固定のスキーマ名</summary>
    public const string SchemaName = "projectdeck/1";

    /// <summary>書き出し用の設定（日本語をエスケープせず、人が読める形で整形する）</summary>
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>1行で書き出す設定（差分判定・ログ用）</summary>
    public static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>整形して文字列にする（末尾は改行）</summary>
    public static string Serialize(JsonNode node) => node.ToJsonString(WriteOptions) + Environment.NewLine;

    /// <summary>
    /// JSON を厳密に解析する（コメント・末尾カンマは不可）。失敗時は行番号付きの日本語メッセージを返す。
    /// </summary>
    public static JsonNode? TryParse(string text, out string? error)
    {
        error = null;
        try
        {
            return JsonNode.Parse(text.TrimStart('﻿'), documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException ex)
        {
            var line = (ex.LineNumber ?? 0) + 1;
            var pos = (ex.BytePositionInLine ?? 0) + 1;
            error = $"JSONの構文エラー（{line}行目 {pos}文字目付近）: {ex.Message}";
            return null;
        }
    }

    /// <summary>空の deck.json（ファイルが無いときの初期状態）</summary>
    public static JsonObject EmptyDeck() => new()
    {
        ["schema"] = SchemaName,
        ["revision"] = 0,
        ["updated_by"] = "app",
        ["pages"] = new JsonArray(),
    };
}

/// <summary>JsonNode の補助</summary>
public static class JsonNodeExtensions
{
    /// <summary>
    /// 整数値を取り出す。解析した JSON（JsonElement）とコードで作った値（int/long）の両方に対応する。
    /// </summary>
    public static bool TryGetInt64(this JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue v || v.GetValueKind() != System.Text.Json.JsonValueKind.Number) return false;
        if (v.TryGetValue<long>(out value)) return true;
        if (v.TryGetValue<int>(out var i)) { value = i; return true; }
        return long.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
