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
using System.Windows.Controls.Primitives;
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
    /// в модель не пишутся. ChangedMask — битовая маска ячеек, отличающихся от данных
    /// модели; BoldMask — маска жирных ячеек; MatchMask — маска ячеек, найденных поиском;
    /// SourceIndex — номер строки-источника в модели (-1 у добавленной строки);
    /// IsDeleted — строка помечена удалённой (показывается зачёркнутой, но не выгружается).</summary>
    public sealed class SnapshotRow : INotifyPropertyChanged
    {
        private long _changed;
        private long _bold;
        private long _match;
        private bool _deleted;
        private bool _added;

        public SnapshotRow(string[] cells) { Cells = new CellCollection(cells); }

        public CellCollection Cells { get; }

        /// <summary>Спецификация (запись), которой принадлежит строка.</summary>
        public ScheduleEntry Owner { get; set; }

        /// <summary>Номер строки-источника в данных модели (-1 — строка добавлена пользователем).</summary>
        public int SourceIndex { get; set; } = -1;

        public long ChangedMask
        {
            get { return _changed; }
            private set { if (_changed == value) return; _changed = value; Notify(nameof(ChangedMask)); }
        }

        public long BoldMask
        {
            get { return _bold; }
            set { if (_bold == value) return; _bold = value; Notify(nameof(BoldMask)); }
        }

        public long MatchMask
        {
            get { return _match; }
            private set { if (_match == value) return; _match = value; Notify(nameof(MatchMask)); }
        }

        public bool IsDeleted
        {
            get { return _deleted; }
            set { if (_deleted == value) return; _deleted = value; Notify(nameof(IsDeleted)); }
        }

        /// <summary>Строка добавлена пользователем (нет источника в модели) — подсвечивается зелёным.</summary>
        public bool MarkAdded
        {
            get { return _added; }
            set { if (_added == value) return; _added = value; Notify(nameof(IsAdded)); }
        }

        public bool IsAdded => _added;

        public bool IsGroupStart => false;

        public string GroupLabel => string.Empty;

        public void SetChanged(int index, bool changed)
        {
            if (index < 0 || index > 62) return;
            long bit = 1L << index;
            ChangedMask = changed ? (_changed | bit) : (_changed & ~bit);
        }

        public void ClearChanged() { ChangedMask = 0; }

        public void ClearMatch() { MatchMask = 0; }

        public void SetMatch(int index, bool match)
        {
            if (index < 0 || index > 62) return;
            long bit = 1L << index;
            MatchMask = match ? (_match | bit) : (_match & ~bit);
        }

        public bool IsBold(int index) { return index >= 0 && index < 62 && (_bold & (1L << index)) != 0L; }

        public void ToggleBold(int index)
        {
            if (index < 0 || index > 62) return;
            BoldMask = IsBold(index) ? (_bold & ~(1L << index)) : (_bold | (1L << index));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Notify(string name) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
    }

    /// <summary>Одна редактируемая спецификация (запись снимка): заголовки, строки модели
    /// и строки с правками. Объекты строк общие для представлений «По листам» и «Всё»,
    /// поэтому правки синхронны в обоих.</summary>
    public sealed class ScheduleEntry
    {
        public string Key = string.Empty;
        public long ScheduleId;
        public long SheetId;
        public int SegmentIndex;
        public string ScheduleName = string.Empty;
        public string SheetNumber = string.Empty;
        public string SheetName = string.Empty;
        public bool IsFree;
        public long TemplateScheduleId;
        public double SheetWidthMm;
        public double SheetHeightMm;

        public List<string> Headers = new List<string>();
        public List<string> ModelHeaders = new List<string>();
        public List<string[]> ModelRows; // null — у свободной спецификации
        public ObservableCollection<SnapshotRow> Rows = new ObservableCollection<SnapshotRow>();
        public bool Dirty;

        public string GroupLabel =>
            "Лист " + (string.IsNullOrWhiteSpace(SheetNumber) ? "—" : SheetNumber) +
            (string.IsNullOrWhiteSpace(SheetName) ? string.Empty : " · " + SheetName) +
            " — «" + ScheduleName + "»";

        /// <summary>Пересчитывает подсветку: изменённые ячейки (жёлтый), добавленные строки
        /// (зелёный). Строки удалённые/добавленные не сравниваются поячеечно.</summary>
        public void RecomputeChanged()
        {
            foreach (var row in Rows)
            {
                if (IsFree || row.IsDeleted || row.SourceIndex < 0)
                {
                    row.ClearChanged();
                    row.MarkAdded = !IsFree && !row.IsDeleted && row.SourceIndex < 0;
                    continue;
                }
                row.MarkAdded = false;
                for (int c = 0; c < row.Cells.Length; c++)
                {
                    bool changed;
                    if (ModelRows != null && row.SourceIndex < ModelRows.Count &&
                        c < ModelRows[row.SourceIndex].Length)
                        changed = !string.Equals(row.Cells[c], ModelRows[row.SourceIndex][c], StringComparison.Ordinal);
                    else
                        changed = true;
                    row.SetChanged(c, changed);
                }
            }
        }

        public SerializedSchedule ToSerialized()
        {
            var s = new SerializedSchedule
            {
                ScheduleId = ScheduleId,
                ScheduleName = ScheduleName,
                IsFree = IsFree,
                TemplateScheduleId = TemplateScheduleId,
                SheetId = SheetId,
                SegmentIndex = SegmentIndex
            };
            s.Headers.AddRange(Headers);
            foreach (var row in Rows)
            {
                s.Rows.Add(row.Cells.ToArray());
                s.Sources.Add(row.SourceIndex);
                s.Bolds.Add(row.BoldMask);
                s.Deleted.Add(row.IsDeleted);
            }
            return s;
        }
    }

    /// <summary>Строка объединённого представления «Всё»: оборачивает исходную строку и
    /// проецирует её ячейки/маски на объединённый набор колонок (Map: колонка вида ->
    /// колонка источника, -1 если колонки нет).</summary>
    public sealed class AllRow : INotifyPropertyChanged
    {
        private readonly int[] _map;
        private readonly long _foreign;

        public AllRow(SnapshotRow source, int[] map)
        {
            Source = source;
            _map = map ?? new int[0];
            for (int i = 0; i < _map.Length && i < 62; i++)
                if (_map[i] < 0) _foreign |= 1L << i;
            Source.PropertyChanged += (s, e) => RaiseAll();
        }

        public SnapshotRow Source { get; }

        public bool IsGroupStart { get; set; }
        public string GroupLabel { get; set; } = string.Empty;

        public string this[int index]
        {
            get { int s = Src(index); return s < 0 ? null : Source.Cells[s]; }
            set { int s = Src(index); if (s >= 0) Source.Cells[s] = value; }
        }

        public int SourceColumn(int viewIndex) { return Src(viewIndex); }

        private int Src(int i) { return i >= 0 && i < _map.Length ? _map[i] : -1; }

        public long ViewChanged => Remap(Source.ChangedMask);
        public long ViewBold => Remap(Source.BoldMask);
        public long ViewMatch => Remap(Source.MatchMask);

        /// <summary>Биты колонок объединённого вида, которых нет у этой спецификации
        /// (не редактируются — для них нет исходной колонки).</summary>
        public long ViewForeign => _foreign;

        private long Remap(long mask)
        {
            if (mask == 0) return 0;
            long r = 0;
            for (int i = 0; i < _map.Length && i < 62; i++)
            {
                int s = _map[i];
                if (s >= 0 && s < 62 && (mask & (1L << s)) != 0L) r |= (1L << i);
            }
            return r;
        }

        public bool IsAdded => Source.IsAdded;
        public bool IsDeleted => Source.IsDeleted;

        public string[] ToUnionArray()
        {
            var a = new string[_map.Length];
            for (int i = 0; i < _map.Length; i++) a[i] = this[i];
            return a;
        }

        private void RaiseAll()
        {
            var pc = PropertyChanged;
            if (pc == null) return;
            pc(this, new PropertyChangedEventArgs("Item[]"));
            pc(this, new PropertyChangedEventArgs(nameof(ViewChanged)));
            pc(this, new PropertyChangedEventArgs(nameof(ViewBold)));
            pc(this, new PropertyChangedEventArgs(nameof(ViewMatch)));
            pc(this, new PropertyChangedEventArgs(nameof(IsAdded)));
            pc(this, new PropertyChangedEventArgs(nameof(IsDeleted)));
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>Преобразует битовую маску в bool для конкретной колонки
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
    /// справа — редактируемая таблица-снимок. Две вкладки: «По листам» (одна выбранная
    /// спецификация) и «Всё» (все избранные спецификации одной таблицей, листы разделены
    /// линиями). Правки синхронны, хранятся только в плагине. «Печать в PDF» сохраняет
    /// текущий вид, включая жирный текст.
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
            double h = (bool)e.NewValue ? double.NaN : 30;
            w.GridTable.RowHeight = h;
            if (w.AllTable != null) w.AllTable.RowHeight = h;
        }

        private const int UndoLimit = 100;

        private static readonly Brush ChangedCellBrush = MakeBrush(Color.FromRgb(255, 244, 204));
        private static readonly Brush MatchBorderBrush = MakeBrush(Color.FromRgb(70, 130, 220));
        private static readonly Brush DeletedTextBrush = MakeBrush(Color.FromRgb(150, 40, 40));
        private static readonly Brush ForeignCellBrush = MakeBrush(Color.FromRgb(240, 240, 240));
        private static readonly Brush SelectedCellBrush = MakeBrush(Color.FromRgb(221, 231, 251));
        private const string ForeignCellToolTip = "У этой спецификации нет такой колонки — значение нельзя изменить.";

        private static Brush MakeBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // ---------------------------------------------------------------- состояние

        private sealed class RowState
        {
            public string[] Cells;
            public int Source;
            public long Bold;
            public bool Deleted;
        }

        private sealed class EntryState
        {
            public string Key;
            public List<RowState> Rows = new List<RowState>();
        }

        private sealed class WindowSnapshot
        {
            public List<EntryState> Entries = new List<EntryState>();
        }

        private readonly List<SheetScheduleInfo> _master = new List<SheetScheduleInfo>();
        private readonly ObservableCollection<SheetScheduleInfo> _visible = new ObservableCollection<SheetScheduleInfo>();
        private readonly ObservableCollection<AllRow> _allRows = new ObservableCollection<AllRow>();
        private readonly Dictionary<string, ScheduleEntry> _entries = new Dictionary<string, ScheduleEntry>(StringComparer.Ordinal);
        private readonly List<WindowSnapshot> _undo = new List<WindowSnapshot>();
        private readonly List<WindowSnapshot> _redo = new List<WindowSnapshot>();
        private readonly List<string> _allHeaders = new List<string>();

        private SheetScheduleInfo _selected;
        private ScheduleEntry _currentEntry;
        private long _pendingSelectScheduleId;
        private long _pendingSelectSheetId;
        private WindowSnapshot _pendingUndoSnapshot;
        private bool _suppressSel;
        private bool _loadingList;
        private bool _initialized;
        private string _docKey = string.Empty;
        private readonly HashSet<string> _favIds = new HashSet<string>(StringComparer.Ordinal);

        private bool IsAllView => ViewTabs != null && ViewTabs.SelectedIndex == 1;
        private DataGrid ActiveGrid => IsAllView ? AllTable : GridTable;

        public ScheduleSnapshotWindow()
        {
            InitializeComponent();
            DataContext = this;
            WindowTopmost.Track(this);
            SheetsList.ItemsSource = _visible;
            GridTable.ItemsSource = null;
            AllTable.ItemsSource = _allRows;
            GridTable.RowHeight = double.NaN;
            AllTable.RowHeight = double.NaN;
            EmptyHint.Visibility = Visibility.Visible;
            AllEmptyHint.Visibility = Visibility.Visible;
            PrintBtn.IsEnabled = false;
            SheetPrintBtn.IsEnabled = false;
            ScheduleLabel.Text = "Спецификация не выбрана.";
            GridTable.PreviewMouseLeftButtonDown += OnSheetGridHeaderMouseDown;

            UpdateCheckedUi();
            UpdateUndoUi();
            UpdateRowUi();
            UpdateButtonsForView();

            _initialized = true;

            Closing += (s, e) => SaveAllDirty();

            Loaded += (s, e) =>
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
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
            if (_loadingList) return;
            _loadingList = true;
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
                        _loadingList = false;
                    }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                _loadingList = false;
                PrintStatus.Text = "Не удалось запросить список: " + ex.Message;
            }
        }

        private void UpdateSheetCount()
        {
            SheetCount.Text = "На листах: " + _master.Count + " спецификаци" +
                              (_master.Count % 10 == 1 && _master.Count % 100 != 11 ? "я"
                                  : (_master.Count % 10 >= 2 && _master.Count % 10 <= 4 && (_master.Count % 100 < 12 || _master.Count % 100 > 14) ? "и" : "й"));
        }

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
            if (IsAllView) EnsureFavouritesLoaded(RebuildAllView);
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
                    ClearCurrentEntry();
                }
            }
            finally
            {
                _suppressSel = false;
            }
            UpdateCheckedUi();
        }

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
                if (_selected != null && _selected != item && _currentEntry != null)
                    SaveEntry(_currentEntry);
                _selected = item;
                LoadSheetEntry(item);
            }
            else if (e.RemovedItems.Count > 0)
            {
                if (_currentEntry != null) SaveEntry(_currentEntry);
                _selected = null;
                ClearCurrentEntry();
            }
        }

        // ------------------------------------------------------------------ Загрузка спецификации

        private ScheduleEntry FindEntry(long scheduleId, long sheetId)
        {
            foreach (var e in _entries.Values)
                if (e.ScheduleId == scheduleId && e.SheetId == sheetId) return e;
            return null;
        }

        private static string EntryKey(long scheduleId, long sheetId, int segmentIndex)
        {
            return scheduleId.ToString(CultureInfo.InvariantCulture) + ":" +
                   sheetId.ToString(CultureInfo.InvariantCulture) + ":" +
                   segmentIndex.ToString(CultureInfo.InvariantCulture);
        }

        private void LoadSheetEntry(SheetScheduleInfo info)
        {
            var existing = FindEntry(info.ScheduleId, info.SheetId);
            if (existing != null)
            {
                SetCurrentEntry(existing);
                return;
            }

            var handler = RevitContext.ScheduleSnapshotHandler;
            var ev = RevitContext.ScheduleSnapshotEvent;
            if (handler == null || ev == null) return;

            PrintBtn.IsEnabled = false;
            ScheduleLabel.Text = "Лист " + (string.IsNullOrWhiteSpace(info.SheetNumber) ? "—" : info.SheetNumber) +
                                 (string.IsNullOrWhiteSpace(info.SheetName) ? "" : " · " + info.SheetName) +
                                 " — «" + info.ScheduleName + "»";
            PrintStatus.Text = "Чтение из модели…";
            try
            {
                handler.QueueReadTable(info.SheetId, info.ScheduleId, res =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(res.Error))
                        {
                            PrintStatus.Text = res.Error;
                            ClearCurrentEntry();
                            return;
                        }
                        if (!string.IsNullOrEmpty(res.DocKey)) _docKey = res.DocKey;
                        var entry = BuildEntry(res.Table, info);
                        _entries[entry.Key] = entry;
                        SetCurrentEntry(entry);
                        if (entry.IsFree)
                            PrintStatus.Text = "Свободная спецификация · строк: " + entry.Rows.Count +
                                               " · данные хранятся отдельно от модели.";
                        else if (res.Table.Warning.Length > 0)
                            PrintStatus.Text = res.Table.Warning;
                        else if (entry.Dirty)
                            PrintStatus.Text = "Восстановлены сохранённые правки. «Сбросить правки» вернёт данные модели.";
                        else
                            PrintStatus.Text = "Заголовков: " + entry.Headers.Count +
                                               " · строк: " + entry.Rows.Count +
                                               " · правки только в этом окне, в модель они не пишутся.";
                    }
                    catch (Exception ex)
                    {
                        PrintStatus.Text = "Ошибка отображения: " + ex.Message;
                    }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                PrintStatus.Text = "Не удалось запустить чтение: " + ex.Message;
            }
        }

        /// <summary>Строит запись спецификации из прочитанной таблицы модели и сохранённых правок.</summary>
        private ScheduleEntry BuildEntry(ScheduleTable table, SheetScheduleInfo info)
        {
            var entry = new ScheduleEntry
            {
                ScheduleId = table.ScheduleId,
                SheetId = table.SheetId,
                SegmentIndex = table.SegmentIndex,
                ScheduleName = table.ScheduleName,
                SheetNumber = info?.SheetNumber ?? table.SheetNumber,
                SheetName = info?.SheetName ?? table.SheetName,
                IsFree = table.IsFree,
                TemplateScheduleId = table.TemplateScheduleId,
                SheetWidthMm = table.SheetWidthMm,
                SheetHeightMm = table.SheetHeightMm
            };
            entry.Key = EntryKey(entry.ScheduleId, entry.SheetId, entry.SegmentIndex);
            entry.Headers = new List<string>(table.Headers);
            entry.ModelHeaders = new List<string>(table.Headers);
            entry.ModelRows = entry.IsFree ? null : table.Rows.Select(r => (string[])r.Clone()).ToList();

            for (int i = 0; i < table.Rows.Count; i++)
                entry.Rows.Add(new SnapshotRow((string[])table.Rows[i].Clone())
                {
                    Owner = entry,
                    SourceIndex = entry.IsFree ? -1 : i
                });

            var saved = string.IsNullOrEmpty(_docKey) ? null :
                ScheduleSnapshotsFile.LoadEntry(_docKey, table.ScheduleId, table.SheetId, table.SegmentIndex);
            if (saved != null && saved.Headers.Count == entry.Headers.Count)
                ApplySaved(entry, saved);

            entry.RecomputeChanged();
            return entry;
        }

        /// <summary>Подставляет сохранённые правки (строки, добавленные/удалённые строки,
        /// жирный текст, переименованные заголовки).</summary>
        private static void ApplySaved(ScheduleEntry entry, SerializedSchedule saved)
        {
            entry.Headers = new List<string>(saved.Headers);
            entry.Rows.Clear();
            for (int r = 0; r < saved.Rows.Count; r++)
            {
                var src = saved.Rows[r] ?? new string[0];
                var cells = new string[entry.Headers.Count];
                for (int c = 0; c < cells.Length && c < src.Length; c++) cells[c] = src[c];
                entry.Rows.Add(new SnapshotRow(cells)
                {
                    Owner = entry,
                    SourceIndex = r < saved.Sources.Count ? saved.Sources[r] : (entry.IsFree ? -1 : r),
                    IsDeleted = r < saved.Deleted.Count && saved.Deleted[r],
                    BoldMask = r < saved.Bolds.Count ? saved.Bolds[r] : 0L
                });
            }
            entry.Dirty = true;
        }

        private void SetCurrentEntry(ScheduleEntry entry)
        {
            _currentEntry = entry;
            GridTable.ItemsSource = entry.Rows;
            BuildSheetColumns(entry);
            var view = CollectionViewSource.GetDefaultView(entry.Rows);
            if (view != null) { view.Filter = null; }
            ApplySearchToSheet();
            UpdateSheetLabel(entry);
            UpdateUndoUi();
            UpdateRowUi();
            UpdateButtonsForView();
        }

        private void UpdateSheetLabel(ScheduleEntry entry)
        {
            ScheduleLabel.Text = "Лист " + (string.IsNullOrWhiteSpace(entry.SheetNumber) ? "—" : entry.SheetNumber) +
                                 (string.IsNullOrWhiteSpace(entry.SheetName) ? "" : " · " + entry.SheetName) +
                                 " — «" + entry.ScheduleName + "»";
        }

        private void ClearCurrentEntry()
        {
            _currentEntry = null;
            GridTable.ItemsSource = null;
            GridTable.Columns.Clear();
            _undo.Clear();
            _redo.Clear();
            _pendingUndoSnapshot = null;
            PrintBtn.IsEnabled = false;
            SheetPrintBtn.IsEnabled = false;
            ScheduleLabel.Text = "Спецификация не выбрана.";
            UpdateUndoUi();
            UpdateRowUi();
            UpdateButtonsForView();
        }

        /// <summary>Записывает правки записи на диск (JSON).</summary>
        private void SaveEntry(ScheduleEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(_docKey)) return;
            try { ScheduleSnapshotsFile.SaveEntry(_docKey, entry.ToSerialized()); }
            catch { }
        }

        private void SaveAllDirty()
        {
            foreach (var e in _entries.Values)
                if (e.Dirty) SaveEntry(e);
        }

        // ------------------------------------------------------------------ Вкладка «Всё»

        private void OnViewTabChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized) return;
            UpdateRowUi();
            UpdateButtonsForView();
            if (IsAllView)
                EnsureFavouritesLoaded(RebuildAllView);
            else
                ApplySearchToSheet();
            ApplySearch();
        }

        private void UpdateButtonsForView()
        {
            bool all = IsAllView;
            bool hasEntry = _currentEntry != null;
            if (RefreshBtn != null) RefreshBtn.IsEnabled = !all && hasEntry;
            if (SheetPrintBtn != null) SheetPrintBtn.IsEnabled = !all && hasEntry && _currentEntry.Rows.Count > 0;
            if (ResetBtn != null) ResetBtn.IsEnabled = !all && hasEntry &&
                                                        (_currentEntry.ModelRows != null || _currentEntry.IsFree);
            if (PrintBtn != null) PrintBtn.IsEnabled = all ? _allRows.Count > 0 : hasEntry && _currentEntry.Rows.Count > 0;
            if (ExportBtn != null) ExportBtn.IsEnabled = all ? _allRows.Count > 0 : hasEntry && _currentEntry.Rows.Count > 0;
            if (BoldBtn != null) BoldBtn.IsEnabled = all ? _allRows.Count > 0 : hasEntry && _currentEntry.Rows.Count > 0;
        }

        /// <summary>Гарантирует, что все избранные спецификации прочитаны, затем вызывает done().</summary>
        private void EnsureFavouritesLoaded(Action done)
        {
            var missing = _master.Where(f => f.IsFavorite && FindEntry(f.ScheduleId, f.SheetId) == null).ToList();
            if (missing.Count == 0) { done(); return; }

            var handler = RevitContext.ScheduleSnapshotHandler;
            var ev = RevitContext.ScheduleSnapshotEvent;
            if (handler == null || ev == null) { done(); return; }

            int remaining = missing.Count;
            foreach (var info in missing)
            {
                handler.QueueReadTable(info.SheetId, info.ScheduleId, res =>
                {
                    try
                    {
                        if (string.IsNullOrEmpty(res.Error) && res.Table != null)
                        {
                            var entry = BuildEntry(res.Table, info);
                            if (!_entries.ContainsKey(entry.Key)) _entries[entry.Key] = entry;
                        }
                    }
                    finally
                    {
                        remaining--;
                        if (remaining == 0) done();
                    }
                });
            }
            try { ev.Raise(); } catch { done(); }
        }

        /// <summary>Собирает объединённую таблицу из всех избранных спецификаций.</summary>
        private void RebuildAllView()
        {
            _allRows.Clear();
            _allHeaders.Clear();

            var entries = _master.Where(x => x.IsFavorite)
                .Select(x => FindEntry(x.ScheduleId, x.SheetId))
                .Where(e => e != null).ToList();

            bool same = entries.Count > 0;
            for (int i = 1; i < entries.Count; i++)
                if (!HeadersEqual(entries[i].Headers, entries[0].Headers)) { same = false; break; }

            if (same)
            {
                _allHeaders.AddRange(entries[0].Headers);
            }
            else
            {
                var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var e in entries)
                    foreach (var h in e.Headers)
                    {
                        var key = h ?? string.Empty;
                        if (!indexByName.ContainsKey(key)) { indexByName[key] = _allHeaders.Count; _allHeaders.Add(key); }
                    }
            }

            BuildAllColumns();

            foreach (var entry in entries)
            {
                var map = BuildMap(_allHeaders, entry.Headers, same);
                bool groupStart = true;
                foreach (var row in entry.Rows)
                {
                    _allRows.Add(new AllRow(row, map)
                    {
                        IsGroupStart = groupStart,
                        GroupLabel = groupStart ? entry.GroupLabel : string.Empty
                    });
                    groupStart = false;
                }
            }

            AllEmptyHint.Visibility = _allRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AllTable.Visibility = _allRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ApplySearchToAll();
            UpdateButtonsForView();
        }

        private static int[] BuildMap(List<string> union, List<string> entryHeaders, bool same)
        {
            var map = new int[union.Count];
            if (same)
            {
                for (int i = 0; i < map.Length; i++) map[i] = i < entryHeaders.Count ? i : -1;
                return map;
            }
            var lookup = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < entryHeaders.Count; i++)
                if (!lookup.ContainsKey(entryHeaders[i] ?? string.Empty)) lookup[entryHeaders[i] ?? string.Empty] = i;
            for (int i = 0; i < union.Count; i++)
                map[i] = lookup.TryGetValue(union[i] ?? string.Empty, out var s) ? s : -1;
            return map;
        }

        private static bool HeadersEqual(List<string> a, List<string> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>Находит запись для активного представления (текущую — для «По листам»,
        /// владельца выделенной строки — для «Всё»).</summary>
        private ScheduleEntry ActiveEntry()
        {
            if (!IsAllView) return _currentEntry;
            foreach (var ci in AllTable.SelectedCells)
                if (ci.Item is AllRow ar) return ar.Source.Owner;
            var first = _allRows.FirstOrDefault();
            return first?.Source.Owner;
        }

        // ------------------------------------------------------------------ Колонки

        private void BuildSheetColumns(ScheduleEntry entry)
        {
            GridTable.Columns.Clear();
            var rows = entry.Rows.Select(r => r.Cells.ToArray()).ToList();
            for (int i = 0; i < entry.Headers.Count; i++)
                GridTable.Columns.Add(MakeColumn(
                    HeaderText(entry.Headers[i], i),
                    "Cells[" + i + "]",
                    "ChangedMask", "BoldMask", "MatchMask",
                    null,
                    i, rows, true));
        }

        private void BuildAllColumns()
        {
            AllTable.Columns.Clear();
            var rows = _allRows.Select(a => a.ToUnionArray()).ToList();
            for (int i = 0; i < _allHeaders.Count; i++)
                AllTable.Columns.Add(MakeColumn(
                    HeaderText(_allHeaders[i], i),
                    "[" + i + "]",
                    "ViewChanged", "ViewBold", "ViewMatch",
                    "ViewForeign",
                    i, rows, true));
        }

        private static string HeaderText(string header, int i)
        {
            return string.IsNullOrWhiteSpace(header) ? "(колонка " + (i + 1) + ")" : header;
        }

        private DataGridTextColumn MakeColumn(string header, string cellPath,
                                              string changedProp, string boldProp, string matchProp,
                                              string foreignProp,
                                              int index, List<string[]> rows, bool editable)
        {
            var col = new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(cellPath)
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                },
                IsReadOnly = !editable
            };
            col.MinWidth = 60;
            col.Width = new DataGridLength(ColumnWidth(header, rows, index));
            col.ElementStyle = MakeColumnDisplayStyle();
            col.EditingElementStyle = MakeColumnEditStyle();
            col.CellStyle = MakeCellStyle(changedProp, boldProp, matchProp, foreignProp, index);
            return col;
        }

        private Style MakeCellStyle(string changedProp, string boldProp, string matchProp,
                                    string foreignProp, int index)
        {
            Style basedOn = null;
            try { basedOn = FindResource(typeof(DataGridCell)) as Style; } catch { }

            var st = basedOn != null ? new Style(typeof(DataGridCell), basedOn) : new Style(typeof(DataGridCell));

            var bold = new DataTrigger
            {
                Binding = new Binding(boldProp) { Converter = MaskBitConverter.Instance, ConverterParameter = index },
                Value = true
            };
            bold.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
            st.Triggers.Add(bold);

            var changed = new DataTrigger
            {
                Binding = new Binding(changedProp) { Converter = MaskBitConverter.Instance, ConverterParameter = index },
                Value = true
            };
            changed.Setters.Add(new Setter(DataGridCell.BackgroundProperty, ChangedCellBrush));
            st.Triggers.Add(changed);

            var match = new DataTrigger
            {
                Binding = new Binding(matchProp) { Converter = MaskBitConverter.Instance, ConverterParameter = index },
                Value = true
            };
            match.Setters.Add(new Setter(DataGridCell.BorderBrushProperty, MatchBorderBrush));
            match.Setters.Add(new Setter(DataGridCell.BorderThicknessProperty, new Thickness(1)));
            st.Triggers.Add(match);

            if (!string.IsNullOrEmpty(foreignProp))
            {
                // Колонка объединённого вида, которой нет у этой спецификации: WPF DataGrid
                // не умеет блокировать редактирование отдельной ячейки (IsReadOnly задаётся
                // колонкой), поэтому помечаем её визуально и объясняем в подсказке.
                var foreign = new DataTrigger
                {
                    Binding = new Binding(foreignProp) { Converter = MaskBitConverter.Instance, ConverterParameter = index },
                    Value = true
                };
                foreign.Setters.Add(new Setter(DataGridCell.BackgroundProperty, ForeignCellBrush));
                foreign.Setters.Add(new Setter(Control.ToolTipProperty, ForeignCellToolTip));
                st.Triggers.Add(foreign);

                // Выделение: иначе серый фон перекрывает цвет выделенного состояния
                // (базовый триггер Modern.xaml проигрывает триггерам производного стиля).
                var selected = new Trigger
                {
                    Property = DataGridCell.IsSelectedProperty,
                    Value = true
                };
                selected.Setters.Add(new Setter(DataGridCell.BackgroundProperty, SelectedCellBrush));
                st.Triggers.Add(selected);
            }

            return st;
        }

        private Style MakeColumnDisplayStyle()
        {
            var st = new Style(typeof(TextBlock));
            st.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
            st.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.None));
            var del = new DataTrigger { Binding = new Binding("IsDeleted"), Value = true };
            del.Setters.Add(new Setter(TextBlock.ForegroundProperty, DeletedTextBrush));
            del.Setters.Add(new Setter(TextBlock.TextDecorationsProperty, TextDecorations.Strikethrough));
            st.Triggers.Add(del);
            st.Triggers.Add(WrapTrigger<TextBlock>(TextBlock.TextWrappingProperty));
            return st;
        }

        private Style MakeColumnEditStyle()
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
                var row = rows[r];
                if (row == null || cIndex >= row.Length) continue;
                var cell = row[cIndex];
                if (cell == null) continue;
                if (cell.Length > 60) return 320;
                w = Math.Max(w, cell.Length * 7.2 + 16);
            }
            return Math.Min(w, 320);
        }

        // ------------------------------------------------------------------ Переименование колонок

        private void OnSheetGridHeaderMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;
            if (_currentEntry == null) return;
            var header = FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
            if (header == null || header.Column == null) return;
            int idx = header.Column.DisplayIndex;
            if (idx < 0 || idx >= _currentEntry.Headers.Count) return;
            BeginHeaderEdit(header, header.Column, idx);
            e.Handled = true;
        }

        private void BeginHeaderEdit(DataGridColumnHeader header, DataGridColumn column, int index)
        {
            var entry = _currentEntry;
            if (entry == null) return;
            string current = entry.Headers[index];
            var tb = new TextBox
            {
                Text = current ?? string.Empty,
                Padding = new Thickness(2, 0, 2, 0),
                MinWidth = 60,
                BorderBrush = MatchBorderBrush,
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center
            };

            bool done = false;
            Action commit = () =>
            {
                if (done) return;
                done = true;
                entry.Headers[index] = tb.Text ?? string.Empty;
                column.Header = HeaderText(entry.Headers[index], index);
                entry.Dirty = true;
                SaveEntry(entry);
                PrintStatus.Text = "Колонка переименована: «" + entry.Headers[index] + "».";
            };

            tb.KeyDown += (s, args) =>
            {
                if (args.Key == Key.Enter) { commit(); args.Handled = true; }
                else if (args.Key == Key.Escape) { done = true; column.Header = current; }
            };
            tb.LostFocus += (s, args) => commit();
            tb.Loaded += (s, args) => { tb.Focus(); tb.SelectAll(); };

            column.Header = tb;
        }

        private static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        // ------------------------------------------------------------------ Поиск (fuzzy)

        private void OnTableSearchChanged(object sender, RoutedEventArgs e)
        {
            ApplySearch();
        }

        private void OnTableSearchReset(object sender, RoutedEventArgs e)
        {
            TableSearchBox.Clear();
        }

        private void ApplySearch()
        {
            if (IsAllView) ApplySearchToAll();
            else ApplySearchToSheet();
        }

        private string SearchQuery()
        {
            return (TableSearchBox?.Text ?? string.Empty).Trim();
        }

        private void ApplySearchToSheet()
        {
            var entry = _currentEntry;
            if (entry == null) return;
            var q = SearchQuery();

            foreach (var row in entry.Rows)
            {
                row.ClearMatch();
                if (q.Length == 0) continue;
                for (int c = 0; c < row.Cells.Length; c++)
                    if (FuzzyMatch(row.Cells[c], q)) row.SetMatch(c, true);
            }

            var view = CollectionViewSource.GetDefaultView(entry.Rows);
            if (view != null)
            {
                bool filter = q.Length > 0 && TableFilterCheck != null && TableFilterCheck.IsChecked == true;
                view.Filter = filter ? (Predicate<object>)(o => o is SnapshotRow r && r.MatchMask != 0) : null;
                view.Refresh();
            }
        }

        private void ApplySearchToAll()
        {
            var q = SearchQuery();
            foreach (var ar in _allRows)
            {
                var row = ar.Source;
                row.ClearMatch();
                if (q.Length == 0) continue;
                for (int c = 0; c < row.Cells.Length; c++)
                    if (FuzzyMatch(row.Cells[c], q)) row.SetMatch(c, true);
            }

            var view = CollectionViewSource.GetDefaultView(_allRows);
            if (view != null)
            {
                bool filter = q.Length > 0 && TableFilterCheck != null && TableFilterCheck.IsChecked == true;
                view.Filter = filter ? (Predicate<object>)(o => o is AllRow a && a.ViewMatch != 0) : null;
                view.Refresh();
            }
        }

        /// <summary>Нечёткое сравнение: символы запроса должны встречаться в тексте по порядку
        /// (без учёта регистра). Пустой запрос совпадает со всем.</summary>
        private static bool FuzzyMatch(string text, string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            if (string.IsNullOrEmpty(text)) return false;
            int qi = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(query[qi]))
                {
                    qi++;
                    if (qi == query.Length) return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ Отмена / возврат

        private WindowSnapshot CaptureAll()
        {
            var snap = new WindowSnapshot();
            foreach (var entry in _entries.Values)
            {
                var es = new EntryState { Key = entry.Key };
                foreach (var row in entry.Rows)
                    es.Rows.Add(new RowState
                    {
                        Cells = row.Cells.ToArray(),
                        Source = row.SourceIndex,
                        Bold = row.BoldMask,
                        Deleted = row.IsDeleted
                    });
                snap.Entries.Add(es);
            }
            return snap;
        }

        private void ApplyWindowSnapshot(WindowSnapshot snap)
        {
            if (snap == null) return;
            foreach (var es in snap.Entries)
            {
                if (!_entries.TryGetValue(es.Key, out var entry)) continue;
                entry.Rows.Clear();
                foreach (var rs in es.Rows)
                    entry.Rows.Add(new SnapshotRow(rs.Cells)
                    {
                        Owner = entry,
                        SourceIndex = rs.Source,
                        BoldMask = rs.Bold,
                        IsDeleted = rs.Deleted
                    });
                entry.Dirty = true;
                entry.RecomputeChanged();
                SaveEntry(entry);
            }
            if (IsAllView) RebuildAllView();
            UpdateUndoUi();
            UpdateRowUi();
        }

        private static bool SnapshotsDiffer(WindowSnapshot a, WindowSnapshot b)
        {
            if (a == null || b == null) return true;
            if (a.Entries.Count != b.Entries.Count) return true;
            for (int i = 0; i < a.Entries.Count; i++)
            {
                var ea = a.Entries[i];
                var eb = b.Entries[i];
                if (ea.Key != eb.Key || ea.Rows.Count != eb.Rows.Count) return true;
                for (int r = 0; r < ea.Rows.Count; r++)
                {
                    var ra = ea.Rows[r];
                    var rb = eb.Rows[r];
                    if (ra.Source != rb.Source || ra.Bold != rb.Bold || ra.Deleted != rb.Deleted) return true;
                    if (ra.Cells.Length != rb.Cells.Length) return true;
                    for (int c = 0; c < ra.Cells.Length; c++)
                        if (!string.Equals(ra.Cells[c], rb.Cells[c], StringComparison.Ordinal)) return true;
                }
            }
            return false;
        }

        private void PushUndo(WindowSnapshot snap)
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
        }

        private void UpdateRowUi()
        {
            bool all = IsAllView;
            bool hasTable = all ? _allRows.Count > 0 : _currentEntry != null;
            if (AddRowBtn != null) AddRowBtn.IsEnabled = all ? _allRows.Count > 0 : _currentEntry != null;
            if (DelRowBtn != null) DelRowBtn.IsEnabled = hasTable;
            if (EmptyHint != null)
            {
                EmptyHint.Text = _currentEntry != null
                    ? "В таблице нет строк. Добавьте строку кнопкой «Добавить строку» (Insert)."
                    : "Выберите спецификацию слева.";
                EmptyHint.Visibility = !all && _currentEntry != null && _currentEntry.Rows.Count == 0
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            if (GridTable != null)
                GridTable.Visibility = (!all && _currentEntry != null && _currentEntry.Rows.Count > 0)
                    ? Visibility.Visible : Visibility.Collapsed;
            if (AllTable != null)
                AllTable.Visibility = all && _allRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (AllEmptyHint != null)
                AllEmptyHint.Visibility = all && _allRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnUndo(object sender, RoutedEventArgs e) { DoUndo(); }

        private void OnRedo(object sender, RoutedEventArgs e) { DoRedo(); }

        private void DoUndo()
        {
            if (_undo.Count == 0) return;
            _redo.Add(CaptureAll());
            var snap = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            ApplyWindowSnapshot(snap);
            PrintStatus.Text = "Действие отменено (Ctrl+Z).";
        }

        private void DoRedo()
        {
            if (_redo.Count == 0) return;
            _undo.Add(CaptureAll());
            var snap = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            ApplyWindowSnapshot(snap);
            PrintStatus.Text = "Действие возвращено (Ctrl+Y).";
        }

        private void OnReset(object sender, RoutedEventArgs e)
        {
            if (IsAllView) return;
            var entry = _currentEntry;
            if (entry == null) return;

            if (entry.IsFree)
            {
                if (entry.Rows.Count == 0) return;
                var rf = MessageBox.Show(this,
                    "Очистить свободную спецификацию?\n\nВсе введённые строки будут удалены, заголовки колонок останутся.",
                    "Снимок спецификаций",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (rf != MessageBoxResult.Yes) return;
                PushUndo(CaptureAll());
                entry.Rows.Clear();
                entry.Dirty = true;
                entry.RecomputeChanged();
                SaveEntry(entry);
                PrintStatus.Text = "Свободная спецификация очищена.";
                UpdateRowUi();
                return;
            }

            if (entry.ModelRows == null) return;
            var r = MessageBox.Show(this,
                "Вернуть спецификацию к виду из модели?\n\nВсе правки этой спецификации будут сброшены.",
                "Снимок спецификаций",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return;

            PushUndo(CaptureAll());
            entry.Headers = new List<string>(entry.ModelHeaders);
            entry.Rows.Clear();
            for (int i = 0; i < entry.ModelRows.Count; i++)
                entry.Rows.Add(new SnapshotRow((string[])entry.ModelRows[i].Clone())
                {
                    Owner = entry,
                    SourceIndex = i
                });
            entry.Dirty = false;
            entry.RecomputeChanged();
            ScheduleSnapshotsFile.DeleteEntry(_docKey, entry.ScheduleId, entry.SheetId, entry.SegmentIndex);
            BuildSheetColumns(entry);
            ApplySearchToSheet();
            UpdateRowUi();
            UpdateButtonsForView();
            PrintStatus.Text = "Правки сброшены к виду из модели.";
        }

        // ------------------------------------------------------------------ Буфер обмена и автозаполнение

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (e.Key == Key.Insert) { AddRow(); e.Handled = true; return; }
            if (e.Key == Key.Delete)
            {
                if (ctrl) DeleteRows(); else ClearSelectedCells();
                e.Handled = true;
                return;
            }
            if (!ctrl) return;
            switch (e.Key)
            {
                case Key.C: CopySelection(); e.Handled = true; break;
                case Key.V: PasteSelection(); e.Handled = true; break;
                case Key.D: FillDown(); e.Handled = true; break;
                case Key.Z: DoUndo(); e.Handled = true; break;
                case Key.Y: DoRedo(); e.Handled = true; break;
                case Key.B: ToggleBoldSelection(); e.Handled = true; break;
            }
        }

        private bool GetSelectionRect(DataGrid grid, out int r0, out int c0, out int r1, out int c1)
        {
            r0 = c0 = int.MaxValue;
            r1 = c1 = -1;
            foreach (var ci in grid.SelectedCells)
            {
                int r = grid.Items.IndexOf(ci.Item);
                int c = ci.Column != null ? ci.Column.DisplayIndex : -1;
                if (r < 0 || c < 0) continue;
                if (r < r0) r0 = r;
                if (r > r1) r1 = r;
                if (c < c0) c0 = c;
                if (c > c1) c1 = c;
            }
            if (r1 < r0 || c1 < c0)
            {
                var cur = grid.CurrentCell;
                if (cur.IsValid && cur.Column != null)
                {
                    int r = grid.Items.IndexOf(cur.Item);
                    int c = cur.Column.DisplayIndex;
                    if (r >= 0 && c >= 0) { r0 = r1 = r; c0 = c1 = c; }
                }
            }
            return r0 >= 0 && r1 >= r0 && c1 >= c0;
        }

        private void CopySelection()
        {
            var grid = ActiveGrid;
            int r0, c0, r1, c1;
            if (!GetSelectionRect(grid, out r0, out c0, out r1, out c1)) return;
            var sb = new StringBuilder();
            for (int r = r0; r <= r1; r++)
            {
                var items = grid.Items;
                if (r >= items.Count) break;
                object item = items[r];
                for (int c = c0; c <= c1; c++)
                {
                    if (c > c0) sb.Append('\t');
                    sb.Append(CellText(item, c) ?? string.Empty);
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

        private static string CellText(object item, int viewCol)
        {
            if (item is SnapshotRow r) return viewCol >= 0 && viewCol < r.Cells.Length ? r.Cells[viewCol] : null;
            if (item is AllRow a) return a[viewCol];
            return null;
        }

        private void PasteSelection()
        {
            var grid = ActiveGrid;
            string text = null;
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
            catch (Exception ex) { GrdLog.Log("ScheduleSnapshotWindow.Paste read EXCEPTION: " + ex); }
            if (string.IsNullOrEmpty(text)) return;

            int r0, c0, r1, c1;
            if (!GetSelectionRect(grid, out r0, out c0, out r1, out c1)) return;

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');
            var undo = CaptureAll();
            bool changed = false;
            var touched = new HashSet<ScheduleEntry>();
            for (int dr = 0; dr < lines.Length; dr++)
            {
                int r = r0 + dr;
                if (r >= grid.Items.Count) break;
                object item = grid.Items[r];
                var values = lines[dr].Split('\t');
                for (int dc = 0; dc < values.Length; dc++)
                {
                    int viewCol = c0 + dc;
                    if (!CellWritable(item, viewCol)) continue;
                    if (!string.Equals(CellText(item, viewCol), values[dc], StringComparison.Ordinal))
                    {
                        SetCellText(item, viewCol, values[dc]);
                        var owner = OwnerOf(item);
                        if (owner != null) touched.Add(owner);
                        changed = true;
                    }
                }
            }
            if (!changed) return;
            PushUndo(undo);
            foreach (var e in touched) { e.Dirty = true; e.RecomputeChanged(); SaveEntry(e); }
            if (IsAllView) RebuildAllView();
            PrintStatus.Text = "Вставлено из буфера: строк " + lines.Length + ".";
        }

        private static bool CellWritable(object item, int viewCol)
        {
            if (item is SnapshotRow r) return viewCol >= 0 && viewCol < r.Cells.Length;
            if (item is AllRow a) return a.SourceColumn(viewCol) >= 0;
            return false;
        }

        private static void SetCellText(object item, int viewCol, string value)
        {
            if (item is SnapshotRow r) { if (viewCol >= 0 && viewCol < r.Cells.Length) r.Cells[viewCol] = value; }
            else if (item is AllRow a) a[viewCol] = value;
        }

        private static ScheduleEntry OwnerOf(object item)
        {
            if (item is SnapshotRow r) return r.Owner;
            if (item is AllRow a) return a.Source.Owner;
            return null;
        }

        private void FillDown()
        {
            var grid = ActiveGrid;
            int r0, c0, r1, c1;
            if (!GetSelectionRect(grid, out r0, out c0, out r1, out c1)) return;
            if (r1 <= r0) return;
            var undo = CaptureAll();
            bool changed = false;
            var touched = new HashSet<ScheduleEntry>();
            for (int c = c0; c <= c1; c++)
            {
                if (r0 >= grid.Items.Count) continue;
                var value = CellText(grid.Items[r0], c);
                for (int r = r0 + 1; r <= r1; r++)
                {
                    if (r >= grid.Items.Count) break;
                    var item = grid.Items[r];
                    if (!CellWritable(item, c)) continue;
                    if (!string.Equals(CellText(item, c), value, StringComparison.Ordinal))
                    {
                        SetCellText(item, c, value);
                        var owner = OwnerOf(item);
                        if (owner != null) touched.Add(owner);
                        changed = true;
                    }
                }
            }
            if (!changed) return;
            PushUndo(undo);
            foreach (var e in touched) { e.Dirty = true; e.RecomputeChanged(); SaveEntry(e); }
            if (IsAllView) RebuildAllView();
            PrintStatus.Text = "Значение заполнено вниз (Ctrl+D).";
        }

        /// <summary>Очищает выделенные ячейки (Delete). Работает и когда ячейка не в режиме
        /// редактирования. Если ничего не выделено — очищает текущую ячейку.</summary>
        private void ClearSelectedCells()
        {
            var grid = ActiveGrid;
            var cells = new List<DataGridCellInfo>(grid.SelectedCells);
            if (cells.Count == 0 && grid.CurrentCell.IsValid) cells.Add(grid.CurrentCell);
            if (cells.Count == 0)
            {
                PrintStatus.Text = "Выделите ячейки для очистки (Delete).";
                return;
            }

            var undo = CaptureAll();
            bool changed = false;
            var touched = new HashSet<ScheduleEntry>();
            foreach (var ci in cells)
            {
                if (ci.Column == null) continue;
                object item = ci.Item;
                int c = ci.Column.DisplayIndex;
                if (!CellWritable(item, c)) continue;
                if (string.IsNullOrEmpty(CellText(item, c))) continue;
                SetCellText(item, c, string.Empty);
                var owner = OwnerOf(item);
                if (owner != null) touched.Add(owner);
                changed = true;
            }
            if (!changed) { PrintStatus.Text = "Нет данных для очистки (Delete)."; return; }
            PushUndo(undo);
            foreach (var e in touched) { e.Dirty = true; e.RecomputeChanged(); SaveEntry(e); }
            PrintStatus.Text = "Ячейки очищены (Delete).";
        }

        // ------------------------------------------------------------------ Жирный

        private void OnToggleBold(object sender, RoutedEventArgs e) { ToggleBoldSelection(); }

        private void ToggleBoldSelection()
        {
            bool changed = false;
            var touched = new HashSet<ScheduleEntry>();
            if (IsAllView)
            {
                foreach (var ci in AllTable.SelectedCells)
                {
                    if (!(ci.Item is AllRow ar) || ci.Column == null) continue;
                    int si = ar.SourceColumn(ci.Column.DisplayIndex);
                    if (si < 0) continue;
                    ar.Source.ToggleBold(si);
                    touched.Add(ar.Source.Owner);
                    changed = true;
                }
            }
            else
            {
                if (_currentEntry == null) return;
                foreach (var ci in GridTable.SelectedCells)
                {
                    if (!(ci.Item is SnapshotRow row) || ci.Column == null) continue;
                    int c = ci.Column.DisplayIndex;
                    if (c < 0 || c >= row.Cells.Length) continue;
                    row.ToggleBold(c);
                    touched.Add(row.Owner);
                    changed = true;
                }
            }
            if (!changed) { PrintStatus.Text = "Выделите ячейки и повторите (Ctrl+B)."; return; }
            foreach (var e in touched) { e.Dirty = true; SaveEntry(e); }
            PrintStatus.Text = "Жирный шрифт переключён для выделенных ячеек.";
        }

        // ------------------------------------------------------------------ Строки

        private List<int> SelectedRowIndices(DataGrid grid)
        {
            var set = new SortedSet<int>();
            foreach (var ci in grid.SelectedCells)
            {
                int r = grid.Items.IndexOf(ci.Item);
                if (r >= 0) set.Add(r);
            }
            if (set.Count == 0)
            {
                var cur = grid.CurrentCell;
                if (cur.IsValid)
                {
                    int r = grid.Items.IndexOf(cur.Item);
                    if (r >= 0) set.Add(r);
                }
            }
            return set.ToList();
        }

        /// <summary>Строка-источник под выделением (для «По листам» — сама строка,
        /// для «Всё» — Source обёртки AllRow). Нужна для вставки относительно выделения.</summary>
        private static SnapshotRow SelectedSnapshotRow(DataGrid grid)
        {
            foreach (var ci in grid.SelectedCells)
            {
                if (ci.Item is AllRow ar) return ar.Source;
                if (ci.Item is SnapshotRow sr) return sr;
            }
            var cur = grid.CurrentCell;
            if (cur.IsValid)
            {
                if (cur.Item is AllRow car) return car.Source;
                if (cur.Item is SnapshotRow csr) return csr;
            }
            return null;
        }

        private void OnAddRow(object sender, RoutedEventArgs e) { AddRow(); }

        private void AddRow()
        {
            var entry = ActiveEntry();
            if (entry == null)
            {
                PrintStatus.Text = "Сначала выберите спецификацию.";
                return;
            }
            int cols = entry.Headers.Count;
            if (cols == 0) return;

            var undo = CaptureAll();
            int modelIndex = entry.Rows.Count;
            if (IsAllView)
            {
                var selRow = SelectedSnapshotRow(AllTable);
                if (selRow != null && ReferenceEquals(selRow.Owner, entry))
                {
                    int idx = entry.Rows.IndexOf(selRow);
                    if (idx >= 0) modelIndex = idx + 1;
                }
            }
            else if (GridTable.SelectedCells.Count > 0)
            {
                int sel = SelectedRowIndices(GridTable).DefaultIfEmpty(-1).First();
                if (sel >= 0 && sel < entry.Rows.Count) modelIndex = sel + 1;
            }

            var row = new SnapshotRow(EmptyRow(cols)) { Owner = entry, SourceIndex = -1 };
            entry.Rows.Insert(modelIndex, row);
            entry.Dirty = true;
            entry.RecomputeChanged();
            PushUndo(undo);
            SaveEntry(entry);
            if (IsAllView) RebuildAllView();
            PrintStatus.Text = "Добавлена строка (Insert).";
            UpdateRowUi();
            SelectRow(row);
        }

        private void OnDeleteRow(object sender, RoutedEventArgs e) { DeleteRows(); }

        private void DeleteRows()
        {
            var grid = ActiveGrid;
            var indices = SelectedRowIndices(grid);
            if (indices.Count == 0)
            {
                PrintStatus.Text = "Выберите строку (ячейку) для удаления.";
                return;
            }

            var undo = CaptureAll();
            var touched = new HashSet<ScheduleEntry>();
            int added = 0, marked = 0;

            if (IsAllView)
            {
                foreach (var idx in indices)
                {
                    if (idx < 0 || idx >= _allRows.Count) continue;
                    var source = _allRows[idx].Source;
                    var owner = source.Owner;
                    if (source.SourceIndex < 0) { owner.Rows.Remove(source); added++; }
                    else { source.IsDeleted = !source.IsDeleted; marked++; }
                    owner.Dirty = true;
                    touched.Add(owner);
                }
            }
            else
            {
                var entry = _currentEntry;
                if (entry == null) return;
                for (int i = indices.Count - 1; i >= 0; i--)
                {
                    int idx = indices[i];
                    if (idx < 0 || idx >= entry.Rows.Count) continue;
                    var row = entry.Rows[idx];
                    if (row.SourceIndex < 0) { entry.Rows.RemoveAt(idx); added++; }
                    else { row.IsDeleted = !row.IsDeleted; marked++; }
                }
                entry.Dirty = true;
                touched.Add(entry);
            }

            foreach (var e in touched) { e.RecomputeChanged(); SaveEntry(e); }
            PushUndo(undo);
            if (IsAllView) RebuildAllView();
            PrintStatus.Text = "Строки изменены: удалено/восстановлено " + marked + ", убрано добавленных " + added + " (Ctrl+Delete).";
            UpdateRowUi();
        }

        private void SelectRow(SnapshotRow row)
        {
            try
            {
                if (IsAllView)
                {
                    var ar = _allRows.FirstOrDefault(a => ReferenceEquals(a.Source, row));
                    if (ar != null)
                    {
                        AllTable.ScrollIntoView(ar);
                        if (AllTable.Columns.Count > 0)
                        {
                            AllTable.SelectedCells.Clear();
                            AllTable.CurrentCell = new DataGridCellInfo(ar, AllTable.Columns[0]);
                        }
                    }
                    return;
                }
                GridTable.ScrollIntoView(row);
                if (GridTable.Columns.Count > 0)
                {
                    GridTable.SelectedCells.Clear();
                    GridTable.CurrentCell = new DataGridCellInfo(row, GridTable.Columns[0]);
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("ScheduleSnapshotWindow.SelectRow EXCEPTION: " + ex);
            }
        }

        private static string[] EmptyRow(int cols)
        {
            var cells = new string[cols];
            for (int i = 0; i < cols; i++) cells[i] = string.Empty;
            return cells;
        }

        // ------------------------------------------------------------------ Редактирование ячеек

        /// <summary>Блокирует вход в редактирование для ячеек вкладки «Всё», у которых нет
        /// исходной колонки в спецификации (SourceColumn &lt; 0). WPF DataGrid не позволяет
        /// сделать отдельную ячейку ReadOnly, но событие BeginningEdit можно отменить.</summary>
        private void OnAllTableBeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            if (e.Row == null || e.Column == null) return;
            if (!CellWritable(e.Row.Item, e.Column.DisplayIndex)) e.Cancel = true;
        }

        private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit)
            {
                _pendingUndoSnapshot = null;
                return;
            }
            object item = e.Row != null ? e.Row.Item : null;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var snap = _pendingUndoSnapshot;
                _pendingUndoSnapshot = null;
                if (snap != null && SnapshotsDiffer(snap, CaptureAll()))
                    PushUndo(snap);
                var owner = OwnerOf(e.Row.Item);
                if (owner != null)
                {
                    owner.Dirty = true;
                    owner.RecomputeChanged();
                    SaveEntry(owner);
                }
                UpdateUndoUi();
                PrintStatus.Text = "Изменено. Правки сохраняются автоматически (Ctrl+Z — отменить).";
            }));
        }

        private void GridTable_PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
        {
            _pendingUndoSnapshot = CaptureAll();
            var tb = e.EditingElement as TextBox;
            if (tb == null) return;
            tb.Focus();
            if (e.EditingEventArgs is TextCompositionEventArgs)
            {
                tb.CaretIndex = tb.Text.Length;
                return;
            }
            tb.SelectAll();
        }

        // ------------------------------------------------------------------ Кнопки печати/экспорта

        private void OnRefresh(object sender, RoutedEventArgs e)
        {
            if (IsAllView || _selected == null) return;
            var entry = _currentEntry;
            if (entry != null && entry.IsFree)
            {
                _entries.Remove(entry.Key);
                _currentEntry = null;
                LoadSheetEntry(_selected);
                return;
            }
            if (entry != null && entry.Dirty)
            {
                var r = MessageBox.Show(this,
                    "Заменить таблицу данными из модели?\n\nВнесённые правки будут потеряны.",
                    "Снимок спецификаций",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (r != MessageBoxResult.Yes) return;
            }
            if (entry != null)
            {
                ScheduleSnapshotsFile.DeleteEntry(_docKey, entry.ScheduleId, entry.SheetId, entry.SegmentIndex);
                _entries.Remove(entry.Key);
                _currentEntry = null;
            }
            LoadSheetEntry(_selected);
        }

        private List<string> ActiveHeaders()
        {
            if (IsAllView) return new List<string>(_allHeaders);
            return _currentEntry != null ? new List<string>(_currentEntry.Headers) : new List<string>();
        }

        private List<string[]> ActiveExportRows(out List<long> rowBold)
        {
            var rows = new List<string[]>();
            rowBold = new List<long>();
            if (IsAllView)
            {
                foreach (var ar in _allRows)
                {
                    if (ar.IsDeleted) continue;
                    rows.Add(ar.ToUnionArray());
                    rowBold.Add(ar.ViewBold);
                }
            }
            else if (_currentEntry != null)
            {
                foreach (var r in _currentEntry.Rows)
                {
                    if (r.IsDeleted) continue;
                    rows.Add(r.Cells.ToArray());
                    rowBold.Add(r.BoldMask);
                }
            }
            return rows;
        }

        private void OnPrint(object sender, RoutedEventArgs e)
        {
            var headers = ActiveHeaders();
            List<long> rowBold;
            var rows = ActiveExportRows(out rowBold);
            if (headers.Count == 0 || rows.Count == 0)
            {
                PrintStatus.Text = "Нет данных для печати.";
                return;
            }

            string name = IsAllView ? "Избранные спецификации" : _currentEntry.ScheduleName;
            double w = IsAllView ? 0 : _currentEntry.SheetWidthMm;
            double h = IsAllView ? 0 : _currentEntry.SheetHeightMm;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить снимок спецификации в PDF",
                Filter = "PDF (*.pdf)|*.pdf",
                DefaultExt = ".pdf",
                AddExtension = true,
                FileName = SafeFileName(name) + ".pdf",
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
                SchedulePdfRenderer.Write(path, name, headers, rows, w, h, rowBold);
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

        private void OnPrintSheet(object sender, RoutedEventArgs e)
        {
            if (IsAllView) return;
            var entry = _currentEntry;
            if (_selected == null || entry == null || entry.Rows.Count == 0)
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
                FileName = SafeFileName(_selected.SheetNumber + " " + entry.ScheduleName) + ".pdf",
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
                var headers = new List<string>(entry.Headers);
                List<long> rowBold;
                var rows = ActiveExportRows(out rowBold);

                handler.QueuePrintEdited(entry.SheetId, entry.ScheduleId, headers, rows, rowBold, path, entry.IsFree, res =>
                {
                    SheetPrintBtn.IsEnabled = _selected != null && _currentEntry != null;
                    PrintBtn.IsEnabled = _currentEntry != null && _currentEntry.Rows.Count > 0;
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
                SaveAllDirty();
                MultiPrintBtn.IsEnabled = false;
                PrintBtn.IsEnabled = false;
                SheetPrintBtn.IsEnabled = false;
                PrintStatus.Text = "Формирование " + items.Count + " листов в Revit…";

                handler.QueuePrintMany(items, path, res =>
                {
                    UpdateCheckedUi();
                    PrintBtn.IsEnabled = _currentEntry != null && _currentEntry.Rows.Count > 0;
                    SheetPrintBtn.IsEnabled = _selected != null && _currentEntry != null;
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
                GrdLog.Log("ScheduleSnapshotWindow.OnPrintSelected EXCEPTION: " + ex);
                PrintStatus.Text = "Не удалось запустить печать: " + ex.Message;
            }
        }

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

            bool oneSheet = ExcelOneSheetCheck != null && ExcelOneSheetCheck.IsChecked == true;

            try
            {
                SaveAllDirty();
                ExportExcelBtn.IsEnabled = false;
                PrintStatus.Text = "Экспорт " + items.Count + " спецификаций в Excel" +
                                   (oneSheet ? " (один лист)…" : "…");

                handler.QueueExportExcel(items, path, oneSheet, res =>
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

        private void OnExportCsv(object sender, RoutedEventArgs e)
        {
            var headers = ActiveHeaders();
            List<long> ignored;
            var rows = ActiveExportRows(out ignored);
            if (headers.Count == 0 || rows.Count == 0)
            {
                PrintStatus.Text = "Нет данных для экспорта.";
                return;
            }

            string name = IsAllView ? "Избранные спецификации" : _currentEntry.ScheduleName;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Экспорт таблицы снимка в CSV",
                Filter = "CSV (*.csv)|*.csv",
                DefaultExt = ".csv",
                AddExtension = true,
                FileName = SafeFileName(name) + ".csv",
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
                sb.AppendLine(string.Join(";", headers.Select(Csv)));
                foreach (var row in rows)
                    sb.AppendLine(string.Join(";", row.Select(Csv)));
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

        // ------------------------------------------------------------------ Свободная спецификация

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
            if (SheetsList.SelectedItem != it) LoadSheetEntry(it);
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
