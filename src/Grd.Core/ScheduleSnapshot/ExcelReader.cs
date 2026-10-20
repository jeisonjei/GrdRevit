using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using Spreadsheet = DocumentFormat.OpenXml.Spreadsheet;

namespace GrdRevit.Core
{
    /// <summary>Объединённый диапазон листа Excel, приведённый к координатам таблицы
    /// (0-based, включительно, относительно верхнего левого угла выбранного диапазона).</summary>
    public sealed class ExcelMerge
    {
        public int Top;
        public int Left;
        public int Bottom;
        public int Right;
    }

    /// <summary>Таблица, готовая к записи в шапку спецификации Revit: заголовки, строки
    /// и (опционально) объединения ячеек относительно этой таблицы.</summary>
    public sealed class ExcelTable
    {
        public string SheetName = string.Empty;
        public List<string> Headers = new List<string>();
        public List<string[]> Rows = new List<string[]>();
        public List<ExcelMerge> Merges = new List<ExcelMerge>();

        /// <summary>Ширина каждой колонки в миллиметрах (по индексу колонки). Пусто — авто.</summary>
        public List<double> ColumnWidthsMm = new List<double>();

        /// <summary>Высота каждой строки в миллиметрах. 0 — авто (не задавать).</summary>
        public double RowHeightMm;

        /// <summary>Число колонок таблицы (по ширине заголовков/строк).</summary>
        public int ColumnCount
        {
            get
            {
                int n = Headers.Count;
                foreach (var row in Rows)
                    if (row != null && row.Length > n) n = row.Length;
                return n;
            }
        }

        /// <summary>Полная высота таблицы, включая строку заголовков.</summary>
        public int RowCount => (Headers.Count > 0 ? 1 : 0) + Rows.Count;

        /// <summary>Строка таблицы по индексу (0 — заголовки), с выравниванием по колонкам.</summary>
        public string[] RowAt(int index)
        {
            if (index == 0 && Headers.Count > 0)
            {
                var h = new string[ColumnCount];
                for (int c = 0; c < h.Length; c++) h[c] = c < Headers.Count ? Headers[c] : string.Empty;
                return h;
            }
            int ri = Headers.Count > 0 ? index - 1 : index;
            var src = ri >= 0 && ri < Rows.Count ? Rows[ri] : null;
            var arr = new string[ColumnCount];
            if (src != null)
                for (int c = 0; c < arr.Length; c++) arr[c] = c < src.Length ? src[c] : string.Empty;
            return arr;
        }
    }

    /// <summary>Прямоугольная сетка листа Excel в границах фактически заполненных ячеек.
    /// Индексы 0-based относительно <see cref="FirstRow"/>/<see cref="FirstCol"/>.</summary>
    public sealed class ExcelGrid
    {
        public string SheetName = string.Empty;
        public int FirstRow;
        public int FirstCol;
        public List<string[]> Cells = new List<string[]>();
        public List<ExcelMerge> Merges = new List<ExcelMerge>();

        public int RowCount => Cells.Count;
        public int ColumnCount => Cells.Count == 0 ? 0 : Cells[0].Length;

        public string this[int r, int c] =>
            r >= 0 && r < RowCount && c >= 0 && c < ColumnCount ? Cells[r][c] : string.Empty;

        /// <summary>Пустая ли сетка (нет заполненных ячеек).</summary>
        public bool IsEmpty => RowCount == 0 || ColumnCount == 0;
    }

    /// <summary>Чтение книг Excel (.xlsx/.xlsm) через Open XML SDK: имена листов, значения
    /// ячеек (общие/встроенные строки, числа, даты, кэш формул) и объединения.
    /// Система дат — 1900 (как в Excel).</summary>
    public static class ExcelReader
    {
        public static List<string> ListSheetNames(string path)
        {
            using (var doc = SpreadsheetDocument.Open(path, false))
            {
                var wb = doc.WorkbookPart?.Workbook;
                if (wb == null) return new List<string>();
                return wb.Sheets.Elements<Spreadsheet.Sheet>()
                    .Select(s => s.Name != null ? s.Name.Value ?? string.Empty : string.Empty)
                    .Where(n => n.Length > 0)
                    .ToList();
            }
        }

        /// <summary>Читает лист целиком в границах заполненных ячеек.</summary>
        public static ExcelGrid ReadGrid(string path, string sheetName)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Не задан путь к книге Excel.");
            using (var doc = SpreadsheetDocument.Open(path, false))
            {
                var wbPart = doc.WorkbookPart;
                if (wbPart == null) throw new InvalidOperationException("В книге нет части workbook.");

                var sheet = wbPart.Workbook.Sheets.Elements<Spreadsheet.Sheet>()
                    .FirstOrDefault(s => string.Equals(s.Name?.Value, sheetName, StringComparison.OrdinalIgnoreCase));
                if (sheet == null)
                    throw new InvalidOperationException("Лист «" + sheetName + "» не найден в книге.");

                var wsPart = wbPart.GetPartById(sheet.Id.Value) as WorksheetPart;
                if (wsPart == null)
                    throw new InvalidOperationException("Не удалось открыть лист «" + sheetName + "».");

                var shared = wbPart.SharedStringTablePart?.SharedStringTable;
                var dateStyles = BuildDateStyleSet(wbPart);

                var grid = new ExcelGrid { SheetName = sheetName };

                int minRow = int.MaxValue, minCol = int.MaxValue, maxRow = -1, maxCol = -1;
                var raw = new Dictionary<long, string>();

                foreach (var row in wsPart.Worksheet.Descendants<Spreadsheet.Row>())
                {
                    foreach (var cell in row.Elements<Spreadsheet.Cell>())
                    {
                        int crow, ccol;
                        if (!TryParseReference(cell.CellReference?.Value, out crow, out ccol))
                        {
                            if (row.RowIndex == null) continue;
                            crow = (int)row.RowIndex.Value - 1;
                            ccol = 0;
                        }
                        string text = CellText(cell, shared, dateStyles);
                        if (text.Length == 0) continue;

                        raw[(long)crow * 100000L + ccol] = text;
                        if (crow < minRow) minRow = crow;
                        if (crow > maxRow) maxRow = crow;
                        if (ccol < minCol) minCol = ccol;
                        if (ccol > maxCol) maxCol = ccol;
                    }
                }

                if (maxRow < 0 || maxCol < 0)
                    return grid;

                grid.FirstRow = minRow;
                grid.FirstCol = minCol;

                int width = maxCol - minCol + 1;
                for (int r = minRow; r <= maxRow; r++)
                {
                    var line = new string[width];
                    for (int c = 0; c < width; c++)
                    {
                        if (raw.TryGetValue((long)r * 100000L + (minCol + c), out var v)) line[c] = v;
                        else line[c] = string.Empty;
                    }
                    grid.Cells.Add(line);
                }

                foreach (var merge in wsPart.Worksheet.Elements<Spreadsheet.MergeCells>()
                             .SelectMany(mc => mc.Elements<Spreadsheet.MergeCell>()))
                {
                    int r1, c1, r2, c2;
                    if (!TryParseRange(merge.Reference?.Value, out r1, out c1, out r2, out c2)) continue;
                    if (r2 < minRow || r1 > maxRow || c2 < minCol || c1 > maxCol) continue;
                    grid.Merges.Add(new ExcelMerge
                    {
                        Top = r1 - minRow,
                        Left = c1 - minCol,
                        Bottom = Math.Min(r2, maxRow) - minRow,
                        Right = Math.Min(c2, maxCol) - minCol
                    });
                }

                return grid;
            }
        }

        /// <summary>Вырезает из сетки выбранный диапазон и собирает таблицу.
        /// Координаты — 0-based, включительно, относительно сетки.</summary>
        public static ExcelTable BuildTable(ExcelGrid grid, int r1, int c1, int r2, int c2,
                                            bool firstRowHeaders, bool includeMerges)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            int rr1 = Math.Min(r1, r2), rr2 = Math.Max(r1, r2);
            int cc1 = Math.Min(c1, c2), cc2 = Math.Max(c1, c2);
            rr1 = Math.Max(0, rr1);
            cc1 = Math.Max(0, cc1);
            if (grid.RowCount > 0) rr2 = Math.Min(grid.RowCount - 1, rr2);
            if (grid.ColumnCount > 0) cc2 = Math.Min(grid.ColumnCount - 1, cc2);

            var table = new ExcelTable { SheetName = grid.SheetName };
            int width = cc2 - cc1 + 1;
            if (width <= 0) return table;

            int firstData = rr1;
            if (firstRowHeaders)
            {
                for (int c = cc1; c <= cc2; c++) table.Headers.Add(grid[rr1, c]);
                firstData = rr1 + 1;
            }

            for (int r = firstData; r <= rr2; r++)
            {
                var line = new string[width];
                for (int c = 0; c < width; c++) line[c] = grid[r, cc1 + c];
                table.Rows.Add(line);
            }

            if (includeMerges && grid.RowCount > 0 && grid.ColumnCount > 0)
            {
                foreach (var m in grid.Merges)
                {
                    int t = Math.Max(m.Top, rr1);
                    int l = Math.Max(m.Left, cc1);
                    int b = Math.Min(m.Bottom, rr2);
                    int ri = Math.Min(m.Right, cc2);
                    if (b <= t && ri <= l) continue;
                    table.Merges.Add(new ExcelMerge
                    {
                        Top = t - rr1,
                        Left = l - cc1,
                        Bottom = b - rr1,
                        Right = ri - cc1
                    });
                }
            }

            return table;
        }

        // ------------------------------------------------------------------ разбор ячеек

        private static string CellText(Spreadsheet.Cell cell, Spreadsheet.SharedStringTable shared, bool[] dateStyles)
        {
            var t = cell.DataType?.Value;

            if (t == Spreadsheet.CellValues.InlineString)
            {
                var inline = cell.InlineString;
                return inline != null ? (inline.Text?.Text ?? inline.InnerText ?? string.Empty) : string.Empty;
            }

            string raw = cell.CellValue?.InnerText ?? string.Empty;
            if (raw.Length == 0) return string.Empty;

            if (t == Spreadsheet.CellValues.SharedString)
            {
                if (shared == null) return string.Empty;
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)) return string.Empty;
                var items = shared.Elements<Spreadsheet.SharedStringItem>().ToList();
                if (idx < 0 || idx >= items.Count) return string.Empty;
                return items[idx].InnerText ?? string.Empty;
            }

            if (t == Spreadsheet.CellValues.Boolean)
                return raw == "1" ? "ИСТИНА" : "ЛОЖЬ";

            // число или кэш формулы
            double number;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                if (IsDateStyle(cell.StyleIndex, dateStyles))
                {
                    try
                    {
                        var dt = DateTime.FromOADate(number);
                        if (dt.TimeOfDay == TimeSpan.Zero)
                            return dt.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
                        return dt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
                    }
                    catch { /* вне диапазона дат — вернём как есть */ }
                }
                if (number == Math.Floor(number) && Math.Abs(number) < 1e15)
                    return ((long)number).ToString(CultureInfo.InvariantCulture);
                return number.ToString(CultureInfo.InvariantCulture);
            }

            return raw;
        }

        private static bool IsDateStyle(DocumentFormat.OpenXml.UInt32Value styleIndex, bool[] dateStyles)
        {
            if (dateStyles == null || styleIndex == null) return false;
            int idx = (int)styleIndex.Value;
            return idx >= 0 && idx < dateStyles.Length && dateStyles[idx];
        }

        /// <summary>Для каждого CellFormat определяет, является ли он форматом даты.</summary>
        private static bool[] BuildDateStyleSet(WorkbookPart wbPart)
        {
            try
            {
                var styles = wbPart.WorkbookStylesPart?.Stylesheet;
                if (styles == null) return Array.Empty<bool>();

                var customDates = new HashSet<uint>();
                var numbering = styles.NumberingFormats;
                if (numbering != null)
                {
                    foreach (var nf in numbering.Elements<Spreadsheet.NumberingFormat>())
                    {
                        if (nf.NumberFormatId == null) continue;
                        if (LooksLikeDateFormat(nf.FormatCode?.Value))
                            customDates.Add(nf.NumberFormatId.Value);
                    }
                }

                var formats = styles.CellFormats;
                if (formats == null) return Array.Empty<bool>();
                var list = formats.Elements<Spreadsheet.CellFormat>().ToList();
                var result = new bool[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    uint numFmtId = list[i].NumberFormatId != null ? list[i].NumberFormatId.Value : 0u;
                    result[i] = IsBuiltinDateFormat(numFmtId) || customDates.Contains(numFmtId);
                }
                return result;
            }
            catch
            {
                return Array.Empty<bool>();
            }
        }

        private static bool IsBuiltinDateFormat(uint id)
        {
            // 14..22 — форматы даты/времени, 45..47 — время/дата, 27..36 — восточноазиатские даты
            return (id >= 14 && id <= 22) || (id >= 27 && id <= 36) || (id >= 45 && id <= 47);
        }

        private static bool LooksLikeDateFormat(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            bool inQuote = false;
            foreach (char ch in code)
            {
                if (ch == '"') { inQuote = !inQuote; continue; }
                if (inQuote) continue;
                if (ch == '\\' || ch == '_' || ch == '*') continue;
                if (ch == 'd' || ch == 'D' || ch == 'y' || ch == 'Y') return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ адреса

        /// <summary>"B3" -> row=2, col=1 (0-based).</summary>
        public static bool TryParseReference(string reference, out int row, out int col)
        {
            row = 0; col = 0;
            if (string.IsNullOrEmpty(reference)) return false;
            int i = 0;
            int column = 0;
            bool hasCol = false;
            while (i < reference.Length && char.IsLetter(reference[i]))
            {
                char ch = char.ToUpperInvariant(reference[i]);
                column = column * 26 + (ch - 'A' + 1);
                hasCol = true;
                i++;
            }
            if (!hasCol) return false;
            int r = 0;
            int digits = 0;
            while (i < reference.Length && char.IsDigit(reference[i]))
            {
                r = r * 10 + (reference[i] - '0');
                digits++;
                i++;
            }
            if (digits == 0) return false;
            row = r - 1;
            col = column - 1;
            return row >= 0 && col >= 0;
        }

        /// <summary>"A1:C3" -> r1=0,c1=0,r2=2,c2=2 (0-based). </summary>
        public static bool TryParseRange(string reference, out int r1, out int c1, out int r2, out int c2)
        {
            r1 = c1 = r2 = c2 = 0;
            if (string.IsNullOrEmpty(reference)) return false;
            int colon = reference.IndexOf(':');
            string a = colon < 0 ? reference : reference.Substring(0, colon);
            string b = colon < 0 ? reference : reference.Substring(colon + 1);
            if (!TryParseReference(a, out r1, out c1)) return false;
            if (!TryParseReference(b, out r2, out c2)) return false;
            if (r2 < r1) { int t = r1; r1 = r2; r2 = t; }
            if (c2 < c1) { int t = c1; c1 = c2; c2 = t; }
            return true;
        }

        /// <summary>0-based индекс колонки -> буквенное имя ("A", "B", ..., "AA").</summary>
        public static string ColumnName(int col)
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

        /// <summary>Диапазон в A1-нотации по 0-based координатам.</summary>
        public static string FormatRange(int r1, int c1, int r2, int c2)
        {
            int rr1 = Math.Min(r1, r2), rr2 = Math.Max(r1, r2);
            int cc1 = Math.Min(c1, c2), cc2 = Math.Max(c1, c2);
            string a = ColumnName(cc1) + (rr1 + 1).ToString(CultureInfo.InvariantCulture);
            if (rr1 == rr2 && cc1 == cc2) return a;
            string b = ColumnName(cc2) + (rr2 + 1).ToString(CultureInfo.InvariantCulture);
            return a + ":" + b;
        }

        /// <summary>Размеры выделения: (rows, cols).</summary>
        public static void SelectionSize(int r1, int c1, int r2, int c2, out int rows, out int cols)
        {
            rows = Math.Abs(r2 - r1) + 1;
            cols = Math.Abs(c2 - c1) + 1;
        }
    }
}
