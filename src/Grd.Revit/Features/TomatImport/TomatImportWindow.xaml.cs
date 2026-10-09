using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GrdRevit.Core;

namespace GrdRevit.Ui
{
    /// <summary>Пространство ОВ для выпадающего списка сопоставления.</summary>
    public sealed class SpaceChoice
    {
        public SpaceChoice(long elementId, string number, string name)
        {
            ElementId = elementId;
            Number = number;
            Name = name;
        }

        public long ElementId { get; }

        /// <summary>Номер пространства.</summary>
        public string Number { get; }

        /// <summary>Имя пространства.</summary>
        public string Name { get; }

        /// <summary>Подпись: «№ Имя» (одно из них может отсутствовать).</summary>
        public string Label
        {
            get
            {
                var a = TomatImportWindow.NormText(Number);
                var b = TomatImportWindow.NormText(Name);
                if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return "(без номера и имени)";
                return (string.IsNullOrEmpty(a) ? "" : Number + " ") + Name;
            }
        }

        public override string ToString()
        {
            return Label;
        }
    }

    /// <summary>Строка соответствия «помещение .tomat ↔ пространство Revit».</summary>
    public sealed class TomatMatchRow : INotifyPropertyChanged
    {
        private SpaceChoice _selected;

        public TomatMatchRow(TomatRoom room, ObservableCollection<SpaceChoice> spaces)
        {
            Room = room;
            Spaces = spaces;
        }

        /// <summary>Помещение из .tomat.</summary>
        public TomatRoom Room { get; }

        /// <summary>Все пространства модели (общий список для всех строк).</summary>
        public ObservableCollection<SpaceChoice> Spaces { get; }

        /// <summary>Выбранное пространство (может быть null — строка не участвует в применении).</summary>
        public SpaceChoice SelectedSpace
        {
            get => _selected;
            set
            {
                if (ReferenceEquals(_selected, value)) return;
                _selected = value;
                OnChanged(nameof(SelectedSpace));
                OnChanged(nameof(IsMatched));
                OnChanged(nameof(StatusLabel));
                OnChanged(nameof(SelectedLabel));
            }
        }

        public string DisplayName => Room.DisplayName;

        public string InfoLabel
        {
            get
            {
                var parts = new List<string>();
                if (Room.AreaSqM.HasValue) parts.Add("S " + Fmt(Room.AreaSqM.Value) + " м²");
                if (Room.HeightM.HasValue) parts.Add("h " + Fmt(Room.HeightM.Value) + " м");
                if (Room.IndoorTemperatureC.HasValue)
                    parts.Add(Fmt(Room.IndoorTemperatureC.Value).TrimEnd('0').TrimEnd('.') + " °C");
                return string.Join(", ", parts);
            }
        }

        public string SupplyLabel
        {
            get
            {
                if (!Room.SupplyM3H.HasValue && string.IsNullOrEmpty(Room.SupplySystemId)) return "—";
                return FlowLabel(Room.SupplyM3H, Room.SupplySystemId);
            }
        }

        public string ExhaustLabel
        {
            get
            {
                if (!Room.ExhaustM3H.HasValue && string.IsNullOrEmpty(Room.ExhaustSystemId)) return "—";
                return FlowLabel(Room.ExhaustM3H, Room.ExhaustSystemId);
            }
        }

        public bool IsMatched => SelectedSpace != null;

        public string StatusLabel => SelectedSpace == null ? "не сопоставлено" : "сопоставлено";

        public string SelectedLabel => SelectedSpace?.Label;

        private static string FlowLabel(double? m3H, string systemId)
        {
            var sb = new System.Text.StringBuilder();
            if (m3H.HasValue) sb.Append(Fmt(m3H.Value)).Append(" м³/ч");
            if (!string.IsNullOrEmpty(systemId))
            {
                if (sb.Length > 0) sb.Append(" • ");
                sb.Append(systemId);
            }
            return sb.ToString();
        }

        private static string Fmt(double v)
        {
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Окно импорта .tomat: параметры пространств и соответствие помещений.</summary>
    public partial class TomatImportWindow : Window
    {
        private readonly List<SpaceChoice> _allSpaces;
        private readonly ObservableCollection<SpaceChoice> _spaces;
        private readonly ObservableCollection<TomatMatchRow> _rows = new ObservableCollection<TomatMatchRow>();
        private readonly List<string> _paramNames;
        private readonly HashSet<System.Windows.Controls.ComboBox> _autocompleteSubscribed =
            new HashSet<System.Windows.Controls.ComboBox>();
        private readonly Dictionary<System.Windows.Controls.ComboBox, ICollectionView> _paramViews =
            new Dictionary<System.Windows.Controls.ComboBox, ICollectionView>();
        private readonly Dictionary<System.Windows.Controls.ComboBox, ICollectionView> _spaceViews =
            new Dictionary<System.Windows.Controls.ComboBox, ICollectionView>();
        private TomatFile _file;

        public TomatImportWindow(ObservableCollection<SpaceChoice> spaces, List<string> paramNames)
        {
            InitializeComponent();
            _allSpaces = spaces.OrderBy(c => c.Label, StringComparer.Ordinal).ToList();
            _spaces = new ObservableCollection<SpaceChoice>(_allSpaces);
            _paramNames = paramNames;
            RowsGrid.ItemsSource = _rows;
            DataContext = this;

            SupplySystemParamBox.ItemsSource = paramNames;
            ExhaustSystemParamBox.ItemsSource = paramNames;
            SupplyAirflowParamBox.ItemsSource = paramNames;
            ExhaustAirflowParamBox.ItemsSource = paramNames;

            // Автодополнение: TextChanged у ComboBox нельзя объявить в XAML и нельзя
            // подписаться напрямую (event внутренний) — ловим всплывающее событие
            // TextBox.TextChanged через AddHandler. Фильтрация выполняется через
            // ICollectionView.Filter (ItemsSource не пересоздаётся), поэтому выбор
            // стрелками не сбрасывает фильтр и каретка ввода не теряется.
            AttachComboAutocomplete(SupplySystemParamBox, OnParamComboTextChanged);
            AttachComboAutocomplete(ExhaustSystemParamBox, OnParamComboTextChanged);
            AttachComboAutocomplete(SupplyAirflowParamBox, OnParamComboTextChanged);
            AttachComboAutocomplete(ExhaustAirflowParamBox, OnParamComboTextChanged);
            SupplySystemParamBox.DropDownClosed += (s, e) => ResetParamView(SupplySystemParamBox);
            ExhaustSystemParamBox.DropDownClosed += (s, e) => ResetParamView(ExhaustSystemParamBox);
            SupplyAirflowParamBox.DropDownClosed += (s, e) => ResetParamView(SupplyAirflowParamBox);
            ExhaustAirflowParamBox.DropDownClosed += (s, e) => ResetParamView(ExhaustAirflowParamBox);

            SupplySystemParam = RevitContext.Settings.TomatSupplySystemParam;
            ExhaustSystemParam = RevitContext.Settings.TomatExhaustSystemParam;
            SupplyAirflowParam = RevitContext.Settings.TomatSupplyAirflowParam;
            ExhaustAirflowParam = RevitContext.Settings.TomatExhaustAirflowParam;

            SummaryLabel.Text = _spaces.Count == 0
                ? "Пространств в модели нет — сопоставление невозможно"
                : "Пространств в модели: " + _spaces.Count;
        }

        /// <summary>Имя параметра приточной системы (текст).</summary>
        public string SupplySystemParam { get; set; }

        /// <summary>Имя параметра вытяжной системы (текст).</summary>
        public string ExhaustSystemParam { get; set; }

        /// <summary>Имя параметра расхода приточного воздуха (число).</summary>
        public string SupplyAirflowParam { get; set; }

        /// <summary>Имя параметра расхода вытяжного воздуха (число).</summary>
        public string ExhaustAirflowParam { get; set; }

        /// <summary>Путь к загруженному файлу .tomat (для запоминания в настройках).</summary>
        public string LastTomatPath { get; private set; } = string.Empty;

        /// <summary>Разобранный файл .tomat.</summary>
        public TomatFile ParsedFile => _file;

        /// <summary>Строки соответствия для команды.</summary>
        public IReadOnlyList<TomatMatchRow> Rows => _rows;

        /// <summary>Сопоставлено ли хоть одно помещение.</summary>
        public bool HasMatchedRows => _rows.Any(r => r.IsMatched);

        /// <summary>Загружает файл .tomat и строит строки соответствия.</summary>
        public void LoadFile(TomatFile file, string path)
        {
            _file = file;
            LastTomatPath = path;

            FileLabel.Text = path;
            ErrorList.ItemsSource = file.ParseMessages;
            ErrorList.Visibility = file.ParseMessages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            BuildRows();
            AutoMatch();
        }

        /// <summary>Кнопка «Применить»: закрыть окно с результатом true.</summary>
        private void OnApply(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        /// <summary>Кнопка «Открыть .tomat…».</summary>
        private void OnOpenFile(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Открыть файл .tomat",
                Filter = "Файлы .tomat (*.tomat)|*.tomat|Все файлы (*.*)|*.*",
                CheckFileExists = true
            };
            if (!string.IsNullOrEmpty(RevitContext.Settings.LastTomatPath))
                dlg.InitialDirectory = System.IO.Path.GetDirectoryName(RevitContext.Settings.LastTomatPath);
            if (dlg.ShowDialog(this) != true) return;

            var file = TomatParser.Parse(dlg.FileName);
            if (file.Rooms.Count == 0)
            {
                MessageBox.Show(this,
                    "В файле не найдено помещений (heatLossRoomRows).\n" +
                    string.Join("\n", file.ParseMessages),
                    "JTOOLS: Импорт .tomat", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            LoadFile(file, dlg.FileName);
        }

        /// <summary>Кнопка «Автосоответствие».</summary>
        private void OnAutoMatch(object sender, RoutedEventArgs e)
        {
            AutoMatch();
        }

        /// <summary>Подписка на всплывающее событие TextBox.TextChanged (редактируемая часть
        /// ComboBox) — то, что пользователь набирает прямо в выпадающем поле.</summary>
        private static void AttachComboAutocomplete(ComboBox combo,
            System.Windows.Controls.TextChangedEventHandler handler)
        {
            if (combo == null) return;
            combo.AddHandler(System.Windows.Controls.TextBox.TextChangedEvent, handler);
        }

        /// <summary>При входе в режим редактирования (клик/F2) выделяет весь текст в ячейке,
        /// как в «Управляющем видами»: первый введённый символ заменяет существующий текст.</summary>
        private static void AttachComboSelectAll(ComboBox combo)
        {
            if (combo == null) return;
            combo.GotKeyboardFocus += (s, e) => SelectAllEditableText(combo);
            combo.GotFocus += (s, e) => SelectAllEditableText(combo);
        }

        /// <summary>Выделяет весь текст в редактируемой части ComboBox.</summary>
        private static void SelectAllEditableText(ComboBox combo)
        {
            if (combo == null) return;
            var tpl = combo.Template;
            if (tpl == null) return;
            if (tpl.FindName("PART_EditableTextBox", combo) is System.Windows.Controls.TextBox tb)
                tb.SelectAll();
        }

        /// <summary>Возвращает ICollectionView для параметров; фильтр применяется через
        /// View.Filter, поэтому сам ItemsSource не меняется (фильтр не сбрасывается
        /// при выборе стрелками, каретка не теряется).</summary>
        private ICollectionView GetParamView(ComboBox combo)
        {
            if (!_paramViews.TryGetValue(combo, out var view))
            {
                view = new ListCollectionView(_paramNames);
                combo.ItemsSource = view;
                _paramViews[combo] = view;
            }
            return view;
        }

        /// <summary>Сброс фильтра параметров после закрытия списка — при повторном открытии
        /// показывается полный список.</summary>
        private void ResetParamView(ComboBox combo)
        {
            if (!_paramViews.TryGetValue(combo, out var view)) return;
            view.Filter = null;
            view.Refresh();
        }

        /// <summary>Автодополнение параметра: по мере ввода в поле фильтруется выпадающий
        /// список. Точное совпадение с существующим параметром не сужает список (выбор
        /// стрелками вниз текст совпадает с пунктом — список остаётся отфильтрованным,
        /// сбрасывается только при закрытии списка).</summary>
        private void OnParamComboTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var combo = sender as ComboBox;
            if (combo == null || _paramNames == null) return;

            var view = GetParamView(combo);
            var text = combo.Text ?? string.Empty;
            var tokens = SplitTokens(text);
            if (tokens.Count == 0)
            {
                view.Filter = null;
                view.Refresh();
                return;
            }
            view.Filter = item =>
            {
                if (item == null) return false;
                if (_paramNames.Any(n => string.Equals(n, item.ToString(), StringComparison.Ordinal)
                                         && string.Equals(n, text, StringComparison.Ordinal)))
                    return true;
                return tokens.All(t => NormText(item.ToString()).Contains(t));
            };
            view.Refresh();
            if (combo.Items.Count > 0 && !combo.IsDropDownOpen) combo.IsDropDownOpen = true;
        }

        /// <summary>Ячейка таблицы создаётся каждый раз заново, поэтому автодополнение
        /// подключаем по Loaded элемента. Один и тот же ItemsSource не пересоздаётся —
        /// фильтрация через ICollectionView.Filter.</summary>
        private void OnSpaceComboLoaded(object sender, RoutedEventArgs e)
        {
            if (!(sender is ComboBox combo)) return;
            if (_autocompleteSubscribed.Add(combo))
            {
                AttachComboAutocomplete(combo, OnSpaceComboTextChanged);
                AttachComboSelectAll(combo);
                combo.DropDownClosed += (s, a) => ResetSpaceView(combo);
            }
            var view = GetSpaceView(combo);
            combo.ItemsSource = view;
            SelectAllEditableText(combo);
        }

        private ICollectionView GetSpaceView(ComboBox combo)
        {
            if (_spaceViews.TryGetValue(combo, out var view)) return view;
            view = new ListCollectionView(_spaces);
            _spaceViews[combo] = view;
            return view;
        }

        private void ResetSpaceView(ComboBox combo)
        {
            if (!_spaceViews.TryGetValue(combo, out var view)) return;
            view.Filter = null;
            view.Refresh();
        }

        /// <summary>Автодополнение пространства в ячейке таблицы: по мере ввода фильтруется
        /// список. Если введено полное имя/номер выбранного пространства — список не сужается
        /// (сброс только при закрытии списка).</summary>
        private void OnSpaceComboTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var combo = sender as ComboBox;
            if (combo == null || _allSpaces == null) return;

            var view = GetSpaceView(combo);
            var text = combo.Text ?? string.Empty;
            var tokens = SplitTokens(text);
            if (tokens.Count == 0)
            {
                view.Filter = null;
                view.Refresh();
                return;
            }
            view.Filter = item =>
            {
                var s = item as SpaceChoice;
                if (s == null) return false;
                if (string.Equals(NormText(s.Label), NormText(text), StringComparison.Ordinal)) return true;
                return tokens.All(t => NormText(s.Label).Contains(t));
            };
            view.Refresh();
            if (combo.Items.Count > 0 && !combo.IsDropDownOpen) combo.IsDropDownOpen = true;
        }

        /// <summary>Разбивает запрос поиска на слова (нечёткий поиск — слова в любом порядке).</summary>
        private static List<string> SplitTokens(string text)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(text)) return tokens;
            foreach (var part in text.Split(new[] { ' ', '\t', '.', ',', ';', '-' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                var t = part.Trim().ToLowerInvariant();
                if (t.Length > 0) tokens.Add(t);
            }
            return tokens;
        }

        /// <summary>Сопоставляет помещения с пространствами: сначала по номеру, затем по имени.
        /// Каждое пространство используется не более одного раза.</summary>
        private void AutoMatch()
        {
            if (_file == null) return;
            foreach (var row in _rows) row.SelectedSpace = null;

            var used = new HashSet<long>();
            MatchBy(row => row.Room.RoomNumber, space => space.Number, used);
            MatchBy(row => row.Room.RoomName, space => space.Name, used);

            UpdateSummary();
        }

        private void MatchBy(Func<TomatMatchRow, string> rowValue, Func<SpaceChoice, string> spaceValue, HashSet<long> used)
        {
            foreach (var row in _rows)
            {
                if (row.SelectedSpace != null) continue;
                var wanted = NormText(rowValue(row));
                if (string.IsNullOrEmpty(wanted)) continue;

                foreach (var space in _allSpaces)
                {
                    if (used.Contains(space.ElementId)) continue;
                    if (string.Equals(wanted, NormText(spaceValue(space)), StringComparison.Ordinal))
                    {
                        row.SelectedSpace = space;
                        used.Add(space.ElementId);
                        break;
                    }
                }
            }
        }

        private void BuildRows()
        {
            _rows.Clear();
            foreach (var room in _file.Rooms)
            {
                _rows.Add(new TomatMatchRow(room, _spaces));
            }
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            if (_file == null) return;
            int matched = _rows.Count(r => r.IsMatched);
            int withAir = _rows.Count(r => r.Room.SupplyM3H.HasValue || r.Room.ExhaustM3H.HasValue);
            SummaryLabel.Text =
                "Сопоставлено " + matched + " из " + _rows.Count +
                "  ·  с воздухообменом: " + withAir +
                (_file.UnitsToMeters.HasValue
                    ? "  ·  масштаб плана: 1 усл. ед. = " +
                      _file.UnitsToMeters.Value.ToString("0.####") + " м"
                    : "") +
                (string.IsNullOrEmpty(_file.SchemaVersion) ? "" : "  ·  schemaVersion " + _file.SchemaVersion);
        }

        /// <summary>Нормализация для сравнения: обрезка и нижний регистр.</summary>
        public static string NormText(string s)
        {
            return s?.Trim().ToLowerInvariant() ?? string.Empty;
        }
    }
}