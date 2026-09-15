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
        /// <summary>Параметр вшит в само семейство (False — только параметр проекта).</summary>
        public bool IsInFamily { get; set; } = true;
        /// <summary>У параметра стоит формула (значение определяется формулой).</summary>
        public bool HasFormula { get; set; }
        /// <summary>Доступен ли «Перенести в семейство»: только для общих параметров,
        /// которых ещё нет в семействе.</summary>
        public bool Movable => IsShared && !IsInFamily;
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
        private bool _selectedMode;
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

        /// <summary>
        /// Включение/выключение режима «Только выбранные в Revit»: список семейств
        /// перестраивается из текущего выделения (галочка снята — обычный режим,
        /// все семейства документа). Обновление может быть в разгаре, поэтому при
        /// занятом обработчике ждём его завершения и повторяем.
        /// </summary>
        private void OnSelectedModeToggled(object sender, RoutedEventArgs e)
        {
            _selectedMode = SelectedModeBox.IsChecked == true;
            GrdLog.Log("FamilyParamsWindow: режим «выбранные» = " + _selectedMode);
            RefreshInfoOrLater();
        }

        private void RefreshInfoOrLater()
        {
            if (!_busy)
            {
                RefreshInfo();
                return;
            }
            Dispatcher.BeginInvoke(new Action(RefreshInfoOrLater), DispatcherPriority.Background);
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
                        if (_selectedMode)
                        {
                            // Режим «только выбранные/спецификация»: отмечаем все найденные
                            // семейства сразу, чтобы область применения соответствовала
                            // спецификации либо выделению в Revit.
                            foreach (var f in FamilyChecks) f.IsChecked = true;
                            FamiliesCount.Text = FamilyChecks.Count > 0
                                ? (string.IsNullOrEmpty(result.Scope)
                                    ? "из выделения в Revit: "
                                    : "из спецификации «" + result.Scope + "»: ") + FamilyChecks.Count
                                : (string.IsNullOrEmpty(result.Scope)
                                    ? "не выделено ни одного семейства в активном виде"
                                    : "в спецификации «" + result.Scope + "» семейств не найдено");
                        }
                        ApplyFamFilter();

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
                }, _selectedMode);
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
                                IsInFamily = p.IsInFamily,
                                HasFormula = p.HasFormula,
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

        private void OnFamFilterChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFamFilter();
        }

        private void OnFamFilterReset(object sender, RoutedEventArgs e)
        {
            FamFilter.Text = string.Empty;
            ApplyFamFilter();
        }

        /// <summary>Применяет текстовый фильтр к списку семейств слева.
        /// Скрытые семейства сохраняют свою галку — фильтр влияет только на отображение.</summary>
        private void ApplyFamFilter()
        {
            var view = CollectionViewSource.GetDefaultView(FamilyChecks);
            if (view == null) return;
            var filter = FamFilter.Text?.Trim();
            if (string.IsNullOrEmpty(filter))
            {
                view.Filter = null;
                FamFilterCount.Text = string.Empty;
                return;
            }
            view.Filter = o => o is FamilyCheckRow r &&
                r.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
            int shown = FamilyChecks.Count(o => o.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            FamFilterCount.Text = "найдено " + shown + " из " + FamilyChecks.Count;
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

        /// <summary>
        /// Кнопка «снять формулу» в строке таблицы параметров: у заданного параметра
        /// формула убирается во всех отмеченных семействах (все типы), семейства
        /// перезагружаются в проект. Работает как в «Параметрах экземпляра», но для
        /// всех отмеченных семейств.
        /// </summary>
        private void OnRowClearFormula(object sender, RoutedEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as FamilyParamRow;
            if (row == null) return;

            var families = FamilyChecks.Where(f => f.IsChecked).Select(f => f.Name).ToList();
            if (families.Count == 0)
            {
                MessageBox.Show("Отметьте хотя бы одно семейство (галка слева от имени).",
                    "Параметры семейств", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var famWord = families.Count == 1 ? "семейства «" + row.Name + "»" : "отмеченных семейств";
            var answer = MessageBox.Show(
                "Будет отредактировано само СЕМЕЙСТВО (как через «Редактировать семейство»): " +
                "у параметра «" + row.Name + "» формула уберётся у всех типов " + famWord + ",\n" +
                "после чего " + (families.Count == 1 ? "семейство" : "они") + " перезагрузятся в проект.\n\n" +
                "Это затронет ВСЕ экземпляры " +
                (families.Count == 1 ? "этого семейства" : "этих семейств") + " в проекте. Продолжить?",
                "Снять формулу", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            try
            {
                var ev = RevitContext.FamilyParamsEvent;
                var handler = RevitContext.FamilyParamsHandler;
                if (ev == null || handler == null)
                {
                    MessageBox.Show("Обработчик изменения параметров не доступен.",
                        "Параметры семейств", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                GrdLog.Log("OnRowClearFormula: семейств=" + families.Count + ", параметр='" + row.Name + "'");
                handler.QueueClearFormula(families, row.Name, ShowResult);
                ev.Raise();
            }
            catch (Exception ex)
            {
                GrdLog.Log("OnRowClearFormula: EXCEPTION " + ex);
                MessageBox.Show("Ошибка: " + ex.Message, "Параметры семейств",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// «Проверить…»: сверяет фактическое наличие параметров (строк таблицы) в каждом
        /// отмеченном семействе — напрямую через диспетчер параметров и тип в проекте.
        /// Только чтение: ничего не меняет. Нужно, чтобы отделить «параметр не добавился»
        /// от «добавился, но не показан в списке» (при нескольких семействах список справа
        /// показывает лишь общие для всех).
        /// </summary>
        private void OnVerify(object sender, RoutedEventArgs e)
        {
            var families = FamilyChecks.Where(f => f.IsChecked).Select(f => f.Name).ToList();
            if (families.Count == 0)
            {
                MessageBox.Show("Отметьте хотя бы одно семейство (галка слева от имени).",
                    "Параметры семейств", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            OpsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var ops = Ops
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => new FamilyParamOp
                {
                    Name = r.Name.Trim(),
                    Source = r.IsShared ? ParamSourceKind.Shared : ParamSourceKind.Family,
                    Status = r.Status
                })
                .ToList();
            if (ops.Count == 0)
            {
                MessageBox.Show("В таблице «Параметры для применения» нет ни одной строки. " +
                    "Добавьте параметр кнопками «Добавить обычный параметр» / «Добавить общий параметр…», " +
                    "затем «Проверить…» покажет, есть ли уже такие параметры в отмеченных семействах.",
                    "Параметры семейств", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var ev = RevitContext.FamilyParamsEvent;
                var handler = RevitContext.FamilyParamsHandler;
                if (ev == null || handler == null)
                {
                    MessageBox.Show("Обработчик изменения параметров не доступен.",
                        "Параметры семейств", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                GrdLog.Log("OnVerify: семейств=" + families.Count + ", операций=" + ops.Count);
                handler.QueueVerify(families, ops, ShowVerifyResult);
                ev.Raise();
            }
            catch (Exception ex)
            {
                GrdLog.Log("OnVerify: EXCEPTION " + ex);
                MessageBox.Show("Ошибка: " + ex.Message, "Параметры семейств",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowVerifyResult(FamilyParamsResult result)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                if (result.Summary.Count > 0) sb.Append(string.Join(Environment.NewLine, result.Summary));
                if (result.Errors.Count > 0)
                {
                    if (sb.Length > 0) sb.AppendLine().AppendLine();
                    sb.Append("Не удалось проверить:").AppendLine();
                    sb.Append(string.Join(Environment.NewLine, result.Errors));
                }
                if (sb.Length == 0) sb.Append("Нет данных.");

                GrdLog.Log("FamilyParams.ShowVerifyResult: " + sb.ToString().Replace("\r", " ").Replace("\n", " "));

                var content = sb.ToString();
                if (content.Length > 6000) content = content.Substring(0, 6000) + "…(полный отчёт в журнале grd-revit.log)";
                var td = new TaskDialog("Проверка параметров семейств")
                {
                    MainInstruction = "Проверено семейств: " + result.AppliedFamilies +
                        (result.Errors.Count > 0 ? " из " + result.TotalFamilies : ""),
                    MainContent = content,
                    ExpandedContent = "«есть» — параметр присутствует в диспетчере параметров семейства; " +
                        "«НЕТ» — параметра нет (операция не применена или имя отличается). " +
                        "Пометка «(в проекте: …)» — расхождение между документом семейства и типом в проекте (например, семейство не перезагружено)."
                };
                td.Show();
            }
            catch (Exception ex)
            {
                GrdLog.Log("ShowVerifyResult: EXCEPTION " + ex);
            }
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

        /// <summary>
        /// «В семейство»: вшивает общий параметр (добавленный на уровне проекта) в само
        /// семейство — у всех отмеченных семейств. После этого параметр — часть RFA и не
        /// зависит от параметров проекта (работает в любом проекте, куда загрузится семейство).
        /// </summary>
        private void OnMoveToFamily(object sender, RoutedEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as FamilyParamRow;
            if (row == null || !row.Movable) return;

            var families = FamilyChecks.Where(f => f.IsChecked).Select(f => f.Name).ToList();
            if (families.Count == 0)
            {
                MessageBox.Show("Отметьте хотя бы одно семейство (галка слева от имени).",
                    "Параметры семейств", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!row.IsShared || string.IsNullOrEmpty(row.Guid))
            {
                MessageBox.Show("«Перенести в семейство» доступно только для общих (shared) параметров, " +
                    "добавленных к проекту («Управление параметрами проекта»), а не к семействам.",
                    "Параметры семейств", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var famWord = families.Count == 1 ? "семейство" : "семейства";
            var answer = MessageBox.Show(
                "Общий параметр «" + row.Name + "» будет вшит в " + families.Count + " отмеченн" +
                (families.Count == 1 ? "ое " : "ых ") + famWord +
                " — каждое семейство отредактируется и перезагрузится в проект.\n\n" +
                "После этого параметр станет частью файла семейства и будет одинаковым в любом проекте, " +
                "куда загрузится семейство. Продолжить?",
                "Перенести в семейство", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            try
            {
                var ev = RevitContext.FamilyParamsEvent;
                var handler = RevitContext.FamilyParamsHandler;
                if (ev == null || handler == null)
                {
                    MessageBox.Show("Обработчик изменения параметров не доступен.",
                        "Параметры семейств", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var op = new FamilyParamOp
                {
                    Name = row.Name,
                    Source = ParamSourceKind.Shared,
                    Status = "Добавить",
                    SharedGuid = row.Guid,
                    IsInstance = row.IsInstance,
                    StorageType = string.IsNullOrEmpty(row.StorageType) || row.StorageType == "—" ? "Текст" : row.StorageType,
                    Group = string.IsNullOrEmpty(row.Group) || row.Group == "—" ? "Данные" : row.Group
                };

                GrdLog.Log("OnMoveToFamily: «" + row.Name + "» в " + families.Count + " семейств" +
                           ", guid=" + row.Guid + ", экземпляр=" + row.IsInstance);
                handler.Queue(families, new List<FamilyParamOp> { op },
                    RevitContext.Settings.LastSharedParamsPath, ShowResult);
                ev.Raise();
            }
            catch (Exception ex)
            {
                GrdLog.Log("OnMoveToFamily: EXCEPTION " + ex);
                MessageBox.Show("Ошибка: " + ex.Message, "Параметры семейств",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
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