using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace GrdRevit.Revit
{
    /// <summary>Строка вида для таблицы «Управляющий видами».</summary>
    public sealed class ViewInfoItem
    {
        public long Id;
        public string Name = string.Empty;
        public string KindText = string.Empty;
        public string KindKey = string.Empty; // "section" | "plan" | "3d"
        public string Scale = string.Empty;
        public bool IsTemplate;
    }

    /// <summary>Результат чтения списка видов документа.</summary>
    public sealed class ViewsResult
    {
        public string Error;
        public List<ViewInfoItem> Views = new List<ViewInfoItem>();
        public List<TemplateInfoItem> Templates = new List<TemplateInfoItem>();
    }

    /// <summary>Шаблон вида для диалога выбора.</summary>
    public sealed class TemplateInfoItem
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string KindText { get; set; } = string.Empty;
    }

    /// <summary>Результат операции с видом (дублирование, переименование, открытие).</summary>
    public sealed class ViewOpResult
    {
        public bool Ok;
        public string Error = string.Empty;
        public string Message = string.Empty;
        public string NewId; // id созданного вида (при дублировании)
    }

    /// <summary>Результат пакетной операции над несколькими видами (добавление префикса).</summary>
    public sealed class BatchOpResult
    {
        public bool Ok;
        public string Error = string.Empty;
        public string Message = string.Empty;
        public int Applied;
        public int Skipped;
    }

    /// <summary>
    /// Читает виды активного документа, дублирует, переименовывает и открывает их.
    /// Моделесс-окно не может обращаться к API напрямую — запросы ставятся в очередь
    /// и выполняются Revit-потоком через ExternalEvent.
    /// </summary>
    public class ViewsManagerHandler : IExternalEventHandler
    {
        private readonly object _sync = new object();
        private readonly Queue<Request> _requests = new Queue<Request>();

        private enum Kind { Read, Duplicate, Rename, Open, ReadTemplates, ApplyTemplate, AddPrefix }

        private sealed class Request
        {
            public Kind Kind;
            public long Id;
            public string Name = string.Empty;
            public List<long> Ids = new List<long>();
            public Action<ViewsResult> ReadCallback;
            public Action<ViewOpResult> OpCallback;
            public Action<BatchOpResult> BatchCallback;
        }

        public void QueueRead(Action<ViewsResult> callback)
        {
            lock (_sync)
            {
                _requests.Enqueue(new Request { Kind = Kind.Read, ReadCallback = callback });
            }
        }

        public void QueueDuplicate(long id, Action<ViewOpResult> callback)
        {
            lock (_sync)
            {
                _requests.Enqueue(new Request { Kind = Kind.Duplicate, Id = id, OpCallback = callback });
            }
        }

        public void QueueRename(long id, string name, Action<ViewOpResult> callback)
        {
            lock (_sync)
            {
                _requests.Enqueue(new Request { Kind = Kind.Rename, Id = id, Name = name ?? string.Empty, OpCallback = callback });
            }
        }

        public void QueueOpen(long id, Action<ViewOpResult> callback)
        {
            lock (_sync)
            {
                _requests.Enqueue(new Request { Kind = Kind.Open, Id = id, OpCallback = callback });
            }
        }

        public void QueueReadTemplates(Action<ViewsResult> callback)
        {
            lock (_sync)
            {
                _requests.Enqueue(new Request { Kind = Kind.ReadTemplates, ReadCallback = callback });
            }
        }

        /// <summary>Применить (link=false) или связать (link=true) шаблон к виду.</summary>
        public void QueueApplyTemplate(long viewId, long templateId, bool link, Action<ViewOpResult> callback)
        {
            lock (_sync)
            {
                _requests.Enqueue(new Request
                {
                    Kind = Kind.ApplyTemplate,
                    Id = viewId,
                    Name = templateId.ToString() + "|" + link.ToString(),
                    OpCallback = callback
                });
            }
        }

        /// <summary>Добавить префикс к именам отмеченных видов (одной транзакцией).</summary>
        public void QueueAddPrefix(IEnumerable<long> ids, string prefix, Action<BatchOpResult> callback)
        {
            lock (_sync)
            {
                _requests.Enqueue(new Request
                {
                    Kind = Kind.AddPrefix,
                    Ids = new List<long>(ids ?? new List<long>()),
                    Name = prefix ?? string.Empty,
                    BatchCallback = callback
                });
            }
        }

        public string GetName()
        {
            return "JTOOLS: управляющий видами";
        }

        public void Execute(UIApplication app)
        {
            List<Request> batch;
            lock (_sync)
            {
                if (_requests.Count == 0)
                {
                    GrdLog.Log("ViewsManagerHandler.Execute: нет запросов в очереди");
                    return;
                }
                batch = new List<Request>(_requests);
                _requests.Clear();
            }

            foreach (var req in batch)
            {
                try
                {
                    switch (req.Kind)
                    {
                        case Kind.Read:
                            req.ReadCallback?.Invoke(Read(app));
                            break;
                        case Kind.Duplicate:
                            req.OpCallback?.Invoke(Duplicate(app, req.Id));
                            break;
                        case Kind.Rename:
                            req.OpCallback?.Invoke(Rename(app, req.Id, req.Name));
                            break;
                        case Kind.Open:
                            req.OpCallback?.Invoke(Open(app, req.Id));
                            break;
                        case Kind.AddPrefix:
                            req.BatchCallback?.Invoke(AddPrefix(app, req.Ids, req.Name));
                            break;
                        case Kind.ReadTemplates:
                            req.ReadCallback?.Invoke(ReadTemplates(app));
                            break;
                        case Kind.ApplyTemplate:
                        {
                            var parts = req.Name.Split('|');
                            long templateId;
                            bool link = false;
                            if (parts.Length == 2 && long.TryParse(parts[0], out templateId) && bool.TryParse(parts[1], out link))
                                req.OpCallback?.Invoke(ApplyTemplate(app, req.Id, templateId, link));
                            else
                                req.OpCallback?.Invoke(new ViewOpResult { Ok = false, Error = "Не удалось разобрать запрос на применение шаблона." });
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    GrdLog.Log("ViewsManagerHandler.Execute: EXCEPTION: " + ex);
                    try
                    {
                        if (req.Kind == Kind.Read || req.Kind == Kind.ReadTemplates)
                            req.ReadCallback?.Invoke(new ViewsResult { Error = "Ошибка чтения видов: " + ex.Message });
                        else if (req.Kind == Kind.AddPrefix)
                            req.BatchCallback?.Invoke(new BatchOpResult { Ok = false, Error = ex.Message });
                        else
                            req.OpCallback?.Invoke(new ViewOpResult { Ok = false, Error = ex.Message });
                    }
                    catch { }
                }
            }
        }

        private static ViewsResult Read(UIApplication app)
        {
            var res = new ViewsResult();
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null)
            {
                res.Error = "Нет активного документа Revit.";
                return res;
            }

            var list = new List<ViewInfoItem>();
            foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)))
            {
                try
                {
                    if (v.IsTemplate) continue;

                    var item = new ViewInfoItem
                    {
                        Id = v.Id.Value,
                        Name = v.Name,
                        KindKey = KindKeyOf(v.ViewType)
                    };
                    item.KindText = KindTextOf(v.ViewType);
                    if (item.KindKey == null)
                    {
                        // Показываем только значимые категории (план/разрез/3D и близкие).
                        if (v.ViewType == ViewType.DraftingView || v.ViewType == ViewType.Schedule ||
                            v.ViewType == ViewType.Legend) continue;
                        item.KindKey = "other";
                    }
                    try
                    {
                        int s = v.Scale;
                        item.Scale = s > 0 ? "1:" + s : "—";
                    }
                    catch { item.Scale = "—"; }

                    list.Add(item);
                }
                catch { }
            }

            res.Views = list
                .OrderBy(x => x.KindText, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            GrdLog.Log("ViewsManager.Read: собрано видов=" + res.Views.Count);
            return res;
        }

        private static string KindKeyOf(ViewType t)
        {
            switch (t)
            {
                case ViewType.Section:
                case ViewType.Elevation:
                    return "section";
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.AreaPlan:
                case ViewType.EngineeringPlan:
                    return "plan";
                case ViewType.ThreeD:
                    return "3d";
                case ViewType.DrawingSheet:
                    return "sheet";
                default:
                    return null;
            }
        }

        private static string KindTextOf(ViewType t)
        {
            switch (t)
            {
                case ViewType.Section: return "Разрез";
                case ViewType.Elevation: return "Фасад";
                case ViewType.FloorPlan: return "План этажа";
                case ViewType.CeilingPlan: return "План потолка";
                case ViewType.AreaPlan: return "План площадей";
                case ViewType.EngineeringPlan: return "План инж. систем";
                case ViewType.ThreeD: return "3D";
                case ViewType.DrawingSheet: return "Лист";
                default: return t.ToString();
            }
        }

        private static ViewOpResult Err(string message)
        {
            GrdLog.Log("ViewsManager: ОШИБКА " + message);
            return new ViewOpResult { Ok = false, Error = message };
        }

        private static ViewOpResult Duplicate(UIApplication app, long id)
        {
            var doc = app?.ActiveUIDocument?.Document;
            var v = doc != null ? doc.GetElement(new ElementId(id)) as View : null;
            if (v == null) return Err("Вид не найден или документ неактивен.");

            long dupId = 0;
            using (var t = new Transaction(doc, "JTOOLS: дублировать вид"))
            {
                try { t.Start(); } catch (Exception ex) { return Err("Не удалось начать транзакцию: " + ex.Message); }

                try
                {
                    dupId = v.Duplicate(ViewDuplicateOption.Duplicate).Value;
                    var dup = dupId != ElementId.InvalidElementId.Value
                        ? doc.GetElement(new ElementId(dupId)) as View
                        : null;
                    if (dup == null)
                    {
                        try { t.RollBack(); } catch { }
                        return Err("Не удалось дублировать вид.");
                    }

                    var taken = new HashSet<string>(
                        new FilteredElementCollector(doc).OfClass(typeof(View)).Select(x => x.Name),
                        StringComparer.OrdinalIgnoreCase);
                    var baseName = v.Name;
                    var newName = baseName + " (копия)";
                    int n = 2;
                    while (taken.Contains(newName))
                        newName = baseName + " (копия " + (n++) + ")";
                    try { dup.Name = newName; } catch { }

                    t.Commit();
                }
                catch (Exception ex)
                {
                    try { t.RollBack(); } catch { }
                    return Err("Невозможно дублировать этот вид: " + ex.Message);
                }
            }

            GrdLog.Log("ViewsManager: дублирован «" + v.Name + "»");
            return new ViewOpResult
            {
                Ok = true,
                NewId = dupId.ToString(),
                Message = "Дублирован: " + v.Name
            };
        }

        private static ViewOpResult Rename(UIApplication app, long id, string newName)
        {
            var doc = app?.ActiveUIDocument?.Document;
            var v = doc != null ? doc.GetElement(new ElementId(id)) as View : null;
            if (v == null) return Err("Вид не найден или документ неактивен.");
            newName = (newName ?? string.Empty).Trim();
            if (newName.Length == 0) return Err("Имя вида не может быть пустым.");
            if (string.Equals(v.Name, newName, StringComparison.Ordinal)) return new ViewOpResult { Ok = true, Message = "Имя не изменилось." };

            var clash = new FilteredElementCollector(doc).OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(x => x.Id.Value != id && string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase));
            if (clash != null) return Err("Вид с именем «" + newName + "» уже существует.");

            using (var t = new Transaction(doc, "JTOOLS: переименовать вид"))
            {
                try { t.Start(); } catch (Exception ex) { return Err("Не удалось начать транзакцию: " + ex.Message); }
                try
                {
                    v.Name = newName;
                }
                catch (Exception ex)
                {
                    try { t.RollBack(); } catch { }
                    return Err("Не удалось переименовать: " + ex.Message);
                }
                try { t.Commit(); }
                catch (Exception ex)
                {
                    try { t.RollBack(); } catch { }
                    return Err("Ошибка сохранения имени: " + ex.Message);
                }
            }
            GrdLog.Log("ViewsManager: переименован «" + v.Name + "»");
            return new ViewOpResult { Ok = true, Message = "Переименован: " + v.Name };
        }

        private static ViewOpResult Open(UIApplication app, long id)
        {
            var uidoc = app?.ActiveUIDocument;
            var v = uidoc?.Document.GetElement(new ElementId(id)) as View;
            if (v == null) return Err("Вид не найден или документ неактивен.");
            try
            {
                uidoc.RequestViewChange(v);
            }
            catch (Exception ex)
            {
                return Err("Не удалось открыть вид: " + ex.Message);
            }
            GrdLog.Log("ViewsManager: открыт «" + v.Name + "»");
            return new ViewOpResult { Ok = true, Message = "Открыт: " + v.Name };
        }

        private static ViewsResult ReadTemplates(UIApplication app)
        {
            var res = new ViewsResult();
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null)
            {
                res.Error = "Нет активного документа Revit.";
                return res;
            }

            foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)))
            {
                try
                {
                    if (!v.IsTemplate) continue;
                    var kindKey = KindKeyOf(v.ViewType);
                    if (kindKey == null || kindKey == "other") continue;
                    res.Templates.Add(new TemplateInfoItem
                    {
                        Id = v.Id.Value,
                        Name = v.Name,
                        KindText = KindTextOf(v.ViewType)
                    });
                }
                catch { }
            }

            res.Templates.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            GrdLog.Log("ViewsManager.ReadTemplates: шаблонов=" + res.Templates.Count);
            return res;
        }

        /// <summary>link=false — разово применить свойства шаблона к виду; link=true — связать вид с шаблоном.</summary>
        private static ViewOpResult ApplyTemplate(UIApplication app, long viewId, long templateId, bool link)
        {
            var doc = app?.ActiveUIDocument?.Document;
            var v = doc != null ? doc.GetElement(new ElementId(viewId)) as View : null;
            if (v == null) return Err("Вид не найден или документ неактивен.");

            var template = doc.GetElement(new ElementId(templateId)) as View;
            if (template == null || !template.IsTemplate)
                return Err("Шаблон не найден.");
            if (link && v.ViewTemplateId == template.Id)
                return new ViewOpResult { Ok = true, Message = "Вид уже связан с шаблоном «" + template.Name + "»." };
            if (!v.IsValidViewTemplate(template.Id))
                return Err("Шаблон «" + template.Name + "» не подходит для вида «" + v.Name + "».");

            using (var t = new Transaction(doc, link ? "JTOOLS: связать вид с шаблоном" : "JTOOLS: применить шаблон к виду"))
            {
                try { t.Start(); } catch (Exception ex) { return Err("Не удалось начать транзакцию: " + ex.Message); }
                try
                {
                    if (link)
                        v.ViewTemplateId = template.Id;
                    else
                        v.ApplyViewTemplateParameters(template);
                }
                catch (Exception ex)
                {
                    try { t.RollBack(); } catch { }
                    return Err((link ? "Не удалось связать шаблон: " : "Не удалось применить шаблон: ") + ex.Message);
                }
                try { t.Commit(); }
                catch (Exception ex)
                {
                    try { t.RollBack(); } catch { }
                    return Err("Ошибка сохранения: " + ex.Message);
                }
            }

            GrdLog.Log("ViewsManager." + (link ? "Связан" : "Применён") + " шаблон «" + template.Name + "» к «" + v.Name + "»");
            return new ViewOpResult
            {
                Ok = true,
                Message = (link ? "Связан с шаблоном: " : "Применён шаблон: ") + template.Name
            };
        }

        private static BatchOpResult AddPrefix(UIApplication app, List<long> ids, string prefix)
        {
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null) return new BatchOpResult { Ok = false, Error = "Нет активного документа." };
            prefix = (prefix ?? string.Empty).Trim();
            if (prefix.Length == 0) return new BatchOpResult { Ok = false, Error = "Префикс не может быть пустым." };

            var views = new List<View>();
            foreach (var id in ids)
            {
                var v = doc.GetElement(new ElementId(id)) as View;
                if (v != null) views.Add(v);
            }
            if (views.Count == 0) return new BatchOpResult { Ok = false, Error = "Нет отмеченных видов." };

            var existing = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(View)).Select(x => x.Name),
                StringComparer.OrdinalIgnoreCase);

            var targets = new Dictionary<long, string>();
            var skippedNames = new List<string>();
            foreach (var v in views)
            {
                if (v.IsTemplate) continue;
                string newName = prefix + v.Name;
                if (string.Equals(newName, v.Name, StringComparison.Ordinal)) continue;
                if (existing.Contains(newName))
                {
                    skippedNames.Add(v.Name);
                    continue;
                }
                targets[v.Id.Value] = newName;
                existing.Add(newName);
            }

            if (targets.Count == 0)
                return new BatchOpResult
                {
                    Ok = true,
                    Applied = 0,
                    Skipped = skippedNames.Count,
                    Message = skippedNames.Count > 0
                        ? "Все имена уже заняты: " + string.Join(", ", skippedNames)
                        : "Переименовывать нечего."
                };

            using (var t = new Transaction(doc, "JTOOLS: добавить префикс именам видов"))
            {
                try { t.Start(); } catch (Exception ex) { return new BatchOpResult { Ok = false, Error = "Не удалось начать транзакцию: " + ex.Message }; }
                try
                {
                    foreach (var kv in targets)
                    {
                        var v = doc.GetElement(new ElementId(kv.Key)) as View;
                        if (v != null) v.Name = kv.Value;
                    }
                }
                catch (Exception ex)
                {
                    try { t.RollBack(); } catch { }
                    return new BatchOpResult { Ok = false, Error = "Не удалось переименовать: " + ex.Message };
                }
                try { t.Commit(); }
                catch (Exception ex)
                {
                    try { t.RollBack(); } catch { }
                    return new BatchOpResult { Ok = false, Error = "Ошибка сохранения: " + ex.Message };
                }
            }

            GrdLog.Log("ViewsManager.AddPrefix: «" + prefix + "» → " + targets.Count + (skippedNames.Count > 0 ? ", пропущено " + skippedNames.Count : ""));
            return new BatchOpResult
            {
                Ok = true,
                Applied = targets.Count,
                Skipped = skippedNames.Count,
                Message = "Добавлен префикс «" + prefix + "»: " + targets.Count + " вид(а) " +
                          (skippedNames.Count > 0 ? " (пропущено: " + skippedNames.Count + ")" : "")
            };
        }
    }
}