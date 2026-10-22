using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Lumos
{
    public sealed class RelayCommand : ICommand
    {
        private readonly Action _run;
        public RelayCommand(Action run) { _run = run; }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _run();
    }

    public sealed class CellVm
    {
        public Brush Brush { get; init; } = Brushes.Transparent;
        public double Opacity { get; init; } = 1;
    }

    public sealed class IntervalVm
    {
        public string Range { get; init; } = "";
        public string Label { get; init; } = "";
        public string Dur { get; init; } = "";
        public Brush Chip { get; init; } = Brushes.Transparent;
        public double Opacity { get; init; } = 1;
    }

    public sealed class QueueVm
    {
        public string Id { get; init; } = "";
        public Brush Bg { get; init; } = Brushes.Transparent;
        public Brush Fg { get; init; } = Brushes.Transparent;
        public FontWeight Weight { get; init; } = FontWeights.Normal;
        public ICommand Pick { get; init; } = new RelayCommand(() => { });
    }

    /// <summary>Усе, що показує плашка. Контролер дає дані через Apply(), плашка малює.</summary>
    public sealed class OverlayViewModel : INotifyPropertyChanged
    {
        public const double CellsWidth = 362; // ширина карточки 400 мінус відступи й рамка

        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action<string>? QueuePicked;
        public event Action? LayoutChanged;

        // Вхідні дані
        private Status _status = new();
        private int[]? _today, _tomorrow;
        private DateTime _kyivNow = DateTime.Now;
        private string _regionName = "", _queue = "";
        private IReadOnlyList<string> _queues = Array.Empty<string>();
        private bool _needsSetup, _fromCache;
        private DateTime _fetchedAt;

        // Стан вигляду
        private bool _isPill;
        private bool _scheduleOpen = true;
        private bool _showTomorrow;

        /// <summary>Кнопка «закрити» на карточці.</summary>
        public event Action? CloseRequested;

        public OverlayViewModel()
        {
            ExpandCommand = new RelayCommand(() => { _isPill = false; Render(); LayoutChanged?.Invoke(); });
            CollapseCommand = new RelayCommand(() => CloseRequested?.Invoke());
            ToggleScheduleCommand = new RelayCommand(() => { _scheduleOpen = !_scheduleOpen; Render(); LayoutChanged?.Invoke(); });
            TodayCommand = new RelayCommand(() => { _showTomorrow = false; Render(); LayoutChanged?.Invoke(); });
            TomorrowCommand = new RelayCommand(() => { _showTomorrow = true; Render(); LayoutChanged?.Invoke(); });
        }

        public ICommand ExpandCommand { get; }
        public ICommand CollapseCommand { get; }
        public ICommand ToggleScheduleCommand { get; }
        public ICommand TodayCommand { get; }
        public ICommand TomorrowCommand { get; }

        // Що показувати
        public bool IsPill => _isPill;
        public bool IsCard => !_isPill;
        public bool IsScheduleOpen => _scheduleOpen && !_isPill;

        // Статус
        public Brush StatusBrush { get; private set; } = Brushes.Gray;
        public Brush TileBrush { get; private set; } = Brushes.Transparent;
        public bool IconOn { get; private set; }
        public bool IconOff { get; private set; }
        public bool IconAlert { get; private set; }
        public string Title { get; private set; } = "";
        public string Detail { get; private set; } = "";
        public string BigText { get; private set; } = "";
        public string BigLabel { get; private set; } = "";
        public bool HasBig => BigText.Length > 0;
        public string PillText { get; private set; } = "";
        public string Footer { get; private set; } = "";
        public string TrayText { get; private set; } = "Lumos";
        public bool IsEmergency { get; private set; }

        // Вкладки
        public string TodayTab { get; private set; } = "Сьогодні";
        public string TomorrowTab { get; private set; } = "Завтра";
        public Brush TodayTabBg => _showTomorrow ? Brushes.Transparent : Theme.Get("TabOn");
        public Brush TomorrowTabBg => _showTomorrow ? Theme.Get("TabOn") : Brushes.Transparent;
        public Brush TodayTabFg => _showTomorrow ? Theme.Get("Fg2") : Theme.Get("Fg");
        public Brush TomorrowTabFg => _showTomorrow ? Theme.Get("Fg") : Theme.Get("Fg2");

        // День
        public bool HasDayData { get; private set; }
        public bool NoDayData => !HasDayData;
        public string NoDayTitle { get; private set; } = "";
        public string NoDayHint { get; private set; } = "";
        public IReadOnlyList<CellVm> Cells { get; private set; } = Array.Empty<CellVm>();
        public IReadOnlyList<IntervalVm> Intervals { get; private set; } = Array.Empty<IntervalVm>();
        public bool NowVisible { get; private set; }
        public Thickness NowLineMargin { get; private set; }
        public Thickness NowLabelMargin { get; private set; }
        public string NowLabel { get; private set; } = "";

        // Черги
        public IReadOnlyList<QueueVm> Queues { get; private set; } = Array.Empty<QueueVm>();
        public bool HasQueues => Queues.Count > 0;

        public void Apply(Status status, int[]? today, int[]? tomorrow, DateTime kyivNow, string regionName,
                          string queue, IReadOnlyList<string> queues, bool needsSetup, DateTime fetchedAt, bool fromCache)
        {
            _status = status; _today = today; _tomorrow = tomorrow; _kyivNow = kyivNow;
            _regionName = regionName; _queue = queue; _queues = queues;
            _needsSetup = needsSetup; _fetchedAt = fetchedAt; _fromCache = fromCache;
            Render();
        }

        public void OpenSchedule()
        {
            _isPill = false;
            _scheduleOpen = true;
            Render();
            LayoutChanged?.Invoke();
        }

        /// <summary>Перемальовує все (наприклад, після зміни теми).</summary>
        public void Render()
        {
            int now = _kyivNow.Hour * 60 + _kyivNow.Minute;
            IsEmergency = _status.Kind == StatusKind.Emergency;
            IconOn = IconOff = IconAlert = false;

            switch (_status.Kind)
            {
                case StatusKind.Emergency:
                    StatusBrush = Theme.StatusRed; TileBrush = Theme.TileRed; IconAlert = true;
                    Title = "Аварійне відключення";
                    Detail = "Графік не діє, стежимо за оновленнями";
                    BigText = "Будь-коли"; BigLabel = "можуть вимкнути світло";
                    PillText = "Аварійне";
                    TrayText = "Аварійні відключення: графік не діє";
                    break;

                case StatusKind.Off:
                    StatusBrush = Theme.StatusYellow; TileBrush = Theme.TileYellow; IconOff = true;
                    Title = "Світла немає";
                    Detail = "Планове відключення за графіком";
                    if (_status.NextChange is int onAt)
                    {
                        BigText = StatusEngine.Clock(onAt - now);
                        BigLabel = "до увімкнення о " + StatusEngine.Hm(onAt);
                        PillText = "до " + StatusEngine.Hm(onAt);
                        TrayText = "Світла немає · увімкнуть о " + StatusEngine.Hm(onAt);
                    }
                    else
                    {
                        BigText = ""; BigLabel = "";
                        PillText = "без світла";
                        TrayText = "Світла немає";
                    }
                    break;

                case StatusKind.On:
                    StatusBrush = Theme.StatusGreen; TileBrush = Theme.TileGreen; IconOn = true;
                    Title = "Світло є";
                    Detail = _status.NextOutage is Interval o
                        ? "Наступне відключення " + StatusEngine.Hm(o.Start) + "–" + StatusEngine.Hm(o.End)
                        : "Відключень за графіком не видно";
                    if (_status.NextChange is int offAt)
                    {
                        BigText = StatusEngine.Clock(offAt - now);
                        BigLabel = "до відключення о " + StatusEngine.Hm(offAt);
                        PillText = "до " + StatusEngine.Hm(offAt);
                        TrayText = "Світло є · вимкнуть о " + StatusEngine.Hm(offAt);
                    }
                    else
                    {
                        BigText = ""; BigLabel = "";
                        PillText = "світло є";
                        TrayText = "Світло є";
                    }
                    break;

                default:
                    StatusBrush = Theme.StatusGrey; TileBrush = Theme.TileGrey; IconOff = true;
                    Title = !_needsSetup ? "Немає даних"
                          : _regionName.Length == 0 ? "Оберіть область і чергу" : "Оберіть свою чергу";
                    Detail = !_needsSetup ? "Не вдалося отримати графік, спробуємо ще"
                           : _regionName.Length == 0 ? "Правий клік по іконці в треї → Область"
                           : "Кнопки внизу або правий клік → Черга";
                    BigText = ""; BigLabel = "";
                    PillText = _needsSetup ? "налаштувати" : "—";
                    TrayText = Title;
                    break;
            }

            string updated = _fetchedAt == default ? "ще не оновлено" : "оновлено " + _fetchedAt.ToString("HH:mm");
            Footer = _needsSetup
                ? (_regionName.Length > 0 ? _regionName + " · " : "Lumos · ") + updated
                : _regionName + " · черга " + _queue + " · " + updated + (_fromCache ? " · офлайн" : "");

            RenderDay(now);
            RenderQueues();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }

        private void RenderDay(int now)
        {
            TodayTab = "Сьогодні, " + _kyivNow.ToString("dd.MM");
            TomorrowTab = "Завтра, " + _kyivNow.AddDays(1).ToString("dd.MM");

            int[]? slots = _showTomorrow ? _tomorrow : _today;
            HasDayData = slots != null;
            NowVisible = HasDayData && !_showTomorrow;

            if (slots == null)
            {
                Cells = Array.Empty<CellVm>();
                Intervals = Array.Empty<IntervalVm>();
                if (_showTomorrow)
                {
                    NoDayTitle = "Графік на завтра ще не опубліковано";
                    NoDayHint = "Обленерго зазвичай публікує його ввечері. Щойно з’явиться — надішлемо сповіщення.";
                }
                else
                {
                    NoDayTitle = !_needsSetup ? "Графіка на сьогодні немає"
                               : _regionName.Length == 0 ? "Оберіть область і чергу" : "Оберіть свою чергу";
                    NoDayHint = !_needsSetup
                        ? "Можливо, відключень не планують або джерело ще не оновилося."
                        : _regionName.Length == 0
                            ? "Правий клік по іконці в треї → Область, потім Черга."
                            : "Свою чергу можна знайти за адресою на сайті обленерго або в його Telegram-боті.";
                }
                return;
            }

            bool dimAll = IsEmergency;
            int nowIndex = now / 30;
            var cells = new List<CellVm>(48);
            for (int i = 0; i < 48; i++)
            {
                bool off = StatusEngine.IsOff(slots[i]);
                double opacity = dimAll ? 0.25 : (!_showTomorrow && i < nowIndex ? 0.38 : 1);
                cells.Add(new CellVm { Brush = off ? Theme.CellYellow : Theme.CellGreen, Opacity = opacity });
            }
            Cells = cells;

            Intervals = StatusEngine.Intervals(slots).Select(iv => new IntervalVm
            {
                Range = StatusEngine.Hm(iv.Start) + " – " + StatusEngine.Hm(iv.End),
                Label = iv.Off ? "немає світла" : "світло є",
                Dur = StatusEngine.Duration(iv.End - iv.Start),
                Chip = iv.Off ? Theme.CellYellow : Theme.CellGreen,
                Opacity = !_showTomorrow && iv.End <= now ? 0.45 : 1
            }).ToList();

            double x = now / 1440.0 * CellsWidth;
            NowLineMargin = new Thickness(x - 1, -3, 0, -3);
            NowLabelMargin = new Thickness(Math.Clamp(x - 16, 0, CellsWidth - 32), 0, 0, 2);
            NowLabel = _kyivNow.ToString("HH:mm");
        }

        private void RenderQueues()
        {
            Brush fg = Theme.Get("Fg"), fg2 = Theme.Get("Fg2"), soft = Theme.Get("Soft"), card = Theme.Get("Card");
            Queues = _queues.Select(q => new QueueVm
            {
                Id = q,
                Bg = q == _queue ? fg : soft,
                Fg = q == _queue ? card : fg2,
                Weight = q == _queue ? FontWeights.SemiBold : FontWeights.Normal,
                Pick = new RelayCommand(() => QueuePicked?.Invoke(q))
            }).ToList();
        }
    }
}
