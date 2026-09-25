using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace GrdRevit
{
    /// <summary>
    /// «Расставить тэги»: ищет пересечения между тэгами на текущем виде и переразмещает их,
    /// чтобы тэги не накладывались друг на друга. Только тэги между собой (стены/оборудование
    /// не учитываются). Тело каждого тэга = текст (BoundingBox на виде) + выноска (линия
    /// голова→якорь с толщиной).
    ///
    /// Алгоритм: для каждого тэга генерируется набор кандидатов-позиций (оригинал + точки
    /// на нескольких орбитах вокруг якоря), каждый оценивается весовой целевой функцией
    /// cost = w_disp·смещение + w_overlap·пересечения тел + w_cross·пересечения выносок
    ///      + w_hit·выноска по чужому тексту + w_out·за границами вида,
    /// после чего выбирается самый дешёвый кандидат. Несколько раундов до стабилизации.
    /// Тэги, которые не удалось развести, возвращаются на исходную позицию.
    /// Результат и качество «до/после» пишутся в лог (без окон).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdArrangeTagsCommand : IExternalCommand
    {
        // ===== Константы и веса (настройка — здесь) =====
        // Веса штрафов целевой функции. Смещение считается в футах (Revit API),
        // остальные штрафы — «за штуку». Баланс: не сдвигать зря, но не оставлять пересечений.
        private const double W_DISPLACEMENT = 60.0;       // за 1 фут смещения (тексты большие — движение дешёвое)
        private const double W_OVERLAP = 1000.0;          // за каждое пересечение тел (текст/выноска)
        private const double W_LEADER_CROSS = 800.0;      // за каждое пересечение выноски с чужой выноской
        private const double W_LEADER_HIT_TEXT = 900.0;   // за выноску, проходящую по чужому тексту
        private const double W_OUTSIDE_BOUNDS = 1.0e7;    // позиция за границами вида (запретная)
        // Штраф за сдвиг против указанной стороны (рядом/колонки поощряют свою, а чужую — штрафуют).
        private const double W_AGAINST_DIR = 1200.0;       // за 1 фут против направления
        private const double W_TOWARD_DIR = 200.0;         // поощрение (вычитается) за 1 фут по направлению

        /// <summary>Направление предпочтительного сдвига (ноль — если не задано пользователем).</summary>
        private static XYZ _moveDir;

// Число раундов оптимизации и генерация кандидатов
            private const int MaxOptimizeRounds = 10;
        private const int OrbitSteps = 16;                // точек на орбите
        // Радиусы орбит (в футах): 0 — базовый вокруг якоря, дальше с шагом.
        // Тонкая настройка: начальный радиус зависит от размера текста тэга (см. CandidateRadii).
        private const int OrbitCount = 5;
        private const double OrbitStepFt = 10.0 / 304.8;  // 10 мм
        // «Эстетика — ряды/колонки»: сколько рядов над/под якорем и колонок вправо/влево.
        private const int ColumnRows = 6;    // тексты вверх/вниз от якоря (вертикальная выноска)
        private const int RowColumns = 4;    // тексты вправо/влево от якоря (горизонтальная выноска)

        private const double GapFt = 2.0 / 304.8;         // зазор между тэгами ~2 мм
        private const double LeaderThicknessFt = 1.5 / 304.8;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                RevitContext.Initialize(commandData.Application);

                var uidoc = commandData.Application.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null)
                {
                    GrdLog.Log("GrdArrangeTagsCommand: нет активного документа");
                    return Result.Cancelled;
                }
                var view = uidoc.ActiveGraphicalView;
                if (view == null || view is View3D || view is ViewSchedule)
                {
                    GrdLog.Log("GrdArrangeTagsCommand: нужен 2D-вид (план/разрез/фасад), текущий: " +
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

                var outline0 = view.Outline;
                if (outline0 == null)
                {
                    GrdLog.Log("GrdArrangeTagsCommand: пустой outline вида");
                    return Result.Cancelled;
                }

                // Предварительное выделение: если пользователь заранее выбрал тэги —
                // работаем только с ними и просим указать точку-сторону смещения.
                var selectedIds = new HashSet<ElementId>();
                try
                {
                    var sel = uidoc.Selection.GetElementIds();
                    foreach (var id in sel)
                    {
                        if (doc.GetElement(id) is IndependentTag) selectedIds.Add(id);
                    }
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
                    XYZ headModel = tag.TagHeadPosition;
                    if (headModel == null) continue;
                    var head = to2d(headModel);

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
                            else
                            {
                                hasLeader = false;
                            }
                        }
                    }
                    catch { }

                    var item = new TagItem
                    {
                        Id = tag.Id,
                        Name = tag.TagText ?? string.Empty,
                        Head = head,
                        OriginalHead = head,
                        HasLeader = hasLeader,
                        LeaderEnd = leaderEnd,
                        TextMin = tMin != null ? to2d(tMin) : null,
                        TextMax = tMax != null ? to2d(tMax) : null,
                        Gap = GapFt,
                        LeaderThickness = LeaderThicknessFt
                    };
                    if (item.TextMin == null)
                    {
                        item.TextMin = item.Head - new XYZ(0.5 / 304.8, 0.5 / 304.8, 0);
                        item.TextMax = item.Head + new XYZ(0.5 / 304.8, 0.5 / 304.8, 0);
                    }
                    items.Add(item);
                }

                // Границы движения строим от самих тэгов: общий охват их текстов (в исходных
                // позициях) + запас, равный максимальному размеру текста. view.Outline на
                // листе измеряется в иных единицах, чем геометрия тэга, поэтому не годится.
                double bx0 = double.PositiveInfinity, bx1 = double.NegativeInfinity;
                double by0 = double.PositiveInfinity, by1 = double.NegativeInfinity;
                double maxTextW = 0.0, maxTextH = 0.0;
                foreach (var it in items)
                {
                    bx0 = Math.Min(bx0, it.TextMin.X); by0 = Math.Min(by0, it.TextMin.Y);
                    bx1 = Math.Max(bx1, it.TextMax.X); by1 = Math.Max(by1, it.TextMax.Y);
                    maxTextW = Math.Max(maxTextW, it.TextMax.X - it.TextMin.X);
                    maxTextH = Math.Max(maxTextH, it.TextMax.Y - it.TextMin.Y);
                }
                // Запас под «ряды/колонки»: чтобы хватило места выстроить тексты в несколько
                // рядов над/под якорем и колонок вправо/влево.
                double padX = Math.Max(2 * maxTextW, RowColumns * (maxTextW + 2 * GapFt) + maxTextW);
                double padY = Math.Max(2 * maxTextH, ColumnRows * (maxTextH + 2 * GapFt) + maxTextH);
                foreach (var it in items)
                {
                    it.BoundMin = new XYZ(bx0 - padX, by0 - padY, 0.0);
                    it.BoundMax = new XYZ(bx1 + padX, by1 + padY, 0.0);
                }

                GrdLog.Log("GrdArrangeTagsCommand: вид «" + view.Name + "», найдено тэгов: " + items.Count +
                           ", область движения=(" + (bx0 - padX).ToString("0.###") + ";" + (by0 - padY).ToString("0.###") + ")..(" +
                           (bx1 + padX).ToString("0.###") + ";" + (by1 + padY).ToString("0.###") + ") фут, размер=" +
                           ((bx1 - bx0 + 2 * padX) * 304.8).ToString("0") + "x" + ((by1 - by0 + 2 * padY) * 304.8).ToString("0") + " мм");
                if (items.Count < 2)
                {
                    GrdLog.Log("GrdArrangeTagsCommand: тэгов меньше двух — расставлять нечего");
                    return Result.Cancelled;
                }

                // Пользователь выбрал тэги заранее — спрашиваем точку-сторону смещения.
                // Направление = от общего центра тэгов к указанной точке. Позже оно подменит
                // «симметричную» сетку рядов/колонок на смещённую в выбранную сторону.
                _moveDir = null;
                if (selectedIds.Count > 0)
                {
                    try
                    {
                        var pick = uidoc.Selection.PickPoint(
                            Autodesk.Revit.UI.Selection.ObjectSnapTypes.None,
                            "JTOOLS: укажите точку — сторону, в которую сдвинуть выбранные тэги");
                        if (pick != null)
                        {
                            var p2 = to2d(pick);
                            double cx = 0, cy = 0;
                            foreach (var it in items) { cx += it.OriginalHead.X; cy += it.OriginalHead.Y; }
                            cx /= items.Count; cy /= items.Count;
                            double len = Math.Sqrt((p2.X - cx) * (p2.X - cx) + (p2.Y - cy) * (p2.Y - cy));
                            if (len > 1e-9)
                            {
                                _moveDir = new XYZ((p2.X - cx) / len, (p2.Y - cy) / len, 0.0);
                                GrdLog.Log("GrdArrangeTagsCommand: пользователь задал сторону смещения → ("
                                           + _moveDir.X.ToString("0.###") + "; " + _moveDir.Y.ToString("0.###") + ")");
                            }
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        GrdLog.Log("GrdArrangeTagsCommand: пользователь отменил выбор стороны — расставляю без неё");
                    }
                    catch (Exception ex)
                    {
                        GrdLog.Log("GrdArrangeTagsCommand: ошибка выбора стороны: " + ex.Message);
                    }
                }

                // Диагностика ДО
                int beforeOverlap = CountBodyOverlaps(items);
                int beforeCross = CountLeaderCrossings(items);
                int beforeHit = CountLeaderHitsText(items);
                GrdLog.Log("GrdArrangeTagsCommand: ДО — пересечений тел=" + beforeOverlap +
                           ", пересечений выносок=" + beforeCross +
                           ", выносок по чужому тексту=" + beforeHit);

                // Геометрия каждого тэга (для диагностики)
                foreach (var it in items)
                {
                    double tw = (it.TextMax.X - it.TextMin.X) * 304.8;
                    double th = (it.TextMax.Y - it.TextMin.Y) * 304.8;
                    string lead = it.HasLeader && it.LeaderEnd != null
                        ? "L(" + ((it.LeaderEnd.X - it.Head.X) * 304.8).ToString("0") + ";"
                          + ((it.LeaderEnd.Y - it.Head.Y) * 304.8).ToString("0") + ") мм"
                        : "L-";
                    GrdLog.Log("GrdArrangeTagsCommand:   геометрия id=" + it.Id.Value +
                               " текст=" + tw.ToString("0") + "x" + th.ToString("0") + " мм, " + lead);
                }

                // Оптимизация: весовой выбор лучшего кандидата на каждую позицию
                Optimize(items);

                // Сбор результата + возврат неразрешённых на исходную
                var toMove = new List<KeyValuePair<IndependentTag, XYZ>>();
                int moved = 0, restored = 0;
                foreach (var it in items)
                {
                    var tag = doc.GetElement(it.Id) as IndependentTag;
                    if (tag == null) continue;

                    bool inBound = TextInBounds(it, it.Head);
                    bool overlaps = CountOverlaps(it, items) > 0 || !inBound;
                    if (overlaps)
                    {
                        if (Distance2(it.Head, it.OriginalHead) > 1e-6) restored++;
                        it.Head = it.OriginalHead;
                        it.RecomputeBody();
                        GrdLog.Log("GrdArrangeTagsCommand: не убран тэг «" + it.Name + "» id=" + it.Id.Value +
                                   " — возвращаю на исходную позицию");
                        toMove.Add(new KeyValuePair<IndependentTag, XYZ>(tag, to3d(it.OriginalHead)));
                    }
                    else
                    {
                        if (Distance2(it.Head, it.OriginalHead) > 1e-6)
                        {
                            moved++;
                            GrdLog.Log("GrdArrangeTagsCommand: сдвигаю «" + it.Name + "» id=" + it.Id.Value +
                                       " на Δ=" + (Distance2(it.Head, it.OriginalHead) * 304.8).ToString("0.#") + " мм");
                        }
                        toMove.Add(new KeyValuePair<IndependentTag, XYZ>(tag, to3d(it.Head)));
                    }
                }

                using (var t = new Transaction(doc, "JTOOLS: расстановка тэгов"))
                {
                    t.Start();
                    foreach (var kv in toMove)
                    {
                        try
                        {
                            kv.Key.TagHeadPosition = kv.Value;
                        }
                        catch (Exception ex)
                        {
                            GrdLog.Log("GrdArrangeTagsCommand: не удалось сдвинуть id=" + kv.Key.Id.Value + ": " + ex.Message);
                        }
                    }
                    t.Commit();
                }

                // Диагностика ПОСЛЕ (по целевым позициям)
                int afterOverlap = CountBodyOverlaps(items);
                int afterCross = CountLeaderCrossings(items);
                int afterHit = CountLeaderHitsText(items);
                GrdLog.Log("GrdArrangeTagsCommand: ПОСЛЕ — пересечений тел=" + afterOverlap +
                           ", пересечений выносок=" + afterCross +
                           ", выносок по чужому тексту=" + afterHit +
                           "  | перемещено=" + moved + ", возвращено на место=" + restored + ", всего=" + items.Count);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("GrdArrangeTagsCommand: EXCEPTION " + ex);
                try { TaskDialog.Show("JTOOLS: Расставить тэги", "Ошибка: " + ex.Message); }
                catch { }
                return Result.Failed;
            }
        }

        /// <summary>Несколько раундов: для каждого тэга выбрать кандидата с минимальной стоимостью.</summary>
        private static void Optimize(List<TagItem> items)
        {
            for (int round = 0; round < MaxOptimizeRounds; round++)
            {
                bool anyChange = false;
                // Сначала — менее "пересекающиеся" тэги (изолированные легко развести)
                var order = items
                    .OrderBy(it => CountOverlaps(it, items))
                    .ThenBy(it => it.Id.Value)
                    .ToList();

                foreach (var it in order)
                {
                    var candidates = CandidatePositions(it);
                    double best = double.PositiveInfinity;
                    XYZ bestPos = it.Head;
                    foreach (var cand in candidates)
                    {
                        double cost = EvaluateAt(it, cand, items);
                        if (cost < best)
                        {
                            best = cost;
                            bestPos = cand;
                        }
                    }
                    if (Distance2(bestPos, it.Head) > 1e-6)
                    {
                        it.Head = bestPos;
                        it.RecomputeBody();
                        anyChange = true;
                    }
                }
                if (!anyChange) break;
            }
        }

        /// <summary>
        /// Сгенерировать кандидатов: оригинал + «ряды/колонки» вокруг якоря.
        /// Текст ставится строго над/под якорем (вертикальная выноска) или вправо/влево
        /// (горизонтальная), шаг кратен размеру текста. Параллельные выноски не пересекаются,
        /// тэги выглядят выровненными по рядам и колонкам.
        /// </summary>
        private static List<XYZ> CandidatePositions(TagItem it)
        {
            var list = new List<XYZ> { it.OriginalHead };
            XYZ anchor = it.HasLeader && it.LeaderEnd != null ? it.LeaderEnd : it.OriginalHead;
            list.Add(anchor);

            double textW = Math.Max(1e-9, it.TextMax.X - it.TextMin.X);
            double textH = Math.Max(1e-9, it.TextMax.Y - it.TextMin.Y);

            // Смещение центра текста относительно головы: чтобы центрировать текст на якоре,
            // голову нужно поставить со сдвигом на (центр текста) относительно якоря.
            double cx = (it.TextMin.X + it.TextMax.X) / 2.0 - it.OriginalHead.X;
            double cy = (it.TextMin.Y + it.TextMax.Y) / 2.0 - it.OriginalHead.Y;

            double stepX = textW + it.Gap * 2.0;   // шаг колонки — ширина текста
            double stepY = textH + it.Gap * 2.0;   // шаг ряда — высота текста

            // Вертикальные колонки: вверх и вниз от якоря (параллельные выноски).
            // Первый ряд отстоит на пол-высоты текста + зазор, чтобы выноска упиралась
            // в нижнюю грань текста, а не в его середину.
            double yFirst = textH * 0.5 + it.Gap;
            for (int k = 1; k <= ColumnRows; k++)
            {
                double dY = yFirst + (k - 1) * stepY;
                list.Add(new XYZ(anchor.X - cx, anchor.Y - cy + dY, 0));
                list.Add(new XYZ(anchor.X - cx, anchor.Y - cy - dY, 0));
            }
            // Горизонтальные колонки: вправо и влево.
            double xFirst = textW * 0.5 + it.Gap;
            for (int k = 1; k <= RowColumns; k++)
            {
                double dX = xFirst + (k - 1) * stepX;
                list.Add(new XYZ(anchor.X - cx + dX, anchor.Y - cy, 0));
                list.Add(new XYZ(anchor.X - cx - dX, anchor.Y - cy, 0));
            }

            // Запасные грязные орбиты (когда ряды не помогли рассечь плотную кучу).
            int rings = 0;
            double r = stepX * 0.5;
            while (rings < OrbitCount)
            {
                double a0 = (rings % 2) * (Math.PI / OrbitSteps);
                for (int k = 0; k < OrbitSteps; k++)
                {
                    double a = k * (2.0 * Math.PI / OrbitSteps) + a0;
                    list.Add(anchor + new XYZ(r * Math.Cos(a), r * Math.Sin(a), 0));
                }
                rings++;
                r = stepX * (0.5 + rings);
            }

            // Сетка по всей области движения как самый последний запасной вариант.
            double x = it.BoundMin.X;
            while (x <= it.BoundMax.X + 1e-9)
            {
                double y = it.BoundMin.Y;
                while (y <= it.BoundMax.Y + 1e-9)
                {
                    list.Add(new XYZ(x, y, 0));
                    y += stepY;
                }
                x += stepX;
            }

            return list;
        }

        /// <summary>Стоимость позиции cand (временно мутирует тэг, восстановливает перед возвратом).</summary>
        private static double EvaluateAt(TagItem it, XYZ cand, List<TagItem> items)
        {
            var prev = it.Head;
            it.Head = cand;
            it.RecomputeBody();

            double cost = 0.0;
            cost += W_DISPLACEMENT * Distance2(cand, it.OriginalHead);
            if (!TextInBounds(it, cand)) cost += W_OUTSIDE_BOUNDS;

            // Направление (выбранная пользователем сторона): смещение вдоль него поощряем,
            // против — штрафуем.
            if (_moveDir != null)
            {
                var d = cand - it.OriginalHead;
                double proj = d.X * _moveDir.X + d.Y * _moveDir.Y;
                if (proj >= 0) cost -= W_TOWARD_DIR * proj;
                else cost += W_AGAINST_DIR * (-proj);
            }

            foreach (var o in items)
            {
                if (o == it) continue;
                if (Intersects(it, o)) cost += W_OVERLAP;
            }

            if (it.HasLeader && it.LeaderEnd != null)
            {
                foreach (var o in items)
                {
                    if (o == it) continue;
                    // выноска по чужому тексту
                    if (SegmentIntersectsRect(it.Head, it.LeaderEnd, o.TextRectMin(), o.TextRectMax()))
                        cost += W_LEADER_HIT_TEXT;
                    // пересечение с чужой выноской
                    if (o.HasLeader && o.LeaderEnd != null &&
                        SegmentsIntersect(it.Head, it.LeaderEnd, o.Head, o.LeaderEnd))
                        cost += W_LEADER_CROSS;
                }
            }

            it.Head = prev;
            it.RecomputeBody();
            return cost;
        }

        private static bool TextInBounds(TagItem it, XYZ head)
        {
            var tMin = new XYZ(head.X + (it.TextMin.X - it.OriginalHead.X),
                               head.Y + (it.TextMin.Y - it.OriginalHead.Y), 0);
            var tMax = new XYZ(head.X + (it.TextMax.X - it.OriginalHead.X),
                               head.Y + (it.TextMax.Y - it.OriginalHead.Y), 0);
            return tMin.X >= it.BoundMin.X - 1e-6 && tMax.X <= it.BoundMax.X + 1e-6 &&
                   tMin.Y >= it.BoundMin.Y - 1e-6 && tMax.Y <= it.BoundMax.Y + 1e-6;
        }

        // ===== Проверки пересечений =====

        private static int CountOverlaps(TagItem it, List<TagItem> items)
        {
            int n = 0;
            foreach (var o in items)
            {
                if (o == it) continue;
                if (Intersects(it, o)) n++;
            }
            return n;
        }

        private static int CountBodyOverlaps(List<TagItem> items)
        {
            int n = 0;
            for (int i = 0; i < items.Count; i++)
                for (int j = i + 1; j < items.Count; j++)
                    if (Intersects(items[i], items[j])) n++;
            return n;
        }

        private static int CountLeaderCrossings(List<TagItem> items)
        {
            int n = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var a = items[i];
                if (!a.HasLeader || a.LeaderEnd == null) continue;
                for (int j = i + 1; j < items.Count; j++)
                {
                    var b = items[j];
                    if (!b.HasLeader || b.LeaderEnd == null) continue;
                    if (SegmentsIntersect(a.Head, a.LeaderEnd, b.Head, b.LeaderEnd)) n++;
                }
            }
            return n;
        }

        private static int CountLeaderHitsText(List<TagItem> items)
        {
            int n = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var a = items[i];
                if (!a.HasLeader || a.LeaderEnd == null) continue;
                for (int j = 0; j < items.Count; j++)
                {
                    if (i == j) continue;
                    var b = items[j];
                    if (SegmentIntersectsRect(a.Head, a.LeaderEnd, b.TextRectMin(), b.TextRectMax())) n++;
                }
            }
            return n;
        }

        private static bool Intersects(TagItem a, TagItem b)
        {
            var aMin = a.TextRectMin();
            var aMax = a.TextRectMax();
            var bMin = b.TextRectMin();
            var bMax = b.TextRectMax();
            double mx = Math.Min(aMax.X, bMax.X);
            double mnX = Math.Max(aMin.X, bMin.X);
            double my = Math.Min(aMax.Y, bMax.Y);
            double mnY = Math.Max(aMin.Y, bMin.Y);
            return mnX < mx - a.Gap * 0.5 && mnY < my - a.Gap * 0.5;
        }

        /// <summary>Пересекаются ли 2D-отрезки p1-p2 и q1-q2 (строго, касание не считается).</summary>
        private static bool SegmentsIntersect(XYZ p1, XYZ p2, XYZ q1, XYZ q2)
        {
            double o1 = Cross2(p1, p2, q1);
            double o2 = Cross2(p1, p2, q2);
            double o3 = Cross2(q1, q2, p1);
            double o4 = Cross2(q1, q2, p2);
            if (Math.Abs(o1) < epsOrientation && Between(p1.X, p2.X, q1.X) && Between(p1.Y, p2.Y, q1.Y)) return false;
            if (Math.Abs(o2) < epsOrientation && Between(p1.X, p2.X, q2.X) && Between(p1.Y, p2.Y, q2.Y)) return false;
            return (o1 * o2 < -epsZero) && (o3 * o4 < -epsZero);
        }

        private const double epsOrientation = 1.0e-10;
        private const double epsZero = 1.0e-12;

        private static double Cross2(XYZ a, XYZ b, XYZ c)
            => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        private static bool Between(double a, double b, double c)
            => (a <= c && c <= b) || (b <= c && c <= a);

        /// <summary>Пересекает ли отрезок p1-p2 прямоугольник [rMin..rMax] (2D).</summary>
        private static bool SegmentIntersectsRect(XYZ p1, XYZ p2, XYZ rMin, XYZ rMax)
        {
            double rMinX = Math.Min(rMin.X, rMax.X), rMaxX = Math.Max(rMin.X, rMax.X);
            double rMinY = Math.Min(rMin.Y, rMax.Y), rMaxY = Math.Max(rMin.Y, rMax.Y);
            // конец внутри
            if (p1.X >= rMinX && p1.X <= rMaxX && p1.Y >= rMinY && p1.Y <= rMaxY) return true;
            if (p2.X >= rMinX && p2.X <= rMaxX && p2.Y >= rMinY && p2.Y <= rMaxY) return true;
            // пересечение с любой из 4 сторон
            return SegmentsIntersect(p1, p2, new XYZ(rMinX, rMinY, 0), new XYZ(rMaxX, rMinY, 0))
                || SegmentsIntersect(p1, p2, new XYZ(rMaxX, rMinY, 0), new XYZ(rMaxX, rMaxY, 0))
                || SegmentsIntersect(p1, p2, new XYZ(rMaxX, rMaxY, 0), new XYZ(rMinX, rMaxY, 0))
                || SegmentsIntersect(p1, p2, new XYZ(rMinX, rMaxY, 0), new XYZ(rMinX, rMinY, 0));
        }

        private static double Distance2(XYZ a, XYZ b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        /// <summary>Данные одного тэга в 2D-координатах плоскости вида.</summary>
        private class TagItem
        {
            public ElementId Id;
            public string Name;
            public XYZ Head;           // текущая позиция головы (2D)
            public XYZ OriginalHead;   // исходная
            public bool HasLeader;
            public XYZ LeaderEnd;      // якорь выноски (2D)
            public XYZ TextMin;        // bbox текста относительно исходной головы (2D)
            public XYZ TextMax;
            public double Gap;
            public double LeaderThickness;
            public XYZ BoundMin;
            public XYZ BoundMax;

            public XYZ BodyMin;
            public XYZ BodyMax;

            public XYZ TextRectMin() => new XYZ(Head.X + (TextMin.X - OriginalHead.X),
                                                Head.Y + (TextMin.Y - OriginalHead.Y), 0);
            public XYZ TextRectMax() => new XYZ(Head.X + (TextMax.X - OriginalHead.X),
                                                Head.Y + (TextMax.Y - OriginalHead.Y), 0);

            public void RecomputeBody()
            {
                double tMinX = Head.X + (TextMin != null ? TextMin.X - OriginalHead.X : 0);
                double tMaxX = Head.X + (TextMax != null ? TextMax.X - OriginalHead.X : 0);
                double tMinY = Head.Y + (TextMin != null ? TextMin.Y - OriginalHead.Y : 0);
                double tMaxY = Head.Y + (TextMax != null ? TextMax.Y - OriginalHead.Y : 0);

                double bMinX = tMinX, bMaxX = tMaxX, bMinY = tMinY, bMaxY = tMaxY;
                if (HasLeader && LeaderEnd != null)
                {
                    double lx1 = Head.X, ly1 = Head.Y, lx2 = LeaderEnd.X, ly2 = LeaderEnd.Y;
                    double t = LeaderThickness * 0.5;
                    double lMinX = Math.Min(lx1, lx2) - t, lMaxX = Math.Max(lx1, lx2) + t;
                    double lMinY = Math.Min(ly1, ly2) - t, lMaxY = Math.Max(ly1, ly2) + t;
                    bMinX = Math.Min(bMinX, lMinX); bMaxX = Math.Max(bMaxX, lMaxX);
                    bMinY = Math.Min(bMinY, lMinY); bMaxY = Math.Max(bMaxY, lMaxY);
                }
                BodyMin = new XYZ(bMinX, bMinY, 0);
                BodyMax = new XYZ(bMaxX, bMaxY, 0);
            }
        }
    }
}