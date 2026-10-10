using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using GrdRevit.Core;

namespace GrdRevit.Ui
{
    public partial class SystemTypesWindow : Window
    {
        private readonly Document _doc;

        public ObservableCollection<SystemTypeRow> Rows { get; } = new ObservableCollection<SystemTypeRow>();
        public IList<int> Weights { get; } = Enumerable.Range(1, 16).ToList();
        public List<LinePatternOption> Patterns { get; }

        private bool IsPipes => KindCombo.SelectedIndex == 0;

        private int LegendWeight => LegendWeightCombo.SelectedItem is int w ? w : 4;

        private SystemTypeRow _draggedRow;
        private System.Windows.Point _dragStartPoint;

        public SystemTypesWindow(Document doc)
        {
            InitializeComponent();
            _doc = doc;
            Patterns = SystemTypeService.CollectPatterns(doc);
            KindCombo.SelectedIndex = 0;
            LegendWeightCombo.SelectedItem = 4;
        }

        private void OnKindChanged(object sender, SelectionChangedEventArgs e)
        {
            Rebuild();
        }

        private void Rebuild()
        {
            Rows.Clear();
            var list = SystemTypeService.Collect(_doc, IsPipes);
            foreach (var row in list)
                Rows.Add(row);
            CountText.Text = "Набор: " + Rows.Count + " типов";
            StatusText.Text = IsPipes
                ? "Трубопроводные системы (PipingSystemType). " +
                  "Изменения применяются по «Сохранить изменения»; для каждого типа создаётся стиль линий «Grd_Трубы_…». " +
                  "Легенду удобно создавать в виде «Легенда», чтобы разместить её затем на разных листах."
                : "Системы воздуховодов (MechanicalSystemType). " +
                  "Изменения применяются по «Сохранить изменения»; для каждого типа создаётся стиль линий «Grd_Воздуховоды_…». " +
                  "Легенду удобно создавать в виде «Легенда», чтобы разместить её затем на разных листах.";
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            try
            {
int n = SystemTypeService.Apply(_doc, IsPipes, Rows, LegendWeight);
                if (n == 0)
                    SnackBar.Show("Нет изменений для сохранения.", SnackBarKind.Info);
                else
                    SnackBar.Show("Сохранено: " + n + " типов систем.", SnackBarKind.Success);
            }
            catch (Exception ex)
            {
                GrdLog.Log("SystemTypesWindow.OnSave EXCEPTION: " + ex);
                SnackBar.Show("Не удалось сохранить: " + ex.Message, SnackBarKind.Error);
            }
        }

        private void OnDuplicateSelected(object sender, RoutedEventArgs e)
        {
            if (!(Grid.SelectedItem is SystemTypeRow row))
            {
                SnackBar.Show("Выберите строку — какой тип копировать.", SnackBarKind.Info);
                return;
            }
            try
            {
                var created = SystemTypeService.Duplicate(_doc, row, out var error);
                if (created == null)
                {
                    SnackBar.Show("Не удалось скопировать: " + error, SnackBarKind.Error);
                    return;
                }
                int idx = Rows.IndexOf(row);
                Rows.Insert(idx + 1, created);
                Grid.SelectedItem = created;
                CountText.Text = "Набор: " + Rows.Count + " типов";
                SnackBar.Show("Копия «" + created.Name + "» добавлена в таблицу.", SnackBarKind.Success);
            }
            catch (Exception ex)
            {
                GrdLog.Log("SystemTypesWindow.OnDuplicateSelected EXCEPTION: " + ex);
                SnackBar.Show("Не удалось скопировать: " + ex.Message, SnackBarKind.Error);
            }
        }

        private void OnExportExcel(object sender, RoutedEventArgs e)
        {
            if (Rows.Count == 0)
            {
                SnackBar.Show("Список типов пуст — экспортировать нечего.", SnackBarKind.Info);
                return;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Экспорт типов систем в Excel",
                Filter = "Excel (*.xlsx)|*.xlsx",
                DefaultExt = ".xlsx",
                AddExtension = true,
                FileName = (IsPipes ? "Типы систем (трубы)" : "Типы систем (воздуховоды)") + ".xlsx",
                OverwritePrompt = true
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                SystemTypeService.WriteExcel(dlg.FileName, IsPipes, Rows.ToList(), Patterns);
                GrdLog.Log("SystemTypesWindow: Excel сохранён: " + dlg.FileName);
                SnackBar.Show("Excel сохранён: " + dlg.FileName, SnackBarKind.Success);
            }
            catch (Exception ex)
            {
                GrdLog.Log("SystemTypesWindow.OnExportExcel EXCEPTION: " + ex);
                SnackBar.Show("Не удалось записать Excel: " + ex.Message, SnackBarKind.Error);
            }
        }

        private void OnPickColor(object sender, RoutedEventArgs e)
        {
            if (!((sender as Button)?.DataContext is SystemTypeRow row)) return;
            var win = new ColorPickWindow(row.ColorValue);
            try { win.Owner = this; }
            catch { }
            if (win.ShowDialog() == true && win.ResultColor != null)
                row.ColorValue = win.ResultColor;
        }

        private void OnDrawLegend(object sender, RoutedEventArgs e)
        {
            if (Rows.Count == 0)
            {
                SnackBar.Show("Список типов пуст — рисовать нечего.", SnackBarKind.Info);
                return;
            }
            var uidoc = RevitContext.UiApp?.ActiveUIDocument;
            if (uidoc == null)
            {
                SnackBar.Show("Нет активного документа Revit.", SnackBarKind.Error);
                return;
            }
            Hide();
            XYZ pick = null;
            try
            {
                pick = uidoc.Selection.PickPoint(
                    "JTOOLS: типы систем — укажите точку вставки легенды. " +
                    "Строки упорядочиваются так, как они расположены в таблице: верхняя строка — верхняя линия стека, " +
                    "остальные ниже её. Перетаскивайте строки мышкой, чтобы задать нужный порядок для легенды. " +
                    "Легенду можно создать и в виде «Легенда», чтобы потом разместить её на листах.");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                GrdLog.Log("SystemTypesWindow.OnDrawLegend PickPoint EXCEPTION: " + ex.Message);
            }
            try { Show(); }
            catch (Exception ex) { GrdLog.Log("SystemTypesWindow.OnDrawLegend Show EXCEPTION: " + ex.Message); }
            if (pick == null)
            {
                SnackBar.Show("Создание легенды отменено.", SnackBarKind.Info);
                return;
            }
            var error = SystemTypeService.DrawLegend(_doc, IsPipes, Rows.ToArray(), pick, LegendWeight);
            if (error == null)
                SnackBar.Show("Легенда создана: " + Rows.Count + " строк.", SnackBarKind.Success);
            else
            {
                GrdLog.Log("SystemTypesWindow.OnDrawLegend: " + error);
                SnackBar.Show(error, SnackBarKind.Error);
            }
        }

        private void OnGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void OnGridPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || !(sender is DataGrid)) return;
            var diff = _dragStartPoint - e.GetPosition(null);
            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            var dep = e.OriginalSource as DependencyObject;
            DataGridRow row = null;
            while (dep != null)
            {
                if (dep is DataGridRow dr) { row = dr; break; }
                dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
            }
            if (row == null || !(row.DataContext is SystemTypeRow ctx)) return;
            _draggedRow = ctx;
            DragDrop.DoDragDrop(row, ctx, DragDropEffects.Move);
            _draggedRow = null;
        }

        private void OnGridDragOver(object sender, DragEventArgs e)
        {
            if (_draggedRow == null) { e.Effects = DragDropEffects.None; return; }
            if (!e.Data.GetDataPresent(typeof(SystemTypeRow))) { e.Effects = DragDropEffects.None; return; }
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void OnGridDrop(object sender, DragEventArgs e)
        {
            if (_draggedRow == null) return;
            if (!e.Data.GetDataPresent(typeof(SystemTypeRow))) return;
            var targetDep = e.OriginalSource as DependencyObject;
            DataGridRow targetRow = null;
            while (targetDep != null)
            {
                if (targetDep is DataGridRow dr) { targetRow = dr; break; }
                targetDep = System.Windows.Media.VisualTreeHelper.GetParent(targetDep);
            }
            SystemTypeRow target = null;
            if (targetRow != null && targetRow.DataContext is SystemTypeRow t) target = t;
            if (_draggedRow == target) return;
            int oldIdx = Rows.IndexOf(_draggedRow);
            int newIdx = target != null ? Rows.IndexOf(target) : Rows.Count - 1;
            if (oldIdx < 0 || newIdx < 0) return;
            Rows.Move(oldIdx, newIdx);
            e.Handled = true;
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (!Rows.Any(r => r.IsDirtyAny)) return;
            var res = MessageBox.Show(this,
                "В таблице есть несохранённые изменения текущего набора.\n\n" +
                "Применить их к типам систем перед закрытием?",
                "JTOOLS: Типы систем", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (res == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (res == MessageBoxResult.Yes)
            {
                try
                {
                    int n = SystemTypeService.Apply(_doc, IsPipes, Rows, LegendWeight);
                    SnackBar.Show("Сохранено: " + n + " типов систем.", SnackBarKind.Success);
                }
                catch (Exception ex)
                {
                    GrdLog.Log("SystemTypesWindow.OnClosing EXCEPTION: " + ex);
                    SnackBar.Show("Не удалось сохранить: " + ex.Message, SnackBarKind.Error);
                }
            }
        }
    }
}