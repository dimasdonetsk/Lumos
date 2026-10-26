using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lumos
{
    /// <summary>
    /// Розпізнавання картинки-графіка Волиньобленерго (PNG 1750×2250).
    ///
    /// Макет фіксований: дві сітки (00–12 і 12–24), у кожній рядок заголовка і 12 рядків підчерг
    /// 1.1–6.2 по 24 клітинки на пів години. Світло-сіро-блакитна клітинка — світло є, темно-синя — немає.
    /// Сітку знаходимо за білими лініями між клітинками і перевіряємо форму й кольори,
    /// перш ніж читати бодай одну клітинку. Якщо щось не сходиться — картинку не беремо зовсім,
    /// а підчергу з незнайомим кольором пропускаємо: краще «немає даних», ніж неправильний графік.
    /// Алгоритм перенесено з fastiuk/ukraine-energy-monitor (MIT), де його виміряно на справжніх картинках.
    /// </summary>
    internal static class VolynImage
    {
        public const int Width = 1750, Height = 2250;
        private const int Columns = 24;
        private const int Groups = 12;
        private static readonly (int R, int G, int B) OnColor = (185, 200, 215);
        private static readonly (int R, int G, int B) OffColor = (68, 110, 155);
        private const int ColorTolerance = 24;
        private const int InkLevel = 236; // яскравість нижче — таблиця, вище — білий фон і лінії
        private const int Edge = 3;       // відступ від лівого краю клітинки, повз текст заголовка
        private const int Inset = 6;      // скільки обрізати з кожного боку клітинки перед читанням кольору
        private const int MaxBytes = 2 * 1024 * 1024;

        /// <summary>Пікселі як RGB у вигляді 0xRRGGBB; прозорі пікселі — білий фон.</summary>
        public sealed class Pixels
        {
            public int W, H;
            public int[] Rgb = Array.Empty<int>();
            public int At(int x, int y) => Rgb[y * W + x];
        }

        public static Pixels Decode(byte[] png)
        {
            if (png.Length > MaxBytes) throw new InvalidDataException("Volyn: image too large");
            using var stream = new MemoryStream(png);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames[0];
            if (frame.PixelWidth != Width || frame.PixelHeight != Height)
                throw new InvalidDataException($"Volyn: image is {frame.PixelWidth}×{frame.PixelHeight}, expected {Width}×{Height}");

            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int stride = Width * 4;
            var raw = new byte[stride * Height];
            bgra.CopyPixels(raw, stride, 0);

            var px = new Pixels { W = Width, H = Height, Rgb = new int[Width * Height] };
            for (int i = 0, p = 0; i < px.Rgb.Length; i++, p += 4)
            {
                int b = raw[p], g = raw[p + 1], r = raw[p + 2], a = raw[p + 3];
                if (a < 255)
                {
                    // Прозоре — це білий фон сторінки.
                    r = (r * a + 255 * (255 - a)) / 255;
                    g = (g * a + 255 * (255 - a)) / 255;
                    b = (b * a + 255 * (255 - a)) / 255;
                }
                px.Rgb[i] = (r << 16) | (g << 8) | b;
            }
            return px;
        }

        /// <summary>48 слотів на кожну підчергу («1.1» … «6.2»): 1 — світло є, 2 — немає. Нерозпізнані підчерги відсутні.</summary>
        public static Dictionary<string, int[]> Parse(Pixels img)
        {
            var (columns, halves) = LocateGrid(img);
            var result = new Dictionary<string, int[]>();
            for (int g = 0; g < Groups; g++)
            {
                var slots = new int[48];
                bool ok = true;
                int i = 0;
                foreach (List<(int, int)> rows in halves)
                    foreach ((int, int) column in columns)
                    {
                        int? slot = Slot(img, column, rows[g]);
                        if (slot == null) { ok = false; break; }
                        slots[i++] = slot.Value;
                    }
                if (ok && i == 48) result[(g / 2 + 1) + "." + (g % 2 + 1)] = slots;
            }
            return result;
        }

        private static int Luma(int rgb) =>
            (((rgb >> 16) & 255) * 299 + ((rgb >> 8) & 255) * 587 + (rgb & 255) * 114) / 1000;

        private static List<(int Start, int End)> Runs(IReadOnlyList<bool> flags)
        {
            var runs = new List<(int, int)>();
            int start = -1;
            for (int i = 0; i < flags.Count; i++)
            {
                if (flags[i] && start < 0) start = i;
                else if (!flags[i] && start >= 0) { runs.Add((start, i)); start = -1; }
            }
            if (start >= 0) runs.Add((start, flags.Count));
            return runs;
        }

        private static void Even(IReadOnlyList<(int Start, int End)> spans, string what)
        {
            var sizes = spans.Select(s => s.End - s.Start).ToList();
            bool gapsOk = spans.Zip(spans.Skip(1), (a, b) => b.Start - a.End).All(gap => gap <= 8);
            if (sizes.Min() < 30 || sizes.Max() - sizes.Min() > 4 || !gapsOk)
                throw new InvalidDataException("Volyn: uneven " + what + " in the schedule grid");
        }

        private static bool Near(int rgb, (int R, int G, int B) c) =>
            Math.Abs(((rgb >> 16) & 255) - c.R) <= ColorTolerance &&
            Math.Abs(((rgb >> 8) & 255) - c.G) <= ColorTolerance &&
            Math.Abs((rgb & 255) - c.B) <= ColorTolerance;

        private static (List<(int Start, int End)> Columns, List<List<(int Start, int End)>> Halves) LocateGrid(Pixels img)
        {
            int w = img.W, h = img.H;
            var ink = new bool[w * h];
            for (int i = 0; i < ink.Length; i++) ink[i] = Luma(img.Rgb[i]) < InkLevel;

            // Стовпці: клітинки мають колір таблиці згори донизу обох сіток, лінії між ними білі.
            var counts = new int[w];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++) if (ink[row + x]) counts[x]++;
            }
            int half = counts.Max() / 2;
            var columns = Runs(counts.Select(c => c > half).ToList());
            if (columns.Count != Columns + 2) // «Черга», «Підчерга», далі стовпці часу
                throw new InvalidDataException($"Volyn: expected {Columns + 2} table columns, found {columns.Count}");
            var label = columns[1];
            columns = columns.Skip(2).ToList();
            Even(columns, "columns");

            // Рядки: зафарбовано лівий край клітинки «Підчерга» і кожної клітинки часу.
            var xs = new List<int> { label.Start + Edge };
            xs.AddRange(columns.Select(c => c.Start + Edge));
            var bandFlags = new bool[h];
            for (int y = 0; y < h; y++) bandFlags[y] = xs.All(x => ink[y * w + x]);
            var bands = Runs(bandFlags);
            if (bands.Count != 2 * (1 + Groups))
                throw new InvalidDataException($"Volyn: expected two grids of {1 + Groups} rows, found {bands.Count} rows");

            var halves = new List<List<(int, int)>>();
            foreach (var grid in new[] { bands.Take(bands.Count / 2).ToList(), bands.Skip(bands.Count / 2).ToList() })
            {
                var header = grid[0];
                var rows = grid.Skip(1).ToList();
                Even(rows, "rows");
                if (rows[0].Start - header.End > 8) throw new InvalidDataException("Volyn: header row is apart from the grid");
                int middle = (header.Start + header.End) / 2;
                bool labelsOk = rows.All(r => Near(img.At(label.Start + Edge, (r.Start + r.End) / 2), OnColor));
                bool headerOk = columns.All(c => Near(img.At(c.Start + Edge, middle), OffColor));
                if (!labelsOk || !headerOk) throw new InvalidDataException("Volyn: the schedule grid has unexpected colours");
                halves.Add(rows);
            }
            return (columns, halves);
        }

        private static int? Slot(Pixels img, (int Start, int End) column, (int Start, int End) row)
        {
            int x0 = column.Start + Inset, x1 = column.End - Inset, y0 = row.Start + Inset, y1 = row.End - Inset;
            if (x1 <= x0 || y1 <= y0) return null;
            var colors = new Dictionary<int, int>();
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int c = img.At(x, y);
                    colors[c] = colors.TryGetValue(c, out int n) ? n + 1 : 1;
                    if (colors.Count > 16) return null; // градієнти чи текст — не клітинка графіка
                }
            var top = colors.OrderByDescending(kv => kv.Value).First();
            if (top.Value < 0.9 * (x1 - x0) * (y1 - y0)) return null;
            if (Near(top.Key, OnColor)) return 1;
            if (Near(top.Key, OffColor)) return 2;
            return null;
        }
    }
}
