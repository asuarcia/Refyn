# Refyn

Select a sloppy prompt anywhere on Windows, press **Ctrl+Alt+P**, and it is
replaced in place by a better one.

```
hey can u write me somthing that explains how dns works but like simple
```
↓
```
Write a simple explanation of how DNS works. Use plain, everyday language and
avoid technical jargon. Keep it to a short paragraph or two, as if explaining
to a friend.
```

It works in ChatGPT, Claude, a Google Doc, an email, VS Code, Slack — anything
with a text field — because it works at the level of the operating system, not
the page. No browser extension, and nothing to install per-site.

---

## Why not an extension

An extension only reaches the browser. This runs as a tray app and drives the
selection through the OS, so a single install covers every app on the machine.

The one place in-place replacement genuinely cannot work is a terminal: there is
no "selection" on a shell input line to paste over, and sending Ctrl+C to a
console is SIGINT, which would kill whatever you are running. Refyn
detects a terminal by its window class and opens the compose window instead —
type or paste there, get the rewrite, and it is copied ready to paste.

---

## Install

Requires Node 20+. Nothing else — the tray app compiles with the .NET Framework
compiler already present on every Windows install. No .NET SDK, no NuGet, no
AutoHotkey.

```powershell
git clone <this repo> && cd Refyn
cp .env.example .env      # then put your API key in it
node refyn.mjs start
```

`start` builds the tray host on first run, launches the daemon, and puts a
**P** in your system tray.

To have it come back after a reboot:

```powershell
node refyn.mjs autostart on
```

---

## Hotkeys

| Key | What it does |
|---|---|
| `Ctrl+Alt+P` | Rewrite the selected text in place, using the last style |
| `Ctrl+Alt+O` | Open the compose window |
| `Ctrl+Alt+L` | Pick a style for the current selection |

With nothing selected, `Ctrl+Alt+P` opens the compose window rather than doing
nothing.

---

## Settings

Three ways in, all the same window:

- **right-click the tray icon → Settings…**
- **`refyn settings`** from a terminal
- double-click the tray icon opens Compose; Settings is one menu item away

It covers hotkeys (click a field and press the combination you want), theme,
the model and API key, the **default mode**, the port, and run-at-login. Hotkey
changes take effect on the next launch; everything else applies immediately.

### Default mode

The style every rewrite starts from. `Ctrl+Alt+L` picks a different one for a
single rewrite without changing the default.

If you would rather the picker be sticky — pick `concise` once and stay there —
tick **Remember the last mode I pick instead**. That was the original behaviour
and it was wrong as a default: it silently retired the mode you had configured,
which made the setting look broken.

Settings are split across two files by owner, and the window writes both:

| File | Holds | Written by |
|---|---|---|
| `%APPDATA%\Refyn\config.json` | hotkeys, theme, default style, port | the tray app |
| `.env` in the repo | model, endpoint, API key | the daemon |

The daemon owns the key because it is the only process that should ever hold
one. It rewrites `.env` line by line, so your comments and any extra variables
survive, and it keeps a `.env.bak` the first time it touches the file.

### Theme

**System**, **Light**, or **Dark**. System follows the Windows app theme, and
the choice previews live before you save it. Both windows are painted by hand —
stock WinForms has no dark mode at all, and its dropdowns stay bright white on
a dark form no matter what you set `BackColor` to.

---

## Styles

| Style | For |
|---|---|
| `improve` | General strengthening. The default. |
| `concise` | Same request, less of it. Output is always shorter than input. |
| `technical` | Objective, inputs, constraints, acceptance criteria. |
| `code` | Language, types, edge cases, error behaviour, tests. |
| `reasoning` | Forces the model to work the problem before answering. |
| `creative` | Concrete sensory detail for writing and image prompts. |
| `socratic` | Makes the AI interview you before it starts. |

Every style is a system prompt in `daemon/styles.mjs`. They share two hard
rules: never invent requirements the author did not imply, and stay
proportional — a one-line question comes back as one to three lines, not a
specification. Where a real decision is missing, the rewrite leaves an explicit
`[slot]` rather than choosing for you.

Editing a style takes effect on the next daemon restart. No recompile.

---

## From the terminal

```bash
refyn rewrite "make a thing that sorts stuff" --style code
cat rough-draft.txt | refyn rewrite --style concise
refyn status      # what is running, plus a live end-to-end key check
refyn history 5   # recent rewrites, with what went in and what came out
```

---

## How it fits together

```
  keypress
     │
     ▼
  RefynHost.exe  (C#, tray, ~40KB)
     │   releases your held modifiers, sends Ctrl+C,
     │   waits for the clipboard sequence number to move
     ▼
  daemon on 127.0.0.1:8477  (Node)
     │   applies the style's system prompt, calls the model
     ▼
  clipboard ← rewrite, then Ctrl+V into the original window,
              then your old clipboard is put back
```

Two processes on purpose. The styles are the part that gets tuned constantly,
and a tray app that needed recompiling to change a system prompt would never
get tuned. So the C# side stays dumb and compiled-once; all the judgement lives
in JavaScript.

The daemon binds to `127.0.0.1` only and refuses any request carrying an
`Origin` header, so a web page on your own machine cannot reach it.

---

## Testing

Two suites, split by whether they touch your screen.

**Quiet — run these any time.** No windows, no focus stealing.

```bash
node --test test/daemon.test.mjs    # 16 unit tests: styles, cleanup, .env parsing
node test/smoke.mjs                 # 17 live checks: daemon routes, a real
                                    # rewrite in all 7 styles, host + hotkeys
```

`smoke.mjs` asserts the things that actually matter: that the model *rewrote*
the prompt instead of answering it, that `concise` really does come back
shorter, and that `/settings` never returns the API key.

**Loud — takes over the machine.** Opt-in, and it tells you so if you forget:

```powershell
powershell -ExecutionPolicy Bypass -File test/e2e-hotkey.ps1 -TakeOverMyDesktop
```

This is the only way to cover SendInput, the modifier release and the clipboard
hand-off, because none of them work without a real focused window. It opens
Notepad, forces it to the foreground and types into it for about a minute — run
it when you are away from the keyboard.

It verifies its own preconditions first: if it cannot prove it selected text, it
reports a *harness* failure rather than blaming the app. An earlier version did
the opposite and produced a confident false PASS. It also restores the system
foreground-lock timeout on every exit path, having once left it at zero and made
every app on the machine able to steal focus.

When something goes wrong, `%APPDATA%\Refyn\host.log` has one line per step of
the last session — the only way to see inside a tray app with no console.

When something goes wrong, `%APPDATA%\Refyn\host.log` has one line per
step of the last session, which is the only way to see inside a tray app with
no console.

---

## Troubleshooting

**Nothing happens on the hotkey.** Check `host.log`. If it says
`FAILED to register`, another program owns that combo — change it in
`config.json`. If it says `nothing was selected`, the copy did not take.

**"daemon is not running".** `refyn status`, then `refyn start`.

**Rewrites fail with HTTP 404 or 410.** The model was retired. Set
`REFYN_MODEL` in `.env` to a current one.

**The selection is too big.** There is a 12,000 character ceiling; past that you
have almost certainly selected a whole document by accident.
