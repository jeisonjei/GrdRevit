using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GrdRevit.Core
{
    /// <summary>
    /// Выбор способа записи числового значения в параметр так, чтобы отображаемое
    /// значение совпало с исходным («Фhl» -> параметр экземпляра). Логика не зависит
    /// от Revit API и покрывается юнит-тестами (tests/GrdRevit.Core.Tests).
    ///
    /// Порядок попыток (сам способ записи, без ожидания отклика: Revit может не
    /// обновлять AsValueString внутри активной транзакции — отклик читается
    /// ПОСЛЕ коммита, см. RoundTripVerify.Repair):
    ///  1) «string» — Parameter.SetValueString(число): Revit сам разбирает строку
    ///     в единицах параметра, поэтому отображение, как правило, равно числу.
    ///  2) «internal» — Set(ConvertToInternalUnits(число, спецификация параметра));
    ///     если конвертация недоступна (спецификация не поддерживается) —
    ///     способ пропускается.
    ///  3) «display» — Set(число) как есть.
    /// Возвращает способ и отображение сразу после записи (может быть «устаревшим»
    /// внутри транзакции — на него не полагаемся для успеха).
    /// </summary>
    public static class ValueWriteSelector
    {
        public const double DefaultTolerance = 0.005;

        /// <summary>
        /// Выбирает ОДИН способ записи по приоритету. Отображение читается только
        /// для лога; решение об успехе принимается после коммита (RoundTripVerify).
        /// </summary>
        public static (string mode, string shown) ChooseWrite(
            double desired,
            Func<double, double> convertToInternal,
            Action<double> setValue,
            Func<string, bool> setValueString,
            Func<string> readback)
        {
            if (setValueString != null)
            {
                bool ok = false;
                try { ok = setValueString(FormatNumber(desired)); }
                catch { ok = false; }
                if (ok) return ("string", SafeReadback(readback));
            }

            double internalCandidate = double.NaN;
            try { internalCandidate = convertToInternal(desired); }
            catch { internalCandidate = double.NaN; }

            if (!double.IsNaN(internalCandidate))
            {
                try
                {
                    setValue(internalCandidate);
                    return ("internal", SafeReadback(readback));
                }
                catch { }
            }

            try
            {
                setValue(desired);
                return ("display", SafeReadback(readback));
            }
            catch { }

            return ("none", null);
        }

        private static string FormatNumber(double d)
        {
            return d.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static string SafeReadback(Func<string> readback)
        {
            try { return readback(); }
            catch { }
            return null;
        }

        /// <summary>Сверяет числовую часть отображаемого значения с эталоном (пустая строка и null никогда не считаются совпадением).</summary>
        public static bool Matches(string display, double expected, double tolerance = DefaultTolerance)
        {
            if (!TryParseNumber(display, out var shown)) return false;
            return Math.Abs(shown - expected) <= tolerance * Math.Max(1.0, Math.Abs(expected));
        }

        /// <summary>Извлекает число из отображаемого значения (например «1260 Вт» -> 1260).</summary>
        public static bool TryParseNumber(string display, out double value)
        {
            value = 0;
            if (string.IsNullOrEmpty(display)) return false;
            var m = Regex.Match(display, @"[-+]?[0-9][0-9\s\u00A0,.]*");
            if (!m.Success) return false;
            var norm = Regex.Replace(m.Value, @"\s+", string.Empty).Replace(',', '.');
            return double.TryParse(norm, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}