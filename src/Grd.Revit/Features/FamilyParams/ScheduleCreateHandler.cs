using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GrdRevit.Core;

namespace GrdRevit.Revit
{
    /// <summary>Результат создания ведомости по выбранным элементам.</summary>
    public sealed class ScheduleCreateResult
    {
        /// <summary>Имена созданных ведомостей.</summary>
        public List<string> Created = new List<string>();
        /// <summary>Ошибки.</summary>
        public List<string> Errors = new List<string>();
        /// <summary>Параметры, которые не удалось добавить в поля (нет на категории).</summary>
        public List<string> SkippedParams = new List<string>();
        /// <summary>Сколько элементов отмечено (попадут в ведомость).</summary>
        public int ElementsMarked;
    }

    /// <summary>Запрошенный параметр ведомости: отображаемое имя и GUID общего
    /// параметра (если он общий). GUID — основной признак сопоставления, имя в ведомости
    /// может называться иначе («Расход» в семействе, «Flow» в проекте).</summary>
    public sealed class ScheduleParamRef
    {
        public string Name = string.Empty;
        public string Guid = string.Empty;

        public ScheduleParamRef() { }

        public ScheduleParamRef(string name, string guid)
        {
            Name = name ?? string.Empty;
            Guid = guid ?? string.Empty;
        }
    }

    /// <summary>
    /// Создаёт ведомость по выбранным элементам (кнопка «Создать ведомость» окна
    /// «Параметры семейств», только в режиме «Только выбранные»). Общий параметр-маркер
    /// (число) привязывается к категориям выбранных элементов, на них ставится значение 1,
    /// на каждую категорию создаётся спецификация с выбранными параметрами и фильтром по
    /// маркеру штатным средством Revit (ScheduleFilter Equal). Маркер сбрасывается у всех
    /// элементов привязанных категорий, чтобы повторные запуски не «подтаскивали» старый
    /// выбор. Работает через ExternalEvent (окно моделесс).
    /// </summary>
    public class ScheduleCreateHandler : IExternalEventHandler
    {
        private readonly object _sync = new object();
        private Request _request;

        private sealed class Request
        {
            public List<string> FamilyNames = new List<string>();
            public bool IncludeViews;
            public bool IncludeSheets;
            public List<ScheduleParamRef> ParamRefs = new List<ScheduleParamRef>();
            public List<long> SelectedIds = new List<long>();
            public Action<ScheduleCreateResult> DoneCb;
        }

        /// <summary>Очередь запроса «создать ведомость». Выполняется при очередном
        /// вызове <see cref="Execute"/> (Revit-поток), итог — в <paramref name="done"/>.</summary>
        public void Queue(List<string> familyNames, bool includeViews, bool includeSheets,
            List<ScheduleParamRef> paramRefs, List<long> selectedIds, Action<ScheduleCreateResult> done)
        {
            lock (_sync)
            {
                _request = new Request
                {
                    FamilyNames = familyNames ?? new List<string>(),
                    IncludeViews = includeViews,
                    IncludeSheets = includeSheets,
                    ParamRefs = paramRefs ?? new List<ScheduleParamRef>(),
                    SelectedIds = selectedIds ?? new List<long>(),
                    DoneCb = done
                };
            }
        }

        public string GetName()
        {
            return "JTOOLS: создать ведомость по выбранным";
        }

        public void Execute(UIApplication app)
        {
            Request req;
            lock (_sync) { req = _request; _request = null; }
            if (req == null)
            {
                GrdLog.Log("ScheduleCreateHandler.Execute: нет запроса в очереди");
                return;
            }

            var result = new ScheduleCreateResult();
            try { ExecuteCore(app, req, result); }
            catch (Exception ex)
            {
                GrdLog.Log("ScheduleCreateHandler.Execute EXCEPTION: " + ex);
                result.Errors.Add("Ошибка: " + ex.Message);
            }

            GrdLog.Log("ScheduleCreateHandler: создано=" + result.Created.Count +
                       ", отмечено=" + result.ElementsMarked + ", ошибок=" + result.Errors.Count +
                       (result.SkippedParams.Count > 0 ? ", пропущено полей: " + string.Join("; ", result.SkippedParams) : ""));
            try { req.DoneCb?.Invoke(result); }
            catch (Exception ex) { GrdLog.Log("ScheduleCreateHandler: done EXCEPTION: " + ex); }
        }

        /// <summary>Добавляет ошибку в результат, склеивая одинаковые сообщения:
        /// «Ведомость по категории «X»: текст» (категорий N) вместо N одинаковых строк.</summary>
        private static void AddError(ScheduleCreateResult result, string message)
        {
            if (result == null || string.IsNullOrEmpty(message)) return;
            if (result.Errors.Contains(message))
            {
                // Повтор: ищем уже добавленную строку с тем же текстом ошибки после «»: » и дописываем счётчик.
                for (int i = 0; i < result.Errors.Count; i++)
                {
                    if (!result.Errors[i].StartsWith(message + " (ещё ", StringComparison.Ordinal)) continue;
                    int close = result.Errors[i].LastIndexOf(')');
                    if (close < 0) break;
                    int count;
                    if (!int.TryParse(result.Errors[i].Substring(message.Length + 7, close - message.Length - 7), out count)) break;
                    result.Errors[i] = message + " (ещё " + (count + 1) + ")";
                    return;
                }
                result.Errors.Add(message + " (ещё 1)");
                return;
            }
            result.Errors.Add(message);
        }

        private static void ExecuteCore(UIApplication app, Request req, ScheduleCreateResult result)
        {
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null)
            {
                result.Errors.Add("Нет активного документа Revit.");
                return;
            }

            // Выбранные элементы с учётом отмеченных семейств/видов/листов.
            var famSet = new HashSet<string>(req.FamilyNames, StringComparer.OrdinalIgnoreCase);
            var selected = new List<Element>();
            foreach (var idVal in req.SelectedIds)
            {
                Element el = null;
                try { el = doc.GetElement(new ElementId(idVal)); } catch { }
                if (el == null) continue;
                if (el is FamilyInstance fi)
                {
                    if (!famSet.Contains(fi.Symbol?.Family?.Name ?? string.Empty)) continue;
                }
                else if (el is View) { if (!req.IncludeViews) continue; }
                else if (el is ViewSheet) { if (!req.IncludeSheets) continue; }
                else continue;
                selected.Add(el);
            }
            if (selected.Count == 0)
            {
                result.Errors.Add("Нет выбранных элементов для ведомости: отметьте семейства (или «Виды»/«Листы») слева.");
                return;
            }

            // Категории выбранных элементов, пригодные к привязке параметра.
            var catSet = doc.Application.Create.NewCategorySet();
            foreach (var el in selected)
            {
                var c = CategoryOf(el);
                if (c == null) continue;
                if (!ContainsCategory(catSet, c)) catSet.Insert(c);
            }
            catSet = SharedMarkerParam.FilterBindable(doc.Application, catSet);
            if (catSet.IsEmpty)
            {
                result.Errors.Add("Категории выбранных элементов не допускают привязку параметра-маркера.");
                return;
            }

            using (var t = new Transaction(doc, "JTOOLS: ведомость по выбранным"))
            {
                t.Start();
                try
                {
                    var def = SharedMarkerParam.EnsureMarkerDefinition(app, doc, catSet,
                        SharedMarkerParam.ScheduleMarkerName, "JTOOLS",
                        "JTOOLS: элементы, вошедшие в созданную ведомость (фильтр). Не удаляйте параметр — сломается фильтр ведомости.");
                    if (def == null)
                    {
                        result.Errors.Add("Не удалось создать параметр-маркер для фильтра ведомости.");
                        t.RollBack();
                        return;
                    }

                    // Сброс маркера у всех элементов привязанных категорий (старый выбор).
                    long markerId = 0;
                    foreach (Category cat in catSet)
                        ResetMarker(doc, cat, def.Name, ref markerId);

                    // Маркер=1 только на выбранных (отсюда берём id параметра).
                    foreach (var el in selected)
                    {
                        var p = Lookup(el, def.Name);
                        if (p == null) continue;
                        try
                        {
                            p.Set(1.0);
                            result.ElementsMarked++;
                        }
                        catch (Exception ex) { GrdLog.Log("ScheduleCreateHandler: метка «" + el.Id + "» EXCEPTION: " + ex); }
                        if (markerId == 0 && p.Id != ElementId.InvalidElementId) markerId = p.Id.Value;
                    }
                    var markerFieldId = markerId == 0 ? ElementId.InvalidElementId : new ElementId(markerId);
                    if (markerFieldId == ElementId.InvalidElementId)
                    {
                        result.Errors.Add("Не удалось получить параметр-маркер на элементах.");
                        t.RollBack();
                        return;
                    }

                    // Ведомость на каждую категорию: только выбранные + поля по отмеченным параметрам.
                    var groups = selected
                        .Select(el => new { El = el, Cat = CategoryOf(el) })
                        .Where(x => x.Cat != null && ContainsCategory(catSet, x.Cat))
                        .GroupBy(x => x.Cat.Id.Value);

                    ViewSchedule first = null;
                    foreach (var g in groups)
                    {
                        try
                        {
                            var catGroup = g.FirstOrDefault()?.Cat;
                            if (catGroup == null) continue;
                            var sched = CreateCategorySchedule(doc, catGroup, g.Select(x => x.El).ToList(),
                                req.ParamRefs, markerFieldId, result);
                            if (sched == null) continue;
                            result.Created.Add(sched.Name);
                            if (first == null) first = sched;
                        }
                        catch (Exception ex)
                        {
                            var catGroupName = g.FirstOrDefault()?.Cat?.Name ?? "неизвестно";
                            AddError(result, "Ведомость по категории «" + catGroupName + "»: " + ex.Message);
                            GrdLog.Log("ScheduleCreateHandler: спецификация EXCEPTION: " + ex);
                        }
                    }

                    if (result.Created.Count == 0)
                    {
                        if (result.Errors.Count == 0)
                            result.Errors.Add("Не создано ни одной ведомости.");
                        t.RollBack();
                        return;
                    }
                    if (t.Commit() != TransactionStatus.Committed)
                    {
                        result.Errors.Add("Транзакция не зафиксирована.");
                        t.RollBack();
                        return;
                    }

                    if (first != null && first.IsValidObject)
                    {
                        try { app.ActiveUIDocument?.RequestViewChange(first); }
                        catch (Exception ex) { GrdLog.Log("ScheduleCreateHandler: RequestViewChange: " + ex.Message); }
                    }
                }
                catch (Exception ex)
                {
                    GrdLog.Log("ScheduleCreateHandler.ExecuteCore EXCEPTION: " + ex);
                    result.Errors.Add("Ошибка: " + ex.Message);
                    try { t.RollBack(); } catch { }
                }
            }
        }

        /// <summary>Создаёт спецификацию категории с полями выбранных параметров и
        /// скрытым маркер-полем + фильтром. Возвращает null при неудаче.</summary>
        private static ViewSchedule CreateCategorySchedule(Document doc, Category cat, List<Element> elements,
            List<ScheduleParamRef> paramRefs, ElementId markerFieldId, ScheduleCreateResult result)
        {
            if (cat == null) return null;

            // Кандидаты в полях ведомости: параметры экземпляра и типа.
            // Сопоставление запроса с кандидатом — ParamNames.Find (GUID → имя →
            // нормализация → синонимы → встроенный параметр → вхождение).
            //
            // Параметры собираются со ВСЕХ элементов группы, а не с первого:
            // в одной категории могут быть разные семейства, и параметр, которого
            // нет у первого экземпляра, вполне есть у второго. Раньше брался
            // только elements.FirstOrDefault(), из-за чего собственные параметры
            // семейств (кроме параметров первого элемента) в ведомость не попадали.
            var instanceCands = new List<Parameter>();
            var typeCands = new List<Parameter>();

            void ScanInstance(Element el)
            {
                if (el?.Parameters == null) return;
                try
                {
                    foreach (Parameter p in el.Parameters)
                    {
                        if (p?.Definition == null) continue;
                        if (!instanceCands.Contains(p)) instanceCands.Add(p);
                    }
                }
                catch { }
            }
            void ScanType(Element el)
            {
                // Параметры типа лежат не в самом элементе, а в его символе.
                // Экземпляр может быть и не семейством (вид, лист) — тогда типовых
                // параметров у него нет, и набор остаётся пустым.
                FamilySymbol sym = null;
                try { sym = (el as FamilyInstance)?.Symbol; }
                catch { }
                if (sym?.Parameters == null) return;
                try
                {
                    foreach (Parameter p in sym.Parameters)
                    {
                        if (p?.Definition == null) continue;
                        if (!typeCands.Contains(p)) typeCands.Add(p);
                    }
                }
                catch { }
            }

            foreach (var el in elements)
            {
                ScanInstance(el);
                ScanType(el);
            }

            var sched = ViewSchedule.CreateSchedule(doc, cat.Id);
            var rawName = "JTOOLS: выбранные — " + cat.Name;
            var safeName = RevitNames.UniqueViewName(doc, rawName, "JTOOLS - выбранные");
            if (!string.Equals(safeName, rawName, StringComparison.Ordinal))
                GrdLog.Log("ScheduleCreateHandler: имя очищено: «" + rawName + "» -> «" + safeName + "»");
            sched.Name = safeName;

            int added = 0;
            var usedIds = new HashSet<long>();
            var addedFields = new List<ScheduleField>();

            // Нативный параметр «Тип» (BuiltInParameter.ELEM_TYPE_PARAM) Revit не
            // отдаёт в FamilyManager, поэтому в списке параметров семейства его нет,
            // а в ведомость он по умолчанию не попадает. Добавляем его всегда и
            // всегда первой колонкой: без Типа строки не группируются и не читаются.
            ScheduleField typeField = null;
            var typeParam = FindTypeParameter(instanceCands);
            if (typeParam != null)
            {
                var typeSchedulable = FindSchedulable(sched.Definition, typeParam.Id);
                if (typeSchedulable != null)
                {
                    try
                    {
                        typeField = sched.Definition.AddField(typeSchedulable);
                        addedFields.Add(typeField);
                        usedIds.Add(typeParam.Id.Value);
                        added++;
                        GrdLog.Log("ScheduleCreateHandler: «Тип» (" + cat.Name + ") — нативное поле, колонка 1");
                    }
                    catch (Exception ex)
                    {
                        GrdLog.Log("ScheduleCreateHandler: нативное поле «Тип» (" + cat.Name + ") EXCEPTION: " + ex.Message);
                    }
                }
            }
            else
            {
                GrdLog.Log("ScheduleCreateHandler: «Тип» (" + cat.Name + ") — нативного поля нет (категория не из семейств)");
            }

            foreach (var req2 in paramRefs)
            {
                if (req2 == null || string.IsNullOrEmpty(req2.Name)) continue;

                // Собираем кандидатов с обеих сторон (экземпляр и тип) сразу и
                // разбираем их по приоритету совпадения, чтобы не потерять
                // параметр только из-за того, что он оказался в другом списке.
                var cands = new List<Parameter>();
                foreach (var p in instanceCands) if (p != null) cands.Add(p);
                foreach (var p in typeCands) if (p != null) cands.Add(p);

                var m = ParamNames.Find(cands, req2.Name, req2.Guid);
                if (m == null || m.Parameter == null)
                {
                    if (!result.SkippedParams.Contains(req2.Name)) result.SkippedParams.Add(req2.Name);
                    GrdLog.Log("ScheduleCreateHandler: параметр «" + req2.Name + "» (" + cat.Name +
                               ") не найден среди " + instanceCands.Count + "/" + typeCands.Count + " полей");
                    continue;
                }

                long pid = m.Parameter.Id.Value;
                if (usedIds.Contains(pid))
                {
                    // Совпадение с уже добавленным нативным «Типом» — не дубликат,
                    // пользователь просто отметил его в списке.
                    if (typeParam != null && pid == typeParam.Id.Value) continue;
                    GrdLog.Log("ScheduleCreateHandler: «" + req2.Name + "» (" + cat.Name +
                               ") дублирует уже добавленное поле " + m.Parameter.Definition.Name);
                    continue;
                }

                // Revit разрешает добавить в ведомость не любой параметр категории,
                // а только те, что перечислены в GetSchedulableFields(). Проверяем
                // по этому списку и берём тот тип поля, который Revit считает
                // корректным. Раньше тип угадывался по месту нахождения параметра,
                // и Revit отвечал ArgumentException «The field specified by fieldType
                // and parameterId may not included in this ScheduleDefinition».
                SchedulableField schedulable = FindSchedulable(sched.Definition, m.Parameter.Id);
                if (schedulable == null)
                {
                    if (!result.SkippedParams.Contains(req2.Name)) result.SkippedParams.Add(req2.Name);
                    GrdLog.Log("ScheduleCreateHandler: «" + req2.Name + "» (" + cat.Name + ") -> «" +
                               ParamNames.DisplayName(m.Parameter) + "» [" + m.Parameter.Definition.Name +
                               "]: Revit не считает этот параметр добавляемым в ведомость");
                    continue;
                }

                ScheduleField addedField;
                try
                {
                    addedField = sched.Definition.AddField(schedulable);
                }
                catch (Exception ex)
                {
                    if (!result.SkippedParams.Contains(req2.Name)) result.SkippedParams.Add(req2.Name);
                    GrdLog.Log("ScheduleCreateHandler: поле «" + req2.Name + "» (" + cat.Name +
                               ") -> «" + ParamNames.DisplayName(m.Parameter) + "» не принято Revit: " + ex.Message);
                    continue;
                }

                usedIds.Add(pid);
                addedFields.Add(addedField);
                added++;
                GrdLog.Log("ScheduleCreateHandler: «" + req2.Name + "» (" + cat.Name + ") -> «" +
                           ParamNames.DisplayName(m.Parameter) + "» [" + m.Parameter.Definition.Name +
                           "] по правилу «" + m.Rule + "», поле " + schedulable.FieldType);
            }

            // Скрытое маркер-поле + штатный фильтр по нему. Тип поля берём из списка
            // допустимых, иначе Revit может отказать так же, как обычный параметр.
            ScheduleField mf = null;
            var markerSchedulable = FindSchedulable(sched.Definition, markerFieldId);
            try
            {
                if (markerSchedulable != null) mf = sched.Definition.AddField(markerSchedulable);
                else mf = sched.Definition.AddField(ScheduleFieldType.Instance, markerFieldId);
            }
            catch (Exception ex)
            {
                GrdLog.Log("ScheduleCreateHandler: добавление маркерного поля EXCEPTION: " + ex.Message);
            }
            if (mf != null)
            {
                try { mf.IsHidden = true; }
                catch (Exception ex) { GrdLog.Log("ScheduleCreateHandler: скрытие поля маркера: " + ex.Message); }
                try { sched.Definition.AddFilter(new ScheduleFilter(mf.FieldId, ScheduleFilterType.Equal, 1.0)); }
                catch (Exception ex) { GrdLog.Log("ScheduleCreateHandler: фильтр по маркеру EXCEPTION: " + ex.Message); }
            }
            else
            {
                result.Errors.Add("Ведомость «" + safeName + "»: не удалось добавить поле-маркер, "
                    + "фильтр «только выбранные» не применён.");
            }

            // Порядок колонок: нативный «Тип» — первым, остальные параметры в порядке
            // отметки пользователя. Маркерное поле скрыто, поэтому в конец списка.
            MoveTypeFieldFirst(sched.Definition, typeField, addedFields);

            // Сортировка по «Типу» по возрастанию. Если нативного поля нет
            // (категория не из семейств), сортируем по первому доступному полю.
            ApplyTypeSorting(sched.Definition, typeField, addedFields, cat);

            GrdLog.Log("ScheduleCreateHandler: создана «" + sched.Name + "» (категория «" + cat.Name +
                       "»), полей добавлено " + added);
            return sched;
        }

        /// <summary>Нативный параметр «Тип» среди параметров элемента. Revit не
        /// показывает его в FamilyManager, поэтому ищем по BuiltInParameter.</summary>
        private static Parameter FindTypeParameter(IEnumerable<Parameter> candidates)
        {
            if (candidates == null) return null;
            try
            {
                foreach (Parameter p in candidates)
                {
                    var def = p?.Definition as InternalDefinition;
                    if (def != null && def.BuiltInParameter == BuiltInParameter.ELEM_TYPE_PARAM) return p;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Ставит поле «Тип» первой колонкой ведомости. Порядок задаётся
        /// через SetFieldOrder: AddField всегда добавляет поле в конец.</summary>
        private static void MoveTypeFieldFirst(ScheduleDefinition def, ScheduleField typeField,
            List<ScheduleField> userFields)
        {
            if (def == null || typeField == null) return;
            try
            {
                var order = new List<ScheduleFieldId>();
                order.Add(typeField.FieldId);
                foreach (var f in userFields)
                {
                    if (f == null || f.FieldId == typeField.FieldId) continue;
                    if (!order.Contains(f.FieldId)) order.Add(f.FieldId);
                }
                // Остальные поля (маркер и добавленные Revit по умолчанию) — в конце.
                IList<ScheduleFieldId> all = def.GetFieldOrder();
                if (all != null)
                {
                    foreach (var id in all)
                    {
                        if (id == null || order.Contains(id)) continue;
                        order.Add(id);
                    }
                }
                if (order.Count > 0) def.SetFieldOrder(order);
            }
            catch (Exception ex)
            {
                GrdLog.Log("ScheduleCreateHandler.MoveTypeFieldFirst EXCEPTION: " + ex.Message);
            }
        }

        /// <summary>Сортирует строки ведомости по «Типу» по возрастанию. Если нативного
        /// поля нет, берёт первое добавленное пользовательское поле.</summary>
        private static void ApplyTypeSorting(ScheduleDefinition def, ScheduleField typeField,
            List<ScheduleField> userFields, Category cat)
        {
            if (def == null) return;
            try
            {
                ScheduleFieldId sortId = null;
                string byWhat = "Тип";
                if (typeField != null) sortId = typeField.FieldId;
                else
                {
                    foreach (var f in userFields)
                    {
                        if (f == null) continue;
                        sortId = f.FieldId;
                        byWhat = f.ParameterId != null ? f.ParameterId.Value.ToString() : "первое поле";
                        break;
                    }
                }
                if (sortId == null)
                {
                    GrdLog.Log("ScheduleCreateHandler: сортировка не задана (" + cat.Name + ") — нет полей");
                    return;
                }

                if (!def.CanSortByField(sortId))
                {
                    GrdLog.Log("ScheduleCreateHandler: поле «" + byWhat + "» (" + cat.Name + ") не сортируемо");
                    return;
                }

                def.ClearSortGroupFields();
                def.AddSortGroupField(new ScheduleSortGroupField(sortId, ScheduleSortOrder.Ascending));
                GrdLog.Log("ScheduleCreateHandler: сортировка по «" + byWhat + "» по возрастанию (" + cat.Name + ")");
            }
            catch (Exception ex)
            {
                GrdLog.Log("ScheduleCreateHandler.ApplyTypeSorting EXCEPTION: " + ex.Message);
            }
        }

        /// <summary>Ищет в списке допустимых полей ведомости запись для параметра.
        /// Возвращает null, если Revit не разрешает этот параметр в данной ведомости.
        /// Приоритет у обычного поля экземпляра/типа: служебные поля (Count,
        /// HostCount, Percentage, FromRoom и прочие) для обычных параметров не годятся.</summary>
        private static SchedulableField FindSchedulable(ScheduleDefinition def, ElementId paramId)
        {
            if (def == null || paramId == null) return null;
            try
            {
                IList<SchedulableField> fields = def.GetSchedulableFields();
                if (fields == null || fields.Count == 0) return null;

                SchedulableField fallback = null;
                foreach (SchedulableField f in fields)
                {
                    if (f == null || f.ParameterId == null) continue;
                    if (f.ParameterId.Value != paramId.Value) continue;

                    var ft = f.FieldType;
                    if (ft == ScheduleFieldType.Instance || ft == ScheduleFieldType.ElementType) return f;
                    if (fallback == null) fallback = f;
                }
                return fallback;
            }
            catch (Exception ex)
            {
                GrdLog.Log("ScheduleCreateHandler.FindSchedulable EXCEPTION: " + ex.Message);
                return null;
            }
        }

        /// <summary>Сбрасывает маркер у всех элементов категории и запоминает id параметра.</summary>
        private static void ResetMarker(Document doc, Category cat, string defName, ref long markerId)
        {
            try
            {
                foreach (Element el in new FilteredElementCollector(doc).OfCategoryId(cat.Id))
                {
                    var p = Lookup(el, defName);
                    if (p == null) continue;
                    if (markerId == 0 && p.Id != ElementId.InvalidElementId) markerId = p.Id.Value;
                    try
                    {
                        if (p.HasValue) p.Set(0.0);
                    }
                    catch (Exception ex) { GrdLog.Log("ScheduleCreateHandler: сброс «" + el.Id + "»: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("ScheduleCreateHandler: сброс маркера «" + cat.Name + "» EXCEPTION: " + ex);
            }
        }

        private static Parameter Lookup(Element el, string name)
        {
            if (el == null || string.IsNullOrEmpty(name)) return null;
            try { return el.LookupParameter(name); }
            catch { return null; }
        }

        private static Category CategoryOf(Element el)
        {
            try { return el?.Category; }
            catch { return null; }
        }

        private static bool ContainsCategory(CategorySet set, Category c)
        {
            if (set == null || c == null) return false;
            foreach (Category c2 in set)
            {
                if (c2 != null && c2.Id == c.Id) return true;
            }
            return false;
        }

        }
}