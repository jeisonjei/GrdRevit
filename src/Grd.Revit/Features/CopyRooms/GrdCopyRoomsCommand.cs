using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using GrdRevit.Ui;

namespace GrdRevit
{
    /// <summary>Создаёт пространства ОВ по помещениям выбранной связи АР: имя и номер берутся из АР.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdCopyRoomsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                RevitContext.Initialize(commandData.Application);

                var uiDoc = commandData.Application.ActiveUIDocument;
                var doc = uiDoc.Document;

                GrdLog.Log("GrdCopyRooms: start, doc=" + doc.Title);

                var links = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .Where(l => l.GetLinkDocument() != null)
                    .ToList();

                if (links.Count == 0)
                {
                    GrdLog.Log("GrdCopyRooms: no loaded links");
                    MessageBox.Show("Нет загруженных связей.\nЗагрузите ссылку АР в модель и повторите.",
                        "JTOOLS: Скопировать комнаты", MessageBoxButton.OK, MessageBoxImage.Information);
                    return Result.Cancelled;
                }

                RevitLinkInstance link;
                if (links.Count == 1)
                {
                    link = links[0];
                    GrdLog.Log("GrdCopyRooms: single link '" + LinkDisplayName(link) + "'");
                }
                else
                {
                    var picker = new LinkPickerWindow(links);
                    new WindowInteropHelper(picker) { Owner = RevitContext.MainWindowHandle };
                    if (picker.ShowDialog() != true)
                    {
                        GrdLog.Log("GrdCopyRooms: link selection cancelled");
                        return Result.Cancelled;
                    }
                    link = picker.SelectedLink;
                    GrdLog.Log("GrdCopyRooms: selected link '" + LinkDisplayName(link) + "'");
                }

                var linkDoc = link.GetLinkDocument();

                var rooms = new FilteredElementCollector(linkDoc)
                    .WhereElementIsNotElementType()
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .Cast<Room>()
                    .ToList();

                GrdLog.Log("GrdCopyRooms: rooms in link = " + rooms.Count);

                if (rooms.Count == 0)
                {
                    MessageBox.Show("В выбранной связи АР нет помещений (категория «Помещения»).",
                        "JTOOLS: Скопировать комнаты", MessageBoxButton.OK, MessageBoxImage.Information);
                    return Result.Cancelled;
                }

                var transform = link.GetTotalTransform();

                int created = 0;
                int skippedNoPoint = 0;
                var failed = new List<string>();

                using (var tx = new Transaction(doc, "Скопировать комнаты из АР"))
                {
                    tx.Start();

                    foreach (var room in rooms)
                    {
                        var loc = room.Location as LocationPoint;
                        if (loc == null)
                        {
                            skippedNoPoint++;
                            GrdLog.Log("GrdCopyRooms: room '" + room.Number + " " + room.Name +
                                       "' без точки вставки — пропущено");
                            continue;
                        }

                        var hostPoint = transform.OfPoint(loc.Point);
                        var level = FindHostLevel(doc, room.Level);
                        var phase = FindHostPhase(doc, linkDoc.GetElement(room.CreatedPhaseId) as Phase);

                        if (level == null || phase == null)
                        {
                            failed.Add(room.Number + " " + room.Name);
                            GrdLog.Log("GrdCopyRooms: room '" + room.Number + " " + room.Name +
                                       "': не найден уровень/фаза в модели — пропущено");
                            continue;
                        }

                        try
                        {
                            var space = doc.Create.NewSpace(level, phase, new UV(hostPoint.X, hostPoint.Y));
                            space.Name = room.Name;
                            space.Number = room.Number;
                            created++;
                            GrdLog.Log("GrdCopyRooms: created space " + space.Number + " " + space.Name +
                                       " @" + FormatPoint(hostPoint) + ", level=" + level.Name);
                        }
                        catch (Exception ex)
                        {
                            failed.Add(room.Number + " " + room.Name);
                            GrdLog.Log("GrdCopyRooms: FAILED '" + room.Number + " " + room.Name +
                                       "' @ " + FormatPoint(hostPoint) + " : " + ex.Message);
                        }
                    }

                    if (created > 0)
                    {
                        tx.Commit();
                        GrdLog.Log("GrdCopyRooms: committed, created=" + created +
                                   ", skippedNoPoint=" + skippedNoPoint + ", failed=" + failed.Count);
                    }
                    else
                    {
                        tx.RollBack();
                        GrdLog.Log("GrdCopyRooms: nothing to create, rolled back");
                    }
                }

                var summary = "Создано пространств: " + created + "\n";
                if (skippedNoPoint > 0) summary += "Без точки вставки в АР (пропущено): " + skippedNoPoint + "\n";
                if (failed.Count > 0)
                {
                    summary += "Не удалось создать: " + failed.Count + "\n";
                    if (failed.Count <= 20) summary += "  " + string.Join("; ", failed);
                }
                summary += "\nИмя и номер пространств — как в помещении АР.";

                GrdLog.Log("GrdCopyRooms: done");
                if (created > 0 || failed.Count > 0)
                {
                    MessageBox.Show(summary, "JTOOLS: Скопировать комнаты", MessageBoxButton.OK,
                        created == 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("GrdCopyRooms: EXCEPTION: " + ex);
                try
                {
                    MessageBox.Show(ex.Message, "JTOOLS: ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
                return Result.Failed;
            }
        }

        private static string LinkDisplayName(RevitLinkInstance link)
        {
            try
            {
                var doc = link.GetLinkDocument();
                if (doc != null && !string.IsNullOrEmpty(doc.PathName))
                    return System.IO.Path.GetFileName(doc.PathName);
                return link.Name;
            }
            catch
            {
                return "?";
            }
        }

        /// <summary>Уровень модели по имени уровня АР; при расхождении имён — ближайший по высоте.</summary>
        private static Level FindHostLevel(Document doc, Level sourceLevel)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToList();
            if (levels.Count == 0) return null;

            if (sourceLevel != null)
            {
                var byName = levels.FirstOrDefault(l => l.Name == sourceLevel.Name);
                if (byName != null) return byName;
                return levels.OrderBy(l => Math.Abs(l.Elevation - sourceLevel.Elevation)).FirstOrDefault();
            }
            return levels.FirstOrDefault();
        }

        /// <summary>Фаза модели по имени фазы АР; при расхождении — фаза активного вида, затем первая фаза модели.</summary>
        private static Phase FindHostPhase(Document doc, Phase sourcePhase)
        {
            if (sourcePhase != null)
            {
                var byName = new FilteredElementCollector(doc)
                    .OfClass(typeof(Phase))
                    .Cast<Phase>()
                    .FirstOrDefault(p => p.Name == sourcePhase.Name);
                if (byName != null) return byName;
            }

            try
            {
                var view = doc.ActiveView;
                if (view != null)
                {
                    var phaseId = view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
                    if (phaseId != null && phaseId != ElementId.InvalidElementId)
                    {
                        var viewPhase = doc.GetElement(phaseId) as Phase;
                        if (viewPhase != null) return viewPhase;
                    }
                }
            }
            catch { }

            return new FilteredElementCollector(doc)
                .OfClass(typeof(Phase))
                .Cast<Phase>()
                .FirstOrDefault();
        }

        private static string FormatPoint(XYZ p)
        {
            return p == null ? "?" :
                p.X.ToString("F2") + ";" + p.Y.ToString("F2") + ";" + p.Z.ToString("F2");
        }
    }
}