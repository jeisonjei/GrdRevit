using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GrdRevit.Revit;

namespace GrdRevit.Ui
{
    /// <summary>Диалог создания свободной спецификации: выбор образца заголовков и данных листа.</summary>
    public partial class NewFreeScheduleWindow : Window
    {
        public SheetScheduleInfo SelectedTemplate { get; private set; }
        public string SheetNumber { get; private set; } = string.Empty;
        public string SheetName { get; private set; } = string.Empty;

        public NewFreeScheduleWindow(IEnumerable<SheetScheduleInfo> templates, string defaultSheetNumber,
                                     string defaultSheetName)
        {
            InitializeComponent();
            WindowTopmost.Track(this);
            TemplateList.ItemsSource = templates?.ToList() ?? new List<SheetScheduleInfo>();
            if (TemplateList.Items.Count > 0) TemplateList.SelectedIndex = 0;
            SheetNumberBox.Text = defaultSheetNumber ?? string.Empty;
            SheetNameBox.Text = defaultSheetName ?? string.Empty;
            UpdateOk();
        }

        private void OnTemplateChanged(object sender, SelectionChangedEventArgs e) { UpdateOk(); }

        private void OnUseAsTemplateChanged(object sender, RoutedEventArgs e) { UpdateOk(); }

        private void OnTextChanged(object sender, TextChangedEventArgs e) { UpdateOk(); }

        private void UpdateOk()
        {
            bool hasTemplate = TemplateList.SelectedItem is SheetScheduleInfo;
            bool useTemplate = UseAsTemplate.IsChecked == true;
            if (OkButton != null) OkButton.IsEnabled = hasTemplate && useTemplate;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            var sel = TemplateList.SelectedItem as SheetScheduleInfo;
            if (sel == null) return;
            SelectedTemplate = sel;
            SheetNumber = (SheetNumberBox.Text ?? string.Empty).Trim();
            SheetName = (SheetNameBox.Text ?? string.Empty).Trim();
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; }
    }
}
