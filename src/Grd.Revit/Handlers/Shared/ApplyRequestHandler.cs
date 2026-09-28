using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GrdRevit.Core;

namespace GrdRevit.Revit
{
    /// <summary>
    /// Выполняет применение типа прибора в API-контексте. Кнопка моделесс-окна
    /// не может открывать транзакции напрямую ("outside of API context") — вместо
    /// этого она ставит запрос в очередь и вызывает ExternalEvent.Raise().
    /// Execute() этого обработчика Revit выполняет сам, в корректном API-контексте.
    /// </summary>
    public class ApplyRequestHandler : IExternalEventHandler
    {
        private readonly object _sync = new object();
        private GrdDevice _row;
        private GrdSettings _settings;
        private ElementId[] _ids;
        private KeyValuePair<ElementId, GrdDevice>[] _pairs;
        private Action _done;

        /// <summary>Очередь для одиночного применения: id элемента берётся из текущего выбора.</summary>
        public void Queue(GrdDevice row, GrdSettings settings)
        {
            Queue(null, row, settings, null);
        }

        /// <summary>Очередь применения к конкретным элементам (одному или нескольким сразу).</summary>
        public void Queue(ElementId[] ids, GrdDevice row, GrdSettings settings)
        {
            Queue(ids, row, settings, null);
        }

        /// <summary>Очередь применения с колбэком завершения (вызывается после ShowResult, в API-контексте).</summary>
        public void Queue(ElementId[] ids, GrdDevice row, GrdSettings settings, Action done)
        {
            lock (_sync)
            {
                _ids = ids;
                _row = row;
                _pairs = null;
                _settings = settings;
                _done = done;
            }
        }

        /// <summary>
        /// Очередь применения «каждому элементу — свой прибор» (замена нескольких несоответствий разом,
        /// когда у элементов разные марки DXF). Каждая пара применяется отдельной транзакцией.
        /// </summary>
        public void Queue(KeyValuePair<ElementId, GrdDevice>[] pairs, GrdSettings settings, Action done)
        {
            lock (_sync)
            {
                _ids = null;
                _row = null;
                _pairs = pairs;
                _settings = settings;
                _done = done;
            }
        }

        public string GetName()
        {
            return "JTOOLS: применить тип прибора";
        }

        public void Execute(UIApplication app)
        {
            GrdDevice row;
            GrdSettings settings;
            ElementId[] ids;
            KeyValuePair<ElementId, GrdDevice>[] pairs;
            Action done;
            lock (_sync)
            {
                ids = _ids;
                row = _row;
                pairs = _pairs;
                settings = _settings;
                done = _done;
                _ids = null;
                _row = null;
                _pairs = null;
                _settings = null;
                _done = null;
            }

            if (row == null && pairs == null)
            {
                GrdLog.Log("ApplyHandler.Execute: нет запроса в очереди");
                return;
            }

            GrdLog.Log("ApplyHandler.Execute: обработка " + (row?.Code ?? (pairs != null ? pairs.Length + " пар(ы)" : "?")));
            string result;
            try
            {
                var uiDoc = app?.ActiveUIDocument;
                if (uiDoc == null)
                {
                    result = "Нет активного документа.";
                }
                else if (pairs != null && pairs.Length > 0)
                {
                    result = ApplyManyPairs(app, pairs, settings);
                }
                else if (ids != null && ids.Length > 0)
                {
                    result = ApplyMany(app, ids, row, settings);
                }
                else
                {
                    var selected = uiDoc.Selection.GetElementIds();
                    if (selected == null || selected.Count != 1 || selected.First() == ElementId.InvalidElementId)
                    {
                        result = "Выберите ровно один элемент механического оборудования.";
                    }
                    else
                    {
                        result = ApplyService.Apply(app, selected.First(), row, settings);
                    }
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("ApplyHandler.Execute: EXCEPTION: " + ex);
                result = "! Ошибка при применении: " + ex.Message;
            }

            GrdLog.Log("ApplyHandler.Execute: результат: " + result);
            ShowResult(result, row);

            try
            {
                done?.Invoke();
            }
            catch (Exception ex)
            {
                GrdLog.Log("ApplyHandler.Execute: колбэк завершения упал: " + ex);
            }
        }

        /// <summary>Применяет марку к каждому из выбранных элементов (отдельная транзакция на элемент).</summary>
        private static string ApplyMany(UIApplication app, ElementId[] ids, GrdDevice row, GrdSettings settings)
        {
            var sb = new System.Text.StringBuilder();
            var failures = new System.Collections.Generic.List<string>();
            int applied = 0;
            foreach (var id in ids)
            {
                var r = ApplyService.Apply(app, id, row, settings);
                if (string.IsNullOrEmpty(r)) { applied++; continue; }
                if (IsFailure(r)) failures.Add(FirstLine(r));
                else applied++;
            }

            if (failures.Count == 0)
                sb.Append("Применено к ").Append(applied).Append(Plural(applied, " элементу", " элементам", " элементам")).Append('.');
            else
                sb.Append("Применено к ").Append(applied).Append(" из ").Append(ids.Length).Append(". Не применено: ")
                  .Append(string.Join("; ", failures));

            string output = sb.ToString();
            GrdLog.Log("ApplyHandler.ApplyMany: " + output);
            return output;
        }

        /// <summary>Применяет каждой паре «элемент → свой прибор» (отдельная транзакция на элемент).</summary>
        private static string ApplyManyPairs(UIApplication app, KeyValuePair<ElementId, GrdDevice>[] pairs, GrdSettings settings)
        {
            var sb = new System.Text.StringBuilder();
            var failures = new System.Collections.Generic.List<string>();
            int applied = 0;
            foreach (var p in pairs)
            {
                var r = ApplyService.Apply(app, p.Key, p.Value, settings);
                if (string.IsNullOrEmpty(r)) { applied++; continue; }
                if (IsFailure(r)) failures.Add(FirstLine(r));
                else applied++;
            }

            if (failures.Count == 0)
                sb.Append("Применено к ").Append(applied).Append(Plural(applied, " элементу", " элементам", " элементам")).Append('.');
            else
                sb.Append("Применено к ").Append(applied).Append(" из ").Append(pairs.Length).Append(". Не применено: ")
                  .Append(string.Join("; ", failures));

            string output = sb.ToString();
            GrdLog.Log("ApplyHandler.ApplyManyPairs: " + output);
            return output;
        }

        private static bool IsFailure(string r)
        {
            return r.StartsWith("!") || r.StartsWith("Не удалось") || r.StartsWith("Выберите")
                   || r.StartsWith("Нет активного") || r.StartsWith("Карта значений не настроена")
                   || r.StartsWith("Тип не применён");
        }

        private static string FirstLine(string s)
        {
            var i = s.IndexOf('\n');
            return i < 0 ? s : s.Substring(0, i).Trim();
        }

        private static string Plural(int n, string one, string few, string many)
        {
            int m10 = n % 10, m100 = n % 100;
            if (m10 == 1 && m100 != 11) return one;
            if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
            return many;
        }

        private void ShowResult(string result, GrdDevice row)
        {
            bool bad = result.StartsWith("!") || result.StartsWith("Не удалось")
                       || result.StartsWith("Выберите") || result.StartsWith("Нет активного")
                       || result.StartsWith("Карта значений не настроена");
            if (bad)
            {
                try
                {
                    var td = new TaskDialog("JTOOLS: применение типа")
                    {
                        MainInstruction = "Тип не применён.",
                        MainContent = result,
                        MainIcon = TaskDialogIcon.TaskDialogIconWarning,
                    };
                    td.Show();
                }
                catch
                {
                    // Не даём сбою диалога результата вылететь в Revit.
                }
                return;
            }

            // Успех «поздравляем» лёгким уведомлением: применённый тип, мощность и настройка клапана.
            try
            {
                var sb = new System.Text.StringBuilder("Применено: ");

                var appliedType = ExtractAppliedType(result);
                sb.Append("Тип: ").Append(appliedType ?? row.FamilyHint ?? row.Code);

                if (row.CapacityW.HasValue)
                    sb.Append(" | Мощность: ").Append(row.CapacityW.Value.ToString(
                        "0", System.Globalization.CultureInfo.InvariantCulture)).Append(" Вт");

                if (!string.IsNullOrEmpty(row.ValveSetting))
                    sb.Append(" | Настройка: ").Append(row.ValveSetting);

                GrdRevit.Ui.SnackBar.Show(sb.ToString());
            }
            catch (Exception ex)
            {
                GrdLog.Log("ShowResult: уведомление упало, лог выше остаётся источником истины: " + ex);
            }
        }

        /// <summary>Вытаскивает применённый тип («Семейство :: Тип») из строки результата применения.</summary>
        private static string ExtractAppliedType(string result)
        {
            if (string.IsNullOrEmpty(result)) return null;
            foreach (var line in result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim();
                if (t.StartsWith("Тип:")) return t.Substring(4).Trim();
                if (t.StartsWith("Создан:")) return t.Substring(7).Trim();
            }
            return null;
        }
    }
}