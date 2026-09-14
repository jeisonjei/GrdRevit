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
    /// Содержимое берётся из штатного экспорта Revit (ViewSchedule.Export, как «Файл →
    /// Экспорт → Спецификации → ... (CSV)»), поэтому выгружаются все колонки и строки ровно
    /// так, как они отображаются в спецификации (включая группировки, итоги, пустые поля).
    /// Запоминает последние папку и имя файла, после сохранения открывает Проводник.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdScheduleExportCommand : IExternalCommand
    {
        private const char CsvDelimiter = ';';

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

                var rows = ReadTable(schedule);
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

        /// <summary>Читает спецификацию штатным экспортом Revit в CSV (во временную папку)
        /// и переводит содержимое в список строк. Так получаются все колонки и строки
        /// ровно в том виде, как спецификация отображается в Revit (группировки, итоги,
        /// пустые ячейки) — в отличие от прямого чтения таблицы, которое часть значений теряет.</summary>
        private static List<string[]> ReadTable(ViewSchedule schedule)
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), "GrdRevitSchedule_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            try
            {
                var options = new ViewScheduleExportOptions
                {
                    FieldDelimiter = CsvDelimiter.ToString(),
                    ColumnHeaders = ExportColumnHeaders.None,
                    TextQualifier = ExportTextQualifier.DoubleQuote,
                    Title = false,
                    HeadersFootersBlanks = false
                };
                schedule.Export(tmpDir, "grd_schedule", options);
                if (!Directory.Exists(tmpDir))
                    throw new InvalidOperationException("Revit не смог выгрузить спецификацию во временный CSV.");

                var files = Directory.GetFiles(tmpDir);
                if (files.Length != 1)
                    throw new InvalidOperationException(
                        "Непредвиденный результат экспорта Revit (файлов: " + files.Length + ").");
                var csv = files[0];
                var rows = CsvReader.ParseFile(csv, CsvDelimiter);
                GrdLog.Log("GrdScheduleExportCommand: штатный экспорт Revit -> " + csv + ", строк=" + rows.Count);
                return rows;
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
                catch (Exception ex) { GrdLog.Log("GrdScheduleExportCommand: clean temp EXCEPTION " + ex); }
            }
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