using System;
using System.Collections.Generic;
using System.Linq;
using System.Media;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Lumos
{
    /// <summary>Зв'язує дані, плашку, трей і сповіщення.</summary>
    internal sealed class AppController : IDisposable
    {
        public Settings Settings { get; private set; } = new();
        public Snapshot? Snapshot { get; private set; }

        private readonly OverlayViewModel _vm = new();
        private OverlayWindow? _window;
        private TrayController? _tray;
        // Хвилинна точність: графік іде кроком 30 хв, таймер щосекунди не потрібен.
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };
        // Звільнити пам'ять трохи згодом після закриття вікна або оновлення даних.
        private readonly DispatcherTimer _trim = new() { Interval = TimeSpan.FromSeconds(3) };
        private DateTime _lastClosed = DateTime.MinValue;
        private readonly DispatcherTimer _fetch = new() { Interval = TimeSpan.FromMinutes(5) };

        private readonly HashSet<string> _notified = new();
        private string? _seededScope;
        private bool _hintShown;
        private bool _lastEmergency;
        private System.Drawing.Point? _lastAnchor;
        private bool _fetching;

        // Аварійні відключення з Telegram (Укренерго і обленерго), перевіряються разом з оновленням графіка.
        private EmergencyInfo? _tgEmergency;
        private string? _tgRegion;
        private bool _checkingEmergency;

        // Оновлення програми: перша перевірка за 2 хв після запуску, далі раз на 6 годин.
        private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(2) };
        private bool _updating;

        // Графік Укрзалізниці (лише обраної області), оновлюється раз на 10 хв.
        private RegionData? _uzRegion;
        private DateTime _uzFetchedAt;

        public bool IsUzProvider => UzSource.IsUz(Settings.RegionId);
        public RegionData? CurrentRegion => IsUzProvider
            ? (_uzRegion?.Id == Settings.RegionId ? _uzRegion : null)
            : Snapshot?.Find(Settings.RegionId);

        public EmergencyInfo? TelegramEmergency => _tgEmergency;
        public bool IsManualOff => Settings.ManualOffSince != null;

        public void Start()
        {
            Settings = Settings.Load();
            Theme.Apply(Settings.Theme == "Light");
            AutostartHelper.Apply(Settings.Autostart);

            _vm.QueuePicked += SetQueue;
            _vm.ManualToggled += ToggleManualOff;
            _vm.OpenUrl += OpenUrl;
            _tray = new TrayController(this);

            _tick.Tick += (_, _) => Update();
            _fetch.Tick += async (_, _) => await FetchAsync();
            _trim.Tick += (_, _) =>
            {
                _trim.Stop();
                if (_window == null) MemoryHelper.Trim();
            };
            _updateTimer.Tick += async (_, _) =>
            {
                _updateTimer.Interval = TimeSpan.FromHours(6);
                await CheckUpdatesAsync(false);
            };
            _tick.Start();
            _fetch.Start();
            if (Updater.IsInstalled) _updateTimer.Start();

            if (Program.UpdatedTo != null)
                _tray.Notify("Lumos оновлено", "Встановлено версію " + Program.UpdatedTo + ".");

            if (Program.FirstRun)
            {
                _hintShown = true; // підказку замінює вікно «Lumos встановлено»
                new WelcomeWindow().Show();
            }

            Update();
            _ = FetchAsync();
        }

        // ───────────────────────────── дані ─────────────────────────────

        private async Task FetchAsync()
        {
            if (_fetching) return;
            _fetching = true;
            try
            {
                Snapshot? snap = await DataService.FetchAsync(Snapshot);
                if (snap != null) Snapshot = snap;
                Snapshot ??= new Snapshot { FromCache = true };
                await ExtraSources.MergeAsync(Snapshot, Settings.RegionId, _forceRefresh);
                await FetchUzAsync(_forceRefresh);
                _forceRefresh = false;

                // Перший запуск: підказати, що треба обрати область.
                if (!_hintShown && Settings.RegionId == null && snap != null)
                {
                    _hintShown = true;
                    _tray?.Notify("Lumos працює", "Правий клік по іконці в треї → оберіть область і чергу.");
                }
            }
            finally
            {
                _fetching = false;
            }
            Update();
            await CheckEmergencyAsync();
            ScheduleTrim();
        }

        private bool _forceRefresh;

        public void RefreshNow()
        {
            _forceRefresh = true;
            _ = FetchAsync();
        }

        private async Task FetchUzAsync(bool force)
        {
            string? regionId = Settings.RegionId;
            if (!UzSource.IsUz(regionId) || regionId == UzSource.Prefix) return;
            bool stale = _uzRegion?.Id != regionId || DateTime.Now - _uzFetchedAt > TimeSpan.FromMinutes(9);
            if (!force && !stale) return;

            RegionData? data = await UzSource.FetchAsync(regionId!, _uzRegion);
            if (Settings.RegionId != regionId) return; // область змінили, поки качали
            if (data != null)
            {
                _uzRegion = data;
                _uzFetchedAt = DateTime.Now;
            }
        }

        private void ScheduleTrim()
        {
            _trim.Stop();
            _trim.Start();
        }

        private async Task CheckEmergencyAsync()
        {
            string? regionId = Settings.RegionId;
            if (_checkingEmergency || regionId == null) return;
            _checkingEmergency = true;
            try
            {
                EmergencyInfo? info = await EmergencyMonitor.CheckAsync(regionId);
                if (Settings.RegionId != regionId) return; // область змінили, поки перевіряли
                _tgEmergency = info;
                _tgRegion = regionId;
            }
            catch (Exception ex)
            {
                Log.Write("Emergency check: " + ex.Message);
            }
            finally
            {
                _checkingEmergency = false;
            }
            Update();
        }

        private void Update()
        {
            DateTime now = StatusEngine.KyivNow();
            int nowMinutes = now.Hour * 60 + now.Minute;
            string todayKey = now.ToString("yyyy-MM-dd");
            string tomorrowKey = now.AddDays(1).ToString("yyyy-MM-dd");

            RegionData? region = CurrentRegion;
            string? queue = Settings.Queue;
            if (region != null && (queue == null || !region.Queues.ContainsKey(queue)))
                queue = null;

            int[]? today = null, tomorrow = null;
            if (region != null && queue != null && region.Queues.TryGetValue(queue, out var days))
            {
                days.TryGetValue(todayKey, out today);
                days.TryGetValue(tomorrowKey, out tomorrow);
            }

            bool needsSetup = region == null || queue == null;
            EmergencyInfo? tg = _tgRegion == Settings.RegionId ? _tgEmergency : null;
            bool emergency = (region?.Emergency ?? false) || tg != null;
            Status status = needsSetup ? new Status() : StatusEngine.Compute(today, tomorrow, emergency, nowMinutes);

            // Ручна позначка «немає світла» сильніша за графік; сама знімається через 12 годин.
            if (Settings.ManualOffSince is DateTime since && now - since > TimeSpan.FromHours(12))
            {
                Settings.ManualOffSince = null;
                Settings.Save();
            }
            if (Settings.ManualOffSince != null)
                status.Kind = StatusKind.ManualOff;

            IReadOnlyList<string> queues = region?.SortedQueues().ToList() ?? new List<string>();
            // Поки вікно закрите, малювати графік не треба: рахуємо лише статус для іконки й сповіщень.
            _vm.IsActive = _window != null;
            _vm.Apply(status, today, tomorrow, now, region?.Name ?? "", queue ?? "", queues, needsSetup,
                      Snapshot?.FetchedAt ?? default, Snapshot?.FromCache ?? false, tg, Settings.ManualOffSince);
            _tray?.Update(status.Kind, _vm.TrayText);

            if (!needsSetup)
                CheckNotifications(status, today, tomorrow, emergency, nowMinutes, todayKey, tomorrowKey, region!, queue!);

        }

        // ───────────────────────────── сповіщення ─────────────────────────────

        private void CheckNotifications(Status status, int[]? today, int[]? tomorrow, bool emergency, int now,
                                        string todayKey, string tomorrowKey, RegionData region, string queue)
        {
            string scope = region.Id + "/" + queue + "/";

            // На старті або після зміни області/черги нічого не показуємо, лише запам'ятовуємо поточний стан.
            if (_seededScope != scope)
            {
                _seededScope = scope;
                _lastEmergency = emergency;
                if (tomorrow != null) _notified.Add("tomorrow:" + scope + tomorrowKey);
                return;
            }

            if (emergency && !_lastEmergency && Settings.Notify)
            {
                EmergencyInfo? info = _tgRegion == region.Id ? _tgEmergency : null;
                string text = info != null
                    ? info.Source + ": " + (info.Text.Length > 180 ? info.Text.Substring(0, 180) + "…" : info.Text)
                    : region.Name + ": графік зараз не діє, світло можуть вимкнути будь-коли.";
                Send("Аварійні відключення", text);
            }
            _lastEmergency = emergency;

            if (Settings.NotifyNewSchedule && tomorrow != null && _notified.Add("tomorrow:" + scope + tomorrowKey))
            {
                var outages = StatusEngine.Intervals(tomorrow).Where(i => i.Off)
                    .Select(i => StatusEngine.Hm(i.Start) + "–" + StatusEngine.Hm(i.End)).ToList();
                Send("Є графік на завтра", outages.Count == 0
                    ? "Черга " + queue + ": відключень не заплановано."
                    : "Черга " + queue + ", без світла: " + string.Join(", ", outages));
            }

            if (!Settings.Notify || Settings.WarnMinutes <= 0 || emergency) return;

            if (status.Kind == StatusKind.On && status.NextOutage is Interval outage)
            {
                int left = outage.Start - now;
                if (left > 0 && left <= Settings.WarnMinutes && _notified.Add("off:" + scope + todayKey + ":" + outage.Start))
                    Send("Скоро вимкнуть світло",
                         "Через " + StatusEngine.Duration(left) + ", о " + StatusEngine.Hm(outage.Start) +
                         ". Без світла до " + StatusEngine.Hm(outage.End) + ".");
            }
            else if (status.Kind == StatusKind.Off && status.NextChange is int onAt)
            {
                int left = onAt - now;
                if (left > 0 && left <= Math.Min(Settings.WarnMinutes, 15) && _notified.Add("on:" + scope + todayKey + ":" + onAt))
                    Send("Скоро увімкнуть світло", "За графіком о " + StatusEngine.Hm(onAt) + ".");
            }
        }

        private void Send(string title, string text)
        {
            _tray?.Notify(title, text);
            if (Settings.Sound) SystemSounds.Exclamation.Play();
        }

        // ───────────────────────────── плашка ─────────────────────────────

        /// <summary>Відкрити карточку з графіком над точкою на екрані (зазвичай над значком у треї).</summary>
        public void ShowSchedule(System.Drawing.Point anchorPx)
        {
            _lastAnchor = anchorPx;
            if (_window == null)
            {
                _window = new OverlayWindow(_vm);
                _window.Closed += (_, _) =>
                {
                    _window = null;
                    _vm.IsActive = false;
                    _lastClosed = DateTime.Now;
                    ScheduleTrim();
                };
            }
            _trim.Stop();
            _vm.IsActive = true;
            _vm.OpenSchedule();
            Update();
            _window.ShowAt(anchorPx);
        }

        public void ShowSchedule() => ShowSchedule(_lastAnchor ?? DefaultAnchor());

        /// <summary>Лівий клік по значку: відкрити або закрити.</summary>
        public void ToggleFromTray(System.Drawing.Point anchorPx)
        {
            if (_window != null)
            {
                _window.RequestClose();
                return;
            }
            // Клік по значку спершу закриває вікно (воно втрачає фокус), тож не відкриваємо його одразу знову.
            if ((DateTime.Now - _lastClosed).TotalMilliseconds < 400) return;
            ShowSchedule(anchorPx);
        }

        private static System.Drawing.Point DefaultAnchor()
        {
            // Правий нижній кут основного екрана, біля годинника.
            System.Drawing.Rectangle area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                                            ?? new System.Drawing.Rectangle(0, 0, 1920, 1040);
            return new System.Drawing.Point(area.Right - 120, area.Bottom - 10);
        }

        // ───────────────────────────── налаштування ─────────────────────────────

        public void SetRegion(string regionId)
        {
            Settings.RegionId = regionId;
            // Чергу не вгадуємо: якщо в новій області такої немає, людина обирає сама.
            bool queueValid = UzSource.IsUz(regionId)
                ? Settings.Queue != null && UzSource.AllQueues.Contains(Settings.Queue)
                : Snapshot?.Find(regionId) is RegionData region && Settings.Queue != null && region.Queues.ContainsKey(Settings.Queue);
            if (!queueValid) Settings.Queue = null;
            Settings.Save();
            _tgEmergency = null;
            _tgRegion = null;
            ShowSchedule();
            _ = RefreshRegionAsync();
        }

        private async Task RefreshRegionAsync()
        {
            if (Snapshot != null && ExtraSources.IsExtra(Settings.RegionId))
                await ExtraSources.MergeAsync(Snapshot, Settings.RegionId, false);
            await FetchUzAsync(true);
            Update();
            await CheckEmergencyAsync();
        }

        /// <summary>Перемкнути постачальника, зберігши ту саму область, якщо вона є в обох.</summary>
        public void SetProvider(bool uz)
        {
            if (uz == IsUzProvider) return;
            string? mapped = uz ? UzSource.FromOblenergo(Settings.RegionId)
                                : (Settings.RegionId != null ? UzSource.ToOblenergo(Settings.RegionId) : null);
            if (!uz && mapped != null && Snapshot != null && Snapshot.Find(mapped) == null) mapped = null;

            if (mapped != null)
            {
                SetRegion(mapped);
                return;
            }
            // Області немає в іншого постачальника: позначаємо вибір і просимо обрати область.
            Settings.RegionId = uz ? UzSource.Prefix : null;
            Settings.Save();
            _tgEmergency = null;
            _tgRegion = null;
            Update();
            ShowSchedule();
        }

        // ───────────────────────────── оновлення ─────────────────────────────

        public void CheckUpdatesManual() => _ = CheckUpdatesAsync(true);

        private async Task CheckUpdatesAsync(bool manual)
        {
            if (_updating) return;
            if (!Updater.IsInstalled)
            {
                if (manual)
                    _tray?.Notify("Автооновлення вимкнене",
                        "Воно працює лише у встановленій версії. Встанови Lumos через Lumos-win-Setup.exe з GitHub.");
                return;
            }

            _updating = true;
            try
            {
                var info = await Updater.CheckAsync();
                if (info == null)
                {
                    if (manual) _tray?.Notify("Оновлень немає", "У тебе остання версія " + Updater.CurrentVersion + ".");
                    return;
                }

                string version = Updater.VersionOf(info);
                if (manual) _tray?.Notify("Знайдено версію " + version, "Завантажую. Lumos перезапуститься сам.");
                await Updater.DownloadAsync(info);

                // Не перезапускатися, поки людина дивиться на графік.
                while (_window != null) await Task.Delay(TimeSpan.FromSeconds(5));

                _tray?.Dispose();
                _tray = null;
                Updater.ApplyAndRestart(info);
            }
            catch (Exception ex)
            {
                Log.Write("Update: " + ex);
                if (manual) _tray?.Notify("Не вдалося оновитися", "Спробуй пізніше. " + ex.Message);
            }
            finally
            {
                _updating = false;
            }
        }

        public void ToggleManualOff()
        {
            Settings.ManualOffSince = Settings.ManualOffSince == null ? StatusEngine.KyivNow() : null;
            Settings.Save();
            Update();
        }

        public void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("Open: " + ex.Message); }
        }

        public void SetQueue(string queue)
        {
            Settings.Queue = queue;
            Settings.Save();
            Update();
        }

        public void Change(Action<Settings> change)
        {
            string oldTheme = Settings.Theme;
            bool oldAutostart = Settings.Autostart;
            change(Settings);
            Settings.Save();

            if (Settings.Autostart != oldAutostart) AutostartHelper.Apply(Settings.Autostart);
            if (Settings.Theme != oldTheme) Theme.Apply(Settings.Theme == "Light");
            Update();
        }

        public void Exit()
        {
            Application.Current.Shutdown();
        }

        public void Dispose()
        {
            _tick.Stop();
            _fetch.Stop();
            _trim.Stop();
            _updateTimer.Stop();
            _tray?.Dispose();
            _window?.Close();
        }
    }
}
