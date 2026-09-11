using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GrdRevit.Ui;

namespace GrdRevit
{
    /// <summary>Кнопка ленты: открывает единое окно настроек всех функций плагина.</summary>
    [Transaction(TransactionMode.Manual)]
    public class GrdSettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                RevitContext.Initialize(commandData.Application);
                GrdLog.Log("GrdSettingsCommand: открытие настроек");
                SettingsWindow.ShowShared(RevitContext.MainWindowHandle);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("GrdSettingsCommand: EXCEPTION " + ex);
                try
                {
                    System.Windows.MessageBox.Show(ex.Message, "JTOOLS: настройки",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
                catch { }
                return Result.Failed;
            }
        }
    }
}