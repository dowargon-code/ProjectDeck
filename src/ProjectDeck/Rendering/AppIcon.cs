using System.Drawing.Drawing2D;
using ProjectDeck.Native;

namespace ProjectDeck.Rendering;

/// <summary>アプリのアイコン（2×2 のボタン）をその場で描いて作る。アイコンファイルを持たずに済むように</summary>
internal static class AppIcon
{
    public static Icon Create(int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var colors = new[]
            {
                Color.FromArgb(0x3d, 0x7b, 0xd9), Color.FromArgb(0xf0, 0xa0, 0x35),
                Color.FromArgb(0x2a, 0x9d, 0x5c), Color.FromArgb(0xe5, 0x53, 0x4b),
            };
            var cell = size / 2f;
            var gap = Math.Max(1f, size / 16f);
            for (var i = 0; i < 4; i++)
            {
                var rect = new RectangleF(i % 2 * cell + gap / 2, i / 2 * cell + gap / 2, cell - gap, cell - gap);
                using var path = DeckButtonRenderer.RoundedRect(rect, Math.Max(1f, size / 10f));
                using var brush = new SolidBrush(colors[i]);
                g.FillPath(brush, path);
            }
        }
        var h = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(h).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(h);
        }
    }
}
