using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Qural
{
    // Меню трея в оформлении Qural (GlassWise): светлое или тёмное, как окно; скругления Windows 11, крупные строки,
    // значки, подзаголовки разделов, переключатели вместо галочек, скорость «Сети» цветом.
    // Это обычное меню Windows Forms — меняется только то, как оно рисуется.
    sealed class TrayMenuStyle : ToolStripRenderer
    {
        public sealed class Palette
        {
            public Color Bg, Hover, Text, Text2, Sep, Border, Accent, Green, Orange, Red, SwitchOff;
        }

        public static readonly Palette Light = new Palette
        {
            Bg = Color.FromArgb(249, 250, 252), Hover = Color.FromArgb(232, 236, 244), Text = Color.FromArgb(11, 18, 32), Text2 = Color.FromArgb(110, 116, 128),
            Sep = Color.FromArgb(226, 230, 237), Border = Color.FromArgb(214, 219, 228), Accent = Color.FromArgb(0, 122, 255), Green = Color.FromArgb(52, 199, 89),
            Orange = Color.FromArgb(230, 130, 0), Red = Color.FromArgb(255, 59, 48), SwitchOff = Color.FromArgb(206, 211, 220)
        };
        public static readonly Palette Dark = new Palette
        {
            Bg = Color.FromArgb(28, 31, 40), Hover = Color.FromArgb(44, 49, 62), Text = Color.FromArgb(240, 243, 250), Text2 = Color.FromArgb(150, 157, 172),
            Sep = Color.FromArgb(48, 53, 66), Border = Color.FromArgb(58, 64, 80), Accent = Color.FromArgb(10, 132, 255), Green = Color.FromArgb(48, 209, 88),
            Orange = Color.FromArgb(255, 159, 10), Red = Color.FromArgb(255, 69, 58), SwitchOff = Color.FromArgb(70, 76, 92)
        };

        public readonly Palette P;
        public TrayMenuStyle(bool dark) { P = dark ? Dark : Light; }

        /// Тёмная ли тема: как выбрано в окне Qural (запомнено), иначе — как у приложений Windows.
        public static bool IsDark(string saved)
        {
            if (saved == "dark") return true;
            if (saved == "light") return false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && k.GetValue("AppsUseLightTheme") is int && (int)k.GetValue("AppsUseLightTheme") == 0;
            }
            catch (Exception) { return false; }
        }

        // ── разметка пунктов (Tag): «head» — подзаголовок, «switch» — переключатель, «net|↓…|↑…» — скорость сети ──
        public static ToolStripMenuItem Header(string text) { return new ToolStripMenuItem(text) { Enabled = false, Tag = "head" }; }

        /// Пункт со скоростью справа: ↓ синим, ↑ оранжевым.
        public static ToolStripMenuItem Speed(string text, Image icon, string down, string up)
        {
            return new ToolStripMenuItem(text, icon) { Tag = "net|" + down + "|" + up, ShortcutKeyDisplayString = down + "   " + up };
        }

        public static ToolStripMenuItem Switch(string text, bool on, EventHandler click)
        {
            return new ToolStripMenuItem(text, null, click) { Tag = on ? "switch|on" : "switch|off" };
        }

        /// Подготовить меню и все подменю: этот стиль, скругления и тень.
        public void Apply(ToolStrip strip)
        {
            strip.Renderer = this;
            strip.Font = new Font("Segoe UI", 9.75f);
            strip.Padding = new Padding(4, 6, 4, 6);
            var dd = strip as ToolStripDropDown;
            if (dd != null)
            {
                dd.DropShadowEnabled = true;
                dd.Opened -= OnOpened; dd.Opened += OnOpened;
                dd.HandleCreated -= OnOpened; dd.HandleCreated += OnOpened;
            }
            // воздух сверху и снизу — первый и последний пункт не липнут к краю
            ToolStripItem first = null, last = null;
            foreach (ToolStripItem it in strip.Items) { if (!it.Available) continue; if (first == null) first = it; last = it; }
            foreach (ToolStripItem it in strip.Items)
            {
                if (it is ToolStripSeparator) { it.Margin = new Padding(0, 3, 0, 3); continue; }
                it.Padding = new Padding(2, 5, 2, 5);
                if (Equals(it.Tag, "head")) { it.Font = new Font("Segoe UI Semibold", 8.25f); it.Padding = new Padding(2, 6, 2, 1); }
                var mi = it as ToolStripMenuItem;
                if (mi != null && mi.HasDropDownItems) Apply(mi.DropDown);
            }
            if (first != null) first.Margin = new Padding(first.Margin.Left, first.Margin.Top + 3, first.Margin.Right, first.Margin.Bottom);
            if (last != null) last.Margin = new Padding(last.Margin.Left, last.Margin.Top, last.Margin.Right, last.Margin.Bottom + 3);
        }

        // Windows 11: скруглённые углы и рамка цветом темы рисует сама система (DWM) — ровно по скруглению
        static readonly bool Win11 = Build() >= 22000;
        static int Build()
        {
            try { using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")) return int.Parse((string)k.GetValue("CurrentBuildNumber")); }
            catch (Exception) { return 0; }
        }

        // статический обработчик (меню трея одно на всё время — новые стили не копят подписки); цвета — от текущего стиля
        static void OnOpened(object sender, EventArgs e)
        {
            var dd = (ToolStripDropDown)sender;
            var st = dd.Renderer as TrayMenuStyle;
            if (!Win11 || st == null) return;
            var P = st.P;
            try
            {
                int round = 2; DwmSetWindowAttribute(dd.Handle, 33 /*WINDOW_CORNER_PREFERENCE: ROUND*/, ref round, 4);
                int border = ColorTranslator.ToWin32(P.Border); DwmSetWindowAttribute(dd.Handle, 34 /*BORDER_COLOR*/, ref border, 4);
            }
            catch (Exception) { }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // ── рисование ──
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(P.Bg)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (Win11 && e.ToolStrip is ToolStripDropDown) return;   // рамку рисует Windows
            var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (var pen = new Pen(P.Border)) e.Graphics.DrawRectangle(pen, r);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }   // без серой полосы слева

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var pen = new Pen(P.Sep)) e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(5, 1, e.Item.Width - 10, e.Item.Height - 2);
            using (var path = Round(r, 6)) using (var b = new SolidBrush(P.Hover)) g.FillPath(b, path);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            string tag = e.Item.Tag as string ?? "";
            if (tag.StartsWith("switch")) return;
            // выбранный профиль: синяя точка
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle; float d = Math.Min(r.Width, r.Height) * 0.42f;
            using (var b = new SolidBrush(P.Accent)) g.FillEllipse(b, r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ArrowRectangle; float k = g.DpiX / 96f, s = 3.5f * k;
            float cx = Edge(e.Item, g) - s / 2 - k, cy = r.Y + r.Height / 2f;   // вровень с переключателями и клавишами
            using (var pen = new Pen(P.Text2, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLines(pen, new[] { new PointF(cx - s / 2, cy - s), new PointF(cx + s / 2, cy), new PointF(cx - s / 2, cy + s) });
        }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            if (e.Image == null) return;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(e.Image, e.ImageRectangle);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var g = e.Graphics; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var mi = e.Item as ToolStripMenuItem;
            string tag = e.Item.Tag as string ?? "";
            bool shortcut = mi != null && !string.IsNullOrEmpty(mi.ShortcutKeyDisplayString) && e.Text == mi.ShortcutKeyDisplayString;
            Color c = tag == "head" ? P.Text2 : !e.Item.Enabled ? P.Text2 : shortcut ? P.Text2 : P.Text;
            if (tag.StartsWith("accent")) c = P.Accent;

            // у пункта с подменю Windows не рисует правую колонку — скорость рисуем сами, вровень с горячими клавишами
            bool parent = mi != null && mi.HasDropDownItems;
            if (tag.StartsWith("net|") && parent && !shortcut)
            {
                TextRenderer.DrawText(g, e.Text, e.TextFont, e.TextRectangle, c, e.TextFormat);
                var parts = tag.Split('|');
                if (parts.Length >= 3)
                {
                    var f = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
                    int right = (int)(Edge(e.Item, g) - 16 * g.DpiX / 96f);   // левее стрелки подменю
                    int w2 = TextRenderer.MeasureText(g, parts[2], e.TextFont, Size.Empty, f).Width;
                    int w1 = TextRenderer.MeasureText(g, parts[1], e.TextFont, Size.Empty, f).Width;
                    TextRenderer.DrawText(g, parts[2], e.TextFont, new Rectangle(right - w2, e.TextRectangle.Y, w2 + 2, e.TextRectangle.Height), P.Orange, f);
                    TextRenderer.DrawText(g, parts[1], e.TextFont, new Rectangle(right - w2 - 8 - w1, e.TextRectangle.Y, w1 + 2, e.TextRectangle.Height), P.Accent, f);
                }
                return;
            }
            if (tag.StartsWith("net|") && shortcut)
            {
                // скорость «Сети» в правой колонке: ↓ синим, ↑ оранжевым (ShortcutKeyDisplayString держит ширину)
                var parts = tag.Split('|');
                if (parts.Length >= 3)
                {
                    var f = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
                    int w2 = TextRenderer.MeasureText(g, parts[2], e.TextFont, Size.Empty, f).Width;
                    int right = (int)Edge(e.Item, g);
                    TextRenderer.DrawText(g, parts[2], e.TextFont, new Rectangle(right - w2, e.TextRectangle.Y, w2 + 2, e.TextRectangle.Height), P.Orange, f);
                    int w1 = TextRenderer.MeasureText(g, parts[1], e.TextFont, Size.Empty, f).Width;
                    TextRenderer.DrawText(g, parts[1], e.TextFont, new Rectangle(right - w2 - 8 - w1, e.TextRectangle.Y, w1 + 2, e.TextRectangle.Height), P.Accent, f);
                    return;
                }
            }
            if (shortcut)
            {
                // горячие клавиши — по общей правой линии
                var f = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                int w = TextRenderer.MeasureText(g, e.Text, e.TextFont, Size.Empty, f).Width;
                int right = (int)Edge(e.Item, g);
                TextRenderer.DrawText(g, e.Text, e.TextFont, new Rectangle(right - w, e.TextRectangle.Y, w + 2, e.TextRectangle.Height), c, f);
                return;
            }
            TextRenderer.DrawText(g, e.Text, e.TextFont, e.TextRectangle, c, e.TextFormat);

            // переключатель справа
            if (tag.StartsWith("switch") && !shortcut)
            {
                bool on = tag.EndsWith("on");
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float k = g.DpiX / 96f, h = 16 * k, w = 28 * k, x = Edge(e.Item, g) - w, y = (e.Item.Height - h) / 2f;
                using (var path = Round(new RectangleF(x, y, w, h), h / 2)) using (var b = new SolidBrush(on ? P.Green : P.SwitchOff)) g.FillPath(b, path);
                float d = h - 4 * k;
                using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, on ? x + w - d - 2 * k : x + 2 * k, y + 2 * k, d, d);
            }
        }

        // общая правая линия пункта: по ней кончаются клавиши, скорость, стрелки и переключатели
        static float Edge(ToolStripItem it, Graphics g) { return it.Width - 16 * g.DpiX / 96f; }

        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        // ── значки пунктов: простые линии 16×16 (масштаб под экран), цветом подписи ──
        public Image Icon(string name)
        {
            int px;
            using (var gg = Graphics.FromHwnd(IntPtr.Zero)) px = (int)Math.Round(16 * gg.DpiX / 96f);
            var b = new Bitmap(px, px);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(px / 16f, px / 16f);
                Color c = name == "rec" ? P.Red : name == "net" ? P.Accent : name == "update" ? P.Accent : P.Text2;
                using (var pen = new Pen(c, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                using (var br = new SolidBrush(c))
                {
                    switch (name)
                    {
                        case "shot":
                            g.DrawLines(pen, new[] { new PointF(2.5f, 5.5f), new PointF(2.5f, 2.5f), new PointF(5.5f, 2.5f) });
                            g.DrawLines(pen, new[] { new PointF(10.5f, 2.5f), new PointF(13.5f, 2.5f), new PointF(13.5f, 5.5f) });
                            g.DrawLines(pen, new[] { new PointF(13.5f, 10.5f), new PointF(13.5f, 13.5f), new PointF(10.5f, 13.5f) });
                            g.DrawLines(pen, new[] { new PointF(5.5f, 13.5f), new PointF(2.5f, 13.5f), new PointF(2.5f, 10.5f) });
                            g.DrawEllipse(pen, 5.8f, 5.8f, 4.4f, 4.4f);
                            break;
                        case "rec":
                            g.DrawEllipse(pen, 2, 2, 12, 12);
                            g.FillEllipse(br, 5, 5, 6, 6);
                            break;
                        case "net":
                            g.FillEllipse(br, 1.6f, 2.8f, 2.4f, 2.4f); g.FillEllipse(br, 1.6f, 10.8f, 2.4f, 2.4f);
                            g.DrawBezier(pen, 4, 4, 7, 4, 7.5f, 8, 10, 8);
                            g.DrawBezier(pen, 4, 12, 7, 12, 7.5f, 8, 10, 8);
                            g.DrawLine(pen, 10, 8, 13.5f, 8);
                            g.DrawLines(pen, new[] { new PointF(11.8f, 6.2f), new PointF(13.6f, 8), new PointF(11.8f, 9.8f) });
                            break;
                        case "reset":
                            g.DrawArc(pen, 2.5f, 2.5f, 11, 11, 200, 290);
                            g.DrawLines(pen, new[] { new PointF(2.2f, 2.8f), new PointF(2.8f, 6f), new PointF(6, 5.4f) });
                            break;
                        case "power":
                            g.DrawArc(pen, 2.5f, 3, 11, 11, -60, 300);
                            g.DrawLine(pen, 8, 1.8f, 8, 7.5f);
                            break;
                        case "update":
                            g.DrawLine(pen, 8, 2.5f, 8, 10.5f);
                            g.DrawLines(pen, new[] { new PointF(4.5f, 7.5f), new PointF(8, 11), new PointF(11.5f, 7.5f) });
                            g.DrawLine(pen, 3, 13.5f, 13, 13.5f);
                            break;
                        case "open":
                            g.DrawRectangle(pen, 2, 3, 12, 10);
                            g.DrawLine(pen, 2, 6, 14, 6);
                            break;
                    }
                }
            }
            return b;
        }
    }
}
