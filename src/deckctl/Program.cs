using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using ProjectDeck.Core;
using ProjectDeck.Core.Deck;
using ProjectDeck.Core.Settings;

// deckctl：Grok のローカル実行（PowerShell）から deck.json を安全に取得・更新するコマンド（仕様書 6.3）。
// 出力はすべて JSON（標準出力）。終了コード 0：成功／1：検証エラー（deck.json 変更なし）／2：ファイル競合・読み書き失敗
//
//   deckctl get                         全ページの一覧
//   deckctl get --page <ID>             指定ページの定義と _info（行列数・空き位置・次のボタンID）
//   deckctl apply <パッチ> [--dry-run] [--force]
//   deckctl validate                    現在の deck.json を検証
//   deckctl undo                        1つ前の履歴に戻す
//   deckctl check-image <URL>           URL が画像として取得できるか確認
// 共通オプション：--deck <deck.jsonのパス>（settings.toml の指定より優先）

Console.OutputEncoding = new UTF8Encoding(false);
Logger.Init(AppPaths.LogsDirectory, "deckctl");

try
{
    return await DeckCtl.RunAsync(args);
}
catch (Exception ex)
{
    Logger.Error("予期しないエラー: " + ex);
    DeckCtl.Write(new JsonObject { ["ok"] = false, ["errors"] = new JsonArray("予期しないエラー: " + ex.Message) });
    return 2;
}

internal static class DeckCtl
{
    public static async Task<int> RunAsync(string[] args)
    {
        var opts = Options.Parse(args);
        if (opts.Command is null or "help" or "--help" or "-h")
        {
            Write(Help());
            return 0;
        }
        if (opts.Command is "version" or "--version")
        {
            Write(new JsonObject { ["ok"] = true, ["version"] = Version });
            return 0;
        }

        var settings = new SettingsStore(AppPaths.SettingsFile).Load();
        var s = settings.Settings ?? new AppSettings();
        var deckPath = opts.Get("--deck") ?? s.DeckPath;
        var store = new DeckStore(deckPath, AppPaths.HistoryDirectory, s.Interface.History);
        var warnings = new List<string>();
        if (!settings.Ok) warnings.Add("settings.toml を読めないため既定値を使います: " + string.Join(" / ", settings.Errors));

        switch (opts.Command)
        {
            case "get":
                return Get(store, s, opts.Get("--page"), warnings);
            case "validate":
                return Validate(store, s, warnings);
            case "apply":
                return await ApplyAsync(store, s, opts, warnings);
            case "undo":
                return Finish(store.Undo(), warnings, "undo");
            case "check-image":
                if (opts.Positional.Count < 1) return Error(1, "URL を指定してください（deckctl check-image <URL>）");
                using (var client = ImageProbe.CreateClient(s.Image.Proxy, s.Image.DownloadTimeoutSec))
                {
                    var r = await ImageProbe.CheckAsync(client, opts.Positional[0], s.Image.MaxSizeMb * 1024L * 1024L);
                    Write(r.ToJson());
                    return r.Ok ? 0 : 1;
                }
            default:
                return Error(1, $"不明なコマンドです: {opts.Command}（deckctl help で一覧）");
        }
    }

    private static int Get(DeckStore store, AppSettings s, string? pageId, List<string> warnings)
    {
        var v = store.ValidateCurrent();
        if (!v.Ok) return Error(1, ["現在の deck.json が正しくありません", .. v.Errors]);
        var rows = s.Layout.Rows;
        var cols = s.Layout.Cols;
        JsonObject output;
        if (pageId == null)
        {
            output = DeckInfo.PageList(v.Deck!, rows, cols);
        }
        else
        {
            var detail = DeckInfo.PageDetail(v.Root!, v.Deck!, pageId, rows, cols);
            if (detail == null)
            {
                var ids = string.Join(", ", v.Deck!.Pages.Select(p => $"{p.Id}（{p.Name}）"));
                return Error(1, $"ページ \"{pageId}\" がありません。あるページ: {(ids.Length > 0 ? ids : "なし")}");
            }
            output = new JsonObject { ["ok"] = true };
            foreach (var (k, val) in detail) output[k] = val?.DeepClone();
        }
        var allWarnings = warnings.Concat(v.Warnings).Concat(DeckValidator.GridWarnings(v.Deck!, rows, cols)).ToList();
        if (allWarnings.Count > 0) output["warnings"] = ToArray(allWarnings);
        Write(output);
        return 0;
    }

    private static int Validate(DeckStore store, AppSettings s, List<string> warnings)
    {
        var exists = File.Exists(store.DeckPath);
        var v = store.ValidateCurrent();
        var o = new JsonObject
        {
            ["ok"] = v.Ok,
            ["path"] = store.DeckPath,
            ["exists"] = exists,
        };
        if (v.Deck != null)
        {
            o["revision"] = v.Deck.Revision;
            o["pages"] = v.Deck.Pages.Count;
            o["buttons"] = v.Deck.ButtonCount;
            warnings.AddRange(DeckValidator.GridWarnings(v.Deck, s.Layout.Rows, s.Layout.Cols));
        }
        o["warnings"] = ToArray(warnings.Concat(v.Warnings));
        if (v.Errors.Count > 0) o["errors"] = ToArray(v.Errors);
        Write(o);
        return v.Ok ? 0 : 1;
    }

    private static async Task<int> ApplyAsync(DeckStore store, AppSettings s, Options opts, List<string> warnings)
    {
        if (opts.Positional.Count < 1) return Error(1, "パッチファイルを指定してください（deckctl apply <パッチ>）");
        var patchPath = Path.GetFullPath(PathExpander.Expand(opts.Positional[0]));
        if (!File.Exists(patchPath)) return Error(2, $"パッチファイルがありません: {patchPath}");

        string patchText;
        try
        {
            // UTF-8（BOMあり・なし両対応）
            patchText = File.ReadAllText(patchPath, Encoding.UTF8);
        }
        catch (IOException ex)
        {
            return Error(2, "パッチを読めません: " + ex.Message);
        }

        var before = store.ValidateCurrent();
        var dryRun = opts.Has("--dry-run");
        var outcome = store.ApplyPatch(patchText, dryRun, opts.Has("--force"));

        if (outcome.Ok && outcome.NewText != null)
        {
            // 新しい・変わった画像URLを簡易チェック（取得できなくても適用はする）
            var after = DeckValidator.Validate(outcome.NewText);
            warnings.AddRange(DeckValidator.GridWarnings(after.Deck!, s.Layout.Rows, s.Layout.Cols));
            var oldUrls = before.Deck?.Pages.SelectMany(p => p.Buttons).Select(b => b.Image?.Url).OfType<string>().ToHashSet() ?? [];
            var newUrls = after.Deck!.Pages.SelectMany(p => p.Buttons).Select(b => b.Image?.Url).OfType<string>()
                .Where(u => !oldUrls.Contains(u)).Distinct().ToList();
            if (newUrls.Count > 0)
            {
                using var client = ImageProbe.CreateClient(s.Image.Proxy, s.Image.DownloadTimeoutSec);
                foreach (var url in newUrls)
                {
                    var r = await ImageProbe.CheckAsync(client, url, s.Image.MaxSizeMb * 1024L * 1024L);
                    if (!r.Ok) warnings.Add($"画像を取得できません（{r.Error}）: {url}");
                }
            }

            // inbox のパッチは適用後に inbox\done\ へ移す（記録として残す）
            if (!dryRun && IsUnder(patchPath, AppPaths.InboxDirectory))
            {
                try
                {
                    var done = Path.Combine(AppPaths.InboxDirectory, "done");
                    Directory.CreateDirectory(done);
                    var dest = Path.Combine(done, Path.GetFileName(patchPath));
                    if (File.Exists(dest)) dest = Path.Combine(done, $"{Path.GetFileNameWithoutExtension(patchPath)}_{DateTime.Now:HHmmssfff}.json");
                    File.Move(patchPath, dest);
                }
                catch (IOException ex)
                {
                    warnings.Add("パッチを inbox\\done へ移せませんでした: " + ex.Message);
                }
            }
        }
        return Finish(outcome, warnings, dryRun ? "apply --dry-run" : "apply");
    }

    private static int Finish(ApplyOutcome outcome, List<string> warnings, string command)
    {
        outcome.Warnings.InsertRange(0, warnings);
        var json = outcome.ToJson();
        Write(json);
        var summary = $"{command}: ok={outcome.Ok} rev={outcome.Revision} +{outcome.Added.Count} ~{outcome.Changed.Count} -{outcome.Deleted.Count}";
        if (outcome.Ok) Logger.Info(summary);
        else Logger.Warn(summary + " " + string.Join(" / ", outcome.Errors));
        return outcome.ExitCode;
    }

    private static bool IsUnder(string path, string dir)
    {
        var d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        return path.StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }

    private static int Error(int code, params string[] errors)
    {
        Write(new JsonObject { ["ok"] = false, ["errors"] = ToArray(errors) });
        return code;
    }

    public static void Write(JsonNode node) => Console.WriteLine(node.ToJsonString(DeckJson.WriteOptions));

    private static JsonArray ToArray(IEnumerable<string> items) => new(items.Select(x => (JsonNode?)x).ToArray());

    private static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private static JsonObject Help() => new()
    {
        ["ok"] = true,
        ["name"] = "deckctl",
        ["version"] = Version,
        ["deck"] = new SettingsStore(AppPaths.SettingsFile).Load().Settings?.DeckPath,
        ["inbox"] = AppPaths.InboxDirectory,
        ["commands"] = new JsonArray(
            "get [--page <ID>]",
            "apply <パッチ> [--dry-run] [--force]",
            "validate",
            "undo",
            "check-image <URL>"),
        ["exit_codes"] = "0:成功 / 1:検証エラー(deck.json変更なし) / 2:ファイル競合・読み書き失敗",
    };

    /// <summary>コマンドライン引数（コマンド・位置引数・--オプション）</summary>
    private sealed class Options
    {
        public string? Command { get; private set; }
        public List<string> Positional { get; } = [];
        private readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> WithValue = new(StringComparer.OrdinalIgnoreCase) { "--page", "--deck" };

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--", StringComparison.Ordinal) && o.Command != null)
                {
                    if (WithValue.Contains(a) && i + 1 < args.Length) o._named[a] = args[++i];
                    else o._named[a] = null;
                }
                else if (o.Command == null) o.Command = a.ToLowerInvariant();
                else o.Positional.Add(a);
            }
            return o;
        }

        public string? Get(string name) => _named.TryGetValue(name, out var v) ? v : null;
        public bool Has(string name) => _named.ContainsKey(name);
    }
}
