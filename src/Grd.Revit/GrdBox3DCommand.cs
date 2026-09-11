using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.Exceptions;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace GrdRevit
{
    /// <summary>
    /// «3D-фрагмент»: пользователь растягивает прямоугольную область на плане (PickBox),
    /// создаётся изометрический 3D-вид с границами точно по этой области и высотой
    /// по умолчанию из настроек плагина (Default3DBoxHeight, метры).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdBox3DCommand : IExternalCommand
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
                    TaskDialog.Show("JTOOLS: 3D-фрагмент", "Откройте документ и план этажа.");
                    return Result.Cancelled;
                }
                if (doc.IsFamilyDocument)
                {
                    TaskDialog.Show("JTOOLS: 3D-фрагмент",
                        "Команда доступна только в проектном документе (RVT).");
                    return Result.Cancelled;
                }

                var view = uidoc.ActiveGraphicalView as ViewPlan;
                if (view == null)
                {
                    TaskDialog.Show("JTOOLS: 3D-фрагмент",
                        "Откройте план этажа (вид сверху, включая потолок и АР) и повторите команду.");
                    return Result.Cancelled;
                }

                PickedBox box;
                try
                {
                    box = uidoc.Selection.PickBox(PickBoxStyle.Directional);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    GrdLog.Log("GrdBox3DCommand: выделение отменено пользователем");
                    return Result.Cancelled;
                }

                double heightM = RevitContext.Settings.Default3DBoxHeight;
                if (heightM <= 0.0) heightM = 3.0;
                double heightFt = heightM / 0.3048;

                double minX = Math.Min(box.Min.X, box.Max.X);
                double maxX = Math.Max(box.Min.X, box.Max.X);
                double minY = Math.Min(box.Min.Y, box.Max.Y);
                double maxY = Math.Max(box.Min.Y, box.Max.Y);
                double minZ = view.GenLevel != null ? view.GenLevel.Elevation : Math.Min(box.Min.Z, box.Max.Z);
                double maxZ = minZ + heightFt;

                if (maxX - minX < 1.0e-6 || maxY - minY < 1.0e-6)
                {
                    TaskDialog.Show("JTOOLS: 3D-фрагмент",
                        "Область слишком маленькая для создания 3D-вида.");
                    return Result.Cancelled;
                }

                View3D view3d = null;
                using (var t = new Transaction(doc, "Создать 3D-вид-фрагмент"))
                {
                    t.Start();

                    var vft = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewFamilyType))
                        .Cast<ViewFamilyType>()
                        .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);
                    if (vft == null)
                    {
                        t.RollBack();
                        TaskDialog.Show("JTOOLS: 3D-фрагмент",
                            "В проекте нет типа 3D-видов.");
                        return Result.Cancelled;
                    }

                    view3d = View3D.CreateIsometric(doc, vft.Id);

                    // Не активируем crop region (экранная рамка): вид создаётся без обрезки.
                    // Вместо этого включается трёхмерный разделительный бокс (section box).
                    // С Transform = Identity границы задаются в координатах модели, поэтому
                    // бокс точно совпадает с растянутой на плане областью, поднятой на высоту.
                    view3d.SetSectionBox(new BoundingBoxXYZ
                    {
                        Transform = Transform.Identity,
                        Min = new XYZ(minX, minY, minZ),
                        Max = new XYZ(maxX, maxY, maxZ)
                    });
                    view3d.IsSectionBoxActive = true;
                    view3d.Name = UniqueViewName(doc, "3D (фрагмент)");

                    t.Commit();
                }

                uidoc.ActiveView = view3d;
                GrdLog.Log("GrdBox3DCommand: создан 3D-вид-фрагмент '" + view3d.Name +
                           "', высота=" + heightM.ToString(System.Globalization.CultureInfo.InvariantCulture) + " м");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("GrdBox3DCommand: EXCEPTION " + ex);
                try { TaskDialog.Show("JTOOLS: 3D-фрагмент", "Ошибка: " + ex.Message); }
                catch { }
                return Result.Failed;
            }
        }

        /// <summary>Уникальное имя вида: к базовому имени добавляется «(2)», «(3)», …</summary>
        private static string UniqueViewName(Document doc, string baseName)
        {
            var existing = new HashSet<string>(
                new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Select(v => v.Name),
                StringComparer.OrdinalIgnoreCase);

            if (!existing.Contains(baseName)) return baseName;
            for (int i = 2; ; i++)
            {
                var name = baseName + " (" + i + ")";
                if (!existing.Contains(name)) return name;
            }
        }
    }
}