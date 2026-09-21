using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GrdRevit.Revit;

namespace GrdRevit.Ui
{
    /// <summary>Строка параметра выбранных экземпляров в таблице окна.</summary>
    public class ElementParamRow : ObservableObject
    {
        public string Name { get; }
        public string Group { get; }
        public string StorageType { get; }
        public bool IsInstance { get; }
        public string BindingText { get; }
        public bool IsReadOnly { get; }
        public bool IsEditable => !IsReadOnly;
        public string OriginalValue { get; }
        public bool IsDiverse { get; }
        public int MatchCount { get; }
        private readonly bool _allowClearFormula;

        private string _value;
        public string Value
        {
            get => _value;
            set => Set(ref _value, value);
        }

        /// <summary>Отображение в ячейке: для строк с разными значениями у разных типов
        /// показывается маркер, а не первое попавшееся число.</summary>
        public string DisplayValue => IsDiverse ? "⟨несколько значений⟩" : Value;

        public ElementParamRow(ElementParamValue p, bool allowClearFormula)
        {
            Name = p?.Name ?? string.Empty;
            Group = string.IsNullOrEmpty(p?.Group) ? "—" : p.Group;
            StorageType = string.IsNullOrEmpty(p?.StorageType) ? "—" : p.StorageType;
            IsInstance = p?.IsInstance ?? true;
            BindingText = IsInstance ? "экземпляр" : "тип";
            IsReadOnly = p?.IsReadOnly ?? true;
            IsDiverse = p?.Diverse ?? false;
            MatchCount = p?.MatchCount ?? 1;
            OriginalValue = p?.Value ?? string.Empty;
            _value = OriginalValue;
            _allowClearFormula = allowClearFormula;
        }

        /// <summary>Строка изменена, если пользователь ввёл другое значение. Для строк с
        /// «несколькими значениями» (IsDiverse) изменение — только явный ввод текста;
        /// пустая ячейка оставляется как есть (без перезаписи всех типов пустым значением).</summary>
        public bool Changed => IsDiverse
            ? Value.Length > 0
            : !string.Equals(Value, OriginalValue, StringComparison.Ordinal);

        /// <summary>Кнопка «снять формулу»: у любого параметра только для чтения и при
        /// наличии в выборке хотя бы одного элемента (при нескольких — формула снимается
        /// во всех их семействах). Формула в семействе всегда на параметре ТИПА, но
        /// значение в проекте может блокироваться и у строки «экземпляр».</summary>
        public bool ShowClearFormula => !IsEditable && _allowClearFormula;
    }

    /// <summary>Моделесс-окно: редактирование значений всех параметров выбранных
    /// экземпляров семейств без открытия редактора семейства. Можно выбрать несколько
    /// экземпляров разных типов — значения применяются к параметрам всех выбранных
    /// элементов по имени параметра.</summary>
    public partial class InstanceParamsWindow : Window
    {
        public ObservableCollection<ElementParamRow> Rows { get; } = new ObservableCollection<ElementParamRow>();

        /// <summary>Галочка «Переносить строки ячеек»: при включении длинные значения
        /// переносятся на несколько строк, а высота строк таблицы растёт автоматически.</summary>
        public static readonly DependencyProperty WrapCellsProperty =
            DependencyProperty.Register(nameof(WrapCells), typeof(bool), typeof(InstanceParamsWindow),
                new PropertyMetadata(false, OnWrapCellsChanged));

        public bool WrapCells
        {
            get => (bool)GetValue(WrapCellsProperty);
            set => SetValue(WrapCellsProperty, value);
        }

        private static void OnWrapCellsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var w = d as InstanceParamsWindow;
            if (w?.ParamsGrid == null) return;
            bool wrap = (bool)e.NewValue;
            w.ParamsGrid.MinRowHeight = 30;
            w.ParamsGrid.RowHeight = wrap ? double.NaN : 30;
        }

        private readonly List<ElementId> _elementIds = new List<ElementId>();
        private bool _busy;

        public InstanceParamsWindow(List<ElementId> elementIds)
        {
            _elementIds.AddRange(elementIds ?? new List<ElementId>());
            InitializeComponent();
            WindowTopmost.Track(this);
            DataContext = this;
            Loaded += (s, e) =>
            {
                // UX: фокус сразу в поле поиска
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    FilterBox.Focus();
                    FilterBox.SelectAll();
                }), System.Windows.Threading.DispatcherPriority.Input);
                LoadParams();
            };
        }

        /// <summary>Новая выборка в Revit при уже открытом окне: перечитать параметры.</summary>
        public void SetSelection(IList<ElementId> elementIds)
        {
            _elementIds.Clear();
            if (elementIds != null) _elementIds.AddRange(elementIds);
            LoadParams();
            try { Activate(); } catch { }
        }

        /// <summary>F2, двойной щелчок или клик по ячейке «Значение»: выделить весь текст.
        /// ВАЖНО: каретку выставляют ПОСЛЕ вызова этого события (DataGrid и сам TextBox),
        /// поэтому «в лоб» выделить не выходит — дублируем через TextBox-обработчики
        /// (GotKeyboardFocus, перехват клика) и отложенное выделение на низком приоритете.</summary>
        private void OnPreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
        {
            if (!(e.EditingElement is System.Windows.Controls.TextBox tb)) return;
            Dispatcher.BeginInvoke(new Action(() => TrySelectAll(tb)),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>Фокус на редактор (F2, клик, двойной щелчок): выделить всё.</summary>
        private void OnEditBoxGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            TrySelectAll(sender as System.Windows.Controls.TextBox);
        }

        /// <summary>Клик по редактируемой ячейке: не даём TextBox поставить каретку
        /// в точку клика (она «смазала» бы выделение), а выделяем весь текст.</summary>
        private void OnEditBoxPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var tb = sender as System.Windows.Controls.TextBox;
            if (tb == null) return;
            e.Handled = true;
            TrySelectAll(tb);
        }

        /// <summary>Редактор создан: дополнительная подстраховка — выделить весь текст
        /// на следующем проходе, когда применится привязка.</summary>
        private void OnEditBoxLoaded(object sender, RoutedEventArgs e)
        {
            var tb = sender as System.Windows.Controls.TextBox;
            if (tb == null) return;
            Dispatcher.BeginInvoke(new Action(() => TrySelectAll(tb)),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private static void TrySelectAll(System.Windows.Controls.TextBox tb)
        {
            if (tb == null) return;
            if (!tb.IsKeyboardFocusWithin) tb.Focus();
            tb.SelectAll();
        }

        private void OnClearFormulaClick(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.DataContext is ElementParamRow row)) return;

            var answer = MessageBox.Show(
                "Будут отредактированы сами СЕМЕЙСТВА (как через «Редактировать семейство»): " +
                "у параметра «" + row.Name + "» формула уберётся у всех типов каждого семейства " +
                "выбранных экземпляров, после чего семейства перезагрузятся в проект.\n\n" +
                "Это затронет ВСЕ экземпляры этих семейств в проекте. Продолжить?",
                "Снять формулу", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            var handler = RevitContext.InstanceParamsHandler;
            var ev = RevitContext.InstanceParamsEvent;
            if (handler == null || ev == null)
            {
                SnackBar.Show("Обработчик параметров недоступен.", SnackBarKind.Error);
                return;
            }

            StatusText.Text = "Снятие формулы: «" + row.Name + "»…";
            try
            {
                handler.QueueClearFormula(new List<ElementId>(_elementIds), row.Name, row.IsInstance, result =>
                {
                    try
                    {
                        if (result.Applied > 0)
                        {
                            SnackBar.Show("Формула снята: «" + row.Name + "» теперь редактируется" +
                                (result.Applied > 1 ? " (семейств: " + result.Applied + ")" : string.Empty) + ".", SnackBarKind.Success);
                        }
                        else if (result.Errors.Count > 0)
                        {
                            var td = new TaskDialog("Снять формулу")
                            {
                                MainInstruction = "Формулу снять не удалось.",
                                MainContent = string.Join(Environment.NewLine, result.Errors),
                                MainIcon = TaskDialogIcon.TaskDialogIconWarning
                            };
                            td.Show();
                        }
                        LoadParams();
                    }
                    catch (Exception ex)
                    {
                        GrdLog.Log("InstanceParamsWindow.OnClearFormulaClick callback: EXCEPTION " + ex);
                    }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Ошибка: " + ex.Message;
                GrdLog.Log("InstanceParamsWindow.OnClearFormulaClick: EXCEPTION " + ex);
            }
        }

        /// <summary>Загружает параметры экземпляра и типа через ExternalEvent-обработчик.</summary>
        private void LoadParams()
        {
            if (_busy) return;
            var handler = RevitContext.InstanceParamsHandler;
            var ev = RevitContext.InstanceParamsEvent;
            if (handler == null || ev == null)
            {
                StatusText.Text = "Обработчик параметров недоступен.";
                return;
            }

            _busy = true;
            StatusText.Text = "Загрузка…";
            try
            {
                handler.QueueRead(_elementIds, result =>
                {
                    try
                    {
                        Rows.Clear();
                        if (!string.IsNullOrEmpty(result.Error))
                        {
                            ElementInfo.Text = result.Error;
                            CountText.Text = string.Empty;
                            return;
                        }

                        bool allowClearFormula = result.Elements.Count > 0;
                        foreach (var p in result.Params)
                            Rows.Add(new ElementParamRow(p, allowClearFormula));

                        ElementInfo.Text = result.ElementName;
                        int total = Math.Max(1, result.Elements.Count);
                        int incomplete = Rows.Count(r => r.MatchCount < total);
                        if (incomplete > 0)
                            ElementInfo.Text += Environment.NewLine + incomplete + " параметров есть не у всех выбранных видов/типов — они применяются только к тем экземплярам, у которых такой параметр есть.";

                        int editable = Rows.Count(r => r.IsEditable);
                        int instance = Rows.Count(r => r.IsInstance);
                        CountText.Text = "параметров: " + Rows.Count +
                            " (экземпляр: " + instance + ", тип: " + (Rows.Count - instance) +
                            ", изменяемых: " + editable + ")";
                        Title = "Параметры экземпляра — " + result.ElementName;
                        StatusText.Text = editable == 0
                            ? "Изменяемых параметров нет — все значения только для чтения."
                            : "Двойной щелчок по ячейке «Значение» открывает её для редактирования.";

                        UpdateChangedCount();
                        GrdLog.Log("InstanceParamsWindow: загружено " + Rows.Count + " параметров '" + result.ElementName + "'");
                    }
                    catch (Exception ex)
                    {
                        GrdLog.Log("InstanceParamsWindow.LoadParams callback: EXCEPTION " + ex);
                    }
                    finally
                    {
                        _busy = false;
                    }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                _busy = false;
                GrdLog.Log("InstanceParamsWindow.LoadParams: EXCEPTION " + ex);
            }
        }

        private void OnRefresh(object sender, RoutedEventArgs e)
        {
            LoadParams();
        }

        private void OnFilterChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void OnFilterReset(object sender, RoutedEventArgs e)
        {
            FilterBox.Text = string.Empty;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var view = CollectionViewSource.GetDefaultView(Rows);
            if (view == null) return;
            var filter = FilterBox.Text?.Trim();
            if (string.IsNullOrEmpty(filter))
            {
                view.Filter = null;
            }
            else
            {
                view.Filter = o => o is ElementParamRow r &&
                    r.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            UpdateChangedCount();
        }

        private void UpdateChangedCount()
        {
            try
            {
                int changed = Rows.Count(r => r.Changed);
                CountText.Text = (CountText.Text ?? string.Empty) +
                    (changed > 0 ? "  |  изменено: " + changed : string.Empty);
            }
            catch { }
        }

        private void OnApply(object sender, RoutedEventArgs e)
        {
            var edits = new List<InstanceParamEdit>();
            foreach (var r in Rows)
            {
                if (!r.IsEditable || !r.Changed) continue;
                edits.Add(new InstanceParamEdit
                {
                    Name = r.Name,
                    IsInstance = r.IsInstance,
                    NewValue = r.Value ?? string.Empty
                });
            }

            foreach (var r in Rows)
            {
                bool pass = r.IsEditable && r.Changed;
                if (!pass) continue;
                GrdLog.Log("InstanceParamsWindow.OnApply: собрано «" + r.Name + "» isInstance=" +
                           r.IsInstance + " ed=" + r.IsEditable + " changed=" + r.Changed +
                           " value=\"" + (r.Value ?? string.Empty) + "\" orig=\"" + (r.OriginalValue ?? string.Empty) + "\"");
            }

            if (edits.Count == 0)
            {
                MessageBox.Show("Нет изменений для применения.",
                    "Параметры экземпляра", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var handler = RevitContext.InstanceParamsHandler;
            var ev = RevitContext.InstanceParamsEvent;
            if (handler == null || ev == null)
            {
                MessageBox.Show("Обработчик параметров недоступен.",
                    "Параметры экземпляра", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            bool typeChanged = edits.Any(x => !x.IsInstance);
            if (typeChanged)
            {
                var answer = MessageBox.Show(
                    "Среди изменений есть параметры ТИПА. Они применяются ко ВСЕМ экземплярам " +
                    "соответствующих типов в проекте, а не только к выбранным элементам.\n\nПродолжить?",
                    "Параметры экземпляра", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.OK) return;
            }

            GrdLog.Log("InstanceParamsWindow.OnApply: изменений=" + edits.Count);
            StatusText.Text = "Применение…";
            try
            {
                handler.QueueApply(_elementIds, edits, result =>
                {
                    try
                    {
                        string msg = "Применено " + result.Applied + " из " + result.Total + ".";
                        if (result.Errors.Count > 0)
                        {
                            var td = new TaskDialog("Параметры экземпляра")
                            {
                                MainInstruction = "Часть изменений не применена.",
                                MainContent = msg + Environment.NewLine + Environment.NewLine +
                                              string.Join(Environment.NewLine, result.Errors),
                                MainIcon = TaskDialogIcon.TaskDialogIconWarning
                            };
                            td.Show();
                        }
                        else
                        {
                            SnackBar.Show(msg, SnackBarKind.Success);
                        }

                        GrdLog.Log("InstanceParamsWindow.OnApply результат: " + msg +
                                   (result.Errors.Count > 0 ? " errors=" + result.Errors.Count : ""));
                        LoadParams();
                    }
                    catch (Exception ex)
                    {
                        GrdLog.Log("InstanceParamsWindow.OnApply callback: EXCEPTION " + ex);
                    }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Ошибка: " + ex.Message;
                GrdLog.Log("InstanceParamsWindow.OnApply: EXCEPTION " + ex);
            }
        }
    }
}