using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace GrdRevit.Revit
{
    /// <summary>Результат чтения семейства и его типов из выделенного в Revit элемента.</summary>
    public sealed class PickTypeResult
    {
        public bool Ok;
        public string FamilyName;
        public List<string> TypeNames = new List<string>();
        public string Error;
    }

    /// <summary>
    /// Выполняет в API-контексте чтение семейства и всех его типов из выбранного элемента.
    /// Окно настроек не может обращаться к документу напрямую ("outside of API context") —
    /// запрос ставится в очередь и выполняется Revit-потоком через ExternalEvent,
    /// результат возвращается в колбэк на том же потоке.
    /// </summary>
    public class PickTypeHandler : IExternalEventHandler
    {
        private readonly object _sync = new object();
        private Action<PickTypeResult> _callback;

        public void Queue(Action<PickTypeResult> callback)
        {
            lock (_sync) { _callback = callback; }
        }

        public string GetName()
        {
            return "JTOOLS: прочитать типы выделенного семейства";
        }

        public void Execute(UIApplication app)
        {
            Action<PickTypeResult> cb;
            lock (_sync)
            {
                cb = _callback;
                _callback = null;
            }
            if (cb == null)
            {
                GrdLog.Log("PickTypeHandler.Execute: нет запроса в очереди");
                return;
            }

            try
            {
                cb(Read(app));
            }
            catch (Exception ex)
            {
                GrdLog.Log("PickTypeHandler.Execute: EXCEPTION: " + ex);
                try
                {
                    cb(new PickTypeResult { Ok = false, Error = "Ошибка чтения типа: " + ex.Message });
                }
                catch { }
            }
        }

        private static PickTypeResult Read(UIApplication app)
        {
            var uiDoc = app?.ActiveUIDocument;
            if (uiDoc == null) return Fail("Нет активного документа Revit.");
            if (uiDoc.Document == null) return Fail("Документ недоступен.");

            var ids = uiDoc.Selection.GetElementIds();
            if (ids == null || ids.Count == 0) return Fail("Ничего не выбрано. Выберите элемент механического оборудования.");
            if (ids.Count != 1) return Fail("Выбрано элементов: " + ids.Count + ". Нужен ровно один.");

            var el = uiDoc.Document.GetElement(ids.First());

            Family family = null;
            if (el is FamilyInstance fi)
            {
                if (!fi.IsValidObject || fi.Symbol == null || fi.Symbol.Family == null)
                    return Fail("У выбранного элемента нет семейства.");
                family = fi.Symbol.Family;
            }
            else if (el is FamilySymbol fs)
            {
                family = fs.Family;
            }
            else
            {
                return Fail("Выбранный элемент — не экземпляр механического оборудования.");
            }

            if (family == null) return Fail("Не удалось определить семейство элемента.");

            var doc = uiDoc.Document;
            var typeNames = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Family != null && s.Family.Id == family.Id)
                .Select(s => s.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (typeNames.Count == 0)
                return Fail("В семействе «" + family.Name + "» нет типов.");

            GrdLog.Log("PickTypeHandler: family='" + family.Name + "' types=" + typeNames.Count);
            return new PickTypeResult
            {
                Ok = true,
                FamilyName = family.Name,
                TypeNames = typeNames,
                Error = string.Empty
            };
        }

        private static PickTypeResult Fail(string message)
        {
            GrdLog.Log("PickTypeHandler: " + message);
            return new PickTypeResult { Ok = false, Error = message };
        }
    }
}