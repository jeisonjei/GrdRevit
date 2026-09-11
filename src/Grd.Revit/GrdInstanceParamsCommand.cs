using System;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using GrdRevit.Ui;

namespace GrdRevit
{
    /// <summary>
    /// Открывает окно «Параметры экземпляра» для выбранного в Revit экземпляра семейства.
    /// Окно позволяет изменить значения всех параметров элемента без открытия редактора
    /// семейства: параметры экземпляра меняются только у выбранного элемента, параметры
    /// типа — у типа (у всех экземпляров).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdInstanceParamsCommand : IExternalCommand
    {
        private static InstanceParamsWindow _window;

        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            var sw = Stopwatch.StartNew();
            GrdLog.Log("IP0: Execute start");
            try
            {
                RevitContext.Initialize(commandData.Application);

                var uidoc = commandData.Application.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (uidoc == null || doc == null)
                {
                    TaskDialog.Show("JTOOLS: параметры экземпляра", "Откройте документ Revit.");
                    return Result.Cancelled;
                }

                var selected = uidoc.Selection.GetElementIds();
                if (selected.Count != 1)
                {
                    TaskDialog.Show("JTOOLS: параметры экземпляра",
                        "Выделите ровно один экземпляр семейства в проекте и повторите команду.");
                    return Result.Cancelled;
                }

                var el = doc.GetElement(selected.First());
                if (el == null)
                {
                    TaskDialog.Show("JTOOLS: параметры экземпляра", "Выбранный элемент не найден.");
                    return Result.Cancelled;
                }
                if (!(el is Autodesk.Revit.DB.FamilyInstance))
                {
                    TaskDialog.Show("JTOOLS: параметры экземпляра",
                        "Выбранный элемент не является экземпляром семейства. Команда работает с экземплярами семейств.");
                    return Result.Cancelled;
                }

                if (_window == null || !_window.IsVisible)
                {
                    var window = new InstanceParamsWindow(el.Id);
                    window.Closed += (s, e) => _window = null;

                    try
                    {
                        var owner = MainWindow.Instance;
                        if (owner != null && owner.IsVisible)
                        {
                            window.Owner = owner;
                            GrdLog.Log("IP0b: owner = главное окно плагина");
                        }
                    }
                    catch { }

                    _window = window;
                    window.Show();
                    GrdLog.Log("IP1: окно показано");
                }
                else
                {
                    _window.Activate();
                    GrdLog.Log("IP1b: окно активировано");
                }

                GrdLog.Log("IP2: succeeded in " + sw.ElapsedMilliseconds + " ms");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("IP99: EXCEPTION: " + ex);
                try
                {
                    System.Windows.MessageBox.Show(ex.Message, "JTOOLS: параметры экземпляра",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
                catch { }
                return Result.Failed;
            }
        }
    }
}