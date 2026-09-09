using System.Collections.Generic;

namespace GrdRevit.Core
{
    public enum DeviceKind
    {
        Unknown = 0,
        RadiatorHeating = 1,   // отопительный прибор СО
        PipeAccessory = 2      // трубопроводная арматура (зарезервировано)
    }

    /// <summary>Сырой текст-ран: непрерывная последовательность печатаемых символов (ASCII + кириллица) из .grd.</summary>
    public sealed class GrdTextRun
    {
        public string Text;
        public long ByteOffset;
        public int Order;
        public bool IsUppercase;
        public bool HasCyrillic;

        public override string ToString() => $"[{ByteOffset}] {Text}";
    }

    public sealed class GrdRoom
    {
        public string Name;
        public int Order;
    }

    /// <summary>Один экземпляр прибора/арматуры, найденный в файле.</summary>
    public sealed class GrdDevice
    {
        public DeviceKind Kind = DeviceKind.Unknown;
        public string Code;            // TPLCPL714T2L, HZ-814, GS-4-80 ...
        public int Order;
        public long ByteOffset;
        public double? CapacityW;      // из кода
        public string Diameter;        // диаметр подводки (таблица троек)
        public string Connection;      // T2L / T2 / пусто
        public string FamilyHint;      // "TEPLA Classic Plus" и т.п.
        public string IsTwinPipe;      // "двухтрубное" | "однотрубное" (признак)
        public string Room;            // лучшее совпадение по окрестности (может быть пустым)
        public string RawNeighbours;   // диагностика: сырые соседние раны
        public string ValveSetting;    // настройка клапана (редактируется пользователем)
        public string MissingReason;   // причина отсутствия кода/типа (пусто, если тип найден)
    }

    public sealed class GrdDocument
    {
        public string SourcePath;
        public string ProjectName;
        public List<GrdTextRun> Runs = new List<GrdTextRun>();
        public List<GrdDevice> Devices = new List<GrdDevice>();
        public List<GrdRoom> Rooms = new List<GrdRoom>();
        public int DeviceCount;

        /// <summary>Агрегированная статистика по типам (для таблицы).</summary>
        public List<GrdDeviceTypeStats> TypeStats = new List<GrdDeviceTypeStats>();
    }

    public sealed class GrdDeviceTypeStats
    {
        public string Code;
        public string FamilyHint;
        public double? CapacityW;
        public string Connection;
        public int Count;
        public List<string> Rooms = new List<string>();
    }

    /// <summary>Строка файла настроек клапанов (valve-settings.txt): один клапан/прибор.</summary>
    public sealed class ValveSettingRow
    {
        public string Room { get; set; }          // колонка 0 — помещение
        public string Diameter { get; set; }      // колонка 1 — диаметр (15/20/25)
        public string DeviceCode { get; set; }    // колонка 2 — код клапана/прибора (КТС-ВП2, D-2500015 ...)
        public string Setting { get; set; }       // колонка 3 — настройка клапана (1, 2, 1.5, 3 ...)
        public string Kv { get; set; }            // колонка 5 — пропускная способность (0.93 ...)
        public double? PowerW { get; set; }       // колонка 6 — тепловая мощность, Вт (ключ сопоставления с приборами)
        public int Order { get; set; }            // порядок в файле
    }
}