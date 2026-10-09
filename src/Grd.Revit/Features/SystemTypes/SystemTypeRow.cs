using System.Windows.Media;
using AutodeskColor = Autodesk.Revit.DB.Color;

namespace GrdRevit.Ui
{
    public class SystemTypeRow : ObservableObject
    {
        private static readonly Brush DirtyBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xE9, 0xA8));

        public Autodesk.Revit.DB.ElementId Id { get; }

        private string _name;
        private string _abbreviation;
        private AutodeskColor _color;
        private int _lineWeight;
        private long _patternKey;

        public string OriginalName { get; private set; }
        public string OriginalAbbreviation { get; private set; }
        public AutodeskColor OriginalColor { get; private set; }
        public int OriginalLineWeight { get; private set; }
        public long OriginalPatternKey { get; private set; }

        public SystemTypeRow(
            Autodesk.Revit.DB.ElementId id,
            string name,
            string abbreviation,
            AutodeskColor color,
            int lineWeight,
            long patternKey)
        {
            Id = id;
            _name = name ?? string.Empty;
            _abbreviation = abbreviation ?? string.Empty;
            _color = color;
            _lineWeight = lineWeight;
            _patternKey = patternKey;
            ResetOriginals();
        }

        public string Name
        {
            get => _name;
            set
            {
                if (Set(ref _name, value))
                {
                    OnPropertyChanged(nameof(NameDirtyBrush));
                    OnPropertyChanged(nameof(ColorSortKey));
                }
            }
        }

        public string Abbreviation
        {
            get => _abbreviation;
            set
            {
                if (Set(ref _abbreviation, value))
                {
                    OnPropertyChanged(nameof(AbbreviationDirtyBrush));
                }
            }
        }

        public AutodeskColor ColorValue
        {
            get => _color;
            set
            {
                if (Set(ref _color, value))
                {
                    OnPropertyChanged(nameof(ColorDirtyBrush));
                    OnPropertyChanged(nameof(ColorBrush));
                    OnPropertyChanged(nameof(ColorHex));
                    OnPropertyChanged(nameof(ColorSortKey));
                }
            }
        }

        public int LineWeight
        {
            get => _lineWeight;
            set
            {
                if (Set(ref _lineWeight, value))
                {
                    OnPropertyChanged(nameof(LineWeightDirtyBrush));
                    OnPropertyChanged(nameof(ColorSortKey));
                }
            }
        }

        public long PatternKey
        {
            get => _patternKey;
            set
            {
                if (Set(ref _patternKey, value))
                {
                    OnPropertyChanged(nameof(PatternDirtyBrush));
                    OnPropertyChanged(nameof(ColorSortKey));
                }
            }
        }

        public long PatternSortKey => _patternKey;

        public bool IsNameDirty => _name != OriginalName;
        public bool IsAbbreviationDirty => _abbreviation != OriginalAbbreviation;
        public bool IsColorDirty => _color != OriginalColor
            || (_color != null && OriginalColor != null && !ColorsEqual(_color, OriginalColor));
        public bool IsLineWeightDirty => _lineWeight != OriginalLineWeight;
        public bool IsPatternDirty => _patternKey != OriginalPatternKey;

        public bool IsDirtyAny => IsNameDirty || IsAbbreviationDirty || IsColorDirty ||
                                  IsLineWeightDirty || IsPatternDirty;

        public Brush NameDirtyBrush => IsNameDirty ? DirtyBrush : Brushes.Transparent;
        public Brush AbbreviationDirtyBrush => IsAbbreviationDirty ? DirtyBrush : Brushes.Transparent;
        public Brush ColorDirtyBrush => IsColorDirty ? DirtyBrush : Brushes.Transparent;
        public Brush LineWeightDirtyBrush => IsLineWeightDirty ? DirtyBrush : Brushes.Transparent;
        public Brush PatternDirtyBrush => IsPatternDirty ? DirtyBrush : Brushes.Transparent;

        public Brush ColorBrush
        {
            get
            {
                var c = _color;
                if (c == null || !c.IsValid) return Brushes.White;
                return new SolidColorBrush(Color.FromRgb(c.Red, c.Green, c.Blue));
            }
        }

        public string ColorHex
        {
            get
            {
                var c = _color;
                if (c == null || !c.IsValid) return "—";
                return "#" + c.Red.ToString("X2") + c.Green.ToString("X2") + c.Blue.ToString("X2");
            }
        }

        public int ColorSortKey => _color == null || !_color.IsValid
            ? -1
            : _color.Red * 65536 + _color.Green * 256 + _color.Blue;

        public void ResetOriginals()
        {
            OriginalName = _name;
            OriginalAbbreviation = _abbreviation;
            OriginalColor = _color;
            OriginalLineWeight = _lineWeight;
            OriginalPatternKey = _patternKey;
        }

        private static bool ColorsEqual(AutodeskColor a, AutodeskColor b)
        {
            if (a == null) return false;
            if (b == null) return false;
            return a.IsValid && b.IsValid && a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue;
        }
    }
}