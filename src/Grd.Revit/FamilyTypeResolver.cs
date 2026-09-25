using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using GrdRevit.Core;

namespace GrdRevit.Revit
{
    /// <summary>
    /// Специфический парсер: сопоставляет код прибора из .grd (TPLCPL714T2L, HZ-814, GS-4-80)
    /// с типом семейства Revit в активном документе.
    /// Приоритет: точная карта кода -> имя типа (настройки), затем имя семейства из подсказки,
    /// затем подбор по мощности/названию.
    /// </summary>
    public static class FamilyTypeResolver
    {
        public static bool TryResolve(Document doc, GrdDevice device, GrdSettings settings, out FamilySymbol result, out string log)
        {
            result = null;
            log = string.Empty;

            if (doc == null) { log = "Документ недоступен"; return false; }
            if (device == null) { log = "Прибор пуст"; return false; }

            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Family != null)
                .ToList();

            // 0) Прямое указание пользователя: марка из DXF -> семейство и тип Revit.
            //    Заполняется в окне «Марки DXF» (настройки плана) и имеет наивысший приоритет.
            var byMark = FindMarkTypeEntry(device, settings);
            if (byMark != null)
            {
                FamilySymbol found = null;
                if (!string.IsNullOrEmpty(byMark.Family))
                {
                    // Семейство задано: ищем тип в семействе. Если тип тоже задан —
                    // ровно по имени типа, иначе первый тип семейства.
                    var inFamily = symbols
                        .Where(s => string.Equals(s.Family.Name, byMark.Family, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (inFamily.Count > 0)
                    {
                        found = string.IsNullOrEmpty(byMark.Type)
                            ? inFamily[0]
                            : inFamily.FirstOrDefault(s => string.Equals(s.Name, byMark.Type, StringComparison.OrdinalIgnoreCase));
                    }
                    if (found != null)
                    {
                        result = found;
                        log = $"Марка: {found.Family.Name} :: {found.Name}";
                        return true;
                    }
                    log = $"Марка \u201C{device.Mark}\u201D: семейство \u201C{byMark.Family}\u201D";
                    if (!string.IsNullOrEmpty(byMark.Type)) log += " :: тип \u201C" + byMark.Type + "\u201D";
                    log += " не найдено в документе.";
                }
                else if (!string.IsNullOrEmpty(byMark.Type))
                {
                    // Старая запись без семейства: ищем по имени типа или семейства.
                    found = symbols.FirstOrDefault(s =>
                        string.Equals(s.Name, byMark.Type, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.Family.Name, byMark.Type, StringComparison.OrdinalIgnoreCase));
                    if (found != null)
                    {
                        result = found;
                        log = $"Марка: {found.Family.Name} :: {found.Name}";
                        return true;
                    }
                    log = $"Марка \u201C{device.Mark}\u201D: тип \u201C{byMark.Type}\u201D не найден в документе.";
                }
            }

            // 1) Точное соответствие кода -> имя типа. Прямой и нормализованный варианты:
            //    код из .grd может отличаться от ключа карты суффиксом подключения
            //    ("TPLCPL2000T2L" против "TPLCPL2000L") или пробелом ("TPLCPL1875 L").
            string exactTypeName = FindExactTypeName(device, settings, out string exactLog);
            if (!string.IsNullOrEmpty(exactTypeName))
            {
                var exact = symbols.FirstOrDefault(s =>
                    string.Equals(s.Name, exactTypeName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Family.Name, exactTypeName, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    result = exact;
                    log = $"Точная карта: {exact.Family.Name} :: {exact.Name}";
                    return true;
                }
                log = $"Не найден тип по карте \u201C{exactTypeName}\u201D";
            }
            else if (!string.IsNullOrEmpty(exactLog))
            {
                log = exactLog;
            }

            // 2) Семейство по подсказке (с учётом переопределений из настроек).
            string familyHint = FamilyHintFor(doc, device, settings);
            var candidates = symbols
                .Where(s => FamilyMatches(s.Family.Name, familyHint))
                .ToList();

            if (candidates.Count == 0)
            {
                // Пробуем без переопределений — по хинту.
                candidates = symbols.Where(s => FamilyMatches(s.Family.Name, device.FamilyHint)).ToList();
            }

            if (candidates.Count == 0)
            {
                log = $"Семейство \u201C{familyHint}\u201D не найдено в документе. " +
                      "Загрузите семейство или добавьте карту \u201Cкод -> имя типа\u201D в настройках.";
                return false;
            }

            // 3) Среди кандидатов выбираем по мощности/имени.
            var best = candidates
                .Select(s => new { S = s, Score = Score(s, device) })
                .OrderByDescending(x => x.Score)
                .First();

            if (best.Score <= 0)
            {
                log = $"Не удалось подобрать тип по мощности кода {device.Code}; " +
                      $"найдено семейств: {candidates.Count}.";
                return false;
            }

            result = best.S;
            log = $"Подобрано: {best.S.Family.Name} :: {best.S.Name} (score {best.Score})";
            return true;
        }

        /// <summary>Ищет привязку «семейство/тип Revit» по марке прибора в карте марок из DXF.</summary>
        private static MarkTypeEntry FindMarkTypeEntry(GrdDevice device, GrdSettings settings)
        {
            var map = settings?.MarkTypeMap;
            if (map == null || map.Count == 0) return null;

            var raw = device?.Mark?.Trim();
            if (string.IsNullOrEmpty(raw)) return null;

            if (map.TryGetValue(raw, out var direct) && direct != null && !direct.IsEmpty)
                return direct;

            // Нормализованное совпадение: без учёта пробелов/регистра,
            // чтобы "Classic Plus L 1,375 (…)" совпал несмотря на вариации пробелов.
            var norm = NormalizeMarkText(raw);
            foreach (var kv in map)
            {
                if (kv.Value != null && !kv.Value.IsEmpty &&
                    string.Equals(NormalizeMarkText(kv.Key), norm, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Value;
                }
            }
            return null;
        }

        /// <summary>
        /// Есть ли у прибора явная привязка в карте типов («Марки DXF → семейство/тип»).
        /// Массовое применение к текущему виду назначает ТОЛЬКО такие семейства —
        /// без угадывания по мощности/подсказке, чтобы не подставить случайный тип.
        /// </summary>
        public static bool HasMarkTypeEntry(GrdDevice device, GrdSettings settings)
        {
            return FindMarkTypeEntry(device, settings) != null;
        }

        private static string NormalizeMarkText(string s)
        {
            var up = (s ?? string.Empty).Trim().ToUpperInvariant();
            var sb = new System.Text.StringBuilder();
            foreach (char c in up)
                if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>Ищет имя типа в карте кода по прямому и нормализованному (без суффикса подключения) совпадению.</summary>
        private static string FindExactTypeName(GrdDevice device, GrdSettings settings, out string note)
        {
            note = string.Empty;
            var map = settings?.TypeNameExactMap;
            if (map == null || map.Count == 0) return null;

            var raw = device.Code?.Trim();
            if (!string.IsNullOrEmpty(raw) && map.TryGetValue(raw, out var direct) && !string.IsNullOrEmpty(direct))
                return direct;

            // Нормализованное совпадение: без суффикса подключения и пробелов.
            var norm = NormalizeCode(raw);
            if (!string.IsNullOrEmpty(norm))
            {
                foreach (var kv in map)
                {
                    var keyNorm = NormalizeCode(kv.Key);
                    if (string.Equals(keyNorm, norm, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(kv.Value))
                    {
                        return kv.Value;
                    }
                }
                note = "Код прибора не совпал с ключами карты (проверьте схему/суффиксы).";
            }
            return null;
        }

        /// <summary>
        /// Приводит код к "логическому ядру": буквенный префикс + первые цифры (3–4 знака),
        /// без суффикса подключения (T2L/T2/TO), букв "L/R" и пробелов.
        /// "TPLCPL2000T2L", "TPLCPL2000L", "TPLCPL2000" -> "TPLCPL2000".
        /// </summary>
        private static string NormalizeCode(string code)
        {
            var up = (code ?? string.Empty).Trim().ToUpperInvariant();
            if (up.Length == 0) return string.Empty;
            var m = System.Text.RegularExpressions.Regex.Match(up,
                @"^[A-Z0-9]*\d{3,4}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            return m.Success ? m.Value : up;
        }

        private static string FamilyHintFor(Document doc, GrdDevice device, GrdSettings settings)
        {
            // Переопределение по префиксу кода.
            if (settings.FamilyNameOverrides != null)
            {
                foreach (var kv in settings.FamilyNameOverrides)
                {
                    if (device.Code.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
                }
            }
            return device.FamilyHint;
        }

        private static bool FamilyMatches(string familyName, string hint)
        {
            if (string.IsNullOrEmpty(hint)) return false;
            if (familyName.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            // Совпадение по токенам: убираем пробелы/подчёркивания,
            // чтобы "TEPLA Classic Plus" совпал с "556_TEPLA_..._Classic_Plus".
            var fTokens = Tokens(familyName);
            var hTokens = Tokens(hint);
            if (hTokens.Count == 0) return false;
            if (hTokens.Count == 1)
                return fTokens.Contains(hTokens[0]);
            for (int i = 0; i <= fTokens.Count - hTokens.Count; i++)
            {
                bool match = true;
                for (int j = 0; j < hTokens.Count; j++)
                {
                    if (!string.Equals(fTokens[i + j], hTokens[j], StringComparison.OrdinalIgnoreCase))
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return true;
            }
            return false;
        }

        private static List<string> Tokens(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            var sb = new System.Text.StringBuilder();
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
            }
            if (sb.Length > 0) list.Add(sb.ToString());
            return list;
        }

        // Оценка соответствия типа мощности кода прибора.
        private static int Score(FamilySymbol s, GrdDevice device)
        {
            int score = 0;
            double? want = device.CapacityW;

            string typeName = s.Name ?? string.Empty;
            var digits = System.Text.RegularExpressions.Regex.Match(typeName, @"\d[\d.,]*(?:\s*кВт|kw|W|Вт)?", RegexX);
            if (digits.Success)
            {
                var num = ExtractNumber(digits.Value);
                if (num.HasValue)
                {
                    double numW = num.Value < 10 ? num.Value * 1000.0 : num.Value;
                    if (want.HasValue && Math.Abs(numW - want.Value) < 5)
                    {
                        score += 100; // точное совпадение в названии
                    }
                    else if (want.HasValue && Math.Abs(numW - want.Value) < 50)
                    {
                        score += 40;
                    }
                }
                score += 20;
            }

            if (typeName.IndexOf(device.Code, StringComparison.OrdinalIgnoreCase) >= 0) score += 80;
            if (typeName.IndexOf(device.FamilyHint, StringComparison.OrdinalIgnoreCase) >= 0) score += 10;

            // Параметр тепловой мощности в типе.
            double? paramPower = GetParameterPower(s);
            if (paramPower.HasValue && want.HasValue)
            {
                if (Math.Abs(paramPower.Value - want.Value) < 5) score += 60;
                else if (Math.Abs(paramPower.Value - want.Value) < 50) score += 25;
            }

            return score;
        }

        private static double? GetParameterPower(FamilySymbol s)
        {
            var names = new[] { "Мощность", "Тепловая мощность", "Теплопроизводительность", "Power" };
            foreach (var n in names)
            {
                var p = s.LookupParameter(n);
                if (p != null && p.StorageType == StorageType.Double)
                {
                    double v = p.AsDouble();
                    return v > 100 ? v * 0.2931 : v * 1000.0; // fallback
                }
            }
            return null;
        }

        private static double? ExtractNumber(string s)
        {
            s = s.Trim();
            if (s.Length == 0) return null;
            // убираем суффиксы
            s = s.Replace("кВт", "").Replace("kw", "").Replace("KW", "").Replace("w", "").Replace("W", "").Trim();
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var inv)) return inv;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out var cur)) return cur;
            return null;
        }

        private static readonly System.Text.RegularExpressions.RegexOptions RegexX =
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant;
    }
}