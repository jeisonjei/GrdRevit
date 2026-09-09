using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GrdRevit.Core
{
    public enum GrrSectionKind
    {
        Other = 0,
        Header = 1,          // шапка проекта (Москва, адрес)
        Scheme = 2,          // название схемы + стояки
        Devices = 3,         // блоки [код]+ ИТП [локация] — отопительные приборы
        PlainList = 4,       // сплошной перечень кодов приборов без ИТП (ведомость)
        Tubes = 5,           // трубы/участки (ПЕНКА PE 1, ГОСТ ...)
        Armature = 6,        // арматура: блоки с "Выход из распределителя"
        Reference = 7        // справочник производителей и параметры ("ПО УМОЛЧАНИЮ ...", HEIZEN ...)
    }

    /// <summary>Одна строка-прибор из чистого разбора .grr.</summary>
    public sealed class GrrDeviceRow
    {
        public string Code;
        public string FamilyHint;
        public double? CapacityW;
        public string Connection;
        public string Room;
        public string Diameter;
        public string Marker;
        public string BlockKind;   // Devices / PlainList / Armature
        public int Run;
        public long ByteOffset;
    }

    /// <summary>Логический раздел внутри .grr (вкладка/таблица).</summary>
    public sealed class GrrSection
    {
        public GrrSectionKind Kind;
        public string Name;
        public int FirstRun;
        public int LastRun;
        public long FirstByteOffset;
        public List<GrrDeviceRow> Devices = new List<GrrDeviceRow>();
        public int RunCount => LastRun - FirstRun + 1;
    }

    public sealed class GrrCleanResult
    {
        public string SourcePath;
        public string ProjectName;
        public string SchemeTitle;
        public List<GrdTextRun> Runs = new List<GrdTextRun>();
        public List<GrrSection> Sections = new List<GrrSection>();
        public List<GrrDeviceRow> Devices = new List<GrrDeviceRow>();
        public List<string> Rooms = new List<string>();
        public int DeviceCount;
        public int DeviceKindCount => Devices.Count(d => d.BlockKind != "Armature");
        public int ArmatureCount => Devices.Count(d => d.BlockKind == "Armature");
    }

    /// <summary>
    /// Чистый разбор .grr (RES7.00) на логические разделы с таблицами приборов.
    /// Структура файла (по результатам исследования):
    ///   4..12   — название схемы/стояки (Ст.1.2, Ст.1.1 ...)
    ///   13..174 — блоки [код]+ ИТП [комната] [диаметр] [маркер]  (отопительные приборы)
    ///   175..609— ведомость кодов TPLCPL/TPLNE без ИТП (квартирные конвекторы, комнат нет)
    ///   610..657— GS-4-80, ИТП, СТОЯНКА/НАСОСНАЯ/ВЕНТКАМЕРА
    ///   658..1709— HZ-814: [HZ 814] [32 [2]] [HZ-814] ИТП [комната] [диаметр] [Весь коллектор]
    ///   1710..   — трубы/участки (А, А!, ПЕНКА PE 1, ГОСТ ...)
    ///   ~11733..15980 — арматура: [код] ИТП [секция] р/п 32 Выход из распределителя
    ///   15981+   — справочник производителей (HEIZEN, PRADEX ...)
    /// </summary>
    public static class GrrCleanParser
    {
        private const string ArmMarker = "Выход из распределителя";

        private static readonly string[] TubeMarkers =
        {
            "ПЕНКА PE 1", "РОСТ-PEXA-EVOH 7.4", "PR ПНД КР", "ГОСТ 3262-75 O", "ГОСТ 10704-91", "А", "А!", "В гофре"
        };

        // Токены, которые прерывают поиск локации/диаметра.
        private static readonly HashSet<string> StopTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "ИТП", "ПЕНКА PE 1", "РОСТ-PEXA-EVOH 7.4", "PR ПНД КР", "ГОСТ 3262-75 O", "ГОСТ 10704-91",
            "А", "А!", "Весь коллектор", ArmMarker
        };

        // Белый список служебных маркеров подключения/подписи.
        private static readonly HashSet<string> MarkerTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "Весь коллектор", ArmMarker, "lb", "OD", "vl", "J@", "]F", "]S", "SF", "BdZ", "ГВ", "ХВ", "ОБР",
            "ЛЕЖАК", "СТОЯНКА", "НАСОСНАЯ", "ВЕНТКАМЕРА ПРИТОЧНАЯ", "ВЕНТКАМЕРА ПОДПОРА"
        };

        private static readonly string[] DiameterPatterns =
        {
            "15", "16", "16x2,2", "16x2,0", "20", "20x2,8", "25", "25x3,5", "32", "40", "50", "63",
            "15 [2]", "16 [2]", "16x2,2 [2]", "20 [2]", "25 [2]", "32 [2]", "32 [3]", "32 [4]", "32 [5]",
            "32 [6]", "32 [7]", "32 [8]", "32 [9]"
        };

        public static GrrCleanResult Parse(string path)
        {
            var res = new GrrCleanResult { SourcePath = path };
            var runs = GrdParser.ExtractRuns(File.ReadAllBytes(path));
            res.Runs = runs;
            if (runs.Count == 0) return res;

            res.ProjectName = BuildProjectName(runs);
            res.SchemeTitle = FindSchemeTitle(runs);

            var devices = new List<GrrDeviceRow>();
            var consumed = new bool[runs.Count];

            // ---- Проход 1: ИТП-блоки ------------------------------------------------
            for (int i = 0; i < runs.Count; i++)
            {
                if (!string.Equals(runs[i].Text.Trim(), "ИТП", StringComparison.OrdinalIgnoreCase)) continue;
                var b = ReadItpBlock(runs, i, devices);
                if (b != null)
                    for (int k = b.FirstRun; k <= b.LastRun && k < consumed.Length; k++)
                        consumed[k] = true;
            }

            // ---- Проход 2: неиспользованные коды (ведомость) -------------------------
            int devZoneEnd = FindTubesStart(runs); // ведомость живёт в зоне приборов (до труб)
            if (devZoneEnd < 0) devZoneEnd = runs.Count;
            for (int i = 0; i < devZoneEnd; i++)
            {
                if (consumed[i]) continue;
                string t = runs[i].Text.Trim();
                if (!DeviceCodeParser.CanStartWith(t)) continue;
                if (!DeviceCodeParser.TryParse(t, out var dev)) continue;

                devices.Add(new GrrDeviceRow
                {
                    Code = dev.Code,
                    FamilyHint = dev.FamilyHint,
                    CapacityW = dev.CapacityW,
                    Connection = dev.Connection,
                    Room = string.Empty,
                    Diameter = string.Empty,
                    Marker = string.Empty,
                    BlockKind = "PlainList",
                    Run = i,
                    ByteOffset = runs[i].ByteOffset
                });
            }

            // ---- Сегментация на разделы ----------------------------------------------
            res.Devices = devices.OrderBy(d => d.Run).ToList();
            BuildSections(runs, res);
            res.DeviceCount = res.Devices.Count;
            res.Rooms = res.Devices
                .Where(d => !string.IsNullOrWhiteSpace(d.Room) && d.BlockKind != "Armature")
                .Select(d => d.Room)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToList();
            return res;
        }

        private static string BuildProjectName(List<GrdTextRun> runs)
        {
            return string.Join(", ", runs.Take(2).Select(r => r.Text.Trim()).Where(t => t.Length > 0));
        }

        private static string FindSchemeTitle(List<GrdTextRun> runs)
        {
            for (int i = 0; i < runs.Count && i < 20; i++)
            {
                string t = runs[i].Text.Trim();
                if (t.IndexOf("схема", StringComparison.OrdinalIgnoreCase) >= 0) return t;
            }
            return string.Empty;
        }

        /// <summary>Разбор одного блока с "ИТП": устройства в окне ДО "ИТП" получают локацию ПОСЛЕ "ИТП".</summary>
        private static GrrItpBlock ReadItpBlock(List<GrdTextRun> runs, int itp, List<GrrDeviceRow> devices)
        {
            // Коды ДО "ИТП" (окно 14 ранов); дедуп: HZ 814 == HZ-814.
            string loc = string.Empty;
            string diameter = string.Empty;
            string marker = string.Empty;
            int firstCodeRun = -1;
            var codes = new List<GrdDevice>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Идём от "ИТП" вверх до границы блока (14 ранов) или до предыдущего "ИТП".
            int scanBack = Math.Min(14, itp);
            int windowStart = itp - scanBack;
            for (int k = 1; k <= scanBack; k++)
            {
                string t = runs[itp - k].Text.Trim();
                if (t.Length == 0) continue;
                if (string.Equals(t, "ИТП", StringComparison.OrdinalIgnoreCase))
                {
                    windowStart = itp - k;
                    break;
                }
            }

            for (int k = windowStart; k < itp; k++)
            {
                string t = runs[k].Text.Trim();
                if (t.Length == 0) continue;
                if (!DeviceCodeParser.CanStartWith(t)) continue;
                if (!DeviceCodeParser.TryParse(t, out var dev)) continue;
                if (!seen.Add(dev.Code)) continue;
                if (firstCodeRun < 0) firstCodeRun = k;
                codes.Add(dev);
            }
            if (codes.Count == 0) return null; // без кода — труба/участок, не прибор

            // Локация и диаметр: токены после "ИТП" до границы блока (12 ранов или следующий "ИТП").
            int blockEnd = itp;
            for (int j = 1; j <= 12 && itp + j < runs.Count; j++)
            {
                if (string.Equals(runs[itp + j].Text.Trim(), "ИТП", StringComparison.OrdinalIgnoreCase))
                {
                    blockEnd = itp + j;
                    break;
                }
            }
            for (int j = itp + 1; j < blockEnd; j++)
            {
                string s = runs[j].Text.Trim();
                if (s.Length == 0 || StopTokens.Contains(s)) continue;
                if (string.Equals(s, "ИТП", StringComparison.OrdinalIgnoreCase)) continue;
                if (!s.Any(char.IsLetter)) continue;
                if (DeviceCodeParser.CanStartWith(s)) continue;
                if (GrdParser.PublicNotRooms.Contains(s)) continue;
                loc = s;
                break;
            }

            // Диаметр: первый "числовой" токен в блоке после ИТП.
            diameter = FindDiameter(runs, itp + 1, blockEnd);
            // Маркер: белый список служебных токенов в блоке после ИТП.
            for (int j = itp + 1; j < blockEnd; j++)
            {
                string s = runs[j].Text.Trim();
                if (MarkerTokens.Contains(s))
                {
                    marker = s;
                    if (string.Equals(s, ArmMarker, StringComparison.Ordinal)) break;
                }
            }

            bool isArmature = string.Equals(marker, ArmMarker, StringComparison.Ordinal);
            if (isArmature) loc = string.Empty; // арматура: локация — секция (С1.207), не прибор

            string blockKind = isArmature ? "Armature" : "Devices";
            int lastRun = Math.Max(firstCodeRun, blockEnd - 1);
            foreach (var cd in codes)
            {
                devices.Add(new GrrDeviceRow
                {
                    Code = cd.Code,
                    FamilyHint = cd.FamilyHint,
                    CapacityW = cd.CapacityW,
                    Connection = cd.Connection,
                    Room = loc,
                    Diameter = isArmature ? string.Empty : diameter,
                    Marker = marker,
                    BlockKind = blockKind,
                    Run = firstCodeRun,
                    ByteOffset = runs[firstCodeRun].ByteOffset
                });
            }
            return new GrrItpBlock { FirstRun = firstCodeRun, LastRun = lastRun };
        }

        private static string FindDiameter(List<GrdTextRun> runs, int from, int to)
        {
            for (int j = from; j < runs.Count && j <= to; j++)
            {
                string s = runs[j].Text.Trim();
                if (s.Length == 0) continue;
                if (StopTokens.Contains(s)) continue;
                if (string.Equals(s, "ИТП", StringComparison.OrdinalIgnoreCase)) continue;
                if (DeviceCodeParser.CanStartWith(s)) continue;
                if (s.Any(char.IsLetter) && !s.Contains('x') && !s.Contains('[')) continue; // имя
                if (DiameterPatterns.Contains(s, StringComparer.OrdinalIgnoreCase)) return s;
                if (!s.Any(char.IsLetter) || s.Contains('x') || s.Contains('['))
                    return s; // "16x2,2", "32 [2]", "100"
            }
            return string.Empty;
        }

        private static void BuildSections(List<GrdTextRun> runs, GrrCleanResult res)
        {
            int n = runs.Count;
            int tubesStart = FindTubesStart(runs);
            int armatureStart = -1, referenceStart = -1;
            for (int i = 0; i < n; i++)
            {
                string t = runs[i].Text.Trim();
                if (armatureStart < 0 && string.Equals(t, ArmMarker, StringComparison.Ordinal)
                    && tubesStart >= 0 && i > tubesStart)
                    armatureStart = i;
                if (referenceStart < 0 && (t.StartsWith("ПО УМОЛЧАНИЮ", StringComparison.Ordinal)
                                           || string.Equals(t, "HEIZEN", StringComparison.Ordinal)
                                           || t.StartsWith("PF_Dat", StringComparison.Ordinal)))
                    referenceStart = i;
            }

            int firstDevRun = res.Devices.Count > 0 ? res.Devices.Min(d => d.Run) : 13;

            int headerLast = 3;
            int schemeFirst = Math.Min(headerLast + 1, n - 1);
            int schemeLast = Math.Max(schemeFirst, Math.Min(firstDevRun, (tubesStart > 0 ? tubesStart : n)) - 1);
            if (schemeLast < schemeFirst) schemeLast = schemeFirst;

            int devFirst = firstDevRun;
            int devLast = tubesStart > 0 ? tubesStart - 1 : n - 1;
            if (tubesStart < 0 && armatureStart > 0) devLast = armatureStart - 1;

            int tubeFirst = tubesStart < 0 ? (armatureStart > 0 ? armatureStart : n) : tubesStart;
            int tubeLast = (armatureStart > 0 ? armatureStart - 1 : (referenceStart > 0 ? referenceStart - 1 : n - 1));

            var devs = res.Devices.Where(d => d.BlockKind == "Devices").ToList();
            var plDevs = res.Devices.Where(d => d.BlockKind == "PlainList").ToList();
            var armDevs = res.Devices.Where(d => d.BlockKind == "Armature").ToList();

            var sections = new List<GrrSection>();
            AddSection(sections, GrrSectionKind.Header, "Шапка проекта", 0, Math.Min(headerLast, n - 1), runs, null);
            if (schemeFirst <= schemeLast)
                AddSection(sections, GrrSectionKind.Scheme, "Название схемы и стояки", schemeFirst, schemeLast, runs, null);
            if (devFirst <= devLast)
                AddSection(sections, GrrSectionKind.Devices, "Отопительные приборы СО", devFirst, devLast, runs, devs);
            if (plDevs.Count > 0)
                AddSection(sections, GrrSectionKind.PlainList, "Ведомость приборов (без комнат)", plDevs.Min(d => d.Run),
                    plDevs.Max(d => d.Run), runs, plDevs);
            if (tubeFirst <= tubeLast)
                AddSection(sections, GrrSectionKind.Tubes, "Трубы и участки СО", tubeFirst, tubeLast, runs, null);
            if (armatureStart >= 0)
                AddSection(sections, GrrSectionKind.Armature, "Арматура СО (выходы из распределителя)",
                    armatureStart, referenceStart > 0 ? referenceStart - 1 : n - 1, runs, armDevs);
            if (referenceStart >= 0)
                AddSection(sections, GrrSectionKind.Reference, "Справочник: производители и параметры",
                    referenceStart, n - 1, runs, null);

            res.Sections = sections.OrderBy(s => s.FirstRun).ToList();
        }

        /// <summary>Первое появление маркеров труб/раздела "Отопительные приборы" (0-based или -1).</summary>
        private static int FindTubesStart(List<GrdTextRun> runs)
        {
            for (int i = 0; i < runs.Count; i++)
            {
                string t = runs[i].Text.Trim();
                if (TubeMarkers.Contains(t, StringComparer.Ordinal))
                    return i;
            }
            return -1;
        }

        private static void AddSection(List<GrrSection> list, GrrSectionKind kind, string name, int first, int last,
            List<GrdTextRun> runs, List<GrrDeviceRow> devs)
        {
            first = Math.Max(0, Math.Min(first, runs.Count - 1));
            last = Math.Max(first, Math.Min(last, runs.Count - 1));
            list.Add(new GrrSection
            {
                Kind = kind,
                Name = name,
                FirstRun = first,
                LastRun = last,
                FirstByteOffset = first < runs.Count ? runs[first].ByteOffset : 0,
                Devices = devs ?? new List<GrrDeviceRow>()
            });
        }

        private sealed class GrrItpBlock
        {
            public int FirstRun;
            public int LastRun;
        }
    }
}