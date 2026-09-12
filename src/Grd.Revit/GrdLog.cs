using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

namespace GrdRevit
{
    /// <summary>Лёгкий пошаговый журнал для диагностики вне отладчика.
    /// Пишет в НЕСКОЛЬКО гарантированных мест: %LOCALAPPDATA%\GrdRevit\grd-revit.log
    /// (папка самого плагина — всегда доступна на запись) и %TEMP%\grd-revit.log.
    /// Любая запись выполняется максимально независимо: падение не влияет на Revit.</summary>
    public static class GrdLog
    {
        private static readonly object Sync = new object();
        private static readonly string[] Candidates = BuildCandidates();

        private static string[] BuildCandidates()
        {
            var list = new List<string>();
            void Add(string dir)
            {
                if (string.IsNullOrEmpty(dir)) return;
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    if (Directory.Exists(dir)) list.Add(dir);
                }
                catch { }
            }

            // Папка плагина — приоритетная: она точно существует (туда развёрнуты DLL).
            var asmDir = Path.GetDirectoryName(typeof(GrdLog).Assembly.Location);
            Add(asmDir);

            string la = null;
            try { la = Environment.GetEnvironmentVariable("LOCALAPPDATA"); } catch { }
            if (!string.IsNullOrEmpty(la))
            {
                Add(Path.Combine(la, "GrdRevit"));
                Add(Path.Combine(la, "Temp"));
            }

            foreach (var v in new[] { "TEMP", "TMP" })
            {
                string t = null;
                try { t = Environment.GetEnvironmentVariable(v); } catch { }
                Add(t);
            }

            return list.ToArray();
        }

        /// <summary>Banner новой сессии: код сборки, путь, время. Пишется из OnStartup,
        /// чтобы по логу можно было понять, какая сборка и откуда загрузилась.</summary>
        public static void LogSessionStart()
        {
            try
            {
                string version = "?";
                string location = "?";
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    var fv = asm.GetCustomAttribute<AssemblyFileVersionAttribute>();
                    version = fv?.Version ?? asm.GetName().Version?.ToString() ?? "?";
                    location = asm.Location;
                }
                catch { }

                Log(new string('=', 78));
                Log("SESSION START  версия=" + version + "  сборка=" + location);
                int pid = 0;
                try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }
                Log("процесс=" + pid + "  путь к сборке=" +
                    (string.IsNullOrEmpty(location) ? "?" : Path.GetDirectoryName(location)));
                Log("файл журнала: " + CurrentPath());
                Log(new string('=', 78));
            }
            catch { }
        }

        /// <summary>Текущий путь журнала (первый доступный из кандидатов).</summary>
        public static string CurrentPath()
        {
            try
            {
                foreach (var d in Candidates)
                {
                    try
                    {
                        var p = Path.Combine(d, "grd-revit.log");
                        return p;
                    }
                    catch { }
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GrdRevit", "grd-revit.log");
        }

        public static void Log(string message)
        {
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                    " [T" + Thread.CurrentThread.ManagedThreadId + "] " + message + Environment.NewLine;
                foreach (var dir in Candidates)
                {
                    try
                    {
                        File.AppendAllText(Path.Combine(dir, "grd-revit.log"), line);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}