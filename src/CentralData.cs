using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Lumos
{
    /// <summary>
    /// Спільний збирач. Кожні 5 хвилин GitHub запускає Lumos у режимі «--collect»: він сам обходить сайти
    /// обленерго, Укрзалізниці й Telegram-канали і кладе результат в один файл data.json (гілка data).
    /// Програми читають цей файл замість того, щоб кожна окремо навантажувала ці сайти.
    /// Якщо файл застарів, недоступний або в ньому немає потрібної області — програма бере дані напряму, як раніше.
    /// </summary>
    internal static class CentralData
    {
        public const string Url = "https://raw.githubusercontent.com/dimasdonetsk/Lumos/data/data.json";
        // Розклад GitHub інколи запізнюється, тож вважаємо файл свіжим до 20 хвилин.
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(20);

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
        private static string? _etag;
        private static DateTime _generatedUtc;
        private static Dictionary<string, RegionData> _regions = new();
        private static Dictionary<string, EmergencyInfo?> _emergency = new();

        public static bool IsFresh => _regions.Count > 0 && DateTime.UtcNow - _generatedUtc < MaxAge;
        public static DateTime GeneratedLocal => _generatedUtc.ToLocalTime();

        public static bool TryRegion(string? id, out RegionData data)
        {
            data = null!;
            return id != null && IsFresh && _regions.TryGetValue(id, out data!);
        }

        /// <summary>true — збирач перевіряв цю область (info == null означає «аварійних немає»).</summary>
        public static bool TryEmergency(string? id, out EmergencyInfo? info)
        {
            info = null;
            return id != null && IsFresh && _emergency.TryGetValue(id, out info);
        }

        // ───────────────────────────── читання (програма) ─────────────────────────────

        public static async Task RefreshAsync()
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Url);
                if (_etag != null && _regions.Count > 0) request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
                using HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotModified) return;
                response.EnsureSuccessStatusCode();
                byte[] json = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                Load(json);
                _etag = response.Headers.ETag?.Tag;
            }
            catch (Exception ex)
            {
                Log.Write("Central: " + ex.Message);
            }
        }

        private static void Load(byte[] json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("generated", out JsonElement gen) ||
                !DateTimeOffset.TryParse(gen.GetString(), out DateTimeOffset generated)) return;

            var regions = new Dictionary<string, RegionData>();
            if (root.TryGetProperty("regions", out JsonElement regs) && regs.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty r in regs.EnumerateObject())
                {
                    var region = new RegionData
                    {
                        Id = r.Name,
                        Name = r.Value.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? r.Name : r.Name,
                        Emergency = r.Value.TryGetProperty("emergency", out JsonElement e) && e.ValueKind == JsonValueKind.True,
                        Extra = ExtraSources.IsExtra(r.Name),
                    };
                    foreach (string q in UzSource.AllQueues) region.Queues[q] = new Dictionary<string, int[]>();
                    if (r.Value.TryGetProperty("queues", out JsonElement qs) && qs.ValueKind == JsonValueKind.Object)
                        foreach (JsonProperty q in qs.EnumerateObject())
                        {
                            if (!region.Queues.TryGetValue(q.Name, out var days) || q.Value.ValueKind != JsonValueKind.Object) continue;
                            foreach (JsonProperty day in q.Value.EnumerateObject())
                                if (day.Value.GetString() is string s && s.Length == 48 && s.All(c => c == '1' || c == '2'))
                                    days[day.Name] = s.Select(c => c - '0').ToArray();
                        }
                    // Область без жодного дня не замінює прямого завантаження.
                    if (region.Queues.Values.Any(d => d.Count > 0)) regions[region.Id] = region;
                }

            var emergency = new Dictionary<string, EmergencyInfo?>();
            if (root.TryGetProperty("emergency", out JsonElement ems) && ems.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty e in ems.EnumerateObject())
                {
                    if (e.Value.ValueKind != JsonValueKind.Object) { emergency[e.Name] = null; continue; }
                    string Str(string p) => e.Value.TryGetProperty(p, out JsonElement v) ? v.GetString() ?? "" : "";
                    if (!DateTimeOffset.TryParse(Str("time"), out DateTimeOffset time)) continue;
                    emergency[e.Name] = new EmergencyInfo { Source = Str("source"), Text = Str("text"), Url = Str("url"), Time = time.LocalDateTime };
                }

            _generatedUtc = generated.UtcDateTime;
            _regions = regions;
            _emergency = emergency;
        }

        // ───────────────────────────── збирання (GitHub) ─────────────────────────────

        /// <summary>Режим «Lumos.exe --collect шлях»: обійти джерела й записати data.json. Повертає код виходу.</summary>
        public static async Task<int> CollectAsync(string outPath)
        {
            var errors = new List<string>();
            var regions = new Dictionary<string, RegionData>();

            // Укрзалізниця: усі області.
            // Сайт УЗ часто не відповідає закордонним серверам GitHub: якщо дві перші області підряд
            // не завантажились через мережу, решту не чекаємо — програми візьмуть УЗ напряму.
            int netFails = 0;
            foreach (var r in UzSource.Regions)
            {
                string id = UzSource.Prefix + r.PageId;
                if (netFails >= 2) { errors.Add(id + ": skipped (uz.gov.ua unreachable)"); continue; }
                try { regions[id] = await UzSource.FetchFreshAsync(id).ConfigureAwait(false); netFails = 0; }
                catch (Exception ex)
                {
                    if (ex is HttpRequestException || ex is TaskCanceledException) netFails++;
                    errors.Add(id + ": " + ex.Message);
                }
            }

            // Області з окремих сайтів обленерго (Волинь, Миколаїв, Кіровоград).
            foreach (var (id, data, error) in await ExtraSources.CollectAsync().ConfigureAwait(false))
            {
                if (data != null) regions[id] = data;
                if (error != null) errors.Add(id + ": " + error);
            }

            // Аварійні: для кожної області, яку програма вміє показувати.
            var emergency = new Dictionary<string, EmergencyInfo?>();
            foreach (string id in EmergencyMonitor.KnownRegionIds().Concat(UzSource.Regions.Select(r => UzSource.Prefix + r.PageId)).Distinct())
            {
                try { emergency[id] = await EmergencyMonitor.CheckAsync(id, strict: true).ConfigureAwait(false); }
                catch (Exception ex) { errors.Add("emergency " + id + ": " + ex.Message); }
            }

            foreach (string id in regions.Where(kv => !kv.Value.Queues.Values.Any(d => d.Count > 0)).Select(kv => kv.Key).ToList())
            {
                regions.Remove(id);
                errors.Add(id + ": no schedule days");
            }

            if (regions.Count == 0 && emergency.Count == 0) return 1;

            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteNumber("v", 1);
                w.WriteString("generated", DateTimeOffset.UtcNow.ToString("o"));
                w.WriteStartObject("regions");
                foreach (var (id, region) in regions.OrderBy(kv => kv.Key))
                {
                    w.WriteStartObject(id);
                    w.WriteString("name", region.Name);
                    w.WriteBoolean("emergency", region.Emergency);
                    w.WriteStartObject("queues");
                    foreach (var (queue, days) in region.Queues.OrderBy(kv => kv.Key))
                    {
                        w.WriteStartObject(queue);
                        foreach (var (day, slots) in days.OrderBy(kv => kv.Key))
                            w.WriteString(day, string.Concat(slots.Select(s => s == 1 ? '1' : '2')));
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                w.WriteStartObject("emergency");
                foreach (var (id, info) in emergency.OrderBy(kv => kv.Key))
                {
                    if (info == null) { w.WriteNull(id); continue; }
                    w.WriteStartObject(id);
                    w.WriteString("source", info.Source);
                    w.WriteString("time", new DateTimeOffset(info.Time).ToUniversalTime().ToString("o"));
                    w.WriteString("text", info.Text.Length > 600 ? info.Text.Substring(0, 600) : info.Text);
                    w.WriteString("url", info.Url);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                w.WriteStartArray("errors");
                foreach (string e in errors) w.WriteStringValue(e.Length > 300 ? e.Substring(0, 300) : e);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            File.WriteAllBytes(outPath, stream.ToArray());
            File.WriteAllText(Path.ChangeExtension(outPath, ".log"),
                $"regions={regions.Count} emergency={emergency.Count(e => e.Value != null)}/{emergency.Count} errors={errors.Count}\n" +
                string.Join("\n", errors), Encoding.UTF8);
            return 0;
        }
    }
}
