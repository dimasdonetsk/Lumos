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

    /// <summary>Шматок смуги графіка: суцільний проміжок «світло є» або «немає».</summary>
    public sealed class SegmentVm
    {
        public double Left { get; init; }
        public double Width { get; init; }
        public Brush Brush { get; init; } = Brushes.Transparent;
        public double Opacity { get; init; } = 1;
        public CornerRadius Corners { get; init; }
        public string Tip { get; init; } = "";
    }

    /// <summary>Підпис під смугою: час зміни (з засічкою) або край доби «00» / «24».</summary>
    public sealed class TimeLabelVm
    {
        public double Left { get; init; }        // лівий край блоку підпису
        public double TickOffset { get; init; }  // де засічка відносно блоку
        public double TickHeight { get; init; }  // 0 — без засічки
        public double TextTop { get; init; }
        public string Text { get; init; } = "";
        public Brush Brush { get; init; } = Brushes.Gray;
        public TextAlignment Align { get; init; } = TextAlignment.Center;
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
        private EmergencyInfo? _emergencyInfo;
        private DateTime? _manualSince;

        // Стан вигляду
        private bool _isPill;
        private bool _scheduleOpen = true;
        private bool _showTomorrow;

        /// <summary>Кнопка «закрити» на карточці.</summary>
        public event Action? CloseRequested;
        /// <summary>Кнопка «У мене немає світла» / «Світло повернулося».</summary>
        public event Action? ManualToggled;
        /// <summary>Відкрити пост про аварійні відключення.</summary>
        public event Action<string>? OpenUrl;

        public OverlayViewModel()
        {
            ExpandCommand = new RelayCommand(() => { _isPill = false; Render(); LayoutChanged?.Invoke(); });
            CollapseCommand = new RelayCommand(() => CloseRequested?.Invoke());
            ToggleScheduleCommand = new RelayCommand(() => { _scheduleOpen = !_scheduleOpen; Render(); LayoutChanged?.Invoke(); });
            TodayCommand = new RelayCommand(() => { _showTomorrow = false; Render(); LayoutChanged?.Invoke(); });
            TomorrowCommand = new RelayCommand(() => { _showTomorrow = true; Render(); LayoutChanged?.Invoke(); });
            ToggleManualCommand = new RelayCommand(() => ManualToggled?.Invoke());
            OpenPostCommand = new RelayCommand(() => { if (_emergencyInfo?.Url.Length > 0) OpenUrl?.Invoke(_emergencyInfo.Url); });
        }

        /// <summary>Чи відкрите вікно. Коли ні, графік і кнопки не перераховуються.</summary>
        public bool IsActive { get; set; }

        public ICommand ToggleManualCommand { get; }
        public ICommand OpenPostCommand { get; }
        public bool IsManual => _manualSince != null;
        public string ManualButtonText => IsManual ? "Світло повернулося" : "У мене немає світла";
        public string EmergencyBanner { get; private set; } = "";
        public bool HasEmergencyPost => _emergencyInfo != null && _emergencyInfo.Url.Length > 0;

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
        public IReadOnlyList<SegmentVm> Segments { get; private set; } = Array.Empty<SegmentVm>();
        public IReadOnlyList<TimeLabelVm> TimeLabels { get; private set; } = Array.Empty<TimeLabelVm>();
        public double TimelineHeight { get; private set; } = 70;
        public double NowX { get; private set; }
        public double NowLabelX { get; private set; }
        public IReadOnlyList<IntervalVm> Intervals { get; private set; } = Array.Empty<IntervalVm>();
        public bool NowVisible { get; private set; }
        public string NowLabel { get; private set; } = "";

        // Черги
        public IReadOnlyList<QueueVm> Queues { get; private set; } = Array.Empty<QueueVm>();
        public bool HasQueues => Queues.Count > 0;

        public void Apply(Status status, int[]? today, int[]? tomorrow, DateTime kyivNow, string regionName,
                          string queue, IReadOnlyList<string> queues, bool needsSetup, DateTime fetchedAt, bool fromCache,
                          EmergencyInfo? emergencyInfo, DateTime? manualSince)
        {
            _emergencyInfo = emergencyInfo; _manualSince = manualSince;
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
                case StatusKind.ManualOff:
                {
                    int since = Math.Max(0, (int)(_kyivNow - _manualSince!.Value).TotalMinutes);
                    StatusBrush = Theme.StatusRed; TileBrush = Theme.TileRed; IconOff = true;
                    Title = "Світла немає";
                    Detail = "Не за графіком · позначено о " + _manualSince.Value.ToString("HH:mm");
                    BigText = StatusEngine.Clock(since); BigLabel = "без світла";
                    PillText = "без світла";
                    TrayText = "Світла немає (не за графіком)";
                    break;
                }

                case StatusKind.Emergency:
                    StatusBrush = Theme.StatusRed; TileBrush = Theme.TileRed; IconAlert = true;
                    Title = "Аварійне відключення";
                    Detail = _emergencyInfo != null
                        ? _emergencyInfo.Source + " · " + _emergencyInfo.Time.ToString("HH:mm")
                        : "Графік не діє, стежимо за оновленнями";
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

            EmergencyBanner = _emergencyInfo != null
                ? _emergencyInfo.Source + ", " + _emergencyInfo.Time.ToString("HH:mm") + ": " + Shorten(_emergencyInfo.Text, 240)
                : "Діють аварійні відключення. Графік нижче зараз не діє: світло можуть вимкнути будь-коли.";

            if (!IsActive) return;
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
                Segments = Array.Empty<SegmentVm>();
                TimeLabels = Array.Empty<TimeLabelVm>();
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

            List<Interval> intervals = StatusEngine.Intervals(slots);
            Segments = BuildSegments(intervals, _showTomorrow ? -1 : now, IsEmergency);
            TimeLabels = BuildTimeLabels(intervals, _showTomorrow ? -1 : now, out bool twoRows);
            TimelineHeight = twoRows ? 84 : 70;

            Intervals = StatusEngine.Intervals(slots).Select(iv => new IntervalVm
            {
                Range = StatusEngine.Hm(iv.Start) + " – " + StatusEngine.Hm(iv.End),
                Label = iv.Off ? "немає світла" : "світло є",
                Dur = StatusEngine.Duration(iv.End - iv.Start),
                Chip = iv.Off ? Theme.CellYellow : Theme.CellGreen,
                Opacity = !_showTomorrow && iv.End <= now ? 0.45 : 1
            }).ToList();

            NowX = Math.Round(now / 1440.0 * CellsWidth) - 1;
            NowLabelX = Math.Clamp(NowX + 1 - NowLabelWidth / 2, 0, CellsWidth - NowLabelWidth);
            NowLabel = _kyivNow.ToString("HH:mm");
        }

        public const double NowLabelWidth = 42;
        private const double SegmentGap = 3;
        private const double SegmentRadius = 6;

        /// <summary>
        /// Смуга з суцільних шматків замість 48 окремих клітинок: рівні краї, однакові проміжки,
        /// минула частина приглушена, а шматок, на який припадає «зараз», ділиться навпіл без проміжку.
        /// </summary>
        private static List<SegmentVm> BuildSegments(List<Interval> intervals, int now, bool dimAll)
        {
            var result = new List<SegmentVm>();
            double W = CellsWidth;
            for (int i = 0; i < intervals.Count; i++)
            {
                Interval iv = intervals[i];
                double x0 = Math.Round(iv.Start / 1440.0 * W) + (i > 0 ? SegmentGap / 2 : 0);
                double x1 = Math.Round(iv.End / 1440.0 * W) - (i < intervals.Count - 1 ? SegmentGap / 2 : 0);
                if (x1 - x0 < 1) continue;

                Brush brush = iv.Off ? Theme.CellYellow : Theme.CellGreen;
                string tip = StatusEngine.Hm(iv.Start) + " – " + StatusEngine.Hm(iv.End) + " · " +
                             (iv.Off ? "немає світла" : "світло є") + " · " + StatusEngine.Duration(iv.End - iv.Start);
                const double past = 0.32;

                if (dimAll)
                {
                    result.Add(Segment(x0, x1, brush, 0.25, true, true, tip));
                }
                else if (now >= 0 && iv.End <= now)
                {
                    result.Add(Segment(x0, x1, brush, past, true, true, tip));
                }
                else if (now >= 0 && iv.Start < now)
                {
                    double xn = Math.Clamp(Math.Round(now / 1440.0 * W), x0 + 1, x1 - 1);
                    result.Add(Segment(x0, xn, brush, past, true, false, tip));
                    result.Add(Segment(xn, x1, brush, 1, false, true, tip));
                }
                else
                {
                    result.Add(Segment(x0, x1, brush, 1, true, true, tip));
                }
            }
            return result;
        }

        private static SegmentVm Segment(double x0, double x1, Brush brush, double opacity, bool roundLeft, bool roundRight, string tip)
        {
            double r = Math.Min(SegmentRadius, (x1 - x0) / 2);
            return new SegmentVm
            {
                Left = x0,
                Width = x1 - x0,
                Brush = brush,
                Opacity = opacity,
                Corners = new CornerRadius(roundLeft ? r : 0, roundRight ? r : 0, roundRight ? r : 0, roundLeft ? r : 0),
                Tip = tip
            };
        }

        private const double LabelWidth = 36;

        /// <summary>
        /// Точний час кожної зміни під смугою. Минулі зміни приглушені. Якщо підписи наїжджають,
        /// наступний опускається на другий рядок; якщо й там тісно — пропускається (час є в списку нижче).
        /// </summary>
        private static List<TimeLabelVm> BuildTimeLabels(List<Interval> intervals, int now, out bool twoRows)
        {
            var labels = new List<TimeLabelVm>();
            double W = CellsWidth;
            Brush future = Theme.Get("Fg"), pastBrush = Theme.Get("Fg3");
            twoRows = false;

            // Краї доби займають місце на першому рядку.
            double[] rowRight = { 18, double.NegativeInfinity };
            double edgeRight = W - 18;

            for (int i = 0; i < intervals.Count - 1; i++)
            {
                int m = intervals[i].End;
                double x = Math.Round(m / 1440.0 * W);
                double left = Math.Clamp(x - LabelWidth / 2, 0, W - LabelWidth);
                double right = left + LabelWidth;

                int row = -1;
                if (left >= rowRight[0] + 4 && right <= edgeRight - 2) row = 0;
                else if (left >= rowRight[1] + 4) row = 1;
                if (row < 0) continue;
                rowRight[row] = right;
                if (row == 1) twoRows = true;

                labels.Add(new TimeLabelVm
                {
                    Left = left,
                    TickOffset = x - left,
                    TickHeight = row == 0 ? 4 : 18,
                    TextTop = row == 0 ? 5 : 19,
                    Text = StatusEngine.Hm(m),
                    Brush = now >= 0 && m <= now ? pastBrush : future
                });
            }

            Brush edge = pastBrush;
            labels.Add(new TimeLabelVm { Left = 0, TextTop = 5, Text = "00", Brush = edge, Align = TextAlignment.Left, TickHeight = 0 });
            labels.Add(new TimeLabelVm { Left = W - LabelWidth, TextTop = 5, Text = "24", Brush = edge, Align = TextAlignment.Right, TickHeight = 0 });
            return labels;
        }

        private static string Shorten(string text, int max) =>
            text.Length <= max ? text : text.Substring(0, max).TrimEnd() + "…";

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
