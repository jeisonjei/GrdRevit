using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using GrdRevit.Ui;

namespace GrdRevit
{
    /// <summary>
    /// Открывает окно «Печать в PDF»: список всех листов документа с галочками и
    /// сохранение отмеченных листов в один PDF-файл с автоматическим размером страницы.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdPdfPrintCommand : IExternalCommand
    {
        private static SheetPdfPrintWindow _window;

        /// <summary>Открывает (или активирует уже открытое) окно «Печать в PDF».
        /// Используется и кнопкой ленты, и кнопкой из окна «Снимок спецификаций».</summary>
        public static void ShowWindow()
        {
            if (_window == null || !_window.IsVisible)
            {
                var window = new SheetPdfPrintWindow();
                window.Closed += (s, e) => _window = null;

                try
                {
                    var owner = MainWindow.Instance;
                    if (owner != null && owner.IsVisible)
                    {
                        window.Owner = owner;
                        GrdLog.Log("PDF0b: owner = главное окно плагина");
                    }
                }
                catch { }

                _window = window;
                window.Show();
                GrdLog.Log("PDF1: окно показано");
            }
            else
            {
                WindowRestore.Activate(_window);
                GrdLog.Log("PDF1b: окно активировано");
            }
        }

        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            var sw = Stopwatch.StartNew();
            GrdLog.Log("PDF0: Execute start");
            try
            {
                RevitContext.Initialize(commandData.Application);

                ShowWindow();

                GrdLog.Log("PDF2: succeeded in " + sw.ElapsedMilliseconds + " ms");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("PDF99: EXCEPTION: " + ex);
                try
                {
                    System.Windows.MessageBox.Show(ex.Message, "JTOOLS: печать в PDF",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
                catch { }
                return Result.Failed;
            }
        }
    }
}
