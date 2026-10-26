using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using WF = System.Windows.Forms;

namespace Lumos
{
    /// <summary>Іконка в треї і чорне меню правого кліку з підменю.</summary>
    internal sealed class TrayController : IDisposable
    {
        private readonly AppController _app;
        private readonly WF.NotifyIcon _icon;
        private readonly WF.ContextMenuStrip _menu;
        private StatusKind? _iconKind;
        private System.Drawing.Point _menuAnchor;

        // Області, для яких графік зараз неможливо отримати.
        private static readonly (string Name, string Why)[] NotPublished =
        {
            ("Донецька", "ДТЕК не публікує з 12.2024"),
            ("Херсонська", "лише в особистому кабінеті"),
            ("Луганська", "не публікується"),
            ("АР Крим, Севастополь", "тимчасово окуповані"),
        };

        // Області, які додамо в наступних версіях (якщо їх ще немає в джерелі).
        private static readonly string[] Planned =
            { "Вінницька", "Волинська", "Дніпропетровська", "Кіровоградська", "Миколаївська", "Одеська", "Сумська" };

        public TrayController(AppController app)
        {
            _app = app;
            _menu = new WF.ContextMenuStrip
            {
                Renderer = new DarkMenuRenderer(),
                ShowImageMargin = false,
                ShowCheckMargin = true,
                Font = new Font("Segoe UI", 10f),
                BackColor = DarkMenuRenderer.Back,
                ForeColor = DarkMenuRenderer.Text,
            };
            _menu.Opening += (_, _) => { _menuAnchor = WF.Cursor.Position; BuildMenu(); };

            _icon = new WF.NotifyIcon
            {
                Text = "Lumos",
                ContextMenuStrip = _menu,
                Visible = true,
                Icon = MakeIcon(StatusKind.NoData),
            };
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == WF.MouseButtons.Left) _app.ToggleFromTray(WF.Cursor.Position);
            };
            _icon.BalloonTipClicked += (_, _) => _app.ShowSchedule();
        }

        public void Update(StatusKind kind, string tooltip)
        {
            // Підказка в треї обмежена 127 символами.
            _icon.Text = tooltip.Length > 120 ? tooltip.Substring(0, 120) : tooltip;
            if (_iconKind != kind)
            {
                Icon old = _icon.Icon;
                _icon.Icon = MakeIcon(kind);
                old?.Dispose();
                _iconKind = kind;
            }
        }

        public void Notify(string title, string text)
        {
            _icon.ShowBalloonTip(8000, title, text, WF.ToolTipIcon.None);
        }

        // ───────────────────────────── меню ─────────────────────────────

        private void BuildMenu()
        {
            Settings s = _app.Settings;
            Snapshot? snap = _app.Snapshot;
            RegionData? region = _app.CurrentRegion;

            DisposeItems(_menu.Items);
            _menu.Items.Clear();

            Add(_menu.Items, Item("Оновити зараз", () => _app.RefreshNow()));
            Add(_menu.Items, Item("Показати графік", () => _app.ShowSchedule(_menuAnchor)));
            Add(_menu.Items, Item(_app.IsManualOff ? "Світло повернулося" : "У мене немає світла", () => _app.ToggleManualOff()));
            EmergencyInfo? em = _app.TelegramEmergency;
            if (em != null && em.Url.Length > 0)
                Add(_menu.Items, Item("Аварійні: відкрити повідомлення " + em.Source, () => _app.OpenUrl(em.Url)));
            _menu.Items.Add(new WF.ToolStripSeparator());

            bool uz = _app.IsUzProvider;

            // Постачальник: обленерго (або ДТЕК) чи Укрзалізниця
            var providerMenu = Sub("Постачальник   ·  " + (uz ? "Укрзалізниця" : "Обленерго"));
            providerMenu.DropDownItems.Add(Radio("Обленерго / ДТЕК", !uz, () => _app.SetProvider(false)));
            providerMenu.DropDownItems.Add(Radio("Укрзалізниця", uz, () => _app.SetProvider(true)));
            providerMenu.DropDownItems.Add(new WF.ToolStripSeparator());
            providerMenu.DropDownItems.Add(Info("Укрзалізниця живить будинки біля залізниці."));
            providerMenu.DropDownItems.Add(Info("Хто твій постачальник — видно в рахунку за світло."));
            Add(_menu.Items, providerMenu);

            // Область
            string regionLabel = region != null ? region.Name.Replace(" · Укрзалізниця", "") : "";
            var regionMenu = Sub("Область" + (regionLabel.Length > 0 ? "   ·  " + regionLabel : ""));
            if (uz)
            {
                foreach (var r in UzSource.Regions)
                {
                    string id = UzSource.Prefix + r.PageId;
                    regionMenu.DropDownItems.Add(Radio(r.Name, id == s.RegionId, () => _app.SetRegion(id)));
                }
            }
            else if (snap == null || snap.Regions.Count == 0)
                regionMenu.DropDownItems.Add(Info("Дані ще завантажуються…"));
            else
                foreach (RegionData r in snap.Regions)
                {
                    RegionData captured = r;
                    regionMenu.DropDownItems.Add(Radio(r.Name, r.Id == s.RegionId, () => _app.SetRegion(captured.Id)));
                }
            Add(_menu.Items, regionMenu);

            // Черга
            var queueMenu = Sub("Черга" + (s.Queue != null && region != null ? "   ·  " + s.Queue : ""));
            if (region == null)
                queueMenu.DropDownItems.Add(Info(uz && s.RegionId != null && s.RegionId != UzSource.Prefix
                    ? "Завантажую графік…" : "Спершу оберіть область"));
            else
                foreach (string q in region.SortedQueues())
                {
                    string captured = q;
                    queueMenu.DropDownItems.Add(Radio("Черга " + q, q == s.Queue, () => _app.SetQueue(captured)));
                }
            Add(_menu.Items, queueMenu);

            // Попереджати
            var warn = Sub("Попереджати");
            foreach ((string label, int minutes) in new[] { ("За 5 хв", 5), ("За 15 хв", 15), ("За 30 хв", 30), ("За 1 год", 60), ("Не попереджати", 0) })
            {
                int m = minutes;
                warn.DropDownItems.Add(Radio(label, s.WarnMinutes == m, () => _app.Change(x => x.WarnMinutes = m)));
            }
            Add(_menu.Items, warn);
            _menu.Items.Add(new WF.ToolStripSeparator());

            Add(_menu.Items, Toggle("Запускати при вході", s.Autostart, v => _app.Change(x => x.Autostart = v)));
            Add(_menu.Items, Toggle("Звук перед відключенням", s.Sound, v => _app.Change(x => x.Sound = v)));
            Add(_menu.Items, Toggle("Сповіщення перед відключенням", s.Notify, v => _app.Change(x => x.Notify = v)));
            Add(_menu.Items, Toggle("Сповіщати про новий графік", s.NotifyNewSchedule, v => _app.Change(x => x.NotifyNewSchedule = v)));

            var theme = Sub("Тема");
            theme.DropDownItems.Add(Radio("Темна", s.Theme != "Light", () => _app.Change(x => x.Theme = "Dark")));
            theme.DropDownItems.Add(Radio("Світла", s.Theme == "Light", () => _app.Change(x => x.Theme = "Light")));
            Add(_menu.Items, theme);
            _menu.Items.Add(new WF.ToolStripSeparator());

            // Області без графіків
            var missing = Sub("Області без графіків");
            missing.DropDownItems.Add(Info("Не публікують графік:"));
            foreach ((string name, string why) in NotPublished)
                missing.DropDownItems.Add(Info("   " + name + "  —  " + why));
            var planned = Planned.Where(p => snap == null || !snap.Regions.Any(r => r.Name.StartsWith(p, StringComparison.CurrentCulture))).ToList();
            if (planned.Count > 0)
            {
                missing.DropDownItems.Add(new WF.ToolStripSeparator());
                missing.DropDownItems.Add(Info("Додамо в наступних версіях:"));
                foreach (string p in planned)
                    missing.DropDownItems.Add(Info("   " + p));
            }
            Add(_menu.Items, missing);

            var sources = Sub("Джерела даних");
            sources.DropDownItems.Add(Item("UA Power Outages Monitor (GitHub)", () => Open(DataService.SourcePage)));
            sources.DropDownItems.Add(Item("Укренерго (Telegram)", () => Open("https://t.me/s/Ukrenergo")));
            if (uz && s.RegionId != null && s.RegionId != UzSource.Prefix)
            {
                string uzUrl = UzSource.PageUrl(s.RegionId);
                sources.DropDownItems.Add(Item("Укрзалізниця: графік області", () => Open(uzUrl)));
            }
            string? channel = EmergencyMonitor.ChannelFor(s.RegionId);
            if (channel != null)
                sources.DropDownItems.Add(Item("Обленерго (Telegram, @" + channel + ")", () => Open("https://t.me/s/" + channel)));
            sources.DropDownItems.Add(Item("Відкрити папку налаштувань", () => Open(Settings.DataDir)));
            Add(_menu.Items, sources);
            _menu.Items.Add(new WF.ToolStripSeparator());

            Add(_menu.Items, Item("Перевірити оновлення", () => _app.CheckUpdatesManual()));
            _menu.Items.Add(Info("Версія " + Updater.CurrentVersion));
            _menu.Items.Add(new WF.ToolStripSeparator());

            Add(_menu.Items, Item("Вийти", () => _app.Exit()));
        }

        private static void Add(WF.ToolStripItemCollection items, WF.ToolStripItem item) => items.Add(item);

        private static WF.ToolStripMenuItem Item(string text, Action onClick)
        {
            var item = new WF.ToolStripMenuItem(text) { ForeColor = DarkMenuRenderer.Text };
            item.Click += (_, _) => onClick();
            return item;
        }

        private static WF.ToolStripMenuItem Radio(string text, bool isChecked, Action onClick)
        {
            WF.ToolStripMenuItem item = Item(text, onClick);
            item.Checked = isChecked;
            return item;
        }

        private static WF.ToolStripMenuItem Toggle(string text, bool isChecked, Action<bool> onChange)
        {
            var item = new WF.ToolStripMenuItem(text) { ForeColor = DarkMenuRenderer.Text, Checked = isChecked };
            item.Click += (_, _) => onChange(!isChecked);
            return item;
        }

        private static WF.ToolStripMenuItem Sub(string text)
        {
            var item = new WF.ToolStripMenuItem(text) { ForeColor = DarkMenuRenderer.Text };
            item.DropDown.BackColor = DarkMenuRenderer.Back;
            if (item.DropDown is WF.ToolStripDropDownMenu dd)
            {
                dd.ShowImageMargin = false;
                dd.ShowCheckMargin = true;
                dd.Renderer = new DarkMenuRenderer();
            }
            return item;
        }

        private static WF.ToolStripMenuItem Info(string text) =>
            new WF.ToolStripMenuItem(text) { Enabled = false, ForeColor = DarkMenuRenderer.Muted };

        private static void DisposeItems(WF.ToolStripItemCollection items)
        {
            foreach (WF.ToolStripItem item in items.Cast<WF.ToolStripItem>().ToList())
            {
                if (item is WF.ToolStripMenuItem mi) DisposeItems(mi.DropDownItems);
                item.Dispose();
            }
        }

        private static void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("Open: " + ex.Message); }
        }

        // ───────────────────────────── іконка ─────────────────────────────

        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

        private static readonly PointF[] Bolt =
        {
            new(13.5f, 2f), new(4.5f, 13.5f), new(11f, 13.5f), new(9.5f, 22f), new(19.5f, 10f), new(13f, 10f)
        };

        public static Icon MakeIcon(StatusKind kind)
        {
            using var bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                g.ScaleTransform(32f / 24f, 32f / 24f);

                Color green = ColorTranslator.FromHtml("#36C66A");
                Color yellow = ColorTranslator.FromHtml("#F2C14E");
                Color red = ColorTranslator.FromHtml("#F0574D");
                Color grey = ColorTranslator.FromHtml("#9E92AF");

                switch (kind)
                {
                    case StatusKind.On:
                        using (var b = new SolidBrush(green)) g.FillPolygon(b, Bolt);
                        break;

                    case StatusKind.Off:
                    case StatusKind.ManualOff:
                    case StatusKind.NoData:
                        Color c = kind == StatusKind.Off ? yellow : kind == StatusKind.ManualOff ? red : grey;
                        using (var p = new Pen(c, 2.4f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                        {
                            g.DrawPolygon(p, Bolt);
                            g.DrawLine(p, 3f, 3f, 21f, 21f);
                        }
                        break;

                    case StatusKind.Emergency:
                        var tri = new[] { new PointF(12f, 1.5f), new PointF(23f, 21.5f), new PointF(1f, 21.5f) };
                        using (var b = new SolidBrush(red)) g.FillPolygon(b, tri);
                        using (var p = new Pen(Color.White, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawLine(p, 12f, 8.5f, 12f, 14.5f);
                        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, 10.5f, 16.4f, 3f, 3f);
                        break;
                }
            }

            IntPtr handle = bmp.GetHicon();
            try { return (Icon)Icon.FromHandle(handle).Clone(); }
            finally { DestroyIcon(handle); }
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
        }
    }

    /// <summary>Чорне меню у стилі Windows 11.</summary>
    internal sealed class DarkMenuRenderer : WF.ToolStripProfessionalRenderer
    {
        public static readonly Color Back = Color.FromArgb(30, 30, 30);
        public static readonly Color Hover = Color.FromArgb(51, 51, 51);
        public static readonly Color Border = Color.FromArgb(51, 51, 51);
        public static readonly Color Text = Color.FromArgb(242, 242, 242);
        public static readonly Color Muted = Color.FromArgb(160, 160, 160);
        public static readonly Color Separator = Color.FromArgb(56, 56, 56);

        public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Muted;
            base.OnRenderItemText(e);
        }

        // Фон і підсвітку малюємо самі: інакше Windows підставляє світло-блакитну, і білого тексту не видно.
        protected override void OnRenderToolStripBackground(WF.ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(Back);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderImageMargin(WF.ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(Back);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(WF.ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Border);
            Rectangle r = e.AffectedBounds;
            e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(WF.ToolStripItemRenderEventArgs e)
        {
            var bounds = new Rectangle(Point.Empty, e.Item.Size);
            using (var back = new SolidBrush(Back))
                e.Graphics.FillRectangle(back, bounds);

            bool highlighted = e.Item.Enabled && (e.Item.Selected || (e.Item is WF.ToolStripMenuItem mi && mi.DropDown.Visible));
            if (!highlighted) return;

            var r = new Rectangle(4, 1, bounds.Width - 8, bounds.Height - 2);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRect(r, 4);
            using var hover = new SolidBrush(Hover);
            e.Graphics.FillPath(hover, path);
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnRenderArrow(WF.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(208, 208, 208);
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(WF.ToolStripItemImageRenderEventArgs e)
        {
            // Тонка біла галочка замість стандартної рамки з квадратом.
            Rectangle r = e.ImageRectangle;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Text, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float x = r.Left + r.Width / 2f - 6f, y = r.Top + r.Height / 2f;
            e.Graphics.DrawLines(pen, new[] { new PointF(x, y), new PointF(x + 4f, y + 4f), new PointF(x + 11f, y - 5f) });
        }

        protected override void OnRenderSeparator(WF.ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(Separator);
            e.Graphics.DrawLine(pen, 6, y, e.Item.Width - 6, y);
        }

        private sealed class DarkColors : WF.ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Back;
            public override Color ImageMarginGradientBegin => Back;
            public override Color ImageMarginGradientMiddle => Back;
            public override Color ImageMarginGradientEnd => Back;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientMiddle => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
            public override Color CheckBackground => Back;
            public override Color CheckSelectedBackground => Hover;
            public override Color CheckPressedBackground => Hover;
            public override Color SeparatorDark => Separator;
            public override Color SeparatorLight => Separator;
        }
    }
}
