using ProjectDeck.Native;

namespace ProjectDeck.Services;

/// <summary>ホットキーの文字列表記（"Ctrl+Alt+Space" など）と Win32 の値の相互変換</summary>
internal static class Hotkey
{
    /// <summary>文字列を解析する。空文字は「設定なし」として false</summary>
    public static bool TryParse(string? text, out uint modifiers, out Keys key)
    {
        modifiers = 0;
        key = Keys.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= NativeMethods.MOD_CONTROL; break;
                case "alt": modifiers |= NativeMethods.MOD_ALT; break;
                case "shift": modifiers |= NativeMethods.MOD_SHIFT; break;
                case "win" or "windows": modifiers |= NativeMethods.MOD_WIN; break;
                default:
                    if (key != Keys.None) return false;
                    if (raw.Length == 1 && char.IsDigit(raw[0])) key = Keys.D0 + (raw[0] - '0');
                    else if (!Enum.TryParse(raw, true, out key)) return false;
                    break;
            }
        }
        return key != Keys.None && modifiers != 0;
    }

    /// <summary>KeyDown の値を表記にする（設定ダイアログのキー入力用）。修飾キーだけなら null</summary>
    public static string? Format(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.None) return null;
        var parts = new List<string>();
        if (keyData.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (keyData.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (keyData.HasFlag(Keys.Shift)) parts.Add("Shift");
        if (parts.Count == 0) return null;
        parts.Add(key is >= Keys.D0 and <= Keys.D9 ? ((char)('0' + (key - Keys.D0))).ToString() : key.ToString());
        return string.Join("+", parts);
    }
}
