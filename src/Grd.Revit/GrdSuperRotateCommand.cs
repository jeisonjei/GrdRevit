using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using GrdRevit.Ui;

namespace GrdRevit
{
    /// <summary>
    /// «Супер Поворот»: упрощённый поворот элемента.
    /// Порядок: запуск команды → кликом выбрать элемент → кликом указать центр поворота → поворот.
    /// Угол всегда берётся последний запомненный (при первом запуске запрашивается один раз
    /// и сохраняется в настройках), центр поворота — это точная позиция щелчка мыши.
    /// Поворот на планах выполняется вокруг вертикальной мировой оси Z (без смещения по высоте).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdSuperRotateCommand : IExternalCommand
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
                    TaskDialog.Show("JTOOLS: Супер Поворот", "Откройте документ Revit.");
                    return Result.Cancelled;
                }
                if (doc.IsFamilyDocument)
                {
                    TaskDialog.Show("JTOOLS: Супер Поворот",
                        "Команда доступна только в проектном документе (RVT).");
                    return Result.Cancelled;
                }

                double angleDeg = RevitContext.Settings.SuperRotateAngleDeg;
                if (angleDeg == 0.0)
                {
                    var win = new SuperRotateAngleWindow(90.0);
                    try
                    {
                        var owner = MainWindow.Instance;
                        if (owner != null && owner.IsVisible) win.Owner = owner;
                    }
                    catch { }
                    if (win.ShowDialog() != true)
                    {
                        GrdLog.Log("GrdSuperRotateCommand: ввод угла отменён");
                        return Result.Cancelled;
                    }
                    angleDeg = win.AngleDeg;
                    RevitContext.Settings.SuperRotateAngleDeg = angleDeg;
                    RevitContext.SaveSettings();
                    GrdLog.Log("GrdSuperRotateCommand: угол задан вручную: " + angleDeg);
                }

                var viewBefore = uidoc.ActiveGraphicalView;
                GrdLog.Log("GrdSuperRotateCommand: вид=" + (viewBefore?.GetType().Name ?? "нет") +
                           " «" + (viewBefore?.Name ?? "") + "»" +
                           ", UpDirection=" + (viewBefore?.UpDirection != null ? Fmt(viewBefore.UpDirection) : "null"));

                Reference picked;
                try
                {
                    var filter = new RotatableElementsFilter();
                    picked = uidoc.Selection.PickObject(
                        ObjectType.Element,
                        filter,
                        "Супер Поворот: щёлкните по элементу для поворота");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    GrdLog.Log("GrdSuperRotateCommand: выбор элемента отменён");
                    return Result.Cancelled;
                }

                var ids = new List<ElementId> { picked.ElementId };
                GrdLog.Log("GrdSuperRotateCommand: выбран элемент id=" + picked.ElementId.Value);

                XYZ center;
                try
                {
                    center = uidoc.Selection.PickPoint(
                        "Супер Поворот: укажите центр поворота — это будет точная позиция щелчка мыши");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    GrdLog.Log("GrdSuperRotateCommand: указание центра отменено");
                    return Result.Cancelled;
                }
                GrdLog.Log("GrdSuperRotateCommand: центр клика=" + Fmt(center));

                var view = uidoc.ActiveGraphicalView;
                bool is3D = view is View3D;
                XYZ axisDir = is3D
                    ? (view != null && view.UpDirection != null ? view.UpDirection : XYZ.BasisZ)
                    : XYZ.BasisZ;
                XYZ p2 = center + axisDir * 10.0;
                Line axis = Line.CreateBound(center, p2);
                double angleRad = angleDeg * Math.PI / 180.0;
                GrdLog.Log("GrdSuperRotateCommand: ось=" + Fmt(axisDir) + (is3D ? " (3D-вид)" : " (план/разрез: мировая Z)"));

                int rotated = 0;
                var errors = new List<string>();
                var disappearWarnings = new List<string>();
                var afterXY = new List<KeyValuePair<string, XYZ>>();

                using (var t = new Transaction(doc, "Супер поворот элементов"))
                {
                    t.Start();
                    foreach (var id in ids)
                    {
                        var el = doc.GetElement(id);
                        if (el == null) continue;
                        string label = el.Name + " [id " + id.Value + "]";
                        if (el.Pinned)
                        {
                            errors.Add(label + " — зафиксирован (pinned)");
                            continue;
                        }
                        XYZ before = LocationOrBboxMin(el);
                        try
                        {
                            ElementTransformUtils.RotateElement(doc, id, axis, angleRad);
                            rotated++;
                        }
                        catch (Exception ex)
                        {
                            GrdLog.Log("GrdSuperRotateCommand: «" + label + "»: " + ex.Message);
                            errors.Add(label + " — " + ex.Message);
                            continue;
                        }
                        var elAfter = doc.GetElement(id);
                        XYZ after = elAfter != null ? LocationOrBboxMin(elAfter) : null;
                        GrdLog.Log("GrdSuperRotateCommand: повёрнут «" + label + "» до=" + Fmt(before) +
                                   " после=" + Fmt(after) + " ΔZ=" +
                                   (before != null && after != null ? (after.Z - before.Z).ToString("0.###") : "n/a"));
                        if (before != null && after != null)
                        {
                            afterXY.Add(new KeyValuePair<string, XYZ>(label, after));
                            if (Math.Abs(after.Z - before.Z) > 1.0e-3)
                                disappearWarnings.Add(label + ": ΔZ=" + (after.Z - before.Z).ToString("0.###"));
                        }
                    }
                    t.Commit();
                }

                if (rotated == 0)
                {
                    TaskDialog.Show("JTOOLS: Супер Поворот",
                        "Ни один элемент не повёрнут.\n" + FirstLines(errors, 6));
                    return Result.Cancelled;
                }

                if (view is ViewPlan viewPlan)
                {
                    var range = viewPlan.GetViewRange();
                    double bottom = range.GetOffset(PlanViewPlane.BottomClipPlane);
                    double top = range.GetOffset(PlanViewPlane.TopClipPlane);
                    foreach (var kv in afterXY)
                    {
                        var z = kv.Value.Z;
                        if (z < bottom - 0.05 || z > top + 0.05)
                        {
                            string warn = "«" + kv.Key + "» после поворота вне диапазона вида по Z: " +
                                          z.ToString("0.###") + " (план: " + bottom.ToString("0.###") + "…" +
                                          top.ToString("0.###") + ")";
                            disappearWarnings.Add(warn);
                            GrdLog.Log("GrdSuperRotateCommand: ВНИМАНИЕ " + warn);
                        }
                    }
                }

                GrdLog.Log("GrdSuperRotateCommand: угол=" + angleDeg +
                           ", центр=" + Fmt(center) + ", повёрнуто=" + rotated + "/" + ids.Count +
                           (errors.Count > 0 ? ", ошибок=" + errors.Count : "") +
                           (disappearWarnings.Count > 0 ? ", вне-вида=" + disappearWarnings.Count : ""));

                if (disappearWarnings.Count > 0)
                {
                    TaskDialog.Show("JTOOLS: Супер Поворот",
                        "⚠ Элементы могли пропасть с вида:\n" + FirstLines(disappearWarnings, 6));
                    return Result.Cancelled;
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("GrdSuperRotateCommand: EXCEPTION " + ex);
                try { TaskDialog.Show("JTOOLS: Супер Поворот", "Ошибка: " + ex.Message); }
                catch { }
                return Result.Failed;
            }
        }

        private static XYZ LocationOrBboxMin(Element el)
        {
            try
            {
                var lp = el.Location as LocationPoint;
                if (lp != null) return lp.Point;
            }
            catch { }
            try
            {
                var bb = el.get_BoundingBox(null);
                if (bb != null) return bb.Min;
            }
            catch { }
            return null;
        }

        private static string Fmt(XYZ p)
        {
            if (p == null) return "n/a";
            return "(" + p.X.ToString("0.###") + ";" + p.Y.ToString("0.###") + ";" + p.Z.ToString("0.###") + ")";
        }

        private static string FirstLines(List<string> lines, int limit)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < lines.Count && i < limit; i++)
            {
                if (i > 0) sb.Append("\n");
                sb.Append(lines[i]);
            }
            if (lines.Count > limit) sb.Append("\n… ещё " + (lines.Count - limit));
            return sb.ToString();
        }

        /// <summary>Разрешает выбор только поворачиваемых элементов модели.</summary>
        private class RotatableElementsFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                if (elem == null || elem.Category == null || elem.Category.CategoryType != CategoryType.Model) return false;
                if (elem is Level || elem is Grid || elem is ReferencePlane) return false;
                return true;
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return false;
            }
        }
    }
}