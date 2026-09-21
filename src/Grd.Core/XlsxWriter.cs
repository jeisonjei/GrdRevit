using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace GrdRevit.Core
{
    /// <summary>Таблица для книги Excel: имя листа, заголовки и строки (текстовые значения).</summary>
    public sealed class XlsxSheet
    {
        public string Name = string.Empty;
        public List<string> Headers = new List<string>();
        public List<string[]> Rows = new List<string[]>();
    }

    /// <summary>Запись книги Excel (.xlsx) без внешних зависимостей: минимальный OOXML.
    /// Все значения — inline-строки (без формул и локализованных чисел), первый ряд —
    /// шапка (заморожена, жирная); автофильтр и ширина колонок по содержимому.</summary>
    public static class XlsxWriter
    {
        private const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string NsRels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string NsPackage = "http://schemas.openxmlformats.org/package/2006/content-types";
        private const string NsPackageRels = "http://schemas.openxmlformats.org/package/2006/relationships";

        public static void Write(string path, IReadOnlyList<XlsxSheet> sheets)
        {
            if (sheets == null || sheets.Count == 0)
                throw new ArgumentException("Нет листов для записи книги Excel.");
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Не задан путь файла Excel.");

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var names = new string[sheets.Count];
                for (int i = 0; i < sheets.Count; i++)
                    names[i] = UniqueSheetName(sheets[i].Name, used);

                WriteEntry(zip, "[Content_Types].xml", w => WriteContentTypes(w, sheets.Count));
                WriteEntry(zip, "_rels/.rels", WriteRootRels);
                WriteEntry(zip, "xl/workbook.xml", w => WriteWorkbook(w, sheets, names));
                WriteEntry(zip, "xl/_rels/workbook.xml.rels", w => WriteWorkbookRels(w, sheets.Count));
                WriteEntry(zip, "xl/styles.xml", WriteStyles);

                for (int i = 0; i < sheets.Count; i++)
                {
                    var sheet = sheets[i];
                    var name = names[i];
                    WriteEntry(zip, "xl/worksheets/sheet" + (i + 1) + ".xml",
                        w => WriteWorksheet(w, sheet));
                }
            }
        }

        private static void WriteEntry(ZipArchive zip, string name, Action<XmlWriter> write)
        {
            using (var ms = new MemoryStream())
            {
                using (var xw = XmlWriter.Create(ms, new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(false),
                    OmitXmlDeclaration = false,
                    Indent = false,
                    CloseOutput = false
                }))
                {
                    write(xw);
                    xw.Flush();
                }
                ms.Position = 0;
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using (var es = entry.Open())
                    ms.CopyTo(es);
            }
        }

        private static void WriteContentTypes(XmlWriter w, int count)
        {
            w.WriteStartDocument();
            w.WriteStartElement("Types", NsPackage);
            w.WriteStartElement("Default", NsPackage);
            w.WriteAttributeString("Extension", "rels");
            w.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml");
            w.WriteEndElement();
            w.WriteStartElement("Default", NsPackage);
            w.WriteAttributeString("Extension", "xml");
            w.WriteAttributeString("ContentType", "application/xml");
            w.WriteEndElement();
            w.WriteStartElement("Override", NsPackage);
            w.WriteAttributeString("PartName", "/xl/workbook.xml");
            w.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            w.WriteEndElement();
            w.WriteStartElement("Override", NsPackage);
            w.WriteAttributeString("PartName", "/xl/styles.xml");
            w.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
            w.WriteEndElement();
            for (int i = 1; i <= count; i++)
            {
                w.WriteStartElement("Override", NsPackage);
                w.WriteAttributeString("PartName", "/xl/worksheets/sheet" + i + ".xml");
                w.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndDocument();
        }

        private static void WriteRootRels(XmlWriter w)
        {
            w.WriteStartDocument();
            w.WriteStartElement("Relationships", NsPackageRels);
            w.WriteStartElement("Relationship", NsPackageRels);
            w.WriteAttributeString("Id", "rId1");
            w.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument");
            w.WriteAttributeString("Target", "xl/workbook.xml");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndDocument();
        }

        private static void WriteWorkbook(XmlWriter w, IReadOnlyList<XlsxSheet> sheets, string[] names)
        {
            w.WriteStartDocument();
            w.WriteStartElement("workbook", NsMain);
            w.WriteAttributeString("xmlns", "r", null, NsRels);
            w.WriteStartElement("sheets", NsMain);
            for (int i = 0; i < sheets.Count; i++)
            {
                w.WriteStartElement("sheet", NsMain);
                w.WriteAttributeString("name", names[i]);
                w.WriteAttributeString("sheetId", (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                w.WriteAttributeString("r", "id", NsRels, "rId" + (i + 1));
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndDocument();
        }

        private static void WriteWorkbookRels(XmlWriter w, int count)
        {
            w.WriteStartDocument();
            w.WriteStartElement("Relationships", NsPackageRels);
            for (int i = 1; i <= count; i++)
            {
                w.WriteStartElement("Relationship", NsPackageRels);
                w.WriteAttributeString("Id", "rId" + i);
                w.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet");
                w.WriteAttributeString("Target", "worksheets/sheet" + i + ".xml");
                w.WriteEndElement();
            }
            w.WriteStartElement("Relationship", NsPackageRels);
            w.WriteAttributeString("Id", "rId" + (count + 1));
            w.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles");
            w.WriteAttributeString("Target", "styles.xml");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndDocument();
        }

        private static void WriteStyles(XmlWriter w)
        {
            w.WriteStartDocument();
            w.WriteStartElement("styleSheet", NsMain);
            w.WriteStartElement("fonts", NsMain);
            w.WriteAttributeString("count", "2");
            w.WriteStartElement("font", NsMain);
            w.WriteStartElement("sz", NsMain); w.WriteAttributeString("val", "11"); w.WriteEndElement();
            w.WriteStartElement("name", NsMain); w.WriteAttributeString("val", "Calibri"); w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("font", NsMain);
            w.WriteStartElement("b", NsMain); w.WriteEndElement();
            w.WriteStartElement("sz", NsMain); w.WriteAttributeString("val", "11"); w.WriteEndElement();
            w.WriteStartElement("name", NsMain); w.WriteAttributeString("val", "Calibri"); w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("fills", NsMain);
            w.WriteAttributeString("count", "3");
            w.WriteStartElement("fill", NsMain);
            w.WriteStartElement("patternFill", NsMain); w.WriteAttributeString("patternType", "none"); w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("fill", NsMain);
            w.WriteStartElement("patternFill", NsMain); w.WriteAttributeString("patternType", "gray125"); w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("fill", NsMain);
            w.WriteStartElement("patternFill", NsMain);
            w.WriteAttributeString("patternType", "solid");
            w.WriteStartElement("fgColor", NsMain); w.WriteAttributeString("rgb", "FF4472C4"); w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("borders", NsMain);
            w.WriteAttributeString("count", "2");
            w.WriteStartElement("border", NsMain);
            w.WriteStartElement("left", NsMain); w.WriteEndElement();
            w.WriteStartElement("right", NsMain); w.WriteEndElement();
            w.WriteStartElement("top", NsMain); w.WriteEndElement();
            w.WriteStartElement("bottom", NsMain); w.WriteEndElement();
            w.WriteStartElement("diagonal", NsMain); w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("border", NsMain);
            w.WriteStartElement("left", NsMain); w.WriteAttributeString("style", "thin");
            w.WriteStartElement("color", NsMain); w.WriteAttributeString("rgb", "FFB8CCE4"); w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("right", NsMain); w.WriteAttributeString("style", "thin");
            w.WriteStartElement("color", NsMain); w.WriteAttributeString("rgb", "FFB8CCE4"); w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("top", NsMain); w.WriteAttributeString("style", "thin");
            w.WriteStartElement("color", NsMain); w.WriteAttributeString("rgb", "FFB8CCE4"); w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("bottom", NsMain); w.WriteAttributeString("style", "thin");
            w.WriteStartElement("color", NsMain); w.WriteAttributeString("rgb", "FFB8CCE4"); w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("diagonal", NsMain); w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("cellStyleXfs", NsMain);
            w.WriteAttributeString("count", "1");
            w.WriteStartElement("xf", NsMain);
            w.WriteAttributeString("numFmtId", "0");
            w.WriteAttributeString("fontId", "0");
            w.WriteAttributeString("fillId", "0");
            w.WriteAttributeString("borderId", "0");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("cellXfs", NsMain);
            w.WriteAttributeString("count", "2");
            w.WriteStartElement("xf", NsMain);
            w.WriteAttributeString("numFmtId", "0");
            w.WriteAttributeString("fontId", "0");
            w.WriteAttributeString("fillId", "0");
            w.WriteAttributeString("borderId", "1");
            w.WriteAttributeString("xfId", "0");
            w.WriteEndElement();
            w.WriteStartElement("xf", NsMain);
            w.WriteAttributeString("numFmtId", "0");
            w.WriteAttributeString("fontId", "1");
            w.WriteAttributeString("fillId", "2");
            w.WriteAttributeString("borderId", "1");
            w.WriteAttributeString("xfId", "0");
            w.WriteAttributeString("applyFont", "1");
            w.WriteAttributeString("applyFill", "1");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndDocument();
        }

        private static void WriteWorksheet(XmlWriter w, XlsxSheet sheet)
        {
            int colCount = sheet.Headers.Count;
            int rowCount = sheet.Rows.Count;
            var widths = new int[colCount];
            for (int c = 0; c < colCount; c++)
            {
                int len = Len(sheet.Headers[c]);
                if (len > widths[c]) widths[c] = len;
            }
            for (int r = 0; r < rowCount; r++)
            {
                var cells = sheet.Rows[r];
                if (cells == null || cells.Length == 0) continue;
                for (int c = 0; c < cells.Length && c < colCount; c++)
                {
                    int len = Len(cells[c]);
                    if (len > widths[c]) widths[c] = len;
                }
            }

            w.WriteStartDocument();
            w.WriteStartElement("worksheet", NsMain);
            if (colCount > 0 && rowCount > 0)
            {
                w.WriteStartElement("sheetViews", NsMain);
                w.WriteStartElement("sheetView", NsMain);
                w.WriteAttributeString("workbookViewId", "0");
                w.WriteStartElement("pane", NsMain);
                w.WriteAttributeString("ySplit", "1");
                w.WriteAttributeString("topLeftCell", "A2");
                w.WriteAttributeString("activePane", "bottomLeft");
                w.WriteAttributeString("state", "frozen");
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();
            }
            if (colCount > 0)
            {
                w.WriteStartElement("cols", NsMain);
                for (int c = 0; c < colCount; c++)
                {
                    int wid = widths[c] + 3;
                    if (wid < 8) wid = 8;
                    if (wid > 90) wid = 90;
                    w.WriteStartElement("col", NsMain);
                    w.WriteAttributeString("min", (c + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    w.WriteAttributeString("max", (c + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    w.WriteAttributeString("width", wid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    w.WriteAttributeString("customWidth", "1");
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteStartElement("sheetData", NsMain);
            if (colCount > 0)
            {
                w.WriteStartElement("row", NsMain);
                w.WriteAttributeString("r", "1");
                for (int c = 0; c < colCount; c++)
                    WriteCell(w, ColLetter(c) + "1", sheet.Headers[c], true);
                w.WriteEndElement();
                for (int r = 0; r < rowCount; r++)
                {
                    var cells = sheet.Rows[r];
                    w.WriteStartElement("row", NsMain);
                    w.WriteAttributeString("r", (r + 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    for (int c = 0; c < colCount; c++)
                    {
                        string v = cells != null && c < cells.Length ? cells[c] : null;
                        WriteCell(w, ColLetter(c) + (r + 2), v, false);
                    }
                    w.WriteEndElement();
                }
            }
            w.WriteEndElement();
            if (colCount > 0)
            {
                w.WriteStartElement("autoFilter", NsMain);
                w.WriteAttributeString("ref", "A1:" + ColLetter(colCount - 1) + (rowCount + 1));
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndDocument();
        }

        private static void WriteCell(XmlWriter w, string addr, string value, bool header)
        {
            string text = value ?? string.Empty;
            w.WriteStartElement("c", NsMain);
            w.WriteAttributeString("r", addr);
            w.WriteAttributeString("s", header ? "1" : "0");
            w.WriteAttributeString("t", "inlineStr");
            w.WriteStartElement("is", NsMain);
            w.WriteStartElement("t", NsMain);
            w.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
            w.WriteString(Limit(text));
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static string UniqueSheetName(string name, HashSet<string> used)
        {
            var sb = new StringBuilder();
            foreach (char ch in name ?? string.Empty)
            {
                if (ch == '\\' || ch == '/' || ch == '?' || ch == '*' || ch == '[' || ch == ']' ||
                    ch == ':' || ch == '\r' || ch == '\n' || ch == '\t')
                    continue;
                sb.Append(ch);
            }
            string s = sb.ToString().Trim();
            if (s.Length == 0) s = "Лист";
            if (s.Length > 26) s = s.Substring(0, 26);
            if (used.Add(s)) return s;
            int k = 2;
            string candidate;
            do
            {
                candidate = s + " (" + k + ")";
                k++;
            } while (!used.Add(candidate));
            return candidate;
        }

        private static int Len(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            int n = 0;
            foreach (char ch in value)
                n += ch > 127 ? 2 : 1;
            return n;
        }

        private static string Limit(string value)
        {
            if (value == null) return string.Empty;
            if (value.Length > 32000)
                return value.Substring(0, 32000);
            return value;
        }

        private static string ColLetter(int col)
        {
            col++;
            var sb = new StringBuilder();
            while (col > 0)
            {
                col--;
                sb.Insert(0, (char)('A' + col % 26));
                col /= 26;
            }
            return sb.ToString();
        }
    }
}