using System.Text;

namespace Obd2.Core.Obd;

/// <summary>Minimal dependency-free PDF writer for plain-text reports (A4, Courier text, bold title).</summary>
public static class SimplePdf
{
    private const double PageWidth = 595, PageHeight = 842, Margin = 44, LineHeight = 11.5;

    public static byte[] FromText(string title, IEnumerable<string> lines)
    {
        int perPage = (int)((PageHeight - 2 * Margin - 30) / LineHeight);
        var pages = new List<List<string>>();
        var current = new List<string>();
        foreach (var raw in lines)
        {
            foreach (var line in Wrap(raw, 92))
            {
                if (current.Count >= perPage) { pages.Add(current); current = new List<string>(); }
                current.Add(line);
            }
        }
        pages.Add(current);

        // Object numbers: 1 catalog, 2 pages, 3 Courier, 4 Helvetica-Bold, then (page, contents) pairs.
        var objects = new List<string>();
        var pageIds = new List<int>();
        int next = 5;
        var bodies = new List<(int Id, string Body)>();

        for (int i = 0; i < pages.Count; i++)
        {
            int pageId = next++, contentId = next++;
            pageIds.Add(pageId);

            var sb = new StringBuilder();
            sb.Append("BT /F2 14 Tf ").Append(F(Margin)).Append(' ').Append(F(PageHeight - Margin)).Append(" Td (").Append(Escape(title)).Append(") Tj ET\n");
            sb.Append("BT /F2 8 Tf ").Append(F(PageWidth - Margin - 60)).Append(' ').Append(F(PageHeight - Margin)).Append(" Td (Page ").Append(i + 1).Append(" of ").Append(pages.Count).Append(") Tj ET\n");
            double y = PageHeight - Margin - 24;
            foreach (var line in pages[i])
            {
                sb.Append("BT /F1 9 Tf ").Append(F(Margin)).Append(' ').Append(F(y)).Append(" Td (").Append(Escape(line)).Append(") Tj ET\n");
                y -= LineHeight;
            }
            string stream = sb.ToString();

            bodies.Add((pageId, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(PageWidth)} {F(PageHeight)}] /Contents {contentId} 0 R /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> >>"));
            bodies.Add((contentId, $"<< /Length {Encoding.Latin1.GetByteCount(stream)} >>\nstream\n{stream}endstream"));
        }

        var all = new SortedDictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R >>",
            [2] = $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] /Count {pageIds.Count} >>",
            [3] = "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>",
            [4] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
        };
        foreach (var (id, body) in bodies) all[id] = body;

        using var ms = new MemoryStream();
        void Write(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        var offsets = new Dictionary<int, long>();
        foreach (var (id, body) in all)
        {
            offsets[id] = ms.Position;
            Write($"{id} 0 obj\n{body}\nendobj\n");
        }

        long xref = ms.Position;
        int count = all.Keys.Max() + 1;
        Write($"xref\n0 {count}\n0000000000 65535 f \n");
        for (int id = 1; id < count; id++)
            Write($"{offsets[id]:D10} 00000 n \n");
        Write($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static IEnumerable<string> Wrap(string text, int width)
    {
        text = text.Replace("\t", "    ");
        if (text.Length <= width) { yield return text; yield break; }
        while (text.Length > width)
        {
            int cut = text.LastIndexOf(' ', width);
            if (cut <= 0) cut = width;
            yield return text[..cut];
            text = "    " + text[cut..].TrimStart();
        }
        yield return text;
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c == '\\' || c == '(' || c == ')') sb.Append('\\').Append(c);
            else if (c >= 32 && c < 127) sb.Append(c);
            else if (c < 256 && c >= 160) sb.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
            else sb.Append('?');
        }
        return sb.ToString();
    }
}
