using System;

namespace GrdRevit.Core
{
    /// <summary>
    /// Проверка и починка значения параметра ПОСЛЕ коммита транзакции.
    ///
    /// Зачем: Revit может не обновлять AsValueString внутри активной транзакции
    /// (наблюдается: после p.Set(...) чтение в той же транзакции возвращает значение
    /// из ПРЕДЫДУЩЕЙ транзакции). Поэтому точное значение гарантируется так:
    ///  1) пишем значение -> коммит;
    ///  2) читаем СО СВЕЖЕГО состояния документа;
    ///  3) если отображение не совпало с желаемым и зависимость «отображение = k*хранимое»
    ///     является линейной, вычисляем корректное хранимое значение и дописываем его
    ///     отдельной транзакцией;
    ///  4) повторяем до сходимости (максимум maxAttempts попыток).
    ///
    /// Логика чистая и покрыта юнит-тестами (например k = м²/фут² = 0.0929, когда
    /// запись 1160 по любой из API-дорог отображается как «~107.8 Вт»).
    /// </summary>
    public static class RoundTripVerify
    {
        /// <summary>
        /// Доводит значение до желаемого. 
        /// </summary>
        /// <param name="desired">Значение, которое должно отображаться.</param>
        /// <param name="readDouble">Текущее хранимое значение параметра (AsDouble, внутренние единицы).</param>
        /// <param name="readDisplay">Текущее отображаемое значение (AsValueString).</param>
        /// <param name="write">Запись хранимого значения (после вызова починки вызов читается заново).</param>
        /// <param name="tolerance">Допуск относительного совпадения.</param>
        public static (bool ok, double stored, string shown) Repair(
            double desired,
            Func<double> readDouble,
            Func<string> readDisplay,
            Action<double> write,
            double tolerance = ValueWriteSelector.DefaultTolerance)
        {
            return RepairCore(desired, readDouble, readDisplay, write, 5, tolerance);
        }

        internal static (bool ok, double stored, string shown) RepairCore(
            double desired,
            Func<double> readDouble,
            Func<string> readDisplay,
            Action<double> write,
            int maxAttempts,
            double tolerance)
        {
            string shown = SafeRead(readDisplay);
            double stored = SafeRead(readDouble, double.NaN);

            for (int i = 0; i < maxAttempts; i++)
            {
                if (ValueWriteSelector.Matches(shown, desired, tolerance)) return (true, stored, shown);

                if (double.IsNaN(stored) || stored <= 0 ||
                    !ValueWriteSelector.TryParseNumber(shown, out double sd) || sd <= 0)
                {
                    return (false, stored, shown);
                }

                double next = desired * stored / sd; // отображение (sd) = k*stored -> нужно stored' = desired/k
                try { write(next); }
                catch { return (false, stored, shown); }

                shown = SafeRead(readDisplay);
                stored = SafeRead(readDouble, double.NaN);
            }

            return (ValueWriteSelector.Matches(shown, desired, tolerance), stored, shown);
        }

        private static T SafeRead<T>(Func<T> f, T fallback = default)
        {
            try { return f(); }
            catch { }
            return fallback;
        }

        private static string SafeRead(Func<string> f)
        {
            try { return f(); }
            catch { }
            return null;
        }
    }
}