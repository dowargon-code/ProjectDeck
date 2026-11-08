using System.Buffers.Binary;
using ProjectDeck.Core.Images;

namespace ProjectDeck.Core.Tests;

public class ImageTests
{
    // ---- 各形式の先頭部分を最小限で作る ----

    private static byte[] Png(int w, int h)
    {
        var b = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), w);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), h);
        return b;
    }

    private static byte[] Gif(int w, int h)
    {
        var b = "GIF89a"u8.ToArray().Concat(new byte[7]).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)h);
        return b;
    }

    private static byte[] Bmp(int w, int h)
    {
        var b = new byte[54];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(18), w);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(22), -h); // 上から下へ並ぶ BMP は高さが負
        return b;
    }

    private static byte[] Jpeg(int w, int h)
    {
        // SOI, APP0(長さ16), SOF0
        var list = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        list.AddRange(new byte[14]);
        list.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 0x03]);
        list.AddRange(new byte[9]);
        return list.ToArray();
    }

    private static byte[] WebPX(int w, int h)
    {
        var b = new byte[30];
        "RIFF"u8.CopyTo(b); "WEBP"u8.CopyTo(b.AsSpan(8)); "VP8X"u8.CopyTo(b.AsSpan(12));
        b[24] = (byte)(w - 1); b[25] = (byte)((w - 1) >> 8); b[26] = (byte)((w - 1) >> 16);
        b[27] = (byte)(h - 1); b[28] = (byte)((h - 1) >> 8); b[29] = (byte)((h - 1) >> 16);
        return b;
    }

    private static byte[] WebPLossy(int w, int h)
    {
        var b = new byte[30];
        "RIFF"u8.CopyTo(b); "WEBP"u8.CopyTo(b.AsSpan(8)); "VP8 "u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(28), (ushort)h);
        return b;
    }

    private static byte[] Ico(params int[] sizes)
    {
        var b = new byte[6 + sizes.Length * 16];
        b[2] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)sizes.Length);
        for (var i = 0; i < sizes.Length; i++)
        {
            b[6 + i * 16] = (byte)(sizes[i] == 256 ? 0 : sizes[i]);
            b[7 + i * 16] = (byte)(sizes[i] == 256 ? 0 : sizes[i]);
        }
        return b;
    }

    public static TheoryData<string, byte[], int, int> Headers => new()
    {
        { "png", Png(800, 600), 800, 600 },
        { "gif", Gif(120, 90), 120, 90 },
        { "bmp", Bmp(256, 128), 256, 128 },
        { "jpeg", Jpeg(1024, 768), 1024, 768 },
        { "webp", WebPX(640, 480), 640, 480 },
        { "webp", WebPLossy(300, 200), 300, 200 },
        { "ico", Ico(16, 48, 256), 256, 256 },
    };

    [Theory]
    [MemberData(nameof(Headers))]
    public void 形式と大きさを読める(string format, byte[] data, int w, int h)
    {
        Assert.Equal(format, ImageHeader.DetectFormat(data));
        Assert.True(ImageHeader.TryGetSize(data, out var width, out var height));
        Assert.Equal((w, h), (width, height));
    }

    [Fact]
    public void 画像でないものは判定しない()
    {
        var html = "<!DOCTYPE html><html>"u8.ToArray();
        Assert.Null(ImageHeader.DetectFormat(html));
        Assert.False(ImageHeader.TryGetSize(html, out _, out _));
        Assert.False(ImageHeader.TryGetSize(Png(1, 1)[..10], out _, out _)); // 途中で切れている
    }

    [Fact]
    public void 短辺64px未満は不可()
    {
        Assert.True(ImageProbe.Inspect("u", Png(64, 200)).Ok);
        var small = ImageProbe.Inspect("u", Png(200, 48));
        Assert.False(small.Ok);
        Assert.Contains("小さすぎ", small.Error);
        var html = ImageProbe.Inspect("u", "<html>"u8.ToArray());
        Assert.False(html.Ok);
        Assert.Contains("画像形式ではありません", html.Error);
    }

    [Fact]
    public void キャッシュの保存と一覧()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdimg_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new ImageCache(dir);
            const string url = "https://upload.wikimedia.org/a/AE86.jpg";
            Assert.False(cache.Contains(url));
            cache.Save(url, [1, 2, 3], new ImageCacheEntry { Query = "Toyota AE86", License = "CC BY-SA 4.0", Fetched = DateTime.Now, Format = "jpeg" });
            Assert.True(cache.Contains(url));
            Assert.Matches("^[0-9a-f]{32}\\.png$", ImageCache.FileNameFor(url));
            var entry = cache.LoadIndex()[url];
            Assert.Equal("CC BY-SA 4.0", entry.License);
            Assert.Equal(ImageCache.FileNameFor(url), entry.File);
            Assert.Contains("\"license\"", File.ReadAllText(cache.IndexPath));

            cache.Remove(url);
            Assert.False(cache.Contains(url));
            Assert.Empty(cache.LoadIndex());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
