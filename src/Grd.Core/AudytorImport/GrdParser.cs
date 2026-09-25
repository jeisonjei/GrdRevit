using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GrdRevit.Core
{
    /// <summary>
    /// Читает .grd (PWF, UTF-16LE). Извлекает "раны" печатаемого текста (ASCII + кириллица),
    /// классифицирует комнаты, стояки, приборы и собирает статистику по типам приборов.
    /// </summary>
    public static class GrdParser
    {
        /// <summary>Одна запись квартирного конвектора, извлечённая из бинарной зоны .grr.</summary>
        public sealed class ApartmentRecord
        {
            public string Room;
            public string Code;
            public double FhlW;
            public int ValveSetting;
            public long ByteOffset;
        }
        // Префиксы, с которых начинаются настоящие имена помещений/зон/стояков.
        private static readonly string[] RoomPrefixes =
        {
            "НАСОСНАЯ", "СТОЯНКА", "ЛК", "ПУИ", "ЛЕЖАК", "БКТ", "ЛХ", "ГЛ", "ВЕСТИБЮЛЬ",
            "КОЛЯСОЧНАЯ", "ТАМБУР", "СТ.", "СТ ", "СТ."
        };

        private static readonly HashSet<string> NotRooms = new HashSet<string>(StringComparer.Ordinal)
        {
            "МОСКВА", "БАКУНИНСКАЯ", "БАКУНИНСКАЯ 77", "РФ", "СИСТЕМА", "СО", "ПРОЕКТНЫЕ", "ДИАМЕТРЫ",
            "ПЕНКА PE 1", "PR ПНД КР", "ГОСТ 3262-75 O", "ГОСТ 10704-91", "РОСТ-PEXA-EVOH 7.4",
            "HZ ШКР НГ", "ТС М-0.6", "ФИЛЬТР", "ВЕН ЗАП ФЛ",
            "РИД-BVR-R", "РИД-MNF-R", "РИД-MVT-R", "РИД-JIP-R-FF", "РИД-BVR-FR", "РИД-BVR-DR",
            "РИД-FVR-R", "РИД-ФСФ-01", "РИД-LV-П", "РИД-TR-N-П", "РИД-APT-R3 5-25"
        };

        private static HashSet<string> _publicNotRooms;
        /// <summary>Публичный список токенов, гарантированно не являющихся помещениями.</summary>
        public static HashSet<string> PublicNotRooms =>
            _publicNotRooms ?? (_publicNotRooms = new HashSet<string>(NotRooms, StringComparer.Ordinal));

        // Ключевые слова, по которым токен гарантированно НЕ является помещением.
        private static readonly string[] NotRoomContains =
        {
            "PEXA", "РИД-", "РОСТ-", "ГОСТ", "ПНД", "PE 1", "MNF", "MVT", "JIP", "BVR", "FVR",
            "ШКР", "ФИЛЬТР", "ЗАП ФЛ", "НГ", "А!", "А\""
        };

        public static GrdDocument Parse(string path)
        {
            string ext = Path.GetExtension(path);
            if (string.Equals(ext, ".grr", StringComparison.OrdinalIgnoreCase))
                return ParseResults(path); // RES-файл: блоки кодов + маркер "ИТП" + локация.

            var doc = new GrdDocument { SourcePath = path };
            var bytes = File.ReadAllBytes(path);

            // Только печатаемые символы лишний раз чистим от не-текстовых подряд идущих.
            var runs = ExtractRuns(bytes);

            doc.Runs = runs;

            // Классификация ранов: комнаты.
            for (int i = 0; i < runs.Count; i++)
            {
                var r = runs[i];
                string t = r.Text.Trim();
                if (r.HasCyrillic && IsRoomLike(t))
                {
                    if (!doc.Rooms.Any(rm => string.Equals(rm.Name, t, StringComparison.Ordinal)))
                        doc.Rooms.Add(new GrdRoom { Name = t, Order = doc.Rooms.Count });
                }
            }

            // Приборы: таблица троек "комната → диаметр → нагрузка Вт" в зоне до спецификации.
            // В .grd отопительные приборы записаны именно так (по одному на помещение),
            // а коды TPL/HZ/GS из зоны спецификации — это ведомость, а не привязка к комнатам.
            doc.Devices = ExtractTripletDevices(runs);
            doc.DeviceCount = doc.Devices.Count;

            // Код модели (TPL/HZ/GS) подставляем из одноимённого .grr по совпадению мощности.
            TryFillTypesFromGrr(path, doc);

            // Агрегированная статистика по типам (ключ = код + семейство).
            doc.TypeStats = doc.Devices
                .GroupBy(d => new { d.Code })
                .Select(g =>
                {
                    var first = g.First();
                    var stats = new GrdDeviceTypeStats
                    {
                        Code = g.Key.Code,
                        FamilyHint = first.FamilyHint,
                        CapacityW = first.CapacityW,
                        Connection = first.Connection,
                        Count = g.Count()
                    };
                    stats.Rooms = g.Select(x => x.Room).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList();
                    return stats;
                })
                .OrderByDescending(s => s.Count)
                .ToList();

            return doc;
        }

        /// <summary>
        /// Ищет рядом с .grd одноимённый .grr и подставляет приборам код модели (TPL/HZ/GS)
        /// по прямому соответствию «комната → запись .grr» (каждая запись в .grr содержит
        /// имя комнаты, код, Фhl и настройку клапана).
        /// </summary>
        private static void TryFillTypesFromGrr(string grdPath, GrdDocument doc)
        {
            try
            {
                string grrPath = Path.ChangeExtension(grdPath, ".grr");
                if (!File.Exists(grrPath)) return;

                byte[] grrBytes = File.ReadAllBytes(grrPath);
                var apartments = new List<ApartmentRecord>();
                apartments.AddRange(ExtractApartmentRecords(grrBytes));
                apartments.AddRange(ExtractUpperFloorRecords(grrBytes));
                if (apartments.Count == 0) return;

                // Комната → запись .grr (берём первую, если дубли).
                var byRoom = new Dictionary<string, ApartmentRecord>(StringComparer.Ordinal);
                foreach (var a in apartments)
                {
                    if (!byRoom.ContainsKey(a.Room))
                        byRoom[a.Room] = a;
                }

                foreach (var dev in doc.Devices)
                {
                    if (string.IsNullOrEmpty(dev.Room)) continue;
                    if (!byRoom.TryGetValue(dev.Room, out var apt))
                    {
                        dev.MissingReason = ClassifyUnmatchedRoom(dev.Room);
                        continue;
                    }
                    dev.Code = apt.Code;
                    dev.CapacityW = apt.FhlW > 0 ? apt.FhlW : dev.CapacityW;
                    dev.ValveSetting = apt.ValveSetting > 0 ? apt.ValveSetting.ToString() : dev.ValveSetting;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TryFillTypesFromGrr: " + ex);
            }
        }

        /// <summary>
        /// Извлекает приборы из троек .grd: "комната → диаметр → нагрузка Вт".
        /// Каждый такой прибор соответствует одному помещению (секции С1.213, C2.302 и т.д.).
        /// Работает по зоне ДО начала спецификации кодов (TPL/HZ/GS), где тройки и живут.
        /// </summary>
        public static List<GrdDevice> ExtractTripletDevices(List<GrdTextRun> runs)
        {
            var devices = new List<GrdDevice>();
            if (runs == null || runs.Count == 0) return devices;

            // Зона троек заканчивается на первом коде прибора спецификации (TPL...) либо на
            // границе зоны (тяжёлый рановый маркер "Arialn"/"Ariald" перед спецификацией).
            int zoneEnd = runs.Count;
            for (int i = 0; i < runs.Count; i++)
            {
                string t = runs[i].Text.Trim();
                if (t.StartsWith("Arialn", StringComparison.Ordinal) ||
                    t.StartsWith("Ariald", StringComparison.Ordinal))
                {
                    zoneEnd = i;
                    break;
                }
            }

            // Идём по зоне. Искомый шаблон: [комната] [диаметр] [нагрузка Вт].
            // Иногда диаметр отсутствует — тогда комната идёт сразу с нагрузкой.
            for (int i = 0; i < zoneEnd && i < runs.Count; i++)
            {
                string t = runs[i].Text.Trim();
                if (t.Length == 0 || !IsRoomLike(t)) continue;

                // Диаметр: следующий ран — число.
                double? diameter = null;
                double? capacity = null;
                int p = i + 1;
                if (p < zoneEnd && double.TryParse(runs[p].Text.Trim(), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var d0) && d0 >= 1 && d0 <= 63)
                {
                    diameter = d0;

                    // Нагрузка: ещё один ран — число (Вт). Диаметры в проекте 10..32,
                    // нагрузки от сотен Вт, поэтому исключаем повторный "диаметр".
                    int q = p + 1;
                    if (q < zoneEnd && double.TryParse(runs[q].Text.Trim(), System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var d1) && d1 >= 80)
                    {
                        capacity = d1;
                        i = q;
                    }
                    else
                    {
                        i = p;
                    }
                }

                devices.Add(new GrdDevice
                {
                    Kind = DeviceKind.RadiatorHeating,
                    Code = string.Empty,           // код модели подставляется из .grr по мощности
                    FamilyHint = string.Empty,
                    Diameter = diameter?.ToString("0") ?? string.Empty,
                    CapacityW = capacity,
                    Room = t,
                    Order = devices.Count,
                    ByteOffset = runs[i].ByteOffset
                });
            }

            return devices;
        }

        /// <summary>
        /// Парсит RES-файл (.grr). Если рядом с .grr лежит одноимённый .grd —
        /// комнаты и состав приборов берутся ИЗ ТРОЕК .grd (каждый прибор = помещение),
        /// а коды моделей (TPL/HZ/GS) и мощности — из блоков .grr по совпадению.
        /// Без соседнего .grd — как раньше: чистый разбор блоков .grr.
        /// </summary>
        public static GrdDocument ParseResults(string path)
        {
            var clean = GrrCleanParser.Parse(path);

            var doc = new GrdDocument { SourcePath = path };
            doc.Runs = clean.Runs;

            var rooms = new List<GrdRoom>();
            var devices = new List<GrdDevice>();

            // Если рядом лежит одноимённый .grd — используем его тройки как модель "прибор по помещению".
            string grdPath = Path.ChangeExtension(path, ".grd");
            if (File.Exists(grdPath))
            {
                try
                {
                    var grdDevices = ExtractTripletDevices(GrdParser.ExtractRuns(File.ReadAllBytes(grdPath)));
                    if (grdDevices.Count > 0)
                    {
                        // Прямая привязка: каждая запись квартирного конвектора .grr
                        // содержит комнату, код, Фhl и настройку клапана.
                        byte[] grrBytes = File.ReadAllBytes(path);
                        var apartments = new List<ApartmentRecord>();
                        apartments.AddRange(ExtractApartmentRecords(grrBytes));
                        apartments.AddRange(ExtractUpperFloorRecords(grrBytes));
                        var byRoom = new Dictionary<string, ApartmentRecord>(StringComparer.Ordinal);
                        foreach (var a in apartments)
                        {
                            if (!byRoom.ContainsKey(a.Room))
                                byRoom[a.Room] = a;
                        }

                        foreach (var dev in grdDevices)
                        {
                            if (!string.IsNullOrEmpty(dev.Room)
                                && byRoom.TryGetValue(dev.Room, out var apt))
                            {
                                dev.Code = apt.Code;
                                dev.FamilyHint = apt.Code; // для отображения в UI
                                dev.CapacityW = apt.FhlW > 0 ? apt.FhlW : dev.CapacityW;
                                dev.ValveSetting = apt.ValveSetting > 0
                                    ? apt.ValveSetting.ToString()
                                    : dev.ValveSetting;
                            }
                            else if (!string.IsNullOrEmpty(dev.Room))
                            {
                                dev.MissingReason = ClassifyUnmatchedRoom(dev.Room);
                            }
                            devices.Add(dev);
                            if (!rooms.Any(rm => string.Equals(rm.Name, dev.Room, StringComparison.Ordinal)))
                                rooms.Add(new GrdRoom { Name = dev.Room, Order = rooms.Count });
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("ParseResults/.grr -> .grd: " + ex);
                    devices = new List<GrdDevice>();
                    rooms = new List<GrdRoom>();
                }
            }

            // Fallback: нет .grd рядом или его тройки не прочитались — чистый разбор .grr.
            if (devices.Count == 0)
            {
                foreach (var r in clean.Devices.Where(d => d.BlockKind != "Armature"))
                {
                    devices.Add(new GrdDevice
                    {
                        Kind = DeviceKind.RadiatorHeating,
                        Code = r.Code,
                        FamilyHint = r.FamilyHint,
                        CapacityW = r.CapacityW,
                        Connection = r.Connection,
                        Room = r.Room ?? string.Empty,
                        Order = devices.Count,
                        ByteOffset = r.ByteOffset
                    });

                    if (!string.IsNullOrWhiteSpace(r.Room) &&
                        !rooms.Any(rm => string.Equals(rm.Name, r.Room, StringComparison.Ordinal)))
                        rooms.Add(new GrdRoom { Name = r.Room, Order = rooms.Count });
                }
            }

            doc.Devices = devices;
            doc.Rooms = rooms;
            doc.DeviceCount = devices.Count;

            doc.TypeStats = devices
                .GroupBy(d => new { d.Code })
                .Select(g =>
                {
                    var first = g.First();
                    return new GrdDeviceTypeStats
                    {
                        Code = g.Key.Code,
                        FamilyHint = first.FamilyHint,
                        CapacityW = first.CapacityW,
                        Connection = first.Connection,
                        Count = g.Count(),
                        Rooms = g.Select(x => x.Room).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList()
                    };
                })
                .OrderByDescending(s => s.Count)
                .ToList();

            return doc;
        }

        /// <summary>
        /// Парсит бинарную зону квартирных конвекторов в .grr: каждая запись (~678 байт)
        /// содержит код прибора, комнату (С1.xxx/С2.xxx), фактическую теплоотдачу (Фhl)
        /// и настройку клапана. Строки хранятся при нечётном смещении — обычный
        /// ExtractRuns (шаг 2 от 0) их не видит.
        /// </summary>
        public static List<ApartmentRecord> ExtractApartmentRecords(byte[] bytes)
        {
            var result = new List<ApartmentRecord>();
            if (bytes == null || bytes.Length < 200) return result;

            // Декодируем файл как UTF-16LE начиная с байта 1 (нечётная пара):
            // символ I в строке → байтовое смещение 2*I + 1.
            string oddText;
            try { oddText = Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1); }
            catch { return result; }

            // Ищем коды TPLCPL/TPLNE в нечётной развёртке.
            var codeRegex = new System.Text.RegularExpressions.Regex(
                @"T(?:PLCPL|PLNE)[A-Z0-9-]{2,}",
                System.Text.RegularExpressions.RegexOptions.None);

            foreach (System.Text.RegularExpressions.Match m in codeRegex.Matches(oddText))
            {
                long baseOff = 2L * m.Index + 1;  // нечётный байтовый offset
                if (baseOff < 0x4C000) continue;   // зона квартир — примерно от 311K

                // Комната: UTF-16 строка на +108 от начала записи.
                if (baseOff + 122 > bytes.Length) continue;
                string roomRaw = Encoding.Unicode.GetString(bytes, (int)baseOff + 108, 12);
                string room = ExtractRoomName(roomRaw);
                if (string.IsNullOrEmpty(room)) continue;

                // Фhl: два одинаковых double на +184 и +192.
                double fhl = 0;
                if (baseOff + 200 <= bytes.Length)
                {
                    double f1 = BitConverter.ToDouble(bytes, (int)baseOff + 184);
                    double f2 = BitConverter.ToDouble(bytes, (int)baseOff + 192);
                    if (f1 > 0 && f1 < 10000 && Math.Abs(f1 - f2) < 0.01
                        && Math.Abs(f1 - Math.Round(f1)) < 0.01)
                        fhl = f1;
                }

                // Настройка клапана: byte на +0x171.
                int valve = 0;
                if (baseOff + 0x172 <= bytes.Length)
                    valve = bytes[(int)baseOff + 0x171];

                result.Add(new ApartmentRecord
                {
                    Room = room,
                    Code = m.Value,
                    FhlW = fhl,
                    ValveSetting = valve,
                    ByteOffset = baseOff
                });
            }

            return result;
        }

        /// <summary>
        /// Парсит бинарные записи конвекторов верхних этажей (С1.####/С2.####) в .grr:
        /// в отличие от квартирных (С1.xxx/С2.xxx, нечётная развёртка, комната на +108)
        /// здесь код прибора лежит при ЧЁТНОМ смещении, а имя комнаты — ровно через
        /// 35 байт после конца строки кода (codeEnd + 35). Фhl — два одинаковых double
        /// на +78/+86 от начала имени комнаты. Такой структуре соответствует, например,
        /// зона ~500К..660К в main.grr (записи С2.1001..С2.1616).
        /// </summary>
        public static List<ApartmentRecord> ExtractUpperFloorRecords(byte[] bytes)
        {
            var result = new List<ApartmentRecord>();
            if (bytes == null || bytes.Length < 200) return result;

            // Декодируем файл как UTF-16LE с байта 0 (чётная пара).
            string evenText;
            try { evenText = Encoding.Unicode.GetString(bytes); }
            catch { return result; }

            var codeRegex = new System.Text.RegularExpressions.Regex(
                @"T(?:PLCPL|PLNE)[A-Z0-9-]{2,}",
                System.Text.RegularExpressions.RegexOptions.None);

            foreach (System.Text.RegularExpressions.Match m in codeRegex.Matches(evenText))
            {
                long codeOff = 2L * m.Index;  // чётный байтовый offset
                if (codeOff < 0x4C000) continue;

                // Комната: UTF-16 строка на codeEnd+35. Верхние этажи — строго 7 знаков (С1.####/С2.####);
                // 6-значные имена принадлежат квартирной зоне (их читает ExtractApartmentRecords).
                int roomOff = (int)codeOff + 2 * m.Value.Length + 35;
                if (roomOff + 14 > bytes.Length) continue;
                string roomRaw = Encoding.Unicode.GetString(bytes, roomOff, 14);
                string room = ExtractRoomName(roomRaw);
                if (string.IsNullOrEmpty(room) || !RoomApartment7.IsMatch(room)) continue;

                // Фhl: два одинаковых double на +78/+86 от начала комнаты.
                double fhl = 0;
                if (roomOff + 94 <= bytes.Length)
                {
                    double f1 = BitConverter.ToDouble(bytes, roomOff + 78);
                    double f2 = BitConverter.ToDouble(bytes, roomOff + 86);
                    if (f1 > 0 && f1 < 10000 && Math.Abs(f1 - f2) < 0.01
                        && Math.Abs(f1 - Math.Round(f1)) < 0.01)
                        fhl = f1;
                }

                result.Add(new ApartmentRecord
                {
                    Room = room,
                    Code = m.Value,
                    FhlW = fhl,
                    ValveSetting = 0,
                    ByteOffset = codeOff
                });
            }

            return result;
        }

        /// <summary>Извлекает имя комнаты (С1.213, С2.202, С2.1001 и т.д.) из UTF-16 строки.</summary>
        private static string ExtractRoomName(string raw)
        {
            var sb = new StringBuilder();
            foreach (char c in raw)
            {
                if ((c >= 'А' && c <= 'Я') || (c >= 'а' && c <= 'я') || c == 'Ё' || c == 'ё')
                {
                    // Кириллица: нормализуем к верхнему регистру и заменяем 'С' → 'С' (UTF-16 0x0421).
                    sb.Append(c);
                }
                else if ((c >= '0' && c <= '9') || c == '.')
                {
                    sb.Append(c);
                }
                else if (sb.Length > 0)
                {
                    break; // конец имени
                }
            }
            string s = sb.ToString().Trim();
            // Имя комнаты: С1.### / С2.### (6 знаков) либо С1.#### / С2.#### (7 знаков, верхние этажи).
            if (RoomApartment6.IsMatch(s) || RoomApartment7.IsMatch(s))
                return s;
            return string.Empty;
        }

        private static readonly System.Text.RegularExpressions.Regex RoomApartment6 =
            new System.Text.RegularExpressions.Regex(@"^[СC][12]\.\d{3}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static readonly System.Text.RegularExpressions.Regex RoomApartment7 =
            new System.Text.RegularExpressions.Regex(@"^[СC][12]\.\d{4}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// Классифицирует комнату, для которой не нашлось записи конвектора в .grr,
        /// и даёт понятную причину для пустой ячейки «Тип».
        /// </summary>
        public static string ClassifyUnmatchedRoom(string room)
        {
            if (string.IsNullOrWhiteSpace(room)) return string.Empty;
            string r = room.Trim();

            // Квартирные коллекторы: нагрузка агрегированная, отдельного прибора нет.
            if (r.IndexOf("КВ.КОЛЛ", StringComparison.OrdinalIgnoreCase) >= 0)
                return "квартирный коллектор — агрегирующая нагрузка, отдельного прибора нет";

            // Помещения верхних этажей (С2.####, 7 знаков) — записи конвектора в .grr нет.
            if (RoomApartment7.IsMatch(r))
                return "жилое помещение верхних этажей — нет записи конвектора в .grr";

            // Стандартное жилое помещение (С1.###/С2.###) без записи в .grr.
            if (RoomApartment6.IsMatch(r))
                return "нет записи конвектора в .grr для этого помещения";

            // Служебные/общие помещения — тип отопительного прибора не заполняется.
            return "служебное/общее помещение — тип не присваивается";
        }

        public static List<GrdTextRun> ExtractRuns(byte[] bytes)
        {
            string text;
            try
            {
                text = Encoding.Unicode.GetString(bytes);
            }
            catch
            {
                text = Encoding.UTF8.GetString(bytes);
            }

            var runs = new List<GrdTextRun>();
            var sb = new StringBuilder();
            long runStart = -1;

            Action flush = () =>
            {
                if (sb.Length >= 2)
                {
                    var s = sb.ToString();
                    runs.Add(new GrdTextRun
                    {
                        Text = s,
                        ByteOffset = runStart,
                        Order = runs.Count,
                        HasCyrillic = s.Any(IsCyrillic),
                        IsUppercase = s.Any(char.IsLetter) && s.All(c => !char.IsLetter(c) || char.IsUpper(c))
                    });
                }
                sb.Clear();
            };

            for (int i = 0; i < bytes.Length - 1; i += 2)
            {
                char c = (char)(bytes[i] | (bytes[i + 1] << 8));
                if (IsPrintable(c))
                {
                    if (sb.Length == 0) runStart = i;
                    sb.Append(c);
                }
                else
                {
                    flush();
                }
            }
            flush();
            return runs;
        }

        private static bool IsPrintable(char c)
        {
            if (c >= 32 && c <= 126) return true;          // ASCII print
            if (c >= 0x0410 && c <= 0x044F) return true;   // А-я
            if (c == 0x0401 || c == 0x0451) return true;   // Ё/ё
            if (c == 0x00B0 || c == 0x2011 || c == 0x2212 || c == 0x2013 || c == 0x2014) return true;
            if (c == 0x00D7 || c == 0x2219) return true;   // ×, ·
            return false;
        }

        private static bool IsCyrillic(char c) => c >= 0x0410 && c <= 0x044F || c == 0x0401 || c == 0x0451;

        private static bool IsRoomLike(string t)
        {
            if (t.Length < 2 || t.Length > 30) return false;
            if (NotRooms.Contains(t)) return false;
            foreach (var kw in NotRoomContains)
                if (t.Contains(kw)) return false;

            // Имя комнаты: есть буквы, первая заглавная; не является одиночным словом-служебным.
            bool hasLetter = t.Any(char.IsLetter);
            bool allUpper = t.All(c => !char.IsLetter(c) || char.IsUpper(c));
            if (!hasLetter || !allUpper) return false;

            // Не имя комнаты, если это одинокое служебное/техническое слово.
            if (t.Split(' ').All(s => { double d; return double.TryParse(s, out d) || s.Length == 1; })) return false;

            // Имена комнат обычно содержат кириллицу.
            if (!t.Any(IsCyrillic)) return false;

            // Помещения/зоны/стояки: либо префикс из белого списка, либо рисер-маркер (С1.xxx / С2.xxx).
            if (t.StartsWith("С1.", StringComparison.Ordinal) || t.StartsWith("С2.", StringComparison.Ordinal))
                return true;
            foreach (var p in RoomPrefixes)
                if (t.StartsWith(p, StringComparison.Ordinal)) return true;

            return false;
        }

        private static string Neighbours(List<GrdTextRun> runs, int i)
        {
            var sb = new StringBuilder();
            for (int k = Math.Max(0, i - 3); k <= Math.Min(runs.Count - 1, i + 3); k++)
            {
                if (k == i) continue;
                sb.Append(runs[k].Text).Append(" | ");
            }
            return sb.ToString().TrimEnd(' ', '|');
        }

        private static string FindNearestRoom(List<GrdRoom> rooms, List<GrdTextRun> runs, int idx)
        {
            // Ищем ближайший предшествующий ран, являющийся комнатой из списка, в разумном окне.
            long byteOffset = runs[idx].ByteOffset;
            for (int k = idx - 1; k >= 0 && k >= idx - 60; k--)
            {
                string t = runs[k].Text.Trim();
                if (rooms.Any(rm => string.Equals(rm.Name, t, StringComparison.Ordinal)))
                    return t;
            }
            return string.Empty;
        }
    }
}