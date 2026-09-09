using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace GrdRevit.Core
{
    /// <summary>
    /// Разбор файла настроек клапанов (valve-settings.txt, TAB-разделители).
    /// Колонки:
    ///   0 — помещение (БКТ 102)
    ///   1 — диаметр (20, 15 ...)
    ///   2 — код клапана/прибора (КТС-ВП2, D-2500015, MD-00015, 013G7014R ...)
    ///   3 — НАСТРОЙКА КЛАПАНА (1, 2, 1.5, 3 ...) — значение для колонки «Настройка клапана»
    ///   4 — … (не используется)
    ///   5 — пропускная способность Kv (0.93 ...)
    ///   6 — мощность, Вт (1503) — ключ сопоставления с приборами
    ///   7+ — технические колонки (не используются)
    /// </summary>
    public static class ValveSettingsParser
    {
        public static List<ValveSettingRow> Parse(string path)
        {
            var rows = new List<ValveSettingRow>();
            var lines = File.ReadAllLines(path);
            int order = 0;
            foreach (var raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var f = raw.Split('\t');
                if (f.Length < 7) continue;

                var room = f[0].Trim();
                var code = f[2].Trim();
                if (room.Length == 0) continue;

                double power = 0;
                if (!TryParseNumber(f[6], out power) || power <= 0) power = 0;

                rows.Add(new ValveSettingRow
                {
                    Room = room,
                    Diameter = f[1].Trim(),
                    DeviceCode = code,
                    Setting = f[3].Trim(),
                    Kv = f.Length > 5 ? f[5].Trim() : string.Empty,
                    PowerW = power > 0 ? power : (double?)null,
                    Order = order++
                });
            }
            return rows;
        }

        private static bool TryParseNumber(string s, out double v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }
}