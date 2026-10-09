using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using GrdRevit.Core;
using GrdRevit.Ui;

namespace GrdRevit
{
    /// <summary>
    /// Записывает в параметры пространств ОВ приточные/вытяжные системы и расходы воздуха
    /// из файла .tomat (программа расчёта теплопотерь). Помещения сопоставляются с
    /// существующими пространствами в окне импорта; новые пространства не создаются.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GrdTomatImportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                RevitContext.Initialize(commandData.Application);

                var uiDoc = commandData.Application.ActiveUIDocument;
                var doc = uiDoc.Document;

                GrdLog.Log("GrdTomatImport: start, doc=" + doc.Title);

                var spaces = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_MEPSpaces)
                    .WhereElementIsNotElementType()
                    .Cast<SpatialElement>()
                    .ToList();

                GrdLog.Log("GrdTomatImport: spaces = " + spaces.Count);

                if (spaces.Count == 0)
                {
                    GrdLog.Log("GrdTomatImport: no spaces in model");
                    System.Windows.MessageBox.Show(
                        "В модели нет пространств ОВ.\n" +
                        "Сначала создайте их (например, кнопкой «Скопировать комнаты») и повторите.",
                        "JTOOLS: Импорт .tomat", System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                    return Result.Cancelled;
                }

                var spaceList = BuildSpaceChoices(spaces);
                var paramNames = CollectParamNames(spaces);

                var file = PickAndParse();
                if (file == null)
                {
                    GrdLog.Log("GrdTomatImport: file not selected or empty");
                    return Result.Cancelled;
                }
                if (file.Rooms.Count == 0)
                {
                    System.Windows.MessageBox.Show(
                        "В файле не найдено помещений.\n" + string.Join("\n", file.ParseMessages),
                        "JTOOLS: Импорт .tomat", System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return Result.Cancelled;
                }

                var window = new TomatImportWindow(spaceList, paramNames);
                new WindowInteropHelper(window) { Owner = RevitContext.MainWindowHandle };
                window.LoadFile(file, RevitContext.Settings.LastTomatPath);

                GrdLog.Log("GrdTomatImport: showing window, rooms=" + file.Rooms.Count + ", spaces=" + spaceList.Count);
                if (window.ShowDialog() != true)
                {
                    GrdLog.Log("GrdTomatImport: cancelled by user");
                    return Result.Cancelled;
                }

                // Сохраняем выбор параметров и путь файла.
                RevitContext.Settings.TomatSupplySystemParam = window.SupplySystemParam;
                RevitContext.Settings.TomatExhaustSystemParam = window.ExhaustSystemParam;
                RevitContext.Settings.TomatSupplyAirflowParam = window.SupplyAirflowParam;
                RevitContext.Settings.TomatExhaustAirflowParam = window.ExhaustAirflowParam;
                if (!string.IsNullOrEmpty(window.LastTomatPath))
                {
                    RevitContext.Settings.LastTomatPath = window.LastTomatPath;
                }
                RevitContext.SaveSettings();

                var result = ApplyToSpaces(doc, window.Rows,
                    window.SupplySystemParam, window.ExhaustSystemParam,
                    window.SupplyAirflowParam, window.ExhaustAirflowParam);

                GrdLog.Log("GrdTomatImport: done, applied=" + result.AppliedCount +
                           ", missingParams=" + result.MissingParams.Count +
                           ", reportedApplied=" + result.ReportOnly + ", errors=" + result.Errors.Count);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                GrdLog.Log("GrdTomatImport: EXCEPTION: " + ex);
                try
                {
                    System.Windows.MessageBox.Show(ex.Message, "JTOOLS: ошибка",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
                catch { }
                return Result.Failed;
            }
        }

        // ---------- Выбор файла ----------

        /// <summary>
        /// Выбирает и разбирает файл .tomat: последний открытый, если существует, иначе диалог.
        /// Возвращает null, если выбор отменён.
        /// </summary>
        private static TomatFile PickAndParse()
        {
            string path = RevitContext.Settings.LastTomatPath;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Открыть файл .tomat",
                    Filter = "Файлы .tomat (*.tomat)|*.tomat|Все файлы (*.*)|*.*",
                    CheckFileExists = true
                };
                if (!string.IsNullOrEmpty(RevitContext.Settings.LastTomatPath))
                    dlg.InitialDirectory = System.IO.Path.GetDirectoryName(RevitContext.Settings.LastTomatPath);
                if (dlg.ShowDialog() != true) return null;
                path = dlg.FileName;
            }

            RevitContext.Settings.LastTomatPath = path;
            GrdLog.Log("GrdTomatImport: parsing " + path);
            return TomatParser.Parse(path);
        }

        // ---------- Сбор пространств и имён параметров ----------

        private static ObservableCollection<SpaceChoice> BuildSpaceChoices(List<SpatialElement> spaces)
        {
            var list = new ObservableCollection<SpaceChoice>(
                spaces
                    .Select(s => new SpaceChoice(s.Id.Value, s.Number ?? string.Empty, s.Name ?? string.Empty))
                    .OrderBy(c => c.Label, StringComparer.Ordinal));
            return list;
        }

        /// <summary>Имена доступных для записи параметров экземпляра пространств (текст/число).</summary>
        private static List<string> CollectParamNames(List<SpatialElement> spaces)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in spaces)
            {
                foreach (Parameter p in s.Parameters)
                {
                    if (p.IsReadOnly) continue;
                    if (p.StorageType != StorageType.String &&
                        p.StorageType != StorageType.Double &&
                        p.StorageType != StorageType.Integer) continue;
                    var name = p.Definition?.Name;
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }
            }
            return names.OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        // ---------- Применение ----------

        private sealed class ApplyResult
        {
            public int AppliedCount;
            public int ReportOnly;
            public readonly List<string> Errors = new List<string>();
            public readonly Dictionary<string, int> MissingParams = new Dictionary<string, int>();
            public readonly Dictionary<string, int> TypeMismatch = new Dictionary<string, int>();
            public readonly List<string> Unmatched = new List<string>();
        }

        private static ApplyResult ApplyToSpaces(Document doc, IReadOnlyList<TomatMatchRow> rows,
            string supplySystemParam, string exhaustSystemParam,
            string supplyAirflowParam, string exhaustAirflowParam)
        {
            var result = new ApplyResult();
            var matched = rows.Where(r => r.IsMatched).ToList();

            if (matched.Count == 0)
            {
                System.Windows.MessageBox.Show("Не сопоставлено ни одного помещения — нечего записывать.\n" +
                    "Сопоставьте помещения с пространствами вручную и повторите.",
                    "JTOOLS: Импорт .tomat", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return result;
            }

            using (var tx = new Transaction(doc, "Импорт .tomat: системы и расходы"))
            {
                tx.Start();

                foreach (var row in matched)
                {
                    var space = doc.GetElement(new ElementId(row.SelectedSpace.ElementId));
                    if (space == null)
                    {
                        result.Errors.Add(row.DisplayName + ": пространство не найдено в модели");
                        continue;
                    }

                    bool hasData =
                        WriteText(space, result, supplySystemParam, row.Room.SupplySystemId) |
                        WriteText(space, result, exhaustSystemParam, row.Room.ExhaustSystemId) |
                        WriteNumber(space, result, supplyAirflowParam, row.Room.SupplyM3H) |
                        WriteNumber(space, result, exhaustAirflowParam, row.Room.ExhaustM3H);

                    if (hasData) result.AppliedCount++;
                    else result.ReportOnly++;

                    GrdLog.Log("GrdTomatImport: '" + row.DisplayName + "' -> space " + row.SelectedSpace.Label +
                               (hasData ? " written" : " no data"));
                }

                tx.Commit();
            }

            ShowReport(rows.Count, matched.Count, result);
            return result;
        }

        /// <summary>Записывает текстовый параметр (система). Возвращает true, если запись выполнена.</summary>
        private static bool WriteText(Element space, ApplyResult result, string paramName, string value)
        {
            if (string.IsNullOrWhiteSpace(paramName)) return false;
            if (string.IsNullOrWhiteSpace(value)) return false;

            var param = space.LookupParameter(paramName);
            if (param == null)
            {
                IncCount(result.MissingParams, paramName);
                return false;
            }
            if (param.IsReadOnly)
            {
                IncCount(result.TypeMismatch, paramName);
                return false;
            }
            if (param.StorageType != StorageType.String)
            {
                IncCount(result.TypeMismatch, paramName);
                return false;
            }
            param.Set(value);
            return true;
        }

        /// <summary>Записывает числовой параметр (расход, м³/ч). Возвращает true, если запись выполнена.</summary>
        private static bool WriteNumber(Element space, ApplyResult result, string paramName, double? value)
        {
            if (string.IsNullOrWhiteSpace(paramName)) return false;
            if (!value.HasValue) return false;

            var param = space.LookupParameter(paramName);
            if (param == null)
            {
                IncCount(result.MissingParams, paramName);
                return false;
            }
            if (param.IsReadOnly)
            {
                IncCount(result.TypeMismatch, paramName);
                return false;
            }

            switch (param.StorageType)
            {
                case StorageType.Double:
                    param.Set(value.Value);
                    return true;
                case StorageType.Integer:
                    param.Set((int)Math.Round(value.Value));
                    return true;
                case StorageType.String:
                    param.Set(value.Value.ToString("0.###", CultureInfo.InvariantCulture));
                    return true;
                default:
                    IncCount(result.TypeMismatch, paramName);
                    return false;
            }
        }

        /// <summary>Инкремент счётчика в словаре.</summary>
        private static void IncCount(Dictionary<string, int> dict, string key)
        {
            dict.TryGetValue(key, out var v);
            dict[key] = v + 1;
        }

        private static void ShowReport(int totalRows, int matchedCount, ApplyResult result)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("Записано помещений: ").Append(result.AppliedCount).Append(" из ").Append(totalRows).AppendLine();
            if (result.ReportOnly > 0)
            {
                sb.Append("Сопоставлено, но в .tomat нет данных воздухообмена: ")
                  .AppendLine(result.ReportOnly.ToString(CultureInfo.InvariantCulture));
            }

            int unmatched = totalRows - matchedCount;
            if (unmatched > 0)
            {
                sb.Append("Не сопоставлено (не записывались): ").AppendLine(unmatched.ToString(CultureInfo.InvariantCulture));
            }

            if (result.MissingParams.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Параметров нет у пространств (не записано):");
                foreach (var kv in result.MissingParams.OrderBy(k => k.Key))
                    sb.Append("  «").Append(kv.Key).Append("» — ").AppendLine(kv.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (result.TypeMismatch.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Параметры с несовместимым типом значения:");
                foreach (var kv in result.TypeMismatch.OrderBy(k => k.Key))
                    sb.Append("  «").Append(kv.Key).Append("» — ").AppendLine(kv.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (result.Errors.Count > 0)
            {
                sb.AppendLine();
                sb.Append("Ошибок: ").AppendLine(result.Errors.Count.ToString(CultureInfo.InvariantCulture));
                if (result.Errors.Count <= 10) foreach (var e in result.Errors) sb.Append("  ").AppendLine(e);
            }

            GrdLog.Log("GrdTomatImport: report: " + sb.ToString());
            System.Windows.MessageBox.Show(sb.ToString(),
                "JTOOLS: Импорт .tomat", System.Windows.MessageBoxButton.OK,
                result.AppliedCount == 0 ? System.Windows.MessageBoxImage.Warning : System.Windows.MessageBoxImage.Information);
        }
    }
}