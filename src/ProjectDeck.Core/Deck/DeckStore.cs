using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ProjectDeck.Core.Deck;

/// <summary>deck.json の更新結果（deckctl の出力にもそのまま使う）</summary>
public sealed class ApplyOutcome
{
    public bool Ok { get; set; }

    /// <summary>0：成功／1：検証エラー（deck.json 変更なし）／2：ファイル競合・読み書き失敗</summary>
    public int ExitCode { get; set; }

    public bool DryRun { get; set; }
    public long Revision { get; set; }
    public List<string> Added { get; } = [];
    public List<string> Changed { get; } = [];
    public List<string> Deleted { get; } = [];
    public List<string> PagesAdded { get; } = [];
    public List<string> PagesChanged { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Errors { get; } = [];

    /// <summary>大量削除のため --force が必要で止めた</summary>
    public bool NeedsForce { get; set; }

    /// <summary>書き込んだ（または dry-run で書き込むはずだった）内容</summary>
    public string? NewText { get; set; }

    public static ApplyOutcome Fail(int exitCode, params string[] errors)
    {
        var o = new ApplyOutcome { Ok = false, ExitCode = exitCode };
        o.Errors.AddRange(errors);
        return o;
    }

    /// <summary>JSON 出力用</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["ok"] = Ok,
            ["dry_run"] = DryRun,
            ["revision"] = Revision,
            ["added"] = ToArray(Added),
            ["changed"] = ToArray(Changed),
            ["deleted"] = ToArray(Deleted),
        };
        if (PagesAdded.Count > 0) o["pages_added"] = ToArray(PagesAdded);
        if (PagesChanged.Count > 0) o["pages_changed"] = ToArray(PagesChanged);
        o["warnings"] = ToArray(Warnings);
        if (Errors.Count > 0) o["errors"] = ToArray(Errors);
        return o;
    }

    private static JsonArray ToArray(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)s).ToArray());
}

/// <summary>ロックを取れなかった</summary>
public sealed class DeckLockException(string message) : Exception(message);

/// <summary>
/// deck.json の読み書きをまとめたもの。本体（D&amp;D登録・削除）と deckctl（apply・undo）の両方から使う。
/// ・ロックファイルで同時書き込みを防ぐ（取れなければ最大3秒待つ）
/// ・一時ファイルに書いてから置き換える（書きかけを残さない）
/// ・書き込み前の内容を history\ にバックアップ（既定30世代）
/// </summary>
public sealed class DeckStore(string deckPath, string historyDirectory, int historyLimit = 30)
{
    /// <summary>1回でこれ以上ボタンが減る変更は --force（本体では確認）が必要</summary>
    public const int MassDeleteThreshold = 5;

    public string DeckPath { get; } = deckPath;
    public string HistoryDirectory { get; } = historyDirectory;
    public int HistoryLimit { get; } = Math.Max(1, historyLimit);
    public string LockPath => DeckPath + ".lock";

    /// <summary>現在の内容を読む。ファイルが無ければ null。ロック中・書きかけに備えて100ms間隔で最大5回リトライ</summary>
    public string? ReadText()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(DeckPath)) return null;
                using var fs = new FileStream(DeckPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return sr.ReadToEnd();
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>書き込み用ロックを取る。Dispose で解放</summary>
    public IDisposable AcquireLock(TimeSpan? timeout = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DeckPath))!);
        while (true)
        {
            try
            {
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < limit)
            {
                Thread.Sleep(50);
            }
            catch (IOException)
            {
                throw new DeckLockException("deck.json が他の処理で更新中です（ロックを取得できませんでした）");
            }
        }
    }

    /// <summary>一時ファイルに書いてから置き換える（UTF-8 BOM なし）</summary>
    public void WriteAtomic(string text)
    {
        var full = Path.GetFullPath(DeckPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, full, overwrite: true);
    }

    /// <summary>
    /// 履歴に保存する。直近の履歴と同じ内容なら保存しない（deckctl と本体の二重保存を防ぐ）。
    /// </summary>
    public void SaveHistory(string text)
    {
        Directory.CreateDirectory(HistoryDirectory);
        var files = HistoryFiles();
        if (files.Count > 0 && Hash(File.ReadAllText(files[0])) == Hash(text)) return;
        var name = $"deck_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json";
        File.WriteAllText(Path.Combine(HistoryDirectory, name), text, new UTF8Encoding(false));
        foreach (var old in HistoryFiles().Skip(HistoryLimit))
        {
            try { File.Delete(old); } catch (IOException) { }
        }
    }

    /// <summary>履歴ファイル（新しい順）</summary>
    public IReadOnlyList<string> HistoryFiles() =>
        Directory.Exists(HistoryDirectory)
            ? Directory.GetFiles(HistoryDirectory, "deck_*.json").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).ToList()
            : [];

    /// <summary>
    /// パッチを適用する：検証 → 適用 → 全体を再検証 → バックアップ → 書き込み。
    /// </summary>
    public ApplyOutcome ApplyPatch(string patchText, bool dryRun = false, bool force = false)
    {
        var patchNode = DeckJson.TryParse(patchText, out var parseError);
        if (patchNode == null) return ApplyOutcome.Fail(1, "パッチ: " + (parseError ?? "空です"));
        return ApplyPatch(patchNode, dryRun, force);
    }

    public ApplyOutcome ApplyPatch(JsonNode patchNode, bool dryRun = false, bool force = false) =>
        Update(current =>
        {
            var pr = DeckPatcher.Apply(current, patchNode);
            return (pr.Result, pr);
        }, dryRun, force);

    /// <summary>1つ前の履歴に戻す（使った履歴ファイルは消すので、繰り返すとさらに前に戻る）</summary>
    public ApplyOutcome Undo()
    {
        try
        {
            using var lockHandle = AcquireLock();
            var history = HistoryFiles();
            if (history.Count == 0) return ApplyOutcome.Fail(1, "戻せる履歴がありません");
            var currentText = ReadText();
            var currentRev = currentText != null && DeckJson.TryParse(currentText, out _) is JsonObject c ? RevisionOf(c) : 0;

            foreach (var file in history)
            {
                var v = DeckValidator.Validate(File.ReadAllText(file));
                if (!v.Ok)
                {
                    // 壊れた履歴は飛ばす
                    File.Delete(file);
                    continue;
                }
                var root = v.Root!;
                root["revision"] = currentRev + 1;
                root["updated_by"] = "undo";
                var text = DeckJson.Serialize(root);
                WriteAtomic(text);
                File.Delete(file);
                var o = new ApplyOutcome { Ok = true, ExitCode = 0, Revision = currentRev + 1, NewText = text };
                o.Warnings.Add($"{Path.GetFileName(file)} の状態に戻しました");
                return o;
            }
            return ApplyOutcome.Fail(1, "戻せる履歴がありません（有効な履歴がありません）");
        }
        catch (DeckLockException ex) { return ApplyOutcome.Fail(2, ex.Message); }
        catch (IOException ex) { return ApplyOutcome.Fail(2, "読み書きに失敗しました: " + ex.Message); }
        catch (UnauthorizedAccessException ex) { return ApplyOutcome.Fail(2, "読み書きに失敗しました: " + ex.Message); }
    }

    /// <summary>現在の deck.json を検証する（ファイルが無ければ空の状態として扱う）</summary>
    public ValidationResult ValidateCurrent()
    {
        var text = ReadText();
        return text == null ? DeckValidator.Validate(DeckJson.EmptyDeck()) : DeckValidator.Validate(text);
    }

    // 更新処理の共通部分
    private ApplyOutcome Update(Func<JsonObject, (JsonObject? next, PatchResult? patch)> change, bool dryRun, bool force)
    {
        try
        {
            using var lockHandle = dryRun ? null : AcquireLock();
            var currentText = ReadText();
            var current = currentText == null ? DeckValidator.Validate(DeckJson.EmptyDeck()) : DeckValidator.Validate(currentText);
            if (!current.Ok)
            {
                var f = ApplyOutcome.Fail(1, "現在の deck.json が正しくないため変更できません（deckctl undo で戻すか、手で修正してください）");
                f.Errors.AddRange(current.Errors);
                return f;
            }

            var (next, pr) = change(current.Root!);
            var o = new ApplyOutcome { DryRun = dryRun };
            if (pr != null)
            {
                o.Added.AddRange(pr.Added);
                o.Changed.AddRange(pr.Changed);
                o.Deleted.AddRange(pr.Deleted);
                o.PagesAdded.AddRange(pr.PagesAdded);
                o.PagesChanged.AddRange(pr.PagesChanged);
                o.Warnings.AddRange(pr.Warnings);
                o.Errors.AddRange(pr.Errors);
            }
            if (next == null || o.Errors.Count > 0)
            {
                o.ExitCode = 1;
                return o;
            }

            // 適用後の全体を再検証
            var after = DeckValidator.Validate(next);
            o.Warnings.AddRange(after.Warnings);
            if (!after.Ok)
            {
                o.Errors.AddRange(after.Errors);
                o.ExitCode = 1;
                return o;
            }

            // 大量削除の保護
            var removed = current.Deck!.ButtonCount - after.Deck!.ButtonCount;
            if (removed >= MassDeleteThreshold && !force)
            {
                o.NeedsForce = true;
                o.Errors.Add($"ボタンが {removed} 個減る変更です。意図どおりなら --force を付けて実行してください");
                o.ExitCode = 1;
                return o;
            }

            var text = DeckJson.Serialize(next);
            o.NewText = text;
            o.Revision = after.Deck.Revision;
            if (!dryRun)
            {
                if (currentText != null) SaveHistory(currentText);
                WriteAtomic(text);
            }
            o.Ok = true;
            o.ExitCode = 0;
            return o;
        }
        catch (DeckLockException ex) { return ApplyOutcome.Fail(2, ex.Message); }
        catch (IOException ex) { return ApplyOutcome.Fail(2, "読み書きに失敗しました: " + ex.Message); }
        catch (UnauthorizedAccessException ex) { return ApplyOutcome.Fail(2, "読み書きに失敗しました: " + ex.Message); }
    }

    private static long RevisionOf(JsonObject o) =>
        o["revision"].TryGetInt64(out var n) ? n : 0;

    /// <summary>内容のハッシュ（自己書き込みの判定などに使う）</summary>
    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.TrimStart('﻿'))));
}
