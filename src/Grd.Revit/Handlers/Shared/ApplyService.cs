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

                GrdLog.Log("ApplyService.Apply: id=" + selectedId + ", марка=\u201C" + (parsed?.Mark ?? string.Empty) +
                    "\u201D, код=" + (parsed?.Code ?? string.Empty) +
                    ", семейство-подсказка=\u201C" + (parsed?.FamilyHint ?? string.Empty) + "\u201D");

                ElementId appliedId = ElementId.InvalidElementId;
                string result;

                using (var t = new Transaction(doc, "JTOOLS: применить тип прибора"))
                {
                    try
                    {
                        t.Start();

                        if (!FamilyTypeResolver.TryResolve(doc, parsed, settings, out var symbol, out var resolveLog))
                        {
                            t.RollBack();
                            GrdLog.Log("ApplyService.Apply: ТИП НЕ РЕШЁН. detail: " + resolveLog);
                            return resolveLog;
                        }

                        var currentFamily = instance.Symbol?.Family?.Name ?? string.Empty;
                        var targetFamily = symbol.Family.Name;
                        GrdLog.Log("ApplyService.Apply: resolved=" + symbol.Family.Name + " :: " + symbol.Name +
                                   " | текущее семейство=\u201C" + currentFamily + "\u201D, целевое=\u201C" + targetFamily + "\u201D" +
                                   " (resolveLog: " + resolveLog + ")");

                        var sb = new StringBuilder();
                        sb.AppendLine(resolveLog);

                        Element target = instance;

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
                        GrdLog.Log("ApplyService.Apply: коммит ok (id=" + appliedId + "); итог: " +
                                   result.Replace("\r\n", " | "));
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
            double rotation = 0;
            try { rotation = lp.Rotation; } catch { }

            Level level = null;
            try
            {
                level = doc.GetElement(old.LevelId) as Level;
            }
            catch
            {
                // У элемента без корректного уровня GetElement может бросить исключение.
            }

            // «Отметка от уровня» старого экземпляра. NewFamilyInstance игнорирует Z точки
            // и ставит элемент на отметку уровня: если старый прибор был поднят над уровнем,
            // новый окажется ниже/выше диапазона видимости плана и «исчезнет» на виде.
            double oldOffset = 0;
            try
            {
                var off = old.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                if (off != null) oldOffset = off.AsDouble();
            }
            catch { }

            // Фазы: новый элемент по умолчанию попадает в фазу документа, а старый мог быть
            // в другой фазе (или в демонтаже) — там, где видна эта фаза, он «пропадает».
            ElementId oldPhaseId = ElementId.InvalidElementId;
            ElementId oldDemoId = ElementId.InvalidElementId;
            try
            {
                var p = old.get_Parameter(BuiltInParameter.PHASE_CREATED);
                if (p != null && p.StorageType == StorageType.ElementId) oldPhaseId = p.AsElementId();
                var d = old.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                if (d != null && d.StorageType == StorageType.ElementId) oldDemoId = d.AsElementId();
            }
            catch { }

            string oldCategory = old.Category?.Name ?? string.Empty;

            try
            {
                created = doc.Create.NewFamilyInstance(point, symbol, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            }
            catch (Exception ex)
            {
                msg = $"Не удалось создать новый экземпляр: {ex.Message}";
                return false;
            }

            if (Math.Abs(rotation) > 1e-9)
            {
                try
                {
                    var axis = Line.CreateBound(point, point + XYZ.BasisZ);
                    ElementTransformUtils.RotateElement(doc, created.Id, axis, rotation);
                }
                catch { }
            }
            if (Math.Abs(oldOffset) > 1e-9)
            {
                try
                {
                    var off = created.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                    if (off != null && !off.IsReadOnly) off.Set(oldOffset);
                }
                catch { }
            }
            if (oldPhaseId != ElementId.InvalidElementId)
            {
                try
                {
                    var p = created.get_Parameter(BuiltInParameter.PHASE_CREATED);
                    if (p != null && !p.IsReadOnly) p.Set(oldPhaseId);
                }
                catch { }
            }
            if (oldDemoId != ElementId.InvalidElementId)
            {
                try
                {
                    var d = created.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                    if (d != null && !d.IsReadOnly) d.Set(oldDemoId);
                }
                catch { }
            }

            // Остальные значения параметров экземпляра — из старого элемента (значения,
            // которые пишет ParameterApplier, накладываются сверху уже после замены).
            CopyInstanceParameters(old, created);

            try
            {
                doc.Delete(old.Id);
            }
            catch (Exception ex)
            {
                // Элемент может быть закреплён или связан зависимостями — новый не оставляем.
                try { doc.Delete(created.Id); } catch { }
                created = null;
                msg = $"Новый экземпляр создан, но старый не удалён: {ex.Message}";
                return false;
            }

            string newCategory = created.Category?.Name ?? string.Empty;
            if (!string.Equals(oldCategory, newCategory, StringComparison.OrdinalIgnoreCase))
            {
                msg = $"Создан: {symbol.Family.Name} :: {symbol.Name}. ВНИМАНИЕ: категория изменилась " +
                      $"«{oldCategory}» -> «{newCategory}» — если элемент не виден на виде, проверьте категорию " +
                      "и визуальный стиль семейства.";
            }
            else
            {
                msg = $"Создан: {symbol.Family.Name} :: {symbol.Name}";
            }

            GrdLog.Log("ApplyService.TryReplaceInstance: " + symbol.Family.Name + " :: " + symbol.Name +
                       ", точка=(" + point.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "; " +
                       point.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "), " +
                       "уровень=" + (level?.Name ?? "?") +
                       ", поворот=" + rotation.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) +
                       ", отметка=" + oldOffset.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) +
                       ", категория=" + newCategory);
            return true;
        }

        /// <summary>
        /// Переносит значения параметров экземпляра со старого элемента на новый
        /// (созданный вместо него). Обход через Definition, т.к. готового API для
        /// копирования «всех параметров» у Revit нет. Применяемые ниже параметры
        /// (Фhl, настройка клапана, марка) записываются после вызова поверх.
        /// </summary>
        private static void CopyInstanceParameters(FamilyInstance from, FamilyInstance to)
        {
            foreach (Parameter p in from.Parameters)
            {
                if (p == null || p.IsReadOnly || !p.HasValue) continue;

                Parameter target;
                try { target = to.get_Parameter(p.Definition); } catch { continue; }
                if (target == null || target.IsReadOnly) continue;
                if (!target.IsShared && target.Id == ElementId.InvalidElementId && p.Id == ElementId.InvalidElementId) continue;

                try
                {
                    switch (p.StorageType)
                    {
                        case StorageType.Double:
                            target.Set(p.AsDouble());
                            break;
                        case StorageType.Integer:
                            target.Set(p.AsInteger());
                            break;
                        case StorageType.String:
                            if (p.AsString() != null) target.Set(p.AsString());
                            break;
                        case StorageType.ElementId:
                            if (p.AsElementId() != ElementId.InvalidElementId) target.Set(p.AsElementId());
                            break;
                    }
                }
                catch { }
            }
        }
    }
}