using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GrdRevit.Core;

namespace GrdRevit
{
    /// <summary>
    /// «Экспорт спецификации в Calc»: выгружает текущую спецификацию (активный вид-таблица
    /// либо выбранная на листе рамка со спецификацией) в файл LibreOffice Calc (.ods).
    /// Запоминает последние папку и имя файла, после сохранения открывает Проводник
    /// с выделенным файлом (сам файл не открывается).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdScheduleExportCommand : IExternalCommand
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
                    TaskDialog.Show("JTOOLS: экспорт спецификации", "Откройте документ проекта.");
                    return Result.Cancelled;
                }
                if (doc.IsFamilyDocument)
                {
                    TaskDialog.Show("JTOOLS: экспорт спецификации",
                        "Экспорт доступен только в проектном документе (RVT).");
                    return Result.Cancelled;
                }

                var schedule = ResolveSchedule(uidoc);
                if (schedule == null)
                {
                    TaskDialog.Show("JTOOLS: экспорт спецификации",
                        "Откройте спецификацию (вид-таблицу) или выделите на листе рамку со спецификацией и повторите.");
                    return Result.Cancelled;
                }

                var rows = ReadTable(doc, schedule);
                if (rows.Count == 0)
                {
                    TaskDialog.Show("JTOOLS: экспорт спецификации",
                        "Спецификация «" + schedule.Name + "» пуста — экспортировать нечего.");
                    return Result.Cancelled;
                }

                // Папка и имя файла по умолчанию — из последнего экспорта (если были).
                var dirs = RevitContext.Settings.ScheduleExportDir;
                if (!string.IsNullOrEmpty(dirs) && !Directory.Exists(dirs)) dirs = string.Empty;
                var defaultName = !string.IsNullOrEmpty(RevitContext.Settings.ScheduleExportName)
                    ? RevitContext.Settings.ScheduleExportName
                    : SafeFileName(schedule.Name);

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Экспорт спецификации «" + schedule.Name + "» в Calc",
                    Filter = "LibreOffice Calc (*.ods)|*.ods|Все файлы (*.*)|*.*",
                    DefaultExt = ".ods",
                    AddExtension = true,
                    FileName = defaultName,
                    OverwritePrompt = true
                };
                if (!string.IsNullOrEmpty(dirs)) dlg.InitialDirectory = dirs;
                else
                {
                    var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    if (!string.IsNullOrEmpty(docs)) dlg.InitialDirectory = docs;
                }

                if (dlg.ShowDialog() != true)
                {
                    GrdLog.Log("GrdScheduleExportCommand: пользователь отменил выбор файла");
                    return Result.Cancelled;
                }
                var path = dlg.FileName;
                if (string.IsNullOrEmpty(path)) return Result.Cancelled;

                var sw = Stopwatch.StartNew();
                OdsExporter.Write(path, schedule.Name, rows);
                GrdLog.Log("GrdScheduleExportCommand: экспортировано «" + schedule.Name + "» -> " +
                           path + ", строк=" + rows.Count + " за " + sw.ElapsedMilliseconds + " мс");

                RevitContext.Settings.ScheduleExportDir = Path.GetDirectoryName(path) ?? string.Empty;
                RevitContext.Settings.ScheduleExportName = Path.GetFileName(path);
                try { RevitContext.SaveSettings(); }
                catch (Exception ex) { GrdLog.Log("GrdScheduleExportCommand: SaveSettings EXCEPTION " + ex); }

                OpenInExplorer(path);

                TaskDialog.Show("JTOOLS: экспорт спецификации",
                    "Спецификация «" + schedule.Name + "» сохранена:\n" + path +
                    "\n\nВ Проводнике открыта папка с файлом (файл специально не открывался).");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("GrdScheduleExportCommand: EXCEPTION " + ex);
                try { TaskDialog.Show("JTOOLS: экспорт спецификации", "Ошибка: " + ex.Message); }
                catch { }
                return Result.Failed;
            }
        }

        /// <summary>Текущая спецификация: активный вид-таблица, либо выделенная на листе рамка с таблицей.</summary>
        private static ViewSchedule ResolveSchedule(UIDocument uidoc)
        {
            var doc = uidoc.Document;
            if (uidoc.ActiveView is ViewSchedule active) return active;
            if (uidoc.ActiveView is ViewSheet)
            {
                foreach (var eid in uidoc.Selection.GetElementIds())
                {
                    if (!(doc.GetElement(eid) is Viewport vp)) continue;
                    if (doc.GetElement(vp.ViewId) is ViewSchedule vs) return vs;
                }
            }
            return null;
        }

        /// <summary>Читает таблицу спецификации: шапка, тело, итоги, подвал — как показано в Revit.
        /// Перед чтением документ пересчитывается, а секции таблицы принудительно обновляются:
        /// иначе рассчитанные значения (формулы, итоги) могут вернуть пустые ячейки.</summary>
        private static List<string[]> ReadTable(Document doc, ViewSchedule schedule)
        {
            try { doc.Regenerate(); }
            catch (Exception ex) { GrdLog.Log("GrdScheduleExportCommand: Regenerate EXCEPTION " + ex); }

            var rows = new List<string[]>();
            var td = schedule.GetTableData();
            int maxCols = 0;
            var sections = new List<(SectionType type, string[] line)>();

            foreach (var st in new[] { SectionType.Header, SectionType.Body, SectionType.Summary, SectionType.Footer })
            {
                var sd = td.GetSectionData(st);
                if (sd == null) continue;
                try { if (sd.HideSection) continue; } catch { }
                try { sd.RefreshData(); }
                catch (Exception ex) { GrdLog.Log("GrdScheduleExportCommand: RefreshData(" + st + ") EXCEPTION " + ex); }
                int r = sd.NumberOfRows;
                int c = sd.NumberOfColumns;
                maxCols = Math.Max(maxCols, c);
                for (int i = 0; i < r; i++)
                {
                    var line = new string[c];
                    for (int j = 0; j < c; j++)
                    {
                        try { line[j] = sd.GetCellText(i, j) ?? string.Empty; }
                        catch (Exception ex)
                        {
                            line[j] = string.Empty;
                            GrdLog.Log("GrdScheduleExportCommand: GetCellText(" + st + ", " + i + ", " + j + ") EXCEPTION " + ex.Message);
                        }
                    }
                    sections.Add((st, line));
                }
            }

            foreach (var (type, line) in sections)
            {
                var row = new string[maxCols];
                for (int j = 0; j < line.Length; j++) row[j] = line[j];
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>Имя файла по умолчанию из имени спецификации (без расширения).</summary>
        private static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Спецификация";
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Where(c => !invalid.Contains(c)).ToArray();
            var s = new string(chars);
            return s.Trim().Length == 0 ? "Спецификация" : s.Trim();
        }

        /// <summary>Открывает Проводник с выделенным файлом (сам файл не открывается).</summary>
        private static void OpenInExplorer(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\""));
            }
            catch (Exception ex)
            {
                GrdLog.Log("GrdScheduleExportCommand.OpenInExplorer EXCEPTION: " + ex);
            }
        }
    }
}