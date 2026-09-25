using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GrdRevit.Revit;

namespace GrdRevit
{
    /// <summary>
    /// «План-фрагмент из 3D» — обратная операция к «3D-фрагмент»:
    /// по выделенным на 3D-виде объектам определяется самый низкий уровень,
    /// берётся (или создаётся) этажный план этого уровня, дублируется в
    /// фрагмент, обрезается точно по габаритам выделения и сразу открывается.
    /// Границы видимости по высоте (пределы видимости) охватывают всё выделение.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdBoxPlanCommand : IExternalCommand
    {
        private const double CmdToFeet = 1.0 / 0.3048;
        private const double MarginFt = 0.45 * CmdToFeet;         // отступ обрезки по плану, 0.45 м
        private const double HeightMarginFt = 0.45 * CmdToFeet;   // запас границ видимости по высоте
        private const double DepthSpareFt = 5.0 * CmdToFeet;      // запас обрезки по Z ниже плоскости глубины

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                RevitContext.Initialize(commandData.Application);

                var uidoc = commandData.Application.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (uidoc == null || doc == null)
                {
                    TaskDialog.Show("JTOOLS: план-фрагмент", "Откройте документ и 3D-вид, выделите объекты.");
                    return Result.Cancelled;
                }
                if (doc.IsFamilyDocument)
                {
                    TaskDialog.Show("JTOOLS: план-фрагмент",
                        "Команда доступна только в проектном документе (RVT).");
                    return Result.Cancelled;
                }

                var view3d = uidoc.ActiveGraphicalView as View3D;
                if (view3d == null)
                {
                    TaskDialog.Show("JTOOLS: план-фрагмент",
                        "Откройте 3D-вид, выделите объекты и повторите команду.");
                    return Result.Cancelled;
                }

                var ids = uidoc.Selection.GetElementIds();
                if (ids == null || ids.Count == 0)
                {
                    TaskDialog.Show("JTOOLS: план-фрагмент",
                        "Выделите объекты на 3D-виде и повторите команду.");
                    return Result.Cancelled;
                }

                var selected = ids
                    .Select(id => doc.GetElement(id))
                    .Where(e => e != null && e.Category != null)
                    .ToList();
                if (selected.Count == 0)
                {
                    TaskDialog.Show("JTOOLS: план-фрагмент",
                        "Не удалось прочитать выделенные объекты.");
                    return Result.Cancelled;
                }

                // Габариты выделения (в координатах модели) и самый низкий уровень.
                BoundingBoxXYZ outline = CollectBounds(selected, view3d, doc, out Level lowestLevel);
                if (outline == null || lowestLevel == null)
                {
                    TaskDialog.Show("JTOOLS: план-фрагмент",
                        "Не удалось определить габариты выделения: у объектов нет геометрии и уровня.");
                    return Result.Cancelled;
                }

                double minX = outline.Min.X;
                double maxX = outline.Max.X;
                double minY = outline.Min.Y;
                double maxY = outline.Max.Y;
                double minZ = outline.Min.Z;
                double maxZ = outline.Max.Z;

                ViewPlan fragment = null;
                using (var t = new Transaction(doc, "JTOOLS: план-фрагмент из 3D"))
                {
                    t.Start();

                    // План-основа: первый этажный план выбранного уровня; если нет — создаём новый.
                    var source = FindFloorPlan(doc, lowestLevel.Id);
                    if (source == null)
                    {
                        var vft = new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewFamilyType))
                            .Cast<ViewFamilyType>()
                            .FirstOrDefault(x => x.ViewFamily == ViewFamily.FloorPlan);
                        if (vft == null)
                        {
                            t.RollBack();
                            TaskDialog.Show("JTOOLS: план-фрагмент",
                                "В проекте нет типа этажных планов.");
                            return Result.Cancelled;
                        }
                        source = ViewPlan.Create(doc, vft.Id, lowestLevel.Id);
                    }

                    var newId = source.Duplicate(ViewDuplicateOption.Duplicate);
                    fragment = doc.GetElement(newId) as ViewPlan;
                    if (fragment == null)
                    {
                        t.RollBack();
                        TaskDialog.Show("JTOOLS: план-фрагмент",
                            "Не удалось дублировать этажный план.");
                        return Result.Cancelled;
                    }

                    fragment.Name = UniqueViewName(doc, source.Name + " (фрагмент)");

                    // Обрезка по габаритам выделения (с отступом), в координатах плана.
                    ApplyCropAndRange(doc, fragment, lowestLevel,
                        minX - MarginFt, maxX + MarginFt,
                        minY - MarginFt, maxY + MarginFt,
                        minZ - HeightMarginFt, maxZ + HeightMarginFt);

                    t.Commit();
                }

                uidoc.ActiveView = fragment;

                GrdLog.Log("GrdBoxPlanCommand: создан план-фрагмент '" + fragment.Name +
                           "' уровня '" + lowestLevel.Name + "', объектов=" + selected.Count);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("GrdBoxPlanCommand: EXCEPTION " + ex);
                try { TaskDialog.Show("JTOOLS: план-фрагмент", "Ошибка: " + ex.Message); }
                catch { }
                return Result.Failed;
            }
        }

        /// <summary>Первый этажный план уровня (не шаблон), отсортированный по имени.</summary>
        private static ViewPlan FindFloorPlan(Document doc, ElementId levelId)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel?.Id == levelId)
                .OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault();
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

        /// <summary>Обрезка фрагмента по прямоугольнику и пределы видимости, чтобы
        /// всё выделение было видно по высоте.</summary>
        private static void ApplyCropAndRange(Document doc, ViewPlan plan, Level level,
            double axisMinX, double axisMaxX, double axisMinY, double axisMaxY,
            double axisMinZ, double axisMaxZ)
        {
            double levelElev = level.Elevation;

            // Пределы видимости: верхняя плоскость выше верхнего объекта,
            // нижняя — ниже нижнего, глубина ещё ниже.
            double topOff = axisMaxZ - levelElev;
            double botOff = axisMinZ - levelElev;
            if (topOff <= botOff) topOff = botOff + 1.0;
            try
            {
                var pvr = plan.GetViewRange();
                var depthPlane = PlanViewPlane.ViewDepthPlane;
                pvr.SetLevelId(PlanViewPlane.TopClipPlane, level.Id);
                pvr.SetOffset(PlanViewPlane.TopClipPlane, topOff);
                pvr.SetLevelId(PlanViewPlane.BottomClipPlane, level.Id);
                pvr.SetOffset(PlanViewPlane.BottomClipPlane, botOff);
                pvr.SetLevelId(depthPlane, level.Id);
                pvr.SetOffset(depthPlane, botOff - 1.0);
                plan.SetViewRange(pvr);
            }
            catch (Exception ex)
            {
                GrdLog.Log("GrdBoxPlanCommand: не удалось задать пределы видимости: " + ex.Message);
            }

            // Обрезка: преобразуем модельный прямоугольник в систему координат плана.
            try
            {
                Transform t = Transform.Identity;
                try { t = plan.CropBox.Transform; } catch { }
                XYZ locMin = t.Inverse.OfPoint(new XYZ(axisMinX, axisMinY, axisMinZ - DepthSpareFt));
                XYZ locMax = t.Inverse.OfPoint(new XYZ(axisMaxX, axisMaxY, axisMaxZ + DepthSpareFt));

                plan.CropBox = new BoundingBoxXYZ
                {
                    Transform = t,
                    Min = locMin,
                    Max = locMax
                };
                plan.CropBoxActive = true;
                plan.CropBoxVisible = true;
            }
            catch (Exception ex)
            {
                GrdLog.Log("GrdBoxPlanCommand: не удалось задать обрезку: " + ex.Message);
            }
        }

        /// <summary>Общие габариты выделения в координатах модели и самый низкий уровень.</summary>
        private static BoundingBoxXYZ CollectBounds(List<Element> elements, View3D view3d,
            Document doc, out Level lowestLevel)
        {
            lowestLevel = null;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool any = false;

            // Уровень объекта: его собственный, иначе уровень по нижней точке геометрии.
            double lowestElevation = double.MaxValue;
            Level fallbackLowest = null;
            foreach (Level level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
            {
                if (fallbackLowest == null || level.Elevation < fallbackLowest.Elevation)
                    fallbackLowest = level;
            }

            foreach (var e in elements)
            {
                XYZ min, max;
                double? ownElevation = GetAssociatedElevation(e);
                if (!TryGetBounds(e, view3d, out min, out max))
                {
                    // Геометрии нет — уровень учитываем, габариты не расширяем.
                    if (ownElevation.HasValue && ownElevation.Value < lowestElevation)
                        lowestElevation = ownElevation.Value;
                    continue;
                }

                any = true;
                minX = Math.Min(minX, min.X); maxX = Math.Max(maxX, max.X);
                minY = Math.Min(minY, min.Y); maxY = Math.Max(maxY, max.Y);
                minZ = Math.Min(minZ, min.Z); maxZ = Math.Max(maxZ, max.Z);

                double elevation = ownElevation ?? LevelForElevation(doc, fallbackLowest, min.Z);
                if (elevation < lowestElevation)
                    lowestElevation = elevation;
            }

            if (!any) return null;

            foreach (Level level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
            {
                if (level.Elevation <= lowestElevation + 1.0e-6 &&
                    (lowestLevel == null || level.Elevation > lowestLevel.Elevation))
                    lowestLevel = level;
            }
            if (lowestLevel == null) lowestLevel = fallbackLowest;

            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }

        /// <summary>Высота уровня элемента через Element.LevelId, иначе null.</summary>
        private static double? GetAssociatedElevation(Element e)
        {
            try
            {
                if (e.LevelId == null || e.LevelId == ElementId.InvalidElementId) return null;
                var level = e.Document.GetElement(e.LevelId) as Level;
                return level?.Elevation;
            }
            catch { return null; }
        }

        /// <summary>Ближайший уровень выше нижней точки геометрии (для элементов без уровня).</summary>
        private static double LevelForElevation(Document doc, Level fallbackLowest, double z)
        {
            Level best = null;
            foreach (Level level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
            {
                if (level.Elevation <= z + 1.0e-6 &&
                    (best == null || level.Elevation > best.Elevation))
                    best = level;
            }
            return (best ?? fallbackLowest).Elevation;
        }

        /// <summary>Габариты элемента в координатах модели: геометрия, затем точка размещения
        /// или отрезок размещения; иначе false.</summary>
        private static bool TryGetBounds(Element e, View3D view3d, out XYZ min, out XYZ max)
        {
            min = max = null;
            try
            {
                var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
                var geom = e.get_Geometry(opt);
                var gb = geom?.GetBoundingBox();
                if (gb != null)
                {
                    min = gb.Min; max = gb.Max;
                    return true;
                }

                var loc = e.Location;
                if (loc is LocationPoint lp)
                {
                    min = max = lp.Point;
                    return true;
                }
                if (loc is LocationCurve lc && lc.Curve != null)
                {
                    XYZ p0 = lc.Curve.GetEndPoint(0);
                    XYZ p1 = lc.Curve.GetEndPoint(1);
                    min = new XYZ(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), Math.Min(p0.Z, p1.Z));
                    max = new XYZ(Math.Max(p0.X, p1.X), Math.Max(p0.Y, p1.Y), Math.Max(p0.Z, p1.Z));
                    return true;
                }
            }
            catch { }
            return false;
        }
    }
}