#!/usr/bin/env node
/**
 * Quiet end-to-end check. Opens no windows and steals no focus.
 *
 *   node test/smoke.mjs
 *
 * This is the suite to run while you are using the machine. It exercises
 * everything except the synthetic-keystroke path: the daemon's routes, a real
 * rewrite through every style against the live model, the settings round trip,
 * and whether the tray host is alive and logging.
 *
 * What it deliberately cannot cover is the part that needs a focused window —
 * SendInput, the modifier release, the clipboard hand-off. That is
 * test/e2e-hotkey.ps1, which takes over the desktop and is opt-in for exactly
 * that reason.
 */

import { execFile } from "node:child_process";
import { readFileSync, existsSync } from "node:fs";
import path from "node:path";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";
import { loadEnv } from "../daemon/env.mjs";
import { STYLES } from "../daemon/styles.mjs";

const execFileAsync = promisify(execFile);
const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
loadEnv(path.join(ROOT, ".env"));

const PORT = Number(process.env.REFYN_PORT) || 8477;
const BASE = `http://127.0.0.1:${PORT}`;

let passed = 0;
let failed = 0;

async function check(name, fn) {
  try {
    const detail = await fn();
    passed++;
    console.log(`  \x1b[32mok\x1b[0m   ${name}${detail ? `  \x1b[90m${detail}\x1b[0m` : ""}`);
  } catch (err) {
    failed++;
    console.log(`  \x1b[31mFAIL\x1b[0m ${name}\n       ${err.message}`);
  }
}

async function get(route) {
  const response = await fetch(`${BASE}${route}`, { signal: AbortSignal.timeout(8000) });
  if (!response.ok) throw new Error(`${route} returned HTTP ${response.status}`);
  return response.json();
}

console.log("\nRefyn smoke test \x1b[90m(no windows, no focus stealing)\x1b[0m\n");

// --- daemon ----------------------------------------------------------------

let health;
await check("daemon responds on /health", async () => {
  health = await get("/health");
  if (!health.ok) throw new Error("daemon reported not ok");
  return `pid ${health.pid}, up ${health.uptimeSec}s`;
});

if (!health) {
  console.log("\n\x1b[31mDaemon is not running.\x1b[0m Start it with: node refyn.mjs start\n");
  process.exit(1);
}

await check("API key is configured", async () => {
  if (!health.keyConfigured) throw new Error("NIM_API_KEY is missing from .env");
  return health.model;
});

await check("/styles lists every style the daemon knows", async () => {
  const { styles } = await get("/styles");
  const ids = styles.map((s) => s.id).sort();
  const expected = Object.keys(STYLES).sort();
  if (ids.join(",") !== expected.join(",")) {
    throw new Error(`served [${ids}] but styles.mjs defines [${expected}]`);
  }
  return `${ids.length} styles`;
});

await check("/settings reports model without leaking the key", async () => {
  const settings = await get("/settings");
  if (!settings.model) throw new Error("no model reported");
  const raw = process.env.NIM_API_KEY || "";
  if (raw && settings.keyMasked.includes(raw)) {
    throw new Error("the full API key was returned to the client");
  }
  if (!settings.appRoot || !settings.node) throw new Error("appRoot/node missing (settings UI needs them)");
  return settings.keyMasked || "no key";
});

// --- the actual product ----------------------------------------------------

await check("rejects an empty rewrite", async () => {
  const response = await fetch(`${BASE}/rewrite`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ text: "   ", style: "improve" }),
  });
  if (response.status !== 400) throw new Error(`expected HTTP 400, got ${response.status}`);
});

await check("refuses browser-origin requests", async () => {
  const response = await fetch(`${BASE}/health`, { headers: { Origin: "https://evil.example" } });
  if (response.status !== 403) throw new Error(`expected HTTP 403, got ${response.status}`);
});

const SLOPPY = "hey can u write me somthing that explains how dns works but like simple";

for (const style of Object.keys(STYLES)) {
  await check(`rewrites with style "${style}"`, async () => {
    const response = await fetch(`${BASE}/rewrite`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ text: SLOPPY, style }),
      signal: AbortSignal.timeout(60000),
    });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || `HTTP ${response.status}`);

    const out = data.result || "";
    if (!out.trim()) throw new Error("empty rewrite");
    // The dominant failure mode is the model ANSWERING instead of rewriting.
    // An answer explains DNS; a rewrite still asks for one.
    if (!/dns/i.test(out)) throw new Error(`lost the subject of the prompt: ${out.slice(0, 80)}`);
    if (style === "concise" && out.length >= SLOPPY.length) {
      throw new Error(`concise returned ${out.length} chars from ${SLOPPY.length}`);
    }
    return `${SLOPPY.length} -> ${out.length} chars, ${data.ms}ms`;
  });
}

await check("history records what was rewritten", async () => {
  const { entries } = await get("/history?limit=1");
  if (!entries.length) throw new Error("no history entries");
  if (entries[0].input !== SLOPPY) throw new Error("newest entry is not the rewrite we just made");
  return entries[0].style;
});

// --- host ------------------------------------------------------------------

await check("tray host is running", async () => {
  const { stdout } = await execFileAsync("tasklist", [
    "/FI", "IMAGENAME eq RefynHost.exe", "/NH", "/FO", "CSV",
  ]);
  if (!stdout.includes("RefynHost.exe")) throw new Error("RefynHost.exe is not running");
  return "RefynHost.exe";
});

await check("host registered all three hotkeys", async () => {
  const logFile = path.join(process.env.APPDATA || "", "Refyn", "host.log");
  if (!existsSync(logFile)) throw new Error("no host.log — has the host started?");
  const log = readFileSync(logFile, "utf8");
  const failures = log.split(/\r?\n/).filter((l) => l.includes("FAILED to register"));
  if (failures.length) throw new Error(failures.join("; "));
  const registered = (log.match(/registered hotkey/g) || []).length;
  if (registered < 3) throw new Error(`only ${registered} hotkeys registered this session`);
  return `${registered} registered`;
});

await check("config.json is valid and complete", async () => {
  const file = path.join(process.env.APPDATA || "", "Refyn", "config.json");
  if (!existsSync(file)) return "not written yet (defaults in use)";
  const config = JSON.parse(readFileSync(file, "utf8"));
  for (const key of ["port", "hotkeyImprove", "hotkeyCompose", "hotkeyStyles", "theme", "defaultStyle"]) {
    if (!(key in config)) throw new Error(`missing "${key}"`);
  }
  if (!STYLES[config.defaultStyle]) throw new Error(`defaultStyle "${config.defaultStyle}" is not a real style`);
  return `default mode: ${config.defaultStyle}, theme: ${config.theme}`;
});

console.log(
  `\n${failed === 0 ? "\x1b[32m" : "\x1b[31m"}${passed} passed, ${failed} failed\x1b[0m\n`
);
process.exit(failed === 0 ? 0 : 1);
