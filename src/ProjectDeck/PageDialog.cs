using System.Text.RegularExpressions;
using ProjectDeck.Core.Deck;
using ProjectDeck.Rendering;

namespace ProjectDeck;

/// <summary>ページの追加・変更ダイアログの結果。Color / Root は未指定なら null</summary>
internal sealed record PageEdit(string Id, string Name, string? Color, string? Root);

/// <summary>
/// ページ（案件）の追加と、名前・色の変更（仕様書 5.3「ページタブの右クリック」）。
/// 追加のときだけ ID と基準フォルダ（vars.root）も入力できる。ID はあとから変えられない（ボタンIDの接頭辞になるため）。
/// </summary>
internal sealed partial class PageDialog : Form
{
    private readonly TextBox _name = new() { Width = 260 };
    private readonly TextBox _id = new() { Width = 260 };
    private readonly TextBox _color = new() { Width = 100 };
    private readonly Panel _swatch = new() { Size = new Size(22, 22), BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _root = new() { Width = 300 };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(360, 0) };
    private readonly Button _ok = new() { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 0) };
    private readonly IReadOnlySet<string> _existingIds;
    private readonly bool _isNew;

    private PageDialog(bool isNew, IReadOnlySet<string> existingIds)
    {
        _isNew = isNew;
        _existingIds = existingIds;
        Text = isNew ? "新しいページ" : "ページの名前・色";
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Row(table, "名前", _name, "タブに出る名前（全角6文字程度まで）");
        if (isNew)
        {
            Row(table, "ID", _id, "英小文字・数字・ハイフン。ボタンIDの接頭辞になり、あとから変えられない");
        }

        var pick = new Button { Text = "選択…", AutoSize = true };
        pick.Click += (_, _) => PickColor();
        var clear = new Button { Text = "既定色", AutoSize = true };
        clear.Click += (_, _) => _color.Text = "";
        Row(table, "色", Flow(_color, _swatch, pick, clear), "タブと、画像の無いボタンの背景。空欄なら既定色");

        if (isNew)
        {
            var browse = new Button { Text = "参照…", AutoSize = true };
            browse.Click += (_, _) => BrowseRoot();
            Row(table, "基準フォルダ", Flow(_root, browse), "案件のフォルダ（vars.root）。ボタンのパスは ${root}/… と書ける。省略可");
        }

        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 0) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);
        table.Controls.Add(_status, 0, table.RowCount);
        table.SetColumnSpan(_status, 2);
        table.RowCount++;
        table.Controls.Add(buttons, 0, table.RowCount);
        table.SetColumnSpan(buttons, 2);
        table.RowCount++;
        Controls.Add(table);
        AcceptButton = _ok;
        CancelButton = cancel;

        _name.TextChanged += (_, _) => Validate_();
        _id.TextChanged += (_, _) => Validate_();
        _color.TextChanged += (_, _) => Validate_();
        _root.TextChanged += (_, _) => Validate_();
        Shown += (_, _) => { _name.SelectAll(); _name.Focus(); };
    }

    private static void Row(TableLayoutPanel table, string caption, Control control, string hint)
    {
        var row = table.RowCount++;
        table.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, row);
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        control.Margin = new Padding(0, 3, 0, 0);
        stack.Controls.Add(control);
        stack.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 4) });
        table.Controls.Add(stack, 1, row);
    }

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        foreach (var c in controls)
        {
            c.Margin = new Padding(0, 0, 6, 0);
            flow.Controls.Add(c);
        }
        return flow;
    }

    private void PickColor()
    {
        using var dlg = new ColorDialog { FullOpen = true, AnyColor = true };
        if (DeckButtonRenderer.TryParseColor(_color.Text.Trim(), out var current)) dlg.Color = current;
        if (dlg.ShowDialog(this) == DialogResult.OK) _color.Text = $"#{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}";
    }

    private void BrowseRoot()
    {
        using var dlg = new FolderBrowserDialog { Description = "案件の基準フォルダ", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (Directory.Exists(_root.Text.Trim())) dlg.InitialDirectory = _root.Text.Trim();
        if (dlg.ShowDialog(this) == DialogResult.OK) _root.Text = dlg.SelectedPath;
    }

    private void Validate_()
    {
        var errors = new List<string>();
        if (_name.Text.Trim().Length == 0) errors.Add("名前を入力してください");
        if (_isNew)
        {
            var id = _id.Text.Trim();
            if (!IdRegex().IsMatch(id)) errors.Add("ID は英小文字・数字・ハイフンで、英小文字か数字から始めてください");
            else if (_existingIds.Contains(id)) errors.Add($"ID \"{id}\" はすでにあります");
        }
        var color = _color.Text.Trim();
        var colorOk = color.Length == 0 || DeckButtonRenderer.TryParseColor(color, out _);
        if (!colorOk) errors.Add("色は #RRGGBB で入力してください");
        _swatch.BackColor = colorOk && DeckButtonRenderer.TryParseColor(color, out var c) ? c : SystemColors.Control;
        if (_isNew && _root.Text.Trim().Length > 0 && !Directory.Exists(Core.PathExpander.Expand(_root.Text.Trim())))
            errors.Add("基準フォルダが見つかりません（空欄でも構いません）");
        _status.Text = errors.FirstOrDefault() ?? "";
        _ok.Enabled = errors.Count == 0;
    }

    private PageEdit Result()
    {
        var color = _color.Text.Trim();
        var root = _root.Text.Trim();
        return new PageEdit(_id.Text.Trim(), _name.Text.Trim(),
            color.Length == 0 ? null : color.ToLowerInvariant(),
            root.Length == 0 ? null : root.Replace('\\', '/'));
    }

    /// <summary>新しいページ。キャンセルなら null</summary>
    public static PageEdit? AskNew(Form owner, IReadOnlySet<string> existingIds)
    {
        // ランチャーが最前面（📌 ON）のときは、ダイアログも最前面にしないとランチャーの下に隠れる
        using var dialog = new PageDialog(isNew: true, existingIds) { TopMost = owner.TopMost };
        dialog._id.Text = NextPageId(existingIds);
        dialog.Validate_();
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Result() : null;
    }

    /// <summary>既存ページの名前・色。キャンセルなら null（Id・Root は元のまま）</summary>
    public static PageEdit? AskEdit(Form owner, DeckPage page)
    {
        using var dialog = new PageDialog(isNew: false, new HashSet<string>()) { TopMost = owner.TopMost };
        dialog._id.Text = page.Id;
        dialog._name.Text = page.Name;
        dialog._color.Text = page.Color ?? "";
        dialog.Validate_();
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog.Result() with { Id = page.Id, Root = page.Vars.GetValueOrDefault("root") }
            : null;
    }

    /// <summary>空いている page-N</summary>
    private static string NextPageId(IReadOnlySet<string> existing)
    {
        for (var n = existing.Count + 1; ; n++)
        {
            var id = $"page-{n}";
            if (!existing.Contains(id)) return id;
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex IdRegex();
}
