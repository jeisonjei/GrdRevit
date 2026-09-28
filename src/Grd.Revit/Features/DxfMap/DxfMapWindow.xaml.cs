using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrdRevit.Core;
using GrdRevit.Revit;
using Microsoft.Win32;

namespace GrdRevit.Ui
{
    /// <summary>
    /// Окно «DXF — план отопления с приборами»: отрисовка чертежа из DXF с операциями
    /// pan/zoom/выделение рамкой, выбор марок приборов прямо на плане и применение к Revit:
    ///   - «Применить к выделенному» — по текущему выделению в Revit;
    ///   - режим «Применить к Revit» — включил, кликнул прибор на плане, затем клик по
    ///     элементу в Revit: марка/тип/параметры применяются к этому элементу сразу.
    /// </summary>
    public partial class DxfMapWindow : Window
    {
        public static DxfMapWindow Instance;

        private DxfPlanResult _plan;
        private GrdDevice _chosen;
        private SelectionWatcher _watcher;
        private string _planSummary = string.Empty;

        private bool _applyMode;
        private bool _skipNextWatcher;

        // Guard от повторного применения при быстрых кликах по Revit.
        private int _lastApplyTick;

        // Тип (марка), выбранный в блоке «Типы радиаторов в выборе»: его приборы подсвечиваются на плане.
        private string _activeType;

        // Режим сортировки списка типов: по типу (А-Я) вместо сортировки по количеству.
        private bool _sortByType;

        // Защита от авто-применения марки, когда выделение в Revit меняем мы сами (синхронизация с планом).
        private int _syncGuard;

        // Строки списка несоответствий (заголовки групп по типам DXF + строки приборов).
        private readonly ObservableCollection<MismatchRow> _mismatchRows = new ObservableCollection<MismatchRow>();

        public DxfMapWindow()
        {
            InitializeComponent();
            WindowTopmost.Track(this);

            MismatchItems.ItemsSource = _mismatchRows;
            Map.SelectionChanged += OnMapSelectionChanged;
            Map.WarningsChanged += OnWarningsChanged;
            Map.ShowValveSettings = ValveLabelsButton.IsChecked == true;

            // Восстанавливаем последний путь из настроек и сразу показываем план.
            var last = RevitContext.Settings.LastDxfPath;
            if (!string.IsNullOrEmpty(last) && File.Exists(last))
                LoadDxf(last);
        }

        private void OnOpenDxf(object sender, RoutedEventArgs e)
        {
            var last = RevitContext.Settings.LastDxfPath;
            var dlg = new OpenFileDialog
            {
                Title = "Открыть план отопления (DXF из расчёта КСМ)",
                Filter = "DXF-файлы (*.dxf)|*.dxf|Все файлы (*.*)|*.*",
                InitialDirectory = string.IsNullOrEmpty(last) ? string.Empty : Path.GetDirectoryName(last),
                FileName = string.IsNullOrEmpty(last) ? string.Empty : Path.GetFileName(last),
                Multiselect = false
            };
            if (dlg.ShowDialog(this) != true) return;
            LoadDxf(dlg.FileName);
        }

        private void LoadDxf(string path)
        {
            try
            {
                Cursor = System.Windows.Input.Cursors.Wait;
                _plan = DxfCalloutParser.Parse(path);

                RevitContext.Settings.LastDxfPath = path;
                RevitContext.SaveSettings();

                FileLabel.Text = Path.GetFileName(path);
                Map.SetContent(_plan.Lines, _plan.Heaters, _plan.Texts);

                if (DiagButton.IsChecked == true)
                    UpdateDiagnostics();

                _planSummary = $"Приборов: {_plan.HeaterCount}  |  Марок: {_plan.MarkDevices.Count}  |  Этажей: {_plan.FloorCount}  |  Линий плана: {_plan.LineCount}  |  Выносок: {_plan.LeaderCount}";
                UpdateSummary();
                Map.FitAll();

                if (_plan.ParseMessages.Count > 0)
                    SnackBar.Show("Файл прочитан с замечаниями: " + _plan.ParseMessages[0], SnackBarKind.Info);
            }
            catch (Exception ex)
            {
                GrdLog.Log("DxfMapWindow.LoadDxf: EXCEPTION: " + ex);
                SnackBar.Show("Не удалось прочитать DXF: " + ex.Message, SnackBarKind.Error);
            }
            finally
            {
                Cursor = System.Windows.Input.Cursors.Arrow;
            }
        }

        private void OnFitAll(object sender, RoutedEventArgs e)
        {
            Map.FitAll();
        }

        private void OnValveLabelsChanged(object sender, RoutedEventArgs e)
        {
            if (Map == null) return;
            Map.ShowValveSettings = ValveLabelsButton.IsChecked == true;
        }

        private void OnDiagChanged(object sender, RoutedEventArgs e)
        {
            if (DiagGrid == null) return;
            DiagGrid.Visibility = DiagButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (DiagButton.IsChecked == true)
                UpdateDiagnostics();
        }

        private void UpdateDiagnostics()
        {
            try
            {
                if (_plan == null)
                {
                    DiagGrid.ItemsSource = null;
                    return;
                }

                var rows = _plan.Heaters
                    .Select(h => new MarkDiagRow
                    {
                        Seq = h.Seq,
                        Floor = h.Floor + 1,
                        Block = h.Block,
                        Size = h.Size,
                        TruthMark = h.TruthMark,
                        SuggestedMark = h.SuggestedMark ?? h.Device?.Mark,
                        TruthValveSetting = h.TruthValveSetting,
                        MarkSource = h.MarkSource ?? "выноска не найдена",
                        TruthDistance = h.TruthDistance
                    })
                    .ToList();

                DiagGrid.ItemsSource = rows;
            }
            catch (Exception ex)
            {
                GrdLog.Log("DxfMapWindow.UpdateDiagnostics: EXCEPTION: " + ex);
            }
        }

        private sealed class MarkDiagRow
        {
            public int Seq { get; set; }
            public int Floor { get; set; }
            public string Block { get; set; }
            public double? Size { get; set; }
            public string TruthMark { get; set; }
            public string SuggestedMark { get; set; }
            public string TruthValveSetting { get; set; }
            public string MarkSource { get; set; }
            public double? TruthDistance { get; set; }
        }

        private void OnOpenMarksSettings(object sender, RoutedEventArgs e)
        {
            var window = MarksMapWindow.Instance;
            if (window == null)
            {
                window = new MarksMapWindow();
                window.Closed += (s, ev) => MarksMapWindow.Instance = null;
                MarksMapWindow.Instance = window;
            }

            if (!window.IsVisible)
            {
                new System.Windows.Interop.WindowInteropHelper(window)
                {
                    Owner = RevitContext.MainWindowHandle
                };
                window.Show();
            }
            else
            {
                window.Activate();
            }
        }

        private void OnClearSelection(object sender, RoutedEventArgs e)
        {
            Map.ClearSelection();
        }

private void OnMapSelectionChanged()
        {
            // Подсветка «не совпадают» живёт только для текущего выбора: сняли — очистили.
            Map.ClearMismatch();
            UpdateSelectionTypes();
            var sel = Map.Selected;
            if (sel.Count == 1)
            {
                var heater = sel[0];
                if (heater.Device != null)
                {
                    _chosen = heater.Device;
                    var sb = new System.Text.StringBuilder();
                    sb.Append("Блок: ").Append(heater.Block);
                    sb.Append("  |  Этаж: ").Append((heater.Floor + 1).ToString(CultureInfo.InvariantCulture));
                    if (heater.Size.HasValue)
                        sb.Append("  |  Размер: ").Append(heater.Size.Value.ToString("0", CultureInfo.InvariantCulture)).Append(" мм");
                    sb.AppendLine();
                    sb.Append("Марка: ").Append(heater.SuggestedMark ?? "-");
                    if (!string.IsNullOrEmpty(heater.Device?.ValveSetting))
                        sb.Append("  |  Настройка: n=").Append(heater.Device.ValveSetting);
                    if (!string.IsNullOrEmpty(heater.MarkSource))
                        sb.Append("  |  Источник: ").Append(heater.MarkSource);
                    ChosenLabel.Text = sb.ToString();
                    ApplySelButton.IsEnabled = true;
                    ApplyToViewButton.IsEnabled = true;

                    // Новый порядок работы: выбор прибора на плане сразу включает режим
                    // применения — следующим кликом по элементу в Revit марка применяется.
                    if (ApplyModeButton.IsChecked != true)
                        ApplyModeButton.IsChecked = true;
                }
                else
                {
                    _chosen = null;
                    ChosenLabel.Text = $"Блок: {heater.Block}. Выноска не найдена — марка не назначена.";
                    ApplySelButton.IsEnabled = false;
                }
            }
            else
            {
                bool any = sel.Count > 0;
                _chosen = any ? sel[0].Device : null;
                ChosenLabel.Text = any
                    ? $"Выбрано приборов: {sel.Count}"
                    : "Выберите прибор на плане кликом или рамкой.";
                ApplySelButton.IsEnabled = any && _chosen != null;
                ApplyToViewButton.IsEnabled = any;

                // Выбор снят — режим применения тоже выключаем, чтобы случайный клик
                // по Revit не давал ошибку «сначала выберите прибор».
                if (!any && ApplyModeButton.IsChecked == true)
                    ApplyModeButton.IsChecked = false;
            }

            SyncSelectionToRevit();
        }

        // ---------- Выделение соответствующих элементов в Revit ----------

        /// <summary>
        /// Выбранные на плане приборы применяются к текущему виду тем же совмещением координат,
        /// что и «Применить к текущему виду» (PlanAligner). Результат — выделение в Revit.
        /// </summary>
        private void SyncSelectionToRevit(bool force = false)
        {
            try
            {
                if (RevitContext.UiApp?.ActiveUIDocument == null) return;
                var on = SyncRevitButton?.IsChecked == true;
                if (!on && !force) return;
                if (_plan == null) return;

                var handler = RevitContext.SelectInRevitHandler;
                var ev = RevitContext.SelectInRevitEvent;
                if (handler == null || ev == null) return;

                _syncGuard++;
                try
                {
                    handler.Queue(_plan.Heaters, Map.Selected.ToList(), RevitContext.Settings, OnSelectInRevitResult);
                    ev.Raise();
                }
                catch
                {
                    _syncGuard--;
                    throw;
                }
            }
            catch (Exception ex)
            {
                GrdLog.Log("DxfMapWindow.SyncSelectionToRevit: EXCEPTION: " + ex);
            }
        }

        private void OnSelectInRevitResult(Revit.SelectInRevitResult result)
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke((Action)(() =>
            {
                _syncGuard = Math.Max(0, _syncGuard - 1);
                UpdateTypeCheck(result);
                UpdateMismatchHighlight(result);
                if (result == null) return;
                // Ошибка совмещения/пустой результат — сообщаем; успех Revit показывает сам (подсветка).
                if (!result.Aligned || result.Count == 0)
                {
                    if (!string.IsNullOrEmpty(result.Summary) &&
                        !result.Summary.StartsWith("Выбор на плане", StringComparison.Ordinal))
                        ModeHint.Text = "Revit: " + result.Summary;
                }
            }));
        }

        /// <summary>
        /// Показывает результат проверки «семейство/тип элемента ↔ марка DXF» для выбранных приборов:
        /// аккуратная цветная карточка с вердиктом, а при нескольких выбранных — ещё и список
        /// несоответствий, сгруппированных по типам DXF (каждую строку можно отметить галочкой).
        /// </summary>
        private void UpdateTypeCheck(Revit.SelectInRevitResult result)
        {
            var checks = result?.Checks;
            if (checks == null || checks.Count == 0)
            {
                TypeCheckPanel.Visibility = Visibility.Collapsed;
                MismatchPanel.Visibility = Visibility.Collapsed;
                _mismatchRows.Clear();
                return;
            }

            TypeCheckPanel.Visibility = Visibility.Visible;

            var match = checks.Count(c => c.State == Revit.TypeCheckState.Match);
            var mismatch = checks.Count(c => c.State == Revit.TypeCheckState.Mismatch);
            var unknown = checks.Count - match - mismatch;

            if (checks.Count == 1)
                ShowSingleCheck(checks[0]);
            else
                ShowSummaryCheck(mismatch, unknown, checks.Count - mismatch - unknown);

            BuildMismatchList(checks);
        }

        private void ShowSingleCheck(Revit.TypeCheckResult c)
        {
            string title, detail;
            Brush bg, border, titleBrush;

            if (c.State == Revit.TypeCheckState.Match)
            {
                title = "Тип соответствует DXF";
                detail = "В Revit: «" + c.Actual + "»";
                bg = CheckGreenBg; border = CheckGreenBorder; titleBrush = CheckGreenTitle;
            }
            else if (c.State == Revit.TypeCheckState.Mismatch)
            {
                if (c.FamilyMismatch)
                {
                    title = "Семейство не соответствует DXF";
                    bg = CheckRedBg; border = CheckRedBorder; titleBrush = CheckRedTitle;
                }
                else
                {
                    title = "Тип не соответствует DXF";
                    bg = CheckOrangeBg; border = CheckOrangeBorder; titleBrush = CheckOrangeTitle;
                }
                detail = "В Revit: «" + (string.IsNullOrEmpty(c.Actual) ? "(элемент не найден)" : c.Actual) + "»" +
                         "\nОжидается: «" + c.Expected + "»";
            }
            else
            {
                title = "Проверка недоступна";
                detail = c.Reason;
                if (!string.IsNullOrEmpty(c.Heater?.Device?.Mark))
                    detail += " («" + c.Heater.Device.Mark + "»).";
                bg = CheckGrayBg; border = CheckGrayBorder; titleBrush = CheckGrayTitle;
            }

            SetCheckCard(bg, border, titleBrush, title, detail);
        }

        private void ShowSummaryCheck(int mismatch, int unknown, int match)
        {
            var parts = new List<string>();
            if (match > 0) parts.Add(match + " совпадает");
            if (unknown > 0) parts.Add(unknown + " без карты");
            if (mismatch > 0) parts.Add(mismatch + " НЕ совпадает");

            Brush bg, border, titleBrush;
            if (mismatch > 0) { bg = CheckRedBg; border = CheckRedBorder; titleBrush = CheckRedTitle; }
            else if (unknown > 0) { bg = CheckGrayBg; border = CheckGrayBorder; titleBrush = CheckGrayTitle; }
            else { bg = CheckGreenBg; border = CheckGreenBorder; titleBrush = CheckGreenTitle; }

            string detail = mismatch > 0
                ? "Несовпавшие перечислены ниже — отметьте галочками и нажмите «Применить к выделенному»."
                : string.Empty;

            SetCheckCard(bg, border, titleBrush, "Проверка типа: " + string.Join(", ", parts) + ".", detail);
        }

        private void SetCheckCard(Brush bg, Brush border, Brush titleBrush, string title, string detail)
        {
            TypeCheckCard.Background = bg;
            TypeCheckCard.BorderBrush = border;
            TypeCheckTitle.Foreground = titleBrush;
            TypeCheckTitle.Text = title;
            TypeCheckDetail.Text = detail;
        }

        /// <summary>
        /// Строит список несоответствий, сгруппированных по типу из DXF (марка прибора).
        /// Состояние галочки каждой строки — «прибор выбран на плане».
        /// </summary>
        private void BuildMismatchList(List<Revit.TypeCheckResult> checks)
        {
            var mism = checks.Where(c => c.State == Revit.TypeCheckState.Mismatch && c.Heater != null).ToList();
            if (mism.Count == 0)
            {
                MismatchPanel.Visibility = Visibility.Collapsed;
                _mismatchRows.Clear();
                SelectFamilyMismatchButton.IsEnabled = false;
                SelectTypeMismatchButton.IsEnabled = false;
                return;
            }

            MismatchPanel.Visibility = Visibility.Visible;
            MismatchItems.Visibility = Visibility.Visible;
            _mismatchRows.Clear();

            int countFamily = 0, countType = 0;
            var groups = mism
                .GroupBy(c => BuildTypeLabel(c.Heater))
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var g in groups)
            {
                var first = g.First();
                string expected = string.IsNullOrEmpty(first.Expected)
                    ? "не задано"
                    : "«" + first.Expected + "»";

                _mismatchRows.Add(new MismatchRow
                {
                    IsHeader = true,
                    HeaderText = g.Key + " — не совпадает: " + g.Count(),
                    ExpectedText = "Ожидается: " + expected
                });

                foreach (var c in g)
                {
                    bool family = c.FamilyMismatch;
                    if (family) countFamily++;
                    else if (c.TypeMismatch) countType++;

                    string block = c.Heater.Block ?? "?";
                    string floor = (c.Heater.Floor + 1).ToString(CultureInfo.InvariantCulture);
                    _mismatchRows.Add(new MismatchRow
                    {
                        Heater = c.Heater,
                        ElementId = c.ElementId,
                        IsFamilyMismatch = family,
                        IsTypeMismatch = !family && c.TypeMismatch,
                        RowTitle = "Блок " + block + " · Этаж " + floor,
                        RowDetail = "В Revit: «" + (string.IsNullOrEmpty(c.Actual) ? "(элемент не найден)" : c.Actual) + "»",
                        BadgeText = family ? "Семейство" : "Тип",
                        BadgeBrush = family ? FamilyBadgeBrush : TypeBadgeBrush,
                        IsSelected = Map.Selected.Contains(c.Heater)
                    });
                }
            }

            SelectFamilyMismatchButton.IsEnabled = countFamily > 0;
            SelectTypeMismatchButton.IsEnabled = countType > 0;
        }

        /// <summary>Отметка строки несоответствия: добавляет/убирает прибор в выделении плана (далее синхронизируется с Revit).</summary>
        private void OnMismatchToggled(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox cb) || cb.IsChecked == null) return;
            if (!(cb.DataContext is MismatchRow row) || row.IsHeader || row.Heater == null) return;
            try
            {
                if (cb.IsChecked.Value)
                    Map.AddToSelection(row.Heater);
                else
                    Map.RemoveFromSelection(row.Heater);
            }
            catch (Exception ex)
            {
                GrdLog.Log("DxfMapWindow.OnMismatchToggled: EXCEPTION: " + ex);
            }
        }

        /// <summary>Выбрать на плане только несоответствия семейств или типов (затем синхронизация с Revit).</summary>
        private void OnSelectMismatches(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(sender, SelectFamilyMismatchButton))
                SelectOnlyMismatches(r => r.IsFamilyMismatch, "несоответствий семейств");
            else
                SelectOnlyMismatches(r => r.IsTypeMismatch, "несоответствий типов");
        }

        private void SelectOnlyMismatches(Func<MismatchRow, bool> pred, string what)
        {
            var heaters = _mismatchRows
                .Where(r => !r.IsHeader && r.Heater != null && pred(r))
                .Select(r => r.Heater)
                .Distinct()
                .ToList();
            if (heaters.Count == 0)
            {
                SnackBar.Show("Нет " + what + " в текущем выборе.", SnackBarKind.Info);
                return;
            }
            try
            {
                Map.SetSelection(heaters);
            }
            catch (Exception ex)
            {
                GrdLog.Log("DxfMapWindow.SelectOnlyMismatches: EXCEPTION: " + ex);
            }
        }

        /// <summary>Строка списка несоответствий: заголовок группы по типу DXF или прибор с фактическим типом Revit.</summary>
        private sealed class MismatchRow
        {
            public bool IsHeader { get; set; }
            public bool IsItem => !IsHeader;
            public string HeaderText { get; set; }
            public string ExpectedText { get; set; }
            public string RowTitle { get; set; }
            public string RowDetail { get; set; }
            public string BadgeText { get; set; }
            public Brush BadgeBrush { get; set; }
            public bool IsSelected { get; set; }
            public bool IsFamilyMismatch { get; set; }
            public bool IsTypeMismatch { get; set; }
            public DxfCalloutItem Heater { get; set; }
            public Autodesk.Revit.DB.ElementId ElementId { get; set; }
        }

        private static Brush MakeBrush(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        private static readonly Brush CheckGreenBg = MakeBrush("#EAF6EE");
        private static readonly Brush CheckGreenBorder = MakeBrush("#5FBB73");
        private static readonly Brush CheckGreenTitle = MakeBrush("#1E7B32");
        private static readonly Brush CheckRedBg = MakeBrush("#FBECEC");
        private static readonly Brush CheckRedBorder = MakeBrush("#D16A60");
        private static readonly Brush CheckRedTitle = MakeBrush("#B3261E");
        private static readonly Brush CheckOrangeBg = MakeBrush("#FFF4E5");
        private static readonly Brush CheckOrangeBorder = MakeBrush("#E0A33D");
        private static readonly Brush CheckOrangeTitle = MakeBrush("#9C5D00");
        private static readonly Brush CheckGrayBg = MakeBrush("#F3F4F6");
        private static readonly Brush CheckGrayBorder = MakeBrush("#C9CDD3");
        private static readonly Brush CheckGrayTitle = MakeBrush("#566070");
        private static readonly Brush FamilyBadgeBrush = MakeBrush("#B3261E");
        private static readonly Brush TypeBadgeBrush = MakeBrush("#E07B00");

        /// <summary>
        /// Подсвечивает на плане выбранные приборы, чей тип в Revit не соответствует марке DXF
        /// (красное кольцо). Нет результата/нет выбранных — подсветка снимается.
        /// </summary>
        private void UpdateMismatchHighlight(Revit.SelectInRevitResult result)
        {
            var checks = result?.Checks;
            if (checks == null || checks.Count == 0)
            {
                Map.ClearMismatch();
                return;
            }

            Map.SetMismatch(checks
                .Where(c => c.State == Revit.TypeCheckState.Mismatch)
                .Select(c => c.Heater));
        }

        private void OnWarningsChanged()
        {
            UpdateSummary();
        }

        private void UpdateSelectionTypes()
        {
            try
            {
                var sel = Map?.Selected ?? new List<DxfCalloutItem>();
                var rows = sel
                    .GroupBy(h => BuildTypeLabel(h))
                    .Select(g => new TypeCountRow
                    {
                        Type = g.Key,
                        Count = g.Count()
                    })
                    .ToList();

                if (_sortByType)
                    rows = rows.OrderBy(r => r.Type, StringComparer.OrdinalIgnoreCase).ToList();
                else
                    rows = rows.OrderByDescending(r => r.Count)
                               .ThenBy(r => r.Type, StringComparer.OrdinalIgnoreCase)
                               .ToList();

                // Если активного типа больше нет в выборе — снимаем подсветку.
                if (_activeType != null && rows.All(r => !string.Equals(r.Type, _activeType, StringComparison.Ordinal)))
                {
                    _activeType = null;
                    Map.ClearTypeHighlight();
                }

                foreach (var r in rows)
                {
                    r.IsActive = string.Equals(r.Type, _activeType, StringComparison.Ordinal);
                    r.SwatchBrush = TypeSwatchBrush(r.Type);
                }

                bool any = rows.Count > 0;
                TypesEmptyLabel.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
                TypesList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
                TypesList.ItemsSource = rows;
            }
            catch (Exception ex)
            {
                GrdLog.Log("DxfMapWindow.UpdateSelectionTypes: EXCEPTION: " + ex);
            }
        }

        private void OnTypesSortChanged(object sender, RoutedEventArgs e)
        {
            if (TypesList == null) return;
            _sortByType = TypesSortButton.IsChecked == true;
            UpdateSelectionTypes();
        }

        private static string BuildTypeLabel(DxfCalloutItem h)
        {
            var mark = h.SuggestedMark ?? h.TruthMark ?? h.Block;
            if (string.IsNullOrWhiteSpace(mark)) mark = "(без марки)";
            return mark;
        }

        private void OnTypeRowClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (_plan == null) return;
                var row = (sender as FrameworkElement)?.DataContext as TypeCountRow;
                if (row == null) return;

                if (string.Equals(_activeType, row.Type, StringComparison.Ordinal))
                {
                    _activeType = null;
                    Map.ClearTypeHighlight();
                }
                else
                {
                    _activeType = row.Type;
                    Map.SetTypeHighlight(_plan.Heaters.Where(h => BuildTypeLabel(h) == row.Type));
                }
                UpdateSelectionTypes();
            }
            catch (Exception ex)
            {
                GrdLog.Log("DxfMapWindow.OnTypeRowClick: EXCEPTION: " + ex);
            }
        }

        private sealed class TypeCountRow
        {
            public string Type { get; set; }
            public int Count { get; set; }
            public bool IsActive { get; set; }
            public Brush SwatchBrush { get; set; }
        }

        // Светлая палитра для маркеров типов; цвет привязан к имени типа (стабильный хеш),
        // чтобы один и тот же тип оставался одного цвета независимо от сортировки списка.
        private static readonly string[] TypePalette =
        {
            "#FFE0B2", "#FFCDD2", "#C8E6C9", "#BBDEFB", "#E1BEE7", "#FFF9C4",
            "#B2DFDB", "#F8BBD0", "#CFD8DC", "#D7CCC8", "#DCEDC8", "#F0F4C3"
        };

        private static Brush TypeSwatchBrush(string type)
        {
            uint h = 2166136261;
            foreach (char c in type ?? string.Empty)
            {
                h ^= c;
                h *= 16777619;
            }
            var color = (Color)ColorConverter.ConvertFromString(TypePalette[h % (uint)TypePalette.Length]);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private void UpdateSummary()
        {
            string baseText = _planSummary;
            if (Map.HasWarnings)
                baseText += $"  |  Не применено: {Map.WarningCount}";
            SummaryLabel.Text = baseText;
            ClearWarningsButton.IsEnabled = Map.HasWarnings;
        }

        // ---------- Применение к Revit ----------

        private void OnApplyModeChanged(object sender, RoutedEventArgs e)
        {
            if (ApplyModeButton.IsChecked == true)
            {
                if (_chosen == null)
                {
                    SnackBar.Show("Сначала выберите прибор на плане.", SnackBarKind.Error);
                    ApplyModeButton.IsChecked = false;
                    return;
                }
                EnableApplyMode();
            }
            else
            {
                DisableApplyMode();
            }
        }

        private void EnableApplyMode()
        {
            if (_watcher != null) return;
            ApplyModeButton.IsEnabled = false;
            try
            {
                var uiApp = RevitContext.UiApp;
                if (uiApp == null)
                {
                    SnackBar.Show("Приложение Revit недоступно.", SnackBarKind.Error);
                    return;
                }
                _applyMode = true;
                _watcher = new SelectionWatcher(uiApp);
                _skipNextWatcher = true; // игнорируем немедленный вызов при подписке
                _watcher.Subscribe(OnRevitSelection);
            }
            finally
            {
                ApplyModeButton.IsEnabled = true;
            }
            ModeHint.Text = "Применение к Revit: кликните элемент механического оборудования на виде Revit.";
        }

        private void DisableApplyMode()
        {
            _applyMode = false;
            try { _watcher?.Unsubscribe(OnRevitSelection); } catch { }
            try { _watcher?.Dispose(); } catch { }
            _watcher = null;
            ModeHint.Text = "Выберите прибор на плане — режим применения включится сам. Затем кликните элемент в Revit.";
        }

        private void OnRevitSelection(bool ok, Autodesk.Revit.DB.ElementId id)
        {
            // Вызов приходит из события выбора Revit: сам apply (ExternalEvent.Raise)
            // откладываем в очередь диспетчера WPF — чтобы не вызывать Raise
            // внутри колбэка выбора Revit.
            try
            {
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(new Action(() => OnApplyFromRevitSelection(ok, id)));
            }
            catch { }
        }

        private void OnApplyFromRevitSelection(bool ok, Autodesk.Revit.DB.ElementId id)
        {
            if (_skipNextWatcher)
            {
                _skipNextWatcher = false;
                return;
            }
            if (!ok || id == null || id == Autodesk.Revit.DB.ElementId.InvalidElementId) return;
            if (!_applyMode) return;
            // Синхронизация выбора с планом меняет выделение в Revit сама — применяем только
            // клики пользователя, пока активный запрос синхронизации не завершился.
            if (_syncGuard > 0) return;

            if (_chosen == null)
            {
                SnackBar.Show("Сначала выберите прибор на плане.", SnackBarKind.Error);
                return;
            }

            // guard от повторного срабатывания при быстрых кликах
            if (_lastApplyTick > 0 && Environment.TickCount - _lastApplyTick < 400) return;

            var handler = RevitContext.ApplyHandler;
            var ev = RevitContext.ApplyEvent;
            if (handler == null || ev == null)
            {
                SnackBar.Show("Обработчик применения недоступен.", SnackBarKind.Error);
                return;
            }

            GrdLog.Log("DxfMapWindow.OnRevitSelection: применяю " + _chosen.Mark + " -> " + id);
            handler.Queue(new[] { id }, _chosen, RevitContext.Settings);
            ev.Raise();
            _lastApplyTick = Environment.TickCount;
        }

        private void OnApplyToSelected(object sender, RoutedEventArgs e)
        {
            if (_chosen == null)
            {
                SnackBar.Show("Сначала выберите прибор на плане.", SnackBarKind.Error);
                return;
            }

            Autodesk.Revit.DB.ElementId[] ids = null;
            try
            {
                var sel = RevitContext.UiApp?.ActiveUIDocument?.Selection?.GetElementIds();
                if (sel != null && sel.Count > 0)
                    ids = sel.Cast<Autodesk.Revit.DB.ElementId>().ToArray();
            }
            catch { }

            if (ids == null || ids.Length == 0)
            {
                SnackBar.Show("Выберите один или несколько приборов на виде.", SnackBarKind.Error);
                return;
            }

            var handler = RevitContext.ApplyHandler;
            var ev = RevitContext.ApplyEvent;
            if (handler == null || ev == null)
            {
                SnackBar.Show("Обработчик применения недоступен.", SnackBarKind.Error);
                return;
            }

            // Отмеченные в списке несоответствий строки заменяются «каждому элементу — своя марка»
            // (у разных несоответствий могут быть разные типы DXF). Иначе — текущая марка всем.
            var pairs = BuildApplyPairs(ids);
            if (pairs != null && pairs.Count > 0)
            {
                GrdLog.Log("DxfMapWindow.OnApplyToSelected: замена несоответствий парами (" + pairs.Count + ")");
                handler.Queue(pairs.ToArray(), RevitContext.Settings, () =>
                {
                    if (Dispatcher.HasShutdownStarted) return;
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (Map.Selected.Count > 0) SyncSelectionToRevit(force: true);
                    }));
                });
            }
            else
            {
                handler.Queue(ids, _chosen, RevitContext.Settings, () =>
                {
                    if (Dispatcher.HasShutdownStarted) return;
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (Map.Selected.Count > 0) SyncSelectionToRevit(force: true);
                    }));
                });
            }
            ev.Raise();
        }

        /// <summary>Пары «элемент → его прибор» для отмеченных строк несоответствий, входящих в текущее выделение Revit.</summary>
        private List<KeyValuePair<Autodesk.Revit.DB.ElementId, GrdDevice>> BuildApplyPairs(Autodesk.Revit.DB.ElementId[] ids)
        {
            if (ids == null || ids.Length == 0) return null;
            var idSet = new HashSet<Autodesk.Revit.DB.ElementId>(ids);

            var rows = _mismatchRows
                .Where(r => !r.IsHeader && r.IsSelected && r.Heater?.Device != null &&
                            r.ElementId != null && idSet.Contains(r.ElementId))
                .ToList();
            if (rows.Count == 0) return null;

            return rows
                .Select(r => new KeyValuePair<Autodesk.Revit.DB.ElementId, GrdDevice>(r.ElementId, r.Heater.Device))
                .ToList();
        }

        // ---------- Применение выбранных рамкой приборов к текущему виду ----------

        private void OnApplyToView(object sender, RoutedEventArgs e)
        {
            var heaters = Map.Selected;
            if (heaters == null || heaters.Count == 0)
            {
                SnackBar.Show("Сначала выберите приборы на плане рамкой или кликом.", SnackBarKind.Error);
                return;
            }

            var handler = RevitContext.ApplyToViewHandler;
            var ev = RevitContext.ApplyToViewEvent;
            if (handler == null || ev == null)
            {
                SnackBar.Show("Обработчик применения к виду недоступен.", SnackBarKind.Error);
                return;
            }

            ApplyToViewButton.IsEnabled = false;
            ModeHint.Text = "Применяю приборы к текущему виду…";

            handler.Queue(
                heaters.ToList(),
                _plan != null ? _plan.Heaters : new System.Collections.Generic.List<DxfCalloutItem>(),
                RevitContext.Settings,
                OnApplyToViewCompleted);
            ev.Raise();
        }

        private void OnApplyToViewCompleted(Revit.ApplyToViewResult result)
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke((Action)(() => HandleApplyToViewResult(result)));
        }

        private void HandleApplyToViewResult(Revit.ApplyToViewResult result)
        {
            ApplyToViewButton.IsEnabled = Map.Selected.Count > 0;

            if (result == null)
            {
                ModeHint.Text = "Выберите прибор на плане — режим применения включится сам. Затем кликните элемент в Revit.";
                return;
            }

            Map.SetWarnings(result.Failures);
            UpdateSummary();

            ModeHint.Text = result.Aligned
                ? "Готово. Красное кольцо — не применённые приборы; снять — «Очистить предупреждения»."
                : "Выберите прибор на плане — режим применения включится сам. Затем кликните элемент в Revit.";

            if (!result.Aligned)
                SnackBar.Show(result.Summary, SnackBarKind.Error);
            else if (result.Failures.Count > 0)
                SnackBar.Show(result.Summary, SnackBarKind.Info);
            else
                SnackBar.Show(result.Summary, SnackBarKind.Success);
        }

        private void OnClearWarnings(object sender, RoutedEventArgs e)
        {
            Map.ClearWarnings();
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _watcher?.Unsubscribe(OnRevitSelection); } catch { }
            try { _watcher?.Dispose(); } catch { }
            _watcher = null;
            base.OnClosed(e);
        }
    }
}