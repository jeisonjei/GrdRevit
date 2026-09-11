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

        /// <summary>Сопоставление значений прибора -> имя параметра экземпляра Revit.
        /// Ключами являются фиксированные источники: "Фhl" и "Valve setting".</summary>
        public Dictionary<string, string> ValueParamMap { get; set; } = new Dictionary<string, string>();

        /// <summary>Включать ли колонку "Кол-во" в таблице.</summary>
        public bool ShowCount { get; set; } = true;

        /// <summary>Создавать ли transaction отдельно для копирования типа.</summary>
        public bool WrapInTransaction { get; set; } = true;

        /// <summary>Высота 3D-вида-фрагмента по умолчанию (в метрах) для кнопки «3D-фрагмент».</summary>
        public double Default3DBoxHeight { get; set; } = 3.0;

        /// <summary>Путь к файлу .grd, загруженному последний раз.</summary>
        public string LastGrdPath { get; set; } = string.Empty;

        /// <summary>Путь к файлу «помещения и радиаторы» (room-radiator.txt).</summary>
        public string LastRoomsPath { get; set; } = string.Empty;

        /// <summary>Путь к файлу настроек клапанов (valve-settings.txt).</summary>
        public string LastValveSettingsPath { get; set; } = string.Empty;

        /// <summary>Путь к последнему загруженному файлу общих параметров Revit (.txt).</summary>
        public string LastSharedParamsPath { get; set; } = string.Empty;

        /// <summary>Определения общих параметров из последнего загруженного файла (для восстановления при запуске).</summary>
        public List<SharedParamDef> SharedParamDefs { get; set; } = new List<SharedParamDef>();

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