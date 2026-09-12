using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GrdRevit.Revit;

namespace GrdRevit.Ui
{
    /// <summary>Диалог выбора шаблона вида (модальный поверх «Управляющего видами»).</summary>
    public partial class ViewTemplatesPickerWindow : Window
    {
        private readonly List<TemplateInfoItem> _all;

        /// <summary>Выбранный шаблон (после Ok).</summary>
        public TemplateInfoItem SelectedTemplate { get; private set; }

        public ViewTemplatesPickerWindow(List<TemplateInfoItem> templates)
        {
            _all = templates ?? new List<TemplateInfoItem>();
            InitializeComponent();
            DataContext = null;

            Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);

            ApplyFilter();
            if (TemplatesList.Items.Count > 0)
            {
                TemplatesList.SelectedIndex = 0;
                TemplatesList.ScrollIntoView(TemplatesList.Items[0]);
            }
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (SearchBox == null || TemplatesList == null) return;
            var q = (SearchBox.Text ?? string.Empty).Trim();

            var visible = q.Length == 0
                ? _all.ToList()
                : _all.Where(t =>
                      t.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                      t.KindText.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();

            TemplatesList.ItemsSource = visible;
            if (visible.Count > 0)
            {
                TemplatesList.SelectedIndex = 0;
                TemplatesList.ScrollIntoView(visible[0]);
            }
        }

        private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (TemplatesList.SelectedItem != null)
                Accept();
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            Accept();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            SelectedTemplate = null;
            DialogResult = false;
        }

        private void Accept()
        {
            if (!(TemplatesList.SelectedItem is TemplateInfoItem t))
            {
                SnackBar.Show("Выберите шаблон из списка.", SnackBarKind.Error);
                return;
            }
            SelectedTemplate = t;
            DialogResult = true;
        }
    }
}