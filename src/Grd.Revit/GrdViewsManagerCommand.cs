using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using GrdRevit.Ui;

namespace GrdRevit
{
    /// <summary>
    /// Открывает окно «Управляющий видами»: список всех видов активного документа
    /// с фильтрами (разрезы/планы/3D), поиском, переименованием (F2), дублированием
    /// и кнопкой перехода к виду.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdViewsManagerCommand : IExternalCommand
    {
        private static ViewsManagerWindow _window;

        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            var sw = Stopwatch.StartNew();
            GrdLog.Log("VM0: Execute start");
            try
            {
                RevitContext.Initialize(commandData.Application);

                var uidoc = commandData.Application.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (uidoc == null || doc == null)
                {
                    TaskDialog.Show("JTOOLS: управляющий видами", "Откройте документ Revit.");
                    return Result.Cancelled;
                }

                if (_window == null || !_window.IsVisible)
                {
                    var window = new ViewsManagerWindow();
                    window.Closed += (s, e) => _window = null;

                    try
                    {
                        var owner = MainWindow.Instance;
                        if (owner != null && owner.IsVisible)
                        {
                            window.Owner = owner;
                            GrdLog.Log("VM0b: owner = главное окно плагина");
                        }
                    }
                    catch { }

                    _window = window;
                    window.Show();
                    GrdLog.Log("VM1: окно показано");
                }
                else
                {
                    _window.Activate();
                    GrdLog.Log("VM1b: окно активировано");
                }

                GrdLog.Log("VM2: succeeded in " + sw.ElapsedMilliseconds + " ms");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("VM99: EXCEPTION: " + ex);
                try
                {
                    TaskDialog.Show("JTOOLS: управляющий видами", "Ошибка: " + ex.Message);
                }
                catch { }
                return Result.Failed;
            }
        }
    }
}