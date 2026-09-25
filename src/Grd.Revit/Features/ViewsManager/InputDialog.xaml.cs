using System;
using System.Windows;

namespace GrdRevit.Ui
{
    /// <summary>Простой модальный диалог ввода строки (для префиксов имён видов).</summary>
    public partial class InputDialog : Window
    {
        public string Value { get; private set; } = string.Empty;

        public InputDialog()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ValueBox.Focus();
                    ValueBox.SelectAll();
                }), System.Windows.Threading.DispatcherPriority.Input);
            };
        }

        public string Prompt
        {
            get => (string)GetValue(PromptProperty);
            set => SetValue(PromptProperty, value);
        }

        public static readonly DependencyProperty PromptProperty =
            DependencyProperty.Register(nameof(Prompt), typeof(string), typeof(InputDialog), new PropertyMetadata(string.Empty));

        private void OnValueChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            OkButton.IsEnabled = !string.IsNullOrWhiteSpace(ValueBox.Text);
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            Value = ValueBox.Text;
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}