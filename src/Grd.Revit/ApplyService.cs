using System;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GrdRevit.Core;

namespace GrdRevit.Revit
{
    /// <summary>
    /// Применяет тип из выбранной строки таблицы к выделенному единственному
    /// механическому оборудованию: устанавливает тип семейства и параметры экземпляра.
    /// Если семейство выбранного элемента отличается от нужного — новый экземпляр
    /// размещается на месте старого, старый удаляется.
    /// </summary>
    public static class ApplyService
    {
        public static string Apply(UIApplication uiApp, ElementId selectedId, GrdDevice parsed, GrdSettings settings)
        {
            try
            {
                var uiDoc = uiApp?.ActiveUIDocument;
                if (uiDoc == null) return "Активный документ не доступен.";
                var doc = uiDoc.Document;
                if (doc == null) return "Документ не доступен.";

                if (doc.GetElement(selectedId) is not FamilyInstance instance)
                {
                    return "Выбранный элемент не является экземпляром семейства.";
                }

                ElementId appliedId = ElementId.InvalidElementId;
                string result;

                using (var t = new Transaction(doc, "Audytor: применить тип прибора"))
                {
                    try
                    {
                        t.Start();

                        if (!FamilyTypeResolver.TryResolve(doc, parsed, settings, out var symbol, out var resolveLog))
                        {
                            t.RollBack();
                            return resolveLog;
                        }

                        var sb = new StringBuilder();
                        sb.AppendLine(resolveLog);

                        Element target = instance;
                        var currentFamily = instance.Symbol?.Family?.Name ?? string.Empty;
                        var targetFamily = symbol.Family.Name;

                        if (!string.Equals(currentFamily, targetFamily, StringComparison.OrdinalIgnoreCase))
                        {
                            // Разные семейства: размещаем новый экземпляр целевого типа на месте старого.
                            if (TryReplaceInstance(instance, symbol, out var created, out var msg))
                            {
                                target = created;
                                sb.AppendLine($"Заменено: \u201C{currentFamily}\u201D -> \u201C{targetFamily}\u201D");
                                sb.AppendLine(msg);
                            }
                            else
                            {
                                t.RollBack();
                                return msg;
                            }
                        }
                        else
                        {
                            instance.Symbol = symbol;
                            sb.AppendLine($"Тип: {targetFamily} :: {symbol.Name}");
                        }

                        appliedId = target.Id;
                        var paramLog = ParameterApplier.Apply(target, settings, parsed);
                        if (!string.IsNullOrEmpty(paramLog)) sb.AppendLine(paramLog);

                        t.Commit();
                        result = sb.ToString();
                    }
                    catch (Exception ex)
                    {
                        // Любое непредвиденное исключение (закреплённые/связанные элементы,
                        // параметры в группах, сбой Commit и т.п.) должно приводить к откату,
                        // а не к вылету необработанной ошибки в Revit.
                        try { if (t.HasStarted()) t.RollBack(); } catch { }
                        return "! Ошибка при применении: " + ex.Message;
                    }
                }

                // ПОСЛЕ коммита: проверяем по свежему состоянию документа, что параметры
                // приняли точные значения (в транзакции чтение может отставать), и при
                // необходимости доводим отдельными транзакциями.
                try
                {
                    var verify = ParameterApplier.VerifyAndRepair(doc, appliedId, settings, parsed);
                    if (!string.IsNullOrEmpty(verify)) result += "\n" + verify;
                }
                catch (Exception ex)
                {
                    GrdLog.Log("ApplyService: верификация: EXCEPTION " + ex);
                    result += "\n! Ошибка верификации: " + ex.Message;
                }

                return result;
            }
            catch (Exception ex)
            {
                return "! Ошибка при применении: " + ex.Message;
            }
        }

        private static bool TryReplaceInstance(FamilyInstance old, FamilySymbol symbol, out FamilyInstance created, out string msg)
        {
            created = null;
            msg = string.Empty;
            var doc = old.Document;

            if (!(old.Location is LocationPoint lp))
            {
                msg = "Не удалось определить точку размещения элемента.";
                return false;
            }

            var point = lp.Point;
            Level level = null;
            try
            {
                level = doc.GetElement(old.LevelId) as Level;
            }
            catch
            {
                // У элемента без корректного уровня GetElement может бросить исключение.
            }

            try
            {
                created = doc.Create.NewFamilyInstance(point, symbol, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            }
            catch (Exception ex)
            {
                msg = $"Не удалось создать новый экземпляр: {ex.Message}";
                return false;
            }

            try
            {
                doc.Delete(old.Id);
            }
            catch (Exception ex)
            {
                // Элемент может быть закреплён или связан зависимостями.
                msg = $"Новый экземпляр создан, но старый не удалён: {ex.Message}";
                return false;
            }

            msg = $"Создан: {symbol.Family.Name} :: {symbol.Name}";
            return true;
        }
    }
}