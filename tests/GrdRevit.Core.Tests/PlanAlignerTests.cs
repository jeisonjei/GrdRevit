using System.Collections.Generic;
using System.Linq;
using GrdRevit.Core;
using Xunit;

namespace GrdRevit.Core.Tests
{
    /// <summary>Совмещение координат DXF-плана (мм) с координатами вида Revit (мм).</summary>
    public class PlanAlignerTests
    {
        private static PlanPoint P(double x, double y) => new PlanPoint(x, y);

        [Fact]
        public void Aligner_ReturnsOffset_WhenPlanShifted()
        {
            var dxf = new List<PlanPoint>
            {
                P(1000, 2000), P(3000, 2000), P(1000, 4000), P(3000, 4000)
            };
            var revit = dxf.Select(p => P(p.X + 500000, p.Y + 12000)).ToList();

            var res = PlanAligner.Estimate(dxf, revit, 1000);

            Assert.True(res.IsValid);
            Assert.Equal(4, res.Inliers);
            Assert.Equal(500000, res.OffsetX, 2);
            Assert.Equal(12000, res.OffsetY, 2);
            Assert.Equal(501000, res.MapX(1000), 2);
        }

        [Fact]
        public void Aligner_HandlesSubsetMatchingFloor()
        {
            // Текущий вид содержит только часть приборов (этаж), остальные — «другие этажи»:
            // хоть их точки далеко, сдвиг считается по совпавшим.
            var dxf = new List<PlanPoint>
            {
                P(0, 0), P(5000, 0), P(10000, 0),
                P(0, 100000), P(5000, 100000), P(10000, 100000)
            };
            var revit = dxf.Take(3).Select(p => P(p.X + 70000, p.Y - 5000)).ToList();

            var res = PlanAligner.Estimate(dxf, revit, 1500);

            Assert.True(res.IsValid);
            Assert.True(res.Inliers >= 3);
            Assert.Equal(70000, res.OffsetX, 2);
            Assert.Equal(-5000, res.OffsetY, 2);
        }

        [Fact]
        public void Aligner_QueryFocusesOnSelectedRegion()
        {
            // Этаж А — выбранные приборы (координаты совпадают с точками вида: сдвиг 0,0).
            // Этаж Б — гора приборов в другой зоне: при сдвиге (-30000,-30000) его точки
            // ложатся ПРЯМО на точки вида, давая ложный доминирующий кластер. Гистограмма по
            // всем парам выберет его, «успешно» совместит — но выбранные приборы останутся мимо.
            var revit = new List<PlanPoint> { P(0, 0), P(0, 14000), P(18000, 0), P(18000, 14000) };

            var selectedA = new List<PlanPoint>
            {
                P(0, 0), P(0, 14000), P(18000, 0), P(18000, 14000)
            };

            // Ложный доминирующий кластер: 60 приборов «другого этажа» в зоне (+30000,+30000).
            // От каждой его точки до точки вида сдвиг (-30000,-30000) — один общий пик
            // гистограммы (60 голосов против 16 у правильного нулевого сдвига).
            var noiseB = new List<PlanPoint>();
            for (int i = 0; i < 15; i++)
            {
                foreach (var r in revit)
                {
                    noiseB.Add(P(r.X + 30000, r.Y + 30000));
                    noiseB.Add(P(r.X + 30010, r.Y + 30010));
                    noiseB.Add(P(r.X + 30020, r.Y + 30020));
                    noiseB.Add(P(r.X + 30030, r.Y + 30030));
                }
            }

            var dxf = selectedA.Concat(noiseB).ToList();

            var withoutQuery = PlanAligner.Estimate(dxf, revit, 1500, 3);
            var withQuery = PlanAligner.Estimate(dxf, revit, 1500, 3, selectedA);

            // Без ориентира попадаем в ложный кластер «этажа Б»….
            Assert.True(withoutQuery.IsValid);
            Assert.NotEqual(0, withoutQuery.OffsetX, 1);
            Assert.NotEqual(0, withoutQuery.OffsetY, 1);
            Assert.True(withoutQuery.Inliers >= 60); // большинство точек noiseB встали на точки вида

            // …но по выбранным приборам этажа А ни одна точка не ложится на вид.
            int selectedHitWithoutQuery = selectedA.Count(p => anyWithin(revit, p.X + withoutQuery.OffsetX, p.Y + withoutQuery.OffsetY, 1500));
            Assert.Equal(0, selectedHitWithoutQuery);

            // С query совмещение ориентируется на выбранный этаж: сдвиг 0,0 и все 4 точки.
            Assert.True(withQuery.IsValid);
            Assert.Equal(4, withQuery.Inliers);
            Assert.Equal(0, withQuery.OffsetX, 1);
            Assert.Equal(0, withQuery.OffsetY, 1);
        }

        private static bool anyWithin(IReadOnlyList<PlanPoint> pts, double x, double y, double tol)
        {
            double t2 = tol * tol;
            foreach (var p in pts)
            {
                double dx = p.X - x, dy = p.Y - y;
                if (dx * dx + dy * dy <= t2) return true;
            }
            return false;
        }

        [Fact]
        public void Aligner_RejectsWhenNothingMatches()
        {
            var dxf = new List<PlanPoint> { P(1000, 1000), P(2000, 2000), P(3000, 3000) };
            var revit = new List<PlanPoint> { P(90000, 90000), P(95000, 95000), P(98000, 98000) };

            var res = PlanAligner.Estimate(dxf, revit, 500);

            Assert.False(res.IsValid);
            Assert.True(res.Inliers < 3);
        }

        [Fact]
        public void Aligner_EmptyInputNotValid()
        {
            Assert.False(PlanAligner.Estimate(null, null, 500).IsValid);
            Assert.False(PlanAligner.Estimate(new List<PlanPoint>(), new List<PlanPoint>(), 500).IsValid);
        }

        [Fact]
        public void Aligner_UnitsConversionConstant()
        {
            Assert.Equal(304.8, PlanAligner.MmPerFoot, 3);
        }
    }
}