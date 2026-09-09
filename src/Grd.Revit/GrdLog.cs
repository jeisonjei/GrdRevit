using System;
using System.IO;
using System.Threading;

namespace GrdRevit
{
    /// <summary>Лёгкий пошаговый журнал для диагностики вне отладчика.
    /// Всегда пишет в %TEMP%\grd-revit.log (дописывает).</summary>
    public static class GrdLog
    {
        private static readonly object Sync = new object();

        public static void Log(string message)
        {
            try
            {
                lock (Sync)
                {
                    string path = Path.Combine(Path.GetTempPath(), "grd-revit.log");
                    File.AppendAllText(path,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " [T" + Thread.CurrentThread.ManagedThreadId + "] " + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}