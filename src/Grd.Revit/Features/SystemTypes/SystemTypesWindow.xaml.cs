using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

        public SystemTypesWindow(Document doc)
        {
            InitializeComponent();
            _doc = doc;
            Patterns = SystemTypeService.CollectPatterns(doc);
            KindCombo.SelectedIndex = 0;
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
                ? "Трубопроводные системы (PipingSystemType). Перенумерованные паттерны и цвета применяются по «Сохранить изменения»."
                : "Системы воздуховодов (MechanicalSystemType). Перенумерованные паттерны и цвета применяются по «Сохранить изменения».";
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            try
            {
                int n = SystemTypeService.Apply(_doc, Rows);
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
                    "Первая строка таблицы будет верхней линией стека, остальные ниже её.");
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
            var error = SystemTypeService.DrawLegend(_doc, Rows.ToArray(), pick);
            if (error == null)
                SnackBar.Show("Легенда создана: " + Rows.Count + " строк.", SnackBarKind.Success);
            else
            {
                GrdLog.Log("SystemTypesWindow.OnDrawLegend: " + error);
                SnackBar.Show(error, SnackBarKind.Error);
            }
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
                    int n = SystemTypeService.Apply(_doc, Rows);
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