using System.Linq;
using GrdRevit.Core;
using Xunit;

namespace GrdRevit.Core.Tests
{
    /// <summary>Парсеры новых файлов: помещения+радиаторы и настройки клапанов.</summary>
    public class RoomsValvesParsersTests
    {
        private const string RoomsSample =
            "БКТ 102\t155\tD\tTPLNE2518T2L\t1.175 м\t25\t1503\t1.5\t1385\t1.4\n" +
            "БКТ 102\t155\tC\tTPLNE2518T2L\t1.175 м\t25\t1503\t1.5\t1339\t1.3\n" +
            "БКТ 105\t159\tE\tTPLNE2246T2L\t1.080 м\t20\t1526\t1.5\t1367\t1.4\n" +
            "С1.201\t145\t\tTPLCPL1875T2L\t1.129 м\t100\t1050\t1.1\t970\t1.0\n" +
            "КОНСЬЕРЖ 106\t156\tB\tTPLNE1393T2L\t0.785 м\t50\t725\t0.7\t651\t0.7\n";

        [Fact]
        public void RoomsRadiatorsParser_ReadsRoomCodeCapacityDiameter()
        {
            var tmp = XunitTmp.Write(RoomsSample);
            var devices = RoomsRadiatorsParser.Parse(tmp);

            Assert.Equal(5, devices.Count);

            var first = devices[0];
            Assert.Equal("БКТ 102", first.Room);
            Assert.Contains("TPLNE2518T2L", first.Code);
            Assert.Equal(1503, first.CapacityW.Value, 0);
            Assert.Equal("25", first.Diameter);
            Assert.Equal("TEPLA NEO EXPO", first.FamilyHint);

            var s201 = devices.First(d => d.Room == "С1.201");
            Assert.Equal(1050, s201.CapacityW.Value, 0);
            Assert.Equal("TEPLA Classic Plus", s201.FamilyHint);

            var kons = devices.First(d => d.Room == "КОНСЬЕРЖ 106");
            Assert.Equal(725, kons.CapacityW.Value, 0);
        }

        [Fact]
        public void ValveSettingsParser_ReadsSettingFromFourthColumn()
        {
            var tmp = XunitTmp.Write(
                "БКТ 102\t20\tКТС-ВП2\t3\t\t0.93\t1503\t1.5\n" +
                "БКТ 105\t20\tКТС-ВП2\t1\t\t0.34\t1526\t1.5\n" +
                "ГЛ.1\t25\tD-2500025\t13\t18.0\t\t16170\t16.2\n" +
                "ВЕНТКАМЕРА\t15\t013G7014R\t1\t\t0.36\t860\t0.9\n");

            var rows = ValveSettingsParser.Parse(tmp);

            Assert.Equal(4, rows.Count);
            Assert.Equal("3", rows[0].Setting);
            Assert.Equal("БКТ 102", rows[0].Room);
            Assert.Equal("КТС-ВП2", rows[0].DeviceCode);
            Assert.Equal(1503, rows[0].PowerW.Value, 0);
            Assert.Equal("0.93", rows[0].Kv);

            Assert.Equal("1", rows[1].Setting);
            Assert.Equal("13", rows[2].Setting);
            Assert.Equal("1", rows[3].Setting);
        }

        [Fact]
        public void Matcher_FillsByRoomPlusPower_InOrder()
        {
            var devices = RoomsRadiatorsParser.Parse(XunitTmp.Write(RoomsSample));
            var valves = ValveSettingsParser.Parse(XunitTmp.Write(
                "БКТ 102\t20\tКТС-ВП2\t3\t\t0.93\t1503\t1.5\n" +
                "БКТ 102\t20\tКТС-ВП2\t3\t\t0.92\t1503\t1.5\n" +
                "БКТ 105\t20\tКТС-ВП2\t1\t\t0.34\t1526\t1.5\n" +
                "С1.201\t20\tКТС-ВП2\t2\t\t0.52\t1050\t1.1\n" +
                "КОНСЬЕРЖ 106\t20\tКТС-ВП2\t1\t\t0.64\t725\t0.7\n" +
                "КОНСЬЕРЖ 106\t20\tКТС-ВП2\t1\t\t0.64\t725\t0.7\n" +
                "ГЛ.1\t25\tD-2500025\t13\t18.0\t\t16170\t16.2\n"));

            var filled = ValveSettingsMatcher.Fill(devices, valves);

            Assert.Equal(5, filled);

            var bkt102 = devices.Where(d => d.Room == "БКТ 102").OrderBy(d => d.Order).ToList();
            Assert.Equal(2, bkt102.Count);
            Assert.Equal("3", bkt102[0].ValveSetting);
            Assert.Equal("3", bkt102[1].ValveSetting);

            var bkt105 = devices.Single(d => d.Room == "БКТ 105");
            Assert.Equal("1", bkt105.ValveSetting);

            var s201 = devices.Single(d => d.Room == "С1.201");
            Assert.Equal("2", s201.ValveSetting);

            var kons = devices.Where(d => d.Room == "КОНСЬЕРЖ 106").OrderBy(d => d.Order).ToList();
            Assert.Single(kons);
            Assert.Equal("1", kons[0].ValveSetting);
        }

        [Fact]
        public void Matcher_LeavesRowEmpty_WhenNoValveMatches()
        {
            var devices = RoomsRadiatorsParser.Parse(XunitTmp.Write(RoomsSample));
            var valves = ValveSettingsParser.Parse(XunitTmp.Write(
                "БКТ 102\t20\tКТС-ВП2\t3\t\t0.93\t1503\t1.5\n")); // нет совпадений по 1526/1050/725

            var filled = ValveSettingsMatcher.Fill(devices, valves);

            Assert.Equal(1, filled); // только одна настройка под 1503 — одной строке БКТ 102
            Assert.Contains(devices.Where(d => d.Room == "БКТ 102"), d => d.ValveSetting == "3");
            Assert.Single(devices.Where(d => d.Room == "БКТ 102").Where(d => d.ValveSetting == null));
            Assert.Null(devices.Single(d => d.Room == "БКТ 105").ValveSetting);
            Assert.Null(devices.Single(d => d.Room == "С1.201").ValveSetting);
        }

        [Fact]
        public void Matcher_EmptyInput_ReturnsZero()
        {
            Assert.Equal(0, ValveSettingsMatcher.Fill(null, null));
            Assert.Equal(0, ValveSettingsMatcher.Fill(new System.Collections.Generic.List<GrdDevice>(), null));
        }
    }

    /// <summary>Пишет текст во временный файл и возвращает путь.</summary>
    internal static class XunitTmp
    {
        public static string Write(string content)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "grdtests");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "sample_" + System.Guid.NewGuid().ToString("N") + ".txt");
            System.IO.File.WriteAllText(path, content);
            return path;
        }
    }
}