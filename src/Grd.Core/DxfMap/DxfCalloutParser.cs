using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GrdRevit.Core
{
    /// <summary>Один распознанный объект плана: отопительный прибор.</summary>
    public sealed class DxfCalloutItem
    {
        public string Kind;        // "heater" | "callout"
        public string Text;        // текст выноски (марка) — только для callout/каталога
        public string Block;       // имя блока (INSERT)
        public double X;
        public double Y;
        public int Floor;          // индекс этажа
        public int Seq;            // порядок внутри этажа
        public double? Size;       // размер из имени блока (напр. Bottom_R_1500 -> 1500)
        public string SuggestedMark;  // каталоговая марка, подобранная по размеру
        public GrdDevice Device;      // распознанная марка (для каталога и рекомендованной)

        /// <summary>Истинная марка из выноски (MULTILEADER), найденной рядом с прибором (может быть null).</summary>
        public string TruthMark;
        /// <summary>Настройка клапана «n=…» из той же выноски (может быть null).</summary>
        public string TruthValveSetting;
        /// <summary>Расстояние до найденной выноски-марки (для диагностики).</summary>
        public double? TruthDistance;
        /// <summary>Источник марки: "выноска" | "каталог" | null.</summary>
        public string MarkSource;

        public override string ToString() => $"{Kind} {Block} ({X:N0}, {Y:N0}) '{Text}'";
    }

    /// <summary>Выноска (MULTILEADER) из ENTITIES: текст в коде 304 и его привязка.</summary>
    public sealed class DxfLeaderText
    {
        public string Layer;
        public double X;   // остриё стрелки (первая пара 10/20 внутри LEADER_LINE{)
        public double Y;
        public string Text;

        /// <summary>handle (код 341) блока-выноски, чей текст используется при пустой строке 304.</summary>
        public string BlockHandle;

        public override string ToString() => $"{Layer} ({X:N0}, {Y:N0}) '{Text}'";
    }

    /// <summary>Отрезок геометрии плана (LINE / сегмент LWPOLYLINE или POLYLINE).</summary>
    public sealed class DxfGeomLine
    {
        public string Layer;
        public double X1, Y1, X2, Y2;

        public override string ToString() => $"{Layer} ({X1:N0}, {Y1:N0}) -> ({X2:N0}, {Y2:N0})";
    }

    /// <summary>Этаж/кластер плана: набор приборов в одной зоне координат.</summary>
    public sealed class DxfFloor
    {
        public int Index;
        public string Label;
        public double CenterX;
        public double CenterY;
        public List<DxfCalloutItem> Heaters = new List<DxfCalloutItem>();

        public int DeviceCount => Heaters.Count;

        /// <summary>Номер этажа из метки, если удалось распознать (пусто — не распознан).</summary>
        public int? LevelNumber { get; set; }
    }

    /// <summary>
    /// Результат разбора DXF: приборы по этажам, каталог марок из выносок-шаблонов
    /// и рекомендация «марка = ближайшая по размеру».
    /// </summary>
    public sealed class DxfPlanResult
    {
        public string SourcePath;
        public List<DxfCalloutItem> Heaters = new List<DxfCalloutItem>();
        public List<DxfFloor> Floors = new List<DxfFloor>();
        public List<string> ParseMessages = new List<string>();

        /// <summary>Геометрия плана: отрезки линий/контуров (LINE, LWPOLYLINE, POLYLINE+VERTEX).</summary>
        public List<DxfGeomLine> Lines = new List<DxfGeomLine>();

        /// <summary>Выноски (MULTILEADER) из ENTITIES: текст марки/настройки, привязанный к прибору.</summary>
        public List<DxfLeaderText> Leaders = new List<DxfLeaderText>();

        public int LineCount => Lines.Count;
        public int LeaderCount => Leaders.Count;

        /// <summary>Уникальные тексты выносок (марки) из определений блоков DCAD_LEADER.</summary>
        public List<string> Marks = new List<string>();

        /// <summary>Каталог марок, распознанных как отопительные приборы.</summary>
        public List<GrdDevice> MarkDevices = new List<GrdDevice>();

        public int HeaterCount => Heaters.Count;
        public int FloorCount => Floors.Count;

        /// <summary>По размеру имени блока — ближайшая марка из каталога.</summary>
        public GrdDevice SuggestMark(double? size)
        {
            if (!size.HasValue || MarkDevices.Count == 0) return null;
            GrdDevice best = null;
            double bestD = double.MaxValue;
            foreach (var m in MarkDevices)
            {
                double d = m.CapacityW.HasValue ? Math.Abs(m.CapacityW.Value - size.Value) : double.MaxValue;
                if (d < bestD) { bestD = d; best = m; }
            }
            return best;
        }
    }

    /// <summary>
    /// Разбор DXF-расчёта отопительной системы.
    ///   - приборы: INSERT-блоки на слое DCAD-HeaterNodesOnPlan (имена Bottom_R_1500, Side_DPR_1000 …);
    ///   - марки: определения блоков DCAD_LEADER_* хранят текст выноски («Classic Plus L 1,375 (…)»).
    ///     На плане выноски не вставлены — это шаблоны. Собираем каталог уникальных марок;
    ///   - этажи: кластеры координат;
    ///   - рекомендация: размер из имени блока ↔ ближайшая мощность марки.
    /// Особенности формата: значения строк UTF-8 (заголовок $DWGCODEPAGE может врать), коды
    /// группируются правым смещением с ведущими пробелами, разделители тысяч — NBSP (U+00A0).
    /// </summary>
    public static class DxfCalloutParser
    {
        public const string HeaterLayer = "DCAD-HeaterNodesOnPlan";
        public const int DefaultFloorMergeGap = 50000; // мм: порог слияния точек в один этаж
        public const int MaxGeometrySegments = 500000;  // защитный предел отрезков геометрии
        public const double DefaultLeaderMarkDistance = 300; // мм, допуск: остриё выноски в центре прямоугольника прибора

        private static readonly Regex BlockSize = new Regex(@"_(\d+)$", RegexOptions.Compiled);

        /// <summary>Настройка клапана в выноске: «n=1», «N = 1,5», «н=2», «n=6*» и т.п.</summary>
        private static readonly Regex ValveSettingText = new Regex(
            @"^\s*[nNнН]\s*=\s*(\d+(?:[.,]\d+)?)\*?\s*$",
            RegexOptions.Compiled);

        public static DxfPlanResult Parse(string path, int floorMergeGap = DefaultFloorMergeGap)
        {
            var res = new DxfPlanResult { SourcePath = path };
            BlockCatalog blockCatalog = null;
            try
            {
                using (var fs1 = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr1 = new StreamReader(fs1, Encoding.UTF8))
                    blockCatalog = ReadBlockCalloutTexts(sr1);

                using (var fs2 = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr2 = new StreamReader(fs2, Encoding.UTF8))
                    ReadEntities(sr2, res, blockCatalog);
            }
            catch (Exception ex)
            {
                res.ParseMessages.Add("Ошибка чтения файла: " + ex.Message);
                return res;
            }

            AugmentCatalogWithLeaders(res, blockCatalog);
            BuildMarkCatalog(res, blockCatalog.Marks, blockCatalog.ValveByMark);
            AssignFloors(res, floorMergeGap);
            AssignSequence(res);
            AssignTruthMarks(res);
            EnsureCatalogCoversHeaterMarks(res);
            SuggestMarks(res);
            return res;
        }

        /// <summary>Тексты из определений блоков: марки-выноски и настройки клапанов по маркам.</summary>
        private sealed class BlockCatalog
        {
            public List<string> Marks = new List<string>();
            public Dictionary<string, string> ValveByMark = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>handle BLOCK_RECORD (TABLES) -&gt; имя блока.</summary>
            public Dictionary<string, string> BlockNameByHandle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Имя блока (BLOCKS) -&gt; тексты "1" внутри его определения.</summary>
            public Dictionary<string, List<string>> TextsByBlock = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Уникальные тексты, похожие на выноски, из определений блоков + сопоставление
        /// «марка -&gt; настройка клапана» («n=1» и т.п., лежащие в том же блоке, что и марка).
        /// Также собирает handle выноски-блока (BLOCK_RECORD в TABLES) -&gt; имя блока
        /// и имя блока -&gt; все его тексты, чтобы для MULTILEADER с пустой строкой 304
        /// восстановить марку из блока, на который ссылается код 341.
        /// </summary>
        private static BlockCatalog ReadBlockCalloutTexts(StreamReader sr)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var catalog = new BlockCatalog();
            string line;
            bool inBlocks = false, inBlock = false, inTables = false, inBlockRecord = false;
            string blockName = null, recordHandle = null;
            var current = new List<string>();

            void Commit()
            {
                if (blockName != null)
                {
                    var copy = current.ToList();
                    catalog.TextsByBlock[blockName] = copy;
                }

                var marks = current
                    .Select(t => t.Trim())
                    .Where(t => LooksLikeCalloutText(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var valveMatch = current
                    .Select(t => ValveSettingText.Match(t.Trim()))
                    .FirstOrDefault(m => m.Success);

                foreach (string ms in marks)
                {
                    if (seen.Add(ms)) catalog.Marks.Add(ms);
                    if (valveMatch != null && !catalog.ValveByMark.ContainsKey(ms))
                        catalog.ValveByMark[ms] = valveMatch.Groups[1].Value.Trim().Replace(',', '.');
                }
                current.Clear();
            }

            while ((line = sr.ReadLine()) != null)
            {
                string code = line.Trim();
                string value = sr.ReadLine();
                if (value == null) break;

                if (code == "0")
                {
                    if (value == "SECTION")
                    {
                        var n1 = sr.ReadLine();
                        var n2 = sr.ReadLine();
                        if (n1 != null && n2 != null)
                        {
                            inBlocks = n2.Trim() == "BLOCKS";
                            inTables = n2.Trim() == "TABLES";
                        }
                        continue;
                    }
                    if (value == "ENDSEC") { inBlocks = false; inTables = false; inBlock = false; inBlockRecord = false; recordHandle = null; continue; }
                    if (value == "ENDBLK") { if (inBlock) Commit(); inBlock = false; blockName = null; continue; }
                    if (inBlocks && value == "BLOCK") { inBlock = true; continue; }
                    if (inTables && value == "BLOCK_RECORD") { inBlockRecord = true; recordHandle = null; continue; }
                    if (inBlockRecord)
                    {
                        if (value == "BLOCK_RECORD") { inBlockRecord = true; recordHandle = null; continue; }
                        // конец записи: следующая сущность (без гарантии "ENDTAB")
                        inBlockRecord = false;
                    }
                    continue;
                }

                if (inBlockRecord)
                {
                    if (code == "5" && recordHandle == null) recordHandle = value;
                    else if (code == "2" && recordHandle != null && !catalog.BlockNameByHandle.ContainsKey(recordHandle))
                        catalog.BlockNameByHandle[recordHandle] = value;
                    continue;
                }

                if (!inBlocks || !inBlock) continue;
                if (code == "1") current.Add(value);
                else if (code == "2" && blockName == null) blockName = value;
            }
            Commit();
            return catalog;
        }

        private static bool LooksLikeCalloutText(string s)
        {
            if (s.Length < 2) return false;
            if (s[0] == '?' || s[0] == '{' || s[0] == '}') return false;
            if (s.StartsWith("LEADER_LINE", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>
        /// ENTITIES-секция: приборы (INSERT на слое нагревателей) и геометрия плана
        /// (LINE, LWPOLYLINE, POLYLINE+VERTEX) для отрисовки чертежа.
        /// Для MULTILEADER берём остриё стрелки (первая пара 10/20 внутри LEADER_LINE{)
        /// и текст: строка 304, а если она пустая — марка из блока, на который ссылается
        /// handle 341 (BLOCK_RECORD из TABLES), через каталог блоков.
        /// </summary>
        private static void ReadEntities(StreamReader sr, DxfPlanResult res, BlockCatalog blockCatalog)
        {
            string line;
            bool inEntities = false;
            string entityType = null;
            string layer = null, block = null;
            double? x = null, y = null;

            // MULTILEADER: выноска-марка. Текст — код 304 (или блок по 341), остриё стрелки — 10/20.
            string mlText = null;
            string mlBlockHandle = null;
            double? mlX = null, mlY = null;
            bool mlInLeaderLine = false;
            double? mlTipX = null, mlTipY = null;

            // LINE: начальная и конечная точки.
            double? lx = null, ly = null, rx = null, ry = null;
            // LWPOLYLINE: вершины читаются внутри одной сущности.
            List<double[]> lwVert = null;
            bool lwClosed = false;
            // POLYLINE (+ идущие следом VERTEX-сущности) до SEQEND.
            List<double[]> pvVert = null;
            bool inPolyChain = false;
            string polyLayer = null;  // слой POLYLINE сохраняется на время цепочки VERTEX
            double? pvx = null, pvy = null;

            void AddVertex(List<double[]> list)
            {
                if (pvx.HasValue && pvy.HasValue) list.Add(new[] { pvx.Value, pvy.Value });
                pvx = null; pvy = null;
            }

            void Chain(List<double[]> verts, bool closed, string useLayer)
            {
                if (verts == null || verts.Count < 2) return;
                for (int i = 0; i + 1 < verts.Count; i++)
                    AddLine(res, useLayer, verts[i][0], verts[i][1], verts[i + 1][0], verts[i + 1][1]);
                if (closed && verts.Count >= 3)
                    AddLine(res, useLayer, verts[verts.Count - 1][0], verts[verts.Count - 1][1], verts[0][0], verts[0][1]);
            }

            void FlushEntity(string t)
            {
                switch (t)
                {
                    case "INSERT":
                        if (string.IsNullOrEmpty(block)) return;
                        if (layer != HeaterLayer) return;
                        res.Heaters.Add(new DxfCalloutItem
                        {
                            Kind = "heater",
                            Block = block,
                            X = x ?? 0,
                            Y = y ?? 0,
                            Size = ParseSize(block)
                        });
                        break;
                    case "MULTILEADER":
                        if (string.IsNullOrEmpty(mlText))
                        {
                            mlText = ResolveBlockText(blockCatalog, mlBlockHandle);
                        }
                        if (string.IsNullOrEmpty(mlText)) return;
                        if (mlTipX.HasValue && mlTipY.HasValue)
                        {
                            // остриё стрелки внутри LEADER_LINE{ — точка у прибора
                            res.Leaders.Add(new DxfLeaderText
                            {
                                Layer = layer,
                                X = mlTipX.Value,
                                Y = mlTipY.Value,
                                Text = mlText,
                                BlockHandle = mlBlockHandle
                            });
                        }
                        else if (mlX.HasValue && mlY.HasValue)
                        {
                            // fallback для простых фикстур без LEADER_LINE{ — первая пара 10/20
                            res.Leaders.Add(new DxfLeaderText
                            {
                                Layer = layer,
                                X = mlX.Value,
                                Y = mlY.Value,
                                Text = mlText,
                                BlockHandle = mlBlockHandle
                            });
                        }
                        break;
                    case "LINE":
                        if (lx.HasValue && ly.HasValue && rx.HasValue && ry.HasValue)
                            AddLine(res, layer, lx.Value, ly.Value, rx.Value, ry.Value);
                        break;
                    case "LWPOLYLINE":
                        Chain(lwVert, lwClosed, layer);
                        break;
                    case "VERTEX":
                        if (inPolyChain) AddVertex(pvVert);
                        break;
                    case "SEQEND":
                        if (inPolyChain)
                        {
                            Chain(pvVert, false, polyLayer);
                            inPolyChain = false;
                        }
                        break;
                }
            }

            void ResetCurrent()
            {
                entityType = null;
                layer = null; block = null;
                x = null; y = null;
                lx = null; ly = null; rx = null; ry = null;
                pvx = null; pvy = null;
                mlText = null; mlX = null; mlY = null;
                mlBlockHandle = null; mlInLeaderLine = false;
                mlTipX = null; mlTipY = null;
            }

            while ((line = sr.ReadLine()) != null)
            {
                string code = line.Trim();
                string value = sr.ReadLine();
                if (value == null) break;

                if (code == "0")
                {
                    if (value == "SECTION")
                    {
                        var n1 = sr.ReadLine();
                        var n2 = sr.ReadLine();
                        if (n1 != null && n2 != null) inEntities = n2.Trim() == "ENTITIES";
                        continue;
                    }
                    if (value == "ENDSEC") { inEntities = false; continue; }
                    FlushEntity(entityType);
                    ResetCurrent();
                    entityType = value;
                    if (value == "LWPOLYLINE") { lwVert = new List<double[]>(); lwClosed = false; }
                    else if (value == "POLYLINE") { inPolyChain = true; pvVert = new List<double[]>(); }
                    continue;
                }

                if (!inEntities) continue;

                switch (entityType)
                {
                    case "INSERT":
                        if (code == "8") layer = value;
                        else if (code == "2") block = value;
                        else if (code == "10") x = ParseCoord(value);
                        else if (code == "20") y = ParseCoord(value);
                        break;
                    case "LINE":
                        if (code == "8") layer = value;
                        else if (code == "10") lx = ParseCoord(value);
                        else if (code == "20") ly = ParseCoord(value);
                        else if (code == "11") rx = ParseCoord(value);
                        else if (code == "21") ry = ParseCoord(value);
                        break;
                    case "LWPOLYLINE":
                        if (code == "8") layer = value;
                        else if (code == "70") lwClosed = value.Trim() == "1";
                        else if (code == "10") { pvx = ParseCoord(value); }
                        else if (code == "20") { if (pvx.HasValue) { lwVert.Add(new[] { pvx.Value, ParseCoord(value) ?? 0 }); pvx = null; } }
                        break;
                    case "POLYLINE":
                        if (code == "8") { layer = value; polyLayer = value; }
                        break;
                    case "VERTEX":
                        // Идёт следом за POLYLINE: координаты собираются, сброс — при следующем "0".
                        if (code == "10") pvx = ParseCoord(value);
                        else if (code == "20") pvy = ParseCoord(value);
                        break;
                    case "MULTILEADER":
                        if (code == "8") layer = value ?? layer;
                        else if (code == "341" && string.IsNullOrEmpty(mlBlockHandle)) mlBlockHandle = value;
                        else if (code == "304")
                        {
                            if (value == "LEADER_LINE{")
                            {
                                mlInLeaderLine = true;
                                mlTipX = null; mlTipY = null;
                            }
                            else if (value == "}" && mlInLeaderLine) mlInLeaderLine = false;
                            else if (mlText == null && value != "CONTEXT_DATA{" && value != "LEADER{" && value != "LEADER_LINE{")
                                mlText = value;
                        }
                        else if (code == "305") mlInLeaderLine = false;
                        else if (code == "10")
                        {
                            var v = ParseCoord(value);
                            if (mlInLeaderLine)
                            {
                                if (mlTipX == null) mlTipX = v;
                            }
                            else if (mlX == null) mlX = v;
                        }
                        else if (code == "20")
                        {
                            var v = ParseCoord(value);
                            if (mlInLeaderLine)
                            {
                                if (mlTipX.HasValue && mlTipY == null) mlTipY = v;
                            }
                            else if (mlY == null) mlY = v;
                        }
                        break;
                }
            }
            FlushEntity(entityType);
        }

        /// <summary>Добавляет отрезок геометрии (пропуская вырожденные), с защитным пределом.</summary>
        private static void AddLine(DxfPlanResult res, string layer, double x1, double y1, double x2, double y2)
        {
            if (x1 == x2 && y1 == y2) return;
            if (res.Lines.Count >= MaxGeometrySegments) return;
            res.Lines.Add(new DxfGeomLine { Layer = layer ?? string.Empty, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 });
        }

        /// <summary>Число из имени блока: "Bottom_R_1500" -> 1500, "Side_DPR_1250" -> 1250.</summary>
        public static double? ParseSize(string block)
        {
            if (string.IsNullOrEmpty(block)) return null;
            var m = BlockSize.Match(block);
            if (!m.Success) return null;
            return double.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? (double?)d : null;
        }

        /// <summary>Число в координате: NBSP/пробелы-разделители убираются, дробная часть — точка.</summary>
        private static double? ParseCoord(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            var s = v.Replace('\u00A0', ' ').Replace(" ", string.Empty).Trim();
            if (s.Length == 0) return null;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            return null;
        }

        /// <summary>
        /// Каталог марок пополняется марками, найденными в выносках на плане (текст 304
        /// или восстановленный из блока по handle 341): часть марок (например, Neo Expo)
        /// хранится только в самих выносках и отсутствует в определениях блоков.
        /// </summary>
        private static void AugmentCatalogWithLeaders(DxfPlanResult res, BlockCatalog catalog)
        {
            foreach (var l in res.Leaders)
            {
                string text = l.Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text)) continue;

                var (mark, valve) = SplitLeaderText(text);
                if (string.IsNullOrWhiteSpace(mark)) continue;
                if (!CalloutDeviceParser.TryParse(mark, out _)) continue;

                if (!catalog.Marks.Any(m => string.Equals(m, mark, StringComparison.OrdinalIgnoreCase)))
                    catalog.Marks.Add(mark);
                if (!string.IsNullOrEmpty(valve) && !catalog.ValveByMark.ContainsKey(mark))
                    catalog.ValveByMark[mark] = valve;
            }
        }

        /// <summary>Каталог: уникальные читаемые тексты, распознаваемые как прибор отопления.</summary>
        private static void BuildMarkCatalog(DxfPlanResult res, List<string> texts,
            Dictionary<string, string> valveByMark = null)
        {
            foreach (var t in texts.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            {
                if (CalloutDeviceParser.TryParse(t, out var dev))
                {
                    res.Marks.Add(t);
                    res.MarkDevices.Add(dev);

                    if (valveByMark != null && valveByMark.TryGetValue(t, out var valve) &&
                        !string.IsNullOrEmpty(valve))
                    {
                        dev.ValveSetting = valve;
                    }
                }
            }
        }

        /// <summary>Кластеры координат -> этажи (слева направо, затем сверху вниз).</summary>
        private static void AssignFloors(DxfPlanResult res, int mergeGap)
        {
            if (res.Heaters.Count == 0) return;

            var clusters = new List<List<DxfCalloutItem>>();
            foreach (var p in res.Heaters)
            {
                List<DxfCalloutItem> best = null;
                double bestD = mergeGap;
                foreach (var c in clusters)
                {
                    var cx = c.Average(q => q.X);
                    var cy = c.Average(q => q.Y);
                    double d = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
                    if (d <= bestD) { best = c; bestD = d; }
                }
                if (best == null) { best = new List<DxfCalloutItem>(); clusters.Add(best); }
                best.Add(p);
            }

            var ordered = clusters
                .OrderBy(c => c.Average(q => q.X))
                .ThenBy(c => -c.Average(q => q.Y))
                .ToList();

            for (int k = 0; k < ordered.Count; k++)
            {
                var c = ordered[k];
                var f = new DxfFloor
                {
                    Index = k,
                    Label = "Этаж " + (k + 1),
                    CenterX = c.Average(q => q.X),
                    CenterY = c.Average(q => q.Y)
                };
                foreach (var p in c) { p.Floor = k; f.Heaters.Add(p); }
                res.Floors.Add(f);
            }
        }

        private static void AssignSequence(DxfPlanResult res)
        {
            foreach (var f in res.Floors)
            {
                var h = f.Heaters.OrderBy(q => q.Y).ThenBy(q => q.X).ToList();
                for (int i = 0; i < h.Count; i++) h[i].Seq = i;
            }
        }

        /// <summary>Дополнительные действия после чтения плана: сейчас каталоговое «угадывание»
        /// отключено — марка назначается только из выноски (см. AssignTruthMarks).
        /// Метод оставлен как точка расширения.
        /// </summary>
        private static void SuggestMarks(DxfPlanResult res)
        {
        }

        /// <summary>Истинная марка прибора — из выноски (MULTILEADER), остриё стрелки которой попадает
        /// в центр прямоугольника прибора (допуск по координатам центра: ±300 мм).
        /// Правило: ровно одна такая выноска → марка назначается; ноль или две и более —
        /// марка не назначается (прибор остаётся «выноска не найдена»).
        /// Допуск — диаметр минимума/максимальная длина стороны прямоугольника, т.е. остриё
        /// может касаться края прямоугольника, но не прилегающей к нему другой выноски.
        /// </summary>
        private static void AssignTruthMarks(DxfPlanResult res, double maxDistance = DefaultLeaderMarkDistance)
        {
            foreach (var h in res.Heaters)
            {
                DxfLeaderText best = null;
                string bestMark = null;
                string bestValve = null;
                double bestD = double.MaxValue;
                int count = 0;

                foreach (var l in res.Leaders)
                {
                    var (mark, valve) = SplitLeaderText(l.Text);
                    if (string.IsNullOrWhiteSpace(mark)) continue;
                    if (!CalloutDeviceParser.TryParse(mark, out _)) continue;

                    double dx = l.X - h.X;
                    double dy = l.Y - h.Y;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d <= maxDistance)
                    {
                        count++;
                        if (d < bestD) { bestD = d; best = l; bestMark = mark; bestValve = valve; }
                    }
                }
                if (count != 1 || best == null || bestMark == null) continue;

                h.TruthMark = bestMark;
                h.TruthValveSetting = bestValve;
                h.TruthDistance = bestD;
                h.MarkSource = "стрелка в центре";

                if (CalloutDeviceParser.TryParse(bestMark, out var dev))
                {
                    dev.ValveSetting = bestValve ?? dev.ValveSetting;
                    h.Device = dev;
                    h.SuggestedMark = dev.Mark;
                }
            }
        }

        /// <summary>
        /// Гарантия полноты каталога: каждая марка, реально назначенная прибору на плане
        /// (TruthMark), обязана присутствовать в каталоге (карте типов), даже если
        /// CalloutDeviceParser не смог её распознать как известный тип. Строка каталога
        /// создаётся с минимальным набором полей — пользователь назначит семейство/тип
        /// вручную в окне «Марки DXF».
        /// Приборы без выноски тоже попадают в каталог — по имени блока (например,
        /// "Side_DPR_2000"), чтобы нельзя было «потерять» ни один тип прибора на плане.
        /// </summary>
        private static void EnsureCatalogCoversHeaterMarks(DxfPlanResult res)
        {
            foreach (var h in res.Heaters)
            {
                string mark = string.IsNullOrWhiteSpace(h.TruthMark)
                    ? (string.IsNullOrWhiteSpace(h.Block) ? null : h.Block)
                    : h.TruthMark.Trim();
                if (string.IsNullOrWhiteSpace(mark)) continue;

                if (string.IsNullOrEmpty(h.SuggestedMark)) h.SuggestedMark = mark;

                if (res.MarkDevices.Any(d => string.Equals(d.Mark, mark, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var dev = new GrdDevice
                {
                    Kind = DeviceKind.RadiatorHeating,
                    Mark = mark,
                    Code = mark,
                    LengthMm = h.Size,
                    ValveSetting = h.TruthValveSetting
                };
                res.MarkDevices.Add(dev);
                if (!res.Marks.Any(m => string.Equals(m, mark, StringComparison.OrdinalIgnoreCase)))
                    res.Marks.Add(mark);
            }
        }

        /// <summary>Расщепление выноски: текст до первого «\P» — марка, после — «n=…».</summary>
        private static (string mark, string valve) SplitLeaderText(string text)
        {
            if (string.IsNullOrEmpty(text)) return (null, null);
            string[] parts = text.Split(new[] { "\\P" }, StringSplitOptions.None);

            string markPart = parts[0].Trim();
            string valve = null;
            for (int i = 1; i < parts.Length; i++)
            {
                var m = ValveSettingText.Match(parts[i].Trim().Trim('\u200B'));
                if (m.Success)
                {
                    valve = m.Groups[1].Value.Trim().Replace(',', '.');
                    break;
                }
            }
            return (markPart, valve);
        }

        /// <summary>
        /// Текст выноски из определения блока, на который MULTILEADER ссылается кодом 341
        /// (при пустой строке 304): марка — первый текст, распознаваемый как прибор,
        /// настройка «n=…» из текста вида «n=1». Если марки нет — null.
        /// </summary>
        private static string ResolveBlockText(BlockCatalog catalog, string blockHandle)
        {
            if (catalog == null || string.IsNullOrEmpty(blockHandle)) return null;
            if (!catalog.BlockNameByHandle.TryGetValue(blockHandle, out var blockName)) return null;
            if (!catalog.TextsByBlock.TryGetValue(blockName, out var texts)) return null;

            string mark = null, valve = null;
            foreach (var raw in texts)
            {
                string t = raw.Trim().Trim('\u200B');
                if (string.IsNullOrEmpty(t)) continue;

                if (mark == null && CalloutDeviceParser.TryParse(t, out _)) { mark = t; continue; }

                var vm = ValveSettingText.Match(t);
                if (valve == null && vm.Success)
                {
                    valve = vm.Groups[1].Value.Trim().Replace(',', '.');
                }
            }

            if (mark == null) return null;
            return valve == null ? mark : mark + "\\P" + "n=" + valve;
        }
    }
}