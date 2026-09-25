using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace GrdRevit.Core
{
    /// <summary>
    /// Разбор файла «помещения и радиаторы» (room-radiator.txt, TAB-разделители).
    /// Колонки:
    ///   0 — помещение (БКТ 102)
    ///   1 — марка помещения/код (155) — не используется
    ///   2 — буквенный маркер прибора (D/C/B/A) — не используется
    ///   3 — код прибора (TPLNE2518T2L, GS-4-80 ...)
    ///   4 — длина подводки (1.175 м)
    ///   5 — диаметр (25, 20 ...)
    ///   6 — мощность, Вт (1503)  — Фhl
    ///   7+ — технические колонки (не используются)
    /// </summary>
    public static class RoomsRadiatorsParser
    {
        public static List<GrdDevice> Parse(string path)
        {
            var devices = new List<GrdDevice>();
            var lines = File.ReadAllLines(path);
            int order = 0;
            foreach (var raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var f = raw.Split('\t');
                if (f.Length < 7) continue;

                var room = f[0].Trim();
                var code = f[3].Trim();
                if (code.Length == 0) continue;

                double capacity = 0;
                if (!TryParseNumber(f[6], out capacity) || capacity <= 0) capacity = 0;

                var dev = new GrdDevice
                {
                    Room = room,
                    Code = code,
                    CapacityW = capacity > 0 ? capacity : (double?)null,
                    Diameter = f[5].Trim(),
                    Order = order++
                };

                // Подсказка семейства из кода прибора (для FamilyTypeResolver).
                if (DeviceCodeParser.TryParse(code, out var parsed))
                {
                    dev.FamilyHint = parsed.FamilyHint;
                    dev.Connection = parsed.Connection;
                    dev.Code = parsed.Code;
                    if (!dev.CapacityW.HasValue) dev.CapacityW = parsed.CapacityW;
                }

                devices.Add(dev);
            }
            return devices;
        }

        private static bool TryParseNumber(string s, out double v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().Replace(',', '.').Replace(" м", "").Trim();
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }
}