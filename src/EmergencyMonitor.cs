using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Lumos
{
    public sealed class EmergencyInfo
    {
        public string Source = "";
        public DateTime Time;     // місцевий час публікації
        public string Text = "";  // текст повідомлення
        public string Url = "";   // посилання на пост
    }

    /// <summary>
    /// Аварійних відключень немає в графіках, про них пишуть окремо. Читаємо публічні Telegram-канали
    /// Укренерго і обленерго (сторінки t.me/s/...) і шукаємо свіжі повідомлення про аварійні відключення.
    /// </summary>
    internal static class EmergencyMonitor
    {
        private const string Ukrenergo = "Ukrenergo";
        // Загальний канал ДТЕК: пише про екстрені відключення в Києві, Київщині, Одещині, Дніпропетровщині.
        private const string Dtek = "dtek_ua";
        private static readonly HashSet<string> DtekRegions = new()
            { "kyiv", "kiivska-oblast", "odeska-oblast", "dnipropetrovska-oblast", "dnipro-dnem", "donetska-oblast" };
        // Аварійні оголошують раз і не повторюють, тож дивимося на добу назад:
        // діють, доки не з'явиться пост про скасування або про повернення до графіків.
        private static readonly TimeSpan Window = TimeSpan.FromHours(24);

        // Канали обленерго. Їх пости стосуються лише своєї області, тож згадка області не потрібна.
        private static readonly Dictionary<string, string[]> RegionChannels = new()
        {
            ["zhytomyrska-oblast"] = new[] { "ztoe_ua" },
            ["lvivska-oblast"] = new[] { "lvivoblenergo" },
            ["poltavska-oblast"] = new[] { "poltavaoe" },
            ["cherkaska-oblast"] = new[] { "pat_cherkasyoblenergo" },
            ["chernihivska-oblast"] = new[] { "chernigivoblenergo" },
            ["kharkivska-oblast"] = new[] { "kharkivenergy" },
            ["khmelnytska-oblast"] = new[] { "khmelnytskoblenergo" },
            ["ivano-frankivska-oblast"] = new[] { "prykarpattyaoblenergo_official" },
            ["rivnenska-oblast"] = new[] { "rivneoblenergo" },
            ["ternopilska-oblast"] = new[] { "ternopiloblenerho" },
            ["zakarpatska-oblast"] = new[] { "zakarpatenergyofficial" },
            ["zaporizka-oblast"] = new[] { "Zaporizhzhyaoblenergo_news" },
            ["chernivetska-oblast"] = new[] { "oe_cv_ua" },
            ["volynska-oblast"] = new[] { "energy_volyn_official" },
            ["mykolaivska-oblast"] = new[] { "mk_energy_ua" },
            ["sumska-oblast"] = new[] { "SumyEnergo" },
            ["dnipro-cek"] = new[] { "cek_info" },
            ["kirovohradska-oblast"] = new[] { "SvitloKropyvnytskyiMisto" },
        };

        // Як область згадується в тексті (з великої літери, щоб не ловити випадкові збіги).
        private static readonly Dictionary<string, string[]> RegionStems = new()
        {
            ["kyiv"] = new[] { "Києв", "Київ " , "Київ,", "Київ." },
            ["kiivska-oblast"] = new[] { "Київщин", "Київськ" },
            ["zhytomyrska-oblast"] = new[] { "Житомир" },
            ["lvivska-oblast"] = new[] { "Львів" },
            ["poltavska-oblast"] = new[] { "Полтав" },
            ["cherkaska-oblast"] = new[] { "Черкас" },
            ["chernihivska-oblast"] = new[] { "Чернігів" },
            ["kharkivska-oblast"] = new[] { "Харків" },
            ["khmelnytska-oblast"] = new[] { "Хмельниц" },
            ["ivano-frankivska-oblast"] = new[] { "Франків", "Прикарпат" },
            ["rivnenska-oblast"] = new[] { "Рівненськ", "Рівне", "Рівного", "Рівному" },
            ["ternopilska-oblast"] = new[] { "Тернопіл" },
            ["zakarpatska-oblast"] = new[] { "Закарпат" },
            ["zaporizka-oblast"] = new[] { "Запоріж" },
            ["chernivetska-oblast"] = new[] { "Чернівец", "Чернівц", "Буковин" },
            ["volynska-oblast"] = new[] { "Волин" },
            ["vinnytska-oblast"] = new[] { "Вінниц" },
            ["sumska-oblast"] = new[] { "Сумщин", "Сумськ", "Суми" },
            ["kirovohradska-oblast"] = new[] { "Кіровоград", "Кропивниц" },
            ["mykolaivska-oblast"] = new[] { "Миколаїв" },
            ["odeska-oblast"] = new[] { "Одес" },
            ["dnipropetrovska-oblast"] = new[] { "Дніпр" },
            ["dnipro-dnem"] = new[] { "Дніпр" },
            ["dnipro-cek"] = new[] { "Дніпр" },
        };

        private static readonly string[] Nationwide = { "усіх регіонах", "всіх регіонах", "усіх областях", "всіх областях", "по всій країні", "по всій Україні" };

        // Ознаки, що зараз діють аварійні відключення.
        private static readonly Regex EmergencyRx = new(
            @"(застосов\w*|запровадж\w*|введено|вводяться|діють|діятимуть)\s+(\S+\s+){0,2}(аварійн|екстрен)" +
            @"|(аварійн|екстрен)\w*\s+(\S+\s+){0,1}(відключен|вимкнен|знеструмлен)\w*\s+(застосов|запровадж|діють|триває)" +
            @"|знеструмлен(о|ня)\s+(через|внаслідок|після|у\s+зв)" +
            @"|(запровадж\w*|застосов\w*)\s+(\S+\s+){0,1}знеструмлен" +
            @"|без\s+світла\s+через\s+(атак|обстріл|удар)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex GavRx = new(@"\bГАВ\b", RegexOptions.CultureInvariant);

        // Ознаки, що аварійні відключення скасовані.
        // «аварійні знеструмлення», «екстрені відключення» — якщо це не умова («за відсутності аварійних…»).
        private static readonly Regex MentionRx = new(
            @"(аварійн|екстрен)\w*\s+(\S+\s+)?(відключен|знеструмлен|вимкнен)\w*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ConditionalRx = new(
            @"(відсутн|у\s+разі|в\s+разі|можлив|умов|якщо|уникнути|не\s+виключ|попередж|ризик)\w*[^.!?]{0,40}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // «Графіки погодинних відключень наразі не діють» — разом зі згадкою аварійних це однозначно.
        private static readonly Regex SchedulesOffRx = new(
            @"(графік\w*|ГПВ)\s+(\S+\s+){0,4}не\s+(діють|застосовуються|діятимуть)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // Повернення до звичайних графіків сьогодні: «діють графіки погодинних відключень», «ГПВ застосовуються».
        private static readonly Regex SchedulesBackRx = new(
            @"(?<!не\s)(діють|застосовуються|застосовано|запроваджено)\s+(\S+\s+){0,3}(графік|ГПВ)" +
            @"|(графік\w*|ГПВ)\s+(\S+\s+){0,4}(?<!не\s)(діють|застосовуються)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex CancelRx = new(
            @"скасов\w*|відмін\w*|завершен\w*|повернення\s+до\s+(графік|ГПВ)|повертаємо?ся\s+до\s+(графік|ГПВ)|повертаються\s+графік",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex WrapSplit = new(@"class=""tgme_widget_message_wrap", RegexOptions.CultureInvariant);
        private static readonly Regex TextRx = new(@"class=""tgme_widget_message_text[^""]*""[^>]*>(.*?)</div>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        private static readonly Regex TimeRx = new(@"<time[^>]*datetime=""([^""]+)""", RegexOptions.CultureInvariant);
        private static readonly Regex PostRx = new(@"data-post=""([^""]+)""", RegexOptions.CultureInvariant);
        private static readonly Regex TagRx = new(@"<br\s*/?>|<[^>]+>", RegexOptions.CultureInvariant);

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Lumos/0.4");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("uk,en;q=0.5");
            return client;
        }

        public static string? ChannelFor(string? regionId) =>
            regionId != null && !UzSource.IsUz(regionId) && RegionChannels.TryGetValue(regionId, out string[]? ch) ? ch[0] : null;

        /// <summary>Найсвіжіше повідомлення про аварійні відключення в області, або null.</summary>
        /// <summary>Усі області, для яких відомо, де шукати аварійні.</summary>
        public static IEnumerable<string> KnownRegionIds() => RegionStems.Keys.Concat(RegionChannels.Keys).Distinct();

        /// <param name="strict">Для збирача: якщо якийсь канал не відкрився — виняток, щоб не записати «аварійних немає» навмання.</param>
        public static async Task<EmergencyInfo?> CheckAsync(string regionId, bool strict = false)
        {
            var checks = new List<Task<EmergencyInfo?>>();
            // Споживачі Укрзалізниці: канал обленерго їх не стосується, лише команди Укренерго по області.
            if (UzSource.IsUz(regionId))
                regionId = UzSource.ToOblenergo(regionId) ?? regionId;
            else if (RegionChannels.TryGetValue(regionId, out string[]? channels))
                foreach (string ch in channels)
                    checks.Add(CheckChannelAsync(ch, ch, null, strict));
            if (RegionStems.TryGetValue(regionId, out string[]? stems))
            {
                checks.Add(CheckChannelAsync(Ukrenergo, "Укренерго", stems, strict));
                if (DtekRegions.Contains(regionId))
                    checks.Add(CheckChannelAsync(Dtek, "ДТЕК", stems, strict));
            }

            EmergencyInfo?[] results = await Task.WhenAll(checks).ConfigureAwait(false);
            return results.Where(r => r != null).OrderByDescending(r => r!.Time).FirstOrDefault();
        }

        // Одна сторінка каналу на 2 хвилини: збирач перевіряє Укренерго для кожної області, качати його щоразу немає сенсу.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, Task<string> Html)> Pages = new();

        private static Task<string> PageAsync(string channel)
        {
            if (Pages.TryGetValue(channel, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(2) &&
                !cached.Html.IsFaulted)
                return cached.Html;
            Task<string> task = Http.GetStringAsync("https://t.me/s/" + channel);
            Pages[channel] = (DateTime.UtcNow, task);
            return task;
        }

        private static async Task<EmergencyInfo?> CheckChannelAsync(string channel, string sourceName, string[]? mustMention, bool strict)
        {
            try
            {
                string html = await PageAsync(channel).ConfigureAwait(false);
                return Evaluate(ParsePosts(html), sourceName, mustMention, DateTime.Now);
            }
            catch (Exception ex) when (!strict)
            {
                Log.Write("Telegram " + channel + ": " + ex.Message);
                return null;
            }
        }

        private sealed class Post
        {
            public DateTime Time;
            public string Text = "";
            public string Url = "";
        }

        private static List<Post> ParsePosts(string html)
        {
            var posts = new List<Post>();
            string[] chunks = WrapSplit.Split(html);
            for (int i = 1; i < chunks.Length; i++)
            {
                string chunk = chunks[i];
                Match text = TextRx.Match(chunk);
                Match time = TimeRx.Match(chunk);
                if (!text.Success || !time.Success) continue;
                if (!DateTimeOffset.TryParse(time.Groups[1].Value, out DateTimeOffset when)) continue;

                string plain = WebUtility.HtmlDecode(TagRx.Replace(text.Groups[1].Value, " "));
                plain = Regex.Replace(plain, @"\s+", " ").Trim();
                Match post = PostRx.Match(chunk);
                posts.Add(new Post
                {
                    Time = when.LocalDateTime,
                    Text = plain,
                    Url = post.Success ? "https://t.me/" + post.Groups[1].Value : ""
                });
            }
            return posts;
        }

        private static EmergencyInfo? Evaluate(List<Post> posts, string source, string[]? mustMention, DateTime now)
        {
            // Від найновішого: перший доречний пост вирішує — діють аварійні чи вже ні.
            foreach (Post p in posts.OrderByDescending(p => p.Time))
            {
                if (now - p.Time > Window) break;
                if (mustMention != null && !Mentions(p.Text, mustMention)) continue;

                if (CancelRx.IsMatch(p.Text)) return null;
                // Опублікований погодинний графік (купа інтервалів «08:00 - 11:00») — це звичайні ГПВ, а не аварійні.
                if (IsSchedulePost(p.Text))
                {
                    if (p.Text.Contains("завтра", StringComparison.OrdinalIgnoreCase)) continue;
                    return null;
                }
                if (IsEmergency(p.Text))
                    return new EmergencyInfo { Source = source, Time = p.Time, Text = p.Text, Url = p.Url };
                // Звичайний пост про графіки на сьогодні означає, що аварійні вже позаду.
                // Пости про завтра цього не означають: аварійні можуть ще тривати.
                if (SchedulesBackRx.IsMatch(p.Text) && !p.Text.Contains("завтра", StringComparison.OrdinalIgnoreCase))
                    return null;
            }
            return null;
        }

        private static readonly Regex RangeRx = new(@"\b\d{1,2}:\d{2}\s*[-–—]\s*\d{1,2}:\d{2}\b", RegexOptions.CultureInvariant);

        internal static bool IsSchedulePost(string text) =>
            RangeRx.Matches(text).Count >= 4 &&
            !SchedulesOffRx.IsMatch(text) &&
            !EmergencyRx.IsMatch(text);

        internal static bool IsEmergency(string text)
        {
            if (EmergencyRx.IsMatch(text)) return true;
            foreach (Match m in GavRx.Matches(text))
            {
                string before = text.Substring(Math.Max(0, m.Index - 60), Math.Min(60, m.Index));
                if (!ConditionalRx.IsMatch(before)) return true;
            }
            if (SchedulesOffRx.IsMatch(text) && Regex.IsMatch(text, @"аварійн|екстрен|знеструмлен", RegexOptions.IgnoreCase))
                return true;
            foreach (Match m in MentionRx.Matches(text))
            {
                string before = text.Substring(Math.Max(0, m.Index - 60), Math.Min(60, m.Index));
                if (!ConditionalRx.IsMatch(before)) return true;
            }
            return false;
        }

        internal static bool IsCancel(string text) => CancelRx.IsMatch(text);

        private static bool Mentions(string text, string[] stems) =>
            stems.Any(s => text.Contains(s, StringComparison.Ordinal)) ||
            Nationwide.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase));
    }
}
