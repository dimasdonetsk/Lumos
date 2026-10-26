using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace Lumos
{
    /// <summary>Що зараз відбувається зі станцією.</summary>
    public sealed class StationStatus
    {
        public int Soc { get; init; }
        public int AcInW { get; init; }
        public int SolarW { get; init; }
        public int OutW { get; init; }
        /// <summary>Скільки хвилин до розрядки (null — не розряджається або невідомо).</summary>
        public int? DischargeMin { get; init; }
        /// <summary>Скільки хвилин до повного заряду (null — не заряджається або невідомо).</summary>
        public int? ChargeMin { get; init; }
        public DateTime At { get; init; } = DateTime.Now;

        public int InW => AcInW + SolarW;
        public bool Charging => InW - OutW > 10 && Soc < 100;
        public bool Discharging => OutW > InW + 5;
        /// <summary>Від розетки йде струм — отже світло є (якщо станція в розетці).</summary>
        public bool GridPowered => AcInW > 5;
    }

    /// <summary>
    /// EcoFlow через хмару. Два способи входу:
    /// 1) ключі розробника (офіційний Open API, developer-eu.ecoflow.com) — запити з підписом HMAC-SHA256;
    /// 2) логін і пароль застосунку EcoFlow (неофіційно: той самий шлях, що й у мобільного застосунку, через MQTT).
    /// Поля для River 2 / River 2 Pro: pd.soc, inv.inputWatts, mppt.inWatts, pd.wattsOutSum,
    /// bms_emsStatus.dsgRemainTime, bms_bmsStatus.remainTime.
    /// </summary>
    internal static class EcoFlow
    {
        public const string HostEu = "https://api-e.ecoflow.com";
        public const string HostGlobal = "https://api.ecoflow.com";
        // EcoFlow ставить 5999 хв (~100 год), коли час невідомий або станція не розряджається.
        private const int RemainSentinel = 5999;

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        public sealed class EcoException : Exception
        {
            public EcoException(string message) : base(message) { }
        }

        public sealed record Device(string Sn, string Name, bool Online);

        // ───────────────────────────── ключі розробника ─────────────────────────────

        internal static string Sign(string secretKey, string accessKey, string nonce, string timestamp,
                                    IEnumerable<KeyValuePair<string, string>>? parameters)
        {
            string data = parameters == null ? "" :
                string.Join("&", parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));
            string message = (data.Length > 0 ? data + "&" : "") + "accessKey=" + accessKey + "&nonce=" + nonce + "&timestamp=" + timestamp;
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
        }

        private static async Task<JsonElement> SignedGetAsync(string host, string accessKey, string secretKey, string path,
                                                              Dictionary<string, string>? query)
        {
            string nonce = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture);
            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            string qs = query == null ? "" :
                "?" + string.Join("&", query.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));
            using var request = new HttpRequestMessage(HttpMethod.Get, host + path + qs);
            request.Headers.TryAddWithoutValidation("accessKey", accessKey);
            request.Headers.TryAddWithoutValidation("nonce", nonce);
            request.Headers.TryAddWithoutValidation("timestamp", timestamp);
            request.Headers.TryAddWithoutValidation("sign", Sign(secretKey, accessKey, nonce, timestamp, query));
            using HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return CheckEnvelope(body);
        }

        private static JsonElement CheckEnvelope(string body)
        {
            JsonElement root;
            try { root = JsonDocument.Parse(body).RootElement.Clone(); }
            catch { throw new EcoException("EcoFlow повернув незрозумілу відповідь"); }
            string code = root.TryGetProperty("code", out JsonElement c) ? c.ToString() : "";
            string message = root.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "";
            if (code != "0" && !message.Equals("success", StringComparison.OrdinalIgnoreCase))
                throw new EcoException(Explain(code, message));
            return root.TryGetProperty("data", out JsonElement data) ? data : default;
        }

        private static string Explain(string code, string message)
        {
            if (code == "1006") return "Цю станцію офіційний API не підтримує";
            if (message.Contains("accessKey", StringComparison.OrdinalIgnoreCase) || message.Contains("sign", StringComparison.OrdinalIgnoreCase))
                return "Ключі не підійшли. Перевір Access Key і Secret Key";
            if (message.Contains("password", StringComparison.OrdinalIgnoreCase) || message.Contains("account", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("user", StringComparison.OrdinalIgnoreCase))
                return "Невірний логін або пароль EcoFlow";
            return (message.Length > 0 ? message : "Помилка EcoFlow") + (code.Length > 0 ? " (код " + code + ")" : "");
        }

        /// <summary>Перевірити ключі й отримати список станцій. Пробує європейський і світовий сервер.</summary>
        public static async Task<(string Host, List<Device> Devices)> ListDevicesAsync(string accessKey, string secretKey)
        {
            EcoException? last = null;
            foreach (string host in new[] { HostEu, HostGlobal })
            {
                try
                {
                    JsonElement data = await SignedGetAsync(host, accessKey, secretKey, "/iot-open/sign/device/list", null).ConfigureAwait(false);
                    var list = new List<Device>();
                    if (data.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement d in data.EnumerateArray())
                        {
                            string sn = Str(d, "sn");
                            if (sn.Length == 0) continue;
                            string name = Str(d, "deviceName");
                            if (name.Length == 0) name = Str(d, "productName");
                            list.Add(new Device(sn, name.Length > 0 ? name : sn, Str(d, "online") == "1"));
                        }
                    return (host, list);
                }
                catch (EcoException ex) { last = ex; }
            }
            throw last ?? new EcoException("Не вдалося підключитися до EcoFlow");
        }

        public static async Task<StationStatus> QuotaByKeysAsync(string host, string accessKey, string secretKey, string sn)
        {
            JsonElement data = await SignedGetAsync(host, accessKey, secretKey, "/iot-open/sign/device/quota/all",
                new Dictionary<string, string> { ["sn"] = sn }).ConfigureAwait(false);
            return Parse(data);
        }

        // ───────────────────────────── логін застосунку ─────────────────────────────

        private sealed class Session
        {
            public string Email = "", Token = "", UserId = "", MqttHost = "", MqttUser = "", MqttPassword = "";
            public int MqttPort;
        }
        private static Session? _session;

        private static async Task<Session> LoginAsync(string email, string password)
        {
            if (_session != null && _session.Email == email) return _session;

            string body = JsonSerializer.Serialize(new
            {
                email,
                password = Convert.ToBase64String(Encoding.UTF8.GetBytes(password)),
                scene = "IOT_APP",
                userType = "ECOFLOW",
            });
            using var login = new HttpRequestMessage(HttpMethod.Post, HostGlobal + "/auth/login")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            login.Headers.TryAddWithoutValidation("lang", "en_US");
            using HttpResponseMessage r1 = await Http.SendAsync(login).ConfigureAwait(false);
            JsonElement d1 = CheckEnvelope(await r1.Content.ReadAsStringAsync().ConfigureAwait(false));
            string token = Str(d1, "token");
            string userId = d1.TryGetProperty("user", out JsonElement user) ? Str(user, "userId") : "";
            if (token.Length == 0 || userId.Length == 0) throw new EcoException("Невірний логін або пароль");

            using var cert = new HttpRequestMessage(HttpMethod.Get, HostGlobal + "/iot-auth/app/certification");
            cert.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            cert.Headers.TryAddWithoutValidation("lang", "en_US");
            using HttpResponseMessage r2 = await Http.SendAsync(cert).ConfigureAwait(false);
            JsonElement d2 = CheckEnvelope(await r2.Content.ReadAsStringAsync().ConfigureAwait(false));

            _session = new Session
            {
                Email = email, Token = token, UserId = userId,
                MqttHost = Str(d2, "url"),
                MqttPort = int.TryParse(Str(d2, "port"), out int port) ? port : 8883,
                MqttUser = Str(d2, "certificateAccount"),
                MqttPassword = Str(d2, "certificatePassword"),
            };
            return _session;
        }

        public static void ForgetSession() => _session = null;

        /// <summary>Запит «latestQuotas» через MQTT: з'єднались, спитали, отримали відповідь, від'єднались.</summary>
        public static async Task<StationStatus> QuotaByLoginAsync(string email, string password, string sn)
        {
            Session s = await LoginAsync(email, password).ConfigureAwait(false);
            try
            {
                return await QuotaMqttAsync(s, sn).ConfigureAwait(false);
            }
            catch (EcoException) { throw; }
            catch
            {
                // Облікові дані MQTT могли застаріти — один раз увійти заново.
                _session = null;
                s = await LoginAsync(email, password).ConfigureAwait(false);
                return await QuotaMqttAsync(s, sn).ConfigureAwait(false);
            }
        }

        private static async Task<StationStatus> QuotaMqttAsync(Session s, string sn)
        {
            string topicBase = "/app/" + s.UserId + "/" + sn + "/thing/property";
            var factory = new MqttFactory();
            using IMqttClient client = factory.CreateMqttClient();
            var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ApplicationMessageReceivedAsync += e =>
            {
                try
                {
                    var seg = e.ApplicationMessage.PayloadSegment;
                    JsonElement root = JsonDocument.Parse(seg.AsMemory()).RootElement.Clone();
                    if (root.TryGetProperty("operateType", out JsonElement op) && op.GetString() == "latestQuotas" &&
                        root.TryGetProperty("data", out JsonElement data) && data.TryGetProperty("quotaMap", out JsonElement map))
                        reply.TrySetResult(map);
                }
                catch { }
                return Task.CompletedTask;
            };

            MqttClientOptions options = new MqttClientOptionsBuilder()
                .WithTcpServer(s.MqttHost, s.MqttPort)
                .WithCredentials(s.MqttUser, s.MqttPassword)
                .WithClientId("ANDROID_" + Guid.NewGuid().ToString("N").ToUpperInvariant() + "_" + s.UserId)
                .WithTlsOptions(o => o.UseTls())
                .WithCleanSession()
                .WithTimeout(TimeSpan.FromSeconds(15))
                .Build();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await client.ConnectAsync(options, cts.Token).ConfigureAwait(false);
            try
            {
                await client.SubscribeAsync(factory.CreateSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(topicBase + "/get_reply").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                    .Build(), cts.Token).ConfigureAwait(false);

                string request = JsonSerializer.Serialize(new
                {
                    from = "Lumos", id = RandomNumberGenerator.GetInt32(100000, 999999), version = "1.1",
                    moduleType = 0, operateType = "latestQuotas", @params = new { },
                });
                await client.PublishAsync(new MqttApplicationMessageBuilder()
                    .WithTopic(topicBase + "/get").WithPayload(request)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), cts.Token).ConfigureAwait(false);

                Task done = await Task.WhenAny(reply.Task, Task.Delay(TimeSpan.FromSeconds(12), cts.Token)).ConfigureAwait(false);
                if (done != reply.Task) throw new EcoException("Станція не відповіла. Вона ввімкнена й у Wi-Fi?");
                return Parse(reply.Task.Result);
            }
            finally
            {
                try { await client.DisconnectAsync().ConfigureAwait(false); } catch { }
            }
        }

        // ───────────────────────────── розбір ─────────────────────────────

        internal static StationStatus Parse(JsonElement q)
        {
            if (q.ValueKind != JsonValueKind.Object) throw new EcoException("Станція не передала дані");
            int? soc = Num(q, "pd.soc") ?? Num(q, "bms_bmsStatus.soc");
            if (soc == null) throw new EcoException("Станція не передала заряд. Вона в мережі?");
            int? dsg = Num(q, "bms_emsStatus.dsgRemainTime") ?? Num(q, "pd.remainTime");
            int? chg = Num(q, "bms_emsStatus.chgRemainTime") ?? Num(q, "bms_bmsStatus.remainTime");
            static int? Valid(int? m) => m is int v && v > 0 && v < RemainSentinel ? v : null;
            return new StationStatus
            {
                Soc = Math.Clamp(soc.Value, 0, 100),
                AcInW = Math.Max(0, Num(q, "inv.inputWatts") ?? 0),
                SolarW = Math.Max(0, Num(q, "mppt.inWatts") ?? 0),
                OutW = Math.Max(0, Num(q, "pd.wattsOutSum") ?? Num(q, "inv.outputWatts") ?? 0),
                DischargeMin = Valid(dsg),
                ChargeMin = Valid(chg),
                At = DateTime.Now,
            };
        }

        private static int? Num(JsonElement q, string key)
        {
            if (!q.TryGetProperty(key, out JsonElement v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return (int)Math.Round(d);
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s)) return (int)Math.Round(s);
            return null;
        }

        private static string Str(JsonElement e, string key) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out JsonElement v)
                ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
                : "";

        // ───────────────────────────── збереження секретів ─────────────────────────────

        /// <summary>Шифрує рядок ключем поточного користувача Windows (DPAPI): з іншого акаунта чи ПК не розшифрувати.</summary>
        public static string Protect(string plain) =>
            plain.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

        public static string Unprotect(string? cipher)
        {
            if (string.IsNullOrEmpty(cipher)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Lumos.EcoFlow");
    }
}
