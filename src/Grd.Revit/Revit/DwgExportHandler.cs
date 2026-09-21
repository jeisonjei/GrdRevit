using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace GrdRevit.Revit
{
    /// <summary>Элемент списка «Экспорт в DWG»: лист или вид, который можно отметить галочкой.</summary>
    public interface IDwgExportItem
    {
        long IdValue { get; }
        string FavScope { get; }
        bool IsChecked { get; set; }
        bool IsFavorite { get; set; }
        string Display { get; }
        string SearchKey { get; }
    }

    /// <summary>Вид (не лист) для экспорта в DWG.</summary>
    public sealed class ViewExportInfo : IDwgExportItem
    {
        public long ViewId;
        public string ViewName = string.Empty;
        public string TypeText = string.Empty;

        public long IdValue => ViewId;
        public string FavScope => "views";
        public bool IsChecked { get; set; }
        public bool IsFavorite { get; set; }

        public string Display =>
            string.IsNullOrWhiteSpace(TypeText) ? ViewName : ViewName + "  (" + TypeText + ")";

        public string SearchKey => (ViewName + " " + TypeText).ToLowerInvariant();
    }

    /// <summary>Список видов документа для окна «Экспорт в DWG».</summary>
    public sealed class ViewExportListResult
    {
        public string Error = string.Empty;
        public string DocKey = string.Empty;
        public List<ViewExportInfo> Views = new List<ViewExportInfo>();
    }

    /// <summary>Ход экспорта листов/видов в DWG: фаза и что делается сейчас, чем закончилось.</summary>
    public sealed class DwgExportProgress
    {
        public int Done;
        public int Total;
        public string Phase = string.Empty;
        public string Current = string.Empty;
        public bool Finished;
        public string Error = string.Empty;
        public string ResultPath = string.Empty;
    }

    /// <summary>
    /// Экспорт выбранных листов или видов в один DWG-файл (каждый элемент становится
    /// отдельным layout в файле, MergedViews). Моделесс-окно не может обращаться
    /// к API напрямую, поэтому запросы выполняются Revit-потоком через ExternalEvent.
    /// Чтение списков и экспорт выполняются за один заход в API-потоке.
    /// </summary>
    public class DwgExportHandler : IExternalEventHandler
    {
        private readonly object _sync = new object();
        private readonly Queue<Request> _requests = new Queue<Request>();

        private enum Kind { ReadSheets, ReadViews, ExportSheets, ExportViews }

        private sealed class Request
        {
            public Kind Kind;
            public List<long> Ids;
            public string TargetPath;
            public Action<SheetListResult> SheetListCallback;
            public Action<ViewExportListResult> ViewListCallback;
            public Action<DwgExportProgress> ExportCallback;
        }

        /// <summary>Список всех листов документа (кроме листов-заменителей).</summary>
        public void QueueReadSheets(Action<SheetListResult> callback)
        {
            lock (_sync)
                _requests.Enqueue(new Request { Kind = Kind.ReadSheets, SheetListCallback = callback });
        }

        /// <summary>Список всех видов, доступных для экспорта в DWG (исключая листы,
        /// спецификации, легенды и виды с шаблонами).</summary>
        public void QueueReadViews(Action<ViewExportListResult> callback)
        {
            lock (_sync)
                _requests.Enqueue(new Request { Kind = Kind.ReadViews, ViewListCallback = callback });
        }

        /// <summary>Экспорт выбранных листов в один DWG-файл.</summary>
        public void QueueExportSheets(List<long> sheetIds, string targetPath,
                                      Action<DwgExportProgress> callback)
        {
            lock (_sync)
                _requests.Enqueue(new Request
                {
                    Kind = Kind.ExportSheets,
                    Ids = sheetIds,
                    TargetPath = targetPath,
                    ExportCallback = callback
                });
        }

        /// <summary>Экспорт выбранных видов в один DWG-файл.</summary>
        public void QueueExportViews(List<long> viewIds, string targetPath,
                                     Action<DwgExportProgress> callback)
        {
            lock (_sync)
                _requests.Enqueue(new Request
                {
                    Kind = Kind.ExportViews,
                    Ids = viewIds,
                    TargetPath = targetPath,
                    ExportCallback = callback
                });
        }

        public string GetName()
        {
            return "JTOOLS: экспорт листов и видов в DWG";
        }

        public void Execute(UIApplication app)
        {
            List<Request> batch;
            lock (_sync)
            {
                if (_requests.Count == 0) return;
                batch = new List<Request>(_requests);
                _requests.Clear();
            }

            foreach (var req in batch)
            {
                try
                {
                    switch (req.Kind)
                    {
                        case Kind.ReadSheets:
                            req.SheetListCallback?.Invoke(ReadSheets(app));
                            break;
                        case Kind.ReadViews:
                            req.ViewListCallback?.Invoke(ReadViews(app));
                            break;
                        case Kind.ExportSheets:
                            req.ExportCallback?.Invoke(ExportElementIds(app, req.Ids, req.TargetPath, "Листы"));
                            break;
                        case Kind.ExportViews:
                            req.ExportCallback?.Invoke(ExportElementIds(app, req.Ids, req.TargetPath, "Виды"));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    GrdLog.Log("DwgExportHandler.Execute EXCEPTION: " + ex);
                    try
                    {
                        if (req.Kind == Kind.ReadSheets)
                            req.SheetListCallback?.Invoke(new SheetListResult { Error = "Ошибка чтения листов: " + ex.Message });
                        else if (req.Kind == Kind.ReadViews)
                            req.ViewListCallback?.Invoke(new ViewExportListResult { Error = "Ошибка чтения видов: " + ex.Message });
                        else
                            req.ExportCallback?.Invoke(Done("Ошибка экспорта: " + ex.Message));
                    }
                    catch { }
                }
            }
        }

        // ------------------------------------------------------------------ СПИСОК

        private static SheetListResult ReadSheets(UIApplication app)
        {
            var res = new SheetListResult();
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null) { res.Error = "Нет активного документа Revit."; return res; }
            if (doc.IsFamilyDocument)
            {
                res.Error = "Экспорт в DWG работает только в проектной модели (RVT).";
                return res;
            }
            res.DocKey = DocKeys.Get(doc);

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
            GrdLog.Log("DwgExportHandler.ReadSheets: листов=" + items.Count);
            return res;
        }

        private static ViewExportListResult ReadViews(UIApplication app)
        {
            var res = new ViewExportListResult();
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null) { res.Error = "Нет активного документа Revit."; return res; }
            if (doc.IsFamilyDocument)
            {
                res.Error = "Экспорт в DWG работает только в проектной модели (RVT).";
                return res;
            }
            res.DocKey = DocKeys.Get(doc);

            var items = new List<ViewExportInfo>();
            foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)))
            {
                if (!IsExportableView(v)) continue;
                items.Add(new ViewExportInfo
                {
                    ViewId = v.Id.Value,
                    ViewName = v.Name ?? string.Empty,
                    TypeText = ViewTypeText(v.ViewType)
                });
            }
            items.Sort((a, b) =>
            {
                int c = string.Compare(a.TypeText, b.TypeText, StringComparison.CurrentCultureIgnoreCase);
                return c != 0 ? c : string.Compare(a.ViewName, b.ViewName, StringComparison.CurrentCultureIgnoreCase);
            });
            res.Views = items;
            GrdLog.Log("DwgExportHandler.ReadViews: видов=" + items.Count);
            return res;
        }

        /// <summary>Графический вид, который Revit может выгрузить в DWG: планы,
        /// разрезы, фасады, 3D и ручные виды. Спецификации, легенды, листы,
        /// шаблоны и служебные виды исключаются.</summary>
        private static bool IsExportableView(View v)
        {
            if (v == null || v.IsTemplate) return false;
            if (v is ViewSheet) return false;
            switch (v.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.AreaPlan:
                case ViewType.EngineeringPlan:
                case ViewType.Elevation:
                case ViewType.ThreeD:
                case ViewType.DraftingView:
                case ViewType.Section:
                    return true;
                default:
                    return false;
            }
        }

        private static string ViewTypeText(ViewType t)
        {
            switch (t)
            {
                case ViewType.FloorPlan: return "План";
                case ViewType.CeilingPlan: return "План потолка";
                case ViewType.AreaPlan: return "План площади";
                case ViewType.EngineeringPlan: return "Инженерный план";
                case ViewType.Elevation: return "Фасад";
                case ViewType.ThreeD: return "3D";
                case ViewType.DraftingView: return "Вид";
                case ViewType.Section: return "Разрез";
                default: return "Вид";
            }
        }

        // ------------------------------------------------------------------ ЭКСПОРТ

        /// <summary>Экспорт отмеченных листов или видов в один DWG: MergedViews=true
        /// объединяет все элементы в файл, где каждый лист/вид располагается в отдельном
        /// layout. Списки листов и видов проходят одним и тем же путём.</summary>
        private static DwgExportProgress ExportElementIds(UIApplication app, List<long> ids, string targetPath, string defaultBaseName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(targetPath)) return Done("Не задан путь для DWG.");
                if (ids == null || ids.Count == 0) return Done("Ничего не выбрано для экспорта.");

                var doc = app?.ActiveUIDocument?.Document;
                if (doc == null) return Done("Нет активного документа Revit.");

                var views = new List<View>();
                foreach (var id in ids)
                {
                    var v = doc.GetElement(new ElementId(id)) as View;
                    if (v == null) continue;
                    var sheet = v as ViewSheet;
                    if (sheet != null && sheet.IsPlaceholder) continue;
                    views.Add(v);
                }
                if (views.Count == 0) return Done("Выбранные листы/виды не найдены в документе.");

                var targetFull = Path.GetFullPath(targetPath);
                var dir = Path.GetDirectoryName(targetFull);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    try { Directory.CreateDirectory(dir ?? Path.GetTempPath()); } catch { }
                }

                // Экспортируем во временную папку, находим созданный файл и переносим
                // в целевой путь (имена файлов, выдаваемые Revit, зависят от версии).
                var tmp = Path.Combine(Path.GetTempPath(), "GrdRevitDwg_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmp);
                try
                {
                    var baseName = Path.GetFileNameWithoutExtension(targetFull);
                    if (string.IsNullOrEmpty(baseName)) baseName = defaultBaseName;

                    var before = new HashSet<string>(
                        Directory.GetFiles(tmp, "*.dwg").Select(Path.GetFullPath),
                        StringComparer.OrdinalIgnoreCase);

                    var options = new DWGExportOptions
                    {
                        MergedViews = true,
                        FileVersion = ACADVersion.R2018,
                        Colors = ExportColorMode.TrueColor,
                        TextTreatment = TextTreatment.Exact,
                        HideScopeBox = true,
                        HideReferencePlane = true
                    };
                    doc.Export(tmp, baseName, views.Select(v => v.Id).ToList(), options);

                    var produced = Directory.GetFiles(tmp, "*.dwg").Select(Path.GetFullPath)
                        .FirstOrDefault(f => !before.Contains(f));
                    if (string.IsNullOrEmpty(produced))
                        return new DwgExportProgress
                        {
                            Done = 0,
                            Total = views.Count,
                            Finished = true,
                            Error = "Revit не создал DWG-файл. Попробуйте повторить экспорт."
                        };

                    try { if (File.Exists(targetFull)) File.Delete(targetFull); } catch { }
                    File.Move(produced, targetFull);

                    GrdLog.Log("DwgExportHandler: элементов=" + views.Count + " -> " + targetFull);
                    return new DwgExportProgress
                    {
                        Done = views.Count,
                        Total = views.Count,
                        Finished = true,
                        ResultPath = targetFull
                    };
                }
                finally
                {
                    try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("DwgExportHandler.ExportElementIds EXCEPTION: " + ex);
                return Done("Ошибка экспорта DWG: " + ex.Message);
            }
        }

        private static DwgExportProgress Done(string error)
        {
            return new DwgExportProgress { Finished = true, Error = error ?? string.Empty };
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