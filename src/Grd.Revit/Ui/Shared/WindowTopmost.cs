using System.Collections.Generic;
using System.Windows;

namespace GrdRevit.Ui
{
    /// <summary>
    /// Следит за открытыми немодальными окнами плагина: применяет настройку
    /// «поверх всех окон» (GrdSettings.WindowsTopmost) при показе окна и при
    /// её изменении в окне настроек.
    /// </summary>
    internal static class WindowTopmost
    {
        private static readonly object Lock = new object();
        private static readonly List<Window> Windows = new List<Window>();

        /// <summary>
        /// Регистрирует окно (вызывать в конструкторе): сразу применяет текущую
        /// настройку и подхватывает последующие изменения, пока окно открыто.
        /// </summary>
        public static void Track(Window window)
        {
            if (window == null) return;
            window.Closed += (s, e) =>
            {
                lock (Lock) Windows.Remove(window);
            };
            lock (Lock) Windows.Add(window);
            ApplyTo(window);
        }

        /// <summary>Применяет текущую настройку к конкретному окну.</summary>
        public static void ApplyTo(Window window)
        {
            if (window == null) return;
            window.Topmost = RevitContext.Settings.WindowsTopmost;
        }

        /// <summary>Применяет текущую настройку ко всем открытым окнам (после её изменения в настройках).</summary>
        public static void ApplyAll()
        {
            lock (Lock)
            {
                foreach (var w in Windows) ApplyTo(w);
            }
        }
    }
}