using System;
using System.Collections.Generic;
using System.Linq;

namespace GrdRevit.Core
{
    /// <summary>
    /// Сопоставляет настройки клапанов из valve-settings.txt с радиаторами главной
    /// таблицы по ключу «помещение + мощность Вт». Если по одному ключу несколько
    /// приборов (несколько отопительных приборов в одном помещении с одинаковой
    /// мощностью) и несколько настроек — пары образуются по порядку следования.
    /// </summary>
    public static class ValveSettingsMatcher
    {
        /// <summary>
        /// Заполняет поле ValveSetting у каждого прибора. Возвращает число строк,
        /// которым удалось сопоставить настройку.
        /// </summary>
        public static int Fill(List<GrdDevice> devices, List<ValveSettingRow> valveRows)
        {
            if (devices == null || valveRows == null || valveRows.Count == 0) return 0;

            var byKey = valveRows
                .Where(r => r.PowerW.HasValue)
                .GroupBy(r => Key(r.Room, r.PowerW.Value))
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Order).ToList());

            int filled = 0;
            // Считаем, сколько таких ключей уже сматчено — пары по порядку.
            var cursor = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var d in devices)
            {
                if (d == null || !d.CapacityW.HasValue) continue;
                var key = Key(d.Room, d.CapacityW.Value);
                if (!byKey.TryGetValue(key, out var rows)) continue;

                int idx = cursor.TryGetValue(key, out var c) ? c : 0;
                if (idx >= rows.Count) continue;

                d.ValveSetting = rows[idx].Setting;
                cursor[key] = idx + 1;
                filled++;
            }

            return filled;
        }

        private static string Key(string room, double power)
        {
            int w = (int)Math.Round(power, MidpointRounding.AwayFromZero);
            return (room ?? string.Empty).Trim().ToUpperInvariant() + "\u0001" + w.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}