using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GrdRevit.Core;

namespace GrdRevit.Revit
{
    /// <summary>Итог проверки соответствия типа элемента Revit марке DXF.</summary>
    public enum TypeCheckState
    {
        Match,
        Mismatch,
        Unknown
    }

    /// <summary>Результат проверки типа для одного прибора.</summary>
    public sealed class TypeCheckResult
    {
        public DxfCalloutItem Heater;
        public ElementId ElementId;
        public TypeCheckState State;
        public string Expected;   // ожидаемые «Семейство :: Тип» из карты марок
        public string Actual;     // фактические «Семейство :: Тип» элемента
        public string Reason;     // пояснение (для Unknown и Mismatch)

        public string ExpectedFamily;
        public string ExpectedType;
        public string ActualFamily;
        public string ActualType;

        /// <summary>Семейство элемента не совпало с ожидаемым (из карты марок).</summary>
        public bool FamilyMismatch;
        /// <summary>Тип элемента не совпал с ожидаемым (при совпадающем/не заданном семействе).</summary>
        public bool TypeMismatch;
    }

    /// <summary>Результат синхронизации выбора: элементы текущего вида, найденные по приборам плана.</summary>
    public sealed class SelectInRevitResult
    {
        public bool Aligned;                       // координаты удалось совместить
        public int Count;                          // сколько элементов выделено
        public List<ElementId> Ids = new List<ElementId>();
        public string Summary = string.Empty;
        public List<TypeCheckResult> Checks = new List<TypeCheckResult>();
    }

    /// <summary>
    /// «Выделить в Revit»: по приборам, выбранным на DXF-плане, находит элементы механического
    /// оборудования текущего вида и выделяет их. Используется то же совмещение координат, что и в
    /// «Применить к текущему виду» (PlanAligner: плановые точки — приборы DXF, целевые — элементы вида).
    /// Совмещение и список кандидатов кэшируются до смены документа/вида/плана.
    /// </summary>
    public class SelectInRevitHandler : IExternalEventHandler
    {
        private const double AlignToleranceMm = 1500.0;
        private const int MinInliers = 3;
        private const double MatchToleranceMm = 2000.0;

        private static readonly BuiltInCategory[] DeviceCategories =
        {
            BuiltInCategory.OST_MechanicalEquipment,
            BuiltInCategory.OST_SpecialityEquipment,
            BuiltInCategory.OST_PlumbingFixtures
        };

        private readonly object _sync = new object();
        private List<DxfCalloutItem> _planHeaters;
        private List<DxfCalloutItem> _heaters;
        private GrdSettings _settings;
        private Action<SelectInRevitResult> _callback;

        // Кэш совмещения: действителен только для конкретного документа, вида и плана.
        private List<DxfCalloutItem> _cachePlanHeaters;
        private int _cacheDocKey;
        private ElementId _cacheViewId;
        private PlanAlignResult _align;
        private List<DeviceCandidate> _candidates;

        public void Queue(List<DxfCalloutItem> planHeaters, List<DxfCalloutItem> heaters,
            GrdSettings settings, Action<SelectInRevitResult> callback)
        {
            lock (_sync)
            {
                _planHeaters = planHeaters;
                _heaters = heaters;
                _settings = settings;
                _callback = callback;
            }
        }

        public string GetName()
        {
            return "JTOOLS: выделить приборы DXF в Revit";
        }

        public void Execute(UIApplication app)
        {
            List<DxfCalloutItem> planHeaters, heaters;
            GrdSettings settings;
            Action<SelectInRevitResult> cb;
            lock (_sync)
            {
                planHeaters = _planHeaters;
                heaters = _heaters;
                settings = _settings;
                cb = _callback;
                _planHeaters = null;
                _heaters = null;
                _settings = null;
                _callback = null;
            }

            var result = new SelectInRevitResult();
            try
            {
                result = Run(app, planHeaters, heaters, settings);
            }
            catch (Exception ex)
            {
                GrdLog.Log("SelectInRevitHandler.Execute: EXCEPTION: " + ex);
                result = new SelectInRevitResult
                {
                    Summary = "! Ошибка выделения в Revit: " + ex.Message
                };
            }

            try
            {
                cb?.Invoke(result);
            }
            catch (Exception ex)
            {
                GrdLog.Log("SelectInRevitHandler.Execute: колбэк завершения упал: " + ex);
            }
        }

        private SelectInRevitResult Run(UIApplication app, List<DxfCalloutItem> planHeaters,
            List<DxfCalloutItem> heaters, GrdSettings settings)
        {
            var result = new SelectInRevitResult();
            var uiDoc = app?.ActiveUIDocument;
            if (uiDoc == null)
            {
                result.Summary = "Нет активного документа.";
                return result;
            }
            var doc = uiDoc.Document;
            var view = uiDoc.ActiveGraphicalView;
            if (view == null)
            {
                result.Summary = "Нет активного вида.";
                return result;
            }

            int docKey = doc.GetHashCode();
            // Если в кэше кандидатов есть удалённые элементы (замена семейства удаляет старый
            // экземпляр и создаёт новый id) — сбрасываем совмещение, чтобы пересобрать список
            // по текущему виду и не давать проверке «мёртвый» id.
            if (_candidates != null && _candidates.Count > 0)
            {
                bool stale = false;
                for (int i = _candidates.Count - 1; i >= 0; i--)
                {
                    if (doc.GetElement(_candidates[i].Id) == null)
                    {
                        stale = true;
                        _candidates.RemoveAt(i);
                    }
                }
                if (stale)
                {
                    GrdLog.Log("SelectInRevitHandler.Run: обнаружены удалённые кандидаты — пересобираю элементы вида.");
                    _candidates = null;
                    _align = null;
                }
            }

            if (_align == null || _candidates == null ||
                !ReferenceEquals(_cachePlanHeaters, planHeaters) ||
                _cacheDocKey != docKey || _cacheViewId != view.Id)
            {
                if (planHeaters == null || planHeaters.Count == 0)
                {
                    result.Summary = "План не содержит приборов.";
                    return result;
                }

                _candidates = CollectDevices(doc, view);
                if (_candidates.Count == 0)
                {
                    _align = null;
                    result.Summary = "На текущем виде нет элементов механического оборудования.";
                    return result;
                }

                var dxfPts = planHeaters.Select(h => new PlanPoint(h.X, h.Y)).ToList();
                var revitPts = _candidates.Select(c => new PlanPoint(c.Xmm, c.Ymm)).ToList();
                // Совмещение ориентируется на выбранные приборы, как в «Применить к текущему виду».
                var query = heaters != null && heaters.Count > 0
                    ? heaters.Select(h => new PlanPoint(h.X, h.Y)).ToList()
                    : null;
                var align = PlanAligner.Estimate(dxfPts, revitPts, AlignToleranceMm, MinInliers, query);
                if (!align.IsValid)
                {
                    _align = null;
                    result.Summary = "Не удалось совместить план с текущим видом: " +
                                     "проверьте, что на виде видны те же приборы, что и на плане.";
                    return result;
                }

                _align = align;
                _cachePlanHeaters = planHeaters;
                _cacheDocKey = docKey;
                _cacheViewId = view.Id;
            }

            if (!_align.IsValid)
            {
                result.Summary = "План не совмещён с текущим видом.";
                return result;
            }
            result.Aligned = true;

            if (heaters == null || heaters.Count == 0)
            {
                uiDoc.Selection.SetElementIds(new List<ElementId>());
                result.Summary = "Выбор на плане снят — выделение в Revit очищено.";
                return result;
            }

            var used = new HashSet<ElementId>();
            var ids = new List<ElementId>();
            foreach (var heater in heaters)
            {
                double mx = _align.MapX(heater.X);
                double my = _align.MapY(heater.Y);
                var match = FindMatch(_candidates, mx, my, used, MatchToleranceMm);
                if (match != null)
                {
                    used.Add(match.Id);
                    ids.Add(match.Id);
                    var chk = CheckType(doc, match.Id, heater, settings);
                    if (chk != null) result.Checks.Add(chk);
                }
            }

            try
            {
                uiDoc.Selection.SetElementIds(ids);
            }
            catch (Exception ex)
            {
                GrdLog.Log("SelectInRevitHandler.SetElementIds: EXCEPTION: " + ex);
            }

            result.Ids = ids;
            result.Count = ids.Count;
            result.Summary = ids.Count == 0
                ? "Рядом с выбранными приборами нет элементов на текущем виде."
                : "Выделено в Revit: " + ids.Count + ".";
            GrdLog.Log("SelectInRevitHandler: " + result.Summary);
            return result;
        }

        /// <summary>
        /// Сверяет семейство/тип найденного элемента с ожидаемыми для марки DXF из карты типов
        /// («Марки DXF → семейства»). Без записи в карте тип не проверить — результат Unknown.
        /// </summary>
        private static TypeCheckResult CheckType(Document doc, ElementId id, DxfCalloutItem heater, GrdSettings settings)
        {
            var res = new TypeCheckResult { Heater = heater, ElementId = id };

            string mark = heater.Device?.Mark;
            if (string.IsNullOrWhiteSpace(mark))
            {
                res.State = TypeCheckState.Unknown;
                res.Reason = "марка не назначена (выноска не найдена)";
                return res;
            }

            var entry = FamilyTypeResolver.GetMarkTypeEntry(heater.Device, settings);
            if (entry == null || entry.IsEmpty)
            {
                res.State = TypeCheckState.Unknown;
                res.Reason = "марка \u201C" + mark + "\u201D не настроена в карте типов";
                return res;
            }

            string actualFamily = string.Empty, actualType = string.Empty;
            var el = doc.GetElement(id);
            if (el == null)
            {
                res.State = TypeCheckState.Unknown;
                res.Reason = "элемент не найден (удалён или заменён)";
                GrdLog.Log("SelectInRevitHandler.CheckType: id=" + id + " отсутствует в документе.");
                return res;
            }
            if (el is FamilyInstance fi && fi.Symbol != null && fi.Symbol.Family != null)
            {
                actualFamily = fi.Symbol.Family.Name ?? string.Empty;
                actualType = fi.Symbol.Name ?? string.Empty;
            }
            res.Actual = string.IsNullOrEmpty(actualFamily)
                ? string.Empty
                : actualFamily + " :: " + actualType;
            res.ActualFamily = actualFamily;
            res.ActualType = actualType;

            var expectedFamily = entry.Family?.Trim() ?? string.Empty;
            var expectedType = entry.Type?.Trim() ?? string.Empty;
            res.Expected = string.IsNullOrEmpty(expectedFamily)
                ? expectedType
                : expectedFamily + (string.IsNullOrEmpty(expectedType) ? string.Empty : " :: " + expectedType);
            res.ExpectedFamily = expectedFamily;
            res.ExpectedType = expectedType;

            bool familyOk = string.IsNullOrEmpty(expectedFamily) ||
                            string.Equals(expectedFamily, actualFamily, StringComparison.OrdinalIgnoreCase);
            bool typeOk = string.IsNullOrEmpty(expectedType) ||
                          string.Equals(expectedType, actualType, StringComparison.OrdinalIgnoreCase);

            res.FamilyMismatch = !familyOk;
            res.TypeMismatch = !typeOk;

            res.State = familyOk && typeOk ? TypeCheckState.Match : TypeCheckState.Mismatch;
            if (res.State == TypeCheckState.Mismatch)
            {
                if (res.FamilyMismatch)
                    res.Reason = string.IsNullOrEmpty(res.ExpectedFamily) ? "семейство не задано" : "семейство не совпадает";
                else if (res.TypeMismatch)
                    res.Reason = string.IsNullOrEmpty(res.ExpectedType) ? "тип не задан" : "тип не совпадает";
                else
                    res.Reason = "свойства не совпадают";
            }

            GrdLog.Log("SelectInRevitHandler.CheckType: id=" + id + ", марка=\u201C" + mark + "\u201D, " +
                       "ожидаемое=\u201C" + res.Expected + "\u201D, фактическое=\u201C" + res.Actual + "\u201D, " +
                       "вердикт=" + res.State + (string.IsNullOrEmpty(res.Reason) ? string.Empty : " (" + res.Reason + ")"));
            return res;
        }

        /// <summary>Элементы вида, которые могут быть приборами: экземпляры семейств с точкой размещения.</summary>
        private static List<DeviceCandidate> CollectDevices(Document doc, View view)
        {
            var list = new List<DeviceCandidate>();
            var cats = new HashSet<ElementId>(DeviceCategories.Select(c => new ElementId((int)c)));

            var instances = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.Category != null && fi.Category.Id != null && cats.Contains(fi.Category.Id));

            foreach (var fi in instances)
            {
                if (!(fi.Location is LocationPoint lp)) continue;
                list.Add(new DeviceCandidate
                {
                    Id = fi.Id,
                    Xmm = lp.Point.X * PlanAligner.MmPerFoot,
                    Ymm = lp.Point.Y * PlanAligner.MmPerFoot
                });
            }
            return list;
        }

        private static DeviceCandidate FindMatch(List<DeviceCandidate> candidates,
            double mx, double my, HashSet<ElementId> used, double maxDistMm)
        {
            double best = maxDistMm;
            DeviceCandidate bestC = null;
            foreach (var c in candidates)
            {
                if (used.Contains(c.Id)) continue;
                double dx = c.Xmm - mx;
                double dy = c.Ymm - my;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d <= best)
                {
                    best = d;
                    bestC = c;
                }
            }
            return bestC;
        }

        private sealed class DeviceCandidate
        {
            public ElementId Id;
            public double Xmm;
            public double Ymm;
        }
    }
}