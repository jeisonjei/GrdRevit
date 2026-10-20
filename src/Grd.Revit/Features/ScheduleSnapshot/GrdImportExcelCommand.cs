using System;
using System.Linq;
using System.Windows;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using GrdRevit.Ui;

namespace GrdRevit
{
    /// <summary>
    /// Импорт таблицы из Excel: выбор книги и листа, выделение диапазона мышью и создание
    /// реальной спецификации Revit, в шапку которой переносится выбранная таблица
    /// (текст, добавленные строки и объединённые ячейки), размещённой на новом листе.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdImportExcelCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            try
            {
                RevitContext.Initialize(commandData.Application);

                var handler = RevitContext.ScheduleSnapshotHandler;
                var ev = RevitContext.ScheduleSnapshotEvent;
                if (handler == null || ev == null)
                {
                    message = "Обработчик Revit недоступен.";
                    return Result.Failed;
                }

                var dlg = new ImportExcelWindow();
                try { dlg.SetFonts(CollectFontNames(commandData.Application)); } catch (Exception ex) { GrdLog.Log("GrdImportExcelCommand: fonts: " + ex.Message); }
                try
                {
                    var owner = MainWindow.Instance;
                    if (owner != null && owner.IsVisible) dlg.Owner = owner;
                }
                catch { }

                if (dlg.ShowDialog() != true || dlg.Table == null) return Result.Cancelled;

                handler.QueueImportExcel(dlg.Table, dlg.ScheduleName, string.Empty, string.Empty, dlg.FontName, res =>
                {
                    try
                    {
                        Application.Current?.Dispatcher.Invoke(new Action(() =>
                        {
                            if (!string.IsNullOrEmpty(res.Error))
                            {
                                MessageBox.Show(res.Error, "Импорт из Excel",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                                return;
                            }
                            MessageBox.Show("Спецификация «" + dlg.ScheduleName +
                                            "» создана.", "Импорт из Excel",
                                            MessageBoxButton.OK, MessageBoxImage.Information);
                            try
                            {
                                if (res.NewScheduleId != 0)
                                {
                                    var uidoc = RevitContext.UiApp?.ActiveUIDocument;
                                    if (uidoc != null)
                                        uidoc.ActiveView = uidoc.Document.GetElement(new Autodesk.Revit.DB.ElementId(res.NewScheduleId)) as Autodesk.Revit.DB.View;
                                }
                            }
                            catch (Exception ex2) { GrdLog.Log("GrdImportExcelCommand: open view EXCEPTION: " + ex2); }
                        }));
                    }
                    catch (Exception ex) { GrdLog.Log("GrdImportExcelCommand: callback EXCEPTION: " + ex); }
                });
                ev.Raise();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("GrdImportExcelCommand EXCEPTION: " + ex);
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>Имена доступных стилей текста (шрифтов) в проекте для выпадающего списка.</summary>
        private static System.Collections.Generic.List<string> CollectFontNames(UIApplication app)
        {
            var names = new System.Collections.Generic.List<string>();
            try
            {
                var doc = app?.ActiveUIDocument?.Document;
                if (doc == null) return names;
                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var tt in new Autodesk.Revit.DB.FilteredElementCollector(doc)
                            .OfClass(typeof(Autodesk.Revit.DB.TextNoteType))
                            .Cast<Autodesk.Revit.DB.TextNoteType>())
                {
                    var n = tt.Name;
                    if (!string.IsNullOrWhiteSpace(n) && seen.Add(n)) names.Add(n);
                }
                names.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { GrdLog.Log("CollectFontNames: " + ex.Message); }
            return names;
        }
    }
}
