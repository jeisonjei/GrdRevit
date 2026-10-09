using System;
using System.Collections.Generic;
using System.IO;

namespace GrdRevit.Core
{
    /// <summary>
    /// Настройки плагина: какие параметры экземпляра выставлять при применении типа,
    /// настройки поиска типа в проекте.
    /// </summary>
    public sealed class GrdSettings
    {
        /// <summary>Параметры экземпляра: имя + значение + способ записи.</summary>
        public List<InstanceParamSetting> InstanceParams { get; set; } = new List<InstanceParamSetting>();

        /// <summary>Приоритетная замена имени семейства (код-префикс -> имя семейства Revit).</summary>
        public Dictionary<string, string> FamilyNameOverrides { get; set; } = new Dictionary<string, string>();

        /// <summary>Точное соответствие "код прибора" -> "имя типа семейства Revit".</summary>
        public Dictionary<string, string> TypeNameExactMap { get; set; } = new Dictionary<string, string>();

        /// <summary>Соответствие "марка из DXF" -> семейство и тип семейства Revit.
        /// Заполняется в окне «Марки DXF» (настройки плана DXF); при применении марки
        /// к элементу ищется тип с таким именем семейства и именем типа.
        /// Старые значения только с типом (без семейства) поддерживаются.</summary>
        public Dictionary<string, MarkTypeEntry> MarkTypeMap { get; set; } = new Dictionary<string, MarkTypeEntry>();

        /// <summary>Сопоставление значений прибора -> имя параметра экземпляра Revit.
        /// Ключами являются фиксированные источники: "Фhl" и "Valve setting".</summary>
        public Dictionary<string, string> ValueParamMap { get; set; } = new Dictionary<string, string>();

        /// <summary>Помарковая карта «марка из DXF -> параметры экземпляра Revit».
        /// Для каждой распознанной марки можно указать, в какие параметры записывать
        /// её значения (мощность, настройку клапана, текст выноски). Поля, оставленные
        /// пустыми, берутся из глобальной карты <see cref="ValueParamMap"/>.</summary>
        public Dictionary<string, MarkValueParamEntry> MarkValueMap { get; set; } = new Dictionary<string, MarkValueParamEntry>();

        /// <summary>Включать ли колонку "Кол-во" в таблице.</summary>
        public bool ShowCount { get; set; } = true;

        /// <summary>Создавать ли transaction отдельно для копирования типа.</summary>
        public bool WrapInTransaction { get; set; } = true;

        /// <summary>Высота 3D-вида-фрагмента по умолчанию (в метрах) для кнопки «3D-фрагмент».</summary>
        public double Default3DBoxHeight { get; set; } = 3.0;

        /// <summary>Последний угол «Супер Поворота» в градусах (0 = ещё не задан, спросим при первом запуске).
        /// Положительное значение — против часовой стрелки, отрицательное — по часовой.</summary>
        public double SuperRotateAngleDeg { get; set; } = 0.0;

        /// <summary>Имя последнего применённого шаблона 3D-вида (кнопка «3D-фрагмент»):
        /// применяется к создаваемому фрагменту автоматически.</summary>
        public string Last3DTemplateName { get; set; } = string.Empty;

        /// <summary>Путь к файлу .grd, загруженному последний раз.</summary>
        public string LastGrdPath { get; set; } = string.Empty;

        /// <summary>Путь к файлу «помещения и радиаторы» (room-radiator.txt).</summary>
        public string LastRoomsPath { get; set; } = string.Empty;

        /// <summary>Путь к файлу настроек клапанов (valve-settings.txt).</summary>
        public string LastValveSettingsPath { get; set; } = string.Empty;

        /// <summary>Путь к файлу плана .dxf, проанализированному в окне «Марки DXF» (настройки плана).</summary>
        public string LastDxfPath { get; set; } = string.Empty;

        /// <summary>Путь к последнему загруженному файлу общих параметров Revit (.txt).</summary>
        public string LastSharedParamsPath { get; set; } = string.Empty;

        /// <summary>Полный путь к последнему загруженному файлу (для восстановления при запуске).</summary>
        public List<SharedParamDef> SharedParamDefs { get; set; } = new List<SharedParamDef>();

        /// <summary>Папка последнего экспорта спецификации в Calc (для SaveFileDialog по умолчанию).</summary>
        public string ScheduleExportDir { get; set; } = string.Empty;

        /// <summary>Имя файла последнего экспорта спецификации в Calc.</summary>
        public string ScheduleExportName { get; set; } = string.Empty;

        /// <summary>Папка последнего сохранения PDF со снимком спецификации («Снимок спецификаций»).</summary>
        public string ScheduleSnapPdfDir { get; set; } = string.Empty;

        /// <summary>Отмеченные для печати листы по документам: ключ документа -> номера листов,
        /// разделённые переводом строки (окно «Печать в PDF»).</summary>
        public Dictionary<string, string> SheetSelectionByDoc { get; set; } = new Dictionary<string, string>();

        /// <summary>Полный путь последнего экспорта листов в DWG (окно «Экспорт в DWG»):
        /// подставляется в диалог сохранения при следующем открытии.</summary>
        public string SheetDwgExportPath { get; set; } = string.Empty;

        /// <summary>Держать окна плагина поверх всех остальных окон (Topmost).</summary>
        public bool WindowsTopmost { get; set; } = false;

        /// <summary>Ширины колонок таблицы «Управляющий видами»: заголовок колонки ->
        /// ширина в пикселях. Сохраняется между запусками Revit после ручного
        /// изменения ширины пользователем (звёздочные колонки не запоминаются).</summary>
        public Dictionary<string, double> ViewColumnWidths { get; set; } = new Dictionary<string, double>();

        /// <summary>Галочка «Перенос строк» в таблице «Управляющий видами».</summary>
        public bool ViewWrapText { get; set; } = false;

        /// <summary>Параметр пространства Revit для приточной системы (импорт .tomat).</summary>
        public string TomatSupplySystemParam { get; set; } = "Приточная система";

        /// <summary>Параметр пространства Revit для вытяжной системы (импорт .tomat).</summary>
        public string TomatExhaustSystemParam { get; set; } = "Вытяжная система";

        /// <summary>Параметр пространства Revit для расхода приточного воздуха (импорт .tomat).</summary>
        public string TomatSupplyAirflowParam { get; set; } = "Расход приточного воздуха";

        /// <summary>Параметр пространства Revit для расхода вытяжного воздуха (импорт .tomat).</summary>
        public string TomatExhaustAirflowParam { get; set; } = "Расход вытяжного воздуха";

        /// <summary>Путь к файлу .tomat, загруженному последний раз.</summary>
        public string LastTomatPath { get; set; } = string.Empty;

        public static GrdSettings Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return new GrdSettings();
                return MiniJson.Deserialize(File.ReadAllText(path)) ?? new GrdSettings();
            }
            catch
            {
                return new GrdSettings();
            }
        }

        public void Save(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, MiniJson.Serialize(this));
            }
            catch { /* не критично */ }
        }
    }

    /// <summary>Привязка «марка из DXF» к семейству и типу Revit.</summary>
    public sealed class MarkTypeEntry
    {
        /// <summary>Имя семейства Revit (может быть пустым для старых записей).</summary>
        public string Family { get; set; } = string.Empty;

        /// <summary>Имя типа семейства Revit.</summary>
        public string Type { get; set; } = string.Empty;

        public bool IsEmpty => string.IsNullOrEmpty(Family) && string.IsNullOrEmpty(Type);

        public override string ToString()
        {
            return string.IsNullOrEmpty(Family) ? Type : $"{Family} :: {Type}";
        }
    }

    /// <summary>Параметры экземпляра для записи значений одной марки из DXF.
    /// Пустое поле = использовать глобальную карту ValueParamMap.</summary>
    public sealed class MarkValueParamEntry
    {
        /// <summary>Параметр для мощности прибора, Вт (источник «Фhl»).</summary>
        public string PowerParam { get; set; } = string.Empty;

        /// <summary>Параметр для настройки клапана (источник «Valve setting», «n=…»).</summary>
        public string ValveParam { get; set; } = string.Empty;

        /// <summary>Параметр для полного текста выноски (источник «Марка»).</summary>
        public string MarkParam { get; set; } = string.Empty;

        public bool IsEmpty => string.IsNullOrEmpty(PowerParam) && string.IsNullOrEmpty(ValveParam) && string.IsNullOrEmpty(MarkParam);
    }

    public sealed class InstanceParamSetting
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public ParamValueKind Kind { get; set; } = ParamValueKind.String;

        /// <summary>Метка, вставляемая над значением (например код прибора преобразуется в мощность).</summary>
        public string Display => string.IsNullOrEmpty(Name) ? "(без имени)" : $"{Name} = {Value}";
    }

    public enum ParamValueKind
    {
        String,
        Double,
        Int
    }
}