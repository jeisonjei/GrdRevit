using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GrdRevit.Core;
using GrdRevit.Revit;

namespace GrdRevit.Ui
{
    /// <summary>
    /// Окно «Печать в PDF»: список всех листов проекта с галочками. Отмеченные листы
    /// сохраняются в один PDF-файл с автоматическим размером страницы по каждому листу.
    /// Печать идёт пошагово (один лист за цикл ExternalEvent), поэтому шкала прогресса
    /// двигается после каждого листа. Моделесс-окно не обращается к API напрямую.
    /// </summary>
    public partial class SheetPdfPrintWindow : Window
    {
        private readonly List<SheetInfo> _master = new List<SheetInfo>();
        private readonly ObservableCollection<SheetInfo> _visible = new ObservableCollection<SheetInfo>();

        private bool _loading;
        private bool _printing;
        private bool _closed;
        private string _targetPath = string.Empty;
        private int _targetCount;
        private string _docKey = string.Empty;
        private readonly HashSet<string> _favIds = new HashSet<string>(StringComparer.Ordinal);

        public SheetPdfPrintWindow()
        {
            InitializeComponent();
            WindowTopmost.Track(this);
            SheetsList.ItemsSource = _visible;
            UpdateCounts();

            Closing += OnClosing;
            Loaded += (s, e) =>
            {
                SearchBox.Focus();
                System.Windows.Input.Keyboard.Focus(SearchBox);
                LoadSheets();
            };
        }

        private void LoadSheets()
        {
            var handler = RevitContext.SheetPrintHandler;
            var ev = RevitContext.SheetPrintEvent;
            if (handler == null || ev == null)
            {
                StatusText.Text = "Обработчик печати недоступен.";
                return;
            }
            if (_loading) return;
            _loading = true;
            StatusText.Text = "Чтение списка листов…";
            try
            {
                handler.QueueReadSheets(res =>
                {
                    try
                    {
                        if (_closed) return;
                        if (!string.IsNullOrEmpty(res.Error))
                        {
                            StatusText.Text = res.Error;
                            return;
                        }
                        if (!string.IsNullOrEmpty(res.DocKey)) _docKey = res.DocKey;
                        _favIds.Clear();
                        foreach (var id in FavoriteStore.Load(_docKey, "sheets"))
                            _favIds.Add(id);
                        _master.Clear();
                        _master.AddRange(res.Sheets);
                        foreach (var s in _master)
                            s.IsFavorite = _favIds.Contains(s.SheetId.ToString(CultureInfo.InvariantCulture));
                        ApplySavedSelection();
                        RebuildList();
                        StatusText.Text = "Листов: " + _master.Count + ".";
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
                StatusText.Text = "Не удалось запросить листы: " + ex.Message;
            }
        }

        private void RebuildList()
        {
            var q = (SearchBox.Text ?? string.Empty).Trim().ToLowerInvariant();
            bool onlyChecked = OnlyCheckedCheck.IsChecked == true;
            bool onlyFav = OnlyFavCheck.IsChecked == true;
            _visible.Clear();
            foreach (var item in _master)
            {
                if (onlyChecked && !item.IsChecked) continue;
                if (onlyFav && !item.IsFavorite) continue;
                if (q.Length == 0 || item.SearchKey.IndexOf(q, StringComparison.Ordinal) >= 0)
                    _visible.Add(item);
            }
            UpdateCounts();
        }

        /// <summary>После изменения отметок: пересчёт, сохранение и, при включённом
        /// фильтре «Только выбранные», перестроение списка (через диспетчер, чтобы не
        /// удалять строку прямо внутри события её флажка).</summary>
        private void AfterCheckChanged()
        {
            UpdateCounts();
            SaveSelection();
            if (OnlyCheckedCheck.IsChecked == true)
                Dispatcher.BeginInvoke(new Action(RebuildList));
        }

        private void OnOnlyCheckedChanged(object sender, RoutedEventArgs e)
        {
            RebuildList();
        }

        private void OnOnlyFavChanged(object sender, RoutedEventArgs e)
        {
            RebuildList();
        }

        /// <summary>Звёздочка в строке: отметить/снять избранное. Избранное хранится
        /// по документу и общее для «Печати в PDF» и «Экспорта в DWG».</summary>
        private void OnFavClick(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.Tag is SheetInfo s)) return;
            s.IsFavorite = !s.IsFavorite;
            FavoriteStore.Set(_docKey, "sheets",
                s.SheetId.ToString(CultureInfo.InvariantCulture), s.IsFavorite);
            SheetsList.Items.Refresh();
            if (OnlyFavCheck.IsChecked == true)
                Dispatcher.BeginInvoke(new Action(RebuildList));
        }

        private void OnListPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Space) return;
            // Если фокус на самом флажке, пробел обрабатывается штатно.
            if (System.Windows.Input.Keyboard.FocusedElement is CheckBox) return;

            var item = SheetsList.SelectedItem as SheetInfo;
            if (item == null) return;
            item.IsChecked = !item.IsChecked;
            SheetsList.Items.Refresh();
            AfterCheckChanged();
            e.Handled = true;
        }

        private void UpdateCounts()
        {
            int total = _master.Count;
            int sel = _master.Count(x => x.IsChecked);
            SheetCount.Text = "Всего: " + total;
            SelectedCount.Text = "Выбрано: " + sel;
            PrintBtn.IsEnabled = !_printing && sel > 0;
        }

        private List<long> CheckedIds()
        {
            return _master.Where(x => x.IsChecked).Select(x => x.SheetId).ToList();
        }

        /// <summary>Восстанавливает отметки листов, сохранённые для этого документа.</summary>
        private void ApplySavedSelection()
        {
            var saved = SavedNumbers();
            if (saved.Count == 0) return;
            foreach (var item in _master)
                item.IsChecked = saved.Contains(item.SheetNumber);
        }

        private HashSet<string> SavedNumbers()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (!string.IsNullOrEmpty(_docKey) &&
                    RevitContext.Settings.SheetSelectionByDoc.TryGetValue(_docKey, out var raw) &&
                    !string.IsNullOrEmpty(raw))
                {
                    foreach (var n in raw.Split('\n'))
                        if (n.Length > 0) set.Add(n);
                }
            }
            catch (Exception ex) { GrdLog.Log("SheetPdfPrintWindow.SavedNumbers EXCEPTION " + ex); }
            return set;
        }

        /// <summary>Запоминает набор отмеченных листов для текущего документа.</summary>
        private void SaveSelection()
        {
            try
            {
                if (string.IsNullOrEmpty(_docKey)) return;
                var numbers = _master.Where(x => x.IsChecked).Select(x => x.SheetNumber);
                var raw = string.Join("\n", numbers);
                if (raw.Length == 0) RevitContext.Settings.SheetSelectionByDoc.Remove(_docKey);
                else RevitContext.Settings.SheetSelectionByDoc[_docKey] = raw;
                RevitContext.SaveSettings();
            }
            catch (Exception ex) { GrdLog.Log("SheetPdfPrintWindow.SaveSelection EXCEPTION " + ex); }
        }

        private void SetBusy(bool busy)
        {
            _printing = busy;
            PrintBtn.IsEnabled = !busy && _master.Any(x => x.IsChecked);
            CancelBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            RasterCheck.IsEnabled = !busy;
            BlackWhiteCheck.IsEnabled = !busy;
            SheetsList.IsEnabled = !busy;
            SearchBox.IsEnabled = !busy;
            SelectAllBtn.IsEnabled = !busy;
            SelectNoneBtn.IsEnabled = !busy;
            OnlyCheckedCheck.IsEnabled = !busy;
            OnlyFavCheck.IsEnabled = !busy;
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            RebuildList();
        }

        private void OnSearchReset(object sender, RoutedEventArgs e)
        {
            SearchBox.Clear();
        }

        private void OnSelectAll(object sender, RoutedEventArgs e)
        {
            foreach (var item in _visible) item.IsChecked = true;
            SheetsList.Items.Refresh();
            AfterCheckChanged();
        }

        private void OnSelectNone(object sender, RoutedEventArgs e)
        {
            foreach (var item in _visible) item.IsChecked = false;
            SheetsList.Items.Refresh();
            AfterCheckChanged();
        }

        private void OnSheetCheckToggled(object sender, RoutedEventArgs e)
        {
            AfterCheckChanged();
        }

        private void OnPrint(object sender, RoutedEventArgs e)
        {
            var ids = CheckedIds();
            if (ids.Count == 0)
            {
                StatusText.Text = "Не отмечено ни одного листа.";
                return;
            }

            var handler = RevitContext.SheetPrintHandler;
            var ev = RevitContext.SheetPrintEvent;
            if (handler == null || ev == null)
            {
                StatusText.Text = "Обработчик печати недоступен.";
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить выбранные листы в PDF",
                Filter = "PDF (*.pdf)|*.pdf",
                DefaultExt = ".pdf",
                AddExtension = true,
                FileName = "Листы.pdf",
                OverwritePrompt = true
            };
            var dirs = RevitContext.Settings.ScheduleSnapPdfDir;
            dlg.InitialDirectory = !string.IsNullOrEmpty(dirs) && Directory.Exists(dirs)
                ? dirs
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            if (dlg.ShowDialog() != true) return;

            _targetPath = dlg.FileName;
            _targetCount = ids.Count;
            bool raster = RasterCheck.IsChecked == true;
            bool blackWhite = BlackWhiteCheck.IsChecked == true;

            try
            {
                SetBusy(true);
                Progress.Minimum = 0;
                Progress.Maximum = ids.Count;
                Progress.Value = 0;
                PercentText.Text = "0 %";
                StatusText.Text = "Подготовка…";

                handler.QueuePrintSheets(ids, _targetPath, raster, blackWhite, OnProgress);
                ev.Raise();
            }
            catch (Exception ex)
            {
                SetBusy(false);
                GrdLog.Log("SheetPdfPrintWindow.OnPrint EXCEPTION: " + ex);
                StatusText.Text = "Не удалось запустить печать: " + ex.Message;
            }
        }

        /// <summary>Ход печати приходит после каждого листа (в потоке Revit).</summary>
        private void OnProgress(SheetPrintProgress p)
        {
            if (_closed) return;

            Progress.Maximum = Math.Max(1, p.Total);
            Progress.Value = p.Done;
            PercentText.Text = p.Total > 0 ? ((int)Math.Round(p.Percent)).ToString() + " %" : string.Empty;

            if (!p.Finished)
            {
                StatusText.Text = string.IsNullOrEmpty(p.Current)
                    ? p.Phase
                    : p.Phase + ": " + p.Current + " (" + p.Done + " из " + p.Total + ")";
                try { RevitContext.SheetPrintEvent?.Raise(); }
                catch (Exception ex) { GrdLog.Log("SheetPdfPrintWindow.OnProgress raise EXCEPTION: " + ex); }
                return;
            }

            SetBusy(false);
            Progress.Value = 0;
            PercentText.Text = string.Empty;

            if (p.Cancelled)
            {
                StatusText.Text = "Печать отменена.";
                return;
            }
            if (!string.IsNullOrEmpty(p.Error))
            {
                StatusText.Text = p.Error;
                MessageBox.Show(this, p.Error, "Печать в PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RevitContext.Settings.ScheduleSnapPdfDir = Path.GetDirectoryName(_targetPath) ?? string.Empty;
            try { RevitContext.SaveSettings(); } catch (Exception ex) { GrdLog.Log("SheetPdfPrintWindow.SaveSettings EXCEPTION " + ex); }
            StatusText.Text = "Листов сохранено: " + _targetCount + " → " + _targetPath;
            try { System.Diagnostics.Process.Start(_targetPath); }
            catch (Exception ex) { GrdLog.Log("SheetPdfPrintWindow: открыть PDF EXCEPTION " + ex); }
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            if (!_printing) return;
            CancelBtn.IsEnabled = false;
            StatusText.Text = "Отмена…";
            try
            {
                RevitContext.SheetPrintHandler?.Cancel();
                RevitContext.SheetPrintEvent?.Raise();
            }
            catch (Exception ex) { GrdLog.Log("SheetPdfPrintWindow.OnCancel EXCEPTION: " + ex); }
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _closed = true;
            try { SaveSelection(); } catch { }
            if (!_printing) return;
            try
            {
                RevitContext.SheetPrintHandler?.Cancel();
                RevitContext.SheetPrintEvent?.Raise();
            }
            catch (Exception ex) { GrdLog.Log("SheetPdfPrintWindow.OnClosing EXCEPTION: " + ex); }
        }
    }
}
