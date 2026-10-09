using Autodesk.Revit.DB;

namespace GrdRevit.Revit
{
    /// <summary>Санитайзер имён элементов Revit. Revit запрещает в именах видов символы
    /// \ / : * ? " &lt; &gt; | и управляющие; без очистки Element.set_Name бросает
    /// ArgumentException «Name cannot include prohibited characters».</summary>
    public static class RevitNames
    {
        /// <summary>Символы, запрещённые Revit в именах видов и элементов.</summary>
        private static readonly char[] Prohibited = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

        /// <summary>Заменяет запрещённые и управляющие символы на «-», схлопывает пробелы.
        /// Возвращает безопасное имя; если после очистки ничего не осталось — <paramref name="fallback"/>.</summary>
        public static string Sanitize(string name, string fallback = "Без имени")
        {
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            var sb = new System.Text.StringBuilder(name.Length);
            bool lastDash = false;
            foreach (char c in name)
            {
                bool bad = char.IsControl(c) || System.Array.IndexOf(Prohibited, c) >= 0;
                if (bad)
                {
                    if (!lastDash) { sb.Append('-'); lastDash = true; }
                    continue;
                }
                if (c == ' ' && lastDash) continue;
                sb.Append(c);
                lastDash = false;
            }
            var result = sb.ToString().Trim('-', ' ');
            return string.IsNullOrEmpty(result) ? fallback : result;
        }

        /// <summary>Уникальное имя вида в пределах документа: базовое имя + « (2)», « (3)»…
        /// Имя предварительно очищается от запрещённых символов.</summary>
        public static string UniqueViewName(Document doc, string baseName, string fallback = "Без имени")
        {
            var taken = new System.Collections.Generic.SortedSet<string>(System.StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)))
                {
                    if (!string.IsNullOrEmpty(v?.Name)) taken.Add(v.Name);
                }
            }
            catch { }
            string root = Sanitize(baseName, fallback);
            string name = root;
            int n = 2;
            while (taken.Contains(name)) name = root + " (" + (n++) + ")";
            return name;
        }
    }

    /// <summary>Единый ключ документа для хранения данных плагина (путь или название):
    /// избранные и правки запоминаются для каждого документа отдельно.</summary>
    public static class DocKeys
    {
        public static string Get(Document doc)
        {
            if (doc == null) return string.Empty;
            try
            {
                if (!string.IsNullOrEmpty(doc.PathName)) return doc.PathName;
                return (doc.Title ?? string.Empty) + " (без документа)";
            }
            catch { return string.Empty; }
        }
    }
}