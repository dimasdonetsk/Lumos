using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Lumos
{
    /// <summary>
    /// Області, яких немає в основному агрегаторі. Щоб не навантажувати ні ПК, ні чужі сервери,
    /// у меню вони з'являються одразу, а графік качається лише для обраної області (раз на 10 хв).
    /// </summary>
    internal static class ExtraSources
    {
        private enum Kind { DtekJson, Mykolaiv, KirovohradTelegram, VolynImage }

        private sealed record Source(string Id, string Name, Kind Kind, string Url);

        private static readonly Source[] Sources =
        {
            // Дзеркала у форматі сайтів ДТЕК (fact.data: година → yes/no/first/second).
            new("vinnytska-oblast", "Вінницька область", Kind.DtekJson,
                "https://raw.githubusercontent.com/vn-progr/gpv-voe-vinnytsia/main/data/Vinnytsiaoblenerho.json"),
            new("odeska-oblast", "Одеська область", Kind.DtekJson,
                "https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/data/odesa.json"),
            new("dnipropetrovska-oblast", "Дніпропетровська область", Kind.DtekJson,
                "https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/data/dnipro.json"),
            new("kiivska-oblast", "Київська область", Kind.DtekJson,
                "https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/data/kyiv-region.json"),
            // Миколаївобленерго: відкритий JSON-API.
            new("mykolaivska-oblast", "Миколаївська область", Kind.Mykolaiv, "https://off.energy.mk.ua/api"),
            // Кіровоградобленерго: графік текстом у районних Telegram-каналах.
            new("kirovohradska-oblast", "Кіровоградська область", Kind.KirovohradTelegram,
                "https://t.me/s/SvitloKropyvnytskyiMisto"),
            // Волиньобленерго: графік лише картинкою, розпізнаємо кольори клітинок (VolynImage).
            new("volynska-oblast", "Волинська область", Kind.VolynImage, "https://api-voe-poweron.inneti.net"),
        };

        private static readonly Dictionary<string, (RegionData Data, DateTime At)> Fetched = new();
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Lumos/0.10");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("uk,en;q=0.5");
            return client;
        }

        public static bool IsExtra(string? regionId) => regionId != null && Sources.Any(s => s.Id == regionId);

        /// <summary>Додає до знімка області, яких у ньому немає; для обраної — свіжий графік.</summary>
        public static async Task MergeAsync(Snapshot snapshot, string? selectedId, bool force)
        {
            // Спершу качаємо (без змін у знімку), потім одним махом перебудовуємо список,
            // щоб інтерфейс ніколи не бачив його наполовину.
            Source? selected = Sources.FirstOrDefault(s => s.Id == selectedId &&
                                                           !snapshot.Regions.Any(r => !r.Extra && r.Id == s.Id));
            if (selected != null)
            {
                bool stale = !Fetched.TryGetValue(selected.Id, out var last) || DateTime.Now - last.At > TimeSpan.FromMinutes(9);
                if (force || stale)
                {
                    RegionData? data = await FetchAsync(selected);
                    if (data != null) Fetched[selected.Id] = (data, DateTime.Now);
                }
            }

            var regions = snapshot.Regions.Where(r => !r.Extra).ToList();
            var existing = new HashSet<string>(regions.Select(r => r.Id));
            foreach (Source source in Sources)
                if (!existing.Contains(source.Id))
                    regions.Add(Fetched.TryGetValue(source.Id, out var cached) ? cached.Data : Stub(source));
            regions.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
            snapshot.Regions = regions;
        }

        private static RegionData Stub(Source source)
        {
            var region = new RegionData { Id = source.Id, Name = source.Name, Extra = true };
            foreach (string q in UzSource.AllQueues) region.Queues[q] = new Dictionary<string, int[]>();
            return region;
        }

        private static async Task<RegionData?> FetchAsync(Source source)
        {
            try
            {
                RegionData region = Stub(source);
                switch (source.Kind)
                {
                    case Kind.DtekJson:
                        ParseDtek(await Http.GetByteArrayAsync(source.Url).ConfigureAwait(false), region);
                        break;
                    case Kind.Mykolaiv:
                        await FetchMykolaivAsync(source.Url, region).ConfigureAwait(false);
                        break;
                    case Kind.KirovohradTelegram:
                        ParseKirovohrad(await Http.GetStringAsync(source.Url).ConfigureAwait(false), region);
                        break;
                    case Kind.VolynImage:
                        await FetchVolynAsync(source.Url, region).ConfigureAwait(false);
                        break;
                }
                return region;
            }
            catch (Exception ex)
            {
                Log.Write("Extra " + source.Id + ": " + ex.Message);
                return null;
            }
        }

        private static int[] Day(RegionData region, string queue, string dayKey)
        {
            if (!region.Queues.TryGetValue(queue, out var days))
                region.Queues[queue] = days = new Dictionary<string, int[]>();
            if (!days.TryGetValue(dayKey, out int[]? slots))
                days[dayKey] = slots = Enumerable.Repeat(1, 48).ToArray();
            return slots;
        }

        private static void MarkOff(int[] slots, int fromMinutes, int toMinutes)
        {
            for (int i = Math.Max(0, fromMinutes / 30); i < Math.Min(48, (toMinutes + 29) / 30); i++) slots[i] = 2;
        }

        private static DateTime KyivDate(DateTimeOffset moment)
        {
            try
            {
                TimeZoneInfo tz = TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time");
                return TimeZoneInfo.ConvertTime(moment, tz).Date;
            }
            catch
            {
                return moment.LocalDateTime.Date;
            }
        }

        // ───────────────────────────── формат ДТЕК ─────────────────────────────

        /// <summary>
        /// {"fact": {"data": {"&lt;північ у Києві, unix&gt;": {"GPV1.1": {"1": "yes", "2": "first", ...}}}}}.
        /// Ключі 1–24 — години; no — немає всю годину, first / second — немає першу / другу половину.
        /// «maybe…» (можливо) рахуємо як «світло є», як і основне джерело.
        /// </summary>
        private static void ParseDtek(byte[] json, RegionData region)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("fact", out JsonElement fact) ||
                !fact.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
                return;

            foreach (JsonProperty day in data.EnumerateObject())
            {
                if (!long.TryParse(day.Name, out long unix) || day.Value.ValueKind != JsonValueKind.Object) continue;
                string dayKey = KyivDate(DateTimeOffset.FromUnixTimeSeconds(unix).AddHours(3)).ToString("yyyy-MM-dd");

                foreach (JsonProperty group in day.Value.EnumerateObject())
                {
                    Match m = Regex.Match(group.Name, @"^GPV([1-6])\.([12])$");
                    if (!m.Success || group.Value.ValueKind != JsonValueKind.Object) continue;
                    int[] slots = Day(region, m.Groups[1].Value + "." + m.Groups[2].Value, dayKey);

                    foreach (JsonProperty hour in group.Value.EnumerateObject())
                    {
                        if (!int.TryParse(hour.Name, out int h) || h < 1 || h > 24) continue;
                        int first = (h - 1) * 2, second = first + 1;
                        switch (hour.Value.ValueKind == JsonValueKind.String ? hour.Value.GetString() : null)
                        {
                            case "no": slots[first] = 2; slots[second] = 2; break;
                            case "first": slots[first] = 2; break;
                            case "second": slots[second] = 2; break;
                        }
                    }
                }
            }
        }

        // ───────────────────────────── Миколаївобленерго ─────────────────────────────

        private static async Task FetchMykolaivAsync(string baseUrl, RegionData region)
        {
            byte[] queuesJson = await Http.GetByteArrayAsync(baseUrl + "/outage-queue/by-type/3").ConfigureAwait(false);
            byte[] seriesJson = await Http.GetByteArrayAsync(baseUrl + "/schedule/time-series").ConfigureAwait(false);
            byte[] activeJson = await Http.GetByteArrayAsync(baseUrl + "/v2/schedule/active").ConfigureAwait(false);

            var queues = new Dictionary<long, string>();
            using (JsonDocument doc = JsonDocument.Parse(queuesJson))
                foreach (JsonElement q in doc.RootElement.EnumerateArray())
                    if (q.TryGetProperty("id", out JsonElement id) && id.TryGetInt64(out long qid) &&
                        q.TryGetProperty("name", out JsonElement name) && name.GetString() is string n &&
                        Regex.IsMatch(n, @"^[1-6]\.[12]$"))
                        queues[qid] = n;

            var spans = new Dictionary<long, (int From, int To)>();
            using (JsonDocument doc = JsonDocument.Parse(seriesJson))
                foreach (JsonElement s in doc.RootElement.EnumerateArray())
                    if (s.TryGetProperty("id", out JsonElement id) && id.TryGetInt64(out long sid) &&
                        Minutes(s, "start") is int from && Minutes(s, "end") is int to)
                        spans[sid] = (from, to == 0 ? 1440 : to);

            using (JsonDocument doc = JsonDocument.Parse(activeJson))
                foreach (JsonElement day in doc.RootElement.EnumerateArray())
                {
                    if (!day.TryGetProperty("from", out JsonElement fromEl) ||
                        !DateTimeOffset.TryParse(fromEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset start) ||
                        !day.TryGetProperty("series", out JsonElement series) || series.ValueKind != JsonValueKind.Array)
                        continue;
                    string dayKey = KyivDate(start.AddHours(1)).ToString("yyyy-MM-dd");
                    foreach (string q in queues.Values) Day(region, q, dayKey);

                    foreach (JsonElement cell in series.EnumerateArray())
                    {
                        string? type = cell.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
                        if (type != "OFF" && type != "SURE_OFF") continue;
                        if (cell.TryGetProperty("outage_queue_id", out JsonElement qEl) && qEl.TryGetInt64(out long qid) &&
                            cell.TryGetProperty("time_series_id", out JsonElement sEl) && sEl.TryGetInt64(out long sid) &&
                            queues.TryGetValue(qid, out string? queue) && spans.TryGetValue(sid, out var span))
                            MarkOff(Day(region, queue, dayKey), span.From, span.To);
                    }
                }
        }

        private static int? Minutes(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out JsonElement v) || v.GetString() is not string s) return null;
            Match m = Regex.Match(s, @"^(\d{1,2}):(\d{2})");
            return m.Success ? int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value) : null;
        }

        // ───────────────────────────── Волиньобленерго ─────────────────────────────

        // Розпізнані картинки за адресою: кожне завантаження має нове ім'я файлу,
        // тож та сама адреса — та сама картинка, качати й розпізнавати її вдруге не треба.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, int[]>> VolynDecoded = new();

        /// <summary>
        /// GET /api/options → адреси картинок «сьогодні» і «завтра»; GET /api/archive_gpv_graphs → на який день кожна
        /// (ключ «сьогодні» пізно ввечері вже показує завтрашню картинку, тож дату беремо з архіву).
        /// </summary>
        private static async Task FetchVolynAsync(string baseUrl, RegionData region)
        {
            string optionsUrl = baseUrl + "/api/options?option_key[]=pw_gpv_image_today&option_key[]=pw_gpv_image_tomorrow";
            var paths = new List<string>();
            using (var request = new HttpRequestMessage(HttpMethod.Get, optionsUrl))
            {
                request.Headers.TryAddWithoutValidation("Accept", "application/ld+json");
                using HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
                foreach (JsonElement m in Members(doc.RootElement))
                    if (m.TryGetProperty("value", out JsonElement v) && v.GetString() is string path &&
                        Regex.IsMatch(path, @"^/media/[\w/.-]+\.png$"))
                        paths.Add(path);
            }
            if (paths.Count == 0) return; // графіків зараз немає

            DateTime since = StatusEngine.KyivNow().Date.AddDays(-1);
            var versions = new Dictionary<string, (long Id, DateTime Day)>();
            using (var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/archive_gpv_graphs?dateGraph[after]=" + since.ToString("yyyy-MM-dd")))
            {
                request.Headers.TryAddWithoutValidation("Accept", "application/ld+json");
                using HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
                foreach (JsonElement m in Members(doc.RootElement))
                {
                    if (!m.TryGetProperty("id", out JsonElement idEl) || !idEl.TryGetInt64(out long id) ||
                        !m.TryGetProperty("dateGraph", out JsonElement dg) || dg.GetString() is not string dateGraph ||
                        !m.TryGetProperty("imageUrl", out JsonElement img) || img.ValueKind != JsonValueKind.Object ||
                        !img.TryGetProperty("contentUrl", out JsonElement cu) || cu.GetString() is not string contentUrl)
                        continue;
                    // dateGraph — київська північ (позначена як UTC): беремо дату як написано.
                    if (DateTime.TryParse(dateGraph.Substring(0, Math.Min(10, dateGraph.Length)), CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out DateTime day))
                        versions[contentUrl] = (id, day.Date);
                }
            }

            var latest = new Dictionary<DateTime, (string Path, long Id)>();
            foreach (string path in paths)
                if (versions.TryGetValue(path, out var ver) && (!latest.TryGetValue(ver.Day, out var cur) || cur.Id < ver.Id))
                    latest[ver.Day] = (path, ver.Id);

            foreach (var (day, (path, _)) in latest)
            {
                if (!VolynDecoded.TryGetValue(path, out var groups))
                {
                    byte[] png = await Http.GetByteArrayAsync(baseUrl + path).ConfigureAwait(false);
                    groups = await Task.Run(() => VolynImage.Parse(VolynImage.Decode(png))).ConfigureAwait(false);
                    VolynDecoded[path] = groups;
                }
                string dayKey = day.ToString("yyyy-MM-dd");
                foreach (var (queue, slots) in groups) region.Queues[queue][dayKey] = slots;
            }

            // Старі картинки пам'ятати не треба.
            foreach (string old in VolynDecoded.Keys.Where(k => !latest.Values.Any(v => v.Path == k)).ToList())
                VolynDecoded.TryRemove(old, out _);
        }

        private static IEnumerable<JsonElement> Members(JsonElement root) =>
            root.TryGetProperty("hydra:member", out JsonElement members) && members.ValueKind == JsonValueKind.Array
                ? members.EnumerateArray().Take(100).ToList()
                : new List<JsonElement>();

        // ───────────────────────────── Кіровоградобленерго ─────────────────────────────

        private static readonly Regex WrapSplit = new(@"class=""tgme_widget_message_wrap");
        private static readonly Regex TextRx = new(@"class=""tgme_widget_message_text[^""]*""[^>]*>(.*?)</div>", RegexOptions.Singleline);
        private static readonly Regex TimeRx = new(@"<time[^>]*datetime=""([^""]+)""");
        private static readonly Regex PostRx = new(@"data-post=""[^""/]+/(\d+)""");
        private static readonly Regex DayRx = new(@"Укренерго\W{0,3}\s{0,3}(\d{2})\.(\d{2})\.(\d{4})\s{1,3}(буде|внесено|застосування)", RegexOptions.IgnoreCase);
        private static readonly Regex CancelRx = new(@"застосування\s{1,3}графіка[^\n]{0,80}скасовано", RegexOptions.IgnoreCase);
        private static readonly Regex GroupLineRx = new(@"^Черга\s{1,3}([1-6])\s{0,2}\.\s{0,2}([12])\s{0,3}:\s{0,3}(.{0,300})$");
        private static readonly Regex SpanRx = new(@"(?<![\d:])(\d{1,2})(?::(\d{2}))?\s{0,3}[-–—]\s{0,3}(\d{1,2})(?::(\d{2}))?(?![\d:])");

        /// <summary>
        /// Пост «…«Укренерго» 01.07.2026 внесено зміни…» з рядками «Черга 1.2: 20-22» — графік на цей день.
        /// Черги, яких немає в пості, без відключень. Новіший пост на той самий день замінює старіший.
        /// Прогнози пропускаємо, «застосування графіка … скасовано» — день без відключень.
        /// </summary>
        private static void ParseKirovohrad(string page, RegionData region)
        {
            DateTime today = StatusEngine.KyivNow().Date;
            var newest = new Dictionary<string, int>();

            foreach (string chunk in WrapSplit.Split(page).Skip(1))
            {
                Match text = TextRx.Match(chunk), number = PostRx.Match(chunk);
                if (!text.Success || !number.Success) continue;

                string body = Regex.Replace(text.Groups[1].Value, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
                body = WebUtility.HtmlDecode(Regex.Replace(body, @"<[^>]+>", ""));
                if (body.Contains("прогноз", StringComparison.OrdinalIgnoreCase)) continue;

                Match day = DayRx.Match(body);
                if (!day.Success) continue;
                if (!DateTime.TryParseExact(day.Groups[1].Value + "." + day.Groups[2].Value + "." + day.Groups[3].Value,
                        "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)) continue;
                if (date < today || date > today.AddDays(1)) continue;

                string dayKey = date.ToString("yyyy-MM-dd");
                int postNumber = int.Parse(number.Groups[1].Value);
                if (newest.TryGetValue(dayKey, out int seen) && seen > postNumber) continue;
                newest[dayKey] = postNumber;

                // Пост — увесь графік дня: спершу все «світло є», потім відключення з рядків.
                foreach (string q in UzSource.AllQueues) region.Queues[q][dayKey] = Enumerable.Repeat(1, 48).ToArray();
                if (CancelRx.IsMatch(body)) continue;

                foreach (string rawLine in body.Split('\n'))
                {
                    Match g = GroupLineRx.Match(rawLine.Trim());
                    if (!g.Success) continue;
                    int[] slots = region.Queues[g.Groups[1].Value + "." + g.Groups[2].Value][dayKey];
                    foreach (Match s in SpanRx.Matches(g.Groups[3].Value))
                    {
                        int from = int.Parse(s.Groups[1].Value) * 60 + (s.Groups[2].Success ? int.Parse(s.Groups[2].Value) : 0);
                        int to = int.Parse(s.Groups[3].Value) * 60 + (s.Groups[4].Success ? int.Parse(s.Groups[4].Value) : 0);
                        if (to == 0) to = 1440;
                        if (from < to && to <= 1440) MarkOff(slots, from, to);
                    }
                }
            }
        }
    }
}
