using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace GrdRevit.Ui
{
    /// <summary>Ввод угла «Супер Поворота». Значение запоминается в настройках плагина.</summary>
    public partial class SuperRotateAngleWindow : Window
    {
        /// <summary>Введённый угол в градусах (после Ok).</summary>
        public double AngleDeg { get; private set; }

        public SuperRotateAngleWindow(double initialDeg)
        {
            InitializeComponent();
            AngleBox.Text = initialDeg.ToString(CultureInfo.InvariantCulture);

            Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                AngleBox.Focus();
                AngleBox.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void OnAnglePreviewInput(object sender, TextCompositionEventArgs e)
        {
            var text = (AngleBox.Text + e.Text).Replace('.', ',');
            e.Handled = text.Length > 0 && !double.TryParse(text, NumberStyles.Float,
                CultureInfo.CurrentCulture, out _);
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            var text = (AngleBox.Text ?? string.Empty).Trim();
            double val;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out val))
            {
                AngleDeg = val;
                DialogResult = true;
            }
            else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
            {
                AngleDeg = val;
                DialogResult = true;
            }
            else
            {
                SnackBar.Show("Введите число — угол в градусах.", SnackBarKind.Error);
            }
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}