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
    // ---------- item views ----------

    private void CreateView(Panel workspace, Item item)
    {
        switch (item)
        {
            case IconItem ii:
                CreateIconView(workspace, ii);
                break;
            case NoteItem ni:
                CreateNoteView(workspace, ni);
                break;
        }
    }

    // Shared drag logic for any draggable control (and its child subcontrols that
    // should all act as one draggable region, e.g. icon panel + icon + title).
    private static void WireDrag(Control[] controls, Action<int, int> onDrag, Action onClick, Action<Control>? onDoubleClick = null, Action? onDragStart = null, Action? onDragEnd = null)
    {
        bool dragging = false;
        bool doubleClick = false;
        Point dragOffset = Point.Empty;
        foreach (var c in controls)
        {
            c.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (e.Clicks == 2)
                {
                    doubleClick = true;
                    onDoubleClick?.Invoke(c);
                    return;
                }
                doubleClick = false;
                dragging = false;
                dragOffset = e.Location;
                controls[0].BringToFront();
                c.Capture = true;
            };
            c.MouseMove += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (!dragging && (Math.Abs(e.X - dragOffset.X) > 2 || Math.Abs(e.Y - dragOffset.Y) > 2))
                {
                    dragging = true;
                    onDragStart?.Invoke();
                }
                if (dragging) onDrag(e.X - dragOffset.X, e.Y - dragOffset.Y);
            };
            c.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                c.Capture = false;
                if (doubleClick) { doubleClick = false; return; }
                if (dragging) onDragEnd?.Invoke();
                else onClick();
                dragging = false;
            };
        }
    }

    private static Bitmap CaptureControl(Control c)
    {
        var bmp = new Bitmap(c.Width, c.Height);
        c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
        return bmp;
    }

    private static Bitmap ApplyOpacity(Bitmap source, float opacity)
    {
        var matrix = new ColorMatrix { Matrix33 = opacity };
        var attrs = new ImageAttributes();
        attrs.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
        var dest = new Bitmap(source.Width, source.Height);
        using (var g = Graphics.FromImage(dest))
        {
            g.DrawImage(source, new Rectangle(0, 0, dest.Width, dest.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attrs);
        }
        source.Dispose();
        return dest;
    }

    private static Label CreateDragGhost(Control source)
    {
        var ghost = new Label
        {
            Size = source.Size,
            BackColor = Color.Transparent,
            Image = ApplyOpacity(CaptureControl(source), 0.6f),
            Tag = "ghost"
        };
        return ghost;
    }

    private static void DisposeDragGhost(Label? ghost, Panel workspace)
    {
        if (ghost is null) return;
        workspace.Controls.Remove(ghost);
        if (ghost.Image is not null) { ghost.Image.Dispose(); ghost.Image = null; }
        ghost.Dispose();
    }

    private float NextZoom(int direction)
    {
        var z = (float)Math.Round(_zoom / ZoomStep + direction) * ZoomStep;
        return Math.Clamp(z, ZoomMin, ZoomMax);
    }

    private void ZoomIn()
    {
        var z = NextZoom(1);
        if (Math.Abs(z - _zoom) < 0.001f) return;
        _zoom = z;
        if (tabs.SelectedTab is TabPage page)
            ApplyZoom(WorkspaceFromPage(page));
    }

    private void ZoomOut()
    {
        var z = NextZoom(-1);
        if (Math.Abs(z - _zoom) < 0.001f) return;
        _zoom = z;
        if (tabs.SelectedTab is TabPage page)
            ApplyZoom(WorkspaceFromPage(page));
    }

    private void ResetZoom()
    {
        if (_zoom == 1f) return;
        _zoom = 1f;
        if (tabs.SelectedTab is TabPage page)
            ApplyZoom(WorkspaceFromPage(page));
    }

    private void ApplyZoom(Panel workspace)
    {
        foreach (Control c in workspace.Controls)
            ApplyZoomToControl(c);
        workspace.Invalidate();
    }

    private void ApplyZoomToControl(Control c)
    {
        switch (c.Tag)
        {
            case IconItem iconItem:
                ScaleIconView((Panel)c, iconItem);
                break;
            case NoteItem noteItem:
                ScaleNoteView((Label)c, noteItem);
                break;
        }
    }

    private void ScaleIconView(Panel panel, IconItem item)
    {
        var title = panel.Controls.OfType<Label>().First(l => l.Tag is string);
        var icon = panel.Controls.OfType<Label>().First(l => l.Tag is not null && l.Tag is not string);
        var displayName = title.Tag?.ToString() ?? "";

        var labelWidth = (int)(110 * _zoom);
        var titleFont = new Font(TitleFont.FontFamily, TitleFont.Size * _zoom);
        var titleSize = TextRenderer.MeasureText(
            displayName, titleFont,
            new Size(labelWidth, int.MaxValue), TitleFlags);
        var panelHeight = (int)(6 * _zoom) + (int)(42 * _zoom) + titleSize.Height + (int)(6 * _zoom);

        panel.Size = new Size(labelWidth, panelHeight);
        panel.Location = new Point((int)(item.X * _zoom), (int)(item.Y * _zoom));

        icon.Size = new Size((int)(60 * _zoom), (int)(42 * _zoom));
        icon.Location = new Point((labelWidth - icon.Width) / 2, (int)(6 * _zoom));

        var original = icon.Tag as Image ?? icon.Image;
        if (original is not null)
        {
            var old = icon.Image;
            icon.Image = ScaleBitmap(original, icon.Size);
            if (old is not null && old != original) old.Dispose();
        }

        title.Size = new Size(labelWidth, titleSize.Height);
        title.Location = new Point(0, (int)(48 * _zoom));
        ReplaceFont(title, titleFont);
    }

    private void ScaleNoteView(Label note, NoteItem item)
    {
        note.Padding = new Padding((int)(6 * _zoom));
        ReplaceFont(note, new Font(TitleFont.FontFamily, TitleFont.Size * _zoom));
        int w = (int)(item.Width * _zoom);
        note.Width = w;
        // BorderStyle.FixedSingle eats 1px per side; measure inside it so the
        // last wrapped line isn't clipped.
        const int border = 2;
        var tsize = TextRenderer.MeasureText(note.Text, note.Font,
            new Size(w - note.Padding.Horizontal - border, int.MaxValue),
            TextFormatFlags.WordBreak);
        note.Height = tsize.Height + note.Padding.Vertical + border;
        note.Location = new Point((int)(item.X * _zoom), (int)(item.Y * _zoom));
    }

    private static void ReplaceFont(Control c, Font font)
    {
        var old = c.Font;
        if (old != TitleFont) old.Dispose();
        c.Font = font;
    }

    private static Bitmap ScaleBitmap(Image source, Size fit)
    {
        var bmp = new Bitmap(fit.Width, fit.Height);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);
        var scale = Math.Min((float)fit.Width / source.Width, (float)fit.Height / source.Height);
        var w = (int)(source.Width * scale);
        var h = (int)(source.Height * scale);
        var x = (fit.Width - w) / 2;
        var y = (fit.Height - h) / 2;
        g.DrawImage(source, x, y, w, h);
        return bmp;
    }

    private Panel CreateIconView(Panel workspace, IconItem item)
    {
        var fileName = Path.GetFileName(item.Path);
        var displayName = string.IsNullOrWhiteSpace(item.Label)
            ? (string.IsNullOrEmpty(fileName) ? item.Path : fileName)
            : item.Label;

        const int labelWidth = 110;
        var titleSize = TextRenderer.MeasureText(
            displayName, TitleFont,
            new Size(labelWidth, int.MaxValue),
            TitleFlags);
        var panelHeight = 6 + 42 + titleSize.Height + 6;

        var panel = new Panel
        {
            Size = new Size(labelWidth, panelHeight),
            Location = new Point(item.X, item.Y),
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
            Tag = item
        };
        SetDoubleBuffered(panel);

        var icon = new Label
        {
            AutoSize = false,
            Size = new Size(60, 42),
            Location = new Point((labelWidth - 60) / 2, 6),
            BackColor = Color.Transparent,
            ImageAlign = ContentAlignment.MiddleCenter,
            Text = ""
        };
        if (IsUrl(item.Path))
        {
            var cache = GetFaviconCachePath(item.Path);
            if (File.Exists(cache))
            {
                try { icon.Image = LoadFaviconBitmap(cache); } catch { }
            }
            if (icon.Image is null)
            {
                icon.Image = GetIconBitmap(item.Path, false); // chrome placeholder until favicon loads
            }
            icon.Tag = icon.Image;
            _ = FetchUrlFaviconAsync(item.Path, panel, icon);
        }
        else
        {
            icon.Image = GetIconBitmap(item.Path, Directory.Exists(item.Path));
            icon.Tag = icon.Image;
        }

        var title = new Label
        {
            AutoSize = false,
            Size = new Size(labelWidth, titleSize.Height),
            Location = new Point(0, 48),
            TextAlign = ContentAlignment.TopCenter,
            BackColor = Color.Transparent,
            ForeColor = SystemColors.WindowText,
            Text = "",
            Font = TitleFont,
            Tag = displayName
        };
        title.Paint += (s, e) =>
            TextRenderer.DrawText(e.Graphics, (string)title.Tag!, title.Font,
                title.ClientRectangle, title.ForeColor, TitleFlags);

        panel.Controls.Add(icon);
        panel.Controls.Add(title);

        if (!ItemExists(item.Path))
            panel.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.Red, 2);
                e.Graphics.DrawRectangle(pen, 1, 1, panel.Width - 2, panel.Height - 2);
            };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Rename", null, (s, e) => RenameIcon(item, title, panel));
        if (File.Exists(item.Path))
        {
            menu.Items.Add("Open file location", null, (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Could not open file location", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        }
        menu.Items.Add("Delete", null, (s, e) =>
        {
            if (IsSelected(panel))
                DeleteSelectedItems(workspace);
            else
                DeleteIcon(workspace, item, panel);
        });
        panel.ContextMenuStrip = menu;

        List<(Control c, Item i, Point s, Label g)>? dragGroup = null;
        WireDrag(new Control[] { panel, icon, title },
            onDragStart: () =>
            {
                // Only clear selection if this icon wasn't already selected
                if (!IsSelected(panel))
                    SelectSingle(workspace, panel);
                // Create ghosts for all selected items and send originals behind them
                dragGroup = workspace.Controls.OfType<Control>()
                    .Where(IsSelected)
                    .Select(c =>
                    {
                        var g = CreateDragGhost(c);
                        g.Location = c.Location;
                        workspace.Controls.Add(g);
                        g.BringToFront();
                        c.SendToBack();
                        return (c, (Item)c.Tag!, c.Location, g);
                    })
                    .ToList();
            },
            onDrag: (dx, dy) =>
            {
                if (dragGroup is null) return;
                foreach (var (_, _, gs, g) in dragGroup)
                    g.Location = Clamp(workspace, g.Size,
                        new Point(gs.X + dx, gs.Y + dy));
            },
            onClick: () =>
            {
                if (dragGroup is not null)
                    foreach (var (_, _, _, g) in dragGroup)
                        DisposeDragGhost(g, workspace);
                dragGroup = null;
                if ((Control.ModifierKeys & Keys.Control) != 0)
                    Toggle(panel);
                else
                    SelectSingle(workspace, panel);
            },
            onDoubleClick: ctrl =>
            {
                if (dragGroup is not null)
                    foreach (var (_, _, _, g) in dragGroup)
                        DisposeDragGhost(g, workspace);
                dragGroup = null;
                if (title.RectangleToScreen(title.ClientRectangle).Contains(Cursor.Position))
                    return; // title double-click handled by manual detector below
                if (!ItemExists(item.Path))
                {
                    MessageBox.Show(
                        $"The source cannot be found:\n{item.Path}",
                        "Missing target",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }
                if ((Control.ModifierKeys & Keys.Shift) != 0
                    && !IsUrl(item.Path) && !Directory.Exists(item.Path)
                    && FindNotepadPlus() is string npp)
                {
                    try { Process.Start(new ProcessStartInfo(npp) { UseShellExecute = false, ArgumentList = { item.Path } }); }
                    catch (Exception ex) { MessageBox.Show(ex.Message, "Could not open in Notepad++", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    return;
                }
                OpenItem(item.Path);
            },
            onDragEnd: () =>
            {
                if (dragGroup is null) return;
                foreach (var (c, i, _, g) in dragGroup)
                {
                    c.Location = SnapToGrid(workspace, c.Size, g.Location);
                    i.X = (int)(c.Left / _zoom);
                    i.Y = (int)(c.Top / _zoom);
                    c.BringToFront();
                }
                foreach (var (_, _, _, g) in dragGroup)
                    DisposeDragGhost(g, workspace);
                dragGroup = null;
                _board.Dirty();
            });

        // Labels don't reliably synthesize WM_LBUTTONDBLCLK, so detect title double-click manually.
        DateTime lastTitleUp = DateTime.MinValue;
        Point lastTitleUpPos = Point.Empty;
        title.MouseUp += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var now = DateTime.UtcNow;
            if ((now - lastTitleUp).TotalMilliseconds <= SystemInformation.DoubleClickTime
                && Math.Abs(e.X - lastTitleUpPos.X) <= SystemInformation.DoubleClickSize.Width
                && Math.Abs(e.Y - lastTitleUpPos.Y) <= SystemInformation.DoubleClickSize.Height)
            {
                lastTitleUp = DateTime.MinValue;
                RenameIcon(item, title, panel);
            }
            else
            {
                lastTitleUp = now;
                lastTitleUpPos = e.Location;
            }
        };

        workspace.Controls.Add(panel);
        ApplyZoomToControl(panel);
        return panel;
    }

    private static void SetDoubleBuffered(Control c) =>
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(c, true);

    private static Bitmap ResizeToIcon(Image source)
    {
        var bmp = new Bitmap(42, 42);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, 0, 0, 42, 42);
        return bmp;
    }

    private static Bitmap? LoadFaviconBitmap(string path)
    {
        using var ms = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var source = Image.FromStream(ms);
        return ResizeToIcon(source);
    }

    private void UpdateIconLabel(IconItem item, Label title, Panel panel, string newLabel)
    {
        item.Label = newLabel;
        title.Tag = newLabel;
        ApplyZoomToControl(panel);
        title.Invalidate();
    }

    private void RenameIcon(IconItem item, Label title, Panel panel)
    {
        var newLabel = Prompt("Rename icon", "Rename icon:", item.Label ?? Path.GetFileName(item.Path));
        if (string.IsNullOrWhiteSpace(newLabel)) return;
        UpdateIconLabel(item, title, panel, newLabel);
        _board.Dirty();
    }

    private void RenameNote(NoteItem item, Label note)
    {
        var newText = Prompt("Edit note", "Edit note:", item.Text, multiline: true);
        if (string.IsNullOrWhiteSpace(newText)) return;
        item.Text = newText;
        note.Text = newText;
        ScaleNoteView(note, item);
        _board.Dirty();
    }

    private void RenameSelected(Panel workspace)
    {
        var sel = workspace.Controls.OfType<Control>().FirstOrDefault(IsSelected);
        if (sel is null) return;
        switch (sel.Tag)
        {
            case IconItem ii when sel is Panel p:
                var title = p.Controls.OfType<Label>().First(l => l.Tag is string);
                RenameIcon(ii, title, p);
                break;
            case NoteItem ni when sel is Label note:
                RenameNote(ni, note);
                break;
        }
    }

    private void DeleteIcon(Panel workspace, IconItem item, Panel panel)
    {
        var displayName = item.Label ?? Path.GetFileName(item.Path);
        if (ConfirmRemove(displayName, ItemExists(item.Path)) != DialogResult.Yes) return;
        workspace.Controls.Remove(panel);
        TabFromSelected(tabs).Items.Remove(item);
        DisposeItemControl(panel);
        DeleteFaviconCache(item.Path);
        _board.Dirty();
    }

    private void DeleteSelectedItems(Panel workspace)
    {
        var selected = workspace.Controls.OfType<Control>().Where(IsSelected).ToList();
        if (selected.Count == 0) return;

        var message = selected.Count == 1
            ? $"Remove '{GetItemDisplayName((Item)selected[0].Tag!)}' from the board?"
            : $"Remove {selected.Count} selected items from the board?";
        var iconPaths = selected
            .Where(c => c.Tag is IconItem)
            .Select(c => ((IconItem)c.Tag!).Path);
        var detail = iconPaths.All(ItemExists)
            ? "\n\nThe original files/folders will not be affected."
            : "";
        if (MessageBox.Show(message + detail, "Remove?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var tab = TabFromSelected(tabs);
        foreach (var c in selected)
        {
            workspace.Controls.Remove(c);
            tab.Items.Remove((Item)c.Tag!);
            if (c.Tag is IconItem iconItem)
                DeleteFaviconCache(iconItem.Path);
            DisposeItemControl(c);
        }
        _board.Dirty();
    }

    private static string GetIconDisplayName(IconItem item) =>
        item.Label ?? Path.GetFileName(item.Path) ?? item.Path;

    private void CreateNoteView(Panel workspace, NoteItem item)
    {
        var note = new Label
        {
            AutoSize = false,
            Size = new Size(item.Width, 40),
            Location = new Point(item.X, item.Y),
            BackColor = NoteBaseColor(item),
            ForeColor = SystemColors.WindowText,
            Text = item.Text,
            Font = TitleFont,
            Cursor = Cursors.Hand,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(6),
            Tag = item
        };

        void EditNote() => RenameNote(item, note);
        void SetColor(string? color)
        {
            item.Color = color;
            if (!IsSelected(note))
                note.BackColor = NoteBaseColor(item);
            _board.Dirty();
        }

        List<(Control c, Item i, Point s, Label g)>? dragGroup = null;
        WireDrag(new[] { note },
            onDragStart: () =>
            {
                if (!IsSelected(note))
                    SelectSingle(workspace, note);
                dragGroup = workspace.Controls.OfType<Control>()
                    .Where(IsSelected)
                    .Select(c =>
                    {
                        var g = CreateDragGhost(c);
                        g.Location = c.Location;
                        workspace.Controls.Add(g);
                        g.BringToFront();
                        c.SendToBack();
                        return (c, (Item)c.Tag!, c.Location, g);
                    })
                    .ToList();
            },
            onDrag: (dx, dy) =>
            {
                if (dragGroup is null) return;
                foreach (var (_, _, gs, g) in dragGroup)
                    g.Location = Clamp(workspace, g.Size,
                        new Point(gs.X + dx, gs.Y + dy));
            },
            onClick: () =>
            {
                if (dragGroup is not null)
                    foreach (var (_, _, _, g) in dragGroup)
                        DisposeDragGhost(g, workspace);
                dragGroup = null;
                if ((Control.ModifierKeys & Keys.Control) != 0)
                    Toggle(note);
                else
                    SelectSingle(workspace, note);
            },
            onDoubleClick: _ => EditNote(),
            onDragEnd: () =>
            {
                if (dragGroup is null) return;
                foreach (var (c, i, _, g) in dragGroup)
                {
                    c.Location = SnapToGrid(workspace, c.Size, g.Location);
                    i.X = (int)(c.Left / _zoom);
                    i.Y = (int)(c.Top / _zoom);
                    c.BringToFront();
                }
                foreach (var (_, _, _, g) in dragGroup)
                    DisposeDragGhost(g, workspace);
                dragGroup = null;
                _board.Dirty();
            });

        var menu = new ContextMenuStrip();
        menu.Items.Add("Edit", null, (s, e) => EditNote());

        var colors = new ContextMenuStrip();
        colors.Items.Add("Default", null, (_, _) => SetColor(null));
        colors.Items.Add("Yellow", null, (_, _) => SetColor("yellow"));
        colors.Items.Add("Green", null, (_, _) => SetColor("green"));
        colors.Items.Add("Red", null, (_, _) => SetColor("red"));
        colors.Items.Add("Gray", null, (_, _) => SetColor("gray"));
        menu.Items.Add(new ToolStripMenuItem("Background color", null, colors.Items.Cast<ToolStripItem>().ToArray()));

        menu.Items.Add("Delete", null, (s, e) =>
        {
            if (IsSelected(note))
            {
                DeleteSelectedItems(workspace);
                return;
            }
            if (MessageBox.Show(
                    $"Remove note '{item.Text}'?\n\nThis will only remove the note from the board.",
                    "Remove?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            workspace.Controls.Remove(note);
            TabFromSelected(tabs).Items.Remove(item);
            DisposeItemControl(note);
            _board.Dirty();
        });
        note.ContextMenuStrip = menu;

        // Resize grip: child control in the bottom-right corner, fully separate
        // from the note's drag/select wiring (child consumes its own mouse events).
        const int gripSize = 10;
        var grip = new Panel
        {
            Size = new Size(gripSize, gripSize),
            Location = new Point(item.Width - gripSize, 40 - gripSize),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            BackColor = Color.Transparent,
            Cursor = Cursors.SizeNWSE
        };
        grip.Paint += (s, e) =>
        {
            var g = e.Graphics;
            using var pen = new Pen(Color.FromArgb(120, 80, 80, 80), 1f);
            var r = grip.ClientRectangle;
            // two strokes form a small "\" triangle in the corner
            g.DrawLine(pen, r.Right - 4, r.Bottom - 1, r.Right - 1, r.Bottom - 4);
            g.DrawLine(pen, r.Right - 7, r.Bottom - 1, r.Right - 1, r.Bottom - 7);
        };
        bool resizing = false;
        int resizeStartWidth = 0;
        Point resizeStartMouse = Point.Empty;
        grip.MouseDown += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            resizing = true;
            resizeStartWidth = item.Width;
            resizeStartMouse = grip.PointToScreen(e.Location);
            grip.Capture = true;
            SelectSingle(workspace, note);
        };
        grip.MouseMove += (s, e) =>
        {
            if (!resizing) return;
            var now = grip.PointToScreen(e.Location);
            int delta = (int)((now.X - resizeStartMouse.X) / _zoom);
            item.Width = Math.Clamp(resizeStartWidth + delta, NoteItem.MinWidth, NoteItem.MaxWidth);
            ScaleNoteView(note, item);
        };
        grip.MouseUp += (s, e) =>
        {
            if (!resizing) return;
            resizing = false;
            grip.Capture = false;
            _board.Dirty();
        };
        note.Controls.Add(grip);

        ApplyZoomToControl(note);
        workspace.Controls.Add(note);
    }

    private static void DisposeItemControl(Control c)
    {
        if (c is Panel p)
            foreach (var child in p.Controls)
                if (child is Label l && l.Image is not null)
                {
                    l.Image.Dispose();
                    l.Image = null;
                    l.Tag = null;
                }
        c.Dispose();
    }

    private static Point Clamp(Panel workspace, Size size, Point p)
    {
        var r = workspace.ClientRectangle;
        int x = Math.Max(0, Math.Min(p.X, Math.Max(0, r.Width - size.Width)));
        int y = Math.Max(0, Math.Min(p.Y, Math.Max(0, r.Height - size.Height)));
        return new Point(x, y);
    }

    private Point SnapToGrid(Panel workspace, Size size, Point p)
    {
        var grid = (int)(GridSize * _zoom);
        if (grid == 0) grid = GridSize;
        int x = (int)Math.Round((double)p.X / grid) * grid;
        int y = (int)Math.Round((double)p.Y / grid) * grid;
        return Clamp(workspace, size, new Point(x, y));
    }

}
