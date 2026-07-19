namespace Shortcutz;

// Replaces Microsoft.VisualBasic.Interaction.InputBox with a native WinForms prompt.
public sealed class PromptForm : Form
{
    private readonly TextBox _box;
    public string Result => _box.Text;

    public PromptForm(string title, string label, string initial)
    {
        Text = title;
        ClientSize = new Size(360, 150);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        var lbl = new Label { Text = label, Location = new Point(12, 12), AutoSize = true };
        _box = new TextBox
        {
            Location = new Point(12, 35),
            Size = new Size(336, 80),
            Multiline = true,
            Text = initial
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(196, 118), Size = new Size(75, 23) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(277, 118), Size = new Size(75, 23) };

        Controls.AddRange(lbl, _box, ok, cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        _box.SelectAll();
        _box.Focus();
    }
}
