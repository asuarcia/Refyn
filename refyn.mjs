#!/usr/bin/env node
/**
 * refyn — command line for the whole thing.
 *
 *   refyn start          start daemon + tray host
 *   refyn stop           stop both
 *   refyn status         what is running, and is the key working
 *   refyn rewrite "..."  rewrite from the terminal (no hotkey involved)
 *   refyn build          recompile the tray host
 *   refyn autostart on   run at logon
 *   refyn history        recent rewrites
 */

import { spawn, execFile } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, writeFileSync, unlinkSync, openSync, closeSync } from "node:fs";
import path from "node:path";
import os from "node:os";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { loadEnv } from "./daemon/env.mjs";

const execFileAsync = promisify(execFile);
const ROOT = path.dirname(fileURLToPath(import.meta.url));
const HOST_EXE = path.join(ROOT, "host", "bin", "RefynHost.exe");
const DAEMON = path.join(ROOT, "daemon", "server.mjs");
const STATE_DIR = path.join(process.env.APPDATA || path.join(os.homedir(), ".config"), "Refyn");
const CONFIG_FILE = path.join(STATE_DIR, "config.json");
const PID_FILE = path.join(STATE_DIR, "daemon.pid");
const LOG_FILE = path.join(STATE_DIR, "daemon.log");
const TASK_NAME = "RefynLogon";

loadEnv();
const PORT = Number(process.env.REFYN_PORT) || 8477;
const BASE = `http://127.0.0.1:${PORT}`;

const c = {
  dim: (s) => `\x1b[90m${s}\x1b[0m`,
  green: (s) => `\x1b[32m${s}\x1b[0m`,
  red: (s) => `\x1b[31m${s}\x1b[0m`,
  yellow: (s) => `\x1b[33m${s}\x1b[0m`,
  bold: (s) => `\x1b[1m${s}\x1b[0m`,
};

// ---------------------------------------------------------------- daemon ops

async function daemonHealth() {
  try {
    const response = await fetch(`${BASE}/health`, { signal: AbortSignal.timeout(1500) });
    if (!response.ok) return null;
    return await response.json();
  } catch {
    return null;
  }
}

async function startDaemon() {
  if (await daemonHealth()) {
    console.log(c.dim("daemon already running"));
    return true;
  }
  mkdirSync(STATE_DIR, { recursive: true });

  // Hand the child raw file descriptors rather than pipes. A pipe would be a
  // libuv handle owned by THIS process, and an open handle keeps the event loop
  // alive — so `refyn start` would print its success line and then hang
  // forever, holding the terminal, which is exactly what it did before this.
  // unref() on the child does not help: the stdio handles are separate.
  const logFd = openSync(LOG_FILE, "a");
  const child = spawn(process.execPath, [DAEMON], {
    detached: true,
    windowsHide: true,
    stdio: ["ignore", logFd, logFd],
    cwd: ROOT,
  });
  closeSync(logFd); // the child holds its own duplicate now
  child.unref();

  writeFileSync(PID_FILE, String(child.pid));

  for (let i = 0; i < 40; i++) {
    await sleep(100);
    if (await daemonHealth()) {
      console.log(`${c.green("+")} daemon on ${BASE} (pid ${child.pid})`);
      return true;
    }
  }
  console.log(c.red("x") + ` daemon did not come up. Last lines of ${LOG_FILE}:`);
  console.log(tail(LOG_FILE, 15));
  return false;
}

async function stopDaemon() {
  const health = await daemonHealth();
  const pid = health?.pid ?? readPid();
  if (!pid) {
    console.log(c.dim("daemon not running"));
    return;
  }
  try {
    process.kill(pid, "SIGTERM");
    // SIGTERM is emulated on Windows and does not always land; verify.
    await sleep(400);
    if (await daemonHealth()) {
      await execFileAsync("taskkill", ["/PID", String(pid), "/T", "/F"]);
    }
    console.log(`${c.green("-")} daemon stopped (pid ${pid})`);
  } catch (err) {
    console.log(c.yellow("!") + ` could not stop pid ${pid}: ${err.message}`);
  }
  try { unlinkSync(PID_FILE); } catch { /* already gone */ }
}

function readPid() {
  try { return Number(readFileSync(PID_FILE, "utf8").trim()) || null; } catch { return null; }
}

// ------------------------------------------------------------------ host ops

async function hostRunning() {
  if (process.platform !== "win32") return false;
  try {
    const { stdout } = await execFileAsync("tasklist", [
      "/FI", "IMAGENAME eq RefynHost.exe", "/NH", "/FO", "CSV",
    ]);
    return stdout.includes("RefynHost.exe");
  } catch {
    return false;
  }
}

async function startHost() {
  if (process.platform !== "win32") {
    console.log(c.yellow("!") + " the tray host is Windows-only; the daemon and `rewrite` still work.");
    return;
  }
  if (!existsSync(HOST_EXE)) {
    console.log(c.dim("host not built yet — building"));
    if (!(await buildHost())) return;
  }
  if (await hostRunning()) {
    console.log(c.dim("tray host already running"));
    return;
  }
  const child = spawn(HOST_EXE, [], { detached: true, stdio: "ignore", windowsHide: true });
  child.unref();
  await sleep(600);
  if (await hostRunning()) {
    console.log(`${c.green("+")} tray host running — look for the P near the clock`);
  } else {
    console.log(c.red("x") + " tray host exited immediately.");
  }
}

async function stopHost() {
  if (process.platform !== "win32") return;
  if (!(await hostRunning())) {
    console.log(c.dim("tray host not running"));
    return;
  }
  try {
    await execFileAsync("taskkill", ["/IM", "RefynHost.exe", "/F"]);
    console.log(`${c.green("-")} tray host stopped`);
  } catch (err) {
    console.log(c.yellow("!") + ` ${err.message}`);
  }
}

/**
 * Open the settings window.
 *
 * If the host is already running, launching it again with --settings makes the
 * new process broadcast a message to the running one and exit immediately —
 * the single-instance guard is what turns a second launch into a remote
 * control rather than a duplicate tray icon.
 */
/**
 * `refyn launch` — the front door.
 *
 * Starts the daemon and the tray host (so Ctrl+Alt+P works from here on) and
 * opens the full window, which carries both Compose and Settings. This is what
 * a first-time user should run: it makes the hotkey live AND shows them the app.
 */
async function cmdLaunch() {
  if (process.platform !== "win32") {
    console.log(c.yellow("!") + " the Refyn window is Windows-only; `refyn rewrite` still works here.");
    return;
  }
  if (!existsSync(HOST_EXE)) {
    console.log(c.dim("first run - building the tray host"));
    if (!(await buildHost())) return;
  }
  if (!(await daemonHealth())) {
    if (!(await startDaemon())) return;
  }
  if (!(await hostRunning())) {
    await startHost();
    await sleep(1200);
  }
  openHostWindow("--launch");
  console.log(`${c.green("+")} Refyn is running - ${c.bold("Ctrl+Alt+P")} rewrites whatever you have selected`);
}

/**
 * Ask the running tray host to show a window.
 *
 * Launching the exe again does not start a second copy: the single-instance
 * guard turns the duplicate into a broadcast to the copy already running, and
 * the new process exits immediately.
 */
function openHostWindow(flag) {
  const child = spawn(HOST_EXE, [flag], { detached: true, stdio: "ignore", windowsHide: true });
  child.unref();
}

async function cmdSettings() {
  if (process.platform !== "win32") {
    console.log(c.yellow("!") + " the settings window is Windows-only. Edit .env and " + CONFIG_FILE + " directly.");
    return;
  }
  if (!existsSync(HOST_EXE)) {
    console.log(c.dim("host not built yet - building"));
    if (!(await buildHost())) return;
  }
  // Settings reads the model and key from the daemon, so it wants both up.
  if (!(await daemonHealth())) await startDaemon();
  if (!(await hostRunning())) {
    await startHost();
    await sleep(1200);
  }
  openHostWindow("--settings");
  console.log(c.green("+") + " settings window opened");
}

async function buildHost() {
  if (process.platform !== "win32") {
    console.log(c.yellow("!") + " nothing to build off Windows.");
    return false;
  }
  try {
    const { stdout } = await execFileAsync("powershell", [
      "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path.join(ROOT, "host", "build.ps1"),
    ]);
    process.stdout.write(stdout);
    return true;
  } catch (err) {
    console.log(c.red("build failed:"));
    process.stdout.write(err.stdout || "");
    process.stderr.write(err.stderr || String(err));
    return false;
  }
}

// ----------------------------------------------------------------- autostart

/**
 * Register a logon task.
 *
 * The tray host is a winexe and starts silently on its own. The daemon is a
 * console process, and `schtasks` would flash a window on every logon — so it
 * is launched through a one-line WScript shim, which is the only launcher on
 * Windows that starts a console program with genuinely no window at all.
 */
async function setAutostart(enable) {
  if (process.platform !== "win32") {
    console.log(c.yellow("!") + " autostart is Windows-only.");
    return;
  }
  if (!enable) {
    try {
      await execFileAsync("schtasks", ["/Delete", "/TN", TASK_NAME, "/F"]);
      console.log(`${c.green("-")} autostart removed`);
    } catch {
      console.log(c.dim("autostart was not registered"));
    }
    return;
  }

  mkdirSync(STATE_DIR, { recursive: true });
  const shim = path.join(STATE_DIR, "launch.vbs");
  writeFileSync(
    shim,
    [
      "' Generated by `refyn autostart on`. Starts Refyn with no",
      "' console window. 0 = hidden, False = do not wait for it to exit.",
      'Set sh = CreateObject("WScript.Shell")',
      `sh.Run """${process.execPath}"" ""${path.join(ROOT, "refyn.mjs")}"" start", 0, False`,
      "",
    ].join("\r\n"),
    "utf8"
  );

  await execFileAsync("schtasks", [
    "/Create", "/TN", TASK_NAME,
    "/TR", `wscript.exe "${shim}"`,
    "/SC", "ONLOGON",
    "/RL", "LIMITED", // never elevated: an elevated process cannot SendInput
    "/F",
  ]);
  console.log(`${c.green("+")} autostart registered as scheduled task "${TASK_NAME}"`);
}

// ------------------------------------------------------------------ commands

async function cmdStart() {
  const ok = await startDaemon();
  if (ok) await startHost();
}

async function cmdStop() {
  await stopHost();
  await stopDaemon();
}

async function cmdStatus() {
  const health = await daemonHealth();
  const host = await hostRunning();

  console.log(c.bold("Refyn"));
  if (health) {
    console.log(`  daemon    ${c.green("running")}  ${BASE}  pid ${health.pid}  up ${health.uptimeSec}s`);
    console.log(`  model     ${health.model}`);
    console.log(`  api key   ${health.keyConfigured ? c.green("configured") : c.red("MISSING — add NIM_API_KEY to .env")}`);
    console.log(`  rewrites  ${health.rewrites} this session`);
  } else {
    console.log(`  daemon    ${c.red("stopped")}`);
  }
  console.log(`  tray host ${host ? c.green("running") : c.red("stopped")}${existsSync(HOST_EXE) ? "" : c.dim("  (not built)")}`);

  if (health?.keyConfigured) {
    process.stdout.write(c.dim("  checking inference... "));
    try {
      const t = Date.now();
      await rewriteOnce("test prompt", "concise");
      console.log(c.green(`ok (${Date.now() - t}ms)`));
    } catch (err) {
      console.log(c.red(err.message));
    }
  }
}

async function rewriteOnce(text, style) {
  const response = await fetch(`${BASE}/rewrite`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ text, style }),
  });
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || `HTTP ${response.status}`);
  return data;
}

async function cmdRewrite(args) {
  const styleFlag = args.findIndex((a) => a === "--style" || a === "-s");
  let style = "improve";
  if (styleFlag >= 0) {
    style = args[styleFlag + 1];
    args.splice(styleFlag, 2);
  }

  let text = args.join(" ").trim();
  if (!text && !process.stdin.isTTY) {
    text = await readStdin();
  }
  if (!text) {
    console.error("Nothing to rewrite. Pass text as an argument or pipe it in.");
    process.exit(1);
  }

  if (!(await daemonHealth())) {
    if (!(await startDaemon())) process.exit(1);
  }

  try {
    const out = await rewriteOnce(text, style);
    console.log(out.result);
    if (process.stdout.isTTY) {
      console.error(c.dim(`\n[${out.style} · ${out.model} · ${out.ms}ms]`));
    }
  } catch (err) {
    console.error(c.red(err.message));
    process.exit(1);
  }
}

async function cmdHistory(args) {
  const limit = Number(args[0]) || 10;
  if (!(await daemonHealth())) {
    console.error("daemon not running");
    process.exit(1);
  }
  const response = await fetch(`${BASE}/history?limit=${limit}`);
  const { entries } = await response.json();
  if (!entries.length) {
    console.log(c.dim("no rewrites yet"));
    return;
  }
  for (const entry of entries) {
    console.log(c.dim(`${entry.at}  [${entry.style}]  ${entry.ms}ms`));
    console.log(c.dim("  in  ") + truncate(entry.input));
    console.log(c.dim("  out ") + truncate(entry.output));
    console.log();
  }
}

function truncate(s, n = 140) {
  const flat = String(s).replace(/\s+/g, " ").trim();
  return flat.length > n ? flat.slice(0, n - 1) + "…" : flat;
}

function tail(file, lines) {
  try {
    return readFileSync(file, "utf8").split(/\r?\n/).slice(-lines).join("\n");
  } catch {
    return "(no log)";
  }
}

function readStdin() {
  return new Promise((resolve) => {
    let data = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (chunk) => (data += chunk));
    process.stdin.on("end", () => resolve(data.trim()));
  });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function usage() {
  console.log(`${c.bold("refyn")} — always-on prompt rewriter

  ${c.bold("launch")}                start Refyn and open the full window
  ${c.bold("settings")}              open just the settings window
  ${c.bold("start")}                 start in the background only, no window
  ${c.bold("stop")}                  stop both
  ${c.bold("restart")}               stop, then start
  ${c.bold("status")}                what is running, and an end-to-end key check
  ${c.bold("rewrite")} <text>        rewrite from here; also reads stdin
                        ${c.dim("-s, --style <id>   improve | concise | technical | code |")}
                        ${c.dim("                   reasoning | creative | socratic")}
  ${c.bold("history")} [n]           recent rewrites
  ${c.bold("build")}                 recompile the tray host
  ${c.bold("autostart")} on|off      run at logon

  ${c.dim("Hotkeys - change these in `refyn settings`:")}
  ${c.dim("  Ctrl+Alt+P   rewrite the selected text in place")}
  ${c.dim("  Ctrl+Alt+O   compose window")}
  ${c.dim("  Ctrl+Alt+L   pick a style for the selection")}`);
}

// ---------------------------------------------------------------------- main

const [command, ...rest] = process.argv.slice(2);

switch (command) {
  case "start": await cmdStart(); break;
  case "stop": await cmdStop(); break;
  case "restart": await cmdStop(); await sleep(300); await cmdStart(); break;
  case "status": await cmdStatus(); break;
  case "rewrite": await cmdRewrite(rest); break;
  case "history": await cmdHistory(rest); break;
  case "launch": await cmdLaunch(); break;
  case "settings": case "config": await cmdSettings(); break;
  case "build": await buildHost(); break;
  case "autostart": await setAutostart(rest[0] !== "off"); break;
  case "help": case "--help": case "-h": case undefined: usage(); break;
  default:
    console.error(`Unknown command "${command}".\n`);
    usage();
    process.exit(1);
}
