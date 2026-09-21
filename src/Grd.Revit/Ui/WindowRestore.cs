using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GrdRevit.Ui
{
    /// <summary>Восстановление окна плагина из свёрнутого состояния и подъём вперёд.
    /// WPF-Window.Activate() свёрнутое окно не разворачивает, поэтому дополняется
    /// Win32-вызовами. Используется командами ленты: повторный вызов по сочетанию
    /// клавиш (или кнопке) возвращает уже открытое окно вместо создания нового.</summary>
    internal static class WindowRestore
    {
        public static void Activate(Window window)
        {
            if (window == null) return;

            try
            {
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
            }
            catch { }

            try { window.Activate(); } catch { }

            try
            {
                var h = new WindowInteropHelper(window).Handle;
                if (h != IntPtr.Zero)
                {
                    if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                    SetForegroundWindow(h);
                }
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int SW_RESTORE = 9;
    }
}