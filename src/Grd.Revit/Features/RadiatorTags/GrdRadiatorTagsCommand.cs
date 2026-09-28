using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace GrdRevit
{
    /// <summary>
    /// «Расставить тэги по границе»: выравнивает выбранные тэги (обычно радиаторы по периметру
    /// плана) вдоль начерченной пользователем линии-границы.
    ///
    /// Взаимодействие: 1) заранее выделите тэги на виде; 2) запустите команду;
    /// 3) начертите линию-границу двумя кликами; 4) кликните с той стороны линии,
    /// куда сдвинуть тэги. Тэги укладываются рядами параллельно границе: каждый тэг
    /// стремится встать напротив своего якоря (прямая выноска), при нехватке места
    /// по длине границы — во внутренние ряды-колонки. Позиция тэга внутри ряда
    /// подбирается так, чтобы прямая выноска к якорю не пересекала чужие тексты.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdRadiatorTagsCommand : IExternalCommand
    {
        // ===== Настройка =====
        private const double GapFt = 3.0 / 304.8;          // зазор между текстами в ряду и от границы
        private const double RowGapFt = 3.0 / 304.8;       // интервал между рядами
        private const double W_LEADER_HIT = 1000.0;        // штраф за выноску по чужому тексту
        private const double W_SHIFT = 60.0;               // штраф за 1 фут смещения от проекции якоря
        private const int OffsetSamples = 72;              // сколько позиций пробуется внутри ряда
        private const int MaxRows = 14;                    // предельная глубина колонок
        private const double MinLineLenFt = 0.01;          // минимальная длина начерченной границы

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                RevitContext.Initialize(commandData.Application);

                var uidoc = commandData.Application.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null)
                {
                    GrdLog.Log("GrdRadiatorTagsCommand: нет активного документа");
                    return Result.Cancelled;
                }
                var view = uidoc.ActiveGraphicalView;
                if (view == null || view is View3D || view is ViewSchedule)
                {
                    GrdLog.Log("GrdRadiatorTagsCommand: нужен 2D-вид (план/разрез/фасад), текущий: " +
                               (view?.GetType().Name ?? "нет"));
                    return Result.Cancelled;
                }

                var origin = view.Origin;
                var right = view.RightDirection;
                var up = view.UpDirection;
                Func<XYZ, XYZ> to2d = p =>
                {
                    var d = p - origin;
                    return new XYZ(d.DotProduct(right), d.DotProduct(up), 0.0);
                };
                Func<XYZ, XYZ> to3d = p => origin + p.X * right + p.Y * up;

                // 1) Собрать тэги: выделенные заранее, иначе — все на виде.
                var selectedIds = new HashSet<ElementId>();
                try
                {
                    foreach (var id in uidoc.Selection.GetElementIds())
                        if (doc.GetElement(id) is IndependentTag) selectedIds.Add(id);
                }
                catch { }

                var collector = new FilteredElementCollector(doc, view.Id)
                    .WhereElementIsNotElementType()
                    .OfClass(typeof(IndependentTag));

                var items = new List<TagItem>();
                foreach (IndependentTag tag in collector)
                {
                    if (tag.Pinned) continue;
                    if (selectedIds.Count > 0 && !selectedIds.Contains(tag.Id)) continue;
                    var t = BuildItem(tag, view, to2d);
                    if (t != null) items.Add(t);
                }

                if (items.Count < 1)
                {
                    GrdLog.Log("GrdRadiatorTagsCommand: тэги не найдены — выделите их на виде заранее");
                    TaskDialog.Show("JTOOLS: Расставить тэги по границе",
                                    "Тэги не найдены. Выделите нужные тэги на виде, затем запустите команду.");
                    return Result.Cancelled;
                }

                // 2) Начертить линию-границу (два клика).
                XYZ p0, p1;
                ElementId tempAxisId = ElementId.InvalidElementId;
                try
                {
                    p0 = to2d(uidoc.Selection.PickPoint(
                        ObjectSnapTypes.Endpoints | ObjectSnapTypes.Points | ObjectSnapTypes.Intersections,
                        "JTOOLS: тэги по границе — клик 1 (начало линии-границы)"));
                    p1 = to2d(uidoc.Selection.PickPoint(
                        ObjectSnapTypes.Endpoints | ObjectSnapTypes.Points | ObjectSnapTypes.Intersections,
                        "JTOOLS: тэги по границе — клик 2 (конец линии-границы)"));

                    // Показать начерченную ось на виде до выбора стороны.
                    double tmpLen = Distance2(p0, p1);
                    if (tmpLen > MinLineLenFt)
                    {
                        try
                        {
                            using (var tt = new Transaction(doc, "JTOOLS: превью оси"))
                            {
                                tt.Start();
                                var axisLine = doc.Create.NewDetailCurve(
                                    view, Line.CreateBound(to3d(p0), to3d(p1)));
                                if (axisLine != null) tempAxisId = axisLine.Id;
                                tt.Commit();
                            }
                        }
                        catch (Exception ex)
                        {
                            GrdLog.Log("GrdRadiatorTagsCommand: не создана линия-ось: " + ex.Message);
                        }
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    GrdLog.Log("GrdRadiatorTagsCommand: пользователь отменил черчение границы");
                    return Result.Cancelled;
                }

                double len = Distance2(p0, p1);
                if (len < MinLineLenFt)
                {
                    GrdLog.Log("GrdRadiatorTagsCommand: линия-граница слишком короткая");
                    return Result.Cancelled;
                }
                var u = new XYZ((p1.X - p0.X) / len, (p1.Y - p0.Y) / len, 0.0);
                var n = new XYZ(-u.Y, u.X, 0.0); // перпендикуляр, знак потом уточним кликом

                // 3) Клик — с какой стороны линии сдвинуть тэги.
                XYZ side;
                try
                {
                    side = to2d(uidoc.Selection.PickPoint(
                        ObjectSnapTypes.None, "JTOOLS: тэги по границе — кликните с той стороны линии, куда сдвинуть тэги"));
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    DeleteTempAxis(doc, tempAxisId);
                    GrdLog.Log("GrdRadiatorTagsCommand: пользователь отменил выбор стороны");
                    return Result.Cancelled;
                }

                double sign = (side.X - p0.X) * n.X + (side.Y - p0.Y) * n.Y;
                if (sign < 0) n = new XYZ(-n.X, -n.Y, 0.0);

                // 4) Диагностика ДО.
                int beforeHit = CountLeaderHits(items);
                GrdLog.Log("GrdRadiatorTagsCommand: тэгов=" + items.Count +
                           ", выносок по чужому тексту ДО=" + beforeHit);

                // 5) Укладка рядами вдоль границы.
                Layout(items, p0, u, n);

                // 6) Транзакция.
                int moved = 0, failed = 0;
                using (var t = new Transaction(doc, "JTOOLS: расстановка тэгов по границе"))
                {
                    t.Start();
                    foreach (var it in items)
                    {
                        var tag = doc.GetElement(it.Id) as IndependentTag;
                        if (tag == null) continue;
                        try
                        {
                            if (Distance2(it.Head, it.OriginalHead) > 1e-8)
                            {
                                tag.TagHeadPosition = to3d(it.Head);
                                moved++;
                            }
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            GrdLog.Log("GrdRadiatorTagsCommand: не сдвинут id=" + it.Id.Value + ": " + ex.Message);
                        }
                    }
                    if (tempAxisId != ElementId.InvalidElementId)
                    {
                        try { doc.Delete(tempAxisId); }
                        catch (Exception ex) { GrdLog.Log("GrdRadiatorTagsCommand: не удалена ось: " + ex.Message); }
                    }
                    t.Commit();
                }

                // 7) Диагностика ПОСЛЕ.
                int afterHit = CountLeaderHits(items);
                int rowsUsed = items.Max(x => x.Row) + 1;
                var perRow = items.GroupBy(x => x.Row).OrderBy(g => g.Key)
                                  .Select(g => "ряд" + g.Key + "=" + g.Count())
                                  .ToList();
                GrdLog.Log("GrdRadiatorTagsCommand: ПОСЛЕ — выносок по чужому тексту=" + afterHit +
                           ", перемещено=" + moved + ", ошибок=" + failed +
                           ", рядов задействовано=" + rowsUsed + " (" + string.Join(", ", perRow) + ")  | " +
                           "maxShift=" + (items.Max(x => Distance2(x.Head, x.OriginalHead)) * 304.8).ToString("0") + " мм");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("GrdRadiatorTagsCommand: EXCEPTION " + ex);
                try { TaskDialog.Show("JTOOLS: Расставить тэги по границе", "Ошибка: " + ex.Message); }
                catch { }
                return Result.Failed;
            }
        }

        /// <summary>Собрать модель одного тэга (в 2D-координатах плоскости вида).</summary>
        private static TagItem BuildItem(IndependentTag tag, View view, Func<XYZ, XYZ> to2d)
        {
            var headPos = tag.TagHeadPosition;
            if (headPos == null) return null;
            XYZ head = to2d(headPos);
            XYZ tMin = null, tMax = null;
            try
            {
                var bb = tag.get_BoundingBox(view);
                if (bb != null) { tMin = to2d(bb.Min); tMax = to2d(bb.Max); }
            }
            catch { }

            XYZ leaderEnd = null;
            bool hasLeader = false;
            try
            {
                hasLeader = tag.HasLeader;
                if (hasLeader)
                {
                    var refs = tag.GetTaggedReferences();
                    if (refs != null && refs.Count > 0)
                    {
                        var le = tag.GetLeaderEnd(refs[0]);
                        if (le != null) leaderEnd = to2d(le);
                    }
                }
            }
            catch { }
            // Если якорь выноски не получен — считаем, что выноски нет.
            if (leaderEnd == null) { hasLeader = false; leaderEnd = head; }

            if (tMin == null || tMax == null)
            {
                tMin = head - new XYZ(0.5 / 304.8, 0.5 / 304.8, 0);
                tMax = head + new XYZ(0.5 / 304.8, 0.5 / 304.8, 0);
            }

            return new TagItem
            {
                Id = tag.Id,
                Head = head,
                OriginalHead = head,
                HasLeader = hasLeader,
                LeaderEnd = leaderEnd,
                Anchor = leaderEnd,
                TextMin = tMin,
                TextMax = tMax,
                Gap = GapFt
            };
        }

        /// <summary>Удалить временную линию-ось (в отдельной транзакции).</summary>
        private static void DeleteTempAxis(Document doc, ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return;
            try
            {
                using (var t = new Transaction(doc, "JTOOLS: удаление превью оси"))
                {
                    t.Start();
                    doc.Delete(id);
                    t.Commit();
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("GrdRadiatorTagsCommand: не удалена ось при отмене: " + ex.Message);
            }
        }

        /// <summary>
        /// Укладка: тэги сортируются по проекции якоря на ось границы; для каждого тэга
        /// перебираются ряды от ближнего к дальним и позиции внутри ряда — выбирается
        /// позиция с минимальной стоимостью (смещение + выноска по чужим текстам),
        /// при обязательном отсутствии наложения текста на текст в этом ряду.
        /// </summary>
        private static void Layout(List<TagItem> items, XYZ p0, XYZ u, XYZ n)
        {
            // Проекции якорей на ось.
            foreach (var it in items)
            {
                it.Ti = (it.Anchor.X - p0.X) * u.X + (it.Anchor.Y - p0.Y) * u.Y;
                // Вершины bbox → ширина вдоль оси и высота вдоль нормали.
                double minA = double.PositiveInfinity, maxA = double.NegativeInfinity;
                double minB = double.PositiveInfinity, maxB = double.NegativeInfinity;
                foreach (var c in Corners(it.TextMin, it.TextMax))
                {
                    double a = (c.X - p0.X) * u.X + (c.Y - p0.Y) * u.Y;
                    double b = (c.X - p0.X) * n.X + (c.Y - p0.Y) * n.Y;
                    minA = Math.Min(minA, a); maxA = Math.Max(maxA, a);
                    minB = Math.Min(minB, b); maxB = Math.Max(maxB, b);
                }
                it.Width = Math.Max(1e-9, maxA - minA);
                it.Height = Math.Max(1e-9, maxB - minB);
            }

            double maxH = items.Max(x => x.Height);
            double pitch = maxH + 2.0 * RowGapFt;
            double firstDepth = GapFt + maxH * 0.5;

            // Занятые интервалы осевых позиций по каждому ряду.
            var rowOccupied = new List<List<Interval>>();
            for (int r = 0; r < MaxRows; r++) rowOccupied.Add(new List<Interval>());

foreach (var it in items.OrderBy(x => x.Ti))
                {
                    double bestCost = double.PositiveInfinity;
                    XYZ bestHead = it.Head;
                    int bestRow = 0;
                    double bestOffset = 0;
                    bool bestZeroCross = false;

                    for (int r = 0; r < MaxRows; r++)
                    {
                        double depth = firstDepth + r * pitch;
                        foreach (var cand in CandidateOffsets(it, items, rowOccupied[r], p0, u, n, depth))
                        {
                        if (!cand.Fits) continue;
                        double cost = Math.Abs(cand.Offset) * W_SHIFT +
                                      cand.LeaderHits * W_LEADER_HIT;
                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            bestHead = cand.Head;
                            bestRow = r;
                            bestOffset = cand.Offset;
                            bestZeroCross = cand.LeaderHits == 0;
                        }
                    }
                    // Глубже смысла нет, если уже нашли позицию без пересечений выносок.
                    if (bestZeroCross) break;
                }

                it.Head = bestHead;
                it.Row = bestRow;
                rowOccupied[bestRow].Add(
                    new Interval(bestOffset + it.Ti - it.Width * 0.5,
                                 bestOffset + it.Ti + it.Width * 0.5));
            }
        }

        /// <summary>Кандидатные позиции головы тэга внутри ряда на глубине depth.</summary>
        private static IEnumerable<Candidate> CandidateOffsets(
            TagItem it, List<TagItem> items, List<Interval> occupied, XYZ p0, XYZ u, XYZ n, double depth)
        {
            double maxOff = Math.Max(it.Width, 0.5 / 304.8);
            double cx = (it.TextMin.X + it.TextMax.X) / 2.0 - it.OriginalHead.X;
            double cy = (it.TextMin.Y + it.TextMax.Y) / 2.0 - it.OriginalHead.Y;

            for (int k = -OffsetSamples; k <= OffsetSamples; k++)
            {
                double off = maxOff * k / OffsetSamples;
                double x = it.Ti + off;
                // Центр текста на глубине depth, голова — со сдвигом центра относительно неё.
                var textCenter = new XYZ(p0.X + x * u.X + depth * n.X,
                                         p0.Y + x * u.Y + depth * n.Y, 0.0);
                var head = new XYZ(textCenter.X - cx, textCenter.Y - cy, 0.0);

                // Текст в этом ряду не должен накладываться на соседей.
                double lo = x - it.Width * 0.5 - GapFt * 0.5;
                double hi = x + it.Width * 0.5 + GapFt * 0.5;
                bool fits = true;
                for (int i = 0; i < occupied.Count; i++)
                {
                    if (lo < occupied[i].Hi && hi > occupied[i].Lo) { fits = false; break; }
                }
                if (!fits)
                {
                    yield return new Candidate { Offset = off, Fits = false };
                    continue;
                }

                yield return new Candidate
                {
                    Offset = off,
                    Fits = true,
                    Head = head,
                    LeaderHits = LeaderHitsFor(it, items, head)
                };
            }
        }

        private static IEnumerable<XYZ> Corners(XYZ min, XYZ max)
        {
            yield return min;
            yield return new XYZ(min.X, max.Y, 0);
            yield return max;
            yield return new XYZ(max.X, min.Y, 0);
        }

        // ===== Проверки пересечений =====

        /// <summary>Сколько выносок (head→anchor) пересекает чужие тексты.</summary>
        private static int CountLeaderHits(List<TagItem> items)
        {
            int n = 0;
            foreach (var a in items)
            {
                if (!a.HasLeader || a.LeaderEnd == null) continue;
                foreach (var b in items)
                {
                    if (b == a) continue;
                    if (SegmentIntersectsRect(a.Head, a.LeaderEnd, b.TextRectMin(), b.TextRectMax())) n++;
                }
            }
            return n;
        }

        /// <summary>Пересечения выноски (head→anchor) того же тэга при временной позиции head.</summary>
        private static int LeaderHitsFor(TagItem it, List<TagItem> items, XYZ head)
        {
            if (!it.HasLeader || it.LeaderEnd == null) return 0;
            int n = 0;
            foreach (var b in items)
            {
                if (b == it) continue;
                if (SegmentIntersectsRect(head, it.LeaderEnd, b.TextRectMin(), b.TextRectMax())) n++;
            }
            return n;
        }

        /// <summary>Пересекаются ли 2D-отрезки p1-p2 и q1-q2 (строго, касание не считается).</summary>
        private static bool SegmentsIntersect(XYZ p1, XYZ p2, XYZ q1, XYZ q2)
        {
            double o1 = Cross2(p1, p2, q1);
            double o2 = Cross2(p1, p2, q2);
            double o3 = Cross2(q1, q2, p1);
            double o4 = Cross2(q1, q2, p2);
            return (o1 * o2 < -epsZero) && (o3 * o4 < -epsZero);
        }

        private const double epsZero = 1.0e-12;

        private static double Cross2(XYZ a, XYZ b, XYZ c)
            => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        /// <summary>Пересекает ли отрезок p1-p2 прямоугольник [rMin..rMax] (2D).</summary>
        private static bool SegmentIntersectsRect(XYZ p1, XYZ p2, XYZ rMin, XYZ rMax)
        {
            double rMinX = Math.Min(rMin.X, rMax.X), rMaxX = Math.Max(rMin.X, rMax.X);
            double rMinY = Math.Min(rMin.Y, rMax.Y), rMaxY = Math.Max(rMin.Y, rMax.Y);
            if (p1.X >= rMinX && p1.X <= rMaxX && p1.Y >= rMinY && p1.Y <= rMaxY) return true;
            if (p2.X >= rMinX && p2.X <= rMaxX && p2.Y >= rMinY && p2.Y <= rMaxY) return true;
            return SegmentsIntersect(p1, p2, new XYZ(rMinX, rMinY, 0), new XYZ(rMaxX, rMinY, 0))
                || SegmentsIntersect(p1, p2, new XYZ(rMaxX, rMinY, 0), new XYZ(rMaxX, rMaxY, 0))
                || SegmentsIntersect(p1, p2, new XYZ(rMaxX, rMaxY, 0), new XYZ(rMinX, rMaxY, 0))
                || SegmentsIntersect(p1, p2, new XYZ(rMinX, rMaxY, 0), new XYZ(rMinX, rMinY, 0));
        }

        private static double Distance2(XYZ a, XYZ b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        // ===== Данные =====

        private struct Interval
        {
            public double Lo, Hi;
            public Interval(double lo, double hi) { Lo = lo; Hi = hi; }
        }

        private class Candidate
        {
            public double Offset;
            public bool Fits;
            public XYZ Head;
            public int LeaderHits;
        }

        /// <summary>Данные одного тэга в 2D-координатах плоскости вида.</summary>
        private class TagItem
        {
            public ElementId Id;
            public XYZ Head;           // текущая позиция головы (2D)
            public XYZ OriginalHead;   // исходная
            public bool HasLeader;
            public XYZ LeaderEnd;      // якорь выноски (2D)
            public XYZ Anchor;         // точка проекции на ось (якорь выноски или голова)
            public XYZ TextMin;        // bbox текста (2D)
            public XYZ TextMax;
            public double Gap;
            public double Ti;          // проекция якоря на ось границы
            public double Width;       // экстент текста вдоль оси
            public double Height;      // экстент текста вдоль нормали
            public int Row;            // номер ряда, в котором размещён тэг

            public XYZ TextRectMin() => new XYZ(Head.X + (TextMin.X - OriginalHead.X),
                                                Head.Y + (TextMin.Y - OriginalHead.Y), 0);
            public XYZ TextRectMax() => new XYZ(Head.X + (TextMax.X - OriginalHead.X),
                                                Head.Y + (TextMax.Y - OriginalHead.Y), 0);
        }
    }
}