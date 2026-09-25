using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using GrdRevit.Ui;

namespace GrdRevit
{
    /// <summary>
    /// Открывает окно «Снимок спецификаций»: слева листы со спецификациями (с поиском),
    /// справа — редактируемая таблица-снимок выбранной спецификации с возможностью
    /// вернуть её к виду из модели («Обновить из модели») и печатью в PDF.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdScheduleSnapshotCommand : IExternalCommand
    {
        private static ScheduleSnapshotWindow _window;

        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            var sw = Stopwatch.StartNew();
            GrdLog.Log("SS0: Execute start");
            try
            {
                RevitContext.Initialize(commandData.Application);

                if (_window == null || !_window.IsVisible)
                {
                    var window = new ScheduleSnapshotWindow();
                    window.Closed += (s, e) => _window = null;

                    try
                    {
                        var owner = MainWindow.Instance;
                        if (owner != null && owner.IsVisible)
                        {
                            window.Owner = owner;
                            GrdLog.Log("SS0b: owner = главное окно плагина");
                        }
                    }
                    catch { }

                    _window = window;
                    window.Show();
                    GrdLog.Log("SS1: окно показано");
                }
                else
                {
                    WindowRestore.Activate(_window);
                    GrdLog.Log("SS1b: окно активировано");
                }

                GrdLog.Log("SS2: succeeded in " + sw.ElapsedMilliseconds + " ms");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("SS99: EXCEPTION: " + ex);
                try
                {
                    System.Windows.MessageBox.Show(ex.Message, "JTOOLS: снимок спецификаций",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
                catch { }
                return Result.Failed;
            }
        }
    }
}