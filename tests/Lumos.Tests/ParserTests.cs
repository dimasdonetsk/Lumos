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
