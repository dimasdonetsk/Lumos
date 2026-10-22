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
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(15) };
        private readonly DispatcherTimer _fetch = new() { Interval = TimeSpan.FromMinutes(5) };

        private readonly HashSet<string> _notified = new();
        private string? _seededScope;
        private bool _hintShown;
        private bool _lastEmergency;
        private bool _forceShow;
        private bool _fetching;

        public void Start()
        {
            Settings = Settings.Load();
            Theme.Apply(Settings.Theme == "Light");
            AutostartHelper.Apply(Settings.Autostart);

            _vm.QueuePicked += SetQueue;
            _window = new OverlayWindow(_vm) { Position = Settings.Position };
            _tray = new TrayController(this);

            _tick.Tick += (_, _) => Update();
            _fetch.Tick += async (_, _) => await FetchAsync();
            _tick.Start();
            _fetch.Start();

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
                Snapshot? snap = await DataService.FetchAsync();
                if (snap != null) Snapshot = snap;

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
        }

        public void RefreshNow() => _ = FetchAsync();

        private void Update()
        {
            DateTime now = StatusEngine.KyivNow();
            int nowMinutes = now.Hour * 60 + now.Minute;
            string todayKey = now.ToString("yyyy-MM-dd");
            string tomorrowKey = now.AddDays(1).ToString("yyyy-MM-dd");

            RegionData? region = Snapshot?.Find(Settings.RegionId);
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
            bool emergency = region?.Emergency ?? false;
            Status status = needsSetup ? new Status() : StatusEngine.Compute(today, tomorrow, emergency, nowMinutes);

            IReadOnlyList<string> queues = region?.SortedQueues().ToList() ?? new List<string>();
            _vm.Apply(status, today, tomorrow, now, region?.Name ?? "", queue ?? "", queues, needsSetup,
                      Snapshot?.FetchedAt ?? default, Snapshot?.FromCache ?? false);
            _tray?.Update(status.Kind, _vm.TrayText);

            if (!needsSetup)
                CheckNotifications(status, today, tomorrow, emergency, nowMinutes, todayKey, tomorrowKey, region!, queue!);

            UpdateVisibility(status.Kind);
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
                Send("Аварійні відключення", region.Name + ": графік зараз не діє, світло можуть вимкнути будь-коли.");
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

        private void UpdateVisibility(StatusKind kind)
        {
            if (_window == null) return;
            bool hide = !_forceShow &&
                        (Settings.Position == "TrayOnly" || (Settings.HideWhenOn && kind == StatusKind.On));
            if (hide)
            {
                if (_window.IsVisible) _window.Hide();
            }
            else if (!_window.IsVisible)
            {
                _window.Show();
                _window.Reposition();
            }
        }

        public void ShowSchedule()
        {
            _forceShow = true;
            _vm.OpenSchedule();
            Update();
        }

        public void ToggleFromTray()
        {
            if (_window != null && _window.IsVisible && _vm.IsScheduleOpen)
            {
                _forceShow = false;
                _vm.CollapseCommand.Execute(null);
                Update();
            }
            else
            {
                ShowSchedule();
            }
        }

        // ───────────────────────────── налаштування ─────────────────────────────

        public void SetRegion(string regionId)
        {
            Settings.RegionId = regionId;
            RegionData? region = Snapshot?.Find(regionId);
            if (region != null && (Settings.Queue == null || !region.Queues.ContainsKey(Settings.Queue)))
                Settings.Queue = region.SortedQueues().FirstOrDefault();
            Settings.Save();
            ShowSchedule();
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
            if (_window != null)
            {
                _window.Position = Settings.Position;
                _window.Reposition();
            }
            _forceShow = false;
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
            _tray?.Dispose();
            _window?.Close();
        }
    }
}
