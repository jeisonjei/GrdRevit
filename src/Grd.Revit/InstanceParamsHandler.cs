using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace GrdRevit.Revit
{
    /// <summary>Параметр выбранного элемента: имя, признак «экземпляр/тип», текущее значение.
    /// При выборе нескольких элементов значение агрегируется: если у разных типов значения
    /// различаются — Diverse=true, а Value пуст (в окне показывается «несколько значений»).</summary>
    public sealed class ElementParamValue
    {
        public string Name = string.Empty;
        public bool IsInstance;
        public string StorageType = string.Empty;
        public string Group = string.Empty;
        public bool IsReadOnly;
        public string Value = string.Empty;
        public bool Diverse;
        public int MatchCount = 1;
    }

    /// <summary>Информация о выбранном элементе для заголовка окна и отчётов об ошибках.</summary>
    public sealed class ElementRefInfo
    {
        public long Id;
        public string Name = string.Empty;
    }

    /// <summary>Результат чтения параметров экземпляра (параметры экземпляра + параметры типа).</summary>
    public sealed class ElementParamsResult
    {
        public string Error;
        public string ElementName = string.Empty;
        public bool IsFamilyInstance;
        public List<ElementParamValue> Params = new List<ElementParamValue>();
        public List<ElementRefInfo> Elements = new List<ElementRefInfo>();
    }

    /// <summary>Изменение одного параметра (экземплярного или типового) к применению.</summary>
    public sealed class InstanceParamEdit
    {
        public string Name = string.Empty;
        public bool IsInstance;
        public string NewValue = string.Empty;
    }

    /// <summary>Результат применения изменений.</summary>
    public sealed class InstanceParamsResult
    {
        public int Total;
        public int Applied;
        public List<string> Changed = new List<string>();
        public List<string> Errors = new List<string>();
    }

    /// <summary>
    /// Читает и изменяет значения ВСЕХ параметров выбранных экземпляров семейств
    /// без открытия редактора семейства. Можно выбрать несколько экземпляров разных
    /// типов: значения, указанные в окне, применяются к параметрам ВСЕХ выбранных
    /// элементов (по имени параметра). Параметры «экземпляр» меняются только у
    /// выбранных элементов; параметры «тип» меняются у типов — то есть у всех
    /// экземпляров этих типов (об этом пользователь предупреждается в окне).
    /// Моделесс-окно не может обращаться к API напрямую — запросы ставятся в
    /// очередь и выполняются Revit-потоком через ExternalEvent.
    /// </summary>
    public class InstanceParamsHandler : IExternalEventHandler
    {
        private readonly object _sync = new object();
        private readonly Queue<Request> _requests = new Queue<Request>();

        private enum Kind { Read, Apply, ClearFormula }

        private sealed class Request
        {
            public Kind Kind;
            public List<ElementId> ElementIds = new List<ElementId>();
            public List<InstanceParamEdit> Edits;
            public string ClearName;
            public bool ClearIsInstance;
            public Action<ElementParamsResult> ReadCallback;
            public Action<InstanceParamsResult> ApplyCallback;
        }

        public void QueueRead(List<ElementId> elementIds, Action<ElementParamsResult> callback)
        {
            lock (_sync)
            {
                _requests.Enqueue(new Request
                {
                    Kind = Kind.Read,
                    ElementIds = new List<ElementId>(elementIds ?? new List<ElementId>()),
                    ReadCallback = callback
                });
            }
        }

        public void QueueApply(List<ElementId> elementIds, List<InstanceParamEdit> edits, Action<InstanceParamsResult> callback)
        {
            lock (_sync)
            {
                _requests.Enqueue(new Request
                {
                    Kind = Kind.Apply,
                    ElementIds = new List<ElementId>(elementIds ?? new List<ElementId>()),
                    Edits = edits ?? new List<InstanceParamEdit>(),
                    ApplyCallback = callback
                });
            }
        }

        public void QueueClearFormula(ElementId elementId, string paramName, bool isInstance, Action<InstanceParamsResult> callback)
        {
            lock (_sync)
            {
                _requests.Enqueue(new Request
                {
                    Kind = Kind.ClearFormula,
                    ElementIds = new List<ElementId> { elementId },
                    ClearName = paramName ?? string.Empty,
                    ClearIsInstance = isInstance,
                    ApplyCallback = callback
                });
            }
        }

        public string GetName()
        {
            return "JTOOLS: параметры экземпляра (без редактора семейства)";
        }

        public void Execute(UIApplication app)
        {
            List<Request> batch;
            lock (_sync)
            {
                if (_requests.Count == 0)
                {
                    GrdLog.Log("InstanceParamsHandler.Execute: нет запросов в очереди");
                    return;
                }
                batch = new List<Request>(_requests);
                _requests.Clear();
            }

            foreach (var req in batch)
            {
                try
                {
                    if (req.Kind == Kind.Read)
                    {
                        req.ReadCallback?.Invoke(Read(app, req.ElementIds));
                    }
                    else if (req.Kind == Kind.ClearFormula)
                    {
                        var single = req.ElementIds.Count > 0 ? req.ElementIds[0] : ElementId.InvalidElementId;
                        req.ApplyCallback?.Invoke(ClearFormula(app, single, req.ClearName, req.ClearIsInstance));
                    }
                    else
                    {
                        req.ApplyCallback?.Invoke(Apply(app, req.ElementIds, req.Edits));
                    }
                }
                catch (Exception ex)
                {
                    GrdLog.Log("InstanceParamsHandler.Execute: EXCEPTION: " + ex);
                    try
                    {
                        if (req.Kind == Kind.Read)
                            req.ReadCallback?.Invoke(new ElementParamsResult { Error = "Ошибка чтения: " + ex.Message });
                        else
                            req.ApplyCallback?.Invoke(new InstanceParamsResult { Errors = { "Ошибка применения: " + ex.Message } });
                    }
                    catch { }
                }
            }
        }

        private static ElementParamsResult Read(UIApplication app, List<ElementId> elementIds)
        {
            var res = new ElementParamsResult();
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null)
            {
                res.Error = "Нет активного документа Revit.";
                return res;
            }

            if (elementIds == null || elementIds.Count == 0)
            {
                res.Error = "Нет выбранных элементов.";
                return res;
            }

            var elements = new List<Element>();
            foreach (var id in elementIds)
            {
                Element el = null;
                try { el = doc.GetElement(id); } catch { }
                if (el != null) elements.Add(el);
            }
            if (elements.Count == 0)
            {
                res.Error = "Выбранные элементы не найдены в документе.";
                return res;
            }
            if (elements.Any(e => !(e is FamilyInstance)))
            {
                res.Error = "Один из выбранных элементов не является экземпляром семейства.";
                return res;
            }

            res.IsFamilyInstance = true;
            res.Elements = elements.Select(e => new ElementRefInfo { Id = e.Id.Value, Name = DescribeElement(e) }).ToList();
            res.ElementName = DescribeSelection(elements);
            bool verbose = elements.Count == 1;

            GrdLog.Log("InstanceParamsHandler.Read: элементов=" + elements.Count +
                       " (id=" + string.Join(",", elements.Select(e => e.Id.Value)) + ")");

            // Агрегация по имени параметра и привязке (экземпляр/тип): строки с одинаковым
            // именем у разных выбранных элементов сливаются в одну. Значение считается
            // «разным» (Diverse), если у разных типов оно различается.
            var agg = new Dictionary<string, ElementParamValue>();
            foreach (var el in elements)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                bool isNestedEl = IsNestedComponent(el);
                foreach (var p in SafeOrderedParameters(el, instance: true))
                    AddParam(agg, seen, p, isInstance: true, isNested: isNestedEl, doc: doc, verbose: verbose);
                var fi = el as FamilyInstance;
                if (fi?.Symbol != null)
                {
                    foreach (var p in SafeOrderedParameters(fi.Symbol, instance: false))
                        AddParam(agg, seen, p, isInstance: false, isNested: false, doc: doc, verbose: verbose);
                }
            }

            res.Params = agg.Values
                .OrderBy(p => p.IsInstance ? 0 : 1)
                .ThenBy(p => p.Group, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            GrdLog.Log("InstanceParamsHandler: «" + res.ElementName + "» параметров=" + res.Params.Count);
            return res;
        }

        private static string DescribeSelection(List<Element> elements)
        {
            try
            {
                if (elements.Count == 1) return DescribeElement(elements[0]);
                var names = elements.Select(DescribeElement)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var sb = new System.Text.StringBuilder();
                sb.Append("Выбрано ").Append(elements.Count).Append(" экземпляров");
                if (names.Count == 1)
                {
                    sb.Append(" семейства «").Append(names[0]).Append("»");
                }
                else
                {
                    sb.Append(" семейств (").Append(names.Count).Append(" типов): ");
                    sb.Append(string.Join("; ", names.Take(3)));
                    if (names.Count > 3) sb.Append(" и др.");
                }
                return sb.ToString();
            }
            catch
            {
                return elements.Count + " элементов";
            }
        }

        private static IEnumerable<Parameter> SafeOrderedParameters(Element el, bool instance)
        {
            var list = new List<Parameter>();
            try
            {
                if (el != null && el.Parameters != null)
                {
                    foreach (Parameter p in el.GetOrderedParameters())
                        list.Add(p);
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("InstanceParamsHandler: GetOrderedParameters(instance=" + instance + ") EXCEPTION: " + ex);
            }
            return list;
        }

        private static string DescribeElement(Element el)
        {
            try
            {
                if (el is FamilyInstance fi && fi.Symbol != null)
                    return fi.Symbol.Family.Name + " :: " + fi.Symbol.Name;
            }
            catch { }
            try
            {
                var name = el.Name;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
            return el.Id?.ToString() ?? string.Empty;
        }

        private static void AddParam(Dictionary<string, ElementParamValue> agg, HashSet<string> seen, Parameter p, bool isInstance, bool isNested, Document doc, bool verbose)
        {
            if (p?.Definition?.Name == null) return;

            // Не дублируем один и тот же параметр в пределах ОДНОГО элемента:
            // у «экземпляр» имя может идти и как «тип» — ключи разных пространств.
            if (isInstance)
            {
                var key = p.Definition.Name + "\u0001" + "I";
                if (!seen.Add(key)) return;
            }
            else
            {
                if (!seen.Add(p.Definition.Name)) return;
            }

            var row = new ElementParamValue
            {
                Name = p.Definition.Name,
                IsInstance = isInstance,
                StorageType = DescribeStorage(p),
                Group = FriendlyGroup(p),
                IsReadOnly = !IsWritable(p, isInstance, isNested),
                Value = ReadValue(p),
                MatchCount = 1
            };

            var aggKey = p.Definition.Name + "\u0001" + (isInstance ? "I" : "T");
            if (agg.TryGetValue(aggKey, out var existing))
            {
                existing.MatchCount++;
                if (!IsWritable(p, isInstance, isNested)) existing.IsReadOnly = true;
                if (!existing.Diverse && !string.Equals(existing.Value, row.Value, StringComparison.Ordinal))
                {
                    existing.Diverse = true;
                    existing.Value = string.Empty;
                }
            }
            else
            {
                agg[aggKey] = row;
            }

            if (!verbose) return;

            try
            {
                bool ro = IsReadOnlyParam(p);
                string guid = string.Empty;
                try { guid = p.GUID.ToString().Substring(0, 8); } catch { }
                string bind = "?";
                if (doc != null && p.Definition != null)
                {
                    try
                    {
                        Binding b = null;
                        try
                        {
                            var spe = new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement))
                                .Cast<SharedParameterElement>()
                                .FirstOrDefault(x => x.GuidValue == p.GUID);
                            if (spe != null) b = doc.ParameterBindings.get_Item(spe.GetDefinition());
                        }
                        catch { }
                        if (b == null)
                        {
                            try { b = doc.ParameterBindings.get_Item(p.Definition); } catch { }
                        }
                        bind = b == null ? "null"
                            : b is InstanceBinding ? "Instance"
                            : b is TypeBinding ? "Type"
                            : b is ElementBinding ? "Element"
                            : b.GetType().Name;
                    }
                    catch { }
                }
                GrdLog.Log("InstanceParams: PARAM «" + p.Definition.Name + "» pass=" + (isInstance ? "I" : "T") +
                           " shared=" + p.IsShared + " gid=" + guid + " bind=" + bind +
                           " storage=" + p.StorageType +
                           (ro ? " RO" : " RW") + " value=\"" + row.Value + "\"");
            }
            catch { }
        }

        /// <summary>Человекопонятный тип значения параметра.</summary>
        private static string DescribeStorage(Parameter p)
        {
            try
            {
                if (IsYesNo(p)) return "Да/Нет";
                var t = p.Definition?.GetDataType();
                if (t == null || string.IsNullOrEmpty(t.TypeId)) return "—";
                if (t == SpecTypeId.String.Text) return "Текст";
                if (t == SpecTypeId.Int.Integer) return "Целое";
                if (t == SpecTypeId.Number) return "Число";
                if (t == SpecTypeId.Length) return "Длина";
                if (t == SpecTypeId.Area) return "Площадь";
                if (t == SpecTypeId.Volume) return "Объём";
                return "Число";
            }
            catch
            {
                switch (p.StorageType)
                {
                    case StorageType.String: return "Текст";
                    case StorageType.Integer: return "Целое";
                    case StorageType.Double: return "Число";
                    case StorageType.ElementId: return "Ссылка";
                    default: return "—";
                }
            }
        }

        private static string FriendlyGroup(Parameter p)
        {
            try
            {
                var gt = p.Definition?.GetGroupTypeId();
                if (gt == null || string.IsNullOrEmpty(gt.TypeId)) return string.Empty;
                if (gt == GroupTypeId.General) return "Прочее";
                if (gt == GroupTypeId.Data) return "Данные";
                if (gt == GroupTypeId.Text) return "Текст";
                if (gt == GroupTypeId.Construction || gt == GroupTypeId.Structural) return "Конструкция";
                if (gt == GroupTypeId.Mechanical) return "ОВК";
                if (gt == GroupTypeId.Electrical) return "Электрика";
                if (gt == GroupTypeId.IdentityData) return "Идентификация";
                if (gt == GroupTypeId.Materials) return "Материалы";
                if (gt == GroupTypeId.Graphics) return "Графика";
                if (gt == GroupTypeId.Geometry) return "Геометрия";
                if (gt == GroupTypeId.Constraints) return "Ограничения";
                if (gt == GroupTypeId.Phasing) return "Стадии";
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsReadOnlyParam(Parameter p)
        {
            try { return p.IsReadOnly; } catch { return true; }
        }

        /// <summary>
        /// Действительно ли параметр доступен для записи.
        /// <para>ВАЖНО: общие (shared) параметры ЭКЗЕМПЛЯРА у экземпляров ВЛОЖЕННЫХ
        /// семейств Revit в проектном документе помечает IsReadOnly=true, хотя в UI они
        /// редактируются (значение пишется как локальное переопределение экземпляра).
        /// Поэтому для shared-параметров экземпляра вложенного элемента флагу IsReadOnly
        /// не доверяем: пробуем записать, а фактический результат сверяем после коммита.
        /// Параметры, определяемые формулой, остаются недоступными (для них в строке
        /// доступна кнопка «снять формулу»).</para>
        /// </summary>
        private static bool IsWritable(Parameter p, bool isInstance, bool isNested)
        {
            if (p == null) return false;
            if (isInstance && isNested)
            {
                bool shared;
                try { shared = p.GUID != Guid.Empty; } catch { shared = false; }
                if (shared) return true;
            }
            try { return !p.IsReadOnly; } catch { return false; }
        }

        /// <summary>Элемент — экземпляр ВЛОЖЕННОГО семейства (находится внутри другого
        /// экземпляра семейства в проекте).</summary>
        private static bool IsNestedComponent(Element el)
        {
            try { return el is FamilyInstance fi && fi.SuperComponent != null; }
            catch { return false; }
        }

        private static bool IsYesNo(Parameter p)
        {
            try
            {
                var t = p.Definition?.GetDataType();
                return t != null && t == SpecTypeId.Boolean.YesNo;
            }
            catch { return false; }
        }

        private static string ReadValue(Parameter p)
        {
            if (p == null) return string.Empty;
            try
            {
                if (!p.HasValue) return string.Empty;

                switch (p.StorageType)
                {
                    case StorageType.String:
                        return p.AsString() ?? string.Empty;
                    case StorageType.Integer:
                    {
                        var vs = SafeValueString(p);
                        if (!string.IsNullOrEmpty(vs)) return vs;
                        return p.AsInteger().ToString(CultureInfo.InvariantCulture);
                    }
                    case StorageType.Double:
                    {
                        var vs = SafeValueString(p);
                        if (!string.IsNullOrEmpty(vs)) return vs;
                        return p.AsDouble().ToString("0.####", CultureInfo.InvariantCulture);
                    }
                    case StorageType.ElementId:
                    {
                        var vs = SafeValueString(p);
                        if (!string.IsNullOrEmpty(vs)) return vs;
                        var eid = p.AsElementId();
                        return (eid?.Value ?? 0L).ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("InstanceParamsHandler.ReadValue EXCEPTION: " + ex);
            }
            return string.Empty;
        }

        private static string SafeValueString(Parameter p)
        {
            try { return p.AsValueString() ?? string.Empty; } catch { return string.Empty; }
        }

        private static InstanceParamsResult Apply(UIApplication app, List<ElementId> elementIds, List<InstanceParamEdit> edits)
        {
            var res = new InstanceParamsResult { Total = edits?.Count ?? 0 };
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null)
            {
                res.Errors.Add("Нет активного документа Revit.");
                return res;
            }

            var elements = new List<Element>();
            if (elementIds != null)
            {
                foreach (var id in elementIds)
                {
                    Element el = null;
                    try { el = doc.GetElement(id); } catch { }
                    if (el != null) elements.Add(el);
                }
            }
            if (elements.Count == 0)
            {
                res.Errors.Add("Выбранные элементы не найдены в документе.");
                return res;
            }

            if (edits == null || edits.Count == 0) return res;
            GrdLog.Log("InstanceParams.Apply: элементов=" + elements.Count);

            using (var t = new Transaction(doc, "JTOOLS: изменить параметры экземпляра"))
            {
                try
                {
                    t.Start();

                    foreach (var edit in edits)
                    {
                        int fails = 0;
                        int writes = 0;
                        var reasons = new List<string>();
                        foreach (var el in elements)
                        {
                            try
                            {
                                var r = ApplyOne(el, edit);
                                if (r.Ok)
                                {
                                    if (r.Written)
                                    {
                                        writes++;
                                        var key = edit.Name + (edit.IsInstance ? " (экз.)" : " (тип)") + " = \"" + edit.NewValue + "\"";
                                        if (!res.Changed.Contains(key)) res.Changed.Add(key);
                                    }
                                }
                                else
                                {
                                    fails++;
                                    reasons.Add(r.Element + (string.IsNullOrEmpty(r.Reason) ? string.Empty : " — " + r.Reason));
                                }
                            }
                            catch (Exception ex)
                            {
                                GrdLog.Log("InstanceParamsHandler.Apply edit EXCEPTION: " + ex);
                                fails++;
                                reasons.Add(DescribeElement(el) + " — " + ex.Message);
                            }
                        }

                        if (fails == 0)
                        {
                            res.Applied++;
                            GrdLog.Log("InstanceParams.Apply: «" + edit.Name + "» ок у " + elements.Count +
                                       (writes > 0 ? ", записей=" + writes : ", без изменений") +
                                       " value=\"" + edit.NewValue + "\"");
                        }
                        else
                        {
                            res.Errors.Add("«" + edit.Name + "»: не применено у " + fails + " из " +
                                           elements.Count + " экземпляров (" +
                                           string.Join("; ", reasons.Take(3)) + (reasons.Count > 3 ? "; и др." : "") + ")");
                            GrdLog.Log("InstanceParams.Apply: «" + edit.Name +
                                       "» не применено у " + fails + " из " + elements.Count +
                                       " reasons=" + string.Join(" | ", reasons));
                        }
                    }

                    t.Commit();
                }
                catch (Exception ex)
                {
                    GrdLog.Log("InstanceParamsHandler.Apply EXCEPTION: " + ex);
                    try { if (t.HasStarted()) t.RollBack(); } catch { }
                    res.Errors.Add("Транзакция отменена: " + ex.Message);
                }
            }

            // Контроль после коммита: читаем фактическое состояние параметров.
            foreach (var edit in edits)
            {
                var key = edit.Name + (edit.IsInstance ? " (экз.)" : " (тип)") + " = \"" + edit.NewValue + "\"";
                if (!res.Changed.Contains(key)) continue;
                foreach (var el in elements)
                {
                    try
                    {
                        var target = edit.IsInstance ? el : (el as FamilyInstance)?.Symbol;
                        var verb = target?.LookupParameter(edit.Name);
                        var actual = verb != null ? ReadValue(verb) : string.Empty;
                        GrdLog.Log("InstanceParamsHandler.Apply verify: «" + edit.Name + "» isInstance=" +
                                   edit.IsInstance + " el=\"" + DescribeElement(el) + "\" found=" + (verb != null) +
                                   " RO=" + (verb != null && IsReadOnlyParam(verb)) +
                                   " value=\"" + actual +
                                   "\" (запрошено \"" + edit.NewValue + "\")");
                        if (verb != null && actual != (edit.NewValue ?? string.Empty))
                        {
                            res.Errors.Add("«" + edit.Name + "» у «" + DescribeElement(el) + "»: Revit не сохранил значение \"" +
                                           edit.NewValue + "\" (осталось \"" + actual + "\") — параметр пересчитывается семейством.");
                        }
                    }
                    catch { }
                }
            }

            GrdLog.Log("InstanceParamsHandler: применено " + res.Applied + " из " + res.Total +
                       ", ошибок=" + res.Errors.Count);
            return res;
        }

        /// <summary>Результат записи одного значения параметра в один элемент.</summary>
        private sealed class ApplyOneResult
        {
            public bool Ok;
            public bool Written;
            public string Reason = string.Empty;
            public string Element = string.Empty;
        }

        private static ApplyOneResult ApplyOne(Element el, InstanceParamEdit edit)
        {
            try
            {
                var resRec = new ApplyOneResult { Element = DescribeElement(el) };
                if (!(el is FamilyInstance))
                {
                    resRec.Ok = false;
                    resRec.Reason = "элемент не является экземпляром семейства";
                    return resRec;
                }

                Element target;
                Parameter p = FindWritableParam(el, edit.Name, edit.IsInstance, out target);

                if (target == null || p == null)
                {
                    resRec.Ok = false;
                    resRec.Reason = "параметр не найден";
                    GrdLog.Log("InstanceParams.Apply: не найден «" + edit.Name + "» isInstance=" + edit.IsInstance +
                               " el=\"" + DescribeElement(el) + "\"");
                    return resRec;
                }
                if (!IsWritable(p, edit.IsInstance, IsNestedComponent(el)))
                {
                    resRec.Ok = false;
                    resRec.Reason = "параметр доступен только для чтения";
                    GrdLog.Log("InstanceParams.Apply: только чтение «" + edit.Name + "» el=\"" + DescribeElement(el) + "\"");
                    return resRec;
                }
                if (p.StorageType == StorageType.ElementId)
                {
                    resRec.Ok = false;
                    resRec.Reason = "значение ссылки не редактируется";
                    return resRec;
                }

                var current = ReadValue(p);
                if (string.Equals(current, edit.NewValue ?? string.Empty, StringComparison.Ordinal))
                {
                    GrdLog.Log("InstanceParams.Apply: «" + edit.Name + "» el=\"" + DescribeElement(el) +
                               "\" без изменений (current=\"" + current + "\")");
                    resRec.Ok = true;
                    resRec.Written = false;
                    return resRec;
                }

                if (!TryWrite(p, edit.NewValue ?? string.Empty))
                {
                    resRec.Ok = false;
                    resRec.Reason = "не удалось записать значение \"" + edit.NewValue + "\"";
                    GrdLog.Log("InstanceParams.Apply: не удалось записать «" + edit.Name +
                               "» el=\"" + DescribeElement(el) + "\" value=\"" + edit.NewValue + "\" current=\"" + current + "\"");
                    return resRec;
                }

                resRec.Ok = true;
                resRec.Written = true;
                GrdLog.Log("InstanceParams.Apply: «" + edit.Name + "» ок el=\"" + DescribeElement(el) + "\" target=" +
                           target.GetType().Name + " value=\"" + edit.NewValue + "\" (было \"" + current + "\")");
                return resRec;
            }
            catch (Exception ex)
            {
                GrdLog.Log("InstanceParamsHandler.ApplyOne EXCEPTION: " + ex);
                return new ApplyOneResult { Ok = false, Element = DescribeElement(el), Reason = ex.Message };
            }
        }

        /// <summary>Находит параметр с нужной привязкой (экземпляр/тип) у элемента.
        /// Если параметр не найден или только для чтения — пробует другую привязку
        /// того же элемента (тип↔экземпляр): строка таблицы могла быть размечена
        /// не той привязкой.</summary>
        private static Parameter FindWritableParam(Element el, string name, bool isInstance, out Element target)
        {
            target = isInstance ? el : (el as FamilyInstance)?.Symbol;
            bool isNested = IsNestedComponent(el);
            Parameter p = null;
            try { p = target?.LookupParameter(name); } catch { }

            if (p == null || !IsWritable(p, isInstance, isNested))
            {
                var alternate = isInstance ? (el as FamilyInstance)?.Symbol : el;
                if (alternate != null && alternate != target)
                {
                    try
                    {
                        var alt = alternate.LookupParameter(name);
                        if (alt != null && IsWritable(alt, isInstance, isNested))
                        {
                            target = alternate;
                            p = alt;
                        }
                    }
                    catch { }
                }
            }
            return p;
        }

        /// <summary>
        /// Снимает формулу с параметра, редактируя САМО СЕМЕЙСТВО через его документ
        /// (Document.EditFamily) — так же, как окно «Редактировать семейство», но без
        /// открытия UI. Формула убирается у ВСЕХ типов семейства, после чего семейство
        /// перезагружается в проект (затрагивает все экземпляры этого семейства).
        /// </summary>
        private static InstanceParamsResult ClearFormula(UIApplication app, ElementId elementId, string paramName, bool isInstance)
        {
            var res = new InstanceParamsResult();
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null)
            {
                res.Errors.Add("Нет активного документа Revit.");
                return res;
            }

            Element el = null;
            try { el = doc.GetElement(elementId); } catch { }
            if (el == null)
            {
                res.Errors.Add("Выбранный элемент не найден в документе.");
                return res;
            }

            var fi = el as FamilyInstance;
            var family = fi?.Symbol?.Family;
            if (family == null)
            {
                res.Errors.Add("Снять формулу можно только у экземпляра семейства.");
                return res;
            }
            // Сохраняем имя семейства ДО перезагрузки: после LoadFamily старые ссылки
            // на элементы проекта (в т.ч. Family) становятся недействительными,
            // и обращение к ним бросает «The referenced object is not valid»,
            string familyName = family.Name;
            if (family.IsInPlace)
            {
                res.Errors.Add("Семейство «" + familyName + "» — встроенное (in-place): для него нет файла семейства, формула не снимается.");
                return res;
            }
            if (!family.IsEditable)
            {
                res.Errors.Add("Семейство «" + familyName + "» недоступно для редактирования.");
                return res;
            }

            res.Total = 1;
            string tempPath = null;
            Document famDoc = null;
            try
            {
                famDoc = doc.EditFamily(family);

                int cleared = 0;
                GrdLog.Log("ClearFormula: семейство «" + familyName + "» параметр «" + paramName + "»");
                using (var t = new Transaction(famDoc, "JTOOLS: снять формулу параметра"))
                {
                    t.Start();
                    var fm = famDoc.FamilyManager;
                    GrdLog.Log("ClearFormula: типов=" + (fm.Types != null ? fm.Types.Cast<FamilyType>().Count() : -1));
                    foreach (FamilyType type in fm.Types)
                    {
                        fm.CurrentType = type;
                        foreach (FamilyParameter fp in fm.GetParameters())
                        {
                            bool idxMatch = string.Equals(fp.Definition?.Name, paramName, StringComparison.Ordinal);
                            bool hasFormula = false;
                            try { hasFormula = fp.IsDeterminedByFormula; } catch { }
                            if (!idxMatch) continue;
                            GrdLog.Log("ClearFormula: тип «" + (type?.Name ?? "?") + "» fp=\"" +
                                       (fp.Definition?.Name ?? "?") + "\" formula=" + hasFormula);
                            if (!hasFormula) continue;
                            fm.SetFormula(fp, null);
                            cleared++;
                        }
                    }
                    t.Commit();
                }

                if (cleared == 0)
                {
                    res.Errors.Add("Параметр «" + paramName + "» в семействе «" + familyName +
                                   "» не определён формулой — снимать нечего. Если параметр добавляется " +
                                   "из проекта (общий параметр проекта/свойство), в семействе формулы нет, " +
                                   "и редактировать его можно только на уровне самого проекта.");
                    GrdLog.Log("ClearFormula: cleared=0 — параметр не определён формулой ни у одного типа");
                    return res;
                }

                // ВАЖНО (тот же паттерн, что в FamilyParamsHandler): имя временного
                // RFA-файла ОБЯЗАНО совпадать с именем семейства в проекте. Иначе Revit
                // загрузит семейство под именем файла как НОВОЕ семейство, а исходное
                // (всё ещё с формулой) останется в проекте — параметр так и будет
                // доступен только для чтения.
                tempPath = Path.Combine(Path.GetTempPath(), SafeFamilyFileName(familyName));
                if (File.Exists(tempPath)) File.Delete(tempPath);

                Family loadedFamily = null;
                using (var t = new Transaction(doc, "JTOOLS: перезагрузка семейства «" + familyName + "»"))
                {
                    t.Start();
                    try
                    {
                        famDoc.SaveAs(tempPath, new SaveAsOptions());
                        loadedFamily = doc.LoadFamily(tempPath, new OverwriteFamilyLoadOptions(), out var lf)
                            ? lf
                            : null;
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
                            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                            tempPath = null;
                        }
                    }
                }

                if (loadedFamily == null)
                {
                    res.Errors.Add("Семейство «" + familyName + "» не перезагрузилось — снять формулу не удалось.");
                    GrdLog.Log("ClearFormula: LoadFamily вернул false — перезагрузка не удалась");
                    return res;
                }
                GrdLog.Log("ClearFormula: перезагрузка успешна (loadedFamily=" + loadedFamily.Name + ")");

                // Жёсткая проверка: после перезагрузки параметр в проекте обязан
                // стать редактируемым. Если он остался только для чтения — формула
                // не снята (или её значение по-прежнему задаёт другой параметр).
                // ВАЖНО: static-ссылки на Family/элемент после LoadFamily недействительны,
                // поэтому ищем семейство (и тип) заново по имени.
                Family newFamily = null;
                try
                {
                    newFamily = new FilteredElementCollector(doc).OfClass(typeof(Family))
                        .Cast<Family>()
                        .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
                    if (newFamily == null)
                    {
                        newFamily = new FilteredElementCollector(doc).OfClass(typeof(Family))
                            .Cast<Family>()
                            .Where(f => f.Name.IndexOf(familyName, StringComparison.OrdinalIgnoreCase) >= 0)
                            .FirstOrDefault();
                    }
                }
                catch { }

                Parameter symParam = null;
                int symCount = 0;
                if (newFamily != null)
                {
                    try
                    {
                        symCount = newFamily.GetFamilySymbolIds().Count;
                        foreach (var sid in newFamily.GetFamilySymbolIds())
                        {
                            var sym = doc.GetElement(sid) as FamilySymbol;
                            symParam = sym?.LookupParameter(paramName);
                            if (symParam != null) break;
                        }
                    }
                    catch { }
                }

                // Решающей является точка, где параметр видит пользователь — элемент.
                // Если по нему параметр теперь редактируем, операция удалась, даже если
                // проверка «по символу» этого не подтвердила (например, система отдаёт
                // другой параметр того же имени с другой привязкой/из проекта).
                FamilyInstance el2 = null;
                Parameter afterParam = null;
                try
                {
                    el2 = doc.GetElement(elementId) as FamilyInstance;
                    afterParam = el2?.LookupParameter(paramName);
                }
                catch { }
                if (afterParam == null) afterParam = symParam;

                bool fRef = afterParam != null && IsReadOnlyParam(afterParam);
                bool fSym = symParam != null && IsReadOnlyParam(symParam);
                GrdLog.Log("InstanceParamsHandler.ClearFormula verify: familyName=\"" + familyName +
                           "\" newFamily=" + (newFamily != null ? newFamily.Name : "null") +
                           " symbols=" + symCount +
                           " symParam=" + (symParam != null ? symParam.StorageType.ToString() + (fSym ? " RO" : " RW") : "null") +
                           " elParam=" + (afterParam != null ? afterParam.StorageType.ToString() + (fRef ? " RO" : " RW") : "null"));

                if (afterParam == null || fRef)
                {
                    res.Errors.Add("Параметр «" + paramName + "» в семействе «" + familyName +
                                   "» остался доступен только для чтения — формула не снята.");
                    return res;
                }

                res.Applied = 1;
                res.Changed.Add(paramName + ": формула снята у семейства «" + familyName + "» (все типы)");
            }
            catch (Exception ex)
            {
                GrdLog.Log("InstanceParamsHandler.ClearFormula EXCEPTION: " + ex);
                res.Errors.Add("Снять формулу не удалось: " + ex.Message);
            }
            finally
            {
                if (famDoc != null)
                {
                    try { famDoc.Close(false); } catch { }
                    try { famDoc.Dispose(); } catch { }
                }
            }

            GrdLog.Log("InstanceParamsHandler.ClearFormula: «" + paramName + "» applied=" + res.Applied +
                       ", errors=" + res.Errors.Count);
            return res;
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

        /// <summary>Правила перезагрузки семейства: перезаписывать параметры типа в проекте.</summary>
        private class OverwriteFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool isInProject, out FamilySource source, out bool overwriteParameterValues)
            {
                source = isInProject ? FamilySource.Project : FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }

        private static bool TryWrite(Parameter p, string newValue)
        {
            if (p == null) return false;

            // Текстовое значение в формате отображения (с единицами измерения) — самый
            // естественный путь: пользователь правит то, что видит в ячейке.
            if (IsYesNo(p))
            {
                var b = NormalizeBool(newValue);
                if (b.HasValue)
                {
                    try { p.Set(b.Value ? 1 : 0); return true; } catch { return false; }
                }
                return false;
            }

            // Основной путь для текстовых: Set напрямую (то же, что делает UI-редактор).
            // ВАЖНО: Revit отражает новое значение в AsString() для этого параметра только
            // после коммита — немедленное чтение в транзакции возвращает старое. Поэтому
            // доверяем bool-результату Set, а фактическая сверка выполняется ПОСЛЕ коммита
            // (цикл verify в Apply). SetValueString раньше вызывался ПЕРВЫМ и его bool
            // игнорировался — при тихом отказе возвращали success, не записав ничего.
            bool okSet = false;
            try { okSet = p.Set(newValue); } catch (Exception ex) { GrdLog.Log("TryWrite.Set EXCEPTION (" + ex.GetType().Name + "): " + ex.Message); }
            GrdLog.Log("TryWrite.Set(" + (p.Definition != null ? p.Definition.Name : "?") + ")=" + okSet);
            if (okSet) return true;

            // Запасной путь: значение в формате отображения.
            bool okVs = false;
            try { okVs = p.SetValueString(newValue); } catch (Exception ex) { GrdLog.Log("TryWrite.SetValueString EXCEPTION: " + ex.Message); }
            GrdLog.Log("TryWrite.SetValueString=" + okVs);
            if (okVs) return true;

            try
            {
                switch (p.StorageType)
                {
                    case StorageType.Integer:
                        if (int.TryParse(newValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ||
                            int.TryParse(newValue, NumberStyles.Integer, CultureInfo.CurrentCulture, out i))
                        {
                            return p.Set(i);
                        }
                        return false;
                    case StorageType.Double:
                        if (double.TryParse(newValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ||
                            double.TryParse(newValue, NumberStyles.Float, CultureInfo.CurrentCulture, out d))
                        {
                            try
                            {
                                var spec = p.Definition?.GetDataType();
                                double v = spec == null ? d : UnitUtils.ConvertToInternalUnits(d, spec);
                                return p.Set(v);
                            }
                            catch
                            {
                                return p.Set(d);
                            }
                        }
                        return false;
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("InstanceParamsHandler.TryWrite switch EXCEPTION: " + ex);
            }
            return false;
        }

        private static bool? NormalizeBool(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            switch (v.Trim().ToLowerInvariant())
            {
                case "да": case "yes": case "1": case "true": case "истина":
                case "вкл": case "включено": case "on":
                    return true;
                case "нет": case "no": case "0": case "false": case "ложь":
                case "выкл": case "выключено": case "off":
                    return false;
                default:
                    return null;
            }
        }
    }
}
