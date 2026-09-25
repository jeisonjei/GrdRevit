using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace GrdRevit.Core
{
    /// <summary>
    /// Экспорт таблицы в формат LibreOffice Calc (.ods, OpenDocument Spreadsheet).
    /// Минимальный, но валидный ODS: content.xml / styles.xml / meta.xml / manifest
    /// упакованы в ZIP (запись mimetype — первой и без сжатия, как требует ODF).
    /// </summary>
    public static class OdsExporter
    {
        private const string MimeType = "application/vnd.oasis.opendocument.spreadsheet";

        /// <summary>Записывает .ods-файл. rows — строки таблицы (первая обычно заголовок колонок).
        /// Каждая строка — значения ячеек; ширина выравнивается по самой широкой строке.</summary>
        public static void Write(string path, string sheetName, IReadOnlyList<string[]> rows)
        {
            if (rows == null || rows.Count == 0)
                throw new ArgumentException("Нет строк для экспорта.");
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var safeSheet = string.IsNullOrEmpty(sheetName) ? "Лист1" : SanitizeSheetName(sheetName);

            if (File.Exists(path)) File.Delete(path);
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                WriteEntry(zip, "mimetype", MimeType, CompressionLevel.NoCompression);
                WriteEntry(zip, "content.xml", BuildContent(safeSheet, rows), CompressionLevel.Optimal);
                WriteEntry(zip, "styles.xml", BuildStyles(), CompressionLevel.Optimal);
                WriteEntry(zip, "meta.xml", BuildMeta(), CompressionLevel.Optimal);
                WriteEntry(zip, "META-INF/manifest.xml", BuildManifest(), CompressionLevel.Optimal);
            }
        }

        private static void WriteEntry(ZipArchive zip, string name, string text, CompressionLevel level)
        {
            var entry = zip.CreateEntry(name, level);
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                w.Write(text);
        }

        private static string BuildContent(string sheetName, IReadOnlyList<string[]> rows)
        {
            int cols = 0;
            foreach (var r in rows)
                if (r != null && r.Length > cols) cols = r.Length;
            if (cols == 0) cols = 1;

            var sb = new StringBuilder(rows.Count * 64 + 512);
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.Append("<office:document-content")
              .Append(" xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"")
              .Append(" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\"")
              .Append(" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"")
              .Append(" office:version=\"1.2\">");
            sb.Append("<office:body><office:spreadsheet>");
            sb.Append("<table:table table:name=\"").Append(Escape(sheetName)).Append("\">");
            if (cols > 1)
                sb.Append("<table:table-column table:number-columns-repeated=\"").Append(cols).Append("\"/>");
            else
                sb.Append("<table:table-column/>");

            foreach (var r in rows)
            {
                if (r == null) continue;
                sb.Append("<table:table-row>");
                for (int c = 0; c < cols; c++)
                {
                    string v = c < r.Length ? r[c] : string.Empty;
                    AppendCell(sb, v);
                }
                sb.Append("</table:table-row>");
            }

            sb.Append("</table:table></office:spreadsheet></office:body></office:document-content>");
            return sb.ToString();
        }

        private static void AppendCell(StringBuilder sb, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                sb.Append("<table:table-cell/>");
                return;
            }
            // Число (например «3,5» или «12»): кладём как число, чтобы Calc умел считать.
            if (TryParseNumber(value, out double d))
            {
                sb.Append("<table:table-cell office:value-type=\"float\" office:value=\"")
                  .Append(d.ToString("R", CultureInfo.InvariantCulture))
                  .Append("\"/>");
                return;
            }
            sb.Append("<table:table-cell office:value-type=\"string\">");
            foreach (var line in value.Split('\n'))
                sb.Append("<text:p>").Append(Escape(line)).Append("</text:p>");
            sb.Append("</table:table-cell>");
        }

        private static bool TryParseNumber(string v, out double d)
        {
            var s = v.Trim();
            if (s.Length == 0) { d = 0; return false; }
            if (s.IndexOfAny(new[] { '%', ' ' }) >= 0) { d = 0; return false; }
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return true;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out d);
        }

        private static string BuildStyles()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                 + "<office:document-styles"
                 + " xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\""
                 + " xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\""
                 + " xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\""
                 + " office:version=\"1.2\">"
                 + "<office:styles/></office:document-styles>";
        }

        private static string BuildMeta()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                 + "<office:document-meta"
                 + " xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\""
                 + " xmlns:meta=\"urn:oasis:names:tc:opendocument:xmlns:meta:1.0\""
                 + " office:version=\"1.2\">"
                 + "<office:meta><meta:generator>GrdRevit JTOOLS</meta:generator>"
                 + "<meta:creation-date>" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + "</meta:creation-date>"
                 + "</office:meta></office:document-meta>";
        }

        private static string BuildManifest()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                 + "<manifest:manifest"
                 + " xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\""
                 + " manifest:version=\"1.2\">"
                 + "<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"1.2\" manifest:media-type=\""
                 + MimeType + "\"/>"
                 + "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>"
                 + "<manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>"
                 + "<manifest:file-entry manifest:full-path=\"meta.xml\" manifest:media-type=\"text/xml\"/>"
                 + "</manifest:manifest>";
        }

        /// <summary>Имя таблицы/листа ODS: непустое, без недопустимых символов и кавычек.</summary>
        private static string SanitizeSheetName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (c == '/' || c == '\\' || c == '[' || c == ']' || c == ':' || c == '*' || c == '?' ||
                    c == '"' || c == '\'' || c == '=' || c == '`' || c == ';')
                    continue;
                sb.Append(c);
            }
            var s = sb.ToString().Trim();
            return s.Length == 0 ? "Лист1" : (s.Length > 30 ? s.Substring(0, 30) : s);
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 16);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}