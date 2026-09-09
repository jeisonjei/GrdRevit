using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GrdRevit.Core;

namespace GrdRevit.Revit
{
    /// <summary>
    /// Результат чтения типовых параметров (тип-параметров) механического оборудования:
    /// список их имён и — если запрошен конкретный параметр — значения этого параметра
    /// для каждого имени типа из переданного набора (по имени типа/family в документе).
    /// Параллельно собирает имена параметров экземпляра для выпадающих списков настроек.
    /// </summary>
    public sealed class TypeParamsResult
    {
        public string Error;
        public List<string> ParameterNames = new List<string>();
        public List<string> InstanceParameterNames = new List<string>();
        public Dictionary<string, string> TypeNameToValue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Выполняет в API-контексте сбор имён типовых параметров семейств механического
    /// оборудования и чтение значений выбранного параметра по именам типов.
    /// Окно плагина не может обращаться к документу напрямую — запрос ставится
    /// в очередь и выполняется Revit-потоком через ExternalEvent.
    /// </summary>
    public class TypeParamsHandler : IExternalEventHandler
    {
        private readonly object _sync = new object();
        private List<string> _typeNames;
        private string _paramName;
        private Action<TypeParamsResult> _callback;

        /// <summary>
        /// Ставит запрос в очередь. При paramName=null/пуст — только список имён
        /// параметров; иначе дополнительно значения параметра для каждого имени типа.
        /// </summary>
        public void Queue(List<string> typeNames, string paramName, Action<TypeParamsResult> callback)
        {
            lock (_sync)
            {
                _typeNames = typeNames ?? new List<string>();
                _paramName = paramName ?? string.Empty;
                _callback = callback;
            }
        }

        public string GetName()
        {
            return "Audytor: типовые параметры механического оборудования";
        }

        public void Execute(UIApplication app)
        {
            List<string> typeNames;
            string paramName;
            Action<TypeParamsResult> cb;
            lock (_sync)
            {
                typeNames = _typeNames;
                paramName = _paramName;
                cb = _callback;
                _typeNames = null;
                _paramName = null;
                _callback = null;
            }

            if (cb == null)
            {
                GrdLog.Log("TypeParamsHandler.Execute: нет запроса в очереди");
                return;
            }

            var result = new TypeParamsResult();
            try
            {
                var uiDoc = app?.ActiveUIDocument;
                if (uiDoc == null)
                {
                    result.Error = "Нет активного документа Revit.";
                    cb(result);
                    return;
                }
                var doc = uiDoc.Document;
                if (doc == null)
                {
                    result.Error = "Документ недоступен.";
                    cb(result);
                    return;
                }

                result.ParameterNames = CollectTypeParamNames(doc);
                result.InstanceParameterNames = CollectInstanceParamNames(doc);

                if (!string.IsNullOrEmpty(paramName))
                    result.TypeNameToValue = CollectParamValues(doc, typeNames, paramName);
            }
            catch (Exception ex)
            {
                GrdLog.Log("TypeParamsHandler.Execute: EXCEPTION: " + ex);
                result.Error = "Ошибка чтения типовых параметров: " + ex.Message;
            }

            try { cb(result); }
            catch (Exception ex)
            {
                GrdLog.Log("TypeParamsHandler.Execute: callback EXCEPTION: " + ex);
            }
        }

        /// <summary>Все имена типовых параметров всех семейств механического оборудования (упорядоченные, без дублей).</summary>
        private static List<string> CollectTypeParamNames(Document doc)
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sym in Symbols(doc))
            {
                try
                {
                    foreach (var o in sym.Parameters)
                    {
                        var p = o as Parameter;
                        if (p == null || p.Definition == null) continue;
                        var n = p.Definition.Name;
                        if (!string.IsNullOrEmpty(n)) names.Add(n);
                    }
                }
                catch { /* один тип нельзя читать — пропускаем */ }
            }
            return names.ToList();
        }

        /// <summary>
        /// Имена параметров экземпляра механического оборудования: параметры, которые
        /// встречаются на экземплярах, но НЕ являются типовыми (нет на символах/типах).
        /// Используются в выпадающих списках настроек («Фhl»/«Valve setting» -> параметр).
        /// </summary>
        private static List<string> CollectInstanceParamNames(Document doc)
        {
            var typeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sym in Symbols(doc))
            {
                try
                {
                    foreach (var o in sym.Parameters)
                    {
                        var p = o as Parameter;
                        var n = p?.Definition?.Name;
                        if (!string.IsNullOrEmpty(n)) typeNames.Add(n);
                    }
                }
                catch { /* пропускаем */ }
            }

            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var catMep = new ElementId((int)BuiltInCategory.OST_MechanicalEquipment);
            try
            {
                var instances = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(fi => fi.IsValidObject && fi.Symbol != null && fi.Symbol.Family != null)
                    .ToList();

                foreach (var fi in instances)
                {
                    try
                    {
                        if ((fi.Category?.Id ?? ElementId.InvalidElementId) != catMep) continue;
                        foreach (var o in fi.Parameters)
                        {
                            var p = o as Parameter;
                            var n = p?.Definition?.Name;
                            if (string.IsNullOrEmpty(n)) continue;
                            if (typeNames.Contains(n)) continue; // это типовой параметр
                            names.Add(n);
                        }
                    }
                    catch { /* один экземпляр нельзя читать — пропускаем */ }
                }
            }
            catch { /* нет экземпляров в документе */ }

            return names.ToList();
        }

        /// <summary>
        /// Значение параметра для каждого имени типа: строка вида «Имя типа Revit»
        /// -> значение параметра этого типа. Имя ищется по FamilySymbol.Name или
        /// по имени семейства.
        /// </summary>
        private static Dictionary<string, string> CollectParamValues(Document doc, List<string> typeNames, string paramName)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (typeNames == null || typeNames.Count == 0) return result;

            var symbols = Symbols(doc).ToList();
            foreach (var lookup in typeNames)
            {
                var key = lookup?.Trim();
                if (string.IsNullOrEmpty(key)) continue;

                var sym = symbols.FirstOrDefault(s =>
                    string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Family?.Name, key, StringComparison.OrdinalIgnoreCase));
                if (sym == null) continue; // типа нет в документе — строку не трогаем

                var value = ReadParamString(sym, paramName);
                if (!string.IsNullOrEmpty(value)) result[key] = value;
            }

            GrdLog.Log("TypeParamsHandler: параметр='" + paramName + "' запрошено типов=" + typeNames.Count +
                       ", значений=" + result.Count);
            return result;
        }

        private static string ReadParamString(FamilySymbol sym, string paramName)
        {
            try
            {
                var p = sym.LookupParameter(paramName);
                if (p == null) return null;
                switch (p.StorageType)
                {
                    case StorageType.String:
                        return p.AsString();
                    case StorageType.Integer:
                        return p.AsInteger().ToString(CultureInfo.InvariantCulture);
                    case StorageType.Double:
                        try { return p.AsValueString(); }
                        catch { return p.AsDouble().ToString("0.###", CultureInfo.InvariantCulture); }
                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<FamilySymbol> Symbols(Document doc)
        {
            var mepId = new ElementId((int)BuiltInCategory.OST_MechanicalEquipment);
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Family != null)
                .Where(s => (s.Category?.Id ?? ElementId.InvalidElementId) == mepId)
                .ToList();
        }
    }
}