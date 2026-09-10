using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using GrdRevit.Core;
using GrdRevit.Revit;

namespace GrdRevit.Ui
{
    /// <summary>Строка списка семейств с флажком выбора.</summary>
    public class FamilyCheckRow : ObservableObject
    {
        private bool _isChecked;
        public string Name { get; }
        public bool IsChecked
        {
            get => _isChecked;
            set => Set(ref _isChecked, value);
        }

        public FamilyCheckRow(string name) { Name = name; }
    }

    /// <summary>Определение общего параметра из файла (обёртка для привязки к полям DTO).</summary>
    public class SharedDefRow : ObservableObject
    {
        public SharedParamDef Def;
        public string Name => Def?.Name ?? string.Empty;
        public string Group => Def?.Group ?? string.Empty;
        public string StorageType => Def?.StorageType ?? string.Empty;
    }

    /// <summary>Строка операции с параметром (добавить/удалить/изменить привязку).</summary>
    public class ParamOpRow : ObservableObject
    {
        private string _name = string.Empty;
        private bool _isShared;
        private string _status = "Добавить";
        private string _storageType = "Текст";
        private string _group = "Данные";
        private bool _isInstance = true;

        public string Name
        {
            get => _name;
            set => Set(ref _name, value ?? string.Empty);
        }

        public bool IsShared
        {
            get => _isShared;
            set => Set(ref _isShared, value);
        }

        public string Status
        {
            get => _status;
            set => Set(ref _status, value ?? "Добавить");
        }

        public string StorageType
        {
            get => _storageType;
            set => Set(ref _storageType, value ?? "Текст");
        }

        public string Group
        {
            get => _group;
            set => Set(ref _group, value ?? "Данные");
        }

        public bool IsInstance
        {
            get => _isInstance;
            set => Set(ref _isInstance, value);
        }

        /// <summary>GUID общего параметра (пусто = создать новое определение).</summary>
        public string SharedGuid = string.Empty;
    }

    /// <summary>Строка параметра выбранного семейства в правой панели окна.</summary>
    public class FamilyParamRow
    {
        public string Name { get; set; } = string.Empty;
        public bool IsShared { get; set; }
        public string SourceText => IsShared ? "общий" : "семейный";
        public string StorageType { get; set; } = "—";
        public string Group { get; set; } = "—";
        public bool IsInstance { get; set; }
        public string BindingText => IsInstance ? "экземпляр" : "тип";
        public string Guid { get; set; } = string.Empty;
    }

    /// <summary>
    /// Окно «Параметры семейств»: добавление/удаление обычных и общих параметров
    /// сразу у нескольких семейств активного документа. Окно моделесс, поэтому
    /// работа с документом выполняется через ExternalEvent-обработчики.
    /// </summary>
    public partial class FamilyParamsWindow : Window
    {
        public ObservableCollection<FamilyCheckRow> FamilyChecks { get; } = new ObservableCollection<FamilyCheckRow>();
        public ObservableCollection<SharedDefRow> SharedDefs { get; } = new ObservableCollection<SharedDefRow>();
        public ObservableCollection<ParamOpRow> Ops { get; } = new ObservableCollection<ParamOpRow>();
        public ObservableCollection<FamilyParamRow> SelParams { get; } = new ObservableCollection<FamilyParamRow>();

        public string[] StatusOptions { get; } = { "Добавить", "Удалить", "Изменить привязку" };
        public string[] StorageOptions { get; } = { "Текст", "Число", "Целое", "Длина", "Площадь", "Объём" };

        private bool _busy;
        private readonly DispatcherTimer _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };

        public FamilyParamsWindow()
        {
            InitializeComponent();
            DataContext = this;
            _debounce.Tick += (s, e) =>
            {
                _debounce.Stop();
                RefreshSelection();
            };
            Loaded += (s, e) => RefreshInfo();
        }

        /// <summary>Перезапускает отложенное обновление правой панели после изменений в галках.</summary>
        private void DebounceSelection()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        private void OnFamilyCheckChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FamilyCheckRow.IsChecked)) DebounceSelection();
        }

        /// <summary>Обновляет список семейств и определения общих параметров из сохранённого файла.</summary>
        private void RefreshInfo()
        {
            if (_busy) return;
            var ev = RevitContext.FamilyInfoEvent;
            if (ev == null || RevitContext.FamilyInfoHandler == null)
            {
                FamiliesCount.Text = "Обработчик недоступен.";
                SelInfo.Text = "Обработчик недоступен.";
                return;
            }

            try
            {
                _busy = true;
                SharedFileInfo.Text = "Обновление…";
                RevitContext.FamilyInfoHandler.Queue(null, RevitContext.Settings.LastSharedParamsPath, result =>
                {
                    try
                    {
                        FamilyChecks.Clear();
                        foreach (var f in result.Families)
                        {
                            var row = new FamilyCheckRow(f.Name);
                            row.PropertyChanged += OnFamilyCheckChanged;
                            FamilyChecks.Add(row);
                        }
                        FamiliesCount.Text = string.IsNullOrEmpty(result.Error)
                            ? "семейств: " + FamilyChecks.Count
                            : result.Error;

                        SharedDefs.Clear();
                        foreach (var d in result.SharedDefs) SharedDefs.Add(new SharedDefRow { Def = d });

                        SelParams.Clear();
                        SelInfo.Text = "Отметьте одно семейство слева — покажутся все его параметры; несколько — только общие для всех.";

                        SharedFileInfo.Text = string.IsNullOrEmpty(result.SharedFilePath)
                            ? string.Empty
                            : "файл: " + System.IO.Path.GetFileName(result.SharedFilePath) +
                              (SharedDefs.Count > 0 ? " (" + SharedDefs.Count + " общих параметров)" : " (не загружен)");

                        // Сохраняем последний файл общих параметров и его определения,
                        // чтобы при следующем запуске окно восстановило их без повторного выбора.
                        var s = RevitContext.Settings;
                        s.LastSharedParamsPath = result.SharedFilePath;
                        s.SharedParamDefs = result.SharedDefs
                            .Select(d => new SharedParamDef
                            {
                                Name = d.Name, Guid = d.Guid, Group = d.Group, StorageType = d.StorageType
                            }).ToList();
                        RevitContext.SaveSettings();

                        GrdLog.Log("FamilyParamsWindow: обновлено семейств=" + FamilyChecks.Count +
                                   ", общих параметров=" + SharedDefs.Count + ", файл='" + result.SharedFilePath + "'");
                    }
                    catch (Exception ex)
                    {
                        GrdLog.Log("FamilyParamsWindow.RefreshInfo callback: EXCEPTION " + ex);
                    }
                    finally
                    {
                        _busy = false;
                    }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                _busy = false;
                GrdLog.Log("FamilyParamsWindow.RefreshInfo: EXCEPTION " + ex);
            }
        }

        /// <summary>Загружает параметры отмеченных семейств в правую панель.</summary>
        private void RefreshSelection()
        {
            var names = FamilyChecks.Where(f => f.IsChecked).Select(f => f.Name).ToList();
            if (names.Count == 0)
            {
                SelParams.Clear();
                SelInfo.Text = "Отметьте одно семейство слева — покажутся все его параметры; несколько — только общие для всех.";
                ApplySelFilter();
                return;
            }

            var ev = RevitContext.FamilyInfoEvent;
            var handler = RevitContext.FamilyInfoHandler;
            if (ev == null || handler == null)
            {
                SelInfo.Text = "Обработчик недоступен.";
                return;
            }

            if (_busy)
            {
                DebounceSelection();
                return;
            }

            _busy = true;
            SelInfo.Text = "Загрузка…";
            try
            {
                var requested = names.ToList();
                handler.Queue(requested, RevitContext.Settings.LastSharedParamsPath, result =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(result.Error))
                        {
                            SelParams.Clear();
                            SelInfo.Text = result.Error;
                            return;
                        }

                        SelParams.Clear();
                        foreach (var p in result.Params)
                        {
                            SelParams.Add(new FamilyParamRow
                            {
                                Name = p.Name,
                                IsShared = p.IsShared,
                                IsInstance = p.IsInstance,
                                StorageType = string.IsNullOrEmpty(p.StorageType) ? "—" : p.StorageType,
                                Group = string.IsNullOrEmpty(p.Group) ? "—" : p.Group,
                                Guid = p.Guid ?? string.Empty
                            });
                        }

                        var extra = string.IsNullOrEmpty(result.ParamsMessage) ? string.Empty : "  Предупреждение: " + result.ParamsMessage;
                        if (SelParams.Count == 0)
                        {
                            SelInfo.Text = (requested.Count == 1
                                    ? "У семейства «" + requested[0] + "» нет параметров."
                                    : "Общих параметров для всех выбранных семейств не найдено.")
                                + extra;
                        }
                        else
                        {
                            SelInfo.Text = (requested.Count == 1
                                    ? "Все параметры семейства «" + requested[0] + "»: " + SelParams.Count
                                    : "Только общие для всех (" + requested.Count + " семейств): " + SelParams.Count)
                                + extra;
                        }
                        GrdLog.Log("FamilyParamsWindow: параметров=" + SelParams.Count + " для '" +
                                   string.Join("; ", requested) + "'" + extra);
                        ApplySelFilter();
                    }
                    catch (Exception ex)
                    {
                        GrdLog.Log("FamilyParamsWindow.RefreshSelection callback: EXCEPTION " + ex);
                    }
                    finally
                    {
                        _busy = false;
                    }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                _busy = false;
                GrdLog.Log("FamilyParamsWindow.RefreshSelection: EXCEPTION " + ex);
            }
        }

        private void OnRefresh(object sender, RoutedEventArgs e)
        {
            RefreshInfo();
        }

        private void OnSelFilterChanged(object sender, TextChangedEventArgs e)
        {
            ApplySelFilter();
        }

        private void OnSelFilterReset(object sender, RoutedEventArgs e)
        {
            SelFilter.Text = string.Empty;
            ApplySelFilter();
        }

        /// <summary>Применяет текстовый фильтр к правой панели параметров.</summary>
        private void ApplySelFilter()
        {
            var view = CollectionViewSource.GetDefaultView(SelParams);
            if (view == null) return;
            var filter = SelFilter.Text?.Trim();
            if (string.IsNullOrEmpty(filter))
            {
                view.Filter = null;
                SelFilterCount.Text = string.Empty;
                return;
            }
            view.Filter = o => o is FamilyParamRow r &&
                r.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
            var shown = SelParams.Cast<object>().Where(o => view.Filter(o)).Count();
            SelFilterCount.Text = "показано " + shown + " из " + SelParams.Count;
        }

        private void OnSelectAll(object sender, RoutedEventArgs e)
        {
            foreach (var f in FamilyChecks) f.IsChecked = true;
        }

        private void OnSelectNone(object sender, RoutedEventArgs e)
        {
            foreach (var f in FamilyChecks) f.IsChecked = false;
        }

        /// <summary>
        /// Щелчок по строке семейства отмечает его галкой (сам чекбокс
        /// обрабатывает свой клик отдельно).
        /// </summary>
        private void OnFamiliesListMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            var source = e.OriginalSource as DependencyObject;
            if (FindVisualParent<CheckBox>(source) != null) return; // клик по самому чекбоксу
            var item = FindVisualParent<ListBoxItem>(source);
            if (item?.DataContext is FamilyCheckRow row)
                row.IsChecked = !row.IsChecked;
        }

        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T match) return match;
                child = (child is Visual or System.Windows.Media.Media3D.Visual3D)
                    ? VisualTreeHelper.GetParent(child)
                    : null;
            }
            return null;
        }

        private void OnAddFamilyParam(object sender, RoutedEventArgs e)
        {
            Ops.Add(new ParamOpRow { Name = "Новый параметр", IsShared = false });
        }

        private void OnAddSharedParam(object sender, RoutedEventArgs e)
        {
            if (SharedDefs.Count == 0)
            {
                MessageBox.Show("Файл общих параметров не загружен. Нажмите «Загрузить файл общих параметров...» внизу окна.",
                    "Параметры семейств", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var picker = new SharedParamsPickerWindow(SharedDefs.ToList()) { Owner = IsLoaded ? this : null };
            if (picker.ShowDialog() != true) return;

            foreach (var d in picker.GetSelection())
            {
                AddOp(new ParamOpRow
                {
                    Name = d.Def.Name,
                    IsShared = true,
                    StorageType = string.IsNullOrEmpty(d.Def.StorageType) ? "Текст" : d.Def.StorageType,
                    Group = string.IsNullOrEmpty(d.Def.Group) ? "Данные" : d.Def.Group,
                    SharedGuid = d.Def.Guid,
                    Status = "Добавить"
                });
            }
        }

        private void OnRemoveOps(object sender, RoutedEventArgs e)
        {
            OpsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var toRemove = OpsGrid.SelectedItems.Cast<object>().ToList();
            foreach (var item in toRemove) Ops.Remove(item as ParamOpRow);
        }

        /// <summary>Добавляет операцию в верхнюю таблицу без явных дубликатов.</summary>
        private void AddOp(ParamOpRow op)
        {
            var dupe = Ops.Any(o => string.Equals(o.Name, op.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(o.Status, op.Status, StringComparison.OrdinalIgnoreCase)
                && o.IsShared == op.IsShared);
            if (!dupe) Ops.Add(op);
        }

        private void OnRowRemove(object sender, RoutedEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as FamilyParamRow;
            if (row == null) return;
            AddOp(new ParamOpRow
            {
                Name = row.Name,
                IsShared = row.IsShared,
                StorageType = string.IsNullOrEmpty(row.StorageType) || row.StorageType == "—" ? "Текст" : row.StorageType,
                Group = string.IsNullOrEmpty(row.Group) || row.Group == "—" ? "Данные" : row.Group,
                SharedGuid = row.Guid ?? string.Empty,
                Status = "Удалить",
                IsInstance = row.IsInstance
            });
        }

        private void OnRowChangeBinding(object sender, RoutedEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as FamilyParamRow;
            if (row == null) return;
            AddOp(new ParamOpRow
            {
                Name = row.Name,
                IsShared = row.IsShared,
                SharedGuid = row.Guid ?? string.Empty,
                Status = "Изменить привязку",
                IsInstance = !row.IsInstance
            });
        }

        private void OnLoadSharedFile(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Файлы общих параметров Revit (*.txt)|*.txt|Все файлы (*.*)|*.*",
                    Title = "Выберите файл общих параметров (.txt)"
                };
                if (!string.IsNullOrEmpty(RevitContext.Settings.LastSharedParamsPath))
                    dlg.InitialDirectory = System.IO.Path.GetDirectoryName(RevitContext.Settings.LastSharedParamsPath);

                bool? result = (FileDialogOwnerOk()) ? dlg.ShowDialog(this) : dlg.ShowDialog();
                if (result != true) return;

                RevitContext.Settings.LastSharedParamsPath = dlg.FileName;
                RevitContext.SaveSettings();
                RefreshInfo();
            }
            catch (Exception ex)
            {
                GrdLog.Log("OnLoadSharedFile: EXCEPTION " + ex);
                MessageBox.Show("Ошибка: " + ex.Message, "Параметры семейств",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool FileDialogOwnerOk()
        {
            try { return IsLoaded && Visibility == Visibility.Visible; }
            catch { return false; }
        }

        private void OnApply(object sender, RoutedEventArgs e)
        {
            var families = FamilyChecks.Where(f => f.IsChecked).Select(f => f.Name).ToList();
            if (families.Count == 0)
            {
                MessageBox.Show("Отметьте хотя бы одно семейство (галка слева от имени).",
                    "Параметры семейств", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            OpsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var ops = new List<FamilyParamOp>();
            foreach (var r in Ops)
            {
                if (string.IsNullOrWhiteSpace(r.Name)) continue;
                ops.Add(new FamilyParamOp
                {
                    Name = r.Name.Trim(),
                    Source = r.IsShared ? ParamSourceKind.Shared : ParamSourceKind.Family,
                    StorageType = r.StorageType,
                    Group = r.Group,
                    IsInstance = r.IsInstance,
                    Remove = string.Equals(r.Status, "Удалить", StringComparison.OrdinalIgnoreCase),
                    Status = r.Status,
                    SharedGuid = r.SharedGuid
                });
            }
            if (ops.Count == 0)
            {
                MessageBox.Show("Задайте хотя бы одну операцию с параметрами (имя параметра не должно быть пустым).",
                    "Параметры семейств", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var ev = RevitContext.FamilyParamsEvent;
                if (ev == null || RevitContext.FamilyParamsHandler == null)
                {
                    MessageBox.Show("Обработчик изменения параметров не доступен.",
                        "Параметры семейств", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                GrdLog.Log("OnApply: семейств=" + families.Count + ", операций=" + ops.Count);
                RevitContext.FamilyParamsHandler.Queue(families, ops,
                    RevitContext.Settings.LastSharedParamsPath, ShowResult);
                ev.Raise();
            }
            catch (Exception ex)
            {
                GrdLog.Log("OnApply: EXCEPTION " + ex);
                MessageBox.Show("Ошибка: " + ex.Message, "Параметры семейств",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowResult(FamilyParamsResult result)
        {
            try
            {
                var sb = new System.Text.StringBuilder("Применено к " + result.AppliedFamilies +
                    " из " + result.TotalFamilies + " семейств.");
                if (result.Summary.Count > 0)
                {
                    sb.AppendLine();
                    sb.Append(string.Join(Environment.NewLine, result.Summary));
                }
                if (result.Errors.Count > 0)
                {
                    var td = new TaskDialog("Параметры семейств")
                    {
                        MainInstruction = "Часть семейств не обработана.",
                        MainContent = sb.ToString() + Environment.NewLine + Environment.NewLine +
                                      string.Join(Environment.NewLine, result.Errors),
                        MainIcon = TaskDialogIcon.TaskDialogIconWarning
                    };
                    td.Show();
                    GrdLog.Log("FamilyParams.ShowResult: " + sb + " | errors: " + string.Join("; ", result.Errors));
                    return;
                }

                SnackBar.Show(sb.ToString(), SnackBarKind.Success);
                GrdLog.Log("FamilyParams.ShowResult: " + sb.ToString().Replace("\r", " ").Replace("\n", " "));

                // Параметры выбранных семейств могли измениться — обновляем правую панель,
                // чтобы добавленные/удалённые параметры сразу были видны.
                try { RefreshSelection(); } catch { }
            }
            catch (Exception ex)
            {
                GrdLog.Log("ShowResult: EXCEPTION " + ex);
            }
        }
    }
}