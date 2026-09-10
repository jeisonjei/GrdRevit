using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using GrdRevit.Core;

namespace GrdRevit.Ui
{
    /// <summary>Строка таблицы: один отопительный прибор в конкретном помещении.</summary>
    public class DeviceRow : ObservableObject
    {
        public GrdDevice Source;

        /// <summary>Вызывается при изменении данных строки (для сохранения снимка).</summary>
        public Action Changed;

        public string Room => string.IsNullOrEmpty(Source.Room) ? "(без помещения)" : Source.Room;

        public string Code
        {
            get => Source.Code;
            set
            {
                var v = value?.Trim() ?? string.Empty;
                if (Set(ref Source.Code, v))
                {
                    if (!string.IsNullOrEmpty(v) && !string.IsNullOrEmpty(Source.MissingReason))
                        Source.MissingReason = string.Empty; // причина снята — код задан вручную
                    OnPropertyChanged(nameof(Family));
                    OnPropertyChanged(nameof(Reason));
                    OnPropertyChanged(nameof(IsTypeMissing));
                    Changed?.Invoke();
                }
            }
        }

        /// <summary>Тип прибора; для строк без найденного типа — причина (отображается в колонке «Тип»).</summary>
        public string Family
        {
            get
            {
                if (!string.IsNullOrEmpty(Source.MissingReason)) return Source.MissingReason;
                if (!string.IsNullOrEmpty(Source.FamilyHint)) return Source.FamilyHint;
                return Source.Code; // резерв: сам код, если семейство не распознано
            }
        }

        /// <summary>Ряд без найденного типа отопительного прибора (подсветка предупреждением).</summary>
        public bool IsTypeMissing => !string.IsNullOrEmpty(Source.MissingReason);

        /// <summary>Фактическая тепловая мощность прибора, Вт (из нагрузки в .grd).</summary>
        public string Fhl
        {
            get
            {
                return Source.CapacityW.HasValue
                    ? Source.CapacityW.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                    : "-";
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                if (double.TryParse(value.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var w) && w > 0)
                {
                    Source.CapacityW = w;
                    OnPropertyChanged(nameof(Fhl));
                    Changed?.Invoke();
                }
            }
        }

        public string ValveSetting
        {
            get => Source.ValveSetting;
            set
            {
                if (Set(ref Source.ValveSetting, value)) Changed?.Invoke();
            }
        }

        /// <summary>Причина отсутствия кода/типа (пустая строка, если тип найден).</summary>
        public string Reason
        {
            get
            {
                if (!string.IsNullOrEmpty(Source.MissingReason)) return Source.MissingReason;
                return string.IsNullOrEmpty(Source.Code)
                    ? "тип не определён"
                    : string.Empty;
            }
        }

        private bool _isApplyEnabled;
        public bool IsApplyEnabled
        {
            get => _isApplyEnabled;
            set => Set(ref _isApplyEnabled, value);
        }
    }

    public class MainViewModel : ObservableObject
    {
        public string FileInfo { get; private set; } = "Файл не загружен";
        public List<DeviceRow> Rows { get; private set; } = new List<DeviceRow>();
        public List<GrdRoom> Rooms { get; private set; } = new List<GrdRoom>();

        private string _roomFilter = string.Empty;
        public string RoomFilter
        {
            get => _roomFilter;
            set
            {
                if (Set(ref _roomFilter, value))
                    ApplyRoomFilter();
            }
        }

        private void ApplyRoomFilter()
        {
            RefreshRows();
            OnPropertyChanged(nameof(Rows));
        }

        /// <summary>Пересчитывает видимый список по текущему фильтру помещения.</summary>
        private void RefreshRows()
        {
            var filter = (_roomFilter ?? string.Empty).Trim();
            Rows = (filter.Length == 0 ? _allRows : _allRows.Where(r =>
                r.Room.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            foreach (var r in Rows) r.IsApplyEnabled = _selectionOk;
        }

        private List<DeviceRow> _allRows = new List<DeviceRow>();

        private bool _selectionOk;
        public bool SelectionOk
        {
            get => _selectionOk;
            set
            {
                if (Set(ref _selectionOk, value))
                {
                    foreach (var r in _allRows) r.IsApplyEnabled = value;
                }
            }
        }

        public ICommand LoadCommand { get; }
        public ICommand SettingsCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand LoadRoomsRadiatorsCommand { get; }
        public ICommand LoadValveSettingsCommand { get; }
        public ICommand FillValveSettingsCommand { get; }

        public Window FileDialogOwner { get; set; }

        /// <summary>Строки настроек клапанов (вкладка «Valve Settings»).</summary>
        public List<ValveSettingRow> ValveRows { get; private set; } = new List<ValveSettingRow>();

        private List<ValveSettingRow> _valveSettings = new List<ValveSettingRow>();

        private string _roomsSourcePath = string.Empty;
        private string _valveSourcePath = string.Empty;
        private bool _valveFilled;

        public MainViewModel()
        {
            LoadCommand = new RelayCommand(_ => LoadFile());
            SettingsCommand = new RelayCommand(_ => OpenSettings());
            ApplyCommand = new RelayCommand(ApplyRow);
            ClearCommand = new RelayCommand(_ => ClearRows());
            LoadRoomsRadiatorsCommand = new RelayCommand(_ => LoadRoomsRadiators());
            LoadValveSettingsCommand = new RelayCommand(_ => LoadValveSettings());
            FillValveSettingsCommand = new RelayCommand(_ => FillValveSettings());
        }

        private bool _loading;
        public bool Loading => _loading;
        public void LoadFile()
        {
            if (_loading) return;
            _loading = true;
            try
            {
                GrdLog.Log("LoadFile: showing dialog (ownerLoaded=" +
                    (FileDialogOwner != null && FileDialogOwner.IsLoaded) + ")");

                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Расчётные файлы Audytor (*.grd;*.grr)|*.grd;*.grr|Все файлы (*.*)|*.*",
                    Title = "Выберите файл .grd (или результаты .grr)"
                };
                if (!string.IsNullOrEmpty(RevitContext.Settings.LastGrdPath))
                    dlg.InitialDirectory = System.IO.Path.GetDirectoryName(RevitContext.Settings.LastGrdPath);

                // Диалог из кнопки "Загрузить" открываем с владельцем ТОЛЬКО если
                // окно уже показано и готово. Иначе — без владельца (as-is),
                // чтобы не зависать на неготовом к модальности окне.
                bool? result = (FileDialogOwner != null && FileDialogOwner.IsLoaded)
                    ? dlg.ShowDialog(FileDialogOwner)
                    : dlg.ShowDialog();
                GrdLog.Log("LoadFile: dialog returned " + (result ?? false));
                if (result != true) return;

                LoadPath(dlg.FileName);
            }
            catch (Exception ex)
            {
                GrdLog.Log("LoadFile: EXCEPTION: " + ex);
                MessageBox.Show("Ошибка чтения файла: " + ex.Message, "Audytor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>Загружает .grd по пути без диалога (автоподбор из Execute и кнопка).</summary>
        public void LoadPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            _loading = true;
            try
            {
                var doc = GrdParser.Parse(path);
                RevitContext.Settings.LastGrdPath = path;
                RevitContext.SaveSettings();
                SetDocument(doc);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка чтения файла: " + ex.Message, "Audytor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _loading = false;
            }
        }

        public void SetDocument(GrdDocument doc)
        {
            // Одна строка = один прибор в конкретном помещении. Схема текста в .grd
            // построчная: комнаты идут блоками, приборы внутри выстроены по порядку.
            _allRows = doc.Devices
                .Where(d => d.Kind == DeviceKind.RadiatorHeating)
                .OrderBy(d => d.Room, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.Order)
                .Select(d => new DeviceRow { Source = d })
                .ToList();
            Rooms = doc.Rooms;
            FileInfo = $"{System.IO.Path.GetFileName(doc.SourcePath)} — помещений: {doc.Rooms.Count}, " +
                       $"приборов: {_allRows.Count}";

            RoomFilter = string.Empty;
            RefreshRows();
            OnPropertyChanged(nameof(Rows));
            OnPropertyChanged(nameof(Rooms));
            OnPropertyChanged(nameof(FileInfo));

            SnackBar.Show($"Загружено: {System.IO.Path.GetFileName(doc.SourcePath)} — приборов: {_allRows.Count}");
        }

        /// <summary>Заполняет таблицу приборами из файла «помещения и радиаторы» (room-radiator.txt).</summary>
        public void LoadRoomsRadiators()
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Помещения и радиаторы (*.txt)|*.txt|Все файлы (*.*)|*.*",
                    Title = "Выберите файл помещений и радиаторов"
                };
                if (!string.IsNullOrEmpty(RevitContext.Settings.LastRoomsPath))
                    dlg.InitialDirectory = System.IO.Path.GetDirectoryName(RevitContext.Settings.LastRoomsPath);

                bool? result = (FileDialogOwner != null && FileDialogOwner.IsLoaded)
                    ? dlg.ShowDialog(FileDialogOwner)
                    : dlg.ShowDialog();
                if (result != true) return;

                LoadRoomsRadiatorsPath(dlg.FileName);
            }
            catch (Exception ex)
            {
                GrdLog.Log("LoadRoomsRadiators: EXCEPTION: " + ex);
                MessageBox.Show("Ошибка чтения файла: " + ex.Message, "Audytor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Загружает room-radiator.txt по пути без диалога.</summary>
        public void LoadRoomsRadiatorsPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var devices = RoomsRadiatorsParser.Parse(path);
                RevitContext.Settings.LastRoomsPath = path;
                RevitContext.SaveSettings();

                _allRows = devices
                    .OrderBy(d => d.Room, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => d.Order)
                    .Select(d => new DeviceRow { Source = d, Changed = Persist })
                    .ToList();
                Rooms = _allRows.Select(r => r.Room).Distinct()
                    .Select((n, i) => new GrdRoom { Name = n, Order = i }).ToList();

                _roomsSourcePath = path;
                Persist();

                FileInfo = $"{System.IO.Path.GetFileName(path)} — помещений: {Rooms.Count}, приборов: {_allRows.Count}";

                RoomFilter = string.Empty;
                RefreshRows();
                OnPropertyChanged(nameof(Rows));
                OnPropertyChanged(nameof(Rooms));
                OnPropertyChanged(nameof(FileInfo));

                GrdLog.Log($"LoadRoomsRadiatorsPath: {path}, приборов {_allRows.Count}");
                SnackBar.Show($"Радиаторы: {System.IO.Path.GetFileName(path)} — приборов: {_allRows.Count}");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка чтения файла: " + ex.Message, "Audytor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Загружает файл настроек клапанов (valve-settings.txt) во вкладку «Valve Settings».</summary>
        public void LoadValveSettings()
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Настройки клапанов (*.txt)|*.txt|Все файлы (*.*)|*.*",
                    Title = "Выберите файл настроек клапанов"
                };
                if (!string.IsNullOrEmpty(RevitContext.Settings.LastValveSettingsPath))
                    dlg.InitialDirectory = System.IO.Path.GetDirectoryName(RevitContext.Settings.LastValveSettingsPath);

                bool? result = (FileDialogOwner != null && FileDialogOwner.IsLoaded)
                    ? dlg.ShowDialog(FileDialogOwner)
                    : dlg.ShowDialog();
                if (result != true) return;

                LoadValveSettingsPath(dlg.FileName);
            }
            catch (Exception ex)
            {
                GrdLog.Log("LoadValveSettings: EXCEPTION: " + ex);
                MessageBox.Show("Ошибка чтения файла: " + ex.Message, "Audytor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Загружает valve-settings.txt по пути без диалога.</summary>
        public void LoadValveSettingsPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                _valveSettings = ValveSettingsParser.Parse(path);
                RevitContext.Settings.LastValveSettingsPath = path;
                RevitContext.SaveSettings();

                _valveSourcePath = path;
                _valveFilled = false;
                ValveRows = _valveSettings;
                Persist();
                OnPropertyChanged(nameof(ValveRows));

                GrdLog.Log($"LoadValveSettingsPath: {path}, строк {_valveSettings.Count}");
                SnackBar.Show($"Настройки клапанов: {System.IO.Path.GetFileName(path)} — строк: {_valveSettings.Count}");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка чтения файла: " + ex.Message, "Audytor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Сопоставляет настройки клапанов строкам таблицы по ключу «помещение + мощность».</summary>
        public void FillValveSettings()
        {
            try
            {
                if (_valveSettings.Count == 0)
                {
                    MessageBox.Show("Сначала загрузите файл настроек клапанов (Load Valve Settings).",
                        "Audytor", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (_allRows.Count == 0)
                {
                    MessageBox.Show("Сначала загрузите помещения и радиаторы (Load Rooms and Radiators).",
                        "Audytor", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                int filled = ValveSettingsMatcher.Fill(
                    _allRows.Select(r => r.Source).ToList(), _valveSettings);

                if (filled > 0) _valveFilled = true;
                Persist();

                RefreshRows();
                OnPropertyChanged(nameof(Rows));
                GrdLog.Log($"FillValveSettings: заполнено настроек {filled} из {_allRows.Count}");
                SnackBar.Show(filled > 0
                    ? $"Настройки клапанов: заполнено {filled} из {_allRows.Count}"
                    : "Совпадений по ключу «помещение + мощность» не найдено",
                    filled > 0 ? SnackBarKind.Success : SnackBarKind.Info);
            }
            catch (Exception ex)
            {
                GrdLog.Log("FillValveSettings: EXCEPTION " + ex);
                MessageBox.Show("Ошибка: " + ex.Message, "Audytor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private SettingsWindow _settingsWindow;

        /// <summary>Очищает таблицы приборов и настроек клапанов и удаляет сохранённые данные.</summary>
        public void ClearRows()
        {
            _allRows.Clear();
            _valveSettings.Clear();
            ValveRows = _valveSettings;
            Rooms = new List<GrdRoom>();
            _roomsSourcePath = string.Empty;
            _valveSourcePath = string.Empty;
            _valveFilled = false;

            RefreshRows();
            OnPropertyChanged(nameof(Rows));
            OnPropertyChanged(nameof(Rooms));
            OnPropertyChanged(nameof(ValveRows));
            FileInfo = "Таблица очищена";
            OnPropertyChanged(nameof(FileInfo));

            SavedDataFile.Delete(RevitContext.DataPath());
            SnackBar.Show("Таблица очищена", SnackBarKind.Info);
        }

        /// <summary>
        /// Сохраняет текущее состояние (приборы, помещения, настройки клапанов, правки)
        /// в файл снимка. Данные восстанавливаются при следующем открытии окна и живут,
        /// пока пользователь их не очистит или не перезагрузит файлы.
        /// </summary>
        private void Persist()
        {
            try
            {
                var snap = new SavedSnapshot
                {
                    RoomsPath = _roomsSourcePath,
                    ValveSettingsPath = _valveSourcePath,
                    ValveFilled = _valveFilled,
                    Devices = _allRows.Select(r => new SavedDevice
                    {
                        Room = r.Source.Room,
                        Code = r.Source.Code,
                        FamilyHint = r.Source.FamilyHint,
                        Diameter = r.Source.Diameter,
                        Connection = r.Source.Connection,
                        CapacityW = r.Source.CapacityW,
                        ValveSetting = r.Source.ValveSetting,
                        MissingReason = r.Source.MissingReason,
                        Order = r.Source.Order
                    }).ToList(),
                    RoomsList = Rooms.Select(rm => new SavedRoom { Name = rm.Name, Order = rm.Order }).ToList(),
                    ValveRows = _valveSettings.Select(v => new SavedValveRow
                    {
                        Room = v.Room,
                        Diameter = v.Diameter,
                        DeviceCode = v.DeviceCode,
                        Setting = v.Setting,
                        Kv = v.Kv,
                        PowerW = v.PowerW,
                        Order = v.Order
                    }).ToList()
                };
                SavedDataFile.Save(RevitContext.DataPath(), snap);
            }
            catch (Exception ex)
            {
                GrdLog.Log("Persist: EXCEPTION " + ex);
            }
        }

        /// <summary>
        /// Восстанавливает сохранённое состояние. Возвращает true, если данные были
        /// загружены (таблицы заполнены); false — таблицы пусты.
        /// </summary>
        public bool RestoreSaved()
        {
            try
            {
                var snap = SavedDataFile.Load(RevitContext.DataPath());
                if (snap == null || snap.Devices == null || snap.Devices.Count == 0) return false;

                _roomsSourcePath = snap.RoomsPath;
                _valveSourcePath = snap.ValveSettingsPath;
                _valveFilled = snap.ValveFilled;

                _allRows = snap.Devices
                    .OrderBy(d => d.Room, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => d.Order)
                    .Select(d => new DeviceRow
                    {
                        Source = new GrdDevice
                        {
                            Kind = DeviceKind.RadiatorHeating,
                            Room = d.Room,
                            Code = d.Code,
                            FamilyHint = d.FamilyHint,
                            Diameter = d.Diameter,
                            Connection = d.Connection,
                            CapacityW = d.CapacityW,
                            ValveSetting = d.ValveSetting,
                            MissingReason = d.MissingReason,
                            Order = d.Order
                        },
                        Changed = Persist
                    })
                    .ToList();

                Rooms = snap.RoomsList != null && snap.RoomsList.Count > 0
                    ? snap.RoomsList.Select(r => new GrdRoom { Name = r.Name, Order = r.Order }).ToList()
                    : _allRows.Select(r => r.Room).Distinct()
                        .Select((n, i) => new GrdRoom { Name = n, Order = i }).ToList();

                _valveSettings = (snap.ValveRows ?? new List<SavedValveRow>())
                    .Select(v => new ValveSettingRow
                    {
                        Room = v.Room,
                        Diameter = v.Diameter,
                        DeviceCode = v.DeviceCode,
                        Setting = v.Setting,
                        Kv = v.Kv,
                        PowerW = v.PowerW,
                        Order = v.Order
                    }).ToList();
                ValveRows = _valveSettings;

                FileInfo = $"Сохранённые данные — приборов: {_allRows.Count}";

                RoomFilter = string.Empty;
                RefreshRows();
                OnPropertyChanged(nameof(Rows));
                OnPropertyChanged(nameof(Rooms));
                OnPropertyChanged(nameof(ValveRows));
                OnPropertyChanged(nameof(FileInfo));

                GrdLog.Log("RestoreSaved: приборов=" + _allRows.Count + ", клапанов=" + _valveSettings.Count +
                           ", из " + (snap.RoomsPath.Length > 0 ? snap.RoomsPath : "(менее файлов)"));
                return true;
            }
            catch (Exception ex)
            {
                GrdLog.Log("RestoreSaved: EXCEPTION " + ex);
                return false;
            }
        }

        private void OpenSettings()
        {
            // Моделесс-окно: пока открыты настройки, пользователь может выбрать элемент
            // в Revit (нужно для «Взять из выделенного»). Окно не должно быть модальным.
            try
            {
                if (_settingsWindow != null && _settingsWindow.IsVisible)
                {
                    _settingsWindow.Activate();
                    return;
                }

                _settingsWindow = new SettingsWindow();
                _settingsWindow.Owner = MainWindow.Instance;
                _settingsWindow.Closed += (s, e) => _settingsWindow = null;
                _settingsWindow.Show();
            }
            catch (Exception ex)
            {
                GrdLog.Log("OpenSettings: EXCEPTION " + ex);
                MessageBox.Show("Ошибка открытия настроек: " + ex.Message, "Audytor",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyRow(object param)
        {
            if (!(param is DeviceRow row)) return;
            if (!_selectionOk) return;

            try
            {
                var ev = RevitContext.ApplyEvent;
                if (ev == null)
                {
                    MessageBox.Show("Обработчик применения не доступен.", "Audytor", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                GrdLog.Log("ApplyRow: ставим в очередь " + row.Code);
                RevitContext.ApplyHandler.Queue(row.Source, RevitContext.Settings);
                ev.Raise();
                GrdLog.Log("ApplyRow: Raise() отправлен");
            }
            catch (Exception ex)
            {
                // Ни одно исключение не должно вылетать в диспетчер WPF:
                // необработанная ошибка на потоке Revit = "непоправимая ошибка".
                GrdLog.Log("ApplyRow: EXCEPTION " + ex);
                MessageBox.Show("Ошибка: " + ex.Message, "Audytor: применение типа",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}