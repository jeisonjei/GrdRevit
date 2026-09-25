using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using GrdRevit.Revit;

namespace GrdRevit.Ui
{
    /// <summary>
    /// Окно «Скопировать параметры»: два списка семейств (источник и цель) с поиском и
    /// взаимоисключающей галочкой «выбранные/спецификация», и две таблицы параметров.
    /// Слева параметры источника с колонкой-чекбоксом (что копировать), справа —
    /// параметры целевого семейства (формулы подсвечены, есть кнопка «снять формулу»).
    /// Моделесс-окно не может обращаться к API — запросы идут через ExternalEvent.
    /// </summary>
    public partial class CopyParamsWindow : Window
    {
        public ObservableCollection<string> SourceFamilies { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> TargetFamilies { get; } = new ObservableCollection<string>();
        public ObservableCollection<CopyParamItem> SourceParams { get; } = new ObservableCollection<CopyParamItem>();
        public ObservableCollection<CopyParamItem> TargetParams { get; } = new ObservableCollection<CopyParamItem>();

        private readonly List<CopyParamItem> _sourceMaster = new List<CopyParamItem>();
        private readonly List<CopyParamItem> _targetMaster = new List<CopyParamItem>();
        // Списки семейств независимы: у источника и цели свои данные и своя галочка
        // «только выбранные/спецификация».
        private List<string> _sourceFamiliesMaster = new List<string>();
        private List<string> _targetFamiliesMaster = new List<string>();
        private int _sourceFamilyCount;
        private int _targetFamilyCount;
        private string _sourceScope = string.Empty;
        private string _targetScope = string.Empty;

        private string _lastSource;
        private string _lastTarget;
        private bool _loadingSourceFamilies;
        private bool _loadingTargetFamilies;
        private bool _suppressSel;

        public CopyParamsWindow()
        {
            InitializeComponent();
            WindowTopmost.Track(this);
            DataContext = this;
            Loaded += (s, e) =>
            {
                LoadFamilies(source: true, selectedOnly: SourceScopeBox.IsChecked == true);
                LoadFamilies(source: false, selectedOnly: TargetScopeBox.IsChecked == true);
            };
        }

        // ------------------------------------------------------------- Семейства

        private void LoadFamilies(bool source, bool selectedOnly)
        {
            var handler = RevitContext.FamilyInfoHandler;
            var ev = RevitContext.FamilyInfoEvent;
            if (handler == null || ev == null)
            {
                ApplyStatus.Text = "Обработчик списка семейств недоступен.";
                return;
            }
            if (source ? _loadingSourceFamilies : _loadingTargetFamilies) return;
            if (source) _loadingSourceFamilies = true; else _loadingTargetFamilies = true;
            try
            {
                handler.Queue(null, string.Empty, res =>
                {
                    try
                    {
                        var master = (res.Families ?? new List<FamilyInfoItem>())
                            .Select(f => f.Name)
                            .Where(n => !string.IsNullOrEmpty(n))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                            .ToList();
                        if (source)
                        {
                            _sourceFamiliesMaster = master;
                            _sourceFamilyCount = master.Count;
                            _sourceScope = res.Scope ?? string.Empty;
                        }
                        else
                        {
                            _targetFamiliesMaster = master;
                            _targetFamilyCount = master.Count;
                            _targetScope = res.Scope ?? string.Empty;
                        }
                        RebuildFamilies(source);
                        UpdateFamilyCounts();
                    }
                    finally
                    {
                        if (source) _loadingSourceFamilies = false; else _loadingTargetFamilies = false;
                    }
                }, selectedOnly);
                ev.Raise();
            }
            catch (Exception ex)
            {
                if (source) _loadingSourceFamilies = false; else _loadingTargetFamilies = false;
                GrdLog.Log("CopyParamsWindow.LoadFamilies EXCEPTION: " + ex);
            }
        }

        private void RebuildFamilies(bool source)
        {
            var master = source ? _sourceFamiliesMaster : _targetFamiliesMaster;
            var view = source ? SourceFamilies : TargetFamilies;
            var list = source ? SourceFamList : TargetFamList;
            string filter = ((source ? SourceFamFilter.Text : TargetFamFilter.Text) ?? string.Empty).Trim();
            string prev = source ? _lastSource : _lastTarget;

            _suppressSel = true;
            try
            {
                view.Clear();
                foreach (var name in master)
                    if (filter.Length == 0 || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        view.Add(name);
                if (!string.IsNullOrEmpty(prev) && view.Contains(prev)) list.SelectedItem = prev;
            }
            finally
            {
                _suppressSel = false;
            }
        }

        private void UpdateFamilyCounts()
        {
            ApplyCount.Text = "источник: " + _sourceFamilyCount + ScopeSuffix(_sourceScope) +
                              " · цель: " + _targetFamilyCount + ScopeSuffix(_targetScope);
        }

        private static string ScopeSuffix(string scope) =>
            string.IsNullOrEmpty(scope) ? string.Empty : " («" + scope + "»)";

        private void OnSourceScopeChanged(object sender, RoutedEventArgs e)
        {
            LoadFamilies(source: true, selectedOnly: SourceScopeBox.IsChecked == true);
        }

        private void OnTargetScopeChanged(object sender, RoutedEventArgs e)
        {
            LoadFamilies(source: false, selectedOnly: TargetScopeBox.IsChecked == true);
        }

        private void OnRefreshFamilies(object sender, RoutedEventArgs e)
        {
            LoadFamilies(source: true, selectedOnly: SourceScopeBox.IsChecked == true);
            LoadFamilies(source: false, selectedOnly: TargetScopeBox.IsChecked == true);
            // Перечитать параметры/типы текущего выбора: окно «запоминает» последнее
            // семейство и без повторного выбора не перечитывало бы его данные.
            if (!string.IsNullOrEmpty(_lastSource)) LoadParams(_lastSource, source: true);
            if (!string.IsNullOrEmpty(_lastTarget)) LoadParams(_lastTarget, source: false);
        }

        private void OnSourceFamFilterChanged(object sender, TextChangedEventArgs e) { RebuildFamilies(true); }
        private void OnTargetFamFilterChanged(object sender, TextChangedEventArgs e) { RebuildFamilies(false); }

        private void OnSourceFamFilterReset(object sender, RoutedEventArgs e) { SourceFamFilter.Text = string.Empty; }
        private void OnTargetFamFilterReset(object sender, RoutedEventArgs e) { TargetFamFilter.Text = string.Empty; }

        private void OnSourceFamSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSel) return;
            var name = SourceFamList.SelectedItem as string;
            if (string.Equals(name, _lastSource, StringComparison.Ordinal)) return;
            _lastSource = name;
            LoadParams(name, source: true);
        }

        private void OnTargetFamSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSel) return;
            var name = TargetFamList.SelectedItem as string;
            if (string.Equals(name, _lastTarget, StringComparison.Ordinal)) return;
            _lastTarget = name;
            LoadParams(name, source: false);
        }

        // ------------------------------------------------------------- Параметры

        private void LoadParams(string familyName, bool source)
        {
            var master = source ? _sourceMaster : _targetMaster;
            var view = source ? SourceParams : TargetParams;
            if (string.IsNullOrEmpty(familyName))
            {
                master.Clear();
                view.Clear();
                if (source) SrcCount.Text = string.Empty;
                return;
            }

            var handler = RevitContext.CopyParamsHandler;
            var ev = RevitContext.CopyParamsEvent;
            if (handler == null || ev == null)
            {
                ApplyStatus.Text = "Обработчик копирования недоступен.";
                return;
            }
            try
            {
                // Галочки «копировать» переносим из предыдущего чтения (по имени параметра).
                var prevChecked = source
                    ? new HashSet<string>(_sourceMaster.Where(p => p.IsSelected).Select(p => p.Name), StringComparer.OrdinalIgnoreCase)
                    : null;

                handler.QueueRead(familyName, res =>
                {
                    try
                    {
                        master.Clear();
                        if (!string.IsNullOrEmpty(res.Error))
                        {
                            view.Clear();
                            ApplyStatus.Text = res.Error;
                            return;
                        }
                        foreach (var p in res.Params)
                        {
                            if (prevChecked != null && !prevChecked.Contains(p.Name)) p.IsSelected = false;
                            master.Add(p);
                        }
                        RebuildParams(source);
                        if (source)
                        {
                            PopulateTypes(SourceTypeCombo, res.Types);
                            ApplyDisplayValues(true);
                            SrcCount.Text = SelectedCountText();
                        }
                        else
                        {
                            PopulateTypes(TargetTypeCombo, res.Types);
                            ApplyDisplayValues(false);
                            ApplyStatus.Text = "Параметров у «" + familyName + "»: " + master.Count;
                        }
                    }
                    catch (Exception ex)
                    {
                        GrdLog.Log("CopyParamsWindow.LoadParams callback EXCEPTION: " + ex);
                    }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                GrdLog.Log("CopyParamsWindow.LoadParams EXCEPTION: " + ex);
            }
        }

        private void RebuildParams(bool source)
        {
            var master = source ? _sourceMaster : _targetMaster;
            var view = source ? SourceParams : TargetParams;
            string filter = (source ? SourceParamFilter.Text : TargetParamFilter.Text) ?? string.Empty;
            filter = filter.Trim();
            view.Clear();
            foreach (var p in master)
                if (filter.Length == 0 || p.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    view.Add(p);
        }

        private void OnSourceParamFilterChanged(object sender, TextChangedEventArgs e) { RebuildParams(true); }
        private void OnTargetParamFilterChanged(object sender, TextChangedEventArgs e) { RebuildParams(false); }
        private void OnSourceParamFilterReset(object sender, RoutedEventArgs e) { SourceParamFilter.Text = string.Empty; }
        private void OnTargetParamFilterReset(object sender, RoutedEventArgs e) { TargetParamFilter.Text = string.Empty; }

        private void OnSelectAllParams(object sender, RoutedEventArgs e) { SetAllSelected(SourceParams, true); }
        private void OnSelectNoneParams(object sender, RoutedEventArgs e) { SetAllSelected(SourceParams, false); }

        private void SetAllSelected(IEnumerable<CopyParamItem> items, bool value)
        {
            foreach (var p in items) p.IsSelected = value;
            SrcCount.Text = SelectedCountText();
        }

        private void OnParamChecked(object sender, RoutedEventArgs e)
        {
            // Клик по галочке: чекбокс уже переключился — обновляем счётчик.
            Dispatcher.BeginInvoke(new Action(() => SrcCount.Text = SelectedCountText()),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private string SelectedCountText()
        {
            int total = _sourceMaster.Count;
            int sel = _sourceMaster.Count(p => p.IsSelected);
            return "отмечено: " + sel + " из " + total;
        }

        private const string AllTypesItem = "— все типы —";

        /// <summary>Наполняет выпадающий тип семейства; «все типы» — первым.</summary>
        private static void PopulateTypes(ComboBox combo, List<string> types)
        {
            var items = new List<string> { AllTypesItem };
            if (types != null)
                items.AddRange(types
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
            combo.ItemsSource = items;
            if (items.Count > 0) combo.SelectedIndex = 0;
        }

        /// <summary>Выбранный тип: пустая строка — «все типы».</summary>
        private static string SelectedType(ComboBox combo)
        {
            var s = combo.SelectedItem as string;
            return string.IsNullOrEmpty(s) || string.Equals(s, AllTypesItem, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : s;
        }

        /// <summary>Пересчитывает колонку «Значение» по выбранному типу: при конкретном
        /// типе — его значение; при «все типы» — свёртка (одно значение / «несколько»).</summary>
        private void ApplyDisplayValues(bool source)
        {
            string typeName = source ? SelectedType(SourceTypeCombo) : SelectedType(TargetTypeCombo);
            var master = source ? _sourceMaster : _targetMaster;
            foreach (var p in master)
            {
                string show;
                if (string.IsNullOrEmpty(typeName))
                {
                    var vals = p.TypeValues
                        .Where(t => !string.IsNullOrEmpty(t.Value))
                        .Select(t => t.Value)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    show = vals.Count == 1 ? vals[0] : (vals.Count > 1 ? "⟨несколько значений⟩" : string.Empty);
                }
                else
                {
                    var tv = p.TypeValues.FirstOrDefault(t =>
                        string.Equals(t.TypeName, typeName, StringComparison.OrdinalIgnoreCase));
                    show = tv?.Value ?? string.Empty;
                }
                p.SetDisplayValue(show);
            }
        }

        private void OnSourceTypeChanged(object sender, SelectionChangedEventArgs e) { ApplyDisplayValues(true); }
        private void OnTargetTypeChanged(object sender, SelectionChangedEventArgs e) { ApplyDisplayValues(false); }

        private string TypeCopyDescription()
        {
            var src = SelectedType(SourceTypeCombo);
            var tgt = SelectedType(TargetTypeCombo);
            if (string.IsNullOrEmpty(src) && string.IsNullOrEmpty(tgt))
                return "по совпадающим именам типов";
            if (!string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(tgt))
                return "из типа «" + src + "» в тип «" + tgt + "»";
            return string.IsNullOrEmpty(src)
                ? "в тип «" + tgt + "» из соответствующих по имени"
                : "из типа «" + src + "» в соответствующие по имени";
        }

        // ------------------------------------------------------------- Действия

        private void OnClearFormulaClick(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.DataContext is CopyParamItem row)) return;
            if (string.IsNullOrEmpty(_lastTarget))
            {
                MessageBox.Show("Сначала выберите целевое семейство.", "Скопировать параметры",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(
                "Будет отредактировано само семейство «" + _lastTarget + "»: у параметра «" + row.Name +
                "» формула уберётся у всех типов, после чего семейство перезагрузится в проект.\n\n" +
                "Это затронет ВСЕ экземпляры этого семейства. Продолжить?",
                "Снять формулу", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            var handler = RevitContext.CopyParamsHandler;
            var ev = RevitContext.CopyParamsEvent;
            if (handler == null || ev == null) return;

            ApplyStatus.Text = "Снятие формулы: «" + row.Name + "»…";
            try
            {
                handler.QueueClearFormula(_lastTarget, row.Name, res =>
                {
                    try
                    {
                        if (res.Errors.Count > 0)
                        {
                            MessageBox.Show(string.Join(Environment.NewLine, res.Errors),
                                "Снять формулу", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                        else
                        {
                            SnackBar.Show("Формула снята: «" + row.Name + "».", SnackBarKind.Success);
                        }
                        ApplyStatus.Text = string.Empty;
                        LoadParams(_lastTarget, source: false);
                    }
                    catch (Exception ex)
                    {
                        GrdLog.Log("CopyParamsWindow.OnClearFormulaClick callback EXCEPTION: " + ex);
                    }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                GrdLog.Log("CopyParamsWindow.OnClearFormulaClick EXCEPTION: " + ex);
            }
        }

        private void OnApply(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastSource) || string.IsNullOrEmpty(_lastTarget))
            {
                MessageBox.Show("Выберите семейство-источник и целевое семейство.",
                    "Скопировать параметры", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var items = _sourceMaster.Where(p => p.IsSelected).ToList();
            if (items.Count == 0)
            {
                MessageBox.Show("Отметьте галками параметры, значения которых нужно скопировать.",
                    "Скопировать параметры", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(
                "Скопировать " + items.Count + " параметр(ов) из «" + _lastSource + "» в «" + _lastTarget + "»?\n\n" +
                "Отсутствующие параметры будут добавлены с такой же привязкой (экземпляр/тип), при иной привязке — заменены. " +
                "Значения типовых параметров копируются " + TypeCopyDescription() + ". " +
                "Затронет все экземпляры целевого семейства в проекте.",
                "Скопировать параметры", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            var handler = RevitContext.CopyParamsHandler;
            var ev = RevitContext.CopyParamsEvent;
            if (handler == null || ev == null)
            {
                MessageBox.Show("Обработчик копирования недоступен.", "Скопировать параметры",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            ApplyButton.IsEnabled = false;
            ApplyStatus.Text = "Копирование «" + _lastSource + "» → «" + _lastTarget + "»…";
            try
            {
                handler.QueueApply(_lastSource, _lastTarget,
                    SelectedType(SourceTypeCombo), SelectedType(TargetTypeCombo), items, res =>
                {
                    try
                    {
                        ApplyStatus.Text = string.Empty;
                        var text = string.Join(Environment.NewLine, res.Summary);
                        if (res.Errors.Count > 0)
                        {
                            if (text.Length > 0) text += Environment.NewLine + Environment.NewLine;
                            text += "Не удалось:" + Environment.NewLine + string.Join(Environment.NewLine, res.Errors);
                            MessageBox.Show(text, "Скопировать параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                        else
                        {
                            SnackBar.Show("Скопировано значений: " + res.CopyCount + ".", SnackBarKind.Success);
                            if (text.Length > 0) ApplyStatus.Text = text;
                        }
                        LoadParams(_lastTarget, source: false);
                    }
                    catch (Exception ex)
                    {
                        GrdLog.Log("CopyParamsWindow.OnApply callback EXCEPTION: " + ex);
                    }
                    finally
                    {
                        ApplyButton.IsEnabled = true;
                    }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                ApplyButton.IsEnabled = true;
                ApplyStatus.Text = "Ошибка: " + ex.Message;
                GrdLog.Log("CopyParamsWindow.OnApply EXCEPTION: " + ex);
            }
        }
    }
}