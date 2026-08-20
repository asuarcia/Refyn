// PromptsmithHost — the input layer for Promptsmith.
//
// Tray-resident Windows app: registers global hotkeys, lifts the current
// selection out of whatever app has focus, sends it to the local daemon for
// rewriting, and pastes the result back in place.
//
// BUILD CONSTRAINT — read before editing.
// This compiles with the .NET Framework compiler that ships in the box:
//     C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
// which is **C# 5 only**. No string interpolation, no null-conditional (?.),
// no `out var`, no expression-bodied members, no nameof, no auto-property
// initialisers. async/await and LINQ are fine. The payoff for that discipline
// is that Promptsmith installs on any Windows machine with nothing to download:
// no .NET SDK, no runtime, no AutoHotkey. See host/build.ps1.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Promptsmith
{
    // ---------------------------------------------------------------- Program

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool createdNew;
            using (Mutex instanceLock = new Mutex(true, "Global\\PromptsmithHost", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show(
                        "Promptsmith is already running. Look for the tray icon near the clock.",
                        "Promptsmith", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // An unhandled exception on the UI thread would otherwise pop the
                // .NET crash dialog and kill a background app the user cannot see.
                Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
                {
                    Tray.Balloon("Promptsmith error", e.Exception.Message, ToolTipIcon.Error);
                };
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

                Application.Run(new PromptsmithContext());
                GC.KeepAlive(instanceLock);
            }
        }
    }

    // ------------------------------------------------------- Application core

    internal sealed class PromptsmithContext : ApplicationContext
    {
        private const int HotkeyImprove = 1;
        private const int HotkeyCompose = 2;
        private const int HotkeyStyles = 3;

        private readonly Config config;
        private readonly DaemonClient daemon;
        private readonly HotkeyWindow hotkeys;
        private readonly ForegroundTracker foreground;
        private NotifyIcon tray;
        private ToolStripMenuItem pauseItem;
        private ContextMenuStrip stylePicker;
        private ComposeForm compose;

        private List<StyleInfo> styles;
        private string lastStyle;
        private bool busy;
        private bool paused;

        public PromptsmithContext()
        {
            config = Config.Load();
            lastStyle = "improve";
            styles = StyleInfo.Fallback();
            daemon = new DaemonClient(config.Port);

            Log.Write("--- Promptsmith host starting, port " + config.Port + " ---");
            foreground = new ForegroundTracker();
            hotkeys = new HotkeyWindow(OnHotkey);
            BuildTray();
            RegisterAll();

            // Styles come from the daemon so adding one there needs no rebuild
            // here. Fire and forget — the fallback list is already usable.
            RefreshStylesAsync();
        }

        // ------------------------------------------------------------ tray UI

        private void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = TrayIconFactory.Create(false);
            tray.Text = "Promptsmith";
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowCompose(foreground.LastExternal); };
            tray.ContextMenuStrip = BuildMenu();
            Tray.Attach(tray);
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            // Every menu action targets the last app the user was actually in.
            // By the time a tray menu is clicked the foreground window is the
            // taskbar, so GetForegroundWindow() is useless here \u2014 the tracker is
            // the only thing that still knows where the text lives.
            ToolStripMenuItem improve = new ToolStripMenuItem("Improve selection\t" + config.HotkeyImprove);
            improve.Click += delegate { ImproveSelection(null, foreground.LastExternal); };
            menu.Items.Add(improve);

            ToolStripMenuItem composeItem = new ToolStripMenuItem("Compose\u2026\t" + config.HotkeyCompose);
            composeItem.Click += delegate { ShowCompose(foreground.LastExternal); };
            menu.Items.Add(composeItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem stylesRoot = new ToolStripMenuItem("Rewrite selection as");
            stylesRoot.Name = "stylesRoot";
            PopulateStyleItems(stylesRoot.DropDownItems, IntPtr.Zero);
            menu.Items.Add(stylesRoot);

            menu.Items.Add(new ToolStripSeparator());

            pauseItem = new ToolStripMenuItem("Pause hotkeys");
            pauseItem.CheckOnClick = true;
            pauseItem.Click += delegate
            {
                paused = pauseItem.Checked;
                if (paused) { UnregisterAll(); } else { RegisterAll(); }
                tray.Icon = TrayIconFactory.Create(paused);
                tray.Text = paused ? "Promptsmith (paused)" : "Promptsmith";
            };
            menu.Items.Add(pauseItem);

            ToolStripMenuItem folder = new ToolStripMenuItem("Open config folder");
            folder.Click += delegate
            {
                try
                {
                    Directory.CreateDirectory(Config.Folder);
                    System.Diagnostics.Process.Start("explorer.exe", Config.Folder);
                }
                catch (Exception ex)
                {
                    Tray.Balloon("Promptsmith", ex.Message, ToolTipIcon.Warning);
                }
            };
            menu.Items.Add(folder);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem quit = new ToolStripMenuItem("Quit Promptsmith");
            quit.Click += delegate { Shutdown(); };
            menu.Items.Add(quit);

            return menu;
        }

        /// <param name="target">
        /// Window to rewrite into. IntPtr.Zero means "resolve at click time from
        /// the foreground tracker" — used by the tray menu, which is rebuilt
        /// once at startup and must not capture a stale window handle.
        /// </param>
        private void PopulateStyleItems(ToolStripItemCollection into, IntPtr target)
        {
            into.Clear();
            for (int i = 0; i < styles.Count; i++)
            {
                StyleInfo style = styles[i];
                ToolStripMenuItem item = new ToolStripMenuItem(style.Label);
                item.ToolTipText = style.Hint;
                string id = style.Id; // capture per-iteration, not the loop var
                IntPtr fixedTarget = target;
                item.Click += delegate
                {
                    ImproveSelection(id, fixedTarget != IntPtr.Zero ? fixedTarget : foreground.LastExternal);
                };
                into.Add(item);
            }
        }

        private async void RefreshStylesAsync()
        {
            try
            {
                List<StyleInfo> fetched = await daemon.GetStylesAsync();
                if (fetched != null && fetched.Count > 0)
                {
                    styles = fetched;
                    ToolStripItem[] found = tray.ContextMenuStrip.Items.Find("stylesRoot", true);
                    if (found.Length > 0)
                    {
                        PopulateStyleItems(((ToolStripMenuItem)found[0]).DropDownItems, IntPtr.Zero);
                    }
                }
            }
            catch
            {
                // Daemon down at startup is normal — the CLI starts it right
                // after this process. The fallback list keeps the menu usable.
            }
        }

        // ------------------------------------------------------------ hotkeys

        private void RegisterAll()
        {
            TryRegister(HotkeyImprove, config.HotkeyImprove);
            TryRegister(HotkeyCompose, config.HotkeyCompose);
            TryRegister(HotkeyStyles, config.HotkeyStyles);
        }

        private void TryRegister(int id, string combo)
        {
            uint mods, vk;
            if (!Hotkey.Parse(combo, out mods, out vk))
            {
                Tray.Balloon("Promptsmith", "Could not understand the hotkey \"" + combo +
                    "\" in config.json. Using no hotkey for that action.", ToolTipIcon.Warning);
                return;
            }
            if (Native.RegisterHotKey(hotkeys.Handle, id, mods, vk))
            {
                Log.Write("registered hotkey " + combo + " (id " + id + ")");
            }
            else
            {
                Log.Write("FAILED to register " + combo + " (id " + id + ") err=" +
                          Marshal.GetLastWin32Error());
                Tray.Balloon("Promptsmith",
                    combo + " is already claimed by another program, so that action has no hotkey. " +
                    "Change it in config.json (tray menu \u2192 Open config folder).",
                    ToolTipIcon.Warning);
            }
        }

        private void UnregisterAll()
        {
            Native.UnregisterHotKey(hotkeys.Handle, HotkeyImprove);
            Native.UnregisterHotKey(hotkeys.Handle, HotkeyCompose);
            Native.UnregisterHotKey(hotkeys.Handle, HotkeyStyles);
        }

        private void OnHotkey(int id)
        {
            try
            {
                Log.Write("hotkey " + id + " received");
                if (id == HotkeyImprove) { ImproveSelection(null, Native.GetForegroundWindow()); }
                else if (id == HotkeyCompose) { ShowCompose(Native.GetForegroundWindow()); }
                else if (id == HotkeyStyles) { ShowStylePicker(); }
            }
            catch (Exception ex)
            {
                Tray.Balloon("Promptsmith error", ex.Message, ToolTipIcon.Error);
            }
        }

        private void ShowStylePicker()
        {
            // Read the foreground window BEFORE showing the menu. Displaying it
            // makes the menu itself the foreground window, so asking afterwards
            // would hand us our own popup to paste into.
            IntPtr target = Native.GetForegroundWindow();

            // One reusable menu rather than a fresh one per press: disposing a
            // ToolStripDropDown from its own Closed event races the pending
            // Click, and not disposing leaks a window handle every time.
            if (stylePicker == null)
            {
                stylePicker = new ContextMenuStrip();
                stylePicker.ShowImageMargin = false;
            }
            PopulateStyleItems(stylePicker.Items, target);
            stylePicker.Show(Cursor.Position);
        }

        // -------------------------------------------------- the main pathway

        /// <param name="target">
        /// The window holding the selection. Passed in rather than read here,
        /// because every menu-driven entry point has already taken focus away
        /// from it by the time this runs.
        /// </param>
        private async void ImproveSelection(string style, IntPtr target)
        {
            if (busy) { return; }
            string chosen = style != null ? style : lastStyle;

            if (target == IntPtr.Zero) { target = Native.GetForegroundWindow(); }

            // A terminal is a special case in two ways, and both point the same
            // direction. First, sending Ctrl+C to a console with no selection
            // is SIGINT — it would kill whatever the user is running. Second,
            // even with a selection, pasting cannot *replace* a shell input
            // line, so in-place rewriting is meaningless there. Route straight
            // to the compose window instead.
            Log.Write("improve: style=" + chosen + " target=" + target.ToInt64().ToString("X") +
                      " class=" + Native.ClassNameOf(target));
            if (Native.IsTerminalWindow(target))
            {
                Log.Write("improve: target is a terminal, opening compose instead");
                ShowCompose(target);
                return;
            }

            busy = true;
            SetWorking(true);
            string savedClipboard = null;
            bool clipboardWasText = false;

            try
            {
                clipboardWasText = ClipboardSafe.TryGetText(out savedClipboard);

                // Ctrl+C goes to whatever has focus. If we arrived via a menu,
                // that is the menu — put the user's window back in front first,
                // or we would copy nothing and report "no selection".
                if (Native.GetForegroundWindow() != target)
                {
                    Native.ForceForeground(target);
                    await Task.Delay(120);
                }

                string selection = await CaptureSelectionAsync();
                Log.Write("improve: captured " + (selection == null ? "NOTHING" : selection.Length + " chars"));
                if (selection == null)
                {
                    // Nothing was selected. Not an error — fall through to the
                    // compose window, which is what the user probably wanted.
                    SetWorking(false);
                    ShowCompose(target);
                    return;
                }

                string rewritten = await daemon.RewriteAsync(selection, chosen);
                Log.Write("improve: daemon returned " + rewritten.Length + " chars");
                lastStyle = chosen;

                if (!ClipboardSafe.TrySetText(rewritten))
                {
                    Tray.Balloon("Promptsmith", "Another program is holding the clipboard; could not paste.",
                        ToolTipIcon.Warning);
                    return;
                }

                Log.Write("paste: fg before=" + Native.GetForegroundWindow().ToInt64().ToString("X") +
                          " target=" + target.ToInt64().ToString("X"));
                Native.ForceForeground(target);
                await Task.Delay(90);

                string onClipboard;
                ClipboardSafe.TryGetText(out onClipboard);
                Log.Write("paste: fg after=" + Native.GetForegroundWindow().ToInt64().ToString("X") +
                          " clipboard=" + (onClipboard == null ? "NULL" : onClipboard.Length + " chars") +
                          " match=" + (onClipboard == rewritten));

                uint sent = Input.SendChord(Native.VK_CONTROL, Native.VK_V);
                Log.Write("paste: SendInput accepted " + sent + "/4 events" +
                          (sent < 4 ? " err=" + Marshal.GetLastWin32Error() : ""));

                // Give the target time to read the clipboard before we put the
                // user's own content back. Restoring too early pastes nothing.
                await Task.Delay(450);
            }
            catch (DaemonDownException)
            {
                Log.Write("improve: DAEMON DOWN");
                Tray.Balloon("Promptsmith",
                    "The Promptsmith daemon is not running. Start it with:  promptsmith start",
                    ToolTipIcon.Error);
            }
            catch (Exception ex)
            {
                // An error must never end up pasted into the user's document.
                Log.Write("improve: ERROR " + ex.GetType().Name + ": " + ex.Message);
                Tray.Balloon("Promptsmith", ex.Message, ToolTipIcon.Error);
            }
            finally
            {
                if (clipboardWasText && savedClipboard != null)
                {
                    ClipboardSafe.TrySetText(savedClipboard);
                }
                busy = false;
                SetWorking(false);
            }
        }

        /// <summary>
        /// Copies the current selection out of the focused window.
        /// Returns null when nothing was selected.
        /// </summary>
        private async Task<string> CaptureSelectionAsync()
        {
            // The user is physically holding Ctrl+Alt right now — that is how
            // they triggered us. Synthesising Ctrl+C on top of held Alt makes
            // the target see Ctrl+Alt+C, which copies in almost nothing. Let go
            // of their modifiers first. This is the single most important line
            // in the file; without it the tool appears to do nothing at all.
            Input.ReleaseHeldModifiers();
            await Task.Delay(40);

            uint before = Native.GetClipboardSequenceNumber();
            Log.Write("capture: sending Ctrl+C, clipboard seq=" + before);
            Input.SendChord(Native.VK_CONTROL, Native.VK_C);

            // Await rather than Sleep: this runs on the UI thread, and blocking
            // it would freeze the message pump the clipboard notification and
            // the tray icon both depend on.
            for (int waited = 0; waited < 1200; waited += 25)
            {
                await Task.Delay(25);
                if (Native.GetClipboardSequenceNumber() != before)
                {
                    string text;
                    if (ClipboardSafe.TryGetText(out text) && text.Trim().Length > 0)
                    {
                        return text;
                    }
                    return null; // copied something non-textual
                }
            }
            Log.Write("capture: clipboard never changed within 1200ms - nothing was selected");
            return null;
        }

        private void SetWorking(bool working)
        {
            tray.Text = working ? "Promptsmith: rewriting\u2026" : (paused ? "Promptsmith (paused)" : "Promptsmith");
        }

        // ------------------------------------------------------------ compose

        private void ShowCompose(IntPtr returnTo)
        {
            if (compose == null || compose.IsDisposed)
            {
                compose = new ComposeForm(daemon, styles);
            }
            compose.Open(returnTo, lastStyle);
        }

        // ----------------------------------------------------------- teardown

        private void Shutdown()
        {
            try { UnregisterAll(); }
            catch { }
            if (tray != null)
            {
                // Without an explicit Dispose the icon lingers in the tray as a
                // ghost until the user hovers over it.
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }
            if (foreground != null) { foreground.Dispose(); }
            if (hotkeys != null) { hotkeys.DestroyHandle(); }
            ExitThread();
        }
    }

    // -------------------------------------------------------- hotkey receiver

    /// <summary>
    /// Receives WM_HOTKEY. A NativeWindow rather than a hidden Form: a Form
    /// brings a window class, an icon, taskbar/Alt-Tab participation and a
    /// paint cycle we would spend the rest of the file suppressing. All we need
    /// is an HWND with a WndProc.
    /// </summary>
    internal sealed class HotkeyWindow : NativeWindow
    {
        private const int WM_HOTKEY = 0x0312;
        private readonly Action<int> onHotkey;

        public HotkeyWindow(Action<int> handler)
        {
            onHotkey = handler;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                onHotkey(m.WParam.ToInt32());
            }
            base.WndProc(ref m);
        }
    }

    // ------------------------------------------------------ foreground memory

    /// <summary>
    /// Remembers the last real application window the user was in.
    ///
    /// Needed because a tray icon click moves focus to the taskbar, and a popup
    /// menu moves it to the popup — so by the time any menu-driven action runs,
    /// GetForegroundWindow() no longer points at the user's text. A foreground
    /// WinEvent is the only way to know where they were a moment ago.
    ///
    /// WINEVENT_OUTOFCONTEXT means Windows posts events to our message loop
    /// rather than injecting this process into every other one.
    /// </summary>
    internal sealed class ForegroundTracker : IDisposable
    {
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

        private readonly IntPtr hook;
        // The delegate must be held in a field. If it is only passed to
        // SetWinEventHook the GC will collect it while Windows still holds the
        // pointer, and the process dies on the next foreground change.
        private readonly Native.WinEventDelegate callback;
        private readonly uint ownProcessId;

        public IntPtr LastExternal { get; private set; }

        public ForegroundTracker()
        {
            ownProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            callback = OnForegroundChanged;
            hook = Native.SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, callback, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            LastExternal = Native.GetForegroundWindow();
        }

        private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hWnd,
                                         int objectId, int childId, uint thread, uint time)
        {
            // objectId 0 is OBJID_WINDOW; anything else is a control inside one.
            if (hWnd == IntPtr.Zero || objectId != 0) { return; }

            uint processId;
            Native.GetWindowThreadProcessId(hWnd, out processId);
            if (processId == ownProcessId) { return; }

            if (IsShellSurface(hWnd)) { return; }
            LastExternal = hWnd;
        }

        /// <summary>
        /// The taskbar, desktop and notification overflow are not places a user
        /// edits text, and they are exactly what takes focus when a tray icon is
        /// clicked — so recording them would defeat the purpose of this class.
        /// </summary>
        private static bool IsShellSurface(IntPtr hWnd)
        {
            StringBuilder buffer = new StringBuilder(256);
            if (Native.GetClassName(hWnd, buffer, buffer.Capacity) == 0) { return true; }
            string className = buffer.ToString();
            return className == "Shell_TrayWnd"
                || className == "Shell_SecondaryTrayWnd"
                || className == "NotifyIconOverflowWindow"
                || className == "TopLevelWindowForOverflowXamlIsland"
                || className == "Progman"
                || className == "WorkerW"
                || className == "Windows.UI.Core.CoreWindow"; // Start menu, search
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero) { Native.UnhookWinEvent(hook); }
            GC.KeepAlive(callback);
        }
    }

    // ----------------------------------------------------------- compose form

    internal sealed class ComposeForm : Form
    {
        private readonly DaemonClient daemon;
        private readonly TextBox input;
        private readonly TextBox output;
        private readonly ComboBox stylePicker;
        private readonly Button rewriteButton;
        private readonly Button pasteButton;
        private readonly Label status;
        private IntPtr returnTo;

        public ComposeForm(DaemonClient client, List<StyleInfo> styles)
        {
            daemon = client;

            Text = "Promptsmith";
            Icon = TrayIconFactory.Create(false);
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(640, 460);
            MinimumSize = new Size(460, 340);
            KeyPreview = true;
            Font = new Font("Segoe UI", 9.75f);
            BackColor = Color.FromArgb(250, 250, 250);

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.Padding = new Padding(12);
            grid.ColumnCount = 1;
            grid.RowCount = 5;
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 42f));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 58f));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            input = new TextBox();
            input.Multiline = true;
            input.ScrollBars = ScrollBars.Vertical;
            input.Dock = DockStyle.Fill;
            input.AcceptsReturn = true;
            input.Font = new Font("Consolas", 10f);
            input.KeyDown += OnInputKeyDown;
            grid.Controls.Add(input, 0, 0);

            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.AutoSize = true;
            bar.Padding = new Padding(0, 8, 0, 8);
            bar.WrapContents = false;

            stylePicker = new ComboBox();
            stylePicker.DropDownStyle = ComboBoxStyle.DropDownList;
            stylePicker.Width = 170;
            for (int i = 0; i < styles.Count; i++)
            {
                stylePicker.Items.Add(styles[i]);
            }
            if (stylePicker.Items.Count > 0) { stylePicker.SelectedIndex = 0; }
            bar.Controls.Add(stylePicker);

            rewriteButton = new Button();
            rewriteButton.Text = "Rewrite  (Ctrl+Enter)";
            rewriteButton.AutoSize = true;
            rewriteButton.Margin = new Padding(8, 0, 0, 0);
            rewriteButton.Click += delegate { RunRewrite(); };
            bar.Controls.Add(rewriteButton);

            status = new Label();
            status.AutoSize = true;
            status.Margin = new Padding(12, 6, 0, 0);
            status.ForeColor = Color.FromArgb(110, 110, 110);
            bar.Controls.Add(status);

            grid.Controls.Add(bar, 0, 1);

            output = new TextBox();
            output.Multiline = true;
            output.ReadOnly = true;
            output.ScrollBars = ScrollBars.Vertical;
            output.Dock = DockStyle.Fill;
            output.Font = new Font("Consolas", 10f);
            output.BackColor = Color.White;
            grid.Controls.Add(output, 0, 2);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.AutoSize = true;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.Padding = new Padding(0, 8, 0, 0);

            Button close = new Button();
            close.Text = "Close";
            close.AutoSize = true;
            close.Click += delegate { Hide(); };
            actions.Controls.Add(close);

            Button copy = new Button();
            copy.Text = "Copy";
            copy.AutoSize = true;
            copy.Click += delegate { CopyOut(false); };
            actions.Controls.Add(copy);

            pasteButton = new Button();
            pasteButton.Text = "Paste into last app";
            pasteButton.AutoSize = true;
            pasteButton.Click += delegate { CopyOut(true); };
            actions.Controls.Add(pasteButton);

            grid.Controls.Add(actions, 0, 3);

            Label hint = new Label();
            hint.AutoSize = true;
            hint.ForeColor = Color.FromArgb(130, 130, 130);
            hint.Text = "Esc closes.  Nothing here leaves your machine except the text you rewrite.";
            grid.Controls.Add(hint, 0, 4);

            Controls.Add(grid);

            // Esc closes. Handled at the form because KeyPreview is on.
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { Hide(); }
            };
        }

        public void Open(IntPtr previousWindow, string preferredStyle)
        {
            returnTo = previousWindow;
            pasteButton.Enabled = previousWindow != IntPtr.Zero;

            for (int i = 0; i < stylePicker.Items.Count; i++)
            {
                StyleInfo s = stylePicker.Items[i] as StyleInfo;
                if (s != null && s.Id == preferredStyle) { stylePicker.SelectedIndex = i; break; }
            }

            if (input.Text.Length == 0)
            {
                string clip;
                if (ClipboardSafe.TryGetText(out clip) && clip.Trim().Length > 0 && clip.Length < 12000)
                {
                    input.Text = clip;
                }
            }

            // Centre on whichever monitor the mouse is on, not the primary one.
            Screen screen = Screen.FromPoint(Cursor.Position);
            Location = new Point(
                screen.WorkingArea.X + (screen.WorkingArea.Width - Width) / 2,
                screen.WorkingArea.Y + (screen.WorkingArea.Height - Height) / 2);

            Show();
            Native.ForceForeground(Handle);
            input.Focus();
            input.SelectAll();
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
            string text = input.Text.Trim();
            if (text.Length == 0) { return; }

            StyleInfo style = stylePicker.SelectedItem as StyleInfo;
            string styleId = style != null ? style.Id : "improve";

            rewriteButton.Enabled = false;
            rewriteButton.Text = "Rewriting\u2026";
            status.Text = "";
            try
            {
                DateTime started = DateTime.UtcNow;
                string result = await daemon.RewriteAsync(text, styleId);
                output.Text = result;
                status.Text = ((int)(DateTime.UtcNow - started).TotalMilliseconds) + " ms";
            }
            catch (DaemonDownException)
            {
                output.Text = "";
                status.Text = "Daemon not running \u2014 start it with:  promptsmith start";
            }
            catch (Exception ex)
            {
                output.Text = "";
                status.Text = ex.Message;
            }
            finally
            {
                rewriteButton.Enabled = true;
                rewriteButton.Text = "Rewrite  (Ctrl+Enter)";
            }
        }

        private async void CopyOut(bool paste)
        {
            if (output.Text.Length == 0) { return; }
            if (!ClipboardSafe.TrySetText(output.Text))
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

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Closing the window should not kill the tray app.
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }
    }

    // ---------------------------------------------------------- daemon client

    internal sealed class DaemonDownException : Exception
    {
        public DaemonDownException() : base("daemon unreachable") { }
    }

    internal sealed class DaemonClient
    {
        private readonly HttpClient http;

        public DaemonClient(int port)
        {
            http = new HttpClient();
            http.BaseAddress = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/");
            http.Timeout = TimeSpan.FromSeconds(60);
        }

        public async Task<string> RewriteAsync(string text, string style)
        {
            string body = "{\"text\":" + Json.Quote(text) + ",\"style\":" + Json.Quote(style) + "}";
            HttpResponseMessage response;
            try
            {
                StringContent content = new StringContent(body, Encoding.UTF8, "application/json");
                response = await http.PostAsync("rewrite", content);
            }
            catch (HttpRequestException)
            {
                throw new DaemonDownException();
            }
            catch (TaskCanceledException)
            {
                throw new Exception("The rewrite timed out after 60 seconds.");
            }

            string payload = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                string error = Json.StringField(payload, "error");
                throw new Exception(error != null ? error
                    : "Daemon returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }

            string result = Json.StringField(payload, "result");
            if (result == null || result.Length == 0)
            {
                throw new Exception("Daemon returned an empty rewrite.");
            }
            return result;
        }

        public async Task<List<StyleInfo>> GetStylesAsync()
        {
            try
            {
                HttpResponseMessage response = await http.GetAsync("styles");
                if (!response.IsSuccessStatusCode) { return null; }
                string payload = await response.Content.ReadAsStringAsync();
                return StyleInfo.Parse(payload);
            }
            catch (HttpRequestException)
            {
                return null;
            }
            catch (TaskCanceledException)
            {
                return null;
            }
        }
    }

    internal sealed class StyleInfo
    {
        public string Id;
        public string Label;
        public string Hint;

        public StyleInfo(string id, string label, string hint)
        {
            Id = id; Label = label; Hint = hint;
        }

        // Shown in the ComboBox.
        public override string ToString() { return Label; }

        /// <summary>Used until the daemon answers, and if it never does.</summary>
        public static List<StyleInfo> Fallback()
        {
            List<StyleInfo> list = new List<StyleInfo>();
            list.Add(new StyleInfo("improve", "Improve", ""));
            list.Add(new StyleInfo("concise", "Concise", ""));
            list.Add(new StyleInfo("technical", "Technical spec", ""));
            list.Add(new StyleInfo("code", "Code", ""));
            list.Add(new StyleInfo("reasoning", "Reasoning", ""));
            list.Add(new StyleInfo("creative", "Creative", ""));
            list.Add(new StyleInfo("socratic", "Ask me first", ""));
            return list;
        }

        /// <summary>
        /// Pulls {"id":..,"label":..,"hint":..} objects out of the /styles
        /// response by walking object braces. Deliberately not a real parser —
        /// see the note on the Json class.
        /// </summary>
        public static List<StyleInfo> Parse(string json)
        {
            List<StyleInfo> list = new List<StyleInfo>();
            if (json == null) { return list; }
            int i = 0;
            while (true)
            {
                int idAt = json.IndexOf("\"id\"", i, StringComparison.Ordinal);
                if (idAt < 0) { break; }
                int objectEnd = json.IndexOf('}', idAt);
                if (objectEnd < 0) { break; }
                string slice = json.Substring(idAt, objectEnd - idAt);
                string id = Json.StringField(slice, "id");
                string label = Json.StringField(slice, "label");
                string hint = Json.StringField(slice, "hint");
                if (id != null && id.Length > 0)
                {
                    list.Add(new StyleInfo(id, label != null && label.Length > 0 ? label : id, hint != null ? hint : ""));
                }
                i = objectEnd + 1;
            }
            return list;
        }
    }

    // -------------------------------------------------------------- minimal JSON
    //
    // Hand-rolled on purpose. The alternative on .NET Framework is
    // JavaScriptSerializer (System.Web.Extensions, an obsolete assembly) or a
    // NuGet package — and a package would mean the build no longer works with
    // just csc.exe, which is the whole point of this project's build story. The
    // shapes exchanged with the daemon are fixed and tiny, so a reader that
    // handles exactly "find this key, unescape its string value" is enough.
    // It is not a general JSON parser and must not be used as one.

    internal static class Json
    {
        public static string Quote(string value)
        {
            StringBuilder sb = new StringBuilder(value.Length + 16);
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ' || c == '\u2028' || c == '\u2029')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Value of "key" as a string, or null. Handles escapes.</summary>
        public static string StringField(string json, string key)
        {
            int valueStart = FindValue(json, key);
            if (valueStart < 0 || valueStart >= json.Length || json[valueStart] != '"') { return null; }

            StringBuilder sb = new StringBuilder();
            for (int i = valueStart + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"') { return sb.ToString(); }
                if (c != '\\') { sb.Append(c); continue; }

                i++;
                if (i >= json.Length) { break; }
                char esc = json[i];
                switch (esc)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '/': sb.Append('/'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'u':
                        if (i + 4 < json.Length)
                        {
                            int code;
                            if (int.TryParse(json.Substring(i + 1, 4), NumberStyles.HexNumber,
                                             CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                            }
                            i += 4;
                        }
                        break;
                    default: sb.Append(esc); break;
                }
            }
            return null; // unterminated string
        }

        /// <summary>Value of "key" as an int, or `fallback`.</summary>
        public static int IntField(string json, string key, int fallback)
        {
            int valueStart = FindValue(json, key);
            if (valueStart < 0) { return fallback; }
            int end = valueStart;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) { end++; }
            if (end == valueStart) { return fallback; }
            int parsed;
            if (int.TryParse(json.Substring(valueStart, end - valueStart),
                             NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            return fallback;
        }

        /// <summary>Index of the first non-space character after "key": </summary>
        private static int FindValue(string json, string key)
        {
            if (json == null) { return -1; }
            int at = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (at < 0) { return -1; }
            int colon = json.IndexOf(':', at + key.Length + 2);
            if (colon < 0) { return -1; }
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) { i++; }
            return i;
        }
    }

    // --------------------------------------------------------------- config

    internal sealed class Config
    {
        public int Port = 8477;
        public string HotkeyImprove = "Ctrl+Alt+P";
        public string HotkeyCompose = "Ctrl+Alt+O";
        public string HotkeyStyles = "Ctrl+Alt+L";

        public static string Folder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Promptsmith");
            }
        }

        public static string File_ { get { return Path.Combine(Folder, "config.json"); } }

        /// <summary>
        /// A malformed config must never stop the app from starting — the user
        /// would have a broken tool and no obvious way to fix it. Every field
        /// falls back independently.
        /// </summary>
        public static Config Load()
        {
            Config config = new Config();
            try
            {
                if (!System.IO.File.Exists(File_)) { return config; }
                string json = System.IO.File.ReadAllText(File_);

                config.Port = Json.IntField(json, "port", config.Port);
                if (config.Port < 1 || config.Port > 65535) { config.Port = 8477; }

                config.HotkeyImprove = Or(Json.StringField(json, "hotkeyImprove"), config.HotkeyImprove);
                config.HotkeyCompose = Or(Json.StringField(json, "hotkeyCompose"), config.HotkeyCompose);
                config.HotkeyStyles = Or(Json.StringField(json, "hotkeyStyles"), config.HotkeyStyles);
            }
            catch
            {
                return new Config();
            }
            return config;
        }

        private static string Or(string value, string fallback)
        {
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
    }

    internal static class Hotkey
    {
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        // Without this the hotkey auto-repeats while held down, firing a dozen
        // rewrites from one press.
        private const uint MOD_NOREPEAT = 0x4000;

        public static bool Parse(string combo, out uint mods, out uint vk)
        {
            mods = MOD_NOREPEAT;
            vk = 0;
            if (string.IsNullOrEmpty(combo)) { return false; }

            string[] parts = combo.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0) { continue; }
                string lower = part.ToLowerInvariant();

                if (lower == "ctrl" || lower == "control") { mods |= MOD_CONTROL; }
                else if (lower == "alt") { mods |= MOD_ALT; }
                else if (lower == "shift") { mods |= MOD_SHIFT; }
                else if (lower == "win" || lower == "windows") { mods |= MOD_WIN; }
                else if (part.Length == 1 && ((part[0] >= 'A' && part[0] <= 'Z') || (part[0] >= 'a' && part[0] <= 'z')))
                {
                    vk = (uint)char.ToUpperInvariant(part[0]);
                }
                else if (part.Length == 1 && part[0] >= '0' && part[0] <= '9')
                {
                    vk = (uint)part[0];
                }
                else if (lower.Length >= 2 && lower[0] == 'f')
                {
                    int n;
                    if (int.TryParse(lower.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
                        && n >= 1 && n <= 24)
                    {
                        vk = (uint)(0x70 + (n - 1)); // VK_F1 .. VK_F24
                    }
                    else { return false; }
                }
                else if (lower == "space") { vk = 0x20; }
                else { return false; }
            }

            // A modifier-only combo would fire on every Ctrl press.
            return vk != 0 && (mods & ~MOD_NOREPEAT) != 0;
        }
    }

    // ---------------------------------------------------- clipboard + input

    internal static class ClipboardSafe
    {
        // The clipboard is a single global resource with no lock discipline;
        // any app can hold it open mid-operation and every call here throws
        // ExternalException when that happens. Retrying is the documented
        // remedy and in practice one or two attempts is always enough.
        private const int Attempts = 10;
        private const int PauseMs = 40;

        public static bool TryGetText(out string text)
        {
            text = null;
            for (int i = 0; i < Attempts; i++)
            {
                try
                {
                    if (!Clipboard.ContainsText()) { return false; }
                    text = Clipboard.GetText();
                    return text != null;
                }
                catch (ExternalException) { Thread.Sleep(PauseMs); }
                catch (ThreadStateException) { return false; }
            }
            return false;
        }

        public static bool TrySetText(string text)
        {
            if (text == null) { return false; }
            for (int i = 0; i < Attempts; i++)
            {
                try
                {
                    if (text.Length == 0) { Clipboard.Clear(); } else { Clipboard.SetText(text); }
                    return true;
                }
                catch (ExternalException) { Thread.Sleep(PauseMs); }
                catch (ThreadStateException) { return false; }
            }
            return false;
        }
    }

    internal static class Input
    {
        /// <summary>Press modifier+key and release both, as one atomic batch.</summary>
        public static uint SendChord(ushort modifier, ushort key)
        {
            Native.INPUT[] inputs = new Native.INPUT[4];
            inputs[0] = Key(modifier, false);
            inputs[1] = Key(key, false);
            inputs[2] = Key(key, true);
            inputs[3] = Key(modifier, true);
            // One SendInput call, not four: the batch cannot be interleaved with
            // the user's own keystrokes, which would scramble the chord.
            return Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        /// <summary>
        /// Synthesise key-up for every modifier the user is still physically
        /// holding. Called before sending a chord, because the hotkey that got
        /// us here is itself a held modifier combination.
        /// </summary>
        public static void ReleaseHeldModifiers()
        {
            ushort[] modifiers = new ushort[]
            {
                Native.VK_LCONTROL, Native.VK_RCONTROL,
                Native.VK_LMENU,    Native.VK_RMENU,
                Native.VK_LSHIFT,   Native.VK_RSHIFT,
                Native.VK_LWIN,     Native.VK_RWIN,
            };

            List<Native.INPUT> release = new List<Native.INPUT>();
            for (int i = 0; i < modifiers.Length; i++)
            {
                if ((Native.GetAsyncKeyState(modifiers[i]) & 0x8000) != 0)
                {
                    release.Add(Key(modifiers[i], true));
                }
            }
            if (release.Count > 0)
            {
                Native.INPUT[] batch = release.ToArray();
                Native.SendInput((uint)batch.Length, batch, Marshal.SizeOf(typeof(Native.INPUT)));
            }
        }

        private static Native.INPUT Key(ushort vk, bool up)
        {
            Native.INPUT input = new Native.INPUT();
            input.type = Native.INPUT_KEYBOARD;
            input.U.ki.wVk = vk;
            input.U.ki.wScan = 0;
            input.U.ki.dwFlags = up ? Native.KEYEVENTF_KEYUP : 0u;
            input.U.ki.time = 0;
            input.U.ki.dwExtraInfo = IntPtr.Zero;
            return input;
        }
    }

    // ------------------------------------------------------------ tray helper

    /// <summary>
    /// Append-only log at %APPDATA%\Promptsmith\host.log.
    ///
    /// A tray app has no console and no window most of the time, so when a
    /// hotkey silently does nothing there is otherwise no way at all to find
    /// out where it stopped — was the hotkey even received, did the copy come
    /// back empty, did the daemon answer? Every step below writes one line.
    /// </summary>
    internal static class Log
    {
        private static readonly object gate = new object();
        private static string file;

        public static void Write(string message)
        {
            try
            {
                lock (gate)
                {
                    if (file == null)
                    {
                        Directory.CreateDirectory(Config.Folder);
                        file = Path.Combine(Config.Folder, "host.log");
                        // Truncate on each launch: the interesting history is
                        // always the current session, and an unbounded log in
                        // an always-on app is a slow disk leak.
                        System.IO.File.WriteAllText(file, "");
                    }
                    System.IO.File.AppendAllText(file,
                        DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + message + "\r\n");
                }
            }
            catch
            {
                // Logging must never be the thing that breaks the app.
            }
        }
    }

    internal static class Tray
    {
        private static NotifyIcon icon;

        public static void Attach(NotifyIcon notifyIcon) { icon = notifyIcon; }

        public static void Balloon(string title, string message, ToolTipIcon kind)
        {
            if (icon == null) { return; }
            icon.BalloonTipTitle = title;
            icon.BalloonTipText = message.Length > 250 ? message.Substring(0, 247) + "..." : message;
            icon.BalloonTipIcon = kind;
            icon.ShowBalloonTip(4000);
        }
    }

    internal static class TrayIconFactory
    {
        /// <summary>
        /// Draws the icon rather than shipping an .ico, so the build stays a
        /// single csc invocation over a single source file with no resources.
        /// </summary>
        public static Icon Create(bool paused)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                    Color fill = paused ? Color.FromArgb(120, 120, 120) : Color.FromArgb(196, 92, 60);
                    using (GraphicsPath path = RoundedRect(new Rectangle(1, 1, 30, 30), 8))
                    using (SolidBrush brush = new SolidBrush(fill))
                    {
                        g.FillPath(brush, path);
                    }

                    using (Font font = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush text = new SolidBrush(Color.White))
                    using (StringFormat format = new StringFormat())
                    {
                        format.Alignment = StringAlignment.Center;
                        format.LineAlignment = StringAlignment.Center;
                        g.DrawString("P", font, text, new RectangleF(0, 0, 32, 33), format);
                    }
                }

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    // Clone: the Icon returned by FromHandle does not own the
                    // HICON, so it would dangle once we destroy it below.
                    using (Icon temporary = Icon.FromHandle(handle))
                    {
                        return (Icon)temporary.Clone();
                    }
                }
                finally
                {
                    Native.DestroyIcon(handle);
                }
            }
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    // ---------------------------------------------------------------- P/Invoke

    internal static class Native
    {
        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        public const ushort VK_CONTROL = 0x11;
        public const ushort VK_C = 0x43;
        public const ushort VK_V = 0x56;
        public const ushort VK_LSHIFT = 0xA0;
        public const ushort VK_RSHIFT = 0xA1;
        public const ushort VK_LCONTROL = 0xA2;
        public const ushort VK_RCONTROL = 0xA3;
        public const ushort VK_LMENU = 0xA4;
        public const ushort VK_RMENU = 0xA5;
        public const ushort VK_LWIN = 0x5B;
        public const ushort VK_RWIN = 0x5C;

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        // The three input kinds overlap in memory. MOUSEINPUT is the largest,
        // so it sets the union's size: 40 bytes total on x64, 28 on x86 — which
        // is why the cbSize argument to SendInput must come from Marshal.SizeOf
        // and never from a hardcoded constant.
        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr handle);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hWnd,
                                              int objectId, int childId, uint eventThread, uint eventTime);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
                                                    WinEventDelegate callback, uint processId, uint threadId,
                                                    uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        /// <summary>
        /// SetForegroundWindow is refused unless the calling process already
        /// owns the foreground, which a background tray app usually does not.
        /// Briefly attaching to the target's input queue makes the two threads
        /// share foreground state, which lifts the restriction. Ugly, and the
        /// only reliable way to do this.
        /// </summary>
        public static void ForceForeground(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) { return; }
            if (GetForegroundWindow() == hWnd) { return; }

            uint dummy;
            uint targetThread = GetWindowThreadProcessId(hWnd, out dummy);
            uint thisThread = GetCurrentThreadId();

            if (targetThread == thisThread || targetThread == 0)
            {
                SetForegroundWindow(hWnd);
                return;
            }

            bool attached = AttachThreadInput(thisThread, targetThread, true);
            try
            {
                SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attached) { AttachThreadInput(thisThread, targetThread, false); }
            }
        }

        /// <summary>
        /// Is this window a console or terminal? Matched on window class, which
        /// is what the host actually registers, rather than on a process-name
        /// list that would go stale with every new terminal emulator.
        /// </summary>
        public static string ClassNameOf(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) { return "(none)"; }
            StringBuilder buffer = new StringBuilder(256);
            if (GetClassName(hWnd, buffer, buffer.Capacity) == 0) { return "(unknown)"; }
            return buffer.ToString();
        }

        public static bool IsTerminalWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) { return false; }
            string className = ClassNameOf(hWnd);

            return className == "ConsoleWindowClass"            // conhost: cmd, powershell
                || className == "PseudoConsoleWindow"           // conpty
                || className.StartsWith("CASCADIA_HOSTING_WINDOW", StringComparison.Ordinal) // Windows Terminal
                || className == "mintty"                        // Git Bash, Cygwin
                || className == "Alacritty"
                || className == "org.wezfurlong.wezterm";
        }
    }
}
