using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GrdRevit.Ui
{
    /// <summary>Тип всплывающего уведомления.</summary>
    public enum SnackBarKind { Info, Success, Error }

    /// <summary>
    /// Лёгкое всплывающее уведомление в правом нижнем углу экрана
    /// (как SnackBar из Angular Material). Без внешних библиотек: обычное
    /// безрамочное topmost-окно с анимацией появления/исчезновения.
    /// </summary>
    public static class SnackBar
    {
        private static readonly List<ToastWindow> _live = new List<ToastWindow>();
        private static Dispatcher _dispatcher;
        private static DispatcherTimer _layoutTimer;
        private const int MaxVisible = 3;

        public static void Show(string message, SnackBarKind kind = SnackBarKind.Success)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(message)) return;

                if (_dispatcher == null)
                {
                    _dispatcher = MainWindow.Instance != null
                        ? MainWindow.Instance.Dispatcher
                        : Dispatcher.CurrentDispatcher;
                }

                if (_dispatcher != null && !_dispatcher.CheckAccess())
                {
                    _dispatcher.BeginInvoke((Action)(() => ShowCore(message, kind)));
                    return;
                }
                ShowCore(message, kind);
            }
            catch (Exception ex)
            {
                GrdLog.Log("SnackBar.Show: EXCEPTION " + ex);
            }
        }

        private static void ShowCore(string message, SnackBarKind kind)
        {
            foreach (var w in _live.ToArray())
            {
                if (w == null || !w.IsLoaded)
                    _live.Remove(w);
            }

            var win = new ToastWindow(message, kind);
            win.Closed += (s, e) =>
            {
                _live.Remove((ToastWindow)s);
                Relayout();
            };
            win.ContentRendered += (s, e) => Relayout();

            _live.Add(win);
            while (_live.Count > MaxVisible)
            {
                var oldest = _live[0];
                _live.RemoveAt(0);
                try { oldest.Close(); } catch { }
            }

            win.Show();
        }

        /// <summary>
        /// Отложенный пересчёт позиций: Window может изменить свой размер после первого
        /// показа (перетекание текста в многострочное уведомление), из-за чего нижний
        /// край уходит под рабочий стол и обрезается. Переставляем окна, как только
        /// новый размер устаканился.
        /// </summary>
        internal static void RescheduleLayout()
        {
            if (_live.Count == 0) return;
            if (_layoutTimer == null)
            {
                _layoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                _layoutTimer.Tick += (s, e) =>
                {
                    _layoutTimer.Stop();
                    Relayout();
                };
            }
            _layoutTimer.Stop();
            _layoutTimer.Start();
        }

        /// <summary>Ставит все видимые уведомления в правый нижний угол рабочего стола стопкой снизу вверх.</summary>
        private static void Relayout()
        {
            var wa = SystemParameters.WorkArea;
            // Отступ снизу увеличен, чтобы карточка не упиралась в панель задач/нижний край
            // экрана и не обрезалась.
            const double bottomMargin = 48;
            const double rightMargin = 24;
            double bottom = wa.Bottom - bottomMargin;
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var w = _live[i];
                if (!w.IsLoaded || w.ActualHeight <= 0) continue;
                bottom -= w.ActualHeight;
                w.Left = wa.Right - w.ActualWidth - rightMargin;
                w.Top = Math.Max(wa.Top + 4, bottom);
                bottom -= 8;
            }
        }
    }

    /// <summary>Само окно уведомления; рисуется целиком в коде, XAML не нужен.</summary>
    internal sealed class ToastWindow : Window
    {
        private readonly DispatcherTimer _timer;

        public ToastWindow(string message, SnackBarKind kind)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Focusable = false;
            SnapsToDevicePixels = true;
            Opacity = 0;
            // Окно может вырасти после первого рендера (многострочный текст) — тогда
            // нижний край уходит за рабочий стол. Переставляем его при любом layout-обновлении.
            LayoutUpdated += (s, e) => SnackBar.RescheduleLayout();

            Color bg, accent;
            string icon;
            switch (kind)
            {
                case SnackBarKind.Error:
                    bg = Color.FromRgb(0xB0, 0x26, 0x1E);
                    accent = Colors.White;
                    icon = "✕";
                    break;
                case SnackBarKind.Info:
                    bg = Color.FromRgb(0x32, 0x32, 0x32);
                    accent = Color.FromRgb(0x42, 0xA5, 0xF5);
                    icon = "ℹ";
                    break;
                default:
                    bg = Color.FromRgb(0x32, 0x32, 0x32);
                    accent = Color.FromRgb(0x66, 0xBB, 0x6A);
                    icon = "✔";
                    break;
            }

            var border = new Border
            {
                Background = new SolidColorBrush(bg),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 12, 16, 12),
            };

            border.MouseLeftButtonDown += (s, e) => BeginFadeOut();

            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            var iconText = new TextBlock
            {
                Text = icon,
                Foreground = new SolidColorBrush(accent),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            panel.Children.Add(iconText);

            var msg = new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                FontSize = 13,
                MaxWidth = 380,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            panel.Children.Add(msg);

            border.Child = panel;
            Content = border;
            UseLayoutRounding = true;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(kind == SnackBarKind.Error ? 6000 : 3500) };
            _timer.Tick += (s, e) =>
            {
                _timer.Stop();
                BeginFadeOut();
            };
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            BeginFadeIn();
            _timer.Start();
        }

        private void BeginFadeIn()
        {
            BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }

        private void BeginFadeOut()
        {
            if (Opacity <= 0) return;
            var anim = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200));
            anim.Completed += (s, e) =>
            {
                try { Close(); } catch { }
            };
            BeginAnimation(OpacityProperty, anim);
        }
    }
}