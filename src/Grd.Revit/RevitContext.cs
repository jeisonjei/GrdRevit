using System;
using Autodesk.Revit.UI;

namespace GrdRevit
{
    /// <summary>Статический контекст: активное приложение и настройки плагина.</summary>
    public static class RevitContext
    {
        public static UIApplication UiApp;
        public static Autodesk.Revit.DB.Document Doc => UiApp?.ActiveUIDocument?.Document;

        /// <summary>
        /// Главное окно Revit. Для владения окнами плагина: берём из API
        /// (UIApplication.MainWindowHandle), не из Process.MainWindowHandle —
        /// последний может вернуть ноль или служебное окно, и тогда при закрытии
        /// окна плагина Windows «сворачивает» Revit в панель задач.
        /// </summary>
        public static IntPtr MainWindowHandle { get; private set; }

        public static string SettingsPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return System.IO.Path.Combine(appData, "GrdRevit", "settings.json");
        }

        /// <summary>Файл снимка рабочего состояния (загруженные приборы, клапаны, правки).</summary>
        public static string DataPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return System.IO.Path.Combine(appData, "GrdRevit", "data.json");
        }

        public static Core.GrdSettings Settings = new Core.GrdSettings();

        /// <summary>Маршалит применение типа прибора в API-контекст (кнопка моделесс-окна его не может).</summary>
        public static Revit.ApplyRequestHandler ApplyHandler;
        private static Autodesk.Revit.UI.ExternalEvent _applyEvent;
        public static Autodesk.Revit.UI.ExternalEvent ApplyEvent => _applyEvent;

        /// <summary>Маршалит чтение типа выделенного элемента для автозаполнения карты кода в настройках.</summary>
        public static Revit.PickTypeHandler PickHandler;
        private static Autodesk.Revit.UI.ExternalEvent _pickEvent;
        public static Autodesk.Revit.UI.ExternalEvent PickEvent => _pickEvent;

        /// <summary>Маршалит чтение типовых параметров для колонки «Код» (подстановка значений).</summary>
        public static Revit.TypeParamsHandler TypeParamsHandler;
        private static Autodesk.Revit.UI.ExternalEvent _typeParamsEvent;
        public static Autodesk.Revit.UI.ExternalEvent TypeParamsEvent => _typeParamsEvent;

        /// <summary>Маршалит чтение семейств и общих параметров для окна «Параметры семейств».</summary>
        public static Revit.FamilyInfoHandler FamilyInfoHandler;
        private static Autodesk.Revit.UI.ExternalEvent _familyInfoEvent;
        public static Autodesk.Revit.UI.ExternalEvent FamilyInfoEvent => _familyInfoEvent;

        /// <summary>Маршалит применение операций с параметрами к семействам.</summary>
        public static Revit.FamilyParamsHandler FamilyParamsHandler;
        private static Autodesk.Revit.UI.ExternalEvent _familyParamsEvent;
        public static Autodesk.Revit.UI.ExternalEvent FamilyParamsEvent => _familyParamsEvent;

        /// <summary>
        /// Создаёт ExternalEvent-обработчики. Допустимо ТОЛЬКО в контексте стандартного
        /// API-выполнения (внешняя команда/событие/старт) — из кнопки моделесс-окна
        /// ExternalEvent.Create() бросает "outside of a standard API execution".
        /// </summary>
        private static void EnsureHandlers()
        {
            if (_applyEvent == null)
            {
                ApplyHandler = new Revit.ApplyRequestHandler();
                _applyEvent = Autodesk.Revit.UI.ExternalEvent.Create(ApplyHandler);
                GrdLog.Log("EnsureHandlers: ApplyEvent created");
            }
            if (_pickEvent == null)
            {
                PickHandler = new Revit.PickTypeHandler();
                _pickEvent = Autodesk.Revit.UI.ExternalEvent.Create(PickHandler);
                GrdLog.Log("EnsureHandlers: PickEvent created");
            }
            if (_typeParamsEvent == null)
            {
                TypeParamsHandler = new Revit.TypeParamsHandler();
                _typeParamsEvent = Autodesk.Revit.UI.ExternalEvent.Create(TypeParamsHandler);
                GrdLog.Log("EnsureHandlers: TypeParamsEvent created");
            }
            if (_familyInfoEvent == null)
            {
                FamilyInfoHandler = new Revit.FamilyInfoHandler();
                _familyInfoEvent = Autodesk.Revit.UI.ExternalEvent.Create(FamilyInfoHandler);
                GrdLog.Log("EnsureHandlers: FamilyInfoEvent created");
            }
            if (_familyParamsEvent == null)
            {
                FamilyParamsHandler = new Revit.FamilyParamsHandler();
                _familyParamsEvent = Autodesk.Revit.UI.ExternalEvent.Create(FamilyParamsHandler);
                GrdLog.Log("EnsureHandlers: FamilyParamsEvent created");
            }
        }

        public static void Initialize(UIApplication app)
        {
            GrdLog.Log("Init:0 enter, app = " + (app != null ? "notnull" : "NULL"));
            try
            {
                UiApp = app;
                try
                {
                    MainWindowHandle = app != null ? app.MainWindowHandle : IntPtr.Zero;
                }
                catch
                {
                    MainWindowHandle = IntPtr.Zero;
                }
                if (MainWindowHandle == IntPtr.Zero)
                {
                    try { MainWindowHandle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; }
                    catch { MainWindowHandle = IntPtr.Zero; }
                }
                GrdLog.Log("Init:1a MainWindowHandle=0x" + MainWindowHandle.ToString("X"));
                GrdLog.Log("Init:1 UiApp set; InstanceParams.Count=" + (Settings?.InstanceParams?.Count ?? -1) +
                           ", LastGrdPath.Length=" + (Settings?.LastGrdPath?.Length ?? -1));
                if (Settings == null || Settings.InstanceParams.Count == 0 && Settings.LastGrdPath.Length == 0)
                {
                    GrdLog.Log("Init:2 loading settings from " + SettingsPath());
                    Settings = Core.GrdSettings.Load(SettingsPath());
                    GrdLog.Log("Init:3 settings loaded");
                }
                EnsureHandlers();
                GrdLog.Log("Init:9 complete");
            }
            catch (Exception ex)
            {
                GrdLog.Log("Init:EXC " + ex);
                throw;
            }
        }

        public static void SaveSettings()
        {
            Settings.Save(SettingsPath());
        }
    }
}