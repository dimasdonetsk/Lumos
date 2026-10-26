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
        public static SolidColorBrush StatusGrey => IsLight ? Make("#5A6A80") : Make("#9E92AF");

        private static readonly SolidColorBrush LightGreen = Make("#17803F");
        private static readonly SolidColorBrush LightYellow = Make("#8A6200");
        private static readonly SolidColorBrush LightRed = Make("#C2362C");

        // Напівпрозорі підкладки під іконку статусу.
        public static readonly SolidColorBrush TileGreen = Make("#2636C66A");
        public static readonly SolidColorBrush TileYellow = Make("#2EF2C14E");
        public static readonly SolidColorBrush TileRed = Make("#2BF0574D");
        public static readonly SolidColorBrush TileGrey = Make("#229E92AF");

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
                Set("Card", "#241C2E"); Set("Line", "#3A2F48"); Set("Line2", "#30273D");
                Set("Fg", "#F1ECF7"); Set("Fg2", "#B3A7C4"); Set("Fg3", "#9E92AF");
                Set("TabOn", "#43375A"); Set("Soft", "#2C2337");
                Set("AlertBg", "#3E1B1C"); Set("AlertFg", "#FFB8B1");
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
