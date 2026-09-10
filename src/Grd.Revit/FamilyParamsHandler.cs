using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GrdRevit.Core;

namespace GrdRevit.Revit
{
    /// <summary>Результат применения операций с параметрами к семействам.</summary>
    public sealed class FamilyParamsResult
    {
        public List<string> Summary = new List<string>();
        public List<string> Errors = new List<string>();
        public int TotalFamilies;
        public int AppliedFamilies;
    }

    /// <summary>
    /// Применяет операции с параметрами (добавить/удалить) к выбранным семействам
    /// активного документа. Каждое семейство редактируется через Document
    /// (Document.EditFamily) и загружается обратно (LoadFamily) в отдельной транзакции.
    /// Общие параметры добавляются из указанного файла общих параметров (.txt).
    /// </summary>
    public class FamilyParamsHandler : IExternalEventHandler
    {
        private readonly object _sync = new object();
        private List<string> _familyNames;
        private List<FamilyParamOp> _ops;
        private string _sharedFilePath;
        private Action<FamilyParamsResult> _callback;

        public void Queue(List<string> familyNames, List<FamilyParamOp> ops, string sharedFilePath, Action<FamilyParamsResult> callback)
        {
            lock (_sync)
            {
                _familyNames = familyNames ?? new List<string>();
                _ops = ops ?? new List<FamilyParamOp>();
                _sharedFilePath = sharedFilePath ?? string.Empty;
                _callback = callback;
            }
        }

        public string GetName()
        {
            return "Audytor: изменить параметры семейств";
        }

        public void Execute(UIApplication app)
        {
            List<string> names;
            List<FamilyParamOp> ops;
            string path;
            Action<FamilyParamsResult> cb;
            lock (_sync)
            {
                names = _familyNames;
                ops = _ops;
                path = _sharedFilePath;
                cb = _callback;
                _familyNames = null;
                _ops = null;
                _sharedFilePath = null;
                _callback = null;
            }
            if (cb == null)
            {
                GrdLog.Log("FamilyParamsHandler.Execute: нет запроса в очереди");
                return;
            }

            var result = new FamilyParamsResult();
            try
            {
                result = Apply(app, names, ops, path);
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyParamsHandler.Execute: EXCEPTION: " + ex);
                result.Errors.Add("Ошибка: " + ex.Message);
            }

            GrdLog.Log("FamilyParamsHandler: применено " + result.AppliedFamilies + " из " + result.TotalFamilies +
                       ", ошибок " + result.Errors.Count);
            try { cb(result); }
            catch (Exception ex) { GrdLog.Log("FamilyParamsHandler.Execute: callback EXCEPTION: " + ex); }
        }

        private static FamilyParamsResult Apply(UIApplication app, List<string> names, List<FamilyParamOp> ops, string sharedPath)
        {
            var result = new FamilyParamsResult();
            var uiDoc = app?.ActiveUIDocument;
            if (uiDoc?.Document == null)
            {
                result.Errors.Add("Нет активного документа Revit.");
                result.TotalFamilies = names == null ? 0 : names.Count;
                return result;
            }
            var doc = uiDoc.Document;
            result.TotalFamilies = names == null ? 0 : names.Count;
            if (result.TotalFamilies == 0)
            {
                result.Errors.Add("Не выбрано ни одного семейства.");
                return result;
            }
            if (ops == null || ops.Count == 0)
            {
                result.Errors.Add("Не задано ни одной операции с параметрами.");
                return result;
            }

            // Файл общих параметров для сеанса: указываем заранее, чтобы общие параметры
            // добавлялись из него, а новые определения попадали в правильный файл.
            if (!string.IsNullOrEmpty(sharedPath) && System.IO.File.Exists(sharedPath))
            {
                try { app.Application.SharedParametersFilename = sharedPath; }
                catch (Exception ex)
                {
                    // Файл уже задан или недоступен — продолжаем, Revit сам разберётся.
                    GrdLog.Log("FamilyParamsHandler: SharedParametersFilename: " + ex.Message);
                }
            }

            var loadOpts = new FamilyLoadOptionsImpl();
            foreach (var familyName in names)
            {
                var line = ApplyOne(doc, app, familyName, ops, loadOpts);
                if (line.StartsWith("!")) result.Errors.Add(line);
                else { result.AppliedFamilies++; result.Summary.Add(line); }
            }
            return result;
        }

        private static string ApplyOne(Document doc, UIApplication app, string familyName, List<FamilyParamOp> ops, IFamilyLoadOptions loadOpts)
        {
            Family family = null;
            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Family)))
            {
                var f = e as Family;
                if (f == null || string.IsNullOrEmpty(f.Name)) continue;
                if (string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase))
                {
                    family = f;
                    break;
                }
            }
            if (family == null) return "! Семейство «" + familyName + "» не найдено в документе.";

            Document fdoc;
            try { fdoc = doc.EditFamily(family); }
            catch (Exception ex)
            {
                return "! «" + familyName + "»: не удалось открыть семейство: " + ex.Message;
            }

            var added = new List<string>();
            var removed = new List<string>();
            var skipped = new List<string>();
            try
            {
                // Изменение параметров в документе семейства ОБЯЗАНО идти внутри
                // транзакции этого документа. Иначе AddParameter/RemoveParameter/
                // MakeInstance оставляют «висячие» правки: сводка показывает «добавлено»,
                // но SaveAs их не фиксирует, и после LoadFamily параметра в семействе нет.
                using (var ft = new Transaction(fdoc, "Audytor: параметры «" + familyName + "»"))
                {
                    ft.Start();
                    try
                    {
                        foreach (var op in ops)
                        {
                            if (op == null || string.IsNullOrEmpty(op.Name)) continue;
                            try
                            {
                                ApplyOp(fdoc, app, op, added, removed, skipped);
                            }
                            catch (Exception ex)
                            {
                                skipped.Add(op.Name + " («" + ex.Message + "»)");
                            }
                        }
                    }
                    catch
                    {
                        try { ft.RollBack(); } catch { }
                        throw;
                    }
                    ft.Commit();
                }

                GrdLog.Log("FamilyParamsHandler: после правок в семействе «" + familyName +
                           "» параметров=" + fdoc.FamilyManager?.GetParameters().Count +
                           ", OwnerFamily=" + (fdoc.OwnerFamily?.Name ?? "<null>") +
                           ", PathName=" + (fdoc.PathName ?? "<н/з>"));

                string tempPath = null;
                bool loaded = false;
                var beforeNames = FamilyNames(doc);
                GrdLog.Log("FamilyParamsHandler: до LoadFamily семейств=" + beforeNames.Count +
                           ": " + string.Join("; ", beforeNames));
                using (var t = new Transaction(doc, "Audytor: параметры семейства «" + familyName + "»"))
                {
                    t.Start();
                    try
                    {
                        // LoadFamily(Document, options) нельзя вызывать на модифицированном
                        // документе семейства («The document must not be modifiable...»).
                        // Поэтому сохраняем семейство во временный файл и загружаем по пути.
                        // ВАЖНО: имя временного файла должно совпадать с именем семейства
                        // в проекте — Revit именует «новое» семейство по имени RFA-файла,
                        // иначе LoadFamily создаст семейство с именем файла (Audytor_<guid>)
                        // вместо замены существующего.
                        tempPath = System.IO.Path.Combine(
                            System.IO.Path.GetTempPath(),
                            SafeFamilyFileName(familyName));
                        if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath);
                        fdoc.SaveAs(tempPath, new SaveAsOptions());
                        loaded = doc.LoadFamily(tempPath, loadOpts, out var loadedFamily);
                        GrdLog.Log("FamilyParamsHandler: LoadFamily вернул " + loaded +
                                   ", новое семейство: Name=" + (loadedFamily?.Name ?? "<null>") +
                                   ", Id=" + loadedFamily?.Id);
                        t.Commit();
                    }
                    catch
                    {
                        try { t.RollBack(); } catch { }
                        throw;
                    }
                    finally
                    {
                        if (!string.IsNullOrEmpty(tempPath))
                        {
                            try
                            {
                                if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath);
                            }
                            catch { }
                        }
                    }
                }
                if (!loaded)
                    return "! «" + familyName + "»: LoadFamily вернул false";

                VerifyAfterLoad(doc, familyName, beforeNames, added);
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyParamsHandler: «" + familyName + "» EXCEPTION: " + ex);
                return "! «" + familyName + "»: " + ex.Message;
            }
            finally
            {
                try { fdoc.Dispose(); } catch { }
            }

            var parts = new List<string>();
            if (added.Count > 0) parts.Add("добавлено: " + string.Join(", ", added));
            if (removed.Count > 0) parts.Add("удалено: " + string.Join(", ", removed));
            if (skipped.Count > 0) parts.Add("пропущено: " + string.Join(", ", skipped));
            return "«" + familyName + "»: " + (parts.Count > 0 ? string.Join("; ", parts) : "нет изменений");
        }

        /// <summary>
        /// Диагностика после LoadFamily: сколько семейств в документе, сколько из них
        /// с тем же именем, какие новые имена появились (если LoadFamily создал
        /// семейство под другим именем), сколько пользовательских параметров видно
        /// и какие из добавленных присутствуют.
        /// </summary>
        private static void VerifyAfterLoad(Document doc, string familyName, List<string> beforeNames, List<string> added)
        {
            try
            {
                var afterNames = FamilyNames(doc);
                var appended = afterNames.Except(beforeNames, StringComparer.OrdinalIgnoreCase).ToList();
                var removed = beforeNames.Except(afterNames, StringComparer.OrdinalIgnoreCase).ToList();

                int famCount = afterNames.Count;
                int nameMatches = 0;
                var userParams = new List<string>();
                Family matched = null;
                foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Family)))
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    if (!string.Equals(e.Name, familyName, StringComparison.OrdinalIgnoreCase)) continue;
                    nameMatches++;
                    if (nameMatches > 1) continue;
                    try
                    {
                        var ids = (e as Family)?.GetFamilySymbolIds();
                        if (ids == null || ids.Count == 0) continue;
                        var sym = doc.GetElement(ids.First()) as FamilySymbol;
                        if (sym?.Parameters == null) continue;
                        foreach (Parameter p in sym.Parameters)
                        {
                            if (p?.Definition == null) continue;
                            if (p.Definition is InternalDefinition id && id.BuiltInParameter != BuiltInParameter.INVALID) continue;
                            if (!string.IsNullOrEmpty(p.Definition.Name)) userParams.Add(p.Definition.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        GrdLog.Log("FamilyParamsHandler.Verify: параметры «" + familyName + "» EXCEPTION: " + ex);
                    }
                }
                var present = added.Where(n => userParams.Any(u =>
                    string.Equals(u, n, StringComparison.OrdinalIgnoreCase))).ToList();
                GrdLog.Log("FamilyParamsHandler.Verify: семейств=" + famCount +
                           " (было " + beforeNames.Count + "), " +
                           (appended.Count > 0
                               ? "ПОЯВИЛИСЬ семейства: " + string.Join("; ", appended) + "; "
                               : "новых семейств нет; ") +
                           (removed.Count > 0 ? "исчезли: " + string.Join("; ", removed) + "; " : string.Empty) +
                           "находок по имени «" + familyName + "»=" + nameMatches +
                           (nameMatches > 1 ? " (ВНИМАНИЕ: LoadFamily создал дубль семейства)" : string.Empty) +
                           ", пользовательских параметров=" + userParams.Count +
                           ", из добавленных присутствует: " + (present.Count > 0 ? string.Join(", ", present) : "<нет>"));
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyParamsHandler.Verify EXCEPTION: " + ex);
            }
        }

        /// <summary>Имя временного RFA-файла = имя семейства (+.rfa), с заменой
        /// недопустимых в имени файла символов.</summary>
        private static string SafeFamilyFileName(string familyName)
        {
            var s = string.IsNullOrEmpty(familyName) ? "Family" : familyName;
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s + ".rfa";
        }

        /// <summary>Отсортированный список имён всех семейств документа.</summary>
        private static List<string> FamilyNames(Document doc)
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Family)))
            {
                if (!string.IsNullOrEmpty(e.Name)) names.Add(e.Name);
            }
            return names.ToList();
        }

        private static void ApplyOp(Document fdoc, UIApplication app, FamilyParamOp op,
            List<string> added, List<string> removed, List<string> skipped)
        {
            var fm = fdoc.FamilyManager;
            if (op.Remove)
            {
                var existing = FindParam(fm, op);
                if (existing == null)
                {
                    skipped.Add(op.Name + " (не найден)");
                    return;
                }
                fm.RemoveParameter(existing);
                removed.Add(op.Name);
                return;
            }

            if (string.Equals(op.Status, "Изменить привязку", StringComparison.OrdinalIgnoreCase))
            {
                var existing = FindParam(fm, op);
                if (existing == null)
                {
                    skipped.Add(op.Name + " (не найден)");
                    return;
                }
                if (existing.IsInstance == op.IsInstance)
                {
                    skipped.Add(op.Name + " (уже " + (op.IsInstance ? "экземпляр" : "тип") + ")");
                    return;
                }
                try
                {
                    if (op.IsInstance) fm.MakeInstance(existing);
                    else fm.MakeType(existing);
                    removed.Add(op.Name + " (" + (op.IsInstance ? "тип→экземпляр" : "экземпляр→тип") + ")");
                }
                catch (Exception ex)
                {
                    skipped.Add(op.Name + " (ошибка: " + ex.Message + ")");
                }
                return;
            }

            FamilyParameter fp = op.Source == ParamSourceKind.Shared
                ? AddShared(fdoc, app, op)
                : AddFamily(fdoc, op);
            if (fp != null) added.Add(fp.Definition?.Name ?? op.Name);
        }

        private static FamilyParameter AddFamily(Document fdoc, FamilyParamOp op)
        {
            var fm = fdoc.FamilyManager;
            var gt = GroupFor(op.Group);
            switch (op.StorageType)
            {
                case "Целое":
                    return fm.AddParameter(op.Name, gt, SpecTypeId.Int.Integer, op.IsInstance);
                case "Число":
                    return fm.AddParameter(op.Name, gt, SpecTypeId.Number, op.IsInstance);
                case "Длина":
                    return fm.AddParameter(op.Name, gt, SpecTypeId.Length, op.IsInstance);
                case "Площадь":
                    return fm.AddParameter(op.Name, gt, SpecTypeId.Area, op.IsInstance);
                case "Объём":
                    return fm.AddParameter(op.Name, gt, SpecTypeId.Volume, op.IsInstance);
                default:
                    return fm.AddParameter(op.Name, gt, SpecTypeId.String.Text, op.IsInstance);
            }
        }

        private static FamilyParameter AddShared(Document fdoc, UIApplication app, FamilyParamOp op)
        {
            var spf = app.Application.OpenSharedParameterFile();
            if (spf == null)
                throw new InvalidOperationException("Файл общих параметров не открыт: загрузите файл общих параметров (.txt) в окне");

            var def = FindSharedDef(spf, op);
            if (def == null)
            {
                var groupName = string.IsNullOrEmpty(op.Group) ? "Параметры" : op.Group;
                var grp = spf.Groups.Create(groupName);
                var options = new ExternalDefinitionCreationOptions(op.Name, SharedStorage(op.StorageType))
                {
                    GUID = string.IsNullOrEmpty(op.SharedGuid)
                        ? System.Guid.NewGuid()
                        : System.Guid.Parse(op.SharedGuid)
                };
                def = grp.Definitions.Create(options) as ExternalDefinition;
                if (def == null)
                    throw new InvalidOperationException("Не удалось создать определение общего параметра «" + op.Name + "»");
            }

            return fdoc.FamilyManager.AddParameter(def, GroupFor(op.Group), op.IsInstance);
        }

        private static ExternalDefinition FindSharedDef(DefinitionFile file, FamilyParamOp op)
        {
            foreach (DefinitionGroup g in file.Groups)
            {
                if (g == null) continue;
                foreach (Definition d in g.Definitions)
                {
                    var ext = d as ExternalDefinition;
                    if (ext == null) continue;
                    if (System.Guid.TryParse(op.SharedGuid, out var guid) &&
                        guid != System.Guid.Empty && ext.GUID == guid) return ext;
                    if (string.Equals(ext.Name, op.Name, StringComparison.OrdinalIgnoreCase)) return ext;
                }
            }
            return null;
        }

        private static FamilyParameter FindParam(FamilyManager fm, FamilyParamOp op)
        {
            foreach (FamilyParameter fp in fm.GetParameters())
            {
                if (fp?.Definition == null) continue;
                if (string.Equals(fp.Definition.Name, op.Name, StringComparison.OrdinalIgnoreCase)) return fp;
            }
            return null;
        }

        private static ForgeTypeId SharedStorage(string storageType)
        {
            switch (storageType)
            {
                case "Целое": return SpecTypeId.Int.Integer;
                case "Число": return SpecTypeId.Number;
                case "Длина": return SpecTypeId.Length;
                case "Площадь": return SpecTypeId.Area;
                case "Объём": return SpecTypeId.Volume;
                default: return SpecTypeId.String.Text;
            }
        }

        /// <summary>Группа параметра в свойствах семейства (ForgeTypeId).</summary>
        private static ForgeTypeId GroupFor(string group)
        {
            if (string.IsNullOrEmpty(group)) return GroupTypeId.General;
            switch (group.Trim().ToLowerInvariant())
            {
                case "данные": return GroupTypeId.Data;
                case "текст": return GroupTypeId.Text;
                case "конструкция": return GroupTypeId.Construction;
                case "механика": return GroupTypeId.Mechanical;
                case "электрика": return GroupTypeId.Electrical;
                default: return GroupTypeId.General;
            }
        }

        private sealed class FamilyLoadOptionsImpl : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }
    }
}
