using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace GrdRevit.Revit
{
    /// <summary>Сопоставление имён параметров Revit между разными источниками.
    ///
    /// Параметр может называться по-разному в разных местах: в файле общих параметров
    /// («Расход»), в диалоге «Параметры семейств», в определении семейства и в самом
    /// проекте («Flow» — внутреннее имя встроенного параметра). Строка-сравнение здесь
    /// не годится, поэтому используется каскад правил от строгого к приблизительному:
    /// GUID общего параметра → точное имя → нормализованное имя (без регистра,
    /// пробелов, подчёркиваний, дефисов и типографских различий) → группа синонимов
    /// RU/EN → нормализованное имя встроенного параметра (BuiltInParameter) →
    /// вхождение одного имени в другое.
    /// </summary>
    public static class ParamNames
    {
        /// <summary>Результат подбора: параметр и правило, по которому он найден.</summary>
        public sealed class Match
        {
            public Parameter Parameter;
            /// <summary>Правило совпадения — для диагностики в логе.</summary>
            public string Rule;
        }

        /// <summary>Символы-разделители и типографские различия, игнорируемые при сверке.
        /// Объявлен до Synonyms: статические поля инициализируются по порядку
        /// объявления, а BuildSynonyms() уже вызывает Normalize(), читающий Noise.</summary>
        private static readonly char[] Noise =
        {
            ' ', '\t', '_', '-', '.', '/', '\\', ':', '(', ')', '[', ']', '{', '}',
            ',', ';', '\'', '"', '№', '\u00A0', '\u00B0'
        };

        /// <summary>Группы синонимов (рус/англ и общепринятые сокращения).
        /// Ключи нормализуются функцией <see cref="Normalize"/>. Группы намеренно узкие:
        /// «массовый расход» и «объёмный расход» — разные параметры и не смешиваются.</summary>
        private static readonly string[][] SynonymGroups =
        {
            new[] { "расход", "flow", "flowrate", "расходпотока" },
            new[] { "массовыйрасход", "massflow", "massflowrate" },
            new[] { "объемныйрасход", "volumetricflow", "volumeflow" },
            new[] { "давление", "pressure" },
            new[] { "температура", "temperature" },
            new[] { "площадь", "area" },
            new[] { "объем", "volume" },
            new[] { "длина", "length" },
            new[] { "ширина", "width" },
            new[] { "высота", "height" },
            new[] { "глубина", "depth" },
            new[] { "толщина", "thickness" },
            new[] { "диаметр", "diameter", "diam" },
            new[] { "материал", "material" },
            new[] { "описание", "description", "comment", "примечание" },
            new[] { "наименование", "имя", "name", "маркировка", "mark", "tag" },
            new[] { "тип", "type", "типсемейства", "familytype", "символ" },
            new[] { "количество", "count", "number", "количествоэкземпляров" },
            new[] { "единицаизмерения", "единицы", "unit", "units" },
            new[] { "этаж", "level", "уровень" },
            new[] { "категория", "category" },
            new[] { "группа", "group" },
            new[] { "производитель", "manufacturer", "изготовитель" },
            new[] { "артикул", "sku", "код", "code", "номер" },
            new[] { "стоимость", "cost", "цена", "price" },
            new[] { "примечание1", "примечание2", "notes" },
        };

        /// <summary>Нормализованное имя → идентификатор группы синонимов.</summary>
        private static readonly Dictionary<string, int> Synonyms = BuildSynonyms();

        private static Dictionary<string, int> BuildSynonyms()
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < SynonymGroups.Length; i++)
            {
                foreach (var raw in SynonymGroups[i])
                {
                    var key = Normalize(raw);
                    if (key.Length > 0 && !map.ContainsKey(key)) map[key] = i;
                }
            }
            return map;
        }

        /// <summary>Приводит имя параметра к сравнимому виду: нижний регистр, без
        /// разделителей, «ё»→«е», «й»→«и». Сравнение становится независимым от
        /// регистра, пробелов, подчёркиваний и оформления.</summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (Array.IndexOf(Noise, c) >= 0) continue;
                char ch = char.ToLowerInvariant(c);
                switch (ch)
                {
                    case '\u0451': ch = '\u0435'; break; // ё → е
                    case '\u0401': ch = '\u0435'; break; // Ё → е
                    case '\u0439': ch = '\u0438'; break; // й → и
                    case '\u0419': ch = '\u0438'; break; // Й → и
                    case '\u00ab': ch = '"'; break;     // « »
                    case '\u00bb': ch = '"'; break;
                }
                sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>Идентификатор группы синонимов для имени (0 — группа не найдена).</summary>
        public static int SynonymGroup(string name)
        {
            var key = Normalize(name);
            if (key.Length == 0) return 0;
            int id;
            return Synonyms.TryGetValue(key, out id) ? id + 1 : 0;
        }

        /// <summary>Нормализованное имя встроенного параметра Revit (например
        /// «Расход потока» → «rbsflowparam»). Пусто для общих и семейных параметров.</summary>
        public static string BuiltInName(Parameter p)
        {
            try
            {
                var def = p?.Definition;
                // Общие и семейные параметры не встроенные.
                if (def == null || def is ExternalDefinition) return string.Empty;
                // У встроенных параметров ElementId отрицательный и соответствует
                // значению перечисления BuiltInParameter.
                long id = p.Id.Value;
                if (id >= 0) return string.Empty;
                int code = (int)id;
                if (!Enum.IsDefined(typeof(BuiltInParameter), code)) return string.Empty;
                return Normalize(((BuiltInParameter)code).ToString());
            }
            catch { return string.Empty; }
        }

        /// <summary>GUID общего параметра строкой. Пусто для встроенных и семейных.</summary>
        public static string GuidOf(Parameter p)
        {
            try
            {
                var ext = p?.Definition as ExternalDefinition;
                return ext == null ? string.Empty : ext.GUID.ToString();
            }
            catch { return string.Empty; }
        }

        /// <summary>Все имена параметра, по которым его можно опознать: внутреннее
        /// имя определения (у встроенных — английское, например «Air Flow») и
        /// локализованная подпись Revit (в русском UI — «Расход»). Оба сравниваются
        /// при подборе, поэтому неважно, какое имя прислал пользователь.
        /// Для общих и семейных параметров набор состоит из одного имени.</summary>
        public static string[] Aliases(Parameter p)
        {
            var list = new List<string>(2);
            string defName = null;
            try { defName = p?.Definition?.Name; }
            catch { }
            if (!string.IsNullOrWhiteSpace(defName)) list.Add(defName.Trim());

            string loc = LocalizedName(p);
            if (!string.IsNullOrWhiteSpace(loc) &&
                !list.Exists(x => string.Equals(x, loc.Trim(), StringComparison.OrdinalIgnoreCase)))
                list.Add(loc.Trim());
            return list.ToArray();
        }

        /// <summary>Локализованная подпись встроенного параметра («Расход», «Площадь»…)
        /// на языке текущего интерфейса Revit. Пусто, если параметр не встроенный
        /// либо Revit не смог подобрать подпись.</summary>
        public static string LocalizedName(Parameter p)
        {
            try
            {
                var def = p?.Definition;
                if (def == null || def is ExternalDefinition) return string.Empty;
                var internalDef = def as InternalDefinition;
                if (internalDef == null) return string.Empty;
                var bip = internalDef.BuiltInParameter;
                if (bip == BuiltInParameter.INVALID) return string.Empty;
                var label = LabelUtils.GetLabelFor(bip);
                return string.IsNullOrWhiteSpace(label) ? string.Empty : label.Trim();
            }
            catch { return string.Empty; }
        }

        /// <summary>Имя параметра для показа пользователю: локализованная подпись,
        /// если она есть (как в интерфейсе Revit), иначе имя определения.
        /// Так «Air Flow» отображается как «Расход», а у общего параметра из .txt
        /// остаётся его собственное имя.</summary>
        public static string DisplayName(Parameter p)
        {
            var loc = LocalizedName(p);
            if (!string.IsNullOrEmpty(loc)) return loc;
            try { return p?.Definition?.Name ?? string.Empty; }
            catch { return string.Empty; }
        }

        /// <summary>Подбирает параметр, соответствующий имени <paramref name="name"/> и,
        /// если задан, GUID <paramref name="guid"/>. Возвращает лучшее совпадение по
        /// каскаду правил либо null.</summary>
        public static Match Find(IEnumerable<Parameter> candidates, string name, string guid = null)
        {
            if (candidates == null) return null;
            var list = candidates.Where(p => p != null).ToList();
            if (list.Count == 0) return null;

            string wantGuid = string.IsNullOrWhiteSpace(guid) ? string.Empty : guid.Trim();
            string want = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
            string wantNorm = Normalize(want);
            int wantGroup = SynonymGroup(want);

            Parameter bestP = null;
            string bestRule = null;
            int bestScore = 0;

            foreach (var p in list)
            {
                string score = string.Empty;
                int rank = Score(p, want, wantNorm, wantGroup, wantGuid, out score);
                if (rank > bestScore)
                {
                    bestScore = rank;
                    bestP = p;
                    bestRule = score;
                }
            }

            if (bestP == null) return null;
            return new Match { Parameter = bestP, Rule = bestRule };
        }

        private static int Score(Parameter p, string want, string wantNorm, int wantGroup,
            string wantGuid, out string rule)
        {
            rule = null;

            // 1. Общий параметр: точное совпадение GUID — самый надёжный признак.
            if (wantGuid.Length > 0)
            {
                var g = GuidOf(p);
                if (g.Length > 0 && string.Equals(g, wantGuid, StringComparison.OrdinalIgnoreCase))
                {
                    rule = "GUID";
                    return 100;
                }
            }

            string defName = null;
            try { defName = p.Definition?.Name; }
            catch { }
            if (string.IsNullOrEmpty(defName)) return 0;

            // Сравниваем со всеми именами параметра: внутренним (у встроенных
            // английское — «Air Flow») и локализованной подписью Revit («Расход»).
            // Запрос пользователя тоже сверяем со всеми именами.
            var paramNames = Aliases(p);
            var paramNorms = paramNames.Select(Normalize).Where(s => s.Length > 0).ToList();
            var wantNorms = new List<string>();
            if (wantNorm.Length > 0) wantNorms.Add(wantNorm);

            // 2. Точное имя без учёта регистра.
            foreach (var pn in paramNames)
            {
                if (!string.IsNullOrEmpty(want) &&
                    string.Equals(pn, want, StringComparison.OrdinalIgnoreCase))
                {
                    rule = "имя";
                    return 90;
                }
            }

            // 3. Нормализованное имя (пробелы/регистр/_/- не важны).
            foreach (var norm in paramNorms)
            {
                if (wantNorms.Contains(norm))
                {
                    rule = "нормализовано";
                    return 80;
                }
            }

            // 4. Группа синонимов RU/EN.
            foreach (var pn in paramNames)
            {
                int group = SynonymGroup(pn);
                if (wantGroup != 0 && group != 0 && group == wantGroup)
                {
                    rule = "синоним";
                    return 70;
                }
            }

            // 5. Имя встроенного параметра Revit (русское имя против английского
            //    внутреннего, например «Расход» → RBS_FLOW_PARAM).
            var bi = BuiltInName(p);
            if (bi.Length >= 3)
            {
                foreach (var wn in wantNorms)
                {
                    if (wn.Length >= 3 && bi == wn)
                    {
                        rule = "встроенный";
                        return 65;
                    }
                    if (wn.Length >= 3 && bi.Contains(wn))
                    {
                        rule = "встроенный (вхождение)";
                        return 55;
                    }
                    if (wn.Length >= 4 && wn.Contains(bi))
                    {
                        rule = "вхождение в имя";
                        return 50;
                    }
                }
            }

            // 6. Одно имя содержит другое («Расход потока» ⊃ «Расход»).
            foreach (var norm in paramNorms)
            {
                if (norm.Length < 4) continue;
                foreach (var wn in wantNorms)
                {
                    if (wn.Length < 4) continue;
                    if (norm.Contains(wn))
                    {
                        rule = "вхождение";
                        return 40;
                    }
                    if (wn.Contains(norm))
                    {
                        rule = "вхождение (обратное)";
                        return 35;
                    }
                }
            }

            return 0;
        }
    }
}