using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GrdRevit.Core;

namespace GrdRevit.Revit
{
    /// <summary>Семейство активного документа (для списка в окне параметров).</summary>
    public sealed class FamilyInfoItem
    {
        public string Name = string.Empty;
        public int TypeCount;
    }

    /// <summary>Результат чтения семейств и определений общих параметров из файла.</summary>
    public sealed class FamilyInfoResult
    {
        public string Error;
        public List<FamilyInfoItem> Families = new List<FamilyInfoItem>();
        public List<SharedParamDef> SharedDefs = new List<SharedParamDef>();
        public string SharedFilePath = "";
        /// <summary>Параметры выбранных семейств (одно — все; несколько — только общие).</summary>
        public List<FamilyParamInfo> Params = new List<FamilyParamInfo>();
        /// <summary>Предупреждения при чтении параметров (напр. «семейство не удалось открыть»).</summary>
        public string ParamsMessage = "";
        /// <summary>Откуда взяты семейства в режиме «только выбранные»: название спецификации
        /// (если считали из неё) либо пусто (обычное выделение элементов).</summary>
        public string Scope = "";
        /// <summary>Id элементов, попавших в область «только выбранные» (экземпляры семейств,
        /// виды и листы). Нужны для создания ведомости по выбранным элементам.</summary>
        public List<long> SelectedElementIds = new List<long>();
        /// <summary>В область «только выбранные» попали виды (ведомость видов либо выделение).</summary>
        public bool HasSelectedViews;
        /// <summary>В область «только выбранные» попали листы (ведомость листов либо выделение).</summary>
        public bool HasSelectedSheets;
    }

    /// <summary>Параметр выбранного семейства (для правой панели окна).</summary>
    public sealed class FamilyParamInfo
    {
        public string Name = string.Empty;
        public bool IsShared;
        public bool IsInstance;
        public string StorageType = string.Empty;
        public string Group = string.Empty;
        /// <summary>GUID общего параметра (пусто для несемейного).</summary>
        public string Guid = string.Empty;
        /// <summary>Параметр есть в самом семействе (в FamilyManager). False — параметр
        /// добавлен на уровне проекта («Управление параметрами проекта» в основном окне
        /// Revit): привязан к категории семейства в этом документе, но не вшит в RFA.</summary>
        public bool IsInFamily = true;
        /// <summary>Значение параметра определяется формулой в семействе
        /// (хотя бы у одного типа).</summary>
        public bool HasFormula;
    }

    /// <summary>
    /// Читает в API-контексте список семейств активного документа и определения
    /// общих параметров из файла (.txt). Окно плагина не может обращаться к API
    /// напрямую — запрос ставится в очередь и выполняется Revit-потоком через
    /// ExternalEvent, результат возвращается в колбэк.
    /// </summary>
    public class FamilyInfoHandler : IExternalEventHandler
    {
        /// <summary>Постоянная псевдозапись левого списка окна «Параметры семейств»: виды.</summary>
        public const string PseudoViewsName = "✦ Виды";
        /// <summary>Постоянная псевдозапись левого списка окна «Параметры семейств»: листы.</summary>
        public const string PseudoSheetsName = "✦ Листы";

        /// <summary>True, если имя — псевдозапись «виды»/«листы» (не семейство).</summary>
        public static bool IsPseudoName(string name)
        {
            return string.Equals(name, PseudoViewsName, StringComparison.Ordinal)
                || string.Equals(name, PseudoSheetsName, StringComparison.Ordinal);
        }

        private readonly object _sync = new object();
        private readonly Queue<Request> _requests = new Queue<Request>();

        private sealed class Request
        {
            public List<string> ReadParamsFor;
            public string SharedPath;
            public bool SelectedOnly;
            public Action<FamilyInfoResult> Callback;
        }

        /// <summary>
        /// Очередь запроса. <paramref name="readParamsFor"/> — имена семейств, чьи параметры
        /// нужно прочитать (null — только список семейств и общих параметров файла).
        /// <paramref name="selectedOnly"/> — показывать только семейства выделенных
        /// в Revit элементов (иначе — все семейства документа).
        /// Запросы не перекрываются: каждый из них будет выполнен при очередном
        /// вызове <see cref="Execute"/>.
        /// </summary>
        public void Queue(List<string> readParamsFor, string sharedPath, Action<FamilyInfoResult> callback,
            bool selectedOnly = false)
        {
            lock (_sync)
            {
                _requests.Enqueue(new Request
                {
                    ReadParamsFor = readParamsFor,
                    SharedPath = sharedPath ?? string.Empty,
                    SelectedOnly = selectedOnly,
                    Callback = callback
                });
            }
        }

        public string GetName()
        {
            return "JTOOLS: список семейств и общих параметров";
        }

        public void Execute(UIApplication app)
        {
            List<Request> batch;
            lock (_sync)
            {
                if (_requests.Count == 0)
                {
                    GrdLog.Log("FamilyInfoHandler.Execute: нет запросов в очереди");
                    return;
                }
                batch = new List<Request>(_requests);
                _requests.Clear();
            }

            foreach (var req in batch)
            {
                try
                {
                    req.Callback(Read(app, req.SharedPath, req.ReadParamsFor, req.SelectedOnly));
                }
                catch (Exception ex)
                {
                    GrdLog.Log("FamilyInfoHandler.Execute: EXCEPTION: " + ex);
                    try { req.Callback(new FamilyInfoResult { Error = "Ошибка чтения: " + ex.Message }); }
                    catch { }
                }
            }
        }

        private static FamilyInfoResult Read(UIApplication app, string requestedPath, List<string> readParamsFor, bool selectedOnly)
        {
            var res = new FamilyInfoResult();
            var uiDoc = app?.ActiveUIDocument;
            if (uiDoc?.Document == null)
            {
                res.Error = "Нет активного документа Revit.";
                return res;
            }
            var doc = uiDoc.Document;

            try
            {
                var docNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Family)))
                {
                    if (e == null || string.IsNullOrEmpty(e.Name)) continue;
                    docNames.Add(e.Name);
                }
                if (selectedOnly)
                {
                    var scope = CollectScope(uiDoc, docNames);
                    res.Scope = scope.ScheduleName ?? string.Empty;
                    res.Families = scope.FamilyNames.Select(n => new FamilyInfoItem { Name = n }).ToList();
                    res.SelectedElementIds = scope.ElementIds;
                    res.HasSelectedViews = scope.HasViews;
                    res.HasSelectedSheets = scope.HasSheets;
                    GrdLog.Log("FamilyInfoHandler: из выделения/спецификации семейств=" + res.Families.Count +
                               ", элементов=" + res.SelectedElementIds.Count +
                               ", видов=" + (scope.HasViews ? "да" : "нет") +
                               ", листов=" + (scope.HasSheets ? "да" : "нет"));
                }
                else
                {
                    res.Families = docNames.Select(n => new FamilyInfoItem { Name = n }).ToList();
                    GrdLog.Log("FamilyInfoHandler: семейств=" + res.Families.Count);
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyInfoHandler: список семейств EXCEPTION: " + ex);
            }

            var path = string.IsNullOrEmpty(requestedPath) ? RevitContext.Settings.LastSharedParamsPath : requestedPath;
            res.SharedFilePath = path ?? string.Empty;
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                try
                {
                    app.Application.SharedParametersFilename = path;
                    var file = app.Application.OpenSharedParameterFile();
                    if (file != null)
                    {
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (DefinitionGroup g in file.Groups)
                        {
                            if (g == null) continue;
                            foreach (Definition d in g.Definitions)
                            {
                                if (d == null || string.IsNullOrEmpty(d.Name)) continue;
                                var key = g.Name + "|" + d.Name;
                                if (!seen.Add(key)) continue;
                                res.SharedDefs.Add(new SharedParamDef
                                {
                                    Name = d.Name,
                                    Group = g.Name,
                                    Guid = (d as ExternalDefinition)?.GUID.ToString() ?? string.Empty,
                                    StorageType = DescribeType(d)
                                });
                            }
                        }
                        GrdLog.Log("FamilyInfoHandler: общих параметров=" + res.SharedDefs.Count + " из " + path);
                    }
                }
                catch (Exception ex)
                {
                    GrdLog.Log("FamilyInfoHandler: общие параметры EXCEPTION: " + ex);
                }
            }

            if (readParamsFor != null && readParamsFor.Count > 0)
            {
                try
                {
                    var msgs = new List<string>();
                    res.Params = ReadFamiliesParams(doc, readParamsFor, msgs);
                    if (msgs.Count > 0) res.ParamsMessage = string.Join("; ", msgs);
                }
                catch (Exception ex)
                {
                    GrdLog.Log("FamilyInfoHandler: параметры семейств EXCEPTION: " + ex);
                }
            }

            return res;
        }

        /// <summary>
        /// Читает параметры выбранных семейств через документ семейства (EditFamily):
        /// это авторитетный список FamilyManager'а (все параметры, точные признак
        /// «тип/экземпляр», GUID общих). Одно семейство — все параметры; несколько —
        /// только общие (shared), присутствующие во всех выбранных.
        /// Проблемные семейства попадают в <paramref name="messages"/>.
        /// </summary>
        private static List<FamilyParamInfo> ReadFamiliesParams(Document doc, List<string> names, List<string> messages)
        {
            var single = names.Count == 1;
            var perFamily = new Dictionary<string, List<FamilyParamInfo>>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in names)
            {
                // Псевдозаписи «Виды»/«Листы»: параметры читаются с представительного
                // вида/листа (семейства как такового у них нет).
                if (IsPseudoName(name))
                {
                    perFamily[name] = ReadPseudoParams(doc, name, messages);
                    continue;
                }

                var fam = FindFamily(doc, name);
                if (fam == null)
                {
                    messages.Add("«" + name + "» не найдено в документе");
                    continue;
                }

                Document fdoc = null;
                try
                {
                    fdoc = doc.EditFamily(fam);
                }
                catch (Exception ex)
                {
                    GrdLog.Log("FamilyInfoHandler: EditFamily «" + name + "» EXCEPTION: " + ex);
                    messages.Add("«" + name + "»: не удалось открыть (" + ex.Message + ")");
                    continue;
                }

                try
                {
                    var list = new List<FamilyParamInfo>();
                    var fm = fdoc.FamilyManager;
                    if (fm != null)
                    {
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (FamilyParameter fp in fm.GetParameters())
                        {
                            if (fp?.Definition == null) continue;
                            if (!seen.Add(fp.Definition.Name)) continue;
                            list.Add(new FamilyParamInfo
                            {
                                Name = fp.Definition.Name,
                                IsShared = fp.IsShared,
                                IsInstance = fp.IsInstance,
                                StorageType = DescribeType(fp.Definition),
                                Group = GroupName(fp.Definition),
                                Guid = fp.IsShared ? fp.GUID.ToString() : string.Empty,
                                HasFormula = HasFormulaIn(fm, fp)
                            });
                        }
                    }
                    else
                    {
                        messages.Add("«" + name + "»: у семейства нет диспетчера параметров");
                    }
                    perFamily[name] = list;
                    // Дополняем «проектными» параметрами (добавленными через главное окно Revit,
                    // «Управление параметрами проекта»): они привязаны к категории семейства в этом
                    // документе, но не вшиты в RFA, и FamilyManager их не показывает.
                    AddProjectParams(doc, fam, list);
                    GrdLog.Log("FamilyInfoHandler: «" + name + "» параметров=" + list.Count + ": " +
                               JoinNames(list));
                }
                catch (Exception ex)
                {
                    GrdLog.Log("FamilyInfoHandler: параметры «" + name + "» EXCEPTION: " + ex);
                    messages.Add("«" + name + "»: " + ex.Message);
                    perFamily[name] = new List<FamilyParamInfo>();
                }
                finally
                {
                    try { fdoc.Dispose(); } catch { }
                }
            }

            if (perFamily.Count == 0) return new List<FamilyParamInfo>();

            if (single)
            {
                return perFamily.Values.FirstOrDefault() ?? new List<FamilyParamInfo>();
            }

            var lists = perFamily.Values.ToList();
            var result = new List<FamilyParamInfo>();
            // «Общий для всех» = параметр с таким именем есть в КАЖДОМ отмеченном
            // семействе — и общий (shared), и обычный семейный. Раньше учитывались
            // только shared-параметры, из-за чего добавленный «обычный» параметр
            // (или общий, применённый не ко всем семействам) не показывался в списке.
            foreach (var candidate in lists[0])
            {
                var inAll = true;
                foreach (var other in lists.Skip(1))
                {
                    if (!other.Any(p =>
                        string.Equals(p.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        inAll = false;
                        break;
                    }
                }
                if (inAll)
                {
                    // Формула может быть не у всех семейств — подсвечиваем, если есть
                    // хотя бы у одного отмеченного.
                    foreach (var other in lists.Skip(1))
                    {
                        var match = other.FirstOrDefault(p =>
                            string.Equals(p.Name, candidate.Name, StringComparison.OrdinalIgnoreCase));
                        if (match != null && match.HasFormula)
                        {
                            candidate.HasFormula = true;
                            break;
                        }
                    }
                    result.Add(candidate);
                }
            }
            GrdLog.Log("FamilyInfoHandler: общих для всех параметров=" + result.Count + ": " + JoinNames(result));
            return result;
        }

        /// <summary>Имена параметров через «; » для журнала (не более <paramref name="max"/>).</summary>
        private static string JoinNames(IEnumerable<FamilyParamInfo> list, int max = 25)
        {
            var names = list.Select(p => p.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
            if (names.Count > max) return string.Join("; ", names.Take(max)) + "; …(" + names.Count + ")";
            return string.Join("; ", names);
        }

        /// <summary>Есть ли у параметра формула (значение определяется формулой) хотя бы
        /// у одного типа семейства. Формула — атрибут типа, поэтому проверяем по всем
        /// типам через FamilyManager.CurrentType (как при снятии формулы).</summary>
        private static bool HasFormulaIn(FamilyManager fm, FamilyParameter fp)
        {
            try
            {
                if (fm == null || fp == null) return false;
                var types = fm.Types;
                if (types == null) return fp.IsDeterminedByFormula;
                bool any = false;
                foreach (FamilyType type in types)
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

        /// <summary>
        /// Дополняет список параметров семейства «проектными» (добавленными через
        /// «Управление параметрами проекта» в основном окне Revit): это общие (shared)
        /// параметры, привязанные здесь к категории семейства, но не вшитые в само RFA
        /// (поэтому их нет в FamilyManager). Отмечаются IsInFamily = false.
        /// <para>Источники берутся два независимых: (1) параметры, реально видимые на
        /// символах/экземплярах семейства в проекте (Parameter.GUID — самый достоверный,
        /// показывает то, что видит пользователь в свойствах); (2) привязки к категории
        /// семейства из ParameterBindings — так не теряются параметры семейств без
        /// размещённых экземпляров.</para>
        /// </summary>
        private static void AddProjectParams(Document doc, Family family, List<FamilyParamInfo> push)
        {
            try
            {
                var famCat = family.Category;
                var already = new HashSet<string>(push.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

                var merged = new Dictionary<string, FamilyParamInfo>(StringComparer.OrdinalIgnoreCase);

                // 1) Элементы семейства в проекте (экземпляры — параметры экземпляра,
                //    символы — параметры типа). Это ровно то, что видит пользователь.
                CollectElementProjectParams(doc, family, merged);

                // 2) Привязки к категории (покрывает семейства без размещённых экземпляров).
                CollectBindingParams(doc, famCat, merged);

                foreach (var item in merged.Values)
                {
                    if (already.Contains(item.Name)) continue;
                    push.Add(item);
                    already.Add(item.Name);
                }
                GrdLog.Log("FamilyInfoHandler: проектных (не в семействе) добавлено=" +
                           merged.Count + " для «" + (family?.Name ?? "?") + "»: " +
                           JoinNames(merged.Values));
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyInfoHandler: проектные параметры «" + (family?.Name ?? "?") + "» EXCEPTION: " + ex);
            }
        }

        /// <summary>Общие параметры, реально присутствующие на элементах семейства в
        /// проекте (те, что «вшиты планировщиком параметров проекта»): у экземпляров —
        /// параметры экземпляра, у символов — параметры типа. Ключ — имя параметра.</summary>
        private static void CollectElementProjectParams(Document doc, Family family, Dictionary<string, FamilyParamInfo> map)
        {
            if (family == null) return;
            try
            {
                void Add(FamilyParamInfo info)
                {
                    if (string.IsNullOrEmpty(info.Name)) return;
                    if (!map.ContainsKey(info.Name)) map[info.Name] = info;
                }

                // 1) Параметры типа — с символов (они точны по IsInstance=false).
                var ids = family.GetFamilySymbolIds();
                if (ids != null)
                {
                    foreach (var sid in ids)
                    {
                        var sym = doc.GetElement(sid) as FamilySymbol;
                        if (sym?.Parameters == null) continue;
                        foreach (Parameter p in sym.Parameters)
                        {
                            var info = ProjectParamFrom(p);
                            if (info != null) Add(info);
                        }
                    }
                }

                // 2) Параметры экземпляра: тип-параметры в список символов уже вошли,
                // поэтому всё, что на экземплярах иначе — параметры экземпляра.
                int scanned = 0;
                foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
                {
                    try
                    {
                        if (fi.Symbol?.Family == null || fi.Symbol.Family.Id != family.Id) continue;
                        if (scanned++ >= 200) break;
                        foreach (Parameter p in fi.Parameters)
                        {
                            var info = ProjectParamFrom(p);
                            if (info == null || map.ContainsKey(info.Name)) continue;
                            info.IsInstance = true;
                            map[info.Name] = info;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyInfoHandler: CollectElementProjectParams EXCEPTION: " + ex);
            }
        }

        /// <summary>FamilyParamInfo из параметра элемента, ЕСЛИ это общий параметр
        /// проекта (не вшитый в семейство): Parameter.GUID != Guid.Empty означают общий
        /// параметр; семейных (не shared) он Guid.Empty. Возвращает null для встроенных
        /// и обычных семейных параметров.</summary>
        private static FamilyParamInfo ProjectParamFrom(Parameter p)
        {
            try
            {
                if (p?.Definition == null) return null;
                if (p.Definition is InternalDefinition kbi && kbi.BuiltInParameter != BuiltInParameter.INVALID) return null;
                var name = p.Definition.Name;
                if (string.IsNullOrEmpty(name)) return null;
                var g = p.GUID;
                if (g == Guid.Empty) return null;
                return new FamilyParamInfo
                {
                    Name = name,
                    IsShared = true,
                    IsInstance = false,
                    StorageType = DescribeType(p.Definition),
                    Group = GroupName(p.Definition),
                    Guid = g.ToString(),
                    IsInFamily = false
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Общие параметры из привязок к категории семейства. Извлечение
        /// определения не зависит от конкретного типа элементов BindingMap
        /// (KeyValuePair / DictionaryEntry / иная обёртка): при неудаче используем
        /// индексер ParameterBindings[definition].</summary>
        private static void CollectBindingParams(Document doc, Category famCat, Dictionary<string, FamilyParamInfo> map)
        {
            if (famCat == null || doc == null) return;
            try
            {
                foreach (var entry in doc.ParameterBindings)
                {
                    Definition def = TryGetDefinition(entry, out Binding embedded);
                    if (def == null) continue;
                    if (def is InternalDefinition kitem && kitem.BuiltInParameter != BuiltInParameter.INVALID) continue;
                    var name = def.Name;
                    if (string.IsNullOrEmpty(name) || map.ContainsKey(name)) continue;

                    var eb = embedded as ElementBinding;
                    if (eb == null)
                    {
                        try
                        {
                            eb = BindingMapItem<ElementBinding>(doc, def);
                        }
                        catch { }
                    }
                    if (eb == null) continue;

                    var cats = eb.Categories;
                    if (cats == null || cats.IsEmpty) continue;
                    var bound = false;
                    foreach (Category c in cats)
                    {
                        if (c == null) continue;
                        if (c.Id == famCat.Id || string.Equals(c.Name, famCat.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            bound = true;
                            break;
                        }
                    }
                    if (!bound) continue;

                    Guid guid = Guid.Empty;
                    if (def is ExternalDefinition ext) guid = ext.GUID;
                    map[name] = new FamilyParamInfo
                    {
                        Name = name,
                        IsShared = guid != Guid.Empty,
                        IsInstance = eb is InstanceBinding,
                        StorageType = DescribeType(def),
                        Group = GroupName(def),
                        Guid = guid != Guid.Empty ? guid.ToString() : string.Empty,
                        IsInFamily = false
                    };
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyInfoHandler: CollectBindingParams EXCEPTION: " + ex);
            }
        }

        /// <summary>Binding по Definition: BindingMap.get_Item(Definition) в метаданных —
        /// обычный метод, не C#-индексатор; вызываем через рефлексию.</summary>
        private static T BindingMapItem<T>(Document doc, Definition def) where T : class
        {
            var map = doc?.ParameterBindings;
            if (map == null || def == null) return null;
            var meth = map.GetType().GetMethod("get_Item");
            if (meth == null) return null;
            return meth.Invoke(map, new object[] { def }) as T;
        }

        /// <summary>Вытаскивает Definition (и, если повезёт, Binding) из элемента
        /// перечисления BindingMap: сначала типовые паттерны, затем рефлексия по имени
        /// свойства (Definition/Key + Value/Binding).</summary>
        private static Definition TryGetDefinition(object entry, out Binding binding)
        {
            binding = null;
            if (entry == null) return null;
            if (entry is KeyValuePair<Definition, Binding> kp)
            {
                binding = kp.Value;
                return kp.Key;
            }
            if (entry is System.Collections.DictionaryEntry de)
            {
                binding = de.Value as Binding;
                return de.Key as Definition;
            }
            try
            {
                var t = entry.GetType();
                var defProp = t.GetProperty("Definition") ?? t.GetProperty("Key");
                if (defProp != null)
                {
                    var d = defProp.GetValue(entry, null) as Definition;
                    if (d != null)
                    {
                        var valProp = t.GetProperty("Value") ?? t.GetProperty("Binding");
                        if (valProp != null) binding = valProp.GetValue(entry, null) as Binding;
                    }
                    return d;
                }
                GrdLog.Log("FamilyInfoHandler.TryGetDefinition: неизвестный тип элемента: " + t.FullName);
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyInfoHandler.TryGetDefinition EXCEPTION: " + ex);
            }
            return null;
        }

        /// <summary>Параметры псевдозаписи «Виды»/«Листы»: с представительного вида/листа.</summary>
        private static List<FamilyParamInfo> ReadPseudoParams(Document doc, string name, List<string> messages)
        {
            var isViews = string.Equals(name, PseudoViewsName, StringComparison.Ordinal);
            var el = isViews
                ? (Element)FindRepresentativeView(doc)
                : (Element)FindRepresentativeSheet(doc);
            if (el == null)
            {
                messages.Add("В документе нет " + (isViews ? "видов" : "листов") + " — параметры недоступны");
                return new List<FamilyParamInfo>();
            }
            return ReadElementParams(el);
        }

        /// <summary>Представительный (первый нешаблонный) вид документа для чтения параметров.</summary>
        private static View FindRepresentativeView(Document doc)
        {
            try
            {
                foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)))
                {
                    if (v == null || v is ViewSchedule || v is View3D) continue;
                    try { if (v.IsTemplate) continue; } catch { }
                    return v;
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyInfoHandler.FindRepresentativeView EXCEPTION: " + ex);
            }
            return null;
        }

        /// <summary>Представительный (первый) лист документа для чтения параметров.</summary>
        private static ViewSheet FindRepresentativeSheet(Document doc)
        {
            try
            {
                foreach (ViewSheet s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))
                    return s;
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyInfoHandler.FindRepresentativeSheet EXCEPTION: " + ex);
            }
            return null;
        }

        /// <summary>Все параметры элемента (для видов/листов).</summary>
        private static List<FamilyParamInfo> ReadElementParams(Element el)
        {
            var list = new List<FamilyParamInfo>();
            if (el?.Parameters == null) return list;
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Parameter p in el.Parameters)
                {
                    if (p?.Definition == null || string.IsNullOrEmpty(p.Definition.Name)) continue;
                    if (!seen.Add(p.Definition.Name)) continue;
                    var guid = string.Empty;
                    var isShared = false;
                    try
                    {
                        isShared = p.IsShared;
                        if (isShared)
                        {
                            var g = p.GUID;
                            if (g != Guid.Empty) guid = g.ToString();
                        }
                    }
                    catch { }
                    list.Add(new FamilyParamInfo
                    {
                        Name = p.Definition.Name,
                        IsShared = isShared,
                        IsInstance = true,
                        StorageType = DescribeType(p.Definition),
                        Group = GroupName(p.Definition),
                        Guid = guid,
                        IsInFamily = true,
                        HasFormula = false
                    });
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyInfoHandler.ReadElementParams EXCEPTION: " + ex);
            }
            return list;
        }

        private static Family FindFamily(Document doc, string name)
        {
            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Family)))
            {
                var f = e as Family;
                if (f != null && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
            }
            return null;
        }

        /// <summary>Область «только выбранные»: семейства (по именам), id элементов
        /// (экземпляры семейств, виды, листы) и флаги наличия видов/листов.</summary>
        private sealed class ScopeInfo
        {
            public string ScheduleName = string.Empty;
            public List<string> FamilyNames = new List<string>();
            public List<long> ElementIds = new List<long>();
            public bool HasViews;
            public bool HasSheets;
        }

        /// <summary>
        /// Определяет область «только выбранные»: если активный вид — спецификация
        /// (перечень элементов либо ведомость видов/листов), берутся её экземпляры
        /// (ид возвращаются целиком); иначе — выделенные в Revit элементы (экземпляры,
        /// семейства — расширяются до их экземпляров, символы — до экземпляров типа,
        /// виды и листы — как есть). Название спецификации возвращается в
        /// <see cref="ScopeInfo.ScheduleName"/>. Семейства результатов ограничены
        /// присутствующими в документе (<paramref name="docNames"/>).
        /// </summary>
        private static ScopeInfo CollectScope(UIDocument uiDoc, ISet<string> docNames)
        {
            var scope = new ScopeInfo();
            if (uiDoc == null) return scope;

            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<long>();

            void AddElement(Document doc, Element el)
            {
                if (el == null) return;
                try
                {
                    if (el is FamilyInstance fi)
                    {
                        var famName = fi.Symbol?.Family?.Name;
                        if (!string.IsNullOrEmpty(famName))
                        {
                            if (docNames.Contains(famName)) names.Add(famName);
                            ids.Add(el.Id.Value);
                        }
                        return;
                    }
                    if (el is Family fam)
                    {
                        if (!string.IsNullOrEmpty(fam.Name) && docNames.Contains(fam.Name)) names.Add(fam.Name);
                        foreach (var instId in FamilyInstanceIds(doc, fam)) ids.Add(instId);
                        return;
                    }
                    if (el is FamilySymbol sym)
                    {
                        if (sym.Family != null && !string.IsNullOrEmpty(sym.Family.Name) &&
                            docNames.Contains(sym.Family.Name)) names.Add(sym.Family.Name);
                        foreach (var instId in FamilyInstanceIds(doc, sym.Id)) ids.Add(instId);
                        return;
                    }
                    if (el is View)
                    {
                        ids.Add(el.Id.Value);
                        scope.HasViews = true;
                        return;
                    }
                    if (el is ViewSheet)
                    {
                        ids.Add(el.Id.Value);
                        scope.HasSheets = true;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    GrdLog.Log("FamilyInfoHandler.CollectScope.AddElement EXCEPTION: " + ex);
                }
            }

            try
            {
                var doc = uiDoc.Document;
                if (uiDoc.ActiveView is ViewSchedule schedule)
                {
                    var sd = schedule.Definition as ScheduleDefinition;
                    // Работаем с ведомостями элементов, а также со списками видов и листов.
                    if (sd != null && !sd.IsKeySchedule && !sd.IsMaterialTakeoff)
                    {
                        schedule.RefreshData();
                        var instances = schedule.GetScheduleInstances(-1);
                        if (instances != null && instances.Count > 0)
                        {
                            scope.ScheduleName = schedule.Name;
                            foreach (var id in instances)
                            {
                                Element el = null;
                                try { el = doc.GetElement(id); } catch { }
                                if (el == null) continue;
                                AddElement(doc, el);
                            }
                            GrdLog.Log("FamilyInfoHandler: спецификация «" + schedule.Name + "», семейств=" +
                                       names.Count + ", элементов=" + ids.Count);
                            scope.FamilyNames = names.ToList();
                            scope.ElementIds = ids.ToList();
                            return scope;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyInfoHandler: спецификация EXCEPTION: " + ex);
            }

            try
            {
                foreach (var id in uiDoc.Selection.GetElementIds())
                {
                    var el = uiDoc.Document.GetElement(id);
                    AddElement(uiDoc.Document, el);
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyInfoHandler: выделение EXCEPTION: " + ex);
            }

            scope.FamilyNames = names.ToList();
            scope.ElementIds = ids.ToList();
            return scope;
        }

        /// <summary>Id всех экземпляров семейства <paramref name="fam"/> в документе.</summary>
        private static IEnumerable<long> FamilyInstanceIds(Document doc, Family fam)
        {
            try
            {
                var symIds = new HashSet<ElementId>(fam.GetFamilySymbolIds());
                return new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(fi => fi?.Symbol != null && symIds.Contains(fi.Symbol.Id))
                    .Select(fi => fi.Id.Value);
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyInfoHandler.FamilyInstanceIds(fam) EXCEPTION: " + ex);
                return Enumerable.Empty<long>();
            }
        }

        /// <summary>Id всех экземпляров указанного символа (типа) семейства в документе.</summary>
        private static IEnumerable<long> FamilyInstanceIds(Document doc, ElementId symbolId)
        {
            try
            {
                return new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(fi => fi?.Symbol != null && fi.Symbol.Id == symbolId)
                    .Select(fi => fi.Id.Value);
            }
            catch (Exception ex)
            {
                GrdLog.Log("FamilyInfoHandler.FamilyInstanceIds(symbol) EXCEPTION: " + ex);
                return Enumerable.Empty<long>();
            }
        }

        /// <summary>Человекопонятное имя группы параметра по ForgeTypeId.</summary>
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
    }
}