using System;
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

        public void Queue(GrdDevice row, GrdSettings settings)
        {
            lock (_sync)
            {
                _row = row;
                _settings = settings;
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
            lock (_sync)
            {
                row = _row;
                settings = _settings;
                _row = null;
            }

            if (row == null)
            {
                GrdLog.Log("ApplyHandler.Execute: нет запроса в очереди");
                return;
            }

            GrdLog.Log("ApplyHandler.Execute: обработка " + row.Code);
            string result;
            try
            {
                var uiDoc = app?.ActiveUIDocument;
                if (uiDoc == null)
                {
                    result = "Нет активного документа.";
                }
                else
                {
                    var ids = uiDoc.Selection.GetElementIds();
                    if (ids == null || ids.Count != 1 || ids.First() == ElementId.InvalidElementId)
                    {
                        result = "Выберите ровно один элемент механического оборудования.";
                    }
                    else
                    {
                        result = ApplyService.Apply(app, ids.First(), row, settings);
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