using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Lumos.Tests
{
    public class VolynImageTests
    {
        private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

        private static string Row(int[] slots) => string.Concat(slots.Select(s => s.ToString()));

        // Синтетичні картинки за макетом Волиньобленерго (виміряно на справжніх, 2026):
        // 1.1 без світла 10:00–14:00 (через межу двох сіток), 3.2 — 00:00–00:30, 6.2 — 23:00–24:00.
        [Theory]
        [InlineData("volyn.png")]
        [InlineData("volyn_shifted.png")]
        public void ReadsGrid(string file)
        {
            var groups = VolynImage.Parse(VolynImage.Decode(File.ReadAllBytes(Fixture(file))));

            Assert.Equal(12, groups.Count);
            Assert.Equal("111111111111111111112222222211111111111111111111", Row(groups["1.1"]));
            Assert.Equal("211111111111111111111111111111111111111111111111", Row(groups["3.2"]));
            Assert.Equal("111111111111111111111111111111111111111111111122", Row(groups["6.2"]));
            foreach (var (queue, slots) in groups)
                if (queue is not ("1.1" or "3.2" or "6.2"))
                    Assert.All(slots, s => Assert.Equal(1, s));
        }

        [Fact]
        public void SkipsGroupWithUnknownColour()
        {
            var groups = VolynImage.Parse(VolynImage.Decode(File.ReadAllBytes(Fixture("volyn_unknown.png"))));

            Assert.Equal(11, groups.Count);
            Assert.False(groups.ContainsKey("2.1"));
        }
    }

    public class UzSourceTests
    {
        // Збережена сторінка «м. Київ» з сайту Укрзалізниці, 26.10.2026.
        [Fact]
        public void ParsesKyivPage()
        {
            string page = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "uz_kyiv.html"));
            var region = UzSource.Parse(page, "uz:649725", new DateTime(2026, 10, 26, 12, 0, 0));

            string Row(string q, string day) => string.Concat(region.Queues[q][day].Select(s => s.ToString()));

            Assert.Equal("Київ · Укрзалізниця", region.Name);
            Assert.False(region.Emergency);
            Assert.Equal("111111111111222222222221111111222222211111111111", Row("1.1", "2026-10-26"));
            Assert.Equal("111111111111111122222222222222111111122222221111", Row("3.2", "2026-10-26"));
            Assert.Equal("111111111111222222222211111111111111222222211111", Row("1.1", "2026-10-27"));
            Assert.Equal("111111111111111111111111111111112222111111111111", Row("6.2", "2026-10-27"));
        }
    }
}

namespace Lumos.Tests
{
    public class EmergencyRulesTests
    {
        [Theory]
        // Справжній пост Житомиробленерго, жовтень 2026.
        [InlineData("Внаслідок ворожої атаки на Житомирщині аварійні знеструмлення. Графіки погодинних відключень електроенергії наразі не діють.", true)]
        [InlineData("У Житомирській області застосовуються аварійні відключення.", true)]
        [InlineData("За командою НЕК «Укренерго» застосовано ГАВ в Житомирській області", true)]
        [InlineData("Графік надано за умови відсутності аварійних відключень або планових робіт", false)]
        [InlineData("Нагадуємо: у разі аварійних відключень звертайтесь до кол-центру", false)]
        [InlineData("Завтра графіки погодинних відключень не діятимуть", false)]
        [InlineData("За командою НЕК «Укренерго» на Житомирщині діють графіки погодинних відключень.", false)]
        public void RecognisesEmergencyPosts(string text, bool expected) =>
            Assert.Equal(expected, EmergencyMonitor.IsEmergency(text));

        [Theory]
        // Пост Житомиробленерго 3.11 о 14:50 про «3 листопада» — 4.11 вже не діє.
        [InlineData("Увага! 3 листопада 2026 запроваджено аварійні відключення", "2026-11-03 14:50", "2026-11-04 00:00")]
        // Без дати — до кінця дня поста.
        [InlineData("Застосовуються аварійні відключення", "2026-11-03 09:10", "2026-11-04 00:00")]
        // Пізно ввечері — щонайменше 6 годин.
        [InlineData("Застосовуються аварійні відключення", "2026-11-03 22:30", "2026-11-04 04:30")]
        // Нічний пост про наступний день — діє весь той день.
        [InlineData("4 листопада застосовуватимуться аварійні відключення", "2026-11-03 23:40", "2026-11-05 00:00")]
        // Історична дата в тексті ігнорується.
        [InlineData("Як і 24 лютого, застосовано аварійні відключення", "2026-11-03 10:00", "2026-11-04 00:00")]
        public void EmergencyExpiresWithItsDay(string text, string posted, string until) =>
            Assert.Equal(DateTime.Parse(until), EmergencyMonitor.ValidUntil(text, DateTime.Parse(posted)));

        [Fact]
        public void PublishedScheduleIsNotEmergency()
        {
            // Пост Черкасиобленерго з графіком: раніше хибно ставав «аварійним».
            const string text = "Оновлення графіку погодинних відключень (ГПВ) на 26 жовтня Години відсутності електропостачання: " +
                "1.1 08:00 - 11:00, 14:00 - 16:00 1.2 08:00 - 10:00, 14:00 - 17:00 2.1 08:00 - 10:00 " +
                "У разі виникнення аварійної ситуації можливе застосування ГАВ.";
            Assert.True(EmergencyMonitor.IsSchedulePost(text));
            Assert.False(EmergencyMonitor.IsEmergency(text));
        }

        [Fact]
        public void RecognisesCancellation() =>
            Assert.True(EmergencyMonitor.IsCancel("Аварійні відключення скасовано, повертаємось до графіків"));

        // Пост Житомиробленерго 5.11.2026: у data.json збирача він без переносів, у свіжому парсері — з ними.
        [Theory]
        [InlineData("❗️ Увага! Аварійні відключення! \U0001F4C5 5 листопада 2026 \U0001F50C За розпорядженням Укренерго на Житомирщині " +
                    "знову запроваджено аварійні відключення електроенергії (ГАВ) Нагадаємо: ГАВ діє до окремого розпорядження НЕК «Укренерго». " +
                    "\U0001F4CC З метою безпеки заздалегідь зарядіть пристрої.")]
        [InlineData("❗️ Увага! Аварійні відключення!\n\U0001F4C5 5 листопада 2026\n\U0001F50C За розпорядженням Укренерго на Житомирщині " +
                    "знову запроваджено аварійні відключення електроенергії (ГАВ)\nНагадаємо: ГАВ діє до окремого розпорядження НЕК «Укренерго».")]
        public void SummaryKeepsOnlyTheMainSentence(string post) =>
            Assert.Equal("На Житомирщині знову запроваджено аварійні відключення.", EmergencyMonitor.Summary(post));

        [Fact]
        public void SummaryDoesNotSplitAbbreviations() =>
            Assert.Equal("У м. Житомир застосовуються аварійні відключення.",
                EmergencyMonitor.Summary("Увага! У м. Житомир застосовуються аварійні відключення. Дякуємо за розуміння!"));

        [Fact]
        public void SummaryIsShortened()
        {
            string s = EmergencyMonitor.Summary("Внаслідок ворожої атаки на енергетичну інфраструктуру в Житомирській, Київській, " +
                "Чернігівській, Сумській, Полтавській і Харківській областях застосовуються аварійні відключення", 80);
            Assert.True(s.Length <= 80, s);
            Assert.EndsWith("…", s);
        }

        [Theory]
        [InlineData("ztoe_ua", "Житомиробленерго")]
        [InlineData("Укренерго", "Укренерго")]
        [InlineData("невідомий_канал", "невідомий_канал")]
        public void ChannelsHaveHumanNames(string source, string expected) =>
            Assert.Equal(expected, EmergencyMonitor.SourceName(source));
    }

    public class StationTitleTests
    {
        [Theory]
        // Станція в розетці пропускає струм крізь себе: вхід ≈ вихід. Раніше тут писало «навантаження немає».
        [InlineData(99, 62, 0, 55, "Навантаження 55 Вт · від мережі")]
        [InlineData(100, 3, 120, 110, "Навантаження 110 Вт · від сонця")]
        [InlineData(100, 8, 0, 0, "Заряджена, від мережі")]
        [InlineData(80, 0, 0, 0, "Очікує · навантаження немає")]
        [InlineData(60, 300, 0, 40, "Заряджається · 300 Вт")]
        [InlineData(60, 0, 0, 90, "Віддає 90 Вт")]
        public void DescribesWhatStationDoes(int soc, int ac, int solar, int output, string expected) =>
            Assert.Equal(expected, new StationStatus { Soc = soc, AcInW = ac, SolarW = solar, OutW = output }.Title);
    }
}

namespace Lumos.Tests
{
    public class EcoFlowTests
    {
        [Fact]
        public void SignsSortedParamsThenKeys()
        {
            string sign = EcoFlow.Sign("secret", "AK", "123456", "1700000000000",
                new[] { new System.Collections.Generic.KeyValuePair<string, string>("sn", "R621ZAB5XG00000") });
            using var h = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes("secret"));
            string expected = System.Convert.ToHexString(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
                "sn=R621ZAB5XG00000&accessKey=AK&nonce=123456&timestamp=1700000000000"))).ToLowerInvariant();
            Xunit.Assert.Equal(expected, sign);
        }

        [Fact]
        public void ParsesRiver2ProQuota()
        {
            var json = System.Text.Json.JsonDocument.Parse(
                "{\"pd.soc\":64,\"inv.inputWatts\":0,\"mppt.inWatts\":0,\"pd.wattsOutSum\":118," +
                "\"bms_emsStatus.dsgRemainTime\":250,\"bms_emsStatus.chgRemainTime\":5999}").RootElement;
            StationStatus st = EcoFlow.Parse(json);
            Xunit.Assert.Equal(64, st.Soc);
            Xunit.Assert.Equal(118, st.OutW);
            Xunit.Assert.Equal(250, st.DischargeMin);
            Xunit.Assert.Null(st.ChargeMin); // 5999 — «невідомо»
            Xunit.Assert.True(st.Discharging);
            Xunit.Assert.False(st.Charging);
            Xunit.Assert.False(st.GridPowered);
        }

        [Fact]
        public void ChargingFromGrid()
        {
            var json = System.Text.Json.JsonDocument.Parse(
                "{\"pd.soc\":\"40\",\"inv.inputWatts\":310,\"pd.wattsOutSum\":20,\"bms_bmsStatus.remainTime\":95}").RootElement;
            StationStatus st = EcoFlow.Parse(json);
            Xunit.Assert.True(st.Charging);
            Xunit.Assert.True(st.GridPowered);
            Xunit.Assert.Equal(95, st.ChargeMin);
        }
    }
}
