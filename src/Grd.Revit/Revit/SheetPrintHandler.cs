using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace GrdRevit.Revit
{
    /// <summary>Лист проекта (для окна «Печать в PDF» и «Экспорт в DWG»).</summary>
    public sealed class SheetInfo : IDwgExportItem
    {
        public long SheetId;
        public string SheetNumber = string.Empty;
        public string SheetName = string.Empty;

        public long IdValue => SheetId;
        public string FavScope => "sheets";

        /// <summary>Галочка в списке листов: отмеченные листы печатаются в один PDF.</summary>
        public bool IsChecked { get; set; }

        /// <summary>Избранный лист (★): общий для окон «Печать в PDF» и «Экспорт в DWG»,
        /// хранится по документу в FavoriteStore (scope "sheets").</summary>
        public bool IsFavorite { get; set; }

        public string Display =>
            "Лист " + (string.IsNullOrWhiteSpace(SheetNumber) ? "—" : SheetNumber) +
            (string.IsNullOrWhiteSpace(SheetName) ? string.Empty : " — " + SheetName);

        public string SearchKey => (SheetNumber + " " + SheetName).ToLowerInvariant();
    }

    /// <summary>Ход печати листов: сколько обработано, что делается сейчас, чем закончилось.</summary>
    public sealed class SheetPrintProgress
    {
        public int Done;
        public int Total;
        public string Phase = string.Empty;
        public string Current = string.Empty;
        public bool Finished;
        public bool Cancelled;
        public string Error = string.Empty;

        public double Percent => Total > 0 ? 100.0 * Done / Total : 0.0;
    }

    /// <summary>Список листов документа для окна «Печать в PDF».</summary>
    public sealed class SheetListResult
    {
        public string Error = string.Empty;
        public string DocKey = string.Empty;
        public List<SheetInfo> Sheets = new List<SheetInfo>();
    }

    /// <summary>
    /// Печать выбранных листов в один PDF с пошаговым ходом. Моделесс-окно не может
    /// обращаться к API напрямую, поэтому запросы выполняются Revit-потоком через
    /// ExternalEvent. За один вызов Execute обрабатывается РОВНО один лист: между
    /// вызовами управление возвращается в цикл сообщений Revit, и окно успевает
    /// перерисовать шкалу прогресса. Фоновых потоков нет — только API-поток.
    /// </summary>
    public class SheetPrintHandler : IExternalEventHandler
    {
        private readonly object _sync = new object();
        private readonly Queue<Request> _requests = new Queue<Request>();
        private bool _cancelRequested;
        private PrintJob _job;

        private enum Kind { ReadSheets, PrintSheets }

        private sealed class Request
        {
            public Kind Kind;
            public List<long> SheetIds;
            public string TargetPath;
            public bool Raster;
            public bool BlackWhite;
            public Action<SheetListResult> ListCallback;
            public Action<SheetPrintProgress> ProgressCallback;
        }

        private sealed class PrintJob
        {
            public Document Doc;
            public List<ViewSheet> Sheets;
            public string TargetPath;
            public bool Raster;
            public bool BlackWhite;
            public int Index;
            public bool Assembling;
            public string TmpDir;
            public PdfDocument Pdf;
            public readonly List<string> TempPdfs = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public bool CancelRequested;
            public Action<SheetPrintProgress> OnProgress;
        }

        /// <summary>Список всех листов документа (кроме листов-заменителей).</summary>
        public void QueueReadSheets(Action<SheetListResult> callback)
        {
            lock (_sync)
                _requests.Enqueue(new Request { Kind = Kind.ReadSheets, ListCallback = callback });
        }

        /// <summary>Печать выбранных листов в один PDF. Raster=false — векторный экспорт
        /// Revit; Raster=true — изображения листов и сборка PDF средствами PDFsharp
        /// (гарантирует корректный знак «Ø», который ломает векторное ядро Revit).</summary>
        public void QueuePrintSheets(List<long> sheetIds, string targetPath, bool raster, bool blackWhite,
                                     Action<SheetPrintProgress> callback)
        {
            lock (_sync)
                _requests.Enqueue(new Request
                {
                    Kind = Kind.PrintSheets,
                    SheetIds = sheetIds,
                    TargetPath = targetPath,
                    Raster = raster,
                    BlackWhite = blackWhite,
                    ProgressCallback = callback
                });
        }

        /// <summary>Запрос отмены текущей печати: обработчик завершит шаг, удалит временные
        /// файлы и сообщит окну об отмене.</summary>
        public void Cancel()
        {
            lock (_sync) _cancelRequested = true;
        }

        public string GetName()
        {
            return "JTOOLS: печать листов в PDF";
        }

        public void Execute(UIApplication app)
        {
            try
            {
                lock (_sync)
                {
                    if (_job != null && _cancelRequested) _job.CancelRequested = true;
                }

                if (_job != null)
                {
                    Step();
                    return;
                }

                Request req = null;
                lock (_sync)
                {
                    if (_requests.Count > 0) req = _requests.Dequeue();
                }
                if (req == null) return;

                if (req.Kind == Kind.ReadSheets)
                {
                    SheetListResult list;
                    try { list = ReadSheets(app); }
                    catch (Exception ex)
                    {
                        GrdLog.Log("SheetPrintHandler.ReadSheets EXCEPTION: " + ex);
                        list = new SheetListResult { Error = "Ошибка чтения списка листов: " + ex.Message };
                    }
                    try { req.ListCallback?.Invoke(list); }
                    catch (Exception ex) { GrdLog.Log("SheetPrintHandler.ReadSheets callback: " + ex); }
                    return;
                }

                StartJob(app, req);
            }
            catch (Exception ex)
            {
                GrdLog.Log("SheetPrintHandler.Execute EXCEPTION: " + ex);
                Finish("Ошибка: " + ex.Message, false);
            }
        }

        // ------------------------------------------------------------------ ЗАДАНИЕ

        private void StartJob(UIApplication app, Request req)
        {
            var cb = req.ProgressCallback;
            try
            {
                if (string.IsNullOrEmpty(req.TargetPath)) { cb?.Invoke(Done("Не задан путь для PDF.")); return; }
                if (req.SheetIds == null || req.SheetIds.Count == 0) { cb?.Invoke(Done("Листы не выбраны.")); return; }

                var doc = app?.ActiveUIDocument?.Document;
                if (doc == null) { cb?.Invoke(Done("Нет активного документа Revit.")); return; }

                var sheets = new List<ViewSheet>();
                foreach (var id in req.SheetIds)
                {
                    var s = doc.GetElement(new ElementId(id)) as ViewSheet;
                    if (s != null && !s.IsPlaceholder) sheets.Add(s);
                }
                if (sheets.Count == 0) { cb?.Invoke(Done("Листы не найдены в документе.")); return; }

                var job = new PrintJob
                {
                    Doc = doc,
                    Sheets = sheets,
                    TargetPath = req.TargetPath,
                    Raster = req.Raster,
                    BlackWhite = req.BlackWhite,
                    OnProgress = cb,
                    TmpDir = Path.Combine(Path.GetTempPath(), "GrdRevitPdf_" + Guid.NewGuid().ToString("N"))
                };
                Directory.CreateDirectory(job.TmpDir);
                if (job.Raster) job.Pdf = new PdfDocument();

                lock (_sync)
                {
                    _cancelRequested = false;
                    _job = job;
                }
                GrdLog.Log("SheetPrintHandler: старт печати, листов=" + sheets.Count +
                           (job.Raster ? " (растр)" : " (вектор)") + " -> " + req.TargetPath);
                Step();
            }
            catch (Exception ex)
            {
                GrdLog.Log("SheetPrintHandler.StartJob EXCEPTION: " + ex);
                cb?.Invoke(Done("Ошибка: " + ex.Message));
                Cleanup();
            }
        }

        private void Step()
        {
            var job = _job;
            if (job == null) return;
            try
            {
                if (job.CancelRequested)
                {
                    Finish("Печать отменена.", true);
                    return;
                }

                if (job.Index < job.Sheets.Count)
                {
                    var sheet = job.Sheets[job.Index];
                    string warning;
                    ProcessSheet(job, sheet, out warning);
                    if (!string.IsNullOrEmpty(warning)) job.Warnings.Add(warning);
                    job.Index++;

                    job.OnProgress?.Invoke(new SheetPrintProgress
                    {
                        Done = job.Index,
                        Total = job.Sheets.Count,
                        Phase = job.Raster ? "Экспорт листов в изображения" : "Экспорт листов в PDF",
                        Current = "Лист " + sheet.SheetNumber
                    });
                    return;
                }

                // Все листы обработаны: сообщаем окну о сборке и выходим, чтобы сообщение
                // успело отрисоваться; сборка выполнится на следующем шаге.
                if (!job.Assembling)
                {
                    job.Assembling = true;
                    job.OnProgress?.Invoke(new SheetPrintProgress
                    {
                        Done = job.Sheets.Count,
                        Total = job.Sheets.Count,
                        Phase = "Сборка PDF",
                        Current = "Запись файла…"
                    });
                    return;
                }

                string err = FinalizeJob(job);
                Finish(err, false);
            }
            catch (Exception ex)
            {
                GrdLog.Log("SheetPrintHandler.Step EXCEPTION: " + ex);
                Finish("Ошибка печати: " + ex.Message, false);
            }
        }

        /// <summary>Обработка одного листа: экспорт в изображение или в отдельный PDF.</summary>
        private static void ProcessSheet(PrintJob job, ViewSheet sheet, out string warning)
        {
            warning = null;
            if (job.Raster)
            {
                var baseName = "sheet-" + sheet.Id.Value + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                var basePath = Path.Combine(job.TmpDir, baseName);
                var before = new HashSet<string>(
                    Directory.GetFiles(job.TmpDir, "*.png").Select(Path.GetFullPath),
                    StringComparer.OrdinalIgnoreCase);

                var options = new ImageExportOptions
                {
                    ExportRange = ExportRange.SetOfViews,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_300,
                    ZoomType = ZoomFitType.FitToPage,
                    FitDirection = FitDirectionType.Horizontal,
                    FilePath = basePath
                };
                options.SetViewsAndSheets(new List<ElementId> { sheet.Id });
                job.Doc.ExportImage(options);

                var png = Directory.GetFiles(job.TmpDir, "*.png").Select(Path.GetFullPath)
                    .FirstOrDefault(f => !before.Contains(f));
                if (png == null)
                    throw new InvalidOperationException("Revit не создал изображение листа «" + sheet.SheetNumber + "».");

                double wMm, hMm;
                SheetSizeMm(sheet, out wMm, out hMm);

                var page = job.Pdf.AddPage();
                page.Width = XUnit.FromMillimeter(wMm);
                page.Height = XUnit.FromMillimeter(hMm);
                using (var gfx = XGraphics.FromPdfPage(page))
                using (var img = XImage.FromFile(png))
                {
                    gfx.DrawImage(img, 0, 0, page.Width.Point, page.Height.Point);
                }
                try { File.Delete(png); } catch { }

                GrdLog.Log("SheetPrintHandler: растр «" + sheet.SheetNumber + "» " +
                           wMm.ToString("0.#") + "x" + hMm.ToString("0.#") + " мм");
            }
            else
            {
                var name = "seg" + job.Index.ToString("000") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                var before = new HashSet<string>(
                    Directory.GetFiles(job.TmpDir, "*.pdf").Select(Path.GetFullPath),
                    StringComparer.OrdinalIgnoreCase);

                var options = new PDFExportOptions
                {
                    FileName = name,
                    Combine = true,
                    ZoomType = ZoomType.FitToPage,
                    ColorDepth = job.BlackWhite ? ColorDepthType.BlackLine : ColorDepthType.Color,
                    ExportQuality = PDFExportQualityType.DPI300,
                    HideCropBoundaries = true,
                    HideScopeBoxes = true,
                    HideReferencePlane = true,
                    PaperOrientation = PageOrientationType.Auto
                };
                job.Doc.Export(job.TmpDir, new List<ElementId> { sheet.Id }, options);

                var expected = Path.Combine(job.TmpDir, name + ".pdf");
                var pdf = File.Exists(expected)
                    ? expected
                    : Directory.GetFiles(job.TmpDir, "*.pdf").Select(Path.GetFullPath)
                        .FirstOrDefault(f => !before.Contains(f));
                if (pdf == null)
                    throw new InvalidOperationException("Revit не создал PDF для листа «" + sheet.SheetNumber + "».");

                job.TempPdfs.Add(Path.GetFullPath(pdf));
                GrdLog.Log("SheetPrintHandler: вектор «" + sheet.SheetNumber + "» -> " + Path.GetFileName(pdf));
            }
        }

        private static void SheetSizeMm(ViewSheet sheet, out double wMm, out double hMm)
        {
            try
            {
                var o = sheet.Outline;
                wMm = (o.Max.U - o.Min.U) * 304.8;
                hMm = (o.Max.V - o.Min.V) * 304.8;
            }
            catch { wMm = 210; hMm = 297; }
            if (wMm <= 0) wMm = 210;
            if (hMm <= 0) hMm = 297;
        }

        // ------------------------------------------------------------------ СБОРКА

        private static string FinalizeJob(PrintJob job)
        {
            var targetFull = Path.GetFullPath(job.TargetPath);
            if (job.Raster)
            {
                try { if (File.Exists(targetFull)) File.Delete(targetFull); } catch { }
                job.Pdf.Save(targetFull);
                return null;
            }

            // Вектор: Revit отдаёт по одному PDF на лист — объединяем их в PDFsharp.
            try
            {
                try { if (File.Exists(targetFull)) File.Delete(targetFull); } catch { }
                using (var outDoc = new PdfDocument())
                {
                    foreach (var f in job.TempPdfs)
                    {
                        using (var src = PdfReader.Open(f, PdfDocumentOpenMode.Import, null))
                        {
                            for (int i = 0; i < src.PageCount; i++)
                                outDoc.AddPage(src.Pages[i]);
                        }
                    }
                    outDoc.Save(targetFull);
                }
                return null;
            }
            catch (Exception ex)
            {
                GrdLog.Log("SheetPrintHandler.FinalizeJob: объединение не удалось, резервный общий экспорт: " + ex);
                try { if (File.Exists(targetFull)) File.Delete(targetFull); } catch { }
                return ExportCombined(job.Doc, job.Sheets, targetFull, job.BlackWhite);
            }
        }

        /// <summary>Резервный путь: один общий экспорт всех листов средствами Revit.</summary>
        private static string ExportCombined(Document doc, List<ViewSheet> sheets, string targetPath, bool blackWhite)
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
            Directory.CreateDirectory(dir);
            var baseName = Path.GetFileNameWithoutExtension(targetPath);
            var targetFull = Path.GetFullPath(targetPath);
            try { if (File.Exists(targetFull)) File.Delete(targetFull); } catch { }
            var before = new HashSet<string>(
                Directory.GetFiles(dir, "*.pdf").Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

            var options = new PDFExportOptions
            {
                FileName = baseName,
                Combine = true,
                ZoomType = ZoomType.FitToPage,
                ColorDepth = blackWhite ? ColorDepthType.BlackLine : ColorDepthType.Color,
                ExportQuality = PDFExportQualityType.DPI300,
                HideCropBoundaries = true,
                HideScopeBoxes = true,
                HideReferencePlane = true,
                PaperOrientation = PageOrientationType.Auto
            };
            doc.Export(dir, sheets.Select(s => s.Id).ToList(), options);

            var expected = Path.Combine(dir, baseName + ".pdf");
            string produced = File.Exists(expected)
                ? expected
                : Directory.GetFiles(dir, "*.pdf").FirstOrDefault(f => !before.Contains(Path.GetFullPath(f)));
            if (produced == null) return "Revit не создал PDF для выбранных листов.";

            var producedFull = Path.GetFullPath(produced);
            if (!string.Equals(producedFull, targetFull, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(targetFull)) File.Delete(targetFull);
                File.Move(producedFull, targetFull);
            }
            return null;
        }

        // ------------------------------------------------------------------ ЗАВЕРШЕНИЕ

        private void Finish(string error, bool cancelled)
        {
            var job = _job;
            Cleanup();
            if (job == null) return;
            try
            {
                job.OnProgress?.Invoke(new SheetPrintProgress
                {
                    Done = job.Sheets.Count,
                    Total = job.Sheets.Count,
                    Finished = true,
                    Cancelled = cancelled,
                    Error = error ?? string.Empty,
                    Phase = cancelled ? "Отменено" : (string.IsNullOrEmpty(error) ? "Готово" : "Ошибка")
                });
            }
            catch (Exception ex) { GrdLog.Log("SheetPrintHandler.Finish callback: " + ex); }
        }

        private void Cleanup()
        {
            PrintJob job;
            lock (_sync)
            {
                job = _job;
                _job = null;
            }
            if (job == null) return;
            try { job.Pdf?.Dispose(); } catch { }
            try
            {
                if (Directory.Exists(job.TmpDir)) Directory.Delete(job.TmpDir, true);
            }
            catch (Exception ex) { GrdLog.Log("SheetPrintHandler.Cleanup temp EXCEPTION: " + ex.Message); }
        }

        private static SheetPrintProgress Done(string error)
        {
            return new SheetPrintProgress
            {
                Finished = true,
                Error = error ?? string.Empty,
                Phase = string.IsNullOrEmpty(error) ? "Готово" : "Ошибка"
            };
        }

        // ------------------------------------------------------------------ СПИСОК

        private static SheetListResult ReadSheets(UIApplication app)
        {
            var res = new SheetListResult();
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null) { res.Error = "Нет активного документа Revit."; return res; }
            if (doc.IsFamilyDocument)
            {
                res.Error = "Печать листов работает только в проектной модели (RVT).";
                return res;
            }
            res.DocKey = GetDocKey(doc);

            var items = new List<SheetInfo>();
            foreach (ViewSheet sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))
            {
                if (sheet == null || sheet.IsPlaceholder) continue;
                items.Add(new SheetInfo
                {
                    SheetId = sheet.Id.Value,
                    SheetNumber = sheet.SheetNumber ?? string.Empty,
                    SheetName = sheet.Name ?? string.Empty
                });
            }
            items.Sort((a, b) => CompareSheetNumbers(a.SheetNumber, b.SheetNumber));
            res.Sheets = items;
            GrdLog.Log("SheetPrintHandler.ReadSheets: листов=" + items.Count);
            return res;
        }

        /// <summary>Ключ документа для хранения отметок листов: путь файла, а для
        /// несохранённого документа — его название.</summary>
        private static string GetDocKey(Document doc)
        {
            if (doc == null) return string.Empty;
            try
            {
                if (!string.IsNullOrEmpty(doc.PathName)) return doc.PathName;
                return (doc.Title ?? string.Empty) + " (несохранённый)";
            }
            catch { return string.Empty; }
        }

        private static int CompareSheetNumbers(string a, string b)
        {
            var pa = System.Text.RegularExpressions.Regex.Matches(a ?? string.Empty, @"\d+");
            var pb = System.Text.RegularExpressions.Regex.Matches(b ?? string.Empty, @"\d+");
            if (pa.Count > 0 && pb.Count > 0)
            {
                long ia, ib;
                if (long.TryParse(pa[0].Value, out ia) && long.TryParse(pb[0].Value, out ib) && ia != ib)
                    return ia.CompareTo(ib);
            }
            return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
