using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectDeck.Core.Images;

/// <summary>キャッシュ一覧（index.json）の1件。出典を残すため URL・検索語・ライセンス・取得日時を記録する</summary>
public sealed class ImageCacheEntry
{
    public string File { get; set; } = "";
    public string? Query { get; set; }
    public string? License { get; set; }
    public DateTime Fetched { get; set; }
    public string? Format { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long? Bytes { get; set; }
}

/// <summary>
/// 画像キャッシュ（cache\images\）。URL のハッシュをファイル名にして 256×256 PNG で保存する（仕様書 7.1）。
/// 加工（切り取り・縮小）は本体側で行い、ここではファイルの置き場所と一覧の記録だけを扱う。
/// </summary>
public sealed class ImageCache(string directory)
{
    /// <summary>キャッシュ画像の大きさ（正方形）</summary>
    public const int Size = 256;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Directory { get; } = directory;
    public string IndexPath => Path.Combine(Directory, "index.json");

    /// <summary>URL ごとのファイル名（SHA-256 の先頭32桁）</summary>
    public static string FileNameFor(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32].ToLowerInvariant() + ".png";

    public string PathFor(string url) => Path.Combine(Directory, FileNameFor(url));

    public bool Contains(string url) => File.Exists(PathFor(url));

    /// <summary>キャッシュ一覧を読む（無い・壊れているときは空）</summary>
    public Dictionary<string, ImageCacheEntry> LoadIndex()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(IndexPath))
                    return JsonSerializer.Deserialize<Dictionary<string, ImageCacheEntry>>(File.ReadAllText(IndexPath), JsonOptions) ?? [];
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Logger.Warn("画像キャッシュ一覧を読めません: " + ex.Message);
            }
            return [];
        }
    }

    /// <summary>加工済みの PNG を保存して一覧に記録する</summary>
    public void Save(string url, byte[] png, ImageCacheEntry entry)
    {
        lock (Gate)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = PathFor(url);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, png);
            File.Move(temp, path, overwrite: true);
            entry.File = Path.GetFileName(path);
            var index = LoadIndex();
            index[url] = entry;
            WriteIndex(index);
        }
    }

    /// <summary>キャッシュから消す（「画像を再取得」用）</summary>
    public void Remove(string url)
    {
        lock (Gate)
        {
            try { File.Delete(PathFor(url)); }
            catch (IOException) { }
            var index = LoadIndex();
            if (index.Remove(url)) WriteIndex(index);
        }
    }

    private void WriteIndex(Dictionary<string, ImageCacheEntry> index)
    {
        var temp = IndexPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(index, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, IndexPath, overwrite: true);
    }
}
