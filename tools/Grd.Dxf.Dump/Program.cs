using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using GrdRevit.Core;

namespace GrdDxfDump
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Out.WriteLine("Usage: Grd.Dxf.Dump <file.dxf> [--variants] [--scan]");
                return 1;
            }

            var path = args[0];
            if (!File.Exists(path)) { Console.Error.WriteLine("Файл не найден: " + path); return 1; }

            if (args.Length > 1 && args[1] == "--variants") return Variants(path);
            if (args.Length > 1 && args[1] == "--scan") return Scan(path);

            var res = DxfCalloutParser.Parse(path);

            foreach (var msg in res.ParseMessages) Console.Out.WriteLine("[msg] " + msg);
            Console.Out.WriteLine("Приборов на плане: " + res.HeaterCount);
            Console.Out.WriteLine("Этажей:            " + res.FloorCount);
            Console.Out.WriteLine("Марок в каталоге:  " + res.Marks.Count);
            Console.Out.WriteLine("Выносок:           " + res.LeaderCount);
            Console.Out.WriteLine("Выносок из блока:  " + res.Leaders.Count(l => l.BlockHandle != null));

            var src = res.Heaters.GroupBy(h => h.MarkSource ?? "выноска не найдена")
                .OrderByDescending(g => g.Count())
                .Select(g => $"  {g.Key,-20} x{g.Count()}");
            Console.Out.WriteLine("Источники марок:");
            Console.Out.WriteLine(string.Join("\n", src));

            var noMark = res.Heaters.Where(h => h.TruthMark == null).ToList();
            Console.Out.WriteLine("Без марок: " + noMark.Count);
            foreach (var h in noMark.Take(20))
                Console.Out.WriteLine($"    {h.Block,-24} ({h.X:N0},{h.Y:N0})");

            Console.Out.WriteLine("Примеры марок (блок, марка, настройка, дистанция):");
            foreach (var h in res.Heaters.Where(x => x.TruthMark != null).Take(15))
                Console.Out.WriteLine($"    {h.Block,-24} {h.TruthMark}  n={h.TruthValveSetting ?? "-"}  d={h.TruthDistance:N0}");

            Console.Out.WriteLine();
            Console.Out.WriteLine("--- Каталог марок ---");
            foreach (var m in res.MarkDevices)
                Console.Out.WriteLine("  {0,-28} {1,5} Вт  {2,-20} {3}", m.Code, m.CapacityW, m.FamilyHint, m.Mark);

            Console.Out.WriteLine();
            Console.Out.WriteLine("--- Приборы: вариант -> рекомендованная марка ---");
            foreach (var g in res.Heaters.GroupBy(h => h.Block).OrderByDescending(g2 => g2.Count()))
            {
                var first = g.First();
                var m = res.SuggestMark(first.Size);
                Console.Out.WriteLine("  {0,-24} x{1,-4} размер={2,-6} марка={3}", g.Key, g.Count(),
                    first.Size.HasValue ? first.Size.Value.ToString("0") : "-",
                    m == null ? "-" : m.Mark);
            }

            Console.Out.WriteLine();
            Console.Out.WriteLine("--- Распределение по этажам ---");
            foreach (var f in res.Floors)
            {
                Console.Out.WriteLine($"  {f.Label}: {f.DeviceCount} приборов, центр ({f.CenterX:N0}, {f.CenterY:N0})");
                foreach (var h in f.Heaters.Take(6))
                {
                    var m = h.Device;
                    Console.Out.WriteLine("    H {0} ({1:N0},{2:N0}) -> {3}", h.Block, h.X, h.Y, m?.Code ?? "-");
                }
            }
            return 0;
        }

        private static int Variants(string path)
        {
            Console.Out.WriteLine("--- Варианты выносок (тексты Classic/TEPLA/NEO/Surpa) ---");
            var calloutTexts = new Dictionary<string, int>();
            var heaterBlocks = new Dictionary<string, int>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, System.Text.Encoding.UTF8))
            {
                string section = "-"; bool inBlocks = false, inBlock = false, inEntities = false;
                string name = null; string etype = null; string txt = null;
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    string code = line.Trim();
                    string value = sr.ReadLine();
                    if (value == null) break;
                    if (code == "0")
                    {
                        if (value == "SECTION")
                        {
                            var n1 = sr.ReadLine(); var n2 = sr.ReadLine();
                            if (n1 != null && n2 != null) { var s = n2.Trim(); inBlocks = s == "BLOCKS"; inEntities = s == "ENTITIES"; section = s; }
                            continue;
                        }
                        if (value == "ENDSEC") { inBlocks = false; inEntities = false; continue; }
                        if (value == "ENDBLK") { if (inBlock && name != null && IsCalloutText(txt)) calloutTexts[(txt.Trim())] = calloutTexts.TryGetValue(txt.Trim(), out var c) ? c + 1 : 1; inBlock = false; name = null; txt = null; continue; }
                        if (inBlocks && value == "BLOCK") { inBlock = true; continue; }
                        if (inEntities && value == "INSERT") { txt = null; }
                        etype = value;
                        continue;
                    }
                    if (inEntities)
                    {
                        if (etype == "INSERT" && code == "2" && value.StartsWith("Bottom_") || etype == "INSERT" && code == "2" && value.StartsWith("Side_"))
                            heaterBlocks[value] = heaterBlocks.TryGetValue(value, out var h) ? h + 1 : 1;
                        continue;
                    }
                    if (!inBlocks || !inBlock) continue;
                    if (name == null && code == "2") name = value;
                    else if ((etype == "MTEXT" || etype == "TEXT") && code == "1" && IsCalloutText(value)) txt = value;
                }
            }
            Console.Out.WriteLine("Всего выносок-текстов (уникальных): " + calloutTexts.Count);
            foreach (var kv in calloutTexts.OrderByDescending(k => k.Value))
                Console.Out.WriteLine("  [" + kv.Value + "x] " + kv.Key);
            Console.Out.WriteLine();
            Console.Out.WriteLine("Приборы на плане (блоки и количества):");
            foreach (var kv in heaterBlocks.OrderByDescending(k => k.Value))
                Console.Out.WriteLine("  " + kv.Key + "  x" + kv.Value);
            Console.Out.WriteLine("Итого приборов: " + heaterBlocks.Values.Sum());
            return 0;
        }

        private static bool IsCalloutText(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return false;
            var s = v.ToUpperInvariant();
            return s.Contains("CLASSIC") || s.Contains("TEPLA") || s.Contains("NEO") || s.Contains("SURPA");
        }

        private static int Scan(string path)
        {
            Console.Out.WriteLine("Scan: " + path);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, System.Text.Encoding.UTF8))
            {
                var sections = new List<string>();
                string section = "-";
                string entityType = null;
                string layer = null, block = null;
                var layerCount = new Dictionary<string, int>();
                var blockLayerCount = new Dictionary<string, int>();
                var heaterBlocks = new Dictionary<string, int>();
                string line;
                void Flush()
                {
                    if (section == "ENTITIES" && entityType == "INSERT" && layer != null)
                    {
                        layerCount[layer] = layerCount.TryGetValue(layer, out var a) ? a + 1 : 1;
                        string key = (block ?? "") + "|" + layer;
                        blockLayerCount[key] = blockLayerCount.TryGetValue(key, out var b) ? b + 1 : 1;
                        if (layer == DxfCalloutParser.HeaterLayer)
                            heaterBlocks[block ?? "?"] = heaterBlocks.TryGetValue(block, out var c) ? c + 1 : 1;
                    }
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
                            var n1 = sr.ReadLine(); var n2 = sr.ReadLine();
                            if (n1 != null && n2 != null) { section = n2.Trim(); sections.Add(section); }
                            continue;
                        }
                        if (value == "ENDSEC") { section = "-"; continue; }
                        Flush();
                        entityType = value; layer = null; block = null;
                        continue;
                    }
                    if (section != "ENTITIES") continue;
                    if (code == "8") layer = value;
                    else if (code == "2") block = value;
                }
                Flush();
                Console.Out.WriteLine("Sections: " + string.Join(", ", sections.Distinct()));
                Console.Out.WriteLine("INSERT layers:");
                foreach (var kv in layerCount.OrderByDescending(k => k.Value).Take(15))
                    Console.Out.WriteLine("  {0,-40} {1}", kv.Key, kv.Value);
                Console.Out.WriteLine("INSERT block|layer:");
                foreach (var kv in blockLayerCount.OrderByDescending(k => k.Value).Take(15))
                    Console.Out.WriteLine("  {0,-40} {1}", kv.Key, kv.Value);
                Console.Out.WriteLine("Heater-layer blocks:");
                foreach (var kv in heaterBlocks.OrderByDescending(k => k.Value).Take(15))
                    Console.Out.WriteLine("  {0,-30} {1}", kv.Key, kv.Value);
            }
            return 0;
        }
    }
}