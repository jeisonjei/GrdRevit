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
                if (selected.Count == 0)
                {
                    TaskDialog.Show("JTOOLS: параметры экземпляра",
                        "Выделите один или несколько экземпляров семейств в проекте и повторите команду.\n\n" +
                        "Можно выделить несколько экземпляров РАЗНЫХ типов — значения параметров будут " +
                        "применяться ко всем выбранным элементам.");
                    return Result.Cancelled;
                }

                var selectedElements = new System.Collections.Generic.List<Autodesk.Revit.DB.Element>();
                foreach (var id in selected)
                {
                    var el = doc.GetElement(id);
                    if (el == null)
                    {
                        TaskDialog.Show("JTOOLS: параметры экземпляра", "Один из выбранных элементов не найден в документе.");
                        return Result.Cancelled;
                    }
                    selectedElements.Add(el);
                }
                if (selectedElements.Any(e => !(e is Autodesk.Revit.DB.FamilyInstance)))
                {
                    TaskDialog.Show("JTOOLS: параметры экземпляра",
                        "Все выбранные элементы должны быть экземплярами семейств. Команда работает с экземплярами семейств.");
                    return Result.Cancelled;
                }

                var elementIds = selectedElements.Select(e => e.Id).ToList();

                if (_window == null || !_window.IsVisible)
                {
                    var window = new InstanceParamsWindow(elementIds);
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
                    _window.SetSelection(elementIds);
                    GrdLog.Log("IP1b: окно активировано, выборка обновлена (id=" +
                               string.Join(",", elementIds.Select(x => x.Value)) + ")");
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