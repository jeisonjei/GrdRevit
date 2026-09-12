using System;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace GrdRevit
{
    public class GrdApplication : IExternalApplication
    {
        public const string ProductName = "JTOOLS";
        // Вкладка и панель ленты: короткие подписи; полное название — на кнопке и в окне.
        public const string TabName = "JTOOLS";
        public const string PanelName = "Данные";

        private const string ButtonDataName = "GrdRevit.Open";
        private const string FamilyParamsButtonName = "GrdRevit.FamilyParams";
        private const string Box3DButtonName = "GrdRevit.Box3D";
        private const string InstanceParamsButtonName = "GrdRevit.InstanceParams";
        private const string SettingsButtonName = "GrdRevit.Settings";
        private const string ViewsManagerButtonName = "GrdRevit.ViewsManager";

        public Result OnStartup(UIControlledApplication app)
        {
            GrdLog.LogSessionStart();
            GrdLog.Log("OnStartup: begin");
            try
            {
                app.CreateRibbonTab(TabName);

                var panel = app.CreateRibbonPanel(TabName, PanelName);

                var data = new PushButtonData(
                    ButtonDataName,
                    "Применить данные\nиз Audytor",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdLoaderCommand).FullName)
                {
                    ToolTip = "Заполнить таблицу приборов (room-radiator.txt), сопоставить настройки клапанов и применить тип к выделенному оборудованию",
                    LongDescription = "Загружает помещения и радиаторы из room-radiator.txt, настройки клапанов из valve-settings.txt, " +
                                      "заполняет колонку «Настройка клапана» по ключу «помещение + мощность». " +
                                      "При выбранном единичном механическом оборудовании кнопка «Применить» в строке " +
                                      "применяет тип семейства и параметры экземпляра из настроек плагина."
                };

                data.Image = LoadIcon("GrdRevit.Resources.plugin16.png");
                data.LargeImage = LoadIcon("GrdRevit.Resources.plugin32.png");
                data.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(data);

                var familyData = new PushButtonData(
                    FamilyParamsButtonName,
                    "Параметры\nсемейств",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdFamilyParamsCommand).FullName)
                {
                    ToolTip = "Добавить, удалить или изменить привязку (экземпляр/тип) параметров сразу у нескольких семейств активного документа",
                    LongDescription = "Открывает окно: выберите семейства, добавьте операции «добавить/удалить/изменить привязку» для обычных или общих параметров, " +
                                      "укажите тип значения и группу, затем нажмите «Применить». " +
                                      "Операция «Изменить привязку» переключает существующий параметр между экземпляром и типом. " +
                                      "Файл общих параметров запоминается и восстанавливается при следующем запуске."
                };
                familyData.Image = LoadIcon("GrdRevit.Resources.params16.png");
                familyData.LargeImage = LoadIcon("GrdRevit.Resources.params32.png");
                familyData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(familyData);

                var box3dData = new PushButtonData(
                    Box3DButtonName,
                    "3D-фрагмент\nиз области",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdBox3DCommand).FullName)
                {
                    ToolTip = "Создать 3D-вид-фрагмент по растянутой на плане области с высотой по умолчанию из настроек",
                    LongDescription = "Нажмите кнопку, затем растяните прямоугольную область мышью на плане этажа. " +
                                      "Плагин создаст изометрический 3D-вид с границами точно по этой области. " +
                                      "Высота объёма берётся из настроек плагина (по умолчанию 3 м) — " +
                                      "изменить можно в отдельной кнопке «Настройки»."
                };
                box3dData.Image = LoadIcon("GrdRevit.Resources.box3d16.png");
                box3dData.LargeImage = LoadIcon("GrdRevit.Resources.box3d32.png");
                box3dData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(box3dData);

                var instanceData = new PushButtonData(
                    InstanceParamsButtonName,
                    "Параметры\nэкземпляра",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdInstanceParamsCommand).FullName)
                {
                    ToolTip = "Изменить значения всех параметров выбранного экземпляра семейства без открытия редактора семейства",
                    LongDescription = "Выделите один экземпляр семейства в проекте и нажмите кнопку. " +
                                      "Откроется окно со всеми параметрами элемента и типа: параметры экземпляра " +
                                      "изменяются только у выбранного элемента, параметры типа — у типа, то есть " +
                                      "у всех экземпляров этого типа. Значения редактируются прямо в таблице."
                };
                instanceData.Image = LoadIcon("GrdRevit.Resources.params16.png");
                instanceData.LargeImage = LoadIcon("GrdRevit.Resources.params32.png");
                instanceData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(instanceData);

                var viewsData = new PushButtonData(
                    ViewsManagerButtonName,
                    "Управляющий\nвидами",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdViewsManagerCommand).FullName)
                {
                    ToolTip = "Список всех видов документа с поиском и фильтрами: переименовать, дублировать, перейти к виду",
                    LongDescription = "Открывает окно со всеми видами активного документа. " +
                                      "Фильтры «Разрезы / Планы / 3D» и поиск по имени. " +
                                      "Переименование — двойной щелчок или F2 по имени вида, " +
                                      "кнопка «Копия» дублирует вид и сразу открывает его имя, " +
                                      "кнопка «Открыть» переключает Revit на этот вид."
                };
                viewsData.Image = LoadIcon("GrdRevit.Resources.views16.png");
                viewsData.LargeImage = LoadIcon("GrdRevit.Resources.views32.png");
                viewsData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(viewsData);

                var settingsData = new PushButtonData(
                    SettingsButtonName,
                    "Настройки",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdSettingsCommand).FullName)
                {
                    ToolTip = "Настройки всех функций плагина JTOOLS",
                    LongDescription = "Открывает единое окно настроек: карта значений «прибор -> параметр экземпляра», " +
                                      "сопоставление кода прибора с именем типа семейства и высота 3D-вида-фрагмента по умолчанию."
                };
                settingsData.Image = LoadIcon("GrdRevit.Resources.settings16.png");
                settingsData.LargeImage = LoadIcon("GrdRevit.Resources.settings32.png");
                settingsData.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(settingsData);

                GrdLog.Log("OnStartup: done");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("OnStartup: EXCEPTION: " + ex);
                TaskDialog.Show(ProductName + ": ошибка запуска", ex.Message);
                return Result.Failed;
            }
        }

        /// <summary>Значок кнопки из встроенного ресурса сборки (PNG).</summary>
        private static ImageSource LoadIcon(string resourceName)
        {
            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        GrdLog.Log("LoadIcon: не найден ресурс " + resourceName);
                        return null;
                    }
                    var decoder = new PngBitmapDecoder(stream,
                        BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    return decoder.Frames[0];
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("LoadIcon: EXCEPTION " + ex);
                return null;
            }
        }

        public Result OnShutdown(UIControlledApplication app)
        {
            return Result.Succeeded;
        }
    }

    public class GrdAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, Autodesk.Revit.DB.CategorySet selectedCategories)
        {
            return true;
        }
    }
}