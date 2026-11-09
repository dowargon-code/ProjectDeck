namespace ProjectDeck;

/// <summary>ボタンの「名前を変更」で出す小さな入力欄</summary>
internal sealed class RenameDialog : Form
{
    private readonly TextBox _text = new() { Width = 260 };

    private RenameDialog(string current)
    {
        Text = "名前を変更";
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

        _text.Text = current;
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 0) };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 0) };
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            if (_text.Text.Trim().Length == 0)
            {
                DialogResult = DialogResult.None;
                _text.Focus();
            }
        };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = "表示名（全角6文字程度まで）", AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
        layout.Controls.Add(_text);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        Shown += (_, _) => { _text.SelectAll(); _text.Focus(); };
    }

    /// <summary>新しい名前（前後の空白は除く）。キャンセルなら null</summary>
    public static string? Ask(IWin32Window owner, string current)
    {
        using var dialog = new RenameDialog(current);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._text.Text.Trim() : null;
    }
}
