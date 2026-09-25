using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GrdRevit.Core
{
    /// <summary>Сохранённый снимок одной спецификации: заголовки и строки с правками
    /// пользователя. Хранится в JSON и восстанавливается при повторном открытии окна
    /// «Снимок спецификаций» независимо от сеансов Revit.</summary>
    public sealed class SerializedSchedule
    {
        public long ScheduleId;
        public string ScheduleName = string.Empty;
        public List<string> Headers = new List<string>();
        public List<string[]> Rows = new List<string[]>();

        /// <summary>Свободная спецификация: содержимое не берётся из модели, а живёт только здесь.
        /// Заголовки скопированы со спецификации-образца при создании.</summary>
        public bool IsFree;

        /// <summary>Id спецификации-образца, с которой скопированы заголовки (0 — не задан).</summary>
        public long TemplateScheduleId;

        /// <summary>Лист, на котором размещена спецификация (0 у старых записей без листа).</summary>
        public long SheetId;

        /// <summary>Номер сегмента спецификации на листе. Разделённая спецификация хранит свои
        /// правки в отдельной записи для каждого листа/сегмента; -1 — неразделённая.</summary>
        public int SegmentIndex;
    }

    /// <summary>Чтение/запись сохранённых правок снимков спецификаций (JSON).
    /// Файл один на все документы; разделение по ключу документа (путь или название).
    /// Ошибки не выбрасываются: сохранение не должно влиять на работу Revit.</summary>
    public static class ScheduleSnapshotsFile
    {
        private static readonly object Sync = new object();

        private sealed class SnapDoc
        {
            public string Key = string.Empty;
            public List<SerializedSchedule> Schedules = new List<SerializedSchedule>();
        }

        public static string StoragePath
        {
            get
            {
                try
                {
                    return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "GrdRevit", "schedule-snapshots.json");
                }
                catch { return "schedule-snapshots.json"; }
            }
        }

        public static void SaveEntry(string docKey, SerializedSchedule sched)
        {
            try
            {
                lock (Sync)
                {
                    var docs = LoadAll();
                    var doc = docs.Find(d => d.Key == docKey);
                    if (doc == null)
                    {
                        doc = new SnapDoc { Key = docKey };
                        docs.Add(doc);
                    }
                    // Запись уникальна для (спецификация, лист, сегмент): разделённая спецификация
                    // на разных листах не должна затирать правки друг друга.
                    doc.Schedules.RemoveAll(s => s.ScheduleId == sched.ScheduleId &&
                                                 s.SheetId == sched.SheetId &&
                                                 s.SegmentIndex == sched.SegmentIndex);
                    doc.Schedules.Add(sched);
                    WriteAll(docs);
                }
            }
            catch { /* не критично */ }
        }

        public static SerializedSchedule LoadEntry(string docKey, long scheduleId, long sheetId, int segmentIndex)
        {
            try
            {
                lock (Sync)
                {
                    var docs = LoadAll();
                    var doc = docs.Find(d => d.Key == docKey);
                    return doc == null ? null : FindMatch(doc.Schedules, scheduleId, sheetId, segmentIndex);
                }
            }
            catch { return null; }
        }

        private static SerializedSchedule FindMatch(List<SerializedSchedule> list, long scheduleId,
                                                    long sheetId, int segmentIndex)
        {
            var exact = list.Find(s => s.ScheduleId == scheduleId && s.SheetId == sheetId &&
                                       s.SegmentIndex == segmentIndex);
            if (exact != null) return Copy(exact);
            // Старые записи без листа (SheetId==0) подходили для любой размещённой копии;
            // для неразделённой спецификации оставляем их рабочими.
            if (segmentIndex <= 0)
            {
                var legacy = list.Find(s => s.ScheduleId == scheduleId && s.SheetId == 0 && s.SegmentIndex <= 0);
                if (legacy != null) return Copy(legacy);
            }
            return null;
        }

        private static SerializedSchedule Copy(SerializedSchedule s)
        {
            var copy = new SerializedSchedule
            {
                ScheduleId = s.ScheduleId,
                ScheduleName = s.ScheduleName,
                IsFree = s.IsFree,
                TemplateScheduleId = s.TemplateScheduleId,
                SheetId = s.SheetId,
                SegmentIndex = s.SegmentIndex
            };
            copy.Headers.AddRange(s.Headers);
            foreach (var row in s.Rows) copy.Rows.Add((string[])row.Clone());
            return copy;
        }

        public static void DeleteEntry(string docKey, long scheduleId, long sheetId, int segmentIndex)
        {
            try
            {
                lock (Sync)
                {
                    var docs = LoadAll();
                    var doc = docs.Find(d => d.Key == docKey);
                    if (doc == null) return;
                    doc.Schedules.RemoveAll(s => s.ScheduleId == scheduleId && s.SheetId == sheetId &&
                                                 s.SegmentIndex == segmentIndex);
                    if (segmentIndex <= 0)
                        doc.Schedules.RemoveAll(s => s.ScheduleId == scheduleId && s.SheetId == 0 &&
                                                     s.SegmentIndex <= 0);
                    WriteAll(docs);
                }
            }
            catch { /* не критично */ }
        }

        // ------------------------------------------------------------- сериализация

        private static List<SnapDoc> LoadAll()
        {
            var list = new List<SnapDoc>();
            try
            {
                if (!File.Exists(StoragePath)) return list;
                var root = MiniJson.ParseTree(File.ReadAllText(StoragePath)) as Dictionary<string, object>;
                if (root == null || !root.TryGetValue("Docs", out var ds) || !(ds is IList<object> docs))
                    return list;
                foreach (var item in docs)
                {
                    if (!(item is Dictionary<string, object> m)) continue;
                    var d = new SnapDoc { Key = AsStr(m, "Key") };
                    if (m.TryGetValue("Schedules", out var ss) && ss is IList<object> scheds)
                    {
                        foreach (var si in scheds)
                        {
                            if (!(si is Dictionary<string, object> sm)) continue;
                            var sd = new SerializedSchedule
                            {
                                ScheduleId = long.TryParse(AsStr(sm, "ScheduleId"),
                                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0,
                                ScheduleName = AsStr(sm, "ScheduleName"),
                                IsFree = string.Equals(AsStr(sm, "IsFree"), "true", StringComparison.OrdinalIgnoreCase),
                                TemplateScheduleId = long.TryParse(AsStr(sm, "TemplateScheduleId"),
                                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var tid) ? tid : 0,
                                SheetId = long.TryParse(AsStr(sm, "SheetId"),
                                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var sid) ? sid : 0,
                                SegmentIndex = int.TryParse(AsStr(sm, "SegmentIndex"),
                                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var seg) ? seg : 0
                            };
                            if (sm.TryGetValue("Headers", out var hd) && hd is IList<object> hl)
                                foreach (var h in hl) sd.Headers.Add(AsStr(h));
                            if (sm.TryGetValue("Rows", out var rd) && rd is IList<object> rl)
                            {
                                foreach (var rowObj in rl)
                                {
                                    if (rowObj is IList<object> cells)
                                    {
                                        var arr = new string[cells.Count];
                                        for (int i = 0; i < cells.Count; i++) arr[i] = AsStr(cells[i]);
                                        sd.Rows.Add(arr);
                                    }
                                }
                            }
                            d.Schedules.Add(sd);
                        }
                    }
                    list.Add(d);
                }
            }
            catch { /* повреждённый файл — начинаем с пустого списка */ }
            return list;
        }

        private static void WriteAll(List<SnapDoc> docs)
        {
            try
            {
                var dir = Path.GetDirectoryName(StoragePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                sb.Append("{\"Docs\":[");
                bool firstDoc = true;
                foreach (var d in docs)
                {
                    if (!firstDoc) sb.Append(',');
                    sb.Append("{\"Key\":").Append(Quote(MiniJson.Encode(d.Key)))
                      .Append(",\"Schedules\":[");
                    bool firstS = true;
                    foreach (var s in d.Schedules)
                    {
                        if (!firstS) sb.Append(',');
                        sb.Append("{\"ScheduleId\":\"").Append(s.ScheduleId.ToString(CultureInfo.InvariantCulture))
                          .Append("\",\"ScheduleName\":").Append(Quote(MiniJson.Encode(s.ScheduleName)))
                          .Append(",\"IsFree\":\"").Append(s.IsFree ? "true" : "false")
                          .Append("\",\"TemplateScheduleId\":\"").Append(s.TemplateScheduleId.ToString(CultureInfo.InvariantCulture))
                          .Append("\",\"SheetId\":\"").Append(s.SheetId.ToString(CultureInfo.InvariantCulture))
                          .Append("\",\"SegmentIndex\":\"").Append(s.SegmentIndex.ToString(CultureInfo.InvariantCulture))
                          .Append("\",\"Headers\":[");
                        for (int i = 0; i < s.Headers.Count; i++)
                        {
                            if (i > 0) sb.Append(',');
                            sb.Append(Quote(MiniJson.Encode(s.Headers[i])));
                        }
                        sb.Append("],\"Rows\":[");
                        for (int r = 0; r < s.Rows.Count; r++)
                        {
                            if (r > 0) sb.Append(',');
                            sb.Append('[');
                            var row = s.Rows[r];
                            for (int c = 0; c < row.Length; c++)
                            {
                                if (c > 0) sb.Append(',');
                                sb.Append(Quote(MiniJson.Encode(row[c] ?? string.Empty)));
                            }
                            sb.Append(']');
                        }
                        sb.Append("]}");
                        firstS = false;
                    }
                    sb.Append("]}");
                    firstDoc = false;
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