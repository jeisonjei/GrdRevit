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
    /// Окно «Экспорт в DWG»: список листов или видов проекта с галочками (переключатель
    /// «Листы / Виды»). Листы — в один DWG-файл, где каждый лист становится отдельным
    /// layout (MergedViews); виды — КАЖДЫЙ в отдельный DWG-файл с именем «префикс + имя
    /// вида» (поле префикса появляется в режиме «Виды»). Экспорт выполняется в потоке
    /// Revit через ExternalEvent, окно — моделессое и к API напрямую не обращается.
    /// Есть избранные элементы (★): для листов они общие с окном «Печать в PDF»,
    /// для видов — с окном «Управляющий видами»; последний путь сохранения запоминается.
    /// </summary>
    public partial class SheetDwgExportWindow : Window
    {
        private readonly List<IDwgExportItem> _masterSheets = new List<IDwgExportItem>();
        private readonly List<IDwgExportItem> _masterViews = new List<IDwgExportItem>();
        private readonly ObservableCollection<IDwgExportItem> _visible = new ObservableCollection<IDwgExportItem>();

        private bool _loading;
        private bool _exporting;
        private bool _closed;
        private string _targetPath = string.Empty;
        private int _targetCount;
        private bool _targetViews;
        private string _docKey = string.Empty;

        /// <summary>Какая коллекция сейчас показана в списке: виды (иначе листы).</summary>
        private bool ViewsMode => RbViews != null && RbViews.IsChecked == true;

        private List<IDwgExportItem> CurrentMaster()
        {
            return ViewsMode ? _masterViews : _masterSheets;
        }

        public SheetDwgExportWindow()
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
                Load();
            };
        }

        private void Load()
        {
            var handler = RevitContext.DwgExportHandler;
            var ev = RevitContext.DwgExportEvent;
            if (handler == null || ev == null)
            {
                StatusText.Text = "Обработчик экспорта DWG недоступен.";
                return;
            }
            if (_loading) return;
            _loading = true;
            StatusText.Text = "Чтение списков…";
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
                        var favs = FavoriteStore.Load(_docKey, "sheets");
                        _masterSheets.Clear();
                        foreach (var s in res.Sheets)
                        {
                            s.IsFavorite = favs.Contains(s.SheetId.ToString(CultureInfo.InvariantCulture));
                            _masterSheets.Add(s);
                        }
                        RebuildList();
                        StatusText.Text = "Листов: " + _masterSheets.Count + ".";
                    }
                    finally
                    {
                        _loading = false;
                    }
                });
                handler.QueueReadViews(res =>
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
                        var favs = FavoriteStore.Load(_docKey, "views");
                        _masterViews.Clear();
                        foreach (var v in res.Views)
                        {
                            v.IsFavorite = favs.Contains(v.ViewId.ToString(CultureInfo.InvariantCulture));
                            _masterViews.Add(v);
                        }
                        RebuildList();
                        StatusText.Text = "Видов: " + _masterViews.Count + ".";
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
                StatusText.Text = "Не удалось запросить списки: " + ex.Message;
            }
        }

        private void RebuildList()
        {
            var q = (SearchBox.Text ?? string.Empty).Trim().ToLowerInvariant();
            bool onlyChecked = OnlyCheckedCheck.IsChecked == true;
            bool onlyFav = OnlyFavCheck.IsChecked == true;
            _visible.Clear();
            foreach (var item in CurrentMaster())
            {
                if (onlyChecked && !item.IsChecked) continue;
                if (onlyFav && !item.IsFavorite) continue;
                if (q.Length == 0 || item.SearchKey.IndexOf(q, StringComparison.Ordinal) >= 0)
                    _visible.Add(item);
            }
            ApplySourceUi();
            UpdateCounts();
        }

        /// <summary>Заголовок, описание и счётчик группы зависят от выбранного источника.</summary>
        private void ApplySourceUi()
        {
            bool views = ViewsMode;
            if (ListGroupBox != null)
                ListGroupBox.Header = views ? "Виды" : "Листы";
            if (HintText != null)
            {
                HintText.Text = views
                    ? "Отметьте виды и нажмите «Экспорт в DWG»: каждый выбранный вид (план, разрез, фасад, 3D, узел) сохраняется в отдельный DWG-файл в выбранной папке с именем «префикс + имя вида». Нажмите «Экспорт в DWG» и укажите папку. Звёздочка ★ отмечает избранные виды — они общие с окном «Управляющий видами»."
                    : "Отметьте листы и нажмите «Экспорт в DWG» — выбранные листы сохраняются в один DWG-файл, где каждый лист становится отдельным layout. Последний путь сохранения запоминается. Звёздочка ★ отмечает избранные листы — они общие с окном «Печать в PDF».";
            }
            if (PrefixPanel != null)
                PrefixPanel.Visibility = views ? Visibility.Visible : Visibility.Collapsed;
            if (SourceStatus != null)
            {
                var master = CurrentMaster();
                SourceStatus.Text = master.Count > 0
                    ? (views ? "Видов в документе: " + master.Count : "Листов в документе: " + master.Count)
                    : (views ? "Список видов загружается…" : "Список листов загружается…");
            }
        }

        private void AfterCheckChanged()
        {
            UpdateCounts();
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

        /// <summary>Переключатель «Листы / Виды».</summary>
        private void OnSourceChanged(object sender, RoutedEventArgs e)
        {
            if (!(sender is RadioButton rb) || rb.IsChecked != true) return;
            // Во время InitializeComponent сам список ещё не построен — не перестраиваем.
            if (ListGroupBox == null || HintText == null) return;
            StatusText.Text = ViewsMode ? "Виды — отметьте нужные для экспорта." : "Листы — отметьте нужные для экспорта.";
            RebuildList();
        }

        /// <summary>Звёздочка в строке: отметить/снять избранное. Избранное хранится
        /// по документу: для листов общее с «Печатью в PDF», для видов — с «Управляющим видами».</summary>
        private void OnFavClick(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.Tag is IDwgExportItem item)) return;
            item.IsFavorite = !item.IsFavorite;
            FavoriteStore.Set(_docKey, item.FavScope,
                item.IdValue.ToString(CultureInfo.InvariantCulture), item.IsFavorite);
            SheetsList.Items.Refresh();
            if (OnlyFavCheck.IsChecked == true)
                Dispatcher.BeginInvoke(new Action(RebuildList));
        }

        /// <summary>Отметить все избранные элементы и снять остальные.</summary>
        private void OnSelectFav(object sender, RoutedEventArgs e)
        {
            foreach (var item in CurrentMaster())
                item.IsChecked = item.IsFavorite;
            SheetsList.Items.Refresh();
            AfterCheckChanged();
        }

        private void OnListPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Space) return;
            if (System.Windows.Input.Keyboard.FocusedElement is CheckBox) return;

            var item = SheetsList.SelectedItem as IDwgExportItem;
            if (item == null) return;
            item.IsChecked = !item.IsChecked;
            SheetsList.Items.Refresh();
            AfterCheckChanged();
            e.Handled = true;
        }

        private void UpdateCounts()
        {
            var master = CurrentMaster();
            int total = master.Count;
            int sel = master.Count(x => x.IsChecked);
            int fav = master.Count(x => x.IsFavorite);
            SheetCount.Text = "Всего: " + total + "   ★: " + fav;
            SelectedCount.Text = "Выбрано: " + sel;
            ExportBtn.IsEnabled = !_exporting && sel > 0;
        }

        private List<long> CheckedIds()
        {
            return CurrentMaster().Where(x => x.IsChecked).Select(x => x.IdValue).ToList();
        }

        private void SetBusy(bool busy)
        {
            _exporting = busy;
            ExportBtn.IsEnabled = !busy && CurrentMaster().Any(x => x.IsChecked);
            SheetsList.IsEnabled = !busy;
            SearchBox.IsEnabled = !busy;
            SelectAllBtn.IsEnabled = !busy;
            SelectNoneBtn.IsEnabled = !busy;
            SelectFavBtn.IsEnabled = !busy;
            OnlyCheckedCheck.IsEnabled = !busy;
            OnlyFavCheck.IsEnabled = !busy;
            RbSheets.IsEnabled = !busy;
            RbViews.IsEnabled = !busy;
            if (PrefixBox != null) PrefixBox.IsEnabled = !busy;
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

        private void OnExport(object sender, RoutedEventArgs e)
        {
            bool views = ViewsMode;
            var ids = CheckedIds();
            if (ids.Count == 0)
            {
                StatusText.Text = views ? "Не отмечено ни одного вида." : "Не отмечено ни одного листа.";
                return;
            }

            var handler = RevitContext.DwgExportHandler;
            var ev = RevitContext.DwgExportEvent;
            if (handler == null || ev == null)
            {
                StatusText.Text = "Обработчик экспорта DWG недоступен.";
                return;
            }

            var last = RevitContext.Settings.SheetDwgExportPath;
            string lastDir = null;
            if (!string.IsNullOrEmpty(last))
            {
                if (File.Exists(last)) lastDir = Path.GetDirectoryName(last);
                else if (Directory.Exists(last)) lastDir = last;
            }
            if (string.IsNullOrEmpty(lastDir))
                lastDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            try
            {
                if (views)
                {
                    _targetPath = PickViewsExportFolder(lastDir);
                    if (_targetPath == null) return;
                    _targetCount = ids.Count;
                    _targetViews = true;

                    StartBusyProgress("Экспорт видов…");
                    handler.QueueExportViews(ids, _targetPath, PrefixBox?.Text ?? string.Empty, OnProgress);
                    ev.Raise();
                }
                else
                {
                    var dlg = new Microsoft.Win32.SaveFileDialog
                    {
                        Title = "Экспорт листов в DWG (каждый лист — отдельный layout)",
                        Filter = "DWG (*.dwg)|*.dwg",
                        DefaultExt = ".dwg",
                        AddExtension = true,
                        FileName = "Листы.dwg",
                        OverwritePrompt = true,
                        InitialDirectory = lastDir
                    };
                    if (File.Exists(last))
                    {
                        dlg.InitialDirectory = Path.GetDirectoryName(last);
                        dlg.FileName = Path.GetFileName(last);
                    }
                    if (dlg.ShowDialog() != true) return;

                    _targetPath = dlg.FileName;
                    _targetCount = ids.Count;
                    _targetViews = false;

                    StartBusyProgress("Экспорт листов…");
                    handler.QueueExportSheets(ids, _targetPath, OnProgress);
                    ev.Raise();
                }
            }
            catch (Exception ex)
            {
                SetBusy(false);
                GrdLog.Log("SheetDwgExportWindow.OnExport EXCEPTION: " + ex);
                StatusText.Text = "Не удалось запустить экспорт: " + ex.Message;
            }
        }

        /// <summary>Виды: диалог выбора папки (SaveFileDialog используется только ради
        /// папки; имя файла игнорируется — оно формируется из префикса и имени вида).
        /// Возвращает папку или null, если пользователь отменил выбор.</summary>
        private string PickViewsExportFolder(string lastDir)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Виды: выберите папку — каждый вид в отдельный файл .dwg",
                Filter = "DWG (*.dwg)|*.dwg",
                DefaultExt = ".dwg",
                AddExtension = true,
                FileName = "Виды.dwg",
                OverwritePrompt = false,
                InitialDirectory = lastDir
            };
            if (dlg.ShowDialog() != true) return null;
            var folder = Path.GetDirectoryName(dlg.FileName);
            if (string.IsNullOrEmpty(folder)) folder = lastDir;
            return folder;
        }

        private void StartBusyProgress(string status)
        {
            SetBusy(true);
            Progress.Minimum = 0;
            Progress.Maximum = Math.Max(1, _targetCount);
            Progress.Value = 0;
            PercentText.Text = "0 %";
            StatusText.Text = status;
        }

        /// <summary>Итог экспорта приходит по завершении (в потоке Revit).</summary>
        private void OnProgress(DwgExportProgress p)
        {
            if (_closed) return;

            Progress.Maximum = Math.Max(1, p.Total);
            Progress.Value = p.Done;
            PercentText.Text = p.Total > 0 ? ((int)Math.Round(100.0 * p.Done / p.Total)) + " %" : string.Empty;

            if (!p.Finished)
            {
                StatusText.Text = string.IsNullOrEmpty(p.Current) ? p.Phase : p.Phase + ": " + p.Current;
                return;
            }

            SetBusy(false);
            Progress.Value = 0;
            PercentText.Text = string.Empty;

            if (!string.IsNullOrEmpty(p.Error))
            {
                StatusText.Text = p.Error;
                MessageBox.Show(this, p.Error, "Экспорт в DWG", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Запоминаем папку/файл для следующего раза: для видов — папку плюс
            // «Виды.dwg» (из папки берётся только каталог), для листов — полный путь файла.
            RevitContext.Settings.SheetDwgExportPath = _targetViews
                ? Path.Combine(_targetPath, "Виды.dwg")
                : _targetPath;
            try { RevitContext.SaveSettings(); } catch (Exception ex) { GrdLog.Log("SheetDwgExportWindow.SaveSettings EXCEPTION " + ex); }
            StatusText.Text = (_targetViews ? "Видов экспортировано: " : "Листов экспортировано: ") +
                              (p.Done > 0 ? p.Done : _targetCount) + " → " + _targetPath;
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _closed = true;
        }
    }
}