using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GrdRevit.Core
{
    /// <summary>
    /// Снимок рабочего состояния плагина: загруженные приборы, помещения и настройки
    /// клапанов. Сохраняется на диск при каждом изменении и хранится до тех пор, пока
    /// пользователь не очистит таблицы («Очистить») или не перезагрузит файлы.
    /// </summary>
    public sealed class SavedSnapshot
    {
        public string RoomsPath = string.Empty;
        public string ValveSettingsPath = string.Empty;
        public bool ValveFilled;
        public List<SavedDevice> Devices = new List<SavedDevice>();
        public List<SavedRoom> RoomsList = new List<SavedRoom>();
        public List<SavedValveRow> ValveRows = new List<SavedValveRow>();
    }

    public sealed class SavedDevice
    {
        public string Room = string.Empty;
        public string Code = string.Empty;
        public string FamilyHint = string.Empty;
        public string Diameter = string.Empty;
        public string Connection = string.Empty;
        public double? CapacityW;
        public string ValveSetting = string.Empty;
        public string MissingReason = string.Empty;
        public int Order;
    }

    public sealed class SavedRoom
    {
        public string Name = string.Empty;
        public int Order;
    }

    public sealed class SavedValveRow
    {
        public string Room = string.Empty;
        public string Diameter = string.Empty;
        public string DeviceCode = string.Empty;
        public string Setting = string.Empty;
        public string Kv = string.Empty;
        public double? PowerW;
        public int Order;
    }

    /// <summary>Чтение/запись снимка данных (JSON) в файл. Ошибки не выбрасываются.</summary>
    public static class SavedDataFile
    {
        public static void Save(string path, SavedSnapshot snap)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, Serialize(snap));
            }
            catch { /* не критично */ }
        }

        public static void Delete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* не критично */ }
        }

        public static SavedSnapshot Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var root = MiniJson.ParseTree(File.ReadAllText(path)) as Dictionary<string, object>;
                if (root == null) return null;
                return ReadSnapshot(root);
            }
            catch { return null; }
        }

        public static string Serialize(SavedSnapshot snap)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            WriteStr(sb, "RoomsPath", snap.RoomsPath);
            sb.Append(',');
            WriteStr(sb, "ValveSettingsPath", snap.ValveSettingsPath);
            sb.Append(',');
            WriteProp(sb, "ValveFilled", snap.ValveFilled ? "true" : "false");
            sb.Append(',');
            WriteProp(sb, "Devices", WriteDevices(snap.Devices));
            sb.Append(',');
            WriteProp(sb, "RoomsList", WriteRooms(snap.RoomsList));
            sb.Append(',');
            WriteProp(sb, "ValveRows", WriteValveRows(snap.ValveRows));
            sb.Append('}');
            return sb.ToString();
        }

        private static string WriteDevices(List<SavedDevice> items)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var d in items)
            {
                if (!first) sb.Append(',');
                sb.Append('{');
                WriteStr(sb, "Room", d.Room);
                sb.Append(',');
                WriteStr(sb, "Code", d.Code);
                sb.Append(',');
                WriteStr(sb, "FamilyHint", d.FamilyHint);
                sb.Append(',');
                WriteStr(sb, "Diameter", d.Diameter);
                sb.Append(',');
                WriteStr(sb, "Connection", d.Connection);
                sb.Append(',');
                WriteNum(sb, "CapacityW", d.CapacityW);
                sb.Append(',');
                WriteStr(sb, "ValveSetting", d.ValveSetting);
                sb.Append(',');
                WriteStr(sb, "MissingReason", d.MissingReason);
                sb.Append(',');
                WriteProp(sb, "Order", d.Order.ToString(CultureInfo.InvariantCulture));
                sb.Append('}');
                first = false;
            }
            return sb.Append(']').ToString();
        }

        private static string WriteRooms(List<SavedRoom> items)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var r in items)
            {
                if (!first) sb.Append(',');
                sb.Append('{');
                WriteStr(sb, "Name", r.Name);
                sb.Append(',');
                WriteProp(sb, "Order", r.Order.ToString(CultureInfo.InvariantCulture));
                sb.Append('}');
                first = false;
            }
            return sb.Append(']').ToString();
        }

        private static string WriteValveRows(List<SavedValveRow> items)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var v in items)
            {
                if (!first) sb.Append(',');
                sb.Append('{');
                WriteStr(sb, "Room", v.Room);
                sb.Append(',');
                WriteStr(sb, "Diameter", v.Diameter);
                sb.Append(',');
                WriteStr(sb, "DeviceCode", v.DeviceCode);
                sb.Append(',');
                WriteStr(sb, "Setting", v.Setting);
                sb.Append(',');
                WriteStr(sb, "Kv", v.Kv);
                sb.Append(',');
                WriteNum(sb, "PowerW", v.PowerW);
                sb.Append(',');
                WriteProp(sb, "Order", v.Order.ToString(CultureInfo.InvariantCulture));
                sb.Append('}');
                first = false;
            }
            return sb.Append(']').ToString();
        }

        private static SavedSnapshot ReadSnapshot(Dictionary<string, object> root)
        {
            var snap = new SavedSnapshot
            {
                RoomsPath = GetStr(root, "RoomsPath"),
                ValveSettingsPath = GetStr(root, "ValveSettingsPath"),
                ValveFilled = GetBool(root, "ValveFilled")
            };

            if (root.TryGetValue("Devices", out var dl) && dl is IList<object> listDev)
            {
                foreach (var item in listDev)
                {
                    if (!(item is Dictionary<string, object> m)) continue;
                    snap.Devices.Add(new SavedDevice
                    {
                        Room = GetStr(m, "Room"),
                        Code = GetStr(m, "Code"),
                        FamilyHint = GetStr(m, "FamilyHint"),
                        Diameter = GetStr(m, "Diameter"),
                        Connection = GetStr(m, "Connection"),
                        CapacityW = GetDbl(m, "CapacityW"),
                        ValveSetting = GetStr(m, "ValveSetting"),
                        MissingReason = GetStr(m, "MissingReason"),
                        Order = GetInt(m, "Order")
                    });
                }
            }

            if (root.TryGetValue("RoomsList", out var rl) && rl is IList<object> listRooms)
            {
                foreach (var item in listRooms)
                {
                    if (!(item is Dictionary<string, object> m)) continue;
                    snap.RoomsList.Add(new SavedRoom
                    {
                        Name = GetStr(m, "Name"),
                        Order = GetInt(m, "Order")
                    });
                }
            }

            if (root.TryGetValue("ValveRows", out var vl) && vl is IList<object> listValve)
            {
                foreach (var item in listValve)
                {
                    if (!(item is Dictionary<string, object> m)) continue;
                    snap.ValveRows.Add(new SavedValveRow
                    {
                        Room = GetStr(m, "Room"),
                        Diameter = GetStr(m, "Diameter"),
                        DeviceCode = GetStr(m, "DeviceCode"),
                        Setting = GetStr(m, "Setting"),
                        Kv = GetStr(m, "Kv"),
                        PowerW = GetDbl(m, "PowerW"),
                        Order = GetInt(m, "Order")
                    });
                }
            }

            return snap;
        }

        private static string GetStr(Dictionary<string, object> m, string key)
        {
            return m.TryGetValue(key, out var v) ? (v as string) ?? string.Empty : string.Empty;
        }

        private static int GetInt(Dictionary<string, object> m, string key)
        {
            var s = GetStr(m, key);
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
        }

        private static double? GetDbl(Dictionary<string, object> m, string key)
        {
            var s = GetStr(m, key);
            if (string.IsNullOrEmpty(s)) return null;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
        }

        private static bool GetBool(Dictionary<string, object> m, string key)
        {
            return m.TryGetValue(key, out var v) && (v is bool b ? b : (v as string) == "true");
        }

        private static void WriteProp(StringBuilder sb, string name, string valueJson)
        {
            sb.Append('"').Append(name).Append("\":").Append(valueJson);
        }

        private static void WriteStr(StringBuilder sb, string name, string value)
        {
            WriteProp(sb, name, "\"" + MiniJson.Encode(value ?? string.Empty) + "\"");
        }

        private static void WriteNum(StringBuilder sb, string name, double? value)
        {
            WriteProp(sb, name, value.HasValue
                ? value.Value.ToString("0.########", CultureInfo.InvariantCulture)
                : "null");
        }
    }
}