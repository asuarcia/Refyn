# End-to-end test of the hotkey path.
#
# Opens a scratch file in Notepad, selects it, presses the real global hotkey,
# and checks the text was replaced by a real rewrite of that text. This is the
# only way to exercise the parts that matter — SendInput, the modifier release,
# the clipboard round trip, the paste — because none of them work without a
# real focused window.
#
#   powershell -ExecutionPolicy Bypass -File test\e2e-hotkey.ps1
#
# Four things about Windows 11 that each produced a false result before being
# handled here. Left as a list because every one of them will bite again:
#
#  1. `notepad` on PATH may resolve to something else entirely (here, a Git
#     shell script), so Notepad opens THAT FILE. Always use the System32 path.
#  2. Notepad is a packaged app: Start-Process returns a stub whose Id and
#     MainWindowHandle are not the editor's. Find the real process by name.
#  3. Notepad restores its previous tabs. A test that types into "a fresh
#     Notepad" is actually typing into whatever was open last, so the run is
#     seeded from a known scratch file instead.
#  4. A newly launched window does not get the foreground while another app
#     holds it. Without an explicit grab the test drove the terminal that
#     launched it and reported a failure that had nothing to do with the app.
#
# And one about the assertions: "the text changed" is not a sufficient check for
# a rewriter. The daemon must have received OUR text, and the window must end up
# holding exactly what the daemon returned.

#
# THIS TEST TAKES OVER THE DESKTOP.
#
# It opens Notepad, forces it to the foreground, and synthesises keystrokes into
# it for about a minute. You cannot use the machine while it runs — anything you
# type goes into whatever it has focused. It is therefore opt-in:
#
#   powershell -ExecutionPolicy Bypass -File test\e2e-hotkey.ps1 -TakeOverMyDesktop
#
# Run it when you are away from the keyboard. For routine checks use the quiet
# suites instead, which touch nothing on screen:
#
#   node --test test\*.test.mjs        daemon logic
#   node test\smoke.mjs                daemon + host over HTTP, no windows
#
param([switch]$TakeOverMyDesktop)

$ErrorActionPreference = 'Stop'

if (-not $TakeOverMyDesktop) {
    Write-Host ""
    Write-Host "  This test hijacks the keyboard and focus for ~1 minute." -ForegroundColor Yellow
    Write-Host "  Re-run with -TakeOverMyDesktop when you are away from the machine." -ForegroundColor Yellow
    Write-Host "  For a quiet check: node --test test\*.test.mjs  and  node test\smoke.mjs" -ForegroundColor DarkGray
    Write-Host ""
    exit 2
}

Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Focus {
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool c);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    static extern bool SystemParametersInfoGet(uint action, uint param, ref uint value, uint winIni);

    const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
    const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
    const uint SPIF_SENDCHANGE = 0x0002;

    // Windows enforces a "foreground lock timeout" — a window cannot be pushed
    // to the front shortly after the user interacted with a different app, no
    // matter what the caller does. AttachThreadInput alone does not defeat it.
    // Setting the timeout to zero for the duration of the test is the
    // documented way to make automated focus changes take effect.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);

    // Diagnostic: who actually owns the foreground right now.
    public static string ForegroundProcess() {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return "(none)";
        uint pid;
        GetWindowThreadProcessId(fg, out pid);
        var title = new System.Text.StringBuilder(200);
        GetWindowTextW(fg, title, 200);
        string name;
        try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { name = "pid" + pid; }
        return name + " '" + title.ToString() + "'";
    }

    // NOTE: the value goes in pvParam and uiParam must be zero. Passing it in
    // uiParam sets the timeout to 0 no matter what you intended — which is how
    // an earlier version of this file left the machine letting every app steal
    // focus, long after the test had finished.
    public static uint GetForegroundLockTimeout() {
        uint v = 0;
        SystemParametersInfoGet(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref v, 0);
        return v;
    }

    public static void SetForegroundLockTimeout(uint ms) {
        SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, new IntPtr(ms), SPIF_SENDCHANGE);
    }

    // Windows refuses SetForegroundWindow from a process that does not already
    // own the foreground. Attaching to the target's input queue makes the two
    // threads share foreground state, which lifts the restriction. Same trick
    // the host itself uses in Native.ForceForeground.
    public static bool Grab(IntPtr target) {
        if (target == IntPtr.Zero) return false;
        ShowWindow(target, 9); // SW_RESTORE
        uint targetPid;
        uint targetThread = GetWindowThreadProcessId(target, out targetPid);
        uint here = GetCurrentThreadId();

        for (int attempt = 0; attempt < 3; attempt++) {
            bool attached = AttachThreadInput(here, targetThread, true);
            try { SetForegroundWindow(target); }
            finally { if (attached) AttachThreadInput(here, targetThread, false); }
            System.Threading.Thread.Sleep(300);

            // Compare by owning PROCESS, not by handle. A packaged app such as
            // Windows 11 Notepad puts a different window in the foreground than
            // the one Process.MainWindowHandle reports, so an equality check on
            // handles reports failure even when focus landed correctly.
            uint fgPid;
            GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);
            if (fgPid == targetPid) return true;
        }
        return false;
    }
}
'@

$original   = "hey can u write me somthing that explains how dns works but like simple"
$port       = 8477
$notepadExe = Join-Path $env:WINDIR 'System32\notepad.exe'
# A unique name per run. Windows 11 Notepad restores unsaved tabs from previous
# sessions, so a fixed filename means run N opens run N-1's edited buffer - the
# title shows "*scratch.txt" and the content is last run's rewrite, not ours.
$scratch    = Join-Path $env:TEMP ("refyn-e2e-" + [guid]::NewGuid().ToString("N").Substring(0,8) + ".txt")

function Get-Clip {
    for ($i = 0; $i -lt 12; $i++) {
        try {
            $t = [System.Windows.Forms.Clipboard]::GetText()
            if ($t) { return $t }
        } catch { }
        Start-Sleep -Milliseconds 60
    }
    return ""
}

function Cleanup {
    Get-Process Notepad -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-ChildItem (Join-Path $env:TEMP "refyn-e2e-*.txt") -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    # Always put the foreground lock back. Leaving it at 0 lets every app on the
    # machine steal focus for the rest of the session, which outlives this test
    # and is not the test's to decide.
    if ($null -ne $script:savedLockTimeout) {
        [Focus]::SetForegroundLockTimeout($script:savedLockTimeout)
        $script:savedLockTimeout = $null
    }
}

# Runs even on Ctrl+C or an unhandled error, which a plain call at the end
# of the script would not.
trap { Cleanup; break }

# When no selection is found, Refyn opens its compose window — which is
# correct, but it is TopMost, so one left over from a previous run steals the
# foreground and makes the NEXT run fail for an unrelated reason. Close it by
# restarting the host, which is cheap and leaves no state behind.
function Reset-Host {
    if (Get-Process RefynHost -ErrorAction SilentlyContinue) {
        Stop-Process -Name RefynHost -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
    Start-Process (Join-Path $PSScriptRoot '../host/bin/RefynHost.exe') -WindowStyle Hidden
    Start-Sleep -Seconds 2
}

function Fail($message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    Write-Host "--- host.log (last 20) ---" -ForegroundColor DarkGray
    Get-Content (Join-Path $env:APPDATA 'Refyn\host.log') -ErrorAction SilentlyContinue | Select-Object -Last 20
    Cleanup
    exit 1
}

# --- preconditions ----------------------------------------------------------

Write-Host "0. checking daemon and host are up" -ForegroundColor Cyan
try {
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$port/health" -TimeoutSec 3
} catch {
    Fail "daemon is not responding on port $port - run: node refyn.mjs start"
}
# Always start from a known host state. Reset-Host both clears a compose window
# left over from a previous run and starts the host if it is not up, so this
# doubles as the "is it installed" check.
if (-not (Test-Path (Join-Path $PSScriptRoot '../host/bin/RefynHost.exe'))) {
    Fail "RefynHost.exe is not built - run: node refyn.mjs build"
}
Reset-Host
if (-not (Get-Process RefynHost -ErrorAction SilentlyContinue)) {
    Fail "RefynHost.exe would not stay running"
}
if (-not $health.keyConfigured) { Fail "daemon has no API key configured" }
$rewritesBefore = $health.rewrites
Write-Host "   daemon ok (model $($health.model), $rewritesBefore rewrites so far)" -ForegroundColor DarkGray
if (-not (Test-Path $notepadExe)) { Fail "no notepad.exe at $notepadExe" }

# --- drive the UI -----------------------------------------------------------

$script:savedLockTimeout = [Focus]::GetForegroundLockTimeout()
[Focus]::SetForegroundLockTimeout(0)

Write-Host "1. opening a scratch file in Notepad" -ForegroundColor Cyan
Get-Process Notepad -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
Set-Content -Path $scratch -Value $original -NoNewline -Encoding UTF8
Start-Process $notepadExe -ArgumentList "`"$scratch`""

$handle = [IntPtr]::Zero
for ($i = 0; $i -lt 24; $i++) {
    Start-Sleep -Milliseconds 300
    $editor = Get-Process Notepad -ErrorAction SilentlyContinue |
              Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
              Select-Object -First 1
    if ($editor) { $handle = [IntPtr]$editor.MainWindowHandle; break }
}
if ($handle -eq [IntPtr]::Zero) { Fail "Notepad never created a window" }
if (-not [Focus]::Grab($handle)) { Fail "could not bring Notepad to the foreground" }
Write-Host "   focused '$($editor.MainWindowTitle)' (hwnd $($handle.ToString('X')))" -ForegroundColor DarkGray

# A test that cannot prove it selected anything cannot conclude anything about
# the app. Earlier runs failed here and blamed Refyn, when the truth was
# that Ctrl+A never reached Notepad — the app was correctly reporting "nothing
# was selected". So: select, copy, and verify the clipboard holds our text
# BEFORE firing the hotkey. If this cannot be made to work, the harness is
# broken, and that is what gets reported.
Write-Host "2. selecting the text (and proving the selection took)" -ForegroundColor Cyan
# Re-grab and CONFIRM the foreground immediately before every keystroke. On a
# busy desktop another app (Edge, here) reclaims focus in the gap between the
# grab and the SendKeys, and the keys land in the wrong window - which is how
# earlier runs ended up selecting a single character out of a browser.
function Send-Verified($hwnd, $keys) {
    for ($try = 0; $try -lt 5; $try++) {
        if ([Focus]::Grab($hwnd)) {
            [System.Windows.Forms.SendKeys]::SendWait($keys)
            return $true
        }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

$primed = $false
for ($attempt = 1; $attempt -le 8; $attempt++) {
    [System.Windows.Forms.Clipboard]::SetText("__not-yet__")
    Start-Sleep -Milliseconds 200
    if (-not (Send-Verified $handle "^a")) { Start-Sleep -Milliseconds 500; continue }
    Start-Sleep -Milliseconds 400
    if (-not (Send-Verified $handle "^c")) { Start-Sleep -Milliseconds 500; continue }
    Start-Sleep -Milliseconds 600
    $got = (Get-Clip).Trim()
    if ($got -eq $original) { $primed = $true; break }
    $fg = [Focus]::ForegroundProcess()
    Write-Host "   attempt $attempt did not take (foreground=$fg, clipboard=[$got]); retrying" -ForegroundColor DarkYellow
    Start-Sleep -Milliseconds 800
}
if (-not $primed) {
    Fail "HARNESS: could not select text in Notepad, so the hotkey cannot be tested. This is a test-rig failure, not a Refyn failure."
}
Write-Host "   selection verified" -ForegroundColor DarkGray

# Re-select with the same verified sender.
if (-not (Send-Verified $handle "^a")) { Fail "HARNESS: lost Notepad focus before firing the hotkey" }
Start-Sleep -Milliseconds 400

Write-Host "3. firing Ctrl+Alt+P (the real global hotkey)" -ForegroundColor Cyan
# Fire, then wait — retried as a unit.
#
# The hotkey is global, so it fires wherever focus is, but Refyn rewrites the
# selection in the FOREGROUND window. On a busy desktop another app reclaims
# focus in the gap between selecting and firing, and Refyn then faithfully looks
# at the wrong window — the log shows `target=Chrome_WidgetWin_1` and
# `captured NOTHING`, which is Refyn behaving correctly on a bad setup.
#
# So each attempt re-primes the selection and re-grabs focus. This is the
# harness compensating for its environment, not the app being unreliable.
$landed = $false
for ($attempt = 1; $attempt -le 3 -and -not $landed; $attempt++) {
    if ($attempt -gt 1) {
        Write-Host "   focus drifted; re-priming and retrying ($attempt/3)" -ForegroundColor DarkYellow
        if (-not (Send-Verified $handle "^a")) { continue }
        Start-Sleep -Milliseconds 400
    }

    if (-not (Send-Verified $handle "^%p")) { continue }

    Write-Host "4. waiting for the rewrite to land" -ForegroundColor Cyan
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 500
        try {
            $now = Invoke-RestMethod -Uri "http://127.0.0.1:$port/health" -TimeoutSec 2
            if ($now.rewrites -gt $rewritesBefore) { $landed = $true; break }
        } catch { }
    }
}
if (-not $landed) { Fail "the daemon never received a rewrite - the hotkey did not complete" }
Start-Sleep -Seconds 2   # let the paste and the clipboard restore finish

# --- verify -----------------------------------------------------------------

# Reading the window back is itself a focus-dependent operation, so it gets the
# same treatment as everything else: verified sends, and a retry, because a
# readback that silently grabs the wrong window would report a paste bug that
# the host log flatly contradicts.
Write-Host "5. reading the window contents back" -ForegroundColor Cyan
$final = ""
for ($attempt = 1; $attempt -le 3; $attempt++) {
    [System.Windows.Forms.Clipboard]::SetText("__sentinel__")
    Start-Sleep -Milliseconds 300
    if (-not (Send-Verified $handle "^a")) { Start-Sleep -Milliseconds 400; continue }
    Start-Sleep -Milliseconds 350
    if (-not (Send-Verified $handle "^c")) { Start-Sleep -Milliseconds 400; continue }
    Start-Sleep -Milliseconds 900
    $final = (Get-Clip).Trim()
    if ($final -ne "__sentinel__" -and $final.Length -gt 0) { break }
}

# Ground truth: what the daemon actually received and returned. The window and
# the daemon have to agree, or something between them is broken.
$history = Invoke-RestMethod -Uri "http://127.0.0.1:$port/history?limit=1" -TimeoutSec 3
$entry   = $history.entries[0]

Write-Host ""
Write-Host "--- daemon received ---" -ForegroundColor DarkGray
Write-Host $entry.input
Write-Host "--- daemon returned ---" -ForegroundColor DarkGray
Write-Host $entry.output
Write-Host "--- window now holds ---" -ForegroundColor DarkGray
Write-Host $final
Write-Host "------------------------" -ForegroundColor DarkGray
Write-Host ""

if ($entry.input.Trim() -ne $original) {
    Fail "daemon received different text than we selected`n  expected: $original`n  got:      $($entry.input)"
}
if ($final -eq "__sentinel__" -or $final.Length -eq 0) { Fail "could not read the window contents back" }
if ($final -eq $original)                              { Fail "text unchanged - the rewrite never pasted" }
if ($final -ne $entry.output.Trim()) {
    Fail ("window does not hold what the daemon returned - the paste is wrong" +
          "`n  daemon returned: [" + $entry.output.Trim() + "]" +
          "`n  window holds:    [" + $final + "]")
}
if ($final -notmatch '(?i)dns')                        { Fail "the rewrite lost the subject of the prompt" }

Write-Host "PASS: selection was rewritten in place" -ForegroundColor Green
Write-Host "      $($original.Length) chars -> $($final.Length) chars in $($entry.ms)ms" -ForegroundColor Green

Cleanup
exit 0
