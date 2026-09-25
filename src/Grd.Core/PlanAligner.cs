using System;
using System.Collections.Generic;
using System.Linq;

namespace GrdRevit.Core
{
    /// <summary>Точка на плоскости (любая система координат; мм, если не оговорено).</summary>
    public struct PlanPoint
    {
        public PlanPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X;
        public double Y;
    }

    /// <summary>
    /// Результат совмещения DXF-плана и текущего вида: только перенос (масштаб и
    /// поворот считаются совпадающими). Отсюда — координаты прибора DXF -> вид Revit.
    /// </summary>
    public sealed class PlanAlignResult
    {
        public double OffsetX;
        public double OffsetY;
        public int Inliers;
        public bool IsValid;
        internal int _votes;

        public double MapX(double x) => x + OffsetX;
        public double MapY(double y) => y + OffsetY;
    }

    /// <summary>
    /// Совмещение множества точек плана (DXF, мм) с точками элементов вида (Revit, мм).
    /// Пользователь исходит из того, что масштаб и координаты приборов совпадают, — поэтому
    /// оцениваем только перенос: по гистограмме разностей «точка Revit - точка DXF» берём
    /// саму населённую ячейку (сдвиг, общий для большинства приборов текущего вида).
    /// </summary>
    public static class PlanAligner
    {
        /// <summary>Перевод м в футы Revit (внутренние единицы).</summary>
        public const double MmPerFoot = 304.8;

        /// <summary>
        /// Совмещение множества точек плана (source) с точками элементов вида (target).
        /// Среди топ-кандидатов сдвига из гистограмм пар «плановые × видимые» выбирается тот,
        /// который лучше всего совмещает query-точки (обычно — именно выбранные приборы):
        /// это исключает ложный «валидный» сдвиг, найденный по другим этажам/элементам плана.
        /// </summary>
        public static PlanAlignResult Estimate(
            IReadOnlyList<PlanPoint> source,
            IReadOnlyList<PlanPoint> target,
            double toleranceMm,
            int minInliers = 3,
            IReadOnlyList<PlanPoint> query = null,
            int topCandidates = 12)
        {
            var res = new PlanAlignResult();
            if (source == null || target == null || source.Count == 0 || target.Count == 0) return res;

            var q = query ?? source;
            if (q.Count == 0) return res;

            double cell = Math.Max(50.0, toleranceMm / 2.0);

            var cands = TopCells(source, target, cell, Math.Max(1, topCandidates / 2));
            cands.AddRange(TopCells(q, target, cell, Math.Max(1, topCandidates / 2)));

            PlanAlignResult best = null;
            foreach (var c in cands)
            {
                var (ox, oy) = RefineOffset(source, target, c.Ox, c.Oy, toleranceMm);
                int focusMatches = CountWithin(q, target, ox, oy, toleranceMm);
                if (best == null ||
                    focusMatches > best.Inliers ||
                    (focusMatches == best.Inliers && c.Votes > best._votes))
                {
                    best = new PlanAlignResult
                    {
                        OffsetX = ox,
                        OffsetY = oy,
                        Inliers = focusMatches,
                        IsValid = focusMatches >= minInliers,
                        _votes = c.Votes
                    };
                }
            }

            return best ?? res;
        }

        private sealed class Candidate
        {
            public double Ox;
            public double Oy;
            public int Votes;
        }

        /// <summary>Топ-ячейки гистограммы сдвигов «target - source» по числу совпадений.</summary>
        private static List<Candidate> TopCells(
            IReadOnlyList<PlanPoint> src, IReadOnlyList<PlanPoint> tgt, double cell, int n)
        {
            var votes = new Dictionary<long, int>();
            var sums = new Dictionary<long, (double Sx, double Sy)>();

            foreach (var s in src)
            {
                foreach (var t in tgt)
                {
                    double dx = t.X - s.X;
                    double dy = t.Y - s.Y;
                    long key = CellKey(dx, dy, cell);
                    votes[key] = votes.TryGetValue(key, out int v) ? v + 1 : 1;
                    if (sums.TryGetValue(key, out var acc)) sums[key] = (acc.Sx + dx, acc.Sy + dy);
                    else sums[key] = (dx, dy);
                }
            }

            return votes
                .OrderByDescending(kv => kv.Value)
                .Take(n)
                .Select(kv => { var s = sums[kv.Key]; return new Candidate { Ox = s.Sx / kv.Value, Oy = s.Sy / kv.Value, Votes = kv.Value }; })
                .ToList();
        }

        /// <summary>Уточнение сдвига по совпавшим парам (одна итерация ICP-стиля).</summary>
        private static (double Ox, double Oy) RefineOffset(
            IReadOnlyList<PlanPoint> src, IReadOnlyList<PlanPoint> tgt, double ox, double oy, double toleranceMm)
        {
            double t2 = toleranceMm * toleranceMm;
            double sx = 0, sy = 0;
            int n = 0;
            foreach (var s in src)
            {
                foreach (var t in tgt)
                {
                    double ddx = (t.X - ox) - s.X;
                    double ddy = (t.Y - oy) - s.Y;
                    if (ddx * ddx + ddy * ddy <= t2)
                    {
                        sx += t.X - s.X;
                        sy += t.Y - s.Y;
                        n++;
                    }
                }
            }
            if (n == 0) return (ox, oy);
            return (sx / n, sy / n);
        }

        private static int CountWithin(
            IReadOnlyList<PlanPoint> query, IReadOnlyList<PlanPoint> target,
            double ox, double oy, double toleranceMm)
        {
            int matches = 0;
            foreach (var qp in query)
            {
                if (AnyWithin(target, qp.X + ox, qp.Y + oy, toleranceMm)) matches++;
            }
            return matches;
        }

        private static long CellKey(double dx, double dy, double cell)
        {
            long ix = (long)Math.Round(dx / cell);
            long iy = (long)Math.Round(dy / cell);
            return (ix << 32) ^ (iy & 0xFFFFFFFFL);
        }

        private static bool AnyWithin(IReadOnlyList<PlanPoint> points, double x, double y, double tol)
        {
            double t2 = tol * tol;
            foreach (var p in points)
            {
                double ddx = p.X - x;
                double ddy = p.Y - y;
                if (ddx * ddx + ddy * ddy <= t2) return true;
            }
            return false;
        }
    }
}