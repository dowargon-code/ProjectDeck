using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ProjectDeck.Rendering;

/// <summary>
/// ダウンロードした画像を読み込み、ボタン用に加工する（仕様書 7.1「加工」）。
/// ・読込：まず GDI+（PNG/JPG/GIF/BMP/ICO）、読めなければ Windows の画像コーデック（WIC。WebP など）
/// ・加工：中央基準で正方形に切り取り → 指定の大きさ（256×256）に縮小 → PNG
/// </summary>
internal static class ImageDecoder
{
    /// <summary>読み込んで正方形に加工し、PNG のバイト列を返す。読めなければ例外</summary>
    public static byte[] ToSquarePng(byte[] data, int size)
    {
        using var source = Decode(data);
        using var square = CropSquare(source, size);
        using var ms = new MemoryStream();
        square.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>画像を読み込む（アニメーション GIF などは最初のコマ）</summary>
    public static Bitmap Decode(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true);
            return new Bitmap(img);
        }
        catch (ArgumentException)
        {
            // GDI+ が読めない形式（WebP など）
            return DecodeWithWic(data);
        }
    }

    /// <summary>
    /// Windows の画像コーデック（WIC）で読む。WebP は Windows 10 1809 以降の標準（または「WebP 画像拡張機能」）で読める。
    /// </summary>
    private static Bitmap DecodeWithWic(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(ms,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var w = bgra.PixelWidth;
            var h = bgra.PixelHeight;
            var stride = w * 4;
            var pixels = new byte[stride * h];
            bgra.CopyPixels(pixels, stride, 0);

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var lockData = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (var y = 0; y < h; y++)
                    Marshal.Copy(pixels, y * stride, lockData.Scan0 + y * lockData.Stride, stride);
            }
            finally
            {
                bmp.UnlockBits(lockData);
            }
            return bmp;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or COMException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("画像を読み込めません（この PC に対応するコーデックが無い可能性）: " + ex.Message, ex);
        }
    }

    /// <summary>中央基準で正方形に切り取り、size×size に縮小する</summary>
    public static Bitmap CropSquare(Image src, int size)
    {
        var side = Math.Min(src.Width, src.Height);
        var srcRect = new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side);
        var dst = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY); // 端のにじみ防止
        g.DrawImage(src, new Rectangle(0, 0, size, size), srcRect.X, srcRect.Y, srcRect.Width, srcRect.Height, GraphicsUnit.Pixel, attrs);
        return dst;
    }
}
