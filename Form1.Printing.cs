using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;

namespace Shortcutz;

public partial class Form1
{
    private void PrintCurrentPage()
    {
        if (SelectedSubPage is not TabPage subPage) return;
        PopulateSubPage(subPage);
        var page = PageFromSubPage(subPage);
        var workspace = WorkspaceFromSubPage(subPage);
        var headerPath = $"{Path.GetFileName(_stateFile)}/{TabFromPage(tabs.SelectedTab!).Name}/{page.Name}";
        var printedAt = DateTime.Now;
        using var document = new PrintDocument
        {
            DocumentName = $"{TabFromPage(tabs.SelectedTab!).Name} - {page.Name}"
        };
        document.DefaultPageSettings.Landscape = false;
        document.QueryPageSettings += (_, e) => e.PageSettings.Landscape = false;

        int textOffset = 0;
        int markdownOffset = 0;
        MarkdownTextView? markdownView = null;
        Font? markdownFont = null;
        document.PrintPage += (_, e) =>
        {
            if (e.Graphics is not Graphics printGraphics) return;
            var contentBounds = DrawPrintHeader(printGraphics, e.MarginBounds, headerPath, printedAt);
            if (page.Type != "Text")
            {
                PrintBoardPage(page, workspace, e, contentBounds);
                return;
            }

            if (!page.MarkdownPreview)
            {
                PrintRawText(page, e, contentBounds, ref textOffset);
                return;
            }

            float dpiX = Math.Min(printGraphics.DpiX, 300f);
            float dpiY = Math.Min(printGraphics.DpiY, 300f);
            int width = Math.Max(1, (int)Math.Round(contentBounds.Width * dpiX / 100));
            int height = Math.Max(1, (int)Math.Round(contentBounds.Height * dpiY / 100));
            markdownFont ??= new Font(TitleFont.FontFamily, TitleFont.Size * dpiY / 96f);
            var view = markdownView ??= new MarkdownTextView
            {
                Font = markdownFont,
                ForeColor = SystemColors.WindowText,
                Markdown = page.Text
            };
            using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                var result = view.DrawPrintPage(graphics, width, height, markdownOffset);
                markdownOffset = result.NextOffset;
                e.HasMorePages = markdownOffset < result.Height;
            }
            printGraphics.DrawImage(bitmap, contentBounds);
        };

        try
        {
            using var dialog = new PrintDialog { Document = document, UseEXDialog = true };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                document.Print();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not print the current page.\n\n{ex.Message}",
                "Print failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            markdownView?.Dispose();
            markdownFont?.Dispose();
        }
    }

    private static Rectangle DrawPrintHeader(Graphics graphics, Rectangle bounds, string path, DateTime printedAt)
    {
        const int headerHeight = 24;
        using var font = new Font(TitleFont.FontFamily, 8);
        using var format = new StringFormat
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter
        };
        graphics.DrawString($"{path}    {printedAt:yyyy-MM-dd HH:mm:ss}", font, Brushes.Black,
            new RectangleF(bounds.Left, bounds.Top, bounds.Width, headerHeight), format);
        using var pen = new Pen(Color.LightGray);
        graphics.DrawLine(pen, bounds.Left, bounds.Top + headerHeight, bounds.Right, bounds.Top + headerHeight);
        return new Rectangle(bounds.Left, bounds.Top + headerHeight + 8, bounds.Width,
            Math.Max(1, bounds.Height - headerHeight - 8));
    }

    private static void PrintRawText(Page page, PrintPageEventArgs e, Rectangle bounds, ref int offset)
    {
        if (e.Graphics is not Graphics graphics) return;
        var text = page.Text;
        if (text.Length == 0 || offset >= text.Length)
        {
            e.HasMorePages = false;
            return;
        }

        using var format = new StringFormat(StringFormat.GenericDefault)
        {
            FormatFlags = StringFormatFlags.LineLimit | StringFormatFlags.MeasureTrailingSpaces
        };
        var font = page.Monospace ? MonospaceFont : TitleFont;
        var remaining = text[offset..];
        graphics.MeasureString(remaining, font,
            new SizeF(bounds.Width, bounds.Height), format,
            out int charactersFitted, out _);
        if (charactersFitted == 0)
            charactersFitted = char.IsHighSurrogate(remaining[0]) && remaining.Length > 1 ? 2 : 1;

        graphics.DrawString(remaining[..charactersFitted], font, Brushes.Black, bounds, format);
        offset += charactersFitted;
        e.HasMorePages = offset < text.Length;
    }

    private void PrintBoardPage(Page page, Panel workspace, PrintPageEventArgs e, Rectangle contentBounds)
    {
        if (e.Graphics is not Graphics graphics) return;
        // Child index 0 is frontmost, so draw back-to-front to preserve the on-screen stack.
        var items = workspace.Controls.Cast<Control>().Where(IsSelectable)
            .OrderByDescending(workspace.Controls.GetChildIndex).ToArray();
        graphics.FillRectangle(Brushes.White, contentBounds);
        if (items.Length == 0) return;

        float originalZoom = page.Zoom;
        var originalBackColor = workspace.BackColor;
        try
        {
            // Render at the largest UI zoom before scaling the board down to paper.
            workspace.BackColor = Color.White;
            page.Zoom = 3f;
            ApplyZoom(workspace);
            var bounds = items.Select(item => item.Bounds).Aggregate(Rectangle.Union);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            float scale = Math.Min((float)contentBounds.Width / bounds.Width,
                (float)contentBounds.Height / bounds.Height);
            Debug.Assert(scale > 0);
            float width = bounds.Width * scale;
            float height = bounds.Height * scale;
            float left = contentBounds.Left + (contentBounds.Width - width) / 2;
            float top = contentBounds.Top + (contentBounds.Height - height) / 2;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

            foreach (var item in items)
            {
                if (item.Width <= 0 || item.Height <= 0) continue;
                bool selected = IsSelected(item);
                try
                {
                    if (selected) SetSelected(item, false);
                    using var bitmap = item is Label { Tag: NoteItem noteItem } note
                        ? CapturePrintNote(note, noteItem)
                        : CapturePrintControl(item);
                    var destination = new RectangleF(
                        left + (item.Left - bounds.Left) * scale,
                        top + (item.Top - bounds.Top) * scale,
                        item.Width * scale,
                        item.Height * scale);
                    graphics.DrawImage(bitmap, destination);
                }
                finally
                {
                    if (selected) SetSelected(item, true);
                }
            }
        }
        finally
        {
            page.Zoom = originalZoom;
            workspace.BackColor = originalBackColor;
            ApplyZoom(workspace);
        }
    }

    private static Color OpaqueOnWhite(Color color) => Color.FromArgb(
        (color.R * color.A + 255 * (255 - color.A) + 127) / 255,
        (color.G * color.A + 255 * (255 - color.A) + 127) / 255,
        (color.B * color.A + 255 * (255 - color.A) + 127) / 255);

    private static Bitmap CapturePrintNote(Label note, NoteItem item)
    {
        var bitmap = new Bitmap(note.Width, note.Height, PixelFormat.Format24bppRgb);
        try
        {
            var color = OpaqueOnWhite(NoteBaseColor(item));
            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
            if (note.Controls.OfType<MarkdownTextView>().FirstOrDefault() is { } markdown)
            {
                var originalBackColor = markdown.BackColor;
                try
                {
                    markdown.BackColor = color;
                    markdown.DrawToBitmap(bitmap, markdown.Bounds);
                }
                finally { markdown.BackColor = originalBackColor; }
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static Bitmap CapturePrintControl(Control control)
    {
        var bitmap = new Bitmap(control.Width, control.Height, PixelFormat.Format24bppRgb);
        try
        {
            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.White);
            control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

}
