using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ProjectDeck.Native;

namespace ProjectDeck.Services;

/// <summary>
/// 時間のかかる処理（パスの存在確認・関連付けアイコンの取得）を UI と別のスレッドで行う。
/// NAS 未接続などで待たされても画面が止まらないようにする。シェルの COM を使うので STA スレッドで動かす。
/// 結果は Completed イベントで通知（UI スレッドではないので受け取り側で Invoke すること）。
/// </summary>
internal sealed class ShellWorker : IDisposable
{
    private readonly BlockingCollection<string> _queue = new();
    private readonly ConcurrentDictionary<string, PathInfo> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Thread _thread;
    private readonly int _iconSize;

    /// <summary>パスの存在と関連付けアイコン</summary>
    public sealed record PathInfo(bool Exists, Bitmap? Icon);

    /// <summary>1件の確認が終わった（引数はパス）</summary>
    public event Action<string>? Completed;

    public ShellWorker(int iconSize)
    {
        _iconSize = iconSize;
        _thread = new Thread(Loop) { IsBackground = true, Name = "ShellWorker" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>結果があれば返す。無ければ確認を依頼して null を返す</summary>
    public PathInfo? Get(string path)
    {
        if (_results.TryGetValue(path, out var info)) return info;
        if (_pending.TryAdd(path, 0)) _queue.Add(path);
        return null;
    }

    /// <summary>結果を捨てて確認し直す（読み直し時・右クリック「再確認」など）</summary>
    public void Invalidate(string path)
    {
        _results.TryRemove(path, out _);
    }

    /// <summary>存在確認の結果だけ捨てる（アイコンは保持）。読み直しのたびに NAS の接続状態を見直すため</summary>
    public void RecheckAll()
    {
        foreach (var key in _results.Keys) _results.TryRemove(key, out _);
    }

    private void Loop()
    {
        foreach (var path in _queue.GetConsumingEnumerable())
        {
            var exists = Exists(path);
            var icon = exists ? LoadIcon(path, _iconSize) : null;
            _results[path] = new PathInfo(exists, icon);
            _pending.TryRemove(path, out _);
            try { Completed?.Invoke(path); } catch (Exception) { }
        }
    }

    private static bool Exists(string path)
    {
        try
        {
            // フルパスでないもの（notepad.exe など）は確認できないので「ある」とみなす
            if (!Path.IsPathRooted(path)) return true;
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>エクスプローラーと同じアイコン（Excel・フォルダなど）を取得する</summary>
    private static Bitmap? LoadIcon(string path, int size)
    {
        try
        {
            var iid = typeof(NativeMethods.IShellItemImageFactory).GUID;
            if (NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, iid, out var factory) != 0 || factory == null)
                return null;
            try
            {
                var hr = factory.GetImage(new NativeMethods.NativeSize(size, size),
                    NativeMethods.SIIGBF_ICONONLY | NativeMethods.SIIGBF_BIGGERSIZEOK, out var hbm);
                if (hr != 0 || hbm == IntPtr.Zero) return null;
                try
                {
                    return FromHBitmapWithAlpha(hbm);
                }
                finally
                {
                    NativeMethods.DeleteObject(hbm);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>HBITMAP を透明度付きの Bitmap に変換（Image.FromHbitmap だけだと透明部分が黒くなる）</summary>
    private static Bitmap FromHBitmapWithAlpha(IntPtr hbm)
    {
        using var raw = Image.FromHbitmap(hbm);
        var rect = new Rectangle(0, 0, raw.Width, raw.Height);
        var data = raw.LockBits(rect, ImageLockMode.ReadOnly, raw.PixelFormat);
        try
        {
            using var view = new Bitmap(data.Width, data.Height, data.Stride, PixelFormat.Format32bppPArgb, data.Scan0);
            var copy = new Bitmap(data.Width, data.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(copy)) g.DrawImageUnscaled(view, 0, 0);
            return copy;
        }
        finally
        {
            raw.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
    }
}
