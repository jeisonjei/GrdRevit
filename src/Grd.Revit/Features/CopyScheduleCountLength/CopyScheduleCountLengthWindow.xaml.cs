using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using GrdRevit.Core;
using GrdRevit.Revit;

namespace GrdRevit.Ui
{
    public partial class CopyScheduleCountLengthWindow : Window
    {
        private readonly CopyScheduleCountLengthHandler _handler;
        private readonly System.Windows.Threading.DispatcherTimer _debounce;

        public CopyScheduleCountLengthWindow(CopyScheduleCountLengthHandler handler)
        {
            InitializeComponent();
            _handler = handler;
            SourceCombo.ItemsSource = new List<CopyScheduleCountLengthParam>();
            TargetCombo.ItemsSource = new List<CopyScheduleCountLengthParam>();
            StatusText.Text = "Загрузка данных спецификации...";
            LoadOptions();
            _debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _debounce.Tick += (s, e) => { _debounce.Stop(); Execute(); };
        }

        private void LoadOptions()
        {
            try
            {
                GrdLog.Log("CountLengthWindow.LoadOptions: запрос данных; event=" +
                           (RevitContext.CopyScheduleCountLengthEvent == null ? "<null>" : "ok"));
                _handler.QueueCollectParams(opt =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SourceCombo.ItemsSource = opt.SourceColumns;
                        TargetCombo.ItemsSource = opt.TargetParams;
                        if (opt.SourceColumns.Count > 0) SourceCombo.SelectedIndex = 0;
                        if (opt.TargetParams.Count > 0) TargetCombo.SelectedIndex = 0;
                        StatusText.Text = (opt.SourceColumns.Count > 0 && opt.TargetParams.Count > 0)
                            ? "Выберите колонку-источник и параметр-приёмник."
                            : "В спецификации не найдено колонок или доступных текстовых параметров.";
                        GrdLog.Log("CountLengthWindow.LoadOptions: columns=" + opt.SourceColumns.Count +
                                   " params=" + opt.TargetParams.Count);
                    }));
                });
                RevitContext.CopyScheduleCountLengthEvent?.Raise();
            }
            catch (Exception ex)
            {
                GrdLog.Log("CountLengthWindow.LoadOptions EXCEPTION: " + ex);
                StatusText.Text = "Ошибка: " + ex.Message;
            }
        }

        private void OnExecute(object sender, RoutedEventArgs e)
        {
            _debounce.Stop();
            _debounce.Start();
        }

        private void OnClear(object sender, RoutedEventArgs e)
        {
            if (!(TargetCombo.SelectedItem is CopyScheduleCountLengthParam dst)) { SnackBar.Show("Выберите параметр-приёмник.", SnackBarKind.Error); return; }
            _debounce.Stop();
            ExecuteBtn.IsEnabled = false;
            ClearBtn.IsEnabled = false;
            StatusText.Text = "Очистка...";
            _handler.QueueClear(dst.Id, dst.ParamName ?? dst.Name, res =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ExecuteBtn.IsEnabled = true;
                    ClearBtn.IsEnabled = true;
                    if (res.Ok)
                    {
                        StatusText.Text = res.Message;
                        SnackBar.Show("Готово. " + res.Message, SnackBarKind.Success);
                    }
                    else
                    {
                        StatusText.Text = res.Error;
                        SnackBar.Show(res.Error, SnackBarKind.Error);
                    }
                }));
            });
            RevitContext.CopyScheduleCountLengthEvent?.Raise();
        }

        private void Execute()
        {
            if (!(SourceCombo.SelectedItem is CopyScheduleCountLengthParam src)) { SnackBar.Show("Выберите колонку-источник.", SnackBarKind.Error); return; }
            if (!(TargetCombo.SelectedItem is CopyScheduleCountLengthParam dst)) { SnackBar.Show("Выберите параметр-приёмник.", SnackBarKind.Error); return; }
            ExecuteBtn.IsEnabled = false;
            StatusText.Text = "Выполняется...";
            bool skipFilled = SkipFilledCheck.IsChecked == true;
            _handler.QueueExecute(src.Id, dst.Id, dst.ParamName ?? dst.Name, skipFilled, res =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ExecuteBtn.IsEnabled = true;
                    if (res.Ok)
                    {
                        StatusText.Text = res.Message;
                        SnackBar.Show("Готово. " + res.Message, SnackBarKind.Success);
                    }
                    else
                    {
                        StatusText.Text = res.Error;
                        SnackBar.Show(res.Error, SnackBarKind.Error);
                    }
                }));
            });
            RevitContext.CopyScheduleCountLengthEvent?.Raise();
        }
    }
}
