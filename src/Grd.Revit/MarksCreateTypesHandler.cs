using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace GrdRevit.Revit
{
    /// <summary>Запрос на создание недостающего типа в семействе механического оборудования.</summary>
    public sealed class MarksCreateTypeRequest
    {
        public string FamilyName = string.Empty;
        public string TypeName = string.Empty;
    }

    /// <summary>Результат создания одного типа: создан / уже существует / ошибка.</summary>
    public sealed class MarksCreateTypeItem
    {
        public string FamilyName = string.Empty;
        public string TypeName = string.Empty;
        public bool Created;
        public bool AlreadyExists;
        public string Error = string.Empty;
    }

    /// <summary>Итог пакетного создания типов.</summary>
    public sealed class MarksCreateTypesResult
    {
        public bool Ok;
        public string Error;
        public List<MarksCreateTypeItem> Items = new List<MarksCreateTypeItem>();
    }

    /// <summary>
    /// Создаёт в API-контексте недостающие типы в семействах механического оборудования:
    /// кнопка «Создать недостающие типы» окна «Марки DXF». Для каждого запроса ищет в
    /// семействе тип с заданным именем; если его нет — дублирует первый существующий тип
    /// семейства (ElementType.Duplicate) в транзакции, присваивая новому типу имя марки.
    /// </summary>
    public class MarksCreateTypesHandler : IExternalEventHandler
    {
        /// <summary>Префикс автоматически созданных типов (без «:» и прочих запрещённых символов).</summary>
        public const string AutoTypePrefix = "Авто_";
        private readonly object _sync = new object();
        private List<MarksCreateTypeRequest> _requests;
        private Action<MarksCreateTypesResult> _callback;

        public void Queue(List<MarksCreateTypeRequest> requests, Action<MarksCreateTypesResult> callback)
        {
            lock (_sync)
            {
                _requests = requests ?? new List<MarksCreateTypeRequest>();
                _callback = callback;
            }
        }

        public string GetName()
        {
            return "JTOOLS: создать нехватающие типы марок DXF";
        }

        public void Execute(UIApplication app)
        {
            List<MarksCreateTypeRequest> reqs;
            Action<MarksCreateTypesResult> cb;
            lock (_sync)
            {
                reqs = _requests;
                _requests = null;
                cb = _callback;
                _callback = null;
            }
            if (reqs == null || cb == null)
            {
                GrdLog.Log("MarksCreateTypesHandler.Execute: нет запросов в очереди");
                return;
            }

            try
            {
                cb(Create(app, reqs));
            }
            catch (Exception ex)
            {
                GrdLog.Log("MarksCreateTypesHandler.Execute: EXCEPTION: " + ex);
                try
                {
                    cb(new MarksCreateTypesResult { Ok = false, Error = "Ошибка создания типов: " + ex.Message });
                }
                catch { }
            }
        }

        /// <summary>
        /// Преобразует произвольную марку DXF в допустимое имя типа Revit: оставляет только
        /// «безопасные» символы — буквы/цифры (включая кириллицу), пробелы и пунктуацию
        /// используется в именах типов. Точка с запятой (разделитель параметров марки)
        /// заменяется на запятую, всё остальное отбрасывается, лишние пробелы схлопываются.
        /// Возвращает пустую строку, если после очистки ничего не осталось.
        /// </summary>
        public static string SanitizeTypeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var sb = new StringBuilder(name.Length);
            bool needSpace = false;
            foreach (char c in name)
            {
                if (c == ';')
                {
                    sb.Append(", ");
                    needSpace = false;
                    continue;
                }
                if (char.IsLetterOrDigit(c))
                {
                    if (needSpace && sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                    needSpace = false;
                    sb.Append(c);
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    needSpace = true;
                    continue;
                }
                if ("-_.,/()x".IndexOf(c) >= 0)
                {
                    if (needSpace && sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                    needSpace = false;
                    sb.Append(c);
                    continue;
                }
                needSpace = true;
            }
            return sb.ToString().Trim();
        }

        /// <summary>Полное имя автоматически созданного типа: префикс + очищенная марка.</summary>
        public static string AutoTypeName(string mark)
        {
            string clean = SanitizeTypeName(mark);
            return string.IsNullOrEmpty(clean) ? string.Empty : AutoTypePrefix + clean;
        }

        private static MarksCreateTypesResult Create(UIApplication app, List<MarksCreateTypeRequest> requests)
        {
            var uiDoc = app?.ActiveUIDocument;
            if (uiDoc == null) return Fail("Нет активного документа Revit.");
            var doc = uiDoc.Document;

            var result = new MarksCreateTypesResult { Ok = true };
            var mechCat = Category.GetCategory(doc, BuiltInCategory.OST_MechanicalEquipment);
            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Family != null &&
                            (mechCat == null || s.Category == null || s.Category.Id == mechCat.Id))
                .ToList();

            using (var tx = new Transaction(doc, "Создать типы марок DXF"))
            {
                tx.Start();
                try
                {
                    foreach (var req in requests)
                    {
                        var item = new MarksCreateTypeItem
                        {
                            FamilyName = req.FamilyName,
                            TypeName = req.TypeName
                        };

                        if (string.IsNullOrWhiteSpace(req.FamilyName) || string.IsNullOrWhiteSpace(req.TypeName))
                        {
                            item.Error = "Не задано семейство или имя типа";
                            result.Items.Add(item);
                            continue;
                        }

                        var familySymbols = symbols
                            .Where(s => string.Equals(s.Family.Name, req.FamilyName, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        if (familySymbols.Count == 0)
                        {
                            item.Error = "Семейство «" + req.FamilyName + "» не найдено среди механического оборудования";
                            result.Items.Add(item);
                            continue;
                        }

                        var exists = familySymbols.FirstOrDefault(s =>
                            string.Equals(s.Name, req.TypeName, StringComparison.OrdinalIgnoreCase));
                        if (exists != null)
                        {
                            item.AlreadyExists = true;
                            result.Items.Add(item);
                            continue;
                        }

                        // Дублируем первый существующий тип семейства и переименовываем в тип марки.
                        try
                        {
                            var baseType = familySymbols
                                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                                .First();
                            var newType = baseType.Duplicate(req.TypeName);
                            if (newType != null && string.Equals(newType.Name, req.TypeName, StringComparison.OrdinalIgnoreCase))
                            {
                                item.Created = true;
                            }
                            else
                            {
                                item.Error = "Не удалось получить созданный тип";
                            }
                        }
                        catch (Exception ex)
                        {
                            item.Error = ex.Message;
                        }

                        result.Items.Add(item);
                    }

                    tx.Commit();
                }
                catch (Exception ex)
                {
                    try { tx.RollBack(); }
                    catch { }
                    GrdLog.Log("MarksCreateTypesHandler.Create: EXCEPTION: " + ex);
                    return Fail("Ошибка транзакции: " + ex.Message);
                }
            }

            GrdLog.Log("MarksCreateTypesHandler: запросов=" + requests.Count +
                       ", создано=" + result.Items.Count(i => i.Created) +
                       ", есть=" + result.Items.Count(i => i.AlreadyExists) +
                       ", ошибок=" + result.Items.Count(i => !string.IsNullOrEmpty(i.Error)));
            return result;
        }

        private static MarksCreateTypesResult Fail(string message)
        {
            GrdLog.Log("MarksCreateTypesHandler: " + message);
            return new MarksCreateTypesResult { Ok = false, Error = message };
        }
    }
}