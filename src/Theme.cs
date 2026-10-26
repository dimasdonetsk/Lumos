using System.Windows;
using System.Windows.Media;

namespace Lumos
{
    /// <summary>Дві теми: темна і світла.</summary>
    internal static class Theme
    {
        public static bool IsLight { get; private set; }

        // Кольори клітинок графіка однакові в обох темах.
        public static readonly SolidColorBrush CellGreen = Make("#36C66A");
        public static readonly SolidColorBrush CellYellow = Make("#F2C14E");
        public static readonly SolidColorBrush CellRed = Make("#F0574D");

        // Колір тексту й іконок статусу: у світлій темі темніший, щоб читався на світлому фоні.
        public static SolidColorBrush StatusGreen => IsLight ? LightGreen : CellGreen;
        public static SolidColorBrush StatusYellow => IsLight ? LightYellow : CellYellow;
        public static SolidColorBrush StatusRed => IsLight ? LightRed : CellRed;
        public static SolidColorBrush StatusGrey => IsLight ? Make("#5A6A80") : Make("#8E8E96");

        private static readonly SolidColorBrush LightGreen = Make("#17803F");
        private static readonly SolidColorBrush LightYellow = Make("#8A6200");
        private static readonly SolidColorBrush LightRed = Make("#C2362C");

        // Напівпрозорі підкладки під іконку статусу.
        public static readonly SolidColorBrush TileGreen = Make("#2636C66A");
        public static readonly SolidColorBrush TileYellow = Make("#2EF2C14E");
        public static readonly SolidColorBrush TileRed = Make("#2BF0574D");
        public static readonly SolidColorBrush TileGrey = Make("#228E8E96");

        public static SolidColorBrush Get(string key) =>
            Application.Current.Resources[key] as SolidColorBrush ?? Brushes.Gray;

        public static void Apply(bool light)
        {
            IsLight = light;
            if (light)
            {
                Set("Card", "#EEF3F9"); Set("Line", "#D3DDEA"); Set("Line2", "#DFE6EF");
                Set("Fg", "#142033"); Set("Fg2", "#4E5D73"); Set("Fg3", "#5A6A80");
                Set("TabOn", "#FFFFFF"); Set("Soft", "#DCE4EE");
                Set("AlertBg", "#F9E3E1"); Set("AlertFg", "#9E2A1F");
            }
            else
            {
                Set("Card", "#0E0E11"); Set("Line", "#24242A"); Set("Line2", "#1C1C21");
                Set("Fg", "#F0F0F2"); Set("Fg2", "#A6A6AE"); Set("Fg3", "#8E8E96");
                Set("TabOn", "#2B2B32"); Set("Soft", "#18181C");
                Set("AlertBg", "#2E1416"); Set("AlertFg", "#FFB8B1");
            }
        }

        private static void Set(string key, string hex) => Application.Current.Resources[key] = Make(hex);

        public static SolidColorBrush Make(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
