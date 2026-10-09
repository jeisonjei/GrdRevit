using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace GrdRevit.Core
{
    /// <summary>
    /// Парсер файла .tomat (программа расчёта теплопотерь и отопления, JSON schemaVersion 3).
    /// Файл достигает десятков мегабайт, поэтому полный разбор в дерево не выполняется:
    /// нужные верхнеуровневые секции находятся сканированием срезами (без раскрытия чужих),
    /// а затем каждая секция разбирается через <see cref="MiniJson.ParseTree"/>.
    /// </summary>
    public static class TomatParser
    {
        public static TomatFile Parse(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                var notFound = new TomatFile();
                notFound.ParseMessages.Add("Файл .tomat не найден: " + path);
                return notFound;
            }
            try
            {
                return ParseText(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                var failed = new TomatFile();
                failed.ParseMessages.Add("Не удалось прочитать файл .tomat: " + ex.Message);
                return failed;
            }
        }

        public static TomatFile ParseText(string json)
        {
            var file = new TomatFile();
            if (string.IsNullOrWhiteSpace(json))
            {
                file.ParseMessages.Add("Файл пуст");
                return file;
            }

            var sections = FindTopLevelSections(json, new[] { "heatLossRoomRows", "airChanges", "shapes" });
            file.SchemaVersion = FindScalar(json, "schemaVersion");

            var rooms = SectionJson(json, sections, "heatLossRoomRows") as IList<object>;
            if (rooms == null)
            {
                file.ParseMessages.Add("В файле нет таблицы помещений (heatLossRoomRows)");
                return file;
            }

            var air = SectionJson(json, sections, "airChanges") as Dictionary<string, object>;
            var shapes = SectionJson(json, sections, "shapes") as IList<object>;

            var polygons = ReadRoomPolygons(shapes);
            var airByRoom = ReadAirByRoom(air);
            var airRates = ReadAirRates(air);
            var systemFlows = ReadSystemFlows(air);

            var scales = new List<double>();
            foreach (var item in rooms)
            {
                var row = item as Dictionary<string, object>;
                if (row == null) continue;

                var room = new TomatRoom
                {
                    Id = Str(row, "id"),
                    FloorNumber = Int(row, "floorNumber"),
                    RoomNumber = Str(row, "roomNumber"),
                    RoomName = Str(row, "name"),
                    IndoorTemperatureC = Dbl(row, "indoorTemperatureC"),
                    AreaSqM = Dbl(row, "areaSqM"),
                    HeightM = Dbl(row, "heightM"),
                    ColdVentilationAirflowM3H = Dbl(row, "coldVentilationAirflowM3H"),
                    Source = Str(row, "source")
                };

                if (airByRoom.TryGetValue(room.Id, out var airInfo) && airInfo != null)
                {
                    room.SupplyM3H = airInfo.Supply;
                    room.ExhaustM3H = airInfo.Exhaust;
                    room.SupplySystemId = airInfo.SupplySystem;
                    room.ExhaustSystemId = airInfo.ExhaustSystem;
                }
                if (airRates.TryGetValue(room.Id, out var rates))
                {
                    room.SupplyRatePerHour = rates.Item1;
                    room.ExhaustRatePerHour = rates.Item2;
                }
                if (systemFlows.TryGetValue(room.Id, out var sysFlows))
                {
                    room.SupplyM3H = sysFlows;
                }

                var linked = Str(row, "linkedShapeId");
                if (!string.IsNullOrEmpty(linked) && polygons.TryGetValue(linked, out var poly))
                {
                    room.Polygon = poly;
                }

                file.Rooms.Add(room);

                if (room.HasPolygon && room.AreaSqM.HasValue && room.AreaSqM.Value > 0.0)
                {
                    var polyArea = ShoelaceArea(room.Polygon);
                    if (polyArea > 0.0)
                    {
                        scales.Add(Math.Sqrt(room.AreaSqM.Value / polyArea));
                    }
                }
            }

            if (scales.Count > 0)
            {
                scales.Sort();
                int n = scales.Count;
                file.UnitsToMeters = (n % 2 == 1) ? scales[n / 2] : (scales[n / 2 - 1] + scales[n / 2]) / 2.0;
            }

            return file;
        }

        // ---------- Секции ----------

        /// <summary>Значение секции JSON из корня файла, разобранное в дерево MiniJson.</summary>
        private static object SectionJson(string json, Dictionary<string, (int, int)> sections, string key)
        {
            if (!sections.TryGetValue(key, out var range)) return null;
            return MiniJson.ParseTree(json.Substring(range.Item1, range.Item2 - range.Item1));
        }

        /// <summary>
        /// Значение первого строкового ключа в файле (может лежать на любой глубине,
        /// например schemaVersion внутри meta). Возвращает текст значения без кавычек
        /// либо пустую строку, если ключ или значение не найдены.
        /// </summary>
        private static string FindScalar(string json, string key)
        {
            string needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return string.Empty;
            i += needle.Length;
            i = SkipWs(json, i);
            if (i >= json.Length || json[i] != ':') return string.Empty;
            int vs = i + 1;
            int ve = SkipValue(json, vs);
            var raw = json.Substring(vs, ve - vs).Trim();
            if (raw.Length >= 2 && raw[0] == '"') raw = raw.Substring(1, raw.Length - 2);
            return raw;
        }

        /// <summary>
        /// Ищет значения верхнеуровневых ключей JSON за один проход по тексту.
        /// Возвращает диапазон индексов каждого искомого ключа: начало значения (включительно)
        /// и конец (исключительно). Сканирование не раскрывает строки и чужие значения: только
        /// подсчёт вложенности на уровне корневого объекта.
        /// </summary>
        internal static Dictionary<string, (int, int)> FindTopLevelSections(string json, string[] keys)
        {
            var wanted = new HashSet<string>(keys, StringComparer.Ordinal);
            var result = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            int n = json.Length;
            int depth = 0;
            int i = 0;

            while (i < n)
            {
                char c = json[i];
                if (c == '"')
                {
                    bool atKeyLevel = depth == 1;
                    int strStart = i;
                    i++;
                    while (i < n)
                    {
                        if (json[i] == '\\') i += 2;
                        else if (json[i] == '"') { i++; break; }
                        else i++;
                    }
                    if (!atKeyLevel) continue;

                    string name = json.Substring(strStart + 1, i - strStart - 2);
                    if (!wanted.Contains(name) || result.ContainsKey(name)) continue;

                    i = SkipWs(json, i);
                    if (i >= n || json[i] != ':') continue;
                    int vs = i + 1;
                    int ve = SkipValue(json, vs);
                    result[name] = (vs, ve);
                    if (result.Count == wanted.Count) break;
                    i = ve;
                    continue;
                }

                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']')
                {
                    if (depth > 0) depth--;
                }
                i++;
            }
            return result;
        }

        /// <summary>Индекс сразу после значения JSON, начинающегося на позиции i.</summary>
        private static int SkipValue(string json, int i)
        {
            int n = json.Length;
            i = SkipWs(json, i);
            if (i >= n) return i;
            char c = json[i];

            if (c == '"')
            {
                i++;
                while (i < n)
                {
                    if (json[i] == '\\') i += 2;
                    else if (json[i] == '"') { i++; break; }
                    else i++;
                }
                return i;
            }

            if (c == '{' || c == '[')
            {
                int depth = 0;
                while (i < n)
                {
                    char ch = json[i];
                    if (ch == '"')
                    {
                        i++;
                        while (i < n)
                        {
                            if (json[i] == '\\') i += 2;
                            else if (json[i] == '"') { i++; break; }
                            else i++;
                        }
                        continue;
                    }
                    if (ch == '{' || ch == '[') depth++;
                    else if (ch == '}' || ch == ']')
                    {
                        depth--;
                        if (depth == 0) { i++; break; }
                    }
                    i++;
                }
                return i;
            }

            while (i < n && json[i] != ',' && json[i] != '}' && json[i] != ']' &&
                   json[i] != ' ' && json[i] != '\t' && json[i] != '\n' && json[i] != '\r')
            {
                i++;
            }
            return i;
        }

        private static int SkipWs(string json, int i)
        {
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r')) i++;
            return i;
        }

        // ---------- Связки секций ----------

        /// <summary>Контуры помещений: id фигуры (shapes.id) -> список точек.</summary>
        private static Dictionary<string, List<TomatPoint>> ReadRoomPolygons(IList<object> shapes)
        {
            var polygons = new Dictionary<string, List<TomatPoint>>(StringComparer.Ordinal);
            if (shapes == null) return polygons;

            foreach (var item in shapes)
            {
                var shape = item as Dictionary<string, object>;
                if (shape == null) continue;
                var id = Str(shape, "id");
                if (string.IsNullOrEmpty(id)) continue;

                // Фигуры комнат: room-area (у служебных block нет точек).
                var kind = Str(shape, "kind");
                if (!string.IsNullOrEmpty(kind) && !string.Equals(kind, "room-area", StringComparison.Ordinal)) continue;

                if (!(shape.TryGetValue("points", out var pts) && pts is IList<object> ptList)) continue;

                var poly = new List<TomatPoint>();
                foreach (var pItem in ptList)
                {
                    var p = pItem as Dictionary<string, object>;
                    if (p == null) continue;
                    var x = Dbl(p, "x");
                    var y = Dbl(p, "y");
                    if (x.HasValue && y.HasValue) poly.Add(new TomatPoint(x.Value, y.Value));
                }
                if (poly.Count >= 3) polygons[id] = poly;
            }
            return polygons;
        }

        /// <summary>
        /// Воздухообмен по комнате (airChanges.rows, объединён по roomId).
        /// На одну комнату может быть несколько периодов (warm/cold): приток и вытяжка
        /// берутся как максимальные непустые значения; системы — первая непустая.
        /// </summary>
        internal sealed class AirInfo
        {
            public double? Supply;
            public double? Exhaust;
            public string SupplySystem = string.Empty;
            public string ExhaustSystem = string.Empty;
        }

        private static Dictionary<string, AirInfo> ReadAirByRoom(Dictionary<string, object> air)
        {
            var result = new Dictionary<string, AirInfo>(StringComparer.Ordinal);
            if (air == null || !(air.TryGetValue("rows", out var rowsObj) && rowsObj is IList<object> rows))
            {
                return result;
            }

            foreach (var item in rows)
            {
                var row = item as Dictionary<string, object>;
                if (row == null) continue;
                var roomId = Str(row, "roomId");
                if (string.IsNullOrEmpty(roomId)) continue;

                if (!result.TryGetValue(roomId, out var info)) info = new AirInfo();
                var supply = Dbl(row, "supplyM3H");
                var exhaust = Dbl(row, "exhaustM3H");

                if (!info.Supply.HasValue && supply.HasValue) info.Supply = supply;
                else if (supply.HasValue && supply.Value > info.Supply.Value) info.Supply = supply;

                if (!info.Exhaust.HasValue && exhaust.HasValue) info.Exhaust = exhaust;
                else if (exhaust.HasValue && exhaust.Value > info.Exhaust.Value) info.Exhaust = exhaust;

                if (string.IsNullOrEmpty(info.SupplySystem)) info.SupplySystem = Str(row, "supplySystemId");
                if (string.IsNullOrEmpty(info.ExhaustSystem)) info.ExhaustSystem = Str(row, "exhaustSystemId");

                result[roomId] = info;
            }
            return result;
        }

        /// <summary>Кратности воздухообмена: roomId -> (приток, вытяжка), 1/ч.</summary>
        private static Dictionary<string, (double?, double?)> ReadAirRates(Dictionary<string, object> air)
        {
            var result = new Dictionary<string, (double?, double?)>(StringComparer.Ordinal);
            if (air == null || !(air.TryGetValue("roomData", out var dataObj) && dataObj is Dictionary<string, object> roomData))
            {
                return result;
            }

            foreach (var kv in roomData)
            {
                var rd = kv.Value as Dictionary<string, object>;
                if (rd == null) continue;
                result[kv.Key] = (Dbl(rd, "supplyRatePerHour"), Dbl(rd, "exhaustRatePerHour"));
            }
            return result;
        }

        /// <summary>
        /// «Состав систем вентиляции» (airChanges.systemFlows): roomId -> суммарный расход
        /// воздуха всех систем помещения, м³/ч. Системы в файле не делятся на приток/вытяжку,
        /// поэтому сумма по помещению используется как расход притока.
        /// </summary>
        private static Dictionary<string, double> ReadSystemFlows(Dictionary<string, object> air)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            if (air == null || !(air.TryGetValue("systemFlows", out var flowsObj) &&
                                  flowsObj is Dictionary<string, object> flows))
            {
                return result;
            }

            foreach (var kv in flows)
            {
                var perSystem = kv.Value as Dictionary<string, object>;
                if (perSystem == null) continue;
                double total = 0.0;
                foreach (var item in perSystem.Values)
                {
                    var flow = Dbl(item);
                    if (flow.HasValue) total += flow.Value;
                }
                if (total > 0.0) result[kv.Key] = total;
            }
            return result;
        }

        // ---------- Геометрия и чтение скаляров ----------

        /// <summary>Площадь полигона по формуле шнурка (в единицах чертежа).</summary>
        private static double ShoelaceArea(List<TomatPoint> p)
        {
            double sum = 0.0;
            int n = p.Count;
            for (int i = 0; i < n; i++)
            {
                var a = p[i];
                var b = p[(i + 1) % n];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return Math.Abs(sum) / 2.0;
        }

        private static string Str(Dictionary<string, object> m, string key)
        {
            return m.TryGetValue(key, out var v) && v != null ? v.ToString() : string.Empty;
        }

        private static int Int(Dictionary<string, object> m, string key)
        {
            var d = Dbl(m, key);
            return d.HasValue ? (int)Math.Round(d.Value) : 0;
        }

        private static double? Dbl(Dictionary<string, object> m, string key)
        {
            if (!m.TryGetValue(key, out var v) || v == null) return null;
            return Dbl(v);
        }

        private static double? Dbl(object v)
        {
            if (v is double d) return d;
            if (v is bool) return null;
            var s = v as string;
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) return r;
            return null;
        }
    }
}