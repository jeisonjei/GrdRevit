using System.Linq;
using GrdRevit.Core;
using Xunit;

namespace GrdRevit.Core.Tests
{
    /// <summary>Парсер каталога марок (выноски DXF) и плана приборов.</summary>
    public class DxfParsersTests
    {
        [Theory]
        [InlineData("Classic Plus L 1,375 (К; НР 3/4; T2; НП; прав.)", "TPLCPL1375T2L", 1375, "TEPLA Classic Plus", "T2L", "двухтрубное", true)]
        [InlineData("Classic Super L 1,692 (К; НР 3/4; T2; НП; прав.)", "TPLSC1692T2L", 1692, "TEPLA Classic Super", "T2L", "двухтрубное", true)]
        [InlineData("Classic Plus 0,714 (К; НР 3/4; ТН.)", "TPLCPL714", 714, "TEPLA Classic Plus", null, null, false)]
        [InlineData("Classic Plus 1,251 (К; НР 3/4; ТН.)", "TPLCPL1251", 1251, "TEPLA Classic Plus", null, null, false)]
        [InlineData("Neo Expo 156(?)x320(?) 3,345 (В;НР 3/4; T2; СВ.;Л)", "TPLNE3345T2L", 3345, "TEPLA NEO EXPO", "T2L", "двухтрубное", false)]
        public void CalloutParser_ParsesMarkToDevice(string mark, string code, int? watts, string family, string conn, string twin, bool length)
        {
            var ok = CalloutDeviceParser.TryParse(mark, out var dev);

            Assert.True(ok);
            Assert.Equal(code, dev.Code);
            Assert.Equal(watts, dev.CapacityW);
            Assert.Equal(family, dev.FamilyHint);
            Assert.Equal(conn, dev.Connection);
            Assert.Equal(twin, dev.IsTwinPipe);
            if (length) Assert.Equal(watts, dev.LengthMm);
            Assert.Equal(mark, dev.Mark);
        }

        [Theory]
        [InlineData("Classic Plus L T2 (Х)")]
        [InlineData("Classic Super L T2 (Х)")]
        [InlineData("Neo Expo 156x320 (???. )")]
        [InlineData("n=1")]
        [InlineData("КВ")]
        [InlineData("3, 4")]
        [InlineData("")]
        public void CalloutParser_RejectsPlaceholders(string mark)
        {
            Assert.False(CalloutDeviceParser.TryParse(mark, out _));
        }

        [Theory]
        [InlineData("DN80-4р-2000", "REG-DN80-4-2000", 2000)]
        [InlineData("DN65-4р-500", "REG-DN65-4-500", 500)]
        public void CalloutParser_ParsesDnRegister(string mark, string code, int lengthMm)
        {
            var ok = CalloutDeviceParser.TryParse(mark, out var dev);

            Assert.True(ok);
            Assert.Equal(code, dev.Code);
            Assert.Equal("Регистр", dev.FamilyHint);
            Assert.Equal(DeviceKind.RadiatorHeating, dev.Kind);
            Assert.Null(dev.CapacityW);
            Assert.Equal(lengthMm, dev.LengthMm);
            Assert.Equal(mark, dev.Mark);
        }

        [Fact]
        public void Parser_ParsesSizeFromBlockName()
        {
            Assert.Equal(1500, DxfCalloutParser.ParseSize("Bottom_R_1500"));
            Assert.Equal(1600, DxfCalloutParser.ParseSize("Bottom_L_1600"));
            Assert.Equal(1000, DxfCalloutParser.ParseSize("Side_DPR_1000"));
            Assert.Equal(1250, DxfCalloutParser.ParseSize("Side_DPR_1250"));
            Assert.Null(DxfCalloutParser.ParseSize("riser"));
            Assert.Null(DxfCalloutParser.ParseSize("room"));
        }

        [Fact]
        public void Parser_ReadsSyntheticPlanAndCatalog()
        {
            var tmp = XunitTmp.Write(SyntheticPlanDxf);
            var res = DxfCalloutParser.Parse(tmp);

            Assert.Empty(res.ParseMessages);
            Assert.Equal(2, res.HeaterCount);
            Assert.Equal(1, res.FloorCount);
            Assert.Equal(2, res.Marks.Count);
            Assert.Equal("TPLCPL1375T2L", res.MarkDevices[0].Code);
            Assert.Equal("1", res.MarkDevices[0].ValveSetting);

            var suggested = res.SuggestMark(1500);
            Assert.NotNull(suggested);
            Assert.Equal("TPLCPL1375T2L", suggested.Code);
            Assert.Equal("1", suggested.ValveSetting);
        }

        [Fact]
        public void Parser_ExtractsValveSettingsFromCalloutBlocks()
        {
            string dxf =
                "0\nSECTION\n2\nBLOCKS\n" +
                "0\nBLOCK\n8\n0\n2\nD1\n10\n0\n20\n0\n30\n0\n3\nD1\n" +
                "0\nMTEXT\n8\n0\n10\n0\n20\n0\n30\n0\n1\nClassic Plus L 1,375 (К; НР 3/4; T2; НП; прав.)\n" +
                "0\nTEXT\n8\n0\n10\n0\n20\n0\n30\n0\n1\nn=1\n" +
                "0\nENDBLK\n" +
                "0\nBLOCK\n8\n0\n2\nD2\n10\n0\n20\n0\n30\n0\n3\nD2\n" +
                "0\nMTEXT\n8\n0\n10\n0\n20\n0\n30\n0\n1\nClassic Super L 1,692 (К; НР 3/4; T2; НП; прав.)\n" +
                "0\nTEXT\n8\n0\n10\n0\n20\n0\n30\n0\n1\nн=2\n" +
                "0\nENDBLK\n" +
                "0\nBLOCK\n8\n0\n2\nD3\n10\n0\n20\n0\n30\n0\n3\nD3\n" +
                "0\nMTEXT\n8\n0\n10\n0\n20\n0\n30\n0\n1\nNeo Expo 156(?)x320(?) 3,345 (В;НР 3/4; T2; СВ.;Л)\n" +
                "0\nENDBLK\n" +
                "0\nENDSEC\n" +
                "0\nSECTION\n2\nENTITIES\n0\nENDSEC\n0\nEOF\n";

            var res = DxfCalloutParser.Parse(XunitTmp.Write(dxf));

            Assert.Equal(3, res.MarkDevices.Count);
            Assert.Equal("1", res.MarkDevices.Single(d => d.Code == "TPLCPL1375T2L").ValveSetting);
            Assert.Equal("2", res.MarkDevices.Single(d => d.Code == "TPLSC1692T2L").ValveSetting);
            Assert.Null(res.MarkDevices.Single(d => d.Code == "TPLNE3345T2L").ValveSetting);
        }

        private const string SyntheticPlanDxf =
            "0\nSECTION\n2\nHEADER\n9\n$ACADVER\n1\nAC1032\n0\nENDSEC\n" +
            "0\nSECTION\n2\nBLOCKS\n" +
            "0\nBLOCK\n8\n0\n2\nBottom_R_1500\n10\n0\n20\n0\n30\n0\n3\nBottom_R_1500\n0\nENDBLK\n" +
            "0\nBLOCK\n8\n0\n2\nDCAD_LEADER_1ABC\n10\n0\n20\n0\n30\n0\n3\nDCAD_LEADER_1ABC\n" +
            "0\nMTEXT\n8\n0\n10\n100\n20\n200\n30\n0\n1\nClassic Plus L 1,375 (К; НР 3/4; T2; НП; прав.)\n" +
            "0\nTEXT\n8\n0\n10\n100\n20\n200\n30\n0\n1\nn=1\n" +
            "0\nENDBLK\n" +
            "0\nENDSEC\n" +
            "0\nSECTION\n2\nENTITIES\n" +
            "0\nINSERT\n8\nDCAD-HeaterNodesOnPlan\n2\nBottom_R_1500\n10\n1000\n20\n2000\n30\n0\n" +
            "0\nINSERT\n8\nDCAD-HeaterNodesOnPlan\n2\nBottom_R_1500\n10\n3000\n20\n2000\n30\n0\n" +
            "0\nLINE\n8\nWalls\n10\n100\n20\n200\n11\n250\n21\n400\n" +
            "0\nLWPOLYLINE\n8\nWalls\n90\n4\n70\n1\n10\n500\n20\n100\n10\n700\n20\n100\n10\n700\n20\n300\n10\n500\n20\n300\n" +
            "0\nPOLYLINE\n8\nWalls\n" +
            "0\nVERTEX\n8\nWalls\n10\n900\n20\n100\n" +
            "0\nVERTEX\n8\nWalls\n10\n1100\n20\n100\n" +
            "0\nVERTEX\n8\nWalls\n10\n1000\n20\n250\n" +
            "0\nSEQEND\n8\nWalls\n" +
            "0\nENDSEC\n0\nEOF\n";

        [Fact]
        public void Parser_ReadsGeometryLinesPolylines()
        {
            var tmp = XunitTmp.Write(SyntheticPlanDxf);
            var res = DxfCalloutParser.Parse(tmp);

            Assert.Empty(res.ParseMessages);
            Assert.Equal(2, res.HeaterCount);
            Assert.Equal(1, res.FloorCount);

            // LINE: 1 сегмент.
            // LWPOLYLINE (замкнутый прямоугольник): 4 сегмента.
            // POLYLINE (3 вершины): 2 сегмента.
            Assert.Equal(7, res.LineCount);
            Assert.All(res.Lines, l => Assert.Equal("Walls", l.Layer));
            Assert.Equal(100, res.Lines[0].X1);
            Assert.Equal(200, res.Lines[0].Y1);
            Assert.Equal(250, res.Lines[0].X2);
            Assert.Equal(400, res.Lines[0].Y2);

            // Замыкающий сегмент прямоугольника (последняя вершина -> первая).
            var closed = res.Lines.First(l => l.X1 == 500 && l.Y1 == 300 && l.X2 == 500 && l.Y2 == 100);
            Assert.NotNull(closed);

            // Сегменты POLYLINE.
            var p1 = res.Lines.FirstOrDefault(l => l.X1 == 900 && l.Y1 == 100 && l.X2 == 1100 && l.Y2 == 100);
            var p2 = res.Lines.FirstOrDefault(l => l.X1 == 1100 && l.Y1 == 100 && l.X2 == 1000 && l.Y2 == 250);
            Assert.NotNull(p1);
            Assert.NotNull(p2);
        }

        // Слой приборов, INSERT и выноски MULTILEADER рядом (304 — текст, 10/20 — остриё стрелки).
        private const string PlanWithLeadersDxf =
            "0\nSECTION\n2\nHEADER\n9\n$ACADVER\n1\nAC1032\n0\nENDSEC\n" +
            "0\nSECTION\n2\nBLOCKS\n" +
            "0\nBLOCK\n8\n0\n2\nBottom_R_1500\n10\n0\n20\n0\n30\n0\n3\nBottom_R_1500\n0\nENDBLK\n" +
            "0\nBLOCK\n8\n0\n2\nBottom_R_1100\n10\n0\n20\n0\n30\n0\n3\nBottom_R_1100\n0\nENDBLK\n" +
            "0\nBLOCK\n8\n0\n2\nDCAD_LEADER_1ABC\n10\n0\n20\n0\n30\n0\n3\nDCAD_LEADER_1ABC\n" +
            "0\nMTEXT\n8\n0\n10\n100\n20\n200\n30\n0\n1\nClassic Plus L 1,375 (К; НР 3/4; T2; НП; прав.)\n" +
            "0\nTEXT\n8\n0\n10\n100\n20\n200\n30\n0\n1\nn=1\n" +
            "0\nENDBLK\n" +
            "0\nENDSEC\n" +
            "0\nSECTION\n2\nENTITIES\n" +
            "0\nINSERT\n8\nDCAD-HeaterNodesOnPlan\n2\nBottom_R_1500\n10\n1000\n20\n1000\n30\n0\n" +
            "0\nINSERT\n8\nDCAD-HeaterNodesOnPlan\n2\nBottom_R_1100\n10\n500000\n20\n100000\n30\n0\n" +
            "0\nMULTILEADER\n8\nDCAD-Leaders\n10\n1030\n20\n1040\n12\n1030\n22\n1040\n304\nClassic Plus L 1,875 (К; НР 3/4; T2; НП; прав.)\\Pn=3\n" +
            "0\nMULTILEADER\n8\nDCAD-Leaders\n10\n500030\n20\n100040\n12\n500030\n22\n100040\n304\n%%C16x2,2\n" +
            "0\nENDSEC\n0\nEOF\n";

        [Fact]
        public void Parser_ReadsBigTextLabels()
        {
            string dxf =
                "0\nSECTION\n2\nENTITIES\n" +
                "0\nTEXT\n8\nОВ ком-рии\n62\n2\n10\n132210\n20\n88164\n30\n0\n40\n5000\n1\n1 этаж\n" +
                "0\nTEXT\n8\nОВ ком-рии\n10\n132210\n20\n88164\n30\n0\n40\n250\n1\nмаленькая подпись\n" +
                "0\nMTEXT\n8\n0\n10\n1\n20\n2\n30\n0\n40\n5000\n1\n25 этаж\n" +
                "0\nENDSEC\n0\nEOF\n";

            var res = DxfCalloutParser.Parse(XunitTmp.Write(dxf));

            Assert.Empty(res.ParseMessages);
            Assert.Equal(2, res.TextCount);
            Assert.All(res.Texts, t => Assert.True(t.Height >= DxfCalloutParser.MinBigTextHeight));
            Assert.Contains(res.Texts, t => t.Text == "1 этаж");
            Assert.Contains(res.Texts, t => t.Text == "25 этаж");
        }

        [Fact]
        public void Parser_ReadsTruthMarksFromLeaders()
        {
            var res = DxfCalloutParser.Parse(XunitTmp.Write(PlanWithLeadersDxf));

            Assert.Empty(res.ParseMessages);
            Assert.Equal(2, res.HeaterCount);
            Assert.Equal(2, res.LeaderCount);
            Assert.Equal(2, res.FloorCount);

            // Прибор с выноской-маркой рядом: берём текст из MULTILEADER (304), а не каталог.
            var withLeader = res.Heaters.Single(h => h.Block == "Bottom_R_1500");
            Assert.Equal("стрелка в центре", withLeader.MarkSource);
            Assert.Equal("Classic Plus L 1,875 (К; НР 3/4; T2; НП; прав.)", withLeader.TruthMark);
            Assert.Equal("3", withLeader.TruthValveSetting);
            Assert.NotNull(withLeader.TruthDistance);
            Assert.Equal("TPLCPL1875T2L", withLeader.Device?.Code);
            Assert.Equal("3", withLeader.Device?.ValveSetting);

            // Прибор без выноски-марки (рядом только метка трубы %%C…): марка не назначается,
            // подбор «по каталогу» не выполняется — остаётся «выноска не найдена».
            var pipeOnly = res.Heaters.Single(h => h.Block == "Bottom_R_1100");
            Assert.Null(pipeOnly.MarkSource);
            Assert.Null(pipeOnly.TruthMark);
            Assert.Null(pipeOnly.Device);
        }

        // Выноска с пустой строкой 304: марка восстанавливается из блока, на который
        // MULTILEADER ссылается handle-ом 341 (BLOCK_RECORD в TABLES -> блок в BLOCKS).
        private const string PlanWithBlockLeaderDxf =
            "0\nSECTION\n2\nHEADER\n9\n$ACADVER\n1\nAC1032\n0\nENDSEC\n" +
            "0\nSECTION\n2\nTABLES\n" +
            "0\nBLOCK_RECORD\n5\n4A00\n2\nDCAD_LEADER_4A00\n0\nBLOCK_RECORD\n5\n4A01\n2\nDCAD_LEADER_4A01\n" +
            "0\nENDSEC\n" +
            "0\nSECTION\n2\nBLOCKS\n" +
            "0\nBLOCK\n8\n0\n2\nDCAD_LEADER_4A00\n10\n0\n20\n0\n30\n0\n3\nDCAD_LEADER_4A00\n" +
            "0\nMTEXT\n8\n0\n10\n0\n20\n0\n30\n0\n1\nClassic Plus L 1,375 (К; НР 3/4; T2; НП; прав.)\n" +
            "0\nTEXT\n8\n0\n10\n0\n20\n0\n30\n0\n1\nn=2\n" +
            "0\nENDBLK\n" +
            "0\nBLOCK\n8\n0\n2\nDCAD_LEADER_4A01\n10\n0\n20\n0\n30\n0\n3\nDCAD_LEADER_4A01\n" +
            "0\nMTEXT\n8\n0\n10\n0\n20\n0\n30\n0\n1\nClassic Super L 1,692 (К; НР 3/4; T2; НП; прав.)\n" +
            "0\nENDBLK\n" +
            "0\nENDSEC\n" +
            "0\nSECTION\n2\nENTITIES\n" +
            "0\nINSERT\n8\nDCAD-HeaterNodesOnPlan\n2\nBottom_R_1500\n10\n2000\n20\n2000\n30\n0\n" +
            "0\nMULTILEADER\n8\nDCAD-Leaders\n" +
            "300\nCONTEXT_DATA{\n341\n4A00\n" +
            "302\nLEADER{\n304\nLEADER_LINE{\n10\n2000\n20\n2000\n30\n0\n91\n0\n305\n}\n303\n}\n301\n}\n" +
            "0\nENDSEC\n0\nEOF\n";

        [Fact]
        public void Parser_ReadsLeaderTextFromBlockViaHandle()
        {
            var res = DxfCalloutParser.Parse(XunitTmp.Write(PlanWithBlockLeaderDxf));

            Assert.Empty(res.ParseMessages);
            Assert.Equal(1, res.LeaderCount);

            var leader = res.Leaders[0];
            Assert.Equal(2000, leader.X);
            Assert.Equal(2000, leader.Y);
            Assert.Equal("Classic Plus L 1,375 (К; НР 3/4; T2; НП; прав.)", leader.Text.Split('\\')[0]);

            var heater = res.Heaters.Single();
            Assert.Equal("стрелка в центре", heater.MarkSource);
            Assert.Equal("Classic Plus L 1,375 (К; НР 3/4; T2; НП; прав.)", heater.TruthMark);
            Assert.Equal("2", heater.TruthValveSetting);
        }

        // Марка Neo Expo присутствует только в выноске на плане (304), вне определений
        // блоков: каталог пополняется марками из выносок, чтобы строка появилась в таблице.
        [Fact]
        public void Parser_AddsNeoExpoMarkToCatalogFromLeader()
        {
            string dxf =
                "0\nSECTION\n2\nBLOCKS\n" +
                "0\nBLOCK\n8\n0\n2\nBottom_R_1500\n10\n0\n20\n0\n30\n0\n3\nBottom_R_1500\n0\nENDBLK\n" +
                "0\nENDSEC\n" +
                "0\nSECTION\n2\nENTITIES\n" +
                "0\nINSERT\n8\nDCAD-HeaterNodesOnPlan\n2\nBottom_R_1500\n10\n1000\n20\n1000\n30\n0\n" +
                "0\nMULTILEADER\n8\nDCAD-Leaders\n10\n1000\n20\n1000\n12\n1000\n22\n1000\n304\nNeo Expo 156(В)x320(Г) 3,345 (К;НР3/4;T2; лев.;БП)\\Pn=6\n" +
                "0\nENDSEC\n0\nEOF\n";

            var res = DxfCalloutParser.Parse(XunitTmp.Write(dxf));

            Assert.Empty(res.ParseMessages);
            Assert.Equal("TPLNE3345T2L", res.MarkDevices.Single(d => d.Code == "TPLNE3345T2L").Code);
            Assert.Contains(res.Marks, m => m.StartsWith("Neo Expo 156(В)x320(Г) 3,345"));
            Assert.NotNull(res.MarkDevices.Single(d => d.Code == "TPLNE3345T2L").ValveSetting);
        }

        // Регистр из труб ("DN80-4р-2000") — выноска на приборе: марка назначена и есть в каталоге.
        [Fact]
        public void Parser_AddsDnRegisterMarkToCatalogFromLeader()
        {
            string dxf =
                "0\nSECTION\n2\nBLOCKS\n" +
                "0\nBLOCK\n8\n0\n2\nSide_DPR_2000\n10\n0\n20\n0\n30\n0\n3\nSide_DPR_2000\n0\nENDBLK\n" +
                "0\nENDSEC\n" +
                "0\nSECTION\n2\nENTITIES\n" +
                "0\nINSERT\n8\nDCAD-HeaterNodesOnPlan\n2\nSide_DPR_2000\n10\n1000\n20\n1000\n30\n0\n" +
                "0\nMULTILEADER\n8\nDCAD-Leaders\n10\n1000\n20\n1000\n12\n1000\n22\n1000\n304\nDN80-4р-2000\\Pn=4\n" +
                "0\nENDSEC\n0\nEOF\n";

            var res = DxfCalloutParser.Parse(XunitTmp.Write(dxf));

            Assert.Empty(res.ParseMessages);
            var heater = res.Heaters.Single(h => h.Block == "Side_DPR_2000");
            Assert.Equal("DN80-4р-2000", heater.TruthMark);
            Assert.Equal("REG-DN80-4-2000", heater.Device?.Code);
            Assert.Equal("4", heater.TruthValveSetting);
            Assert.Contains(res.MarkDevices, d => d.Code == "REG-DN80-4-2000");
            var dev = res.MarkDevices.Single(d => d.Mark == "DN80-4р-2000");
            Assert.Equal("Регистр", dev.FamilyHint);
            Assert.Equal(2000, dev.LengthMm);
            Assert.Null(dev.CapacityW);
        }

        // Прибор без выноски тоже должен попасть в каталог (карту типов) — по имени блока.
        [Fact]
        public void Parser_AddsUnmarkedHeaterBlockToCatalog()
        {
            string dxf =
                "0\nSECTION\n2\nBLOCKS\n" +
                "0\nBLOCK\n8\n0\n2\nSide_DPR_2000\n10\n0\n20\n0\n30\n0\n3\nSide_DPR_2000\n0\nENDBLK\n" +
                "0\nENDSEC\n" +
                "0\nSECTION\n2\nENTITIES\n" +
                "0\nINSERT\n8\nDCAD-HeaterNodesOnPlan\n2\nSide_DPR_2000\n10\n1000\n20\n1000\n30\n0\n" +
                "0\nENDSEC\n0\nEOF\n";

            var res = DxfCalloutParser.Parse(XunitTmp.Write(dxf));

            Assert.Empty(res.ParseMessages);
            var heater = res.Heaters.Single(h => h.Block == "Side_DPR_2000");
            Assert.Null(heater.TruthMark);
            Assert.Equal("Side_DPR_2000", heater.SuggestedMark);
            Assert.Contains(res.MarkDevices, d => d.Mark == "Side_DPR_2000");
        }

        // Две выноски-стрелки в пределах допуска: марка не назначается (коллизия).
        [Fact]
        public void Parser_DoesNotAssignMarkOnAmbiguousLeaders()
        {
            string dxf = PlanWithLeadersDxf.Replace(
                "0\nMULTILEADER\n8\nDCAD-Leaders\n10\n1030\n20\n1040\n12\n1030\n22\n1040\n304\nClassic Plus L 1,875 (К; НР 3/4; T2; НП; прав.)\\Pn=3\n" +
                "0\nMULTILEADER\n8\nDCAD-Leaders\n10\n500030\n20\n100040\n12\n500030\n22\n100040\n304\n%%C16x2,2\n",
                "0\nMULTILEADER\n8\nDCAD-Leaders\n10\n1010\n20\n1010\n12\n1010\n22\n1010\n304\nClassic Plus L 1,875 (К; НР 3/4; T2; НП; прав.)\\Pn=3\n" +
                "0\nMULTILEADER\n8\nDCAD-Leaders\n10\n1030\n20\n1030\n12\n1030\n22\n1030\n304\nClassic Super L 1,692 (К; НР 3/4; T2; НП; прав.)\\Pn=1\n");

            var res = DxfCalloutParser.Parse(XunitTmp.Write(dxf));

            var heater = res.Heaters.Single(h => h.Block == "Bottom_R_1500");
            Assert.Null(heater.TruthMark);
            Assert.Null(heater.MarkSource);
            Assert.Null(heater.Device);
        }
    }
}