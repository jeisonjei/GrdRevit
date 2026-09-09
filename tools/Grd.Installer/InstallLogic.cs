using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace GrdInstaller
{
    internal sealed class RevitInstall
    {
        public int Year;
        public string ExePath;
        public string AddinDir;
        public string AddinFile;
        public string BinDir;

        public bool SupportsNet48 => Year >= 2020 && Year <= 2024;
        public bool SupportsNet8 => Year >= 2025 && Year <= 2026;
        public bool Supported => SupportsNet48 || SupportsNet8;

        public bool IsInstalled => File.Exists(AddinFile) && Directory.Exists(BinDir);
    }

    internal static class InstallLogic
    {
        private const string AddinId2024 = "86C8044E-E80E-438B-9C23-239B96D1E94B";
        private const string AddinId2026 = "8E3F35CA-4985-4D7F-8F9F-FFEF76E7DB9E";
        private const string AddinName = "ГрД - отопительные приборы СО";
        private const string VendorDesc = "Загрузка .grd и типы отопительных приборов СО";

        public static List<RevitInstall> Detect()
        {
            var list = new List<RevitInstall>();
            var names = new HashSet<string>();

            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Autodesk\Revit"))
                {
                    if (key != null)
                        foreach (var sub in key.GetSubKeyNames())
                            if (int.TryParse(sub, out var y))
                                names.Add("Revit " + y);
                }
            }
            catch { }

            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (var root in new[] { pf, pf86 }.Where(p => !string.IsNullOrEmpty(p)))
                foreach (var d in SafeDirs(root, "Revit*"))
                {
                    var exe = Path.Combine(d, "Revit.exe");
                    if (File.Exists(exe)) names.Add(Path.GetFileName(d));
                }

            foreach (var n in names.OrderByDescending(n => n, StringComparer.Ordinal))
            {
                var year = 0;
                var parts = n.Split(' ');
                if (parts.Length == 2 && int.TryParse(parts[1], out year)) { }
                else continue;
                if (year < 2020 || year > 2026) continue;

                var exe = FindExe(year, pf, pf86);
                if (string.IsNullOrEmpty(exe)) continue;
                var addinsRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Autodesk", "Revit", "Addins", year.ToString());

                list.Add(new RevitInstall
                {
                    Year = year,
                    ExePath = exe,
                    AddinDir = addinsRoot,
                    AddinFile = Path.Combine(addinsRoot, "GrdRevit.addin"),
                    BinDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "GrdRevit", year.ToString())
                });
            }
            return list;
        }

        private static string FindExe(int year, string pf, string pf86)
        {
            foreach (var root in new[] { pf, pf86 }.Where(p => !string.IsNullOrEmpty(p)))
            {
                var d = Path.Combine(root, "Autodesk", "Revit " + year);
                var exe = Path.Combine(d, "Revit.exe");
                if (File.Exists(exe)) return exe;
            }
            return null;
        }

        private static IEnumerable<string> SafeDirs(string root, string pattern)
        {
            try { return Directory.GetDirectories(root, pattern); }
            catch { return Enumerable.Empty<string>(); }
        }

        public static void Install(RevitInstall r)
        {
            Directory.CreateDirectory(r.BinDir);
            Directory.CreateDirectory(r.AddinDir);

            var flavor = r.SupportsNet48 ? "Revit2024" : "Revit2026";
            WriteEmbedded(flavor + ".dll", Path.Combine(r.BinDir, "GrdRevit.dll"));
            WriteEmbedded(flavor + ".Core.dll", Path.Combine(r.BinDir, "GrdRevit.Core.dll"));

            var id = r.SupportsNet48 ? AddinId2024 : AddinId2026;
            var addin = new StringBuilder();
            addin.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            addin.AppendLine("<RevitAddIns>");
            addin.AppendLine("  <AddIn Type=\"Application\">");
            addin.AppendLine("    <Name>" + AddinName + "</Name>");
            addin.AppendLine("    <Assembly>" + Path.Combine(r.BinDir, "GrdRevit.dll") + "</Assembly>");
            addin.AppendLine("    <AddInId>" + id + "</AddInId>");
            addin.AppendLine("    <FullClassName>GrdRevit.GrdApplication</FullClassName>");
            addin.AppendLine("    <VendorId>GrdRevit</VendorId>");
            addin.AppendLine("    <VendorDescription>" + VendorDesc + "</VendorDescription>");
            addin.AppendLine("  </AddIn>");
            addin.AppendLine("</RevitAddIns>");
            File.WriteAllText(r.AddinFile, addin.ToString(), new UTF8Encoding(true));
        }

        public static void Uninstall(RevitInstall r)
        {
            if (File.Exists(r.AddinFile)) File.Delete(r.AddinFile);
            if (Directory.Exists(r.BinDir))
                try { Directory.Delete(r.BinDir, true); } catch { }
        }

        private static void WriteEmbedded(string logicalName, string dest)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream("GrdInstaller." + logicalName))
            {
                if (s == null) throw new FileNotFoundException("Встроенный файл не найден: " + logicalName);
                using (var f = File.Create(dest)) s.CopyTo(f);
            }
        }

        public static List<string> RunningRevit()
        {
            var list = new List<string>();
            foreach (var p in Process.GetProcessesByName("Revit"))
            {
                try
                {
                    var exe = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exe)) list.Add(exe);
                }
                catch { }
            }
            return list.Distinct().ToList();
        }

        public static void RestartRevit(string exePath)
        {
            foreach (var p in Process.GetProcessesByName("Revit"))
                try { p.Kill(); } catch { }
            System.Threading.Thread.Sleep(1500);
            try { Process.Start(exePath); } catch { }
        }
    }
}