using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace GrdRevit.Revit
{
    public sealed class CopyScheduleCountLengthParam
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string ParamName { get; set; }
    }

    public sealed class CopyScheduleCountLengthOptions
    {
        public List<CopyScheduleCountLengthParam> SourceColumns = new List<CopyScheduleCountLengthParam>();
        public List<CopyScheduleCountLengthParam> TargetParams = new List<CopyScheduleCountLengthParam>();
    }

    public sealed class CopyScheduleCountLengthResult
    {
        public bool Ok;
        public string Error = string.Empty;
        public string Message = string.Empty;
        public int Applied;
        public int Skipped;
    }

    public class CopyScheduleCountLengthHandler : IExternalEventHandler
    {
        private readonly object _sync = new object();
        private readonly Queue<Request> _requests = new Queue<Request>();

        private const string KeySeparator = "\u0001";

        private enum Kind { CollectParams, Execute, Clear }

        private sealed class Request
        {
            public Kind Kind;
            public long SourceFieldId;
            public long TargetFieldId;
            public string TargetParam;
            public bool SkipFilled;
            public Action<CopyScheduleCountLengthOptions> OptionsCallback;
            public Action<CopyScheduleCountLengthResult> ResultCallback;
        }

        public void QueueCollectParams(Action<CopyScheduleCountLengthOptions> callback)
        {
            lock (_sync) _requests.Enqueue(new Request { Kind = Kind.CollectParams, OptionsCallback = callback });
        }

        public void QueueExecute(long sourceFieldId, long targetFieldId, string targetParam, bool skipFilled, Action<CopyScheduleCountLengthResult> callback)
        {
            lock (_sync) _requests.Enqueue(new Request { Kind = Kind.Execute, SourceFieldId = sourceFieldId, TargetFieldId = targetFieldId, TargetParam = targetParam, SkipFilled = skipFilled, ResultCallback = callback });
        }

        public void QueueClear(long targetFieldId, string targetParam, Action<CopyScheduleCountLengthResult> callback)
        {
            lock (_sync) _requests.Enqueue(new Request { Kind = Kind.Clear, TargetFieldId = targetFieldId, TargetParam = targetParam, ResultCallback = callback });
        }

        public string GetName() => "JTOOLS: copy column to parameter";

        public void Execute(UIApplication app)
        {
            List<Request> batch;
            lock (_sync)
            {
                if (_requests.Count == 0) return;
                batch = new List<Request>(_requests);
                _requests.Clear();
            }

            foreach (var req in batch)
            {
                try
                {
                    if (req.Kind == Kind.CollectParams)
                        req.OptionsCallback?.Invoke(CollectParams(app));
                    else if (req.Kind == Kind.Clear)
                        req.ResultCallback?.Invoke(ExecuteClear(app, req.TargetFieldId, req.TargetParam));
                    else
                        req.ResultCallback?.Invoke(ExecuteCopy(app, req.SourceFieldId, req.TargetFieldId, req.TargetParam, req.SkipFilled));
                }
                catch (Exception ex)
                {
                    GrdLog.Log("CountLength: Execute batch EXCEPTION: " + ex);
                    try
                    {
                        if (req.Kind == Kind.CollectParams)
                            req.OptionsCallback?.Invoke(new CopyScheduleCountLengthOptions());
                        else
                            req.ResultCallback?.Invoke(new CopyScheduleCountLengthResult { Ok = false, Error = ex.Message });
                    }
                    catch { }
                }
            }
        }

        private CopyScheduleCountLengthOptions CollectParams(UIApplication app)
        {
            var options = new CopyScheduleCountLengthOptions();
            var uidoc = app?.ActiveUIDocument;
            var doc = uidoc?.Document;
            var view = doc == null ? null : uidoc.ActiveView as ViewSchedule;
            GrdLog.Log("CountLength.CollectParams: doc=" + (doc == null ? "<null>" : doc.Title) +
                       " view=\"" + (uidoc?.ActiveView?.Name ?? "<null>") + "\" isSchedule=" + (view != null));
            if (doc == null || view?.Definition == null) return options;

            var def = view.Definition;

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                int fieldCount = def.GetFieldCount();
                for (int i = 0; i < fieldCount; i++)
                {
                    ScheduleField field;
                    try { field = def.GetField(i); } catch { continue; }
                    if (field == null) continue;
                    bool calc = SafeCalc(field);
                    string name = null, heading = null;
                    try { name = field.GetName(); } catch { }
                    try { heading = field.ColumnHeading; } catch { }
                    GrdLog.Log("CountLength.Field[" + i + "] name=\"" + (name ?? "") + "\" heading=\"" +
                               (heading ?? "") + "\" type=" + SafeType(field) + " calc=" + calc +
                               " agg=" + IsAggregate(field) + " pid=" + (SafeParamId(field)?.ToString() ?? "-"));
                    if (calc) continue;
                    if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(heading)) continue;
                    options.SourceColumns.Add(new CopyScheduleCountLengthParam
                    {
                        Id = i,
                        Name = string.IsNullOrEmpty(name) ? heading : name,
                        ParamName = name
                    });
                }
            }
            catch (Exception ex) { GrdLog.Log("CountLength.CollectParams(source) EXCEPTION: " + ex); }

            foreach (var col in options.SourceColumns)
            {
                var key = col.ParamName ?? col.Name ?? string.Empty;
                if (!known.Add(key)) continue;
                options.TargetParams.Add(new CopyScheduleCountLengthParam { Id = col.Id, Name = col.Name, ParamName = col.ParamName });
            }

            GrdLog.Log("CountLength.CollectParams: columns=" + options.SourceColumns.Count);

            return options;
        }

        private CopyScheduleCountLengthResult ExecuteCopy(UIApplication app, long sourceFieldId, long targetFieldId, string targetParam, bool skipFilled)
        {
            var res = new CopyScheduleCountLengthResult();
            if (string.IsNullOrEmpty(targetParam)) { res.Ok = false; res.Error = "Не выбран параметр."; return res; }

            var uidoc = app?.ActiveUIDocument;
            var doc = uidoc?.Document;
            var view = doc == null ? null : uidoc.ActiveView as ViewSchedule;
            GrdLog.Log("CountLength.ExecuteCopy: sourceField=" + sourceFieldId + " targetField=" + targetFieldId +
                       " target=\"" + targetParam + "\" skipFilled=" + skipFilled + " doc=" + (doc == null ? "<null>" : doc.Title) +
                       " isSchedule=" + (view != null));
            if (doc == null || view?.Definition == null) { res.Ok = false; res.Error = "Нет активной спецификации."; return res; }

            var def = view.Definition;
            DumpDefinition(def);

            ScheduleField sourceField;
            try { sourceField = def.GetField((int)sourceFieldId); } catch { sourceField = null; }
            if (sourceField == null) { res.Ok = false; res.Error = "Не выбрана колонка-источник."; return res; }

            ScheduleField targetField = null;
            try { targetField = def.GetField((int)targetFieldId); } catch { }

            var table = view.GetTableData();
            var section = table?.GetSectionData(SectionType.Body);
            if (section == null) { res.Ok = false; res.Error = "Не удалось получить тело спецификации."; return res; }
            DumpSection(section, "body", 120);

            int cols = section.NumberOfColumns;
            int rows = section.NumberOfRows;

            var colByParam = BuildColumnParamMap(section);
            int colSrc = ResolveColumn(section, sourceField, colByParam);
            GrdLog.Log("CountLength: colSrc=" + colSrc + " bodyCols=" + cols + " bodyRows=" + rows);
            if (colSrc < 0) { res.Ok = false; res.Error = "Не удалось определить колонку-источник."; return res; }

            var sortFields = CollectSortFields(def, sourceField, targetField);
            GrdLog.Log("CountLength: sortFields=" + sortFields.Count + " [" +
                       string.Join(", ", sortFields.Select(s => FieldLabel(s.Field) + (s.Ascending ? " ^" : " v") + (s.Header ? " (hdr)" : ""))) + "]");
            int gfCount = sortFields.Count;

            // 1) Разбор тела. Строки-заголовки групп определяем по содержимому (текст в
            //    колонке 0, остальные пусты), а не по факту объединения ячеек: Revit часто
            //    рисует заголовок простым текстом без слияния. Поле/уровень заголовка
            //    определяем по тому, какому полю сортировки принадлежит его значение; строка
            //    детали получает «путь» из активных заголовков, воспроизводя дерево вложенности.
            var elements = GetScheduleElements(doc, view);

            // Возможные значения каждого поля сортировки — по ним определяем, какому полю
            // принадлежит текст заголовка (значения разных полей, как правило, не пересекаются).
            var valSets = new List<HashSet<string>>();
            for (int c = 0; c < gfCount; c++)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var el in elements)
                {
                    try
                    {
                        var v = FieldValue(doc, el, sortFields[c].Field, out _);
                        if (v != null) set.Add(Norm(v));
                    }
                    catch { }
                }
                valSets.Add(set);
            }

            for (int c = 0; c < gfCount; c++)
            {
                var sf = sortFields[c].Field;
                string meta;
                try
                {
                    meta = "name=\"" + FieldLabel(sf) + "\" pid=" + (SafeParamId(sf)?.ToString() ?? "-") +
                           " type=" + SafeType(sf) + " fieldType=" + sf.FieldType +
                           " fieldIndex=" + sf.FieldIndex +
                           " calc=" + sf.IsCalculatedField + " comb=" + sf.IsCombinedParameterField +
                           " displayType=" + (sf.DisplayType.ToString());
                }
                catch (Exception ex) { meta = "EXC " + ex.Message; }
                int empt = 0, nonNull = 0, nulls = 0;
                var emptyTypes = new List<string>();
                foreach (var el in elements)
                {
                    try
                    {
                        var v = FieldValue(doc, el, sf, out _);
                        if (v == null) { nulls++; continue; }
                        nonNull++;
                        if (Norm(v).Length == 0)
                        {
                            empt++;
                            if (emptyTypes.Count < 8)
                            {
                                string tn = "?";
                                try { tn = doc.GetElement(el.GetTypeId())?.Name ?? "?"; } catch { }
                                emptyTypes.Add(tn);
                            }
                        }
                    }
                    catch { }
                }
                GrdLog.Log("CountLength: sortField[" + c + "] " + meta +
                           " distinct=" + valSets[c].Count + " nonNull=" + nonNull +
                           " empty=" + empt + " nulls=" + nulls +
                           (emptyTypes.Count > 0 ? " emptyTypes=[" + string.Join(", ", emptyTypes) + "]" : ""));
            }

            var pathOrder = new List<string>();
            var detailByPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var detailRows = new List<int>();
            var headerSeen = new bool[gfCount];
            var cur = new string[gfCount];
            int headerRowCount = 0;

            var dumpSb = new StringBuilder();
            for (int r = 1; r < Math.Min(rows, 80); r++)
            {
                string t0 = "", ts = "";
                try { t0 = (section.GetCellText(r, 0) ?? string.Empty).Trim(); } catch { }
                try { ts = (section.GetCellText(r, colSrc) ?? string.Empty).Trim(); } catch { }
                dumpSb.Append(r).Append(":[0]=\"").Append(Escape(t0)).Append("\"[").Append(colSrc).Append("]=\"").Append(Escape(ts)).Append("\" ");
            }
            GrdLog.Log("CountLength: rowsDump " + dumpSb);

            for (int r = 1; r < rows; r++)
            {
                if (IsGroupHeaderRow(section, r, cols, colSrc, out var htext))
                {
                    headerRowCount++;
                    int fieldOf = -1;
                    for (int c = 0; c < gfCount; c++)
                        if (valSets[c].Contains(htext)) { fieldOf = c; break; }
                    if (fieldOf >= 0)
                    {
                        headerSeen[fieldOf] = true;
                        cur[fieldOf] = htext;
                        for (int j = fieldOf + 1; j < gfCount; j++) cur[j] = string.Empty;
                    }
                    else
                    {
                        // Заголовок не сопоставлен полю (иная форматировка/итоговая строка):
                        // обновляем самый глубокий уже известный уровень, не добавляя полей.
                        int d = -1;
                        for (int c = 0; c < gfCount; c++) if (headerSeen[c]) d = c;
                        if (d >= 0) { cur[d] = htext; for (int j = d + 1; j < gfCount; j++) cur[j] = string.Empty; }
                    }
                    continue;
                }
                var pkey = string.Join(KeySeparator, cur);
                if (!detailByPath.TryGetValue(pkey, out var lst)) { lst = new List<string>(); detailByPath[pkey] = lst; pathOrder.Add(pkey); }
                lst.Add((section.GetCellText(r, colSrc) ?? string.Empty).Trim());
                detailRows.Add(r);
            }
            var headerIdx = new List<int>();
            for (int c = 0; c < gfCount; c++) if (headerSeen[c]) headerIdx.Add(c);
            int hCount = headerIdx.Count;
            GrdLog.Log("CountLength: hdrRows=" + headerRowCount + " hdrFields=" + hCount +
                       " [" + string.Join(",", headerIdx) + "] paths=" + pathOrder.Count + " detailRows=" + detailRows.Count);

            if (gfCount == 0 || detailRows.Count == 0 || def.IsItemized)
            {
                GrdLog.Log("CountLength: нет группировки или itemized -> сопоставление строк по колонкам.");
                return ExecuteByColumns(doc, view, def, section, cols, rows, sourceField, colSrc, colByParam, targetField, targetParam);
            }

            // 2) Группируем элементы по значениям полей сортировки/группировки спецификации.
            var groups = new Dictionary<string, GroupInfo>(StringComparer.OrdinalIgnoreCase);
            int nullKeys = 0, used = 0;
            foreach (var el in elements)
            {
                try
                {
                    var values = new string[gfCount];
                    var keyParts = new string[gfCount];
                    bool ok = true;
                    for (int c = 0; c < gfCount; c++)
                    {
                        var v = FieldValue(doc, el, sortFields[c].Field, out _);
                        if (v == null) { ok = false; break; }
                        values[c] = Norm(v);
                        // Revit группирует поле «Тип» (ELEM_TYPE_PARAM / ElementType) по самому
                        // элементу типа, поэтому два типа с одинаковым именем — разные группы;
                        // добавляем идентичность типа в ключ группировки (отображение/путь — по values).
                        string kp = values[c];
                        try
                        {
                            bool typeIdentity = sortFields[c].Field.FieldType == ScheduleFieldType.ElementType;
                            if (!typeIdentity)
                            {
                                var fpid = SafeParamId(sortFields[c].Field);
                                if (fpid != null && fpid.Value == (long)BuiltInParameter.ELEM_TYPE_PARAM)
                                    typeIdentity = true;
                            }
                            if (typeIdentity)
                                kp += "\u0001" + el.GetTypeId().Value.ToString(CultureInfo.InvariantCulture);
                        }
                        catch { }
                        keyParts[c] = kp;
                    }
                    if (!ok) { nullKeys++; continue; }
                    var gkey = string.Join(KeySeparator, keyParts);
                    if (!groups.TryGetValue(gkey, out var gi)) { gi = new GroupInfo { Values = values }; groups[gkey] = gi; }
                    gi.Elements.Add(el);
                    used++;
                }
                catch { }
            }
            GrdLog.Log("CountLength: groups=" + groups.Count + " elementsUsed=" + used + " nullKeyElements=" + nullKeys);

            // 3) Раскладываем группы по «путям» заголовков. Путь — значения заголовочных
            //    полей (headerIdx). Поля сортировки без заголовка остаются внутри группы
            //    и разбираются по агрегату/порядку. Внутри пути строка сопоставляется
            //    группе по агрегату колонки, приведённому к единицам отображения
            //    масштабом (см. ниже), — то есть по числу, видимому в ячейке; сравнение
            //    точное. Равные значения безвредны (в них пишется одно и то же), остаток
            //    разбирается порядком полей сортировки.
            string PathKeyOfGroup(GroupInfo gi)
            {
                var parts = new string[gfCount];
                for (int c = 0; c < gfCount; c++) parts[c] = headerSeen[c] ? gi.Values[c] : string.Empty;
                return string.Join(KeySeparator, parts);
            }

            var byPath = new Dictionary<string, List<GroupInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var gi in groups.Values)
            {
                var pk = PathKeyOfGroup(gi);
                if (!byPath.TryGetValue(pk, out var l)) { l = new List<GroupInfo>(); byPath[pk] = l; }
                l.Add(gi);
            }

            var columnUnit = ResolveColumnUnit(doc, section, colSrc, sourceField);
            GrdLog.Log("CountLength: columnUnit=" + (columnUnit == null ? "<null>" : columnUnit.TypeId));

            // Пред-расчёт. Для каждого заголовка — упорядоченные группы (ключ = значения
            // полей сортировки/группировки, в порядке применения) и агрегаты колонки
            // во внутренних единицах. Затем по суммарному отношению «строка/агрегат»
            // вычисляем масштаб единиц k: он одинаков для всех строк и не зависит от
            // способа агрегации (сумма/макс/мин/среднее линейны по единицам), поэтому
            // сравнение получается точным без угадывания единицы API.
            var pathGroups = new Dictionary<string, List<GroupInfo>>(StringComparer.OrdinalIgnoreCase);
            var pathAggs = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            double sumRowsAll = 0, sumAggAll = 0;
            int applied = 0, skipped = 0, wroteGroups = 0;
            var unmatchedPaths = new List<string>();
            var mismatches = new List<string>();

            foreach (var path in pathOrder)
            {
                if (!byPath.TryGetValue(path, out var list)) { unmatchedPaths.Add(path); continue; }
                list.Sort((a, b) =>
                {
                    for (int q = 0; q < gfCount; q++)
                    {
                        int c = CompareFieldValues(a.Values[q], b.Values[q]);
                        if (c != 0) return sortFields[q].Ascending ? c : -c;
                    }
                    return 0;
                });
                pathGroups[path] = list;

                var vals = detailByPath[path];
                if (list.Count != vals.Count)
                    mismatches.Add("\"" + Escape(path) + "\" группы=" + list.Count + " строк=" + vals.Count);

                var aggs = new double[list.Count];
                for (int j = 0; j < list.Count; j++)
                {
                    aggs[j] = AggregateDisplay(sourceField, list[j].Elements, out _);
                    sumAggAll += aggs[j];
                }
                pathAggs[path] = aggs;

                for (int i = 0; i < vals.Count; i++)
                    if (TryParseNum(vals[i], out var rv)) sumRowsAll += rv;
            }

            double k = (Math.Abs(sumAggAll) > 1e-12) ? sumRowsAll / sumAggAll : 1.0;
            if (!(k > 0) || double.IsNaN(k) || double.IsInfinity(k)) k = 1.0;
            GrdLog.Log("CountLength: unitScale k=" + k.ToString(CultureInfo.InvariantCulture) +
                       " sumRows=" + sumRowsAll.ToString(CultureInfo.InvariantCulture) +
                       " sumAgg=" + sumAggAll.ToString(CultureInfo.InvariantCulture));

            Element probe = null;
            using (var t = new Transaction(doc, "JTOOLS: копировать колонку в параметр"))
            {
                try { t.Start(); } catch (Exception ex) { res.Ok = false; res.Error = "Не удалось начать транзакцию: " + ex.Message; return res; }
                try
                {
                    int dumpTitles = 0;
                    foreach (var path in pathOrder)
                    {
                        if (!pathGroups.TryGetValue(path, out var list)) continue;
                        var vals = detailByPath[path];
                        var aggs = pathAggs[path];
                        int[] assign = MatchRows(vals, aggs, k, out var how);
                        var aggLog = new List<string>();
                        for (int j = 0; j < aggs.Length; j++)
                            aggLog.Add((aggs[j] * k).ToString("0.####", CultureInfo.InvariantCulture));
                        GrdLog.Log("CountLength: path \"" + Escape(path) + "\" группы=" + list.Count +
                                   " строк=" + vals.Count + " match=" + how +
                                   " значения=[" + string.Join(", ", vals) + "]" +
                                   " агрегаты=[" + string.Join(", ", aggLog) + "]");

                        {
                            var gd = new List<string>();
                            for (int j = 0; j < list.Count; j++)
                            {
                                var tids = new List<string>();
                                var seenT = new HashSet<long>();
                                foreach (var e in list[j].Elements)
                                {
                                    long tid;
                                    try { tid = e.GetTypeId().Value; } catch { tid = -1; }
                                    if (seenT.Add(tid)) tids.Add(tid.ToString());
                                }
                                gd.Add("[" + string.Join("|", list[j].Values) + "]=" +
                                       (aggs[j] * k).ToString("0.####", CultureInfo.InvariantCulture) +
                                       "(" + list[j].Elements.Count + ")T{" + string.Join(",", tids) + "}");
                            }
                            GrdLog.Log("CountLength: groupDump \"" + Escape(path) + "\": " + string.Join("; ", gd));
                            dumpTitles++;
                        }

                        for (int i = 0; i < vals.Count; i++)
                        {
                            int gi = (i < assign.Length) ? assign[i] : -1;
                            if (gi < 0 || gi >= list.Count) { skipped++; continue; }
                            var src = vals[i];
                            wroteGroups++;
                            if (src.Length == 0) continue;
                            foreach (var el in list[gi].Elements)
                            {
                                var p = ResolveTarget(el, targetField, targetParam);
                                if (p == null) { skipped++; continue; }
                                if (p.IsReadOnly) { skipped++; continue; }
                                if (skipFilled && IsFilled(p)) { skipped++; continue; }
                                if (probe == null) probe = el;
                                bool setOk;
                                if (p.StorageType == StorageType.String) setOk = p.Set(src);
                                else setOk = p.SetValueString(src);
                                if (setOk) applied++; else skipped++;
                            }
                        }
                    }
                    t.Commit();
                }
                catch (Exception ex)
                {
                    try { t.RollBack(); } catch { }
                    res.Ok = false; res.Error = ex.Message;
                    GrdLog.Log("CountLength: row loop EXCEPTION: " + ex);
                    return res;
                }
            }

            GrdLog.Log("CountLength: wroteGroups=" + wroteGroups + " applied=" + applied + " skipped=" + skipped);
            if (unmatchedPaths.Count > 0)
                GrdLog.Log("CountLength: пути без групп элементов: " + string.Join(", ", unmatchedPaths.Select(x => "\"" + Escape(x) + "\"")));
            if (mismatches.Count > 0)
                GrdLog.Log("CountLength: несовпадение количества: " + string.Join("; ", mismatches));
            if (probe != null)
                GrdLog.Log("CountLength: probe el=\"" + (probe.Name ?? "") + "\" param storage=" +
                           (ResolveTarget(probe, targetField, targetParam)?.StorageType.ToString() ?? "<none>"));

            res.Ok = true;
            res.Applied = applied;
            res.Skipped = skipped;
            res.Message = "Записано элементов: " + applied + " (групп: " + wroteGroups + "), пропущено: " + skipped + ".";
            return res;
        }

        /// <summary>Сравнение значений полей сортировки так же, как это делает Revit:
        /// числа — численно, текст — без учёта регистра по текущей культуре.</summary>
        private static int CompareFieldValues(string a, string b)
        {
            a = a ?? string.Empty;
            b = b ?? string.Empty;
            if (TryParseNum(a, out var na) && TryParseNum(b, out var nb))
                return na.CompareTo(nb);
            return string.Compare(a, b, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
        }

        /// <summary>Очищает целевой параметр у всех элементов активной спецификации.</summary>
        private CopyScheduleCountLengthResult ExecuteClear(UIApplication app, long targetFieldId, string targetParam)
        {
            var res = new CopyScheduleCountLengthResult();
            if (string.IsNullOrEmpty(targetParam)) { res.Ok = false; res.Error = "Не выбран параметр."; return res; }

            var uidoc = app?.ActiveUIDocument;
            var doc = uidoc?.Document;
            var view = doc == null ? null : uidoc.ActiveView as ViewSchedule;
            GrdLog.Log("CountLength.Clear: targetField=" + targetFieldId + " target=\"" + targetParam +
                       "\" doc=" + (doc == null ? "<null>" : doc.Title) + " isSchedule=" + (view != null));
            if (doc == null || view?.Definition == null) { res.Ok = false; res.Error = "Нет активной спецификации."; return res; }

            ScheduleField targetField = null;
            try { targetField = view.Definition.GetField((int)targetFieldId); } catch { }

            var elements = GetScheduleElements(doc, view);
            int cleared = 0, skipped = 0;
            using (var t = new Transaction(doc, "JTOOLS: очистить целевой параметр"))
            {
                try { t.Start(); } catch (Exception ex) { res.Ok = false; res.Error = "Не удалось начать транзакцию: " + ex.Message; return res; }
                try
                {
                    foreach (var el in elements)
                    {
                        var p = ResolveTarget(el, targetField, targetParam);
                        if (p == null) { skipped++; continue; }
                        if (p.IsReadOnly) { skipped++; continue; }
                        bool ok;
                        if (p.StorageType == StorageType.String) ok = p.Set(string.Empty);
                        else ok = p.SetValueString(string.Empty);
                        if (ok) cleared++; else skipped++;
                    }
                    t.Commit();
                }
                catch (Exception ex)
                {
                    try { t.RollBack(); } catch { }
                    res.Ok = false; res.Error = ex.Message;
                    GrdLog.Log("CountLength.Clear EXCEPTION: " + ex);
                    return res;
                }
            }

            GrdLog.Log("CountLength.Clear: cleared=" + cleared + " skipped=" + skipped);
            res.Ok = true;
            res.Applied = cleared;
            res.Skipped = skipped;
            res.Message = "Очищено элементов: " + cleared + ", пропущено: " + skipped + ".";
            return res;
        }

        /// <summary>Единица отображения колонки-источника в теле спецификации
        /// (именно в ней показаны значения строк). В приоритете формат самой колонки.</summary>
        private static ForgeTypeId ResolveColumnUnit(Document doc, TableSectionData section, int colSrc, ScheduleField field)
        {
            try
            {
                var fo = section.GetCellFormatOptions(colSrc, doc);
                var u = fo?.GetUnitTypeId();
                if (u != null) return u;
            }
            catch { }
            try
            {
                var u = field?.GetFormatOptions()?.GetUnitTypeId();
                if (u != null) return u;
            }
            catch { }
            return null;
        }

        /// <summary>Агрегат поля-источника по элементам группы во внутренних единицах
        /// Revit. Способ агрегации берётся из настроек отображения поля (сумма / макс /
        /// мин / среднее / количество), чтобы значение совпало с ячейкой после
        /// масштабирования единиц.</summary>
        private static double AggregateDisplay(ScheduleField field, List<Element> els, out int counted)
        {
            counted = els.Count;
            try
            {
                if (field != null && field.FieldType == ScheduleFieldType.Count) return els.Count;
            }
            catch { }

            var nums = new List<double>();
            var pid = SafeParamId(field);
            if (pid != null)
            {
                foreach (var el in els)
                {
                    try
                    {
                        var p = GetParamById(el, pid);
                        if (p == null) continue;
                        if (p.StorageType == StorageType.Double) nums.Add(p.AsDouble());
                        else if (p.StorageType == StorageType.Integer) nums.Add(p.AsInteger());
                    }
                    catch { }
                }
            }
            counted = nums.Count;

            string dt = "Standard";
            try { dt = field.DisplayType.ToString(); } catch { }
            dt = dt.ToLowerInvariant();

            if (dt.Contains("count")) return els.Count;
            if (nums.Count == 0) return 0;
            if (dt.Contains("max"))
            {
                double m = nums[0];
                foreach (var x in nums) if (x > m) m = x;
                return m;
            }
            if (dt.Contains("min"))
            {
                double m = nums[0];
                foreach (var x in nums) if (x < m) m = x;
                return m;
            }
            if (dt.Contains("average") || dt.Contains("avg") || dt.Contains("mean"))
            {
                double s = 0;
                foreach (var x in nums) s += x;
                return s / nums.Count;
            }
            double sum = 0;
            foreach (var x in nums) sum += x;
            return sum;
        }

        /// <summary>Сопоставление строк и групп по агрегату, приведённому к единицам
        /// отображения колонки масштабом scale: точное равенство после округления до
        /// точности, которую показывает спецификация; остаток — по порядку полей
        /// сортировки (устойчиво к совпадающим значениям, например двум нулям).</summary>
        private static int[] MatchRows(List<string> rows, double[] agg, double scale, out string how)
        {
            int n = agg.Length, m = rows.Count;
            var assign = new int[m];
            for (int i = 0; i < m; i++) assign[i] = -1;

            var rowv = new double[m];
            bool allNums = m > 0;
            int maxDec = 0;
            for (int i = 0; i < m; i++)
            {
                if (!TryParseNum(rows[i], out rowv[i])) { allNums = false; break; }
                maxDec = Math.Max(maxDec, DecimalPlaces(rows[i]));
            }

            if (allNums && n > 0)
            {
                var ra = new double[n];
                for (int j = 0; j < n; j++) ra[j] = RoundTo(agg[j] * scale, maxDec);
                var rr = new double[m];
                for (int i = 0; i < m; i++) rr[i] = RoundTo(rowv[i], maxDec);

                var usedG = new bool[n];
                for (int i = 0; i < m; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        if (usedG[j]) continue;
                        if (Math.Abs(ra[j] - rr[i]) <= 1e-9) { assign[i] = j; usedG[j] = true; break; }
                    }
                }

                bool anyUnmatched = false;
                for (int i = 0; i < m; i++) if (assign[i] < 0) { anyUnmatched = true; break; }
                if (!anyUnmatched) { how = "value"; return assign; }

                how = "value+order";
                int g = 0;
                for (int i = 0; i < m; i++)
                    if (assign[i] < 0)
                    {
                        while (g < n && usedG[g]) g++;
                        if (g < n) { assign[i] = g; usedG[g] = true; }
                    }
                return assign;
            }

            how = "order";
            int nn = Math.Min(n, m);
            for (int i = 0; i < nn; i++) assign[i] = i;
            return assign;
        }

        private static int DecimalPlaces(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int dot = -1;
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '.' || s[i] == ',') dot = i;
            if (dot < 0) return 0;
            int c = 0;
            for (int i = dot + 1; i < s.Length; i++)
                if (s[i] >= '0' && s[i] <= '9') c++;
            return c;
        }

        private static double RoundTo(double v, int d)
        {
            if (d <= 0) return Math.Round(v, MidpointRounding.AwayFromZero);
            double f = Math.Pow(10, d);
            return Math.Round(v * f, MidpointRounding.AwayFromZero) / f;
        }

        private static bool TryParseNum(string s, out double v)
        {
            v = 0;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim().Replace("\u00A0", string.Empty).Replace("\u2009", string.Empty)
                 .Replace("\u202F", string.Empty).Replace(" ", string.Empty);
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out v)) return true;
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return true;
            return false;
        }

        private sealed class GroupInfo
        {
            public string[] Values;
            public List<Element> Elements = new List<Element>();
        }

        /// <summary>Сопоставление строк и элементов по значениям отображаемых колонок.
        /// Применяется, когда спецификация не сгруппирована (заголовков групп нет):
        /// каждая строка соответствует элементу (или нескольким неотличимым элементам).
        /// Не зависит от конкретных полей и локализации.</summary>
        private CopyScheduleCountLengthResult ExecuteByColumns(
            Document doc, ViewSchedule view, ScheduleDefinition def, TableSectionData section,
            int cols, int rows, ScheduleField sourceField, int colSrc,
            Dictionary<long, int> colByParam, ScheduleField targetField, string targetParam)
        {
            var res = new CopyScheduleCountLengthResult();

            var idFields = new List<ScheduleField>();
            var idCols = new List<int>();
            int fc = def.GetFieldCount();
            for (int i = 0; i < fc; i++)
            {
                ScheduleField f;
                try { f = def.GetField(i); } catch { continue; }
                if (f == null) continue;
                bool hidden;
                try { hidden = f.IsHidden; } catch { hidden = false; }
                if (hidden) continue;
                if (SafeCalc(f) || SafeCombined(f)) continue;
                if (IsAggregate(f)) continue;
                int c = ResolveColumn(section, f, colByParam);
                if (c < 0 || c >= cols) continue;
                idFields.Add(f);
                idCols.Add(c);
            }
            if (idFields.Count == 0) { res.Ok = false; res.Error = "Не удалось определить колонки для сопоставления строк."; return res; }

            GrdLog.Log("CountLength: columns-match fields=[" + string.Join(", ", idFields.Select(FieldLabel)) + "]");

            var elements = GetScheduleElements(doc, view);
            var index = new Dictionary<string, List<Element>>(StringComparer.Ordinal);
            foreach (var el in elements)
            {
                try
                {
                    var parts = new string[idFields.Count];
                    for (int k = 0; k < idFields.Count; k++)
                    {
                        var v = FieldValue(doc, el, idFields[k], out _);
                        parts[k] = v == null ? "\u0002" : Norm(v);
                    }
                    var key = string.Join(KeySeparator, parts);
                    if (!index.TryGetValue(key, out var l)) { l = new List<Element>(); index[key] = l; }
                    l.Add(el);
                }
                catch { }
            }

            int applied = 0, skipped = 0, matched = 0;
            using (var t = new Transaction(doc, "JTOOLS: копировать колонку в параметр"))
            {
                try { t.Start(); } catch (Exception ex) { res.Ok = false; res.Error = "Не удалось начать транзакцию: " + ex.Message; return res; }
                try
                {
                    for (int r = 1; r < rows; r++)
                    {
                        var parts = new string[idFields.Count];
                        bool anyNonEmpty = false;
                        for (int k = 0; k < idFields.Count; k++)
                        {
                            var v = Norm(section.GetCellText(r, idCols[k]));
                            parts[k] = v;
                            if (v.Length > 0) anyNonEmpty = true;
                        }
                        if (!anyNonEmpty) continue;
                        var key = string.Join(KeySeparator, parts);
                        if (!index.TryGetValue(key, out var list) || list.Count == 0) continue;
                        var el = list[0];
                        list.RemoveAt(0);
                        matched++;
                        var src = (section.GetCellText(r, colSrc) ?? string.Empty).Trim();
                        if (src.Length == 0) { skipped++; continue; }
                        var p = ResolveTarget(el, targetField, targetParam);
                        if (p == null || p.IsReadOnly) { skipped++; continue; }
                        bool ok = p.StorageType == StorageType.String ? p.Set(src) : p.SetValueString(src);
                        if (ok) applied++; else skipped++;
                    }
                    t.Commit();
                }
                catch (Exception ex)
                {
                    try { t.RollBack(); } catch { }
                    res.Ok = false; res.Error = ex.Message;
                    GrdLog.Log("CountLength: columns-match EXCEPTION: " + ex);
                    return res;
                }
            }

            GrdLog.Log("CountLength: columns-match matched=" + matched + " applied=" + applied + " skipped=" + skipped);
            res.Ok = true;
            res.Applied = applied;
            res.Skipped = skipped;
            res.Message = "Записано элементов: " + applied + ", пропущено: " + skipped + ".";
            return res;
        }

        /// <summary>Строка-заголовок группы: в колонке 0 есть текст, а все прочие
        /// ячейки строки пусты (Revit не всегда объединяет ячейки заголовка).</summary>
        private static bool IsGroupHeaderRow(TableSectionData s, int r, int cols, int colSrc, out string text)
        {
            text = string.Empty;
            try
            {
                if (colSrc <= 0) return false;
                var t0 = (s.GetCellText(r, 0) ?? string.Empty).Trim();
                if (t0.Length == 0)
                {
                    // Полностью пустая строка — заголовок группы с пустым значением поля
                    // группировки (Revit рисует его пустым). Не считаем её строкой данных.
                    for (int c = 0; c < cols; c++)
                        if (!string.IsNullOrEmpty((s.GetCellText(r, c) ?? string.Empty).Trim())) return false;
                    text = string.Empty;
                    return true;
                }
                for (int c = 1; c < cols; c++)
                    if (!string.IsNullOrEmpty((s.GetCellText(r, c) ?? string.Empty).Trim())) return false;
                text = Norm(t0);
                return true;
            }
            catch { }
            return false;
        }

        private static List<Element> GetScheduleElements(Document doc, ViewSchedule view)
        {
            try
            {
                var list = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToList();
                GrdLog.Log("CountLength: schedule collector -> " + list.Count + " elements");
                if (list.Count > 0) return list;
            }
            catch (Exception ex) { GrdLog.Log("CountLength: schedule collector failed: " + ex.Message); }
            var all = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToList();
            GrdLog.Log("CountLength: fallback collector -> " + all.Count + " elements");
            return all;
        }

        private sealed class SortKeyField
        {
            public ScheduleField Field;
            public bool Ascending = true;
            public bool Header;
        }

        /// <summary>Поля диалога «Сортировка/Группировка» в порядке применения — именно
        /// они задают порядок строк тела и разбиение на группы.</summary>
        private static List<SortKeyField> CollectSortFields(ScheduleDefinition def, ScheduleField source, ScheduleField target)
        {
            var list = new List<SortKeyField>();
            try
            {
                int gc = def.GetSortGroupFieldCount();
                for (int i = 0; i < gc; i++)
                {
                    var sgf = def.GetSortGroupField(i);
                    if (sgf == null) continue;
                    ScheduleField f = null;
                    try { f = def.GetField(sgf.FieldId); } catch { }
                    if (f == null || IsAggregate(f)) continue;
                    var item = new SortKeyField { Field = f };
                    try { item.Ascending = sgf.SortOrder == ScheduleSortOrder.Ascending; } catch { }
                    try { item.Header = sgf.ShowHeader; } catch { }
                    list.Add(item);
                }
            }
            catch (Exception ex) { GrdLog.Log("CountLength: CollectSortFields EXCEPTION: " + ex.Message); }
            if (list.Count > 0) return list;

            try
            {
                int fc = def.GetFieldCount();
                for (int i = 0; i < fc; i++)
                {
                    var f = def.GetField(i);
                    if (f == null) continue;
                    if (SafeCalc(f) || SafeCombined(f)) continue;
                    if (IsAggregate(f)) continue;
                    if (source != null && f.FieldIndex == source.FieldIndex) continue;
                    if (target != null && f.FieldIndex == target.FieldIndex) continue;
                    list.Add(new SortKeyField { Field = f });
                }
            }
            catch (Exception ex) { GrdLog.Log("CountLength: key fallback EXCEPTION: " + ex.Message); }
            return list;
        }

        private static bool IsAggregate(ScheduleField f)
        {
            try
            {
                if (f.FieldType == ScheduleFieldType.Count) return true;
                var pid = SafeParamId(f);
                if (pid != null && pid == new ElementId((long)BuiltInParameter.CURVE_ELEM_LENGTH)) return true;
            }
            catch { }
            return false;
        }

        private static bool IsTypeNameParameter(ElementId pid)
        {
            return pid == new ElementId((long)BuiltInParameter.ELEM_TYPE_PARAM)
                || pid == new ElementId((long)BuiltInParameter.SYMBOL_NAME_PARAM);
        }

        /// <summary>Карта «ParameterId колонки -> индекс колонки» в секции тела.
        /// GetCellParamId напрямую сообщает, какой параметр показывает колонка,
        /// поэтому не зависит от локализации заголовков.</summary>
        private static Dictionary<long, int> BuildColumnParamMap(TableSectionData section)
        {
            var map = new Dictionary<long, int>();
            if (section == null) return map;
            int cols = section.NumberOfColumns;
            for (int c = 0; c < cols; c++)
            {
                var pid = CellParamId(section, c);
                if (pid != null && !map.ContainsKey(pid.Value))
                    map[pid.Value] = c;
            }
            return map;
        }

        private static ElementId CellParamId(TableSectionData section, int col)
        {
            try
            {
                var p = section.GetCellParamId(col);
                if (p != null && p != ElementId.InvalidElementId) return p;
            }
            catch { }
            try
            {
                var p = section.GetCellParamId(0, col);
                if (p != null && p != ElementId.InvalidElementId) return p;
            }
            catch { }
            return null;
        }

        private static int ResolveColumn(TableSectionData section, ScheduleField field, Dictionary<long, int> colByParam)
        {
            int cols = section.NumberOfColumns;
            var pid = SafeParamId(field);
            if (pid != null && colByParam != null && colByParam.TryGetValue(pid.Value, out var byParam))
                return byParam;
            var heading = (field.ColumnHeading ?? string.Empty).Trim();
            if (heading.Length > 0)
            {
                for (int c = 0; c < cols; c++)
                {
                    var header = (section.GetCellText(0, c) ?? string.Empty).Trim();
                    if (string.Equals(header, heading, StringComparison.OrdinalIgnoreCase))
                        return c;
                }
            }
            int index = field.FieldIndex;
            return (index >= 0 && index < cols) ? index : -1;
        }

        private static void DumpSection(TableSectionData s, string label, int maxRows)
        {
            if (s == null) { GrdLog.Log("CountLength.Dump " + label + ": <null>"); return; }
            int cols = s.NumberOfColumns, rows = s.NumberOfRows;
            GrdLog.Log("CountLength.Dump " + label + ": rows=" + rows + " cols=" + cols);
            var head = new StringBuilder();
            for (int c = 0; c < cols; c++)
            {
                var pid = CellParamId(s, c);
                head.Append("\n  col ").Append(c).Append(" pid=").Append(pid?.ToString() ?? "-");
            }
            GrdLog.Log("CountLength.Dump " + label + " columnParamIds:" + head);
            int lim = Math.Min(rows, maxRows);
            for (int r = 0; r < lim; r++)
            {
                var sb = new StringBuilder();
                sb.Append("CountLength.Dump ").Append(label).Append(" r").Append(r).Append(":");
                for (int c = 0; c < cols; c++)
                {
                    string t;
                    try { t = s.GetCellText(r, c); } catch { t = "<err>"; }
                    sb.Append(" [").Append(c).Append("]=\"").Append(Escape(t)).Append('"');
                    try
                    {
                        var m = s.GetMergedCell(r, c);
                        if (m != null && (m.Top != r || m.Bottom != r || m.Left != c || m.Right != c))
                            sb.Append("{m").Append(m.Top).Append(',').Append(m.Left).Append('-')
                              .Append(m.Bottom).Append(',').Append(m.Right).Append('}');
                    }
                    catch { }
                }
                GrdLog.Log(sb.ToString());
            }
        }

        private static void DumpDefinition(ScheduleDefinition def)
        {
            try
            {
                var order = def.GetFieldOrder();
                GrdLog.Log("CountLength.Def: fieldCount=" + def.GetFieldCount() +
                           " fieldOrder=[" + string.Join(",", order.Select(id => id.ToString())) + "]" +
                           " sortGroupCount=" + def.GetSortGroupFieldCount());

                int n = def.GetFieldCount();
                for (int i = 0; i < n; i++)
                {
                    ScheduleField f;
                    try { f = def.GetField(i); } catch { continue; }
                    if (f == null) continue;
                    string hidden;
                    try { hidden = f.IsHidden ? "hidden" : "shown"; } catch { hidden = "?"; }
                    GrdLog.Log("CountLength.Def field[" + i + "] \"" + FieldLabel(f) + "\" " + hidden +
                               " pid=" + (SafeParamId(f)?.ToString() ?? "-") + " type=" + SafeType(f));
                }

                int sc = def.GetSortGroupFieldCount();
                for (int i = 0; i < sc; i++)
                {
                    ScheduleSortGroupField sg;
                    try { sg = def.GetSortGroupField(i); } catch { continue; }
                    if (sg == null) continue;
                    string fn = "?";
                    try { fn = FieldLabel(def.GetField(sg.FieldId)); } catch { }
                    string ord;
                    try { ord = sg.SortOrder.ToString(); } catch { ord = "?"; }
                    string show;
                    try { show = " hdr=" + sg.ShowHeader + " ftr=" + sg.ShowFooter + " blank=" + sg.ShowBlankLine; }
                    catch { show = ""; }
                    GrdLog.Log("CountLength.Def sortGroup[" + i + "] fieldId=" + sg.FieldId +
                               " \"" + fn + "\" order=" + ord + show);
                }
            }
            catch (Exception ex) { GrdLog.Log("CountLength.DumpDefinition EXCEPTION: " + ex.Message); }
        }

        /// <summary>Строка, которую показывает спецификация для этого поля у элемента.
        /// Резолвим параметр по ParameterId (не зависит от локализации), затем по имени.</summary>
        private static string FieldValue(Document doc, Element el, ScheduleField field, out string via)
        {
            via = "?";
            try
            {
                var ftype = field.FieldType;
                bool typeName = SafeParamId(field) != null && IsTypeNameParameter(SafeParamId(field));

                if (ftype == ScheduleFieldType.ElementType || ftype == ScheduleFieldType.PhysicalType || typeName)
                {
                    var tid = el.GetTypeId();
                    if (tid == null || tid == ElementId.InvalidElementId) { via = "no-type"; return null; }
                    var type = doc.GetElement(tid);
                    if (type == null) { via = "type-null"; return null; }
                    var heading = (field.ColumnHeading ?? string.Empty).ToLowerInvariant();
                    bool familyAndType = heading.Contains("family and type") || heading.Contains("семейство и тип");
                    if (familyAndType)
                    {
                        via = "family:type";
                        return ((type as ElementType)?.FamilyName ?? string.Empty) + ": " + type.Name;
                    }
                    via = "type-name";
                    return type.Name;
                }

                var pid = SafeParamId(field);
                Parameter p = null;
                if (pid != null && pid != ElementId.InvalidElementId)
                {
                    p = GetParamById(el, pid);
                    via = "pid=" + pid;
                }
                if (p == null)
                {
                    var name = field.GetName();
                    if (string.IsNullOrEmpty(name)) { via += " no-name"; return null; }
                    p = Lookup(el, name);
                    via += " name=" + name;
                }
                if (p == null) { via += " =null"; return null; }
                if (p.StorageType == StorageType.String) return p.AsString() ?? string.Empty;
                return p.AsValueString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                via = "EXC " + ex.Message;
                return null;
            }
        }

        private static Parameter ResolveTarget(Element el, ScheduleField targetField, string targetParam)
        {
            Parameter p = null;
            var pid = SafeParamId(targetField);
            if (pid != null && pid != ElementId.InvalidElementId) p = GetParamById(el, pid);
            if (p == null && !string.IsNullOrEmpty(targetParam)) p = Lookup(el, targetParam);
            return p;
        }

        /// <summary>Параметр считается заполненным: текст — непустая строка,
        /// число — не 0, ElementId — не пустой.</summary>
        private static bool IsFilled(Parameter p)
        {
            if (p == null || !p.HasValue) return false;
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String: return !string.IsNullOrEmpty(p.AsString());
                    case StorageType.Double: return p.AsDouble() != 0.0;
                    case StorageType.Integer: return p.AsInteger() != 0;
                    case StorageType.ElementId:
                        var id = p.AsElementId();
                        return id != null && id != ElementId.InvalidElementId;
                    default: return false;
                }
            }
            catch { return false; }
        }

        private static Parameter GetParamById(Element el, ElementId pid)
        {
            try
            {
                var doc = el.Document;
                bool builtIn = pid.Value < 0;
                if (builtIn)
                {
                    try
                    {
                        var bp = (BuiltInParameter)(int)pid.Value;
                        var pb = el.get_Parameter(bp);
                        if (pb != null) return pb;
                        var tid0 = el.GetTypeId();
                        if (tid0 != null && tid0 != ElementId.InvalidElementId)
                        {
                            var pt = doc.GetElement(tid0)?.get_Parameter(bp);
                            if (pt != null) return pt;
                        }
                    }
                    catch { }
                }
                var def = (doc.GetElement(pid) as ParameterElement)?.GetDefinition();
                if (def == null) return null;
                var p = el.get_Parameter(def);
                if (p != null) return p;
                var tid = el.GetTypeId();
                if (tid != null && tid != ElementId.InvalidElementId)
                    return doc.GetElement(tid)?.get_Parameter(def);
            }
            catch { }
            return null;
        }

        private static Parameter Lookup(Element el, string name)
        {
            var p = el.LookupParameter(name);
            if (p != null) return p;
            var tid = el.GetTypeId();
            if (tid != null && tid != ElementId.InvalidElementId)
                return el.Document.GetElement(tid)?.LookupParameter(name);
            return null;
        }

        private static ElementId SafeParamId(ScheduleField field)
        {
            if (field == null) return null;
            try { return field.ParameterId; } catch { return null; }
        }

        private static bool SafeCalc(ScheduleField field)
        {
            try { return field.IsCalculatedField; } catch { return false; }
        }

        private static bool SafeCombined(ScheduleField field)
        {
            try { return field.IsCombinedParameterField; } catch { return false; }
        }

        private static string SafeType(ScheduleField field)
        {
            try { return field.FieldType.ToString(); } catch { return "?"; }
        }

        private static string FieldLabel(ScheduleField field)
        {
            try
            {
                var name = field.GetName();
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
            try { return field.ColumnHeading ?? "?"; } catch { return "?"; }
        }

        /// <summary>Нормализация пробелов (в т.ч. неразрывных), чтобы текст ячейки
        /// спецификации совпадал со значением параметра элемента.</summary>
        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (var ch in s)
            {
                bool isSpace = ch == ' ' || ch == '\u00A0' || ch == '\u2009' || ch == '\u202F' || ch == '\t' || ch == '\r' || ch == '\n';
                if (isSpace) { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            return (s ?? string.Empty).Replace(KeySeparator, " | ").Replace("\n", " ").Replace("\r", " ");
        }
    }
}
