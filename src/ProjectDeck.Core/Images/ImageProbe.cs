using System.Net;
using System.Text.Json.Nodes;

namespace ProjectDeck.Core.Images;

/// <summary>画像URLの取得結果。Data は取得した画像そのもの（失敗時は null）</summary>
public sealed record ImageProbeResult(
    bool Ok, string Url, int? Status, string? ContentType, string? Format,
    long? Bytes, int? Width, int? Height, string? Error, byte[]? Data = null, bool Transient = false)
{
    /// <summary>deckctl の出力用（画像そのものは含めない）</summary>
    public JsonObject ToJson() => new()
    {
        ["ok"] = Ok,
        ["url"] = Url,
        ["status"] = Status,
        ["content_type"] = ContentType,
        ["format"] = Format,
        ["bytes"] = Bytes,
        ["width"] = Width,
        ["height"] = Height,
        ["error"] = Error,
    };
}

/// <summary>
/// 画像URLを取得して、画像として使えるか確かめる（仕様書 7.1 のチェック）。
/// deckctl check-image・apply 時の簡易チェックと、本体の画像取得で同じ判定を使う。
/// ・HTTP 200 で取得できるか ・画像形式か（PNG/JPG/GIF/WebP/BMP/ICO） ・最大サイズ以下か ・短辺 64px 以上か
/// </summary>
public static class ImageProbe
{
    /// <summary>短辺の最小ピクセル数（これ未満はボタンに引き伸ばすと荒くなる）</summary>
    public const int MinSide = 64;

    public static HttpClient CreateClient(string proxy, int timeoutSec)
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        if (proxy == "none") handler.UseProxy = false;
        else if (proxy != "system" && Uri.TryCreate(proxy, UriKind.Absolute, out var p)) handler.Proxy = new WebProxy(p);
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        // Wikimedia などは User-Agent が無いと拒否するため付ける
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ProjectDeck/1.0 (personal launcher)");
        return client;
    }

    /// <summary>取得して確かめる。成功時は Data に画像そのものが入る</summary>
    public static async Task<ImageProbeResult> FetchAsync(HttpClient client, string url, long maxBytes, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Fail(url, "http(s) のURLではありません");
        try
        {
            using var res = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)res.StatusCode;
            var type = res.Content.Headers.ContentType?.MediaType;
            if (!res.IsSuccessStatusCode)
                return Fail(url, $"HTTP {status}", status, type);

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes)
                    return Fail(url, $"{maxBytes / 1024 / 1024}MB を超えています", status, type, bytes: buffer.Length);
            }
            return Inspect(url, buffer.ToArray(), status, type);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(url, "タイムアウトしました") with { Transient = true };
        }
        catch (HttpRequestException ex)
        {
            // 接続が切れた・名前解決できない など（もう一度試すと通ることがある）
            return Fail(url, "接続できません: " + ex.Message) with { Transient = true };
        }
    }

    /// <summary>取得済みのデータを確かめる（形式・短辺）</summary>
    public static ImageProbeResult Inspect(string url, byte[] data, int? status = null, string? contentType = null)
    {
        var format = ImageHeader.DetectFormat(data);
        if (format == null)
            return Fail(url, "画像形式ではありません（PNG/JPG/GIF/WebP/BMP/ICO）", status, contentType, bytes: data.Length);
        int? w = null, h = null;
        if (ImageHeader.TryGetSize(data, out var width, out var height))
        {
            (w, h) = (width, height);
            if (Math.Min(width, height) < MinSide)
                return new(false, url, status, contentType, format, data.Length, w, h,
                    $"画像が小さすぎます（{width}×{height}、短辺 {MinSide}px 以上が必要）");
        }
        return new(true, url, status, contentType, format, data.Length, w, h, null, data);
    }

    private static ImageProbeResult Fail(string url, string error, int? status = null, string? type = null, long? bytes = null) =>
        new(false, url, status, type, null, bytes, null, null, error);
}
