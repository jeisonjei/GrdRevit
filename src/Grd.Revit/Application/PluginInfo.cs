using System;
using System.IO;
using System.Reflection;

namespace GrdRevit
{
    /// <summary>Сведения о версии плагина. Значение задаётся один раз — в
    /// Directory.Build.props (свойство Version) — и распространяется на все
    /// сборки; здесь оно только читается из атрибутов текущей сборки.</summary>
    public static class PluginInfo
    {
        public const string Product = "JTOOLS";

        /// <summary>Путь к файлу загруженной сборки (показывает, какая копия подключена).</summary>
        public static string AssemblyLocation
        {
            get
            {
                try { return Assembly.GetExecutingAssembly().Location; }
                catch { return "?"; }
            }
        }

        /// <summary>Номер версии из атрибута FileVersion, например «1.0.0.0».</summary>
        public static string Version
        {
            get
            {
                try
                {
                    var fv = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>();
                    if (fv != null && !string.IsNullOrEmpty(fv.Version)) return fv.Version;
                }
                catch { }
                return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
            }
        }

        /// <summary>Хэш коммита из InformationalVersion («1.0.0+abc1234…» или пусто).</summary>
        public static string Revision
        {
            get
            {
                try
                {
                    var iv = Assembly.GetExecutingAssembly()
                                      .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                    if (iv != null)
                    {
                        var plus = iv.IndexOf('+');
                        if (plus >= 0 && plus < iv.Length - 1)
                            return iv.Substring(plus + 1, Math.Min(12, iv.Length - plus - 1));
                    }
                }
                catch { }
                return "";
            }
        }

        /// <summary>Дата и время сборки файла DLL (время записи на диск).</summary>
        public static DateTime BuildDate
        {
            get
            {
                try { return File.GetLastWriteTime(Assembly.GetExecutingAssembly().Location); }
                catch { return DateTime.MinValue; }
            }
        }

        /// <summary>Короткая строка для строки состояния, например «JTOOLS v1.0.0».</summary>
        public static string DisplayShort
        {
            get
            {
                var v = Version;
                return Product + " v" + v.Substring(0, v.IndexOf('.') >= 0 ? v.LastIndexOf('.') : v.Length);
            }
        }

        /// <summary>Полная строка для блока «О плагине», например «JTOOLS v1.0.0 (сборка 21.09.2026 11:14, коммит abc1234)».</summary>
        public static string Display
        {
            get
            {
                var s = Product + " v" + Version;
                if (BuildDate != DateTime.MinValue) s += " (сборка " + BuildDate.ToString("dd.MM.yyyy HH:mm") + ")";
                if (!string.IsNullOrEmpty(Revision)) s += "  коммит " + Revision;
                return s;
            }
        }
    }
}