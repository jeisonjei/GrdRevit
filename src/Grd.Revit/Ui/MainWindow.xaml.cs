using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GrdRevit.Core;
using GrdRevit.Revit;

namespace GrdRevit.Ui
{
    public partial class MainWindow : Window
    {
        public static MainWindow Instance;
        private SelectionWatcher _watcher;
        private MainViewModel _vm;

        public MainWindow()
        {
            GrdLog.Log("MainWindow: ctor start");
            InitializeComponent();
            _vm = new MainViewModel();
            _vm.FileDialogOwner = this;
            DataContext = _vm;

            var doc = LoadSavedOrDefault();
            if (doc != null)
                _vm.SetDocument(doc);

            try
            {
                var uiApp = RevitContext.UiApp;
                if (uiApp != null)
                {
                    _watcher = new SelectionWatcher(uiApp);
                    _watcher.Subscribe(OnSelectionChanged);
                    GrdLog.Log("MainWindow: SelectionWatcher subscribed");
                }
            }
            catch { }
            GrdLog.Log("MainWindow: ctor done");
        }

        /// <summary>
        /// Файл-диалог при первом запуске без сохранённого пути показываем
        /// синхронно, ПОСЛЕ того как окно создано и привязано к главному окну
        /// Revit. Делать это через Dispatcher/после Execute нельзя — модальный
        /// диалог вне контекста вызова Revit приводит к "непоправимой ошибке".
        /// </summary>
        private GrdDocument LoadSavedOrDefault()
        {
            try
            {
                var path = RevitContext.Settings.LastGrdPath;
                if (System.IO.File.Exists(path)) return GrdParser.Parse(path);
            }
            catch { }
            return null;
        }

        private void OnSelectionChanged(bool ok, Autodesk.Revit.DB.ElementId id)
        {
            // Если окно уже закрыто, диспетчер может не принимать BeginInvoke —
            // это не должно вылетать наружу.
            try
            {
                if (!Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(new Action(() => _vm.SelectionOk = ok));
            }
            catch { }
        }

        private void OnLoadingRow(object sender, DataGridRowEventArgs e)
        {
            if (e.Row.DataContext is DeviceRow row)
                row.IsApplyEnabled = _vm.SelectionOk;
        }

        protected override void OnClosed(EventArgs e)
        {
            GrdLog.Log("MainWindow: OnClosed");
            // Закрытие окна не должно ронять Revit: отписка от SelectionChanged
            // может упасть, если в этот момент документ/приложение завершается.
            try { _watcher?.Dispose(); } catch { }
            _watcher = null;
            base.OnClosed(e);

            // Закрытие WPF-окна иногда «оставляет» Revit свёрнутым в панели задач:
            // Windows отдаёт активацию служебному окну вместо главного окна Revit.
            // Принудительно восстанавливаем и поднимаем главное окно Revit.
            var h = RevitContext.MainWindowHandle;
            if (h == IntPtr.Zero) return;
            try
            {
                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                SetForegroundWindow(h);
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

    public sealed class BoolToHintConverter : IValueConverter
    {
        public static readonly BoolToHintConverter Instance = new BoolToHintConverter();

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value is bool b && b
                ? "Выбрано одно механическое оборудование — кнопки строк активны."
                : "Выберите ОДНО механическое оборудование, чтобы активировать кнопки строк.";
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}