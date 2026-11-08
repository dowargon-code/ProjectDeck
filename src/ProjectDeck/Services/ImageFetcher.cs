using System.Collections.Concurrent;
using ProjectDeck.Core;
using ProjectDeck.Core.Images;
using ProjectDeck.Core.Settings;
using ProjectDeck.Rendering;

namespace ProjectDeck.Services;

/// <summary>
/// deck.json の image.url の画像を取得・加工・キャッシュする（仕様書 7.1、F-12・F-13）。
/// ・キャッシュ（cache\images\）にあればそれを使い、再取得しない
/// ・無ければ別スレッドでダウンロード → 確認 → 正方形 256×256 PNG に加工 → 保存
/// ・失敗した URL は起動中は再挑戦しない（右クリック「画像を再取得」か、再起動で再挑戦）
/// 結果は Completed イベントで通知（UI スレッドではないので受け取り側で Invoke すること）。
/// </summary>
internal sealed class ImageFetcher : IDisposable
{
    private readonly ImageCache _cache;
    private readonly ConcurrentDictionary<string, Bitmap> _loaded = new();
    private readonly ConcurrentDictionary<string, string> _failures = new();
    private readonly ConcurrentDictionary<string, byte> _pending = new();
    private readonly SemaphoreSlim _parallel = new(2);
    private ImageSettings _settings;
    private HttpClient? _client;
    private readonly object _clientGate = new();

    /// <summary>1件の取得が終わった（成功・失敗とも。引数は URL）</summary>
    public event Action<string>? Completed;

    public ImageFetcher(ImageCache cache, ImageSettings settings)
    {
        _cache = cache;
        _settings = settings;
    }

    /// <summary>取得中の件数（ランプの黄点滅に使う）</summary>
    public int Pending => _pending.Count;

    /// <summary>
    /// 画像を返す。まだ無ければ取得を始めて null を返す（その間は代替アイコンで表示）
    /// </summary>
    public Bitmap? Get(string url, string? query, string? license)
    {
        if (_loaded.TryGetValue(url, out var bmp)) return bmp;
        if (_failures.ContainsKey(url)) return null;

        // キャッシュにあれば読む（256×256 の PNG なので速い）
        var path = _cache.PathFor(url);
        if (File.Exists(path))
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var img = Image.FromStream(fs);
                bmp = new Bitmap(img);
                _loaded[url] = bmp;
                return bmp;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                Logger.Warn($"キャッシュ画像を読めないため取り直します {url}: {ex.Message}");
            }
        }

        Start(url, query, license);
        return null;
    }

    /// <summary>まだ取得していない画像を先に取りにいく（今表示していないページの分）</summary>
    public void Prefetch(IEnumerable<(string Url, string? Query, string? License)> images)
    {
        foreach (var (url, query, license) in images)
        {
            if (_loaded.ContainsKey(url) || _failures.ContainsKey(url) || _cache.Contains(url)) continue;
            Start(url, query, license);
        }
    }

    /// <summary>取得に失敗した理由（失敗していなければ null）</summary>
    public string? FailureOf(string url) => _failures.TryGetValue(url, out var e) ? e : null;

    /// <summary>キャッシュを捨てて取り直す（右クリック「画像を再取得」）</summary>
    public void Retry(string url, string? query, string? license)
    {
        _failures.TryRemove(url, out _);
        if (_loaded.TryRemove(url, out var old)) old.Dispose();
        _cache.Remove(url);
        Start(url, query, license);
    }

    /// <summary>[image] の設定（タイムアウト・最大サイズ・プロキシ）が変わったとき</summary>
    public void UpdateSettings(ImageSettings settings)
    {
        lock (_clientGate)
        {
            if (settings == _settings) return;
            _settings = settings;
            _client?.Dispose();
            _client = null;
        }
        _failures.Clear(); // 設定を直したら失敗分も取り直せるように
    }

    private HttpClient Client
    {
        get
        {
            lock (_clientGate)
                return _client ??= ImageProbe.CreateClient(_settings.Proxy, _settings.DownloadTimeoutSec);
        }
    }

    private void Start(string url, string? query, string? license)
    {
        if (!_pending.TryAdd(url, 0)) return;
        _ = Task.Run(async () =>
        {
            await _parallel.WaitAsync();
            try
            {
                await FetchAsync(url, query, license);
            }
            catch (Exception ex)
            {
                // 想定外の失敗でも他の画像の取得は続ける
                _failures[url] = ex.Message;
                Logger.Warn($"画像の取得に失敗 {url}: {ex}");
            }
            finally
            {
                _parallel.Release();
                _pending.TryRemove(url, out _);
                try { Completed?.Invoke(url); } catch (Exception) { }
            }
        });
    }

    private async Task FetchAsync(string url, string? query, string? license)
    {
        var maxBytes = _settings.MaxSizeMb * 1024L * 1024L;
        var r = await ImageProbe.FetchAsync(Client, url, maxBytes);
        if (r.Transient)
        {
            // 通信の一時的な失敗は1回だけ取り直す
            await Task.Delay(1000);
            r = await ImageProbe.FetchAsync(Client, url, maxBytes);
        }
        if (!r.Ok || r.Data == null)
        {
            _failures[url] = r.Error ?? "取得できません";
            Logger.Warn($"画像を取得できません（{r.Error}）: {url}");
            return;
        }

        byte[] png;
        try
        {
            png = ImageDecoder.ToSquarePng(r.Data, ImageCache.Size);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OutOfMemoryException)
        {
            // GDI+ は壊れた画像に OutOfMemoryException を出すことがある
            _failures[url] = ex is InvalidDataException ? ex.Message : "画像を読み込めません（壊れている可能性）";
            Logger.Warn($"画像を読み込めません {url}: {ex.Message}");
            return;
        }

        _cache.Save(url, png, new ImageCacheEntry
        {
            Query = query,
            License = license,
            Fetched = DateTime.Now,
            Format = r.Format,
            Width = r.Width,
            Height = r.Height,
            Bytes = r.Bytes,
        });
        using (var ms = new MemoryStream(png))
        using (var img = Image.FromStream(ms))
            _loaded[url] = new Bitmap(img);
        _failures.TryRemove(url, out _);
        Logger.Info($"画像を取得しました（{r.Format} {r.Width}×{r.Height} {r.Bytes / 1024}KB）: {url}");
    }

    public void Dispose()
    {
        lock (_clientGate) _client?.Dispose();
        foreach (var b in _loaded.Values) b.Dispose();
        _loaded.Clear();
    }
}
