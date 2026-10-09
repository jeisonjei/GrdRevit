using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Autodesk.Revit.DB;

namespace GrdRevit.Ui
{
    /// <summary>Выбор связи (ссылки АР) для копирования помещений. После Ok — SelectedLink.</summary>
    public partial class LinkPickerWindow : Window
    {
        private class LinkItem
        {
            public RevitLinkInstance Link { get; set; }
            public string DisplayText { get; set; }
        }

        /// <summary>Выбранная связь (после Ok).</summary>
        public RevitLinkInstance SelectedLink { get; private set; }

        public LinkPickerWindow(List<RevitLinkInstance> links)
        {
            InitializeComponent();

            LinksList.ItemsSource = links
                .Select(l => new LinkItem
                {
                    Link = l,
                    DisplayText = BuildText(l)
                })
                .ToList();

            Loaded += (s, e) =>
            {
                if (LinksList.Items.Count > 0) LinksList.SelectedIndex = 0;
                LinksList.Focus();
            };
        }

        private static string BuildText(RevitLinkInstance link)
        {
            var name = link.Name;
            var path = "";
            try
            {
                var doc = link.GetLinkDocument();
                if (doc != null && !string.IsNullOrEmpty(doc.PathName))
                {
                    path = doc.PathName;
                    name = System.IO.Path.GetFileName(path);
                }
            }
            catch { }
            return string.IsNullOrEmpty(path) ? ("«" + name + "»") : ("«" + name + "»\n" + path);
        }

        private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
        {
            OnOk(sender, e);
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            var item = LinksList.SelectedItem as LinkItem;
            if (item == null)
            {
                MessageBox.Show(this, "Выберите связь из списка.",
                    "Скопировать комнаты", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SelectedLink = item.Link;
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}