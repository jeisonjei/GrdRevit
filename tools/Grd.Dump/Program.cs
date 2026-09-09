using System;
using System.IO;
using System.Linq;
using System.Text;
using GrdRevit.Core;

namespace GrdDump
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: Grd.Dump <file.grd> [--exports] | Grd.Dump grr <file.grr> [--csv <file.csv>]");
                return 2;
            }

            if (string.Equals(args[0], "grr", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("usage: Grd.Dump grr <file.grr> [--txt <file.txt>] [--csv <file.csv>]");
                    return 2;
                }
                return DumpGrr(args[1], args);
            }

            if (string.Equals(args[0], "--runs", StringComparison.OrdinalIgnoreCase))
                return DumpRuns(args[1], args.Length > 2 ? args[2] : null);

            if (string.Equals(args[0], "--verify", StringComparison.OrdinalIgnoreCase))
                return Verify(args[1], args);

            var doc = GrdParser.Parse(args[0]);
            Console.WriteLine($"Project : {doc.ProjectName}");
            Console.WriteLine($"Runs    : {doc.Runs.Count}");
            Console.WriteLine($"Rooms   : {doc.Rooms.Count}");
            Console.WriteLine($"Devices : {doc.DeviceCount}");
            Console.WriteLine($"Types   : {doc.TypeStats.Count}");
            Console.WriteLine();

            Console.WriteLine("-- ROOMS --");
            foreach (var r in doc.Rooms)
                Console.WriteLine($"   {r.Order,3}: {r.Name}");
            Console.WriteLine();

            Console.WriteLine("-- DEVICE TYPE STATS --");
            foreach (var s in doc.TypeStats)
            {
                var cap = s.CapacityW.HasValue ? $"{s.CapacityW.Value:0} Вт" : "-";
                var rooms = s.Rooms.Count > 0 ? string.Join(", ", s.Rooms.Take(4)) : "-";
                Console.WriteLine($"   {s.Count,4} x {s.Code,-20} {s.FamilyHint,-20} {cap,-8} conn={s.Connection,-3} rooms={rooms}");
            }
            Console.WriteLine();

            Console.WriteLine("-- SAMPLE DEVICES (first 15) --");
            foreach (var d in doc.Devices.Take(15))
            {
                Console.WriteLine($"   @{d.ByteOffset,8} {d.Code,-20} cap={d.CapacityW?.ToString("0") ?? "-",-6} d={d.Diameter,-4} room={d.Room,-14} nb=[{d.RawNeighbours}]");
            }

            var unparsed = doc.Runs.Where(r => DeviceCodeParser.CanStartWith(r.Text)
                                               && !DeviceCodeParser.TryParse(r.Text, out _)).Select(r => r.Text).Distinct().ToList();
            Console.WriteLine();
            Console.WriteLine("-- CANDIDATE-UNPARSED --");
            foreach (var u in unparsed.Take(20))
                Console.WriteLine($"   {u}");

            if (args.Any(a => string.Equals(a, "--exports", StringComparison.OrdinalIgnoreCase)))
                return GrdExports.Save(doc, args[0], Arg(args, "--txt"), Arg(args, "--csv"));

            return 0;
        }

        private static int Verify(string grdPath, string[] args)
        {
            // usage: Grd.Dump --verify <file> --runs N --rooms N --devices N --types N
            int Exp(string key) =>
                int.TryParse(Arg(args, key), out var v) ? v : -1;

            var doc = GrdParser.Parse(grdPath);
            var (er, env, ed, et) = (Exp("--runs"), Exp("--rooms"), Exp("--devices"), Exp("--types"));
            int bad = 0;
            void Cmp(string name, int got, int exp)
            {
                string ok = got == exp ? "OK" : $"FAIL (got {got}, exp {exp})";
                Console.WriteLine($"   {name,-10} {ok}");
                if (got != exp) bad++;
            }
            Console.WriteLine($"Verify: {grdPath}");
            if (er >= 0) Cmp("runs", doc.Runs.Count, er);
            if (env >= 0) Cmp("rooms", doc.Rooms.Count, env);
            if (ed >= 0) Cmp("devices", doc.DeviceCount, ed);
            if (et >= 0) Cmp("types", doc.TypeStats.Count, et);
            Console.WriteLine(bad == 0 ? "PASS" : $"FAIL ({bad} mismatch)");
            return bad;
        }

        private static int DumpRuns(string grdPath, string outFile)
        {
            var runs = GrdParser.ExtractRuns(File.ReadAllBytes(grdPath));
            var sb = new StringBuilder();
            sb.AppendLine($"Runs : {runs.Count}");
            foreach (var (run, k) in runs.Select((r, i) => (r, i)))
            {
                var t = run.Text.Length > 40 ? run.Text.Substring(0, 40) + "…" : run.Text;
                sb.AppendLine($"   [{k,5}] @{run.ByteOffset,8} '{t}'");
            }
            if (outFile != null)
            {
                File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(true));
                Console.WriteLine($"Runs -> {outFile}");
                return 0;
            }
            Console.Write(sb.ToString());
            return 0;
        }

        private static int DumpGrr(string grrPath, string[] args)
        {
            var res = GrrCleanParser.Parse(grrPath);
            Console.WriteLine($"Project : {res.ProjectName}");
            Console.WriteLine($"Scheme  : {res.SchemeTitle}");
            Console.WriteLine($"Sections: {res.Sections.Count}");
            foreach (var s in res.Sections)
            {
                var devInfo = s.Devices.Count > 0
                    ? $" | devices={s.Devices.Count} (room: {s.Devices.Count(d => !string.IsNullOrWhiteSpace(d.Room))})"
                    : string.Empty;
                Console.WriteLine($"   {s.Kind,-12} [{s.FirstRun,5}..{s.LastRun,5}] {s.Name}{devInfo}");
            }
            Console.WriteLine();
            Console.WriteLine($"Devices     : {res.DeviceCount} (devices={res.DeviceKindCount}, armature={res.ArmatureCount})");
            Console.WriteLine($"Rooms       : {res.Rooms.Count}");

            Console.WriteLine();
            Console.WriteLine("-- DEVICES: код | комната | диаметр | маркер --");
            foreach (var d in res.Devices.Where(d => d.BlockKind != "Armature"))
            {
                var cap = d.CapacityW.HasValue ? $"{d.CapacityW.Value:0} Вт" : "-";
                var conn = string.IsNullOrEmpty(d.Connection) ? "-" : d.Connection;
                Console.WriteLine($"   [{d.Run,5}] {d.Code,-18} {d.FamilyHint,-18} {cap,-8} conn={conn,-4} room={d.Room,-20} d={d.Diameter,-8} m={d.Marker}");
            }

            Console.WriteLine();
            Console.WriteLine("-- TYPE STATS (отопительные приборы) --");
            foreach (var g in res.Devices.Where(d => d.BlockKind != "Armature").GroupBy(d => d.Code)
                         .OrderByDescending(g => g.Count()))
            {
                var cap = g.First().CapacityW.HasValue ? $"{g.First().CapacityW.Value:0} Вт" : "-";
                var rooms = g.Select(x => x.Room).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().Count();
                Console.WriteLine($"   {g.Count(),4} x {g.Key,-18} {g.First().FamilyHint,-18} {cap,-8} rooms={rooms}");
            }

            Console.WriteLine();
            Console.WriteLine("-- ARMATURE (sample 10) --");
            foreach (var d in res.Devices.Where(d => d.BlockKind == "Armature").Take(10))
                Console.WriteLine($"   [{d.Run,5}] {d.Code,-18} room={d.Room,-20} m={d.Marker}");

            string txt = Arg(args, "--txt");
            string csv = Arg(args, "--csv");
            if (txt != null || csv != null)
                return GrrExports.Save(grrPath, res, txt, csv);

            return 0;
        }

        private static string Arg(string[] args, string key)
        {
            int idx = Array.FindIndex(args, a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 || idx + 1 >= args.Length) return null;
            return args[idx + 1];
        }
    }

    internal static class GrdExports
    {
        public static int Save(GrdDocument doc, string grdPath, string txtPath, string csvPath)
        {
            txtPath = txtPath ?? Path.Combine(Path.GetDirectoryName(grdPath) ?? ".", Path.GetFileNameWithoutExtension(grdPath) + ".clean.txt");
            csvPath = csvPath ?? Path.Combine(Path.GetDirectoryName(grdPath) ?? ".", Path.GetFileNameWithoutExtension(grdPath) + ".devices.csv");

            var sb = new StringBuilder();
            sb.AppendLine("=== ЧИСТАЯ СТРУКТУРА .grd (ПВФ / АУДИТОР 7.3) ===");
            sb.AppendLine($"Файл    : {grdPath}");
            sb.AppendLine($"Проект  : {doc.ProjectName}");
            sb.AppendLine($"Ранов   : {doc.Runs.Count}");
            sb.AppendLine($"Комнат  : {doc.Rooms.Count}");
            sb.AppendLine($"Приборов: {doc.Devices.Count} (по кодам {doc.TypeStats.Count} типов)");
            sb.AppendLine();

            sb.AppendLine("## КОМНАТЫ");
            foreach (var r in doc.Rooms.OrderBy(r => r.Name))
                sb.AppendLine($"    {r.Name}");
            sb.AppendLine();

            sb.AppendLine("## ТИПЫ (агрегированно)");
            foreach (var s in doc.TypeStats)
            {
                var cap = s.CapacityW.HasValue ? $"{s.CapacityW.Value:0} Вт" : "-";
                sb.AppendLine($"    {s.Count,4} x {s.Code,-20} {s.FamilyHint,-20} {cap,-10} conn={s.Connection}");
            }
            sb.AppendLine();

            sb.AppendLine("## ПРИБОРЫ (по комнатам)");
            foreach (var g in doc.Devices.GroupBy(d => string.IsNullOrWhiteSpace(d.Room) ? "(без комнаты)" : d.Room)
                         .OrderBy(g => g.Key == "(без комнаты)" ? "zzz" : g.Key))
            {
                sb.AppendLine($"    — {g.Key} ({g.Count()})");
                foreach (var d in g)
                {
                    var cap = d.CapacityW.HasValue ? $"{d.CapacityW.Value:0} Вт" : "-";
                    var conn = string.IsNullOrEmpty(d.Connection) ? "-" : d.Connection;
                    var diam = string.IsNullOrEmpty(d.Diameter) ? "-" : d.Diameter;
                    sb.AppendLine($"        {d.Code,-20} {d.FamilyHint,-20} {cap,-10} d={diam,-4} {conn}");
                }
            }
            File.WriteAllText(txtPath, sb.ToString(), Encoding.UTF8);
            File.WriteAllText(csvPath, DevicesCsv(doc), Encoding.UTF8);
            Console.WriteLine($"TXT -> {txtPath}");
            Console.WriteLine($"CSV -> {csvPath}");
            return 0;
        }

        private static string DevicesCsv(GrdDocument doc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Код;Семейство;Мощность,Вт;Подключение;Диаметр;Комната");
            foreach (var d in doc.Devices)
            {
                sb.AppendLine(string.Join(";",
                    d.Code,
                    d.FamilyHint,
                    d.CapacityW?.ToString("0") ?? "",
                    d.Connection ?? "",
                    d.Diameter ?? "",
                    d.Room ?? ""));
            }
            return sb.ToString();
        }
    }

    internal static class GrrExports
    {
        public static int Export(GrdDocument doc, string srcPath, string outPath)
        {
            return 0;
        }

        public static int Save(string grrPath, GrrCleanResult res, string txtPath, string csvPath)
        {
            txtPath = txtPath ?? Path.Combine(Path.GetDirectoryName(grrPath) ?? ".", Path.GetFileNameWithoutExtension(grrPath) + ".clean.txt");
            csvPath = csvPath ?? Path.Combine(Path.GetDirectoryName(grrPath) ?? ".", Path.GetFileNameWithoutExtension(grrPath) + ".devices.csv");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== ЧИСТАЯ СТРУКТУРА .grr ===");
            sb.AppendLine($"Файл    : {grrPath}");
            sb.AppendLine($"Проект  : {res.ProjectName}");
            sb.AppendLine($"Схема   : {res.SchemeTitle}");
            sb.AppendLine($"Ранов   : {res.Runs.Count}");
            sb.AppendLine();
            foreach (var s in res.Sections)
            {
                sb.AppendLine($"## [{s.FirstRun,5}..{s.LastRun,5}] {s.Name} ({s.RunCount} ранов)");
                foreach (var d in s.Devices.Take(400))
                {
                    sb.AppendLine($"    [{d.Run,5}] {d.Code,-18} room={d.Room,-20} d={d.Diameter,-8} m={d.Marker}");
                }
                if (s.Devices.Count > 400)
                    sb.AppendLine($"    ... и ещё {s.Devices.Count - 400} устройств");
                sb.AppendLine();
            }
            File.WriteAllText(txtPath, sb.ToString(), Encoding.UTF8);
            File.WriteAllText(csvPath, Csv(res), Encoding.UTF8);
            Console.WriteLine($"TXT -> {txtPath}");
            Console.WriteLine($"CSV -> {csvPath}");
            return 0;
        }

        private static string Csv(GrrCleanResult res)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Раздел;Ран;Код;Семейство;Мощность,Вт;Подключение;Комната;Диаметр;Маркер");
            foreach (var d in res.Devices)
            {
                sb.AppendLine(string.Join(";",
                    d.BlockKind,
                    d.Run,
                    d.Code,
                    d.FamilyHint,
                    d.CapacityW?.ToString("0") ?? "",
                    d.Connection ?? "",
                    d.Room ?? "",
                    d.Diameter ?? "",
                    d.Marker ?? ""));
            }
            return sb.ToString();
        }
    }
}