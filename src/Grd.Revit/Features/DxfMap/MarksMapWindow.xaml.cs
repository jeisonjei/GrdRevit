using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GrdRevit.Core;
using GrdRevit.Revit;
using Microsoft.Win32;

namespace GrdRevit.Ui
{
    /// <summary>Строка таблицы «Марки DXF»: марка, количество на плане, семейство и тип Revit.</summary>
    public sealed class MarksMapRow : ObservableObject
    {
        private string _markText = string.Empty;
        private string _code = string.Empty;
        private string _capacityLabel = string.Empty;
        private int _count;
        private string _family = string.Empty;
        private string _typeName = string.Empty;
        private string _familyHint = string.Empty;
        private double? _lengthMm;
        private double? _capacityW;
        private IReadOnlyDictionary<string, IList<string>> _catalog;

        public string MarkText { get => _markText; set => Set(ref _markText, value); }
        public string Code { get => _code; set => Set(ref _code, value); }
        public string CapacityLabel { get => _capacityLabel; set => Set(ref _capacityLabel, value); }
        public int Count { get => _count; set => Set(ref _count, value); }

        /// <summary>Марка есть в текущем файле DXF. False — привязка сохранилась в карте типов, но марка в файле больше не встречается.</summary>
        public bool IsMissing { get; set; }

        /// <summary>Текст колонки «Статус».</summary>
        public string StatusLabel => IsMissing ? "нет в файле" : string.Empty;

        /// <summary>Подсказка семейства из кода прибора (для «умного» сопоставления).</summary>
        public string FamilyHint { get => _familyHint; set => Set(ref _familyHint, value); }

        /// <summary>Длина прибора, мм (для «умного» сопоставления по имени типа).</summary>
        public double? LengthMm { get => _lengthMm; set => Set(ref _lengthMm, value); }

        /// <summary>Мощность прибора, Вт (для «умного» сопоставления).</summary>
        public double? CapacityW { get => _capacityW; set => Set(ref _capacityW, value); }

        /// <summary>Список семейств механического оборудования (общий на все строки).</summary>
        public ObservableCollection<string> AvailableFamilies { get; } = new ObservableCollection<string>();

        /// <summary>Типы выбранного семейства (для выпадающего списка «Тип семейства Revit»).</summary>
        public ObservableCollection<string> FamilyTypes { get; } = new ObservableCollection<string>();

        /// <summary>Имя семейства Revit, выбранное для данной марки.</summary>
        public string Family
        {
            get => _family;
            set
            {
                if (Set(ref _family, value)) RebuildFamilyTypes();
            }
        }

        /// <summary>Имя типа семейства Revit, выбранное для данной марки.</summary>
        public string TypeName
        {
            get => _typeName;
            set => Set(ref _typeName, value);
        }

        /// <summary>Задаёт каталог семейств->типов и список доступных семейств (из документа Revit).</summary>
        public void SetCatalog(Dictionary<string, IList<string>> catalog, IList<string> familyNames)
        {
            _catalog = catalog ?? new Dictionary<string, IList<string>>();

            AvailableFamilies.Clear();
            if (familyNames != null)
            {
                foreach (var name in familyNames)
                {
                    if (string.IsNullOrEmpty(name) || AvailableFamilies.Contains(name)) continue;
                    AvailableFamilies.Add(name);
                }
            }

            RebuildFamilyTypes();
        }

        /// <summary>Наполняет список типов по выбранному семейству.</summary>
        private void RebuildFamilyTypes()
        {
            FamilyTypes.Clear();
            if (string.IsNullOrEmpty(_family) || _catalog == null) return;
            if (_catalog.TryGetValue(_family, out var types) && types != null)
            {
                foreach (var t in types)
                    if (!string.IsNullOrEmpty(t)) FamilyTypes.Add(t);
            }
        }
    }

    /// <summary>Строка вкладки «Значения прибора → параметры»: параметры, единые для всех семейств.</summary>
    public sealed class MarkValueMapRow : ObservableObject
    {
        private string _powerParam = string.Empty;
        private string _valveParam = string.Empty;
        private string _markParam = string.Empty;

        public string PowerParam { get => _powerParam; set => Set(ref _powerParam, value); }
        public string ValveParam { get => _valveParam; set => Set(ref _valveParam, value); }
        public string MarkParam { get => _markParam; set => Set(ref _markParam, value); }
    }

    /// <summary>
    /// Окно «Марки DXF» — настройки плана «DXF — план отопления с приборами»:
    /// каталог марок приборов из DXF (с количеством вхождений на плане) и сопоставление
    /// «марка -> семейство/тип Revit» через выпадающие списки механического оборудования
    /// активного документа. Сохранённая карта используется при применении марки к элементу
    /// в Revit (вращается с высшим приоритетом в FamilyTypeResolver).
    /// </summary>
    public partial class MarksMapWindow : Window
    {
        public static MarksMapWindow Instance;

        private DxfPlanResult _plan;
        private readonly ObservableCollection<MarksMapRow> _rows = new ObservableCollection<MarksMapRow>();
        private readonly ObservableCollection<MarkValueMapRow> _valueRows = new ObservableCollection<MarkValueMapRow>();
        private Dictionary<string, IList<string>> _catalog = new Dictionary<string, IList<string>>();
        private bool _edited;

        /// <summary>Имена параметров экземпляра (для выпадающих списков вкладки «Значения марок → параметры»).</summary>
        public ObservableCollection<string> InstanceParams { get; } = new ObservableCollection<string>();

        public MarksMapWindow()
        {
            InitializeComponent();
            WindowTopmost.Track(this);
            DataContext = this;
            MarksGrid.ItemsSource = _rows;
            MarkValueGrid.ItemsSource = _valueRows;

            var last = RevitContext.Settings.LastDxfPath;
            if (!string.IsNullOrEmpty(last) && File.Exists(last))
                LoadDxf(last);

            RefreshFamilies();
            Loaded += (s, e) => RequestInstanceParamNames();
        }

        /// <summary>Наполняет выпадающий список имён параметров экземпляра (вкладка «Значения марок → параметры»).</summary>
        private void RequestInstanceParamNames()
        {
            try
            {
                var handler = RevitContext.TypeParamsHandler;
                var ev = RevitContext.TypeParamsEvent;
                if (handler == null || ev == null)
                {
                    GrdLog.Log("MarksMapWindow.RequestInstanceParamNames: обработчик недоступен");
                    return;
                }
                handler.Queue(null, string.Empty, result =>
                {
                    if (result?.Error == null && result.InstanceParameterNames != null)
                    {
                        var existing = new HashSet<string>(InstanceParams, StringComparer.OrdinalIgnoreCase);
                        foreach (var n in result.InstanceParameterNames)
                            if (existing.Add(n)) InstanceParams.Add(n);
                    }
                });
                ev.Raise();
            }
            catch (Exception ex)
            {
                GrdLog.Log("MarksMapWindow.RequestInstanceParamNames: EXCEPTION " + ex);
            }
        }

        private void OnOpenDxf(object sender, RoutedEventArgs e)
        {
            var last = RevitContext.Settings.LastDxfPath;
            var dlg = new OpenFileDialog
            {
                Title = "Открыть план отопления (DXF из расчёта КСМ)",
                Filter = "DXF-файлы (*.dxf)|*.dxf|Все файлы (*.*)|*.*",
                InitialDirectory = string.IsNullOrEmpty(last) ? string.Empty : Path.GetDirectoryName(last),
                FileName = string.IsNullOrEmpty(last) ? string.Empty : Path.GetFileName(last),
                Multiselect = false
            };
            if (dlg.ShowDialog(this) != true) return;
            LoadDxf(dlg.FileName);
        }

        private void LoadDxf(string path)
        {
            try
            {
                Cursor = System.Windows.Input.Cursors.Wait;
                PersistMarkMapIfEdited();
                _plan = DxfCalloutParser.Parse(path);

                RevitContext.Settings.LastDxfPath = path;
                RevitContext.SaveSettings();

                FileLabel.Text = Path.GetFileName(path);
                BuildRows();

                UpdateSummary();
                _edited = false;

                if (_plan.ParseMessages.Count > 0)
                    SnackBar.Show("Файл прочитан с замечаниями: " + _plan.ParseMessages[0], SnackBarKind.Info);
            }
            catch (Exception ex)
            {
                GrdLog.Log("MarksMapWindow.LoadDxf: EXCEPTION: " + ex);
                SnackBar.Show("Не удалось прочитать DXF: " + ex.Message, SnackBarKind.Error);
            }
            finally
            {
                Cursor = System.Windows.Input.Cursors.Arrow;
            }
        }

        /// <summary>Собирает каталог марок с количеством вхождений на плане и значением из сохранённой карты.
        /// Сохранённые привязки марок, которых не оказалось в текущем файле, добавляются отдельно с флагом «нет в файле».</summary>
        private void BuildRows()
        {
            _rows.Clear();
            _valueRows.Clear();
            if (_plan == null || _plan.MarkDevices.Count == 0)
            {
                UpdateSummary();
                return;
            }

            var savedMap = RevitContext.Settings.MarkTypeMap ?? new Dictionary<string, MarkTypeEntry>();
            var valueMap = RevitContext.Settings.ValueParamMap ?? new Dictionary<string, string>();

            _valueRows.Add(new MarkValueMapRow
            {
                PowerParam = valueMap.TryGetValue("Фhl", out var pw) ? pw ?? string.Empty : string.Empty,
                ValveParam = valueMap.TryGetValue("Valve setting", out var vv) ? vv ?? string.Empty : string.Empty,
                MarkParam = valueMap.TryGetValue("Марка", out var mk) ? mk ?? string.Empty : string.Empty
            });

            var presentNorm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dev in _plan.MarkDevices.OrderBy(d => d.Mark, StringComparer.OrdinalIgnoreCase))
            {
                string mark = dev.Mark ?? string.Empty;
                int count = _plan.Heaters.Count(h =>
                    string.Equals(h.SuggestedMark, mark, StringComparison.OrdinalIgnoreCase) ||
                    (h.Device != null && string.Equals(h.Device.Mark, mark, StringComparison.OrdinalIgnoreCase)));

                MarkTypeEntry saved = null;
                if (!string.IsNullOrEmpty(mark))
                {
                    savedMap.TryGetValue(mark, out saved);
                    if (saved == null || saved.IsEmpty)
                    {
                        // ищем нормализованным совпадением (без учёта пробелов/регистра)
                        var normMark = Normalize(mark);
                        foreach (var kv in savedMap)
                        {
                            if (kv.Value != null && !kv.Value.IsEmpty &&
                                string.Equals(Normalize(kv.Key), normMark, StringComparison.OrdinalIgnoreCase))
                            {
                                saved = kv.Value;
                                break;
                            }
                        }
                    }
                }

                var row = new MarksMapRow
                {
                    MarkText = mark,
                    Code = dev.Code ?? string.Empty,
                    CapacityLabel = dev.CapacityW.HasValue
                        ? dev.CapacityW.Value.ToString("0", CultureInfo.InvariantCulture)
                        : string.Empty,
                    Count = count,
                    FamilyHint = dev.FamilyHint ?? string.Empty,
                    LengthMm = dev.LengthMm,
                    CapacityW = dev.CapacityW,
                    Family = saved?.Family ?? string.Empty,
                    TypeName = saved?.Type ?? string.Empty
                };
                row.SetCatalog(_catalog, _catalog.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList());
                _rows.Add(row);

                string norm = Normalize(mark);
                if (norm.Length > 0) presentNorm.Add(norm);
            }

            // привязки из сохранённой карты, которых нет в текущем файле — показываем «нет в файле» с кнопкой «Удалить»
            var catalogNames = _catalog.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var kv in savedMap)
            {
                if (kv.Value == null || kv.Value.IsEmpty) continue;
                string markKey = (kv.Key ?? string.Empty).Trim();
                string norm = Normalize(markKey);
                if (norm.Length == 0 || presentNorm.Contains(norm)) continue;
                presentNorm.Add(norm);

                var row = new MarksMapRow
                {
                    MarkText = markKey,
                    IsMissing = true,
                    Family = kv.Value.Family ?? string.Empty,
                    TypeName = kv.Value.Type ?? string.Empty
                };
                row.SetCatalog(_catalog, catalogNames);
                _rows.Add(row);
            }
        }

        private void UpdateSummary()
        {
            string familiesNote = _catalog.Count > 0
                ? "  |  Семейств мех. оборудования: " + _catalog.Count
                : "  |  Семейства загружаются из Revit…";
            int missing = _rows.Count(r => r.IsMissing);
            SummaryLabel.Text = _plan == null || _plan.MarkDevices.Count == 0
                ? "В файле не найдено марок приборов (нет шаблонов-выносок DCAD_LEADER)." + familiesNote
                : $"Марок: {_rows.Count}  |  Приборов на плане: {_plan.HeaterCount}" +
                  (missing > 0 ? $"  |  Не в файле: {missing}" : string.Empty) + familiesNote;
        }

        private static string Normalize(string s)
        {
            var up = (s ?? string.Empty).Trim().ToUpperInvariant();
            var sb = new System.Text.StringBuilder();
            foreach (char c in up)
                if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }

        private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            _edited = true;
        }

        /// <summary>Запрашивает (через ExternalEvent) каталог механического оборудования активного документа.</summary>
        private void RefreshFamilies()
        {
            var handler = RevitContext.MarksCatalogHandler;
            var ev = RevitContext.MarksCatalogEvent;
            if (handler == null || ev == null)
            {
                GrdLog.Log("MarksMapWindow.RefreshFamilies: обработчик недоступен");
                return;
            }

            GrdLog.Log("MarksMapWindow: запрос каталога механического оборудования");
            handler.Queue(result =>
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() => ApplyCatalog(result)));
                }
                catch (Exception ex)
                {
                    GrdLog.Log("MarksMapWindow catalog callback: EXCEPTION " + ex);
                }
            });
            ev.Raise();
        }

        private void OnRefreshFamilies(object sender, RoutedEventArgs e)
        {
            RefreshFamilies();
        }

        private void ApplyCatalog(MarksCatalogResult result)
        {
            if (result == null || !result.Ok)
            {
                GrdLog.Log("MarksMapWindow.ApplyCatalog: " + (result?.Error ?? "нет результата"));
                SnackBar.Show(result?.Error ?? "Не удалось прочитать семейства из Revit.", SnackBarKind.Error);
                return;
            }

            _catalog = result.Families.ToDictionary(f => f.Name, f => (IList<string>)f.Types, StringComparer.OrdinalIgnoreCase);
            var names = _catalog.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var row in _rows)
                row.SetCatalog(_catalog, names);

            GrdLog.Log("MarksMapWindow.ApplyCatalog: семейств=" + _catalog.Count);
            UpdateSummary();
            if (_catalog.Count == 0)
                SnackBar.Show("В активном документе нет семейств категории «Механическое оборудование».", SnackBarKind.Info);
        }

        /// <summary>Кнопка «Взять параметры из выделенного»: считывает все параметры (проектные и
        /// семейственные, экземпляр и тип) семейства выбранного в Revit элемента и наполняет
        /// выпадающие списки вкладки «Значения марок → параметры».
        /// </summary>
        private void OnPickParamsFromSelection(object sender, RoutedEventArgs e)
        {
            var ev = RevitContext.PickEvent;
            var handler = RevitContext.PickHandler;
            if (ev == null || handler == null)
            {
                SnackBar.Show("Обработчик чтения параметров не доступен.", SnackBarKind.Error);
                return;
            }

            GrdLog.Log("MarksMapWindow: запрос параметров семейства из выделенного элемента");
            handler.Queue(result =>
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!result.Ok || string.IsNullOrEmpty(result.FamilyName))
                        {
                            GrdLog.Log("MarksMapWindow: параметры не получены - " + result.Error);
                            SnackBar.Show(result.Error ?? "Выберите элемент семейства в Revit.", SnackBarKind.Error);
                            return;
                        }

                        var names = result.ParameterNames;
                        InstanceParams.Clear();
                        foreach (var n in names)
                            if (!string.IsNullOrEmpty(n)) InstanceParams.Add(n);

                        ParamsSourceLabel.Text = "Семейство «" + result.FamilyName + "»: параметров " + names.Count;
                        SnackBar.Show("Параметры семейства «" + result.FamilyName + "»: " + names.Count + " шт.",
                            SnackBarKind.Success);
                    }));
                }
                catch (Exception ex)
                {
                    GrdLog.Log("MarksMapWindow pick params callback: EXCEPTION " + ex);
                }
            });
            ev.Raise();
        }

        /// <summary>Строит карту «марка → семейство/тип» из текущих строк таблицы (только заполненные).</summary>
        private Dictionary<string, MarkTypeEntry> BuildMarkMap()
        {
            MarksGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var map = new Dictionary<string, MarkTypeEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _rows)
            {
                var mark = row.MarkText?.Trim();
                if (string.IsNullOrEmpty(mark)) continue;
                var family = row.Family?.Trim();
                var type = row.TypeName?.Trim();
                if (string.IsNullOrEmpty(family) && string.IsNullOrEmpty(type)) continue;
                map[mark] = new MarkTypeEntry { Family = family ?? string.Empty, Type = type ?? string.Empty };
            }
            return map;
        }

        /// <summary>Если в таблице есть несохранённые правки — сохраняет карту марок перед перезагрузкой DXF,
        /// чтобы заполненные привязки не терялись.</summary>
        private void PersistMarkMapIfEdited()
        {
            if (!_edited || _rows.Count == 0) return;
            RevitContext.Settings.MarkTypeMap = BuildMarkMap();
            RevitContext.SaveSettings();
            GrdLog.Log("MarksMapWindow: карта марок автосохранена перед перезагрузкой DXF");
        }

        /// <summary>Кнопка «Удалить» в строке «нет в файле»: убирает привязку марки из таблицы (после «Сохранить» — из карты типов).</summary>
        private void OnDeleteRow(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button { Tag: MarksMapRow row })) return;
            if (_rows.Remove(row))
            {
                _edited = true;
                UpdateSummary();
            }
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            MarksGrid.CommitEdit(DataGridEditingUnit.Row, true);
            MarkValueGrid.CommitEdit(DataGridEditingUnit.Row, true);

            var map = BuildMarkMap();
            int filled = map.Count;

            var valueMap = RevitContext.Settings.ValueParamMap ?? new Dictionary<string, string>();
            var valueRow = _valueRows.FirstOrDefault();
            var valueFilled = 0;
            if (valueRow != null)
            {
                var power = (valueRow.PowerParam ?? string.Empty).Trim();
                var valve = (valueRow.ValveParam ?? string.Empty).Trim();
                var markParam = (valueRow.MarkParam ?? string.Empty).Trim();

                if (!string.IsNullOrEmpty(power))
                {
                    valueMap["Фhl"] = power;
                    valueFilled++;
                }
                else
                {
                    valueMap.Remove("Фhl");
                }

                if (!string.IsNullOrEmpty(valve))
                {
                    valueMap["Valve setting"] = valve;
                    valueFilled++;
                }
                else
                {
                    valueMap.Remove("Valve setting");
                }

                if (!string.IsNullOrEmpty(markParam))
                {
                    valueMap["Марка"] = markParam;
                    valueFilled++;
                }
                else
                {
                    valueMap.Remove("Марка");
                }
            }

            RevitContext.Settings.MarkTypeMap = map;
            RevitContext.Settings.ValueParamMap = valueMap;
            RevitContext.Settings.MarkValueMap = new Dictionary<string, MarkValueParamEntry>();
            RevitContext.SaveSettings();
            _edited = false;

            GrdLog.Log("MarksMapWindow.OnSave: сохранено марок=" + filled + ", значений=" + valueFilled);
            SnackBar.Show("Карта марок сохранена: " + filled + " позиц. типа + " + valueFilled + " значений", SnackBarKind.Success);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_edited)
            {
                var confirm = MessageBox.Show(this,
                    "Есть несохранённые изменения. Сохранить карту марок?",
                    "Марки DXF", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (confirm == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                if (confirm == MessageBoxResult.Yes)
                {
                    OnSave(this, null);
                }
            }
            base.OnClosing(e);
        }

        private ContextMenu _columnsMenu;

        /// <summary>Пункты меню «Колонки ▾»: чекбоксы видимости колонок таблицы.</summary>
        private ContextMenu BuildColumnsMenu()
        {
            var menu = new ContextMenu();
            foreach (var col in MarksGrid.Columns)
            {
                if (string.IsNullOrEmpty(col.Header?.ToString())) continue;
                var item = new MenuItem
                {
                    Header = col.Header.ToString(),
                    IsCheckable = true,
                    IsChecked = col.Visibility == Visibility.Visible,
                    Tag = col
                };
                item.Click += (s, e) =>
                {
                    if (s is MenuItem mi && mi.Tag is DataGridColumn c)
                    {
                        c.Visibility = mi.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                    }
                };
                menu.Items.Add(item);
            }
            return menu;
        }

        private void OnShowColumnsMenu(object sender, RoutedEventArgs e)
        {
            if (_columnsMenu == null) _columnsMenu = BuildColumnsMenu();
            _columnsMenu.PlacementTarget = ColumnsMenuButton;
            _columnsMenu.IsOpen = true;
        }

        /// <summary>Кнопка «Подобрать автоматически»: «умный» подбор семейства и типа.</summary>
        private void OnAutoMatch(object sender, RoutedEventArgs e)
        {
            if (_rows.Count == 0)
            {
                SnackBar.Show("Сначала откройте DXF.", SnackBarKind.Info);
                return;
            }
            if (_catalog.Count == 0)
            {
                SnackBar.Show("Каталог семейств пуст. Нажмите «Обновить семейства».", SnackBarKind.Info);
                return;
            }

            int filled = 0;
            foreach (var row in _rows)
            {
                bool needsFamily = string.IsNullOrWhiteSpace(row.Family);
                bool needsType = string.IsNullOrWhiteSpace(row.TypeName);
                if (!needsFamily && !needsType) continue;

                var hit = FindConfidentMatch(row);
                if (hit == null) continue;

                if (needsFamily) row.Family = hit.Value.Family;
                if (needsType) row.TypeName = hit.Value.Type;
                filled++;
            }

            if (filled > 0) _edited = true;
            SnackBar.Show(filled > 0
                ? "Автоподбор: заполнено " + filled + " строк (только однозначные совпадения)."
                : "Однозначных совпадений не найдено. Заполнено строк: 0.", SnackBarKind.Success);
        }

        /// <summary>Ищет семейство и тип с гарантией уверенности: лучший кандидат заметно сильнее второго.</summary>
        private (string Family, string Type)? FindConfidentMatch(MarksMapRow row)
        {
            const int minScore = 140;
            const int minGap = 60;

            string bestFamily = null, bestType = null;
            int best = 0, second = 0;

            foreach (var kv in _catalog)
            {
                string family = kv.Key;
                int famScore = ScoreFamily(family, row);
                if (famScore <= 0) continue;
                if (kv.Value == null) continue;

                foreach (var type in kv.Value)
                {
                    if (string.IsNullOrWhiteSpace(type)) continue;
                    int total = famScore + ScoreType(type, row);
                    if (total > best)
                    {
                        second = best;
                        best = total;
                        bestFamily = family;
                        bestType = type;
                    }
                    else if (total > second)
                    {
                        second = total;
                    }
                }
            }

            if (best < minScore || bestFamily == null) return null;
            if (best - second < minGap) return null;
            return (bestFamily, bestType);
        }

        private static int ScoreFamily(string familyName, MarksMapRow row)
        {
            int s = 0;
            if (!string.IsNullOrWhiteSpace(row.FamilyHint))
            {
                var fTokens = Tokens(familyName);
                var hTokens = Tokens(row.FamilyHint);
                if (familyName.IndexOf(row.FamilyHint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    s += 60;
                }
                else if (hTokens.Count > 0 && ContainsTokenSequence(fTokens, hTokens))
                {
                    s += 50;
                }
                else if (hTokens.Count == 1 && fTokens.Contains(hTokens[0], StringComparer.OrdinalIgnoreCase))
                {
                    s += 40;
                }
            }

            // подсказка из самого текста выноски (семейство упомянуто в марке) — слабый, но полезный сигнал
            if (!string.IsNullOrWhiteSpace(row.MarkText) && FamilyMentionsMark(familyName, row.MarkText)) s += 10;
            return s;
        }

        private static int ScoreType(string typeName, MarksMapRow row)
        {
            int s = 0;
            string tn = Normalize(typeName);

            // Имя типа совпало с кодом — сильный сигнал.
            if (!string.IsNullOrWhiteSpace(row.Code))
            {
                string code = Normalize(row.Code);
                if (string.Equals(tn, code, StringComparison.OrdinalIgnoreCase)) s += 120;
                else if (ContainsTokenSequence(Tokens(typeName), Tokens(row.Code))) s += 70;
                else if (tn.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0) s += 60;
            }

            // Число в имени типа ≈ длине прибора из DXF.
            if (row.LengthMm.HasValue)
            {
                var num = ExtractNumberMm(typeName);
                if (num.HasValue)
                {
                    double diff = Math.Abs(num.Value - row.LengthMm.Value);
                    if (diff < 10) s += 100;
                    else if (diff < 60) s += 50;
                }
            }

            // Число в имени типа ≈ мощности прибора из DXF.
            if (row.CapacityW.HasValue && row.CapacityW.Value > 0)
            {
                var num = ExtractNumberMm(typeName);
                if (num.HasValue)
                {
                    double diff = Math.Abs(num.Value - row.CapacityW.Value);
                    if (diff < 20) s += 50;
                }
            }

            // Пересечение с текстом выноски (токены длиннее 1 символа).
            var markWords = TokenWords(row.MarkText);
            var typeWords = TokenWords(typeName);
            foreach (var w in typeWords)
                if (markWords.Contains(w, StringComparer.OrdinalIgnoreCase)) s += 2;
            return s;
        }

        /// <summary>Семейство упомянуто в тексте марки (без учёта единичных токенов).</summary>
        private static bool FamilyMentionsMark(string familyName, string markText)
        {
            var fTokens = Tokens(familyName);
            var markWords = TokenWords(markText);
            if (fTokens.Count == 0) return false;
            int hits = fTokens.Count(t => t.Length > 2 && markWords.Contains(t, StringComparer.OrdinalIgnoreCase));
            return hits >= 2 && hits >= Math.Min(2, fTokens.Count);
        }

        private static List<string> Tokens(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            var sb = new System.Text.StringBuilder();
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
            }
            if (sb.Length > 0) list.Add(sb.ToString());
            return list;
        }

        private static List<string> TokenWords(string s)
        {
            return Tokens(s).Where(t => t.Length > 1).ToList();
        }

        private static bool ContainsTokenSequence(List<string> inTokens, List<string> seq)
        {
            if (seq.Count == 0) return false;
            for (int i = 0; i <= inTokens.Count - seq.Count; i++)
            {
                bool ok = true;
                for (int j = 0; j < seq.Count; j++)
                {
                    if (!string.Equals(inTokens[i + j], seq[j], StringComparison.OrdinalIgnoreCase))
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok) return true;
            }
            return false;
        }

        /// <summary>Ключевое (наибольшее) число из имени типа в миллиметрах. Метры (значение &lt; 40 без «мм») приводим к миллиметрам.</summary>
        private static double? ExtractNumberMm(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            double? best = null;
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(s,
                         @"\d[\d.,]+(?:\s*(?:кВт|kw|kW|Вт|W|м|мм))?",
                         System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            {
                string v = m.Value;
                bool hasMm = v.IndexOf("мм", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isMeters = !hasMm && (v.IndexOf("м", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 v.IndexOf("квт", StringComparison.OrdinalIgnoreCase) < 0 && v.IndexOf("кВт", StringComparison.OrdinalIgnoreCase) < 0 &&
                                 v.IndexOf("kw", StringComparison.OrdinalIgnoreCase) < 0 &&
                                 v.IndexOf("W", StringComparison.OrdinalIgnoreCase) < 0 &&
                                 v.IndexOf("Вт", StringComparison.OrdinalIgnoreCase) < 0);
                v = System.Text.RegularExpressions.Regex.Replace(v, @"\s*(?:кВт|kw|kW|Вт|W|м|мм)", "",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant).Trim();
                if (v.Length == 0) continue;

                double num;
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out num))
                {
                    if (!double.TryParse(v, NumberStyles.Float, CultureInfo.CurrentCulture, out num)) continue;
                }
                if (isMeters && num < 40) num *= 1000.0; // 1,375 → 1375 мм
                if (!best.HasValue || num > best.Value) best = num;
            }
            return best;
        }

        /// <summary>Кнопка «Создать недостающие типы»: типы с именем марки в выбранном семействе.</summary>
        private void OnCreateMissingTypes(object sender, RoutedEventArgs e)
        {
            MarksGrid.CommitEdit(DataGridEditingUnit.Row, true);

            var requests = new List<MarksCreateTypeRequest>();
            var affected = new List<MarksMapRow>();
            int skippedAlready = 0;

            foreach (var row in _rows)
            {
                string family = row.Family?.Trim();
                if (string.IsNullOrEmpty(family)) continue;
                if (!string.IsNullOrWhiteSpace(row.TypeName)) continue;

                string typeName = MarksCreateTypesHandler.AutoTypeName(row.MarkText);
                if (string.IsNullOrEmpty(typeName)) continue;

                if (_catalog.TryGetValue(family, out var types) && types?.Count > 0)
                {
                    if (types.Contains(typeName, StringComparer.OrdinalIgnoreCase))
                    {
                        row.TypeName = typeName;
                        skippedAlready++;
                        continue;
                    }
                    string raw = row.MarkText?.Trim();
                    if (!string.IsNullOrEmpty(raw) && types.Contains(raw, StringComparer.OrdinalIgnoreCase))
                    {
                        row.TypeName = raw;
                        skippedAlready++;
                        continue;
                    }
                    string clean = MarksCreateTypesHandler.SanitizeTypeName(raw);
                    if (!string.IsNullOrEmpty(clean) && types.Contains(clean, StringComparer.OrdinalIgnoreCase))
                    {
                        row.TypeName = clean;
                        skippedAlready++;
                        continue;
                    }
                }

                requests.Add(new MarksCreateTypeRequest { FamilyName = family, TypeName = typeName });
                affected.Add(row);
            }

            if (requests.Count == 0)
            {
                SnackBar.Show($"Создавать нечего: нужные типы уже есть ({skippedAlready}). " +
                              "Заполните семейство — тогда тип будет создан.", SnackBarKind.Info);
                return;
            }

            var handler = RevitContext.MarksCreateTypesHandler;
            var ev = RevitContext.MarksCreateTypesEvent;
            if (handler == null || ev == null)
            {
                SnackBar.Show("Обработчик создания типов недоступен.", SnackBarKind.Error);
                return;
            }

            handler.Queue(requests, result =>
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (result == null || !result.Ok)
                        {
                            SnackBar.Show(result?.Error ?? "Ошибка создания типов.", SnackBarKind.Error);
                            return;
                        }

                        int createdCount = 0;
                        for (int i = 0; i < result.Items.Count && i < affected.Count; i++)
                        {
                            var item = result.Items[i];
                            var row = affected[i];
                            if (item.Created || item.AlreadyExists)
                            {
                                row.TypeName = item.TypeName;
                                createdCount++;
                            }
                            else if (!string.IsNullOrEmpty(item.Error))
                            {
                                GrdLog.Log("MarksCreateTypes: «" + (item.FamilyName ?? string.Empty) + "»: " + item.Error);
                            }
                        }

                        if (createdCount > 0 || skippedAlready > 0)
                        {
                            _edited = true;
                            RefreshFamilies();
                        }

                        int errCount = result.Items.Count(i => !string.IsNullOrEmpty(i.Error));
                        string detail = string.Empty;
                        if (errCount > 0)
                        {
                            var firstErr = result.Items.FirstOrDefault(i => !string.IsNullOrEmpty(i.Error));
                            if (firstErr != null)
                            {
                                detail = " Первая ошибка: «" + (firstErr.Error ?? string.Empty) + "»";
                            }
                        }
                        SnackBar.Show(
                            "Типы: создано/найдено " + (createdCount + skippedAlready) +
                            (errCount > 0 ? ", ошибок: " + errCount + "." + detail : "."), SnackBarKind.Success);
                    }));
                }
                catch (Exception ex)
                {
                    GrdLog.Log("MarksMapWindow create-types callback: EXCEPTION " + ex);
                }
            });
            ev.Raise();
        }
    }
}
