using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GrdRevit.Revit;

namespace GrdRevit.Ui
{
    /// <summary>Строка вида в таблице «Управляющий видами».</summary>
    public class ViewsRow : ObservableObject
    {
        public long Id { get; }
        public string KindKey { get; }
        public string KindText { get; }
        public string Scale { get; }

        private string _name;
        public string Name
        {
            get => _name;
            set => Set(ref _name, value);
        }

        private string _sheetNumber;
        public string SheetNumber
        {
            get => _sheetNumber;
            set => Set(ref _sheetNumber, value);
        }

        private bool _marked;
        public bool Marked
        {
            get => _marked;
            set => Set(ref _marked, value);
        }

        public ViewsRow(ViewInfoItem v)
        {
            Id = v.Id;
            KindKey = v.KindKey;
            KindText = v.KindText;
            Scale = v.Scale;
            _name = v.Name;
            _sheetNumber = v.SheetNumber;
        }
    }

    /// <summary>Моделесс-окно «Управляющий видами»: поиск, фильтры (разрезы/планы/3D),
    /// переименование (F2), дублирование и переход к виду.</summary>
    public partial class ViewsManagerWindow : Window
    {
        private readonly List<ViewsRow> _all = new List<ViewsRow>();
        private long _activeTemplateId;
        private string _activeTemplateName = string.Empty;
        private bool _busy;

        public ObservableCollection<ViewsRow> Rows { get; } = new ObservableCollection<ViewsRow>();

        public ViewsManagerWindow()
        {
            InitializeComponent();
            DataContext = Rows;
            Loaded += (s, e) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    FilterBox.Focus();
                    FilterBox.SelectAll();
                }), System.Windows.Threading.DispatcherPriority.Input);
                Reload();
            };
        }

        private ViewsManagerHandler Handler()
        {
            var handler = RevitContext.ViewsManagerHandler;
            var ev = RevitContext.ViewsManagerEvent;
            if (handler == null || ev == null)
            {
                SnackBar.Show("Обработчик видов недоступен.", SnackBarKind.Error);
                return null;
            }
            return handler;
        }

        private void Raise()
        {
            try { RevitContext.ViewsManagerEvent?.Raise(); }
            catch (Exception ex) { GrdLog.Log("ViewsManagerWindow.Raise EXCEPTION: " + ex); }
        }

        private void Reload()
        {
            var handler = Handler();
            if (handler == null) return;
            if (_busy) return;
            _busy = true;
            StatusText.Text = "Загрузка списка видов…";
            try
            {
                handler.QueueRead(result =>
                {
                    _busy = false;
                    try
                    {
                        if (!string.IsNullOrEmpty(result.Error))
                        {
                            SnackBar.Show(result.Error, SnackBarKind.Error);
                            StatusText.Text = result.Error;
                            return;
                        }
                        ApplyViews(result.Views);
                    }
                    catch (Exception ex)
                    {
                        GrdLog.Log("ViewsManagerWindow.QueueRead callback EXCEPTION: " + ex);
                    }
                });
                Raise();
            }
            catch (Exception ex)
            {
                _busy = false;
                StatusText.Text = "Ошибка: " + ex.Message;
                GrdLog.Log("ViewsManagerWindow.Reload EXCEPTION: " + ex);
            }
        }

        private void ApplyViews(List<ViewInfoItem> views)
        {
            _all.Clear();
            foreach (var v in views)
                _all.Add(new ViewsRow(v));
            InfoText.Text = "Виды активного документа: " + _all.Count + " (с учётом фильтров).";
            ApplyFilter();
            StatusText.Text = "Готово.";
            GrdLog.Log("ViewsManagerWindow: строк=" + _all.Count);
        }

        private void ApplyFilter()
        {
            if (FilterBox == null || ChkSection == null || ChkPlan == null || Chk3d == null || ChkSheet == null || ChkSchedule == null || RbStartsWith == null || RbContains == null) return;
            var search = (FilterBox.Text ?? string.Empty).Trim();
            bool showSection = ChkSection.IsChecked == true;
            bool showPlan = ChkPlan.IsChecked == true;
            bool show3d = Chk3d.IsChecked == true;
            bool showSheet = ChkSheet.IsChecked == true;
            bool showSchedule = ChkSchedule.IsChecked == true;
            bool startsWith = RbStartsWith.IsChecked == true;

            if (SheetNumberCol != null)
                SheetNumberCol.Visibility = showSheet && !showSection && !showPlan && !show3d && !showSchedule
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            if (Rows == null) return;

            Rows.Clear();
            foreach (var r in _all)
            {
                bool kindOk = (showSection && r.KindKey == "section")
                              || (showPlan && r.KindKey == "plan")
                              || (show3d && r.KindKey == "3d")
                              || (showSheet && r.KindKey == "sheet")
                              || (showSchedule && r.KindKey == "schedule")
                              || (showSection && showPlan && show3d && showSheet && showSchedule && r.KindKey == "other");
                bool nameOk = search.Length == 0;
                if (!nameOk)
                {
                    nameOk = startsWith
                        ? r.Name.StartsWith(search, StringComparison.CurrentCultureIgnoreCase)
                        : r.Name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0;
                    if (!nameOk)
                        nameOk = r.KindText.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0;
                }
                if (kindOk && nameOk)
                    Rows.Add(r);
            }
            if (CountText != null)
                CountText.Text = "Показано: " + Rows.Count + " из " + _all.Count;
        }

        private void OnFilterChanged(object sender, RoutedEventArgs e)
        {
            ApplyFilter();
        }

        private void OnMatchModeChanged(object sender, RoutedEventArgs e)
        {
            ApplyFilter();
        }

        /// <summary>Пресет фильтра типов: одним кликом показать только нужный тип
        /// либо «Всё» сразу. Редкие комбинации — пресет «Вручную…», он просто
        /// показывает чекбоксы (ManualFiltersPanel) с текущим состоянием.</summary>
        private void OnPresetChanged(object sender, RoutedEventArgs e)
        {
            if (!(sender is RadioButton rb) || rb.IsChecked != true) return;
            ApplyPreset(rb);
        }

        private void ApplyPreset(RadioButton rb)
        {
            // null-guard: Checked пресетов срабатывает ещё во время InitializeComponent.
            if (FilterBox == null || ChkSection == null || ChkPlan == null || Chk3d == null ||
                ChkSheet == null || ChkSchedule == null || RbManual == null) return;
            if (rb == RbAll) SetTypeChecks(true, true, true, true, true);
            else if (rb == RbPlans) SetTypeChecks(false, true, false, false, false);
            else if (rb == RbSections) SetTypeChecks(true, false, false, false, false);
            else if (rb == Rb3d) SetTypeChecks(false, false, true, false, false);
            else if (rb == RbSheets) SetTypeChecks(false, false, false, true, false);
            else if (rb == RbSchedules) SetTypeChecks(false, false, false, false, true);

            if (ManualFiltersPanel != null)
                ManualFiltersPanel.Visibility = rb == RbManual ? Visibility.Visible : Visibility.Collapsed;
            ApplyFilter();
        }

        private void SetTypeChecks(bool section, bool plan, bool d3, bool sheet, bool schedule)
        {
            ChkSection.IsChecked = section;
            ChkPlan.IsChecked = plan;
            Chk3d.IsChecked = d3;
            ChkSheet.IsChecked = sheet;
            ChkSchedule.IsChecked = schedule;
        }

        private void OnFilterReset(object sender, RoutedEventArgs e)
        {
            FilterBox.Clear();
            FilterBox.Focus();
            ApplyFilter();
        }

        private void OnRefresh(object sender, RoutedEventArgs e)
        {
            Reload();
        }

        // ---- Переименование (F2 / двойной щелчок / редактирование ячейки) ----

        private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ViewsGrid.CurrentCell.Column == null) return;
            var header = ViewsGrid.CurrentCell.Column.Header?.ToString();
            if (header == "Имя вида" || header == "Номер листа")
                ViewsGrid.BeginEdit();
        }

        private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            if (!(e.Row.Item is ViewsRow row)) return;
            var text = (e.EditingElement as TextBox)?.Text ?? string.Empty;
            var header = e.Column.Header?.ToString();
            if (header == "Номер листа")
                CommitSheetNumber(row, text);
            else
                CommitName(row, text);
        }

        private void CommitName(ViewsRow row, string text)
        {
            var name = text.Trim();
            if (string.Equals(row.Name, name, StringComparison.Ordinal)) return;
            if (name.Length == 0)
            {
                SnackBar.Show("Имя вида не может быть пустым.", SnackBarKind.Error);
                Reload();
                return;
            }
            var original = row.Name;
            row.Name = name;
            RequestRename(row, name, original);
        }

        private void CommitSheetNumber(ViewsRow row, string text)
        {
            var number = text.Trim();
            if (string.Equals(row.SheetNumber, number, StringComparison.Ordinal)) return;
            if (number.Length == 0)
            {
                SnackBar.Show("Номер листа не может быть пустым.", SnackBarKind.Error);
                Reload();
                return;
            }
            var original = row.SheetNumber;
            row.SheetNumber = number;
            RequestSheetNumber(row, number, original);
        }

        private void RequestRename(ViewsRow row, string name, string original)
        {
            var handler = Handler();
            if (handler == null) return;
            StatusText.Text = "Переименование: «" + name + "»…";
            handler.QueueRename(row.Id, name, result =>
            {
                try
                {
                    if (result.Ok)
                    {
                        SnackBar.Show(result.Message, SnackBarKind.Success);
                        StatusText.Text = "Готово.";
                    }
                    else
                    {
                        SnackBar.Show(result.Error, SnackBarKind.Error);
                        StatusText.Text = result.Error;
                        row.Name = original;
                    }
                }
                catch (Exception ex)
                {
                    GrdLog.Log("ViewsManagerWindow.RequestRename callback EXCEPTION: " + ex);
                }
            });
            Raise();
        }

        private void RequestSheetNumber(ViewsRow row, string number, string original)
        {
            var handler = Handler();
            if (handler == null) return;
            StatusText.Text = "Изменение номера листа «" + row.Name + "» → «" + number + "»…";
            handler.QueueSetSheetNumber(row.Id, number, result =>
            {
                try
                {
                    if (result.Ok)
                    {
                        SnackBar.Show(result.Message, SnackBarKind.Success);
                        StatusText.Text = "Готово.";
                    }
                    else
                    {
                        SnackBar.Show(result.Error, SnackBarKind.Error);
                        StatusText.Text = result.Error;
                        row.SheetNumber = original;
                    }
                }
                catch (Exception ex)
                {
                    GrdLog.Log("ViewsManagerWindow.RequestSheetNumber callback EXCEPTION: " + ex);
                }
            });
            Raise();
        }

        // ---- Дублирование ----

        private void OnDuplicateClick(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.Tag is ViewsRow row)) return;
            DuplicateRow(row);
        }

        private void OnDuplicateSelected(object sender, RoutedEventArgs e)
        {
            if (!(ViewsGrid.SelectedItem is ViewsRow row)) return;
            DuplicateRow(row);
        }

        private void DuplicateRow(ViewsRow row)
        {
            var handler = Handler();
            if (handler == null) return;
            StatusText.Text = "Дублирование: «" + row.Name + "»…";
            handler.QueueDuplicate(row.Id, result =>
            {
                try
                {
                    if (!result.Ok)
                    {
                        SnackBar.Show(result.Error, SnackBarKind.Error);
                        StatusText.Text = result.Error;
                        return;
                    }
                    SnackBar.Show(result.Message, SnackBarKind.Success);
                    Reload();
                    if (!string.IsNullOrEmpty(result.NewId) && long.TryParse(result.NewId, out var nid))
                    {
                        var nr = _all.FirstOrDefault(x => x.Id == nid);
                        if (nr != null)
                            BeginRename(nr);
                    }
                }
                catch (Exception ex)
                {
                    GrdLog.Log("ViewsManagerWindow.DuplicateRow callback EXCEPTION: " + ex);
                }
            });
            Raise();
        }

        /// <summary>Выделяет строку и включает редактирование ячейки «Имя вида».</summary>
        private void BeginRename(ViewsRow row)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var rowIdx = Rows.IndexOf(row);
                if (rowIdx < 0) return;
                ViewsGrid.SelectedItem = row;
                ViewsGrid.ScrollIntoView(row);
                ViewsGrid.CurrentCell = new DataGridCellInfo(row, ViewsGrid.Columns[0]);
                ViewsGrid.Focus();
                ViewsGrid.BeginEdit();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        // ---- Переход к виду ----

        private void OnOpenClick(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.Tag is ViewsRow row)) return;
            OpenRow(row);
        }

        private void OnOpenSelected(object sender, RoutedEventArgs e)
        {
            if (!(ViewsGrid.SelectedItem is ViewsRow row)) return;
            OpenRow(row);
        }

        // ---- Групповые операции (несколько отмеченных видов) ----

        private void OnAddPrefix(object sender, RoutedEventArgs e)
        {
            var selected = _all.Where(r => r.Marked).ToList();
            if (selected.Count == 0)
            {
                SnackBar.Show("Отметьте виды чекбоксами слева, чтобы добавить им префикс.", SnackBarKind.Error);
                return;
            }

            var dialog = new InputDialog
            {
                Owner = this,
                Title = "Префикс имён видов (" + selected.Count + ")",
                Prompt = "Введите префикс — он будет добавлен в начало имени каждого из " + selected.Count +
                         " отмеченных видов. Пример: «АР — » → вид станет «АР — План этажа 1»."
            };
            if (dialog.ShowDialog() != true)
                return;
            var prefix = (dialog.Value ?? string.Empty).Trim();
            if (prefix.Length == 0)
            {
                SnackBar.Show("Префикс не может быть пустым.", SnackBarKind.Error);
                return;
            }

            var handler = Handler();
            if (handler == null) return;

            StatusText.Text = "Добавление префикса «" + prefix + "» " + selected.Count + " видам…";
            handler.QueueAddPrefix(selected.Select(r => r.Id), prefix, result =>
            {
                try
                {
                    if (result.Ok)
                    {
                        SnackBar.Show(result.Message, SnackBarKind.Success);
                        StatusText.Text = "Готово.";
                        Reload();
                    }
                    else
                    {
                        SnackBar.Show(result.Error, SnackBarKind.Error);
                        StatusText.Text = result.Error;
                    }
                }
                catch (Exception ex)
                {
                    GrdLog.Log("ViewsManagerWindow.OnAddPrefix callback EXCEPTION: " + ex);
                }
            });
            Raise();
        }

        private void OpenRow(ViewsRow row)
        {
            var handler = Handler();
            if (handler == null) return;
            StatusText.Text = "Открытие: «" + row.Name + "»…";
            handler.QueueOpen(row.Id, result =>
            {
                try
                {
                    if (result.Ok)
                        StatusText.Text = "Готово.";
                    else
                        SnackBar.Show(result.Error, SnackBarKind.Error);
                }
                catch (Exception ex)
                {
                    GrdLog.Log("ViewsManagerWindow.OpenRow callback EXCEPTION: " + ex);
                }
            });
            Raise();
        }

        // ---- Шаблоны вида (выбор «активного» шаблона) ----

        private void OnPickTemplate(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var handler = Handler();
            if (handler == null) return;

            PickTemplateButton.IsEnabled = false;
            StatusText.Text = "Загрузка шаблонов…";
            try
            {
                handler.QueueReadTemplates(result =>
                {
                    PickTemplateButton.IsEnabled = true;
                    try
                    {
                        if (!string.IsNullOrEmpty(result.Error))
                        {
                            SnackBar.Show(result.Error, SnackBarKind.Error);
                            StatusText.Text = result.Error;
                            return;
                        }
                        if (result.Templates.Count == 0)
                        {
                            SnackBar.Show("В документе нет шаблонов видов.", SnackBarKind.Error);
                            StatusText.Text = "Шаблонов нет.";
                            return;
                        }

                        var picker = new ViewTemplatesPickerWindow(result.Templates) { Owner = this };
                        if (picker.ShowDialog() == true && picker.SelectedTemplate != null)
                        {
                            _activeTemplateId = picker.SelectedTemplate.Id;
                            _activeTemplateName = picker.SelectedTemplate.Name;
                            ActiveTemplateText.Text = "«" + _activeTemplateName + "»";
                            ActiveTemplatePanel.Visibility = Visibility.Visible;
                            StatusText.Text = "Выбран шаблон «" + _activeTemplateName + "» — применяйте его к видам кнопками в таблице.";
                            GrdLog.Log("ViewsManagerWindow: выбран шаблон «" + _activeTemplateName + "» id=" + _activeTemplateId);
                        }
                    }
                    catch (Exception ex)
                    {
                        GrdLog.Log("ViewsManagerWindow.QueueReadTemplates callback EXCEPTION: " + ex);
                    }
                });
                Raise();
            }
            catch (Exception ex)
            {
                PickTemplateButton.IsEnabled = true;
                StatusText.Text = "Ошибка: " + ex.Message;
                GrdLog.Log("ViewsManagerWindow.OnPickTemplate EXCEPTION: " + ex);
            }
        }

        private void OnApplyTemplateClick(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.Tag is ViewsRow row)) return;
            ApplyTemplateToRow(row, false);
        }

        private void OnLinkTemplateClick(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.Tag is ViewsRow row)) return;
            ApplyTemplateToRow(row, true);
        }

        private void ApplyTemplateToRow(ViewsRow row, bool link)
        {
            if (_activeTemplateId <= 0 || string.IsNullOrEmpty(_activeTemplateName))
            {
                SnackBar.Show("Сначала выберите шаблон вида кнопкой «Шаблон вида…».", SnackBarKind.Error);
                return;
            }
            var handler = Handler();
            if (handler == null) return;

            StatusText.Text = (link ? "Связывание" : "Применение") + " шаблона «" + _activeTemplateName + "» к виду «" + row.Name + "»…";
            handler.QueueApplyTemplate(row.Id, _activeTemplateId, link, result =>
            {
                try
                {
                    if (result.Ok)
                    {
                        SnackBar.Show(result.Message, SnackBarKind.Success);
                        StatusText.Text = "Готово.";
                    }
                    else
                    {
                        SnackBar.Show(result.Error, SnackBarKind.Error);
                        StatusText.Text = result.Error;
                    }
                }
                catch (Exception ex)
                {
                    GrdLog.Log("ViewsManagerWindow.ApplyTemplateToRow callback EXCEPTION: " + ex);
                }
            });
            Raise();
        }
    }
}