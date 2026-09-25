using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace GrdRevit.Core
{
    /// <summary>
    /// Разбор текста выноски прибора (марки) в модель GrdDevice.
    /// Примеры:
    ///   "Classic Plus L 1,375 (К; НР 3/4; T2; НП; прав.)" -> TEPLA Classic Plus, 1375 Вт, T2L, длина 1375 мм
    ///   "Classic Super L 1,692 (К; НР 3/4; T2; НП; прав.)" -> TEPLA Classic Super, 1692 Вт
///     "Classic Plus 0,714 (К; НР 3/4; ТН.)"             -> 714 Вт
    ///   "Neo Expo 156(?)x320(?) 3,345 (В;НР 3/4; T2; СВ.;Л)" -> TEPLA NEO EXPO, 3345 Вт
    ///   "DN80-4р-2000" (или "DN80-4-2000")                   -> стальной регистр, длина 2000 мм
    /// Полный текст выноски сохраняется как Mark.
    /// </summary>
    public static class CalloutDeviceParser
    {
        private static readonly Regex Series = new Regex(
            @"(?<series>Classic\s*(Plus|Super|DG)?|Neo\s*Expo|Surpa|HZ|GS)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Регистр из труб: "DN80-4р-2000" — диаметр, количество рядов, длина в мм.
        /// Буква «р» (кириллица или латиница) после числа рядов необязательна.</summary>
        private static readonly Regex DnRegister = new Regex(
            @"^DN\s*(?<diam>\d+)\s*[–—-]\s*(?<rows>\d+)\s*[рpРP]?\s*[–—-]\s*(?<len>\d+)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Numbers = new Regex(@"\d+(?:[,.]\d+)?", RegexOptions.Compiled);

        /// <summary>Распознать текст выноски как отопительный прибор.</summary>
        public static bool TryParse(string text, out GrdDevice dev)
        {
            dev = new GrdDevice { Mark = text?.Trim() };
            if (string.IsNullOrWhiteSpace(text)) return false;

            // Регистр из труб ("DN80-4р-2000"): мощность в выноске не задана — берём из
            // выноски только код и длину. Физическая мощность вычисляется отдельно.
            var rn = DnRegister.Match(text);
            if (rn.Success)
            {
                string diam = rn.Groups["diam"].Value;
                string rows = rn.Groups["rows"].Value;
                string len = rn.Groups["len"].Value;
                dev.Kind = DeviceKind.RadiatorHeating;
                dev.FamilyHint = "Регистр";
                dev.Code = "REG-DN" + diam + "-" + rows + "-" + len;
                if (double.TryParse(len, NumberStyles.Integer, CultureInfo.InvariantCulture, out double mm))
                    dev.LengthMm = mm;
                return true;
            }

            var sm = Series.Match(text);
            if (!sm.Success) return false;
            string series = sm.Groups["series"].Value.ToUpperInvariant();

            // Значимо последнее число в «голове» (до последней скобки): для Neo Expo это 3345, а не 156x320.
            int end = text.LastIndexOf('(');
            string head = end >= 0 ? text.Substring(0, end) : text;

            // Заглушки-шаблоны: "Classic Plus L T2 (Х)" или "Neo Expo 156x320 (???.)".
            if (Regex.IsMatch(head, @"L\s*[Tt]\d")) return false;
            if (Regex.IsMatch(head, @"[xх]\s*\d+\s*$")) return false;

            var nums = Numbers.Matches(head).Cast<Match>().Select(m => m.Value).ToList();
            if (nums.Count == 0) return false;
            string capRaw = nums[nums.Count - 1];

            if (!TryParseKw(capRaw, out double cx)) return false;
            int watts = (int)Math.Round(cx * 1000.0);

            string seriesName = SeriesName(series);
            dev.FamilyHint = seriesName;
            dev.CapacityW = watts;
            dev.Code = BuildCode(series, watts, text);
            dev.Kind = DeviceKind.RadiatorHeating;

            // Числу предшествует «L» -> длина прибора в метрах.
            int idx = head.LastIndexOf(capRaw, StringComparison.Ordinal);
            string before = idx > 0 ? head.Substring(0, idx) : string.Empty;
            if (Regex.IsMatch(before, @"L\s*$"))
                dev.LengthMm = watts;

            if (text.IndexOf("T2", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                dev.Connection = "T2L";
                dev.IsTwinPipe = "двухтрубное";
            }
            else if (text.IndexOf("T1", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                dev.Connection = "TO";
            }

            return true;
        }

        /// <summary>Десятичное число из выноски с учётом запятой/точки и NBSP.</summary>
        private static bool TryParseKw(string v, out double kw)
        {
            kw = 0;
            if (string.IsNullOrWhiteSpace(v)) return false;
            var norm = v.Replace('\u00A0', ' ').Replace(" ", string.Empty).Trim();
            if (norm.Length == 0) return false;
            var asDot = norm.Replace(',', '.');
            if (double.TryParse(asDot, NumberStyles.Float, CultureInfo.InvariantCulture, out kw))
                return true;
            var asComma = norm.Replace('.', ',');
            return double.TryParse(asComma, NumberStyles.Float, CultureInfo.InvariantCulture, out kw);
        }

        private static string SeriesName(string series)
        {
            switch (series)
            {
                case "CLASSIC PLUS":
                case "CLASSIC":
                    return "TEPLA Classic Plus";
                case "CLASSIC SUPER":
                    return "TEPLA Classic Super";
                case "CLASSIC DG":
                    return "TEPLA Classic DG";
                case "NEO EXPO":
                    return "TEPLA NEO EXPO";
                case "HZ": return "HZ";
                case "GS": return "GS";
                case "SURPA": return "TEPLA Surpa";
                default: return "TEPLA";
            }
        }

        private static string BuildCode(string series, int watts, string text)
        {
            string sc = series == "NEO EXPO" ? "NE"
                : series == "CLASSIC SUPER" ? "SC"
                : series == "CLASSIC DG" ? "CDG" : "CPL";
            string conn = text.IndexOf("T2", StringComparison.OrdinalIgnoreCase) >= 0
                ? "T2L"
                : text.IndexOf("T1", StringComparison.OrdinalIgnoreCase) >= 0 ? "TO" : string.Empty;
            return "TPL" + sc + watts.ToString(CultureInfo.InvariantCulture) + conn;
        }
    }
}