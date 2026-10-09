using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace GrdRevit
{
    /// <summary>
    /// «Выбрать изоляцию»: режим, в котором выбирается только изоляция
    /// (трубы, воздуховоды, фитинги) и ничто другое.
    ///
    /// Проблема Revit: изоляцию на трубах/воздуховодах нельзя выделить напрямую —
    /// нужно сначала выделить трубу, затем нажать «Редактировать изоляцию».
    /// Эта команда обходит ограничение: пользователь щёлкает по элементу или
    /// выделяет несколько рамкой, плагин находит связанную с ними изоляцию
    /// (InsulationLiningBase.HostElementId) и подставляет в текущее выделение
    /// именно изоляцию. Прямое попадание в изоляцию (если Revit её вернул) тоже
    /// принимается. Фильтр выбора пропускает только изоляцию и хостов, у которых
    /// изоляция реально есть, поэтому рамка не захватывает прочие элементы.
    ///
    /// Сеанс выбора один: «Готово»/Enter подставляет изоляцию в активное выделение
    /// (дальше доступны «Редактировать изоляцию», смена типа и прочие команды),
    /// Esc — отмена без изменений.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdSelectInsulationCommand : IExternalCommand
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
                    TaskDialog.Show("JTOOLS: Выбрать изоляцию", "Откройте документ Revit.");
                    return Result.Cancelled;
                }
                if (doc.IsFamilyDocument)
                {
                    TaskDialog.Show("JTOOLS: Выбрать изоляцию",
                        "Команда доступна только в проектном документе (RVT).");
                    return Result.Cancelled;
                }

                var allInsulation = CollectInsulation(doc);
                if (allInsulation.Count == 0)
                {
                    TaskDialog.Show("JTOOLS: Выбрать изоляцию",
                        "В документе нет изоляции труб или воздуховодов.");
                    return Result.Cancelled;
                }

                // Хосты с изоляцией и id самой изоляции — фильтр отсечёт всё остальное,
                // поэтому рамкой выделяются только трубы/воздуховоды/фитинги с изоляцией.
                var byHost = new Dictionary<ElementId, List<ElementId>>();
                var hostIds = new HashSet<long>();
                var insulationIds = new HashSet<long>();
                foreach (var ins in allInsulation)
                {
                    insulationIds.Add(ins.Id.Value);
                    ElementId host = null;
                    try { host = ins.HostElementId; } catch { }
                    if (host == null || host == ElementId.InvalidElementId) continue;
                    hostIds.Add(host.Value);
                    if (!byHost.TryGetValue(host, out var list))
                    {
                        list = new List<ElementId>();
                        byHost[host] = list;
                    }
                    if (!list.Contains(ins.Id)) list.Add(ins.Id);
                }

                var filter = new InsulatedElementFilter(hostIds, insulationIds);

                // Один сеанс выбора: «Готово»/Enter завершает его сам Revit.
                // Возврат из PickObjects очищает текущее выделение, поэтому подменять
                // его изоляцией можно только строго после выхода из PickObjects —
                // иначе следующий вызов сеанса сбросит выделение (изоляция пропадёт).
                IList<Reference> refs;
                try
                {
                    refs = uidoc.Selection.PickObjects(
                        ObjectType.Element,
                        filter,
                        BuildStatusText(insulationIds.Count));
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    GrdLog.Log("GrdSelectInsulationCommand: выбор отменён (Esc)");
                    return Result.Cancelled;
                }

                var chosen = new List<ElementId>();
                var chosenSet = new HashSet<ElementId>();
                int missCount = 0;
                int resolved = 0;

                if (refs != null)
                {
                    foreach (var reference in refs)
                    {
                        if (reference == null) continue;
                        Element picked = null;
                        try { picked = doc.GetElement(reference.ElementId); } catch { }
                        if (picked == null) continue;

                        if (picked is InsulationLiningBase direct)
                        {
                            Add(chosen, chosenSet, direct.Id);
                            resolved++;
                            continue;
                        }

                        if (byHost.TryGetValue(picked.Id, out var insIds) && insIds.Count > 0)
                        {
                            foreach (var id in insIds) Add(chosen, chosenSet, id);
                            resolved++;
                        }
                        else
                        {
                            missCount++;
                        }
                    }
                }

                if (chosen.Count == 0)
                {
                    try { System.Media.SystemSounds.Beep.Play(); } catch { }
                    GrdLog.Log("GrdSelectInsulationCommand: изоляция не выбрана, " +
                               "изоляции в документе=" + allInsulation.Count);
                    return Result.Cancelled;
                }

                // Изоляция остаётся в активном выборе: доступны «Редактировать изоляцию»,
                // смена типа и прочие команды изоляции.
                ApplySelection(uidoc, chosen);

                GrdLog.Log("GrdSelectInsulationCommand: изоляции в документе=" + allInsulation.Count +
                           ", выбрано=" + chosen.Count + ", без изоляции=" + missCount +
                           ", разрешено=" + resolved);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("GrdSelectInsulationCommand: EXCEPTION " + ex);
                try { TaskDialog.Show("JTOOLS: Выбрать изоляцию", "Ошибка: " + ex.Message); }
                catch { }
                return Result.Failed;
            }
        }

        private static string BuildStatusText(int docInsulationCount)
        {
            return "JTOOLS: выбор изоляции — щёлкните трубу/воздуховод с изоляцией " +
                   "или выделите несколько рамкой (выделятся только элементы с изоляцией). " +
                   "Готово/Enter — выделить изоляцию, Esc — отмена. Изоляции в документе: " +
                   docInsulationCount + ".";
        }

        private static void Add(List<ElementId> chosen, HashSet<ElementId> set, ElementId id)
        {
            if (set.Add(id)) chosen.Add(id);
        }

        private static void ApplySelection(UIDocument uidoc, List<ElementId> chosen)
        {
            try { uidoc.Selection.SetElementIds(chosen); } catch { }
        }

        /// <summary>Вся изоляция документа (трубы и воздуховоды).</summary>
        private static List<InsulationLiningBase> CollectInsulation(Document doc)
        {
            var result = new List<InsulationLiningBase>();
            try
            {
                foreach (var e in new FilteredElementCollector(doc)
                             .OfClass(typeof(PipeInsulation))
                             .WhereElementIsNotElementType())
                {
                    if (e is InsulationLiningBase ilb) result.Add(ilb);
                }
            }
            catch (Exception ex) { GrdLog.Log("GrdSelectInsulationCommand: PipeInsulation: " + ex.Message); }
            try
            {
                foreach (var e in new FilteredElementCollector(doc)
                             .OfClass(typeof(DuctInsulation))
                             .WhereElementIsNotElementType())
                {
                    if (e is InsulationLiningBase ilb) result.Add(ilb);
                }
            }
            catch (Exception ex) { GrdLog.Log("GrdSelectInsulationCommand: DuctInsulation: " + ex.Message); }
            return result;
        }

        /// <summary>Пропускает только изоляцию и хост-элементы, у которых изоляция
        /// действительно есть. Без проверки по <c>HostElementId</c> рамка выделяла бы
        /// вообще все элементы модели. Аннотации, оси, уровни и т.п. отсекаются.</summary>
        private class InsulatedElementFilter : ISelectionFilter
        {
            private readonly HashSet<long> _hostIds;
            private readonly HashSet<long> _insulationIds;

            public InsulatedElementFilter(HashSet<long> hostIds, HashSet<long> insulationIds)
            {
                _hostIds = hostIds ?? new HashSet<long>();
                _insulationIds = insulationIds ?? new HashSet<long>();
            }

            public bool AllowElement(Element elem)
            {
                if (elem == null) return false;
                if (_insulationIds.Contains(elem.Id.Value)) return true;
                var cat = elem.Category;
                if (cat == null) return false;
                if (cat.CategoryType != CategoryType.Model) return false;
                if (elem is Level || elem is Grid || elem is ReferencePlane) return false;
                return _hostIds.Contains(elem.Id.Value);
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return true;
            }
        }
    }
}