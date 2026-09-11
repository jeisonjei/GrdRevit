using System;
using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using GrdRevit.Core;

namespace GrdRevit.Revit
{
    /// <summary>
    /// Применяет значения прибора к параметрам экземпляра выбранного элемента
    /// по карте настроек «Источник значения -> параметр экземпляра Revit».
    /// Источники фиксированы: "Фhl" (тепловая мощность, Вт) и "Valve setting"
    /// (настройка клапана — текстовое значение из главной таблицы).
    /// </summary>
    public static class ParameterApplier
    {
        public static string Apply(Element element, GrdSettings settings, GrdDevice device)
        {
            var sb = new StringBuilder();
            var map = settings.ValueParamMap;

            if (map == null || map.Count == 0)
            {
                return "Карта значений не настроена (Настройки -> Карта значений прибора).";
            }

            if (map.TryGetValue("Фhl", out var fhlParam) && !string.IsNullOrWhiteSpace(fhlParam))
            {
                string value = device?.CapacityW.HasValue == true
                    ? Math.Round(device.CapacityW.Value).ToString("0", CultureInfo.InvariantCulture)
                    : string.Empty;

                if (!string.IsNullOrEmpty(value))
                    WriteParam(element, sb, fhlParam, "Фhl", value);
                else
                    sb.AppendLine($"- «Фhl»: нет значения тепловой мощности");
            }

            if (map.TryGetValue("Valve setting", out var valveParam) && !string.IsNullOrWhiteSpace(valveParam))
            {
                string value = device?.ValveSetting ?? string.Empty;
                if (!string.IsNullOrEmpty(value))
                    WriteParam(element, sb, valveParam, "Valve setting", value);
                else
                    sb.AppendLine($"- «Valve setting»: пустое значение");
            }

            return sb.ToString().TrimEnd('\n');
        }

        private static void WriteParam(Element element, StringBuilder sb, string paramName, string source, string value)
        {
            Parameter p = null;
            try { p = element.LookupParameter(paramName); } catch { }

            if (p == null)
            {
                sb.AppendLine($"- Параметр \u201C{paramName}\u201D не найден");
                return;
            }
            if (p.IsReadOnly)
            {
                sb.AppendLine($"- Параметр \u201C{paramName}\u201D доступен только для чтения");
                return;
            }

            try
            {
                switch (p.StorageType)
                {
                    case StorageType.Double:
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                            || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out d))
                        {
                            var spec = GetSpec(p);
                            var (chosen, shown) = SetDoubleMatchingDisplay(p, d, spec);
                            if (chosen == "none")
                                sb.AppendLine($"! {source} -> \u201C{paramName}\u201D = {value}: запись не выполнена" +
                                    $" (unit={spec?.TypeId ?? "<none>"})");
                            else
                                sb.AppendLine($"+ {source} -> \u201C{paramName}\u201D = {value}" +
                                    (string.IsNullOrEmpty(shown) ? string.Empty : $" -> \"{shown}\"") +
                                    $" [unit={spec?.TypeId ?? "<none>"}, mode={chosen}]");
                        }
                        break;
                    case StorageType.Integer:
                        p.Set(int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0);
                        break;
                    case StorageType.String:
                        p.Set(value);
                        break;
                    case StorageType.ElementId:
                        // не трогаем ссылочные параметры
                        return;
                }
                string shownPlain = string.Empty;
                try { shownPlain = p.AsValueString() ?? string.Empty; } catch { }
                if (p.StorageType != StorageType.Double)
                {
                    string detail = string.IsNullOrEmpty(shownPlain) ? string.Empty : $" -> \"{shownPlain}\"";
                    sb.AppendLine($"+ {source} -> \u201C{paramName}\u201D = {value}{detail}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"! Не удалось записать {paramName}: {ex.Message}");
            }
        }

        private static ForgeTypeId GetSpec(Parameter p)
        {
            try { return p.Definition?.GetDataType(); } catch { }
            return null;
        }

        /// <summary>
        /// Пишет число выбранным способом (string -> internal -> display).
        /// Верификация точного значения выполняется ПОСЛЕ коммита (VerifyAndRepair),
        /// т.к. в активной транзакции AsValueString может быть устаревшим.
        /// </summary>
        private static (string mode, string shown) SetDoubleMatchingDisplay(Parameter p, double d, ForgeTypeId spec)
        {
            Func<double, double> toInternal = cand =>
            {
                double v;
                try { v = spec == null ? cand : UnitUtils.ConvertToInternalUnits(cand, spec); }
                catch (Exception ex)
                {
                    GrdLog.Log($"  [debug] toInternal({cand}): конвертация недоступна: {ex.Message}");
                    throw;
                }
                GrdLog.Log($"  [debug] toInternal({cand}) -> {v} (spec={spec?.TypeId ?? "<none>"})");
                return v;
            };
            Action<double> setDouble = cand =>
            {
                GrdLog.Log($"  [debug] Parameter.Set({cand}) ...");
                bool ok = p.Set(cand);
                GrdLog.Log($"  [debug]     после Set: ok={ok} AsDouble={TryAsDouble(p):0.####} AsValueString=\"{SafeAsValueString(p)}\"");
            };
            Func<string, bool> setValueString = s =>
            {
                GrdLog.Log($"  [debug] Parameter.SetValueString(\"{s}\") ...");
                p.SetValueString(s);
                GrdLog.Log($"  [debug]     после SetValueString: AsDouble={TryAsDouble(p):0.####} AsValueString=\"{SafeAsValueString(p)}\"");
                return true;
            };
            Func<string> readback = () => SafeAsValueString(p);
            var (chosen, shown) = ValueWriteSelector.ChooseWrite(d, toInternal, setDouble, setValueString, readback);
            GrdLog.Log($"  [debug] выбран способ mode={chosen}; отклик читается после коммита ([verify])");
            return (chosen, shown);
        }

        /// <summary>
        /// После коммита транзакции проверяет по СВЕЖЕМУ состоянию документа, что
        /// отображаемое значение параметра равно желаемому; если нет — доводит
        /// отдельными транзакциями (линейная модель «отображение = k*хранимое»).
        /// </summary>
        public static string VerifyAndRepair(Document doc, ElementId targetId, GrdSettings settings, GrdDevice device)
        {
            var map = settings?.ValueParamMap;
            if (map == null || map.Count == 0) return string.Empty;

            var sb = new StringBuilder();

            if (map.TryGetValue("Фhl", out var fhlParam) && !string.IsNullOrWhiteSpace(fhlParam) &&
                device?.CapacityW.HasValue == true)
            {
                double desired = Math.Round(device.CapacityW.Value);
                VerifyOneDouble(doc, targetId, fhlParam, "Фhl", desired, sb);
            }

            string result = sb.ToString().TrimEnd('\n');
            if (!string.IsNullOrEmpty(result)) GrdLog.Log("[verify] " + result.Replace("\n", "\n[verify] "));
            return result;
        }

        private static void VerifyOneDouble(Document doc, ElementId elId, string paramName, string source, double desired, StringBuilder sb)
        {
            Element FindElement()
            {
                try { return doc.GetElement(elId); } catch { return null; }
            }
            Parameter FindParam()
            {
                return FindElement()?.LookupParameter(paramName);
            }
            double ReadDouble()
            {
                return TryAsDouble(FindParam());
            }
            string ReadDisplay()
            {
                return SafeAsValueString(FindParam());
            }

            var shownBefore = ReadDisplay();

            Action<double> write = next =>
            {
                var pp = FindParam();
                if (pp == null) throw new InvalidOperationException("параметр не найден после применения");
                using (var t = new Transaction(doc, "JTOOLS: коррекция значения"))
                {
                    t.Start();
                    if (!pp.Set(next)) throw new InvalidOperationException("Set(bool) вернул false");
                    t.Commit();
                }
                GrdLog.Log($"  [verify] коррекция: Set({next:0.####}) -> \"{SafeAsValueString(FindParam())}\"");
            };

            var (ok, stored, shown) = RoundTripVerify.Repair(
                desired,
                ReadDouble,
                ReadDisplay,
                write);

            string line = ok
                ? $"\u002B {source} -> \u201C{paramName}\u201D: итог \"{shown}\" (было \"{shownBefore}\")"
                : $"! {source} -> \u201C{paramName}\u201D: не удалось добиться отображения {desired:0.####} (итог \"{shown}\", хранимое {stored:0.####})";
            sb.AppendLine(line);
        }

        private static double TryAsDouble(Parameter p)
        {
            try { return p.AsDouble(); } catch { return double.NaN; }
        }

        private static string SafeAsValueString(Parameter p)
        {
            try { return p.AsValueString() ?? string.Empty; } catch { }
            return string.Empty;
        }
    }
}