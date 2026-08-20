# Promptsmith

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
console is SIGINT, which would kill whatever you are running. Promptsmith
detects a terminal by its window class and opens the compose window instead —
type or paste there, get the rewrite, and it is copied ready to paste.

---

## Install

Requires Node 20+. Nothing else — the tray app compiles with the .NET Framework
compiler already present on every Windows install. No .NET SDK, no NuGet, no
AutoHotkey.

```powershell
git clone <this repo> && cd Promptsmith
cp .env.example .env      # then put your API key in it
node promptsmith.mjs start
```

`start` builds the tray host on first run, launches the daemon, and puts a
**P** in your system tray.

To have it come back after a reboot:

```powershell
node promptsmith.mjs autostart on
```

---

## Hotkeys

| Key | What it does |
|---|---|
| `Ctrl+Alt+P` | Rewrite the selected text in place, using the last style |
| `Ctrl+Alt+O` | Open the compose window |
| `Ctrl+Alt+L` | Pick a style for the current selection |

With nothing selected, `Ctrl+Alt+P` opens the compose window rather than doing
nothing. Change any of these in `%APPDATA%\Promptsmith\config.json`:

```json
{ "port": 8477, "hotkeyImprove": "Ctrl+Alt+P", "hotkeyCompose": "Ctrl+Alt+O", "hotkeyStyles": "Ctrl+Alt+L" }
```

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
promptsmith rewrite "make a thing that sorts stuff" --style code
cat rough-draft.txt | promptsmith rewrite --style concise
promptsmith status      # what is running, plus a live end-to-end key check
promptsmith history 5   # recent rewrites, with what went in and what came out
```

---

## How it fits together

```
  keypress
     │
     ▼
  PromptsmithHost.exe  (C#, tray, ~40KB)
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

```bash
node --test test/*.test.mjs                                     # daemon logic
powershell -ExecutionPolicy Bypass -File test/e2e-hotkey.ps1    # the real hotkey
```

The end-to-end test opens Notepad, selects text, presses the actual global
hotkey, and asserts the window ends up holding exactly what the daemon
returned. It verifies its own preconditions first: if it cannot prove it
selected text, it reports a harness failure rather than blaming the app — an
earlier version did the opposite and produced a confident false PASS.

When something goes wrong, `%APPDATA%\Promptsmith\host.log` has one line per
step of the last session, which is the only way to see inside a tray app with
no console.

---

## Troubleshooting

**Nothing happens on the hotkey.** Check `host.log`. If it says
`FAILED to register`, another program owns that combo — change it in
`config.json`. If it says `nothing was selected`, the copy did not take.

**"daemon is not running".** `promptsmith status`, then `promptsmith start`.

**Rewrites fail with HTTP 404 or 410.** The model was retired. Set
`PROMPTSMITH_MODEL` in `.env` to a current one.

**The selection is too big.** There is a 12,000 character ceiling; past that you
have almost certainly selected a whole document by accident.
