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
