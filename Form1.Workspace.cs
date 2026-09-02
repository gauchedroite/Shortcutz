using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shortcutz;

public partial class Form1
{
    private static readonly Color NoteBackColor = Color.FromArgb(220, 255, 255, 200);
    private static readonly Color NoteSelectionBackColor = Color.FromArgb(255, 255, 240, 150);

    private static Color SelectionTint()
    {
        var c = SystemColors.Highlight;
        return Color.FromArgb(26, c.R, c.G, c.B);
    }

    private static bool IsSelectable(Control c) =>
        (c is Panel && c.Tag is IconItem) || (c is Label && c.Tag is NoteItem);

    private static bool IsSelected(Control c) => c switch
    {
        Panel => c.BackColor != Color.Transparent,
        Label => c.BackColor != NoteBackColor,
        _ => false
    };

    private static void SetSelected(Control c, bool selected)
    {
        if (c is Panel p)
        {
            p.BackColor = selected ? SelectionTint() : Color.Transparent;
            if (p.Controls.Count > 1 && p.Controls[1] is Label title)
                title.ForeColor = SystemColors.WindowText;
        }
        else if (c is Label note)
        {
            note.BackColor = selected ? NoteSelectionBackColor : NoteBackColor;
        }
    }

    private static string GetItemDisplayName(Item item) => item switch
    {
        IconItem i => i.Label ?? Path.GetFileName(i.Path) ?? i.Path,
        NoteItem n => n.Text,
        _ => ""
    };

    private static string? GetItemPath(Item item) => item is IconItem i ? i.Path : null;

    private static string? ExtractUrlTitle(IDataObject data, string url)
    {
        if (data.GetDataPresent(DataFormats.Html))
        {
            var html = ((string?)data.GetData(DataFormats.Html)) ?? "";
            var htmlIdx = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
            if (htmlIdx >= 0) html = html[htmlIdx..];
            var title = ExtractTitleFromHtml(html);
            if (title is not null) return title;
            var link = Regex.Match(html, @"<a[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (link.Success)
            {
                var text = WebUtility.HtmlDecode(link.Groups[1].Value.Trim());
                if (!string.IsNullOrWhiteSpace(text) && !string.Equals(text, url, StringComparison.OrdinalIgnoreCase))
                    return text;
            }
        }
        foreach (var fmt in new[] { "text/x-moz-url", "text/x-moz-url-desc", "chromium/x-page-title" })
        {
            if (!data.GetDataPresent(fmt)) continue;
            var raw = data.GetData(fmt)?.ToString() ?? "";
            var lines = raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length >= 2)
            {
                var candidate = lines[1].Trim();
                if (!string.IsNullOrWhiteSpace(candidate) && !IsUrl(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private static string? ExtractTitleFromHtml(string html)
    {
        var match = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value)) return null;
        return WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
    }

    private static bool IsUrl(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("www.", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeUrl(string url) =>
        url.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url
            : url;

    private void Workspace_DragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data is null) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop) ?? Array.Empty<string>();
            if (files.Any(ItemExists))
                e.Effect = DragDropEffects.Link;
        }
        else if (e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.Html))
        {
            var text = (string?)e.Data.GetData(DataFormats.Text) ?? "";
            if (IsUrl(text))
                e.Effect = DragDropEffects.Link;
        }
    }

    private void Workspace_DragDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Panel workspace || e.Data is null) return;
        var dropPoint = workspace.PointToClient(new Point(e.X, e.Y));
        var tab = TabFromSelected(tabs);

        int i = 0;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop) ?? Array.Empty<string>();
            foreach (var path in files.Where(ItemExists))
            {
                var loc = SnapToGrid(workspace, new Size(110, 90),
                    new Point(dropPoint.X + i % 3 * (int)(GridSize * _zoom), dropPoint.Y + i / 3 * (int)(GridSize * _zoom)));
                var item = new IconItem(path, (int)(loc.X / _zoom), (int)(loc.Y / _zoom), null);
                tab.Items.Add(item);
                CreateIconView(workspace, item);
                i++;
            }
        }
        else if (e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.Html))
        {
            var text = (string?)e.Data.GetData(DataFormats.Text) ?? "";
            if (IsUrl(text))
            {
                var url = NormalizeUrl(text);
                var label = ExtractUrlTitle(e.Data, url);
                var loc = SnapToGrid(workspace, new Size(110, 90),
                    new Point(dropPoint.X + i % 3 * (int)(GridSize * _zoom), dropPoint.Y + i / 3 * (int)(GridSize * _zoom)));
                var item = new IconItem(url, (int)(loc.X / _zoom), (int)(loc.Y / _zoom), label);
                tab.Items.Add(item);
                var p = CreateIconView(workspace, item);
                if (label is null && p.Controls[1] is Label titleLabel)
                    _ = FetchUrlTitleAsync(url, item, titleLabel, p);
            }
        }
        _board.Dirty();
    }

    private bool _selecting;
    private Point _selStart;
    private Rectangle _selRect;

    private void Workspace_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.Clicks != 1 || sender is not Panel workspace) return;
        ClearHighlights(workspace);
        _selecting = true;
        _selStart = e.Location;
        _selRect = Rectangle.Empty;
        workspace.Invalidate();
    }

    private void Workspace_MouseMove(object? sender, MouseEventArgs e)
    {
        if (!_selecting || e.Button != MouseButtons.Left || sender is not Panel workspace) return;
        var old = _selRect;
        _selRect = SelectionRect(_selStart, e.Location);
        if (old.Width > 0 && old.Height > 0) workspace.Invalidate(old);
        if (_selRect.Width > 0 && _selRect.Height > 0) workspace.Invalidate(_selRect);
        workspace.Update();
    }

    private void Workspace_MouseUp(object? sender, MouseEventArgs e)
    {
        if (sender is not Panel workspace) return;
        if (e.Button == MouseButtons.Right)
        {
            if (workspace.GetChildAtPoint(e.Location) == null)
                _workspaceMenu.Show(workspace, e.Location);
            return;
        }
        if (!_selecting || e.Button != MouseButtons.Left) return;
        _selecting = false;
        if (_selRect.Width > 0 && _selRect.Height > 0) workspace.Invalidate(_selRect);
        if (_selRect.Width > 3 && _selRect.Height > 3)
            SelectItems(workspace, _selRect);
    }

    private void Workspace_Paint(object? sender, PaintEventArgs e)
    {
        if (sender is not Panel workspace) return;
        if (_showGridDots)
            DrawGridDots(e.Graphics, workspace);
        if (!_selecting || _selRect.Width <= 0 || _selRect.Height <= 0) return;
        var c = SystemColors.Highlight;
        using var fill = new SolidBrush(Color.FromArgb(32, c.R, c.G, c.B));
        using var pen = new Pen(c, 1);
        e.Graphics.FillRectangle(fill, _selRect);
        e.Graphics.DrawRectangle(pen, _selRect.X, _selRect.Y, _selRect.Width - 1, _selRect.Height - 1);
    }

    private static void DrawGridDots(Graphics g, Panel workspace)
    {
        using var brush = new SolidBrush(Color.FromArgb(80, 128, 128, 128));
        const int r = 1;
        var bounds = workspace.ClientRectangle;
        for (int x = 0; x < bounds.Width; x += GridSize)
            for (int y = 0; y < bounds.Height; y += GridSize)
                g.FillEllipse(brush, x - r, y - r, r * 2, r * 2);
    }

    private static Rectangle SelectionRect(Point a, Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private static void SelectItems(Panel workspace, Rectangle rect)
    {
        foreach (var c in workspace.Controls.OfType<Control>().Where(IsSelectable))
            if (rect.IntersectsWith(c.Bounds))
                SetSelected(c, true);
    }

    private static void ClearHighlights(Panel workspace)
    {
        foreach (var c in workspace.Controls.OfType<Control>().Where(IsSelectable))
            SetSelected(c, false);
    }

    private static void SelectSingle(Panel workspace, Control c)
    {
        ClearHighlights(workspace);
        SetSelected(c, true);
    }

    private static void Toggle(Control c)
    {
        SetSelected(c, !IsSelected(c));
    }

    private void Workspace_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (sender is not Panel workspace) return;
        if (workspace.GetChildAtPoint(e.Location) != null) return;
        var text = Prompt("New note", "Enter note text:", "", multiline: true);
        if (string.IsNullOrWhiteSpace(text)) return;
        var tab = TabFromSelected(tabs);
        var loc = Clamp(workspace, new Size(120, 40), e.Location);
        var item = new NoteItem(text, (int)(loc.X / _zoom), (int)(loc.Y / _zoom));
        tab.Items.Add(item);
        CreateNoteView(workspace, item);
        _board.Dirty();
    }

}
