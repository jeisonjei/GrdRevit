using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GrdRevit.Core
{
    /// <summary>Избранные элементы (виды, листы со спецификациями). Один JSON-файл на все
    /// документы; разделение по ключу документа и области (scope). Выбранные элементы
    /// переживают сеансы Revit. Ошибки не выбрасываются: хранение не влияет на работу.</summary>
    public static class FavoriteStore
    {
        private static readonly object Sync = new object();

        private sealed class FavEntry
        {
            public string DocKey = string.Empty;
            public string Scope = string.Empty;
            public List<string> Ids = new List<string>();
        }

        public static string StoragePath
        {
            get
            {
                try
                {
                    return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "GrdRevit", "favorites.json");
                }
                catch { return "favorites.json"; }
            }
        }

        /// <summary>Id избранных элементов области для документа.</summary>
        public static HashSet<string> Load(string docKey, string scope)
        {
            lock (Sync)
            {
                var result = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    if (string.IsNullOrEmpty(docKey) || string.IsNullOrEmpty(scope)) return result;
                    var e = LoadAll().Find(x => x.DocKey == docKey && x.Scope == scope);
                    if (e != null)
                        foreach (var id in e.Ids)
                            if (!string.IsNullOrEmpty(id)) result.Add(id);
                }
                catch { }
                return result;
            }
        }

        /// <summary>Включить/выключить избранное для элемента области.</summary>
        public static void Set(string docKey, string scope, string id, bool favorite)
        {
            lock (Sync)
            {
                try
                {
                    if (string.IsNullOrEmpty(docKey) || string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(id)) return;
                    var entries = LoadAll();
                    var e = entries.Find(x => x.DocKey == docKey && x.Scope == scope);
                    if (e == null)
                    {
                        e = new FavEntry { DocKey = docKey, Scope = scope };
                        entries.Add(e);
                    }
                    if (favorite)
                    {
                        if (!e.Ids.Contains(id)) e.Ids.Add(id);
                    }
                    else
                    {
                        e.Ids.RemoveAll(x => x == id);
                    }
                    WriteAll(entries);
                }
                catch { /* не критично */ }
            }
        }

        // ------------------------------------------------------------- сериализация

        private static List<FavEntry> LoadAll()
        {
            var list = new List<FavEntry>();
            try
            {
                if (!File.Exists(StoragePath)) return list;
                var root = MiniJson.ParseTree(File.ReadAllText(StoragePath)) as Dictionary<string, object>;
                if (root == null || !root.TryGetValue("Entries", out var ds) || !(ds is IList<object> docs))
                    return list;
                foreach (var item in docs)
                {
                    if (!(item is Dictionary<string, object> m)) continue;
                    var e = new FavEntry { DocKey = AsStr(m, "DocKey"), Scope = AsStr(m, "Scope") };
                    if (m.TryGetValue("Ids", out var ids) && ids is IList<object> idList)
                        foreach (var id in idList)
                        {
                            var s = AsStr(id);
                            if (s.Length > 0) e.Ids.Add(s);
                        }
                    list.Add(e);
                }
            }
            catch { /* повреждённый файл — начинаем с пустого списка */ }
            return list;
        }

        private static void WriteAll(List<FavEntry> entries)
        {
            try
            {
                var dir = Path.GetDirectoryName(StoragePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                sb.Append("{\"Entries\":[");
                bool first = true;
                foreach (var e in entries)
                {
                    if (e.Ids.Count == 0) continue;
                    if (!first) sb.Append(',');
                    sb.Append("{\"DocKey\":").Append(Quote(MiniJson.Encode(e.DocKey)))
                      .Append(",\"Scope\":").Append(Quote(MiniJson.Encode(e.Scope)))
                      .Append(",\"Ids\":[");
                    for (int i = 0; i < e.Ids.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(Quote(MiniJson.Encode(e.Ids[i])));
                    }
                    sb.Append("]}");
                    first = false;
                }
                sb.Append("]}");
                File.WriteAllText(StoragePath, sb.ToString());
            }
            catch { /* не критично */ }
        }

        private static string AsStr(Dictionary<string, object> m, string key)
        {
            return m.TryGetValue(key, out var v) ? (v as string) ?? string.Empty : string.Empty;
        }

        private static string AsStr(object v)
        {
            return v as string ?? string.Empty;
        }

        private static string Quote(string encoded) => "\"" + encoded + "\"";
    }
}