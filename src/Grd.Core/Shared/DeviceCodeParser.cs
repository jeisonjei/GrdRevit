using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GrdRevit.Core
{
    /// <summary>
    /// Разбор кода прибора из .grd.
    /// Примеры:
    ///   TPLCPL714T2L   -> TEPLA Classic Plus, 714 Вт, двухтрубное (T2L)
    ///   TPLCPL1500T2L  -> 1500 Вт
    ///   TPLCDG1611     -> TEPLA Classic DG, 1611 Вт
    ///   TPLNE1969T2L   -> TEPLA NEO EXPO, 1969 Вт
    ///   HZ-814         -> 814 Вт
    ///   GS-4-80        -> стальная панельная GS, 80 Вт (по коду), параметры задаются настройками
    /// </summary>
    public static class DeviceCodeParser
    {
        public const string NoMatchKey = "Нераспознанно";

        private static readonly Regex Tpl = new Regex(
            @"^TPL(?<series>CPLDG|CPL|CDG|NE|C)\s*(?<cap>\d{3,4})\s*(?<conn>T2L|T2|TO)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Hz = new Regex(
            @"^HZ[\s-]*(?<cap>\d{3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Gs = new Regex(
            @"^GS[\s-]*(?<type>\d)[\s-]*(?<cap>\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Распознать, является ли токен кодом отопительного прибора.</summary>
        public static bool TryParse(string token, out GrdDevice dev)
        {
            dev = new GrdDevice { Code = token, Kind = DeviceKind.RadiatorHeating };
            if (string.IsNullOrWhiteSpace(token)) return false;

            var t = token.Trim().Trim('.', ',', ';', ' ', '!');
            if (t.Length == 0) return false;

            var m = Tpl.Match(t);
            if (m.Success)
            {
                string series = m.Groups["series"].Value.ToUpperInvariant();
                dev.FamilyHint = SeriesName(series);
                dev.CapacityW = ParseCap(m.Groups["cap"].Value);
                dev.Connection = m.Groups["conn"].Value;
                dev.IsTwinPipe = !string.IsNullOrEmpty(dev.Connection) && dev.Connection.StartsWith("T2", StringComparison.Ordinal) ? "двухтрубное" : string.Empty;
                return true;
            }

            var mh = Hz.Match(t);
            if (mh.Success)
            {
                dev.FamilyHint = "HZ";
                dev.CapacityW = ParseCap(mh.Groups["cap"].Value);
                // Нормализуем "HZ 814" -> "HZ-814", чтобы группировка была единой.
                dev.Code = "HZ-" + mh.Groups["cap"].Value;
                return true;
            }

            var mg = Gs.Match(t);
            if (mg.Success)
            {
                dev.FamilyHint = "GS";
                dev.CapacityW = ParseCap(mg.Groups["cap"].Value);
                dev.Connection = "панельное " + mg.Groups["type"].Value;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Парсит код прибора, но без гарантии, что это прибор (для классификации токенов).
        /// </summary>
        public static bool CanStartWith(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return false;
            var up = t.TrimStart().ToUpperInvariant();
            return up.StartsWith("TPL", StringComparison.Ordinal)
                || Regex.IsMatch(up, @"^HZ[\s-]?\d")
                || Regex.IsMatch(up, @"^GS[\s-]?\d[\s-]?\d");
        }

        private static string SeriesName(string series)
        {
            switch (series)
            {
                case "CPL":
                case "C":
                    return "TEPLA Classic Plus";
                case "CDG":
                case "CPLDG":
                    return "TEPLA Classic DG";
                case "NE":
                    return "TEPLA NEO EXPO";
                default:
                    return "TEPLA";
            }
        }

        private static double? ParseCap(string s)
        {
            if (double.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return v;
            return null;
        }

        /// <summary>Человеко-читаемое название типа для таблицы.</summary>
        public static string DisplayName(GrdDevice d)
        {
            if (d == null) return string.Empty;
            if (d.CapacityW.HasValue)
            {
                double kw = d.CapacityW.Value / 1000.0;
                return string.Format(CultureInfo.InvariantCulture, "{0} — {1:0.###} кВт", d.FamilyHint, kw);
            }
            return d.FamilyHint + " — " + d.Code;
        }
    }
}