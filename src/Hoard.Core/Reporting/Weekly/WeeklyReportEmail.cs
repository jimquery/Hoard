using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Hoard.Core.Reporting.Weekly;

/// <summary>
/// Renders a <see cref="WeeklySpendingReport"/> as an email: table-based HTML with inline styles only (mail clients drop
/// stylesheets and scripts), bar "charts" drawn as sized table cells, and a plain-text alternative.
/// </summary>
public static partial class WeeklyReportEmail
{
    private const string Ink = "#1b1b1a", Muted = "#6b6a66", Line = "#e6e5e2", Accent = "#2a78d6", Good = "#1a7f4b", Bad = "#c0392b", Panel = "#f6f6f4";

    public static string Subject(WeeklySpendingReport r) =>
        $"Hoard: {Money(r.Total)} spent {r.Start:MMM d}–{r.End:MMM d}" + (r.FourWeekAverage > 0 ? $" ({Percent((r.Total - r.FourWeekAverage) / r.FourWeekAverage)} vs. 4-week avg)" : "");

    public static string Html(WeeklySpendingReport r)
    {
        var h = new StringBuilder();
        h.Append($"""
            <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>{E(r.Title)}</title></head>
            <body style="margin:0;padding:0;background:#efefec;font-family:Segoe UI,Helvetica,Arial,sans-serif;color:{Ink}">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#efefec"><tr><td align="center" style="padding:20px 10px">
            <table role="presentation" width="640" cellpadding="0" cellspacing="0" style="max-width:640px;width:100%;background:#ffffff;border-radius:10px">
            <tr><td style="padding:22px 26px 6px">
              <div style="font-size:13px;color:{Muted};letter-spacing:.04em;text-transform:uppercase">Hoard · weekly spending</div>
              <div style="font-size:22px;font-weight:600;margin-top:4px">{r.Start:dddd, MMM d} – {r.End:dddd, MMM d, yyyy}</div>
            </td></tr>
            """);

        // KPI row
        h.Append("<tr><td style=\"padding:12px 20px\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"6\"><tr>");
        Kpi(h, "Spent this week", Money(r.Total), $"{r.TransactionCount} transaction(s)");
        Kpi(h, "vs. previous week", Signed(r.Total - r.PriorWeek), $"{Money(r.PriorWeek)} the week before", r.Total - r.PriorWeek);
        Kpi(h, "vs. 4-week average", Signed(r.Total - r.FourWeekAverage), $"average {Money(r.FourWeekAverage)} a week", r.Total - r.FourWeekAverage);
        h.Append("</tr></table></td></tr>");

        if (r.WeeklyIncome is { } income && income > 0)
            Para(h, $"Your average income is about <b>{Money(income)}</b> a week; this week's spending was <b>{Percent0(r.Total / income)}</b> of it.");

        // AI analysis first: it is the part people read.
        Section(h, "Analysis and ways to spend less");
        if (!string.IsNullOrWhiteSpace(r.AiAnalysis))
            h.Append($"<tr><td style=\"padding:0 26px 6px;font-size:14px;line-height:1.5\">{MarkdownToHtml(r.AiAnalysis)}</td></tr>");
        else
            Para(h, E(r.AiNote ?? "No analysis this week."), Muted);

        // 13-week trend as horizontal bars
        Section(h, "Last 13 weeks");
        var max = Math.Max(1m, r.Trend.Select(w => w.Amount).DefaultIfEmpty(0).Max());
        h.Append("<tr><td style=\"padding:0 26px\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"font-size:12px\">");
        foreach (var w in r.Trend)
        {
            var isThis = w.Start == r.Start;
            Bar(h, $"{w.Start:MMM d}", w.Amount, max, isThis ? Accent : "#9fb9d9", bold: isThis);
        }
        h.Append("</table></td></tr>");

        // Days
        Section(h, "By day");
        var dayMax = Math.Max(1m, r.Days.Select(d => d.Amount).DefaultIfEmpty(0).Max());
        h.Append("<tr><td style=\"padding:0 26px\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"font-size:12px\">");
        foreach (var d in r.Days) Bar(h, d.Date.ToString("ddd M/d", CultureInfo.CurrentCulture), d.Amount, dayMax, Accent);
        h.Append("</table></td></tr>");

        // Category groups
        Section(h, "By category group");
        h.Append($"<tr><td style=\"padding:0 26px\"><table role=\"presentation\" width=\"100%\" cellpadding=\"4\" cellspacing=\"0\" style=\"font-size:13px;border-collapse:collapse\">");
        h.Append($"<tr style=\"color:{Muted};font-size:12px\"><td>Group</td><td align=\"right\">This week</td><td align=\"right\">4-wk avg</td><td align=\"right\">Change</td></tr>");
        foreach (var g in r.Groups.Take(14))
            h.Append($"<tr style=\"border-top:1px solid {Line}\"><td>{E(g.Name)}</td><td align=\"right\">{Money(g.Amount)}</td><td align=\"right\" style=\"color:{Muted}\">{Money(g.FourWeekAverage)}</td>" +
                     $"<td align=\"right\" style=\"color:{(g.Change > 0 ? Bad : Good)}\">{Signed(g.Change)}</td></tr>");
        h.Append("</table></td></tr>");

        if (r.TopMerchants.Count > 0)
        {
            Section(h, "Top merchants");
            h.Append($"<tr><td style=\"padding:0 26px\"><table role=\"presentation\" width=\"100%\" cellpadding=\"4\" cellspacing=\"0\" style=\"font-size:13px;border-collapse:collapse\">");
            foreach (var m in r.TopMerchants)
                h.Append($"<tr style=\"border-top:1px solid {Line}\"><td>{E(m.Name)}</td><td align=\"right\" style=\"color:{Muted}\">{m.Count}×</td><td align=\"right\">{Money(m.Amount)}</td></tr>");
            h.Append("</table></td></tr>");
        }

        if (r.Largest.Count > 0)
        {
            Section(h, "Largest charges");
            h.Append($"<tr><td style=\"padding:0 26px\"><table role=\"presentation\" width=\"100%\" cellpadding=\"4\" cellspacing=\"0\" style=\"font-size:13px;border-collapse:collapse\">");
            foreach (var t in r.Largest)
                h.Append($"<tr style=\"border-top:1px solid {Line}\"><td style=\"color:{Muted};white-space:nowrap\">{t.Date:ddd M/d}</td><td>{E(t.Merchant)}<div style=\"color:{Muted};font-size:11px\">{E(t.Category ?? "Uncategorized")} · {E(t.Account)}</div></td><td align=\"right\">{Money(t.Amount, cents: true)}</td></tr>");
            h.Append("</table></td></tr>");
        }

        if (r.Budget is { Target: > 0 } b)
        {
            Section(h, $"Budget, {b.Month:MMMM} so far");
            Para(h, $"{Money(b.Spent)} of {Money(b.Target)} spent in groups with a target (day {b.DayOfMonth} of {b.DaysInMonth}); on pace for <b>{Money(b.Projected)}</b> by month end.");
            foreach (var (group, actual, target) in b.Over)
                Para(h, $"<span style=\"color:{Bad}\">Over:</span> {E(group)} {Money(actual)} of {Money(target)}");
            foreach (var (group, projected, target) in b.AtRisk)
                Para(h, $"<span style=\"color:#b7791f\">At risk:</span> {E(group)} heading for {Money(projected)} against {Money(target)}");
        }

        h.Append($"""
            <tr><td style="padding:18px 26px 22px;font-size:11px;color:{Muted};border-top:1px solid {Line}">
              Spending counts the account types ticked for spending reports in Hoard; transfers, income and transactions marked as duplicates are left out.
              Sent by Hoard on {DateTime.Now:MMM d, yyyy 'at' h:mm tt}. Change the schedule or recipients under Reports › Weekly spending.
            </td></tr>
            </table></td></tr></table></body></html>
            """);
        return h.ToString();
    }

    public static string Text(WeeklySpendingReport r)
    {
        var t = new StringBuilder();
        t.AppendLine(r.Title).AppendLine();
        t.AppendLine($"Spent this week: {Money(r.Total)} ({r.TransactionCount} transactions)");
        t.AppendLine($"Previous week: {Money(r.PriorWeek)}   4-week average: {Money(r.FourWeekAverage)}").AppendLine();
        if (!string.IsNullOrWhiteSpace(r.AiAnalysis)) t.AppendLine(r.AiAnalysis.Trim()).AppendLine();
        else if (r.AiNote is { } note) t.AppendLine(note).AppendLine();
        t.AppendLine("By category group:");
        foreach (var g in r.Groups.Take(14)) t.AppendLine($"  {g.Name,-28} {Money(g.Amount),10}   (4-wk avg {Money(g.FourWeekAverage)})");
        if (r.TopMerchants.Count > 0)
        {
            t.AppendLine().AppendLine("Top merchants:");
            foreach (var m in r.TopMerchants) t.AppendLine($"  {m.Name,-28} {Money(m.Amount),10}  ({m.Count}x)");
        }
        return t.ToString();
    }

    private static void Kpi(StringBuilder h, string label, string value, string detail, decimal? change = null)
    {
        var color = change is { } c ? (c > 0 ? Bad : c < 0 ? Good : Ink) : Ink;
        h.Append($"<td width=\"33%\" valign=\"top\" style=\"background:{Panel};border-radius:8px;padding:10px 12px\">" +
                 $"<div style=\"font-size:12px;color:{Muted}\">{E(label)}</div><div style=\"font-size:20px;font-weight:600;color:{color}\">{E(value)}</div>" +
                 $"<div style=\"font-size:11px;color:{Muted}\">{E(detail)}</div></td>");
    }

    private static void Section(StringBuilder h, string title) =>
        h.Append($"<tr><td style=\"padding:18px 26px 6px;font-size:15px;font-weight:600\">{E(title)}</td></tr>");

    private static void Para(StringBuilder h, string html, string? color = null) =>
        h.Append($"<tr><td style=\"padding:2px 26px;font-size:13px;line-height:1.5{(color is null ? "" : ";color:" + color)}\">{html}</td></tr>");

    private static void Bar(StringBuilder h, string label, decimal amount, decimal max, string color, bool bold = false)
    {
        var pct = (int)Math.Round(Math.Clamp(amount / max, 0, 1) * 100);
        var weight = bold ? "font-weight:600;" : "";
        h.Append($"<tr><td width=\"70\" style=\"padding:2px 6px 2px 0;color:{Muted};white-space:nowrap;{weight}\">{E(label)}</td>" +
                 $"<td style=\"padding:2px 0\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>" +
                 (pct > 0 ? $"<td width=\"{pct}%\" style=\"background:{color};height:12px;border-radius:3px;font-size:1px\">&nbsp;</td>" : "") +
                 $"<td style=\"font-size:1px\">&nbsp;</td></tr></table></td>" +
                 $"<td width=\"80\" align=\"right\" style=\"padding:2px 0 2px 6px;white-space:nowrap;{weight}\">{Money(amount)}</td></tr>");
    }

    private static string Money(decimal v, bool cents = false) => v.ToString(cents || Math.Abs(v) < 100 ? "C2" : "C0", CultureInfo.CurrentCulture);
    private static string Signed(decimal v) => (v > 0 ? "+" : v < 0 ? "−" : "") + Money(Math.Abs(v));
    private static string Percent(decimal v) => v.ToString("+0%;-0%;0%", CultureInfo.CurrentCulture);
    private static string Percent0(decimal v) => v.ToString("0%", CultureInfo.CurrentCulture);
    private static string E(string s) => WebUtility.HtmlEncode(s);

    [GeneratedRegex(@"\*\*(.+?)\*\*")] private static partial Regex Bold();
    [GeneratedRegex(@"(?<![\*\w])\*(?!\s)(.+?)(?<!\s)\*(?!\*)")] private static partial Regex Italic();
    [GeneratedRegex(@"`([^`]+)`")] private static partial Regex Code();
    [GeneratedRegex(@"^\s*(\d+)[.)]\s+(.*)$")] private static partial Regex Numbered();

    /// <summary>The Markdown subset Claude writes (headings, lists, bold/italic/code, paragraphs, simple tables) as inline-styled HTML.</summary>
    public static string MarkdownToHtml(string markdown)
    {
        var html = new StringBuilder();
        string? openList = null;
        var paragraph = new List<string>();
        var table = new List<string[]>();

        string Inline(string text)
        {
            var s = E(text);
            s = Bold().Replace(s, "<b>$1</b>");
            s = Italic().Replace(s, "<i>$1</i>");
            s = Code().Replace(s, "<code style=\"background:#f0f0ee;padding:0 3px;border-radius:3px\">$1</code>");
            return s;
        }
        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            html.Append($"<p style=\"margin:0 0 10px\">{Inline(string.Join(" ", paragraph))}</p>");
            paragraph.Clear();
        }
        void CloseList()
        {
            if (openList is null) return;
            html.Append($"</{openList}>");
            openList = null;
        }
        void FlushTable()
        {
            if (table.Count == 0) return;
            html.Append($"<table cellpadding=\"4\" cellspacing=\"0\" style=\"border-collapse:collapse;font-size:13px;margin:0 0 10px\">");
            for (var i = 0; i < table.Count; i++)
            {
                html.Append("<tr>");
                foreach (var cell in table[i])
                    html.Append(i == 0 ? $"<th align=\"left\" style=\"border-bottom:1px solid {Line}\">{Inline(cell)}</th>" : $"<td style=\"border-bottom:1px solid {Line}\">{Inline(cell)}</td>");
                html.Append("</tr>");
            }
            html.Append("</table>");
            table.Clear();
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('|'))
            {
                FlushParagraph(); CloseList();
                var cells = trimmed.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':' or ' '))) continue; // separator row
                table.Add(cells);
                continue;
            }
            FlushTable();
            if (trimmed.Length == 0) { FlushParagraph(); CloseList(); continue; }
            if (trimmed.StartsWith('#'))
            {
                FlushParagraph(); CloseList();
                var level = trimmed.TakeWhile(c => c == '#').Count();
                var size = level <= 2 ? 16 : 14;
                html.Append($"<div style=\"font-size:{size}px;font-weight:600;margin:12px 0 6px\">{Inline(trimmed[level..].Trim())}</div>");
                continue;
            }
            if (trimmed is "---" or "***") { FlushParagraph(); CloseList(); html.Append($"<hr style=\"border:0;border-top:1px solid {Line}\">"); continue; }
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("• "))
            {
                FlushParagraph();
                if (openList != "ul") { CloseList(); html.Append("<ul style=\"margin:0 0 10px;padding-left:20px\">"); openList = "ul"; }
                html.Append($"<li style=\"margin:0 0 4px\">{Inline(trimmed[2..])}</li>");
                continue;
            }
            if (Numbered().Match(trimmed) is { Success: true } n)
            {
                FlushParagraph();
                if (openList != "ol") { CloseList(); html.Append("<ol style=\"margin:0 0 10px;padding-left:20px\">"); openList = "ol"; }
                html.Append($"<li style=\"margin:0 0 4px\">{Inline(n.Groups[2].Value)}</li>");
                continue;
            }
            CloseList();
            paragraph.Add(trimmed);
        }
        FlushParagraph(); CloseList(); FlushTable();
        return html.ToString();
    }
}
