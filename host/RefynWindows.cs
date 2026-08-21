// RefynWindows.cs — the Settings and Compose windows.
//
// C# 5 only; see the header in RefynHost.cs.
//
// Both windows are laid out in code with explicit pixel positions rather than
// TableLayoutPanel. That is deliberate: the nested-panel approach fights the
// custom-painted controls over background colour, and produces a visibly
// mismatched patchwork the moment a theme changes.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Refyn
{
    // ------------------------------------------------------------- base form

    /// <summary>
    /// Shared plumbing: themed background, dark DWM caption, rounded corners,
    /// and Esc-to-close.
    /// </summary>
    internal class ThemedForm : Form
    {
        protected Theme Palette;

        /// <summary>
        /// Set by the --dump-ui design aid. While true, windows are shown
        /// without taking activation, so a screenshot pass cannot steal focus
        /// from whatever the user is actually doing.
        /// </summary>
        internal static bool DumpMode;

        protected override bool ShowWithoutActivation
        {
            get { return DumpMode; }
        }

        protected ThemedForm(Theme theme)
        {
            Palette = theme;
            Font = new Font("Segoe UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = theme.Bg;
            ForeColor = theme.Text;
            StartPosition = FormStartPosition.Manual;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Dpi;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowChrome.Apply(Handle, Palette.IsDark);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Not while a HotkeyBox is capturing — Esc there cancels recording.
            if (e.KeyCode == Keys.Escape && !(ActiveControl is HotkeyBox))
            {
                Hide();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Closing a window must not take the tray app down with it.
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        /// <summary>Centre on whichever monitor the pointer is on.</summary>
        protected void CentreOnCursor()
        {
            Screen screen = Screen.FromPoint(Cursor.Position);
            Location = new Point(
                screen.WorkingArea.X + (screen.WorkingArea.Width - Width) / 2,
                screen.WorkingArea.Y + (screen.WorkingArea.Height - Height) / 2);
        }

        protected Label Caption(string text, int x, int y, bool dim)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Location = new Point(x, y);
            label.ForeColor = dim ? Palette.TextDim : Palette.Text;
            label.BackColor = Color.Transparent;
            label.Tag = dim ? "dim" : "body";
            Controls.Add(label);
            return label;
        }
    }

    // -------------------------------------------------------- UI design aid

    /// <summary>
    /// Renders a window to a PNG without ever showing it to the user.
    ///
    /// The window is parked far off-screen and shown without activation, so it
    /// takes no focus and appears on no monitor; PrintWindow with
    /// PW_RENDERFULLCONTENT then captures it. This exists because the honest way
    /// to check a layout is to look at it, and the previous way of doing that —
    /// opening the real window on the real desktop — interrupted whoever was
    /// using the machine.
    /// </summary>
    internal static class UiDump
    {
        private const uint PW_RENDERFULLCONTENT = 2;

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        public static void Render(string which, string pngPath)
        {
            ThemedForm.DumpMode = true;

            Config config = Config.Load();
            Theme theme = Theme.Resolve(config.ThemePreference);
            DaemonClient daemon = new DaemonClient(config.Port);
            List<StyleInfo> styles = StyleInfo.Fallback();

            Form target;
            ComposeForm compose = new ComposeForm(theme, daemon, styles);
            SettingsForm settings = new SettingsForm(theme, config, daemon, null);

            if (which == "compose") { target = compose; }
            else if (which == "settings") { target = settings; }
            else
            {
                MainWindow window = new MainWindow(theme, daemon, compose, settings);
                // "main-settings" renders the main window showing its Settings
                // page — the combination most likely to clip, since Settings is
                // the tallest page.
                if (which == "main-settings") { window.GoTo(settings); }
                target = window;
            }

            target.StartPosition = FormStartPosition.Manual;
            target.Location = new Point(-6000, -6000);
            target.Show();

            // Let layout settle and any async daemon fetch land. DoEvents keeps
            // the pump running without Application.Run, which would not return.
            for (int i = 0; i < 60; i++)
            {
                Application.DoEvents();
                Thread.Sleep(50);
            }

            using (Bitmap bitmap = new Bitmap(target.Width, target.Height))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    IntPtr hdc = g.GetHdc();
                    try { PrintWindow(target.Handle, hdc, PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(hdc); }
                }
                bitmap.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
            }
            Console.WriteLine("wrote " + pngPath);
        }
    }

    // ------------------------------------------------------------ main window

    /// <summary>
    /// The window `refyn launch` opens: everything in one place, with Compose
    /// and Settings as pages behind a left rail.
    ///
    /// The two pages are the existing ComposeForm and SettingsForm, embedded as
    /// child controls (TopLevel = false). Reusing them rather than reimplementing
    /// their contents means `refyn settings` and this window can never drift
    /// apart — they are literally the same code, shown in a different frame.
    /// </summary>
    internal sealed class MainWindow : ThemedForm
    {
        private readonly Panel content;
        private readonly List<FlatButton> navButtons = new List<FlatButton>();
        private readonly Label statusDot;
        private readonly Label statusText;
        private readonly DaemonClient daemon;
        private Form currentPage;

        public MainWindow(Theme theme, DaemonClient client, Form composePage, Form settingsPage)
            : base(theme)
        {
            daemon = client;

            Text = "Refyn";
            Icon = TrayIconFactory.Create(false);
            FormBorderStyle = FormBorderStyle.Sizable;
            // Tall enough that the Settings page fits without scrolling on a
            // 1080p screen; AutoScroll on the content panel covers the rest.
            ClientSize = new Size(880, 838);
            MinimumSize = new Size(760, 560);
            ShowInTaskbar = true; // this one is a real window the user opened

            const int RailWidth = 186;

            Panel rail = new Panel();
            rail.Dock = DockStyle.Left;
            rail.Width = RailWidth;
            rail.BackColor = theme.Surface;
            Controls.Add(rail);

            Label brand = new Label();
            brand.Text = "Refyn";
            brand.Font = new Font("Segoe UI", 15f, FontStyle.Regular, GraphicsUnit.Point);
            brand.ForeColor = theme.Text;
            brand.BackColor = Color.Transparent;
            brand.AutoSize = true;
            brand.Location = new Point(22, 24);
            rail.Controls.Add(brand);

            Label tagline = new Label();
            tagline.Text = "Ctrl+Alt+P rewrites\nwhatever you select.";
            tagline.Font = new Font("Segoe UI", 8.5f);
            tagline.ForeColor = theme.TextDim;
            tagline.BackColor = Color.Transparent;
            tagline.AutoSize = true;
            tagline.Location = new Point(22, 56);
            rail.Controls.Add(tagline);

            content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = theme.Bg;
            // The Settings page is a fixed 798px tall — taller than this window
            // on a small screen. Without AutoScroll its Save button is simply
            // cut off, with nothing to indicate there is more below.
            content.AutoScroll = true;
            Controls.Add(content);
            content.BringToFront();

            AddNav(rail, "Rewrite", 108, composePage);
            AddNav(rail, "Settings", 152, settingsPage);

            // Live status, bottom of the rail.
            statusDot = new Label();
            statusDot.Text = "●";
            statusDot.Font = new Font("Segoe UI", 11f);
            statusDot.ForeColor = theme.TextDim;
            statusDot.BackColor = Color.Transparent;
            statusDot.AutoSize = true;
            statusDot.Location = new Point(22, ClientSize.Height - 62);
            statusDot.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            rail.Controls.Add(statusDot);

            statusText = new Label();
            statusText.Text = "checking…";
            statusText.Font = new Font("Segoe UI", 8.5f);
            statusText.ForeColor = theme.TextDim;
            statusText.BackColor = Color.Transparent;
            statusText.AutoSize = true;
            statusText.MaximumSize = new Size(RailWidth - 56, 0);
            statusText.Location = new Point(40, ClientSize.Height - 60);
            statusText.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            rail.Controls.Add(statusText);

            Show(composePage);
            RefreshStatusAsync();
        }

        private void AddNav(Panel rail, string label, int y, Form page)
        {
            FlatButton button = new FlatButton();
            button.Text = label;
            button.Palette = Palette;
            button.Font = Font;
            button.Size = new Size(rail.Width - 44, 36);
            button.Location = new Point(22, y);
            button.Tag = page;
            button.Click += delegate { Show(page); };
            rail.Controls.Add(button);
            navButtons.Add(button);
        }

        /// <summary>Swap the visible page. Pages are kept alive, not recreated.</summary>
        private void Show(Form page)
        {
            if (page == null || page == currentPage) { return; }

            if (currentPage != null) { currentPage.Visible = false; }

            if (!content.Controls.Contains(page))
            {
                // A Form can live inside another control once it stops being a
                // top-level window. Its own border and title bar must go, or it
                // renders a window frame in the middle of the page.
                page.TopLevel = false;
                page.FormBorderStyle = FormBorderStyle.None;
                page.Dock = DockStyle.Fill;
                page.ControlBox = false;
                content.Controls.Add(page);
            }

            page.Visible = true;
            page.BringToFront();
            currentPage = page;

            foreach (FlatButton button in navButtons)
            {
                button.Primary = ReferenceEquals(button.Tag, page);
            }
        }

        private async void RefreshStatusAsync()
        {
            try
            {
                DaemonSettings settings = await daemon.GetSettingsAsync();
                if (settings == null)
                {
                    statusDot.ForeColor = Palette.Danger;
                    statusText.Text = "daemon not running\nhotkeys will not work";
                    return;
                }
                statusDot.ForeColor = Color.FromArgb(64, 178, 108);
                statusText.Text = settings.KeyConfigured
                    ? "ready\n" + ShortModel(settings.Model)
                    : "no API key set\nadd one in Settings";
                if (!settings.KeyConfigured) { statusDot.ForeColor = Palette.Danger; }
            }
            catch
            {
                statusDot.ForeColor = Palette.Danger;
                statusText.Text = "daemon unreachable";
            }
        }

        /// <summary>
        /// Vendor prefix dropped and length capped, because the rail is narrow
        /// and a long id wraps mid-word into something like "…flash-073 / 1".
        /// </summary>
        private static string ShortModel(string model)
        {
            if (string.IsNullOrEmpty(model)) { return ""; }
            int slash = model.LastIndexOf('/');
            string name = slash >= 0 ? model.Substring(slash + 1) : model;
            return name.Length > 20 ? name.Substring(0, 19) + "…" : name;
        }

        /// <summary>Is this page hosted here? Governs where the CLI routes.</summary>
        public bool Owns(Form page)
        {
            return page != null && content.Controls.Contains(page);
        }

        /// <summary>Bring the window forward on a given page.</summary>
        public void GoTo(Form page)
        {
            Show(page);
            // During an off-screen dump the window must not be brought forward;
            // that is the whole point of dump mode.
            if (!ThemedForm.DumpMode) { Open(); }
        }

        public void Open()
        {
            if (!Visible)
            {
                CentreOnCursor();
                Show();
            }
            if (WindowState == FormWindowState.Minimized) { WindowState = FormWindowState.Normal; }
            Native.ForceForeground(Handle);
            Activate();
            RefreshStatusAsync();
        }
    }

    // --------------------------------------------------------------- settings

    internal sealed class SettingsForm : ThemedForm
    {
        private readonly Config config;
        private readonly DaemonClient daemon;
        private readonly Action<Config> onSaved;

        private HotkeyBox improveKey;
        private HotkeyBox composeKey;
        private HotkeyBox stylesKey;
        private SegmentedControl themeChoice;
        private ThemedTextBox modelBox;
        private ThemedTextBox keyBox;
        private ThemedTextBox portBox;
        private ThemedSelect styleBox;
        private ThemedCheck autostartBox;
        private ThemedCheck rememberBox;
        private Label status;
        private FlatButton saveButton;

        private string appRoot;
        private string nodePath;

        public SettingsForm(Theme theme, Config current, DaemonClient client, Action<Config> saved)
            : base(theme)
        {
            config = current;
            daemon = client;
            onSaved = saved;

            Text = "Refyn Settings";
            // Height is derived from the layout below, not guessed. The first
            // version was 618 and silently clipped the Port field and the Save
            // button off the bottom of the window.
            ClientSize = new Size(520, 798);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Icon = TrayIconFactory.Create(false);

            Build();
            LoadFromDaemonAsync();
        }

        private void Build()
        {
            int x = 28;
            int width = ClientSize.Width - (x * 2);
            int y = 22;

            Label title = Caption("Settings", x, y, false);
            title.Font = new Font("Segoe UI", 16f, FontStyle.Regular, GraphicsUnit.Point);
            y += 46;

            // --- hotkeys -----------------------------------------------------
            SectionHeader("Hotkeys", x, ref y);

            improveKey = AddHotkey("Rewrite selection in place", config.HotkeyImprove, x, width, ref y);
            composeKey = AddHotkey("Open compose window", config.HotkeyCompose, x, width, ref y);
            stylesKey = AddHotkey("Pick a style", config.HotkeyStyles, x, width, ref y);

            Label note = Caption("Hotkey changes apply when Refyn restarts.", x, y, true);
            note.Font = new Font("Segoe UI", 8.5f);
            y += 28;

            // --- appearance --------------------------------------------------
            SectionHeader("Appearance", x, ref y);
            Caption("Theme", x, y, true);
            y += 20;
            themeChoice = new SegmentedControl(Palette, new string[] { "System", "Light", "Dark" },
                                               IndexOfTheme(config.ThemePreference));
            themeChoice.Location = new Point(x, y);
            themeChoice.Size = new Size(width, 34);
            themeChoice.Font = Font;
            themeChoice.SelectionChanged += delegate { PreviewTheme(); };
            Controls.Add(themeChoice);
            y += 48;

            // --- model -------------------------------------------------------
            SectionHeader("Model", x, ref y);

            Caption("Model ID", x, y, true);
            y += 20;
            modelBox = new ThemedTextBox(Palette, false);
            modelBox.Location = new Point(x, y);
            modelBox.Size = new Size(width, 34);
            modelBox.Inner.Font = Font;
            Controls.Add(modelBox);
            y += 44;

            Caption("API key", x, y, true);
            y += 20;
            keyBox = new ThemedTextBox(Palette, false);
            keyBox.Location = new Point(x, y);
            keyBox.Size = new Size(width, 34);
            keyBox.Inner.Font = Font;
            Controls.Add(keyBox);
            y += 44;

            // --- behaviour ---------------------------------------------------
            SectionHeader("Behaviour", x, ref y);

            // Default style and Port share a row — Port is set roughly never,
            // and giving it a whole row of its own pushed the window past the
            // height a laptop screen can show.
            int styleWidth = width - 130;
            Caption("Default mode", x, y, true);
            Caption("Port", x + styleWidth + 20, y, true);
            y += 20;

            styleBox = new ThemedSelect(Palette);
            styleBox.Location = new Point(x, y);
            styleBox.Size = new Size(styleWidth, 34);
            styleBox.Font = Font;
            Controls.Add(styleBox);

            portBox = new ThemedTextBox(Palette, false);
            portBox.Location = new Point(x + styleWidth + 20, y);
            portBox.Size = new Size(110, 34);
            portBox.Inner.Font = Font;
            portBox.Value = config.Port.ToString(CultureInfo.InvariantCulture);
            Controls.Add(portBox);
            y += 46;

            rememberBox = new ThemedCheck(Palette, "Remember the last mode I pick instead");
            rememberBox.Location = new Point(x, y);
            rememberBox.Size = new Size(width, 26);
            rememberBox.Font = Font;
            rememberBox.Checked = config.RememberLastMode;
            Controls.Add(rememberBox);
            y += 30;

            Label modeHint = Caption("Off: every rewrite uses the default mode above, and the style\npicker is a one-off. On: picking a style makes it the new default.",
                                     x + 26, y, true);
            modeHint.Font = new Font("Segoe UI", 8.5f);
            y += 42;

            autostartBox = new ThemedCheck(Palette, "Start Refyn when I sign in");
            autostartBox.Location = new Point(x, y);
            autostartBox.Size = new Size(width, 26);
            autostartBox.Font = Font;
            autostartBox.Checked = AutostartRegistered();
            Controls.Add(autostartBox);
            y += 46;

            // --- footer ------------------------------------------------------
            status = Caption("", x, y + 9, true);
            status.MaximumSize = new Size(width - 200, 0);

            saveButton = new FlatButton();
            saveButton.Text = "Save";
            saveButton.Primary = true;
            saveButton.Palette = Palette;
            saveButton.Font = Font;
            saveButton.Size = new Size(96, 34);
            saveButton.Location = new Point(ClientSize.Width - 28 - 96, y);
            saveButton.Click += delegate { Save(); };
            Controls.Add(saveButton);

            FlatButton openFolder = new FlatButton();
            openFolder.Text = "Config folder";
            openFolder.Palette = Palette;
            openFolder.Font = Font;
            openFolder.Size = new Size(118, 34);
            openFolder.Location = new Point(ClientSize.Width - 28 - 96 - 8 - 118, y);
            openFolder.Click += delegate
            {
                try
                {
                    System.IO.Directory.CreateDirectory(Config.Folder);
                    Process.Start("explorer.exe", Config.Folder);
                }
                catch (Exception ex) { status.Text = ex.Message; }
            };
            Controls.Add(openFolder);
        }

        /// <summary>
        /// Section headers get breathing room above them, not below — the gap
        /// belongs to the boundary between groups, and without it every section
        /// reads as a continuation of the one before.
        /// </summary>
        private void SectionHeader(string text, int x, ref int y)
        {
            y += 14;
            Label label = Caption(text.ToUpperInvariant(), x, y, true);
            label.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            y += 26;
        }

        private HotkeyBox AddHotkey(string label, string combo, int x, int width, ref int y)
        {
            Caption(label, x, y + 8, true);
            HotkeyBox box = new HotkeyBox(Palette, combo);
            box.Location = new Point(x + width - 190, y);
            box.Size = new Size(190, 34);
            box.Font = Font;
            Controls.Add(box);
            y += 42;
            return box;
        }

        private static int IndexOfTheme(string preference)
        {
            if (preference == "light") { return 1; }
            if (preference == "dark") { return 2; }
            return 0;
        }

        private static string ThemeAt(int index)
        {
            if (index == 1) { return "light"; }
            if (index == 2) { return "dark"; }
            return "system";
        }

        /// <summary>
        /// Repaint the whole window in the newly chosen theme immediately, so
        /// the choice is visible before it is saved.
        /// </summary>
        private void PreviewTheme()
        {
            Palette = Theme.Resolve(ThemeAt(themeChoice.SelectedIndex));
            BackColor = Palette.Bg;
            ForeColor = Palette.Text;
            WindowChrome.Apply(Handle, Palette.IsDark);

            foreach (Control control in Controls)
            {
                FlatButton button = control as FlatButton;
                if (button != null) { button.Palette = Palette; continue; }

                ThemedTextBox text = control as ThemedTextBox;
                if (text != null) { text.Palette = Palette; continue; }

                HotkeyBox hotkey = control as HotkeyBox;
                if (hotkey != null) { hotkey.Palette = Palette; continue; }

                SegmentedControl segment = control as SegmentedControl;
                if (segment != null) { segment.Palette = Palette; continue; }

                ThemedSelect select = control as ThemedSelect;
                if (select != null) { select.Palette = Palette; continue; }

                ThemedCheck check = control as ThemedCheck;
                if (check != null) { check.Palette = Palette; continue; }

                Label label = control as Label;
                if (label != null)
                {
                    // Whether a label is dim is recorded in its Tag when built.
                    // Inferring it from the current colour instead would break
                    // the moment a theme uses the same value for two roles.
                    bool dim = "dim".Equals(label.Tag);
                    label.ForeColor = dim ? Palette.TextDim : Palette.Text;
                }
            }
            Invalidate(true);
        }

        private async void LoadFromDaemonAsync()
        {
            try
            {
                List<StyleInfo> styles = await daemon.GetStylesAsync();
                if (styles == null) { styles = StyleInfo.Fallback(); }
                styleBox.Items.Clear();
                foreach (StyleInfo style in styles)
                {
                    styleBox.Items.Add(style);
                    if (style.Id == config.DefaultStyle) { styleBox.SelectedIndex = styleBox.Items.Count - 1; }
                }
                if (styleBox.SelectedIndex < 0 && styleBox.Items.Count > 0) { styleBox.SelectedIndex = 0; }

                DaemonSettings settings = await daemon.GetSettingsAsync();
                if (settings != null)
                {
                    modelBox.Value = settings.Model;
                    keyBox.Value = settings.KeyMasked;
                    appRoot = settings.AppRoot;
                    nodePath = settings.Node;
                    status.Text = settings.KeyConfigured ? "" : "No API key set.";
                }
                else
                {
                    status.Text = "Daemon not running — model and key are unavailable.";
                    modelBox.Inner.Enabled = false;
                    keyBox.Inner.Enabled = false;
                }
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
        }

        private async void Save()
        {
            saveButton.Enabled = false;
            saveButton.Text = "Saving…";
            status.Text = "";
            try
            {
                int port = config.Port;
                int parsed;
                if (int.TryParse(portBox.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                    && parsed > 0 && parsed < 65536)
                {
                    port = parsed;
                }

                StyleInfo chosen = styleBox.SelectedItem as StyleInfo;

                config.HotkeyImprove = improveKey.Combo;
                config.HotkeyCompose = composeKey.Combo;
                config.HotkeyStyles = stylesKey.Combo;
                config.ThemePreference = ThemeAt(themeChoice.SelectedIndex);
                config.DefaultStyle = chosen != null ? chosen.Id : config.DefaultStyle;
                config.RememberLastMode = rememberBox.Checked;
                config.Port = port;
                config.Save();

                // Model and key belong to the daemon; it owns .env.
                string keyValue = keyBox.Value.Trim();
                await daemon.PutSettingsAsync(modelBox.Value.Trim(), keyValue);

                SetAutostart(autostartBox.Checked);

                if (onSaved != null) { onSaved(config); }
                status.Text = "Saved.";
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
            finally
            {
                saveButton.Enabled = true;
                saveButton.Text = "Save";
            }
        }

        // --- autostart ------------------------------------------------------

        private static bool AutostartRegistered()
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("schtasks", "/Query /TN RefynLogon");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                using (Process process = Process.Start(info))
                {
                    process.WaitForExit(4000);
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Delegated to the CLI rather than reimplemented, so the scheduled task
        /// and its windowless launcher shim are defined in exactly one place.
        /// </summary>
        private void SetAutostart(bool enable)
        {
            if (AutostartRegistered() == enable) { return; }
            if (string.IsNullOrEmpty(nodePath) || string.IsNullOrEmpty(appRoot))
            {
                status.Text = "Saved, but autostart needs the daemon running.";
                return;
            }
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(nodePath,
                    "\"" + System.IO.Path.Combine(appRoot, "refyn.mjs") + "\" autostart " + (enable ? "on" : "off"));
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                Process.Start(info);
            }
            catch (Exception ex)
            {
                status.Text = "Saved, but autostart failed: " + ex.Message;
            }
        }

        public void Open()
        {
            CentreOnCursor();
            Show();
            Native.ForceForeground(Handle);
            Activate();
        }
    }

    // ---------------------------------------------------------------- compose

    internal sealed class ComposeForm : ThemedForm
    {
        private readonly DaemonClient daemon;
        private readonly ThemedTextBox input;
        private readonly ThemedTextBox output;
        private readonly ThemedSelect stylePicker;
        private readonly FlatButton rewriteButton;
        private readonly FlatButton pasteButton;
        private readonly Label status;
        private IntPtr returnTo;

        public ComposeForm(Theme theme, DaemonClient client, List<StyleInfo> styles)
            : base(theme)
        {
            daemon = client;

            Text = "Refyn";
            Icon = TrayIconFactory.Create(false);
            FormBorderStyle = FormBorderStyle.Sizable;
            ClientSize = new Size(680, 520);
            MinimumSize = new Size(520, 420);
            TopMost = true;

            int pad = 20;
            int width = ClientSize.Width - (pad * 2);

            Label heading = Caption("Compose", pad, 16, false);
            heading.Font = new Font("Segoe UI", 13f);

            input = new ThemedTextBox(Palette, true);
            input.Location = new Point(pad, 52);
            input.Size = new Size(width, 150);
            input.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            input.Inner.Font = new Font("Consolas", 10.5f);
            input.Inner.KeyDown += OnInputKeyDown;
            Controls.Add(input);

            stylePicker = new ThemedSelect(Palette);
            stylePicker.Location = new Point(pad, 213);
            stylePicker.Size = new Size(190, 34);
            stylePicker.Font = Font;
            stylePicker.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            foreach (StyleInfo style in styles) { stylePicker.Items.Add(style); }
            if (stylePicker.Items.Count > 0) { stylePicker.SelectedIndex = 0; }
            Controls.Add(stylePicker);

            rewriteButton = new FlatButton();
            rewriteButton.Text = "Rewrite";
            rewriteButton.Primary = true;
            rewriteButton.Palette = Palette;
            rewriteButton.Font = Font;
            rewriteButton.Size = new Size(110, 34);
            rewriteButton.Location = new Point(pad + 200, 213);
            rewriteButton.Click += delegate { RunRewrite(); };
            Controls.Add(rewriteButton);

            status = Caption("Ctrl+Enter to rewrite", pad + 322, 222, true);
            status.Font = new Font("Segoe UI", 8.5f);

            output = new ThemedTextBox(Palette, true);
            output.Location = new Point(pad, 262);
            output.Size = new Size(width, 190);
            output.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            output.Inner.Font = new Font("Consolas", 10.5f);
            output.Inner.ReadOnly = true;
            Controls.Add(output);

            pasteButton = new FlatButton();
            pasteButton.Text = "Paste into last app";
            pasteButton.Primary = true;
            pasteButton.Palette = Palette;
            pasteButton.Font = Font;
            pasteButton.Size = new Size(150, 34);
            pasteButton.Location = new Point(ClientSize.Width - pad - 150, 466);
            pasteButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            pasteButton.Click += delegate { Deliver(true); };
            Controls.Add(pasteButton);

            FlatButton copyButton = new FlatButton();
            copyButton.Text = "Copy";
            copyButton.Palette = Palette;
            copyButton.Font = Font;
            copyButton.Size = new Size(84, 34);
            copyButton.Location = new Point(ClientSize.Width - pad - 150 - 8 - 84, 466);
            copyButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            copyButton.Click += delegate { Deliver(false); };
            Controls.Add(copyButton);
        }

        /// <summary>
        /// Set the window up without showing it. Split out from Open so the
        /// main window can host this form as a page — an embedded form is not a
        /// top-level window, and calling Show/ForceForeground on one puts a
        /// frameless panel at the screen origin.
        /// </summary>
        public void Prepare(IntPtr previousWindow, string preferredStyle)
        {
            returnTo = previousWindow;
            pasteButton.Enabled = previousWindow != IntPtr.Zero;

            for (int i = 0; i < stylePicker.Items.Count; i++)
            {
                StyleInfo style = stylePicker.Items[i] as StyleInfo;
                if (style != null && style.Id == preferredStyle) { stylePicker.SelectedIndex = i; break; }
            }

            if (input.Value.Length == 0)
            {
                string clip;
                if (ClipboardSafe.TryGetText(out clip) && clip.Trim().Length > 0 && clip.Length < 12000)
                {
                    input.Value = clip;
                }
            }

        }

        public void Open(IntPtr previousWindow, string preferredStyle)
        {
            Prepare(previousWindow, preferredStyle);
            CentreOnCursor();
            Show();
            Native.ForceForeground(Handle);
            input.Inner.Focus();
            input.Inner.SelectAll();
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return))
            {
                e.SuppressKeyPress = true; // otherwise a newline lands in the box
                RunRewrite();
            }
        }

        private async void RunRewrite()
        {
            string text = input.Value.Trim();
            if (text.Length == 0) { return; }

            StyleInfo style = stylePicker.SelectedItem as StyleInfo;
            string styleId = style != null ? style.Id : "improve";

            rewriteButton.Enabled = false;
            rewriteButton.Text = "Rewriting…";
            status.Text = "";
            try
            {
                DateTime started = DateTime.UtcNow;
                output.Value = await daemon.RewriteAsync(text, styleId);
                status.Text = ((int)(DateTime.UtcNow - started).TotalMilliseconds) + " ms";
            }
            catch (DaemonDownException)
            {
                output.Value = "";
                status.Text = "Daemon not running — run:  refyn start";
            }
            catch (Exception ex)
            {
                output.Value = "";
                status.Text = ex.Message;
            }
            finally
            {
                rewriteButton.Enabled = true;
                rewriteButton.Text = "Rewrite";
            }
        }

        private async void Deliver(bool paste)
        {
            if (output.Value.Length == 0) { return; }
            if (!ClipboardSafe.TrySetText(output.Value))
            {
                status.Text = "Clipboard is locked by another program.";
                return;
            }
            Hide();
            if (paste && returnTo != IntPtr.Zero)
            {
                Native.ForceForeground(returnTo);
                await Task.Delay(120);
                Input.SendChord(Native.VK_CONTROL, Native.VK_V);
            }
        }
    }
}
