using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using PdfSharp.Fonts;

namespace GrdRevit.Ui
{
    /// <summary>
    /// Резолвер шрифтов PDFsharp для Windows: находит файлы системных шрифтов (Arial и любые другие
    /// из реестра шрифтов) и отдаёт их байты PDFsharp для встраивания в PDF. Без этого PDFsharp 6
    /// на .NET Framework не находит шрифты и выбрасывает "No appropriate font found".
    /// </summary>
    internal sealed class GdiFontResolver : IFontResolver
    {
        private static readonly string FontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        private static readonly Dictionary<string, List<FontEntry>> RegistryFonts = BuildRegistryFonts();

        private sealed class FontEntry
        {
            public string FileName;
            public bool Bold;
            public bool Italic;
        }

        public static void Install()
        {
            if (GlobalFontSettings.FontResolver is GdiFontResolver) return;
            GlobalFontSettings.FontResolver = new GdiFontResolver();
        }

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            var candidates = new List<FontEntry>();
            foreach (var kv in RegistryFonts)
            {
                if (string.Equals(kv.Key, familyName, StringComparison.OrdinalIgnoreCase))
                    candidates.AddRange(kv.Value);
            }

            var exact = candidates.FirstOrDefault(e => e.Bold == isBold && e.Italic == isItalic);
            if (exact != null && File.Exists(Path.Combine(FontsDir, exact.FileName)))
                return new FontResolverInfo(Path.Combine(FontsDir, exact.FileName));

            var regular = candidates.FirstOrDefault(e => !e.Bold && !e.Italic);
            if (regular != null && File.Exists(Path.Combine(FontsDir, regular.FileName)))
                return new FontResolverInfo(Path.Combine(FontsDir, regular.FileName), isBold, isItalic);

            var fallback = FindFile("arial.ttf");
            if (fallback != null)
                return new FontResolverInfo(fallback, isBold, isItalic);

            return new FontResolverInfo(familyName);
        }

        public byte[] GetFont(string faceName)
        {
            try
            {
                if (!string.IsNullOrEmpty(faceName) && File.Exists(faceName))
                    return File.ReadAllBytes(faceName);
            }
            catch { }

            var arial = FindFile("arial.ttf");
            if (arial != null)
            {
                try { return File.ReadAllBytes(arial); }
                catch { }
            }
            return null;
        }

        private static string FindFile(string name)
        {
            try
            {
                var dir = string.IsNullOrEmpty(FontsDir) ? @"C:\Windows\Fonts" : FontsDir;
                if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
                if (Directory.Exists(dir))
                {
                    var found = Directory.GetFiles(dir, "*.ttf").FirstOrDefault();
                    return found;
                }
            }
            catch { }
            return null;
        }

        private static Dictionary<string, List<FontEntry>> BuildRegistryFonts()
        {
            var map = new Dictionary<string, List<FontEntry>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"))
                {
                    if (key == null) return map;
                    foreach (var name in key.GetValueNames())
                    {
                        var file = key.GetValue(name) as string;
                        if (string.IsNullOrEmpty(file) || !IsFontFile(file)) continue;
                        var clean = name;
                        foreach (var suffix in new[] { " (TrueType)", " (OpenType)", " (TT)", " (OT)", " (All res)" })
                            clean = clean.Replace(suffix, string.Empty);

                        var tokens = clean.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        var skipIdx = new HashSet<int>();
                        bool bold = false, italic = false;
                        for (int i = 0; i < tokens.Length; i++)
                        {
                            var t = tokens[i].ToLowerInvariant();
                            if (t == "bold") { bold = true; skipIdx.Add(i); }
                            else if (t == "italic" || t == "oblique") { italic = true; skipIdx.Add(i); }
                            else if (t == "regular") { skipIdx.Add(i); }
                        }
                        var family = string.Join(" ", tokens.Where((t, i) => !skipIdx.Contains(i)));
                        if (family.Length == 0) continue;

                        if (!map.TryGetValue(family, out var list)) map[family] = list = new List<FontEntry>();
                        list.Add(new FontEntry { FileName = file, Bold = bold, Italic = italic });
                    }
                }
            }
            catch { }
            return map;
        }

        private static bool IsFontFile(string name)
        {
            return name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase);
        }
    }
}