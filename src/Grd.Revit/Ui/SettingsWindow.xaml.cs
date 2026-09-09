using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GrdRevit.Core;

namespace GrdRevit.Ui
{
    /// <summary>Редактируемая пара "ключ -> значение" для таблиц сопоставления в настройках.</summary>
    public class MapPair : ObservableObject
    {
        private string _key = string.Empty;
        private string _value = string.Empty;

        public string Key
        {
            get => _key;
            set => Set(ref _key, value);
        }

        public string Value
        {
            get => _value;
            set => Set(ref _value, value);
        }
    }

    public partial class SettingsWindow : Window
    {
        private ObservableCollection<MapPair> _valueMap;
        private ObservableCollection<MapPair> _exactMap;

        /// <summary>Имена типовых параметров механического оборудования (для выпадающего списка).</summary>
        public ObservableCollection<string> TypeParams { get; } = new ObservableCollection<string>();

        /// <summary>Имена параметров экземпляра (для выпадающих списков карты значений).</summary>
        public ObservableCollection<string> InstanceParams { get; } = new ObservableCollection<string>();

        private string _selectedTypeParam;
        public string SelectedTypeParam
        {
            get => _selectedTypeParam;
            set
            {
                if (string.Equals(_selectedTypeParam, value, StringComparison.Ordinal)) return;
                _selectedTypeParam = value;
                OnTypeParamSelected(value);
            }
        }

        public SettingsWindow()
        {
            InitializeComponent();
            DataContext = this;

            var settings = RevitContext.Settings;

            _valueMap = new ObservableCollection<MapPair>(FixedValueRows(settings.ValueParamMap));
            _exactMap = new ObservableCollection<MapPair>(
                settings.TypeNameExactMap.Select(kv => new MapPair { Key = kv.Key, Value = kv.Value }));

            ValueMapGrid.ItemsSource = _valueMap;
            ExactMapGrid.ItemsSource = _exactMap;

            Loaded += (s, e) => RequestTypeParamNames();
        }

        private static IEnumerable<MapPair> FixedValueRows(Dictionary<string, string> map)
        {
            yield return new MapPair { Key = "Фhl", Value = map.TryGetValue("Фhl", out var f) ? f : string.Empty };
            yield return new MapPair { Key = "Valve setting", Value = map.TryGetValue("Valve setting", out var v) ? v : string.Empty };
        }

        /// <summary>Наполняет выпадающий список имён типовых параметров и параметров экземпляра.</summary>
        private void RequestTypeParamNames()
        {
            try
            {
                var handler = RevitContext.TypeParamsHandler;
                var ev = RevitContext.TypeParamsEvent;
                if (handler == null || ev == null)
                {
                    GrdLog.Log("SettingsWindow.RequestTypeParamNames: обработчик не доступен");
                    return;
                }
                handler.Queue(null, string.Empty, result =>
                {
                    if (result.Error == null)
                    {
                        if (result.ParameterNames.Count > 0)
                        {
                            TypeParams.Clear();
                            foreach (var n in result.ParameterNames) TypeParams.Add(n);
                        }
                        // Не очищаем InstanceParams: добавление новых имён сохраняет
                        // уже выбранный элемент в выпадающих списках.
                        if (result.InstanceParameterNames.Count > 0)
                        {
                            var existing = new HashSet<string>(InstanceParams, StringComparer.OrdinalIgnoreCase);
                            foreach (var n in result.InstanceParameterNames)
                                if (existing.Add(n)) InstanceParams.Add(n);
                        }
                    }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                GrdLog.Log("SettingsWindow.RequestTypeParamNames: EXCEPTION " + ex);
            }
        }

        private void OnTypeParamDropDownClosed(object sender, EventArgs e)
        {
            // Обновляем список после закрытия (не во время открытия попапа).
            try { RequestTypeParamNames(); } catch { }
        }

        /// <summary>
        /// Прочитанный из Revit тип-параметр проставляется в колонку «Код» таблицы
        /// «Сопоставление кода прибора»: для каждой строки берется её «Имя типа Revit»,
        /// в документе находится этот тип и читается значение выбранного параметра.
        /// Строки без найденного типа/значения не меняются.
        /// </summary>
        private void OnTypeParamSelected(string paramName)
        {
            if (string.IsNullOrEmpty(paramName)) return;
            try
            {
                var handler = RevitContext.TypeParamsHandler;
                var ev = RevitContext.TypeParamsEvent;
                if (handler == null || ev == null)
                {
                    MessageBox.Show("Обработчик типовых параметров не доступен.", "ГрД", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var typeNames = _exactMap
                    .Select(p => p.Value?.Trim())
                    .Where(v => !string.IsNullOrEmpty(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                GrdLog.Log("OnTypeParamSelected: параметр='" + paramName + "' строк=" + typeNames.Count);
                handler.Queue(typeNames, paramName, result =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(result.Error))
                        {
                            var td = new Autodesk.Revit.UI.TaskDialog("ГрД: типовые параметры")
                            {
                                MainInstruction = "Не удалось прочитать типовые параметры.",
                                MainContent = result.Error,
                                MainIcon = Autodesk.Revit.UI.TaskDialogIcon.TaskDialogIconWarning
                            };
                            td.Show();
                            return;
                        }

                        int updated = 0;
                        foreach (var pair in _exactMap)
                        {
                            var key = pair.Value?.Trim();
                            if (!string.IsNullOrEmpty(key) &&
                                result.TypeNameToValue.TryGetValue(key, out var value))
                            {
                                if (!string.Equals(pair.Key, value, StringComparison.Ordinal))
                                {
                                    pair.Key = value;
                                    updated++;
                                }
                            }
                        }
                        GrdLog.Log("OnTypeParamSelected: обновлено строк=" + updated);
                    }
                    catch (Exception ex)
                    {
                        GrdLog.Log("OnTypeParamSelected callback: EXCEPTION " + ex);
                    }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                GrdLog.Log("OnTypeParamSelected: EXCEPTION " + ex);
                MessageBox.Show("Ошибка: " + ex.Message, "ГрД: типовые параметры",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnRefreshValueParams(object sender, RoutedEventArgs e)
        {
            try { RequestTypeParamNames(); }
            catch { }
        }

        private void OnAddExact(object sender, RoutedEventArgs e)
        {
            CommitAll();
            _exactMap.Add(new MapPair());
        }

        /// <summary>
        /// Добавляет в таблицу «Сопоставление кода прибора» все типы семейства
        /// выделенного элемента (самокарта: код = имя типа, тип = имя типа).
        /// Чтение выбора выполняется через ExternalEvent (кнопка модального окна не в API-контексте).
        /// </summary>
        private void OnPickTypeFromSelection(object sender, RoutedEventArgs e)
        {
            try
            {
                var ev = RevitContext.PickEvent;
                if (ev == null || RevitContext.PickHandler == null)
                {
                    MessageBox.Show("Обработчик чтения типа не доступен.", "ГрД: чтение типа",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                GrdLog.Log("OnPickTypeFromSelection: запрос типов семейства из выделенного элемента");
                var handler = RevitContext.PickHandler;
                handler.Queue(result =>
                {
                    try
                    {
                        if (!result.Ok)
                        {
                            GrdLog.Log("OnPickTypeFromSelection: NO - " + result.Error);
                            var td = new Autodesk.Revit.UI.TaskDialog("ГрД: чтение типа семейства")
                            {
                                MainInstruction = "Не удалось прочитать семейство выделенного элемента.",
                                MainContent = result.Error ?? "Неизвестная ошибка.",
                                MainIcon = Autodesk.Revit.UI.TaskDialogIcon.TaskDialogIconWarning
                            };
                            td.Show();
                            return;
                        }

                        int added = 0, skipped = 0;
                        foreach (var typeName in result.TypeNames)
                        {
                            bool exists = _exactMap.Any(p =>
                                string.Equals(p.Key?.Trim(), typeName, StringComparison.OrdinalIgnoreCase));
                            if (exists)
                            {
                                skipped++;
                                continue;
                            }
                            _exactMap.Add(new MapPair { Key = typeName, Value = typeName });
                            added++;
                        }

                        GrdLog.Log("OnPickTypeFromSelection: семейство «" + result.FamilyName +
                                   "» добавлено=" + added + " пропущено=" + skipped);
                    }
                    catch (Exception ex)
                    {
                        GrdLog.Log("OnPickTypeFromSelection callback: EXCEPTION " + ex);
                    }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                GrdLog.Log("OnPickTypeFromSelection: EXCEPTION " + ex);
                MessageBox.Show("Ошибка: " + ex.Message, "ГрД: чтение типа",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnRemoveExact(object sender, RoutedEventArgs e) => RemoveSelected(ExactMapGrid, _exactMap);

        private void RemoveSelected(DataGrid grid, System.Collections.IList items)
        {
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            if (grid.SelectedItems.Count == 0)
            {
                MessageBox.Show("Выделите строки для удаления.", "ГрД", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Удалять с конца, т.к. SelectedItems — живая коллекция.
            var toRemove = System.Linq.Enumerable.ToList(grid.SelectedItems.Cast<object>());
            foreach (var item in toRemove) items.Remove(item);
        }

        private void CommitAll()
        {
            ValueMapGrid.CommitEdit(DataGridEditingUnit.Row, true);
            ExactMapGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            CommitAll();

            var settings = RevitContext.Settings;
            settings.ValueParamMap = _valueMap
                .Where(p => !string.IsNullOrWhiteSpace(p.Key))
                .ToDictionary(p => p.Key.Trim(), p => p.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

            settings.TypeNameExactMap = ToDict(_exactMap);

            RevitContext.SaveSettings();
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private static Dictionary<string, string> ToDict(IEnumerable<MapPair> pairs)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in pairs)
            {
                var key = p.Key?.Trim();
                if (string.IsNullOrEmpty(key)) continue;
                dict[key] = p.Value ?? string.Empty;
            }
            return dict;
        }
    }
}