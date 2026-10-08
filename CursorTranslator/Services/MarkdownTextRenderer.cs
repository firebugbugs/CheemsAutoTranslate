using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using MediaBrushes = Avalonia.Media.Brushes;
using MediaColor = Avalonia.Media.Color;
using MediaFontFamily = Avalonia.Media.FontFamily;

namespace CursorTranslator.Services;

/// <summary>
/// Renders the common Markdown emitted by translation and analysis models with
/// native Avalonia text controls, keeping the result selectable and theme-aware.
/// </summary>
internal static partial class MarkdownTextRenderer
{
    private static readonly Regex HeadingPattern = new(@"^(?<level>#{1,6})\s+(?<text>.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex ListItemPattern = new(@"^(?<indent>[ \t]*)(?<marker>(?:[-+*])|(?:\d+[.)]))[ \t]+(?<text>.*)$", RegexOptions.Compiled);
    private static readonly Regex HorizontalRulePattern = new(@"^\s*(?:(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,})$", RegexOptions.Compiled);

    public static void Render(string? markdown, StackPanel host, IBrush foreground)
    {
        host.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown)) return;

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        for (var index = 0; index < lines.Length;)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                index++;
                continue;
            }

            if (IsFenceStart(trimmed, out var fence))
            {
                index++;
                var code = new List<string>();
                while (index < lines.Length && !IsFenceEnd(lines[index].Trim(), fence))
                    code.Add(lines[index++]);
                if (index < lines.Length) index++;
                AddCodeBlock(string.Join("\n", code), host, foreground);
                continue;
            }

            var heading = HeadingPattern.Match(trimmed);
            if (heading.Success)
            {
                var level = heading.Groups["level"].Length;
                var size = Math.Max(13, 20 - (level * 1.2));
                host.Children.Add(CreateText(heading.Groups["text"].Value, foreground,
                    fontSize: size, fontWeight: FontWeight.Bold, margin: new Thickness(0, level == 1 ? 5 : 3, 0, 1)));
                index++;
                continue;
            }

            if (HorizontalRulePattern.IsMatch(trimmed))
            {
                host.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 4),
                    Background = new SolidColorBrush(MediaColor.FromArgb(0x55, 0x80, 0x80, 0x80))
                });
                index++;
                continue;
            }

            if (IsTableStart(lines, index))
            {
                index = AddTable(lines, index, host, foreground);
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                var quoteLines = new List<string>();
                while (index < lines.Length && lines[index].TrimStart().StartsWith('>'))
                {
                    var quoteLine = lines[index++].TrimStart()[1..].TrimStart();
                    quoteLines.Add(quoteLine);
                }
                var quoteText = CreateText(string.Join(" ", quoteLines), foreground);
                host.Children.Add(new Border
                {
                    BorderBrush = new SolidColorBrush(MediaColor.FromArgb(0xA0, 0x9E, 0x4B, 0x2E)),
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(8, 1, 0, 1),
                    Margin = new Thickness(2, 2),
                    Child = quoteText
                });
                continue;
            }

            var listMatch = ListItemPattern.Match(line);
            if (listMatch.Success)
            {
                index = AddList(lines, index, host, foreground);
                continue;
            }

            var paragraph = new List<string> { trimmed };
            index++;
            while (index < lines.Length && lines[index].Trim().Length > 0
                && !IsBlockStart(lines, index))
                paragraph.Add(lines[index++].Trim());
            host.Children.Add(CreateText(string.Join(" ", paragraph), foreground,
                margin: new Thickness(0, 1, 0, 2)));
        }
    }

    private static int AddList(string[] lines, int start, StackPanel host, IBrush foreground)
    {
        var index = start;
        while (index < lines.Length)
        {
            var match = ListItemPattern.Match(lines[index]);
            if (!match.Success) break;

            var indent = match.Groups["indent"].Value
                .Replace("\t", "    ", StringComparison.Ordinal).Length;
            var marker = match.Groups["marker"].Value;
            var textLines = new List<string> { match.Groups["text"].Value };
            index++;
            while (index < lines.Length)
            {
                if (ListItemPattern.IsMatch(lines[index])) break;
                if (lines[index].Trim().Length == 0) break;
                var leadingWhitespace = lines[index].Length - lines[index].TrimStart().Length;
                if (leadingWhitespace <= indent) break;
                textLines.Add(lines[index].Trim());
                index++;
            }

            var item = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 6,
                Margin = new Thickness(Math.Min(indent * 5, 32), 1, 0, 1)
            };
            var markerText = marker.Length == 1 && !char.IsDigit(marker[0]) ? "•" : marker;
            item.Children.Add(new SelectableTextBlock
            {
                Text = markerText,
                FontSize = 13,
                Foreground = foreground,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
            });
            var body = CreateText(string.Join(" ", textLines), foreground);
            Grid.SetColumn(body, 1);
            item.Children.Add(body);
            host.Children.Add(item);
        }
        return index;
    }

    private static int AddTable(string[] lines, int start, StackPanel host, IBrush foreground)
    {
        var rows = new List<string[]>();
        var index = start;
        while (index < lines.Length && lines[index].Contains('|'))
        {
            var cells = SplitTableRow(lines[index++]);
            if (rows.Count == 1 && cells.Length > 0 && cells.All(IsTableSeparator))
                continue;
            rows.Add(cells);
        }

        var columnCount = rows.Count == 0 ? 0 : rows.Max(row => row.Length);
        if (columnCount == 0) return Math.Max(index, start + 1);

        var table = new Grid
        {
            ColumnSpacing = 0,
            RowSpacing = 0,
            Margin = new Thickness(0, 3, 0, 5)
        };
        for (var column = 0; column < columnCount; column++)
            table.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        for (var row = 0; row < rows.Count; row++)
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var row = 0; row < rows.Count; row++)
        {
            for (var column = 0; column < columnCount; column++)
            {
                var cellText = column < rows[row].Length ? rows[row][column] : "";
                var cell = new Border
                {
                    Padding = new Thickness(6, 4),
                    BorderBrush = new SolidColorBrush(MediaColor.FromArgb(0x50, 0x80, 0x80, 0x80)),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Background = row == 0
                        ? new SolidColorBrush(MediaColor.FromArgb(0x18, 0x80, 0x80, 0x80))
                        : MediaBrushes.Transparent,
                    Child = CreateText(cellText, foreground, fontWeight: row == 0 ? FontWeight.SemiBold : FontWeight.Normal)
                };
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                table.Children.Add(cell);
            }
        }

        host.Children.Add(table);
        return index;
    }

    private static string[] SplitTableRow(string line)
    {
        var trimmed = line.Trim().Trim('|');
        return trimmed.Split('|').Select(cell => cell.Trim().Replace("\\|", "|", StringComparison.Ordinal)).ToArray();
    }

    private static bool IsTableSeparator(string cell)
        => Regex.IsMatch(cell, @"^:?-{3,}:?$");

    private static bool IsTableStart(string[] lines, int index)
        => index + 1 < lines.Length
            && lines[index].Contains('|')
            && lines[index + 1].Contains('|')
            && SplitTableRow(lines[index + 1]).Length > 0
            && SplitTableRow(lines[index + 1]).All(IsTableSeparator);

    private static bool IsBlockStart(string[] lines, int index)
    {
        var trimmed = lines[index].Trim();
        return IsFenceStart(trimmed, out _)
            || HeadingPattern.IsMatch(trimmed)
            || HorizontalRulePattern.IsMatch(trimmed)
            || trimmed.StartsWith('>')
            || ListItemPattern.IsMatch(lines[index])
            || IsTableStart(lines, index);
    }

    private static bool IsFenceStart(string line, out string fence)
    {
        fence = line.StartsWith("~~~", StringComparison.Ordinal) ? "~~~"
            : line.StartsWith("```", StringComparison.Ordinal) ? "```"
            : "";
        return fence.Length > 0;
    }

    private static bool IsFenceEnd(string line, string fence)
        => line.StartsWith(fence, StringComparison.Ordinal);

    private static void AddCodeBlock(string code, StackPanel host, IBrush foreground)
    {
        var codeText = new SelectableTextBlock
        {
            Text = code,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            FontFamily = new MediaFontFamily("Consolas"),
            Foreground = foreground
        };
        host.Children.Add(new Border
        {
            Padding = new Thickness(8, 6),
            Margin = new Thickness(0, 3),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(MediaColor.FromArgb(0x18, 0x80, 0x80, 0x80)),
            Child = codeText
        });
    }

    private static SelectableTextBlock CreateText(
        string text,
        IBrush foreground,
        double fontSize = 13,
        FontWeight? fontWeight = null,
        Thickness? margin = null)
    {
        var block = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            FontWeight = fontWeight ?? FontWeight.Normal,
            Foreground = foreground,
            Margin = margin ?? new Thickness(0),
            LineHeight = fontSize * 1.4
        };
        AddInlineMarkdown(block.Inlines!, text);
        return block;
    }

    private static void AddInlineMarkdown(InlineCollection inlines, string text)
    {
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length == 0) return;
            inlines.Add(new Run { Text = plain.ToString() });
            plain.Clear();
        }

        for (var index = 0; index < text.Length;)
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                plain.Append(text[index + 1]);
                index += 2;
                continue;
            }

            if (TryReadInlineSpan(text, index, "***", out var end, out var content)
                || TryReadInlineSpan(text, index, "___", out end, out content))
            {
                Flush();
                var boldItalic = new Bold();
                var italic = new Italic();
                AddInlineMarkdown(italic.Inlines, content);
                boldItalic.Inlines.Add(italic);
                inlines.Add(boldItalic);
                index = end;
                continue;
            }

            if (TryReadInlineSpan(text, index, "**", out end, out content)
                || TryReadInlineSpan(text, index, "__", out end, out content))
            {
                Flush();
                var bold = new Bold();
                AddInlineMarkdown(bold.Inlines, content);
                inlines.Add(bold);
                index = end;
                continue;
            }

            if (TryReadInlineSpan(text, index, "~~", out end, out content))
            {
                Flush();
                var strike = new Span { TextDecorations = TextDecorations.Strikethrough };
                AddInlineMarkdown(strike.Inlines, content);
                inlines.Add(strike);
                index = end;
                continue;
            }

            if (text[index] == '`' && TryReadInlineSpan(text, index, "`", out end, out content))
            {
                Flush();
                inlines.Add(new Run { Text = content, FontFamily = new MediaFontFamily("Consolas"), FontSize = 12 });
                index = end;
                continue;
            }

            if (text[index] == '[' && TryReadLink(text, index, out end, out content))
            {
                Flush();
                var linkText = new Underline();
                AddInlineMarkdown(linkText.Inlines, content);
                inlines.Add(linkText);
                index = end;
                continue;
            }

            if (text[index] is '*' or '_'
                && TryReadInlineSpan(text, index, text[index].ToString(), out end, out content)
                && content.Length > 0
                && !(text[index] == '_' && index > 0 && char.IsLetterOrDigit(text[index - 1])))
            {
                Flush();
                var italic = new Italic();
                AddInlineMarkdown(italic.Inlines, content);
                inlines.Add(italic);
                index = end;
                continue;
            }

            plain.Append(text[index++]);
        }
        Flush();
    }

    private static bool TryReadInlineSpan(string text, int start, string delimiter, out int end, out string content)
    {
        end = start;
        content = "";
        if (!text.AsSpan(start).StartsWith(delimiter, StringComparison.Ordinal)) return false;
        var contentStart = start + delimiter.Length;
        var close = text.IndexOf(delimiter, contentStart, StringComparison.Ordinal);
        while (close >= 0 && close > contentStart && text[close - 1] == '\\')
            close = text.IndexOf(delimiter, close + delimiter.Length, StringComparison.Ordinal);
        if (close <= contentStart) return false;
        content = text[contentStart..close];
        end = close + delimiter.Length;
        return true;
    }

    private static bool TryReadLink(string text, int start, out int end, out string label)
    {
        end = start;
        label = "";
        var labelEnd = text.IndexOf("](", start + 1, StringComparison.Ordinal);
        if (labelEnd <= start + 1) return false;
        var targetEnd = text.IndexOf(')', labelEnd + 2);
        if (targetEnd < 0) return false;
        label = text[(start + 1)..labelEnd];
        end = targetEnd + 1;
        return true;
    }
}
