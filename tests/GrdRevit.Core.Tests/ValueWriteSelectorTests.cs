using System;
using GrdRevit.Core;
using Xunit;

namespace GrdRevit.Core.Tests
{
    /// <summary>
    /// Гарантия: значение «Фhl» (например 1300 или 1370 Вт) записывается в параметр
    /// экземпляра таким образом, что отображаемое значение параметра равно исходному.
    /// Наблюдаемый баг: 1160 -> "98 Вт" (Revit хранит часть единиц в Вт/фут² и
    /// отображает «k*хранимое», k = м²/фут² = 0.09290304), а чтение внутри транзакции
    /// отстаёт на один коммит. Поэтому: способ записи выбирается по приоритету,
    /// а точность добивается ПОСЛЕ коммита (RoundTripVerify.Repair).
    /// </summary>
    public class ValueWriteSelectorTests
    {
        // == Коэффициент из бага: Вт/м² vs Вт/фут². ==
        private const double M2_PER_FT2 = 0.09290304;

        private static string Fmt(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        [Fact]
        public void Matches_RecognizesExactValue_AndRejectsConverted()
        {
            Assert.True(ValueWriteSelector.Matches("1370 Вт", 1370));
            Assert.True(ValueWriteSelector.Matches("1 370 Вт", 1370));
            Assert.True(ValueWriteSelector.Matches("1370,0 Вт/м²", 1370));
            Assert.True(ValueWriteSelector.Matches("1300 Вт", 1300));

            Assert.False(ValueWriteSelector.Matches("127.28 Вт", 1370));
            Assert.False(ValueWriteSelector.Matches("14747 Вт/м²", 1370));
            Assert.False(ValueWriteSelector.Matches(null, 1370));
            Assert.False(ValueWriteSelector.Matches(string.Empty, 1370));
        }

        [Fact]
        public void ChooseWrite_PrefersSetValueString()
        {
            var (mode, shown) = ValueWriteSelector.ChooseWrite(
                1300,
                c => c,
                c => throw new Exception("Set(double) не должен вызываться"),
                s => { Assert.Equal("1300", s); return true; },
                () => "1300 Вт");

            Assert.Equal("string", mode);
            Assert.Equal("1300 Вт", shown);
        }

        [Fact]
        public void ChooseWrite_FallsToInternal_WhenSetValueStringUnavailable()
        {
            var (mode, _) = ValueWriteSelector.ChooseWrite(
                1300,
                c => c * M2_PER_FT2,
                c => { },
                null,
                () => Fmt(0));

            Assert.Equal("internal", mode);
        }

        [Fact]
        public void ChooseWrite_FallsToDisplay_WhenInternalConversionFails()
        {
            var (mode, _) = ValueWriteSelector.ChooseWrite(
                1300,
                c => { throw new Exception("ConvertToInternalUnits недоступен"); },
                c => { },
                null,
                () => Fmt(0));

            Assert.Equal("display", mode);
        }

        [Fact]
        public void ChooseWrite_ReturnsNone_WhenEveryWriteThrows()
        {
            var (mode, shown) = ValueWriteSelector.ChooseWrite(
                1370,
                c => c,
                c => throw new Exception("fail"),
                s => throw new Exception("fail"),
                () => "любое");

            Assert.Equal("none", mode);
            Assert.Null(shown);
        }

        [Fact]
        public void RoundTripVerify_RepairsLinearFamily_ToExactValue()
        {
            // Параметр отображает «хранимое*0.0929» (1160 Вт -> "~107.8 Вт").
            // Коррекция должна добиться отображения ровно 1160.
            foreach (var v in new[] { 780.0, 1160.0, 1260.0, 1370.0 })
            {
                double stored = v * M2_PER_FT2;   // что реально оказалось после первой записи
                var (ok, storedFinal, shown) = RoundTripVerify.Repair(
                    v,
                    () => stored,
                    () => Fmt(stored * M2_PER_FT2),
                    next => { stored = next; });

                Assert.True(ok, $"для {v} должно сойтись, показано \"{shown}\"");
                Assert.True(ValueWriteSelector.Matches(shown, v), $"отображение должно быть {v}, а не \"{shown}\"");
            }
        }

        [Fact]
        public void RoundTripVerify_RepairsValueLeftByPreviousCalibration()
        {
            // Лог наблюдаемого применения: предыдущая «калибровка» оставила 13730.6,
            // отображение «1276 Вт», а нужно 1160. Свежая коррекция должна направить к 1160.
            double stored = 13730.612244898;
            var (ok, _, shown) = RoundTripVerify.Repair(
                1160,
                () => stored,
                () => Fmt(stored * M2_PER_FT2),
                next => { stored = next; });

            Assert.True(ok);
            Assert.True(ValueWriteSelector.Matches(shown, 1160), $"отображение \"{shown}\" должно быть 1160");
        }

        [Fact]
        public void RoundTripVerify_NeverClaimsSuccess_WhenParameterDoesNotReact()
        {
            // Параметр ни на что не реагирует (вычисляется формулой): коррекция
            // не должна ложно заявлять успех.
            double stored = 500;
            var (ok, storedFinal, shown) = RoundTripVerify.Repair(
                1260,
                () => stored,
                () => "500 Вт",
                _ => { }); // запись игнорируется, отображение константа

            Assert.False(ok);
            Assert.False(ValueWriteSelector.Matches(shown, 1260));
        }

        [Fact]
        public void ExactFhlValues_AreNotTreatedAsConverted()
        {
            foreach (var v in new[] { 500.0, 1300.0, 1370.0, 1500.0, 2250.0 })
            {
                Assert.True(ValueWriteSelector.Matches($"{Fmt(v)} Вт", v), $"значение {v} должно совпадать");
                Assert.False(ValueWriteSelector.Matches($"{Fmt(v * M2_PER_FT2)} Вт", v), $"конвертированное {v} не должно совпадать");
            }
        }
    }
}