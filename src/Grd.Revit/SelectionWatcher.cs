using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace GrdRevit.Revit
{
    /// <summary>
    /// Следит за изменением выбора. Кнопки строк таблицы активны,
    /// когда выбрано ровно одно механическое оборудование.
    /// </summary>
    public class SelectionWatcher : IDisposable
    {
        private readonly UIApplication _uiApp;
        private event Action<bool, ElementId> SelectionChanged;

        public ElementId SelectedId { get; private set; }
        public bool IsSingleEquipment { get; private set; }

        public SelectionWatcher(UIApplication uiApp)
        {
            _uiApp = uiApp;
            try { _uiApp.SelectionChanged += OnSelectionChanged; } catch { }
            try { OnSelectionChanged(null, null); } catch { }
        }

        public void Subscribe(Action<bool, ElementId> handler)
        {
            SelectionChanged += handler;
            try { handler?.Invoke(IsSingleEquipment, SelectedId); } catch { }
        }

        public void Unsubscribe(Action<bool, ElementId> handler)
        {
            SelectionChanged -= handler;
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool ok = false;
            ElementId id = ElementId.InvalidElementId;

            try
            {
                var uiDoc = _uiApp?.ActiveUIDocument;
                if (uiDoc != null)
                {
                    var ids = uiDoc.Selection.GetElementIds();
                    if (ids.Count == 1)
                    {
                        var el = uiDoc.Document.GetElement(ids.First());
                        if (IsSupported(el))
                        {
                            ok = true;
                            id = el.Id;
                        }
                    }
                }
            }
            catch { /* выбор может быть недоступен в момент события */ }

            IsSingleEquipment = ok;
            SelectedId = id;
            try { SelectionChanged?.Invoke(ok, id); } catch { }
        }

        public static bool IsSupported(Element el)
        {
            if (el is FamilyInstance fi && fi.Symbol != null && fi.Symbol.Family != null)
            {
                var cat = el.Category?.Id;
                // Механическое оборудование (и, запасом, трубопроводная арматура из категории креплений).
                if (cat == new ElementId((int)BuiltInCategory.OST_MechanicalEquipment)) return true;
#if REVIT2026
                if (cat == new ElementId((int)BuiltInCategory.OST_PipeAccessory)) return true;
#endif
            }
            return false;
        }

        public void Dispose()
        {
            try { if (_uiApp != null) _uiApp.SelectionChanged -= OnSelectionChanged; } catch { }
        }
    }
}