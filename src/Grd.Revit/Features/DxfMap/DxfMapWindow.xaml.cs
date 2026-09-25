using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
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

        public DxfMapWindow()
        {
            InitializeComponent();
            WindowTopmost.Track(this);

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
                Map.SetContent(_plan.Lines, _plan.Heaters);

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
        }

        private void OnWarningsChanged()
        {
            UpdateSummary();
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

            handler.Queue(ids, _chosen, RevitContext.Settings);
            ev.Raise();
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