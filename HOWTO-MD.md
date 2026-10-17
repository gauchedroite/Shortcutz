# Markdown in Shortcutz

Shortcutz supports a basic set of Markdown formats. Text pages show the source in Edit mode and formatted text in Preview mode. Press `F5` to switch modes. You can also right-click the page tab and select **Markdown preview**. The preview mode is saved with the page.

Notes always show formatted text. Double-click a note to edit its Markdown source. Icon labels support bold text only.

## Supported formats

| Format | Example |
|---|---|
| Heading | `## Heading` |
| Bullet list | `- Item` |
| Nested bullet | Indent a child `- Item` by two spaces |
| Numbered list | `1. Item` |
| Quote | `> Quoted text` |
| Code block | Three backticks on lines before and after the code |
| Horizontal rule | `---` |
| Bold | `**bold**` or `__bold__` |
| Italic | `*italic*` or `_italic_` |
| Inline code | `` `code` `` |
| Strikethrough | `~~text~~` |
| Link | `[label](https://example.com)` or `[file](file:///~/Documents/file.txt)` |

Clicking an absolute link URI passes it to Windows to open with its registered handler. In `file:` links, `~/` expands to the current user's profile folder. Relative links are not opened.

Icon labels support only `**bold**`. Other Markdown marks appear as typed.

## Limits

This is not a full CommonMark parser. Nested bullet lists support one level only; nested ordered lists and nested formatting are not supported. Tables and images are not rendered. Raw HTML and Markdown escape sequences are not interpreted. Put a blank line between paragraphs. Line breaks inside a paragraph display as spaces.
