using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GrdRevit.Core;

namespace GrdRevit.Revit
{
    /// <summary>Результат применения приборов из выбранной области DXF к текущему виду.</summary>
    public sealed class ApplyToViewResult
    {
        public bool Aligned;                       // координаты удалось совместить
        public int Applied;                        // сколько приборов применено
        public List<DxfCalloutItem> Failures = new List<DxfCalloutItem>();
        public List<string> FailureReasons = new List<string>();
        public string Summary = string.Empty;
    }

    /// <summary>
    /// «Применить к текущему виду»: приборы, выбранные рамкой на DXF-плане, применяются
    /// к элементам механического оборудования текущего вида, найденным по координатам.
    ///
    /// Считаем, что масштаб и координаты приборов в DXF (мм) совпадают с координатами
    /// модели Revit (мм) — ищем только перенос между множествами точек (PlanAligner),
    /// затем для каждого выбранного прибора подбираем ближайший элемент вида. Приборы,
    /// которые не удалось применить (нет рядом элемента / не применён тип), возвращаются
    /// списком для подсветки на плане.
    /// </summary>
    public class ApplyToViewHandler : IExternalEventHandler
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
        private List<DxfCalloutItem> _heaters;
        private List<DxfCalloutItem> _planHeaters;
        private GrdSettings _settings;
        private Action<ApplyToViewResult> _callback;

        public void Queue(List<DxfCalloutItem> heaters, List<DxfCalloutItem> planHeaters,
            GrdSettings settings, Action<ApplyToViewResult> callback)
        {
            lock (_sync)
            {
                _heaters = heaters;
                _planHeaters = planHeaters;
                _settings = settings;
                _callback = callback;
            }
        }

        public string GetName()
        {
            return "JTOOLS: применить приборы DXF к текущему виду";
        }

        public void Execute(UIApplication app)
        {
            List<DxfCalloutItem> heaters;
            List<DxfCalloutItem> planHeaters;
            GrdSettings settings;
            Action<ApplyToViewResult> cb;
            lock (_sync)
            {
                heaters = _heaters;
                planHeaters = _planHeaters;
                settings = _settings;
                cb = _callback;
                _heaters = null;
                _planHeaters = null;
                _settings = null;
                _callback = null;
            }

            if (cb == null)
            {
                GrdLog.Log("ApplyToViewHandler.Execute: нет запроса в очереди");
                return;
            }

            var result = new ApplyToViewResult();
            try
            {
                result = Run(app, heaters, planHeaters, settings);
            }
            catch (Exception ex)
            {
                GrdLog.Log("ApplyToViewHandler.Execute: EXCEPTION: " + ex);
                result = new ApplyToViewResult
                {
                    Summary = "! Ошибка при применении к текущему виду: " + ex.Message
                };
            }

            try
            {
                cb(result);
            }
            catch (Exception ex)
            {
                GrdLog.Log("ApplyToViewHandler.Execute: колбэк завершения упал: " + ex);
            }
        }

        private static ApplyToViewResult Run(UIApplication app, List<DxfCalloutItem> heaters,
            List<DxfCalloutItem> planHeaters, GrdSettings settings)
        {
            var result = new ApplyToViewResult();
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

            var candidates = CollectDevices(doc, view);
            if (candidates.Count == 0)
            {
                result.Summary = "На текущем виде нет элементов механического оборудования " +
                                 "(категории «Механическое оборудование», «Специальное оборудование», «Санитарные приборы»).";
                return result;
            }

            if (planHeaters == null || planHeaters.Count == 0)
            {
                result.Summary = "План не содержит приборов.";
                return result;
            }

            var dxfPts = planHeaters.Select(h => new PlanPoint(h.X, h.Y)).ToList();
            var revitPts = candidates.Select(c => new PlanPoint(c.Xmm, c.Ymm)).ToList();
            // Совмещение ориентируем именно на выбранные приборы: на многоэтажном плане
            // гистограмма по всем этажам может отдать сдвиг другого этажа.
            var selectedPts = heaters != null && heaters.Count > 0
                ? heaters.Select(h => new PlanPoint(h.X, h.Y)).ToList()
                : null;
            var align = PlanAligner.Estimate(dxfPts, revitPts, AlignToleranceMm, MinInliers, selectedPts);
            if (!align.IsValid)
            {
                result.Summary = "Не удалось совместить координаты плана с текущим видом. " +
                                 "Проверьте, что на виде видны те же приборы, что и на плане.";
                return result;
            }
            result.Aligned = true;

            LogDiagnostics(candidates, planHeaters, align, heaters);

            if (heaters == null || heaters.Count == 0)
            {
                result.Summary = "Нет выбранных приборов.";
                return result;
            }

            var used = new HashSet<ElementId>();
            int noMarkMap = 0;
            foreach (var heater in heaters)
            {
                if (heater.Device == null)
                {
                    result.Failures.Add(heater);
                    result.FailureReasons.Add("нет марки из каталога");
                    continue;
                }

                // Массовое применение назначает ТОЛЬКО семейства из карты типов («Марки DXF → семейства»):
                // без «угадывания» по мощности/подсказке, чтобы не подставить случайный тип.
                if (!FamilyTypeResolver.HasMarkTypeEntry(heater.Device, settings))
                {
                    result.Failures.Add(heater);
                    result.FailureReasons.Add("марка «" + heater.Device.Mark +
                                              "» отсутствует в карте типов — укажите семейство в окне «Марки DXF»");
                    noMarkMap++;
                    continue;
                }

                double mx = align.MapX(heater.X);
                double my = align.MapY(heater.Y);

                var match = FindMatch(candidates, mx, my, used, MatchToleranceMm);
                if (match == null)
                {
                    result.Failures.Add(heater);
                    result.FailureReasons.Add("рядом с координатами прибора нет элемента на виде");
                    continue;
                }

                used.Add(match.Id);
                var apply = ApplyService.Apply(app, match.Id, heater.Device, settings);
                if (IsFailure(apply))
                {
                    result.Failures.Add(heater);
                    result.FailureReasons.Add(FirstLine(apply));
                }
                else
                {
                    result.Applied++;
                }
            }

            result.Summary = result.Failures.Count == 0
                ? $"Применено к текущему виду: {result.Applied} из {heaters.Count}."
                : $"Применено: {result.Applied} из {heaters.Count}. Не применено: {result.Failures.Count} " +
                  (noMarkMap > 0 ? "(из них " + noMarkMap + " без марки в карте типов). " : ". ") +
                  "Подсвечено на плане, снять — «Очистить предупреждения».";

            GrdLog.Log("ApplyToViewHandler: " + result.Summary);
            return result;
        }

        private static void LogDiagnostics(List<DeviceCandidate> candidates,
            List<DxfCalloutItem> planHeaters, PlanAlignResult align, List<DxfCalloutItem> heaters)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("ApplyToViewHandler align: offset=(")
                    .Append(align.OffsetX.ToString("F0"))
                    .Append(", ").Append(align.OffsetY.ToString("F0"))
                    .Append(") inliers=").Append(align.Inliers)
                    .Append(" candidates=").Append(candidates.Count)
                    .Append(" planHeaters=").Append(planHeaters.Count);

                if (align.OffsetX == 0 && align.OffsetY == 0)
                    sb.Append(" [ВНИМАНИЕ: нулевой сдвиг!]");

                if (candidates.Count > 0 && heaters != null && heaters.Count > 0)
                {
                    double minX = candidates.Min(c => c.Xmm), maxX = candidates.Max(c => c.Xmm);
                    double minY = candidates.Min(c => c.Ymm), maxY = candidates.Max(c => c.Ymm);
                    sb.AppendFormat(" candRange=({0:F0}..{1:F0}, {2:F0}..{3:F0})", minX, maxX, minY, maxY);

                    var h0 = heaters[0];
                    double mx = align.MapX(h0.X), my = align.MapY(h0.Y);
                    double d0 = candidates.Min(c => Math.Sqrt((c.Xmm - mx) * (c.Xmm - mx) + (c.Ymm - my) * (c.Ymm - my)));
                    sb.AppendFormat(" firstHeater({0:F0},{1:F0})->({2:F0},{3:F0}) nearest={4:F0}mm",
                        h0.X, h0.Y, mx, my, d0);
                }
                GrdLog.Log(sb.ToString());
            }
            catch { }
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

        private static bool IsFailure(string r)
        {
            if (string.IsNullOrEmpty(r)) return false;
            return r.StartsWith("!") || r.StartsWith("Не удалось") || r.StartsWith("Выберите")
                   || r.StartsWith("Нет активного") || r.StartsWith("Карта значений не настроена")
                   || r.StartsWith("Тип не применён")
                   || r.IndexOf("не найдено в документе", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FirstLine(string s)
        {
            var i = s.IndexOf('\n');
            return i < 0 ? s : s.Substring(0, i).Trim();
        }

        private sealed class DeviceCandidate
        {
            public ElementId Id;
            public double Xmm;
            public double Ymm;
        }
    }
}