using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using GrdRevit.Core;

namespace GrdRevit.Ui
{
    public class LinePatternOption
    {
        public string Name { get; set; }
        public long Key { get; set; }
    }

    public static class SystemTypeService
    {
        private const string TxApply = "JTOOLS: типы систем — применить изменения";
        private const string TxDuplicate = "JTOOLS: типы систем — копировать";
        private const string TxLegend = "JTOOLS: типы систем — легенда";

        public static SystemTypeRow[] Collect(Document doc, bool pipes)
        {
            var result = new List<SystemTypeRow>();
            try
            {
                var type = pipes ? typeof(PipingSystemType) : typeof(MechanicalSystemType);
                foreach (var e in new FilteredElementCollector(doc).OfClass(type))
                {
                    if (!(e is MEPSystemType m)) continue;
                    result.Add(new SystemTypeRow(
                        m.Id,
                        SafeName(m),
                        SafeAbbreviation(m),
                        SafeColor(m),
                        SafeWeight(m),
                        SafePatternKey(m)));
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("SystemTypeService.Collect EXCEPTION: " + ex);
            }
            return result
                .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }

        public static List<LinePatternOption> CollectPatterns(Document doc)
        {
            var list = new List<LinePatternOption>
            {
                new LinePatternOption { Name = "(паттерн не задан)", Key = -1 }
            };
            try
            {
                foreach (var e in new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)))
                {
                    if (e is LinePatternElement p)
                        list.Add(new LinePatternOption { Name = p.Name, Key = p.Id.Value });
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("SystemTypeService.CollectPatterns EXCEPTION: " + ex);
            }
            return list.OrderBy(o => o.Key == -1 ? 0 : 1).ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static int Apply(Document doc, IEnumerable<SystemTypeRow> rows)
        {
            var dirty = rows.Where(r => r.IsDirtyAny).ToList();
            if (dirty.Count == 0) return 0;
            using (var t = new Transaction(doc, TxApply))
            {
                t.Start();
                foreach (var row in dirty)
                    ApplyRow(doc, row);
                t.Commit();
            }
            foreach (var row in dirty)
                row.ResetOriginals();
            return dirty.Count;
        }

        public static SystemTypeRow Duplicate(Document doc, SystemTypeRow original, out string error)
        {
            error = null;
            if (!(doc.GetElement(original.Id) is MEPSystemType src))
            {
                error = "Тип «" + original.Name + "» не найден в документе.";
                return null;
            }
            ElementType dup = null;
            using (var t = new Transaction(doc, TxDuplicate))
            {
                t.Start();
                try
                {
                    dup = src.Duplicate(UniqueName(doc, src.Name));
                    t.Commit();
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    error = ex.Message;
                    GrdLog.Log("SystemTypeService.Duplicate EXCEPTION: " + ex);
                    return null;
                }
            }
            if (dup is MEPSystemType m)
            {
                return new SystemTypeRow(
                    dup.Id,
                    SafeName(m),
                    SafeAbbreviation(m),
                    SafeColor(m),
                    SafeWeight(m),
                    SafePatternKey(m));
            }
            error = "Дублирование не вернуло тип системы.";
            return null;
        }

        public static string DrawLegend(Document doc, IReadOnlyList<SystemTypeRow> rows, XYZ pick)
        {
            var view = doc.ActiveView;
            if (view == null) return "Нет активного вида для легенды.";
            var vt = view.ViewType;
            if (vt != ViewType.FloorPlan && vt != ViewType.CeilingPlan &&
                vt != ViewType.Section && vt != ViewType.Elevation &&
                vt != ViewType.DraftingView)
                return "Легенда создаётся в графическом виде (план, разрез, фасад или детализация). " +
                       "Активный вид «" + view.Name + "» для этого не подходит.";
            if (rows == null || rows.Count == 0) return "Список типов пуст — рисовать нечего.";
            try
            {
                var noteTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                const double lenFt = 3.28083989501;
                const double stepFt = 1.2;
                using (var t = new Transaction(doc, TxLegend))
                {
                    t.Start();
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var row = rows[i];
                        double y = pick.Y - i * stepFt;
                        var start = new XYZ(pick.X, y, pick.Z);
                        var end = new XYZ(pick.X + lenFt, y, pick.Z);
                        var dl = doc.Create.NewDetailCurve(view, Line.CreateBound(start, end));
                        var ogs = new OverrideGraphicSettings();
                        var c = row.ColorValue;
                        if (c != null && c.IsValid)
                            ogs.SetProjectionLineColor(c);
                        ogs.SetProjectionLineWeight(4);
                        if (row.PatternKey >= 0)
                            ogs.SetProjectionLinePatternId(new ElementId(row.PatternKey));
                        view.SetElementOverrides(dl.Id, ogs);
                        var label = row.Name;
                        var abbr = (row.Abbreviation ?? string.Empty).Trim();
                        if (label.Length > 0 && abbr.Length > 0) label += " (" + abbr + ")";
                        TextNote.Create(doc, view.Id,
                            new XYZ(start.X + lenFt + 0.35, y, pick.Z),
                            label, noteTypeId);
                    }
                    t.Commit();
                }
                return null;
            }
            catch (Exception ex)
            {
                GrdLog.Log("SystemTypeService.DrawLegend EXCEPTION: " + ex);
                return "Не удалось создать легенду: " + ex.Message;
            }
        }

        public static void WriteExcel(string path, bool pipes, IReadOnlyList<SystemTypeRow> rows,
            IEnumerable<LinePatternOption> patterns)
        {
            var sheet = new XlsxSheet
            {
                Name = pipes ? "Типы (трубы)" : "Типы (воздуховоды)"
            };
            sheet.Headers.Add("Имя");
            sheet.Headers.Add("Сокращение");
            sheet.Headers.Add("Цвет (HEX)");
            sheet.Headers.Add("Толщина линий");
            sheet.Headers.Add("Тип линий");
            var lookup = patterns.ToDictionary(p => p.Key, p => p.Name);
            foreach (var row in rows)
            {
                var c = row.ColorValue;
                var hex = c != null && c.IsValid ? ToHex(c) : "—";
                var patName = lookup.TryGetValue(row.PatternKey, out var name) ? name : "—";
                sheet.Rows.Add(new[]
                {
                    row.Name,
                    row.Abbreviation ?? string.Empty,
                    hex,
                    row.LineWeight.ToString(),
                    patName
                });
            }
            XlsxWriter.Write(path, new[] { sheet });
        }

        public static string ToHex(Autodesk.Revit.DB.Color c)
        {
            return "#" + c.Red.ToString("X2") + c.Green.ToString("X2") + c.Blue.ToString("X2");
        }

        private static void ApplyRow(Document doc, SystemTypeRow row)
        {
            if (!(doc.GetElement(row.Id) is MEPSystemType m)) return;
            if (row.IsNameDirty)
            {
                try { if (m.Name != row.Name) m.Name = row.Name; }
                catch (Exception ex) { GrdLog.Log("SystemTypeService.ApplyRow Name EXCEPTION: " + ex.Message); }
            }
            if (row.IsAbbreviationDirty)
            {
                try { m.Abbreviation = row.Abbreviation ?? string.Empty; }
                catch (Exception ex) { GrdLog.Log("SystemTypeService.ApplyRow Abbreviation EXCEPTION: " + ex.Message); }
            }
            if (row.IsColorDirty && row.ColorValue != null)
            {
                try { m.LineColor = row.ColorValue; }
                catch (Exception ex) { GrdLog.Log("SystemTypeService.ApplyRow LineColor EXCEPTION: " + ex.Message); }
            }
            if (row.IsLineWeightDirty)
            {
                try { m.LineWeight = row.LineWeight; }
                catch (Exception ex) { GrdLog.Log("SystemTypeService.ApplyRow LineWeight EXCEPTION: " + ex.Message); }
            }
            if (row.IsPatternDirty)
            {
                try
                {
                    m.LinePatternId = row.PatternKey >= 0
                        ? new ElementId(row.PatternKey)
                        : ElementId.InvalidElementId;
                }
                catch (Exception ex) { GrdLog.Log("SystemTypeService.ApplyRow LinePatternId EXCEPTION: " + ex.Message); }
            }
        }

        private static string UniqueName(Document doc, string baseName)
        {
            var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            try
            {
                foreach (var m in new FilteredElementCollector(doc).OfClass(typeof(MEPSystemType)))
                {
                    if (m is MEPSystemType type && type.Name != null)
                        names.Add(type.Name);
                }
            }
            catch (Exception ex) { GrdLog.Log("SystemTypeService.UniqueName EXCEPTION: " + ex.Message); }
            var candidate = baseName + " (копия)";
            int i = 2;
            while (names.Contains(candidate))
                candidate = baseName + " (копия " + (i++) + ")";
            return candidate;
        }

        private static string SafeName(MEPSystemType m)
        {
            try { return m.Name ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string SafeAbbreviation(MEPSystemType m)
        {
            try { return m.Abbreviation ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static Autodesk.Revit.DB.Color SafeColor(MEPSystemType m)
        {
            try
            {
                var c = m.LineColor;
                if (c != null && c.IsValid) return c;
            }
            catch { }
            return null;
        }

        private static int SafeWeight(MEPSystemType m)
        {
            try { return m.LineWeight; }
            catch { return 1; }
        }

        private static long SafePatternKey(MEPSystemType m)
        {
            try
            {
                var id = m.LinePatternId;
                if (id != null && id != ElementId.InvalidElementId) return id.Value;
            }
            catch { }
            return -1;
        }
    }
}