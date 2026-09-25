using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace GrdRevit.Revit
{
    /// <summary>Семейство механического оборудования с его типами (для выпадающих списков).</summary>
    public sealed class MarksCatalogFamily
    {
        public string Name = string.Empty;
        public List<string> Types = new List<string>();
    }

    /// <summary>Результат чтения каталога механического оборудования из документа.</summary>
    public sealed class MarksCatalogResult
    {
        public bool Ok;
        public string Error;
        public List<MarksCatalogFamily> Families = new List<MarksCatalogFamily>();
    }

    /// <summary>
    /// Читает в API-контексте каталог механического оборудования активного документа:
    /// семейства категории «Механическое оборудование» и все их типы. Окно марок DXF
    /// не может обращаться к документу напрямую — запрос ставится в очередь и выполняется
    /// Revit-потоком через ExternalEvent, результат возвращается в колбэк.
    /// </summary>
    public class MarksCatalogHandler : IExternalEventHandler
    {
        private readonly object _sync = new object();
        private Action<MarksCatalogResult> _callback;

        public void Queue(Action<MarksCatalogResult> callback)
        {
            lock (_sync) { _callback = callback; }
        }

        public string GetName()
        {
            return "JTOOLS: каталог механического оборудования";
        }

        public void Execute(UIApplication app)
        {
            Action<MarksCatalogResult> cb;
            lock (_sync)
            {
                cb = _callback;
                _callback = null;
            }
            if (cb == null)
            {
                GrdLog.Log("MarksCatalogHandler.Execute: нет запроса в очереди");
                return;
            }

            try
            {
                cb(Read(app));
            }
            catch (Exception ex)
            {
                GrdLog.Log("MarksCatalogHandler.Execute: EXCEPTION: " + ex);
                try
                {
                    cb(new MarksCatalogResult { Ok = false, Error = "Ошибка чтения каталога: " + ex.Message });
                }
                catch { }
            }
        }

        private static MarksCatalogResult Read(UIApplication app)
        {
            var uiDoc = app?.ActiveUIDocument;
            if (uiDoc == null) return Fail("Нет активного документа Revit.");
            if (uiDoc.Document == null) return Fail("Документ недоступен.");

            var doc = uiDoc.Document;
            var mechCat = Category.GetCategory(doc, BuiltInCategory.OST_MechanicalEquipment);
            if (mechCat == null) return Fail("Категория «Механическое оборудование» не найдена в документе.");

            var result = new MarksCatalogResult { Ok = true };
            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Family != null && s.Category != null && s.Category.Id == mechCat.Id)
                .ToList();

            var byFamily = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in symbols)
            {
                if (s.Family == null || string.IsNullOrEmpty(s.Family.Name)) continue;
                if (!byFamily.TryGetValue(s.Family.Name, out var types))
                {
                    types = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    byFamily[s.Family.Name] = types;
                }
                if (!string.IsNullOrEmpty(s.Name))
                    types.Add(s.Name);
            }

            foreach (var kv in byFamily.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                result.Families.Add(new MarksCatalogFamily
                {
                    Name = kv.Key,
                    Types = kv.Value.ToList()
                });
            }

            GrdLog.Log("MarksCatalogHandler: семейств=" + result.Families.Count +
                       ", типов=" + symbols.Count);
            return result;
        }

        private static MarksCatalogResult Fail(string message)
        {
            GrdLog.Log("MarksCatalogHandler: " + message);
            return new MarksCatalogResult { Ok = false, Error = message };
        }
    }
}