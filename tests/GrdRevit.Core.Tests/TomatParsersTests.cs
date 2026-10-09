using System.Linq;
using GrdRevit.Core;
using Xunit;

namespace GrdRevit.Core.Tests
{
    /// <summary>Парсер файла .tomat: помещения, воздухообмен, полигоны, масштаб.</summary>
    public class TomatParsersTests
    {
        private const string SyntheticTomat =
            "{\n" +
            "\"schemaVersion\":\"3\",\n" +
            "\"meta\":{},\n" +
            "\"heatLossRoomRows\":[\n" +
            "{\"id\":\"A1\",\"floorNumber\":1,\"roomNumber\":\"0.01\",\"name\":\"Коридор\",\"indoorTemperatureC\":18,\"coldVentilationAirflowM3H\":30,\"areaSqM\":8.04,\"heightM\":3.2,\"source\":\"manual\",\"linkedShapeId\":\"S1\"},\n" +
            "{\"id\":\"A2\",\"floorNumber\":1,\"roomNumber\":\"0.02\",\"name\":\"Прихожая\",\"indoorTemperatureC\":\"\",\"areaSqM\":5.0,\"heightM\":3.0,\"source\":\"manual\"},\n" +
            "{\"id\":\"A3\",\"floorNumber\":2,\"roomNumber\":\"2.01\",\"name\":\"Кухня\",\"indoorTemperatureC\":20,\"areaSqM\":10.0,\"heightM\":3.0,\"source\":\"manual\",\"linkedShapeId\":\"S3\"}\n" +
            "],\n" +
            "\"airChanges\":{\n" +
            "\"roomData\":{\"A1\":{\"supplyRatePerHour\":2.5,\"exhaustRatePerHour\":3.0}},\n" +
            "\"rows\":[\n" +
            "{\"id\":\"R1\",\"roomId\":\"A1\",\"period\":\"warm\",\"supplyM3H\":150,\"exhaustM3H\":null,\"supplySystemId\":\"п1\",\"exhaustSystemId\":\"\"},\n" +
            "{\"id\":\"R2\",\"roomId\":\"A1\",\"period\":\"cold\",\"supplyM3H\":100,\"exhaustM3H\":200,\"supplySystemId\":\"п1\",\"exhaustSystemId\":\"в2\"},\n" +
            "{\"id\":\"R3\",\"roomId\":\"A2\",\"period\":\"warm\",\"supplyM3H\":50,\"exhaustM3H\":50,\"supplySystemId\":\"п1\",\"exhaustSystemId\":\"в1\"}\n" +
            "]\n" +
            "},\n" +
            "\"shapes\":[\n" +
            "{\"kind\":\"room-area\",\"id\":\"S1\",\"points\":[{\"id\":1,\"x\":0,\"y\":0},{\"id\":2,\"x\":100,\"y\":0},{\"id\":3,\"x\":100,\"y\":100},{\"id\":4,\"x\":0,\"y\":100}]},\n" +
            "{\"kind\":\"block\",\"id\":\"BLK1\",\"net\":{},\"points\":[{\"id\":1,\"x\":0,\"y\":0}]}\n" +
            "]\n" +
            "}";

        [Fact]
        public void Parser_ReadsRoomsAirAndPolygons()
        {
            var file = TomatParser.ParseText(SyntheticTomat);

            Assert.Empty(file.ParseMessages);
            Assert.Equal("3", file.SchemaVersion);
            Assert.Equal(3, file.Rooms.Count);

            var corridor = file.Rooms[0];
            Assert.Equal("A1", corridor.Id);
            Assert.Equal("Коридор", corridor.RoomName);
            Assert.Equal("0.01", corridor.RoomNumber);
            Assert.Equal(1, corridor.FloorNumber);
            Assert.Equal(8.04, corridor.AreaSqM);
            Assert.Equal(3.2, corridor.HeightM);
            Assert.Equal(18.0, corridor.IndoorTemperatureC);
            Assert.Equal(30.0, corridor.ColdVentilationAirflowM3H);

            // Периоды: приток = максимум по непустым (150 из warm), вытяжка = 200 из cold;
            // система вытяжки берётся первой непустой.
            Assert.Equal(150.0, corridor.SupplyM3H);
            Assert.Equal(200.0, corridor.ExhaustM3H);
            Assert.Equal("п1", corridor.SupplySystemId);
            Assert.Equal("в2", corridor.ExhaustSystemId);
            Assert.Equal(2.5, corridor.SupplyRatePerHour);
            Assert.Equal(3.0, corridor.ExhaustRatePerHour);

            Assert.True(corridor.HasPolygon);
            Assert.Equal(4, corridor.Polygon.Count);
            Assert.Equal(100, corridor.Polygon[2].X);
            Assert.Equal(100, corridor.Polygon[2].Y);

            var entry = file.Rooms[1];
            Assert.Equal("Прихожая", entry.RoomName);
            Assert.Null(entry.IndoorTemperatureC);
            Assert.Empty(entry.Polygon);
            Assert.Equal(50.0, entry.SupplyM3H);
            Assert.Equal(50.0, entry.ExhaustM3H);
            Assert.Equal("п1", entry.SupplySystemId);
            Assert.Equal("в1", entry.ExhaustSystemId);
            Assert.Null(entry.SupplyRatePerHour);

            var kitchen = file.Rooms[2];
            Assert.Equal("Кухня", kitchen.RoomName);
            Assert.Null(kitchen.SupplyM3H);
            Assert.Null(kitchen.ExhaustM3H);
            Assert.Empty(kitchen.SupplySystemId);
            Assert.Empty(kitchen.Polygon);
        }

        [Fact]
        public void Parser_ComputesUnitsToMetersFromPolygonAndArea()
        {
            var file = TomatParser.ParseText(SyntheticTomat);

            // Квадрат 100x100 усл. ед. = 10000 ед² соответствует 8.04 м².
            Assert.True(file.UnitsToMeters.HasValue);
            Assert.Equal(System.Math.Sqrt(8.04 / 10000.0), file.UnitsToMeters.Value, 6);
            Assert.Equal("1 этаж • 0.01 • Коридор", file.Rooms[0].DisplayName);
        }

        [Fact]
        public void Parser_MissingRoomTable_ReportsMessage()
        {
            var file = TomatParser.ParseText("{\"meta\":{},\"airChanges\":{\"rows\":[]}}");

            Assert.Empty(file.Rooms);
            Assert.Contains(file.ParseMessages, m => m.Contains("heatLossRoomRows"));
        }

        [Fact]
        public void Parser_EmptyRoomTable_NoCrash()
        {
            var file = TomatParser.ParseText("{\"heatLossRoomRows\":[],\"airChanges\":{\"rows\":[]},\"shapes\":[]}");

            Assert.Empty(file.Rooms);
            Assert.Empty(file.ParseMessages);
            Assert.Null(file.UnitsToMeters);
        }

        [Fact]
        public void Parser_GarbageText_ReportsMessage()
        {
            var file = TomatParser.ParseText("это не json и вообще не файл проекта");

            Assert.Empty(file.Rooms);
            Assert.Contains(file.ParseMessages, m => m.Contains("heatLossRoomRows"));
        }

        [Fact]
        public void Parser_MissingFile_ReportsMessage()
        {
            var file = TomatParser.Parse(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "нет_такого_файла_123.tomat"));

            Assert.Empty(file.Rooms);
            Assert.NotEmpty(file.ParseMessages);
        }

        [Fact]
        public void Parser_BadRowValues_BecomeNull()
        {
            string json = System.IO.File.ReadAllText(XunitTmp.Write(
                "{\"heatLossRoomRows\":[{\"id\":\"X\",\"name\":\"Плохая\",\"areaSqM\":\"abc\",\"indoorTemperatureC\":true}],\"airChanges\":{\"rows\":[]},\"shapes\":[]}"));

            var file = TomatParser.ParseText(json);

            var room = Assert.Single(file.Rooms);
            Assert.Null(room.AreaSqM);
            Assert.Null(room.IndoorTemperatureC);
        }

        [Fact]
        public void Parser_SystemFlows_SumsSupplyPerRoom()
        {
            string json =
                "{\"heatLossRoomRows\":[" +
                "{\"id\":\"A1\",\"floorNumber\":1,\"roomNumber\":\"1.01\",\"name\":\"Зал\",\"areaSqM\":50,\"heightM\":3}," +
                "{\"id\":\"A2\",\"floorNumber\":1,\"roomNumber\":\"1.02\",\"name\":\"Офис\",\"areaSqM\":30,\"heightM\":3}" +
                "],\"airChanges\":{" +
                "\"roomData\":{\"A1\":{\"supplyRatePerHour\":1,\"exhaustRatePerHour\":1}},\"rows\":[]," +
                "\"systemFlows\":{" +
                "\"A1\":{\"?2\":1580,\"?6\":7250}," +
                "\"A2\":{\"?5\":750,\"?2\":220}" +
                "}},\"shapes\":[]}";

            var file = TomatParser.ParseText(json);

            Assert.Empty(file.ParseMessages);
            Assert.Equal(2, file.Rooms.Count);
            // Состав систем: приток = сумма расходов всех систем помещения.
            Assert.Equal(1580 + 7250, file.Rooms[0].SupplyM3H);
            Assert.Equal(750 + 220, file.Rooms[1].SupplyM3H);
            // Вытяжка и системы из rows отсутствуют — остаются пустыми.
            Assert.Null(file.Rooms[0].ExhaustM3H);
            Assert.Empty(file.Rooms[0].SupplySystemId);
        }

        [Fact]
        public void Parser_SystemFlows_Absent_KeepsRowsSupply()
        {
            string json =
                "{\"heatLossRoomRows\":[" +
                "{\"id\":\"A1\",\"roomNumber\":\"1.01\",\"name\":\"Зал\",\"areaSqM\":50,\"heightM\":3}" +
                "],\"airChanges\":{" +
                "\"rows\":[{\"id\":\"R1\",\"roomId\":\"A1\",\"period\":\"warm\",\"supplyM3H\":150,\"exhaustM3H\":200," +
                "\"supplySystemId\":\"п1\",\"exhaustSystemId\":\"в2\"}]" +
                "},\"shapes\":[]}";

            var file = TomatParser.ParseText(json);

            var room = Assert.Single(file.Rooms);
            Assert.Equal(150.0, room.SupplyM3H);
            Assert.Equal(200.0, room.ExhaustM3H);
            Assert.Equal("п1", room.SupplySystemId);
            Assert.Equal("в2", room.ExhaustSystemId);
        }

        [Fact]
        public void Settings_TomatParamsRoundTrip()
        {
            var s = new GrdSettings
            {
                TomatSupplySystemParam = "П1",
                TomatExhaustSystemParam = "В1",
                TomatSupplyAirflowParam = "Расход",
                TomatExhaustAirflowParam = "Вытяжка",
                LastTomatPath = @"D:\data\t.tomat"
            };

            var back = MiniJson.Deserialize(MiniJson.Serialize(s));

            Assert.Equal("П1", back.TomatSupplySystemParam);
            Assert.Equal("В1", back.TomatExhaustSystemParam);
            Assert.Equal("Расход", back.TomatSupplyAirflowParam);
            Assert.Equal("Вытяжка", back.TomatExhaustAirflowParam);
            Assert.Equal(@"D:\data\t.tomat", back.LastTomatPath);
        }

        [Fact]
        public void Settings_TomatDefaults_WhenAbsent()
        {
            var back = MiniJson.Deserialize("{}");

            Assert.Equal("Приточная система", back.TomatSupplySystemParam);
            Assert.Equal("Вытяжная система", back.TomatExhaustSystemParam);
            Assert.Equal("Расход приточного воздуха", back.TomatSupplyAirflowParam);
            Assert.Equal("Расход вытяжного воздуха", back.TomatExhaustAirflowParam);
        }
    }
}