using System;
using System.Collections.Generic;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using GrdRevit.Core;
using Spreadsheet = DocumentFormat.OpenXml.Spreadsheet;
using Xunit;

namespace GrdRevit.Core.Tests
{
    /// <summary>Чтение книг Excel: имена листов, значения ячеек (встроенные/общие строки,
    /// числа, даты), объединения и вырезание выбранного диапазона в таблицу.</summary>
    public class ExcelReaderTests
    {
        private static string Tmp(string ext)
        {
            var dir = Path.Combine(Path.GetTempPath(), "grdtests");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "excel_" + Guid.NewGuid().ToString("N") + ext);
        }

        [Fact]
        public void RoundTrip_WithXlsxWriter_ReadsHeadersAndRows()
        {
            var path = Tmp(".xlsx");
            try
            {
                var sheet = new XlsxSheet
                {
                    Name = "Таблица",
                    Headers = new List<string> { "№", "Наименование", "Кол." },
                    Rows = new List<string[]>
                    {
                        new[] { "1", "Клапан", "2" },
                        new[] { "2", "Воздухоотводчик", "8" }
                    }
                };
                XlsxWriter.Write(path, new[] { sheet });

                Assert.Equal(new[] { "Таблица" }, ExcelReader.ListSheetNames(path));

                var grid = ExcelReader.ReadGrid(path, "Таблица");
                Assert.Equal(3, grid.RowCount);
                Assert.Equal(3, grid.ColumnCount);
                Assert.Equal("№", grid[0, 0]);
                Assert.Equal("Воздухоотводчик", grid[2, 1]);

                var table = ExcelReader.BuildTable(grid, 0, 0, grid.RowCount - 1, 2, true, true);
                Assert.Equal(new[] { "№", "Наименование", "Кол." }, table.Headers);
                Assert.Equal(2, table.Rows.Count);
                Assert.Equal(new[] { "1", "Клапан", "2" }, table.Rows[0]);
                Assert.Equal(3, table.ColumnCount);
            }
            finally { TryDelete(path); }
        }

        [Fact]
        public void BuildTable_SlicesSubRange_AndShiftsMerges()
        {
            var grid = new ExcelGrid { SheetName = "S" };
            grid.Cells.Add(new[] { "заг", string.Empty, string.Empty });
            grid.Cells.Add(new[] { "a", "b", "c" });
            grid.Cells.Add(new[] { "d", "e", "f" });
            grid.Merges.Add(new ExcelMerge { Top = 0, Left = 0, Bottom = 0, Right = 2 });

            // Берём только столбцы 1..2 (b,c / e,f), заголовков нет
            var table = ExcelReader.BuildTable(grid, 1, 1, 2, 2, false, true);
            Assert.Equal(2, table.Rows.Count);
            Assert.Equal(new[] { "b", "c" }, table.Rows[0]);
            Assert.Equal(new[] { "e", "f" }, table.Rows[1]);

            // Заголовки из первой строки, объединение урезается до выбранного
            var withHeader = ExcelReader.BuildTable(grid, 0, 1, 2, 2, true, true);
            Assert.Equal(new[] { "", "" }, withHeader.Headers);
            Assert.Equal(2, withHeader.Rows.Count);
            Assert.Single(withHeader.Merges);
            Assert.Equal(0, withHeader.Merges[0].Left);
            Assert.Equal(1, withHeader.Merges[0].Right);
        }

        [Fact]
        public void ReadGrid_SharedStrings_Merges_AndDates()
        {
            var path = Tmp(".xlsx");
            try
            {
                BuildRichBook(path);

                var grid = ExcelReader.ReadGrid(path, "Данные");
                Assert.Equal(3, grid.RowCount);
                Assert.Equal(3, grid.ColumnCount);

                Assert.Equal("Поз.", grid[0, 0]);
                Assert.Equal("Итого", grid[1, 1]);
                Assert.Equal("Примечание", grid[2, 0]);
                Assert.Equal("01.02.2024", grid[2, 2]);

                Assert.Single(grid.Merges);
                Assert.Equal(0, grid.Merges[0].Top);
                Assert.Equal(0, grid.Merges[0].Left);
                Assert.Equal(0, grid.Merges[0].Bottom);
                Assert.Equal(2, grid.Merges[0].Right);
            }
            finally { TryDelete(path); }
        }

        [Theory]
        [InlineData(0, "A")]
        [InlineData(2, "C")]
        [InlineData(25, "Z")]
        [InlineData(26, "AA")]
        [InlineData(27, "AB")]
        public void ColumnName_IsExcelStyle(int index, string expected)
        {
            Assert.Equal(expected, ExcelReader.ColumnName(index));
        }

        [Fact]
        public void AddressHelpers_Work()
        {
            Assert.True(ExcelReader.TryParseReference("B3", out var r, out var c));
            Assert.Equal(2, r);
            Assert.Equal(1, c);

            Assert.True(ExcelReader.TryParseRange("A1:C6", out var r1, out var c1, out var r2, out var c2));
            Assert.Equal(0, r1);
            Assert.Equal(5, r2);
            Assert.Equal(2, c2);

            Assert.Equal("A1:C6", ExcelReader.FormatRange(0, 0, 5, 2));
            Assert.Equal("B2", ExcelReader.FormatRange(1, 1, 1, 1));

            ExcelReader.SelectionSize(0, 0, 5, 2, out var rows, out var cols);
            Assert.Equal(6, rows);
            Assert.Equal(3, cols);
        }

        private static void BuildRichBook(string path)
        {
            using (var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
            {
                var wbPart = doc.AddWorkbookPart();
                wbPart.Workbook = new Spreadsheet.Workbook();

                var sstPart = wbPart.AddNewPart<SharedStringTablePart>();
                sstPart.SharedStringTable = new Spreadsheet.SharedStringTable();
                foreach (var s in new[] { "Поз.", "Обозначение", "Итого", "Примечание" })
                    sstPart.SharedStringTable.AppendChild(new Spreadsheet.SharedStringItem(new Spreadsheet.Text(s)));

                var stylesPart = wbPart.AddNewPart<WorkbookStylesPart>();
                stylesPart.Stylesheet = new Spreadsheet.Stylesheet(
                    new Spreadsheet.CellFormats(
                        new Spreadsheet.CellFormat { NumberFormatId = 14 }));
                stylesPart.Stylesheet.Save();

                var wsPart = wbPart.AddNewPart<WorksheetPart>();
                var ws = new Spreadsheet.Worksheet();
                var data = new Spreadsheet.SheetData();
                ws.AppendChild(data);

                // Строка 1: "Поз." (shared 0) + объединение A1:C1
                var row1 = new Spreadsheet.Row { RowIndex = 1 };
                row1.AppendChild(SharedCell("A1", 0));
                data.AppendChild(row1);

                // Строка 2: пусто(A) + "Итого" (shared 2) + число 5
                var row2 = new Spreadsheet.Row { RowIndex = 2 };
                row2.AppendChild(SharedCell("B2", 2));
                row2.AppendChild(NumberCell("C2", 5, null));
                data.AppendChild(row2);

                // Строка 3: "Примечание" (shared 3) + дата 45323 (01.02.2024), стиль 0 = дата
                var row3 = new Spreadsheet.Row { RowIndex = 3 };
                row3.AppendChild(SharedCell("A3", 3));
                row3.AppendChild(NumberCell("C3", 45323, 0));
                data.AppendChild(row3);

                ws.AppendChild(new Spreadsheet.MergeCells(
                    new Spreadsheet.MergeCell { Reference = "A1:C1" }));
                wsPart.Worksheet = ws;
                wsPart.Worksheet.Save();

                var sheets = wbPart.Workbook.AppendChild(new Spreadsheet.Sheets());
                sheets.AppendChild(new Spreadsheet.Sheet
                {
                    Id = wbPart.GetIdOfPart(wsPart),
                    SheetId = 1,
                    Name = "Данные"
                });
                wbPart.Workbook.Save();
            }
        }

        private static Spreadsheet.Cell SharedCell(string reference, int sharedIndex)
        {
            return new Spreadsheet.Cell
            {
                CellReference = reference,
                DataType = Spreadsheet.CellValues.SharedString,
                CellValue = new Spreadsheet.CellValue(sharedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture))
            };
        }

        private static Spreadsheet.Cell NumberCell(string reference, double value, uint? style)
        {
            var cell = new Spreadsheet.Cell
            {
                CellReference = reference,
                CellValue = new Spreadsheet.CellValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            };
            if (style.HasValue) cell.StyleIndex = style.Value;
            return cell;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
