using System.Buffers.Binary;

namespace ProjectDeck.Core.Images;

/// <summary>
/// 画像ファイルの先頭部分から形式と縦横サイズを読む（画像として開かずに判定するため）。
/// 対応：PNG / JPEG / GIF / WebP / BMP / ICO（仕様書 7.1）
/// </summary>
public static class ImageHeader
{
    /// <summary>先頭バイトから画像形式を判定する。画像でなければ null</summary>
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

    /// <summary>縦横サイズを読む。読めなければ false（壊れている・未対応の書き方など）</summary>
    public static bool TryGetSize(ReadOnlySpan<byte> b, out int width, out int height)
    {
        width = height = 0;
        try
        {
            switch (DetectFormat(b))
            {
                case "png":
                    // シグネチャ(8) + 長さ(4) + "IHDR"(4) の後に幅・高さ（ビッグエンディアン）
                    if (b.Length < 24) return false;
                    width = BinaryPrimitives.ReadInt32BigEndian(b[16..]);
                    height = BinaryPrimitives.ReadInt32BigEndian(b[20..]);
                    break;
                case "gif":
                    if (b.Length < 10) return false;
                    width = BinaryPrimitives.ReadUInt16LittleEndian(b[6..]);
                    height = BinaryPrimitives.ReadUInt16LittleEndian(b[8..]);
                    break;
                case "bmp":
                    if (b.Length < 26) return false;
                    width = BinaryPrimitives.ReadInt32LittleEndian(b[18..]);
                    height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(b[22..])); // 負の値は上下反転の意味
                    break;
                case "ico":
                    return TryIco(b, out width, out height);
                case "jpeg":
                    return TryJpeg(b, out width, out height);
                case "webp":
                    return TryWebP(b, out width, out height);
                default:
                    return false;
            }
            return width > 0 && height > 0;
        }
        catch (ArgumentOutOfRangeException)
        {
            width = height = 0;
            return false;
        }
    }

    /// <summary>ICO は複数の大きさを持てるので、いちばん大きいものを返す</summary>
    private static bool TryIco(ReadOnlySpan<byte> b, out int width, out int height)
    {
        width = height = 0;
        if (b.Length < 6) return false;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(b[4..]);
        for (var i = 0; i < count; i++)
        {
            var o = 6 + i * 16;
            if (b.Length < o + 16) break;
            var w = b[o] == 0 ? 256 : b[o];       // 0 は 256 の意味
            var h = b[o + 1] == 0 ? 256 : b[o + 1];
            if (w * h > width * height) (width, height) = (w, h);
        }
        return width > 0;
    }

    /// <summary>JPEG はマーカーをたどって SOF（フレームの始まり）にある大きさを読む</summary>
    private static bool TryJpeg(ReadOnlySpan<byte> b, out int width, out int height)
    {
        width = height = 0;
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) return false;
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }                 // 詰め物
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; } // 長さを持たないマーカー
            var length = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);
            // SOF0～SOF15（C4=DHT, C8=JPG, CC=DAC は除く）
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 5)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 7)..]);
                return width > 0 && height > 0;
            }
            i += 2 + length;
        }
        return false;
    }

    /// <summary>WebP は先頭チャンク（VP8 / VP8L / VP8X）ごとに書き方が違う</summary>
    private static bool TryWebP(ReadOnlySpan<byte> b, out int width, out int height)
    {
        width = height = 0;
        if (b.Length < 30) return false;
        var chunk = System.Text.Encoding.ASCII.GetString(b[12..16]);
        switch (chunk)
        {
            case "VP8 ": // 非可逆
                width = BinaryPrimitives.ReadUInt16LittleEndian(b[26..]) & 0x3FFF;
                height = BinaryPrimitives.ReadUInt16LittleEndian(b[28..]) & 0x3FFF;
                break;
            case "VP8L": // 可逆
                if (b[20] != 0x2F) return false;
                int b0 = b[21], b1 = b[22], b2 = b[23], b3 = b[24];
                width = 1 + (b0 | ((b1 & 0x3F) << 8));
                height = 1 + ((b1 >> 6) | (b2 << 2) | ((b3 & 0x0F) << 10));
                break;
            case "VP8X": // 拡張（透過・アニメーションなど）
                width = 1 + (b[24] | (b[25] << 8) | (b[26] << 16));
                height = 1 + (b[27] | (b[28] << 8) | (b[29] << 16));
                break;
            default:
                return false;
        }
        return width > 0 && height > 0;
    }
}
