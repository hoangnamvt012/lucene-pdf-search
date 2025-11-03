// Program.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// iText7
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;

// Lucene.NET
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Analysis.Util;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;

// Alias tránh xung đột Directory
using LuceneDirectory = Lucene.Net.Store.Directory;
using FSDirectory = Lucene.Net.Store.FSDirectory;

class Program
{
    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        // === ĐƯỜNG DẪN CỦA BẠN ===
        string pdfFolder = @"C:\Users\Hoang Nam\Downloads\luat";
        string indexPath = Path.Combine(AppContext.BaseDirectory, "LuceneIndex");
        string htmlPath = Path.Combine(AppContext.BaseDirectory, "search-result.html");

        var luceneVersion = LuceneVersion.LUCENE_48;
        Analyzer analyzer = new StandardAnalyzer(luceneVersion, CharArraySet.EMPTY_SET);
        LuceneDirectory luceneDir = FSDirectory.Open(indexPath);

        // ===== Indexing =====
        var indexConfig = new IndexWriterConfig(luceneVersion, analyzer)
        {
            OpenMode = OpenMode.CREATE_OR_APPEND
        };

        using (var writer = new IndexWriter(luceneDir, indexConfig))
        {
            if (!System.IO.Directory.Exists(pdfFolder))
            {
                Console.WriteLine($"Thư mục không tồn tại: {pdfFolder}");
                return;
            }

            var pdfFiles = System.IO.Directory.GetFiles(pdfFolder, "*.pdf", SearchOption.TopDirectoryOnly);
            Console.WriteLine($"Đang lập chỉ mục {pdfFiles.Length} file...");

            foreach (var pdfPath in pdfFiles)
            {
                string content;
                try { content = ExtractTextFromPdf(pdfPath); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Không đọc được PDF: {pdfPath}\n{ex.Message}");
                    continue;
                }

                string filename = Path.GetFileName(pdfPath);
                string filepath = Path.GetFullPath(pdfPath);
                string lastModified = File.GetLastWriteTimeUtc(pdfPath).Ticks.ToString();

                bool needsUpdate = true;
                using var searchReader = writer.GetReader(applyAllDeletes: true);
                var searcher = new IndexSearcher(searchReader);
                var existsQuery = new TermQuery(new Term("filepath", filepath));
                var found = searcher.Search(existsQuery, 1);

                if (found.TotalHits > 0)
                {
                    var oldDoc = searcher.Doc(found.ScoreDocs[0].Doc);
                    if (oldDoc.Get("lastmodified") == lastModified) needsUpdate = false;
                }

                if (needsUpdate)
                {
                    writer.DeleteDocuments(new Term("filepath", filepath));

                    var doc = new Document
                    {
                        new StringField("filename", filename, Field.Store.YES),
                        new StringField("filepath", filepath, Field.Store.YES),
                        new StringField("lastmodified", lastModified, Field.Store.YES),
                        new TextField("content", content ?? string.Empty, Field.Store.YES) // store để cắt snippet & đếm
                    };

                    writer.AddDocument(doc);
                    Console.WriteLine($"Đã index/cập nhật: {filename}");
                }
                else
                {
                    Console.WriteLine($"Không đổi, bỏ qua: {filename}");
                }
            }

            writer.Flush(triggerMerge: false, applyAllDeletes: true);
        }

        // ===== Nhập CỤM cần tìm =====
        Console.WriteLine("\nIndex xong. Nhập 1 CỤM từ cần tìm (có thể có hoặc không có dấu \"):");
        Console.Write("Cụm cần tìm: ");
        string rawPhrase = (Console.ReadLine() ?? "").Trim();
        if (string.IsNullOrEmpty(rawPhrase))
        {
            Console.WriteLine("Bạn chưa nhập cụm.");
            return;
        }

        string phrase = TrimQuotes(rawPhrase);
        if (string.IsNullOrEmpty(phrase))
        {
            Console.WriteLine("Cụm rỗng sau khi chuẩn hoá.");
            return;
        }

        // ===== Tạo PhraseQuery CHÍNH XÁC (API của Lucene.Net 4.8) =====
        var tokens = AnalyzeToTerms(analyzer, "content", phrase);
        if (tokens.Count == 0)
        {
            Console.WriteLine("Cụm không tạo được token nào (kiểm tra Analyzer).");
            return;
        }

        var pq = new PhraseQuery();          // <<<< dùng API cũ, không có Builder
        foreach (var t in tokens) pq.Add(new Term("content", t));
        pq.Slop = 0;                         // phải liền nhau, đúng thứ tự

        // ===== Search =====
        using var readerFinal = DirectoryReader.Open(luceneDir);
        var searcherFinal = new IndexSearcher(readerFinal);
        TopDocs topDocsFinal = searcherFinal.Search(pq, 50);

        // ===== Build HTML (SNIPPETS & ĐẾM cho CỤM) =====
        var html = new StringBuilder();
        html.AppendLine("<!doctype html><html lang='vi'><head><meta charset='utf-8'/>");
        html.AppendLine("<meta name='viewport' content='width=device-width, initial-scale=1'/>");
        html.AppendLine("<title>Kết quả tìm kiếm (cụm)</title>");
        html.AppendLine("<style>");
        html.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;max-width:980px;margin:40px auto;padding:0 16px;}");
        html.AppendLine("h1{font-size:20px;margin:0 0 6px} .meta{color:#666;font-size:12px;margin:0 0 18px}");
        html.AppendLine(".item{padding:16px 0;border-bottom:1px solid #eee}");
        html.AppendLine(".fn{font-weight:600;margin:0 0 2px} .path{color:#6b7280;font-size:12px;margin:0 0 10px;word-break:break-all}");
        html.AppendLine(".counts{list-style:none;padding:0;margin:0 0 10px;display:flex;flex-wrap:wrap;gap:10px}");
        html.AppendLine(".counts li{background:#f3f4f6;border:1px solid #e5e7eb;border-radius:8px;padding:4px 8px;font-size:12px}");
        html.AppendLine(".snip{margin:8px 0 10px;white-space:pre-wrap;line-height:1.55}");
        html.AppendLine(".snip:before{content:'…';} .snip:after{content:' …';}");
        html.AppendLine("mark{background:#fff39a;padding:0 2px;border-radius:3px}");
        html.AppendLine("</style></head><body>");

        html.AppendLine($"<h1>Kết quả cho <code>&quot;{HtmlEscape(phrase)}&quot; (cụm)</code></h1>");
        html.AppendLine($"<p class='meta'>Tổng số tài liệu khớp: {topDocsFinal.TotalHits}</p>");

        if (topDocsFinal.TotalHits == 0)
        {
            html.AppendLine("<p>Không có kết quả.</p>");
        }
        else
        {
            foreach (var sd in topDocsFinal.ScoreDocs)
            {
                var doc = searcherFinal.Doc(sd.Doc);
                string filename = doc.Get("filename");
                string filepath = doc.Get("filepath");
                string content = doc.Get("content");
                string fileUri = new Uri(filepath).AbsoluteUri;

                int count = CountPhraseOccurrences(content, phrase); // đếm cụm

                var snippets = BuildPhraseSnippets(content, phrase, maxSnippets: 5, window: 250);

                html.AppendLine("<div class='item'>");
                html.AppendLine($"  <p class='fn'><a href='{HtmlEscape(fileUri)}'>{HtmlEscape(filename)}</a></p>");
                html.AppendLine($"  <p class='path'>{HtmlEscape(filepath)}</p>");
                html.AppendLine($"  <ul class='counts'><li><b>&quot;{HtmlEscape(phrase)}&quot; <i>(cụm)</i>: {count} lần</li></ul>");
                foreach (var sn in snippets)
                    html.AppendLine($"  <div class='snip'>{sn}</div>");
                html.AppendLine("</div>");
            }
        }

        html.AppendLine("</body></html>");

        File.WriteAllText(htmlPath, html.ToString(), Encoding.UTF8);
        Console.WriteLine($"Đã xuất HTML: {htmlPath}");

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo { FileName = htmlPath, UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch { /* ignore */ }
    }

    // ===== Helpers =====

    static string ExtractTextFromPdf(string pdfPath)
    {
        var sb = new StringBuilder();
        using (var reader = new PdfReader(pdfPath))
        using (var pdfDoc = new PdfDocument(reader))
        {
            int n = pdfDoc.GetNumberOfPages();
            for (int i = 1; i <= n; i++)
            {
                string pageText = PdfTextExtractor.GetTextFromPage(pdfDoc.GetPage(i));
                if (!string.IsNullOrEmpty(pageText))
                    sb.AppendLine(pageText);
            }
        }
        return sb.ToString();
    }

    static string TrimQuotes(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s.StartsWith("\"") && s.EndsWith("\""))
            return s.Substring(1, s.Length - 2).Trim();
        return s;
    }

    // Phân tích cụm theo Analyzer để lấy danh sách token (đồng bộ với index)
    static List<string> AnalyzeToTerms(Analyzer analyzer, string field, string text)
    {
        var list = new List<string>();
        using var tokenStream = analyzer.GetTokenStream(field, new StringReader(text));
        var termAttr = tokenStream.AddAttribute<ICharTermAttribute>();
        tokenStream.Reset();
        while (tokenStream.IncrementToken())
        {
            list.Add(termAttr.ToString());
        }
        tokenStream.End();
        return list;
    }

    // Đếm số lần CỤM xuất hiện (case-insensitive), ràng buộc biên từ Unicode
    static int CountPhraseOccurrences(string content, string phrase)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(phrase)) return 0;

        string core = Regex.Escape(phrase);
        const string BORDER = @"[\p{L}\p{N}]";
        string pattern = $"(?<!{BORDER}){core}(?!{BORDER})";
        var rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return rx.Matches(content).Count;
    }

    static string HtmlEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    // Tạo các snippet quanh match của CỤM, highlight bằng <mark>
    static List<string> BuildPhraseSnippets(string content, string phrase, int maxSnippets = 5, int window = 250)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(phrase)) return result;

        string core = Regex.Escape(phrase);
        const string BORDER = @"[\p{L}\p{N}]";
        string pattern = $"(?<!{BORDER}){core}(?!{BORDER})";
        var rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var matches = rx.Matches(content);
        if (matches.Count == 0) return result;

        var ranges = new List<(int Start, int End)>();
        foreach (Match m in matches)
        {
            int start = Math.Max(0, m.Index - window / 2);
            int end = Math.Min(content.Length, m.Index + m.Length + window / 2);
            ranges.Add((start, end));
        }
        ranges = MergeRanges(ranges);
        if (ranges.Count > maxSnippets) ranges = ranges.Take(maxSnippets).ToList();

        foreach (var (Start, End) in ranges)
        {
            string slice = content.Substring(Start, End - Start);
            string withMarkers = rx.Replace(slice, mm => "__H__" + mm.Value + "__E__");
            string escaped = HtmlEscape(withMarkers);
            string highlighted = escaped.Replace("__H__", "<mark>").Replace("__E__", "</mark>");
            result.Add(highlighted);
        }
        return result;
    }

    // Gộp các khoảng chồng lắp
    static List<(int Start, int End)> MergeRanges(List<(int Start, int End)> ranges)
    {
        if (ranges.Count <= 1) return ranges.OrderBy(r => r.Start).ToList();
        var sorted = ranges.OrderBy(r => r.Start).ToList();
        var merged = new List<(int Start, int End)>();
        var cur = sorted[0];

        for (int i = 1; i < sorted.Count; i++)
        {
            var r = sorted[i];
            if (r.Start <= cur.End) cur = (cur.Start, Math.Max(cur.End, r.End));
            else { merged.Add(cur); cur = r; }
        }
        merged.Add(cur);
        return merged;
    }
}
