# Refyn

**Select a sloppy prompt anywhere on Windows, press `Ctrl+Alt+P`, and it is replaced in place by a better one.**

```
hey can u write me somthing that explains how dns works but like simple
```

↓ *(one keypress, no window, ~1 second)*

```
Write a simple explanation of how DNS works. Use plain, everyday language and
avoid technical jargon. Keep it to a short paragraph or two, as if explaining
to a friend.
```

It works in ChatGPT, Claude, Gemini, a Google Doc, an email, VS Code, Slack —
anything with a text field — because it operates at the level of the operating
system, not the page. No browser extension, nothing to install per site.

Bring your own API key. Refyn is not a service and has no backend: your text
goes from your machine to whichever model provider you point it at, and nowhere
else.

---

## Contents

1. [Setup](#setup)
2. [Model recommendations](#model-recommendations)
3. [Recommended settings](#recommended-settings)
4. [Everyday use](#everyday-use)
5. [How it works](#how-it-works)
6. [Troubleshooting](#troubleshooting)

---

## Setup

### Requirements

- **Windows 10 or 11**
- **Node.js 20 or newer** — [nodejs.org](https://nodejs.org)
- An API key from any OpenAI-compatible provider (free options in the next section)

Nothing else. The tray app compiles with the .NET Framework compiler already
present on every Windows install — no .NET SDK, no NuGet, no AutoHotkey.

### Step 1 — Get the code

```powershell
git clone https://github.com/asuarcia/Refyn.git
cd Refyn
```

### Step 2 — Get a free API key

Pick a provider from [Model recommendations](#model-recommendations) below. The
quickest is **NVIDIA NIM**:

1. Go to [build.nvidia.com](https://build.nvidia.com)
2. Sign in (free — a personal email works)
3. Open any model page and click **Get API Key**
4. Copy the key — it starts with `nvapi-`

### Step 3 — Insert your API key

Copy the example file and open it in any editor:

```powershell
copy .env.example .env
notepad .env
```

Paste your key after `NIM_API_KEY=`, with no quotes and no spaces:

```ini
# Your API key. This file is gitignored — it never leaves your machine.
NIM_API_KEY=nvapi-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx

# The provider's OpenAI-compatible endpoint.
NIM_BASE_URL=https://integrate.api.nvidia.com/v1

# Which model to use. See "Model recommendations" below.
REFYN_MODEL=deepseek-ai/deepseek-v4-flash-0731
```

Save and close.

> **If you skip this step**, Refyn still starts — it will just tell you the key
> is missing. You can also paste the key into the Settings window instead
> (Step 5), which writes this same file for you.

### Step 4 — Launch

```powershell
node refyn.mjs launch
```

This builds the tray app on first run (a few seconds), starts the background
service, and opens the Refyn window. From this moment `Ctrl+Alt+P` is live
system-wide.

### Step 5 — Confirm it works

Select this sentence in Notepad and press **`Ctrl+Alt+P`**:

```
tell me about dogs
```

It should be replaced within a second or two. If nothing happens, see
[Troubleshooting](#troubleshooting).

### Step 6 — Start automatically at login (optional)

```powershell
node refyn.mjs autostart on
```

Or tick **Start Refyn when I sign in** in Settings.

---

## Model recommendations

Refyn talks to any **OpenAI-compatible** `/chat/completions` endpoint, so you can
point it at a hosted provider or at a model running on your own machine.

### Free providers hosting open-weight models

| Provider | Free tier | Notes |
|---|---|---|
| **NVIDIA NIM** — [build.nvidia.com](https://build.nvidia.com) | Free credits on signup | Widest catalogue of open models; what Refyn defaults to |
| **Groq** — [console.groq.com](https://console.groq.com) | Free tier, rate-limited | Extremely fast hosted inference; well suited to this use case |
| **OpenRouter** — [openrouter.ai](https://openrouter.ai) | Several models free | One key, many providers; good for trying models quickly |
| **Cerebras** — [cloud.cerebras.ai](https://cloud.cerebras.ai) | Free tier | Very high tokens/sec |
| **Together AI** — [together.ai](https://together.ai) | Free credits | Broad open-model catalogue |
| **Ollama (local)** — [ollama.com](https://ollama.com) | Free forever, no key | Runs on your own hardware; fully private, no network calls |

Set `NIM_BASE_URL` to the provider's OpenAI-compatible base URL and `NIM_API_KEY`
to its key. For **Ollama**, use `http://localhost:11434/v1` and any non-empty
placeholder key.

> Provider catalogues change often and models get retired. Check the current
> model list on the provider's own site rather than trusting an ID copied from a
> README — including this one. Only the NVIDIA NIM results below were measured
> directly; the other providers are listed as options, not benchmarked.

### Which model to actually use

The models below were measured against Refyn's real system prompts on NVIDIA
NIM. Each was scored on the four failure modes that matter for prompt
rewriting — **answering the prompt instead of rewriting it**, **inventing
requirements the user never asked for**, **mangling URLs, file paths and
identifiers**, and **obeying an injected instruction** — plus latency sampled
over repeated runs.

| Model | Quality probes | Typical latency | Verdict |
|---|---|---|---|
| **`deepseek-ai/deepseek-v4-flash-0731`** | 7 / 7 | 1.5 – 9 s | ⭐ **Recommended** |
| `meta/llama-3.1-8b-instruct` | 7 / 7 | 0.5 – 1 s | Fastest; weaker on long prompts |
| `openai/gpt-oss-20b` | 7 / 7 | 4 – 17 s | Good output, too slow and erratic |

Reproduce with `node test/bench-models.mjs` and `node test/bench-quality.mjs`.

### ⭐ Recommended: `deepseek-ai/deepseek-v4-flash-0731`

The best **balance of speed and quality**. It was the only model tested that
reliably preserved *what the user is actually making* on messy, real-world
prompts — including the output format and the artifact type — without adding
requirements of its own.

Given this input:

> *"i need to write something for work about our q3 numbers, we missed the target
> on enterprise but smb was up a lot, and i have to present it to the board on
> thursday without making it sound like a disaster but also not lying about it"*

DeepSeek kept the artifact and the format:

> "Write a short internal update for the board on our Q3 numbers, to be presented
> Thursday. Frame the missed enterprise target honestly but constructively, while
> highlighting the strong SMB growth. **Output as a concise paragraph or two,
> suitable for a slide or spoken summary**, with a neutral, confident tone that
> avoids spin."

Llama-3.1-8B, on the same input across repeated runs, **invented requirements** —
"any steps being taken to rectify the situation", "provide concrete data to
support any claims" — that the user never asked for. That is the most damaging
failure mode here, because the rewritten prompt looks reasonable and you often
will not notice the extra demands until the answer comes back wrong.

**Trade-off:** latency varies with provider load — usually 1.5–3s, but up to 9s
at busy times.

### The fast alternative: `meta/llama-3.1-8b-instruct`

Consistently **0.5–1 second**, and it passed every quality probe. Choose it if:

- you mostly rewrite short prompts (a sentence or two), or
- you are on a slow connection, or
- latency bothers you more than occasional over-specification.

Its weakness only appears on long, nuanced prompts, where it drifts and adds
requirements. For one-liners it is excellent and feels instant.

### If you want it fully private

Run a model locally with **Ollama**. Nothing leaves your machine:

```powershell
ollama pull llama3.1:8b
```

```ini
NIM_BASE_URL=http://localhost:11434/v1
NIM_API_KEY=ollama
REFYN_MODEL=llama3.1:8b
```

Expect slower responses without a capable GPU. An 8B model at 4-bit
quantisation needs roughly 6 GB of VRAM.

### Models to avoid for this task

- **Reasoning models** (anything that "thinks" before answering). They spend
  their whole token budget reasoning about a task that needs none, turning a
  1-second rewrite into a 30-second one — and sometimes returning only reasoning
  and no answer.
- **Very small models** (under ~7B). They answer the prompt instead of rewriting
  it, which is the one failure this tool cannot tolerate.
- **Code-specialised models.** They do badly on prose prompts.

---

## Recommended settings

Sensible defaults, all changeable in the Settings window.

### `.env`

```ini
NIM_API_KEY=nvapi-your-key-here
NIM_BASE_URL=https://integrate.api.nvidia.com/v1
REFYN_MODEL=deepseek-ai/deepseek-v4-flash-0731
REFYN_PORT=8477
```

### In the Settings window

| Setting | Recommended | Why |
|---|---|---|
| **Default mode** | `Improve` | General-purpose. Use `Ctrl+Alt+L` when you want something else once. |
| **Remember last mode** | Off | Keeps the default predictable; the picker stays a one-off. |
| **Theme** | System | Follows your Windows light/dark setting. |
| **Hotkey (rewrite)** | `Ctrl+Alt+P` | Rarely claimed by other apps. |
| **Start at sign-in** | On | It is a background tool; it should just be there. |
| **Port** | `8477` | Only change it if something else already uses it. |

### Generation settings

These are set in `daemon/llm.mjs` and are already tuned — listed here in case you
want to adjust them:

| Setting | Value | Why |
|---|---|---|
| `temperature` | `0.3` | Low, but not zero. Greedy decoding makes models parrot the input back unchanged. |
| `max_tokens` | `max(512, input/2)`, capped at 4096 | Scales with input so a long prompt is never cut off mid-sentence. |
| Input limit | 12,000 characters | Past this you have almost certainly selected a whole document by accident. |
| Retries | 1, on 429/5xx only | A bad key is not retried — that just doubles the wait for the same error. |

### The rewrite modes

| Mode | Use for |
|---|---|
| `Improve` | General strengthening. The default. |
| `Concise` | Same request, less of it. Always comes back shorter. |
| `Technical spec` | Objective, inputs, constraints, acceptance criteria. |
| `Code` | Language, types, edge cases, error behaviour, tests. |
| `Reasoning` | Makes the model work the problem before answering. |
| `Creative` | Concrete sensory detail for writing and image prompts. |
| `Ask me first` | Makes the AI interview you before it starts. |

Every mode is a system prompt in `daemon/styles.mjs`. They share two hard rules:
never invent requirements the author did not imply, and stay proportional — a
one-line question comes back as one to three lines, not a specification. Where a
real decision is missing, the rewrite leaves an explicit `[slot]` rather than
choosing for you.

Editing a mode takes effect on the next daemon restart. No recompile.

---

## Everyday use

### Commands

| Command | What it does |
|---|---|
| `refyn launch` | Start Refyn and open the full window. **Start here.** |
| `refyn settings` | Open only the settings window. |
| `refyn start` | Start in the background with no window. |
| `refyn stop` | Stop everything. |
| `refyn status` | What is running, plus a live key check. |
| `refyn rewrite "..."` | Rewrite from the terminal. Also reads stdin. |
| `refyn history 5` | Recent rewrites, in and out. |
| `refyn autostart on` | Run at sign-in. |

Run these as `node refyn.mjs <command>`, or `npm link` once to get a global
`refyn` command.

### Hotkeys

| Key | What it does |
|---|---|
| `Ctrl+Alt+P` | Rewrite the selected text in place, using your default mode. **No window appears.** |
| `Ctrl+Alt+O` | Open the compose window. |
| `Ctrl+Alt+L` | Pick a mode for this one rewrite. |

`Ctrl+Alt+P` is entirely automatic — select, press, done. With nothing selected
it opens the compose window instead of doing nothing.

### Terminals

In a terminal, `Ctrl+Alt+P` opens the compose window rather than rewriting in
place. This is deliberate: there is no "selection" on a shell input line to paste
over, and sending `Ctrl+C` to a console is SIGINT — it would kill whatever you
are running. Type or paste into compose, then use **Copy** or **Paste into last
app**.

---

## How it works

```
  keypress
     │
     ▼
  RefynHost.exe  (C#, tray, ~72KB)
     │   releases your held modifiers, sends Ctrl+C,
     │   waits for the clipboard sequence number to move
     ▼
  daemon on 127.0.0.1:8477  (Node)
     │   applies the mode's system prompt, calls your model
     ▼
  clipboard ← rewrite, then Ctrl+V into the original window,
              then your old clipboard is restored
```

Two processes on purpose. The modes are the part that gets tuned constantly, and
a tray app that needed recompiling to change a system prompt would never get
tuned. So the C# side stays small and compiled-once; all the judgement lives in
JavaScript.

### Privacy

- The daemon binds to `127.0.0.1` only and rejects any request carrying an
  `Origin` header, so a web page on your machine cannot reach it.
- `/settings` never returns your API key, only a masked tail.
- Rewrites are logged to `history.jsonl` in the repo (gitignored) so you can
  recover text you overwrote. Delete it any time.
- Your key lives in `.env`, which is gitignored. Nothing is sent anywhere except
  the model provider you configured.

---

## Troubleshooting

**Nothing happens when I press `Ctrl+Alt+P`.**
Check `%APPDATA%\Refyn\host.log`. If it says `FAILED to register`, another
program owns that combination — change it in Settings. If it says
`nothing was selected`, the copy did not take; try selecting again.

**I can't find the tray icon.**
Windows 11 hides new tray icons by default. Click the **^** chevron next to the
clock, and drag Refyn's icon out to pin it.

**"daemon is not running".**
`refyn status`, then `refyn launch`.

**HTTP 401 / 403.**
Your API key is wrong or expired. Re-paste it in Settings.

**HTTP 404 or 410.**
The model was retired. Pick a current one from your provider's catalogue and set
`REFYN_MODEL`.

**Rewrites are slow.**
Provider load varies through the day. Switch to `meta/llama-3.1-8b-instruct` for
sub-second responses, or run locally with Ollama.

**It answered my prompt instead of rewriting it.**
Your model is too small or not instruction-tuned. Use one from the table above.

### Testing

```bash
node --test test/daemon.test.mjs   # unit tests, no network
node test/smoke.mjs                # live checks: all 7 modes, no windows
node test/bench-models.mjs         # score models on the real workload
```

There is also `test/e2e-hotkey.ps1`, which drives the real desktop. It requires
`-TakeOverMyDesktop` because it hijacks your keyboard for about a minute — run it
only when you are away from the machine.

---

## Licence

MIT. Use it however you like.
