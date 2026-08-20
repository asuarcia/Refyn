// RefynUi.cs — the visual layer: palette, custom-painted controls, and the two
// windows (Settings and Compose).
//
// Same C# 5 constraint as RefynHost.cs — see the header there.
//
// Why everything here is custom-painted rather than using stock WinForms:
// stock WinForms looks like 2005 and, more importantly, has no dark mode at
// all. A Windows 11 user running a dark desktop gets a blinding white dialog.
// Painting our own controls is the only way to get a current look out of this
// toolkit — the same trade as writing a renderer instead of accepting a
// library's default output.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Refyn
{
    // ---------------------------------------------------------------- palette

    /// <summary>
    /// One colour scheme. Instances are immutable and cached; call
    /// <see cref="Resolve"/> to get the one matching the user's preference.
    /// </summary>
    internal sealed class Theme
    {
        public readonly Color Bg;
        public readonly Color Surface;
        public readonly Color SurfaceAlt;
        public readonly Color Border;
        public readonly Color Text;
        public readonly Color TextDim;
        public readonly Color Accent;
        public readonly Color AccentHover;
        public readonly Color AccentText;
        public readonly Color InputBg;
        public readonly Color Danger;
        public readonly bool IsDark;

        private static Theme light;
        private static Theme dark;

        private Theme(bool isDark, int bg, int surface, int surfaceAlt, int border, int text,
                      int textDim, int accent, int accentHover, int accentText, int inputBg, int danger)
        {
            IsDark = isDark;
            Bg = Hex(bg); Surface = Hex(surface); SurfaceAlt = Hex(surfaceAlt);
            Border = Hex(border); Text = Hex(text); TextDim = Hex(textDim);
            Accent = Hex(accent); AccentHover = Hex(accentHover); AccentText = Hex(accentText);
            InputBg = Hex(inputBg); Danger = Hex(danger);
        }

        private static Color Hex(int rgb)
        {
            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        public static Theme Light
        {
            get
            {
                if (light == null)
                {
                    light = new Theme(false, 0xFAFAFA, 0xFFFFFF, 0xF2F3F5, 0xE1E3E6, 0x1A1D23,
                                      0x6B7280, 0xC45C3C, 0xB04E30, 0xFFFFFF, 0xFFFFFF, 0xDC2626);
                }
                return light;
            }
        }

        public static Theme Dark
        {
            get
            {
                if (dark == null)
                {
                    dark = new Theme(true, 0x16181D, 0x1E2127, 0x262A31, 0x32373F, 0xE6E8EC,
                                     0x9BA3AF, 0xC45C3C, 0xD46B49, 0xFFFFFF, 0x1A1D23, 0xE5484D);
                }
                return dark;
            }
        }

        /// <param name="preference">"dark", "light", or anything else for "follow Windows".</param>
        public static Theme Resolve(string preference)
        {
            if (preference == "dark") { return Dark; }
            if (preference == "light") { return Light; }
            return SystemPrefersDark() ? Dark : Light;
        }

        /// <summary>
        /// Windows exposes the app theme as a DWORD: 0 = dark, 1 = light. A
        /// missing value means a build old enough to predate dark mode, so
        /// light is the right fallback.
        /// </summary>
        public static bool SystemPrefersDark()
        {
            try
            {
                object value = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                if (value is int) { return ((int)value) == 0; }
                return false;
            }
            catch
            {
                return false;
            }
        }

        public static Color Blend(Color a, Color b, double t)
        {
            int r = Clamp((int)(a.R + (b.R - a.R) * t));
            int g = Clamp((int)(a.G + (b.G - a.G) * t));
            int bl = Clamp((int)(a.B + (b.B - a.B) * t));
            return Color.FromArgb(r, g, bl);
        }

        private static int Clamp(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            if (d > r.Width) { d = r.Width; }
            if (d > r.Height) { d = r.Height; }

            GraphicsPath path = new GraphicsPath();
            if (d <= 0) { path.AddRectangle(r); return path; }

            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    // --------------------------------------------------------- window chrome

    /// <summary>
    /// The title bar is drawn by the OS, not by us, so a dark form still gets a
    /// white caption unless DWM is told otherwise. These two attributes are the
    /// difference between "a themed app" and "a themed app with a white bar
    /// stuck to the top of it".
    /// </summary>
    internal static class WindowChrome
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static void Apply(IntPtr hwnd, bool dark)
        {
            try
            {
                int darkFlag = dark ? 1 : 0;
                // Fails harmlessly on Windows 10 builds before 1809 and on any
                // build without the rounded-corner attribute; both are ignored.
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));
                int corner = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            }
            catch
            {
            }
        }
    }

    // -------------------------------------------------------- custom controls

    /// <summary>
    /// A button drawn from scratch. Derives from Control rather than Button so
    /// there is no system chrome underneath to fight with.
    /// </summary>
    internal sealed class FlatButton : Control
    {
        private Theme palette = Theme.Light;
        private bool primary;
        private bool hovered;
        private bool pressed;

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            Height = 34;
        }

        public Theme Palette
        {
            get { return palette; }
            set { palette = value; Invalidate(); }
        }

        public bool Primary
        {
            get { return primary; }
            set { primary = value; Invalidate(); }
        }

        public override string Text
        {
            get { return base.Text; }
            set { base.Text = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : palette.Bg);

            Rectangle rect = ClientRectangle;
            rect.Width -= 1;
            rect.Height -= 1;

            Color fill = primary ? palette.Accent : palette.Surface;
            if (primary && hovered && Enabled) { fill = palette.AccentHover; }
            if (pressed && Enabled) { fill = Theme.Blend(fill, Color.Black, 0.12); }
            if (!primary && hovered && Enabled) { fill = palette.SurfaceAlt; }
            if (!Enabled) { fill = Theme.Blend(fill, palette.Bg, 0.55); }

            using (GraphicsPath path = Theme.RoundedRect(rect, 6))
            {
                using (SolidBrush brush = new SolidBrush(fill))
                {
                    g.FillPath(brush, path);
                }
                if (!primary)
                {
                    using (Pen pen = new Pen(palette.Border))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }

            Color textColor = primary ? palette.AccentText : palette.Text;
            if (!Enabled) { textColor = Theme.Blend(textColor, fill, 0.55); }

            TextRenderer.DrawText(g, Text, Font, ClientRectangle, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>
    /// A borderless TextBox inside a panel we paint ourselves. WinForms offers
    /// exactly three border styles, none of which can be coloured, so the only
    /// route to a themed input is to host one and draw around it.
    /// </summary>
    internal sealed class ThemedTextBox : Panel
    {
        private Theme palette;
        private readonly TextBox inner;

        public ThemedTextBox(Theme theme, bool multiline)
        {
            palette = theme;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            inner = new TextBox();
            inner.BorderStyle = BorderStyle.None;
            inner.Multiline = multiline;
            inner.Dock = DockStyle.Fill;
            if (multiline) { inner.ScrollBars = ScrollBars.Vertical; }
            inner.GotFocus += delegate { Invalidate(); };
            inner.LostFocus += delegate { Invalidate(); };

            Padding = new Padding(11, 9, 11, 9);
            Controls.Add(inner);
            ApplyTheme(theme);
        }

        public TextBox Inner { get { return inner; } }

        public string Value
        {
            get { return inner.Text; }
            set { inner.Text = value; }
        }

        public Theme Palette
        {
            get { return palette; }
            set { ApplyTheme(value); }
        }

        private void ApplyTheme(Theme theme)
        {
            palette = theme;
            BackColor = theme.InputBg;
            inner.BackColor = theme.InputBg;
            inner.ForeColor = theme.Text;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle rect = ClientRectangle;
            rect.Width -= 1;
            rect.Height -= 1;

            using (GraphicsPath path = Theme.RoundedRect(rect, 6))
            using (Pen pen = new Pen(inner.Focused ? palette.Accent : palette.Border))
            {
                g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>
    /// Click it, press a combination, it records it. Typing a hotkey is the
    /// only sane way to set one — asking a user to hand-write "Ctrl+Alt+P" into
    /// a JSON file and get the spelling right is how settings go unchanged.
    /// </summary>
    internal sealed class HotkeyBox : Control
    {
        private Theme palette;
        private string combo;
        private bool recording;

        public HotkeyBox(Theme theme, string initial)
        {
            palette = theme;
            combo = initial;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Height = 34;
        }

        public string Combo
        {
            get { return combo; }
            set { combo = value; Invalidate(); }
        }

        public Theme Palette
        {
            get { return palette; }
            set { palette = value; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            recording = true;
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            recording = false;
            Invalidate();
        }

        // Arrow keys, Tab and Enter would otherwise be swallowed by the form's
        // navigation before OnKeyDown ever sees them.
        protected override bool IsInputKey(Keys keyData) { return recording; }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!recording) { base.OnKeyDown(e); return; }

            e.SuppressKeyPress = true;
            e.Handled = true;

            if (e.KeyCode == Keys.Escape) { recording = false; Invalidate(); return; }

            // A bare letter is not a global hotkey — it would fire while the
            // user types anywhere on the system. Require at least one modifier.
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.Menu || e.KeyCode == Keys.ShiftKey ||
                e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin)
            {
                Invalidate();
                return;
            }
            if (!e.Control && !e.Alt && !e.Shift) { return; }

            string key = KeyName(e.KeyCode);
            if (key == null) { return; }

            List<string> parts = new List<string>();
            if (e.Control) { parts.Add("Ctrl"); }
            if (e.Alt) { parts.Add("Alt"); }
            if (e.Shift) { parts.Add("Shift"); }
            parts.Add(key);

            combo = string.Join("+", parts.ToArray());
            recording = false;
            Invalidate();
        }

        /// <summary>Only the keys Hotkey.Parse in RefynHost.cs can read back.</summary>
        private static string KeyName(Keys code)
        {
            if (code >= Keys.A && code <= Keys.Z) { return code.ToString(); }
            if (code >= Keys.D0 && code <= Keys.D9) { return ((char)('0' + (code - Keys.D0))).ToString(); }
            if (code >= Keys.F1 && code <= Keys.F12) { return code.ToString(); }
            if (code == Keys.Space) { return "Space"; }
            return null;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : palette.Bg);

            Rectangle rect = ClientRectangle;
            rect.Width -= 1;
            rect.Height -= 1;

            using (GraphicsPath path = Theme.RoundedRect(rect, 6))
            {
                using (SolidBrush brush = new SolidBrush(palette.InputBg))
                {
                    g.FillPath(brush, path);
                }
                using (Pen pen = new Pen(recording ? palette.Accent : palette.Border))
                {
                    g.DrawPath(pen, path);
                }
            }

            string label = recording ? "Press a combination…" : combo;
            Color color = recording ? palette.Accent : palette.Text;
            Rectangle textRect = new Rectangle(11, 0, Width - 22, Height);
            TextRenderer.DrawText(g, label, Font, textRect, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>
    /// A dropdown that actually respects the theme.
    ///
    /// Stock ComboBox is not themeable on .NET Framework: `BackColor` reaches
    /// the text area but the drop button is drawn by the OS, so on a dark form
    /// it stays a bright white rectangle in the corner. Owner-drawing the items
    /// does not fix the button either. The only reliable route is to draw the
    /// closed state ourselves and pop a themed menu for the list.
    /// </summary>
    internal sealed class ThemedSelect : Control
    {
        private Theme palette;
        private readonly List<object> items = new List<object>();
        private int selected = -1;
        private bool hovered;

        public event EventHandler SelectionChanged;

        public ThemedSelect(Theme theme)
        {
            palette = theme;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Height = 34;
        }

        public Theme Palette
        {
            get { return palette; }
            set { palette = value; Invalidate(); }
        }

        public IList<object> Items { get { return items; } }

        public int SelectedIndex
        {
            get { return selected; }
            set { selected = value; Invalidate(); }
        }

        public object SelectedItem
        {
            get { return (selected >= 0 && selected < items.Count) ? items[selected] : null; }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (items.Count == 0) { return; }

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new ThemedMenuRenderer(palette);
            menu.BackColor = palette.Surface;
            menu.ForeColor = palette.Text;
            menu.ShowImageMargin = false;
            menu.Font = Font;

            for (int i = 0; i < items.Count; i++)
            {
                ToolStripMenuItem entry = new ToolStripMenuItem(items[i].ToString());
                entry.ForeColor = palette.Text;
                entry.Checked = (i == selected);
                int index = i;
                entry.Click += delegate
                {
                    selected = index;
                    Invalidate();
                    if (SelectionChanged != null) { SelectionChanged(this, EventArgs.Empty); }
                };
                menu.Items.Add(entry);
            }
            // Open below the control so it reads as a dropdown, not a popup.
            menu.Show(this, new Point(0, Height));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : palette.Bg);

            Rectangle rect = ClientRectangle;
            rect.Width -= 1;
            rect.Height -= 1;

            using (GraphicsPath path = Theme.RoundedRect(rect, 6))
            {
                using (SolidBrush brush = new SolidBrush(palette.InputBg))
                {
                    g.FillPath(brush, path);
                }
                using (Pen pen = new Pen(hovered ? palette.Accent : palette.Border))
                {
                    g.DrawPath(pen, path);
                }
            }

            string label = SelectedItem != null ? SelectedItem.ToString() : "";
            TextRenderer.DrawText(g, label, Font, new Rectangle(11, 0, Width - 34, Height),
                palette.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            // Chevron.
            int cx = Width - 18;
            int cy = Height / 2 - 1;
            using (Pen pen = new Pen(palette.TextDim, 1.6f))
            {
                g.DrawLines(pen, new Point[] {
                    new Point(cx - 4, cy - 2), new Point(cx, cy + 2), new Point(cx + 4, cy - 2)
                });
            }
        }
    }

    /// <summary>Dark-aware renderer for the dropdown and tray menus.</summary>
    internal sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly Theme palette;

        public ThemedMenuRenderer(Theme theme) : base(new ThemedColorTable(theme))
        {
            palette = theme;
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? palette.AccentText : palette.Text;
            base.OnRenderItemText(e);
        }
    }

    internal sealed class ThemedColorTable : ProfessionalColorTable
    {
        private readonly Theme palette;
        public ThemedColorTable(Theme theme) { palette = theme; UseSystemColors = false; }

        public override Color ToolStripDropDownBackground { get { return palette.Surface; } }
        public override Color ImageMarginGradientBegin { get { return palette.Surface; } }
        public override Color ImageMarginGradientMiddle { get { return palette.Surface; } }
        public override Color ImageMarginGradientEnd { get { return palette.Surface; } }
        public override Color MenuItemSelected { get { return palette.Accent; } }
        public override Color MenuItemSelectedGradientBegin { get { return palette.Accent; } }
        public override Color MenuItemSelectedGradientEnd { get { return palette.Accent; } }
        public override Color MenuItemBorder { get { return palette.Accent; } }
        public override Color MenuBorder { get { return palette.Border; } }
        public override Color SeparatorDark { get { return palette.Border; } }
        public override Color SeparatorLight { get { return palette.Border; } }
        public override Color MenuItemPressedGradientBegin { get { return palette.Surface; } }
        public override Color MenuItemPressedGradientEnd { get { return palette.Surface; } }
        public override Color CheckBackground { get { return palette.Accent; } }
        public override Color CheckSelectedBackground { get { return palette.Accent; } }
    }

    /// <summary>A checkbox drawn to match, since the stock one has no dark mode.</summary>
    internal sealed class ThemedCheck : Control
    {
        private Theme palette;
        private bool isChecked;
        private bool hovered;

        public ThemedCheck(Theme theme, string label)
        {
            palette = theme;
            Text = label;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Height = 26;
        }

        public bool Checked
        {
            get { return isChecked; }
            set { isChecked = value; Invalidate(); }
        }

        public Theme Palette
        {
            get { return palette; }
            set { palette = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            isChecked = !isChecked;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : palette.Bg);

            Rectangle box = new Rectangle(0, (Height - 18) / 2, 18, 18);
            using (GraphicsPath path = Theme.RoundedRect(box, 4))
            {
                using (SolidBrush brush = new SolidBrush(isChecked ? palette.Accent : palette.InputBg))
                {
                    g.FillPath(brush, path);
                }
                if (!isChecked)
                {
                    using (Pen pen = new Pen(hovered ? palette.Accent : palette.Border))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }

            if (isChecked)
            {
                using (Pen pen = new Pen(palette.AccentText, 2f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLines(pen, new Point[] {
                        new Point(box.X + 4, box.Y + 9),
                        new Point(box.X + 7, box.Y + 12),
                        new Point(box.X + 14, box.Y + 5)
                    });
                }
            }

            TextRenderer.DrawText(g, Text, Font, new Rectangle(26, 0, Width - 26, Height),
                palette.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>A segmented control: System / Light / Dark.</summary>
    internal sealed class SegmentedControl : Control
    {
        private Theme palette;
        private readonly string[] options;
        private int selected;

        public event EventHandler SelectionChanged;

        public SegmentedControl(Theme theme, string[] items, int initial)
        {
            palette = theme;
            options = items;
            selected = initial;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Height = 34;
        }

        public int SelectedIndex
        {
            get { return selected; }
            set { selected = value; Invalidate(); }
        }

        public Theme Palette
        {
            get { return palette; }
            set { palette = value; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int index = (e.X * options.Length) / Math.Max(1, Width);
            if (index < 0) { index = 0; }
            if (index >= options.Length) { index = options.Length - 1; }
            if (index != selected)
            {
                selected = index;
                Invalidate();
                if (SelectionChanged != null) { SelectionChanged(this, EventArgs.Empty); }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : palette.Bg);

            Rectangle rect = ClientRectangle;
            rect.Width -= 1;
            rect.Height -= 1;

            using (GraphicsPath path = Theme.RoundedRect(rect, 6))
            {
                using (SolidBrush brush = new SolidBrush(palette.SurfaceAlt))
                {
                    g.FillPath(brush, path);
                }
                using (Pen pen = new Pen(palette.Border))
                {
                    g.DrawPath(pen, path);
                }
            }

            float segment = (float)Width / options.Length;
            for (int i = 0; i < options.Length; i++)
            {
                Rectangle cell = new Rectangle((int)(i * segment), 0, (int)segment, Height);
                if (i == selected)
                {
                    Rectangle pill = cell;
                    pill.Inflate(-3, -3);
                    using (GraphicsPath path = Theme.RoundedRect(pill, 5))
                    using (SolidBrush brush = new SolidBrush(palette.Accent))
                    {
                        g.FillPath(brush, path);
                    }
                }
                TextRenderer.DrawText(g, options[i], Font, cell,
                    i == selected ? palette.AccentText : palette.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
