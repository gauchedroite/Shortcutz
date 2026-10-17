using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Shortcutz;

internal sealed class MarkdownTextView : Control
{
    private static readonly Regex InlinePattern = new(@"\*\*[^*]+\*\*|__[^_]+__|`[^`]+`|\*[^*]+\*|_[^_]+_|~~[^~]+~~|\[[^\]]+\]\([^)]+\)", RegexOptions.Compiled);
    private static readonly Regex Words = new(@"\s+|\S+", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.+?)\s*#*$", RegexOptions.Compiled);
    private static readonly Regex Unordered = new(@"^(\s*)[-+*]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex Ordered = new(@"^\s*(\d+)[.)]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex Quote = new(@"^\s*>\s?(.*)$", RegexOptions.Compiled);
    private string _markdown = "";

    static MarkdownTextView()
    {
        Debug.Assert(ParseInline("**bold**").Single() is { Text: "bold", Style: FontStyle.Bold });
        Debug.Assert(ParseBlocks("# Heading").Single() is { Kind: BlockKind.Heading, Level: 1, Text: "Heading" });
        Debug.Assert(ParseBlocks("- Parent\n  - Child").Select(block => block.Nesting).SequenceEqual(new[] { 0, 1 }));
        Debug.Assert(ExpandHomeFileUri(new Uri("file:///~/.pi/agent/prompts/dominus.md")).LocalPath ==
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent", "prompts", "dominus.md"));
    }

    private enum BlockKind { Paragraph, Heading, Unordered, Ordered, Quote, Code, Blank, Rule }
    private sealed record Block(BlockKind Kind, string Text = "", int Level = 0, string Prefix = "", int Nesting = 0);
    private sealed record Run(string Text, FontStyle Style = FontStyle.Regular, bool Code = false, string? Link = null);
    private sealed record Fragment(string Text, Font Font, Color Color, Rectangle Bounds, string? Link);
    private sealed record LinkHit(Rectangle Bounds, string Url);
    private sealed class LayoutData(List<Fragment> fragments, List<LinkHit> links, List<Font> fonts, int height) : IDisposable
    {
        public List<Fragment> Fragments { get; } = fragments;
        public List<LinkHit> Links { get; } = links;
        public int Height { get; } = height;
        public void Dispose() { foreach (var font in fonts) font.Dispose(); }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Markdown
    {
        get => _markdown;
        set { _markdown = value ?? ""; Invalidate(); }
    }

    public MarkdownTextView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    public int ContentHeight(int width)
    {
        using var layout = CreateLayout(Math.Max(1, width));
        return layout.Height;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var layout = CreateLayout(ClientSize.Width);
        foreach (var fragment in layout.Fragments)
            TextRenderer.DrawText(e.Graphics, fragment.Text, fragment.Font, fragment.Bounds,
                fragment.Color, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        _linkHits = layout.Links;
    }

    private List<LinkHit> _linkHits = new();

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _linkHits.Any(link => link.Bounds.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        var hit = _linkHits.FirstOrDefault(link => link.Bounds.Contains(e.Location));
        if (hit is null || !Uri.TryCreate(hit.Url, UriKind.Absolute, out var uri)) return;
        uri = ExpandHomeFileUri(uri);
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { }
    }

    private static Uri ExpandHomeFileUri(Uri uri)
    {
        if (!uri.IsFile || !uri.AbsolutePath.StartsWith("/~", StringComparison.Ordinal) ||
            (uri.AbsolutePath.Length > 2 && uri.AbsolutePath[2] != '/')) return uri;
        var path = Uri.UnescapeDataString(uri.AbsolutePath[2..]).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return new Uri(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path));
    }

    private LayoutData CreateLayout(int width)
    {
        var fragments = new List<Fragment>();
        var links = new List<LinkHit>();
        var fonts = new List<Font>();
        var fontCache = new Dictionary<(int Heading, FontStyle Style, bool Code), Font>();
        Font GetFont(int heading, FontStyle style, bool code)
        {
            var key = (heading, style, code);
            if (fontCache.TryGetValue(key, out var cached)) return cached;
            float size = Font.Size * (heading == 0 ? 1 : Math.Max(1.05f, 1.65f - (heading - 1) * 0.1f));
            var font = new Font(code ? FontFamily.GenericMonospace : Font.FontFamily, size,
                style | (heading > 0 ? FontStyle.Bold : FontStyle.Regular));
            fonts.Add(font);
            fontCache.Add(key, font);
            return font;
        }

        int y = 0;
        foreach (var block in ParseBlocks(_markdown))
        {
            if (block.Kind == BlockKind.Blank)
            {
                y += Math.Max(3, Font.Height / 2);
                continue;
            }

            var runs = new List<Run>();
            int indent = 0;
            switch (block.Kind)
            {
                case BlockKind.Unordered:
                case BlockKind.Ordered:
                    runs.Add(new Run(block.Prefix, FontStyle.Regular));
                    indent = 16 + block.Nesting * 20;
                    break;
                case BlockKind.Quote:
                    runs.Add(new Run("│ ", FontStyle.Italic));
                    indent = 12;
                    break;
                case BlockKind.Rule:
                    runs.Add(new Run("────────────────────────", FontStyle.Regular));
                    break;
            }
            runs.AddRange(block.Kind == BlockKind.Code
                ? new[] { new Run(block.Text, FontStyle.Regular, Code: true) }
                : ParseInline(block.Text));

            int x = indent;
            int lineHeight = 0;
            foreach (var run in runs)
            {
                var style = run.Style;
                if (block.Kind == BlockKind.Quote) style |= FontStyle.Italic;
                var font = GetFont(block.Kind == BlockKind.Heading ? block.Level : 0, style, run.Code || block.Kind == BlockKind.Code);
                var color = run.Link is not null ? Color.RoyalBlue : run.Code || block.Kind == BlockKind.Code ? Color.DarkSlateGray : ForeColor;
                foreach (Match word in Words.Matches(run.Text))
                {
                    var text = word.Value;
                    if (string.IsNullOrWhiteSpace(text) && x == indent) continue;
                    int textWidth = TextRenderer.MeasureText(text, font, Size.Empty,
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                    if (x > indent && x + textWidth > width)
                    {
                        y += Math.Max(lineHeight, font.Height);
                        x = indent;
                        lineHeight = 0;
                        if (string.IsNullOrWhiteSpace(text)) continue;
                    }
                    int drawWidth = Math.Max(1, Math.Min(textWidth, width - x));
                    var bounds = new Rectangle(x, y, drawWidth + 2, font.Height + 2);
                    fragments.Add(new Fragment(text, font, color, bounds, run.Link));
                    if (run.Link is not null) links.Add(new LinkHit(bounds, run.Link));
                    x += textWidth;
                    lineHeight = Math.Max(lineHeight, font.Height);
                }
            }
            y += Math.Max(lineHeight, Font.Height) + (block.Kind == BlockKind.Heading ? 3 : 4);
        }
        return new LayoutData(fragments, links, fonts, Math.Max(Font.Height, y));
    }

    // shortcut: one level of bullet nesting only; use a full parser for nested ordered lists, deeper nesting, tables, or raw HTML.
    private static List<Block> ParseBlocks(string markdown)
    {
        var blocks = new List<Block>();
        var paragraph = new StringBuilder();
        var code = new List<string>();
        bool inCode = false;
        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new Block(BlockKind.Paragraph, paragraph.ToString()));
            paragraph.Clear();
        }
        foreach (var line in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                if (inCode)
                {
                    blocks.AddRange(code.Select(text => new Block(BlockKind.Code, text)));
                    code.Clear();
                }
                inCode = !inCode;
                continue;
            }
            if (inCode) { code.Add(line); continue; }
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                if (blocks.Count > 0 && blocks[^1].Kind != BlockKind.Blank)
                    blocks.Add(new Block(BlockKind.Blank));
                continue;
            }

            var heading = Heading.Match(line);
            var unordered = Unordered.Match(line);
            var ordered = Ordered.Match(line);
            var quote = Quote.Match(line);
            bool rule = Regex.IsMatch(line.Trim(), @"^(\*\s*){3,}$|^(-\s*){3,}$|^(_\s*){3,}$");
            if (heading.Success || unordered.Success || ordered.Success || quote.Success || rule)
            {
                FlushParagraph();
                if (heading.Success) blocks.Add(new Block(BlockKind.Heading, heading.Groups[2].Value, heading.Groups[1].Length));
                else if (unordered.Success) blocks.Add(new Block(BlockKind.Unordered, unordered.Groups[2].Value, Prefix: "• ", Nesting: unordered.Groups[1].Length > 0 ? 1 : 0));
                else if (ordered.Success) blocks.Add(new Block(BlockKind.Ordered, ordered.Groups[2].Value, Prefix: ordered.Groups[1].Value + ". "));
                else if (quote.Success) blocks.Add(new Block(BlockKind.Quote, quote.Groups[1].Value));
                else blocks.Add(new Block(BlockKind.Rule));
                continue;
            }
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(line.Trim());
        }
        FlushParagraph();
        if (inCode) blocks.AddRange(code.Select(text => new Block(BlockKind.Code, text)));
        return blocks;
    }

    private static List<Run> ParseInline(string text)
    {
        var runs = new List<Run>();
        int position = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            if (match.Index > position) runs.Add(new Run(text[position..match.Index]));
            var token = match.Value;
            if (token.StartsWith("**") || token.StartsWith("__"))
                runs.Add(new Run(token[2..^2], FontStyle.Bold));
            else if (token.StartsWith('`'))
                runs.Add(new Run(token[1..^1], FontStyle.Regular, Code: true));
            else if (token.StartsWith("~~"))
                runs.Add(new Run(token[2..^2], FontStyle.Strikeout));
            else if (token.StartsWith('*') || token.StartsWith('_'))
                runs.Add(new Run(token[1..^1], FontStyle.Italic));
            else
            {
                var link = Regex.Match(token, @"^\[([^\]]+)\]\(([^)]+)\)$");
                if (link.Success) runs.Add(new Run(link.Groups[1].Value, FontStyle.Underline, Link: link.Groups[2].Value));
                else runs.Add(new Run(token));
            }
            position = match.Index + match.Length;
        }
        if (position < text.Length) runs.Add(new Run(text[position..]));
        return runs;
    }

    public static string StripBold(string text) => Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");

    public static void DrawBoldLabel(Graphics graphics, string text, Font font, Rectangle bounds, bool compact)
    {
        var bold = new Font(font, font.Style | FontStyle.Bold);
        try
        {
            var parts = new List<(string Text, Font Font, int Width)>();
            int position = 0;
            foreach (Match match in Regex.Matches(text, @"\*\*(.+?)\*\*"))
            {
                if (match.Index > position) Add(text[position..match.Index], font);
                Add(match.Groups[1].Value, bold);
                position = match.Index + match.Length;
            }
            if (position < text.Length) Add(text[position..], font);
            void Add(string value, Font useFont)
            {
                foreach (Match word in Words.Matches(value.Replace("\r", "")))
                {
                    var width = TextRenderer.MeasureText(word.Value, useFont, Size.Empty,
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                    parts.Add((word.Value, useFont, width));
                }
            }

            var lines = new List<List<(string Text, Font Font, int Width)>> { new() };
            int lineWidth = 0;
            foreach (var part in parts)
            {
                if (!compact && part.Text.Contains('\n'))
                {
                    var split = part.Text.Split('\n');
                    for (int i = 0; i < split.Length; i++)
                    {
                        if (split[i].Length > 0) AddMeasured(split[i], part.Font);
                        if (i < split.Length - 1) { lines.Add(new()); lineWidth = 0; }
                    }
                    continue;
                }
                if (!compact && lineWidth > 0 && lineWidth + part.Width > bounds.Width)
                {
                    lines.Add(new());
                    lineWidth = 0;
                    if (string.IsNullOrWhiteSpace(part.Text)) continue;
                }
                lines[^1].Add(part);
                lineWidth += part.Width;
            }
            int y = 0;
            foreach (var line in lines)
            {
                int width = line.Sum(part => part.Width);
                int x = compact ? Math.Max(0, (bounds.Width - width) / 2) : Math.Max(0, (bounds.Width - width) / 2);
                foreach (var part in line)
                {
                    var rect = new Rectangle(bounds.Left + x, bounds.Top + y, Math.Max(1, part.Width + 2), font.Height + 2);
                    TextRenderer.DrawText(graphics, part.Text, part.Font, rect, SystemColors.WindowText,
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                    x += part.Width;
                }
                y += font.Height;
            }
            void AddMeasured(string value, Font useFont)
            {
                int width = TextRenderer.MeasureText(value, useFont, Size.Empty,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                lines[^1].Add((value, useFont, width));
                lineWidth += width;
            }
        }
        finally { bold.Dispose(); }
    }
}
