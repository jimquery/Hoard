using System.Globalization;
using System.Text;

namespace Hoard.Core.Import;

/// <summary>One row of an exported transaction file, before it is mapped to Hoard accounts and categories.</summary>
public sealed record ImportedTransaction(int Line, DateOnly Date, string Account, string Description, string Category, string? Tags, decimal Amount);

public sealed class ImportParseResult
{
    public List<ImportedTransaction> Rows { get; } = new();
    public List<string> Problems { get; } = new();
    public int SkippedLines => Problems.Count;
}

/// <summary>
/// Reads the Empower (formerly Personal Capital) transaction export:
///   Date,Account,Description,Category,Tags,Amount   (ISO dates; negative amounts are money out)
/// Column order is taken from the header, so files with extra or reordered columns still load.
/// </summary>
public static class CsvTransactionParser
{
    public static ImportParseResult Parse(TextReader reader)
    {
        var result = new ImportParseResult();
        var header = ReadRecord(reader, out _);
        if (header is null) { result.Problems.Add("The file is empty."); return result; }
        var index = header.Select((h, i) => (Name: h.Trim().ToLowerInvariant(), Index: i)).ToDictionary(x => x.Name, x => x.Index);
        int Col(params string[] names) => names.Select(n => index.TryGetValue(n, out var i) ? i : -1).FirstOrDefault(i => i >= 0, -1);
        int date = Col("date"), account = Col("account", "account name"), desc = Col("description", "payee", "name"),
            cat = Col("category"), tags = Col("tags"), amount = Col("amount");
        if (date < 0 || account < 0 || desc < 0 || amount < 0)
        {
            result.Problems.Add("Expected columns Date, Account, Description and Amount (Category and Tags optional). Found: " + string.Join(", ", header));
            return result;
        }

        var line = 1;
        while (true)
        {
            var fields = ReadRecord(reader, out var consumed);
            if (fields is null) break;
            line += consumed;
            if (fields.All(string.IsNullOrWhiteSpace)) continue;
            string F(int i) => i >= 0 && i < fields.Count ? fields[i].Trim() : "";
            if (!TryParseDate(F(date), out var d)) { result.Problems.Add($"Line {line}: unreadable date \"{F(date)}\"."); continue; }
            if (!TryParseAmount(F(amount), out var amt)) { result.Problems.Add($"Line {line}: unreadable amount \"{F(amount)}\"."); continue; }
            var acct = F(account);
            if (acct.Length == 0) { result.Problems.Add($"Line {line}: no account."); continue; }
            result.Rows.Add(new ImportedTransaction(line, d, acct, F(desc), F(cat), tags >= 0 ? NullIfEmpty(F(tags)) : null, amt));
        }
        return result;
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    public static bool TryParseDate(string text, out DateOnly date)
    {
        string[] formats = ["yyyy-MM-dd", "yyyy/MM/dd", "M/d/yyyy", "MM/dd/yyyy", "M/d/yy", "yyyy-MM-ddTHH:mm:ss"];
        if (DateOnly.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) { date = DateOnly.FromDateTime(dt); return true; }
        return false;
    }

    public static bool TryParseAmount(string text, out decimal amount)
    {
        var t = text.Replace("$", "").Replace(",", "").Trim();
        var negative = false;
        if (t.StartsWith('(') && t.EndsWith(')')) { negative = true; t = t[1..^1]; }
        if (!decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out amount)) return false;
        if (negative) amount = -amount;
        return true;
    }

    /// <summary>RFC 4180 record reader: quoted fields may contain commas, doubled quotes and line breaks.</summary>
    private static List<string>? ReadRecord(TextReader reader, out int linesConsumed)
    {
        linesConsumed = 0;
        var first = reader.ReadLine();
        if (first is null) return null;
        linesConsumed = 1;
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var text = first;
        var i = 0;
        while (true)
        {
            if (i >= text.Length)
            {
                if (!inQuotes) break;
                var more = reader.ReadLine();
                if (more is null) break;
                linesConsumed++;
                sb.Append('\n');
                text = more; i = 0;
                continue;
            }
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;
                }
                sb.Append(c); i++;
            }
            else if (c == '"') { inQuotes = true; i++; }
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); i++; }
            else { sb.Append(c); i++; }
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
