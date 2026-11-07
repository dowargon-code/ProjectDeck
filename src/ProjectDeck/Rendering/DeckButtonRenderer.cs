using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using ProjectDeck.Core.Deck;

namespace ProjectDeck.Rendering;

/// <summary>ボタン1個を描くのに必要な情報</summary>
internal sealed record ButtonVisual(
    DeckButton Button,
    string? PageColor,
    Image? Picture,       // image.file / image.data の画像（全面に表示）
    Bitmap? ShellIcon,    // 関連付けアイコン（画像が無いとき中央に表示）
    bool Missing);        // 対象パスが存在しない

/// <summary>ラベルの見た目（settings.toml の [label]）</summary>
internal sealed record LabelStyle(string Font, double SizePt, string Color);

/// <summary>
/// ボタンの画像を作る（仕様書 5.2）。
/// 画像は全面（cover）・角丸、文字は下部に薄い黒グラデーションを敷いて縁取り付きで重ねる。
/// 描画時は貼るだけで済むよう、ボタンサイズのビットマップを事前に作っておく。
/// </summary>
internal static class DeckButtonRenderer
{
    private static readonly Color DefaultBgTop = Color.FromArgb(0x4a, 0x5d, 0x7e);
    private static readonly Color DefaultBgBottom = Color.FromArgb(0x1c, 0x24, 0x33);

    /// <summary>ボタン画像を作る。size・radius・フォントはスケール済みのピクセル値</summary>
    public static Bitmap Render(ButtonVisual v, int size, int radius, LabelStyle style, float scale)
    {
        // 1) 中身を正方形に描く
        using var content = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(content))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            DrawBackground(g, v, size);

            // 2) 下部に薄い黒グラデーション
            var shadeTop = size * 36 / 64;
            using (var shade = new LinearGradientBrush(new Rectangle(0, shadeTop - 1, size, size - shadeTop + 2),
                       Color.FromArgb(0, 0, 0, 0), Color.FromArgb(166, 0, 0, 0), LinearGradientMode.Vertical))
            {
                g.FillRectangle(shade, 0, shadeTop, size, size - shadeTop);
            }

            // 3) 文字
            var color = v.Button.LabelColor ?? style.Color;
            DrawLabel(g, content, v.Button.Label, size, style, color, scale);

            // 4) パスが無い（NAS未接続など）→ 暗くして ⚠
            if (v.Missing)
            {
                using var dark = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
                g.FillRectangle(dark, 0, 0, size, size);
                DrawWarningMark(g, size);
            }
        }

        // 5) 角丸で切り抜く（アンチエイリアスを効かせるためテクスチャで塗る）
        var result = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(result))
        using (var path = RoundedRect(new RectangleF(0, 0, size, size), radius))
        using (var brush = new TextureBrush(content))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.FillPath(brush, path);
        }
        return result;
    }

    // ---------------- 背景 ----------------

    private static void DrawBackground(Graphics g, ButtonVisual v, int size)
    {
        var rect = new Rectangle(0, 0, size, size);
        if (v.Picture != null)
        {
            DrawCover(g, v.Picture, rect);
            return;
        }

        var img = v.Button.Image;
        if (img?.Color != null && TryParseColor(img.Color, out var solid))
        {
            // 単色背景＋文字アイコン
            FillGradient(g, rect, solid, Darken(solid, 0.45f));
            DrawLetter(g, v.Button.Label, size);
            return;
        }

        if (v.ShellIcon != null)
        {
            // 関連付けアイコンを中央（文字の分だけ少し上）に大きく
            var (top, bottom) = PageGradient(v.PageColor);
            FillGradient(g, rect, top, bottom);
            var iconSize = (int)(size * 0.56f);
            var x = (size - iconSize) / 2;
            var y = (int)(size * 0.40f) - iconSize / 2;
            g.DrawImage(v.ShellIcon, new Rectangle(x, Math.Max(2, y), iconSize, iconSize));
            return;
        }

        // 文字アイコン（ラベルの頭文字＋ラベルから決めた色）
        var baseColor = ColorFromText(v.Button.Label);
        FillGradient(g, rect, baseColor, Darken(baseColor, 0.5f));
        DrawLetter(g, v.Button.Label, size);
    }

    /// <summary>縦横比を保って全面を埋める（はみ出しは切り取り）</summary>
    private static void DrawCover(Graphics g, Image img, Rectangle dest)
    {
        var scale = Math.Max((float)dest.Width / img.Width, (float)dest.Height / img.Height);
        var srcW = dest.Width / scale;
        var srcH = dest.Height / scale;
        var src = new RectangleF((img.Width - srcW) / 2, (img.Height - srcH) / 2, srcW, srcH);
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY); // 端のにじみ防止
        g.DrawImage(img, dest, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
    }

    private static void DrawLetter(Graphics g, string label, int size)
    {
        var letter = FirstLetter(label);
        if (letter.Length == 0) return;
        using var font = new Font("Segoe UI", size * 0.38f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(225, 255, 255, 255));
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(letter, font, brush, new RectangleF(0, 0, size, size * 0.80f), sf);
    }

    private static string FirstLetter(string label)
    {
        var t = label.Trim();
        if (t.Length == 0) return "";
        var e = StringInfo.GetTextElementEnumerator(t);
        e.MoveNext();
        return e.GetTextElement().ToUpperInvariant();
    }

    // ---------------- 文字 ----------------

    private static void DrawLabel(Graphics g, Bitmap canvas, string label, int size, LabelStyle style, string color, float scale)
    {
        if (string.IsNullOrWhiteSpace(label)) return;

        var maxWidth = size - 4 * scale;
        var sizePx = (float)(style.SizePt * 96 / 72) * scale;
        var minPx = 7f * 96 / 72 * scale;
        FontFamily family;
        try { family = new FontFamily(style.Font); }
        catch (ArgumentException) { family = new FontFamily("Meiryo UI"); }

        using (family)
        using (var sf = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap })
        {
            // はみ出す場合は文字を縮小（下限 7pt）→ それでも入らなければ末尾「…」
            var text = label;
            var width = Measure(g, text, family, sizePx, sf);
            while (width > maxWidth && sizePx > minPx)
            {
                sizePx = Math.Max(minPx, sizePx - 0.5f * scale);
                width = Measure(g, text, family, sizePx, sf);
            }
            if (width > maxWidth)
            {
                var elements = SplitElements(label);
                for (var n = elements.Count - 1; n > 0; n--)
                {
                    text = string.Concat(elements.Take(n)) + "…";
                    width = Measure(g, text, family, sizePx, sf);
                    if (width <= maxWidth) break;
                }
            }

            var (fill, outline) = LabelColors(color, canvas, size);
            var baseline = size - 6 * scale;
            using var path = new GraphicsPath();
            var ascent = sizePx * family.GetCellAscent(FontStyle.Bold) / family.GetEmHeight(FontStyle.Bold);
            var x = (size - width) / 2f;
            path.AddString(text, family, (int)FontStyle.Bold, sizePx, new PointF(x, baseline - ascent), sf);

            using var pen = new Pen(outline, 2.6f * scale) { LineJoin = LineJoin.Round };
            using var brush = new SolidBrush(fill);
            g.DrawPath(pen, path);
            g.FillPath(brush, path);
        }
    }

    private static float Measure(Graphics g, string text, FontFamily family, float px, StringFormat sf)
    {
        using var font = new Font(family, px, FontStyle.Bold, GraphicsUnit.Pixel);
        return g.MeasureString(text, font, int.MaxValue, sf).Width;
    }

    private static List<string> SplitElements(string s)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add(e.GetTextElement());
        return list;
    }

    /// <summary>文字色と縁取り色。auto は画像下部の明るさで白文字（黒縁）／黒文字（白縁）を選ぶ</summary>
    private static (Color fill, Color outline) LabelColors(string color, Bitmap canvas, int size)
    {
        switch (color)
        {
            case "white": return (Color.White, Color.Black);
            case "black": return (Color.Black, Color.White);
            case "auto":
                return BottomLuminance(canvas, size) > 0.62 ? (Color.Black, Color.White) : (Color.White, Color.Black);
            default:
                if (TryParseColor(color, out var c))
                    return (c, Luminance(c) > 0.5 ? Color.Black : Color.White);
                return (Color.White, Color.Black);
        }
    }

    /// <summary>ボタン下部（文字が乗る部分）の平均の明るさ 0～1</summary>
    private static double BottomLuminance(Bitmap bmp, int size)
    {
        double sum = 0;
        var n = 0;
        var step = Math.Max(1, size / 16);
        for (var y = size * 3 / 4; y < size; y += step)
            for (var x = 0; x < size; x += step)
            {
                sum += Luminance(bmp.GetPixel(x, y));
                n++;
            }
        return n == 0 ? 0 : sum / n;
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static void DrawWarningMark(Graphics g, int size)
    {
        var r = size * 0.13f;
        var cx = size - r - size * 0.06f;
        var cy = r + size * 0.06f;
        var tri = new[] { new PointF(cx, cy - r), new PointF(cx + r, cy + r * 0.8f), new PointF(cx - r, cy + r * 0.8f) };
        using var fill = new SolidBrush(Color.FromArgb(0xff, 0xd4, 0x00));
        g.FillPolygon(fill, tri);
        using var pen = new Pen(Color.FromArgb(40, 40, 40), Math.Max(1.5f, r * 0.22f));
        g.DrawLine(pen, cx, cy - r * 0.45f, cx, cy + r * 0.25f);
        g.DrawLine(pen, cx, cy + r * 0.48f, cx, cy + r * 0.58f);
    }

    // ---------------- 色・形の補助 ----------------

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void FillGradient(Graphics g, Rectangle rect, Color top, Color bottom)
    {
        using var brush = new LinearGradientBrush(rect, top, bottom, 45f);
        g.FillRectangle(brush, rect);
    }

    private static (Color, Color) PageGradient(string? pageColor)
    {
        if (pageColor != null && TryParseColor(pageColor, out var c))
            return (Darken(c, 0.35f), Darken(c, 0.78f));
        return (DefaultBgTop, DefaultBgBottom);
    }

    public static bool TryParseColor(string? s, out Color color)
    {
        color = Color.Empty;
        if (s is not { Length: 7 } || s[0] != '#') return false;
        if (!int.TryParse(s.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = Color.FromArgb((v >> 16) & 0xff, (v >> 8) & 0xff, v & 0xff);
        return true;
    }

    public static Color Darken(Color c, float amount) =>
        Color.FromArgb(c.A, (int)(c.R * (1 - amount)), (int)(c.G * (1 - amount)), (int)(c.B * (1 - amount)));

    /// <summary>文字列から決まった色（文字アイコン用。同じラベルは毎回同じ色）</summary>
    public static Color ColorFromText(string text)
    {
        uint h = 2166136261;
        foreach (var ch in text) h = (h ^ ch) * 16777619;
        var hue = h % 360;
        return FromHsv(hue, 0.55, 0.78);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }
}
