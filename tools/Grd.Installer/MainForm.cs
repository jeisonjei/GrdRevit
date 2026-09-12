using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace GrdInstaller
{
    public sealed class MainForm : Form
    {
        private static readonly Color BgMain = Color.FromArgb(31, 34, 40);
        private static readonly Color BgPanel = Color.FromArgb(38, 42, 50);
        private static readonly Color BgGrid = Color.FromArgb(27, 30, 36);
        private static readonly Color BgGridAlt = Color.FromArgb(33, 37, 44);
        private static readonly Color Border = Color.FromArgb(60, 65, 76);
        private static readonly Color Accent = Color.FromArgb(255, 122, 26);
        private static readonly Color AccentHover = Color.FromArgb(255, 141, 55);
        private static readonly Color TextMain = Color.FromArgb(235, 238, 242);
        private static readonly Color TextDim = Color.FromArgb(150, 157, 168);
        private static readonly Color TextGrid = Color.FromArgb(210, 215, 224);
        private static readonly Color Green = Color.FromArgb(94, 201, 120);
        private static readonly Color Amber = Color.FromArgb(242, 172, 59);
        private static readonly Color Red = Color.FromArgb(236, 95, 95);

        private readonly List<RevitInstall> _detected = new List<RevitInstall>();
        private readonly DataGridView _grid;
        private readonly ListBox _log;
        private readonly Label _lblStatus;
        private readonly Panel _header;
        private readonly Button _btnInstall;

        public MainForm()
        {
            Text = "Установка плагина JTOOLS";
            Font = new Font("Segoe UI", 9.5f);
            Size = new Size(840, 640);
            MinimumSize = new Size(760, 560);
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            BackColor = BgMain;
            ForeColor = TextMain;
            AutoScaleMode = AutoScaleMode.Dpi;

            _header = new HeaderPanel
            {
                Dock = DockStyle.Top,
                Height = 88,
                BackColor = Color.Transparent
            };

            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18, 14, 18, 0),
                ColumnCount = 1,
                RowCount = 4,
                BackColor = BgMain
            };
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 240));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var lbl = new Label
            {
                Text = "Установленные версии Revit",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = TextMain,
                Font = new Font("Segoe UI Semibold", 10f)
            };

            _grid = new DarkGrid
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = false,
                MultiSelect = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BackgroundColor = BgGrid,
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor = Border,
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeight = 32,
                RowTemplate = { Height = 34 },
                AllowUserToResizeRows = false,
                AllowUserToOrderColumns = false
            };
            _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = BgPanel,
                ForeColor = TextDim,
                SelectionBackColor = BgPanel,
                SelectionForeColor = TextDim,
                Font = new Font("Segoe UI Semibold", 9f),
                Alignment = DataGridViewContentAlignment.MiddleLeft
            };
            _grid.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = BgGrid,
                ForeColor = TextGrid,
                SelectionBackColor = Color.FromArgb(58, 63, 74),
                SelectionForeColor = TextMain,
                Font = new Font("Segoe UI", 9.5f),
                Padding = new Padding(2, 0, 0, 0)
            };
            _grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = BgGridAlt,
                ForeColor = TextGrid,
                SelectionBackColor = Color.FromArgb(58, 63, 74),
                SelectionForeColor = TextMain
            };
            _grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = "sel", HeaderText = "Отметить", Width = 64,
                ReadOnly = false, FillWeight = 8,
                CellTemplate = new DarkCheckboxCell()
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ver", HeaderText = "Версия", ReadOnly = true, FillWeight = 14 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "status", HeaderText = "Статус", ReadOnly = true, FillWeight = 18 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "exe", HeaderText = "Путь к Revit.exe", ReadOnly = true, FillWeight = 60 });

            var btns = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                Padding = new Padding(0, 10, 0, 0),
                BackColor = BgMain
            };
            _btnInstall = CreateButton("Установить выбранные", Accent, Color.White);
            var btnUninstall = CreateButton("Удалить выбранные", Color.Transparent, TextMain);
            var btnStart = CreateButton("Перезапустить Revit", Color.Transparent, TextMain);
            var btnRefresh = CreateButton("Обновить список", Color.Transparent, TextMain);
            btnUninstall.Width = 170;
            btnStart.Width = 170;
            btnRefresh.Width = 140;
            StyleOutline(btnUninstall);
            StyleOutline(btnStart);
            StyleOutline(btnRefresh);
            _btnInstall.FlatAppearance.MouseOverBackColor = AccentHover;
            _btnInstall.FlatAppearance.MouseDownBackColor = Accent;
            btns.Controls.AddRange(new Control[] { _btnInstall, btnUninstall, btnStart, btnRefresh });

            _log = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = BgGrid,
                ForeColor = TextGrid,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9f),
                IntegralHeight = false
            };

            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 34,
                BackColor = BgPanel,
                Padding = new Padding(18, 0, 18, 0)
            };
            _lblStatus = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = TextDim,
                Text = ""
            };
            footer.Controls.Add(_lblStatus);

            body.Controls.Add(lbl, 0, 0);
            body.Controls.Add(_grid, 0, 1);
            body.Controls.Add(btns, 0, 2);
            body.Controls.Add(_log, 0, 3);
            Controls.Add(footer);
            Controls.Add(body);
            Controls.Add(_header);
            _header.BringToFront();

            _btnInstall.Click += (s, e) => Run(log: _log, install: true);
            btnUninstall.Click += (s, e) => Run(log: _log, install: false);
            btnStart.Click += (s, e) => StartRevit(_log);
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
                var installed = r.IsInstalled;
                var status = installed ? "Установлен" : "Не установлен";
                var idx = _grid.Rows.Add(true, "Revit " + r.Year, status, r.ExePath);
                var cell = _grid.Rows[idx].Cells["status"];
                cell.Style.ForeColor = installed ? Green : TextDim;
                cell.Style.Font = new Font("Segoe UI", 9.5f, installed ? FontStyle.Bold : FontStyle.Regular);
            }
            var installedCount = _detected.Count(x => x.IsInstalled);
            _lblStatus.Text = "Обнаружено: " + _detected.Count + " версий Revit (установлено плагина: " + installedCount + ")   •   " +
                              "Версия сборки: " + ProductVersionText;
        }

        private static string ProductVersionText
        {
            get
            {
                try
                {
                    var v = Assembly.GetExecutingAssembly().GetName().Version;
                    if (v != null) return v.ToString(3);
                }
                catch { }
                return "1.0.0";
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
                        log.Items.Add("Revit " + r.Year + ": плагин установлен.");
                    }
                    else
                    {
                        InstallLogic.Uninstall(r);
                        log.Items.Add("Revit " + r.Year + ": плагин удалён.");
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

        // ---- Утилиты оформления ----

        private static Button CreateButton(string text, Color back, Color fore)
        {
            return new Button
            {
                Text = text,
                Width = 180,
                Height = 32,
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderSize = 1, BorderColor = Border },
                BackColor = back,
                ForeColor = fore,
                Font = new Font("Segoe UI Semibold", 9.5f),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 12, 0)
            };
        }

        private static void StyleOutline(Button b)
        {
            b.UseVisualStyleBackColor = false;
            b.MouseEnter += (s, e) =>
            {
                b.BackColor = BgPanel;
                b.FlatAppearance.BorderColor = Accent;
            };
            b.MouseLeave += (s, e) =>
            {
                b.BackColor = Color.Transparent;
                b.FlatAppearance.BorderColor = Border;
            };
        }

        /// <summary>Верхняя панель с градиентом и логотипом-полосой.</summary>
        private sealed class HeaderPanel : Panel
        {
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = ClientRectangle;

                using (var grad = new LinearGradientBrush(rect, Color.FromArgb(52, 56, 66), Color.FromArgb(31, 34, 40), 0f))
                    g.FillRectangle(grad, rect);

                using (var pen = new Pen(Color.FromArgb(30, 32, 38), 1f))
                    g.DrawLine(pen, 0, rect.Height - 1, rect.Width, rect.Height - 1);

                // Акцентная полоса слева.
                using (var accent = new LinearGradientBrush(
                    new Rectangle(18, 20, 6, 48), Accent, AccentHover, 90f))
                    g.FillRectangle(accent, 18, 20, 6, 48);

                using (var titleFont = new Font("Segoe UI Semibold", 20f))
                using (var subFont = new Font("Segoe UI", 9.5f))
                {
                    g.DrawString("Ассистент установки JTOOLS", titleFont, Brushes.White, 34, 18);
                    g.DrawString("Плагин отопительных приборов для Autodesk Revit 2020–2026",
                        subFont, new SolidBrush(TextDim), 36, 52);
                }
            }
        }

        /// <summary>DataGridView с включённым двойным буфером (без мерцания).</summary>
        private sealed class DarkGrid : DataGridView
        {
            public DarkGrid()
            {
                DoubleBuffered = true;
            }
        }

        /// <summary>CheckBox-ячейка с тёмной темой.</summary>
        private sealed class DarkCheckboxCell : DataGridViewCheckBoxCell
        {
            private readonly SolidBrush _bg = new SolidBrush(BgGrid);

            protected override void Paint(Graphics graphics,
                Rectangle clipBounds, Rectangle cellBounds, int rowIndex,
                DataGridViewElementStates elementState, object value,
                object formattedValue, string errorText,
                DataGridViewCellStyle cellStyle,
                DataGridViewAdvancedBorderStyle advancedBorderStyle,
                DataGridViewPaintParts paintParts)
            {
                graphics.FillRectangle(_bg, cellBounds);
                base.Paint(graphics, clipBounds, cellBounds, rowIndex, elementState, value,
                    formattedValue, errorText, cellStyle, advancedBorderStyle,
                    paintParts & ~DataGridViewPaintParts.Border & ~DataGridViewPaintParts.Background);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _bg.Dispose();
                }
                base.Dispose(disposing);
            }
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