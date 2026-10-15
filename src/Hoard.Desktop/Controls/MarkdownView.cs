using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Hoard.Desktop.Controls;

/// <summary>
/// Read-only rich text for the advisor's answers: renders the Markdown subset Claude uses (headings, paragraphs,
/// bullet and numbered lists, **bold**, *italic*, `code`, tables, horizontal rules) into a FlowDocument. Colors are
/// resource references, so a light/dark switch restyles answers already on screen. Text stays selectable for copying.
/// </summary>
public sealed partial class MarkdownView : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata("", (d, _) => ((MarkdownView)d).Render()));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Render();
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex Heading();
    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")] private static partial Regex ListItem();
    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")] private static partial Regex TableRule();
    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")] private static partial Regex HorizontalRule();
    [GeneratedRegex(@"(\*\*|__)(.+?)\1|(?<![*\w])\*(?!\s)(.+?)(?<!\s)\*(?!\*)|`([^`]+)`")] private static partial Regex Inline();

    private void Render()
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0), FontFamily = FontFamily, FontSize = 14, LineHeight = 21 };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");
        var lines = (Markdown ?? "").Replace("\r\n", "\n").Split('\n');
        var para = new List<string>();
        List? list = null;
        var listOrdered = false;

        void FlushParagraph()
        {
            if (para.Count == 0) return;
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            AddInlines(p.Inlines, string.Join(" ", para.Select(l => l.Trim())));
            doc.Blocks.Add(p);
            para.Clear();
        }
        void FlushList()
        {
            if (list is null) return;
            doc.Blocks.Add(list);
            list = null;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { FlushParagraph(); FlushList(); continue; }

            if (Heading().Match(line) is { Success: true } h)
            {
                FlushParagraph(); FlushList();
                var level = h.Groups[1].Value.Length;
                var p = new Paragraph
                {
                    FontSize = level switch { 1 => 20, 2 => 17, _ => 15 },
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, level <= 2 ? 10 : 6, 0, 4),
                };
                AddInlines(p.Inlines, h.Groups[2].Value.Trim());
                doc.Blocks.Add(p);
                continue;
            }

            if (HorizontalRule().IsMatch(line))
            {
                FlushParagraph(); FlushList();
                var rule = new BlockUIContainer(new Separator { Margin = new Thickness(0, 4, 0, 8) });
                doc.Blocks.Add(rule);
                continue;
            }

            // A table: a header row followed by a |---|---| rule.
            if (line.TrimStart().StartsWith('|') && i + 1 < lines.Length && TableRule().IsMatch(lines[i + 1]))
            {
                FlushParagraph(); FlushList();
                var rows = new List<string[]> { Cells(line) };
                i += 2;
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|')) rows.Add(Cells(lines[i++]));
                i--;
                doc.Blocks.Add(BuildTable(rows));
                continue;
            }

            if (ListItem().Match(line) is { Success: true } li)
            {
                FlushParagraph();
                var ordered = char.IsDigit(li.Groups[2].Value[0]);
                if (list is null || listOrdered != ordered)
                {
                    FlushList();
                    list = new List
                    {
                        MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(0, 0, 0, 8),
                        Padding = new Thickness(22, 0, 0, 0),
                    };
                    listOrdered = ordered;
                }
                var p = new Paragraph { Margin = new Thickness(li.Groups[1].Value.Length >= 2 ? 18 : 0, 0, 0, 3) };
                AddInlines(p.Inlines, li.Groups[3].Value.Trim());
                list.ListItems.Add(new ListItem(p));
                continue;
            }

            // A continuation line of a list item, or plain paragraph text.
            if (list is not null && line.StartsWith("  ") && list.ListItems.LastListItem?.Blocks.LastBlock is Paragraph last)
            {
                last.Inlines.Add(new Run(" "));
                AddInlines(last.Inlines, line.Trim());
                continue;
            }
            FlushList();
            para.Add(line);
        }
        FlushParagraph();
        FlushList();
        Document = doc;
    }

    private static string[] Cells(string row) =>
        row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    private Table BuildTable(List<string[]> rows)
    {
        var columns = rows.Max(r => r.Length);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 10), BorderThickness = new Thickness(0) };
        for (var c = 0; c < columns; c++) table.Columns.Add(new TableColumn { Width = GridLength.Auto });
        var group = new TableRowGroup();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = new TableRow();
            if (r == 0) row.FontWeight = FontWeights.SemiBold;
            for (var c = 0; c < columns; c++)
            {
                var text = c < rows[r].Length ? rows[r][c] : "";
                var p = new Paragraph { Margin = new Thickness(0) };
                // Right-align figures so columns of amounts line up.
                if (r > 0 && Regex.IsMatch(text, @"^[\s\-+$€£(]*[\d,.]+[%)kKmM]*\s*$")) p.TextAlignment = TextAlignment.Right;
                AddInlines(p.Inlines, text);
                var cell = new TableCell(p) { Padding = new Thickness(6, 3, 12, 3), BorderThickness = new Thickness(0, 0, 0, 1) };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "CardStrokeColorDefaultBrush");
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    private static void AddInlines(InlineCollection target, string text)
    {
        var pos = 0;
        foreach (Match m in Inline().Matches(text))
        {
            if (m.Index > pos) target.Add(new Run(text[pos..m.Index]));
            if (m.Groups[2].Success)
            {
                var bold = new Bold();
                AddInlines(bold.Inlines, m.Groups[2].Value);
                target.Add(bold);
            }
            else if (m.Groups[3].Success)
            {
                var italic = new Italic();
                AddInlines(italic.Inlines, m.Groups[3].Value);
                target.Add(italic);
            }
            else
            {
                var code = new Run(m.Groups[4].Value) { FontFamily = new FontFamily("Consolas") };
                code.SetResourceReference(TextElement.BackgroundProperty, "ControlFillColorSecondaryBrush");
                target.Add(code);
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) target.Add(new Run(text[pos..]));
    }
}
