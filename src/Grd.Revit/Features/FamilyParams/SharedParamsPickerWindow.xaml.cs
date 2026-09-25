using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace GrdRevit.Ui
{
    /// <summary>
    /// Модальный диалог выбора общих параметров из загруженного файла
    /// с полем поиска. Выбранные определения возвращаются через <see cref="GetSelection"/>.
    /// </summary>
    public partial class SharedParamsPickerWindow : Window
    {
        private readonly List<SharedDefRow> _all = new List<SharedDefRow>();
        private readonly ObservableCollection<SharedDefRow> _filtered = new ObservableCollection<SharedDefRow>();
        private bool _updating;

        public SharedParamsPickerWindow(IEnumerable<SharedDefRow> defs)
        {
            InitializeComponent();
            if (defs != null) _all.AddRange(defs);
            DefsList.ItemsSource = _filtered;
            if (_all.Count == 0)
            {
                CountText.Text = "Файл общих параметров не содержит определений.";
                return;
            }
            ApplyFilter();
            CountText.Text = "Всего: " + _all.Count;
            Loaded += (s, e) => SearchBox.Focus();
        }

        public List<SharedDefRow> GetSelection()
        {
            return DefsList.SelectedItems.Cast<SharedDefRow>().ToList();
        }

        private void OnSearch(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // Отложенная фильтрация: ищем по мере ввода, но без блокировки окна.
            if (_updating) return;
            _updating = true;
            Dispatcher.BeginInvoke(new Action(ApplyFilter), DispatcherPriority.Background);
        }

        private void ApplyFilter()
        {
            _updating = false;
            var q = (SearchBox?.Text ?? string.Empty).Trim();
            var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            _filtered.Clear();
            if (_all.Count == 0) return;

            if (words.Length == 0)
            {
                foreach (var d in _all) _filtered.Add(d);
            }
            else
            {
                foreach (var d in _all)
                {
                    var hay = (d.Name + " " + d.Group + " " + d.StorageType).ToLowerInvariant();
                    if (words.All(w => hay.Contains(w.ToLowerInvariant())))
                        _filtered.Add(d);
                }
            }
            CountText.Text = "Всего: " + _all.Count + "; в списке: " + _filtered.Count;
        }

        private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            OkButton.IsEnabled = DefsList.SelectedItems.Count > 0;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            if (DefsList.SelectedItems.Count > 0) DialogResult = true;
        }

        private void OnDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DefsList.SelectedItems.Count > 0) DialogResult = true;
        }
    }
}