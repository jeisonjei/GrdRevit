using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using GrdRevit.Ui;

namespace GrdRevit
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdCopyScheduleCountLengthCommand : IExternalCommand
    {
        private static CopyScheduleCountLengthWindow _window;

        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            var sw = Stopwatch.StartNew();
            GrdLog.Log("CountLengthCommand: Execute start");
            try
            {
                RevitContext.Initialize(commandData.Application);

                var uidoc = commandData.Application.ActiveUIDocument;
                if (uidoc == null || uidoc.Document == null)
                {
                    TaskDialog.Show("JTOOLS: Count/Length", "Откройте документ Revit.");
                    return Result.Cancelled;
                }

                var handler = RevitContext.CopyScheduleCountLengthHandler;
                var ev = RevitContext.CopyScheduleCountLengthEvent;
                if (handler == null || ev == null)
                {
                    message = "Обработчик недоступен.";
                    return Result.Failed;
                }

                GrdLog.Log("CountLengthCommand: activeView=\"" + (uidoc.ActiveView?.Name ?? "<null>") +
                           "\" type=" + (uidoc.ActiveView?.GetType().Name ?? "<null>"));

                if (_window == null || !_window.IsVisible)
                {
                    var window = new CopyScheduleCountLengthWindow(handler);
                    window.Closed += (s, e) => _window = null;

                    try
                    {
                        var owner = MainWindow.Instance;
                        if (owner != null && owner.IsVisible) window.Owner = owner;
                    }
                    catch { }

                    _window = window;
                    window.Show();
                    GrdLog.Log("CountLengthCommand: окно показано за " + sw.ElapsedMilliseconds + " ms");
                }
                else
                {
                    WindowRestore.Activate(_window);
                    GrdLog.Log("CountLengthCommand: окно активировано");
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("CountLengthCommand: EXCEPTION: " + ex);
                try
                {
                    System.Windows.MessageBox.Show(ex.Message, "JTOOLS: Count/Length",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
                catch { }
                return Result.Failed;
            }
        }
    }
}
