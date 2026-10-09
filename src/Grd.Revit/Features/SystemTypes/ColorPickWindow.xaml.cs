using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AutodeskColor = Autodesk.Revit.DB.Color;
using WpfColor = System.Windows.Media.Color;

namespace GrdRevit.Ui
{
    public partial class ColorPickWindow : Window
    {
        private byte _r;
        private byte _g;
        private byte _b;
        private bool _updating;

        public AutodeskColor ResultColor { get; private set; }

        public ColorPickWindow(AutodeskColor initial)
        {
            InitializeComponent();
            _r = initial != null && initial.IsValid ? initial.Red : (byte)0x2E;
            _g = initial != null && initial.IsValid ? initial.Green : (byte)0x6B;
            _b = initial != null && initial.IsValid ? initial.Blue : (byte)0xED;
            Loaded += (s, e) =>
            {
                _updating = true;
                RSlider.Value = _r;
                GSlider.Value = _g;
                BSlider.Value = _b;
                _updating = false;
                SyncFromChannels();
            };
        }

        private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updating) return;
            _updating = true;
            try
            {
                _r = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(RSlider.Value)));
                _g = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(GSlider.Value)));
                _b = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(BSlider.Value)));
                HexBox.Text = "#" + _r.ToString("X2") + _g.ToString("X2") + _b.ToString("X2");
                RVal.Text = _r.ToString();
                GVal.Text = _g.ToString();
                BVal.Text = _b.ToString();
                PreviewSwatch.Background = new SolidColorBrush(WpfColor.FromRgb(_r, _g, _b));
            }
            finally
            {
                _updating = false;
            }
        }

        private void OnHexChanged(object sender, TextChangedEventArgs e)
        {
            if (_updating) return;
            var hex = (HexBox.Text ?? string.Empty).Trim();
            byte r, g, b;
            if (TryParseHex(hex, out r, out g, out b))
            {
                _updating = true;
                try
                {
                    _r = r; _g = g; _b = b;
                    RSlider.Value = r;
                    GSlider.Value = g;
                    BSlider.Value = b;
                    RVal.Text = r.ToString();
                    GVal.Text = g.ToString();
                    BVal.Text = b.ToString();
                    PreviewSwatch.Background = new SolidColorBrush(WpfColor.FromRgb(r, g, b));
                }
                finally
                {
                    _updating = false;
                }
            }
        }

        private void OnCopyHex(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(HexBox.Text.Trim());
                SnackBar.Show("HEX " + HexBox.Text.Trim() + " скопирован в буфер обмена.", SnackBarKind.Success);
            }
            catch (Exception ex)
            {
                GrdLog.Log("ColorPickWindow.OnCopyHex EXCEPTION: " + ex.Message);
                SnackBar.Show("Не удалось скопировать: " + ex.Message, SnackBarKind.Error);
            }
        }

        private void SyncFromChannels()
        {
            HexBox.Text = "#" + _r.ToString("X2") + _g.ToString("X2") + _b.ToString("X2");
            RVal.Text = _r.ToString();
            GVal.Text = _g.ToString();
            BVal.Text = _b.ToString();
            PreviewSwatch.Background = new SolidColorBrush(WpfColor.FromRgb(_r, _g, _b));
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            ResultColor = new AutodeskColor(_r, _g, _b);
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private static bool TryParseHex(string hex, out byte r, out byte g, out byte b)
        {
            r = 0; g = 0; b = 0;
            if (string.IsNullOrEmpty(hex)) return false;
            if (hex.Length == 7 && hex[0] == '#')
                hex = hex.Substring(1);
            else if (hex.Length != 6)
                return false;
            if (hex.Length != 6) return false;
            int value;
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return false;
            r = (byte)((value >> 16) & 0xFF);
            g = (byte)((value >> 8) & 0xFF);
            b = (byte)(value & 0xFF);
            return true;
        }
    }
}