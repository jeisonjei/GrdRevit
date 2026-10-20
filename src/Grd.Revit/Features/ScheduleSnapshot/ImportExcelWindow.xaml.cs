using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using GrdRevit.Core;
using Microsoft.Win32;

namespace GrdRevit.Ui
{
    /// <summary>Строка превью таблицы (редактируемая).</summary>
    public sealed class ExcelRowVm : System.ComponentModel.INotifyPropertyChanged
    {
        public int Index;
        private string[] _cells;
        public string[] Cells
        {
            get => _cells;
            set { _cells = value; Raise(null); }
        }
        public string this[int c]
        {
            get => _cells != null && c >= 0 && c < _cells.Length ? _cells[c] : string.Empty;
            set
            {
                if (_cells == null || c < 0 || c >= _cells.Length) return;
                if (string.Equals(_cells[c], value)) return;
                _cells[c] = value;
                Raise("Item[]");
                Raise("[" + c + "]");
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        private void Raise(string name)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    /// <summary>Диалог импорта таблицы. Два режима:
    /// «Импорт из Excel» — выбор книги/листа/диапазона, таблица переносится в редактируемое превью;
    /// «Ручная таблица» — пустая таблица, заполняемая вручную.
    /// В обоих режимах ячейки, строки и колонки редактируются прямо в превью.
    /// Результат — <see cref="Table"/> (таблица к переносу в шапку спецификации).</summary>
    public partial class ImportExcelWindow : Window
    {
        public ExcelTable Table { get; private set; }
        public string ScheduleName { get; private set; } = string.Empty;
        public string SheetNumber { get; private set; } = string.Empty;
        public string SheetName { get; private set; } = string.Empty;
        public string FontName { get; private set; } = string.Empty;

        /// <summary>Заполняет выпадающий список шрифтов (стилей текста проекта).</summary>
        public void SetFonts(IEnumerable<string> fonts)
        {
            var list = fonts != null ? fonts.Where(f => !string.IsNullOrWhiteSpace(f)).ToList() : new List<string>();
            FontCombo.ItemsSource = list;
            if (list.Count > 0)
                FontCombo.SelectedIndex = list.FindIndex(f => f.IndexOf("Спецификация по умолчанию", StringComparison.OrdinalIgnoreCase) >= 0);
            if (FontCombo.SelectedIndex < 0 && list.Count > 0) FontCombo.SelectedIndex = 0;
        }

        private string _path;
        private ExcelGrid _grid;
        private readonly List<ExcelRowVm> _rows = new List<ExcelRowVm>();
        private readonly List<string> _headers = new List<string>();
        private readonly List<double> _widthsMm = new List<double>();
        private int _r1, _c1, _r2, _c2;
        private bool _hasSelection;
        private int _widthCol = -1;
        private bool _syncingWidth;
        private bool _widthsReady;
        private int _colCount;
        private double[] _dragWidths;

        private const int ManualDefaultCols = 4;
        private const int ManualDefaultRows = 6;

        public ImportExcelWindow()
        {
            InitializeComponent();
            WindowTopmost.Track(this);
            RowsGrid.PreviewMouseLeftButtonDown += OnGridPreviewMouseDown;
            RowsGrid.PreviewMouseLeftButtonUp += OnGridPreviewMouseUp;
            UpdateOk();
            UpdateWidthBox();
        }

        // ---------------------------------------------------------------- Режим

        private bool IsManual => ModeManual != null && ModeManual.IsChecked == true;

        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            if (ExcelPanel == null) return;
            bool manual = IsManual;
            ExcelPanel.Visibility = manual ? Visibility.Collapsed : Visibility.Visible;
            RangeLabel.Text = manual ? "ручной ввод" : (_hasSelection ? RangeLabel.Text : "диапазон не выбран");

            if (manual)
            {
                _grid = null;
                BuildManualPreview();
            }
            else if (!string.IsNullOrEmpty(_path) && SheetCombo.SelectedItem is string s)
            {
                OnSheetChanged(SheetCombo, null);
            }
            else
            {
                ClearPreview();
            }
            UpdateOk();
        }

        private void BuildManualPreview()
        {
            var rows = new List<string[]>();
            for (int r = 0; r < ManualDefaultRows; r++)
            {
                var arr = new string[ManualDefaultCols];
                for (int c = 0; c < ManualDefaultCols; c++) arr[c] = string.Empty;
                rows.Add(arr);
            }
            LoadModel(null, rows, true);
            if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = "Ручная таблица";
            _r1 = 0; _c1 = 0; _r2 = Math.Max(0, rows.Count - 1); _c2 = ManualDefaultCols - 1;
            _hasSelection = true;
            _widthCol = 0;
            ApplyWidthsToColumns();
            UpdateRangeLabel();
            UpdateWidthBox();
        }

        private void ClearPreview()
        {
            _rows.Clear();
            _headers.Clear();
            _colCount = 0;
            _widthsMm.Clear();
            RowsGrid.Columns.Clear();
            RowsGrid.ItemsSource = null;
            _hasSelection = false;
            _widthsReady = false;
            _widthCol = -1;
            UpdateWidthBox();
        }

        // ---------------------------------------------------------------- Excel

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Выберите книгу Excel",
                Filter = "Книги Excel (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|Все файлы (*.*)|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) != true) return;
            LoadFile(dlg.FileName);
        }

        private void LoadFile(string path)
        {
            try
            {
                _path = path;
                FileLabel.Text = path;
                GrdLog.Log("ImportExcelWindow: LoadFile " + path);
                var names = ExcelReader.ListSheetNames(path);
                if (names.Count == 0)
                {
                    MessageBox.Show(this, "В книге не найдено ни одного листа.", "Импорт из Excel",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                SheetCombo.ItemsSource = names;
                SheetCombo.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось открыть файл:\n" + ex.Message, "Импорт из Excel",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnSheetChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsManual) return;
            var sheetName = SheetCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(sheetName) || string.IsNullOrEmpty(_path)) return;

            try
            {
                _grid = ExcelReader.ReadGrid(_path, sheetName);
                GrdLog.Log("ImportExcelWindow: ReadGrid rows=" + _grid.RowCount + " cols=" + _grid.ColumnCount + " sheet=" + sheetName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось прочитать лист:\n" + ex.Message, "Импорт из Excel",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            BuildPreviewFromGrid(_grid);

            if (string.IsNullOrWhiteSpace(NameBox.Text))
                NameBox.Text = !string.IsNullOrWhiteSpace(sheetName)
                    ? sheetName
                    : Path.GetFileNameWithoutExtension(_path ?? string.Empty);
        }

        private void BuildPreviewFromGrid(ExcelGrid grid)
        {
            _grid = grid;
            if (grid == null || grid.IsEmpty)
            {
                ClearPreview();
                RangeLabel.Text = "нет данных";
                UpdateOk();
                GrdLog.Log("ImportExcelWindow: Grid empty for preview");
                return;
            }

            bool withHeader = HeadersCheck.IsChecked == true;
            var headers = new List<string>();
            var rows = new List<string[]>();
            if (withHeader)
                for (int c = 0; c < grid.ColumnCount; c++)
                    headers.Add(grid.Cells.Count > 0 ? (grid.Cells[0][c] ?? string.Empty) : string.Empty);
            int start = withHeader ? 1 : 0;
            for (int r = start; r < grid.RowCount; r++)
            {
                var arr = new string[grid.ColumnCount];
                Array.Copy(grid.Cells[r], arr, grid.ColumnCount);
                rows.Add(arr);
            }

            LoadModel(headers, rows, true);

            for (int c = 0; c < grid.ColumnCount; c++)
                _widthsMm[c] = DefaultWidthMm(grid, c);

            _r1 = 0; _c1 = 0; _r2 = Math.Max(0, rows.Count - 1); _c2 = grid.ColumnCount - 1;
            _hasSelection = true;
            _widthCol = 0;
            ApplyWidthsToColumns();
            UpdateRangeLabel();
            UpdateWidthBox();
            UpdateOk();
            GrdLog.Log("ImportExcelWindow: Preview cols=" + grid.ColumnCount + " rows=" + rows.Count);
        }

        // ---------------------------------------------------------------- Модель + колонки

        /// <summary>Строит колонки DataGrid и наполняет превью по headers/rows.
        /// headers — необязательная строка заголовков таблицы (в Excel-режиме);
        /// в шапке DataGrid всегда показываются технические метки A/B/C.</summary>
        private void LoadModel(List<string> headers, List<string[]> rows, bool editable)
        {
            _rows.Clear();
            _headers.Clear();
            _widthsMm.Clear();
            RowsGrid.Columns.Clear();
            RowsGrid.ItemsSource = null;

            if (headers != null) _headers.AddRange(headers);
            _colCount = headers != null
                ? headers.Count
                : (rows != null && rows.Count > 0 ? rows[0].Length : 0);
            if (rows != null)
            {
                foreach (var r in rows)
                {
                    var arr = new string[_colCount];
                    for (int c = 0; c < _colCount; c++)
                        arr[c] = r != null && c < r.Length ? (r[c] ?? string.Empty) : string.Empty;
                    _rows.Add(new ExcelRowVm { Index = _rows.Count, Cells = arr });
                }
            }

            RowsGrid.AutoGenerateColumns = false;
            for (int c = 0; c < _colCount; c++)
            {
                var col = new DataGridTextColumn
                {
                    Header = ExcelReader.ColumnName(c),
                    Binding = new Binding("[" + c + "]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                    IsReadOnly = !editable,
                    MinWidth = 20
                };
                col.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
                col.HeaderStyle = BuildColumnHeaderStyle();
                RowsGrid.Columns.Add(col);
                _widthsMm.Add(80);
            }
            RowsGrid.ItemsSource = _rows;

            _widthsReady = _colCount > 0;
            UpdateOk();
        }

        private void RebuildGridColumnsKeepModel()
        {
            RowsGrid.Columns.Clear();
            for (int c = 0; c < _colCount; c++)
            {
                var col = new DataGridTextColumn
                {
                    Header = ExcelReader.ColumnName(c),
                    Binding = new Binding("[" + c + "]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                    IsReadOnly = false,
                    MinWidth = 20
                };
                col.Width = new DataGridLength(Math.Max(15.0, _widthsMm.Count > c ? _widthsMm[c] : 80), DataGridLengthUnitType.Pixel);
                col.HeaderStyle = BuildColumnHeaderStyle();
                RowsGrid.Columns.Add(col);
            }
            RowsGrid.ItemsSource = _rows;
        }

        /// <summary>Стиль шапки колонки: базовый вид + клик по шапке выбирает всю колонку
        /// (чтобы ширина/удаление работали для текущей колонки).</summary>
        private Style BuildColumnHeaderStyle()
        {
            Style basedOn = null;
            try { basedOn = FindResource("DataGridColumnHeaderBase") as Style; } catch { }
            var st = basedOn != null ? new Style(typeof(DataGridColumnHeader), basedOn)
                                     : new Style(typeof(DataGridColumnHeader));
            var ev = new EventSetter
            {
                Event = System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
                Handler = new RoutedEventHandler((s, e) =>
                {
                    var hdr = s as DataGridColumnHeader;
                    if (hdr == null || hdr.Column == null) return;
                    int idx = RowsGrid.Columns.IndexOf(hdr.Column);
                    if (idx < 0) return;
                    _widthCol = idx;
                    RowsGrid.SelectedCells.Clear();
                    for (int r = 0; r < _rows.Count; r++)
                        RowsGrid.SelectedCells.Add(new DataGridCellInfo(_rows[r], hdr.Column));
                    UpdateWidthBox();
                })
            };
            st.Setters.Add(ev);
            return st;
        }

        // ---------------------------------------------------------------- Строки

        private void OnAddRow(object sender, RoutedEventArgs e) => AddRowInPlace();

        private void AddRowInPlace()
        {
            if (_colCount == 0) return;
            int at = _rows.Count;
            var idx = SelectedRowIndices();
            if (idx.Count > 0) at = Math.Min(_rows.Count, idx.Max() + 1);
            var arr = new string[_colCount];
            for (int c = 0; c < _colCount; c++) arr[c] = string.Empty;
            _rows.Insert(at, new ExcelRowVm { Cells = arr });
            RenumberRows();
            RowsGrid.ItemsSource = null;
            RowsGrid.ItemsSource = _rows;
        }

        private void OnDeleteRow(object sender, RoutedEventArgs e)
        {
            var idx = SelectedRowIndices();
            if (idx.Count == 0) return;
            foreach (var i in idx.OrderByDescending(x => x))
                if (i >= 0 && i < _rows.Count) _rows.RemoveAt(i);
            RenumberRows();
            RowsGrid.ItemsSource = null;
            RowsGrid.ItemsSource = _rows;
            UpdateOk();
        }

        private void RenumberRows()
        {
            for (int i = 0; i < _rows.Count; i++) _rows[i].Index = i;
        }

        private List<int> SelectedRowIndices()
        {
            var list = new List<int>();
            foreach (var cell in RowsGrid.SelectedCells)
                if (cell.Item is ExcelRowVm vm) list.Add(vm.Index);
            return list.Distinct().ToList();
        }

        // ---------------------------------------------------------------- Колонки

        private void OnAddColumn(object sender, RoutedEventArgs e)
        {
            if (_colCount == 0 && _rows.Count == 0) return;
            int at = _widthCol >= 0 && _widthCol < _colCount ? _widthCol + 1 : _colCount;

            if (_headers.Count > 0)
            {
                if (at > _headers.Count) at = _headers.Count;
                _headers.Insert(at, string.Empty);
            }
            foreach (var r in _rows) r.Cells = InsertInto(r.Cells, at, string.Empty);
            _widthsMm.Insert(Math.Min(at, _widthsMm.Count), 80);
            _colCount++;
            _widthCol = at;
            RebuildGridColumnsKeepModel();
            SelectColumn(_widthCol);
            UpdateWidthBox();
            UpdateOk();
        }

        private void OnDeleteColumn(object sender, RoutedEventArgs e)
        {
            if (_colCount <= 1) return;
            int at = _widthCol >= 0 && _widthCol < _colCount ? _widthCol : _colCount - 1;
            if (_headers.Count > at) _headers.RemoveAt(at);
            foreach (var r in _rows) r.Cells = RemoveAt(r.Cells, at);
            if (at < _widthsMm.Count) _widthsMm.RemoveAt(at);
            _colCount--;
            if (_widthCol >= _colCount) _widthCol = _colCount - 1;
            RebuildGridColumnsKeepModel();
            SelectColumn(_widthCol);
            UpdateWidthBox();
            UpdateOk();
        }

        private void SelectColumn(int c)
        {
            if (c < 0 || c >= RowsGrid.Columns.Count) return;
            RowsGrid.SelectedCells.Clear();
            for (int r = 0; r < _rows.Count; r++)
                RowsGrid.SelectedCells.Add(new DataGridCellInfo(_rows[r], RowsGrid.Columns[c]));
        }

        private static string[] InsertInto(string[] src, int at, string value)
        {
            int n = (src?.Length ?? 0) + 1;
            var dst = new string[n];
            for (int i = 0, j = 0; i < n; i++)
            {
                if (i == at) dst[i] = value;
                else dst[i] = src != null && j < src.Length ? src[j++] : string.Empty;
            }
            return dst;
        }

        private static string[] RemoveAt(string[] src, int at)
        {
            if (src == null) return new string[0];
            if (at < 0 || at >= src.Length) return src;
            var dst = new string[src.Length - 1];
            for (int i = 0, j = 0; i < src.Length; i++)
                if (i != at) dst[j++] = src[i];
            return dst;
        }

        // ---------------------------------------------------------------- Выбор / ширина

        private void OnLoadingRow(object sender, DataGridRowEventArgs e)
        {
            try
            {
                int idx = e.Row.GetIndex();
                e.Row.Header = (idx + 1).ToString();
                double mm = RowHeightMm();
                e.Row.Height = mm > 0 ? MmToPx(mm) : double.NaN;
            }
            catch { }
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var cells = RowsGrid.SelectedCells;
            GrdLog.Log("SelectionChanged: cells=" + (cells?.Count ?? -1));
            if (cells != null && cells.Count > 0)
            {
                int rmin = int.MaxValue, cmin = int.MaxValue, rmax = -1, cmax = -1;
                foreach (var cell in cells)
                {
                    if (!(cell.Item is ExcelRowVm vm)) continue;
                    int c = cell.Column != null ? RowsGrid.Columns.IndexOf(cell.Column) : -1;
                    if (c < 0) c = cell.Column != null ? cell.Column.DisplayIndex : 0;
                    if (vm.Index < rmin) rmin = vm.Index;
                    if (vm.Index > rmax) rmax = vm.Index;
                    if (c < cmin) cmin = c;
                    if (c > cmax) cmax = c;
                }
                if (rmax >= 0)
                {
                    _r1 = rmin; _r2 = rmax; _c1 = cmin; _c2 = cmax;
                    _hasSelection = true;
                    _widthCol = cmax;
                }
            }
            UpdateRangeLabel();
            UpdateWidthBox();
            UpdateOk();
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Insert) { AddRowInPlace(); e.Handled = true; }
            else if (e.Key == Key.Delete && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            { OnDeleteRow(sender, null); e.Handled = true; }
        }

        private void UpdateRangeLabel()
        {
            if (IsManual) { RangeLabel.Text = "ручной ввод"; return; }
            if (_grid == null || !_hasSelection)
            {
                RangeLabel.Text = "диапазон не выбран";
                return;
            }
            int baseRow = _grid.FirstRow;
            int baseCol = _grid.FirstCol;
            ExcelReader.SelectionSize(_r1, _c1, _r2, _c2, out var rows, out var cols);
            string range = ExcelReader.FormatRange(baseRow + _r1, baseCol + _c1, baseRow + _r2, baseCol + _c2);
            RangeLabel.Text = range + " — " + rows + "×" + cols;
        }

        // ---------------------------------------------------------------- Ширина колонок (мм)

        private static double DefaultWidthMm(ExcelGrid grid, int c)
        {
            int maxLen = 0;
            for (int r = 0; r < grid.RowCount; r++)
            {
                var row = grid.Cells[r];
                if (row == null || c >= row.Length) continue;
                int l = (row[c] ?? string.Empty).Length;
                if (l > maxLen) maxLen = l;
            }
            double mm = maxLen * 2.4 + 6.0;
            return Math.Max(15.0, Math.Min(200.0, mm));
        }

        private double DpiScale()
        {
            try
            {
                var src = PresentationSource.FromVisual(this);
                if (src != null && src.CompositionTarget != null)
                    return src.CompositionTarget.TransformToDevice.M11;
            }
            catch { }
            return 1.0;
        }

        private double MmToPx(double mm) => mm / 25.4 * 96.0 * DpiScale();
        private double PxToMm(double px) => px / (96.0 * DpiScale()) * 25.4;

        private void ApplyWidthsToColumns()
        {
            _syncingWidth = true;
            try
            {
                for (int c = 0; c < RowsGrid.Columns.Count && c < _widthsMm.Count; c++)
                {
                    RowsGrid.Columns[c].MinWidth = 0;
                    RowsGrid.Columns[c].Width = new DataGridLength(MmToPx(_widthsMm[c]), DataGridLengthUnitType.Pixel);
                }
            }
            finally { _syncingWidth = false; }
        }

        private void UpdateWidthBox()
        {
            if (ColWidthBox == null) return;
            GrdLog.Log("UpdateWidthBox: ready=" + _widthsReady + " col=" + _widthCol + " n=" + _widthsMm.Count);
            if (!_widthsReady || _widthCol < 0 || _widthCol >= _widthsMm.Count)
            {
                ColWidthBox.IsEnabled = false;
                _syncingWidth = true;
                try { ColWidthBox.Text = string.Empty; } finally { _syncingWidth = false; }
                return;
            }
            ColWidthBox.IsEnabled = true;
            _syncingWidth = true;
            try { ColWidthBox.Text = Math.Round(_widthsMm[_widthCol]).ToString("0"); }
            finally { _syncingWidth = false; }
        }

        private void OnColWidthKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CommitWidthBox(); e.Handled = true; }
        }

        private void OnColWidthCommit(object sender, RoutedEventArgs e) => CommitWidthBox();

        /// <summary>Применяет введённую ширину (мм) к текущей колонке после Enter/потери фокуса.</summary>
        private void CommitWidthBox()
        {
            if (_syncingWidth) return;
            if (!_widthsReady || _widthCol < 0 || _widthCol >= _widthsMm.Count) return;
            if (_widthCol >= RowsGrid.Columns.Count) return;
            if (double.TryParse((ColWidthBox.Text ?? string.Empty).Replace(',', '.').Trim(),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var mm))
            {
                mm = Math.Max(15.0, Math.Min(400.0, mm));
                _widthsMm[_widthCol] = mm;
                _syncingWidth = true;
                try { RowsGrid.Columns[_widthCol].Width = new DataGridLength(MmToPx(mm)); }
                finally { _syncingWidth = false; }
            }
            UpdateWidthBox();
        }

        // ---------------------------------------------------------------- Высота строк (мм)

        private double RowHeightMm()
        {
            if (RowHeightBox == null) return 0;
            if (double.TryParse((RowHeightBox.Text ?? string.Empty).Replace(',', '.').Trim(),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var mm))
                return Math.Max(0, Math.Min(400, mm));
            return 0;
        }

        private void OnRowHeightKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CommitRowHeight(); e.Handled = true; }
        }

        private void OnRowHeightCommit(object sender, RoutedEventArgs e) => CommitRowHeight();

        /// <summary>Применяет высоту строк (мм) ко всем строкам превью и запоминает её.</summary>
        private void CommitRowHeight()
        {
            double mm = RowHeightMm();
            _syncingWidth = true;
            try { RowsGrid.RowHeight = mm > 0 ? MmToPx(mm) : double.NaN; }
            finally { _syncingWidth = false; }
        }

        /// <summary>Снимает снапшот ширин при нажатии (для последующего сравнения после перетаскивания).</summary>
        private void OnGridPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragWidths = new double[RowsGrid.Columns.Count];
            for (int c = 0; c < RowsGrid.Columns.Count; c++)
                _dragWidths[c] = RowsGrid.Columns[c].ActualWidth;
        }

        /// <summary>После отпускания кнопки находит колонку, чья ширина изменилась (тянули её границу),
        /// и обновляет поле «Ширина».</summary>
        private void OnGridPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragWidths == null) return;
            if (_syncingWidth || _dragWidths.Length != RowsGrid.Columns.Count)
            {
                _dragWidths = null;
                return;
            }

            int idx = -1;
            double best = 0.5;
            for (int c = 0; c < RowsGrid.Columns.Count; c++)
            {
                double d = Math.Abs(RowsGrid.Columns[c].ActualWidth - _dragWidths[c]);
                if (d > best) { best = d; idx = c; }
            }
            _dragWidths = null;
            if (idx < 0 || idx >= _widthsMm.Count) return;

            _widthsMm[idx] = Math.Round(PxToMm(RowsGrid.Columns[idx].ActualWidth));
            _widthCol = idx;
            GrdLog.Log("WidthResize: idx=" + idx + " mm=" + _widthsMm[idx]);
            UpdateWidthBox();
        }

        // ---------------------------------------------------------------- Итог

        private void OnOptionChanged(object sender, RoutedEventArgs e)
        {
            if (IsManual) return;
            if (_grid != null && !_grid.IsEmpty) BuildPreviewFromGrid(_grid);
            UpdateOk();
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e) { UpdateOk(); }

        private void UpdateOk()
        {
            if (OkButton == null) return;
            bool hasData = IsManual
                ? _colCount > 0 && _rows.Count > 0
                : _grid != null && !_grid.IsEmpty && _hasSelection;
            bool hasName = !string.IsNullOrWhiteSpace(NameBox?.Text);
            OkButton.IsEnabled = hasData && hasName;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            try
            {
                Table = BuildTableFromModel();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось собрать таблицу:\n" + ex.Message, "Импорт из Excel",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (Table == null || Table.RowCount == 0 || Table.ColumnCount == 0)
            {
                MessageBox.Show(this, "Таблица пуста.", "Импорт из Excel",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ScheduleName = (NameBox.Text ?? string.Empty).Trim();
            FontName = FontCombo.SelectedItem as string ?? string.Empty;
            DialogResult = true;
        }

        /// <summary>Собирает ExcelTable из редактируемой модели превью.</summary>
        private ExcelTable BuildTableFromModel()
        {
            var table = new ExcelTable { SheetName = _grid?.SheetName ?? string.Empty };
            if (_headers.Count > 0)
                for (int c = 0; c < _colCount; c++)
                    table.Headers.Add(c < _headers.Count ? (_headers[c] ?? string.Empty) : string.Empty);
            foreach (var r in _rows)
            {
                var line = new string[_colCount];
                for (int c = 0; c < _colCount; c++) line[c] = r[c] ?? string.Empty;
                table.Rows.Add(line);
            }
            if (!IsManual && MergesCheck.IsChecked == true && _grid != null)
            {
                try
                {
                    var src = ExcelReader.BuildTable(_grid, _r1, _c1, _r2, _c2,
                                                     HeadersCheck.IsChecked == true, true);
                    if (src != null && src.Merges != null) table.Merges.AddRange(src.Merges);
                }
                catch (Exception ex) { GrdLog.Log("ImportExcelWindow: merges: " + ex.Message); }
            }
            table.ColumnWidthsMm.Clear();
            for (int c = 0; c < _colCount; c++)
                table.ColumnWidthsMm.Add(c < _widthsMm.Count ? _widthsMm[c] : 80);
            table.RowHeightMm = RowHeightMm();
            return table;
        }

        private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; }
    }
}
