using System;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GrdRevit.Ui;

namespace GrdRevit
{
    /// <summary>
    /// «Типы систем»: редактирование типов трубных и воздуховодных систем —
    /// имя, сокращение, цвет, толщина и тип линий, легенда, экспорт в Excel.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdSystemTypesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                RevitContext.Initialize(commandData.Application);

                var uidoc = commandData.Application.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (uidoc == null || doc == null)
                {
                    TaskDialog.Show("JTOOLS: Типы систем", "Откройте документ Revit.");
                    return Result.Cancelled;
                }
                if (doc.IsFamilyDocument)
                {
                    TaskDialog.Show("JTOOLS: Типы систем",
                        "Команда доступна только в проектном документе (RVT).");
                    return Result.Cancelled;
                }

                var window = new SystemTypesWindow(doc);
                try
                {
                    new WindowInteropHelper(window) { Owner = RevitContext.MainWindowHandle };
                }
                catch (Exception ex)
                {
                    GrdLog.Log("GrdSystemTypesCommand: Owner EXCEPTION " + ex.Message);
                }

                GrdLog.Log("GrdSystemTypesCommand: showing window, doc=" + doc.Title);
                window.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("GrdSystemTypesCommand: EXCEPTION " + ex);
                try { TaskDialog.Show("JTOOLS: Типы систем", "Ошибка: " + ex.Message); }
                catch { }
                return Result.Failed;
            }
        }
    }
}