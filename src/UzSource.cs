using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Lumos
{
    /// <summary>
    /// Графіки Укрзалізниці (АТ «Укрзалізниця» як оператор системи розподілу).
    /// Для кожної області окрема сторінка з таблицею: проміжки часу і черги, які в цей час без світла.
    /// </summary>
    internal static class UzSource
    {
        public const string Prefix = "uz:";
        private const string BaseUrl =
            "https://www.uz.gov.ua/about/activity/electropostachannia/electro_consumers/temporary_shutdown/grafikiobmezhen/";

        /// <summary>Сторінки областей на сайті Укрзалізниці і відповідні області в даних обленерго.</summary>
        public static readonly (string PageId, string Name, string[] OblenergoIds)[] Regions =
        {
            ("649725", "Київ", new[] { "kyiv" }),
            ("622232", "Вінницька область", new[] { "vinnytska-oblast" }),
            ("622240", "Волинська область", new[] { "volynska-oblast" }),
            ("622248", "Дніпропетровська область", new[] { "dnipropetrovska-oblast", "dnipro-dnem", "dnipro-cek" }),
            ("622256", "Донецька область", new[] { "donetska-oblast" }),
            ("622264", "Житомирська область", new[] { "zhytomyrska-oblast" }),
            ("622272", "Закарпатська область", new[] { "zakarpatska-oblast" }),
            ("622280", "Запорізька область", new[] { "zaporizka-oblast" }),
            ("622288", "Івано-Франківська область", new[] { "ivano-frankivska-oblast" }),
            ("622296", "Київська область", new[] { "kiivska-oblast" }),
            ("622304", "Кіровоградська область", new[] { "kirovohradska-oblast" }),
            ("622320", "Львівська область", new[] { "lvivska-oblast" }),
            ("622328", "Миколаївська область", new[] { "mykolaivska-oblast" }),
            ("622336", "Одеська область", new[] { "odeska-oblast" }),
            ("622344", "Полтавська область", new[] { "poltavska-oblast" }),
            ("622352", "Рівненська область", new[] { "rivnenska-oblast" }),
            ("622360", "Сумська область", new[] { "sumska-oblast" }),
            ("622368", "Тернопільська область", new[] { "ternopilska-oblast" }),
            ("622376", "Харківська область", new[] { "kharkivska-oblast" }),
            ("622384", "Херсонська область", new[] { "khersonska-oblast" }),
            ("622392", "Хмельницька область", new[] { "khmelnytska-oblast" }),
            ("622400", "Черкаська область", new[] { "cherkaska-oblast" }),
            ("622408", "Чернівецька область", new[] { "chernivetska-oblast" }),
            ("622416", "Чернігівська область", new[] { "chernihivska-oblast" }),
        };

        public static readonly string[] AllQueues =
            { "1.1", "1.2", "2.1", "2.2", "3.1", "3.2", "4.1", "4.2", "5.1", "5.2", "6.1", "6.2" };

        public static bool IsUz(string? regionId) => regionId != null && regionId.StartsWith(Prefix, StringComparison.Ordinal);

        public static string PageIdOf(string regionId) => regionId.Substring(Prefix.Length);

        public static string? NameOf(string regionId) =>
            Regions.Where(r => Prefix + r.PageId == regionId).Select(r => r.Name).FirstOrDefault();

        public static string PageUrl(string regionId) => BaseUrl + PageIdOf(regionId) + "/";

        /// <summary>Та сама область у даних обленерго (для перемикання постачальника і пошуку аварійних).</summary>
        public static string? ToOblenergo(string regionId) =>
            Regions.Where(r => Prefix + r.PageId == regionId).Select(r => r.OblenergoIds[0]).FirstOrDefault();

        public static string? FromOblenergo(string? oblenergoId) =>
            oblenergoId == null ? null
            : Regions.Where(r => r.OblenergoIds.Contains(oblenergoId)).Select(r => Prefix + r.PageId).FirstOrDefault();

        // ───────────────────────────── завантаження ─────────────────────────────

        private static readonly HttpClient Http = CreateClient();
        private static string CachePath(string regionId) => Path.Combine(Settings.DataDir, "uz-" + PageIdOf(regionId) + ".html");

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Lumos/0.6.1");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("uk,en;q=0.5");
            return client;
        }

        /// <summary>Завантажити і розібрати сторінку області. Без інтернету бере останню збережену.</summary>
        public static async Task<RegionData?> FetchAsync(string regionId, RegionData? current)
        {
            try
            {
                string page = await Http.GetStringAsync(PageUrl(regionId)).ConfigureAwait(false);
                RegionData data = Parse(page, regionId, StatusEngine.KyivNow());
                Directory.CreateDirectory(Settings.DataDir);
                File.WriteAllText(CachePath(regionId), page);
                return data;
            }
            catch (Exception ex)
            {
                Log.Write("UZ " + regionId + ": " + ex.Message);
            }

            if (current != null && current.Id == regionId) return current;
            try
            {
                string path = CachePath(regionId);
                if (File.Exists(path)) return Parse(File.ReadAllText(path), regionId, StatusEngine.KyivNow());
            }
            catch (Exception ex)
            {
                Log.Write("UZ cache: " + ex.Message);
            }
            return null;
        }

        // ───────────────────────────── розбір ─────────────────────────────

        private static readonly string[] Months =
            { "січня", "лютого", "березня", "квітня", "травня", "червня", "липня", "серпня", "вересня", "жовтня", "листопада", "грудня" };

        private static readonly Regex TableRx = new(@"<table.*?</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex RowRx = new(@"<tr.*?</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex CellRx = new(@"<td.*?</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex TagRx = new(@"<[^>]+>", RegexOptions.Singleline);
        private static readonly Regex SpaceRx = new(@"\s+");
        private static readonly Regex TimeRx = new(@"(\d{1,2})\s*[-:.]\s*(\d{2})");
        private static readonly Regex QueueRx = new(@"([1-6])\s*(?:\(\s*([12](?:\s*[,;]\s*[12])*)\s*\))?");
        private static readonly Regex EmergencyRx = new(
            @"(застосов\w*|діють|діятимуть|запроваджен\w*|введено|вводяться)\s+(\S+\s+){0,3}(аварійн|екстрен)",
            RegexOptions.IgnoreCase);
        private static readonly Regex DateRx = new(@"(\d{1,2})\s+(" + string.Join("|", Months) + ")", RegexOptions.IgnoreCase);

        private static string Text(string htmlPart) =>
            SpaceRx.Replace(WebUtility.HtmlDecode(TagRx.Replace(htmlPart, " ")), " ").Trim();

        public static RegionData Parse(string page, string regionId, DateTime kyivNow)
        {
            var region = new RegionData
            {
                Id = regionId,
                Name = (NameOf(regionId) ?? "Область") + " · Укрзалізниця",
            };
            foreach (string q in AllQueues) region.Queues[q] = new Dictionary<string, int[]>();

            int start = page.IndexOf("class=\"article\"", StringComparison.Ordinal);
            if (start > 0) page = page.Substring(start);

            // Аварійні: Укрзалізниця пише про них у заголовку над таблицями («…застосовуються графіки аварійних відключень»).
            int firstTable = page.IndexOf("<table", StringComparison.OrdinalIgnoreCase);
            string heading = Text(page.Substring(0, firstTable > 0 ? firstTable : Math.Min(page.Length, 20000)));
            region.Emergency = EmergencyRx.IsMatch(heading);

            int position = 0, tableIndex = 0;
            foreach (Match table in TableRx.Matches(page))
            {
                string before = Text(page.Substring(position, table.Index - position));
                position = table.Index + table.Length;

                List<string> rows = RowRx.Matches(table.Value).Select(m => m.Value).ToList();
                if (rows.Count < 2) continue;
                List<string> times = CellRx.Matches(rows[0]).Select(m => Text(m.Value)).ToList();
                List<string> queues = CellRx.Matches(rows[1]).Select(m => Text(m.Value)).ToList();
                if (times.Count < 2 || queues.Count < 2) continue;
                if (!times[0].Contains("Години", StringComparison.OrdinalIgnoreCase) ||
                    !queues[0].Contains("черг", StringComparison.OrdinalIgnoreCase)) continue;

                string dayKey = DayKey(before, kyivNow, tableIndex);
                tableIndex++;

                var off = new Dictionary<string, int[]>();
                foreach (string q in AllQueues)
                {
                    if (!region.Queues[q].TryGetValue(dayKey, out int[]? slots))
                    {
                        slots = Enumerable.Repeat(1, 48).ToArray();
                        region.Queues[q][dayKey] = slots;
                    }
                    off[q] = slots;
                }

                for (int c = 1; c < Math.Min(times.Count, queues.Count); c++)
                {
                    MatchCollection t = TimeRx.Matches(times[c]);
                    if (t.Count < 2) continue;
                    int from = int.Parse(t[0].Groups[1].Value) * 60 + int.Parse(t[0].Groups[2].Value);
                    int to = int.Parse(t[1].Groups[1].Value) * 60 + int.Parse(t[1].Groups[2].Value);
                    if (to <= from || to > 1440) continue;

                    foreach (Match q in QueueRx.Matches(queues[c]))
                    {
                        string queue = q.Groups[1].Value;
                        IEnumerable<string> subs = q.Groups[2].Success
                            ? Regex.Matches(q.Groups[2].Value, "[12]").Select(m => m.Value)
                            : new[] { "1", "2" };
                        foreach (string sub in subs)
                        {
                            if (!off.TryGetValue(queue + "." + sub, out int[]? slots)) continue;
                            for (int i = from / 30; i < (to + 29) / 30 && i < 48; i++) slots[i] = 2;
                        }
                    }
                }
            }
            return region;
        }

        /// <summary>Дата таблиці з заголовка над нею («26 жовтня…»); якщо її немає — перша таблиця сьогодні, друга завтра.</summary>
        private static string DayKey(string textBefore, DateTime kyivNow, int tableIndex)
        {
            Match? last = null;
            foreach (Match m in DateRx.Matches(textBefore)) last = m;
            if (last != null)
            {
                int day = int.Parse(last.Groups[1].Value);
                int month = Array.IndexOf(Months, last.Groups[2].Value.ToLowerInvariant()) + 1;
                int year = kyivNow.Year;
                if (month < kyivNow.Month - 6) year++;      // грудень → січень
                else if (month > kyivNow.Month + 6) year--; // січень → грудень
                if (month >= 1 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
                    return new DateTime(year, month, day).ToString("yyyy-MM-dd");
            }
            return kyivNow.Date.AddDays(Math.Min(tableIndex, 1)).ToString("yyyy-MM-dd");
        }
    }
}
