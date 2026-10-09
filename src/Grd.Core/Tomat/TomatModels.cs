using System.Collections.Generic;

namespace GrdRevit.Core
{
    /// <summary>Точка полигона помещения (в единицах чертежа программы).</summary>
    public sealed class TomatPoint
    {
        public TomatPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    /// <summary>
    /// Помещение из файла .tomat. Объединяет строку таблицы теплопотерь
    /// (<c>heatLossRoomRows</c>), данные воздухообмена (<c>airChanges</c>)
    /// и полигон из плана (<c>shapes</c>).
    /// </summary>
    public sealed class TomatRoom
    {
        /// <summary>Идентификатор помещения в файле (guid из heatLossRoomRows.id).</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Номер этажа.</summary>
        public int FloorNumber { get; set; }

        /// <summary>Номер помещения (например «0.01»).</summary>
        public string RoomNumber { get; set; } = string.Empty;

        /// <summary>Наименование помещения.</summary>
        public string RoomName { get; set; } = string.Empty;

        /// <summary>Расчётная температура помещения, °C (может отсутствовать).</summary>
        public double? IndoorTemperatureC { get; set; }

        /// <summary>Площадь помещения, м².</summary>
        public double? AreaSqM { get; set; }

        /// <summary>Высота помещения, м.</summary>
        public double? HeightM { get; set; }

        /// <summary>Расход холодного вентиляционного воздуха, м³/ч (инфильтрация).</summary>
        public double? ColdVentilationAirflowM3H { get; set; }

        /// <summary>Расход приточного воздуха, м³/ч (максимальный по периодам).</summary>
        public double? SupplyM3H { get; set; }

        /// <summary>Расход вытяжного воздуха, м³/ч (максимальный по периодам).</summary>
        public double? ExhaustM3H { get; set; }

        /// <summary>Идентификатор приточной системы (например «п1»).</summary>
        public string SupplySystemId { get; set; } = string.Empty;

        /// <summary>Идентификатор вытяжной системы (например «в2»).</summary>
        public string ExhaustSystemId { get; set; } = string.Empty;

        /// <summary>Кратность притока, 1/ч.</summary>
        public double? SupplyRatePerHour { get; set; }

        /// <summary>Кратность вытяжки, 1/ч.</summary>
        public double? ExhaustRatePerHour { get; set; }

        /// <summary>Источник данных строки (heatLossRoomRows.source).</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>Контур помещения из плана (в единицах чертежа).</summary>
        public List<TomatPoint> Polygon { get; set; } = new List<TomatPoint>();

        public bool HasPolygon => Polygon.Count >= 3;

        /// <summary>Подпись строки для окна соответствия.</summary>
        public string DisplayName
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                if (FloorNumber != 0) sb.Append(FloorNumber).Append(" этаж • ");
                if (!string.IsNullOrEmpty(RoomNumber)) sb.Append(RoomNumber).Append(" • ");
                if (!string.IsNullOrEmpty(RoomName)) sb.Append(RoomName);
                else if (sb.Length == 0) sb.Append(Id);
                return sb.ToString();
            }
        }
    }

    /// <summary>Результат разбора файла .tomat.</summary>
    public sealed class TomatFile
    {
        public List<string> ParseMessages { get; } = new List<string>();

        /// <summary>Помещения файла (порядок таблицы heatLossRoomRows).</summary>
        public List<TomatRoom> Rooms { get; } = new List<TomatRoom>();

        /// <summary>Версия схемы файла (schemaVersion), если есть.</summary>
        public string SchemaVersion { get; set; } = string.Empty;

        /// <summary>
        /// Масштаб чертежа: сколько метров в одной единице координат (медиана по
        /// помещениям, где известна площадь и есть полигон). Может отсутствовать.
        /// </summary>
        public double? UnitsToMeters { get; set; }
    }
}