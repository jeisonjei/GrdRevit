using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using GrdRevit.Revit;
using GrdRevit.Core;

namespace GrdRevit.Ui
{
    /// <summary>Коллекция значений строки: уведомляет об изменении индексатора,
    /// чтобы запись значения по коду (вставка, автозаполнение) сразу отражалась в сетке.</summary>
    public sealed class CellCollection : INotifyPropertyChanged
    {
        private readonly string[] _cells;

        public CellCollection(string[] cells) { _cells = cells ?? new string[0]; }

        public int Length => _cells.Length;

        public string this[int index]
        {
            get { return index >= 0 && index < _cells.Length ? _cells[index] : null; }
            set
            {
                if (index < 0 || index >= _cells.Length) return;
                if (string.Equals(_cells[index], value, StringComparison.Ordinal)) return;
                _cells[index] = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            }
        }

        public string[] ToArray() { return (string[])_cells.Clone(); }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>Строка редактируемой таблицы снимка: значения ячеек хранятся в памяти,
    /// в модель не пишутся. Изменения ячеек работают через двухстороннюю привязку к индексам.
    /// ChangedMask хранит битовую маску ячеек, отличающихся от данных модели.</summary>
    public sealed class SnapshotRow : INotifyPropertyChanged
    {
        private long _mask;

        public SnapshotRow(string[] cells) { Cells = new CellCollection(cells); }

        public CellCollection Cells { get; }

        public long ChangedMask
        {
            get { return _mask; }
            private set
            {
                if (_mask == value) return;
                _mask = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChangedMask)));
            }
        }

        public void SetChanged(int index, bool changed)
        {
            if (index < 0 || index > 62) return;
            long bit = 1L << index;
            ChangedMask = changed ? (_mask | bit) : (_mask & ~bit);
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>Преобразует битовую маску изменённых ячеек в bool для конкретной колонки
    /// (номер колонки передаётся в ConverterParameter).</summary>
    public sealed class MaskBitConverter : IValueConverter
    {
        public static readonly MaskBitConverter Instance = new MaskBitConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            long mask = value is long l ? l : (value is int i ? i : 0L);
            int bit;
            if (!int.TryParse(System.Convert.ToString(parameter, CultureInfo.InvariantCulture), out bit)) return false;
            if (bit < 0 || bit > 62) return false;
            return (mask & (1L << bit)) != 0L;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

    /// <summary>
    /// Окно «Снимок спецификаций»: слева спецификации, размещённые на листах (с поиском),
    /// справа — редактируемая таблица-снимок. «Обновить из модели» возвращает сетку
    /// к исходному виду, «Печать в PDF» сохраняет текущую (с правками) в PDF.
    /// Моделесс-окно не обращается к API напрямую — чтение идёт через ExternalEvent.
    /// </summary>
    public partial class ScheduleSnapshotWindow : Window
    {
        /// <summary>Галочка «Переносить строки ячеек»: при включении длинные значения
        /// переносятся на несколько строк, а высота строк таблицы растёт автоматически.</summary>
        public static readonly DependencyProperty WrapCellsProperty =
            DependencyProperty.Register(nameof(WrapCells), typeof(bool), typeof(ScheduleSnapshotWindow),
                new PropertyMetadata(false, OnWrapCellsChanged));

        public bool WrapCells
        {
            get => (bool)GetValue(WrapCellsProperty);
            set => SetValue(WrapCellsProperty, value);
        }

        private static void OnWrapCellsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var w = d as ScheduleSnapshotWindow;
            if (w?.GridTable == null) return;
            w.GridTable.RowHeight = (bool)e.NewValue ? double.NaN : 30;
        }

        private const int UndoLimit = 100;

        private static readonly Brush ChangedCellBrush = CreateChangedBrush();

        private static Brush CreateChangedBrush()
        {
            var b = new SolidColorBrush(Color.FromRgb(255, 244, 204));
            b.Freeze();
            return b;
        }

        private readonly List<SheetScheduleInfo> _master = new List<SheetScheduleInfo>();
        private readonly ObservableCollection<SheetScheduleInfo> _visible = new ObservableCollection<SheetScheduleInfo>();
        private readonly ObservableCollection<SnapshotRow> _rows = new ObservableCollection<SnapshotRow>();
        private readonly List<List<string[]>> _undo = new List<List<string[]>>();
        private readonly List<List<string[]>> _redo = new List<List<string[]>>();

        private SheetScheduleInfo _selected;
        private ScheduleTable _table;
        private List<string[]> _modelRows;
        private bool _isFree;
        private long _templateScheduleId;
        private long _pendingSelectScheduleId;
        private long _pendingSelectSheetId;
        private List<string[]> _pendingUndoSnapshot;
        private bool _suppressSel;
        private bool _loading;
        private bool _dirty;
        private string _docKey = string.Empty;
        private readonly HashSet<string> _favIds = new HashSet<string>(StringComparer.Ordinal);

        public ScheduleSnapshotWindow()
        {
            InitializeComponent();
            DataContext = this;
            WindowTopmost.Track(this);
            SheetsList.ItemsSource = _visible;
            GridTable.ItemsSource = _rows;
            GridTable.RowHeight = double.NaN;
            EmptyHint.Visibility = Visibility.Visible;
            PrintBtn.IsEnabled = false;
            SheetPrintBtn.IsEnabled = false;
            ScheduleLabel.Text = "Спецификация не выбрана.";
            UpdateCheckedUi();
            UpdateUndoUi();
            UpdateRowUi();

            Closing += (s, e) => SaveCurrentEdits();

            Loaded += (s, e) =>
            {
                SearchBox.Focus();
                System.Windows.Input.Keyboard.Focus(SearchBox);
                LoadList();
            };
        }

        // ------------------------------------------------------------------ Список

        private void LoadList()
        {
            var handler = RevitContext.ScheduleSnapshotHandler;
            var ev = RevitContext.ScheduleSnapshotEvent;
            if (handler == null || ev == null)
            {
                PrintStatus.Text = "Обработчик снимков спецификаций недоступен.";
                return;
            }
            if (_loading) return;
            _loading = true;
            try
            {
                handler.QueueReadList(res =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(res.Error))
                        {
                            PrintStatus.Text = res.Error;
                            return;
                        }
                        if (!string.IsNullOrEmpty(res.DocKey)) _docKey = res.DocKey;
                        _master.Clear();
                        _master.AddRange(res.Schedules);
                        LoadFavorites();
                        UpdateSheetCount();
                        RebuildList();
                        Dispatcher.BeginInvoke(new Action(SelectPending));
                    }
                    finally
                    {
                        _loading = false;
                    }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                _loading = false;
                PrintStatus.Text = "Не удалось запросить список: " + ex.Message;
            }
        }

        private void UpdateSheetCount()
        {
            SheetCount.Text = "На листах: " + _master.Count + " спецификаци" +
                              (_master.Count % 10 == 1 && _master.Count % 100 != 11 ? "я"
                                  : (_master.Count % 10 >= 2 && _master.Count % 10 <= 4 && (_master.Count % 100 < 12 || _master.Count % 100 > 14) ? "и" : "й"));
        }

        /// <summary>Загружает избранное для этого документа и расставляет звёздочки
        /// в списке. Ключ элемента — лист + спецификация (разные листы одного
        /// носителя избранного — отдельные элементы).</summary>
        private void LoadFavorites()
        {
            _favIds.Clear();
            if (string.IsNullOrEmpty(_docKey)) return;
            foreach (var id in FavoriteStore.Load(_docKey, "snap"))
                _favIds.Add(id);
            foreach (var item in _master)
                item.IsFavorite = _favIds.Contains(FavKey(item));
        }

        private static string FavKey(SheetScheduleInfo item)
        {
            return item.SheetId.ToString(CultureInfo.InvariantCulture) + ":" +
                   item.ScheduleId.ToString(CultureInfo.InvariantCulture);
        }

        private void OnFavoriteClick(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !(b.Tag is SheetScheduleInfo item)) return;
            item.IsFavorite = !item.IsFavorite;
            FavoriteStore.Set(_docKey, "snap", FavKey(item), item.IsFavorite);
            RebuildList();
        }

        private void OnFavOnlyChanged(object sender, RoutedEventArgs e)
        {
            RebuildList();
        }

        private void RebuildList()
        {
            var q = (SearchBox.Text ?? string.Empty).Trim().ToLowerInvariant();
            bool onlyFav = FavOnlyCheck != null && FavOnlyCheck.IsChecked == true;
            _suppressSel = true;
            try
            {
                _visible.Clear();
                foreach (var item in _master)
                {
                    if (onlyFav && !item.IsFavorite) continue;
                    if (q.Length == 0 || item.SearchKey.IndexOf(q, StringComparison.Ordinal) >= 0)
                        _visible.Add(item);
                }
                if (_visible.Contains(_selected))
                {
                    SheetsList.SelectedItem = _selected;
                }
                else if (_selected != null)
                {
                    _selected = null;
                    ClearTable();
                }
            }
            finally
            {
                _suppressSel = false;
            }
            UpdateCheckedUi();
        }

        /// <summary>Отмеченные галочками листы (в порядке списка).</summary>
        private List<SheetScheduleInfo> CheckedItems()
        {
            return _master.Where(x => x.IsChecked).ToList();
        }

        private void UpdateCheckedUi()
        {
            int n = _master.Count(x => x.IsChecked);
            SelectedCount.Text = "Выбрано листов: " + n;
            MultiPrintBtn.IsEnabled = n > 0;
            ExportExcelBtn.IsEnabled = n > 0;
        }

        private void OnSheetCheckToggled(object sender, RoutedEventArgs e)
        {
            UpdateCheckedUi();
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            RebuildList();
        }

        private void OnSearchReset(object sender, RoutedEventArgs e)
        {
            SearchBox.Clear();
        }

        private void OnSheetSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSel) return;
            var item = SheetsList.SelectedItem as SheetScheduleInfo;
            if (item != null)
            {
                // Правки сохраняются автоматически при переходе к другой спецификации,
                // поэтому подтверждение не запрашивается и ничего не теряется.
                if (_selected != null && _selected != item)
                    SaveCurrentEdits();
                _selected = item;
                SheetPrintBtn.IsEnabled = true;
                LoadTable(item);
            }
            else if (e.RemovedItems.Count > 0)
            {
                SaveCurrentEdits();
                _selected = null;
                ClearTable();
            }
        }

        // ------------------------------------------------------------------ Таблица

        private void LoadTable(SheetScheduleInfo info)
        {
            var handler = RevitContext.ScheduleSnapshotHandler;
            var ev = RevitContext.ScheduleSnapshotEvent;
            if (handler == null || ev == null) return;
            if (_loading) return;
            _loading = true;
            PrintBtn.IsEnabled = false;
            ScheduleLabel.Text = "Лист " + (string.IsNullOrWhiteSpace(info.SheetNumber) ? "—" : info.SheetNumber) +
                                 ((string.IsNullOrWhiteSpace(info.SheetName)) ? "" : " · " + info.SheetName) +
                                 " — «" + info.ScheduleName + "»";
            PrintStatus.Text = "Чтение из модели…";
            try
            {
                handler.QueueReadTable(info.SheetId, info.ScheduleId, res =>
                {
                    try
                    {
                        _loading = false;
                        if (!string.IsNullOrEmpty(res.Error))
                        {
                            PrintStatus.Text = res.Error;
                            ClearTable();
                            return;
                        }
                        _table = res.Table;
                        _isFree = _table.IsFree;
                        _templateScheduleId = _table.TemplateScheduleId;
                        _modelRows = _isFree ? null : _table.Rows.Select(r => (string[])r.Clone()).ToList();
                        if (!string.IsNullOrEmpty(res.DocKey)) _docKey = res.DocKey;
                        bool restored = !_isFree && TryRestoreSaved(_table);
                        PopulateGrid(_table);
                        if (_isFree)
                        {
                            PrintStatus.Text = "Свободная спецификация (образец заголовков) · строк: " +
                                               _rows.Count +
                                               " · данные хранятся отдельно от модели.";
                        }
                        else if (_table.Warning.Length > 0)
                        {
                            PrintStatus.Text = _table.Warning;
                        }
                        else
                        {
                            PrintStatus.Text = "Заголовков: " + _table.Headers.Count +
                                               " · строк: " + _table.Rows.Count +
                                               " · правки только в этом окне, в модель они не пишутся.";
                        }

                        if (restored)
                        {
                            _dirty = true;
                            PrintStatus.Text = "Восстановлены сохранённые правки. «Сбросить правки» вернёт данные модели.";
                        }
                    }
                    catch (Exception ex)
                    {
                        _loading = false;
                        PrintStatus.Text = "Ошибка отображения: " + ex.Message;
                    }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                _loading = false;
                PrintStatus.Text = "Не удалось запустить чтение: " + ex.Message;
            }
        }

        private void PopulateGrid(ScheduleTable table)
        {
            GridTable.Columns.Clear();
            for (int i = 0; i < table.Headers.Count; i++)
            {
                var col = new DataGridTextColumn
                {
                    Header = string.IsNullOrWhiteSpace(table.Headers[i]) ? "(колонка " + (i + 1) + ")" : table.Headers[i],
                    Binding = new Binding("Cells[" + i + "]")
                    {
                        Mode = BindingMode.TwoWay,
                        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                    },
                    IsReadOnly = false
                };
                col.MinWidth = 60;
                col.Width = new DataGridLength(ColumnWidth(table.Headers[i], table.Rows, i));
                col.ElementStyle = MakeColumnDisplayStyle();
                col.EditingElementStyle = MakeColumnEditStyle();
                col.CellStyle = MakeCellStyle(i);
                GridTable.Columns.Add(col);
            }

            _rows.Clear();
            foreach (var row in table.Rows)
                _rows.Add(new SnapshotRow(row));

            _undo.Clear();
            _redo.Clear();
            _pendingUndoSnapshot = null;
            RecomputeChanged();
            UpdateUndoUi();

            _dirty = false;
            PrintBtn.IsEnabled = _rows.Count > 0;
            UpdateRowUi();
        }

        private void ClearTable()
        {
            _table = null;
            _modelRows = null;
            _isFree = false;
            _templateScheduleId = 0;
            _rows.Clear();
            GridTable.Columns.Clear();
            _undo.Clear();
            _redo.Clear();
            _pendingUndoSnapshot = null;
            PrintBtn.IsEnabled = false;
            SheetPrintBtn.IsEnabled = false;
            _dirty = false;
            UpdateUndoUi();
            UpdateRowUi();
        }

        /// <summary>Подставляет сохранённые ранее правки этой спецификации, если набор
        /// колонок совпадает с моделью. Возвращает true, если правки восстановлены.</summary>
        private bool TryRestoreSaved(ScheduleTable table)
        {
            if (table == null || string.IsNullOrEmpty(_docKey)) return false;
            var saved = ScheduleSnapshotsFile.LoadEntry(_docKey, table.ScheduleId, table.SheetId, table.SegmentIndex);
            if (saved == null || saved.Headers.Count != table.Headers.Count) return false;
            for (int i = 0; i < saved.Headers.Count; i++)
            {
                if (!string.Equals(saved.Headers[i], table.Headers[i], StringComparison.Ordinal))
                    return false;
            }
            table.Rows.Clear();
            foreach (var row in saved.Rows) table.Rows.Add(row);
            return saved.Rows.Count > 0;
        }

        /// <summary>Записывает текущие правки таблицы на диск (JSON).</summary>
        private void SaveCurrentEdits()
        {
            if (_table == null || string.IsNullOrEmpty(_docKey) || !_dirty) return;
            var sched = new SerializedSchedule
            {
                ScheduleId = _table.ScheduleId,
                ScheduleName = _table.ScheduleName,
                IsFree = _isFree,
                TemplateScheduleId = _templateScheduleId,
                SheetId = _table.SheetId,
                SegmentIndex = _table.SegmentIndex
            };
            sched.Headers.AddRange(_table.Headers);
            foreach (var row in _rows) sched.Rows.Add(row.Cells.ToArray());
            ScheduleSnapshotsFile.SaveEntry(_docKey, sched);
        }

        // ------------------------------------------------------------------ Подсветка изменённых ячеек

        /// <summary>Пересчитывает маску изменённых относительно модели ячеек для каждой строки.</summary>
        private void RecomputeChanged()
        {
            // Свободная спецификация не сопоставляется с моделью — подсветки нет.
            if (_isFree)
            {
                foreach (var fr in _rows)
                    for (int fc = 0; fc < fr.Cells.Length; fc++)
                        fr.SetChanged(fc, false);
                return;
            }
            for (int r = 0; r < _rows.Count; r++)
            {
                var row = _rows[r];
                for (int c = 0; c < row.Cells.Length; c++)
                {
                    bool changed;
                    if (_modelRows != null && r < _modelRows.Count && c < _modelRows[r].Length)
                        changed = !string.Equals(row.Cells[c], _modelRows[r][c], StringComparison.Ordinal);
                    else
                        changed = true;
                    row.SetChanged(c, changed);
                }
            }
        }

        private Style MakeCellStyle(int index)
        {
            Style basedOn = null;
            try { basedOn = FindResource(typeof(DataGridCell)) as Style; } catch { }

            var st = basedOn != null ? new Style(typeof(DataGridCell), basedOn) : new Style(typeof(DataGridCell));
            var tr = new DataTrigger
            {
                Binding = new Binding(nameof(SnapshotRow.ChangedMask))
                {
                    Converter = MaskBitConverter.Instance,
                    ConverterParameter = index
                },
                Value = true
            };
            tr.Setters.Add(new Setter(DataGridCell.BackgroundProperty, ChangedCellBrush));
            st.Triggers.Add(tr);
            return st;
        }

        // ------------------------------------------------------------------ Отмена / возврат

        private List<string[]> CurrentSnapshot()
        {
            var list = new List<string[]>(_rows.Count);
            foreach (var row in _rows) list.Add(row.Cells.ToArray());
            return list;
        }

        private void ApplySnapshot(List<string[]> snap)
        {
            int cols = snap.Count > 0 ? snap[0].Length : ColumnCount();
            while (_rows.Count > snap.Count) _rows.RemoveAt(_rows.Count - 1);
            while (_rows.Count < snap.Count) _rows.Add(new SnapshotRow(EmptyRow(cols)));
            for (int r = 0; r < _rows.Count; r++)
            {
                var cells = _rows[r].Cells;
                for (int c = 0; c < cells.Length && c < snap[r].Length; c++)
                    cells[c] = snap[r][c];
            }
        }

        private int ColumnCount()
        {
            if (_table != null) return _table.Headers.Count;
            return GridTable.Columns.Count;
        }

        private static string[] EmptyRow(int cols)
        {
            var cells = new string[cols];
            for (int i = 0; i < cols; i++) cells[i] = string.Empty;
            return cells;
        }

        private static bool SnapshotsDiffer(List<string[]> a, List<string[]> b)
        {
            if (a == null || b == null) return true;
            if (a.Count != b.Count) return true;
            for (int r = 0; r < a.Count; r++)
            {
                if (a[r].Length != b[r].Length) return true;
                for (int c = 0; c < a[r].Length; c++)
                {
                    if (!string.Equals(a[r][c], b[r][c], StringComparison.Ordinal)) return true;
                }
            }
            return false;
        }

        private void PushUndo(List<string[]> snap)
        {
            if (snap == null) return;
            _undo.Add(snap);
            while (_undo.Count > UndoLimit) _undo.RemoveAt(0);
            _redo.Clear();
        }

        private void UpdateUndoUi()
        {
            if (UndoBtn != null) UndoBtn.IsEnabled = _undo.Count > 0;
            if (RedoBtn != null) RedoBtn.IsEnabled = _redo.Count > 0;
            if (ResetBtn != null) ResetBtn.IsEnabled = _modelRows != null || (_isFree && _rows.Count > 0);
        }

        private void UpdateRowUi()
        {
            bool hasTable = _table != null;
            if (AddRowBtn != null) AddRowBtn.IsEnabled = hasTable;
            if (DelRowBtn != null) DelRowBtn.IsEnabled = hasTable && _rows.Count > 0;
            if (EmptyHint != null)
            {
                EmptyHint.Text = hasTable
                    ? "В таблице нет строк. Добавьте строку кнопкой «Добавить строку» (Insert)."
                    : "Выберите спецификацию слева.";
                EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (GridTable != null)
                GridTable.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void AfterBulkChange(string status)
        {
            _dirty = true;
            RecomputeChanged();
            UpdateUndoUi();
            UpdateRowUi();
            SaveCurrentEdits();
            if (!string.IsNullOrEmpty(status)) PrintStatus.Text = status;
        }

        private void OnUndo(object sender, RoutedEventArgs e) { DoUndo(); }

        private void OnRedo(object sender, RoutedEventArgs e) { DoRedo(); }

        private void DoUndo()
        {
            if (_undo.Count == 0) return;
            _redo.Add(CurrentSnapshot());
            var snap = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            ApplySnapshot(snap);
            AfterBulkChange("Действие отменено (Ctrl+Z).");
        }

        private void DoRedo()
        {
            if (_redo.Count == 0) return;
            _undo.Add(CurrentSnapshot());
            var snap = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            ApplySnapshot(snap);
            AfterBulkChange("Действие возвращено (Ctrl+Y).");
        }

        private void OnReset(object sender, RoutedEventArgs e)
        {
            // Свободная спецификация: сброс — это очистка строк (модели-образца у неё нет),
            // сама запись о свободной спецификации сохраняется.
            if (_isFree)
            {
                if (_rows.Count == 0) return;
                var rf = MessageBox.Show(this,
                    "Очистить свободную спецификацию?\n\nВсе введённые строки будут удалены, заголовки колонок останутся.",
                    "Снимок спецификаций",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (rf != MessageBoxResult.Yes) return;
                PushUndo(CurrentSnapshot());
                ApplySnapshot(new List<string[]>());
                AfterBulkChange("Свободная спецификация очищена.");
                return;
            }

            if (_rows.Count == 0 || _modelRows == null) return;
            var r = MessageBox.Show(this,
                "Вернуть спецификацию к виду из модели?\n\nВсе правки этой спецификации будут сброшены.",
                "Снимок спецификаций",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return;

            PushUndo(CurrentSnapshot());
            ApplySnapshot(_modelRows);
            ScheduleSnapshotsFile.DeleteEntry(_docKey, _table.ScheduleId, _table.SheetId, _table.SegmentIndex);
            _dirty = false;
            RecomputeChanged();
            UpdateUndoUi();
            UpdateRowUi();
            PrintStatus.Text = "Правки сброшены к виду из модели.";
        }

        // ------------------------------------------------------------------ Буфер обмена и автозаполнение

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            // Во время правки ячейки не перехватываем стандартные действия текстового поля.
            if (Keyboard.FocusedElement is TextBox) return;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (e.Key == Key.Insert) { AddRow(); e.Handled = true; return; }
            if (e.Key == Key.Delete && ctrl) { DeleteRows(); e.Handled = true; return; }
            if (!ctrl) return;
            switch (e.Key)
            {
                case Key.C: CopySelection(); e.Handled = true; break;
                case Key.V: PasteSelection(); e.Handled = true; break;
                case Key.D: FillDown(); e.Handled = true; break;
                case Key.Z: DoUndo(); e.Handled = true; break;
                case Key.Y: DoRedo(); e.Handled = true; break;
            }
        }

        private bool GetSelectionRect(out int r0, out int c0, out int r1, out int c1)
        {
            r0 = c0 = int.MaxValue;
            r1 = c1 = -1;
            foreach (var ci in GridTable.SelectedCells)
            {
                int r = GridTable.Items.IndexOf(ci.Item);
                int c = ci.Column != null ? ci.Column.DisplayIndex : -1;
                if (r < 0 || c < 0) continue;
                if (r < r0) r0 = r;
                if (r > r1) r1 = r;
                if (c < c0) c0 = c;
                if (c > c1) c1 = c;
            }
            if (r1 < r0 || c1 < c0)
            {
                var cur = GridTable.CurrentCell;
                if (cur.IsValid && cur.Column != null)
                {
                    int r = GridTable.Items.IndexOf(cur.Item);
                    int c = cur.Column.DisplayIndex;
                    if (r >= 0 && c >= 0) { r0 = r1 = r; c0 = c1 = c; }
                }
            }
            return r0 >= 0 && r1 >= r0 && c1 >= c0;
        }

        private bool InBounds(int r, int c)
        {
            return r >= 0 && r < _rows.Count && c >= 0 && c < _rows[r].Cells.Length;
        }

        private void CopySelection()
        {
            int r0, c0, r1, c1;
            if (!GetSelectionRect(out r0, out c0, out r1, out c1)) return;
            if (!InBounds(r0, c0) || !InBounds(r1, c1)) return;
            var sb = new StringBuilder();
            for (int r = r0; r <= r1; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    if (c > c0) sb.Append('\t');
                    sb.Append(_rows[r].Cells[c] ?? string.Empty);
                }
                sb.Append("\r\n");
            }
            try
            {
                Clipboard.SetText(sb.ToString());
                PrintStatus.Text = "Скопировано в буфер: строк " + (r1 - r0 + 1) + " × колонок " + (c1 - c0 + 1) + ".";
            }
            catch (Exception ex)
            {
                GrdLog.Log("ScheduleSnapshotWindow.Copy EXCEPTION: " + ex);
                PrintStatus.Text = "Не удалось скопировать: " + ex.Message;
            }
        }

        private void PasteSelection()
        {
            string text = null;
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
            catch (Exception ex) { GrdLog.Log("ScheduleSnapshotWindow.Paste read EXCEPTION: " + ex); }
            if (string.IsNullOrEmpty(text)) return;

            int r0, c0, r1, c1;
            if (!GetSelectionRect(out r0, out c0, out r1, out c1)) return;

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');
            var undo = CurrentSnapshot();
            bool changed = false;
            for (int dr = 0; dr < lines.Length; dr++)
            {
                int r = r0 + dr;
                if (r >= _rows.Count) break;
                var values = lines[dr].Split('\t');
                for (int dc = 0; dc < values.Length; dc++)
                {
                    int c = c0 + dc;
                    if (!InBounds(r, c)) break;
                    if (!string.Equals(_rows[r].Cells[c], values[dc], StringComparison.Ordinal))
                    {
                        _rows[r].Cells[c] = values[dc];
                        changed = true;
                    }
                }
            }
            if (!changed) return;
            PushUndo(undo);
            AfterBulkChange("Вставлено из буфера: строк " + lines.Length + ".");
        }

        private void FillDown()
        {
            int r0, c0, r1, c1;
            if (!GetSelectionRect(out r0, out c0, out r1, out c1)) return;
            if (r1 <= r0) return;
            var undo = CurrentSnapshot();
            bool changed = false;
            for (int c = c0; c <= c1; c++)
            {
                if (!InBounds(r0, c)) continue;
                var value = _rows[r0].Cells[c];
                for (int r = r0 + 1; r <= r1; r++)
                {
                    if (!InBounds(r, c)) break;
                    if (!string.Equals(_rows[r].Cells[c], value, StringComparison.Ordinal))
                    {
                        _rows[r].Cells[c] = value;
                        changed = true;
                    }
                }
            }
            if (!changed) return;
            PushUndo(undo);
            AfterBulkChange("Значение заполнено вниз (Ctrl+D).");
        }

        // ------------------------------------------------------------------ Строки

        private int SelectedRowIndex()
        {
            var indices = SelectedRowIndices();
            return indices.Count > 0 ? indices[0] : -1;
        }

        /// <summary>Индексы строк, попавших в текущее выделение (по возрастанию, без повторов).</summary>
        private List<int> SelectedRowIndices()
        {
            var set = new SortedSet<int>();
            foreach (var ci in GridTable.SelectedCells)
            {
                int r = GridTable.Items.IndexOf(ci.Item);
                if (r >= 0) set.Add(r);
            }
            if (set.Count == 0)
            {
                var cur = GridTable.CurrentCell;
                if (cur.IsValid)
                {
                    int r = GridTable.Items.IndexOf(cur.Item);
                    if (r >= 0) set.Add(r);
                }
            }
            return set.ToList();
        }

        private void OnAddRow(object sender, RoutedEventArgs e) { AddRow(); }

        /// <summary>Добавляет пустую строку: под выделённой строкой, иначе в конец.</summary>
        private void AddRow()
        {
            if (_table == null)
            {
                PrintStatus.Text = "Сначала выберите спецификацию.";
                return;
            }
            int cols = ColumnCount();
            if (cols == 0) return;

            var undo = CurrentSnapshot();
            int index = _rows.Count;
            int sel = SelectedRowIndex();
            if (sel >= 0 && sel < _rows.Count) index = sel + 1;

            var row = new SnapshotRow(EmptyRow(cols));
            _rows.Insert(index, row);
            PushUndo(undo);
            AfterBulkChange("Добавлена строка (Insert).");
            SelectRow(index);
        }

        private void OnDeleteRow(object sender, RoutedEventArgs e) { DeleteRows(); }

        /// <summary>Удаляет все строки, попавшие в выделение.</summary>
        private void DeleteRows()
        {
            if (_rows.Count == 0) return;
            var indices = SelectedRowIndices();
            if (indices.Count == 0)
            {
                PrintStatus.Text = "Выберите строку (ячейку) для удаления.";
                return;
            }

            var undo = CurrentSnapshot();
            for (int i = indices.Count - 1; i >= 0; i--)
            {
                if (indices[i] >= 0 && indices[i] < _rows.Count) _rows.RemoveAt(indices[i]);
            }
            PushUndo(undo);
            AfterBulkChange("Удалено строк: " + indices.Count + " (Ctrl+Delete).");
        }

        private void SelectRow(int index)
        {
            if (index < 0 || index >= _rows.Count) return;
            try
            {
                GridTable.ScrollIntoView(_rows[index]);
                if (GridTable.Columns.Count > 0)
                {
                    GridTable.SelectedCells.Clear();
                    GridTable.CurrentCell = new DataGridCellInfo(_rows[index], GridTable.Columns[0]);
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("ScheduleSnapshotWindow.SelectRow EXCEPTION: " + ex);
            }
        }

        // ------------------------------------------------------------------ Стили и ширина

        private static Style MakeColumnDisplayStyle()
        {
            var st = new Style(typeof(TextBlock));
            st.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
            st.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.None));
            st.Triggers.Add(WrapTrigger<TextBlock>(TextBlock.TextWrappingProperty));
            return st;
        }

        private static Style MakeColumnEditStyle()
        {
            var st = new Style(typeof(TextBox));
            st.Setters.Add(new Setter(TextBox.TextWrappingProperty, TextWrapping.NoWrap));
            st.Setters.Add(new Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            st.Triggers.Add(WrapTrigger<TextBox>(TextBox.TextWrappingProperty));
            return st;
        }

        private static DataTrigger WrapTrigger<T>(DependencyProperty property) where T : DependencyObject
        {
            var tr = new DataTrigger { Value = true };
            tr.Binding = new Binding(nameof(WrapCells))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1)
            };
            tr.Setters.Add(new Setter(property, TextWrapping.Wrap));
            return tr;
        }

        private static double ColumnWidth(string header, IReadOnlyList<string[]> rows, int cIndex)
        {
            double w = Math.Max(header?.Length ?? 0, 4) * 7.2 + 16;
            for (int r = 0; r < rows.Count && r < 500; r++)
            {
                var cell = rows[r][cIndex];
                if (cell == null) continue;
                if (cell.Length > 60) return 320;
                w = Math.Max(w, cell.Length * 7.2 + 16);
            }
            return Math.Min(w, 320);
        }

        // ------------------------------------------------------------------ Кнопки

        private void OnRefresh(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            if (_isFree)
            {
                LoadTable(_selected);
                return;
            }
            if (_dirty)
            {
                var r = MessageBox.Show(this,
                    "Заменить таблицу данными из модели?\n\nВнесённые правки будут потеряны.",
                    "Снимок спецификаций",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (r != MessageBoxResult.Yes) return;
            }
            ScheduleSnapshotsFile.DeleteEntry(_docKey, _selected.ScheduleId, _table?.SheetId ?? _selected.SheetId,
                    _table?.SegmentIndex ?? -1);
            LoadTable(_selected);
        }

        private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit)
            {
                _pendingUndoSnapshot = null;
                return;
            }
            _dirty = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var snap = _pendingUndoSnapshot;
                _pendingUndoSnapshot = null;
                if (snap != null && SnapshotsDiffer(snap, CurrentSnapshot()))
                    PushUndo(snap);
                RecomputeChanged();
                UpdateUndoUi();
                SaveCurrentEdits();
                PrintStatus.Text = "Изменено. Правки сохраняются автоматически (Ctrl+Z — отменить).";
            }));
        }

        private void GridTable_PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
        {
            _pendingUndoSnapshot = CurrentSnapshot();
            var tb = e.EditingElement as TextBox;
            if (tb == null) return;
            tb.Focus();
            // Если редактирование начато вводом символа, текст ячейки не выделяем: иначе
            // первый введённый символ оказывается выделен и следующий затирает его.
            // Выделение оставляем только для F2/двойного щелчка, где важен ввод «поверх».
            if (e.EditingEventArgs is TextCompositionEventArgs)
            {
                tb.CaretIndex = tb.Text.Length;
                return;
            }
            tb.SelectAll();
        }

        private void OnPrint(object sender, RoutedEventArgs e)
        {
            if (_table == null || _rows.Count == 0)
            {
                PrintStatus.Text = "Нет данных для печати.";
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить снимок спецификации в PDF",
                Filter = "PDF (*.pdf)|*.pdf",
                DefaultExt = ".pdf",
                AddExtension = true,
                FileName = SafeFileName(_table.ScheduleName) + ".pdf",
                OverwritePrompt = true
            };
            var dirs = RevitContext.Settings.ScheduleSnapPdfDir;
            dlg.InitialDirectory = !string.IsNullOrEmpty(dirs) && Directory.Exists(dirs)
                ? dirs
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            if (dlg.ShowDialog() != true) return;
            var path = dlg.FileName;

            try
            {
                PrintStatus.Text = "Формирование PDF…";
                var headers = GridTable.Columns.Select(c => ((DataGridTextColumn)c).Header?.ToString() ?? string.Empty).ToList();
                var rows = _rows.Select(row => row.Cells.ToArray()).ToList();
                SchedulePdfRenderer.Write(path, _table.ScheduleName, headers, rows,
                                          _table.SheetWidthMm, _table.SheetHeightMm);

                RevitContext.Settings.ScheduleSnapPdfDir = Path.GetDirectoryName(path) ?? string.Empty;
                try { RevitContext.SaveSettings(); } catch (Exception ex) { GrdLog.Log("ScheduleSnapshotWindow.SaveSettings EXCEPTION " + ex); }

                PrintStatus.Text = "PDF сохранён: " + path;
                try { System.Diagnostics.Process.Start(path); }
                catch (Exception ex) { GrdLog.Log("ScheduleSnapshotWindow: открыть PDF EXCEPTION " + ex); }
            }
            catch (Exception ex)
            {
                GrdLog.Log("ScheduleSnapshotWindow.OnPrint EXCEPTION: " + ex);
                PrintStatus.Text = "Не удалось создать PDF: " + ex.Message;
                MessageBox.Show(this, "Не удалось создать PDF:\n\n" + ex.Message,
                    "Снимок спецификаций", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Печать листа (рамка/штамп, форма ГОСТ) с текущей отредактированной таблицей.
        /// Revit во временной транзакции убирает исходную спецификацию, рисует нашу таблицу,
        /// экспортирует лист в PDF и откатывает изменения — модель остаётся неизменной.</summary>
        private void OnPrintSheet(object sender, RoutedEventArgs e)
        {
            if (_selected == null || _table == null || _rows.Count == 0)
            {
                PrintStatus.Text = "Нет данных для печати.";
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить лист (форма ГОСТ) с текущей таблицей",
                Filter = "PDF (*.pdf)|*.pdf",
                DefaultExt = ".pdf",
                AddExtension = true,
                FileName = SafeFileName(_selected.SheetNumber + " " + _table.ScheduleName) + ".pdf",
                OverwritePrompt = true
            };
            var dirs = RevitContext.Settings.ScheduleSnapPdfDir;
            dlg.InitialDirectory = !string.IsNullOrEmpty(dirs) && Directory.Exists(dirs)
                ? dirs
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            if (dlg.ShowDialog() != true) return;
            var path = dlg.FileName;

            var handler = RevitContext.ScheduleSnapshotHandler;
            var ev = RevitContext.ScheduleSnapshotEvent;
            if (handler == null || ev == null)
            {
                PrintStatus.Text = "Обработчик Revit недоступен.";
                return;
            }

            try
            {
                SheetPrintBtn.IsEnabled = false;
                PrintBtn.IsEnabled = false;
                PrintStatus.Text = "Формирование листа в Revit…";
                var headers = GridTable.Columns
                    .Select(c => ((DataGridTextColumn)c).Header?.ToString() ?? string.Empty).ToList();
                var rows = _rows.Select(row => row.Cells.ToArray()).ToList();

                handler.QueuePrintEdited(_selected.SheetId, _selected.ScheduleId, headers, rows, path, _isFree, res =>
                {
                    SheetPrintBtn.IsEnabled = _selected != null;
                    PrintBtn.IsEnabled = _rows.Count > 0;
                    if (!string.IsNullOrEmpty(res.Error))
                    {
                        PrintStatus.Text = res.Error;
                        MessageBox.Show(this, res.Error, "Снимок спецификаций",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    RevitContext.Settings.ScheduleSnapPdfDir = Path.GetDirectoryName(path) ?? string.Empty;
                    try { RevitContext.SaveSettings(); } catch (Exception ex) { GrdLog.Log("ScheduleSnapshotWindow.SaveSettings EXCEPTION " + ex); }
                    PrintStatus.Text = "Лист сохранён в PDF: " + path;
                    try { System.Diagnostics.Process.Start(path); }
                    catch (Exception ex) { GrdLog.Log("ScheduleSnapshotWindow: открыть PDF листа EXCEPTION " + ex); }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                SheetPrintBtn.IsEnabled = _selected != null;
                GrdLog.Log("ScheduleSnapshotWindow.OnPrintSheet EXCEPTION: " + ex);
                PrintStatus.Text = "Не удалось запустить печать листа: " + ex.Message;
            }
        }

        /// <summary>Печать всех отмеченных листов (форма ГОСТ) в один PDF-файл: для каждого листа
        /// берётся его отредактированный снимок спецификации (или данные модели), затем все листы
        /// экспортируются вместе.</summary>
        private void OnPrintSelected(object sender, RoutedEventArgs e)
        {
            var items = CheckedItems();
            if (items.Count == 0)
            {
                PrintStatus.Text = "Не отмечено ни одного листа.";
                return;
            }

            var handler = RevitContext.ScheduleSnapshotHandler;
            var ev = RevitContext.ScheduleSnapshotEvent;
            if (handler == null || ev == null)
            {
                PrintStatus.Text = "Обработчик Revit недоступен.";
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить выбранные листы (форма ГОСТ) в один PDF",
                Filter = "PDF (*.pdf)|*.pdf",
                DefaultExt = ".pdf",
                AddExtension = true,
                FileName = "Листы (ГОСТ).pdf",
                OverwritePrompt = true
            };
            var dirs = RevitContext.Settings.ScheduleSnapPdfDir;
            dlg.InitialDirectory = !string.IsNullOrEmpty(dirs) && Directory.Exists(dirs)
                ? dirs
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            if (dlg.ShowDialog() != true) return;
            var path = dlg.FileName;

            try
            {
                SaveCurrentEdits();
                MultiPrintBtn.IsEnabled = false;
                PrintBtn.IsEnabled = false;
                SheetPrintBtn.IsEnabled = false;
                PrintStatus.Text = "Формирование " + items.Count + " листов в Revit…";

                handler.QueuePrintMany(items, path, res =>
                {
                    UpdateCheckedUi();
                    PrintBtn.IsEnabled = _rows.Count > 0;
                    SheetPrintBtn.IsEnabled = _selected != null;
                    if (!string.IsNullOrEmpty(res.Error))
                    {
                        PrintStatus.Text = res.Error;
                        MessageBox.Show(this, res.Error, "Снимок спецификаций",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    RevitContext.Settings.ScheduleSnapPdfDir = Path.GetDirectoryName(path) ?? string.Empty;
                    try { RevitContext.SaveSettings(); } catch (Exception ex) { GrdLog.Log("ScheduleSnapshotWindow.SaveSettings EXCEPTION " + ex); }
                    PrintStatus.Text = "Листов сохранено: " + items.Count + " → " + path;
                    try { System.Diagnostics.Process.Start(path); }
                    catch (Exception ex) { GrdLog.Log("ScheduleSnapshotWindow: открыть PDF пакета EXCEPTION " + ex); }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                UpdateCheckedUi();
                PrintBtn.IsEnabled = _rows.Count > 0;
                SheetPrintBtn.IsEnabled = _selected != null;
                GrdLog.Log("ScheduleSnapshotWindow.OnPrintSelected EXCEPTION: " + ex);
                PrintStatus.Text = "Не удалось запустить печать: " + ex.Message;
            }
        }

        /// <summary>Экспорт всех отмеченных листов в один файл Excel: каждая спецификация —
        /// отдельный лист книги, данные — с учётом сохранённых правок. Модель не изменяется.</summary>
        private void OnExportExcel(object sender, RoutedEventArgs e)
        {
            var items = CheckedItems();
            if (items.Count == 0)
            {
                PrintStatus.Text = "Не отмечено ни одного листа.";
                return;
            }

            var handler = RevitContext.ScheduleSnapshotHandler;
            var ev = RevitContext.ScheduleSnapshotEvent;
            if (handler == null || ev == null)
            {
                PrintStatus.Text = "Обработчик Revit недоступен.";
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить выбранные спецификации в один файл Excel",
                Filter = "Excel (*.xlsx)|*.xlsx",
                DefaultExt = ".xlsx",
                AddExtension = true,
                FileName = "Снимок спецификаций.xlsx",
                OverwritePrompt = true
            };
            var dirs = RevitContext.Settings.ScheduleSnapPdfDir;
            dlg.InitialDirectory = !string.IsNullOrEmpty(dirs) && Directory.Exists(dirs)
                ? dirs
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            if (dlg.ShowDialog() != true) return;
            var path = dlg.FileName;

            try
            {
                SaveCurrentEdits();
                ExportExcelBtn.IsEnabled = false;
                PrintStatus.Text = "Экспорт " + items.Count + " спецификаций в Excel…";

                handler.QueueExportExcel(items, path, res =>
                {
                    UpdateCheckedUi();
                    if (!string.IsNullOrEmpty(res.Error))
                    {
                        PrintStatus.Text = res.Error;
                        MessageBox.Show(this, res.Error, "Снимок спецификаций",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    RevitContext.Settings.ScheduleSnapPdfDir = Path.GetDirectoryName(path) ?? string.Empty;
                    try { RevitContext.SaveSettings(); } catch (Exception ex) { GrdLog.Log("ScheduleSnapshotWindow.SaveSettings EXCEPTION " + ex); }
                    PrintStatus.Text = "Экспортировано спецификаций: " + items.Count + " → " + path;
                    try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); }
                    catch (Exception ex) { GrdLog.Log("ScheduleSnapshotWindow: показать Excel EXCEPTION " + ex); }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                UpdateCheckedUi();
                GrdLog.Log("ScheduleSnapshotWindow.OnExportExcel EXCEPTION: " + ex);
                PrintStatus.Text = "Не удалось запустить экспорт: " + ex.Message;
            }
        }

        /// <summary>Экспорт текущей (отредактированной) таблицы снимка в CSV для Excel.</summary>
        private void OnExportCsv(object sender, RoutedEventArgs e)
        {
            if (_table == null || _rows.Count == 0)
            {
                PrintStatus.Text = "Нет данных для экспорта.";
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Экспорт таблицы снимка в CSV",
                Filter = "CSV (*.csv)|*.csv",
                DefaultExt = ".csv",
                AddExtension = true,
                FileName = SafeFileName(_table.ScheduleName) + ".csv",
                OverwritePrompt = true
            };
            var dirs = RevitContext.Settings.ScheduleSnapPdfDir;
            dlg.InitialDirectory = !string.IsNullOrEmpty(dirs) && Directory.Exists(dirs)
                ? dirs
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            if (dlg.ShowDialog() != true) return;
            var path = dlg.FileName;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine(string.Join(";", _table.Headers.Select(Csv)));
                foreach (var row in _rows)
                    sb.AppendLine(string.Join(";", row.Cells.ToArray().Select(Csv)));
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));

                RevitContext.Settings.ScheduleSnapPdfDir = Path.GetDirectoryName(path) ?? string.Empty;
                try { RevitContext.SaveSettings(); } catch (Exception ex) { GrdLog.Log("ScheduleSnapshotWindow.SaveSettings EXCEPTION " + ex); }

                PrintStatus.Text = "CSV сохранён: " + path;
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); }
                catch (Exception ex) { GrdLog.Log("ScheduleSnapshotWindow: показать CSV EXCEPTION " + ex); }
            }
            catch (Exception ex)
            {
                GrdLog.Log("ScheduleSnapshotWindow.OnExportCsv EXCEPTION: " + ex);
                PrintStatus.Text = "Не удалось сохранить CSV: " + ex.Message;
                MessageBox.Show(this, "Не удалось сохранить CSV:\n\n" + ex.Message,
                    "Снимок спецификаций", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.IndexOf(';') >= 0 || value.IndexOf('"') >= 0 ||
                value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0 || value.IndexOf('\t') >= 0)
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        /// <summary>Создание свободной спецификации: диалог выбора образца, затем запрос к Revit.</summary>
        private void OnCreateFree(object sender, RoutedEventArgs e)
        {
            var templates = _master.Where(x => !x.IsFree).ToList();
            if (templates.Count == 0)
            {
                MessageBox.Show(this,
                    "В проекте нет спецификаций на листах, которые можно взять за образец заголовков.",
                    "Свободная спецификация", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new NewFreeScheduleWindow(templates, NextSheetNumber(), "Свободная спецификация")
            {
                Owner = this
            };
            if (dlg.ShowDialog() != true || dlg.SelectedTemplate == null) return;

            var handler = RevitContext.ScheduleSnapshotHandler;
            var ev = RevitContext.ScheduleSnapshotEvent;
            if (handler == null || ev == null)
            {
                PrintStatus.Text = "Обработчик Revit недоступен.";
                return;
            }

            try
            {
                CreateFreeBtn.IsEnabled = false;
                PrintStatus.Text = "Создание свободной спецификации…";
                handler.QueueCreateFree(dlg.SelectedTemplate.ScheduleId, dlg.SheetNumber, dlg.SheetName, res =>
                {
                    CreateFreeBtn.IsEnabled = true;
                    if (!string.IsNullOrEmpty(res.Error))
                    {
                        PrintStatus.Text = res.Error;
                        MessageBox.Show(this, res.Error, "Свободная спецификация",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    PrintStatus.Text = "Свободная спецификация создана на новом листе.";
                    _pendingSelectScheduleId = res.NewScheduleId;
                    _pendingSelectSheetId = res.NewSheetId;
                    LoadList();
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                CreateFreeBtn.IsEnabled = true;
                GrdLog.Log("ScheduleSnapshotWindow.OnCreateFree EXCEPTION: " + ex);
                PrintStatus.Text = "Не удалось создать свободную спецификацию: " + ex.Message;
            }
        }

        /// <summary>После перестройки списка выбрать только что созданную спецификацию.</summary>
        private void SelectPending()
        {
            if (_pendingSelectScheduleId == 0) return;
            var it = _master.FirstOrDefault(x => x.ScheduleId == _pendingSelectScheduleId &&
                                                 (x.SheetId == _pendingSelectSheetId || _pendingSelectSheetId == 0));
            _pendingSelectScheduleId = 0;
            _pendingSelectSheetId = 0;
            if (it == null) return;
            if (!_visible.Contains(it) && SearchBox != null) SearchBox.Clear();
            SheetsList.SelectedItem = it;
            if (SheetsList.SelectedItem != it) LoadTable(it);
        }

        private string NextSheetNumber()
        {
            int max = 0;
            foreach (var s in _master)
            {
                var num = s.SheetNumber ?? string.Empty;
                int i = num.Length;
                while (i > 0 && char.IsDigit(num[i - 1])) i--;
                if (i < num.Length && int.TryParse(num.Substring(i), out var n) && n > max) max = n;
            }
            return (max + 1).ToString();
        }

        private static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Спецификация";
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Where(ch => !invalid.Contains(ch)).ToArray();
            var s = new string(chars);
            return s.Trim().Length == 0 ? "Спецификация" : s.Trim();
        }
    }
}
