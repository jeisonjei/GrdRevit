using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GrdRevit.Core
{
    /// <summary>
    /// Крошечный JSON-сериализатор для настроек плагина (без внешних зависимостей).
    /// Предназначен ТОЛЬКО для GrdSettings.
    /// </summary>
    public static class MiniJson
    {
        public static string Serialize(GrdSettings s)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            WriteProp(sb, "InstanceParams", WriteArray(s.InstanceParams, WriteParam));
            sb.Append(',');
            WriteProp(sb, "FamilyNameOverrides", WriteDict(s.FamilyNameOverrides));
            sb.Append(',');
            WriteProp(sb, "TypeNameExactMap", WriteDict(s.TypeNameExactMap));
            sb.Append(',');
            WriteProp(sb, "ShowCount", s.ShowCount ? "true" : "false");
            sb.Append(',');
            WriteProp(sb, "WrapInTransaction", s.WrapInTransaction ? "true" : "false");
            sb.Append(',');
            WriteProp(sb, "LastGrdPath", Quote(Encode(s.LastGrdPath)));
            sb.Append('}');
            return sb.ToString();
        }

        private static string WriteParam(InstanceParamSetting p)
        {
            return "{\"Name\":\"" + Encode(p.Name) +
                   "\",\"Value\":\"" + Encode(p.Value) +
                   "\",\"Kind\":\"" + p.Kind +
                   "\"}";
        }

        private static string WriteArray<T>(IEnumerable<T> items, Func<T, string> w)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var it in items)
            {
                if (!first) sb.Append(',');
                sb.Append(w(it));
                first = false;
            }
            return sb.Append(']').ToString();
        }

        private static string WriteDict(Dictionary<string, string> d)
        {
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var kv in d)
            {
                if (!first) sb.Append(',');
                sb.Append('"').Append(Encode(kv.Key)).Append("\":\"").Append(Encode(kv.Value)).Append('"');
                first = false;
            }
            return sb.Append('}').ToString();
        }

        private static void WriteProp(StringBuilder sb, string name, string valueJson)
        {
            sb.Append('"').Append(name).Append("\":").Append(valueJson);
        }

        private static string Quote(string s)
        {
            return "\"" + s + "\"";
        }

        public static GrdSettings Deserialize(string json)
        {
            var s = new GrdSettings();
            if (string.IsNullOrEmpty(json)) return s;
            int i = 0;
            var root = ParseValue(json, ref i) as Dictionary<string, object>;
            if (root == null) return s;
            var m = root;

            if (m.TryGetValue("InstanceParams", out var arr) && arr is IList<object> plist)
            {
                foreach (var item in plist)
                {
                    var pm = item as Dictionary<string, object>;
                    if (pm == null) continue;
                    var p = new InstanceParamSetting
                    {
                        Name = Get<string>(pm, "Name"),
                        Value = Get<string>(pm, "Value")
                    };
                    Enum.TryParse(Get<string>(pm, "Kind"), out ParamValueKind k);
                    p.Kind = k;
                    s.InstanceParams.Add(p);
                }
            }
            if (m.TryGetValue("FamilyNameOverrides", out var f) && f is IList<object> fl) CopyDict(fl, s.FamilyNameOverrides);
            if (m.TryGetValue("TypeNameExactMap", out var t) && t is IList<object> tl) CopyDict(tl, s.TypeNameExactMap);
            if (m.TryGetValue("ShowCount", out var sc)) s.ShowCount = GetBool(sc);
            if (m.TryGetValue("WrapInTransaction", out var wt)) s.WrapInTransaction = GetBool(wt);
            if (m.TryGetValue("LastGrdPath", out var lp)) s.LastGrdPath = lp as string ?? string.Empty;
            return s;
        }

        private static void CopyDict(IList<object> list, Dictionary<string, string> dst)
        {
            foreach (var item in list)
            {
                var pm = item as Dictionary<string, object>;
                if (pm == null) continue;
                foreach (var kv in pm)
                {
                    string key = kv.Key;
                    string val = (kv.Value as string) ?? string.Empty;
                    if (!string.IsNullOrEmpty(key)) dst[key] = val;
                }
            }
        }

        private static T Get<T>(object v, string key)
        {
            if (v is Dictionary<string, object> m && m.TryGetValue(key, out var val) && val is T t) return t;
            return default;
        }

        private static bool GetBool(object v)
        {
            if (v is bool b) return b;
            return (v as string) == "true" || (v as string) == "True";
        }

        // ---------- Парсер ----------
        // Возвращает дерево: Dictionary<string,object> либо IList<object> либо string/bool/double.
        // Для простоты корень конвертируем в "кортеж" [объект].

        private static object ParseValue(string j, ref int i)
        {
            i = SkipWs(j, i);
            if (i >= j.Length) return null;
            char c = j[i];
            switch (c)
            {
                case '{': return ParseObject(j, ref i);
                case '[': return ParseArray(j, ref i);
                case '"': return ParseString(j, ref i);
                case 't':
                    i += 4; return true;
                case 'f':
                    i += 5; return false;
                case 'n':
                    i += 4; return null;
                default:
                    // число или слово
                    int start = i;
                    while (i < j.Length && (char.IsDigit(j[i]) || j[i] == '.' || j[i] == '-' || j[i] == '+'
                                            || j[i] == 'e' || j[i] == 'E')) i++;
                    var tok = j.Substring(start, i - start);
                    return tok;
            }
        }

        private static Dictionary<string, object> ParseObject(string j, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; // {
            while (true)
            {
                int before = i;
                i = SkipWs(j, i);
                if (i >= j.Length) break;
                if (j[i] == '}') { i++; break; }
                if (j[i] != '"')
                {
                    // Не-строка в позиции ключа: битый JSON. Пропускаем один символ,
                    // чтобы гарантированно сдвинуться — защита от бесконечного цикла.
                    i++;
                    continue;
                }
                var key = ParseString(j, ref i);
                i = SkipWs(j, i);
                if (i < j.Length && j[i] == ':') i++;
                var val = ParseValue(j, ref i);
                d[key] = val;
                i = SkipWs(j, i);
                if (i < j.Length && j[i] == ',') { i++; continue; }
                if (i < j.Length && j[i] == '}') { i++; break; }
                if (i == before)
                {
                    // Парсер не сдвинулся — вход повреждён. Чтобы не виснуть,
                    // принудительно пропускаем один символ.
                    i++;
                }
            }
            return d;
        }

        private static IList<object> ParseArray(string j, ref int i)
        {
            var list = new List<object>();
            i++; // [
            while (true)
            {
                int before = i;
                i = SkipWs(j, i);
                if (i >= j.Length) break;
                if (j[i] == ']') { i++; break; }
                list.Add(ParseValue(j, ref i));
                i = SkipWs(j, i);
                if (i < j.Length && j[i] == ',') { i++; continue; }
                if (i < j.Length && j[i] == ']') { i++; break; }
                if (i == before)
                {
                    i++;
                }
            }
            return list;
        }

        private static string ParseString(string j, ref int i)
        {
            i = SkipWs(j, i);
            if (i >= j.Length || j[i] != '"') return string.Empty;
            i++;
            var sb = new StringBuilder();
            while (i < j.Length)
            {
                char c = j[i];
                if (c == '"') { i++; break; }
                if (c == '\\' && i + 1 < j.Length)
                {
                    char n = j[i + 1];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 5 < j.Length)
                            {
                                var hex = j.Substring(i + 2, 4);
                                if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
                                    sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                    i += 2;
                }
                else { sb.Append(c); i++; }
            }
            return sb.ToString();
        }

        private static int SkipWs(string j, int i)
        {
            while (i < j.Length && (j[i] == ' ' || j[i] == '\t' || j[i] == '\n' || j[i] == '\r')) i++;
            return i;
        }

        internal static string Encode(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c >= 32 && c < 127) sb.Append(c);
                        else sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        break;
                }
            }
            return sb.ToString();
        }
    }
}