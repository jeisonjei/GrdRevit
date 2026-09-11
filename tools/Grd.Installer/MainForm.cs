using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GrdInstaller
{
    public sealed class MainForm : Form
    {
        private readonly List<RevitInstall> _detected = new List<RevitInstall>();
        private readonly DataGridView _grid;

        public MainForm()
        {
            Text = "Установка плагина JTOOLS";
            Font = new Font("Segoe UI", 9f);
            Width = 760;
            Height = 560;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;

            var pnl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                ColumnCount = 1,
                RowCount = 4
            };
            pnl.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            pnl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pnl.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            pnl.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));

            var lbl = new Label
            {
                Text = "Установленные версии Revit (2020–2026):",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                MultiSelect = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "sel", HeaderText = "Отметить", Width = 60, ReadOnly = false, FillWeight = 10 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ver", HeaderText = "Версия", ReadOnly = true, FillWeight = 15 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "status", HeaderText = "Статус", ReadOnly = true, FillWeight = 25 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "exe", HeaderText = "Путь к Revit.exe", ReadOnly = true, FillWeight = 50 });

            var btns = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            var btnInstall = new Button { Text = "Установить выбранные", Width = 180, Height = 28 };
            var btnUninstall = new Button { Text = "Удалить выбранные", Width = 180, Height = 28 };
            var btnStart = new Button { Text = "Запустить Revit (выборка)", Width = 190, Height = 28 };
            var btnRefresh = new Button { Text = "Обновить список", Width = 140, Height = 28 };
            btns.Controls.AddRange(new Control[] { btnInstall, btnUninstall, btnStart, btnRefresh });

            var log = new ListBox { Dock = DockStyle.Fill };

            pnl.Controls.Add(lbl, 0, 0);
            pnl.Controls.Add(_grid, 0, 1);
            pnl.Controls.Add(btns, 0, 2);
            pnl.Controls.Add(log, 0, 3);
            Controls.Add(pnl);

            btnInstall.Click += (s, e) => Run(log, install: true);
            btnUninstall.Click += (s, e) => Run(log, install: false);
            btnStart.Click += (s, e) => StartRevit(log);
            btnRefresh.Click += (s, e) => RefreshList();

            RefreshList();
        }

        private void RefreshList()
        {
            _detected.Clear();
            _grid.Rows.Clear();
            foreach (var r in InstallLogic.Detect())
            {
                _detected.Add(r);
                _grid.Rows.Add(true, "Revit " + r.Year,
                    r.IsInstalled ? "Установлен" : "Не установлен",
                    r.ExePath);
            }
        }

        private void Run(ListBox log, bool install)
        {
            log.Items.Clear();
            var sel = Selected();
            if (sel.Count == 0) { log.Items.Add("Ничего не выбрано."); return; }

            foreach (var r in sel)
            {
                try
                {
                    if (install)
                    {
                        if (!r.Supported) { log.Items.Add("Revit " + r.Year + ": не поддерживается (нужна 2020–2026)."); continue; }
                        InstallLogic.Install(r);
                        log.Items.Add("Revit " + r.Year + ": установлено.");
                    }
                    else
                    {
                        InstallLogic.Uninstall(r);
                        log.Items.Add("Revit " + r.Year + ": удалено.");
                    }
                }
                catch (Exception ex)
                {
                    log.Items.Add("Revit " + r.Year + ": ОШИБКА " + ex.Message);
                }
            }

            foreach (var exe in InstallLogic.RunningRevit())
            {
                var year = PathYear(exe);
                if (year != 0)
                {
                    var r = _detected.FirstOrDefault(x => x.Year == year);
                    if (r != null && r.IsInstalled)
                        log.Items.Add("ВНИМАНИЕ: Revit " + year + " запущен — плагин появится после перезапуска Revit.");
                }
            }
            RefreshList();
        }

        private void StartRevit(ListBox log)
        {
            var exes = InstallLogic.RunningRevit();
            if (exes.Count == 0) { log.Items.Clear(); log.Items.Add("Revit не запущен."); return; }

            foreach (var exe in exes)
                log.Items.Add("Работает: " + exe);

            log.Items.Clear();
            foreach (var exe in exes)
            {
                try
                {
                    InstallLogic.RestartRevit(exe);
                    log.Items.Add("Перезапущен: " + exe);
                }
                catch (Exception ex)
                {
                    log.Items.Add("Ошибка: " + ex.Message);
                }
            }
        }

        private List<RevitInstall> Selected()
        {
            var list = new List<RevitInstall>();
            for (int i = 0; i < _grid.Rows.Count; i++)
            {
                var r = _detected[i];
                var cell = _grid.Rows[i].Cells["sel"];
                var val = cell.Value as bool? ?? false;
                if (val) list.Add(r);
            }
            return list;
        }

        private static int PathYear(string exe)
        {
            var d = System.IO.Path.GetDirectoryName(exe);
            var name = System.IO.Path.GetFileNameWithoutExtension(d);
            var parts = name.Split(' ');
            return (parts.Length == 2 && int.TryParse(parts[1], out var y)) ? y : 0;
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool silent = args.Any(a => a.Equals("-silent", StringComparison.OrdinalIgnoreCase));
            bool doInstall = args.Any(a => a.Equals("-install", StringComparison.OrdinalIgnoreCase));
            bool doUninstall = args.Any(a => a.Equals("-uninstall", StringComparison.OrdinalIgnoreCase));

            if (silent)
            {
                if (doInstall || !args.Contains("-uninstall"))
                {
                    foreach (var r in InstallLogic.Detect().Where(r => r.Supported))
                        try { InstallLogic.Install(r); } catch { }
                }
                if (doUninstall)
                    foreach (var r in InstallLogic.Detect())
                        try { InstallLogic.Uninstall(r); } catch { }
                return;
            }

            Application.Run(new MainForm());
        }
    }
}