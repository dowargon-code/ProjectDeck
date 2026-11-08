using System.Globalization;
using System.Reflection;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace ProjectDeck.Core.Settings;

/// <summary>settings.toml の読込結果</summary>
public sealed class SettingsLoadResult
{
    public AppSettings? Settings { get; set; }
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool Ok => Errors.Count == 0 && Settings != null;
}

/// <summary>
/// settings.toml の読み書き。
/// 読込は Tomlyn、書込は TomlLineEditor（コメント・並び順を保つため値だけ置き換える）。
/// </summary>
public sealed class SettingsStore(string path)
{
    public string SettingsPath { get; } = path;

    /// <summary>同梱の既定 settings.toml（config\settings.toml を埋め込んだもの）</summary>
    public static string DefaultText()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("settings.default.toml")!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>
    /// settings.toml が無ければ作る。exe の隣に原本があればそれを、無ければ埋め込みの既定値をコピーする。
    /// </summary>
    public void EnsureExists(string? bundledPath = null)
    {
        if (File.Exists(SettingsPath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(SettingsPath))!);
        if (bundledPath != null && File.Exists(bundledPath)) File.Copy(bundledPath, SettingsPath);
        else File.WriteAllText(SettingsPath, DefaultText(), new UTF8Encoding(false));
    }

    public string? ReadText()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(SettingsPath)) return null;
                using var fs = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8, true);
                return sr.ReadToEnd();
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>読み込む。ファイルが無ければ既定値</summary>
    public SettingsLoadResult Load()
    {
        var text = ReadText();
        return text == null ? Parse(DefaultText()) : Parse(text);
    }

    /// <summary>
    /// 設定の全項目を書き戻す（変わった値の行だけが変わり、コメントは残る）。
    /// </summary>
    public string Save(AppSettings s)
    {
        var text = ReadText() ?? DefaultText();
        text = Apply(text, s);
        WriteAtomic(text);
        return text;
    }

    /// <summary>1項目だけ書き戻す（📌・◎ボタンの状態保存用）</summary>
    public string SetValue(string section, string key, string literal)
    {
        var text = ReadText() ?? DefaultText();
        text = TomlLineEditor.SetValue(text, section, key, literal);
        WriteAtomic(text);
        return text;
    }

    /// <summary>設定内容を TOML テキストに反映する（ファイルには書かない）</summary>
    public static string Apply(string text, AppSettings s)
    {
        static string L<T>(T v) => v switch
        {
            bool b => TomlLineEditor.Literal(b),
            int i => TomlLineEditor.Literal(i),
            double d => TomlLineEditor.Literal(d),
            _ => TomlLineEditor.Literal(Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""),
        };
        string Set(string t, string sec, string key, string lit) => TomlLineEditor.SetValue(t, sec, key, lit);

        // 値が変わらない項目は書き換えない（表記の揺れ 1.0 / 1 などを保つ）
        var old = Parse(text).Settings ?? new AppSettings();
        void SetIf<T>(string sec, string key, T before, T after, Func<T, string> lit)
        {
            if (!EqualityComparer<T>.Default.Equals(before, after)) text = Set(text, sec, key, lit(after));
        }

        SetIf("layout", "rows", old.Layout.Rows, s.Layout.Rows, L);
        SetIf("layout", "cols", old.Layout.Cols, s.Layout.Cols, L);
        SetIf("layout", "button_size", old.Layout.ButtonSize, s.Layout.ButtonSize, L);
        SetIf("layout", "gap", old.Layout.Gap, s.Layout.Gap, L);
        SetIf("layout", "corner_radius", old.Layout.CornerRadius, s.Layout.CornerRadius, L);

        SetIf("window", "position", old.Window.Position, s.Window.Position, L);
        SetIf("window", "monitor", old.Window.Monitor, s.Window.Monitor, L);
        SetIf("window", "margin", old.Window.Margin, s.Window.Margin, L);
        SetIf("window", "topmost", old.Window.Topmost, s.Window.Topmost, L);
        SetIf("window", "opacity", old.Window.Opacity, s.Window.Opacity, L);
        SetIf("window", "hide_after_launch", old.Window.HideAfterLaunch, s.Window.HideAfterLaunch, L);

        SetIf("startup", "auto_start", old.Startup.AutoStart, s.Startup.AutoStart, L);
        SetIf("startup", "show_on_start", old.Startup.ShowOnStart, s.Startup.ShowOnStart, L);
        SetIf("startup", "delay_sec", old.Startup.DelaySec, s.Startup.DelaySec, L);

        SetIf("dial", "visible", old.Dial.Visible, s.Dial.Visible, L);

        SetIf("label", "font", old.Label.Font, s.Label.Font, L);
        SetIf("label", "size", old.Label.Size, s.Label.Size, v => v == Math.Floor(v) ? ((int)v).ToString(CultureInfo.InvariantCulture) : L(v));
        SetIf("label", "color", old.Label.Color, s.Label.Color, L);

        SetIf("hotkey", "toggle", old.Hotkey.Toggle, s.Hotkey.Toggle, L);

        SetIf("interface", "path", old.Interface.Path, s.Interface.Path, L);
        SetIf("interface", "debounce_ms", old.Interface.DebounceMs, s.Interface.DebounceMs, L);
        SetIf("interface", "history", old.Interface.History, s.Interface.History, L);

        SetIf("image", "download_timeout_sec", old.Image.DownloadTimeoutSec, s.Image.DownloadTimeoutSec, L);
        SetIf("image", "max_size_mb", old.Image.MaxSizeMb, s.Image.MaxSizeMb, L);
        SetIf("image", "proxy", old.Image.Proxy, s.Image.Proxy, L);
        return text;
    }

    private void WriteAtomic(string text)
    {
        var full = Path.GetFullPath(SettingsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, full, overwrite: true);
    }

    // ---------------- 解析 ----------------

    /// <summary>TOML テキストを解析する。範囲外の値は補正して警告、型違いはエラー</summary>
    public static SettingsLoadResult Parse(string text)
    {
        var r = new SettingsLoadResult();
        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(text.TrimStart('﻿'))!;
        }
        catch (TomlException ex)
        {
            r.Errors.Add("settings.toml の構文エラー: " + ex.Message);
            return r;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            r.Errors.Add("settings.toml の構文エラー: " + ex.Message);
            return r;
        }

        var s = new AppSettings();
        var reader = new Reader(root, r);

        s.Layout.Rows = reader.Int("layout", "rows", s.Layout.Rows, SettingsLimits.Rows);
        s.Layout.Cols = reader.Int("layout", "cols", s.Layout.Cols, SettingsLimits.Cols);
        s.Layout.ButtonSize = reader.Int("layout", "button_size", s.Layout.ButtonSize, SettingsLimits.ButtonSize);
        s.Layout.Gap = reader.Int("layout", "gap", s.Layout.Gap, SettingsLimits.Gap);
        s.Layout.CornerRadius = reader.Int("layout", "corner_radius", s.Layout.CornerRadius, SettingsLimits.CornerRadius);

        s.Window.Position = reader.Str("window", "position", s.Window.Position);
        if (!SettingsLimits.Positions.Contains(s.Window.Position))
        {
            r.Warnings.Add($"window.position \"{s.Window.Position}\" は使えません（top_left / top_right / bottom_right）。bottom_right にします");
            s.Window.Position = "bottom_right";
        }
        s.Window.Monitor = reader.StrOrInt("window", "monitor", s.Window.Monitor);
        s.Window.Margin = reader.Int("window", "margin", s.Window.Margin, SettingsLimits.Margin);
        s.Window.Topmost = reader.Bool("window", "topmost", s.Window.Topmost);
        s.Window.Opacity = reader.Double("window", "opacity", s.Window.Opacity, SettingsLimits.Opacity);
        s.Window.HideAfterLaunch = reader.Bool("window", "hide_after_launch", s.Window.HideAfterLaunch);

        s.Startup.AutoStart = reader.Bool("startup", "auto_start", s.Startup.AutoStart);
        s.Startup.ShowOnStart = reader.Bool("startup", "show_on_start", s.Startup.ShowOnStart);
        s.Startup.DelaySec = reader.Int("startup", "delay_sec", s.Startup.DelaySec, SettingsLimits.DelaySec);

        s.Dial.Visible = reader.Bool("dial", "visible", s.Dial.Visible);

        s.Label.Font = reader.Str("label", "font", s.Label.Font);
        s.Label.Size = reader.Double("label", "size", s.Label.Size, SettingsLimits.LabelSize);
        s.Label.Color = reader.Str("label", "color", s.Label.Color);

        s.Hotkey.Toggle = reader.Str("hotkey", "toggle", s.Hotkey.Toggle);

        s.Interface.Path = reader.Str("interface", "path", s.Interface.Path);
        s.Interface.DebounceMs = reader.Int("interface", "debounce_ms", s.Interface.DebounceMs, SettingsLimits.DebounceMs);
        s.Interface.History = reader.Int("interface", "history", s.Interface.History, SettingsLimits.History);

        s.Image.DownloadTimeoutSec = reader.Int("image", "download_timeout_sec", s.Image.DownloadTimeoutSec, SettingsLimits.DownloadTimeoutSec);
        s.Image.MaxSizeMb = reader.Int("image", "max_size_mb", s.Image.MaxSizeMb, SettingsLimits.MaxSizeMb);
        s.Image.Proxy = reader.Str("image", "proxy", s.Image.Proxy);

        if (r.Errors.Count == 0) r.Settings = s;
        return r;
    }

    /// <summary>TomlTable から型を確かめながら値を取り出す</summary>
    private sealed class Reader(TomlTable root, SettingsLoadResult r)
    {
        private object? Get(string section, string key) =>
            root.TryGetValue(section, out var sec) && sec is TomlTable t && t.TryGetValue(key, out var v) ? v : null;

        public int Int(string section, string key, int def, (int Min, int Max) range)
        {
            var v = Get(section, key);
            if (v == null) return def;
            if (v is not long n)
            {
                r.Errors.Add($"{section}.{key}: 整数で指定してください");
                return def;
            }
            if (n < range.Min || n > range.Max)
            {
                var clamped = (int)Math.Clamp(n, range.Min, range.Max);
                r.Warnings.Add($"{section}.{key} = {n} は範囲外です（{range.Min}～{range.Max}）。{clamped} にします");
                return clamped;
            }
            return (int)n;
        }

        public double Double(string section, string key, double def, (double Min, double Max) range)
        {
            var v = Get(section, key);
            if (v == null) return def;
            double d;
            if (v is double x) d = x;
            else if (v is long n) d = n;
            else
            {
                r.Errors.Add($"{section}.{key}: 数値で指定してください");
                return def;
            }
            if (d < range.Min || d > range.Max)
            {
                var clamped = Math.Clamp(d, range.Min, range.Max);
                r.Warnings.Add($"{section}.{key} = {d} は範囲外です（{range.Min}～{range.Max}）。{clamped} にします");
                return clamped;
            }
            return d;
        }

        public bool Bool(string section, string key, bool def)
        {
            var v = Get(section, key);
            if (v == null) return def;
            if (v is bool b) return b;
            r.Errors.Add($"{section}.{key}: true / false で指定してください");
            return def;
        }

        public string Str(string section, string key, string def)
        {
            var v = Get(section, key);
            if (v == null) return def;
            if (v is string s) return s;
            r.Errors.Add($"{section}.{key}: 文字列（\"～\"）で指定してください");
            return def;
        }

        public string StrOrInt(string section, string key, string def)
        {
            var v = Get(section, key);
            return v switch
            {
                null => def,
                string s => s,
                long n => n.ToString(CultureInfo.InvariantCulture),
                _ => Str(section, key, def),
            };
        }
    }
}
