using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GrdRevit.Core;
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

        private bool _isFavorite;
        public bool IsFavorite
        {
            get => _isFavorite;
            set => Set(ref _isFavorite, value);
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
        private readonly HashSet<string> _favIds = new HashSet<string>(StringComparer.Ordinal);
        private long _activeTemplateId;
        private string _activeTemplateName = string.Empty;
        private string _docKey = string.Empty;
        private bool _busy;

        public ObservableCollection<ViewsRow> Rows { get; } = new ObservableCollection<ViewsRow>();

        public ViewsManagerWindow()
        {
            InitializeComponent();
            WindowTopmost.Track(this);
            DataContext = Rows;
            Loaded += (s, e) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    FocusSearch();
                }), System.Windows.Threading.DispatcherPriority.Input);
                Reload();
            };
        }

        /// <summary>Ключ документа задаётся командой: избранное хранится по документу.</summary>
        public void SetDocKey(string docKey)
        {
            _docKey = docKey ?? string.Empty;
            _favIds.Clear();
            foreach (var id in FavoriteStore.Load(_docKey, "views"))
                _favIds.Add(id);
        }

        /// <summary>Фокус в поле поиска по умолчанию: вызывается при открытии окна
        /// и после каждого обновления списка (сетка могла перехватить фокус).</summary>
        private void FocusSearch()
        {
            try
            {
                FilterBox.Focus();
                Keyboard.Focus(FilterBox);
                FilterBox.SelectAll();
            }
            catch { }
        }

        /// <summary>Повторный вызов по горячей клавише (или кнопке ленты) уже открытого окна:
        /// разворачивает из свёрнутого состояния, активирует и ставит фокус в поле поиска,
        /// чтобы пользователь мог сразу начать ввод.</summary>
        public void RestoreWithFocus()
        {
            WindowRestore.Activate(this);
            Dispatcher.BeginInvoke(new Action(FocusSearch),
                System.Windows.Threading.DispatcherPriority.Input);
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
            foreach (var r in _all)
                r.IsFavorite = _favIds.Contains(r.Id.ToString(CultureInfo.InvariantCulture));
            InfoText.Text = "Виды активного документа: " + _all.Count + " (с учётом фильтров).";
            ApplyFilter();
            StatusText.Text = "Готово.";
            GrdLog.Log("ViewsManagerWindow: строк=" + _all.Count);
            // Возвращаем фокус в поиск: после перезаполнения сетки он может уйти в грид.
            Dispatcher.BeginInvoke(new Action(FocusSearch), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void ApplyFilter()
        {
            if (FilterBox == null || ChkSection == null || ChkPlan == null || Chk3d == null || ChkSheet == null || ChkSchedule == null || RbStartsWith == null || RbContains == null || FavOnlyCheck == null) return;
            var search = (FilterBox.Text ?? string.Empty).Trim();
            bool showSection = ChkSection.IsChecked == true;
            bool showPlan = ChkPlan.IsChecked == true;
            bool show3d = Chk3d.IsChecked == true;
            bool showSheet = ChkSheet.IsChecked == true;
            bool showSchedule = ChkSchedule.IsChecked == true;
            bool startsWith = RbStartsWith.IsChecked == true;
            bool onlyFav = FavOnlyCheck.IsChecked == true;

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
                if (kindOk && nameOk && (!onlyFav || r.IsFavorite))
                    Rows.Add(r);
            }
            if (CountText != null)
                CountText.Text = "Показано: " + Rows.Count + " из " + _all.Count + (onlyFav ? " (избранное)" : "");
        }

        /// <summary>Возвращает фокус в поле поиска после щелчка по фильтрам
        /// (пресетам, чекбоксам, способу поиска). Каретка — в конец, выделение
        /// не трогаем: это перефокусировка, а не старт ввода с чистого листа.</summary>
        private void RefocusSearch()
        {
            if (FilterBox == null || FilterBox.IsKeyboardFocusWithin) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    FilterBox.Focus();
                    Keyboard.Focus(FilterBox);
                    FilterBox.CaretIndex = FilterBox.Text.Length;
                }
                catch { }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void OnFilterChanged(object sender, RoutedEventArgs e)
        {
            ApplyFilter();
            RefocusSearch();
        }

        /// <summary>Клавиши на уровне окна: Esc — закрыть окно (по желанию пользователя),
        /// Ctrl+K — вернуть курсор в поле поиска, а ▲▼/Home/End — навигация по списку
        /// видов/листов из любого места (поле поиска, таблица, фильтры, кнопки).
        /// Перехват на уровне окна перехватывает стрелку раньше, чем её попытается
        /// обработать системная навигация по фокусируемым элементам, поэтому выделение
        /// никогда не «уходит» на кнопки и радио-кнопки окна. Во время правки ячейки
        /// таблицы стрелками управляет поле ввода — не вмешиваемся.
        /// Ctrl+K, а не Ctrl+F: Ctrl+F перехватывает сам Revit (его «Найти/поиск») ещё
        /// до того, как клавиша дойдёт до WPF-окна, и Revit закрывает моделесс-диалог.
        /// </summary>
        private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (e.Key == Key.Escape && IsVisible)
                {
                    Close();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    FocusSearch();
                    e.Handled = true;
                    return;
                }

                // Редактирование ячейки таблицы: ▲▼/Home/End нужны полю ввода (каретка,
                // курсор по тексту) — пропускаем. Поле поиска (FilterBox) не относится
                // к правке ячейки: стрелки из него должны двигать список.
                if (Keyboard.FocusedElement is TextBox t && !ReferenceEquals(t, FilterBox))
                    return;

                if (e.Key == Key.Down || e.Key == Key.Up || e.Key == Key.Home || e.Key == Key.End)
                {
                    if (Rows.Count > 0)
                    {
                        MoveSelection(e.Key);
                        e.Handled = true;
                    }
                }
            }
            catch (Exception ex) { GrdLog.Log("ViewsManagerWindow.OnWindowPreviewKeyDown EXCEPTION: " + ex); }
        }

        /// <summary>Следующая строка списка от текущей выделенной: ▲▼ — на одну строку,
        /// Home/End — в начало/конец списка.</summary>
        private void MoveSelection(Key key)
        {
            var cur = ViewsGrid.SelectedItem as ViewsRow;
            int idx = cur != null ? Rows.IndexOf(cur) : -1;
            ViewsRow target;
            if (key == Key.Down)
                target = idx >= 0 ? Rows[Math.Min(idx + 1, Rows.Count - 1)] : Rows[0];
            else if (key == Key.Up)
                target = idx >= 0 ? Rows[Math.Max(idx - 1, 0)] : Rows[Rows.Count - 1];
            else if (key == Key.Home)
                target = Rows[0];
            else
                target = Rows[Rows.Count - 1];
            SelectRowInGrid(target);
        }

        /// <summary>Выделяет строку, ставит текущую ячейку на колонку имени и держит фокус
        /// в таблице. Фокус отдаётся конкретной строке/ячейке, а не контейнеру сетки:
        /// если фокус остаётся на контейнере, сетка не «съедает» стрелку и та уходит
        /// системной навигации — фокус прыгает по кнопкам и радио-кнопкам окна.</summary>
        private void SelectRowInGrid(ViewsRow target)
        {
            if (target == null || ViewsGrid == null || NameCol == null) return;
            ViewsGrid.SelectedItem = target;
            ViewsGrid.CurrentItem = target;
            ViewsGrid.CurrentCell = new DataGridCellInfo(target, NameCol);
            ViewsGrid.ScrollIntoView(target);
            ViewsGrid.UpdateLayout();
            try
            {
                ViewsGrid.Focus();
                var row = ViewsGrid.ItemContainerGenerator.ContainerFromItem(target) as DataGridRow;
                Keyboard.Focus(row ?? (IInputElement)ViewsGrid);
            }
            catch { }
        }

        /// <summary>Клавиатура в поле поиска: Enter — открыть выбранный вид. Стрелки
        /// и Home/End обрабатываются на уровне окна (см. OnWindowPreviewKeyDown),
        /// чтобы выделение двигалось строго по списку.</summary>
        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (e.Key == Key.Enter)
                {
                    if (Rows.Count == 0) return;
                    var row = ViewsGrid.SelectedItem as ViewsRow ?? Rows[0];
                    OpenRow(row);
                    e.Handled = true;
                }
            }
            catch (Exception ex) { GrdLog.Log("ViewsManagerWindow.OnSearchKeyDown EXCEPTION: " + ex); }
        }

        /// <summary>Клавиатура в сетке: Enter — открыть выбранный вид (если не идёт
        /// правка ячейки), а Ctrl+K — дублирующий возврат курсора в поле поиска
        /// (основной перехват — на уровне окна). Стрелки и Home/End обрабатываются
        /// на уровне окна (см. OnWindowPreviewKeyDown).</summary>
        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    FocusSearch();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Enter && !(Keyboard.FocusedElement is TextBox))
                {
                    if (ViewsGrid.SelectedItem is ViewsRow row)
                    {
                        OpenRow(row);
                        e.Handled = true;
                    }
                }
            }
            catch (Exception ex) { GrdLog.Log("ViewsManagerWindow.OnGridKeyDown EXCEPTION: " + ex); }
        }

        private void OnMatchModeChanged(object sender, RoutedEventArgs e)
        {
            ApplyFilter();
            RefocusSearch();
        }

        /// <summary>Переключатель «Только избранное»: просто переприменяем фильтр.</summary>
        private void OnFavOnlyChanged(object sender, RoutedEventArgs e)
        {
            ApplyFilter();
            RefocusSearch();
        }

        /// <summary>Звёздочка в строке: отметить/снять избранное и переприменить фильтр.</summary>
        private void OnToggleFavoriteClick(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !(b.Tag is ViewsRow row)) return;
            row.IsFavorite = !row.IsFavorite;
            FavoriteStore.Set(_docKey, "views",
                row.Id.ToString(CultureInfo.InvariantCulture), row.IsFavorite);
            ApplyFilter();
        }

        /// <summary>Пресет фильтра типов: одним кликом показать только нужный тип
        /// либо «Всё» сразу. Редкие комбинации — пресет «Вручную…», он просто
        /// показывает чекбоксы (ManualFiltersPanel) с текущим состоянием.</summary>
        private void OnPresetChanged(object sender, RoutedEventArgs e)
        {
            if (!(sender is RadioButton rb) || rb.IsChecked != true) return;
            ApplyPreset(rb);
            RefocusSearch();
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