using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GrdRevit.Core;
using PdfSharp.Drawing;

namespace GrdRevit.Revit
{
    /// <summary>Спецификация, размещённая на листе (для списка слева в окне «Снимок спецификаций»).</summary>
    public sealed class SheetScheduleInfo
    {
        public long SheetId;
        public string SheetNumber = string.Empty;
        public string SheetName = string.Empty;
        public long ScheduleId;
        public string ScheduleName = string.Empty;

        /// <summary>Свободная спецификация: содержимое хранится только в плагине (JSON),
        /// а не читается из модели. Создаётся дублированием спецификации-образца.</summary>
        public bool IsFree;

        /// <summary>Галочка в списке листов: отмеченные листы печатаются пакетом в один PDF.</summary>
        public bool IsChecked { get; set; }

        /// <summary>Избранное: элемент отмечен звёздочкой и может быть показан фильтром
        /// «Только избранное». Хранится в FavoriteStore по ключу документа.</summary>
        public bool IsFavorite { get; set; }

        /// <summary>Подпись строки списка: «Лист {номер} — {имя спецификации}».</summary>
        public string Display =>
            "Лист " + (string.IsNullOrWhiteSpace(SheetNumber) ? "—" : SheetNumber) + " — " + ScheduleName +
            (IsFree ? "  ·  свободная" : string.Empty);

        /// <summary>Подпись-подсказка (ниже основной строки).</summary>
        public string Subtitle => string.IsNullOrWhiteSpace(SheetName) ? ScheduleName : SheetName;

        /// <summary>Строка для поиска по номеру/имени листа и имени спецификации.</summary>
        public string SearchKey =>
            (SheetNumber + " " + SheetName + " " + ScheduleName).ToLowerInvariant();
    }

    /// <summary>Таблица спецификации: заголовки колонок и строки (точная сетка Revit).</summary>
    public sealed class ScheduleTable
    {
        public long ScheduleId;
        public string ScheduleName = string.Empty;
        public string SheetNumber = string.Empty;
        public string SheetName = string.Empty;
        public double SheetWidthMm;
        public double SheetHeightMm;
        public List<string> Headers = new List<string>();
        public List<string[]> Rows = new List<string[]>();
        public string Warning = string.Empty;

        /// <summary>Свободная спецификация: строки берутся из сохранённых правок, а не из модели.</summary>
        public bool IsFree;

        /// <summary>Id спецификации-образца для свободной спецификации (0 — не задан).</summary>
        public long TemplateScheduleId;

        /// <summary>Лист, для которого прочитана таблица, и номер сегмента (для разделённой
        /// спецификации). По ним подбираются сохранённые правки.</summary>
        public long SheetId;
        public int SegmentIndex;
    }

    /// <summary>Результат запроса окна «Снимок спецификаций».</summary>
    public sealed class ScheduleSnapshotResult
    {
        public string Error = string.Empty;
        public string DocKey = string.Empty;
        public List<SheetScheduleInfo> Schedules = new List<SheetScheduleInfo>();
        public ScheduleTable Table;

        /// <summary>Путь к временному изображению листа (рамка/штамп) — только для «рамка + таблица».</summary>
        public string ImagePath = string.Empty;
        public double SheetWidthMm;
        public double SheetHeightMm;
        /// <summary>Положение размещённой спецификации на листе (мм от верхнего левого угла листа).</summary>
        public double RectXmm;
        public double RectYmm;
        public double RectWmm;
        public double RectHmm;
        public int RectRotation;

        /// <summary>Id созданной свободной спецификации и её листа (для команды создания).</summary>
        public long NewScheduleId;
        public long NewSheetId;
    }

    /// <summary>
    /// Читает список спецификаций, размещённых на листах, и содержимое любой из них.
    /// Моделесс-окно не может обращаться к API напрямую — запросы ставятся в очередь
    /// и выполняются Revit-потоком через ExternalEvent.
    /// Сетка спецификации получается штатным экспортом Revit в CSV (как «Файл → Экспорт →
    /// Спецификации»), поэтому получаются все колонки и строки ровно в том виде, как
    /// спецификация отображается в модели (заголовки, группировки, итоги, пустые ячейки).
    /// </summary>
    public class ScheduleSnapshotHandler : IExternalEventHandler
    {
        private readonly object _sync = new object();
        private readonly Queue<Request> _requests = new Queue<Request>();

        private enum Kind { ReadList, ReadTable, ExportFrame, PrintEdited, PrintMany, CreateFree, ExportExcel }

        private sealed class Request
        {
            public Kind Kind;
            public long ScheduleId;
            public long SheetId;
            public string TargetPath;
            public List<string> Headers;
            public List<string[]> Rows;
            public List<SheetScheduleInfo> Items;
            public bool Free;

            /// <summary>Создание свободной спецификации: образец заголовков и данные листа.</summary>
            public long TemplateScheduleId;
            public string SheetNumber;
            public string SheetName;

            public Action<ScheduleSnapshotResult> Callback;
        }

        public void QueueReadList(Action<ScheduleSnapshotResult> callback)
        {
            lock (_sync)
                _requests.Enqueue(new Request { Kind = Kind.ReadList, Callback = callback });
        }

        public void QueueReadTable(long sheetId, long scheduleId, Action<ScheduleSnapshotResult> callback)
        {
            lock (_sync)
                _requests.Enqueue(new Request
                {
                    Kind = Kind.ReadTable,
                    SheetId = sheetId,
                    ScheduleId = scheduleId,
                    Callback = callback
                });
        }

        /// <summary>Экспорт листа в изображение (PNG, рамка и штамп, форма ГОСТ) и определение
        /// прямоугольника размещённой спецификации, чтобы окно наложило свою отредактированную
        /// таблицу на это место. Модель не изменяется.</summary>
        public void QueueExportFrame(long sheetId, long scheduleId, Action<ScheduleSnapshotResult> callback)
        {
            lock (_sync)
                _requests.Enqueue(new Request
                {
                    Kind = Kind.ExportFrame,
                    SheetId = sheetId,
                    ScheduleId = scheduleId,
                    Callback = callback
                });
        }

        public string GetName()
        {
            return "JTOOLS: снимок спецификаций";
        }

        /// <summary>Печать листа (форма ГОСТ) с текущей отредактированной таблицей, полностью
        /// векторно: во временной транзакции исходная спецификация уводится с листа, вместо неё
        /// рисуется наша таблица, лист экспортируется в PDF встроенными средствами Revit, затем
        /// изменения откатываются — модель остаётся неизменной.</summary>
        public void QueuePrintEdited(long sheetId, long scheduleId, List<string> headers, List<string[]> rows,
                                     string targetPath, bool free, Action<ScheduleSnapshotResult> callback)
        {
            lock (_sync)
                _requests.Enqueue(new Request
                {
                    Kind = Kind.PrintEdited,
                    SheetId = sheetId,
                    ScheduleId = scheduleId,
                    Headers = headers,
                    Rows = rows,
                    TargetPath = targetPath,
                    Free = free,
                    Callback = callback
                });
        }

        /// <summary>Создание свободной спецификации: дубликат образца (в него копируются
        /// заголовки колонок), размещение на новом листе со штампом, запись в хранилище правок.</summary>
        public void QueueCreateFree(long templateScheduleId, string sheetNumber, string sheetName,
                                    Action<ScheduleSnapshotResult> callback)
        {
            lock (_sync)
                _requests.Enqueue(new Request
                {
                    Kind = Kind.CreateFree,
                    TemplateScheduleId = templateScheduleId,
                    SheetNumber = sheetNumber,
                    SheetName = sheetName,
                    Callback = callback
                });
        }

        /// <summary>Пакетная печать: все переданные листы (с их отредактированными снимками
        /// спецификаций) собираются в один PDF-файл. Модель не изменяется.</summary>
        public void QueuePrintMany(List<SheetScheduleInfo> items, string targetPath,
                                   Action<ScheduleSnapshotResult> callback)
        {
            lock (_sync)
                _requests.Enqueue(new Request
                {
                    Kind = Kind.PrintMany,
                    Items = items,
                    TargetPath = targetPath,
                    Callback = callback
                });
        }

        /// <summary>Экспорт всех выбранных листов (с их отредактированными снимками) в один
        /// файл Excel: каждая спецификация — отдельный лист книги. Модель не изменяется.</summary>
        public void QueueExportExcel(List<SheetScheduleInfo> items, string targetPath,
                                     Action<ScheduleSnapshotResult> callback)
        {
            lock (_sync)
                _requests.Enqueue(new Request
                {
                    Kind = Kind.ExportExcel,
                    Items = items,
                    TargetPath = targetPath,
                    Callback = callback
                });
        }

        public void Execute(UIApplication app)
        {
            List<Request> batch;
            lock (_sync)
            {
                if (_requests.Count == 0)
                {
                    GrdLog.Log("ScheduleSnapshotHandler.Execute: нет запросов в очереди");
                    return;
                }
                batch = new List<Request>(_requests);
                _requests.Clear();
            }

            foreach (var req in batch)
            {
                try
                {
                    ScheduleSnapshotResult res;
                    switch (req.Kind)
                    {
                        case Kind.ReadList:
                            res = ReadList(app);
                            break;
                        case Kind.ExportFrame:
                            res = ExportFrameImage(app, req.SheetId, req.ScheduleId);
                            break;
                        case Kind.PrintEdited:
                            res = PrintEditedSheet(app, req.SheetId, req.ScheduleId,
                                                   req.Headers, req.Rows, req.TargetPath, req.Free);
                            break;
                        case Kind.PrintMany:
                            res = PrintEditedSheetsMany(app, req.Items, req.TargetPath);
                            break;
                        case Kind.CreateFree:
                            res = CreateFreeSchedule(app, req.TemplateScheduleId,
                                                     req.SheetNumber, req.SheetName);
                            break;
                        case Kind.ExportExcel:
                            res = ExportSheetsExcel(app, req.Items, req.TargetPath);
                            break;
                        default:
                            res = ReadTable(app, req.SheetId, req.ScheduleId);
                            break;
                    }
                    req.Callback?.Invoke(res);
                }
                catch (Exception ex)
                {
                    GrdLog.Log("ScheduleSnapshotHandler.Execute: EXCEPTION: " + ex);
                    try { req.Callback?.Invoke(new ScheduleSnapshotResult { Error = "Ошибка: " + ex.Message }); }
                    catch { }
                }
            }
        }

        // ------------------------------------------------------------------ РАМКА ЛИСТА (РАСТР)

        /// <summary>Экспорт листа в PNG встроенными средствами Revit: рамка, штамп и всё
        /// оформление листа как в модели. Возвращает путь к изображению и прямоугольник
        /// размещённой спецификации в мм от верхнего левого угла листа. Модель не меняется:
        /// исходная спецификация остаётся на изображении, но окно перекрывает её своей таблицей.</summary>
        private static ScheduleSnapshotResult ExportFrameImage(UIApplication app, long sheetId, long scheduleId)
        {
            var res = new ScheduleSnapshotResult();
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null)
            {
                res.Error = "Нет активного документа Revit.";
                return res;
            }
            var sheet = doc.GetElement(new ElementId(sheetId)) as ViewSheet;
            if (sheet == null)
            {
                res.Error = "Лист не найден в документе.";
                return res;
            }

            try
            {
                var outline = sheet.Outline;
                double minX = outline.Min.U, minY = outline.Min.V;
                double maxX = outline.Max.U, maxY = outline.Max.V;
                res.SheetWidthMm = (maxX - minX) * 304.8;
                res.SheetHeightMm = (maxY - minY) * 304.8;

                var inst = new FilteredElementCollector(doc)
                    .OfClass(typeof(ScheduleSheetInstance))
                    .Cast<ScheduleSheetInstance>()
                    .FirstOrDefault(s => s.OwnerViewId == sheet.Id && s.ScheduleId.Value == scheduleId);
                if (inst != null)
                {
                    var bb = inst.get_BoundingBox(sheet);
                    if (bb != null)
                    {
                        res.RectXmm = (bb.Min.X - minX) * 304.8;
                        res.RectWmm = (bb.Max.X - bb.Min.X) * 304.8;
                        res.RectYmm = (maxY - bb.Max.Y) * 304.8;
                        res.RectHmm = (bb.Max.Y - bb.Min.Y) * 304.8;
                        res.RectRotation = (int)inst.Rotation;
                    }
                }

                var tmpDir = Path.Combine(Path.GetTempPath(), "GrdRevit");
                Directory.CreateDirectory(tmpDir);
                var baseName = "frame-" + sheetId + "-" + scheduleId + "-" +
                               Guid.NewGuid().ToString("N").Substring(0, 8);
                var basePath = Path.Combine(tmpDir, baseName);

                var before = new HashSet<string>(
                    Directory.GetFiles(tmpDir, "*.png").Select(Path.GetFullPath),
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
                doc.ExportImage(options);

                var after = Directory.GetFiles(tmpDir, "*.png");
                var produced = after.FirstOrDefault(f =>
                        !before.Contains(Path.GetFullPath(f)) &&
                        Path.GetFileNameWithoutExtension(f).StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                    ?? after.FirstOrDefault(f => !before.Contains(Path.GetFullPath(f)) &&
                        string.Equals(Path.GetDirectoryName(Path.GetFullPath(f)),
                                      Path.GetFullPath(tmpDir), StringComparison.OrdinalIgnoreCase));
                if (produced == null)
                {
                    res.Error = "Revit не создал изображение листа «" + sheet.SheetNumber + "».";
                    return res;
                }
                res.ImagePath = Path.GetFullPath(produced);
                GrdLog.Log("ScheduleSnapshotHandler.ExportFrameImage: лист «" + sheet.SheetNumber +
                           "» -> " + res.ImagePath + "; прямоугольник " +
                           res.RectXmm.ToString("0.#") + "," + res.RectYmm.ToString("0.#") + " " +
                           res.RectWmm.ToString("0.#") + "x" + res.RectHmm.ToString("0.#") + " мм, поворот " +
                           res.RectRotation);
            }
            catch (Exception ex)
            {
                GrdLog.Log("ScheduleSnapshotHandler.ExportFrameImage EXCEPTION: " + ex);
                res.Error = "Не удалось получить изображение листа: " + ex.Message;
            }
            return res;
        }

        // ------------------------------------------------------------------ ЛИСТ + ПРАВКИ (ВЕКТОР)

        private const double MmFt = 1.0 / 304.8;

        private static XGraphics _measureGfx;
        private static bool _measureFailedLogged;
        private static readonly object _measureLock = new object();
        private static readonly Dictionary<string, double> _measureCache = new Dictionary<string, double>();

        /// <summary>Отношение высоты прописной буквы к кеглю em для Arial: Revit свойством
        /// TEXT_SIZE задаёт высоту прописной буквы, а не кегль.</summary>
        private const double ArialCapRatio = 0.716;

        /// <summary>Ширина строки в футах при кегле textH (футы) по метрикам Arial.
        /// При недоступности замерщика — грубая оценка по числу символов.</summary>
        private static double TextWidth(string text, double textH)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            double emMm = (textH * 304.8) / ArialCapRatio;
            string key = emMm.ToString("0.###") + "|" + text;
            lock (_measureLock)
            {
                if (_measureCache.TryGetValue(key, out var cached)) return cached;
                double w;
                try
                {
                    if (_measureGfx == null)
                    {
                        GrdRevit.Ui.GdiFontResolver.Install();
                        _measureGfx = XGraphics.CreateMeasureContext(new XSize(1000, 1000),
                            XGraphicsUnit.Point, XPageDirection.Downwards);
                    }
                    double emPt = emMm * 72.0 / 25.4;
                    var font = new XFont("Arial", emPt, XFontStyleEx.Regular);
                    w = _measureGfx.MeasureString(text, font).Width * 25.4 / 72.0 * MmFt;
                }
                catch (Exception ex)
                {
                    if (!_measureFailedLogged)
                    {
                        _measureFailedLogged = true;
                        GrdLog.Log("ScheduleSnapshotHandler.TextWidth: замер недоступен, оценка: " + ex.Message);
                    }
                    w = text.Length * textH * 0.6;
                }
                _measureCache[key] = w;
                return w;
            }
        }

        /// <summary>Печать листа с текущей таблицей без растровой подложки: Revit сам рисует
        /// рамку/штамп (форма ГОСТ), а на месте спецификации временно размещается наша таблица.
        /// Всё векторное; после экспорта модель возвращается в исходное состояние.</summary>
        private static ScheduleSnapshotResult PrintEditedSheet(UIApplication app, long sheetId, long scheduleId,
                                                               List<string> headers, List<string[]> rows,
                                                               string targetPath, bool isFree)
        {
            var res = new ScheduleSnapshotResult();
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null) { res.Error = "Нет активного документа Revit."; return res; }
            if (string.IsNullOrEmpty(targetPath)) { res.Error = "Не задан путь для PDF."; return res; }
            if (headers == null || headers.Count == 0) { res.Error = "Нет данных для печати."; return res; }
            var sheet = doc.GetElement(new ElementId(sheetId)) as ViewSheet;
            if (sheet == null) { res.Error = "Лист не найден в документе."; return res; }

            var viewFamilyType = GetDraftingViewType(doc);
            if (viewFamilyType == null) { res.Error = "В документе нет типа вида «Чертёжный вид»."; return res; }

            var created = new List<ElementId>();
            var tempTypes = new List<ElementId>();
            var restores = new List<SheetRestore>();
            Transaction t1 = null;
            try
            {
                t1 = new Transaction(doc, "JTOOLS: временная спецификация для печати");
                t1.Start();

                var err = PrepareSheetTable(doc, sheet, scheduleId, headers, rows, viewFamilyType,
                                            created, tempTypes, restores, isFree);
                if (err != null)
                {
                    res.Error = err;
                    try { t1.RollBack(); } catch { t1.Dispose(); }
                    t1 = null;
                    return res;
                }

                t1.Commit();
                t1 = null;

                var ex2 = ExportSheetsPdf(doc, new List<ViewSheet> { sheet }, targetPath);
                if (ex2 != null) res.Error = ex2;
                else
                {
                    GrdLog.Log("ScheduleSnapshotHandler.PrintEditedSheet: лист «" + sheet.SheetNumber +
                               "» -> " + targetPath);
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("ScheduleSnapshotHandler.PrintEditedSheet EXCEPTION: " + ex);
                res.Error = "Не удалось напечатать лист: " + ex.Message;
            }
            finally
            {
                try { t1?.RollBack(); } catch { t1?.Dispose(); }
                RestoreSheets(doc, created, tempTypes, restores, res);
            }
            return res;
        }

        /// <summary>Состояние размещённой спецификации до временных изменений: куда и как вернуть.</summary>
        private sealed class SheetRestore
        {
            public ElementId InstId;
            public XYZ Point;
            public ViewportRotation Rotation;
            public int Segment;
        }

        private static ViewFamilyType GetDraftingViewType(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.Drafting);
        }

        /// <summary>Готовит временную таблицу на листе (рамка и штамп по ГОСТ плюс отредактированная
        /// спецификация) внутри уже открытой транзакции. Исходная спецификация уводится с листа, её
        /// положение запоминается в restores. Возвращает null при успехе, иначе текст ошибки.</summary>
        private static string PrepareSheetTable(Document doc, ViewSheet sheet, long scheduleId,
                                                List<string> headers, List<string[]> rows,
                                                ViewFamilyType viewFamilyType,
                                                List<ElementId> created, List<ElementId> tempTypes,
                                                List<SheetRestore> restores, bool isFree = false)
        {
            var inst = new FilteredElementCollector(doc)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>()
                .FirstOrDefault(s => s.OwnerViewId == sheet.Id && s.ScheduleId.Value == scheduleId);
            if (inst == null) return "Спецификация не найдена на листе «" + sheet.SheetNumber + "».";
            var bb = inst.get_BoundingBox(sheet);
            if (bb == null) return "Не удалось определить габарит спецификации на листе «" + sheet.SheetNumber + "».";

            restores.Add(new SheetRestore
            {
                InstId = inst.Id,
                Point = inst.Point,
                Rotation = inst.Rotation,
                Segment = inst.SegmentIndex
            });

            var outline = sheet.Outline;
            double sheetW = outline.Max.U - outline.Min.U;
            double sheetH = outline.Max.V - outline.Min.V;

            // Рамка листа: по ГОСТ отступ 20 мм слева и 5 мм с остальных сторон.
            double frameLeft = outline.Min.U + 20.0 * MmFt;
            double frameRight = outline.Max.U - 5.0 * MmFt;
            double frameBottom = outline.Min.V + 5.0 * MmFt;
            double frameTop = outline.Max.V - 5.0 * MmFt;

            // Габарит штампа уточняет рамку ТОЛЬКО пересечением: рамка ГОСТ не может
            // расшириться из-за штампа, но может оказаться уже (внутри) неё. Иначе штамп
            // размером во весь лист обнуляет подрезку.
            try
            {
                long tbCat = (long)BuiltInCategory.OST_TitleBlocks;
                var tb = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .FirstOrDefault(f => f.OwnerViewId == sheet.Id &&
                                         f.Category != null && f.Category.Id.Value == tbCat);
                var tbb = tb?.get_BoundingBox(sheet);
                if (tbb != null)
                {
                    double tw = tbb.Max.X - tbb.Min.X, th = tbb.Max.Y - tbb.Min.Y;
                    if (tw > sheetW * 0.5 && th > sheetH * 0.5)
                    {
                        frameLeft = Math.Max(frameLeft, tbb.Min.X);
                        frameRight = Math.Min(frameRight, tbb.Max.X);
                        frameBottom = Math.Max(frameBottom, tbb.Min.Y);
                        frameTop = Math.Min(frameTop, tbb.Max.Y);
                    }
                }
            }
            catch (Exception ex) { GrdLog.Log("PrepareSheetTable: габарит штампа: " + ex.Message); }

            // Габарит спецификации, подрезанный по рамке: таблица не выходит за её поля.
            // Для свободной спецификации тело пустое (только заголовок), поэтому ширину берём
            // от рамки, а не от габарита — иначе таблица печаталась бы по ширине заголовка.
            double inset = 1.0 * MmFt;
            double left = Math.Max(bb.Min.X, frameLeft + inset);
            double top = Math.Min(bb.Max.Y, frameTop - inset);
            double right;
            double rectW;
            double rectH;
            if (isFree)
            {
                // Свободная спецификация привязывается к углу поля листа, а не к позиции
                // носителя: иначе таблица печатается со смещением, равным смещению носителя.
                left = frameLeft + inset;
                top = frameTop - inset;
                right = frameRight - inset;
                rectW = right - left;
                rectH = 0;
            }
            else
            {
                right = Math.Min(bb.Max.X, frameRight - inset);
                if (right <= left) { left = bb.Min.X; right = bb.Max.X; }
                rectW = right - left;
                rectH = Math.Min(bb.Max.Y - bb.Min.Y, top - Math.Max(bb.Min.Y, frameBottom + inset));
                if (rectH <= 0) rectH = bb.Max.Y - bb.Min.Y;
            }
            double maxH = top - (frameBottom + inset);

            GrdLog.Log("PrepareSheetTable: лист «" + sheet.SheetNumber + "» " +
                       (sheetW * 304.8).ToString("0.#") + "x" + (sheetH * 304.8).ToString("0.#") +
                       " мм; рамка " + ((frameRight - frameLeft) * 304.8).ToString("0.#") + "x" +
                       ((frameTop - frameBottom) * 304.8).ToString("0.#") + " мм; спецификация " +
                       ((bb.Min.X - outline.Min.U) * 304.8).ToString("0.#") + "," +
                       ((outline.Max.V - bb.Max.Y) * 304.8).ToString("0.#") + " " +
                       ((bb.Max.X - bb.Min.X) * 304.8).ToString("0.#") + "x" +
                       ((bb.Max.Y - bb.Min.Y) * 304.8).ToString("0.#") +
                       " мм; поле " + ((left - outline.Min.U) * 304.8).ToString("0.#") + "," +
                       ((outline.Max.V - top) * 304.8).ToString("0.#") + " " +
                       (rectW * 304.8).ToString("0.#") + "x" + (rectH * 304.8).ToString("0.#") +
                       " мм; строк=" + rows.Count + " колонок=" + headers.Count);

            // Реальные ширины колонок из модели: перенос текста совпадёт с отображением.
            double[] colWidths = null;
            try
            {
                var modelSchedule = doc.GetElement(new ElementId(scheduleId)) as ViewSchedule;
                var body = modelSchedule?.GetTableData()?.GetSectionData(SectionType.Body);
                if (body != null && body.NumberOfColumns == headers.Count)
                {
                    var w = new double[headers.Count];
                    bool ok = true;
                    for (int c = 0; c < w.Length; c++)
                    {
                        w[c] = body.GetColumnWidth(c);
                        if (w[c] <= 0) { ok = false; break; }
                    }
                    if (ok) colWidths = w;
                }
            }
            catch (Exception ex) { GrdLog.Log("PrepareSheetTable: ширины колонок: " + ex.Message); }

            // Вписать таблицу по высоте в габарит исходной спецификации, но не мельче 1.6 мм.
            // Свободная спецификация не подгоняется под габарит заголовка (он пуст) — печатается
            // естественным размером и при необходимости обрезается по высоте поля листа.
            var layout = BuildLayout(headers, rows, rectW, 2.5 * MmFt, colWidths);
            if (!isFree && rectH > 0 && layout.TotalH > rectH)
            {
                double k = rectH / layout.TotalH;
                layout = BuildLayout(headers, rows, rectW, Math.Max(1.6 * MmFt, layout.TextH * k), colWidths);
            }

            double cropW = Math.Min(layout.TotalW,
                isFree ? Math.Max(0.0, frameRight - inset - left) : (frameRight - frameLeft));
            double cropH = layout.TotalH;
            if (maxH > 0 && cropH > maxH) cropH = maxH;
            if (cropH <= 0) cropH = rectH;

            GrdLog.Log("PrepareSheetTable: текст " + (layout.TextH * 304.8).ToString("0.##") +
                       " мм; колонок из модели=" + (colWidths != null ? colWidths.Length : 0) +
                       "; таблица " + (layout.TotalW * 304.8).ToString("0.#") + "x" +
                       (layout.TotalH * 304.8).ToString("0.#") + " мм; вьюпорт " +
                       (cropW * 304.8).ToString("0.#") + "x" + (cropH * 304.8).ToString("0.#") +
                       " мм" + (layout.TotalH > cropH + 1e-6 ? " (низ обрезан)" : string.Empty));

            // Исходную спецификацию убрать с листа (вернём при восстановлении).
            inst.Point = new XYZ(inst.Point.X - 500.0, inst.Point.Y, inst.Point.Z);

            var dv = ViewDrafting.Create(doc, viewFamilyType.Id);
            dv.Scale = 1;
            created.Add(dv.Id);

            DrawLayout(doc, dv, layout, created, tempTypes);

            // Вид обрезан ровно по габариту таблицы; левый верхний угол — в левом верхнем
            // углу допустимого поля листа.
            dv.CropBoxActive = true;
            dv.CropBoxVisible = false;
            dv.CropBox = new BoundingBoxXYZ
            {
                Min = new XYZ(0, -cropH, 0),
                Max = new XYZ(cropW, 0, 0),
                Transform = Transform.Identity
            };
            dv.CropBoxActive = true;

            var center = new XYZ(left + cropW / 2.0, top - cropH / 2.0, 0);
            var vp = Viewport.Create(doc, sheet.Id, dv.Id, center);
            created.Add(vp.Id);
            try { vp.SetBoxCenter(center); } catch (Exception ex) { GrdLog.Log("PrepareSheetTable.SetBoxCenter: " + ex.Message); }
            try { vp.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL)?.Set(0); } catch { }
            try
            {
                var bo = vp.GetBoxOutline();
                GrdLog.Log("PrepareSheetTable: вьюпорт " +
                           ((bo.MinimumPoint.X - outline.Min.U) * 304.8).ToString("0.#") + "," +
                           ((outline.Max.V - bo.MaximumPoint.Y) * 304.8).ToString("0.#") + " " +
                           ((bo.MaximumPoint.X - bo.MinimumPoint.X) * 304.8).ToString("0.#") + "x" +
                           ((bo.MaximumPoint.Y - bo.MinimumPoint.Y) * 304.8).ToString("0.#") + " мм");
            }
            catch (Exception ex) { GrdLog.Log("PrepareSheetTable.GetBoxOutline: " + ex.Message); }

            return null;
        }

        /// <summary>Удаляет временные элементы и возвращает спецификации на листы.
        /// Ошибку восстановления пишет в res.Error, если он ещё пуст.</summary>
        private static void RestoreSheets(Document doc, List<ElementId> created, List<ElementId> tempTypes,
                                          List<SheetRestore> restores, ScheduleSnapshotResult res)
        {
            if (created.Count == 0 && tempTypes.Count == 0 && restores.Count == 0) return;
            Transaction t = null;
            try
            {
                t = new Transaction(doc, "JTOOLS: возврат спецификации");
                t.Start();
                for (int i = created.Count - 1; i >= 0; i--)
                {
                    try { if (doc.GetElement(created[i]) != null) doc.Delete(created[i]); } catch { }
                }
                foreach (var r in restores)
                {
                    var back = doc.GetElement(r.InstId) as ScheduleSheetInstance;
                    if (back != null)
                    {
                        back.Point = r.Point;
                        back.Rotation = r.Rotation;
                        back.SegmentIndex = r.Segment;
                    }
                }
                foreach (var tt in tempTypes)
                {
                    try { if (doc.GetElement(tt) != null) doc.Delete(tt); } catch { }
                }
                t.Commit();
                t = null;
            }
            catch (Exception ex)
            {
                GrdLog.Log("ScheduleSnapshotHandler.RestoreSheets EXCEPTION: " + ex);
                try { t?.RollBack(); } catch { }
                if (res != null && string.IsNullOrEmpty(res.Error))
                    res.Error = "PDF создан, но не удалось вернуть исходные спецификации на листы: " + ex.Message;
            }
        }

        /// <summary>Готовый к печати лист: спецификация и её отредактированные данные.</summary>
        private sealed class PrintJobData
        {
            public ViewSheet Sheet;
            public long ScheduleId;
            public List<string> Headers;
            public List<string[]> Rows;
            public bool Free;
        }

        /// <summary>Лист и его отредактированная таблица (для печати и экспорта в Excel).</summary>
        private sealed class EditedTableData
        {
            public ViewSheet Sheet;
            public SheetScheduleInfo It;
            public ScheduleTable Table;
        }

        /// <summary>Читает таблицы выбранных листов, подставляя сохранённые правки (для свободной
        /// спецификации источник — только правки). Общий для пакетной печати и экспорта в Excel.</summary>
        private static List<EditedTableData> GatherEditedTables(UIApplication app, List<SheetScheduleInfo> items)
        {
            var result = new List<EditedTableData>();
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null || items == null) return result;
            string docKey = GetDocKey(doc);

            foreach (var it in items)
            {
                if (it == null) continue;
                var sheet = doc.GetElement(new ElementId(it.SheetId)) as ViewSheet;
                if (sheet == null)
                {
                    GrdLog.Log("GatherEditedTables: лист sheetId=" + it.SheetId + " не найден");
                    continue;
                }

                ScheduleTable t = null;
                try
                {
                    var rr = ReadTable(app, it.SheetId, it.ScheduleId);
                    if (!string.IsNullOrEmpty(rr.Error))
                        GrdLog.Log("GatherEditedTables: «" + it.ScheduleName + "»: " + rr.Error);
                    else
                        t = rr.Table;
                }
                catch (Exception ex)
                {
                    GrdLog.Log("GatherEditedTables: чтение «" + it.ScheduleName + "» EXCEPTION " + ex.Message);
                }

                if (t == null || t.Rows.Count == 0)
                {
                    GrdLog.Log("GatherEditedTables: лист «" + it.SheetNumber + "» пропущен (нет данных)");
                    continue;
                }

                // Сохранённые окном правки — если набор колонок совпадает с моделью.
                // Для свободной спецификации источник — только сохранённые правки.
                try
                {
                    var saved = ScheduleSnapshotsFile.LoadEntry(docKey, it.ScheduleId, t.SheetId, t.SegmentIndex);
                    bool free = (saved != null && saved.IsFree) || t.IsFree;
                    if (saved != null && HeadersSame(saved.Headers, t.Headers) &&
                        (free || saved.Rows.Count > 0))
                    {
                        t.Headers = saved.Headers;
                        t.Rows = saved.Rows;
                    }
                    t.IsFree = free;
                }
                catch (Exception ex) { GrdLog.Log("GatherEditedTables: правки EXCEPTION " + ex.Message); }

                result.Add(new EditedTableData { Sheet = sheet, It = it, Table = t });
            }
            return result;
        }

        /// <summary>Экспорт всех выбранных листов (их отредактированные снимки спецификаций)
        /// в один файл Excel: каждая спецификация — отдельный лист книги. Модель не изменяется.</summary>
        private static ScheduleSnapshotResult ExportSheetsExcel(UIApplication app, List<SheetScheduleInfo> items,
                                                                string targetPath)
        {
            var res = new ScheduleSnapshotResult();
            if (app?.ActiveUIDocument?.Document == null) { res.Error = "Нет активного документа Revit."; return res; }
            if (string.IsNullOrEmpty(targetPath)) { res.Error = "Не задан путь для файла Excel."; return res; }
            if (items == null || items.Count == 0) { res.Error = "Листы не выбраны."; return res; }

            var data = GatherEditedTables(app, items);
            if (data.Count == 0)
            {
                res.Error = "Нечего экспортировать: спецификации не найдены или пусты.";
                return res;
            }

            try
            {
                var sheets = new List<XlsxSheet>();
                foreach (var d in data)
                    sheets.Add(new XlsxSheet
                    {
                        Name = d.Table.ScheduleName,
                        Headers = d.Table.Headers,
                        Rows = d.Table.Rows
                    });
                XlsxWriter.Write(targetPath, sheets);
            }
            catch (Exception ex)
            {
                GrdLog.Log("ExportSheetsExcel EXCEPTION: " + ex);
                res.Error = "Не удалось сохранить файл Excel: " + ex.Message;
                return res;
            }

            GrdLog.Log("ExportSheetsExcel: экспортировано таблиц=" + data.Count + " → " + targetPath);
            return res;
        }

        /// <summary>Пакетная печать: каждый выбранный лист получает рамку и штамп ГОСТ и свою
        /// отредактированную спецификацию, затем все листы объединяются в один PDF.
        /// Модель не изменяется: временные элементы удаляются после экспорта.</summary>
        private static ScheduleSnapshotResult PrintEditedSheetsMany(UIApplication app, List<SheetScheduleInfo> items,
                                                                    string targetPath)
        {
            var res = new ScheduleSnapshotResult();
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null) { res.Error = "Нет активного документа Revit."; return res; }
            if (string.IsNullOrEmpty(targetPath)) { res.Error = "Не задан путь для PDF."; return res; }
            if (items == null || items.Count == 0) { res.Error = "Листы не выбраны."; return res; }

            var viewFamilyType = GetDraftingViewType(doc);
            if (viewFamilyType == null) { res.Error = "В документе нет типа вида «Чертёжный вид»."; return res; }

            var data = GatherEditedTables(app, items);
            var jobs = new List<PrintJobData>();
            foreach (var d in data)
            {
                jobs.Add(new PrintJobData
                {
                    Sheet = d.Sheet,
                    ScheduleId = d.It.ScheduleId,
                    Headers = d.Table.Headers,
                    Rows = d.Table.Rows,
                    Free = d.Table.IsFree
                });
            }

            if (jobs.Count == 0) { res.Error = "Нечего печатать: спецификации не найдены или пусты."; return res; }

            var created = new List<ElementId>();
            var tempTypes = new List<ElementId>();
            var restores = new List<SheetRestore>();
            Transaction t1 = null;
            try
            {
                t1 = new Transaction(doc, "JTOOLS: временные спецификации для пакетной печати");
                t1.Start();

                var failed = new List<string>();
                foreach (var job in jobs)
                {
                    var err = PrepareSheetTable(doc, job.Sheet, job.ScheduleId, job.Headers, job.Rows,
                                                viewFamilyType, created, tempTypes, restores, job.Free);
                    if (err != null) failed.Add("№" + job.Sheet.SheetNumber + ": " + err);
                }

                t1.Commit();
                t1 = null;

                var sheets = jobs.Select(j => j.Sheet).ToList();
                var ex2 = ExportSheetsPdf(doc, sheets, targetPath);
                if (ex2 != null) res.Error = ex2;
                else
                {
                    GrdLog.Log("ScheduleSnapshotHandler.PrintEditedSheetsMany: листов=" + jobs.Count +
                               " -> " + targetPath);
                }

                if (failed.Count > 0)
                    res.Error = "Часть листов не удалось подготовить: " + string.Join("; ", failed.ToArray());
            }
            catch (Exception ex)
            {
                GrdLog.Log("ScheduleSnapshotHandler.PrintEditedSheetsMany EXCEPTION: " + ex);
                res.Error = "Не удалось напечатать листы: " + ex.Message;
            }
            finally
            {
                try { t1?.RollBack(); } catch { t1?.Dispose(); }
                RestoreSheets(doc, created, tempTypes, restores, res);
            }
            return res;
        }

        private static bool HeadersSame(List<string> a, List<string> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>Расчёт геометрии таблицы: ширины колонок, перенос текста, высоты строк.</summary>
        private sealed class TableLayout
        {
            public double TextH, PadX, PadY, LineH, CharW, TotalW, TotalH, HeaderH;
            public int Cols;
            public List<string> ColHeaders;
            public double[] ColX, ColW, RowH;
            public List<string>[] HeaderLines;
            public List<string>[][] RowLines;
        }

        /// <summary>Геометрия таблицы. Ширины колонок берутся из модели (fixedWidths, футы),
        /// если их удалось прочитать; иначе колонки распределяются по содержимому.</summary>
        private static TableLayout BuildLayout(List<string> headers, List<string[]> rows, double totalW,
                                               double textH, double[] fixedWidths)
        {
            double padX = 1.0 * MmFt, padY = 0.5 * MmFt;
            double lineH = textH * 1.7, charW = textH * 0.6;
            int n = headers.Count;

            var colW = new double[n];
            var colX = new double[n];
            double x = 0;

            if (fixedWidths != null && fixedWidths.Length == n)
            {
                double sum = 0;
                for (int c = 0; c < n; c++) sum += fixedWidths[c];
                double k = (sum > 0 && totalW > 0 && sum > totalW) ? totalW / sum : 1.0;
                for (int c = 0; c < n; c++)
                {
                    colW[c] = fixedWidths[c] * k;
                    colX[c] = x;
                    x += colW[c];
                }
            }
            else
            {
                var natural = new double[n];
                double total = 0;
                for (int c = 0; c < n; c++)
                {
                    int maxLen = Math.Max(headers[c]?.Length ?? 0, 4);
                    for (int r = 0; r < rows.Count && r < 500; r++)
                    {
                        var cell = rows[r][c];
                        if (cell != null && cell.Length > maxLen) maxLen = cell.Length;
                    }
                    natural[c] = Math.Min(maxLen, 70) * charW + 2 * padX;
                    total += natural[c];
                }
                for (int c = 0; c < n; c++)
                {
                    colW[c] = total > 0 ? natural[c] * (totalW / total) : totalW / n;
                    colX[c] = x;
                    x += colW[c];
                }
            }
            totalW = x;

            var headerLines = new List<string>[n];
            int headerMax = 1;
            for (int c = 0; c < n; c++)
            {
                headerLines[c] = WrapText(headers[c] ?? string.Empty, colW[c] - 2 * padX, textH);
                if (headerLines[c].Count > headerMax) headerMax = headerLines[c].Count;
            }
            double headerH = headerMax * lineH + 2 * padY;

            var rowLines = new List<string>[rows.Count][];
            var rowH = new double[rows.Count];
            for (int r = 0; r < rows.Count; r++)
            {
                rowLines[r] = new List<string>[n];
                int maxLines = 1;
                for (int c = 0; c < n; c++)
                {
                    rowLines[r][c] = WrapText(rows[r][c] ?? string.Empty, colW[c] - 2 * padX, textH);
                    if (rowLines[r][c].Count > maxLines) maxLines = rowLines[r][c].Count;
                }
                rowH[r] = maxLines * lineH + 2 * padY;
            }

            double totalH = headerH;
            for (int r = 0; r < rows.Count; r++) totalH += rowH[r];

            return new TableLayout
            {
                TextH = textH, PadX = padX, PadY = padY, LineH = lineH, CharW = charW,
                TotalW = totalW, TotalH = totalH, HeaderH = headerH, Cols = n,
                ColHeaders = headers, HeaderLines = headerLines,
                ColX = colX, ColW = colW, RowH = rowH, RowLines = rowLines
            };
        }

        /// <summary>Рисует таблицу на чертёжном виде (линии сетки + заметки).</summary>
        private static void DrawLayout(Document doc, ViewDrafting dv, TableLayout L,
                                       List<ElementId> created, List<ElementId> tempTypes)
        {
            var baseType = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>().FirstOrDefault();
            var cellType = (TextNoteType)baseType.Duplicate("GRD_TMP_CELL_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            cellType.get_Parameter(BuiltInParameter.TEXT_SIZE).Set(L.TextH);
            try { cellType.get_Parameter(BuiltInParameter.TEXT_FONT).Set("Arial"); } catch { }
            try { cellType.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD).Set(0); } catch { }
            tempTypes.Add(cellType.Id);

            var headerType = (TextNoteType)baseType.Duplicate("GRD_TMP_HEAD_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            headerType.get_Parameter(BuiltInParameter.TEXT_SIZE).Set(L.TextH);
            try { headerType.get_Parameter(BuiltInParameter.TEXT_FONT).Set("Arial"); } catch { }
            try { headerType.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD).Set(1); } catch { }
            tempTypes.Add(headerType.Id);

            int n = L.Cols;
            for (int c = 0; c <= n; c++)
            {
                double gx = c < n ? L.ColX[c] : L.TotalW;
                AddLine(doc, dv, gx, 0, gx, -L.TotalH, created);
            }
            double gy = 0;
            AddLine(doc, dv, 0, gy, L.TotalW, gy, created);
            gy -= L.HeaderH;
            for (int r = 0; r < L.RowH.Length; r++)
            {
                AddLine(doc, dv, 0, gy, L.TotalW, gy, created);
                gy -= L.RowH[r];
            }
            // Нижняя граница последней строки (gy == -L.TotalH).
            AddLine(doc, dv, 0, gy, L.TotalW, gy, created);

            for (int c = 0; c < n; c++)
            {
                double hy = -L.PadY;
                foreach (var line in L.HeaderLines[c])
                {
                    AddNote(doc, dv, new XYZ(L.ColX[c] + L.PadX, hy, 0), line, headerType.Id,
                            L.ColW[c] - 2 * L.PadX, created);
                    hy -= L.LineH;
                }
            }

            double y = -L.HeaderH;
            for (int r = 0; r < L.RowLines.Length; r++)
            {
                for (int c = 0; c < n; c++)
                {
                    double ty = y - L.PadY;
                    foreach (var line in L.RowLines[r][c])
                    {
                        AddNote(doc, dv, new XYZ(L.ColX[c] + L.PadX, ty, 0), line, cellType.Id,
                                L.ColW[c] - 2 * L.PadX, created);
                        ty -= L.LineH;
                    }
                }
                y -= L.RowH[r];
            }
        }

        private static void AddLine(Document doc, View view, double x1, double y1, double x2, double y2,
                                    List<ElementId> created)
        {
            try
            {
                var curve = Line.CreateBound(new XYZ(x1, y1, 0), new XYZ(x2, y2, 0));
                var dc = doc.Create.NewDetailCurve(view, curve);
                if (dc != null) created.Add(dc.Id);
            }
            catch (Exception ex) { GrdLog.Log("ScheduleSnapshotHandler.AddLine EXCEPTION: " + ex.Message); }
        }

        private static void AddNote(Document doc, View view, XYZ pos, string text, ElementId typeId,
                                    double widthF, List<ElementId> created)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var note = TextNote.Create(doc, view.Id, pos, text, new TextNoteOptions
                {
                    TypeId = typeId,
                    HorizontalAlignment = HorizontalTextAlignment.Left,
                    VerticalAlignment = VerticalTextAlignment.Top,
                    KeepRotatedTextReadable = true
                });
                if (note != null)
                {
                    if (widthF > 0)
                    {
                        try { note.Width = widthF; } catch (Exception ex) { GrdLog.Log("ScheduleSnapshotHandler.AddNote.Width: " + ex.Message); }
                    }
                    created.Add(note.Id);
                }
            }
            catch (Exception ex) { GrdLog.Log("ScheduleSnapshotHandler.AddNote EXCEPTION: " + ex.Message); }
        }

        /// <summary>Перенос текста по ширине колонки с точным замером метрик Arial.
        /// Слова, не влезающие целиком, разбиваются по символам.</summary>
        private static List<string> WrapText(string text, double width, double textH)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) { result.Add(string.Empty); return result; }
            if (width <= 0) width = 1.0 * MmFt;

            var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var cur = new System.Text.StringBuilder();
            double curW = 0;

            foreach (var word in words)
            {
                double wordW = TextWidth(word, textH);

                if (wordW > width)
                {
                    if (cur.Length > 0) { result.Add(cur.ToString()); cur.Clear(); curW = 0; }
                    var part = new System.Text.StringBuilder();
                    double partW = 0;
                    foreach (var ch in word)
                    {
                        double chW = TextWidth(ch.ToString(), textH);
                        if (part.Length > 0 && partW + chW > width)
                        {
                            result.Add(part.ToString());
                            part.Clear();
                            partW = 0;
                        }
                        part.Append(ch);
                        partW += chW;
                    }
                    if (part.Length > 0) { cur.Append(part); curW = partW; }
                    continue;
                }

                double spW = cur.Length > 0 ? TextWidth(" ", textH) : 0;
                if (cur.Length > 0 && curW + spW + wordW > width)
                {
                    result.Add(cur.ToString());
                    cur.Clear();
                    curW = 0;
                    spW = 0;
                }
                if (cur.Length > 0) { cur.Append(' '); curW += spW; }
                cur.Append(word);
                curW += wordW;
            }

            if (cur.Length > 0) result.Add(cur.ToString());
            if (result.Count == 0) result.Add(string.Empty);
            return result;
        }

        /// <summary>Экспорт листа в PDF встроенными средствами Revit. Возвращает null при успехе.</summary>
        /// <summary>Экспорт одного или нескольких листов в PDF штатными средствами Revit.
        /// Несколько листов объединяются в один файл (Combine), порядок — как в списке.
        /// Возвращает null при успехе.</summary>
        private static string ExportSheetsPdf(Document doc, List<ViewSheet> sheets, string targetPath)
        {
            if (sheets == null || sheets.Count == 0) return "Не выбрано ни одного листа для экспорта.";
            var dir = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
            Directory.CreateDirectory(dir);
            var baseName = Path.GetFileNameWithoutExtension(targetPath);
            var targetFull = Path.GetFullPath(targetPath);

            // Уже существующий файл удаляем, чтобы Revit записал ровно baseName.pdf, а не «(2)».
            try { if (File.Exists(targetFull)) File.Delete(targetFull); } catch { }
            var before = new HashSet<string>(
                Directory.GetFiles(dir, "*.pdf").Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

            var options = new PDFExportOptions
            {
                FileName = baseName,
                Combine = sheets.Count > 1,
                ZoomType = ZoomType.FitToPage,
                ColorDepth = ColorDepthType.Color,
                ExportQuality = PDFExportQualityType.DPI300,
                HideCropBoundaries = true,
                HideScopeBoxes = true,
                HideReferencePlane = true,
                PaperOrientation = PageOrientationType.Auto
            };
            doc.Export(dir, sheets.Select(s => s.Id).ToList(), options);

            var expected = Path.Combine(dir, baseName + ".pdf");
            string produced;
            if (File.Exists(expected)) produced = expected;
            else
            {
                produced = Directory.GetFiles(dir, "*.pdf")
                    .FirstOrDefault(f => !before.Contains(Path.GetFullPath(f)));
            }
            if (produced == null)
                return sheets.Count > 1
                    ? "Revit не создал PDF для выбранных листов."
                    : "Revit не создал PDF для листа «" + sheets[0].SheetNumber + "».";

            var producedFull = Path.GetFullPath(produced);
            if (!string.Equals(producedFull, targetFull, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(targetFull)) File.Delete(targetFull);
                File.Move(producedFull, targetFull);
            }
            return null;
        }

        // ------------------------------------------------------------------ СПИСОК

        /// <summary>Ключ документа для хранения сохранённых правок: путь файла, а для
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

        // ------------------------------------------------------------------ СВОБОДНЫЕ СПЕЦИФИКАЦИИ

        /// <summary>Заголовки колонок спецификации по её определению (видимые поля, в порядке
        /// вывода). Используется как резерв, если сохранённых заголовков ещё нет.</summary>
        private static List<string> GetScheduleHeaders(ViewSchedule schedule)
        {
            var list = new List<string>();
            try
            {
                var def = schedule?.Definition;
                if (def == null) return list;
                foreach (var fid in def.GetFieldOrder())
                {
                    try
                    {
                        var f = def.GetField(fid);
                        if (f == null || f.IsHidden) continue;
                        var h = f.ColumnHeading;
                        if (string.IsNullOrEmpty(h)) h = f.GetName();
                        list.Add(h ?? string.Empty);
                    }
                    catch { }
                }
            }
            catch (Exception ex) { GrdLog.Log("GetScheduleHeaders: " + ex.Message); }
            return list;
        }

        /// <summary>Тип штампа (основной надписи) с листа, на котором размещён образец.</summary>
        private static ElementId GetTitleBlockTypeId(Document doc, ViewSchedule schedule)
        {
            try
            {
                long tbCat = (long)BuiltInCategory.OST_TitleBlocks;
                foreach (ScheduleSheetInstance inst in new FilteredElementCollector(doc)
                             .OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                             .Where(i => i.ScheduleId == schedule.Id))
                {
                    var sheet = doc.GetElement(inst.OwnerViewId) as ViewSheet;
                    if (sheet == null) continue;
                    var tb = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                        .FirstOrDefault(f => f.OwnerViewId == sheet.Id &&
                                             f.Category != null && f.Category.Id.Value == tbCat);
                    if (tb != null) return tb.GetTypeId();
                }
            }
            catch (Exception ex) { GrdLog.Log("GetTitleBlockTypeId: " + ex.Message); }
            return ElementId.InvalidElementId;
        }

        private static string MakeUniqueScheduleName(Document doc, string baseName, ElementId excludeId)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ViewSchedule v in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)))
            {
                if (v == null || v.Name == null || (excludeId != null && v.Id == excludeId)) continue;
                used.Add(v.Name);
            }
            if (!used.Contains(baseName)) return baseName;
            for (int i = 2; i < 1000; i++)
            {
                var cand = baseName + " " + i;
                if (!used.Contains(cand)) return cand;
            }
            return baseName + " " + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        /// <summary>Пытается сделать спецификацию пустой фильтром (строка или элементид, которые
        /// заведомо не совпадут). Заголовки колонок сохраняются. Не удалось — спецификация
        /// остаётся с данными, но плагин всё равно показывает только свои строки.</summary>
        private static bool TryEmptySchedule(Document doc, ViewSchedule s, out string info)
        {
            info = string.Empty;
            try
            {
                var def = s.Definition;
                if (def == null || !def.CanFilter()) { info = "фильтр недоступен"; return false; }

                ElementId foreignId = ElementId.InvalidElementId;
                try { foreignId = new FilteredElementCollector(doc).OfClass(typeof(WallType)).FirstElementId(); }
                catch { }
                string needle = "__GRD_FREE_" + Guid.NewGuid().ToString("N");

                foreach (var fid in def.GetFieldOrder())
                {
                    bool canValue;
                    try { canValue = def.CanFilterByValue(fid); } catch { canValue = false; }
                    if (!canValue) continue;
                    try
                    {
                        def.AddFilter(new ScheduleFilter(fid, ScheduleFilterType.Equal, needle));
                        info = "строковый фильтр";
                        return true;
                    }
                    catch { }
                    if (foreignId != null && foreignId != ElementId.InvalidElementId)
                    {
                        try
                        {
                            def.AddFilter(new ScheduleFilter(fid, ScheduleFilterType.Equal, foreignId));
                            info = "фильтр по ElementId";
                            return true;
                        }
                        catch { }
                    }
                }

                foreach (var fid in def.GetFieldOrder())
                {
                    bool canPres;
                    try { canPres = def.CanFilterByValuePresence(fid); } catch { canPres = false; }
                    if (!canPres) continue;
                    try
                    {
                        def.AddFilter(new ScheduleFilter(fid, ScheduleFilterType.HasNoValue));
                        info = "фильтр HasNoValue";
                        return true;
                    }
                    catch { }
                }
                info = "нет подходящего поля для фильтра";
                return false;
            }
            catch (Exception ex) { info = "сбой: " + ex.Message; return false; }
        }

        /// <summary>Создаёт свободную спецификацию: копию образца (заголовки колонок), пустой
        /// список на новом листе со штампом образца и запись в хранилище правок.</summary>
        private static ScheduleSnapshotResult CreateFreeSchedule(UIApplication app, long templateScheduleId,
                                                                 string sheetNumber, string sheetName)
        {
            var res = new ScheduleSnapshotResult();
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null) { res.Error = "Нет активного документа Revit."; return res; }
            if (doc.IsFamilyDocument) { res.Error = "Свободные спецификации работают только в проектном документе (RVT)."; return res; }

            var template = doc.GetElement(new ElementId(templateScheduleId)) as ViewSchedule;
            if (template == null) { res.Error = "Спецификация-образец не найдена."; return res; }

            res.DocKey = GetDocKey(doc);
            var headers = GetScheduleHeaders(template);
            if (headers.Count == 0) { res.Error = "У образца не удалось прочитать заголовки колонок."; return res; }

            var titleBlockTypeId = GetTitleBlockTypeId(doc, template);
            if (titleBlockTypeId == ElementId.InvalidElementId)
            { res.Error = "На листе образца не найден штамп (основная надпись)."; return res; }

            long newScheduleId = 0, newSheetId = 0;
            string finalName = string.Empty;
            Transaction t = null;
            try
            {
                t = new Transaction(doc, "JTOOLS: свободная спецификация");
                t.Start();

                var dupId = template.Duplicate(ViewDuplicateOption.Duplicate);
                var free = doc.GetElement(dupId) as ViewSchedule;
                if (free == null) { t.RollBack(); t = null; res.Error = "Не удалось создать копию образца."; return res; }

                try
                {
                    finalName = MakeUniqueScheduleName(doc, (template.Name ?? "Спецификация") + " — свободная", free.Id);
                    free.Name = finalName;
                }
                catch (Exception ex)
                {
                    finalName = free.Name ?? "Свободная спецификация";
                    GrdLog.Log("CreateFreeSchedule: имя: " + ex.Message);
                }

                string info;
                bool emptied = TryEmptySchedule(doc, free, out info);
                GrdLog.Log("CreateFreeSchedule: опустошение «" + finalName + "»: " +
                           (emptied ? "да" : "нет") + " (" + info + ")");

                var sheet = ViewSheet.Create(doc, titleBlockTypeId);
                if (sheet == null) { t.RollBack(); t = null; res.Error = "Не удалось создать лист."; return res; }
                try { if (!string.IsNullOrWhiteSpace(sheetNumber)) sheet.SheetNumber = sheetNumber.Trim(); }
                catch (Exception ex) { GrdLog.Log("CreateFreeSchedule: номер листа: " + ex.Message); }
                try { if (!string.IsNullOrWhiteSpace(sheetName)) sheet.Name = sheetName.Trim(); }
                catch (Exception ex) { GrdLog.Log("CreateFreeSchedule: имя листа: " + ex.Message); }

                // Носитель ставим в тот же угол поля, к которому привязана печать:
                // 20 мм слева (брошюровка) + 1 мм отступ, 5 мм сверху + 1 мм отступ.
                var outline = sheet.Outline;
                var origin = new XYZ(outline.Min.U + 21.0 * MmFt, outline.Max.V - 6.0 * MmFt, 0);
                var inst = ScheduleSheetInstance.Create(doc, sheet.Id, free.Id, origin);
                if (inst == null) { t.RollBack(); t = null; res.Error = "Не удалось разместить спецификацию на листе."; return res; }

                t.Commit();
                t = null;

                newScheduleId = free.Id.Value;
                newSheetId = sheet.Id.Value;
            }
            catch (Exception ex)
            {
                GrdLog.Log("CreateFreeSchedule EXCEPTION: " + ex);
                try { t?.RollBack(); } catch { }
                res.Error = "Не удалось создать свободную спецификацию: " + ex.Message;
                return res;
            }

            try
            {
                var finalSchedule = doc.GetElement(new ElementId(newScheduleId)) as ViewSchedule;
                var entry = new SerializedSchedule
                {
                    ScheduleId = newScheduleId,
                    ScheduleName = finalSchedule?.Name ?? finalName,
                    IsFree = true,
                    TemplateScheduleId = templateScheduleId,
                    SheetId = newSheetId,
                    SegmentIndex = -1
                };
                entry.Headers.AddRange(headers);
                ScheduleSnapshotsFile.SaveEntry(res.DocKey, entry);
                res.NewScheduleId = newScheduleId;
                res.NewSheetId = newSheetId;
                GrdLog.Log("CreateFreeSchedule: создана «" + entry.ScheduleName + "» id=" + newScheduleId +
                           " лист=" + newSheetId + " образец=" + templateScheduleId);
            }
            catch (Exception ex)
            {
                GrdLog.Log("CreateFreeSchedule: сохранение записи EXCEPTION: " + ex.Message);
                res.Error = "Спецификация создана, но не сохранилась в хранилище: " + ex.Message;
            }
            return res;
        }

        /// <summary>Все спецификации, размещённые на листах активного документа.</summary>
        private static ScheduleSnapshotResult ReadList(UIApplication app)
        {
            var res = new ScheduleSnapshotResult();
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null)
            {
                res.Error = "Нет активного документа Revit.";
                return res;
            }
            if (doc.IsFamilyDocument)
            {
                res.Error = "Снимок спецификаций работает только в проектном документе (RVT).";
                return res;
            }
            res.DocKey = GetDocKey(doc);

            var items = new List<SheetScheduleInfo>();
            int viaInstances = 0;
            int viaSheets = 0;

            // Метод 1: специализированные элементы размещения спецификаций на листах
            // (ScheduleSheetInstance) — именно так Revit хранит спецификацию на листе.
            var seen = new HashSet<string>();
            foreach (ScheduleSheetInstance inst in new FilteredElementCollector(doc)
                         .OfClass(typeof(ScheduleSheetInstance)))
            {
                if (inst == null || inst.ScheduleId == ElementId.InvalidElementId) continue;
                if (!(doc.GetElement(inst.ScheduleId) is ViewSchedule schedule)) continue;
                var sheet = doc.GetElement(inst.OwnerViewId) as ViewSheet;
                if (sheet == null || sheet.IsPlaceholder) continue;
                var key = sheet.Id.Value + ":" + schedule.Id.Value;
                if (!seen.Add(key)) continue;
                viaInstances++;
                items.Add(new SheetScheduleInfo
                {
                    SheetId = sheet.Id.Value,
                    SheetNumber = sheet.SheetNumber ?? string.Empty,
                    SheetName = sheet.Name ?? string.Empty,
                    ScheduleId = schedule.Id.Value,
                    ScheduleName = schedule.Name ?? string.Empty
                });
            }

            // Метод 2 (резервный): листы -> GetAllViewports. Используется, только если метод 1
            // ничего не дал, чтобы перекрыть редкие случаи старых размещений через вьюпорты.
            if (items.Count == 0)
            {
                foreach (ViewSheet sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))
                {
                    if (sheet == null || sheet.IsPlaceholder) continue;
                    foreach (var vpId in sheet.GetAllViewports())
                    {
                        var vp = doc.GetElement(vpId) as Viewport;
                        if (vp == null) continue;
                        if (!(doc.GetElement(vp.ViewId) is ViewSchedule schedule)) continue;
                        viaSheets++;
                        items.Add(new SheetScheduleInfo
                        {
                            SheetId = sheet.Id.Value,
                            SheetNumber = sheet.SheetNumber ?? string.Empty,
                            SheetName = sheet.Name ?? string.Empty,
                            ScheduleId = schedule.Id.Value,
                            ScheduleName = schedule.Name ?? string.Empty
                        });
                    }
                }
            }

            // Метод 2 (резервный): листы -> GetAllViewports. Используется только если метод 1
            // ничего не дал, чтобы исключить расхождения в поведении GetAllViewports.
            if (items.Count == 0)
            {
                foreach (ViewSheet sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))
                {
                    if (sheet == null || sheet.IsPlaceholder) continue;
                    foreach (var vpId in sheet.GetAllViewports())
                    {
                        var vp = doc.GetElement(vpId) as Viewport;
                        if (vp == null) continue;
                        if (!(doc.GetElement(vp.ViewId) is ViewSchedule schedule)) continue;
                        viaSheets++;
                        items.Add(new SheetScheduleInfo
                        {
                            SheetId = sheet.Id.Value,
                            SheetNumber = sheet.SheetNumber ?? string.Empty,
                            SheetName = sheet.Name ?? string.Empty,
                            ScheduleId = schedule.Id.Value,
                            ScheduleName = schedule.Name ?? string.Empty
                        });
                    }
                }
            }

            // Пометка свободных спецификаций: их список хранится в правках окна.
            foreach (var it in items)
            {
                try
                {
                    var saved = ScheduleSnapshotsFile.LoadEntry(res.DocKey, it.ScheduleId, it.SheetId, -1);
                    if (saved != null && saved.IsFree) it.IsFree = true;
                }
                catch (Exception ex) { GrdLog.Log("ReadList: признак свободной: " + ex.Message); }
            }

            items.Sort((a, b) =>
            {
                var c = CompareSheetNumbers(a.SheetNumber, b.SheetNumber);
                return c != 0 ? c : string.Compare(a.ScheduleName, b.ScheduleName, StringComparison.CurrentCultureIgnoreCase);
            });
            res.Schedules = items;
            var totalSheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>().Count(s => !s.IsPlaceholder);
            var totalSchedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Count();
            var totalViewports = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Count();
            var totalInstances = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Count();

            // Детальная диагностика: что скрывается за вьюпортами и каково состояние спецификаций.
            var diag = new System.Text.StringBuilder();
            diag.Append("Views=");
            var byAssignments = new Dictionary<string, int>();
            foreach (Viewport vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)))
            {
                var v = vp.ViewId != ElementId.InvalidElementId ? doc.GetElement(vp.ViewId) : null;
                var cls = v == null ? "NOELEMENT" : v.GetType().Name;
                byAssignments[cls] = byAssignments.TryGetValue(cls, out var c) ? c + 1 : 1;
            }
            diag.Append(string.Join(";", byAssignments.Select(kv => kv.Key + "=" + kv.Value)));
            diag.Append(" | SampleSchedules=");
            foreach (ViewSchedule s in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Take(5))
            {
                var isTpl = s.IsTemplate;
                var pSheet = s.get_Parameter(BuiltInParameter.SHEET_NUMBER);
                var sheetVal = pSheet == null ? "no-param" : (pSheet.HasValue ? (pSheet.AsString() ?? "«" + pSheet.AsValueString() + "»") : "empty");
                diag.Append("{" + s.Name + ":" + s.GetType().Name + ":tpl=" + isTpl + ":sheet=" + sheetVal + "};");
            }
            GrdLog.Log("ScheduleSnapshotHandler: найдено=" + items.Count +
                       " (через ScheduleSheetInstance=" + viaInstances + ", через листы=" + viaSheets + ")," +
                       " листов=" + totalSheets + ", спецификаций=" + totalSchedules +
                       ", вьюпортов=" + totalViewports + ", ScheduleSheetInstance=" + totalInstances +
                       " | " + diag);
            if (items.Count == 0)
            {
                if (totalSheets == 0)
                    res.Error = "В проекте нет ни одного листа. Разместите спецификации на листах и повторите.";
                else if (totalSchedules == 0)
                    res.Error = "В проекте нет ни одной спецификации.";
                else if (totalViewports == 0)
                    res.Error = "В проекте " + totalSchedules + " спецификаци(й) и " + totalSheets + " листов, " +
                                "но в модели нет ни одного вьюпорта. Проверьте, что листы действительно содержат размещённые виды.";
                else
                    res.Error = "В проекте " + totalSchedules + " спецификаци(й), ни одна не привязана к листу " +
                                "(вьюпортов в документе: " + totalViewports + "). Разместите спецификацию на листе и повторите.";
            }
            return res;
        }

        /// <summary>Сравнение номеров листов: с учётом чисел («2» раньше «10»), иначе по алфавиту.</summary>
        private static int CompareSheetNumbers(string a, string b)
        {
            var pa = new System.Text.RegularExpressions.Regex(@"\d+").Matches(a ?? string.Empty);
            var pb = new System.Text.RegularExpressions.Regex(@"\d+").Matches(b ?? string.Empty);
            if (pa.Count > 0 && pb.Count > 0)
            {
                var ia = long.Parse(pa[0].Value);
                var ib = long.Parse(pb[0].Value);
                if (ia != ib) return ia.CompareTo(ib);
            }
            return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
        }

        // ------------------------------------------------------------------ ТАБЛИЦА

        /// <summary>Содержимое спецификации: штатный экспорт Revit в CSV -> сетка строк.
        /// Возвращает точную сетку Revit (группировки, итоги, пустые ячейки).</summary>
        /// <summary>Диапазон строк спецификации, отображаемых на листе для данного сегмента.
        /// Revit 2024 умеет делить спецификацию на сегменты (Split): один и тот же ViewSchedule
        /// размещается на нескольких листах, и на каждом показана лишь часть строк. Без учёта
        /// сегмента программа выводила всю спецификацию на каждом листе.
        /// Возвращает false, если спецификация не разделена или данные недоступны.</summary>
        private static bool TryGetSegmentRows(ViewSchedule schedule, int segmentIndex,
                                              out int start, out int count, out int totalRows, out string info)
        {
            start = 0; count = -1; totalRows = 0; info = string.Empty;
            try
            {
                if (schedule == null) { info = "schedule=null"; return false; }
                if (!schedule.IsSplit()) { info = "не разделена (IsSplit=false)"; return false; }
                int segCount = schedule.GetSegmentCount();
                if (segCount <= 1) { info = "сегментов=" + segCount; return false; }
                if (segmentIndex < 0 || segmentIndex >= segCount) { info = "SegmentIndex=" + segmentIndex + " вне 0.." + (segCount - 1); return false; }

                double headerH, titleH;
                var rowHeights = new List<double>();
                using (var h = schedule.GetScheduleHeightsOnSheet())
                {
                    headerH = h.ColumnHeaderHeight;
                    titleH = h.TitleHeight;
                    foreach (var x in h.GetBodyRowHeights()) rowHeights.Add(x);
                }
                totalRows = rowHeights.Count;
                if (totalRows == 0) { info = "нет высот строк (IsSplit=true)"; return false; }

                int r = 0;
                int segStart = 0, segEnd = totalRows;
                for (int i = 0; i < segCount; i++)
                {
                    int s0 = r;
                    if (i == segCount - 1)
                    {
                        r = totalRows;
                    }
                    else
                    {
                        double limit = schedule.GetSegmentHeight(i);
                        double avail = limit - headerH - (i == 0 ? titleH : 0.0);
                        double cum = 0;
                        while (r < totalRows && cum + rowHeights[r] <= avail + 1e-6)
                        {
                            cum += rowHeights[r];
                            r++;
                        }
                    }
                    if (i == segmentIndex) { segStart = s0; segEnd = r; }
                }

                start = segStart;
                count = segEnd - segStart;
                info = "сегмент " + (segmentIndex + 1) + "/" + segCount + ", строк " + count +
                       " из " + totalRows + ", шапка " + (headerH * 304.8).ToString("0.#") + " мм";
                return count > 0;
            }
            catch (Exception ex)
            {
                info = "сбой: " + ex.Message;
                return false;
            }
        }

        private static ScheduleSnapshotResult ReadTable(UIApplication app, long sheetId, long scheduleId)
        {
            var res = new ScheduleSnapshotResult();
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null)
            {
                res.Error = "Нет активного документа Revit.";
                return res;
            }
            res.DocKey = GetDocKey(doc);

            var schedule = doc.GetElement(new ElementId(scheduleId)) as ViewSchedule;
            if (schedule == null)
            {
                res.Error = "Спецификация не найдена в документе (возможно, удалена или несовместима).";
                return res;
            }

            var table = new ScheduleTable
            {
                ScheduleId = scheduleId,
                ScheduleName = schedule.Name ?? string.Empty,
                SheetId = sheetId,
                SegmentIndex = -1
            };

            // Габарит листа из модели (Outline — в футах; приводим к миллиметрам),
            // чтобы PDF печатался именно в размере листа, на котором размещена спецификация.
            try
            {
                var inst = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance))
                    .Cast<ScheduleSheetInstance>()
                    .FirstOrDefault(i => i.ScheduleId == schedule.Id);
                if (inst != null && doc.GetElement(inst.OwnerViewId) is ViewSheet sheet && sheet.Outline != null)
                {
                    var bb = sheet.Outline;
                    table.SheetWidthMm = (bb.Max.U - bb.Min.U) * 304.8;
                    table.SheetHeightMm = (bb.Max.V - bb.Min.V) * 304.8;
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("ScheduleSnapshotHandler: читать габарит листа EXCEPTION " + ex.Message);
            }

            // Свободная спецификация: содержимое берётся только из сохранённых правок,
            // модель (и её дубликат-носитель) игнорируется, сегменты не применяются.
            try
            {
                var saved = ScheduleSnapshotsFile.LoadEntry(res.DocKey, scheduleId, sheetId, -1);
                if (saved != null && saved.IsFree)
                {
                    table.IsFree = true;
                    table.TemplateScheduleId = saved.TemplateScheduleId;
                    table.Headers = new List<string>(saved.Headers);
                    if (table.Headers.Count == 0) table.Headers = GetScheduleHeaders(schedule);
                    foreach (var r in saved.Rows)
                    {
                        var row = new string[table.Headers.Count];
                        if (r != null)
                            for (int c = 0; c < row.Length && c < r.Length; c++)
                                row[c] = r[c] ?? string.Empty;
                        table.Rows.Add(row);
                    }
                    res.Table = table;
                    GrdLog.Log("ScheduleSnapshotHandler: прочитана свободная «" + table.ScheduleName +
                               "», колонок=" + table.Headers.Count + ", строк=" + table.Rows.Count);
                    return res;
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("ScheduleSnapshotHandler: свободная спецификация EXCEPTION " + ex.Message);
            }

            var tmpDir = Path.Combine(Path.GetTempPath(), "GrdRevitSnap_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            try
            {
                var options = new ViewScheduleExportOptions
                {
                    FieldDelimiter = ";",
                    ColumnHeaders = ExportColumnHeaders.OneRow,
                    TextQualifier = ExportTextQualifier.DoubleQuote,
                    Title = false,
                    // true — иначе экспорт пропускает строки-заголовки групп, итоги групп и пустые
                    // строки, а GetScheduleHeightsOnSheet().GetBodyRowHeights() их считает; из-за
                    // расхождения числа строк диапазоны сегментов не удавалось применить.
                    HeadersFootersBlanks = true
                };
                schedule.Export(tmpDir, "grd_snap", options);

                var files = Directory.GetFiles(tmpDir);
                if (files.Length == 0)
                    throw new InvalidOperationException("Revit не создал файл экспорта спецификации.");
                if (files.Length > 1)
                    table.Warning = "Спецификация шире одной части — показана первая (главная) часть. " +
                                    "Для печати используйте модель, либо сузьте спецификацию.";

                var rows = CsvReader.ParseFile(files[0], ';');
                if (rows.Count == 0)
                {
                    res.Error = "Спецификация «" + table.ScheduleName + "» пуста.";
                    return res;
                }

                table.Headers = rows[0].Select(h => h ?? string.Empty).ToList();
                for (int i = 1; i < rows.Count; i++)
                {
                    var row = new string[table.Headers.Count];
                    for (int c = 0; c < table.Headers.Count; c++)
                        row[c] = rows[i][c] ?? string.Empty;
                    table.Rows.Add(row);
                }

                // Учёт разбиения спецификации на сегменты/листы (Revit 2024 Split):
                // на каждом листе показана лишь часть строк, поэтому берём диапазон строк сегмента.
                try
                {
                    var instances = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance))
                        .Cast<ScheduleSheetInstance>()
                        .Where(i => i.ScheduleId == schedule.Id).ToList();

                    ScheduleSheetInstance inst = null;
                    if (sheetId > 0)
                        inst = instances.FirstOrDefault(i => i.OwnerViewId == new ElementId(sheetId));
                    if (inst == null)
                        inst = instances.FirstOrDefault();

                    int segIndex = inst != null ? inst.SegmentIndex : -1;
                    int segCountModel = schedule.GetSegmentCount();
                    table.SegmentIndex = segIndex;

                    var ranges = new List<string>();
                    for (int si = 0; si < segCountModel; si++)
                    {
                        int a, b, t; string di;
                        bool ok = TryGetSegmentRows(schedule, si, out a, out b, out t, out di);
                        ranges.Add(si + (ok ? ("[" + a + "+" + b + "/" + t + "]") : "?" + di));
                    }

                    var insts = new List<string>();
                    foreach (var ii in instances)
                    {
                        string num = null;
                        try { num = (doc.GetElement(ii.OwnerViewId) as ViewSheet)?.SheetNumber; }
                        catch { }
                        insts.Add((num ?? "?") + "#" + ii.SegmentIndex);
                    }

                    int segStart, segCount, totalModelRows; string segInfo;
                    bool applied = TryGetSegmentRows(schedule, segIndex, out segStart, out segCount, out totalModelRows, out segInfo) &&
                        totalModelRows == table.Rows.Count &&
                        segStart >= 0 && segCount > 0 && segStart + segCount <= table.Rows.Count;

                    GrdLog.Log("ScheduleSnapshotHandler: сегменты «" + table.ScheduleName + "»: IsSplit=" + schedule.IsSplit() +
                               ", сегментов=" + segCountModel +
                               ", экземпляров=" + instances.Count + " [" + string.Join(", ", insts.ToArray()) + "]" +
                               ", листSheetId=" + sheetId + " найден=" + (inst != null) +
                               ", SegmentIndex=" + segIndex +
                               ", CSV строк=" + table.Rows.Count +
                               ", диапазоны: " + string.Join(", ", ranges.ToArray()));

                    if (applied)
                    {
                        if (segStart > 0 || segCount < table.Rows.Count)
                        {
                            table.Rows = table.Rows.GetRange(segStart, segCount);
                            table.Warning = "Спецификация разделена на листы — показана часть: " + segInfo + ".";
                        }
                        GrdLog.Log("ScheduleSnapshotHandler: применено «" + table.ScheduleName + "»: " + segInfo);
                    }
                    else
                    {
                        GrdLog.Log("ScheduleSnapshotHandler: НЕ применено «" + table.ScheduleName + "»: " + segInfo);
                    }
                }
                catch (Exception ex)
                {
                    GrdLog.Log("ScheduleSnapshotHandler: сегменты EXCEPTION " + ex);
                }

                GrdLog.Log("ScheduleSnapshotHandler: прочитана «" + table.ScheduleName +
                           "», колонок=" + table.Headers.Count + ", строк=" + table.Rows.Count +
                           ", файлов экспорта=" + files.Length);
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
                catch (Exception ex) { GrdLog.Log("ScheduleSnapshotHandler: clean temp EXCEPTION " + ex); }
            }

            res.Table = table;
            return res;
        }
    }
}