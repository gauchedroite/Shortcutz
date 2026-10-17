namespace Shortcutz;

// Replaces Microsoft.VisualBasic.Interaction.InputBox with a native WinForms prompt.
public sealed class PromptForm : Form
{
    private static readonly Font MonospaceFont = new(FontFamily.GenericMonospace, 10);
    private readonly TextBox _box;
    public string Result => _box.Text;

    public PromptForm(string title, string label, string initial, bool multiline = false)
    {
        Text = title;
        ClientSize = new Size(360, multiline ? 300 : 150);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        TopMost = true; // stay above the always-on-top main window

        var lbl = new Label { Text = label, Location = new Point(12, 12), AutoSize = true };
        _box = new TextBox
        {
            Location = new Point(12, 35),
            Size = new Size(336, multiline ? 220 : 80),
            Multiline = true,
            Text = initial
        };
        if (multiline) _box.Font = MonospaceFont;
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(196, multiline ? 268 : 118), Size = new Size(75, 23) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(277, multiline ? 268 : 118), Size = new Size(75, 23) };

        Controls.AddRange(lbl, _box, ok, cancel);
        CancelButton = cancel;
        if (multiline)
        {
            // Enter confirms, Shift+Enter inserts a newline, Esc cancels.
            _box.AcceptsReturn = false;
            _box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    if (e.Shift)
                    {
                        e.SuppressKeyPress = true;
                        _box.SelectedText = Environment.NewLine;
                    }
                    else if (!e.Control)
                    {
                        e.SuppressKeyPress = true;
                        ok.PerformClick();
                    }
                }
            };
        }
        else
        {
            AcceptButton = ok;
        }
        if (multiline) _box.SelectionStart = _box.TextLength;
        else _box.SelectAll();
        _box.Focus();
    }
}
