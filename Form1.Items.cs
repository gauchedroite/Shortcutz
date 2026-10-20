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
    private static void WireDrag(Control[] controls, Action<int, int> onDrag, Action onClick, Action<Control, MouseEventArgs>? onDoubleClick = null, Action? onDragStart = null, Action? onDragEnd = null, Action? onDragCancel = null)
    {
        bool dragging = false;
        bool mouseDown = false;
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
                    onDoubleClick?.Invoke(c, e);
                    return;
                }
                doubleClick = false;
                mouseDown = true;
                dragging = false;
                dragOffset = e.Location;
                controls[0].BringToFront();
                c.Capture = true;
            };
            c.MouseCaptureChanged += (s, e) =>
            {
                if (c.Capture || !mouseDown) return;
                mouseDown = false;
                if (!dragging) return;
                dragging = false;
                onDragCancel?.Invoke();
            };
            c.MouseMove += (s, e) =>
            {
                if (e.Button != MouseButtons.Left || !mouseDown) return;
                if (c is MarkdownTextView { IsTextSelecting: true })
                {
                    mouseDown = false;
                    dragging = false;
                    return;
                }
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
                if (doubleClick) { doubleClick = false; return; }
                if (!mouseDown) return;
                mouseDown = false;
                var wasDragging = dragging;
                dragging = false;
                c.Capture = false;
                if (wasDragging) onDragEnd?.Invoke();
                else onClick();
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

    private static void ClearDragGhosts(Panel workspace)
    {
        foreach (var ghost in workspace.Controls.OfType<Label>().Where(l => l.Tag as string == "ghost").ToList())
            DisposeDragGhost(ghost, workspace);
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
        if (SelectedWorkspace() is Panel workspace)
            ApplyZoom(workspace);
    }

    private void ZoomOut()
    {
        var z = NextZoom(-1);
        if (Math.Abs(z - _zoom) < 0.001f) return;
        _zoom = z;
        if (SelectedWorkspace() is Panel workspace)
            ApplyZoom(workspace);
    }

    private void ResetZoom()
    {
        if (_zoom == 1f) return;
        _zoom = 1f;
        if (SelectedWorkspace() is Panel workspace)
            ApplyZoom(workspace);
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
        var compactIcons = item.Compact;

        var padding = (int)((compactIcons ? 2 : 6) * _zoom);
        var iconWidth = (int)((compactIcons ? 16 : 60) * _zoom);
        var iconHeight = (int)((compactIcons ? 16 : 42) * _zoom);
        var titleFont = new Font(TitleFont.FontFamily, (compactIcons ? 8 : TitleFont.Size) * _zoom);
        var titleSize = TextRenderer.MeasureText(
            MarkdownTextView.StripBold(displayName), titleFont,
            new Size(compactIcons ? int.MaxValue : (int)(110 * _zoom), int.MaxValue),
            compactIcons ? CompactTitleFlags : TitleFlags);
        var labelWidth = compactIcons ? titleSize.Width : (int)(110 * _zoom);
        var panelWidth = compactIcons ? padding + iconWidth + padding + labelWidth + padding : labelWidth;
        var panelHeight = compactIcons
            ? Math.Max(1, (int)(GridSize * _zoom))
            : padding + iconHeight + titleSize.Height + padding;

        panel.Size = new Size(panelWidth, panelHeight);
        panel.Location = new Point((int)(item.X * _zoom), (int)(item.Y * _zoom));

        icon.Size = new Size(iconWidth, iconHeight);
        icon.Location = new Point(
            compactIcons ? padding : (labelWidth - icon.Width) / 2,
            compactIcons ? (panelHeight - icon.Height) / 2 : padding);

        var original = icon.Tag as Image ?? icon.Image;
        if (original is not null)
        {
            var old = icon.Image;
            icon.Image = ScaleBitmap(original, icon.Size);
            if (old is not null && old != original) old.Dispose();
        }

        title.Size = new Size(labelWidth, compactIcons ? panelHeight - 2 * padding : titleSize.Height);
        title.Location = new Point(compactIcons ? padding + iconWidth + padding : 0,
            compactIcons ? padding : padding + iconHeight);
        ReplaceFont(title, titleFont);
    }

    private void ScaleNoteView(Label note, NoteItem item)
    {
        note.Padding = new Padding((int)(6 * _zoom));
        int w = (int)(item.Width * _zoom);
        note.Width = w;
        // The selection border eats 1px per side; measure inside it so the
        // last wrapped line isn't clipped.
        const int border = 2;
        var markdown = note.Controls.OfType<MarkdownTextView>().First();
        markdown.Location = new Point(note.Padding.Left, note.Padding.Top);
        markdown.Width = Math.Max(1, w - note.Padding.Horizontal - border);
        ReplaceFont(markdown, new Font(TitleFont.FontFamily, TitleFont.Size * _zoom));
        int contentHeight = markdown.ContentHeight(markdown.Width);
        note.Height = Math.Max((int)(40 * _zoom), contentHeight + note.Padding.Vertical + border);
        markdown.Height = contentHeight;
        note.Location = new Point((int)(item.X * _zoom), (int)(item.Y * _zoom));
    }

    private static void ReplaceFont(Control c, Font font)
    {
        var old = c.Font;
        if (old.Equals(font)) { font.Dispose(); return; }
        c.Font = font;
        if (!ReferenceEquals(old, TitleFont)) old.Dispose();
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
            MarkdownTextView.StripBold(displayName), TitleFont,
            new Size(labelWidth, int.MaxValue),
            TitleFlags);
        var panelHeight = 6 + 42 + titleSize.Height + 6;

        var panel = new Panel
        {
            Size = new Size(labelWidth, panelHeight),
            Location = new Point(item.X, item.Y),
            BackColor = IconBaseColor(item),
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
            icon.Image = GetIconBitmap(item.Path, false); // chrome placeholder; favicon loaded async by FetchUrlFaviconAsync
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
            MarkdownTextView.DrawBoldLabel(e.Graphics, (string)title.Tag!, title.Font,
                title.ClientRectangle, item.Compact);

        panel.Controls.Add(icon);
        panel.Controls.Add(title);

        if (!ItemExists(item.Path))
            panel.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.Red, 2);
                e.Graphics.DrawRectangle(pen, 1, 1, panel.Width - 2, panel.Height - 2);
            };

        void SetColor(string? color)
        {
            item.Color = color;
            panel.BackColor = IsSelected(panel) ? IconSelectionColor(item) : IconBaseColor(item);
            _board.Dirty();
        }

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
        menu.Items.Add("Copy", null, (s, e) => CopySelectedItems(workspace, panel));

        var colors = new ContextMenuStrip();
        colors.Items.Add("Default", null, (_, _) => SetColor(null));
        colors.Items.Add("Yellow", null, (_, _) => SetColor("yellow"));
        colors.Items.Add("Green", null, (_, _) => SetColor("green"));
        colors.Items.Add("Red", null, (_, _) => SetColor("red"));
        colors.Items.Add("Transparent", null, (_, _) => SetColor("transparent"));
        menu.Items.Add(new ToolStripMenuItem("Background color", null, colors.Items.Cast<ToolStripItem>().ToArray()));

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
                    .ToList()
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
            onDoubleClick: (ctrl, e) =>
            {
                if (dragGroup is not null)
                    foreach (var (_, _, _, g) in dragGroup)
                        DisposeDragGhost(g, workspace);
                dragGroup = null;
                var clickPoint = panel.PointToClient(ctrl.PointToScreen(e.Location));
                if (title.Bounds.Contains(clickPoint) && !item.Compact)
                {
                    RenameIcon(item, title, panel);
                    return;
                }
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
                var moving = dragGroup.Select(x => x.c).ToHashSet();
                var placed = new List<Rectangle>();
                foreach (var (c, i, _, g) in dragGroup)
                {
                    c.Location = i is IconItem iconItem && iconItem.Compact
                        ? SnapCompactIconToList(workspace, c, g.Location, moving, placed)
                        : SnapToGrid(workspace, c.Size, g.Location);
                    i.X = (int)(c.Left / _zoom);
                    i.Y = (int)(c.Top / _zoom);
                    c.BringToFront();
                    placed.Add(c.Bounds);
                }
                foreach (var (_, _, _, g) in dragGroup)
                    DisposeDragGhost(g, workspace);
                dragGroup = null;
                _board.Dirty();
            },
            onDragCancel: () =>
            {
                ClearDragGhosts(workspace);
                dragGroup = null;
            });

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
        var workspace = (Panel)panel.Parent!;
        if (item.Compact)
            PlaceCompactIconInList(workspace, panel);
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
        note.Controls.OfType<MarkdownTextView>().First().Markdown = newText;
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
        PageFromWorkspace(workspace).Items.Remove(item);
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

        var page = PageFromWorkspace(workspace);
        foreach (var c in selected)
        {
            workspace.Controls.Remove(c);
            page.Items.Remove((Item)c.Tag!);
            if (c.Tag is IconItem iconItem)
                DeleteFaviconCache(iconItem.Path);
            DisposeItemControl(c);
        }
        _board.Dirty();
    }

    private static Item CloneItem(Item item) => item switch
    {
        IconItem i => new IconItem(i.Path, i.X, i.Y, i.Label, i.Color) { Compact = i.Compact },
        NoteItem n => new NoteItem(n.Text, n.X, n.Y, n.Width, n.Color),
        _ => throw new NotSupportedException()
    };

    private void CopySelectedItems(Panel workspace, Control? source = null)
    {
        var selected = workspace.Controls.OfType<Control>().Where(IsSelected).ToList();
        if (selected.Count == 0 && source is not null && IsSelectable(source))
            selected.Add(source);
        if (selected.Count == 0) return;
        _clipboard.Clear();
        _clipboard.AddRange(selected.Select(c => CloneItem((Item)c.Tag!)));
    }

    private void CutSelectedItems(Panel workspace)
    {
        CopySelectedItems(workspace);
        var selected = workspace.Controls.OfType<Control>().Where(IsSelected).ToList();
        if (selected.Count == 0) return;
        var page = PageFromWorkspace(workspace);
        foreach (var c in selected)
        {
            workspace.Controls.Remove(c);
            page.Items.Remove((Item)c.Tag!);
            if (c.Tag is IconItem iconItem)
                DeleteFaviconCache(iconItem.Path);
            DisposeItemControl(c);
        }
        _board.Dirty();
    }

    private void PasteItems(Point? location = null)
    {
        if (_clipboard.Count == 0 || SelectedWorkspace() is not Panel workspace) return;
        var page = PageFromWorkspace(workspace);
        int minX = _clipboard.Min(i => i.X);
        int minY = _clipboard.Min(i => i.Y);
        var pasteLoc = location ?? new Point(workspace.Width / 2, workspace.Height / 2);
        var loc = SnapToGrid(workspace, new Size(1, 1), pasteLoc);
        int dx = (int)(loc.X / _zoom) - minX;
        int dy = (int)(loc.Y / _zoom) - minY;
        foreach (var proto in _clipboard)
        {
            var copy = CloneItem(proto);
            copy.X += dx;
            copy.Y += dy;
            page.Items.Add(copy);
            if (copy is IconItem icon)
            {
                var panel = CreateIconView(workspace, icon);
                if (icon.Compact) PlaceCompactIconInList(workspace, panel);
            }
            else
                CreateView(workspace, copy);
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
            Text = "",
            Font = TitleFont,
            Cursor = Cursors.Hand,
            BorderStyle = BorderStyle.None,
            Padding = new Padding(6),
            Tag = item
        };
        var markdown = new MarkdownTextView
        {
            Location = new Point(6, 6),
            Size = new Size(Math.Max(1, item.Width - 14), 28),
            Font = TitleFont,
            ForeColor = SystemColors.WindowText,
            Markdown = item.Text
        };
        note.Controls.Add(markdown);
        note.Paint += (_, e) =>
        {
            if (IsSelected(note))
            {
                using var pen = new Pen(Color.Black);
                e.Graphics.DrawRectangle(pen, 0, 0, note.Width - 1, note.Height - 1);
            }
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
        WireDrag(new Control[] { note, markdown },
            onDragStart: () =>
            {
                if (!IsSelected(note))
                    SelectSingle(workspace, note);
                dragGroup = workspace.Controls.OfType<Control>()
                    .Where(IsSelected)
                    .ToList()
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
            onDoubleClick: (_, _) => EditNote(),
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
            },
            onDragCancel: () =>
            {
                ClearDragGhosts(workspace);
                dragGroup = null;
            });

        var menu = new ContextMenuStrip();
        menu.Items.Add("Edit", null, (s, e) => EditNote());

        var colors = new ContextMenuStrip();
        colors.Items.Add("Default", null, (_, _) => SetColor(null));
        colors.Items.Add("Yellow", null, (_, _) => SetColor("yellow"));
        colors.Items.Add("Green", null, (_, _) => SetColor("green"));
        colors.Items.Add("Red", null, (_, _) => SetColor("red"));
        colors.Items.Add("Transparent", null, (_, _) => SetColor("transparent"));
        menu.Items.Add(new ToolStripMenuItem("Background color", null, colors.Items.Cast<ToolStripItem>().ToArray()));

        menu.Items.Add("Copy", null, (s, e) => CopySelectedItems(workspace, note));
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
            PageFromWorkspace(workspace).Items.Remove(item);
            DisposeItemControl(note);
            _board.Dirty();
        });
        note.ContextMenuStrip = menu;
        markdown.ContextMenuStrip = menu;

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
            if (!IsSelected(note)) return;
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

    private Point SnapCompactIconToList(Panel workspace, Control item, Point p, HashSet<Control> moving, List<Rectangle> placed)
    {
        var grid = (int)(GridSize * _zoom);
        if (grid == 0) grid = GridSize;
        int x = (int)Math.Round((double)p.X / grid) * grid;
        x = Math.Clamp(x, 0, Math.Max(0, workspace.Width - item.Width));
        int maxRow = Math.Max(0, workspace.Height - item.Height) / grid;
        int desiredRow = (int)Math.Round((double)p.Y / grid);

        bool IsOccupied(Rectangle bounds) =>
            placed.Any(bounds.IntersectsWith) ||
            workspace.Controls.OfType<Control>().Any(c => !moving.Contains(c) && IsSelectable(c) && bounds.IntersectsWith(c.Bounds));

        foreach (var row in Enumerable.Range(0, maxRow + 1).OrderBy(row => Math.Abs(row - desiredRow)))
        {
            var bounds = new Rectangle(x, row * grid, item.Width, item.Height);
            if (IsOccupied(bounds)) continue;
            Debug.Assert(!IsOccupied(bounds), "Compact list placement must not overlap another item.");
            return bounds.Location;
        }

        // shortcut: if no list row is free, leave the item where it was; add scrolling when pages need more rows.
        return item.Location;
    }

    private void PlaceCompactIconInList(Panel workspace, Panel item)
    {
        var moving = new HashSet<Control> { item };
        item.Location = SnapCompactIconToList(workspace, item, item.Location, moving, new List<Rectangle>());
        var icon = (IconItem)item.Tag!;
        icon.X = (int)(item.Left / _zoom);
        icon.Y = (int)(item.Top / _zoom);
    }

}
