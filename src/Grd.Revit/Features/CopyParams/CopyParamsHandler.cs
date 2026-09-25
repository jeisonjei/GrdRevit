using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace GrdRevit.Revit
{
    /// <summary>Значение параметра у конкретного типа семейства (для копирования).</summary>
    public sealed class CopyTypeValue
    {
        public string TypeName = string.Empty;
        public string Value = string.Empty;
    }

    /// <summary>Строка параметра семейства для окна «Скопировать параметры».</summary>
    public sealed class CopyParamItem : System.ComponentModel.INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;
        public bool IsShared { get; set; }
        public bool IsInstance { get; set; }
        public string StorageType { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public bool HasFormula { get; set; }
        public string Guid { get; set; } = string.Empty;
        /// <summary>Отображение: значение первого типа; «⟨несколько значений⟩», если различаются.</summary>
        public string Value { get; set; } = string.Empty;
        /// <summary>Устанавливает строку отображения и уведомляет окно (колонка «Значение»).</summary>
        public void SetDisplayValue(string value)
        {
            if (string.Equals(Value, value, StringComparison.Ordinal)) return;
            Value = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Value)));
        }
        /// <summary>Подробные значения по типам (для подсказки и копирования).</summary>
        public List<CopyTypeValue> TypeValues { get; set; } = new List<CopyTypeValue>();

        private bool _isSelected = true;
        /// <summary>Отмечен для копирования (колонка-чекбокс в окне).</summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>Результат чтения/копирования параметров семейства.</summary>
    public sealed class CopyParamsResult
    {
        public string Error;
        public string FamilyName = string.Empty;
        public string Scope = string.Empty;
        public List<CopyParamItem> Params = new List<CopyParamItem>();
        public List<string> Types = new List<string>();
        public List<string> Summary = new List<string>();
        public List<string> Errors = new List<string>();
        public int CopyCount;
    }

    /// <summary>
    /// Читает параметры семейства, добавляет отсутствующие/меняет привязку и копирует
    /// значения в целевое семейство, а также снимает формулы. Вся работа — в Revit-потоке
    /// через ExternalEvent (моделесс-окно не может обращаться к API напрямую).
    /// </summary>
    public class CopyParamsHandler : IExternalEventHandler
    {
        private readonly object _sync = new object();
        private readonly Queue<Request> _requests = new Queue<Request>();

        private enum Kind { Read, Apply, ClearFormula }

        private sealed class Request
        {
            public Kind Kind;
            public string SourceName = string.Empty;
            public string TargetName = string.Empty;
            public string SourceType = string.Empty;
            public string TargetType = string.Empty;
            public string ParamName = string.Empty;
            public List<CopyParamItem> Items = new List<CopyParamItem>();
            public Action<CopyParamsResult> Callback;
        }

        public void QueueRead(string familyName, Action<CopyParamsResult> callback)
        {
            lock (_sync)
            {
                _requests.Enqueue(new Request
                {
                    Kind = Kind.Read,
                    SourceName = familyName ?? string.Empty,
                    Callback = callback
                });
            }
        }

        public void QueueApply(string sourceName, string targetName, List<CopyParamItem> items, Action<CopyParamsResult> callback)
        {
            QueueApply(sourceName, targetName, string.Empty, string.Empty, items, callback);
        }

        public void QueueApply(string sourceName, string targetName, string sourceType, string targetType,
            List<CopyParamItem> items, Action<CopyParamsResult> callback)
        {
            lock (_sync)
            {
                _requests.Enqueue(new Request
                {
                    Kind = Kind.Apply,
                    SourceName = sourceName ?? string.Empty,
                    TargetName = targetName ?? string.Empty,
                    SourceType = sourceType ?? string.Empty,
                    TargetType = targetType ?? string.Empty,
                    Items = items ?? new List<CopyParamItem>(),
                    Callback = callback
                });
            }
        }

        public void QueueClearFormula(string familyName, string paramName, Action<CopyParamsResult> callback)
        {
            lock (_sync)
            {
                _requests.Enqueue(new Request
                {
                    Kind = Kind.ClearFormula,
                    TargetName = familyName ?? string.Empty,
                    ParamName = paramName ?? string.Empty,
                    Callback = callback
                });
            }
        }

        public string GetName()
        {
            return "JTOOLS: скопировать параметры между семействами";
        }

        public void Execute(UIApplication app)
        {
            List<Request> batch;
            lock (_sync)
            {
                if (_requests.Count == 0)
                {
                    GrdLog.Log("CopyParamsHandler.Execute: нет запросов в очереди");
                    return;
                }
                batch = new List<Request>(_requests);
                _requests.Clear();
            }

            foreach (var req in batch)
            {
                try
                {
                    CopyParamsResult res;
                    switch (req.Kind)
                    {
                        case Kind.Read:
                            res = Read(app, req.SourceName);
                            break;
                        case Kind.Apply:
                            res = Apply(app, req.SourceName, req.TargetName, req.SourceType, req.TargetType, req.Items);
                            break;
                        default:
                            res = ClearFormula(app, req.TargetName, req.ParamName);
                            break;
                    }
                    req.Callback?.Invoke(res);
                }
                catch (Exception ex)
                {
                    GrdLog.Log("CopyParamsHandler.Execute: EXCEPTION: " + ex);
                    try { req.Callback?.Invoke(new CopyParamsResult { Error = "Ошибка: " + ex.Message }); }
                    catch { }
                }
            }
        }

        // ------------------------------------------------------------------ READ

        private static CopyParamsResult Read(UIApplication app, string familyName)
        {
            var res = new CopyParamsResult { FamilyName = familyName };
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null)
            {
                res.Error = "Нет активного документа Revit.";
                return res;
            }
            var family = FindFamily(doc, familyName);
            if (family == null)
            {
                res.Error = "Семейство «" + familyName + "» не найдено в документе.";
                return res;
            }

            Document fdoc = null;
            try
            {
                fdoc = doc.EditFamily(family);
                var fm = fdoc.FamilyManager;
                if (fm == null)
                {
                    res.Error = "У семейства «" + familyName + "» нет диспетчера параметров.";
                    return res;
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FamilyParameter fp in fm.GetParameters())
                {
                    if (fp?.Definition == null) continue;
                    if (!seen.Add(fp.Definition.Name)) continue;
                    var item = new CopyParamItem
                    {
                        Name = fp.Definition.Name,
                        IsShared = fp.IsShared,
                        IsInstance = fp.IsInstance,
                        StorageType = DescribeType(fp.Definition),
                        Group = GroupName(fp.Definition),
                        HasFormula = HasFormulaIn(fm, fp),
                        Guid = fp.IsShared ? fp.GUID.ToString() : string.Empty
                    };
                    if (!fp.IsInstance)
                        CollectTypeValues(fm, fp, item);
                    else
                        item.Value = "—";
                    res.Params.Add(item);
                }
                GrdLog.Log("CopyParamsHandler.Read: «" + familyName + "» параметров=" + res.Params.Count +
                           ", значений отображено=" +
                           res.Params.Count(p => !string.IsNullOrEmpty(p.Value) && p.Value != "—") +
                           " из " + res.Params.Count(p => !p.IsInstance));
                if (fm.Types != null)
                {
                    var typeNames = new List<string>();
                    foreach (FamilyType t in fm.Types)
                        if (t != null && !string.IsNullOrEmpty(t.Name)) typeNames.Add(t.Name);
                    res.Types = typeNames
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
                    GrdLog.Log("CopyParamsHandler.Read: «" + familyName + "» типов=" + res.Types.Count);
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("CopyParamsHandler.Read «" + familyName + "» EXCEPTION: " + ex);
                res.Error = "Не удалось открыть семейство «" + familyName + "»: " + ex.Message;
            }
            finally
            {
                try { fdoc?.Dispose(); } catch { }
            }
            return res;
        }

        /// <summary>Значения типового параметра по всем типам семейства + отображаемая строка.</summary>
        private static void CollectTypeValues(FamilyManager fm, FamilyParameter fp, CopyParamItem item)
        {
            try
            {
                if (fm.Types == null) return;
                var raw = new List<string>();
                int types = 0;
                int failed = 0;
                foreach (FamilyType type in fm.Types)
                {
                    if (type == null) continue;
                    types++;
                    try
                    {
                        // Не полагаемся на FamilyManager.CurrentType: чтение значения
                        // методом FamilyType.As* работает без него (так же делается при
                        // копировании в Apply).
                        string v = TypeRawString(type, fp, item.StorageType);
                        item.TypeValues.Add(new CopyTypeValue { TypeName = type.Name ?? string.Empty, Value = v });
                        if (!string.IsNullOrEmpty(v)) raw.Add(v);
                    }
                    catch (Exception ex)
                    {
                        if (failed++ == 0)
                            GrdLog.Log("CopyParamsHandler.CollectTypeValues «" + (fp.Definition?.Name ?? "?") +
                                       "» тип «" + (type.Name ?? "?") + "» EXCEPTION: " + ex.Message);
                    }
                }
                var distinct = raw.Distinct(StringComparer.Ordinal).ToList();
                item.Value = distinct.Count <= 1
                    ? (raw.Count > 0 ? raw[0] : string.Empty)
                    : "⟨несколько значений⟩";
                if (types > 0 && raw.Count == 0)
                    GrdLog.Log("CopyParamsHandler.CollectTypeValues «" + (fp.Definition?.Name ?? "?") +
                               "»: типов=" + types + ", значений прочитано=0 (failed=" + failed + ")");
            }
            catch (Exception ex)
            {
                GrdLog.Log("CopyParamsHandler.CollectTypeValues EXCEPTION: " + ex);
            }
        }

        /// <summary>Чтение значения по типу значения (по сырому типу данных).</summary>
        private static string TypeRawString(FamilyType type, FamilyParameter fp, string storage)
        {
            try
            {
                if (storage == "Текст") return type.AsString(fp) ?? string.Empty;
                if (storage == "Целое")
                {
                    var v = type.AsInteger(fp);
                    return v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
                }
                if (storage == "Число" || storage == "Длина" || storage == "Площадь" || storage == "Объём")
                {
                    var v = type.AsDouble(fp);
                    return v.HasValue ? v.Value.ToString("0.####", CultureInfo.InvariantCulture) : string.Empty;
                }
                var s = type.AsValueString(fp);
                return string.IsNullOrEmpty(s) ? string.Empty : s;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>Есть ли у параметра формула хотя бы у одного типа семейства.</summary>
        private static bool HasFormulaIn(FamilyManager fm, FamilyParameter fp)
        {
            try
            {
                if (fm == null || fp == null) return false;
                if (fm.Types == null) return fp.IsDeterminedByFormula;
                bool any = false;
                foreach (FamilyType type in fm.Types)
                {
                    if (type == null) continue;
                    any = true;
                    try
                    {
                        fm.CurrentType = type;
                        if (fp.IsDeterminedByFormula) return true;
                    }
                    catch { }
                }
                return !any && fp.IsDeterminedByFormula;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ APPLY

        private static CopyParamsResult Apply(UIApplication app, string sourceName, string targetName,
            string sourceType, string targetType, List<CopyParamItem> items)
        {
            var res = new CopyParamsResult { FamilyName = targetName };
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null)
            {
                res.Errors.Add("Нет активного документа Revit.");
                return res;
            }
            if (items == null || items.Count == 0)
            {
                res.Errors.Add("Не отмечено ни одного параметра для копирования.");
                return res;
            }
            if (string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(targetName))
            {
                res.Errors.Add("Выберите семейство-источник и целевое семейство.");
                return res;
            }
            if (string.Equals(sourceName, targetName, StringComparison.OrdinalIgnoreCase))
            {
                res.Errors.Add("Семейство-источник и целевое семейство должны различаться.");
                return res;
            }

            var srcFamily = FindFamily(doc, sourceName);
            var tgtFamily = FindFamily(doc, targetName);
            if (srcFamily == null || tgtFamily == null)
            {
                res.Errors.Add((srcFamily == null ? sourceName : targetName) + ": не найдено в документе.");
                return res;
            }
            if (tgtFamily.IsInPlace)
            {
                res.Errors.Add("Целевое семейство «" + targetName + "» — встроенное (in-place): отредактировать файл семейства нельзя.");
                return res;
            }
            if (!tgtFamily.IsEditable)
            {
                res.Errors.Add("Целевое семейство «" + targetName + "» недоступно для редактирования.");
                return res;
            }

            Document srcDoc = null;
            Document tgtDoc = null;
            string tempPath = null;
            int copied = 0;
            var unmatchedTypes = new List<string>();
            try
            {
                srcDoc = doc.EditFamily(srcFamily);
                tgtDoc = doc.EditFamily(tgtFamily);
                var srcFm = srcDoc.FamilyManager;
                var tgtFm = tgtDoc.FamilyManager;
                if (tgtFm == null)
                {
                    res.Errors.Add("У целевого семейства «" + targetName + "» нет диспетчера параметров.");
                    return res;
                }

                using (var ft = new Transaction(tgtDoc, "JTOOLS: скопировать параметры («" + targetName + "»)"))
                {
                    ft.Start();
                    try
                    {
                        // 1) Обеспечить наличие каждого параметра и верную привязку экземпляр/тип.
                        foreach (var item in items)
                        {
                            if (item == null || string.IsNullOrEmpty(item.Name)) continue;
                            var existing = FindParam(tgtFm, item.Name);
                            if (existing == null)
                            {
                                var added = AddParam(app, tgtFm, item);
                                if (added != null)
                                {
                                    res.Summary.Add("«" + item.Name + "»: добавлен (" + (item.IsInstance ? "экземпляр" : "тип") + ")");
                                    GrdLog.Log("CopyParams.Apply: добавлен «" + item.Name + "» в «" + targetName + "»");
                                }
                                else
                                {
                                    res.Errors.Add("«" + item.Name + "»: не удалось добавить в целевое семейство.");
                                }
                            }
                            else if (existing.IsInstance != item.IsInstance)
                            {
                                try
                                {
                                    if (item.IsInstance) tgtFm.MakeInstance(existing);
                                    else tgtFm.MakeType(existing);
                                    res.Summary.Add("«" + item.Name + "»: привязка изменена (" +
                                        (item.IsInstance ? "тип→экземпляр" : "экземпляр→тип") + ")");
                                }
                                catch (Exception ex)
                                {
                                    res.Errors.Add("«" + item.Name + "»: не удалось сменить привязку (" + ex.Message + ")");
                                }
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

                // 2) Скопировать значения ТИПОВЫХ параметров. Если выбраны конкретные
                //    типы источника и цели — только между ними; иначе — копирование
                //    по совпадающим именам типов.
                using (var ft = new Transaction(tgtDoc, "JTOOLS: значения параметров («" + targetName + "»)"))
                {
                    ft.Start();
                    try
                    {
                        var srcTypes = new SortedDictionary<string, FamilyType>(StringComparer.OrdinalIgnoreCase);
                        if (srcFm?.Types != null)
                            foreach (FamilyType t in srcFm.Types)
                                if (t?.Name != null && !srcTypes.ContainsKey(t.Name)) srcTypes[t.Name] = t;

                        bool specific = !string.IsNullOrEmpty(sourceType) && !string.IsNullOrEmpty(targetType);

                        foreach (var item in items)
                        {
                            if (item == null || string.IsNullOrEmpty(item.Name)) continue;
                            if (item.IsInstance) continue; // значения экземпляра хранятся в проекте, не в семействе
                            var tgtFp = FindParam(tgtFm, item.Name);
                            if (tgtFp == null || tgtFp.IsInstance) continue;
                            var srcFp = FindParam(srcFm, item.Name);
                            if (srcFp == null) continue;

                            if (tgtFm.Types == null) continue;
                            foreach (FamilyType tgtType in tgtFm.Types)
                            {
                                if (tgtType?.Name == null) continue;
                                if (specific && !string.Equals(tgtType.Name, targetType, StringComparison.OrdinalIgnoreCase)) continue;
                                FamilyType srcType;
                                if (specific)
                                {
                                    if (!srcTypes.TryGetValue(sourceType, out srcType))
                                    {
                                        unmatchedTypes.Add(tgtType.Name);
                                        continue;
                                    }
                                }
                                else
                                {
                                    if (!srcTypes.TryGetValue(tgtType.Name, out srcType)) continue;
                                }
                                try
                                {
                                    tgtFm.CurrentType = tgtType;
                                    if (!TryCopyValue(srcFm, srcType, srcFp, tgtFm, tgtFp, item.StorageType))
                                        unmatchedTypes.Add(tgtType.Name);
                                    else
                                        copied++;
                                }
                                catch
                                {
                                    unmatchedTypes.Add(tgtType.Name);
                                }
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

                // 3) Сохранить и перезагрузить семейство. overwriteParameterValues=true:
                // значения из RFA (скопированные) должны заменить значения в проекте.
                tempPath = Path.Combine(Path.GetTempPath(), FamilyReloadFileName(tgtDoc, targetName));
                if (File.Exists(tempPath)) File.Delete(tempPath);
                tgtDoc.SaveAs(tempPath, new SaveAsOptions());

                var ctx = new LoadFailuresContext();
                bool loaded;
                using (var t = new Transaction(doc, "JTOOLS: перезагрузка семейства «" + targetName + "»"))
                {
                    try
                    {
                        var opts = t.GetFailureHandlingOptions();
                        opts.SetFailuresPreprocessor(new LoadFailuresPreprocessor(ctx));
                        t.SetFailureHandlingOptions(opts);
                    }
                    catch (Exception ex) { GrdLog.Log("CopyParams.Apply: FailureHandlingOptions " + ex.Message); }
                    t.Start();
                    try
                    {
                        loaded = doc.LoadFamily(tempPath, new CopyFamilyLoadOptions(true), out var lf);
                        t.Commit();
                    }
                    catch
                    {
                        try { t.RollBack(); } catch { }
                        throw;
                    }
                }
                if (ctx.SuppressedConstraint)
                    res.Errors.Add("Перезагрузка «" + targetName + "» требует действий Revit (связи) — семейство не изменено.");
                else if (!loaded)
                    res.Errors.Add("Семейство «" + targetName + "» не перезагрузилось — изменения не применены.");
                else
                {
                    try { doc.Regenerate(); } catch { }
                    VerifyParamPresent(doc, targetName, items, res);
                }

                res.CopyCount = copied;
                res.Summary.Add("Скопировано значений (по типам): " + copied);
                if (unmatchedTypes.Count > 0)
                    res.Summary.Add("Типы без совпадения в источнике (значения не менялись): " +
                        string.Join("; ", unmatchedTypes.Distinct(StringComparer.OrdinalIgnoreCase).Take(6)) +
                        (unmatchedTypes.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 6 ? "; и др." : string.Empty));
                GrdLog.Log("CopyParams.Apply: «" + sourceName + "» → «" + targetName + "» значений=" + copied +
                           ", строка=" + string.Join(" | ", res.Summary));
            }
            catch (Exception ex)
            {
                GrdLog.Log("CopyParamsHandler.Apply EXCEPTION: " + ex);
                res.Errors.Add("Копирование не выполнено: " + ex.Message);
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempPath))
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
                try { tgtDoc?.Dispose(); } catch { }
                try { srcDoc?.Dispose(); } catch { }
            }
            return res;
        }

        /// <summary>Скопировать значение из типа-источника в тип-приёмник. True — значение записано.</summary>
        private static bool TryCopyValue(FamilyManager srcFm, FamilyType srcType, FamilyParameter srcFp,
            FamilyManager tgtFm, FamilyParameter tgtFp, string storage)
        {
            try
            {
                if (storage == "Текст")
                {
                    var s = srcType.AsString(srcFp);
                    if (s == null) return false;
                    tgtFm.Set(tgtFp, s);
                    return true;
                }
                if (storage == "Целое")
                {
                    var v = srcType.AsInteger(srcFp);
                    if (!v.HasValue) return false;
                    tgtFm.Set(tgtFp, v.Value);
                    return true;
                }
                if (storage == "Число" || storage == "Длина" || storage == "Площадь" || storage == "Объём")
                {
                    var v = srcType.AsDouble(srcFp);
                    if (!v.HasValue) return false;
                    tgtFm.Set(tgtFp, v.Value);
                    return true;
                }
                var vs = srcType.AsValueString(srcFp);
                if (string.IsNullOrEmpty(vs)) return false;
                tgtFm.Set(tgtFp, vs);
                return true;
            }
            catch (Exception ex)
            {
                GrdLog.Log("CopyParamsHandler.TryCopyValue EXCEPTION: " + ex);
                return false;
            }
        }

        /// <summary>Добавить параметр в FamilyManager целевого семейства: shared — тем же
        /// GUID из открытого файла общих параметров (создаёт определение, если нет);
        /// обычный — по имени/типу хранения/привязке.</summary>
        private static FamilyParameter AddParam(UIApplication app, FamilyManager fm, CopyParamItem item)
        {
            if (item.IsShared)
            {
                try
                {
                    var file = app.Application.OpenSharedParameterFile();
                    if (file != null)
                    {
                        var def = FindSharedDef(file, item.Guid, item.Name);
                        if (def == null)
                        {
                            var groupName = string.IsNullOrEmpty(item.Group) ? "Параметры" : item.Group;
                            var grp = file.Groups.Create(groupName);
                            var options = new ExternalDefinitionCreationOptions(item.Name, SharedStorage(item.StorageType))
                            {
                                GUID = Guid.TryParse(item.Guid, out var g) && g != Guid.Empty ? g : Guid.NewGuid()
                            };
                            def = grp.Definitions.Create(options) as ExternalDefinition;
                            GrdLog.Log("CopyParams.AddParam: создано определение «" + item.Name + "» в общем файле");
                        }
                        if (def != null)
                            return fm.AddParameter(def, GroupFor(item.Group), item.IsInstance);
                    }
                }
                catch (Exception ex)
                {
                    GrdLog.Log("CopyParamsHandler.AddParam shared EXCEPTION: " + ex);
                }
            }
            return fm.AddParameter(item.Name, GroupFor(item.Group), FamilyStorage(item.StorageType), item.IsInstance);
        }

        // ------------------------------------------------------------------ CLEAR FORMULA

        private static CopyParamsResult ClearFormula(UIApplication app, string familyName, string paramName)
        {
            var res = new CopyParamsResult { FamilyName = familyName };
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null)
            {
                res.Errors.Add("Нет активного документа Revit.");
                return res;
            }
            var family = FindFamily(doc, familyName);
            if (family == null)
            {
                res.Errors.Add("Семейство «" + familyName + "» не найдено в документе.");
                return res;
            }
            if (family.IsInPlace)
            {
                res.Errors.Add("«" + familyName + "» — встроенное (in-place) семейство: файла семейства нет, формулу снять нельзя.");
                return res;
            }
            if (!family.IsEditable)
            {
                res.Errors.Add("«" + familyName + "» недоступно для редактирования.");
                return res;
            }

            Document fdoc = null;
            string tempPath = null;
            try
            {
                fdoc = doc.EditFamily(family);
                int cleared = 0;
                using (var ft = new Transaction(fdoc, "JTOOLS: снять формулу «" + familyName + "»"))
                {
                    ft.Start();
                    try
                    {
                        var fm = fdoc.FamilyManager;
                        if (fm?.Types != null)
                        {
                            foreach (FamilyType type in fm.Types)
                            {
                                fm.CurrentType = type;
                                foreach (FamilyParameter fp in fm.GetParameters())
                                {
                                    if (fp?.Definition == null) continue;
                                    if (!string.Equals(fp.Definition.Name, paramName, StringComparison.OrdinalIgnoreCase)) continue;
                                    bool hasFormula = false;
                                    try { hasFormula = fp.IsDeterminedByFormula; } catch { }
                                    if (!hasFormula) continue;
                                    fm.SetFormula(fp, null);
                                    cleared++;
                                }
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

                if (cleared == 0)
                {
                    res.Errors.Add("Параметр «" + paramName + "» в семействе «" + familyName + "» не определён формулой.");
                    return res;
                }

                tempPath = Path.Combine(Path.GetTempPath(), FamilyReloadFileName(fdoc, familyName));
                if (File.Exists(tempPath)) File.Delete(tempPath);
                fdoc.SaveAs(tempPath, new SaveAsOptions());

                var ctx = new LoadFailuresContext();
                bool loaded;
                using (var t = new Transaction(doc, "JTOOLS: перезагрузка семейства «" + familyName + "»"))
                {
                    try
                    {
                        var opts = t.GetFailureHandlingOptions();
                        opts.SetFailuresPreprocessor(new LoadFailuresPreprocessor(ctx));
                        t.SetFailureHandlingOptions(opts);
                    }
                    catch (Exception ex) { GrdLog.Log("CopyParams.ClearFormula: FailureHandlingOptions " + ex.Message); }
                    t.Start();
                    try
                    {
                        loaded = doc.LoadFamily(tempPath, new CopyFamilyLoadOptions(false), out var lf);
                        t.Commit();
                    }
                    catch
                    {
                        try { t.RollBack(); } catch { }
                        throw;
                    }
                }

                if (ctx.SuppressedConstraint)
                    res.Errors.Add("Перезагрузка «" + familyName + "» требует действий Revit — формула не снята.");
                else if (!loaded)
                    res.Errors.Add("Семейство «" + familyName + "» не перезагрузилось — формула не снята.");
                else if (ParamStillReadOnly(doc, familyName, paramName))
                    res.Errors.Add("«" + familyName + "»: параметр «" + paramName + "» остался только для чтения — формула не снята.");
                else
                    res.Summary.Add("Формула снята у «" + paramName + "» (все типы, семейство «" + familyName + "»).");
            }
            catch (Exception ex)
            {
                GrdLog.Log("CopyParamsHandler.ClearFormula EXCEPTION: " + ex);
                res.Errors.Add("Снять формулу не удалось: " + ex.Message);
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempPath))
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
                try { fdoc?.Dispose(); } catch { }
            }
            return res;
        }

        /// <summary>После перезагрузки проверяем, что копируемые параметры реально видны на семействе.</summary>
        private static void VerifyParamPresent(Document doc, string familyName, List<CopyParamItem> items, CopyParamsResult res)
        {
            try
            {
                var family = FindFamily(doc, familyName);
                if (family == null || family.GetFamilySymbolIds() == null) return;
                foreach (var sid in family.GetFamilySymbolIds())
                {
                    var sym = doc.GetElement(sid) as FamilySymbol;
                    if (sym == null) continue;
                    foreach (var item in items)
                    {
                        if (item == null || string.IsNullOrEmpty(item.Name)) continue;
                        if (sym.LookupParameter(item.Name) == null)
                            res.Errors.Add("Параметр «" + item.Name + "» не виден на типах «" + familyName + "» в проекте.");
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("CopyParamsHandler.VerifyParamPresent EXCEPTION: " + ex);
            }
        }

        // ------------------------------------------------------------------ HELPERS

        private static Family FindFamily(Document doc, string name)
        {
            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Family)))
            {
                var f = e as Family;
                if (f != null && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
            }
            return null;
        }

        private static FamilyParameter FindParam(FamilyManager fm, string name)
        {
            if (fm == null) return null;
            foreach (FamilyParameter fp in fm.GetParameters())
            {
                if (fp?.Definition == null) continue;
                if (string.Equals(fp.Definition.Name, name, StringComparison.OrdinalIgnoreCase)) return fp;
            }
            return null;
        }

        private static ExternalDefinition FindSharedDef(DefinitionFile file, string guid, string name)
        {
            foreach (DefinitionGroup g in file.Groups)
            {
                if (g == null) continue;
                foreach (Definition d in g.Definitions)
                {
                    var ext = d as ExternalDefinition;
                    if (ext == null) continue;
                    if (Guid.TryParse(guid, out var g2) && g2 != Guid.Empty && ext.GUID == g2) return ext;
                    if (string.Equals(ext.Name, name, StringComparison.OrdinalIgnoreCase)) return ext;
                }
            }
            return null;
        }

        private static ForgeTypeId FamilyStorage(string storageType)
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

        private static ForgeTypeId SharedStorage(string storageType)
        {
            return FamilyStorage(storageType);
        }

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

        /// <summary>Группа параметра в свойствах семейства (ForgeTypeId).</summary>
        private static string GroupName(Definition d)
        {
            try
            {
                if (d == null) return string.Empty;
                var gt = d.GetGroupTypeId();
                if (gt == null || string.IsNullOrEmpty(gt.TypeId)) return string.Empty;
                if (gt == GroupTypeId.Data) return "Данные";
                if (gt == GroupTypeId.Text) return "Текст";
                if (gt == GroupTypeId.Construction) return "Конструкция";
                if (gt == GroupTypeId.Mechanical) return "Механика";
                if (gt == GroupTypeId.Electrical) return "Электрика";
                if (gt == GroupTypeId.General) return "Прочее";
                return gt.TypeId;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string DescribeType(Definition d)
        {
            try
            {
                if (d == null) return string.Empty;
                var t = d.GetDataType();
                if (t == null || string.IsNullOrEmpty(t.TypeId)) return string.Empty;
                if (t == SpecTypeId.String.Text) return "Текст";
                if (t == SpecTypeId.Int.Integer) return "Целое";
                if (t == SpecTypeId.Number) return "Число";
                if (t == SpecTypeId.Length) return "Длина";
                if (t == SpecTypeId.Area) return "Площадь";
                if (t == SpecTypeId.Volume) return "Объём";
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>Имя временного RFA-файла: берём имя файла, из которого семейство загружено
        /// в проект (PathName), чтобы LoadFamily заменил семейство «на месте», а не создал дубль.</summary>
        private static string FamilyReloadFileName(Document fdoc, string familyName)
        {
            try
            {
                var path = fdoc?.PathName;
                if (!string.IsNullOrEmpty(path))
                {
                    var baseName = Path.GetFileName(path);
                    if (!string.IsNullOrEmpty(baseName)) return baseName;
                }
            }
            catch { }
            return SafeFamilyFileName(familyName);
        }

        private static string SafeFamilyFileName(string familyName)
        {
            var s = string.IsNullOrEmpty(familyName) ? "Family" : familyName;
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s + ".rfa";
        }

        /// <summary>Правила перезагрузки семейства. overwriteParameterValues:
        /// true во время копирования (значения из RFA должны заменить значения в проекте,
        /// иначе скопированные числа не применятся); false при снятии формулы (не трогать
        /// заполненные в проекте значения остальных параметров).</summary>
        private sealed class CopyFamilyLoadOptions : IFamilyLoadOptions
        {
            private readonly bool _overwrite;
            public CopyFamilyLoadOptions(bool overwrite) { _overwrite = overwrite; }

            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = _overwrite;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = _overwrite;
                return true;
            }
        }

        /// <summary>«Вопросные» сбои (например, «Remove constraints») гасятся молча.</summary>
        private sealed class LoadFailuresContext
        {
            public bool SuppressedConstraint;
        }

        private sealed class LoadFailuresPreprocessor : IFailuresPreprocessor
        {
            private readonly LoadFailuresContext _ctx;
            public LoadFailuresPreprocessor(LoadFailuresContext ctx) { _ctx = ctx; }

            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                if (_ctx.SuppressedConstraint) return FailureProcessingResult.Continue;
                foreach (var msg in failuresAccessor.GetFailureMessages())
                {
                    if (IsConstraintStyle(msg))
                    {
                        _ctx.SuppressedConstraint = true;
                        break;
                    }
                }
                return _ctx.SuppressedConstraint ? FailureProcessingResult.ProceedWithRollBack
                                                 : FailureProcessingResult.Continue;
            }

            private static bool IsConstraintStyle(FailureMessageAccessor fm)
            {
                try
                {
                    var d = (fm.GetDescriptionText() ?? string.Empty) + " " +
                            (fm.GetDefaultResolutionCaption() ?? string.Empty);
                    d = d.ToLowerInvariant();
                    return d.Contains("constraint")
                        || d.Contains("ограничен")
                        || d.Contains("разъед")
                        || d.Contains("unjoin")
                        || d.Contains("disconnect");
                }
                catch { return false; }
            }
        }

        /// <summary>Параметр остался только для чтения (формула не снята).</summary>
        private static bool ParamStillReadOnly(Document doc, string familyName, string paramName)
        {
            try
            {
                var family = FindFamily(doc, familyName);
                if (family == null || family.GetFamilySymbolIds() == null) return false;
                foreach (var sid in family.GetFamilySymbolIds())
                {
                    var sym = doc.GetElement(sid) as FamilySymbol;
                    if (sym == null) continue;
                    var p = sym.LookupParameter(paramName);
                    if (p != null) return p.IsReadOnly;
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("CopyParamsHandler.ParamStillReadOnly EXCEPTION: " + ex);
            }
            return false;
        }
    }
}