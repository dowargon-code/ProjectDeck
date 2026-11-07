using System.Net;
using System.Text.Json.Nodes;

namespace ProjectDeck.Core;

/// <summary>画像URLの確認結果</summary>
public sealed record ImageProbeResult(bool Ok, string Url, int? Status, string? ContentType, string? Format, long? Bytes, string? Error)
{
    public JsonObject ToJson() => new()
    {
        ["ok"] = Ok,
        ["url"] = Url,
        ["status"] = Status,
        ["content_type"] = ContentType,
        ["format"] = Format,
        ["bytes"] = Bytes,
        ["error"] = Error,
    };
}

/// <summary>
/// URL が画像として取得できるか確認する（deckctl check-image、apply 時の簡易チェック）。
/// HTTP 状態・形式（先頭バイトで判定）・サイズを調べる。
/// </summary>
public static class ImageProbe
{
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

    public static async Task<ImageProbeResult> CheckAsync(HttpClient client, string url, long maxBytes, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new(false, url, null, null, null, null, "http(s) のURLではありません");
        try
        {
            using var res = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)res.StatusCode;
            var type = res.Content.Headers.ContentType?.MediaType;
            if (!res.IsSuccessStatusCode)
                return new(false, url, status, type, null, null, $"HTTP {status}");

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes)
                    return new(false, url, status, type, null, buffer.Length, $"{maxBytes / 1024 / 1024}MB を超えています");
            }
            var bytes = buffer.ToArray();
            var format = DetectFormat(bytes);
            if (format == null)
                return new(false, url, status, type, null, bytes.Length, "画像形式ではありません（PNG/JPG/GIF/WebP/BMP/ICO）");
            return new(true, url, status, type, format, bytes.Length, null);
        }
        catch (TaskCanceledException)
        {
            return new(false, url, null, null, null, null, "タイムアウトしました");
        }
        catch (HttpRequestException ex)
        {
            return new(false, url, null, null, null, null, ex.Message);
        }
    }

    /// <summary>先頭バイトから画像形式を判定する</summary>
    public static string? DetectFormat(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "jpeg";
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return "gif";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "webp";
        if (b.Length >= 2 && b[0] == 'B' && b[1] == 'M') return "bmp";
        if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 1 && b[3] == 0) return "ico";
        return null;
    }
}
