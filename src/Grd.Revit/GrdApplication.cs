using System;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace GrdRevit
{
    public class GrdApplication : IExternalApplication
    {
        public const string TabName = "ГРД";
        public const string PanelName = "Бакунинская 77";

        private const string ButtonDataName = "GrdRevit.Open";
        private const string ButtonText = "ГрД";

        public Result OnStartup(UIControlledApplication app)
        {
            GrdLog.Log("OnStartup: begin");
            try
            {
                app.CreateRibbonTab(TabName);

                var panel = app.CreateRibbonPanel(TabName, PanelName);

                var data = new PushButtonData(
                    ButtonDataName,
                    "Отопительные\nприборы СО",
                    Assembly.GetExecutingAssembly().Location,
                    typeof(GrdLoaderCommand).FullName)
                {
                    ToolTip = "Загрузить .grd и применить тип прибора к выбранному оборудованию",
                    LongDescription = "Парсит файл .grd (PWF, UTF-16), показывает отопительные приборы по типам. " +
                                      "При выбранном единичном механическом оборудовании кнопки строк активируются — " +
                                      "нажатие применяет тип семейства и параметры экземпляра из настроек плагина."
                };

                data.AvailabilityClassName = typeof(GrdAvailability).FullName;

                panel.AddItem(data);
                GrdLog.Log("OnStartup: done");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("OnStartup: EXCEPTION: " + ex);
                TaskDialog.Show("ГрД: ошибка запуска", ex.Message);
                return Result.Failed;
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