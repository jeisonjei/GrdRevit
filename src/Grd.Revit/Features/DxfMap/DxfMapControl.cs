using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GrdRevit.Core;

namespace GrdRevit.Ui
{
    /// <summary>
    /// Отрисовка DXF-плана (концепция операций позаимствована у tomatsapr:
    /// геометрия не мутируется, экран = (мир - смещение) * масштаб):
    ///   - pan: правая/средняя кнопка или Space+левая;
    ///   - зум колесом к точке под курсором (scale * 0.9 / 1.1, clamp [0.1, 10]);
    ///   - клик: hit-test прибора с порогом 20 px (toggle);
    ///   - рамка левой кнопкой: слева->направо — «окно» (фиолетовая), иначе — «секущая» (жёлтая).
    /// </summary>
    public sealed class DxfMapControl : FrameworkElement
    {
        public const double HoverDistanceThreshold = 20.0; // px, как HOVER_DISTANCE_THRESHOLD
        private const double FrameDragThreshold = 4.0;     // px: движение до/после это «рамка», иначе «клик»
        private const double MinScale = 1e-4;              // планы в абсолютных координатах имеют габариты ~10^6 единиц
        private const double MaxScale = 10.0;

        private List<DxfGeomLine> _lines = new List<DxfGeomLine>();
        private List<DxfCalloutItem> _devices = new List<DxfCalloutItem>();
        private List<DxfTextLabel> _texts = new List<DxfTextLabel>();
        private readonly HashSet<DxfCalloutItem> _selected = new HashSet<DxfCalloutItem>();
        private readonly HashSet<DxfCalloutItem> _warnings = new HashSet<DxfCalloutItem>();
        private readonly HashSet<DxfCalloutItem> _typeHighlight = new HashSet<DxfCalloutItem>();
        private readonly HashSet<DxfCalloutItem> _mismatch = new HashSet<DxfCalloutItem>();

        private double _scale = 1.0;
        private double _ox;              // мировая X левого края
        private double _oy;              // мировая Y нижнего края (Y в мире направлен вверх)

        private bool _dragPan;
        private bool _dragFrame;
        private bool _clickPending;
        private bool _frameIsWindow;
        private Point _pressPos;
        private Point _lastPos;
        private Rect _frameRect;
        private bool _hasFrame;

        private DxfCalloutItem _hovered;

        private bool _showValveSettings;

        private bool _pendingFit;   // окно ещё не разложено: подгонку отложить до первой отрисовки

        public event Action SelectionChanged;
        public event Action<DxfCalloutItem> HoverChanged;

        /// <summary>Выбранные приборы (порядок = порядок в списке плана).</summary>
        public IReadOnlyList<DxfCalloutItem> Selected => _devices.Where(_selected.Contains).ToList();

        /// <summary>Показывать ли на плане настройки клапанов «n=…» у каждого прибора.</summary>
        public bool ShowValveSettings
        {
            get => _showValveSettings;
            set
            {
                if (_showValveSettings == value) return;
                _showValveSettings = value;
                InvalidateVisual();
            }
        }

        public DxfCalloutItem Hovered => _hovered;

        private readonly FontFamily _fontFamily;

        public DxfMapControl()
        {
            _fontFamily = new FontFamily("Segoe UI");
            Focusable = true;
            ClipToBounds = true;

            MouseDown += OnMouseDown;
            MouseUp += OnMouseUp;
            MouseMove += OnMouseMove;
            MouseWheel += OnMouseWheel;
            SizeChanged += OnSizeChanged;
        }

        public void SetContent(IReadOnlyCollection<DxfGeomLine> lines, IReadOnlyCollection<DxfCalloutItem> devices,
            IReadOnlyCollection<DxfTextLabel> texts)
        {
            _lines = lines?.ToList() ?? new List<DxfGeomLine>();
            _devices = devices?.ToList() ?? new List<DxfCalloutItem>();
            _texts = texts?.ToList() ?? new List<DxfTextLabel>();
            _selected.Clear();
            _warnings.Clear();
            _typeHighlight.Clear();
            _mismatch.Clear();
            _hovered = null;
            FitAll();
        }

        /// <summary>Подсветить предупреждения: приборы, которые не удалось применить.</summary>
        public void SetWarnings(IEnumerable<DxfCalloutItem> items)
        {
            _warnings.Clear();
            if (items != null)
            {
                foreach (var d in items) if (d != null) _warnings.Add(d);
            }
            WarningsChanged?.Invoke();
            InvalidateVisual();
        }

        public void ClearWarnings()
        {
            if (_warnings.Count == 0) return;
            _warnings.Clear();
            WarningsChanged?.Invoke();
            InvalidateVisual();
        }

        public int WarningCount => _warnings.Count;
        public bool HasWarnings => _warnings.Count > 0;

        /// <summary>Событие изменений предупреждений (для управления кнопкой «Очистить»).</summary>
        public event Action WarningsChanged;

        public void ClearSelection()
        {
            if (_selected.Count == 0) return;
            _selected.Clear();
            SelectionChanged?.Invoke();
            InvalidateVisual();
        }

        /// <summary>Добавить прибор к выделению (не очищая остальное).</summary>
        public void AddToSelection(DxfCalloutItem d)
        {
            if (d == null || _selected.Contains(d)) return;
            _selected.Add(d);
            SelectionChanged?.Invoke();
            InvalidateVisual();
        }

        /// <summary>Снять выделение с одного прибора.</summary>
        public void RemoveFromSelection(DxfCalloutItem d)
        {
            if (d == null || !_selected.Remove(d)) return;
            SelectionChanged?.Invoke();
            InvalidateVisual();
        }

        /// <summary>Заменить выделение ровно этими приборами (остальные снимаются).</summary>
        public void SetSelection(IEnumerable<DxfCalloutItem> items)
        {
            _selected.Clear();
            if (items != null)
            {
                foreach (var d in items)
                    if (d != null) _selected.Add(d);
            }
            SelectionChanged?.Invoke();
            InvalidateVisual();
        }

        /// <summary>Подсветить на плане приборы выбранного типа (марки).</summary>
        public void SetTypeHighlight(IEnumerable<DxfCalloutItem> items)
        {
            _typeHighlight.Clear();
            if (items != null)
            {
                foreach (var d in items) if (d != null) _typeHighlight.Add(d);
            }
            InvalidateVisual();
        }

        public void ClearTypeHighlight()
        {
            if (_typeHighlight.Count == 0) return;
            _typeHighlight.Clear();
            InvalidateVisual();
        }

        /// <summary>Подсветить на плане выбранные приборы, чей тип в Revit НЕ соответствует марке DXF.</summary>
        public void SetMismatch(IEnumerable<DxfCalloutItem> items)
        {
            _mismatch.Clear();
            if (items != null)
            {
                foreach (var d in items) if (d != null) _mismatch.Add(d);
            }
            InvalidateVisual();
        }

        public void ClearMismatch()
        {
            if (_mismatch.Count == 0) return;
            _mismatch.Clear();
            InvalidateVisual();
        }

        public void FitAll()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0)
            {
                _pendingFit = true;
                return;
            }
            _pendingFit = false;

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            bool any = false;

            foreach (var l in _lines)
            {
                any = true;
                minX = Math.Min(minX, Math.Min(l.X1, l.X2)); maxX = Math.Max(maxX, Math.Max(l.X1, l.X2));
                minY = Math.Min(minY, Math.Min(l.Y1, l.Y2)); maxY = Math.Max(maxY, Math.Max(l.Y1, l.Y2));
            }
            foreach (var d in _devices)
            {
                any = true;
                minX = Math.Min(minX, d.X); maxX = Math.Max(maxX, d.X);
                minY = Math.Min(minY, d.Y); maxY = Math.Max(maxY, d.Y);
            }
            foreach (var t in _texts)
            {
                any = true;
                minX = Math.Min(minX, t.X); maxX = Math.Max(maxX, t.X);
                minY = Math.Min(minY, t.Y); maxY = Math.Max(maxY, t.Y);
            }

            if (!any)
            {
                _scale = 1.0; _ox = 0; _oy = 0;
                InvalidateVisual();
                return;
            }

            double w = Math.Max(1.0, maxX - minX);
            double h = Math.Max(1.0, maxY - minY);
            double cw = Math.Max(100.0, ActualWidth);
            double ch = Math.Max(100.0, ActualHeight);

            _scale = Math.Max(MinScale, Math.Min(MaxScale, Math.Min(cw / (w * 1.1), ch / (h * 1.1))));
            _ox = (minX + maxX) / 2.0 - cw / (2.0 * _scale);
            _oy = (minY + maxY) / 2.0 - ch / (2.0 * _scale);
            InvalidateVisual();
        }

        /// <summary>Первая фактическая раскладка: выполняем отложенную FitAll (вызов был до разметки).</summary>
        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_pendingFit) return;
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            _pendingFit = false;
            FitAll();
        }

        private Point ScreenToWorld(Point p)
        {
            return new Point(_ox + p.X / _scale, _oy + (ActualHeight - p.Y) / _scale);
        }

        private Point WorldToScreen(double wx, double wy)
        {
            return new Point((wx - _ox) * _scale, ActualHeight - (wy - _oy) * _scale);
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            Focus();
            var pos = e.GetPosition(this);

            if (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space))
            {
                _dragPan = true;
                _lastPos = pos;
                CaptureMouse();
                e.Handled = true;
                return;
            }
            if (e.ChangedButton == MouseButton.Right || e.ChangedButton == MouseButton.Middle)
            {
                _dragPan = true;
                _lastPos = pos;
                CaptureMouse();
                e.Handled = true;
                return;
            }
            if (e.ChangedButton == MouseButton.Left)
            {
                _clickPending = true;
                _dragFrame = false;
                _pressPos = pos;
                _lastPos = pos;
                CaptureMouse();
                e.Handled = true;
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(this);

            if (_dragPan)
            {
                double dx = pos.X - _lastPos.X;
                double dy = pos.Y - _lastPos.Y;
                _ox -= dx / _scale;
                _oy += dy / _scale;
                _lastPos = pos;
                InvalidateVisual();
                return;
            }

            if (_dragFrame)
            {
                _frameRect = NormalizeRect(_pressPos, pos);
                _frameIsWindow = _pressPos.X <= pos.X;
                _hasFrame = true;
                InvalidateVisual();
                return;
            }

            if (_clickPending)
            {
                if ((pos - _pressPos).Length > FrameDragThreshold)
                {
                    _clickPending = false;
                    _dragFrame = true;
                    _frameRect = NormalizeRect(_pressPos, pos);
                    _frameIsWindow = _pressPos.X <= pos.X;
                    _hasFrame = true;
                    InvalidateVisual();
                    return;
                }
                _lastPos = pos;
                return;
            }

            UpdateHover(pos, e.LeftButton != MouseButtonState.Pressed && e.RightButton != MouseButtonState.Pressed && e.MiddleButton != MouseButtonState.Pressed);
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragPan && !_dragFrame && !_clickPending) return;

            var pos = e.GetPosition(this);

            if (_dragPan)
            {
                _dragPan = false;
                ReleaseMouseCapture();
                e.Handled = true;
                return;
            }

            if (_dragFrame)
            {
                _dragFrame = false;
                ReleaseMouseCapture();
                ApplyFrameSelection(_frameRect, _frameIsWindow);
                _hasFrame = false;
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (_clickPending)
            {
                _clickPending = false;
                ReleaseMouseCapture();
                ClickSelect(pos);
                e.Handled = true;
            }
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var pos = e.GetPosition(this);
            bool zoomIn = e.Delta > 0;
            ZoomAt(pos, zoomIn ? 1.1 : 0.9);
            e.Handled = true;
        }

        /// <summary>Масштаб относительно точки экрана: мировая точка под курсором остаётся на месте.</summary>
        public void ZoomAt(Point screen, double factor)
        {
            var world = ScreenToWorld(screen);
            _scale = Math.Max(MinScale, Math.Min(MaxScale, _scale * factor));
            _ox = world.X - screen.X / _scale;
            _oy = world.Y - (ActualHeight - screen.Y) / _scale;
            UpdateHover(screen, true);
            InvalidateVisual();
        }

        private void UpdateHover(Point pos, bool allow)
        {
            if (!allow) return;
            var hit = HitTestDevice(pos);
            if (ReferenceEquals(_hovered, hit)) return;
            _hovered = hit;
            HoverChanged?.Invoke(hit);
            InvalidateVisual();
        }

        private DxfCalloutItem HitTestDevice(Point pos)
        {
            // Порог в экранных пикселях: мировое расстояние переводится через масштаб.
            var world = ScreenToWorld(pos);
            double worldTol = HoverDistanceThreshold / _scale;
            DxfCalloutItem best = null;
            double bestD = worldTol;
            foreach (var d in _devices)
            {
                double dx = d.X - world.X;
                double dy = d.Y - world.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist <= bestD)
                {
                    bestD = dist;
                    best = d;
                }
            }
            return best;
        }

        /// <summary>Клик: toggle прибора на расстоянии ≤ 20 px.</summary>
        private void ClickSelect(Point pos)
        {
            var hit = HitTestDevice(pos);
            if (hit == null)
            {
                ClearSelection();
                return;
            }

            if (_selected.Remove(hit))
            {
                // Повторный клик по выбранному — снять выделение.
            }
            else
            {
                _selected.Clear();
                _selected.Add(hit);
            }
            SelectionChanged?.Invoke();
            InvalidateVisual();
        }

        /// <summary>
        /// Рамка. window (слева->направо): если всё внутри уже выбрано — снять, иначе выбрать
        /// внутри (семантика окна). crossing (иначе): добавить к выделению (пересечение/внутри).
        /// </summary>
        private void ApplyFrameSelection(Rect r, bool window)
        {
            if (!_hasFrame || (r.Width <= 0 && r.Height <= 0)) return;

            var inside = new List<DxfCalloutItem>();
            foreach (var d in _devices)
            {
                var sp = WorldToScreen(d.X, d.Y);
                if (r.Contains(sp)) inside.Add(d);
            }

            if (window)
            {
                bool allSelected = inside.Count > 0 && inside.All(_selected.Contains);
                if (allSelected)
                {
                    foreach (var d in inside) _selected.Remove(d);
                }
                else
                {
                    foreach (var d in inside) _selected.Add(d);
                }
            }
            else
            {
                foreach (var d in inside)
                {
                    // Секущая: точка внутри рамки -> выбрать (для точечных марок пересечение = вхождение).
                    _selected.Add(d);
                }
            }

            SelectionChanged?.Invoke();
        }

        private static Rect NormalizeRect(Point a, Point b)
        {
            return new Rect(
                Math.Min(a.X, b.X),
                Math.Min(a.Y, b.Y),
                Math.Abs(b.X - a.X),
                Math.Abs(b.Y - a.Y));
        }

        // ---------- Rendering ----------

        private static readonly Pen WallPen = CreatePen(Color.FromRgb(0x30, 0x38, 0x3E), 1.6);
        private static readonly Pen PipeFlowPen = CreatePen(Color.FromRgb(0x2E, 0x6D, 0xC5), 1.2);
        private static readonly Pen PipeReturnPen = CreatePen(Color.FromRgb(0xC7, 0x50, 0x2E), 1.2);
        private static readonly Pen BasePen = CreatePen(Color.FromRgb(0x90, 0x90, 0x90), 1.0);

        private static readonly Brush DeviceFill = Brushes.Navy;
        private static readonly Brush DeviceFillHover = Brushes.SteelBlue;
        private static readonly Brush DeviceFillSelected = Brushes.OrangeRed;
        private static readonly Pen DeviceBorder = new Pen(Brushes.White, 1.0);

        private static readonly Brush ValveSettingBrush = Brushes.DarkSlateGray;

        /// <summary>Цвет крупных надписей плана (тёмно-жёлтый — читается на белом фоне, как «жёлтый» текст DWG).</summary>
        private static readonly Brush BigTextBrush = CreateBrush(255, 0x9A, 0x67, 0x00);

        private static readonly Pen WarningRing = CreatePen(Color.FromRgb(0xC6, 0x28, 0x28), 2.0);
        private static readonly Pen TypeHighlightRing = CreatePen(Color.FromRgb(0x2E, 0x8B, 0x57), 2.2);
        private static readonly Pen MismatchRing = CreatePen(Color.FromRgb(0xB2, 0x22, 0x22), 2.4);

        private static readonly Brush FrameWindowFill = CreateBrush(160, 201, 133, 255);
        private static readonly Pen FrameWindowPen = CreatePen(Color.FromRgb(0x8B, 0x5C, 0xF6), 1.0);
        private static readonly Brush FrameCrossingFill = CreateBrush(60, 255, 235, 120);
        private static readonly Pen FrameCrossingPen = CreatePen(Color.FromRgb(0xE6, 0xB8, 0x00), 1.0);

        private static Brush CreateBrush(byte a, byte r, byte g, byte b)
        {
            return new SolidColorBrush(Color.FromArgb(a, r, g, b));
        }

        private static Pen CreatePen(Color c, double w)
        {
            var pen = new Pen(new SolidColorBrush(c), w);
            pen.Freeze();
            return pen;
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));

            foreach (var l in _lines)
                DrawLine(dc, l);

            DrawTextLabels(dc);

            for (int i = 0; i < _devices.Count; i++)
                DrawDevice(dc, _devices[i]);

            if (_hasFrame)
            {
                DrawFrame(dc, _frameRect, _frameIsWindow);
            }

            // Подсказки управления в углу.
            DrawHint(dc);
        }

        private void DrawLine(DrawingContext dc, DxfGeomLine l)
        {
            dc.DrawLine(SelectPen(l.Layer), WorldToScreen(l.X1, l.Y1), WorldToScreen(l.X2, l.Y2));
        }

        /// <summary>Крупные надписи плана (этажи/зоны): рисуются в мире, размер шрифта пропорционален высоте текста.</summary>
        private void DrawTextLabels(DrawingContext dc)
        {
            if (_texts.Count == 0) return;
            var tf = new Typeface(_fontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            foreach (var t in _texts)
            {
                string text = CleanLabelText(t.Text);
                if (text.Length == 0) continue;

                double size = Math.Max(9.0, t.Height * _scale * 0.9);
                var ft = new FormattedText(text,
                    System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    tf, size, BigTextBrush);
                var p = WorldToScreen(t.X, t.Y);
                dc.DrawText(ft, new Point(p.X + 2, p.Y - ft.Height));
            }
        }

        /// <summary>Убирает разметку MTEXT («{…}», «\P» и прочие коды) — для ярлыков вида «1 этаж» остаётся чистый текст.</summary>
        private static string CleanLabelText(string text)
        {
            var s = (text ?? string.Empty).Replace("\\P", " ");
            var sb = new System.Text.StringBuilder(s.Length);
            int braces = 0;
            foreach (char c in s)
            {
                if (c == '{') { braces++; continue; }
                if (c == '}') { braces = Math.Max(0, braces - 1); continue; }
                if (braces == 0) sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        private void DrawDevice(DrawingContext dc, DxfCalloutItem d)
        {
            var p = WorldToScreen(d.X, d.Y);
            bool selected = _selected.Contains(d);
            bool hover = ReferenceEquals(_hovered, d);
            bool warning = _warnings.Contains(d);
            bool typeHl = _typeHighlight.Contains(d);
            bool mismatch = _mismatch.Contains(d);
            double r = selected ? 7 : hover ? 6 : 5;

            var brush = selected ? DeviceFillSelected : hover ? DeviceFillHover : DeviceFill;
            dc.DrawEllipse(brush, DeviceBorder, p, r, r);

            if (warning)
            {
                dc.DrawEllipse(null, WarningRing, p, r + 4, r + 4);
            }

            if (mismatch)
            {
                dc.DrawEllipse(null, MismatchRing, p, r + 5, r + 5);
            }

            if (typeHl)
            {
                dc.DrawEllipse(null, TypeHighlightRing, p, r + 5, r + 5);
            }

            if (_showValveSettings && d.Device != null &&
                (!string.IsNullOrEmpty(d.Device.ValveSetting) || !string.IsNullOrEmpty(d.Device.Mark)))
            {
                var lines = new List<string>();
                if (!string.IsNullOrEmpty(d.Device.Mark))
                    lines.Add(d.Device.Mark);
                if (!string.IsNullOrEmpty(d.Device.ValveSetting))
                    lines.Add("n=" + d.Device.ValveSetting);

                var tf = new Typeface(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var fts = lines.Select(line => new FormattedText(line,
                    System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    tf, 9.5, ValveSettingBrush)).ToList();

                double txr = p.X + r + 3;
                double tyr = p.Y + r + 1;
                double maxW = fts.Max(f => f.Width);
                double totalH = fts.Sum(f => f.Height);

                double tx = txr;
                double ty = tyr;
                if (tx + maxW > ActualWidth) tx = Math.Max(0, p.X - r - 3 - maxW);
                if (ty + totalH > ActualHeight) ty = Math.Max(0, ActualHeight - totalH);

                foreach (var ft in fts)
                {
                    dc.DrawText(ft, new Point(tx, ty));
                    ty += ft.Height;
                }
            }
        }

        private void DrawFrame(DrawingContext dc, Rect r, bool window)
        {
            dc.DrawRectangle(window ? FrameWindowFill : FrameCrossingFill,
                window ? FrameWindowPen : FrameCrossingPen, r);
        }

        private void DrawHint(DrawingContext dc)
        {
            const double fs = 10.5;
            var lines = new[]
            {
                "Колесо — зум к курсору   |   Правая/Средняя или Пробел+левая — панорама",
                "Левая — выбор прибора   |   Рамка вправо — окно, рамка влево — секущая"
            };
            double y = ActualHeight - ((lines.Length + 1) * fs + 8);
            for (int i = 0; i < lines.Length; i++)
            {
                var ft = new FormattedText(lines[i],
                    System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    fs, Brushes.Gray);
                dc.DrawText(ft, new Point(8, y + i * (fs + 3)));
            }
        }

        private static Pen SelectPen(string layer)
        {
            string l = (layer ?? string.Empty).ToLowerInvariant();
            if (l.Contains("wall") || l.Contains("стен") || l.Contains("ось") || l == "0")
                return WallPen;
            if (l.Contains("flowpipe") || l.Contains("пода") || l.Contains("подвод"))
                return PipeFlowPen;
            if (l.Contains("returnpipe") || l.Contains("обрат") || l.Contains("обр."))
                return PipeReturnPen;
            return BasePen;
        }
    }
}