using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using GrdRevit.Core;
using Xunit;

namespace GrdRevit.Core.Tests
{
    /// <summary>Разбор CSV-выгрузки спецификации (как её пишет сам Revit) и упаковка в ODS.</summary>
    public class ScheduleExportCsvTests
    {
        private static string Tmp(string name)
        {
            var dir = Path.Combine(Path.GetTempPath(), "grdtests");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name + "_" + System.Guid.NewGuid().ToString("N") + ".csv");
        }

        /// <summary>CSV в том виде, как его выгружает Revit: первая колонка (позиция) пустая у обычных
        /// строк, строки-группы с итогами, пустые колонки, значение «&lt;различные значения&gt;»,
        /// кавычки, многострочная ячейка с разделителем внутри и удвоенными кавычками.</summary>
        private const string RevitLikeCsv =
            ";\u0412\u043e\u0437\u0434\u0443\u0445\u043e\u043e\u0442\u0432\u043e\u0434\u0447\u0438\u043a;;;;;25;;\r\n" +
            ";\u041a\u043b\u0430\u043f\u0430\u043d \u0431\u0430\u043b\u0430\u043d\u0441\u0438\u0440\u043e\u0432\u043e\u0447\u043d\u044b\u0439 DN25;\u0410\u041f\u0422-R3 DN25;003Z5703R3;\u041e\u041e\u041e \u00ab\u0420\u0438\u0434\u0430\u043d\u00bb;\u0448\u0442.;2;2\r\n" +
            ";;;\u041a\u043e\u0434;1;2;3\r\n" +
            "<\u0440\u0430\u0437\u043b\u0438\u0447\u043d\u044b\u0435 \u0437\u043d\u0430\u0447\u0435\u043d\u0438\u044f>;\u0412\u043e\u0437\u0434\u0443\u0445\u043e\u043e\u0442\u0432\u043e\u0434\u0447\u0438\u043a;;;;;8;;\r\n" +
            "5;\u0432\u0441\u0435\u0433\u043e \"\u0432 \u043a\u0430\u0432\u044b\u0447\u043a\u0430\u0445\";\u043d\u0430\u0434\u043e;\u0432\u0442\u043e\u0440\u043e\u0439;\u0442\u0440\u0435\u0442\u0438\u0439\r\n" +
            "\";\u0432\u043d\u0443\u0442\u0440\u0438 \u043a\u0430\u0432\u044b\u0447\u0435\u043a ;\u0438 \u043d\u043e\u0432\u0430\u044f\n\u0441\u0442\u0440\u043e\u043a\u0430\";\u043f\u043e\u0441\u043b\u0435";

        [Fact]
        public void CsvReader_ParsesRevitScheduleStructure()
        {
            var rows = CsvReader.ParseText(RevitLikeCsv, ';');
            Assert.Equal(6, rows.Count);

            Assert.Equal(9, rows[0].Length);
            Assert.Equal("", rows[0][0]);
            Assert.Equal("Воздухоотводчик", rows[0][1]);
            Assert.Equal("25", rows[0][6]);

            Assert.Equal(8, rows[1].Length);
            Assert.Equal("Клапан балансировочный DN25", rows[1][1]);
            Assert.Equal("АПТ-R3 DN25", rows[1][2]);
            Assert.Equal("003Z5703R3", rows[1][3]);
            Assert.Equal("ООО «Ридан»", rows[1][4]);
            Assert.Equal("шт.", rows[1][5]);
            Assert.Equal("2", rows[1][6]);
            Assert.Equal("2", rows[1][7]);

            var varies = rows[3];
            Assert.Equal(9, varies.Length);
            Assert.Equal("<различные значения>", varies[0]);
            Assert.Equal("8", varies[6]);

            var quotes = rows[4];
            Assert.Equal("5", quotes[0]);
            Assert.Equal("всего \"в кавычках\"", quotes[1]);
            Assert.Equal("надо", quotes[2]);

            var multiline = rows[5];
            Assert.Equal(";внутри кавычек ;и новая\nстрока", multiline[0]);
            Assert.Equal("после", multiline[1]);
        }

        [Fact]
        public void CsvReader_DecodeTextFallsBackToWin1251()
        {
            // «шт.» в Windows-1251: 0xF8 0xF2 0x2E ; «Клапан»: 0xCA 0xEB 0xE0 0xEF 0xE0 0xED
            var bytes = new byte[] { 0xF8, 0xF2, 0x2E };
            Assert.Equal("шт.", CsvReader.DecodeText(bytes));
            var klapan = new byte[] { 0xCA, 0xEB, 0xE0, 0xEF, 0xE0, 0xED };
            Assert.Equal("Клапан", CsvReader.DecodeText(klapan));
        }

        [Fact]
        public void CsvToOds_RoundTripsAllCells()
        {
            var csvPath = Tmp("schedule");
            File.WriteAllText(csvPath, RevitLikeCsv, new UTF8Encoding(false));
            var rows = CsvReader.ParseFile(csvPath, ';');

            var odsPath = csvPath.Replace(".csv", ".ods");
            OdsExporter.Write(odsPath, "О_ОВ_Арматура", rows);

            var back = ReadOdsRows(odsPath);

            var maxCols = 0;
            foreach (var r in rows) maxCols = Math.Max(maxCols, r.Length);
            Assert.Equal(rows.Count, back.Count);

            // ODS-файл выравнивает все строки по самой широкой: каждое значение
            // исходного CSV должно быть на месте в своём столбце (только дополняется пустыми).
            for (int r = 0; r < rows.Count; r++)
            {
                Assert.Equal(maxCols, back[r].Length);
                for (int c = 0; c < rows[r].Length; c++)
                    Assert.Equal(rows[r][c], back[r][c]);
            }
        }

        /// <summary>Вернёт содержимое ячеек ODS (content.xml) как матрицу строк.</summary>
        private static List<string[]> ReadOdsRows(string odsPath)
        {
            var result = new List<string[]>();
            using (var zip = ZipFile.OpenRead(odsPath))
            {
                using (var sr = new StreamReader(zip.GetEntry("content.xml").Open(), Encoding.UTF8))
                {
                    var xml = sr.ReadToEnd();
                    var rowMatches = Regex.Matches(xml, "<table:table-row>(.*?)</table:table-row>", RegexOptions.Singleline);
                    foreach (Match row in rowMatches)
                    {
                        var cells = new List<string>();
                        var cellMatches = Regex.Matches(row.Groups[1].Value,
                            "<table:table-cell(?<attrs2>[^>]*)/>|<table:table-cell(?<attrs>[^>]*)>(?<inner>.*?)</table:table-cell>",
                            RegexOptions.Singleline);
                        foreach (Match cm in cellMatches)
                        {
                            var a2 = cm.Groups["attrs2"].Value;
                            if (cm.Groups["attrs2"].Success && a2 != null && a2.Length == 0) { cells.Add(""); continue; }
                            if (cm.Groups["attrs2"].Success)
                            {
                                var m = Regex.Match(a2, "office:value=\"([^\"]*)\"");
                                cells.Add(m.Success ? m.Groups[1].Value : "");
                                continue;
                            }
                            var inner = cm.Groups["inner"].Value;
                            var v = Regex.Match(cm.Groups["attrs"].Value, "office:value=\"([^\"]*)\"");
                            var text = Regex.Matches(inner, "<text:p>(.*?)</text:p>", RegexOptions.Singleline);
                            var joined = new StringBuilder();
                            foreach (Match t in text) joined.Append(t.Groups[1].Value).Append('\n');
                            var raw = joined.Length > 0 && !v.Success ? joined.ToString(0, joined.Length - 1) :
                                      v.Success ? v.Groups[1].Value : joined.ToString();
                            cells.Add(System.Net.WebUtility.HtmlDecode(raw));
                        }
                        result.Add(cells.ToArray());
                    }
                }
            }
            return result;
        }
    }
}