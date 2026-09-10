using System;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace GrdRevit
{
    public class GrdApplication : IExternalApplication
    {
        public const string ProductName = "Применить данные из Audytor";
        // Вкладка и панель ленты: короткие подписи; полное название — на кнопке и в окне.
        public const string TabName = "Audytor";
        public const string PanelName = "Данные";

        private const string ButtonDataName = "GrdRevit.Open";
        private const string FamilyParamsButtonName = "GrdRevit.FamilyParams";

        public Result OnStartup(UIControlledApplication app)
        {
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