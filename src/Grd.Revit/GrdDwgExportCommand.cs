using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using GrdRevit.Ui;

namespace GrdRevit
{
    /// <summary>
    /// Открывает окно «Экспорт в DWG»: список листов либо видов документа с галочками,
    /// сохранение отмеченных элементов в один DWG-файл (каждый элемент — отдельный layout).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdDwgExportCommand : IExternalCommand
    {
        private static SheetDwgExportWindow _window;

        /// <summary>Открывает (или активирует уже открытое) окно «Экспорт в DWG».</summary>
        public static void ShowWindow()
        {
            if (_window == null || !_window.IsVisible)
            {
                var window = new SheetDwgExportWindow();
                window.Closed += (s, e) => _window = null;

                try
                {
                    var owner = MainWindow.Instance;
                    if (owner != null && owner.IsVisible)
                    {
                        window.Owner = owner;
                        GrdLog.Log("DWG0b: owner = главное окно плагина");
                    }
                }
                catch { }

                _window = window;
                window.Show();
                GrdLog.Log("DWG1: окно показано");
            }
            else
            {
                WindowRestore.Activate(_window);
                GrdLog.Log("DWG1b: окно активировано");
            }
        }

        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            var sw = Stopwatch.StartNew();
            GrdLog.Log("DWG0: Execute start");
            try
            {
                RevitContext.Initialize(commandData.Application);

                ShowWindow();

                GrdLog.Log("DWG2: succeeded in " + sw.ElapsedMilliseconds + " ms");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("DWG99: EXCEPTION: " + ex);
                try
                {
                    System.Windows.MessageBox.Show(ex.Message, "JTOOLS: экспорт в DWG",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
                catch { }
                return Result.Failed;
            }
        }
    }
}