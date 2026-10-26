using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
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
        /// <summary>Область з додаткового джерела (ExtraSources), а не з основного агрегатора.</summary>
        public bool Extra;
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
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Lumos/0.4 (Windows; power outage tray)");
            return client;
        }

        private static string? _etag;

        /// <summary>
        /// Завантажує графіки. Якщо файл на сервері не змінився з минулого разу, повертає поточні дані
        /// без повторного завантаження і розбору.
        /// </summary>
        public static async Task<Snapshot?> FetchAsync(Snapshot? current)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, SourceUrl);
                if (_etag != null && current != null && !current.FromCache)
                    request.Headers.TryAddWithoutValidation("If-None-Match", _etag);

                using HttpResponseMessage response =
                    await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotModified && current != null)
                {
                    current.FetchedAt = DateTime.Now;
                    return current;
                }
                response.EnsureSuccessStatusCode();

                byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                Snapshot? snap = Parse(bytes);
                if (snap != null)
                {
                    _etag = response.Headers.ETag?.Tag;
                    snap.FetchedAt = DateTime.Now;
                    Directory.CreateDirectory(Settings.DataDir);
                    File.WriteAllBytes(CachePath, bytes);
                    return snap;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Fetch: " + ex.Message);
            }

            // Немає інтернету: лишаємо те, що вже є, або беремо з диска.
            if (current != null)
            {
                current.FromCache = true;
                return current;
            }
            return LoadCache();
        }

        private static Snapshot? LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath)) return null;
                Snapshot? snap = Parse(File.ReadAllBytes(CachePath));
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

        public static Snapshot? Parse(byte[] utf8Json)
        {
            using JsonDocument doc = JsonDocument.Parse(utf8Json);
            return Parse(doc);
        }

        private static Snapshot? Parse(JsonDocument doc)
        {
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
