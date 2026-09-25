using System;
using System.Collections.Generic;
using System.IO;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace GrdRevit.Ui
{
    /// <summary>Печать сетки спецификации в PDF (PDFsharp): размер страницы берётся из габарита
    /// Revit-листа (A4–A1 и любые другие штамповки), шапка повторяется на каждой странице,
    /// колонтитул со страницей, текст переносится.</summary>
    internal static class SchedulePdfRenderer
    {
        private const double Margin = 28;
        private const double PadX = 3;
        private const double PadTop = 2;
        private const double PadBottom = 2;
        private const double HeaderFont = 7.5;
        private const double CellFont = 7.5;
        private const double TitleFont = 13;
        private const double FooterFont = 7.5;
        private const double MaxCellChars = 70;

        /// <summary>Запасной размер страницы (A4 альбомная), если габарит листа не определён.</summary>
        private const double PageFallbackW = 297;
        private const double PageFallbackH = 210;

        /// <summary>Перевод миллиметров в пункты PDF: 72 точки на дюйм, 25.4 мм на дюйм.</summary>
        private static double MmToPt(double mm) => mm * 72.0 / 25.4;

        public static void Write(string filePath, string scheduleName, IReadOnlyList<string> headers,
                                 IReadOnlyList<string[]> rows, double sheetWidthMm, double sheetHeightMm)
        {
            GdiFontResolver.Install();
            using (var doc = new PdfDocument())
            {
                var page = doc.AddPage();
                page.Width = MmToPt(sheetWidthMm > 0 ? sheetWidthMm : PageFallbackW);
                page.Height = MmToPt(sheetHeightMm > 0 ? sheetHeightMm : PageFallbackH);

                double pageW = page.Width;
                double pageH = page.Height;
                double availW = pageW - 2 * Margin;
                double availH = pageH - 2 * Margin;

                // Ширины колонок: по содержимому, масштаб до ширины страницы.
                var widths = ComputeWidths(headers, rows, availW);
                var colX = new double[widths.Count];
                double x = Margin;
                for (int i = 0; i < widths.Count; i++)
                {
                    colX[i] = x;
                    x += widths[i];
                }

                // Высоты строк (учитывая перенос текста по ширине колонки).
                var cellLines = new List<List<string>[]>(rows.Count);
                var rowHeights = new double[rows.Count];
                var lineFont = new XFont("Arial", CellFont, XFontStyleEx.Regular);
                double lineH = LineHeight(lineFont);
                for (int r = 0; r < rows.Count; r++)
                {
                    var lines = new List<string>[headers.Count];
                    int maxLines = 1;
                    for (int c = 0; c < headers.Count; c++)
                    {
                        var wrapped = Wrap(rows[r][c] ?? string.Empty, widths[c] - 2 * PadX, lineFont);
                        lines[c] = wrapped;
                        if (wrapped.Count > maxLines) maxLines = wrapped.Count;
                    }
                    cellLines.Add(lines);
                    rowHeights[r] = maxLines * lineH + PadTop + PadBottom;
                }

                // Разбиение на страницы (шапка повторяется на каждой; титул — на первой).
                double headerRowH = LineHeight(new XFont("Arial", HeaderFont, XFontStyleEx.Bold)) + PadTop + PadBottom;
                double titleH = 22;
                var pages = LayoutPages(rows.Count, rowHeights, availH, headerRowH, titleH);

                // Первый проход страниц добавлен в PDF выше; рисуем содержимое.
                DrawAll(doc, pages, headers, rows, cellLines, rowHeights, widths, colX, lineH,
                        scheduleName, pageW, pageH, availW, availH, headerRowH);
                doc.Save(filePath);
            }
        }

        /// <summary>Печать таблицы поверх изображения листа (рамка/штамп, форма ГОСТ): изображение
        /// растягивается на страницу, исходная спецификация перекрывается белым прямоугольником,
        /// в него рисуется текущая (отредактированная) таблица. Размер страницы берётся из габарита
        /// листа, положение и размер таблицы — из прямоугольника размещённой спецификации (мм).</summary>
        public static void WriteOverFrame(string filePath, string scheduleName,
                                          IReadOnlyList<string> headers, IReadOnlyList<string[]> rows,
                                          double pageWidthMm, double pageHeightMm,
                                          string backgroundImagePath,
                                          double rectXmm, double rectYmm, double rectWmm, double rectHmm)
        {
            GdiFontResolver.Install();
            using (var doc = new PdfDocument())
            {
                var page = doc.AddPage();
                page.Width = MmToPt(pageWidthMm > 0 ? pageWidthMm : PageFallbackW);
                page.Height = MmToPt(pageHeightMm > 0 ? pageHeightMm : PageFallbackH);
                double pageW = page.Width;
                double pageH = page.Height;

                double x0 = MmToPt(Math.Max(0, rectXmm));
                double y0 = MmToPt(Math.Max(0, rectYmm));
                double tableW = MmToPt(rectWmm > 0 ? rectWmm : (pageWidthMm > 0 ? pageWidthMm - 2 * Margin : PageFallbackW - 2 * Margin));

                using (var gfx = XGraphics.FromPdfPage(page))
                {
                    if (!string.IsNullOrEmpty(backgroundImagePath) && File.Exists(backgroundImagePath))
                    {
                        using (var img = XImage.FromFile(backgroundImagePath))
                            gfx.DrawImage(img, 0, 0, pageW, pageH);
                    }

                    // Перекрыть исходную спецификацию на листе.
                    double coverW = MmToPt(rectWmm > 0 ? rectWmm : 0) + 2;
                    double coverH = MmToPt(rectHmm > 0 ? rectHmm : 0) + 2;
                    if (coverW > 2 && coverH > 2)
                        gfx.DrawRectangle(XBrushes.White, new XRect(x0 - 1, y0 - 1, coverW, coverH));

                    DrawTable(gfx, headers, rows, scheduleName, pageW, pageH, x0, y0, tableW);
                }
                doc.Save(filePath);
            }
        }

        private static void DrawTable(XGraphics gfx, IReadOnlyList<string> headers, IReadOnlyList<string[]> rows,
                                      string scheduleName, double pageW, double pageH,
                                      double x0, double y0, double tableW)
        {
            if (headers.Count == 0) return;

            var widths = ComputeWidths(headers, rows, tableW);
            var colX = new double[widths.Count];
            double x = x0;
            for (int i = 0; i < widths.Count; i++)
            {
                colX[i] = x;
                x += widths[i];
            }

            var cellFont = new XFont("Arial", CellFont, XFontStyleEx.Regular);
            var headerFont = new XFont("Arial", HeaderFont, XFontStyleEx.Bold);
            double lineH = LineHeight(cellFont);
            var textBrush = new XSolidBrush(XColor.FromArgb(255, 40, 48, 64));
            var headerBrush = new XSolidBrush(XColor.FromArgb(255, 235, 243, 240));
            var gridPen = new XPen(XColor.FromArgb(255, 90, 98, 110), 0.5);
            var alignLeft = new XStringFormat { Alignment = XStringAlignment.Near };

            double headerRowH = LineHeight(headerFont) + PadTop + PadBottom;
            double y = y0;

            gfx.DrawRectangle(headerBrush, new XRect(x0, y, tableW, headerRowH));
            for (int c = 0; c < headers.Count; c++)
            {
                gfx.DrawString(headers[c], headerFont, textBrush,
                               new XRect(colX[c] + PadX, y, widths[c] - 2 * PadX, headerRowH), alignLeft);
                gfx.DrawLine(gridPen, colX[c], y, colX[c], y + headerRowH);
            }
            gfx.DrawLine(gridPen, x0 + tableW, y, x0 + tableW, y + headerRowH);
            gfx.DrawLine(gridPen, x0, y, x0 + tableW, y);
            y += headerRowH;

            for (int r = 0; r < rows.Count; r++)
            {
                int maxLines = 1;
                var wrapped = new List<string>[headers.Count];
                for (int c = 0; c < headers.Count; c++)
                {
                    wrapped[c] = Wrap(rows[r][c] ?? string.Empty, widths[c] - 2 * PadX, cellFont);
                    if (wrapped[c].Count > maxLines) maxLines = wrapped[c].Count;
                }
                double rowH = maxLines * lineH + PadTop + PadBottom;
                double rowBottom = y + rowH;

                for (int c = 0; c < headers.Count; c++)
                {
                    double ty = y + PadTop;
                    foreach (var line in wrapped[c])
                    {
                        gfx.DrawString(line, cellFont, textBrush,
                                       new XRect(colX[c] + PadX, ty, widths[c] - 2 * PadX, lineH), alignLeft);
                        ty += lineH;
                    }
                    gfx.DrawLine(gridPen, colX[c], y, colX[c], rowBottom);
                }
                gfx.DrawLine(gridPen, x0 + tableW, y, x0 + tableW, rowBottom);
                gfx.DrawLine(gridPen, x0, rowBottom, x0 + tableW, rowBottom);
                y = rowBottom;
            }
        }

        private static List<List<int>> LayoutPages(int rowCount, double[] rowH, double availH,
                                                   double headerRowH, double titleH)
        {
            var pages = new List<List<int>>();
            var cur = new List<int>();
            double used = titleH + headerRowH;
            for (int r = 0; r < rowCount; r++)
            {
                if (cur.Count > 0 && used + rowH[r] > availH)
                {
                    pages.Add(cur);
                    cur = new List<int>();
                    used = headerRowH;
                }
                cur.Add(r);
                used += rowH[r];
            }
            if (cur.Count > 0) pages.Add(cur);
            return pages;
        }

        private static void DrawAll(PdfDocument doc, List<List<int>> pages,
                                    IReadOnlyList<string> headers, IReadOnlyList<string[]> rows,
                                    List<List<string>[]> cellLines, double[] rowH, List<double> widths,
                                    double[] colX, double lineH, string scheduleName,
                                    double pageW, double pageH, double availW, double availH,
                                    double headerRowH)
        {
            var cellFont = new XFont("Arial", CellFont, XFontStyleEx.Regular);
            var headerFont = new XFont("Arial", HeaderFont, XFontStyleEx.Bold);
            var titleFont = new XFont("Arial", TitleFont, XFontStyleEx.Bold);
            var footerFont = new XFont("Arial", FooterFont, XFontStyleEx.Regular);
            var titleBrush = new XSolidBrush(XColor.FromArgb(255, 40, 48, 64));
            var headerBrush = new XSolidBrush(XColor.FromArgb(255, 235, 243, 240));
            var headerTextBrush = new XSolidBrush(XColor.FromArgb(255, 40, 70, 64));
            var gridPen = new XPen(XColor.FromArgb(180, 150, 158, 170), 0.5);
            var alignLeft = new XStringFormat { Alignment = XStringAlignment.Near };

            for (int p = 0; p < pages.Count; p++)
            {
                PdfPage page;
                if (p == 0)
                    page = doc.Pages[0];
                else
                    page = doc.AddPage();

                page.Width = pageW;
                page.Height = pageH;

                var gfx = XGraphics.FromPdfPage(page);
                double y = Margin;
                double bottom = Margin + availH;

                if (p == 0)
                {
                    gfx.DrawString(scheduleName, titleFont, titleBrush,
                                   new XRect(Margin, y, availW, 18), XStringFormats.Center);
                    y += 22;
                }

                // Шапка таблицы.
                double headerY = y;
                gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, 235, 243, 240)),
                                  new XRect(Margin, headerY, availW, headerRowH));
                for (int c = 0; c < headers.Count; c++)
                {
                    var text = headers[c];
                    var rect = new XRect(colX[c] + PadX, headerY, widths[c] - 2 * PadX, headerRowH);
                    gfx.DrawString(text, headerFont, headerTextBrush, rect, alignLeft);
                }
                y += headerRowH;

                // Строки.
                foreach (var r in pages[p])
                {
                    double rowBottom = y + rowH[r];
                    for (int c = 0; c < headers.Count; c++)
                    {
                        var lines = cellLines[r][c];
                        double ty = y + PadTop;
                        foreach (var line in lines)
                        {
                            var rect = new XRect(colX[c] + PadX, ty, widths[c] - 2 * PadX, lineH);
                            gfx.DrawString(line, cellFont, new XSolidBrush(XColor.FromArgb(255, 40, 48, 64)),
                                           rect, alignLeft);
                            ty += lineH;
                        }
                        // Левая граница ячейки (она же внутренний разделитель и левый внешний край).
                        gfx.DrawLine(gridPen, colX[c], y, colX[c], rowBottom);
                    }
                    // Горизонтальная граница строки.
                    gfx.DrawLine(gridPen, Margin, rowBottom, Margin + availW, rowBottom);
                    y = rowBottom;
                }

                // Внешние границы таблицы: верх, левая и правая сторона (правые края колонок
                // рисуются в цикле, поэтому не дублируем их).
                gfx.DrawLine(gridPen, Margin, headerY, Margin + availW, headerY);
                gfx.DrawLine(gridPen, Margin, headerY, Margin, y);
                gfx.DrawLine(gridPen, Margin + availW, headerY, Margin + availW, y);

                // Нижняя горизонтальная линия таблицы (если таблица закончилась).
                if (y < bottom)
                    gfx.DrawLine(gridPen, Margin, y, Margin + availW, y);

                // Колонтитул.
                string footerText = "Снимок спецификации «" + scheduleName + "»";
                gfx.DrawString(footerText, footerFont, new XSolidBrush(XColor.FromArgb(255, 110, 118, 132)),
                               new XRect(Margin, bottom + 6, availW - 120, FooterFont + 4), XStringFormats.CenterLeft);
                gfx.DrawString("Стр. " + (p + 1) + " из " + pages.Count, footerFont,
                               new XSolidBrush(XColor.FromArgb(255, 110, 118, 132)),
                               new XRect(Margin + availW - 120, bottom + 6, 120, FooterFont + 4), XStringFormats.CenterRight);
            }
        }

        private static List<double> ComputeWidths(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows, double availW)
        {
            var natural = new double[headers.Count];
            double total = 0;
            for (int c = 0; c < headers.Count; c++)
            {
                double len = Math.Max(headers[c]?.Length ?? 0, 4);
                double minN = len * 4.6 + 8;
                for (int r = 0; r < rows.Count && r < 400; r++)
                {
                    var cell = rows[r][c];
                    if (cell == null) continue;
                    minN = Math.Max(minN, Math.Min(cell.Length, MaxCellChars) * 4.6 + 8);
                }
                natural[c] = Math.Min(minN, 240);
                total += natural[c];
            }

            double scale = availW / total;
            var widths = new List<double>(headers.Count);
            double used = 0;
            for (int c = 0; c < headers.Count; c++)
            {
                double w = natural[c] * scale;
                if (w < 30) w = 30;
                widths.Add(w);
                used += w;
            }
            // Небольшая корректировка, чтобы сумма точно совпадала с доступной шириной.
            if (used > availW)
            {
                double k = availW / used;
                for (int c = 0; c < widths.Count; c++) widths[c] *= k;
            }
            return widths;
        }

        private static List<string> Wrap(string text, double width, XFont font)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) { result.Add(string.Empty); return result; }
            double charW = font.Size * 0.52;
            int maxChars = Math.Max(1, (int)(width / charW));
            var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var current = new System.Text.StringBuilder();
            foreach (var w in words)
            {
                if (current.Length == 0)
                {
                    if (w.Length > maxChars)
                    {
                        int i = 0;
                        while (i < w.Length)
                        {
                            var part = w.Substring(i, Math.Min(maxChars, w.Length - i));
                            result.Add(part);
                            i += part.Length;
                        }
                    }
                    else current.Append(w);
                }
                else if (current.Length + 1 + w.Length <= maxChars)
                {
                    current.Append(' ').Append(w);
                }
                else
                {
                    result.Add(current.ToString());
                    current.Clear();
                    if (w.Length > maxChars)
                    {
                        int i = 0;
                        while (i < w.Length)
                        {
                            var part = w.Substring(i, Math.Min(maxChars, w.Length - i));
                            result.Add(part);
                            i += part.Length;
                        }
                    }
                    else current.Append(w);
                }
            }
            if (current.Length > 0) result.Add(current.ToString());
            if (result.Count == 0) result.Add(string.Empty);
            return result;
        }

        private static double LineHeight(XFont font)
        {
            return Math.Max(10, font.GetHeight() + 1.5);
        }
    }
}