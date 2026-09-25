using System.Collections.Generic;
using System.IO;
using System.Linq;
using GrdRevit.Core;
using Xunit;

namespace GrdRevit.Core.Tests
{
    /// <summary>Снимок состояния (SavedDataFile) и сохранение последних путей в настройках.</summary>
    public class SavedDataTests
    {
        private static string Tmp(string name)
        {
            var dir = Path.Combine(Path.GetTempPath(), "grdtests");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name + "_" + System.Guid.NewGuid().ToString("N") + ".json");
        }

        [Fact]
        public void SavedDataFile_RoundTripsFullSnapshot()
        {
            var path = Tmp("snapshot");
            var snap = new SavedSnapshot
            {
                RoomsPath = @"C:\project\room-radiator.txt",
                ValveSettingsPath = @"C:\project\valve-settings.txt",
                ValveFilled = true,
                Devices = new List<SavedDevice>
                {
                    new SavedDevice
                    {
                        Room = "БКТ 102", Code = "TPLNE2518T2L", FamilyHint = "TEPLA NEO EXPO",
                        Diameter = "25", Connection = "T2L", CapacityW = 1503,
                        ValveSetting = "3", MissingReason = "", Order = 1
                    },
                    new SavedDevice { Room = "КОНСЬЕРЖ", Code = "", CapacityW = null, Order = 2 }
                },
                RoomsList = { new SavedRoom { Name = "БКТ 102", Order = 0 } },
                ValveRows = new List<SavedValveRow>
                {
                    new SavedValveRow { Room = "БКТ 102", Diameter = "20", DeviceCode = "КТС-ВП2",
                        Setting = "3", Kv = "0.93", PowerW = 1503, Order = 0 }
                }
            };

            SavedDataFile.Save(path, snap);

            var loaded = SavedDataFile.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal(snap.RoomsPath, loaded.RoomsPath);
            Assert.Equal(snap.ValveSettingsPath, loaded.ValveSettingsPath);
            Assert.True(loaded.ValveFilled);
            Assert.Equal(2, loaded.Devices.Count);

            var d0 = loaded.Devices[0];
            Assert.Equal("БКТ 102", d0.Room);
            Assert.Equal("TPLNE2518T2L", d0.Code);
            Assert.Equal("TEPLA NEO EXPO", d0.FamilyHint);
            Assert.Equal("25", d0.Diameter);
            Assert.Equal("T2L", d0.Connection);
            Assert.Equal(1503, d0.CapacityW.Value, 0);
            Assert.Equal("3", d0.ValveSetting);
            Assert.Equal(1, d0.Order);

            Assert.Null(loaded.Devices[1].CapacityW);
            Assert.Single(loaded.RoomsList);
            Assert.Equal("БКТ 102", loaded.RoomsList[0].Name);

            var v = loaded.ValveRows[0];
            Assert.Equal("КТС-ВП2", v.DeviceCode);
            Assert.Equal("3", v.Setting);
            Assert.Equal(1503, v.PowerW.Value, 0);
        }

        [Fact]
        public void SavedDataFile_DeleteRemovesSnapshot()
        {
            var path = Tmp("clear");
            SavedDataFile.Save(path, new SavedSnapshot { Devices = { new SavedDevice { Code = "X" } } });
            Assert.NotNull(SavedDataFile.Load(path));

            SavedDataFile.Delete(path);
            Assert.False(File.Exists(path));
            Assert.Null(SavedDataFile.Load(path));
        }

        [Fact]
        public void MiniJson_MarkTypeMapRoundTripsFamilyAndType()
        {
            var s = new GrdSettings
            {
                MarkTypeMap = new Dictionary<string, MarkTypeEntry>
                {
                    ["КС-1"] = new MarkTypeEntry { Family = "TEPLA Classic", Type = "TEPLA Classic-535" },
                    ["КС-2"] = new MarkTypeEntry { Type = "Только тип" },
                    ["КС-3"] = new MarkTypeEntry { Family = "Семейство без типа", Type = "" }
                }
            };

            var json = MiniJson.Serialize(s);
            Assert.Contains("\"MarkTypeMap\"", json);

            var back = MiniJson.Deserialize(json);
            Assert.Equal("TEPLA Classic", back.MarkTypeMap["КС-1"].Family);
            Assert.Equal("TEPLA Classic-535", back.MarkTypeMap["КС-1"].Type);
            Assert.Equal("", back.MarkTypeMap["КС-2"].Family);
            Assert.Equal("Только тип", back.MarkTypeMap["КС-2"].Type);
            Assert.Equal("Семейство без типа", back.MarkTypeMap["КС-3"].Family);
            Assert.Equal("", back.MarkTypeMap["КС-3"].Type);
        }

        [Fact]
        public void MiniJson_MarkTypeMapReadsLegacyStringOnlyValues()
        {
            // Старый формат: значение карты было строкой («марка»: «тип»).
            var json = "{\"MarkTypeMap\":{\"КС-1\":\"TEPLA Classic-535\",\"КС-2\":\"\"}}";

            var back = MiniJson.Deserialize(json);
            Assert.Equal("TEPLA Classic-535", back.MarkTypeMap["КС-1"].Type);
            Assert.Equal("", back.MarkTypeMap["КС-1"].Family);
            Assert.False(back.MarkTypeMap.ContainsKey("КС-2"));
        }

        [Fact]
        public void MiniJson_MarkValueMapRoundTripsPerMarkParams()
        {
            var s = new GrdSettings
            {
                MarkValueMap = new Dictionary<string, MarkValueParamEntry>
                {
                    ["КС-1"] = new MarkValueParamEntry { PowerParam = "Мощность", ValveParam = "", MarkParam = "Марка" },
                    ["КС-2"] = new MarkValueParamEntry { ValveParam = "Настройка" }
                }
            };

            var json = MiniJson.Serialize(s);
            Assert.Contains("\"MarkValueMap\"", json);

            var back = MiniJson.Deserialize(json);
            Assert.Equal("Мощность", back.MarkValueMap["КС-1"].PowerParam);
            Assert.Equal("", back.MarkValueMap["КС-1"].ValveParam);
            Assert.Equal("Марка", back.MarkValueMap["КС-1"].MarkParam);
            Assert.Equal("Настройка", back.MarkValueMap["КС-2"].ValveParam);
            Assert.Equal(2, back.MarkValueMap.Count);
        }

        [Fact]
        public void MiniJson_SettingsKeepValueMapAndLastPaths()
        {
            var s = new GrdSettings
            {
                ValueParamMap = new Dictionary<string, string>
                {
                    ["Фhl"] = "ADSK_Тепловая мощность",
                    ["Valve setting"] = "Настройка клапана"
                },
                LastRoomsPath = @"C:\project\room-radiator.txt",
                LastValveSettingsPath = @"C:\project\valve-settings.txt",
                LastDxfPath = @"C:\project\КСМ 33   Касимовская 33 расчет.dxf"
            };

            var json = MiniJson.Serialize(s);
            Assert.Contains("LastRoomsPath", json);
            Assert.Contains("ValueParamMap", json);

            var back = MiniJson.Deserialize(json);
            Assert.Equal(@"C:\project\room-radiator.txt", back.LastRoomsPath);
            Assert.Equal(@"C:\project\valve-settings.txt", back.LastValveSettingsPath);
            Assert.Equal(@"C:\project\КСМ 33   Касимовская 33 расчет.dxf", back.LastDxfPath);
            Assert.Equal("ADSK_Тепловая мощность", back.ValueParamMap["Фhl"]);
            Assert.Equal("Настройка клапана", back.ValueParamMap["Valve setting"]);
        }
    }
}