using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Lumos
{
    public sealed class RegionData
    {
        public string Id = "";
        public string Name = "";
        public bool Emergency;
        /// <summary>черга → дата (yyyy-MM-dd) → 48 слотів по 30 хв (1 = світло є, інше = немає).</summary>
        public Dictionary<string, Dictionary<string, int[]>> Queues = new();

        public IEnumerable<string> SortedQueues() => Queues.Keys.OrderBy(QueueSortKey);

        public static double QueueSortKey(string q) =>
            double.TryParse(q, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.MaxValue;
    }

    public sealed class Snapshot
    {
        public List<RegionData> Regions = new();
        public DateTime FetchedAt;
        public bool FromCache;

        public RegionData? Find(string? id) => id == null ? null : Regions.FirstOrDefault(r => r.Id == id);
    }

    internal static class DataService
    {
        // Відкритий агрегатор графіків (github.com/chaichuk/UA-power-outages-monitor), оновлюється кожні 5 хв.
        public const string SourceUrl =
            "https://raw.githubusercontent.com/chaichuk/UA-power-outages-monitor/main/artifacts/last-message.json";
        public const string SourcePage = "https://github.com/chaichuk/UA-power-outages-monitor";

        private static readonly HttpClient Http = CreateClient();
        private static string CachePath => Path.Combine(Settings.DataDir, "cache.json");

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Lumos/0.1 (Windows; power outage tray)");
            return client;
        }

        public static async Task<Snapshot?> FetchAsync()
        {
            try
            {
                string json = await Http.GetStringAsync(SourceUrl).ConfigureAwait(false);
                Snapshot? snap = Parse(json);
                if (snap != null)
                {
                    snap.FetchedAt = DateTime.Now;
                    Directory.CreateDirectory(Settings.DataDir);
                    File.WriteAllText(CachePath, json);
                    return snap;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Fetch: " + ex.Message);
            }
            return LoadCache();
        }

        private static Snapshot? LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath)) return null;
                Snapshot? snap = Parse(File.ReadAllText(CachePath));
                if (snap != null)
                {
                    snap.FromCache = true;
                    snap.FetchedAt = File.GetLastWriteTime(CachePath);
                }
                return snap;
            }
            catch (Exception ex)
            {
                Log.Write("Cache: " + ex.Message);
                return null;
            }
        }

        public static Snapshot? Parse(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("regions", out JsonElement regions) || regions.ValueKind != JsonValueKind.Array)
                return null;

            var snap = new Snapshot();
            foreach (JsonElement r in regions.EnumerateArray())
            {
                var region = new RegionData
                {
                    Id = Str(r, "cpu"),
                    Name = DisplayName(Str(r, "name_ua")),
                    Emergency = r.TryGetProperty("emergency", out JsonElement em) && em.ValueKind == JsonValueKind.True
                };
                if (region.Id.Length == 0) continue;

                if (r.TryGetProperty("schedule", out JsonElement schedule) && schedule.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty queue in schedule.EnumerateObject())
                    {
                        var days = new Dictionary<string, int[]>();
                        if (queue.Value.ValueKind == JsonValueKind.Object)
                        {
                            foreach (JsonProperty day in queue.Value.EnumerateObject())
                            {
                                if (day.Value.ValueKind != JsonValueKind.Object) continue;
                                var slots = new int[48];
                                for (int i = 0; i < 48; i++) slots[i] = 1;
                                foreach (JsonProperty slot in day.Value.EnumerateObject())
                                {
                                    int index = SlotIndex(slot.Name);
                                    if (index >= 0 && index < 48 && slot.Value.ValueKind == JsonValueKind.Number &&
                                        slot.Value.TryGetInt32(out int value))
                                        slots[index] = value;
                                }
                                days[day.Name] = slots;
                            }
                        }
                        region.Queues[queue.Name] = days;
                    }
                }
                snap.Regions.Add(region);
            }
            snap.Regions.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
            return snap;
        }

        private static int SlotIndex(string hhmm)
        {
            string[] parts = hhmm.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int h) || !int.TryParse(parts[1], out int m)) return -1;
            return h * 2 + (m >= 30 ? 1 : 0);
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static string DisplayName(string name)
        {
            name = name.Trim();
            if (name.Length == 0) return name;
            if (name.Contains("обл", StringComparison.OrdinalIgnoreCase) || name.StartsWith("м.") || name == "Київ")
                return name;
            return name + " область";
        }
    }
}
